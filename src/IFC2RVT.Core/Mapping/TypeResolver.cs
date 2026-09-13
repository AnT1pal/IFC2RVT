using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Mapping
{
    /// <summary>
    /// Finds, or manufactures, the Revit type each converted element needs.
    ///
    /// Three strategies, in order of trustworthiness:
    ///   1. Name match against the "Family:Type:Id" string Revit stamps into IfcRoot.Name. Exact
    ///      and free when the IFC came from Revit.
    ///   2. Geometric match on thickness for walls and slabs.
    ///   3. Duplicate the closest template and correct its compound structure.
    ///
    /// Everything is cached: type lookup is per-element work on a model with thousands of them.
    /// </summary>
    public class TypeResolver
    {
        readonly Document _doc;
        readonly MaterialResolver _materials;
        readonly IfcScale _scale;
        readonly bool _useNameHeuristic;
        readonly bool _createMissing;
        readonly double _thicknessToleranceFeet;

        readonly Dictionary<string, Resolved<WallType>> _wallCache = new Dictionary<string, Resolved<WallType>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Resolved<FloorType>> _floorCache = new Dictionary<string, Resolved<FloorType>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Resolved<FamilySymbol>> _symbolCache = new Dictionary<string, Resolved<FamilySymbol>>(StringComparer.OrdinalIgnoreCase);

        List<WallType> _wallTypes;
        List<FloorType> _floorTypes;

        public int TypesCreated { get; private set; }

        /// <summary>
        /// How a type was arrived at. This matters more than it looks: a substituted type produces
        /// a native element of the wrong thickness, which is silent and far harder to notice than
        /// an outright failure. Builders surface <see cref="IsSubstitute"/> in the report.
        /// </summary>
        public class Resolved<T> where T : ElementType
        {
            public T Type { get; set; }
            public string How { get; set; }
            public bool IsSubstitute { get; set; }

            /// <summary>Why creating the correct type failed, when it did.</summary>
            public string Reason { get; set; }

            public string Note
            {
                get
                {
                    if (!IsSubstitute || Type == null) return null;

                    var note = $"тип не найден, подставлен \"{Type.Name}\" - проверьте толщину";
                    return string.IsNullOrWhiteSpace(Reason) ? note : note + " (" + Reason + ")";
                }
            }
        }

        public TypeResolver(Document doc, MaterialResolver materials, IfcScale scale,
                            bool useNameHeuristic, bool createMissing)
        {
            _doc = doc;
            _materials = materials;
            _scale = scale;
            _useNameHeuristic = useNameHeuristic;
            _createMissing = createMissing;
            _thicknessToleranceFeet = scale.MmToFeet(1.0);
        }

        // ---- walls --------------------------------------------------------------------------

        List<WallType> WallTypes => _wallTypes ?? (_wallTypes =
            new FilteredElementCollector(_doc).OfClass(typeof(WallType))
                .Cast<WallType>().Where(w => w.Kind == WallKind.Basic).ToList());

        /// <summary>Must run inside a transaction: may duplicate a type.</summary>
        public Resolved<WallType> ResolveWallType(RevitTypeName name, double thicknessFeet, ElementId materialId)
        {
            var key = (name?.TypeName ?? "?") + "|" + thicknessFeet.ToString("F5");
            if (_wallCache.TryGetValue(key, out var cached)) return cached;

            LastCreateFailure = null;

            var result = FindWallType(name, thicknessFeet)
                      ?? CreateWallType(name, thicknessFeet, materialId)
                      ?? new Resolved<WallType>
                         {
                             Type = WallTypes.FirstOrDefault(),
                             How = "подстановка",
                             IsSubstitute = true,
                             Reason = LastCreateFailure
                         };

            _wallCache[key] = result;
            return result;
        }

        Resolved<WallType> FindWallType(RevitTypeName name, double thicknessFeet)
        {
            if (_useNameHeuristic && name != null)
            {
                foreach (var candidate in name.MatchCandidates())
                {
                    var byName = WallTypes.FirstOrDefault(w => string.Equals(w.Name, candidate, StringComparison.OrdinalIgnoreCase));
                    if (byName != null)
                        return new Resolved<WallType> { Type = byName, How = "по имени" };
                }
            }

            if (thicknessFeet > _thicknessToleranceFeet)
            {
                var byWidth = WallTypes
                    .Where(w => Math.Abs(w.Width - thicknessFeet) <= _thicknessToleranceFeet)
                    .OrderBy(w => Math.Abs(w.Width - thicknessFeet))
                    .FirstOrDefault();
                if (byWidth != null)
                    return new Resolved<WallType> { Type = byWidth, How = "по толщине" };
            }

            return null;
        }

        Resolved<WallType> CreateWallType(RevitTypeName name, double thicknessFeet, ElementId materialId)
        {
            if (!_createMissing || thicknessFeet <= _thicknessToleranceFeet) return null;

            var template = WallTypes.FirstOrDefault(w => w.GetCompoundStructure() != null)
                        ?? WallTypes.FirstOrDefault();
            if (template == null) return null;

            try
            {
                var typeName = UniqueTypeName(name, thicknessFeet, "IFC Стена");
                var created = (WallType)template.Duplicate(typeName);

                if (!TryApplyThickness(created, thicknessFeet, materialId, out var reason))
                {
                    // Leave no half-made type behind for the user to wonder about.
                    LastCreateFailure = reason;
                    _doc.Delete(created.Id);
                    return null;
                }

                _wallTypes.Add(created);
                TypesCreated++;
                return new Resolved<WallType> { Type = created, How = "создан" };
            }
            catch (Exception ex)
            {
                // Swallowing this silently is what hid a wrong-thickness wall last time.
                LastCreateFailure = ex.Message;
                return null;
            }
        }

        /// <summary>Reason the last type duplication failed, surfaced by the builders.</summary>
        public string LastCreateFailure { get; private set; }

        // ---- floors and slabs ---------------------------------------------------------------

        List<FloorType> FloorTypes => _floorTypes ?? (_floorTypes =
            new FilteredElementCollector(_doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList());

        /// <summary>Must run inside a transaction: may duplicate a type.</summary>
        public Resolved<FloorType> ResolveFloorType(RevitTypeName name, double thicknessFeet, ElementId materialId)
        {
            var key = (name?.TypeName ?? "?") + "|" + thicknessFeet.ToString("F5");
            if (_floorCache.TryGetValue(key, out var cached)) return cached;

            LastCreateFailure = null;

            var result = FindFloorType(name, thicknessFeet)
                      ?? CreateFloorType(name, thicknessFeet, materialId)
                      ?? new Resolved<FloorType>
                         {
                             Type = FloorTypes.FirstOrDefault(),
                             How = "подстановка",
                             IsSubstitute = true,
                             Reason = LastCreateFailure
                         };

            _floorCache[key] = result;
            return result;
        }

        Resolved<FloorType> FindFloorType(RevitTypeName name, double thicknessFeet)
        {
            if (_useNameHeuristic && name != null)
            {
                foreach (var candidate in name.MatchCandidates())
                {
                    var byName = FloorTypes.FirstOrDefault(f => string.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase));
                    if (byName != null)
                        return new Resolved<FloorType> { Type = byName, How = "по имени" };
                }
            }

            if (thicknessFeet > _thicknessToleranceFeet)
            {
                var byThickness = FloorTypes
                    .Select(f => new { Type = f, Width = WidthOf(f) })
                    .Where(x => x.Width > 0 && Math.Abs(x.Width - thicknessFeet) <= _thicknessToleranceFeet)
                    .OrderBy(x => Math.Abs(x.Width - thicknessFeet))
                    .FirstOrDefault();
                if (byThickness != null)
                    return new Resolved<FloorType> { Type = byThickness.Type, How = "по толщине" };
            }

            return null;
        }

        Resolved<FloorType> CreateFloorType(RevitTypeName name, double thicknessFeet, ElementId materialId)
        {
            if (!_createMissing || thicknessFeet <= _thicknessToleranceFeet) return null;

            // Foundation slabs carry constraints that make them a poor donor; prefer a plain floor.
            var template = FloorTypes.FirstOrDefault(f => f.GetCompoundStructure() != null && !IsFoundation(f))
                        ?? FloorTypes.FirstOrDefault(f => f.GetCompoundStructure() != null)
                        ?? FloorTypes.FirstOrDefault();
            if (template == null) return null;

            try
            {
                var typeName = UniqueTypeName(name, thicknessFeet, "IFC Перекрытие");
                var created = (FloorType)template.Duplicate(typeName);

                if (!TryApplyThickness(created, thicknessFeet, materialId, out var reason))
                {
                    LastCreateFailure = reason;
                    _doc.Delete(created.Id);
                    return null;
                }

                _floorTypes.Add(created);
                TypesCreated++;
                return new Resolved<FloorType> { Type = created, How = "создан" };
            }
            catch (Exception ex)
            {
                LastCreateFailure = ex.Message;
                return null;
            }
        }

        static bool IsFoundation(FloorType type)
        {
            try { return type.IsFoundationSlab; }
            catch { return false; }
        }

        static double WidthOf(FloorType type)
        {
            try
            {
                var cs = type.GetCompoundStructure();
                return cs?.GetWidth() ?? 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Sets a duplicated wall or floor type to the required thickness.
        ///
        /// Two strategies, and the order matters. Resizing the core layer of the template keeps the
        /// finishes, membranes and layer functions the template already had, which is what a user
        /// expects from a type named after their own standard. Replacing the whole structure with a
        /// single layer works more often but throws that away, so it is the fallback.
        ///
        /// Building a fresh structure unconditionally is what silently produced 150 mm slabs where
        /// the IFC said 600 mm: the call failed, its exception was swallowed, and a substitute type
        /// was used instead without anything in the report saying so.
        /// </summary>
        static bool TryApplyThickness(HostObjAttributes type, double targetFeet,
                                      ElementId materialId, out string reason)
        {
            reason = null;

            try
            {
                var structure = type.GetCompoundStructure();
                if (structure != null && structure.LayerCount > 0)
                {
                    var layers = structure.GetLayers();
                    var core = structure.GetFirstCoreLayerIndex();

                    if (core >= 0 && core < layers.Count && structure.CanLayerWidthBeNonZero(core))
                    {
                        var others = 0.0;
                        for (int i = 0; i < layers.Count; i++)
                            if (i != core) others += layers[i].Width;

                        var coreWidth = targetFeet - others;
                        if (coreWidth >= CompoundStructure.GetMinimumLayerThickness())
                        {
                            structure.SetLayerWidth(core, coreWidth);
                            if (materialId != ElementId.InvalidElementId &&
                                structure.CanLayerBeStructuralMaterial(core))
                                structure.SetMaterialId(core, materialId);

                            type.SetCompoundStructure(structure);
                            return true;
                        }

                        reason = "слои шаблона толще требуемого";
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "правка слоёв: " + ex.Message;
            }

            try
            {
                type.SetCompoundStructure(CompoundStructure.CreateSingleLayerCompoundStructure(
                    MaterialFunctionAssignment.Structure, targetFeet, materialId));
                return true;
            }
            catch (Exception ex)
            {
                reason = (reason == null ? string.Empty : reason + "; ") + "один слой: " + ex.Message;
                return false;
            }
        }

        // ---- loadable families ---------------------------------------------------------------

        /// <summary>
        /// Nearest family symbol in a category. Returns null when the project has no family at all
        /// for that category - loading one from disk needs a library path the add-in cannot guess,
        /// so the caller falls back to DirectShape instead of inventing something wrong.
        /// </summary>
        public FamilySymbol ResolveSymbol(BuiltInCategory category, RevitTypeName name)
        {
            var key = (int)category + "|" + (name?.TypeName ?? name?.Raw ?? "?");
            if (_symbolCache.TryGetValue(key, out var cached)) return cached.Type;

            var symbols = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(category)
                .Cast<FamilySymbol>()
                .ToList();

            FamilySymbol result = null;

            if (_useNameHeuristic && name != null && symbols.Count > 0)
            {
                foreach (var candidate in name.MatchCandidates())
                {
                    result = symbols.FirstOrDefault(s => string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase));
                    if (result != null) break;

                    result = symbols.FirstOrDefault(s =>
                        string.Equals(s.FamilyName, name.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.Name, name.TypeName, StringComparison.OrdinalIgnoreCase));
                    if (result != null) break;
                }
            }

            var substituted = false;
            if (result == null)
            {
                result = symbols.FirstOrDefault();
                substituted = result != null;
            }

            _symbolCache[key] = new Resolved<FamilySymbol>
            {
                Type = result,
                How = substituted ? "подстановка" : "по имени",
                IsSubstitute = substituted
            };
            return result;
        }

        /// <summary>Family symbols must be activated before the first instance is placed.</summary>
        public static void EnsureActive(FamilySymbol symbol)
        {
            if (symbol != null && !symbol.IsActive) symbol.Activate();
        }

        // ---- sized openings --------------------------------------------------------------------

        /// <summary>
        /// Door and window symbol matched to the size the IFC actually declares.
        ///
        /// Width and height are type parameters in Revit, not instance ones, so placing whatever
        /// symbol happens to be loaded gives an opening of the wrong size - the family cuts its own
        /// hole and the result visibly disagrees with the surrounding wall. Match on size, and
        /// duplicate a symbol to the required dimensions when nothing fits.
        /// </summary>
        public Resolved<FamilySymbol> ResolveSizedSymbol(BuiltInCategory category, RevitTypeName name,
                                                         double widthFeet, double heightFeet)
        {
            var key = "sized|" + (int)category + "|" + (name?.TypeName ?? "?") + "|" +
                      widthFeet.ToString("F4") + "|" + heightFeet.ToString("F4");
            if (_symbolCache.TryGetValue(key, out var cached)) return cached;

            LastCreateFailure = null;

            var symbols = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(category)
                .Cast<FamilySymbol>()
                .ToList();

            var result = MatchSymbol(symbols, name, widthFeet, heightFeet, category)
                      ?? CreateSizedSymbol(symbols, name, widthFeet, heightFeet, category)
                      ?? new Resolved<FamilySymbol>
                         {
                             Type = symbols.FirstOrDefault(),
                             How = "подстановка",
                             IsSubstitute = symbols.Count > 0,
                             Reason = LastCreateFailure
                         };

            _symbolCache[key] = result;
            return result;
        }

        Resolved<FamilySymbol> MatchSymbol(List<FamilySymbol> symbols, RevitTypeName name,
                                           double widthFeet, double heightFeet, BuiltInCategory category)
        {
            if (_useNameHeuristic && name != null)
            {
                foreach (var candidate in name.MatchCandidates())
                {
                    var byName = symbols.FirstOrDefault(s => string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase));
                    if (byName != null) return new Resolved<FamilySymbol> { Type = byName, How = "по имени" };
                }
            }

            if (widthFeet <= 0 || heightFeet <= 0) return null;

            var tolerance = _scale.MmToFeet(5.0);
            var bySize = symbols
                .Select(s => new { Symbol = s, Size = SizeOf(s, category) })
                .Where(x => x.Size != null &&
                            Math.Abs(x.Size[0] - widthFeet) <= tolerance &&
                            Math.Abs(x.Size[1] - heightFeet) <= tolerance)
                .OrderBy(x => Math.Abs(x.Size[0] - widthFeet) + Math.Abs(x.Size[1] - heightFeet))
                .FirstOrDefault();

            return bySize == null ? null : new Resolved<FamilySymbol> { Type = bySize.Symbol, How = "по габаритам" };
        }

        Resolved<FamilySymbol> CreateSizedSymbol(List<FamilySymbol> symbols, RevitTypeName name,
                                                 double widthFeet, double heightFeet, BuiltInCategory category)
        {
            if (!_createMissing || widthFeet <= 0 || heightFeet <= 0) return null;

            // Prefer a template whose size parameters are actually writable.
            var template = symbols.FirstOrDefault(s => SizeOf(s, category) != null) ?? symbols.FirstOrDefault();
            if (template == null) return null;

            try
            {
                var mmWidth = Math.Round(widthFeet / IfcScale.FeetPerMetre * 1000.0);
                var mmHeight = Math.Round(heightFeet / IfcScale.FeetPerMetre * 1000.0);
                var basis = !string.IsNullOrWhiteSpace(name?.TypeName)
                    ? name.TypeName
                    : $"IFC {mmWidth:F0}x{mmHeight:F0}";

                var created = (FamilySymbol)template.Duplicate(UniqueName(MaterialResolver.SanitiseName(basis)));

                if (!SetSize(created, category, widthFeet, heightFeet))
                {
                    LastCreateFailure = "габариты типоразмера недоступны для записи";
                    _doc.Delete(created.Id);
                    return null;
                }

                TypesCreated++;
                return new Resolved<FamilySymbol> { Type = created, How = "создан по габаритам" };
            }
            catch (Exception ex)
            {
                LastCreateFailure = ex.Message;
                return null;
            }
        }

        static double[] SizeOf(FamilySymbol symbol, BuiltInCategory category)
        {
            var width = SizeParameter(symbol, category, true);
            var height = SizeParameter(symbol, category, false);
            if (width == null || height == null) return null;

            var w = width.AsDouble();
            var h = height.AsDouble();
            return w > 0 && h > 0 ? new[] { w, h } : null;
        }

        static bool SetSize(FamilySymbol symbol, BuiltInCategory category, double widthFeet, double heightFeet)
        {
            var width = SizeParameter(symbol, category, true);
            var height = SizeParameter(symbol, category, false);
            if (width == null || height == null || width.IsReadOnly || height.IsReadOnly) return false;

            return width.Set(widthFeet) && height.Set(heightFeet);
        }

        /// <summary>
        /// Size parameters are built-in for stock door and window families, but plenty of local
        /// libraries expose their own, so fall back to the usual names in both languages.
        /// </summary>
        static Parameter SizeParameter(FamilySymbol symbol, BuiltInCategory category, bool width)
        {
            var builtIn = category == BuiltInCategory.OST_Doors
                ? (width ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.DOOR_HEIGHT)
                : (width ? BuiltInParameter.WINDOW_WIDTH : BuiltInParameter.WINDOW_HEIGHT);

            var parameter = symbol.get_Parameter(builtIn);
            if (parameter != null) return parameter;

            foreach (var candidate in width
                         ? new[] { "Ширина", "Width", "Ширина проема", "Ширина проёма" }
                         : new[] { "Высота", "Height", "Высота проема", "Высота проёма" })
            {
                parameter = symbol.LookupParameter(candidate);
                if (parameter != null) return parameter;
            }
            return null;
        }

        string UniqueName(string basis)
        {
            var used = new HashSet<string>(
                new FilteredElementCollector(_doc).OfClass(typeof(ElementType))
                    .Cast<ElementType>().Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            if (!used.Contains(basis)) return basis;

            for (int i = 2; i < 500; i++)
            {
                var candidate = $"{basis} ({i})";
                if (!used.Contains(candidate)) return candidate;
            }
            return basis + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        // ---- naming ---------------------------------------------------------------------------

        string UniqueTypeName(RevitTypeName name, double thicknessFeet, string prefix)
        {
            var mm = Math.Round(thicknessFeet / IfcScale.FeetPerMetre * 1000.0);
            var basis = !string.IsNullOrWhiteSpace(name?.TypeName)
                ? name.TypeName
                : $"{prefix} {mm:F0}";

            basis = MaterialResolver.SanitiseName(basis);

            var used = new HashSet<string>(
                new FilteredElementCollector(_doc).OfClass(typeof(ElementType))
                    .Cast<ElementType>().Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            if (!used.Contains(basis)) return basis;

            for (int i = 2; i < 500; i++)
            {
                var candidate = $"{basis} ({i})";
                if (!used.Contains(candidate)) return candidate;
            }
            return basis + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }
    }
}
