using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using MapEditor.Map;

namespace MapEditor;

/// Eintrag der Gebäudeliste: eine Gebäudeart mit allen Exemplaren auf der Karte
public sealed class PrefabEntry
{
    public string Name { get; init; }
    public string Label { get; init; }
    public PrefabCategory Category { get; init; }
    public string File { get; init; }   // Beschreibung (XML) im Spielordner; Vorschaubild daneben als .jpg
    public List<PlacedPrefab> Instances { get; set; } = new();
    public string Icon => Category.Icon;
    /// Platzieren (auch Stadt-Kacheln, im 150-m-Raster)
    public bool CanPlace => true;
    /// Schwierigkeit (DifficultyTier der Gebäudebeschreibung), 0 = keine Angabe
    public int Tier { get; set; }
    public string TierText => PrefabDef.Stars(Tier);
    public string TierToolTip => Tier > 0 ? Loc.F("Tier {0}", Tier) : null;
    public string Display => string.IsNullOrEmpty(Label) ? Name : $"{Label} ({Name})";
    /// Straßenanschlüsse einer Kachel in Metern (z. B. "16 m · 8 m"), sonst null
    public string ExitsText { get; set; }
    public string ExitsToolTip => ExitsText == null ? null : Loc.T("Width of the road at each connection of this city tile");
    public string CountText => Instances.Count > 0 ? $"{Instances.Count}×" : "";

    /// Tags aus der Beschreibung (z. B. countrytown, nodiagonal): Viertel, in denen das Spiel das Gebäude baut
    public IEnumerable<string> Tags { get; set; } = Array.Empty<string>();
    (string Code, string Tip, string Biomes, string BiomesTip)? rwg;
    bool rwgRead;
    // Wie das Spiel es baut (Wunsch des Nutzers 5.11.2026): Kacheln = Viertel aus dem Namen, Gebäude = Viertel aus den Tags
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
    /// Baut das Spiel dies im Biom mit diesem Kürzel (F, B, D, S, W)? Ohne Viertel: nein
    public bool BuiltIn(string biomeLetter) => BiomeText != null && (BiomeText == "*" || BiomeText.Contains(biomeLetter));
}

/// Rechte Seite (Wunsch des Nutzers): Symbole für die Gruppen, Vorschau mit Bild, Liste aller Gebäude
public partial class MainWindow
{
    List<PrefabEntry> mapEntries = new(), gameEntries;   // auf der Karte / alle aus dem Spiel (erst bei Bedarf)
    Dictionary<string, string> prefabFiles;              // Name → XML im Spielordner
    readonly Dictionary<string, PrefabDef> describedDefs = new(StringComparer.OrdinalIgnoreCase);
    string categoryKey;   // null = alle Gruppen außer Teilen
    PrefabEntry shownEntry;


    string GameFolder => settings.GameFolder ?? AppSettings.GuessGameFolder();

    // Filter nach Biom (nur Anzeige, nicht gespeichert): Kürzel wie in der Spalte „Biom“
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

    /// Nach dem Laden einer Welt: Liste der Gebäude auf der Karte (und, falls gewählt, aller aus dem Spiel)
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

    // alle Gebäude aus Spiel und Mods; die auf der Karte behalten ihre Exemplare
    async Task LoadGameEntries()
    {
        if (gameEntries != null || prefabFiles == null) return;
        string game = GameFolder;
        var files = prefabFiles;
        var onMap = mapEntries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        PrefabCountText.Text = Loc.T("Reading buildings from the game …");
        var labels = await Task.Run(() => PrefabCatalog.Labels(game, files.Keys));
        // Schwierigkeit für die Liste: aus jeder Beschreibung lesen (einmal, im Hintergrund)
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

    /// Anschlussbreiten der Kachelarten in der Liste eintragen (im Hintergrund gelesen, je Kachelart einmal)
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

    // Sortieren über die Spaltenköpfe Stufe / Name / Anzahl (Wunsch des Nutzers 31.10.2026); nochmal klicken = umgekehrt.
    // Bei gleicher Stufe oder Anzahl immer nach Name; gemerkt in den Einstellungen.
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
        else { settings.PrefabSort = key; settings.PrefabSortDescending = key != "name"; }   // Stufe/Anzahl: erst die höchsten
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

    // Klick auf einen Eintrag: Fenster „Gebäude“ mit seinen Kopien öffnen; die Karte springt nicht (Wunsch des Nutzers)
    void PrefabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrefabList.SelectedItem is not PrefabEntry entry || selectingFromMap) return;
        shownEntry = entry;
        ShowBuildingWindow(entry, null);
    }

    bool selectingFromMap;

    // Klick auf ein Gebäude in der Karte: in der Liste auswählen (Gruppe und Suche passend umstellen) und im Fenster zeigen
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

    // Beschreibung zu einem Eintrag (von der Karte oder aus der XML im Spielordner)
    PrefabDef DescribeEntry(PrefabEntry entry)
    {
        var def = entry.Instances.FirstOrDefault()?.Def;
        if (def == null && entry.File != null && !describedDefs.TryGetValue(entry.Name, out def))
            describedDefs[entry.Name] = def = PrefabCatalog.Describe(entry.File);
        return def;
    }

    // Kopie blau markieren (Wunsch des Nutzers); liegt sie außerhalb der Ansicht, wird die Karte dorthin geschoben (ohne Zoom)
    void MarkCopy(PlacedPrefab p, bool center)
    {
        Map.Selected = p;
        if (!center || p == null) return;
        double cx = p.X + p.SizeX / 2.0, cz = p.Z + p.SizeZ / 2.0;
        bool visible = cx >= Map.ToWorldX(0) && cx <= Map.ToWorldX(Map.ActualWidth) && cz <= Map.ToWorldZ(0) && cz >= Map.ToWorldZ(Map.ActualHeight);
        if (!visible) Map.CenterOn(cx, cz);
    }

    // rechte Seite nach rechts einklappen (Wunsch des Nutzers 4.11.2026): nur ein schmaler Streifen bleibt, gemerkt
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

    // rechte Seite in der Breite ziehen (Wunsch des Nutzers), 260–900 px, gemerkt
    void RightPanelThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        RightPanel.Width = Math.Clamp(RightPanel.Width - e.HorizontalChange, 260, Math.Max(260, Math.Min(900, ActualWidth - 600)));

    void RightPanelThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        settings.RightPanelWidth = RightPanel.Width;
        settings.Save();
    }
}
