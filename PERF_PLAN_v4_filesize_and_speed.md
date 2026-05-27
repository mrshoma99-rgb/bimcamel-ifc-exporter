# BIMCamel v4 — Shrink the file, then cut the time (grounded in the `wsc.ifc` prova)

> Continuation of `IMPLEMENTATION_PLAN_v3_perf_and_gaps.md` after the first full real export.
> Part A (streaming) is done and **the crash is gone**. This plan tackles the three things the
> prova exposed, in order: (F) the 2 GB file, (S-managed) cheap speed wins, (S-native) the real
> speed lever.

## 0. What the prova proved

`wsc.ifc`, IFC4, whole model (860,262 items scanned):

| Metric | Value | Reading |
|---|---|---|
| Elements | 674,641 | one occurrence each |
| Instances | 683,917 | **≈1.01 per element** — almost no within-element instancing |
| Unique geometries | 102,384 | **6.7× instancing** — geometry sharing works well |
| Triangles | 26,539,426 | total across instances |
| **File size** | **2,000,042 KB (≈1.9 GB)** | the problem |
| **Duration** | **715,952 ms (≈12 min)**, 37,069 tris/s | the *other* problem (timer starts AFTER the scan) |
| Materials / Types / Class. | 0 / 0 / 0 | instancing disables materials (v3 Part D); no mapping configured |

**Two conclusions that redirect the work:**
1. **File size is now metadata-bound, not geometry-bound.** Instancing already shrank geometry
   (only 102k unique meshes). The 1.9 GB is dominated by *per-element* entities — and the report's
   1.01 instances/element means every element carries its own placement, transform, quantities and
   **property sets**.
2. **The 12 min is pure extract+write** (the duration timer starts after the model scan), so it is
   the **per-triangle COM marshaling** in `GenerateSimplePrimitives` — the read asymptote.

## 1. Estimated byte budget (confirm with F0 before trusting)

| Bucket | Est. size | Est. share | Lever |
|---|---|---|---|
| **Property sets** (`IfcPropertySingleValue`/`IfcPropertySet`/`IfcRelDefinesByProperties`, per element) | **~1.3 GB** | **~65%** | **F3 dedup** |
| Per-element non-property (shape rep, prod-def, placement, element, **quantities ~195 MB**) | ~350 MB | ~17% | F1, F2 |
| Per-instance transforms (3× `IfcDirection` + point + operator + mapped item) | ~157 MB | ~8% | F1 |
| Geometry (point lists + coord indices for 102k unique meshes) | ~200 MB | ~10% | F5/A7 |

So: **properties are the whole game for file size**; quantities and transforms are easy secondary
wins; geometry is already fine for this model.

---

## F0 — Instrument first (cheap, do before guessing)

Two tiny additions so we *measure* instead of estimate:

- **Phase timing in the report.** Time the scan (tree walk + extents) separately from extract+write
  and from validation; print all three. Today only extract+write is timed (`sw` starts after
  `BeginBusy`). Touch: `ExportDockPane.RunExport` (wrap `ResolveScope`/`ScopeMinCorner` in their own
  stopwatch), `BuildReport` (add the lines).
- **Entity byte profile (opt-in).** A streaming pass over the written file that sums bytes per
  leading entity keyword (`IFCPROPERTYSINGLEVALUE`, `IFCCARTESIANPOINTLIST3D`, …) and prints the top
  15. Reuses the `StreamReader` pattern from the (to-be-streamed) validator. Gated behind a
  "Profile size" checkbox or folded into "Validate output". Confirms the budget above on the real
  2 GB file.

Exit: we know the true top-3 byte consumers and the scan-vs-export time split.

---

## Part F — File size

### F1 — Drop redundant transform axes (easy; ~65 MB + ~2 M fewer entities)

**Now:** `IfcExporter.WriteTransform` writes 3 `IfcDirection` + 1 point + 1 operator **per instance**
(`Ifc/IfcExporter.cs`). For the common **axis-aligned, unit-scale** instance those three directions
are `(1,0,0)/(0,1,0)/(0,0,1)` — repeated 683,917×.

**IFC grounding:** `IfcCartesianTransformationOperator3D` has **optional** `Axis1`, `Axis2`, `Axis3`,
`Scale` — omitted (`$`) they default to the context axes and scale 1.0.

**Change:**
- If the instance rotation is identity (within tol) and scale ≈ 1 → emit
  `IFCCARTESIANTRANSFORMATIONOPERATOR3D($,$,#pt,$,$)` — just the local origin point. No directions.
- Otherwise → emit directions, but **dedup them through a small per-export cache** keyed by the
  quantized direction vector (rotated parts reuse the same handful of axes).

Net: from 6 entities/instance to ~2 for the (likely majority) axis-aligned case.

### F2 — Make base quantities optional (easy; ~195 MB when off)

**Now:** every element gets `IfcElementQuantity` + 3 `IfcQuantity*` + `IfcRelDefinesByProperties`
= 5 entities, unconditionally (`WriteQuantities`).

**Change:** add a **"Compute base quantities"** checkbox (Properties tab; default ON to preserve
behaviour). Thread a bool through `ExtractOptions`/the export call; guard the `WriteQuantities` call.
Off → 5 fewer entities/element. (Also lets quantity-averse workflows shrink immediately.)

### F3 — Property-set content dedup (the big lever; targets the ~1.3 GB)

**Now:** `WritePropertySets` writes, **per element**, for each category: one `IfcPropertySet`, N
`IfcPropertySingleValue`, one `IfcRelDefinesByProperties`. Nothing is shared, so identical property
sets are duplicated across hundreds of thousands of elements.

**Change (streaming-friendly):**
- Hash each element's pset by content: `pset name` + ordered `(propName, kind, value)` tuples →
  64-bit hash.
- Keep `Dictionary<long, int> psetId` (hash → written `IfcPropertySet` id) and
  `Dictionary<int, List<int>> psetMembers` (psetId → object ids).
- First time a pset content is seen: write the `IfcPropertySet` + its `IfcPropertySingleValue`s once,
  store the id. Every time: append the element id to `psetMembers[psetId]`.
- **At export end**, write **one** `IfcRelDefinesByProperties` per unique pset with **all** its
  objects in `RelatedObjects` (a SET — one relationship may relate many occurrences; valid IFC).

**Effect:** property entities collapse from *per element* to *per distinct pset content*. Hit rate is
data-dependent, but type-level categories (material, type, manufacturer, nominal sizes…) are shared
across all like elements → potentially the single largest size cut.
**Memory:** the hash dict + member-id lists ≈ a few tens of MB for 674k elements (ints) — fine, and
still bounded.
**Caveat:** categories carrying per-occurrence values (Id, Mark, length) won't dedup — no worse than
today, just hashed. F0 will show the realised hit rate.

Touch: `IfcExporter.WritePropertySets` (becomes "register", returns nothing/defers), a new
end-of-export `WriteDeferredPsetRelations`, both `Export` and `ExportInstanced` loops + their tail.

### F4 — Type-property hoisting (proper IFC; larger, optional, after F3)

The fully-correct IFC answer: group occurrences by **Type** and hoist properties that are constant
across a type onto `IfcElementType.HasPropertySets`, shared via `IfcRelDefinesByType`
(IFC_STRUCTURE_NOTES.md occurrence-vs-type). Needs a type key (the Semantics **Type** role, or a
synthesized one). F3 already captures most of the byte win without requiring type configuration, so
F4 is a later refinement, not a prerequisite.

### F5 — Geometry size knob (A7; lower priority for this model)

Geometry is ~10% here, but for geometry-heavy models: wire **quality presets** to weld tolerance
(done) **plus** an optional decimation pass on the welded unique mesh, and an item-size filter to
drop trivial screen-only geometry. Honest limit (v3 A7): we **cannot** re-tessellate finer than the
NWC cache — only coarsen.

---

## Part S — Speed (the 12 min)

### S1 — `Array.Copy` vertex/matrix reads (A5; easy, pure managed)

`PrimitiveSink.AddVertex` does 3× `Convert.ToDouble(c.GetValue(...))` per vertex — boxing per
ordinate. Replace with `Array.Copy((Array)v.coord, lb, _reused3, 0, 3)` into a reused `float[3]`
(the coord SAFEARRAY is a 1-based `Single[*]` — direct `(float[])` cast throws; `Array.Copy` is the
fast, correct path). Same for the 16-value matrix in `ReadMatrix`. Lowers per-vertex constant cost;
does not change the asymptote.

### S2 — Convert the scope to COM once (A6; medium, pure managed)

Both extractors do `new ModelItemCollection{item}` + `ComApiBridge.ToInwOpSelection` **per element**.
Convert the whole scope **once**, iterate `selection.Paths()`, and map each `InwOaPath3` back to its
`ModelItem` via `ComApiBridge.ToModelItem(path)` (verify the exact overload against the installed
SDK). Removes N COM-bridge conversions — helps most on element-heavy models like this one (674k).

### S3 — Native `InwSimplePrimitivesCB` shim (PERF.2; the real lever, big effort)

The 715 s is the **per-triangle managed callback**. Implement `InwSimplePrimitivesCB` in a
**C++/CLI mixed-mode assembly**: the COM callback fires into native code, accumulates vertices/indices
in a native buffer, and crosses the managed boundary **once per fragment** (bulk copy) instead of per
triangle. Hidden behind the `IPrimitiveSource` seam so the managed `PrimitiveSink` stays as fallback.
Expected large (≈5–10×) extraction speedup — **benchmark-gated**, not promised. Cost: a second build
artifact + per-runtime care. This is the only thing that moves 37k tris/s materially.

---

## Ordering (cheap & certain first, big efforts last)

| Step | Work | Why here | Risk |
|---|---|---|---|
| **1. F0** | Phase timing + byte profile | Measure before optimizing; confirms F3 is worth it | trivial |
| **2. F1 + F2** | Drop transform axes; optional quantities | Easy, immediate file-size wins | low |
| **3. S1 + S2** | `Array.Copy` reads; single COM convert | Easy, pure-managed speed wins | low/med |
| **4. F3** | Property-set dedup | **Biggest file-size win** (~the 1.3 GB) | medium |
| **5. S3** | Native C++/CLI shim | **Biggest speed win** (the 12 min) | high |
| **6. F4 / F5 / D** | Type hoisting; decimation; materials-with-instancing | Refinements | medium |

**Gates:** after step 2–3 re-run `wsc` and read F0's numbers; after F3 the file should drop
multiple-fold (target: well under 1 GB, ideally a few hundred MB); after S3 the 12 min should fall
toward minutes. Speed/size claims stay unspoken until measured (v3 §5/§9).
