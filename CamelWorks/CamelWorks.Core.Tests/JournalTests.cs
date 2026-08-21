using System;
using System.Collections.Generic;
using System.Linq;
using CamelWorks.Core.Store;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class JournalFoldTests
    {
        private const string Finding = "f001";

        private static JournalEntry Scalar(string author, long ts, string field, string? value) =>
            new JournalEntry(author, ts, Finding, field, value);

        private static JournalEntry Comment(string author, long ts, string id, string? text) =>
            new JournalEntry(author, ts, Finding, JournalEntry.CommentField, text, id);

        // ---------------------------------------------------------------------------------
        // THE case. Comments are a set, not a value.
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Two_people_commenting_offline_keep_both_comments()
        {
            // Latest-wins on comments would silently drop one of these, with no trace that
            // anything had been said. A comment thread is a set being added to, not a value
            // being overwritten.
            var folded = JournalFold.Fold(new[]
            {
                Comment("anna", 100, "c1", "Can the duct drop 150?"),
                Comment("ben", 100, "c2", "Beam cannot move, it is a transfer."),
            });

            var comments = folded[Finding].Comments;
            Assert.Equal(2, comments.Count);
            Assert.Contains(comments, c => c.Author == "anna");
            Assert.Contains(comments, c => c.Author == "ben");
        }

        [Fact]
        public void Editing_your_own_comment_replaces_that_comment_and_no_other()
        {
            var folded = JournalFold.Fold(new[]
            {
                Comment("anna", 100, "c1", "typo verison"),
                Comment("anna", 200, "c1", "typo version"),   // same id: an edit
                Comment("ben", 150, "c2", "separate point"),
            });

            var comments = folded[Finding].Comments;
            Assert.Equal(2, comments.Count);
            Assert.Equal("typo version", comments.Single(c => c.Id == "c1").Text);
            Assert.Equal("separate point", comments.Single(c => c.Id == "c2").Text);
        }

        [Fact]
        public void A_deleted_comment_stays_in_the_thread_as_a_tombstone()
        {
            // Removing the row entirely would renumber a thread people refer to by position in a
            // meeting. A null text is rendered as "<deleted>" instead.
            var folded = JournalFold.Fold(new[]
            {
                Comment("anna", 100, "c1", "said something"),
                Comment("anna", 200, "c1", null),
            });

            var comment = Assert.Single(folded[Finding].Comments);
            Assert.Null(comment.Text);
        }

        [Fact]
        public void Comments_come_back_in_the_order_they_were_written()
        {
            var folded = JournalFold.Fold(new[]
            {
                Comment("ben", 300, "c3", "third"),
                Comment("anna", 100, "c1", "first"),
                Comment("cara", 200, "c2", "second"),
            });

            Assert.Equal(new[] { "first", "second", "third" },
                folded[Finding].Comments.Select(c => c.Text).ToArray());
        }

        // ---------------------------------------------------------------------------------
        // Scalars fold to one winner
        // ---------------------------------------------------------------------------------

        [Fact]
        public void The_latest_edit_of_a_scalar_wins()
        {
            var folded = JournalFold.Fold(new[]
            {
                Scalar("anna", 100, "assignee", "MEP"),
                Scalar("ben", 200, "assignee", "STR"),
            });

            Assert.Equal("STR", folded[Finding].Field("assignee"));
        }

        [Fact]
        public void A_simultaneous_edit_resolves_the_same_way_on_every_machine()
        {
            // Which author wins a tie does not matter. That everyone computes the same answer does
            // — otherwise two people's boards disagree and neither is wrong.
            var entries = new[]
            {
                Scalar("anna", 100, "assignee", "MEP"),
                Scalar("ben", 100, "assignee", "STR"),
            };

            var forwards = JournalFold.Fold(entries)[Finding].Field("assignee");
            var backwards = JournalFold.Fold(entries.Reverse())[Finding].Field("assignee");

            Assert.Equal(forwards, backwards);
        }

        [Fact]
        public void The_fold_does_not_depend_on_the_order_the_files_were_read()
        {
            var entries = new List<JournalEntry>
            {
                Scalar("anna", 100, "status", "Active"),
                Scalar("ben", 300, "status", "Resolved"),
                Scalar("cara", 200, "priority", "High"),
                Comment("anna", 150, "c1", "one"),
                Comment("ben", 250, "c2", "two"),
            };

            var a = JournalFold.Fold(entries);
            var b = JournalFold.Fold(Shuffle(entries));

            Assert.Equal(a[Finding].Field("status"), b[Finding].Field("status"));
            Assert.Equal(a[Finding].Field("priority"), b[Finding].Field("priority"));
            Assert.Equal(a[Finding].Comments.Select(c => c.Id), b[Finding].Comments.Select(c => c.Id));
        }

        [Fact]
        public void Clearing_a_field_is_an_edit_like_any_other()
        {
            var folded = JournalFold.Fold(new[]
            {
                Scalar("anna", 100, "due", "2026-09-01"),
                Scalar("anna", 200, "due", null),
            });

            Assert.Null(folded[Finding].Field("due"));
            Assert.True(folded[Finding].Fields.ContainsKey("due"));   // cleared, not absent
        }

        [Fact]
        public void Different_fields_and_targets_do_not_interfere()
        {
            var folded = JournalFold.Fold(new[]
            {
                new JournalEntry("anna", 100, "f001", "status", "Active"),
                new JournalEntry("anna", 100, "f002", "status", "Resolved"),
                new JournalEntry("anna", 100, "f001", "assignee", "MEP"),
            });

            Assert.Equal("Active", folded["f001"].Field("status"));
            Assert.Equal("Resolved", folded["f002"].Field("status"));
            Assert.Equal("MEP", folded["f001"].Field("assignee"));
        }

        [Fact]
        public void An_unset_field_reads_as_null_rather_than_throwing()
        {
            var folded = JournalFold.Fold(new[] { Scalar("anna", 100, "status", "Active") });

            Assert.Null(folded[Finding].Field("nobody-set-this"));
        }

        [Fact]
        public void Folding_nothing_yields_nothing()
        {
            Assert.Empty(JournalFold.Fold(Array.Empty<JournalEntry>()));
        }

        // ---------------------------------------------------------------------------------
        // JSON Lines
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Entries_round_trip_through_json_lines()
        {
            var entries = new[]
            {
                Scalar("anna", 100, "status", "Active"),
                Scalar("ben", 200, "due", null),
                Comment("cara", 300, "c1", "text with \"quotes\" and \n newline"),
            };

            var back = JournalFold.FromJsonLines(JournalFold.ToJsonLines(entries), out var skipped);

            Assert.Equal(0, skipped);
            Assert.Equal(3, back.Count);
            Assert.Equal("text with \"quotes\" and \n newline", back[2].Value);
            Assert.Equal("c1", back[2].CommentId);
            Assert.Null(back[1].Value);
        }

        [Fact]
        public void A_truncated_last_line_costs_one_edit_not_the_whole_file()
        {
            // An append-only file killed mid-write by a crash or a sync client loses its last
            // line. Refusing the file would throw away a week of triage to protect one lost edit.
            var good = JournalFold.ToJsonLines(new[]
            {
                Scalar("anna", 100, "status", "Active"),
                Scalar("anna", 200, "assignee", "MEP"),
            });

            var truncated = good + "\n{\"author\":\"anna\",\"ts\":300,\"tar";

            var back = JournalFold.FromJsonLines(truncated, out var skipped);

            Assert.Equal(2, back.Count);
            Assert.Equal(1, skipped);   // reported, so the pane can say so rather than pretend
        }

        [Theory]
        [InlineData("{\"ts\":1,\"target\":\"t\",\"field\":\"f\"}")]                  // no author
        [InlineData("{\"author\":\"a\",\"target\":\"t\",\"field\":\"f\"}")]          // no timestamp
        [InlineData("{\"author\":\"a\",\"ts\":1,\"field\":\"f\"}")]                  // no target
        [InlineData("{\"author\":\"a\",\"ts\":1,\"target\":\"t\"}")]                 // no field
        [InlineData("{\"author\":\"a\",\"ts\":\"soon\",\"target\":\"t\",\"field\":\"f\"}")]
        [InlineData("{\"author\":\"a\",\"ts\":1,\"target\":\"t\",\"field\":\"comment\"}")]  // comment, no id
        [InlineData("[1,2,3]")]
        public void A_malformed_line_is_skipped_and_counted(string line)
        {
            var back = JournalFold.FromJsonLines(line, out var skipped);

            Assert.Empty(back);
            Assert.Equal(1, skipped);
        }

        [Fact]
        public void Blank_lines_are_not_counted_as_damage()
        {
            var text = JournalFold.ToJsonLines(new[] { Scalar("anna", 100, "status", "Active") }) + "\n\n   \n";

            var back = JournalFold.FromJsonLines(text, out var skipped);

            Assert.Single(back);
            Assert.Equal(0, skipped);
        }

        [Fact]
        public void An_absent_journal_is_an_ordinary_first_run_state()
        {
            Assert.Empty(JournalFold.FromJsonLines(null, out var skipped));
            Assert.Equal(0, skipped);
        }

        // ---------------------------------------------------------------------------------
        // Entry validation
        // ---------------------------------------------------------------------------------

        [Fact]
        public void A_comment_entry_without_an_id_is_refused_at_construction()
        {
            // Without an id the fold cannot union comments, and the failure would show up later as
            // silently lost text rather than here as an argument error.
            Assert.Throws<ArgumentException>(() =>
                new JournalEntry("anna", 1, "f001", JournalEntry.CommentField, "text"));
        }

        [Theory]
        [InlineData("", "t", "f")]
        [InlineData("a", "", "f")]
        [InlineData("a", "t", "")]
        public void Author_target_and_field_are_all_required(string author, string target, string field)
        {
            Assert.Throws<ArgumentException>(() => new JournalEntry(author, 1, target, field, "v"));
        }

        private static IEnumerable<JournalEntry> Shuffle(IEnumerable<JournalEntry> entries)
        {
            // Deterministic reordering — a fixed permutation, not randomness, so a failure is
            // reproducible.
            var list = entries.ToList();
            return list.Where((_, i) => i % 2 == 1).Concat(list.Where((_, i) => i % 2 == 0));
        }
    }
}
