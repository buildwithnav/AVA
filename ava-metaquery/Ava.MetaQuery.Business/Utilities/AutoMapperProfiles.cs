using AutoMapper;
using Codex.Data.Models;
using Ava.MetaQuery.Business.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Ava.MetaQuery.Business.Utilities
{
    class AutoMapperProfiles : Profile
    {
        public AutoMapperProfiles()
        {
            CreateMap<Study, StudyViewModel>();
            CreateMap<Study, StudyDetailViewModel>();
            CreateMap<Person, UserViewModel>();
        }
    }
}
