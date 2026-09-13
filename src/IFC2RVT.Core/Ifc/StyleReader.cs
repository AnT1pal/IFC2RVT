using System;
using System.Collections.Generic;
using System.Linq;
using Xbim.Common;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Ifc
{
    /// <summary>Surface colour and transparency as the exporter declared them.</summary>
    public class Shade
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }

        /// <summary>0 = opaque, 100 = invisible, matching the Revit material scale.</summary>
        public int Transparency { get; set; }

        public string Key => $"{R},{G},{B},{Transparency}";

        public override string ToString() => $"IFC RGB {R}-{G}-{B}";
    }

    /// <summary>
    /// Reads IfcSurfaceStyle colours and attaches them to the geometry and materials they belong to.
    ///
    /// Without this every imported element lands on a default grey Revit material, because material
    /// names alone carry no appearance. The sample model holds 92 IfcColourRgb and 4913 IfcStyledItem
    /// - all of the colour a viewer shows lives there, and none of it reaches Revit unless it is
    /// read explicitly.
    /// </summary>
    public class StyleReader
    {
        readonly Dictionary<int, Shade> _byItem = new Dictionary<int, Shade>();
        readonly Dictionary<int, Shade> _byMaterial = new Dictionary<int, Shade>();

        public int StyledItems => _byItem.Count;
        public int StyledMaterials => _byMaterial.Count;

        public StyleReader(IModel model)
        {
            foreach (var styled in model.Instances.OfType<IIfcStyledItem>())
            {
                var shade = ShadeOf(styled);
                if (shade == null) continue;

                // Item is optional: a styled item with no target styles the material instead.
                if (styled.Item != null) _byItem[styled.Item.EntityLabel] = shade;
            }

            foreach (var definition in model.Instances.OfType<IIfcMaterialDefinitionRepresentation>())
            {
                var material = definition.RepresentedMaterial;
                if (material == null) continue;

                var shade = definition.Representations
                    .SelectMany(r => r.Items.OfType<IIfcStyledItem>())
                    .Select(ShadeOf)
                    .FirstOrDefault(s => s != null);

                if (shade != null) _byMaterial[material.EntityLabel] = shade;
            }
        }

        public Shade ForItem(IIfcRepresentationItem item)
            => item != null && _byItem.TryGetValue(item.EntityLabel, out var shade) ? shade : null;

        public Shade ForMaterial(IIfcMaterial material)
            => material != null && _byMaterial.TryGetValue(material.EntityLabel, out var shade) ? shade : null;

        static Shade ShadeOf(IIfcStyledItem styled)
        {
            foreach (var style in styled.Styles)
            {
                var shade = FromAssignment(style);
                if (shade != null) return shade;
            }
            return null;
        }

        static Shade FromAssignment(IIfcStyleAssignmentSelect style)
        {
            switch (style)
            {
                case IIfcSurfaceStyle surface:
                    return FromSurfaceStyle(surface);

                case IIfcPresentationStyleAssignment assignment:
                    // Deprecated in IFC4 but still emitted by older exporters.
                    foreach (var nested in assignment.Styles)
                    {
                        if (nested is IIfcSurfaceStyle nestedSurface)
                        {
                            var shade = FromSurfaceStyle(nestedSurface);
                            if (shade != null) return shade;
                        }
                    }
                    return null;

                default:
                    return null;
            }
        }

        static Shade FromSurfaceStyle(IIfcSurfaceStyle surface)
        {
            foreach (var element in surface.Styles)
            {
                if (!(element is IIfcSurfaceStyleShading shading)) continue;

                var colour = shading.SurfaceColour;
                if (colour == null) continue;

                var transparency = 0.0;
                if (shading is IIfcSurfaceStyleRendering rendering && rendering.Transparency.HasValue)
                    transparency = (double)rendering.Transparency.Value;

                return new Shade
                {
                    R = ToByte(colour.Red),
                    G = ToByte(colour.Green),
                    B = ToByte(colour.Blue),
                    Transparency = Clamp((int)Math.Round(transparency * 100.0))
                };
            }
            return null;
        }

        static byte ToByte(Xbim.Ifc4.MeasureResource.IfcNormalisedRatioMeasure value)
        {
            var scaled = (int)Math.Round((double)value * 255.0);
            return (byte)(scaled < 0 ? 0 : scaled > 255 ? 255 : scaled);
        }

        static int Clamp(int value) => value < 0 ? 0 : value > 100 ? 100 : value;
    }
}
