using AutoMapper;
using Codex.Business.Extensions;
using Codex.Business.Model;
using Codex.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ava.MetaQuery.Business.Extensions;
using Ava.MetaQuery.Business.Models;
using Ava.MetaQuery.Data;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using Ava.MetaQuery.Business.Utilities;
using System.Linq;
using Ava.MetaQuery.Data.Models;

namespace Ava.MetaQuery.Business.Services
{
    public class MetaQueryService : IMetaQueryService
    {
        private readonly MetaQueryContext _context;
        private readonly IMapper _mapper;
        private readonly CodexUser _codexUser;
        public Person CurrentUser { get { return _codexUser.CurrentUser; } set { _codexUser.CurrentUser = value; } }

        public MetaQueryService(MetaQueryContext context, IMapper mapper, CodexUser codexUser)
        {
            _context = context;
            _mapper = mapper;
            _codexUser = codexUser;

        }
        public bool CurrentUserHasADAccount()
        {
            return _codexUser.CurrentUser != null && _codexUser.CurrentUser.ADAccount != null;

        }
        public async Task<SearchResult> Get(StudySearchParameters parameters)
        {
            await using var newContext = new MetaQueryContext(_context.DbOptions);

            parameters ??= new StudySearchParameters();

            return await newContext.Studies
                .Filter(parameters)
                .Sort(parameters)
                .ToStudyViewModel()
                .AsNoTracking()
                .ToSearchResultAsync(parameters);
        }

        public async Task<StudyDetailViewModel> Get(int id)
        {
            var study = await _context.Studies.FirstOrDefaultAsync(x => x.StudyId == id);

            var studyViewModel = _mapper.Map<StudyDetailViewModel>(study);

            return studyViewModel;
        }

        public async Task<StudyDetailViewModel> Update(int id, string code)
        {
            var study = await _context.Studies.FirstOrDefaultAsync(x => x.StudyId == id);

            if (study != null)
            {
                study.Code = code;
                await _context.SaveChangesAsync();

                var studyViewModel = _mapper.Map<StudyDetailViewModel>(study);

                return studyViewModel;
            }

            return null;
        }

        public async Task<StudyDetailViewModel> Add(Study model)
        {
            //var study = await _context.Studies.FirstOrDefaultAsync(x => x.StudyId == id);

            if (model != null)
            {
                var s = _context.Add(model);
                await _context.SaveChangesAsync();

                var study = await _context.Studies.FirstOrDefaultAsync(x => x.StudyId == model.StudyId);

                var studyViewModel = _mapper.Map<StudyDetailViewModel>(study);
                return studyViewModel;
            }

            return null;
        }

        public async Task Delete(int studyId)
        {
            var study = await _context.Studies.FirstOrDefaultAsync(x => x.StudyId == studyId);
            if (study != null)
            {
                _context.Remove(study);
                await _context.SaveChangesAsync();
            }
        }

        public async Task<List<Agreement>> GetAgreementsBySqlQuery(AgreementSearchParameters parameters)
        {
            OracleParameter[] oracleParameters =
            {
                new OracleParameter("pUserName", OracleDbType.Varchar2, parameters.UserName, ParameterDirection.Input),
            };

            List<Agreement> agreements = await _context.Agreements.FromSqlRaw(QueryHelpers.GetAgreements, oracleParameters).ToListAsync();

            return agreements;
        }

        public async Task<List<StudyViewModel>> GetStudies(StudyStoredProcSearchParameters parameters)
        {
            var studies = await _context.SqlCommandToList<StudyFromProcedure>(new SqlParameter()
            {
                CommandText = QueryHelpers.GetStudies,
                OutputParameterName = "RESULT",
                Parameters = new Dictionary<string, object> {
                    { "P_STUDY_STAGE", parameters.StudyStage },
                    { "P_ORG_UNIT_CODE", parameters.OrgUnitCode }
                }
            });

            var studyViewModel = studies.ToStudyViewModel();

            return studyViewModel.ToList();
        }

        public async Task<DataTable> GetStudiesDataTable(StudyStoredProcSearchParameters parameters)
        {
            DataTable studies = await _context.ExecuteSqlCommand(new SqlParameter()
            {
                CommandText = QueryHelpers.GetStudies,
                OutputParameterName = "RESULT",
                Parameters = new Dictionary<string, object> {
                    { "P_STUDY_STAGE", parameters.StudyStage },
                    { "P_ORG_UNIT_CODE", parameters.OrgUnitCode }
                }
            });

            return studies;
        }

        public UserViewModel GetUser()
        {
            var person = _codexUser.CurrentUser;

            var user = _mapper.Map<UserViewModel>(person);

            return user;
        }

        public async Task<dynamic> GetAllFilters()
        {
            var getStudies = Get(new StudySearchParameters());

            var getStudiesSp = Get(1167601);

            var getStudiesSp1 = Get(new StudySearchParameters());

            var getStudiesSp2 = Get(new StudySearchParameters());

            await Task.WhenAll(getStudies, getStudiesSp, getStudiesSp1);

            return new { SingleStudy = getStudiesSp.Result, Studies = getStudiesSp.Result, StudiesParallel_1 = getStudies.Result, StudiesParallel_2 = getStudiesSp2.Result };
        }
    }
}
