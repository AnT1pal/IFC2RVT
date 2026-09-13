using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Ifc
{
    /// <summary>
    /// Resolves an IfcProfileDef into Revit CurveLoops in the plane of the profile, scaled to feet.
    /// The first loop is the outer boundary; any further loops are voids.
    /// Returns null for profile kinds with no faithful representation, which routes the element
    /// to the DirectShape fallback.
    /// </summary>
    public class ProfileReader
    {
        readonly IfcScale _scale;
        readonly CurveReader _curves;
        readonly PlacementResolver _placements;
        readonly double _shortCurveTolerance;

        public ProfileReader(IfcScale scale, CurveReader curves, PlacementResolver placements,
                             double shortCurveTolerance)
        {
            _scale = scale;
            _curves = curves;
            _placements = placements;
            _shortCurveTolerance = shortCurveTolerance;
        }

        public List<CurveLoop> Read(IIfcProfileDef profile)
        {
            switch (profile)
            {
                case IIfcArbitraryProfileDefWithVoids withVoids: return FromArbitraryWithVoids(withVoids);
                case IIfcArbitraryClosedProfileDef closed: return FromArbitrary(closed);
                case IIfcRectangleHollowProfileDef rh: return FromRectangleHollow(rh);
                case IIfcRectangleProfileDef r: return FromRectangle(r);
                case IIfcCircleHollowProfileDef ch: return FromCircleHollow(ch);
                case IIfcCircleProfileDef c: return FromCircle(c);
                case IIfcIShapeProfileDef i: return FromIShape(i);
                case IIfcDerivedProfileDef d: return FromDerived(d);
                case IIfcCompositeProfileDef comp: return FromComposite(comp);
                default: return null;
            }
        }

        /// <summary>Nominal thickness of a profile measured across its narrow axis, in feet.</summary>
        public double ApproximateThickness(IIfcProfileDef profile)
        {
            switch (profile)
            {
                case IIfcRectangleProfileDef r:
                    return Math.Min((double)r.XDim, (double)r.YDim) * _scale.Length;
                case IIfcCircleProfileDef c:
                    return (double)c.Radius * 2.0 * _scale.Length;
                default:
                {
                    var loops = Read(profile);
                    if (loops == null || loops.Count == 0) return 0.0;
                    var bb = BoundingBoxOf(loops[0]);
                    return Math.Min(bb.Item2.X - bb.Item1.X, bb.Item2.Y - bb.Item1.Y);
                }
            }
        }

        public static Tuple<XYZ, XYZ> BoundingBoxOf(CurveLoop loop)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var c in loop)
            {
                foreach (var p in c.Tessellate())
                {
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                    minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
                }
            }
            return Tuple.Create(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
        }

        // ---- arbitrary outlines -----------------------------------------------------------

        List<CurveLoop> FromArbitrary(IIfcArbitraryClosedProfileDef p)
        {
            var loop = LoopFrom(p.OuterCurve);
            return loop == null ? null : new List<CurveLoop> { loop };
        }

        List<CurveLoop> FromArbitraryWithVoids(IIfcArbitraryProfileDefWithVoids p)
        {
            var outer = LoopFrom(p.OuterCurve);
            if (outer == null) return null;

            var loops = new List<CurveLoop> { outer };
            foreach (var inner in p.InnerCurves)
            {
                var l = LoopFrom(inner);
                // A void we cannot read is better dropped than allowed to corrupt the outline.
                if (l != null) loops.Add(l);
            }
            return loops;
        }

        CurveLoop LoopFrom(IIfcCurve curve)
        {
            var segments = _curves.Read(curve);
            return segments == null ? null : BuildLoop(segments);
        }

        /// <summary>
        /// Assembles curves into a closed CurveLoop, bridging the small gaps that IFC exporters
        /// leave behind and dropping duplicate end points.
        /// </summary>
        public CurveLoop BuildLoop(List<Curve> segments)
        {
            if (segments == null || segments.Count == 0) return null;

            var loop = new CurveLoop();
            XYZ first = null, previous = null;

            foreach (var c in segments)
            {
                var start = c.GetEndPoint(0);
                var end = c.GetEndPoint(1);
                if (start.DistanceTo(end) <= _shortCurveTolerance) continue;

                if (previous != null && previous.DistanceTo(start) > _shortCurveTolerance)
                {
                    // Gap between consecutive segments: stitch it rather than fail the loop.
                    try { loop.Append(Line.CreateBound(previous, start)); }
                    catch { return null; }
                }

                try { loop.Append(c); }
                catch { return null; }

                if (first == null) first = start;
                previous = end;
            }

            if (first == null || previous == null) return null;

            if (previous.DistanceTo(first) > _shortCurveTolerance)
            {
                try { loop.Append(Line.CreateBound(previous, first)); }
                catch { return null; }
            }

            return loop.IsOpen() ? null : loop;
        }

        // ---- parametric outlines ----------------------------------------------------------

        List<CurveLoop> FromRectangle(IIfcRectangleProfileDef r)
        {
            var frame = _placements.FromAxisPlacement(r.Position);
            var loop = Rectangle(frame, (double)r.XDim * _scale.Length, (double)r.YDim * _scale.Length);
            return loop == null ? null : new List<CurveLoop> { loop };
        }

        List<CurveLoop> FromRectangleHollow(IIfcRectangleHollowProfileDef r)
        {
            var frame = _placements.FromAxisPlacement(r.Position);
            var x = (double)r.XDim * _scale.Length;
            var y = (double)r.YDim * _scale.Length;
            var t = (double)r.WallThickness * _scale.Length;

            var outer = Rectangle(frame, x, y);
            if (outer == null) return null;

            var loops = new List<CurveLoop> { outer };
            var inner = Rectangle(frame, x - 2 * t, y - 2 * t);
            if (inner != null) loops.Add(inner);
            return loops;
        }

        CurveLoop Rectangle(Transform frame, double xDim, double yDim)
        {
            if (xDim <= _shortCurveTolerance || yDim <= _shortCurveTolerance) return null;

            var hx = xDim / 2.0;
            var hy = yDim / 2.0;
            var pts = new[]
            {
                frame.OfPoint(new XYZ(-hx, -hy, 0)),
                frame.OfPoint(new XYZ( hx, -hy, 0)),
                frame.OfPoint(new XYZ( hx,  hy, 0)),
                frame.OfPoint(new XYZ(-hx,  hy, 0))
            };

            var segments = new List<Curve>();
            for (int i = 0; i < 4; i++)
                segments.Add(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
            return BuildLoop(segments);
        }

        List<CurveLoop> FromCircle(IIfcCircleProfileDef c)
        {
            var frame = _placements.FromAxisPlacement(c.Position);
            var loop = Circle(frame, (double)c.Radius * _scale.Length);
            return loop == null ? null : new List<CurveLoop> { loop };
        }

        List<CurveLoop> FromCircleHollow(IIfcCircleHollowProfileDef c)
        {
            var frame = _placements.FromAxisPlacement(c.Position);
            var radius = (double)c.Radius * _scale.Length;
            var t = (double)c.WallThickness * _scale.Length;

            var outer = Circle(frame, radius);
            if (outer == null) return null;

            var loops = new List<CurveLoop> { outer };
            var inner = Circle(frame, radius - t);
            if (inner != null) loops.Add(inner);
            return loops;
        }

        CurveLoop Circle(Transform frame, double radius)
        {
            if (radius <= _shortCurveTolerance) return null;
            return BuildLoop(new List<Curve>
            {
                Arc.Create(frame.Origin, radius, 0, Math.PI, frame.BasisX, frame.BasisY),
                Arc.Create(frame.Origin, radius, Math.PI, 2 * Math.PI, frame.BasisX, frame.BasisY)
            });
        }

        /// <summary>Sharp-cornered I outline. Fillets are ignored - they do not survive into a
        /// Revit family type anyway, and the area error is well under a millimetre of wall.</summary>
        List<CurveLoop> FromIShape(IIfcIShapeProfileDef p)
        {
            var frame = _placements.FromAxisPlacement(p.Position);
            var b = (double)p.OverallWidth * _scale.Length;
            var h = (double)p.OverallDepth * _scale.Length;
            var tw = (double)p.WebThickness * _scale.Length;
            var tf = (double)p.FlangeThickness * _scale.Length;

            if (b <= 0 || h <= 0 || tw <= 0 || tf <= 0 || h <= 2 * tf || b <= tw) return null;

            var hb = b / 2.0;
            var hh = h / 2.0;
            var hw = tw / 2.0;

            var local = new[]
            {
                new XYZ(-hb, -hh, 0),      new XYZ( hb, -hh, 0),
                new XYZ( hb, -hh + tf, 0), new XYZ( hw, -hh + tf, 0),
                new XYZ( hw,  hh - tf, 0), new XYZ( hb,  hh - tf, 0),
                new XYZ( hb,  hh, 0),      new XYZ(-hb,  hh, 0),
                new XYZ(-hb,  hh - tf, 0), new XYZ(-hw,  hh - tf, 0),
                new XYZ(-hw, -hh + tf, 0), new XYZ(-hb, -hh + tf, 0)
            };

            var pts = local.Select(frame.OfPoint).ToList();
            var segments = new List<Curve>();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var next = pts[(i + 1) % pts.Count];
                if (a.DistanceTo(next) <= _shortCurveTolerance) continue;
                segments.Add(Line.CreateBound(a, next));
            }

            var loop = BuildLoop(segments);
            return loop == null ? null : new List<CurveLoop> { loop };
        }

        // ---- composition ------------------------------------------------------------------

        List<CurveLoop> FromDerived(IIfcDerivedProfileDef d)
        {
            var parent = Read(d.ParentProfile);
            if (parent == null) return null;

            var op = CartesianOperator(d.Operator);
            if (op == null) return parent;

            return parent.Select(l => CurveLoop.CreateViaTransform(l, op)).ToList();
        }

        List<CurveLoop> FromComposite(IIfcCompositeProfileDef c)
        {
            var loops = new List<CurveLoop>();
            foreach (var p in c.Profiles)
            {
                var part = Read(p);
                if (part == null) return null;
                loops.AddRange(part);
            }
            return loops.Count > 0 ? loops : null;
        }

        Transform CartesianOperator(IIfcCartesianTransformationOperator op)
        {
            if (op == null) return null;

            var origin = _curves.Point(op.LocalOrigin);
            // Scl is the derived attribute, already defaulted to 1.0 when Scale is absent.
            var scale = (double)op.Scl;
            if (Math.Abs(scale) < 1e-12) scale = 1.0;

            var x = IfcHelpers.Ratios(op.Axis1, new[] { 1.0, 0.0, 0.0 });
            var y = IfcHelpers.Ratios(op.Axis2, new[] { 0.0, 1.0, 0.0 });

            var bx = new XYZ(x[0], x[1], x[2]);
            var by = new XYZ(y[0], y[1], y[2]);
            if (bx.GetLength() < 1e-9) bx = XYZ.BasisX;
            if (by.GetLength() < 1e-9) by = XYZ.BasisY;
            bx = bx.Normalize();
            by = by.Normalize();

            var t = Transform.Identity;
            t.Origin = origin;
            t.BasisX = bx;
            t.BasisY = by;
            t.BasisZ = bx.CrossProduct(by).Normalize();
            return Math.Abs(scale - 1.0) < 1e-12 ? t : t.ScaleBasis(scale);
        }
    }
}
