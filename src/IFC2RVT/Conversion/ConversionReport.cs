using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace IFC2RVT.Conversion
{
    public enum ConversionOutcome
    {
        Native,
        DirectShape,
        Skipped,
        Failed
    }

    public class ConversionRecord
    {
        public string IfcGuid { get; set; }
        public string IfcEntity { get; set; }
        public string IfcName { get; set; }
        public ConversionOutcome Outcome { get; set; }
        public string RevitCategory { get; set; }
        public long RevitElementId { get; set; }
        public string Message { get; set; }
    }

    public class ConversionReport
    {
        readonly List<ConversionRecord> _records = new List<ConversionRecord>();
        public IReadOnlyList<ConversionRecord> Records => _records;

        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public TimeSpan Duration { get; set; }
        public string SourceFile { get; set; }
        public string SourceApplication { get; set; }
        public string SourceSchema { get; set; }
        /// <summary>Translation applied to bring a far-flung model near the origin, in metres.</summary>
        public double[] AppliedOffsetMetres { get; set; }

        public int LevelsCreated { get; set; }
        public int TypesCreated { get; set; }
        public int SharedParametersCreated { get; set; }
        /// <summary>Sections the project has no family for: designation -> (kind, standard, count).</summary>
        public List<Tuple<string, string, string, int>> MissingSections { get; }
            = new List<Tuple<string, string, string, int>>();

        /// <summary>Framing families generated from the section outlines found in the file.</summary>
        public int SectionFamiliesCreated { get; set; }

        /// <summary>
        /// Of those, how many had a member placed and measured at the length it was placed at.
        /// Worth printing on its own line: a family that loads and does not stretch builds every
        /// member at the template's nominal size without raising anything.
        /// </summary>
        public int SectionFamiliesVerified { get; set; }

        /// <summary>What the section generator made, and what it declined to make and why.</summary>
        public List<string> SectionNotes { get; } = new List<string>();

        public int GridsCreated { get; set; }
        public int GridsReused { get; set; }
        public int MaterialsCreated { get; set; }
        public int MaterialsColoured { get; set; }

        /// <summary>Revit warnings discarded during the run (overlaps, unenclosed rooms and such).</summary>
        public int WarningsSuppressed { get; set; }

        /// <summary>
        /// Revit errors handed back to Revit to resolve. Worth showing: the default resolution can
        /// join or delete an element, and that is a change the converter did not intend.
        /// </summary>
        public int ErrorsResolved { get; set; }

        public void Add(ConversionRecord r) => _records.Add(r);

        public void Add(string guid, string entity, string name, ConversionOutcome outcome,
                        string category = null, long elementId = 0, string message = null)
            => _records.Add(new ConversionRecord
            {
                IfcGuid = guid, IfcEntity = entity, IfcName = name, Outcome = outcome,
                RevitCategory = category, RevitElementId = elementId, Message = message
            });

        public int Count(ConversionOutcome o) => _records.Count(r => r.Outcome == o);

        public string BuildSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("IFC2RVT - отчёт о конвертации");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine($"Файл:        {SourceFile}");
            sb.AppendLine($"Экспортёр:   {SourceApplication}");
            sb.AppendLine($"Схема:       {SourceSchema}");
            sb.AppendLine($"Длительность: {Duration.TotalSeconds:F1} с");
            if (AppliedOffsetMetres != null)
            {
                sb.AppendLine($"Смещение к нулю: X {AppliedOffsetMetres[0]:F1} м, " +
                              $"Y {AppliedOffsetMetres[1]:F1} м, Z {AppliedOffsetMetres[2]:F1} м");
                sb.AppendLine("  (модель была вынесена в координаты площадки; исходные координаты");
                sb.AppendLine("   восстанавливаются обратным сдвигом на эти значения)");
            }
            sb.AppendLine();
            sb.AppendLine($"Нативных элементов:   {Count(ConversionOutcome.Native)}");
            sb.AppendLine($"DirectShape:          {Count(ConversionOutcome.DirectShape)}");
            sb.AppendLine($"Пропущено:            {Count(ConversionOutcome.Skipped)}");
            sb.AppendLine($"Ошибок:               {Count(ConversionOutcome.Failed)}");
            sb.AppendLine();
            sb.AppendLine($"Создано уровней:      {LevelsCreated}");
            sb.AppendLine($"Создано типов:        {TypesCreated}");
            sb.AppendLine($"Общих параметров:     {SharedParametersCreated}");
            sb.AppendLine($"Создано материалов:   {MaterialsCreated} (с цветом из IFC: {MaterialsColoured})");
            if (GridsCreated > 0 || GridsReused > 0)
                sb.AppendLine($"Создано осей:         {GridsCreated} (переиспользовано: {GridsReused})");
            if (SectionFamiliesCreated > 0)
                sb.AppendLine($"Семейств сечений:     {SectionFamiliesCreated} " +
                              $"(по контурам из файла; длина проверена у {SectionFamiliesVerified})");

            if (SectionNotes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Сечения проката:");
                sb.AppendLine(new string('-', 60));
                foreach (var note in SectionNotes.Take(20)) sb.AppendLine("  " + note);
            }

            if (WarningsSuppressed > 0 || ErrorsResolved > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Предупреждений Revit подавлено: {WarningsSuppressed}");
                if (ErrorsResolved > 0)
                {
                    sb.AppendLine($"Ошибок Revit разрешено автоматически: {ErrorsResolved}");
                    sb.AppendLine("  внимание: автоматическое разрешение может объединить или удалить элемент");
                }
            }
            sb.AppendLine();
            sb.AppendLine("По типам IFC:");
            sb.AppendLine(new string('-', 60));
            sb.AppendLine($"{"IFC",-28}{"Native",8}{"Direct",8}{"Skip",7}{"Fail",7}");
            foreach (var g in _records.GroupBy(r => r.IfcEntity).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-28}{1,8}{2,8}{3,7}{4,7}",
                    g.Key.Length > 27 ? g.Key.Substring(0, 27) : g.Key,
                    g.Count(r => r.Outcome == ConversionOutcome.Native),
                    g.Count(r => r.Outcome == ConversionOutcome.DirectShape),
                    g.Count(r => r.Outcome == ConversionOutcome.Skipped),
                    g.Count(r => r.Outcome == ConversionOutcome.Failed)));
            }

            var failures = _records.Where(r => r.Outcome == ConversionOutcome.Failed)
                                   .GroupBy(r => r.Message ?? "(без сообщения)")
                                   .OrderByDescending(g => g.Count()).Take(15).ToList();
            if (failures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Частые ошибки:");
                sb.AppendLine(new string('-', 60));
                foreach (var f in failures) sb.AppendLine($"{f.Count(),6}x  {f.Key}");
            }

            // Caveats ride on elements that converted successfully - a substituted wall type is
            // the clearest example. Without their own section they would never be seen, because
            // nothing about the element looks like a failure.
            var caveats = _records.Where(r => r.Outcome != ConversionOutcome.Failed &&
                                              !string.IsNullOrWhiteSpace(r.Message))
                                  .GroupBy(r => r.Message)
                                  .OrderByDescending(g => g.Count()).Take(15).ToList();
            if (caveats.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Замечания (элемент создан, но требует проверки):");
                sb.AppendLine(new string('-', 60));
                foreach (var c in caveats) sb.AppendLine($"{c.Count(),6}x  {c.Key}");
            }

            if (MissingSections.Count > 0)
            {
                var wanted = MissingSections.Sum(m => m.Item4);
                sb.AppendLine();
                sb.AppendLine($"НЕ ХВАТАЕТ СЕМЕЙСТВ: {MissingSections.Count} сечений, {wanted} элементов");
                sb.AppendLine(new string('-', 60));
                sb.AppendLine("Эти элементы оставлены геометрией, чтобы не подменять сечение чужим.");
                sb.AppendLine("Загрузите семейства и запустите конвертацию заново.");
                sb.AppendLine();

                foreach (var group in MissingSections
                             .GroupBy(m => Tuple.Create(m.Item2, m.Item3))
                             .OrderByDescending(g => g.Sum(m => m.Item4)))
                {
                    var standard = string.IsNullOrEmpty(group.Key.Item2) ? "" : "  " + group.Key.Item2;
                    sb.AppendLine($"  {group.Sum(m => m.Item4),6} элементов   {group.Key.Item1}{standard}");

                    foreach (var section in group.OrderByDescending(m => m.Item4).Take(8))
                        sb.AppendLine($"           {section.Item4,6}x  {section.Item1}");
                }
            }

            return sb.ToString();
        }

        public string BuildCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("IfcGuid;IfcEntity;IfcName;Outcome;RevitCategory;RevitElementId;Message");
            foreach (var r in _records)
                sb.AppendLine(string.Join(";", Esc(r.IfcGuid), Esc(r.IfcEntity), Esc(r.IfcName),
                    r.Outcome.ToString(), Esc(r.RevitCategory),
                    r.RevitElementId.ToString(CultureInfo.InvariantCulture), Esc(r.Message)));
            return sb.ToString();
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.IndexOf(';') >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
