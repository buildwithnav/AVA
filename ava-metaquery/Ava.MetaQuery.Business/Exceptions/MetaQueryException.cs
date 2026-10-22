using Codex.Main.Exceptions;
using System;
using System.Collections.Generic;
using System.Text;

namespace Ava.MetaQuery.Business.Exceptions
{
    public class MetaQueryException : CodexBaseException
    {
        public MetaQueryException(string message) : base(message)
        {
        }
    }
}
