using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace IFC2RVT.Mapping
{
    /// <summary>What kind of rolled product a designation names.</summary>
    public enum SteelKind
    {
        Unknown,
        Angle,          // уголок
        HollowSection,  // гнутый замкнутый профиль
        Channel,        // швеллер
        IBeam,          // двутавр
        Strip,          // полоса
        Sheet,          // лист
        CheckerPlate,   // рифлёный лист
        Tube            // труба
    }

    /// <summary>
    /// Reads a Russian rolled-steel designation out of the text an exporter wrote, and says what
    /// kind of product it names.
    ///
    /// Two things make this worth having rather than matching the raw string.
    ///
    /// The first is homoglyphs. A designation written with Cyrillic Х and the same designation
    /// written with Latin X are different strings that no name match will join, and they look
    /// identical on screen. The model this was built against contains both spellings of the same
    /// standard - "Гн[160Х60Х4" in Cyrillic beside "Гнз120X60X4" in Latin.
    ///
    /// The second is kind. Counted over that model, 40% of the steel is hollow section and 34% is
    /// angle, while I-beams are 1.8%. A converter that quietly substitutes whatever framing family
    /// happens to be loaded turns the other 98% into something the structure is not. Knowing the
    /// kind is what lets it refuse instead, and lets the report say which families to load.
    ///
    /// A further fifth of the model is sheet and strip, which are not framing at all.
    /// </summary>
    public class SteelDesignation
    {
        /// <summary>Cyrillic letters drawn identically to a Latin one.</summary>
        static readonly Dictionary<char, char> Homoglyphs = new Dictionary<char, char>
        {
            { 'А', 'A' }, { 'В', 'B' }, { 'Е', 'E' }, { 'К', 'K' }, { 'М', 'M' }, { 'Н', 'H' },
            { 'О', 'O' }, { 'Р', 'P' }, { 'С', 'C' }, { 'Т', 'T' }, { 'У', 'Y' }, { 'Х', 'X' },
            { 'а', 'a' }, { 'е', 'e' }, { 'о', 'o' }, { 'р', 'p' }, { 'с', 'c' }, { 'у', 'y' },
            { 'х', 'x' },
        };

        static readonly (Regex Pattern, SteelKind Kind, string Standard)[] Rules =
        {
            (new Regex(@"^L\s*\d", RegexOptions.IgnoreCase), SteelKind.Angle, "ГОСТ 8509-93 / 8510-86"),
            (new Regex(@"^Гн[зc\[]?\s*\d", RegexOptions.IgnoreCase), SteelKind.HollowSection, "ГОСТ 30245-2003"),
            (new Regex(@"^\[\s*\d"), SteelKind.Channel, "ГОСТ 8240-97"),
            (new Regex(@"^(I|Б|Ш|K|Д)\s*\d", RegexOptions.IgnoreCase), SteelKind.IBeam, "ГОСТ 26020-83 / 8239-89"),
            (new Regex(@"^(PL|ПЛ)\s*\d", RegexOptions.IgnoreCase), SteelKind.Strip, "ГОСТ 103-2006"),
            (new Regex(@"^[-—–]\s*\d+\s*[*xхХ]"), SteelKind.Sheet, "ГОСТ 19903-2015"),
            (new Regex(@"^риф", RegexOptions.IgnoreCase), SteelKind.CheckerPlate, "ГОСТ 8568-77"),
            (new Regex(@"^(D|Ø|Труб)\s*\d", RegexOptions.IgnoreCase), SteelKind.Tube, "ГОСТ 8732-78 / 10704-91"),
        };

        public string Raw { get; private set; }

        /// <summary>The designation with homoglyphs and spelling variants folded out.</summary>
        public string Normalised { get; private set; }

        public SteelKind Kind { get; private set; }
        public string Standard { get; private set; }

        /// <summary>Sheet, strip and checker plate are flat products, not framing members.</summary>
        public bool IsFlatProduct =>
            Kind == SteelKind.Sheet || Kind == SteelKind.Strip || Kind == SteelKind.CheckerPlate;

        public static SteelDesignation Parse(string text)
        {
            var result = new SteelDesignation
            {
                Raw = text,
                Normalised = Normalise(text),
                Kind = SteelKind.Unknown,
                Standard = null
            };

            if (string.IsNullOrWhiteSpace(result.Normalised)) return result;

            // Classify on the raw text first. Normalisation folds Cyrillic letters onto their Latin
            // lookalikes, which is right for comparing two designations and wrong for reading a
            // prefix: "рифл." becomes "pифл." with a Latin p, and a Cyrillic pattern stops
            // matching a word that is plainly Russian. Normalised is the fallback, for a file that
            // spelled the prefix in Latin to begin with.
            var raw = result.Raw?.Trim();

            foreach (var candidate in new[] { raw, result.Normalised })
            {
                if (string.IsNullOrEmpty(candidate)) continue;

                foreach (var rule in Rules)
                {
                    if (!rule.Pattern.IsMatch(candidate)) continue;
                    result.Kind = rule.Kind;
                    result.Standard = rule.Standard;
                    return result;
                }
            }
            return result;
        }

        /// <summary>
        /// Folds a designation to a form two spellings of the same section share: homoglyphs to
        /// Latin, the separator to a single form, and the interchangeable hollow-section prefixes
        /// to one. "Гн[160Х60Х4" and "Гнз160x60x4" are the same product written two ways.
        /// </summary>
        public static string Normalise(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var sb = new StringBuilder(text.Length);
            foreach (var ch in text.Trim())
                sb.Append(Homoglyphs.TryGetValue(ch, out var latin) ? latin : ch);

            var value = sb.ToString();

            // Hollow sections appear as "Гнз", "Гн[" and "Гн" for the same standard.
            value = Regex.Replace(value, @"^(Гн)[зc\[]", "$1", RegexOptions.IgnoreCase);

            // The size separator is written as x, *, or the multiplication sign.
            value = value.Replace('*', 'x').Replace('×', 'x').Replace('X', 'x');

            // Trailing bracket left over from "Гн[160Х60Х4]" split on the closing half.
            value = value.TrimEnd(']', ' ');

            return Regex.Replace(value, @"\s+", " ").Trim();
        }

        /// <summary>True when two designations name the same section, whatever the spelling.</summary>
        public static bool SameSection(string a, string b)
        {
            var left = Normalise(a);
            var right = Normalise(b);
            return left != null && right != null &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        public string KindName
        {
            get
            {
                switch (Kind)
                {
                    case SteelKind.Angle: return "уголок";
                    case SteelKind.HollowSection: return "гнутый замкнутый профиль";
                    case SteelKind.Channel: return "швеллер";
                    case SteelKind.IBeam: return "двутавр";
                    case SteelKind.Strip: return "полоса";
                    case SteelKind.Sheet: return "лист";
                    case SteelKind.CheckerPlate: return "рифлёный лист";
                    case SteelKind.Tube: return "труба";
                    default: return "не опознан";
                }
            }
        }

        public override string ToString()
            => Standard == null ? KindName : KindName + " (" + Standard + ")";
    }
}
