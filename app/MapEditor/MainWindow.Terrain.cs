using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MapEditor.Map;

namespace MapEditor;

/// Welches Werkzeug den Pinsel der Karte gerade benutzt (Radiergummi, Biom-Pinsel, Terrain-Pinsel)
enum BrushTool { Eraser, Biome, Terrain, Mountain, Range, Water }

/// Terrain-Pinsel (Wunsch des Nutzers 2.11.2026): Abschnitt „Terrain“ links mit Art, Form, Größe, Stärke und weichem Rand.
/// Benutzt wie Radiergummi und Biom-Pinsel den Pinsel der Karte (Map.EraseMode); brushTool sagt, welches Werkzeug gerade malt.
public partial class MainWindow
{
    BrushTool brushTool;
    bool biomeMode => brushTool == BrushTool.Biome;
    bool terrainMode => brushTool == BrushTool.Terrain;
    bool mountainMode => brushTool == BrushTool.Mountain;
    bool rangeMode => brushTool == BrushTool.Range;
    bool waterMode => brushTool == BrushTool.Water;
    /// Werkzeuge, bei denen der Nutzer einen Bereich zeichnet und dann übernimmt
    bool areaMode => rangeMode || (waterMode && waterKind != 3);

    double terrainSize = 32, terrainStrength = 50, terrainSoft = 60, terrainMaxGrade = 25, terrainMin = 30, terrainMax = 60;

    static string HelpTerrain => Loc.T("Terrain brush: drag left = shape · drag right = move · mouse wheel = zoom · Esc = cancel");
    string HelpBrushText => waterMode ? (waterKind == 3 ? HelpCrater : HelpWater) : rangeMode ? HelpRange : mountainMode ? HelpMountain : biomeMode ? HelpBiome : terrainMode ? HelpTerrain : HelpErase;

    TerrainKind SelectedTerrain => (TerrainKind)Math.Clamp(TerrainKindBox.SelectedIndex, 0, 5);

    static readonly Color[] TerrainColors =
    {
        Color.FromRgb(0x5B, 0x9B, 0xD5), Color.FromRgb(0xD9, 0x8C, 0x3F), Color.FromRgb(0x3F, 0x6F, 0xB5),
        Color.FromRgb(0x8A, 0x90, 0x96), Color.FromRgb(0xB5, 0x6B, 0xB5), Color.FromRgb(0x6F, 0xB0, 0x5A),
    };

    // Terrain-Pinsel links aus den gespeicherten Werten
    void ShowTerrainValues()
    {
        settings.TerrainStrengths = Normalized(settings.TerrainStrengths);
        terrainSize = settings.TerrainSize;
        terrainSoft = settings.TerrainSoftness;
        TerrainSizeBox.Text = Fmt(terrainSize);
        TerrainSoftBox.Text = Fmt(terrainSoft);
        TerrainShapeBox.SelectedIndex = Math.Clamp(settings.TerrainShape > 0 ? settings.TerrainShape : settings.TerrainSquare ? 1 : 0, 0, 8);
        TerrainKindBox.SelectedIndex = Math.Clamp(settings.TerrainKind, 0, 5);
        TerrainRoadsBox.SelectedIndex = Math.Clamp(settings.TerrainRoads, 0, 2);
        TerrainTiltBox.SelectedIndex = settings.TerrainRoadTilt ? 1 : 0;
        TerrainPoisBox.SelectedIndex = Math.Clamp(settings.TerrainPois, 0, 2);
        terrainMaxGrade = settings.TerrainMaxGrade;
        TerrainMaxGradeBox.Text = Fmt(terrainMaxGrade);
        terrainMin = settings.TerrainMinHeight; terrainMax = settings.TerrainMaxHeight;
        TerrainMinBox.Text = Fmt(terrainMin); TerrainMaxBox.Text = Fmt(terrainMax);
        TerrainHeightCheck.IsChecked = settings.TerrainHeightLimit;
        TerrainHeightRow.IsEnabled = settings.TerrainHeightLimit;
        TerrainLimitCheck.IsChecked = settings.TerrainLimitGrade;
        UpdateRoadOptions();
        ShowTerrainStrength();
        ApplyBrush();
    }

    static double[] Normalized(double[] v)
    {
        var d = TerrainBrush.DefaultStrength;
        if (v == null || v.Length != d.Length) return (double[])d.Clone();
        return v;
    }

    // Stärke der gewählten Art: Prozent oder Meter
    void ShowTerrainStrength()
    {
        var kind = SelectedTerrain;
        terrainStrength = settings.TerrainStrengths[(int)kind];
        TerrainStrengthBox.Text = Fmt(terrainStrength);
        TerrainStrengthLabel.Text = TerrainBrush.IsPercent(kind) ? Loc.T("Strength (%)") : Loc.T("Strength (m)");
    }

    Brush TerrainFill(TerrainKind kind)
    {
        var c = TerrainColors[(int)kind];
        var fill = new SolidColorBrush(Color.FromArgb(170, c.R, c.G, c.B));
        fill.Freeze();
        return fill;
    }

    async void TerrainTool_Click(object sender, RoutedEventArgs e)
    {
        if (TerrainTool.IsChecked != true) { ToolCancel_Click(sender, e); return; }
        if (!await ConfirmSwitch(sender)) { UpdateToolModule(); return; }
        EndSpawnMode();
        EndGhost();
        SetEraseMode(true, BrushTool.Terrain);
    }

    void TerrainKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TerrainKindBox.SelectedIndex < 0) return;
        if (settings.TerrainKind != TerrainKindBox.SelectedIndex) { settings.TerrainKind = TerrainKindBox.SelectedIndex; settings.Save(); }
        ShowTerrainStrength();
        ApplyBrush();
        UpdateToolModule();
    }

    void TerrainRoadsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRoadOptions();
        if (!IsLoaded || TerrainRoadsBox.SelectedIndex < 0 || settings.TerrainRoads == TerrainRoadsBox.SelectedIndex) return;
        settings.TerrainRoads = TerrainRoadsBox.SelectedIndex;
        settings.Save();
    }

    // Querschnitt und Gefällegrenze gelten nur, wenn sich Straßen ändern dürfen
    void UpdateRoadOptions()
    {
        if (TerrainRoadOptions != null) TerrainRoadOptions.IsEnabled = TerrainRoadsBox.SelectedIndex > 0;
    }

    void TerrainPoisBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TerrainPoisBox.SelectedIndex < 0 || settings.TerrainPois == TerrainPoisBox.SelectedIndex) return;
        settings.TerrainPois = TerrainPoisBox.SelectedIndex;
        settings.Save();
    }

    void TerrainTiltBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool tilt = TerrainTiltBox.SelectedIndex == 1;
        if (!IsLoaded || TerrainTiltBox.SelectedIndex < 0 || settings.TerrainRoadTilt == tilt) return;
        settings.TerrainRoadTilt = tilt;
        settings.Save();
    }


    void TerrainHeightCheck_Click(object sender, RoutedEventArgs e)
    {
        bool on = TerrainHeightCheck.IsChecked == true;
        TerrainHeightRow.IsEnabled = on;
        if (settings.TerrainHeightLimit == on) return;
        settings.TerrainHeightLimit = on;
        settings.Save();
    }

    void TerrainLimit_Click(object sender, RoutedEventArgs e)
    {
        bool on = TerrainLimitCheck.IsChecked == true;
        if (settings.TerrainLimitGrade == on) return;
        settings.TerrainLimitGrade = on;
        settings.Save();
    }

    BrushShape SelectedShape => (BrushShape)Math.Clamp(TerrainShapeBox.SelectedIndex, 0, 8);

    void TerrainShapeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TerrainShapeBox.SelectedIndex < 0) return;
        int idx = TerrainShapeBox.SelectedIndex;
        if (settings.TerrainShape != idx || settings.TerrainSquare != (idx == 1))
        {
            settings.TerrainShape = idx;
            settings.TerrainSquare = idx == 1;   // alte Einstellung bleibt passend
            settings.Save();
        }
        ApplyBrush();
    }

    void TerrainValue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TerrainValue_Changed(sender, e);
    }

    // Größe, Stärke und Rand lesen; links geänderte Werte gelten auch beim nächsten Start
    void TerrainValue_Changed(object sender, RoutedEventArgs e)
    {
        var kind = SelectedTerrain;
        terrainSize = ReadBox(TerrainSizeBox, 2, 500, terrainSize);
        terrainStrength = ReadBox(TerrainStrengthBox, TerrainBrush.MinStrength(kind), TerrainBrush.MaxStrength(kind), terrainStrength);
        terrainSoft = ReadBox(TerrainSoftBox, 0, 100, terrainSoft);
        terrainMaxGrade = ReadBox(TerrainMaxGradeBox, 1, 200, terrainMaxGrade);
        terrainMin = ReadBox(TerrainMinBox, -100, 1000, terrainMin);
        terrainMax = ReadBox(TerrainMaxBox, -100, 1000, terrainMax);
        if (terrainMin > terrainMax) TerrainMinBox.BorderBrush = TerrainMaxBox.BorderBrush = System.Windows.Media.Brushes.Red;
        ApplyBrush();
        if (sender == null) return;
        bool dirty = settings.TerrainSize != terrainSize || settings.TerrainSoftness != terrainSoft || settings.TerrainStrengths[(int)kind] != terrainStrength
            || settings.TerrainMaxGrade != terrainMaxGrade || settings.TerrainMinHeight != terrainMin || settings.TerrainMaxHeight != terrainMax;
        if (!dirty) return;
        settings.TerrainMaxGrade = terrainMaxGrade;
        settings.TerrainMinHeight = terrainMin; settings.TerrainMaxHeight = terrainMax;
        settings.TerrainSize = terrainSize;
        settings.TerrainSoftness = terrainSoft;
        settings.TerrainStrengths[(int)kind] = terrainStrength;
        settings.Save();
    }

    async void PaintTerrainStroke(List<Point> stroke)
    {
        if (world == null || edits == null) { Map.ClearStroke(); return; }
        TerrainValue_Changed(null, null);
        var kind = SelectedTerrain;
        double radius = terrainSize / 2, strength = terrainStrength, soft = terrainSoft / 100;
        var shape = SelectedShape;
        int seed = Map.StrokeSeed;
        var roadMode = (TerrainRoads)Math.Clamp(TerrainRoadsBox.SelectedIndex, 0, 2);
        double maxGrade = TerrainLimitCheck.IsChecked == true ? terrainMaxGrade / 100 : 0;
        bool limitHeight = TerrainHeightCheck.IsChecked == true && terrainMin <= terrainMax;
        double? minH = limitHeight ? terrainMin : null, maxH = limitHeight ? terrainMax : null;
        bool tilt = TerrainTiltBox.SelectedIndex == 1;
        var poiMode = (TerrainPois)Math.Clamp(TerrainPoisBox.SelectedIndex, 0, 2);
        string name = ((ComboBoxItem)TerrainKindBox.SelectedItem).Content as string;
        double cx = (stroke.Min(p => p.X) + stroke.Max(p => p.X)) / 2, cz = (stroke.Min(p => p.Y) + stroke.Max(p => p.Y)) / 2;
        string title = Loc.F("Terrain: {0} at {1:0}, {2:0}", name, cx, cz);
        string detail = string.Format(CultureInfo.InvariantCulture, "Terrain {0} bei {1:0},{2:0}: Pinsel {3} m {4}, Stärke {5}, weicher Rand {6} %, Straßen {8} ({9}, Gefälle höchstens {10}), Gebäude {11}, Höhengrenzen {12}, {7} Strichpunkte",
            kind, cx, cz, terrainSize, shape, strength, terrainSoft, stroke.Count, roadMode,
            tilt ? "Neigung erlaubt" : "ganze Breite gleich", maxGrade > 0 ? maxGrade * 100 + " %" : "unbegrenzt", poiMode,
            limitHeight ? terrainMin + "…" + terrainMax + " m" : "aus");
        var w = world;
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            TerrainStats st = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var s = TerrainBrush.Apply(w, e, stroke, radius, shape, kind, strength, soft, roadMode, maxGrade, tilt, poiMode, minH, maxH, seed);
                st ??= s;   // Ergebnis des ersten Durchgangs (spätere: Wiederholen/Neu rechnen)
            }));
            Map.ClearStroke();
            if (st == null || st.Points == 0)
            {
                if (st != null && st.Protected > 0)
                    Msg.Info(this, settings, "terrain-1", Loc.T("Nothing changed: the brush covers only roads, buildings or city tiles, which stay untouched."), Loc.T("Terrain brush"));
                return;   // nichts geändert: kein Schritt
            }
            step.Note = Loc.F("{0:#,0} height points changed, by up to {1:0.0} m.", st.Points, st.MaxChange) +
                (st.Protected > 0 ? " " + Loc.F("{0:#,0} m² under the brush were left alone (roads, buildings, tiles).", st.Protected) : "");
            if (st.TilesUnder > 0)
                step.Note += " " + Loc.F("{0} city tile(s) under the brush stayed unchanged in height.", st.TilesUnder);
            if (st.Limited > 0)
                step.Note += " " + Loc.F("{0:#,0} height points were levelled off at the height limits.", st.Limited);
            if (st.MovedBuildings > 0)
                step.Note += " " + Loc.F("{0} building(s) moved with the ground.", st.MovedBuildings);
            if (poiMode != TerrainPois.Fixed && st.StayedInTile + st.StayedShared + st.StayedEdge > 0)
            {
                var why = new List<string>();
                if (st.StayedInTile > 0) why.Add(Loc.F("{0} in city tiles", st.StayedInTile));
                if (st.StayedShared > 0) why.Add(Loc.F("{0} sharing driveways with another building", st.StayedShared));
                if (st.StayedEdge > 0) why.Add(Loc.F("{0} too close to the edge of the calculated area", st.StayedEdge));
                step.Note += " " + Loc.F("Buildings that stayed where they are: {0}.", string.Join(", ", why));
            }
            if (st.RoadScale < 0.999)
                step.Note += " " + Loc.F("To keep the road grade within {0:0} %, the effect near the road was reduced to {1:0} %.", maxGrade * 100, st.RoadScale * 100);
            if (maxGrade <= 0 && st.RoadGrade > 0.25)
                step.Note += " ⚠ " + Loc.F("The steepest road grade is now {0:0} %. Roads steeper than about 25 % are hard to drive.", st.RoadGrade * 100);
            Refresh(step);
            Recompute();
        }
        catch (Exception ex)
        {
            Map.ClearStroke();
            MessageBox.Show(this, Loc.T("Shaping the terrain failed:") + "\n" + ex.Message, Loc.T("Terrain brush"), MessageBoxButton.OK, MessageBoxImage.Error);
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
