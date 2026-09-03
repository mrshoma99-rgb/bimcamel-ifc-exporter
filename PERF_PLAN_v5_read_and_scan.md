# BIMCamel v5 — Stop doing the work twice (read side + scan side)

> Continuation of `PERF_PLAN_v4_filesize_and_speed.md`. v4 fixed the **file** (pset dedup, optional
> quantities, transform axes) and named the **read** as the remaining wall. This plan is a full
> re-reading of the pipeline — collector, harvester, both extractors, exporter, STEP writer,
> validator, and the mapping-scan path — looking for work that is done twice, done per element when
> it could be done once, or done at all when it need not be.
>
> Two halves, because they have different ceilings:
> **Part E** = export speed. **Part S** = the scan that feeds the mapping UI.

---

## 0. The ceiling, stated first

`PrimitiveSink.cs` records the measurement that governs everything here:

> this per-vertex callback IS the export's cost centre (82–92% of wall clock on real models,
> a near-constant ~2.9 µs/vertex)

and the `wsc.ifc` prova agrees: 715,952 ms for 26,539,426 triangles = 37,069 tris/s.

So **every item in Part E except E1 is dividing up the remaining 8–18%.** That is still minutes on a
12-minute export and worth taking, but the plan must not pretend otherwise. Only two things break the
ceiling without a native shim:

- **E1** — stop re-reading geometry we have already read (the prova says 85% of fragment reads are
  repeats).
- Reading fewer triangles at all (quality presets, scope) — already shipped.

The v4 **S3 native `InwSimplePrimitivesCB` shim** remains the largest single-thread lever and is
unchanged by this plan; it stays queued behind these because these are cheap and certain.

**Every number below is a hypothesis until the report's own phase lines confirm it.** The report
already prints `COM convert / Geometry rd / Prop harvest / Weld / Geom write / Prop write /
Qty compute / UI pump / Other`. S4 adds the same decomposition for the scan phase, so both halves
become measurable before they are optimised.

---

# Part E — Export

## E1 — Pre-read instance identity (the only item that beats the ceiling)

**Now.** The prova: 683,917 instances, **102,384 unique geometries** — 6.7× instancing. The exporter
writes each unique mesh once, which is why the file is geometry-cheap. But the *extractor* calls
`GenerateSimplePrimitives` for all 683,917, because `InstancedExtractor.Key(lm)` is computed **after**
the triangles have been read (`InstancedExtractor.cs`). In plain terms: **85% of the most expensive
work in the export is re-reading shapes we already hold.**

**Why it was not fixed in v4.** Recognising a repeat before reading it needs a geometry identity from
Navisworks, and there isn't one: `InwOaFragment3.Geometry` throws
`"<<NavisWorks Error - Not implemented>>"` on a real install, and reflection over both assemblies
found no bulk/mesh/handle surface. v4 therefore concluded "make each callback cheap, not avoid the
callbacks". That conclusion is right about the *API*, and wrong about the *model*: the source
application already knows two occurrences of a family type are the same shape, and it tells us in
data we can read without touching a triangle.

**The candidate key.** Everything in it is cheap (bounding boxes are cached by Navisworks; the type
name is already read for the semantic roles):

```
candidate = ( TypeName , quantised bbox size (0.1 mm) , fragment count , item class name )
```

**The protocol** — a guess alone is not shippable, so the guess verifies itself:

1. First sighting of a candidate key → read fully, store the resulting `LocalMesh` list and their
   real 128-bit `DedupKey`s against the candidate.
2. Later sighting → **skip `GenerateSimplePrimitives` entirely**; reuse the stored meshes and keys,
   and read only each fragment's `GetLocalToWorldMatrix` (one COM call, no triangles) for the
   placement, pairing meshes to fragments by index.
3. Fragment count mismatch → the assumption is void for this occurrence; full read, no caching.
4. **Every Nth occurrence (default 20) → read fully anyway and compare the fresh `DedupKey`s against
   the cached ones.** Match → the candidate has earned another 20. Mismatch → evict the candidate,
   blacklist it for the rest of the export, and fall back to full reads for that key.

The sampling costs 5% of the reads it saves and turns "trust me" into a number. The report prints
verifications run, mismatches found, and reads skipped, so a user can see whether it held.

**Correctness stance.** Off by default. A mismatch is a *silently wrong mesh* — the worst failure
this exporter can produce — so it is opt-in, labelled as an approximation, and any mismatch at all is
surfaced in the report rather than swallowed.

**Memory.** The cache holds the unique geometry of the model: on `wsc` roughly 102k meshes ≈ a few
hundred MB. Bounded by an explicit byte budget (default 512 MB) with LRU eviction; an evicted
candidate simply goes back to full reads. Peak heap is already reported, so the cost is visible.

**Expected.** If the assumption holds on Revit/Plant-derived NWCs, the fragment reads drop by roughly
the instancing ratio — ~4–5× on the read, which is 82–92% of the export. Benchmark-gated, unpromised.

**Touch:** `Geometry/InstancedExtractor.cs` (candidate cache + skip path), `Geometry/ExtractOptions`
(the toggle + budget), `UI/ExporterView.xaml(.cs)` (checkbox, report lines), `Geometry/ExportTiming`
(skipped/verified/mismatch counters).

---

## E2 — One property pass per element, not two

**Now.** Both extractors call `PropertyHarvester.Harvest(item, …)` and then
`PropertyHarvester.ReadRoles(item, …)`. Each independently walks
`item.PropertyCategories → cat.Properties → p.Value` from scratch
(`PropertyHarvester.cs`, the `Harvest` loop and the `ReadRoles` loop). Every one of those member
accesses crosses into COM. On 674,641 elements with ~40 properties each that is tens of millions of
interop calls — **paid twice for the same bytes.**

**Change.** One `HarvestWithRoles(item, filter, roles)` that fills the `IfcProp` list *and* picks the
four role values inside the same loop, returning both. `Harvest` and `ReadRoles` stay as thin wrappers
for the callers that only want one (the mapping preview, the scans).

Two allocations go with it: `ReadRoles` currently builds a throwaway `IfcProp` per matched property
just to reach `.Value` (`Typed("", "", p.Value).Value`), and so does `ScanValues`. Split the value
formatting into a `ValueToString(VariantData)` that both use without allocating.

**Note on the pset filter.** `Harvest` must still enumerate every category to read its name before
skipping it — that is unavoidable and already minimal (it skips `cat.Properties` on a filtered
category, which is where the cost is).

**Expected.** Roughly halves the `Prop harvest` line. On models with a live DataTools/database link —
where the report already warns above 2 ms/element — it halves something much larger than 8%.

**Touch:** `Data/PropertyHarvester.cs`, `Geometry/MeshExtractor.cs`, `Geometry/InstancedExtractor.cs`.

---

## E3 — Convert the scope to COM in chunks, not per element

**Now.** Both extractors do, **per element**:

```csharp
var coll = new ModelItemCollection { item };
InwOpSelection comSel = ComApiBridge.ToInwOpSelection(coll);
```

674,641 bridge conversions, each with its own setup cost. This is v4's **S2**, still unbuilt.

**Change, with the refinement v4 missed.** v4 said "convert the whole scope once". Converting 674k
items to a single COM selection is a memory risk the plan did not price. Convert in **bounded chunks**
(default 2,048 items): build one `ModelItemCollection` per chunk, one `ToInwOpSelection`, then walk
`selection.Paths()` and map each `InwOaPath3` back with `ComApiBridge.ToModelItem(path)`. Same
saving, bounded memory, and the chunk size is a single constant to tune.

**Risks, named.**
- `Paths()` order is not documented to match the input collection order — hence the map-back rather
  than positional pairing. The map-back is also what keeps the per-element semantics (properties,
  keys, issue reporting) attached to the right item.
- The exact `ToModelItem` overload varies by API year; CI compiles all four (2024–2027), so a wrong
  guess fails the build rather than shipping.
- If a chunk conversion throws, fall back to per-element conversion **for that chunk only** — one bad
  item must not cost the export.

**Expected.** Whatever the report's `COM convert` line says today, minus almost all of it.

**Touch:** `Geometry/MeshExtractor.cs`, `Geometry/InstancedExtractor.cs` (a shared chunked
path-enumerator helper).

---

## E4 — Fold the extra passes over every coordinate

**Now**, for one element on the plain (non-instanced) path, every coordinate is visited **four**
times:

| Pass | Where | Timed? |
|---|---|---|
| 1 | `MeshWelder.Weld` — vertices, then indices | `Weld` |
| 2 | `IMeshWriter.WriteMesh` — vertices, then indices | `Geom write` |
| 3 | `MeshQuantities.Compute` — indices, then vertices | `Qty compute` |
| 4 | revision signature — `for i in el.Vertices → hm.Add(...)` | **no — lands in `Other`** |

≈4 full sweeps over ~240 M doubles on `wsc`. Pass 4 is invisible on today's report, which is why it
was never questioned.

**Change — quantities during welding.** `Weld` already touches every surviving vertex as it appends
it and every surviving triangle as it remaps and degenerate-checks it. That is exactly the input the
quantities need, so the volume, area and AABB are accumulated in those same loops and pass 3
disappears. `MeshQuantities.Compute` stays for the callers that did not weld, and both share one
`Finish()` so the folded numbers cannot drift from the reference ones.

**Only the plain path.** The instanced path measures once per **unique geometry** in the writer
(102,384 times), not once per fragment (683,917). Folding it into the per-fragment weld there would
compute quantities 6.7× *more* often — a regression. It stays where it is.

**Correctness note.** Welding changes the mesh, so quantities must be computed on the **post-weld**
mesh to match today's behaviour — which is what folding into the weld gives, provided the
accumulation runs on the remapped, non-degenerate triangles and the box covers the welded vertices.
Both hold by construction here.

**Pass 4 is deliberately NOT folded in — and this is a change from the first draft of this plan.**
The intent was to hash the revision signature from the `DedupKey` the way the instanced path does.
Two facts killed it:
1. The manifest hash is a *sequential* FNV fold of semantics, then the index count, then the
   coordinates. Restructuring what goes in changes every element's hash, so the first export after
   the upgrade would report the entire model as MODIFIED against any existing `.bcmanifest`
   baseline. That is a real cost to the one feature whose whole promise is "re-exports diff cleanly".
2. The pass is smaller than it looks: it runs over the **welded** vertices (already collapsed from
   3-per-triangle), and each `Hasher.Add(double)` is a round, a cast, an xor and a multiply.

Trading a broken diff for that is a bad deal, so the pass stays. Worth revisiting only alongside a
deliberate manifest version bump.

**Touch:** `Geometry/MeshWelder.cs`, `Geometry/MeshQuantities.cs`, `Geometry/MeshExtractor.cs`,
`Ifc/IfcExporter.cs`.

---

## E5 — Use the streaming writer for the entities that repeat most

**Now.** `StreamingStepWriter` has a zero-allocation entity API (`Begin`/`Tok`/`RefTok`/`WriteReal`/
`WriteStr`/`End`) built precisely so large models stop churning strings. The mesh writers use it. The
entities *around* the mesh do not:

| Entity | Frequency on `wsc` |
|---|---|
| `IFCSHAPEREPRESENTATION`, `IFCPRODUCTDEFINITIONSHAPE`, `IFCLOCALPLACEMENT` | 3 × 674,641 elements |
| `IFCMAPPEDITEM` | 683,917 instances |
| `IFCCARTESIANPOINT` in `WriteTransform` (3 × `R6()` inside a format string) | 683,917 instances |
| a fresh `StringBuilder` for the mapped-item list | per element |

Each is an interpolated string plus, for the point, three `double.ToString()` allocations — in the
hottest loop in the writer, feeding the GC that the peak-heap line already tracks.

**Change.** Convert them to the streaming API. `IFCCARTESIANTRANSFORMATIONOPERATOR3D` and
`IFCMAPPEDITEM` too; the mapped-item list can be streamed directly into the
`IFCSHAPEREPRESENTATION` from the `List<int>` with no `StringBuilder` at all.

Also pool the per-element `int[] ptIds` in `Ifc2x3MeshWriter` the way `_faceIds` already is — it is
allocated fresh per element today.

**Expected.** Part of `Geom write` and most of the writer's share of `Other`, plus less GC pressure
(which shows up as `Other` and as peak heap). Mechanical, no behaviour change — the emitted bytes must
be identical, which is exactly what makes it safe.

**Touch:** `Ifc/IfcExporter.cs` (element + instance writers, `WriteTransform`), `Ifc/MeshWriters.cs`.

---

## E6 — Validation without a regex per line

**Now.** `IfcValidator.Validate` streams the output twice and runs a compiled
`Regex.Match` on **every line** of a file that the prova says can be 2 GB — tens of millions of regex
invocations. It is **on by default**, and it runs *after* the export stopwatch stops, so it is
wall-clock the user feels and the report never shows.

**Change.**
- Replace `Def.Match(line)` with a hand-rolled scan: `line[0] == '#'`, read digits, expect `=`, read
  the entity name to `(`. Same information, no regex machinery, no `Match`/`Group` allocation per
  line.
- Same for `GuidRx` and `EnumRx` — both are simple character-class checks over a short string.
- Skip lines that cannot matter before doing any work.
- **Time it and print it**, so the next person can see what validation costs.

**Expected.** Typically 5–10× on that pass. Nothing about *what* is validated changes; the issue list
for a given file must be byte-identical.

**Touch:** `Ifc/IfcValidator.cs`, `UI/ExporterView.xaml.cs` (the report line).

---

## E7 — Get the writing off the read thread

**Now.** `RunOneExport` calls `IfcExporter.Export*` directly with the extractor's lazy
`IEnumerable`, so read and write **interleave on the Navisworks UI thread**. This is v3's Part A4,
designed and never built.

**Change.** Producer stays on the UI thread and does nothing but read (it must — the API is STA). A
bounded `BlockingCollection` (capacity ~64 elements) carries `ElementMesh`/`InstancedElement` to a
single background consumer that owns the `StreamingStepWriter` and does welding-dependent work,
serialisation and disk I/O.

**The details that make it correct**, none of which are optional:

- **Bounded queue.** Unbounded would reintroduce the v2 memory failure the streaming writer was built
  to fix. Capacity is small on purpose: the producer must block, not buffer.
- **Exceptions.** A consumer fault must stop the producer and surface as the export's exception, not
  be swallowed on a background thread and leave a truncated file. `CompleteAdding` in a `finally`,
  consumer exception captured and rethrown on the UI thread after the join.
- **Cancellation.** Same path as today — the tick throws, the producer unwinds, the consumer drains
  and finishes the file cleanly.
- **Timing counters.** `ExportTiming`'s plain statics are documented as safe "single-threaded export".
  With two threads the read-side and write-side counters are touched by different threads; they are
  monotone accumulators, so split them into producer-owned and consumer-owned fields rather than
  interlocking every add on the hot path.
- **`ExportIssues`.** Same treatment — the producer owns the read-side counters.
- **Progress.** The producer already marshals via the tick; the consumer must never touch WPF.

**Expected.** Collects the write-side share (the 8–18%) into the read's shadow, and makes E5's
remaining string work free. It also fixes a UX defect: the UI stops being pumped from inside the
write loop.

**Touch:** new `Ifc/ExportPipeline.cs`, `UI/ExporterView.xaml.cs` (`RunOneExport`),
`Geometry/ExportTiming.cs`.

---

# Part S — The scan that feeds the mapping UI

## S1 — Stop calling `BoundingBox()` on every element

**Now.** `ItemCollector.ScopeMinCorner` is a **whole extra pass over every element**, calling
`item.BoundingBox()` on all 674,641 of them, purely to find the scope's minimum corner for the
base-point offset.

**The cheap version already exists.** `ExporterView.ModelMinCorner(doc)` reads the bounding box of
each **root** item — a handful of COM calls — and is already used by batch export and by the
base-point preview. For scope = whole model the two are mathematically identical.

**Change.**
- Whole-model scope → use the root-box version. One traversal disappears from the most common export.
- Sub-scopes → read the bounding box **during** the collect walk, where every node is already being
  visited, and hand the min corner back with the item list. `ScopeMinCorner` stays for callers that
  hold only a list (batch union, the preview).

**Expected.** Deletes an entire per-element COM pass from the scan phase. S4's scan decomposition
will say exactly how much that was.

**Touch:** `Collect/ItemCollector.cs`, `UI/ExporterView.xaml.cs`.

---

## S2 — Compute each item key once per session, not once per button

**Now.** `ItemCollector.ItemKey` is an O(tree-depth) COM ancestor walk for any item without an
`InstanceGuid`. It is memoised — correctly — but **each caller builds a fresh dictionary**:

| Caller | Cache |
|---|---|
| `PreviewMapping` | new `Dictionary<ModelItem,string>` |
| `RunExport` | another new one |
| `RunBatchExport` | a third |

So Smart setup → look at the preview → Export pays for the same keys three times. The same is true of
set resolution: `BuildSetMaps` runs a model-wide `Search.FindAll` per distinct set, and preview and
export each run the whole thing from zero.

**Change.** One `ScanCache` owned by the view holding (a) the item-key memo and (b) resolved
`SetMaps` keyed by the rule list, invalidated when the active document, the scope selection, or the
rules change. Preview and export then share one computation.

**Invalidation is the whole risk here** — a stale cache silently exports yesterday's mapping. So:
invalidate on document change (identity, not just null), on scope change (every control that feeds
`ResolveScope`), and on any mapping-grid edit. When in doubt, drop it: a lost cache is a lost
optimisation, a stale one is a wrong file.

**Touch:** `UI/ExporterView.xaml.cs`, new `Collect/ScanCache.cs`.

---

## S3 — The property scan needs 1,000 items; stop resolving 674,641 first

**Now.** `ScanFrom` samples with `Spread(items, SampleCap)` — capped at **1,000** items — which is
sensible. But to get that sample it first calls `ResolveScope`, which walks all ~860k tree nodes to
build the complete leaf list. **The scan that fills the mapping dropdowns pays a full-model traversal
to look at 0.15% of it.**

**Change.** A `CollectSample(doc, cap)` that stops walking once it has enough, dividing the budget
across the model roots **and across each root's top-level branches**, so a federation contributes
every discipline and several places within each rather than the walk filling up inside the first one.

**Where the saving actually lands — narrower than it first appears.** Two callers reach the property
scan:

| Caller | Before | After |
|---|---|---|
| "Scan properties" with **nothing selected** | refused: *"select elements first"* | samples the model — the fast path, no resolve at all |
| "Scan properties" with a **selection** | resolves the selection | unchanged (a selection is already the scope the user meant) |
| **Smart setup** | full `ResolveScope` | **unchanged** |

Smart setup keeps the full resolve because its preview reports real mapped/unmapped counts over the
real export scope, and a sampled preview would put an estimate where a fact used to be — the pane was
explicitly fixed once before for measuring something other than what it named. So S3 makes the
standalone scan fast and turns a dead end into an action; it does **not** speed up Smart setup. What
speeds Smart setup up is S1 and S2, which remove two of the three passes its resolve used to feed.

**Touch:** `Collect/ItemCollector.cs`, `UI/ExporterView.xaml.cs`.

---

## S4 — Measure the scan before optimising it further

**Now.** The report gives one `Scan` number covering the tree walk, set resolution, the min-corner
pass and the property scan together. That is enough to know the scan is slow and not enough to know
which part of it is.

**Change.** Decompose it the way the export phase already is:

```
Scan        : 41,200 ms  (tree walk + extents)
  Collect     :   28,400 ms   (861,204 nodes)
  Set resolve :    9,100 ms   (12 set(s))
  Min corner  :      120 ms
  Prop scan   :    3,580 ms   (1,000 sampled)
```

This is the v4 **F0** principle applied to the half of the pipeline that never got it, and it is what
makes S1/S2/S3 verifiable rather than plausible.

**On replacing the tree walk itself.** `CollectFrom` recurses the whole tree touching `IsHidden`,
`Children` and `HasGeometry` on every node — three COM reads × 861k. Navisworks' own `Search`/
`FindAll` runs natively and is usually faster. But the branch-geometry-recovery rule (a
SolidWorks/Inventor part that carries its mesh while hanging reference planes underneath) needs
parent/child context that a flat search does not give, and that rule exists because losing those
parts was a real bug.

So this one **ships as a measurement, not a swap**: the decomposition above, so a real model can say
whether `Collect` is even the dominant term. If it is, the search-based walker is a v6 item with a
count-equality gate — it may only replace the recursive walk on a model where both produce the
identical element set.

**Touch:** `UI/ExporterView.xaml.cs`, `Geometry/ExportTiming.cs`.

---

# Ordering

Cheap and certain first; the two structural items last; the ceiling-breaker last of all because it is
the only one that can be *wrong* rather than merely slow.

| # | Item | Effort | Expected | Risk |
|---|---|---|---|---|
| 1 | **S4** scan decomposition | trivial | measurement | none |
| 2 | **S1** min-corner pass | tiny | a whole COM pass gone | low |
| 3 | **E2** one property pass | small | ~½ of `Prop harvest` | low |
| 4 | **S2** scan caches | small | scan paid once, not 3× | low (invalidation) |
| 5 | **E6** validator scan | small | 5–10× on validation | low |
| 6 | **S3** sample walk | small | scan feels instant | low |
| 7 | **E5** streaming entities | medium | `Geom write` + GC | low (byte-identical) |
| 8 | **E4** folded passes | medium | `Qty` + part of `Other` | medium (quantities must match) |
| 9 | **E3** chunked COM convert | medium | the `COM convert` line | medium (SDK surface) |
| 10 | **E7** producer/consumer | medium | the write share | medium (threading) |
| 11 | **E1** pre-read identity | large | **~4–5× the read** | **high — opt-in, self-verifying** |
| 12 | v4 **S3** native shim | large | 5–10× the read | high |

**Gates.** After 1–6, re-run `wsc` and read the new scan decomposition and `Prop harvest`. After 7–10,
the export phase lines should have moved and `Geometry rd` should be almost the whole of it. Only then
is E1 worth switching on, and only with its verification counters visible.

**Claims stay unspoken until measured** (v3 §5/§9, v4). Nothing in this plan is a promise; every item
is a hypothesis with the report line that confirms or kills it named next to it.
