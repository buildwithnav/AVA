using Codex.Web;
using Ava.MetaQuery.Business.Models;
using Ava.MetaQuery.Business.Services;
using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Threading.Tasks;

namespace Ava.MetaQuery.Api.Controllers
{
    /// <summary>
    /// Dynamic SQL execution endpoints for Oracle.
    /// </summary>
    [Route("cds_api/sql")]
    [ApiController]
    public class SqlExecutionController : CodexBaseController
    {
        private readonly ISqlExecutionService _sqlService;

        public SqlExecutionController(ISqlExecutionService sqlService)
        {
            _sqlService = sqlService;
        }

        /// <summary>
        /// Execute a raw read-only SQL query with a row cap.
        /// </summary>
        /// <remarks>
        /// Accepts a single SELECT (or WITH ... SELECT) query. DML and DDL statements are rejected.
        /// Results are dynamic — columns are determined by the query at runtime.
        /// </remarks>
        [HttpPost("execute")]
        public async Task<IActionResult> Execute([FromBody] SqlExecuteRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Query))
                return BadRequest("A non-empty SQL query is required.");

            try
            {
                var rows = await _sqlService.ExecuteQuery(request.Query, request.MaxRows);
                return Ok(rows);
            }
            catch (Exception ex) when (IsClientError(ex))
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// Execute a raw read-only SQL query with full page/pageSize pagination.
        /// </summary>
        /// <remarks>
        /// Accepts a single SELECT (or WITH ... SELECT) query. DML and DDL statements are rejected.
        /// Returns total row count, total pages, and the rows for the requested page.
        /// </remarks>
        [HttpPost("execute/paged")]
        public async Task<IActionResult> ExecutePaged([FromBody] SqlPagedExecuteRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Query))
                return BadRequest("A non-empty SQL query is required.");

            try
            {
                var result = await _sqlService.ExecuteQueryPaged(request.Query, request.Page, request.PageSize);
                return Ok(result);
            }
            catch (Exception ex) when (IsClientError(ex))
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// SELECT * from a named Oracle object with a row cap.
        /// </summary>
        /// <remarks>
        /// Provide the object name in SCHEMA.OBJECT or OBJECT format (e.g. CAP.ST_EVENTS).
        /// Results are dynamic — columns are determined by the object at runtime.
        /// </remarks>
        [HttpGet("select")]
        public async Task<IActionResult> Select([FromQuery] string objectName, [FromQuery] int maxRows = 1000)
        {
            if (string.IsNullOrWhiteSpace(objectName))
                return BadRequest("objectName is required.");

            try
            {
                var rows = await _sqlService.SelectObject(objectName, maxRows);
                return Ok(rows);
            }
            catch (Exception ex) when (IsClientError(ex))
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// SELECT * from a named Oracle object with full page/pageSize pagination.
        /// </summary>
        /// <remarks>
        /// Provide the object name in SCHEMA.OBJECT or OBJECT format (e.g. CAP.ST_EVENTS).
        /// Returns total row count, total pages, and the rows for the requested page.
        /// </remarks>
        [HttpGet("select/paged")]
        public async Task<IActionResult> SelectPaged(
            [FromQuery] string objectName,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            if (string.IsNullOrWhiteSpace(objectName))
                return BadRequest("objectName is required.");

            try
            {
                var result = await _sqlService.SelectObjectPaged(objectName, page, pageSize);
                return Ok(result);
            }
            catch (Exception ex) when (IsClientError(ex))
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// Get metadata for a named Oracle object.
        /// </summary>
        /// <remarks>
        /// Returns the object type, object-level comment, and per-column metadata
        /// (name, data type, nullability, comment) sourced from Oracle's data dictionary.
        /// Provide the object name in SCHEMA.OBJECT or OBJECT format (e.g. CAP.ST_EVENTS).
        /// </remarks>
        [HttpGet("metadata")]
        public async Task<IActionResult> GetMetadata([FromQuery] string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName))
                return BadRequest("objectName is required.");

            try
            {
                var metadata = await _sqlService.GetObjectMetadata(objectName);
                if (string.IsNullOrEmpty(metadata.ObjectType))
                    return NotFound($"Object '{objectName}' was not found.");

                return Ok(metadata);
            }
            catch (Exception ex) when (IsClientError(ex))
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// Errors we surface to the caller as 400 so an automated/LLM consumer can self-correct:
        /// validation failures and Oracle SQL errors (e.g. ORA-00904 invalid identifier).
        /// </summary>
        private static bool IsClientError(Exception ex)
            => ex is ArgumentException or InvalidOperationException or OracleException;
    }
}
