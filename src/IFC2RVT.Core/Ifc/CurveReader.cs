using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Ifc
{
    /// <summary>
    /// Turns IfcCurve geometry into Revit curves, in the local coordinate space of the curve and
    /// already scaled to feet. Returns null for anything it cannot represent faithfully - callers
    /// treat null as "hand this element to the DirectShape fallback" rather than silently
    /// producing approximate geometry.
    /// </summary>
    public class CurveReader
    {
        readonly IfcScale _scale;
        readonly PlacementResolver _placements;
        readonly double _shortCurveTolerance;

        public CurveReader(IfcScale scale, PlacementResolver placements, double shortCurveTolerance)
        {
            _scale = scale;
            _placements = placements;
            _shortCurveTolerance = shortCurveTolerance;
        }

        public XYZ Point(IIfcCartesianPoint p)
        {
            var c = IfcHelpers.Coords(p);
            return new XYZ(c[0] * _scale.Length, c[1] * _scale.Length, c[2] * _scale.Length);
        }

        public List<Curve> Read(IIfcCurve curve)
        {
            switch (curve)
            {
                case IIfcPolyline pl: return FromPolyline(pl);
                case IIfcCompositeCurve cc: return FromComposite(cc);
                case IIfcTrimmedCurve tc: return FromTrimmed(tc);
                case IIfcCircle c: return FromFullCircle(c);
                case IIfcEllipse e: return FromEllipse(e);
                case IIfcIndexedPolyCurve ipc: return FromIndexedPolyCurve(ipc);
                case IIfcBSplineCurveWithKnots bs: return FromBSpline(bs);
                default: return null;
            }
        }

        // ---- polyline -------------------------------------------------------------------

        List<Curve> FromPolyline(IIfcPolyline pl)
        {
            var pts = pl.Points.Select(Point).ToList();
            return Chain(pts);
        }

        List<Curve> Chain(List<XYZ> pts)
        {
            if (pts.Count < 2) return null;
            var result = new List<Curve>();
            for (int i = 0; i < pts.Count - 1; i++)
            {
                // Collapse micro-segments instead of letting Revit reject the whole loop.
                if (pts[i].DistanceTo(pts[i + 1]) <= _shortCurveTolerance) continue;
                result.Add(Line.CreateBound(pts[i], pts[i + 1]));
            }
            return result.Count > 0 ? result : null;
        }

        // ---- composite ------------------------------------------------------------------

        List<Curve> FromComposite(IIfcCompositeCurve cc)
        {
            var result = new List<Curve>();
            foreach (var seg in cc.Segments)
            {
                var part = Read(seg.ParentCurve);
                if (part == null) return null;

                if (!seg.SameSense)
                {
                    part.Reverse();
                    part = part.Select(c => c.CreateReversed()).ToList();
                }
                result.AddRange(part);
            }
            return result.Count > 0 ? result : null;
        }

        // ---- trimmed --------------------------------------------------------------------

        List<Curve> FromTrimmed(IIfcTrimmedCurve tc)
        {
            var preferCartesian = tc.MasterRepresentation == IfcTrimmingPreference.CARTESIAN;
            var p1 = TrimPoint(tc.Trim1);
            var p2 = TrimPoint(tc.Trim2);
            var u1 = TrimParameter(tc.Trim1);
            var u2 = TrimParameter(tc.Trim2);
            var sense = tc.SenseAgreement;

            switch (tc.BasisCurve)
            {
                case IIfcLine line:
                    return TrimLine(line, p1, p2, u1, u2, sense, preferCartesian);

                case IIfcCircle circle:
                    return TrimCircle(circle, p1, p2, u1, u2, sense, preferCartesian);

                default:
                    // Trimming an arbitrary basis curve (b-spline and friends) is not worth
                    // approximating; return the untrimmed curve and let the caller judge.
                    return Read(tc.BasisCurve);
            }
        }

        List<Curve> TrimLine(IIfcLine line, XYZ p1, XYZ p2, double? u1, double? u2,
                             bool sense, bool preferCartesian)
        {
            var origin = Point(line.Pnt);
            var ratios = IfcHelpers.Ratios(line.Dir?.Orientation, new[] { 1.0, 0.0, 0.0 });
            var magnitude = line.Dir == null ? 1.0 : (double)line.Dir.Magnitude;
            var dir = new XYZ(ratios[0], ratios[1], ratios[2]);
            if (dir.GetLength() < 1e-12) return null;

            XYZ a, b;
            if (preferCartesian && p1 != null && p2 != null)
            {
                a = p1; b = p2;
            }
            else if (u1.HasValue && u2.HasValue)
            {
                // IfcLine parameterisation runs in multiples of Dir.Magnitude.
                var step = dir.Normalize().Multiply(magnitude * _scale.Length);
                a = origin + step.Multiply(u1.Value);
                b = origin + step.Multiply(u2.Value);
            }
            else if (p1 != null && p2 != null)
            {
                a = p1; b = p2;
            }
            else return null;

            if (!sense) { var t = a; a = b; b = t; }
            if (a.DistanceTo(b) <= _shortCurveTolerance) return null;
            return new List<Curve> { Line.CreateBound(a, b) };
        }

        List<Curve> TrimCircle(IIfcCircle circle, XYZ p1, XYZ p2, double? u1, double? u2,
                               bool sense, bool preferCartesian)
        {
            var radius = (double)circle.Radius * _scale.Length;
            if (radius <= _shortCurveTolerance) return null;

            var frame = _placements.FromAxisPlacement(circle.Position);
            double a1, a2;

            if (u1.HasValue && u2.HasValue && !preferCartesian)
            {
                a1 = u1.Value * _scale.Angle;
                a2 = u2.Value * _scale.Angle;
            }
            else if (p1 != null && p2 != null)
            {
                a1 = AngleIn(frame, p1);
                a2 = AngleIn(frame, p2);
            }
            else return null;

            return BuildArc(frame, radius, a1, a2, sense);
        }

        static XYZ TrimPointCore(IEnumerable<IIfcTrimmingSelect> trim, Func<IIfcCartesianPoint, XYZ> conv)
        {
            foreach (var t in trim)
                if (t is IIfcCartesianPoint cp) return conv(cp);
            return null;
        }

        XYZ TrimPoint(IEnumerable<IIfcTrimmingSelect> trim) => TrimPointCore(trim, Point);

        static double? TrimParameter(IEnumerable<IIfcTrimmingSelect> trim)
        {
            foreach (var t in trim)
                if (t is Xbim.Ifc4.MeasureResource.IfcParameterValue pv) return (double)pv;
            return null;
        }

        // ---- circles, arcs, ellipses -----------------------------------------------------

        static double AngleIn(Transform frame, XYZ worldPoint)
        {
            var local = frame.Inverse.OfPoint(worldPoint);
            return Math.Atan2(local.Y, local.X);
        }

        List<Curve> BuildArc(Transform frame, double radius, double a1, double a2, bool sense)
        {
            if (!sense) { var t = a1; a1 = a2; a2 = t; }

            var sweep = a2 - a1;
            while (sweep <= 0) sweep += 2 * Math.PI;
            while (sweep > 2 * Math.PI) sweep -= 2 * Math.PI;

            // A closed circle cannot be a single Revit Arc.
            if (sweep < 1e-9 || Math.Abs(sweep - 2 * Math.PI) < 1e-9)
                return SplitCircle(frame, radius);

            // Revit rejects arcs spanning close to a full turn; halve anything large.
            if (sweep > Math.PI * 1.9)
            {
                var mid = a1 + sweep / 2.0;
                return new List<Curve>
                {
                    Arc.Create(frame.Origin, radius, a1, mid, frame.BasisX, frame.BasisY),
                    Arc.Create(frame.Origin, radius, mid, a1 + sweep, frame.BasisX, frame.BasisY)
                };
            }

            return new List<Curve>
            {
                Arc.Create(frame.Origin, radius, a1, a1 + sweep, frame.BasisX, frame.BasisY)
            };
        }

        List<Curve> FromFullCircle(IIfcCircle c)
        {
            var radius = (double)c.Radius * _scale.Length;
            if (radius <= _shortCurveTolerance) return null;
            return SplitCircle(_placements.FromAxisPlacement(c.Position), radius);
        }

        static List<Curve> SplitCircle(Transform frame, double radius)
            => new List<Curve>
            {
                Arc.Create(frame.Origin, radius, 0, Math.PI, frame.BasisX, frame.BasisY),
                Arc.Create(frame.Origin, radius, Math.PI, 2 * Math.PI, frame.BasisX, frame.BasisY)
            };

        List<Curve> FromEllipse(IIfcEllipse e)
        {
            var frame = _placements.FromAxisPlacement(e.Position);
            var rx = (double)e.SemiAxis1 * _scale.Length;
            var ry = (double)e.SemiAxis2 * _scale.Length;
            if (rx <= _shortCurveTolerance || ry <= _shortCurveTolerance) return null;

            return new List<Curve>
            {
                Ellipse.CreateCurve(frame.Origin, rx, ry, frame.BasisX, frame.BasisY, 0, Math.PI),
                Ellipse.CreateCurve(frame.Origin, rx, ry, frame.BasisX, frame.BasisY, Math.PI, 2 * Math.PI)
            };
        }

        // ---- IFC4 indexed poly curve -----------------------------------------------------

        List<Curve> FromIndexedPolyCurve(IIfcIndexedPolyCurve ipc)
        {
            var pts = ReadPointList(ipc.Points);
            if (pts == null || pts.Count < 2) return null;

            var segments = ipc.Segments?.ToList();
            if (segments == null || segments.Count == 0) return Chain(pts);

            var result = new List<Curve>();
            foreach (var seg in segments)
            {
                // IfcLineIndex and IfcArcIndex are EXPRESS defined types, not entities: xBIM
                // models them as value structs holding the index list in Value.
                if (seg is Xbim.Ifc4.GeometryResource.IfcLineIndex li)
                {
                    var idx = ReadIndices(li.Value);
                    if (idx == null) continue;
                    for (int i = 0; i < idx.Count - 1; i++)
                    {
                        var a = At(pts, idx[i]);
                        var b = At(pts, idx[i + 1]);
                        if (a == null || b == null || a.DistanceTo(b) <= _shortCurveTolerance) continue;
                        result.Add(Line.CreateBound(a, b));
                    }
                }
                else if (seg is Xbim.Ifc4.GeometryResource.IfcArcIndex ai)
                {
                    var idx = ReadIndices(ai.Value);
                    if (idx == null || idx.Count < 3) continue;
                    var a = At(pts, idx[0]);
                    var m = At(pts, idx[1]);
                    var b = At(pts, idx[2]);
                    if (a == null || m == null || b == null) continue;
                    if (a.DistanceTo(b) <= _shortCurveTolerance) continue;
                    try { result.Add(Arc.Create(a, b, m)); }
                    catch { result.Add(Line.CreateBound(a, b)); }
                }
            }
            return result.Count > 0 ? result : null;
        }

        /// <summary>Unwraps the boxed index list of an IfcLineIndex / IfcArcIndex.</summary>
        static List<int> ReadIndices(object boxedList)
        {
            if (!(boxedList is System.Collections.IEnumerable items)) return null;

            var result = new List<int>();
            foreach (var item in items)
            {
                var value = item is Xbim.Common.IExpressValueType ev ? ev.Value : item;
                if (value == null) return null;
                try { result.Add(Convert.ToInt32(value)); }
                catch { return null; }
            }
            return result.Count > 0 ? result : null;
        }

        static XYZ At(List<XYZ> pts, int oneBasedIndex)
            => oneBasedIndex >= 1 && oneBasedIndex <= pts.Count ? pts[oneBasedIndex - 1] : null;

        List<XYZ> ReadPointList(IIfcCartesianPointList list)
        {
            switch (list)
            {
                case IIfcCartesianPointList3D l3:
                    return l3.CoordList
                             .Select(c => new XYZ(c[0] * _scale.Length,
                                                  c[1] * _scale.Length,
                                                  c[2] * _scale.Length))
                             .ToList();
                case IIfcCartesianPointList2D l2:
                    return l2.CoordList
                             .Select(c => new XYZ(c[0] * _scale.Length,
                                                  c[1] * _scale.Length,
                                                  0.0))
                             .ToList();
                default:
                    return null;
            }
        }

        // ---- b-splines -------------------------------------------------------------------

        List<Curve> FromBSpline(IIfcBSplineCurveWithKnots bs)
        {
            try
            {
                var ctrl = bs.ControlPointsList.OfType<IIfcCartesianPoint>().Select(Point).ToList();
                if (ctrl.Count < 2) return null;

                var degree = (int)bs.Degree;
                var multiplicities = bs.KnotMultiplicities.Select(m => (int)m).ToList();
                var values = bs.Knots.Select(k => (double)k).ToList();

                var knots = new List<double>();
                for (int i = 0; i < values.Count && i < multiplicities.Count; i++)
                    for (int j = 0; j < multiplicities[i]; j++)
                        knots.Add(values[i]);

                // Revit demands the canonical knot count; a mismatch means we misread the curve.
                if (knots.Count != ctrl.Count + degree + 1) return null;

                if (bs is IIfcRationalBSplineCurveWithKnots rational)
                {
                    var weights = rational.WeightsData.Select(w => (double)w).ToList();
                    if (weights.Count == ctrl.Count)
                        return new List<Curve> { NurbSpline.CreateCurve(degree, knots, ctrl, weights) };
                }

                return new List<Curve> { NurbSpline.CreateCurve(degree, knots, ctrl) };
            }
            catch
            {
                return null;
            }
        }
    }
}
