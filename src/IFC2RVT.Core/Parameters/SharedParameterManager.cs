using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace IFC2RVT.Parameters
{
    /// <summary>
    /// Creates the shared parameters that IFC property sets land in, one definition group per Pset.
    ///
    /// GUIDs are derived deterministically from the parameter name, so re-importing the same model
    /// - or importing a sibling model from the same exporter - reuses the existing parameters
    /// instead of producing a second set that schedules separately.
    /// </summary>
    public class SharedParameterManager : IDisposable
    {
        readonly Document _doc;
        readonly Autodesk.Revit.ApplicationServices.Application _app;
        readonly string _previousSharedFile;
        readonly string _sharedFilePath;

        readonly Dictionary<string, Definition> _bound =
            new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase);

        CategorySet _bindableCategories;
        bool _disposed;

        public int ParametersCreated { get; private set; }

        public SharedParameterManager(Document doc)
        {
            _doc = doc;
            _app = doc.Application;

            _previousSharedFile = SafeGetSharedFile();
            _sharedFilePath = Path.Combine(
                Path.GetDirectoryName(typeof(SharedParameterManager).Assembly.Location) ?? Path.GetTempPath(),
                "IFC2RVT_SharedParameters.txt");

            EnsureFileExists();
            _app.SharedParametersFilename = _sharedFilePath;

            CacheExistingBindings();
        }

        string SafeGetSharedFile()
        {
            try { return _app.SharedParametersFilename; }
            catch { return null; }
        }

        void EnsureFileExists()
        {
            if (File.Exists(_sharedFilePath) && new FileInfo(_sharedFilePath).Length > 0) return;

            // Revit needs the group/param header lines present before it will open the file.
            var header = "# This is a Revit shared parameter file.\r\n" +
                         "# Do not edit manually.\r\n" +
                         "*META\tVERSION\tMINVERSION\r\n" +
                         "META\t2\t1\r\n" +
                         "*GROUP\tID\tNAME\r\n" +
                         "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n";
            File.WriteAllText(_sharedFilePath, header, new UTF8Encoding(false));
        }

        void CacheExistingBindings()
        {
            var it = _doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (it.Key is Definition d) _bound[d.Name] = d;
            }
        }

        CategorySet BindableCategories
        {
            get
            {
                if (_bindableCategories != null) return _bindableCategories;

                _bindableCategories = _app.Create.NewCategorySet();
                foreach (Category c in _doc.Settings.Categories)
                {
                    // Binding broadly is cheaper than guessing which categories the converter
                    // will end up producing, and avoids a rebind pass per new category.
                    if (c != null && c.AllowsBoundParameters) _bindableCategories.Insert(c);
                }
                return _bindableCategories;
            }
        }

        /// <summary>
        /// Returns a project parameter with the given name and spec, creating and binding it on
        /// first use. Must run inside a transaction. Returns null if Revit rejects the definition.
        /// </summary>
        public Definition GetOrCreate(string groupName, string parameterName, ForgeTypeId spec)
        {
            if (string.IsNullOrWhiteSpace(parameterName)) return null;
            parameterName = Sanitise(parameterName);

            if (_bound.TryGetValue(parameterName, out var existing)) return existing;

            try
            {
                var file = _app.OpenSharedParameterFile();
                if (file == null)
                {
                    EnsureFileExists();
                    _app.SharedParametersFilename = _sharedFilePath;
                    file = _app.OpenSharedParameterFile();
                    if (file == null) return null;
                }

                var group = file.Groups.get_Item(Sanitise(groupName)) ?? file.Groups.Create(Sanitise(groupName));

                var definition = group.Definitions.get_Item(parameterName) as ExternalDefinition;
                if (definition == null)
                {
                    var options = new ExternalDefinitionCreationOptions(parameterName, spec)
                    {
                        GUID = DeterministicGuid(parameterName),
                        UserModifiable = true,
                        Description = "Импортировано из IFC"
                    };
                    definition = group.Definitions.Create(options) as ExternalDefinition;
                    if (definition == null) return null;
                }

                var binding = _app.Create.NewInstanceBinding(BindableCategories);
                if (!InsertBinding(definition, binding))
                {
                    _doc.ParameterBindings.ReInsert(definition, binding, GroupType());
                }

                _bound[parameterName] = definition;
                ParametersCreated++;
                return definition;
            }
            catch
            {
                // Cache the failure so a broken name is not retried thousands of times.
                _bound[parameterName] = null;
                return null;
            }
        }

        bool InsertBinding(Definition definition, Binding binding)
            => _doc.ParameterBindings.Insert(definition, binding, GroupType());

#if REVIT2024_OR_GREATER
        static ForgeTypeId GroupType() => GroupTypeId.Data;
#else
        static BuiltInParameterGroup GroupType() => BuiltInParameterGroup.PG_DATA;
#endif

        /// <summary>Stable GUID per parameter name, so repeated imports converge on one parameter.</summary>
        public static Guid DeterministicGuid(string name)
        {
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes("IFC2RVT|" + name.ToUpperInvariant()));
                return new Guid(bytes);
            }
        }

        /// <summary>Revit forbids these characters in parameter names.</summary>
        public static string Sanitise(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "IFC";
            var cleaned = new string(name.Select(c =>
                c == '{' || c == '}' || c == '[' || c == ']' || c == '|' ||
                c == ';' || c == '<' || c == '>' || c == '?' || c == '`' ||
                c == '~' || c == ':' || c == '\\' || c == '"' ? '_' : c).ToArray()).Trim();

            if (cleaned.Length == 0) cleaned = "IFC";
            return cleaned.Length > 100 ? cleaned.Substring(0, 100) : cleaned;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Leave the user shared-parameter file selection as we found it.
            try
            {
                if (!string.IsNullOrEmpty(_previousSharedFile) && File.Exists(_previousSharedFile))
                    _app.SharedParametersFilename = _previousSharedFile;
            }
            catch { }
        }
    }
}
