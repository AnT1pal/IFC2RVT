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
    /// Everything the installer actually does. Kept free of any UI so the same code serves the
    /// wizard and the silent command line, and so it can be reasoned about without a window.
    /// </summary>
    internal static class Setup
    {
        public const string Product = "IFC2RVT";
        public const string Vendor = "baidurovlabs.ru";
        public const string SourceUrl = "https://github.com/AnT1pal/IFC2RVT";
        public const string VendorSite = "https://baidurovlabs.ru";

        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IFC2RVT";

        /// <summary>Revit version -> which build of the add-in serves it.</summary>
        public static readonly Dictionary<string, string> BuildFor = new Dictionary<string, string>
        {
            { "2022", "2022" }, { "2023", "2022" }, { "2024", "2022" },
            { "2025", "2025" }, { "2026", "2025" },
        };

        public static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

        // ---- environment ----------------------------------------------------------------------

        public static List<string> DetectRevit()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return BuildFor.Keys
                .Where(v => File.Exists(Path.Combine(programFiles, "Autodesk", "Revit " + v, "Revit.exe")))
                .OrderBy(v => v)
                .ToList();
        }

        public static List<string> InstalledVersions()
            => BuildFor.Keys
                .Where(v => Directory.Exists(Path.Combine(AddinRoot(v), Product)))
                .OrderBy(v => v)
                .ToList();

        public static string AddinRoot(string version)
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "Autodesk", "Revit", "Addins", version);

        public static string HomeFolder
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Programs", Product);

        public static bool RevitIsRunning(out string pids)
        {
            var running = Process.GetProcessesByName("Revit");
            pids = string.Join(", ", running.Select(p => p.Id));
            return running.Length > 0;
        }

        /// <summary>Licence text carried inside the installer, for the agreement page.</summary>
        public static string ReadLicence()
        {
            var staging = Path.Combine(Path.GetTempPath(), Product + "_lic_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                ExtractPayload(staging);
                var licence = Path.Combine(staging, "LICENSE");
                return File.Exists(licence) ? File.ReadAllText(licence) : "LICENSE не найден во встроенном архиве.";
            }
            catch (Exception ex)
            {
                return "Не удалось прочитать текст лицензии: " + ex.Message +
                       Environment.NewLine + Environment.NewLine +
                       "Полный текст: https://www.gnu.org/licenses/gpl-3.0.txt";
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }

        // ---- install --------------------------------------------------------------------------

        /// <summary>
        /// Installs for the given Revit versions. <paramref name="report"/> receives one line per
        /// step so the wizard can show progress without this code knowing a window exists.
        /// </summary>
        public static int Install(IEnumerable<string> versions, Action<string> report)
        {
            report = report ?? (_ => { });

            if (RevitIsRunning(out var pids))
                throw new InvalidOperationException(
                    $"Revit запущен (PID {pids}). Закройте его — файлы надстройки заблокированы.");

            var targets = versions.Where(BuildFor.ContainsKey).Distinct().OrderBy(v => v).ToList();
            if (targets.Count == 0)
                throw new InvalidOperationException("Не выбрано ни одной версии Revit.");

            var staging = Path.Combine(Path.GetTempPath(), Product + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                report("Распаковка файлов...");
                ExtractPayload(staging);

                var done = 0;
                foreach (var version in targets)
                {
                    var source = Path.Combine(staging, BuildFor[version]);
                    if (!Directory.Exists(source))
                    {
                        report($"Revit {version}: в дистрибутиве нет сборки {BuildFor[version]} — пропуск");
                        continue;
                    }

                    var addinRoot = AddinRoot(version);
                    var target = Path.Combine(addinRoot, Product);

                    // A stale folder from an older version would leave orphaned assemblies behind.
                    TryDeleteDirectory(target);
                    Directory.CreateDirectory(target);
                    CopyTree(source, target);

                    foreach (var extra in new[] { "LICENSE", "README.md" })
                    {
                        var from = Path.Combine(staging, extra);
                        if (File.Exists(from)) File.Copy(from, Path.Combine(target, extra), true);
                    }

                    WriteManifest(Path.Combine(addinRoot, Product + ".addin"),
                                  Path.Combine(target, Product + ".dll"));

                    report($"Revit {version} — установлено");
                    done++;
                }

                if (done == 0) throw new InvalidOperationException("Ничего не установлено.");

                report("Регистрация для удаления...");
                RegisterUninstall(staging, done, report);
                return done;
            }
            finally
            {
                TryDeleteDirectory(staging);
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
        /// lives in the user profile, so asking for elevation would be theatre.
        /// </summary>
        static void RegisterUninstall(string staging, int versionCount, Action<string> report)
        {
            try
            {
                var home = HomeFolder;
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
                    key.SetValue("DisplayVersion", Version);
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
                report("Запись для удаления не создана: " + ex.Message);
            }
        }

        // ---- uninstall ------------------------------------------------------------------------

        public static int Uninstall(IEnumerable<string> versions, Action<string> report)
        {
            report = report ?? (_ => { });

            if (RevitIsRunning(out var pids))
                throw new InvalidOperationException(
                    $"Revit запущен (PID {pids}). Закройте его перед удалением.");

            var targets = (versions ?? BuildFor.Keys).Distinct().OrderBy(v => v).ToList();
            var removed = 0;

            foreach (var version in targets)
            {
                var addinRoot = AddinRoot(version);
                var folder = Path.Combine(addinRoot, Product);
                var manifest = Path.Combine(addinRoot, Product + ".addin");
                var touched = false;

                if (Directory.Exists(folder)) { TryDeleteDirectory(folder); touched = true; }
                if (File.Exists(manifest)) { TryDeleteFile(manifest); touched = true; }

                if (touched) { report($"Revit {version} — удалено"); removed++; }
            }

            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            ScheduleSelfRemoval();

            return removed;
        }

        /// <summary>
        /// Removes the installer copy once this process exits: a process cannot delete its own
        /// image while running, so a detached shell waits for the handle to be released.
        /// </summary>
        static void ScheduleSelfRemoval()
        {
            try
            {
                var home = HomeFolder;
                if (!Directory.Exists(home)) return;

                Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{home}\"")
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

        static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
