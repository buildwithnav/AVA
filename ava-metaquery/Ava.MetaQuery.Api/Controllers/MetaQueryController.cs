using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Codex.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Ava.MetaQuery.Business.Models;
using Ava.MetaQuery.Business.Services;
using Ava.MetaQuery.Business.Exceptions;
using System.Diagnostics;

namespace Ava.MetaQuery.Api.Controllers
{
    /// <summary>
    /// API Controller
    /// </summary>
    [Route("cds_api")]
    [ApiController]
    public class MetaQueryController : CodexBaseController
    {
        private readonly IMetaQueryService _MetaQueryService;

        public MetaQueryController(IMetaQueryService MetaQueryService)
        {
            _MetaQueryService = MetaQueryService;
        }

        [HttpGet("test")]
        public async Task<IActionResult> Test()
        {
            return Ok("Test Endpoint Success!");    
        }


        /// <summary>
        /// Get codex version
        /// </summary> 
        /// <remarks>
        /// Get Current codex version
        /// </remarks> 
        [HttpGet("codexVersion")]
        public async Task<IActionResult> GetCodexVersion()
        {
            string assemblyPath = Path.Combine(AppContext.BaseDirectory, "Codex.Main.dll");

            if (!System.IO.File.Exists(assemblyPath))
            {
                return NotFound($"Assembly not found at path: {assemblyPath}");
            }

            // Get the version information of the assembly
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(assemblyPath);

            // Return the version information
            return Ok(new
            {
                versionInfo.ProductName,
                versionInfo.ProductVersion,
                versionInfo.FileVersion
            });
        }


        /// <summary>
        /// Get All Studies
        /// </summary> 
        /// <remarks>
        /// Get All Studies
        /// </remarks> 
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] StudySearchParameters parameters)
        {
            parameters ??= new StudySearchParameters();

            return Ok(await _MetaQueryService.Get(parameters));
        }

        /// <summary>
        /// Get Study by Id
        /// </summary> 
        /// <remarks>
        /// Get Study by Id
        /// </remarks> 
        [HttpGet("{id}")]
        public async Task<IActionResult> Get(int id) => Ok(await _MetaQueryService.Get(id));

        /// <summary>
        /// Get TOS Agreements using a raw sql query with parameter
        /// </summary> 
        /// <remarks>
        /// Get TOS Agreements using a raw sql query with parameter
        /// </remarks> 
        [HttpGet("sqlqueryagree")]
        public async Task<IActionResult> GetAgreementsBySqlQuery([FromQuery] AgreementSearchParameters parameters)
        {
            return Ok(await _MetaQueryService.GetAgreementsBySqlQuery(parameters));
        }

        /// <summary>
        /// Get Studies using a stored procedure
        /// </summary> 
        /// <remarks>
        /// Even though paging, ordering and searching are included in the params, the stored procedure doesn't use them
        /// </remarks> 
        [HttpGet("storedprocedure")]
        public async Task<IActionResult> GetStudies([FromQuery] StudyStoredProcSearchParameters parameters)
        {
            return Ok(await _MetaQueryService.GetStudies(parameters));
        }

        /// <summary>
        /// Get Study counts using a stored procedure (returns DataTable)
        /// </summary> 
        /// <remarks>
        /// Even though paging, ordering and searching are included in the params, the stored procedure doesn't use them
        /// </remarks> 
        [HttpGet("storedproceduredatatable")]
        public async Task<IActionResult> GetStudiesDataTable([FromQuery] StudyStoredProcSearchParameters parameters)
        {
            var studies = await _MetaQueryService.GetStudiesDataTable(parameters);
            return Ok(studies.Rows.Count);
        }

        /// <summary>
        /// Get current user from Service
        /// </summary> 
        /// <remarks>
        /// Get current user from Service
        /// </remarks> 
        [HttpGet("currentuserservice")]
        public IActionResult Get() => Ok(_MetaQueryService.GetUser());

        /// <summary>
        /// Get current user from Controller
        /// </summary> 
        /// <remarks>
        ///  Get current user from Controller
        /// </remarks> 
        [HttpGet("currentusercontroller")]
        public IActionResult GetCurrentUser() => Ok(CurrentUser);

        /// <summary>
        /// Get Exception
        /// </summary> 
        /// <remarks>
        /// Get Exception
        /// </remarks> 
        [HttpGet("exception")]
        public IActionResult GetException()
        {
            throw new Exception("new Exception");
            return Ok("return");
        }

        /// <summary>
        /// Get Custom Exception
        /// </summary> 
        /// <remarks>
        /// Get Custom Exception
        /// </remarks> 
        [HttpGet("customexception")]
        public IActionResult GetAppException()
        {
            throw new MetaQueryException("Custom Exception");
            return Ok("return");
        }


        /// <summary>
        /// Get Studies using a stored procedure
        /// </summary> 
        /// <remarks>
        /// Even though paging, ordering and searching are included in the params, the stored procedure doesn't use them
        /// </remarks> 
        [HttpGet("task_parallel")]
        public async Task<IActionResult> GetAllFilters()
        {
            return Ok(await _MetaQueryService.GetAllFilters());
        }
    }
}
