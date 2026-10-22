using Ava.MetaQuery.Business.Models;
using Ava.MetaQuery.Data;
using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Ava.MetaQuery.Business.Services
{
    public class SqlExecutionService : ISqlExecutionService
    {
        private const int DefaultMaxRows = 1000;
        private const int HardMaxRows = 10_000;

        // Fail fast on runaway queries (e.g. an LLM-generated cartesian join).
        private const int CommandTimeoutSeconds = 30;

        // LOB / large-text guards to protect the caller's context window.
        private const int MaxTextLength = 4000;

        // Keywords that indicate a mutating or structural statement.
        private static readonly string[] ForbiddenKeywords =
        {
            "INSERT", "UPDATE", "DELETE", "MERGE",
            "DROP", "CREATE", "ALTER", "TRUNCATE",
            "GRANT", "REVOKE", "EXECUTE", "CALL", "BEGIN", "DECLARE"
        };

        private readonly MetaQueryContext _context;

        public SqlExecutionService(MetaQueryContext context)
        {
            _context = context;
        }

        // ─────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────

        public async Task<List<SqlRowResult>> ExecuteQuery(string sql, int maxRows)
        {
            sql = NormalizeQuery(sql);
            ValidateReadOnly(sql);
            maxRows = ClampMaxRows(maxRows);

            string wrapped = $"SELECT * FROM ({sql}) WHERE ROWNUM <= {maxRows}";
            return await RunQuery(wrapped);
        }

        public async Task<PagedSqlResult> ExecuteQueryPaged(string sql, int page, int pageSize)
        {
            sql = NormalizeQuery(sql);
            ValidateReadOnly(sql);
            (page, pageSize) = NormalisePaging(page, pageSize);

            int total = await CountRows($"SELECT COUNT(*) FROM ({sql})");
            string wrapped = $"SELECT * FROM ({sql}) OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY";
            var rows = await RunQuery(wrapped);

            return new PagedSqlResult
            {
                Page = page,
                PageSize = pageSize,
                TotalRows = total,
                Rows = rows
            };
        }

        public async Task<List<SqlRowResult>> SelectObject(string objectName, int maxRows)
        {
            ValidateObjectName(objectName);
            maxRows = ClampMaxRows(maxRows);

            string sql = $"SELECT * FROM {objectName} FETCH FIRST :maxRows ROWS ONLY";
            return await RunQuery(sql, new OracleParameter("maxRows", OracleDbType.Int32, maxRows, ParameterDirection.Input));
        }

        public async Task<PagedSqlResult> SelectObjectPaged(string objectName, int page, int pageSize)
        {
            ValidateObjectName(objectName);
            (page, pageSize) = NormalisePaging(page, pageSize);

            int total = await CountRows($"SELECT COUNT(*) FROM {objectName}");

            string sql = $"SELECT * FROM {objectName} OFFSET :offset ROWS FETCH NEXT :pageSize ROWS ONLY";
            var rows = await RunQuery(sql,
                new OracleParameter("offset", OracleDbType.Int32, (page - 1) * pageSize, ParameterDirection.Input),
                new OracleParameter("pageSize", OracleDbType.Int32, pageSize, ParameterDirection.Input));

            return new PagedSqlResult
            {
                Page = page,
                PageSize = pageSize,
                TotalRows = total,
                Rows = rows
            };
        }

        public async Task<ObjectMetadataResult> GetObjectMetadata(string objectName)
        {
            ValidateObjectName(objectName);

            var parts = objectName.Split('.');
            string? owner = parts.Length == 2 ? parts[0].ToUpperInvariant() : null;
            string name = (parts.Length == 2 ? parts[1] : parts[0]).ToUpperInvariant();

            var result = new ObjectMetadataResult
            {
                Owner = owner,
                ObjectName = name
            };

            await using var conn = CreateConnection();
            await conn.OpenAsync();

            await LoadObjectInfo(conn, result, owner, name);
            await LoadColumnInfo(conn, result, owner, name);

            return result;
        }

        // ─────────────────────────────────────────────────────────────
        // Connection / execution helpers
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a fresh, independently-disposable Oracle connection from the context's
        /// configured connection string. We deliberately do NOT reuse the EF-managed
        /// connection (GetDbConnection) because disposing that would corrupt the DbContext.
        /// </summary>
        private OracleConnection CreateConnection()
        {
            string? connectionString = _context.Database.GetConnectionString();
            if (string.IsNullOrEmpty(connectionString))
                throw new InvalidOperationException("No Oracle connection string is configured.");

            return new OracleConnection(connectionString);
        }

        /// <summary>Creates a command with a consistent read timeout applied.</summary>
        private static OracleCommand CreateCommand(OracleConnection conn, string sql)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandType = CommandType.Text;
            cmd.CommandTimeout = CommandTimeoutSeconds;
            return cmd;
        }

        private async Task<List<SqlRowResult>> RunQuery(string sql, params OracleParameter[] parameters)
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();

            await using var cmd = CreateCommand(conn, sql);
            if (parameters.Length > 0)
                cmd.Parameters.AddRange(parameters);

            return await ReadRows(cmd);
        }

        private static async Task<List<SqlRowResult>> ReadRows(OracleCommand cmd)
        {
            var rows = new List<SqlRowResult>();

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new SqlRowResult();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string col = reader.GetName(i);

                    // Guard against duplicate column names (e.g. from joins) clobbering each other.
                    if (row.ContainsKey(col))
                        col = $"{col}_{i}";

                    row[col] = ReadCell(reader, i);
                }
                rows.Add(row);
            }

            return rows;
        }

        /// <summary>
        /// Reads a single cell, guarding against arbitrarily large LOBs that would bloat the
        /// caller's payload/context: binary columns are replaced with a size placeholder and
        /// text columns (including CLOB/NCLOB) are streamed and truncated.
        /// </summary>
        private static object? ReadCell(System.Data.Common.DbDataReader reader, int i)
        {
            if (reader.IsDBNull(i))
                return null;

            Type fieldType = reader.GetFieldType(i);

            // Binary data (BLOB/RAW/LONG RAW) — don't materialise or base64-encode megabytes.
            if (fieldType == typeof(byte[]))
            {
                long length = reader.GetBytes(i, 0, null, 0, 0);
                return $"<binary {length} bytes>";
            }

            // Text data — stream up to the cap so huge CLOBs aren't fully loaded.
            if (fieldType == typeof(string))
            {
                var buffer = new char[MaxTextLength + 1];
                long read;
                try
                {
                    read = reader.GetChars(i, 0, buffer, 0, MaxTextLength + 1);
                }
                catch (Exception)
                {
                    // Some providers/columns don't support GetChars; fall back to full read.
                    string full = reader.GetString(i);
                    return full.Length > MaxTextLength
                        ? full[..MaxTextLength] + $"…[truncated, {full.Length} chars]"
                        : full;
                }

                if (read > MaxTextLength)
                    return new string(buffer, 0, MaxTextLength) + "…[truncated]";

                return new string(buffer, 0, (int)read);
            }

            return reader.GetValue(i);
        }

        private async Task<int> CountRows(string countSql)
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();

            await using var cmd = CreateCommand(conn, countSql);

            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        // ─────────────────────────────────────────────────────────────
        // Validation / normalisation
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Trims the query, removes a single trailing semicolon (ODP.NET rejects it with
        /// ORA-00911), and rejects any remaining statement separators.
        /// </summary>
        private static string NormalizeQuery(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("SQL query must not be empty.");

            sql = sql.Trim();

            // Strip a single trailing semicolon.
            if (sql.EndsWith(";"))
                sql = sql[..^1].TrimEnd();

            // Any remaining semicolon (outside literals/comments) means multiple statements.
            string stripped = StripLiteralsAndComments(sql);
            if (stripped.Contains(';'))
                throw new InvalidOperationException("Only a single SQL statement is allowed.");

            return sql;
        }

        private static void ValidateReadOnly(string sql)
        {
            // Strip string literals and comments to avoid false positives, then check keywords.
            string stripped = StripLiteralsAndComments(sql).Trim();
            string upper = stripped.ToUpperInvariant();

            // Positive check: the statement must begin with SELECT or WITH.
            if (!Regex.IsMatch(upper, @"^\s*(SELECT|WITH)\b"))
                throw new InvalidOperationException("Only read-only SELECT (or WITH ... SELECT) queries are permitted.");

            foreach (var keyword in ForbiddenKeywords)
            {
                if (Regex.IsMatch(upper, $@"\b{keyword}\b"))
                    throw new InvalidOperationException(
                        $"The keyword '{keyword}' is not allowed. Only read-only SELECT queries are permitted.");
            }
        }

        /// <summary>
        /// Validates that an object name contains only schema-qualified identifiers
        /// (letters, digits, underscores, $, #, and a single dot separator).
        /// Prevents SQL injection via the object name parameter.
        /// </summary>
        private static void ValidateObjectName(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName))
                throw new ArgumentException("Object name must not be empty.");

            if (!Regex.IsMatch(objectName, @"^[A-Za-z0-9_$#]+(\.[A-Za-z0-9_$#]+)?$"))
                throw new ArgumentException(
                    $"Invalid object name '{objectName}'. Expected format: SCHEMA.OBJECT_NAME or OBJECT_NAME.");
        }

        private static int ClampMaxRows(int maxRows)
            => maxRows <= 0 ? DefaultMaxRows : Math.Min(maxRows, HardMaxRows);

        private static (int page, int pageSize) NormalisePaging(int page, int pageSize)
            => (Math.Max(1, page), pageSize <= 0 ? 50 : Math.Min(pageSize, HardMaxRows));

        /// <summary>
        /// Strips block comments, line comments and single-quoted literals so keyword and
        /// separator checks don't produce false positives on literal values.
        /// </summary>
        private static string StripLiteralsAndComments(string sql)
        {
            sql = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            sql = Regex.Replace(sql, @"--[^\r\n]*", " ");
            sql = Regex.Replace(sql, @"'[^']*'", "''");
            return sql;
        }

        // ─────────────────────────────────────────────────────────────
        // Metadata loaders
        // ─────────────────────────────────────────────────────────────

        private static async Task LoadObjectInfo(OracleConnection conn, ObjectMetadataResult result, string? owner, string name)
        {
            string objSql = owner != null
                ? "SELECT OBJECT_TYPE FROM ALL_OBJECTS WHERE OWNER = :owner AND OBJECT_NAME = :name AND ROWNUM = 1"
                : "SELECT OBJECT_TYPE FROM ALL_OBJECTS WHERE OBJECT_NAME = :name AND ROWNUM = 1";

            await using var cmd = CreateCommand(conn, objSql);
            if (owner != null)
                cmd.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2, owner, ParameterDirection.Input));
            cmd.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2, name, ParameterDirection.Input));

            var objType = (await cmd.ExecuteScalarAsync())?.ToString();
            result.ObjectType = objType;

            if (string.IsNullOrEmpty(objType))
                return;

            string commentSql = objType == "MATERIALIZED VIEW"
                ? (owner != null
                    ? "SELECT COMMENTS FROM ALL_MVIEW_COMMENTS WHERE OWNER = :owner AND MVIEW_NAME = :name"
                    : "SELECT COMMENTS FROM ALL_MVIEW_COMMENTS WHERE MVIEW_NAME = :name")
                : (owner != null
                    ? "SELECT COMMENTS FROM ALL_TAB_COMMENTS WHERE OWNER = :owner AND TABLE_NAME = :name"
                    : "SELECT COMMENTS FROM ALL_TAB_COMMENTS WHERE TABLE_NAME = :name");

            await using var commentCmd = CreateCommand(conn, commentSql);
            if (owner != null)
                commentCmd.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2, owner, ParameterDirection.Input));
            commentCmd.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2, name, ParameterDirection.Input));

            var comment = await commentCmd.ExecuteScalarAsync();
            result.Comment = comment == null || comment == DBNull.Value ? null : comment.ToString();
        }

        private static async Task LoadColumnInfo(OracleConnection conn, ObjectMetadataResult result, string? owner, string name)
        {
            string colSql = owner != null
                ? @"SELECT COLUMN_NAME, DATA_TYPE, NULLABLE FROM ALL_TAB_COLUMNS
                    WHERE OWNER = :owner AND TABLE_NAME = :name ORDER BY COLUMN_ID"
                : @"SELECT COLUMN_NAME, DATA_TYPE, NULLABLE FROM ALL_TAB_COLUMNS
                    WHERE TABLE_NAME = :name ORDER BY COLUMN_ID";

            await using var colCmd = CreateCommand(conn, colSql);
            if (owner != null)
                colCmd.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2, owner, ParameterDirection.Input));
            colCmd.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2, name, ParameterDirection.Input));

            var columns = new Dictionary<string, ColumnMetadataResult>();
            var ordered = new List<ColumnMetadataResult>();

            await using (var reader = await colCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var col = new ColumnMetadataResult
                    {
                        ColumnName = reader.GetString(0),
                        DataType = reader.GetString(1),
                        Nullable = reader.GetString(2) == "Y"
                    };
                    columns[col.ColumnName] = col;
                    ordered.Add(col);
                }
            }

            string ccomSql = owner != null
                ? "SELECT COLUMN_NAME, COMMENTS FROM ALL_COL_COMMENTS WHERE OWNER = :owner AND TABLE_NAME = :name"
                : "SELECT COLUMN_NAME, COMMENTS FROM ALL_COL_COMMENTS WHERE TABLE_NAME = :name";

            await using var ccomCmd = CreateCommand(conn, ccomSql);
            if (owner != null)
                ccomCmd.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2, owner, ParameterDirection.Input));
            ccomCmd.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2, name, ParameterDirection.Input));

            await using (var reader = await ccomCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    string colName = reader.GetString(0);
                    string? comment = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (columns.TryGetValue(colName, out var col))
                        col.Comment = comment;
                }
            }

            result.Columns = ordered;
        }
    }
}
