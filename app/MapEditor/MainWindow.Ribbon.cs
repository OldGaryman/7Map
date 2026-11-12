using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MapEditor.Map;

namespace MapEditor;

/// New interface (2026-11-04, the user's request): ribbon with tabs (World, Terrain, Mountain ranges, Roads, Settings); on the left
/// are the values of the chosen tool. A click on a tool switches the ribbon to the matching tab and shows the values of this
/// tool. Roads and mountain ranges work with the window above the map (Apply/Cancel); if the user clicks on something
/// else meanwhile, they are asked whether the edit should be applied.
public partial class MainWindow
{
    string ribbonTab = "world";
    string lastActiveTool;

    // drag the width of the left side (the user's request, 2026-11-05), 240–700 px, remembered
    void LeftPanelThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        LeftPanel.Width = Math.Clamp(LeftPanel.Width + e.HorizontalChange, 240, Math.Max(240, Math.Min(700, ActualWidth - 700)));

    void LeftPanelThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        settings.LeftPanelWidth = LeftPanel.Width;
        settings.Save();
    }

    void RibbonTab_Click(object sender, RoutedEventArgs e) => ShowRibbonTab((string)((FrameworkElement)sender).Tag);

    void ShowRibbonTab(string tab)
    {
        ribbonTab = tab;
        RibbonWorld.Visibility = tab == "world" ? Visibility.Visible : Visibility.Collapsed;
        RibbonTerrain.Visibility = tab == "terrain" ? Visibility.Visible : Visibility.Collapsed;
        RibbonRange.Visibility = tab == "range" ? Visibility.Visible : Visibility.Collapsed;
        RibbonWater.Visibility = tab == "water" ? Visibility.Visible : Visibility.Collapsed;
        RibbonRoads.Visibility = tab == "roads" ? Visibility.Visible : Visibility.Collapsed;
        RibbonSpawn.Visibility = tab == "spawn" ? Visibility.Visible : Visibility.Collapsed;
        RibbonView.Visibility = tab == "view" ? Visibility.Visible : Visibility.Collapsed;
        RibbonSettings.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;
        TabWorld.IsChecked = tab == "world";
        TabTerrain.IsChecked = tab == "terrain";
        TabRange.IsChecked = tab == "range";
        TabWater.IsChecked = tab == "water";
        TabRoads.IsChecked = tab == "roads";
        TabSpawn.IsChecked = tab == "spawn";
        TabView.IsChecked = tab == "view";
        TabSettings.IsChecked = tab == "settings";
    }

    // menu buttons of the tools (RT_…) and the invisible state buttons (tag = name of the state button)
    List<ToggleButton> ribbonTools;

    IEnumerable<ToggleButton> RibbonTools => ribbonTools ??= new[] { RT_Terrain, RT_Mountain, RT_Hill, RT_Plateau, RT_Dome, RT_Biome, RT_RMountain, RT_RHills, RT_RHighland, RT_Lake, RT_River, RT_Crater, RT_Dry,
        RT_Asphalt, RT_Gravel, RT_RoadEdit, RT_Eraser }.ToList();

    /// click on a tool button in the ribbon: switches the state button (same procedures, same confirmation)
    void RibbonTool_Click(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        if (FindName((string)button.Tag) is not ToggleButton shortcut) return;
        shortcut.IsChecked = shortcut.IsChecked != true;
        shortcut.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        UpdateRibbon();
    }

    /// the tool that is currently switched on (null = none)
    string ActiveTool()
    {
        if (AsphaltTool.IsChecked == true) return "asphalt";
        if (GravelTool.IsChecked == true) return "gravel";
        if (RoadEditTool.IsChecked == true) return "roadedit";
        if (EraserTool.IsChecked == true) return "eraser";
        if (TerrainTool.IsChecked == true) return "terrain";
        if (MountainTool.IsChecked == true) return "mountain";
        if (BiomeTool.IsChecked == true) return "biome";
        if (RangeTool.IsChecked == true) return "range";
        if (WaterTool.IsChecked == true) return "water";
        if (AddPlaySpawn.IsChecked == true || AddTestSpawn.IsChecked == true) return "spawn";
        return null;
    }

    static string TabOf(string tool) => tool switch
    {
        "asphalt" or "gravel" or "roadedit" or "eraser" => "roads",
        "terrain" or "mountain" or "biome" => "terrain",
        "range" => "range",
        "water" => "water",
        "spawn" => "spawn",
        _ => "world",
    };

    /// after every change to the tool: set buttons on the left, tabs and groups in the ribbon accordingly
    void UpdateRibbon()
    {
        if (!IsInitialized) return;   // window not completely built yet (events while loading the XAML)
        bool loaded = world != null;
        bool m = MountainTool.IsChecked == true, r = RangeTool.IsChecked == true;
        int mk = MountainKindBox.SelectedIndex, rk = RangeKindBox.SelectedIndex;
        MtnMountainButton.IsChecked = m && mk == 0;
        MtnPlateauButton.IsChecked = m && mk == 1;
        MtnHillButton.IsChecked = m && mk == 2;
        MtnDomeButton.IsChecked = m && mk == 3;
        bool wt = WaterTool.IsChecked == true;
        WaterLakeButton.IsChecked = wt && waterKind == 0;
        WaterDryButton.IsChecked = wt && waterKind == 1;
        WaterRiverButton.IsChecked = wt && waterKind == 2;
        WaterCraterButton.IsChecked = wt && waterKind == 3;
        WaterLakeButton.IsEnabled = WaterDryButton.IsEnabled = WaterRiverButton.IsEnabled = WaterCraterButton.IsEnabled = loaded;
        RngMountainButton.IsChecked = r && rk == 0;
        RngHillsButton.IsChecked = r && rk == 1;
        RngHighlandButton.IsChecked = r && rk == 2;
        MtnMountainButton.IsEnabled = MtnPlateauButton.IsEnabled = MtnHillButton.IsEnabled = MtnDomeButton.IsEnabled = loaded;
        RngMountainButton.IsEnabled = RngHillsButton.IsEnabled = RngHighlandButton.IsEnabled = BiomeTool.IsEnabled = loaded;
        foreach (var rt in RibbonTools)   // show and disable menu buttons like the shortcuts on the left
            if (FindName((string)rt.Tag) is ToggleButton s) { rt.IsChecked = s.IsChecked == true; rt.IsEnabled = s.IsEnabled; }

        // heading = name of the button in the ribbon
        MountainTitle.Text = Loc.T(new[] { "Mountain", "Plateau", "Hill", "Dome" }[Math.Clamp(mk, 0, 3)]);
        WaterTitle.Text = Loc.T(waterKind == 1 ? "Remove water" : waterKind == 2 ? "River" : waterKind == 3 ? "Fill craters" : "Lake");
        WaterResetButton.Visibility = waterKind == 1 ? Visibility.Collapsed : Visibility.Visible;   // "Remove water" has no values
        WaterLakeRows.Visibility = waterKind == 1 || waterKind == 3 ? Visibility.Collapsed : Visibility.Visible;
        WaterCraterRows.Visibility = waterKind == 3 ? Visibility.Visible : Visibility.Collapsed;
        WaterBiomeRow.Visibility = waterKind == 1 ? Visibility.Collapsed : Visibility.Visible;
        WaterBumpsRow.Visibility = waterKind == 1 || waterKind == 3 ? Visibility.Collapsed : Visibility.Visible;
        WaterWidthRow.Visibility = waterKind == 2 ? Visibility.Visible : Visibility.Collapsed;
        WaterIrregRow.Visibility = waterKind == 2 ? Visibility.Visible : Visibility.Collapsed;
        WaterShoreRow.Visibility = waterKind == 2 ? Visibility.Collapsed : Visibility.Visible;
        RangeTitle.Text = Loc.T(new[] { "Mountain chain", "Chain of hills", "Highland" }[Math.Clamp(rk, 0, 2)]);

        string t = ActiveTool();
        if (t != lastActiveTool)
        {
            lastActiveTool = t;
            if (t != null) ShowRibbonTab(TabOf(t));
        }
        GroupTerrain.Visibility = t == "terrain" ? Visibility.Visible : Visibility.Collapsed;
        GroupMountain.Visibility = t == "mountain" ? Visibility.Visible : Visibility.Collapsed;
        GroupBiome.Visibility = t == "biome" ? Visibility.Visible : Visibility.Collapsed;
        GroupRange.Visibility = t == "range" ? Visibility.Visible : Visibility.Collapsed;
        GroupWater.Visibility = t == "water" ? Visibility.Visible : Visibility.Collapsed;
        GroupAsphalt.Visibility = t == "asphalt" ? Visibility.Visible : Visibility.Collapsed;
        GroupGravel.Visibility = t == "gravel" ? Visibility.Visible : Visibility.Collapsed;
        GroupRoadEdit.Visibility = t == "roadedit" ? Visibility.Visible : Visibility.Collapsed;
        GroupEraser.Visibility = t == "eraser" ? Visibility.Visible : Visibility.Collapsed;
        // without a tool (and with the spawn points, which have no values on the left) the left side disappears completely
        LeftPanel.Visibility = t is null or "spawn" ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Mountains and ranges: one button per kind on the left, internally one tool with a chosen kind ----------

    async void MountainKind_Click(object sender, RoutedEventArgs e)
    {
        int kind = int.Parse((string)((FrameworkElement)sender).Tag);
        bool off = MountainTool.IsChecked == true && MountainKindBox.SelectedIndex == kind;   // second click on the same kind
        if (!off && !await ConfirmSwitch(null)) { UpdateRibbon(); return; }
        if (off) { MountainTool.IsChecked = false; MountainTool_Click(MountainTool, e); return; }
        MountainKindBox.SelectedIndex = kind;
        MountainTool.IsChecked = true;
        MountainTool_Click(MountainTool, e);
    }

    async void RangeKind_Click(object sender, RoutedEventArgs e)
    {
        int kind = int.Parse((string)((FrameworkElement)sender).Tag);
        bool active = RangeTool.IsChecked == true;
        if (active && RangeKindBox.SelectedIndex == kind)   // second click on the same kind: tool off, a drawn area is discarded
        {
            Map.ClearArea();
            RangeTool.IsChecked = false;
            RangeTool_Click(RangeTool, e);
            return;
        }
        if (!active && !await ConfirmSwitch(null)) { UpdateRibbon(); return; }
        RangeKindBox.SelectedIndex = kind;   // with a range switched on only another kind: the drawn area stays
        if (!active) { RangeTool.IsChecked = true; RangeTool_Click(RangeTool, e); }
        else UpdateRibbon();
    }

    // ---------- Confirmation when something unfinished is open ----------

    /// what the tools with Apply/Cancel currently hold: null = nothing, otherwise "road", "edit" or "range"
    string PendingWork()
    {
        if (world == null) return null;
        if (roadEditMode) return editPlan != null && editPlan.Error == null && editPlan.Changes ? "edit" : null;
        if (Map.RoadPoints.Count >= 2 && Map.Result != null) return "road";
        if (areaMode && Map.EraseMode && Map.Area.Count >= MinAreaPoints) return rangeMode ? "range" : "water";
        return null;
    }

    /// before every other tool or button: if a road, road change or mountain range has not been applied yet, the user is asked
    /// (Yes = apply, No = discard, Cancel = keep editing). true = the new command may continue.
    async Task<bool> ConfirmSwitch(object sender)
    {
        if (sender is ToggleButton { IsChecked: not true }) return true;   // the active tool is switched off: like "Cancel"
        string what = PendingWork();
        if (what == null) return true;
        string text = what switch
        {
            "road" => Loc.T("The new road has not been applied yet."),
            "edit" => Loc.T("The change to the road has not been applied yet."),
            "water" => Loc.T("The lake has not been made yet."),
            _ => Loc.T("The mountain range has not been calculated yet."),
        };
        var result = MessageBox.Show(this, text + "\n\n" + Loc.T("Apply it now?\n\nYes = apply · No = discard · Cancel = keep editing"), Loc.T("Unfinished edit"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.No)
        {
            if (what is "range" or "water") Map.ClearArea(); else ClearRoad();
            return true;
        }
        if (what is "range" or "water") await PlaceArea();
        else if (what == "edit") await ApplyRoadEdit();
        else await ApplyRoad();
        return PendingWork() == null;   // applying was declined or failed: then everything stays as it was
    }
}

public partial class MainWindow
{
    // ---------- Tab View: rulers and dark theme ----------

    void InitRibbonOptions()
    {
        LeftPanel.Width = Math.Clamp(settings.LeftPanelWidth, 240, 700);
        RulerTop.Map = RulerLeft.Map = Map;
        ShowRulerCheck.IsChecked = settings.ShowRuler;
        ApplyRulers();
        DarkThemeCheck.IsChecked = settings.DarkTheme;
        ThemeManager.Apply(settings.DarkTheme);
        FillLanguageBox();
    }

    void ShowRuler_Click(object sender, RoutedEventArgs e)
    {
        settings.ShowRuler = ShowRulerCheck.IsChecked == true;
        settings.Save();
        ApplyRulers();
    }

    void ApplyRulers() => RulerTopRow.Visibility = RulerLeft.Visibility = settings.ShowRuler ? Visibility.Visible : Visibility.Collapsed;

    void DarkTheme_Click(object sender, RoutedEventArgs e)
    {
        settings.DarkTheme = DarkThemeCheck.IsChecked == true;
        settings.Save();
        ThemeManager.Apply(settings.DarkTheme);
        RedrawAfterTheme();
    }

    // ---------- Tab Settings ----------

    async void SettingsPage_Click(object sender, RoutedEventArgs e) => await OpenSettings(int.Parse((string)((FrameworkElement)sender).Tag));

    bool loadingLanguage;

    void FillLanguageBox()
    {
        loadingLanguage = true;
        var languages = Loc.Available();
        RibbonLanguageBox.ItemsSource = languages;
        RibbonLanguageBox.SelectedItem = languages.FirstOrDefault(l => l.Id == Loc.Language) ?? languages[0];
        loadingLanguage = false;
    }

    async void RibbonLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loadingLanguage || RibbonLanguageBox.SelectedItem is not Loc.LanguageInfo l) return;
        settings.Language = l.Id;
        settings.Save();
        if ((settings.Language ?? Loc.English) != Loc.Language) await ApplyLanguage(settings.Language);
    }

    static void OpenInShell(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        string folder = world == null ? null : Backups.Root(world);
        if (folder == null || !Directory.Exists(folder))
        {
            MessageBox.Show(this, Loc.T("There are no backups of this project yet. One is made every time you save."), Loc.T("Project backups"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenInShell(folder);
    }

    /// short hint in the status bar (e.g. for switched-off messages)
    public void ShowStatus(string text) => HelpText.Text = text;


    void OpenErrorLog_Click(object sender, RoutedEventArgs e)
    {
        string file = Path.Combine(AppSettings.DataFolder, "error.log");
        if (!File.Exists(file))
        {
            MessageBox.Show(this, Loc.T("No errors have been logged. Good."), Loc.T("Error log"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenInShell(file);
    }

    // version history: next to the exe (installation) or in the project folder (development); depending on the language the English version
    void OpenChangelog_Click(object sender, RoutedEventArgs e)
    {
        // history for users (all versions as bullet points); the detailed CHANGELOG*.md stays in the project
        string name = Loc.Language == Loc.English ? "RELEASE_NOTES.md" : "RELEASE_NOTES.de.md";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string file = Path.Combine(dir.FullName, name);
            if (File.Exists(file)) { OpenInShell(file); return; }
        }
        MessageBox.Show(this, Loc.T("The version history was not found."), Loc.T("Version history"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

public partial class MainWindow
{
    /// after changing the theme: redraw areas drawn in code
    void RedrawAfterTheme()
    {
        Map.InvalidateVisual();
        Profile.InvalidateVisual();
        RulerTop.InvalidateVisual();
        RulerLeft.InvalidateVisual();
    }
}
