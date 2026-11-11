using System.Text.Json;

namespace MapEditor;

/// Einstellungen des Nutzers (zuletzt geöffnete Welt, Spielordner), gespeichert unter %APPDATA%\YAMEE\settings.json
/// (Entwicklungsstand: %APPDATA%\YAMEE-Dev).
public sealed class AppSettings
{
    public string WorldFolder { get; set; }
    public string GameFolder { get; set; }
    /// Weltordner im Spiel je Weltname, falls er nicht automatisch gefunden wurde (beim Laden aus GeneratedWorlds gemerkt)
    public Dictionary<string, string> GameWorlds { get; set; } = new();
    /// Projektordner für die Arbeitskopien; relativ = bezogen auf den Ordner von YAMEE.exe (Wunsch des Nutzers)
    public string ProjectFolder { get; set; } = "Maps";
    /// Werte für neue Straßen (Einstellungsfenster); null = Werkswerte aus Standardwerte.ini
    public RoadValues Asphalt { get; set; }
    public RoadValues Gravel { get; set; }
    /// Radiergummi (Startwerte aus Standardwerte.ini, solange in settings.json nichts steht):
    /// Unebenheit 0 = keine, 1 = leicht, 2 = mittel, 3 = stark; Pinselgröße (Durchmesser, m); Form eckig/rund
    public int EraserBumps { get; set; } = StandardValues.Current.EraserBumps;
    public double EraserSize { get; set; } = StandardValues.Current.EraserSize;
    public bool EraserSquare { get; set; } = StandardValues.Current.EraserSquare;
    /// Biom-Pinsel (Startwerte aus default.ini): Pinselgröße (Durchmesser, m), Form eckig/rund, zuletzt gewähltes Biom
    public double BiomeBrushSize { get; set; } = StandardValues.Current.BiomeBrushSize;
    public bool BiomeBrushSquare { get; set; } = StandardValues.Current.BiomeBrushSquare;
    public int BiomeChoice { get; set; }
    /// Terrain-Pinsel: Art (TerrainKind), Pinselgröße (Durchmesser, m), Form, Stärke je Art, weicher Rand (%)
    public int TerrainKind { get; set; }
    public double TerrainSize { get; set; } = 32;
    public bool TerrainSquare { get; set; }
    /// Pinselform (BrushShape als Zahl; 0 = Rund, dann gilt noch TerrainSquare aus älteren Einstellungen)
    public int TerrainShape { get; set; }
    public double[] TerrainStrengths { get; set; } = (double[])Map.TerrainBrush.DefaultStrength.Clone();
    public double TerrainSoftness { get; set; } = 60;
    /// Straßen beim Formen (TerrainRoads): 0 fest, 1 leicht, 2 mit
    public int TerrainRoads { get; set; }
    /// Straße darf sich quer neigen (sonst über die ganze Breite gleich), Gefälle begrenzen (an/aus) und Grenze (%)
    public bool TerrainRoadTilt { get; set; }
    /// Einzelne Gebäude beim Formen (TerrainPois): 0 fest, 1 leicht, 2 mitnehmen
    public int TerrainPois { get; set; }
    /// Höhengrenzen des Pinsels (Meter, absolut): an/aus, kleinste und größte Höhe
    public bool TerrainHeightLimit { get; set; }
    public double TerrainMinHeight { get; set; } = 30;
    public double TerrainMaxHeight { get; set; } = 60;
    public bool TerrainLimitGrade { get; set; } = true;
    public double TerrainMaxGrade { get; set; } = 25;
    /// Berg-Pinsel: Art (MountainKind), Form 1/2 (0/1), Höhe, Größe, Drehung, Spiegeln, Steilheit, Zerklüftung, Straßen und Gebäude (wie beim Terrain)
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
    /// Gebirgskette im gezeichneten Bereich: Art (RangeKind), Ausläufer (RangeEdge), Höhe, Hangbreite, Gipfelabstand, Zerklüftung
    public int RangeKind { get; set; }
    public int RangeEdge { get; set; } = 3;
    public double RangeHeight { get; set; } = 90;
    public double RangeFoot { get; set; } = 150;
    public double RangeSpacing { get; set; } = 250;
    public double RangeRough { get; set; } = 30;
    public double RangeCrag { get; set; } = 60;
    /// Gebirgsketten: Straßen und Gebäude darunter (0 fest, 1 leicht, 2 mit), eigene Werte (früher wie beim Berg)
    public int RangeRoads { get; set; }
    public int RangePois { get; set; }
    /// See: Art (0 See, 1 Wasser entfernen), Wasserspiegel (m), größte Tiefe (m), Uferbreite (m), Unebenheit des Bodens (0–3)
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
    /// Straße bearbeiten: Profilart, Kreuzungen halten, automatische Querneigung (Wunsch des Nutzers 10.11.2026: stehen in default.ini / user.ini)
    public bool EditEven { get; set; } = true;
    public bool EditKeepJunctions { get; set; }
    public bool EditAutoCross { get; set; }
    /// höchste automatische Querneigung in %
    public double EditAutoCrossMax { get; set; } = 8;
    /// Fenster „Bearbeitungsreihenfolge“ sichtbar
    public bool ShowSteps { get; set; } = true;
    /// Lineale am Rand der Karte (Ansicht → Lineale)
    public bool ShowRuler { get; set; } = true;
    /// dunkle Oberfläche (Ansicht → Dunkles Design)
    public bool DarkTheme { get; set; }
    /// Gebäudeliste: auch Gebäude zeigen, die nicht auf der Karte stehen
    public bool ListAllPrefabs { get; set; }
    /// Gebäudeliste: sortiert nach "name", "tier" oder "count", absteigend
    public string PrefabSort { get; set; } = "name";
    public bool PrefabSortDescending { get; set; }
    /// Testpunkt beim Übernehmen einer neuen Straße an ihren Anfang setzen (bisheriges Verhalten, jetzt abschaltbar)
    public bool TestSpawnAtRoad { get; set; } = true;
    /// „Übernehmen in 7 Days to Die“ schreibt den Testpunkt statt der Spielpunkte als spawnpoints.xml ins Spiel
    public bool DeployTestSpawn { get; set; }
    /// Breite der rechten Seite (Gebäudeliste), am linken Rand ziehbar
    public double RightPanelWidth { get; set; } = 380;
    public double LeftPanelWidth { get; set; } = 324;   // Breite der linken Seite mit den Werten des Werkzeugs
    public bool RightPanelCollapsed { get; set; }   // Gebäudeliste rechts eingeklappt
    /// Stadt-Kacheln beim Platzieren/Verschieben im 150-m-Raster einrasten (Alt gedrückt = umgekehrt)
    public bool TileGrid { get; set; } = true;
    /// Sprache der Oberfläche („English“ oder Name einer Datei in Languages\); null = nach Windows
    public string Language { get; set; }

    /// Projektordner als vollständiger Pfad
    public string ProjectPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, string.IsNullOrWhiteSpace(ProjectFolder) ? "Maps" : ProjectFolder));

    /// Einstellungen für eine neue Straße dieses Belags (Werte aus dem Einstellungsfenster, sonst Werkswerte aus der .ini)
    public Map.RoadSettings RoadFor(Map.Surface s)
    {
        var r = StandardValues.Current.RoadFor(s);
        (s == Map.Surface.Asphalt ? Asphalt : s == Map.Surface.Gravel ? Gravel : null)?.ApplyTo(r);
        return r;
    }

    /// Projektordner speichern: innerhalb des Programmordners relativ (wandert mit dem Programm mit), sonst vollständig
    public void SetProjectPath(string full)
    {
        string rel = Path.GetRelativePath(AppContext.BaseDirectory, full);
        ProjectFolder = rel.StartsWith("..") || Path.IsPathRooted(rel) ? full : rel;
    }

    /// GeneratedWorlds des Spiels (Mods wie Undead Legacy zuerst), zum Auswählen einer Welt
    public static string GuessGeneratedWorlds() => GameLocator.WorldFolders().FirstOrDefault();

    /// Erster Start auf diesem PC (keine Einstellungen gefunden): Fenster „Willkommen“ zeigen
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FirstStart { get; private set; }

    /// Projektordner bei einem neuen PC: Dokumente\YAMEE\Maps (neben der exe darf unter „Programme“ niemand schreiben)
    public static string DefaultProjectPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "YAMEE", "Maps");

    // Entwicklungsstand (aus dem Quelltext gebaut, DEV) und weitergegebene/installierte Fassung (publish.bat, YameeEdition=Release)
    // haben getrennte Daten (Wunsch des Nutzers 30.10.2026: Installieren und Erststart dürfen nichts von der Entwicklung wissen)
#if DEV
    public const bool IsDev = true;
    const string DataFolderName = "YAMEE-Dev";
#else
    public const bool IsDev = false;
    const string DataFolderName = "YAMEE";
#endif
    /// Ordner für Einstellungen (und default.ini, wenn neben der exe nicht schreibbar): %APPDATA%\YAMEE bzw. YAMEE-Dev
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataFolderName);

    // MAPEDITOR_SETTINGS (Umgebungsvariable) = eigene Datei, damit Testläufe die Einstellungen des Nutzers nicht überschreiben
    static string FilePath => Environment.GetEnvironmentVariable("MAPEDITOR_SETTINGS") ?? Path.Combine(DataFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                UserValues.ApplyTo(loaded, migrate: true);   // Werkzeugwerte: default.ini + user.ini (früher in settings.json)
                return loaded;
            }
        }
        catch (Exception)
        {
            // kaputte Datei: mit Standardwerten weitermachen, die kaputte Datei aber als .bad aufheben (nichts wegwerfen, Prüfbericht F3)
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch (Exception) { }
        }
        var fresh = new AppSettings { FirstStart = true };
        fresh.SetProjectPath(DefaultProjectPath);
        UserValues.ApplyTo(fresh, migrate: false);
        return fresh;
    }

    // Werte, die in default.ini/user.ini stehen, kommen nicht mehr in settings.json
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
        // erst in eine neue Datei, dann ersetzen: bricht etwas ab, bleibt die alte Datei heil (Prüfbericht F3)
        string tmp = FilePath + ".neu";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, SaveOptions));
        File.Move(tmp, FilePath, true);
        UserValues.Save(this);   // geänderte Werkzeugwerte (Abweichungen von default.ini) nach user.ini
    }

    /// Sucht vom Programmordner aufwärts nach dem Projektordner mit „My World“ (die App liegt in 7Map\app\…); nur Entwicklungsstand
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

    /// Spielordner, automatisch gefunden (Steam-Bibliotheken, siehe GameLocator); null = nicht gefunden
    public static string GuessGameFolder() => GameLocator.Find()?.Folder;
}

/// Werte einer Straßenart, wie sie im Einstellungsfenster stehen (Steigung als Anteil, 0,08 = 0,8 m auf 10 m)
public sealed class RoadValues
{
    public double Width { get; set; }
    public double Shoulder { get; set; }
    /// Rasenkante je Seite (m), zusätzlich zum Schotterrand; 0 = keine (ältere settings.json haben den Wert noch nicht)
    public double GrassEdge { get; set; }
    public double MaxGrade { get; set; }
    public double Smooth { get; set; }
    public double Blend { get; set; }
    public double CutSlope { get; set; }
    /// Kreuzung eben bis (Anteil, 0,02 = 2 %); null = Standard (ältere settings.json haben den Wert noch nicht)
    public double? JunctionTolerance { get; set; }
    /// Querneigung bis (Anteil); null = Standard
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
