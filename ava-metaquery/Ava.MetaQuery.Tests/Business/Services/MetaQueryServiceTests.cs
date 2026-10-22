using AutoMapper;
using Ava.MetaQuery.Business.Models;
using Ava.MetaQuery.Business.Services;
using Ava.MetaQuery.Data;
using Codex.Data.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Moq;
using Moq.EntityFrameworkCore;
using System.Linq.Expressions;


namespace Ava.MetaQuery.Tests
{
    public class MetaQueryServiceTests
    {
        private MetaQueryContext _context;
        private MetaQueryService _service;

        public MetaQueryServiceTests()
        {
            _context = UnitTestHelper.GetDatabaseContext();
            _service = UnitTestHelper.CreateService<MetaQueryService, Study, StudyDetailViewModel>();

            //Setup dbContext we can use manually created method which will insert new data to our dataset or
            //if the values of data is not important besides the id(key) then AddMockDataTo<DataType>(count,[starting ID]) can be used

            // example of using Custom method to add specific data to Study dataset
            //DataHelper.MockStudyData(_context).Wait();

            _context.AddMockDataTo<Study>(5).Wait();

        }

        [Fact]
        public void CurrentUserHasADAccount_SetAdAccount_returnsTrue()
        {
            //Arrange
            _service.CurrentUser = new Person() { ADAccount = "VALIJVX" };

            //Act
            bool result = _service.CurrentUserHasADAccount();

            //Assert
            result.Should().BeTrue();

            //Assert.True(result); //Using Xunit Assertion 
        }

        [Fact]
        public async Task Get_GivenValidStudyID_ReturnStudyViewModel()
        {
            //Arrange
            int expectedStudyId = 1;

            //Act
            var result = await _service.Get(expectedStudyId);

            //Assert

            result.Should().NotBeNull();
            result.StudyId.Should().Be(expectedStudyId);

            //Assert.Equal(result.StudyId, expectedStudyId); //Using Xunit Assertion 
        }

        [Fact]
        public async Task Update_StudyCode_ReturnUpdatedStudyViewModel()
        {
            //Arrange
            string newCode = "Testing";
            int studyId = 2;

            //Act
            var result = await _service.Update(studyId, newCode);

            //Assert
            result.Code.Should().Be(newCode);

            //Assert.Equal(result.Code.ToString(), newCode); //Using Xunit Assertion 
        }

        [Fact]
        public async Task Add_Study_ReturnUpdatedStudyViewModel()
        {
            //Arrange
            Study newStudy = new Study()
            {
                StudyId = 1111,
                Code = "SS120",
                OrganizationUnitId = 2,
            };

            //Act
            var result = await _service.Add(newStudy);

            //Assert
            result.Code.Should().Be(newStudy.Code);

            //Assert.Equal(result.Code.ToString(), newStudy.Code); //Using Xunit Assertion 
        }

        [Fact]
        public async Task Delete_StudyId_StudyDeleted()
        {
            //Arrange
            Study newStudy = new Study()
            {
                StudyId = 222,
                Code = "DELETE_222",
                OrganizationUnitId = 2,
            };

            //Act
            var addResult = await _service.Add(newStudy);
            await _service.Delete(newStudy.StudyId);
            var result = await _service.Get(newStudy.StudyId);

            //Assert
            addResult.Should().NotBeNull(); // this is not neccessary however it makes sure the add method worked before checking for null
            result.Should().BeNull();

            //Using Xunit Assertion 
            //Assert.NotNull(addResult); 
            //Assert.Null(result);

        }

        [Fact]
        public async Task Get_GivenInvalidStudyID_ReturnNull()
        {
            //Arrange
            int InvalidStudyID = -999;

            //Act
            var result = await _service.Get(InvalidStudyID);

            //Assert
            result.Should().BeNull();

            //Assert.Null(result);             //Using Xunit Assertion 
        }


    }
}
