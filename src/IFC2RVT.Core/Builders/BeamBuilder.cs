using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using IFC2RVT.Ifc;
using IFC2RVT.Mapping;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>
    /// IfcBeam and IfcMember to native structural framing.
    ///
    /// The axis comes from the extrusion: a rolled profile is swept along a straight line, so the
    /// solid carries the centreline the framing family needs. The profile designation is a
    /// different matter - it lives in IfcBeam.Description as plain text ("L50X3", "Гн[160Х60Х4]")
    /// and there is no way to conjure the matching Revit family from it. A section that is not
    /// already loaded gets the nearest loaded one, and the substitution is reported rather than
    /// passed off as an exact match.
    ///
    /// Off by default: on a project without the right section families loaded, a wrong profile on
    /// every member is worse than honest DirectShape geometry.
    /// </summary>
    public class BeamBuilder : IElementBuilder
    {
        readonly BuilderContext _ctx;

        public string CategoryName => "Несущие конструкции";

        public BeamBuilder(BuilderContext ctx) => _ctx = ctx;

        public bool CanBuild(IIfcProduct product) => product is IIfcBeam || product is IIfcMember;

        public BuildResult Build(IIfcProduct product)
        {
            var element = (IIfcElement)product;
            var world = _ctx.Ifc.Placements.WorldTransform(element);
            var extrusion = _ctx.Ifc.Representations.LargestExtrusion(element);

            if (extrusion == null) return BuildResult.Fail("тело не является выдавливанием");
            if (extrusion.Depth <= _ctx.ShortCurveTolerance) return BuildResult.Fail("нулевая длина");

            var frame = GeometryHelper.WorldFrame(extrusion, world);
            var direction = GeometryHelper.WorldDirection(extrusion, world);

            var start = frame.Origin;
            var end = start + direction.Multiply(extrusion.Depth);
            if (start.DistanceTo(end) <= _ctx.ShortCurveTolerance)
                return BuildResult.Fail("вырожденная ось");

            var trimmed = ApplyClips(extrusion, world, ref start, ref end);
            var clipNote = extrusion.WasClipped && !trimmed
                ? "тело подрезано - длина взята до подрезки"
                : null;

            var level = _ctx.Levels.For(element, new XYZ(0, 0, Math.Min(start.Z, end.Z)));
            if (level == null) return BuildResult.Fail("нет подходящего уровня");

            SectionSize(element, out var width, out var height);
            var wanted = SectionName(element);
            var designation = SteelDesignation.Parse(wanted?.TypeName);

            // Sheet, strip and checker plate are flat products, not framing. A fifth of the steel
            // in the model this was measured on is exactly that, and putting it into a beam family
            // is wrong by category before it is wrong by shape.
            if (designation.IsFlatProduct)
            {
                _ctx.NoteMissingSection(wanted.TypeName, designation.KindName, designation.Standard);
                return BuildResult.Fail($"{designation.KindName} \"{wanted.TypeName}\" - не несущая конструкция");
            }

            // A family built from the outline this very file carries is the section itself, not a
            // stand-in for it, so it comes before anything the project happens to have loaded.
            var generated = _ctx.Sections?.Symbol(wanted?.TypeName);
            if (generated != null)
            {
                var placed = PlaceGenerated(generated, wanted, frame, start, end, level, clipNote);
                if (placed != null) return placed;
            }

            var match = width > 0
                ? _ctx.Types.ResolveSizedSymbol(BuiltInCategory.OST_StructuralFraming, wanted, width, height)
                : null;

            var resolved = match?.Type
                        ?? _ctx.Types.ResolveSymbol(BuiltInCategory.OST_StructuralFraming, wanted);

            if (resolved == null) return BuildResult.Fail("в проекте нет семейств несущих конструкций");

            // A framing family cannot be resized the way a door can, so a section that is neither
            // named nor sized like the one the model asks for is simply a different section. On a
            // real run this refused 2552 members - every angle, tube and plate would otherwise have
            // become the one I-beam the project happened to have loaded, and the model would look
            // plausible while being structurally false. DirectShape keeps the true shape instead.
            if (IsSubstitute(match, resolved, wanted))
            {
                _ctx.NoteMissingSection(wanted?.TypeName, designation.KindName, designation.Standard);
                return BuildResult.Fail(Missing(designation, wanted));
            }

            try
            {
                TypeResolver.EnsureActive(resolved);

                var axis = Line.CreateBound(start, end);
                var instance = _ctx.Doc.Create.NewFamilyInstance(axis, resolved, level, StructuralType.Beam);
                if (instance == null) return BuildResult.Fail("NewFamilyInstance вернул null");

                DetachFromLevel(instance);

                return new BuildResult
                {
                    Element = instance,
                    Message = WallBuilder.Notes(
                        SectionNote(element, resolved),
                        clipNote)
                };
            }
            catch (Exception ex)
            {
                return BuildResult.Fail("NewFamilyInstance: " + ex.Message);
            }
        }

        /// <summary>
        /// Places a member into a family generated from the file's own outline.
        ///
        /// Returns null rather than a failure when it does not work out, so the caller falls back to
        /// the ordinary search through loaded families and, failing that, to geometry.
        /// </summary>
        BuildResult PlaceGenerated(FamilySymbol symbol, RevitTypeName wanted, Transform frame,
                                   XYZ start, XYZ end, Level level, string clipNote)
        {
            FamilyInstance instance;
            try
            {
                TypeResolver.EnsureActive(symbol);
                var axis = Line.CreateBound(start, end);
                instance = _ctx.Doc.Create.NewFamilyInstance(axis, symbol, level, StructuralType.Beam);
            }
            catch
            {
                return null;
            }

            if (instance == null) return null;

            DetachFromLevel(instance);
            AlignSection(instance, frame, (end - start).Normalize());

            // The generated family ties the far end of its solid to this parameter, so the member is
            // only as long as it is told to be.
            SectionFamilyFactory.SetLength(instance, start.DistanceTo(end));

            // The first member of each section is measured against the length it was placed at. A
            // family whose solid never got tied to its end planes builds every member at the
            // template's nominal length, and nothing throws when that happens - only a measurement
            // catches it.
            if (_ctx.Sections.NeedsCheck(wanted?.TypeName))
            {
                // Measuring before Revit has rebuilt the solid reads the length from before the
                // parameter was set, which would fail every section for the wrong reason.
                try { _ctx.Doc.Regenerate(); } catch { }

                if (!_ctx.Sections.Accept(wanted?.TypeName, instance, start.DistanceTo(end)))
                {
                    try { _ctx.Doc.Delete(instance.Id); } catch { }
                    return null;
                }
            }

            return new BuildResult { Element = instance, Message = clipNote };
        }

        /// <summary>
        /// Shortens the axis where the solid was cut, and says whether it managed to.
        ///
        /// A clipped member used to be built to its full uncut extrusion, which puts it straight
        /// through the face it was cut against. The clip planes are in product coordinates while the
        /// axis is already in world, so each plane is carried over before it is intersected.
        /// </summary>
        bool ApplyClips(ExtrusionInfo extrusion, Transform world, ref XYZ start, ref XYZ end)
        {
            if (extrusion.Clips.Count == 0) return false;

            var direction = (end - start).Normalize();
            var length = start.DistanceTo(end);
            double low = 0.0, high = length;

            foreach (var clip in extrusion.Clips)
            {
                var origin = world.OfPoint(clip.Origin);
                var normal = world.OfVector(clip.Normal);
                if (normal.GetLength() < 1e-9) continue;
                normal = normal.Normalize();

                var along = normal.DotProduct(direction);

                // A plane square to the member cuts across it, not along it: nothing to trim.
                if (Math.Abs(along) < 1e-9) continue;

                var crossing = normal.DotProduct(origin - start) / along;
                if (crossing <= low || crossing >= high) continue;

                // Material survives on the side the plane keeps; the axis keeps the same interval.
                if (clip.KeepsPositiveSide == (along > 0)) low = Math.Max(low, crossing);
                else high = Math.Min(high, crossing);
            }

            if (low <= 0 && high >= length) return false;
            if (high - low <= _ctx.ShortCurveTolerance) return false;

            var origin0 = start;
            start = origin0 + direction.Multiply(low);
            end = origin0 + direction.Multiply(high);
            return true;
        }

        /// <summary>
        /// Turns the section about the member's axis until it sits the way the IFC has it.
        ///
        /// The generated family draws the outline in its own YZ plane, but Revit decides for itself
        /// which way that plane faces once the member is placed on a line. On a symmetric section
        /// nothing shows; on an angle or a channel a quarter turn is the whole difference between a
        /// correct model and a plausible one.
        /// </summary>
        static void AlignSection(FamilyInstance instance, Transform frame, XYZ axis)
        {
            try
            {
                var placed = instance.GetTransform();
                if (placed == null) return;

                // Correct only what is understood: if the instance is not oriented along its own
                // axis, the assumption this correction rests on does not hold.
                if (Math.Abs(placed.BasisX.Normalize().DotProduct(axis)) < 0.99) return;

                var have = Perpendicular(placed.BasisY, axis);
                var want = Perpendicular(frame.BasisX, axis);
                if (have.GetLength() < 1e-9 || want.GetLength() < 1e-9) return;

                var angle = have.Normalize().AngleOnPlaneTo(want.Normalize(), axis);
                if (angle < 1e-6 || Math.Abs(angle - 2 * Math.PI) < 1e-6) return;

                var parameter = instance.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE);
                if (parameter != null && !parameter.IsReadOnly)
                    parameter.Set(parameter.AsDouble() + angle);
            }
            catch
            {
                // An unrotated section is still the right section; never lose the member over this.
            }
        }

        static XYZ Perpendicular(XYZ vector, XYZ axis) => vector - axis.Multiply(vector.DotProduct(axis));

        /// <summary>
        /// Profile designation, from the most trustworthy source available.
        ///
        /// IfcMaterialProfileSet is the real one: a parametric profile with a name the authoring
        /// tool assigned. Description is loose text an exporter happened to write there, and the
        /// instance name is the usual Family:Type:Id. Try them in that order.
        /// </summary>
        RevitTypeName SectionName(IIfcElement element)
        {
            var declared = IfcHelpers.ProfileName(IfcHelpers.StructuralProfile(element));
            if (!string.IsNullOrWhiteSpace(declared)) return RevitTypeName.Parse(declared);

            var description = IfcHelpers.Str(element.Description);
            if (!string.IsNullOrWhiteSpace(description) &&
                !description.Equals("main piece", StringComparison.OrdinalIgnoreCase))
                return RevitTypeName.Parse(description);

            return _ctx.TypeNameOf(element);
        }

        /// <summary>
        /// Cross-section size in feet, taken from the declared profile. Used to pick the closest
        /// loaded section when nothing matches by name: a beam of roughly the right depth reads far
        /// better in a model than whichever section happened to be first in the project.
        /// </summary>
        bool SectionSize(IIfcElement element, out double width, out double height)
        {
            width = 0;
            height = 0;

            var profile = IfcHelpers.StructuralProfile(element);
            if (profile == null) return false;

            var loops = _ctx.Ifc.Profiles.Read(profile);
            if (loops == null || loops.Count == 0) return false;

            var box = ProfileReader.BoundingBoxOf(loops[0]);
            width = box.Item2.X - box.Item1.X;
            height = box.Item2.Y - box.Item1.Y;
            return width > 0 && height > 0;
        }

        /// <summary>
        /// True when the section on offer is neither the one asked for by name nor one of matching
        /// size. Named and sized matches are both legitimate; anything else is a different profile
        /// wearing the right place in the model.
        /// </summary>
        static bool IsSubstitute(Mapping.TypeResolver.Resolved<FamilySymbol> match,
                                 FamilySymbol resolved, RevitTypeName wanted)
        {
            if (match != null && !match.IsSubstitute) return false;

            var name = wanted?.TypeName;
            if (string.IsNullOrWhiteSpace(name)) return false;   // nothing was asked for

            // Compare through the designation, so a type named with Latin X still matches a
            // section the file spelled with Cyrillic Х.
            return !SteelDesignation.SameSection(resolved.Name, name);
        }

        /// <summary>
        /// Says which family is missing, not merely that something is. A report that names the
        /// standard turns a wall of failures into a shopping list.
        /// </summary>
        static string Missing(SteelDesignation designation, RevitTypeName name)
        {
            var text = name?.TypeName;
            if (string.IsNullOrWhiteSpace(text)) return "профиль не определён - оставлено геометрией";

            return designation.Standard == null
                ? $"нет типоразмера \"{text}\" - оставлено геометрией"
                : $"нет типоразмера \"{text}\" ({designation.KindName}, {designation.Standard}) - оставлено геометрией";
        }

        string SectionNote(IIfcElement element, FamilySymbol symbol)
        {
            var wanted = SectionName(element)?.TypeName;
            if (string.IsNullOrWhiteSpace(wanted)) return null;

            return SteelDesignation.SameSection(symbol.Name, wanted)
                ? null
                : $"профиль подобран по габариту: \"{wanted}\" → \"{symbol.Name}\"";
        }

        /// <summary>
        /// Imported framing already sits at its final elevation. Leaving the Z-justification
        /// attached to the level lets Revit shift the member when the level moves, which silently
        /// undoes the geometry we just placed.
        /// </summary>
        static void DetachFromLevel(FamilyInstance instance)
        {
            var parameter = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (parameter != null && !parameter.IsReadOnly)
            {
                try { parameter.Set(0.0); } catch { }
            }
        }
    }
}
