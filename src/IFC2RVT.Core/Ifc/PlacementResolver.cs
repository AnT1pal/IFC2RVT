using System.Collections.Generic;
using Autodesk.Revit.DB;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Ifc
{
    /// <summary>
    /// Flattens the IfcLocalPlacement chain into a single Revit Transform per product.
    /// Placements are heavily shared (17k of them in the sample for 17k products), so results
    /// are cached by entity label - resolving naively would walk the same parents thousands of times.
    /// </summary>
    public class PlacementResolver
    {
        readonly IfcScale _scale;
        readonly Dictionary<int, Transform> _cache = new Dictionary<int, Transform>();

        Transform _root = Transform.Identity;

        public PlacementResolver(IfcScale scale) => _scale = scale;

        /// <summary>
        /// Translation applied ahead of every placement chain, used to bring a model authored in
        /// site coordinates back near the Revit origin. Must be set before anything is resolved:
        /// results are cached, and the cache is dropped here to make that safe.
        /// </summary>
        public void SetRootOffset(XYZ offsetFeet)
        {
            _root = offsetFeet == null || offsetFeet.IsZeroLength()
                ? Transform.Identity
                : Transform.CreateTranslation(offsetFeet);
            _cache.Clear();
        }

        public Transform WorldTransform(IIfcProduct product)
            => product?.ObjectPlacement == null ? Transform.Identity : Resolve(product.ObjectPlacement);

        public Transform Resolve(IIfcObjectPlacement placement)
        {
            if (placement == null) return Transform.Identity;
            if (_cache.TryGetValue(placement.EntityLabel, out var cached)) return cached;

            Transform result;
            if (placement is IIfcLocalPlacement local)
            {
                var parent = local.PlacementRelTo != null ? Resolve(local.PlacementRelTo) : _root;
                result = parent.Multiply(FromAxisPlacement(local.RelativePlacement));
            }
            else
            {
                // IfcGridPlacement and IfcLinearPlacement carry no simple affine equivalent.
                result = _root;
            }

            _cache[placement.EntityLabel] = result;
            return result;
        }

        /// <summary>IfcAxis2Placement (2D or 3D SELECT) -> Revit Transform, lengths already in feet.</summary>
        public Transform FromAxisPlacement(IIfcAxis2Placement placement)
        {
            switch (placement)
            {
                case IIfcAxis2Placement3D p3: return From3D(p3);
                case IIfcAxis2Placement2D p2: return From2D(p2);
                default: return Transform.Identity;
            }
        }

        public Transform From3D(IIfcAxis2Placement3D p)
        {
            if (p == null) return Transform.Identity;

            var o = IfcHelpers.Coords(IfcHelpers.LocationOf(p));
            var z = IfcHelpers.Ratios(p.Axis, new[] { 0.0, 0.0, 1.0 });
            var x = IfcHelpers.Ratios(p.RefDirection, new[] { 1.0, 0.0, 0.0 });

            return Build(
                new XYZ(o[0] * _scale.Length, o[1] * _scale.Length, o[2] * _scale.Length),
                new XYZ(x[0], x[1], x[2]),
                new XYZ(z[0], z[1], z[2]));
        }

        public Transform From2D(IIfcAxis2Placement2D p)
        {
            if (p == null) return Transform.Identity;

            var o = IfcHelpers.Coords(IfcHelpers.LocationOf(p));
            var x = IfcHelpers.Ratios(p.RefDirection, new[] { 1.0, 0.0, 0.0 });

            return Build(
                new XYZ(o[0] * _scale.Length, o[1] * _scale.Length, 0.0),
                new XYZ(x[0], x[1], 0.0),
                XYZ.BasisZ);
        }

        /// <summary>Gram-Schmidt the supplied axes into an orthonormal right-handed frame.</summary>
        static Transform Build(XYZ origin, XYZ xDir, XYZ zDir)
        {
            var z = Normalize(zDir, XYZ.BasisZ);
            var x = xDir - z.Multiply(xDir.DotProduct(z));
            x = Normalize(x, PickPerpendicular(z));
            var y = z.CrossProduct(x);

            var t = Transform.Identity;
            t.Origin = origin;
            t.BasisX = x;
            t.BasisY = y;
            t.BasisZ = z;
            return t;
        }

        static XYZ Normalize(XYZ v, XYZ fallback)
            => v == null || v.GetLength() < 1e-9 ? fallback : v.Normalize();

        static XYZ PickPerpendicular(XYZ z)
            => System.Math.Abs(z.DotProduct(XYZ.BasisZ)) > 0.9
                ? XYZ.BasisX
                : XYZ.BasisZ.CrossProduct(z).Normalize();
    }
}
