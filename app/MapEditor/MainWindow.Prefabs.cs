using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using MapEditor.Map;

namespace MapEditor;

/// entry of the building list: one building type with all copies on the map
public sealed class PrefabEntry
{
    public string Name { get; init; }
    public string Label { get; init; }
    public PrefabCategory Category { get; init; }
    public string File { get; init; }   // description (XML) in the game folder; preview image beside it as .jpg
    public List<PlacedPrefab> Instances { get; set; } = new();
    public string Icon => Category.Icon;
    /// place (also city tiles, in the 150 m grid)
    public bool CanPlace => true;
    /// difficulty (DifficultyTier of the building description), 0 = no information
    public int Tier { get; set; }
    public string TierText => PrefabDef.Stars(Tier);
    public string TierToolTip => Tier > 0 ? Loc.F("Tier {0}", Tier) : null;
    public string Display => string.IsNullOrEmpty(Label) ? Name : $"{Label} ({Name})";
    /// road connections of a tile in meters (e.g. "16 m · 8 m"), otherwise null
    public string ExitsText { get; set; }
    public string ExitsToolTip => ExitsText == null ? null : Loc.T("Width of the road at each connection of this city tile");
    public string CountText => Instances.Count > 0 ? $"{Instances.Count}×" : "";

    /// tags from the description (e.g. countrytown, nodiagonal): districts in which the game builds the building
    public IEnumerable<string> Tags { get; set; } = Array.Empty<string>();
    (string Code, string Tip, string Biomes, string BiomesTip)? rwg;
    bool rwgRead;
    // how the game builds it (the user's request, 2026-11-05): tiles = district from the name, buildings = district from the tags
    (string Code, string Tip, string Biomes, string BiomesTip)? Rwg
    {
        get
        {
            if (!rwgRead)
            {
                rwg = RwgRules.Summarize(Category.Key == "tile" ? new[] { RwgRules.DistrictOfTile(Name) } : Tags);
                rwgRead = true;
            }
            return rwg;
        }
    }
    public string BiomeText => Rwg?.Biomes;
    public string BiomeToolTip => Rwg?.Tip;
    /// does the game build this in the biome with this abbreviation (F, B, D, S, W)? Without district: no
    public bool BuiltIn(string biomeLetter) => BiomeText != null && (BiomeText == "*" || BiomeText.Contains(biomeLetter));
}

/// right side (the user's request): icons for the groups, preview with image, list of all buildings
public partial class MainWindow
{
    List<PrefabEntry> mapEntries = new(), gameEntries;   // on the map / all from the game (only when needed)
    Dictionary<string, string> prefabFiles;              // name → XML in the game folder
    readonly Dictionary<string, PrefabDef> describedDefs = new(StringComparer.OrdinalIgnoreCase);
    string categoryKey;   // null = all groups except parts
    PrefabEntry shownEntry;


    string GameFolder => settings.GameFolder ?? AppSettings.GuessGameFolder();

    // filter by biome (display only, not saved): abbreviation like in the column "Biome"
    string biomeFilter;

    void FillBiomeFilter()
    {
        var items = new (string Key, string Title)[]
        {
            (null, Loc.T("All biomes")), ("F", Loc.T("Pine forest")), ("B", Loc.T("Burnt forest")),
            ("D", Loc.T("Desert")), ("S", Loc.T("Snow")), ("W", Loc.T("Wasteland")),
        };
        BiomeFilterBox.SelectionChanged -= BiomeFilter_Changed;
        BiomeFilterBox.Items.Clear();
        foreach (var (key, title) in items) BiomeFilterBox.Items.Add(new ComboBoxItem { Content = title, Tag = key });
        BiomeFilterBox.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.Key == biomeFilter));
        BiomeFilterBox.SelectionChanged += BiomeFilter_Changed;
    }

    void BiomeFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        biomeFilter = (BiomeFilterBox.SelectedItem as ComboBoxItem)?.Tag as string;
        RefreshPrefabList();
    }

    void BuildCategoryBar()
    {
        FillBiomeFilter();
        CategoryBar.Children.Clear();
        AddCategoryButton(null, Icons.StarFill,Loc.T("All groups (without parts)"));
        foreach (var c in PrefabCategories.All) AddCategoryButton(c.Key, c.Icon, Loc.T(c.Title));
        UpdateCategoryButtons();
    }

    void AddCategoryButton(string key, string icon, string title)
    {
        var b = new ToggleButton { Content = icon, ToolTip = title, Tag = key, Style = (Style)FindResource("CategoryButton") };
        b.Click += (_, _) => { categoryKey = key; UpdateCategoryButtons(); RefreshPrefabList(); };
        CategoryBar.Children.Add(b);
    }

    void UpdateCategoryButtons()
    {
        var source = CurrentSource();
        foreach (ToggleButton b in CategoryBar.Children)
        {
            var key = b.Tag as string;
            b.IsChecked = key == categoryKey;
            var c = key == null ? null : PrefabCategories.All.First(x => x.Key == key);
            int kinds = source.Count(e => key == null ? e.Category.Key != "part" : e.Category.Key == key);
            b.ToolTip = (c != null ? Loc.T(c.Title) : Loc.T("All groups (without parts)")) + " – " + Loc.F("{0} kinds", kinds);
        }
    }

    List<PrefabEntry> CurrentSource() => AllPrefabsCheck.IsChecked == true && gameEntries != null ? gameEntries : mapEntries;

    /// after loading a world: list of the buildings on the map (and, if chosen, all from the game)
    async Task LoadPrefabList()
    {
        string game = GameFolder;
        var w = world;
        RwgRules.Load(game);
        prefabFiles ??= await Task.Run(() => game == null ? new Dictionary<string, string>() : PrefabCatalog.AllFiles(game));
        mapEntries = w.Prefabs.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PrefabEntry
            {
                Name = g.First().Name,
                Label = g.First().Def?.Label,
                Tier = g.First().Def?.Tier ?? 0,
                Tags = g.First().Def?.Tags.ToList() ?? new List<string>(),
                Category = PrefabCategories.Of(g.Key),
                File = prefabFiles.GetValueOrDefault(g.Key),
                Instances = g.ToList(),
            })
            .ToList();
        await FillTileExits(mapEntries);
        gameEntries = null;
        shownEntry = null;
        HideDetails();
        if (AllPrefabsCheck.IsChecked == true) await LoadGameEntries();
        UpdateCategoryButtons();
        RefreshPrefabList();
    }

    // all buildings from game and mods; those on the map keep their copies
    async Task LoadGameEntries()
    {
        if (gameEntries != null || prefabFiles == null) return;
        string game = GameFolder;
        var files = prefabFiles;
        var onMap = mapEntries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        PrefabCountText.Text = Loc.T("Reading buildings from the game …");
        var labels = await Task.Run(() => PrefabCatalog.Labels(game, files.Keys));
        // difficulty for the list: read from every description (once, in the background)
        var described = await Task.Run(() => files.Where(f => !onMap.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => PrefabCatalog.Describe(f.Value), StringComparer.OrdinalIgnoreCase));
        gameEntries = files.Select(f => onMap.TryGetValue(f.Key, out var m) ? m : new PrefabEntry
        {
            Name = f.Key,
            Label = labels.GetValueOrDefault(f.Key),
            Category = PrefabCategories.Of(f.Key),
            File = f.Value,
            Tier = described.GetValueOrDefault(f.Key)?.Tier ?? 0,
            Tags = described.GetValueOrDefault(f.Key)?.Tags.ToList() ?? new List<string>(),
        }).ToList();
        await FillTileExits(gameEntries);
    }

    /// enter the connection widths of the tile types into the list (read in the background, once per tile type)
    async Task FillTileExits(List<PrefabEntry> entries)
    {
        var w = world;
        if (w == null) return;
        var todo = entries.Where(e => e.Category.Key == "tile" && e.ExitsText == null).Select(e => (Entry: e, Def: DefFor(e))).Where(x => x.Def != null && x.Def.Found).ToList();
        if (todo.Count == 0) return;
        var found = await Task.Run(() => todo.Select(x => (x.Entry, Widths: w.TileExitWidthsOf(x.Entry.Name, x.Def))).ToList());
        foreach (var (entry, widths) in found)
            entry.ExitsText = string.Join(" · ", widths.GroupBy(v => v).Select(g => g.Count() > 1 ? Loc.F("{0} × {1} m", g.Count(), g.Key) : Loc.F("{0} m", g.Key)));
    }

    async void AllPrefabsCheck_Click(object sender, RoutedEventArgs e)
    {
        settings.ListAllPrefabs = AllPrefabsCheck.IsChecked == true;
        settings.Save();
        if (settings.ListAllPrefabs && world != null) await LoadGameEntries();
        UpdateCategoryButtons();
        RefreshPrefabList();
    }

    void PrefabSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshPrefabList();

    void RefreshPrefabList()
    {
        string q = PrefabSearch.Text.Trim();
        var found = CurrentSource()
            .Where(e => categoryKey == null ? e.Category.Key != "part" : e.Category.Key == categoryKey)
            .Where(e => biomeFilter == null || e.BuiltIn(biomeFilter))
            .Where(e => q == "" || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (e.Label?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        var list = SortPrefabs(found).ToList();
        UpdateSortHeads();
        PrefabList.ItemsSource = list;
        PrefabCountText.Text = Loc.F("{0} kinds, {1} of them on the map ({2} pieces)", list.Count, list.Count(e => e.Instances.Count > 0), list.Sum(e => e.Instances.Count));
        if (shownEntry != null && list.Contains(shownEntry)) PrefabList.SelectedItem = shownEntry;
    }

    // sorting via the column headers Tier / Name / Count (the user's request, 2026-10-31); click again = reversed.
    // With equal tier or count always by name; remembered in the settings.
    IEnumerable<PrefabEntry> SortPrefabs(IEnumerable<PrefabEntry> items)
    {
        bool desc = settings.PrefabSortDescending;
        var byName = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<PrefabEntry> sorted = settings.PrefabSort switch
        {
            "tier" => desc ? items.OrderByDescending(e => e.Tier) : items.OrderBy(e => e.Tier),
            "count" => desc ? items.OrderByDescending(e => e.Instances.Count) : items.OrderBy(e => e.Instances.Count),
            _ => desc ? items.OrderByDescending(e => e.Display, byName) : items.OrderBy(e => e.Display, byName),
        };
        return settings.PrefabSort is "tier" or "count" ? sorted.ThenBy(e => e.Display, byName) : sorted;
    }

    void SortHead_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        string key = (string)((FrameworkElement)sender).Tag;
        if ((settings.PrefabSort ?? "name") == key) settings.PrefabSortDescending = !settings.PrefabSortDescending;
        else { settings.PrefabSort = key; settings.PrefabSortDescending = key != "name"; }   // tier/count: highest first
        settings.Save();
        RefreshPrefabList();
    }

    void UpdateSortHeads()
    {
        string key = settings.PrefabSort ?? "name"; bool desc = settings.PrefabSortDescending;
        IconUi.SetSortHead(SortTierHead, Loc.T("Tier"), key == "tier", desc);
        IconUi.SetSortHead(SortNameHead, Loc.T("Name"), key == "name", desc);
        IconUi.SetSortHead(SortCountHead, Loc.T("Count"), key == "count", desc);
    }

    // click on an entry: open the window "Building" with its copies; the map does not jump (the user's request)
    void PrefabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrefabList.SelectedItem is not PrefabEntry entry || selectingFromMap) return;
        shownEntry = entry;
        ShowBuildingWindow(entry, null);
    }

    bool selectingFromMap;

    // click on a building in the map: select it in the list (set group and search accordingly) and show it in the window
    void SelectFromMap(PlacedPrefab p)
    {
        var entry = CurrentSource().FirstOrDefault(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            if (!((IEnumerable<PrefabEntry>)PrefabList.ItemsSource ?? Array.Empty<PrefabEntry>()).Contains(entry))
            {
                categoryKey = entry.Category.Key == "part" || entry.Category == PrefabCategories.Other ? entry.Category.Key : null;
                PrefabSearch.Text = "";
                UpdateCategoryButtons();
                RefreshPrefabList();
            }
            selectingFromMap = true;
            try { PrefabList.SelectedItem = entry; shownEntry = entry; }
            finally { selectingFromMap = false; }
            PrefabList.ScrollIntoView(entry);
        }
        ShowPrefabDetails(p);
    }

    // description for an entry (from the map or from the XML in the game folder)
    PrefabDef DescribeEntry(PrefabEntry entry)
    {
        var def = entry.Instances.FirstOrDefault()?.Def;
        if (def == null && entry.File != null && !describedDefs.TryGetValue(entry.Name, out def))
            describedDefs[entry.Name] = def = PrefabCatalog.Describe(entry.File);
        return def;
    }

    // mark the copy blue (the user's request); if it lies outside the view, the map is moved there (without zoom)
    void MarkCopy(PlacedPrefab p, bool center)
    {
        Map.Selected = p;
        if (!center || p == null) return;
        double cx = p.X + p.SizeX / 2.0, cz = p.Z + p.SizeZ / 2.0;
        bool visible = cx >= Map.ToWorldX(0) && cx <= Map.ToWorldX(Map.ActualWidth) && cz <= Map.ToWorldZ(0) && cz >= Map.ToWorldZ(Map.ActualHeight);
        if (!visible) Map.CenterOn(cx, cz);
    }

    // collapse the right side to the right (the user's request, 2026-11-04): only a narrow strip remains, remembered
    void RightPanelToggle_Click(object sender, RoutedEventArgs e)
    {
        settings.RightPanelCollapsed = !settings.RightPanelCollapsed;
        settings.Save();
        ApplyRightPanel();
    }

    void ApplyRightPanel()
    {
        bool folded = settings.RightPanelCollapsed;
        RightPanelFull.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        RightPanelStrip.Visibility = folded ? Visibility.Visible : Visibility.Collapsed;
        RightPanel.Width = folded ? 36 : Math.Clamp(settings.RightPanelWidth, 260, 900);
    }

    // drag the width of the right side (the user's request), 260–900 px, remembered
    void RightPanelThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        RightPanel.Width = Math.Clamp(RightPanel.Width - e.HorizontalChange, 260, Math.Max(260, Math.Min(900, ActualWidth - 600)));

    void RightPanelThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        settings.RightPanelWidth = RightPanel.Width;
        settings.Save();
    }
}
