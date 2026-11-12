using System.Globalization;
using System.Windows;
using MapEditor.Map;

namespace MapEditor;

/// Create cities (the user's request, 2026-11-07, step 2 after the terrain generator): town types like in the game, level main part,
/// districts at their natural height with curved roads (model Fidife-Land), gateway with the trader of the biome,
/// lots built up with randomly fitting buildings.
/// Everything is one edit step (Ctrl+Z undoes all cities). Roads to other cities and buildings outside
/// the cities follow in later steps.
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
        // Are there already cities on the map? Warning and question: Replace (terrain copy back) / Add / Cancel
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
        // preset: last used values, otherwise the rules of the game for this map size, on top of that default.ini and user.ini
        string worldClass = rules.WorldClass;
        UserValues.EnsureCityTypes(rules.Types, worldClass);   // enter missing town types (values of the game) into default.ini
        var freshTypes = rules.Types.Select(t => t.Clone()).ToList();
        UserValues.ApplyCityTypes(freshTypes, worldClass);
        var start = lastCities?.Clone() ?? UserValues.Load(new CitySpec { Types = freshTypes, Seed = world.Name });
        if (start.TradersPerBiome < 0) start.TradersPerBiome = CitySpec.DefaultTraders(world.Size);   // -1 in the .ini = automatic
        if (lastCities != null && lastCities.Types.Count != rules.Types.Count) start.Types = freshTypes;
        var dlg = new CityWindow(start) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var spec = dlg.Spec;
        lastCities = spec;
        var stored = spec.Clone();
        if (stored.TradersPerBiome == CitySpec.DefaultTraders(world.Size)) stored.TradersPerBiome = -1;   // preset stays "automatic"
        UserValues.Remember(stored);
        UserValues.RememberCityTypes(spec.Types, rules.Types, worldClass);
        if (replace)
        {
            // Replace: backup of today's map, terrain copy back, reload (unsaved changes are lost – it said so in the warning)
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
            edits = null;   // do not ask again about unsaved changes
            await OpenWorld(folder);
            if (world == null || edits == null) return;
        }
        else if (!hasTowns)
        {
            // first cities: store the terrain as a copy beforehand (for later replacing); it has to be saved for that
            if (edits.HasChanges)
            {
                Msg.Info(this, settings, "towns-save-first", Loc.T("Before the first towns the map is saved, so that a copy of the terrain can be kept in the project (needed to replace the towns later)."),
                        Loc.T("Generate towns"));
                if (!await Save()) return;
            }
            try { TerrainCopy.Save(folder); }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }
        // existing traders per biome (when adding)
        var existingTraders = new int[5];
        foreach (var p in world.Prefabs.Where(p => p.Name.StartsWith("trader_", StringComparison.OrdinalIgnoreCase)))
            existingTraders[CityGenerator.BiomeAt(world, p.X + p.SizeX / 2.0, p.Z + p.SizeZ / 2.0)]++;
        if ((spec.FillLots || spec.TradersPerBiome > 0) && !await EnsurePoiIndex()) spec.FillLots = false;

        // plan (in the background, only reads)
        var w = world;
        Overlay.Visibility = Visibility.Visible;
        IProgress<string> progress = new Progress<string>(t => LoadingText.Text = Loc.T("Generating towns …") + "\n" + t);
        List<CityPlan> plans;
        CityGenerator gen;
        try
        {
            var names = prefabFiles.Keys.ToList();
            // read the descriptions of the tiles beforehand (TileDef uses fields of the window)
            var defs = names.Where(n => n.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase)).ToDictionary(n => n, TileDef, StringComparer.OrdinalIgnoreCase);
            var transition = PartDef("part_highway_transition");   // taper at the gateway (read beforehand: PartDef uses fields of the window)
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

        // build up lots: one fitting building per lot, in a city if possible none twice. Traders only on the
        // gateway of the cities that get one (the right one per biome); otherwise never a trader on a lot.
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
            if (plan.Trader != null && !traderPlaced) plan.Trader = null;   // trader not found (game folder) or lot does not fit
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
                foreach (var q in plan.Parts) ed.AddPrefab(q);   // after the tiles (later rows overwrite earlier ones)
                changed += CityGenerator.ShapeTerrain(w, ed, plan);
            }
            // roads only when all tiles stand and the terrain is shaped
            foreach (var plan in plans)
                foreach (var road in plan.Roads)
                {
                    var r = CityGenerator.PlanRoad(w, road, grade);
                    if (r == null) continue;
                    if (r.MaxGrade > grade + 0.01) steepRoads++;
                    RoadApplier.Apply(w, ed, r, none);
                }
            foreach (var plan in plans) CityGenerator.SmoothSteps(w, ed, plan);   // steps between roads running close beside each other
            AddFilled(ed, fill);
        }, null, () => Loc.F("{0} tiles, {1} roads, {2} buildings.", tiles, roads, fill.Count));
        if (world != w) return;

        // overview: cities with location (like the compass display in the game), heights and trader
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
