using System;
using System.Collections.Generic;
using System.Linq;

namespace CamelWorks.UI.Shell
{
    /// <summary>What a ribbon button does when it is pressed.</summary>
    public enum CommandKind
    {
        /// <summary>Opens the pane on a named workspace and tab.</summary>
        Pane = 0,

        /// <summary>Acts immediately, with no pane and no dialog.</summary>
        Act = 1,

        /// <summary>Opens a menu of choices under the button.</summary>
        Menu = 2,

        /// <summary>
        /// Opens a true modal.
        ///
        /// Reserved for terminal file operations that need a save path and nothing else. Anything
        /// that changes what the user sees is modeless with a live preview instead — a modal over
        /// a 3D view hides the very thing the user is deciding about.
        /// </summary>
        Dialog = 3,
    }

    /// <summary>One ribbon command.</summary>
    public sealed class RibbonCommand
    {
        internal RibbonCommand(string id, string title, CommandKind kind, string? route,
                               string tooltip, string[] synonyms, string[] symptoms)
        {
            Id = id; Title = title; Kind = kind; Route = route;
            Tooltip = tooltip; Synonyms = synonyms; Symptoms = symptoms;
        }

        /// <summary>The host command id, matching the ribbon XAML.</summary>
        public string Id { get; }

        /// <summary>How it reads on the button.</summary>
        public string Title { get; }

        /// <summary>What pressing it does.</summary>
        public CommandKind Kind { get; }

        /// <summary>The "workspace/tab" it opens, for a pane command.</summary>
        public string? Route { get; }

        /// <summary>The tooltip.</summary>
        public string Tooltip { get; }

        /// <summary>Other words for the same thing, so search finds it under the name the user knows.</summary>
        public IReadOnlyList<string> Synonyms { get; }

        /// <summary>
        /// The problem this solves, in the words somebody would use while having it.
        ///
        /// The reason Find a Tool is worth building. Nobody looking for the appearance manager
        /// types "appearance manager" — they type "why is this thing hidden", and a search that
        /// only matches command names answers nothing.
        /// </summary>
        public IReadOnlyList<string> Symptoms { get; }

        /// <inheritdoc />
        public override string ToString() => Title;
    }

    /// <summary>
    /// Every command in the product, as data.
    ///
    /// One list, because the ribbon handler, the Find a Tool search and the keyboard shortcut map
    /// all need the same set and would otherwise each carry their own copy. A command missing from
    /// one of three hand-maintained lists is invisible in exactly one place, which is the hardest
    /// kind of gap to notice.
    /// </summary>
    public static class CommandCatalogue
    {
        /// <summary>All twenty-five, in ribbon order.</summary>
        public static IReadOnlyList<RibbonCommand> All { get; } = new[]
        {
            // Panel 1 — Project
            Pane("ID_CW_HealthCheck", "Health Check", "Project/Health",
                "One scorecard over models, sets and data. Works on a raw model with nothing set up.",
                new[] { "audit", "model check", "quality", "scorecard", "validate" },
                new[] { "is this model any good", "something is wrong with the federation",
                        "elements in the wrong place", "model loaded twice", "duplicate elements" }),

            Pane("ID_CW_ProjectSetup", "Project Setup", "Project/Setup",
                "What CamelWorks already derived, with an override on each line. Skippable.",
                new[] { "configure", "settings", "options", "preferences" },
                new[] { "where do I set this up", "do I have to configure it first" }),

            Dialog("ID_CW_FixLinks", "Fix Broken Links",
                "Repoint broken NWF paths, rename models, find missing sources.",
                new[] { "missing model", "relink", "repath", "broken reference" },
                new[] { "model did not load", "file not found", "the NWF points at the wrong folder" }),

            Menu("ID_CW_ProjectProfile", "Project Profile",
                "Save, load, or start from a template pack.",
                new[] { "template", "profile", "preset" },
                new[] { "set this up the same way on the next job" }),

            // Panel 2 — Coordinate
            Pane("ID_CW_Triage", "Clash Triage", "Coordinate/Triage",
                "The board. Opens populated and grouped, with the funnel showing how the engine's "
                + "count became this one.",
                new[] { "clashes", "board", "issues", "results", "grouping" },
                new[] { "too many clashes", "thousands of results", "the same clash forty times",
                        "one pipe through forty joists", "where do I even start" }),

            Act("ID_CW_Review", "Review",
                "Review mode over the board's current filter and scope.",
                new[] { "walk the clashes", "go through results", "sign off" },
                new[] { "I need to go through these one at a time" }),

            Pane("ID_CW_ClashTests", "Clash Tests", "Coordinate/Tests",
                "The matrix builder and Run. One click is every model against every other.",
                new[] { "matrix", "run clashes", "test setup", "batch clash" },
                new[] { "setting up clash tests takes all morning", "which tests have I already run" }),

            Pane("ID_CW_ClashRules", "Clash Rules", "Coordinate/Rules",
                "Suppress and flag, then group, then assign — one pipeline, previewed before it runs.",
                new[] { "filters", "ignore", "suppress", "auto-assign", "grouping rules" },
                new[] { "the same false positives every week", "insulation touching a wall",
                        "I keep ignoring the same things" }),

            Pane("ID_CW_Headroom", "Headroom", "Coordinate/Triage",
                "Floor set against target set, emitting findings onto the board.",
                new[] { "clearance", "ceiling height", "soffit", "head height" },
                new[] { "not enough headroom", "the ceiling is too low under that duct" }),

            Pane("ID_CW_ClashReport", "Clash Report", "Coordinate/Report",
                "PDF, XLSX or HTML from a built-in template. Nothing to configure first.",
                new[] { "export report", "issue list", "pdf", "spreadsheet" },
                new[] { "the client wants a report", "I need this in a spreadsheet" }),

            Pane("ID_CW_Bcf", "BCF", "Coordinate/BCF",
                "Export and import, BCF 2.1 and 3.0.",
                new[] { "bcf", "openbim", "issue exchange", "solibri", "bimcollab" },
                new[] { "send these to the architect", "they use a different tool" }),

            Pane("ID_CW_Merge", "Merge", "Coordinate/BCF",
                "One conflict preview for inbound BCF and inbound reviewer files.",
                new[] { "combine", "reconcile", "incoming", "conflicts" },
                new[] { "two people reviewed the same clashes", "whose version wins" }),

            // Panel 3 — Data
            Pane("ID_CW_DataManager", "Data Manager", "Data/Data",
                "Browse and edit properties in one place, with calculated columns.",
                new[] { "properties", "parameters", "attributes", "edit data" },
                new[] { "I need to see every property at once", "bulk edit properties" }),

            Menu("ID_CW_Excel", "Excel",
                "Properties out, properties back in with a preview diff, or import a sheet keyed to elements.",
                new[] { "spreadsheet", "xlsx", "csv", "round trip" },
                new[] { "get this into Excel", "the QS sent a spreadsheet back" }),

            Pane("ID_CW_Zones", "Assign Levels & Zones", "Data/Zones",
                "Derives levels from whatever the model has, and says which source it used.",
                new[] { "storeys", "floors", "levels", "zoning", "grids" },
                new[] { "the model has no levels", "clashes are not grouped by floor",
                        "which floor is this on" }),

            Pane("ID_CW_Takeoff", "Takeoff", "Data/Takeoff",
                "Sums the numeric properties it finds, grouped, and reports what it could not read.",
                new[] { "quantities", "measure", "counts", "schedule", "qto" },
                new[] { "how much of this is there", "I need quantities out of the model" }),

            // Panel 4 — Sets & Views
            Pane("ID_CW_Sets", "Sets", "Sets/Sets",
                "Boolean set building, compiled down to native search sets.",
                new[] { "search sets", "selection sets", "filters", "find items" },
                new[] { "the search only does AND", "I need everything except this",
                        "building the same set over and over" }),

            Pane("ID_CW_Appearance", "Appearance", "Sets/Appearance",
                "The layers system: what is hidden, what is overridden, and by which layer.",
                new[] { "colours", "override", "hide", "isolate", "transparency", "layers" },
                new[] { "why is this thing hidden", "why is this pink", "unhide all lost my work",
                        "I cannot tell what is overridden" }),

            Pane("ID_CW_Viewpoints", "Viewpoints", "Sets/Viewpoints",
                "Bulk rename, renumber and re-folder; batch render; copy overrides.",
                new[] { "views", "saved viewpoints", "renumber", "rename" },
                new[] { "two hundred viewpoints named Viewpoint", "renaming these one at a time" }),

            Menu("ID_CW_SectionBox", "Section Box",
                "Box the selection, a clash or a group; clear it; or toggle it without losing it.",
                new[] { "section", "clip", "cut", "crop" },
                new[] { "I cannot see inside", "turning sectioning off loses my box" }),

            // Panel 5 — Deliver & Automate
            Pane("ID_CW_Batch", "Batch", "Batch/Jobs",
                "Jobs and their run history. A job from this document in one click.",
                new[] { "automation", "scheduled", "overnight", "unattended" },
                new[] { "doing the same export every week", "this takes an hour every Friday" }),

            Pane("ID_CW_Graph", "Graph Editor", "Automate/Canvas",
                "The graph canvas, in its own pane because it needs the full width.",
                new[] { "nodes", "visual scripting", "dyncamelo", "workflow" },
                new[] { "I want to chain these together" }),

            Pane("ID_CW_ExportIfc", "Export IFC", "Project/Export",
                "The IFC exporter, full pane.",
                new[] { "ifc", "openbim export", "ifc4", "ifc2x3" },
                new[] { "they want IFC", "export to IFC" }),

            Menu("ID_CW_Export", "Export",
                "glTF, GLB, CSV or XLSX.",
                new[] { "gltf", "glb", "csv", "xlsx", "web viewer" },
                new[] { "put this on the web", "send them the geometry" }),

            Menu("ID_CW_Help", "Help",
                "Guide, limitations, shortcuts, Find a Tool, sample project, diagnostics, updates, about.",
                new[] { "documentation", "support", "diagnostics", "about", "version" },
                new[] { "how do I do this", "something is broken", "what version am I on" }),
        };

        /// <summary>Look one up by its host command id.</summary>
        public static RibbonCommand? Find(string? commandId) =>
            All.FirstOrDefault(c => string.Equals(c.Id, commandId, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Find a Tool: match a query against names, synonyms and symptom phrasing.
        ///
        /// Symptoms carry the most weight after an exact name match, because they are what people
        /// actually type. A search that only matched command names would answer "why is this thing
        /// hidden" with nothing at all, which is precisely when somebody needs it.
        /// </summary>
        public static IReadOnlyList<RibbonCommand> Search(string? query)
        {
            if (string.IsNullOrWhiteSpace(query)) return All;

            var needle = query!.Trim();

            return All
                .Select(c => new { Command = c, Score = Score(c, needle) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Command.Title, StringComparer.Ordinal)
                .Select(x => x.Command)
                .ToList();
        }

        private static int Score(RibbonCommand command, string needle)
        {
            if (Has(command.Title, needle)) return command.Title.Length == needle.Length ? 100 : 80;
            if (command.Symptoms.Any(s => Has(s, needle))) return 60;
            if (command.Synonyms.Any(s => Has(s, needle))) return 50;
            if (Has(command.Tooltip, needle)) return 20;

            // Every word matching somewhere is still a match — "hidden elements" should find the
            // appearance manager even though neither word alone is one of its synonyms.
            var words = needle.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1 && words.All(w => Score(command, w) > 0)) return 10;

            return 0;
        }

        private static bool Has(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static RibbonCommand Pane(string id, string title, string route, string tooltip,
                                          string[] synonyms, string[] symptoms) =>
            new RibbonCommand(id, title, CommandKind.Pane, route, tooltip, synonyms, symptoms);

        private static RibbonCommand Act(string id, string title, string tooltip,
                                         string[] synonyms, string[] symptoms) =>
            new RibbonCommand(id, title, CommandKind.Act, null, tooltip, synonyms, symptoms);

        private static RibbonCommand Menu(string id, string title, string tooltip,
                                          string[] synonyms, string[] symptoms) =>
            new RibbonCommand(id, title, CommandKind.Menu, null, tooltip, synonyms, symptoms);

        private static RibbonCommand Dialog(string id, string title, string tooltip,
                                            string[] synonyms, string[] symptoms) =>
            new RibbonCommand(id, title, CommandKind.Dialog, null, tooltip, synonyms, symptoms);
    }
}
