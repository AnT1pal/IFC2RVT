using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>
    /// Shared geometry plumbing: moving profiles into world space and turning IFC shells into
    /// Revit solids or meshes. Used both by the native builders and by the DirectShape fallback.
    /// </summary>
    public static class GeometryHelper
    {
        /// <summary>Full transform from profile space to Revit world space.</summary>
        public static Transform WorldFrame(ExtrusionInfo extrusion, Transform productTransform)
            => (productTransform ?? Transform.Identity).Multiply(extrusion.Position);

        public static List<CurveLoop> WorldLoops(ProfileReader profiles, ExtrusionInfo extrusion,
                                                 Transform productTransform)
        {
            var loops = profiles.Read(extrusion.Profile);
            if (loops == null || loops.Count == 0) return null;

            var frame = WorldFrame(extrusion, productTransform);
            if (frame.IsIdentity) return loops;

            try
            {
                return loops.Select(l => CurveLoop.CreateViaTransform(l, frame)).ToList();
            }
            catch
            {
                return null;
            }
        }

        public static XYZ WorldDirection(ExtrusionInfo extrusion, Transform productTransform)
        {
            var frame = WorldFrame(extrusion, productTransform);
            var dir = frame.OfVector(extrusion.Direction);
            return dir.GetLength() < 1e-12 ? XYZ.BasisZ : dir.Normalize();
        }

        public static Solid SolidFrom(ProfileReader profiles, ExtrusionInfo extrusion,
                                      Transform productTransform, ElementId materialId = null)
        {
            var loops = WorldLoops(profiles, extrusion, productTransform);
            if (loops == null || extrusion.Depth <= 0) return null;

            var direction = WorldDirection(extrusion, productTransform);

            try
            {
                // A solid built without SolidOptions carries no material and renders as default
                // grey however the element is painted, so pass the material into the geometry.
                if (materialId != null && materialId != ElementId.InvalidElementId)
                {
                    var options = new SolidOptions(materialId, ElementId.InvalidElementId);
                    return GeometryCreationUtilities.CreateExtrusionGeometry(
                        loops, direction, extrusion.Depth, options);
                }

                return GeometryCreationUtilities.CreateExtrusionGeometry(loops, direction, extrusion.Depth);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Oriented extent of a closed loop measured in a frame, returned as
        /// (centre, long axis direction, long extent, short extent), all in that frame.
        /// Used to recover a wall centreline from its footprint when no Axis representation exists.
        /// </summary>
        public static bool LoopExtents(CurveLoop loop, Transform frame,
                                       out XYZ centre, out XYZ longAxis,
                                       out double longExtent, out double shortExtent)
        {
            centre = null; longAxis = null; longExtent = 0; shortExtent = 0;

            var inverse = frame.Inverse;
            var pts = new List<XYZ>();
            foreach (var c in loop)
                foreach (var p in c.Tessellate())
                    pts.Add(inverse.OfPoint(p));

            if (pts.Count < 2) return false;

            double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            double minZ = pts.Min(p => p.Z), maxZ = pts.Max(p => p.Z);

            var dx = maxX - minX;
            var dy = maxY - minY;

            var localCentre = new XYZ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
            centre = frame.OfPoint(localCentre);

            if (dx >= dy)
            {
                longAxis = frame.OfVector(XYZ.BasisX).Normalize();
                longExtent = dx;
                shortExtent = dy;
            }
            else
            {
                longAxis = frame.OfVector(XYZ.BasisY).Normalize();
                longExtent = dy;
                shortExtent = dx;
            }
            return longExtent > 0;
        }

        // ---- shells to meshes ----------------------------------------------------------------

        /// <summary>
        /// Converts a boundary representation into Revit geometry by tessellating its faces.
        /// Advanced B-reps are approximated from their edge curves: curved surfaces come through
        /// faceted rather than exact, which is the honest limit of doing this without an
        /// OpenCascade-class kernel in the process.
        /// </summary>
        public static IList<GeometryObject> TessellateShell(IIfcRepresentationItem item,
                                                            CurveReader curves,
                                                            IfcScale scale,
                                                            Transform worldTransform,
                                                            ElementId materialId,
                                                            out string diagnosis)
        {
            diagnosis = null;
            var polygons = new List<List<XYZ>>();

            switch (item)
            {
                case IIfcFaceBasedSurfaceModel fbsm:
                    foreach (var set in fbsm.FbsmFaces) CollectFaces(set.CfsFaces, curves, polygons);
                    break;

                case IIfcShellBasedSurfaceModel sbsm:
                    foreach (var shell in sbsm.SbsmBoundary) CollectShell(shell, curves, polygons);
                    break;

                case IIfcManifoldSolidBrep brep:
                    CollectFaces(brep.Outer?.CfsFaces, curves, polygons);
                    if (brep is IIfcFacetedBrepWithVoids withVoids)
                        foreach (var v in withVoids.Voids) CollectFaces(v.CfsFaces, curves, polygons);
                    break;

                case IIfcPolygonalFaceSet pfs:
                    CollectPolygonalFaceSet(pfs, scale, polygons);
                    break;

                case IIfcTriangulatedFaceSet tfs:
                    CollectTriangulatedFaceSet(tfs, scale, polygons);
                    break;

                default:
                    diagnosis = "не поддержано: " + IfcHelpers.EntityName(item);
                    return null;
            }

            if (polygons.Count == 0)
            {
                // Distinguish a defect in the source file from a gap in this converter. The sample
                // model contains 313 IfcFacetedBrep whose IfcClosedShell is literally empty -
                // IFCCLOSEDSHELL(()) - and no importer can build anything from those.
                diagnosis = IsEmptyShell(item)
                    ? "пустая оболочка в исходном файле (" + IfcHelpers.EntityName(item) + ")"
                    : "не прочитаны грани: " + IfcHelpers.EntityName(item);
                return null;
            }

            var built = BuildTessellation(polygons, worldTransform, materialId);
            if (built == null) diagnosis = "тесселяция не удалась: " + IfcHelpers.EntityName(item);
            return built;
        }

        /// <summary>True when the item declares a boundary representation that carries no faces.</summary>
        static bool IsEmptyShell(IIfcRepresentationItem item)
        {
            switch (item)
            {
                case IIfcManifoldSolidBrep brep:
                    return brep.Outer == null || !brep.Outer.CfsFaces.Any();
                case IIfcFaceBasedSurfaceModel fbsm:
                    return fbsm.FbsmFaces.All(f => !f.CfsFaces.Any());
                case IIfcShellBasedSurfaceModel sbsm:
                    return !sbsm.SbsmBoundary.Any();
                default:
                    return false;
            }
        }

        static void CollectShell(IIfcShell shell, CurveReader curves, List<List<XYZ>> polygons)
        {
            switch (shell)
            {
                case IIfcClosedShell cs: CollectFaces(cs.CfsFaces, curves, polygons); break;
                case IIfcOpenShell os: CollectFaces(os.CfsFaces, curves, polygons); break;
            }
        }

        static void CollectFaces(IEnumerable<IIfcFace> faces, CurveReader curves, List<List<XYZ>> polygons)
        {
            if (faces == null) return;

            foreach (var face in faces)
            {
                var bounds = face.Bounds.ToList();
                if (bounds.Count == 0) continue;

                // Revit tessellation has no concept of a hole, so each face contributes only its
                // outer boundary; openings arrive separately as cut solids anyway.
                //
                // IfcFaceOuterBound is optional and plenty of exporters emit only plain
                // IfcFaceBound. Treating "not marked outer" as "is a hole" would discard every
                // bound on such a face and lose the whole shell, so fall back to the bound with
                // the most points, which is the outer one in practice.
                var outer = bounds.OfType<IIfcFaceOuterBound>().Cast<IIfcFaceBound>().FirstOrDefault();

                if (outer != null)
                {
                    AddBound(outer, curves, polygons);
                    continue;
                }

                if (bounds.Count == 1)
                {
                    AddBound(bounds[0], curves, polygons);
                    continue;
                }

                IIfcFaceBound widest = null;
                var best = -1;
                foreach (var bound in bounds)
                {
                    var points = LoopPoints(bound.Bound, curves);
                    var count = points?.Count ?? 0;
                    if (count <= best) continue;
                    best = count;
                    widest = bound;
                }

                if (widest != null) AddBound(widest, curves, polygons);
            }
        }

        static void AddBound(IIfcFaceBound bound, CurveReader curves, List<List<XYZ>> polygons)
        {
            var polygon = LoopPoints(bound.Bound, curves);
            if (polygon == null || polygon.Count < 3) return;

            if (!bound.Orientation) polygon.Reverse();
            polygons.Add(polygon);
        }

        static List<XYZ> LoopPoints(IIfcLoop loop, CurveReader curves)
        {
            switch (loop)
            {
                case IIfcPolyLoop poly:
                    return poly.Polygon.Select(curves.Point).ToList();

                case IIfcEdgeLoop edgeLoop:
                {
                    var pts = new List<XYZ>();
                    foreach (var oriented in edgeLoop.EdgeList)
                    {
                        var vertex = (oriented.Orientation ? oriented.EdgeStart : oriented.EdgeEnd) as IIfcVertexPoint;
                        if (vertex?.VertexGeometry is IIfcCartesianPoint cp) pts.Add(curves.Point(cp));
                    }
                    return pts;
                }

                default:
                    return null;
            }
        }

        static void CollectPolygonalFaceSet(IIfcPolygonalFaceSet pfs, IfcScale scale, List<List<XYZ>> polygons)
        {
            var coords = pfs.Coordinates?.CoordList;
            if (coords == null) return;

            // Point lists store plain numbers in file units, unlike IfcCartesianPoint entities.
            var scaled = coords
                .Select(c => new XYZ(c[0] * scale.Length, c[1] * scale.Length, c[2] * scale.Length))
                .ToList();

            foreach (var face in pfs.Faces)
            {
                var indices = face.CoordIndex.Select(i => (int)i).ToList();
                var polygon = indices
                    .Where(i => i >= 1 && i <= scaled.Count)
                    .Select(i => scaled[i - 1])
                    .ToList();
                if (polygon.Count >= 3) polygons.Add(polygon);
            }
        }

        static void CollectTriangulatedFaceSet(IIfcTriangulatedFaceSet tfs, IfcScale scale, List<List<XYZ>> polygons)
        {
            var coords = tfs.Coordinates?.CoordList;
            if (coords == null) return;

            var scaled = coords
                .Select(c => new XYZ(c[0] * scale.Length, c[1] * scale.Length, c[2] * scale.Length))
                .ToList();

            foreach (var triangle in tfs.CoordIndex)
            {
                var idx = triangle.Select(i => (int)i).ToList();
                if (idx.Count < 3) continue;
                if (idx.Any(i => i < 1 || i > scaled.Count)) continue;
                polygons.Add(new List<XYZ> { scaled[idx[0] - 1], scaled[idx[1] - 1], scaled[idx[2] - 1] });
            }
        }

        static IList<GeometryObject> BuildTessellation(List<List<XYZ>> polygons, Transform worldTransform,
                                                       ElementId materialId)
        {
            try
            {
                var builder = new TessellatedShapeBuilder { Fallback = TessellatedShapeBuilderFallback.Mesh };
                builder.OpenConnectedFaceSet(false);

                var t = worldTransform ?? Transform.Identity;
                var identity = t.IsIdentity;
                int added = 0;

                foreach (var polygon in polygons)
                {
                    var pts = identity ? polygon : polygon.Select(t.OfPoint).ToList();
                    var cleaned = RemoveDuplicates(pts);
                    if (cleaned.Count < 3) continue;

                    builder.AddFace(new TessellatedFace(cleaned, materialId));
                    added++;
                }

                builder.CloseConnectedFaceSet();
                if (added == 0) return null;

                builder.Target = TessellatedShapeBuilderTarget.AnyGeometry;
                builder.Build();

                var result = builder.GetBuildResult();
                var objects = result.GetGeometricalObjects();
                return objects != null && objects.Count > 0 ? objects : null;
            }
            catch
            {
                return null;
            }
        }

        static List<XYZ> RemoveDuplicates(List<XYZ> pts)
        {
            const double tolerance = 1e-7;
            var result = new List<XYZ>();
            foreach (var p in pts)
            {
                if (result.Count > 0 && result[result.Count - 1].DistanceTo(p) < tolerance) continue;
                result.Add(p);
            }
            while (result.Count > 1 && result[0].DistanceTo(result[result.Count - 1]) < tolerance)
                result.RemoveAt(result.Count - 1);
            return result;
        }
    }
}
