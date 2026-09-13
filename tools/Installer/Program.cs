using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace IFC2RVT.Setup
{
    /// <summary>
    /// Entry point. Double-clicking opens the wizard; the command line stays available so the
    /// add-in can be rolled out by a script and so Windows can call the uninstaller silently.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var quiet = Has(args, "--quiet", "/quiet", "/S");
            var uninstall = Has(args, "--uninstall", "/uninstall");
            var install = Has(args, "--install", "/install");
            var versions = ParseVersions(args);

            if (quiet) return RunSilent(uninstall, versions);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (var form = new WizardForm(uninstall))
            {
                // --install without --quiet still shows the wizard: an installer that does its work
                // behind a shortcut with no window is how people end up not knowing what they ran.
                _ = install;
                Application.Run(form);
            }
            return 0;
        }

        static int RunSilent(bool uninstall, List<string> versions)
        {
            try
            {
                if (uninstall)
                {
                    Setup.Uninstall(versions, null);
                    return 0;
                }

                var targets = versions != null && versions.Count > 0 ? versions : Setup.DetectRevit();
                if (targets.Count == 0) return 3;

                Setup.Install(targets, null);
                return 0;
            }
            catch (Exception)
            {
                // Silent means silent; the caller reads the exit code.
                return 1;
            }
        }

        static bool Has(string[] args, params string[] names)
            => args.Any(a => names.Any(n => string.Equals(a, n, StringComparison.OrdinalIgnoreCase)));

        static List<string> ParseVersions(string[] args)
        {
            var index = Array.FindIndex(args, a =>
                string.Equals(a, "--versions", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/versions", StringComparison.OrdinalIgnoreCase));

            if (index < 0 || index + 1 >= args.Length) return null;

            return args[index + 1]
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .ToList();
        }
    }
}
