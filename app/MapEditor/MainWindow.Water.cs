using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MapEditor.Map;

namespace MapEditor;

/// Seen und Wasser (Wunsch des Nutzers 5.11.2026; Grundlagen in WASSER_ANALYSE.md): Der Nutzer zeichnet wie bei der Gebirgskette einen
/// Bereich (Punkte ziehen, einfügen, löschen), dann „Übernehmen“. „See“ senkt den Boden zu einem Becken ab, lässt das Ufer weich in das
/// Gelände auslaufen und setzt die Wasserpixel in splat4.png (Blauwert = Spiegel). „Wasser entfernen“ löscht die Pixel im Bereich und
/// füllt den Seeboden natürlich auf. Rechnung in Map/Lake.cs; Straßen, Gebäude und Kacheln bleiben unberührt.
public partial class MainWindow
{
    int waterKind;   // 0 See, 1 Wasser entfernen, 2 Fluss
    double waterWidth = 12, waterIrregular = 10;
    double waterCraterHeight = 6;
    int waterBiome = -1;   // Biom unter neuem Wasser, -1 = bleibt
    static string HelpCrater => Loc.T("Fill craters: drag the brush through the craters with the left button · drag right = move the map · mouse wheel = zoom · Esc = cancel");
    /// Ein Fluss ist eine offene Linie (mindestens 2 Punkte), See/Gebirge ein Vieleck (3)
    int MinAreaPoints => RiverPathMode ? 2 : 3;
    /// Der Fluss wird in zwei Schritten gebaut: erst der Pfad (Punkte wie bei der Straße), mit „Uferlinie erstellen“ wird er zum Vieleck, das wie ein See gebaut wird
    bool riverConverted;
    bool RiverPathMode => waterMode && waterKind == 2 && !riverConverted;

    /// Pfad in eine Uferlinie (Vieleck links und rechts) umwandeln; die Punkte lassen sich danach einzeln ziehen
    Task ConvertRiver()
    {
        if (Map.Area.Count < 2) return Task.CompletedTask;
        WaterValue_Changed(null, null);
        var poly = Lake.Corridor(Map.Area.ToList(), waterWidth, Random.Shared.Next(1, 1_000_000), Math.Max(6, waterWidth * 0.7), waterIrregular / 100);
        riverConverted = true;
        Map.OpenArea = false;
        Map.SetArea(poly);
        return Task.CompletedTask;
    }
    double waterLevel = 30, waterDepth = 6, waterWDepth = 4, waterShore = 14;
    bool loadingWater;

    static string HelpWater => Loc.T("Lake: draw an area with the left button held, change its points, then Apply or Enter · drag right = move the map · Esc = discard the area · Esc again = cancel");

    void ShowWaterValues()
    {
        loadingWater = true;
        try
        {
            waterKind = Math.Clamp(settings.WaterKind, 0, 3);
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

    // Menüknöpfe See / Wasser entfernen (Tag = Art), wie bei den Gebirgsketten
    async void WaterKind_Click(object sender, RoutedEventArgs e)
    {
        int kind = int.Parse((string)((FrameworkElement)sender).Tag);
        bool active = WaterTool.IsChecked == true;
        if (active && waterKind == kind)   // zweiter Klick auf dieselbe Art: Werkzeug aus, ein gezeichneter Bereich wird verworfen
        {
            Map.ClearArea();
            WaterTool.IsChecked = false;
            WaterTool_Click(WaterTool, e);
            return;
        }
        if (!active && !await ConfirmSwitch(null)) { UpdateRibbon(); return; }
        if ((kind == 2) != (waterKind == 2) || (kind == 3) != (waterKind == 3)) Map.ClearArea();   // Linie, Vieleck und Pinsel passen nicht ineinander
        waterKind = kind;
        ApplyBrush();
        Map.OpenArea = active && kind == 2;
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
        Map.AreaWidth = waterWidth;
        Map.InvalidateVisual();
        waterShore = ReadBox(WaterShoreBox, 2, 120, waterShore);
        if (sender == null) return;
        if (settings.WaterLevel == waterLevel && settings.WaterDepth == waterDepth && settings.WaterWDepth == waterWDepth && settings.WaterWidth == waterWidth && settings.WaterIrregular == waterIrregular && settings.WaterShore == waterShore && settings.WaterCraterHeight == waterCraterHeight) return;
        settings.WaterLevel = waterLevel; settings.WaterDepth = waterDepth; settings.WaterWDepth = waterWDepth; settings.WaterWidth = waterWidth; settings.WaterIrregular = waterIrregular; settings.WaterShore = waterShore; settings.WaterCraterHeight = waterCraterHeight;
        settings.Save();
    }

    /// Krater füllen: jeder Punkt des Strichs (alle ca. 4 m) ist ein Startpunkt; ein Strich ist ein Bearbeitungsschritt.
    /// Vorher wird gemessen, wie groß die Fläche würde; ist sie sehr groß, entscheidet der Nutzer.
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
            if (plan.Total == 0) { Map.ClearStroke(); return; }   // schon Wasser oder nichts darunter
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
            if (st == null || st.Water == 0) return;   // kein Schritt
            step.Note = Loc.F("{0} crater(s) filled, {1:#,0} m² of water, level {2} to {3} m.", st.Craters, st.Water, st.MinLevel, st.MaxLevel);
            Refresh(step);
            Recompute();
        }
        catch (Exception ex)
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
        bool river = waterKind == 2;
        int seed = Random.Shared.Next(1, 1_000_000);
        var outline = RangeSpec.PrepareOutline(stroke, true, 1);   // nur leicht abrunden, bleibt kantig (beim Fluss: die bearbeitete Uferlinie)
        bool dry = waterKind == 1;
        double minArea = dry ? 100 : river ? 150 : LakeSpec.MinArea;
        if (outline.Count < 3 || RangeSpec.Area(outline) < minArea)
        {
            Msg.Info(this, settings, "water-1", Loc.F("The area is too small. Draw a larger one (at least about {0} m²).", minArea), Loc.T("Water"));
            return;
        }
        var spec = new LakeSpec { Level = (int)waterLevel, Depth = waterWDepth, Shore = river ? Math.Max(2, Math.Min(waterShore, waterWidth / 2)) : waterShore, Bumps = WaterBumpsBox.SelectedIndex, Seed = seed, Outline = outline, Biome = waterBiome };
        // Erweitern/Anschließen: Gibt es Wasser im Bereich oder direkt daneben, gilt dessen Stand (sonst Stufen im Wasser)
        int adopted = 0;
        if (!dry && Lake.ExistingLevel(world, outline) is int old && old != spec.Level) { adopted = old; spec.Level = old; waterLevel = old; WaterLevelBox.Text = Fmt(old); }
        double x0 = outline.Min(p => p.X), x1 = outline.Max(p => p.X), z0 = outline.Min(p => p.Y), z1 = outline.Max(p => p.Y);
        double area = RangeSpec.Area(outline);
        string title = Loc.F(dry ? "Water removed at {0:0}, {1:0}" : river ? "River at {0:0}, {1:0}" : "Lake at {0:0}, {1:0}", (x0 + x1) / 2, (z0 + z1) / 2);
        string detail = string.Format(CultureInfo.InvariantCulture, dry
            ? "Wasser entfernen bei {0:0},{1:0}: Fläche {2:0} m², Unebenheit {3}"
            : "See bei {0:0},{1:0}: Fläche {2:0} m², Spiegel {4} m, Tiefe {5} m, Ufer {6} m, Unebenheit {3}, Startwert {7}",
            (x0 + x1) / 2, (z0 + z1) / 2, area, spec.Bumps, spec.Level, spec.Depth, spec.Shore, spec.Seed);
        var w = world;
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            LakeStats st = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var s = dry ? Lake.Remove(w, e, outline, spec.Bumps) : Lake.Place(w, e, spec);
                st ??= s;   // Ergebnis des ersten Durchgangs (spätere: Wiederholen/Neu rechnen)
            }));
            if (st == null || (st.Water == 0 && st.Changed == 0))
            {
                MessageBox.Show(this, dry ? Loc.T("There is no water in this area.") : Loc.T("Nothing changed: the area lies only on roads, buildings or city tiles, which stay untouched."),
                    Loc.T("Water"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;   // kein Schritt: der Bereich bleibt zum Ändern stehen
            }
            Map.ClearArea();
            if (dry)
                step.Note = Loc.F("{0:#,0} m² of water removed. The ground stays as it is.", st.Water);
            else
            {
                step.Note = Loc.F(river ? "River with {0:#,0} m² at {1} m above sea level, up to {2:0.0} m deep." : "Lake with {0:#,0} m² at {1} m above sea level, up to {2:0.0} m deep.", st.Water, spec.Level, st.MaxDepth) + " " +
                    Loc.F("{0:#,0} height points changed.", st.Changed);
                if (st.MaxRaise > 2)
                    step.Note += " ⚠ " + Loc.F("The land around is lower than the water level: a bank up to {0:0.0} m high was built. For a mountain lake use a lower level or a hollow in higher ground.", st.MaxRaise);
                if (st.MeanGround - spec.Level > spec.Depth + 3)
                    step.Note += " ⚠ " + Loc.F("The ground here is {0:0} m high, much above the water level; the lake needs deep digging. Try a level near the ground height.", st.MeanGround);
            }
            if (adopted != 0)
                step.Note += " " + Loc.F("The existing water here stands at {0} m: the new water takes this level so that there are no steps.", adopted);
            if (st.Spared > 0)
                step.Note += " " + Loc.F("{0:#,0} m² on roads, buildings or tiles were left alone.", st.Spared);
            Refresh(step);
            Recompute();
        }
        catch (Exception ex)
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
    /// Den gezeichneten Bereich berechnen (Gebirgskette oder See, je nach Werkzeug)
    Task PlaceArea() => rangeMode ? PlaceRange(Map.Area.ToList()) : RiverPathMode ? ConvertRiver() : PlaceWater(Map.Area.ToList());
}

public partial class MainWindow
{
    /// Spiegel aus dem gezeichneten Bereich: niedrigste Geländehöhe entlang des Umrisses (abgerundet), dann braucht der See keinen Damm
    void WaterFromGround_Click(object sender, RoutedEventArgs e)
    {
        if (world == null || Map.Area.Count < MinAreaPoints)
        {
            Msg.Info(this, settings, "water-2", Loc.T("Draw the area of the lake first."), Loc.T("Water"));
            return;
        }
        WaterValue_Changed(null, null);
        var outline = RiverPathMode ? Lake.Corridor(Map.Area.ToList(), waterWidth, 1) : RangeSpec.PrepareOutline(Map.Area.ToList(), true, 1);
        // Tiefstes Gelände in der ganzen Fläche plus 10 m ringsum (nicht nur auf der Linie: fällt das Land dort ab, wäre der Spiegel zu hoch)
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
        // Tiefe (gegraben) minus Wassertiefe = so viel liegt der Spiegel unter dem tiefsten Punkt des Umrisses
        waterLevel = Math.Clamp(Math.Floor(low - Math.Max(0, waterDepth - waterWDepth)), LakeSpec.MinLevel, LakeSpec.MaxLevel);
        WaterLevelBox.Text = Fmt(waterLevel);
        WaterValue_Changed(sender, e);
    }
}
