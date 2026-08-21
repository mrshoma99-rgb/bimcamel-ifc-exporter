using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CamelWorks.Core.Store
{
    /// <summary>
    /// One recorded edit.
    ///
    /// Journals are append-only and per-writer: two people editing the same board write to two
    /// different files and never contend for one. The materialised view is derived by folding them
    /// together, which means a lost update is impossible by construction rather than by locking.
    ///
    /// <b>The timestamp is supplied, never read from the clock here.</b> A fold that reads the
    /// clock is a fold that cannot be tested, and the ordering it produces would depend on when it
    /// happened to run. Callers stamp entries at the moment of the edit.
    /// </summary>
    public sealed class JournalEntry
    {
        /// <summary>Create an entry.</summary>
        /// <param name="author">Stable writer id — the local user identity, not a display name.</param>
        /// <param name="timestampTicks">When the edit was made, as UTC ticks.</param>
        /// <param name="target">What was edited: a finding id, a clash key, a group id.</param>
        /// <param name="field">Which field. <see cref="CommentField"/> is folded differently.</param>
        /// <param name="value">The new value, or null to clear.</param>
        /// <param name="commentId">
        /// Required when <paramref name="field"/> is <see cref="CommentField"/>: comments are
        /// identified individually so a fold can union them instead of picking a winner.
        /// </param>
        public JournalEntry(string author, long timestampTicks, string target, string field,
                            string? value, string? commentId = null)
        {
            if (string.IsNullOrWhiteSpace(author)) throw new ArgumentException("author is required", nameof(author));
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("target is required", nameof(target));
            if (string.IsNullOrWhiteSpace(field)) throw new ArgumentException("field is required", nameof(field));
            if (field == CommentField && string.IsNullOrWhiteSpace(commentId))
                throw new ArgumentException("a comment entry needs a comment id, or the fold cannot union comments", nameof(commentId));

            Author = author;
            TimestampTicks = timestampTicks;
            Target = target;
            Field = field;
            Value = value;
            CommentId = commentId;
        }

        /// <summary>The field name reserved for comments, which fold by union rather than by winner.</summary>
        public const string CommentField = "comment";

        /// <summary>Stable writer id.</summary>
        public string Author { get; }

        /// <summary>When the edit was made, as UTC ticks.</summary>
        public long TimestampTicks { get; }

        /// <summary>What was edited.</summary>
        public string Target { get; }

        /// <summary>Which field.</summary>
        public string Field { get; }

        /// <summary>The new value, or null to clear.</summary>
        public string? Value { get; }

        /// <summary>Identity of the comment, for comment entries.</summary>
        public string? CommentId { get; }

        /// <summary>Serialise to a JSON object.</summary>
        public JsonValue ToJson()
        {
            var o = JsonValue.Object()
                .Set("author", Author)
                .Set("ts", TimestampTicks)
                .Set("target", Target)
                .Set("field", Field);

            if (Value != null) o.Set("value", Value);
            if (CommentId != null) o.Set("commentId", CommentId);
            return o;
        }

        /// <summary>Read from a JSON object. Returns false on anything malformed rather than throwing.</summary>
        public static bool TryFromJson(JsonValue json, out JournalEntry? entry)
        {
            entry = null;
            if (json == null || json.Kind != JsonKind.Object) return false;

            var author = json["author"].AsString();
            var target = json["target"].AsString();
            var field = json["field"].AsString();
            if (author == null || target == null || field == null) return false;
            if (json["ts"].Kind != JsonKind.Number) return false;

            var commentId = json["commentId"].AsString();
            if (field == CommentField && commentId == null) return false;

            entry = new JournalEntry(author, json["ts"].AsLong(), target, field, json["value"].AsString(), commentId);
            return true;
        }

        /// <inheritdoc />
        public override string ToString() =>
            Target + "." + Field + "=" + (Value ?? "<null>")
            + " @" + TimestampTicks.ToString(CultureInfo.InvariantCulture) + " by " + Author;
    }

    /// <summary>The state one target ends up in after folding every writer's journal.</summary>
    public sealed class FoldedTarget
    {
        internal FoldedTarget(string target) => Target = target;

        /// <summary>What this state is about.</summary>
        public string Target { get; }

        /// <summary>Scalar fields, folded to one winner each.</summary>
        public IDictionary<string, string?> Fields { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);

        /// <summary>Comments, unioned by id and ordered by when they were written.</summary>
        public IList<FoldedComment> Comments { get; } = new List<FoldedComment>();

        /// <summary>Read a folded field, or null when nobody set it.</summary>
        public string? Field(string name) => Fields.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>One comment after folding.</summary>
    public readonly struct FoldedComment
    {
        internal FoldedComment(string id, string author, long timestampTicks, string? text)
        {
            Id = id; Author = author; TimestampTicks = timestampTicks; Text = text;
        }

        /// <summary>Comment identity.</summary>
        public string Id { get; }

        /// <summary>Who wrote it.</summary>
        public string Author { get; }

        /// <summary>When, as UTC ticks.</summary>
        public long TimestampTicks { get; }

        /// <summary>The text, or null when the author deleted their own comment.</summary>
        public string? Text { get; }

        /// <inheritdoc />
        public override string ToString() => Author + ": " + (Text ?? "<deleted>");
    }

    /// <summary>
    /// Folds every writer's journal into one materialised view.
    ///
    /// Two classes of field, and the distinction is the whole design:
    ///
    /// <b>Scalars</b> — status, assignee, due date, priority — fold to a single winner:
    /// latest timestamp wins, ties broken by author id. One value, one winner, and a later edit
    /// replaces an earlier one because that is what "I changed the assignee" means.
    ///
    /// <b>Comments</b> — union by id, never a winner. Applying latest-wins to comments would mean
    /// two people commenting while offline lose one of the two, silently, with no trace that
    /// anything was said. A comment thread is not a value being overwritten; it is a set being
    /// added to. This distinction was missed in an earlier design and is the reason the field is
    /// carried as its own class rather than as one more scalar.
    ///
    /// The fold is pure: same entries in, same view out, whatever order the files were read in.
    /// </summary>
    public static class JournalFold
    {
        /// <summary>Key separator; a control character because it cannot occur in an id or a field name.</summary>
        private const string Sep = "\u001F";

        /// <summary>Fold entries from any number of writers into one view, keyed by target.</summary>
        public static IReadOnlyDictionary<string, FoldedTarget> Fold(IEnumerable<JournalEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));

            var byTarget = new Dictionary<string, FoldedTarget>(StringComparer.Ordinal);

            // Winner-so-far per (target, field), so the fold is one pass and order-independent.
            var scalarWinner = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
            var commentWinner = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);

            foreach (var e in entries)
            {
                if (e == null) continue;

                if (!byTarget.ContainsKey(e.Target)) byTarget[e.Target] = new FoldedTarget(e.Target);

                if (e.Field == JournalEntry.CommentField)
                {
                    // Union by comment id. Within ONE comment id, a later edit by its author is an
                    // edit of that comment — so the same winner rule applies inside the id, but
                    // never across ids.
                    var key = e.Target + Sep +  e.CommentId;
                    if (!commentWinner.TryGetValue(key, out var held) || Beats(e, held))
                        commentWinner[key] = e;
                }
                else
                {
                    var key = e.Target + Sep +  e.Field;
                    if (!scalarWinner.TryGetValue(key, out var held) || Beats(e, held))
                        scalarWinner[key] = e;
                }
            }

            foreach (var kv in scalarWinner)
                byTarget[kv.Value.Target].Fields[kv.Value.Field] = kv.Value.Value;

            foreach (var group in commentWinner.Values
                         .GroupBy(e => e.Target, StringComparer.Ordinal))
            {
                var list = byTarget[group.Key].Comments;
                foreach (var e in group
                             .OrderBy(e => e.TimestampTicks)
                             .ThenBy(e => e.CommentId ?? string.Empty, StringComparer.Ordinal))
                    list.Add(new FoldedComment(e.CommentId!, e.Author, e.TimestampTicks, e.Value));
            }

            return byTarget;
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> supersedes <paramref name="held"/>.
        /// Later wins; a tie is broken by author id so that every machine folding the same entries
        /// reaches the same answer. Which author wins a tie does not matter — that they all agree does.
        /// </summary>
        private static bool Beats(JournalEntry candidate, JournalEntry held)
        {
            if (candidate.TimestampTicks != held.TimestampTicks)
                return candidate.TimestampTicks > held.TimestampTicks;
            return string.CompareOrdinal(candidate.Author, held.Author) > 0;
        }

        /// <summary>Serialise entries as JSON Lines — one object per line, append-only.</summary>
        public static string ToJsonLines(IEnumerable<JournalEntry> entries) =>
            string.Join("\n", entries.Select(e => e.ToJson().ToJson(indented: false)));

        /// <summary>
        /// Read JSON Lines, skipping any line that does not parse.
        ///
        /// A damaged line is skipped rather than failing the whole file, and this is the one place
        /// that leniency is right: an append-only file truncated by a crash or a sync client loses
        /// its LAST line, and refusing to read the other nine hundred would throw away a week of
        /// triage to protect one lost edit. <paramref name="skipped"/> reports how many, so the
        /// pane can say so rather than pretending the file was clean.
        /// </summary>
        public static IReadOnlyList<JournalEntry> FromJsonLines(string? text, out int skipped)
        {
            skipped = 0;
            var list = new List<JournalEntry>();
            if (string.IsNullOrWhiteSpace(text)) return list;

            foreach (var line in text!.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                if (!JsonReader.TryParse(trimmed, out var json) || !JournalEntry.TryFromJson(json, out var entry) || entry == null)
                {
                    skipped++;
                    continue;
                }

                list.Add(entry);
            }

            return list;
        }
    }
}
