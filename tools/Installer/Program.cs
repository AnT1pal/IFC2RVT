using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace IFC2RVT.Setup
{
    /// <summary>
    /// Installer for the IFC2RVT Revit add-in.
    ///
    /// Carries the add-in binaries as an embedded archive so the whole thing is one file, and
    /// registers itself under HKCU so the add-in can be removed from Windows the ordinary way.
    /// Everything lives in the per-user profile, so no administrator rights are needed.
    /// </summary>
    internal static class Program
    {
        const string Product = "IFC2RVT";
        const string Vendor = "baidurovlabs.ru";
        const string SourceUrl = "https://github.com/AnT1pal/IFC2RVT";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IFC2RVT";

        /// <summary>Revit version -> which build of the add-in serves it.</summary>
        static readonly Dictionary<string, string> BuildFor = new Dictionary<string, string>
        {
            { "2022", "2022" }, { "2023", "2022" }, { "2024", "2022" },
            { "2025", "2025" }, { "2026", "2025" },
        };

        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Console.Title = Product + " — установка";

            var quiet = args.Any(a => Eq(a, "--quiet") || Eq(a, "/quiet") || Eq(a, "/S"));
            var versions = ParseVersions(args);

            try
            {
                if (args.Any(a => Eq(a, "--uninstall") || Eq(a, "/uninstall")))
                    return Uninstall(versions, quiet);

                if (args.Any(a => Eq(a, "--install") || Eq(a, "/install")) || quiet)
                    return Install(versions, quiet);

                return Interactive();
            }
            catch (Exception ex)
            {
                Error("Непредвиденная ошибка: " + ex.Message);
                if (!quiet) Pause();
                return 1;
            }
        }

        // ---- interactive ----------------------------------------------------------------------

        static int Interactive()
        {
            Banner();

            var installed = DetectRevit().ToList();
            var present = InstalledVersions().ToList();

            if (installed.Count == 0)
                Warn("Revit 2022–2026 на этом компьютере не найден. Установка возможна, но работать будет нечему.");
            else
                Info("Найден Revit: " + string.Join(", ", installed));

            if (present.Count > 0)
                Info("Уже установлено для: " + string.Join(", ", present));

            Console.WriteLine();
            Console.WriteLine("  1 — установить");
            Console.WriteLine("  2 — удалить");
            Console.WriteLine("  3 — выход");
            Console.WriteLine();
            Console.Write("Выбор: ");

            var choice = Console.ReadLine()?.Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": { var c = Install(null, false); Pause(); return c; }
                case "2": { var c = Uninstall(null, false); Pause(); return c; }
                default: return 0;
            }
        }

        static void Banner()
        {
            Console.WriteLine();
            Console.WriteLine("  " + Product + " — конвертер IFC в нативные элементы Revit");
            Console.WriteLine("  " + Vendor + "   |   GNU GPLv3   |   " + SourceUrl);
            Console.WriteLine("  " + new string('-', 68));
            Console.WriteLine("  Свободное ПО без каких-либо гарантий. Исходный код по ссылке выше;");
            Console.WriteLine("  полный текст лицензии кладётся рядом с программой при установке.");
            Console.WriteLine();
        }

        // ---- install --------------------------------------------------------------------------

        static int Install(List<string> requested, bool quiet)
        {
            if (!quiet) Banner();

            if (RevitIsRunning(out var pids))
            {
                Error($"Revit запущен (PID {pids}). Закройте его — файлы надстройки заблокированы.");
                return 2;
            }

            var targets = (requested != null && requested.Count > 0 ? requested : DetectRevit().ToList())
                .Where(v => BuildFor.ContainsKey(v))
                .Distinct()
                .OrderBy(v => v)
                .ToList();

            if (targets.Count == 0)
            {
                Error("Не найдено ни одной поддерживаемой версии Revit (2022–2026).");
                Error("Укажите версию явно:  IFC2RVT-Setup.exe --install --versions 2026");
                return 3;
            }

            var staging = Path.Combine(Path.GetTempPath(), Product + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                ExtractPayload(staging);

                var done = 0;
                foreach (var version in targets)
                {
                    var source = Path.Combine(staging, BuildFor[version]);
                    if (!Directory.Exists(source))
                    {
                        Warn($"Revit {version}: в дистрибутиве нет сборки {BuildFor[version]} — пропуск.");
                        continue;
                    }

                    var addinRoot = AddinRoot(version);
                    var target = Path.Combine(addinRoot, Product);

                    Directory.CreateDirectory(target);
                    CopyTree(source, target);

                    foreach (var extra in new[] { "LICENSE", "README.md" })
                    {
                        var from = Path.Combine(staging, extra);
                        if (File.Exists(from)) File.Copy(from, Path.Combine(target, extra), true);
                    }

                    WriteManifest(Path.Combine(addinRoot, Product + ".addin"),
                                  Path.Combine(target, Product + ".dll"));

                    Ok($"Revit {version}  →  {target}");
                    done++;
                }

                if (done == 0) { Error("Ничего не установлено."); return 4; }

                RegisterUninstall(staging, done);

                Console.WriteLine();
                Ok($"Готово: установлено для {done} версий. Перезапустите Revit.");
                Info("Вкладка ленты: IFC2RVT → Конвертация → «IFC → нативные»");
                return 0;
            }
            finally
            {
                TryDelete(staging);
            }
        }

        static void WriteManifest(string manifestPath, string assemblyPath)
        {
            var xml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<RevitAddIns>\r\n" +
                "  <AddIn Type=\"Application\">\r\n" +
                "    <Name>IFC2RVT</Name>\r\n" +
                "    <Assembly>" + assemblyPath + "</Assembly>\r\n" +
                "    <AddInId>7f3a1c52-9b64-4d8e-a1f0-5c2b9e7d4a31</AddInId>\r\n" +
                "    <FullClassName>IFC2RVT.App</FullClassName>\r\n" +
                "    <VendorId>IFC2RVT</VendorId>\r\n" +
                "    <VendorDescription>baidurovlabs.ru — IFC to native Revit elements, GNU GPLv3</VendorDescription>\r\n" +
                "  </AddIn>\r\n" +
                "</RevitAddIns>\r\n";

            File.WriteAllText(manifestPath, xml, new UTF8Encoding(true));
        }

        /// <summary>
        /// Puts the add-in into Programs and Features. HKCU rather than HKLM: everything installed
        /// lives in the user profile, and asking for elevation to copy files there would be theatre.
        /// </summary>
        static void RegisterUninstall(string staging, int versionCount)
        {
            try
            {
                var home = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", Product);
                Directory.CreateDirectory(home);

                var self = Assembly.GetExecutingAssembly().Location;
                var copy = Path.Combine(home, Path.GetFileName(self));
                if (!string.Equals(self, copy, StringComparison.OrdinalIgnoreCase))
                    File.Copy(self, copy, true);

                var licence = Path.Combine(staging, "LICENSE");
                if (File.Exists(licence)) File.Copy(licence, Path.Combine(home, "LICENSE"), true);

                using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    if (key == null) return;
                    key.SetValue("DisplayName", Product + " — IFC в нативные элементы Revit");
                    key.SetValue("DisplayVersion", Version());
                    key.SetValue("Publisher", Vendor);
                    key.SetValue("URLInfoAbout", SourceUrl);
                    key.SetValue("HelpLink", SourceUrl);
                    key.SetValue("InstallLocation", home);
                    key.SetValue("UninstallString", "\"" + copy + "\" --uninstall");
                    key.SetValue("QuietUninstallString", "\"" + copy + "\" --uninstall --quiet");
                    key.SetValue("DisplayIcon", copy);
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    key.SetValue("EstimatedSize", 25 * 1024 * versionCount, RegistryValueKind.DWord);
                    key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                }
            }
            catch (Exception ex)
            {
                Warn("Не удалось зарегистрировать запись для удаления: " + ex.Message);
            }
        }

        // ---- uninstall ------------------------------------------------------------------------

        static int Uninstall(List<string> requested, bool quiet)
        {
            if (!quiet) Banner();

            if (RevitIsRunning(out var pids))
            {
                Error($"Revit запущен (PID {pids}). Закройте его перед удалением.");
                return 2;
            }

            var targets = requested != null && requested.Count > 0 ? requested : BuildFor.Keys.ToList();
            var removed = 0;

            foreach (var version in targets.Distinct().OrderBy(v => v))
            {
                var addinRoot = AddinRoot(version);
                var folder = Path.Combine(addinRoot, Product);
                var manifest = Path.Combine(addinRoot, Product + ".addin");
                var touched = false;

                if (Directory.Exists(folder)) { TryDelete(folder); touched = true; }
                if (File.Exists(manifest)) { TryDeleteFile(manifest); touched = true; }

                if (touched) { Ok($"Revit {version}: удалено"); removed++; }
            }

            var home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", Product);

            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

            // The running executable cannot delete itself; hand that to a detached shell.
            ScheduleSelfRemoval(home);

            Console.WriteLine();
            if (removed == 0) Warn("Установленных версий не найдено — удалять нечего.");
            else Ok($"Удалено для {removed} версий.");

            Info("Общие параметры и уже созданные элементы в проектах не трогаются — они ваши.");
            return 0;
        }

        /// <summary>
        /// Removes the installer copy after this process exits. A process cannot delete its own
        /// image while it is running, so a detached cmd waits for the handle to be released.
        /// </summary>
        static void ScheduleSelfRemoval(string home)
        {
            try
            {
                if (!Directory.Exists(home)) return;

                var command = $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{home}\"";
                Process.Start(new ProcessStartInfo("cmd.exe", command)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }

        // ---- payload --------------------------------------------------------------------------

        static void ExtractPayload(string destination)
        {
            var assembly = Assembly.GetExecutingAssembly();

            using (var stream = assembly.GetManifestResourceStream("payload.zip"))
            {
                if (stream == null)
                    throw new InvalidOperationException(
                        "В этом экземпляре установщика нет встроенных файлов надстройки. " +
                        "Скачайте IFC2RVT-Setup.exe из раздела Releases.");

                Directory.CreateDirectory(destination);

                var temp = Path.Combine(destination, "payload.zip");
                using (var file = File.Create(temp)) stream.CopyTo(file);

                ZipFile.ExtractToDirectory(temp, destination);
                File.Delete(temp);
            }
        }

        static void CopyTree(string source, string target)
        {
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, target));

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(source, target), true);
        }

        // ---- environment ----------------------------------------------------------------------

        static IEnumerable<string> DetectRevit()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return BuildFor.Keys
                .Where(v => File.Exists(Path.Combine(programFiles, "Autodesk", "Revit " + v, "Revit.exe")))
                .OrderBy(v => v);
        }

        static IEnumerable<string> InstalledVersions()
            => BuildFor.Keys
                .Where(v => Directory.Exists(Path.Combine(AddinRoot(v), Product)))
                .OrderBy(v => v);

        static string AddinRoot(string version)
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "Autodesk", "Revit", "Addins", version);

        static bool RevitIsRunning(out string pids)
        {
            var running = Process.GetProcessesByName("Revit");
            pids = string.Join(", ", running.Select(p => p.Id));
            return running.Length > 0;
        }

        static List<string> ParseVersions(string[] args)
        {
            var index = Array.FindIndex(args, a => Eq(a, "--versions") || Eq(a, "/versions"));
            if (index < 0 || index + 1 >= args.Length) return null;

            return args[index + 1]
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .ToList();
        }

        static string Version()
            => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ---- output ---------------------------------------------------------------------------

        static void Ok(string text) => Write(text, ConsoleColor.Green, "  ");
        static void Info(string text) => Write(text, ConsoleColor.Gray, "  ");
        static void Warn(string text) => Write(text, ConsoleColor.Yellow, "  ! ");
        static void Error(string text) => Write(text, ConsoleColor.Red, "  x ");

        static void Write(string text, ConsoleColor colour, string prefix)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = colour;
            Console.WriteLine(prefix + text);
            Console.ForegroundColor = previous;
        }

        static void Pause()
        {
            Console.WriteLine();
            Console.Write("Нажмите Enter, чтобы закрыть...");
            Console.ReadLine();
        }
    }
}
