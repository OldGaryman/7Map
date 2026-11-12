using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MapEditor.Map;

/// settings of the terrain generator (the user's request 2026-11-06: new world without cities, roads and POIs, little water)
public sealed class GeneratorSpec
{
    public string Name = "New World";
    /// edge length in meters (2048 … 10240, multiple of 1024)
    public int Size = 8192;
    public string Seed = "YAMEE";
    /// shares of the biomes in percent (order like BiomePalette: pine forest, burnt forest, desert, snow, wasteland);
    /// default = values of the game (Generation.Forest … in map_info.xml, measured on the worlds)
    public int[] Shares = { 18, 18, 21, 21, 22 };
    /// factors 0 … 2 (1 = as measured in the game)
    public double Mountains = 1.4, Hills = 1.3, Rough = 1.2, Valleys = 1.2, Erosion = 1.2;
    /// game folder (for the height stamps in DataStamps); null = noise only
    public string GameFolder;
    /// 0 = no lakes, 1 = few, 2 = some, 3 = many
    public int Lakes = 2;
    /// unique formations: 0 = none, 1 = few (about 1 per 12 km²), 2 = some, 3 = many
    public int Anomalies = 2;
    public int SpawnCount = 32;

    public int SeedValue
    {
        get
        {
            // stable across program versions (string.GetHashCode is random per program start): FNV-1a
            uint h = 2166136261;
            foreach (char c in Seed ?? "") { h ^= c; h *= 16777619; }
            return (int)(h & 0x7FFFFFFF);
        }
    }

    public string Describe() => string.Format(CultureInfo.InvariantCulture,
        "Name={0}\r\nSize={1}\r\nSeed={2}\r\nShares={3}\r\nMountains={4:0.##}\r\nHills={5:0.##}\r\nRough={6:0.##}\r\nLakes={7}\r\nSpawnCount={8}\r\nValleys={9:0.##}\r\nErosion={10:0.##}\r\nStamps={11}\r\nAnomalies={12}\r\nGenerator=4\r\n",
        Name, Size, Seed, string.Join(",", Shares), Mountains, Hills, Rough, Lakes, SpawnCount, Valleys, Erosion, GameFolder != null, Anomalies);
}

/// generates a whole world (heights, biomes, lakes, spawn points) from noise – without cities, roads and buildings.
/// Everything is described in meters so that the small preview (coarse grid) shows the same landscape as the finished world.
/// Procedure: biomes grow from seed points to their share (grid 32 m) → terrain shape per biome (base height, hills,
/// mountain ridges, mesas in the desert; transitions at the biome borders soft) on a 4 m grid → landslide
/// (thermal erosion) → lakes (basins + water level) → full resolution (bicubic + fine noise) → files.
public sealed partial class WorldGenerator
{
    // values per biome (index like BiomePalette). From TERRAIN_ANALYSE.md: peak relief pine 41–55 m, burnt 71–85,
    // desert 41–46 (plateaus), snow 89–96, wasteland flat; ground 41–49 m.
    // 2026-11-07 (feedback of the user: "very tame"): mountains clearly higher and more frequent, valleys, waves in the terrain everywhere
    static readonly double[] BaseOff = { 4, 2, 3, 10, 0 };
    static readonly double[] HillAmp = { 26, 22, 16, 28, 22 };   // 2026-11-09: wasteland hillier
    static readonly double[] RollAmp = { 7, 7, 6, 8, 10 };
    static readonly double[] MountAmp = { 95, 135, 45, 175, 40 };
    static readonly double[] MountCover = { 0.38, 0.48, 0.22, 0.62, 0.16 };
    static readonly double[] ValleyAmp = { 22, 26, 14, 30, 10 };
    static readonly double[] PlateauAmp = { 0, 0, 60, 0, 0 };   // 2026-11-10: state 0.73 (55) + 10 % (the user's request)
    static readonly double[] RoughAmp = { 0.55, 0.7, 0.75, 0.7, 0.45 };   // desert craggier

    const int BiomeCell = 32;   // grid of the biome regions in meters
    const double GroundLevel = 40;

    readonly GeneratorSpec spec;
    readonly int size;
    readonly Random rnd;
    readonly Perlin cont, hills, mmask, ridge, plat, warpX, warpZ, cost, detail, peakVar, rolls, valley, crag;

    // biome regions (grid BiomeCell, row 0 = south) and soft weights for the terrain shape
    int nb;
    byte[] region;
    float[][] weight;

    // coarse grid
    public int N { get; private set; }
    public double Cell { get; private set; }
    float[] coarse;
    int[] lakeOf;        // 0 = no lake, otherwise number + 1 (only cells in the basin including an 8 m edge)
    float[] calm;        // 0 at the lake … 1 far away: damping of the fine noise
    public readonly List<LakeInfo> LakeList = new();

    public sealed record LakeInfo(double X, double Z, double Radius, int Level, double Depth);

    public WorldGenerator(GeneratorSpec spec)
    {
        this.spec = spec;
        size = spec.Size;
        int seed = spec.SeedValue;
        rnd = new Random(seed);
        cont = new Perlin(seed + 1); hills = new Perlin(seed + 2); mmask = new Perlin(seed + 3); ridge = new Perlin(seed + 4);
        plat = new Perlin(seed + 5); warpX = new Perlin(seed + 6); warpZ = new Perlin(seed + 7); cost = new Perlin(seed + 8);
        detail = new Perlin(seed + 9); peakVar = new Perlin(seed + 10); rolls = new Perlin(seed + 11); valley = new Perlin(seed + 12); crag = new Perlin(seed + 13);
    }

    // ---------------------------------------------------------------- biomes

    void GrowBiomes()
    {
        nb = size / BiomeCell;
        int n = nb * nb;
        region = new byte[n];
        var dist = new double[n];
        Array.Fill(dist, double.MaxValue);
        var owner = new sbyte[n];
        Array.Fill(owner, (sbyte)-1);
        int total = spec.Shares.Sum();
        if (total <= 0) total = 1;
        var quota = spec.Shares.Select(s => (long)Math.Round((double)s / total * n)).ToArray();
        var count = new long[5];
        var pq = new PriorityQueue<(int idx, int b), double>();
        var seeds = new List<(double x, double z)>();
        double areaKm2 = (double)size * size / 1e6;

        // seed points: pine forest in the middle (where one mostly spawns), snow rather in the north, desert rather in the south
        var order = new[] { 0, 3, 2, 1, 4 };
        foreach (int b in order)
        {
            if (spec.Shares[b] <= 0) continue;
            int k = Math.Max(1, (int)Math.Round(spec.Shares[b] / (double)total * areaKm2 / 7));
            for (int s = 0; s < k; s++)
            {
                double bx = 0, bz = 0, minDist = size * 0.2;
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    if (b == 0 && s == 0) { bx = size / 2.0; bz = size / 2.0; break; }
                    double lo = b == 3 ? 0.55 : b == 2 ? 0.05 : 0.05, hi = b == 3 ? 0.95 : b == 2 ? 0.45 : 0.95;
                    bx = size * (0.05 + 0.9 * rnd.NextDouble());
                    bz = size * (lo + (hi - lo) * rnd.NextDouble());
                    if (seeds.All(p => Math.Abs(p.x - bx) + Math.Abs(p.z - bz) >= minDist)) break;
                    if (attempt % 40 == 39) minDist *= 0.7;
                }
                seeds.Add((bx, bz));
                int ix = Math.Clamp((int)(bx / BiomeCell), 0, nb - 1), iz = Math.Clamp((int)(bz / BiomeCell), 0, nb - 1);
                int idx = iz * nb + ix;
                if (dist[idx] > 0) { dist[idx] = 0; pq.Enqueue((idx, b), 0); }
            }
        }
        if (pq.Count == 0) { Array.Fill(region, (byte)0); return; }

        // growing (Dijkstra with noisy costs → irregular borders), each biome up to its share
        var stepCost = new double[n];
        Parallel.For(0, nb, iz =>
        {
            for (int ix = 0; ix < nb; ix++)
            {
                double x = (ix + 0.5) * BiomeCell, z = (iz + 0.5) * BiomeCell;
                stepCost[iz * nb + ix] = 1 + 6 * Math.Clamp(0.5 + 1.5 * cost.Fbm(x / 600, z / 600, 4), 0, 1);
            }
        });
        var leftover = new List<(int idx, int b, double d)>();
        Grow(pq, dist, owner, stepCost, quota, count, leftover);
        // rest (a biome was enclosed or rounding): keep growing without a border
        if (owner.Any(o => o < 0))
        {
            var pq2 = new PriorityQueue<(int idx, int b), double>();
            for (int i = 0; i < n; i++)
                if (owner[i] >= 0) pq2.Enqueue((i, owner[i]), 0);
            var noLimit = Enumerable.Repeat(long.MaxValue, 5).ToArray();
            Array.Fill(dist, double.MaxValue);
            for (int i = 0; i < n; i++) if (owner[i] >= 0) dist[i] = 0;
            var o2 = (sbyte[])owner.Clone();
            for (int i = 0; i < n; i++) if (owner[i] >= 0) o2[i] = -1;
            Grow(pq2, dist, o2, stepCost, noLimit, new long[5], leftover);
            for (int i = 0; i < n; i++) if (owner[i] < 0) owner[i] = o2[i];
        }
        for (int i = 0; i < n; i++) region[i] = (byte)Math.Max((sbyte)0, owner[i]);

        // smooth borders (majority 3 × 3, twice)
        for (int pass = 0; pass < 2; pass++)
        {
            var copy = (byte[])region.Clone();
            Parallel.For(0, nb, iz =>
            {
                Span<int> votes = stackalloc int[5];
                for (int ix = 0; ix < nb; ix++)
                {
                    votes.Clear();
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = Math.Clamp(ix + dx, 0, nb - 1), z = Math.Clamp(iz + dz, 0, nb - 1);
                            votes[copy[z * nb + x]]++;
                        }
                    int best = copy[iz * nb + ix];
                    for (int b = 0; b < 5; b++) if (votes[b] > votes[best]) best = b;
                    if (votes[best] >= 5) region[iz * nb + ix] = (byte)best;
                }
            });
        }

        // equalize shares (2026-11-10, Hibape-Land: wasteland 15.5 % instead of 22 %, snow 25.7 % instead of 21 %)
        Rebalance(region, quota, stepCost);

        // map center (0,0) always pine forest (the user's request 2026-11-10): the pine forest does grow from the center, but
        // the swirling of the borders (BiomeAt, up to about 350 m) could push another biome there. Therefore set the cells that
        // BiomeAt actually looks at within 150 m around the center to pine forest.
        if (spec.Shares[0] > 0)
        {
            double c = size / 2.0;
            for (double dz = -CenterForest - 4; dz <= CenterForest + 4; dz += 2)
                for (double dx = -CenterForest - 4; dx <= CenterForest + 4; dx += 2)
                    if (dx * dx + dz * dz <= (CenterForest + 4) * (CenterForest + 4))   // sampled densely, otherwise a point between two samples falls into another cell
                        region[RegionIndex(c + dx, c + dz)] = 0;
        }

        // soft weights (box filter twice, radius 8 cells = 256 m)
        weight = new float[5][];
        for (int b = 0; b < 5; b++)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = region[i] == b ? 1 : 0;
            w = BoxBlur(w, nb, 8);
            weight[b] = BoxBlur(w, nb, 8);
        }
    }

    /// equalizes the areas to the shares: cells at the border to a too small biome change over from a too large biome
    /// (in rounds, the noisiest costs first so that the borders stay irregular).
    /// cause of the deviation: growth stops at the share, enclosed biomes stay too small, the open ones take the rest.
    void Rebalance(byte[] reg, long[] quota, double[] noise)
    {
        int n = reg.Length;
        var count = new long[5];
        foreach (byte r in reg) count[r]++;
        long tol = Math.Max(1, n / 500);   // 0.2 % of the map
        for (int round = 0; round < 600; round++)
        {
            var deficit = new long[5];
            long worst = 0;
            for (int b = 0; b < 5; b++) { deficit[b] = quota[b] - count[b]; worst = Math.Max(worst, Math.Abs(deficit[b])); }
            if (worst <= tol) return;
            var cand = new List<(double key, int idx, int to)>();
            for (int iz = 0; iz < nb; iz++)
                for (int ix = 0; ix < nb; ix++)
                {
                    int i = iz * nb + ix, o = reg[i];
                    if (deficit[o] >= worst) continue;
                    int best = -1;
                    for (int k = 0; k < 4; k++)
                    {
                        int x = ix + (k == 0 ? 1 : k == 1 ? -1 : 0), z = iz + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (x < 0 || z < 0 || x >= nb || z >= nb) continue;
                        int t = reg[z * nb + x];
                        if (t != o && deficit[t] - deficit[o] >= 2 && (best < 0 || deficit[t] > deficit[best])) best = t;
                    }
                    if (best >= 0) cand.Add((noise[i] + rnd.NextDouble() * 4, i, best));
                }
            if (cand.Count == 0) return;
            cand.Sort((a, b) => a.key.CompareTo(b.key));
            int take = Math.Max(1, cand.Count / 6);
            int moved = 0;
            foreach (var c in cand)
            {
                if (moved >= take) break;
                int o = reg[c.idx];
                if (deficit[c.to] - deficit[o] < 2) continue;
                reg[c.idx] = (byte)c.to;
                count[o]--; count[c.to]++; deficit[o]++; deficit[c.to]--;
                moved++;
            }
            if (moved == 0) return;
        }
    }

    void Grow(PriorityQueue<(int idx, int b), double> pq, double[] dist, sbyte[] owner, double[] stepCost, long[] quota, long[] count,
        List<(int, int, double)> _)
    {
        Span<int> ox = stackalloc int[] { 1, -1, 0, 0 }, oz = stackalloc int[] { 0, 0, 1, -1 };
        while (pq.TryDequeue(out var item, out double d))
        {
            int idx = item.idx, b = item.b;
            if (owner[idx] >= 0) continue;
            if (count[b] >= quota[b]) continue;
            owner[idx] = (sbyte)b;
            count[b]++;
            int ix = idx % nb, iz = idx / nb;
            for (int k = 0; k < 4; k++)
            {
                int x = ix + ox[k], z = iz + oz[k];
                if (x < 0 || z < 0 || x >= nb || z >= nb) continue;
                int j = z * nb + x;
                if (owner[j] >= 0) continue;
                double nd = d + stepCost[j];
                pq.Enqueue((j, b), nd);
            }
        }
    }

    static float[] BoxBlur(float[] src, int n, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, n, z =>
        {
            for (int x = 0; x < n; x++)
            {
                float s = 0;
                for (int k = -r; k <= r; k++) s += src[z * n + Math.Clamp(x + k, 0, n - 1)];
                tmp[z * n + x] = s / (2 * r + 1);
            }
        });
        Parallel.For(0, n, z =>
        {
            for (int x = 0; x < n; x++)
            {
                float s = 0;
                for (int k = -r; k <= r; k++) s += tmp[Math.Clamp(z + k, 0, n - 1) * n + x];
                dst[z * n + x] = s / (2 * r + 1);
            }
        });
        return dst;
    }

    /// biome at the place (gx, gz = meters from the southwest corner); borders swirled via displacement
    public int BiomeAt(double gx, double gz) => region[RegionIndex(gx, gz)];

    /// radius around the map center in which it is always pine forest (meters)
    const double CenterForest = 150;

    /// cell of the biome regions that BiomeAt looks at for the place (gx, gz) (borders swirled via displacement)
    int RegionIndex(double gx, double gz)
    {
        double wx = gx + 320 * warpX.Fbm(gx / 900, gz / 900, 4) + 25 * warpX.Noise(gx / 60 + 17.3, gz / 60);
        double wz = gz + 320 * warpZ.Fbm(gx / 900, gz / 900, 4) + 25 * warpZ.Noise(gx / 60, gz / 60 + 5.1);
        int ix = Math.Clamp((int)(wx / BiomeCell), 0, nb - 1), iz = Math.Clamp((int)(wz / BiomeCell), 0, nb - 1);
        return iz * nb + ix;
    }

    void Weights(double gx, double gz, Span<double> w)
    {
        // same swirling as BiomeAt so that mountains match the visible biome borders (only the coarse one, the fine one is blurred away anyway)
        double ox = 320 * warpX.Fbm(gx / 900, gz / 900, 4), oz = 320 * warpZ.Fbm(gx / 900, gz / 900, 4);
        gx += ox; gz += oz;
        double fx = gx / BiomeCell - 0.5, fz = gz / BiomeCell - 0.5;
        int x0 = (int)Math.Floor(fx), z0 = (int)Math.Floor(fz);
        double tx = fx - x0, tz = fz - z0;
        int xa = Math.Clamp(x0, 0, nb - 1), xb = Math.Clamp(x0 + 1, 0, nb - 1), za = Math.Clamp(z0, 0, nb - 1), zb = Math.Clamp(z0 + 1, 0, nb - 1);
        for (int b = 0; b < 5; b++)
        {
            var a = weight[b];
            w[b] = (a[za * nb + xa] * (1 - tx) + a[za * nb + xb] * tx) * (1 - tz) + (a[zb * nb + xa] * (1 - tx) + a[zb * nb + xb] * tx) * tz;
        }
    }

    // ---------------------------------------------------------------- terrain shape

    /// height of the landscape before erosion and lakes (meters) at the place (gx, gz)
    public double RawHeight(double gx, double gz, out double roughness)
    {
        Span<double> w = stackalloc double[5];
        Weights(gx, gz, w);
        double baseOff = 0, hillAmp = 0, rollAmp = 0, mountAmp = 0, cover = 0, platAmp = 0, valleyAmp = 0;
        roughness = 0;
        for (int b = 0; b < 5; b++)
        {
            baseOff += w[b] * BaseOff[b]; hillAmp += w[b] * HillAmp[b]; rollAmp += w[b] * RollAmp[b]; mountAmp += w[b] * MountAmp[b];
            cover += w[b] * MountCover[b]; platAmp += w[b] * PlateauAmp[b]; roughness += w[b] * RoughAmp[b]; valleyAmp += w[b] * ValleyAmp[b];
        }
        double h = GroundLevel + baseOff + 10 * cont.Fbm(gx / 2600, gz / 2600, 3);
        h += hillAmp * spec.Hills * (0.5 + 0.5 * Math.Clamp(hills.Fbm(gx / 520, gz / 520, 4) * 1.8, -1, 1));
        // smaller waves (about 250 m) everywhere so that there are hardly any large smooth areas
        h += rollAmp * Math.Min(spec.Hills, 1.5) * Math.Clamp(rolls.Fbm(gx / 260, gz / 260, 3) * 1.8, -1, 1);
        double m = MountMask(gx, gz, cover);
        double mountPart = 0;
        if (m > 0 && mountAmp > 0.5)
        {
            // mountain regions: soft mask (share ≈ cover), in it ridges from "ridged" noise
            double r = Math.Min(1, ridge.Ridged(gx / 720, gz / 720, 6) * 1.25);
            double vary = 0.75 + 0.25 * peakVar.Fbm(gx / 1100, gz / 1100, 2) * 2;
            // with game stamps the noise carries only 70 % (the rest comes from the stamps)
            mountPart = mountAmp * spec.Mountains * m * vary * (0.15 + 0.85 * Math.Pow(r, 1.4)) * (stampBuckets != null ? 0.7 : 1);
        }
        // height stamps of the game (Data\Stamps), like the mountain brush: bring gullies, ridges and rock steps
        var (stampMount, stampHill) = StampSum(gx, gz);
        mountPart += stampMount;
        h += mountPart + stampHill;
        // craggedness like the mountain range: ridges and rock steps (45/18/8 m), distributed in patches, stronger the higher the mountain
        if (spec.Rough > 0 && mountPart + stampHill > 1)
        {
            double rel = Math.Min(mountPart + 0.6 * stampHill, 130);
            double patch = 0.3 + 0.7 * SmoothStep(-0.35, 0.35, crag.Noise(gx / 260 + 7.7, gz / 260));
            double c1 = crag.Ridged(gx / 45, gz / 45, 3), c2 = crag.Ridged(gx / 18 + 5.3, gz / 18, 2), c3 = crag.Noise(gx / 8 + 1.1, gz / 8);
            h += spec.Rough * patch * rel * (0.11 * (c1 - 0.42) + 0.05 * (c2 - 0.42) + 0.014 * c3);
        }
        if (valleyAmp > 0.5 && spec.Valleys > 0)
        {
            // valleys: narrow, winding lines (zero crossings of a noise), deeper in mountains
            double vx = gx + 160 * valley.Noise(gx / 450 + 5.5, gz / 450), vz = gz + 160 * valley.Noise(gx / 450, gz / 450 + 21.2);
            double line = 1 - Math.Min(1, Math.Abs(valley.Fbm(vx / 1500, vz / 1500, 2) * 2.4));
            double k = Math.Min(spec.Valleys, 3);
            double cut = Math.Pow(line, 4);
            if (k > 1)
            {
                // from 100 % a second, finer network of side valleys
                double line2 = 1 - Math.Min(1, Math.Abs(valley.Fbm(vx / 750 + 77.7, vz / 750, 2) * 2.4));
                cut = Math.Max(cut, (k - 1) * 0.5 * Math.Pow(line2, 6));
            }
            // valley = terrain sinks to the valley floor (just below the base height): deep gorges in the mountains, flat hollows in the lowlands
            // valley floor wavy, walls eroded to different extents (otherwise gorges look smoothly milled)
            double floor = GroundLevel + baseOff - valleyAmp * 0.35 * k + 7 * valley.Noise(gx / 170 + 9.1, gz / 170);
            double strength = Math.Min(1, cut * (0.55 + 0.45 * k) * (0.82 + 0.3 * valley.Noise(gx / 70 + 3.3, gz / 70 + 1.7)));
            double beforeCut = h;
            if (h > floor) h += (floor - h) * strength;
            else h -= valleyAmp * 0.15 * strength;
            // valley flanks not smooth: rock bands and ridges depending on the cut depth
            double cutDepth = beforeCut - h;
            if (spec.Rough > 0 && cutDepth > 2)
                h += spec.Rough * Math.Min(cutDepth, 90) * (0.07 * (crag.Ridged(gx / 38 + 9.1, gz / 38, 3) - 0.42) + 0.02 * crag.Noise(gx / 12, gz / 12 + 3.3));
        }
        if (platAmp > 0.5 && spec.Mountains > 0)
            h += platAmp * spec.Mountains * PlateauShape(gx, gz);
        // keep peaks softly below the limit of the game (heights up to 255 m)
        if (h > 200) h = 200 + 45 * (1 - Math.Exp(-(h - 200) / 45));
        return Math.Max(h, 4);
    }

    /// mesas of the desert (0 … about 1.3): two steps with a steep edge. Edges frayed (finer noise) and at each place
    /// different steepness, height of the steps varies. 2026-11-10 (the user's request): shape like version 0.73 (without the steep walls
    /// and ridges from 0.74/0.75), instead about 10 % higher (PlateauAmp 60) and more variety: in about a quarter of the areas
    /// the plateaus are clearly lower (up to 55 %).
    double PlateauShape(double gx, double gz)
    {
        double v = plat.Fbm(gx / 850, gz / 850, 4) * 2.2 + 0.14 * plat.Noise(gx / 120 + 41.7, gz / 120) + 0.05 * plat.Noise(gx / 35, gz / 35 + 9.2);
        double edge = 0.04 + 0.14 * (0.5 + 0.5 * Math.Clamp(plat.Noise(gx / 500 + 3.3, gz / 500 + 8.8) * 1.6, -1, 1));
        double tall = 1 + 0.25 * plat.Noise(gx / 700 + 13.1, gz / 700);
        double low = 1 - 0.45 * SmoothStep(0.05, 0.3, plat.Noise(gx / 1300 + 21.7, gz / 1300 + 4.2));
        double t1 = Threshold(Math.Min(0.6, 0.3 * Math.Sqrt(spec.Mountains)));
        return tall * low * (0.55 * SmoothStep(t1, t1 + edge, v) + 0.45 * SmoothStep(t1 + 0.35, t1 + 0.35 + edge, v));
    }

    /// mountain mask 0 … 1 at the place (cover = weighted share of the biomes; &lt; 0 = get it from the weights yourself)
    double MountMask(double gx, double gz, double cover = -1)
    {
        if (spec.Mountains <= 0) return 0;
        if (cover < 0)
        {
            Span<double> w = stackalloc double[5];
            Weights(gx, gz, w);
            cover = 0;
            for (int b = 0; b < 5; b++) cover += w[b] * MountCover[b];
        }
        if (cover <= 0.001) return 0;
        double cov = Math.Min(0.9, cover * Math.Sqrt(spec.Mountains));
        double v = mmask.Fbm(gx / 1700, gz / 1700, 3) * 2.2;
        double th = Threshold(cov);
        return SmoothStep(th - 0.2, th + 0.3, v);
    }

    // ---------------------------------------------------------------- height stamps of the game

    sealed class StampPlace
    {
        public float[] Data;
        public double X, Z, Radius, Cos, Sin, Height, Steep;
        public bool Mirror, Hill;
    }

    const double StampBucket = 256;
    List<StampPlace>[] stampBuckets;
    int stampBucketsN;
    static readonly Dictionary<string, float[]> stampCache = new();

    static float[] CachedStamp(string game, MountainKind kind, int variant)
    {
        string key = game + "|" + kind + "|" + variant;
        lock (stampCache)
        {
            if (!stampCache.TryGetValue(key, out var s))
            {
                try { s = MountainSpec.LoadStamp(game, kind, variant); } catch (Exception) { s = null; }
                stampCache[key] = s;
            }
            return s;
        }
    }

    /// distribute stamps (like the game: mountains in snow, burnt and pine forest, plateaus in the desert, hills everywhere);
    /// mountain stamps preferably in the mountain mask. Without a game folder there are no stamps (then noise only).
    void PlaceStamps()
    {
        stampBuckets = null;
        string game = spec.GameFolder;
        if (game == null || (spec.Mountains <= 0 && spec.Hills <= 0)) return;
        var stamps = new Dictionary<MountainKind, float[][]>();
        foreach (var k in new[] { MountainKind.Mountain, MountainKind.Plateau, MountainKind.Hill })
            stamps[k] = new[] { CachedStamp(game, k, 0), CachedStamp(game, k, 1) }.Where(s => s != null).ToArray();
        if (stamps.Values.All(a => a.Length == 0)) return;
        // per biome: (kind, count per km², height in m at 100 %)
        // per biome: (kind, count per km², height in m at 100 %, steepness from … to: power of the shape, > 1 = sharper)
        // 2026-11-09 (the user's request): desert with single sharp mountains, wasteland hillier, steepness more widely spread
        var table = new (MountainKind Kind, double PerKm2, double Height, double SteepMin, double SteepMax)[][]
        {
            new[] { (MountainKind.Mountain, 0.55, 60.0, 0.8, 1.5), (MountainKind.Hill, 1.2, 22.0, 0.8, 1.4) },
            new[] { (MountainKind.Mountain, 0.8, 85.0, 0.8, 1.6), (MountainKind.Hill, 0.8, 20.0, 0.8, 1.4) },
            new[] { (MountainKind.Plateau, 0.9, 55.0, 0.85, 1.4), (MountainKind.Mountain, 0.3, 75.0, 1.4, 2.6), (MountainKind.Hill, 0.9, 18.0, 0.8, 1.9) },
            new[] { (MountainKind.Mountain, 1.1, 100.0, 0.8, 1.6), (MountainKind.Hill, 0.5, 22.0, 0.8, 1.4) },
            new[] { (MountainKind.Hill, 1.5, 20.0, 0.7, 1.7), (MountainKind.Mountain, 0.12, 50.0, 0.9, 1.6) },
        };
        int total = Math.Max(1, spec.Shares.Sum());
        double areaKm2 = (double)size * size / 1e6;
        var r = new Random(spec.SeedValue + 31);
        var list = new List<StampPlace>();
        for (int b = 0; b < 5; b++)
        {
            foreach (var (kind, perKm2, height, steepMin, steepMax) in table[b])
            {
                bool hill = kind == MountainKind.Hill;
                double amount = hill ? spec.Hills : spec.Mountains;
                if (amount <= 0 || stamps[kind].Length == 0) continue;
                int n = (int)Math.Round(spec.Shares[b] / (double)total * areaKm2 * perKm2 * Math.Sqrt(amount));
                for (int i = 0; i < n; i++)
                {
                    for (int attempt = 0; attempt < 40; attempt++)
                    {
                        double x = r.NextDouble() * size, z = r.NextDouble() * size;
                        if (BiomeAt(x, z) != b) continue;
                        // mountains and plateaus mainly where the mountain mask is (a few also outside)
                        if (!hill && MountMask(x, z) < 0.25 && r.NextDouble() > (b == 2 || b == 4 ? 0.6 : 0.2)) continue;   // desert/wasteland: single mountains also outside
                        double diameter = kind switch { MountainKind.Mountain => 450 + r.NextDouble() * 700, MountainKind.Plateau => 400 + r.NextDouble() * 550, _ => 280 + r.NextDouble() * 450 };
                        double a = r.NextDouble() * Math.PI * 2;
                        list.Add(new StampPlace
                        {
                            Data = stamps[kind][r.Next(stamps[kind].Length)],
                            X = x, Z = z, Radius = diameter / 2, Cos = Math.Cos(a), Sin = Math.Sin(a), Mirror = r.Next(2) == 0,
                            Height = height * amount * (kind == MountainKind.Plateau ? 0.35 + 0.65 * r.NextDouble() : 0.5 + 0.5 * r.NextDouble()),   // plateaus: also clearly lower ones
                            Steep = steepMin + (steepMax - steepMin) * r.NextDouble(), Hill = hill,
                        });
                        break;
                    }
                }
            }
        }
        stampBucketsN = (int)Math.Ceiling(size / StampBucket);
        stampBuckets = new List<StampPlace>[stampBucketsN * stampBucketsN];
        foreach (var s in list)
        {
            int x0 = Math.Max(0, (int)((s.X - s.Radius) / StampBucket)), x1 = Math.Min(stampBucketsN - 1, (int)((s.X + s.Radius) / StampBucket));
            int z0 = Math.Max(0, (int)((s.Z - s.Radius) / StampBucket)), z1 = Math.Min(stampBucketsN - 1, (int)((s.Z + s.Radius) / StampBucket));
            for (int bz = z0; bz <= z1; bz++)
                for (int bx = x0; bx <= x1; bx++)
                    (stampBuckets[bz * stampBucketsN + bx] ??= new()).Add(s);
        }
        StampCount = list.Count;
    }

    public int StampCount { get; private set; }

    (double Mount, double Hill) StampSum(double gx, double gz)
    {
        if (stampBuckets == null) return (0, 0);
        int bx = Math.Clamp((int)(gx / StampBucket), 0, stampBucketsN - 1), bz = Math.Clamp((int)(gz / StampBucket), 0, stampBucketsN - 1);
        var list = stampBuckets[bz * stampBucketsN + bx];
        if (list == null) return (0, 0);
        double mount = 0, hill = 0;
        const int S = MountainSpec.StampSize;
        foreach (var s in list)
        {
            double dx = gx - s.X, dz = gz - s.Z;
            double rr = Math.Sqrt(dx * dx + dz * dz) / s.Radius;
            if (rr >= 1) continue;
            double u = dx * s.Cos - dz * s.Sin, v = dx * s.Sin + dz * s.Cos;
            if (s.Mirror) u = -u;
            double fx = S / 2.0 + u / (2 * s.Radius) * S, fy = S / 2.0 + v / (2 * s.Radius) * S;
            if (fx < 0 || fy < 0 || fx >= S - 1 || fy >= S - 1) continue;
            int ix = (int)fx, iy = (int)fy;
            double tx = fx - ix, ty = fy - iy;
            int i = iy * S + ix;
            var d = s.Data;
            double val = (d[i] * (1 - tx) + d[i + 1] * tx) * (1 - ty) + (d[i + S] * (1 - tx) + d[i + S + 1] * tx) * ty;
            if (val <= 0) continue;
            double edge = rr <= 0.75 ? 1 : 1 - SmoothStep(0.75, 1, rr);
            double add = s.Height * Math.Pow(val, s.Steep) * edge;
            if (s.Hill) hill += add; else mount += add;
        }
        return (mount, hill);
    }

    /// threshold for a noise (Fbm × 2.2, roughly normally distributed with spread 0.45) above which the share cov lies
    static double Threshold(double cov)
    {
        cov = Math.Clamp(cov, 0.01, 0.99);
        // approximation of the inverse of the normal distribution (Abramowitz/Stegun)
        double p = 1 - cov, q = p < 0.5 ? p : 1 - p;
        double t = Math.Sqrt(-2 * Math.Log(q));
        double z = t - (2.515517 + 0.802853 * t + 0.010328 * t * t) / (1 + 1.432788 * t + 0.189269 * t * t + 0.001308 * t * t * t);
        if (p < 0.5) z = -z;
        return z * 0.45;
    }

    static double SmoothStep(double a, double b, double v)
    {
        double t = Math.Clamp((v - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    float[] roughCoarse;

    /// calculate the coarse grid (cell meters per cell): biomes, shape, erosion, lakes. For the preview with a large cell.
    public void BuildCoarse(double cell, IProgress<string> progress = null)
    {
        Cell = cell;
        N = (int)Math.Round(size / cell);
        progress?.Report(Loc.T("Growing biomes …"));
        GrowBiomes();
        PlaceStamps();
        progress?.Report(Loc.T("Shaping hills and mountains …"));
        coarse = new float[N * N];
        roughCoarse = new float[N * N];
        hard = new byte[N * N];
        Parallel.For(0, N, j =>
        {
            for (int i = 0; i < N; i++)
            {
                double h = RawHeight((i + 0.5) * cell, (j + 0.5) * cell, out double r);
                coarse[j * N + i] = (float)h;
                roughCoarse[j * N + i] = (float)r;
                hard[j * N + i] = Hardness((i + 0.5) * cell, (j + 0.5) * cell);
            }
        });
        progress?.Report(Loc.T("Letting slopes settle (erosion) …"));
        Erode(cell <= 8 ? 40 : 6);
        progress?.Report(Loc.T("Unique formations …"));
        PlaceAnomalies();
        if (cell <= 4 && spec.Erosion > 0)
        {
            // rain: drops dig gullies into the slopes and deposit at the bottom (only in the finished world, the preview is too coarse)
            progress?.Report(Loc.T("Rain erosion (gullies on the slopes) …"));
            var before = (float[])coarse.Clone();
            HydraulicErode((int)(N * (long)N / 5 * spec.Erosion), spec.SeedValue + 55);
            // safeguard: erosion changes the ground by at most −15 … +8 m (otherwise rare deep holes where many drops converge)
            Parallel.For(0, N * N, i => coarse[i] = Math.Clamp(coarse[i], before[i] - 15, before[i] + 8));
            before = null;
        }
        progress?.Report(Loc.T("Craters and cracks …"));
        PlaceCratersAndCracks();
        Erode(4);
        progress?.Report(Loc.T("Placing lakes …"));
        PlaceLakes();
    }

    /// landslide: where two neighbors stand steeper than about 38° to each other, material slides down (mass-conserving)
    void Erode(int iterations)
    {
        // angle of repose per rock type (hard): 0 normal 38°, 1 hard 72°, 2 firm 52°, 3 soft 30°
        var tal = new[] { 38.0, 72, 52, 30 }.Select(deg => (float)(Math.Tan(deg * Math.PI / 180) * Cell)).ToArray();
        var delta = new float[N * N];
        for (int it = 0; it < iterations; it++)
        {
            Parallel.For(0, N, j =>
            {
                for (int i = 0; i < N; i++)
                {
                    int idx = j * N + i;
                    float h = coarse[idx], d = 0;
                    // hard rock (steep walls, rock needles, cracks): may stand much steeper; counts if one of the two cells is hard
                    float ti = tal[hard[idx]];
                    if (i > 0) d += Flow(coarse[idx - 1], h, Math.Max(ti, tal[hard[idx - 1]]));
                    if (i < N - 1) d += Flow(coarse[idx + 1], h, Math.Max(ti, tal[hard[idx + 1]]));
                    if (j > 0) d += Flow(coarse[idx - N], h, Math.Max(ti, tal[hard[idx - N]]));
                    if (j < N - 1) d += Flow(coarse[idx + N], h, Math.Max(ti, tal[hard[idx + N]]));
                    delta[idx] = d;
                }
            });
            Parallel.For(0, N * N, idx => coarse[idx] += delta[idx]);
        }
    }

    /// drop erosion (after the known method "hydraulic erosion" with drops): every drop runs downhill, picks up
    /// material depending on gradient, speed and amount of water and deposits it when it becomes slower. Many drops = gullies,
    /// ridges and alluvial fans. The drops run in parallel; simultaneous access to the same cell is rare and harmless.
    void HydraulicErode(int attempts, int seed)
    {
        // values after experiments (2026-11-07, in DropletsInTile): longer, branching gullies instead of short notches
        // brush for removing: radius 2 cells, weight falls off outwards
        var brush = new List<(int dx, int dy, float w)>();
        float sum = 0;
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= 3.2) continue;
                float w = (float)(3.2 - dist);
                brush.Add((dx, dy, w)); sum += w;
            }
        var br = brush.Select(b => (b.dx, b.dy, w: b.w / sum)).ToArray();
        float[] H = coarse;
        int n = N;
        double cell = Cell;
        // same start value = same world: tiles (128 cells) in a checkerboard of 4 colors; only tiles of one color run at the same time,
        // every drop stays in its tile ± half a tile (then areas running at the same time never touch), own randomness per tile.
        // two passes with a grid offset by half a tile so that no seams remain at the tile borders.
        const int T = 128;
        int tilesN = n / T + 2;
        for (int pass = 0; pass < 2; pass++)
            for (int color = 0; color < 4; color++)
            {
                int off = pass * T / 2;
                var tiles = new List<(int tx, int ty)>();
                for (int ty = 0; ty < tilesN; ty++)
                    for (int tx = 0; tx < tilesN; tx++)
                        if (((tx & 1) | ((ty & 1) << 1)) == color) tiles.Add((tx, ty));
                int p = pass, c = color;
                Parallel.ForEach(tiles, tile => DropletsInTile(tile.tx * T - off, tile.ty * T - off, T, p, c, attempts / 2, seed, H, n, cell, br));
            }
    }

    void DropletsInTile(int x0, int y0, int T, int pass, int color, int attemptsAll, int seed, float[] H, int n, double cell, (int dx, int dy, float w)[] br)
    {
        const double inertia = 0.3, capFactor = 3, minCap = 0.01, erodeSpeed = 0.12, depositSpeed = 0.2, evaporate = 0.012;
        const int maxSteps = 90;
        int lx = Math.Max(3, x0), hx = Math.Min(n - 4, x0 + T), ly = Math.Max(3, y0), hy = Math.Min(n - 4, y0 + T);
        if (hx <= lx || hy <= ly) return;
        // allowed area of the drop (half a tile margin, minus brush)
        double ax = Math.Max(3, x0 - T / 2 + 4), bx2 = Math.Min(n - 4, x0 + T + T / 2 - 4), ay = Math.Max(3, y0 - T / 2 + 4), by2 = Math.Min(n - 4, y0 + T + T / 2 - 4);
        var r = new Random(unchecked(seed + pass * 1000003 + (x0 + 2 * T) * 7919 + (y0 + 2 * T) * 104729));
        int per = (int)((long)attemptsAll * (hx - lx) * (hy - ly) / ((long)n * n));
        {
            for (int a = 0; a < per; a++)
            {
                double x = lx + r.NextDouble() * (hx - lx), y = ly + r.NextDouble() * (hy - ly);
                var (h0, gx0, gy0) = Gradient(H, n, x, y);
                // drops mainly where there is a gradient (nothing happens in the plain anyway)
                if (Math.Sqrt(gx0 * gx0 + gy0 * gy0) / cell < 0.08 && r.NextDouble() > 0.03) continue;
                double dx = 0, dy = 0, speed = 1, water = 1, sediment = 0;
                for (int step = 0; step < maxSteps; step++)
                {
                    int ix = (int)x, iy = (int)y;
                    double fx = x - ix, fy = y - iy;
                    var (h, gx, gy) = Gradient(H, n, x, y);
                    dx = dx * inertia - gx * (1 - inertia);
                    dy = dy * inertia - gy * (1 - inertia);
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-9) break;
                    dx /= len; dy /= len;
                    x += dx; y += dy;
                    if (x < ax || y < ay || x >= bx2 || y >= by2) break;
                    double dh = Gradient(H, n, x, y).h - h;
                    double capacity = Math.Max(-dh * speed * water * capFactor, minCap);
                    if (sediment > capacity || dh > 0)
                    {
                        double dep = dh > 0 ? Math.Min(dh, sediment) : (sediment - capacity) * depositSpeed;
                        sediment -= dep;
                        int i = iy * n + ix;
                        H[i] += (float)(dep * (1 - fx) * (1 - fy));
                        H[i + 1] += (float)(dep * fx * (1 - fy));
                        H[i + n] += (float)(dep * (1 - fx) * fy);
                        H[i + n + 1] += (float)(dep * fx * fy);
                    }
                    else
                    {
                        // fade out at the map edge (last 60 cells): there drops leave the map and would draw trenches
                        double edgeFade = Math.Min(1, Math.Min(Math.Min(ix, iy), Math.Min(n - 1 - ix, n - 1 - iy)) / 60.0);
                        double er = Math.Min((capacity - sediment) * erodeSpeed, -dh) * edgeFade;
                        foreach (var (bx, by, w) in br) H[(iy + by) * n + ix + bx] -= (float)(er * w);
                        sediment += er;
                    }
                    // speed from the gradient (measured per cell, in meters per meter)
                    speed = Math.Min(6, Math.Sqrt(Math.Max(0.01, speed * speed - dh / cell * 4)));
                    water *= 1 - evaporate;
                }
            }
        }
    }

    static (double h, double gx, double gy) Gradient(float[] H, int n, double x, double y)
    {
        int ix = (int)x, iy = (int)y;
        double fx = x - ix, fy = y - iy;
        int i = iy * n + ix;
        double a = H[i], b = H[i + 1], c = H[i + n], d = H[i + n + 1];
        double gx = (b - a) * (1 - fy) + (d - c) * fy;
        double gy = (c - a) * (1 - fx) + (d - b) * fx;
        double h = a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) + c * (1 - fx) * fy + d * fx * fy;
        return (h, gx, gy);
    }

    static float Flow(float neighbor, float h, float talus)
    {
        float diff = neighbor - h;
        if (diff > talus) return (diff - talus) * 0.12f;
        if (diff < -talus) return (diff + talus) * 0.12f;
        return 0;
    }

    double SampleCoarse(double gx, double gz)
    {
        double fx = gx / Cell - 0.5, fz = gz / Cell - 0.5;
        int x0 = (int)Math.Floor(fx), z0 = (int)Math.Floor(fz);
        double tx = fx - x0, tz = fz - z0;
        int xa = Math.Clamp(x0, 0, N - 1), xb = Math.Clamp(x0 + 1, 0, N - 1), za = Math.Clamp(z0, 0, N - 1), zb = Math.Clamp(z0 + 1, 0, N - 1);
        return (coarse[za * N + xa] * (1 - tx) + coarse[za * N + xb] * tx) * (1 - tz) + (coarse[zb * N + xa] * (1 - tx) + coarse[zb * N + xb] * tx) * tz;
    }

    // ---------------------------------------------------------------- lakes

    /// lakes in flat areas: irregular outline, level 1 m below the lowest point at the shore, basin down to "depth".
    /// The selection (position, size) depends only on chance and the terrain, not on the grid – preview and world have the same lakes.
    void PlaceLakes()
    {
        LakeList.Clear();
        lakeOf = new int[N * N];
        calm = new float[N * N];
        Array.Fill(calm, 1f);
        if (spec.Lakes <= 0) return;
        double perKm2 = spec.Lakes switch { 1 => 1 / 18.0, 2 => 1 / 8.0, _ => 1 / 4.0 };
        int want = Math.Max(spec.Lakes, (int)Math.Round((double)size * size / 1e6 * perKm2));   // also small worlds: 1/2/3 lakes
        var lr = new Random(spec.SeedValue + 77);
        double margin = Math.Min(500, size * 0.1);
        for (int attempt = 0; attempt < want * 60 && LakeList.Count < want; attempt++)
        {
            double cx = margin + lr.NextDouble() * (size - 2 * margin), cz = margin + lr.NextDouble() * (size - 2 * margin);
            // size widely spread: many small, some large (2026-11-07, the user's request: less uniform)
            double radius = 30 + Math.Pow(lr.NextDouble(), 1.6) * (spec.Lakes >= 3 ? 190 : 150);
            double depth = 2.5 + lr.NextDouble() * 7;
            var shape = LakeShape.Random(lr, radius);
            // lakes belong in hollows: walk downhill from the random point (also mountain lakes in depressions between the peaks)
            (cx, cz) = Downhill(cx, cz, margin);
            if (LakeList.Any(l => Math.Sqrt((l.X - cx) * (l.X - cx) + (l.Z - cz) * (l.Z - cz)) < l.Radius * 1.5 + radius * 1.5 + 350)) continue;
            var (min, max) = RimRange(cx, cz, shape);
            if (max - min > 12)
            {
                // steep surroundings (mountains): small mountain lake
                shape = shape.Scaled(0.55);
                (min, max) = RimRange(cx, cz, shape);
            }
            if (max - min > 40) continue;
            int level = (int)Math.Floor(min) - 1;
            if (level < 3 || level > 235) continue;
            LakeList.Add(new LakeInfo(cx, cz, shape.Radius, level, depth));
            Carve(LakeList.Count, cx, cz, shape, level, depth, lr);
        }
    }

    (double Min, double Max) RimRange(double cx, double cz, LakeShape shape)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int k = 0; k < 40; k++)
        {
            double a = k * Math.PI * 2 / 40;
            double rr = shape.R(a);
            foreach (double off in new[] { 0.0, 10, 20, 30 })   // shoreline is frayed up to about 14 m
            {
                // from the finished terrain (after the erosion: gullies can lower the edge)
                double h = SampleCoarse(cx + Math.Cos(a) * (rr + off), cz + Math.Sin(a) * (rr + off));
                min = Math.Min(min, h); max = Math.Max(max, h);
            }
        }
        return (min, max);
    }

    /// in steps of 30 m downhill to a hollow (at most 40 steps)
    (double, double) Downhill(double x, double z, double margin)
    {
        double h = RawHeight(x, z, out _);
        for (int step = 0; step < 40; step++)
        {
            double bx = x, bz = z, bh = h;
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4, nx = x + 30 * Math.Cos(a), nz = z + 30 * Math.Sin(a);
                if (nx < margin || nz < margin || nx > size - margin || nz > size - margin) continue;
                double nh = RawHeight(nx, nz, out _);
                if (nh < bh - 0.05) { bx = nx; bz = nz; bh = nh; }
            }
            if (bh >= h) break;
            x = bx; z = bz; h = bh;
        }
        return (x, z);
    }

    /// outline of a lake: ellipse (stretched, rotated) with random bays (waves of 2nd–8th order)
    sealed class LakeShape
    {
        public double Radius, Stretch, Axis;
        public double[] Amp = new double[9], Phase = new double[9];

        public static LakeShape Random(Random r, double radius)
        {
            var s = new LakeShape { Radius = radius, Stretch = 1 + Math.Pow(r.NextDouble(), 1.5) * 1.3, Axis = r.NextDouble() * Math.PI };
            for (int k = 2; k <= 8; k++)
            {
                s.Amp[k] = r.NextDouble() * 0.42 / Math.Pow(k, 0.85);
                s.Phase[k] = r.NextDouble() * Math.PI * 2;
            }
            return s;
        }

        public LakeShape Scaled(double f)
        {
            var s = (LakeShape)MemberwiseClone();
            s.Radius *= f;
            return s;
        }

        public double R(double a)
        {
            // ellipse with semi-axes radius·√Stretch and radius/√Stretch (area like a circle with radius)
            double t = a - Axis, ra = Radius * Math.Sqrt(Stretch), rb = Radius / Math.Sqrt(Stretch);
            double c = Math.Cos(t) / ra, s = Math.Sin(t) / rb;
            double e = 1 / Math.Sqrt(c * c + s * s);
            double w = 1;
            for (int k = 2; k <= 8; k++) w += Amp[k] * Math.Sin(k * a + Phase[k]);
            return e * Math.Max(0.35, w);
        }
    }

    /// distance to the shoreline (inside +, outside −), outline with noise in three sizes; the same in the coarse and fine grid
    double LakeEdgeDistance(double cx, double cz, LakeShape shape, int seed, double gx, double gz)
    {
        double dx = gx - cx, dz = gz - cz, dist = Math.Sqrt(dx * dx + dz * dz);
        double d = shape.R(Math.Atan2(dz, dx)) - dist;
        return d + 9 * detail.Noise(gx / 45 + seed, gz / 45) + 4 * detail.Noise(gx / 16, gz / 16 + seed) + 1.5 * detail.Noise(gx / 6 + seed, gz / 6 + 3.3);
    }

    /// minimum slope of the shore bank between level − 0.3 m and level + 1.6 m (fraction, 0.30 = 30 %)
    const double BankSlope = 0.30;

    void Carve(int id, double cx, double cz, LakeShape shape, int level, double depth, Random lr)
    {
        double reach = shape.Radius * 2.6 + 90;
        int i0 = Math.Max(0, (int)((cx - reach) / Cell)), i1 = Math.Min(N - 1, (int)((cx + reach) / Cell));
        int j0 = Math.Max(0, (int)((cz - reach) / Cell)), j1 = Math.Min(N - 1, (int)((cz + reach) / Cell));
        // sometimes an island (only in larger lakes)
        bool island = shape.Radius > 70 && lr.NextDouble() < 0.4;
        double ia = lr.NextDouble() * Math.PI * 2, idist = lr.NextDouble() * 0.45;
        double ix = cx + Math.Cos(ia) * shape.R(ia) * idist, iz = cz + Math.Sin(ia) * shape.R(ia) * idist;
        double ir = 8 + lr.NextDouble() * shape.Radius * 0.22, ih = 1.5 + lr.NextDouble() * 4;
        int seed = lr.Next(1000);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                double gx = (i + 0.5) * Cell, gz = (j + 0.5) * Cell;
                double dx = gx - cx, dz = gz - cz;
                double a = Math.Atan2(dz, dx);
                double rr = shape.R(a);
                // > 0 = inside; shoreline frayed (2026-11-08, the user's request: shore too artificial): noise in three sizes
                double d = LakeEdgeDistance(cx, cz, shape, seed, gx, gz);
                // shore kind along the shore: steep bank (−) … beach with shallow water (+)
                double kind = SmoothStep(-0.35, 0.35, AngNoise(a, 1.7, seed) + 0.25 * detail.Noise(gx / 120, gz / 120 + seed));
                int idx = j * N + i;
                float h = coarse[idx];
                // shore slope on high sides (mountain lakes): terrain at most as high as a slope with 30–45° from the shore,
                // otherwise a vertical wall would stand there
                double grade = 0.6 + 0.4 * (0.5 + 0.5 * AngNoise(a, 2, seed + 11));
                double bank = level + 1.5 + grade * Math.Max(0, -d);
                if (h > bank && d > -70) { coarse[idx] = (float)bank; h = (float)bank; }
                if (d > 0)
                {
                    // shore of different widths (running out flat to steep), wider on high sides, uneven ground
                    double shore = Math.Max(5 + 40 * kind, 0.8 * (h - level));
                    double deep = depth * (0.65 + 0.7 * (0.5 + 0.5 * detail.Noise(gx / 45 + seed, gz / 45)));
                    // at the beach a shallow-water strip (0.4–0.8 m deep), only then deeper
                    double shelf = 25 * kind;
                    double bottom = d < shelf ? level - 0.4 - 0.4 * (d / Math.Max(shelf, 1)) : level - 0.8 - deep * SmoothStep(0, 1, Math.Min((d - shelf) / (0.5 * rr), 1));
                    double t = SmoothStep(0, 1, Math.Min(d / shore, 1));
                    double target = h + (bottom - h) * t;
                    // minimum slope (2026-11-11, the user's hint: flat shore gave jagged waterline, terraces and wet strip
                    // without water): from level + 1.6 m at the edge the slope falls with BankSlope to level − 0.3 m, then shallow water.
                    // So the waterline (level + WetMargin) lies on a slope and not on an almost level step.
                    target = Math.Min(target, Math.Max(level + 1.6 - BankSlope * d, level - 0.3));
                    if (island)
                    {
                        // island irregular (outline from noise, not circular)
                        double di = Math.Sqrt((gx - ix) * (gx - ix) + (gz - iz) * (gz - iz));
                        double irr = ir * 2.2 * (1 + 0.45 * AngNoise(Math.Atan2(gz - iz, gx - ix), 1.2, seed + 5) + 0.15 * AngNoise(Math.Atan2(gz - iz, gx - ix), 3, seed + 9));
                        if (di < irr) target = Math.Max(target, level - 3 + (ih + 3) * SmoothStep(0, 1, 1 - di / irr) * (0.75 + 0.5 * (0.5 + 0.5 * detail.Noise(gx / 9, gz / 9 + seed))));
                    }
                    if (target < h) coarse[idx] = (float)target;
                }
                else
                {
                    // shore safely above the level (otherwise the lake would run out), slightly rising instead of a flat ring
                    // only the immediate shore edge (otherwise a wall arises around the lake; gaps further out are sealed by FillWater)
                    double floorH = level + 1.6;   // above the waterline (level + 1 m) so that noise does not make water patches there
                    if (d > -6 && h < floorH) { coarse[idx] = (float)floorH; h = (float)floorH; }
                    // beach: land falls gently to the water (about 12 %), only lower
                    // beach narrow to wide and uneven (smooth strips looked artificial)
                    double beachW = (4 + 24 * kind) * (0.6 + 0.8 * (0.5 + 0.5 * detail.Noise(gx / 60 + seed, gz / 60 + 4.4)));
                    double beachH = level + 1.6 + 0.12 * (-d) + 0.5 * (0.5 + 0.5 * detail.Noise(gx / 13, gz / 13 + seed));
                    if (kind > 0.15 && -d < beachW && h > beachH) coarse[idx] = (float)(h + (beachH - h) * kind * (1 - SmoothStep(0, beachW, -d)));
                }
                if (d > -20 * Math.Max(1, Cell / 4)) lakeOf[idx] = id;
                // shore hardly calmer than the land (gaps in the shore are sealed by FillWater)
                // calm at the shore: noise otherwise shifts the waterline
                calm[idx] = Math.Min(calm[idx], (float)(d > 0 ? 0.3 + 0.45 * SmoothStep(2, 20, d) : 0.3 + 0.7 * SmoothStep(2, 34, -d)));
            }
    }

    // ---------------------------------------------------------------- preview

    /// preview image (BGRA, n × n, row 0 = north): biome color with shading, lakes blue
    public byte[] RenderPreview(out int n, bool heightColors = false)
    {
        n = N;
        var px = new byte[N * N * 4];
        Parallel.For(0, N, row =>
        {
            int j = N - 1 - row;
            for (int i = 0; i < N; i++)
            {
                int idx = j * N + i;
                float h = coarse[idx];
                double hx = coarse[j * N + Math.Min(i + 1, N - 1)] - coarse[j * N + Math.Max(i - 1, 0)];
                double hz = coarse[Math.Min(j + 1, N - 1) * N + i] - coarse[Math.Max(j - 1, 0) * N + i];
                // light from the northwest, exaggerated (in the coarse grid slopes otherwise look too flat)
                double shade = Math.Clamp(0.85 + (-hx + hz) / (2 * Cell) * 1.6, 0.3, 1.3);
                double r, g, bl;
                if (heightColors)
                {
                    // height steps: deep green → brown → gray → white
                    double t = Math.Clamp((h - 30) / 190.0, 0, 1);
                    (r, g, bl) = t < 0.15 ? (110, 160, 90) : t < 0.35 ? (160, 170, 105) : t < 0.6 ? (170, 140, 100) : t < 0.8 ? (150, 140, 135) : (235, 235, 240);
                }
                else
                {
                    var c = BiomePalette.Display[BiomeAt((i + 0.5) * Cell, (j + 0.5) * Cell)];
                    (r, g, bl) = (c[0], c[1], c[2]);
                }
                int lake = lakeOf[idx];
                if (lake > 0 && h < LakeList[lake - 1].Level + Lake.WetMargin) { r = 70; g = 120; bl = 200; shade = 1; }
                int o = (row * N + i) * 4;
                px[o] = (byte)Math.Clamp(bl * shade, 0, 255);
                px[o + 1] = (byte)Math.Clamp(g * shade, 0, 255);
                px[o + 2] = (byte)Math.Clamp(r * shade, 0, 255);
                px[o + 3] = 255;
            }
        });
        return px;
    }

    public (double Min, double Max) HeightRange()
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (float h in coarse) { if (h < min) min = h; if (h > max) max = h; }
        return (min, max);
    }

    // ---------------------------------------------------------------- write world

    public sealed record Result(string Folder, int Lakes, long WaterArea, int Spawns, double MinHeight, double MaxHeight, TimeSpan Time,
        List<(string Kind, int X, int Z)> Anomalies, int Craters, int Cracks);

    /// write the whole world into the (empty) folder
    public Result Write(string folder, string gameWorlds, IProgress<string> progress)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        BuildCoarse(size <= 8192 ? 2 : 2.5, progress);   // 2 m: gullies and rock steps of the stamps stay visible
        Directory.CreateDirectory(folder);

        progress.Report(Loc.T("Computing heights at full resolution …"));
        var heights = new ushort[(long)size * size];
        FullResolution(heights);
        coarse = null; roughCoarse = null;   // memory free for the next steps

        progress.Report(Loc.T("Filling lakes with water …"));
        var water = new byte[(long)size * size];
        long wet = FillWater(heights, water);

        progress.Report(Loc.T("Choosing spawn points …"));
        var spawns = ChooseSpawns(heights, water);

        progress.Report(Loc.F("Writing {0} …", "dtm.raw"));
        using (var fs = File.Create(Path.Combine(folder, "dtm.raw")))
        {
            const int chunk = 1 << 20;
            var bytes = new byte[chunk * 2];
            for (long start = 0; start < heights.LongLength; start += chunk)
            {
                int len = (int)Math.Min(chunk, heights.LongLength - start);
                Buffer.BlockCopy(heights, (int)(start * 2), bytes, 0, len * 2);
                fs.Write(bytes, 0, len * 2);
            }
        }
        double minH = double.MaxValue, maxH = double.MinValue;
        foreach (ushort v in heights) { if (v < minH) minH = v; if (v > maxH) maxH = v; }

        progress.Report(Loc.F("Writing {0} …", "splat4.png"));
        PngWriter.Write(Path.Combine(folder, "splat4.png"), size, size, (row, buf) =>
        {
            long src = (long)(size - 1 - row) * size;   // PNG row 0 = north
            Array.Clear(buf);
            for (int x = 0; x < size; x++) buf[x * 4 + 2] = water[src + x];   // RGBA: blue = level
        });
        water = null;
        progress.Report(Loc.F("Writing {0} …", "splat3.png"));
        PngWriter.Write(Path.Combine(folder, "splat3.png"), size, size, (row, buf) => Array.Clear(buf));

        progress.Report(Loc.F("Writing {0} …", "biomes.png"));
        int bn = size / 8;
        PngWriter.Write(Path.Combine(folder, "biomes.png"), bn, bn, (row, buf) =>
        {
            for (int x = 0; x < bn; x++)
            {
                var (r, g, b) = BiomePalette.SourceColor(BiomeAt((x + 0.5) * 8, size - (row + 0.5) * 8));
                buf[x * 4] = r; buf[x * 4 + 1] = g; buf[x * 4 + 2] = b; buf[x * 4 + 3] = 255;
            }
        });
        int rn = size / 32;
        PngWriter.Write(Path.Combine(folder, "radiation.png"), rn, rn, (row, buf) => Array.Clear(buf));

        progress.Report(Loc.T("Writing world description …"));
        var utf8Bom = new UTF8Encoding(true);
        File.WriteAllText(Path.Combine(folder, "prefabs.xml"), "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<prefabs>\r\n</prefabs>\r\n", utf8Bom);
        WorldData.WriteSpawnFile(Path.Combine(folder, WorldData.SpawnFile), spawns);
        WorldData.WriteSpawnFile(Path.Combine(folder, "spawnpoints_original.xml"), spawns);
        WorldData.WriteSpawnFile(Path.Combine(folder, WorldData.PlaySpawnFile), spawns);   // the same point for playing and testing
        WorldData.WriteSpawnFile(Path.Combine(folder, WorldData.TestSpawnFile), spawns);
        var (mainTtw, version) = TemplateFiles(gameWorlds);
        File.WriteAllBytes(Path.Combine(folder, "main.ttw"), mainTtw);
        File.WriteAllText(Path.Combine(folder, "map_info.xml"), MapInfo(version), utf8Bom);
        // also write the location of the anomalies so that they can be found in the game
        File.WriteAllText(Path.Combine(folder, "yamee_generator.txt"), spec.Describe() +
            string.Concat(Anomalies.Select(a => $"Anomaly={a.Kind} at {a.X},{a.Z}\r\n")) + $"Craters={CraterCount}\r\nCracks={CrackCount}\r\n", utf8Bom);
        return new Result(folder, LakeList.Count, wet, spawns.Count, minH / 256, maxH / 256, watch.Elapsed, Anomalies.ToList(), CraterCount, CrackCount);
    }

    /// coarse grid bicubic to 1 m, plus fine noise (bumps), of different strength per biome and slope, calm at lakes
    void FullResolution(ushort[] heights)
    {
        int n = size;
        // precalculate the weights of the columns once (same for all rows)
        var ix = new int[n * 4];
        var wx = new float[n * 4];
        for (int x = 0; x < n; x++) CubicTaps((x + 0.5) / Cell - 0.5, ix, wx, x * 4);
        // roughness per coarse cell (biome × slope × slider × calm at the lake)
        var amp = new float[N * N];
        Parallel.For(0, N, j =>
        {
            for (int i = 0; i < N; i++)
            {
                int idx = j * N + i;
                double hx = coarse[j * N + Math.Min(i + 1, N - 1)] - coarse[j * N + Math.Max(i - 1, 0)];
                double hz = coarse[Math.Min(j + 1, N - 1) * N + i] - coarse[Math.Max(j - 1, 0) * N + i];
                double slope = Math.Sqrt(hx * hx + hz * hz) / (2 * Cell);
                amp[idx] = (float)(roughCoarse[idx] * spec.Rough * (1 + 1.8 * Math.Min(slope, 1)) * calm[idx]);
            }
        });
        Parallel.For(0, n, z =>
        {
            Span<int> iz = stackalloc int[4];
            Span<float> wz = stackalloc float[4];
            CubicTaps((z + 0.5) / Cell - 0.5, iz, wz, 0);
            int za = Math.Clamp((int)Math.Floor((z + 0.5) / Cell - 0.5), 0, N - 1), zb = Math.Min(za + 1, N - 1);
            double tz = Math.Clamp((z + 0.5) / Cell - 0.5 - za, 0, 1);
            long row = (long)z * n;
            for (int x = 0; x < n; x++)
            {
                double h = 0;
                int o = x * 4;
                for (int a = 0; a < 4; a++)
                {
                    int r = iz[a] * N;
                    h += wz[a] * (wx[o] * coarse[r + ix[o]] + wx[o + 1] * coarse[r + ix[o + 1]] + wx[o + 2] * coarse[r + ix[o + 2]] + wx[o + 3] * coarse[r + ix[o + 3]]);
                }
                // roughness bilinear
                int xa = ix[o + 1], xb = ix[o + 2];
                double tx = Math.Clamp((x + 0.5) / Cell - 0.5 - Math.Floor((x + 0.5) / Cell - 0.5), 0, 1);
                double am = (amp[za * N + xa] * (1 - tx) + amp[za * N + xb] * tx) * (1 - tz) + (amp[zb * N + xa] * (1 - tx) + amp[zb * N + xb] * tx) * tz;
                if (am > 0.01)
                    h += am * (0.6 * detail.Noise(x / 23.0, z / 23.0) + 0.3 * detail.Noise(x / 9.0 + 31.7, z / 9.0) + 0.15 * detail.Noise(x / 4.0, z / 4.0 + 57.1)) * 1.6;
                heights[row + x] = (ushort)Math.Clamp(Math.Round(h * 256), 256 * 2, 65535);
            }
        });
    }

    /// Catmull-Rom: four support points and weights for the place f (in cells)
    void CubicTaps(double f, Span<int> idx, Span<float> w, int o)
    {
        int i = (int)Math.Floor(f);
        double t = f - i;
        double t2 = t * t, t3 = t2 * t;
        w[o] = (float)(-0.5 * t3 + t2 - 0.5 * t);
        w[o + 1] = (float)(1.5 * t3 - 2.5 * t2 + 1);
        w[o + 2] = (float)(-1.5 * t3 + 2 * t2 + 0.5 * t);
        w[o + 3] = (float)(0.5 * t3 - 0.5 * t2);
        for (int k = 0; k < 4; k++) idx[o + k] = Math.Clamp(i - 1 + k, 0, N - 1);
    }

    /// water: per lake flood from the center every meter whose ground lies below level + WetMargin (only in the basin),
    /// blue value = level (like the crater tool; so no dry bushes stand in shallow water)
    public long SealedCells { get; private set; }

    long FillWater(ushort[] heights, byte[] water)
    {
        long total = 0;
        SealedCells = 0;
        long sealedN = 0;
        for (int k = 0; k < LakeList.Count; k++)
        {
            var l = LakeList[k];
            int id = k + 1;
            double limit = (l.Level + Lake.WetMargin) * 256;
            // start points: center of every coarse cell of the basin that lies clearly below the level
            // (the lake center can be an island; bays can be separated from the rest)
            var queue = new Queue<long>();
            int reach = (int)(l.Radius * 2.6 / Cell) + 2, ci0 = (int)(l.X / Cell), cj0 = (int)(l.Z / Cell);
            for (int cj = Math.Max(0, cj0 - reach); cj <= Math.Min(N - 1, cj0 + reach); cj++)
                for (int ci = Math.Max(0, ci0 - reach); ci <= Math.Min(N - 1, ci0 + reach); ci++)
                {
                    if (lakeOf[cj * N + ci] != id) continue;
                    int fx =Math.Min(size - 1, (int)((ci + 0.5) * Cell)), fz = Math.Min(size - 1, (int)((cj + 0.5) * Cell));
                    long s = (long)fz * size + fx;
                    if (water[s] != 0 || heights[s] >= (l.Level - 0.5) * 256) continue;
                    water[s] = (byte)l.Level;
                    queue.Enqueue(s);
                }
            while (queue.Count > 0)
            {
                long p = queue.Dequeue();
                total++;
                int x = (int)(p % size), z = (int)(p / size);
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= size || nz >= size) continue;
                    long q = (long)nz * size + nx;
                    if (water[q] != 0 || heights[q] >= limit) continue;
                    int ci = Math.Min(N - 1, (int)(nx / Cell)), cj = Math.Min(N - 1, (int)(nz / Cell));
                    if (lakeOf[cj * N + ci] != id)
                    {
                        // outside the basin and lower than the level: gap in the shore (e.g. erosion gully) – seal it,
                        // otherwise dry land below the waterline would stand beside the water
                        heights[q] = (ushort)Math.Min(65535, limit + 13);
                        sealedN++;
                        continue;
                    }
                    water[q] = (byte)l.Level;
                    queue.Enqueue(q);
                }
            }
        }
        SealedCells = sealedN;
        return total;
    }

    /// spawn points: flat, dry places in the middle part of the map, preferably pine forest, with distance to each other
    List<SpawnPoint> ChooseSpawns(ushort[] heights, byte[] water)
    {
        // since 0.97: only one spawn point exactly in the middle (the user's request); the others come only at the end
        // (button "Spawn points" after cities, roads and wilderness POIs, see SpawnPlacer)
        long mid = (long)(size / 2) * size + size / 2;
        double level = Math.Max(heights[mid] / 256.0, water[mid] != 0 ? water[mid] + 1 : 0);
        return new List<SpawnPoint> { new SpawnPoint { X = 0, Z = 0, Y = Math.Ceiling(level), Heading = 0 } };
    }

    /// earlier selection of several spawn points (no longer used)
    List<SpawnPoint> ChooseSpawnsMany(ushort[] heights, byte[] water)
    {
        var list = new List<SpawnPoint>();
        var sr = new Random(spec.SeedValue + 99);
        int half = size / 2;
        double spacing = size / Math.Sqrt(Math.Max(1, spec.SpawnCount)) * 0.55;
        for (int pass = 0; pass < 4 && list.Count < spec.SpawnCount; pass++)
        {
            for (int attempt = 0; attempt < 4000 && list.Count < spec.SpawnCount; attempt++)
            {
                int gx = (int)(size * (0.15 + 0.7 * sr.NextDouble())), gz = (int)(size * (0.15 + 0.7 * sr.NextDouble()));
                int b = BiomeAt(gx, gz);
                if (pass == 0 && b != 0) continue;              // first only pine forest
                if (pass == 1 && (b == 3 || b == 1)) continue;   // then without snow and burnt
                if (list.Any(s => Math.Abs(s.X + half - gx) + Math.Abs(s.Z + half - gz) < spacing)) continue;
                if (!Flat(heights, gx, gz)) continue;
                if (NearWater(water, gx, gz, 40)) continue;
                double h = heights[(long)gz * size + gx] / 256.0;
                list.Add(new SpawnPoint { X = gx - half, Z = gz - half, Y = Math.Ceiling(h), Heading = sr.Next(0, 360) });
            }
            spacing *= 0.7;
        }
        return list;
    }

    bool Flat(ushort[] heights, int gx, int gz)
    {
        if (gx < 3 || gz < 3 || gx >= size - 3 || gz >= size - 3) return false;
        int min = int.MaxValue, max = int.MinValue;
        for (int dz = -3; dz <= 3; dz++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int v = heights[(long)(gz + dz) * size + gx + dx];
                min = Math.Min(min, v); max = Math.Max(max, v);
            }
        return max - min < 256 * 1.2;
    }

    bool NearWater(byte[] water, int gx, int gz, int r)
    {
        for (int dz = -r; dz <= r; dz += 4)
            for (int dx = -r; dx <= r; dx += 4)
            {
                int x = gx + dx, z = gz + dz;
                if (x < 0 || z < 0 || x >= size || z >= size) continue;
                if (water[(long)z * size + x] != 0) return true;
            }
        return false;
    }

    // main.ttw: the same for all worlds except the ID at the end (compared: Cezuxoma and East Pozesogu Valley, V 2.6 b14).
    // template = main.ttw of an existing world in the game (fits the game version), otherwise the built-in one.
    const string TtwTemplate = "dHR3ABYAAAALViAyLjYgKGIxNCkBAAAAAgAAAAYAAAAOAAAAAAAAAAAAAAAAAAAAH4V7QhAAAAAQAAAAEAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD//////////wAAAAA8AAAACgAAAAAAAAAAAAAAAAAAAAAAAADgLgAAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAcAAAAHAAAABAAAAAAAAAAEAAAAAAAAAAQAAAAAAAAABAAAACA=";

    static (byte[] Ttw, string Version) TemplateFiles(string gameWorlds)
    {
        byte[] prefix = Convert.FromBase64String(TtwTemplate);
        string version = "V.2.6.14";
        try
        {
            var dirs = new List<string>();
            if (gameWorlds != null && Directory.Exists(gameWorlds)) dirs.AddRange(Directory.GetDirectories(gameWorlds));
            var newest = dirs.Where(d => File.Exists(Path.Combine(d, "main.ttw")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "main.ttw"))).FirstOrDefault();
            if (newest != null)
            {
                var b = File.ReadAllBytes(Path.Combine(newest, "main.ttw"));
                // check the structure: "ttw", at the end length 32 + 32 characters of ID
                if (b.Length > 40 && b[0] == 't' && b[1] == 't' && b[2] == 'w' && b[b.Length - 33] == 32) prefix = b[..^32];
                var info = Path.Combine(newest, "map_info.xml");
                if (File.Exists(info))
                {
                    var doc = XmlSafe.Load(info);
                    var v = doc.SelectSingleNode("//property[@name='GameVersion']/@value")?.Value;
                    if (!string.IsNullOrWhiteSpace(v)) version = v;
                }
            }
        }
        catch (Exception) { }   // then the built-in template
        var id = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N").ToUpperInvariant());
        return ([.. prefix, .. id], version);
    }

    string MapInfo(string version)
    {
        string P(string n, string v) => $"  <property name=\"{n}\" value=\"{System.Security.SecurityElement.Escape(v)}\" />\r\n";
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<MapInfo>\r\n");
        sb.Append(P("Scale", "1"));
        sb.Append(P("HeightMapSize", $"{size},{size}"));
        sb.Append(P("Modes", "Survival,SurvivalSP,SurvivalMP,Creative"));
        sb.Append(P("FixedWaterLevel", "false"));
        sb.Append(P("RandomGeneratedWorld", "true"));
        sb.Append(P("GameVersion", version));
        sb.Append(P("Generation.Seed", spec.Seed ?? ""));
        sb.Append(P("Seed", spec.SeedValue.ToString(CultureInfo.InvariantCulture)));
        sb.Append(P("Generation.Towns", "None"));
        sb.Append(P("Generation.Wilderness", "None"));
        sb.Append(P("Generation.Lakes", spec.Lakes switch { 0 => "None", 1 => "Few", 2 => "Default", _ => "Many" }));
        sb.Append(P("Generation.Rivers", "None"));
        sb.Append(P("Generation.Cracks", "None"));
        sb.Append(P("Generation.Craters", "None"));
        string[] keys = { "Forest", "BurntForest", "Desert", "Snow", "Wasteland" };
        for (int b = 0; b < 5; b++) sb.Append(P("Generation." + keys[b], spec.Shares[b].ToString(CultureInfo.InvariantCulture)));
        sb.Append("</MapInfo>");
        return sb.ToString();
    }
}

/// Perlin noise (gradients on the integer grid), values about −1 … 1
public sealed class Perlin
{
    readonly int[] p = new int[512];

    public Perlin(int seed)
    {
        var r = new Random(seed);
        var a = Enumerable.Range(0, 256).ToArray();
        for (int i = 255; i > 0; i--) { int j = r.Next(i + 1); (a[i], a[j]) = (a[j], a[i]); }
        for (int i = 0; i < 512; i++) p[i] = a[i & 255];
    }

    public double Noise(double x, double y)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double xf = x - xi, yf = y - yi;
        int X = xi & 255, Y = yi & 255;
        double u = Fade(xf), v = Fade(yf);
        int aa = p[p[X] + Y], ab = p[p[X] + Y + 1], ba = p[p[X + 1] + Y], bb = p[p[X + 1] + Y + 1];
        double x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        double x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Lerp(x1, x2, v);
    }

    /// sum of octaves (frequency ×2, strength ×0.5), normalized to about −1 … 1 (mostly ±0.5)
    public double Fbm(double x, double y, int octaves)
    {
        double sum = 0, amp = 1, norm = 0, f = 1;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Noise(x * f + o * 19.19, y * f + o * 7.73);
            norm += amp; amp *= 0.5; f *= 2;
        }
        return sum / norm;
    }

    /// "ridged multifractal": sharp ridges, values 0 … 1
    public double Ridged(double x, double y, int octaves)
    {
        double sum = 0, amp = 1, norm = 0, f = 1, weight = 1;
        for (int o = 0; o < octaves; o++)
        {
            double n = 1 - Math.Abs(Noise(x * f + o * 11.3, y * f + o * 3.7));
            n *= n;
            n *= weight;
            weight = Math.Clamp(n * 2, 0, 1);
            sum += n * amp;
            norm += amp; amp *= 0.5; f *= 2;
        }
        return sum / norm;
    }

    static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    static double Grad(int h, double x, double y) => (h & 7) switch
    {
        0 => x + y, 1 => -x + y, 2 => x - y, 3 => -x - y,
        4 => x, 5 => -x, 6 => y, _ => -y,
    };
}

/// writes PNG (RGBA, 8 bit) row by row – without the whole image in memory (8192² RGBA would be 256 MB)
public static class PngWriter
{
    static readonly uint[] crcTable = MakeTable();

    public static void Write(string file, int width, int height, Action<int, byte[]> fillRow)
    {
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                fillRow(y, row);
                z.WriteByte(0);   // filter: none
                z.Write(row, 0, row.Length);
            }
        }
        string tmp = file + ".neu";
        using (var fs = File.Create(tmp))
        {
            fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            BeU32(ihdr, 0, (uint)width); BeU32(ihdr, 4, (uint)height);
            ihdr[8] = 8; ihdr[9] = 6;   // 8 bit, RGBA
            Chunk(fs, "IHDR", ihdr, 0, 13);
            var data = compressed.GetBuffer();
            int len = (int)compressed.Length;
            for (int off = 0; off < len; off += 1 << 20) Chunk(fs, "IDAT", data, off, Math.Min(1 << 20, len - off));
            if (len == 0) Chunk(fs, "IDAT", data, 0, 0);
            Chunk(fs, "IEND", Array.Empty<byte>(), 0, 0);
        }
        File.Move(tmp, file, true);
    }

    static void Chunk(Stream s, string type, byte[] data, int off, int len)
    {
        var head = new byte[8];
        BeU32(head, 0, (uint)len);
        Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        s.Write(head, 0, 8);
        s.Write(data, off, len);
        uint c = 0xFFFFFFFF;
        for (int i = 4; i < 8; i++) c = crcTable[(c ^ head[i]) & 0xFF] ^ (c >> 8);
        for (int i = 0; i < len; i++) c = crcTable[(c ^ data[off + i]) & 0xFF] ^ (c >> 8);
        var tail = new byte[4];
        BeU32(tail, 0, c ^ 0xFFFFFFFF);
        s.Write(tail, 0, 4);
    }

    static void BeU32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
