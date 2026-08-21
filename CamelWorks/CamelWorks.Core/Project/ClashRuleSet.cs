using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Clash;
using CamelWorks.Core.Store;

namespace CamelWorks.Core.Project
{
    /// <summary>
    /// What a rule needs the user to supply, so one editor can draw every rule.
    /// </summary>
    public enum SpecInput
    {
        /// <summary>Nothing.</summary>
        None = 0,

        /// <summary>One number.</summary>
        Number = 1,

        /// <summary>Two numbers, a range.</summary>
        Range = 2,

        /// <summary>One piece of text.</summary>
        Text = 3,

        /// <summary>A name and a value.</summary>
        NameAndValue = 4,

        /// <summary>Other rules, nested.</summary>
        Nested = 5,
    }

    /// <summary>One kind of rule, as the editor offers it.</summary>
    public sealed class SpecKind
    {
        internal SpecKind(string id, string title, SpecInput input, string hint)
        {
            Id = id; Title = title; Input = input; Hint = hint;
        }

        /// <summary>Stable id, written to the file.</summary>
        public string Id { get; }

        /// <summary>How it reads in the editor.</summary>
        public string Title { get; }

        /// <summary>What the editor has to ask for.</summary>
        public SpecInput Input { get; }

        /// <summary>What the two number or text boxes mean.</summary>
        public string Hint { get; }

        /// <inheritdoc />
        public override string ToString() => Title;
    }

    /// <summary>
    /// A clash test, as data rather than as a closure.
    ///
    /// <see cref="ClashPredicates"/> hands out sealed private implementations, which is right — the
    /// pipeline should not be able to reach inside a rule. But a rule the user built has to survive
    /// being saved and reopened, and an editor has to be able to draw it. So the saved form is this
    /// recipe, and <see cref="Build"/> turns it back into the real thing. The closed types stay
    /// closed and the file stays readable, which a serialised delegate would manage neither of.
    /// </summary>
    public sealed class PredicateSpec
    {
        /// <summary>Create a spec.</summary>
        /// <param name="kind">One of the ids in <see cref="Kinds"/>.</param>
        public PredicateSpec(string kind)
        {
            Kind = string.IsNullOrWhiteSpace(kind) ? throw new ArgumentException("kind is required", nameof(kind)) : kind;
        }

        /// <summary>Which rule this is.</summary>
        public string Kind { get; }

        /// <summary>First number, where the rule takes one.</summary>
        public double A { get; set; }

        /// <summary>Second number, where the rule takes a range.</summary>
        public double B { get; set; }

        /// <summary>The name, set or category the rule works on.</summary>
        public string? Name { get; set; }

        /// <summary>The value the rule compares against.</summary>
        public string? Value { get; set; }

        /// <summary>Nested rules, for the combining kinds.</summary>
        public IList<PredicateSpec> Parts { get; } = new List<PredicateSpec>();

        /// <summary>Every kind the editor offers, in the order it offers them.</summary>
        public static IReadOnlyList<SpecKind> Kinds { get; } = new[]
        {
            new SpecKind("always", "Every result", SpecInput.None, string.Empty),
            new SpecKind("maxVolume", "Overlap smaller than", SpecInput.Number, "cubic metres"),
            new SpecKind("distance", "Distance between", SpecInput.Range, "metres, from and to"),
            new SpecKind("angle", "Angle between", SpecInput.Range, "degrees, from and to"),
            new SpecKind("sameModel", "Both items in the same model", SpecInput.None, string.Empty),
            new SpecKind("eitherInSet", "Either item in set", SpecInput.Text, "set name"),
            new SpecKind("bothInSet", "Both items in set", SpecInput.Text, "set name"),
            new SpecKind("propertyEquals", "Property equals", SpecInput.NameAndValue, "property, then value"),
            new SpecKind("propertyContains", "Property contains", SpecInput.NameAndValue, "property, then text"),
            new SpecKind("category", "Either item is a", SpecInput.Text, "category"),
            new SpecKind("not", "Not", SpecInput.Nested, "inverts the rule inside it"),
            new SpecKind("all", "All of", SpecInput.Nested, "every rule inside must match"),
            new SpecKind("any", "Any of", SpecInput.Nested, "one rule inside is enough"),
        };

        /// <summary>The kind descriptor, or null when the file names one this build does not know.</summary>
        public SpecKind? Descriptor => Kinds.FirstOrDefault(k => string.Equals(k.Id, Kind, StringComparison.Ordinal));

        /// <summary>
        /// Turn the recipe into the real rule.
        ///
        /// An unknown kind becomes a rule that matches nothing rather than a rule that matches
        /// everything. A saved file naming a rule this build cannot run must not quietly suppress
        /// the whole board.
        /// </summary>
        public IClashPredicate Build() => Kind switch
        {
            "always" => ClashPredicates.Always(),
            "maxVolume" => ClashPredicates.MaxOverlapVolume(A),
            "distance" => ClashPredicates.DistanceBetween(A, B),
            "angle" => ClashPredicates.AngleBetween(A, B),
            "sameModel" => ClashPredicates.SameModel(),
            "eitherInSet" => ClashPredicates.EitherInSet(Name ?? string.Empty),
            "bothInSet" => ClashPredicates.BothInSet(Name ?? string.Empty),
            "propertyEquals" => ClashPredicates.PropertyEquals(Name ?? string.Empty, Value),
            "propertyContains" => ClashPredicates.PropertyContains(Name ?? string.Empty, Value ?? string.Empty),
            "category" => ClashPredicates.EitherCategoryIs(Name ?? string.Empty),
            "not" => ClashPredicates.Not(Parts.Count > 0 ? Parts[0].Build() : ClashPredicates.Always()),
            "all" => ClashPredicates.All(Parts.Select(p => p.Build()).ToArray()),
            "any" => ClashPredicates.Any(Parts.Select(p => p.Build()).ToArray()),
            _ => ClashPredicates.Not(ClashPredicates.Always()),
        };

        /// <summary>How the rule reads on one line, without building it.</summary>
        public string Describe()
        {
            var descriptor = Descriptor;
            if (descriptor == null) return "unknown rule '" + Kind + "'";

            return descriptor.Input switch
            {
                SpecInput.None => descriptor.Title,
                SpecInput.Number => descriptor.Title + " " + Num(A),
                SpecInput.Range => descriptor.Title + " " + Num(A) + " and " + Num(B),
                SpecInput.Text => descriptor.Title + " " + Quote(Name),
                SpecInput.NameAndValue => descriptor.Title.Replace("Property", Quote(Name)) + " " + Quote(Value),
                SpecInput.Nested => descriptor.Title + " (" + string.Join("; ", Parts.Select(p => p.Describe())) + ")",
                _ => descriptor.Title,
            };
        }

        private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Quote(string? text) => "'" + (text ?? string.Empty) + "'";

        /// <summary>Serialise.</summary>
        public JsonValue ToJson()
        {
            var json = JsonValue.Object().Set("kind", JsonValue.String(Kind));

            if (A != 0) json.Set("a", JsonValue.Number(A));
            if (B != 0) json.Set("b", JsonValue.Number(B));
            if (Name != null) json.Set("name", JsonValue.String(Name));
            if (Value != null) json.Set("value", JsonValue.String(Value));
            if (Parts.Count > 0) json.Set("parts", JsonValue.Array(Parts.Select(p => p.ToJson())));

            return json;
        }

        /// <summary>Read one back, or null when the JSON is not a spec.</summary>
        /// <param name="json">The candidate.</param>
        public static PredicateSpec? FromJson(JsonValue? json)
        {
            if (json == null || json.Kind != JsonKind.Object) return null;

            var kind = json["kind"].AsString();
            if (string.IsNullOrWhiteSpace(kind)) return null;

            var spec = new PredicateSpec(kind!)
            {
                A = json["a"].AsDouble(),
                B = json["b"].AsDouble(),
                Name = json["name"].AsString(),
                Value = json["value"].AsString(),
            };

            foreach (var part in json["parts"].Items)
            {
                var child = FromJson(part);
                if (child != null) spec.Parts.Add(child);
            }

            return spec;
        }

        /// <inheritdoc />
        public override string ToString() => Describe();
    }

    /// <summary>A grouping rule as data. Same reasoning as <see cref="PredicateSpec"/>.</summary>
    public sealed class GroupingSpec
    {
        /// <summary>Create a spec.</summary>
        /// <param name="kind">One of the ids in <see cref="Kinds"/>.</param>
        public GroupingSpec(string kind)
        {
            Kind = string.IsNullOrWhiteSpace(kind) ? throw new ArgumentException("kind is required", nameof(kind)) : kind;
        }

        /// <summary>Which rule this is.</summary>
        public string Kind { get; }

        /// <summary>The radius, for the proximity rule.</summary>
        public double A { get; set; }

        /// <summary>The property key, for the property rule.</summary>
        public string? Name { get; set; }

        /// <summary>Every kind the editor offers.</summary>
        public static IReadOnlyList<SpecKind> Kinds { get; } = new[]
        {
            new SpecKind("modelPair", "Model pair", SpecInput.None, string.Empty),
            new SpecKind("disciplinePair", "Discipline pair", SpecInput.None, string.Empty),
            new SpecKind("level", "Level", SpecInput.None, string.Empty),
            new SpecKind("grid", "Grid", SpecInput.None, string.Empty),
            new SpecKind("zone", "Zone", SpecInput.None, string.Empty),
            new SpecKind("test", "Clash test", SpecInput.None, string.Empty),
            new SpecKind("system", "System", SpecInput.None, string.Empty),
            new SpecKind("sameItem", "Same item on both sides", SpecInput.None, string.Empty),
            new SpecKind("proximity", "Within a distance", SpecInput.Number, "metres"),
            new SpecKind("property", "A property", SpecInput.Text, "property name"),
        };

        /// <summary>The kind descriptor, or null when the file names one this build does not know.</summary>
        public SpecKind? Descriptor => Kinds.FirstOrDefault(k => string.Equals(k.Id, Kind, StringComparison.Ordinal));

        /// <summary>
        /// Turn the recipe into the real rule, or null when this build does not know the kind.
        ///
        /// Null rather than a stand-in: an unknown grouping rule dropped from the stack changes how
        /// the board is arranged, which the user can see, while a stand-in that groups by nothing
        /// would look like the rule ran and found no structure.
        /// </summary>
        public IGroupingRule? Build() => Kind switch
        {
            "modelPair" => GroupingRules.ByModelPair(),
            "disciplinePair" => GroupingRules.ByDisciplinePair(),
            "level" => GroupingRules.ByLevel(),
            "grid" => GroupingRules.ByGrid(),
            "zone" => GroupingRules.ByZone(),
            "test" => GroupingRules.ByTest(),
            "system" => GroupingRules.BySystem(),
            "sameItem" => GroupingRules.BySameItem(),
            "proximity" => GroupingRules.ByProximity(A > 0 ? A : 5.0),
            "property" => string.IsNullOrWhiteSpace(Name) ? null : GroupingRules.ByProperty(Name!),
            _ => null,
        };

        /// <summary>How the rule reads on one line.</summary>
        public string Describe()
        {
            var descriptor = Descriptor;
            if (descriptor == null) return "unknown rule '" + Kind + "'";

            return descriptor.Input switch
            {
                SpecInput.Number => descriptor.Title + " " + A.ToString("0.###", CultureInfo.InvariantCulture) + " m",
                SpecInput.Text => descriptor.Title + " '" + (Name ?? string.Empty) + "'",
                _ => descriptor.Title,
            };
        }

        /// <summary>Serialise.</summary>
        public JsonValue ToJson()
        {
            var json = JsonValue.Object().Set("kind", JsonValue.String(Kind));
            if (A != 0) json.Set("a", JsonValue.Number(A));
            if (Name != null) json.Set("name", JsonValue.String(Name));
            return json;
        }

        /// <summary>Read one back, or null.</summary>
        /// <param name="json">The candidate.</param>
        public static GroupingSpec? FromJson(JsonValue? json)
        {
            if (json == null || json.Kind != JsonKind.Object) return null;

            var kind = json["kind"].AsString();
            if (string.IsNullOrWhiteSpace(kind)) return null;

            return new GroupingSpec(kind!) { A = json["a"].AsDouble(), Name = json["name"].AsString() };
        }

        /// <inheritdoc />
        public override string ToString() => Describe();
    }

    /// <summary>A suppress-or-flag rule, saved.</summary>
    public sealed class FilterSpec
    {
        /// <summary>Create a rule.</summary>
        /// <param name="name">What it is called on the board and in the funnel.</param>
        /// <param name="when">What it matches.</param>
        /// <param name="suppress">True to remove matches from the board, false to flag them on it.</param>
        public FilterSpec(string name, PredicateSpec when, bool suppress)
        {
            Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("name is required", nameof(name)) : name;
            When = when ?? throw new ArgumentNullException(nameof(when));
            Suppress = suppress;
        }

        /// <summary>What it is called.</summary>
        public string Name { get; }

        /// <summary>What it matches.</summary>
        public PredicateSpec When { get; }

        /// <summary>True to suppress, false to flag.</summary>
        public bool Suppress { get; }

        /// <summary>Off rules stay in the list and do nothing.</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>Build the real rule.</summary>
        public FilterRule Build() => new FilterRule(Name, When.Build(), Suppress);

        /// <summary>Serialise.</summary>
        public JsonValue ToJson() =>
            JsonValue.Object()
                .Set("name", JsonValue.String(Name))
                .Set("suppress", JsonValue.String(Suppress ? "yes" : "no"))
                .Set("enabled", JsonValue.String(IsEnabled ? "yes" : "no"))
                .Set("when", When.ToJson());

        /// <summary>Read one back, or null.</summary>
        /// <param name="json">The candidate.</param>
        public static FilterSpec? FromJson(JsonValue? json)
        {
            if (json == null || json.Kind != JsonKind.Object) return null;

            var name = json["name"].AsString();
            var when = PredicateSpec.FromJson(json["when"]);
            if (string.IsNullOrWhiteSpace(name) || when == null) return null;

            return new FilterSpec(name!, when, json["suppress"].AsString() == "yes")
            {
                IsEnabled = json["enabled"].AsString() != "no",
            };
        }

        /// <inheritdoc />
        public override string ToString() =>
            (Suppress ? "Suppress" : "Flag") + " " + Name + ": " + When.Describe();
    }

    /// <summary>An assignment rule, saved.</summary>
    public sealed class AssignSpec
    {
        /// <summary>Create a rule.</summary>
        /// <param name="when">What it matches.</param>
        /// <param name="party">Who the match goes to.</param>
        /// <param name="priority">The priority to stamp, or null.</param>
        public AssignSpec(PredicateSpec when, string party, string? priority = null)
        {
            When = when ?? throw new ArgumentNullException(nameof(when));
            Party = string.IsNullOrWhiteSpace(party) ? throw new ArgumentException("party is required", nameof(party)) : party;
            Priority = priority;
        }

        /// <summary>What it matches.</summary>
        public PredicateSpec When { get; }

        /// <summary>Who the match goes to.</summary>
        public string Party { get; }

        /// <summary>The priority to stamp, or null.</summary>
        public string? Priority { get; }

        /// <summary>Off rules stay in the list and do nothing.</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>Build the real rule.</summary>
        public AssignRule Build() => new AssignRule(When.Build(), Party, Priority);

        /// <summary>Serialise.</summary>
        public JsonValue ToJson()
        {
            var json = JsonValue.Object()
                .Set("party", JsonValue.String(Party))
                .Set("enabled", JsonValue.String(IsEnabled ? "yes" : "no"))
                .Set("when", When.ToJson());

            if (Priority != null) json.Set("priority", JsonValue.String(Priority));
            return json;
        }

        /// <summary>Read one back, or null.</summary>
        /// <param name="json">The candidate.</param>
        public static AssignSpec? FromJson(JsonValue? json)
        {
            if (json == null || json.Kind != JsonKind.Object) return null;

            var party = json["party"].AsString();
            var when = PredicateSpec.FromJson(json["when"]);
            if (string.IsNullOrWhiteSpace(party) || when == null) return null;

            return new AssignSpec(when, party!, json["priority"].AsString())
            {
                IsEnabled = json["enabled"].AsString() != "no",
            };
        }

        /// <inheritdoc />
        public override string ToString() => Party + " gets " + When.Describe();
    }

    /// <summary>
    /// Every clash rule on the project, in one saveable object.
    ///
    /// <b>Empty is a working state.</b> <see cref="ToOptions"/> on a fresh rule set produces the
    /// default grouping stack and no filters, which is exactly what the board needs to open
    /// populated on a model nobody has configured — the zero-setup rule, at the one screen where it
    /// is hardest to keep.
    /// </summary>
    public sealed class ClashRuleSet
    {
        /// <summary>Suppress and flag rules, in order.</summary>
        public IList<FilterSpec> Filters { get; } = new List<FilterSpec>();

        /// <summary>The grouping stack, outermost first. Empty means the default stack.</summary>
        public IList<GroupingSpec> Grouping { get; } = new List<GroupingSpec>();

        /// <summary>Assignment rules, first match wins.</summary>
        public IList<AssignSpec> Assigns { get; } = new List<AssignSpec>();

        /// <summary>Group names the user pinned, so re-running does not rearrange them.</summary>
        public IDictionary<string, IReadOnlyList<string>> Pinned { get; } =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        /// <summary>True when nothing has been configured and the defaults are what will run.</summary>
        public bool IsDefault => Filters.Count == 0 && Grouping.Count == 0 && Assigns.Count == 0;

        /// <summary>
        /// The options to run the pipeline with.
        /// </summary>
        /// <param name="proximityMetres">The default proximity band, used when no grouping is saved.</param>
        public ClashPipelineOptions ToOptions(double proximityMetres = 5.0)
        {
            var grouping = Grouping.Select(g => g.Build()).Where(g => g != null).Select(g => g!).ToList();

            return new ClashPipelineOptions
            {
                Filters = Filters.Where(f => f.IsEnabled).Select(f => f.Build()).ToList(),
                Grouping = grouping.Count > 0
                    ? (IReadOnlyList<IGroupingRule>)grouping
                    : new IGroupingRule[]
                    {
                        GroupingRules.ByModelPair(),
                        GroupingRules.ByLevel(),
                        GroupingRules.ByProximity(proximityMetres > 0 ? proximityMetres : 5.0),
                    },
                Assigns = Assigns.Where(a => a.IsEnabled).Select(a => a.Build()).ToList(),
                PinnedGroups = new Dictionary<string, IReadOnlyList<string>>(Pinned, StringComparer.Ordinal),
            };
        }

        /// <summary>Serialise.</summary>
        public JsonValue ToJson()
        {
            var pinned = JsonValue.Object();
            foreach (var pair in Pinned)
                pinned.Set(pair.Key, JsonValue.Array(pair.Value.Select(JsonValue.String)));

            return JsonValue.Object()
                .Set("filters", JsonValue.Array(Filters.Select(f => f.ToJson())))
                .Set("grouping", JsonValue.Array(Grouping.Select(g => g.ToJson())))
                .Set("assigns", JsonValue.Array(Assigns.Select(a => a.ToJson())))
                .Set("pinned", pinned);
        }

        /// <summary>Read a rule set back. Anything unreadable is skipped rather than failing the load.</summary>
        /// <param name="json">The saved section.</param>
        public static ClashRuleSet FromJson(JsonValue? json)
        {
            var rules = new ClashRuleSet();
            if (json == null || json.Kind != JsonKind.Object) return rules;

            foreach (var item in json["filters"].Items)
            {
                var filter = FilterSpec.FromJson(item);
                if (filter != null) rules.Filters.Add(filter);
            }

            foreach (var item in json["grouping"].Items)
            {
                var group = GroupingSpec.FromJson(item);
                if (group != null) rules.Grouping.Add(group);
            }

            foreach (var item in json["assigns"].Items)
            {
                var assign = AssignSpec.FromJson(item);
                if (assign != null) rules.Assigns.Add(assign);
            }

            var pinned = json["pinned"];
            foreach (var key in pinned.Keys)
                rules.Pinned[key] = pinned[key].Items.Select(i => i.AsString() ?? string.Empty)
                                                    .Where(s => s.Length > 0).ToList();

            return rules;
        }

        /// <inheritdoc />
        public override string ToString() =>
            IsDefault
                ? "the built-in rules"
                : Filters.Count + " filters, " + Grouping.Count + " grouping, " + Assigns.Count + " assignments";
    }
}
