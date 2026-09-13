using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Mapping
{
    /// <summary>
    /// Maps IfcBuildingStorey onto Revit levels, reusing existing levels within tolerance so a
    /// repeated import does not litter the project with near-duplicates.
    ///
    /// Spatial containment is unreliable in practice - the sample model routes only 10 of ~17000
    /// products through IfcRelContainedInSpatialStructure, the rest hang off element assemblies -
    /// so <see cref="NearestLevel"/> provides an elevation-based fallback.
    /// </summary>
    public class LevelMapper
    {
        readonly Document _doc;
        readonly IfcScale _scale;
        readonly PlacementResolver _placements;
        readonly double _toleranceFeet;

        readonly Dictionary<int, Level> _byStorey = new Dictionary<int, Level>();
        readonly Dictionary<int, int> _productToStorey = new Dictionary<int, int>();
        List<Level> _sorted;

        public int LevelsCreated { get; private set; }

        public LevelMapper(Document doc, IfcScale scale, PlacementResolver placements, double toleranceMm)
        {
            _doc = doc;
            _scale = scale;
            _placements = placements;
            _toleranceFeet = scale.MmToFeet(toleranceMm);
        }

        /// <summary>Must run inside a transaction: it may create levels.</summary>
        public void Build(IfcModelContext context)
        {
            var existing = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();

            foreach (var storey in context.Storeys)
            {
                var elevation = StoreyElevation(storey);
                var match = existing.FirstOrDefault(l => Math.Abs(l.Elevation - elevation) <= _toleranceFeet);

                if (match == null)
                {
                    match = Level.Create(_doc, elevation);
                    var name = IfcHelpers.Str(storey.Name);
                    if (!string.IsNullOrWhiteSpace(name)) TrySetName(match, name);
                    existing.Add(match);
                    LevelsCreated++;
                }

                _byStorey[storey.EntityLabel] = match;
            }

            // An IFC with no storeys at all still needs somewhere to put elements.
            if (existing.Count == 0)
            {
                var fallback = Level.Create(_doc, 0.0);
                TrySetName(fallback, "IFC Level 0");
                existing.Add(fallback);
                LevelsCreated++;
            }

            _sorted = existing.OrderBy(l => l.Elevation).ToList();
            IndexContainment(context);
        }

        void TrySetName(Level level, string name)
        {
            // Level names must be unique; fall back to the auto-generated one on collision.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    level.Name = attempt == 0 ? name : $"{name} ({attempt + 1})";
                    return;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return; }
            }
        }

        double StoreyElevation(IIfcBuildingStorey storey)
        {
            // The placement carries the authoritative Z; Elevation is relative to the site and
            // is frequently left at zero by exporters.
            var placed = _placements.Resolve(storey.ObjectPlacement);
            if (placed != null && !placed.Origin.IsZeroLength()) return placed.Origin.Z;
            return storey.Elevation.HasValue ? (double)storey.Elevation.Value * _scale.Length : 0.0;
        }

        void IndexContainment(IfcModelContext context)
        {
            foreach (var rel in context.SpatialContainment)
            {
                if (!(rel.RelatingStructure is IIfcBuildingStorey storey)) continue;
                foreach (var e in rel.RelatedElements)
                    _productToStorey[e.EntityLabel] = storey.EntityLabel;
            }
        }

        /// <summary>Level for a product, preferring declared containment over elevation guessing.</summary>
        public Level For(IIfcProduct product, XYZ locationHint = null)
        {
            if (product != null
                && _productToStorey.TryGetValue(product.EntityLabel, out var storeyLabel)
                && _byStorey.TryGetValue(storeyLabel, out var declared))
                return declared;

            var z = locationHint?.Z ?? _placements.WorldTransform(product).Origin.Z;
            return NearestLevel(z);
        }

        /// <summary>Highest level at or below the given elevation, or the lowest level overall.</summary>
        public Level NearestLevel(double elevationFeet)
        {
            if (_sorted == null || _sorted.Count == 0) return null;

            Level best = _sorted[0];
            foreach (var l in _sorted)
            {
                if (l.Elevation <= elevationFeet + _toleranceFeet) best = l;
                else break;
            }
            return best;
        }

        public Level Lowest => _sorted != null && _sorted.Count > 0 ? _sorted[0] : null;

        /// <summary>Level directly above the given one, used to derive wall heights.</summary>
        public Level Above(Level level)
        {
            if (_sorted == null || level == null) return null;
            return _sorted.FirstOrDefault(l => l.Elevation > level.Elevation + _toleranceFeet);
        }
    }
}
