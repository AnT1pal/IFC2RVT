using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Ifc
{
    /// <summary>A swept solid reduced to the pieces a native Revit builder needs.</summary>
    public class ExtrusionInfo
    {
        public IIfcProfileDef Profile { get; set; }

        /// <summary>Placement of the profile plane, relative to the product placement.</summary>
        public Transform Position { get; set; }

        /// <summary>Unit extrusion direction, expressed in the coordinate system of <see cref="Position"/>.</summary>
        public XYZ Direction { get; set; }

        /// <summary>Extrusion depth in feet.</summary>
        public double Depth { get; set; }

        /// <summary>True when the solid was clipped or cut; the native result will be approximate.</summary>
        public bool WasClipped { get; set; }

        /// <summary>
        /// Half-space planes the solid was cut by, in product coordinates. A wall clipped by the
        /// roof carries its real top here; without them the only height available is the uncut
        /// extrusion, which drives the wall straight through the roof.
        /// </summary>
        public List<ClipPlane> Clips { get; } = new List<ClipPlane>();

        /// <summary>Extrusion direction taken into the product coordinate system.</summary>
        public XYZ WorldDirection => Position.OfVector(Direction).Normalize();
    }

    /// <summary>A cutting plane, with the side that survives the cut.</summary>
    public class ClipPlane
    {
        public XYZ Origin { get; set; }
        public XYZ Normal { get; set; }

        /// <summary>True when material on the positive side of the normal is kept.</summary>
        public bool KeepsPositiveSide { get; set; }

        /// <summary>Height of the plane above a point, or NaN when the plane is vertical.</summary>
        public double ElevationAt(XYZ point)
        {
            if (Math.Abs(Normal.Z) < 1e-6) return double.NaN;
            var d = (point.X - Origin.X) * Normal.X + (point.Y - Origin.Y) * Normal.Y;
            return Origin.Z - d / Normal.Z;
        }
    }

    /// <summary>
    /// Navigates IfcProductDefinitionShape down to the representation items the builders care
    /// about. Mapped representations are followed and their transforms composed, so a product
    /// that reuses a shared shape reads the same as one carrying its own geometry.
    /// </summary>
    public class RepresentationReader
    {
        readonly IfcScale _scale;
        readonly PlacementResolver _placements;
        readonly CurveReader _curves;

        public RepresentationReader(IfcScale scale, PlacementResolver placements, CurveReader curves)
        {
            _scale = scale;
            _placements = placements;
            _curves = curves;
        }

        public IIfcShapeRepresentation FindRepresentation(IIfcProduct product, string identifier)
        {
            var shape = product?.Representation as IIfcProductDefinitionShape;
            if (shape == null) return null;

            return shape.Representations
                        .OfType<IIfcShapeRepresentation>()
                        .FirstOrDefault(r => string.Equals(
                            IfcHelpers.Str(r.RepresentationIdentifier),
                            identifier,
                            StringComparison.OrdinalIgnoreCase));
        }

        public IIfcShapeRepresentation BodyOf(IIfcProduct p) => FindRepresentation(p, "Body");
        public IIfcShapeRepresentation AxisOf(IIfcProduct p) => FindRepresentation(p, "Axis");
        public IIfcShapeRepresentation FootPrintOf(IIfcProduct p) => FindRepresentation(p, "FootPrint");

        /// <summary>
        /// Flattens a representation into its geometric items, resolving IfcMappedItem and
        /// accumulating the mapping transform alongside each item.
        /// </summary>
        public IEnumerable<Tuple<IIfcRepresentationItem, Transform>> Items(
            IIfcRepresentation representation, Transform accumulated = null, int depth = 0)
        {
            if (representation == null || depth > 8) yield break;   // mapped representations do not nest deeply
            var outer = accumulated ?? Transform.Identity;

            foreach (var item in representation.Items)
            {
                if (item is IIfcMappedItem mapped)
                {
                    var source = mapped.MappingSource;
                    if (source == null) continue;

                    var origin = _placements.FromAxisPlacement(source.MappingOrigin);
                    var target = TargetTransform(mapped.MappingTarget);
                    var composed = outer.Multiply(target).Multiply(origin);

                    foreach (var nested in Items(source.MappedRepresentation, composed, depth + 1))
                        yield return nested;
                }
                else
                {
                    yield return Tuple.Create(item, outer);
                }
            }
        }

        Transform TargetTransform(IIfcCartesianTransformationOperator op)
        {
            if (op == null) return Transform.Identity;

            var t = Transform.Identity;
            t.Origin = _curves.Point(op.LocalOrigin);

            var x = IfcHelpers.Ratios(op.Axis1, new[] { 1.0, 0.0, 0.0 });
            var bx = new XYZ(x[0], x[1], x[2]);
            if (bx.GetLength() < 1e-9) bx = XYZ.BasisX;
            bx = bx.Normalize();

            XYZ bz = XYZ.BasisZ;
            if (op is IIfcCartesianTransformationOperator3D op3)
            {
                var z = IfcHelpers.Ratios(op3.Axis3, new[] { 0.0, 0.0, 1.0 });
                bz = new XYZ(z[0], z[1], z[2]);
                if (bz.GetLength() < 1e-9) bz = XYZ.BasisZ;
                bz = bz.Normalize();
            }

            // Re-orthogonalise: exporters are not always careful about this.
            var projected = bx - bz.Multiply(bx.DotProduct(bz));
            bx = projected.GetLength() < 1e-9 ? ArbitraryPerpendicular(bz) : projected.Normalize();

            t.BasisX = bx;
            t.BasisZ = bz;
            t.BasisY = bz.CrossProduct(bx);

            // Scl is the derived attribute, already defaulted to 1.0 when Scale is absent.
            var scale = (double)op.Scl;
            return Math.Abs(scale - 1.0) < 1e-12 || Math.Abs(scale) < 1e-12 ? t : t.ScaleBasis(scale);
        }

        static XYZ ArbitraryPerpendicular(XYZ z)
            => Math.Abs(z.DotProduct(XYZ.BasisZ)) > 0.9
                ? XYZ.BasisX
                : XYZ.BasisZ.CrossProduct(z).Normalize();

        /// <summary>
        /// The dominant extrusion of a product, or null when the body is not extrusion-based.
        /// Boolean results are followed down their first operand: in Revit-authored IFC the
        /// clipping operand is the roof cut or the wall end, and the base extrusion is what
        /// carries the wall axis and thickness we need.
        /// </summary>
        public ExtrusionInfo LargestExtrusion(IIfcProduct product)
        {
            var body = BodyOf(product);
            if (body == null) return null;

            ExtrusionInfo best = null;
            foreach (var pair in Items(body))
            {
                var info = ExtrusionFrom(pair.Item1, pair.Item2, false);
                if (info == null) continue;
                if (best == null || info.Depth > best.Depth) best = info;
            }
            return best;
        }

        public List<ExtrusionInfo> AllExtrusions(IIfcProduct product)
        {
            var body = BodyOf(product);
            var result = new List<ExtrusionInfo>();
            if (body == null) return result;

            foreach (var pair in Items(body))
            {
                var info = ExtrusionFrom(pair.Item1, pair.Item2, false);
                if (info != null) result.Add(info);
            }
            return result;
        }

        ExtrusionInfo ExtrusionFrom(IIfcRepresentationItem item, Transform outer, bool clipped, int depth = 0)
        {
            // A wall with many openings is a stack of boolean subtractions, one level per hole.
            // The old limit of 8 quietly lost the extrusion on such walls, and with it the only
            // source of wall height - which is why walls containing doors and windows came out as
            // DirectShape while their plain neighbours converted natively.
            if (depth > 64) return null;

            switch (item)
            {
                case IIfcExtrudedAreaSolid solid:
                {
                    var ratios = IfcHelpers.Ratios(solid.ExtrudedDirection, new[] { 0.0, 0.0, 1.0 });
                    var dir = new XYZ(ratios[0], ratios[1], ratios[2]);
                    if (dir.GetLength() < 1e-12) return null;

                    return new ExtrusionInfo
                    {
                        Profile = solid.SweptArea,
                        Position = outer.Multiply(_placements.From3D(solid.Position)),
                        Direction = dir.Normalize(),
                        Depth = (double)solid.Depth * _scale.Length,
                        WasClipped = clipped
                    };
                }

                case IIfcBooleanResult boolean:
                {
                    var inner = ExtrusionFrom(boolean.FirstOperand as IIfcRepresentationItem, outer, true, depth + 1);
                    if (inner == null) return null;

                    var clip = ClipFrom(boolean.SecondOperand as IIfcRepresentationItem, outer);
                    if (clip != null) inner.Clips.Add(clip);
                    return inner;
                }

                default:
                    return null;
            }
        }

        /// <summary>Reads the cutting plane out of a half-space operand.</summary>
        ClipPlane ClipFrom(IIfcRepresentationItem item, Transform outer)
        {
            if (!(item is IIfcHalfSpaceSolid half)) return null;
            if (!(half.BaseSurface is IIfcPlane plane)) return null;

            var frame = outer.Multiply(_placements.From3D(plane.Position));

            return new ClipPlane
            {
                Origin = frame.Origin,
                Normal = frame.BasisZ,
                KeepsPositiveSide = half.AgreementFlag
            };
        }

        /// <summary>Curves of the Axis representation, in product coordinates.</summary>
        public List<Curve> AxisCurves(IIfcProduct product)
        {
            var axis = AxisOf(product);
            if (axis == null) return null;

            var result = new List<Curve>();
            foreach (var pair in Items(axis))
            {
                if (!(pair.Item1 is IIfcCurve curve)) continue;

                var read = _curves.Read(curve);
                if (read == null) continue;

                var t = pair.Item2;
                result.AddRange(t.IsIdentity ? read : read.Select(c => c.CreateTransformed(t)));
            }
            return result.Count > 0 ? result : null;
        }
    }
}
