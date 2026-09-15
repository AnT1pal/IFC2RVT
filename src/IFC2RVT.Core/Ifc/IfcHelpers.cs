using System.Collections.Generic;
using System.Linq;
using Xbim.Common;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Ifc
{
    /// <summary>
    /// Small readers for the awkward corners of the xBIM interface layer: nullable IfcLabel
    /// wrappers, coordinate item-sets, and the SELECT types that can hold more than one thing.
    /// </summary>
    public static class IfcHelpers
    {
        public static string Str(Xbim.Ifc4.MeasureResource.IfcLabel? v)
            => v.HasValue ? (string)v.Value : null;

        public static string Str(Xbim.Ifc4.MeasureResource.IfcIdentifier? v)
            => v.HasValue ? (string)v.Value : null;

        public static string Str(Xbim.Ifc4.MeasureResource.IfcText? v)
            => v.HasValue ? (string)v.Value : null;

        /// <summary>Entity type name as it appears in the STEP file, e.g. "IfcWallStandardCase".</summary>
        public static string EntityName(IPersistEntity e)
            => e?.ExpressType?.ExpressName ?? e?.GetType().Name;

        public static double[] Coords(IIfcCartesianPoint p)
        {
            if (p == null) return new[] { 0.0, 0.0, 0.0 };
            var c = p.Coordinates;
            var r = new[] { 0.0, 0.0, 0.0 };
            for (int i = 0; i < 3 && i < c.Count; i++) r[i] = c[i];
            return r;
        }

        public static double[] Ratios(IIfcDirection d, double[] fallback)
        {
            if (d == null) return fallback;
            var c = d.DirectionRatios;
            var r = new[] { 0.0, 0.0, 0.0 };
            for (int i = 0; i < 3 && i < c.Count; i++) r[i] = c[i];
            if (c.Count == 2) r[2] = 0.0;
            return r;
        }

        /// <summary>IIfcPlacement.Location widened to IIfcPoint in IFC4x3; narrow it back.</summary>
        public static IIfcCartesianPoint LocationOf(IIfcPlacement p)
            => p?.Location as IIfcCartesianPoint;

        /// <summary>All property sets on the instance, optionally including those on its type.</summary>
        public static IEnumerable<IIfcPropertySet> PropertySets(IIfcObject obj, bool includeType)
        {
            if (obj == null) yield break;

            foreach (var rel in obj.IsDefinedBy)
                if (rel.RelatingPropertyDefinition is IIfcPropertySet ps)
                    yield return ps;

            if (!includeType) yield break;

            foreach (var t in obj.IsTypedBy.Select(r => r.RelatingType).Where(t => t != null))
                foreach (var ps in t.HasPropertySets.OfType<IIfcPropertySet>())
                    yield return ps;
        }

        /// <summary>Quantity sets (Qto_*) carry areas/volumes/lengths the property sets often omit.</summary>
        public static IEnumerable<IIfcElementQuantity> QuantitySets(IIfcObject obj)
        {
            if (obj == null) yield break;
            foreach (var rel in obj.IsDefinedBy)
                if (rel.RelatingPropertyDefinition is IIfcElementQuantity q)
                    yield return q;
        }

        /// <summary>Material association, resolving the layer-set-usage indirection walls use.</summary>
        public static IIfcMaterialLayerSet LayerSet(IIfcObjectDefinition o)
        {
            if (o == null) return null;
            foreach (var rel in o.HasAssociations.OfType<IIfcRelAssociatesMaterial>())
            {
                switch (rel.RelatingMaterial)
                {
                    case IIfcMaterialLayerSetUsage u: return u.ForLayerSet;
                    case IIfcMaterialLayerSet ls: return ls;
                }
            }
            return null;
        }

        public static IIfcMaterialLayerSetUsage LayerSetUsage(IIfcObjectDefinition o)
        {
            if (o == null) return null;
            foreach (var rel in o.HasAssociations.OfType<IIfcRelAssociatesMaterial>())
                if (rel.RelatingMaterial is IIfcMaterialLayerSetUsage u) return u;
            return null;
        }

        /// <summary>
        /// Structural profile associated with an element, following the usage indirection.
        ///
        /// This is the real thing: a parametric IfcProfileDef with named dimensions. Reading the
        /// section out of IfcBeam.Description instead - which is where an exporter writes "L50X3"
        /// as loose text - gives a string to match on and nothing to measure.
        /// </summary>
        public static IIfcProfileDef StructuralProfile(IIfcObjectDefinition o)
        {
            if (o == null) return null;

            foreach (var rel in o.HasAssociations.OfType<IIfcRelAssociatesMaterial>())
            {
                IIfcMaterialProfileSet set = null;

                switch (rel.RelatingMaterial)
                {
                    case IIfcMaterialProfileSetUsage usage: set = usage.ForProfileSet; break;
                    case IIfcMaterialProfileSet direct: set = direct; break;
                }

                var profile = set?.MaterialProfiles
                                  .Select(p => p.Profile)
                                  .FirstOrDefault(p => p != null);
                if (profile != null) return profile;
            }
            return null;
        }

        /// <summary>Profile designation as the authoring tool named it, e.g. "L50X3".</summary>
        public static string ProfileName(IIfcProfileDef profile)
            => profile == null ? null : Str(profile.ProfileName);

        /// <summary>Total thickness of a layer set in raw IFC units, or 0 when unknown.</summary>
        public static double TotalThickness(IIfcMaterialLayerSet ls)
            => ls == null ? 0.0 : ls.MaterialLayers.Sum(l => (double)l.LayerThickness);
    }
}
