using Ava.MetaQuery.Business.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ava.MetaQuery.Business.Services
{
    public interface ISqlExecutionService
    {
        /// <summary>
        /// Execute a raw read-only SQL query and return up to <paramref name="maxRows"/> rows.
        /// </summary>
        Task<List<SqlRowResult>> ExecuteQuery(string sql, int maxRows);

        /// <summary>
        /// Execute a raw read-only SQL query and return a specific page of results.
        /// </summary>
        Task<PagedSqlResult> ExecuteQueryPaged(string sql, int page, int pageSize);

        /// <summary>
        /// SELECT * FROM <paramref name="objectName"/> and return up to <paramref name="maxRows"/> rows.
        /// </summary>
        Task<List<SqlRowResult>> SelectObject(string objectName, int maxRows);

        /// <summary>
        /// SELECT * FROM <paramref name="objectName"/> and return a specific page of results.
        /// </summary>
        Task<PagedSqlResult> SelectObjectPaged(string objectName, int page, int pageSize);

        /// <summary>
        /// Return object-level and column-level metadata for a named Oracle object.
        /// </summary>
        Task<ObjectMetadataResult> GetObjectMetadata(string objectName);
    }
}
