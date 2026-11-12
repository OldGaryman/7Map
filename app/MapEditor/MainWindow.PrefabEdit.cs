using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MapEditor.Map;

namespace MapEditor;

/// Place, change and delete buildings (the user's request, 2026-10-29): "+" in the building list attaches the outline to the
/// mouse pointer (R = rotate, Esc/right click = cancel), a click places it at the mean terrain height and levels the
/// ground beneath. The detail window shows position, build height and rotation (changeable), move and delete. Deleting takes
/// parts (driveways …) on the building along and adapts the ground to the surroundings. Everything is edit steps.
public partial class MainWindow
{
    readonly PrefabWindow prefabWindow = new();
    PrefabEntry windowEntry;     // building type in the window "Building" (with the list of its copies)
    PlacedPrefab detailPrefab;   // copy chosen there (null = none; the fields below are then locked)
    bool fillingCopies;


    static string GhostHelp => Loc.T("Place building: click = place · R = rotate · drag (left or right mouse button) = move the map · right-click or Esc = cancel");

    void InitPrefabEdit()
    {
        Map.GhostPlaced += GhostPlaced;
        Map.GhostCancelled += EndGhost;
        Map.SelectedDragged += SetPendingMove;
        Map.PrefabFileOf = name => prefabFiles?.GetValueOrDefault(name);   // surface of the buildings from their blocks
        Map.PartsFor = p => PrefabParts.For(p, PartDef);                   // driveways in the preview
        prefabWindow.ConfirmMoveButton.Click += (_, _) => ConfirmMove();
        prefabWindow.CancelMoveButton.Click += (_, _) => CancelMove();
        prefabWindow.FromTerrainButton.Click += (_, _) =>
        {
            if (detailPrefab == null) return;
            prefabWindow.GroundBox.Text = TerrainUnder(detailPrefab).Mean.ToString("0.0", Num);
            ApplyDetailValues();
        };
        foreach (var b in new[] { prefabWindow.Rot0, prefabWindow.Rot1, prefabWindow.Rot2, prefabWindow.Rot3 })
            b.Click += (_, _) => ApplyRotation(int.Parse((string)b.Tag, CultureInfo.InvariantCulture));
        foreach (var box in new[] { prefabWindow.XBox, prefabWindow.ZBox, prefabWindow.GroundBox })
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyDetailValues(); e.Handled = true; } };
        prefabWindow.Hidden += () => { ClearPendingMove(); detailPrefab = null; windowEntry = null; Map.Selected = null; };
        prefabWindow.PlaceCopyButton.Click += (_, _) => { if (windowEntry != null) StartPlacing(windowEntry); };
        prefabWindow.SwapButton.Click += SwapButton_Click;
        prefabWindow.TileGridCheck.IsChecked = settings.TileGrid;
        Map.TileSnap = settings.TileGrid;
        prefabWindow.TileGridCheck.Click += (_, _) =>
        {
            settings.TileGrid = prefabWindow.TileGridCheck.IsChecked == true;
            Map.TileSnap = settings.TileGrid;
            settings.Save();
        };
        prefabWindow.CopyList.SelectionChanged += (_, _) =>
        {
            if (fillingCopies || (prefabWindow.CopyList.SelectedItem as ListBoxItem)?.Tag is not PlacedPrefab p) return;
            MarkCopy(p, center: true);
            ShowBuildingWindow(windowEntry, p);
        };
        ApplyRightPanel();
        Loaded += (_, _) => prefabWindow.Owner = this;
    }

    // ---------- Move with the move icon: first confirm in the window "Building" (the user's request) ----------

    (PlacedPrefab P, int X, int Z)? pendingMove;

    void SetPendingMove(PlacedPrefab p, int x, int z)
    {
        if (p != detailPrefab || (x == p.X && z == p.Z)) { CancelMove(); return; }
        pendingMove = (p, x, z);
        var win = prefabWindow;
        win.XBox.Text = x.ToString(Num);
        win.ZBox.Text = z.ToString(Num);
        foreach (var box in new[] { win.XBox, win.ZBox }) box.BorderBrush = System.Windows.Media.Brushes.Goldenrod;
        win.MoveText.Text = Loc.F("Moved on the map to x {0}, z {1} ({2:+0;-0;0} m / {3:+0;-0;0} m). Not applied yet – the building height is taken from the terrain there.",
            x, z, x - p.X, z - p.Z);
        win.MovePanel.Visibility = Visibility.Visible;
    }

    void ConfirmMove()
    {
        if (pendingMove is not (PlacedPrefab p, int x, int z)) return;
        // stays open if the confirmation (overlap, road) is declined: keep dragging or cancel
        PlaceAt(new PlacedPrefab { Name = p.Name, Def = p.Def, X = x, Z = z, Rotation = p.Rotation }, p);
        if (p.X == x && p.Z == z) ClearPendingMove();
    }

    // discard: outline gone, fields back to the previous position
    void CancelMove()
    {
        bool had = pendingMove != null || Map.PendingMove != (0, 0);
        ClearPendingMove();
        if (had && detailPrefab != null) ShowBuildingWindow(windowEntry ?? EntryFor(detailPrefab), detailPrefab);
    }

    void ClearPendingMove()
    {
        pendingMove = null;
        Map.PendingMove = (0, 0);
        prefabWindow.MovePanel.Visibility = Visibility.Collapsed;
    }

    // footprint is leveled or made natural after deleting – not with parts (lie on the ground) and tiles
    static bool ShapesGround(PlacedPrefab p) => p.Def != null && p.Def.Kind is PrefabKind.Poi or PrefabKind.Filler;

    static string Caption(PlacedPrefab p) => string.IsNullOrEmpty(p.Def?.Label) ? p.Name : p.Def.Label;

    // ---------- Place and move ----------

    /// attach a building to the mouse pointer ("+ Place" on the right)
    async void StartPlacing(PrefabEntry entry)
    {
        if (entry == null || world == null) return;
        // brushes, mountains and eraser take effect at once: their tool is simply ended (layout 2026-11-04).
        // Only an unfinished road/chain asks (apply/discard/cancel).
        if (!await ConfirmSwitch(null)) return;
        if (Map.EraseMode) SetEraseMode(false);
        if (Map.EditMode || Map.RoadPoints.Count > 0) ClearRoad();
        var def = DefFor(entry);
        if (def == null || !def.Found)
        {
            Msg.Info(this, settings, "info-1", Loc.F("The description of \"{0}\" was not found in the game folder.", entry.Name), Loc.T("Place building"));
            return;
        }
        EndSpawnMode();
        Map.TileSnap = settings.TileGrid;
        Map.Ghost = new PlacedPrefab { Name = entry.Name, Def = def };
        Map.SetGhostCenter(Map.ToWorldX(Map.ActualWidth / 2), Map.ToWorldZ(Map.ActualHeight / 2));
        HelpText.Text = Map.Ghost.IsTile ? Loc.T("Place city tile: click = place · R = rotate · Alt = switch snapping on/off while moving · right-click or Esc = cancel") : GhostHelp;
        Map.Focus();
    }

    // description of a building: from the map (same name) or from its XML in the game folder
    PrefabDef DefFor(PrefabEntry entry)
    {
        var onMap = entry.Instances.FirstOrDefault()?.Def ?? world.Prefabs.FirstOrDefault(p => p.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))?.Def;
        if (onMap != null) return onMap;
        if (entry.File == null) return null;
        if (!describedDefs.TryGetValue(entry.Name, out var def)) describedDefs[entry.Name] = def = PrefabCatalog.Describe(entry.File);
        def.Kind = entry.Category.Key switch { "part" => PrefabKind.Part, "tile" => PrefabKind.Tile, _ => entry.Name.Contains("_filler", StringComparison.OrdinalIgnoreCase) ? PrefabKind.Filler : PrefabKind.Poi };
        def.Label = entry.Label ?? "";
        return def;
    }

    void EndGhost()
    {
        if (Map.Ghost == null) return;
        Map.Ghost = null;
        HelpText.Text = HelpIdle;
    }

    void GhostPlaced(PlacedPrefab g)
    {
        EndGhost();
        PlaceAt(g, null);
    }

    // new building (moving = null) or existing one to a new place (move icon dragged in the map)
    void PlaceAt(PlacedPrefab g, PlacedPrefab moving)
    {
        if (world == null || edits == null || Map.Busy) return;   // Busy: saving/applying a road is running right now (audit finding F19)
        if (g.IsTile) { PlaceTileAt(g, moving); return; }   // city tile: grid, height of the neighbors, content moves along (MainWindow.Tiles.cs)
        // at the map edge push it completely onto the map: only the center of the ghost is limited, the building could otherwise stand half
        // outside (audit finding F23)
        g.X = Math.Clamp(g.X, -world.Half, world.Half - g.SizeX);
        g.Z = Math.Clamp(g.Z, -world.Half, world.Half - g.SizeZ);
        // overlap with other buildings or roads: ask (roads keep their height, the building then stands partly on them)
        var hits = Overlapping(g, moving);
        int roadCells = 0;
        for (int z = g.Z; z < g.Z + g.SizeZ; z++)
            for (int x = g.X; x < g.X + g.SizeX; x++)
                if (world.RoadAt(x, z) != 0) roadCells++;
        var problems = new List<string>();
        if (hits.Count > 0) problems.Add(Loc.F("The building overlaps: {0}.", string.Join(", ", hits.Select(Caption).Distinct().Take(5))));
        if (roadCells > 0) problems.Add(Loc.F("It stands on {0} m² of road; the road keeps its height.", roadCells));
        if (problems.Count > 0 && MessageBox.Show(this, string.Join("\n", problems) + "\n\n" + Loc.T("Place it anyway?"),
                Loc.T("Place building"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // build height = mean of the terrain under the footprint (the user's request); y = ground + YOffset (whole meters)
        double mean = GroundShaper.AverageHeight(world, g.X, g.Z, g.SizeX, g.SizeZ);
        int y = (int)Math.Round(mean + 0.85) + g.Def.YOffset;
        if (moving != null) { MoveTo(moving, g.X, y, g.Z, g.Rotation); return; }
        var p = new PlacedPrefab { Name = g.Name, Def = g.Def, X = g.X, Y = y, Z = g.Z, Rotation = g.Rotation };
        var w = world;
        double bumps = settings.EraserBumps;
        // driveways and other parts from the markers of the building (the user's request: connect roads)
        var parts = PrefabParts.For(p, PartDef);
        var infos = new List<GroundShaper.ShapeInfo>();
        RunPrefabStep(Loc.F("Building {0} placed", Caption(p)), Detail("platziert", p) + PartsDetail(parts), e =>
        {
            infos.Clear();
            e.AddPrefab(p);
            foreach (var q in parts) e.AddPrefab(q);
            if (!ShapesGround(p)) return;
            infos.Add(GroundShaper.Flatten(w, e, p, p.GroundHeight, bumps));
            foreach (var q in parts) infos.Add(GroundShaper.Flatten(w, e, q, p.GroundHeight, bumps));   // driveways at the ground height of the building
        }, p, () => ShapeHint(Merge(infos), null, false, parts.Append(p)) + PartsHint(parts));
    }

    // description of a part: from the map or from its XML in the game folder (null = not found)
    PrefabDef PartDef(string name)
    {
        var onMap = world.Prefabs.FirstOrDefault(q => q.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Def;
        if (onMap != null) return onMap;
        if (describedDefs.TryGetValue(name, out var def)) return def.Found ? def : null;
        string file = prefabFiles?.GetValueOrDefault(name);
        if (file == null) return null;
        describedDefs[name] = def = PrefabCatalog.Describe(file);
        def.Kind = PrefabKind.Part;
        return def.Found ? def : null;
    }

    static string PartsDetail(List<PlacedPrefab> parts) =>
        parts.Count == 0 ? "" : ", Teile: " + string.Join(";", parts.Select(q => string.Format(CultureInfo.InvariantCulture, "{0} {1},{2},{3} r{4}", q.Name, q.X, q.Y, q.Z, q.Rotation)));

    static string PartsHint(List<PlacedPrefab> parts) =>
        parts.Count == 0 ? "" : " " + Loc.F("With {0} driveway(s)/part(s): {1}.", parts.Count, string.Join(", ", parts.Select(q => q.Name).Distinct()));

    static GroundShaper.ShapeInfo Merge(List<GroundShaper.ShapeInfo> infos) =>
        infos.Count == 0 ? null : new GroundShaper.ShapeInfo(infos.Sum(i => i.Changed), infos.Sum(i => i.RoadCells), infos.SelectMany(i => i.Spared).Distinct().ToList());

    // other buildings and tiles under the footprint (parts do not count)
    List<PlacedPrefab> Overlapping(PlacedPrefab g, PlacedPrefab except) =>
        world.Prefabs.Where(o => o != except && o.Def?.Kind != PrefabKind.Part
            && o.X < g.X + g.SizeX && o.X + o.SizeX > g.X && o.Z < g.Z + g.SizeZ && o.Z + o.SizeZ > g.Z).ToList();

    // change position/height/rotation; at a new position the old place becomes natural, the new one is leveled
    void MoveTo(PlacedPrefab p, int x, int y, int z, int rotation)
    {
        if (p.X == x && p.Y == y && p.Z == z && p.Rotation == rotation) return;
        if (p.IsTile) { ChangeTile(p, x, y, z, rotation); return; }
        var old = new PlacedPrefab { Name = p.Name, Def = p.Def, X = p.X, Y = p.Y, Z = p.Z, Rotation = p.Rotation };
        bool relocated = old.X != x || old.Z != z || old.Rotation != rotation;
        var w = world;
        double bumps = settings.EraserBumps;
        // driveways move along: the previous parts gone, at the new position new from the markers (also new height)
        var oldParts = PrefabParts.Attached(w, p);
        var newParts = PrefabParts.For(new PlacedPrefab { Name = p.Name, Def = p.Def, X = x, Y = y, Z = z, Rotation = rotation }, PartDef);
        var placed = new List<GroundShaper.ShapeInfo>();
        GroundShaper.ShapeInfo freed = null;
        RunPrefabStep(Loc.F("Building {0} changed", Caption(p)), Detail("geändert", p) + string.Format(CultureInfo.InvariantCulture, " → {0},{1},{2} Drehung {3}", x, y, z, rotation) + PartsDetail(newParts), e =>
        {
            placed.Clear();
            foreach (var q in oldParts) e.RemovePrefab(q);
            e.MovePrefab(p, x, y, z, rotation);
            foreach (var q in newParts) e.AddPrefab(q);
            if (!ShapesGround(p)) return;
            if (relocated) freed = GroundShaper.Naturalize(w, e, oldParts.Prepend(old).ToList(), bumps);
            placed.Add(GroundShaper.Flatten(w, e, p, p.GroundHeight, bumps));
            foreach (var q in newParts) placed.Add(GroundShaper.Flatten(w, e, q, p.GroundHeight, bumps));
        }, p, () => ShapeHint(Merge(placed), freed, false, newParts.Append(p)) + PartsHint(newParts));
    }

    static string Detail(string what, PlacedPrefab p) =>
        string.Format(CultureInfo.InvariantCulture, "Gebäude {0} {1}: {2},{3},{4} Drehung {5}", p.Name, what, p.X, p.Y, p.Z, p.Rotation);

    /// delete a building (detail window or trash can in the list of copies), with confirmation
    void DeletePrefab(PlacedPrefab p)
    {
        if (p != null && p.IsTile) { DeleteTile(p); return; }
        if (p == null || world == null || edits == null || Map.Busy) return;
        // parts (driveways, signs …) on the footprint or directly at it belong to it (the user's request)
        var parts = PrefabParts.Attached(world, p);
        string text = Loc.F("Delete \"{0}\"?", Caption(p));
        if (parts.Count > 0) text += "\n\n" + Loc.F("{0} part(s) on it are deleted too: {1}", parts.Count, string.Join(", ", parts.Select(q => q.Name).Distinct()));
        text += "\n\n" + Loc.T("The ground adapts to the surroundings. Trees and plants are placed by the game in a new game. Undo with Ctrl+Z.");
        if (MessageBox.Show(prefabWindow.IsVisible ? prefabWindow : this, text, Loc.T("Delete building"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var removed = new List<PlacedPrefab> { p };
        removed.AddRange(parts);
        var w = world;
        double bumps = settings.EraserBumps;
        GroundShaper.ShapeInfo info = null;
        RunPrefabStep(Loc.F("Building {0} deleted", Caption(p)), Detail("gelöscht", p) + (parts.Count > 0 ? ", Teile: " + string.Join(";", parts.Select(q => q.Name)) : ""), e =>
        {
            foreach (var q in removed) e.RemovePrefab(q);
            if (ShapesGround(p)) info = GroundShaper.Naturalize(w, e, removed, bumps);
        }, null, () => ShapeHint(null, info, true));
    }

    // hint after changing the terrain: what was left out or adjusted (the user's request)
    static string ShapeHint(GroundShaper.ShapeInfo placed, GroundShaper.ShapeInfo freed, bool deleting, IEnumerable<PlacedPrefab> self = null)
    {
        var parts = new List<string>();
        var own = new HashSet<PlacedPrefab>(self ?? Array.Empty<PlacedPrefab>());
        // do not name tiles: a building on a lot always lies in its tile, whose ground stays anyway
        var spared = (placed?.Spared ?? Array.Empty<PlacedPrefab>()).Concat(freed?.Spared ?? Array.Empty<PlacedPrefab>()).Where(o => !own.Contains(o) && !o.IsTile).Distinct().ToList();
        if (spared.Count > 0) parts.Add(Loc.F("Left out at other buildings: {0}.", string.Join(", ", spared.Select(Caption).Distinct().Take(5))));
        if (placed?.RoadCells > 0) parts.Add(Loc.F("{0} m² of road left unchanged.", placed.RoadCells));
        if (freed?.RoadCells > 0) parts.Add(Loc.F("{0} m² of road adapted smoothly.", freed.RoadCells));
        return string.Join(" ", parts);
    }

    void RunPrefabStep(string title, string detail, Action<WorldEdits> op, PlacedPrefab show, Func<string> hint = null)
    {
        if (Map.Busy) return;   // a background step is already running: do not change the same data in parallel (audit finding F19)
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var step = edits.Run(title, detail, op);
            string more = hint?.Invoke()?.Trim();
            if (!string.IsNullOrEmpty(more)) step.Note = more;   // second line in the edit order
            Refresh(step);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("Changing the building failed:") + "\n" + ex.Message + "\n\n" + Loc.T("The map is unchanged."),
                Loc.T("Building"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Mouse.OverrideCursor = null; }
        // window stays open: after placing/changing with this copy, after deleting without a selection (list updated)
        if (show != null && world.Prefabs.Contains(show))
        {
            detailPrefab = show;
            windowEntry = EntryFor(show);
        }
        else
        {
            if (detailPrefab != null && Map.Selected == detailPrefab) Map.Selected = null;
            detailPrefab = null;
        }
        UpdateButtons();
        UpdateUndo();   // calls UpdatePrefabEdit: list, entrances/exits, window "Building"
    }

    /// after every change (also undo/redo): building list, entrances/exits and detail window to the new state
    void UpdatePrefabEdit()
    {
        if (world == null) { HideDetails(); return; }
        Map.RebuildExits();
        RefreshPrefabEntries();
        if (windowEntry == null || !prefabWindow.IsVisible) return;
        if (detailPrefab != null && !world.Prefabs.Contains(detailPrefab))
        {
            if (Map.Selected == detailPrefab) Map.Selected = null;
            detailPrefab = null;
        }
        ShowBuildingWindow(EntryByName(windowEntry.Name) ?? windowEntry, detailPrefab);
    }

    void HideDetails()
    {
        if (detailPrefab != null && Map.Selected == detailPrefab) Map.Selected = null;   // no blue marking on an empty place
        detailPrefab = null;
        windowEntry = null;
        if (!prefabWindow.IsVisible) return;
        // if the detail window was active, Windows otherwise brings some other program to the front
        if (prefabWindow.IsActive) Activate();
        prefabWindow.Hide();
    }

    // recount the number of copies per building (new names are added, deleted ones disappear from "on the map")
    void RefreshPrefabEntries()
    {
        var groups = world.Prefabs.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, PrefabEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var en in (gameEntries ?? new List<PrefabEntry>()).Concat(mapEntries)) byName[en.Name] = en;
        foreach (var en in byName.Values) en.Instances = groups.GetValueOrDefault(en.Name) ?? new List<PlacedPrefab>();
        foreach (var (name, list) in groups)
        {
            if (byName.ContainsKey(name)) continue;
            var en = new PrefabEntry { Name = list[0].Name, Label = list[0].Def?.Label, Category = PrefabCategories.Of(name), File = prefabFiles?.GetValueOrDefault(name), Instances = list, Tier = list[0].Def?.Tier ?? 0 };
            byName[name] = en;
            gameEntries?.Add(en);
        }
        mapEntries = byName.Values.Where(en => en.Instances.Count > 0).ToList();
        UpdateCategoryButtons();
        RefreshPrefabList();
    }

    PrefabEntry EntryByName(string name) =>
        (gameEntries ?? new List<PrefabEntry>()).Concat(mapEntries).FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // entry for a building on the map (if there is none, a provisional one)
    PrefabEntry EntryFor(PlacedPrefab p) => EntryByName(p.Name) ?? new PrefabEntry
    {
        Name = p.Name, Label = p.Def?.Label, Category = PrefabCategories.Of(p.Name), File = prefabFiles?.GetValueOrDefault(p.Name),
        Instances = world.Prefabs.Where(q => q.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).ToList(), Tier = p.Def?.Tier ?? 0,
    };

    // ---------- Detail window ----------

    /// show or refresh the window "Building" for a building on the map (this copy chosen and marked blue)
    void ShowPrefabDetails(PlacedPrefab p)
    {
        if (p == null || world == null) return;
        Map.Selected = p;
        ShowBuildingWindow(EntryFor(p), p);
    }

    /// window "Building" (the user's request): on top the building type with image, data and the list of its copies (+ Place,
    /// trash can), below position, build height and rotation of the chosen copy (locked without a selection)
    void ShowBuildingWindow(PrefabEntry entry, PlacedPrefab copy)
    {
        if (entry == null || world == null) return;
        ClearPendingMove();   // new display = previous position; a dragged move lapses
        windowEntry = entry;
        detailPrefab = copy;
        var win = prefabWindow;
        var def = copy?.Def ?? DescribeEntry(entry);
        win.TitleText.Text = string.IsNullOrEmpty(entry.Label) ? entry.Name : entry.Label;
        var info = new List<string> { entry.Name, Loc.T(entry.Category.Title) };
        if (def != null && def.Found)
        {
            info.Add(Loc.F("{0} × {1} m, {2} m high", def.SizeX, def.SizeZ, def.SizeY));
            if (def.Tier > 0) info.Add(Loc.F("Tier {0}", def.Tier));
            if (def.Exits.Count > 0) info.Add(Loc.F("{0} entrance(s)", def.Exits.Count));
            if (!string.IsNullOrEmpty(entry.ExitsText)) info.Add(Loc.F("Road connections: {0}", entry.ExitsText));
        }
        win.InfoText.Text = string.Join("  ·  ", info);
        FillCopyList(entry, copy);

        string file = entry.File ?? prefabFiles?.GetValueOrDefault(entry.Name);
        string jpg = file == null ? null : Path.ChangeExtension(file, ".jpg");
        win.PreviewImage.Source = null;
        win.NoImageText.Visibility = Visibility.Visible;
        if (jpg != null && File.Exists(jpg))
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(jpg);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                win.PreviewImage.Source = img;
                // always in original size (100 %, the user's request 2026-10-31), smaller only if the window is too narrow
                win.PreviewImage.MaxWidth = img.PixelWidth;
                win.PreviewImage.MaxHeight = img.PixelHeight;
                win.NoImageText.Visibility = Visibility.Collapsed;
            }
            catch (Exception) { }
        }

        var p = copy;
        win.SelectHint.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
        win.XBox.Text = p?.X.ToString(Num) ?? "";
        win.ZBox.Text = p?.Z.ToString(Num) ?? "";
        win.GroundBox.Text = p?.GroundHeight.ToString("0.0", Num) ?? "";
        foreach (var box in new[] { win.XBox, win.ZBox, win.GroundBox }) box.ClearValue(Control.BorderBrushProperty);
        win.YText.Text = p == null ? "" : Loc.F("y in prefabs.xml: {0} (YOffset {1})", p.Y, p.Def?.YOffset ?? 0);
        if (p != null)
        {
            var t = TerrainUnder(p);
            win.TerrainText.Text = Loc.F("Terrain under it: average {0:0.0} m, {1:0.0} … {2:0.0} m", t.Mean, t.Min, t.Max);
        }
        else win.TerrainText.Text = "";
        foreach (var (b, r) in new[] { (win.Rot0, 0), (win.Rot1, 1), (win.Rot2, 2), (win.Rot3, 3) }) b.IsChecked = p?.Rotation == r;

        bool editable = p != null;
        foreach (UIElement c in new UIElement[] { win.XBox, win.ZBox, win.GroundBox, win.FromTerrainButton, win.Rot0, win.Rot1, win.Rot2, win.Rot3 })
            c.IsEnabled = editable;
        Map.Movable = editable ? p : null;   // move icon on the marked copy (the user's request)
        ShowLot(p);
        var warn = new List<string>();
        // the tile of its own lot is no overlap
        var hits = editable && !p.IsTile ? Overlapping(p, p).Where(o => o != detailLot?.Tile).ToList() : new List<PlacedPrefab>();
        if (hits.Count > 0) warn.Add(Loc.F("Overlaps: {0}", string.Join(", ", hits.Select(Caption).Distinct().Take(5))));
        win.WarnText.Text = string.Join("\n", warn);
        win.WarnText.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!win.IsVisible)
        {
            win.Left = Left + ActualWidth - RightPanel.ActualWidth - win.Width - 30;
            win.Top = Top + Math.Max(60, (ActualHeight - win.Height) / 2);
            win.Show();
        }
    }

    // list of the copies in the window "Building": position (southwest corner), rotation, trash can
    void FillCopyList(PrefabEntry entry, PlacedPrefab selected)
    {
        var win = prefabWindow;
        win.InstanceText.Text = entry.Instances.Count == 0 ? Loc.T("not on this map") : Loc.F("{0} on the map", entry.Instances.Count);
        win.PlaceCopyButton.IsEnabled = entry.CanPlace;
        win.TileGridCheck.Visibility = entry.Category.Key == "tile" ? Visibility.Visible : Visibility.Collapsed;
        fillingCopies = true;
        try
        {
            win.CopyList.Items.Clear();
            win.CopyList.Visibility = entry.Instances.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var p in entry.Instances.OrderBy(q => q.X).ThenBy(q => q.Z))
            {
                var row = new DockPanel();
                var del = new Button { Content = Icons.Trash, FontFamily = IconUi.Font, Padding = new Thickness(4, 2, 4, 2),
                                       Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = Loc.T("Delete this copy"), Tag = p,
                                       };
                del.Click += (s, ev) => { ev.Handled = true; DeletePrefab((PlacedPrefab)((Button)s).Tag); };
                DockPanel.SetDock(del, Dock.Right);
                row.Children.Add(del);
                var rot = new TextBlock { Text = Loc.F("rotation {0}", (p.Rotation * 90) + "°"), Foreground = ThemeManager.Brush("SubtleTextBrush", System.Windows.Media.Brushes.DimGray), Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(rot, Dock.Right);
                row.Children.Add(rot);
                row.Children.Add(new TextBlock { Text = string.Format(Num, "x {0}   z {1}", p.X, p.Z), VerticalAlignment = VerticalAlignment.Center });
                var item = new ListBoxItem { Content = row, Tag = p, Padding = new Thickness(4, 1, 2, 1) };
                win.CopyList.Items.Add(item);
                if (p == selected) { item.IsSelected = true; win.CopyList.ScrollIntoView(item); }
            }
        }
        finally { fillingCopies = false; }
    }

    (double Mean, double Min, double Max) TerrainUnder(PlacedPrefab p)
    {
        double sum = 0, min = double.MaxValue, max = double.MinValue;
        int n = 0;
        for (int z = p.Z; z < p.Z + p.SizeZ; z++)
            for (int x = p.X; x < p.X + p.SizeX; x++)
            {
                double h = world.HeightAt(x, z);
                sum += h; n++;
                min = Math.Min(min, h); max = Math.Max(max, h);
            }
        return n == 0 ? (0, 0, 0) : (sum / n, min, max);
    }

    // take over the typed position or build height (Enter)
    void ApplyDetailValues()
    {
        var p = detailPrefab;
        if (p == null) return;
        var win = prefabWindow;
        ClearPendingMove();   // Enter takes over the values in the fields (also a dragged position)
        bool ok = true;
        int ReadInt(TextBox box, int current)
        {
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, Num, out int v) && Math.Abs(v) < world.Half) { box.ClearValue(Control.BorderBrushProperty); return v; }
            box.BorderBrush = System.Windows.Media.Brushes.Red; ok = false; return current;
        }
        int x = ReadInt(win.XBox, p.X), z = ReadInt(win.ZBox, p.Z);
        if (ok && p.IsTile && settings.TileGrid) { x = world.SnapTile(x, false); z = world.SnapTile(z, true); }   // tiles in the 150 m grid if the switch is on
        // the whole building must lie on the map (otherwise it stands half outside in prefabs.xml; audit finding F23)
        if (ok && (x < -world.Half || x + p.SizeX > world.Half)) { win.XBox.BorderBrush = System.Windows.Media.Brushes.Red; ok = false; }
        if (ok && (z < -world.Half || z + p.SizeZ > world.Half)) { win.ZBox.BorderBrush = System.Windows.Media.Brushes.Red; ok = false; }
        double ground = p.GroundHeight;
        if (double.TryParse(win.GroundBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, Num, out double gv) && gv >= 0 && gv < 255) { ground = gv; win.GroundBox.ClearValue(Control.BorderBrushProperty); }
        else { win.GroundBox.BorderBrush = System.Windows.Media.Brushes.Red; ok = false; }
        if (!ok) return;
        int y = (int)Math.Round(ground + 0.85) + (p.Def?.YOffset ?? 0);
        MoveTo(p, x, y, z, p.Rotation);
        if (p.X == x && p.Y == y && p.Z == z) ShowBuildingWindow(windowEntry ?? EntryFor(p), p);   // smooth the display even without a change (e.g. 42.4 → 42.2)
    }

    // rotation via the buttons 0°/90°/180°/270°; the center stays
    void ApplyRotation(int rotation)
    {
        var p = detailPrefab;
        if (p == null) return;
        if (p.Rotation == rotation) { ShowBuildingWindow(windowEntry ?? EntryFor(p), p); return; }
        var turned = new PlacedPrefab { Name = p.Name, Def = p.Def, Rotation = rotation };
        int x = (int)Math.Round(p.X + p.SizeX / 2.0 - turned.SizeX / 2.0), z = (int)Math.Round(p.Z + p.SizeZ / 2.0 - turned.SizeZ / 2.0);
        x = Math.Clamp(x, -world.Half, world.Half - turned.SizeX);   // rotated building at the map edge stays on the map
        z = Math.Clamp(z, -world.Half, world.Half - turned.SizeZ);
        MoveTo(p, x, p.Y, z, rotation);
    }
}
