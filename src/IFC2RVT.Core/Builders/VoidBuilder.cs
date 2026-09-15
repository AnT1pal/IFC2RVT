using System;
using System.Linq;
using Autodesk.Revit.DB;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>
    /// IfcOpeningElement that nothing fills, cut into its host as a real Revit opening.
    ///
    /// These are the holes that are not doors or windows: niches, service penetrations, shafts.
    /// The sample model carries 3752 openings of which 15 hold a door or a window, so ignoring the
    /// rest leaves walls and slabs solid where the source model has them pierced.
    ///
    /// Runs after the hosts are built, and never falls back to DirectShape: an opening rendered as
    /// a solid is not a hole, it is a block sitting inside a wall.
    /// </summary>
    public class VoidBuilder : IElementBuilder
    {
        readonly BuilderContext _ctx;

        public string CategoryName => "Проёмы";

        public VoidBuilder(BuilderContext ctx) => _ctx = ctx;

        public bool CanBuild(IIfcProduct product)
        {
            if (!(product is IIfcOpeningElement opening)) return false;

            // A filled opening belongs to the door or window; cutting it as well would leave a
            // second hole beside the one the family already makes.
            return !opening.HasFillings.Any();
        }

        public BuildResult Build(IIfcProduct product)
        {
            var opening = (IIfcOpeningElement)product;

            var ifcHost = _ctx.Ifc.OpeningHosts.TryGetValue(opening.EntityLabel, out var h) ? h : null;

            // Most openings in a detailed model are orphans: the test file holds 3752 of them but
            // only 440 IfcRelVoidsElement, because bolt holes live inside the shapes of assemblies
            // rather than voiding a building element. Nothing is wrong and nothing can be cut.
            if (ifcHost == null)
                return BuildResult.Fail("файл не указывает, что вырезает этот проём");

            var host = _ctx.Created(ifcHost);
            if (host == null) return BuildResult.Fail("хост не сконвертирован нативно");

            var geometry = new DirectShapeBuilder(_ctx).BuildGeometry(opening, out var diagnosis);
            if (geometry == null) return BuildResult.Fail(diagnosis ?? "не прочитана геометрия проёма");

            if (!GeometryHelper.Extent(geometry, out var min, out var max))
                return BuildResult.Fail("не определён габарит проёма");

            try
            {
                switch (host)
                {
                    case Wall wall:
                        return CutWall(wall, min, max);

                    case Floor _:
                    case Ceiling _:
                    case RoofBase _:
                        return CutHorizontal(host, min, max);

                    case FamilyInstance _:
                        // Openings through beams and columns are bolt holes. NewOpening can make
                        // them, but it needs the reference face chosen correctly, and a hole put
                        // through the wrong face of a member is worse than no hole at all.
                        return BuildResult.Fail("проёмы в балках и колоннах пока не вырезаются");

                    default:
                        return BuildResult.Fail("хост " + host.GetType().Name + " не принимает проёмы");
                }
            }
            catch (Exception ex)
            {
                return BuildResult.Fail("NewOpening: " + ex.Message);
            }
        }

        /// <summary>
        /// Rectangular opening in a wall. Revit takes two opposite corners and works out the rest
        /// from the wall plane, so the box of the void is exactly the input it wants.
        /// </summary>
        BuildResult CutWall(Wall wall, XYZ min, XYZ max)
        {
            if (max.Z - min.Z <= _ctx.ShortCurveTolerance)
                return BuildResult.Fail("нулевая высота проёма");

            var opening = _ctx.Doc.Create.NewOpening(wall, min, max);
            return opening == null
                ? BuildResult.Fail("NewOpening вернул null")
                : BuildResult.Ok(opening);
        }

        /// <summary>
        /// Opening through a floor, ceiling or roof. The profile is the footprint of the void taken
        /// at the host level; a rectangle is enough, because a void that is not rectangular in plan
        /// would not have survived the reduction to a bounding box anyway.
        /// </summary>
        BuildResult CutHorizontal(Element host, XYZ min, XYZ max)
        {
            var width = max.X - min.X;
            var depth = max.Y - min.Y;
            if (width <= _ctx.ShortCurveTolerance || depth <= _ctx.ShortCurveTolerance)
                return BuildResult.Fail("вырожденный контур проёма");

            var z = (min.Z + max.Z) / 2.0;
            var corners = new[]
            {
                new XYZ(min.X, min.Y, z),
                new XYZ(max.X, min.Y, z),
                new XYZ(max.X, max.Y, z),
                new XYZ(min.X, max.Y, z)
            };

            var profile = new CurveArray();
            for (int i = 0; i < 4; i++)
                profile.Append(Line.CreateBound(corners[i], corners[(i + 1) % 4]));

            // The flag asks for a face perpendicular to the host, which is what a vertical
            // penetration through a horizontal slab is.
            var opening = _ctx.Doc.Create.NewOpening(host, profile, true);
            return opening == null
                ? BuildResult.Fail("NewOpening вернул null")
                : BuildResult.Ok(opening);
        }
    }
}
