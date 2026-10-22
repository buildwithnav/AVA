using Codex.Business.Model;
using System;
using System.Collections.Generic;
using System.Text;

namespace Ava.MetaQuery.Business.Models
{
    public class StudySearchParameters : SearchParameters
    {
        public string? StudyId { get; set; }
        public string? Code { get; set; }
        public string? Title { get; set; }
        public string? Status { get; set; }
        public string? Category { get; set; }
    }

    public class StudyStoredProcSearchParameters : SearchParameters
    {
        public string StudyStage { get; set; } = "%";
        public string OrgUnitCode { get; set; } = "%";
    }

    public class AgreementSearchParameters : SearchParameters
    {
        public string UserName { get; set; }
    }
}
