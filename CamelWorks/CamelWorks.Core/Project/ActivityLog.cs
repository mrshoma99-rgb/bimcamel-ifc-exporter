using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Store;

namespace CamelWorks.Core.Project
{
    /// <summary>The kinds of thing worth remembering happened. Constants, because Home groups by them.</summary>
    public static class ActivityKind
    {
        /// <summary>A model revision was brought in, or the federation was refreshed.</summary>
        public const string Reconcile = "reconcile";

        /// <summary>The clash pipeline was run.</summary>
        public const string Regroup = "regroup";

        /// <summary>A report was written.</summary>
        public const string Report = "report";

        /// <summary>BCF or another exchange format went in or out.</summary>
        public const string Exchange = "exchange";

        /// <summary>Properties were written to the model.</summary>
        public const string Write = "write";

        /// <summary>The appearance stack was applied.</summary>
        public const string Appearance = "appearance";

        /// <summary>Geometry or data was exported.</summary>
        public const string Export = "export";

        /// <summary>A batch job ran.</summary>
        public const string Job = "job";

        /// <summary>The order Home lists them in: the weekly cycle first, everything else after.</summary>
        public static IReadOnlyList<string> Cycle { get; } = new[] { Reconcile, Regroup, Report };

        /// <summary>How a kind reads in a sentence.</summary>
        /// <param name="kind">One of the constants on this class.</param>
        public static string Title(string? kind) => kind switch
        {
            Reconcile => "Reconcile",
            Regroup => "Regroup",
            Report => "Report",
            Exchange => "Exchange",
            Write => "Write",
            Appearance => "Appearance",
            Export => "Export",
            Job => "Job",
            _ => kind ?? "Activity",
        };

        /// <summary>What the kind is for, shown on Home under its title before it has ever run.</summary>
        /// <param name="kind">One of the constants on this class.</param>
        public static string Purpose(string? kind) => kind switch
        {
            Reconcile => "Bring in the week's model revisions and see what moved.",
            Regroup => "Run the rules over the clash results so the board is a work list, not a dump.",
            Report => "Produce the thing that actually leaves the office.",
            _ => string.Empty,
        };
    }

    /// <summary>One thing that happened.</summary>
    public sealed class Activity
    {
        /// <summary>Create an entry.</summary>
        /// <param name="kind">One of <see cref="ActivityKind"/>.</param>
        /// <param name="whenTicks">When, as UTC ticks. Passed in rather than read, so this is testable.</param>
        /// <param name="summary">One line, in the past tense.</param>
        /// <param name="detail">Anything worth keeping that does not fit on the line.</param>
        public Activity(string kind, long whenTicks, string summary, string? detail = null)
        {
            Kind = string.IsNullOrWhiteSpace(kind) ? throw new ArgumentException("kind is required", nameof(kind)) : kind;
            WhenTicks = whenTicks;
            Summary = summary ?? string.Empty;
            Detail = detail;
        }

        /// <summary>What kind of thing it was.</summary>
        public string Kind { get; }

        /// <summary>When, as UTC ticks.</summary>
        public long WhenTicks { get; }

        /// <summary>One line, in the past tense.</summary>
        public string Summary { get; }

        /// <summary>Anything worth keeping that did not fit on the line.</summary>
        public string? Detail { get; }

        /// <summary>Serialise.</summary>
        public JsonValue ToJson()
        {
            var json = JsonValue.Object()
                .Set("kind", JsonValue.String(Kind))
                .Set("at", JsonValue.Number(WhenTicks))
                .Set("summary", JsonValue.String(Summary));

            if (!string.IsNullOrEmpty(Detail)) json.Set("detail", JsonValue.String(Detail));
            return json;
        }

        /// <summary>Read one back. Returns false for anything that is not an entry.</summary>
        /// <param name="json">The candidate.</param>
        /// <param name="activity">The entry, when this returns true.</param>
        public static bool TryFromJson(JsonValue? json, out Activity? activity)
        {
            activity = null;
            if (json == null || json.Kind != JsonKind.Object) return false;

            var kind = json["kind"].AsString();
            if (string.IsNullOrWhiteSpace(kind)) return false;

            activity = new Activity(kind!, json["at"].AsLong(), json["summary"].AsString() ?? string.Empty,
                                    json["detail"].AsString());
            return true;
        }

        /// <inheritdoc />
        public override string ToString() => ActivityKind.Title(Kind) + ": " + Summary;
    }

    /// <summary>
    /// What has happened on this project, newest first.
    ///
    /// Home is built on this. The weekly cycle — reconcile, regroup, report — only reads as a cycle
    /// if the product can say when each of the three last happened; a front door that shows the
    /// same three buttons whatever the state of the project is a menu, not a front door.
    /// </summary>
    public sealed class ActivityLog
    {
        /// <summary>How many entries are kept. Old ones fall off the end rather than growing the file forever.</summary>
        public const int Keep = 300;

        private readonly List<Activity> _entries = new List<Activity>();

        /// <summary>Every entry, newest first.</summary>
        public IReadOnlyList<Activity> Entries => _entries;

        /// <summary>Add an entry, dropping the oldest once the cap is reached.</summary>
        /// <param name="activity">The entry.</param>
        public void Record(Activity activity)
        {
            if (activity == null) throw new ArgumentNullException(nameof(activity));

            _entries.Insert(0, activity);
            while (_entries.Count > Keep) _entries.RemoveAt(_entries.Count - 1);
        }

        /// <summary>The most recent entry of a kind, or null when it has never happened.</summary>
        /// <param name="kind">One of <see cref="ActivityKind"/>.</param>
        public Activity? Latest(string kind) =>
            _entries.FirstOrDefault(e => string.Equals(e.Kind, kind, StringComparison.Ordinal));

        /// <summary>Serialise.</summary>
        public JsonValue ToJson() => JsonValue.Array(_entries.Select(e => e.ToJson()));

        /// <summary>Read a log back. Anything unreadable is skipped rather than failing the load.</summary>
        /// <param name="json">The saved array.</param>
        public static ActivityLog FromJson(JsonValue? json)
        {
            var log = new ActivityLog();
            if (json == null || json.Kind != JsonKind.Array) return log;

            foreach (var item in json.Items)
                if (Activity.TryFromJson(item, out var entry) && entry != null && log._entries.Count < Keep)
                    log._entries.Add(entry);

            return log;
        }

        /// <summary>
        /// How long ago something was, in the words a person would use.
        ///
        /// Pure, and takes "now" as an argument, so the phrasing is testable — which matters more
        /// than it sounds, because every off-by-one in this kind of function shows up as "0 minutes
        /// ago" on somebody's screen.
        /// </summary>
        /// <param name="whenTicks">When it happened, as UTC ticks.</param>
        /// <param name="nowTicks">Now, as UTC ticks.</param>
        public static string Ago(long whenTicks, long nowTicks)
        {
            if (whenTicks <= 0) return "never";

            var span = TimeSpan.FromTicks(nowTicks - whenTicks);

            if (span.Ticks < 0) return "just now";
            if (span.TotalSeconds < 90) return "just now";
            if (span.TotalMinutes < 60) return Plural((int)span.TotalMinutes, "minute") + " ago";
            if (span.TotalHours < 24) return Plural((int)span.TotalHours, "hour") + " ago";
            if (span.TotalHours < 48) return "yesterday";
            if (span.TotalDays < 30) return Plural((int)span.TotalDays, "day") + " ago";

            return new DateTime(whenTicks, DateTimeKind.Utc).ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        }

        private static string Plural(int count, string unit) =>
            count.ToString(CultureInfo.InvariantCulture) + " " + unit + (count == 1 ? string.Empty : "s");

        /// <inheritdoc />
        public override string ToString() => _entries.Count + " entries";
    }
}
