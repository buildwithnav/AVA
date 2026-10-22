using Ava.MetaQuery.Api.Controllers;
using Ava.MetaQuery.Business.Models;
using Ava.MetaQuery.Business.Services;
using Codex.Business.Model;
using Codex.Data.Models;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using System.Runtime.CompilerServices;

namespace Ava.MetaQuery.Tests
{
    public class MetaQuery_ControllerTests
    {
        private static IMetaQueryService _templateService;
        private static MetaQueryController _controller;

        public MetaQuery_ControllerTests()
        {
            _templateService = A.Fake<IMetaQueryService>();
            _controller = new MetaQueryController(_templateService);
        }

        [Fact]
        public async Task GetStudies_GivenStudyStoredProcSearchParameters_ReturnOk()
        {
            //Arrange
            StudyStoredProcSearchParameters _param = A.Fake<StudyStoredProcSearchParameters>();
            SearchResult _searchResult = A.Fake<SearchResult>();
            var studies = CreateStudyViewModels();

            A.CallTo(() => _templateService.GetStudies(_param)).Returns(studies);

            //Act
            var result = await _controller.GetStudies(_param);

            //Assert
            //We can do seprate assert to access the result
            result.Should().NotBeNull();
            result.Should().BeOfType(typeof(OkObjectResult));
            A.CallTo(() => _templateService.GetStudies(_param)).MustHaveHappenedOnceExactly();

            //In this case we want to make sure that the result is not null and its type of OkObjectResult 
            //Also it does returns list of values in StudyViewModel data type and we know it must be 2 records so we check the count
            result.Should().NotBeNull().And
                .BeAssignableTo<OkObjectResult>()
                .Subject.Value.Should().BeAssignableTo<List<StudyViewModel>>()
                .Subject.Should().HaveCount(2);
        }

        [Fact]
        public async Task Get_GivenStudyID_ReturnOk()
        {
            //Arrange
            var studies = CreateStudyDetailViewModels();
            
            A.CallTo(() => _templateService.Get(studies[0].StudyId)).Returns(studies[0]);

            //Act
            var result = await _controller.Get(studies[0].StudyId);

            //Assert
            result.Should().NotBeNull();
            result.Should().BeOfType(typeof(OkObjectResult));


            result.Should().NotBeNull().And
                .BeAssignableTo<OkObjectResult>()
                .Subject.Value.Should().BeAssignableTo<StudyDetailViewModel>()
                .Subject.Should().BeSameAs(studies[0]);
        }

        //[Fact]
        //public void FailTestCase()
        //{
        //    Assert.False(true);
        //}


        private List<StudyDetailViewModel> CreateStudyDetailViewModels()
        {
            List<StudyDetailViewModel> studies = new List<StudyDetailViewModel>
            {
                new StudyDetailViewModel
                {
                    StudyId = 1,
                    Code = "Test",
                    Title = "Test",
                    Status = "CLOSED",
                    Category = "PMS-SS",
                    RscDocCtr = "",
                    SubCategory = "B",
                    Stage = "COMPLT",
                    SiteTrkFg = "NO",
                    MonitorFg = "NO",
                    ScanningFg = "NO",
                    EDC = "",
                    XfilesFg = "NO",
                    AcuteFg = "NO",
                    SubmitTo = "",
                    MerlinFg = "NO",
                    Division = "CRM",
                    ProjectCode = "CRMD",
                    OrganizationUnit = null,
                    Department = "CRMPM",
                    ModifiedBy = "",
                    EnteredBy = "",
                    OrganizationUnitName = "",
                    OrganizationUnitCode = "",
                    RandomFlag = "NO",
                    DSMBFlag = "NO",
                    CoreLabFlag = "NO",
                    AeSteerFlag = "NO",
                    DeviceType = "",
                    Sites = null,
                    Personnel = null,
                    StudyAccounts = null
                },
                new StudyDetailViewModel
                {
                    StudyId = 2,
                    Code = "Test",
                    Title = "Test",
                    Status = "CLOSED",
                    Category = "PMS-SS",
                    RscDocCtr = "",
                    SubCategory = "B",
                    Stage = "COMPLT",
                    SiteTrkFg = "NO",
                    MonitorFg = "NO",
                    ScanningFg = "NO",
                    EDC = "",
                    XfilesFg = "NO",
                    AcuteFg = "NO",
                    SubmitTo = "",
                    MerlinFg = "NO",
                    Division = "CRM",
                    ProjectCode = "CRMD",
                    OrganizationUnit = null,
                    Department = "CRMPM",
                    ModifiedBy = "",
                    EnteredBy = "",
                    OrganizationUnitName = "",
                    OrganizationUnitCode = "",
                    RandomFlag = "NO",
                    DSMBFlag = "NO",
                    CoreLabFlag = "NO",
                    AeSteerFlag = "NO",
                    DeviceType = "",
                    Sites = null,
                    Personnel = null,
                    StudyAccounts = null
                },

            };

            return studies;
        }

        private List<StudyViewModel> CreateStudyViewModels()
        {
            List<StudyViewModel> studies = new List<StudyViewModel>
            {
                new StudyViewModel
                {
                    StudyId = 1,
                    Code = "Test",
                    Title = "Test",
                    Status = "CLOSED",
                    Category = "PMS-SS"
                },
                new StudyViewModel
                {
                    StudyId = 2,
                    Code = "Test",
                    Title = "Test",
                    Status = "CLOSED",
                    Category = "PMS-SS"
                },

            };

            return studies;
        }
    }
}
