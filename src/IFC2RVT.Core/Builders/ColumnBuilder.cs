using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>
    /// IfcColumn to a native structural column.
    ///
    /// This is the weakest of the native builders by nature: Revit columns are family instances,
    /// and the profile in the IFC cannot conjure a matching family that is not already loaded in
    /// the project. The builder places an instance of the closest available family and records the
    /// original profile designation in the IFC parameters; it does not fabricate family geometry.
    /// </summary>
    public class ColumnBuilder : IElementBuilder
    {
        readonly BuilderContext _ctx;

        public string CategoryName => "Колонны";

        public ColumnBuilder(BuilderContext ctx) => _ctx = ctx;

        public bool CanBuild(IIfcProduct product) => product is IIfcColumn;

        public BuildResult Build(IIfcProduct product)
        {
            var column = (IIfcColumn)product;
            var world = _ctx.Ifc.Placements.WorldTransform(column);
            var extrusion = _ctx.Ifc.Representations.LargestExtrusion(column);

            if (extrusion == null) return BuildResult.Fail("тело не является выдавливанием");

            var direction = extrusion.WorldDirection;
            if (Math.Abs(direction.DotProduct(XYZ.BasisZ)) < 0.999)
                return BuildResult.Fail("наклонная колонна - нужен DirectShape");

            var frame = GeometryHelper.WorldFrame(extrusion, world);
            var origin = frame.Origin;

            var baseZ = direction.Z >= 0 ? origin.Z : origin.Z - extrusion.Depth;
            var topZ = baseZ + extrusion.Depth;

            var baseLevel = _ctx.Levels.For(column, new XYZ(0, 0, baseZ));
            if (baseLevel == null) return BuildResult.Fail("нет подходящего уровня");

            var symbol = _ctx.Types.ResolveSymbol(BuiltInCategory.OST_StructuralColumns, _ctx.TypeNameOf(column));
            if (symbol == null) return BuildResult.Fail("в проекте нет семейств несущих колонн");

            try
            {
                TypeResolverActivate(symbol);

                var location = new XYZ(origin.X, origin.Y, baseZ);
                var instance = _ctx.Doc.Create.NewFamilyInstance(
                    location, symbol, baseLevel, StructuralType.Column);

                if (instance == null) return BuildResult.Fail("NewFamilyInstance вернул null");

                ApplyExtent(instance, baseLevel, baseZ, topZ);
                ApplyRotation(instance, location, frame);

                return BuildResult.Ok(instance);
            }
            catch (Exception ex)
            {
                return BuildResult.Fail("NewFamilyInstance: " + ex.Message);
            }
        }

        static void TypeResolverActivate(FamilySymbol symbol)
            => Mapping.TypeResolver.EnsureActive(symbol);

        void ApplyExtent(FamilyInstance instance, Level baseLevel, double baseZ, double topZ)
        {
            SetParameter(instance, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, baseLevel.Id);
            SetParameter(instance, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, baseZ - baseLevel.Elevation);

            var topLevel = _ctx.Levels.NearestLevel(topZ) ?? baseLevel;
            SetParameter(instance, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLevel.Id);
            SetParameter(instance, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, topZ - topLevel.Elevation);
        }

        /// <summary>Carries the profile orientation over as a rotation about the column axis.</summary>
        void ApplyRotation(FamilyInstance instance, XYZ location, Transform frame)
        {
            try
            {
                var x = frame.BasisX;
                var angle = Math.Atan2(x.Y, x.X);
                if (Math.Abs(angle) < 1e-9) return;

                var axis = Line.CreateUnbound(location, XYZ.BasisZ);
                ElementTransformUtils.RotateElement(_ctx.Doc, instance.Id, axis, angle);
            }
            catch { }
        }

        static void SetParameter(Element e, BuiltInParameter bip, ElementId value)
        {
            var p = e.get_Parameter(bip);
            if (p != null && !p.IsReadOnly) { try { p.Set(value); } catch { } }
        }

        static void SetParameter(Element e, BuiltInParameter bip, double value)
        {
            var p = e.get_Parameter(bip);
            if (p != null && !p.IsReadOnly) { try { p.Set(value); } catch { } }
        }
    }
}
