# CamelWorks — Implementation Plan

**One Navisworks add-in.** BIMCamel IFC Exporter + Dyncamelo, merged. Free to use, including
commercially. **Source-available, not open source** — Apache-2.0 + Commons Clause.
Navisworks Manage and Simulate 2024–2027.

**Ships as one complete product.** No phasing, no MVP, no "coming in 1.1", no greyed-out buttons.
The build order in §7 is an internal dependency order, not a release schedule.

Companion document: [`CAMELWORKS_PLAN.md`](CAMELWORKS_PLAN.md) — the market research (24 pain points,
competitor pricing, the free comparables) this plan answers.

---

## 1. Two findings that change the plan

Before anything else, because they invalidate assumptions the research doc made.

### 1.1 The clash write API is broken on half our target versions, today

`ClashResult.SetStatus` and `ClashResult.Assign` in Dyncamelo are compiled out on 2026 and throw:

```csharp
// src/Dyncamelo.Navisworks/ClashNodes.cs:452
#if !NAV2026
    …TestsData.TestsEditResultStatus(clashResult, wanted);
#else
    // TestsEditResultStatus gained a required Assignee (current-user) argument in
    // Navisworks 2026; pending a port verified on that release.
    throw new System.NotSupportedException(…);
#endif
```

These two calls are the entire engine under the Triage board's **Status** and **Assignee** columns and
under the write-back projection into Clash Detective. On 2026 they do not work, and on 2027 the
`#if !NAV2026` gate takes the *working* branch against an API that changed — i.e. it is worse than
broken, it is untested. **Porting them is a Stage-0 spike, not a Stage-5 detail.**

### 1.2 Section Box works on exactly one of our four target versions

```csharp
// src/Dyncamelo.Navisworks/SectionBoxNodes.cs:35
#if NAV2024
    clipPlanes.SetMode(LcOaClipPlaneSetMode.eMODE_BOX);
#else
    // The internal clip-plane API (InternalClipPlanes / LcOaClipPlaneSetMode) changed
    // in Navisworks 2025+; this node is pending a port verified on those releases.
    throw new System.NotSupportedException(…);
#endif
```

It rides `viewpoint.InternalClipPlanes` — an **undocumented internal surface** — and throws on
2025/2026/2027. The research doc named Section Box as the single highest value-per-line-of-code item in
the product (P6: COINS Auto-Section Box is the most-loved free Revit add-in for exactly this). It
currently works on 2024 only, and review mode's group-extent boxing depends on it.

**Consequence for the plan:** two of the three flagship capabilities rest on code that does not run on
most target versions. They become named Stage-0 spikes with decided failure branches (§7).

---

## 2. Architecture

### 2.1 Two front doors, one core

Every capability is a plain C# service in `CamelWorks.Core`. Two thin front doors call it:

```
   ribbon → pane ──┐
                   ├──▶  CamelWorks.Core  ──▶  CamelWorks.Nav[.Clash]  ──▶  Navisworks API / COM
   Dyncamelo node ─┘         (no UI)              (all host calls)
```

A node is a ~10-line wrapper. A pane tab is a ViewModel. **Neither owns logic.** Every new Core service
ships both doors in the same PR, enforced by a reflection-driven parity test over `ITool<TSettings>`.

*Rejected:* "every tool is a saved graph." A triage board needs sub-100ms interaction, not a graph
re-evaluation, and a graph-backed dialog is harder to debug than code. **`Open as graph`** ships only on
the named parameterised-batch screens and is *absent*, not stubbed, elsewhere.

### 2.2 Solution layout — five host-bound assemblies

```
CamelWorks.sln
├── CamelWorks.Core        netstandard2.0, zero Autodesk references, architecture-tested
│   ├── Identity/          ElementKey · ClashKey · GroupId      [from BIMCamel IfcGuid]
│   ├── Geometry/          mesh, quantities, bbox                [from BIMCamel]
│   ├── Clash/ Data/ Sets/ Appearance/ Quantify/ Batch/ Report/ Store/
│   └── Abstractions/      IModelDocument · IModelItem · IClashSource
│                          IModelWriteTransaction · IViewSession · IViewpointStore
├── CamelWorks.Nav         every Autodesk.Navisworks.* and COM call, except clash
├── CamelWorks.Nav.Clash   ★ every Autodesk.Navisworks.Api.Clash reference, incl. the 41 clash nodes
├── CamelWorks.Graph       Dyncamelo engine + editor
├── CamelWorks.UI          ribbon, 2 dock panes, shared dialog kit
└── CamelWorks.Tests       Core unit + contract suites (Linux-runnable)
```

★ **`Autodesk.Navisworks.Clash.dll` ships only with Manage.** Any type whose *signature* names a clash
type throws at load on Simulate — including the capability probe whose job there is to report
"Clash Detective unavailable". Isolating it into its own assembly, loaded only after the probe passes,
is a correctness requirement, not tidiness. The existing codebase only survives this today because
`AssemblyNodeLoader.cs:47` swallows `ReflectionTypeLoadException` — a reflection-scanning recovery that
does not exist on the ribbon → pane → Nav call path.

**5 host-bound assemblies × 4 years = 20 builds.**

### 2.3 Identity

| Key | What it names | Used by |
|---|---|---|
| `ElementKey` | one model element, **scoped to its owning model**, via a 3-rung fallback (instance GUID → source path + tree path hash → rounded geometry signature) | everything |
| `ClashKey` | one clash result: the ordered `ElementKey` pair + a **stable** occurrence discriminator | triage carry-over |
| `GroupId` | a minted, persistent group identity | review, BCF, report, the meeting |

**The occurrence discriminator must not be a dense 1..n ordinal.** A dense rank renumbers whenever the
count changes — which is exactly the moment carry-over matters (three penetrations become two). Use a
quantised position relative to item A's own frame, not a rank.

### 2.4 Persistence — the sidecar store

Custom properties written through COM live in the NWF/NWD and can be dropped by a model refresh. So the
**authoritative copy of every CamelWorks-authored datum is a sidecar**, and model properties are a
*projection* re-stamped on load. The model is never the only copy.

```
<project>/.camelworks/
├── project.cwproj        sets · clash matrix · filter/assign rules · responsible parties · priorities
│                         · colour profiles · report templates · board presets · IFC mapping · bands
├── triage/*.jsonl        per-writer append-only journals — triage.json is never a write target
├── groups/               group records: id, members, pin state, comments
├── project/*.jsonl       who changed which rule, when
├── sets/*.cwset          portable set templates
├── snapshots/            last 3 per NWF, for the Δ column
└── undo/                 pre-edit snapshots for Undo CamelWorks operation
```

**Concurrency:** every triage change appends to a per-writer journal and folds deterministically
(latest-timestamp-wins per field; **comments merge by id, they never overwrite**). `project.cwproj`
takes a lease and shows a section-level conflict diff on a refused write.

**Clash triage state is projected both ways, asymmetrically.** CamelWorks status *is* the five native
values, and every board write projects into the native result. On load CamelWorks reads back **only**
the two statuses Navisworks does not recompute (`Reviewed`, `Approved`) plus `AssignedTo`, and surfaces
differences as a reviewable conflict list — store wins by default. Navisworks recomputes New/Active/
Resolved on every re-run, so reading those back as truth would silently overwrite a human decision.

---

## 3. The ribbon — 5 panels, 26 buttons

**Routing rule.** A button either acts immediately `[A]` or opens the pane on a named workspace/tab
`[P:workspace/tab]`. **No ribbon button opens a modal that duplicates a pane screen.** True modals `[D]`
are reserved for terminal file operations that need a save path and nothing else. Anything that changes
what the user sees is **modeless with live preview**.

Every button is a real AdWindows item, so **right-click → Add to Quick Access Toolbar** works on all of
them — documented in the guide as the answer to "put my tool where I want it".

### Panel 1 — Project
| Button | Type | Does |
|---|---|---|
| **Set Up Project** (large) | `[P:Project/Setup]` | Guided setup: federation scan → levels & zones → test matrix → set library → profile |
| Health Check | `[P:Project/Health]` | One scorecard: Models · Sets · Data |
| Fix Broken Links | `[D]` | Repoint broken NWF paths, rename models, find missing sources |
| Project Profile ▾ | `[A]` | Save · Load · New from template |

The three destructive commands — **Sync to model**, **Undo CamelWorks operation**, **Remove CamelWorks
properties** — are *not* on the ribbon. They live only in Project ▸ Profile ▸ Model writes, each
rendering an affected-element count before it runs, Remove behind a typed confirm.

### Panel 2 — Coordinate *(Manage only)*
| Button | Type | Does |
|---|---|---|
| **Clash Triage** (large) | `[P:Coordinate/Triage]` | The board |
| **Review** | `[A]` | Enter review mode on the board's current filter and scope |
| Clash Tests | `[P:Coordinate/Tests]` | Matrix builder — and Run selected / Run all |
| Clash Rules | `[P:Coordinate/Rules]` | The one pipeline: Suppress & Flag → Group → Assign |
| Clash Report | `[P:Coordinate/Report]` | PDF / XLSX / HTML coordination report |
| BCF ▾ | `[P:Coordinate/BCF]` | Export · Import · round-trip status |

### Panel 3 — Data
| Button | Type | Does |
|---|---|---|
| **Data Manager** (large) | `[P:Data/Browse]` | Browse, edit, calculated columns |
| Excel ▾ | `[P:Data/Browse]` `[P:Data/Edit]` | Properties out · Properties back in (preview diff) · Import and join an external sheet |
| Assign Levels & Zones | `[P:Data/Zones]` | Write Level / Zone / Grid properties |
| **Takeoff** | `[P:Data/Takeoff]` | Sum any numeric property by any other, straight to XLSX |

### Panel 4 — Sets & Views
| Button | Type | Does |
|---|---|---|
| **Set Library** (large) | `[P:Sets/Library]` | Portable set library with variables |
| Bulk Sets | `[P:Sets/Generate]` | One set per distinct value of a property |
| Colour by Property | `[P:Sets/Colour]` | Distinct-value or gradient colouring, live |
| Viewpoints | `[P:Sets/Viewpoints]` | Bulk rename/renumber/re-folder, batch render, contact sheet |
| Section Box ▾ | `[A]` | Box selection (+ context margin) · Box clash · Box group · Clear |
| Isolate ▾ | `[A]` | Isolate · Ghost others · Show all · Reset CamelWorks overrides |

**Section Box and Isolate sit here, not in Coordinate.** Neither touches the clash DLL, both work
identically on Simulate, and both are what a user reaches for from *any* selection.

### Panel 5 — Deliver & Automate
| Button | Type | Does |
|---|---|---|
| **Batch** (large) | `[P:Batch/Jobs]` | The Friday federation job runner |
| **Graph Editor** (large) | `[P:Automate]` | The Dyncamelo canvas |
| **Export IFC** | `[P:Project/Export]` | The BIMCamel exporter — full pane, not a dropdown item |
| Export Data ▾ | `[D]` | CSV · XLSX |
| My Tools ▾ | `[A]` | 16 fixed ribbon slots, relabelled/rebound at runtime; a 17th pinned graph opens a picker |
| Help ▾ | `[D]` | Guide · Limitations · Shortcuts · Find a Tool… · Sample project · Report a problem · Diagnostics ▸ Run self-test · Updates · About |

**Find a Tool** is a filter over every command, matching on name, synonyms, and **symptom phrasing
lifted from P1–P24 of the research** — "too many clashes" finds Clash Rules, "empty set" finds Health
Check. It has a **permanently visible home**: a search field in the pane header on every workspace.
Navisworks has no command search, and an index nobody can see is not an index. **F1** anywhere opens the
matching guide page, shipped as local HTML in the bundle so it works offline.

---

## 4. The panes — 2 panes, 6 switcher entries, 19 tabs

Navisworks already ships ten dock panes. The coordinator's screen is full before CamelWorks loads. So
**two** panes:

- **CamelWorks** — Home + five workspaces: Project · Coordinate · Data · Sets & Views · Batch
- **CamelWorks Automate** — the Dyncamelo canvas, separate because it needs full width and is a
  genuinely different mode

**Navigation is a horizontal segmented bar** across the pane's top row, tab strip beneath — the two-row
arrangement Clash Detective, TimeLiner and Quantification already use. Below ~900 DIP it collapses to
`Home` + a labelled dropdown, **never to unlabelled glyphs**. *(A 72 DIP labelled vertical rail needs
340–390 DIP of vertical run for six entries, which does not fit a short wide dock.)*

**Dock policy — an API constraint, not a preference.** A `DockPanePlugin` first appears **floating**;
`DockStyle` is ignored and dock placement lives in the user's Workspace, which a plug-in cannot write.
CamelWorks declares `[DockPanePlugin(1280, 340, MinimumWidth = 470, MinimumHeight = 300)]` so it opens
at board proportions, and **never resizes, closes, undocks or re-tabs a pane it did not create** —
because it cannot. Every workspace renders usably at both **~470×640 and ~1600×340**: grid tabs collapse
rows to two lines below 900 DIP; form tabs go single-column below 700 DIP. A team-wide fixed layout can
only be distributed as a Navisworks Workspace file.

### Home — no tabs
Cards routing to the common jobs, plus **Open the sample project**. Reachable from any workspace.

### Project — 4 tabs
- **Setup** — the guided sequence; per step propose → review → apply, each skippable, nothing overwritten.
- **Health** — one scorecard, one exportable PDF.
  *Models*: origin / rotation / units / bounding-box disagreement per model; a model whose transform
  rotation changed since last snapshot; **duplicate appends**; "no grid system found"; "multiple grid
  systems, only one active".
  *Sets*: sets returning 0, and ±50% count drift.
  *Data*: missing property · empty value · non-numeric in numeric field · duplicate name/GlobalId ·
  naming regex · unit mismatch · orphan geometry · zero volume · **coordination-readiness gate**.
- **Profile** — the `project.cwproj` editor, with who-changed-what-when on every section, plus the
  **Model writes** section (Sync · Flush projection · Undo · Remove properties). Carries the line that
  must appear in three places: ***"Ctrl+Z does not undo a CamelWorks operation."***
- **Export** — **the BIMCamel IFC exporter, hosted unchanged**: scope, schema, Smart setup, both mapping
  grids, georeferencing, size splitting, batch, profiles, Pre-flight. Existing export profiles load
  unchanged. It is a re-host, not a rewrite.

**Responsible Parties** is a first-class profile section (`id, company, discipline, contact, email`).
The Assignee column is a picker over it. It is what makes per-party reports and per-party BCF possible,
and it closes P4's "one `Assigned To` string".

### Coordinate — 5 tabs *(Manage only)*

**Workspace scope control:** *All tests / test folder / single test*, scoping the board, the funnel, the
carry-over banner, review mode's sequence and the report default together. Real matrices are named by
zone, level, phase and pair ("L03 HVAC v Struct") — Discipline A/B does not recover that.

**Tests** — matrix builder **and Run**.
- Run selected / Run all behind `Application.BeginProgress` with cancel. On completion: snapshot →
  group carry-over → re-apply Rules → re-run clash carry-over.
- **Reconcile, three triggers.** Navisworks fires no plug-in event for a run started from Clash
  Detective's own button — the most frequent action in the weekly cycle. CamelWorks compares each scoped
  test's `lastRun` against the newest snapshot on (1) workspace activation, (2) document change, and
  (3) **a 10 s idle timer while Coordinate is visible**, suspended while a job is running. Trigger 3 is
  not optional: the pane and Clash Detective are on screen together, so clicking Run there produces no
  activation event and no document change.
- Generation is a **diff and preview, never a blind write**: *create N · update N · unchanged N ·
  orphaned N*, per-row checkboxes, and **never touches a test holding results without a per-test confirm
  naming the result count about to be discarded** — `ClashTest.Create` replaces a same-named test, which
  destroys its results, native statuses, native groups and anything a colleague did.
- Generated tests bind to **saved search sets**, never a resolved `ModelItem[]`, so the matrix survives
  a refresh. Clash Detective's own Rules tab is not writable through the API; the tab says so.

**Rules** — one tab, three sections, execution order, one Apply. Permanent funnel readout, every number
clickable:

> `1,910 results → 412 suppressed by 6 rules → 214 groups → 198 assigned to 8 parties · 16 unassigned`

This is the most trust-critical readout in the product — it is what a coordinator defends when a client
asks why the board says 214 and the model says 1,910.

1. **Suppress & Flag** *(before grouping)* — predicates: min overlap volume · min/max distance ·
   crossing angle · orientation pair · same model · in set X · property match · previously dismissed.
   Grouping turns 4,000 into 400; it does not stop 3,000 being things already decided not to be clashes,
   and carry-over does not cover it because a fresh re-export produces *new* results arriving as New.
   Suppressed results stay retrievable, and **every report carries "N results suppressed by M rules"
   with the full rule list in an appendix** — an unauditable hide button is not something you hand a client.
2. **Group** — drag-ordered stack: Level · Grid intersection · Model pair · Discipline · System ·
   Proximity(radius) · Same-item · Any property. Chained rules build subgroup names (`{Level}-{GridX}-
   {Discipline}`). Preview before apply. **Pinned groups are never re-derived** — that is what "keep
   existing groups" means. **Group carry-over runs before the stack re-derives.**
3. **Assign** *(after grouping)* — predicate → Responsible Party + default priority + due-date offset.
   Applies **only to results with no assignee**, so a rule can never overwrite a human decision. Without
   this the accountability model can only be filled by hand, group by group — which is Thursday, and the
   repetitive work the product exists to delete.

**Triage** — the board. **Columns are a saved layout with three presets:**

| Preset | Columns |
|---|---|
| **Meeting** *(default)* | Δ · Group · Level/Grid · Status · Assignee · Due |
| **Assign** | Meeting + Test · Discipline A/B · Priority · Age |
| **Full** | everything, incl. Redlined · Report view · Match confidence |

Six columns by default, readable across a room on a projector. **No screen in CamelWorks presents
fifteen columns as a default.**

- **Δ column**: New / Persisting / Resolved / **Regressed** (was Approved or Resolved, now Active), with
  a one-click *Regressed only*.
- **Carry-over banner**, permanently visible after every re-run: *"1,842 of 1,910 matched; 68 unmatched
  (show list); 14 low confidence"* and *"196 groups carried, 12 split, 6 new"*. **The board never
  silently reshuffles.**
- **Bulk edit on multi-select**, including **Group selected · Move to group · Ungroup**. Rules get you
  90%; the last 10% is always hands.
- **Selection sync — two behaviours, not one toggle.** *Board → model* is always on and is not a toggle.
  *Model → board* defaults on but renders a dismissible chip — *"showing 6 clashes involving ⟨item⟩ ·
  Clear"*, `Esc` clears. Without the chip, every incidental Selection Tree click silently re-filters the
  board and the coordinator loses their place mid-meeting.
- **Manual issues — a first-class row kind.** A third to half of what comes out of Wednesday is not a
  clash: *"no access to this valve"*, *"this riser has no builder's work opening"*. The store carries
  `{issueId, kind:"manual", title, elementKeys[], viewpointId, status, assignee, due, priority,
  comments[]}` and it is **treated as a group everywhere downstream** — board (Δ shows `—`), report and
  per-party BCF. Three entry points: context menu, tab header button, `I` in review mode. Without it
  those items go into a parallel spreadsheet — the "we still need the other tool" outcome — and BCF
  import has **no target record** for the 8 new observations the design team raises in Revit.
- **Review mode.** Entry: the **Review** button, a **double-click**, or `Ctrl+Enter` — **never bare
  `Enter`**, which the grid's inline editing owns. The pane collapses to a thin **HUD** showing
  *"12 / 214 · Group L3-C4-MEP · Status · Assignee"* plus the live key legend, so nothing is memorised,
  with visible Exit/Prev/Next/Status/Assign/Comment/Save-viewpoint/New-issue controls.
  **The unit is the group by default** — 40 pipe-through-slab hits are one issue, one decision, one
  viewpoint, one line in the minutes.

  One rule, stated once: *bare keys move, decide or annotate; anything that rebuilds a test or writes to
  the model takes a modifier and a confirm.*

  | Band | Keys |
  |---|---|
  | **Move** | `Space` / `Shift+Space` next / prev group (`N`/`P` aliases); `Shift+N` / `Shift+P` step results inside the group |
  | **Decide** | `1` Reviewed · `2` Approved · `3` Resolved · `0` Active |
  | **Annotate** | `A` assign · `D` due · `C` comment · `V` save viewpoint (stamps the group's report view) · `I` new manual issue |
  | **Structural** | `Ctrl+G` merge into previous group, with a confirm naming the result count |

**Report** — templated PDF / XLSX / HTML. One image per **group**, not per clash. Images carry
**auto-numbered callouts** projected at render time from each result's centre through the image camera,
numbered to match the result table beneath. Nothing to author, nothing to anchor, nothing that can go
stale, correct at every image size. Group comment history prints under the image. Output modes: single,
or **one per responsible party** with a `{Party}` filename template. A pre-flight names any group whose
results carry a native Navisworks redline CamelWorks cannot see.

**BCF** — BCF 2.1 export/import. **The export unit is the group**; one topic per group, manual issues
included. Import re-attaches by `ElementKey`, maps status and comments back, and **creates manual issues
for topics with no matching clash** rather than dropping them.

### Data — 4 tabs
- **Browse** — flat table of the current scope, columns from any property category, filter/sort, export.
- **Edit** — bulk add/edit/rename/delete custom tabs and properties; calculated columns (concat,
  arithmetic, regex extract, unit convert, lookup). **Preview diff before write**, pre-edit snapshot into
  the undo journal (COM `SetUserDefined` writes sit outside Navisworks' own undo).
  **Excel import** — the one external-data path: pick a CSV/XLSX, choose the key column (default GUID),
  map, preview, write, optionally cache the source.
  **Status Stamp** is a named preset here: writes `Status` + `StatusDate` + `StatusBy`, with a saved
  colour profile and a "% complete by zone" rollup.
- **Zones** — three independent sources with a documented fallback chain, each usable alone:
  **Level** = active grid system → else a user-editable elevation band table seeded from a Z-histogram →
  else an existing property. **Zone** = zone volumes → else one set per zone → else an N×N metre grid.
  **Grid** = grid system → else omitted. **The tab states which source it used and how many elements got
  no value.** In real federations grids are frequently absent (most DWG/IFC-sourced NWCs) or there are
  several buildings each with its own system; without the chain every downstream feature quietly
  degrades to blank Level and the user never finds out why.
- **Takeoff** — header states: *"independent of Navisworks Quantification, which has no public API."*
  Property→quantity rules with a **regex cleaner for values containing letters** (directly fixing
  Autodesk's own "export to Excel and strip the letters" advice) and unit conversion. **Per-rule mesh
  fallback** where the property is missing, stamped with the method used and a **closed-shell flag**;
  **any export refuses to include an unflagged estimate without an explicit override**, because
  Navisworks tessellation is frequently an open shell and a takeoff on silently-wrong volumes is a
  liability free positioning cannot absorb. The fallback walks the COM geometry path per item on the STA
  thread — the slowest thing CamelWorks does — so it carries a measured ceiling and never runs on a
  larger scope without a confirm naming the element count.

### Sets & Views — 4 tabs
- **Library** — templates with variables (`Level = <param>`), `.cwset` import/export, **and export of
  resolved GUID lists** — closing the hole where Navisworks' own set XML loses its contents.
- **Generate** — property → distinct values → one set each, folder naming template.
- **Colour** — **modeless, live apply** as property/palette/stops change; editable legend; save/load
  profile. A modal over a 3D view means the user cannot orbit or click an element to check its bucket.
  Uses the **temporary** override layer plus `SaveWithOverrides`; the permanent layer only when the user
  explicitly asks for document-wide colouring, and the pane states which is in effect.
  *There is no API to draw an overlay into a viewpoint* — the saved viewpoint carries the colour
  overrides plus a reference to the legend definition, and the legend is drawn as vector operators over
  exported images by the report engine.
- **Viewpoints** — bulk rename/renumber/re-folder, batch PNG render at fixed resolution, **contact sheet
  PDF**, copy overrides between viewpoints, one viewpoint per set/group.

### Batch — 2 tabs
- **Jobs** — an ordered step list saved as `.cwjob`:
  > Append files · Refresh · Save NWF · Save NWD · Run clash tests *(Manage)* · Apply rules *(Manage)* ·
  > Group *(Manage)* · Export BCF *(Manage)* · Export report · Export IFC · Run graph

  **Jobs run in the open Navisworks session.** There is no automation host. *A hidden
  `NavisworksApplication` consumes a licence seat a free add-in cannot answer for a customer's IT; it
  adds a host-bound assembly to every install cell; and "I scheduled it at 2 am and nothing happened,
  there is no log" is the highest-volume support ticket this product can generate.* The tab and the
  Limitations page both say: ***"CamelWorks jobs run in an open Navisworks session; schedule the
  session, not the job."***
  - **Evidence, not silence.** The run record is written at **start** with the full step list and
    rewritten after every step, so a job that dies names the step it died on. `stepTimeout` (default
    30 min) and `jobTimeout` (default 4 h) abort and record `timed-out`. Host modal dialogs are
    suppressed for the duration and any unexpected dialog is a recorded step failure.
  - **Failure policy.** Each step declares `onError: abort | continue | continue-and-flag`, default
    `abort`. **Save NWF/NWD refuse to run** when an earlier Append/Refresh reported a missing or errored
    model, unless explicitly overridden — in which case the filename is suffixed `_INCOMPLETE`.
    Otherwise the Friday job that hit one locked NWC quietly ships a federation missing a discipline.
  - **Atomic outputs.** Every file step writes `.tmp` then `File.Replace` with a `.bak` — the deliverable
    here is the NWF itself.
  - **"Full publish options" is struck.** There is no managed API for NWD publish options; `Export.NWD`
    is `Document.SaveFile` and nothing more. Spike 0-S3 probes `Application.Options`; if it fails, the
    guide names plainly which publish fields CamelWorks cannot set.
- **Runs** — every run writes `<job>.run-<utc>.json` beside the job file; this tab lists and opens them.

### Automate pane
The Dyncamelo canvas, unchanged, plus **Pin to ribbon** (generates a dialog from the graph's input nodes
and binds it to a My Tools slot) and **Open as graph** targets.

### Context menu
One **CamelWorks ▸** submenu on the 3D view and Selection Tree, scoped to the selection:
*Section box selection (+margin)* · *Isolate + ghost context* · *Colour by property…* · *Create set from
selection* · *Assign level & zone* · *Properties to Excel…* · *Export selection to IFC…* · **New Issue**
· *Show in Triage* *(Manage, when the selection clashes)*.

A coordinator spends the day in the Selection Tree and the right-click menu. Not appearing there means
every CamelWorks action costs a trip to the ribbon tab — and a user who never opens that tab never finds
the tool that solves their problem.

---

## 5. Manage vs Simulate

**Clash Detective is Manage-only.** The supported matrix is **8 host configurations**, not 4.

| Surface | Manage | Simulate |
|---|---|---|
| Panel 2 (6 buttons) · Coordinate workspace | full | **one** explanatory state naming Simulate, linking Limitations — not 11 tooltips |
| Section Box ▾ / Isolate ▾ | full | **full** |
| Project/Export (IFC) | full | **full** |
| Report engine (Health PDF, contact sheet, Takeoff XLSX) | full | **full** — only the clash-scoped Coordination Report is Manage-only |
| Batch clash steps | full | refused at job validation with a named reason; no silent partial deliverable |
| Clash nodes (41) | full | registered as **explicitly-unavailable stubs**, so a `.dyc` says *"requires Manage"*, not *"unknown node"* |
| Everything else | full | full |

Simulate has its **own tier-1 smoke workflow** that exercises the report engine — the largest new
subsystem, which would otherwise be untested inside a host on 4 of 8 rows.

---

## 6. Cut from the product

Not deferred — removed. Criterion: *is it in the weekly coordination loop?*, and *does removing it delete
plumbing rather than screens?*

| Cut | Why |
|---|---|
| **glTF/GLB, OBJ, DWG-per-set, web-viewer package** | 0 existing nodes for any. A web package means writing a browser 3D viewer, and no third-party runtime is allowed. Export is **IFC · CSV/XLSX** |
| **SQL / ODBC sources** | 0 nodes. Connection strings, drivers and credentials are a support surface two people cannot carry — and our own README documents DataTools' per-object SQL path as the thing that ruins exports |
| **BOQ tab** | 0 nodes. Estimating is a different buyer, and "stable line-item link across re-export" is a **second fingerprint problem** before the first is solved |
| **Progress tab + S-curve** | The schedule of record is in P6/MSP, the actuals in the PM system. An S-curve from element counts weights a light fitting like a slab pour. Status stamping survives as a Data/Edit preset |
| **Native redline authoring, and the CamelWorks annotation editor** | A redline that silently no-ops on one host year produces a report image missing the instruction the trade builds from. Replaced by auto-numbered callouts — nothing to author, nothing to go stale |
| **ReTree** (rebuild hierarchy) | Pure greenfield, high blast radius, not in the weekly loop |
| **Schedule Round-Trip (P6/MSP)** and **the whole Schedule/4D workspace** | Not in the weekly coordination loop, and contested by Synchro and Fuzor. The 7 `TimeLiner.*` nodes stay on the Automate side |
| **Compare Models** | No per-element geometry signature exists in 327 nodes; its answer quality is bounded by the same fingerprint with no confidence surface. The coordination question is answered by the Δ column and the carry-over banner; the model-revision question by the IFC exporter's NEW/DELETED/MODIFIED/UNCHANGED manifest |
| **Multi-run trend chart + per-party burn-down** | Longitudinal analytics is not a field we compete on, and it dragged a compaction format and a chart path through a hand-rolled PDF writer |
| **`CamelWorks.Batch.exe` automation host** | Deletes a host-bound assembly, a spike, the licence-seat question, and the highest-volume support ticket. Every hardening rule survives, applied to in-session jobs |

---

## 7. Build order and gates

Dependency order. Nothing is public until all of it passes §8.

| Stage | Build |
|---|---|
| **0** | Solution merge to §2.2 (20 builds, incl. the `Nav`/`Nav.Clash` split + architecture test); **2027 port of the Dyncamelo editor and node library with a green matrix**; ribbon + 2-pane shell + Home + horizontal switcher; sidecar store (schema versions, journals, group store, lease, atomic write, retention); installer + rollback + installer CI job; the five spikes; and 0-P/0-R/0-D |
| **0-P** | **Model supply + the two-revision sample.** 3 design partners at 2+ companies, under an agreement **explicitly covering publication of the scrubbed fixture in a public repo**. Recruit through the existing BIMCamel/Dyncamelo update-check channel. *Fallback decided now:* fewer than 3 sign → complete the corpus with synthesised pairs, and state in the guide which pairs the published recall was measured on |
| **0-R** | **Rig and licence supply.** Name the 8 host installations, the machines, the licence source and expiry, and the owner. ***Exit condition: a host row that cannot be stood up and smoke-run before Stage 5 is removed from the supported matrix*** — dropped from the support commitment, from `PackageContents.xml`, and named unsupported on the download page. The specific exposure is **2027**: BIMCamel compiles it in CI, the Dyncamelo half never has, and nobody has stated it has been *run* |
| **0-D** | **Distribution decision** — Autodesk App Store, yes or no. If yes, packaging, the EULA and review lead time are Stage-0 constraints on the installer. GitHub is not a channel for this audience: the free Navisworks graveyard is GitHub-only, and that is part of why those tools stayed coordinator-invisible |
| **0-S1** | **Spike: clash write API** — port `TestsEditResultStatus`/`TestsEditResultAssignedTo` to the 2026+ `Assignee` shape on real 2025/2026/2027 rigs (§1.1). Also confirms whether a read-modify-write of clash comments **preserves author, status and id of comments CamelWorks did not author**; if not, the comment projection is cut and due/priority/comment stay sidecar-only |
| **0-S2** | **Spike: section box** — port the clip-plane box to 2025+ (§1.2) |
| **0-S3** | **Spike: NWD publish options** via `Application.Options` |
| **0-S4** | **IFC golden-file tests from the current BIMCamel build, green before any BIMCamel code moves.** BIMCamel has no test project today. If extracting `IfcGuid` changes GlobalId derivation for one entity class, every existing user's first CamelWorks export reports 100% NEW / 100% DELETED and nothing catches it |
| **0-S5** | **Spike: review-mode key capture + HUD.** Thread-scoped `SetWindowsHookEx(WH_GETMESSAGE, …)` with the 3D view focused and with a modal open; fallback WinForms `IMessageFilter` (worth an hour — `RibbonTabMerger.cs:53` proves a WinForms `ThreadContext` is alive on Navisworks' UI thread); plus an always-on-top HUD over the 3D view. **`ToolPlugin.OnKeyPress` is struck** — activating a `ToolPlugin` replaces the navigation tool and disables orbit/pan/zoom. *Failure branch:* HUD buttons become the only path and "keyboard-driven review mode" comes out of all copy |
| **1** | Identity (`ElementKey` + `ClashKey` + `GroupId` together), scope resolution, traversal + cache, **all six abstractions** plus `FakeDocument` with an ordered effect log, the `ModelFixture` format, and the contract suite over all six. *A test seam not built alongside the traversal layer is never retrofitted across 30 services* |
| **GATE** | **Fingerprint bake-off. Stage 2 does not start until this passes.** ≥95% recall on unchanged elements and a **false-match rate of exactly 0**, on a committed scrubbed corpus of ≥5 real before/after pairs including: changed export settings; a family swap; a worksharing round-trip; a moved insert point; **a changed model rotation**; **a re-run where one of several results on the same element pair was fixed** (a false-match test); and one DWG/IFC-sourced NWC. *A missed carry-over is annoying; a wrong one shows an unresolved clash as "Approved, signed off by J. Smith" and looks correct to everyone in the room.* **Failure branch decided now:** carry-over ships as an explicit reconcile step the user accepts before anything is applied, and the copy changes from "survives a re-export" to "shows you what it thinks matched, and you accept it" |
| **2** | Report engine (PDF/XLSX/HTML + vector callout & legend layer + image validation) — *four screens emit reports; building it late means retrofitting four callers* |
| **3** | Data services + Data workspace; the job/transaction model and the host job gate |
| **4** | Sets services + Library/Generate; Assign Levels & Zones; Project/Health |
| **5** | Clash services + `Nav.Clash` + the whole Coordinate workspace |
| **5-X** | **Cold-start rehearsal.** Three coordinators who have not seen CamelWorks attempt *open sample → group → triage → export* against the §8 rubric and a stopwatch. **Findings are Stage 6 work.** *Verifying this only at Stage 9 means the first time an outside coordinator touches the product is the moment nothing is left to absorb the result* |
| **6** | Appearance, Colour, Viewpoints, override layers, Section Box, Isolate, context menu |
| **7** | Batch (in-session runner, run-file-at-start, timeouts), Export, Fix Broken Links |
| **8** | Automate: node wrappers for every new Core service, Open-as-graph, the parity test, Pin-to-ribbon, My Tools, Project Profile |
| **9** | Find a Tool + guide/F1/Limitations + Diagnostics self-test + signed installer + install matrix + migration checklist + field validation + App Store submission |

Stages 3–7 parallelise. 0, 0-P, 0-R, 0-D, 0-S*, 1 and GATE do not.

---

## 8. Definition of done

- Every ribbon button and pane tab implemented. **No greyed-out or "coming soon" affordances.**
- Every new Core service has a Dyncamelo node wrapper — enforced by the `ITool<>` parity test, not prose.
- Green build: **5 assemblies × 4 years = 20 builds**, plus the installer, plus the Linux Core test job.
- **8-row smoke matrix** — {2024, 2025, 2026, 2027} × {Manage, Simulate} — each with its tier-1 workflow.
- Clean install, upgrade-from-BIMCamel, upgrade-from-Dyncamelo, upgrade-from-CamelWorks, and
  upgrade-from-legacy-Inno all verified. Existing BIMCamel export profiles load unchanged.
- **Cold-start rubric:** a coordinator who has never seen CamelWorks reaches a grouped triage board and
  an exported PDF **within 10 minutes** of first launch; first launch of the sample reaches the carry-over
  banner and a Regressed-only board **in under two minutes**. Verified at 5-X *and* at Stage 9.
- A test run started from **Clash Detective's own Run button** produces the same Δ column, banner and
  suppression counts as one started from CamelWorks.
- `LICENSE`'s operative text and the plain-words list say the same thing, with no clause a reviewer must
  reconcile against a preamble.
- **The string "open source" does not appear** in the CamelWorks README, release notes, About dialog,
  guide or product page — and the existing BIMCamel README and product page, which say it today, are
  **edited**, not merely headered.
- The MIT notice for exporter-derived code is present in the shipped bundle.

---

## 9. Engineering notes

- **Transactions.** `Document.BeginTransaction` **does not exist**. The only transaction API is
  `Document.Database.BeginTransaction(DatabaseChangedAction)`, and COM `SetUserDefined` property writes
  sit outside Navisworks' undo entirely. So CamelWorks provides **its own** reversibility: a pre-edit
  snapshot into `undo/` before every write, surfaced as *Undo CamelWorks operation* (last 20, with
  author), and the "Ctrl+Z does not undo this" line in three places.
- **Testing.** Core is netstandard2.0 with zero Autodesk references and runs on Linux CI. Everything
  host-touching sits behind the six abstractions with a `FakeDocument` and an ordered effect log. The
  contract suite ships to users as **Help ▾ ▸ Diagnostics ▸ Run self-test** — the only post-release
  regression instrument that exists.
- **Report engine.** Own minimal PDF writer, same discipline as `StreamingStepWriter` — no third-party
  runtime. Because callouts and the legend are **vector**, not raster compositing, `Core/Report` stays
  netstandard2.0 and the golden-byte gate stays Linux-runnable.
- **Fixtures.** `Capture Fixture` runs a **mandatory anonymisation pass** — salted-hash of every item,
  model, level, zone, file name and path; property values dropped except those the harness reads;
  geometry reduced to bbox extents plus the rung-3 signature; `scrubVersion` and `partnerConsentRef`
  stamped into the header. **There is no raw-capture mode**, and CI fails on any fixture lacking those
  fields — the corpus is committed to a repository whose source is published.
- **Telemetry.** None.

---

## 10. Licensing

**Apache-2.0 + Commons Clause.** Free to use, including commercially; selling it, or a product or
service whose value derives substantially from it, is reserved to BIMCamel. All copy says
**source-available**, never open source.

**The professional-services permission is an operative clause, not a preamble** — appended to the
Commons Clause in `LICENSE` as a numbered additional permission:

> **Exception.** Use of the Software to perform professional services for a client — including clash
> detection, coordination, modelling, quantity take-off and reporting services, whether or not billed —
> does not constitute "Selling" under this clause, provided that the Software itself, or access to the
> Software, is not the product or service being provided.

*Why operative:* the Commons Clause text names "fees for hosting or consulting/support services related
to the Software" as Selling. A preamble contradicting it reads to a corporate legal reviewer as
**ambiguity, not permission** — and the stated user is a coordination team inside a GC or consultancy
that bills clients for coordination. That is not an edge case, it is the whole market.

**In plain words** — in the About dialog, a guide FAQ page and the IT deployment page:

> **Use it freely — at home or at work, companies included.**
>
> **Permitted:** using CamelWorks on projects you are paid for, **including coordination work you bill to
> a client**; deploying it across your company; modifying it for internal use; redistributing it
> unmodified, for free.
>
> **Not permitted:** selling CamelWorks or a modified CamelWorks; charging for access to it; bundling it
> into a product or hosted service you charge for.

**Inbound contributions — decided before the repo is published.** Under plain inbound=outbound a merged
PR would reach BIMCamel under Apache-2.0 + Commons Clause, so BIMCamel would **not hold the right to
sell** a product containing that code — precisely the reservation the licence exists to protect, and it
cannot be fixed retroactively once contributions land. Dyncamelo's README invites pull requests today.
**Call for 1.0: no CLA process.** `CONTRIBUTING.md`, the README and the issue templates state that issues
and bug reports are welcome and **code contributions are accepted only by prior written arrangement**. A
CLA workflow can replace this later without invalidating anything.

**Licensing history — stated plainly.** The BIMCamel IFC Exporter is MIT **today**, not merely in
history, so every commit published up to the relicense is MIT and a third party may fork the current
exporter and sell a Navisworks IFC exporter. Commons Clause on CamelWorks does not change that.
Dyncamelo's grants were MIT to v0.1.1, proprietary v0.1.2–v0.26.1, Apache-2.0 + Commons Clause
thereafter. Each grant applies to copies obtained while it was in effect; all three lineages are carried
into the CamelWorks `LICENSE` and README.

**Decision: accept the MIT exposure running outward.** The exporter is not the moat — the moat is the
triage loop, the documented sidecar and fingerprint formats, and the release cadence. Spend no effort
closing it. **The obligation running inward is not optional and is cheap:** the exporter's MIT copyright
and permission notice is reproduced in `THIRD-PARTY-NOTICES.md` covering the code carried into
`CamelWorks.Ifc`, `Core/Identity` and `Core/Geometry`.

---

## 11. Risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | **Clash write API broken on 2026, untested on 2027** (§1.1) | Spike 0-S1 on real rigs before Stage 1 |
| 2 | **Section Box works on 2024 only** (§1.2), on an undocumented internal API | Spike 0-S2; failure branch = the feature is named unavailable per year, not silently dead |
| 3 | **Fingerprint stability** — the whole triage value rests on it | GATE, with a decided failure branch that is still a product |
| 4 | **Review-mode key capture is unproven ground** — neither repo installs a message filter or hook anywhere | Spike 0-S5 with two fallbacks and a copy-change branch |
| 5 | **2027 has never been run**, only compiled | 0-R exit condition removes unstandable rows from the supported matrix |
| 6 | Custom property persistence across model refresh | Sidecar is authoritative; model properties are a projection |
| 7 | Report engine is the largest new subsystem | Built at Stage 2, exercised by the Simulate tier-1 workflow on 4 of 8 rows |
| 8 | **Scope: everything ships at once with 1–2 developers** — the largest risk in the plan | Ten features cut outright (§6); the cold-start rehearsal moved to Stage 5-X; every gate has a decided failure branch so nothing stalls waiting for a number |
| 9 | Abandonware perception (the free Navisworks graveyard) | A published support commitment for the four most recent releases in both editions, funded by the resale reservation |

---

## 12. Panel record

Five roles reviewed this plan over **three rounds**: VDC / BIM Coordination Manager · Senior Navisworks
API engineer · Product designer (AEC desktop) · QA and release engineer · Product strategist. Every
finding carried a concrete proposed edit; the lead architect actioned each one or **explicitly rejected
it with a reason** — 101 accepted and 13 rejected across the first two rounds; round 3's fold produced
the decisions recorded throughout this document.

| Round | Blocking + major findings |
|---|---|
| 1 | 12 · 11 · 8 · 9 · 9 |
| 2 | 6 · 8 · 5 · 8 · 8 |
| 3 | 5 · 7 · 6 · 9 · 6 |

**The panel did not reach silence.** Findings plateaued around 6–9 per reviewer per round rather than
trending to zero, because each revision opened new surface. Round 3's findings are folded in above;
**this document has not been re-reviewed by the panel.** Treat these as the residual open items:

1. **`ClashKey`'s occurrence discriminator** was reworked twice and never re-verified. It is the input to
   GATE, so GATE is the check — but if GATE fails, §2.3 is the first thing to re-derive.
2. **The 10 s reconcile poll** is a stated mechanism so QA can test against it, not a measured one.
   Confirm the cost of one property read per scoped test on a 40-test matrix before shipping the default.
3. **Manual issues** were added late and touch the board, the report, BCF import/export and the context
   menu. That cross-cutting reach has not been reviewed by the API engineer or QA.
4. **The Simulate tier-1 workflow** exists to exercise the report engine on 4 of 8 rows, but the
   clash-scoped Coordination Report is Manage-only — so what it actually exercises is Health/contact
   sheet/Takeoff. Confirm that is enough coverage for risk 7.
