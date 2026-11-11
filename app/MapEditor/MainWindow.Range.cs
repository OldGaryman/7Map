using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MapEditor.Map;

namespace MapEditor;

/// Gebirgskette im frei gezeichneten Bereich (Wunsch des Nutzers 4.11.2026): Abschnitt „Gebirgskette“ links. Der Nutzer zeichnet mit
/// gedrückter linker Maustaste einen Bereich; beim Loslassen entsteht darin eine Gebirgs-, Hügelkette oder ein Hochland, mit
/// unterschiedlichen Ausläufern am Rand (sanft, steil, abgehackt, gemischt). Rechnung und Schutz von Straßen, Gebäuden und Kacheln
/// steckt im Terrain-Pinsel (TerrainBrush.Apply mit RangeSpec); Straßen und Gebäude stellt man im Abschnitt „Berge und Hügel“ ein.
public partial class MainWindow
{
    double rangeHeight = 90, rangeFoot = 150, rangeSpacing = 250, rangeRough = 30, rangeCrag = 60;
    bool loadingRange;

    static string HelpRange => Loc.T("Mountain range: draw an area with the left button held, change its points, then Apply or Enter · drag right = move the map · Esc = discard the area · Esc again = cancel");

    // Standardwerte je Art: Höhe, Hangbreite, Gipfelabstand, Zerklüftung
    static readonly (double Height, double Foot, double Spacing, double Rough, double Crag)[] RangeDefaults =
    {
        (90, 150, 250, 30, 60),   // Gebirgskette
        (20, 80, 120, 70, 25),    // Hügelkette
        (45, 60, 200, 15, 30),    // Hochland
    };

    void ShowRangeValues()
    {
        loadingRange = true;
        try
        {
            rangeHeight = settings.RangeHeight; rangeFoot = settings.RangeFoot; rangeSpacing = settings.RangeSpacing; rangeRough = settings.RangeRough; rangeCrag = settings.RangeCrag;
            RangeCragBox.Text = Fmt(rangeCrag);
            RangeKindBox.SelectedIndex = Math.Clamp(settings.RangeKind, 0, 2);
            RangeEdgeBox.SelectedIndex = Math.Clamp(settings.RangeEdge, 0, 3);
            RangeRoadsBox.SelectedIndex = Math.Clamp(settings.RangeRoads, 0, 2);
            RangePoisBox.SelectedIndex = Math.Clamp(settings.RangePois, 0, 2);
            RangeHeightBox.Text = Fmt(rangeHeight);
            RangeFootBox.Text = Fmt(rangeFoot);
            RangeSpacingBox.Text = Fmt(rangeSpacing);
            RangeRoughBox.Text = Fmt(rangeRough);
        }
        finally { loadingRange = false; }
    }

    void RangeTool_Click(object sender, RoutedEventArgs e)
    {
        if (RangeTool.IsChecked != true) { ToolCancel_Click(sender, e); return; }
        EndSpawnMode();
        EndGhost();
        SetEraseMode(true, BrushTool.Range);
    }

    // andere Art: ihre Standardwerte einsetzen
    void RangeKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingRange || RangeKindBox.SelectedIndex < 0) return;
        var d = RangeDefaults[RangeKindBox.SelectedIndex];
        loadingRange = true;
        try
        {
            RangeHeightBox.Text = Fmt(d.Height);
            RangeFootBox.Text = Fmt(d.Foot);
            RangeSpacingBox.Text = Fmt(d.Spacing);
            RangeRoughBox.Text = Fmt(d.Rough);
            RangeCragBox.Text = Fmt(d.Crag);
        }
        finally { loadingRange = false; }
        settings.RangeKind = RangeKindBox.SelectedIndex;
        RangeValue_Changed(sender, null);
    }

    void RangeEdgeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingRange || RangeEdgeBox.SelectedIndex < 0 || settings.RangeEdge == RangeEdgeBox.SelectedIndex) return;
        settings.RangeEdge = RangeEdgeBox.SelectedIndex;
        settings.Save();
    }

    void RangeRoadsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingRange || RangeRoadsBox.SelectedIndex < 0 || settings.RangeRoads == RangeRoadsBox.SelectedIndex) return;
        settings.RangeRoads = RangeRoadsBox.SelectedIndex;
        settings.Save();
    }

    void RangePoisBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingRange || RangePoisBox.SelectedIndex < 0 || settings.RangePois == RangePoisBox.SelectedIndex) return;
        settings.RangePois = RangePoisBox.SelectedIndex;
        settings.Save();
    }

    void RangeValue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RangeValue_Changed(sender, e);
    }

    // Werte lesen; links geänderte Werte gelten auch beim nächsten Start
    void RangeValue_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingRange) return;
        rangeHeight = ReadBox(RangeHeightBox, -150, 250, rangeHeight);
        rangeFoot = ReadBox(RangeFootBox, 8, 1000, rangeFoot);
        rangeSpacing = ReadBox(RangeSpacingBox, 30, 1500, rangeSpacing);
        rangeRough = ReadBox(RangeRoughBox, 0, 300, rangeRough);
        rangeCrag = ReadBox(RangeCragBox, 0, 300, rangeCrag);
        if (sender == null) return;
        bool dirty = settings.RangeHeight != rangeHeight || settings.RangeFoot != rangeFoot || settings.RangeSpacing != rangeSpacing
            || settings.RangeRough != rangeRough || settings.RangeCrag != rangeCrag || settings.RangeKind != RangeKindBox.SelectedIndex;
        if (!dirty) return;
        settings.RangeHeight = rangeHeight; settings.RangeFoot = rangeFoot; settings.RangeSpacing = rangeSpacing;
        settings.RangeRough = rangeRough; settings.RangeCrag = rangeCrag; settings.RangeKind = RangeKindBox.SelectedIndex;
        settings.Save();
    }

    async Task PlaceRange(List<Point> stroke)
    {
        if (world == null || edits == null) { Map.ClearArea(); return; }
        RangeValue_Changed(null, null);
        var outline = RangeSpec.PrepareOutline(stroke);
        if (outline.Count < 3 || RangeSpec.Area(outline) < RangeSpec.MinArea)
        {
            Msg.Info(this, settings, "range-1", Loc.T("The area is too small. Draw a larger one (at least about 50 × 50 m)."), Loc.T("Mountain range"));
            return;
        }
        var kind = (RangeKind)Math.Clamp(RangeKindBox.SelectedIndex, 0, 2);
        var edge = (RangeEdge)Math.Clamp(RangeEdgeBox.SelectedIndex, 0, 3);
        var spec = new RangeSpec
        {
            Kind = kind, Edge = edge, Height = rangeHeight, Foot = rangeFoot, Spacing = rangeSpacing, Rough = rangeRough, Crag = rangeCrag,
            Seed = Random.Shared.Next(1, 1_000_000), Outline = outline,
        };
        var roadMode = (TerrainRoads)Math.Clamp(RangeRoadsBox.SelectedIndex, 0, 2);
        var poiMode = (TerrainPois)Math.Clamp(RangePoisBox.SelectedIndex, 0, 2);
        double maxGrade = settings.TerrainLimitGrade ? settings.TerrainMaxGrade / 100 : 0;
        double x0 = outline.Min(p => p.X), x1 = outline.Max(p => p.X), z0 = outline.Min(p => p.Y), z1 = outline.Max(p => p.Y);
        double area = RangeSpec.Area(outline);
        string name = ((ComboBoxItem)RangeKindBox.SelectedItem).Content as string;
        string title = Loc.F("{0} at {1:0}, {2:0}", name, (x0 + x1) / 2, (z0 + z1) / 2);
        string detail = string.Format(CultureInfo.InvariantCulture, "Gebirgskette {0} bei {1:0},{2:0}: Fläche {3:0} m², Ausläufer {4}, Höhe {5} m, Hangbreite {6} m, Gipfelabstand {7} m, Zerklüftung {8} %, Schroffheit {12} %, Straßen {9}, Gebäude {10}, Startwert {11}",
            kind, (x0 + x1) / 2, (z0 + z1) / 2, area, edge, spec.Height, spec.Foot, spec.Spacing, spec.Rough, roadMode, poiMode, spec.Seed, spec.Crag);
        var w = world;
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            TerrainStats st = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var s = TerrainBrush.Apply(w, e, outline, 0, BrushShape.Round, TerrainKind.Range, spec.Height, 0, roadMode,
                    maxGrade, false, poiMode, null, null, 0, null, spec);
                st ??= s;   // Ergebnis des ersten Durchgangs (spätere: Wiederholen/Neu rechnen)
            }));
            if (st == null || st.Points == 0)
            {
                if (st != null && st.Protected > 0)
                    Msg.Info(this, settings, "range-2", Loc.T("Nothing changed: the area lies only on roads, buildings or city tiles, which stay untouched."), Loc.T("Mountain range"));
                return;   // nichts geändert: kein Schritt, der Bereich bleibt zum Ändern stehen
            }
            Map.ClearArea();   // gerechnet: der Bereich ist verbraucht, das Werkzeug bleibt für den nächsten an
            // höchster Punkt im Bereich nach der Änderung
            double top = 0;
            int ix0 = Math.Max(-w.Half, (int)Math.Floor(x0)), ix1 = Math.Min(w.Half - 1, (int)Math.Ceiling(x1));
            int iz0 = Math.Max(-w.Half, (int)Math.Floor(z0)), iz1 = Math.Min(w.Half - 1, (int)Math.Ceiling(z1));
            for (int z = iz0; z <= iz1; z++)
                for (int x = ix0; x <= ix1; x++) top = Math.Max(top, w.Heights[w.Index(x, z)] / 256.0);
            step.Note = Loc.F("{0:#,0} height points changed, by up to {1:0.0} m.", st.Points, st.MaxChange) + " " +
                Loc.F("The area is {0:#,0} m²; its highest point is now {1:0} m above sea level.", area, top);
            if (top > 250)
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
            MessageBox.Show(this, Loc.T("Placing the mountain range failed:") + "\n" + ex.Message, Loc.T("Mountain range"), MessageBoxButton.OK, MessageBoxImage.Error);
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
