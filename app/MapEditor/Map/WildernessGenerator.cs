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
    public double Grade = 0.12, MaxGrade = 0.18;   // gravel paths: usual/highest gradient (like the roads)
    /// gravel path 3–5 m depending on the building (the user's request 2026-11-10; before fixed 5 m): small buildings narrow, large wide,
    /// never wider than their entrance
    public double PathWidthMin = 3, PathWidth = 5;
    /// verge: per side this much ground of the biome horizontal beside the gravel (the user's request 2026-11-10: at sloping
    /// places the gravel ended directly at the slope edge)
    public double Verge = 1;
    public double MaxPath = 1500;                  // longest path (original: at most about 1.5 km to the nearest asphalt road)
    public string Seed = "YAMEE";
    public WildernessSpec Clone() => (WildernessSpec)MemberwiseClone();
}

/// planned gravel path from the entrance of a wilderness POI to the nearest road or to an already planned path
public sealed class WildPath
{
    public List<Point> Points;         // entrance → target
    public WildPoi Poi;
    public WildPath Target;            // ends at this planned path (null = at an existing road)
    public byte EndKind;               // surface of the existing road at the target (0 = planned path)
    public Vector EndDir;              // direction of the existing road at the target
    public double EndHalf;             // half width of the existing road at the target (including edge)
    public double Length;
    public double Width = 5;           // width of the gravel (3–5 m depending on the building)
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
    public int Pois, Paths, HeightPoints;
    public double Length, MaxGrade, MaxCut, MaxFill;
    public int SteepPaths;
    public List<(WildPath Path, RoadResult Road)> Built = new();
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
public sealed class WildernessGenerator
{
    const int C = 8;                      // grid (m)
    const double EdgeKeep = 120, TileGap = 100, BuildingGap = 24, PoiSpacing = 100, Lead = 12;

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
        progress?.Report(Loc.T("Reading the map …"));
        BuildGrid();
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
        int total = plan.TargetPerBiome.Sum();

        var failed = new HashSet<(int, string)>();       // building that finds no place in this biome
        var misses = new int[5];
        var done = new bool[5];
        for (int b = 0; b < 5; b++) done[b] = plan.TargetPerBiome[b] == 0 || cells[b].Count == 0;
        while (!done.All(d => d))
        {
            for (int b = 0; b < 5; b++)
            {
                if (done[b]) continue;
                if (plan.PerBiome[b] >= plan.TargetPerBiome[b]) { done[b] = true; continue; }
                var allowed = pool.Where(c => (c.Rule.Biomes == null || c.Rule.Biomes.Contains(b)) && !failed.Contains((b, c.Name))).ToList();
                if (allowed.Count == 0) { done[b] = true; continue; }
                var pick = Weighted(allowed);
                var poi = TryPlace(pick.Name, pick.Def, b, cells[b]);
                if (poi == null)
                {
                    failed.Add((b, pick.Name));
                    if (++misses[b] > 80) done[b] = true;   // many failed attempts in a row: biome is full
                    continue;
                }
                misses[b] = 0;
                plan.PerBiome[b]++;
                if (plan.Pois.Count % 10 == 0) progress?.Report(Loc.F("{0} of about {1} wilderness POIs placed …", plan.Pois.Count, total));
            }
        }
        for (int b = 0; b < 5; b++)
            if (plan.PerBiome[b] < plan.TargetPerBiome[b] * 0.9)
                plan.Problems.Add(Loc.F("{0}: only {1} of {2} POIs found a place (steep terrain, water or no possible path).",
                    Loc.T(BiomePalette.Names[b]), plan.PerBiome[b], plan.TargetPerBiome[b]));
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

    // ---------- Grid ----------

    void BuildGrid()
    {
        int n = gw * gw;
        h = new float[n]; steep = new float[n];
        water = new bool[n]; tile = new bool[n]; target = new bool[n]; building = new bool[n];
        block = new byte[n]; pathOf = new int[n];
        cellDir = new float[n];
        Array.Fill(cellDir, float.NaN);
        Parallel.For(0, gw, cz =>
        {
            for (int cx = 0; cx < gw; cx++)
            {
                int g = cz * gw + cx;
                int x = -half + cx * C, z = -half + cz * C;
                h[g] = (float)((w.HeightAt(x + 2, z + 2) + w.HeightAt(x + 6, z + 2) + w.HeightAt(x + 2, z + 6) + w.HeightAt(x + 6, z + 6)) / 4);
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
            if (p.IsTile) { MarkRect(p.X, p.Z, p.SizeX, p.SizeZ, 0, (g) => { tile[g] = true; target[g] = false; block[g] |= 1; }); continue; }
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
        // edge (60 m) blocked for paths
        int e = 60 / C;
        for (int cz = 0; cz < gw; cz++)
            for (int cx = 0; cx < gw; cx++)
                if (cx < e || cz < e || cx >= gw - e || cz >= gw - e) block[cz * gw + cx] |= 1;
        tileDist = GroundShaper.Distance(tile, gw, gw);
        dist = GroundShaper.Distance(target, gw, gw);
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
                var parts = PrefabParts.For(p, partDef);
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
        var poi = new WildPoi { Building = p, Parts = PrefabParts.For(p, partDef), Biome = biome, HasExit = path != null, Path = path };
        plan.Pois.Add(poi);
        placedCount[p.Name] = placedCount.GetValueOrDefault(p.Name) + 1;
        AddBuilding(p);
        foreach (var q in poi.Parts) MarkRect(q.X, q.Z, q.SizeX, q.SizeZ, 0, g => building[g] = true);
        AddToHash(p.X + p.SizeX / 2.0, p.Z + p.SizeZ / 2.0, p.Name);
        if (path != null)
        {
            path.Poi = poi;
            plan.Paths.Add(path);
            int id = plan.Paths.Count;
            for (int i = 1; i < path.Points.Count; i++)
            {
                var a = path.Points[i - 1]; var b = path.Points[i];
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
            dist = GroundShaper.Distance(target, gw, gw);
        }
        return poi;
    }

    // ---------- Path search ----------

    float[] gScore;
    int[] parent, stamp;
    int epoch, wn, wx0, wz0;
    readonly PriorityQueue<int, float> queue = new();

    /// path from the entrance e (direction outwards) to the nearest road or to a planned path; null = none possible
    WildPath FindPath(Point e, Vector outv, double startH)
    {
        var lead = e + outv * Lead;
        int s0 = CellOf(lead.X, lead.Y);
        if (s0 < 0 || (block[s0] != 0 && !target[s0]) || water[s0]) return null;
        if (dist[s0] * C > spec.MaxPath) return null;
        int r = (int)Math.Ceiling((spec.MaxPath + 100) / C);
        wn = 2 * r + 1;
        wx0 = s0 % gw - r; wz0 = s0 / gw - r;
        int states = wn * wn * 16;
        if (gScore == null || gScore.Length < states) { gScore = new float[states]; parent = new int[states]; stamp = new int[states]; }
        epoch++;
        queue.Clear();
        int Local(int g) { int cx = g % gw - wx0, cz = g / gw - wz0; return cx < 0 || cz < 0 || cx >= wn || cz >= wn ? -1 : cz * wn + cx; }
        int Global(int l) => (wz0 + l / wn) * gw + wx0 + l % wn;
        int d0 = ((int)Math.Round(Math.Atan2(outv.Y, outv.X) / (Math.PI / 8)) % 16 + 16) % 16;
        int ls0 = Local(s0);
        for (int t = -2; t <= 2; t++)
        {
            int st = ls0 * 16 + ((d0 + t) & 15);
            stamp[st] = epoch; gScore[st] = 30 * Math.Abs(t); parent[st] = -1;
            queue.Enqueue(st, gScore[st] + dist[s0] * C);
        }
        int found = -1;
        double limit = spec.MaxPath * 1.6;
        int expanded = 0;
        while (queue.TryDequeue(out int state, out float pr))
        {
            int l = state >> 4, d = state & 15;
            int g = Global(l);
            if (pr > gScore[state] + dist[g] * C + 0.01) continue;
            if (target[g])
            {
                if (g == s0 || ArrivalOk(g, d)) { found = state; break; }
                continue;   // too flat: do not join here (no continuing along the road)
            }
            if (gScore[state] > limit || ++expanded > 600_000) break;
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
                if (!target[ng] && (block[ng] != 0 || water[ng])) continue;
                if (Math.Abs(oi) + Math.Abs(oj) == 3)
                {
                    int m1 = (cz + Math.Sign(oj) * (Math.Abs(oj) == 2 ? 1 : 0)) * gw + cx + Math.Sign(oi) * (Math.Abs(oi) == 2 ? 1 : 0);
                    if (!target[m1] && (block[m1] != 0 || water[m1])) continue;
                }
                double len = C * Math.Sqrt(oi * oi + oj * oj);
                double grade = Math.Abs(h[ng] - h[g]) / len;
                double cost = len * (turn != 0 ? 1.04 : 1);
                double excess = grade - spec.Grade * 0.85;
                if (excess > 0) cost += len * (40 * excess + 400 * excess * excess);
                if (grade > spec.MaxGrade) cost += len * 30;
                // cross slope (slopes) and rock walls
                int px = -oj, pz = oi;
                int a = (nz + Math.Sign(pz)) * gw + nx + Math.Sign(px), b = (nz - Math.Sign(pz)) * gw + nx - Math.Sign(px);
                if (a >= 0 && b >= 0 && a < h.Length && b < h.Length)
                {
                    double cross = Math.Abs(h[a] - h[b]) / (2 * C);
                    if (cross > 0.15) cost += len * 3 * (cross - 0.15);
                }
                if (steep[ng] > 0.7) cost += len * 8 * (steep[ng] - 0.7);
                // more expensive right beside a road/path: the path comes in across instead of running beside it shortly before the target
                // (there its slope cut into the road, steps up to 3.5 m)
                if (!target[ng] && dist[ng] < 3) cost += len * 0.8 * (3 - dist[ng]);
                int ns = nl * 16 + nd;
                float tentative = (float)(gScore[state] + cost);
                if (stamp[ns] == epoch && tentative >= gScore[ns]) continue;
                stamp[ns] = epoch; gScore[ns] = tentative; parent[ns] = state;
                queue.Enqueue(ns, (float)(tentative + dist[ng] * C));
            }
        }
        if (found < 0) return null;
        var raw = new List<Point>();
        for (int st = found; st >= 0; st = parent[st]) raw.Add(Center(Global(st >> 4)));
        raw.Reverse();
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
            if (spot == null) return null;
            end = spot.Center;
            path.EndKind = spot.Kind;
            path.EndDir = spot.Dir;
            path.EndHalf = spot.Width / 2;
        }
        // the target cell itself gone (its center can lie beyond the road center – the smoothed path then ran over the
        // road and back, asphalt lowered by 2 m); arrival direction = driving direction of the last search step
        if (raw.Count > 1) raw.RemoveAt(raw.Count - 1);
        while (raw.Count > 1 && (raw[^1] - end).Length < 6) raw.RemoveAt(raw.Count - 1);
        raw.Add(end);
        int fd = found & 15;
        var gd = new Vector(Math.Cos(fd * Math.PI / 8), Math.Sin(fd * Math.PI / 8));
        bool Blocked(Point p) { int c = CellOf(p.X, p.Y); return c < 0 || (!target[c] && (block[c] != 0 || water[c])); }
        var pts = RoadRouter.SmoothPath(raw, e, (outv.X, outv.Y), end, (gd.X, gd.Y), Blocked);
        double length = 0;
        for (int i = 1; i < pts.Count; i++) length += (pts[i] - pts[i - 1]).Length;
        double straight = (end - e).Length;
        if (length > spec.MaxPath || length > Math.Max(2.5 * straight, straight + 300)) return null;
        if (!Feasible(pts, startH, w.HeightAt((int)Math.Floor(end.X), (int)Math.Floor(end.Y)))) return null;
        path.Points = pts;
        path.Length = length;
        return path;
    }

    /// gradient feasible? The terrain along the path (every 4 m) must not move away from the start (ground height of the building) and from the target
    /// faster than the path can follow with MaxGrade and at most 6 m cut/fill (otherwise a jump behind the
    /// entrance: entrance to a steep slope, 14 m drop over 30 m, profile with 172 %; Rifane-Land 2026-11-09)
    bool Feasible(List<Point> pts, double startH, double endH)
    {
        var d = new List<(double D, double H)>();
        double walked = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var ab = pts[i] - a;
            double len = ab.Length;
            for (double t = 0; t < len; t += 4) { var q = a + ab * (t / len); d.Add((walked + t, w.HeightAt((int)Math.Floor(q.X), (int)Math.Floor(q.Y)))); }
            walked += len;
        }
        // start and end are fixed (each about 4 m horizontal at the entrance and on the road): in between MaxGrade suffices
        if (Math.Abs(startH - endH) > spec.MaxGrade * Math.Max(1, walked - 12)) return false;
        // cut/fill may grow only slowly near the ends (1 m + 0.25 m per meter, at most 6 m)
        foreach (var (dd, hh) in d)
            if (Math.Abs(hh - startH) - Math.Min(6, 1 + 0.25 * dd) > spec.MaxGrade * dd
                || Math.Abs(hh - endH) - Math.Min(6, 1 + 0.25 * (walked - dd)) > spec.MaxGrade * (walked - dd)) return false;
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

    /// enters the plan (one edit step): buildings and driveways, terrain level under every building (like when placing
    /// by hand), then the paths in plan order (connection to earlier built paths) as road-brush gravel paths
    public static WildernessBuildResult Build(WorldData w, WorldEdits e, WildernessPlan plan, WildernessSpec spec, IProgress<string> progress)
    {
        var res = new WildernessBuildResult();
        foreach (var poi in plan.Pois)
        {
            e.AddPrefab(poi.Building);
            foreach (var q in poi.Parts) e.AddPrefab(q);
        }
        int k = 0;
        foreach (var poi in plan.Pois)
        {
            if (++k % 20 == 0) progress?.Report(Loc.F("Levelling the ground: {0} of {1} …", k, plan.Pois.Count));
            // building including its own parts level (do not leave parts out – otherwise around a kiosk a margin with
            // old terrain remained in the footprint, steps up to 3.7 m); parts that protrude beyond the footprint (driveways) afterwards with their own transition
            var p = poi.Building;
            var own = poi.Parts.Append(p).ToList();
            res.HeightPoints += GroundShaper.Flatten(w, e, p, p.GroundHeight, 1, own).Changed;
            foreach (var q in poi.Parts)
                if (q.X < p.X || q.Z < p.Z || q.X + q.SizeX > p.X + p.SizeX || q.Z + q.SizeZ > p.Z + p.SizeZ)
                    res.HeightPoints += GroundShaper.Flatten(w, e, q, p.GroundHeight, 1, own).Changed;
            res.Pois++;
        }
        var built = new Dictionary<WildPath, RoadResult>();
        var quiet = new Progress<string>(_ => { });
        k = 0;
        foreach (var path in plan.Paths)
        {
            if (++k % 10 == 0) progress?.Report(Loc.F("Building path {0} of {1} …", k, plan.Paths.Count));
            var r = PlanPath(w, path, spec, built);
            if (r == null) { res.Warnings.Add(Loc.F("Path to {0} could not be built.", path.Poi.Building.Name)); continue; }
            var end = path.Points[^1];
            var before = Snapshot(w, end, JunctionWindow);
            RoadApplier.Apply(w, e, r, quiet);
            FeatherJunction(w, e, end, before);
            built[path] = r;
            res.Paths++;
            res.Length += r.Length;
            res.MaxGrade = Math.Max(res.MaxGrade, r.MaxGrade);
            res.MaxCut = Math.Max(res.MaxCut, r.MaxCut);
            res.MaxFill = Math.Max(res.MaxFill, r.MaxFill);
            res.Built.Add((path, r));
            if (r.MaxGrade > spec.Grade + 0.005) res.SteepPaths++;
            var xs = path.Points.Select(q => q.X).ToList(); var zs = path.Points.Select(q => q.Y).ToList();
            CityGenerator.SmoothSteps(w, e, (int)xs.Min(), (int)zs.Min(), (int)xs.Max(), (int)zs.Max(), 30);
        }
        return res;
    }

    const int JunctionWindow = 24, FeatherReach = 6;

    /// road mask around the junction before entering (window ±r m)
    static byte[] Snapshot(WorldData w, Point c, int r)
    {
        int x0 = (int)Math.Floor(c.X) - r, z0 = (int)Math.Floor(c.Y) - r, s = 2 * r + 1;
        var m = new byte[s * s];
        for (int z = 0; z < s; z++)
            for (int x = 0; x < s; x++) m[z * s + x] = w.RoadAt(x0 + x, z0 + z);
        return m;
    }

    /// soften the junction: new surface up to FeatherReach m beside the existing one (road or earlier built path) runs out
    /// to its height (nearest existing surface point, smoothstep) – otherwise at oblique junctions and on steep
    /// paths there remained jumps up to 2.5 m between old and new surface (Rifane-Land 2026-11-09)
    static void FeatherJunction(WorldData w, WorldEdits e, Point c, byte[] before)
    {
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
        // read the heights of the old surface points beforehand (they are not changed below)
        for (int z = 0; z < s; z++)
            for (int x = 0; x < s; x++)
            {
                int g = z * s + x;
                int wx = x0 + x, wz = z0 + z;
                if (before[g] != 0 || src[g] < 0 || d[g] > FeatherReach || !w.Contains(wx, wz) || w.RoadAt(wx, wz) == 0) continue;
                int q = src[g];
                double hOld = w.HeightAt(x0 + q % s, z0 + q / s), hNew = w.HeightAt(wx, wz);
                double t = Math.Clamp((d[g] - 1) / (FeatherReach - 1), 0, 1), k = t * t * (3 - 2 * t);
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
    static double[] CutIn(WorldData w, List<Point> pts, double edge)
    {
        double H(double x, double z) => w.HeightAt((int)Math.Floor(x), (int)Math.Floor(z));
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
        s.GrassEdge = spec.Verge;   // verge made of biome ground beside the gravel
        s.OffsetAlong = CutIn(w, path.Points, path.Width / 2 + spec.Verge);
        s.OffsetRamp = 30;   // no cut at the entrance and junction, growing over 30 m (≤ 5 % additional gradient)
        s.Smooth = 20;
        s.Blend = 8;
        s.CutSlope = 1.5;
        // connection at the target: height, direction, gradient and width of the road or built path there
        (double Height, double DX, double DZ, double Grade, double Half)? info = null;
        var end = pts[^1];
        if (path.Target != null)
        {
            if (built.TryGetValue(path.Target, out var tr)) info = NetworkBuilder.At(tr, new Point(end.X, end.Y));
        }
        else
        {
            double H(double x, double z) => w.HeightAt((int)Math.Floor(x), (int)Math.Floor(z));
            var d = path.EndDir;
            double grade = (H(end.X + d.X * 10, end.Y + d.Y * 10) - H(end.X - d.X * 10, end.Y - d.Y * 10)) / 20;
            info = (H(end.X, end.Y), d.X, d.Y, grade, Math.Max(2.5, path.EndHalf));
        }
        var heights = new double?[pts.Count];
        if (info is { } i0) heights[^1] = i0.Height;

        RoadResult Compute(double grade)
        {
            s.MaxGrade = grade;
            var r0 = RoadPlanner.Compute(w, pts, s, heights);
            if (r0 == null || info is not { } inf) return r0;
            var fixes = new List<(double, double)>();
            for (double x = 0; x <= inf.Half + 2.01; x += 1) fixes.Add((r0.Length - x, inf.Height));
            var (nx, nz) = RoadPlanner.Normal(r0.Path, r0.Path.Count - 1);
            double slope = Math.Clamp(inf.Grade * (inf.DX * nx + inf.DZ * nz), -0.2, 0.2);
            var cross = Math.Abs(slope) > 0.001 ? new List<(double, double, double)> { (r0.Length, slope, inf.Half + 3) } : null;
            var r1 = RoadPlanner.Compute(w, pts, s, heights, fixes, cross);
            if (r1 != null) r1.ProtectZones = new List<(Point, double)> { (new Point(end.X, end.Y), inf.Half + 1) };
            return r1;
        }

        var r = Compute(spec.Grade);
        if (r == null) return null;
        // embankments count double (the user's request 2026-11-10: better cut than fill; a 5 m embankment also raised
        // the junction of a short path so high that it became 62 % steep)
        static double Earth(RoadResult q) => Math.Max(q.MaxCut, 2 * q.MaxFill);
        if (Earth(r) > 6 && spec.MaxGrade > spec.Grade)
        {
            var steeper = Compute(spec.MaxGrade);
            if (steeper != null && Earth(steeper) < Earth(r)) r = steeper;
        }
        return r;
    }
}
