using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Xml;

namespace MapEditor.Map;

/// settings for "Create wilderness POIs" (the user's request 2026-11-09, step 4 after terrain, cities and roads)
public sealed class WildernessSpec
{
    /// POIs per km² (decision of the user: like the original East Pozesogu, 8 km: 376–480 wilderness POIs ≈ 6–7 per km²)
    public double Density = 7.0;
    public double Grade = 0.16, MaxGrade = 0.22;   // gravel paths: usual/highest gradient (like the roads)
    /// gravel path 3–5 m depending on the building (the user's request 2026-11-10; before fixed 5 m): small buildings narrow, large wide,
    /// never wider than their entrance
    public double PathWidthMin = 3, PathWidth = 5;
    /// verge: per side this much ground of the biome horizontal beside the gravel (the user's request 2026-11-10: at sloping
    /// places the gravel ended directly at the slope edge)
    public double Verge = 1;
    public double MaxPath = 1500;                  // longest path (original: at most about 1.5 km to the nearest asphalt road)
    public bool BreakInWasteland = true;           // wasteland only: interrupt gravel paths at bomb craters & earthquake cracks
    public string Seed = "YAMEE";
    public WildernessSpec Clone() => (WildernessSpec)MemberwiseClone();
}

/// planned gravel path from the entrance of a wilderness POI to the nearest road or to an already planned path
public sealed class WildPath
{
    public List<Point> Points;         // entrance → target (for 2nd-pass connectors: StartTarget → Target/road)
    public WildPoi Poi;                // null for 2nd-pass connecting paths
    public WildPath StartTarget;       // non-null for 2nd-pass connectors: starts on this planned path
    public byte StartKind;             // surface of an existing road at the start (0 = POI entrance or planned path)
    public Vector StartDir;            // direction of the existing road at the start
    public double StartHalf;           // half width of the existing road at the start (including edge)
    public WildPath Target;            // ends at this planned path (null = at an existing road)
    public byte EndKind;               // surface of the existing road at the target (0 = planned path)
    public Vector EndDir;              // direction of the existing road at the target
    public double EndHalf;             // half width of the existing road at the target (including edge)
    public double Length;
    public double Width = 5;           // width of the gravel (3–5 m depending on the building)
    public bool IsVolcano;             // volcano spiral / mid-height branch: steeper grade (12–20 %) and +1 m downhill verge
    public bool IsVolcanoSpiral;       // main spiral down the volcano (receives fixed 12–20 % heights and 2 landslides)
    public bool IsConnector;           // 2nd-pass through-link / mid-link connecting a wilderness path to a nearby road or other path
    public List<double?> FixedHeights; // optional fixed heights parallel to Points (null = free terrain)
    public List<BridgeSite> Bridges = new();   // bridges over rivers on the way (in driving order)
}

public sealed class WildPoi
{
    public PlacedPrefab Building;
    public List<PlacedPrefab> Parts = new();
    public int Biome;
    public bool HasExit;
    public WildPath Path;
}

public sealed class WildernessPlan
{
    public List<WildPoi> Pois = new();
    public List<WildPath> Paths = new();
    public int[] PerBiome = new int[5], TargetPerBiome = new int[5], Existing = new int[5];
    public int Candidates;
    public List<string> Problems = new();
    public double Seconds;
}

public sealed class WildernessBuildResult
{
    public int Pois, Paths, HeightPoints, Bridges;
    public double Length, MaxGrade, MaxCut, MaxFill;
    public int SteepPaths;
    public List<(WildPath Path, RoadResult Road)> Built = new();
    public List<VolcanoLandslide> VolcanoLandslides = new();
    public List<string> Warnings = new();
}

/// rules of the game for wilderness POIs (Data\Config\rwgmixer.xml, prefab_spawn_adjust): biomes (biomeTags), bias, weight –
/// apply to all buildings whose name contains partial_name
public sealed class WildernessRules
{
    public sealed record Adjust(string Partial, int[] Biomes, double Bias, double Weight);
    public List<Adjust> Adjusts { get; } = new();
    static readonly string[] BiomeTags = { "forest", "burntforest", "desert", "snow", "wasteland" };

    public static WildernessRules Load(string gameFolder)
    {
        var r = new WildernessRules();
        string file = gameFolder == null ? null : Path.Combine(gameFolder, "Data", "Config", "rwgmixer.xml");
        if (file == null || !File.Exists(file)) return r;
        try
        {
            var doc = XmlSafe.Load(file);
            foreach (XmlElement a in doc.SelectNodes("//prefab_spawn_adjust"))
            {
                string partial = a.GetAttribute("partial_name").Trim();
                if (partial.Length == 0) continue;
                int[] biomes = null;
                string bt = a.GetAttribute("biomeTags");
                if (!string.IsNullOrWhiteSpace(bt))
                    biomes = bt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => Array.FindIndex(BiomeTags, b => b.Equals(t, StringComparison.OrdinalIgnoreCase))).Where(i => i >= 0).ToArray();
                double bias = Num(a.GetAttribute("bias"), 0), weight = Num(a.GetAttribute("weight"), 1);
                r.Adjusts.Add(new Adjust(partial, biomes, bias, weight));
            }
        }
        catch (Exception) { }
        return r;
    }

    static double Num(string s, double def) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;

    /// allowed biomes (null = all), bonus and weight for a building
    public (int[] Biomes, double Bias, double Weight) For(string name)
    {
        int[] biomes = null;
        double bias = 0, weight = 1;
        foreach (var a in Adjusts)
        {
            if (!name.Contains(a.Partial, StringComparison.OrdinalIgnoreCase)) continue;
            if (a.Biomes != null) biomes = a.Biomes;
            bias += a.Bias;
            weight *= a.Weight;
        }
        return (biomes, bias, weight);
    }
}

/// place wilderness POIs and connect them with gravel paths (the user's request 2026-11-09; planning changes nothing).
/// Procedure: target per biome = density × land area of the biome. Per biome in turn one building (weight like in the game: 1 + bias + tier/5,
/// divided by 1 + copies already placed), for it up to 40 random places in the biome: completely in the biome, ≥ 120 m from the map edge,
/// ≥ 100 m from city tiles, ≥ 24 m from other buildings, roads and planned paths, center ≥ 100 m from other
/// wilderness POIs, same building ≥ DuplicateRepeatDistance, no water, earthwork limited. If the building has an entrance,
/// it should face the nearest road, and a path (A* on an 8 m grid, 16 driving directions, costs like RoadRouter) must be possible
/// up to a road or an already planned path (at most MaxPath), otherwise the next place
/// (decision of the user). Buildings without an entrance (caves, camps …) stand without a path.
public sealed partial class WildernessGenerator
{
    const int C = 8;                      // grid (m)
    const double EdgeKeep = 120, TileGap = 100, BuildingGap = 24, PoiSpacing = 100, Lead = 12;
    /// minimum horizontal clearance (m) from a city tile edge to a wilderness path center line:
    /// pathEdge (width/2 + verge) + TileClearanceBase + TileClearancePerMeter * |pathH - tileH|, capped at MaxTileClearance
    /// (< TileGap so POIs placed at TileGap stay reachable). Ensures enough horizontal room to smoothly raise/blend the terrain.
    internal const double TileClearanceBase = 14.5;
    internal const double TileClearancePerMeter = 3.0;
    internal const double MaxTileClearance = 96.0;
    const double TileAvoidBand = 24.0;    // soft cost penalty band outside the hard tile clearance

    readonly record struct CityTileInfo(int X0, int Z0, int X1, int Z1, double Height);

    readonly WorldData w;
    readonly WildernessSpec spec;
    readonly WildernessRules rules;
    readonly Dictionary<string, PrefabDef> candidates;
    readonly Func<string, PrefabDef> partDef;
    readonly Random rnd;
    readonly int gw, half;
    float[] h, steep;
    bool[] water, tile, target;
    byte[] block;                 // bit 1: building/tile with margin (blocked for paths), bit 2: footprint of the building currently being checked
    bool[] building;              // footprints (without margin) of all buildings except parts
    int[] pathOf;                 // planned path in the cell (number + 1), 0 = none
    float[] cellDir;              // direction of the road/path in a target cell (radians; NaN = not determined yet, -9 = unknown)
    float[] dist, tileDist;       // distance (cells) to the nearest target (road/path) or to the nearest tile
    float[] tileMargin;           // remaining horizontal room (m) beyond hard TileClearance to nearest city tile
    readonly List<CityTileInfo> cityTiles = new();
    readonly RoadScan scan;
    readonly WildernessPlan plan = new();
    readonly Dictionary<long, List<(double X, double Z)>> poiHash = new();
    readonly Dictionary<string, List<(double X, double Z)>> sameName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> placedCount = new(StringComparer.OrdinalIgnoreCase);

    static readonly (int DI, int DJ)[] Steps =
    {
        (1, 0), (2, 1), (1, 1), (1, 2), (0, 1), (-1, 2), (-1, 1), (-2, 1),
        (-1, 0), (-2, -1), (-1, -1), (-1, -2), (0, -1), (1, -2), (1, -1), (2, -1),
    };

    public static Action<string> Log;   // test output

    /// candidates = buildings with the tag "wilderness" (name → description); partDef = parts (driveways) for PrefabParts.For
    public WildernessGenerator(WorldData world, WildernessSpec s, WildernessRules r, Dictionary<string, PrefabDef> candidates, Func<string, PrefabDef> partDef)
    {
        w = world; spec = s; rules = r; this.candidates = candidates; this.partDef = partDef;
        rnd = new Random(new GeneratorSpec { Seed = s.Seed + "/wilderness" }.SeedValue);
        half = w.Half; gw = w.Size / C;
        scan = new RoadScan(w);
    }

    /// is this a wilderness POI (outside the tiles, tag "wilderness")?
    public static bool IsWild(WorldData w, PlacedPrefab p, Func<string, PrefabDef> defOf)
    {
        if (p.Def == null || p.IsTile || p.Def.Kind == PrefabKind.Part) return false;
        var d = defOf(p.Name) ?? p.Def;
        if (!d.Tags.Contains("wilderness")) return false;
        double cx = p.X + p.SizeX / 2.0, cz = p.Z + p.SizeZ / 2.0;
        return !w.Prefabs.Any(t => t.IsTile && t.Contains(cx, cz));
    }

    int CellOf(double x, double z)
    {
        int cx = (int)Math.Floor((x + half) / C), cz = (int)Math.Floor((z + half) / C);
        return cx < 0 || cz < 0 || cx >= gw || cz >= gw ? -1 : cz * gw + cx;
    }
    Point Center(int c) => new(-half + (c % gw) * C + C / 2.0, -half + (c / gw) * C + C / 2.0);

    public WildernessPlan Plan(IProgress<string> progress)
    {
        var sw = Stopwatch.StartNew();
        progress.Report(Loc.T("Reading the map …"), 0.0);
        BuildGrid(progress.Slice(0.0, 0.08));
        var pool = candidates.Where(kv => kv.Value.Tags.Contains("wilderness") && !kv.Key.StartsWith("trader_", StringComparison.OrdinalIgnoreCase)
                && kv.Value.SizeX <= 160 && kv.Value.SizeZ <= 160)
            .Select(kv => (Name: kv.Key, Def: kv.Value, Rule: rules.For(kv.Key))).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        plan.Candidates = pool.Count;
        if (pool.Count == 0) { plan.Problems.Add(Loc.T("No buildings with the tag \"wilderness\" were found in the game folder.")); return plan; }

        // cells per biome (land outside of tiles and edge), from that the target per biome
        var cells = Enumerable.Range(0, 5).Select(_ => new List<int>()).ToArray();
        int edgeCells = (int)Math.Ceiling(EdgeKeep / C);
        for (int cz = edgeCells; cz < gw - edgeCells; cz++)
            for (int cx = edgeCells; cx < gw - edgeCells; cx++)
            {
                int g = cz * gw + cx;
                if (water[g] || tile[g]) continue;
                var p = Center(g);
                int b = w.BiomeAt((int)p.X, (int)p.Y);
                if (b >= 0 && b < 5) cells[b].Add(g);
            }
        double kmPerCell = C * C / 1e6;
        for (int b = 0; b < 5; b++)
            plan.TargetPerBiome[b] = Math.Max(0, (int)Math.Round(spec.Density * cells[b].Count * kmPerCell) - plan.Existing[b]);
        PlaceVolcanoPois(pool);
        int total = plan.TargetPerBiome.Sum();

        const double poiStart = 0.08, poiEnd = 0.50;
        var failed = new HashSet<(int, string)>();       // building that finds no place in this biome
        var misses = new int[5];
        var done = new bool[5];
        for (int b = 0; b < 5; b++) done[b] = plan.TargetPerBiome[b] == 0 || cells[b].Count == 0;
        void ReportPois()
        {
            int eff = 0;
            for (int i = 0; i < 5; i++)
                eff += done[i] ? plan.TargetPerBiome[i] : Math.Min(plan.PerBiome[i], plan.TargetPerBiome[i]);
            double f = total > 0 ? Math.Clamp((double)eff / total, 0, 1) : 1;
            progress.Report(Loc.F("{0} of about {1} wilderness POIs placed …", plan.Pois.Count, total), poiStart + (poiEnd - poiStart) * f);
        }
        int reportStep = Math.Max(1, Math.Min(5, total / 40));
        if (total > 0) ReportPois();
        while (!done.All(d => d))
        {
            for (int b = 0; b < 5; b++)
            {
                if (done[b]) continue;
                if (plan.PerBiome[b] >= plan.TargetPerBiome[b]) { done[b] = true; ReportPois(); continue; }
                var allowed = pool.Where(c => (c.Rule.Biomes == null || c.Rule.Biomes.Contains(b)) && !failed.Contains((b, c.Name))).ToList();
                if (allowed.Count == 0) { done[b] = true; ReportPois(); continue; }
                var pick = Weighted(allowed);
                var poi = TryPlace(pick.Name, pick.Def, b, cells[b]);
                if (poi == null)
                {
                    failed.Add((b, pick.Name));
                    if (++misses[b] > 80) { done[b] = true; ReportPois(); }   // many failed attempts in a row: biome is full
                    else if (misses[b] % 10 == 0) WorkCancel.ThrowIfRequested();
                    continue;
                }
                misses[b] = 0;
                plan.PerBiome[b]++;
                if (plan.Pois.Count % reportStep == 0) ReportPois();
            }
        }
        for (int b = 0; b < 5; b++)
            if (plan.PerBiome[b] < plan.TargetPerBiome[b] * 0.9)
                plan.Problems.Add(Loc.F("{0}: only {1} of {2} POIs found a place (steep terrain, water or no possible path).",
                    Loc.T(BiomePalette.Names[b]), plan.PerBiome[b], plan.TargetPerBiome[b]));
        EnsureVolcanoConnected();
        ConnectNearbyRoads(progress.Slice(poiEnd, 0.94));
        EnsureWildernessBridge(progress.Slice(0.94, 0.99));
        plan.Seconds = sw.Elapsed.TotalSeconds;
        return plan;
    }

    (string Name, PrefabDef Def, (int[] Biomes, double Bias, double Weight) Rule) Weighted(List<(string Name, PrefabDef Def, (int[] Biomes, double Bias, double Weight) Rule)> list)
    {
        double Score((string Name, PrefabDef Def, (int[] Biomes, double Bias, double Weight) Rule) c) =>
            Math.Max(0.01, (1 + c.Rule.Bias + c.Def.Tier / 5.0) / (1 + placedCount.GetValueOrDefault(c.Name)) * c.Rule.Weight);
        double sum = list.Sum(Score), v = rnd.NextDouble() * sum;
        foreach (var c in list) { v -= Score(c); if (v <= 0) return c; }
        return list[^1];
    }

    double PathEdge => spec.PathWidth / 2.0 + spec.Verge;

    /// Required horizontal clearance (m) from a city tile edge to the center line of a wilderness path,
    /// depending on the height difference between path and tile (m) and the path's half-width + verge (m).
    /// Higher height difference -> wider clearance so the terrain has room to ramp smoothly without steep walls.
    internal static double TileClearance(double heightDiff, double pathEdge = 3.5) =>
        Math.Min(MaxTileClearance, pathEdge + TileClearanceBase + TileClearancePerMeter * Math.Abs(heightDiff));

    static double RectDist(double x, double z, in CityTileInfo t)
    {
        double dx = Math.Max(0, Math.Max(t.X0 - x, x - t.X1));
        double dz = Math.Max(0, Math.Max(t.Z0 - z, z - t.Z1));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    bool TooCloseToTile(double x, double z, double pathH, double pathEdge)
    {
        for (int i = 0; i < cityTiles.Count; i++)
        {
            var t = cityTiles[i];
            if (x < t.X0 - MaxTileClearance || x > t.X1 + MaxTileClearance ||
                z < t.Z0 - MaxTileClearance || z > t.Z1 + MaxTileClearance) continue;
            if (RectDist(x, z, t) < TileClearance(pathH - t.Height, pathEdge)) return true;
        }
        return false;
    }

    // ---------- Grid ----------

    void BuildGrid(IProgress<string> progress = null)
    {
        int n = gw * gw;
        h = new float[n]; steep = new float[n];
        water = new bool[n]; tile = new bool[n]; target = new bool[n]; building = new bool[n];
        block = new byte[n]; pathOf = new int[n];
        cellDir = new float[n];
        Array.Fill(cellDir, float.NaN);
        cityTiles.Clear();
        bool usePre = spec.BreakInWasteland && w.GetWastelandDamage() != null;
        Parallel.For(0, gw, cz =>
        {
            for (int cx = 0; cx < gw; cx++)
            {
                int g = cz * gw + cx;
                int x = -half + cx * C, z = -half + cz * C;
                h[g] = usePre
                    ? (float)((w.PreDamageHeightAt(x + 2, z + 2, true) + w.PreDamageHeightAt(x + 6, z + 2, true) + w.PreDamageHeightAt(x + 2, z + 6, true) + w.PreDamageHeightAt(x + 6, z + 6, true)) / 4)
                    : (float)((w.HeightAt(x + 2, z + 2) + w.HeightAt(x + 6, z + 2) + w.HeightAt(x + 2, z + 6) + w.HeightAt(x + 6, z + 6)) / 4);
                water[g] = w.WaterAt(x + 1, z + 1) || w.WaterAt(x + 6, z + 1) || w.WaterAt(x + 1, z + 6) || w.WaterAt(x + 6, z + 6) || w.WaterAt(x + 4, z + 4);
                bool road = false;
                for (int dz = 0; dz < C && !road; dz++)
                    for (int dx = 0; dx < C; dx++)
                        if (w.RoadMask[w.Index(x + dx, z + dz)] != 0) { road = true; break; }
                target[g] = road;
            }
        });
        Parallel.For(1, gw - 1, cz =>
        {
            for (int cx = 1; cx < gw - 1; cx++)
            {
                int g = cz * gw + cx;
                steep[g] = Math.Max(Math.Abs(h[g + 1] - h[g - 1]), Math.Abs(h[g + gw] - h[g - gw])) / (2f * C);
            }
        });
        foreach (var p in w.Prefabs)
        {
            if (p.Def == null || p.Def.Kind == PrefabKind.Part) continue;
            if (p.IsTile)
            {
                MarkRect(p.X, p.Z, p.SizeX, p.SizeZ, 0, (g) => { tile[g] = true; target[g] = false; block[g] |= 1; });
                double th = p.Def != null ? p.TerrainHeight : w.TileHeight(p, p.X + p.SizeX / 2.0, p.Z + p.SizeZ / 2.0);
                cityTiles.Add(new CityTileInfo(p.X, p.Z, p.X + p.SizeX, p.Z + p.SizeZ, th));
                continue;
            }
            AddBuilding(p);
            double cx = p.X + p.SizeX / 2.0, cz = p.Z + p.SizeZ / 2.0;
            if (p.Def.Tags.Contains("wilderness") || candidates.GetValueOrDefault(p.Name)?.Tags.Contains("wilderness") == true)
            {
                AddToHash(cx, cz, p.Name);
                if (!w.Prefabs.Any(t => t.IsTile && t.Contains(cx, cz)))
                {
                    int b = w.BiomeAt((int)cx, (int)cz);
                    if (b >= 0 && b < 5) plan.Existing[b]++;
                }
            }
        }
        // height-dependent clearance around city tiles: higher ground (larger |h - tileH|) needs more horizontal room
        // between tile edge and path edge so the terrain can rise/fall smoothly without a steep ledge at the tile border
        tileMargin = new float[n];
        Array.Fill(tileMargin, float.MaxValue);
        double pathEdge = PathEdge;
        double scanReach = MaxTileClearance + TileAvoidBand + C;
        foreach (var t in cityTiles)
        {
            int c0 = Math.Max(0, (int)Math.Floor((t.X0 - scanReach + half) / C));
            int c1 = Math.Min(gw - 1, (int)Math.Floor((t.X1 + scanReach + half) / C));
            int r0 = Math.Max(0, (int)Math.Floor((t.Z0 - scanReach + half) / C));
            int r1 = Math.Min(gw - 1, (int)Math.Floor((t.Z1 + scanReach + half) / C));
            for (int cz = r0; cz <= r1; cz++)
                for (int cx = c0; cx <= c1; cx++)
                {
                    int g = cz * gw + cx;
                    var p = Center(g);
                    double d = RectDist(p.X, p.Y, t);
                    if (d > MaxTileClearance + TileAvoidBand) continue;
                    int x = -half + cx * C, z = -half + cz * C;
                    double dh = Math.Abs(h[g] - t.Height);
                    if (d < MaxTileClearance)
                    {
                        double h1 = usePre ? w.PreDamageHeightAt(x + 1, z + 1, true) : w.HeightAt(x + 1, z + 1);
                        double h2 = usePre ? w.PreDamageHeightAt(x + 7, z + 1, true) : w.HeightAt(x + 7, z + 1);
                        double h3 = usePre ? w.PreDamageHeightAt(x + 1, z + 7, true) : w.HeightAt(x + 1, z + 7);
                        double h4 = usePre ? w.PreDamageHeightAt(x + 7, z + 7, true) : w.HeightAt(x + 7, z + 7);
                        dh = Math.Max(dh, Math.Max(
                            Math.Max(Math.Abs(h1 - t.Height), Math.Abs(h2 - t.Height)),
                            Math.Max(Math.Abs(h3 - t.Height), Math.Abs(h4 - t.Height))));
                    }
                    double req = TileClearance(dh, pathEdge);
                    if (d < req)
                    {
                        block[g] |= 1;
                        target[g] = false;
                        tileMargin[g] = 0;
                    }
                    else
                    {
                        float m = (float)(d - req);
                        if (m < tileMargin[g]) tileMargin[g] = m;
                    }
                }
        }
        // edge (60 m) blocked for paths
        int e = 60 / C;
        for (int cz = 0; cz < gw; cz++)
            for (int cx = 0; cx < gw; cx++)
                if (cx < e || cz < e || cx >= gw - e || cz >= gw - e) block[cz * gw + cx] |= 1;
        progress.Report(Loc.T("Reading the map …"), 0.40);
        tileDist = GroundShaper.Distance(tile, gw, gw);
        dist = GroundShaper.Distance(target, gw, gw);
        if (RoadNetwork.Bridges != null) progress.Report(Loc.T("Looking for bridge places …"), 0.65);
        FindBridges();
        progress.Report(Loc.T("Reading the map …"), 1.0);
    }

    void AddBuilding(PlacedPrefab p)
    {
        MarkRect(p.X, p.Z, p.SizeX, p.SizeZ, 0, g => building[g] = true);
        MarkRect(p.X, p.Z, p.SizeX, p.SizeZ, 6, g => block[g] |= 1);
    }

    void AddToHash(double x, double z, string name)
    {
        long k = Key(x, z);
        if (!poiHash.TryGetValue(k, out var l)) poiHash[k] = l = new();
        l.Add((x, z));
        if (!sameName.TryGetValue(name, out var s)) sameName[name] = s = new();
        s.Add((x, z));
    }

    static long Key(double x, double z) => ((long)Math.Floor(x / 128) << 32) ^ (uint)(int)Math.Floor(z / 128);

    void MarkRect(double x, double z, double sx, double sz, double margin, Action<int> mark)
    {
        int c0 = (int)Math.Floor((x - margin + half) / C), c1 = (int)Math.Floor((x + sx + margin - 0.01 + half) / C);
        int r0 = (int)Math.Floor((z - margin + half) / C), r1 = (int)Math.Floor((z + sz + margin - 0.01 + half) / C);
        for (int cz = Math.Max(0, r0); cz <= Math.Min(gw - 1, r1); cz++)
            for (int cx = Math.Max(0, c0); cx <= Math.Min(gw - 1, c1); cx++) mark(cz * gw + cx);
    }

    bool AnyRect(double x, double z, double sx, double sz, double margin, Func<int, bool> test)
    {
        int c0 = (int)Math.Floor((x - margin + half) / C), c1 = (int)Math.Floor((x + sx + margin - 0.01 + half) / C);
        int r0 = (int)Math.Floor((z - margin + half) / C), r1 = (int)Math.Floor((z + sz + margin - 0.01 + half) / C);
        if (c0 < 0 || r0 < 0 || c1 >= gw || r1 >= gw) return true;
        for (int cz = r0; cz <= r1; cz++)
            for (int cx = c0; cx <= c1; cx++) if (test(cz * gw + cx)) return true;
        return false;
    }

    // ---------- Find a place ----------

    WildPoi TryPlace(string name, PrefabDef def, int biome, List<int> biomeCells)
    {
        int repeat = def.DuplicateRepeatDistance ?? 1000;
        repeat = Math.Min(repeat, w.Size / 3);   // 6500/10000 m: once per map
        for (int attempt = 0; attempt < 40; attempt++)
        {
            int g = biomeCells[rnd.Next(biomeCells.Count)];
            var c = Center(g);
            // rotations: with an entrance the one whose entrance lies nearest to a road; otherwise random
            var rots = Enumerable.Range(0, 4).OrderBy(_ => rnd.Next()).ToList();
            var probes = new List<(PlacedPrefab P, List<(Point E, Vector Out, double W)> Exits, double Score)>();
            foreach (int rot in rots)
            {
                var p = new PlacedPrefab { Name = name, Def = def, Rotation = rot };
                p.X = (int)Math.Round(c.X - p.SizeX / 2.0);
                p.Z = (int)Math.Round(c.Y - p.SizeZ / 2.0);
                if (rot == rots[0] && !SiteOk(p, biome, repeat)) break;   // position is almost the same for all rotations (square: exactly)
                if (p.SizeX != p.SizeZ && rot != rots[0] && !SiteOk(p, biome, repeat)) continue;
                var parts = PrefabParts.For(p, partDef, null);
                var exits = ExitsOf(p, parts);
                double score = exits.Count == 0 ? 0 : exits.Min(e => DistAt(e.E));
                probes.Add((p, exits, score));
            }
            if (probes.Count == 0) continue;
            bool hasExit = probes[0].Exits.Count > 0;
            if (!hasExit)
            {
                var pr = probes[0];
                if (!EarthworkOk(pr.P, out int y)) continue;
                pr.P.Y = y;
                return Accept(pr.P, biome, null);
            }
            foreach (var pr in probes.OrderBy(q => q.Score).Take(2))
            {
                if (pr.Score * C > spec.MaxPath) break;
                if (!EarthworkOk(pr.P, out int y)) break;
                pr.P.Y = y;
                // block the own footprint for the path search
                MarkRect(pr.P.X, pr.P.Z, pr.P.SizeX, pr.P.SizeZ, 2, q => block[q] |= 2);
                WildPath path = null;
                foreach (var ex in pr.Exits.OrderBy(e => DistAt(e.E)))
                {
                    path = FindPath(ex.E, ex.Out, pr.P.TerrainHeight);
                    if (path != null) { path.Width = PathWidthFor(pr.P, ex.W); break; }
                }
                MarkRect(pr.P.X, pr.P.Z, pr.P.SizeX, pr.P.SizeZ, 2, q => block[q] &= 1);
                if (path != null) return Accept(pr.P, biome, path);
            }
        }
        return null;
    }

    /// path width per building (the user's request 2026-11-10: 3–5 m instead of always 5 m): by the longer side of the footprint
    /// (narrowest path up to 20 m, widest from 50 m, whole meters), at most as wide as the entrance (marker/driveway)
    double PathWidthFor(PlacedPrefab p, double exitWidth)
    {
        double lo = Math.Min(spec.PathWidthMin, spec.PathWidth), hi = spec.PathWidth;
        double t = Math.Clamp((Math.Max(p.SizeX, p.SizeZ) - 20) / 30.0, 0, 1);
        double width = Math.Round(lo + (hi - lo) * t);
        if (exitWidth >= lo) width = Math.Min(width, Math.Floor(exitWidth));
        return Math.Clamp(width, lo, hi);
    }

    double DistAt(Point p) { int g = CellOf(p.X, p.Y); return g < 0 ? 1e9 : dist[g]; }

    bool SiteOk(PlacedPrefab p, int biome, int repeat)
    {
        if (p.X < -half + EdgeKeep || p.Z < -half + EdgeKeep || p.X + p.SizeX > half - EdgeKeep || p.Z + p.SizeZ > half - EdgeKeep) return false;
        // completely in the biome (corners, edge midpoints, center)
        for (int i = 0; i <= 2; i++)
            for (int j = 0; j <= 2; j++)
                if (w.BiomeAt(p.X + p.SizeX * i / 2 - (i == 2 ? 1 : 0), p.Z + p.SizeZ * j / 2 - (j == 2 ? 1 : 0)) != biome) return false;
        if (AnyRect(p.X, p.Z, p.SizeX, p.SizeZ, 0, g => tileDist[g] * C < TileGap)) return false;
        if (AnyRect(p.X, p.Z, p.SizeX, p.SizeZ, BuildingGap, g => building[g] || target[g] || tile[g])) return false;
        if (AnyRect(p.X, p.Z, p.SizeX, p.SizeZ, 16, g => water[g])) return false;
        double cx = p.X + p.SizeX / 2.0, cz = p.Z + p.SizeZ / 2.0;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if (poiHash.TryGetValue(Key(cx + dx * 128, cz + dz * 128), out var l)
                    && l.Any(q => (q.X - cx) * (q.X - cx) + (q.Z - cz) * (q.Z - cz) < PoiSpacing * PoiSpacing)) return false;
        if (sameName.TryGetValue(p.Name, out var same) && same.Any(q => (q.X - cx) * (q.X - cx) + (q.Z - cz) * (q.Z - cz) < (double)repeat * repeat)) return false;
        return true;
    }

    /// earthwork limited? Build height = mean of the terrain under the footprint (like when placing by hand); in the footprint
    /// on average at most 3.5 m and nowhere more than 9 m deviation, 16 m beside it at most 14 m (no steep walls)
    bool EarthworkOk(PlacedPrefab p, out int y)
    {
        double mean = GroundShaper.AverageHeight(w, p.X, p.Z, p.SizeX, p.SizeZ);
        y = (int)Math.Round(mean + 0.85) + p.Def.YOffset;
        double ground = y - p.Def.YOffset - 0.85, sum = 0;
        int m = 0;
        for (int z = p.Z - 16; z <= p.Z + p.SizeZ + 16; z += 4)
            for (int x = p.X - 16; x <= p.X + p.SizeX + 16; x += 4)
            {
                double d = Math.Abs(w.HeightAt(x, z) - ground);
                if (p.Contains(x, z)) { if (d > 9) return false; sum += d; m++; }
                else if (d > 14) return false;
            }
        return m == 0 || sum / m <= 3.5;
    }

    /// entrances: RoadExit markers, otherwise the outer edges of the driveways; direction = outwards (nearest side of the footprint)
    static List<(Point E, Vector Out, double W)> ExitsOf(PlacedPrefab p, List<PlacedPrefab> parts)
    {
        var list = new List<(Point, Vector, double)>();
        for (int i = 0; i < p.Def.Exits.Count; i++)
        {
            var (rx, rz) = PlacedPrefab.Rotate(p.Def.Exits[i].X, p.Def.Exits[i].Z, p.Rotation, p.Def.SizeX, p.Def.SizeZ);
            var (sx, sz) = i < p.Def.ExitSizes.Count ? p.Def.ExitSizes[i] : (0, 0);
            list.Add((new Point(p.X + rx, p.Z + rz), Outward(p, p.X + rx, p.Z + rz), Math.Max(sx, sz)));
        }
        if (list.Count == 0)
            foreach (var q in parts.Where(PrefabParts.IsDriveway))
            {
                var (x, z) = PrefabParts.DrivewayExit(q, p);
                list.Add((new Point(x, z), Outward(p, x, z), PrefabParts.DrivewayWidth(q, p)));
            }
        return list;
    }

    static Vector Outward(PlacedPrefab p, double x, double z)
    {
        double s = Math.Abs(z - p.Z), n = Math.Abs(p.Z + p.SizeZ - z), we = Math.Abs(x - p.X), e = Math.Abs(p.X + p.SizeX - x);
        // if the point lies outside, the side over which it protrudes counts
        if (z < p.Z) s = -1; else if (z > p.Z + p.SizeZ) n = -1; else if (x < p.X) we = -1; else if (x > p.X + p.SizeX) e = -1;
        double min = new[] { s, n, we, e }.Min();
        return min == s ? new Vector(0, -1) : min == n ? new Vector(0, 1) : min == we ? new Vector(-1, 0) : new Vector(1, 0);
    }

    WildPoi Accept(PlacedPrefab p, int biome, WildPath path)
    {
        var poi = new WildPoi { Building = p, Parts = PrefabParts.For(p, partDef, null), Biome = biome, HasExit = path != null, Path = path };
        plan.Pois.Add(poi);
        placedCount[p.Name] = placedCount.GetValueOrDefault(p.Name) + 1;
        AddBuilding(p);
        foreach (var q in poi.Parts) MarkRect(q.X, q.Z, q.SizeX, q.SizeZ, 0, g => building[g] = true);
        AddToHash(p.X + p.SizeX / 2.0, p.Z + p.SizeZ / 2.0, p.Name);
        if (path != null)
        {
            path.Poi = poi;
            plan.Paths.Add(path);
            RegisterPathCells(path, plan.Paths.Count);
            dist = GroundShaper.Distance(target, gw, gw);
            UseBridges(path);
        }
        return poi;
    }

    void RegisterPathCells(WildPath path, int id)
    {
        var onBridge = BridgePoints(path);
        for (int i = 1; i < path.Points.Count; i++)
        {
            var a = path.Points[i - 1]; var b = path.Points[i];
            if (onBridge.Contains(a) && onBridge.Contains(b)) continue;   // over the bridge: no junction for later paths there
            double len = (b - a).Length;
            for (double t = 0; t <= len; t += 2)
            {
                var q = a + (b - a) * (t / Math.Max(len, 1e-6));
                int g = CellOf(q.X, q.Y);
                if (g < 0 || target[g]) continue;
                target[g] = true;
                pathOf[g] = id;
                cellDir[g] = (float)Math.Atan2(b.Y - a.Y, b.X - a.X);
            }
        }
    }

    // ---------- Path search ----------

    float[] gScore;
    int[] parent, stamp;
    int epoch, wn, wx0, wz0;
    readonly PriorityQueue<int, float> queue = new();

    /// path from the entrance e (direction outwards) to the nearest road or to a planned path; null = none possible.
    /// When forcedVolcano = true, extends search range and allows volcano mountain grade (up to 22 %) while strictly
    /// preserving YAMEE's core road rules: startSpread = ±45° (no 180° U-turns), ArrivalOk (≥ 45°), Feasible, HasSelfLoop and RoadLine.Fit!
    /// When targetFilter != null (2nd-pass connector), searches only toward cells matching targetFilter (using a fast local
    /// window distance transform), blocks non-matching road/path cells and extraBlock cells, and uses RoadLine.Fit (with SmoothPath fallback).
    WildPath FindPath(Point e, Vector outv, double startH, double? maxPathOverride = null, bool forcedVolcano = false,
        Func<int, bool> targetFilter = null, Func<int, bool> extraBlock = null, bool preferSpline = false, double? startSlackOverride = null,
        bool allowBridges = true)
    {
        double maxPath = maxPathOverride ?? spec.MaxPath;
        double effMaxGrade = forcedVolcano ? Math.Max(spec.MaxGrade, 0.22) : spec.MaxGrade;
        double effGrade = forcedVolcano ? Math.Max(spec.Grade, 0.16) : spec.Grade;
        var lead = e + outv * Lead;
        int s0 = CellOf(lead.X, lead.Y);
        if (s0 < 0 || water[s0]) return null;
        if (targetFilter == null)
        {
            if (block[s0] != 0 && !target[s0]) return null;
            if (dist[s0] * C > maxPath) return null;
        }
        int r = Math.Min(gw, (int)Math.Ceiling((maxPath + 100) / C));
        wn = Math.Min(gw, 2 * r + 1);
        wx0 = Math.Clamp(s0 % gw - r, 0, Math.Max(0, gw - wn));
        wz0 = Math.Clamp(s0 / gw - r, 0, Math.Max(0, gw - wn));
        int Local(int g) { int cx = g % gw - wx0, cz = g / gw - wz0; return cx < 0 || cz < 0 || cx >= wn || cz >= wn ? -1 : cz * wn + cx; }
        int Global(int l) => (wz0 + l / wn) * gw + wx0 + l % wn;
        int ls0 = Local(s0);

        bool[] localTarget = null, localBlocked = null;
        float[] localDist = null;
        if (targetFilter != null)
        {
            int wn2 = wn * wn;
            localTarget = new bool[wn2];
            localBlocked = new bool[wn2];
            bool anyTarget = false;
            for (int l = 0; l < wn2; l++)
            {
                int g = Global(l);
                if (target[g])
                {
                    if (targetFilter(g)) { localTarget[l] = true; anyTarget = true; }
                    else localBlocked[l] = true;
                }
                else if (block[g] != 0 || water[g] || (extraBlock != null && extraBlock(g)))
                    localBlocked[l] = true;
            }
            if (!anyTarget || (!localTarget[ls0] && localBlocked[ls0])) return null;
            localDist = GroundShaper.Distance(localTarget, wn, wn);
            if (localDist[ls0] * C > maxPath) return null;
        }

        bool IsTarget(int l, int g) => localTarget != null ? localTarget[l] : target[g];
        bool IsBlockedCell(int l, int g) => localBlocked != null ? (!localTarget[l] && localBlocked[l]) : (!target[g] && (block[g] != 0 || water[g]));
        float DistH(int l, int g) => localDist != null ? localDist[l] : dist[g];

        int states = wn * wn * 16;
        if (gScore == null || gScore.Length < states) { gScore = new float[states]; parent = new int[states]; stamp = new int[states]; }
        epoch++;
        queue.Clear();
        int d0 = ((int)Math.Round(Math.Atan2(outv.Y, outv.X) / (Math.PI / 8)) % 16 + 16) % 16;
        // Always restrict initial heading to ±45° (never 180° hairpin U-turn at the start point!)
        for (int t = -2; t <= 2; t++)
        {
            int st = ls0 * 16 + ((d0 + t) & 15);
            stamp[st] = epoch; gScore[st] = 30 * Math.Abs(t); parent[st] = -1;
            queue.Enqueue(st, gScore[st] + DistH(ls0, s0) * C);
        }
        int found = -1;
        double limit = forcedVolcano ? maxPath * 3.5 : maxPath * 1.6;
        int maxExpand = forcedVolcano ? 1_200_000 : 600_000;
        int expanded = 0;
        while (queue.TryDequeue(out int state, out float pr))
        {
            int l = state >> 4, d = state & 15;
            int g = Global(l);
            if (pr > gScore[state] + DistH(l, g) * C + 0.01) continue;
            if (IsTarget(l, g))
            {
                if ((targetFilter == null && g == s0) || ArrivalOk(g, d)) { found = state; break; }
                continue;   // too flat: do not join here (no continuing along the road)
            }
            if (gScore[state] > limit || ++expanded > maxExpand) break;
            int cx = g % gw, cz = g / gw;
            for (int turn = -1; turn <= 1; turn++)
            {
                int nd = (d + turn) & 15;
                var (oi, oj) = Steps[nd];
                int nx = cx + oi, nz = cz + oj;
                if (nx < 0 || nz < 0 || nx >= gw || nz >= gw) continue;
                int ng = nz * gw + nx;
                int nl = Local(ng);
                if (nl < 0) continue;
                if (IsBlockedCell(nl, ng)) continue;
                if (Math.Abs(oi) + Math.Abs(oj) == 3)
                {
                    int m1 = (cz + Math.Sign(oj) * (Math.Abs(oj) == 2 ? 1 : 0)) * gw + cx + Math.Sign(oi) * (Math.Abs(oi) == 2 ? 1 : 0);
                    int ml1 = Local(m1);
                    if (ml1 < 0 || IsBlockedCell(ml1, m1)) continue;
                }
                double len = C * Math.Sqrt(oi * oi + oj * oj);
                double grade = Math.Abs(h[ng] - h[g]) / len;
                double cost = len * (turn != 0 ? 1.04 : 1);
                double excess = grade - effGrade * 0.7;   // from 70 % of the usual grade on a detour pays off (Habegu-Land 310/3542: the direct way climbed 14-17 % for 100 m)
                if (excess > 0) cost += len * (forcedVolcano ? (18 * excess + 120 * excess * excess) : (40 * excess + 400 * excess * excess));
                if (grade > effMaxGrade) cost += len * (forcedVolcano ? 14 : 30);
                // cross slope (slopes) and rock walls
                int px = -oj, pz = oi;
                int a = (nz + Math.Sign(pz)) * gw + nx + Math.Sign(px), b = (nz - Math.Sign(pz)) * gw + nx - Math.Sign(px);
                if (a >= 0 && b >= 0 && a < h.Length && b < h.Length)
                {
                    double cross = Math.Abs(h[a] - h[b]) / (2 * C);
                    if (cross > 0.15) cost += len * (forcedVolcano ? 1.5 : 3) * (cross - 0.15);
                }
                if (steep[ng] > 0.7) cost += len * (forcedVolcano ? 4 : 8) * (steep[ng] - 0.7);
                // more expensive right beside a road/path: the path comes in across instead of running beside it shortly before the target
                // (there its slope cut into the road, steps up to 3.5 m)
                if (!IsTarget(nl, ng) && dist[ng] < 3) cost += len * 0.8 * (3 - dist[ng]);
                // soft avoidance band outside the hard city-tile clearance so paths swing cleanly around tiles where room exists
                if (!IsTarget(nl, ng) && tileMargin[ng] < TileAvoidBand)
                {
                    double s = (TileAvoidBand - tileMargin[ng]) / TileAvoidBand;
                    cost += len * 0.6 * s * s;
                }
                int ns = nl * 16 + nd;
                float tentative = (float)(gScore[state] + cost);
                if (stamp[ns] == epoch && tentative >= gScore[ns]) continue;
                stamp[ns] = epoch; gScore[ns] = tentative; parent[ns] = state;
                queue.Enqueue(ns, (float)(tentative + DistH(nl, ng) * C));
            }
            // over a river on a bridge: from the cell in front of one approach straight to the cell in front of the other one
            if (allowBridges && bridgeAt.TryGetValue(g, out var bl))
                foreach (int q in bl)
                {
                    var b = bridges[q];
                    if (b.Used || !LeadsFree(b)) continue;
                    bool fromA = g == b.CellA;
                    int jd = fromA ? b.Dir : (b.Dir + 8) & 15;
                    int diff = ((d - jd) % 16 + 16) % 16;
                    diff = Math.Min(diff, 16 - diff);
                    if (diff > 2) continue;
                    int ng = fromA ? b.CellB : b.CellA;
                    int nl = Local(ng);
                    if (nl < 0 || IsBlockedCell(nl, ng)) continue;
                    int ns = nl * 16 + jd;
                    float tentative = gScore[state] + b.Cost + 30 * diff;
                    if (stamp[ns] == epoch && tentative >= gScore[ns]) continue;
                    stamp[ns] = epoch; gScore[ns] = tentative; parent[ns] = state;
                    queue.Enqueue(ns, tentative + DistH(nl, ng) * C);
                }
        }
        if (found < 0) return null;
        var cells = new List<int>();
        for (int st = found; st >= 0; st = parent[st]) cells.Add(Global(st >> 4));
        cells.Reverse();
        for (int i = 0; i + 1 < cells.Count; i++) if (BridgeBetween(cells[i], cells[i + 1]).B != null) Rej("path: found over a bridge");
        int endCell = Global(found >> 4);
        var path = new WildPath();
        Point end;
        if (pathOf[endCell] > 0)
        {
            path.Target = plan.Paths[pathOf[endCell] - 1];
            end = Nearest(path.Target.Points, Center(endCell));
        }
        else
        {
            var spot = RoadFollower.Center(scan, Center(endCell));
            if (spot == null)
            {
                if (!forcedVolcano) return null;
                end = Center(endCell);
                byte rk = w.RoadAt((int)Math.Round(end.X), (int)Math.Round(end.Y));
                path.EndKind = rk != 0 ? rk : WorldData.Gravel;
                int fd0 = found & 15;
                path.EndDir = new Vector(Math.Cos(fd0 * Math.PI / 8), Math.Sin(fd0 * Math.PI / 8));
                path.EndHalf = path.EndKind == WorldData.Asphalt ? 9.0 : 3.0;
            }
            else
            {
                end = spot.Center;
                path.EndKind = spot.Kind;
                path.EndDir = spot.Dir;
                path.EndHalf = spot.Width / 2;
            }
        }
        // pieces between the bridges (cell centers); a jump between two cells in front of the approaches of a bridge separates them
        var pieces = new List<List<Point>> { new() };
        var crossed = new List<(WildBridge B, bool FromA)>();
        for (int i = 0; i < cells.Count; i++)
        {
            pieces[^1].Add(Center(cells[i]));
            if (i + 1 < cells.Count && BridgeBetween(cells[i], cells[i + 1]) is var (b, fromA) && b != null)
            {
                crossed.Add((b, fromA));
                pieces.Add(new());
            }
        }
        // the target cell itself gone (its center can lie beyond the road center – the smoothed path then ran over the
        // road and back, asphalt lowered by 2 m); arrival direction = driving direction of the last search step
        var raw = pieces[^1];
        if (raw.Count > 1) raw.RemoveAt(raw.Count - 1);
        while (raw.Count > 1 && (raw[^1] - end).Length < 6) raw.RemoveAt(raw.Count - 1);
        raw.Add(end);
        int fd = found & 15;
        var gd = new Vector(Math.Cos(fd * Math.PI / 8), Math.Sin(fd * Math.PI / 8));
        double pathEdge = spec.PathWidth / 2.0 + (forcedVolcano ? spec.Verge + 1.0 : spec.Verge);
        bool Blocked(Point p)
        {
            int c = CellOf(p.X, p.Y);
            if (c < 0) return true;
            // on the surface of the existing road at the junction (not in a tile or water): terrain stays untouched
            if (path.Target == null && !tile[c] && !water[c] && (p - end).Length <= path.EndHalf) return false;
            if (localBlocked != null)
            {
                if ((p - e).Length < 14) return block[c] != 0 || water[c];
                int cl = Local(c);
                if (cl < 0 || IsBlockedCell(cl, c)) return true;
            }
            else if (!target[c] && (block[c] != 0 || water[c])) return true;
            if (cityTiles.Count > 0 && !target[c])
            {
                double ph = w.PreDamageHeightAt(p.X, p.Y, spec.BreakInWasteland);
                if (TooCloseToTile(p.X, p.Y, ph, pathEdge)) return true;
            }
            return false;
        }
        double endH = w.PreDamageHeightAt(end.X, end.Y, spec.BreakInWasteland);
        // each piece smoothed on its own: from the entrance (or the end of the straight piece behind a bridge) to the straight piece in front of
        // the next bridge (or the target); over the bridge straight: lead, approach, approach, lead
        var pts = new List<Point>();
        Point from = e; Vector fromDir = outv; double fromH = startH;
        for (int k = 0; k < pieces.Count; k++)
        {
            Point to; Vector toDir; double toH;
            Point[] over = null;
            if (k < crossed.Count)
            {
                var (b, fromA) = crossed[k];
                over = fromA ? new[] { b.LeadA, b.A, b.B, b.LeadB } : new[] { b.LeadB, b.B, b.A, b.LeadA };
                to = over[0];
                toDir = over[3] - over[0]; toDir.Normalize();
                toH = b.Height;
            }
            else { to = end; toDir = gd; toH = endH; }
            var piece = ((forcedVolcano || preferSpline) ? RoadLine.Fit(pieces[k], from, fromDir, to, toDir, Blocked, 10) : null)
                ?? RoadRouter.SmoothPath(pieces[k], from, (fromDir.X, fromDir.Y), to, (toDir.X, toDir.Y), Blocked);
            double sSlack = k > 0 ? RoadNetwork.BridgeBank : (startSlackOverride ?? (forcedVolcano ? 3.5 : 1));
            double eSlack = k < crossed.Count ? RoadNetwork.BridgeBank : 1;
            if (!Feasible(piece, fromH, toH, sSlack, eSlack, forcedVolcano ? effMaxGrade : null)) { if (crossed.Count > 0) Rej("path: not feasible (piece " + k + ")"); return null; }
            pts.AddRange(pts.Count > 0 ? piece.Skip(1) : piece);
            if (over != null)
            {
                pts.Add(over[1]); pts.Add(over[2]); pts.Add(over[3]);
                path.Bridges.Add(crossed[k].B.Site);
                from = over[3]; fromDir = toDir; fromH = crossed[k].B.Height;
            }
        }
        if (RoadNetwork.HasSelfLoop(pts)) return null;
        double length = 0;
        for (int i = 1; i < pts.Count; i++) length += (pts[i] - pts[i - 1]).Length;
        double straight = (end - e).Length;
        if (!forcedVolcano && (length > maxPath || length > Math.Max(2.5 * straight, straight + 300))) { if (crossed.Count > 0) Rej("path: too long"); return null; }
        if (!PathClearsTiles(pts, startH, endH, path, forcedVolcano)) { if (crossed.Count > 0) Rej("path: too close to city tile"); return null; }
        path.Points = pts;
        path.Length = length;
        return path;
    }

    /// Exact clearance check of a finished path against all nearby city tiles, using the planned longitudinal
    /// road profile so embankments, bridge ramps or grading above/below the raw terrain are caught as well.
    bool PathClearsTiles(List<Point> pts, double startH, double endH, WildPath path, bool forcedVolcano)
    {
        if (cityTiles.Count == 0 || pts == null || pts.Count < 2) return true;
        double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        for (int i = 0; i < pts.Count; i++)
        {
            var q = pts[i];
            if (q.X < minX) minX = q.X; if (q.X > maxX) maxX = q.X;
            if (q.Y < minZ) minZ = q.Y; if (q.Y > maxZ) maxZ = q.Y;
        }
        List<CityTileInfo> near = null;
        double pad = MaxTileClearance + 2;
        for (int i = 0; i < cityTiles.Count; i++)
        {
            var t = cityTiles[i];
            if (t.X1 + pad < minX || t.X0 - pad > maxX || t.Z1 + pad < minZ || t.Z0 - pad > maxZ) continue;
            (near ??= new()).Add(t);
        }
        if (near == null) return true;

        double edge = spec.PathWidth / 2.0 + (forcedVolcano ? spec.Verge + 1.0 : spec.Verge);
        var even = NetworkBuilder.Even(pts, 10);
        var s = RoadSettings.Gravel();
        s.Width = spec.PathWidth;
        s.Shoulder = 0;
        s.GrassEdge = forcedVolcano ? spec.Verge + 1.0 : spec.Verge;
        s.MaxGrade = forcedVolcano ? Math.Max(spec.MaxGrade, 0.20) : spec.Grade;
        s.Smooth = forcedVolcano ? 12 : 20;
        s.Blend = 8;
        s.CutSlope = forcedVolcano ? 1.85 : 1.5;
        s.BreakInWasteland = spec.BreakInWasteland;
        s.OffsetAlong = forcedVolcano ? null : CutIn(w, pts, edge, spec.BreakInWasteland);
        s.OffsetRamp = 30;
        var heights = new double?[even.Count];
        heights[0] = startH;
        heights[^1] = endH;
        var r = RoadPlanner.Compute(w, even, s, heights);
        if (r != null && path.Bridges.Count > 0)
        {
            var levels = NetworkBuilder.BridgeZones(r, path.Bridges);
            if (levels.Count > 0)
            {
                var fixes = new List<(double, double)>();
                foreach (var (za, zb, zh) in levels)
                    for (double x = za; x <= zb + 0.01; x += 1) fixes.Add((x, zh));
                r = RoadPlanner.Compute(w, even, s, heights, fixes, levels: levels) ?? r;
            }
        }
        // When joining an existing road, the stretch already on its surface (within EndHalf of the end) leaves the terrain untouched.
        double endSkip = path.Target == null ? path.EndHalf : 0;
        if (r != null && r.Path.Count >= 2)
        {
            int last = r.Path.Count - 1;
            for (int i = 0; i <= last; i = i == last ? last + 1 : Math.Min(last, i + 8))   // every ~2 m plus exact end
            {
                var pt = r.Path[i];
                if (endSkip > 0 && r.Length - pt.Dist < endSkip) continue;
                double roadH = r.Profile[i];
                for (int j = 0; j < near.Count; j++)
                {
                    var t = near[j];
                    if (RectDist(pt.X, pt.Z, t) < TileClearance(roadH - t.Height, edge)) return false;
                }
            }
            return true;
        }
        double walked = 0, total = 0;
        for (int i = 1; i < pts.Count; i++) total += (pts[i] - pts[i - 1]).Length;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var ab = pts[i] - a;
            double len = ab.Length;
            int steps = Math.Max(1, (int)Math.Ceiling(len / 4.0));
            for (int k = 0; k <= steps; k++)
            {
                double distHere = walked + len * (k / (double)steps);
                if (endSkip > 0 && total - distHere < endSkip) continue;
                var q = a + ab * (k / (double)steps);
                double ph = w.PreDamageHeightAt(q.X, q.Y, spec.BreakInWasteland);
                for (int j = 0; j < near.Count; j++)
                {
                    var t = near[j];
                    if (RectDist(q.X, q.Y, t) < TileClearance(ph - t.Height, edge)) return false;
                }
            }
            walked += len;
        }
        return true;
    }

    /// gradient feasible? The terrain along the path (every 4 m) must not move away from the start (ground height of the building) and from the target
    /// faster than the path can follow with MaxGrade and at most 6 m cut/fill (otherwise a jump behind the
    /// entrance: entrance to a steep slope, 14 m drop over 30 m, profile with 172 %; Rifane-Land 2026-11-09)
    /// startSlack/endSlack: cut or fill allowed right at that end (1 m; at a bridge BridgeBank: the river valley lies up to 12 m below the
    /// approach, the path climbs out of it behind the level piece)
    bool Feasible(List<Point> pts, double startH, double endH, double startSlack = 1, double endSlack = 1, double? maxGradeOverride = null)
    {
        double maxG = maxGradeOverride ?? spec.MaxGrade;
        var d = new List<(double D, double H)>();
        double walked = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var ab = pts[i] - a;
            double len = ab.Length;
            for (double t = 0; t < len; t += 4) { var q = a + ab * (t / len); d.Add((walked + t, w.PreDamageHeightAt(q.X, q.Y, spec.BreakInWasteland))); }
            walked += len;
        }
        // start and end are fixed (each about 4 m horizontal at the entrance and on the road): in between MaxGrade suffices
        if (Math.Abs(startH - endH) > maxG * Math.Max(1, walked - 12)) return false;
        // cut/fill may grow only slowly near the ends (1 m + 0.25 m per meter, at most 6 m)
        foreach (var (dd, hh) in d)
            if (Math.Abs(hh - startH) - Math.Min(Math.Max(6, startSlack), startSlack + 0.25 * dd) > maxG * dd
                || Math.Abs(hh - endH) - Math.Min(Math.Max(6, endSlack), endSlack + 0.25 * (walked - dd)) > maxG * (walked - dd)) return false;
        return true;
    }

    /// junction steep enough? The path must meet the road or path at least at 45° – paths joining at a shallow angle ran
    /// alongside for a stretch, their roadways lay up to 2.3 m apart there (Rifane-Land 2026-11-09)
    bool ArrivalOk(int g, int dir)
    {
        if (float.IsNaN(cellDir[g]))
        {
            var axis = RoadFollower.Axis(scan, Center(g), 7);
            cellDir[g] = axis is Vector v ? (float)Math.Atan2(v.Y, v.X) : -9f;
        }
        if (cellDir[g] == -9f) return true;
        double a = dir * Math.PI / 8 - cellDir[g];
        double diff = Math.Abs(Math.IEEERemainder(a, Math.PI));   // 0 … π/2
        return diff >= Math.PI / 4 - 0.01;
    }

    static Point Nearest(List<Point> line, Point p)
    {
        Point best = line[0];
        double bd = double.MaxValue;
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1]; var ab = line[i] - a;
            double l2 = ab.LengthSquared, t = l2 == 0 ? 0 : Math.Clamp(((p - a) * ab) / l2, 0, 1);
            var q = a + ab * t;
            double d = (q - p).LengthSquared;
            if (d < bd) { bd = d; best = q; }
        }
        return best;
    }

    // ---------- Build ----------

    static double PathWorkWeight(WildPath path) => 6.0 + Math.Max(30.0, path.Length) * 0.14;

    /// enters the plan (one edit step): buildings and driveways, terrain level under every building (like when placing
    /// by hand), then the paths in plan order (connection to earlier built paths) as road-brush gravel paths
    public static WildernessBuildResult Build(WorldData w, WorldEdits e, WildernessPlan plan, WildernessSpec spec, IProgress<string> progress)
    {
        e.GuardBanks();   // platforms and paths must not lower the banks of lakes
        var res = new WildernessBuildResult();
        foreach (var poi in plan.Pois)
        {
            e.AddPrefab(poi.Building);
            foreach (var q in poi.Parts) e.AddPrefab(q);
        }
        var bridges = plan.Paths.SelectMany(p => p.Bridges).ToList();
        bool hasVolcano = plan.Paths.Any(p => p.IsVolcanoSpiral);
        // Weight every phase by its actual earthwork/raster cost so levelling, bridges and gravel paths
        // each move the progress bar in proportion to the real time they take (paths dominate on normal maps).
        double wLevel = plan.Pois.Count * 1.0;
        double wBridges = bridges.Count * 16.0;
        double wPaths = plan.Paths.Sum(PathWorkWeight);
        double wVolcano = hasVolcano ? 45.0 : 0.0;
        double totalWork = Math.Max(1.0, wLevel + wBridges + wPaths + wVolcano);
        double doneWork = 0.0;
        double Frac() => Math.Clamp(doneWork / totalWork, 0.0, 0.99);

        int k = 0;
        int levelStep = Math.Max(1, Math.Min(10, plan.Pois.Count / 25));
        foreach (var poi in plan.Pois)
        {
            if (k == 0 || (k + 1) % levelStep == 0 || k + 1 == plan.Pois.Count)
                progress.Report(Loc.F("Levelling the ground: {0} of {1} …", k + 1, plan.Pois.Count), Frac());
            k++;
            // building including its own parts level (do not leave parts out – otherwise around a kiosk a margin with
            // old terrain remained in the footprint, steps up to 3.7 m); parts that protrude beyond the footprint (driveways) afterwards with their own transition
            var p = poi.Building;
            var own = poi.Parts.Append(p).ToList();
            // Match the exact terrain height the game stamps under buildings (TerrainHeight = N * 257/256; at +196 m
            // this is ~0.62 m higher than GroundHeight, avoiding a rectangular "set on top" step around high-altitude POIs)
            double poiH = p.Def != null ? p.TerrainHeight : p.GroundHeight;
            res.HeightPoints += GroundShaper.Flatten(w, e, p, poiH, 1, own, false, true).Changed;
            foreach (var q in poi.Parts)
                if (q.X < p.X || q.Z < p.Z || q.X + q.SizeX > p.X + p.SizeX || q.Z + q.SizeZ > p.Z + p.SizeZ)
                    res.HeightPoints += GroundShaper.Flatten(w, e, q, poiH, 1, own, false, true).Changed;
            res.Pois++;
            doneWork += 1.0;
        }
        // bridges over rivers before the paths, which end on their approaches (bank guard off meanwhile, like "Build roads")
        var kit = RoadNetwork.Bridges;
        int bi = 0;
        foreach (var b in bridges)
        {
            bi++;
            b.Gravel = true;   // wilderness paths are gravel: gravel approaches
            var (endDef, tileDef) = kit?.Defs(b) ?? (null, null);
            if (endDef == null || (!b.Single && tileDef == null)) { res.Warnings.Add(Loc.F("Bridge at {0} not built: the pieces of its bridge kit were not found (Settings › Mods › Bridge mods).", Loc.F("{0} {1}, {2} {3}", Math.Abs(Math.Round(b.Center.X)), b.Center.X < 0 ? Loc.T("W") : Loc.T("E"), Math.Abs(Math.Round(b.Center.Y)), b.Center.Y < 0 ? Loc.T("S") : Loc.T("N")))); doneWork += 16.0; continue; }
            progress.Report(bridges.Count > 1 ? Loc.F("Building bridge {0} of {1} …", bi, bridges.Count) : Loc.T("Building bridges …"), Frac());
            bool keep = e.KeepBanksNow;
            e.KeepBanksNow = false;
            Bridge.Build(w, e, b, endDef, tileDef);
            e.KeepBanksNow = keep;
            res.Bridges++;
            doneWork += 16.0;
        }
        var built = new Dictionary<WildPath, RoadResult>();
        var quiet = new Progress<string>(_ => { });
        k = 0;
        int pathStep = Math.Max(1, Math.Min(4, plan.Paths.Count / 60));
        foreach (var path in plan.Paths)
        {
            if (k == 0 || (k + 1) % pathStep == 0 || k + 1 == plan.Paths.Count)
                progress.Report(Loc.F("Building path {0} of {1} …", k + 1, plan.Paths.Count), Frac());
            k++;
            var r = PlanPath(w, path, spec, built);
            if (r == null) { res.Warnings.Add(Loc.F("Path to {0} could not be built.", path.Poi?.Building.Name ?? Loc.T("wilderness road"))); doneWork += PathWorkWeight(path); continue; }
            var start = path.Points[0];
            var end = path.Points[^1];
            bool hasStartJunction = path.StartTarget != null || path.StartKind != 0;
            var beforeStart = hasStartJunction ? Snapshot(w, start, JunctionWindow) : default;
            var before = Snapshot(w, end, JunctionWindow);
            // heights around the path before entering it: SmoothSteps only evens out steps the path made itself
            const int Pad = 30;
            int bx0 = (int)path.Points.Min(q => q.X) - Pad, bz0 = (int)path.Points.Min(q => q.Y) - Pad;
            int bw = (int)path.Points.Max(q => q.X) + Pad + 2 - bx0, bh = (int)path.Points.Max(q => q.Y) + Pad + 2 - bz0;
            var old = new float[bw * bh];
            for (int z = 0; z < bh; z++)
                for (int x = 0; x < bw; x++) old[z * bw + x] = (float)w.HeightAt(bx0 + x, bz0 + z);
            double Old(int x, int z) => x >= bx0 && z >= bz0 && x < bx0 + bw && z < bz0 + bh ? old[(z - bz0) * bw + (x - bx0)] : w.HeightAt(x, z);
            // everything except the bridges with their approaches (Bridge.Build made them; the path reaches 6 m onto each approach)
            if (path.Bridges.Count == 0) RoadApplier.Apply(w, e, r, quiet);
            else foreach (var (a, b) in NetworkBuilder.Pieces(r, path.Bridges)) RoadApplier.Apply(w, e, r, quiet, a, b);
            if (hasStartJunction) FeatherJunction(w, e, start, beforeStart);
            bool isVolcanoBranch = path.IsVolcano && !path.IsVolcanoSpiral;
            FeatherJunction(w, e, end, before, isVolcanoBranch ? 10 : FeatherReach);
            if (isVolcanoBranch && path.Points.Count >= 2)
            {
                var inDir = path.Points[^2] - end;
                if (inDir.Length > 1e-4) inDir.Normalize();
                SmoothVolcanoFork(w, e, end, inDir, before);
            }
            built[path] = r;
            res.Paths++;
            res.Length += r.Length;
            res.MaxGrade = Math.Max(res.MaxGrade, r.MaxGrade);
            res.MaxCut = Math.Max(res.MaxCut, r.MaxCut);
            res.MaxFill = Math.Max(res.MaxFill, r.MaxFill);
            res.Built.Add((path, r));
            if (r.MaxGrade > spec.Grade + 0.005) res.SteepPaths++;
            var xs = path.Points.Select(q => q.X).ToList(); var zs = path.Points.Select(q => q.Y).ToList();
            CityGenerator.SmoothSteps(w, e, (int)xs.Min(), (int)zs.Min(), (int)xs.Max(), (int)zs.Max(), Pad, Old, path.IsVolcano ? spec.Verge + 1.0 : spec.Verge);
            doneWork += PathWorkWeight(path);
        }
        if (plan.Paths.Count > 0)
            progress.Report(Loc.F("Building path {0} of {1} …", plan.Paths.Count, plan.Paths.Count), Frac());
        // Re-adapt terrain closely around volcano POIs after building the spiral/branch while strictly sparing every
        // road + a 3.5..9.5 m road-edge fade so the spiral verge & Pad 1 junction are NEVER undercut by a vertical ledge!
        if (hasVolcano) progress.Report(Loc.T("Finishing volcano terrain …"), Frac());
        foreach (var poi in plan.Pois)
        {
            if (poi.Path?.IsVolcano != true) continue;
            res.HeightPoints += RelevelVolcanoPoiSafe(w, e, poi);
        }
        var spiralBuilt = res.Built.FirstOrDefault(b => b.Path.IsVolcanoSpiral);
        if (spiralBuilt.Road != null && VolcanoSite.Load(w.Folder) is { } volcanoSite)
        {
            var spiralJunctions = CollectVolcanoSpiralJunctions(spiralBuilt.Path, spiralBuilt.Road, res.Built);
            var branchBuilt = res.Built.FirstOrDefault(b => b.Path.IsVolcano && !b.Path.IsVolcanoSpiral);
            Point? midPoiJoin = branchBuilt.Path?.Points is { Count: >= 2 } bp ? bp[^1] : null;
            ApplyVolcanoLandslides(w, e, volcanoSite, spiralBuilt.Road, spiralJunctions, res.VolcanoLandslides, midPoiJoin);
        }
        return res;
    }

    internal const int JunctionWindow = 24, FeatherReach = 6;
    /// level landing (m) behind the edge of the road or path a gravel path joins, before its rounded knee starts
    const double JunctionLanding = 3;

    /// road mask and heights around the junction before entering (window ±r m)
    internal static (byte[] Mask, ushort[] Heights) Snapshot(WorldData w, Point c, int r)
    {
        int x0 = (int)Math.Floor(c.X) - r, z0 = (int)Math.Floor(c.Y) - r, s = 2 * r + 1;
        var m = new byte[s * s];
        var h = new ushort[s * s];
        for (int z = 0; z < s; z++)
            for (int x = 0; x < s; x++)
            {
                m[z * s + x] = w.RoadAt(x0 + x, z0 + z);
                h[z * s + x] = w.Contains(x0 + x, z0 + z) ? w.Heights[w.Index(x0 + x, z0 + z)] : (ushort)0;
            }
        return (m, h);
    }

    /// soften the junction: new surface up to FeatherReach m beside the existing one (road or earlier built path) runs out
    /// to its height (nearest existing surface point, smoothstep) – otherwise at oblique junctions and on steep
    /// paths there remained jumps up to 2.5 m between old and new surface (Rifane-Land 2026-11-09).
    /// Also the verge and slope of the path there (cells the path changed), not only the gravel: otherwise a ledge up to 1 m
    /// remained beside the asphalt edge (Degaku-Land 1040/1983, 2026-11-14)
    internal static void FeatherJunction(WorldData w, WorldEdits e, Point c, (byte[] Mask, ushort[] Heights) snap, int reach = FeatherReach)
    {
        var before = snap.Mask;
        int r = JunctionWindow, s = 2 * r + 1, x0 = (int)Math.Floor(c.X) - r, z0 = (int)Math.Floor(c.Y) - r;
        var d = new float[s * s];
        var src = new int[s * s];
        for (int g = 0; g < d.Length; g++) { d[g] = before[g] != 0 ? 0 : 1e9f; src[g] = before[g] != 0 ? g : -1; }
        // two passes (chamfer), carrying along the source (nearest old surface point)
        void Relax(int g, int q, float step) { if (q < 0 || src[q] < 0) return; if (d[q] + step < d[g]) { d[g] = d[q] + step; src[g] = src[q]; } }
        for (int z = 0; z < s; z++)
            for (int x = 0; x < s; x++)
            {
                int g = z * s + x;
                if (x > 0) Relax(g, g - 1, 1);
                if (z > 0) { Relax(g, g - s, 1); if (x > 0) Relax(g, g - s - 1, 1.414f); if (x < s - 1) Relax(g, g - s + 1, 1.414f); }
            }
        for (int z = s - 1; z >= 0; z--)
            for (int x = s - 1; x >= 0; x--)
            {
                int g = z * s + x;
                if (x < s - 1) Relax(g, g + 1, 1);
                if (z < s - 1) { Relax(g, g + s, 1); if (x < s - 1) Relax(g, g + s + 1, 1.414f); if (x > 0) Relax(g, g + s - 1, 1.414f); }
            }
        // the chamfer distance is only the pre-filter: blending with it and with the single nearest surface point gave a washboard of
        // ±0.13 m across the whole mouth where the old road runs obliquely through the grid and climbs (Testit-Land 215/1483, 2026-11-26:
        // town road 14 %, one-block humps in the gravel). Now the exact distance to the old surface, and its height is a weighted mean
        // of the surface points within 2 m of the nearest one (weights fall to 0 at that limit, so the mean changes smoothly)
        int look = reach + 3;
        for (int z = 0; z < s; z++)
            for (int x = 0; x < s; x++)
            {
                int g = z * s + x;
                int wx = x0 + x, wz = z0 + z;
                if (before[g] != 0 || src[g] < 0 || d[g] > reach + 1 || !w.Contains(wx, wz)) continue;
                bool changed = w.Heights[w.Index(wx, wz)] != snap.Heights[g];
                if (w.RoadAt(wx, wz) == 0 && !changed) continue;   // untouched terrain stays
                double dmin = double.MaxValue;
                for (int qz = Math.Max(0, z - look); qz <= Math.Min(s - 1, z + look); qz++)
                    for (int qx = Math.Max(0, x - look); qx <= Math.Min(s - 1, x + look); qx++)
                        if (before[qz * s + qx] != 0) dmin = Math.Min(dmin, Math.Sqrt((qx - x) * (qx - x) + (qz - z) * (qz - z)));
                if (dmin > reach) continue;
                double sum = 0, wsum = 0;
                for (int qz = Math.Max(0, z - look); qz <= Math.Min(s - 1, z + look); qz++)
                    for (int qx = Math.Max(0, x - look); qx <= Math.Min(s - 1, x + look); qx++)
                    {
                        int q = qz * s + qx;
                        if (before[q] == 0) continue;
                        double wt = dmin + 2 - Math.Sqrt((qx - x) * (qx - x) + (qz - z) * (qz - z));
                        if (wt <= 0) continue;
                        sum += wt * snap.Heights[q] / 256.0; wsum += wt;
                    }
                double hOld = sum / wsum, hNew = w.HeightAt(wx, wz);
                double t = Math.Clamp((dmin - 1) / Math.Max(1.0, reach - 1), 0, 1), k = t * t * (3 - 2 * t);
                double h = hOld + (hNew - hOld) * k;
                var hv = (ushort)Math.Clamp(Math.Round(h * 256), 0, 65535);
                int i = w.Index(wx, wz);
                if (hv != w.Heights[i]) e.SetHeight(i, hv);
            }
    }

    /// cut the path a little into the terrain (the user's request 2026-11-10: on slopes and ridges it lay like on an embankment,
    /// the gravel ended at the slope edge): every 4 m it is measured how far the terrain at the verge edge on the lower
    /// side (2 m beyond the verge) lies below the center; there the path is lowered by 80 % of that, at most 1.5 m, smoothed
    /// over ±20 m. On level ground 0, on slopes and crests up to 1.5 m – the slope becomes a cut rather than an
    /// embankment there. Result per 4 m of path (RoadSettings.OffsetAlong).
    static double[] CutIn(WorldData w, List<Point> pts, double edge, bool breakInWasteland = false)
    {
        double H(double x, double z) => w.PreDamageHeightAt(x, z, breakInWasteland);
        var off = new List<double>();
        double walked = 0, next = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var ab = pts[i] - a;
            double len = ab.Length;
            if (len < 0.01) continue;
            double nx = -ab.Y / len, nz = ab.X / len, side = edge + 2;
            for (; next <= walked + len; next += 4)
            {
                var q = a + ab * ((next - walked) / len);
                double c = H(q.X, q.Y), lo = Math.Min(H(q.X + nx * side, q.Y + nz * side), H(q.X - nx * side, q.Y - nz * side));
                off.Add(-Math.Min(1.5, 0.8 * Math.Max(0, c - lo)));
            }
            walked += len;
        }
        if (off.Count < 3) return null;
        var v = off.ToArray();
        for (int pass = 0; pass < 2; pass++)
        {
            var t = new double[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                double sum = 0; int m = 0;
                for (int j = Math.Max(0, i - 5); j <= Math.Min(v.Length - 1, i + 5); j++) { sum += v[j]; m++; }
                t[i] = sum / m;
            }
            v = t;
        }
        return v;
    }

    static RoadResult PlanPath(WorldData w, WildPath path, WildernessSpec spec, Dictionary<WildPath, RoadResult> built)
    {
        var pts = NetworkBuilder.Even(path.Points, 10);
        var s = RoadSettings.Gravel();
        s.Width = path.Width;
        s.Shoulder = 0;
        s.GrassEdge = path.IsVolcano ? spec.Verge + 1.0 : spec.Verge;   // volcano spiral gets +1 m extra verge to the cliff edge
        if (path.IsVolcano) { s.MaxGrade = 0.20; s.CurveGrade = true; } // enforce 12–20 % mountain grade & gentler tight curves
        else if (path.IsConnector) s.CurveGrade = true;                 // gentler gradient in tight curves for 2nd-pass connectors
        s.KeepRoads = true;         // the road it joins (and every path it crosses) keeps its heights
        s.BreakInWasteland = spec.BreakInWasteland;
        s.OffsetAlong = path.IsVolcano ? null : CutIn(w, path.Points, path.Width / 2 + s.GrassEdge, spec.BreakInWasteland);
        s.OffsetRamp = 30;   // no cut at the entrance and junction, growing over 30 m (≤ 5 % additional gradient)
        s.Smooth = path.IsVolcano ? 12 : 20;
        s.Blend = 8;
        s.CutSlope = path.IsVolcano ? 1.85 : 1.5;   // natural ~28–30° slope on any small volcano embankment
        // connection at the start (2nd-pass connector branching off an already-built wilderness path or an existing road)
        (double Height, double DX, double DZ, double Grade, double Half)? startInfo = null;
        var start = pts[0];
        if (path.StartTarget != null)
        {
            if (built.TryGetValue(path.StartTarget, out var sr))
                startInfo = NetworkBuilder.At(sr, new Point(start.X, start.Y));
        }
        else if (path.StartKind != 0)
        {
            double H(double x, double z) => w.PreDamageHeightAt(x, z, spec.BreakInWasteland);
            var sd = path.StartDir;
            double sGrade = (H(start.X + sd.X * 10, start.Y + sd.Y * 10) - H(start.X - sd.X * 10, start.Y - sd.Y * 10)) / 20;
            startInfo = (H(start.X, start.Y), sd.X, sd.Y, sGrade, Math.Max(2.5, path.StartHalf));
        }
        // connection at the target: height, direction, gradient and width of the road or built path there
        (double Height, double DX, double DZ, double Grade, double Half)? info = null;
        var end = pts[^1];
        if (path.Target != null)
        {
            if (built.TryGetValue(path.Target, out var tr)) info = NetworkBuilder.At(tr, new Point(end.X, end.Y));
        }
        else
        {
            double H(double x, double z) => w.PreDamageHeightAt(x, z, spec.BreakInWasteland);
            var d = path.EndDir;
            double grade = (H(end.X + d.X * 10, end.Y + d.Y * 10) - H(end.X - d.X * 10, end.Y - d.Y * 10)) / 20;
            info = (H(end.X, end.Y), d.X, d.Y, grade, Math.Max(2.5, path.EndHalf));
        }
        var heights = new double?[pts.Count];
        if (path.IsVolcano && path.FixedHeights != null && path.FixedHeights.Count == path.Points.Count)
            ApplyFixedSpiralHeights(path.Points, path.FixedHeights, pts, heights, path.IsVolcanoSpiral, info?.Height);
        if (startInfo is { } si0) heights[0] = si0.Height;
        if (info is { } i0) heights[^1] = i0.Height;

        // bridges: no cut-in near them (the terrain beside the path is the river valley there), the gravel widens to the approach
        // (gravel over the whole width of the bridge, see BridgeSite.Gravel) over BridgeTaper in front of it
        List<double?> widths = null;
        var noBreak = path.Bridges.Count > 0
            ? path.Bridges.SelectMany(b => { var (pa, pb, _) = NetworkBuilder.ApproachEnds(b); return new[] { (pa, 12.0), (pb, 12.0), (b.Center, 12.0) }; }).ToList()
            : null;
        if (path.Bridges.Count > 0)
        {
            var along = new double[pts.Count];
            for (int q = 1; q < pts.Count; q++) along[q] = along[q - 1] + (pts[q] - pts[q - 1]).Length;
            double AlongAt(Point p) { int best = 0; for (int q = 1; q < pts.Count; q++) if ((pts[q] - p).LengthSquared < (pts[best] - p).LengthSquared) best = q; return along[best]; }
            var zones = path.Bridges.Select(b => { var (pa, pb, _) = NetworkBuilder.ApproachEnds(b); double a = AlongAt(pa), c = AlongAt(pb); return (From: Math.Min(a, c), To: Math.Max(a, c)); }).ToList();
            double Off(double d) => zones.Min(z => Math.Max(0, Math.Max(z.From - d, d - z.To)));
            if (s.OffsetAlong != null)
                for (int q = 0; q < s.OffsetAlong.Length; q++)
                {
                    double t = Math.Clamp((Off(4.0 * q) - RoadNetwork.BridgeLead) / 30, 0, 1);
                    s.OffsetAlong[q] *= t * t * (3 - 2 * t);
                }
            double full = path.Bridges.Max(b => b.RoadWidth);   // the whole road of the approach (10x10 kit: the whole width)
            if (full > path.Width)
            {
                widths = new List<double?>();
                for (int q = 0; q < pts.Count; q++)
                {
                    double t = Math.Clamp(Off(along[q]) / BridgeTaper, 0, 1);
                    widths.Add(full + (path.Width - full) * t * t * (3 - 2 * t));
                }
            }
        }

        RoadResult Compute(double grade)
        {
            s.MaxGrade = grade;
            var r0 = RoadPlanner.Compute(w, pts, s, heights, widths: widths, noBreakZones: noBreak);
            if (r0 == null) return null;
            var levels = NetworkBuilder.BridgeZones(r0, path.Bridges);
            if (startInfo is null && info is null && levels.Count == 0) return r0;
            var fixes = new List<(double, double)>();
            List<(double, double, double)> cross = null;
            // like "Build roads" (NetworkBuilder): on the surface of the road or path it joins the new path lies on its real heights along the
            // new centre line (crown, banking, embankment), not at one height; from its edge on the course continues with the gradient there
            var ends = new List<(double Dist, int Sign, double H, double Grade)>();
            double T(double dist) => r0.Terrain[r0.IndexAt(Math.Clamp(dist, 0, r0.Length))];
            // the real edge of the surface the path joins (asphalt + shoulder, or the gravel of a built path), measured along the new
            // centre line from the junction: only that surface is fixed. Beyond it lies the embankment of the road (Buzovi-Land 1690/1154,
            // 2026-11-26: road 2.2 m above the terrain, the path took over its 25–30 % embankment as "road" and dropped 1.3 m in 4 m
            // right behind the junction) – that is ordinary terrain the path may fill. Returns the distance of the last surface point.
            double SurfaceEdge(int sign, double half)
            {
                double last = -1;
                for (double x = 0; x <= half + 6; x += 0.5)
                {
                    int i = r0.IndexAt(Math.Clamp(sign > 0 ? x : r0.Length - x, 0, r0.Length));
                    if (w.RoadAt((int)Math.Floor(r0.Path[i].X), (int)Math.Floor(r0.Path[i].Z)) != 0) last = x;
                    else if (last >= 0 && x - last >= 1.5) break;
                }
                return last < 0 ? half : Math.Clamp(last, 1, half + 4);
            }
            if (startInfo is { } sinf)
            {
                if (path.IsVolcano)
                    for (double x = 0; x <= sinf.Half + 2.01; x += 1) fixes.Add((x, sinf.Height));
                else
                {
                    // on the surface its real heights, then a short level landing at the height of its edge (like at tiles and building
                    // entrances), from there the rounded knee below starts with 0 %
                    double edge = SurfaceEdge(1, sinf.Half), he = T(edge);
                    for (double x = 0; x <= edge + 0.01; x += 1) fixes.Add((x, T(x)));
                    for (double x = Math.Floor(edge) + 1; x <= edge + JunctionLanding + 0.01; x += 1) fixes.Add((x, he));
                    if (edge + JunctionLanding < r0.Length - 2) ends.Add((edge + JunctionLanding, 1, he, 0));
                }
                var (snx, snz) = RoadPlanner.Normal(r0.Path, 0);
                double sSlope = Math.Clamp(sinf.Grade * (sinf.DX * snx + sinf.DZ * snz), -0.2, 0.2);
                if (Math.Abs(sSlope) > 0.001) (cross ??= new()).Add((0, sSlope, sinf.Half + 3));
            }
            if (info is { } inf2)
            {
                if (path.IsVolcano)
                    for (double x = 0; x <= inf2.Half + 2.01; x += 1) fixes.Add((r0.Length - x, inf2.Height));
                else
                {
                    double edge = SurfaceEdge(-1, inf2.Half), he = T(r0.Length - edge);
                    for (double x = 0; x <= edge + 0.01; x += 1) fixes.Add((r0.Length - x, T(r0.Length - x)));
                    for (double x = Math.Floor(edge) + 1; x <= edge + JunctionLanding + 0.01; x += 1) fixes.Add((r0.Length - x, he));
                    if (edge + JunctionLanding < r0.Length - 2) ends.Add((r0.Length - edge - JunctionLanding, -1, he, 0));
                }
                var (nx, nz) = RoadPlanner.Normal(r0.Path, r0.Path.Count - 1);
                double slope = Math.Clamp(inf2.Grade * (inf2.DX * nx + inf2.DZ * nz), -0.2, 0.2);
                if (Math.Abs(slope) > 0.001) (cross ??= new()).Add((r0.Length, slope, inf2.Half + 3));
            }
            // over the bridges and their approaches level at the height of the approach road
            foreach (var (za, zb, zh) in levels)
                for (double x = za; x <= zb + 0.01; x += 1) fixes.Add((x, zh));
            var r1 = RoadPlanner.Compute(w, pts, s, heights, fixes, cross, widths, levels.Count > 0 ? levels : null, noBreak);
            if (r1 != null && levels.Count > 0)
            {
                // round the knee between the level bridge piece and the course (like "Build roads"): a cubic from the level piece to the
                // course of the first pass, at least 32 m long (otherwise the path climbs right behind the approach), nowhere steeper than allowed
                double Pr(double dist) => r1.Profile[r1.IndexAt(Math.Clamp(dist, 0, r1.Length))];
                var more = new List<(double, double)>(fixes);
                foreach (var (za, zb, zh) in levels)
                    foreach (var (d0, sg) in new[] { (za, -1), (zb, 1) })
                        foreach (double L in new[] { 32.0, 45, 60, 80 })
                        {
                            double d1 = d0 + sg * L;
                            if (d1 <= 2 || d1 >= r1.Length - 2) break;
                            double h1 = Pr(d1), g1 = (Pr(d1 + 2) - Pr(d1 - 2)) / 4;
                            double a = Math.Min(d0, d1), b = Math.Max(d0, d1), ha = sg > 0 ? zh : h1, hb = sg > 0 ? h1 : zh, ga = sg > 0 ? 0 : g1, gb = sg > 0 ? g1 : 0;
                            var curve = new List<(double, double)>();
                            double steepest = 0, prev = ha;
                            for (double x = a + 1; x < b; x += 1)
                            {
                                double u = (x - a) / (b - a), u2 = u * u, u3 = u2 * u;
                                double h = (2 * u3 - 3 * u2 + 1) * ha + (u3 - 2 * u2 + u) * (b - a) * ga + (-2 * u3 + 3 * u2) * hb + (u3 - u2) * (b - a) * gb;
                                steepest = Math.Max(steepest, Math.Abs(h - prev));
                                prev = h;
                                curve.Add((x, h));
                            }
                            if (steepest > Math.Max(grade, Math.Abs(g1)) + 0.01) continue;
                            more.AddRange(curve);
                            break;
                        }
                if (more.Count > fixes.Count && RoadPlanner.Compute(w, pts, s, heights, more, cross, widths, levels, noBreak) is RoadResult r2) { r1 = r2; fixes = more; }
            }
            // round the knee between the piece on the road and the course (vertical curve, as in "Build roads"): a cubic from the road edge
            // (its gradient) to the course of the first pass (height and gradient there), as short as possible but nowhere steeper than
            // allowed; if none of the lengths is that gentle, the gentlest one wins (measured at Habegu-Land 310/3542: the last 4 m
            // climbed at 26 %) as long as it stays below the highest allowed gradient
            if (r1 != null && !path.IsVolcano && levels.Count == 0 && ends.Count > 0)
            {
                double Pr(double dist) => r1.Profile[r1.IndexAt(Math.Clamp(dist, 0, r1.Length))];
                var more = new List<(double, double)>(fixes);
                double kneeMax = grade;
                foreach (var (d0, sg, h0, g0) in ends)
                {
                    List<(double, double)> best = null; double bestSteep = double.MaxValue; bool ok = false;
                    foreach (double L in new[] { 16.0, 24, 32, 45, 60, 80, 110, 140 })
                    {
                        double d1 = d0 + sg * L;
                        if (d1 <= 2 || d1 >= r1.Length - 2) break;
                        double h1 = Pr(d1), g1 = (Pr(d1 + 2) - Pr(d1 - 2)) / 4;
                        double a = Math.Min(d0, d1), b = Math.Max(d0, d1), ha = sg > 0 ? h0 : h1, hb = sg > 0 ? h1 : h0, ga = sg > 0 ? g0 : g1, gb = sg > 0 ? g1 : g0;
                        var curve = new List<(double, double)>();
                        double steepest = 0, prev = ha;
                        for (double x = a + 1; x < b; x += 1)
                        {
                            double u = (x - a) / (b - a), u2 = u * u, u3 = u2 * u;
                            double h = (2 * u3 - 3 * u2 + 1) * ha + (u3 - 2 * u2 + u) * (b - a) * ga + (-2 * u3 + 3 * u2) * hb + (u3 - u2) * (b - a) * gb;
                            steepest = Math.Max(steepest, Math.Abs(h - prev));
                            prev = h;
                            curve.Add((x, h));
                        }
                        if (steepest <= Math.Max(grade, Math.Max(Math.Abs(g0), Math.Abs(g1))) + 0.01) { best = curve; ok = true; break; }
                        if (steepest < bestSteep) { bestSteep = steepest; best = curve; }
                    }
                    if (best == null) continue;
                    if (!ok) { if (bestSteep > spec.MaxGrade + 0.02) continue; kneeMax = Math.Max(kneeMax, bestSteep + 0.01); }
                    more.AddRange(best);
                }
                if (more.Count > fixes.Count)
                {
                    s.MaxGrade = kneeMax;   // otherwise the planner clamps the curve back to the usual grade
                    var r3 = RoadPlanner.Compute(w, pts, s, heights, more, cross, widths, null, noBreak);
                    s.MaxGrade = grade;
                    if (r3 != null && r3.MaxGrade <= r1.MaxGrade + 0.001) r1 = r3;
                }
            }
            if (r1 != null && (startInfo != null || info != null))
            {
                var zones = new List<(Point, double)>();
                if (startInfo is { } sinf3) zones.Add((new Point(start.X, start.Y), sinf3.Half + 1));
                if (info is { } inf3) zones.Add((new Point(end.X, end.Y), inf3.Half + 1));
                r1.ProtectZones = zones;
            }
            return r1;
        }

        var r = Compute(spec.Grade);
        if (r == null) return null;
        // embankments count double (the user's request 2026-11-10: better cut than fill; a 5 m embankment also raised
        // the junction of a short path so high that it became 62 % steep)
        static double Earth(RoadResult q) => Math.Max(q.MaxCut, 2 * q.MaxFill);
        // the steeper variant (steep "drop-offs", 2026-11-23) only if it clearly saves earthwork (at least a quarter)
        if (Earth(r) > 6 && spec.MaxGrade > spec.Grade)
        {
            var steeper = Compute(spec.MaxGrade);
            if (steeper != null && Earth(steeper) < 0.75 * Earth(r)) r = steeper;
        }
        return r;
    }
}
