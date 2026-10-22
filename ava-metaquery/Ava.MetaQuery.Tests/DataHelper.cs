using AutoMapper;
using Castle.Components.DictionaryAdapter.Xml;
using Castle.Core.Internal;
using Ava.MetaQuery.Data;
using Codex.Business.Services;
using Codex.Data.Models;
using FakeItEasy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.ObjectPool;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Ava.MetaQuery.Tests
{
    public static class DataHelper
    {
        /// Do not change any code below ! add your custom methods at the end of the class
        private static Random random = new Random();
        private static Dictionary<Type, Func<object>> RandomValueFactory = new Dictionary<Type, Func<object>>()
        {
            { typeof(int), () => random.Next() },
            { typeof(int?), () => random.Next() },
            { typeof(double), () => random.NextDouble() },
            { typeof(double?), () => random.NextDouble() },
            { typeof(Single), () => random.NextSingle() },
            { typeof(bool), () => {return true; } },
            { typeof(bool?), () => {return false; } },
            { typeof(string), () =>
                {
                    var bytes = new byte[10];
                    random.NextBytes(bytes);
                    return Convert.ToBase64String(bytes);
                }
            },
            { typeof(DateTime), () => DateTime.Now.AddDays((random.NextDouble() - 0.5) * 100) },
            { typeof(DateTime?), () => DateTime.Now.AddDays((random.NextDouble() - 0.5) * 100) },
        };

        private static object GenerateObject(Type type)
        {
            var newObj = Activator.CreateInstance(type);
            return newObj;
        }

        public static async Task AddMockDataTo<T>(this MetaQueryContext context, int count, int idStartsAt = 1) where T : class
        {
            int existingCount = await context.Set<T>().CountAsync();
            if (existingCount >= count)
                return;

            var modelConstructor = typeof(T).GetConstructors();
            var ctor = modelConstructor[0];
            bool hasNoParameterConstructor = false;
            if (modelConstructor.Length > 1)
            {
                foreach (var c in modelConstructor)
                {
                    if (c.IsPublic && c.GetParameters().Length == 0)
                    {
                        ctor = c;
                        hasNoParameterConstructor = true;
                    }
                }
            }

            object?[] args;
            args = new object?[ctor.GetParameters().Length];
            int argIndex = 0;
            foreach (var param in ctor.GetParameters())
            {
                Console.WriteLine(string.Format(
                    "Param {0} is named {1} and is of type {2}",
                    param.Position, param.Name, param.ParameterType));

                if (RandomValueFactory.ContainsKey(param.ParameterType) == true)
                    args[argIndex] = RandomValueFactory[param.ParameterType]();
                else
                    args[argIndex] = GenerateObject(param.ParameterType);

                argIndex++;
            }


            var modelType = typeof(T);
            for (int i = 0; i < count; i++)
            {

                T newModel = hasNoParameterConstructor ? (T)Activator.CreateInstance(typeof(T)) : (T)Activator.CreateInstance(typeof(T), args);
                foreach (var p in typeof(T).GetProperties())
                {
                    object? value = null;
                    if (RandomValueFactory.ContainsKey(p.PropertyType) == true)
                        value = RandomValueFactory[p.PropertyType]();

                    if (p.GetAttribute<KeyAttribute>() != null && p.PropertyType == typeof(int))
                        p.SetValue(newModel, i + idStartsAt);
                    else
                        p.SetValue(newModel, value);
                }
                await context.AddAsync<T>(newModel);
            }

            await context.SaveChangesAsync();

        }
        /// Do not change any code above!

        /// ************ Developer's Manual Data Insertion Methods *****************

        public static async Task<MetaQueryContext> MockStudyData(this MetaQueryContext databaseContext)
        {
            if (await databaseContext.Studies.CountAsync() <= 0)
            {
                for (int i = 1; i <= 5; i++)
                {
                    databaseContext.Studies.Add(new Study
                    {
                        StudyId = i,
                        Code = "Test_"+i.ToString(),
                        Title = "Test"+i.ToString(),
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
                        OrganizationUnitId = 1,
                        Department = "CRMPM",
                        ModifiedBy = "",
                        EnteredBy = "",
                        OrganizationUnitName = "",
                        OrganizationUnitCode = "",
                        RandomFlag = "NO",
                        DSMBFlag = "NO",
                        CoreLabFlag = "NO",
                        AeSteerFlag = "NO",
                        DeviceType = ""
                    });

                    await databaseContext.SaveChangesAsync();
                }
            }
            return databaseContext;
        }


    }
}
