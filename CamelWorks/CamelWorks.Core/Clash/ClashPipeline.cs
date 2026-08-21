using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CamelWorks.Core.Clash
{
    /// <summary>One derived group.</summary>
    public sealed class ClashGroup
    {
        internal ClashGroup(string id, string name)
        {
            Id = id; Name = name;
        }

        /// <summary>Stable id, derived from the group's name within its scope.</summary>
        public string Id { get; }

        /// <summary>The chained rule segments, joined.</summary>
        public string Name { get; }

        /// <summary>
        /// True when the group was made by hand and must never be re-derived.
        ///
        /// Set if ANY member was placed here by hand, not just the first one to arrive. Deciding it
        /// from the first item would make the flag depend on the order the engine enumerated
        /// results in, and a pin that survives or not depending on enumeration order is worse than
        /// no pin at all.
        /// </summary>
        public bool IsPinned { get; internal set; }

        /// <summary>Members.</summary>
        public IList<ClashItem> Items { get; } = new List<ClashItem>();

        /// <summary>Responsible party, once assigned. Rules write it; people go through
        /// <see cref="AssignByHand"/>.</summary>
        public string? AssignedTo { get; internal set; }

        /// <summary>Priority, once set.</summary>
        public string? Priority { get; internal set; }

        /// <summary>
        /// True when the party on this group came from a person rather than a rule.
        ///
        /// Shown on the board, so a coordinator can tell at a glance which assignments the machine
        /// guessed — and, more importantly, snapshotted, because a hand assignment is the one thing
        /// the next run must not re-derive.
        /// </summary>
        public bool AssignedByHand { get; private set; }

        /// <summary>
        /// Record a person's decision. The only way to set a party from outside the pipeline, so
        /// that a hand assignment can never be mistaken for a rule's guess and quietly overwritten.
        /// </summary>
        /// <param name="party">Responsible party.</param>
        /// <param name="priority">Priority, or null to leave whatever is there.</param>
        public void AssignByHand(string? party, string? priority = null)
        {
            AssignedTo = party;
            if (priority != null) Priority = priority;
            AssignedByHand = true;
        }

        /// <inheritdoc />
        public override string ToString() =>
            Name + " (" + Items.Count.ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>
    /// What a person decided about a group, carried into the next run.
    ///
    /// Keyed by group name, because the group is the unit of responsibility and its name is
    /// reproduced exactly by the same stack on the next run. A decision is the only thing in the
    /// pipeline that a rule may not overwrite.
    /// </summary>
    public sealed class GroupDecision
    {
        /// <summary>Create a decision.</summary>
        public GroupDecision(string? assignedTo = null, string? priority = null)
        {
            AssignedTo = assignedTo;
            Priority = priority;
        }

        /// <summary>Responsible party a person chose.</summary>
        public string? AssignedTo { get; }

        /// <summary>Priority a person chose.</summary>
        public string? Priority { get; }
    }

    /// <summary>
    /// Everything the pipeline needs beyond the results themselves.
    ///
    /// An options object rather than eight optional arguments: each stage of the pipeline has its
    /// own input, and they will keep being added. A call site that configures two of them should
    /// not have to write five nulls to reach the sixth.
    /// </summary>
    public sealed class ClashPipelineOptions
    {
        /// <summary>Suppress and flag rules, evaluated in order.</summary>
        public IReadOnlyList<FilterRule> Filters { get; set; } = Array.Empty<FilterRule>();

        /// <summary>
        /// The grouping stack. Null means "nobody configured one", and
        /// <see cref="ClashPipeline.DefaultStack"/> runs instead; an empty list means "explicitly
        /// flat", which is a legitimate view and not the same thing.
        /// </summary>
        public IReadOnlyList<IGroupingRule>? Grouping { get; set; }

        /// <summary>Assign rules, applied only to groups nobody has assigned.</summary>
        public IReadOnlyList<AssignRule> Assigns { get; set; } = Array.Empty<AssignRule>();

        /// <summary>
        /// Group name to the result keys hand-placed in it, for pins made in this session.
        /// These are never re-derived — that is exactly what "keep existing groups" means, and
        /// without it every re-run destroys the hand grouping.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> PinnedGroups { get; set; } =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        /// <summary>Decisions a person made, keyed by group name. A rule can never overwrite one.</summary>
        public IReadOnlyDictionary<string, GroupDecision> Decisions { get; set; } =
            new Dictionary<string, GroupDecision>(StringComparer.Ordinal);

        /// <summary>
        /// How many results <see cref="DuplicateCollapse"/> folded away before the pipeline ran.
        ///
        /// Carried purely so the funnel can show it. A collapse that removes 400 rows from a board
        /// and leaves no trace in the numbers is the same unauditable hide-button the suppression
        /// count exists to avoid.
        /// </summary>
        public int CollapsedDuplicates { get; set; }
    }

    /// <summary>
    /// The funnel readout — the most trust-critical numbers in the product.
    ///
    /// This is what a coordinator defends when a client asks why the board says 214 and the model
    /// says 1,910. Every number is clickable back to the rows behind it, and nothing that removed
    /// a row is left out of it: an unauditable hide-button is not something anyone can hand to a
    /// client.
    /// </summary>
    public sealed class ClashFunnel
    {
        internal ClashFunnel(int input, int duplicates, int suppressed, int flagged, int groups,
                             int assigned, int unassigned, IReadOnlyList<string> rulesApplied, int ungroupable)
        {
            Input = input; Duplicates = duplicates; Suppressed = suppressed; Flagged = flagged;
            Groups = groups; Assigned = assigned; Unassigned = unassigned;
            RulesApplied = rulesApplied; Ungroupable = ungroupable;
        }

        /// <summary>Results the engine reported, including any collapsed before the pipeline ran.</summary>
        public int Input { get; }

        /// <summary>Results folded into another as cross-test duplicates.</summary>
        public int Duplicates { get; }

        /// <summary>Results removed from the board by suppression rules.</summary>
        public int Suppressed { get; }

        /// <summary>Results kept but marked by flag rules.</summary>
        public int Flagged { get; }

        /// <summary>Groups derived.</summary>
        public int Groups { get; }

        /// <summary>Groups with a responsible party.</summary>
        public int Assigned { get; }

        /// <summary>Groups without one — the number that decides whether Thursday is finished.</summary>
        public int Unassigned { get; }

        /// <summary>
        /// Results no grouping rule could place, because every rule returned null for them.
        /// Reported rather than swept into an "Unknown" bucket, since a model with no levels
        /// produces thousands of these and the cause is fixable.
        /// </summary>
        public int Ungroupable { get; }

        /// <summary>Names of the rules that removed or marked anything.</summary>
        public IReadOnlyList<string> RulesApplied { get; }

        /// <summary>The one-line readout.</summary>
        public override string ToString()
        {
            var s = Input.ToString("N0", CultureInfo.InvariantCulture) + " results";
            if (Duplicates > 0)
                s += " → " + Duplicates.ToString("N0", CultureInfo.InvariantCulture) + " duplicates collapsed";
            if (Suppressed > 0)
                s += " → " + Suppressed.ToString("N0", CultureInfo.InvariantCulture) + " suppressed by "
                   + RulesApplied.Count.ToString(CultureInfo.InvariantCulture) + " rules";
            s += " → " + Groups.ToString("N0", CultureInfo.InvariantCulture) + " groups";
            s += " → " + Assigned.ToString("N0", CultureInfo.InvariantCulture) + " assigned · "
               + Unassigned.ToString("N0", CultureInfo.InvariantCulture) + " unassigned";
            return s;
        }
    }

    /// <summary>The result of running the pipeline.</summary>
    public sealed class ClashPipelineResult
    {
        internal ClashPipelineResult(IReadOnlyList<ClashGroup> groups, IReadOnlyList<ClashItem> suppressed,
                                     IReadOnlyList<ClashItem> flagged, ClashFunnel funnel)
        {
            Groups = groups; Suppressed = suppressed; Flagged = flagged; Funnel = funnel;
        }

        /// <summary>The board.</summary>
        public IReadOnlyList<ClashGroup> Groups { get; }

        /// <summary>What suppression removed — retrievable, never discarded.</summary>
        public IReadOnlyList<ClashItem> Suppressed { get; }

        /// <summary>What flag rules marked.</summary>
        public IReadOnlyList<ClashItem> Flagged { get; }

        /// <summary>The numbers.</summary>
        public ClashFunnel Funnel { get; }
    }

    /// <summary>
    /// Suppress and Flag, then Group, then Assign — in that order, under one Apply.
    ///
    /// The order is not arbitrary. Suppression runs BEFORE grouping because grouping 4,000 results
    /// into 400 groups does not stop 3,000 of them being things the coordinator already decided are
    /// not clashes — and fingerprint carry-over does not cover that, because a fresh re-export
    /// produces NEW results matching a known-ignorable pattern, arriving as New every week.
    /// Assignment runs AFTER grouping because the group is the unit of responsibility.
    ///
    /// The whole pipeline is a pure function of its inputs, which is what makes the preview honest:
    /// the numbers shown before Apply are computed by the same code that runs on Apply.
    /// </summary>
    public static class ClashPipeline
    {
        /// <summary>Run the pipeline.</summary>
        /// <param name="items">Results from the engine, after carry-over has run over them.</param>
        /// <param name="options">The stack, the rules, and the decisions. Null runs the defaults.</param>
        public static ClashPipelineResult Run(IEnumerable<ClashItem> items, ClashPipelineOptions? options = null)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            options ??= new ClashPipelineOptions();
            var all = items.Where(i => i != null).ToList();
            var grouping = options.Grouping ?? DefaultStack;

            // --- 1. Suppress and flag -----------------------------------------------------
            var kept = new List<ClashItem>();
            var suppressed = new List<ClashItem>();
            var flagged = new List<ClashItem>();
            var rulesApplied = new List<string>();

            foreach (var item in all)
            {
                var removed = false;

                foreach (var rule in options.Filters)
                {
                    if (!rule.When.Matches(item)) continue;

                    if (!rulesApplied.Contains(rule.Name)) rulesApplied.Add(rule.Name);

                    if (rule.Suppress) { removed = true; break; }
                    if (!flagged.Contains(item)) flagged.Add(item);
                }

                if (removed) suppressed.Add(item); else kept.Add(item);
            }

            // --- 2. Group -----------------------------------------------------------------
            var pinnedByItem = BuildPinnedIndex(options.PinnedGroups);
            var groups = new List<ClashGroup>();
            var byName = new Dictionary<string, ClashGroup>(StringComparer.Ordinal);
            var pinnedNames = new HashSet<string>(StringComparer.Ordinal);
            var ungroupable = 0;

            foreach (var item in kept)
            {
                string name;
                bool pinned;

                // Precedence: a pin made in this session, then a hand-made group carried from the
                // last run, then the stack. The newer hand decision wins over the older one, and
                // both win over the rules — a stack that could silently undo hand grouping is a
                // stack nobody would dare re-run.
                if (pinnedByItem.TryGetValue(item.Key.ToString(), out var pinnedName))
                {
                    name = pinnedName;
                    pinned = true;
                }
                else if (!string.IsNullOrWhiteSpace(item.CarriedGroup))
                {
                    name = item.CarriedGroup!;
                    pinned = true;
                }
                else
                {
                    var segments = grouping
                        .Select(r => r.KeyFor(item))
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();

                    if (segments.Count == 0)
                    {
                        // Every rule returned null. Counted and reported rather than swept into an
                        // "Unknown" bucket: a model with no levels produces thousands of these and
                        // the cause is fixable.
                        ungroupable++;
                        name = "Ungrouped";
                    }
                    else
                    {
                        name = string.Join(" · ", segments);
                    }

                    pinned = false;
                }

                if (pinned) pinnedNames.Add(name);

                if (!byName.TryGetValue(name, out var group))
                {
                    group = new ClashGroup(GroupIdFor(name), name);
                    byName[name] = group;
                    groups.Add(group);
                }

                group.Items.Add(item);
            }

            foreach (var group in groups)
                group.IsPinned = pinnedNames.Contains(group.Name);

            // --- 3. Apply decisions, then assign ------------------------------------------
            // Decisions land before the rules run, which is what makes the guard below reachable.
            foreach (var group in groups)
            {
                if (!options.Decisions.TryGetValue(group.Name, out var decision) || decision == null) continue;

                if (!string.IsNullOrWhiteSpace(decision.AssignedTo))
                    group.AssignByHand(decision.AssignedTo, decision.Priority);
                else if (!string.IsNullOrWhiteSpace(decision.Priority))
                    group.Priority = decision.Priority;
            }

            var assigned = 0;
            foreach (var group in groups)
            {
                // Only groups with no party. A rule can never overwrite a human decision.
                if (!string.IsNullOrWhiteSpace(group.AssignedTo)) { assigned++; continue; }

                foreach (var rule in options.Assigns)
                {
                    if (!group.Items.Any(i => rule.When.Matches(i))) continue;

                    group.AssignedTo = rule.Party;
                    group.Priority ??= rule.Priority;
                    assigned++;
                    break;
                }
            }

            var funnel = new ClashFunnel(
                all.Count + options.CollapsedDuplicates, options.CollapsedDuplicates,
                suppressed.Count, flagged.Count, groups.Count,
                assigned, groups.Count - assigned, rulesApplied, ungroupable);

            return new ClashPipelineResult(groups, suppressed, flagged, funnel);
        }

        /// <summary>
        /// The stack that runs when nobody has configured one — model pair, then level, then a
        /// 5 m proximity bucket.
        ///
        /// This is the zero-setup rule made concrete. A coordinator who has just installed
        /// CamelWorks and opened a federation gets a grouped board on the first click; it is not
        /// their job to design a rule stack before the product does anything.
        /// </summary>
        public static IReadOnlyList<IGroupingRule> DefaultStack { get; } = new[]
        {
            GroupingRules.ByModelPair(),
            GroupingRules.ByLevel(),
            GroupingRules.ByProximity(5.0),
        };

        private static Dictionary<string, string> BuildPinnedIndex(
            IReadOnlyDictionary<string, IReadOnlyList<string>>? pinnedGroups)
        {
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            if (pinnedGroups == null) return index;

            foreach (var kv in pinnedGroups)
                foreach (var key in kv.Value)
                    index[key] = kv.Key;

            return index;
        }

        private static string GroupIdFor(string name) =>
            Identity.Hash.Of(Identity.Hash.ComponentWidth, "group", name);
    }
}
