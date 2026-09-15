using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using IFC2RVT.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC2RVT.Mapping
{
    /// <summary>
    /// Builds one structural framing family per steel section, using the cross-section outline the
    /// IFC already carries.
    ///
    /// This exists because of what the numbers say about a real model. 3344 members carry only 45
    /// distinct designations, so the missing thing was never a family per element - it was a family
    /// per section, forty-odd of them. Without that the converter has nothing to place the members
    /// into and every one of them falls through to DirectShape; with it the same members become
    /// framing instances that schedule, tag and edit.
    ///
    /// The outline is harvested from the file and never invented. A section no element supplies an
    /// outline for is left alone and its members stay geometry - a profile reconstructed from the
    /// designation would be a guess about fillet radii dressed up as measurement, and the whole
    /// point of refusing a substituted profile was to stop exactly that.
    ///
    /// Every step is allowed to fail. A missing family template, a template that will not take the
    /// outline, an outline Revit rejects - each of those disables one section and leaves the old
    /// DirectShape behaviour untouched, so the worst case is the behaviour before this class
    /// existed.
    /// </summary>
    public class SectionFamilyFactory
    {
        /// <summary>Nominal length of the generated solid, before it is tied to the end planes.</summary>
        const double NominalLength = 10.0;

        readonly Document _doc;
        readonly Application _app;
        readonly IfcModelContext _ifc;

        readonly Dictionary<string, FamilySymbol> _symbols =
            new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);

        readonly HashSet<string> _rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Sections whose first placed member has not been measured yet.</summary>
        readonly HashSet<string> _unverified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string _template;
        bool _prepared;

        /// <summary>How many members each section has, for saying what a rejection cost.</summary>
        readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public int FamiliesCreated { get; private set; }
        public int SectionsWithoutOutline { get; private set; }

        /// <summary>Sections whose first member was measured and came out the length it was placed at.</summary>
        public int Verified { get; private set; }

        /// <summary>Human-readable account of what was made and what was not, for the report.</summary>
        public List<string> Notes { get; } = new List<string>();

        public SectionFamilyFactory(Document doc, IfcModelContext ifc)
        {
            _doc = doc;
            _app = doc.Application;
            _ifc = ifc;
        }

        // ---- preparation --------------------------------------------------------------------

        /// <summary>
        /// Groups the members by section, harvests one outline per section and creates a family for
        /// each. Runs once, before any member is placed.
        ///
        /// Opens its own transaction per section rather than running inside one. Drawing the section
        /// happens in a separate family document and needs no transaction on the project at all, and
        /// giving each section its own keeps one outline Revit dislikes from rolling back the forty
        /// that worked.
        /// </summary>
        public void Prepare(IEnumerable<IIfcElement> members, IProgress<string> progress)
        {
            if (_prepared) return;
            _prepared = true;

            var sections = Collect(members);
            if (sections.Count == 0) return;

            _template = FindTemplate();
            if (_template == null)
            {
                Notes.Add("Шаблон семейства несущих конструкций не найден - сечения не создавались. " +
                          "Библиотека шаблонов Revit не установлена?");
                return;
            }

            var index = 0;
            foreach (var section in sections.Values.OrderByDescending(s => s.Count))
            {
                index++;
                progress?.Report($"Сечения: {index} / {sections.Count} — {section.Raw}");

                if (section.Outline == null)
                {
                    SectionsWithoutOutline++;
                    continue;
                }

                var symbol = CreateFamily(section);
                if (symbol == null)
                {
                    _rejected.Add(section.Key);
                    continue;
                }

                _symbols[section.Key] = symbol;
                _unverified.Add(section.Key);
                _counts[section.Key] = section.Count;
                FamiliesCreated++;
            }

            if (SectionsWithoutOutline > 0)
                Notes.Add($"Сечений без контура в файле: {SectionsWithoutOutline} - " +
                          "тела записаны как brep, форма сечения из них не восстанавливается.");
        }

        /// <summary>One section the model asks for, with the best outline found for it.</summary>
        class Section
        {
            public string Key;                 // normalised designation
            public string Raw;                 // designation as the file spells it
            public SteelDesignation Parsed;
            public int Count;
            public int Attempts;               // members asked for an outline so far
            public List<CurveLoop> Outline;    // in the extrusion frame, feet
        }

        Dictionary<string, Section> Collect(IEnumerable<IIfcElement> members)
        {
            var sections = new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase);

            foreach (var member in members)
            {
                var raw = DesignationOf(member);
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var key = SteelDesignation.Normalise(raw);
                if (string.IsNullOrWhiteSpace(key)) continue;

                var parsed = SteelDesignation.Parse(raw);

                // Sheet, strip and checker plate are not framing; they are refused deliberately and
                // making a beam family for them would undo that.
                if (parsed.IsFlatProduct) continue;

                if (!sections.TryGetValue(key, out var section))
                {
                    section = new Section { Key = key, Raw = raw, Parsed = parsed };
                    sections[key] = section;
                }
                section.Count++;

                // One outline is all a section needs. Sections whose members are all meshes would
                // otherwise be asked hundreds of times over, and reading a body is not cheap: the
                // largest section here has 403 members and not one of them carries an outline.
                if (section.Outline == null && section.Attempts < 25)
                {
                    section.Attempts++;
                    section.Outline = OutlineOf(member);
                }
            }

            return sections;
        }

        /// <summary>The designation, from the same sources the beam builder trusts, in that order.</summary>
        public static string DesignationOf(IIfcElement element)
        {
            var declared = IfcHelpers.ProfileName(IfcHelpers.StructuralProfile(element));
            if (!string.IsNullOrWhiteSpace(declared)) return declared;

            var description = IfcHelpers.Str(element.Description);
            if (!string.IsNullOrWhiteSpace(description) &&
                !description.Equals("main piece", StringComparison.OrdinalIgnoreCase))
                return description;

            return null;
        }

        /// <summary>
        /// The cross-section outline of one member, taken from the solid it is swept from. The loops
        /// come back in the frame of the extrusion, which is the frame whose origin the member's
        /// axis runs through - so the outline keeps its true offset from that axis rather than being
        /// recentred on itself.
        /// </summary>
        List<CurveLoop> OutlineOf(IIfcElement element)
        {
            try
            {
                var extrusion = _ifc.Representations.LargestExtrusion(element);
                if (extrusion?.Profile == null) return null;

                var loops = _ifc.Profiles.Read(extrusion.Profile);
                if (loops == null || loops.Count == 0) return null;

                // A degenerate outline makes a family that loads and then draws nothing.
                var box = ProfileReader.BoundingBoxOf(loops[0]);
                var width = box.Item2.X - box.Item1.X;
                var height = box.Item2.Y - box.Item1.Y;
                if (width <= _doc.Application.ShortCurveTolerance ||
                    height <= _doc.Application.ShortCurveTolerance) return null;

                return loops;
            }
            catch
            {
                return null;
            }
        }

        // ---- template -----------------------------------------------------------------------

        /// <summary>
        /// Locates the beams and braces family template.
        ///
        /// Matched on content words, because the file name is whatever language Revit was installed
        /// in: "Metric Structural Framing - Beams and Braces.rft" in English and "Метрическая
        /// система, несущий каркас - Балки и раскосы.rft" in Russian. The wording is worth checking
        /// against a real installation rather than guessing - the Russian name says "несущий
        /// каркас", not the "несущие конструкции" that reads as the natural translation, and a
        /// search for the latter finds nothing on a Russian Revit.
        ///
        /// The installed templates sit in per-language folders side by side, so the search starts in
        /// the folder Revit itself points at and only widens to its neighbours if that yields
        /// nothing. Any language will do - the template is opened through the API, not read.
        /// </summary>
        string FindTemplate()
        {
            var roots = new List<string>();
            try
            {
                var configured = _app.FamilyTemplatePath;
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    roots.Add(configured);

                    // One level up holds the other languages.
                    var parent = System.IO.Path.GetDirectoryName(configured.TrimEnd('\\', '/'));
                    if (!string.IsNullOrWhiteSpace(parent)) roots.Add(parent);
                }
            }
            catch { }

            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var found = BestIn(root);
                if (found != null) return found;
            }
            return null;
        }

        static string BestIn(string root)
        {
            string[] files;
            try
            {
                if (!System.IO.Directory.Exists(root)) return null;
                files = System.IO.Directory.GetFiles(root, "*.rft", System.IO.SearchOption.AllDirectories);
            }
            catch { return null; }

            string best = null;
            var bestScore = 0;

            foreach (var file in files)
            {
                var score = TemplateScore(System.IO.Path.GetFileNameWithoutExtension(file));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = file;
                }
            }

            return bestScore >= 20 ? best : null;
        }

        static int TemplateScore(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            var n = name.ToLowerInvariant();

            var framing = n.Contains("structural framing") || n.Contains("несущий каркас") ||
                          n.Contains("несущего каркаса") || n.Contains("несущие конструкц") ||
                          n.Contains("несущих конструкц");

            // "Balken", "poutres", "vigas" and the rest are not enumerated: the English template is
            // installed alongside every localised one, so widening the search is enough.
            var beams = n.Contains("beams and braces") || n.Contains("балки и раскосы");

            if (!framing || !beams) return 0;

            var score = 20;
            if (n.Contains("metric") || n.Contains("метрич")) score += 3;

            // A template written for one material carries that material's parameters; the plain one
            // is the better host for an arbitrary rolled section.
            if (Regex.IsMatch(n, "concrete|steel|wood|бетон|сталь|дерев")) score -= 5;
            return score;
        }

        // ---- family creation ----------------------------------------------------------------

        FamilySymbol CreateFamily(Section section)
        {
            Document famDoc = null;
            try
            {
                famDoc = _app.NewFamilyDocument(_template);
                if (famDoc == null) return null;

                if (!DrawSection(famDoc, section)) return null;

                // Only bringing the family into the project changes the project.
                using (var t = new Transaction(_doc, "IFC2RVT: сечение " + section.Raw))
                {
                    var swallower = new Conversion.FailureSwallower();
                    t.Start();
                    swallower.AttachTo(t);

                    var family = Load(famDoc, section);
                    var symbol = family == null ? null : Activate(family, section);

                    if (symbol == null) { t.RollBack(); return null; }

                    t.Commit();
                    return symbol;
                }
            }
            catch (Exception ex)
            {
                Notes.Add($"Сечение \"{section.Raw}\": {ex.Message}");
                return null;
            }
            finally
            {
                try { famDoc?.Close(false); } catch { }
            }
        }

        /// <summary>
        /// Puts the outline into the family as a solid running along X, then ties its two ends to
        /// the template's end reference planes so the member takes the length it is placed at.
        /// </summary>
        bool DrawSection(Document famDoc, Section section)
        {
            using (var t = new Transaction(famDoc, "Сечение"))
            {
                t.Start();

                // The template runs its member along X, so the section lives in the YZ plane: the
                // outline's own u goes to Y and v to Z.
                var toFamily = Transform.Identity;
                toFamily.BasisX = XYZ.BasisY;
                toFamily.BasisY = XYZ.BasisZ;
                toFamily.BasisZ = XYZ.BasisX;

                var arrays = new CurveArrArray();
                foreach (var loop in section.Outline)
                {
                    var array = new CurveArray();
                    foreach (var curve in CurveLoop.CreateViaTransform(loop, toFamily))
                        array.Append(curve);
                    if (array.Size > 0) arrays.Append(array);
                }
                if (arrays.Size == 0) { t.RollBack(); return false; }

                var sketch = SketchPlane.Create(famDoc, Plane.CreateByNormalAndOrigin(XYZ.BasisX, XYZ.Zero));

                Extrusion extrusion;
                try
                {
                    extrusion = famDoc.FamilyCreate.NewExtrusion(true, arrays, sketch, NominalLength);
                }
                catch (Exception ex)
                {
                    // Self-touching outlines and unclosed loops land here. Nothing to salvage.
                    Notes.Add($"Сечение \"{section.Raw}\": контур не принят Revit ({ex.Message}).");
                    t.RollBack();
                    return false;
                }

                if (!DriveLengthByParameter(famDoc, extrusion))
                {
                    // Falls back to the way a beam family is normally authored. It works when it
                    // works and says nothing when it does not, which is why it is second.
                    TieToEnds(famDoc, extrusion);
                }

                t.Commit();
                return true;
            }
        }

        /// <summary>Instance parameter the solid's far end is tied to, set per member on placement.</summary>
        public const string LengthParameter = "IFC2RVT_Длина";

        /// <summary>
        /// Makes the solid end follow an instance parameter the converter sets itself.
        ///
        /// This replaced aligning the solid to the template's end reference planes, which is how a
        /// beam family is authored by hand and which failed silently here: 2111 members were built
        /// at the template's nominal ten feet, and since the median member in the model is 590 mm
        /// long, 1786 of them came out as three-metre sticks. Nothing threw, and the geometry only
        /// looked wrong on screen.
        ///
        /// An associated parameter has no such failure mode. The association either exists or the
        /// call throws, the converter writes the exact axis length into it on every member, and
        /// nothing depends on Revit agreeing about which view a reference is visible in.
        /// </summary>
        static bool DriveLengthByParameter(Document famDoc, Extrusion extrusion)
        {
            try
            {
                var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
                if (end == null) return false;

                var manager = famDoc.FamilyManager;
#if REVIT2023_OR_GREATER
                var parameter = manager.AddParameter(LengthParameter, GroupTypeId.Geometry,
                                                     SpecTypeId.Length, true);
#else
                var parameter = manager.AddParameter(LengthParameter,
                                                     BuiltInParameterGroup.PG_GEOMETRY,
                                                     ParameterType.Length, true);
#endif
                if (parameter == null) return false;

                manager.Set(parameter, NominalLength);
                manager.AssociateElementParameterToFamilyParameter(end, parameter);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Aligns the solid's end faces to the reference planes the template dimensions with its
        /// Length parameter. The hand-authored way, kept only for the case where associating a
        /// parameter did not work.
        /// </summary>
        static void TieToEnds(Document famDoc, Extrusion extrusion)
        {
            try
            {
                var view = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate);
                if (view == null) return;

                var planes = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                    .Where(p => Math.Abs(p.Normal.Normalize().DotProduct(XYZ.BasisX)) > 0.99)
                    .OrderBy(p => p.BubbleEnd.X)
                    .ToList();
                if (planes.Count < 2) return;

                var options = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                var faces = extrusion.get_Geometry(options)
                    .OfType<Solid>()
                    .SelectMany(s => s.Faces.OfType<PlanarFace>())
                    .ToList();

                var start = faces.FirstOrDefault(f => f.FaceNormal.Normalize().DotProduct(XYZ.BasisX) < -0.99);
                var end = faces.FirstOrDefault(f => f.FaceNormal.Normalize().DotProduct(XYZ.BasisX) > 0.99);

                if (start?.Reference != null)
                    famDoc.FamilyCreate.NewAlignment(view, planes.First().GetReference(), start.Reference);
                if (end?.Reference != null)
                    famDoc.FamilyCreate.NewAlignment(view, planes.Last().GetReference(), end.Reference);
            }
            catch
            {
                // Measured later; an untied family is caught then.
            }
        }

        Family Load(Document famDoc, Section section)
        {
            var name = FamilyName(section);
            try { famDoc.OwnerFamily.Name = name; } catch { }

            try
            {
                return famDoc.LoadFamily(_doc, new Overwrite());
            }
            catch
            {
                // Some builds refuse a direct load while the project is in a transaction; going via
                // a file on disk works where that happens.
                return LoadViaFile(famDoc, name);
            }
        }

        Family LoadViaFile(Document famDoc, string name)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                              "IFC2RVT_" + Guid.NewGuid().ToString("N") + ".rfa");
            try
            {
                famDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                return _doc.LoadFamily(path, new Overwrite(), out var family) ? family : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
            }
        }

        FamilySymbol Activate(Family family, Section section)
        {
            var symbol = family.GetFamilySymbolIds()
                               .Select(id => _doc.GetElement(id))
                               .OfType<FamilySymbol>()
                               .FirstOrDefault();
            if (symbol == null) return null;

            try { if (!symbol.IsActive) symbol.Activate(); } catch { }

            // The exact designation, including the characters Revit will not take in a name, has to
            // survive somewhere a schedule can read it.
            try
            {
                var comments = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
                if (comments != null && !comments.IsReadOnly) comments.Set(section.Raw);
            }
            catch { }

            return symbol;
        }

        /// <summary>
        /// A name Revit will accept that still reads as the section.
        ///
        /// Revit forbids brackets in a family name, and the cold-formed channel designation is
        /// spelled with one - "Гн[160Х60Х4" - because the bracket is the channel symbol. Dropping it
        /// silently would leave a name a tube could equally claim, so the symbol is replaced by the
        /// word it stands for.
        /// </summary>
        static string FamilyName(Section section)
        {
            var text = section.Raw ?? section.Key;

            if (section.Parsed != null && section.Parsed.Kind == SteelKind.ColdFormedChannel)
                text = Regex.Replace(text, @"^Г[нH]\s*\[", "Гн ", RegexOptions.IgnoreCase) + " швеллер";

            foreach (var forbidden in new[] { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' })
                text = text.Replace(forbidden, ' ');

            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > 60) text = text.Substring(0, 60).Trim();

            return string.IsNullOrWhiteSpace(text) ? "IFC2RVT сечение" : text;
        }

        class Overwrite : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool inUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                                            out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        // ---- use ----------------------------------------------------------------------------

        /// <summary>The generated symbol for a designation, or null when there is none.</summary>
        public FamilySymbol Symbol(string designation)
        {
            var key = SteelDesignation.Normalise(designation);
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (_rejected.Contains(key)) return null;
            return _symbols.TryGetValue(key, out var symbol) ? symbol : null;
        }

        /// <summary>
        /// Writes the member's true length into the parameter the solid's end is tied to.
        ///
        /// Returns false when the family has no such parameter, which means the section fell back to
        /// aligning the solid to reference planes and takes its length from the placement line.
        /// </summary>
        public static bool SetLength(FamilyInstance instance, double length)
        {
            try
            {
                var parameter = instance.LookupParameter(LengthParameter);
                if (parameter == null || parameter.IsReadOnly) return false;
                return parameter.Set(length);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when the first member of this section still has to be measured.</summary>
        public bool NeedsCheck(string designation)
        {
            var key = SteelDesignation.Normalise(designation);
            return key != null && _unverified.Contains(key);
        }

        /// <summary>
        /// Measures a placed member against the length it was asked for, and drops the whole section
        /// if the family did not flex.
        ///
        /// This is the guard that makes generating families safe. If tying the solid to the end
        /// planes failed, every member of that section would be built at the template's nominal
        /// length - visibly wrong, and wrong in a way no exception reports. One measurement per
        /// section settles it, and a section that fails goes back to being geometry.
        /// </summary>
        public bool Accept(string designation, FamilyInstance instance, double expectedLength)
        {
            var key = SteelDesignation.Normalise(designation);
            if (key == null) return true;

            _unverified.Remove(key);

            const double mm = 304.8;
            var measured = LengthOf(instance);

            if (measured <= 0)
            {
                // Unmeasurable used to pass. It must not: the run that shipped 1786 over-long
                // members passed exactly here, because the geometry was asked for at coarse detail
                // and Revit draws framing as a line there, so there was no solid to measure and the
                // check waved everything through. Geometry we cannot check is geometry we cannot
                // vouch for, and DirectShape is the honest answer for it.
                _rejected.Add(key);
                _symbols.Remove(key);
                Notes.Add($"Сечение \"{designation}\": длину вставленного элемента измерить не " +
                          "удалось, проверить семейство нечем - оставлено геометрией.");
                return false;
            }

            var tolerance = Math.Max(0.05, expectedLength * 0.02);
            if (Math.Abs(measured - expectedLength) <= tolerance)
            {
                Verified++;
                return true;
            }

            _rejected.Add(key);
            _symbols.Remove(key);

            var affected = _counts.TryGetValue(key, out var n) ? n : 0;
            Notes.Add($"Сечение \"{designation}\": семейство не растягивается по длине " +
                      $"({measured * mm:F0} мм вместо {expectedLength * mm:F0}) - " +
                      $"элементы этого сечения ({affected} шт) оставлены геометрией.");
            return false;
        }

        /// <summary>
        /// Extent of the instance's solid along its own axis, in feet.
        ///
        /// Fine detail, and that is the whole point: at coarse detail Revit represents structural
        /// framing as a single line and hands back no solid at all, so this returned nothing and the
        /// check that depends on it never ran.
        /// </summary>
        static double LengthOf(FamilyInstance instance)
        {
            try
            {
                var axis = (instance.Location as LocationCurve)?.Curve;
                if (axis == null) return 0;

                var direction = (axis.GetEndPoint(1) - axis.GetEndPoint(0)).Normalize();

                double min = double.MaxValue, max = double.MinValue;
                var geometry = instance.get_Geometry(new Options
                {
                    DetailLevel = ViewDetailLevel.Fine,
                    IncludeNonVisibleObjects = false
                });
                if (geometry == null) return 0;

                foreach (var solid in Solids(geometry))
                {
                    foreach (Edge edge in solid.Edges)
                    {
                        foreach (var point in edge.Tessellate())
                        {
                            var t = point.DotProduct(direction);
                            if (t < min) min = t;
                            if (t > max) max = t;
                        }
                    }
                }

                return max > min ? max - min : 0;
            }
            catch
            {
                return 0;
            }
        }

        static IEnumerable<Solid> Solids(GeometryElement geometry)
        {
            foreach (var item in geometry)
            {
                if (item is Solid solid && solid.Volume > 0) yield return solid;
                else if (item is GeometryInstance nested)
                {
                    var inner = nested.GetInstanceGeometry();
                    if (inner == null) continue;
                    foreach (var s in Solids(inner)) yield return s;
                }
            }
        }
    }
}
