using System;
using System.Collections.Generic;
using System.Linq;
using Xbim.Common;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Ifc
{
    /// <summary>
    /// Everything read once from the IFC and shared by every builder: the readers, the unit scale,
    /// and the relationship indexes that would otherwise cost a full model scan per element.
    ///
    /// Scans here are deliberately eager. On a 266 MB file a lazy per-element
    /// <c>Instances.OfType</c> walk is what turns a two-minute conversion into an hour.
    /// </summary>
    public class IfcModelContext : IDisposable
    {
        readonly IfcStore _store;
        bool _disposed;

        public IModel Model => _store;
        public IfcScale Scale { get; }
        public PlacementResolver Placements { get; }
        public CurveReader Curves { get; }
        public ProfileReader Profiles { get; }
        public RepresentationReader Representations { get; }
        public StyleReader Styles { get; }

        public string SourceApplication { get; }
        public string SourceSchema { get; }

        public List<IIfcBuildingStorey> Storeys { get; }
        public List<IIfcRelContainedInSpatialStructure> SpatialContainment { get; }

        /// <summary>Opening -> the element filling it (door or window).</summary>
        public Dictionary<int, IIfcElement> OpeningFillings { get; }

        /// <summary>Opening -> the element it was cut from (usually a wall).</summary>
        public Dictionary<int, IIfcElement> OpeningHosts { get; }

        /// <summary>
        /// Wall -> which of its ends the model says are joined to another wall.
        ///
        /// Revit re-joins walls on its own and reshapes their ends while doing so, which quietly
        /// contradicts geometry the authoring tool had already resolved. Knowing where a join was
        /// actually intended means only those ends need to stay open.
        /// </summary>
        public Dictionary<int, HashSet<IfcConnectionTypeEnum>> WallConnections { get; }

        public IfcModelContext(string path, double shortCurveTolerance)
        {
            _store = IfcStore.Open(path);

            Scale = new IfcScale(_store);
            Placements = new PlacementResolver(Scale);
            Curves = new CurveReader(Scale, Placements, shortCurveTolerance);
            Profiles = new ProfileReader(Scale, Curves, Placements, shortCurveTolerance);
            Representations = new RepresentationReader(Scale, Placements, Curves);
            Styles = new StyleReader(_store);

            SourceSchema = _store.SchemaVersion.ToString();
            SourceApplication = ReadApplication();

            Storeys = _store.Instances.OfType<IIfcBuildingStorey>().ToList();
            SpatialContainment = _store.Instances.OfType<IIfcRelContainedInSpatialStructure>().ToList();

            OpeningFillings = new Dictionary<int, IIfcElement>();
            OpeningHosts = new Dictionary<int, IIfcElement>();
            WallConnections = new Dictionary<int, HashSet<IfcConnectionTypeEnum>>();
            IndexOpenings();
            IndexConnections();
        }

        string ReadApplication()
        {
            try
            {
                var h = _store.Header?.FileName;
                var app = h?.OriginatingSystem;
                var tool = h?.PreprocessorVersion;
                if (!string.IsNullOrWhiteSpace(app) && !string.IsNullOrWhiteSpace(tool))
                    return app + " / " + tool;
                return app ?? tool ?? "(unknown)";
            }
            catch
            {
                return "(unknown)";
            }
        }

        void IndexOpenings()
        {
            foreach (var rel in _store.Instances.OfType<IIfcRelVoidsElement>())
            {
                var opening = rel.RelatedOpeningElement;
                var host = rel.RelatingBuildingElement;
                if (opening != null && host != null) OpeningHosts[opening.EntityLabel] = host;
            }

            foreach (var rel in _store.Instances.OfType<IIfcRelFillsElement>())
            {
                var opening = rel.RelatingOpeningElement;
                var filling = rel.RelatedBuildingElement;
                if (opening != null && filling != null) OpeningFillings[opening.EntityLabel] = filling;
            }
        }

        void IndexConnections()
        {
            foreach (var rel in _store.Instances.OfType<IIfcRelConnectsPathElements>())
            {
                Record(rel.RelatingElement, rel.RelatingConnectionType);
                Record(rel.RelatedElement, rel.RelatedConnectionType);
            }
        }

        void Record(IIfcElement element, IfcConnectionTypeEnum end)
        {
            if (element == null) return;

            if (!WallConnections.TryGetValue(element.EntityLabel, out var ends))
                WallConnections[element.EntityLabel] = ends = new HashSet<IfcConnectionTypeEnum>();

            ends.Add(end);
        }

        /// <summary>
        /// True when the model declares a join at the given end of a wall. ATSTART and ATEND map
        /// onto the two Revit wall ends; ATPATH is a T-junction along the run and does not.
        /// </summary>
        public bool WallJoinsAt(IIfcElement wall, int end)
        {
            if (wall == null) return false;
            if (!WallConnections.TryGetValue(wall.EntityLabel, out var ends)) return false;

            var wanted = end == 0 ? IfcConnectionTypeEnum.ATSTART : IfcConnectionTypeEnum.ATEND;
            return ends.Contains(wanted);
        }

        /// <summary>The opening a door or window sits in, if the exporter declared one.</summary>
        public IIfcOpeningElement OpeningFor(IIfcElement filling)
        {
            if (filling == null) return null;
            return filling.FillsVoids.Select(r => r.RelatingOpeningElement).FirstOrDefault();
        }

        /// <summary>The element a door or window is cut into, following opening -> host.</summary>
        public IIfcElement HostOf(IIfcElement filling)
        {
            var opening = OpeningFor(filling);
            if (opening == null) return null;
            return OpeningHosts.TryGetValue(opening.EntityLabel, out var host) ? host : null;
        }

        /// <summary>
        /// World origin of the spatial structure, in feet. Models exported in site or survey
        /// coordinates put this tens of kilometres out; past roughly 16 km Revit can no longer
        /// shade faces and the import draws as bare edges even though every element exists.
        ///
        /// Prefers the site, then the building, then the lowest product placement, so the result
        /// is a point the model sits on rather than an arbitrary corner of its bounding box.
        /// </summary>
        public Autodesk.Revit.DB.XYZ SpatialOrigin()
        {
            var site = _store.Instances.OfType<IIfcSite>().FirstOrDefault();
            if (site?.ObjectPlacement != null)
                return Placements.Resolve(site.ObjectPlacement).Origin;

            var building = _store.Instances.OfType<IIfcBuilding>().FirstOrDefault();
            if (building?.ObjectPlacement != null)
                return Placements.Resolve(building.ObjectPlacement).Origin;

            double x = double.MaxValue, y = double.MaxValue, z = double.MaxValue;
            var found = false;

            foreach (var product in _store.Instances.OfType<IIfcProduct>())
            {
                if (product.ObjectPlacement == null) continue;
                var o = Placements.Resolve(product.ObjectPlacement).Origin;
                x = Math.Min(x, o.X);
                y = Math.Min(y, o.Y);
                z = Math.Min(z, o.Z);
                found = true;
            }

            return found ? new Autodesk.Revit.DB.XYZ(x, y, z) : Autodesk.Revit.DB.XYZ.Zero;
        }

        public IEnumerable<T> All<T>() where T : IPersistEntity => _store.Instances.OfType<T>();

        public long CountOf<T>() where T : IPersistEntity => _store.Instances.CountOf<T>();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _store?.Dispose();
        }
    }
}
