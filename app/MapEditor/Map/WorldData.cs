using System.Globalization;
using System.Xml;

namespace MapEditor.Map;

/// Eine generierte Welt: Höhen, Biome, Gebäude und Spawnpunkte. Welt-Koordinaten in Metern, x nach Osten, z nach Norden,
/// Mitte der Karte = (0, 0).
public sealed class WorldData
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Folder { get; private set; }
    public string Name => new DirectoryInfo(Folder).Name;
    public int Size { get; private set; }
    public int Half => Size / 2;
    /// dtm.raw: 16 Bit je Meter, Höhe = Wert / 256, Zeile 0 = Süden
    public ushort[] Heights { get; private set; }
    /// Biom-Index je Pixel von biomes.png (Zeile 0 = Norden)
    public byte[] Biomes { get; private set; }
    /// Belag aus splat3_processed.png in den Stadt-Kacheln (Straßen der Kacheln und Parkplätze ihrer Gebäude); null = keine Datei
    public byte[] CityMask { get; private set; }
    /// Straßenausgänge der Stadt-Kacheln (Mitte, wo Asphalt/Schotter den Kachelrand erreicht): Fangpunkte zum Anschließen
    public List<(double X, double Z, PlacedPrefab Tile)> TileExits { get; } = new();

    // Belag der Kacheln aus ihren Blöcken (PrefabSurface: oberster Geländeblock; an der Originalwelt geprüft, 393 Kacheln:
    // 99,0 % der berechneten Fläche hat das Spiel genauso, 99,7 % seiner Kachelstraßen getroffen). Nur wo city noch leer ist.
    // Ausgänge (Hinweis des Nutzers 30.10.2026: vorher auch an Parkplätzen/Gehwegen am Rand): nur wo Belag die Mitte einer Seite
    // erreicht – dort liegen in allen Kacheln des Spiels die Straßen (Blöcke 70–79 von 150) –, höchstens so viele wie die Form
    // im Namen hat, die schmalsten zuerst. Ergebnis an der Originalwelt gegen die Nachbarn geprüft (metest `tilesides`/`tileblocks`):
    // straight S+N, corner S+O, t S+O+N, gateway_t S+O+W, cap S, intersection alle (ungedreht). Fangpunkt = Mitte der Seite.
    void AddTileSurfaces(List<PlacedPrefab> tiles, string gameFolder, byte[] city)
    {
        this.gameFolder = gameFolder;
        foreach (var t in tiles)
        {
            var local = TileSurface(t, out var exits);
            if (local == null) continue;
            for (int oz = 0; oz < t.SizeZ; oz++)
                for (int ox = 0; ox < t.SizeX; ox++)
                {
                    byte k = local[ox + oz * t.SizeX];
                    int x = t.X + ox, z = t.Z + oz;
                    if (k != 0 && Contains(x, z) && city[Index(x, z)] == 0) city[Index(x, z)] = k;
                }
            foreach (var (px, pz) in exits) TileExits.Add((px, pz, t));
        }
    }

    string gameFolder;
    Dictionary<string, string> prefabFiles;
    readonly Dictionary<string, (byte[] Grid, int SX, int SZ)> tileGrids = new(StringComparer.OrdinalIgnoreCase);

    /// Belag eines Gebäudes aus seinen Blöcken, ungedreht (je Name einmal gelesen; auch der Hintergrund liest hier)
    (byte[] Grid, int SX, int SZ) SurfaceGridOf(string name)
    {
        lock (tileLock)
        {
            if (!tileGrids.TryGetValue(name, out var g))
            {
                prefabFiles ??= gameFolder == null ? new Dictionary<string, string>() : PrefabCatalog.AllFiles(gameFolder);
                byte[] grid = null;
                int sx = 0, sz = 0;
                try { if (prefabFiles.TryGetValue(name, out var f)) grid = PrefabSurface.Read(f, out sx, out sz); } catch (Exception) { }
                tileGrids[name] = g = (grid, sx, sz);
            }
            return g;
        }
    }

    readonly Dictionary<string, List<(double LX, double LZ, int Width, byte Kind)>> edgeExitsByName = new(StringComparer.OrdinalIgnoreCase);
    /// false, bis EdgeExitsWarmUp fertig ist: bis dahin zählen nur schon gelesene Gebäude (das Lesen aller Blockdateien dauert ca. 3 s)
    public volatile bool EdgeExitsReady;

    /// Anschlüsse eines Gebäudes aus seinem eigenen Belag (PrefabSurface.EdgeExits), ungedreht; leer = keine oder Blöcke nicht lesbar;
    /// null = noch nicht gelesen (nur wenn cachedOnly)
    public List<(double LX, double LZ, int Width, byte Kind)> EdgeExitsOf(string name, PrefabDef def, bool cachedOnly)
    {
        lock (tileLock) if (edgeExitsByName.TryGetValue(name, out var cached)) return cached;
        if (cachedOnly) return null;
        var g = SurfaceGridOf(name);
        var result = g.Grid != null && def != null && g.SX == def.SizeX && g.SZ == def.SizeZ ? PrefabSurface.EdgeExits(g.Grid, g.SX, g.SZ) : new();
        lock (tileLock) edgeExitsByName[name] = result;
        return result;
    }

    /// alle Gebäude der Karte einmal lesen (im Hintergrund nach dem Laden; danach zählen auch später gesetzte Gebäude sofort)
    public void EdgeExitsWarmUp()
    {
        foreach (var g in Prefabs.Where(p => !p.IsTile && p.Def != null && p.Def.Kind == PrefabKind.Poi).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            try { EdgeExitsOf(g.Key, g.First().Def, false); }
            catch (Exception) { lock (tileLock) edgeExitsByName[g.Key] = new(); }   // unlesbare Blockdatei: keine Anschlüsse
        }
        EdgeExitsReady = true;
    }

    /// Belag einer Kachel in Welt-Ausrichtung (Index (x − t.X) + (z − t.Z)·Breite) und ihre Straßenausgänge (Mitte der Seite);
    /// null = Blöcke der Kachel nicht lesbar
    public byte[] TileSurface(PlacedPrefab t, out List<(double X, double Z)> exits)
    {
        exits = new List<(double, double)>();
        if (t.Def == null) return null;
        var g = SurfaceGridOf(t.Name);
        if (g.Grid == null || g.SX != t.Def.SizeX || g.SZ != t.Def.SizeZ) return null;
        var local = new byte[t.SizeX * t.SizeZ];   // in Welt-Ausrichtung
        for (int lz = 0; lz < g.SZ; lz++)
            for (int lx = 0; lx < g.SX; lx++)
            {
                byte k = g.Grid[lx + lz * g.SX];
                if (k == 0) continue;
                var (wx, wz) = PlacedPrefab.Rotate(lx + 0.5, lz + 0.5, t.Rotation, g.SX, g.SZ);
                int ox = (int)Math.Floor(wx), oz = (int)Math.Floor(wz);
                if (ox < 0 || oz < 0 || ox >= t.SizeX || oz >= t.SizeZ) continue;
                local[ox + oz * t.SizeX] = k;
            }
        int w = t.SizeX, d = t.SizeZ;
        // Kandidat je Seite: Belag am Rand, der die Mitte der Seite überdeckt; Länge dieses Abschnitts
        var candidates = new List<(int Length, double X, double Z)>();
        void Side(int n, Func<int, byte> at, double px, double pz)
        {
            int c = n / 2;
            if (at(c) == 0 && at(c - 1) == 0) return;
            int a = at(c) != 0 ? c : c - 1, b = a;
            while (a > 0 && at(a - 1) != 0) a--;
            while (b < n - 1 && at(b + 1) != 0) b++;
            candidates.Add((b - a + 1, px, pz));
        }
        Side(w, i => local[i], t.X + w / 2.0, t.Z);                                  // Süden
        Side(w, i => local[i + (d - 1) * w], t.X + w / 2.0, t.Z + d);                // Norden
        Side(d, i => local[i * w], t.X, t.Z + d / 2.0);                              // Westen
        Side(d, i => local[(w - 1) + i * w], t.X + w, t.Z + d / 2.0);                // Osten
        // mehr Kandidaten als die Form erlaubt (z. B. Gehweg/Parkplatz entlang des Rands): die schmalsten sind Straßen
        int count = TileExitCount(t.Name);
        foreach (var (_, px, pz) in candidates.OrderBy(k => k.Length).Take(count > 0 ? count : candidates.Count))
            exits.Add((px, pz));
        return local;
    }

    readonly object tileLock = new();
    readonly Dictionary<(string, int, int, int), (int Width, int Total, byte Surface)> exitWidthCache = new();
    readonly Dictionary<string, int[]> exitWidthsByName = new(StringComparer.OrdinalIgnoreCase);

    /// Breiten der Straßenanschlüsse einer Kachelart (größte zuerst), auch wenn sie nicht auf der Karte steht (Wunsch des Nutzers
    /// 3.11.2026: in der Liste und im Gebäudefenster sehen, welche Kacheln zusammenpassen); leer = keine Anschlüsse / nicht lesbar
    public int[] TileExitWidthsOf(string name, PrefabDef def)
    {
        lock (tileLock) if (exitWidthsByName.TryGetValue(name, out var cached)) return cached;
        var probe = new PlacedPrefab { Name = name, Def = def, X = 0, Z = 0, Rotation = 0 };
        var local = TileSurface(probe, out var ex);
        int[] result = local == null ? Array.Empty<int>()
            : ex.Select(e => TileExitWidth(probe, e.X, e.Z).Width).Where(v => v > 0).OrderByDescending(v => v).ToArray();
        lock (tileLock) exitWidthsByName[name] = result;
        return result;
    }

    /// Breite der Kachelstraße an einem Ausgang am Kachelrand (Wunsch des Nutzers 3.11.2026): zusammenhängender Belag gleicher Sorte
    /// quer zur Straße in der Mitte der Seite (Width = nur Asphalt bzw. nur Schotter, Total = Belag insgesamt inkl. Schotterrand).
    /// Width 0 = Punkt liegt nicht am Rand der Kachel oder die Blöcke sind nicht lesbar.
    public (int Width, int Total, byte Surface) TileExitWidth(PlacedPrefab t, double x, double z)
    {
        if (t.Def == null) return default;
        int w = t.SizeX, d = t.SizeZ, ox = (int)Math.Round(x - t.X), oz = (int)Math.Round(z - t.Z);
        bool south = oz == 0, north = oz == d, west = ox == 0, east = ox == w;
        if (!(south || north || west || east)) return default;
        var key = (t.Name, ((t.Rotation % 4) + 4) % 4, ox, oz);
        lock (tileLock) if (exitWidthCache.TryGetValue(key, out var cached)) return cached;
        var probe = new PlacedPrefab { Name = t.Name, Def = t.Def, X = 0, Z = 0, Rotation = t.Rotation };
        var local = TileSurface(probe, out _);
        (int, int, byte) result = default;
        if (local != null)
        {
            bool horizontal = south || north;
            int n = horizontal ? w : d;
            byte At(int i) => south ? local[i] : north ? local[i + (d - 1) * w] : west ? local[i * w] : local[(w - 1) + i * w];
            int c = n / 2;
            int a = At(c) != 0 ? c : c - 1;
            if (a >= 0 && At(a) != 0)
            {
                byte kind = At(a);
                int Run(Func<byte, bool> same)
                {
                    int lo = a, hi = a;
                    while (lo > 0 && same(At(lo - 1))) lo--;
                    while (hi < n - 1 && same(At(hi + 1))) hi++;
                    return hi - lo + 1;
                }
                result = (Run(k => k == kind), Run(k => k != 0), kind);
            }
        }
        lock (tileLock) exitWidthCache[key] = result;
        return result;
    }

    /// Raster der Stadt-Kacheln (150 m): Ursprung aus vorhandenen Kacheln, sonst die Kartenecke
    public const int TileStep = 150;
    public int SnapTile(int v, bool zAxis)
    {
        var any = Prefabs.FirstOrDefault(p => p.IsTile);
        int origin = any == null ? -Half : (zAxis ? any.Z : any.X);
        return origin + (int)Math.Round((v - origin) / (double)TileStep) * TileStep;
    }

    /// Belagsmaske der Stadt-Kacheln anlegen, falls die Welt noch keine hatte (erste Kachel wird eingefügt)
    public byte[] EnsureCityMask() => CityMask ??= new byte[Size * Size];

    // Zahl der Straßenausgänge aus der Form im Namen (cap 1, straight/corner 2, t 3, intersection 4); 0 = unbekannt (Mod-Kacheln)
    static int TileExitCount(string name)
    {
        var parts = name.ToLowerInvariant().Split('_');
        if (parts.Contains("intersection")) return 4;
        if (parts.Contains("t")) return 3;
        if (parts.Contains("straight") || parts.Contains("corner")) return 2;
        if (parts.Contains("cap")) return 1;
        return 0;
    }
    public int BiomeSize { get; private set; }
    /// Straßen je Meter (wie dtm.raw angeordnet): 0 = keine, 1 = Schotter, 2 = Asphalt; aus splat3.png, in Stadt-Kacheln auch aus splat3_processed.png
    public byte[] RoadMask { get; private set; }
    /// Wasser je Meter (wie dtm.raw angeordnet): Blauwert aus splat4.png, 0 = kein Wasser
    public byte[] WaterMask { get; private set; }
    public List<PlacedPrefab> Prefabs { get; } = new();
    public List<SpawnPoint> Spawns { get; } = new();

    public const byte Gravel = 1, Asphalt = 2;

    public int Index(int x, int z) => (z + Half) * Size + x + Half;
    public byte RoadAt(int x, int z) => Contains(x, z) ? RoadMask[Index(x, z)] : (byte)0;
    public bool WaterAt(int x, int z) => Contains(x, z) && WaterMask[Index(x, z)] != 0;

    public bool Contains(double x, double z) => x >= -Half && x < Half && z >= -Half && z < Half;

    public double HeightAt(int x, int z)
    {
        x = Math.Clamp(x, -Half, Half - 1); z = Math.Clamp(z, -Half, Half - 1);
        return Heights[(z + Half) * Size + x + Half] / 256.0;
    }

    /// Bodenhöhe einer Stadt-Kachel nahe (x, z): Das Spiel ebnet den Boden der Kachel ein und schreibt das in
    /// dtm_processed.raw (dtm.raw ist dort noch uneben). Median aus 5 × 5 Punkten innerhalb der Kachel (2 m vom Rand);
    /// ohne dtm_processed.raw ersatzweise aus dtm.raw.
    public double TileHeight(PlacedPrefab tile, double x, double z)
    {
        // Bodenhöhe aus der Lage der Kachel (y − YOffset − 0,85), nicht aus dtm_processed.raw (Fehler 3.11.2026, Wilibii County:
        // die Datei stammt vom letzten Spieltest und war nach dem Setzen/Verschieben der Kachel veraltet → Straße 0,33 m zu tief,
        // im Spiel eine Stufe von einem Block; außerdem lag der Mittelpunkt bei Kacheln mit Hügel oder Gräben daneben, z. B.
        // gateway_straight3 11 m). An den Ausgängen stimmt die Formel mit dem Spiel überein (processed 1–6 m innen: ±0,02 m).
        // (Stand 4.11.2026: der Wert des Spiels ist ganzer Block × 257/256, siehe TerrainHeight – bei hohen Kacheln weicht die Formel
        // mit 0,85 sonst um bis zu 0,24 m ab)
        if (tile.Def != null) return tile.TerrainHeight;
        double cx = Math.Clamp(x, tile.X + 6, tile.X + tile.SizeX - 7), cz = Math.Clamp(z, tile.Z + 6, tile.Z + tile.SizeZ - 7);
        string processed = Path.Combine(Folder, "dtm_processed.raw");
        FileStream fs = null;
        try
        {
            if (File.Exists(processed) && new FileInfo(processed).Length == (long)Size * Size * 2) fs = File.OpenRead(processed);
        }
        catch (IOException) { }
        var values = new List<double>();
        var buf = new byte[2];
        using (fs)
            for (int dz = -4; dz <= 4; dz += 2)
                for (int dx = -4; dx <= 4; dx += 2)
                {
                    int px = (int)Math.Floor(cx) + dx, pz = (int)Math.Floor(cz) + dz;
                    if (fs == null) { values.Add(HeightAt(px, pz)); continue; }
                    fs.Position = (long)Index(px, pz) * 2;
                    fs.ReadExactly(buf);
                    values.Add((buf[0] | buf[1] << 8) / 256.0);
                }
        values.Sort();
        return values[values.Count / 2];
    }

    public int BiomeAt(int x, int z)
    {
        int bx = Math.Clamp((x + Half) * BiomeSize / Size, 0, BiomeSize - 1);
        int by = Math.Clamp((Half - 1 - z) * BiomeSize / Size, 0, BiomeSize - 1);
        return Biomes[by * BiomeSize + bx];
    }

    public static WorldData Load(string folder, string gameFolder, IProgress<string> progress)
    {
        var w = new WorldData { Folder = folder, gameFolder = gameFolder };
        w.Size = ReadMapSize(Path.Combine(folder, "map_info.xml"));

        progress.Report(Loc.F("Reading height map ({0} × {0}) …", w.Size));
        // direkt in das ushort-Feld lesen (ohne Zwischenfeld: spart 128 MB Spitze beim Laden, Prüfbericht F7)
        using (var dtm = File.OpenRead(Path.Combine(folder, "dtm.raw")))
        {
            if (dtm.Length != (long)w.Size * w.Size * 2) throw new InvalidDataException(Loc.F("dtm.raw does not match the map size {0}.", w.Size));
            w.Heights = new ushort[w.Size * w.Size];
            dtm.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(w.Heights.AsSpan()));
        }

        progress.Report(Loc.T("Reading biomes …"));
        var src = PngTools.Open(Path.Combine(folder, "biomes.png"));
        w.BiomeSize = src.PixelWidth;
        var px = new byte[src.PixelWidth * src.PixelHeight * 4];
        src.CopyPixels(px, src.PixelWidth * 4, 0);
        w.Biomes = new byte[src.PixelWidth * src.PixelHeight];
        Parallel.For(0, src.PixelHeight, y =>
        {
            for (int x = 0, q = y * w.BiomeSize, p = q * 4; x < w.BiomeSize; x++, q++, p += 4)
                w.Biomes[q] = (byte)BiomePalette.Nearest(px[p + 2], px[p + 1], px[p]);
        });

        progress.Report(Loc.T("Reading roads (splat3.png) …"));
        w.RoadMask = new byte[w.Size * w.Size];
        static byte RoadKind(byte b, byte g, byte r) => r > 0 && r >= g ? Asphalt : g > 0 ? Gravel : (byte)0;
        PngTools.ReadMask(Path.Combine(folder, "splat3.png"), w.Size, w.RoadMask, RoadKind);
        progress.Report(Loc.T("Reading water (splat4.png) …"));
        w.WaterMask = new byte[w.Size * w.Size];
        PngTools.ReadMask(Path.Combine(folder, "splat4.png"), w.Size, w.WaterMask, (b, g, r) => b);

        progress.Report(Loc.T("Reading buildings and spawn points …"));
        w.ReadPrefabs(Path.Combine(folder, "prefabs.xml"));
        w.LoadSpawns();
        var changed = ChangedSinceTest(w);
        progress.Report(Loc.T("Looking for building descriptions in the game folder …"));
        var catalog = PrefabCatalog.Load(gameFolder, w.Prefabs.Concat(changed).Select(p => p.Name));
        foreach (var p in w.Prefabs.Concat(changed)) p.Def = catalog[p.Name];

        // Stadtstraßen: das Spiel malt die Straßen der Kacheln erst in splat3_processed.png. Nur innerhalb der Kacheln
        // übernehmen – außerhalb kann die Datei veraltet sein (vom letzten Test; entfernte Straßen tauchten sonst wieder auf).
        // Und nur Pixel mit Alpha 0: so schreibt das Spiel die Kachelstraßen; Alpha 255 = Kopie aus splat3.png zur Zeit des
        // Tests, die nach Radieren/Entfernen veraltet sein kann (an der Originalwelt geprüft: ohne Ausnahme so).
        string processed = Path.Combine(folder, "splat3_processed.png");
        var tiles = w.Prefabs.Where(p => p.IsTile).ToList();
        if (File.Exists(processed) || tiles.Count > 0)
        {
            var city = new byte[w.Size * w.Size];
            if (File.Exists(processed))
            {
                progress.Report(Loc.T("Reading city roads (splat3_processed.png) …"));
                PngTools.ReadMask(processed, w.Size, city, (b, g, r, a) => a == 0 ? RoadKind(b, g, r) : (byte)0);
                // Belag (Parkplätze) der seit dem Test geänderten Gebäude ist veraltet
                foreach (var p in changed.Where(p => !p.IsTile && p.Def != null))
                    for (int z = Math.Max(p.Z - 1, -w.Half); z <= Math.Min(p.Z + p.SizeZ, w.Half - 1); z++)
                        for (int x = Math.Max(p.X - 1, -w.Half); x <= Math.Min(p.X + p.SizeX, w.Half - 1); x++)
                            city[w.Index(x, z)] = 0;
            }
            // Straßen der Kacheln aus ihren eigenen Blöcken: auch ohne splat3_processed.png (Welt noch nie im Spiel geladen;
            // Hinweis des Nutzers 30.10.2026). Dabei die Fangpunkte an den Straßenausgängen der Kacheln.
            if (tiles.Count > 0)
            {
                progress.Report(Loc.T("Reading city tiles …"));
                w.AddTileSurfaces(tiles, gameFolder, city);
            }
            w.CityMask = new byte[w.Size * w.Size];
            foreach (var t in w.Prefabs.Where(p => p.IsTile))
                for (int z = Math.Max(t.Z, -w.Half); z < Math.Min(t.Z + t.SizeZ, w.Half); z++)
                    for (int x = Math.Max(t.X, -w.Half); x < Math.Min(t.X + t.SizeX, w.Half); x++)
                    {
                        int i = w.Index(x, z);
                        w.CityMask[i] = city[i];
                        if (city[i] > w.RoadMask[i]) w.RoadMask[i] = city[i];
                    }
        }
        return w;
    }

    // Drei Spawn-Dateien je Projekt (Wunsch des Nutzers 29.10.2026): Spielpunkte, Testpunkt und die gerade benutzte
    // spawnpoints.xml (Kopie einer der beiden; nur sie liest das Spiel und nur sie kommt beim Übernehmen ins Spiel).
    /// gerade benutzte Spawnpunkte (Kopie von spawnpoints_play.xml oder spawnpoints_test.xml)
    public const string SpawnFile = "spawnpoints.xml";
    /// Spawnpunkte zum Spielen (beliebig viele)
    public const string PlaySpawnFile = "spawnpoints_play.xml";
    /// höchstens ein Testpunkt (Datei ohne Eintrag = kein Testpunkt)
    public const string TestSpawnFile = "spawnpoints_test.xml";
    /// Spawnpunkte der Ausgangswelt (beim Anlegen des Projekts kopiert) für „Originale zurückholen“
    public const string OriginalSpawnFile = "spawnpoints_original.xml";
    const string OldTestSpawnFile = "spawnpoint_test.xml";   // kurzzeitig benutzter Name (29.10.2026)

    public IEnumerable<SpawnPoint> PlaySpawns => Spawns.Where(s => !s.Test);
    public SpawnPoint TestSpawn => Spawns.FirstOrDefault(s => s.Test);
    /// spawnpoints.xml beim Speichern aus dem Testpunkt statt aus den Spielpunkten (Auswahl „Ins Spiel übernehmen mit“)
    public bool UseTestSpawn { get; set; }

    // Spielpunkte und Testpunkt lesen; ältere Projekte haben nur spawnpoints.xml (= Spielpunkte): dann die beiden Dateien
    // daraus anlegen, damit es immer alle drei gibt (spawnpoints.xml selbst bleibt unverändert)
    void LoadSpawns()
    {
        string play = Path.Combine(Folder, PlaySpawnFile), test = Path.Combine(Folder, TestSpawnFile), oldTest = Path.Combine(Folder, OldTestSpawnFile);
        Spawns.AddRange(ReadSpawns(File.Exists(play) ? play : Path.Combine(Folder, SpawnFile), false));
        Spawns.AddRange(ReadSpawns(File.Exists(test) ? test : oldTest, true).Take(1));
        try
        {
            if (!File.Exists(play)) WriteSpawnFile(play, PlaySpawns);
            if (!File.Exists(test)) WriteSpawnFile(test, TestSpawn == null ? Array.Empty<SpawnPoint>() : new[] { TestSpawn });
            if (File.Exists(oldTest)) File.Move(oldTest, oldTest + ".old", true);
        }
        catch (IOException) { }   // schreibgeschützt o. Ä.: beim nächsten Speichern
    }

    /// Schreibt Spielpunkte und Testpunkt in ihre Dateien und danach die gerade benutzte spawnpoints.xml
    public void WriteSpawns()
    {
        WriteSpawnFile(Path.Combine(Folder, PlaySpawnFile), PlaySpawns);
        WriteTestSpawn();
    }

    /// nur die Spielpunkte schreiben (Setzen/Löschen von Hand wird sofort gespeichert, Wunsch des Nutzers) und spawnpoints.xml
    public void WritePlaySpawns()
    {
        WriteSpawnFile(Path.Combine(Folder, PlaySpawnFile), PlaySpawns);
        ActivateSpawns();
    }

    /// nur den Testpunkt schreiben und spawnpoints.xml
    public void WriteTestSpawn()
    {
        WriteSpawnFile(Path.Combine(Folder, TestSpawnFile), TestSpawn == null ? Array.Empty<SpawnPoint>() : new[] { TestSpawn });
        ActivateSpawns();
    }

    /// spawnpoints.xml = Kopie der gespeicherten Test- oder Spieldatei (je nach UseTestSpawn; ohne gespeicherten Testpunkt
    /// die Spielpunkte). Liefert true, wenn der Testpunkt benutzt wird.
    public bool ActivateSpawns()
    {
        string test = Path.Combine(Folder, TestSpawnFile), play = Path.Combine(Folder, PlaySpawnFile);
        bool useTest = UseTestSpawn && ReadSpawns(test, true).Count > 0;
        string src = useTest ? test : play;
        if (File.Exists(src)) File.Copy(src, Path.Combine(Folder, SpawnFile), true);
        return useTest;
    }

    public static void WriteSpawnFile(string file, IEnumerable<SpawnPoint> spawns)
    {
        string text = "<spawnpoints>\r\n" + string.Concat(spawns.Select(s => "    <spawnpoint position=\"" +
            string.Format(Inv, "{0:0},{1:0},{2:0}", s.X, s.Y, s.Z) + "\" rotation=\"" + string.Format(Inv, "0,{0:0},0", s.Heading) + "\"/>\r\n")) +
            "</spawnpoints>\r\n";
        File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
    }

    /// prefabs.xml in der Form des Spiels schreiben (Reihenfolge wie in w.Prefabs; UTF-8 mit BOM, CRLF wie die Originale)
    public void WritePrefabs(string file)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<prefabs>\r\n");
        foreach (var p in Prefabs)
            sb.Append("  <decoration type=\"model\" name=\"").Append(System.Security.SecurityElement.Escape(p.Name)).Append("\" position=\"")
              .Append(string.Format(Inv, "{0},{1},{2}", p.X, p.Y, p.Z)).Append("\" rotation=\"").Append(p.Rotation.ToString(Inv)).Append("\" />\r\n");
        sb.Append("</prefabs>");
        File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(true));
    }

    /// Spawnpunkt-Höhe an einer Stelle: Boden aufgerundet (wie beim Spawnpunkt am Straßenanfang)
    public double SpawnHeight(double x, double z) => Math.Ceiling(HeightAt((int)Math.Floor(x), (int)Math.Floor(z)));

    static int ReadMapSize(string mapInfo)
    {
        var doc = XmlSafe.Load(mapInfo);
        var node = doc.SelectSingleNode("/MapInfo/property[@name='HeightMapSize']") as XmlElement;
        if (node == null) throw new InvalidDataException(Loc.T("map_info.xml contains no map size (HeightMapSize)."));
        return int.Parse(node.GetAttribute("value").Split(',')[0].Trim(), Inv);
    }

    void ReadPrefabs(string file) => Prefabs.AddRange(ParsePrefabs(file));

    internal static List<PlacedPrefab> ParsePrefabs(string file)
    {
        var list = new List<PlacedPrefab>();
        var doc = XmlSafe.Load(file);
        foreach (XmlElement e in doc.SelectNodes("/prefabs/decoration"))
        {
            var pos = e.GetAttribute("position").Split(',');
            list.Add(new PlacedPrefab
            {
                Name = e.GetAttribute("name"),
                X = (int)Math.Round(double.Parse(pos[0], Inv)),
                Y = (int)Math.Round(double.Parse(pos[1], Inv)),
                Z = (int)Math.Round(double.Parse(pos[2], Inv)),
                Rotation = e.HasAttribute("rotation") ? int.Parse(e.GetAttribute("rotation"), Inv) : 0,
            });
        }
        return list;
    }

    /// Gebäude, die sich seit dem letzten Spieltest geändert haben (alte und neue Lage), damit ihr Belag aus
    /// splat3_processed.png nicht mehr gezeigt wird (Hinweis des Nutzers 29.10.2026: nach Drehen/Löschen blieb der Parkplatz).
    /// Stand des Tests = prefabs.xml zur Zeit von checksums.txt: die erste Sicherung danach enthält genau diesen Stand
    /// (sie wird vor dem Überschreiben angelegt); gibt es keine, wurde seitdem nicht gespeichert und nichts ist veraltet.
    static List<PlacedPrefab> ChangedSinceTest(WorldData w)
    {
        string sums = Path.Combine(w.Folder, "checksums.txt");
        if (!File.Exists(sums)) return new List<PlacedPrefab>();
        var tested = File.GetLastWriteTime(sums);
        var first = Backups.List(w).Where(b => b.Time > tested && File.Exists(Path.Combine(b.Folder, "prefabs.xml"))).OrderBy(b => b.Time).FirstOrDefault();
        if (first == null) return new List<PlacedPrefab>();
        static string Key(PlacedPrefab p) => string.Format(Inv, "{0}|{1}|{2}|{3}|{4}", p.Name.ToLowerInvariant(), p.X, p.Y, p.Z, p.Rotation);
        var then = ParsePrefabs(Path.Combine(first.Folder, "prefabs.xml"));
        var nowKeys = new HashSet<string>(w.Prefabs.Select(Key));
        var thenKeys = new HashSet<string>(then.Select(Key));
        return then.Where(p => !nowKeys.Contains(Key(p))).Concat(w.Prefabs.Where(p => !thenKeys.Contains(Key(p)))).ToList();
    }

    public static List<SpawnPoint> ReadSpawns(string file, bool test)
    {
        var list = new List<SpawnPoint>();
        if (!File.Exists(file)) return list;
        var doc = XmlSafe.Load(file);
        foreach (XmlElement e in doc.SelectNodes("/spawnpoints/spawnpoint"))
        {
            var pos = e.GetAttribute("position").Split(',');
            var rot = e.GetAttribute("rotation").Split(',');
            list.Add(new SpawnPoint
            {
                X = double.Parse(pos[0], Inv), Y = double.Parse(pos[1], Inv), Z = double.Parse(pos[2], Inv),
                Heading = rot.Length > 1 ? double.Parse(rot[1], Inv) : 0,
                Test = test,
            });
        }
        return list;
    }
}

/// Ein Eintrag aus prefabs.xml. Position = kleinste x/z-Ecke; bei Drehung 1 und 3 sind Breite und Tiefe vertauscht.
public sealed class PlacedPrefab
{
    public string Name;
    public int X, Y, Z, Rotation;
    public PrefabDef Def;
    public int SizeX => Rotation % 2 == 1 ? Def.SizeZ : Def.SizeX;
    public int SizeZ => Rotation % 2 == 1 ? Def.SizeX : Def.SizeZ;
    public bool Contains(double x, double z) => x >= X && x < X + SizeX && z >= Z && z < Z + SizeZ;
    /// Stadt-Kachel (rwgtiles): Straßen und Boden gehören zur Kachel und werden nicht verändert
    public bool IsTile => Def?.Kind == PrefabKind.Tile || Name.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase);

    /// Bodenhöhe des Gebäudes im Spiel (m): Platzierung y − YOffset − 0,85. An der Karte geprüft: dtm_processed innen liegt
    /// bei 1.700 Gebäuden im Median genau y − YOffset − 1 + 0,14…0,19 (survivor_site_06 bei -708,44,-869: 45,18 m)
    public double GroundHeight => Y - (Def?.YOffset ?? 0) - 0.85;

    /// Höhe, die das Spiel in dtm_processed.raw unter der Kachel/dem Gebäude einträgt: ganzer Block N = y − YOffset − 1 mal 257/256
    /// (Wilibii County 4.11.2026: Kachel bei y 90 → 100,39 m statt 100,15 m; in der Originalwelt liegt das Gelände 2 m vor einem
    /// Kachelausgang im Median genau auf diesem Wert). Straßen, die hier anschließen, müssen auf diese Höhe kommen – bei 100 m
    /// Höhe fehlten sonst 0,24 m und die Straße lag vor der Betonkante einen halben Meter tiefer (bei 40 m nur 0,01 m).
    public double TerrainHeight => Math.Round((double)(Y - (Def?.YOffset ?? 0) - 1)) * 257.0 / 256.0;

    /// Vorderseite (Eingang) laut RotationToFaceNorth: 0 = Süden, 1 = Osten, 2 = Norden, 3 = Westen; null = unbekannt
    public int? FrontDirection => Def?.RotationToFaceNorth is int r ? ((Rotation - r + 2) % 4 + 4) % 4 : null;

    /// Ein-/Ausfahrten in Welt-Koordinaten (Drehung wie im Spiel)
    public IEnumerable<(double X, double Z)> Exits()
    {
        if (Def == null) yield break;
        foreach (var (lx, lz) in Def.Exits)
        {
            var (rx, rz) = Rotate(lx, lz, Rotation, Def.SizeX, Def.SizeZ);
            yield return (X + rx, Z + rz);
        }
    }

    // Drehrichtung an der Karte geprüft: so liegen 653 von 669 Einfahrten höchstens 3 m neben einer Straße (andersherum 380)
    public static (double, double) Rotate(double lx, double lz, int rot, int sx, int sz) => (((rot % 4) + 4) % 4) switch
    {
        1 => (sz - lz, lx),
        2 => (sx - lx, sz - lz),
        3 => (lz, sx - lx),
        _ => (lx, lz),
    };
}

public sealed class SpawnPoint
{
    public double X, Y, Z;
    /// Blickrichtung in Grad: 0 = Norden, 90 = Osten
    public double Heading;
    /// Testpunkt (höchstens einer, Wunsch des Nutzers); sonst Spawnpunkt zum Spielen
    public bool Test;

    public SpawnPoint Clone() => (SpawnPoint)MemberwiseClone();
    public bool SameAs(SpawnPoint o) => o != null && Test == o.Test && X == o.X && Y == o.Y && Z == o.Z && Heading == o.Heading;
}

/// Biome in der Reihenfolge der Indizes: Farbe in biomes.png, Anzeigefarbe, Name
public static class BiomePalette
{
    static readonly int[][] Source = { new[] { 0, 64, 0 }, new[] { 186, 0, 255 }, new[] { 255, 228, 119 }, new[] { 255, 255, 255 }, new[] { 255, 168, 0 } };
    public static readonly int[][] Display = { new[] { 150, 190, 125 }, new[] { 165, 150, 170 }, new[] { 238, 216, 160 }, new[] { 238, 243, 247 }, new[] { 222, 170, 120 } };
    public static readonly string[] Names = { "Pine forest", "Burnt forest", "Desert", "Snow", "Wasteland" };

    /// Farbe des Bioms k in biomes.png (das Spiel schreibt genau diese fünf Farben, Alpha 255)
    public static (byte R, byte G, byte B) SourceColor(int k) => ((byte)Source[k][0], (byte)Source[k][1], (byte)Source[k][2]);

    public static int Nearest(int r, int g, int b)
    {
        int best = 0, bestDist = int.MaxValue;
        for (int k = 0; k < Source.Length; k++)
        {
            int dr = r - Source[k][0], dg = g - Source[k][1], db = b - Source[k][2];
            int dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist) { bestDist = dist; best = k; }
        }
        return best;
    }
}
