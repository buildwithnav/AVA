using System;
using System.Collections.Generic;
using System.Text;

namespace Ava.MetaQuery.Business.Utilities
{
    public static class QueryHelpers
    {
        public static string GetStudies => @"CAP.PKG_TEMPLATE_PROC.GET_SAMPLE_DATA";

        public static string GetAgreements => @"SELECT USERNAME, AGREE_FG, REVERIFY_FG, AGREE_DT, REVERIFY_DT FROM CAP.CAS_USER_AGREEMENT WHERE USERNAME = :pUserName;";
    }
}
