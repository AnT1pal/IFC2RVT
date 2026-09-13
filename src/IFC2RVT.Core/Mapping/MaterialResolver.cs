using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Mapping
{
    /// <summary>
    /// Matches IFC material names to Revit materials, creating them on demand.
    /// Names are the only reliable bridge here: IFC carries no Revit material identity, and
    /// IfcSurfaceStyle colours describe presentation rather than the material itself.
    /// </summary>
    public class MaterialResolver
    {
        readonly Document _doc;
        readonly Dictionary<string, ElementId> _cache =
            new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

        public int MaterialsCreated { get; private set; }

        public MaterialResolver(Document doc)
        {
            _doc = doc;
            foreach (var m in new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>())
                _cache[m.Name] = m.Id;
        }

        /// <summary>Surface styles from the IFC, used to colour what would otherwise be default grey.</summary>
        public StyleReader Styles { get; set; }

        /// <summary>Must run inside a transaction when the material does not exist yet.</summary>
        public ElementId Resolve(string name, Shade shade = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return ElementId.InvalidElementId;
            name = name.Trim();

            if (_cache.TryGetValue(name, out var existing)) return existing;

            try
            {
                var id = Material.Create(_doc, SanitiseName(name));
                _cache[name] = id;
                MaterialsCreated++;
                Paint(id, shade);
                return id;
            }
            catch
            {
                _cache[name] = ElementId.InvalidElementId;
                return ElementId.InvalidElementId;
            }
        }

        public ElementId Resolve(IIfcMaterial material)
            => material == null
                ? ElementId.InvalidElementId
                : Resolve((string)material.Name, Styles?.ForMaterial(material));

        /// <summary>
        /// Material for a bare colour, used when geometry carries a surface style but the element
        /// declares no named material. One Revit material per distinct colour, so a model does not
        /// end up with thousands of identical ones.
        /// </summary>
        public ElementId ResolveShade(Shade shade)
        {
            if (shade == null) return ElementId.InvalidElementId;

            var name = shade.ToString();
            if (_cache.TryGetValue(name, out var existing)) return existing;

            try
            {
                var id = Material.Create(_doc, SanitiseName(name));
                _cache[name] = id;
                MaterialsCreated++;
                Paint(id, shade);
                return id;
            }
            catch
            {
                _cache[name] = ElementId.InvalidElementId;
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>
        /// Applies the IFC colour to a freshly created material. UseRenderAppearanceForShading is
        /// turned off deliberately: left on, Revit shades from the generic render appearance and the
        /// colour just set is ignored in shaded views, which is where the model is actually looked at.
        /// </summary>
        void Paint(ElementId id, Shade shade)
        {
            if (shade == null || id == ElementId.InvalidElementId) return;

            try
            {
                if (!(_doc.GetElement(id) is Material material)) return;

                material.Color = new Color(shade.R, shade.G, shade.B);
                material.Transparency = shade.Transparency;
                material.UseRenderAppearanceForShading = false;

                ApplyAppearance(material, shade);
                MaterialsColoured++;
            }
            catch { }
        }

        /// <summary>
        /// Colours the render appearance as well as the shading colour.
        ///
        /// Material.Color drives the Shaded view only. The Realistic view reads the appearance
        /// asset, and a material created through the API gets Revit's stock generic asset - which
        /// is beige. That is why an imported model whose IFC says RGB(161,161,160) still renders
        /// warm orange until the asset itself is recoloured.
        /// </summary>
        void ApplyAppearance(Material material, Shade shade)
        {
            try
            {
                var assetId = EnsureAppearanceAsset(material, shade);
                if (assetId == ElementId.InvalidElementId) return;

                material.AppearanceAssetId = assetId;

                using (var scope = new AppearanceAssetEditScope(_doc))
                {
                    var editable = scope.Start(assetId);
                    if (editable == null) { scope.Cancel(); return; }

                    var diffuse = editable.FindByName("generic_diffuse") as AssetPropertyDoubleArray4d;
                    if (diffuse == null || diffuse.IsReadOnly) { scope.Cancel(); return; }

                    diffuse.SetValueAsColor(new Color(shade.R, shade.G, shade.B));

                    if (shade.Transparency > 0 &&
                        editable.FindByName("generic_transparency") is AssetPropertyDouble transparency &&
                        !transparency.IsReadOnly)
                        transparency.Value = shade.Transparency / 100.0;

                    scope.Commit(true);
                }
            }
            catch
            {
                // An unrecoloured appearance still leaves a correct Shaded view.
            }
        }

        ElementId EnsureAppearanceAsset(Material material, Shade shade)
        {
            var name = "IFC " + shade.Key;

            var existing = new FilteredElementCollector(_doc)
                .OfClass(typeof(AppearanceAssetElement))
                .Cast<AppearanceAssetElement>()
                .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.Id;

            // Duplicating the material's own asset keeps whatever shader it already uses; falling
            // back to any generic asset in the project covers a material created without one.
            var source = material.AppearanceAssetId != ElementId.InvalidElementId
                ? _doc.GetElement(material.AppearanceAssetId) as AppearanceAssetElement
                : new FilteredElementCollector(_doc).OfClass(typeof(AppearanceAssetElement))
                    .Cast<AppearanceAssetElement>().FirstOrDefault();

            if (source == null) return ElementId.InvalidElementId;

            try { return source.Duplicate(name).Id; }
            catch { return ElementId.InvalidElementId; }
        }

        public int MaterialsColoured { get; private set; }

        /// <summary>Material of a single layer, following the IFC4 material-constituent indirection.</summary>
        public ElementId Resolve(IIfcMaterialLayer layer)
        {
            if (layer == null) return ElementId.InvalidElementId;
            return Resolve(layer.Material);
        }

        /// <summary>Best single material for an element, used when a full layer set is not available.</summary>
        public ElementId PrimaryMaterialOf(IIfcObjectDefinition o)
        {
            if (o == null) return ElementId.InvalidElementId;

            foreach (var rel in o.HasAssociations.OfType<IIfcRelAssociatesMaterial>())
            {
                switch (rel.RelatingMaterial)
                {
                    case IIfcMaterial m:
                        return Resolve(m);

                    case IIfcMaterialLayerSetUsage u:
                        return ThickestOf(u.ForLayerSet);

                    case IIfcMaterialLayerSet ls:
                        return ThickestOf(ls);

                    case IIfcMaterialConstituentSet cs:
                    {
                        var first = cs.MaterialConstituents.FirstOrDefault();
                        if (first != null) return Resolve(first.Material);
                        break;
                    }

                    case IIfcMaterialProfileSet ps:
                    {
                        var first = ps.MaterialProfiles.FirstOrDefault();
                        if (first != null) return Resolve(first.Material);
                        break;
                    }

                    case IIfcMaterialList list:
                    {
                        var first = list.Materials.FirstOrDefault();
                        if (first != null) return Resolve(first);
                        break;
                    }
                }
            }
            return ElementId.InvalidElementId;
        }

        ElementId ThickestOf(IIfcMaterialLayerSet ls)
        {
            if (ls == null) return ElementId.InvalidElementId;
            var thickest = ls.MaterialLayers
                             .OrderByDescending(l => (double)l.LayerThickness)
                             .FirstOrDefault();
            return Resolve(thickest);
        }

        /// <summary>Revit rejects these characters in element names.</summary>
        public static string SanitiseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "IFC";
            var cleaned = new string(name.Select(c =>
                c == '{' || c == '}' || c == '[' || c == ']' || c == '|' ||
                c == ';' || c == '<' || c == '>' || c == '?' || c == '`' ||
                c == '~' || c == ':' || c == '\\' ? '_' : c).ToArray()).Trim();

            if (cleaned.Length == 0) cleaned = "IFC";
            return cleaned.Length > 200 ? cleaned.Substring(0, 200) : cleaned;
        }
    }
}
