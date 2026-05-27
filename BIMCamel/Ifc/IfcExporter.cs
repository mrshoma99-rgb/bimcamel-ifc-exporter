using System;
using System.Collections.Generic;
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
        private struct Occ { public int Id; public string ClassKey, TypeName, Material, ClassCode; }

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
            bool georef = coords.WriteGeoref && schema == IfcSchema.Ifc4;
            bool split = splitLimitBytes > 0;

            int part = 1;
            var doc = new Doc(PartPath(basePath, part, split), coordDecimals, schema, coords, minX, minY, minZ, georef, author, names);
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
                    doc = new Doc(PartPath(basePath, ++part, split), coordDecimals, schema, coords, minX, minY, minZ, georef, author, names);
                    needRoll = false;
                }
                WriteMeshElement(doc, schema, meshWriter, el, t, unitScale, computeQuantities, index);
                if (split && doc.W.BytesWritten >= splitLimitBytes) needRoll = true;
            }
            doc.Finish(schema, computeQuantities, summary);
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
            bool georef = coords.WriteGeoref && schema == IfcSchema.Ifc4;
            bool split = splitLimitBytes > 0;

            int part = 1;
            var doc = new Doc(PartPath(basePath, part, split), coordDecimals, schema, coords, minX, minY, minZ, georef, author, names);
            bool needRoll = false;
            int index = 0;
            foreach (var el in elements)
            {
                index++;
                if (el.Instances.Count == 0) continue;
                if (needRoll)
                {
                    doc.Finish(schema, computeQuantities, summary);
                    doc = new Doc(PartPath(basePath, ++part, split), coordDecimals, schema, coords, minX, minY, minZ, georef, author, names);
                    needRoll = false;
                }
                WriteInstancedElement(doc, schema, meshWriter, el, identity, minX, minY, minZ, computeQuantities, index);
                if (split && doc.W.BytesWritten >= splitLimitBytes) needRoll = true;
            }
            doc.Finish(schema, computeQuantities, summary);
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
            public readonly List<Occ> Occ = new();
            public readonly Dictionary<int, List<int>> ByStorey = new();
            public readonly PsetDedup Psets = new();
            public readonly Dictionary<(long, long, long), int> DirCache = new();   // instanced: shared IfcDirection
            public readonly Dictionary<DedupKey, GeomDef> Geom = new();             // instanced: per-file dedup
            public int Elem, Tris, Insts, UniqueGeom;
            private readonly string _path;

            public Doc(string path, int coordDecimals, IfcSchema schema, CoordOptions coords,
                       double minX, double minY, double minZ, bool georef, string author, SpatialNames names)
            {
                _path = path;
                W = new StreamingStepWriter(path, coordDecimals);
                W.WriteHeader(schema, System.IO.Path.GetFileName(path), author);
                S = WriteSkeletonBase(W, coords, minX, minY, minZ, georef, author, names);
                Storeys = new StoreyTable(W, S, names);
            }

            public void Finish(IfcSchema schema, bool computeQuantities, ExportSummary sum)
            {
                WriteSpatialContainment(W, S.Owner, ByStorey);
                WriteDeferredPsetRels(W, S.Owner, Psets, sum);
                FinishRelationships(W, schema, S.Owner, Occ, sum);
                Storeys.WriteAggregation();
                W.WriteFooter();
                W.Dispose();
                sum.Files.Add(_path);
                sum.ElementCount += Elem; sum.TriangleCount += Tris;
                sum.InstanceCount += Insts; sum.UniqueGeometries += UniqueGeom;
                if (Storeys.Count > sum.StoreyCount) sum.StoreyCount = Storeys.Count;
                if (computeQuantities && Elem > 0) sum.QuantitiesWritten = true;
                try { sum.FileSizeBytes += new System.IO.FileInfo(_path).Length; } catch { }
            }
        }

        private static void WriteMeshElement(Doc d, IfcSchema schema, IMeshWriter mw, ElementMesh el, CoordTransform t, double unitScale, bool computeQuantities, int index)
        {
            var w = d.W;
            var (storeyId, storeyPlace) = d.Storeys.Get(el.Level);
            long tg = ExportTiming.Now;
            int item = mw.WriteMesh(w, el, t);
            ExportTiming.GeomWriteTicks += ExportTiming.Now - tg;
            if (el.Material != null) WriteStyle(w, schema, item, el.Material);
            int rep = w.Write($"IFCSHAPEREPRESENTATION({Ref(d.S.Ctx)},'Body','{mw.RepresentationType}',({Ref(item)}))");
            int prodShape = w.Write($"IFCPRODUCTDEFINITIONSHAPE($,$,({Ref(rep)}))");
            int place = w.Write($"IFCLOCALPLACEMENT({Ref(storeyPlace)},{Ref(d.S.Axis)})");
            string guid = StableGuid(el.InstanceGuid, el.Name, index);
            int id = WriteElement(w, schema, el.ClassKey, el.TypeName, guid, d.S.Owner, el.Name, place, prodShape);
            long tp = ExportTiming.Now;
            if (el.Properties != null && el.Properties.Count > 0) RegisterPropertySets(w, d.S.Owner, id, el.Properties, d.Psets);
            ExportTiming.PropWriteTicks += ExportTiming.Now - tp;
            if (computeQuantities)
            {
                long tq = ExportTiming.Now;
                var q = MeshQuantities.Compute(el.Vertices, el.Indices, unitScale);
                WriteQuantities(w, schema, d.S.Owner, id, q);
                ExportTiming.QtyTicks += ExportTiming.Now - tq;
            }
            if (!d.ByStorey.TryGetValue(storeyId, out var lst)) { lst = new List<int>(); d.ByStorey[storeyId] = lst; }
            lst.Add(id);
            d.Occ.Add(new Occ { Id = id, ClassKey = el.ClassKey ?? "", TypeName = el.TypeName ?? "", Material = el.MaterialName ?? "", ClassCode = el.ClassCode ?? "" });
            d.Tris += el.Indices.Count / 3; d.Elem++;
        }

        private static void WriteInstancedElement(Doc d, IfcSchema schema, IMeshWriter mw, InstancedElement el, CoordTransform identity, double minX, double minY, double minZ, bool computeQuantities, int index)
        {
            var w = d.W;
            var (storeyId, storeyPlace) = d.Storeys.Get(el.Level);
            var mapped = new List<int>(el.Instances.Count);
            double vol = 0, area = 0; MeshQty firstQ = default; bool gotQ = false;
            foreach (var inst in el.Instances)
            {
                if (!d.Geom.TryGetValue(inst.Key, out var gd)) // first sighting IN THIS FILE → define it
                {
                    var lm = inst.Mesh;
                    long tg = ExportTiming.Now;
                    int item = mw.WriteMesh(w, new ElementMesh { Vertices = lm.Vertices, Indices = lm.Indices }, identity);
                    if (lm.Material != null) WriteStyle(w, schema, item, lm.Material); // colour shared by all instances (v4 D)
                    int rep0 = w.Write($"IFCSHAPEREPRESENTATION({Ref(d.S.Ctx)},'Body','{mw.RepresentationType}',({Ref(item)}))");
                    int mapOrigin = w.Write($"IFCAXIS2PLACEMENT3D({Ref(d.S.OriginPt)},$,$)");
                    int repMap = w.Write($"IFCREPRESENTATIONMAP({Ref(mapOrigin)},{Ref(rep0)})");
                    ExportTiming.GeomWriteTicks += ExportTiming.Now - tg;
                    long tq0 = ExportTiming.Now;
                    var qy = computeQuantities ? MeshQuantities.Compute(lm.Vertices, lm.Indices, 1.0) : default;
                    ExportTiming.QtyTicks += ExportTiming.Now - tq0;
                    gd = new GeomDef { RepMapId = repMap, Qty = qy, Tri = lm.Indices.Count / 3 };
                    d.Geom[inst.Key] = gd; d.UniqueGeom++;
                }
                int cto = WriteTransform(w, inst, minX, minY, minZ, d.DirCache);
                mapped.Add(w.Write($"IFCMAPPEDITEM({Ref(gd.RepMapId)},{Ref(cto)})"));
                vol += gd.Qty.Volume; area += gd.Qty.Area; if (!gotQ) { firstQ = gd.Qty; gotQ = true; }
                d.Tris += gd.Tri; d.Insts++;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < mapped.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Ref(mapped[i])); }
            int rep = w.Write($"IFCSHAPEREPRESENTATION({Ref(d.S.Ctx)},'Body','MappedRepresentation',({sb}))");
            int prodShape = w.Write($"IFCPRODUCTDEFINITIONSHAPE($,$,({Ref(rep)}))");
            int place = w.Write($"IFCLOCALPLACEMENT({Ref(storeyPlace)},{Ref(d.S.Axis)})");
            string guid = StableGuid(el.InstanceGuid, el.Name, index);
            int id = WriteElement(w, schema, el.ClassKey, el.TypeName, guid, d.S.Owner, el.Name, place, prodShape);
            long tp = ExportTiming.Now;
            if (el.Properties != null && el.Properties.Count > 0) RegisterPropertySets(w, d.S.Owner, id, el.Properties, d.Psets);
            ExportTiming.PropWriteTicks += ExportTiming.Now - tp;
            if (computeQuantities)
            {
                long tq = ExportTiming.Now;
                WriteQuantities(w, schema, d.S.Owner, id, new MeshQty { Volume = vol, Area = area, Dx = firstQ.Dx, Dy = firstQ.Dy, Dz = firstQ.Dz });
                ExportTiming.QtyTicks += ExportTiming.Now - tq;
            }
            if (!d.ByStorey.TryGetValue(storeyId, out var lst)) { lst = new List<int>(); d.ByStorey[storeyId] = lst; }
            lst.Add(id);
            d.Occ.Add(new Occ { Id = id, ClassKey = el.ClassKey ?? "", TypeName = el.TypeName ?? "", Material = el.MaterialName ?? "", ClassCode = el.ClassCode ?? "" });
            d.Elem++;
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
        }

        // ── spatial structure ───────────────────────────────────────────────────
        private struct SkelBase { public int Ctx, Owner, Axis, OriginPt, Building, BuildingPlace; }

        private static SkelBase WriteSkeletonBase(StreamingStepWriter w, CoordOptions coords, double minX, double minY, double minZ, bool georef, string author, SpatialNames names)
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
                int projCrs = w.Write("IFCPROJECTEDCRS('LOCAL','BIMCamel local engineering CRS',$,$,$,$,$)");
                string xa = "$", xo = "$";
                if (Math.Abs(coords.RotationDeg) > 1e-9) { double rad = coords.RotationDeg * Math.PI / 180.0; xa = R(Math.Cos(rad)); xo = R(Math.Sin(rad)); }
                w.Write($"IFCMAPCONVERSION({Ref(ctx)},{Ref(projCrs)},{R(minX)},{R(minY)},{R(minZ)},{xa},{xo},$)");
            }

            int person = w.Write($"IFCPERSON($,$,{Str(author)},$,$,$,$,$)");
            int org = w.Write("IFCORGANIZATION($,'BIMCamel',$,$,$)");
            int pao = w.Write($"IFCPERSONANDORGANIZATION({Ref(person)},{Ref(org)},$)");
            int app = w.Write($"IFCAPPLICATION({Ref(org)},'0.1','BIMCamel IFC Exporter','BIMCamel')");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int owner = w.Write($"IFCOWNERHISTORY({Ref(pao)},{Ref(app)},$,.ADDED.,$,$,$,{ts})");

            int proj = w.Write($"IFCPROJECT({G()},{Ref(owner)},{Str(names.Project)},$,$,$,$,({Ref(ctx)}),{Ref(units)})");
            int sitePt = w.Write($"IFCCARTESIANPOINT(({R(minX)},{R(minY)},{R(minZ)}))");
            int siteAxis = w.Write($"IFCAXIS2PLACEMENT3D({Ref(sitePt)},$,$)");
            int sitePlace = w.Write($"IFCLOCALPLACEMENT($,{Ref(siteAxis)})");
            int site = w.Write($"IFCSITE({G()},{Ref(owner)},{Str(names.Site)},$,$,{Ref(sitePlace)},$,$,.ELEMENT.,$,$,$,$,$)");
            int bldgPlace = w.Write($"IFCLOCALPLACEMENT({Ref(sitePlace)},{Ref(axis)})");
            int bldg = w.Write($"IFCBUILDING({G()},{Ref(owner)},{Str(names.Building)},$,$,{Ref(bldgPlace)},$,$,.ELEMENT.,$,$,$)");

            w.Write($"IFCRELAGGREGATES({G()},{Ref(owner)},$,$,{Ref(proj)},({Ref(site)}))");
            w.Write($"IFCRELAGGREGATES({G()},{Ref(owner)},$,$,{Ref(site)},({Ref(bldg)}))");

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
            private readonly SkelBase _s;
            private readonly Dictionary<string, (int storey, int place)> _map = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<int> _ids = new();

            public StoreyTable(StreamingStepWriter w, SkelBase s, SpatialNames names)
            {
                _w = w; _s = s;
                int defPlace = w.Write($"IFCLOCALPLACEMENT({Ref(s.BuildingPlace)},{Ref(s.Axis)})");
                int defStorey = w.Write($"IFCBUILDINGSTOREY({G()},{Ref(s.Owner)},{Str(names.Storey)},$,$,{Ref(defPlace)},$,$,.ELEMENT.,0.)");
                _ids.Add(defStorey);
                _map[""] = (defStorey, defPlace);
            }

            public (int storey, int place) Get(string? level)
            {
                var l = (level ?? "").Trim();
                if (l.Length == 0) return _map[""];
                if (_map.TryGetValue(l, out var sp)) return sp;
                int place = _w.Write($"IFCLOCALPLACEMENT({Ref(_s.BuildingPlace)},{Ref(_s.Axis)})");
                int st = _w.Write($"IFCBUILDINGSTOREY({G()},{Ref(_s.Owner)},{Str(l)},$,$,{Ref(place)},$,$,.ELEMENT.,0.)");
                _ids.Add(st);
                var r = (st, place); _map[l] = r; return r;
            }

            public int Count => _ids.Count;

            public void WriteAggregation()
            {
                var sb = new StringBuilder();
                for (int i = 0; i < _ids.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Ref(_ids[i])); }
                _w.Write($"IFCRELAGGREGATES({G()},{Ref(_s.Owner)},$,$,{Ref(_s.Building)},({sb}))");
            }
        }

        private static void WriteSpatialContainment(StreamingStepWriter w, int owner, Dictionary<int, List<int>> byStorey)
        {
            foreach (var kv in byStorey)
            {
                if (kv.Value.Count == 0) continue;
                w.Write($"IFCRELCONTAINEDINSPATIALSTRUCTURE({G()},{Ref(owner)},$,$,({Join(kv.Value)}),{Ref(kv.Key)})");
            }
        }

        // ── post-loop relationship batches: types (IFC4), materials, classification (IFC4) ──
        private static void FinishRelationships(StreamingStepWriter w, IfcSchema schema, int owner, List<Occ> occ, ExportSummary summary)
        {
            // Type objects (IFC4 only — 2x3 type signatures diverge).
            if (schema == IfcSchema.Ifc4)
            {
                var groups = new Dictionary<string, (string cls, string type, List<int> ids)>(StringComparer.Ordinal);
                foreach (var o in occ)
                {
                    if (string.IsNullOrEmpty(o.TypeName)) continue;
                    string key = (TypeMapping.Friendly(o.ClassKey)) + "" + o.TypeName;
                    if (!groups.TryGetValue(key, out var g)) { g = (TypeMapping.Friendly(o.ClassKey), o.TypeName, new List<int>()); groups[key] = g; }
                    g.ids.Add(o.Id);
                }
                foreach (var g in groups.Values)
                {
                    string ent = TypeMapping.TypeEntityFor(g.cls);
                    int typeId = w.Write($"{ent}({G()},{Ref(owner)},{Str(g.type)},$,$,$,$,$,$,$)");
                    w.Write($"IFCRELDEFINESBYTYPE({G()},{Ref(owner)},$,$,({Join(g.ids)}),{Ref(typeId)})");
                }
                summary.TypeCount += groups.Count;
            }

            // Materials (both schemas).
            var mats = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in occ) if (!string.IsNullOrWhiteSpace(o.Material)) AddTo(mats, o.Material, o.Id);
            foreach (var kv in mats)
            {
                int matId = schema == IfcSchema.Ifc4 ? w.Write($"IFCMATERIAL({Str(kv.Key)},$,$)") : w.Write($"IFCMATERIAL({Str(kv.Key)})");
                w.Write($"IFCRELASSOCIATESMATERIAL({G()},{Ref(owner)},$,$,({Join(kv.Value)}),{Ref(matId)})");
            }
            summary.MaterialCount += mats.Count;

            // Classification (IFC4 only — 2x3 classification signatures diverge).
            if (schema == IfcSchema.Ifc4)
            {
                var codes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in occ) if (!string.IsNullOrWhiteSpace(o.ClassCode)) AddTo(codes, o.ClassCode, o.Id);
                if (codes.Count > 0)
                {
                    int source = w.Write("IFCCLASSIFICATION($,$,$,'Source classification',$,$,$)");
                    foreach (var kv in codes)
                    {
                        int refId = w.Write($"IFCCLASSIFICATIONREFERENCE($,{Str(kv.Key)},{Str(kv.Key)},{Ref(source)},$,$)");
                        w.Write($"IFCRELASSOCIATESCLASSIFICATION({G()},{Ref(owner)},$,$,({Join(kv.Value)}),{Ref(refId)})");
                    }
                }
                summary.ClassificationCount += codes.Count;
            }
        }

        // ── quantities ───────────────────────────────────────────────────────────
        private static void WriteQuantities(StreamingStepWriter w, IfcSchema schema, int owner, int occId, MeshQty q)
        {
            string f = schema == IfcSchema.Ifc4 ? ",$" : ""; // IFC4 IfcQuantity* has a trailing Formula
            int qv = w.Write($"IFCQUANTITYVOLUME('NetVolume',$,$,{R(q.Volume)}{f})");
            int qa = w.Write($"IFCQUANTITYAREA('NetSurfaceArea',$,$,{R(q.Area)}{f})");
            int ql = w.Write($"IFCQUANTITYLENGTH('Length',$,$,{R(Math.Max(q.Dx, Math.Max(q.Dy, q.Dz)))}{f})");
            int eq = w.Write($"IFCELEMENTQUANTITY({G()},{Ref(owner)},'Qto_BaseQuantities',$,$,({Ref(qv)},{Ref(qa)},{Ref(ql)}))");
            w.Write($"IFCRELDEFINESBYPROPERTIES({G()},{Ref(owner)},$,$,({Ref(occId)}),{Ref(eq)})");
        }

        // ── element entity ───────────────────────────────────────────────────────
        private static int WriteElement(StreamingStepWriter w, IfcSchema schema, string? classKey, string typeName, string guid, int owner, string name, int place, int shape)
        {
            string friendly = TypeMapping.Friendly(classKey);
            string predef = TypeMapping.Predef(classKey);
            TypeMapping.IfcClass cls = default;
            bool mapped = friendly.Length > 0 && TypeMapping.Catalog.TryGetValue(friendly, out cls);

            string ent; int args; string ninth = "$";
            bool typed2x3 = false;
            if (schema == IfcSchema.Ifc4)
            {
                // Every catalogued IFC4 entity = 8 IfcElement attrs + PredefinedType (9th).
                ent = mapped ? cls.Ifc4! : "IFCBUILDINGELEMENTPROXY";
                args = 9;
                if (!string.IsNullOrEmpty(predef)) ninth = "." + predef.Trim().ToUpperInvariant() + ".";
            }
            else if (mapped && cls.Ifc2x3!.Length > 0)
            {
                // IFC2x3 generic flow/control class: 8 attrs (control element = 9, optional 9th).
                ent = cls.Ifc2x3; args = cls.Args2x3; typed2x3 = true;
            }
            else
            {
                ent = "IFCBUILDINGELEMENTPROXY"; args = 9; // 9th = CompositionType ($)
            }

            // ObjectType: keep the user's intended class visible on 2x3 typed elements; else the type name.
            string label = typed2x3 ? friendly : typeName;
            string objType = string.IsNullOrEmpty(label) ? "$" : Str(label);

            if (args == 9)
                return w.Write($"{ent}('{guid}',{Ref(owner)},{Str(name)},$,{objType},{Ref(place)},{Ref(shape)},$,{ninth})");
            return w.Write($"{ent}('{guid}',{Ref(owner)},{Str(name)},$,{objType},{Ref(place)},{Ref(shape)},$)");
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
            public readonly List<int> Scratch = new();                  // reused per-pset value ids
            public int Refs;
        }

        private static void RegisterPropertySets(StreamingStepWriter w, int owner, int objId, List<IfcProp> props, PsetDedup d)
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
                    var ids = d.Scratch; ids.Clear();
                    foreach (var p in list)
                    {
                        int pv = w.Begin("IFCPROPERTYSINGLEVALUE");
                        w.WriteStr(p.Name); w.Sep(); w.Tok("$"); w.Sep(); WriteNominal(w, p); w.Sep(); w.Tok("$");
                        w.End();
                        ids.Add(pv);
                    }
                    psetId = w.Begin("IFCPROPERTYSET");
                    w.Tok(G()); w.Sep(); w.RefTok(owner); w.Sep(); w.WriteStr(pset); w.Sep(); w.Tok("$"); w.Sep();
                    w.Tok("(");
                    for (int i = 0; i < ids.Count; i++) { if (i > 0) w.Sep(); w.RefTok(ids[i]); }
                    w.Tok(")");
                    w.End();
                    d.ByHash[h] = psetId;
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

        private static void WriteDeferredPsetRels(StreamingStepWriter w, int owner, PsetDedup d, ExportSummary summary)
        {
            foreach (var kv in d.Members)
                w.Write($"IFCRELDEFINESBYPROPERTIES({G()},{Ref(owner)},$,$,({Join(kv.Value)}),{Ref(kv.Key)})");
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
            int op = w.Write($"IFCCARTESIANPOINT(({R6(inst.Translation[0] - ox)},{R6(inst.Translation[1] - oy)},{R6(inst.Translation[2] - oz)}))");

            // Axis1/Axis2/Axis3 and Scale are OPTIONAL on IfcCartesianTransformationOperator3D and
            // default to the context axes / 1.0. The common axis-aligned, unit-scale instance can
            // therefore omit all three directions — 3 fewer entities per instance (v4 F1).
            bool identity = IsAxis(xd, 1, 0, 0) && IsAxis(yd, 0, 1, 0) && IsAxis(zd, 0, 0, 1) && Math.Abs(scale - 1.0) < 1e-9;
            if (identity)
                return w.Write($"IFCCARTESIANTRANSFORMATIONOPERATOR3D($,$,{Ref(op)},$,$)");

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
        private static string G() => "'" + IfcGuid.ToIfcGuid(Guid.NewGuid()) + "'";
    }
}
