using System.Text.Json;

namespace MapEditor;

/// settings of the user (last opened world, game folder), saved as settings.json in the data folder (Documents\YAMEE or the folder the user chose).
public sealed class AppSettings
{
    public string WorldFolder { get; set; }
    public string GameFolder { get; set; }

    /// game folder used for everything: chosen by the user, otherwise found automatically. Only one (the user's decision
    /// 2026-11-13: further game folders for older versions were confusing; World > Check shows what a project lacks there)
    [System.Text.Json.Serialization.JsonIgnore]
    public string MainGameFolder => GameFolder ?? GuessGameFolder();
    /// world folder in the game per world name, if it was not found automatically (remembered when loading from GeneratedWorlds)
    public Dictionary<string, string> GameWorlds { get; set; } = new();
    /// old setting (before 2026-11-23, development build only): read once on loading and moved to the data folder, then gone
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string ProjectFolder { get; set; }
    /// values for new roads (settings window); null = factory values from Standardwerte.ini
    public RoadValues Asphalt { get; set; }
    public RoadValues Gravel { get; set; }
    /// eraser (start values from Standardwerte.ini as long as settings.json contains nothing):
    /// unevenness 0 = none, 1 = light, 2 = medium, 3 = strong; brush size (diameter, m); shape square/round
    public int EraserBumps { get; set; } = StandardValues.Current.EraserBumps;
    public double EraserSize { get; set; } = StandardValues.Current.EraserSize;
    public bool EraserSquare { get; set; } = StandardValues.Current.EraserSquare;
    /// biome brush (start values from default.ini): brush size (diameter, m), shape square/round, last chosen biome
    public double BiomeBrushSize { get; set; } = StandardValues.Current.BiomeBrushSize;
    public bool BiomeBrushSquare { get; set; } = StandardValues.Current.BiomeBrushSquare;
    public int BiomeChoice { get; set; }
    /// terrain brush: kind (TerrainKind), brush size (diameter, m), shape, strength per kind, soft edge (%)
    public int TerrainKind { get; set; }
    public double TerrainSize { get; set; } = 32;
    public bool TerrainSquare { get; set; }
    /// brush shape (BrushShape as a number; 0 = round, then TerrainSquare from older settings still applies)
    public int TerrainShape { get; set; }
    public double[] TerrainStrengths { get; set; } = (double[])Map.TerrainBrush.DefaultStrength.Clone();
    public double TerrainSoftness { get; set; } = 60;
    /// roads when shaping (TerrainRoads): 0 fixed, 1 light, 2 with
    public int TerrainRoads { get; set; }
    /// road may tilt sideways (otherwise equal over the whole width), limit gradient (on/off) and limit (%)
    public bool TerrainRoadTilt { get; set; }
    /// single buildings when shaping (TerrainPois): 0 fixed, 1 light, 2 take along
    public int TerrainPois { get; set; }
    /// height limits of the brush (meters, absolute): on/off, lowest and highest height
    public bool TerrainHeightLimit { get; set; }
    public double TerrainMinHeight { get; set; } = 30;
    public double TerrainMaxHeight { get; set; } = 60;
    public bool TerrainLimitGrade { get; set; } = true;
    public double TerrainMaxGrade { get; set; } = 25;
    /// mountain brush: kind (MountainKind), shape 1/2 (0/1), height, size, rotation, mirroring, steepness, ruggedness, roads and buildings (like the terrain)
    public int MountainKind { get; set; }
    public int MountainVariant { get; set; }
    public double MountainHeight { get; set; } = 90;
    public double MountainSize { get; set; } = 600;
    public double MountainRotation { get; set; }
    public bool MountainMirror { get; set; }
    public double MountainSteep { get; set; } = 100;
    public double MountainRough { get; set; } = 30;
    public int MountainRoads { get; set; }
    public int MountainPois { get; set; }
    /// mountain range in the drawn area: kind (RangeKind), foothills (RangeEdge), height, slope width, peak distance, ruggedness
    public int RangeKind { get; set; }
    public int RangeEdge { get; set; } = 3;
    public double RangeHeight { get; set; } = 90;
    public double RangeFoot { get; set; } = 150;
    public double RangeSpacing { get; set; } = 250;
    public double RangeRough { get; set; } = 30;
    public double RangeCrag { get; set; } = 60;
    /// mountain ranges: roads and buildings underneath (0 fixed, 1 light, 2 with), own values (before like with the mountain)
    public int RangeRoads { get; set; }
    public int RangePois { get; set; }
    /// lake: kind (0 lake, 1 remove water), water level (m), greatest depth (m), shore width (m), unevenness of the ground (0–3)
    public int WaterKind { get; set; }
    public double WaterLevel { get; set; } = 30;
    public double WaterDepth { get; set; } = 6;
    public double WaterWDepth { get; set; } = 4;
    public double WaterWidth { get; set; } = 12;
    public double WaterIrregular { get; set; } = 10;
    public int WaterBelow { get; set; } = 0;
    public int WaterCraterLevel { get; set; } = 0;
    public int WaterMinDepth { get; set; } = 10;
    public double WaterCraterHeight { get; set; } = 6;
    public int WaterBiome { get; set; } = -1;
    public double WaterShore { get; set; } = 14;
    public int WaterBumps { get; set; } = 1;
    public int WaterShoreStyle { get; set; }
    public int WaterBedFeatures { get; set; } = 1;
    public bool WaterIsland { get; set; }
    public double RiverWidthMin { get; set; } = 16;
    public double RiverWidthMax { get; set; } = 28;
    public double RiverDepth { get; set; } = 4.5;
    /// bridge tool: gravel approaches instead of asphalt (BridgeSite.Gravel)
    public bool BridgeGravel { get; set; }
    /// bridge tool: kit chosen in the window of the bridges (id, null = randomly one that fits)
    public string BridgeKitChoice { get; set; }
    /// edit road: profile kind, keep intersections, automatic banking (the user's request 2026-11-10: they are in default.ini / user.ini)
    public bool EditEven { get; set; } = true;
    public bool EditKeepJunctions { get; set; }
    public bool EditAutoCross { get; set; }
    /// maximum automatic banking in %
    public double EditAutoCrossMax { get; set; } = 8;
    /// road generation from point to point (the user's request 2026-11-17): roadway width asphalt/gravel (m), usual and highest gradient (%),
    /// gravel paths to buildings near the route (up to this distance, m), gateway tile where the route meets an existing road
    public double RouteAsphaltWidth { get; set; } = 16;
    public double RouteGravelWidth { get; set; } = 5;
    public double RouteGrade { get; set; } = 10;
    public double RouteMaxGrade { get; set; } = 16;
    public bool RoutePois { get; set; }
    public double RoutePoiDistance { get; set; } = 250;
    public bool RouteJunctionTiles { get; set; }
    /// banking in curves (inside lower) up to RouteBankingMax % – instead of the natural cross slope of the road builder
    public bool RouteBanking { get; set; } = true;
    public double RouteBankingMax { get; set; } = 2;
    /// wasteland only: interrupt route / manual brush roads at bomb craters and earthquake cracks
    public bool RouteBreakInWasteland { get; set; } = true;
    public bool BrushBreakInWasteland { get; set; } = true;
    /// window "Edit order" visible
    public bool ShowSteps { get; set; } = true;
    /// rulers at the edge of the map (View → Rulers)
    public bool ShowRuler { get; set; } = true;
    /// resolution of the exported map image per side (512, 1024, 2048 or 4096; the user's request 2026-11-20)
    public int ExportSize { get; set; } = 1024;
    /// info windows (e.g. "world created") switched off by the check mark in them; switched on again in the tab Settings
    public bool InfoWindowsOff { get; set; }
    /// mark city exits that lead nowhere (View → Dead ends; the user's request 2026-11-14: on by default)
    public bool ShowDeadEnds { get; set; } = true;
    /// layer "Bridges" (View): pieces of the bridge kits, standard on (the user's request 2026-11-26)
    public bool ShowBridges { get; set; } = true;
    /// dark interface (View → Dark theme)
    public bool DarkTheme { get; set; }
    /// building list: also show buildings that are not on the map
    public bool ListAllPrefabs { get; set; }
    /// building list: sorted by "name", "tier" or "count", descending
    public string PrefabSort { get; set; } = "name";
    public bool PrefabSortDescending { get; set; }
    /// set the test point at the start of a new road when applying (earlier behavior, now switchable)
    public bool TestSpawnAtRoad { get; set; } = true;
    /// "Copy to 7 Days to Die" writes the test point instead of the play points as spawnpoints.xml into the game
    public bool DeployTestSpawn { get; set; }
    /// width of the right side (building list), draggable at the left edge
    public double RightPanelWidth { get; set; } = 380;
    public double LeftPanelWidth { get; set; } = 324;   // width of the left side with the values of the tool
    public bool RightPanelCollapsed { get; set; }   // building list on the right collapsed
    /// snap city tiles to the 150 m grid when placing/moving (Alt pressed = reversed)
    public bool TileGrid { get; set; } = true;
    /// language of the interface ("English" or name of a file in Languages\); null = according to Windows
    public string Language { get; set; }

    /// folder of the projects: installed/portable the data folder itself (projects, Backups and settings side by side, no "Maps" subfolder; the user's request 2026-11-23);
    /// development build as before from the settings
    [System.Text.Json.Serialization.JsonIgnore]
    public string ProjectPath => DataRoot;

    /// folder that the user sees in the welcome window and settings: installed/portable the data folder (containing Maps, Backups, settings)
    [System.Text.Json.Serialization.JsonIgnore]
    public string ChosenFolder => ProjectPath;

    /// settings for a new road of this surface (values from the settings window, otherwise factory values from the .ini)
    public Map.RoadSettings RoadFor(Map.Surface s)
    {
        var r = StandardValues.Current.RoadFor(s);
        (s == Map.Surface.Asphalt ? Asphalt : s == Map.Surface.Gravel ? Gravel : null)?.ApplyTo(r);
        return r;
    }

    /// save project folder: relative inside the program folder (moves along with the program), otherwise complete
    public void SetProjectPath(string full) => SetDataRoot(full);

    /// GeneratedWorlds of the game (mods like Undead Legacy first), for choosing a world
    /// for the game in gameFolder: its world folder (with Undead Legacy installed the one of the mod) – also when it does not exist yet
    public static string GuessGeneratedWorlds(string gameFolder) =>
        gameFolder != null && GameLocator.ProfileOf(gameFolder) is GameProfile p ? GameLocator.WorldsFolderFor(p) : GameLocator.WorldFolders().FirstOrDefault();

    /// first start on this PC (no settings found): show the window "Welcome"
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FirstStart { get; private set; }

    /// project folder on a new PC: the data folder (nobody may write next to the exe under "Program Files")
    public static string DefaultProjectPath => DataRoot;

    // development build (built from the source, DEV), portable build (PORTABLE) and installed version (Release)
#if DEV
    public static readonly bool IsDev = true;
#else
    public static readonly bool IsDev = false;
#endif
#if PORTABLE
    /// portable version (built with YameeEdition=Portable; no portable.txt needed)
    public static readonly bool IsPortable = true;
#else
    /// portable version (built with YameeEdition=Portable; no portable.txt needed)
    public static readonly bool IsPortable = false;
#endif
    /// folder for error.log and other data: the data folder
    public static string DataFolder => DataRoot;

    /// folder of settings.json and user.ini: installed version %APPDATA%\YAMEE (Program Files is not writable),
    /// development build and portable version next to the exe
    public static string ConfigFolder => IsDev || IsPortable
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YAMEE");

    // ---- data folder: projects, Backups, error.log (settings.json and user.ini lie in ConfigFolder) ----
    // order: portable build → <exe folder>\Data; otherwise the folder the user chose (CustomDataFolder in settings.json,
    // only if it is not the default); otherwise Documents\YAMEE.
    static string dataRoot;

    /// the data folder the user chose, if any: read straight from settings.json (needed before the settings are loaded)
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string CustomDataFolder { get; set; }

    static string ChosenRoot()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.TryGetProperty(nameof(CustomDataFolder), out var v) && v.ValueKind == JsonValueKind.String
                && v.GetString()?.Trim().Trim('"') is { Length: > 0 } p && Path.IsPathRooted(p)) return Path.GetFullPath(p);
        }
        catch (Exception) { }
        return null;
    }

    /// default data folder: Documents\YAMEE or, with the portable version, Data next to the exe
    public static string DefaultDataRoot => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "YAMEE");

    /// data folder (projects, backups, error log)
    public static string DataRoot => dataRoot ??= (IsPortable ? null : ChosenRoot()) ?? DefaultDataRoot;

    /// change the data folder: equal to the default → nothing remembered, otherwise the path goes into settings.json (with the next save).
    /// Not possible with the portable version.
    public void SetDataRoot(string folder)
    {
        if (IsPortable) return;
        folder = Path.GetFullPath(folder.Trim()).TrimEnd('\\', '/');
        bool isDefault = string.Equals(folder, DefaultDataRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        CustomDataFolder = isDefault ? null : folder;
        dataRoot = folder;
    }

    // MAPEDITOR_SETTINGS (environment variable) = own file so that test runs do not overwrite the settings of the user
    static string FilePath => Environment.GetEnvironmentVariable("MAPEDITOR_SETTINGS") ?? Path.Combine(ConfigFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                loaded.ProjectFolder = null;   // old setting, no longer used
                // the installer writes a file with only the chosen data folder: that is still a first start
                using (var doc = JsonDocument.Parse(File.ReadAllText(FilePath)))
                    loaded.FirstStart = doc.RootElement.EnumerateObject().All(p => p.Name == nameof(CustomDataFolder));
                UserValues.ApplyTo(loaded, migrate: true);   // tool values: default.ini + user.ini (before in settings.json)
                return loaded;
            }
        }
        catch (Exception)
        {
            // broken file: continue with default values, but keep the broken file as .bad (throw nothing away, audit finding F3)
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch (Exception) { }
        }
        var fresh = new AppSettings { FirstStart = true };
        UserValues.ApplyTo(fresh, migrate: false);
        return fresh;
    }

    // values that are in default.ini/user.ini are no longer in settings.json
    static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers = { ti =>
            {
                if (ti.Type != typeof(AppSettings)) return;
                for (int i = ti.Properties.Count - 1; i >= 0; i--) if (UserValues.IsManaged(ti.Properties[i].Name)) ti.Properties.RemoveAt(i);
            } }
        }
    };

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        // first into a new file, then replace: if something aborts, the old file stays intact (audit finding F3)
        string tmp = FilePath + ".neu";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, SaveOptions));
        File.Move(tmp, FilePath, true);
        UserValues.Save(this);   // changed tool values (deviations from default.ini) to user.ini
    }

    /// searches upwards from the program folder for the project folder with "My World" (the app lies in 7Map\app\…); development build only
    public static string GuessWorldFolder()
    {
#if DEV
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "My World");
            if (File.Exists(Path.Combine(candidate, "dtm.raw"))) return candidate;
        }
#endif
        return null;
    }

    /// game folder, found automatically (Steam libraries, see GameLocator); null = not found
    public static string GuessGameFolder() => GameLocator.Find()?.Folder;
}

/// values of a road kind as they are in the settings window (gradient as a fraction, 0.08 = 0.8 m per 10 m)
public sealed class RoadValues
{
    public double Width { get; set; }
    public double Shoulder { get; set; }
    /// grass edge per side (m), in addition to the gravel edge; 0 = none (older settings.json do not have the value yet)
    public double GrassEdge { get; set; }
    public double MaxGrade { get; set; }
    public double Smooth { get; set; }
    public double Blend { get; set; }
    public double CutSlope { get; set; }
    /// intersection level up to (fraction, 0.02 = 2 %); null = default (older settings.json do not have the value yet)
    public double? JunctionTolerance { get; set; }
    /// banking up to (fraction); null = default
    public double? CrossSlope { get; set; }

    public static RoadValues From(Map.RoadSettings r) => new()
    {
        Width = r.Width, Shoulder = r.Shoulder, GrassEdge = r.GrassEdge, MaxGrade = r.MaxGrade, Smooth = r.Smooth, Blend = r.Blend, CutSlope = r.CutSlope,
        JunctionTolerance = r.JunctionTolerance, CrossSlope = r.MaxCrossSlope,
    };

    public void ApplyTo(Map.RoadSettings r)
    {
        r.Width = Width; r.Shoulder = Shoulder; r.GrassEdge = GrassEdge; r.MaxGrade = MaxGrade; r.Smooth = Smooth; r.Blend = Blend; r.CutSlope = CutSlope;
        if (JunctionTolerance is double t) r.JunctionTolerance = t;
        if (CrossSlope is double c) r.MaxCrossSlope = c;
    }
}
