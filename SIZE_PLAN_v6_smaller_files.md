# BIMCamel v6 — Make the file smaller

> Continuation of `PERF_PLAN_v4_filesize_and_speed.md` (which fixed most of the file) and
> `PERF_PLAN_v5_read_and_scan.md` (which fixed the read and the scan). This plan is about **bytes on
> disk** and nothing else.

---

## 0. The 1.9 GB number is stale — start by re-measuring

The `wsc` prova that produced **2,000,042 KB** was measured *before* v4's file work landed. Three of
its four levers have since shipped:

| v4 item | Status today |
|---|---|
| **F1** drop redundant transform axes | **done** — an axis-aligned unit-scale instance emits `IFCCARTESIANTRANSFORMATIONOPERATOR3D($,$,#pt,$,$)`; rotated ones share cached `IfcDirection`s |
| **F2** optional base quantities | **done** — the "Compute base quantities" checkbox |
| **F3** property-set content dedup | **done** — `PsetDedup`, one `IfcPropertySet` per distinct content, one deferred `IfcRelDefinesByProperties` carrying every object that shares it |
| **F4** type-property hoisting | **half** — `IfcElementType` + `IfcRelDefinesByType` are written, but `HasPropertySets` is `$` |
| **F5** geometry decimation | **not started** |

So nobody knows what the file weighs now, or what dominates it. The tool to find out already exists
and is on by default: **"Size & time breakdown in report"** runs `IfcProfiler`, which streams the
output and prints the top 14 STEP entity types by bytes.

**Gate: re-export `wsc` and read that profile before trusting any estimate below.** Every item here
names the profile line that confirms or kills it.

---

## Z1 — Write `.ifczip` (the largest single lever, and it is not implemented at all)

**Now.** There is no compression anywhere in the exporter. Every export is raw STEP text.

**The opportunity.** IFC has a standard zipped form: an ordinary ZIP containing one `.ifc`,
conventionally named `.ifczip`. It is read **directly**, with no unpacking step, by Revit, Solibri,
ArchiCAD, Navisworks, xBim and IfcOpenShell. And STEP text is close to the most compressible payload
there is — ASCII, enormously repetitive, the same twenty keywords over and over. **Expect 6–12×.**

That is larger than everything else in this plan combined.

**Change.** `StreamingStepWriter` currently takes a *path* and creates its own `StreamWriter`. Give
it a `Stream` constructor, and the zip becomes almost free:

```
FileStream → ZipArchive(Create) → entry.Open() → StreamingStepWriter
```

The uncompressed file is then **never written at all** — so this also removes 1.9 GB of disk I/O
from the export, which helps the time as well as the size. `ZipArchiveMode.Create` streams forward
with no seeking, which is exactly the access pattern the writer already has.

**What else has to move.**
- **Validation and profiling read the file back.** Both need to open the archive's single entry
  instead of the path. One shared `IfcSource.OpenText(path)` helper that returns a `TextReader` over
  either form, chosen by extension — so neither the validator nor the profiler learns about zip.
- **Size splitting** counts uncompressed bytes (`BytesWritten`), which is still the right threshold:
  it is what bounds the writer's own memory and what a reader must inflate. Parts become
  `name_001.ifczip`.
- **`FileSizeBytes`** already comes from `FileInfo` on the written path, so it reports the compressed
  size with no change — and the report should say which it is.
- **A checkbox**, because plenty of downstream scripts still expect a literal `.ifc`.

**Confirms it:** the reported file size, against the same export with the box unticked.

---

## Z2 — Deduplicate base quantities the way F3 dedups property sets

**Now.** `WriteQuantities` emits, per element, **unconditionally and never shared**:

```
IFCQUANTITYVOLUME, IFCQUANTITYAREA, IFCQUANTITYLENGTH ×3,
IFCELEMENTQUANTITY, IFCRELDEFINESBYPROPERTIES        = 7 entities
```

Property sets learned to share in v4 F3. Quantities never did — even though on a model with 6.7×
geometry instancing, most repeated parts have *identical* quantities.

**Change.** The machinery already exists; point it at quantities. Hash `(Qto set name, the five
rounded values)`, keep `hash → IfcElementQuantity id` plus a member list, and write **one deferred
`IfcRelDefinesByProperties` per distinct quantity set** with every element that shares it — exactly
the shape `WriteDeferredPsetRels` already has.

**The honest caveat.** On the instanced path `Dx/Dy/Dz` come from the **world**-space box, so a
*rotated* copy of a part has different dimensions and will not dedup; axis-aligned copies will. The
hit rate is data-dependent, which is why the report gains a `Quantities: X unique / Y refs (×Z
shared)` line mirroring the property-set one rather than a promise.

**Confirms it:** that new line, and `IFCQUANTITYLENGTH` / `IFCELEMENTQUANTITY` in the byte profile.

---

## Z3 — Share type-level properties (the case F3 structurally cannot catch)

**Now.** F3 hashes the **whole property set**. A Revit pset with 20 properties of which 19 are
constant for the type and one is the element Id therefore hashes differently for every element — so
**all 20 are written per element**. v4 named this ("categories carrying per-occurrence values won't
dedup") and moved on. It is the largest remaining metadata lever.

**A correction to the v4 framing.** v4 filed this under F4, "type-property hoisting", as the proper
IFC answer worth doing *after* F3 for extra bytes. That reading is now wrong: F3's deferred
relationship already collapses the relationship rows, so plain F4 — writing type objects and
attaching whole psets — buys idiomatic structure, **not bytes**. The bytes are in **splitting a pset
between the type and the occurrence**, which is a different change.

`IFC_STRUCTURE_NOTES.md` already specifies the target: *"type Psets are shared, occurrence Psets
override"*. The scaffolding is in place — `IfcElementType` and `IfcRelDefinesByType` are written
today, with `HasPropertySets` left as `$`.

**Change — single pass, bounded memory.**
- Keep, per `(type key, pset name)`, a candidate table `property name → (kind, value)`. This is
  bounded by *types × categories* — hundreds or thousands of entries, not one per element.
- The **first** occurrence of a type populates the candidate and writes **nothing** at occurrence
  level for that pset.
- Every later occurrence writes only the properties whose value **differs** from the candidate.
  Those override by name, which is precisely the documented merge rule. An occurrence that matches
  the type entirely writes nothing at all.
- At end of file, the candidates are written as `IfcPropertySet`s and referenced from the matching
  `IfcElementType.HasPropertySets`.

The occurrence remainders still flow through `PsetDedup`, so identical remainders continue to share.

**The failure mode, named rather than hidden.** A later occurrence whose pset has a *different set of
property names* than the candidate cannot be expressed by overriding — IFC has no way for an
occurrence to *remove* a property its type supplies. So such an occurrence writes its **full** pset,
and may additionally **inherit** a type property it did not originally carry.

In practice every occurrence under one Navisworks type node carries the same property *names* and
differs only in values, so this should be zero. "Should be" is not "is", so the exporter **counts
these and prints the count**; zero means the export is exactly faithful, non-zero names the risk.

**Gated three ways**, because it moves data from where a naive reader looks:
IFC4 only (this exporter writes types only for IFC4), only when a Type role is configured, and
**off by default** — v4's F2 defaulted ON to preserve behaviour, and preserving behaviour here means
OFF.

**Confirms it:** `IFCPROPERTYSINGLEVALUE` in the byte profile, and the new name-set-mismatch count.

---

## Z4 — Stop writing empty property values

**Now.** `WriteNominal` emits `IFCTEXT(' ')` for a blank value — a full `IfcPropertySingleValue`
entity carrying one space.

**Change.** A "skip empty properties" option that drops them at harvest time, so they cost nothing
downstream either (they also currently participate in the F3 content hash, making psets *less*
likely to dedup). Default on: an empty property communicates nothing that its absence does not.

---

## Z5 — Do not export Navisworks' own bookkeeping categories by default

**Now.** Every category the scan discovers is ticked. That includes Navisworks' internal `Item`,
`Transform` and `Geometry` categories, which describe the *viewer's* model tree rather than the
building, and which are pure noise in an IFC deliverable.

**Change.** A small default-off list applied when a scan first discovers those names. The user can
tick them back on; anything they have already unticked or ticked stays as they left it, matching the
existing "a proposal must never beat a decision" rule in `ScanFrom`.

---

## Z6 — Say what IFC2x3 costs, before the export rather than after

**Now.** IFC2x3 has no compact tessellation entity, so `Ifc2x3MeshWriter` must emit, **per
triangle**, an `IfcPolyLoop` + `IfcFaceOuterBound` + `IfcFace`, plus one `IfcCartesianPoint` per
vertex — against IFC4's single indexed point list and face set. That is roughly **four entities per
triangle versus a handful per mesh**.

Nothing here is fixable in code; the schema is the schema. But a user choosing 2x3 for compatibility
should be told it can multiply the file size several times over, in the pre-flight panel where the
choice is made — not discover it from a 6 GB file.

---

## Z7 — A minimum-size geometry filter (the honest part of F5)

**Now.** F5 proposed decimation. Two things constrain it: v3 A7 established that we **cannot
re-tessellate** finer than the NWC cache, only coarsen — and the existing weld tolerance, wired to
the quality presets, **already is** a vertex-clustering decimator. A true quadric-edge-collapse
simplifier is a large, risky body of numerical code for a model whose geometry the profile may well
show is not the problem.

**Change instead.** An optional minimum-size filter: skip a fragment whose local bounding box is
smaller than a threshold (default off; a few millimetres when on). Washers, fasteners and
screen-only detail disappear, which cuts the file *and* the read. It is honest about what it does —
it removes objects rather than approximating them — so every dropped fragment is counted and
reported, alongside the existing `CollapsedByWeld` tally.

**True decimation stays out of scope** until the byte profile says geometry actually dominates on a
real model. Saying so here is the point: F5 was never justified by measurement.

---

## Ordering

| # | Item | Effort | Expected | Risk |
|---|---|---|---|---|
| 0 | Re-export and read the byte profile | none | the facts | — |
| 1 | **Z1** `.ifczip` | medium | **6–12× on disk** | low |
| 2 | **Z4 + Z5 + Z6** cheap wins and honesty | small | bytes + fewer surprises | low |
| 3 | **Z2** quantity dedup | small | the `IFCQUANTITY*` share | low |
| 4 | **Z7** minimum-size filter | small | data-dependent | low (opt-in, counted) |
| 5 | **Z3** type-level property sharing | large | the `IFCPROPERTYSINGLEVALUE` share | **medium — opt-in, self-reporting** |

**Claims stay unspoken until measured** (v3 §5/§9, v4, v5). Z1 is the only item here whose magnitude
is predictable without the profile, because it does not depend on what the model contains.
