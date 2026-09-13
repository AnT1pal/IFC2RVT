using System;
using System.IO;
using System.Reflection;
using IFC2RVT.Conversion;

#if REVIT2025_OR_GREATER
using System.Runtime.Loader;
#endif

namespace IFC2RVT.Hosting
{
    /// <summary>
    /// Loads the conversion core along with its xBIM dependencies, isolated from whatever Revit
    /// and its other add-ins have already put into the process.
    ///
    /// Why this exists: xBIM 6 binds against Microsoft.Extensions.Logging.Abstractions 8.0 and
    /// DependencyInjection.Abstractions 9.0. Dynamo for Revit ships 6.0 and 2.0 of the same
    /// assemblies and loads at Revit startup. On .NET 8 the default load context serves the copy
    /// that is already loaded regardless of the requested version, so xBIM ends up calling methods
    /// that do not exist in Dynamo's older build and IfcStore fails in its static constructor with
    /// a TypeInitializationException.
    ///
    /// The fix is to resolve those dependencies ourselves. On .NET 8 that is a private
    /// AssemblyLoadContext; on .NET Framework the runtime already binds by exact version, so a
    /// resolve handler pointed at the add-in folder is enough.
    /// </summary>
    public static class CoreLoader
    {
        const string CoreAssemblyName = "IFC2RVT.Core";
        const string RunnerTypeName = "IFC2RVT.Conversion.ConversionRunner";

        static readonly object Gate = new object();
        static IConversionRunner _runner;

        public static string AddinFolder =>
            Path.GetDirectoryName(typeof(CoreLoader).Assembly.Location) ?? Directory.GetCurrentDirectory();

        public static IConversionRunner GetRunner()
        {
            lock (Gate)
            {
                if (_runner != null) return _runner;

                var assembly = LoadCore();
                var type = assembly.GetType(RunnerTypeName, throwOnError: true);
                _runner = (IConversionRunner)Activator.CreateInstance(type);
                return _runner;
            }
        }

        static Assembly LoadCore()
        {
            var path = Path.Combine(AddinFolder, CoreAssemblyName + ".dll");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Не найдена сборка ядра: {path}", path);

#if REVIT2025_OR_GREATER
            return XbimLoadContext.Instance.LoadFromAssemblyPath(path);
#else
            EnsureResolveHandler();
            return Assembly.LoadFrom(path);
#endif
        }

#if !REVIT2025_OR_GREATER
        static bool _handlerAttached;

        /// <summary>
        /// .NET Framework probes only the Revit folder, so dependencies sitting next to the add-in
        /// have to be served explicitly. Binding here is by exact version, which is what keeps a
        /// foreign copy of Microsoft.Extensions.* from being substituted.
        /// </summary>
        static void EnsureResolveHandler()
        {
            if (_handlerAttached) return;
            _handlerAttached = true;

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var simpleName = new AssemblyName(args.Name).Name;
                var candidate = Path.Combine(AddinFolder, simpleName + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };
        }
#endif

#if REVIT2025_OR_GREATER
        sealed class XbimLoadContext : AssemblyLoadContext
        {
            public static readonly XbimLoadContext Instance = new XbimLoadContext();

            readonly string _folder;

            XbimLoadContext() : base("IFC2RVT.Xbim", isCollectible: false)
            {
                _folder = AddinFolder;
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                var name = assemblyName.Name;
                if (name == null) return null;

                // Must stay in the default context: the Revit API so that Document and XYZ are the
                // same types on both sides, and IFC2RVT itself so that the contract types are.
                if (name == "IFC2RVT" ||
                    name.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Autodesk.", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("AdWindows", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("UIFramework", StringComparison.OrdinalIgnoreCase))
                    return null;

                // Everything we shipped alongside the add-in is served from here, which is the
                // whole point: xBIM and Microsoft.Extensions.* never reach the default context.
                var candidate = Path.Combine(_folder, name + ".dll");
                if (File.Exists(candidate)) return LoadFromAssemblyPath(candidate);

                // Anything else (the shared framework) falls through to the default context.
                return null;
            }

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            {
                var candidate = Path.Combine(_folder, unmanagedDllName);
                if (File.Exists(candidate)) return LoadUnmanagedDllFromPath(candidate);

                candidate = Path.Combine(_folder, unmanagedDllName + ".dll");
                return File.Exists(candidate)
                    ? LoadUnmanagedDllFromPath(candidate)
                    : IntPtr.Zero;
            }
        }
#endif
    }
}
