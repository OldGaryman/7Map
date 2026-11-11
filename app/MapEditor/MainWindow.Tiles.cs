using System.Globalization;
using System.Windows;
using MapEditor.Map;

namespace MapEditor;

/// Stadt-Kacheln einfügen, ändern, austauschen und löschen (Wunsch des Nutzers 2.11.2026).
/// Regeln des Spiels (siehe CLAUDE.md): Kacheln liegen im 150-m-Raster, alle Kacheln einer Stadt auf derselben Bodenhöhe; das Spiel
/// ebnet den Boden innerhalb der Kachel auf diese Höhe. Gebäude auf den Bauplätzen und Teile in der Kachel (Lampen, Übergänge …)
/// stehen als eigene Zeilen in prefabs.xml und wandern deshalb mit, wenn die Kachel verschoben oder gedreht wird. Teile einer neuen\n/// Kachel (PartSpawn-Markierungen der Kachel) werden nicht gesetzt: das Spiel wählt sie nach unbekannten Regeln (an der Originalwelt\n/// stimmten nur 39 % der vorhergesagten Lagen), falsche Teile wären schlechter als keine.
public partial class MainWindow
{
    // ---------- gemeinsame Hilfen ----------

    /// Inhalt einer Kachel: Gebäude und Teile, deren Mitte in der Grundfläche liegt, dazu die Teile (Zufahrten) dieser Gebäude
    List<PlacedPrefab> TileContents(PlacedPrefab t)
    {
        var inside = world.Prefabs.Where(q => q != t && !q.IsTile && q.Def != null && t.Contains(q.X + q.SizeX / 2.0, q.Z + q.SizeZ / 2.0)).ToList();
        var all = new HashSet<PlacedPrefab>(inside);
        foreach (var b in inside.Where(b => b.Def.Kind != PrefabKind.Part))
            foreach (var q in PrefabParts.Attached(world, b)) all.Add(q);
        return all.ToList();
    }

    static bool Overlaps(PlacedPrefab a, PlacedPrefab b) => a.X < b.X + b.SizeX && a.X + a.SizeX > b.X && a.Z < b.Z + b.SizeZ && a.Z + a.SizeZ > b.Z;

    // grenzen die Rechtecke mit einer Kante aneinander?
    static bool Adjacent(PlacedPrefab a, PlacedPrefab b) =>
        ((a.X + a.SizeX == b.X || b.X + b.SizeX == a.X) && a.Z < b.Z + b.SizeZ && a.Z + a.SizeZ > b.Z)
        || ((a.Z + a.SizeZ == b.Z || b.Z + b.SizeZ == a.Z) && a.X < b.X + b.SizeX && a.X + a.SizeX > b.X);

    /// Höhe y der Kachel an ihrer Lage: Grenzt eine andere Kachel an, gilt deren Bodenhöhe (eine Stadt liegt auf einer Höhe);
    /// sonst das Mittel des Geländes unter der Grundfläche (wie bei Gebäuden)
    int TileY(PlacedPrefab g, PlacedPrefab except)
    {
        var nb = world.Prefabs.FirstOrDefault(o => o != g && o != except && o.IsTile && o.Def != null && Adjacent(o, g));
        if (nb != null) return nb.Y - nb.Def.YOffset + g.Def.YOffset;
        return (int)Math.Round(GroundShaper.AverageHeight(world, g.X, g.Z, g.SizeX, g.SizeZ) + 0.85) + g.Def.YOffset;
    }

    /// Prüfung der neuen Lage einer Kachel; false = nicht möglich oder vom Nutzer abgelehnt
    bool CheckTileSpot(PlacedPrefab g, PlacedPrefab moving, string title)
    {
        if (g.X < -world.Half || g.X + g.SizeX > world.Half || g.Z < -world.Half || g.Z + g.SizeZ > world.Half)
        {
            Msg.Info(this, settings, "tiles-1", Loc.T("The tile would be partly outside the map."), title);
            return false;
        }
        var tile = world.Prefabs.FirstOrDefault(o => o != moving && o.IsTile && Overlaps(o, g));
        if (tile != null)
        {
            Msg.Info(this, settings, "tiles-2", Loc.F("There is already a city tile: {0}. Delete it first.", tile.Name), title);
            return false;
        }
        var own = moving == null ? new List<PlacedPrefab>() : TileContents(moving);
        var hits = world.Prefabs.Where(o => o != moving && !own.Contains(o) && o.Def?.Kind != PrefabKind.Part && Overlaps(o, g)).ToList();
        int roadCells = 0;
        for (int z = g.Z; z < g.Z + g.SizeZ; z++)
            for (int x = g.X; x < g.X + g.SizeX; x++)
                if (world.RoadAt(x, z) != 0) roadCells++;
        var problems = new List<string>();
        if (hits.Count > 0) problems.Add(Loc.F("The tile overlaps: {0}.", string.Join(", ", hits.Select(Caption).Distinct().Take(5))));
        if (roadCells > 0) problems.Add(Loc.F("{0} m² of road lie in the tile area; the game covers them with the tile's ground.", roadCells));
        return problems.Count == 0 || MessageBox.Show(this, string.Join("\n", problems) + "\n\n" + Loc.T("Continue anyway?"), title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    PrefabDef TileDef(string name)
    {
        if (!describedDefs.TryGetValue(name, out var def))
        {
            string file = prefabFiles?.GetValueOrDefault(name);
            if (file == null) return null;
            describedDefs[name] = def = PrefabCatalog.Describe(file);
            def.Kind = PrefabKind.Tile;
        }
        return def.Found ? def : null;
    }

    // ---------- Einfügen ----------

    /// Platzieren (moving = null) oder Verschieben einer Kachel an die Stelle des Phantoms bzw. des gezogenen Umrisses
    void PlaceTileAt(PlacedPrefab g, PlacedPrefab moving)
    {
        var title = Loc.T(moving == null ? "Place city tile" : "Move city tile");
        if (moving != null && moving.X == g.X && moving.Z == g.Z) return;
        if (!CheckTileSpot(g, moving, title)) return;
        int y = TileY(g, moving);
        if (moving != null) { ChangeTile(moving, g.X, y, g.Z, g.Rotation); return; }
        PlaceNewTile(new PlacedPrefab { Name = g.Name, Def = g.Def, X = g.X, Y = y, Z = g.Z, Rotation = g.Rotation });
    }

    async void PlaceNewTile(PlacedPrefab tile)
    {
        var w = world;
        var fill = await AskFillLots(tile);
        if (fill == null || world != w || edits == null) return;   // abgebrochen oder inzwischen andere Welt
        double bumps = settings.EraserBumps;
        GroundShaper.ShapeInfo info = null;
        RunPrefabStep(Loc.F("City tile {0} placed", Caption(tile)), Detail("platziert", tile) + FillDetail(fill), e =>
        {
            info = GroundShaper.Flatten(w, e, tile, tile.GroundHeight, bumps, null, true);
            e.AddPrefab(tile);
            e.AddTileSurface(tile);
            AddFilled(e, fill);
        }, tile, () => ShapeHint(info, null, false, new[] { tile }) + " " + Loc.F("Ground height {0:0.0} m.", tile.GroundHeight) + FillHint(fill));
    }

    // ---------- Bauplätze automatisch bebauen ----------

    record Filled(PlacedPrefab Building, List<PlacedPrefab> Parts);

    /// Frage nach dem automatischen Bebauen der Bauplätze einer neuen Kachel; leere Liste = nicht bebauen, null = abgebrochen
    async Task<List<Filled>> AskFillLots(PlacedPrefab tile)
    {
        var none = new List<Filled>();
        if (tile.Def.Lots.Count == 0) return none;
        var answer = MessageBox.Show(this, Loc.F("The tile has {0} building lot(s). Fill them with randomly chosen buildings that fit?\n\nYes = fill, No = leave the lots empty (you can build on them later by clicking the green frames).", tile.Def.Lots.Count),
            Loc.T("Place city tile"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return null;
        if (answer == MessageBoxResult.No) return none;
        if (!await EnsurePoiIndex())
        {
            Msg.Info(this, settings, "tiles-3", Loc.T("The game folder is needed to find fitting buildings. The lots stay empty."), Loc.T("Place city tile"));
            return none;
        }
        var rnd = new Random(tile.X * 31 + tile.Z * 17 + tile.Rotation);
        var list = new List<Filled>();
        foreach (var lot in TileLots.Of(tile))
        {
            var fits = poiIndex.Where(kv => TileLots.Fits(kv.Value, lot)).Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            if (fits.Count == 0) continue;
            string name = fits[rnd.Next(fits.Count)];
            var b = TileLots.Place(lot, name, poiIndex[name]);
            list.Add(new Filled(b, PrefabParts.For(b, PartDef)));
        }
        return list;
    }

    static void AddFilled(WorldEdits e, List<Filled> fill)
    {
        foreach (var f in fill)
        {
            e.AddPrefab(f.Building);
            foreach (var q in f.Parts) e.AddPrefab(q);
        }
    }

    static string FillDetail(List<Filled> fill) =>
        fill.Count == 0 ? "" : ", Gebäude: " + string.Join(";", fill.Select(f => f.Building.Name));

    static string FillHint(List<Filled> fill) => fill.Count == 0 ? "" : " " + Loc.F("{0} building(s) placed on the lots.", fill.Count);

    // ---------- Ändern: verschieben, drehen, Höhe ----------

    /// Lage, Drehung oder Bauhöhe einer Kachel ändern; Gebäude und Teile darin wandern mit (starre Drehung um die Kachel)
    void ChangeTile(PlacedPrefab t, int x, int y, int z, int rotation)
    {
        if (t.X == x && t.Y == y && t.Z == z && t.Rotation == rotation) return;
        rotation = (rotation % 4 + 4) % 4;
        var contents = TileContents(t);
        var old = new PlacedPrefab { Name = t.Name, Def = t.Def, X = t.X, Y = t.Y, Z = t.Z, Rotation = t.Rotation };
        int turn = ((rotation - t.Rotation) % 4 + 4) % 4, dy = y - t.Y;
        // neue Lage jedes Teils: Rechteck relativ zur Kachel drehen (kleinste Ecke), Drehung und Höhe mitnehmen
        var moved = contents.Select(q =>
        {
            double lx = q.X - t.X, lz = q.Z - t.Z;
            var (ax, az) = PlacedPrefab.Rotate(lx, lz, turn, t.SizeX, t.SizeZ);
            var (bx, bz) = PlacedPrefab.Rotate(lx + q.SizeX, lz + q.SizeZ, turn, t.SizeX, t.SizeZ);
            return (Q: q, X: x + (int)Math.Round(Math.Min(ax, bx)), Y: q.Y + dy, Z: z + (int)Math.Round(Math.Min(az, bz)), R: (q.Rotation + turn) % 4);
        }).ToList();
        bool relocated = old.X != x || old.Z != z;
        var w = world;
        double bumps = settings.EraserBumps;
        GroundShaper.ShapeInfo freed = null, placed = null;
        string what = relocated ? "verschoben" : "geändert";
        RunPrefabStep(Loc.F("City tile {0} changed", Caption(t)), Detail(what, t) + string.Format(CultureInfo.InvariantCulture, " → {0},{1},{2} Drehung {3}, {4} Teile mit", x, y, z, rotation, contents.Count), e =>
        {
            e.RemoveTileSurface(t);
            e.MovePrefab(t, x, y, z, rotation);
            foreach (var m in moved) e.MovePrefab(m.Q, m.X, m.Y, m.Z, m.R);
            if (relocated) freed = GroundShaper.Naturalize(w, e, new List<PlacedPrefab> { old }, bumps);
            placed = GroundShaper.Flatten(w, e, t, t.GroundHeight, bumps, contents, true);
            e.AddTileSurface(t);
        }, t, () => ShapeHint(placed, freed, false, new[] { t }) + (contents.Count > 0 ? " " + Loc.F("{0} building(s)/part(s) moved with it.", contents.Count) : ""));
    }

    // ---------- Austauschen ----------

    /// Kachel gegen eine andere Kachelart derselben Größe tauschen (Lage, Drehung und Höhe bleiben, Inhalt wird ersetzt)
    async void SwapTileDialog(PlacedPrefab old)
    {
        if (old == null || world == null || edits == null || Map.Busy) return;
        var candidates = (prefabFiles?.Keys ?? Enumerable.Empty<string>())
            .Where(n => n.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase) && !n.Equals(old.Name, StringComparison.OrdinalIgnoreCase))
            .Select(n => (Name: n, Def: TileDef(n))).Where(c => c.Def != null && c.Def.SizeX == old.Def.SizeX && c.Def.SizeZ == old.Def.SizeZ)
            .Select(c => new SwapCandidate(c.Name, c.Def.Label, c.Def.Tier, c.Def.SizeX, c.Def.SizeZ, ImageOf(c.Name)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
        {
            Msg.Info(prefabWindow, settings, "tiles-4", Loc.T("No other city tile of this size was found in the game folder."), Loc.T("Swap city tile"));
            return;
        }
        var dlg = new SwapWindow(Loc.F("Swap \"{0}\" for:", Caption(old)), Loc.F("{0} city tile(s) of {1} × {2} m.", candidates.Count, old.Def.SizeX, old.Def.SizeZ), candidates) { Owner = prefabWindow };
        if (dlg.ShowDialog() != true || dlg.Chosen == null) return;
        var def = TileDef(dlg.Chosen);
        var contents = TileContents(old);
        if (contents.Count > 0 && MessageBox.Show(prefabWindow, Loc.F("The {0} building(s)/part(s) inside are deleted: the new tile has its own lots.", contents.Count) + "\n\n" + Loc.T("Continue?"),
                Loc.T("Swap city tile"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var tile = new PlacedPrefab { Name = dlg.Chosen, Def = def, X = old.X, Z = old.Z, Rotation = old.Rotation, Y = old.Y - old.Def.YOffset + def.YOffset };
        var fill = await AskFillLots(tile);
        if (fill == null || edits == null) return;
        var oldParts = contents;
        RunPrefabStep(Loc.F("City tile {0} replaced by {1}", Caption(old), Caption(tile)), Detail("getauscht", old) + " → " + tile.Name + FillDetail(fill), e =>
        {
            e.RemoveTileSurface(old);
            foreach (var q in oldParts) e.RemovePrefab(q);
            e.ReplacePrefab(old, tile);
            e.AddTileSurface(tile);
            AddFilled(e, fill);
        }, tile, () => FillHint(fill).Trim());
    }

    // ---------- Löschen ----------

    void DeleteTile(PlacedPrefab t)
    {
        if (t == null || world == null || edits == null || Map.Busy) return;
        var contents = TileContents(t);
        int buildings = contents.Count(q => q.Def.Kind != PrefabKind.Part), parts = contents.Count - buildings;
        var owner = prefabWindow.IsVisible ? (Window)prefabWindow : this;
        bool all = true;
        string text = Loc.F("Delete city tile \"{0}\"?", Caption(t));
        if (contents.Count == 0)
        {
            text += "\n\n" + Loc.T("The ground adapts to the surroundings. Trees and plants are placed by the game in a new game. Undo with Ctrl+Z.");
            if (MessageBox.Show(owner, text, Loc.T("Delete city tile"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        else
        {
            text += "\n\n" + Loc.F("{0} building(s) and {1} part(s) stand inside.", buildings, parts)
                  + "\n" + Loc.T("Yes = delete the tile and everything inside\nNo = delete only the tile (the buildings stay)\nCancel = keep everything")
                  + "\n\n" + Loc.T("The ground adapts to the surroundings. Trees and plants are placed by the game in a new game. Undo with Ctrl+Z.");
            var r = MessageBox.Show(owner, text, Loc.T("Delete city tile"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            all = r == MessageBoxResult.Yes;
        }
        var removed = new List<PlacedPrefab> { t };
        if (all) removed.AddRange(contents);
        var w = world;
        double bumps = settings.EraserBumps;
        var footprint = new List<PlacedPrefab> { new PlacedPrefab { Name = t.Name, Def = t.Def, X = t.X, Y = t.Y, Z = t.Z, Rotation = t.Rotation } };
        GroundShaper.ShapeInfo info = null;
        RunPrefabStep(Loc.F("City tile {0} deleted", Caption(t)), Detail("gelöscht", t) + (all ? string.Format(CultureInfo.InvariantCulture, ", {0} Teile mit", contents.Count) : ", Inhalt bleibt"), e =>
        {
            e.RemoveTileSurface(t);
            foreach (var q in removed) e.RemovePrefab(q);
            info = GroundShaper.Naturalize(w, e, footprint, bumps);
        }, null, () => ShapeHint(null, info, true));
    }

    // ---------- Fenster „Gebäude“ ----------

    // Kachel im Fenster: Angaben zu Viertel und Bauplätzen statt des Bauplatz-Kastens
    void ShowTile(PlacedPrefab t)
    {
        var win = prefabWindow;
        int total = t.Def?.Lots.Count ?? 0;
        var inside = world.Prefabs.Where(q => !q.IsTile && q.Def != null && q.Def.Kind != PrefabKind.Part && t.Contains(q.X + q.SizeX / 2.0, q.Z + q.SizeZ / 2.0)).ToList();
        int built = TileLots.Of(t).Count(l => inside.Any(b => b.X + b.SizeX / 2.0 > l.X && b.X + b.SizeX / 2.0 < l.X + l.Width && b.Z + b.SizeZ / 2.0 > l.Z && b.Z + b.SizeZ / 2.0 < l.Z + l.Depth));
        win.LotText.Text = Loc.F("City tile of the district {0}: {1} lot(s), {2} built. Tiles snap to the 150 m grid; buildings inside move with the tile.", TileLots.District(t), total, built);
        win.LotPanel.Visibility = Visibility.Visible;
    }
}
