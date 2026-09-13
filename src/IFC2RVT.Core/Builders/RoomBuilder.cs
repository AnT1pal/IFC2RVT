using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>
    /// IfcSpace to a Revit Room.
    ///
    /// Rooms are bounded by the model, not by the sketch: Revit computes the room volume from the
    /// surrounding walls, so this builder only plants the room point and copies the name and
    /// number. A room whose bounding walls did not convert natively will come out unenclosed, and
    /// that is reported rather than papered over.
    /// </summary>
    public class RoomBuilder : IElementBuilder
    {
        readonly BuilderContext _ctx;

        public string CategoryName => "Помещения";

        public RoomBuilder(BuilderContext ctx) => _ctx = ctx;

        public bool CanBuild(IIfcProduct product) => product is IIfcSpace;

        public BuildResult Build(IIfcProduct product)
        {
            var space = (IIfcSpace)product;
            var point = CentroidOf(space);
            if (point == null) return BuildResult.Fail("не определён центр помещения");

            var level = _ctx.Levels.For(space, point);
            if (level == null) return BuildResult.Fail("нет подходящего уровня");

            try
            {
                var room = _ctx.Doc.Create.NewRoom(level, new UV(point.X, point.Y));
                if (room == null) return BuildResult.Fail("NewRoom вернул null");

                ApplyIdentity(room, space);

                // An unplaced room has no area and is of no use in a schedule; surface it.
                if (room.Area <= 1e-9)
                    return new BuildResult { Element = room, Message = "помещение не замкнуто стенами" };

                return BuildResult.Ok(room);
            }
            catch (Exception ex)
            {
                return BuildResult.Fail("NewRoom: " + ex.Message);
            }
        }

        XYZ CentroidOf(IIfcSpace space)
        {
            var world = _ctx.Ifc.Placements.WorldTransform(space);
            var extrusion = _ctx.Ifc.Representations.LargestExtrusion(space);

            if (extrusion != null)
            {
                var loops = GeometryHelper.WorldLoops(_ctx.Ifc.Profiles, extrusion, world);
                if (loops != null && loops.Count > 0)
                {
                    var frame = GeometryHelper.WorldFrame(extrusion, world);
                    if (GeometryHelper.LoopExtents(loops[0], frame, out var centre, out _, out _, out _))
                        return centre;
                }
            }

            return world?.Origin;
        }

        void ApplyIdentity(Room room, IIfcSpace space)
        {
            var name = IfcHelpers.Str(space.LongName) ?? IfcHelpers.Str(space.Name);
            if (!string.IsNullOrWhiteSpace(name)) TrySet(room, BuiltInParameter.ROOM_NAME, name);

            var number = IfcHelpers.Str(space.Name);
            if (!string.IsNullOrWhiteSpace(number)) TrySet(room, BuiltInParameter.ROOM_NUMBER, number);
        }

        static void TrySet(Element e, BuiltInParameter bip, string value)
        {
            var p = e.get_Parameter(bip);
            if (p == null || p.IsReadOnly) return;
            try { p.Set(value); } catch { }
        }
    }
}
