using AutoMapper;
using Ava.MetaQuery.Data;
using Codex.Business.Model;
using Codex.Data.Models;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Ava.MetaQuery.Tests
{
    public static class UnitTestHelper
    {
        private static MetaQueryContext _context;
        public static MetaQueryContext GetDatabaseContext()
        {
            if (_context != null)
                return _context;

            var options = GetDBContextOptions();

            _context = new MetaQueryContext(options);
            var task = _context.Database.EnsureCreatedAsync();
            task.Wait();
            return _context;
        }

        public static T CreateService<T,S,V>(MetaQueryContext context = null) where T : class
        {
            if (context == null)
                context = GetDatabaseContext();
            var mapper = UnitTestHelper.GetModelMapper<S, V>();
            var service = (T)Activator.CreateInstance(typeof(T), new object[] { context, mapper, GetCodexUser()});
            return service;
        }
        public static DbContextOptions<MetaQueryContext> GetDBContextOptions() {
            return new DbContextOptionsBuilder<MetaQueryContext>().UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options; 
        }
        public static IMapper GetModelMapper<T,M>(){
            var MapperConfig = new MapperConfiguration(cfg => {
                cfg.CreateMap<T, M>();
                cfg.CreateMap<M, T>();
                cfg.CreateMap<M, M>();
                cfg.CreateMap<T, T>();
            });
            var mapper = MapperConfig.CreateMapper();
            return mapper;
        }

        public static IMapper GetModelMapper(Profile[] profiles)
        {
            var MapperConfig = new MapperConfiguration(cfg => {
                cfg.AddProfiles(profiles);
            });
            var mapper = MapperConfig.CreateMapper();
            return mapper;
        }

        public static CodexUser GetCodexUser(int userId = -1, string userName = "SYSTEM") { 
            return new CodexUser() { 
                CurrentUser = new Person()
                {
                    UserId = userId,
                    ADAccount = userName,
                    ADAccount511 = userName
                }
            }; 
        }

        public static DbSet<T> MockDBSetSetup<T>(this Mock<MetaQueryContext> _context, List<T> Models) where T : class
        {
            var mockDBset = CreateDbSetMock(Models);
            _context.Setup(x => x.Add(It.IsAny<T>())).Callback((T m) => Models.Add(m));
            _context.Setup(x => x.Remove(It.IsAny<T>())).Callback((T m) => Models.Remove(m));
            return mockDBset.Object;
        }

        public static Mock<DbSet<T>> CreateDbSetMock<T>(IEnumerable<T> elements) where T : class
        {
            var elementsAsQueryable = elements.AsQueryable();
            var dbSetMock = new Mock<DbSet<T>>();

            dbSetMock.As<IQueryable<T>>().Setup(m => m.Provider).Returns(elementsAsQueryable.Provider);
            dbSetMock.As<IQueryable<T>>().Setup(m => m.Expression).Returns(elementsAsQueryable.Expression);
            dbSetMock.As<IQueryable<T>>().Setup(m => m.ElementType).Returns(elementsAsQueryable.ElementType);
            dbSetMock.As<IQueryable<T>>().Setup(m => m.GetEnumerator()).Returns(() => elementsAsQueryable.GetEnumerator());
            return dbSetMock;
        }
    }
}