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
                        extrusion.WasClipped ? "тело подрезано - длина взята до подрезки" : null)
                };
            }
            catch (Exception ex)
            {
                return BuildResult.Fail("NewFamilyInstance: " + ex.Message);
            }
        }

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
