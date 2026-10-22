using Microsoft.EntityFrameworkCore.Metadata.Internal;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace Ava.MetaQuery.Data.Models
{
    [Table("CAS_USER_AGREEMENT", Schema = "CAP")]
    public class Agreement
    {
        [Column("USERNAME"), Key]
        public string Username { get; set; }
        [Column("AGREE_FG")]
        public string AgreeFg { get; set; }
        [Column("REVERIFY_FG")]
        public string ReverifyFg { get; set; }
        [Column("AGREE_DT")]
        public DateTime AgreeDt { get; set; }
        [Column("REVERIFY_DT")]
        public DateTime ReverifyDt { get; set; }
    }
}
