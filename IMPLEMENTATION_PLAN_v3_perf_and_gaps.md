# BIMCamel — Implementation Plan v3: Performance Re-architecture + Closing the Gaps

> **Implementation status (2026-05-26).** Part A is **done and building** (Navisworks 2024):
> A1 streaming token writer + zero-alloc `WriteReal` (validated to ≤7e-12 m over 8.7M values),
> A2 streamed meshes (no whole-mesh strings; 2x3 face-list from a pooled buffer), A3 bounding-box
> offset (no vertex pre-pass), A4b 128-bit hash dedup key, **A4 full streaming pipeline** (lazy
> `ExtractStream` → interleaved write; single-pass instancing; lazy storeys). The IFC writer was
> driven with synthetic meshes and the output validated (no dangling refs / dup ids / bad envelope)
> for plain+instanced × IFC4+IFC2x3. **Not yet done:** PERF.2 native shim, PERF.3 byte writer, A5/A6
> (faster reads / single COM convert), A7 quality, and Parts B–H. Next gate: live export test on the
> real failing model.



> **Why this document exists.** v2 (`IMPLEMENTATION_PLAN.md`) is the product vision. The code now
> implements most of Phases 1–3, but two things are wrong in practice:
>
> 1. **The export pipeline crashes Navisworks on any model whose IFC would exceed ~40 MB.** This is
>    not a tuning problem — it is an architectural one. The pipeline materialises the *entire* model
>    in managed memory and builds multi-megabyte transient strings, on the UI thread. It must be
>    re-architected to stream end-to-end.
> 2. Several v2 promises are unimplemented or IFC4-only (P3/P4/P6 performance work, Phase-0
>    verification, IFC2x3 semantic parity, real validation, materials-with-instancing).
>
> This plan takes each item **one at a time**: *what is missing → where it lives in the code → how
> to add it (rooted in the IFC schema and the Navisworks API) → how it touches the rest of the
> system → its performance impact.* **Part A (the crash) is the gate; nothing else ships until it is
> fixed**, because every other feature runs through the same pipeline.

---

## Part 0 — Root-cause analysis of the crash (read first)

The crash is **out-of-memory / GC death**, plus a **frozen UI**, and it has *five* compounding
causes. The default export path (`Instancing = ON`) hits the worst of them.

### 0.1 The whole model is materialised in RAM before a single byte is written

- `MeshExtractor.Extract` (`Geometry/MeshExtractor.cs:45`) returns a `List<ElementMesh>` holding
  **every element's vertices and indices simultaneously**. `ElementMesh.Vertices` is a
  `List<double>` — 8 bytes per ordinate, 24 bytes per vertex, plus `List<T>` slack (up to 2×
  over-allocation). A model that serialises to a 40 MB IFC easily holds **300–600 MB** of managed
  vertex/index data this way.
- `InstancedExtractor.Extract` (`Geometry/InstancedExtractor.cs:56`) is worse: it builds an
  `InstancedModel` holding all unique `Geometries`, **all** `MeshInstance`s (12 doubles each), and
  **all** per-element `Properties` lists, all at once.

### 0.2 The deduplication key is a giant string, and they are all kept at once  ⚠️ prime suspect

`InstancedExtractor.Key` (`Geometry/InstancedExtractor.cs:140`) builds, **per fragment**, a
`StringBuilder` containing *every quantised vertex ordinate and every index as text*:

```csharp
sb.Append((long)Math.Round(v * 10000.0)); sb.Append(',');   // for every ordinate
... sb.Append(i); sb.Append(',');                            // for every index
```

These strings are roughly **as large as the geometry itself**, and **all of them live
simultaneously** as keys in `index` (`Dictionary<string,int>`). On the default instanced path this
alone can double or triple peak memory versus the output size — which is exactly why a *40 MB
output* blows past available memory. Hashing multi-KB strings is also CPU-quadratic-ish on big
models.

### 0.3 Multi-megabyte transient entity strings

The "streaming" writer is not actually streaming at the mesh level:

- `Ifc4MeshWriter.WriteMesh` (`Ifc/MeshWriters.cs:42`) concatenates **all points** into one
  `StringBuilder pts` and **all triangles** into one `StringBuilder tris`, then passes the whole
  thing as a single string to `w.Write(...)`. One element = one multi-MB string allocation.
- `StreamingStepWriter.Write(string)` (`Ifc/StreamingStepWriter.cs:28`) *requires* a fully-formed
  string for every entity. Every `$"IFC...({...})"` interpolation in `IfcExporter` allocates.
- `Ifc2x3MeshWriter.WriteMesh` (`Ifc/MeshWriters.cs:74`) emits **3+ entities per triangle**
  (`IfcPolyLoop` + `IfcFaceOuterBound` + `IfcFace`) plus an `IfcCartesianPoint` per vertex, then
  joins a `List<int> faceIds` of *every face* into one final `StringBuilder`. A 1 M-triangle mesh =
  ~3 M `w.Write` calls + ~3 M string allocations + a million-ref string. This is the 2x3 cliff.

### 0.4 The coordinate offset forces a second full pass over all vertices

`IfcExporter.Export` (`Ifc/IfcExporter.cs:49`) computes the base-point offset by iterating **every
ordinate of every element** in a lambda *before* the write loop — then iterates again to write. Two
full traversals of the entire (already oversized) in-memory model.

### 0.5 Everything runs on the UI thread, kept alive by `Application.DoEvents()`

`RunExport` (`UI/ExportDockPane.cs:458`) extracts **and** writes synchronously on the Navisworks UI
thread, pumping `DoEvents()` for progress. While memory climbs and the GC thrashes, the UI is
"responsive" only between pumps — to the user it *freezes*, then Navisworks dies when the CLR can't
satisfy an allocation.

> **Note:** `IfcValidator.Validate` (`Ifc/IfcValidator.cs:25`) does `File.ReadAllLines(path)` —
> loading the whole 40 MB+ file into a `string[]` and scanning it twice. Validation defaults OFF so
> it is not the crash, but it is a second OOM waiting to happen and must also be streamed (Part G).

### 0.6 The fix in one sentence

**Make the pipeline streaming and bounded:** derive the coordinate offset from cheap Navisworks
bounding boxes (no vertex pre-pass); extract one element at a time and hand it off through a
*bounded* queue to a *background* writer thread; emit entity tokens **straight to the buffered
stream** (no whole-mesh strings); replace the giant string dedup key with a fixed-size hash; and
write geometry definitions **interleaved** so instancing stays single-pass. Peak memory then becomes
*O(one element + small per-element metadata)* instead of *O(whole model × text amplification)*.

---

## Part PERF — Direct answers to the "performance is priority #1" directive

This section answers the three explicit points: *(1) performance comes first, everything else after;
(2) we may change anything about how geometry is extracted and written; (3) if xBim is the
bottleneck, replace it.* Read this before the detailed parts.

### PERF.0 — xBim is **not** in the pipeline; it cannot be the bottleneck

Every source file was reviewed. **The exporter does not use xBim, IfcOpenShell, or any third-party
IFC library.** IFC is written by the in-house `StreamingStepWriter`; the only validator
(`Ifc/IfcValidator.cs`) is homegrown regex over text. `BIMCamel.csproj` references only the
Navisworks API, `System.Numerics`, and `System.Runtime.Serialization`. So there is **nothing to swap
out** — the slowness is entirely in *our own* extract→write code, diagnosed in Part 0. (xBim was
only ever a *candidate* for the optional, off-hot-path validator in v2 §12 — not in the export path.)

### PERF.1 — The bottleneck hierarchy (fix in this order)

| Rank | Cost | Symptom | Fixed by | Asymptote? |
|---|---|---|---|---|
| 1 | **Whole-model materialisation + giant strings + giant dedup keys** | OOM crash on big models | A1–A4b | Removes the crash; memory → O(window) |
| 2 | **Per-triangle COM callback marshaling** (`GenerateSimplePrimitives` → managed `InwSimplePrimitivesCB`) | "Painfully slow" even when it completes | **PERF.2 native shim** (P5) — the *only* lever on this | Yes — the true read asymptote |
| 3 | **`StreamWriter` + per-number/string allocation on write** | CPU + GC time in write | A1 token writer; **PERF.3 byte writer** | Lowers constant factor |
| 4 | **Per-vertex `GetValue` boxing; per-element COM convert** | Constant-factor slow | A5, A6 | Lowers constant factor |

**The crash (rank 1) and the read asymptote (rank 2) are different problems.** Part A fixes the
crash and most constant factors; only a **native callback shim** attacks rank 2. Because you have
authorised changing the extraction path, **P5 is promoted from "Phase 4, optional" to a first-class
performance work item** (PERF.2), to be built and benchmarked right after A1–A4 make the pipeline
stable enough to measure.

### PERF.2 — Native `InwSimplePrimitivesCB` shim (promoted P5): the real extraction fix

**Problem:** `GenerateSimplePrimitives` invokes the callback **once per triangle**, crossing the
managed↔native COM boundary every time. On a multi-million-triangle model that is millions of
marshaled transitions — irreducible in pure managed code (confirmed by the community: the per-triangle
managed callback is *the* known Navisworks extraction trap). The current `PrimitiveSink` (managed,
`Geometry/PrimitiveSink.cs`) sits on the slow side of this boundary.

**How:** implement `InwSimplePrimitivesCB` in a **native/mixed-mode (C++/CLI) assembly**. The COM
callback then fires into native code; vertices/indices accumulate in a **native buffer** with the
managed boundary crossed **once per fragment** (bulk copy out) instead of once per triangle. The
managed side hands the resulting buffer straight into the A4 producer queue.

- **Expected gain:** the dominant read cost drops by roughly the per-triangle marshaling factor —
  community reports describe large-model extraction going from minutes to seconds. Treat the exact
  multiplier as **benchmark-gated** (v2 §5/§9), not promised.
- **Cost / trade-offs:** a second build artifact (mixed-mode C++/CLI DLL), per-Navisworks-runtime
  considerations, and it dents the "pure-managed, zero native dependency" simplicity (v2 §12). The
  shim is isolated behind the same `IPrimitiveSource` seam introduced in A4, so the **managed path
  stays as the fallback** and the rest of the pipeline is unaware which source produced the buffer.
- **Verification:** confirm the exact `InwSimplePrimitivesCB` vtable/interop signature against the
  installed SDK during the Part B spike before committing.

### PERF.3 — Raw byte-stream writer (further write speedup beyond A1)

After A1 removes giant strings, the next write-side lever is the `StreamWriter` itself: it does
char→byte UTF-8 conversion and the token API still passes `string`s. STEP is ASCII except inside
string literals. So optionally replace `StreamWriter` with a `FileStream` + a reusable `byte[]`
buffer, writing keywords/digits as ASCII bytes directly and a hand-rolled double→ASCII formatter
emitting bytes (no `string`, no char-encoding pass). String *literals* (names/Pset values) get UTF-8
byte-encoded on the fly. This is the fastest managed write path and produces zero per-entity garbage.
Lower priority than PERF.2 but cheap and complementary.

### PERF.4 — What stays single-threaded, and why

The Navisworks **read API is STA / UI-thread-only** (v2 §5; enforced by the sibling
`ElementCollector`). Therefore the geometry *read* cannot be parallelised across triangles — A4's
producer must run on the UI thread. What *can* move off it (and does, in A4): welding, dedup hashing,
number formatting, STEP serialization, and disk I/O on a background consumer. Parallelising the
*consumer* further (multiple serializer threads with reserved `#id` ranges) is possible but only
helps if writing — not reading — dominates; **defer it until the benchmark shows write is the
limiter** (after PERF.2, read usually still dominates).

### PERF.5 — Ordering decision

**Everything non-performance (Parts C, D, E, G, H) is deferred behind the performance work.** The
build order is: **B (verify) → A1–A3 (stop crash) → A4–A4b (bounded + instancing) → PERF.2 native
shim + A5/A6 (extraction speed) → PERF.3 + benchmark → only then C/D/E/G/H.**

---

## Part A — Performance re-architecture (the gate)

Each sub-part below is independently shippable and ordered by impact-per-effort. A1–A4 alone should
stop the crash; A5–A7 deliver the "fast" wedge; A8 hardens validation.

### A1 — Streaming entity writer (kills 0.3)

**What's missing:** a way to emit an entity *incrementally* so a mesh never becomes one giant
string.

**Where:** `Ifc/StreamingStepWriter.cs`.

**How:** add a low-level token API alongside the existing `Write(string)` (keep the latter for the
hundreds of small skeleton entities where it's fine):

```csharp
public int Begin(string typeName)        // writes "#id=TYPENAME("  → returns id
public void Tok(string s)                // raw append (already-escaped fragment)
public void Sep()                        // writes ','
public void WriteReal(double v)          // formats into a reused char[] (no per-number string)
public void RefTok(int id)               // writes "#id"
public void End()                        // writes ");\n"
```

`WriteReal` is the key allocation win. **net48 lacks `double.TryFormat`**, so hand-roll a
fixed-notation formatter (sign, integer digits, `.`, up to 10 fractional digits, trailing-zero trim
to match the current `"0.0##########"`) writing into a pooled `char[32]` and calling
`_w.Write(buf, 0, len)`. This removes *millions* of tiny string allocations on large meshes.

Also bump the `StreamWriter` buffer (`StreamingStepWriter.cs:24`) from 1 MB to 4 MB and keep the
`\n`-only line ending.

**IFC grounding:** ISO-10303-21 places no ordering constraint on `#id` references — forward and
backward refs are both legal — so token-streaming in any order is valid. Reals must contain a `.`
and avoid exponent form for maximum reader compatibility (already honoured by `R`/`WriteReal`).

**System effect:** `MeshWriters` and the per-element entity writes in `IfcExporter` switch from
`$"...({bigString})"` to `Begin/Tok/WriteReal/End`. The skeleton writers (units, owner, spatial)
can stay on `Write(string)`.

**Perf:** eliminates the multi-MB transient strings and most number-formatting garbage; the single
largest GC-pressure reduction in the whole effort.

### A2 — Stream meshes straight to the stream (uses A1; kills the 2x3 cliff)

**Where:** `Ifc/MeshWriters.cs`.

**IFC4 (`Ifc4MeshWriter`):** keep the compact `IfcTriangulatedFaceSet` over
`IfcCartesianPointList3D`, but write points and the `CoordIndex` list directly via the token API:

```csharp
int pl = w.Begin("IFCCARTESIANPOINTLIST3D"); w.Tok("(");
for (int i = 0; i < verts.Count; i += 3) { if (i>0) w.Sep();
    w.Tok("("); w.WriteReal(t.X(verts[i])); w.Sep(); w.WriteReal(t.Y(verts[i+1])); w.Sep(); w.WriteReal(t.Z(verts[i+2])); w.Tok(")"); }
w.Tok(")"); w.End();
// IFCTRIANGULATEDFACESET(#pl,$,$,(CoordIndex 1-based),$) streamed the same way
```

**IFC2x3 (`Ifc2x3MeshWriter`) — eliminate the per-triangle entity explosion.** `IfcFaceBasedSurfaceModel`
is still the right top entity (open/non-manifold tessellation is invalid as `IfcFacetedBrep` — see
v2 §6), **but we do not need one `IfcFace` per triangle.** Two options, in order of preference:

- **Preferred — IFC2x3 `IfcTriangulatedFaceSet` is *not* available (it is IFC4+), so instead emit a
  single `IfcPolygonalFaceSet`? Also IFC4-only.** Therefore for 2x3 we keep `IfcFaceBasedSurfaceModel`
  but **share the `IfcCartesianPoint`s** (already done) and accept one `IfcFace`/`IfcPolyLoop` per
  triangle — this is schema-mandated for 2x3. The fix is purely *how* we write them: stream each
  `IfcPolyLoop`/`IfcFaceOuterBound`/`IfcFace` via the token API and **stream the `IfcConnectedFaceSet`
  face-ref list directly** instead of building a million-element `StringBuilder`. Keep `faceIds` out
  of a `List<int>`: write the `IfcConnectedFaceSet` *last* by recording only the first and count is
  impossible (ids interleave) — so instead **reserve the face-id list in a pooled `int[]` sized to
  `indices.Count/3`** (one array, reused across elements, grown as needed) rather than a `List<int>`,
  and stream it. This caps 2x3 per-mesh overhead at one reusable array.

> **Honest note:** IFC2x3 will always be heavier than IFC4 for tessellation (no compact face set).
> The default schema is IFC4 (v2 §4) precisely for this reason; the plan keeps 2x3 correct and
> bounded, not small. Document this in the F11 report ("IFC2x3 surface model is larger by design").

**Perf:** removes the 2x3 OOM and the giant face-ref string; IFC4 mesh writing becomes allocation-flat.

### A3 — Derive the coordinate offset from bounding boxes, not a vertex pre-pass (kills 0.4)

**What's missing:** a cheap offset so we can write in a single streaming pass.

**Where:** `IfcExporter.Export`/`ExportInstanced` (`Ifc/IfcExporter.cs:49,118`), `ItemCollector`,
`ExportDockPane.ModelMinCorner` (already does this for the *preview*).

**How:** before extraction, compute the scope's world-space min corner from
`ModelItem.BoundingBox()` (the .NET API returns this **without** reading triangles). The UI preview
(`ExportDockPane.cs:586`) already uses `root.BoundingBox()`; make the exporter use the *same*
value. Pass the offset into the exporter instead of computing it from materialised vertices. Delete
the `ComputeOffset` vertex lambdas.

**IFC grounding:** the offset is lifted into `IfcSite.ObjectPlacement` (already done,
`WriteSkeletonBase`) so geometry sits near the local origin (viewer precision), and the real-world
location is preserved in `IfcMapConversion` (IFC4) — unchanged behaviour, just sourced cheaply.

**System effect:** preview and actual export now agree (a latent inconsistency removed). Enables A4.

**Perf:** removes one full traversal of the entire model and the need to hold it for that traversal.

### A4 — Streaming extraction + bounded producer/consumer write (kills 0.1, 0.2, 0.5; this is P3)

**What's missing:** the pipeline never holds more than a bounded window of work.

**Where:** new seam between `Geometry/*Extractor.cs` and `Ifc/IfcExporter.cs`, driven from
`UI/ExportDockPane.RunExport`.

**How (non-instanced):**
1. Turn extraction into a *producer*: instead of returning `List<ElementMesh>`, the extractor runs
   on the **Navisworks UI thread (STA — mandatory for the read API**, confirmed by the sibling
   `ElementCollector` and v2 §5) and pushes each finished `ElementMesh` into a
   **`BlockingCollection<ElementMesh>(boundedCapacity: e.g. 64)`**.
2. A single *consumer* background thread owns the `StreamingStepWriter`, pulls meshes, writes their
   geometry + representation + placement + element + Psets + quantities, records the small `Occ`
   metadata, and **lets each `ElementMesh` go out of scope** (GC reclaims it immediately).
3. The consumer touches **no** Navisworks API object — only plain arrays/structs handed to it — so
   it is thread-safe off the UI thread.
4. The bounded capacity is the back-pressure that caps peak memory at ~64 elements.

**Skeleton ordering:** the skeleton (units/owner/project/site/building/storeys) and the final
relationship batches (`FinishRelationships`, spatial containment) are written by the consumer
before/after the element loop, exactly as today — only the *element bodies* are streamed. The
per-element metadata that must survive to the end (`Occ`, `byStorey`, storey ids) is **tiny ints and
short strings**, not geometry, so keeping it is fine.

**Threading/UI:** marshal progress to the UI via `control.BeginInvoke` rather than `DoEvents()`.
Disable the dock-pane tabs during export (already done). The UI thread now does only the
unavoidable STA reads; disk I/O and string building move off it, so **Navisworks no longer freezes**.

**System effect:** `MeshExtractor.Extract`'s signature changes (returns nothing / takes a sink
callback or `BlockingCollection`). `IfcExporter.Export` changes from "take a `List`" to "consume a
queue." `RunExport` orchestrates the two threads. `InstancedExtractor` gets the A4b treatment below.

**Perf:** converts peak memory from *O(model)* to *O(queue window)*, and overlaps read with write.

### A4b — Single-pass, low-memory instancing (kills 0.2 properly)

**What's missing:** instancing currently requires holding the whole `InstancedModel`.

**Where:** `Geometry/InstancedExtractor.cs`, `IfcExporter.ExportInstanced`.

**How — interleave geometry-definition writing with element writing:**
1. Replace the giant-string `Key` with a **fixed-size hash**: stream the quantised ordinates and
   indices through an incremental 128-bit hash (two FNV-1a/xxHash lanes). Dedup key =
   `struct { ulong h0, h1; int vCount, triCount; }`. Constant memory per *unique* geometry; the
   counts give the v2 §5 "false-positive guard" essentially for free.
2. As the UI-thread producer walks each element's fragments, for each fragment compute its hash. On
   a **new** hash, immediately hand the local mesh to the consumer, which writes its
   `IfcCartesianPointList3D` + `IfcShapeRepresentation` + `IfcRepresentationMap`, returns the
   `RepresentationMap` id, and **discards the mesh**. On a **repeat** hash, reuse the stored id.
3. The element then writes its `IfcMappedItem`s referencing those ids and is released.

Now the only things held for the whole export are the `Dictionary<DedupKey,int>` (id table — tiny)
and the per-element `Occ` metadata. Unique geometries are written and freed as discovered; instances
never accumulate.

**IFC grounding:** `IfcRepresentationMap` + `IfcMappedItem` + `IfcCartesianTransformationOperator3D`
is the instancing mechanism in **both** IFC4 and IFC2x3 (v2 §6 table) — unchanged. Order is free
(A1 note).

**Perf:** removes the single largest default-path memory hog (0.2) and makes instancing scale to
arbitrary model size.

### A5 — Faster vertex/matrix reads (P4)

**What's missing:** `PrimitiveSink.AddVertex` (`Geometry/PrimitiveSink.cs:52`) does three
`Convert.ToDouble(c.GetValue(...))` calls per vertex — each boxes a `float`.

**Navisworks grounding (confirmed):** `InwSimpleVertex.coord` surfaces as a **1-based, single-dimension
`System.Single[*]` SAFEARRAY**. You **cannot** `(float[])v.coord` — it throws
`Unable to cast 'System.Single[*]' to 'System.Single[]'` (a well-documented Navisworks gotcha). The
correct fast path is a bulk copy that respects the lower bound:

```csharp
// reused field: readonly float[] _c3 = new float[3];
var src = (Array)v.coord;
Array.Copy(src, src.GetLowerBound(0), _c3, 0, 3);   // primitive memmove, no per-element boxing
```

Apply the same `Array.Copy`-into-reused-buffer pattern to the 16-value local→world matrix in
`MeshExtractor.ReadMatrix` (`Geometry/MeshExtractor.cs:97`) / `GeometrySpike.ReadMatrix`.

**Honest ceiling (v2 §5):** the dominant cost is the **per-triangle COM callback marshaling**, which
managed code *cannot* remove — only P5 (native C++/CLI `InwSimplePrimitivesCB` shim) can, and it
stays deferred to Phase 4. A5 reduces per-vertex cost meaningfully but does not change the asymptote.

**System effect:** localised to `PrimitiveSink`/`ReadMatrix`; no API change.

**Perf:** measurable per-vertex speedup; complements A1/A4 which attack memory.

### A6 — Convert the scope to COM once, not per element (P3-adjacent)

**What's missing:** both extractors do `new ModelItemCollection { item }` +
`ComApiBridge.ToInwOpSelection(coll)` **per element** (`MeshExtractor.cs:54`,
`InstancedExtractor.cs:83`). On element-heavy models the repeated COM bridge conversion is pure
overhead.

**Navisworks grounding:** convert the **whole scope** once with
`ComApiBridge.ToInwOpSelection(modelItemCollection)`, iterate `selection.Paths()`, and map each
`InwOaPath3` back to its `ModelItem` with `ComApiBridge.ToModelItem(path)` to attach
properties/GUID. (`ComApiBridge` provides conversions in both directions; the sibling spike already
relies on the forward direction. **Verify the exact `ToModelItem` overload against the installed
SDK during the spike — Part B.**)

**System effect:** changes the extractor loop structure; property/role harvesting keys off the
mapped `ModelItem`. Keep a per-element grouping so fragments of the same item still produce one
`ElementMesh`/`InstancedElement`.

**Perf:** removes N COM-bridge round-trips; helps most exactly where it hurts now (many small items).

### A7 — Honest quality / decimation control (P6, corrected)

**What's missing & the correction:** v2/F13 implies quality presets map to *tessellation deflection*.
**They cannot, post-conversion.** `GenerateSimplePrimitives` returns the geometry **already
tessellated at NWC-creation time**; the COM read path exposes no facet-deviation parameter, so we
**cannot generate finer triangles than exist in the cache.** Today the presets only change the weld
tolerance (`ExportDockPane.cs:449`), which is honest but incomplete.

**How (rooted in what's actually controllable):**
- **Small file / Balanced / High detail** map to **(weld tolerance, optional decimation target)**:
  - *High detail* → exact weld (1e-6 m), no decimation.
  - *Balanced* → 0.1 mm weld (current).
  - *Small file* → coarse weld (1 mm) **plus** optional triangle decimation (quadric or
    vertex-cluster on the welded mesh, in `Geometry/`) to a target ratio.
- Keep the "Advanced" numeric field for the weld tolerance directly.
- **Filter insignificant geometry** (forum-recommended): allow excluding tiny/screen-only items
  (e.g. by bounding-box size threshold) — a cheap, large size win that needs no re-tessellation.

**System effect:** `MeshWelder` already does vertex-cluster welding; decimation is a new optional
pass between weld and write, inside A4's consumer. Update the F13 tooltip/help to state the
"cannot exceed source tessellation" truth.

**Perf:** fewer triangles → less marshaling (the only managed lever that reduces the per-triangle
callback count) **and** smaller files. Highest-leverage *correct* quality knob available.

### A8 — Streaming validator (Part G preview; kills the validator OOM)

`IfcValidator.Validate` must not `File.ReadAllLines`. Stream the file line-by-line
(`using var r = new StreamReader(path); while ((line = r.ReadLine()) != null)`), collecting defined
ids and references in a single pass with a `HashSet<int>` and a `List<(int from,int to)>` capped to a
sample. (Full discussion of the *real* schema validator is Part G.)

---

## Part B — Phase-0 verification (un-gate the de-risking the plan demanded)

**What's missing:** `GeometrySpike` exists (`Geometry/GeometrySpike.cs`) but is **unreachable** —
`ExportDockPane.RunSpike` (`UI/ExportDockPane.cs:731`) is an empty stub. So the interop signatures,
the **local→world transform convention** (column- vs row-major in `PrimitiveSink`), the
`GetLocalToWorldMatrix` layout, the **`ComApiBridge.ToModelItem` overload (A6)**, and the
IfcGuid byte-ordering are all still marked "verify on first run" and have **never been confirmed
against a real Navisworks**. v2 §10 calls Phase 0 the gate for everything.

**How:**
- Add a hidden/diagnostics button (or a temporary menu command) that calls `GeometrySpike.Run` and
  shows the result (triangle/vertex counts, world bbox, `.obj` dump path, GUID stability).
- **Acceptance checks (v2 §10/§11):**
  1. One mesh extracts; the world bbox matches the element's real position (proves the transform
     convention in `PrimitiveSink.AddVertex`; if mirrored/rotated, switch column↔row major there).
  2. A usable geometry **source identity** for dedup exists, else confirm the A4b hash fallback.
  3. `InstanceGuid → 22-char IFC GlobalId` is stable across re-export (`IfcGuid.VerifyStable`).
  4. Write a trivial IFC (skeleton + 1 proxy + 1 mesh) in **both** IFC4 and IFC2x3, **open in
     Solibri and re-append into Navisworks** with correct placement. This is the release gate that
     validates the 2x3 `IfcFaceBasedSurfaceModel` choice.

**System effect:** none on production code beyond wiring the button; it *validates* the assumptions
A1–A6 are built on. **Do B before trusting A on a real dataset.**

---

## Part C — IFC2x3 semantic parity (honour the "both schemas" rule)

v2's non-negotiable #2 is *every feature supports IFC4 and IFC2x3*. Today three semantic features are
IFC4-only by explicit `if (schema == Ifc4)` guards. Each diverges only in **entity vocabulary and
attribute count**, so the fix is schema-aware emission in `IfcExporter`/`TypeMapping` — no pipeline
change.

### C1 — IFC2x3 typed occurrences

**Where:** `IfcExporter.WriteElement` (`Ifc/IfcExporter.cs:341`), `Data/TypeMapping.cs`.

**Missing:** for 2x3, every mapped class falls back to `IfcBuildingElementProxy` + ObjectType.
v2 §6/F5 specifies 2x3 should emit the **generic distribution vocabulary**.

**IFC grounding:** IFC2x3 has no `IfcPipeSegment`/`IfcDuctSegment`/`IfcValve` (those are IFC4). The
2x3 equivalents are the generic flow classes: `IfcFlowSegment`, `IfcFlowFitting`,
`IfcFlowController`, `IfcFlowTerminal`, `IfcFlowMovingDevice`, `IfcFlowStorageDevice`,
`IfcFlowTreatmentDevice`, `IfcEnergyConversionDevice`, `IfcDistributionControlElement`. **Critical
attribute-count difference:** 2x3 flow occurrences have **8** attributes (…`,Tag`) — **no
`PredefinedType`** — whereas IFC4 typed elements have **9** (…`,Tag,PredefinedType`) and
`IfcBuildingElementProxy` differs again (2x3 9th = `CompositionType`, IFC4 9th = `PredefinedType`).
`WriteElement` currently emits a fixed 9-arg template, which would be **invalid** for 2x3 flow
classes.

**How:** change `TypeMapping.Catalog` from `string` (IFC4 only) to a record:
```csharp
struct IfcClass { string Ifc4; string Ifc2x3; string PredefEnum; /* optional */ }
```
e.g. `Pipe segment → { "IFCPIPESEGMENT", "IFCFLOWSEGMENT" }`,
`Valve → { "IFCVALVE", "IFCFLOWCONTROLLER" }`, `Duct segment → { "IFCDUCTSEGMENT", "IFCFLOWSEGMENT" }`.
In `WriteElement`, pick the entity by schema and emit the **schema-correct arg count** (9 for IFC4
typed/proxy with PredefinedType/CompositionType; 8 for 2x3 flow occurrences; 9 for 2x3 proxy with
trailing CompositionType). Centralise the arg template per (schema, entity-family).

### C2 — IFC2x3 type objects (`IfcRelDefinesByType`)

**Where:** `IfcExporter.FinishRelationships` (`Ifc/IfcExporter.cs:282`), `TypeMapping.TypeEntityFor`.

**IFC grounding:** `IfcRelDefinesByType` exists in 2x3. The 2x3 `IfcElementType`-subtype constructor
is **9 attributes** (`GlobalId,OwnerHistory,Name,Description,ApplicableOccurrence,HasPropertySets,
RepresentationMaps,Tag,ElementType`) — IFC4 adds a 10th (`PredefinedType`) for most. The current
code emits 10 args (IFC4). Add the matching 2x3 type entity (`IfcFlowSegmentType`, etc.) and the
9-arg template; gate the arg count by schema, not the whole feature.

### C3 — IFC2x3 classification

**Where:** `FinishRelationships` classification block (`Ifc/IfcExporter.cs:312`).

**IFC grounding:** `IfcRelAssociatesClassification` is identical in both. The referenced entities
differ: 2x3 `IfcClassificationReference(Location,ItemReference,Name,ReferencedSource)` = **4 args**
(IFC4 = 6: adds `Description,Sort`); 2x3 `IfcClassification(Source,Edition,EditionDate,Name)` =
**4 args** (IFC4 = 7). Emit the 4-arg forms for 2x3; drop the IFC4-only guard.

### C4 — IFC2x3 rotation baking

**Where:** `WriteSkeletonBase` (`Ifc/IfcExporter.cs:198`), `WriteTransform`.

**Missing:** v2 §7 says for 2x3 (no `IfcMapConversion`) the **offset *and rotation*** are baked into
the placement. Today only the **offset** is applied; `CoordOptions.RotationDeg` is used **only** in
the IFC4 `IfcMapConversion` path, so a rotated 2x3 export is mis-oriented.

**IFC grounding:** bake rotation into `IfcSite.ObjectPlacement`'s `IfcAxis2Placement3D` by setting
its `RefDirection` (X axis) to `(cos θ, sin θ, 0)` (and `Axis` to Z). All child placements inherit
it. The F11 report already states "baked into placement (IFC2x3)" — make it true.

**System effect of C1–C4:** confined to `IfcExporter` + `TypeMapping`; the streaming pipeline
(Part A) is schema-agnostic and untouched. Update the F11 report counters to show these now populate
for 2x3 too.

---

## Part D — Materials with instancing (fix the friction-free default)

**What's missing:** `ExportDockPane.WireCrossControls` (`UI/ExportDockPane.cs:702`) **disables**
materials whenever instancing is on, and `ExportInstanced` never calls `WriteStyle`. v2 §4's
friction-free default has **both ON**, so the default export silently drops colour.

**IFC grounding & how:** style the geometry **once on the `IfcRepresentationMap`'s representation
item** via `IfcStyledItem` (IFC4) / `IfcStyledItem`+`IfcPresentationStyleAssignment` (2x3) — exactly
the dual path already in `WriteStyle` (`Ifc/IfcExporter.cs:389`). All `IfcMappedItem`s of that map
inherit the style, so one style covers N instances. To stay correct when identical geometry appears
in **different colours**, fold a **quantised colour into the A4b dedup key** so colour variants split
into separate definitions (tiny dedup loss, correct visuals).

**System effect:** re-enables the default; touches A4b (key) + `ExportInstanced` (call `WriteStyle`
on the rep-map item) + remove the UI lockout. `Material` is read per item by
`PropertyHarvester.GetMaterial`; carry it on `InstancedElement`/the unique geometry.

**Perf:** negligible — one style per unique geometry, not per instance.

---

## Part E — PredefinedType support

**What's missing:** `IFC_STRUCTURE_NOTES.md` lists `PredefinedType` as requested; `ClassKey` carries
a `class|predef` encoding *in comments only*; `WriteElement` always emits trailing `$`, and the UI
offers no picker.

**IFC grounding:** most IFC4 distribution elements define a `PredefinedType` enum
(`IfcPipeSegmentTypeEnum`, `IfcValveTypeEnum`, …) as the final occurrence attribute, mirrored on the
type object. 2x3 occurrences generally have **no** occurrence-level PredefinedType (set it on the
type object only).

**How:** in the Mapping grid (`UI/ExportDockPane.cs:259`) add an optional third column "Predefined
type" populated per chosen class from a small enum table in `TypeMapping`. Decode `class|predef` in
`WriteElement` (IFC4: emit the enum as the 9th arg; else `.NOTDEFINED.`/`$`) and in `TypeEntityFor`
(stamp it on the type object). Default `$`/`.NOTDEFINED.` keeps friction-free behaviour.

**System effect:** localised to UI + `TypeMapping` + `WriteElement`; ties into C1/C2 arg templates.

---

## Part F — (folded into A7 above; quality/decimation)

*(Numbered here for traceability to the gap list; the work lives in A7.)*

---

## Part G — Real, isolated output validation (F12 done properly)

**What's missing:** `IfcValidator` is a homegrown structural check (envelope, dup ids, dangling
refs) and, until A8, loads the whole file. v2 §12 specifies an **optional, isolated** third-party
validator behind the "Validate output" checkbox, never in the hot path.

**How (rooted in v2 §12 licensing posture):**
- Keep the streamed structural check (A8) as the always-available, zero-dependency baseline.
- Add an **optional, separately-deployed assembly** exposing `IIfcValidator`, loaded via the
  existing `AssemblyResolver` only when present. Candidate engines: **xBim Toolkit (CDDL-1.0)** or
  **IfcOpenShell (LGPL-3.0, kept as a replaceable dynamically-loaded DLL, never statically linked)**.
  The core ships nothing extra; users who validate drop in the optional component.
- Harden the structural check: the current ref regex `#(\d+)` (`Ifc/IfcValidator.cs:16`) matches
  digits **inside string literals** (e.g. a Pset value `"#5 rebar"`) → false "dangling reference".
  Strip/ignore quoted spans before scanning for refs.

**System effect:** validation stays off the hot path and off by default; only the optional assembly
adds a dependency, preserving the MIT/Apache core (v2 §12).

---

## Part H — Per-version build + smoke matrix

**What's missing:** `BIMCamel.csproj` parameterises `NavisworksDir` and deploys one DLL to
2024/25/26, but there is **no CI that builds/smoke-tests against each installed release**, despite
v2 §11 flagging per-release COM-interop differences as a real risk.

**How:** a build script (PowerShell) that, for each installed `Navisworks Manage 20XX`, runs
`dotnet build -p:NavisworksDir="…20XX"` and a headless smoke export against a tiny sample model in
both schemas, asserting the structural validator passes and the file re-opens. Gate releases on it.

---

## Phasing, ordered by urgency

**Performance-first** (per the directive): every phase up to and including the benchmark is about
speed/stability; semantic features come strictly after.

| Phase | Scope | Gate / exit criterion |
|------|-------|-----------------------|
| **P-A0** | **Part B** — wire & run the Phase-0 spike on a real model | Transform/GUID/round-trip confirmed in both schemas; native-shim interop signature checked (PERF.2) |
| **P-A1** | **A1 + A2 + A3** — streaming writer, streamed meshes, bbox offset | A 40 MB-class IFC4 model exports without OOM; memory ≈ O(element) |
| **P-A2** | **A4 + A4b** — producer/consumer + single-pass instancing | **UI no longer freezes; the crash is gone**; default (instanced) path bounded; large model exports |
| **P-A3** | **PERF.2 native shim + A5 + A6** — attack the per-triangle read asymptote, fast reads, single COM convert | Extraction time drops sharply on the benchmark model; managed fallback still works |
| **P-A4** | **PERF.3 byte writer + A7 + benchmark harness** (v2 §9) | Measured speed/size vs Codemill; *only now* is the marketing claim allowed |
| **P-B** | **Part C (C1–C4) + D + E** — 2x3 parity, materials+instancing, PredefinedType | Every F-feature works in both schemas; default export keeps colour |
| **P-C** | **A8 + Part G** — streamed + optional real validator | Validation never OOMs; optional engine isolated |
| **P-D** | **Part H** — CI matrix | Per-version smoke passes |

## Acceptance gates (unchanged in spirit from v2 §11, made concrete)

1. **No-crash:** a model that produces a ≥100 MB IFC exports to completion with peak managed memory
   bounded (target: a few hundred MB, not multiples of output size), in both schemas.
2. **No-freeze:** the Navisworks UI stays responsive (progress via `BeginInvoke`, work off the UI
   thread) throughout.
3. **Round-trip:** sample exports open in Solibri and re-append into Navisworks with correct
   placement, IFC4 **and** IFC2x3 (validates A2's 2x3 surface-model + C4 rotation).
4. **Determinism:** re-export yields identical `GlobalId`s (`IfcGuid.VerifyStable`).
5. **Speed/size claim** stays unspoken until the §9 benchmark measures it vs Codemill.
6. **Per-version smoke** passes on every installed 2024/25/26.

---

### One-paragraph summary

**xBim is not in the pipeline, so it is not the bottleneck — the slowness is entirely in our own
extract→write code.** Two distinct performance problems: (1) the export *crashes* because the
pipeline is not streaming — it materialises the whole model, builds multi-megabyte entity strings
(and, on the default instanced path, equally large string dedup keys), passes over the geometry twice
for the coordinate offset, and runs it all on the UI thread; (2) even when it completes it is *slow*
because `GenerateSimplePrimitives` marshals **one COM callback per triangle**. **Part A rebuilds the
pipeline to stream end-to-end with bounded memory and a background writer — this removes the crash
and most constant-factor cost, and must land first.** The per-triangle read asymptote is then
attacked by the promoted **native C++/CLI shim (PERF.2)**, the only lever that touches it. Everything
non-performance — dual-schema parity, materials-with-instancing, PredefinedType, real validation,
CI — comes strictly afterward and does not require changing the streaming core.
