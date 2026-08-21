# CamelWorks — scope decision

> **This document is the RECORD, not the spec.** Its outcome is folded into
> [`CAMELWORKS_IMPLEMENTATION.md`](CAMELWORKS_IMPLEMENTATION.md), which is the single build-ready
> source of truth — 25 ribbon buttons, 16 tabs, the revised cut list and the definition of done all
> live there. Read this one for *why*: the 100 candidates, the three-lens judgements, the six
> overturned verdicts, the declines and the cost argument. Where the two ever disagree, the
> implementation plan wins.

Lead Architect, final. Supersedes §6 of `CAMELWORKS_IMPLEMENTATION.md` and adds §0 below to §4.

Seven roles produced 100 candidates; three lenses judged each. This document decides them. It also does
the thing none of the hundred did, and which one judge correctly called the actual deliverable of the
exercise: **it names what comes out.** A re-evaluation that only adds is a wish list with citations.

Two constraints govern every call below, and they are the reason the answer is not "yes to all of it":

- **1–2 developers, one release, eight host configurations, no telemetry, free, with a published
  support commitment.** Build cost is not the binding constraint. *Surface* is — surface the cold-start
  rubric has to survive, surface the 8-row smoke matrix has to cover, and surface that generates tickets
  two people answer with no instrumentation.
- **THE RULE.** "No existing node / no engine / greenfield" appears nowhere below as a reason for or
  against anything. Where I decline something, the reason is a user, a week, a support cost or a
  correctness risk.

---

> ## ⚑ Verified after this decision was written — the Section Box crisis is over
>
> §6's first unknown asked whether the managed API has a documented **write** counterpart to
> `View.GetClippingPlanes()`, noting that seven roles jumped from "the internal surface broke in 2025"
> straight to "use COM" and nobody checked.
>
> **It does, on every target year.** Reflected over the `Speckle.Navisworks.API` metadata for
> 2024 / 2025 / 2026 / 2027:
>
> | Member | 2024 | 2025 | 2026 | 2027 |
> |---|---|---|---|---|
> | `SetClippingPlanes` | ✓ | ✓ | ✓ | ✓ |
> | `TrySetClippingPlanes` | ✓ | ✓ | ✓ | ✓ |
> | `set_ClipPlanes` (property form) | ✗ | ✓ | ✓ | ✓ |
> | `ClipPlaneSetMode` | ✗ | ✓ | ✓ | ✓ |
>
> Use the **method** form, which is universal; the property form and `ClipPlaneSetMode` are 2025+ only.
> The managed surface also carries `ClipPlaneSet`, `ClipPlane`, `ClipPlaneAlignment`, `SetClipPlaneSet`,
> `SetClipPlaneEnabled`, `SetClipPlanePosition` and `SetActiveClipPlane`.
>
> **Consequences:** Risk 2 is closed. Spike 0-S2 is answered before it starts — no COM interop
> reference, no undocumented `InternalClipPlanes`, no STA-confined native surface, no per-year branch
> and no extra smoke row. V2, X3's clipping-plane payload and review mode's group-extent boxing all
> proceed on a documented API. Unknowns 2 and 3 (COM planes surviving viewpoint capture; six planes vs
> one) are moot as posed — re-ask them against `SetClippingPlanes` instead.

---

## 0-L — CLOSED by the owner

This decision was written with "0-L publication clearance" as a blocker on all source harvesting, and
with two open ownership questions in §6. **Both are answered: the owner has confirmed the code is theirs
and that both sources may be used as they see fit.** 0-L is closed, source-harvest is unblocked, and
every harvest verdict below that was marked provisional on it now stands on its own merits. The
remaining work is mechanical de-branding under `HARVEST_PROTOCOL.md` — every company identity in the
shipped product is **BIMCamel / bimcamel.com**.

Two further owner directives amend this document:
- **No viewer.** Harvesting from the web product means its headless engines — BCF, IDS, Findings, the
  appearance precedence logic. Nothing that renders. Navisworks is the viewer.
- **Zero setup.** See `CAMELWORKS_IMPLEMENTATION.md` §0. Every feature works on first open with no
  template, profile or configuration. Where anything below assumes a configured project, the
  zero-config default in §0 takes precedence.

---

## 0. The two directed items, specified

Both are decided. This section is build-ready spec, not argument. Both move **out of Stage 6**: the two
things the owner personally directed were scheduled behind the entire Coordinate workspace, which means
they are the two things a slip squeezes. BetterSets lands at **Stage 4** with Sets; the Appearance
Manager lands at **Stage 4–5**, before the Coordinate workspace consumes it for review mode.

### 0.1 BetterSets — the set authoring surface

**Where it lives.** `Sets & Views ▸ Sets` — **one** tab, replacing both `Library` and `Generate`.
It is the only place a set is authored in CamelWorks.

**Data model** (harvested verbatim from `/home/user/kvi_tools/KVI_Tools/BetterSets/`, all of it
zero-Autodesk except `SearchEmitter`):

```
SetNode
├── RuleNode    { Category, Property, Comparison, Value, Negate, IgnoreCase }
│                 Comparison ∈ Defined | Equals | Contains | Wildcard
│                             | LessThan | LessOrEqual | GreaterOrEqual | GreaterThan | HasCategory
├── SetRefNode  { SetGuid, SetName }          ← reference to another saved set
└── GroupNode   { Op ∈ All | Any | None, Children[] }   ← None is what makes subtraction possible
```

Every comparison is positive; "does not equal" is `Equals` with `Negate`, because Navisworks carries
negation as a flag on the condition and holding it in two places means guessing whether a second
`Negate()` toggles or re-applies. Keep that invariant.

**Compilation.** `DnfCompiler` rewrites any nesting into the one shape Navisworks can store — an OR of
ANDs — because a saved set's conditions are a flat list where each condition carries a `StartGroup`
flag. `SearchEmitter` turns each term into one `SearchConditions.AddGroup(...)`, applying `Negate()`
once and last so the literal's folded negation never double-applies. Caps: 64 terms / 512 literals.

**Two blockers, both must stay visible, never silent.** A reference to a hand-picked set has no rules to
inline, and an expansion can explode past the caps. Both come back as `DnfResult.Blocker` and the set
saves as a **resolved snapshot with the reason named on the set**, not as a live search that quietly
matches something else.

**Four changes required before it ships.**

1. **Parameters.** `RuleNode.Value` is a literal string today — BetterSets has **no variable concept at
   all**. Adopting it as-is silently deletes the plan's promised "templates with variables
   (`Level = <param>`)", which is the mechanism that lets one recipe serve forty projects with different
   level names. Add a `params` block on the recipe: `Value` may carry `<param:Level>`, bound at emit
   time from the project profile or from a prompt. Parameters are resolved into the emitted `Search`,
   so the resulting native set is still live and still readable by someone without CamelWorks.
2. **Provenance and a divergence check.** `BetterSetsStore.TryReadRecipe` takes the **first parseable
   comment with no check that the emitted search still matches the recipe** — so a set edited in native
   Find Items makes its own documentation a lie, invisibly. Add to the recipe header: `templateId`,
   `templateVersion`, `authoredBy`, and a **hash of the emitted `Search`**. Health gains one row:
   *"N sets have been edited outside CamelWorks — their stated definition no longer matches."*
   Detectable divergence instead of a quiet lie.
3. **Comment preservation is a product invariant, not a module behaviour.**
   `BetterSetsStore.WriteRecipe` rebuilds the comment collection skipping only its own recipe comment
   and preserving body, status and author of everyone else's. Every CamelWorks write that touches a
   comment collection obeys this. It is the same rule as "comments merge by id, they never overwrite",
   and it is the one place a careless port silently destroys a colleague's work.
4. **The editor is a WPF rewrite and must be priced as one.** KVI's `BetterSetsForm.cs` is 976 lines of
   WinForms; the CamelWorks shell is WPF. The algebra — `SetNode`, `DnfCompiler`, `SearchEmitter`,
   `SetEvaluator`, `SetFormula`, `SetRecipe`, `SplitterPlanner` — is Core-shaped and moves. The tree
   editor does not. **This is the single largest UI item either directed decision adds**, and "BetterSets
   comes in" has been reading as free because the engine exists. It is not free. Budget 2–3 weeks.

**Bulk generation** folds in here rather than living as its own tab: an **ordered list** of properties
producing a folder per distinct value at each level and a leaf set at the bottom, each leaf emitted as a
BetterSets recipe bound to conditions (never a resolved item list, so it survives a refresh). Depth
capped at 3; the projected folder and set count shown before writing; a refusal above a stated ceiling.
A four-level cross-product mints thousands of sets and the native Sets window does not survive it. The
naming template must make cross-branch collisions **impossible**, not unlikely.

**Library import** gains a dry run and a per-line `Created / SkippedExisting / Failed` report, with
**skip-existing as the default** — re-running a template onto a live project must never silently replace
a set a coordinator has tuned.

**§2.4 is amended from one authority rule to two.** The sidecar is authoritative for triage state and
anything multi-writer. **The document is authoritative for anything whose entire value is that it
reaches someone who does not have CamelWorks** — set recipes, saved viewpoints, native clash status.
BetterSets writes its definition into the set's own comment *deliberately*, and it is right to: it is
the only thing in the product that makes P14 go away for the recipient, and the cheapest available
answer to the abandonware risk. If CamelWorks stops being maintained, last year's NWF still explains
itself. **One caution:** if spike 0-S1 finds that a read-modify-write of clash comments does not
preserve foreign comments, this rule is not applied to clash comments.

### 0.2 The Appearance Manager — the layers system

**Where it lives.** `Sets & Views ▸ Appearance` — one new tab. **It absorbs, and therefore removes:**

| Removed / rebound | Becomes |
|---|---|
| `Sets & Views ▸ Colour` **(tab deleted)** | Colour-by-property is an action that mints **a set of layers**, one per distinct value, each named and each appearing in the legend |
| `Isolate ▾` (ribbon) | Pushes/pops an Isolate layer. Ribbon button stays; it is now a stack operation |
| "Reset CamelWorks overrides" | "Remove every layer" — the panel's existing affordance |
| `Viewpoints ▸ copy overrides between viewpoints` | "Copy from view…" in the panel |
| Review mode's isolate-plus-ghost **action** | A layer the manager pushes on entry and pops on exit |

**Net: one tab added, one tab removed, four surfaces consolidated into one.** Everyone in the panel
priced this as a pure addition. It is not.

**The layer:**

```
AppearanceLayer {
  id, name, enabled,
  scope:  elements(ElementKey[])
        | selectionSet(id) | searchSet(id)
        | rule(BetterSets recipe)
        | category(name),
  color?   : rgb,
  opacity? : 0..1,
  visible? : bool
}
```

Five scope kinds — the owner's list, a superset of the web viewer's two. Rule and category scopes store
**the rule** and expand to elements only at capture/export time.

**Precedence: an ordered list, later wins, and later wins PER PROPERTY.** A later opacity-only layer over
an earlier colour-only layer yields colour + opacity, not opacity alone. This is
`overridePainter.recompute`'s fold, verified in source: iterate in order, and per resolved element assign
only the properties the layer actually sets. Port that semantic and port its tests.

**Hidden, coloured and ghosted are one mechanism.** `visible` is a layer property, not a second system.
That is the entire point of the directed feature.

**The painting layer is PERMANENT, and this is forced, not chosen.** Verified against the API:
`Models.ResetPermanentMaterials(collection)` is per-item; `Models.ResetAllTemporaryMaterials()` is
parameterless and global; and **there is no read-back of temporary overrides at all.** Permanent is the
only layer that is both per-item resettable and readable, so a stack that supports removal, reorder and
scope-narrowing can only live there. Four consequences, none of which is written anywhere today:

1. **Permanent overrides are saved into the NWF, and `SaveWithOverrides` bakes them into every captured
   view.** So the stack a coordinator builds on Wednesday travels into Friday's published NWD, every
   report image and every BCF snapshot unless something stops it. Therefore: the Batch **Save-refuses**
   gate gains an appearance clause; **Health counts elements carrying a CamelWorks permanent override**
   the way it counts hidden items; and every capture-for-deliverable path either resets the stack first
   or records in the output that it did not.
2. **`SetHidden` is durable NWF state while colour is session-visible only in the temporary layer we are
   not using** — so both halves of a layer now persist, and closing a document with hidden elements
   leaves them hidden in the deliverable. The manager asks on close.
3. **The foreign-state row.** A permanent, non-removable bottom row reading
   *"Model state, not managed by CamelWorks — 4,231 hidden · 812 recoloured"*, with **Select**,
   **Adopt as a layer** and **Clear**. Sourced from the read-back in
   `Overrides/OverrideTransfer.CaptureFromDocument`: `IsHidden`, `Geometry.OriginalColor` vs
   `PermanentColor`, `OriginalTransparency` vs `PermanentTransparency`. Without this row the panel reads
   "no layers" while four thousand elements are hidden by a double-clicked saved viewpoint — **worse than
   Navisworks today, where at least the coordinator knows they do not know.** The row must state that it
   reports **permanent overrides only**; it may never imply completeness, because temporary overrides
   cannot be read at all.
4. **"Reset" never means `ResetAllPermanentMaterials`.** It means "clear the CamelWorks stack". The
   global reset also wipes foreign state the user wanted, which is the disease, not the cure.

**Two adversaries write the same surface. Both need a decided arbitration.**

- **Saved viewpoints are an appearance write channel** — nothing in either document says so. Recalling
  one restores *resolved per-item* overrides, not layers: pixels restored, meaning lost, panel empty.
  Fix: hook viewpoint change → mark the stack **stale** with a one-click **Re-apply layers**; and store
  the stack against the viewpoint GUID and restore it with the view, so two report runs of the same
  group cannot produce different pictures.
- **Clash Detective's auto-isolate and highlight write temporary overrides and hidden state on every
  result click**, and we cannot read temporary overrides back — so CamelWorks can only *infer* that it
  has been overpainted, never detect it. Decision: **review mode is a layer the manager owns**, and while
  Clash Detective is open the panel carries a stated line about who owns the view. Ship two systems
  fighting over one surface and we rebuild, inside our own product, exactly the invisible-accumulation
  disease this feature exists to cure.
- **Consequence for the smoke matrix:** those Clash Detective writes do not exist on Simulate, so the
  stack is genuinely *more* stable on the four Simulate rows than on the four Manage rows. The 8-row
  matrix currently asserts parity everywhere. It needs an **appearance row that asserts the difference**,
  or it passes on the easy half and proves nothing.

**Performance and threading.**
- Rule and category scopes **cache their resolved membership**, show a live element count per layer, and
  offer re-resolve. Invalidated by the document generation counter (§1, item G1). A layer showing **0**
  is P15's silently-broken set surfaced for free in a panel the user is already looking at.
- Apply is **bucketed by value, not by item**: one `OverridePermanentColor` per distinct colour over a
  `ModelItemCollection` (the pattern in `OverrideTransfer.ApplyToDocument`). Additive edits paint
  incrementally, because a later call over the same items *is* later-wins-per-property. Removal,
  reorder-downward and scope-narrowing cannot, and repaint the affected scope union.
- **Every COM path here is STA-thread-confined.** "Batched with progress and cancel" therefore means
  pumping the UI thread, not backgrounding work: a large repaint freezes Navisworks for its duration and
  reads as a hang. Every such operation carries a **stated element ceiling and a refuse-above-it path**,
  not merely a progress bar.

**Portability.** The stack serialises — into `project.cwproj` and as a portable file — carrying **layer
names and ordering** (later-wins is meaningless without preserved order). Rule/category/set scopes travel
as rules and re-resolve against the target model; element scopes keep GUID-then-path fallback. The
**NotFound count surfaces in the panel**, because that is how a user learns the standard did not land
here, instead of learning it in a client review. A layer stack that cannot leave the project is not a
company colour standard.

**openBIM projection.** Ordered stack → BCF `Coloring` (ARGB, opacity folded into alpha, layer name as
legend text); visibility → `DefaultVisibility` + `Exceptions`, choosing whichever encoding yields the
shorter list; section box → six `ClippingPlanes`. Lengths convert to metres.
**Delete the three.js Y-up → IFC Z-up transform; do not port it.** `SavedViewExportMapper` maps
`(x,y,z) → (x,−z,y)` because its source frame is a browser viewer. Navisworks is already Z-up. Porting
it silently rotates every exported viewpoint 90° and would look entirely plausible in a unit test.

**In-view indicator.** One `RenderPlugin` (see §1, V1) draws a hidden/overridden count and active-layer
markers into the live view. The plan states flatly that there is no API to draw an overlay into a
viewpoint; that is true of a **saved viewpoint** and false of the **live view**. A panel you must look
away from the model to read only half-fixes an invisible-state problem.

---

## 1. What changes — everything now added

Scope column is the **narrowest shipping form**; that is what gets built, not the candidate's own
description. "Asked by" names the role that made the winning argument.

### A. Correctness and identity — these change the store, so they land before Stage 2

| # | Item | Narrowest shipping scope | Asked by | Why |
|---|---|---|---|---|
| **G1** | Document generation counter | One counter bumped on append/refresh/remove/document-change; every pane binds rows to `(generation, ElementKey)` not to live handles; one re-resolve path; one banner *"the model changed — 3 tabs re-resolved, 41 rows could not be re-found"*. `FakeDocument` test asserts no pane holds a live handle after a bump | API engineer | A `ModelItem` is a handle into a native tree. Appending the late structural NWC at 09:05 with the board, the stack and a takeoff open is the most ordinary action of the week. Cheap at Stage 1, a rewrite of 16 tabs' bindings anywhere after |
| **G2** | One `ElementKey → IFC GlobalId` function | Single Core function used by IFC export, BCF export and BCF import. Precedence: (1) the element's authored IFC GlobalId property, (2) `IfcGuid(InstanceGuid)`, (3) deterministic hash of source path + tree path — **never traversal index**. Per-component rung recorded; BCF export pre-flight prints *"N of M components carry a CamelWorks-derived id and will only resolve against a CamelWorks IFC export"* | openBIM | Three independent identity functions exist for the same element today. The failure is invisible from inside Navisworks — everything resolves locally — and surfaces as the designer opening our .bcfzip in Revit with nothing highlighting. Shipping the uncomfortable pre-flight sentence is the point |
| **G3** | One universal `Finding` record | Clash, manual issue, health, headroom, IDS and set-drift converge on one journal shape with parent/child (one rule failing N elements = parent + N children), stable cross-run key anchored on `ClashKey`/`GroupId`, canonical status. Drop the web product's byte-parity constraint and its tier/severity gating | openBIM | A **simplification**, not an addition: it is what makes headroom, IDS and Health findings worth having, because they inherit the board, the report and BCF instead of each growing a screen. Must land in §2.4 before Stage 1 |
| **G4** | Promote-only status merge | Monotone lattice `New < Active < Reviewed < Approved < Resolved` on **Status only**; assignee, due and priority stay latest-wins | API engineer / IT | §2.4 refuses to read native New/Active/Resolved back precisely because that would overwrite a human decision, then applies plain latest-timestamp-wins to the same field in its own fold. A stale journal landing late un-resolves a signed-off clash |
| **G5** | Published schemas + **preserve unknown keys** | Versioned JSON Schema for `project.cwproj`, `.cwset`, the journals, group records and `.bcmanifest`, in-repo and linked from Limitations. Every reader round-trips fields it does not understand via an overflow bag; one mixed-version contract test | openBIM | Per-writer journals mean two coordinators on staggered CamelWorks versions write the same folder. An older build dropping a newer build's field is **silent data loss inside the concurrency model**, in week one of any company rollout |
| **G6** | Storage-substrate probe and declared support | Detect local disk / UNC-DFS / recognised sync roots (OneDrive, SharePoint, Dropbox, ACC Desktop Connector). Refuse the `project.cwproj` lease where leases cannot work and fall back to journal-only **with the reason shown**; fold sync-client conflicted-copy filenames into the journal set; keep `undo/` and `snapshots/` off any sync root; name the support matrix in Limitations | IT deployer | Nobody keeps projects on a local disk. The ACC Desktop Connector case is the worst outcome the store can produce: a hidden sibling folder that is not synced means two coordinators with two working-looking boards, neither reaching the other, neither told. That is worse than the sidecar failing loudly. **0-R exit condition applies: a substrate we cannot stand up and test is named unsupported, not claimed** |
| **G7** | Local user identity | One-time bind of the Windows account to a Responsible Party (proposed by display-name/UPN match, confirmed once, changeable). Feeds journal authorship, `Mine` / `Assigned to me`, BCF author/modifier, the report's reviewed-by line, and the 2026 clash API's required `Assignee` | IT deployer | **Blocking.** Spike 0-S1 must port to the 2026 required-`Assignee` shape and nothing in either document says where a current-user value comes from — the spike cannot complete. Separately, a 214-row board you cannot filter to your own name is broken for everyone except the person who did the assigning. "No account" was applied one level too broadly. Invariant: an unrecognised assignee is always shown, never silently dropped |
| **G8** | Merge-not-stomp property writer | Rebuild the user-defined tab from its existing contents before `SetUserDefined`, batched over a scope, behind `IModelWriteTransaction`, pre-edit sidecar snapshot on every call. **The hard-coded tab index `0` becomes a real index lookup.** Decide before shipping what an item with two same-named tabs does — the loop currently breaks after the first match | API engineer / trade | `SetUserDefined` replaces an **entire tab**. Five features write custom properties; a naive writer silently destroys the other four's data, outside Navisworks' undo, invisible until a report column reads blank. The comment on the source is earned, not aspirational — it was learned by breaking it |
| **G9** | Clash-test write gate, extended | The per-test confirm naming the result count already required for **create** now also gates **update, delete-orphan and tolerance/set-binding edits**, and every one snapshots the affected results into the sidecar first | API engineer | A whole-test replace destroys the results and with them every `ClashKey` attached — the GATE's subject, the carry-over banner's input, the Δ column's baseline and the freeze record's evidence. Everyone guarded create and nobody guarded the other three |
| **G10** | Per-year API-surface manifest | Enumerate by reflection the exact signature of every Autodesk API member CamelWorks calls; commit one manifest per year; **fail the build when a year's manifest changes** | API engineer | Every version break in this entire document was found by someone hitting it in production: `SetStatus`/`Assign` gaining `Assignee` in 2026, `SetSectionBox` dying in 2025, batch switches drifting, Appearance Profiler flipping format in 2026. Nothing proposed detects the next one. This is also the honest answer to the per-year cost the batch step carries |

### B. Deliverables — the report leaves the building

| # | Item | Narrowest shipping scope | Asked by | Why |
|---|---|---|---|---|
| **R1** | Setting-out coordinates on every row | Signed offsets derived from the grid **intersection lattice** (project the point onto the two nearest orthogonal intersection runs), level-relative elevation from the named datum, and raw project XYZ as a fourth column. Sign convention printed on the sheet. Degrades to raw XYZ **with a named reason** where there is no grid. **One line added to a Stage-0 spike: is per-gridline geometry exposed on all four years?** If yes, upgrade to true perpendicular-to-plane offsets | MEP trade | The single strongest "does this win" item produced. Every deliverable currently ends at a picture with a nearest-intersection label, and a picture cannot be installed from. Nearest-intersection is navigation; a signed offset is an instruction. Nobody in the room had ever had to reproduce a position with a tape. Budget the fallback text and the multi-building case, not the arithmetic |
| **R2** | Typed resolution instruction | One record on the group and on manual issues: `{action ∈ move | resize | reroute | hold | other-trade-moves, subject: ElementKey, direction ±X/±Y/±Z, amount, note}`. Set from review mode (a sixth `Annotate` key). Prints as one bold line under the group image and renders as **one arrow** by the vector callout layer. A group with none prints *"resolution not stated"* | MEP trade | Every output in the plan tells the trade there is a problem — the one thing they already know, because they are looking at their own duct. This puts the answer in the data model where it is filterable, countable and checkable. **Hold the enum to five verbs**; past that it is a taxonomy nobody fills in |
| **R3** | Field pack | An output **mode** of the report engine: portrait, one issue per page, large type, image + R1 coordinates + R2 instruction + a sign-and-date block, filtered by work area, plus the same content as a flat XLSX. No new tab, no ribbon button | MEP trade | The report is specified end to end for a projector in a meeting room. Nobody had watched a foreman pinch-zoom a 40-page landscape PDF one-handed on a lift. **Its author's condition is honoured: it ships with R1 or not at all** — and R1 ships |
| **R4** | Model manifest on the report | Cover table + XLSX sheet: per loaded model — display name, source file, full path, source last-modified UTC, size, units, transform-applied flag; plus test names and `lastRun`, CamelWorks version, profile identity | Competitive | *"Which revision of the structural model was this run against?"* is the first question in the Wednesday meeting and the only question six months later in a dispute. Today the answer lives in the coordinator's head. The whole review optimised the inside of the report; nobody looked at it as a document that leaves the building — which is what it is for |
| **R5** | Per-party open/closed with a delta | **One** cover block and one line in the Triage header: open and closed per responsible party for this run, with the previous snapshot's open count in a Δ column. **Exactly two snapshots, never a series.** Both snapshot timestamps and the scope printed beside it; the row is **omitted, never zero-filled**, when there is no prior snapshot. No chart, no compaction format | VDC / strategist / competitive | The block the project director reads and often the only one. All three objections in the original cut were about the *chart*; none touched the number. Without it the coordinator counts it by hand in Excel every Wednesday |
| **R6** | Locator / key-plan image | One **top** orthographic render fitted to the federation bbox, generated **once per report**, with each group's centroid marked as a vector by the callout layer already being built. Stated behaviour when the model bbox is a 400 m site and the group is 300 mm. **No front elevation.** Current view captured and restored around the render; cancellable | Competitive | R1's coordinates degrade to raw XYZ where there is no grid, which the plan already says is common in DWG/IFC-sourced NWCs — a plan image is exactly what works there. It is also why teams rebuild the deck in PowerPoint with a plan and an arrow |
| **R7** | Redline **text** read | `SavedViewpoint.Redlines` → `LcOpRedlineText.GetText()` only, printed as a quoted line under the group image. **No geometry, no positions, no coordinate calibration, no authoring, no write path ever.** Falls back to today's pre-flight warning if the read throws | API engineer (narrowed) | The factual correction stands and is verified: redlines are enumerable through typed wrappers Autodesk ships, and Dyncamelo reads them today — so the pre-flight warns about a limitation we do not have. But the *words* are the instruction; calibrating an undocumented markup coordinate space across eight host rows, whose failure mode is plausible garbage only golden images catch, is not worth a week of Stage 2 critical path |
| **R8** | One unit formatter | `Core/Units`: `formatLength / formatArea / formatAngle / formatDelta`, metric and imperial, signed deltas, `"—"` never `NaN`. Adds **fractional inches to a settable denominator** and a **mm-only metric mode**. Every screen and every export calls it; no feature formats a number itself. **Unit choice is a project profile setting, not a Navisworks display-unit follower** | MEP trade | A report that prints `2.34` to a fitter holding a tape is useless, and nobody on any site has a tape with a 3.28 ft mark. The shop sets the units, not whoever built the NWF |
| **R9** | Deliverable identity | `{Rev}` and `{Date}` filename tokens beside `{Party}`, and a **"supersedes"** line on the report cover | Judge (missed by all roles) | The most common cause of a trade building from the wrong information is a PDF in an inbox that looks exactly like last week's — the failure R1 and R3 exist to prevent, reintroduced at the filename. One token and one line |

### C. openBIM — the boundary where the accountability model leaves the company

| # | Item | Narrowest shipping scope | Asked by | Why |
|---|---|---|---|---|
| **X1** | Real BCF 2.1 **and** 3.0 | Full Topic payload — `AssignedTo` (**email**, which makes email required on Responsible Parties), `DueDate`, `Priority`, `Labels` from Level/Zone/Discipline, `Stage`, `ModifiedDate`/`Author`, `ReferenceLink`, `RelatedTopic` — plus `project.bcfp` and a per-project extensions declaration. Default 2.1, 3.0 as a dropdown. Two conformance fixes on the way in: 3.0 uses `extensions.xml`, and 3.0's `project.bcfp` carries no `<ExtensionSchema>` | openBIM | The engine CamelWorks would otherwise inherit emits Guid/Type/Status/Title/Dates/Description/Comments/Selection and **nothing else** — verified. So the entire accountability model the product exists to build is unwritable at its only external boundary. A per-party BCF arriving with `AssignedTo` blank is indistinguishable from an unassigned one |
| **X2** | **Deterministic topic GUID** | Derived from `(project id, GroupId)`, so re-exporting the same unresolved group updates the same topic | openBIM | Without it every Friday export duplicates the whole board in the recipient's BIMcollab/Revizto/ACC and the round-trip status map is noise by week four. A product-killing defect the customer discovers, not us. No other candidate named it |
| **X3** | Viewpoint payload that shows the issue | `Visibility DefaultVisibility=false` + `Exceptions` isolating the group; `Coloring` from the appearance stack; **`ClippingPlanes` sourced from `ActiveView.GetClippingPlanes()`** and `SectionBoxJson.Parse` — the documented cross-year **read** surface already used by the exporter on every target year, so this half is **independent of 0-S2**, which concerns writing | openBIM | A topic with a camera and a selection opens in Revit showing the whole federation. A topic with visibility exceptions and six clipping planes opens showing the clash |
| **X4** | IDS **check** | Import a client `.ids` and run it — as a rule source inside `Project ▸ Health ▸ Data` and as the **IFC export pre-flight**, resolving the entity facet through the export's set→IFC-class map (`Data/TypeMapping.cs`) and the property facet through `Data/ParamMap.cs`. **On the conformant engine** (`BIMCamel.Core/IfcIdsChecker.cs`), rebound off Xbim onto `IModelItem`. `attribute` and `partOf` **declared unsupported in the UI and in Limitations, never silently passed**. Applicability facets compile into a `Search`. Navisworks-set scoping kept but bound in `project.cwproj`, so the `.ids` stays byte-valid for every other tool. **No authoring UI, no COBie template, no new tab, no ribbon button** | openBIM / API / IT | The one place a free Navisworks add-in is *first in a category* rather than cheaper in an existing one, it is what clients now attach to EIRs, and it works identically on all eight rows. *"Drop in the file the client sent and press check"* is a much shorter conversation than *"now configure twelve rules."* **Engine choice is not stylistic:** the alternative handles three of six facets, reads pattern-only restrictions so an enumeration or a numeric bound silently becomes a pass, matches entities by **substring against `ClassDisplayName`** (so `IFCWALL` matches `IFCWALLSTANDARDCASE`, against "Basic Wall"), and writes files rejected by every other IDS tool for a missing mandatory `ifcVersion`. A checker that passes on a facet it did not evaluate certifies a model against a requirement nobody checked |
| **X5** | Classification identity in the IFC export | A Classification section in the export profile: `Name`, `Edition`, `EditionDate`, `Location` URI, plus a code/title split rule so `Identification` carries the code and `Name` the title. **No bSDD dictionary seed, no live bSDD API, no IFC4.3** | openBIM | The client's checker fails a model where the data is genuinely present, because our exporter collapsed code and title into one string and declared no system or edition — and the coordinator gets blamed for a formatting decision we made. Four strings into two constructors that already have `$` slots. Declaring IFC4 honestly beats declaring 4.3 dishonestly |
| **X6** | IFC change **list** | `RevisionManifest.Diff` carries four GlobalId **lists** instead of four ints; the exporter writes `<model>.ifc.changes.csv` beside the `.bcmanifest` with `(GlobalId, state, IFC class, type name, level)`. **Manifest header goes to version 2 with a v1-tolerant reader**, plus a golden case in 0-S4 | openBIM | §6 named this manifest as Compare Models' replacement. Verified: `Diff` carries four ints and `Report()` prints four numbers. "MODIFIED 1,412" is not something a coordinator can act on. This makes a cut honest rather than reopening it — and the buried hazard is the real content: `Load()` rejects unrecognised headers, so a naive version bump makes **every existing exporter user's next export report 100% NEW** |
| **X7** | Self-describing artefacts | Every CamelWorks-authored artefact written into the model carries a plain-text statement of its own definition. One line in the Definition of Done | openBIM / strategist | See §0.1's two-authority-rules amendment. The cheapest available answer to the abandonware risk and to the lock-in objection Revizto's field reps use |

### D. The multi-party loop — one merge path, not five

| # | Item | Narrowest shipping scope | Asked by | Why |
|---|---|---|---|---|
| **M1** | **One** merge engine and **one** conflict-preview surface | Both **inbound BCF** and **inbound reviewer files** route through it. Policy `PromoteOnly` (default) / `LastWriterWins` / `ManualAlways`, set in `project.cwproj` and lockable by company policy. Comments union by id with author and timestamp. Ambiguous rows surface as a **reviewable conflict list in the Triage tab** in the carry-over banner's visual language, never a modal, never auto-picked. The merge lands as a **foreign journal with provenance** (source file, reviewer, imported-at) folding through the normal deterministic path — §2.4 gains a third writer state. Re-keyed from Navisworks `ClashGUID` onto `ClashKey`/`GroupId`. Exchange file stamped with the snapshot id it was cut from and **refuses with a named reason** rather than silently merging against a moved model. Merge runs in Core, so it works on Simulate. The report's audit line can say *"12 statuses set by MEP-sub via import, 14 Mar"* | Six roles, five candidates | **BCF import is currently the only write path in the product with no preview** — Excel import previews a diff, native status readback surfaces a conflict list, Assign rules refuse to overwrite an existing assignee. Two subcontractors returning topics on one group is silent last-import-wins over a human decision. `PromoteOnly` encodes the field rule that a re-import must never un-resolve something. **Whether a returned file may reopen an Approved clash is a company decision made once, not a checkbox sixty people meet in a wizard** |
| **M2** | XLSX response leg | Export one party's open issues as a keyed worksheet — short **opaque IssueId**, group, location, description, status, due, plus two empty columns (Response, Response status). Import maps by IssueId through M1's preview and policy. Identity columns protected. **Unmatched rows are a visible decision, never a silent drop** | MEP trade / VDC | BCF assumes the far end runs Revit with a plugin. Two thirds of real trade responses come back as prose in an email and get re-typed on Thursday — the least skilled recurring hour in the coordinator's week. The fabricator will not install anything for you |
| **M3** | Coordinate workspace on Simulate | The **same** workspace, whose columns depend on what the store contains rather than on host edition. Board, groups, clash rows, status, assignee, due, manual issues, review mode, comments, saved viewpoints, per-party report, BCF in and out — all hydrated from the sidecar. **Refused, named plainly at the top: run or create a clash test, and project status back into Clash Detective.** Those two and only those need the clash DLL | VDC / competitive | Four of eight supported hosts currently open the largest new subsystem as an explanatory panel — half the installed base told the product they installed does not do the thing it is for. Nine Simulate seats to two Manage seats is the normal shape of a coordination team. §2.4 already makes the store authoritative and the model a projection; nobody followed that sentence to its conclusion. **It is also the architectural item:** §2.2's rule that no board/report/review signature may name a clash type is today enforced by discipline and tested by nothing, because the workspace never loads on the four Simulate rows |

### E. Findings the board did not have a producer for

| # | Item | Narrowest shipping scope | Asked by | Why |
|---|---|---|---|---|
| **F1** | **Headroom** check | Named **Headroom**, not clearance. Floor set × target set as BetterSets expressions; required clear height in real units via `ModelUnitsPerMeter`; emits **findings into the store** (G3), never a private grid. Own carry-over key (floor key + target key + required value) or a re-run reopens every closed finding. **Per-row confidence flag** in the spirit of Takeoff's closed-shell flag; no export presents a bbox-derived clearance as measured. `RunCheck` stays a pure static; `ResolveItems` replaced by normal scope resolution. **No directional envelopes, no lateral pull-space, no support-availability check** | Six roles | Clash Detective structurally cannot answer it — nothing is intersecting, that is the point — and clear height is in nearly every employer's requirements. No competitor at any price has it. It touches no clash type, so it is real coordination analysis on the four Simulate rows. **Call it headroom or we promise valve pull-space we do not deliver**, and a false promise on a safety-adjacent check is worse than no check |
| **F2** | Model intake register + bulk-Resolved attribution | Six scalar fields per model on the snapshot record already being written (source path, source timestamp, size, geometry-item count, bbox, units + transform) plus per-clash-test resolved scope size. Two readouts: a Project-tab intake list (what arrived, what is byte-identical, what is missing, who has not published) with **element-count Δ for the top ~10 categories**; and an attribution rule — *"Model MEP-L03 has 4,102 fewer geometry items than the last snapshot; 340 of this run's 512 Resolved involve it"* — raising a banner beside the carry-over banner and printing in the report. States evidence, never accuses. **Consumes no `ElementKey`**, so it sits outside the GATE and its failure branch | VDC / strategist | The GATE guards one door to a wrong "Resolved". This is the second door and nobody guarded it: a subcontractor exports a filtered view, six hundred clashes go quietly to Resolved, and it is found three weeks later in steel. It also answers the Tuesday question — *the architect issued a new model, what changed* — which neither existing home answers, since the Δ column only covers elements that clash and the IFC manifest only covers models **we** exported |
| **F3** | Restore model placement | Health lists the model whose transform changed, shows the computed delta, and offers **restore the previous placement** from the snapshot it already keeps — previewed before it writes, reversible after. **Health must list which models carry a CamelWorks transform override and offer to clear it** | API engineer | P12 is the classic silent killer nobody else solves, and the plan ships the detector one field short of the fix. The caveat must be in the UI rather than discovered: an override compensating for a bad source file **double-offsets** when the supplier fixes the source |
| **F4** | Cross-test duplicate collapse | One canonical board row per physical conflict, listing the other tests that returned it, sharing status/assignee/due/comments. **Collapse on the ordered `ElementKey` pair with a proximity band on the occurrence discriminator, sized from the larger of the two tests' tolerances** — not on exact `ClashKey` equality. Never silently picks a canonical result: the row names every contributing test and its tolerance | Competitive + API correction | Any matrix with overlapping sets returns one conflict twice; it is then triaged by two people at two times, producing two decisions and two lines in the client's report. It also makes the funnel readout defensible — *"1,910 → 412 suppressed → 214 groups"* cannot be defended if some of the 1,910 are one conflict counted twice. **The API correction is load-bearing:** two tests with different tolerances report different intersection points, so the same conflict can quantise to different discriminators and exact-key collapse would under-collapse invisibly |
| **F5** | One **Freeze** mechanism — sign-off *and* fabrication release | One immutable record `{scope, date, by, ClashKeys/GroupIds/ElementKeys present, snapshot id, hash}`. Sign-off is a freeze with a signatory; fabrication release is a freeze with a release id. One **Change Notice** screen listing frozen members whose position, size or geometry changed, with deltas, and an **always-visible unmatched count**; one export; a report section per frozen scope with dates and signatories. **No fifth Δ state on the board.** Generated **on demand with progress and cancel**; a refresh only marks a freeze stale. Hash defaults to bbox + transform + property signature, with the full rung-3 geometry signature as an explicit per-freeze opt-in that states its cost. On the GATE's failure branch it ships as an explicit accept-the-match step | VDC + MEP trade | Two roles proposed the same machinery from opposite ends of the contract. It is what turns a coordination *utility* into a coordination *record*, which is what keeps a tool installed at a main contractor after the novelty wears off. Its confidence surface is genuinely better than the cut Compare Models', because a freeze names its members so the unmatched list is bounded and listable. **Two hard rules:** never claim "nothing changed" without printing how many elements could not be matched, and no fifth Δ state — a false post-sign-off flag is a commercial accusation, and the Δ column is the most trust-critical field on the board |
| **F6** | Penetration / sleeve schedule | A **report + XLSX template** over an existing service∩structure group filter, carrying R1 coordinates, service size and system, host element and type, and **one** status field. **No derived opening size, no host-thickness inference, no request→approved→cast state machine** | MEP trade | On a concrete frame the pour date is the one immovable date on the job, approving a clash does not put a sleeve in the deck, and this schedule is rebuilt by hand in Excel on every project. But the two costly halves — a per-service annular-allowance table and a host thickness that is wrong for any non-orthogonal wall — are a second domain model. The deliverable is a schedule, not a workflow |

### F. Setup and the weekly loop

| # | Item | Narrowest shipping scope | Asked by | Why |
|---|---|---|---|---|
| **S1** | Clash matrix from the project's own spreadsheet | User-configurable sheet mapping (row-header block, column-header block, blank-spacer skipping), body columns matched to categories **by number first, set name second**; legend entries carrying test type and tolerance. The mapping **persists in `project.cwproj`** so a re-import next month uses the same layout. The sheet's own test numbering carries into test names. Feeds the existing **create / update / unchanged / orphaned** diff-and-preview — never `ClashTest.Create` directly. Tests bind to saved search sets | Four roles | The matrix is a contractual artefact agreed in the BEP before anyone opens Navisworks, and revised twice. Retyping a 30×30 grid does not happen; one mistyped cell is a discipline pair nobody clashes for the life of the job, with no symptom until handover. **The configurable mapping is the whole difference between a feature and a demo** — it reads the client's template instead of demanding ours. Note the positioning correction: 2027 ships multi-test creation natively, so "create many tests" is now a **parity** claim; the differentiator is generate-from-the-sheet, diff, and refuse to destroy results |
| **S2** | Minimal read-only XLSX reader (Core) | Unzip, shared strings, one sheet, cell values and number formats; CSV alongside. netstandard2.0, Linux-testable against a committed corpus of real client sheets. **Plus a CI check that fails on any non-Microsoft DLL in the installer payload** | IT / openBIM | One component behind three shipped features (S1, M2, Data properties-in). Named as a **real 600–900 line line item**, not assumed to fall out of the writer — reading someone else's workbook is a harder problem than writing your own. The payload check is an afternoon and would have caught a live Polyform-Noncommercial binary being redistributed today by a plug-in that never calls it |
| **S3** | Template-pack folder | A configurable root holding folders of the files the plan already defines — set recipes, matrix + mapping, rules stack, appearance layer profiles + palette, report templates, Responsible Parties, IDS files, code tables, headroom rules. `Project ▸ Profile ▸ New from template` reads from it. **No versions, no drift report, no three-way section diff, no accept flow** | IT deployer (narrowed) | Emailing files to sixty people is not a distribution mechanism. The versioned-library half is declined in §2 |
| **S4** | Named code tables | A `project.cwproj` section (code, description, optional parent), seedable from a template pack. One Data preset **"assign code from table"** that writes only listed values, refuses the rest, and reports coverage — *"3,204 of 3,610 coded · 406 uncoded · 0 invalid"*. One Health check for out-of-table codes | IT deployer | A lookup that silently passes an unlisted value is not governance. Three spellings of one system code split clash grouping, per-party reports, Takeoff buckets and the IFC classification export simultaneously — and a single coordinator keeping one project internally consistent is the common case, so it earns its place without a central library |
| **S5** | Ancestry calculated column | One calculated-column type in Data: `WBS_1..WBS_n` plus a concatenated path from `ModelItem.Ancestors`, configurable depth, skip-list for noise levels. **Recomputed every time, never stored as an identity, no cross-revision link** | API engineer | Takeoff can only group by properties that exist, and the hierarchy a federation actually has — Building > Level > System > Type — lives in the tree, not in any property. Carries none of the second-fingerprint problem that got the BOQ tab cut. Its one support question has a true and useful answer |
| **S6** | Home is the weekly cycle | Home becomes **"This week"**: one *Issue this week* action driving reconcile → rules → group → triage → report → BCF as a sequence, plus a staleness readout — tests not re-run since the last issue, groups with no R2 instruction, parties with nothing sent, models not refreshed. Replaces the card wall | Judge (missed by all roles) | The cycle is performed in the same order every Wednesday across five tabs and the plan makes the coordinator remember and re-drive it by hand. It is also the answer to the surface problem: one front door instead of six workspaces on first launch |
| **S7** | Free-text search on the board | One text box over group name, element name, comment text and assignee | Judge (missed by all roles) | The most-used control in every issue tracker on the market and it is not named once in either document. In the meeting someone says *"what about the riser at grid F"* and the coordinator has three seconds |
| **S8** | Visible undo on the board | Undo the last N board writes (status, assignee, due, group membership) with author, from the journals | Judge (missed by all roles) | Undo is scoped throughout to model writes; *"Ctrl+Z does not undo a CamelWorks operation"* is stated three times about the model. The likeliest in-meeting embarrassment is a status set on the wrong group in front of eight people, and there is no named way to take it back |

### G. Deployment, support and the view

| # | Item | Narrowest shipping scope | Asked by | Why |
|---|---|---|---|---|
| **D1** | `CamelWorks.Boot` load diagnostic | A separate PackageContents component referencing **exactly one assembly** (`Autodesk.Navisworks.Api`) so it cannot itself fail to load. Probes for the real assemblies, checks the built year against `Application.Version`, and on failure shows one dialog naming the file and the reason and writes a log beside the bundle. Owns the forward case (*"CamelWorks 1.0 supports 2024–2027"*). **A CI check fails the build if it acquires a second reference** | API engineer | The Navisworks loader is silent on failure: a missing dependency, a wrong-year build, a `Zone.Identifier` stream, a bad `SeriesMin/Max` and a `ReflectionTypeLoadException` all produce one symptom — no ribbon tab, no dialog, no log. The plan's answer to *"is it working"* lives inside the add-in that did not load. Highest leverage per line in the product |
| **D2** | Machine-wide install + policy file + signed assemblies | (a) An all-users install to `%ProgramData%\Autodesk\ApplicationPlugins` alongside the per-user path, with a machine-scope Apps & Features entry — **verified per year on all 8 rows, not assumed**. (b) `camelworks.policy.json` in `%ProgramData%`, **merged under the user's own settings and shown in the UI as "from company policy"**, except three keys which are **hard-locked**: the Dyncamelo zero-touch Packages folder, the update-check host, and the external data source. Locked settings render as locked-by-policy **with the reason**. (c) **Authenticode signing of the shipped assemblies, not only the installer** | IT deployer | A per-user bundle in a user-writable folder is right for the curious individual and a hard stop for the standardising company — and the standardising company is the only outcome that beats Revizto, which is sold as a company-wide purchase. Unsigned assemblies are what AppLocker publisher rules and EDR react to; without signing the WDAC shops cannot deploy at all. **Doubling the install cells QA verifies is the honest price and the largest part of this cost** |
| **D3** | Zero-touch Packages folder **off by default** | Off unless explicitly enabled; where enabled, require a signature. Lockable by D2's policy file | IT deployer | **A defect fix, not a feature request.** `DyncameloHost.LoadPackages` calls `Assembly.LoadFrom` on every `*.dll` found **recursively** in a user-writable directory, with no signature check and no off switch — arbitrary code execution into the Navisworks process on every machine. It ends the security meeting before the product is discussed, and "no telemetry, no account" does not answer it |
| **D4** | Installer: hard block + PowerShell-free MOTW clear | Hard block with a non-zero exit when Navisworks is running (plugin DLLs are locked). Clear `Zone.Identifier` with PowerShell **and then unconditionally again in plain cmd** — not as a fallback, since the PowerShell call can exit successfully while doing nothing. Distinct documented exit codes on the silent path: host-running, payload-missing, target-not-writable, success | IT deployer | A warning instead of a block means the third of a fleet with Navisworks open silently keeps the old build while the deployment **reports success** — bug reports for a bug that was fixed, invisible to both sides. And Constrained Language Mode estates, which are the target buyer, are exactly where the PowerShell path fails |
| **D5** | Support bundle (with the local usage log folded in) | `Help ▾ ▸ Diagnostics ▸ Copy support bundle`: version and build, host year and edition, install scope and path, policy file in effect and its resolved values, capability-probe result, template-pack version, sidecar schema versions, last 50 lines of run/journal logs, self-test result — **and** an append-only local command-count log (command, timestamp, scope size, duration, outcome). Excludes model data, property values, element names and any path outside the project root. **Shows the user the contents before writing.** The log lives in per-user local application data, **outside the project sidecar and outside any sync root**, and never enters the journal fold | IT deployer / strategist | The self-test answers *"is the build broken"*; this answers *"what is this machine running"* — and configuration is what varies across eight host configurations, two install scopes and a policy file. With no telemetry it is the only channel by which machine state reaches the two people who can act. **A local, visible, user-owned log is not telemetry** and makes the promise stronger, not weaker — but the copy must be exact, and one artifact with one trust paragraph is safer than two |
| **D6** | `.camelworks/status.json` + portfolio rollup | Counts only, rewritten on events that already fire (board save, report export, health run, job completion): last clash run per test scope, open/closed/overdue per party, manual-issue count, health score and readiness verdict, models refreshed since date, template-pack version, CamelWorks version, host year and edition. Read as an **XLSX/HTML digest** over a project-root list through the existing report engine. **No Portfolio pane tab.** Stale or newer-schema rows show as stale with their date, never hidden | IT deployer (narrowed) | Reading N small JSON files off a folder needs none of the server, database or dashboard the product correctly refuses, and it absorbs the useful half of the cut burn-down with no chart path. It also lets any customer build their own view. The write half is nearly free; a twentieth tab is not |
| **V1** | One `RenderPlugin` live overlay | In `CamelWorks.Nav`, **per-document instance state, never static**. Signatures carry plain `Point3D` + colour + label so nothing names a clash type. Exactly three draws: the Appearance Manager's hidden/overridden indicator, board-filter-scoped status markers, and review mode's HUD **display**. Pre-resolved draw list computed **outside** the callback, hard cap with a visible *"showing 500 of 3,200"*, ribbon off-switch, off in report images by default. **Any text-bearing surface is gated behind a Graphics-text probe added to 0-S5, with bare markers as the named fallback.** No OBJ bake in any form | Five roles + API correction | The plan's *"there is no API to draw an overlay into a viewpoint"* is true of a **saved viewpoint** and false of the **live view**. The board is a grid and a grid cannot answer *where are the problems concentrated*, which is the question that decides which zone gets a session this week. **Two corrections that must not be lost:** `RenderPlugin.Render` receives no mouse or keyboard events and does no hit-testing — it removes the always-on-top-window half of Risk 4 and **leaves the input half exactly where it was**, so 0-S5 stays open; and the OBJ path appends a model to the user's federation, dirtying the NWF and minting elements that `ElementKey`, Health's duplicate-append check, Takeoff's counts and F2's intake register must all then reason about |
| **V2** | Section box: re-point 0-S2, and a non-destructive toggle | **Two changes.** (a) 0-S2's question order is rewritten — see §6, item 1. (b) Ship the **toggle** unconditionally and independently of the spike: keep the last applied box in the sidecar, `Clear` becomes a toggle, add an explicit `Discard` | Three roles + judge | Section Box is named the highest value-per-line item in the product, review mode's group-extent boxing depends on it, and it currently works on one of four years. The toggle has **no API dependency at all** and is used dozens of times in one review session — box the group, decide, glance at context, re-box — and today the only exit is destructive |
| **V3** | Out-of-session **Convert / Federate** step | **One** `.cwjob` step, run from an open session, shelling Autodesk's own `FileToolsTaskRunner.exe` located by scanning both Program Files roots for both products, for file conversion / append / publish **only**, with `/log` and `/version`, exit code and captured output folded into the run record. Three conditions are part of the step, not guidance: (a) **the rewrite check is mandatory** — exit code 0 over a byte-identical output file is reported as **FAILURE**, never Done; (b) the step **refuses with a named reason** when no runner is found or when the located exe's version is outside the tested set, instead of trying anyway; (c) it gets its own row in the 8-host matrix including 2027. Clash, rules, grouping, report, BCF and IFC stay in-session and the job validator refuses to sequence them across the boundary. **No scheduler, no service, no CamelWorks executable, and no `.cwjob` command-line trigger.** Optionally, CamelWorks **generates** the task-runner command line and hands it to the user to schedule themselves. Limitations states plainly that the spawned runner consumes a licence for its run, exactly as the native Batch Utility does | Five roles + API narrowing | `Process.Start` with zero Autodesk references adds nothing to any of the 20 builds and, placed in Core, **reduces** host coupling — which inverts one of the three original objections rather than arguing with it, and it is Autodesk's own already-installed utility, which answers a second. Converting supplier files is the multi-hour half of the Friday federation and today it locks the coordinator's session. **The silent-success detector is the single most valuable line in the whole harvest:** the documented failure mode is `Init` failing `0x80080005`, the process exiting **zero**, and no output file — a Friday job reporting Done over last week's NWD, which is precisely the incomplete deliverable §4's Save-refuses rule exists to prevent, and nothing else in the plan detects it |

### H. Recorded rejects — decisions, so nobody re-proposes them

Four roles independently named the same import boundaries. They cost nothing and prevent real harm.

| Do not take | Why |
|---|---|
| `ClashRenamer` (renumber results to Clash1..n via `CreateCopy` + `TestsReplaceWithCopy`) | A dense 1..n ordinal is exactly what §2.3 forbids as a discriminator, for exactly the stated reason. And a whole-test replace for a **cosmetic** change destroys the results and every `ClashKey` on them — the GATE's entire subject — contradicting the per-test-confirm rule we are asking users to trust |
| `TeamMemberStore` (`%APPDATA%\...\team_members.xml`) | Per-machine, so three laptops produce three spellings of one subcontractor and per-party reports and per-party BCF split. Superseded by Responsible Parties in `project.cwproj` before it arrives. **Nothing about assignment may read a per-machine file** |
| `SphereExporter` / OBJ marker bake (`doc.AppendFile`) | Mutates the user's federation, adds a model to the Selection Tree, dirties the NWF and changes every downstream count including Health's and Takeoff's — a self-inflicted wound on the identity layer the whole product rests on, for a picture V1 gives free |
| `WbsWriter` under the name "WBS" | It stringifies the selection-tree path, which is not a work breakdown structure. Shipping it under that name misleads an estimator. S5 covers the real capability honestly |
| `FormaIssueStore` | Hard-binds a general product to one vendor's issue tracker; X1/M1 cover the need in a standard way |
| `ExportActiveObj` | References `Autodesk.Navisworks.Automation` — a host-bound assembly in all 8 install cells for a format the restored `IMeshWriter` path already reaches |
| Every third-party runtime package in both repos — ClosedXML, **EPPlus**, QuestPDF, SkiaSharp, Xbim, geometry3Sharp, MIConvexHull, SixLabors | §9 forbids a third-party runtime. **EPPlus is separately a licence problem, not a policy one:** EPPlus 5+ is Polyform Noncommercial, commercial use requires a paid licence, and it is redistributed in a shipped build **today** by a plug-in with no `OfficeOpenXml` call site anywhere. Putting that inside a product whose headline sentence is *"free to use, including commercially"* is a live violation sitting in the one sentence the entire positioning rests on. **Harvest pure logic files only, and check every ported file's `using` list before acceptance** |
| KVI's About-box disclaimer (*"not officially supported by … the IT department"*) | The exact opposite of what CamelWorks must say. A dialog disclaiming our own support gets the product banned on sight in the estate we most want |
| Ownerless `new Form().Show()` + `MessageBox` outcome reporting | An ownerless dialog drops behind the Navisworks main window on the next click and **reads to the user as a crash**. Parent every dialog to `Application.Gui.MainWindow`; report outcomes into the pane. Harmless in a 2,000-line tool, unsurvivable across sixteen tabs |
| **One rule lifted, not code:** CamelWorks **never hides a value it does not recognise** | Not in the assignee picker, not in a merge, not in a template diff. Silent disappearance of a colleague's decision is the exact failure the whole sidecar design exists to prevent, and hiding an unrecognised value is the cheapest way to cause it |

---

## 2. What we are not doing, and why

### 2.1 Declined outright

| Declined | Real reason |
|---|---|
| **Annotation canvas on the report image** (#1, #87) | The logic is sound — the original cut's reason is entirely about a native API that can silently no-op, and it does not touch a picture our own writer renders. It is still declined. A placement canvas is a screen where a user spends unbounded time on the first three groups and none thereafter — its own author names the time-sink as the main cost and a cap as the control. **R2 delivers the win it is actually chasing** — the instruction — as typed data that renders an arrow automatically, filters, counts, exports to XLSX and BCF, survives re-render at any size, and has nothing to place and nothing to re-anchor. Two developers should not build a drawing tool to say what one enum and a number say better |
| **Issue Pack NWD** (#22) | Genuinely good and honestly not in this release. One to two weeks over `CreateCopy`/`ReplaceWithCopy`'s sharp edges plus N `SaveWithOverrides` captures on the slow path, with Freedom's comment behaviour unverified and its per-view section contingent on 0-S2's unanswered half. Its two audiences are already reached: Simulate seats get the real board (M3), the foreman gets the field pack with setting-out coordinates (R1/R3). **If scope frees up this is the first thing back in** |
| **Versioned standards library with per-project drift reporting** (#47) | Priced honestly by its own author at 3–4 weeks, and more if the section diffs are done well, because **every settings surface in the product grows a version stamp and a three-way compare** — a tax on every screen for a report one person consults occasionally — plus a ticket class that does not exist today (*"my project says v4 and the update button does nothing"*). Two developers shipping everything at once cannot carry a governance product inside a coordination product. S3 ships the distribution half; the BetterSets findings buried in the candidate ship in §0.1 |
| **Appearance Profiler profile import** (`.dat` and `.xml`) (#81) | **Overturning the API lens, which would have shipped the XML reader.** Few teams have curated Profiler profiles worth migrating — the Profiler is weak enough that most people colour ad hoc, which is why P16 exists — so this is a one-time importer serving a small population, with a partial-import failure on any multi-condition selector. It also adds a **per-year branch in the FILE layer**, an axis the 20-build matrix has never accounted for. Rebuilding ten rules in a panel designed to make that easy is fifteen minutes. **What ships is the free half: the positioning correction** — never claim Navisworks cannot order rules or hide from a rule, because 2026+ can. The honest differentiators are the foreign-state read-back, ad-hoc element scoping, five scope kinds, per-property later-wins, and named layers that print into a legend |
| **`.cwjob` command-line launcher / Task Scheduler path** (#44) | The cut automation host wearing a different coat. Something must execute that job, and it is either a CamelWorks executable — the cut host — or an interactive session, which is the thing the GPO forbids. It recreates *"it ran at 2 a.m., nothing happened, and there is no user in the room"* as a ticket class, and the candidate concedes the failure is silent-at-2am. That is the one of the three original objections that genuinely survives. **V3's optional "generate the command line and hand it over" gives the fleet owner the capability without us owning the unattended path** |
| **Live bSDD API integration** | It would be the only outbound network dependency in a product that promises no telemetry and must work on a site with no internet |
| **A private CamelWorks markup exchange schema** (#9, #39, #53, #83, #72) | Keep the workflow, refuse the format. CamelWorks will already carry BCF 2.1/3.0, the sidecar journals and native clash XML. A fourth thing that can disagree about one clash's status — the one no other tool can read and the one **we alone support across four years forever** — is a permanent liability for a two-person team. M1 runs the identical three-screen wizard over BCF |
| **IDS authoring UI** | The expensive half serving the rarest user, and the capability is the check. A client-supplied `.ids` is the common case |
| **COBie exporter, and COBie as a bundled IDS template** | A Navisworks federation has no `IfcSpace` table. A half-COBie labelled COBie is worse than none, and a template invites exactly the mislabelling its own proposer warns against |
| **IFC4.3 export** | Without alignment and linear placement, an "IFC4.3" file is IFC4 with a different `FILE_SCHEMA` token, and the infrastructure buyer it targets would rightly reject it |
| **Directional / lateral clearance envelopes and the support-availability check** | A second geometry problem with its own false-positive profile, before the simplest version has survived contact with a real federation. The support check ("no structure within M metres to hang from") cannot be expressed as a bbox test at all — a 40 m duct's bounding box is 40 m long — and needs sampling or ray-casting against a spatial index on the geometry surface Navisworks makes expensive. Genuinely good; a separate item with its own spike |
| **Cross-team round-trip parity gate with the BIMCamel web product** | The strategic idea is the strongest any role produced — the participation layer already exists, live, free, no login, and the add-in makes no network call because the user exports a file and uploads it. **So the export path ships and is named in the BCF tab, the Home cards and the guide.** What does not ship is a formal ≥90% parity commitment: this release is two developers shipping everything at once, and our ship date must not depend on another team's schedule. Conformance comes from X1/X3 |

### 2.2 Right answer, wrong reason — cuts that stand, restated

| Cut | Reason as given | Real reason |
|---|---|---|
| **Native redline authoring** | *"The redline API is undocumented so we cannot know which year breaks it."* Factually wrong on the read side: redlines are enumerable through typed `LcOpRedline*` wrappers Autodesk ships, and Dyncamelo reads them today | **Authoring** stays cut because a write path into an undocumented surface across eight host rows is a maintenance obligation with no user in the weekly loop who needs to author *in Navisworks*. The **read** was never justified and now ships as R7 — text only |
| **ReTree** | *"`ModelItem` parentage is not writable."* Possibly true, possibly not; 0-S6 was going to check | Close 0-S6 without spending the hour. Reparenting is the wrong answer to the real want: a rebuilt tree is destroyed by the next model update. **A nested folder hierarchy of live search sets is a browsable tree** that sits beside the Selection Tree, re-evaluates on refresh and survives the update — and it ships inside §0.1's bulk generation. The cut stands; the capability arrives by a better route |
| **BOQ tab** | Estimating is a different buyer, and a stable line-item link is a second fingerprint problem | Both true and both stand. But the cut also removed the **tree-derived hierarchy**, which has nothing to do with estimating and which Takeoff needs. S5 restores it as a recomputed grouping key that is explicitly never an identity |
| **Compare Models** | Bounded by the fingerprint GATE with no confidence surface; replaced by the Δ column and the IFC manifest | Stands. But the named replacement did not exist — the manifest emitted four integers. X6 makes the replacement real, and F2 answers the model-level question with no `ElementKey` consumption at all |
| **Multi-run trend chart** | Longitudinal analytics, a snapshot compaction format, a chart path through a hand-rolled PDF writer | All three are the **chart's** costs. The chart stays cut. R5 ships the number, which is what actually gets read out loud |
| **`CamelWorks.Batch.exe`** | Licence seat, host-bound assembly in 8 cells, no log | Two of the three do not survive contact with a `Process.Start` on Autodesk's own utility. The **licence** objection survives in changed form (a spawned runner consumes a seat for its run — stated on the IT page, not claimed away), and the **unattended-schedule** objection survives whole. V3 ships the step; the host and the scheduler stay cut |
| **Self-contained web-viewer package** | Third-party runtime; nobody opens an unsigned zip | Stands, and note what it missed: the self-contained viewer package this audience already trusts is an **NWD** — which is why #22 was a real proposal and why it is declined on budget rather than on principle |

---

## 3. Splits — where the lenses disagreed, and my call

| Item | Usage lens | API lens | Release lens | **Decision** |
|---|---|---|---|---|
| Annotation canvas (#1/#87) | narrow to typed data | ship (no host API, so it cannot no-op) | decline | **Decline.** The API lens is right that it is *safe* and wrong that safe means affordable. The cost is a screen with unbounded dwell time on the Stage 2 critical path plus a golden-byte case, for an outcome R2 delivers as one record |
| Redline read (#16) | ship (invisible to the user) | ship (failure branch = today's behaviour) | narrow to text only | **Text only.** Two lenses were reasoning about safety; the third was reasoning about the week the calibration costs. An 8-row calibration of an undocumented coordinate space whose failure mode is *plausible garbage* is not worth Stage 2 critical path when the words are the value |
| Exchange format for multi-party review | narrow: no private file | ship the engine | narrow: BCF only | **BCF only, one engine, one conflict surface (M1).** The API lens was judging the engine, which is excellent; the format is the liability. Note what the API lens contributed that the others did not: re-keying off `ClashGUID` is *required*, because Navisworks clash GUIDs do not survive a re-run and merging on them silently reattaches to the wrong result |
| In-view overlay (#10/#20/#41/#82/#97) | narrow: markers + HUD | narrow: markers only, HUD claim over-promises | narrow: two draws | **V1, three draws, with the API lens's correction stated in the spec.** The release lens wrote "this removes most of Risk 4" and that is half true: `Render` has no hit-testing and receives no input, so it removes the always-on-top-window problem and leaves the message-filter problem untouched. **0-S5 stays open and its scope is unchanged; only its consequence-if-it-fails shrinks.** Writing it up any other way would close a spike that is still open |
| Task Scheduler / unattended (#44) | narrow: no scheduler | narrow: generate the command line for the user | narrow: no CLI | **The API lens's version.** It is the only one that gives the fleet owner what he actually needs — an unattended overnight conversion — while keeping every objection answered, because Task Scheduler runs Autodesk's exe with no CamelWorks process anywhere. Honest about what is unattended instead of pretending the need does not exist |
| Freeze / sign-off (#6, #36) | narrow: one mechanism, sign-off flavour | ship | narrow: one mechanism, no fifth Δ | **F5: one mechanism, no fifth Δ state, on-demand Change Notice.** The API lens was right about the machinery and did not price the hash: rung-3's geometry signature requires reading geometry, and `GenerateSimplePrimitives` is documented as 82–92% of export wall-clock, so hashing on every refresh turns a routine refresh into a multi-minute stall |
| Clearance (six candidates) | narrow: headroom, scoped, confidence-flagged | ship the general envelope | narrow: headroom only | **F1, headroom only.** The API lens is right that a directional envelope is a parameter change and not a new algorithm — and wrong that this makes it free, because the cost is a per-category rule table nobody completes across forty element types, and an incomplete rule table produces an incomplete check that reads as a passed one |
| Profiler import (#81) | decline | ship the XML reader | narrow to copy | **Decline both readers — overturning the API lens.** Its own reasoning contains the answer: the `.dat` path adds a per-year branch in the FILE layer, a new axis in the build matrix. The XML reader avoids that but still serves a small population with a partial-import failure mode, in a release whose binding constraint is surface |
| Usage log (#93) | narrow: fold into the support bundle | narrow: move it out of the sidecar | ship | **All three, combined: D5.** One artifact, one trust paragraph, one exclusion list, stored in per-user local app data outside any sync root. The usage lens is right that two surfaces double the chance a user reads one as telemetry; the API lens is right that a per-user file inside a multi-writer journal folder on a sync client is a churn generator |
| Duplicate collapse (#79) | ship | narrow: proximity band, not exact key equality | ship | **The API lens's version.** The other two were judging value and missed a correctness bug that would have shipped: two tests with different tolerances report different intersection points on the same pair, so exact `ClashKey` equality **under-collapses invisibly** — which makes the funnel indefensible in the opposite direction |
| Bulk set builder (#55) | ship | ship | narrow: fold into library import | **Fold in.** Three separate ways to author a set in one release is surface the triage loop pays for. The genuinely valuable part is not the builder — it is the audit report (Created / SkippedExisting / Failed, per line, with a dry run and skip-existing as the default) |
| Portfolio (#48) | narrow: no tab | ship | narrow: no tab | **D6, no tab.** The API lens is right that it costs nothing at runtime and wrong that cost is the question. A twentieth tab in a product whose measured adoption risk is cold-start surface is the wrong place to spend it |
| IDS engine choice | narrow: conformant engine only | ship the capability, either engine | narrow: check only, conformant engine | **X4: conformant engine, check only.** The API lens noted the subset engine is Core-clean and cheap; that is true and irrelevant. A checker that passes on a facet it silently dropped is worse than no checker, and the support surface it predicts — *"why does my IDS pass in Solibri and fail here"* — is the symptom of the defect, not a cost of doing business |
| IDS authoring | narrow: no authoring | *"authoring is cheaper than claimed — validate against the XSD in CI"* | narrow: no authoring | **No authoring.** The API lens is right that the XML writing is small. It is not right that small means free: it is a screen, a help page, a Limitations entry, a golden-file suite and a conformance obligation, serving the user who appears once per project and who mostly receives the file rather than writing it |

---

## 4. The revised cut list — §6 as it should now read

**Cut, and staying cut** (reasons restated in §2.2 where the original was wrong):
DWG-per-set export · self-contained web-viewer package · BOQ tab and the stable line-item link ·
S-curve and progress analytics · **native redline authoring** (read ships as R7) · **ReTree by reparenting**
(the capability arrives via §0.1's folder generation) · P6/MSP schedule round-trip and the 4D workspace ·
**Compare Models** (X6 and F2 make the stated replacements real) · **multi-run trend chart** (R5 ships the
number) · **`CamelWorks.Batch.exe` automation host and any scheduler** (V3 ships the one step).

**Newly declined** (§2.1): annotation canvas · Issue Pack NWD · versioned standards library with drift
reporting · Appearance Profiler profile import · `.cwjob` CLI launcher · live bSDD · private markup
exchange schema · IDS authoring · COBie in any form · IFC4.3 · directional clearance envelopes and the
support-availability check · a formal cross-team parity gate with the web product.

**Newly cut from the plan as it stood — this is the part nobody was willing to write:**

| Now cut | Was | Why, on merit |
|---|---|---|
| **Pin-to-ribbon and My Tools (16 slots)** | H2, ribbon dropdown + generated-dialog machinery | A second personalisation system inside a product whose measured first-run problem is surface. The plan already documents the answer: **every button is a real AdWindows item, so right-click → Add to Quick Access Toolbar works on all of them.** The Graph Editor and "Open as graph" stay; pinning goes. **−1 ribbon button** |
| **External ODBC / OLEDB data source** | Restored in §6 | **I am overturning that restoration.** The restore argument was *"DataTools' per-object SQL is ruinous, so do it properly in one cached query"* — but doing it properly does not require **us** to be the ODBC client. The user runs the query once and hands us a sheet, which S2's reader now consumes. What we would be shipping is drivers, connection strings, credential prompts and a network-path failure mode, across eight host configurations, answered by two people with no telemetry. It is one of the three highest ticket-rate surfaces in the product and it has a good substitute |
| **Takeoff's per-rule mesh fallback** | Data ▸ Takeoff | The plan itself calls it *"the slowest thing CamelWorks does"*, running the COM geometry path per item on the STA thread, guarded by a measured ceiling, a method stamp, a closed-shell flag and an export refusal — four safety mechanisms around a number the plan says is *"frequently an open shell"* and *"a liability free positioning cannot absorb."* **The safest version of a liability is not shipping it.** Takeoff keeps property summing, the regex cleaner and unit conversion. Mesh quantities stay where they are unavoidable and correct: inside the IFC exporter, where base quantities are a schema requirement and there is no property to read |
| **OBJ export** | Restored in §6 alongside glTF/GLB | glTF/GLB is the format a client's viewer opens. OBJ is for a modeller moving geometry into another package — not this product's user and not in the weekly loop. Keeping glTF and cutting OBJ costs the buyer nothing and removes a format, a golden-file corpus and a support row |
| **Contact-sheet PDF** | Sets & Views ▸ Viewpoints | Its structural reason was that Simulate's tier-1 smoke workflow needed something to exercise the report engine. **M3 replaces that with a real workflow.** Batch PNG render stays; the contact-sheet layout is a second report format for a job the report and R3 already do |
| **`Data ▸ Browse` and `Data ▸ Edit` as two tabs** | 2 tabs | One grid. Browse-with-edit is one screen. **−1 tab** |
| **`Batch ▸ Runs` as a tab** | 2 tabs | Run history folds into Jobs as a per-job expander. Capability unchanged. **−1 tab** |
| **`Project ▸ Setup` as a guided wizard** | 4-step propose/review/apply sequence with its own per-step diff UI | Narrowed, not cut: a one-screen **checklist** linking to the tabs that now do the work — load a template pack, import the matrix sheet, generate sets, assign zones. With S1, S3, S4 and §0.1 in place, the wizard is a fourth path through machinery that has three good doors, and it is the largest first-run surface in the product |
| **"% complete by zone" rollup** | Data ▸ Edit ▸ Status Stamp | The stamp stays; the rollup goes. It is the same analytics surface as the S-curve already cut for the same reason — an element-count rollup weights a light fitting like a slab pour |

**Surface after all of it:** **25 ribbon buttons** (was 26), **16 tabs** (was 19) — with the Appearance
Manager, BetterSets and the Simulate board added. **Every tab gets a specified empty state**, and that
list is a release artefact, because nothing anywhere currently says what the pane shows on day one
against a project with no sets, no matrix, no parties and no clashes — which is the state every
coordinator meets it in.

---

## 5. What this costs

**Honest answer: yes, it fits — but only with the cuts above, and only if four sequencing decisions are
made now rather than discovered.** Without the cuts it does not fit, and pretending otherwise would be
the failure mode risk 8 already names as the largest in the plan.

**Why it fits.** The additions are not evenly weighted. Roughly two thirds by count are Core work with
zero host coupling — the BCF payload and engine, IDS, the merge engine, the Finding record, the XLSX
reader, the unit formatter, the schemas, setting-out arithmetic, headroom, the report blocks, `status.json`.
That work is netstandard2.0, Linux-testable, golden-file-gated, and **invisible to the 20-build matrix and
to the 8-row smoke matrix**, which are the two things that actually scale badly with two developers. The
expensive additions are few and named: the BetterSets **WPF editor rewrite** (2–3 weeks), the Appearance
Manager panel and its arbitration, the machine-wide install cell (which **doubles install verification**),
and the merge preview screen.

**What pays for them.** The cuts above remove: one ribbon dropdown and its generated-dialog machinery, an
ODBC client and its driver/credential support surface, a COM geometry path on the STA thread with four
guard mechanisms, an export format with its own corpus, a report layout, three tabs, and a four-step
wizard with per-step diff UI. That is roughly the same order of work as the four expensive additions, and
it is a **strictly larger** reduction in support surface — which is the constraint that binds.

**Four sequencing decisions, and they are not negotiable:**

1. **G1 (generation counter), G3 (unified Finding) and G7 (identity) land before Stage 1 closes.**
   All three change `§2.4` or the abstraction set. Each is days now and a retrofit across sixteen tabs
   and every export path later — the exact shape §7 already warns about for the test seam.
2. **The two directed items move out of Stage 6.** BetterSets to Stage 4, the Appearance Manager to
   Stage 4–5, ahead of the Coordinate workspace that consumes it. As scheduled, the two things the owner
   personally directed are the two a slip squeezes.
3. **0-L (publication clearance, §6 item 3 below) blocks everything.** Until it resolves, harvest
   **designs**, not source. Design-harvest is reversible; publication is not.
4. **5-X moves earlier and its findings are not Stage-6 work.** Cold start is the entire adoption
   mechanism for a free tool — there is no salesperson and no trial call — and 100 candidates produced
   **zero** aimed at first run. S6, S7 and the empty-state list are the response; 5-X must run early
   enough that removing surface is still possible.

**Three things must be added to the Definition of Done, because they are now deliverables with no owner:**

- **The Limitations page.** It has become a buyer-facing and IT-facing document, not an appendix. Counting
  only what this decision adds: unsupported IDS facets · bbox-derived headroom as indicator, not
  measurement · redline text read best-effort · 2027 unverified · sync substrates supported / best-effort /
  unsupported · six-plane sectioning showing plane mode rather than box mode · the batch step's licence
  seat · foreign **temporary** overrides unreadable · BCF components that will not resolve in the client's
  tool · which publish fields we cannot set. It needs a format, an owner and a DoD row.
- **A support-load budget.** *"We answer N configuration tickets a week, and here is what we refuse."*
  The three highest ticket-rate surfaces in the shipped product are ones I am shipping — V3's out-of-session
  step, G6's storage-substrate matrix, and D2's machine-wide install plus policy file. Each already has a
  **named refusal path** (refuse and say why, rather than try and fail quietly). Each needs an owner.
- **A continuity artefact.** *"Why would a 400-person contractor standardise on a free two-person tool?"*
  is not answered by solving the install and policy problems, and half the IT-facing work assumes it is.
  Revizto comes with a contract, an SLA and a company that will exist next year. The answer is a published
  support commitment with a **named response time**, a **named end-of-life policy**, and a **named
  continuity position on the sidecar formats** — a business artefact, and the one that actually decides
  the adoption every deployment item here takes for granted. G5's published schemas and X7's
  self-describing artefacts are its technical half; the commitment is the other half.

**If something has to give**, the order I would give it in: F6 (penetration template) → R6 (key-plan
image) → D6 (portfolio rollup) → S4 (code tables) → F5 (freeze). Nothing above that line comes out
without changing what the product is.

---

## 6. Still unknown

Nobody in this process could answer these, and three of them gate work.

1. **Is there a documented .NET *write* counterpart to `View.GetClippingPlanes()`?** Seven roles jumped
   from *"the internal .NET surface broke in 2025"* straight to *"use COM"*, and nobody asked whether the
   `View` object exposes a symmetric setter. If it does, the entire Section Box crisis resolves with **no
   COM interop reference across four years, no STA confinement, no undocumented surface and no per-year
   smoke row** — and the same call writes the box that `GetClippingPlanes` already reads back for BCF.
   **This is fifteen minutes and it is now the FIRST question in 0-S2**, ahead of both the COM port and the
   `InternalClipPlanes` port. The COM route (verified shipping to 2024/2025/2026 × Manage and Simulate with
   zero `#if` directives anywhere in that source tree) is the second question and the fallback.
2. **Do COM-set clip planes survive capture into a `SavedViewpoint`?** The COM path writes the **current
   view's** planes. Review mode's boxing works either way; a section stored **into** a saved viewpoint —
   which the per-group report view and the BCF `ClippingPlanes` write both want — is unproven. 0-S2 must
   answer it. (The *read* half is safe: `GetClippingPlanes` is documented and already parsed on every
   target year.)
3. **Six planes, not one.** KVI ships a single plane. Six aligned planes at `eAlignment_NONE` should give
   the same clipped result as the UI's Box mode — and, usefully, a **non-axis-aligned** box the managed
   `eMODE_BOX` cannot — but six-plane behaviour is not demonstrated by any code either repo contains.
4. **2027 on every count.** KVI's `PackageContents` tops out at `Nw23` (2026). `NavisworksLocator.KnownVersions`
   stops at 2026. BIMCamel compiles 2027 in CI and the Dyncamelo half never has, and nobody has stated 2027
   has been *run*. 0-R's exit condition applies unchanged: **a row we cannot stand up and smoke-run is
   removed from the supported matrix**, not claimed.
5. **Is per-gridline geometry reachable on all four years?** `Grids` exposes systems, levels and
   **intersections**; whether an individual gridline's direction and origin — the plane you take a signed
   perpendicular offset to — is reachable is not established. R1 ships from the intersection lattice
   either way; the answer only decides whether it upgrades to true perpendicular offsets.
6. **Does the `Graphics` object expose text and a screen-space context on all four years?** KVI proves
   `Color`, `Opacity` and `Sphere`, and nothing more. Every text-bearing overlay surface — marker labels,
   an in-view legend, the review HUD's copy — is gated on this probe in 0-S5, with bare markers as the
   named fallback. A legend that silently renders nothing on one year is the same failure class the
   redline cut was about.
7. **Who owns KVI_Tools' code?** The bundle carries `CompanyDetails Name="Kanadevia Inova"`, installs to
   `%APPDATA%\KVI_Tools`, and its About dialog states it is *"community-developed by Ahmed Naser … not
   officially supported by Kanadevia Inova (KVI) or the IT department."* That disclaimer proves the author
   anticipated the question; it does not answer it, because employment IP assignment usually reaches
   anything made in the scope of employment or with employer resources. **Apache-2.0 + Commons Clause is
   irrevocable once published, and for any code the owner does not own, the resale reservation the whole
   licence exists to create is void.** Needs one employment-IP legal opinion. Related and unaudited: the
   clone is **shallow**, its single visible commit is *"Merge pull request #29"*, so at least 29 PRs of
   history exist and none of it has been reviewed — and publishing a repo publishes its history. **This is
   Stage 0-L, it is non-parallelisable, and every harvest verdict above is provisional until it clears.**
8. **What does publishing the web product's engines do to the web product?** The strongest openBIM
   harvests — the BCF writer/reader, the `Findings` model, the 1,693-line conformant IDS engine — are the
   owner's own code from a live commercial product. Relicensing them into a source-available repo is a
   deliberate decision that should be recorded in `THIRD-PARTY-NOTICES.md` **once, up front, for
   everything harvested from that repo**. Commons Clause reserves resale, not reimplementation. That is
   the owner's call and it belongs beside item 7, not after the repo is public.
9. **The cost of the 10 s reconcile poll**, still stated rather than measured — one property read per
   scoped test on a 40-test matrix. Unchanged from the plan's own residual list.
10. **`ClashKey`'s occurrence discriminator**, reworked twice and never re-verified. GATE is the check.
    F4's proximity-band collapse now depends on it too, which raises the stakes but does not change the
    test: if GATE fails, §2.3 is the first thing to re-derive.
