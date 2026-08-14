# BIMCamel IFC Exporter — User Guide

Export Autodesk Navisworks models (NWD / NWF / NWC and anything Navisworks
opens) to clean IFC4 or IFC2x3 — with mapped IFC classes, property sets,
quantities, classification codes, storeys and georeferencing.

This guide covers **v0.10.0 and later** (the five-tab pane with Smart setup
and the Pre-flight panel). It is the long-form manual; the
[online documentation](https://bimcamel.com/Export-Navisworks-to-Ifc/docs)
covers the same ground with screenshots, and the
[product page](https://bimcamel.com/Export-Navisworks-to-Ifc) has the
download and FAQ.

---

## Contents

1. [Requirements & install](#1-requirements--install)
2. [The two workflows](#2-the-two-workflows)
3. [Quick export — one click](#3-quick-export--one-click)
4. [The pane, tab by tab](#4-the-pane-tab-by-tab)
5. [Smart setup](#5-smart-setup)
6. [The Pre-flight panel](#6-the-pre-flight-panel)
7. [The detailed workflow, step by step](#7-the-detailed-workflow-step-by-step)
8. [Mapping deep-dive](#8-mapping-deep-dive)
9. [Coordinates & georeferencing](#9-coordinates--georeferencing)
10. [Batch export & size splitting](#10-batch-export--size-splitting)
11. [Profiles — reuse everything weekly](#11-profiles--reuse-everything-weekly)
12. [The export report & revision diff](#12-the-export-report--revision-diff)
13. [Performance](#13-performance)
14. [Limitations — what this exporter cannot do](#14-limitations--what-this-exporter-cannot-do)
15. [Troubleshooting](#15-troubleshooting)

---

## 1. Requirements & install

- **Autodesk Navisworks Manage or Simulate 2024, 2025, 2026 or 2027**, on
  Windows. A Navisworks add-in must be compiled per API year; the installer
  ships one build per supported year and Navisworks loads the matching one.
- No licence key, no account, no telemetry. MIT-licensed and
  [open source](https://github.com/mrshoma99-rgb/bimcamel-ifc-exporter).

**Install:** download
[`BIMCamelSetup.exe`](https://github.com/mrshoma99-rgb/bimcamel-ifc-exporter/releases/latest/download/BIMCamelSetup.exe)
and run it. It installs per-user into
`%APPDATA%\Autodesk\ApplicationPlugins` (no admin rights), registers an
Apps entry for uninstall, and clears Windows' "downloaded file" mark that
otherwise stops Navisworks loading manually-copied DLLs. If SmartScreen
appears (the installer is unsigned), click **More info → Run anyway**.

Restart Navisworks — a **BIMCamel** ribbon tab appears with the
**IFC exporter** button. If Dyncamelo is installed too, both tools share
that one tab. The pane checks GitHub once a day and offers new releases;
declining never nags again until the next version.

Silent deploys: `BIMCamelSetup.exe /silent`, uninstall with
`/uninstall /silent`. A zip with the same content is published next to the
setup exe for scripted rollouts, with SHA-256 checksums for both.

## 2. The two workflows

The pane is built for two different days at work:

- **The quick export.** You need geometry in IFC, now. Open the pane, click
  **Export IFC**, pick a file name. Every default is chosen for this path:
  IFC4, units read from the model, whole visible model, balanced quality,
  instancing on, validation on. Elements without mapping rules export as
  `IfcBuildingElementProxy` — an honest geometry hand-off.
- **The detailed, well-mapped deliverable.** Real IFC classes, property
  sets, storeys, classification codes, georeferencing — reviewed and
  trusted *before* the file is written, and reproducible next revision.
  That workflow is: **scope → Smart setup → review → pre-flight → export →
  save profile**, and the rest of this guide walks it.

Nothing about the detailed path ever blocks the quick one: the Pre-flight
panel is informational, and the Export button never waits on anything.

## 3. Quick export — one click

1. Open the model (or federation) in Navisworks.
2. **BIMCamel ribbon → IFC exporter.**
3. Click **Export IFC**, choose where to save.

What you get by default: every visible element with geometry, welded
meshes, all property sets, materials/colours, base quantities, repeated
geometry instanced, the file validated after writing, and a full report of
what happened. What you don't get without mapping: semantic IFC classes —
everything is `IfcBuildingElementProxy` until rules or roles say otherwise
(the report tells you exactly how many).

> **Before any big export:** deactivate DataTools / external-database links
> (Home → DataTools). They run a database query per object and can
> dominate the entire export time. The pane warns about this, and the
> report detects the symptom after the fact.

## 4. The pane, tab by tab

Every card in the pane wears one of three chips, so a glance answers
"what must I fill in, what is optional, what fills itself?":

| Chip | Meaning |
|---|---|
| **REQUIRED** | The export reads this. Defaults are already valid. |
| **OPTIONAL** | Enrichment. Blank/off is a legitimate choice. |
| **✨ AUTO** | Smart setup fills it. Everything stays hand-editable. |

### Export — run it
The landing tab. **Smart setup** (one button, section 5), **Output**
(IFC schema, source units — both REQUIRED, both defaulted), **Scope**
(REQUIRED: whole model / current selection / active section box / one
saved set / multiple sets batch), **File options** (geometry quality,
size splitting, instancing, post-export validation, size & time breakdown),
**Pre-flight** (section 6) and the **Report** console.

### Data — what each element carries
- **What to export:** properties, materials/colours, base quantities
  (volume / area / length / width / height as `Qto_*BaseQuantities`).
- **Property sets to include:** the pset checklist, filled by Smart setup's
  model sample — or by **⚡ Scan selection instead**, the manual
  alternative that reads only the elements you selected in Navisworks.
  Nothing scanned = every pset exports. Your unticks survive rescans.
- **Semantic roles:** which source property carries **Type**
  (→ `IfcElementType` + ObjectType), **Level** (→ `IfcBuildingStorey`),
  **Material** (→ `IfcMaterial`) and **Classification**
  (→ `IfcClassificationReference`). Smart setup proposes these from the
  scan; blank = role off.
- **Parameter mapping:** rename a source property or move it into a
  different target pset (e.g. scattered fire ratings →
  `Pset_WallCommon.FireRating`).

### Mapping — which IFC class each thing becomes
One grid, one toolbar. Each rule: **Navisworks set → IFC class**, optional
**PredefinedType**, optional **classification code**. Unmapped elements
stay `IfcBuildingElementProxy`. **🔍 Preview** resolves the rules against
your scope *before export* and prints mapped vs proxy counts, the
per-class breakdown, property coverage and a geometry estimate — into the
console right below the button. A second card holds the classification
system name (written as `IfcClassification`) and "also export each mapped
set as" `IfcGroup` / `IfcSystem` / `IfcZone`. Full detail in section 8.

### Coordinates — where the model sits
**Base point** (how large the stored coordinates are: geometry origin /
model origin / custom, with a live preview line) and **Georeferencing**
(IFC4 `IfcMapConversion`: CRS such as `EPSG:27700`, survey point,
rotation). **Check federated model origins** prints every loaded model's
origin, rotation and units and says plainly whether they agree. Section 9
explains the base-point / survey-point distinction — the one everyone
trips over.

### Structure — names
`IfcProject` / `IfcSite` / `IfcBuilding` names and the fallback storey
name for elements without a Level value. Storeys themselves come from the
**Level role** on the Data tab; when the model's grid levels carry names
and elevations, real elevations are written automatically.

## 5. Smart setup

One button on the Export tab runs every auto-detect in dependency order:

1. **Find sets** — reloads saved/search sets (they also reload whenever
   you open the Mapping tab).
2. **Scan** a bounded, discipline-spread sample of the model (~1,000
   elements — never a full walk) to discover property sets.
3. **Fill blank roles** — proposes Type / Level / Material /
   Classification sources from the scan.
4. **Propose mapping rules** — matches set names against ~90 known
   category keywords ("External Walls" → IfcWall).
5. **Preview** — resolves everything against your scope and fills the
   Mapping console + the Pre-flight panel.

Then it prints exactly what it did: sets found, psets discovered, roles
filled vs kept, rules added, and the preview digest.

**The contract: Smart setup never overwrites anything you typed.** Roles
you set are kept (it says "2 filled, 2 kept"), rules with a class already
chosen are left alone, pset unticks survive. One nuance, stated on the
button too: a role or rule you *deliberately blanked* is indistinguishable
from one never set, so re-running proposes it again.

Cost: roughly what collecting the scope costs (the preview step walks the
scope; geometry is never read). The pane stays responsive and shows
progress throughout.

## 6. The Pre-flight panel

The trust surface: ✓/⚠ rows on the Export tab that answer *"can I trust
this file?"* before you click Export — informational only, never blocking.

**Live rows** recompute every time you open the tab: schema, units,
georeferencing configuration, federation agreement, and configuration
contradictions — properties ON with every pset unticked, CRS filled in
while georeferencing is OFF, two batch sets whose sanitised filenames
would overwrite each other, a rule bound to a set name that exists twice.

**Scanned rows** fill after Smart setup or Preview and carry their
provenance ("Scanned 14:32 · Whole model"): elements collected (and how
many *hidden* subtrees were excluded — the "I hid a discipline for
viewing" catch), rule-match rate, storey coverage with the **distinct
storey count** (a wrong Level role usually still "has a value" — one bogus
storey exposes it instantly), role coverage, primitive count. Change any
setting they depend on and they drop their green and say so — a stale
green tick is worse than none.

Severity follows one rule: **unset is normal (grey), contradiction is
amber.** Zero rules is a legitimate geometry hand-off. No CRS is a normal
local-grid project. Federated origins that differ are federation working.
But data that will *silently not be written*, or IFC2x3 dropping classes
you mapped — those warn, each with a pointer to the tab that fixes it.
Sampled numbers say so: "≈74% · sample 500 of 48,112", "≥ 4 storeys".

## 7. The detailed workflow, step by step

The weekly coordinator loop, using everything above:

1. **Prepare in Navisworks.** Save selection/search sets for everything
   you want classified. A search set is already an IF/THEN rule —
   "Category = Walls AND Name contains External" — build it once in Find
   Items, and the mapping stays live as the model changes. Deactivate
   DataTools links.
2. **Pick the scope** on the Export tab.
3. **✨ Smart setup.** Sets, psets, roles, proposed rules, first preview —
   one click.
4. **Review Data:** are the four roles pointing at the right properties?
   Untick noisy psets you don't want shipped.
5. **Review Mapping:** finish what auto-mapping couldn't ("no obvious
   class" sets), add PredefinedTypes and classification codes, set the
   classification system name. **🔍 Preview** after edits.
6. **Coordinates** once per project: CRS + survey point if the deliverable
   is georeferenced; check federated origins agree.
7. **Pre-flight** on the Export tab: all green or explained amber?
8. **Export IFC.** Read the report — mapped counts, proxy counts with
   reasons, storeys, validation result, timing breakdown.
9. **Save a profile** (💾 in the title bar). Next revision: load profile →
   Smart setup (fills anything new, keeps your decisions) → Preview →
   Export. The revision diff in the report tells you what changed since
   last export.

## 8. Mapping deep-dive

**Rules compose.** Earlier rules win per assignment kind, and a row may
set a class, a classification code, or both — so a broad rule
("All walls → IfcWall") and a narrow one ("External walls → code
`EF_25_10`") work together instead of competing.

**PredefinedType** writes the IFC enum on classes that have one
(`IfcWall.PredefinedType = SHEAR`, doors/windows/stairs/ramps/piles…).
Unknown values fall back safely at export and validation flags them.

**Classification codes** from the grid column override the Classification
*role* (Data tab) for elements in that set. The system name field becomes
`IfcClassification.Name` ("Uniclass 2015", "OmniClass"); codes become
`IfcClassificationReference` with proper associations, in both IFC4 and
IFC2x3 arities.

**Groups.** "Also export each mapped set as" emits every mapped set as an
`IfcGroup` / `IfcSystem` / `IfcZone` with `IfcRelAssignsToGroup`
membership — use IfcSystem for MEP systems, IfcZone for spatial zones.

**IFC2x3 caution:** some classes have no 2x3 entity. Elements you mapped
to them export as proxy under 2x3 — the preview, pre-flight and report all
count these separately and say "export IFC4 to keep them".

**Set names are the binding.** Rules address sets by display name; if two
sets share a name, rules bind to the first (pre-flight warns). Renaming a
set in Navisworks orphans its rule — the row keeps the old name so you can
re-point it.

## 9. Coordinates & georeferencing

The two settings answer different questions:

- **Base point** answers *"how big are the numbers stored in the file?"*
  It never moves the model: the placement chain puts everything back at
  its world position regardless. **Geometry origin** (default) keeps
  coordinates small — the right choice for viewers and for Revit, which
  refuses geometry more than ~10 miles from the origin. **Model origin**
  keeps raw world coordinates. **Custom** lets you type a project base
  point. The live preview line shows the resulting origin.
- **The survey point** answers *"where is this model on Earth?"* — the
  IFC4 `IfcMapConversion`: a CRS name (e.g. `EPSG:27700`), eastings /
  northings / elevation of the model origin in that CRS, and grid
  rotation. Written only when you actually provide data — an empty map
  conversion is worse than none — and only in IFC4 (2x3 has no entity for
  it; the placement still carries the world offset).

**Federated models:** *Check federated model origins* before exporting a
multi-model federation. Different origins are normal (that's federation
working); **mixed declared units are not** — one unit scale applies to the
whole export, so a mixed-unit federation ships one discipline at the wrong
size. Pre-flight ambers exactly that case.

## 10. Batch export & size splitting

**Batch** (scope = "Multiple sets → one IFC each"): tick sets, choose an
output folder, get one IFC per set — the standard per-discipline or
per-zone package cut. File names come from set names (sanitised); the
pre-flight warns when two would collide. Preview under batch measures the
union of the ticked sets.

**Size splitting** (File options): cap file size (default 200 MB) and
larger exports roll into `name_001.ifc`, `name_002.ifc`, … — each a
complete, standalone IFC. Composes with batch. Split parts share
deterministic identity: the project/site/building GUIDs stay stable, so
viewers federate the parts cleanly.

## 11. Profiles — reuse everything weekly

💾 / 📂 in the title bar save and load a `.json` profile: schema, units,
scope, quality, base point, coordinates and georeferencing, all
checkboxes, semantic roles, parameter rules, the full mapping grid,
classification system, group setting, spatial names, split settings.
(List *ticks* — psets and batch — and the theme are not stored.)

Profiles are the loop-closer: `P03_structural.json` today is next month's
one-click setup. Keep them in the project folder, next to the deliverable.
Old profiles load in new versions — the format is stable.

## 12. The export report & revision diff

Every export writes a full account into the Report console (⧉ copies it):
file(s) + sizes, schema, scope, units, base point and georeferencing as
actually written, element and triangle counts, **anything that did NOT
reach the IFC and why**, instancing ratio, mapped vs proxy counts with
reasons, storeys, types/materials/classifications, pset dedup ratio,
quantities, a full timing breakdown (COM conversion, geometry read,
property harvest, weld, write, UI), peak memory, and the validation
result.

**Validation** (on by default) checks the written file for structural
damage and for the mistakes an exporter can make — wrong attribute counts,
bad enum tokens, dangling references, duplicate GUIDs.

**Revision diff:** each export writes a `.bcmanifest` sidecar. The next
export of the same file reports NEW / DELETED / MODIFIED / UNCHANGED
element counts against it — your "what changed since P02?" answer, for
free. Deterministic GUIDs make diffs meaningful across revisions and
split parts.

## 13. Performance

What actually costs time, in order:

1. **DataTools / external-database links** — a query per object. Turn them
   off first; the report detects the symptom (slow property harvest) and
   names the fix.
2. **Geometry conversion** — inherently single-threaded through the
   Navisworks COM API; the pane streams and stays responsive, and shows
   the real breakdown in the report.
3. **Property harvest** — proportional to psets exported. Unticking noisy
   psets on the Data tab cuts it directly (excluded psets are never read).

What the exporter does for you: scans are bounded samples, never full
walks; each mapping set resolves once no matter how many rules reference
it; item keys are computed once per run; **instancing** (on by default)
stores repeated geometry once — on repetitive models that is both a much
smaller file and a faster export. **Geometry quality** trades weld
tolerance and coordinate precision ("Small file" / "Balanced" / "High
detail") — it cannot add detail Navisworks didn't keep, since tessellation
is fixed when the NWC is made.

## 14. Limitations — what this exporter cannot do

Navisworks models are triangulated geometry + properties. That means, honestly:

- **Mesh geometry only** — every element is `IfcFacetedBrep` /
  triangulated surfaces. No parametric solids, no extrusion profiles, no
  native BIM re-authoring. Editing-grade IFC needs the authoring tool.
- **No spaces/rooms** (`IfcSpace`) — Navisworks does not carry them.
  Consequently no space boundaries and no COBie space/zone sheets.
- **No openings** (`IfcOpeningElement`) or void/fill relationships —
  openings arrive from Navisworks as already-cut geometry.
- **No material layer sets** (`IfcMaterialLayerSet`) — material names and
  colours only.
- **No element connectivity** (`IfcRelConnects*`).
- **IFC2x3 cannot carry** `IfcMapConversion` georeferencing or a handful
  of newer classes (counted and reported when it drops something).

## 15. Troubleshooting

| Symptom | Cause & fix |
|---|---|
| Plugin missing after manual copy; `PLUGIN_LOAD_02` / `0x80131515` | Windows kept the browser's "downloaded" mark on the DLLs. Run the setup exe (strips it automatically), or `Get-ChildItem "$env:APPDATA\Autodesk\ApplicationPlugins\BIMCamel.bundle" -Recurse -File \| Unblock-File`. |
| SmartScreen blocks the installer | Unsigned download: **More info → Run anyway**. Verify the SHA-256 checksum published next to the release asset. |
| Export is far slower than the model size suggests; report says "SLOW PROPERTY HARVEST" | An active DataTools link queries a database per object. Home → DataTools, deactivate/delete the link, re-export. |
| Everything exports as `IfcBuildingElementProxy` | No mapping rules matched — run Smart setup, review Mapping, Preview. The report says how many matched. |
| All elements land on one storey | The Level role points at the wrong property (pre-flight shows "1 distinct storey"). Fix it on the Data tab. |
| Model far from origin / Revit import fails | Base point = Geometry origin (default) keeps coordinates small; real-world position belongs in the survey point. See section 9. |
| A mapped class exports as proxy under IFC2x3 | That class has no 2x3 entity. Export IFC4, or accept the proxy — preview counts them. |
| Sets missing from Mapping dropdowns | The list reloads when the Mapping tab opens; sets inside folders are included. Search sets need the search saved as a *set* in Navisworks. |

---

*Found a bug or missing something? [Open an issue](https://github.com/mrshoma99-rgb/bimcamel-ifc-exporter/issues) —
the exporter is developed in the open, and pay-what-you-like
[support](https://bimcamel.com/Export-Navisworks-to-Ifc) keeps it free.*
