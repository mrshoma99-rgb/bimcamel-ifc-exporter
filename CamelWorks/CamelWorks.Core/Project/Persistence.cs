using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Appearance;
using CamelWorks.Core.Identity;
using CamelWorks.Core.Sets;
using CamelWorks.Core.Store;

namespace CamelWorks.Core.Project
{
    /// <summary>
    /// Set expressions, on disk.
    ///
    /// Reading is lenient and writing is strict, the same asymmetry the BCF layer uses: a saved
    /// expression this build cannot understand becomes <see cref="SetExpression.Nothing"/> rather
    /// than throwing, because a set that turns out to be empty is a visible, reportable problem and
    /// a project file that will not open is not.
    /// </summary>
    public static class SetExpressionJson
    {
        /// <summary>Serialise an expression.</summary>
        /// <param name="expression">The expression.</param>
        public static JsonValue Write(SetExpression expression)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));

            switch (expression)
            {
                case ConstantExpression constant:
                    return JsonValue.Object().Set("op", JsonValue.String(constant.Value ? "everything" : "nothing"));

                case ConditionExpression condition:
                    var c = condition.Condition;
                    var json = JsonValue.Object()
                        .Set("op", JsonValue.String("where"))
                        .Set("category", JsonValue.String(c.Category))
                        .Set("comparison", JsonValue.String(c.Operator.ToString()));

                    if (c.Property != null) json.Set("property", JsonValue.String(c.Property));
                    if (c.Value != null) json.Set("value", JsonValue.String(c.Value));
                    return json;

                case SetReferenceExpression reference:
                    var r = JsonValue.Object()
                        .Set("op", JsonValue.String("inSet"))
                        .Set("set", JsonValue.String(reference.SetId));

                    if (reference.DisplayName != null) r.Set("name", JsonValue.String(reference.DisplayName));
                    return r;

                case NotExpression not:
                    return JsonValue.Object()
                        .Set("op", JsonValue.String("not"))
                        .Set("parts", JsonValue.Array(new[] { Write(not.Inner) }));

                case AndExpression and:
                    return JsonValue.Object()
                        .Set("op", JsonValue.String("and"))
                        .Set("parts", JsonValue.Array(and.Parts.Select(Write)));

                case OrExpression or:
                    return JsonValue.Object()
                        .Set("op", JsonValue.String("or"))
                        .Set("parts", JsonValue.Array(or.Parts.Select(Write)));

                default:
                    // The AST is closed, so this is unreachable unless a case is added above without
                    // a case here. Named rather than silently written as "nothing", which would look
                    // like a user's empty set.
                    throw new NotSupportedException("no serialisation for " + expression.GetType().Name);
            }
        }

        /// <summary>Read an expression back. Never throws; unreadable input becomes nothing.</summary>
        /// <param name="json">The saved expression.</param>
        public static SetExpression Read(JsonValue? json)
        {
            if (json == null || json.Kind != JsonKind.Object) return SetExpression.Nothing;

            switch (json["op"].AsString())
            {
                case "everything":
                    return SetExpression.Everything;

                case "nothing":
                    return SetExpression.Nothing;

                case "where":
                    return ReadCondition(json);

                case "inSet":
                    var setId = json["set"].AsString();
                    return string.IsNullOrWhiteSpace(setId)
                        ? SetExpression.Nothing
                        : SetExpression.InSet(setId!, json["name"].AsString());

                case "not":
                    var inner = json["parts"].Items.Select(Read).FirstOrDefault();
                    return inner == null ? SetExpression.Nothing : SetExpression.Not(inner);

                case "and":
                    return SetExpression.And(json["parts"].Items.Select(Read).ToArray());

                case "or":
                    return SetExpression.Or(json["parts"].Items.Select(Read).ToArray());

                default:
                    return SetExpression.Nothing;
            }
        }

        private static SetExpression ReadCondition(JsonValue json)
        {
            var category = json["category"].AsString();
            if (string.IsNullOrWhiteSpace(category)) return SetExpression.Nothing;

            if (!Enum.TryParse<SetOperator>(json["comparison"].AsString(), out var op)) return SetExpression.Nothing;

            try
            {
                return SetExpression.Where(new SetCondition(category!, json["property"].AsString(), op,
                                                            json["value"].AsString()));
            }
            catch (ArgumentException)
            {
                // A saved condition whose operator and value no longer agree — a hand-edited file,
                // or one written before an operator changed shape.
                return SetExpression.Nothing;
            }
        }
    }

    /// <summary>One set the user built and named.</summary>
    public sealed class SavedSet
    {
        /// <summary>Create a set.</summary>
        /// <param name="id">Stable id, referenced by other expressions and by layers.</param>
        /// <param name="name">Display name.</param>
        /// <param name="expression">What it matches.</param>
        public SavedSet(string id, string name, SetExpression expression)
        {
            Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("id is required", nameof(id)) : id;
            Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("name is required", nameof(name)) : name;
            Expression = expression ?? throw new ArgumentNullException(nameof(expression));
        }

        /// <summary>Stable id.</summary>
        public string Id { get; }

        /// <summary>Display name.</summary>
        public string Name { get; set; }

        /// <summary>What it matches.</summary>
        public SetExpression Expression { get; set; }

        /// <summary>Why it exists, in the author's words.</summary>
        public string? Note { get; set; }

        /// <summary>True when the set has been pushed to the host as a native search set.</summary>
        public bool IsPublished { get; set; }

        /// <summary>Serialise.</summary>
        public JsonValue ToJson()
        {
            var json = JsonValue.Object()
                .Set("id", JsonValue.String(Id))
                .Set("name", JsonValue.String(Name))
                .Set("expression", SetExpressionJson.Write(Expression));

            if (!string.IsNullOrEmpty(Note)) json.Set("note", JsonValue.String(Note));
            if (IsPublished) json.Set("published", JsonValue.String("yes"));
            return json;
        }

        /// <summary>Read one back, or null.</summary>
        /// <param name="json">The candidate.</param>
        public static SavedSet? FromJson(JsonValue? json)
        {
            if (json == null || json.Kind != JsonKind.Object) return null;

            var id = json["id"].AsString();
            var name = json["name"].AsString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;

            return new SavedSet(id!, name!, SetExpressionJson.Read(json["expression"]))
            {
                Note = json["note"].AsString(),
                IsPublished = json["published"].AsString() == "yes",
            };
        }

        /// <inheritdoc />
        public override string ToString() => Name + " — " + Expression.Describe();
    }

    /// <summary>
    /// The appearance stack, on disk.
    ///
    /// A layer built from a selection saves its element keys; a layer built from a rule saves the
    /// rule. That difference is the whole reason the Appearance Manager is worth having, so it is
    /// preserved exactly rather than flattened to a list of keys at save time — which is what would
    /// happen if the saved form were "the elements this layer covered when it was saved".
    /// </summary>
    public static class LayerStackJson
    {
        /// <summary>Serialise a stack, bottom layer first.</summary>
        /// <param name="layers">The layers.</param>
        public static JsonValue Write(IEnumerable<AppearanceLayer> layers)
        {
            if (layers == null) throw new ArgumentNullException(nameof(layers));
            return JsonValue.Array(layers.Select(WriteOne));
        }

        private static JsonValue WriteOne(AppearanceLayer layer)
        {
            var json = JsonValue.Object()
                .Set("id", JsonValue.String(layer.Id))
                .Set("name", JsonValue.String(layer.Name))
                .Set("enabled", JsonValue.String(layer.IsEnabled ? "yes" : "no"))
                .Set("targetText", JsonValue.String(layer.Target.Description));

            if (layer.Target.Expression != null)
                json.Set("expression", SetExpressionJson.Write(layer.Target.Expression));
            else
                json.Set("keys", JsonValue.Array(layer.Target.Keys.Select(k => JsonValue.String(k.ToString()))));

            if (layer.Note != null) json.Set("note", JsonValue.String(layer.Note));
            if (layer.Visible != null) json.Set("visible", JsonValue.String(layer.Visible.Value ? "yes" : "no"));
            if (layer.Colour != null) json.Set("colour", JsonValue.String(layer.Colour.Value.ToString()));

            if (layer.Transparency != null)
                json.Set("transparency", JsonValue.Number(layer.Transparency.Value));

            return json;
        }

        /// <summary>Read a stack back. Unreadable layers are skipped rather than failing the load.</summary>
        /// <param name="json">The saved array.</param>
        public static IReadOnlyList<AppearanceLayer> Read(JsonValue? json)
        {
            var layers = new List<AppearanceLayer>();
            if (json == null || json.Kind != JsonKind.Array) return layers;

            foreach (var item in json.Items)
            {
                var layer = ReadOne(item);
                if (layer != null) layers.Add(layer);
            }

            return layers;
        }

        private static AppearanceLayer? ReadOne(JsonValue json)
        {
            if (json.Kind != JsonKind.Object) return null;

            var id = json["id"].AsString();
            var name = json["name"].AsString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;

            var description = json["targetText"].AsString();

            LayerTarget target;

            if (json.Has("expression"))
            {
                target = LayerTarget.Set(SetExpressionJson.Read(json["expression"]), description);
            }
            else
            {
                var saved = json["keys"].Items;
                var keys = new List<ElementKey>();

                foreach (var item in saved)
                    if (ElementKey.TryParse(item.AsString(), out var key)) keys.Add(key);

                // A layer whose keys did not survive is a layer covering nothing, and the saved
                // description would still claim "2 elements" — which is worse than an empty layer,
                // because it looks like the layer is working.
                if (keys.Count < saved.Count)
                    description = keys.Count.ToString(CultureInfo.InvariantCulture) + " of "
                                  + saved.Count.ToString(CultureInfo.InvariantCulture)
                                  + " elements — the rest could not be read";

                target = LayerTarget.Elements(keys, description);
            }

            var layer = new AppearanceLayer(id!, name!, target)
            {
                Note = json["note"].AsString(),
                IsEnabled = json["enabled"].AsString() != "no",
            };

            var visible = json["visible"].AsString();
            if (visible == "yes") layer.Visible = true;
            else if (visible == "no") layer.Visible = false;

            if (Colour.TryParse(json["colour"].AsString(), out var colour)) layer.Colour = colour;

            if (json["transparency"].Kind == JsonKind.Number)
                layer.Transparency = json["transparency"].AsDouble();

            return layer;
        }
    }

    /// <summary>The sets the user has built, saved together.</summary>
    public sealed class SetLibrary
    {
        private readonly List<SavedSet> _sets = new List<SavedSet>();

        /// <summary>Every saved set, in the order they were made.</summary>
        public IReadOnlyList<SavedSet> Sets => _sets;

        /// <summary>Add or replace a set by id.</summary>
        /// <param name="set">The set.</param>
        public void Put(SavedSet set)
        {
            if (set == null) throw new ArgumentNullException(nameof(set));

            var at = _sets.FindIndex(s => string.Equals(s.Id, set.Id, StringComparison.Ordinal));
            if (at >= 0) _sets[at] = set; else _sets.Add(set);
        }

        /// <summary>Remove a set by id. Removing something absent is not an error.</summary>
        /// <param name="id">The set id.</param>
        public void Remove(string id) =>
            _sets.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal));

        /// <summary>A set by id, or null.</summary>
        /// <param name="id">The set id.</param>
        public SavedSet? Find(string? id) =>
            id == null ? null : _sets.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

        /// <summary>A free id that is not in use.</summary>
        /// <param name="stem">What to base it on.</param>
        public string NextId(string stem)
        {
            var root = string.IsNullOrWhiteSpace(stem) ? "set" : stem;

            for (var n = 1; ; n++)
            {
                var candidate = root + "-" + n.ToString(CultureInfo.InvariantCulture);
                if (Find(candidate) == null) return candidate;
            }
        }

        /// <summary>Serialise.</summary>
        public JsonValue ToJson() => JsonValue.Array(_sets.Select(s => s.ToJson()));

        /// <summary>Read a library back.</summary>
        /// <param name="json">The saved array.</param>
        public static SetLibrary FromJson(JsonValue? json)
        {
            var library = new SetLibrary();
            if (json == null || json.Kind != JsonKind.Array) return library;

            foreach (var item in json.Items)
            {
                var set = SavedSet.FromJson(item);
                if (set != null) library._sets.Add(set);
            }

            return library;
        }

        /// <inheritdoc />
        public override string ToString() => _sets.Count + " sets";
    }
}
