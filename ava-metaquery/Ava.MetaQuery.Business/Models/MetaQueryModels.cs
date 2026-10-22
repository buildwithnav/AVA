using Codex.Data.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Ava.MetaQuery.Business.Models
{
    public class UserViewModel
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Division { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string Title { get; set; }
        public string Location { get; set; }
    }

    public class StudyViewModel
    {
        public int StudyId { get; set; }
        public string Code { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
        public string Category { get; set; }
    }

    public class StudyDetailViewModel
    {
        public int StudyId { get; set; }
        public string Code { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
        public string Category { get; set; }
        public DateTime? CancelDate { get; set; }
        public DateTime? ActiveDate { get; set; }
        public DateTime? FinalDataDate { get; set; }
        public DateTime? ArchiveDate { get; set; }
        public string RscDocCtr { get; set; }
        public string SubCategory { get; set; }
        public string Stage { get; set; }
        public string SiteTrkFg { get; set; }
        public DateTime? PrjStartEnrDT { get; set; }
        public DateTime? PrjEndEnrDT { get; set; }
        public int? CapPtSite { get; set; }
        public string MonitorFg { get; set; }
        public string ScanningFg { get; set; }
        public string EDC { get; set; }
        public string XfilesFg { get; set; }
        public string AcuteFg { get; set; }
        public string SubmitTo { get; set; }
        public DateTime? AnnualDueDate { get; set; }
        public string MerlinFg { get; set; }
        public string Division { get; set; }
        public string ProjectCode { get; set; }
        public virtual OrganizationUnit OrganizationUnit { get; set; }
        public string Department { get; set; }
        public string ModifiedBy { get; set; }
        public string EnteredBy { get; set; }
        public int? OrganizationUnitId { get; set; }
        public string OrganizationUnitName { get; set; }
        public string OrganizationUnitCode { get; set; }
        public DateTime? EnrollmentStartDate { get; set; }
        public DateTime? EnrollmentEndDate { get; set; }
        public DateTime? CloseDate { get; set; }
        public DateTime? StProdDT { get; set; }
        public int? Duration { get; set; }
        public int? PatientCap { get; set; }
        public int? PatientCapUS { get; set; }
        public int? SiteCap { get; set; }
        public int? SiteCapUS { get; set; }
        public string RandomFlag { get; set; }
        public string DSMBFlag { get; set; }
        public string CoreLabFlag { get; set; }
        public string AeSteerFlag { get; set; }
        public string DeviceType { get; set; }
        public DateTime? EntryDT { get; set; }
        public DateTime? ModifiedDT { get; set; }
        public ICollection<StudySite> Sites { get; set; }
        public ICollection<Personnel> Personnel { get; set; }
        public ICollection<Account> StudyAccounts { get; set; }
    }
}
