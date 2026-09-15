using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>
    /// IfcWall to a native Revit Wall.
    ///
    /// The centreline comes from the Axis representation when the exporter wrote one, and is
    /// otherwise recovered from the long axis of the body footprint. Thickness comes from the
    /// material layer set, which is what makes the result a real parametric wall rather than a
    /// solid that merely looks like one.
    /// </summary>
    public class WallBuilder : IElementBuilder
    {
        readonly BuilderContext _ctx;

        public string CategoryName => "Стены";

        public WallBuilder(BuilderContext ctx) => _ctx = ctx;

        public bool CanBuild(IIfcProduct product) => product is IIfcWall;

        public BuildResult Build(IIfcProduct product)
        {
            var wall = (IIfcWall)product;
            var world = _ctx.Ifc.Placements.WorldTransform(wall);
            var extrusion = _ctx.Ifc.Representations.LargestExtrusion(wall);

            var thickness = ThicknessOf(wall, extrusion, world);
            if (thickness <= _ctx.ShortCurveTolerance)
                return BuildResult.Fail("не определена толщина стены");

            var axis = AxisOf(wall, extrusion, world, thickness);
            if (axis == null)
                return BuildResult.Fail("не определена ось стены");

            var baseZ = BaseElevationOf(extrusion, world, axis);

            var height = HeightOf(extrusion, axis, baseZ, out var trimmed);
            var heightFromBody = false;

            // The extrusion is the usual source of height, but it is lost whenever the body is
            // something this reader cannot reduce to a sweep. Thickness and axis survive that, so
            // refusing the wall over height alone throws away a wall we could otherwise build.
            if (height <= _ctx.ShortCurveTolerance)
            {
                if (!FallbackExtent(wall, ref baseZ, out height))
                    return BuildResult.Fail("не определена высота стены");
                heightFromBody = true;
            }
            var level = _ctx.Levels.For(wall, new XYZ(0, 0, baseZ));
            if (level == null) return BuildResult.Fail("нет подходящего уровня");

            var materialId = _ctx.Materials.PrimaryMaterialOf(wall);
            var resolved = _ctx.Types.ResolveWallType(_ctx.TypeNameOf(wall), thickness, materialId);
            var type = resolved.Type;
            if (type == null) return BuildResult.Fail("нет типа стены в проекте");

            try
            {
                // The axis is supplied in world coordinates, so the wall is created flat on the
                // level and the base offset carries the true elevation.
                var flat = FlattenToElevation(axis, level.Elevation);
                if (flat == null) return BuildResult.Fail("вырожденная ось стены");

                var created = Wall.Create(_ctx.Doc, flat, type.Id, level.Id,
                                          height, baseZ - level.Elevation, false, false);
                if (created == null) return BuildResult.Fail("Wall.Create вернул null");

                DisableAutoJoin(created);

                // A clipped body means the IFC wall was cut by a roof or a slope. The height here
                // comes from the base extrusion, before the cut, so the top needs attaching to a
                // reference plane by hand. Flag it rather than let it pass as an exact match.
                return new BuildResult
                {
                    Element = created,
                    Message = Notes(
                        ClipNote(extrusion, trimmed),
                        heightFromBody ? "высота взята из габарита тела - выдавливание не разобрано" : null,
                        resolved.Note)
                };
            }
            catch (Exception ex)
            {
                return BuildResult.Fail("Wall.Create: " + ex.Message);
            }
        }

        // ---- thickness -----------------------------------------------------------------------

        double ThicknessOf(IIfcWall wall, ExtrusionInfo extrusion, Transform world)
        {
            // The layer set is authoritative: it is what the authoring tool actually modelled.
            var layerSet = IfcHelpers.LayerSet(wall);
            var total = IfcHelpers.TotalThickness(layerSet) * _ctx.Ifc.Scale.Length;
            if (total > _ctx.ShortCurveTolerance) return total;

            // Otherwise take the narrow dimension of the footprint.
            if (extrusion?.Profile == null) return 0.0;

            var loops = GeometryHelper.WorldLoops(_ctx.Ifc.Profiles, extrusion, world);
            if (loops == null || loops.Count == 0) return 0.0;

            var frame = GeometryHelper.WorldFrame(extrusion, world);
            return GeometryHelper.LoopExtents(loops[0], frame, out _, out _, out _, out var shortExtent)
                ? shortExtent
                : 0.0;
        }

        // ---- centreline ----------------------------------------------------------------------

        Curve AxisOf(IIfcWall wall, ExtrusionInfo extrusion, Transform world, double thickness)
        {
            var declared = DeclaredAxis(wall, world);
            if (declared != null) return ApplyLayerOffset(wall, declared, thickness);

            return FootprintAxis(extrusion, world);
        }

        Curve DeclaredAxis(IIfcWall wall, Transform world)
        {
            var curves = _ctx.Ifc.Representations.AxisCurves(wall);
            if (curves == null || curves.Count == 0) return null;

            // Revit walls take a single location curve; a multi-segment axis cannot be honoured.
            var longest = curves.OrderByDescending(c => SafeLength(c)).First();
            if (SafeLength(longest) <= _ctx.ShortCurveTolerance) return null;

            return world.IsIdentity ? longest : longest.CreateTransformed(world);
        }

        /// <summary>
        /// IfcMaterialLayerSetUsage positions the layers relative to the axis, so the axis is not
        /// necessarily the centreline. Shift it to the middle of the layer stack.
        ///
        /// Per IFC the layer set base sits at OffsetFromReferenceLine along the positive layer-set
        /// direction, and the layers then stack from there along DirectionSense. So the centre is
        /// offset + sign(sense) * thickness/2 - the sign applies to the half-thickness only, not
        /// to the offset. Revit writes offset=+t/2 with NEGATIVE sense (or the mirror of that),
        /// both of which correctly collapse to zero: its location line is already the centreline.
        /// </summary>
        Curve ApplyLayerOffset(IIfcWall wall, Curve axis, double thickness)
        {
            var usage = IfcHelpers.LayerSetUsage(wall);
            if (usage == null) return axis;

            var offset = (double)usage.OffsetFromReferenceLine * _ctx.Ifc.Scale.Length;
            var sense = usage.DirectionSense == IfcDirectionSenseEnum.NEGATIVE ? -1.0 : 1.0;
            var shift = offset + sense * thickness / 2.0;
            if (Math.Abs(shift) <= _ctx.ShortCurveTolerance) return axis;

            try
            {
                var direction = (axis.GetEndPoint(1) - axis.GetEndPoint(0)).Normalize();
                var normal = XYZ.BasisZ.CrossProduct(direction);
                if (normal.GetLength() < 1e-9) return axis;

                return axis.CreateTransformed(Transform.CreateTranslation(normal.Normalize().Multiply(shift)));
            }
            catch
            {
                return axis;
            }
        }

        /// <summary>Long axis through the centre of the extruded footprint.</summary>
        Curve FootprintAxis(ExtrusionInfo extrusion, Transform world)
        {
            if (extrusion?.Profile == null) return null;

            var loops = GeometryHelper.WorldLoops(_ctx.Ifc.Profiles, extrusion, world);
            if (loops == null || loops.Count == 0) return null;

            var frame = GeometryHelper.WorldFrame(extrusion, world);
            if (!GeometryHelper.LoopExtents(loops[0], frame, out var centre, out var longAxis,
                                            out var longExtent, out _))
                return null;

            if (longExtent <= _ctx.ShortCurveTolerance) return null;

            var half = longAxis.Multiply(longExtent / 2.0);
            try { return Line.CreateBound(centre - half, centre + half); }
            catch { return null; }
        }

        static Curve FlattenToElevation(Curve curve, double elevation)
        {
            try
            {
                var a = curve.GetEndPoint(0);
                var b = curve.GetEndPoint(1);
                var flatA = new XYZ(a.X, a.Y, elevation);
                var flatB = new XYZ(b.X, b.Y, elevation);

                if (flatA.DistanceTo(flatB) < 1e-6) return null;

                if (curve is Line) return Line.CreateBound(flatA, flatB);

                // Arcs keep their curvature by dropping the whole curve to the level plane.
                var delta = elevation - a.Z;
                return curve.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, delta)));
            }
            catch
            {
                return null;
            }
        }

        // ---- vertical extent -------------------------------------------------------------------

        /// <summary>
        /// Wall height, cut down to the roof where the IFC body was clipped.
        ///
        /// The extrusion depth is the height BEFORE the clip, so using it directly drives the wall
        /// straight through the roof - which is exactly what the first converted model did. The
        /// clipping planes give the real top at each end of the axis; a Revit wall has a flat top,
        /// so the lower of the two is taken. That leaves a wedge of missing wall under a sloped
        /// roof rather than a wall sticking out through it, and the shortfall is reported.
        /// </summary>
        double HeightOf(ExtrusionInfo extrusion, Curve axis, double baseZ, out double trimmed)
        {
            trimmed = 0.0;
            if (extrusion == null) return 0.0;

            // Walls are extruded along their local Z; a tilted extrusion needs the vertical share.
            var direction = extrusion.WorldDirection;
            var vertical = Math.Abs(direction.DotProduct(XYZ.BasisZ));
            var full = vertical < 1e-6 ? extrusion.Depth : extrusion.Depth * vertical;

            if (extrusion.Clips.Count == 0 || axis == null) return full;

            var lowestTop = double.MaxValue;
            foreach (var end in new[] { axis.GetEndPoint(0), axis.GetEndPoint(1) })
            {
                foreach (var clip in extrusion.Clips)
                {
                    // A plane that keeps its positive side caps nothing from above.
                    if (clip.KeepsPositiveSide) continue;

                    var top = clip.ElevationAt(end);
                    if (double.IsNaN(top)) continue;
                    lowestTop = Math.Min(lowestTop, top);
                }
            }

            if (lowestTop == double.MaxValue) return full;

            var clipped = lowestTop - baseZ;
            if (clipped <= _ctx.ShortCurveTolerance || clipped >= full) return full;

            trimmed = full - clipped;
            return clipped;
        }

        /// <summary>
        /// Height and base taken from the declared quantities, or failing that from the extent of
        /// the built geometry. Both are worse than the extrusion - quantities can disagree with the
        /// model, and an extent includes anything the body happens to contain - so this only runs
        /// when the extrusion is unavailable, and the result is flagged in the report.
        /// </summary>
        bool FallbackExtent(IIfcWall wall, ref double baseZ, out double height)
        {
            height = 0.0;

            var declared = QuantityHeight(wall);
            if (declared > _ctx.ShortCurveTolerance)
            {
                height = declared;
                return true;
            }

            var geometry = new DirectShapeBuilder(_ctx).BuildGeometry(wall, out _);
            if (geometry == null) return false;

            var lo = double.MaxValue;
            var hi = double.MinValue;

            foreach (var item in geometry)
            {
                switch (item)
                {
                    case Solid solid when solid.Volume > 0:
                    {
                        var box = solid.GetBoundingBox();
                        if (box == null) continue;
                        foreach (var corner in Corners(box))
                        {
                            lo = Math.Min(lo, corner.Z);
                            hi = Math.Max(hi, corner.Z);
                        }
                        break;
                    }

                    case Mesh mesh:
                        foreach (var vertex in mesh.Vertices)
                        {
                            lo = Math.Min(lo, vertex.Z);
                            hi = Math.Max(hi, vertex.Z);
                        }
                        break;
                }
            }

            if (lo == double.MaxValue || hi - lo <= _ctx.ShortCurveTolerance) return false;

            baseZ = lo;
            height = hi - lo;
            return true;
        }

        static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            var min = box.Min;
            var max = box.Max;

            for (int i = 0; i < 8; i++)
            {
                var local = new XYZ(
                    (i & 1) == 0 ? min.X : max.X,
                    (i & 2) == 0 ? min.Y : max.Y,
                    (i & 4) == 0 ? min.Z : max.Z);

                yield return box.Transform == null ? local : box.Transform.OfPoint(local);
            }
        }

        /// <summary>Height from Qto_WallBaseQuantities, when the exporter wrote quantities.</summary>
        double QuantityHeight(IIfcWall wall)
        {
            foreach (var quantitySet in IfcHelpers.QuantitySets(wall))
            {
                foreach (var quantity in quantitySet.Quantities.OfType<IIfcQuantityLength>())
                {
                    var name = (string)quantity.Name;
                    if (string.IsNullOrEmpty(name)) continue;

                    if (name.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0)
                        return (double)quantity.LengthValue * _ctx.Ifc.Scale.Length;
                }
            }
            return 0.0;
        }

        double BaseElevationOf(ExtrusionInfo extrusion, Transform world, Curve axis)
        {
            if (extrusion != null)
            {
                var origin = GeometryHelper.WorldFrame(extrusion, world).Origin;
                return origin.Z;
            }
            return axis?.GetEndPoint(0).Z ?? world.Origin.Z;
        }

        /// <summary>
        /// Describes what the clip cost. A Revit wall top is flat, so a wall cut by a sloped roof
        /// is shortened to its lowest corner and the wedge above it is missing; saying by how much
        /// is more useful than a bare warning that something was clipped.
        /// </summary>
        string ClipNote(ExtrusionInfo extrusion, double trimmed)
        {
            if (extrusion == null || !extrusion.WasClipped) return null;

            if (trimmed <= _ctx.ShortCurveTolerance)
                return "тело подрезано - проверьте верх стены";

            var mm = Math.Round(trimmed / IfcScale.FeetPerMetre * 1000.0);
            return $"верх срезан по скату, стена короче на {mm:F0} мм в верхней точке - достройте вручную";
        }

        /// <summary>Joins the caveats worth carrying into the report, dropping the empty ones.</summary>
        internal static string Notes(params string[] parts)
        {
            var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            return kept.Length == 0 ? null : string.Join("; ", kept);
        }

        static double SafeLength(Curve c)
        {
            try { return c.Length; }
            catch { return 0.0; }
        }

        /// <summary>
        /// Imported walls meet at angles the authoring tool already resolved. Letting Revit
        /// re-join them silently reshapes geometry, so joins are disabled at both ends.
        /// </summary>
        static void DisableAutoJoin(Wall wall)
        {
            try
            {
                WallUtils.DisallowWallJoinAtEnd(wall, 0);
                WallUtils.DisallowWallJoinAtEnd(wall, 1);
            }
            catch { }
        }
    }
}
