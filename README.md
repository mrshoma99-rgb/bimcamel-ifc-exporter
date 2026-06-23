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
  PredefinedType); unmapped elements stay `IfcBuildingElementProxy`.
- **Type objects, materials, classification, base quantities** (volume / area / length computed
  from the mesh), **multi-storey** spatial structure from a Level property.
- **Coordinates & georeferencing** — base-point modes with live preview; IFC4 `IfcMapConversion`,
  IFC2x3 baked placement. All split parts / batch files share one origin so they overlay.
- **Reporting** — element/triangle counts, file size, a per-entity-type size profile, a phase-timing
  breakdown, and peak memory; export profiles save/load all settings as JSON.
- Pure managed, **no third-party runtime dependency**; ships for Navisworks **2024 / 2025 / 2026**,
  Manage and Simulate.

## Install

Download **BIMCamel_Setup.exe** from the **[Releases](../../releases/latest)** page and run it.
One installer does everything:

- Installs **just for you** — no admin rights, no UAC prompt. The plug-in goes into your own
  `%AppData%\Autodesk\ApplicationPlugins` folder (the location Navisworks reliably auto-loads for
  your account), so it works without needing a machine administrator.
- A **Navisworks check** page shows which versions (2024/2025/2026, Manage / Simulate) it found and
  pre-selects them, so the plug-in is registered for exactly what you have installed.
- If a previous BIMCamel install is detected (including a leftover machine-wide "all users" install
  from older builds, or a manual folder copy), it's **upgraded / removed automatically** — elevating
  once only if a system-wide copy has to be deleted.
- If the Autodesk `ApplicationPlugins` folder isn't where it should be, the directory page lets
  you **browse to a custom location**.
- After installing, Setup **verifies** the files landed and match a detected Navisworks; if not, it
  says so and drops a shareable log on your Desktop. Uninstall via Apps & Features removes the whole
  bundle (no stale files left for Navisworks to half-load).

Restart Navisworks — a **BIMCamel** ribbon tab appears with **IFC exporter** and **About** buttons.
(The installer is unsigned, so Windows SmartScreen may warn on first run.)

**Manual install (no tooling):** copy a built `BIMCamel.bundle` folder into
`%AppData%\Autodesk\ApplicationPlugins\` — Navisworks loads it on next launch. A ready-to-copy
skeleton (with a `2024/` `2025/` `2026/` folder per version) lives in [`dist/`](dist/); drop the
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
(the community-maintained `Chuongmep.Navis.Api.Autodesk.Navisworks.*` packages), so **no Navisworks
installation is needed to build**.

```bat
dotnet build BIMCamel\BIMCamel.csproj -c Debug -p:NavisworksYear=2025
```

A Debug build auto-deploys to the **matching year folder** (`$(NavisworksYear)`, default 2024) of
your per-user `BIMCamel.bundle` for quick iteration. Note a DLL only loads in the Navisworks version
it was built against — building against 2024 and running 2025 gives `PLUGIN_LOAD_07: invalid
referenced Navisworks Api version` — so set `NavisworksYear` to the version you run.

To produce the installer (needs free [Inno Setup 6 or 7](https://jrsoftware.org/isdl.php)):

```powershell
installer\build_installers.ps1
```

The script builds the plugin in Release **once per Navisworks version (2024 / 2025 / 2026)**, each
against its own API restored from NuGet, generates the wizard images / icon from the camel logo, and
compiles `installer\output\BIMCamel_Setup.exe`.

The Navisworks API is referenced **for compile only** (`ExcludeAssets=runtime`), so no Autodesk DLLs
are redistributed — the user's own licensed Navisworks supplies them at run time. To build against a
local Navisworks install instead (Autodesk's genuine assemblies), pass
`-p:NavisworksDir="…\Navisworks Manage 2025"`.

## Project layout

```
BIMCamel/            plug-in source (UI / Collect / Geometry / Data / Ifc / Profiles)
installer/           Inno Setup script + build helper for BIMCamel_Setup.exe
*.md                 design + implementation notes
LICENSE              MIT
```

## License

[MIT](LICENSE). Not affiliated with Autodesk. "Navisworks" is a trademark of Autodesk, Inc.
