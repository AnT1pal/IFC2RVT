using System.Collections.Generic;

namespace IFC2RVT.Conversion
{
    /// <summary>What the user asked the converter to do. Serialised alongside the mapping config.</summary>
    public class ConversionOptions
    {
        public string IfcFilePath { get; set; }

        // Element families to attempt natively. Anything switched off still arrives as DirectShape
        // when <see cref="FallbackToDirectShape"/> is on, so the model is never left with holes.
        public bool ConvertWalls { get; set; } = true;
        public bool ConvertSlabs { get; set; } = true;
        public bool ConvertColumns { get; set; } = true;
        public bool ConvertBeams { get; set; } = false;
        public bool ConvertOpenings { get; set; } = true;
        public bool ConvertSpaces { get; set; } = true;
        public bool ConvertCeilings { get; set; } = true;

        /// <summary>
        /// Create project grids from IfcGrid. A drawing is dimensioned from its grids, so an
        /// import without them is awkward even when every wall came through perfectly.
        /// </summary>
        public bool ConvertGrids { get; set; } = true;

        /// <summary>
        /// Cut IfcOpeningElement that nothing fills into its host: niches, service penetrations,
        /// shafts. Without this the host keeps the holes the source model had pierced through it.
        /// </summary>
        public bool ConvertOpeningVoids { get; set; } = true;

        /// <summary>
        /// Store geometry that many elements share once, as a DirectShapeType, instead of copying
        /// it per element. Detailing models are mostly the same handful of bolts and brackets
        /// repeated thousands of times, and copying each one is what makes them enormous.
        /// </summary>
        public bool ReuseSharedGeometry { get; set; } = true;

        /// <summary>Create DirectShape for everything that has no native builder or whose builder failed.</summary>
        public bool FallbackToDirectShape { get; set; } = true;

        /// <summary>Copy IfcPropertySet contents into shared parameters.</summary>
        public bool TransferProperties { get; set; } = true;

        /// <summary>Also pull property sets attached to the IfcTypeObject, not just the instance.</summary>
        public bool IncludeTypeProperties { get; set; } = true;

        /// <summary>
        /// Match Revit types by the "Family:Type:Id" name Revit writes into IfcRoot.Name on export.
        /// Only meaningful for IFC that originated in Revit; harmless otherwise.
        /// </summary>
        public bool UseRevitNameHeuristic { get; set; } = true;

        /// <summary>Duplicate the closest existing type when no exact match is found.</summary>
        public bool CreateMissingTypes { get; set; } = true;

        /// <summary>
        /// Shift the model so it sits near the Revit origin.
        /// Models exported in site or survey coordinates land tens of kilometres out, and past
        /// roughly 16 km Revit loses the precision it needs to shade faces - the geometry is
        /// created correctly but draws as bare edges.
        /// </summary>
        public bool MoveToOrigin { get; set; } = true;

        /// <summary>Only shift when the model is farther than this from the origin, in metres.</summary>
        public double OriginThresholdMetres { get; set; } = 1000.0;

        /// <summary>Reuse levels within this tolerance (mm) before creating a new one.</summary>
        public double LevelToleranceMm { get; set; } = 50.0;

        /// <summary>Elements committed per transaction. Large models need this or Revit stalls.</summary>
        public int TransactionBatchSize { get; set; } = 500;

        /// <summary>Optional cap for smoke-testing against huge files. 0 = no limit.</summary>
        public int MaxElements { get; set; } = 0;

        public HashSet<string> SkipIfcTypes { get; set; } = new HashSet<string>();
    }
}
