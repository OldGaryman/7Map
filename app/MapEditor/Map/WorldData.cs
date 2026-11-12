using System.Globalization;
using System.Xml;

namespace MapEditor.Map;

/// A generated world: heights, biomes, buildings and spawn points. World coordinates in meters, x to the east, z to the north,
/// center of the map = (0, 0).
public sealed class WorldData
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Folder { get; private set; }
    public string Name => new DirectoryInfo(Folder).Name;
    public int Size { get; private set; }
    public int Half => Size / 2;
    /// dtm.raw: 16 bit per meter, height = value / 256, row 0 = south
    public ushort[] Heights { get; private set; }
    /// biome index per pixel of biomes.png (row 0 = north)
    public byte[] Biomes { get; private set; }
    /// surface from splat3_processed.png in the city tiles (roads of the tiles and parking lots of their buildings); null = no file
    public byte[] CityMask { get; private set; }
    /// road exits of the city tiles (center, where asphalt/gravel reaches the tile edge): snap points for connecting
    public List<(double X, double Z, PlacedPrefab Tile)> TileExits { get; } = new();

    // surface of the tiles from their blocks (PrefabSurface: topmost terrain block; checked on the original world, 393 tiles:
    // 99.0 % of the calculated area the game has exactly like that, 99.7 % of its tile roads hit). Only where city is still empty.
    // exits (the user's hint 2026-10-30: before also at parking lots/sidewalks at the edge): only where surface reaches the middle of a side
    // – the roads lie there in all tiles of the game (blocks 70–79 of 150) –, at most as many as the shape
    // in the name has, the narrowest first. Result checked on the original world against the neighbors (metest `tilesides`/`tileblocks`):
    // straight S+N, corner S+E, t S+E+N, gateway_t S+E+W, cap S, intersection all (unrotated). Snap point = middle of the side.
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

    /// surface of a building from its blocks, unrotated (read once per name; the background also reads here)
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
    /// false until EdgeExitsWarmUp is finished: until then only buildings already read count (reading all block files takes about 3 s)
    public volatile bool EdgeExitsReady;

    /// connections of a building from its own surface (PrefabSurface.EdgeExits), unrotated; empty = none or blocks not readable;
    /// null = not read yet (only if cachedOnly)
    public List<(double LX, double LZ, int Width, byte Kind)> EdgeExitsOf(string name, PrefabDef def, bool cachedOnly)
    {
        lock (tileLock) if (edgeExitsByName.TryGetValue(name, out var cached)) return cached;
        if (cachedOnly) return null;
        var g = SurfaceGridOf(name);
        var result = g.Grid != null && def != null && g.SX == def.SizeX && g.SZ == def.SizeZ ? PrefabSurface.EdgeExits(g.Grid, g.SX, g.SZ) : new();
        lock (tileLock) edgeExitsByName[name] = result;
        return result;
    }

    /// read all buildings of the map once (in the background after loading; afterwards buildings placed later count immediately)
    public void EdgeExitsWarmUp()
    {
        foreach (var g in Prefabs.Where(p => !p.IsTile && p.Def != null && p.Def.Kind == PrefabKind.Poi).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            try { EdgeExitsOf(g.Key, g.First().Def, false); }
            catch (Exception) { lock (tileLock) edgeExitsByName[g.Key] = new(); }   // unreadable block file: no connections
        }
        EdgeExitsReady = true;
    }

    /// surface of a tile in world orientation (index (x − t.X) + (z − t.Z)·width) and its road exits (middle of the side);
    /// null = blocks of the tile not readable
    public byte[] TileSurface(PlacedPrefab t, out List<(double X, double Z)> exits)
    {
        exits = new List<(double, double)>();
        if (t.Def == null) return null;
        var g = SurfaceGridOf(t.Name);
        if (g.Grid == null || g.SX != t.Def.SizeX || g.SZ != t.Def.SizeZ) return null;
        var local = new byte[t.SizeX * t.SizeZ];   // in world orientation
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
        // candidate per side: surface at the edge that covers the middle of the side; length of this section
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
        Side(w, i => local[i], t.X + w / 2.0, t.Z);                                  // south
        Side(w, i => local[i + (d - 1) * w], t.X + w / 2.0, t.Z + d);                // north
        Side(d, i => local[i * w], t.X, t.Z + d / 2.0);                              // west
        Side(d, i => local[(w - 1) + i * w], t.X + w, t.Z + d / 2.0);                // east
        // more candidates than the shape allows (e.g. sidewalk/parking lot along the edge): the narrowest ones are roads
        int count = TileExitCount(t.Name);
        foreach (var (_, px, pz) in candidates.OrderBy(k => k.Length).Take(count > 0 ? count : candidates.Count))
            exits.Add((px, pz));
        return local;
    }

    readonly object tileLock = new();
    readonly Dictionary<(string, int, int, int), (int Width, int Total, byte Surface)> exitWidthCache = new();
    readonly Dictionary<string, int[]> exitWidthsByName = new(StringComparer.OrdinalIgnoreCase);

    /// widths of the road connections of a tile type (largest first), also if it is not on the map (the user's request
    /// 2026-11-03: see in the list and building window which tiles fit together); empty = no connections / not readable
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

    /// width of the tile road at an exit at the tile edge (the user's request 2026-11-03): connected surface of the same kind
    /// across the road in the middle of the side (Width = asphalt only or gravel only, Total = surface in total including gravel edge).
    /// Width 0 = point does not lie at the edge of the tile or the blocks are not readable.
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

    /// grid of the city tiles (150 m): origin from existing tiles, otherwise the map corner
    public const int TileStep = 150;
    public int SnapTile(int v, bool zAxis)
    {
        var any = Prefabs.FirstOrDefault(p => p.IsTile);
        int origin = any == null ? -Half : (zAxis ? any.Z : any.X);
        return origin + (int)Math.Round((v - origin) / (double)TileStep) * TileStep;
    }

    /// create the surface mask of the city tiles if the world did not have one yet (first tile is inserted)
    public byte[] EnsureCityMask() => CityMask ??= new byte[Size * Size];

    // number of road exits from the shape in the name (cap 1, straight/corner 2, t 3, intersection 4); 0 = unknown (mod tiles)
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
    /// roads per meter (arranged like dtm.raw): 0 = none, 1 = gravel, 2 = asphalt; from splat3.png, in city tiles also from splat3_processed.png
    public byte[] RoadMask { get; private set; }
    /// water per meter (arranged like dtm.raw): blue value from splat4.png, 0 = no water
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

    /// ground height of a city tile near (x, z): the game levels the ground of the tile and writes it into
    /// dtm_processed.raw (dtm.raw is still uneven there). Median of 5 × 5 points inside the tile (2 m from the edge);
    /// without dtm_processed.raw as a substitute from dtm.raw.
    public double TileHeight(PlacedPrefab tile, double x, double z)
    {
        // ground height from the position of the tile (y − YOffset − 0.85), not from dtm_processed.raw (bug 2026-11-03, Wilibii County:
        // the file comes from the last game test and was outdated after placing/moving the tile → road 0.33 m too low,
        // in the game a step of one block; also the median was wrong for tiles with a hill or ditches beside them, e.g.
        // gateway_straight3 11 m). At the exits the formula agrees with the game (processed 1–6 m inside: ±0.02 m).
        // (as of 2026-11-04: the value of the game is whole block × 257/256, see TerrainHeight – for high tiles the formula
        // with 0.85 otherwise deviates by up to 0.24 m)
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
        // read directly into the ushort field (without an intermediate field: saves a 128 MB peak when loading, audit finding F7)
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

        // city roads: the game paints the roads of the tiles only in splat3_processed.png. Take over only inside the tiles
        // – outside the file may be outdated (from the last test; removed roads would otherwise reappear).
        // And only pixels with alpha 0: that is how the game writes the tile roads; alpha 255 = copy from splat3.png at the time of the
        // test, which can be outdated after erasing/removing (checked on the original world: without exception like that).
        string processed = Path.Combine(folder, "splat3_processed.png");
        var tiles = w.Prefabs.Where(p => p.IsTile).ToList();
        if (File.Exists(processed) || tiles.Count > 0)
        {
            var city = new byte[w.Size * w.Size];
            if (File.Exists(processed))
            {
                progress.Report(Loc.T("Reading city roads (splat3_processed.png) …"));
                PngTools.ReadMask(processed, w.Size, city, (b, g, r, a) => a == 0 ? RoadKind(b, g, r) : (byte)0);
                // surface (parking lots) of buildings changed since the test is outdated
                foreach (var p in changed.Where(p => !p.IsTile && p.Def != null))
                    for (int z = Math.Max(p.Z - 1, -w.Half); z <= Math.Min(p.Z + p.SizeZ, w.Half - 1); z++)
                        for (int x = Math.Max(p.X - 1, -w.Half); x <= Math.Min(p.X + p.SizeX, w.Half - 1); x++)
                            city[w.Index(x, z)] = 0;
            }
            // roads of the tiles from their own blocks: also without splat3_processed.png (world never loaded in the game;
            // the user's hint 2026-10-30). Including the snap points at the road exits of the tiles.
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

    // three spawn files per project (the user's request 2026-10-29): play points, test point and the one currently used
    // spawnpoints.xml (copy of one of the two; only it is read by the game and only it goes into the game when copying).
    /// spawn points currently used (copy of spawnpoints_play.xml or spawnpoints_test.xml)
    public const string SpawnFile = "spawnpoints.xml";
    /// spawn points for playing (any number)
    public const string PlaySpawnFile = "spawnpoints_play.xml";
    /// at most one test point (file without an entry = no test point)
    public const string TestSpawnFile = "spawnpoints_test.xml";
    /// spawn points of the starting world (copied when the project is created) for "Restore originals"
    public const string OriginalSpawnFile = "spawnpoints_original.xml";
    const string OldTestSpawnFile = "spawnpoint_test.xml";   // name used briefly (2026-10-29)

    public IEnumerable<SpawnPoint> PlaySpawns => Spawns.Where(s => !s.Test);
    public SpawnPoint TestSpawn => Spawns.FirstOrDefault(s => s.Test);
    /// spawnpoints.xml when saving from the test point instead of the play points (choice "Copy into the game with")
    public bool UseTestSpawn { get; set; }

    // read play points and test point; older projects have only spawnpoints.xml (= play points): then create the two files
    // from it so that all three always exist (spawnpoints.xml itself stays unchanged)
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
        catch (IOException) { }   // read-only or similar: at the next save
    }

    /// writes play points and test point into their files and afterwards the spawnpoints.xml currently used
    public void WriteSpawns()
    {
        WriteSpawnFile(Path.Combine(Folder, PlaySpawnFile), PlaySpawns);
        WriteTestSpawn();
    }

    /// write only the play points (setting/deleting by hand is saved immediately, the user's request) and spawnpoints.xml
    public void WritePlaySpawns()
    {
        WriteSpawnFile(Path.Combine(Folder, PlaySpawnFile), PlaySpawns);
        ActivateSpawns();
    }

    /// write only the test point and spawnpoints.xml
    public void WriteTestSpawn()
    {
        WriteSpawnFile(Path.Combine(Folder, TestSpawnFile), TestSpawn == null ? Array.Empty<SpawnPoint>() : new[] { TestSpawn });
        ActivateSpawns();
    }

    /// spawnpoints.xml = copy of the saved test or play file (depending on UseTestSpawn; without a saved test point
    /// the play points). Returns true if the test point is used.
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

    /// write prefabs.xml in the form of the game (order like in w.Prefabs; UTF-8 with BOM, CRLF like the originals)
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

    /// spawn point height at a place: ground rounded up (like with the spawn point at the start of the road)
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

    /// buildings that changed since the last game test (old and new position), so that their surface from
    /// splat3_processed.png is no longer shown (the user's hint 2026-10-29: after rotating/deleting the parking lot remained).
    /// state of the test = prefabs.xml at the time of checksums.txt: the first backup afterwards contains exactly this state
    /// (it is created before overwriting); if there is none, nothing was saved since and nothing is outdated.
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

/// an entry from prefabs.xml. Position = smallest x/z corner; with rotation 1 and 3 width and depth are swapped.
public sealed class PlacedPrefab
{
    public string Name;
    public int X, Y, Z, Rotation;
    public PrefabDef Def;
    public int SizeX => Rotation % 2 == 1 ? Def.SizeZ : Def.SizeX;
    public int SizeZ => Rotation % 2 == 1 ? Def.SizeX : Def.SizeZ;
    public bool Contains(double x, double z) => x >= X && x < X + SizeX && z >= Z && z < Z + SizeZ;
    /// city tile (rwgtiles): roads and ground belong to the tile and are not changed
    public bool IsTile => Def?.Kind == PrefabKind.Tile || Name.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase);

    /// ground height of the building in the game (m): placement y − YOffset − 0.85. Checked on the map: dtm_processed inside lies
    /// at 1,700 buildings in the median exactly y − YOffset − 1 + 0.14…0.19 (survivor_site_06 at -708,44,-869: 45.18 m)
    public double GroundHeight => Y - (Def?.YOffset ?? 0) - 0.85;

    /// height that the game writes into dtm_processed.raw under the tile/building: whole block N = y − YOffset − 1 times 257/256
    /// (Wilibii County 2026-11-04: tile at y 90 → 100.39 m instead of 100.15 m; in the original world the terrain 2 m in front of a
    /// tile exit lies in the median exactly at this value). Roads that connect here must reach this height – at 100 m
    /// height 0.24 m was otherwise missing and the road lay half a meter lower in front of the concrete edge (at 40 m only 0.01 m).
    public double TerrainHeight => Math.Round((double)(Y - (Def?.YOffset ?? 0) - 1)) * 257.0 / 256.0;

    /// front (entrance) according to RotationToFaceNorth: 0 = south, 1 = east, 2 = north, 3 = west; null = unknown
    public int? FrontDirection => Def?.RotationToFaceNorth is int r ? ((Rotation - r + 2) % 4 + 4) % 4 : null;

    /// entrances/exits in world coordinates (rotation like in the game)
    public IEnumerable<(double X, double Z)> Exits()
    {
        if (Def == null) yield break;
        foreach (var (lx, lz) in Def.Exits)
        {
            var (rx, rz) = Rotate(lx, lz, Rotation, Def.SizeX, Def.SizeZ);
            yield return (X + rx, Z + rz);
        }
    }

    // rotation direction checked on the map: this way 653 of 669 entrances lie at most 3 m beside a road (the other way 380)
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
    /// view direction in degrees: 0 = north, 90 = east
    public double Heading;
    /// test point (at most one, the user's request); otherwise spawn point for playing
    public bool Test;

    public SpawnPoint Clone() => (SpawnPoint)MemberwiseClone();
    public bool SameAs(SpawnPoint o) => o != null && Test == o.Test && X == o.X && Y == o.Y && Z == o.Z && Heading == o.Heading;
}

/// biomes in the order of the indices: color in biomes.png, display color, name
public static class BiomePalette
{
    static readonly int[][] Source = { new[] { 0, 64, 0 }, new[] { 186, 0, 255 }, new[] { 255, 228, 119 }, new[] { 255, 255, 255 }, new[] { 255, 168, 0 } };
    public static readonly int[][] Display = { new[] { 150, 190, 125 }, new[] { 165, 150, 170 }, new[] { 238, 216, 160 }, new[] { 238, 243, 247 }, new[] { 222, 170, 120 } };
    public static readonly string[] Names = { "Pine forest", "Burnt forest", "Desert", "Snow", "Wasteland" };

    /// color of biome k in biomes.png (the game writes exactly these five colors, alpha 255)
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
