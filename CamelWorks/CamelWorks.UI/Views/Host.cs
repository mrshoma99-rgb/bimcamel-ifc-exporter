using System;
using System.IO;
using System.Reflection;
using Autodesk.Navisworks.Api;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Project;
using CamelWorks.Core.Store;
using CamelWorks.Nav;

namespace CamelWorks.UI.Views
{
    /// <summary>
    /// One document, with everything a screen needs hanging off it.
    ///
    /// Screens ask <see cref="Host.Current"/> for this and use what they need. Building it is
    /// cheap — the expensive part, the element index inside <see cref="NavDocument"/>, is built on
    /// first use and kept — so a screen never has to decide whether to cache anything itself.
    /// </summary>
    public sealed class Session
    {
        internal Session(Document document, NavDocument model, ProjectStore store, ProjectProfile profile,
                         IClashSource clash, string? clashProblem)
        {
            Document = document;
            Model = model;
            Store = store;
            Profile = profile;
            Clash = clash;
            ClashProblem = clashProblem;
            View = new NavViewSession(model);
            Viewpoints = new NavViewpointStore(model);
            Search = new NavSearch(model);
        }

        /// <summary>The host document.</summary>
        public Document Document { get; }

        /// <summary>The document behind the seam.</summary>
        public NavDocument Model { get; }

        /// <summary>Runs compiled set expressions and publishes native sets.</summary>
        public NavSearch Search { get; }

        /// <summary>Colour, transparency, visibility, camera and the section box.</summary>
        public IViewSession View { get; }

        /// <summary>Saved viewpoints.</summary>
        public IViewpointStore Viewpoints { get; }

        /// <summary>
        /// The clash engine, or a stand-in that reports itself unavailable.
        ///
        /// Never null. Navisworks Simulate has no clash engine at all, and a screen that has to
        /// null-check before every use would eventually forget once.
        /// </summary>
        public IClashSource Clash { get; }

        /// <summary>Why clash is unavailable, or null when it is available.</summary>
        public string? ClashProblem { get; }

        /// <summary>The project file.</summary>
        public ProjectStore Store { get; }

        /// <summary>What CamelWorks derived about this project, with any overrides applied.</summary>
        public ProjectProfile Profile { get; }

        /// <summary>
        /// How many metres one model unit is.
        ///
        /// Every threshold in this product is stated in metres — a 10 mm clash tolerance, a 5 m
        /// grouping distance — and the host reports geometry in whatever unit the document was
        /// authored in. Multiplying at the boundary is the only place this can be got right once;
        /// leaving it to each rule is how a millimetre model ends up grouping everything within
        /// five millimetres and reporting one group per clash.
        /// </summary>
        public double MetresPerUnit => Metres(Document.Units);

        /// <summary>The saved document path, or null when it has never been saved.</summary>
        public string? SavedPath =>
            string.IsNullOrWhiteSpace(Document.CurrentFileName) ? null : Document.CurrentFileName;

        /// <summary>Now, as UTC ticks.</summary>
        public long NowTicks => DateTime.UtcNow.Ticks;

        /// <summary>Record something in the activity log and save the project file.</summary>
        /// <param name="kind">One of <see cref="ActivityKind"/>.</param>
        /// <param name="summary">One line, in the past tense.</param>
        /// <param name="detail">Anything worth keeping that does not fit on the line.</param>
        public void Record(string kind, string summary, string? detail = null) =>
            Store.Record(kind, NowTicks, summary, detail);

        /// <summary>Metres per unit for a host unit.</summary>
        /// <param name="units">The document's unit.</param>
        public static double Metres(Units units)
        {
            switch (units)
            {
                case Units.Meters: return 1;
                case Units.Centimeters: return 0.01;
                case Units.Millimeters: return 0.001;
                case Units.Kilometers: return 1000;
                case Units.Micrometers: return 0.000001;
                case Units.Feet: return 0.3048;
                case Units.Inches: return 0.0254;
                case Units.Yards: return 0.9144;
                case Units.Miles: return 1609.344;
                case Units.Mils: return 0.0000254;
                case Units.Microinches: return 0.0000000254;
                default: return 1;
            }
        }

        /// <inheritdoc />
        public override string ToString() => Profile.ProjectName + " — " + Model.Models.Count + " models";
    }

    /// <summary>
    /// The way in to Navisworks, and the only place in the UI that talks to it directly.
    ///
    /// <b>Nothing here throws when there is no model.</b> <see cref="Current"/> is null, screens
    /// say so, and no screen is left deciding what an absent document means.
    /// </summary>
    public static class Host
    {
        private static Session? _session;
        private static string? _key;
        private static bool _resolverRegistered;

        /// <summary>What to say when there is no model open.</summary>
        public const string NoModel = "Open a model first. Every screen in CamelWorks works on a raw "
                                      + "model with nothing configured, but it does need one.";

        /// <summary>
        /// The current session, or null when nothing is open.
        ///
        /// Rebuilt when the document changes underneath — a different file, or models added or
        /// removed. Comparing a cheap key each time beats subscribing to host events, which fire on
        /// the host's thread at moments a dock pane is not necessarily ready for.
        /// </summary>
        public static Session? Current
        {
            get
            {
                Ensure();

                var document = Autodesk.Navisworks.Api.Application.ActiveDocument;

                if (document == null || document.IsClear)
                {
                    _session = null;
                    _key = null;
                    return null;
                }

                var key = document.CurrentFileName + "|" + document.Models.Count + "|" + document.FileName;

                if (_session != null && string.Equals(_key, key, StringComparison.Ordinal)) return _session;

                var model = new NavDocument(document);
                var saved = SavedPathOf(document);

                var store = ProjectStore.Open(PhysicalFileSystem.Instance, saved, UserDirectory);

                var profile = ProjectProfile.Derive(model.Models, saved, Environment.UserName);
                profile.ApplyOverrides(store.Section(ProjectStore.ProfileSection));

                var clash = LoadClash(model, out var problem);

                _session = new Session(document, model, store, profile, clash, problem);
                _key = key;
                return _session;
            }
        }

        /// <summary>Drop the cached session, so the next access rebuilds it.</summary>
        public static void Forget()
        {
            _session = null;
            _key = null;
        }

        /// <summary>Where CamelWorks keeps things for a document that has never been saved.</summary>
        public static string UserDirectory =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CamelWorks");

        /// <summary>
        /// Make side-by-side DLLs loadable.
        ///
        /// The CLR probes the application's folder, which for a plug-in is the Navisworks
        /// installation — not the folder the plug-in was installed into. Without this,
        /// CamelWorks.Core.dll sitting right beside CamelWorks.UI.dll is invisible.
        /// </summary>
        public static void Ensure()
        {
            if (_resolverRegistered) return;
            _resolverRegistered = true;

            var folder = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(folder)) return;

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var file = System.IO.Path.Combine(folder!, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
        }

        private static string? SavedPathOf(Document document) =>
            string.IsNullOrWhiteSpace(document.CurrentFileName) ? null : document.CurrentFileName;

        /// <summary>
        /// Load the clash adapter without taking a hard dependency on it.
        ///
        /// <c>Autodesk.Navisworks.Clash.dll</c> ships with Manage and not with Simulate, so
        /// CamelWorks.Nav.Clash cannot be referenced from here: a type from a missing assembly is
        /// fine until the JIT touches the method that uses it, at which point it is a
        /// FileNotFoundException naming a DLL the user has never heard of, in the middle of
        /// whatever they were doing. Loading it by name means a Simulate user simply gets a screen
        /// that says the clash engine is not in their edition.
        /// </summary>
        private static IClashSource LoadClash(NavDocument model, out string? problem)
        {
            problem = null;

            try
            {
                var assembly = Assembly.Load("CamelWorks.Nav.Clash");
                var type = assembly.GetType("CamelWorks.Nav.Clash.NavClashSource", throwOnError: false);

                if (type == null)
                {
                    problem = "The CamelWorks clash adapter is installed but does not contain the expected "
                              + "type. This build is inconsistent; reinstall it.";
                    return new NoClash();
                }

                if (Activator.CreateInstance(type, model) is IClashSource source && source.IsAvailable)
                    return source;

                problem = "This document has no clash tests, or this Navisworks edition has no clash engine.";
                return new NoClash();
            }
            catch (Exception e) when (e is FileNotFoundException || e is FileLoadException
                                      || e is BadImageFormatException || e is TypeLoadException
                                      || e is TargetInvocationException || e is MissingMethodException)
            {
                problem = "Clash Detective is not available in this Navisworks edition. Clash tests, the "
                          + "coordination board and BCF export from clashes all need Navisworks Manage; "
                          + "everything else in CamelWorks works on Simulate.";

                return new NoClash();
            }
        }

        /// <summary>Stands in for the clash engine when there is not one. Answers, rather than throwing.</summary>
        private sealed class NoClash : IClashSource
        {
            public bool IsAvailable => false;

            public System.Collections.Generic.IReadOnlyList<ClashTestInfo> Tests() =>
                Array.Empty<ClashTestInfo>();

            public System.Collections.Generic.IReadOnlyList<ClashResultInfo> Results(string testId) =>
                Array.Empty<ClashResultInfo>();
        }
    }
}
