using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using IFC2RVT.Conversion;
using IFC2RVT.Ifc;
using IFC2RVT.Mapping;
using IFC2RVT.Parameters;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>Outcome of one attempt to create a native element.</summary>
    public class BuildResult
    {
        public Element Element { get; set; }
        public string Message { get; set; }
        public bool Succeeded => Element != null;

        public static BuildResult Ok(Element e) => new BuildResult { Element = e };
        public static BuildResult Fail(string message) => new BuildResult { Message = message };

        public static readonly BuildResult NotApplicable = new BuildResult { Message = "нет нативного построителя" };
    }

    public interface IElementBuilder
    {
        /// <summary>Revit category this builder produces, for reporting.</summary>
        string CategoryName { get; }

        bool CanBuild(IIfcProduct product);

        /// <summary>Runs inside an open transaction.</summary>
        BuildResult Build(IIfcProduct product);
    }

    /// <summary>
    /// Services every builder needs, plus the cross-element bookkeeping: doors have to find the
    /// wall that was created for their IFC host, so the guid to element map is shared state and
    /// walls must be built before openings.
    /// </summary>
    public class BuilderContext
    {
        public Document Doc { get; }
        public IfcModelContext Ifc { get; }
        public LevelMapper Levels { get; }
        public TypeResolver Types { get; }
        public MaterialResolver Materials { get; }
        public ConversionOptions Options { get; }

        /// <summary>
        /// Framing families generated from the outlines in the file, one per steel section. Null
        /// when the feature is off or no template was found, in which case beams behave as before.
        /// </summary>
        public SectionFamilyFactory Sections { get; set; }

        public double ShortCurveTolerance { get; }

        readonly Dictionary<string, ElementId> _byGuid = new Dictionary<string, ElementId>(StringComparer.Ordinal);

        public BuilderContext(Document doc, IfcModelContext ifc, LevelMapper levels,
                              TypeResolver types, MaterialResolver materials, ConversionOptions options)
        {
            Doc = doc;
            Ifc = ifc;
            Levels = levels;
            Types = types;
            Materials = materials;
            Options = options;
            ShortCurveTolerance = doc.Application.ShortCurveTolerance;
        }

        public void Register(IIfcProduct product, Element element)
        {
            if (product == null || element == null) return;
            _byGuid[product.GlobalId.ToString()] = element.Id;
        }

        public Element Created(IIfcProduct product)
        {
            if (product == null) return null;
            return _byGuid.TryGetValue(product.GlobalId.ToString(), out var id) ? Doc.GetElement(id) : null;
        }

        /// <summary>
        /// Sections the model asked for that the project has no family for, with how many members
        /// wanted each. Collected during conversion and printed as a shopping list: a report that
        /// names the standard is actionable, a wall of identical failures is not.
        /// </summary>
        public Dictionary<string, MissingSection> MissingSections { get; }
            = new Dictionary<string, MissingSection>(StringComparer.OrdinalIgnoreCase);

        public class MissingSection
        {
            public string Designation { get; set; }
            public string Kind { get; set; }
            public string Standard { get; set; }
            public int Count { get; set; }
        }

        public void NoteMissingSection(string designation, string kind, string standard)
        {
            if (string.IsNullOrWhiteSpace(designation)) return;

            if (!MissingSections.TryGetValue(designation, out var entry))
                MissingSections[designation] = entry = new MissingSection
                {
                    Designation = designation,
                    Kind = kind,
                    Standard = standard
                };

            entry.Count++;
        }

        public RevitTypeName NameOf(IIfcProduct product)
            => RevitTypeName.Parse(IfcHelpers.Str(product?.Name));

        /// <summary>
        /// Type name as exported on the IfcTypeObject, which is the cleaner "Family:Type" form
        /// when the instance name has been overridden.
        /// </summary>
        public RevitTypeName TypeNameOf(IIfcProduct product)
        {
            if (!(product is IIfcObject obj)) return NameOf(product);

            foreach (var rel in obj.IsTypedBy)
            {
                var typeName = IfcHelpers.Str(rel.RelatingType?.Name);
                if (!string.IsNullOrWhiteSpace(typeName)) return RevitTypeName.Parse(typeName);
            }
            return NameOf(product);
        }

        /// <summary>Element id as a long across API versions.</summary>
        public static long IdValue(ElementId id)
        {
            if (id == null) return 0;
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static ElementId CategoryId(BuiltInCategory category) => new ElementId(category);
    }
}
