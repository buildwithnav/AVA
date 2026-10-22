using Codex.Business.Model;
using Codex.Data.Models;
using Ava.MetaQuery.Business.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Data;
using Ava.MetaQuery.Data.Models;

namespace Ava.MetaQuery.Business.Services
{
    public interface IMetaQueryService
    {
        bool CurrentUserHasADAccount();
        Task<SearchResult> Get(StudySearchParameters parameters);
        Task<StudyDetailViewModel> Get(int id);
        Task<StudyDetailViewModel> Update(int id, string code);
        Task<StudyDetailViewModel> Add(Study model);
        Task Delete(int studyId);
        Task<List<Agreement>> GetAgreementsBySqlQuery(AgreementSearchParameters parameters);
        Task<List<StudyViewModel>> GetStudies(StudyStoredProcSearchParameters parameters);
        Task<DataTable> GetStudiesDataTable(StudyStoredProcSearchParameters parameters);
        UserViewModel GetUser();
        Task<dynamic> GetAllFilters();
    }
}
