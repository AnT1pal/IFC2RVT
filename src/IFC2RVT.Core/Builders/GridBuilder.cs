using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Builders
{
    /// <summary>
    /// IfcGrid to native Revit grids.
    ///
    /// Grids are the thing a drawing is dimensioned from, so an imported model without them is
    /// awkward to work with even when every wall came through perfectly. The data is cheap to read
    /// and was being dropped entirely: the sample model carries five grids and 55 axes.
    ///
    /// Not an IElementBuilder: a grid is not a building element and does not belong in the
    /// element plan, and Revit refuses a second grid on the same line, so these are reconciled
    /// against what the project already has rather than blindly created.
    /// </summary>
    public class GridBuilder
    {
        readonly BuilderContext _ctx;
        readonly double _tolerance;

        public int Created { get; private set; }
        public int Reused { get; private set; }
        public int Failed { get; private set; }

        public GridBuilder(BuilderContext ctx)
        {
            _ctx = ctx;
            _tolerance = ctx.Ifc.Scale.MmToFeet(10.0);
        }

        /// <summary>Must run inside a transaction.</summary>
        public void BuildAll()
        {
            var existing = new FilteredElementCollector(_ctx.Doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .ToList();

            var usedNames = new HashSet<string>(existing.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var grid in _ctx.Ifc.All<IIfcGrid>())
            {
                var world = _ctx.Ifc.Placements.WorldTransform(grid);

                foreach (var axis in AxesOf(grid))
                    Build(axis, world, existing, usedNames);
            }
        }

        static IEnumerable<IIfcGridAxis> AxesOf(IIfcGrid grid)
            => grid.UAxes.Concat(grid.VAxes).Concat(grid.WAxes ?? Enumerable.Empty<IIfcGridAxis>());

        void Build(IIfcGridAxis axis, Transform world, List<Grid> existing, HashSet<string> usedNames)
        {
            var curves = _ctx.Ifc.Curves.Read(axis.AxisCurve);
            if (curves == null || curves.Count == 0) { Failed++; return; }

            // A grid axis is one continuous line or arc; a polyline axis has no Revit equivalent,
            // so the longest segment stands in for it rather than producing a row of stubs.
            var curve = curves.OrderByDescending(SafeLength).First();
            if (SafeLength(curve) <= _ctx.ShortCurveTolerance) { Failed++; return; }

            if (!world.IsIdentity) curve = curve.CreateTransformed(world);

            // Grids live in plan: Revit wants them flat, and an axis that came in tilted would be
            // rejected outright.
            curve = Flatten(curve);
            if (curve == null) { Failed++; return; }

            if (Matches(existing, curve)) { Reused++; return; }

            try
            {
                Grid created;
                switch (curve)
                {
                    case Line line: created = Grid.Create(_ctx.Doc, line); break;
                    case Arc arc: created = Grid.Create(_ctx.Doc, arc); break;
                    default: Failed++; return;
                }

                if (created == null) { Failed++; return; }

                Name(created, IfcHelpers.Str(axis.AxisTag), usedNames);
                existing.Add(created);
                Created++;
            }
            catch
            {
                Failed++;
            }
        }

        /// <summary>Drops the axis to a single elevation; Revit grids are planar by definition.</summary>
        Curve Flatten(Curve curve)
        {
            try
            {
                var start = curve.GetEndPoint(0);
                var end = curve.GetEndPoint(1);

                if (Math.Abs(start.Z - end.Z) <= _tolerance)
                {
                    return Math.Abs(start.Z) <= _tolerance
                        ? curve
                        : curve.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, -start.Z)));
                }

                // A sloped axis is not a grid; flatten it onto the lower end and let the report
                // stay silent - a grid is navigation, not geometry.
                var lower = Math.Min(start.Z, end.Z);
                return Line.CreateBound(new XYZ(start.X, start.Y, lower), new XYZ(end.X, end.Y, lower));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when the project already has a grid on this line.</summary>
        bool Matches(List<Grid> existing, Curve candidate)
        {
            var a0 = candidate.GetEndPoint(0);
            var a1 = candidate.GetEndPoint(1);

            foreach (var grid in existing)
            {
                var other = grid.Curve;
                if (other == null) continue;

                var b0 = other.GetEndPoint(0);
                var b1 = other.GetEndPoint(1);

                var same = a0.DistanceTo(b0) <= _tolerance && a1.DistanceTo(b1) <= _tolerance;
                var reversed = a0.DistanceTo(b1) <= _tolerance && a1.DistanceTo(b0) <= _tolerance;

                if (same || reversed) return true;
            }
            return false;
        }

        void Name(Grid grid, string tag, HashSet<string> usedNames)
        {
            if (string.IsNullOrWhiteSpace(tag)) return;

            var name = tag.Trim();
            for (int attempt = 0; attempt < 50; attempt++)
            {
                var candidate = attempt == 0 ? name : name + "-" + (attempt + 1);
                if (usedNames.Contains(candidate)) continue;

                try
                {
                    grid.Name = candidate;
                    usedNames.Add(candidate);
                    return;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                catch { return; }
            }
        }

        static double SafeLength(Curve c)
        {
            try { return c.Length; }
            catch { return 0.0; }
        }
    }
}
