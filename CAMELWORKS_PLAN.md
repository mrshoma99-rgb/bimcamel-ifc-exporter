# CamelWorks — product plan

**One Navisworks add-in that replaces the paid toolbox.**
Merges the BIMCamel IFC exporter + Dyncamelo into a single bundle, one ribbon tab, no licence key.

This document is the research + feature spec: what Navisworks users actually complain about,
what other developers charge money to fix, and the tool list that follows from it.

---

## 0. TL;DR — the strategic read

Three findings drive everything below.

1. **The money in the Navisworks add-in market is concentrated in one place: clash triage.**
   iConstruct, Navistools, Revizto, BIMcollab, Procore and the Autodesk Coordination add-in all sell
   essentially the same thing — *make 4,000 clash results into 40 actionable, assigned, tracked issues
   with a report someone will actually read*. Native Navisworks does not do this, and the free tools
   (GroupClashes, NavisworksGroupie) each do one slice of it. **Nobody free does the whole loop.**

2. **We already own most of the engine.** Dyncamelo is 314 nodes / 37 categories, including 41 clash
   nodes (`Clash.GroupResultsByLevel` / `ByProximity` / `ByGridIntersection` / `BySameItem`,
   `Clash.CompareSnapshots`, `Clash.SnapshotToFile`), 21 viewpoint nodes, 12 appearance nodes,
   11 property nodes (incl. `Properties.SetCustom` and custom tabs), 11 selection-set nodes
   (incl. `SelectionSets.BulkByPropertyValues`), 8 markup nodes, BCF import/export, TimeLiner,
   grids/levels, Excel/CSV/JSON/XML IO. The IFC exporter adds deterministic GUIDs, revision diffing,
   mesh quantities, validation and a federated-origin report.
   **CamelWorks is mostly a UI and packaging problem, not a new-engine problem.**

3. **The wedge is that graphs don't sell — buttons do.** 95% of Navisworks users will never wire a
   node graph. Every CamelWorks tool should be a one-click ribbon button with a real dialog, that
   *happens* to be a saved Dyncamelo graph underneath. That gives us: fast shipping, a consistent
   engine, and a killer differentiator — **every tool is openable and editable**, which no paid
   competitor allows.

Positioning line: *"Everything Revizto charges $350/month for, plus everything iConstruct bundles,
in one free add-in — and you can open the source of every tool."*

---

## 1. What we already have (inventory)

| Product | What it covers |
|---|---|
| **BIMCamel IFC Exporter** | IFC4 / IFC2x3 export, streaming + memory-bounded, geometry instancing, property sets w/ dedup, set→IFC-class mapping + classification, base quantities from mesh, georeferencing + `IfcMapConversion`, **federated origin/units disagreement report**, batch + size splitting, **deterministic GlobalIds → clean revision diff (NEW/DELETED/MODIFIED/UNCHANGED)**, STEP validation, export profiles as JSON |
| **Dyncamelo** | Node-graph engine (314 nodes / 37 categories): clash ×41, viewpoints ×21, list ×19, ModelItem ×17, file IO ×15, math ×15, string ×14, geometry ×13, appearance ×12, export ×12, properties ×11, selection sets ×11, logic ×9, colour ×9, workflow actions ×9, markup ×8, document ×8, search ×7, TimeLiner ×7, camera ×5, transform ×5, analysis ×5 (fall hazard, proximity clustering), units ×4, grids ×3, comments ×3, BCF ×2, audit ×2, takeoff ×2 |
| **Shared** | One BIMCamel ribbon tab, per-user installer (no admin), 2024–2027 support, update check, MIT / Apache-2.0+CC |

**Gap:** none of it is discoverable to a BIM coordinator who just wants to group this week's clashes
and email a PDF. That is the entire CamelWorks thesis.

---

## 2. Pain points — what Navisworks users actually complain about

Grouped by severity × how often it comes up in the research.

### 2.1 Clash detection — the big one

| # | Pain | Detail | Who sells the fix |
|---|---|---|---|
| P1 | **Clash noise** | One pipe through one floor slab returns thousands of results. Coordinators describe "a mountain of low-value reports and coordination fatigue". Native grouping is manual drag-and-drop with no proximity, level, grid or system logic, and **no subgroups** | iConstruct Clash Manager (proximity radius + zone grouping + skip-duplicates), Navistools, GroupClashes / NavisworksGroupie (free, single-purpose) |
| P2 | **Grouping and status don't survive a model update** | Re-run the test after a Revit re-export and grouping/assignment/comments get shuffled or lost. Known open bugs: group association broken in 2027 Issues add-in; groups silently fail to create when items lack a `Category/Name` property; clash viewpoint locations shift when models move | Revizto sync, Procore Clash Manager, Autodesk Coordination Issues (requires ACC) |
| P3 | **Reports nobody reads** | Native HTML/XML clash reports are described as "frustrating to work with"; no PDF output; image control is poor. Every team rebuilds the meeting deck by hand in Excel/PowerPoint | Navistools Clash Manager (PDF report, multiple views per clash — *this is literally its headline feature*), iConstruct |
| P4 | **No accountability layer** | "An unassigned clash is an unresolved clash." Native gives you 5 status values and one `Assigned To` string. No due dates, no priority, no history, no per-discipline burn-down | Revizto (~$350/mo), BIMcollab (~$73/mo), Newforma Konekt, BIM Track |
| P5 | **Setting up the test matrix is a chore** | Arch × Struct × MEP × per-level = dozens of tests hand-built every project, plus per-pair tolerances and rules | Clash Test Creator (paid) |
| P6 | **No context when reviewing** | You get a clash point, not a workable view. Teams bolt on section-box tools just to see the conflict | COINS Auto-Section Box is *the* most-loved free Revit add-in precisely because of this — there is no equivalent inside Navisworks |
| P7 | **No BCF natively** | Round-tripping to Revit is BCF-based in every professional workflow, and Navisworks can't do it. Existing free exporters (CASE/Navis2BCF, emaschas/BCFplugin) are stale and one-directional | BIMcollab BCF Manager, Revizto |

### 2.2 Data & properties

| # | Pain | Detail | Who sells the fix |
|---|---|---|---|
| P8 | **Properties are effectively read-only** | No native bulk-add, bulk-edit, rename, remap or delete of custom properties | iConstruct Data tools, BulkProps (free, GitHub, narrow) |
| P9 | **No Excel round-trip** | Selection Inspector exports CSV, one selection at a time, no way back in. Whole cottage industry of one-off exporters (BIMCAVE, Navis-SystemPropertyExporter, navisworkspluginexporter) exists because of this | BIMCAVE (paid), iConstruct Data Links |
| P10 | **External data linking is slow or broken** | DataTools runs **a query per object** — our own README already warns it "can add many minutes and floods the console with `DATATOOLS_SQL_EXEC` errors". SQL/Excel joins are a paid feature elsewhere | iConstruct Data Links & Integrator (SQL/Excel → model, "single source of truth") |
| P11 | **No model QA / audit** | No naming-standard check, no missing-property check, no duplicate check, no "is this model fit for coordination" gate. Audits are done by eye or by hand | "BIM Manager" plug-ins, SIGNAX audit tooling |
| P12 | **Federated origin/unit mismatches** | Classic silent killer — one model 1000 m off, one in feet. No native report | *nobody, really* — we already have this in the IFC exporter |
| P13 | **Model hierarchy is whatever the authoring tool emitted** | Can't restructure the tree by property (by level, by system, by zone) | iConstruct **ReConstruct** — a headline paid feature |

### 2.3 Sets, search & visualisation

| # | Pain | Detail | Who sells the fix |
|---|---|---|---|
| P14 | **Sets don't travel** | Selection-set XML export "only carries the names of folders, not the sets in the folders or the objects in the sets" — useless for reuse. Search sets export but break the moment a property structure differs | — (universally complained about, rarely solved) |
| P15 | **Sets break silently** | After a model update a search set can return 0 items and nothing tells you | — |
| P16 | **Appearance Profiler is weak** | Documented limitation: to colour 'Cold Water' across 5 floors you need 5 selectors, one per floor. Profiles need the sets to exist in the target file, so they don't travel. No legend output | SIGNAX TOOLS (status colour-coding, ~$100/yr/user), iConstruct |
| P17 | **Viewpoints are unmanaged** | No bulk create/rename/renumber, no batch image export, no contact sheet, no "one viewpoint per set/group" | partially iConstruct (one-click viewpoints from clashes) |
| P18 | **Redlines are trapped** | Markups live on a viewpoint and can't be exported into a report; the redline API isn't even public | — |

### 2.4 Quantities, schedule, files

| # | Pain | Detail | Who sells the fix |
|---|---|---|---|
| P19 | **Quantification is clunky** | Only works where metadata exists; **a property containing letters can't map to a numeric takeoff field** — Autodesk's own advice is "export to Excel and use formulas to remove the letters". Missing dimensions must be measured and typed in by hand | Navistools Quantification (paid), SIGNAX (cumulative volume/area/length/weight + BOQ export) |
| P20 | **TimeLiner autolink is thin** | "Attaching schedule data to numerous model objects is time consuming and inefficient"; limited rules, one user field per task, no both-ways unlinked report | Synchro, Fuzor (4D + mobile progress capture back into the model) |
| P21 | **No progress/actuals workflow** | No installed/approved status stamping, no planned-vs-actual colouring, no S-curve | SIGNAX TOOLS PRO, Fuzor VDC |
| P22 | **Batch Utility is a separate exe with holes** | Can't publish NWD with all the options available in the publish dialog; scheduling means Windows Task Scheduler; can't chain "federate → clash → report → export" | Batch Utility Enhanced (paid, App Store) |
| P23 | **Compare is a colour mess, not a report** | Native Compare highlights differences in red but produces no change list, no counts, no per-discipline breakdown | iFieldModelCompare (paid) |
| P24 | **Export formats cost money** | OBJ, WebGL, 3D PDF, glTF exporters are all per-seat paid apps | ProtoTech exporters (paid), and Navisworks has **no IFC export at all** — which is why BIMCamel exists |

---

## 3. Competitor / paid-feature landscape

| Product | Vendor | What you pay for | Rough price |
|---|---|---|---|
| **iConstruct PRO** | Hexagon / Topcon | Clash Manager (proximity + zone grouping, skip duplicates, one-click viewpoints), **ReConstruct** (rebuild hierarchy), Data Links & Integrator (SQL/Excel joins), Smart DWG Exporter, Smart BCF Exchange | Feature-bundle / custom enterprise |
| **Navistools** | Codemill | Clash Manager (**PDF clash reports, multiple views per clash**), Quantification, Explorer/Manager | Paid, per-seat |
| **Revizto** | Revizto | Issue tracking, clash sync from Navisworks, cross-discipline accountability | ~$350/mo (~$4.2k/yr) |
| **BIMcollab** | BIMcollab | BCF Manager for Navisworks + cloud issue hub (free tier w/ account) | from ~$73/mo |
| **SIGNAX TOOLS / PRO** | SIGNAX | Completion + acceptance date stamping, status colour-coding, comments and documents on elements, cumulative quantities, BOQ export | from ~$100/yr/user |
| **Clash Test Creator** | 3rd party | Auto-builds clash test matrices | Paid |
| **Batch Utility Enhanced** | 3rd party | Better batch conversion/publish | Paid |
| **ProtoTech exporters** | ProtoTech | OBJ, WebGL, 3D PDF, glTF | Paid per format |
| **Verity** | ClearEdge3D / Topcon | Scan-vs-model as-built verification, variance pushed into Navisworks | Enterprise (quote-only) |
| **Fuzor / Synchro** | Kalloc / Bentley | 4D with rules, resource auto-matching, mobile progress capture | Enterprise |
| **Autodesk Coordination Issues** | Autodesk | Batch issue creation, clash grouping — **but tied to ACC** | Bundled w/ ACC |
| *Free but narrow* | — | GroupClashes (simonmoreau), NavisworksGroupie (dnenov), BulkProps (shaun-wilson), CASE BCF exporter, Procore Clash Manager | Free |

**The gap in one sentence:** the free tools each solve one step; the paid tools solve the loop but are
closed, expensive and per-seat. There is no free, open, *complete* Navisworks toolbox. That's CamelWorks.

---

## 4. The CamelWorks tool list

Effort: **S** = days, **M** = a few weeks, **L** = a month+.
Engine: **✅** = Dyncamelo/BIMCamel already has the primitives, **🔨** = new work.

### Module A — Clash & Coordination *(build this first — it's where the money is)*

| # | Tool | What it does | Kills | Effort | Engine |
|---|---|---|---|---|---|
| A1 | **Clash Grouper** | One dialog, multi-rule grouping: by level, grid intersection, model pair, discipline, system, proximity radius, same-item, or any property. Chained rules build subgroup names (Navisworks has no real subgroups). Skip-duplicates + proximity tolerance. "Keep existing groups" so a re-run doesn't nuke last week's work | P1 | M | ✅ (`Clash.GroupResultsBy*` ×6 exist) |
| A2 | **Clash Fingerprint / status carry-over** | Hash each clash by its element-GUID pair + rounded location → status, assignment, comment thread, priority and group **survive model updates and test re-runs**. This is the single most valuable thing in the whole plan and nobody free does it | P2 | L | ✅ engine + reuse `IfcGuid` deterministic-ID work |
| A3 | **Triage Board** | Spreadsheet-style panel: filter New/Active, bulk assign discipline + person + due date + priority, comment threads, and a keyboard-driven review mode — `N` for next clash → auto-isolate, section box, ghost context, save viewpoint | P4, P6 | L | ✅ appearance/viewpoint/camera nodes |
| A4 | **Clash Test Builder** | Build the whole matrix in one grid: disciplines × levels/zones, per-pair tolerance and rules, saved as a reusable project template | P5 | M | 🔨 (Clash API write) |
| A5 | **Coordination Report** | Branded PDF / XLSX / HTML: cover page, summary charts, one image per **group** (not per clash), location as level + nearest grid, responsible party, status, comment history. Templated | P3 | M | ✅ (`Export.ViewpointImage`, `Clash.SummaryTable`) + 🔨 PDF writer |
| A6 | **Clash Delta** | This run vs last run: resolved / new / persisting / **regressed**, per discipline, with a burn-down chart for the weekly meeting | P2, P4 | S | ✅ (`Clash.CompareSnapshots`, `SnapshotToFile`) |
| A7 | **BCF Hub** | Real BCF 2.1 + 3.0 round-trip panel: push groups/clashes/viewpoints out, pull BCF back in, re-attach to elements by GUID, sync status both ways | P7 | M | ✅ (2 nodes → promote to a panel) |
| A8 | **Auto Section Box** | The COINS Auto-Section-Box equivalent for Navisworks: box the selection/clash with an N-metre context margin, ghost everything else, one hotkey. Sounds trivial, will be one of the most-used buttons in the plug-in | P6 | S | ✅ (`Viewpoint.SetSectionBox`) |

### Module B — Data & Properties

| # | Tool | What it does | Kills | Effort | Engine |
|---|---|---|---|---|---|
| B1 | **Property Manager** | Bulk add / edit / rename / delete custom tabs and properties across a set or search result. Calculated properties: concat, arithmetic, regex extract, unit convert, lookup table | P8 | M | ✅ (`Properties.SetCustom`, `CustomTabs`, `RenameCustomTab`) |
| B2 | **Excel Round-Trip** | Export any set's properties to XLSX with chosen columns → edit in Excel → import back as custom properties, keyed by GUID, with a preview diff before writing | P9 | M | ✅ (`Excel.*`, `Table.JoinByKey`) |
| B3 | **Data Joiner** | Join CSV / XLSX / SQL / ODBC data onto elements by key **once, cached, written as real properties** — instead of DataTools' per-object query storm | P10 | M | 🔨 (+ ✅ join nodes) |
| B4 | **Model QA / Audit** | Rule-based checks: missing property, empty value, non-numeric in a numeric field, duplicate GlobalId or name, naming-convention regex, unit mismatch, orphan geometry. Per-model scorecard, exportable QA report, and a **coordination-readiness gate** ("this model is not fit to federate") | P11 | M | ✅ (`Audit.DuplicateItems`, `Audit.MissingProperty` → expand) |
| B5 | **Federation Health** | Origin, rotation, units and bounding box of every loaded model, flagging disagreements | P12 | S | ✅ **already built** in the IFC exporter — just needs its own button |
| B6 | **ReTree** | Rebuild the selection tree by property — group by level, then system, then zone. iConstruct's ReConstruct, free | P13 | L | 🔨 |

### Module C — Sets, Search & Zoning

| # | Tool | What it does | Kills | Effort | Engine |
|---|---|---|---|---|---|
| C1 | **Set Library** | Save search sets as portable templates **with variables** (`Level = <param>`), apply to any project. Bulk-generate one set per distinct value of a property (per level / per system / per type) with auto-foldering. Export/import as a shareable JSON library — and export resolved GUID lists too, closing the hole where Navisworks' own set XML loses its contents | P14 | M | ✅ (`SelectionSets.BulkByPropertyValues`, `CreateFromSearch`, `CreateFolder`) |
| C2 | **Set Health** | After a model update: which sets now return 0, or ±50% vs last run. Broken search sets fail silently today | P15 | S | ✅ |
| C3 | **Smart Zoning** | Assign every element a Level / Zone / Grid-square property from its bounding box vs the model's grids and levels — then everything else (grouping, reports, takeoff, colour) can key off it | P1, P16, P19 | M | ✅ (`Zone.AssignByVolumes`, `Grids.*`, `Proximity.Cluster`) |

### Module D — Visualisation & Presentation

| # | Tool | What it does | Kills | Effort | Engine |
|---|---|---|---|---|---|
| D1 | **Colour by Property** | Pick a property → automatic distinct-value colours or a numeric gradient → **legend baked into the viewpoint** → save as a profile that travels between files. No pre-built sets required | P16 | S | ✅ (`Appearance.ColorByValues`, `Color.ByValues`, `Color.Gradient`) |
| D2 | **Status & Progress** | Stamp elements installed / approved / on-hold with dates, colour by status, roll up % complete by zone. SIGNAX's paid headline feature | P21 | M | ✅ (properties + appearance) |
| D3 | **Viewpoint Manager** | Bulk create / rename / renumber / re-folder, batch render to PNG at a fixed resolution, contact-sheet PDF, copy overrides across viewpoints, "one viewpoint per set/group" | P17 | M | ✅ (21 viewpoint nodes + `Export.ViewpointImage`) |
| D4 | **Markup Pack** | Text, arrows, clouds, numbered tags on viewpoints — and crucially, **pulled into the reports** | P18 | S | ✅ (8 markup nodes, already on the hidden redline API) |

### Module E — Quantities

| # | Tool | What it does | Kills | Effort | Engine |
|---|---|---|---|---|---|
| E1 | **Quick Takeoff** | Sum any numeric property grouped by any other (per level / system / type), unit conversion, and a **regex cleaner for non-numeric values** — directly fixing Autodesk's own "export to Excel and strip the letters" workaround. Straight to XLSX | P19 | M | ✅ (`Takeoff.SumPropertyByGroup`, `Units.Convert`) |
| E2 | **Geometric Quantities** | Compute volume / area / length / width / height from the mesh where the property is missing — no more measuring by hand and typing it in | P19 | S | ✅ **already built** (`MeshQuantities.cs` for IFC base quantities) |
| E3 | **BOQ Export** | Templated workbook per WBS, with a stable element→line-item link so a re-export diffs cleanly instead of starting over | P19 | M | ✅ + 🔨 |

### Module F — Schedule / 4D

| # | Tool | What it does | Kills | Effort | Engine |
|---|---|---|---|---|---|
| F1 | **TimeLiner Auto-Link** | Rule library: multi-field, regex, fallback chains, link by set / property / zone. **Both-ways unlinked report** — tasks with no elements *and* elements with no task — plus bulk fix | P20 | M | ✅ (`TimeLiner.AutoAttachByProperty`, 7 nodes) |
| F2 | **Schedule Round-Trip** | CSV / XLSX / P6 / MSP import with a diff of what changed since last import | P20 | M | 🔨 |
| F3 | **Progress Snapshot** | Weekly actual-vs-planned capture, colour view, S-curve, exportable | P21 | L | ✅ + 🔨 |

### Module G — Files, Batch & Export

| # | Tool | What it does | Kills | Effort | Engine |
|---|---|---|---|---|---|
| G1 | **Batch Console** | Inside Navisworks: append/refresh a list of NWCs → publish NWD/NWF **with the full publish options the native Batch Utility can't reach** → run clash tests → export reports → export IFC. Saved as a named job, schedulable. This is the "Friday night federation" every BIM manager runs by hand | P22 | L | ✅ (`Document.AppendFiles/Refresh/Save`, `Export.NWD`, workflow nodes) |
| G2 | **Model Change Diff** | Two versions of the same model → added / deleted / moved / geometry-changed / property-changed, as a **report + counts + colour view + saved viewpoints**, not just red highlighting | P23 | M | ✅ (extend the IFC revision manifest to the Navisworks doc; `Snapshot.Diff` exists) |
| G3 | **Export Hub** | IFC (shipping) + glTF/GLB, OBJ, DWG-per-set, CSV/XLSX, and a self-contained web-viewer package — all the things ProtoTech charges per-format for | P24 | L | ✅ IFC done; 🔨 rest |
| G4 | **Link Doctor** | Repoint broken file paths in an NWF, rename models, find missing sources | — | S | ✅ (`Model.FileName`, `Document.*`) |

### Module H — The glue *(this is the differentiator)*

| # | Tool | What it does | Effort |
|---|---|---|---|
| H1 | **Automate tab (Dyncamelo)** | The graph editor ships inside CamelWorks. **Every one-click tool above is a saved graph** — "Edit this tool" opens it on the canvas | S (already exists) |
| H2 | **Pin graph to ribbon** | Any graph the user builds becomes a ribbon button with its inputs as a generated dialog. Turns CamelWorks into a platform, not a product | M |
| H3 | **Project Profile** | One JSON carrying set library + clash matrix + appearance profiles + report template + IFC mapping + batch job. Load it on a new project and you're configured in 30 seconds | M |
| H4 | **Unified ribbon + panel shell** | One CamelWorks tab, grouped panels, consistent dialogs, shared About/update check | M |

---

## 5. Build order

**Tier 1 — ship this and CamelWorks is already worth installing**
A1 Clash Grouper · A2 Status carry-over · A3 Triage Board · A5 Coordination Report · A8 Auto Section Box ·
D1 Colour by Property · B1 Property Manager · B2 Excel Round-Trip · C1 Set Library · H4 Ribbon shell

> Rationale: this is the exact feature set iConstruct + Navistools + Revizto charge for, and it's ~80%
> assembled from nodes that already work. A5's PDF writer is the only meaningful new subsystem.

**Tier 2 — the reason people keep it installed**
A6 Clash Delta · A7 BCF Hub · B4 Model QA · B5 Federation Health · C2 Set Health · C3 Smart Zoning ·
D3 Viewpoint Manager · E1 Quick Takeoff · E2 Geometric Quantities · G2 Model Change Diff · G1 Batch Console

**Tier 3 — the moat**
A4 Clash Test Builder · B3 Data Joiner · B6 ReTree · D2 Status & Progress · D4 Markup Pack ·
E3 BOQ · F1–F3 4D suite · G3 Export Hub · G4 Link Doctor · H2 Pin-to-ribbon · H3 Project Profile

**Deliberately out of scope:** scan-vs-model verification (Verity's domain, needs point-cloud maths and
a hardware ecosystem), cloud issue hosting (that's Revizto's actual business — we round-trip BCF to it
instead), and VR/rendering.

---

## 6. How the merge works

```
CamelWorks.bundle
├── CamelWorks.Core        shared: document access, GUID/fingerprint, units, geometry,
│                          mesh quantities, report/PDF writer, profile JSON
├── CamelWorks.Tools       one class per ribbon tool; each = dialog + a saved graph
├── CamelWorks.Graph       Dyncamelo engine + editor (the "Automate" tab)
├── CamelWorks.Ifc         BIMCamel exporter (unchanged internals)
└── CamelWorks.UI          ribbon, dock panes, shared dialog kit, About, update check
```

- **One ribbon tab, panel-grouped:** Coordinate · Data · Sets · Visualise · Quantify · Schedule · Files · Automate.
- **Tools are graphs.** Ship each tool as a `.dyc` embedded resource. The dialog binds to the graph's
  input nodes. "Edit this tool" opens it in the Automate tab. One engine, one bug surface, and every
  tool is inspectable — which no competitor offers.
- **Backwards compatibility:** BIMCamel and Dyncamelo installers detect CamelWorks and step aside;
  CamelWorks upgrades both in place (the installer already does upgrade/remove of prior installs).
- **Licensing:** Dyncamelo is Apache-2.0 + Commons Clause, BIMCamel is MIT. CamelWorks as a whole
  inherits the stricter one (Commons Clause: free to use commercially, not to resell). Worth a
  deliberate decision — going fully MIT would be a stronger community signal but gives away the
  "only we may sell it" reservation.

---

## 7. Technical risks — the honest list

| Risk | Why it matters | Mitigation |
|---|---|---|
| **Clash fingerprint stability (A2)** | The whole triage value prop rests on it. Element GUIDs shift when a Revit family is swapped or a model is re-exported with different settings | Composite key: instance GUID pair → fall back to (source-file + path-hash + rounded centroid + bbox). Tune against real re-export pairs before committing to the schema |
| **Custom property persistence (B1/B2/D2)** | Custom tabs written through the COM API live in the NWF/NWD, not the source model — a model refresh can drop them | Persist the authoritative data in a sidecar JSON keyed by fingerprint; re-stamp on load. Never let the model be the only copy |
| **Redline/markup API is undocumented (D4)** | Already used in Dyncamelo but it's a hidden interface — could break in any Navisworks release | Feature-flag it, degrade gracefully, test on every year build (we already build 2024–2027 on CI) |
| **Clash API write surface (A1/A4)** | Grouping and test creation are only partly exposed | Prototype A4 before promising it; A1 is proven (nodes already work) |
| **No native PDF writer (A5)** | Reports are the headline deliverable | Own minimal PDF writer, same discipline as `StreamingStepWriter` — no third-party runtime dependency, which is already a project principle |
| **Scope creep** | 32 tools is a lot | Tier 1 only, then measure what people actually click |

---

## 8. Sources

Pain points and competitor research:
BIM Track — [Navisworks pain points](https://bimtrack.co/blog/blog-posts/3-common-pain-points-of-navisworks-and-how-bim-track-solves-them) ·
Newforma — [3 common pain points of Navisworks](https://www.newforma.com/3-common-pain-points-of-navisworks-and-newforma-konekt-solves-them/) ·
BIM Heroes — [Beyond the report: clash detection that protects margins](https://bimheroes.com/navisworks-clash-detection/) ·
Atelier 7 — [Clash Detective field guide](https://www.atelier7arch.com/2026/04/navisworks-clash-detective-complete.html) ·
Autodesk — [Appearance Profiler](https://help.autodesk.com/view/NAV/2025/ENU/?guid=GUID-37A831D3-AE33-45AE-9679-6A8610023E10) ·
Autodesk — [Quantification + Excel formulas workaround](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/Navisworks-Manage-Using-Quantification-and-Excel-formulas-to-manipulate-data.html) ·
Autodesk — [Search Set files](https://help.autodesk.com/cloudhelp/2018/ENU/Navisworks/files/GUID-BC82DD53-3A64-4D22-9217-BC8DBE53B059.htm) ·
Autodesk — [Clash grouping doesn't work](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/Clash-grouping-functionality-doesn-t-work-in-Navisworks.html) ·
Autodesk Community — [2027 Issues add-in: missing clash-group association](https://forums.autodesk.com/t5/navisworks-forum/navisworks-2027-issues-add-in-missing-association-with-clash/td-p/14083289) ·
Autodesk — [Batch Utility](https://help.autodesk.com/cloudhelp/2017/ENU/Navisworks-Manage/files/GUID-974E2165-7403-4025-B0D0-F7EBC46AC592.htm) ·
Autodesk — [Compare two versions of a model](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/How-to-compare-two-versions-of-a-model-in-Navisworks.html) ·
AUGI — [Power up with search and selection sets](https://www.augi.com/articles/detail/power-up-with-search-and-selection-sets)

Paid products:
[iConstruct Clash Manager](https://iconstruct.com/feature/clash-manager/) ·
[Navistools Clash Manager (AECbytes)](https://www.aecbytes.com/tipsandtricks/2020/issue92-clashmanager.html) ·
[SIGNAX TOOLS](https://signax.io/tools) / [pricing](https://signax.io/price) ·
[Revizto pricing](https://www.g2.com/products/revizto/pricing) ·
[BIMcollab BCF Managers](https://www.bimcollab.com/en/products/bimcollab-nexus/bcf-managers/) ·
[ClearEdge3D Verity](https://www.clearedge3d.com/products/verity/) ·
[Batch Utility Enhanced](https://marketplace.autodesk.com/apps/c4588eb3-80bf-4fda-8097-dc69c39cecd4) ·
[Autodesk Coordination Issues add-in](https://marketplace.autodesk.com/apps/d5cddf8d-500b-43f1-8a2e-cf5f4386218d) ·
[Fuzor Construction](https://apps.autodesk.com/NAVIS/en/Detail/Index?id=751529005583123743&appLang=en&os=Web) ·
[Procore Clash Manager](https://support.procore.com/products/procore-bim-plugins/user-guide-clash-manager-for-navisworks)

Free/open-source comparables:
[GroupClashes (simonmoreau)](https://github.com/simonmoreau/GroupClashes) ·
[NavisworksGroupie (dnenov)](https://github.com/dnenov/NavisworksGroupie) ·
[BulkProps (shaun-wilson)](https://github.com/shaun-wilson/BulkProps-Navisworks-Plugin) ·
[BCFplugin (emaschas)](https://github.com/emaschas/BCFplugin) ·
[COINS Auto-Section Box](https://apps.autodesk.com/RVT/en/Detail/Index?id=8920075109543819118&appLang=en&os=Win32_64)
