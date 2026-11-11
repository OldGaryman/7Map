using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MapEditor.Controls;
using MapEditor.Map;
using Microsoft.Win32;

namespace MapEditor;

public partial class MainWindow : Window
{
    static readonly CultureInfo Num = Loc.Num;   // Zahlen immer englisch (1,234.5), Wunsch des Nutzers: App für alle
    readonly AppSettings settings = AppSettings.Load();
    readonly StepsWindow stepsWindow = new();
    WorldData world;
    WorldEdits edits;   // Änderungen, die nur im Programm sind (bis „Speichern“)
    RoadSettings road = RoadSettings.Asphalt();
    double eraserSize = 32;
    const double EraserRim = 2;   // Rand neben dem Belag, der beim Radieren mit angeglichen wird (m)
    double asphaltWidth = 16, asphaltShoulder = 1, gravelWidth = 5, asphaltGrass = 0, gravelGrass = 0;   // Werte der linken Seite
    double asphaltTol = 0.02, gravelTol = 0.02;                       // Kreuzung eben bis (Anteil)
    double asphaltCross = 0.04, gravelCross = 0.06;                   // Querneigung bis (Anteil)
    int eraserStart;   // Anzahl Schritte beim Einschalten des Radiergummis (Abbrechen nimmt die Striche danach zurück)
    bool closeConfirmed;

    static string HelpIdle => Loc.T("Mouse wheel: zoom · drag with left button: move · click a building: show it in the list");
    static string HelpErase => Loc.T("Eraser: drag left = erase · drag right = move · mouse wheel = zoom · Esc = cancel");
    static string HelpEdit => Loc.T("Editing: click = point · drag a point = move · right-click = delete · Esc = cancel");

    public MainWindow()
    {
        InitializeComponent();
        Loc.Translate(this);
        Map.PointerMoved += ShowPointer;
        Map.RoadPointsChanged += finished => { if (roadEditMode) RoadEditChanged(finished); else Recompute(); };
        InitRoadEdit();
        Map.AreaChanged += () =>
        {
            if (Map.Area.Count == 0) riverConverted = false;
            Map.OpenArea = waterMode && waterKind == 2 && !riverConverted;
            UpdateToolModule();
        };
        Map.StrokeFinished += stroke => { if (biomeMode) PaintBiomeStroke(stroke); else if (waterMode && waterKind == 3) FillCraterStroke(stroke); else if (rangeMode) _ = PlaceRange(stroke); else if (mountainMode) PlaceMountain(stroke); else if (terrainMode) PaintTerrainStroke(stroke); else EraseStroke(stroke); };
        Map.PrefabClicked += SelectFromMap;
        Map.LotClicked += BuildOnLot;
        Map.SelectedPointChanged += UpdatePointRow;
        Map.ScaleChanged += ShowZoom;
        ZoomBox.ItemsSource = ZoomLevels.Select(z => $"{z} %").ToList();
        Profile.HoverChanged += d => Map.ProfileMarker = d;
        ShowToolValues(); ShowEditValues();
        ShowEraserValues();
        ShowBiomeValues();
        ShowTerrainValues();
        ShowMountainValues();
        ShowRangeValues();
        ShowWaterValues();
        BuildCategoryBar();
        AllPrefabsCheck.IsChecked = settings.ListAllPrefabs;
        InitSpawns();
        InitRibbonOptions();
        InitPrefabEdit();
        ShowVersionInfo();

        // Bearbeitungsreihenfolge als eigenes kleines Fenster (Wunsch des Nutzers)
        stepsWindow.UndoButton.Click += UndoButton_Click;
        stepsWindow.RedoButton.Click += RedoButton_Click;
        stepsWindow.Hidden += () => { StepsToggle.IsChecked = false; settings.ShowSteps = false; settings.Save(); };
        UpdateButtons();
        Loaded += async (_, _) =>
        {
            stepsWindow.Owner = this;
            stepsWindow.Left = Left + ActualWidth - 380 - stepsWindow.Width - 30;
            stepsWindow.Top = Top + 90;
            SetStepsVisible(settings.ShowSteps);
            if (settings.FirstStart)
            {
                // erster Start auf diesem PC: zeigen, was gefunden wurde (Wunsch des Nutzers 30.10.2026)
                new WelcomeWindow(settings) { Owner = this }.ShowDialog();
                settings.Save();   // auch nach Schließen mit X: beim nächsten Start nicht noch einmal fragen
                if ((settings.Language ?? Loc.English) != Loc.Language) await ApplyLanguage(settings.Language);
            }
            await OpenWorld(settings.WorldFolder ?? AppSettings.GuessWorldFolder());
        };
    }

    // ---------- Welt ----------

    async Task OpenWorld(string folder)
    {
        if (folder == null || !File.Exists(Path.Combine(folder, "dtm.raw")))
        {
            LoadingText.Text = Loc.T("No world is loaded. Choose one of these:\n• \"Open project …\" – open an existing project (folder with dtm.raw, splat3.png, prefabs.xml …)\n• \"New project\" – create a project from an existing map of the game\n• Creation > \"World\" – generate a completely new world");
            Overlay.Visibility = Visibility.Visible;
            return;
        }
        Overlay.Visibility = Visibility.Visible;
        await CheckExternalChanges(folder);
        LoadingText.Text = Loc.T("Loading world …");
        OpenButton.IsEnabled = false;
        var progress = new Progress<string>(s => LoadingText.Text = s);
        string game = settings.GameFolder ?? AppSettings.GuessGameFolder();
        try
        {
            // alte Welt vorher freigeben, sonst liegen beim Neuladen beide im Speicher
            world = null;
            edits = null;
            roadNetWindow?.Close();   // Netz-Vorschau gehört zur alten Welt
            Map.Clear();
            MemoryTools.Release();
            var (w, l) = await Task.Run(() =>
            {
                var w = WorldData.Load(folder, game, progress);
                return (w, MapLayers.Build(w, progress));
            });
            world = w;
            edits = new WorldEdits(w);
            w.UseTestSpawn = settings.DeployTestSpawn;
            Map.SetWorld(w, l);
            ClearRoad();
            ShowWorldInfo();
            settings.WorldFolder = folder;
            settings.Save();
            Overlay.Visibility = Visibility.Collapsed;
            MemoryTools.Release();
            await LoadPrefabList();
            // Anschlüsse aus dem Belag der Gebäude (Fangpunkte ohne RoadExit): alle Blockdateien einmal im Hintergrund lesen (ca. 3 s)
            await Task.Run(() => w.EdgeExitsWarmUp());
            if (w == world) Map.RebuildExits();
        }
        catch (Exception ex)
        {
            LoadingText.Text = Loc.T("The world could not be loaded:") + "\n" + ex.Message;
        }
        finally
        {
            OpenButton.IsEnabled = true;
            UpdateButtons();
            UpdateUndo();
        }
    }

    void ShowWorldInfo()
    {
        if (world == null) return;
        WorldName.Text = Loc.F("{0}  ({1} km, {2} buildings/parts)", world.Name, world.Size / 1024, world.Prefabs.Count);
        WorldFolderText.Text = Loc.F("Working copy: {0}", world.Folder);
        ShowVersionInfo();
    }

    string gameVersionText;   // Spielversion aus dem Protokoll, einmal gelesen

    /// "V.2.6.14" (map_info.xml) -> "V 2.6 (b14)" wie im Spielprotokoll
    static string MapVersionText(string folder)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(folder, "map_info.xml")),
                "name=\"GameVersion\"\\s+value=\"V\\.?(\\d+)\\.(\\d+)\\.(\\d+)\"");
            return m.Success ? $"V {m.Groups[1].Value}.{m.Groups[2].Value} (b{m.Groups[3].Value})" : null;
        }
        catch (Exception) { return null; }
    }

    /// Spielversion, Version der Karte und Mod (Wunsch des Nutzers 5.11.2026): nach einem Spiel-Update können alte Karten vorkommen
    void ShowVersionInfo()
    {
        gameVersionText ??= GameLocator.VersionFromLog() ?? "";
        string mapVersion = world == null ? null : MapVersionText(world.Folder);
        string mod = null;
        if (world != null)
        {
            string game = settings.GameWorlds.TryGetValue(world.Name, out var dir) && Directory.Exists(dir) ? dir : GameSync.GuessGameWorld(world.Name);
            // ...\7DaysToDie\<Mod>\GeneratedWorlds\<Welt>: Mod-Ordner, sonst direkt unter 7DaysToDie
            string modDir = game == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(game));
            if (modDir != null && string.Equals(Path.GetFileName(Path.GetDirectoryName(modDir)), "7DaysToDie", StringComparison.OrdinalIgnoreCase))
                mod = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(modDir), "(?<=[a-z])(?=[A-Z])", " ");
        }
        var parts = new List<string>();
        if (gameVersionText != "") parts.Add(Loc.F("Game {0}", gameVersionText));
        if (mapVersion != null) parts.Add(Loc.F("Map {0}", mapVersion));
        if (mod != null) parts.Add(Loc.F("Mod: {0}", mod));
        VersionInfo.Text = string.Join("  ·  ", parts);
        bool differs = mapVersion != null && gameVersionText != "" && mapVersion != gameVersionText;
        VersionInfo.Foreground = differs ? System.Windows.Media.Brushes.DarkOrange : (System.Windows.Media.Brush)FindResource("HintTextBrush");
        string tip = (differs ? Loc.T("The map was made with a different game version than the one installed. Load it in the game once and check it.") + "\n" : "")
            + (mod != null ? Loc.F("This map comes from the mod {0}. The mod must be active in the game for it to work.", mod) : "");
        VersionInfo.ToolTip = tip.Trim() == "" ? null : tip.Trim();
    }

    MapPointer lastPointer;   // zuletzt gemeldete Mausposition (null = Maus nicht über der Karte)

    void ShowPointer(MapPointer p)
    {
        lastPointer = p;
        if (p == null || world == null) { PositionText.Text = ""; PrefabText.Text = ""; return; }
        int water = Math.Abs(p.X) < world.Half && Math.Abs(p.Z) < world.Half ? world.WaterMask[world.Index(p.X, p.Z)] : 0;
        PositionText.Text = water > 0
            ? Loc.F("{0}, {1}   height {2:0.0} m   water {4} m   {3}", p.X, p.Z, world.HeightAt(p.X, p.Z), Loc.T(BiomePalette.Names[world.BiomeAt(p.X, p.Z)]), water)
            : Loc.F("{0}, {1}   height {2:0.0} m   {3}", p.X, p.Z, world.HeightAt(p.X, p.Z), Loc.T(BiomePalette.Names[world.BiomeAt(p.X, p.Z)]));
        if (p.Prefab == null) { PrefabText.Text = ""; return; }
        var f = p.Prefab;
        string label = string.IsNullOrEmpty(f.Def.Label) ? "" : f.Def.Label + "  ·  ";
        string tier = f.Def.Tier > 0 ? "  ·  " + Loc.F("Tier {0}", f.Def.Tier) : "";
        string kind = f.Def.Kind switch { PrefabKind.Part => Loc.T("Part") + ": ", PrefabKind.Tile => Loc.T("City tile") + ": ", _ => "" };
        PrefabText.Text = kind + label + f.Name + "  ·  " + Loc.F("at {0},{1},{2}", f.X, f.Y, f.Z) + "  ·  " + Loc.F("rotation {0}", f.Rotation) + $"  ·  {f.SizeX} × {f.SizeZ} m{tier}";
    }

    async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmSwitch(null)) return;   // unfertige Straße oder Kette: erst fragen
        var dlg = new OpenFolderDialog { Title = Loc.T("Open project: choose the folder of the working copy (with dtm.raw)") };
        dlg.InitialDirectory = world != null ? Path.GetDirectoryName(world.Folder) : Directory.Exists(settings.ProjectPath) ? settings.ProjectPath : null;
        if (dlg.ShowDialog(this) == true && await ConfirmUnsaved(Loc.T("Open project"))) await OpenWorld(dlg.FolderName);
    }

    async void NewProjectButton_Click(object sender, RoutedEventArgs e) { if (await ConfirmSwitch(null)) await LoadFromGame(); }

    // ---------- Einstellungen und Welt aus dem Spiel laden ----------

    async void SettingsButton_Click(object sender, RoutedEventArgs e) => await OpenSettings(1);

    /// Einstellungsfenster; page = Seite (0 Pfade, 1 Info und Sprache), jede in einem eigenen Fenster
    async Task OpenSettings(int page)
    {
        var dlg = new SettingsWindow(settings, Math.Max(0, page)) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ShowToolValues();
        ShowEraserValues();
        ShowBiomeValues();
        ShowTerrainValues();
        ShowMountainValues();
        ShowRangeValues();
        ShowWaterValues();
        if ((settings.Language ?? Loc.English) != Loc.Language) await ApplyLanguage(settings.Language);
    }

    /// Sprache sofort wechseln (Wunsch des Nutzers): feste Texte der Fenster neu übersetzen, alles vom Code Geschriebene neu
    /// schreiben, Gebäudenamen aus dem Spiel in der neuen Sprache lesen
    async Task ApplyLanguage(string language)
    {
        Loc.Load(language ?? Loc.English);
        Loc.Retranslate(this);
        Loc.Retranslate(stepsWindow);
        Loc.Retranslate(spawnWindow);
        Loc.Retranslate(prefabWindow);
        BuildBiomeBox();
        ShowWorldInfo();
        ShowTerrainStrength();
        HelpText.Text = Map.SpawnMode != null ? SpawnHelp(Map.SpawnMode.Value) : Map.EraseMode ? HelpBrushText : Map.EditMode ? HelpEdit : HelpIdle;

        PositionText.Text = PrefabText.Text = "";
        UpdateButtons();
        UpdateUndo();
        UpdateToolModule();
        UpdatePointRow();
        if (Map.RoadPoints.Count > 0) Recompute();
        Profile.InvalidateVisual();
        BuildCategoryBar();
        if (world == null) return;
        string game = GameFolder;
        var names = world.Prefabs.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var labels = await Task.Run(() => game == null ? new Dictionary<string, string>() : PrefabCatalog.Labels(game, names));
        foreach (var p in world.Prefabs.Where(p => p.Def != null)) p.Def.Label = labels.GetValueOrDefault(p.Name) ?? "";
        await LoadPrefabList();
    }

    // Radiergummi links aus den gespeicherten Standardwerten (Einstellungen, Reiter „Radiergummi“)
    void ShowEraserValues()
    {
        eraserSize = settings.EraserSize;
        BrushSizeBox.Text = Fmt(eraserSize);
        BrushSquareRadio.IsChecked = settings.EraserSquare;
        BrushRoundRadio.IsChecked = !settings.EraserSquare;
        BumpsBox.SelectedIndex = Math.Clamp(settings.EraserBumps, 0, 3);
        ApplyBrush();
    }

    // Breite und Schotterrand links aus den gespeicherten Straßenwerten
    void ShowToolValues()
    {
        var a = settings.RoadFor(Surface.Asphalt);
        var g = settings.RoadFor(Surface.Gravel);
        asphaltWidth = a.Width; asphaltShoulder = a.Shoulder; gravelWidth = g.Width;
        asphaltGrass = a.GrassEdge; gravelGrass = g.GrassEdge;
        asphaltTol = a.JunctionTolerance; gravelTol = g.JunctionTolerance;
        asphaltCross = a.MaxCrossSlope; gravelCross = g.MaxCrossSlope;
        AsphaltWidthBox.Text = Fmt(asphaltWidth);
        AsphaltShoulderBox.Text = Fmt(asphaltShoulder);
        AsphaltGrassBox.Text = Fmt(asphaltGrass); GravelGrassBox.Text = Fmt(gravelGrass);
        GravelWidthBox.Text = Fmt(gravelWidth);
        AsphaltGradeBox.Text = Fmt(a.MaxGrade * 100); GravelGradeBox.Text = Fmt(g.MaxGrade * 100);
        AsphaltSmoothBox.Text = Fmt(a.Smooth); GravelSmoothBox.Text = Fmt(g.Smooth);
        AsphaltBlendBox.Text = Fmt(a.Blend); GravelBlendBox.Text = Fmt(g.Blend);
        AsphaltCutBox.Text = Fmt(a.CutSlope); GravelCutBox.Text = Fmt(g.CutSlope);
        AsphaltTolBox.Text = Fmt(a.JunctionTolerance * 100); GravelTolBox.Text = Fmt(g.JunctionTolerance * 100);
        AsphaltCrossBox.Text = Fmt(a.MaxCrossSlope * 100); GravelCrossBox.Text = Fmt(g.MaxCrossSlope * 100);
        foreach (var b in new[] { AsphaltGradeBox, GravelGradeBox, AsphaltSmoothBox, GravelSmoothBox, AsphaltBlendBox, GravelBlendBox, AsphaltCutBox, GravelCutBox, AsphaltTolBox, GravelTolBox, AsphaltCrossBox, GravelCrossBox })
            b.ClearValue(BorderBrushProperty);
    }

    /// Welt aus GeneratedWorlds wählen, Pfad merken, Kopie im Projektordner anlegen und diese öffnen
    async Task LoadFromGame()
    {
        if (!await ConfirmUnsaved(Loc.T("Load world"))) return;
        var dlg = new OpenFolderDialog { Title = Loc.T("Choose the world in the game (folder in GeneratedWorlds)") };
        string generated = AppSettings.GuessGeneratedWorlds();
        if (generated != null) dlg.InitialDirectory = generated;
        if (dlg.ShowDialog(this) != true) return;
        string src = dlg.FolderName.TrimEnd('\\');
        if (!File.Exists(Path.Combine(src, "dtm.raw")))
        {
            MessageBox.Show(this, Loc.T("There is no world in this folder (dtm.raw is missing). Please choose the folder of the world itself, e.g. GeneratedWorlds\\My World."),
                Loc.T("Load world"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string name = Path.GetFileName(src), dst = Path.Combine(settings.ProjectPath, name);
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) { await OpenWorld(dst); return; }
        if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any())
        {
            var a = MessageBox.Show(this, Loc.F("There already is a working copy \"{0}\" in the projects folder:\n{1}\n\n" +
                "Yes = open this working copy (with your previous changes)\n" +
                "No = replace it with a new copy from the game (the old one stays next to it as a backup)\nCancel = do nothing", name, dst),
                Loc.T("Load world"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (a == MessageBoxResult.Cancel) return;
            if (a == MessageBoxResult.Yes)
            {
                settings.GameWorlds[name] = src;
                settings.Save();
                await OpenWorld(dst);
                return;
            }
            // nichts löschen, nur beiseitelegen – samt den Sicherungen des alten Projekts, die sonst zur neuen Kopie gezählt würden
            string aside = dst + "_old_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            Directory.Move(dst, aside);
            Backups.MoveWithProject(dst, aside);
        }
        Overlay.Visibility = Visibility.Visible;
        LoadingText.Text = Loc.T("Copying the world into the projects folder …");
        IProgress<string> progress = new Progress<string>(t => LoadingText.Text = t);
        try
        {
            await Task.Run(() => CopyFolder(src, dst, progress));
        }
        catch (Exception ex)
        {
            Overlay.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, Loc.T("Copying failed:") + "\n" + ex.Message, Loc.T("Load world"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        settings.GameWorlds[name] = src;   // „Übernehmen in 7 Days to Die“ schreibt hierhin zurück
        settings.Save();
        await Task.Run(() => GameSync.RecordGameState(dst, src));   // Stand im Spiel = Stand der Kopie
        await OpenWorld(dst);
    }

    /// Welt im Spiel von außen geändert (Editor des Spiels, andere YAMEE)? Dann fragen, ob die Änderungen ins Projekt sollen
    /// (Wunsch des Nutzers 30.10.2026). Ja = Sicherung des Projekts, dann holen; Nein = Stand merken, nicht noch einmal fragen.
    async Task CheckExternalChanges(string folder)
    {
        string name = new DirectoryInfo(folder).Name;
        string game = settings.GameWorlds.TryGetValue(name, out var dir) && Directory.Exists(dir) ? dir : GameSync.GuessGameWorld(name);
        if (game == null) return;
        LoadingText.Text = Loc.T("Comparing with the world in the game …");
        List<string> changed;
        try { changed = await Task.Run(() => GameSync.ChangedInGame(folder, game)); }
        catch (Exception) { return; }   // Spiel hält Dateien offen o. Ä.: ohne Abgleich weiter
        if (changed.Count == 0) return;
        var answer = MessageBox.Show(this,
            Loc.F("The map data of \"{0}\" was changed outside of YAMEE (for example in the world editor of the game or by another YAMEE):\n{1}\n\nTake over these changes into the project? A backup of the project is made first.",
                name, string.Join(", ", changed)),
            Loc.T("Map changed externally"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        try
        {
            if (answer == MessageBoxResult.Yes)
            {
                LoadingText.Text = Loc.T("Taking over the changes from the game …");
                await Task.Run(() =>
                {
                    Backups.Create(folder, Loc.F("Before taking over external changes from the game: {0}", string.Join(", ", changed)));
                    GameSync.TakeOverFromGame(folder, game, changed);
                });
            }
            else await Task.Run(() => GameSync.RecordGameState(folder, game));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("Taking over failed:") + "\n" + ex.Message, Loc.T("Map changed externally"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// Kartenänderungen in einen bestehenden Spielstand übernehmen (Wunsch des Nutzers 30.10.2026); verglichen wird mit dem
    /// gespeicherten Projektstand, ungespeicherte Änderungen deshalb vorher speichern
    async void SaveGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (world == null) return;
        if (!await ConfirmSwitch(null)) return;
        if (edits?.HasChanges == true)
        {
            if (MessageBox.Show(this, Loc.T("There are unsaved changes. Save them to the project folder first?"), Loc.T("Copy into a save"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (!await Save()) return;
        }
        string game = GameWorld(ask: false);
        var dlg = new SaveWindow(world.Folder, world.Name, GameFolder, game, d => Map.ChunkMarks = d, async () =>
        {
            if (game == null) return false;
            try { await CopyMapToGame(game); return true; }
            catch (Exception) { return false; }
        })
        {
            Owner = this,
            Left = Left + 240,
            Top = Top + 90,
        };
        dlg.ShowDialog();
        Map.ChunkMarks = null;
    }

    /// Karte ins Spiel kopieren. Vorher merken Spielstände dieser Welt ohne Verweis den bisherigen Stand im Spiel – mit dem liefen
    /// sie (für „In Spielstand übernehmen“, ohne dass der Nutzer einen Vergleichsstand wählen muss).
    async Task<GameSync.DeployResult> CopyMapToGame(string game)
    {
        string project = world.Folder, name = world.Name;
        return await Task.Run(() =>
        {
            SaveUpdater.RememberGameStateForSaves(project, name, game);
            var r = GameSync.Deploy(project, game);
            GameSync.RecordGameState(project, game);
            return r;
        });
    }

    static void CopyFolder(string src, string dst, IProgress<string> progress)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
        {
            progress.Report(Loc.F("Copying: {0} …", Path.GetFileName(f)));
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        }
        // Spawnpunkte der Ausgangswelt aufheben (für „Originale zurückholen“)
        string spawns = Path.Combine(dst, WorldData.SpawnFile), original = Path.Combine(dst, WorldData.OriginalSpawnFile);
        if (File.Exists(spawns) && !File.Exists(original)) File.Copy(spawns, original);
        foreach (var d in Directory.GetDirectories(src)) CopyFolder(d, Path.Combine(dst, Path.GetFileName(d)), progress);
    }

    void Layer_Click(object sender, RoutedEventArgs e)
    {
        Map.ShowBiomes = ShowBiomes.IsChecked == true;
        BiomeLegend.Visibility = Map.ShowBiomes ? Visibility.Visible : Visibility.Collapsed;
        Map.ShowWater = ShowWater.IsChecked == true;
        Map.ShowRoads = ShowRoads.IsChecked == true;
        Map.ShowPrefabs = ShowPrefabs.IsChecked == true;
        Map.ShowParts = ShowParts.IsChecked == true;
    }

    void FitButton_Click(object sender, RoutedEventArgs e) => Map.FitAll();

    // ---------- Zoomstufe neben „Ganze Karte“ (100 % = 1 Bildschirmpixel je Meter) ----------

    static readonly int[] ZoomLevels = { 10, 25, 50, 75, 100, 150, 200, 300, 400, 500, 600, 700, 800, 900, 1000 };
    bool showingZoom;

    // aktuelle Zoomstufe ins Feld schreiben (Mausrad, Ganze Karte, Sprung zu einem Gebäude …)
    void ShowZoom()
    {
        showingZoom = true;
        ZoomBox.SelectedIndex = -1;
        ZoomBox.Text = $"{Map.Scale * 100:0} %";
        showingZoom = false;
    }

    void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (showingZoom || ZoomBox.SelectedIndex < 0) return;
        Map.SetScale(ZoomLevels[ZoomBox.SelectedIndex] / 100.0);
        Map.Focus();
        // erst nach dem Auswählen neu schreiben – sonst überschreibt die ComboBox den Text danach mit der (zurückgesetzten) Auswahl
        Dispatcher.BeginInvoke(ShowZoom, System.Windows.Threading.DispatcherPriority.Background);
    }

    void ZoomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyTypedZoom();
        Map.Focus();
        e.Handled = true;
    }

    void ZoomBox_LostFocus(object sender, RoutedEventArgs e) => ApplyTypedZoom();

    // eingetippte Zahl (mit oder ohne %) übernehmen; Unsinn zeigt wieder die aktuelle Stufe
    void ApplyTypedZoom()
    {
        if (showingZoom) return;
        string t = ZoomBox.Text.Replace("%", "").Trim().Replace(',', '.');
        if (double.TryParse(t, NumberStyles.Float, Num, out var p) && p > 0 && Math.Abs(p - Map.Scale * 100) >= 0.5) Map.SetScale(p / 100);
        else ShowZoom();
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control, inBox = e.OriginalSource is TextBox;
        if (Map.Busy && (ctrl || e.Key == Key.Escape)) { e.Handled = true; return; }   // Hintergrundarbeit läuft: keine Kürzel (Prüfbericht F1)
        // Gebäude platzieren: R = drehen, Esc = abbrechen
        if (Map.Ghost != null && e.Key == Key.R && !inBox) { Map.RotateGhost(); e.Handled = true; return; }
        if (Map.Ghost != null && e.Key == Key.Escape) { EndGhost(); e.Handled = true; return; }
        if (mountainMode && e.Key == Key.R && !inBox) { TurnMountain(); e.Handled = true; return; }
        // Gebirgskette: Enter = Bereich berechnen, Esc = Bereich verwerfen (das Werkzeug bleibt an)
        if (areaMode && Map.Area.Count >= MinAreaPoints && !inBox && e.Key == Key.Enter) { _ = PlaceArea(); e.Handled = true; return; }
        if (areaMode && Map.Area.Count > 0 && e.Key == Key.Escape) { Map.ClearArea(); e.Handled = true; return; }
        // Esc = Abbrechen (Wunsch des Nutzers): wie der Knopf im Modul über der Karte
        if (e.Key == Key.Escape && Map.SpawnMode != null) { EndSpawnMode(); e.Handled = true; }
        else if (e.Key == Key.Escape && ToolModule.Visibility == Visibility.Visible) { ToolCancel_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape && Map.EraseMode) { SetEraseMode(false); e.Handled = true; }   // Pinsel, Berge, Radiergummi: Werkzeug beenden
        else if (ctrl && e.Key == Key.S && SaveButton.IsEnabled) { SaveButton_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z && !inBox && edits?.HasChanges == true) { UndoButton_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y && !inBox && edits?.CanRedo == true) { RedoButton_Click(sender, e); e.Handled = true; }
    }

    // ---------- Linke Seite: Werkzeuge und Modul über der Karte ----------

    async void AsphaltTool_Click(object sender, RoutedEventArgs e) { if (await ConfirmSwitch(sender)) StartRoadTool(Surface.Asphalt, AsphaltTool); else UpdateToolModule(); }
    async void GravelTool_Click(object sender, RoutedEventArgs e) { if (await ConfirmSwitch(sender)) StartRoadTool(Surface.Gravel, GravelTool); else UpdateToolModule(); }

    void StartRoadTool(Surface surface, System.Windows.Controls.Primitives.ToggleButton button)
    {
        if (button.IsChecked != true) { ToolCancel_Click(button, null); return; }   // zweiter Klick = abbrechen
        if (Map.EraseMode) SetEraseMode(false);
        ClearRoad();
        ShowToolValues();   // eine vom Kachelanschluss übernommene Breite gilt nur für die eine Straße
        ToolValue_Changed(null, null);
        road = settings.RoadFor(surface);   // Werte aus dem Einstellungsfenster
        road.Width = surface == Surface.Asphalt ? asphaltWidth : gravelWidth;
        road.Shoulder = surface == Surface.Asphalt ? asphaltShoulder : 0;
        road.GrassEdge = surface == Surface.Asphalt ? asphaltGrass : gravelGrass;
        road.JunctionTolerance = surface == Surface.Asphalt ? asphaltTol : gravelTol;
        road.MaxCrossSlope = surface == Surface.Asphalt ? asphaltCross : gravelCross;
        SetOffset(0, recompute: false);   // jede neue Straße beginnt ohne Versatz
        UniformGradeCheck.IsChecked = false;   // und folgt zunächst dem Gelände
        FlatStartBox.Text = Loc.T("auto"); FlatEndBox.Text = Loc.T("auto");   // Automatik: 4 m waagerecht an Kacheln/Einfahrten
        SetEditMode(true);
    }

    async void EraserTool_Click(object sender, RoutedEventArgs e)
    {
        if (EraserTool.IsChecked != true) { ToolCancel_Click(sender, e); return; }
        if (!await ConfirmSwitch(sender)) { UpdateToolModule(); return; }
        EndSpawnMode();
        EndGhost();
        SetEraseMode(true);
    }

    void ToolCancel_Click(object sender, RoutedEventArgs e)
    {
        if (Map.EraseMode)
        {
            // Pinsel, Berge und Radiergummi wirken sofort (Wunsch des Nutzers 4.11.2026): Abbrechen beendet nur das Werkzeug, die Schritte
            // bleiben (Rückgängig über die Bearbeitungsreihenfolge). Bei der Gebirgskette verwirft es zuerst den noch nicht berechneten Bereich.
            if (areaMode && Map.Area.Count > 0) { Map.ClearArea(); return; }
            SetEraseMode(false);
            return;
        }
        ClearRoad();
    }

    void ToolApply_Click(object sender, RoutedEventArgs e)
    {
        if (areaMode && Map.EraseMode && Map.Area.Count >= MinAreaPoints) { _ = PlaceArea(); return; }   // Bereich berechnen, Werkzeug bleibt an
        if (Map.EraseMode) { SetEraseMode(false); return; }
        if (roadEditMode) { _ = ApplyRoadEdit(); return; }
        if (Map.Result != null) _ = ApplyRoad();
    }

    /// Modul über der Karte (Titel, Hinweis, Abbrechen/Übernehmen) und die Werkzeugknöpfe links passend zum Zustand
    void UpdateToolModule()
    {
        ToolApplyButton.Content = Loc.T(RiverPathMode ? "Make bank line" : "Apply");
        bool roadActive = Map.EditMode || Map.RoadPoints.Count > 0;
        // das Fenster mit Übernehmen/Abbrechen gibt es nur für Straßen und Gebirgsketten (Wunsch des Nutzers 4.11.2026)
        bool visible = roadActive || (Map.EraseMode && areaMode);
        ToolModule.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AsphaltTool.IsChecked = roadActive && !roadEditMode && road.Surface == Surface.Asphalt;
        GravelTool.IsChecked = roadActive && !roadEditMode && road.Surface == Surface.Gravel;
        RoadEditTool.IsChecked = roadActive && roadEditMode;
        EraserTool.IsChecked = Map.EraseMode && !biomeMode && !terrainMode && !mountainMode && !rangeMode && !waterMode;
        WaterTool.IsChecked = Map.EraseMode && waterMode;
        MountainTool.IsChecked = Map.EraseMode && mountainMode;
        RangeTool.IsChecked = Map.EraseMode && rangeMode;
        BiomeTool.IsChecked = Map.EraseMode && biomeMode;
        TerrainTool.IsChecked = Map.EraseMode && terrainMode;
        ToolWarnBox.Visibility = Visibility.Collapsed;   // nur beim Straßenbau gebraucht
        UpdateRibbon();
        if (!visible) return;
        if (Map.EraseMode)
        {
            int n = edits == null ? 0 : Math.Max(0, edits.Steps.Count - eraserStart);
            ToolTitle.Text = waterMode
                ? Loc.T(waterKind == 1 ? "Remove water" : waterKind == 2 ? "River" : "Lake") + (n > 0 ? " – " + Loc.F("{0} placed", n) : "")
                : rangeMode
                ? Loc.T("Mountain range") + (n > 0 ? " – " + Loc.F("{0} placed", n) : "")
                : mountainMode
                ? Loc.T("Mountains and hills") + (n > 0 ? " – " + Loc.F("{0} placed", n) : "")
                : (terrainMode ? Loc.T("Terrain brush") : biomeMode ? Loc.T("Biome brush") : Loc.T("Eraser")) + (n > 0 ? " – " + Loc.F("{0} stroke(s)", n) : "");
            ToolHint.Text = waterMode
                ? (RiverPathMode
                    ? Loc.T(Map.Area.Count >= 2 ? "Drag a point = move it, click on the line = add a point, right-click a point = delete it. Set the width in the ribbon above, then click Make bank line. Esc = discard." : "Click into the map to set the points of the river. Set the width in the ribbon above.")
                    : Map.Area.Count >= MinAreaPoints
                    ? Loc.T("Change the shape first: drag a point = move it, click the outline = add a point, right-click a point = delete it. Set the values in the ribbon above, then click Apply (or press Enter). Esc = discard the area. Drawing again somewhere else replaces it.")
                    : Loc.T(waterKind == 1 ? "Draw the area where the water should go. Only the water is removed when you click Apply, the ground stays as it is. Right mouse button drag = move the map." : waterKind == 2 ? "Draw the course of the river with the left mouse button held. When you let go, it stays editable; the river is only made when you click Apply. Right mouse button drag = move the map." : "Draw the outline of the lake with the left mouse button held. When you let go, it stays editable; the lake is only made when you click Apply. Right mouse button drag = move the map."))
                : rangeMode
                ? (Map.Area.Count >= MinAreaPoints
                    ? Loc.T("Change the shape first: drag a point = move it, click the outline = add a point, right-click a point = delete it. Set the values in the ribbon above, then click Apply (or press Enter). Esc = discard the area. Drawing again somewhere else replaces it.")
                    : Loc.T("Draw the area with the left mouse button held. When you let go, it stays editable; the mountain range is only calculated when you click Apply. Right mouse button drag = move the map."))
                : mountainMode
                ? Loc.T("Click into the map to place the mountain. Hold the left mouse button and drag to move it before letting go. R = turn by 45°. Right mouse button drag = move the map.")
                : terrainMode
                ? Loc.T("Shape the terrain with the left mouse button held. Roads, buildings and city tiles stay untouched. Drag with the right mouse button = move the map.")
                : biomeMode
                ? Loc.F("Paint {0} with the left mouse button held. Drag with the right mouse button = move the map. Heights stay as they are.", Loc.T(BiomePalette.Names[SelectedBiome]))
                : Loc.T("Paint over asphalt or gravel with the left mouse button held. Drag with the right mouse button = move the map.");
            ToolApplyButton.IsEnabled = true;
            RoadHeightPanel.Visibility = Visibility.Collapsed;
            RoadEditPanel.Visibility = Visibility.Collapsed;
            return;
        }
        RoadEditPanel.Visibility = roadEditMode ? Visibility.Visible : Visibility.Collapsed;
        if (roadEditMode)
        {
            RoadHeightPanel.Visibility = Visibility.Collapsed;
            UpdateRoadEditModule();
            return;
        }
        RoadHeightPanel.Visibility = Visibility.Visible;
        var r = Map.Result;
        ToolTitle.Text = (road.Surface == Surface.Gravel ? Loc.T("New gravel road") : Loc.T("New asphalt road")) +
            (r != null ? string.Format(Num, " – {0:#,0} m", r.Length) : "");
        string hint = Map.RoadPoints.Count < 2
            ? Loc.T("Click into the map to set points. Start and end snap to the turquoise entrances.")
            : Loc.T("Drag a point = move, click on the road = insert a point, right-click a point = delete.");
        // kurze Warnungen statt des früheren Prüfberichts
        if (r != null)
        {
            int onRoad = r.Obstacles.Count(o => o.Edge < 0), red = r.Crossings.Count(c => c.NeedsWork);
            var warn = new List<string>();
            if (onRoad > 0) warn.Add("⚠ " + Loc.F("{0} building(s) on the road (red outline).", onRoad));
            if (red > 0) warn.Add("⚠ " + Loc.F("{0} junction(s) with more than 1 m height difference (red circle).", red));
            if (r.NeededGrade > r.Settings.MaxGrade + 0.002)
                warn.Add("⚠ " + Loc.F("Too steep: {0:0} % is needed here, {1:0} % is allowed. The grade is spread evenly{2}. Better: make the route longer or draw curves (serpentine).",
                    r.NeededGrade * 100, r.Settings.MaxGrade * 100, r.NeededGrade > r.Settings.MaxGrade * 2.5 ? " " + Loc.T("(more than 2.5 times is not possible, a step remains there)") : ""));
            ToolWarnText.Text = string.Join("\n", warn);
            ToolWarnBox.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (exitWidthNote != null) hint += "\n" + exitWidthNote;
        ToolHint.Text = hint;
        ToolApplyButton.IsEnabled = r != null;
    }

    void ToolValue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ToolValue_Changed(sender, e);
    }

    // Werte der linken Seite lesen; gilt sofort für eine gerade gezeichnete Straße desselben Belags
    // und wird sofort in user.ini gemerkt (nur Abweichungen von default.ini)
    void ToolValue_Changed(object sender, RoutedEventArgs e)
    {
        asphaltWidth = ReadBox(AsphaltWidthBox, 1, 40, asphaltWidth);
        asphaltShoulder = ReadBox(AsphaltShoulderBox, 0, 5, asphaltShoulder);
        gravelWidth = ReadBox(GravelWidthBox, 1, 40, gravelWidth);
        asphaltGrass = ReadBox(AsphaltGrassBox, 0, 50, asphaltGrass);
        gravelGrass = ReadBox(GravelGrassBox, 0, 50, gravelGrass);
        var ca = settings.RoadFor(Surface.Asphalt);
        var cg = settings.RoadFor(Surface.Gravel);
        var a = new RoadValues
        {
            Width = asphaltWidth, Shoulder = asphaltShoulder, GrassEdge = asphaltGrass,
            MaxGrade = ReadBox(AsphaltGradeBox, 1, 50, ca.MaxGrade * 100) / 100, Smooth = ReadBox(AsphaltSmoothBox, 0, 400, ca.Smooth),
            Blend = ReadBox(AsphaltBlendBox, 1, RoadPlanner.MaxSlopeWidth, ca.Blend), CutSlope = ReadBox(AsphaltCutBox, 0, 5, ca.CutSlope),
            JunctionTolerance = ReadBox(AsphaltTolBox, 0, 20, ca.JunctionTolerance * 100) / 100, CrossSlope = ReadBox(AsphaltCrossBox, 0, 15, ca.MaxCrossSlope * 100) / 100,
        };
        var g = new RoadValues
        {
            Width = gravelWidth, Shoulder = cg.Shoulder, GrassEdge = gravelGrass,
            MaxGrade = ReadBox(GravelGradeBox, 1, 50, cg.MaxGrade * 100) / 100, Smooth = ReadBox(GravelSmoothBox, 0, 400, cg.Smooth),
            Blend = ReadBox(GravelBlendBox, 1, RoadPlanner.MaxSlopeWidth, cg.Blend), CutSlope = ReadBox(GravelCutBox, 0, 5, cg.CutSlope),
            JunctionTolerance = ReadBox(GravelTolBox, 0, 20, cg.JunctionTolerance * 100) / 100, CrossSlope = ReadBox(GravelCrossBox, 0, 15, cg.MaxCrossSlope * 100) / 100,
        };
        asphaltTol = a.JunctionTolerance ?? asphaltTol; gravelTol = g.JunctionTolerance ?? gravelTol;
        asphaltCross = a.CrossSlope ?? asphaltCross; gravelCross = g.CrossSlope ?? gravelCross;
        // Änderungen gelten auch beim nächsten Start (user.ini)
        if (sender != null && (Differs(a, ca) || Differs(g, cg)))
        {
            settings.Asphalt = a; settings.Gravel = g;
            settings.Save();
        }
        if (sender == null || Map.RoadPoints.Count == 0 && !Map.EditMode) return;
        var v = road.Surface == Surface.Asphalt ? a : g;
        road.Width = v.Width; road.GrassEdge = v.GrassEdge; road.JunctionTolerance = v.JunctionTolerance ?? road.JunctionTolerance;
        road.MaxCrossSlope = v.CrossSlope ?? road.MaxCrossSlope;
        road.MaxGrade = v.MaxGrade; road.Smooth = v.Smooth; road.Blend = v.Blend; road.CutSlope = v.CutSlope;
        if (road.Surface == Surface.Asphalt) road.Shoulder = v.Shoulder;
        Recompute();
    }

    // true, wenn sich ein Wert gegenüber den bisherigen Straßenwerten geändert hat
    static bool Differs(RoadValues n, RoadSettings o)
    {
        static bool D(double x, double y) => Math.Abs(x - y) > 1e-9;
        return D(n.Width, o.Width) || D(n.Shoulder, o.Shoulder) || D(n.GrassEdge, o.GrassEdge) || D(n.MaxGrade, o.MaxGrade) || D(n.Smooth, o.Smooth)
            || D(n.Blend, o.Blend) || D(n.CutSlope, o.CutSlope) || D(n.JunctionTolerance ?? o.JunctionTolerance, o.JunctionTolerance)
            || D(n.CrossSlope ?? o.MaxCrossSlope, o.MaxCrossSlope);
    }

    static double ReadBox(TextBox box, double min, double max, double current)
    {
        if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, Num, out var v) && v >= min && v <= max)
        {
            box.ClearValue(BorderBrushProperty);
            return v;
        }
        box.BorderBrush = System.Windows.Media.Brushes.Red;
        return current;
    }

    static string Fmt(double v) => v.ToString("0.##", Num);

    // ---------- Radiergummi ----------

    // links geänderte Pinselwerte gelten auch beim nächsten Start (wie im Einstellungsfenster)
    void BrushShape_Click(object sender, RoutedEventArgs e)
    {
        bool square = BrushSquareRadio.IsChecked == true;
        if (settings.EraserSquare != square) { settings.EraserSquare = square; settings.Save(); }
        ApplyBrush();
    }

    void BrushSize_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BrushSize_Changed(sender, e);
    }

    void BrushSize_Changed(object sender, RoutedEventArgs e)
    {
        eraserSize = ReadBox(BrushSizeBox, 2, 200, eraserSize);
        ApplyBrush();
        if (sender != null && settings.EraserSize != eraserSize) { settings.EraserSize = eraserSize; settings.Save(); }
    }

    void BumpsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || BumpsBox.SelectedIndex < 0 || settings.EraserBumps == BumpsBox.SelectedIndex) return;
        settings.EraserBumps = BumpsBox.SelectedIndex;
        settings.Save();
    }

    /// Pinsel ein/aus; biome = Biom-Pinsel statt Radiergummi (Wechsel zwischen beiden behält die bisherigen Striche)
    void SetEraseMode(bool on, BrushTool tool = BrushTool.Eraser)
    {
        if (on) ClearRoad();
        if (on && (!Map.EraseMode || tool != brushTool)) eraserStart = edits?.Steps.Count ?? 0;
        brushTool = on ? tool : BrushTool.Eraser;
        Map.EraseMode = on;
        BrushSize_Changed(null, null);
        BiomeSize_Changed(null, null);
        TerrainValue_Changed(null, null);
        MountainValue_Changed(null, null);
        HelpText.Text = on ? HelpBrushText : HelpIdle;
        UpdateToolModule();
    }

    async void EraseStroke(List<Point> stroke)
    {
        if (world == null || edits == null) { Map.ClearStroke(); return; }
        BrushSize_Changed(null, null);
        double radius = eraserSize / 2, rim = EraserRim;
        bool square = Map.BrushSquare;
        int bumps = Math.Max(0, BumpsBox.SelectedIndex);
        double cx = (stroke.Min(p => p.X) + stroke.Max(p => p.X)) / 2, cz = (stroke.Min(p => p.Y) + stroke.Max(p => p.Y)) / 2;
        string title = Loc.F("Eraser at {0:0}, {1:0}", cx, cz);
        string detail = string.Format(CultureInfo.InvariantCulture, "Radiergummi bei {0:0},{1:0}: Pinsel {2} m {3}, Rand {4} m, Unebenheit {5}, {6} Strichpunkte",
            cx, cz, eraserSize, square ? "eckig" : "rund", rim, bumps, stroke.Count);
        var w = world;
        Map.Busy = true;
        Cursor = Cursors.Wait;
        try
        {
            EraseStats st = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                var s = Eraser.Erase(w, e, stroke, radius, rim, true, square, bumps);
                st ??= s;   // Ergebnis des ersten Durchgangs (spätere: Wiederholen/Neu rechnen)
            }));
            Map.ClearStroke();
            if (st.Pixels == 0) return;   // kein Belag unter dem Strich: kein Schritt
            step.Note = Loc.F("{0:#,0} m² of surface removed, {1:#,0} height points blended.", st.Pixels, st.HeightPoints);
            Refresh(step);
            Recompute();
        }
        catch (Exception ex)
        {
            Map.ClearStroke();
            MessageBox.Show(this, Loc.T("Erasing failed:") + "\n" + ex.Message, Loc.T("Eraser"), MessageBoxButton.OK, MessageBoxImage.Error);
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

    // geänderten Bereich der Karte neu zeichnen
    void Refresh(EditStep step)
    {
        if (step == null) return;
        if (step.WaterAdded + step.WaterRemoved > 0)
        {
            string wn = Loc.F("Water followed the ground: {0:#,0} m² newly covered, {1:#,0} m² dry now.", step.WaterAdded, step.WaterRemoved);
            if (step.Note == null || !step.Note.Contains(wn)) step.Note = (step.Note == null ? "" : step.Note + " ") + wn;
        }
        if (step.HasArea) Map.RefreshArea(step.X0, step.Z0, step.X1, step.Z1);
        else Map.InvalidateVisual();
        if (lastPointer != null) ShowPointer(lastPointer);   // Höhe unten links gleich neu lesen, auch wenn die Maus still steht
    }

    // ---------- Straße zeichnen ----------

    void SetEditMode(bool on)
    {
        if (on && Map.EraseMode) SetEraseMode(false);
        if (on) { EndSpawnMode(); EndGhost(); }
        Map.EditMode = on;
        HelpText.Text = on ? HelpEdit : HelpIdle;
        UpdateButtons();
        UpdateToolModule();
    }

    void ClearRoad()
    {
        EndRoadEdit();
        Map.SetRoadPoints(Array.Empty<Point>());
        SetEditMode(false);
        Recompute();
    }

    void UpdateButtons()
    {
        bool loaded = world != null;
        DeployButton.IsEnabled = SaveGameButton.IsEnabled = AsphaltTool.IsEnabled = GravelTool.IsEnabled = EraserTool.IsEnabled = TerrainTool.IsEnabled = MountainTool.IsEnabled = RangeTool.IsEnabled = WaterTool.IsEnabled = RoadEditTool.IsEnabled = loaded;
        SaveButton.IsEnabled = loaded && edits?.HasChanges == true;
        UpdateRibbon();
    }

    // Anschlüsse an bestehende Straßen (Anfang/Ende), zwischengespeichert je Lage, weil das Verfolgen etwas dauert
    readonly Dictionary<(int, int, int, int), Junction> junctionCache = new();
    Junction startJunction, endJunction;
    readonly List<Junction> crossJunctions = new();   // Kreuzungen mitten auf der neuen Straße
    List<double?> plannedHeights;   // Höhen der Stützpunkte der zuletzt geplanten Straße (von Hand + Kreuzungen)
    List<(double Dist, double Height)> plannedFixes = new();   // ebene Strecken der neuen Straße an den Kreuzungen
    List<(double Dist, double Slope, double Half)> plannedCross = new();   // Querneigung der neuen Straße an den Kreuzungen
    List<(Point Center, double Radius)> plannedZones = new();             // Fläche bestehender Straßen an den Kreuzungen (bleibt)
    readonly Dictionary<Junction, double> crossSlopeOf = new();

    IEnumerable<Junction> AllJunctions => new[] { startJunction, endJunction }.Concat(crossJunctions).Where(j => j != null);

    // along: Richtung der neuen Straße von dieser Stelle aus (wird bei der Suche ausgeschlossen), bothWays bei Kreuzungen mitten drin
    Junction JunctionAt(Point p, bool atStart, Vector along, bool bothWays)
    {
        // an der Kreuzung darf so viel Steigung bleiben, wie die Toleranz erlaubt – oder die neue Straße quer ausgleicht
        double allowed = Math.Max(road.JunctionTolerance, road.MaxCrossSlope);
        int dirKey = (int)Math.Round(Math.Atan2(along.Y, along.X) * 180 / Math.PI / 10);   // Richtung auf 10° genau
        var key = ((int)Math.Round(p.X), (int)Math.Round(p.Y), (int)Math.Round(road.Width * 10 + road.Shoulder) * 1000 + (int)Math.Round(allowed * 1000), dirKey * 4 + (bothWays ? 2 : 0) + (road.Surface == Surface.Asphalt ? 1 : 0));
        if (!junctionCache.TryGetValue(key, out var j))
            junctionCache[key] = j = Junctions.Find(world, p, atStart, road.Width / 2 + road.Shoulder, allowed, s => settings.RoadFor(s), along, bothWays, road.Surface);
        return j;
    }

    // bestehende Straße an der Kreuzung ungefähr parallel zur neuen (Abweichung unter 35°): keine Kreuzung, sondern Verlängerung/Wegrest
    static bool Parallel(Junction j, Vector dir)
    {
        if (j == null || dir.Length < 1e-6) return false;
        dir.Normalize();
        return Math.Abs(j.Direction.X * dir.X + j.Direction.Y * dir.Y) > Math.Cos(35 * Math.PI / 180);
    }

    // Richtung der Mittellinie bei Abstand d (von d nach d + step)
    static Vector DirAt(List<PathPoint> path, double d, double step)
    {
        int a = IndexAtDist(path, d), b = IndexAtDist(path, d + step);
        return new Vector(path[b].X - path[a].X, path[b].Z - path[a].Z);
    }

    static int IndexAtDist(List<PathPoint> path, double d)
    {
        d = Math.Clamp(d, 0, path[^1].Dist);
        int i = 0;
        while (i < path.Count - 1 && path[i].Dist < d) i++;
        return i;
    }

    // Breiten der Stützpunkte (Verjüngen, Wunsch des Nutzers 3.11.2026): von Hand gesetzt, sonst am Anfang/Ende die Breite des
    // Kachelanschlusses; alle anderen Punkte haben die Standardbreite aus dem Feld links, dazwischen verjüngt sich die Straße
    List<double?> plannedWidths = new();
    string exitWidthNote;

    List<double?> WidthsFromExits()
    {
        var pts = Map.RoadPoints;
        var list = Map.PointWidths.ToList();
        exitWidthNote = null;
        if (world == null || pts.Count == 0 || list.Count != pts.Count) return list;
        var start = Map.ExitNear(pts[0]);
        var end = pts.Count >= 2 ? Map.ExitNear(pts[^1]) : null;
        var notes = new List<string>();
        if (start is { } s) { if (list[0] == null) list[0] = s.Width; notes.Add(Loc.F("Start: {0} m wide (connection).", s.Width)); }
        if (end is { } e) { if (list[^1] == null) list[^1] = e.Width; notes.Add(Loc.F("End: {0} m wide (connection).", e.Width)); }
        if (notes.Count > 0)
        {
            double w0 = list[0] ?? road.Width, w1 = list[^1] ?? road.Width;
            if (Math.Abs(w0 - w1) > 0.05 || list.Any(v => v != null && Math.Abs(v.Value - road.Width) > 0.05))
                notes.Add(Loc.T("The road changes its width between the points. Click a point to set its width."));
            exitWidthNote = string.Join(" ", notes);
        }
        return list;
    }

    void UpdatePointWidthRow(int i, bool show)
    {
        PointWidthRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        bool manual = Map.PointWidths[i] is double;
        double current = i < plannedWidths.Count && plannedWidths[i] is double pw ? pw : road.Width;
        PointWidthInfo.Text = manual ? Loc.T("by hand") : i < plannedWidths.Count && plannedWidths[i] != null ? Loc.T("connection") : Loc.T("standard width");
        PointWidthLabel.FontWeight = manual ? FontWeights.SemiBold : FontWeights.Normal;
        if (!PointWidthBox.IsKeyboardFocused)
        {
            shownWidthText = current.ToString("0.#", Num);
            PointWidthBox.Text = shownWidthText;
            PointWidthBox.ClearValue(BorderBrushProperty);
        }
    }

    string shownWidthText;

    void PointWidth_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { shownWidthText = null; PointWidth_Changed(sender, e); }
    }

    void PointWidth_Changed(object sender, RoutedEventArgs e)
    {
        int i = Map.SelectedPoint;
        if (i < 0 || PointWidthBox.Text == shownWidthText) return;   // nur angesehen, nicht geändert
        double v = ReadBox(PointWidthBox, 1, 40, double.NaN);
        if (double.IsNaN(v)) return;
        shownWidthText = PointWidthBox.Text;
        Map.SetPointWidth(i, v);
    }

    void PointWidthAuto_Click(object sender, RoutedEventArgs e)
    {
        if (Map.SelectedPoint >= 0) Map.SetPointWidth(Map.SelectedPoint, null);
    }
    void Recompute()
    {
        if (roadEditMode) { RecomputeRoadEdit(); return; }
        plannedWidths = WidthsFromExits();
        var pts = Map.RoadPoints;
        RoadResult r = null;
        startJunction = endJunction = null;
        plannedHeights = Map.PointHeights.ToList();
        if (world != null && pts.Count >= 2)
        {
            if (AdjustJunctionsCheck.IsChecked == true)
            {
                var path = RoadPlanner.BuildPath(pts);
                if (path.Count >= 2)
                {
                    double len = path[^1].Dist;
                    startJunction = JunctionAt(pts[0], true, DirAt(path, 0, Math.Min(10, len)), false);
                    endJunction = JunctionAt(pts[^1], false, -DirAt(path, Math.Max(0, len - 10), Math.Min(10, len)), false);
                    // läuft die bestehende Straße in dieselbe Richtung weiter, ist das eine Verlängerung (oder Neuzeichnen), keine
                    // Kreuzung: kein Umbau, keine ebene Fläche, die neue Straße setzt einfach auf ihrer Höhe an (28.10.2026: sonst Sprünge
                    // von 1,8 m und 0,9 m im nördlichen Schotterweg)
                    // bei einer Verlängerung bleibt die Höhe des alten Wegs an der Ansatzstelle fest (sonst Sprung)
                    if (Parallel(startJunction, DirAt(path, 0, Math.Min(10, len)))) { startJunction = null; plannedHeights[0] ??= world.HeightAt((int)Math.Floor(pts[0].X), (int)Math.Floor(pts[0].Y)); }
                    if (Parallel(endJunction, DirAt(path, Math.Max(0, len - 10), Math.Min(10, len)))) { endJunction = null; plannedHeights[^1] ??= world.HeightAt((int)Math.Floor(pts[^1].X), (int)Math.Floor(pts[^1].Y)); }
                }
                // die neue Straße endet auf Kreuzungshöhe (eine von Hand gesetzte Höhe hat Vorrang)
                if (startJunction != null) plannedHeights[0] ??= startJunction.Height;
                if (endJunction != null) plannedHeights[^1] ??= endJunction.Height;
            }
            r = RoadPlanner.Compute(world, pts, road, plannedHeights, widths: plannedWidths);
            // Kreuzungen mitten auf der Straße (Wunsch des Nutzers): auf Höhe der bestehenden Straße und dort eben
            // (±halbe Breite der bestehenden Straße + 3 m); die bestehende Straße wird angepasst wie bei einem Anschluss
            crossJunctions.Clear();
            plannedFixes = new();
            plannedCross = new();
            plannedZones = new();
            crossSlopeOf.Clear();
            if (r != null && AdjustJunctionsCheck.IsChecked == true)
            {
                var at = new List<(Junction J, double Dist)>();
                if (startJunction != null) at.Add((startJunction, 0));
                if (endJunction != null) at.Add((endJunction, r.Length));
                foreach (var c in r.Crossings.Where(c => !c.AtEnd && c.Length < 40))   // längeres Stück = Straße läuft entlang, keine Kreuzung
                {
                    var j = JunctionAt(new Point(c.X, c.Z), false, DirAt(r.Path, c.Dist - 5, 10), true);
                    if (j == null || Parallel(j, DirAt(r.Path, c.Dist - 5, 10))) continue;   // fast parallel: alter Wegrest, keine Kreuzung
                    j.AtStart = false;
                    crossJunctions.Add(j);
                    at.Add((j, c.Dist));
                    double rn = j.Width / 2 + 3;
                    for (double d = -rn; d <= rn + 0.01; d += 1) plannedFixes.Add((c.Dist + d, j.Height));   // längs eben
                }
                // Querneigung (Wunsch des Nutzers): die neue Straße kippt seitlich so weit, wie die bestehende Straße dort
                // (nach einem Umbau) steigt – höchstens MaxCrossSlope; ihr Anteil quer zur neuen Straße zählt
                foreach (var (j, d) in at)
                {
                    var (nx, nz) = RoadPlanner.Normal(r.Path, r.IndexAt(d));
                    // an einer höherrangigen Straße (die bleibt) passt sich die neue Straße stärker an: bis zu ihrer Steigung, höchstens 12 %
                    double cap = j.Protected ? Math.Max(road.MaxCrossSlope, Math.Min(0.12, Math.Abs(j.PlateauGrade))) : road.MaxCrossSlope;
                    double slope = Math.Clamp(j.PlateauGrade * (j.Direction.X * nx + j.Direction.Y * nz), -cap, cap);
                    if (Math.Abs(slope) < 0.001) continue;
                    crossSlopeOf[j] = slope;
                    plannedCross.Add((d, slope, j.Width / 2 + 3));
                }
                // Anfang/Ende auf einer bestehenden Straße: das Stück der neuen Straße auf ihr (plus 2 m davor) liegt auf Kreuzungshöhe,
                // sonst entsteht am Rand der bestehenden Straße eine Stufe (eine von Hand gesetzte Höhe hat Vorrang)
                foreach (var (j, atStart) in new[] { (startJunction, true), (endJunction, false) })
                {
                    if (j == null || Map.PointHeights[atStart ? 0 : ^1] != null) continue;
                    var endPt = atStart ? pts[0] : pts[^1];
                    double inside = Math.Max(0, j.Width / 2 - (endPt - j.Center).Length) + 2;
                    for (double d = 0; d <= inside + 0.01; d += 1) plannedFixes.Add((atStart ? d : r.Length - d, j.Height));
                }
                // Fläche der bestehenden Straße an jeder Kreuzung: dort schreibt die neue Straße keine Höhen
                foreach (var j in AllJunctions) plannedZones.Add((j.Center, Math.Max(j.Width / 2 + 1, road.Width / 2 + road.Shoulder + 1)));
                if (plannedFixes.Count > 0 || plannedCross.Count > 0) r = RoadPlanner.Compute(world, pts, road, plannedHeights, plannedFixes, plannedCross, plannedWidths);
                r.ProtectZones = plannedZones;
            }
        }
        else crossJunctions.Clear();
        Map.ExtraResults = AllJunctions.Where(j => j.RebuildResult != null).Select(j => j.RebuildResult).ToList();
        UpdateJunctionText();
        Map.Result = r;
        ShowProfile(r);
        UpdateButtons();
        UpdateToolModule();
        UpdatePointRow();
    }

    /// Höhenprofil unten nur zeigen, wenn es gebraucht wird: solange eine Straße (oder die Änderung einer Straße) berechnet ist
    void ShowProfile(RoadResult r)
    {
        Profile.Result = r;
        ProfilePanel.Visibility = r != null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Kasten über der Karte verschieben (Wunsch des Nutzers) ----------

    Point? moduleGrab;

    void ToolModule_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // nur an freien Stellen greifen, Knöpfe, Felder und Regler bedienen sich normal
        for (var d = e.OriginalSource as DependencyObject; d != null && d != ToolModule; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase or TextBox or Slider or ComboBox) return;
        moduleGrab = e.GetPosition(this) - new Vector(ToolModuleShift.X, ToolModuleShift.Y);
        ToolModule.CaptureMouse();
        e.Handled = true;
    }

    void ToolModule_MouseMove(object sender, MouseEventArgs e)
    {
        if (moduleGrab is not Point g) return;
        var p = e.GetPosition(this);
        // ganze Pixel, sonst wird die Schrift im Kasten unscharf
        ToolModuleShift.X = Math.Round(p.X - g.X);
        ToolModuleShift.Y = Math.Round(Math.Max(-8, p.Y - g.Y));   // nicht über den oberen Rand der Karte hinaus
    }

    void ToolModule_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (moduleGrab == null) return;
        moduleGrab = null;
        ToolModule.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ---------- Höhe der Straße: Versatz und Höhe einzelner Punkte (Wunsch des Nutzers 28.10.2026) ----------

    bool settingOffset;
    string shownPointText;   // zuletzt angezeigter Wert im Punktfeld (nur eine echte Änderung setzt die Höhe von Hand)

    void SetOffset(double v, bool recompute)
    {
        settingOffset = true;
        road.HeightOffset = v;
        OffsetSlider.Value = Math.Clamp(v, OffsetSlider.Minimum, OffsetSlider.Maximum);
        OffsetBox.Text = Fmt(v);
        OffsetBox.ClearValue(BorderBrushProperty);
        settingOffset = false;
        if (recompute) Recompute();
    }

    void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!settingOffset && IsLoaded) SetOffset(Math.Round(e.NewValue * 2) / 2, recompute: true);
    }

    void OffsetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OffsetBox_Changed(sender, e);
    }

    void OffsetBox_Changed(object sender, RoutedEventArgs e)
    {
        double v = ReadBox(OffsetBox, -20, 20, double.NaN);
        if (!double.IsNaN(v) && Math.Abs(v - road.HeightOffset) > 1e-6) SetOffset(v, recompute: true);
    }

    void FlatBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) FlatBox_Changed(sender, e);
    }

    // leer oder „auto“ = automatisch (-1), sonst Meter
    static double ReadFlat(TextBox box)
    {
        string t = box.Text.Trim();
        if (t.Length == 0 || t.Equals("auto", StringComparison.OrdinalIgnoreCase) || t.Equals(Loc.T("auto"), StringComparison.OrdinalIgnoreCase))
        {
            box.Text = Loc.T("auto");
            box.ClearValue(BorderBrushProperty);
            return -1;
        }
        return ReadBox(box, 0, 100, double.NaN);
    }

    // waagerechtes Stück am Anfang/Ende der Straße (m)
    void FlatBox_Changed(object sender, RoutedEventArgs e)
    {
        double a = ReadFlat(FlatStartBox), b = ReadFlat(FlatEndBox);
        if (double.IsNaN(a) || double.IsNaN(b)) return;
        if (Math.Abs(a - road.FlatStart) < 1e-6 && Math.Abs(b - road.FlatEnd) < 1e-6) return;
        road.FlatStart = a;
        road.FlatEnd = b;
        Recompute();
    }

    void UpdatePointRow()
    {
        if (roadEditMode) { UpdateEditPanel(); return; }
        int i = Map.SelectedPoint;
        var r = Map.Result;
        bool show = i >= 0 && i < Map.RoadPoints.Count;
        PointHeightRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdatePointWidthRow(i, show);
        PointTip.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show) return;
        string which = i == 0 ? $" ({Loc.T("start")})" : i == Map.RoadPoints.Count - 1 ? $" ({Loc.T("end")})" : "";
        bool manual = Map.PointHeights[i] is double;
        PointLabel.Text = Loc.F("Point {0}", i + 1) + which;
        PointLabel.FontWeight = manual ? FontWeights.SemiBold : FontWeights.Normal;
        var pt = Map.RoadPoints[i];
        double terrain = world.HeightAt((int)Math.Floor(pt.X), (int)Math.Floor(pt.Y));
        double? value = Map.PointHeights[i] ?? (r != null && i < r.ControlIndex.Length ? r.Profile[r.ControlIndex[i]] : null);
        PointInfo.Text = Loc.F("Terrain {0:0.0} m", terrain) + " · " + (manual ? Loc.T("by hand") : Loc.T("automatic"));
        if (!PointHeightBox.IsKeyboardFocused)
        {
            shownPointText = value is double v ? v.ToString("0.0", Num) : "";
            PointHeightBox.Text = shownPointText;
            PointHeightBox.ClearValue(BorderBrushProperty);
        }
    }

    void PointHeight_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { shownPointText = null; PointHeight_Changed(sender, e); }
    }

    void PointHeight_Changed(object sender, RoutedEventArgs e)
    {
        int i = Map.SelectedPoint;
        if (i < 0 || PointHeightBox.Text == shownPointText) return;   // nur angesehen, nicht geändert
        double v = ReadBox(PointHeightBox, 0, 400, double.NaN);
        if (double.IsNaN(v)) return;
        shownPointText = PointHeightBox.Text;
        Map.SetPointHeight(i, v);
    }

    void AdjustJunctions_Click(object sender, RoutedEventArgs e) => Recompute();

    void UniformGrade_Click(object sender, RoutedEventArgs e)
    {
        road.UniformGrade = UniformGradeCheck.IsChecked == true;
        Recompute();
    }

    void UpdateJunctionText()
    {
        var lines = new List<string>();
        foreach (var j in AllJunctions)
        {
            string where = j == startJunction ? Loc.T("Start") : j == endJunction ? Loc.T("End") : Loc.F("Junction at {0:0}, {1:0}", j.Center.X, j.Center.Y);
            string kind = j.Kind == Surface.Asphalt ? Loc.T("asphalt road") : Loc.T("gravel road");
            string tilt = crossSlopeOf.TryGetValue(j, out var cs) ? " " + Loc.F("New road tilted sideways there by {0:0.#} %.", Math.Abs(cs) * 100) : "";
            string held = j.HeldBranches.Count == 0 ? "" : " " + Loc.F("{0} other connection(s)/junction(s) in the piece stay unchanged (the rebuild ends before them).", j.HeldBranches.Count);
            lines.Add((j.RebuildResult != null
                ? Loc.F("{0}: connection to {1} with {2:0.0} % grade – it is adjusted over {3:#,0} m (purple), junction at {4:0.0} m with {5:0.#} %.",
                    where, kind, Math.Abs(j.ExistingGrade) * 100, j.RebuildResult.Length, j.Height, Math.Abs(j.PlateauGrade) * 100) + held
                : j.Protected
                ? Loc.F("{0}: connection to {1} with {2:0.0} % grade – stays unchanged (asphalt before gravel), the road adapts, junction at {3:0.0} m.",
                    where, kind, Math.Abs(j.ExistingGrade) * 100, j.Height)
                : Loc.F("{0}: connection to {1} with {2:0.0} % grade – stays, junction at {3:0.0} m.", where, kind, Math.Abs(j.ExistingGrade) * 100, j.Height)) + tilt);
        }
        // Gesamtbild: welche bestehenden Straßen ändern sich, welche bleiben
        if (lines.Count > 0)
        {
            int changedRoads = AllJunctions.Count(j => j.RebuildResult != null), kept = AllJunctions.Count() - changedRoads;
            lines.Insert(0, Loc.F("Affected: {0} junction(s) – {1} existing road(s) are adjusted, {2} stay unchanged.",
                AllJunctions.Count(), changedRoads, kept));
        }
        JunctionText.Text = string.Join("\n", lines);
        JunctionText.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void PointAuto_Click(object sender, RoutedEventArgs e)
    {
        if (Map.SelectedPoint >= 0) Map.SetPointHeight(Map.SelectedPoint, null);
    }

    // ---------- Übernehmen, Rückgängig, Spiel ----------

    async Task ApplyRoad()
    {
        var r = Map.Result;
        if (r == null || world == null || Map.Busy) return;
        var warn = new List<string>();
        int onRoad = r.Obstacles.Count(o => o.Edge < 0), red = r.Crossings.Count(c => c.NeedsWork);
        if (onRoad > 0) warn.Add(Loc.F("{0} building(s) on the road.", onRoad));
        if (red > 0) warn.Add(Loc.F("{0} junction(s) with a height difference – the roads must be adjusted afterwards.", red));
        if (warn.Count > 0 && MessageBox.Show(this, Loc.T("Attention:") + "\n• " + string.Join("\n• ", warn) + "\n\n" + Loc.T("Apply anyway?"), Loc.T("Apply"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (Map.EditMode) SetEditMode(false);

        var s = r.Settings;
        string title = (s.Surface == Surface.Gravel ? Loc.T("Gravel") : Loc.T("Asphalt")) + string.Format(Num, " {0:#,0} m", r.Length);
        string detail = title + ": Punkte " + string.Join(";", Map.RoadPoints.Select(p => string.Format(CultureInfo.InvariantCulture, "{0:0},{1:0}", p.X, p.Y))) + ", " +
            string.Format(CultureInfo.InvariantCulture, "Breite {0}, Rand {1}, Steigung {2}, Glaettung {3}, Boeschung {4}, je m Hoehe {5}, Versatz {6}",
                s.Width, s.Shoulder, s.MaxGrade, s.Smooth, s.Blend, s.CutSlope, s.HeightOffset) + (s.UniformGrade ? ", gleichmaessige Steigung" : "");
        var heights = plannedHeights.ToList();   // von Hand gesetzte Höhen plus Kreuzungshöhen
        if (plannedWidths.Any(v => v != null))
            detail += ", Breiten " + string.Join(";", plannedWidths.Select(v => v is double d ? d.ToString("0.#", CultureInfo.InvariantCulture) : "std"));
        if (heights.Any(h => h != null))
            detail += ", Punkthoehen " + string.Join(";", heights.Select(h => h is double v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "auto"));
        // bestehende Straßen an den Kreuzungen umbauen (vor der neuen Straße, damit diese sauber darauf endet)
        var rebuilds = AllJunctions.Where(j => j.RebuildResult != null)
            .Select(j => (Points: j.RebuildPoints.ToList(), Heights: j.RebuildHeights.ToList(), Settings: j.RebuildSettings.Clone())).ToList();
        var fixes = plannedFixes.ToList();
        var cross = plannedCross.ToList();
        var zones = plannedZones.ToList();
        if (cross.Count > 0) detail += ", Querneigung " + string.Join(";", cross.Select(c => string.Format(CultureInfo.InvariantCulture, "{0:0} m {1:0.0} %", c.Dist, c.Slope * 100)));
        if (fixes.Count > 0)
            detail += ", eben an Kreuzungen " + string.Join(";", crossJunctions.Select(j => string.Format(CultureInfo.InvariantCulture, "{0:0},{1:0} auf {2:0.0}", j.Center.X, j.Center.Y, j.Height)));
        if (rebuilds.Count > 0)
        {
            title += " + " + (rebuilds.Count == 1 ? Loc.T("junction") : Loc.F("{0} junctions", rebuilds.Count));
            detail += "; Kreuzung eben: bestehende Straße angepasst (" + string.Join(" | ", rebuilds.Select(b =>
                string.Join(";", b.Points.Select(p => string.Format(CultureInfo.InvariantCulture, "{0:0},{1:0}", p.X, p.Y))))) + ")";
        }
        var crossings = r.Crossings.Where(c => c.NeedsWork).ToList();
        Overlay.Visibility = Visibility.Visible;
        Map.Busy = true;   // sperrt Strg+Z/Y/S und die Knöpfe im Schritte-Fenster, solange der Hintergrundthread rechnet (Prüfbericht F1)
        LoadingText.Text = Loc.T("Applying road …");
        IProgress<string> progress = new Progress<string>(t => LoadingText.Text = t);
        // der Schritt rechnet die Straße aus Punkten und Einstellungen; so lässt er sich wiederholen oder nach dem
        // Löschen eines früheren Eintrags auf dem dann vorhandenen Gelände neu rechnen. Spawnpunkt immer an den Anfang.
        var w = world;
        var points = Map.RoadPoints.ToList();
        var rs = r.Settings.Clone();
        rs.Width = road.Width;   // bei veränderlicher Breite ist Settings.Width die größte Breite; gerechnet wird mit der Standardbreite
        var widths = plannedWidths.ToList();
        bool testAtRoad = settings.TestSpawnAtRoad;
        try
        {
            RoadApplier.Stats stats = null;
            var step = await Task.Run(() => edits.Run(title, detail, e =>
            {
                foreach (var b in rebuilds)
                    RoadApplier.Apply(w, e, RoadPlanner.Compute(w, b.Points, b.Settings, b.Heights), progress);
                var rr = RoadPlanner.Compute(w, points, rs, heights, fixes, cross, widths);
                rr.ProtectZones = zones;
                var st = RoadApplier.Apply(w, e, rr, progress);
                if (testAtRoad)
                {
                    var sp = RoadPlanner.SpawnAtStart(rr);
                    sp.Test = true;
                    e.SetTestSpawn(sp);
                }
                stats ??= st;
            }));
            Refresh(step);
            ClearRoad();

            // Ergebnis als zweite Zeile in der Bearbeitungsreihenfolge
            string text = Loc.F("Applied: {0:#,0} height points adjusted, {1:#,0} m² of surface painted{2}.",
                stats.HeightPoints, stats.Pixels, testAtRoad ? Loc.T(", test point set at the start of the road") : "");
            if (crossings.Count > 0)
                text += " " + Loc.T("Still to adjust:") + " " + string.Join("; ", crossings.Select(c => Loc.F("{0} at {1:0}, {2:0} (road {3:0.0} m {4})",
                    Loc.T(c.KindName), c.X, c.Z, Math.Abs(c.Diff), c.Diff < 0 ? Loc.T("higher") : Loc.T("lower")))) + ".";
            step.Note = text;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("Applying failed:") + "\n" + ex.Message + "\n\n" + Loc.T("The map is unchanged."), Loc.T("Apply"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Map.Busy = false;
            Overlay.Visibility = Visibility.Collapsed;
            UpdateButtons();
            UpdateUndo();
        }
    }

    async void SaveButton_Click(object sender, RoutedEventArgs e) { if (await ConfirmSwitch(null)) await Save(); }

    /// Änderungen in die Map-Dateien schreiben (vorher Sicherung); false = fehlgeschlagen
    async Task<bool> Save()
    {
        if (edits == null || !edits.HasChanges) return true;
        if (Map.Busy) return false;   // läuft schon etwas (z. B. ein zweites Speichern): nicht parallel auf denselben Daten arbeiten
        if (Map.EditMode) SetEditMode(false);
        string game = GameWorld(ask: false);
        Overlay.Visibility = Visibility.Visible;
        Map.Busy = true;
        LoadingText.Text = Loc.T("Saving …");
        IProgress<string> progress = new Progress<string>(t => LoadingText.Text = t);
        try
        {
            await Task.Run(() => edits.Save(game, progress, out _));
            MemoryTools.Release();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("Saving failed:") + "\n" + ex.Message + "\n\n" + Loc.T("The changes are still in the program. \"Undo\" can restore the backed-up state afterwards."),
                Loc.T("Save"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            Map.Busy = false;
            Overlay.Visibility = Visibility.Collapsed;
            UpdateButtons();
            UpdateUndo();
        }
    }

    /// Vor dem Schließen oder Neuladen: ungespeicherte Änderungen speichern, verwerfen oder abbrechen (false = abbrechen)
    async Task<bool> ConfirmUnsaved(string action)
    {
        if (edits == null || !edits.HasChanges) return true;
        var a = MessageBox.Show(this, Loc.F("{0} change(s) are not saved yet.\n\nSave now?\n(No = discard the changes)", edits.Steps.Count),
            action, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (a == MessageBoxResult.Cancel) return false;
        return a == MessageBoxResult.No || await Save();
    }

    async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closeConfirmed || edits == null || !edits.HasChanges) return;
        e.Cancel = true;   // erst nachfragen (und ggf. speichern), dann selbst schließen
        if (!await ConfirmUnsaved(Loc.T("Close map editor"))) return;
        closeConfirmed = true;
        // erst nach dem laufenden Closing schließen: bei „Nein“ kommt ConfirmUnsaved ohne Pause zurück, und Close() mitten
        // im Schließen wirft „… während ein Window geschlossen wird“ (Fehlermeldung des Nutzers 29.10.2026)
        _ = Dispatcher.BeginInvoke(Close);
    }

    void Window_Closed(object sender, EventArgs e)
    {
        stepsWindow.AllowClose = true;
        stepsWindow.Close();
        spawnWindow.AllowClose = true;
        spawnWindow.Close();
        prefabWindow.AllowClose = true;
        prefabWindow.Close();
    }

    async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (world == null || Map.Busy) return;
        // zuerst die Schritte, die nur im Programm sind
        if (edits.HasChanges)
        {
            var step = edits.Undo();
            Refresh(step);
            Recompute();
            UpdateButtons();
            UpdateUndo();
            UpdateToolModule();
            return;
        }
        var b = Backups.List(world).FirstOrDefault();
        if (b == null) return;
        if (MessageBox.Show(this, Loc.F("Undo the last saved change?\n\n{0:yyyy-MM-dd HH:mm}: {1}\n\nThe map files are set back to the state before, then the world is reloaded.",
                b.Time, b.Description.Split('\n')[0].Trim()),
                Loc.T("Undo"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            Backups.Restore(world, b);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("Undo failed:") + "\n" + ex.Message, Loc.T("Undo"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        await OpenWorld(world.Folder);
    }

    async void RedoButton_Click(object sender, RoutedEventArgs e)
    {
        if (world == null || edits?.CanRedo != true || Map.Busy) return;
        await RunOnSteps(Loc.T("Redoing …"), () => new List<EditStep> { edits.Redo() }, Loc.T("Redone"));
    }

    async void DeleteStep(int index)
    {
        if (world == null || edits == null || Map.Busy) return;
        bool active = index < edits.Steps.Count;
        int later = active ? edits.Steps.Count - 1 - index : 0;
        if (later > 0 && MessageBox.Show(this, Loc.F("Delete this entry?\n\nThe {0} later steps are then recalculated on the terrain as it is then.", later),
                Loc.T("Delete entry"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunOnSteps(Loc.T("Deleting entry …"), () => edits.Delete(index), Loc.T("Entry deleted"));
    }

    // Wiederholen/Löschen im Hintergrund rechnen, danach die betroffenen Bereiche der Karte neu zeichnen
    async Task RunOnSteps(string busyText, Func<List<EditStep>> work, string doneText)
    {
        Overlay.Visibility = Visibility.Visible;
        LoadingText.Text = busyText;
        Map.Busy = true;
        try
        {
            var touched = await Task.Run(work);
            foreach (var s in touched) Refresh(s);
            Recompute();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.F("{0} failed:", doneText) + "\n" + ex.Message, Loc.T("Edit history"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Map.Busy = false;
            Overlay.Visibility = Visibility.Collapsed;
            UpdateButtons();
            UpdateUndo();
            UpdateToolModule();
        }
    }

    // ---------- Fenster „Bearbeitungsreihenfolge“ ----------

    void StepsToggle_Click(object sender, RoutedEventArgs e) => SetStepsVisible(StepsToggle.IsChecked == true);

    void SetStepsVisible(bool on)
    {
        StepsToggle.IsChecked = on;
        if (on) stepsWindow.Show(); else stepsWindow.Hide();
        if (settings.ShowSteps != on) { settings.ShowSteps = on; settings.Save(); }
    }

    // aktive Schritte, darunter die zurückgenommenen (durchgestrichen); X löscht einen Eintrag
    void UpdateStepList()
    {
        var list = stepsWindow.StepList;
        list.Items.Clear();
        if (edits == null) return;
        int index = 0;
        foreach (var s in edits.Steps) AddStepItem(list, index++, s, true);
        foreach (var s in edits.Undone) AddStepItem(list, index++, s, false);
        if (index == 0) list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = Loc.T("No unsaved changes."), TextWrapping = TextWrapping.Wrap }, IsEnabled = false });
        stepsWindow.StepCountText.Text = index == 0 ? "" : Loc.F("{0} of {1} active", edits.Steps.Count, index);
    }

    void AddStepItem(ListBox list, int index, EditStep s, bool active)
    {
        var del = new Button { Content = Icons.XLg, FontFamily = IconUi.Font, FontSize = 11, Width = 26, Height = 22, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = Loc.T("Delete entry"), VerticalAlignment = VerticalAlignment.Center };
        del.Click += (_, _) => DeleteStep(index);
        var text = new TextBlock { Text = $"{index + 1}. {s.Title}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (!active)
        {
            text.Foreground = System.Windows.Media.Brushes.Gray;
            text.TextDecorations = TextDecorations.Strikethrough;
            text.ToolTip = Loc.T("undone – \"Redo\" brings it back");
        }
        var row = new DockPanel();
        DockPanel.SetDock(del, Dock.Right);
        row.Children.Add(del);
        // Ergebnis (z. B. ausgesparte Gebäude, noch anzupassende Kreuzungen) klein darunter – ersetzt das frühere Meldungsfeld links
        if (string.IsNullOrEmpty(s.Note)) row.Children.Add(text);
        else
        {
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(text);
            lines.Children.Add(new TextBlock { Text = s.Note, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = ThemeManager.Brush("SubtleTextBrush", System.Windows.Media.Brushes.DimGray), Margin = new Thickness(14, 1, 0, 0) });
            row.Children.Add(lines);
        }
        list.Items.Add(new ListBoxItem { Content = row, Padding = new Thickness(4, 3, 2, 3) });
    }

    async void DeployButton_Click(object sender, RoutedEventArgs e)
    {
        if (world == null) return;
        if (!await ConfirmSwitch(null)) return;
        string game = GameWorld(ask: true);
        if (game == null) return;
        if (GameSync.GameRunning())
        {
            MessageBox.Show(this, Loc.T("The game is still running. Please quit 7 Days to Die first, then use \"Copy to 7 Days to Die\" again."),
                Loc.T("Copy to 7 Days to Die"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (edits.HasChanges)
        {
            var a = MessageBox.Show(this, Loc.F("{0} change(s) are not saved yet – only the saved state goes into the game.\n\n" +
                "Save now and then copy?\n(No = copy without the unsaved changes)", edits.Steps.Count), Loc.T("Copy to 7 Days to Die"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (a == MessageBoxResult.Cancel) return;
            if (a == MessageBoxResult.Yes && !await Save()) return;
        }
        bool test = settings.DeployTestSpawn;
        world.UseTestSpawn = test;
        if (test != world.ActivateSpawns())   // spawnpoints.xml passend zur Auswahl aus den gespeicherten Dateien
        {
            MessageBox.Show(this, Loc.T("No test point is saved. Please set a test point – or untick \"Use test spawn point\"."),
                Loc.T("Copy to 7 Days to Die"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DeployButton.IsEnabled = false;
        try
        {
            var res = await CopyMapToGame(game);
            int deployed = WorldData.ReadSpawns(Path.Combine(world.Folder, WorldData.SpawnFile), false).Count;
            string spawnInfo = " " + (test ? Loc.T("In the game only the test point applies now.") : Loc.F("In the game the {0} play points apply.", deployed));
            string text = res.Copied.Count == 0
                ? Loc.T("Everything in the game was already up to date.") + spawnInfo
                : Loc.F("Copied into the game: {0}.", string.Join(", ", res.Copied)) + spawnInfo +
                  (res.Deleted.Count > 0 ? " " + Loc.F("Files generated by the game deleted ({0}), it creates them again.", res.Deleted.Count) : "") +
                  " " + Loc.F("Now start a NEW game on \"{0}\" in the game.", world.Name);
            // Das Spiel braucht beim ersten Laden der geänderten Welt sehr viel Speicher (27.10.2026: Absturz, während die App lief)
            if (MessageBox.Show(this, text + "\n\n" + Loc.T("The game needs a lot of memory when loading the world for the first time. Close the map editor now? (Recommended – the map is saved.)"),
                    Loc.T("Copy to 7 Days to Die"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("Copying failed:") + "\n" + ex.Message, Loc.T("Copy to 7 Days to Die"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DeployButton.IsEnabled = true;
        }
    }

    /// Weltordner im Spiel (gemerkt, sonst gesucht; mit ask = true notfalls den Nutzer fragen)
    string GameWorld(bool ask)
    {
        if (settings.GameWorlds.TryGetValue(world.Name, out var dir) && Directory.Exists(dir)) return dir;
        // erzeugte Welt (Terraingenerator): Ordner im Spiel gibt es erst nach dem ersten Übernehmen, GeneratedWorlds aber schon
        if (dir != null && File.Exists(Path.Combine(world.Folder, "yamee_generator.txt")) && Directory.Exists(Path.GetDirectoryName(dir))) return dir;
        dir = GameSync.GuessGameWorld(world.Name);
        if (dir != null || !ask) return dir;
        var dlg = new OpenFolderDialog { Title = Loc.F("Choose the folder of the world \"{0}\" in the game (GeneratedWorlds\\{0})", world.Name) };
        if (dlg.ShowDialog(this) != true) return null;
        settings.GameWorlds[world.Name] = dlg.FolderName;
        settings.Save();
        return dlg.FolderName;
    }

    void UpdateUndo()
    {
        junctionCache.Clear();   // Gelände hat sich geändert: Kreuzungen neu bestimmen
        int n = edits?.Steps.Count ?? 0;
        string app = AppSettings.IsDev ? "YAMEE (Dev)" : "YAMEE";   // Entwicklungsstand im Titel erkennbar
        Title = world == null ? app : $"{app} – {world.Name}" + (n > 0 ? " *" : "");
        SaveButton.IsEnabled = n > 0;
        stepsWindow.RedoButton.IsEnabled = TopRedoButton.IsEnabled = edits?.CanRedo == true;
        ChangesText.Text = world == null ? "" : n == 0 ? Loc.T("Everything saved.") : Loc.F("Not saved yet: {0} change(s) (Ctrl+S).", n);
        ChangesText.Foreground = n == 0 ? ThemeManager.Brush("SubtleTextBrush", System.Windows.Media.Brushes.DimGray) : ThemeManager.Brush("ErrorBrush", System.Windows.Media.Brushes.Firebrick);
        UpdateStepList();
        UpdateSpawns();
        UpdatePrefabEdit();
        if (n > 0)
        {
            stepsWindow.UndoButton.IsEnabled = TopUndoButton.IsEnabled = true;
            stepsWindow.UndoText.Text = Loc.F("Undo (Ctrl+Z) takes back: {0}", edits.Steps[^1].Title);
            return;
        }
        var b = world == null ? null : Backups.List(world).FirstOrDefault();
        stepsWindow.UndoButton.IsEnabled = TopUndoButton.IsEnabled = b != null;
        stepsWindow.UndoText.Text = b == null ? Loc.T("No backup yet.") : Loc.F("Undo restores the last backup: {0} from {1:MM-dd HH:mm}", b.Description.Split('\n')[0].Trim(), b.Time);
    }
}
