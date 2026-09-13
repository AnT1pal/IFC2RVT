using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using IFC2RVT.Ifc;
using Xbim.Common;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Parameters
{
    /// <summary>
    /// Copies IFC property and quantity sets onto the created Revit elements.
    ///
    /// Measures are mapped to real Revit specs rather than dumped as text, so RUS_Area and friends
    /// stay schedulable and summable. Lengths, areas and volumes are converted out of the file
    /// units into Revit internal units on the way in.
    /// </summary>
    public class PropertyTransfer
    {
        public const string GuidParameter = "IFC GUID";
        public const string EntityParameter = "IFC Entity";
        public const string TypeNameParameter = "IFC Type";
        public const string TagParameter = "IFC Tag";
        public const string IdentityGroup = "IFC Identity";

        readonly SharedParameterManager _parameters;
        readonly IfcScale _scale;
        readonly bool _includeTypeProperties;

        public PropertyTransfer(SharedParameterManager parameters, IfcScale scale, bool includeTypeProperties)
        {
            _parameters = parameters;
            _scale = scale;
            _includeTypeProperties = includeTypeProperties;
        }

        /// <summary>
        /// Writes identity plus every readable property onto the element. Must run inside a
        /// transaction. Failures on individual properties are swallowed: one bad value must not
        /// cost the element its other 40 attributes.
        /// </summary>
        public void Apply(Element element, IIfcProduct product)
        {
            if (element == null || product == null) return;

            WriteIdentity(element, product);

            if (!(product is IIfcObject obj)) return;

            foreach (var pset in IfcHelpers.PropertySets(obj, _includeTypeProperties))
            {
                var groupName = (string)pset.Name ?? "IFC";
                foreach (var property in pset.HasProperties)
                    WriteProperty(element, groupName, property);
            }

            foreach (var qto in IfcHelpers.QuantitySets(obj))
            {
                var groupName = (string)qto.Name ?? "IFC Quantities";
                foreach (var quantity in qto.Quantities)
                    WriteQuantity(element, groupName, quantity);
            }
        }

        void WriteIdentity(Element element, IIfcProduct product)
        {
            Set(element, IdentityGroup, GuidParameter, SpecTypeId.String.Text, product.GlobalId.ToString());
            Set(element, IdentityGroup, EntityParameter, SpecTypeId.String.Text, IfcHelpers.EntityName(product));

            var name = IfcHelpers.Str(product.Name);
            if (!string.IsNullOrWhiteSpace(name))
                Set(element, IdentityGroup, TypeNameParameter, SpecTypeId.String.Text, name);

            if (product is IIfcElement el)
            {
                var tag = IfcHelpers.Str(el.Tag);
                if (!string.IsNullOrWhiteSpace(tag))
                    Set(element, IdentityGroup, TagParameter, SpecTypeId.String.Text, tag);
            }
        }

        void WriteProperty(Element element, string groupName, IIfcProperty property)
        {
            switch (property)
            {
                case IIfcPropertySingleValue single:
                {
                    var value = single.NominalValue;
                    if (value == null) return;
                    var spec = SpecFor(value);
                    Set(element, groupName, (string)single.Name, spec, Convert(value, spec));
                    return;
                }

                case IIfcPropertyEnumeratedValue enumerated:
                {
                    var joined = string.Join(", ", enumerated.EnumerationValues
                        .Where(v => v != null)
                        .Select(v => v.Value?.ToString()));
                    if (!string.IsNullOrEmpty(joined))
                        Set(element, groupName, (string)enumerated.Name, SpecTypeId.String.Text, joined);
                    return;
                }

                case IIfcPropertyListValue list:
                {
                    var joined = string.Join(", ", list.ListValues
                        .Where(v => v != null)
                        .Select(v => v.Value?.ToString()));
                    if (!string.IsNullOrEmpty(joined))
                        Set(element, groupName, (string)list.Name, SpecTypeId.String.Text, joined);
                    return;
                }
            }
        }

        void WriteQuantity(Element element, string groupName, IIfcPhysicalQuantity quantity)
        {
            switch (quantity)
            {
                case IIfcQuantityLength q:
                    Set(element, groupName, (string)q.Name, SpecTypeId.Length, (double)q.LengthValue * _scale.Length);
                    return;
                case IIfcQuantityArea q:
                    Set(element, groupName, (string)q.Name, SpecTypeId.Area, (double)q.AreaValue * _scale.Length * _scale.Length);
                    return;
                case IIfcQuantityVolume q:
                    Set(element, groupName, (string)q.Name, SpecTypeId.Volume, (double)q.VolumeValue * _scale.Length * _scale.Length * _scale.Length);
                    return;
                case IIfcQuantityCount q:
                    Set(element, groupName, (string)q.Name, SpecTypeId.Number, (double)q.CountValue);
                    return;
                case IIfcQuantityWeight q:
                    Set(element, groupName, (string)q.Name, SpecTypeId.Number, (double)q.WeightValue);
                    return;
            }
        }

        // ---- value typing --------------------------------------------------------------------

        /// <summary>Picks the Revit spec that preserves the meaning of an IFC measure.</summary>
        static ForgeTypeId SpecFor(IIfcValue value)
        {
            switch (IfcHelpers.EntityName(value as IPersistEntity) ?? value.GetType().Name)
            {
                case "IfcLengthMeasure":
                case "IfcPositiveLengthMeasure":
                case "IfcNonNegativeLengthMeasure":
                    return SpecTypeId.Length;

                case "IfcAreaMeasure":
                case "IfcPositiveAreaMeasure":
                    return SpecTypeId.Area;

                case "IfcVolumeMeasure":
                    return SpecTypeId.Volume;

                case "IfcInteger":
                case "IfcCountMeasure":
                case "IfcIntegerCountRateMeasure":
                    return SpecTypeId.Int.Integer;

                case "IfcBoolean":
                case "IfcLogical":
                    return SpecTypeId.Boolean.YesNo;

                case "IfcReal":
                case "IfcRatioMeasure":
                case "IfcPositiveRatioMeasure":
                case "IfcNormalisedRatioMeasure":
                case "IfcNumericMeasure":
                case "IfcMassMeasure":
                    return SpecTypeId.Number;

                default:
                    return SpecTypeId.String.Text;
            }
        }

        object Convert(IIfcValue value, ForgeTypeId spec)
        {
            var raw = value.Value;
            if (raw == null) return null;

            if (spec == SpecTypeId.String.Text)
                return raw as string ?? raw.ToString();

            try
            {
                if (spec == SpecTypeId.Boolean.YesNo)
                    return System.Convert.ToBoolean(raw) ? 1 : 0;

                if (spec == SpecTypeId.Int.Integer)
                    return System.Convert.ToInt32(raw);

                var number = System.Convert.ToDouble(raw, CultureInfo.InvariantCulture);

                // Measures arrive in file units; Revit stores feet internally.
                if (spec == SpecTypeId.Length) return number * _scale.Length;
                if (spec == SpecTypeId.Area) return number * _scale.Length * _scale.Length;
                if (spec == SpecTypeId.Volume) return number * _scale.Length * _scale.Length * _scale.Length;
                return number;
            }
            catch
            {
                return raw.ToString();
            }
        }

        // ---- writing -----------------------------------------------------------------------

        void Set(Element element, string groupName, string parameterName, ForgeTypeId spec, object value)
        {
            if (value == null || string.IsNullOrWhiteSpace(parameterName)) return;

            var definition = _parameters.GetOrCreate(groupName, parameterName, spec);
            if (definition == null) return;

            var parameter = element.get_Parameter(definition);
            if (parameter == null || parameter.IsReadOnly) return;

            try
            {
                switch (value)
                {
                    case string s: parameter.Set(s); break;
                    case int i: parameter.Set(i); break;
                    case double d: parameter.Set(d); break;
                    default: parameter.Set(value.ToString()); break;
                }
            }
            catch
            {
                // A spec mismatch against a previously created parameter of the same name.
                // The remaining properties are worth more than this one.
            }
        }
    }
}
