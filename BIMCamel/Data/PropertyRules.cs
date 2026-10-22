using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace BIMCamel.Data
{
    public enum RuleTest { Equals = 0, Contains = 1, StartsWith = 2 }

    /// <summary>
    /// "IF Element / Category equals Walls THEN IfcWall": a class (and/or classification code)
    /// chosen by a property value, with no search set to build in Navisworks first.
    /// </summary>
    public sealed class PropertyRule
    {
        public PropRef Source;
        public RuleTest Test;
        public string Value = "";
        /// <summary>Encoded class key (<see cref="TypeMapping.Encode"/>), or "" for a code-only rule.</summary>
        public string ClassKey = "";
        public string Classification = "";

        public bool Matches(string actual)
        {
            if (string.IsNullOrEmpty(actual)) return false;
            string a = actual.Trim(), v = Value.Trim();
            return Test switch
            {
                RuleTest.Contains => a.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0,
                RuleTest.StartsWith => a.StartsWith(v, StringComparison.OrdinalIgnoreCase),
                _ => string.Equals(a, v, StringComparison.OrdinalIgnoreCase)
            };
        }
    }

    /// <summary>
    /// Applies property rules to one element. Rules run AFTER the set rules and only fill what
    /// those left empty: a set is a selection the user made by hand, a property rule is a
    /// pattern, and the hand-made decision wins. Among property rules the first match wins, the
    /// same as set rules.
    /// </summary>
    public static class PropertyRules
    {
        public static void Apply(ModelItem item, List<PropertyRule>? rules, ref string? classKey, ref string? classCode)
        {
            if (rules == null || rules.Count == 0) return;
            bool needClass = string.IsNullOrEmpty(classKey), needCode = string.IsNullOrEmpty(classCode);
            if (!needClass && !needCode) return;

            // Each distinct property is read once per element, through the same reader as the
            // roles, so the "read properties from" setting applies here too.
            var values = new Dictionary<(string, string), string>();
            foreach (var r in rules)
            {
                bool givesClass = needClass && r.ClassKey.Length > 0;
                bool givesCode = needCode && r.Classification.Length > 0;
                if (!givesClass && !givesCode) continue;

                var k = (r.Source.Category, r.Source.Name);
                if (!values.TryGetValue(k, out var actual))
                {
                    actual = ReadValue(item, r.Source);
                    values[k] = actual;
                }
                if (!r.Matches(actual)) continue;

                if (givesClass) { classKey = r.ClassKey; needClass = false; }
                if (givesCode) { classCode = r.Classification; needCode = false; }
                if (!needClass && !needCode) return;
            }
        }

        /// <summary>One property's value as text, "" when absent.</summary>
        public static string ReadValue(ModelItem item, PropRef source)
        {
            try { return PropertyHarvester.ReadRoles(item, new PropertyRoles { Type = source }).Type ?? ""; }
            catch { return ""; }
        }

        /// <summary>True when any rule could give an element a class, for the preview's counts.</summary>
        public static bool AnyClassRule(List<PropertyRule>? rules) => rules != null && rules.Any(r => r.ClassKey.Length > 0);
    }
}
