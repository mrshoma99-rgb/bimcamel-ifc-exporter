# CamelWorks — Implementation Plan

**One Navisworks add-in.** BIMCamel IFC Exporter + Dyncamelo, merged. Free to use, including
commercially. **Source-available, not open source** — Apache-2.0 + Commons Clause.
Navisworks Manage and Simulate 2024–2027.

**Ships as one complete product.** No phasing, no MVP, no "coming in 1.1", no greyed-out buttons.
The build order in §7 is an internal dependency order, not a release schedule.

Companion documents:
- [`CAMELWORKS_PLAN.md`](CAMELWORKS_PLAN.md) — the market research (24 pain points, competitor pricing,
  the free comparables) this plan answers.
- [`CAMELWORKS_SCOPE_DECISION.md`](CAMELWORKS_SCOPE_DECISION.md) — the record of the seven-role scope
  re-evaluation: 100 candidates, three-lens judgements, declines and the cost argument. Its outcome is
  folded into **this** document, which wins on any disagreement.
- [`HARVEST_PROTOCOL.md`](HARVEST_PROTOCOL.md) — the gate for code harvested from the two private
  repositories: security clearance, de-branding, engines-not-viewer, and the release checks.

**No users yet.** Nothing here carries a backward-compatibility constraint; every persisted format is
designed once, properly, and versioned from its first commit.

---

---

## 0. The zero-setup rule

**Nobody spends five hours on templates before the plug-in does anything. Every feature works on
first open, against the raw model, with no configuration.**

This is the exporter's existing principle — *"first click produces a valid, correctly-placed IFC with
no setup"* — promoted to a product-wide law. It outranks every other design goal in this document.
Where it conflicts with a feature's design, the feature changes.

### The three rules it decomposes into

1. **Every default is DERIVED, never blank.** A screen that cannot compute a sensible default has not
   been designed yet. "Pick a property to begin" is a failure, not a neutral starting state.
2. **`project.cwproj` is created lazily and silently, and nothing ever blocks on it.** It is a record of
   what you have already changed, not a prerequisite you fill in first. A user who never opens Project
   ▸ Profile gets the whole product.
3. **Configuration is an accelerator, never a gate.** Templates, standards, code tables and party
   registries make the second project faster. They are never required for the first.

### What every feature does with nothing set up

| Feature | With zero configuration | What configuration later adds |
|---|---|---|
| **Clash Rules** | A default stack runs on first open: model-pair → level → proximity. Level comes from grids if present, else from elevation bands auto-seeded from a Z-histogram of the model bounds. Grouped board, first click | Your own rule order, suppression rules, name templates |
| **Clash Tests** | One click builds every-model-vs-every-other-model with the Navisworks default tolerance | Discipline matrix, per-pair tolerances, your spreadsheet |
| **Triage board** | Opens populated from existing clash results. Assignee is free text. Status is the five native values | Party registry turns the free-text field into a picker; assign rules fill it automatically |
| **Coordination Report** | A built-in default template produces a PDF on first click | Your cover page, logo, column choice, per-party splits |
| **Appearance Manager** | **Most useful with zero setup** — it opens showing what is *already* hidden and overridden in the document, which is a question Navisworks cannot answer today | Saved layer stacks, portable profiles |
| **Set Builder** | Builds from the current selection or an ad-hoc rule immediately; no library needed | Saved recipes, folder templates, parameterised reuse |
| **Levels & Zones** | Derives silently on first use: grids → elevation bands from the Z-histogram → existing property. States which source it used | Hand-edited band table, named zones |
| **Health Check** | Runs its built-in rule set against any model with no rules authored | Your naming regex, required properties, an `.ids` from a client |
| **Data Manager** | Browses and edits real properties immediately | Calculated column presets, code tables |
| **Takeoff** | Sums the numeric properties it finds, grouped by category | Named rules, unit mapping, regex cleaners |
| **Batch** | "Job from this document" captures the open federation as a runnable job in one click | Saved multi-step jobs, output templates |
| **IFC export** | Unchanged — already zero-config by design | Mapping grids, georeferencing, profiles |

### Enforced, not aspirational

- **Definition of done:** every tab is opened on a raw federated model with **no `.camelworks/` folder
  present** and must do something useful. A tab that renders an empty state asking for configuration
  fails the gate.
- **Cold-start rubric (5-X and Stage 9):** the stopwatch runs from first launch on a model the user
  brought, with **no template, no profile and no prior session**. That is the only measured path.
- **Set Up Project is not a wizard and never blocks.** It is a one-screen checklist showing what
  CamelWorks already derived, with the option to override any line. It can be skipped entirely and
  most users never open it.
- The word "template" may not appear in any first-run copy.

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

## 3. The ribbon — 5 panels, 25 buttons

**Routing rule.** A button either acts immediately `[A]` or opens the pane on a named workspace/tab
`[P:workspace/tab]`. **No ribbon button opens a modal that duplicates a pane screen.** True modals `[D]`
are reserved for terminal file operations that need a save path and nothing else. Anything that changes
what the user sees is **modeless with live preview**.

Every button is a real AdWindows item, so **right-click → Add to Quick Access Toolbar** works on all of
them. That is the personalisation story — *Pin-to-ribbon and the 16 My Tools slots are cut* (§6): a
second personalisation system inside a product whose first-run problem is surface.

**Every button below does something on a raw model with nothing configured (§0).**

### Panel 1 — Project
| Button | Type | Does |
|---|---|---|
| **Health Check** (large) | `[P:Project/Health]` | One scorecard: Models · Sets · Data. Runs its built-in rules on any model with nothing set up — first button because it is the first thing that pays off |
| Project Setup | `[P:Project/Setup]` | A checklist of what CamelWorks already derived, with an override per line. Skippable |
| Fix Broken Links | `[D]` | Repoint broken NWF paths, rename models, find missing sources |
| Project Profile ▾ | `[A]` | Save · Load · New from template pack (S3) |

Sync to model, Undo CamelWorks operation and Remove CamelWorks properties are **not** on the ribbon —
they live only in Project ▸ Profile ▸ Model writes, each showing an affected-element count first.

### Panel 2 — Coordinate *(Manage: full. Simulate: the board, read-only from the store — M3)*
| Button | Type | Does |
|---|---|---|
| **Clash Triage** (large) | `[P:Coordinate/Triage]` | The board. Opens populated; assignee is free text until a party registry exists |
| **Review** | `[A]` | Review mode on the board's current filter and scope |
| Clash Tests | `[P:Coordinate/Tests]` | Matrix builder + Run. One click = every model vs every other; S1 imports the project's own spreadsheet |
| Clash Rules | `[P:Coordinate/Rules]` | Suppress & Flag → Group → Assign, one pipeline. A derived default stack runs before you touch it |
| **Headroom** | `[P:Coordinate/Triage]` | F1 — floor set × target set as a BetterSets expression, emitting Findings onto the board |
| Clash Report | `[P:Coordinate/Report]` | PDF / XLSX / HTML from a built-in template. Field pack (R3) is an output mode |
| BCF ▾ | `[P:Coordinate/BCF]` | Export · Import — real BCF 2.1 and 3.0 (X1–X3) |
| Merge | `[P:Coordinate/BCF]` | M1 — one conflict preview for inbound BCF **and** inbound reviewer files. Runs on Simulate |

### Panel 3 — Data
| Button | Type | Does |
|---|---|---|
| **Data Manager** (large) | `[P:Data/Data]` | Browse and edit in one tab (the two-tab split is cut). Calculated columns incl. ancestry (S5) |
| Excel ▾ | `[P:Data/Data]` | Properties out · Properties back in (preview diff) · Import a sheet, keyed |
| Assign Levels & Zones | `[P:Data/Zones]` | Derives silently — grids → Z-histogram bands → property — and states which source it used |
| **Takeoff** | `[P:Data/Takeoff]` | Sums the numeric properties it finds, grouped by category. *Mesh fallback is cut* (§6) |

### Panel 4 — Sets & Views
| Button | Type | Does |
|---|---|---|
| **Sets** (large) | `[P:Sets/Sets]` | Set Builder — boolean AND/OR/NOT + set references, DNF-compiled to native search sets. Works on the live model with an empty library |
| **Appearance** | `[P:Sets/Appearance]` | The layers system. **Absorbs the old Colour tab, Isolate ▾, reset-overrides and copy-overrides-between-viewpoints** |
| Viewpoints | `[P:Sets/Viewpoints]` | Bulk rename/renumber/re-folder, batch render, copy overrides. *Contact sheet is cut* |
| Section Box ▾ | `[A]` | Box selection (+ margin) · Box clash · Box group · Clear · **non-destructive toggle** (V2). Documented `SetClippingPlanes`, all four years |

### Panel 5 — Deliver & Automate
| Button | Type | Does |
|---|---|---|
| **Batch** (large) | `[P:Batch/Jobs]` | Jobs and their run history in one tab. "Job from this document" in one click. V3 adds an out-of-session Convert/Federate step |
| **Graph Editor** (large) | `[P:Automate]` | The Dyncamelo canvas |
| **Export IFC** | `[P:Project/Export]` | The exporter, full pane |
| Export ▾ | `[D]` | glTF/GLB · CSV · XLSX. *OBJ is cut* |
| Help ▾ | `[D]` | Guide · Limitations · Shortcuts · Find a Tool… · Sample project · **Diagnostics ▸ Run self-test / Copy support bundle** (D5) · Updates · About |

**Find a Tool** is a filter over every command, matching name, synonyms and **symptom phrasing from
P1–P24**, with a permanently visible search field in the pane header. **F1** opens the matching guide
page from local HTML in the bundle.

---

## 4. The panes — 2 panes, 6 switcher entries, 16 tabs

- **CamelWorks** — Home + five workspaces: Project · Coordinate · Data · Sets & Views · Batch
- **CamelWorks Automate** — the Dyncamelo canvas, separate because it needs full width

Navigation is a **horizontal segmented bar** with the tab strip beneath, collapsing below ~900 DIP to
`Home` + a labelled dropdown, never to unlabelled glyphs. A `DockPanePlugin` first appears **floating**
and the API exposes no initial dock edge, so CamelWorks declares board proportions and **never resizes,
closes, undocks or re-tabs a pane it did not create**. Every workspace renders usably at both ~470×640
and ~1600×340.

### Home — "This week" (S6)
Not a card wall. The weekly cycle as the front door: one **Issue this week** action driving
reconcile → regroup → report, plus a staleness readout (when tests last ran, when the board last
changed, which models are newer than the last snapshot). Works with nothing configured.

### Project — 4 tabs
- **Setup** — the derived checklist. Never a gate.
- **Health** — Models (origin/rotation/units/bbox disagreement, duplicate appends, changed transform
  with **restore from snapshot** (F3), **model intake register** (F2)) · Sets (0-result and ±50% drift)
  · Data (built-in QA rules, plus a client `.ids` as a rule source when one exists — X4).
- **Profile** — `project.cwproj` editor, created lazily; Responsible Parties, priorities, code tables
  (S4), Model writes. Nothing blocks on it.
- **Export** — the IFC exporter hosted unchanged, plus classification identity (X5) and the IFC
  **change list** (X6).

### Coordinate — 5 tabs *(Manage full; Simulate store-hydrated — M3)*
- **Tests** — matrix builder, Run, three reconcile triggers, diff-and-preview writes, the extended
  write gate (G9), spreadsheet import (S1).
- **Rules** — Suppress & Flag → Group → Assign under one Apply, with the permanent funnel readout.
  **Cross-test duplicate collapse** (F4) on a proximity band, not exact key equality.
- **Triage** — the board. Presets, Δ column, carry-over banner, hand grouping, manual issues, review
  mode, **free-text search** (S7), **visible undo of the last N board writes** (S8), **Freeze** (F5)
  serving sign-off and fabrication release with an on-demand Change Notice.
- **Report** — built-in template; **setting-out coordinates** (R1), **typed resolution instruction**
  (R2), **field pack mode** (R3), **model manifest** (R4), **per-party open/closed delta** (R5),
  **key-plan image** (R6), **redline text** (R7), **deliverable identity** (R8/R9),
  **penetration schedule template** (F6).
- **BCF** — BCF 2.1 and 3.0 with the full accountability payload, deterministic topic GUIDs, a
  viewpoint payload that actually shows the issue, plus **Merge** (M1) and the **XLSX response leg**
  (M2).

### Data — 3 tabs
- **Data** — browse and edit in one surface: bulk property add/edit/rename/delete, calculated columns
  (concat, arithmetic, regex, unit convert, lookup, ancestry), keyed Excel import, preview diff before
  write, merge-not-stomp writes (G8).
- **Zones** — Levels/Zones/Grid with the documented fallback chain, stating its source and its misses.
- **Takeoff** — property→quantity rules with the regex cleaner and unit conversion; pivot to XLSX.

### Sets & Views — 3 tabs
- **Sets** — **Set Builder**: boolean expression tree (AND/OR/NOT, set references), DNF-compiled to
  native search sets, live count, dry run. Bulk generation folded in: ordered property list → folders +
  leaf recipes, depth cap 3, projected count, refusal ceiling. The human-readable formula is written to
  the set's own comment so a colleague without CamelWorks can read it. Saved recipes are optional.
- **Appearance** — the layers system: five scope kinds (elements · selection set · search set ·
  property rule · category), ordered, **later-wins-per-property**, painted on the **permanent** layer
  because `ResetPermanentMaterials` takes a collection while `ResetAllTemporaryMaterials` is
  parameterless and global. **Foreign-state bottom row** shows what is hidden or overridden that
  CamelWorks did not author, with Select / Adopt as a layer / Clear — stating it reports permanent
  overrides only.
- **Viewpoints** — bulk rename/renumber/re-folder, batch render, copy overrides, one viewpoint per
  set/group.

### Batch — 1 tab
- **Jobs** — the job list **and its run history in the same tab**. Steps: Append · Refresh · Save NWF ·
  Save NWD · Run tests · Apply rules · Group · Export BCF · Export report · Export IFC · Run graph ·
  **Convert/Federate out of session** (V3, shelling Autodesk's own `FileToolsTaskRunner` with a
  mandatory byte-identical-output failure check). Jobs run in the open session; run records are written
  at start and rewritten per step.

### Automate pane
The Dyncamelo canvas plus **Open as graph** targets. *Pin-to-ribbon is cut.*

### Context menu
One **CamelWorks ▸** submenu: *Section box selection (+margin)* · *Isolate + ghost context* ·
*Add as appearance layer…* · *Create set from selection* · *Assign level & zone* ·
*Properties to Excel…* · *Export selection to IFC…* · **New Issue** · *Show in Triage*.

---

## 5. Manage vs Simulate

**Clash Detective is Manage-only.** The supported matrix is **8 host configurations**, not 4.

| Surface | Manage | Simulate |
|---|---|---|
| Panel 2 · Coordinate workspace | full | **the board, hydrated from the store** (M3) — same workspace, columns driven by what the store holds rather than by the host. It refuses exactly two things: run/create a test, and project status back into Clash Detective |
| Section Box ▾ / Isolate ▾ | full | **full** |
| Project/Export (IFC) | full | **full** |
| Report engine (Health PDF, contact sheet, Takeoff XLSX) | full | **full** — only the clash-scoped Coordination Report is Manage-only |
| Batch clash steps | full | refused at job validation with a named reason; no silent partial deliverable |
| Clash nodes (41) | full | registered as **explicitly-unavailable stubs**, so a `.dyc` says *"requires Manage"*, not *"unknown node"* |
| Everything else | full | full |

**This is the competitive point, not a consolation.** Revizto, BIMcollab and Procore all sell issue tracking that never touches Clash Detective; leaving 4 of 8 supported hosts with no coordination story was the plan's worst competitive exposure. Simulate also has its **own tier-1 smoke workflow** exercising the report engine — the largest new
subsystem, which would otherwise be untested inside a host on 4 of 8 rows.

---

## 6. Cut from the product

Not deferred — removed.

**One criterion was struck from this section after review.** Earlier drafts justified five cuts with
"0 existing nodes for this". That is circular — it measures what Dyncamelo happened to grow, not what
users need, and it would have cut every genuinely new thing in the product. **Whether an engine exists is
an effort estimate. It is never a reason to cut.** Two cuts did not survive re-examination without it and
are restored below. The criterion that remains is: *is it in the weekly coordination loop, and does its
cost land on a user or on us?*

### Restored on re-examination

| Restored | Why the original cut was wrong |
|---|---|
| **glTF / GLB and OBJ export** | The cut said "0 nodes". But `BIMCamel/Ifc/MeshWriters.cs` already defines an **`IMeshWriter` interface with two implementations**, and `InstancedExtractor` already yields `InstancedElement` = a shared `LocalMesh` (vertices, indices, material) plus per-instance translation and a 3×3 rotation — **which is exactly glTF's data model**: one mesh referenced by many nodes with TRS transforms. glTF is a *third `IMeshWriter`*, not a subsystem. And the expensive half is already paid for: `PrimitiveSink` documents that `GenerateSimplePrimitives` is the only geometry-read surface Navisworks exposes and is 82–92% of export wall-clock, so a second output format costs the serialiser only. ProtoTech charges per-seat *per format* for exactly this |
| **External database source** (read-only ODBC/OLEDB, one query, cached, no stored credentials) | The support-surface concern — drivers, connection strings, credentials — was real, so the capability is **narrowed** rather than dropped: read-only, exactly one query, cached to the sidecar, trusted connection or prompt-per-session. This is iConstruct's paid Data Integrator, and the argument against it (our README documents DataTools' per-object SQL as ruinous) is an argument *for* doing it properly in one cached query, not for leaving users on the broken path |


### Cut in the scope re-evaluation — surface, not capability

These nine came out of the plan as it stood, to pay for the 63 additions in
`CAMELWORKS_SCOPE_DECISION.md`. None of the seven roles proposed them; the architect named them so the
total would fit two developers. Net surface went **down**: 26 buttons → 25, 19 tabs → 16, *with* the
Appearance Manager, Set Builder and the Simulate board added.

| Cut | Why |
|---|---|
| **Pin-to-ribbon and the 16 My Tools slots** | A second personalisation system inside a product whose first-run problem is surface. Right-click → Add to Quick Access Toolbar already does the job on every button |
| **External ODBC / OLEDB source** | Restored earlier, now re-cut. Doing it properly does not require *us* to be the ODBC client — the user runs their query once and hands us a sheet the XLSX reader consumes. What we would otherwise ship is drivers, connection strings, credential prompts and a network-path failure mode across eight host configurations, supported by two people with no telemetry |
| **Takeoff's per-rule mesh fallback** | The plan itself called it the slowest thing CamelWorks does, wrapped in four safety mechanisms around a number it admits is frequently an open shell and "a liability the free positioning cannot absorb". The safest version of a liability is not shipping it. Mesh quantities stay inside the IFC exporter, where base quantities are a schema requirement |
| **OBJ export** | glTF/GLB is what a client's viewer opens. OBJ serves a modeller moving geometry into another package — not this user |
| **Contact-sheet PDF** | Its structural reason was giving Simulate's smoke workflow something to exercise; M3 replaces that with a real workflow |
| **`Data ▸ Browse` and `Data ▸ Edit` as two tabs** | One surface. The split forced a mode choice before the user knew which they wanted |
| **`Batch ▸ Runs` as its own tab** | Run history belongs beside the job that produced it |
| **`Project ▸ Setup` as a guided wizard** | §0. A wizard is a gate; it becomes a one-screen checklist of what was already derived |
| **"% complete by zone" rollup** | A progress metric weighted by element count, which is the same flaw that cut the S-curve |

### Cut earlier, on merit

| Cut | Why — independent of what already exists |
|---|---|
| **DWG-per-set export** | Needs a DWG writer. That is a real new subsystem with no shared pipeline, unlike glTF/OBJ |
| **Self-contained web-viewer package** | Means shipping a browser 3D viewer. §9 forbids a third-party runtime, so it cannot even wrap an existing one — and nobody opens an unsigned zip from an unknown plug-in. Clients want an NWD, an IFC or an ACC link |
| **BOQ tab** (WBS template, stable line-item link) | "Stable line-item link across re-export" is a **second fingerprint problem** — and this plan has a GATE it has not yet passed on the first one. Estimating is also a different buyer with entrenched tools |
| **S-curve and progress analytics** | An S-curve built from model-element counts weights a light fitting the same as a slab pour. The schedule of record lives in P6/MSP and the actuals in the contractor's PM system. **The useful half — status stamping with dates — is not cut**; it is a Data/Edit preset feeding a "% complete by zone" rollup |
| **Native redline authoring, and a CamelWorks annotation editor** | A redline that silently no-ops on one host year produces a report image missing the instruction the trade builds from — and the redline API is undocumented, so we cannot know which year breaks it. Replaced by **auto-numbered callouts** projected at render time: nothing to author, nothing to anchor, nothing that can go stale. *(The 8 `Markup.*` nodes exist — this was cut despite having an engine.)* |
| **ReTree** (rebuild the selection tree by property) | The Navisworks tree is built from the source files and **`ModelItem` parentage is not writable through the API** — iConstruct does this by authoring a new NWD, which is a different product. *Verify in spike 0-S6 before publishing this reason; if reparenting turns out to be reachable, this cut is reopened* |
| **Schedule Round-Trip (P6/MSP) and the 4D workspace** | Not in the weekly coordination loop, and contested by Synchro and Fuzor who own the buyer. *(The 7 `TimeLiner.*` nodes exist — cut despite having an engine.)* They stay on the Automate side |
| **Compare Models** | Its answer quality is bounded by the same fingerprint GATE, with no confidence surface to show the user when it is unsure. The two questions it answers already have better homes: coordination → the Δ column and carry-over banner; model revision → the IFC exporter's NEW/DELETED/MODIFIED/UNCHANGED manifest |
| **Multi-run trend chart and per-party burn-down** | Longitudinal analytics is a field we decline to compete on, and it dragged a snapshot compaction format and a chart-drawing path through a hand-rolled PDF writer |
| **`CamelWorks.Batch.exe` automation host** | A hidden `NavisworksApplication` consumes a licence seat a free add-in cannot answer for a customer's IT; it adds a host-bound assembly to all 8 install cells; and "I scheduled it at 2 am and nothing happened, there is no log" is the highest-volume support ticket this product can generate. Every hardening rule survives, applied to in-session jobs |

## 7. Build order and gates

Dependency order. Nothing is public until all of it passes §8.

| Stage | Build |
|---|---|
| **0** | Solution merge to §2.2 (20 builds, incl. the `Nav`/`Nav.Clash` split + architecture test); **2027 port of the Dyncamelo editor and node library with a green matrix**; ribbon + 2-pane shell + Home + horizontal switcher; sidecar store (schema versions, journals, group store, lease, atomic write, retention); installer + rollback + installer CI job; the five spikes; and 0-P/0-R/0-D |
| **0-P** | **Model supply + the two-revision sample.** 3 design partners at 2+ companies, under an agreement **explicitly covering publication of the scrubbed fixture in a public repo**. Recruit through the existing BIMCamel/Dyncamelo update-check channel. *Fallback decided now:* fewer than 3 sign → complete the corpus with synthesised pairs, and state in the guide which pairs the published recall was measured on |
| **0-R** | **Rig and licence supply.** Name the 8 host installations, the machines, the licence source and expiry, and the owner. ***Exit condition: a host row that cannot be stood up and smoke-run before Stage 5 is removed from the supported matrix*** — dropped from the support commitment, from `PackageContents.xml`, and named unsupported on the download page. The specific exposure is **2027**: BIMCamel compiles it in CI, the Dyncamelo half never has, and nobody has stated it has been *run* |
| **0-D** | **Distribution decision** — Autodesk App Store, yes or no. If yes, packaging, the EULA and review lead time are Stage-0 constraints on the installer. GitHub is not a channel for this audience: the free Navisworks graveyard is GitHub-only, and that is part of why those tools stayed coordinator-invisible |
| **0-S1** | **Spike: clash write API** — port `TestsEditResultStatus`/`TestsEditResultAssignedTo` to the 2026+ `Assignee` shape on real 2025/2026/2027 rigs (§1.1). Also confirms whether a read-modify-write of clash comments **preserves author, status and id of comments CamelWorks did not author**; if not, the comment projection is cut and due/priority/comment stay sidecar-only |
| **0-S2** | ~~Spike: section box~~ — **answered before it started.** Port `SectionBoxNodes` off `InternalClipPlanes` onto `SetClippingPlanes`, which exists on all four years. What remains is a build task, not a spike: confirm a set section survives capture into a `SavedViewpoint`, and that six aligned planes reproduce the UI's Box mode |
| **0-S3** | **Spike: NWD publish options** via `Application.Options` |
| **0-S4** | **IFC golden-file tests from the current BIMCamel build, green before any BIMCamel code moves.** BIMCamel has no test project today. If extracting `IfcGuid` changes GlobalId derivation for one entity class, every existing user's first CamelWorks export reports 100% NEW / 100% DELETED and nothing catches it |
| **0-S6** | **Spike: `ModelItem` reparenting.** One hour: confirm whether the tree is writable at all through the API or COM. It is the sole stated reason ReTree is cut, and that reason is currently an assumption, not a verified fact |
| **0-S5** | **Spike: review-mode key capture + HUD.** Thread-scoped `SetWindowsHookEx(WH_GETMESSAGE, …)` with the 3D view focused and with a modal open; fallback WinForms `IMessageFilter` (worth an hour — `RibbonTabMerger.cs:53` proves a WinForms `ThreadContext` is alive on Navisworks' UI thread); plus an always-on-top HUD over the 3D view. **`ToolPlugin.OnKeyPress` is struck** — activating a `ToolPlugin` replaces the navigation tool and disables orbit/pan/zoom. *Failure branch:* HUD buttons become the only path and "keyboard-driven review mode" comes out of all copy |
| **1** | Identity (`ElementKey` + `ClashKey` + `GroupId` together), scope resolution, traversal + cache, **all six abstractions** plus `FakeDocument` with an ordered effect log, the `ModelFixture` format, and the contract suite over all six. *A test seam not built alongside the traversal layer is never retrofitted across 30 services* |
| **GATE** | **Fingerprint bake-off. Stage 2 does not start until this passes.** ≥95% recall on unchanged elements and a **false-match rate of exactly 0**, on a committed scrubbed corpus of ≥5 real before/after pairs including: changed export settings; a family swap; a worksharing round-trip; a moved insert point; **a changed model rotation**; **a re-run where one of several results on the same element pair was fixed** (a false-match test); and one DWG/IFC-sourced NWC. *A missed carry-over is annoying; a wrong one shows an unresolved clash as "Approved, signed off by J. Smith" and looks correct to everyone in the room.* **Failure branch decided now:** carry-over ships as an explicit reconcile step the user accepts before anything is applied, and the copy changes from "survives a re-export" to "shows you what it thinks matched, and you accept it" |
| **2** | Report engine (PDF/XLSX/HTML + vector callout & legend layer + image validation) — *four screens emit reports; building it late means retrofitting four callers* |
| **3** | Data services + Data workspace; the job/transaction model and the host job gate |
| **4** | Sets services + Library/Generate; Assign Levels & Zones; Project/Health |
| **5** | Clash services + `Nav.Clash` + the whole Coordinate workspace |
| **5-X** | **Cold-start rehearsal.** Three coordinators who have not seen CamelWorks attempt *open sample → group → triage → export* against the §8 rubric and a stopwatch. **Findings are Stage 6 work.** *Verifying this only at Stage 9 means the first time an outside coordinator touches the product is the moment nothing is left to absorb the result* |
| **6** | Appearance, Colour, Viewpoints, override layers, Section Box, Isolate, context menu |
| **7** | Batch (in-session runner, run-file-at-start, timeouts); **Export: the glTF/GLB and OBJ `IMeshWriter` implementations** + CSV/XLSX; Fix Broken Links |
| **8** | Automate: node wrappers for every new Core service, Open-as-graph, the parity test, Pin-to-ribbon, My Tools, Project Profile |
| **9** | Find a Tool + guide/F1/Limitations + Diagnostics self-test + signed installer + install matrix + migration checklist + field validation + App Store submission |

Stages 3–7 parallelise. 0, 0-P, 0-R, 0-D, 0-S*, 1 and GATE do not.

---

## 8. Definition of done

- Every ribbon button and pane tab implemented. **No greyed-out or "coming soon" affordances.**
- Every new Core service has a Dyncamelo node wrapper — enforced by the `ITool<>` parity test, not prose.
- Green build: **5 assemblies × 4 years = 20 builds**, plus the installer, plus the Linux Core test job.
- **8-row smoke matrix** — {2024, 2025, 2026, 2027} × {Manage, Simulate} — each with its tier-1 workflow.
- Clean install, upgrade-from-BIMCamel, upgrade-from-Dyncamelo, upgrade-from-CamelWorks and
  upgrade-from-legacy-Inno all verified — installer hygiene, so two bundles never co-exist.
  **No data-format compatibility is required**: CamelWorks has no users, so export profiles, set
  recipes and settings are redesigned rather than migrated (see `HARVEST_PROTOCOL.md` §2).
- Every persisted format carries a `schemaVersion` from its first commit, with **published JSON Schemas
  and preserve-unknown-keys on rewrite** (G5), proven by a mixed-version contract test.
- **The per-year API-surface manifest** (G10) fails the build when any Autodesk signature CamelWorks
  binds to changes — the only instrument that catches the next version break before a user does.
- A **Limitations page** exists, with an owner and a format, carrying every refusal this plan makes.
- A **support-load budget** names what we answer and what we refuse, and a **published support
  commitment** states response time, EOL policy and format continuity.
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
| 2 | ~~Section Box works on 2024 only~~ — **CLOSED.** `SetClippingPlanes` / `TrySetClippingPlanes` are present in the managed API on 2024, 2025, 2026 **and** 2027 (verified by reflecting the `Speckle.Navisworks.API` metadata for all four years). The `#if NAV2024` gate rode `InternalClipPlanes` unnecessarily | Use the documented method form — universal across years; the `ClipPlanes` property form and `ClipPlaneSetMode` are 2025+ only. No COM, no per-year branch |
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
