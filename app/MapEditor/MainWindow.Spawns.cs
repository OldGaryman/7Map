using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using MapEditor.Map;

namespace MapEditor;

/// Spawn points (the user's request, 2026-10-29): any number for playing, at most one for testing.
/// Box "Spawn points" on the left, list in its own window. Setting, deleting and restoring by hand are written immediately to the
/// files (the user's request, no undo); only the test point of a new road belongs to its step.
public partial class MainWindow
{
    readonly SpawnWindow spawnWindow = new();
    static readonly Brush PlayBrush = Freeze(new SolidColorBrush(Color.FromRgb(216, 90, 48)));
    static readonly Brush TestBrush = Freeze(new SolidColorBrush(Color.FromRgb(83, 74, 183)));

    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    void InitSpawns()
    {
        Map.SpawnPlaced += PlaceSpawn;
        Map.SpawnClicked += ShowSpawnInList;
        spawnWindow.RestoreButton.Click += RestoreSpawns_Click;
        spawnWindow.SpawnList.SelectionChanged += (_, _) =>
        {
            var s = (spawnWindow.SpawnList.SelectedItem as ListBoxItem)?.Tag as SpawnPoint;
            Map.SelectedSpawn = s;
            if (s != null && !selectingSpawnFromMap) Map.CenterOn(s.X, s.Z, 0.5);
        };
        TestAtRoadCheck.IsChecked = settings.TestSpawnAtRoad;
        UseTestSpawnCheck.IsChecked = settings.DeployTestSpawn;
        Loaded += (_, _) => spawnWindow.Owner = this;
    }

    void UpdateSpawns()
    {
        bool loaded = world != null;
        AddPlaySpawn.IsEnabled = AddTestSpawn.IsEnabled = SpawnListButton.IsEnabled = UseTestSpawnCheck.IsEnabled = loaded;
        PlaySpawnCount.Text = loaded ? world.PlaySpawns.Count().ToString() : "0";
        var test = world?.TestSpawn;
        TestSpawnCount.Text = test == null ? "–" : "1";
        spawnWindow.RestoreButton.IsEnabled = loaded && File.Exists(Path.Combine(world.Folder, WorldData.OriginalSpawnFile));

        var list = spawnWindow.SpawnList;
        var keep = Map.SelectedSpawn;
        list.Items.Clear();
        Map.SelectedSpawn = null;
        if (!loaded) { spawnWindow.CountText.Text = Loc.T("SPAWN POINTS"); return; }
        spawnWindow.CountText.Text = Loc.F("{0} FOR PLAYING", world.PlaySpawns.Count()) + " · " + (test == null ? Loc.T("NO TEST POINT") : Loc.T("1 TEST POINT"));
        foreach (var s in world.Spawns.OrderByDescending(s => s.Test))
        {
            var row = new Grid();
            foreach (var w in new[] { 80.0, double.NaN, 50, 56, 26 })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
            var kind = new StackPanel { Orientation = Orientation.Horizontal };
            kind.Children.Add(new Ellipse { Width = 10, Height = 10, Stroke = s.Test ? TestBrush : PlayBrush, StrokeThickness = 2.5, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            kind.Children.Add(new TextBlock { Text = s.Test ? Loc.T("Test") : Loc.T("Play") });
            row.Children.Add(kind);
            AddCell(row, 1, string.Format(Num, "{0:0}, {1:0}", s.X, s.Z), HorizontalAlignment.Left);
            AddCell(row, 2, string.Format(Num, "{0:0}", s.Y), HorizontalAlignment.Right);
            AddCell(row, 3, Compass(s.Heading), HorizontalAlignment.Right);
            var del = new Button { Content = Icons.Trash, FontFamily = IconUi.Font, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(6, 0, 0, 0),
                                   Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = Loc.T("Delete spawn point"), Tag = s };
            del.Click += DeleteSpawn_Click;
            Grid.SetColumn(del, 4);
            row.Children.Add(del);
            var item = new ListBoxItem { Content = row, Tag = s, Padding = new Thickness(4, 2, 2, 2) };
            list.Items.Add(item);
            if (s.SameAs(keep)) { item.IsSelected = true; }
        }
    }

    static void AddCell(Grid row, int col, string text, HorizontalAlignment align)
    {
        var t = new TextBlock { Text = text, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(t, col);
        row.Children.Add(t);
    }

    // view direction as a compass direction with degrees, e.g. "NE 45°"
    static string Compass(double heading)
    {
        string[] names = { Loc.T("N"), Loc.T("NE"), Loc.T("E"), Loc.T("SE"), Loc.T("S"), Loc.T("SW"), Loc.T("W"), Loc.T("NW") };
        double h = (heading % 360 + 360) % 360;
        return $"{names[(int)Math.Round(h / 45) % 8]} {h:0}°";
    }

    async void AddSpawn_Click(object sender, RoutedEventArgs e)
    {
        bool test = sender == AddTestSpawn;
        var button = (System.Windows.Controls.Primitives.ToggleButton)sender;
        if (button.IsChecked != true || world == null) { EndSpawnMode(); UpdateRibbon(); return; }
        if (!await ConfirmSwitch(sender)) { button.IsChecked = false; UpdateRibbon(); return; }
        if (Map.EraseMode) SetEraseMode(false);   // a brush or an empty road is ended, nothing is lost
        if (Map.EditMode || Map.RoadPoints.Count > 0) ClearRoad();
        (test ? AddPlaySpawn : AddTestSpawn).IsChecked = false;
        EndGhost();
        Map.SpawnMode = test;
        HelpText.Text = SpawnHelp(test);
        UpdateRibbon();
    }

    static string SpawnHelp(bool test) => test
        ? Loc.T("Set the test point: click into the map · drag with the button held for the view direction · Esc = cancel")
        : Loc.T("Set a spawn point for playing: click into the map · drag with the button held for the view direction · Esc = cancel");

    void EndSpawnMode()
    {
        if (Map.SpawnMode == null && AddPlaySpawn.IsChecked != true && AddTestSpawn.IsChecked != true) return;
        Map.SpawnMode = null;
        AddPlaySpawn.IsChecked = AddTestSpawn.IsChecked = false;
        UpdateRibbon();
        HelpText.Text = HelpIdle;
    }

    // point set from the map: height from the ground, immediately into the file (a new test point replaces the old one)
    void PlaceSpawn(SpawnPoint p)
    {
        EndSpawnMode();
        if (world == null || Map.Busy) return;   // Save/apply road uses the same list and files (audit finding F29)
        p.Y = world.SpawnHeight(p.X, p.Z);
        if (p.Test) world.Spawns.RemoveAll(s => s.Test);
        world.Spawns.Add(p);
        WriteSpawnFiles(p.Test);
    }

    void DeleteSpawn_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((Button)sender).Tag is not SpawnPoint s || world == null || Map.Busy) return;
        world.Spawns.Remove(s);
        WriteSpawnFiles(s.Test);
    }

    // save play points or test point immediately (play/test file and the spawnpoints.xml currently in use)
    bool WriteSpawnFiles(bool test)
    {
        try
        {
            if (test) world.WriteTestSpawn(); else world.WritePlaySpawns();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("Saving the spawn points failed:") + "\n" + ex.Message, Loc.T("Spawn points"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            Map.InvalidateVisual();
            UpdateSpawns();
        }
        return true;
    }

    // replace the play points with those of the original world; height to today's ground (roads have changed the terrain)
    void RestoreSpawns_Click(object sender, RoutedEventArgs e)
    {
        if (world == null || Map.Busy) return;
        var originals = WorldData.ReadSpawns(Path.Combine(world.Folder, WorldData.OriginalSpawnFile), false);
        if (originals.Count == 0) return;
        if (MessageBox.Show(spawnWindow, Loc.F("Replace the {0} play points with the {1} spawn points of the original world?\nThe test point stays. This is saved right away.",
                world.PlaySpawns.Count(), originals.Count), Loc.T("Restore originals"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        foreach (var s in originals) s.Y = world.SpawnHeight(s.X, s.Z);
        world.Spawns.RemoveAll(s => !s.Test);
        world.Spawns.InsertRange(0, originals);
        WriteSpawnFiles(false);
    }

    // Last step of the world generation: distribute play points over the finished map (number from yamee_generator.txt, otherwise 12)
    async void SpreadSpawnButton_Click(object sender, RoutedEventArgs e)
    {
        if (world == null || Map.Busy) return;
        int count = Math.Clamp(UserValues.Load(new GeneratorSpec()).SpawnCount, 1, 100);   // number from default.ini / user.ini ([Generator] SpawnCount)
        if (MessageBox.Show(this, Loc.F("Replace the {0} play points with {1} new ones near gravel paths in the pine forest?\nThe test point stays. This is saved right away.",
                world.PlaySpawns.Count(), count), Loc.T("Spawn points"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var w = world;
        var list = await Task.Run(() => SpawnPlacer.Choose(w, count, w.Name));
        if (w != world) return;
        w.Spawns.RemoveAll(s => !s.Test);
        w.Spawns.InsertRange(0, list);
        WriteSpawnFiles(false);
        MessageBox.Show(this, Loc.F("{0} spawn points placed.", list.Count), Loc.T("Spawn points"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    bool selectingSpawnFromMap;

    // click on a spawn point in the map: open the list and choose the entry (the map stays where it is)
    void ShowSpawnInList(SpawnPoint s)
    {
        SpawnList_Click(null, null);
        var item = spawnWindow.SpawnList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == s);
        if (item == null) return;
        selectingSpawnFromMap = true;
        try { spawnWindow.SpawnList.SelectedItem = item; }
        finally { selectingSpawnFromMap = false; }
        spawnWindow.SpawnList.ScrollIntoView(item);
    }

    void SpawnList_Click(object sender, RoutedEventArgs e)
    {
        if (!spawnWindow.IsVisible)
        {
            spawnWindow.Left = Left + 300;
            spawnWindow.Top = Top + 120;
        }
        spawnWindow.Show();
        spawnWindow.Activate();
    }

    void TestAtRoad_Click(object sender, RoutedEventArgs e)
    {
        settings.TestSpawnAtRoad = TestAtRoadCheck.IsChecked == true;
        settings.Save();
    }

    // checkbox "Use test spawn point" (box "This world") applies at once: spawnpoints.xml is copied from the saved
    // play or test file
    void UseTestSpawn_Click(object sender, RoutedEventArgs e)
    {
        settings.DeployTestSpawn = UseTestSpawnCheck.IsChecked == true;
        settings.Save();
        if (world == null || Map.Busy) return;   // when copying to the game, spawnpoints.xml is copied again to match anyway
        world.UseTestSpawn = settings.DeployTestSpawn;
        world.ActivateSpawns();
    }
}
