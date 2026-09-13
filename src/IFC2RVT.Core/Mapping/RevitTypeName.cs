using System;
using System.Linq;

namespace IFC2RVT.Mapping
{
    /// <summary>
    /// Revit writes "Family:Type:ElementId" into IfcRoot.Name when it exports, e.g.
    ///   "Базовая стена:ADSK_Наружная_Сэндвич-панель100:485773"
    /// and "Family:Type" into IfcTypeObject.Name. Recovering those three fields is what makes a
    /// Revit-authored IFC round-trip cleanly: the type can be matched by its original name instead
    /// of being guessed from geometry.
    ///
    /// Non-Revit exporters put arbitrary text here, so every field is best-effort and callers must
    /// cope with nulls.
    /// </summary>
    public class RevitTypeName
    {
        public string FamilyName { get; private set; }
        public string TypeName { get; private set; }
        public long? SourceElementId { get; private set; }
        public string Raw { get; private set; }

        /// <summary>True when the name carried the trailing element id Revit appends on export.</summary>
        public bool LooksLikeRevitExport => SourceElementId.HasValue;

        public static RevitTypeName Parse(string raw)
        {
            var result = new RevitTypeName { Raw = raw };
            if (string.IsNullOrWhiteSpace(raw)) return result;

            var parts = raw.Split(':');

            // Type names may themselves contain colons, so anchor on the two ends:
            // the first segment is the family, a trailing all-digit segment is the element id,
            // and whatever sits between them is the type name.
            if (parts.Length >= 3 && IsDigits(parts[parts.Length - 1]))
            {
                if (long.TryParse(parts[parts.Length - 1], out var id)) result.SourceElementId = id;
                result.FamilyName = parts[0].Trim();
                result.TypeName = string.Join(":", parts.Skip(1).Take(parts.Length - 2)).Trim();
            }
            else if (parts.Length >= 2)
            {
                result.FamilyName = parts[0].Trim();
                result.TypeName = string.Join(":", parts.Skip(1)).Trim();
            }
            else
            {
                result.TypeName = raw.Trim();
            }

            if (result.TypeName != null && result.TypeName.Length == 0) result.TypeName = null;
            if (result.FamilyName != null && result.FamilyName.Length == 0) result.FamilyName = null;
            return result;
        }

        static bool IsDigits(string s)
            => !string.IsNullOrEmpty(s) && s.All(char.IsDigit);

        /// <summary>"Family: Type" for logging, falling back to whatever is known.</summary>
        public override string ToString()
        {
            if (FamilyName != null && TypeName != null) return FamilyName + ": " + TypeName;
            return TypeName ?? FamilyName ?? Raw ?? string.Empty;
        }

        /// <summary>
        /// Candidate names to try when hunting for an existing Revit type, most specific first.
        /// </summary>
        public string[] MatchCandidates()
            => new[] { TypeName, Raw, FamilyName }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }
}
