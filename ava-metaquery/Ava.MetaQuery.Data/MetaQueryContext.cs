using Ava.MetaQuery.Data.Models;
using Codex.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Ava.MetaQuery.Data
{
    public class MetaQueryContext : CodexContext
    {
        private readonly DbContextOptions<MetaQueryContext> _options;
        public DbContextOptions<MetaQueryContext> DbOptions => _options;

        public MetaQueryContext(DbContextOptions<MetaQueryContext> options) : base(options)
        {

            _options = options;
        }

        protected MetaQueryContext(DbContextOptions options) : base(options)
        {

        }

        public virtual DbSet<Agreement> Agreements { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

            base.OnModelCreating(modelBuilder);
        }
    }
}
