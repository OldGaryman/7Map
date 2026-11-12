using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MapEditor.Map;

namespace MapEditor;

/// Biome brush (the user's request, 2026-10-29): section "Biomes" on the left with biome choice, round/square brush and brush size.
/// Uses the map's brush like the eraser (Map.EraseMode); biomeMode says which tool is painting at the moment.
public partial class MainWindow
{
    double biomeSize = 32;   // diameter (m)

    static string HelpBiome => Loc.T("Biome brush: drag left = paint · drag right = move · mouse wheel = zoom · Esc = cancel");

    int SelectedBiome => Math.Clamp(BiomeBox.SelectedIndex, 0, BiomePalette.Names.Length - 1);

    // biome brush on the left from the saved default values (settings, tab "Biomes")
    void ShowBiomeValues()
    {
        biomeSize = settings.BiomeBrushSize;
        BiomeSizeBox.Text = Fmt(biomeSize);
        BiomeSquareRadio.IsChecked = settings.BiomeBrushSquare;
        BiomeRoundRadio.IsChecked = !settings.BiomeBrushSquare;
        if (BiomeBox.Items.Count == 0) BuildBiomeBox();
        ApplyBrush();
    }

    // choice: color swatch like in the map and name in the set language (rebuild after a language change)
    void BuildBiomeBox()
    {
        int sel = BiomeBox.SelectedIndex >= 0 ? BiomeBox.SelectedIndex : Math.Clamp(settings.BiomeChoice, 0, BiomePalette.Names.Length - 1);
        BiomeBox.Items.Clear();
        BiomeLegendList.Children.Clear();
        for (int k = 0; k < BiomePalette.Names.Length; k++)
        {
            var c = BiomePalette.Display[k];
            var legendRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            legendRow.Children.Add(new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb((byte)c[0], (byte)c[1], (byte)c[2])),
            });
            legendRow.Children.Add(new TextBlock { Text = Loc.T(BiomePalette.Names[k]), VerticalAlignment = VerticalAlignment.Center });
            BiomeLegendList.Children.Add(legendRow);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb((byte)c[0], (byte)c[1], (byte)c[2])),
            });
            row.Children.Add(new TextBlock { Text = Loc.T(BiomePalette.Names[k]), VerticalAlignment = VerticalAlignment.Center });
            BiomeBox.Items.Add(new ComboBoxItem { Content = row });
        }
        BiomeBox.SelectedIndex = sel;
    }

    // brush of the map matching the active tool (size, shape, color of the stroke)
    void ApplyBrush()
    {
        Map.SingleStamp = mountainMode;
        Map.Lasso = areaMode;
        Map.AngularArea = waterMode;
        Map.OpenArea = waterMode && waterKind == 2 && !riverConverted;
        Map.AreaWidth = waterWidth;
        if (waterMode)
        {
            if (waterKind == 3) { Map.BrushRadius = 2.5; Map.BrushSquare = false; Map.Shape = BrushShape.Round; }
            var wf = new SolidColorBrush(Color.FromArgb(120, 0x3D, 0x8B, 0xD6));
            wf.Freeze();
            Map.StrokeFill = wf;
            return;
        }
        if (rangeMode)
        {
            var rf = new SolidColorBrush(Color.FromArgb(110, 0x8B, 0x6B, 0x4A));
            rf.Freeze();
            Map.StrokeFill = rf;
            return;
        }
        if (mountainMode)
        {
            Map.BrushRadius = mountainSize / 2;
            Map.BrushSquare = false;
            Map.Shape = BrushShape.Round;
            var mc = Color.FromArgb(150, 0x8B, 0x6B, 0x4A);
            var mf = new SolidColorBrush(mc);
            mf.Freeze();
            Map.StrokeFill = mf;
            return;
        }
        Map.BrushRadius = (terrainMode ? terrainSize : biomeMode ? biomeSize : eraserSize) / 2;
        Map.BrushSquare = terrainMode ? SelectedShape == BrushShape.Square : biomeMode ? BiomeSquareRadio.IsChecked == true : BrushSquareRadio.IsChecked == true;
        Map.Shape = terrainMode ? SelectedShape : BrushShape.Round;   // special shapes exist for the terrain brush
        if (terrainMode) { Map.StrokeFill = TerrainFill(SelectedTerrain); return; }
        if (!biomeMode) { Map.StrokeFill = null; return; }
        var c = BiomePalette.Display[SelectedBiome];
        var fill = new SolidColorBrush(Color.FromArgb(190, (byte)c[0], (byte)c[1], (byte)c[2]));
        fill.Freeze();
        Map.StrokeFill = fill;
    }

    async void BiomeTool_Click(object sender, RoutedEventArgs e)
    {
        if (BiomeTool.IsChecked != true) { ToolCancel_Click(sender, e); return; }
        if (!await ConfirmSwitch(sender)) { UpdateToolModule(); return; }
        EndSpawnMode();
        EndGhost();
        SetEraseMode(true, BrushTool.Biome);
    }

    void BiomeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || BiomeBox.SelectedIndex < 0) return;
        if (settings.BiomeChoice != BiomeBox.SelectedIndex) { settings.BiomeChoice = BiomeBox.SelectedIndex; settings.Save(); }
        ApplyBrush();
        UpdateToolModule();
    }

    // brush values changed on the left also apply at the next start (like in the settings window)
    void BiomeShape_Click(object sender, RoutedEventArgs e)
    {
        bool square = BiomeSquareRadio.IsChecked == true;
        if (settings.BiomeBrushSquare != square) { settings.BiomeBrushSquare = square; settings.Save(); }
        ApplyBrush();
    }

    void BiomeSize_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BiomeSize_Changed(sender, e);
    }

    void BiomeSize_Changed(object sender, RoutedEventArgs e)
    {
        biomeSize = ReadBox(BiomeSizeBox, 2, 500, biomeSize);
        ApplyBrush();
        if (sender != null && settings.BiomeBrushSize != biomeSize) { settings.BiomeBrushSize = biomeSize; settings.Save(); }
    }

    async void PaintBiomeStroke(List<Point> stroke)
    {
        if (world == null || edits == null) { Map.ClearStroke(); return; }
        BiomeSize_Changed(null, null);
        double radius = biomeSize / 2;
        bool square = Map.BrushSquare;
        int biome = SelectedBiome;
        string name = Loc.T(BiomePalette.Names[biome]);
        double cx = (stroke.Min(p => p.X) + stroke.Max(p => p.X)) / 2, cz = (stroke.Min(p => p.Y) + stroke.Max(p => p.Y)) / 2;
        string title = Loc.F("Biome {0} at {1:0}, {2:0}", name, cx, cz);
        string detail = string.Format(CultureInfo.InvariantCulture, "Biom {0} bei {1:0},{2:0}: Pinsel {3} m {4}, {5} Strichpunkte",
            BiomePalette.Names[biome], cx, cz, biomeSize, square ? "eckig" : "rund", stroke.Count);
        var w = world;
        double pixel = (double)w.Size / w.BiomeSize;
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            int count = -1;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                int c = BiomePainter.Paint(w, e, stroke, radius, square, biome);
                if (count < 0) count = c;   // result of the first pass (later ones: repeat/recalculate)
            }));
            Map.ClearStroke();
            if (count == 0) return;   // under the stroke everything was already this biome: no step
            step.Note = Loc.F("{0:#,0} m² are now {1}.", count * pixel * pixel, name);
            Refresh(step);
        }
        catch (Exception ex)
        {
            Map.ClearStroke();
            MessageBox.Show(this, Loc.T("Painting the biome failed:") + "\n" + ex.Message, Loc.T("Biome brush"), MessageBoxButton.OK, MessageBoxImage.Error);
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
