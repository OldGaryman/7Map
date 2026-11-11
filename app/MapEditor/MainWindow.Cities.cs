using System.Globalization;
using System.Windows;
using MapEditor.Map;

namespace MapEditor;

/// Städte erzeugen (Wunsch des Nutzers 7.11.2026, Schritt 2 nach dem Terraingenerator): Stadtarten wie im Spiel, ebener Hauptteil,
/// Ortsteile auf ihrer natürlichen Höhe mit geschwungenen Straßen (Vorbild Fidife-Land), Gateway mit dem Trader des Bioms,
/// Bauplätze zufällig passend bebaut.
/// Alles ist ein Bearbeitungsschritt (Strg+Z nimmt alle Städte zurück). Straßen zu anderen Städten und Gebäude außerhalb
/// der Städte folgen in späteren Schritten.
public partial class MainWindow
{
    CitySpec lastCities;

    async void CitiesButton_Click(object sender, RoutedEventArgs e)
    {
        if (world == null || edits == null || Map.Busy) return;
        if (!await ConfirmSwitch(null)) return;
        string game = GameFolder;
        var rules = CityRules.Load(game, world.Size);
        if (prefabFiles == null || prefabFiles.Count == 0 || rules.Types.Count == 0)
        {
            MessageBox.Show(this, Loc.T("The game folder is needed for towns (city tiles, buildings and the town rules of the game). Please set it in the settings."),
                Loc.T("Generate towns"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Stehen schon Städte auf der Karte? Warnung und Frage: Ersetzen (Terrainkopie zurück) / Ergänzen / Abbrechen
        string folder = world.Folder;
        bool hasTowns = world.Prefabs.Any(p => p.IsTile), replace = false;
        if (hasTowns)
        {
            if (TerrainCopy.Exists(folder))
            {
                var a = MessageBox.Show(this, Loc.F("This map already has towns.\n\nYes = replace: the map is reset to the terrain copy of {0}, and the new towns are generated on it. All towns, roads and buildings (POIs) are deleted, as well as all other changes made since then (spawn points stay). A backup of the current map is made first.\n\nNo = add: new towns are placed in the free areas; existing traders count towards the number per biome.\n\nCancel = do nothing",
                        TerrainCopy.Time(folder)?.ToString("yyyy-MM-dd HH:mm", Loc.Num)), Loc.T("Generate towns"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (a == MessageBoxResult.Cancel) return;
                replace = a == MessageBoxResult.Yes;
            }
            else if (MessageBox.Show(this, Loc.T("This map already has towns, but no copy of the terrain (it is made when a world is generated and before the first towns). Replacing the towns is therefore not possible.\n\nOK = add: new towns are placed in the free areas; existing traders count towards the number per biome.\n\nCancel = do nothing"),
                         Loc.T("Generate towns"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        }
        // Vorgabe: zuletzt benutzte Werte, sonst die Regeln des Spiels für diese Kartengröße, darüber default.ini und user.ini
        string worldClass = rules.WorldClass;
        UserValues.EnsureCityTypes(rules.Types, worldClass);   // fehlende Stadtarten (Werte des Spiels) in default.ini eintragen
        var freshTypes = rules.Types.Select(t => t.Clone()).ToList();
        UserValues.ApplyCityTypes(freshTypes, worldClass);
        var start = lastCities?.Clone() ?? UserValues.Load(new CitySpec { Types = freshTypes, Seed = world.Name });
        if (start.TradersPerBiome < 0) start.TradersPerBiome = CitySpec.DefaultTraders(world.Size);   // -1 in der .ini = automatisch
        if (lastCities != null && lastCities.Types.Count != rules.Types.Count) start.Types = freshTypes;
        var dlg = new CityWindow(start) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var spec = dlg.Spec;
        lastCities = spec;
        var stored = spec.Clone();
        if (stored.TradersPerBiome == CitySpec.DefaultTraders(world.Size)) stored.TradersPerBiome = -1;   // Vorgabe bleibt „automatisch“
        UserValues.Remember(stored);
        UserValues.RememberCityTypes(spec.Types, rules.Types, worldClass);
        if (replace)
        {
            // Ersetzen: Sicherung der heutigen Karte, Terrainkopie zurück, neu laden (ungespeicherte Änderungen gehen verloren – stand in der Warnung)
            try
            {
                Backups.Create(folder, "Vor dem Ersetzen der Städte (Terrainkopie zurückgelegt)");
                TerrainCopy.Restore(folder);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                MessageBox.Show(this, Loc.T("Resetting the map to the terrain copy failed:") + "\n" + ex.Message, Loc.T("Generate towns"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            edits = null;   // nicht noch einmal nach ungespeicherten Änderungen fragen
            await OpenWorld(folder);
            if (world == null || edits == null) return;
        }
        else if (!hasTowns)
        {
            // erste Städte: vorher das Terrain als Kopie ablegen (für späteres Ersetzen); dazu muss gespeichert sein
            if (edits.HasChanges)
            {
                Msg.Info(this, settings, "towns-save-first", Loc.T("Before the first towns the map is saved, so that a copy of the terrain can be kept in the project (needed to replace the towns later)."),
                        Loc.T("Generate towns"));
                if (!await Save()) return;
            }
            try { TerrainCopy.Save(folder); }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }
        // vorhandene Trader je Biom (beim Ergänzen)
        var existingTraders = new int[5];
        foreach (var p in world.Prefabs.Where(p => p.Name.StartsWith("trader_", StringComparison.OrdinalIgnoreCase)))
            existingTraders[CityGenerator.BiomeAt(world, p.X + p.SizeX / 2.0, p.Z + p.SizeZ / 2.0)]++;
        if ((spec.FillLots || spec.TradersPerBiome > 0) && !await EnsurePoiIndex()) spec.FillLots = false;

        // planen (im Hintergrund, liest nur)
        var w = world;
        Overlay.Visibility = Visibility.Visible;
        IProgress<string> progress = new Progress<string>(t => LoadingText.Text = Loc.T("Generating towns …") + "\n" + t);
        List<CityPlan> plans;
        CityGenerator gen;
        try
        {
            var names = prefabFiles.Keys.ToList();
            // Beschreibungen der Kacheln vorher lesen (TileDef nutzt Felder des Fensters)
            var defs = names.Where(n => n.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase)).ToDictionary(n => n, TileDef, StringComparer.OrdinalIgnoreCase);
            var transition = PartDef("part_highway_transition");   // Verjüngung am Gateway (vorher lesen: PartDef nutzt Felder des Fensters)
            (gen, plans) = await Task.Run(() =>
            {
                var g = new CityGenerator(w, spec, rules, defs.Keys, n => defs.GetValueOrDefault(n)) { PartDefOf = n => n == "part_highway_transition" ? transition : null, ExistingTraders = existingTraders };
                return (g, g.Plan(progress));
            });
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            MessageBox.Show(this, Loc.T("Planning the towns failed:") + "\n" + ex.Message, Loc.T("Generate towns"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { Overlay.Visibility = Visibility.Collapsed; }
        if (world != w || edits == null) return;
        if (plans.Count == 0)
        {
            Msg.Info(this, settings, "cities-1", Loc.T("No town found a suitable place.") + "\n\n" + string.Join("\n", gen.Problems), Loc.T("Generate towns"));
            return;
        }

        // Bauplätze bebauen: je Bauplatz ein passendes Gebäude, in einer Stadt möglichst keines doppelt. Trader nur auf dem
        // Gateway der Städte, die einen bekommen (je Biom der richtige); sonst nie ein Trader auf einem Bauplatz.
        var fill = new List<Filled>();
        var rnd = new Random(new GeneratorSpec { Seed = spec.Seed + "/lots" }.SeedValue);
        foreach (var plan in plans)
        {
            var inTown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            bool traderPlaced = false;
            foreach (var tile in plan.Tiles)
                foreach (var lot in TileLots.Of(tile))
                {
                    if (poiIndex == null) break;
                    if (tile == plan.Gateway && plan.Trader != null && !traderPlaced && poiIndex.TryGetValue(plan.Trader, out var td) && TileLots.Fits(td, lot))
                    {
                        var tb = TileLots.Place(lot, plan.Trader, td);
                        fill.Add(new Filled(tb, PrefabParts.For(tb, PartDef)));
                        traderPlaced = true;
                        continue;
                    }
                    if (!spec.FillLots) continue;
                    var fits = poiIndex.Where(kv => !kv.Key.StartsWith("trader_", StringComparison.OrdinalIgnoreCase) && TileLots.Fits(kv.Value, lot))
                        .Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                    if (fits.Count == 0) continue;
                    string name = fits.OrderBy(k => inTown.GetValueOrDefault(k) * 10 + rnd.NextDouble()).First();
                    inTown[name] = inTown.GetValueOrDefault(name) + 1;
                    var b = TileLots.Place(lot, name, poiIndex[name]);
                    fill.Add(new Filled(b, PrefabParts.For(b, PartDef)));
                }
            if (plan.Trader != null && !traderPlaced) plan.Trader = null;   // Trader nicht gefunden (Spielordner) oder Bauplatz passt nicht
        }

        int tiles = plans.Sum(p => p.Tiles.Count), roads = plans.Sum(p => p.Roads.Count);
        double grade = spec.RoadGrade;
        int changed = 0, steepRoads = 0;
        string detail = string.Format(CultureInfo.InvariantCulture, "Städte erzeugt (Startwert {0}, Ortsteile höchstens {1}, {2}–{3} m, Straßen bis {4:0.#} %, Trader je Biom {5}, Lage {6}): ",
                spec.Seed, spec.MaxParts, spec.DeltaMin, spec.DeltaMax, grade * 100, spec.TradersPerBiome, spec.Elevation)
            + string.Join("; ", plans.Select(p => string.Format(CultureInfo.InvariantCulture, "{0} bei {1:0},{2:0} ({3} Kacheln, Hauptteil {4}{8}, Ortsteile {5}, {6} Straßen{7})",
                p.Type.Name, p.CenterX, p.CenterZ, p.Tiles.Count, p.MainFloor, string.Join("/", p.PartFloors), p.Roads.Count, p.Trader != null ? ", " + p.Trader : "", p.Elevated ? " auf Anhöhe" : p.Terrace ? " Terrasse" : "")));
        var none = new Progress<string>(_ => { });
        RunPrefabStep(Loc.F("{0} town(s) generated", plans.Count), detail, ed =>
        {
            changed = 0; steepRoads = 0;
            foreach (var plan in plans)
            {
                foreach (var t in plan.Tiles) { ed.AddPrefab(t); ed.AddTileSurface(t); }
                foreach (var q in plan.Parts) ed.AddPrefab(q);   // nach den Kacheln (spätere Zeilen überschreiben frühere)
                changed += CityGenerator.ShapeTerrain(w, ed, plan);
            }
            // Straßen erst, wenn alle Kacheln stehen und das Gelände geformt ist
            foreach (var plan in plans)
                foreach (var road in plan.Roads)
                {
                    var r = CityGenerator.PlanRoad(w, road, grade);
                    if (r == null) continue;
                    if (r.MaxGrade > grade + 0.01) steepRoads++;
                    RoadApplier.Apply(w, ed, r, none);
                }
            foreach (var plan in plans) CityGenerator.SmoothSteps(w, ed, plan);   // Stufen zwischen dicht nebeneinander laufenden Straßen
            AddFilled(ed, fill);
        }, null, () => Loc.F("{0} tiles, {1} roads, {2} buildings.", tiles, roads, fill.Count));
        if (world != w) return;

        // Übersicht: Städte mit Lage (wie die Kompassanzeige im Spiel), Höhen und Trader
        string Pos(double x, double z) => Loc.F("{0} {1}, {2} {3}", Math.Abs(Math.Round(x)), x < 0 ? Loc.T("W") : Loc.T("E"), Math.Abs(Math.Round(z)), z < 0 ? Loc.T("S") : Loc.T("N"));
        string Parts(CityPlan p) => p.PartFloors.Count == 0 ? "" : ", " + Loc.F("district(s) at {0} m", string.Join(" / ", p.PartFloors));
        var lines = plans.Select(p => "• " + Loc.F("{0} – {1}: {2} tiles, main part at {3} m", p.Type.Label, Pos(p.CenterX, p.CenterZ), p.Tiles.Count, p.MainFloor)
            + Parts(p) + (p.Elevated ? ", " + Loc.F("on a hilltop (levelled, up to {0:0} m removed)", p.MaxCut) : p.Terrace ? ", " + Loc.F("terrace cut into the mountainside (up to {0:0} m removed)", p.MaxCut) : "")
            + (p.Trader != null ? ", " + Loc.F("trader {0}", TraderName(p.Trader)) : ""));
        string text = Loc.F("{0} town(s) with {1} tiles, {2} road(s) and {3} building(s) were generated.", plans.Count, tiles, roads, fill.Count)
            + "\n\n" + string.Join("\n", lines)
            + (gen.Problems.Count > 0 ? "\n\n" + Loc.T("Not everything fitted:") + "\n" + string.Join("\n", gen.Problems.Select(p => "• " + p)) : "")
            + (steepRoads > 0 ? "\n\n" + Loc.F("{0} road(s) are steeper than planned – please check.", steepRoads) : "")
            + "\n\n" + Loc.T("Open road ends at the town edges are for the roads to other towns. Next step: Creation → Roads. Undo with Ctrl+Z, keep with Save.");
        Msg.Info(this, settings, "towns-done", text, Loc.T("Generate towns"));
    }

    // trader_rekt → Rekt
    static string TraderName(string prefab) => prefab.Length > 7 ? char.ToUpperInvariant(prefab[7]) + prefab[8..] : prefab;
}
