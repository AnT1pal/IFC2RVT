using Xbim.Common;

namespace IFC2RVT.Ifc
{
    /// <summary>
    /// Converts raw IFC numbers into Revit internal units (decimal feet).
    /// IFC files carry their own unit assignment (this sample is in millimetres), xBIM resolves
    /// it into ModelFactors, and everything downstream multiplies by <see cref="Length"/> exactly once.
    /// </summary>
    public class IfcScale
    {
        public const double FeetPerMetre = 1.0 / 0.3048;

        /// <summary>Multiply a raw IFC length by this to get decimal feet.</summary>
        public double Length { get; }

        /// <summary>Multiply a raw IFC plane angle by this to get radians.</summary>
        public double Angle { get; }

        /// <summary>Model precision expressed in feet, clamped to something Revit tolerates.</summary>
        public double Precision { get; }

        public IfcScale(IModel model)
        {
            var f = model.ModelFactors;
            Length = f.LengthToMetresConversionFactor * FeetPerMetre;
            Angle = f.AngleToRadiansConversionFactor;

            var p = f.Precision * Length;
            if (p <= 0 || double.IsNaN(p)) p = 1e-5;
            Precision = p < 1e-7 ? 1e-7 : (p > 1e-3 ? 1e-3 : p);
        }

        public double ToFeet(double ifcLength) => ifcLength * Length;
        public double MmToFeet(double mm) => mm / 1000.0 * FeetPerMetre;
    }
}
