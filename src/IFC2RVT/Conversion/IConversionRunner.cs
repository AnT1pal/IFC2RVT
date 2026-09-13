using Autodesk.Revit.DB;

namespace IFC2RVT.Conversion
{
    /// <summary>
    /// The boundary between the Revit-facing add-in and the xBIM-backed conversion core.
    ///
    /// This interface, along with <see cref="ConversionOptions"/> and <see cref="ConversionReport"/>,
    /// lives in IFC2RVT.dll and is deliberately kept free of any xBIM type. IFC2RVT.dll is loaded
    /// once in the default context and shared with the isolated context that hosts the core, so
    /// these types have one identity on both sides of the boundary.
    ///
    /// See <c>XbimLoadContext</c> for why the isolation exists.
    /// </summary>
    public interface IConversionRunner
    {
        ConversionReport Run(Document document, ConversionOptions options);
    }
}
