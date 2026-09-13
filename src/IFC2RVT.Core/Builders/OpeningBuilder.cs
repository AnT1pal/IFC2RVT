using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>
    /// IfcDoor and IfcWindow to hosted Revit family instances.
    ///
    /// Depends on the wall having been converted natively first: a door can only be hosted by a
    /// real Revit wall, not by the DirectShape that stands in for one. When the host is missing
    /// or was not converted, the opening is refused here and picked up by the fallback, which is
    /// the honest outcome - an unhosted door in a wall-shaped solid would not behave like a door.
    /// </summary>
    public class OpeningBuilder : IElementBuilder
    {
        readonly BuilderContext _ctx;

        public string CategoryName => "Двери и окна";

        public OpeningBuilder(BuilderContext ctx) => _ctx = ctx;

        public bool CanBuild(IIfcProduct product) => product is IIfcDoor || product is IIfcWindow;

        public BuildResult Build(IIfcProduct product)
        {
            var isDoor = product is IIfcDoor;
            var element = (IIfcElement)product;

            var ifcHost = _ctx.Ifc.HostOf(element);
            if (ifcHost == null) return BuildResult.Fail("не найден хост-элемент в IFC");

            var host = _ctx.Created(ifcHost) as Wall;
            if (host == null) return BuildResult.Fail("стена-хост не сконвертирована нативно");

            var location = LocationOf(element);
            if (location == null) return BuildResult.Fail("не определено положение проёма");

            var category = isDoor ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows;

            // Width and height are type parameters in Revit, so the size has to be settled before
            // the instance is placed: the family cuts its own hole in the wall, and a symbol of the
            // wrong size leaves an opening that visibly disagrees with the IFC geometry.
            SizeOf(product, out var width, out var height);

            var resolved = _ctx.Types.ResolveSizedSymbol(category, _ctx.TypeNameOf(product), width, height);
            var symbol = resolved.Type;
            if (symbol == null)
                return BuildResult.Fail(isDoor ? "в проекте нет семейств дверей" : "в проекте нет семейств окон");

            var level = _ctx.Doc.GetElement(host.LevelId) as Level
                     ?? _ctx.Levels.For(product, location);
            if (level == null) return BuildResult.Fail("нет подходящего уровня");

            try
            {
                Mapping.TypeResolver.EnsureActive(symbol);

                var instance = _ctx.Doc.Create.NewFamilyInstance(
                    location, symbol, host, level, StructuralType.NonStructural);

                if (instance == null) return BuildResult.Fail("NewFamilyInstance вернул null");

                ApplySillHeight(instance, level, location);
                return new BuildResult { Element = instance, Message = resolved.Note };
            }
            catch (Exception ex)
            {
                return BuildResult.Fail("NewFamilyInstance: " + ex.Message);
            }
        }

        /// <summary>Declared opening size in feet, or zero when the exporter omitted it.</summary>
        void SizeOf(IIfcProduct product, out double widthFeet, out double heightFeet)
        {
            widthFeet = 0;
            heightFeet = 0;

            switch (product)
            {
                case IIfcDoor door:
                    if (door.OverallWidth.HasValue) widthFeet = (double)door.OverallWidth.Value * _ctx.Ifc.Scale.Length;
                    if (door.OverallHeight.HasValue) heightFeet = (double)door.OverallHeight.Value * _ctx.Ifc.Scale.Length;
                    break;

                case IIfcWindow window:
                    if (window.OverallWidth.HasValue) widthFeet = (double)window.OverallWidth.Value * _ctx.Ifc.Scale.Length;
                    if (window.OverallHeight.HasValue) heightFeet = (double)window.OverallHeight.Value * _ctx.Ifc.Scale.Length;
                    break;
            }
        }

        /// <summary>
        /// Insertion point of the opening: centred horizontally, but at the BOTTOM of the void.
        ///
        /// Revit measures a door or window from its sill, not its middle. Handing it the centre of
        /// the opening put every instance half its own height too high - a 2500 mm gate came out
        /// with a sill of 1250 mm.
        /// </summary>
        XYZ LocationOf(IIfcElement element)
        {
            var opening = _ctx.Ifc.OpeningFor(element);
            if (opening != null)
            {
                var seat = OpeningSeat(opening);
                if (seat != null) return seat;
            }

            var placement = _ctx.Ifc.Placements.WorldTransform(element);
            return placement?.Origin;
        }

        XYZ OpeningSeat(IIfcOpeningElement opening)
        {
            var world = _ctx.Ifc.Placements.WorldTransform(opening);
            var extrusion = _ctx.Ifc.Representations.LargestExtrusion(opening);
            if (extrusion == null) return world?.Origin;

            var loops = GeometryHelper.WorldLoops(_ctx.Ifc.Profiles, extrusion, world);
            if (loops == null || loops.Count == 0) return world?.Origin;

            // The void may be extruded horizontally through the wall or vertically; measuring the
            // whole swept solid rather than just its profile keeps this independent of which.
            var sweep = GeometryHelper.WorldDirection(extrusion, world).Multiply(extrusion.Depth);

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue;
            var found = false;

            foreach (var curve in loops[0])
            {
                foreach (var point in curve.Tessellate())
                {
                    foreach (var p in new[] { point, point + sweep })
                    {
                        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                        minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                        minZ = Math.Min(minZ, p.Z);
                        found = true;
                    }
                }
            }

            return found ? new XYZ((minX + maxX) / 2.0, (minY + maxY) / 2.0, minZ) : world?.Origin;
        }

        /// <summary>
        /// Pins the sill to the measured bottom of the void. Applies to doors as well as windows:
        /// a gate sitting on the floor and a window at 900 mm are the same calculation, and the
        /// value from the IFC beats whatever the substituted family type happened to carry.
        /// </summary>
        static void ApplySillHeight(FamilyInstance instance, Level level, XYZ location)
        {
            var sill = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (sill == null || sill.IsReadOnly) return;

            try { sill.Set(location.Z - level.Elevation); }
            catch { }
        }
    }
}
