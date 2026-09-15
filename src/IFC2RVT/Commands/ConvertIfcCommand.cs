using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using IFC2RVT.Conversion;
using IFC2RVT.Hosting;
using IFC2RVT.UI;

namespace IFC2RVT.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ConvertIfcCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                message = "Нет открытого документа.";
                return Result.Failed;
            }

            var doc = uiDoc.Document;
            if (doc.IsFamilyDocument)
            {
                message = "Конвертация работает только в проекте, не в документе семейства.";
                return Result.Failed;
            }

            var options = new ConversionOptions();
            var dialog = new ConvertDialog(options);
            new WindowInteropHelper(dialog).Owner = commandData.Application.MainWindowHandle;

            if (dialog.ShowDialog() != true) return Result.Cancelled;

            ConversionReport report;
            try
            {
                report = CoreLoader.GetRunner().Run(doc, dialog.Result);
            }
            catch (Exception ex)
            {
                // A single-line Message hides the cause of the failures that matter most here:
                // assembly loading blows up as TypeInitializationException whose real reason sits
                // two levels down. Write the full chain to disk and point the user at it.
                var log = WriteCrashLog(ex);
                message = Describe(ex) + (log == null ? string.Empty : "\n\nПодробности: " + log);
                return Result.Failed;
            }

            ShowReport(report);
            return Result.Succeeded;
        }

        /// <summary>Flattens the whole exception chain; the outermost message alone is rarely useful.</summary>
        static string Describe(Exception ex)
        {
            var sb = new StringBuilder();
            var current = ex;
            var depth = 0;

            while (current != null && depth < 8)
            {
                sb.Append(depth == 0 ? "Конвертация прервана: " : "  причина: ");
                sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
                sb.AppendLine();

                if (current is ReflectionTypeLoadException load && load.LoaderExceptions != null)
                {
                    foreach (var inner in load.LoaderExceptions)
                        if (inner != null) sb.Append("    загрузка: ").AppendLine(inner.Message);
                }

                current = current.InnerException;
                depth++;
            }
            return sb.ToString().TrimEnd();
        }

        static string WriteCrashLog(Exception ex)
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), $"IFC2RVT_error_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                var sb = new StringBuilder();
                sb.AppendLine("IFC2RVT — отчёт об ошибке");
                sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine(new string('=', 70));
                sb.AppendLine(Describe(ex));
                sb.AppendLine();
                sb.AppendLine("Полная трассировка:");
                sb.AppendLine(new string('-', 70));
                sb.AppendLine(ex.ToString());
                sb.AppendLine();
                sb.AppendLine(DescribeLoadedAssemblies());

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                return path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Lists the assemblies most likely to be behind a load failure, with the location each
        /// was served from. A wrong path here is the whole diagnosis.
        /// </summary>
        static string DescribeLoadedAssemblies()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Загруженные сборки (Xbim / Microsoft.Extensions / IFC2RVT):");
            sb.AppendLine(new string('-', 70));
            sb.AppendLine("Папка надстройки: " + CoreLoader.AddinFolder);
            sb.AppendLine();

            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = a.GetName().Name ?? string.Empty;
                    if (!name.StartsWith("Xbim", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("Microsoft.Extensions", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("IFC2RVT", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("Esent", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string location;
                    try { location = a.IsDynamic ? "(dynamic)" : a.Location; }
                    catch { location = "(недоступно)"; }

                    sb.AppendLine($"  {name,-54} {a.GetName().Version}");
                    sb.AppendLine($"      {location}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  не удалось перечислить: " + ex.Message);
            }

            return sb.ToString();
        }

        static void ShowReport(ConversionReport report)
        {
            var csvPath = WriteCsv(report);

            var dialog = new TaskDialog("IFC2RVT — готово")
            {
                MainInstruction = $"Нативно: {report.Count(ConversionOutcome.Native)}   " +
                                  $"DirectShape: {report.Count(ConversionOutcome.DirectShape)}   " +
                                  $"Ошибок: {report.Count(ConversionOutcome.Failed)}",
                MainContent = $"Время: {report.Duration.TotalSeconds:F1} с\n" +
                              $"Уровней создано: {report.LevelsCreated}\n" +
                              $"Типов создано: {report.TypesCreated}\n" +
                              $"Общих параметров: {report.SharedParametersCreated}",
                ExpandedContent = report.BuildSummary(),
                MainIcon = report.Count(ConversionOutcome.Failed) > 0
                    ? TaskDialogIcon.TaskDialogIconWarning
                    : TaskDialogIcon.TaskDialogIconInformation
            };

            if (csvPath != null)
            {
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Открыть подробный отчёт (CSV)");
                dialog.FooterText = csvPath;
            }

            if (dialog.Show() == TaskDialogResult.CommandLink1 && csvPath != null)
            {
                try { Process.Start(new ProcessStartInfo(csvPath) { UseShellExecute = true }); }
                catch { }
            }
        }

        static string WriteCsv(ConversionReport report)
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(),
                    $"IFC2RVT_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                // UTF-8 with BOM so Excel picks up the Cyrillic without a manual import step.
                File.WriteAllText(path, report.BuildCsv(), new UTF8Encoding(true));

                // The summary alongside it, because the CSV is per element and the things worth
                // knowing about a run are not: which sections were refused, what was measured and
                // found wrong, how far the model was moved. Those lived only in a dialog that
                // closes, and a run that quietly built 1786 members at the wrong length is exactly
                // the case where there has to be something left on disk to read afterwards.
                try
                {
                    File.WriteAllText(Path.ChangeExtension(path, ".txt"),
                                      report.BuildSummary(), new UTF8Encoding(true));
                }
                catch { }

                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
