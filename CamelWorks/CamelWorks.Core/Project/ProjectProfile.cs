using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Store;

namespace CamelWorks.Core.Project
{
    /// <summary>
    /// One line on the Setup screen: what CamelWorks worked out, why, and what the user said instead.
    ///
    /// The shape is the zero-setup rule made concrete. Every setting in the product has a derived
    /// value before anybody opens Setup, so nothing has to be configured for a feature to work; the
    /// override exists for the cases where the derivation is wrong, and until it is used the screen
    /// is a report rather than a form.
    /// </summary>
    public sealed class ProfileSetting
    {
        /// <summary>Create a setting.</summary>
        /// <param name="key">Stable key. What the saved file and the typed accessors use.</param>
        /// <param name="title">How it reads on the Setup screen.</param>
        /// <param name="derived">What CamelWorks worked out, or null when it could work nothing out.</param>
        /// <param name="because">Why it says that — shown next to the value, never hidden behind a tooltip.</param>
        /// <param name="choices">The values worth offering, when the setting is a choice rather than free text.</param>
        public ProfileSetting(string key, string title, string? derived, string because,
                              IReadOnlyList<string>? choices = null)
        {
            Key = string.IsNullOrWhiteSpace(key) ? throw new ArgumentException("key is required", nameof(key)) : key;
            Title = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("title is required", nameof(title)) : title;
            Derived = derived;
            Because = because ?? string.Empty;
            Choices = choices ?? Array.Empty<string>();
        }

        /// <summary>Stable key.</summary>
        public string Key { get; }

        /// <summary>Display title.</summary>
        public string Title { get; }

        /// <summary>What CamelWorks derived, before any override.</summary>
        public string? Derived { get; }

        /// <summary>Why the derived value says what it says.</summary>
        public string Because { get; }

        /// <summary>Values worth offering. Empty means free text.</summary>
        public IReadOnlyList<string> Choices { get; }

        /// <summary>What the user said instead, or null.</summary>
        public string? Override { get; set; }

        /// <summary>The value in force.</summary>
        public string? Value => string.IsNullOrWhiteSpace(Override) ? Derived : Override;

        /// <summary>True when the user's value differs from the derived one.</summary>
        public bool IsOverridden =>
            !string.IsNullOrWhiteSpace(Override) && !string.Equals(Override, Derived, StringComparison.Ordinal);

        /// <summary>Where the value in force came from, as one word for the Setup screen.</summary>
        public string Source => IsOverridden ? "you" : "derived";

        /// <inheritdoc />
        public override string ToString() => Title + ": " + (Value ?? "(none)") + " (" + Source + ")";
    }

    /// <summary>The keys <see cref="ProjectProfile"/> uses. Constants because three screens read them.</summary>
    public static class ProfileKeys
    {
        /// <summary>Project name, used on every report and export.</summary>
        public const string ProjectName = "project.name";

        /// <summary>Who the reports say produced them.</summary>
        public const string Author = "project.author";

        /// <summary>Clash tolerance in metres, used when CamelWorks proposes a test.</summary>
        public const string ClashTolerance = "clash.tolerance";

        /// <summary>Proximity band in metres for grouping clashes that are really one problem.</summary>
        public const string ClashProximity = "clash.proximity";

        /// <summary>The parties a clash can be assigned to, comma separated.</summary>
        public const string Parties = "coordinate.parties";

        /// <summary>Where levels come from: the model's own, or inferred from geometry.</summary>
        public const string LevelSource = "data.levels";

        /// <summary>Prefix for a per-model discipline setting. The rest of the key is the model scope.</summary>
        public const string DisciplinePrefix = "discipline.";
    }

    /// <summary>
    /// The disciplines CamelWorks recognises, and how it guesses one from a file name.
    ///
    /// Guessing rather than asking is the point. Every federation on earth names its models after
    /// their discipline in one of about six conventions, so a product that makes the user type it in
    /// once per model is charging them for something it could have read.
    /// </summary>
    public static class Disciplines
    {
        // Strength, then length, decides. A whole discipline word beats an abbreviation beats a
        // letter — which is why "Site_HVAC_Riser" is mechanical and not civil, even though "Site"
        // comes first and both words are four letters long.
        private const int Word = 3;
        private const int Abbreviation = 2;
        private const int Weak = 1;

        private static readonly (string Discipline, (string Token, int Strength)[] Tokens)[] Table =
        {
            ("Architecture", new[]
            {
                ("ARCHITECTURAL", Word), ("ARCHITECTURE", Word),
                ("ARCH", Abbreviation), ("ARC", Abbreviation),
                ("AR", Weak), ("A", Weak),
            }),
            ("Structure", new[]
            {
                ("STRUCTURAL", Word), ("STRUCTURE", Word),
                ("STRUCT", Abbreviation), ("STR", Abbreviation),
                ("ST", Weak), ("S", Weak),
            }),
            ("Mechanical", new[]
            {
                ("MECHANICAL", Word), ("HVAC", Word),
                ("MECH", Abbreviation), ("MEC", Abbreviation),
                ("ME", Weak), ("M", Weak),
            }),
            ("Electrical", new[]
            {
                ("ELECTRICAL", Word),
                ("ELEC", Abbreviation), ("ELE", Abbreviation),
                ("EL", Weak), ("E", Weak),
            }),
            ("Plumbing", new[]
            {
                ("PLUMBING", Word), ("SANITARY", Word), ("DRAINAGE", Word),
                ("PLUMB", Abbreviation), ("PLB", Abbreviation), ("SAN", Abbreviation),
                ("PL", Weak), ("P", Weak),
            }),
            ("Fire", new[]
            {
                ("FIREFIGHTING", Word), ("SPRINKLER", Word), ("FIRE", Word),
                ("FP", Abbreviation), ("FF", Abbreviation),
            }),
            ("Civil", new[]
            {
                ("INFRASTRUCTURE", Word), ("CIVIL", Word),
                ("CIV", Abbreviation),
                ("SITE", Weak), ("CI", Weak), ("C", Weak),
            }),
            ("Process", new[]
            {
                ("PROCESS", Word), ("PIPING", Word),
                ("PIPE", Abbreviation), ("PIP", Abbreviation),
                ("PI", Weak),
            }),
            ("Landscape", new[]
            {
                ("LANDSCAPE", Word),
                ("LAN", Abbreviation),
                ("LAND", Weak),
            }),
            ("Survey", new[]
            {
                ("TOPOGRAPHY", Word), ("POINTCLOUD", Word), ("SURVEY", Word),
                ("SCAN", Abbreviation), ("TOPO", Abbreviation),
            }),
        };

        /// <summary>Every discipline this product knows, in the order they read in a matrix.</summary>
        public static IReadOnlyList<string> All { get; } = Table.Select(t => t.Discipline).ToList();

        /// <summary>
        /// The discipline a model name suggests, or null.
        ///
        /// Longer evidence wins, so "STRUCTURE" beats a stray "S" elsewhere in the name. A
        /// single-letter token only counts as the first token, which is the convention every
        /// single-letter naming scheme actually uses — otherwise "Tower A" would be architecture
        /// and "Building E" electrical.
        /// </summary>
        /// <param name="name">A file name, display name or path fragment.</param>
        public static string? Guess(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var stem = name!;

            var slash = stem.LastIndexOfAny(Separators);
            if (slash >= 0) stem = stem.Substring(slash + 1);

            var dot = stem.LastIndexOf('.');
            if (dot > 0) stem = stem.Substring(0, dot);

            var tokens = stem.Split(new[] { ' ', '-', '_', '.', '(', ')', '[', ']', '+', '#' },
                                    StringSplitOptions.RemoveEmptyEntries);

            string? best = null;
            var bestScore = 0;

            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i].ToUpperInvariant();

                foreach (var (discipline, candidates) in Table)
                {
                    foreach (var (candidate, strength) in candidates)
                    {
                        // A single letter only counts as the first token, which is the convention
                        // every single-letter naming scheme actually uses — otherwise "Tower A"
                        // would be architecture and "Building E" electrical.
                        if (candidate.Length == 1 && i != 0) continue;
                        if (!string.Equals(token, candidate, StringComparison.Ordinal)) continue;

                        var score = strength * 100 + candidate.Length;
                        if (score <= bestScore) continue;

                        best = discipline;
                        bestScore = score;
                    }
                }
            }

            if (best != null) return best;

            // Nothing tokenised cleanly. Fall back to the longest real word found anywhere in the
            // name, which catches "TowerAArchitecturalModel" and the like. Abbreviations are not
            // looked for this way: "ARC" appears inside "SEARCH".
            var upper = stem.ToUpperInvariant();

            foreach (var (discipline, candidates) in Table)
                foreach (var (candidate, strength) in candidates)
                    if (strength == Word && candidate.Length > bestScore
                        && upper.IndexOf(candidate, StringComparison.Ordinal) >= 0)
                    {
                        best = discipline;
                        bestScore = candidate.Length;
                    }

            return best;
        }

        private static readonly char[] Separators = { '/', '\\' };
    }

    /// <summary>
    /// Everything the product would otherwise have made the user configure, derived from the model
    /// and overridable one line at a time.
    ///
    /// <b>A profile always exists.</b> <see cref="Derive"/> runs against whatever is open, including
    /// an empty document, and returns a complete profile — so no feature anywhere has to handle the
    /// case of "not set up yet", and none of them has a reason to send the user to Setup first.
    /// </summary>
    public sealed class ProjectProfile
    {
        private readonly List<ProfileSetting> _settings;

        private ProjectProfile(List<ProfileSetting> settings) => _settings = settings;

        /// <summary>Every setting, in the order the Setup screen shows them.</summary>
        public IReadOnlyList<ProfileSetting> Settings => _settings;

        /// <summary>A setting by key, or null.</summary>
        /// <param name="key">The key, from <see cref="ProfileKeys"/>.</param>
        public ProfileSetting? Find(string key) =>
            _settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

        /// <summary>The value in force for a key, or null.</summary>
        /// <param name="key">The key, from <see cref="ProfileKeys"/>.</param>
        public string? Value(string key) => Find(key)?.Value;

        /// <summary>Project name. Never empty.</summary>
        public string ProjectName => Value(ProfileKeys.ProjectName) ?? "Untitled project";

        /// <summary>Who reports say produced them. Never empty.</summary>
        public string Author => Value(ProfileKeys.Author) ?? "CamelWorks";

        /// <summary>Clash tolerance in metres.</summary>
        public double ClashTolerance => Number(ProfileKeys.ClashTolerance, 0.010);

        /// <summary>Proximity band in metres for grouping.</summary>
        public double ClashProximity => Number(ProfileKeys.ClashProximity, 5.0);

        /// <summary>The parties a clash can be assigned to.</summary>
        public IReadOnlyList<string> Parties =>
            (Value(ProfileKeys.Parties) ?? string.Empty)
                .Split(',')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>The discipline in force for a model scope, or null when none was worked out.</summary>
        /// <param name="modelScope">The scope, as <c>IModelSource.Scope</c> reports it.</param>
        public string? DisciplineOf(string? modelScope) =>
            modelScope == null ? null : Value(ProfileKeys.DisciplinePrefix + modelScope);

        private double Number(string key, double fallback) =>
            double.TryParse(Value(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        /// <summary>
        /// Work a profile out from what is open.
        /// </summary>
        /// <param name="models">The loaded models. An empty list is fine and still yields a profile.</param>
        /// <param name="documentPath">The saved document's path, when it has one.</param>
        /// <param name="user">Who is working, for the author line.</param>
        public static ProjectProfile Derive(IReadOnlyList<IModelSource>? models, string? documentPath = null,
                                            string? user = null)
        {
            var sources = models ?? Array.Empty<IModelSource>();

            var settings = new List<ProfileSetting>();

            var (name, nameBecause) = DeriveName(sources, documentPath);
            settings.Add(new ProfileSetting(ProfileKeys.ProjectName, "Project name", name, nameBecause));

            settings.Add(new ProfileSetting(ProfileKeys.Author, "Author",
                string.IsNullOrWhiteSpace(user) ? "CamelWorks" : user,
                string.IsNullOrWhiteSpace(user) ? "no signed-in user to read" : "the signed-in Windows user"));

            settings.Add(new ProfileSetting(ProfileKeys.ClashTolerance, "Clash tolerance (m)", "0.010",
                "10 mm — the value most coordination specifications settle on when nobody states one"));

            settings.Add(new ProfileSetting(ProfileKeys.ClashProximity, "Grouping distance (m)", "5",
                "clashes within 5 m of each other are usually one problem seen three times"));

            var disciplines = new List<string>();

            foreach (var model in sources)
            {
                var guess = Disciplines.Guess(model.DisplayName) ?? Disciplines.Guess(model.SourcePath);

                settings.Add(new ProfileSetting(
                    ProfileKeys.DisciplinePrefix + model.Scope,
                    model.DisplayName,
                    guess,
                    guess == null
                        ? "the file name says nothing a discipline can be read from"
                        : "read from the file name",
                    Disciplines.All));

                if (guess != null && !disciplines.Contains(guess)) disciplines.Add(guess);
            }

            settings.Add(new ProfileSetting(ProfileKeys.Parties, "Parties",
                disciplines.Count > 0 ? string.Join(", ", disciplines) : null,
                disciplines.Count > 0
                    ? "one per discipline found in the loaded models"
                    : "no disciplines could be read from the model names"));

            settings.Add(new ProfileSetting(ProfileKeys.LevelSource, "Levels from", "Model, then geometry",
                "the model's own level property where it has one, and a height histogram where it does not",
                new[] { "Model, then geometry", "Model only", "Geometry only" }));

            return new ProjectProfile(settings);
        }

        private static (string? Name, string Because) DeriveName(IReadOnlyList<IModelSource> models, string? documentPath)
        {
            if (!string.IsNullOrWhiteSpace(documentPath))
                return (Stem(documentPath!), "the saved document's file name");

            if (models.Count > 0)
            {
                var folder = FolderOf(models[0].SourcePath);
                if (folder != null) return (folder, "the folder the first model was loaded from");

                return (models[0].DisplayName, "the first model's name");
            }

            return (null, "nothing is open to read a name from");
        }

        private static string Stem(string path)
        {
            var name = path;

            var slash = name.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) name = name.Substring(slash + 1);

            var dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        private static string? FolderOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var parts = path!.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[parts.Length - 2] : null;
        }

        /// <summary>The overrides only, for saving. Derived values are never written — they are re-derived.</summary>
        public JsonValue OverridesToJson()
        {
            var json = JsonValue.Object();

            foreach (var setting in _settings.Where(s => s.IsOverridden))
                json.Set(setting.Key, JsonValue.String(setting.Override));

            return json;
        }

        /// <summary>
        /// Put saved overrides back on a freshly derived profile.
        ///
        /// Overrides for settings this build no longer derives are kept in <paramref name="unknown"/>
        /// rather than dropped, so a model unloaded today does not lose its discipline tomorrow.
        /// </summary>
        /// <param name="json">The saved overrides.</param>
        /// <param name="unknown">Receives overrides with no matching setting.</param>
        public void ApplyOverrides(JsonValue? json, IDictionary<string, string>? unknown = null)
        {
            if (json == null || json.Kind != JsonKind.Object) return;

            foreach (var key in json.Keys)
            {
                var value = json[key].AsString();
                if (value == null) continue;

                var setting = Find(key);

                if (setting != null) setting.Override = value;
                else if (unknown != null) unknown[key] = value;
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            ProjectName + " — " + _settings.Count(s => s.IsOverridden) + " of " + _settings.Count + " overridden";
    }
}
