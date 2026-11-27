using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MapEditor.Map;

namespace MapEditor;

/// Lakes and water (the user's request, 2026-11-05; basics in WASSER_ANALYSE.md): the user draws an area as with the mountain range
/// (drag, insert, delete points), then "Apply". "Lake" lowers the ground to a basin, lets the shore run out softly into the
/// terrain and sets the water pixels in splat4.png (blue value = level). "Remove water" deletes the pixels in the area and
/// fills the lake bed naturally. Calculation in Map/Lake.cs; roads, buildings and tiles stay untouched.
public partial class MainWindow
{
    int waterKind;   // 0 lake, 1 remove water, 2 river, 3 fill craters, 4 connect lakes (river between two lakes, Map/River.cs), 5 bridge (Map/Bridge.cs)
    double waterWidth = 12, waterIrregular = 10;
    double waterCraterHeight = 6;
    int waterBiome = -1;   // biome under new water, -1 = stays
    static string HelpCrater => Loc.T("Fill craters: drag the brush through the craters with the left button · drag right = move the map · mouse wheel = zoom · Esc = cancel");
    /// A river is an open line (at least 2 points), lake/mountains a polygon (3)
    int MinAreaPoints => OpenLine ? 2 : 3;
    /// the area is an open line: course of a river (drawn or between two lakes)
    bool OpenLine => waterMode && (waterKind == 2 || waterKind == 4 || waterKind == 5);
    bool RiverPathMode => waterMode && waterKind == 2;

    /// tool "River" (2026-11-26, the user's request: the water tools should profit from the rivers of the world generator): the drawn line
    /// becomes a river valley like the rivers of the generator and "Connect lakes" (Map/River.cs BuildDrawn): varied banks, depth changing
    /// along the river, pools in the outer bends. Before, the line became a bank polygon that was built like a lake.
    async Task BuildDrawnRiver()
    {
        if (world == null || edits == null || Map.Area.Count < 2) return;
        WaterValue_Changed(null, null);
        var pts = Map.Area.ToList();
        int level = (int)waterLevel, adopted = 0;
        // touches existing water: its level applies (otherwise steps in the water)
        if (Lake.ExistingLevel(world, Lake.Corridor(pts, waterWidth, 1)) is int old && old != level) { adopted = old; level = old; waterLevel = old; WaterLevelBox.Text = Fmt(old); }
        double v = Math.Clamp(waterIrregular / 100, 0, 0.5);
        var spec = new RiverSpec
        {
            Line = pts, WidthMin = Math.Max(3, waterWidth * (1 - v)), WidthMax = Math.Max(3, waterWidth * (1 + v)), Depth = Math.Max(0.5, waterWDepth),
            Seed = Random.Shared.Next(1, 1_000_000), Biome = waterBiome,
        };
        double cx = pts.Average(p => p.X), cz = pts.Average(p => p.Y);
        string title = Loc.F("River at {0:0}, {1:0}", cx, cz);
        string detail = string.Format(CultureInfo.InvariantCulture, "Fluss bei {0:0},{1:0}: {2} Punkte, Breite {3:0.#}–{4:0.#} m, Tiefe {5} m, Spiegel {6} m, Startwert {7}",
            cx, cz, pts.Count, spec.WidthMin, spec.WidthMax, spec.Depth, level, spec.Seed);
        var w = world;
        SetBusyTitle(Loc.T("Digging river …"));
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            RiverStats st = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var s = River.BuildDrawn(w, e, spec, level);
                st ??= s;
            }));
            if (st == null || st.Water == 0)
            {
                MessageBox.Show(this, Loc.T("Nothing changed: the area lies only on roads, buildings or city tiles, which stay untouched."), Loc.T("Water"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Map.ClearArea();
            step.Note = Loc.F("River {0:0} m long, {1:#,0} m² of water at {2} m above sea level.", st.Length, st.Water, st.Level) + " " +
                        Loc.F("{0:#,0} height points changed.", st.Changed);
            if (st.MaxCut > 12) step.Note += " ⚠ " + Loc.F("The valley cuts up to {0:0} m deep into the land. A lower course or a higher water level needs less digging.", st.MaxCut);
            if (st.Embankment > 0) step.Note += " " + Loc.F("Where the land is lower than the bank, an embankment was built ({0:#,0} m²).", st.Embankment);
            if (adopted != 0) step.Note += " " + Loc.F("The existing water here stands at {0} m: the new water takes this level so that there are no steps.", adopted);
            if (st.Spared > 0) step.Note += " " + Loc.F("{0:#,0} m² on roads, buildings or tiles were left alone.", st.Spared);
            Refresh(step);
            Recompute();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex))
        {
            MessageBox.Show(this, Loc.T("Changing the water failed:") + "\n" + ex.Message, Loc.T("Water"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Map.Busy = false;
            Cursor = null;
            UpdateButtons();
            UpdateUndo();
            UpdateToolModule();
        }
    }
    double waterLevel = 30, waterDepth = 6, waterWDepth = 4, waterShore = 14;
    bool loadingWater;

    static string HelpWater => Loc.T("Lake: draw an area with the left button held, change its points, then Apply or Enter · drag right = move the map · Esc = discard the area · Esc again = cancel");

    void ShowWaterValues()
    {
        loadingWater = true;
        try
        {
            waterKind = Math.Clamp(settings.WaterKind, 0, 5);
            waterBiome = settings.WaterBiome;
            WaterBiomeBox.SelectedIndex = Math.Clamp(waterBiome + 1, 0, 5);
            waterCraterHeight = settings.WaterCraterHeight;
            WaterCraterHeightBox.Text = Fmt(waterCraterHeight);
            waterWidth = settings.WaterWidth;
            waterIrregular = settings.WaterIrregular;
            WaterIrregBox.Text = Fmt(waterIrregular);
            WaterWidthBox.Text = Fmt(waterWidth);
            waterLevel = settings.WaterLevel; waterDepth = settings.WaterDepth; waterShore = settings.WaterShore;
            WaterLevelBox.Text = Fmt(waterLevel);
            WaterDepthBox.Text = Fmt(waterDepth);
            waterWDepth = settings.WaterWDepth;
            WaterWDepthBox.Text = Fmt(waterWDepth);
            WaterShoreBox.Text = Fmt(waterShore);
            WaterBumpsBox.SelectedIndex = Math.Clamp(settings.WaterBumps, 0, 3);
            WaterShoreStyleBox.SelectedIndex = Math.Clamp(settings.WaterShoreStyle, 0, 2);
            WaterBedBox.SelectedIndex = Math.Clamp(settings.WaterBedFeatures, 0, 2);
            WaterIslandCheck.IsChecked = settings.WaterIsland;
            ShowRiverValues();
        }
        finally { loadingWater = false; }
    }

    void WaterTool_Click(object sender, RoutedEventArgs e)
    {
        if (WaterTool.IsChecked != true) { ToolCancel_Click(sender, e); return; }
        EndSpawnMode();
        EndGhost();
        SetEraseMode(true, BrushTool.Water);
    }

    // menu buttons Lake / Remove water (tag = kind), like with the mountain ranges
    async void WaterKind_Click(object sender, RoutedEventArgs e)
    {
        int kind = int.Parse((string)((FrameworkElement)sender).Tag);
        bool active = WaterTool.IsChecked == true;
        if (active && waterKind == kind)   // second click on the same kind: tool off, a drawn area is discarded
        {
            Map.ClearArea();
            WaterTool.IsChecked = false;
            WaterTool_Click(WaterTool, e);
            return;
        }
        if (!active && !await ConfirmSwitch(null)) { UpdateRibbon(); return; }
        if (kind != waterKind && (kind is 2 or 3 or 4 or 5 || waterKind is 2 or 3 or 4 or 5)) Map.ClearArea();   // line, polygon, brush and connecting do not fit together
        waterKind = kind;
        ApplyBrush();
        Map.OpenArea = active && (kind == 2 || kind == 4 || kind == 5);
        settings.WaterKind = kind;
        settings.Save();
        if (!active) { WaterTool.IsChecked = true; WaterTool_Click(WaterTool, e); }
        else { UpdateToolModule(); UpdateRibbon(); }
    }

    void WaterBiomeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingWater || WaterBiomeBox.SelectedIndex < 0) return;
        waterBiome = WaterBiomeBox.SelectedIndex - 1;
        if (settings.WaterBiome == waterBiome) return;
        settings.WaterBiome = waterBiome;
        settings.Save();
    }

    void WaterBumpsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingWater || WaterBumpsBox.SelectedIndex < 0 || settings.WaterBumps == WaterBumpsBox.SelectedIndex) return;
        settings.WaterBumps = WaterBumpsBox.SelectedIndex;
        settings.Save();
    }

    void WaterValue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) WaterValue_Changed(sender, e);
    }

    void WaterValue_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingWater) return;
        waterLevel = Math.Round(ReadBox(WaterLevelBox, LakeSpec.MinLevel, LakeSpec.MaxLevel, waterLevel));
        waterDepth = ReadBox(WaterDepthBox, 0.5, 60, waterDepth);
        waterWDepth = ReadBox(WaterWDepthBox, 0.6, 60, waterWDepth);
        waterWidth = ReadBox(WaterWidthBox, 3, 150, waterWidth);
        waterIrregular = ReadBox(WaterIrregBox, 0, 50, waterIrregular);
        waterCraterHeight = ReadBox(WaterCraterHeightBox, 0.1, 200, waterCraterHeight);
        ReadRiverValues(sender != null);
        Map.AreaWidth = AreaWidthNow;
        Map.InvalidateVisual();
        waterShore = ReadBox(WaterShoreBox, 2, 120, waterShore);
        UpdateLakePreview();
        if (sender == null) return;
        if (settings.WaterLevel == waterLevel && settings.WaterDepth == waterDepth && settings.WaterWDepth == waterWDepth && settings.WaterWidth == waterWidth && settings.WaterIrregular == waterIrregular && settings.WaterShore == waterShore && settings.WaterCraterHeight == waterCraterHeight) return;
        settings.WaterLevel = waterLevel; settings.WaterDepth = waterDepth; settings.WaterWDepth = waterWDepth; settings.WaterWidth = waterWidth; settings.WaterIrregular = waterIrregular; settings.WaterShore = waterShore; settings.WaterCraterHeight = waterCraterHeight;
        settings.Save();
    }

    /// Fill crater: every point of the stroke (about every 4 m) is a start point; one stroke is one edit step.
    /// Beforehand it is measured how big the area would become; if it is very large, the user decides.
    async void FillCraterStroke(List<Point> stroke)
    {
        if (world == null || edits == null) { Map.ClearStroke(); return; }
        WaterValue_Changed(null, null);
        var seeds = new List<Point>();
        foreach (var p in stroke)
            if (seeds.Count == 0 || (p - seeds[^1]).Length >= 4) seeds.Add(p);
        if (seeds.Count > 400) seeds = seeds.Where((_, i) => i % ((seeds.Count + 399) / 400) == 0).ToList();
        double height = waterCraterHeight;
        int biome = waterBiome;
        double cx = (stroke.Min(p => p.X) + stroke.Max(p => p.X)) / 2, cz = (stroke.Min(p => p.Y) + stroke.Max(p => p.Y)) / 2;
        string title = Loc.F("Craters filled at {0:0}, {1:0}", cx, cz);
        string detail = string.Format(CultureInfo.InvariantCulture, "Krater füllen bei {0:0},{1:0}: {2} Startpunkte, Wasserhöhe {3} m", cx, cz, seeds.Count, height);
        var w = world;
        SetBusyTitle(Loc.T("Filling craters with water …"));
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            var plan = await Task.Run(() => Lake.PlanCraters(w, seeds, height));
            if (plan.TooHuge)
            {
                Map.ClearStroke();
                MessageBox.Show(this, Loc.T("With this water height the water would cover far too large an area (more than 20 km²). Please choose a smaller height."),
                    Loc.T("Water"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (plan.Total == 0) { Map.ClearStroke(); return; }   // already water or nothing underneath
            if (plan.Total > 500_000 && MessageBox.Show(this,
                    Loc.F("With this water height the water would cover about {0:0.0} km². Fill it anyway?", plan.Total / 1e6),
                    Loc.T("Water"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                Map.ClearStroke();
                return;
            }
            CraterStats st = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var s = Lake.FillCraters(w, e, seeds, height, biome);
                st ??= s;
            }));
            Map.ClearStroke();
            if (st == null || st.Water == 0) return;   // no step
            step.Note = Loc.F("{0} crater(s) filled, {1:#,0} m² of water, level {2} to {3} m.", st.Craters, st.Water, st.MinLevel, st.MaxLevel);
            Refresh(step);
            Recompute();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex))
        {
            Map.ClearStroke();
            MessageBox.Show(this, Loc.T("Changing the water failed:") + "\n" + ex.Message, Loc.T("Water"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Map.Busy = false;
            Cursor = null;
            UpdateButtons();
            UpdateUndo();
            UpdateToolModule();
        }
    }

    async Task PlaceWater(List<Point> stroke)
    {
        if (world == null || edits == null) { Map.ClearArea(); return; }
        WaterValue_Changed(null, null);
        int seed = Random.Shared.Next(1, 1_000_000);
        var outline = RangeSpec.PrepareOutline(stroke, true, 1);   // only round off slightly, stays angular
        bool dry = waterKind == 1;
        double minArea = dry ? 100 : LakeSpec.MinArea;
        if (outline.Count < 3 || RangeSpec.Area(outline) < minArea)
        {
            Msg.Info(this, settings, "water-1", Loc.F("The area is too small. Draw a larger one (at least about {0} m²).", minArea), Loc.T("Water"));
            return;
        }
        var spec = new LakeSpec { Level = (int)waterLevel, Depth = waterWDepth, Shore = waterShore, ShoreStyle = WaterShoreStyleBox.SelectedIndex, BedFeatures = WaterBedBox.SelectedIndex, Island = WaterIslandCheck.IsChecked == true, Bumps = WaterBumpsBox.SelectedIndex,
            Seed = dry ? seed : lakeSeed, Outline = outline, Biome = waterBiome };
        // extend/connect: if there is water in the area or directly beside it, its level applies (otherwise steps in the water)
        int adopted = 0;
        if (!dry && Lake.ExistingLevel(world, outline) is int old && old != spec.Level) { adopted = old; spec.Level = old; waterLevel = old; WaterLevelBox.Text = Fmt(old); }
        if (!dry)
        {
            // the water would run out over lower land: only with an embankment (the user decides)
            var wq = world;
            var probe = await Task.Run(() => Lake.Plan(wq, spec));
            if (probe.Leak > 0)
            {
                var answer = MessageBox.Show(this, Loc.F("At {0} m the water would run out of the lake (red on the map, {1:#,0} m²).", spec.Level, probe.Leak) + "\n\n" +
                    Loc.T("Build a low, wide embankment there?\nYes = build it, No = back (lower the level or change the outline)."),
                    Loc.T("Water"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
                spec.Dam = true;
            }
        }
        double x0 = outline.Min(p => p.X), x1 = outline.Max(p => p.X), z0 = outline.Min(p => p.Y), z1 = outline.Max(p => p.Y);
        double area = RangeSpec.Area(outline);
        string title = Loc.F(dry ? "Water removed at {0:0}, {1:0}" : "Lake at {0:0}, {1:0}", (x0 + x1) / 2, (z0 + z1) / 2);
        string detail = string.Format(CultureInfo.InvariantCulture, dry
            ? "Wasser entfernen bei {0:0},{1:0}: Fläche {2:0} m², Unebenheit {3}"
            : "See bei {0:0},{1:0}: Fläche {2:0} m², Spiegel {4} m, Tiefe {5} m, Ufer {6} m (Art {8}), Unebenheit {3}, Damm {9}, Startwert {7}",
            (x0 + x1) / 2, (z0 + z1) / 2, area, spec.Bumps, spec.Level, spec.Depth, spec.Shore, spec.Seed, spec.ShoreStyle, spec.Dam ? "ja" : "nein");
        var w = world;
        SetBusyTitle(dry ? Loc.T("Removing water …") : Loc.T("Placing lake …"));
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            LakeStats st = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var s = dry ? Lake.Remove(w, e, outline, spec.Bumps) : Lake.Place(w, e, spec);
                st ??= s;   // result of the first pass (later ones: repeat/recalculate)
            }));
            if (st == null || (st.Water == 0 && st.Changed == 0))
            {
                MessageBox.Show(this, dry ? Loc.T("There is no water in this area.") : Loc.T("Nothing changed: the area lies only on roads, buildings or city tiles, which stay untouched."),
                    Loc.T("Water"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;   // no step: the area stays for editing
            }
            Map.ClearArea();
            if (dry)
                step.Note = Loc.F("{0:#,0} m² of water removed. The ground stays as it is.", st.Water);
            else
            {
                step.Note = Loc.F("Lake with {0:#,0} m² at {1} m above sea level, up to {2:0.0} m deep.", st.Water, spec.Level, st.MaxDepth) + " " +
                    Loc.F("{0:#,0} height points changed.", st.Changed);
                if (spec.Dam)
                    step.Note += " " + Loc.F("Embankment up to {0:0.0} m high.", st.MaxRaise);
                if (st.Cliffs + st.Cracks + st.Rocks > 0)
                    step.Note += " " + Loc.F("On the bed: {0} steep walls, {1} cracks, {2} rock groups.", st.Cliffs, st.Cracks, st.Rocks);
                if (st.Island)
                    step.Note += " " + Loc.T("With an island.");
                if (st.Spread > 50)
                    step.Note += " " + Loc.F("The water follows lower land {0:#,0} m² beyond the outline.", st.Spread);
            }
            if (adopted != 0)
                step.Note += " " + Loc.F("The existing water here stands at {0} m: the new water takes this level so that there are no steps.", adopted);
            if (st.Spared > 0)
                step.Note += " " + Loc.F("{0:#,0} m² on roads, buildings or tiles were left alone.", st.Spared);
            Refresh(step);
            Recompute();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex))
        {
            MessageBox.Show(this, Loc.T("Changing the water failed:") + "\n" + ex.Message, Loc.T("Water"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Map.Busy = false;
            Cursor = null;
            UpdateButtons();
            UpdateUndo();
            UpdateToolModule();
        }
    }
}

public partial class MainWindow
{
    /// calculate the drawn area (mountain range or lake, depending on the tool)
    Task PlaceArea() => rangeMode ? PlaceRange(Map.Area.ToList()) : RiverPathMode ? BuildDrawnRiver() : waterMode && waterKind == 4 ? ConnectLakes() : PlaceWater(Map.Area.ToList());
}

public partial class MainWindow
{
    int lakeSeed = Random.Shared.Next(1, 1_000_000);   // the same shape in the preview and when applying; new with every new area
    string lakePreviewKey;
    int lakePreviewVersion;
    bool lakePicking, loadingSlider;
    bool LakeMode => waterMode && waterKind == 0 && Map.EraseMode;

    /// live preview of the lake tool (2026-11-26): blue where the water will stand, red where it would run out; the note says how much
    async void UpdateLakePreview()
    {
        if (!LakeMode && lakePicking) { lakePicking = false; Map.PickOnce = false; Map.Cursor = null; }
        if (!LakeMode || world == null || Map.Area.Count < 3 || Map.Busy)
        {
            if (Map.Area.Count == 0) lakeSeed = Random.Shared.Next(1, 1_000_000);
            if (Map.WaterPreview != null) Map.WaterPreview = null;
            lakePreviewKey = null;
            if (LakeNote.Text.Length > 0) { LakeNote.Text = ""; LakeNote.Visibility = Visibility.Collapsed; }
            return;
        }
        var outline = RangeSpec.PrepareOutline(Map.Area.ToList(), true, 1);
        if (outline.Count < 3 || RangeSpec.Area(outline) < LakeSpec.MinArea) { Map.WaterPreview = null; lakePreviewKey = null; return; }
        var spec = new LakeSpec { Level = (int)waterLevel, Depth = waterWDepth, Shore = waterShore, ShoreStyle = WaterShoreStyleBox.SelectedIndex, BedFeatures = WaterBedBox.SelectedIndex, Island = WaterIslandCheck.IsChecked == true, Bumps = WaterBumpsBox.SelectedIndex, Seed = lakeSeed, Outline = outline };
        string shape = string.Join(";", outline.Select(p => string.Format(CultureInfo.InvariantCulture, "{0:0.#},{1:0.#}", p.X, p.Y)));
        string key = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}", shape, spec.Level, spec.Depth, spec.Shore, spec.ShoreStyle, spec.Bumps, spec.Seed, spec.BedFeatures, spec.Island);
        if (key == lakePreviewKey) return;
        bool newShape = lakePreviewKey == null || !lakePreviewKey.StartsWith(shape + "|", StringComparison.Ordinal);
        lakePreviewKey = key;
        int version = ++lakePreviewVersion;
        var w = world;
        LakePlan plan;
        int? existing;
        double low = 0, high = 0;
        try
        {
            (plan, existing, low, high) = await Task.Run(() =>
            {
                var ex = Lake.ExistingLevel(w, outline);
                if (ex is int l) spec.Level = l;
                // height range along the outline (for the slider)
                double lo = double.MaxValue, hi = double.MinValue;
                for (int k = 0; k < outline.Count; k++)
                {
                    var a = outline[k]; var b = outline[(k + 1) % outline.Count];
                    int steps = Math.Max(1, (int)((b - a).Length / 4));
                    for (int s = 0; s < steps; s++)
                    {
                        var p = a + (b - a) * ((double)s / steps);
                        if (!w.Contains((int)Math.Floor(p.X), (int)Math.Floor(p.Y))) continue;
                        double h = w.HeightAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
                        lo = Math.Min(lo, h); hi = Math.Max(hi, h);
                    }
                }
                return (Lake.Plan(w, spec), ex, lo, hi);
            });
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { return; }
        // back on the UI thread (without a synchronization context the continuation would run on the thread pool)
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => ShowLakePreview(version, plan, spec, existing, low, high, newShape)); return; }
        ShowLakePreview(version, plan, spec, existing, low, high, newShape);
    }

    void ShowLakePreview(int version, LakePlan plan, LakeSpec spec, int? existing, double low, double high, bool newShape)
    {
        if (version != lakePreviewVersion || !LakeMode || Map.Area.Count < 3) return;
        Map.WaterPreview = plan;
        if (newShape && low < double.MaxValue)
        {
            loadingSlider = true;
            WaterLevelSlider.Minimum = Math.Max(LakeSpec.MinLevel, Math.Min(waterLevel, Math.Floor(low) - 20));
            WaterLevelSlider.Maximum = Math.Min(LakeSpec.MaxLevel, Math.Max(waterLevel, Math.Ceiling(high) + 5));
            loadingSlider = false;
        }
        loadingSlider = true;
        WaterLevelSlider.Value = Math.Clamp(spec.Level, WaterLevelSlider.Minimum, WaterLevelSlider.Maximum);
        loadingSlider = false;
        string note = Loc.F("Water {0:#,0} m², up to {1:0.0} m deep.", plan.Water, plan.MaxDepth);
        if (existing is int lv && lv != (int)waterLevel) note += " " + Loc.F("Takes the level of the water beside it ({0} m).", lv);
        if (plan.Spread > 50) note += " " + Loc.F("The water follows lower land {0:#,0} m² beyond the outline.", plan.Spread);
        if (plan.Cliffs + plan.Cracks + plan.Rocks > 0) note += " " + Loc.F("On the bed: {0} steep walls, {1} cracks, {2} rock groups.", plan.Cliffs, plan.Cracks, plan.Rocks);
        if (plan.HasIsland) note += " " + Loc.T("With an island.");
        if (plan.IslandTooSmall) note += "\n" + Loc.T("The lake is too small for an island (it needs about 70 m from the shore to the middle).");
        if (plan.Leak > 0) note += "\n⚠ " + Loc.F("At {0} m the water runs out (red): choose a lower level, or apply with an embankment.", spec.Level);
        if (low < double.MaxValue && low - (spec.Level + Lake.WetMargin) > 3)
            note += "\n⚠ " + Loc.F("Even the lowest ground at the outline lies {0:0} m above the water: it is dug deep. From ground suggests a fitting level.", low - spec.Level - Lake.WetMargin);
        LakeNote.Text = note;
        LakeNote.Visibility = Visibility.Visible;
    }

    void WaterLevelSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || loadingSlider || loadingWater) return;
        WaterLevelBox.Text = Fmt(Math.Round(e.NewValue));
        WaterValue_Changed(sender, e);
    }

    void WaterShoreStyleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingWater || WaterShoreStyleBox.SelectedIndex < 0) return;
        if (settings.WaterShoreStyle != WaterShoreStyleBox.SelectedIndex) { settings.WaterShoreStyle = WaterShoreStyleBox.SelectedIndex; settings.Save(); }
        UpdateLakePreview();
    }

    /// lake bed shapes and island: saved at once, preview anew
    void WaterLakeOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || loadingWater || WaterBedBox.SelectedIndex < 0) return;
        bool island = WaterIslandCheck.IsChecked == true;
        if (settings.WaterBedFeatures != WaterBedBox.SelectedIndex || settings.WaterIsland != island)
        {
            settings.WaterBedFeatures = WaterBedBox.SelectedIndex;
            settings.WaterIsland = island;
            settings.Save();
        }
        UpdateLakePreview();
    }

    /// pipette: the next click into the map takes the water level from the ground there (the shore lies there)
    void WaterPick_Click(object sender, RoutedEventArgs e)
    {
        if (world == null || !LakeMode) return;
        lakePicking = true;
        Map.PickOnce = true;
        Map.Cursor = IconUi.EyedropperCursor;
        ToolHint.Text = Loc.T("Click into the map where the shore should be.");
    }

    void LakePicked(Point p)
    {
        if (!lakePicking) return;
        lakePicking = false;
        if (world == null) return;
        double h = world.HeightAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
        waterLevel = Math.Clamp(Math.Round(h - Lake.WetMargin), LakeSpec.MinLevel, LakeSpec.MaxLevel);
        WaterLevelBox.Text = Fmt(waterLevel);
        WaterValue_Changed(WaterLevelBox, null);
        UpdateToolModule();
    }

    /// level from the drawn area. Lake: suggestion of Lake.SuggestLevel (shore on the lower land around, water does not run out);
    /// river: lowest terrain height along the course (rounded down), then it needs no dam
    async void WaterFromGround_Click(object sender, RoutedEventArgs e)
    {
        if (world == null || Map.Area.Count < MinAreaPoints)
        {
            Msg.Info(this, settings, "water-2", Loc.T("Draw the area of the lake first."), Loc.T("Water"));
            return;
        }
        WaterValue_Changed(null, null);
        if (LakeMode)
        {
            var spec = new LakeSpec { Level = (int)waterLevel, Depth = waterWDepth, Shore = waterShore, ShoreStyle = WaterShoreStyleBox.SelectedIndex, BedFeatures = WaterBedBox.SelectedIndex, Island = WaterIslandCheck.IsChecked == true, Bumps = WaterBumpsBox.SelectedIndex,
                Seed = lakeSeed, Outline = RangeSpec.PrepareOutline(Map.Area.ToList(), true, 1) };
            var w = world;
            Cursor = Cursors.Wait;
            int suggested;
            try { suggested = await Task.Run(() => Lake.SuggestLevel(w, spec)); }
            finally { Dispatcher.Invoke(() => Cursor = null); }
            // back on the UI thread (without a synchronization context the continuation would run on the thread pool)
            Dispatcher.Invoke(() =>
            {
                waterLevel = suggested;
                WaterLevelBox.Text = Fmt(waterLevel);
                WaterValue_Changed(sender, e);
            });
            return;
        }
        var outline = RiverPathMode ? Lake.Corridor(Map.Area.ToList(), waterWidth, 1) : RangeSpec.PrepareOutline(Map.Area.ToList(), true, 1);
        // lowest terrain in the whole area plus 10 m all around (not only on the line: if the land drops there, the level would be too high)
        double low = double.MaxValue;
        const double Ring = 10;
        double x0 = outline.Min(p => p.X) - Ring, x1 = outline.Max(p => p.X) + Ring, z0 = outline.Min(p => p.Y) - Ring, z1 = outline.Max(p => p.Y) + Ring;
        for (double z = z0; z <= z1; z += 2)
            for (double x = x0; x <= x1; x += 2)
            {
                bool inside = false;
                double dist = double.MaxValue;
                for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
                {
                    var a = outline[j]; var b = outline[i];
                    if ((a.Y > z) != (b.Y > z) && x < (b.X - a.X) * (z - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                    double dx = b.X - a.X, dz = b.Y - a.Y, len2 = dx * dx + dz * dz;
                    double t = len2 > 0 ? Math.Clamp(((x - a.X) * dx + (z - a.Y) * dz) / len2, 0, 1) : 0;
                    dist = Math.Min(dist, Math.Sqrt(Math.Pow(x - a.X - t * dx, 2) + Math.Pow(z - a.Y - t * dz, 2)));
                }
                if (!inside && dist > Ring) continue;
                int hx = (int)Math.Floor(x), hz = (int)Math.Floor(z);
                if (Math.Abs(hx) >= world.Half - 2 || Math.Abs(hz) >= world.Half - 2) continue;
                low = Math.Min(low, world.HeightAt(hx, hz));
            }
        if (low == double.MaxValue) return;
        // depth (dug) minus water depth = so much the level lies below the lowest point of the outline
        waterLevel = Math.Clamp(Math.Floor(low - Math.Max(0, waterDepth - waterWDepth)), LakeSpec.MinLevel, LakeSpec.MaxLevel);
        WaterLevelBox.Text = Fmt(waterLevel);
        WaterValue_Changed(sender, e);
    }
}
