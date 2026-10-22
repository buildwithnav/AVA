using System.Collections.Generic;

namespace Ava.MetaQuery.Business.Models
{
    // ──────────────────────────────────────────────
    // Request models
    // ──────────────────────────────────────────────

    /// <summary>
    /// Execute a raw read-only SQL query with a row cap.
    /// </summary>
    public class SqlExecuteRequest
    {
        /// <summary>The SQL SELECT query to execute.</summary>
        public string Query { get; set; } = string.Empty;

        /// <summary>Maximum rows to return. Defaults to 1000, capped at 10 000.</summary>
        public int MaxRows { get; set; } = 1000;
    }

    /// <summary>
    /// Execute a raw read-only SQL query with full page/pageSize pagination.
    /// </summary>
    public class SqlPagedExecuteRequest
    {
        /// <summary>The SQL SELECT query to execute.</summary>
        public string Query { get; set; } = string.Empty;

        /// <summary>1-based page number.</summary>
        public int Page { get; set; } = 1;

        /// <summary>Number of rows per page.</summary>
        public int PageSize { get; set; } = 50;
    }

    // ──────────────────────────────────────────────
    // Response models
    // ──────────────────────────────────────────────

    /// <summary>
    /// A single result row represented as a column-name → value map.
    /// </summary>
    public class SqlRowResult : Dictionary<string, object?> { }

    /// <summary>
    /// Paged result wrapper.
    /// </summary>
    public class PagedSqlResult
    {
        /// <summary>Current page (1-based).</summary>
        public int Page { get; set; }

        /// <summary>Rows per page.</summary>
        public int PageSize { get; set; }

        /// <summary>Total number of rows matching the query.</summary>
        public int TotalRows { get; set; }

        /// <summary>Total number of pages.</summary>
        public int TotalPages => PageSize > 0 ? (int)System.Math.Ceiling((double)TotalRows / PageSize) : 0;

        /// <summary>Rows for the current page.</summary>
        public List<SqlRowResult> Rows { get; set; } = new();
    }

    /// <summary>
    /// Metadata for a single column of a database object.
    /// </summary>
    public class ColumnMetadataResult
    {
        public string ColumnName { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public bool Nullable { get; set; }
        public string? Comment { get; set; }
    }

    /// <summary>
    /// Metadata for a database object (table, view, materialised view, etc.).
    /// </summary>
    public class ObjectMetadataResult
    {
        public string? Owner { get; set; }
        public string ObjectName { get; set; } = string.Empty;
        public string? ObjectType { get; set; }
        public string? Comment { get; set; }
        public List<ColumnMetadataResult> Columns { get; set; } = new();
    }
}
