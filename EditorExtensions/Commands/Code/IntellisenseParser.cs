﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using EnvDTE;
using EnvDTE80;

namespace MadsKristensen.EditorExtensions
{
    public static class IntellisenseParser
    {
        private const string DefaultModuleName = "server";
        private const string ModuleNameAttributeName = "TypeScriptModule";

        private static void AddScript(string filePath, string extension, List<IntellisenseObject> list)
        {
            string resultPath = filePath + extension;

            if (!File.Exists(resultPath))
                return;

            IntellisenseWriter.Write(list, resultPath);

            var item = MarginBase.AddFileToProject(filePath, resultPath);

            if (extension.Equals(Ext.TypeScript, StringComparison.OrdinalIgnoreCase))
                item.Properties.Item("ItemType").Value = "TypeScriptCompile";
            else
            {
                item.Properties.Item("ItemType").Value = "None";
            }
        }

        public static List<IntellisenseObject> ProcessFile(ProjectItem item)
        {
            if (item.FileCodeModel == null)
                return null;

            var list = new List<IntellisenseObject>();

            foreach (CodeElement element in item.FileCodeModel.CodeElements)
            {
                if (element.Kind == vsCMElement.vsCMElementNamespace)
                {
                    var cn = (CodeNamespace)element;
                    foreach (CodeElement member in cn.Members)
                    {
                        if (ShouldProcess(member))
                        {
                            ProcessElement(member, list);
                        }
                    }
                }
                else if (ShouldProcess(element))
                {
                    ProcessElement(element, list);
                }
            }

            return list;
        }

        private static void ProcessElement(CodeElement element, List<IntellisenseObject> list)
        {
            if (element.Kind == vsCMElement.vsCMElementEnum)
            {
                ProcessEnum((CodeEnum)element, list);
            }
            else if (element.Kind == vsCMElement.vsCMElementClass)
            {
                ProcessClass((CodeClass)element, list);
            }
        }

        private static bool ShouldProcess(CodeElement member)
        {
            return
                member.Kind == vsCMElement.vsCMElementClass
                || member.Kind == vsCMElement.vsCMElementEnum;
        }

        private static void ProcessEnum(CodeEnum element, List<IntellisenseObject> list)
        {
            var data = new IntellisenseObject
            {
                Name = element.Name,
                IsEnum = element.Kind == vsCMElement.vsCMElementEnum,
                FullName = element.FullName,
                Namespace = GetNamespace(element),
                Summary = GetSummary(element),
            };

            foreach (var codeEnum in element.Members.OfType<CodeElement>())
            {
                var prop = new IntellisenseProperty
                {
                    Name = codeEnum.Name,
                };

                data.Properties.Add(prop);
            }

            if (data.Properties.Count > 0)
                list.Add(data);
        }

        private static void ProcessClass(CodeClass cc, List<IntellisenseObject> list)
        {
            var properties = GetProperties(cc.Members, new HashSet<string>()).ToList();

            if (properties.Any())
            {
                var intellisenseObject = new IntellisenseObject(properties)
                {
                    Namespace = GetNamespace(cc),
                    Name = cc.Name,
                    FullName = cc.FullName,
                    Summary = GetSummary(cc),
                };

                list.Add(intellisenseObject);
            }
        }

        private static IEnumerable<IntellisenseProperty> GetProperties(CodeElements props,
            HashSet<string> traversedTypes)
        {
            return from p in props.OfType<CodeProperty>()
                where p.Attributes.Cast<CodeAttribute>().All(a => a.Name != "IgnoreDataMember")
                where p.Getter != null && !p.Getter.IsShared && p.Getter.Access == vsCMAccess.vsCMAccessPublic
                select new IntellisenseProperty
                {
                    Name = GetName(p),
                    Type = GetType(p.Type, traversedTypes),
                    Summary = GetSummary(p)
                };
        }

        private static string GetNamespace(CodeClass e)
        {
            return GetNamespace(e.Attributes);
        }

        private static string GetNamespace(CodeEnum e)
        {
            return GetNamespace(e.Attributes);
        }

        private static string GetNamespace(CodeElements attrs)
        {
            if (attrs == null) return DefaultModuleName;
            var namespaceFromAttr = from a in attrs.Cast<CodeAttribute2>()
                where a.Name.EndsWith(ModuleNameAttributeName, StringComparison.OrdinalIgnoreCase)
                from arg in a.Arguments.Cast<CodeAttributeArgument>()
                let v = (arg.Value ?? "").Trim('\"')
                where !String.IsNullOrWhiteSpace(v)
                select v;

            return namespaceFromAttr.FirstOrDefault() ?? DefaultModuleName;
        }

        private static IntellisenseType GetType(CodeTypeRef codeTypeRef, HashSet<string> traversedTypes)
        {
            // TODO: Is there a way to extract the CodeTypeRef for a generic parameter?
            var isArray = codeTypeRef.TypeKind == vsCMTypeRef.vsCMTypeRefArray;
            if (isArray && codeTypeRef.ElementType != null) codeTypeRef = codeTypeRef.ElementType;
            bool isCollection = codeTypeRef.AsString.StartsWith("System.Collections", StringComparison.Ordinal);

            var cl = codeTypeRef.CodeType as CodeClass;
            var en = codeTypeRef.CodeType as CodeEnum;
            var isPrimitive = IsPrimitive(codeTypeRef);
            var result = new IntellisenseType
            {
                IsArray = isArray || isCollection,
                CodeName = codeTypeRef.AsString,
                ClientSideReferenceName =
                    codeTypeRef.TypeKind == vsCMTypeRef.vsCMTypeRefCodeType &&
                    codeTypeRef.CodeType.InfoLocation == vsCMInfoLocation.vsCMInfoLocationProject
                        ? (cl != null && HasIntellisense(cl.ProjectItem, Ext.TypeScript)
                            ? (GetNamespace(cl) + "." + cl.Name)
                            : null) ??
                          (en != null && HasIntellisense(en.ProjectItem, Ext.TypeScript)
                              ? (GetNamespace(en) + "." + en.Name)
                              : null)
                        : null
            };

            if (!isPrimitive && cl != null && !traversedTypes.Contains(codeTypeRef.CodeType.FullName) && !isCollection)
            {
                traversedTypes.Add(codeTypeRef.CodeType.FullName);
                result.Shape = GetProperties(codeTypeRef.CodeType.Members, traversedTypes).ToList();
                traversedTypes.Remove(codeTypeRef.CodeType.FullName);
            }

            return result;
        }

        private static bool IsPrimitive(CodeTypeRef codeTypeRef)
        {
            if (codeTypeRef.TypeKind != vsCMTypeRef.vsCMTypeRefOther &&
                codeTypeRef.TypeKind != vsCMTypeRef.vsCMTypeRefCodeType)
                return true;

            if (codeTypeRef.AsString.EndsWith("DateTime", StringComparison.Ordinal))
                return true;

            return false;
        }

        private static bool HasIntellisense(ProjectItem projectItem, string ext)
        {
            for (short i = 0; i < projectItem.FileCount; i++)
            {
                if (File.Exists(projectItem.FileNames[i] + ext)) return true;
            }
            return false;
        }

        // Maps attribute name to array of attribute properties to get resultant name from
        private static readonly IReadOnlyDictionary<string, string[]> nameAttributes = new Dictionary<string, string[]>
        {
            {"DataMember", new[] {"Name"}},
            {"JsonProperty", new[] {"", "PropertyName"}}
        };

        private static string GetName(CodeProperty property)
        {
            foreach (CodeAttribute attr in property.Attributes)
            {
                var className = Path.GetExtension(attr.Name);
                if (string.IsNullOrEmpty(className)) className = attr.Name;

                string[] argumentNames;
                if (!nameAttributes.TryGetValue(className, out argumentNames))
                    continue;

                var value =
                    attr.Children.OfType<CodeAttributeArgument>().FirstOrDefault(a => argumentNames.Contains(a.Name));

                if (value == null)
                    break;

                // Strip the leading & trailing quotes
                return value.Value.Substring(1, value.Value.Length - 2);
            }

            return property.Name;
        }

        // External items throw an exception from the DocComment getter
        private static string GetSummary(CodeProperty property)
        {
            return property.InfoLocation != vsCMInfoLocation.vsCMInfoLocationProject
                ? null
                : GetSummary(property.InfoLocation, property.DocComment, property.FullName);
        }

        private static string GetSummary(CodeClass property)
        {
            return GetSummary(property.InfoLocation, property.DocComment, property.FullName);
        }

        private static string GetSummary(CodeEnum property)
        {
            return GetSummary(property.InfoLocation, property.DocComment, property.FullName);
        }

        private static string GetSummary(vsCMInfoLocation location, string comment, string fullName)
        {
            if (location != vsCMInfoLocation.vsCMInfoLocationProject || String.IsNullOrWhiteSpace(comment))
                return null;

            try
            {
                string summary = XElement.Parse(comment)
                    .Descendants("summary")
                    .Select(x => x.Value)
                    .FirstOrDefault();
                if (!String.IsNullOrEmpty(summary)) summary = summary.Trim();

                return summary;
            }
            catch (Exception ex)
            {
                Logger.Log("Couldn't parse XML Doc Comment for " + fullName + ":\n" + ex);
                return null;
            }
        }

        internal static class Ext
        {
            public const string JavaScript = ".js";
            public const string TypeScript = ".d.ts";
        }
    }
}