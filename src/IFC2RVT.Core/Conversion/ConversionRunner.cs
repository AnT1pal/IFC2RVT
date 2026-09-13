using Autodesk.Revit.DB;

namespace IFC2RVT.Conversion
{
    /// <summary>
    /// Entry point of the isolated core. Instantiated reflectively by
    /// <c>IFC2RVT.Hosting.CoreLoader</c> inside the private load context, then called through
    /// <see cref="IConversionRunner"/>, whose types come from IFC2RVT.dll in the default context.
    ///
    /// Nothing xBIM-shaped may appear in this signature: that is what keeps the two contexts
    /// agreeing on the types that cross the boundary.
    /// </summary>
    public class ConversionRunner : IConversionRunner
    {
        public ConversionReport Run(Document document, ConversionOptions options)
            => new ConversionEngine(document, options).Run();
    }
}
