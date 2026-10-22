using Codex.Business.Extensions;
using Codex.Data.Models;
using Ava.MetaQuery.Business.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.NetworkInformation;
using Ava.MetaQuery.Data;

namespace Ava.MetaQuery.Business.Extensions
{
    public static partial class Extensions
    {
        public static IQueryable<StudyViewModel> ToStudyViewModel(this IQueryable<Study> items)
        {
            return items
                .Select(x => new StudyViewModel
                {
                    StudyId = x.StudyId,
                    Code = x.Code,
                    Title = x.Title,
                    Status = x.Status,
                    Category = x.Category
                });
        }

        public static IEnumerable<StudyViewModel> ToStudyViewModel(this IEnumerable<StudyFromProcedure> items)
        {
            return items
                 .Select(x => new StudyViewModel
                 {
                     StudyId = x.CLINICAL_STUDY_ID,
                     Code = x.STUDY,
                     Title = x.TITLE,
                     Status = x.STUDY_STATUS,
                     Category = x.STUDY_CATATEGORY
                 });
        }

        public static IQueryable<Study> Sort(this IQueryable<Study> items, StudySearchParameters parameters)
        {
            var ascending = parameters.OrderDir.ToLower() != "desc";
            var orderBy = string.IsNullOrEmpty(parameters.OrderBy) ? "" : parameters.OrderBy.ToLower();

            return orderBy switch
            {
                "studyid" => @ascending ? items.OrderBy(x => x.StudyId) : items.OrderByDescending(x => x.StudyId),
                "code" => @ascending ? items.OrderBy(x => x.Code) : items.OrderByDescending(x => x.Code),
                "title" => @ascending ? items.OrderBy(x => x.Title) : items.OrderByDescending(x => x.Title),
                "status" => @ascending ? items.OrderBy(x => x.Status) : items.OrderByDescending(x => x.Status),
                "category" => @ascending ? items.OrderBy(x => x.Category) : items.OrderByDescending(x => x.Category),
                _ => @ascending ? items.OrderBy(x => x.Code) : items.OrderByDescending(x => x.Code)
            };
        }

        public static IQueryable<Study> Filter(this IQueryable<Study> items, StudySearchParameters parameters)
        {
            if (!string.IsNullOrEmpty(parameters.Term))
            {
                var terms = parameters.Term.ToUpper().ParseTerms();

                items = terms
                    .Aggregate(items, (current, term) => current.Where(x => x.Code.ToUpper().Contains(term) ||
                                                                            x.Title.ToUpper().Contains(term) ||
                                                                            x.Status.ToUpper().Contains(term) ||
                                                                            x.Category.ToUpper().Contains(term)));
            }

            if (!string.IsNullOrEmpty(parameters.Code))
            {
                var terms = parameters.Code.ParseItems();

                items = items.Where(x => terms.Any(y => y == x.Code));
            }

            if (!string.IsNullOrEmpty(parameters.Title))
            {
                var terms = parameters.Title.ParseItems();

                items = items.Where(x => terms.Any(y => y == x.Title));
            }

            if (!string.IsNullOrEmpty(parameters.Status))
            {
                var terms = parameters.Status.ParseItems();

                items = items.Where(x => terms.Any(y => y == x.Status));
            }

            if (!string.IsNullOrEmpty(parameters.Category))
            {
                var terms = parameters.Category.ParseItems();

                items = items.Where(x => terms.Any(y => y == x.Category));
            }

            if (!string.IsNullOrEmpty(parameters.StudyId))
            {
                var terms = parameters.StudyId.ParseItems().ToIntArray();

                items = items.Where(x => terms.Any(y => y == x.StudyId));
            }

            return items;
        }
    }
}
