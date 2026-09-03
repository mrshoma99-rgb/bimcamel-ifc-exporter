using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BIMCamel.Data;
using BIMCamel.Geometry;
using static BIMCamel.Ifc.StreamingStepWriter;

namespace BIMCamel.Ifc
{
    public sealed class ExportSummary
    {
        public int ElementCount, TriangleCount;
        public long FileSizeBytes;
        public IfcSchema Schema;
        public string Path = "";
        public double OffsetX, OffsetY, OffsetZ;
        public bool GeorefWritten;
        public string BasePointMode = "";
        public double RotationDeg;
        public bool Instanced;
        public int UniqueGeometries, InstanceCount;
        public int StoreyCount, TypeCount, MaterialCount, ClassificationCount;
        public bool QuantitiesWritten;
        public int PsetUnique, PsetRefs; // F3 dedup: distinct property sets written vs element→pset assignments
        public readonly List<string> Files = new(); // all output files (≥1; >1 when size-split)
        public int FileCount => Files.Count;

        // ── class-mapping outcome (F12/#12) ────────────────────────────────────────────────
        /// <summary>Elements written as their mapped IFC class.</summary>
        public int MappedCount;
        /// <summary>Elements that fell back to IfcBuildingElementProxy because no rule matched.</summary>
        public int ProxyUnmapped;
        /// <summary>
        /// Elements the user DID map, but which still exported as a proxy because the target class
        /// has no usable IFC2x3 entity. Silent before v0.8.4 — a careful mapping could produce a
        /// file of proxies with no warning anywhere.
        /// </summary>
        public int ProxyDegraded2x3;
        /// <summary>Distinct IFC entity name → element count, for the report breakdown.</summary>
        public readonly Dictionary<string, int> ByEntity = new(StringComparer.Ordinal);

        public int ProxyTotal => ProxyUnmapped + ProxyDegraded2x3;
        public double ProxyPercent => ElementCount > 0 ? 100.0 * ProxyTotal / ElementCount : 0;

        /// <summary>Set when georeferencing was requested but no CRS or survey point was supplied.</summary>
        public bool GeorefSkippedNoData;
        public string CrsName = "";
        public double SurveyE, SurveyN, SurveyH;

        /// <summary>Navisworks sets exported as IfcGroup / IfcSystem / IfcZone.</summary>
        public int GroupCount;

        /// <summary>Comparison against the previous export's manifest, when there was one.</summary>
        public RevisionManifest.Diff? Revision;

        /// <summary>True when a change list was written beside the IFC.</summary>
        public bool ChangesWritten;
    }

    /// <summary>
    /// Phase-1+ exporter. Produces a structured IFC: spatial tree (multi-storey from a level
    /// property), occurrences (typed via set→class mapping) with ObjectType, base quantities
    /// computed from the mesh (IfcElementQuantity), property sets, materials
    /// (IfcRelAssociatesMaterial), and — IFC4 only — type objects (IfcRelDefinesByType) and
    /// classification (IfcRelAssociatesClassification). See IFC_STRUCTURE_NOTES.md.
    /// </summary>
    public static class IfcExporter
    {
        // Collected per emitted occurrence, for post-loop relationship batching.
        private struct Occ { public int Id; public string ClassKey, TypeName, Material, ClassCode, Group; }

        public static ExportSummary Export(
            string basePath, IfcSchema schema, IEnumerable<ElementMesh> elements,
            string author, double unitScale, CoordOptions coords, bool computeQuantities, int coordDecimals,
            (double x, double y, double z) geomMin, SpatialNames? names = null, long splitLimitBytes = 0, ExportSummary? summary = null)
        {
            names ??= new SpatialNames();
            summary ??= new ExportSummary();
            var meshWriter = MakeWriter(schema);
            ComputeOffset(coords, geomMin, out double minX, out double minY, out double minZ);
            var t = new CoordTransform(unitScale, minX, minY, minZ);
            // Only write georeferencing when the user actually supplied some: an IfcMapConversion
            // that says "the origin is at the origin in an unnamed CRS" is noise at best.
            bool georef = coords.WriteGeoref && schema == IfcSchema.Ifc4 && coords.HasGeorefData;
            bool split = splitLimitBytes > 0;
            // One manifest per EXPORT, not per split part — the parts are one deliverable.
            var previous = RevisionManifest.Load(basePath);
            var manifest = new RevisionManifest { Schema = schema == IfcSchema.Ifc4 ? "IFC4" : "IFC2X3", Written = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };

            int part = 1;
            var doc = new Doc(PartPath(basePath, part, split), coordDecimals, schema, coords, minX, minY, minZ, georef, author, names, manifest);
            bool needRoll = false;
            int index = 0;
            foreach (var el in elements)
            {
                index++;
                if (el.Indices.Count == 0) continue;
                // Roll lazily before the next element once the soft limit was crossed — avoids an
                // empty trailing part if the limit trips on the very last element (v-features F2).
                if (needRoll)
                {
                    doc.Finish(schema, computeQuantities, summary);
                    doc = new Doc(PartPath(basePath, ++part, split), coordDecimals, schema, coords, minX, minY, minZ, georef, author, names, manifest);
                    needRoll = false;
                }
                WriteMeshElement(doc, schema, meshWriter, el, t, unitScale, computeQuantities, index);
                if (split && doc.W.BytesWritten >= splitLimitBytes) needRoll = true;
            }
            doc.Finish(schema, computeQuantities, summary);
            SaveManifest(basePath, manifest, previous, summary);
            FillSummaryMeta(summary, schema, minX, minY, minZ, georef, coords, false);
            return summary;
        }

        public static ExportSummary ExportInstanced(
            string basePath, IfcSchema schema, IEnumerable<InstancedElement> elements, string author, double unitScale, CoordOptions coords, bool computeQuantities, int coordDecimals,
            (double x, double y, double z) geomMin, SpatialNames? names = null, long splitLimitBytes = 0, ExportSummary? summary = null)
        {
            names ??= new SpatialNames();
            summary ??= new ExportSummary();
            var meshWriter = MakeWriter(schema);
            var identity = new CoordTransform(1.0, 0, 0, 0);
            ComputeOffset(coords, geomMin, out double minX, out double minY, out double minZ);
            // Only write georeferencing when the user actually supplied some: an IfcMapConversion
            // that says "the origin is at the origin in an unnamed CRS" is noise at best.
            bool georef = coords.WriteGeoref && schema == IfcSchema.Ifc4 && coords.HasGeorefData;
            bool split = splitLimitBytes > 0;
            // One manifest per EXPORT, not per split part — the parts are one deliverable.
            var previous = RevisionManifest.Load(basePath);
            var manifest = new RevisionManifest { Schema = schema == IfcSchema.Ifc4 ? "IFC4" : "IFC2X3", Written = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };

            int part = 1;
            var doc = new Doc(PartPath(basePath, part, split), coordDecimals, schema, coords, minX, minY, minZ, georef, author, names, manifest);
            bool needRoll = false;
            int index = 0;
            foreach (var el in elements)
            {
                index++;
                if (el.Instances.Count == 0) continue;
                if (needRoll)
                {
                    doc.Finish(schema, computeQuantities, summary);
                    doc = new Doc(PartPath(basePath, ++part, split), coordDecimals, schema, coords, minX, minY, minZ, georef, author, names, manifest);
                    needRoll = false;
                }
                WriteInstancedElement(doc, schema, meshWriter, el, identity, minX, minY, minZ, computeQuantities, index);
                if (split && doc.W.BytesWritten >= splitLimitBytes) needRoll = true;
            }
            doc.Finish(schema, computeQuantities, summary);
            SaveManifest(basePath, manifest, previous, summary);
            FillSummaryMeta(summary, schema, minX, minY, minZ, georef, coords, true);
            return summary;
        }

        // ── per-file session + element writers (shared by single, split, and batch export) ──────
        private struct GeomDef { public int RepMapId; public MeshQty Qty; public int Tri; }

        /// <summary>
        /// One output IFC file's writing state. The exporter rolls to a fresh Doc when the soft size
        /// limit is crossed; each Doc is a complete, standalone IFC (own header/skeleton/footer and
        /// its own instancing + pset dedup). Per-file dedup (option C) keeps memory bounded across a
        /// split: geometry that spans a boundary is simply re-emitted in each part.
        /// </summary>
        private sealed class Doc
        {
            public readonly StreamingStepWriter W;
            public readonly SkelBase S;
            public readonly StoreyTable Storeys;
            public readonly Ids Id;
            public readonly RevisionManifest Manifest;
            public readonly List<Occ> Occ = new();
            public readonly Dictionary<int, List<int>> ByStorey = new();
            public readonly PsetDedup Psets = new();
            public readonly Dictionary<(long, long, long), int> DirCache = new();   // instanced: shared IfcDirection
            public readonly Dictionary<DedupKey, GeomDef> Geom = new();             // instanced: per-file dedup
            public int Elem, Tris, Insts, UniqueGeom;
            public int Mapped, ProxyUnmapped, ProxyDegraded;
            public readonly Dictionary<string, int> ByEntity = new(StringComparer.Ordinal);
            private readonly string _path;
            private readonly string _classSystem;
            private readonly string _classSource;
            private readonly string _classEdition;
            private readonly string _classEditionDate;
            private readonly string _classLocation;
            private readonly string _groupEntity;

            public Doc(string path, int coordDecimals, IfcSchema schema, CoordOptions coords,
                       double minX, double minY, double minZ, bool georef, string author, SpatialNames names,
                       RevisionManifest manifest)
            {
                Manifest = manifest;
                _path = path;
                _classSystem = names.ClassificationSystem;
                _classSource = names.ClassificationSource;
                _classEdition = names.ClassificationEdition;
                _classEditionDate = names.ClassificationEditionDate;
                _classLocation = names.ClassificationLocation;
                _groupEntity = names.GroupEntity;
                W = new StreamingStepWriter(path, coordDecimals);
                W.WriteHeader(schema, System.IO.Path.GetFileName(path), author);
                // Salted by PROJECT, deliberately not by file name: a revision exported as
                // "model_P04.ifc" must reuse the ids from "model_P03.ifc" or the diff is worthless.
                // Split parts therefore share their skeleton ids, which is correct — they describe
                // the same project, site, building and storeys.
                Id = new Ids(names.Project);
                S = WriteSkeletonBase(W, Id, schema, coords, minX, minY, minZ, georef, author, names);
                Storeys = new StoreyTable(W, Id, S, names, minZ);
            }

            public void Finish(IfcSchema schema, bool computeQuantities, ExportSummary sum)
            {
                WriteSpatialContainment(W, Id, S.Owner, ByStorey, Storeys);
                WriteDeferredPsetRels(W, Id, S.Owner, Psets, sum);
                FinishRelationships(W, Id, schema, S.Owner, Occ, sum,
                    new ClassId(_classSystem, _classSource, _classEdition, _classEditionDate, _classLocation),
                    _groupEntity);
                Storeys.WriteAggregation();
                W.WriteFooter();
                W.Dispose();
                sum.Files.Add(_path);
                sum.ElementCount += Elem; sum.TriangleCount += Tris;
                sum.InstanceCount += Insts; sum.UniqueGeometries += UniqueGeom;
                sum.MappedCount += Mapped; sum.ProxyUnmapped += ProxyUnmapped; sum.ProxyDegraded2x3 += ProxyDegraded;
                foreach (var kv in ByEntity) { sum.ByEntity.TryGetValue(kv.Key, out int n); sum.ByEntity[kv.Key] = n + kv.Value; }
                if (Storeys.Count > sum.StoreyCount) sum.StoreyCount = Storeys.Count;
                if (computeQuantities && Elem > 0) sum.QuantitiesWritten = true;
                try { sum.FileSizeBytes += new System.IO.FileInfo(_path).Length; } catch { }
            }
        }

        private static void WriteMeshElement(Doc d, IfcSchema schema, IMeshWriter mw, ElementMesh el, CoordTransform t, double unitScale, bool computeQuantities, int index)
        {
            var w = d.W;
            var (storeyId, storeyPlace, storeyAxis) = d.Storeys.Get(el.Level);
            long tg = ExportTiming.Now;
            int item = mw.WriteMesh(w, el, t);
            ExportTiming.GeomWriteTicks += ExportTiming.Now - tg;
            if (el.Material != null) WriteStyle(w, schema, item, el.Material);
            int rep = WriteShapeRep(w, d.S.Ctx, mw.RepresentationType, item);
            int prodShape = WriteProdShape(w, rep);
            // storeyAxis cancels the storey's own elevation, so raising a storey to its real height
            // repositions the storey without moving the geometry hanging off it.
            int place = WriteLocalPlacement(w, storeyPlace, storeyAxis);
            string guid = StableGuid(el.InstanceGuid, el.Name, index);
            int id = WriteElement(d, w, schema, el.ClassKey, el.TypeName, guid, d.S.Owner, el.Name, place, prodShape);
            long tp = ExportTiming.Now;
            if (el.Properties != null && el.Properties.Count > 0) RegisterPropertySets(w, d.Id, d.S.Owner, id, el.Properties, d.Psets);
            ExportTiming.PropWriteTicks += ExportTiming.Now - tp;
            if (computeQuantities)
            {
                long tq = ExportTiming.Now;
                // The extractor already accumulated these while welding (v5 E4), so the mesh is
                // not walked again here; Quantities is null only when nothing welded it.
                var q = el.Quantities ?? MeshQuantities.Compute(el.Vertices, el.Indices, unitScale);
                WriteQuantities(w, d.Id, schema, d.S.Owner, id, q, el.ClassKey, guid);
                ExportTiming.QtyWriteTicks += ExportTiming.Now - tq;
            }
            if (!d.ByStorey.TryGetValue(storeyId, out var lst)) { lst = new List<int>(); d.ByStorey[storeyId] = lst; }
            lst.Add(id);
            d.Occ.Add(new Occ { Id = id, ClassKey = el.ClassKey ?? "", TypeName = el.TypeName ?? "", Material = el.MaterialName ?? "", ClassCode = el.ClassCode ?? "", Group = el.GroupName ?? "" });

            // Revision signature. Geometry is hashed from the vertices themselves — they are
            // already in memory here, so this is one extra pass over coordinates we have.
            var hm = RevisionManifest.Hasher.Start();
            HashSemantics(ref hm, el.ClassKey, el.TypeName, el.Level, el.MaterialName, el.ClassCode, el.GroupName);
            hm.Add(el.Indices.Count);
            for (int i = 0; i < el.Vertices.Count; i++) hm.Add(el.Vertices[i]);
            d.Manifest.Put(guid, hm.Value, el.Name, el.TypeName);

            d.Tris += el.Indices.Count / 3; d.Elem++;
        }

        private static void WriteInstancedElement(Doc d, IfcSchema schema, IMeshWriter mw, InstancedElement el, CoordTransform identity, double minX, double minY, double minZ, bool computeQuantities, int index)
        {
            var w = d.W;
            // Defence in depth: an instance whose mesh has no triangles would emit an empty
            // IfcCartesianPointList3D / CoordIndex. Both are LIST [1:?], so an empty aggregate
            // makes the whole file schema-invalid and strict readers reject it. The extractor
            // already filters these out; never write one even if a caller slips one through.
            if (el.Instances.All(i => i.Mesh == null || i.Mesh.Indices.Count == 0)) return;

            var (storeyId, storeyPlace, storeyAxis) = d.Storeys.Get(el.Level);
            var mapped = new List<int>(el.Instances.Count);
            // Quantities are accumulated in WORLD space across every instance. Before v0.8.4 the
            // volume/area sums ignored each instance's scale factor and the size came from the
            // FIRST unique geometry's LOCAL box — so an instanced element reported a different
            // size and volume than the very same element exported with instancing off.
            double vol = 0, area = 0;
            var box = new WorldBox();
            foreach (var inst in el.Instances)
            {
                if (inst.Mesh == null || inst.Mesh.Indices.Count == 0) continue;
                if (!d.Geom.TryGetValue(inst.Key, out var gd)) // first sighting IN THIS FILE → define it
                {
                    var lm = inst.Mesh;
                    long tg = ExportTiming.Now;
                    int item = mw.WriteMesh(w, new ElementMesh { Vertices = lm.Vertices, Indices = lm.Indices }, identity);
                    if (lm.Material != null) WriteStyle(w, schema, item, lm.Material); // colour shared by all instances (v4 D)
                    int rep0 = WriteShapeRep(w, d.S.Ctx, mw.RepresentationType, item);
                    int mapOrigin = w.Begin("IFCAXIS2PLACEMENT3D");
                    w.RefTok(d.S.OriginPt); w.Tok(",$,$"); w.End();
                    int repMap = w.Begin("IFCREPRESENTATIONMAP");
                    w.RefTok(mapOrigin); w.Sep(); w.RefTok(rep0); w.End();
                    ExportTiming.GeomWriteTicks += ExportTiming.Now - tg;
                    long tq0 = ExportTiming.Now;
                    // Per UNIQUE geometry (102,384 on the prova), not per instance (683,917) — so
                    // this one stays in the writer rather than folding into the per-fragment weld,
                    // where it would be computed 6.7x more often (v5 E4).
                    var qy = computeQuantities ? MeshQuantities.Compute(lm.Vertices, lm.Indices, 1.0) : default;
                    ExportTiming.QtyWriteTicks += ExportTiming.Now - tq0;
                    gd = new GeomDef { RepMapId = repMap, Qty = qy, Tri = lm.Indices.Count / 3 };
                    d.Geom[inst.Key] = gd; d.UniqueGeom++;
                }
                int cto = WriteTransform(w, inst, minX, minY, minZ, d.DirCache);
                mapped.Add(WriteMappedItem(w, gd.RepMapId, cto));
                if (computeQuantities)
                {
                    double s = InstanceScale(inst.Rotation);
                    vol += gd.Qty.Volume * s * s * s;   // volume scales with the cube,
                    area += gd.Qty.Area * s * s;        // area with the square
                    box.AddTransformed(gd.Qty, inst.Rotation, inst.Translation, minX, minY, minZ);
                }
                d.Tris += gd.Tri; d.Insts++;
            }
            int rep = WriteMappedShapeRep(w, d.S.Ctx, mapped);
            int prodShape = WriteProdShape(w, rep);
            int place = WriteLocalPlacement(w, storeyPlace, storeyAxis);
            string guid = StableGuid(el.InstanceGuid, el.Name, index);
            int id = WriteElement(d, w, schema, el.ClassKey, el.TypeName, guid, d.S.Owner, el.Name, place, prodShape);
            long tp = ExportTiming.Now;
            if (el.Properties != null && el.Properties.Count > 0) RegisterPropertySets(w, d.Id, d.S.Owner, id, el.Properties, d.Psets);
            ExportTiming.PropWriteTicks += ExportTiming.Now - tp;
            if (computeQuantities)
            {
                long tq = ExportTiming.Now;
                WriteQuantities(w, d.Id, schema, d.S.Owner, id,
                    new MeshQty { Volume = vol, Area = area, Dx = box.Dx, Dy = box.Dy, Dz = box.Dz }, el.ClassKey, guid);
                ExportTiming.QtyWriteTicks += ExportTiming.Now - tq;
            }
            if (!d.ByStorey.TryGetValue(storeyId, out var lst)) { lst = new List<int>(); d.ByStorey[storeyId] = lst; }
            lst.Add(id);
            d.Occ.Add(new Occ { Id = id, ClassKey = el.ClassKey ?? "", TypeName = el.TypeName ?? "", Material = el.MaterialName ?? "", ClassCode = el.ClassCode ?? "", Group = el.GroupName ?? "" });

            // Revision signature. Each instance already carries a 128-bit content hash of its
            // mesh, so folding those in with the placements costs nothing and is stronger than
            // any summary statistic would be.
            var hm = RevisionManifest.Hasher.Start();
            HashSemantics(ref hm, el.ClassKey, el.TypeName, el.Level, el.MaterialName, el.ClassCode, el.GroupName);
            foreach (var inst in el.Instances)
            {
                if (inst.Mesh == null || inst.Mesh.Indices.Count == 0) continue;
                hm.Add((long)inst.Key.H0); hm.Add((long)inst.Key.H1); hm.Add(inst.Key.V); hm.Add(inst.Key.T);
                for (int i = 0; i < 9; i++) hm.Add(inst.Rotation[i]);
                for (int i = 0; i < 3; i++) hm.Add(inst.Translation[i]);
            }
            d.Manifest.Put(guid, hm.Value, el.Name, el.TypeName);

            d.Elem++;
        }


        /// <summary>
        /// Writes this export's manifest and, when a previous one was sitting next to the target,
        /// reports what changed. A failure here must never fail the export — the IFC is the
        /// deliverable, the manifest is a convenience.
        /// </summary>
        private static void SaveManifest(string basePath, RevisionManifest manifest, RevisionManifest? previous, ExportSummary summary)
        {
            if (previous != null && previous.Elements.Count > 0)
            {
                var diff = RevisionManifest.Compare(previous, manifest);
                summary.Revision = diff;

                // The change list, beside the IFC. Only when something actually changed: a
                // "model.ifc.changes.csv" holding nothing but a header is a file somebody opens,
                // reads as an error, and asks about.
                if (diff.Changed > 0)
                    summary.ChangesWritten = RevisionManifest.SaveChanges(basePath, diff, previous, manifest);
            }

            try { manifest.Save(basePath); } catch { }
        }


        /// <summary>The semantic half of an element's revision signature — everything we write
        /// about it that is not geometry.</summary>
        private static void HashSemantics(ref RevisionManifest.Hasher h, string? classKey, string? type, string? level, string? material, string? code, string? group)
        {
            h.Add(classKey); h.Add(type); h.Add(level); h.Add(material); h.Add(code); h.Add(group);
        }

        // basePath unchanged when not splitting; else "name_001.ifc", "name_002.ifc", … in the same folder.
        private static string PartPath(string basePath, int part, bool split)
        {
            if (!split) return basePath;
            string dir = System.IO.Path.GetDirectoryName(basePath) ?? "";
            string n = System.IO.Path.GetFileNameWithoutExtension(basePath);
            string e = System.IO.Path.GetExtension(basePath);
            return System.IO.Path.Combine(dir, $"{n}_{part:000}{e}");
        }

        private static void FillSummaryMeta(ExportSummary s, IfcSchema schema, double ox, double oy, double oz, bool georef, CoordOptions c, bool instanced)
        {
            s.Schema = schema; s.Path = s.Files.Count > 0 ? s.Files[0] : "";
            s.OffsetX = ox; s.OffsetY = oy; s.OffsetZ = oz;
            s.GeorefWritten = georef; s.BasePointMode = c.Mode.ToString(); s.RotationDeg = c.RotationDeg; s.Instanced = instanced;
            s.GeorefSkippedNoData = c.WriteGeoref && schema == IfcSchema.Ifc4 && !c.HasGeorefData;
            s.CrsName = c.CrsName; s.SurveyE = c.SurveyEastings; s.SurveyN = c.SurveyNorthings; s.SurveyH = c.SurveyElevation;
        }

        // ── spatial structure ───────────────────────────────────────────────────
        private struct SkelBase { public int Ctx, Owner, Axis, OriginPt, Building, BuildingPlace; }

        private static SkelBase WriteSkeletonBase(StreamingStepWriter w, Ids ids, IfcSchema schema, CoordOptions coords, double minX, double minY, double minZ, bool georef, string author, SpatialNames names)
        {
            int len = w.Write("IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)");
            int area = w.Write("IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.)");
            int vol = w.Write("IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.)");
            int ang = w.Write("IFCSIUNIT(*,.PLANEANGLEUNIT.,$,.RADIAN.)");
            int units = w.Write($"IFCUNITASSIGNMENT(({Ref(len)},{Ref(area)},{Ref(vol)},{Ref(ang)}))");

            int origin = w.Write("IFCCARTESIANPOINT((0.,0.,0.))");
            int axis = w.Write($"IFCAXIS2PLACEMENT3D({Ref(origin)},$,$)");
            int ctx = w.Write($"IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,{R(1e-5)},{Ref(axis)},$)");

            if (georef)
            {
                // IfcMapConversion carries ONLY the survey offset — where the model's world origin
                // sits in the map CRS. The base-point offset is already lifted into the IfcSite
                // placement below, which is what puts the geometry back at its world position;
                // writing that same offset here as well made any consumer honouring both apply the
                // real-world shift a second time.
                string crsName = coords.CrsName.Trim().Length > 0 ? Str(coords.CrsName.Trim()) : "'LOCAL'";
                string crsDesc = coords.CrsDescription.Trim().Length > 0 ? Str(coords.CrsDescription.Trim()) : "$";
                int projCrs = w.Write($"IFCPROJECTEDCRS({crsName},{crsDesc},$,$,$,$,$)");
                string xa = "$", xo = "$";
                if (Math.Abs(coords.RotationDeg) > 1e-9) { double rad = coords.RotationDeg * Math.PI / 180.0; xa = R(Math.Cos(rad)); xo = R(Math.Sin(rad)); }
                w.Write($"IFCMAPCONVERSION({Ref(ctx)},{Ref(projCrs)},{R(coords.SurveyEastings)},{R(coords.SurveyNorthings)},{R(coords.SurveyElevation)},{xa},{xo},$)");
            }

            int person = w.Write($"IFCPERSON($,$,{Str(author)},$,$,$,$,$)");
            int org = w.Write("IFCORGANIZATION($,'BIMCamel',$,$,$)");
            int pao = w.Write($"IFCPERSONANDORGANIZATION({Ref(person)},{Ref(org)},$)");
            int app = w.Write($"IFCAPPLICATION({Ref(org)},'0.1','BIMCamel IFC Exporter','BIMCamel')");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // IFC4's CorrectChangeAction rule forbids .ADDED. without a LastModifiedDate, but
            // IFC2x3 keeps .ADDED. — its IfcChangeActionEnum has no NOTDEFINED member.
            string change = schema == IfcSchema.Ifc4 ? ".NOTDEFINED." : ".ADDED.";
            int owner = w.Write($"IFCOWNERHISTORY({Ref(pao)},{Ref(app)},$,{change},$,$,$,{ts})");

            int proj = w.Write($"IFCPROJECT({ids.G("project:" + names.Project)},{Ref(owner)},{Str(names.Project)},$,$,$,$,({Ref(ctx)}),{Ref(units)})");
            int sitePt = w.Write($"IFCCARTESIANPOINT(({R(minX)},{R(minY)},{R(minZ)}))");
            int siteAxis = w.Write($"IFCAXIS2PLACEMENT3D({Ref(sitePt)},$,$)");
            int sitePlace = w.Write($"IFCLOCALPLACEMENT($,{Ref(siteAxis)})");
            int site = w.Write($"IFCSITE({ids.G("site:" + names.Site)},{Ref(owner)},{Str(names.Site)},$,$,{Ref(sitePlace)},$,$,.ELEMENT.,$,$,$,$,$)");
            int bldgPlace = w.Write($"IFCLOCALPLACEMENT({Ref(sitePlace)},{Ref(axis)})");
            int bldg = w.Write($"IFCBUILDING({ids.G("building:" + names.Building)},{Ref(owner)},{Str(names.Building)},$,$,{Ref(bldgPlace)},$,$,.ELEMENT.,$,$,$)");

            w.Write($"IFCRELAGGREGATES({ids.G("agg:project-site")},{Ref(owner)},$,$,{Ref(proj)},({Ref(site)}))");
            w.Write($"IFCRELAGGREGATES({ids.G("agg:site-building")},{Ref(owner)},$,$,{Ref(site)},({Ref(bldg)}))");

            return new SkelBase { Ctx = ctx, Owner = owner, Axis = axis, OriginPt = origin, Building = bldg, BuildingPlace = bldgPlace };
        }

        /// <summary>
        /// Lazily emits IfcBuildingStorey entities as levels are first encountered during streaming
        /// (we no longer know all levels up front — v3 Part A4). A default storey is created eagerly
        /// for level-less elements; the Building→storeys IfcRelAggregates is deferred to
        /// <see cref="WriteAggregation"/> once every storey has been seen (STEP allows any ref order).
        /// </summary>
        private sealed class StoreyTable
        {
            private readonly StreamingStepWriter _w;
            private readonly Ids _ids;
            private readonly SkelBase _s;
            private readonly IReadOnlyDictionary<string, double>? _elev;
            private readonly double _zOffset;
            private readonly Dictionary<string, (int storey, int place, int axis)> _map = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<int> _order = new();
            private readonly Dictionary<int, string> _names = new();   // storey entity id → its name

            public StoreyTable(StreamingStepWriter w, Ids ids, SkelBase s, SpatialNames names, double zOffset)
            {
                _w = w; _ids = ids; _s = s; _elev = names.LevelElevations; _zOffset = zOffset;
                _map[""] = Make(names.Storey, 0.0);
            }

            /// <summary>Storey name for an entity id, so relationships can be keyed by something
            /// stable instead of by a line number that shifts whenever anything above it changes.</summary>
            public string NameOf(int storeyId) => _names.TryGetValue(storeyId, out var n) ? n : storeyId.ToString();

            /// <summary>
            /// Emits one storey at <paramref name="elevLocal"/> metres above the building origin,
            /// and returns the axis that element placements must hang off. Element geometry is
            /// absolute (world − base point), so a storey that is actually raised has to be
            /// cancelled on its children or every element in it would float up by the same amount.
            /// The compensating axis is shared by the whole storey — one extra point + placement
            /// per storey, none per element.
            /// </summary>
            private (int storey, int place, int axis) Make(string name, double elevLocal)
            {
                int place, axis;
                if (Math.Abs(elevLocal) < 1e-9)
                {
                    place = _w.Write($"IFCLOCALPLACEMENT({Ref(_s.BuildingPlace)},{Ref(_s.Axis)})");
                    axis = _s.Axis;
                }
                else
                {
                    int pt = _w.Write($"IFCCARTESIANPOINT((0.,0.,{R(elevLocal)}))");
                    int ax = _w.Write($"IFCAXIS2PLACEMENT3D({Ref(pt)},$,$)");
                    place = _w.Write($"IFCLOCALPLACEMENT({Ref(_s.BuildingPlace)},{Ref(ax)})");
                    int cpt = _w.Write($"IFCCARTESIANPOINT((0.,0.,{R(-elevLocal)}))");
                    axis = _w.Write($"IFCAXIS2PLACEMENT3D({Ref(cpt)},$,$)");
                }
                int st = _w.Write($"IFCBUILDINGSTOREY({_ids.G("storey:" + name)},{Ref(_s.Owner)},{Str(name)},$,$,{Ref(place)},$,$,.ELEMENT.,{R(elevLocal)})");
                _order.Add(st); _names[st] = name;
                return (st, place, axis);
            }

            public (int storey, int place, int axis) Get(string? level)
            {
                var l = (level ?? "").Trim();
                if (l.Length == 0) return _map[""];
                if (_map.TryGetValue(l, out var sp)) return sp;
                // Real elevation when the model's grid levels name this storey; 0 otherwise.
                double elevLocal = 0;
                if (_elev != null && _elev.TryGetValue(l, out double world)) elevLocal = world - _zOffset;
                var r = Make(l, elevLocal);
                _map[l] = r;
                return r;
            }

            public int Count => _order.Count;

            public void WriteAggregation()
            {
                var sb = new StringBuilder();
                for (int i = 0; i < _order.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Ref(_order[i])); }
                _w.Write($"IFCRELAGGREGATES({_ids.G("agg:building-storeys")},{Ref(_s.Owner)},$,$,{Ref(_s.Building)},({sb}))");
            }
        }

        private static void WriteSpatialContainment(StreamingStepWriter w, Ids ids, int owner, Dictionary<int, List<int>> byStorey, StoreyTable storeys)
        {
            foreach (var kv in byStorey)
            {
                if (kv.Value.Count == 0) continue;
                w.Write($"IFCRELCONTAINEDINSPATIALSTRUCTURE({ids.G("contain:" + storeys.NameOf(kv.Key))},{Ref(owner)},$,$,({Join(kv.Value)}),{Ref(kv.Key)})");
            }
        }

        // ── post-loop relationship batches: types (IFC4), materials, classification (IFC4) ──
        /// <summary>
        /// Who publishes the classification, which edition, and where it lives.
        ///
        /// Passed as one thing rather than as five more positional strings, because five adjacent
        /// string parameters is a call nobody can read and a swap nobody would notice.
        /// </summary>
        private readonly struct ClassId
        {
            public ClassId(string? system, string? source, string? edition, string? editionDate, string? location)
            {
                SystemName = (system ?? "").Trim();
                Source = (source ?? "").Trim();
                Edition = (edition ?? "").Trim();
                EditionDate = (editionDate ?? "").Trim();
                Location = (location ?? "").Trim();
            }

            public string SystemName { get; }
            public string Source { get; }
            public string Edition { get; }
            public string EditionDate { get; }
            public string Location { get; }

            /// <summary>The system's name, or a label that does not claim to be one.</summary>
            public string Name => SystemName.Length > 0 ? SystemName : "Source classification";

            /// <summary>
            /// Who published it. Falls back to the system's own name, never to this exporter's.
            ///
            /// IFC2x3 makes Source mandatory, and the old fallback put 'BIMCamel' there — a file
            /// stating that this exporter publishes Uniclass. "Uniclass 2015" as its own source is
            /// imprecise; 'BIMCamel' is wrong, and the two are not the same kind of imperfect.
            /// </summary>
            public string SourceOrName => Source.Length > 0 ? Source : Name;

            /// <summary>The edition, or "-" where IFC2x3 demands one and nobody said.</summary>
            public string EditionOrUnstated => Edition.Length > 0 ? Edition : "-";

            /// <summary>
            /// The edition date, only when it is a plain calendar date.
            ///
            /// A malformed IfcDate is worse than an absent one: an optional attribute left out is
            /// valid, where "spring 2015" in a date field makes a strict reader reject the file.
            /// </summary>
            public bool HasEditionDate =>
                DateTime.TryParseExact(EditionDate, "yyyy-MM-dd",
                                       CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        }

        private static void FinishRelationships(StreamingStepWriter w, Ids ids, IfcSchema schema, int owner, List<Occ> occ, ExportSummary summary, ClassId classification, string groupEntity)
        {
            // Type objects (IFC4 only — 2x3 type signatures diverge).
            if (schema == IfcSchema.Ifc4)
            {
                var groups = new Dictionary<string, (string cls, string predef, string type, List<int> ids)>(StringComparer.Ordinal);
                foreach (var o in occ)
                {
                    if (string.IsNullOrEmpty(o.TypeName)) continue;
                    string key = (o.ClassKey ?? "") + "" + o.TypeName;
                    if (!groups.TryGetValue(key, out var g)) { g = (TypeMapping.Friendly(o.ClassKey), TypeMapping.Predef(o.ClassKey), o.TypeName, new List<int>()); groups[key] = g; }
                    g.ids.Add(o.Id);
                }
                foreach (var g in groups.Values)
                {
                    string ent = TypeMapping.TypeEntityFor(g.cls);
                    // PredefinedType is MANDATORY on IFC4 type entities (unlike on the occurrences,
                    // where it is optional) — '$' makes the file schema-invalid and Revit refuses it.
                    string predef = string.IsNullOrEmpty(g.predef) ? ".NOTDEFINED." : "." + g.predef.Trim().ToUpperInvariant() + ".";
                    // The 9 shared IfcElementType attributes, then whatever this type adds. Door and
                    // window types carry a second mandatory enum after PredefinedType; furniture
                    // types put a mandatory AssemblyPlace before it.
                    string tail = TypeMapping.TypeTail4For(g.cls, predef);
                    int typeId = w.Write($"{ent}({ids.G("type:" + ent + ":" + g.type)},{Ref(owner)},{Str(g.type)},$,$,$,$,$,$,{tail})");
                    w.Write($"IFCRELDEFINESBYTYPE({ids.G("reltype:" + ent + ":" + g.type)},{Ref(owner)},$,$,({Join(g.ids)}),{Ref(typeId)})");
                }
                summary.TypeCount += groups.Count;
            }

            // Materials (both schemas).
            var mats = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in occ) if (!string.IsNullOrWhiteSpace(o.Material)) AddTo(mats, o.Material, o.Id);
            foreach (var kv in mats)
            {
                int matId = schema == IfcSchema.Ifc4 ? w.Write($"IFCMATERIAL({Str(kv.Key)},$,$)") : w.Write($"IFCMATERIAL({Str(kv.Key)})");
                w.Write($"IFCRELASSOCIATESMATERIAL({ids.G("relmat:" + kv.Key)},{Ref(owner)},$,$,({Join(kv.Value)}),{Ref(matId)})");
            }
            summary.MaterialCount += mats.Count;

            // Classification — now in BOTH schemas. The entity signatures genuinely do diverge, so
            // each is written to its own shape rather than skipping 2x3 altogether (which left a
            // 2x3 deliverable silently carrying no classification at all).
            {
                var codes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in occ) if (!string.IsNullOrWhiteSpace(o.ClassCode)) AddTo(codes, o.ClassCode, o.Id);
                if (codes.Count > 0)
                {
                    var c = classification;

                    // Optional in IFC4, mandatory in 2x3, and either way a claim about somebody
                    // else's standard. What goes here now is what the person exporting said
                    // publishes it — never this exporter's own name. See ClassId.SourceOrName.
                    string src = Str(c.SourceOrName);
                    string edition = c.Edition.Length > 0 ? Str(c.Edition) : "$";
                    string editionDate = c.HasEditionDate ? Str(c.EditionDate) : "$";

                    string location = c.Location.Length > 0 ? Str(c.Location) : "$";

                    int source = schema == IfcSchema.Ifc4
                        // IFC4: Source, Edition, EditionDate, Name, Description, Location, ReferenceTokens
                        ? w.Write($"IFCCLASSIFICATION({src},{edition},{editionDate},{Str(c.Name)},$,{location},$)")
                        // IFC2x3: Source, Edition, EditionDate, Name — Source and Edition are
                        // MANDATORY, so an unstated edition is written as "-" rather than omitted:
                        // a missing mandatory attribute makes the file schema-invalid, and Location
                        // does not exist in 2x3 at all.
                        : w.Write($"IFCCLASSIFICATION({src},{Str(c.EditionOrUnstated)},{editionDate},{Str(c.Name)})");
                    foreach (var kv in codes)
                    {
                        int refId = schema == IfcSchema.Ifc4
                            // IFC4: Location, Identification, Name, ReferencedSource, Description, Sort
                            ? w.Write($"IFCCLASSIFICATIONREFERENCE($,{Str(kv.Key)},{Str(kv.Key)},{Ref(source)},$,$)")
                            // IFC2x3: Location, ItemReference, Name, ReferencedSource
                            : w.Write($"IFCCLASSIFICATIONREFERENCE($,{Str(kv.Key)},{Str(kv.Key)},{Ref(source)})");
                        w.Write($"IFCRELASSOCIATESCLASSIFICATION({ids.G("relclass:" + kv.Key)},{Ref(owner)},$,$,({Join(kv.Value)}),{Ref(refId)})");
                    }
                }
                summary.ClassificationCount += codes.Count;
            }

            // Groups / systems / zones from Navisworks sets. A saved or search set is exactly the
            // membership IfcRelAssignsToGroup wants, so this is the one commonly-expected
            // relationship we can reconstruct honestly rather than infer.
            if (groupEntity.Length > 0)
            {
                var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                foreach (var o in occ) if (!string.IsNullOrWhiteSpace(o.Group)) AddTo(groups, o.Group, o.Id);
                foreach (var kv in groups)
                {
                    // IfcGroup and IfcSystem are 5 attributes; IFC4's IfcZone adds LongName.
                    string tail = groupEntity == "IFCZONE" && schema == IfcSchema.Ifc4 ? ",$" : "";
                    int gid = w.Write($"{groupEntity}({ids.G("group:" + kv.Key)},{Ref(owner)},{Str(kv.Key)},$,${tail})");
                    w.Write($"IFCRELASSIGNSTOGROUP({ids.G("relgroup:" + kv.Key)},{Ref(owner)},$,$,({Join(kv.Value)}),$,{Ref(gid)})");
                }
                summary.GroupCount += groups.Count;
            }
        }

        // ── quantities ───────────────────────────────────────────────────────────
        /// <summary>
        /// Writes the element's base quantities into its class's standard buildingSMART set
        /// (Qto_WallBaseQuantities, Qto_SlabBaseQuantities…) rather than one fixed non-standard
        /// name, so a takeoff consumer keying on the standard sets actually matches.
        ///
        /// Length/Width/Height are derived from the world bounding box: Height is the vertical
        /// extent and Length/Width are the longer and shorter plan extents. That is a real
        /// improvement on "Length = longest edge of the box" but it is still a bounding box, not a
        /// parametric dimension — a diagonal brace reports its box, not its length.
        /// </summary>
        private static void WriteQuantities(StreamingStepWriter w, Ids ids, IfcSchema schema, int owner, int occId, MeshQty q, string? classKey, string elemGuid)
        {
            string f = schema == IfcSchema.Ifc4 ? ",$" : ""; // IFC4 IfcQuantity* has a trailing Formula
            int qv = w.Write($"IFCQUANTITYVOLUME('NetVolume',$,$,{R(q.Volume)}{f})");
            int qa = w.Write($"IFCQUANTITYAREA('NetSurfaceArea',$,$,{R(q.Area)}{f})");
            int ql = w.Write($"IFCQUANTITYLENGTH('Length',$,$,{R(q.Length)}{f})");
            int qw = w.Write($"IFCQUANTITYLENGTH('Width',$,$,{R(q.Width)}{f})");
            int qh = w.Write($"IFCQUANTITYLENGTH('Height',$,$,{R(q.Height)}{f})");
            string set = TypeMapping.QtoSetFor(TypeMapping.Friendly(classKey));
            int eq = w.Write($"IFCELEMENTQUANTITY({ids.G("qty:" + elemGuid)},{Ref(owner)},{Str(set)},$,$,({Ref(qv)},{Ref(qa)},{Ref(ql)},{Ref(qw)},{Ref(qh)}))");
            w.Write($"IFCRELDEFINESBYPROPERTIES({ids.G("relqty:" + elemGuid)},{Ref(owner)},$,$,({Ref(occId)}),{Ref(eq)})");
        }

        /// <summary>
        /// Uniform scale carried by an instance's 3x3 matrix — the mean column length, matching
        /// what <see cref="WriteTransform"/> puts on the IfcCartesianTransformationOperator3D.
        /// </summary>
        private static double InstanceScale(double[] rot)
        {
            double sx = Math.Sqrt(rot[0] * rot[0] + rot[1] * rot[1] + rot[2] * rot[2]);
            double sy = Math.Sqrt(rot[3] * rot[3] + rot[4] * rot[4] + rot[5] * rot[5]);
            double sz = Math.Sqrt(rot[6] * rot[6] + rot[7] * rot[7] + rot[8] * rot[8]);
            if (sx < 1e-12) sx = 1; if (sy < 1e-12) sy = 1; if (sz < 1e-12) sz = 1;
            double s = (sx + sy + sz) / 3.0;
            return s > 0 && !double.IsNaN(s) ? s : 1.0;
        }

        // ── element entity ───────────────────────────────────────────────────────
        private static int WriteElement(Doc d, StreamingStepWriter w, IfcSchema schema, string? classKey, string typeName, string guid, int owner, string name, int place, int shape)
        {
            string friendly = TypeMapping.Friendly(classKey);
            string predef = TypeMapping.Predef(classKey);
            TypeMapping.IfcClass cls = default;
            bool mapped = friendly.Length > 0 && TypeMapping.Catalog.TryGetValue(friendly, out cls);

            string ent, tail;
            bool typed2x3 = false, isProxy;
            if (schema == IfcSchema.Ifc4)
            {
                // Attribute shape is per-class: most are 8 IfcElement attrs + PredefinedType, but
                // doors, windows, stair flights and piles are not. Tail4For knows which.
                ent = mapped ? cls.Ifc4! : "IFCBUILDINGELEMENTPROXY";
                string p = string.IsNullOrEmpty(predef) ? "$" : "." + predef.Trim().ToUpperInvariant() + ".";
                tail = mapped ? TypeMapping.Tail4For(friendly, p) : p;
                isProxy = !mapped;
            }
            else if (mapped && cls.Ifc2x3!.Length > 0)
            {
                // IFC2x3 generic flow/control class: 8 attrs (control element = 9, optional 9th).
                ent = cls.Ifc2x3; tail = cls.Args2x3 == 9 ? "$" : ""; typed2x3 = true; isProxy = false;
            }
            else
            {
                ent = "IFCBUILDINGELEMENTPROXY"; tail = "$"; // 9th = CompositionType
                isProxy = true;
                // The user mapped this to a real class; 2x3 simply has no usable entity for it.
                // Counted apart from "never mapped" so the report can say which happened.
                if (mapped) d.ProxyDegraded++;
            }

            if (isProxy) { if (!mapped) d.ProxyUnmapped++; } else d.Mapped++;
            d.ByEntity.TryGetValue(ent, out int n); d.ByEntity[ent] = n + 1;

            // ObjectType: keep the user's intended class visible on 2x3 typed elements; else the type name.
            string label = typed2x3 ? friendly : typeName;
            string objType = string.IsNullOrEmpty(label) ? "$" : Str(label);

            string head = $"{ent}('{guid}',{Ref(owner)},{Str(name)},$,{objType},{Ref(place)},{Ref(shape)},$";
            return w.Write(tail.Length == 0 ? head + ")" : head + "," + tail + ")");
        }

        // ── property sets (F3: content dedup) ───────────────────────────────────────
        // Property sets were 52% of the prova file. The same pset content (e.g. type-level
        // categories) recurs across thousands of elements, so we write each *distinct* pset once and,
        // at the end, emit ONE IfcRelDefinesByProperties per pset relating all its objects (RelatedObjects
        // is a SET in both IFC4 and IFC2x3). Element-unique categories (Id, Mark…) simply don't dedup —
        // no worse than before. Memory: a hash→id map + per-pset object-id lists (ints), bounded.
        private sealed class PsetDedup
        {
            public readonly Dictionary<long, int> ByHash = new();
            public readonly Dictionary<int, List<int>> Members = new(); // psetId → object ids
            public readonly Dictionary<int, long> HashOf = new();       // psetId → its content hash
            public readonly List<int> Scratch = new();                  // reused per-pset value ids
            public int Refs;
        }

        private static void RegisterPropertySets(StreamingStepWriter w, Ids ids, int owner, int objId, List<IfcProp> props, PsetDedup d)
        {
            var groups = new Dictionary<string, List<IfcProp>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var p in props)
            {
                if (!groups.TryGetValue(p.Pset, out var l)) { l = new List<IfcProp>(); groups[p.Pset] = l; order.Add(p.Pset); }
                l.Add(p);
            }
            foreach (var pset in order)
            {
                var list = groups[pset];
                long h = HashPset(pset, list);
                if (!d.ByHash.TryGetValue(h, out int psetId))
                {
                    // Stream every entity token-by-token — no per-entity string interpolation,
                    // no Str()/Nominal() allocations (this path runs millions of times).
                    var valueIds = d.Scratch; valueIds.Clear();
                    foreach (var p in list)
                    {
                        int pv = w.Begin("IFCPROPERTYSINGLEVALUE");
                        w.WriteStr(p.Name); w.Sep(); w.Tok("$"); w.Sep(); WriteNominal(w, p); w.Sep(); w.Tok("$");
                        w.End();
                        valueIds.Add(pv);
                    }
                    psetId = w.Begin("IFCPROPERTYSET");
                    w.Tok(ids.G("pset:" + pset + ":" + h.ToString("x"))); w.Sep(); w.RefTok(owner); w.Sep(); w.WriteStr(pset); w.Sep(); w.Tok("$"); w.Sep();
                    w.Tok("(");
                    for (int i = 0; i < valueIds.Count; i++) { if (i > 0) w.Sep(); w.RefTok(valueIds[i]); }
                    w.Tok(")");
                    w.End();
                    d.ByHash[h] = psetId; d.HashOf[psetId] = h;
                }
                if (!d.Members.TryGetValue(psetId, out var mem)) { mem = new List<int>(); d.Members[psetId] = mem; }
                mem.Add(objId);
                d.Refs++;
            }
        }

        // Streams the typed nominal value (matches the previous Nominal() output exactly).
        private static void WriteNominal(StreamingStepWriter w, IfcProp p)
        {
            switch (p.Kind)
            {
                case PropKind.Boolean: w.Tok("IFCBOOLEAN(."); w.Tok(p.Value); w.Tok(".)"); break;
                case PropKind.Real: w.Tok("IFCREAL("); w.Tok(p.Value); w.Tok(")"); break;
                case PropKind.Integer: w.Tok("IFCINTEGER("); w.Tok(p.Value); w.Tok(")"); break;
                default: w.Tok("IFCTEXT("); w.WriteStr(string.IsNullOrEmpty(p.Value) ? " " : p.Value); w.Tok(")"); break;
            }
        }

        private static void WriteDeferredPsetRels(StreamingStepWriter w, Ids ids, int owner, PsetDedup d, ExportSummary summary)
        {
            foreach (var kv in d.Members)
                w.Write($"IFCRELDEFINESBYPROPERTIES({ids.G("relpset:" + (d.HashOf.TryGetValue(kv.Key, out var ph) ? ph.ToString("x") : kv.Key.ToString()))},{Ref(owner)},$,$,({Join(kv.Value)}),{Ref(kv.Key)})");
            summary.PsetUnique += d.ByHash.Count;
            summary.PsetRefs += d.Refs;
        }

        // FNV-1a 64-bit over the pset name + each property's (name, kind, value). A 64-bit collision
        // among a few hundred-thousand distinct psets is negligible.
        private static long HashPset(string pset, List<IfcProp> props)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                void Mix(string? s)
                {
                    if (s != null) foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
                    h ^= 0x1FUL; h *= 1099511628211UL; // field separator
                }
                Mix(pset);
                foreach (var p in props) { Mix(p.Name); h ^= (ulong)p.Kind; h *= 1099511628211UL; Mix(p.Value); }
                return (long)h;
            }
        }

        // ── material colour style ──────────────────────────────────────────────────
        private static void WriteStyle(StreamingStepWriter w, IfcSchema schema, int item, Material m)
        {
            int col = w.Write($"IFCCOLOURRGB($,{R(m.R)},{R(m.G)},{R(m.B)})");
            int rend = w.Write($"IFCSURFACESTYLERENDERING({Ref(col)},{R(m.Transparency)},$,$,$,$,$,$,.NOTDEFINED.)");
            int style = w.Write($"IFCSURFACESTYLE('colour',.BOTH.,({Ref(rend)}))");
            if (schema == IfcSchema.Ifc4) w.Write($"IFCSTYLEDITEM({Ref(item)},({Ref(style)}),$)");
            else { int psa = w.Write($"IFCPRESENTATIONSTYLEASSIGNMENT(({Ref(style)}))"); w.Write($"IFCSTYLEDITEM({Ref(item)},({Ref(psa)}),$)"); }
        }

        // ── instance transform ─────────────────────────────────────────────────────
        private static int WriteTransform(StreamingStepWriter w, MeshInstance inst, double ox, double oy, double oz, Dictionary<(long, long, long), int> dirCache)
        {
            (double[] xd, double sx) = NormAxis(inst.Rotation[0], inst.Rotation[1], inst.Rotation[2], 1, 0, 0);
            (double[] yd, double sy) = NormAxis(inst.Rotation[3], inst.Rotation[4], inst.Rotation[5], 0, 1, 0);
            (double[] zd, double sz) = NormAxis(inst.Rotation[6], inst.Rotation[7], inst.Rotation[8], 0, 0, 1);
            double scale = (sx + sy + sz) / 3.0; if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
            int op = w.Begin("IFCCARTESIANPOINT");
            w.Tok('(');
            w.WriteReal6(inst.Translation[0] - ox); w.Sep();
            w.WriteReal6(inst.Translation[1] - oy); w.Sep();
            w.WriteReal6(inst.Translation[2] - oz);
            w.Tok(')');
            w.End();

            // Axis1/Axis2/Axis3 and Scale are OPTIONAL on IfcCartesianTransformationOperator3D and
            // default to the context axes / 1.0. The common axis-aligned, unit-scale instance can
            // therefore omit all three directions — 3 fewer entities per instance (v4 F1).
            bool identity = IsAxis(xd, 1, 0, 0) && IsAxis(yd, 0, 1, 0) && IsAxis(zd, 0, 0, 1) && Math.Abs(scale - 1.0) < 1e-9;
            if (identity)
            {
                int cid = w.Begin("IFCCARTESIANTRANSFORMATIONOPERATOR3D");
                w.Tok("$,$,"); w.RefTok(op); w.Tok(",$,$");
                w.End();
                return cid;
            }

            // Rotated/scaled: share IfcDirection entities across instances (only a handful of distinct axes).
            int xId = DirId(w, dirCache, xd);
            int yId = DirId(w, dirCache, yd);
            int zId = DirId(w, dirCache, zd);
            string sc = Math.Abs(scale - 1.0) < 1e-9 ? "$" : R(scale);
            return w.Write($"IFCCARTESIANTRANSFORMATIONOPERATOR3D({Ref(xId)},{Ref(yId)},{Ref(op)},{sc},{Ref(zId)})");
        }

        private static bool IsAxis(double[] d, double x, double y, double z)
            => Math.Abs(d[0] - x) < 1e-9 && Math.Abs(d[1] - y) < 1e-9 && Math.Abs(d[2] - z) < 1e-9;

        private static int DirId(StreamingStepWriter w, Dictionary<(long, long, long), int> cache, double[] d)
        {
            var key = ((long)Math.Round(d[0] * 1e6), (long)Math.Round(d[1] * 1e6), (long)Math.Round(d[2] * 1e6));
            if (cache.TryGetValue(key, out int id)) return id;
            id = w.Write($"IFCDIRECTION(({R(d[0])},{R(d[1])},{R(d[2])}))");
            cache[key] = id;
            return id;
        }
        private static (double[] dir, double len) NormAxis(double x, double y, double z, double fx, double fy, double fz)
        {
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len < 1e-12) return (new[] { fx, fy, fz }, 1.0);
            return (new[] { x / len, y / len, z / len }, len);
        }

        // ── per-element / per-instance entities, streamed (v5 E5) ───────────────────
        // These were interpolated strings. They are small, but they repeat once per ELEMENT
        // (674,641) or once per INSTANCE (683,917) on the prova model, so each one was a string
        // allocation — and the transform's point was three double.ToString() calls on top — in the
        // hottest loop in the writer, feeding the GC that the peak-heap line tracks. The writer
        // already had a zero-allocation entity API; the mesh used it and everything around the mesh
        // did not. The emitted bytes are unchanged, which is what makes this safe.

        /// <summary>IFCSHAPEREPRESENTATION(#ctx,'Body','&lt;type&gt;',(#item))</summary>
        private static int WriteShapeRep(StreamingStepWriter w, int ctx, string repType, int item)
        {
            int id = w.Begin("IFCSHAPEREPRESENTATION");
            w.RefTok(ctx); w.Sep(); w.Tok("'Body'"); w.Sep();
            w.Tok('\''); w.Tok(repType); w.Tok('\''); w.Sep();
            w.Tok('('); w.RefTok(item); w.Tok(')');
            w.End();
            return id;
        }

        /// <summary>IFCSHAPEREPRESENTATION(#ctx,'Body','MappedRepresentation',(#a,#b,…)) — the list
        /// is streamed straight from the ids, so the per-element StringBuilder is gone too.</summary>
        private static int WriteMappedShapeRep(StreamingStepWriter w, int ctx, List<int> items)
        {
            int id = w.Begin("IFCSHAPEREPRESENTATION");
            w.RefTok(ctx); w.Sep(); w.Tok("'Body'"); w.Sep(); w.Tok("'MappedRepresentation'"); w.Sep();
            w.Tok('(');
            for (int i = 0; i < items.Count; i++) { if (i > 0) w.Sep(); w.RefTok(items[i]); }
            w.Tok(')');
            w.End();
            return id;
        }

        /// <summary>IFCPRODUCTDEFINITIONSHAPE($,$,(#rep))</summary>
        private static int WriteProdShape(StreamingStepWriter w, int rep)
        {
            int id = w.Begin("IFCPRODUCTDEFINITIONSHAPE");
            w.Tok("$,$,("); w.RefTok(rep); w.Tok(')');
            w.End();
            return id;
        }

        /// <summary>IFCLOCALPLACEMENT(#rel,#axis)</summary>
        private static int WriteLocalPlacement(StreamingStepWriter w, int rel, int axis)
        {
            int id = w.Begin("IFCLOCALPLACEMENT");
            w.RefTok(rel); w.Sep(); w.RefTok(axis);
            w.End();
            return id;
        }

        /// <summary>IFCMAPPEDITEM(#repMap,#operator)</summary>
        private static int WriteMappedItem(StreamingStepWriter w, int repMap, int op)
        {
            int id = w.Begin("IFCMAPPEDITEM");
            w.RefTok(repMap); w.Sep(); w.RefTok(op);
            w.End();
            return id;
        }

        // ── helpers ─────────────────────────────────────────────────────────────────
        private static IMeshWriter MakeWriter(IfcSchema schema) => schema == IfcSchema.Ifc4 ? new Ifc4MeshWriter() : (IMeshWriter)new Ifc2x3MeshWriter();

        // geomMin is the scope's world-space min corner in METRES (caller scales from model units).
        private static void ComputeOffset(CoordOptions c, (double x, double y, double z) geomMin, out double x, out double y, out double z)
        {
            switch (c.Mode)
            {
                case BasePointMode.ModelOrigin: x = y = z = 0; break;
                case BasePointMode.Custom: x = c.CustomEastings; y = c.CustomNorthings; z = c.CustomElevation; break;
                default: x = geomMin.x; y = geomMin.y; z = geomMin.z; break;
            }
        }

        private static string Join(List<int> ids)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Ref(ids[i])); }
            return sb.ToString();
        }
        private static void AddTo(Dictionary<string, List<int>> d, string key, int id)
        {
            if (!d.TryGetValue(key, out var l)) { l = new List<int>(); d[key] = l; }
            l.Add(id);
        }

        private static string StableGuid(Guid instanceGuid, string name, int index)
            => instanceGuid != Guid.Empty ? IfcGuid.ToIfcGuid(instanceGuid) : IfcGuid.ToIfcGuid(DeterministicGuid($"{name}#{index}"));
        private static Guid DeterministicGuid(string key) { using var md5 = MD5.Create(); return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(key))); }

        /// <summary>
        /// Deterministic GlobalIds for everything that is not an element.
        ///
        /// These were all <c>Guid.NewGuid()</c>, so every re-export minted a brand-new identity for
        /// the project, site, building, every storey, every property set, every type object and
        /// every relationship. Element GlobalIds were stable, but a diff of two exports still
        /// showed the entire surrounding structure as changed — which made the "re-exports diff
        /// cleanly" promise true only of the elements themselves.
        ///
        /// Each id is now derived from a stable key describing WHAT the entity is (its name, its
        /// content hash, its subject) rather than when it happened to be written. Keys that
        /// genuinely repeat get a disambiguating ordinal, so ids stay unique within the file while
        /// remaining reproducible across runs.
        /// </summary>
        private sealed class Ids
        {
            private readonly string _salt;
            private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);
            private readonly MD5 _md5 = MD5.Create();   // reused: this runs once per pset/element

            /// <param name="salt">Per-file, so split parts don't collide but re-splitting the same
            /// model the same way reproduces the same ids.</param>
            public Ids(string salt) { _salt = salt; }

            public string G(string key)
            {
                _seen.TryGetValue(key, out int n);
                _seen[key] = n + 1;
                // Explicit separators, or a key seen twice and a literal key ending in "1"
                // could hash to the same id.
                const string SEP = "\u0001";
                string full = n == 0 ? _salt + SEP + key : _salt + SEP + key + SEP + n.ToString();
                var g = new Guid(_md5.ComputeHash(Encoding.UTF8.GetBytes(full)));
                return "'" + IfcGuid.ToIfcGuid(g) + "'";
            }
        }
    }
}
