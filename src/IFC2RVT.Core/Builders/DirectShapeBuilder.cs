using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>
    /// The safety net. Anything without a native builder, or whose native builder failed, still
    /// arrives as geometry so the converted model is never missing pieces.
    ///
    /// Category is mapped from the IFC entity so the result is at least filterable and
    /// controllable by visibility settings, rather than a heap of generic models.
    /// </summary>
    public class DirectShapeBuilder : IElementBuilder
    {
        readonly BuilderContext _ctx;

        public string CategoryName => "DirectShape";

        public DirectShapeBuilder(BuilderContext ctx) => _ctx = ctx;

        public bool CanBuild(IIfcProduct product) => product != null;

        public BuildResult Build(IIfcProduct product)
        {
            var geometry = BuildGeometry(product, out var message);
            if (geometry == null || geometry.Count == 0)
                return BuildResult.Fail(message ?? "геометрия не прочитана");

            try
            {
                var category = CategoryFor(product);
                var shape = DirectShape.CreateElement(_ctx.Doc, category);
                if (shape == null) return BuildResult.Fail("DirectShape.CreateElement вернул null");

                shape.ApplicationId = "IFC2RVT";
                shape.ApplicationDataId = product.GlobalId.ToString();
                shape.SetShape(geometry);

                var name = IfcHelpers.Str(product.Name);
                if (!string.IsNullOrWhiteSpace(name)) shape.SetName(name);

                return BuildResult.Ok(shape);
            }
            catch (Exception ex)
            {
                return BuildResult.Fail("DirectShape: " + ex.Message);
            }
        }

        /// <summary>
        /// Collects every representation item of the product into Revit geometry.
        /// Solids are preferred; shells fall back to meshes.
        /// </summary>
        public IList<GeometryObject> BuildGeometry(IIfcProduct product, out string message)
        {
            message = null;
            var body = _ctx.Ifc.Representations.BodyOf(product);
            if (body == null)
            {
                message = "нет представления Body";
                return null;
            }

            var world = _ctx.Ifc.Placements.WorldTransform(product);
            var materialId = _ctx.Materials.PrimaryMaterialOf(product as IIfcObjectDefinition);
            var result = new List<GeometryObject>();
            var unsupported = new HashSet<string>();

            foreach (var pair in _ctx.Ifc.Representations.Items(body))
            {
                var item = pair.Item1;
                var local = world.Multiply(pair.Item2);

                // A surface style on the geometry itself beats the element material: it is what the
                // authoring tool actually drew, and it is the only colour many elements carry.
                var shade = _ctx.Ifc.Styles.ForItem(item);
                var itemMaterial = shade != null ? _ctx.Materials.ResolveShade(shade) : materialId;

                var produced = FromItem(item, local, itemMaterial, unsupported);
                if (produced != null) result.AddRange(produced);
            }

            if (result.Count == 0 && unsupported.Count > 0)
                message = string.Join("; ", unsupported.Take(3));

            return result.Count > 0 ? result : null;
        }

        IList<GeometryObject> FromItem(IIfcRepresentationItem item, Transform placement,
                                       ElementId materialId, HashSet<string> unsupported)
        {
            switch (item)
            {
                case IIfcExtrudedAreaSolid _:
                case IIfcBooleanResult _:
                {
                    var solid = SolidFromSweptItem(item, placement, materialId);
                    if (solid != null) return new List<GeometryObject> { solid };
                    unsupported.Add(IfcHelpers.EntityName(item));
                    return null;
                }

                case IIfcCurve _:
                    // Curve-only bodies carry no volume worth materialising.
                    return null;

                default:
                {
                    var mesh = GeometryHelper.TessellateShell(
                        item, _ctx.Ifc.Curves, _ctx.Ifc.Scale, placement, materialId, out var diagnosis);
                    if (mesh != null) return mesh;
                    unsupported.Add(diagnosis ?? IfcHelpers.EntityName(item));
                    return null;
                }
            }
        }

        Solid SolidFromSweptItem(IIfcRepresentationItem item, Transform placement, ElementId materialId)
        {
            // Reuse the extrusion reader by wrapping the single item in a throwaway lookup.
            var extrusion = ExtrusionOf(item);
            if (extrusion == null) return null;
            return GeometryHelper.SolidFrom(_ctx.Ifc.Profiles, extrusion, placement, materialId);
        }

        ExtrusionInfo ExtrusionOf(IIfcRepresentationItem item, int depth = 0)
        {
            if (depth > 64) return null;   // same reason as RepresentationReader: openings nest

            switch (item)
            {
                case IIfcExtrudedAreaSolid solid:
                {
                    var ratios = IfcHelpers.Ratios(solid.ExtrudedDirection, new[] { 0.0, 0.0, 1.0 });
                    var dir = new XYZ(ratios[0], ratios[1], ratios[2]);
                    if (dir.GetLength() < 1e-12) return null;

                    return new ExtrusionInfo
                    {
                        Profile = solid.SweptArea,
                        Position = _ctx.Ifc.Placements.From3D(solid.Position),
                        Direction = dir.Normalize(),
                        Depth = (double)solid.Depth * _ctx.Ifc.Scale.Length
                    };
                }

                case IIfcBooleanResult boolean:
                    // Only the base operand survives: subtracting the clipping half-spaces needs
                    // boolean support the fallback deliberately does without.
                    return ExtrusionOf(boolean.FirstOperand as IIfcRepresentationItem, depth + 1);

                default:
                    return null;
            }
        }

        /// <summary>
        /// IFC entity to Revit category. Kept deliberately coarse: the point is that the result is
        /// filterable, not that every IFC subtype gets its own home.
        /// </summary>
        public static ElementId CategoryFor(IIfcProduct product)
        {
            var name = IfcHelpers.EntityName(product) ?? string.Empty;

            BuiltInCategory category;
            switch (name)
            {
                case "IfcWall":
                case "IfcWallStandardCase":
                case "IfcWallElementedCase": category = BuiltInCategory.OST_Walls; break;

                case "IfcSlab":
                case "IfcSlabStandardCase":
                case "IfcSlabElementedCase": category = BuiltInCategory.OST_Floors; break;

                case "IfcRoof": category = BuiltInCategory.OST_Roofs; break;

                case "IfcColumn":
                case "IfcColumnStandardCase": category = BuiltInCategory.OST_StructuralColumns; break;

                case "IfcBeam":
                case "IfcBeamStandardCase":
                case "IfcMember":
                case "IfcMemberStandardCase": category = BuiltInCategory.OST_StructuralFraming; break;

                case "IfcPlate":
                case "IfcPlateStandardCase": category = BuiltInCategory.OST_StructuralStiffener; break;

                case "IfcDoor":
                case "IfcDoorStandardCase": category = BuiltInCategory.OST_Doors; break;

                case "IfcWindow":
                case "IfcWindowStandardCase": category = BuiltInCategory.OST_Windows; break;

                case "IfcStair":
                case "IfcStairFlight": category = BuiltInCategory.OST_Stairs; break;

                case "IfcRailing": category = BuiltInCategory.OST_StairsRailing; break;
                case "IfcRamp":
                case "IfcRampFlight": category = BuiltInCategory.OST_Ramps; break;
                case "IfcCovering": category = BuiltInCategory.OST_Ceilings; break;
                case "IfcCurtainWall": category = BuiltInCategory.OST_Curtain_Systems; break;
                case "IfcFooting":
                case "IfcPile": category = BuiltInCategory.OST_StructuralFoundation; break;

                case "IfcMechanicalFastener":
                case "IfcFastener":
                case "IfcDiscreteAccessory": category = BuiltInCategory.OST_StructConnections; break;

                case "IfcFlowSegment":
                case "IfcPipeSegment": category = BuiltInCategory.OST_PipeCurves; break;
                case "IfcDuctSegment": category = BuiltInCategory.OST_DuctCurves; break;
                case "IfcFlowTerminal": category = BuiltInCategory.OST_PlumbingFixtures; break;

                default: category = BuiltInCategory.OST_GenericModel; break;
            }

            return new ElementId(category);
        }
    }
}
