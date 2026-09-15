using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>
    /// IfcCovering of type CEILING to a native Revit ceiling.
    ///
    /// Same shape of problem as a slab - a horizontal extrusion with a closed outline - but the
    /// element is worth having natively because ceilings carry the lighting and grid layouts that
    /// a DirectShape cannot host.
    ///
    /// Ceiling.Create arrived in Revit 2022, which is the oldest version this add-in supports, so
    /// no version guard is needed.
    /// </summary>
    public class CeilingBuilder : IElementBuilder
    {
        readonly BuilderContext _ctx;

        public string CategoryName => "Потолки";

        public CeilingBuilder(BuilderContext ctx) => _ctx = ctx;

        public bool CanBuild(IIfcProduct product)
            => product is IIfcCovering covering &&
               (covering.PredefinedType == IfcCoveringTypeEnum.CEILING ||
                covering.PredefinedType == null);

        public BuildResult Build(IIfcProduct product)
        {
            var covering = (IIfcCovering)product;
            var world = _ctx.Ifc.Placements.WorldTransform(covering);
            var extrusion = _ctx.Ifc.Representations.LargestExtrusion(covering);

            if (extrusion == null) return BuildResult.Fail("тело не является выдавливанием");

            var direction = extrusion.WorldDirection;
            if (Math.Abs(direction.DotProduct(XYZ.BasisZ)) < 0.999)
                return BuildResult.Fail("наклонный потолок - нужен DirectShape");

            var loops = GeometryHelper.WorldLoops(_ctx.Ifc.Profiles, extrusion, world);
            if (loops == null || loops.Count == 0) return BuildResult.Fail("не прочитан контур потолка");

            var baseZ = GeometryHelper.WorldFrame(extrusion, world).Origin.Z;
            var topZ = direction.Z >= 0 ? baseZ + extrusion.Depth : baseZ;

            var level = _ctx.Levels.For(covering, new XYZ(0, 0, baseZ));
            if (level == null) return BuildResult.Fail("нет подходящего уровня");

            var type = CeilingType();
            if (type == null) return BuildResult.Fail("в проекте нет типов потолка");

            try
            {
                // A ceiling sketch sits at its own elevation and the offset carries it from the
                // level, exactly as a floor does.
                var flattened = loops.Select(l => Flatten(l, topZ)).ToList();
                if (flattened.Any(l => l == null)) return BuildResult.Fail("контур не приводится к плоскости");

                var ceiling = Ceiling.Create(_ctx.Doc, flattened, type.Id, level.Id);
                if (ceiling == null) return BuildResult.Fail("Ceiling.Create вернул null");

                var offset = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                if (offset != null && !offset.IsReadOnly) offset.Set(topZ - level.Elevation);

                return BuildResult.Ok(ceiling);
            }
            catch (Exception ex)
            {
                return BuildResult.Fail("Ceiling.Create: " + ex.Message);
            }
        }

        ElementType CeilingType()
            => new FilteredElementCollector(_ctx.Doc)
                .OfClass(typeof(CeilingType))
                .Cast<ElementType>()
                .FirstOrDefault();

        CurveLoop Flatten(CurveLoop loop, double elevation)
        {
            try
            {
                var segments = new List<Curve>();
                foreach (var c in loop)
                {
                    var delta = elevation - c.GetEndPoint(0).Z;
                    segments.Add(Math.Abs(delta) < 1e-9
                        ? c
                        : c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, delta))));
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
