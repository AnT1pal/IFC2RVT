using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using IFC2RVT.Builders;
using IFC2RVT.Ifc;
using IFC2RVT.Mapping;
using IFC2RVT.Parameters;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Conversion
{
    /// <summary>
    /// Drives the whole conversion: open the IFC, create levels, run the native builders in
    /// dependency order, fall back to DirectShape, then copy properties.
    ///
    /// Work is committed in batches rather than one giant transaction. On a model of this size a
    /// single transaction means Revit holds every change in memory and the undo record alone can
    /// exhaust the process.
    /// </summary>
    public class ConversionEngine
    {
        readonly Document _doc;
        readonly ConversionOptions _options;
        readonly ConversionReport _report = new ConversionReport();

        public ConversionEngine(Document doc, ConversionOptions options)
        {
            _doc = doc;
            _options = options;
        }

        public ConversionReport Run(IProgress<string> progress = null)
        {
            var stopwatch = Stopwatch.StartNew();
            _report.SourceFile = _options.IfcFilePath;

            progress?.Report("Открытие IFC...");
            using (var ifc = new IfcModelContext(_options.IfcFilePath, _doc.Application.ShortCurveTolerance))
            {
                _report.SourceApplication = ifc.SourceApplication;
                _report.SourceSchema = ifc.SourceSchema;

                ApplyOriginOffset(ifc, progress);

                var materials = new MaterialResolver(_doc) { Styles = ifc.Styles };
                var levels = new LevelMapper(_doc, ifc.Scale, ifc.Placements, _options.LevelToleranceMm);
                var types = new TypeResolver(_doc, materials, ifc.Scale,
                                             _options.UseRevitNameHeuristic, _options.CreateMissingTypes);

                using (var parameters = new SharedParameterManager(_doc))
                {
                    var properties = new PropertyTransfer(parameters, ifc.Scale, _options.IncludeTypeProperties);
                    var ctx = new BuilderContext(_doc, ifc, levels, types, materials, _options);

                    progress?.Report("Сопоставление уровней...");
                    RunBatch("Уровни", () =>
                    {
                        levels.Build(ifc);
                        RecordSiteCoordinates();
                    });
                    _report.LevelsCreated = levels.LevelsCreated;

                    if (_options.ConvertGrids)
                    {
                        progress?.Report("Оси...");
                        var grids = new GridBuilder(ctx);
                        RunBatch("Оси", grids.BuildAll);
                        _report.GridsCreated = grids.Created;
                        _report.GridsReused = grids.Reused;
                    }

                    progress?.Report("Сбор элементов...");
                    var plan = BuildPlan(ifc);
                    progress?.Report($"К конвертации: {plan.Count} элементов");

                    Convert(ctx, plan, properties, progress);

                    _report.TypesCreated = types.TypesCreated;
                    _report.SharedParametersCreated = parameters.ParametersCreated;
                    _report.MaterialsCreated = materials.MaterialsCreated;
                    _report.MaterialsColoured = materials.MaterialsColoured;

                    foreach (var missing in ctx.MissingSections.Values)
                        _report.MissingSections.Add(Tuple.Create(
                            missing.Designation, missing.Kind, missing.Standard ?? "", missing.Count));
                }
            }

            stopwatch.Stop();
            _report.Duration = stopwatch.Elapsed;
            return _report;
        }

        /// <summary>
        /// Brings a model authored in site or survey coordinates back near the Revit origin.
        ///
        /// This is not cosmetic. The sample model sits 24 km out, and beyond roughly 16 km Revit
        /// no longer has the precision to shade faces: every element converts correctly and the
        /// 3D view still shows nothing but edges. Must run before any placement is resolved,
        /// because the resolver caches its results.
        /// </summary>
        void ApplyOriginOffset(IfcModelContext ifc, IProgress<string> progress)
        {
            if (!_options.MoveToOrigin) return;

            var origin = ifc.SpatialOrigin();
            var distanceFeet = Math.Sqrt(origin.X * origin.X + origin.Y * origin.Y);
            var thresholdFeet = _options.OriginThresholdMetres * IfcScale.FeetPerMetre;

            if (distanceFeet <= thresholdFeet) return;

            ifc.Placements.SetRootOffset(-origin);

            var metresPerFoot = 0.3048;
            _report.AppliedOffsetMetres = new[]
            {
                -origin.X * metresPerFoot,
                -origin.Y * metresPerFoot,
                -origin.Z * metresPerFoot
            };

            progress?.Report($"Модель вынесена на {distanceFeet * metresPerFoot / 1000.0:F1} км — переношу к нулю...");
        }

        /// <summary>
        /// Records the shift as the shared coordinate system, so the original site position
        /// survives the move to the origin instead of living only in the conversion report.
        ///
        /// This goes through ProjectPosition and deliberately not through the project base point
        /// parameters. Moving the base point element is itself capped at 16 km from the internal
        /// origin - the very limit the shift exists to get away from - so setting it to a site
        /// 28 km out pops a modal relocation prompt and fails. The shared coordinate system has no
        /// such cap; survey coordinates are routinely in the millions of metres.
        ///
        /// Must run inside a transaction.
        /// </summary>
        void RecordSiteCoordinates()
        {
            var offset = _report.AppliedOffsetMetres;
            if (offset == null) return;

            try
            {
                var location = _doc.ActiveProjectLocation;
                if (location == null) return;

                // Geometry moved by -origin, so Revit (0,0,0) now stands where the site origin was.
                var feetPerMetre = IfcScale.FeetPerMetre;
                var position = location.GetProjectPosition(XYZ.Zero);

                position.EastWest = -offset[0] * feetPerMetre;
                position.NorthSouth = -offset[1] * feetPerMetre;
                position.Elevation = -offset[2] * feetPerMetre;

                location.SetProjectPosition(XYZ.Zero, position);
            }
            catch
            {
                // Geolocation is a convenience; never let it cost the conversion.
            }
        }

        // ---- planning -------------------------------------------------------------------------

        /// <summary>
        /// Products to convert, ordered so that hosts exist before the things they host.
        /// Walls must precede doors and windows; everything else follows.
        /// </summary>
        List<IIfcProduct> BuildPlan(IfcModelContext ifc)
        {
            var all = ifc.All<IIfcElement>()
                .Where(e => !_options.SkipIfcTypes.Contains(IfcHelpers.EntityName(e)))
                .ToList();

            var elements = new List<IIfcElement>();
            foreach (var e in all)
            {
                if (Convertible(e, ifc)) { elements.Add(e); continue; }

                // Record the skip instead of letting the element vanish. An element absent from the
                // report is indistinguishable from one the converter never saw, and that ambiguity
                // is exactly what made seven phantom errors hard to read last time.
                if (e is IIfcOpeningElement || e is IIfcVirtualElement) continue;

                _report.Add(e.GlobalId.ToString(), IfcHelpers.EntityName(e), IfcHelpers.Str(e.Name),
                            ConversionOutcome.Skipped, null, 0,
                            "контейнер без собственной геометрии - составляющие обработаны отдельно");
            }

            var spaces = _options.ConvertSpaces
                ? ifc.All<IIfcSpace>().Cast<IIfcProduct>().ToList()
                : new List<IIfcProduct>();

            var plan = new List<IIfcProduct>();
            plan.AddRange(elements.Where(e => e is IIfcWall));
            plan.AddRange(elements.Where(e => e is IIfcSlab));
            plan.AddRange(elements.Where(e => e is IIfcCovering));
            plan.AddRange(elements.Where(e => e is IIfcColumn));
            plan.AddRange(elements.Where(e => e is IIfcBeam || e is IIfcMember));
            plan.AddRange(elements.Where(e => e is IIfcDoor || e is IIfcWindow));
            plan.AddRange(elements.Where(e => !(e is IIfcWall) && !(e is IIfcSlab) &&
                                              !(e is IIfcColumn) && !(e is IIfcBeam) && !(e is IIfcMember) &&
                                              !(e is IIfcDoor) && !(e is IIfcWindow) && !(e is IIfcCovering)));
            plan.AddRange(spaces);

            // Openings are cut last: the host has to exist before a hole can be made in it.
            if (_options.ConvertOpeningVoids)
                plan.AddRange(ifc.All<IIfcOpeningElement>()
                                 .Where(o => !o.HasFillings.Any())
                                 .Cast<IIfcProduct>());

            if (_options.MaxElements > 0 && plan.Count > _options.MaxElements)
                plan = plan.Take(_options.MaxElements).ToList();

            return plan;
        }

        static bool Convertible(IIfcElement e, IfcModelContext ifc)
        {
            // Openings are voids in other elements, not elements in their own right.
            if (e is IIfcOpeningElement || e is IIfcVirtualElement) return false;

            // Aggregation containers carry no geometry of their own: IfcStair holds IfcStairFlight,
            // IfcRoof holds IfcSlab, IfcElementAssembly holds its parts, and every one of those
            // children is enumerated separately. Converting the container as well would either
            // duplicate the geometry or, as happened on the first real run, report seven phantom
            // "no Body representation" errors for elements that were never meant to have one.
            if (e.IsDecomposedBy.Any() && ifc.Representations.BodyOf(e) == null) return false;

            return true;
        }

        // ---- conversion -------------------------------------------------------------------------

        void Convert(BuilderContext ctx, List<IIfcProduct> plan, PropertyTransfer properties,
                     IProgress<string> progress)
        {
            var builders = NativeBuilders(ctx);
            var fallback = new DirectShapeBuilder(ctx);

            var batchSize = Math.Max(1, _options.TransactionBatchSize);
            var total = plan.Count;

            for (int start = 0; start < total; start += batchSize)
            {
                var batch = plan.Skip(start).Take(batchSize).ToList();
                var done = Math.Min(start + batchSize, total);
                progress?.Report($"Конвертация {done} / {total}...");

                RunBatch($"Элементы {start + 1}-{done}", () =>
                {
                    foreach (var product in batch)
                        ConvertOne(ctx, product, builders, fallback, properties);
                });
            }
        }

        List<IElementBuilder> NativeBuilders(BuilderContext ctx)
        {
            var builders = new List<IElementBuilder>();
            if (_options.ConvertWalls) builders.Add(new WallBuilder(ctx));
            if (_options.ConvertSlabs) builders.Add(new FloorBuilder(ctx));
            if (_options.ConvertColumns) builders.Add(new ColumnBuilder(ctx));
            if (_options.ConvertBeams) builders.Add(new BeamBuilder(ctx));
            if (_options.ConvertOpenings) builders.Add(new OpeningBuilder(ctx));
            if (_options.ConvertSpaces) builders.Add(new RoomBuilder(ctx));
            if (_options.ConvertCeilings) builders.Add(new CeilingBuilder(ctx));
            if (_options.ConvertOpeningVoids) builders.Add(new VoidBuilder(ctx));
            return builders;
        }

        void ConvertOne(BuilderContext ctx, IIfcProduct product, List<IElementBuilder> builders,
                        DirectShapeBuilder fallback, PropertyTransfer properties)
        {
            var guid = product.GlobalId.ToString();
            var entity = IfcHelpers.EntityName(product);
            var name = IfcHelpers.Str(product.Name);

            string nativeFailure = null;

            var builder = builders.FirstOrDefault(b => b.CanBuild(product));
            if (builder != null)
            {
                BuildResult result;
                try { result = builder.Build(product); }
                catch (Exception ex) { result = BuildResult.Fail(ex.Message); }

                if (result.Succeeded)
                {
                    ctx.Register(product, result.Element);
                    ApplyProperties(properties, result.Element, product);

                    _report.Add(guid, entity, name, ConversionOutcome.Native,
                                builder.CategoryName, BuilderContext.IdValue(result.Element.Id), result.Message);
                    return;
                }

                nativeFailure = result.Message;
            }

            // An opening rendered as a solid is not a hole - it is a block inside the wall.
            // Never hand these to the fallback, whatever the user asked for.
            if (!_options.FallbackToDirectShape || product is IIfcOpeningElement)
            {
                _report.Add(guid, entity, name, ConversionOutcome.Skipped, null, 0,
                            nativeFailure ?? "нативный построитель отсутствует");
                return;
            }

            BuildResult shape;
            try { shape = fallback.Build(product); }
            catch (Exception ex) { shape = BuildResult.Fail(ex.Message); }

            if (shape.Succeeded)
            {
                ctx.Register(product, shape.Element);
                ApplyProperties(properties, shape.Element, product);

                _report.Add(guid, entity, name, ConversionOutcome.DirectShape, "DirectShape",
                            BuilderContext.IdValue(shape.Element.Id),
                            nativeFailure == null ? null : "нативно не вышло: " + nativeFailure);
                return;
            }

            _report.Add(guid, entity, name, ConversionOutcome.Failed, null, 0,
                        nativeFailure ?? shape.Message);
        }

        void ApplyProperties(PropertyTransfer properties, Element element, IIfcProduct product)
        {
            if (!_options.TransferProperties) return;
            try { properties.Apply(element, product); }
            catch { }
        }

        // ---- transactions -------------------------------------------------------------------------

        void RunBatch(string name, Action action)
        {
            using (var transaction = new Transaction(_doc, "IFC2RVT: " + name))
            {
                var swallower = new FailureSwallower();
                transaction.Start();
                swallower.AttachTo(transaction);

                try
                {
                    action();
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    if (transaction.HasStarted()) transaction.RollBack();
                    _report.Add(null, "(batch)", name, ConversionOutcome.Failed, null, 0,
                                "пакет отменён: " + ex.Message);
                }
                finally
                {
                    // Suppressing Revit failures silently is how an element gets joined or deleted
                    // without anything saying so; carry the counts into the report instead.
                    _report.WarningsSuppressed += swallower.WarningsSuppressed;
                    _report.ErrorsResolved += swallower.ErrorsEncountered;
                }
            }
        }
    }
}
