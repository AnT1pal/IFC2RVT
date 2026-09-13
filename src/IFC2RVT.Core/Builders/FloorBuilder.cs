using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>
    /// IfcSlab to a native Revit Floor.
    ///
    /// Only horizontally extruded slabs are taken natively. A sloped or warped slab would have to
    /// be flattened to be expressed as a Revit floor sketch, and a silently flattened slab is worse
    /// than an honest DirectShape, so those are handed back to the fallback.
    /// </summary>
    public class FloorBuilder : IElementBuilder
    {
        readonly BuilderContext _ctx;

        public string CategoryName => "Перекрытия";

        public FloorBuilder(BuilderContext ctx) => _ctx = ctx;

        public bool CanBuild(IIfcProduct product)
            => product is IIfcSlab slab && slab.PredefinedType != IfcSlabTypeEnum.LANDING;

        public BuildResult Build(IIfcProduct product)
        {
            var slab = (IIfcSlab)product;
            var world = _ctx.Ifc.Placements.WorldTransform(slab);
            var extrusion = _ctx.Ifc.Representations.LargestExtrusion(slab);

            if (extrusion == null) return BuildResult.Fail("тело не является выдавливанием");

            var direction = extrusion.WorldDirection;
            var verticality = Math.Abs(direction.DotProduct(XYZ.BasisZ));
            if (verticality < 0.999)
                return BuildResult.Fail("наклонная плита - нужен DirectShape");

            var loops = GeometryHelper.WorldLoops(_ctx.Ifc.Profiles, extrusion, world);
            if (loops == null || loops.Count == 0) return BuildResult.Fail("не прочитан контур плиты");

            var thickness = ThicknessOf(slab, extrusion);
            if (thickness <= _ctx.ShortCurveTolerance) return BuildResult.Fail("не определена толщина плиты");

            var baseZ = GeometryHelper.WorldFrame(extrusion, world).Origin.Z;
            var topZ = direction.Z >= 0 ? baseZ + extrusion.Depth : baseZ;

            var level = _ctx.Levels.For(slab, new XYZ(0, 0, topZ));
            if (level == null) return BuildResult.Fail("нет подходящего уровня");

            var materialId = _ctx.Materials.PrimaryMaterialOf(slab);
            var resolved = _ctx.Types.ResolveFloorType(_ctx.TypeNameOf(slab), thickness, materialId);
            var type = resolved.Type;
            if (type == null) return BuildResult.Fail("нет типа перекрытия в проекте");

            try
            {
                // Revit sketches a floor on a horizontal plane and grows it downwards, so the
                // sketch sits at the top face and the offset carries it to the right elevation.
                var flattened = loops.Select(l => Flatten(l, topZ)).ToList();
                if (flattened.Any(l => l == null)) return BuildResult.Fail("контур не приводится к плоскости");

                var floor = Floor.Create(_ctx.Doc, flattened, type.Id, level.Id);
                if (floor == null) return BuildResult.Fail("Floor.Create вернул null");

                var offset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (offset != null && !offset.IsReadOnly) offset.Set(topZ - level.Elevation);

                return new BuildResult { Element = floor, Message = resolved.Note };
            }
            catch (Exception ex)
            {
                return BuildResult.Fail("Floor.Create: " + ex.Message);
            }
        }

        double ThicknessOf(IIfcSlab slab, ExtrusionInfo extrusion)
        {
            var layerSet = IfcHelpers.LayerSet(slab);
            var total = IfcHelpers.TotalThickness(layerSet) * _ctx.Ifc.Scale.Length;
            return total > _ctx.ShortCurveTolerance ? total : extrusion.Depth;
        }

        /// <summary>Projects a loop onto a horizontal plane, preserving lines and arcs.</summary>
        CurveLoop Flatten(CurveLoop loop, double elevation)
        {
            try
            {
                var segments = new List<Curve>();
                foreach (var c in loop)
                {
                    var delta = elevation - c.GetEndPoint(0).Z;
                    var moved = Math.Abs(delta) < 1e-9
                        ? c
                        : c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, delta)));
                    segments.Add(moved);
                }
                return _ctx.Ifc.Profiles.BuildLoop(segments);
            }
            catch
            {
                return null;
            }
        }
    }
}
