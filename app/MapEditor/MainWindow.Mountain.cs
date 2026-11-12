using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MapEditor.Map;

namespace MapEditor;

/// Mountain brush (the user's request, 2026-11-04): section "Mountains and hills" on the left. One click places a whole mountain (stamp of the game
/// or round dome) with height, size, rotation, steepness and ruggedness. Calculation and protection of roads, buildings and tiles
/// is in the terrain brush (TerrainBrush.Apply with MountainSpec).
public partial class MainWindow
{
    double mountainHeight = 90, mountainSize = 600, mountainRotation, mountainSteep = 100, mountainRough = 30;
    bool loadingMountain;
    readonly Dictionary<(MountainKind, int), float[]> stampCache = new();

    static string HelpMountain => Loc.T("Mountains and hills: click = place · drag with left button = move before letting go · R = turn · drag right = move the map · Esc = cancel");

    // default values per kind: height, size, steepness, ruggedness (heights from measurements on the game's worlds)
    static readonly (double Height, double Size, double Steep, double Rough)[] MountainDefaults =
    {
        (90, 600, 100, 30),   // Mountains: relief per biome 41–96 m
        (45, 600, 100, 20),   // Plateau (desert)
        (15, 500, 100, 40),   // Hills
        (40, 300, 100, 20),   // Dome
    };

    // typical peak relief of the mountains in the game per biome (pine forest, burnt forest, desert, snow, wasteland)
    static readonly double[] TypicalMountainHeight = { 55, 85, 42, 95, 40 };

    MountainKind SelectedMountain => (MountainKind)Math.Clamp(MountainKindBox.SelectedIndex, 0, 3);

    void ShowMountainValues()
    {
        loadingMountain = true;
        try
        {
            mountainHeight = settings.MountainHeight; mountainSize = settings.MountainSize; mountainRotation = settings.MountainRotation;
            mountainSteep = settings.MountainSteep; mountainRough = settings.MountainRough;
            MountainKindBox.SelectedIndex = Math.Clamp(settings.MountainKind, 0, 3);
            MountainVariantBox.SelectedIndex = Math.Clamp(settings.MountainVariant, 0, 1);
            MountainHeightBox.Text = Fmt(mountainHeight);
            MountainSizeBox.Text = Fmt(mountainSize);
            MountainRotationBox.Text = Fmt(mountainRotation);
            MountainSteepBox.Text = Fmt(mountainSteep);
            MountainRoughBox.Text = Fmt(mountainRough);
            MountainMirrorCheck.IsChecked = settings.MountainMirror;
            MountainRoadsBox.SelectedIndex = Math.Clamp(settings.MountainRoads, 0, 2);
            MountainPoisBox.SelectedIndex = Math.Clamp(settings.MountainPois, 0, 2);
            MountainVariantBox.IsEnabled = MountainDiceButton.IsEnabled = SelectedMountain != MountainKind.Dome;
        }
        finally { loadingMountain = false; }
        if (mountainMode) ApplyBrush();
    }

    void MountainTool_Click(object sender, RoutedEventArgs e)
    {
        if (MountainTool.IsChecked != true) { ToolCancel_Click(sender, e); return; }
        EndSpawnMode();
        EndGhost();
        SetEraseMode(true, BrushTool.Mountain);
    }

    // other kind: insert its default values (height, size, steepness, ruggedness)
    void MountainKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingMountain || MountainKindBox.SelectedIndex < 0) return;
        var d = MountainDefaults[MountainKindBox.SelectedIndex];
        loadingMountain = true;
        try
        {
            MountainHeightBox.Text = Fmt(d.Height);
            MountainSizeBox.Text = Fmt(d.Size);
            MountainSteepBox.Text = Fmt(d.Steep);
            MountainRoughBox.Text = Fmt(d.Rough);
        }
        finally { loadingMountain = false; }
        MountainVariantBox.IsEnabled = MountainDiceButton.IsEnabled = SelectedMountain != MountainKind.Dome;
        settings.MountainKind = MountainKindBox.SelectedIndex;
        MountainValue_Changed(sender, null);
    }

    void MountainVariantBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingMountain || MountainVariantBox.SelectedIndex < 0 || settings.MountainVariant == MountainVariantBox.SelectedIndex) return;
        settings.MountainVariant = MountainVariantBox.SelectedIndex;
        settings.Save();
    }

    void MountainMirror_Click(object sender, RoutedEventArgs e)
    {
        bool on = MountainMirrorCheck.IsChecked == true;
        if (settings.MountainMirror == on) return;
        settings.MountainMirror = on;
        settings.Save();
    }

    void MountainRoadsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingMountain || MountainRoadsBox.SelectedIndex < 0 || settings.MountainRoads == MountainRoadsBox.SelectedIndex) return;
        settings.MountainRoads = MountainRoadsBox.SelectedIndex;
        settings.Save();
    }

    void MountainPoisBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingMountain || MountainPoisBox.SelectedIndex < 0 || settings.MountainPois == MountainPoisBox.SelectedIndex) return;
        settings.MountainPois = MountainPoisBox.SelectedIndex;
        settings.Save();
    }

    // Roll the dice: shape, rotation (in 15° steps) and mirroring random
    void MountainDice_Click(object sender, RoutedEventArgs e)
    {
        var rnd = Random.Shared;
        MountainVariantBox.SelectedIndex = rnd.Next(2);
        MountainMirrorCheck.IsChecked = rnd.Next(2) == 1;
        MountainRotationBox.Text = Fmt(rnd.Next(24) * 15);
        MountainMirror_Click(sender, e);
        MountainValue_Changed(sender, e);
    }

    // R: rotate on by 45°
    void TurnMountain()
    {
        mountainRotation = (mountainRotation + 45) % 360;
        MountainRotationBox.Text = Fmt(mountainRotation);
        settings.MountainRotation = mountainRotation;
        settings.Save();
    }

    void MountainValue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) MountainValue_Changed(sender, e);
    }

    // read values; values changed on the left also apply at the next start
    void MountainValue_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingMountain) return;
        mountainHeight = ReadBox(MountainHeightBox, -150, 250, mountainHeight);
        mountainSize = ReadBox(MountainSizeBox, 40, 2000, mountainSize);
        mountainRotation = ReadBox(MountainRotationBox, -720, 720, mountainRotation);
        mountainRotation = ((mountainRotation % 360) + 360) % 360;
        mountainSteep = ReadBox(MountainSteepBox, 20, 300, mountainSteep);
        mountainRough = ReadBox(MountainRoughBox, 0, 300, mountainRough);
        if (mountainMode) ApplyBrush();
        if (sender == null) return;
        bool dirty = settings.MountainHeight != mountainHeight || settings.MountainSize != mountainSize || settings.MountainRotation != mountainRotation
            || settings.MountainSteep != mountainSteep || settings.MountainRough != mountainRough || settings.MountainKind != MountainKindBox.SelectedIndex;
        if (!dirty) return;
        settings.MountainHeight = mountainHeight; settings.MountainSize = mountainSize; settings.MountainRotation = mountainRotation;
        settings.MountainSteep = mountainSteep; settings.MountainRough = mountainRough; settings.MountainKind = MountainKindBox.SelectedIndex;
        settings.Save();
    }

    float[] StampFor(MountainKind kind, int variant)
    {
        if (kind == MountainKind.Dome) return null;
        if (!stampCache.TryGetValue((kind, variant), out var stamp))
            stampCache[(kind, variant)] = stamp = MountainSpec.LoadStamp(GameFolder, kind, variant);
        return stamp;
    }

    async void PlaceMountain(List<Point> stroke)
    {
        if (world == null || edits == null || stroke.Count == 0) { Map.ClearStroke(); return; }
        MountainValue_Changed(null, null);
        var kind = SelectedMountain;
        int variant = Math.Max(0, MountainVariantBox.SelectedIndex);
        var stamp = StampFor(kind, variant);
        if (kind != MountainKind.Dome && stamp == null)
        {
            Map.ClearStroke();
            MessageBox.Show(this, Loc.T("The mountain shapes of the game were not found (the Stamps folder inside the Data folder of the game). Set the game folder in the settings, or choose \"Dome\"."),
                Loc.T("Mountains and hills"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var spec = new MountainSpec
        {
            Kind = kind, Variant = variant, Height = mountainHeight, Size = mountainSize, Rotation = mountainRotation,
            Mirror = MountainMirrorCheck.IsChecked == true, Steep = mountainSteep, Rough = mountainRough,
            Seed = Random.Shared.Next(1, 1_000_000), Stamp = stamp,
        };
        var roadMode = (TerrainRoads)Math.Clamp(MountainRoadsBox.SelectedIndex, 0, 2);
        var poiMode = (TerrainPois)Math.Clamp(MountainPoisBox.SelectedIndex, 0, 2);
        double maxGrade = settings.TerrainLimitGrade ? settings.TerrainMaxGrade / 100 : 0;
        var center = stroke[^1];
        string name = ((ComboBoxItem)MountainKindBox.SelectedItem).Content as string;
        string title = Loc.F("{0} at {1:0}, {2:0}", name, center.X, center.Y);
        string detail = string.Format(CultureInfo.InvariantCulture, "Berg {0} (Form {1}) bei {2:0},{3:0}: Höhe {4} m, Größe {5} m, Drehung {6}°{7}, Steilheit {8} %, Zerklüftung {9} %, Straßen {10}, Gebäude {11}",
            kind, variant + 1, center.X, center.Y, spec.Height, spec.Size, spec.Rotation, spec.Mirror ? " gespiegelt" : "", spec.Steep, spec.Rough, roadMode, poiMode);
        var w = world;
        int cx = (int)Math.Floor(center.X), cz = (int)Math.Floor(center.Y);
        double ground = w.Contains(cx, cz) ? w.Heights[w.Index(cx, cz)] / 256.0 : 0;
        int biome = w.Contains(cx, cz) ? w.BiomeAt(cx, cz) : 0;
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            TerrainStats st = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var s = TerrainBrush.Apply(w, e, new[] { center }, spec.Radius, BrushShape.Round, TerrainKind.Mountain, spec.Height, 0, roadMode,
                    maxGrade, false, poiMode, null, null, 0, spec);
                st ??= s;   // result of the first pass (later ones: repeat/recalculate)
            }));
            Map.ClearStroke();
            if (st == null || st.Points == 0)
            {
                if (st != null && st.Protected > 0)
                    Msg.Info(this, settings, "mountain-1", Loc.T("Nothing changed: the mountain lies only on roads, buildings or city tiles, which stay untouched."), Loc.T("Mountains and hills"));
                return;   // nothing changed: no step
            }
            step.Note = Loc.F("{0:#,0} height points changed, by up to {1:0.0} m.", st.Points, st.MaxChange) + " " +
                Loc.F("The peak is about {0:0} m above sea level (the ground there is {1:0} m).", ground + spec.Height, ground);
            if (kind == MountainKind.Mountain && spec.Height > 0)
                step.Note += " " + Loc.F("In the game, mountains in the biome \"{0}\" are about {1:0} m high.", Loc.T(BiomePalette.Names[biome]), TypicalMountainHeight[biome]);
            if (ground + spec.Height > 250)
                step.Note += " ⚠ " + Loc.T("The height limit of the world is about 255 m; the top of the mountain is cut off there.");
            if (st.Protected > 0)
                step.Note += " " + Loc.F("{0:#,0} m² under the mountain were left alone (roads, buildings, tiles). Steep walls may appear next to them; the Terrain brush \"Weather\" softens them.", st.Protected);
            if (st.TilesUnder > 0)
                step.Note += " " + Loc.F("{0} city tile(s) under the mountain stayed unchanged in height.", st.TilesUnder);
            if (st.MovedBuildings > 0)
                step.Note += " " + Loc.F("{0} building(s) moved with the ground.", st.MovedBuildings);
            if (st.RoadScale < 0.999)
                step.Note += " " + Loc.F("To keep the road grade within {0:0} %, the effect near the road was reduced to {1:0} %.", maxGrade * 100, st.RoadScale * 100);
            Refresh(step);
            Recompute();
        }
        catch (Exception ex)
        {
            Map.ClearStroke();
            MessageBox.Show(this, Loc.T("Placing the mountain failed:") + "\n" + ex.Message, Loc.T("Mountains and hills"), MessageBoxButton.OK, MessageBoxImage.Error);
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
