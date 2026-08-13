# BIMCamel — fast Navisworks → IFC exporter

A free, open-source **Autodesk Navisworks** plug-in that exports models to **IFC** (IFC4 and IFC2x3),
built for **speed, small files, and zero-config first-click export**. Navisworks has no native IFC
export — BIMCamel fills that gap without the slowness and setup friction of the commercial tools.

> More free BIM tools, updates and docs: **[bimcamel.com](https://bimcamel.com)**

---

## Highlights

- **Streaming, memory-bounded engine.** Exports huge models without the out-of-memory crashes that
  plague naïve exporters — peak memory stays bounded (a few hundred MB) regardless of model size.
- **Geometry instancing.** Repeated parts (bolts, fittings…) are written once as
  `IfcRepresentationMap` + `IfcMappedItem`, so files stay small.
- **Dual-schema, every feature in both.** IFC4 (`IfcTriangulatedFaceSet`) and IFC2x3
  (`IfcFaceBasedSurfaceModel`), including the generic IFC2x3 MEP vocabulary
  (`IfcFlowSegment`/`IfcFlowController`/…).
- **Friction-free by default.** First click produces a valid, correctly-placed IFC with no setup.

## Features

- **Geometry** — tessellated meshes via the COM geometry path; vertex welding; quality presets
  (Small file / Balanced / High detail) with coordinate precision tied to the weld tolerance.
- **Scope** — whole model, current selection, active section box, a saved/search set, or
  **batch: multiple sets → one IFC each**.
- **Size splitting** — cap output size (e.g. 200 MB); larger exports roll into `name_001.ifc`,
  `name_002.ifc`, … each a complete, standalone IFC. Composes with batch.
- **Property sets** — category-qualified, typed values, with content **dedup** (identical psets
  shared) and optional parameter renaming/relocation to standard Psets.
- **Object → IFC class mapping** — assign Navisworks sets to IFC classes (with optional
  PredefinedType); **auto-map** proposes classes from set names; unmapped elements stay
  `IfcBuildingElementProxy` — and the report tells you **how many**, which is the number that
  matters. A Navisworks search set is already an IF/THEN rule, so the same grid also assigns
  **classification codes**: build "Category = Walls AND Name contains External" in Find Items,
  save it as a set, map it here.
- **Pre-export preview** — resolve the mapping against your scope *before* writing anything:
  mapped vs proxy counts, the per-class breakdown, property coverage and a geometry estimate.
- **Type objects, materials, classification, base quantities** (volume / area / length / width /
  height, into the standard per-class `Qto_*BaseQuantities` sets), **multi-storey** spatial
  structure from a Level property, with **real storey elevations** read from the model's grids.
- **Groups** — export each mapped set as `IfcGroup`, `IfcSystem` or `IfcZone` with
  `IfcRelAssignsToGroup` membership.
- **Coordinates & georeferencing** — base point (where the local origin sits) and survey point
  (where the model sits on earth) are separate: the base point only decides how large the stored
  ordinates are, while IFC4 `IfcMapConversion` + `IfcProjectedCRS` carry the CRS and survey
  offset. Live base-point preview, plus a **federated origin report** listing every loaded
  model's origin, rotation and units and flagging any disagreement. All split parts / batch files
  share one origin so they overlay.
- **Revision-aware** — GlobalIds are deterministic for elements *and* for the spatial tree,
  property sets, type objects and relationships, so a re-export diffs cleanly. Each export drops
  a small manifest beside the IFC and reports **NEW / DELETED / MODIFIED / UNCHANGED** against
  the previous one.
- **Validation** — on by default, checking the STEP envelope, dangling references, duplicate or
  malformed GlobalIds, empty mandatory aggregates and malformed enumeration tokens.
- **Reporting** — element/triangle counts, file size, a per-entity-type size profile, a phase-timing
  breakdown, and peak memory; export profiles save/load all settings as JSON.
- Pure managed, **no third-party runtime dependency**; ships for Navisworks **2024 / 2025 / 2026 / 2027**,
  Manage and Simulate.

## Install

Download **BIMCamelSetup.exe** from the **[Releases](../../releases/latest)** page and run it —
the same graphical installer as the sibling [Dyncamelo](https://github.com/mrshoma99-rgb/dyncamelo)
plug-in, so every BIMCamel tool installs the same way:

- Installs **just for you** — no admin rights, no UAC prompt. The bundle goes into your own
  `%AppData%\Autodesk\ApplicationPlugins` folder (the location Navisworks reliably auto-loads for
  your account), with all four year folders (2024–2027); Navisworks picks the matching one.
- **Detects** which supported Navisworks releases (Manage / Simulate) are present and tells you
  which build will load; warns when Navisworks is running so you know to restart it.
- A previous install — including one made by the old Inno Setup `BIMCamel_Setup.exe` — is
  **upgraded / removed automatically**, and a per-user **Apps & Features** entry is registered
  for uninstall. Silent modes for scripted deployment: `BIMCamelSetup.exe /silent` and
  `BIMCamelSetup.exe /uninstall /silent`.
- Files it writes carry no Mark-of-the-Web, so the `PLUGIN_LOAD_02` / `0x80131515` blocked-DLL
  failure cannot happen with this install path.

Restart Navisworks — a **BIMCamel** ribbon tab appears with **IFC exporter** and **About** buttons.
If **Dyncamelo** is installed too, its buttons appear on the **same BIMCamel tab** — the tools
share one ribbon tab rather than each adding their own. (The installer is unsigned, so Windows
SmartScreen may warn on first run.)

When a newer release is published, the exporter panel offers it on open (checked at most once a
day, silently skipped when offline).

**Manual install (no tooling):** copy a built `BIMCamel.bundle` folder into
`%AppData%\Autodesk\ApplicationPlugins\` — Navisworks loads it on next launch. A ready-to-copy
skeleton (with a `2024/` `2025/` `2026/` `2027/` folder per version) lives in [`dist/`](dist/); drop the
matching per-version DLL into each year folder first — see [`dist/README.md`](dist/README.md).

## Quick start

1. **BIMCamel** ribbon tab → **IFC exporter** to open the panel.
2. Pick a schema and scope (default: IFC4, whole model). Everything else has sensible defaults.
3. Click **Export IFC**, choose a path, done.

> **Slow export?** An active **DataTools / external-database link** runs a query per object and can
> add many minutes (and floods the console with `DATATOOLS_SQL_EXEC` errors if broken). Deactivate
> it under **Home → DataTools** before exporting. The panel shows an up-front reminder.

## Build from source

Requires the **.NET SDK**. The per-year Navisworks API reference assemblies are restored from NuGet
(`Speckle.Navisworks.API`, one matched-set package per release — each bundles the genuine
`Autodesk.Navisworks.Api` + `ComApi` + `Interop.ComApi` at that release's assembly version), so
**no Navisworks installation is needed to build**.

```bat
dotnet build BIMCamel\BIMCamel.csproj -c Debug -p:NavisworksYear=2025
```

A Debug build auto-deploys to the **matching year folder** (`$(NavisworksYear)`, default 2024) of
your per-user `BIMCamel.bundle` for quick iteration. Note a DLL only loads in the Navisworks version
it was built against — building against 2024 and running 2025 gives `PLUGIN_LOAD_07: invalid
referenced Navisworks Api version` — so set `NavisworksYear` to the version you run.

Releases build themselves: commit the new tag (e.g. `v0.6.0`) to
[`dist/RELEASE_VERSION`](dist/RELEASE_VERSION) and push — the **release workflow**
(`.github/workflows/release.yml`, mirrored from Dyncamelo) builds one DLL per Navisworks year on CI,
stages the bundle, compiles `BIMCamelSetup.exe` with the bundle embedded, and publishes the GitHub
release with stable-named assets. No local Windows build machine or Inno Setup needed. (It can also
be run manually from Actions → release with a version input; a version that already has a release
is skipped.)

The Navisworks API is referenced **for compile only** (`ExcludeAssets=runtime`), so no Autodesk DLLs
are redistributed — the user's own licensed Navisworks supplies them at run time. To build against a
local Navisworks install instead (Autodesk's genuine assemblies), pass
`-p:NavisworksDir="…\Navisworks Manage 2025"`.

## Project layout

```
BIMCamel/            plug-in source (UI / Collect / Geometry / Data / Ifc / Profiles)
installer/           BIMCamel.Installer — WPF setup app (BIMCamelSetup.exe), shared UI with Dyncamelo
.github/workflows/   build CI + release workflow (per-year build → bundle → installer → GitHub release)
*.md                 design + implementation notes
LICENSE              MIT
```

## Part of the BIMCamel toolset

- **[Dyncamelo](https://github.com/mrshoma99-rgb/dyncamelo)** — Dynamo-style **visual programming
  for Navisworks** (280+ nodes: search, selection sets, color-coding, QTO, clash triage, BCF,
  viewpoints…). Website: [bimcamel.com/plugins/dyncamelo](https://www.bimcamel.com/plugins/dyncamelo).
  Both plug-ins share the **BIMCamel** ribbon tab when installed together.
- **[bimcamel.com](https://bimcamel.com)** — browser-based IFC tools (validate, compare, upgrade /
  downgrade schema…) and this exporter's page:
  [bimcamel.com/Export-Navisworks-to-Ifc](https://www.bimcamel.com/Export-Navisworks-to-Ifc).

## License

[MIT](LICENSE). Not affiliated with Autodesk. "Navisworks" is a trademark of Autodesk, Inc.
