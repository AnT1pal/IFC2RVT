using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xbim.Common;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IfcProbe
{
    /// <summary>
    /// Standalone check of the assumptions the Revit builders depend on. Runs without Revit,
    /// against the real file, so a wrong guess about the data shows up here rather than as a
    /// silent zero-conversion inside Revit.
    /// </summary>
    internal static class Program
    {
        static StreamWriter _out;

        static void Line(string s = "")
        {
            _out.WriteLine(s);
            _out.Flush();
        }

        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("usage: IfcProbe <file.ifc> [report.txt]");
                return 2;
            }

            var reportPath = args.Length > 1 ? args[1] : "probe-report.txt";
            using (_out = new StreamWriter(reportPath, false, new UTF8Encoding(false)))
            {
                var sw = Stopwatch.StartNew();
                Line($"Файл: {args[0]}");
                Line($"Размер: {new FileInfo(args[0]).Length / 1024.0 / 1024.0:F1} МБ");
                Line();

                using (var model = IfcStore.Open(args[0]))
                {
                    Line($"Открыт за {sw.Elapsed.TotalSeconds:F1} с");
                    Units(model);
                    Coordinates(model);
                    Storeys(model);
                    Walls(model);
                    Slabs(model);
                    Openings(model);
                    FacetedBreps(model);
                    OpeningSizes(model);
                    WallAppearance(model);
                    Glazing(model);
                    Properties(model);
                    Line();
                    Line($"Всего: {sw.Elapsed.TotalSeconds:F1} с");
                }
            }

            Console.WriteLine("report written: " + Path.GetFullPath(reportPath));
            return 0;
        }

        static void Units(IModel model)
        {
            var f = model.ModelFactors;
            Line("=== ЕДИНИЦЫ ===");
            Line($"  LengthToMetres      : {f.LengthToMetresConversionFactor}");
            Line($"  AngleToRadians      : {f.AngleToRadiansConversionFactor}");
            Line($"  Precision           : {f.Precision}");
            Line($"  -> множитель в футы : {f.LengthToMetresConversionFactor / 0.3048:F8}");
            Line();
        }

        /// <summary>
        /// Where the model actually sits. Revit becomes unreliable past roughly 16 km from the
        /// internal origin - geometry degrades and views go strange - so a site placed in real
        /// city coordinates is a common reason an import looks empty or wireframe-only.
        /// </summary>
        static void Coordinates(IModel model)
        {
            Line("=== КООРДИНАТЫ ===");

            foreach (var site in model.Instances.OfType<IIfcSite>())
                Line($"  IfcSite     {site.Name,-22} origin={Origin(site.ObjectPlacement)}");

            foreach (var building in model.Instances.OfType<IIfcBuilding>())
                Line($"  IfcBuilding {building.Name,-22} origin={Origin(building.ObjectPlacement)}");

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var counted = 0;

            foreach (var product in model.Instances.OfType<IIfcProduct>())
            {
                var o = WorldOrigin(product.ObjectPlacement);
                if (o == null) continue;

                minX = Math.Min(minX, o[0]); maxX = Math.Max(maxX, o[0]);
                minY = Math.Min(minY, o[1]); maxY = Math.Max(maxY, o[1]);
                minZ = Math.Min(minZ, o[2]); maxZ = Math.Max(maxZ, o[2]);
                counted++;
            }

            if (counted == 0) { Line("  нет размещений"); Line(); return; }

            Line($"  Размещений учтено: {counted}");
            Line($"  X: {minX:F0} .. {maxX:F0}   (протяжённость {maxX - minX:F0})");
            Line($"  Y: {minY:F0} .. {maxY:F0}   (протяжённость {maxY - minY:F0})");
            Line($"  Z: {minZ:F0} .. {maxZ:F0}   (протяжённость {maxZ - minZ:F0})");

            var metres = model.ModelFactors.LengthToMetresConversionFactor;
            var worst = Math.Max(Math.Max(Math.Abs(minX), Math.Abs(maxX)),
                                 Math.Max(Math.Abs(minY), Math.Abs(maxY))) * metres;
            Line($"  Максимальный вынос от нуля: {worst:F1} м");
            Line(worst > 16000
                ? "  >>> ВНИМАНИЕ: дальше 16 км от нуля Revit ведёт себя некорректно"
                : "  ок: в пределах рабочей зоны Revit");
            Line();
        }

        static string Origin(IIfcObjectPlacement placement)
        {
            var o = WorldOrigin(placement);
            return o == null ? "-" : $"({o[0]:F0}, {o[1]:F0}, {o[2]:F0})";
        }

        /// <summary>Accumulated translation of the IfcLocalPlacement chain, ignoring rotation.</summary>
        static double[] WorldOrigin(IIfcObjectPlacement placement, int depth = 0)
        {
            if (depth > 20) return null;

            if (!(placement is IIfcLocalPlacement local)) return null;

            var result = new[] { 0.0, 0.0, 0.0 };
            if (local.PlacementRelTo != null)
            {
                var parent = WorldOrigin(local.PlacementRelTo, depth + 1);
                if (parent != null) result = parent;
            }

            if (local.RelativePlacement is IIfcPlacement p && p.Location is IIfcCartesianPoint cp)
            {
                var c = cp.Coordinates;
                for (int i = 0; i < 3 && i < c.Count; i++) result[i] += c[i];
            }
            return result;
        }

        static void Storeys(IModel model)
        {
            Line("=== ЭТАЖИ ===");
            foreach (var s in model.Instances.OfType<IIfcBuildingStorey>())
            {
                var z = s.ObjectPlacement is IIfcLocalPlacement lp &&
                        lp.RelativePlacement is IIfcAxis2Placement3D p &&
                        p.Location is IIfcCartesianPoint cp && cp.Coordinates.Count > 2
                    ? (double)cp.Coordinates[2]
                    : double.NaN;

                Line($"  {s.Name,-24} Elevation={s.Elevation?.ToString() ?? "-",-12} PlacementZ={z}");
            }
            Line();
        }

        static void Walls(IModel model)
        {
            var walls = model.Instances.OfType<IIfcWall>().ToList();
            Line($"=== СТЕНЫ ({walls.Count}) ===");

            int withAxis = 0, withUsage = 0, withLayerSet = 0, withExtrusion = 0;

            foreach (var w in walls)
            {
                var shape = w.Representation as IIfcProductDefinitionShape;
                var reps = shape?.Representations.OfType<IIfcShapeRepresentation>().ToList()
                           ?? new List<IIfcShapeRepresentation>();

                var axis = reps.FirstOrDefault(r => (string)r.RepresentationIdentifier == "Axis");
                var body = reps.FirstOrDefault(r => (string)r.RepresentationIdentifier == "Body");

                var usage = w.HasAssociations.OfType<IIfcRelAssociatesMaterial>()
                             .Select(r => r.RelatingMaterial).OfType<IIfcMaterialLayerSetUsage>().FirstOrDefault();
                var layerSet = usage?.ForLayerSet
                    ?? w.HasAssociations.OfType<IIfcRelAssociatesMaterial>()
                        .Select(r => r.RelatingMaterial).OfType<IIfcMaterialLayerSet>().FirstOrDefault();

                var extrusion = body?.Items.Select(BaseExtrusion).FirstOrDefault(e => e != null);

                if (axis != null) withAxis++;
                if (usage != null) withUsage++;
                if (layerSet != null) withLayerSet++;
                if (extrusion != null) withExtrusion++;

                if (walls.IndexOf(w) < 6)
                {
                    var thickness = layerSet?.MaterialLayers.Sum(l => (double)l.LayerThickness) ?? 0;
                    Line($"  #{w.EntityLabel}");
                    Line($"     Name       : {w.Name}");
                    Line($"     Axis       : {(axis == null ? "НЕТ" : (string)axis.RepresentationType)}");
                    Line($"     Body       : {(body == null ? "НЕТ" : (string)body.RepresentationType)}");
                    Line($"     LayerSet   : {(layerSet == null ? "НЕТ" : $"{layerSet.MaterialLayers.Count} слоёв, {thickness} мм")}");
                    Line($"     Usage      : {(usage == null ? "НЕТ" : $"offset={usage.OffsetFromReferenceLine}, sense={usage.DirectionSense}")}");
                    Line($"     Extrusion  : {(extrusion == null ? "НЕТ" : $"{extrusion.SweptArea.GetType().Name}, depth={extrusion.Depth}")}");
                }
            }

            Line($"  ИТОГО: Axis={withAxis}/{walls.Count}  Usage={withUsage}  LayerSet={withLayerSet}  Extrusion={withExtrusion}");
            Line();
        }

        /// <summary>
        /// Walls exported from Revit carry a Clipping body: the extrusion is wrapped in one or
        /// more IfcBooleanClippingResult. Reporting "no extrusion" for those is misleading, so
        /// unwrap to the base operand the way the converter does.
        /// </summary>
        static IIfcExtrudedAreaSolid BaseExtrusion(IIfcRepresentationItem item, int depth = 0)
        {
            if (depth > 8) return null;
            switch (item)
            {
                case IIfcExtrudedAreaSolid solid: return solid;
                case IIfcBooleanResult boolean:
                    return BaseExtrusion(boolean.FirstOperand as IIfcRepresentationItem, depth + 1);
                default: return null;
            }
        }

        static void Slabs(IModel model)
        {
            var slabs = model.Instances.OfType<IIfcSlab>().ToList();
            Line($"=== ПЛИТЫ ({slabs.Count}) ===");

            foreach (var s in slabs.Take(8))
            {
                var body = (s.Representation as IIfcProductDefinitionShape)?.Representations
                    .OfType<IIfcShapeRepresentation>()
                    .FirstOrDefault(r => (string)r.RepresentationIdentifier == "Body");

                var item = body?.Items.FirstOrDefault();
                var extrusion = item == null ? null : BaseExtrusion(item);
                var dir = extrusion?.ExtrudedDirection?.DirectionRatios;

                Line($"  #{s.EntityLabel} {s.Name}");
                Line($"     PredefinedType : {s.PredefinedType}");
                Line($"     BodyType       : {(string)(body?.RepresentationType) ?? "-"} / item={item?.GetType().Name}");
                if (extrusion != null)
                    Line($"     Extrusion      : {extrusion.SweptArea.GetType().Name}, depth={extrusion.Depth}, dir=({dir?[0]},{dir?[1]},{dir?[2]})");
            }
            Line();
        }

        static void Openings(IModel model)
        {
            var doors = model.Instances.OfType<IIfcDoor>().ToList();
            var windows = model.Instances.OfType<IIfcWindow>().ToList();
            Line($"=== ПРОЁМЫ (дверей {doors.Count}, окон {windows.Count}) ===");

            int hosted = 0;
            foreach (var d in doors.Cast<IIfcElement>().Concat(windows))
            {
                var fills = d.FillsVoids.FirstOrDefault();
                var opening = fills?.RelatingOpeningElement;
                var host = opening?.VoidsElements?.RelatingBuildingElement;
                if (host != null) hosted++;

                if (doors.Count + windows.Count <= 30)
                    Line($"  {d.GetType().Name} #{d.EntityLabel} -> opening={(opening == null ? "НЕТ" : "#" + opening.EntityLabel)}, host={(host == null ? "НЕТ" : host.GetType().Name + " #" + host.EntityLabel)}");
            }
            Line($"  С найденным хостом: {hosted}/{doors.Count + windows.Count}");
            Line();
        }

        /// <summary>
        /// Why the DirectShape fallback refuses some faceted B-reps. Reports the shape of the
        /// data rather than assuming: face counts, bound kinds, loop kinds and point counts.
        /// </summary>
        static void FacetedBreps(IModel model)
        {
            Line("=== IFCFACETEDBREP: почему падает тесселяция ===");

            var failures = 0;
            var inspected = 0;
            var boundKinds = new Dictionary<string, int>();
            var loopKinds = new Dictionary<string, int>();
            var pointHistogram = new Dictionary<int, int>();

            foreach (var member in model.Instances.OfType<IIfcMember>())
            {
                var body = (member.Representation as IIfcProductDefinitionShape)?.Representations
                    .OfType<IIfcShapeRepresentation>()
                    .FirstOrDefault(r => (string)r.RepresentationIdentifier == "Body");

                foreach (var brep in (body?.Items ?? Enumerable.Empty<IIfcRepresentationItem>()).OfType<IIfcFacetedBrep>())
                {
                    inspected++;
                    var faces = brep.Outer?.CfsFaces?.ToList();

                    if (faces == null || faces.Count == 0)
                    {
                        failures++;
                        if (failures <= 3) Line($"  #{member.EntityLabel}: Outer пуст или отсутствует");
                        continue;
                    }

                    var usable = 0;
                    foreach (var face in faces)
                    {
                        foreach (var bound in face.Bounds)
                        {
                            Bump(boundKinds, bound.GetType().Name);
                            Bump(loopKinds, bound.Bound?.GetType().Name ?? "(null)");

                            var count = bound.Bound is IIfcPolyLoop poly ? poly.Polygon.Count
                                      : bound.Bound is IIfcEdgeLoop edge ? edge.EdgeList.Count
                                      : 0;
                            Bump(pointHistogram, count);
                            if (count >= 3) usable++;
                        }
                    }

                    if (usable == 0)
                    {
                        failures++;
                        if (failures <= 3)
                            Line($"  #{member.EntityLabel}: {faces.Count} граней, ни одного контура с 3+ точками");
                    }
                    else if (inspected <= 3)
                    {
                        Line($"  #{member.EntityLabel}: {faces.Count} граней, {usable} пригодных контуров — должно строиться");
                    }
                }
            }

            Line($"  Проверено IfcFacetedBrep у IfcMember: {inspected}, из них заведомо пустых: {failures}");
            Line("  Типы контуров : " + Join(boundKinds));
            Line("  Типы петель   : " + Join(loopKinds));
            Line("  Точек в петле : " + Join(pointHistogram));
            Line();
        }

        static void Bump<T>(Dictionary<T, int> map, T key)
        {
            map.TryGetValue(key, out var n);
            map[key] = n + 1;
        }

        static string Join<T>(Dictionary<T, int> map)
            => map.Count == 0 ? "-" : string.Join(", ", map.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key}={k.Value}"));

        /// <summary>
        /// Door and window sizes. The converter currently places a family at its own type size, so
        /// any gap between these numbers and the loaded family shows up as a mismatched opening.
        /// </summary>
        static void OpeningSizes(IModel model)
        {
            Line("=== ГАБАРИТЫ ДВЕРЕЙ И ОКОН ===");

            foreach (var door in model.Instances.OfType<IIfcDoor>())
                Line($"  IfcDoor   #{door.EntityLabel,-8} W={Fmt(door.OverallWidth)} H={Fmt(door.OverallHeight)}  {Trim(door.Name)}");

            foreach (var window in model.Instances.OfType<IIfcWindow>())
                Line($"  IfcWindow #{window.EntityLabel,-8} W={Fmt(window.OverallWidth)} H={Fmt(window.OverallHeight)}  {Trim(window.Name)}");

            Line();
        }

        static string Fmt(Xbim.Ifc4.MeasureResource.IfcPositiveLengthMeasure? v)
            => v.HasValue ? ((double)v.Value).ToString("F0").PadLeft(6) : "     -";

        static string Trim(Xbim.Ifc4.MeasureResource.IfcLabel? v)
        {
            var s = v.HasValue ? (string)v.Value : null;
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length > 54 ? s.Substring(0, 54) : s;
        }

        /// <summary>
        /// Where a wall actually gets its colour from, and how its body is clipped. Both drive
        /// visible defects: the wrong colour source paints the insulation instead of the facing,
        /// and the clip decides whether the wall meets the roof or runs through it.
        /// </summary>
        static void WallAppearance(IModel model)
        {
            Line("=== СТЕНЫ: ЦВЕТ И ПОДРЕЗКА ===");

            foreach (var wall in model.Instances.OfType<IIfcWall>().Take(4))
            {
                Line($"  #{wall.EntityLabel} {Trim(wall.Name)}");

                var layerSet = wall.HasAssociations.OfType<IIfcRelAssociatesMaterial>()
                    .Select(r => r.RelatingMaterial)
                    .Select(m => m is IIfcMaterialLayerSetUsage u ? u.ForLayerSet : m as IIfcMaterialLayerSet)
                    .FirstOrDefault(l => l != null);

                if (layerSet != null)
                {
                    foreach (var layer in layerSet.MaterialLayers)
                        Line($"     слой {layer.LayerThickness} мм  материал: {layer.Material?.Name}  цвет: {MaterialColour(model, layer.Material)}");
                }

                var body = (wall.Representation as IIfcProductDefinitionShape)?.Representations
                    .OfType<IIfcShapeRepresentation>()
                    .FirstOrDefault(r => (string)r.RepresentationIdentifier == "Body");

                foreach (var item in body?.Items ?? Enumerable.Empty<IIfcRepresentationItem>())
                {
                    Line($"     тело: {item.GetType().Name}  стиль на нём: {ItemColour(model, item)}");
                    ReportClip(model, item, 0);
                }
            }
            Line();
        }

        static void ReportClip(IModel model, IIfcRepresentationItem item, int depth)
        {
            if (depth > 4) return;
            var pad = new string(' ', 8 + depth * 2);

            switch (item)
            {
                case IIfcBooleanResult boolean:
                    Line($"{pad}{boolean.Operator} : отсечение");
                    if (boolean.SecondOperand is IIfcHalfSpaceSolid half)
                    {
                        var plane = half.BaseSurface as IIfcPlane;
                        var z = plane?.Position?.Location is IIfcCartesianPoint p && p.Coordinates.Count > 2
                            ? (double)p.Coordinates[2] : double.NaN;
                        var axis = plane?.Position?.Axis?.DirectionRatios;
                        Line($"{pad}  полупространство: плоскость Z={z:F0}, нормаль=({axis?[0]:F3},{axis?[1]:F3},{axis?[2]:F3}), agreement={half.AgreementFlag}");
                    }
                    ReportClip(model, boolean.FirstOperand as IIfcRepresentationItem, depth + 1);
                    return;

                case IIfcExtrudedAreaSolid solid:
                    Line($"{pad}выдавливание: depth={solid.Depth}, профиль={solid.SweptArea.GetType().Name}");
                    return;
            }
        }

        static string MaterialColour(IModel model, IIfcMaterial material)
        {
            if (material == null) return "-";
            foreach (var d in model.Instances.OfType<IIfcMaterialDefinitionRepresentation>())
            {
                if (d.RepresentedMaterial?.EntityLabel != material.EntityLabel) continue;
                var c = d.Representations.SelectMany(r => r.Items.OfType<IIfcStyledItem>())
                                         .Select(ColourOfStyled).FirstOrDefault(x => x != null);
                if (c != null) return c;
            }
            return "нет";
        }

        static string ItemColour(IModel model, IIfcRepresentationItem item)
        {
            foreach (var s in model.Instances.OfType<IIfcStyledItem>())
                if (s.Item?.EntityLabel == item.EntityLabel)
                {
                    var c = ColourOfStyled(s);
                    if (c != null) return c;
                }
            return "нет";
        }

        static string ColourOfStyled(IIfcStyledItem styled)
        {
            foreach (var style in styled.Styles)
            {
                var surface = style as IIfcSurfaceStyle;
                if (surface == null) continue;
                foreach (var e in surface.Styles)
                {
                    if (!(e is IIfcSurfaceStyleShading sh) || sh.SurfaceColour == null) continue;
                    var c = sh.SurfaceColour;
                    return $"RGB({(int)Math.Round((double)c.Red * 255)},{(int)Math.Round((double)c.Green * 255)},{(int)Math.Round((double)c.Blue * 255)}) [{surface.Name}]";
                }
            }
            return null;
        }

        /// <summary>
        /// What the glazing is actually made of, and whether anything in the file says it is
        /// see-through. A viewer showing the interior through a facade and Revit showing a solid
        /// panel is a transparency question, not a geometry one.
        /// </summary>
        static void Glazing(IModel model)
        {
            Line("=== ОСТЕКЛЕНИЕ: ИЗ ЧЕГО И ПРОЗРАЧНО ЛИ ===");

            var transparent = 0;
            var opaque = 0;
            var seen = new Dictionary<string, int>();

            foreach (var style in model.Instances.OfType<IIfcSurfaceStyle>())
            {
                foreach (var e in style.Styles)
                {
                    if (!(e is IIfcSurfaceStyleRendering r)) continue;
                    var t = r.Transparency.HasValue ? (double)r.Transparency.Value : 0.0;
                    var c = r.SurfaceColour;
                    var key = $"{style.Name} | RGB({(int)Math.Round((double)c.Red*255)},{(int)Math.Round((double)c.Green*255)},{(int)Math.Round((double)c.Blue*255)}) | прозр={t:F2}";
                    Bump(seen, key);
                    if (t > 0.01) transparent++; else opaque++;
                }
            }

            Line($"  Стилей поверхностей: {seen.Count}, с прозрачностью: {transparent}, непрозрачных: {opaque}");
            foreach (var kv in seen.OrderByDescending(k => k.Value).Take(30))
                Line($"    {kv.Key}");

            Line();
            Line("  Кто несёт остекление:");
            foreach (var group in new (string, IEnumerable<IIfcElement>)[]
                     {
                         ("IfcPlate",  model.Instances.OfType<IIfcPlate>().Cast<IIfcElement>()),
                         ("IfcMember", model.Instances.OfType<IIfcMember>().Cast<IIfcElement>()),
                         ("IfcWindow", model.Instances.OfType<IIfcWindow>().Cast<IIfcElement>()),
                     })
            {
                var items = group.Item2.Take(400).ToList();
                var styled = 0;
                var names = new Dictionary<string, int>();

                foreach (var e in items)
                {
                    var body = (e.Representation as IIfcProductDefinitionShape)?.Representations
                        .OfType<IIfcShapeRepresentation>()
                        .FirstOrDefault(r => (string)r.RepresentationIdentifier == "Body");

                    foreach (var item in body?.Items ?? Enumerable.Empty<IIfcRepresentationItem>())
                    {
                        var c = ItemColour(model, item);
                        if (c != "нет") { styled++; Bump(names, c); }
                    }
                }

                Line($"    {group.Item1,-10} проверено {items.Count}, со стилем на геометрии: {styled}");
                foreach (var kv in names.OrderByDescending(k => k.Value).Take(4))
                    Line($"       {kv.Value,4}x {kv.Key}");
            }
            Line();
        }

        static void Properties(IModel model)
        {
            Line("=== СВОЙСТВА ===");

            var byEntity = new Dictionary<string, int[]>();
            foreach (var p in model.Instances.OfType<IIfcProduct>())
            {
                var key = p.ExpressType.ExpressName;
                if (!byEntity.TryGetValue(key, out var counts)) byEntity[key] = counts = new int[3];

                counts[0]++;

                if (p is IIfcObject o)
                {
                    var instanceSets = o.IsDefinedBy
                        .Select(r => r.RelatingPropertyDefinition)
                        .OfType<IIfcPropertySet>().Count();

                    var typeSets = o.IsTypedBy
                        .Select(r => r.RelatingType)
                        .Where(t => t != null)
                        .SelectMany(t => t.HasPropertySets.OfType<IIfcPropertySet>())
                        .Count();

                    if (instanceSets > 0) counts[1]++;
                    if (typeSets > 0) counts[2]++;
                }
            }

            Line($"  {"Сущность",-30}{"Всего",8}{"СвойстваЭкз",14}{"СвойстваТипа",14}");
            foreach (var kv in byEntity.OrderByDescending(k => k.Value[0]).Take(25))
                Line($"  {kv.Key,-30}{kv.Value[0],8}{kv.Value[1],14}{kv.Value[2],14}");
            Line();
        }
    }
}
