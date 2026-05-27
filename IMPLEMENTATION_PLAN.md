# VeloIFC — Implementation Plan (v2 — ✅ APPROVED by BIM PM review, round 2)

> **Status:** APPROVED to proceed to Phase 0. Two minor, non-blocking refinements to fold in
> during implementation (not gating):
> 1. **F5 entity naming:** in IFC4, `IfcValve` is a type-occurrence pairing
>    (`IfcValveType` + the `IfcFlowController`/distribution-element occurrence); pin the exact
>    occurrence-vs-type pairing when building the Phase-3 `TypeResolver`. IFC2x3 generic
>    `IfcFlowSegment`/`IfcFlowFitting` family is correct.
> 2. **F3 forward references:** the genuinely hard part of the streaming STEP writer is
>    referencing an entity before its `#id` is emitted (e.g. an `IfcMappedItem` referencing an
>    `IfcRepresentationMap`). Validate the id-reservation strategy in a tiny early-Phase-1 spike.


> **Working name:** *VeloIFC* (changeable). A standalone Autodesk Navisworks Manage plug-in
> (separate product, **not** part of KVI_Tools) that exports Navisworks models to IFC —
> with **speed and small, correct files** as the primary selling point against the slow
> incumbents (Codemill, iConstruct, Navistools, Geometry Gym).
>
> **v2 changelog (addresses BIM PM review):** added explicit friction-free default path (§4);
> promoted coordinates/georeferencing to its own section with dual-schema behavior (§7);
> quality presets instead of bare slider (§8/F13); removed benchmark from the shippable feature
> table and made the summary report a first-class feature (§9/F11); fixed the IFC4-vs-2x3 table
> (§6); locked the IFC2x3 mesh entity decision (§6/§10); stopped overselling geometry "reuse"
> and added Phase-0 spikes (§3/§11); made the speed claim an honest, benchmark-gated target with
> the STA single-thread cap stated (§5); defined dedup key + verification (§5/F2); softened the
> xBim claim (§3); added per-version build/interop matrix (§3/§11).

---

## 1. Positioning & thesis

The market already has competent IFC exporters for Navisworks (Navisworks has **no native IFC
export** — third-party only). Their universal, documented weakness is **performance, file
bloat, and setup friction**:

- "Existing offerings generate huge tessellated models in a primitive way → huge files that
  aren't practical to use; reading them back is very slow."
- Per-triangle COM-interop marshaling is the confirmed bottleneck in the only geometry path
  Navisworks exposes (`GenerateSimplePrimitives`).
- Incumbents require pre-built selection/search sets mapped per object type before you can export.

**VeloIFC's wedge:** *fast, small, correct, and one-click by default.* Extract geometry once,
deduplicate aggressively, stream IFC straight to disk, place it correctly, and require **zero
configuration** to get a valid file. Power features are opt-in, never in the way.

**Non-negotiable constraints (product owner):**
1. Friction-free by default; customizable and powerful when needed. **Ease of use is property #1.**
2. **Every feature supports both IFC4 and IFC2x3** (no silent single-schema features).
3. Every shippable feature has a reachable UI element — no dead code.

---

## 2. Competitor landscape

| Tool | Geometry | Data / Psets | Mapping | Schemas | Weakness we exploit |
|------|----------|--------------|---------|---------|---------------------|
| **Codemill / Navistools** | BREP / tessellated | Yes | Per-type search-set XML class-map (high friction) | 2x3 / 4 / 4x3 | Slow; `GenerateIFC.exe` grid workflow; setup-heavy |
| **iConstruct Smart IFC** | Geometry + colours | Yes (writeable tabs) | Item mapping config | 2x3 / 4 | Expensive 35-tool suite; export is one feature |
| **Geometry Gym** | Yes | Yes | Config-file driven | 2x3 / 4 | Config-heavy; not friction-free |

**Our differentiators:** (a) zero-config valid file on first click; (b) instancing → small files;
(c) benchmark-proven speed; (d) correct placement with a visible base-point preview.

---

## 3. Architecture

The sibling `NavisworksExporter` (CSV/Excel) proves **some** patterns we reuse — and, to be
precise about what is and isn't de-risked:

**Genuinely reusable (verified in `ElementCollector.cs` / `NavisworksExporterPlugin.cs`):**
- SDK-style `.csproj`: `Microsoft.NET.Sdk.WindowsDesktop`, `net48`, `x64`, `UseWindowsForms`,
  `Nullable`, `LangVersion 10`; Navisworks refs via `$(NavisworksDir)`, `<Private>False</Private>`.
- `CommandHandlerPlugin` + `[RibbonLayout/Tab/Command]` → toggling a `DockPanePlugin`.
- **Assembly resolver** (`AppDomain.AssemblyResolve`) to load side-by-side DLLs — needed for our
  writer + optional validator dependencies.
- **Property harvest loop** (`PropertyCategories`→`Properties`→`VariantData`,
  `SafeDisplayString`), **leaf walking** (`HasGeometry`), **section-box via COM clip-planes**
  (`ComApiBridge.State`), `InstanceGuid`, source `Model.FileName`. → directly transfers to F4/F6.

**NET-NEW and UNPROVEN — do not claim as reused:** the entire **geometry extraction pipeline**.
`ElementCollector` contains **no** `ComApiBridge` geometry calls, **no** `InwOaFragment3`, **no**
`GenerateSimplePrimitives`, **no** `InwSimplePrimitivesCB`. F1/F2 and perf items P1/P4/P5 are
green-field and must be de-risked by **Phase-0 spikes** (§11) before any speed/size claim is made.

**Deployment:** follow the exporter's per-user `$(NavisworksDir)\Plugins\VeloIFC` post-build
copy, **and** ship a `.bundle` manifest (like KVI_Tools) so one build deploys to **2024 / 2025 /
2026**. The `.csproj` must **parameterize `NavisworksDir` per version** (not hardcode 2024 as the
exporter does) and the CI/build matrix builds + smoke-tests against each installed release,
because the COM interop assembly differs per release (§11).

### Layered design

```
VeloIFC/  (folders: UI/ Collect/ Geometry/ Data/ Ifc/ Profiles/)
  Ifc/IIfcWriter.cs        ← schema-agnostic interface (the dual-schema seam)
  Ifc/StreamingStepWriter  ← custom low-alloc STEP emitter (hot path)
  Ifc/Ifc4Profile          ← IfcTriangulatedFaceSet path + IfcMapConversion georef
  Ifc/Ifc2x3Profile        ← IfcFaceBasedSurfaceModel path + baked-offset placement
  Geometry/PrimitiveSink   ← InwSimplePrimitivesCB (hot callback)
  ...
```

### Key architectural decision: custom streaming STEP writer, not xBim in the hot path

xBim `IfcStore` builds a full in-memory object graph; **community reports describe poor
large-file write performance and high memory use** (exact figures vary — not relied upon here).
Because speed/size is the entire pitch, the export hot path uses a **purpose-built streaming
STEP writer** emitting `#id=ENTITY(...);` directly to a buffered stream with minimal
allocations. xBim or IfcOpenShell is used **only offline, behind the optional "Validate output"
checkbox (F12)** — never in the performance path.

---

## 4. The friction-free default path (ease-of-use spec — property #1)

**First click must produce a valid, correctly-placed IFC with zero configuration.** "Export"
opens a file-save dialog and runs immediately using these **documented defaults**:

| Setting | Default | Rationale |
|---------|---------|-----------|
| Schema | **IFC4** | Modern, compact tessellation; 2x3 one dropdown away |
| Scope | **Whole model, visible items** | Most common intent |
| Geometry quality | **Balanced** preset (see F13) | Avoids bloat and missing detail |
| Coordinates | **Geometry origin + IFC4 georeferencing ON** | Geometry sits near the origin (precision/viewer-friendly) while the real-world location is preserved in `IfcMapConversion`. Reconciled from the earlier "model origin" default, which reintroduced the far-from-origin "empty file" problem. |
| Type mapping | **All → `IfcBuildingElementProxy`** | Valid file with zero setup (beats Codemill friction) |
| Properties | **All categories → Psets, ON** | Coordination data preserved by default |
| Materials | **ON** | Colours preserved |
| Instancing | **ON** | Smaller files automatically |
| Validation | **OFF** | Opt-in (adds time) |

Everything above is overridable in the dock pane, but **no field is mandatory**. Advanced tabs
(Mapping, Coordinates, Properties) are collapsed by default. This is the literal definition of
"friction-free by default, powerful when needed."

---

## 5. Performance strategy (honest, benchmark-gated)

Confirmed bottleneck: `GenerateSimplePrimitives()` fires a COM callback **per triangle**; the
managed↔native transition dominates on large models. **Critical honesty point:** the Navisworks
**read API is single-threaded / UI-thread-only** (the sibling `ElementCollector` enforces exactly
this). Therefore **the per-triangle marshaling itself cannot be parallelized** — only the work
*after* the bytes are read can be. The only technique that attacks the marshaling cost directly is
P5 (native shim), which is deferred. So the realistic v1 speedup is **bounded** and comes mainly
from doing *less* work (instancing) and *cheaper* output (streaming), not from threading the read.

| # | Technique | Effect | Attacks marshaling? | Confidence |
|---|-----------|--------|:---:|:---:|
| P1 | **Instancing → `IfcRepresentationMap` + `IfcMappedItem`** | Extract a repeated mesh once; reference N times. Cuts extraction *and* file size — directly answers the "huge files" pain. | Yes (less reading) | High |
| P2 | **Custom streaming STEP writer** | No in-memory graph; straight to disk. | n/a (output) | High |
| P3 | **Producer/consumer pipeline** | Read on UI thread → enqueue raw buffers → worker threads do welding, index build, dedup hash, Pset formatting, STEP serialization. | **No** (post-read only) | Medium-High |
| P4 | **`Marshal.Copy`/`unsafe` buffer copy; avoid `Array.GetValue`** | `Array.GetValue` is confirmed "incredibly slow" here. | Reduces per-tri cost | High |
| P5 | **Native C++/CLI `InwSimplePrimitivesCB` shim** | Removes per-triangle managed transition — the real fix. | **Yes** | Medium / deferred |
| P6 | **Deflection/quality control** | Fewer triangles. | Yes (less data) | Medium |
| P7 | **Vertex welding** | Smaller integer index lists. | n/a (size) | High |

**Headline claim is a TARGET, not a promise:** *"materially faster and smaller than Codemill on a
representative piping model."* The specific "≥3× faster / ≥40% smaller" numbers are **gated on the
Phase-1 benchmark** (internal tooling, §9) and will not appear in marketing until measured.

**Dedup safety (F2):** the dedup key = the Navisworks geometry-source identity where available,
**else** a hash of the welded, quantized vertex/index buffer (positions quantized to a tolerance,
e.g. 0.1 mm). Before reusing a definition, a cheap **bounding-box + triangle-count + hash match**
guards against false positives; on mismatch we emit a fresh definition. A "strict (no dedup)"
toggle is available for paranoid exports. This closes the former open risk.

---

## 6. IFC4 vs IFC2x3 — dual-schema correctness (decisions locked)

A `IIfcWriter` interface with two profiles. The schemas diverge in exactly two places: **geometry
emission** and the **typed-entity vocabulary + georeferencing**.

| Concern | IFC4 | IFC2x3 | Status |
|---------|------|--------|--------|
| Mesh geometry | `IfcTriangulatedFaceSet` (compact integer indices) | **`IfcFaceBasedSurfaceModel`** (locked default — see below) | **Decided** |
| Instancing | `IfcMappedItem` + `IfcRepresentationMap` | Same (exists in 2x3) | Same ✓ |
| Proxy element | `IfcBuildingElementProxy` | Same | Same ✓ |
| Typed MEP elements | Rich (see F5 list) | Sparser (see F5 list); unknown→proxy | Differs (documented) |
| Property/quantity sets | `IfcRelDefinesByProperties`/`IfcPropertySet` | Same | Same ✓ |
| Spatial structure | Project→Site→Building→Storey | Same | Same ✓ |
| GUID | 22-char base64 | Same | Same ✓ |
| Units | `IfcUnitAssignment` | Same | Same ✓ |
| **Georeferencing** | `IfcMapConversion` + `IfcProjectedCRS` | **No `IfcMapConversion`** → bake offset/rotation into `IfcSite`/object `ObjectPlacement` | **Differs — handled in §7** |

**IFC2x3 mesh = `IfcFaceBasedSurfaceModel`, not `IfcFacetedBrep`:** `IfcFacetedBrep` requires
*planar, closed, manifold* faces with shared polyloops; tessellated Navisworks meshes are
frequently open/non-manifold, which produces invalid 2x3 or disappearing geometry on append.
`IfcFaceBasedSurfaceModel` accepts arbitrary triangle "soup" and round-trips reliably into
Navisworks/Solibri. (buildingSMART `IfcFacetedBrep` planar-face restriction.)

The abstraction surface is therefore small, and **every UI feature flows through `IIfcWriter`**,
so the schema dropdown is the only switch and all features work in both.

---

## 7. Coordinates & georeferencing (the #1 real-world pain — dedicated section)

Wrong location / shifted-rotated models / shared-coordinate confusion is the most-reported IFC
pain. VeloIFC handles it explicitly and **shows the result before export**:

- **Base-point modes:** (a) **Model origin / no offset** (default), (b) **Navisworks shared/
  project coordinates**, (c) **custom base point** (user enters E/N/Elev + rotation/true-north).
- **Live preview/readback:** the Coordinates panel displays the resulting world origin and
  rotation that will be written, so the user verifies placement *before* running (kills the
  "exported and it's a kilometre away" loop).
- **Units:** auto-detected from the model, shown, overridable; written to `IfcUnitAssignment`.
- **IFC4 georeferencing:** when shared/custom coords are chosen, write `IfcMapConversion` +
  `IfcProjectedCRS` (Eastings/Northings/OrthogonalHeight/XAxisAbscissa/Ordinate).
- **IFC2x3 fallback (explicit, not silent):** 2x3 has **no** `IfcMapConversion`; instead the
  offset + rotation are **baked into the placement** (`IfcSite`/object `ObjectPlacement`), and the
  summary report states that georeferencing metadata was baked rather than carried as CRS. This
  satisfies the dual-schema rule honestly.

UI: dedicated **Coordinates** tab (collapsed by default; default mode needs no interaction).

---

## 8. Data parsing & quality presets

- **Psets:** each property category → one `IfcPropertySet` via `IfcRelDefinesByProperties`;
  values typed from `VariantData` (`IsDisplayString`→`IfcText/IfcLabel`, `IsNamedConstant`→
  `IfcLabel`, numeric→`IfcReal`, bool→`IfcBoolean`); `SafeDisplayString` fallback.
- **Identity:** `InstanceGuid`→deterministic 22-char IFC `GlobalId`; `DisplayName`→`Name`; tree
  path retained as a Pset.
- **Materials:** fragment colour/transparency → `IfcStyledItem`/`IfcSurfaceStyle`.
- **Quality presets (F13) — named, not a bare slider:** **Small file / Balanced (default) /
  High detail**, each mapping to a documented deflection value, with an "Advanced" numeric field
  for experts. Default = Balanced.

---

## 9. Feature table — functionality × difficulty × plan × UI × schemas

Difficulty: **E** / **M** / **H** / **V**. *(Benchmark harness is internal dev tooling, NOT a
shippable feature — removed from this table per the "every feature has UI" rule; it gates the
speed claim, see §5.)*

| # | Feature | What it does | Diff. | Key APIs / risks | Implementation plan | UI element | IFC4 / 2x3 |
|---|---------|--------------|:----:|------------------|---------------------|------------|:----------:|
| F1 | **Geometry export (tessellated)** | Triangles → IFC mesh | M | `ComApiBridge`, `InwOaFragment3.GenerateSimplePrimitives`, `InwSimplePrimitivesCB`, `GetLocalToWorldMatrix`; LCS→WCS risk | Phase-0 spike first; `PrimitiveSink` fills buffers; apply world matrix; emit per profile | Runs on Export; outcome in F11 report | `IfcTriangulatedFaceSet` / `IfcFaceBasedSurfaceModel` |
| F2 | **Geometry instancing (dedup)** | Reuse repeated meshes | H | Dedup key + false-positive risk (see §5) | Source-id or quantized-hash key + bbox/tri-count guard; first→`IfcRepresentationMap`, rest→`IfcMappedItem` | "Optimize repeated geometry" toggle + dedup ratio in report; "strict (no dedup)" | `IfcMappedItem` (both) |
| F3 | **Streaming STEP writer** | Fast low-mem output | H | Forward-ref strategy | Buffered writer; reserve `#id` ranges (two-pass id reservation) | (engine; result surfaced in F11) | Both |
| F4 | **Property sets** | Categories → Psets | M | `PropertyCategories`/`VariantData` (reused) | `PropertyMapper` + type inference | "Properties" tab: pick categories | Both |
| F5 | **Object→IFC type mapping** | Typed elements via rules | H | 2x3 sparser vocab | `TypeResolver` rules; default proxy. **IFC4 emits:** `IfcPipeSegment/IfcPipeFitting/IfcValve/IfcDuctSegment/IfcFlowController/...`; **IFC2x3 emits:** `IfcFlowSegment/IfcFlowFitting/IfcFlowController/...`; unknown→proxy in both | "Mapping" tab: rules grid + presets (collapsed) | Both (documented fallback) |
| F6 | **Scope selection** | Whole / selection / search set / section box | E | Reuse `ElementCollector` | Wire collector; add selection & search-set sources | Scope dropdown + "limit to section box" | Both |
| F7 | **Coordinates & georeferencing** | Correct placement + preview | M | §7; 2x3 has no `IfcMapConversion` | Base-point modes; IFC4 `IfcMapConversion`/`IfcProjectedCRS`; 2x3 baked offset | "Coordinates" tab + live origin/rotation preview | Both (§7) |
| F8 | **Material / colour** | Colours to IFC | M | Fragment colour | `IfcStyledItem`/`IfcSurfaceStyle` | "Include materials" check | Both |
| F9 | **Spatial structure** | Project/Site/Building/Storey | M | Derive from tree or input | `SpatialBuilder`; auto + override | "Structure" panel (auto default) | Both |
| F10 | **Export profiles** | Save/load settings | E | JSON | `ExportProfile` JSON | Profile save/load/dropdown | Both |
| F11 | **Summary report (required)** | Verifiable outcome | M | UI-thread marshaling | After export, show: element count, schema, units, **base point/rotation used**, **dedup ratio**, **output file size**, warnings, validation result | Progress dialog → report panel (with "copy/save") | Both |
| F12 | **Output validation (optional)** | Sanity-check IFC | M | xBim/IfcOpenShell offline | Post-export validation pass | "Validate output" check | Both |
| F13 | **Quality presets** | Speed vs fidelity | M | Deflection granularity | Small/Balanced/High + advanced numeric | Preset buttons + advanced field | Both |

---

## 10. Phasing / roadmap

- **Phase 0 — De-risking spikes (gate everything):**
  1. Extract **one mesh** via `GenerateSimplePrimitives`+`InwSimplePrimitivesCB` on the installed
     2024 interop DLL; confirm interface signatures and LCS→WCS via `GetLocalToWorldMatrix`.
  2. Confirm a usable geometry **source identity** for dedup (else fall back to hashing — §5).
  3. `InstanceGuid`→22-char IFC GUID: **acceptance test for re-export stability** (same input ⇒
     same `GlobalId`).
  4. Write a trivial valid IFC (spatial skeleton + 1 proxy + 1 mesh) and **open it in Solibri +
     re-append into Navisworks** in **both** IFC4 and IFC2x3 — proves the 2x3 surface-model choice.
- **Phase 1 — MVP + the wedge:** F1, F3, F6, F7, F11, plus **F2 instancing** (with verification)
  and the **internal benchmark harness** (gates the speed claim). Default = friction-free path (§4).
- **Phase 2 — Data parity:** F4, F8, F9, F10, F13.
- **Phase 3 — Mapping & polish:** F5 rules + presets, F12 validation.
- **Phase 4 — Speed ceiling (optional):** P5 native C++/CLI callback if benchmarks demand it.

Every phase builds + smoke-tests against **2024, 2025, 2026** (per-version interop).

## 12. Open-source posture & dependency licensing

**The product ships open-source.** Architecture already supports this cleanly, but dependency
choices must be disciplined:

- **Core = zero external IFC-library dependency.** Geometry is read through the Navisworks COM
  API (present on every install). IFC is written by **our own streaming STEP writer** (§3). This
  means the performance-critical, always-on path has no third-party license entanglement at all —
  a direct benefit of the "don't use xBim in the hot path" decision.
- **Optional validator is isolated and swappable.** The only place a third-party IFC library
  would appear is the opt-in "Validate output" checkbox (F12). Candidates and their licenses:
  - **xBim Toolkit** — CDDL-1.0 (OSI-approved, file-level weak copyleft; GPL-incompatible but
    fine to distribute alongside permissive code).
  - **IfcOpenShell** — LGPL-3.0 (keep as a separate, dynamically-loaded DLL the user can replace;
    do not statically link).
  Whichever is chosen, it lives behind an interface in a **separate optional assembly/plugin** so
  the core's license stays unambiguous and users who don't validate ship nothing extra.
- **Avoid non-OSS-friendly libraries.** Notably **EPPlus** (Polyform Noncommercial since v5) and
  any "free for non-commercial" packages — banned. Prefer **MIT/Apache-2.0/BSD**; tolerate
  MPL/CDDL/LGPL only for isolated optional components.
- **Project license recommendation:** **MIT** (maximum adoption, simplest) or **Apache-2.0** (adds
  explicit patent grant — relevant if the instancing/streaming approach is novel). *Owner to pick.*
- **No bundled Autodesk DLLs.** Navisworks API references stay `<Private>False</Private>` (already
  the case) — we never redistribute Autodesk assemblies.

The Phase-0 scaffold already honors this: its only non-framework reference is `System.Numerics`.

## 11. Acceptance gates (so claims are provable)

- No speed/size marketing until the Phase-1 benchmark produces measured numbers vs Codemill on a
  shared sample model.
- No release until a sample export **opens in Solibri and re-appends into Navisworks** in both
  schemas with correct placement (verified against the F11 base-point readback).
- GUID re-export stability test passes.
- Per-version (2024/25/26) smoke test passes.

*End of draft v2 — resubmitted for strict BIM PM re-review.*
