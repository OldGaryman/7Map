namespace MapEditor.Map;

/// Special shapes of the terrain generator (the user's request, 2026-11-08): bomb craters and earthquake cracks in the wasteland,
/// steep walls on desert plateaus, unique formations ("anomalies"). Everything acts on the coarse grid (coarse);
/// cells with hard rock (hard) may stay much steeper in the landslide.
public sealed partial class WorldGenerator
{
    byte[] hard;

    /// found anomalies (kind as English text for Loc.T, world coordinates x/z like in the game)
    public readonly List<(string Kind, int X, int Z)> Anomalies = new();
    public int CraterCount { get; private set; }
    public int CrackCount { get; private set; }

    /// noise around a circle (angle a): calculated via (cos, sin) so that there is no seam at ±180°
    double AngNoise(double a, double scale, int seed) => detail.Noise(Math.Cos(a) * scale + seed * 0.731, Math.Sin(a) * scale + seed * 0.377);

    /// rock type for the landslide (2026-11-09, the user's request: more variation of steepness in desert/wasteland):
    /// 1 = hard (steep walls of the desert plateaus), 2 = firm (52°), 3 = soft (30°), 0 = normal (38°). Pine forest and snow stay normal.
    byte Hardness(double gx, double gz)
    {
        Span<double> w = stackalloc double[5];
        Weights(gx, gz, w);
        // snow styles (2026-11-17): own rock types; style 0 (high mountains) stays normal as before
        int snowHard = SnowHardness(gx, gz, w[3]);
        if (snowHard >= 0) return (byte)snowHard;
        // plateaus of the desert stay normal (shape like version 0.73, the user's request 2026-11-10)
        double badStyle = w[2] > 0.3 ? DesertStyleAt(gx, gz) : 0;
        if (w[2] > 0.3 && (1 - badStyle) * PlateauShape(gx, gz) > 0.02) return 0;
        // badlands (desert style of the game 3.4): firm ground keeps the sharp gullies and ridges, rock clusters stay steep
        if (badStyle > 0.5)
            return bad.Fbm(gx / 1100 + 41.9, gz / 1100 + 27.5, 2) * 1.8 > 0.25 ? (byte)1 : (byte)2;
        double rocky = w[2] + 0.6 * w[1] + 0.8 * w[4];
        if (rocky < 0.4) return 0;
        double n = plat.Noise(gx / 320 + 11.3, gz / 320 + 5.9) + 0.12 * plat.Noise(gx / 70 + 1.9, gz / 70 + 6.6) + 0.05 * plat.Noise(gx / 24 + 4.4, gz / 24);
        if (n > 0.1) return 2;
        if (n < -0.22) return 3;
        return 0;
    }

    /// all cells within reach around (cx, cz): (index, gx, gz)
    void ForCells(double cx, double cz, double reach, Action<int, double, double> act)
    {
        int i0 = Math.Max(0, (int)((cx - reach) / Cell)), i1 = Math.Min(N - 1, (int)((cx + reach) / Cell));
        int j0 = Math.Max(0, (int)((cz - reach) / Cell)), j1 = Math.Min(N - 1, (int)((cz + reach) / Cell));
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
                act(j * N + i, (i + 0.5) * Cell, (j + 0.5) * Cell);
    }

    static float[] LoadRawChannel(string game, string name, int channel)
    {
        if (game == null) return null;
        string key = game + "|" + name + "|" + channel;
        lock (stampCache)
        {
            if (stampCache.TryGetValue(key, out var cached)) return cached;
            float[] res = null;
            try
            {
                string file = Path.Combine(game, "Data", "Stamps", name + ".raw");
                if (File.Exists(file))
                {
                    var b = File.ReadAllBytes(file);
                    const int n = MountainSpec.StampSize * MountainSpec.StampSize;
                    if (b.Length >= n * 6)
                    {
                        res = new float[n];
                        for (int i = 0; i < n; i++) res[i] = BitConverter.ToUInt16(b, i * 6 + channel * 2) / 256f;
                    }
                }
            }
            catch (Exception) { res = null; }
            stampCache[key] = res;
            return res;
        }
    }

    static double SampleStamp(float[] d, double fx, double fy)
    {
        const int S = MountainSpec.StampSize;
        if (fx < 0 || fy < 0 || fx >= S - 1 || fy >= S - 1) return 0;
        int ix = (int)fx, iy = (int)fy;
        double tx = fx - ix, ty = fy - iy;
        int i = iy * S + ix;
        return (d[i] * (1 - tx) + d[i + 1] * tx) * (1 - ty) + (d[i + S] * (1 - tx) + d[i + S + 1] * tx) * ty;
    }

    /// stamp at (cx, cz) with diameter size, rotated by a: returns (fx, fy) in the stamp for the place (gx, gz)
    static (double, double) StampCoord(double gx, double gz, double cx, double cz, double size, double a, bool mirror)
    {
        double dx = gx - cx, dz = gz - cz, ca = Math.Cos(a), sa = Math.Sin(a);
        double u = dx * ca - dz * sa, v = dx * sa + dz * ca;
        if (mirror) u = -u;
        const int S = MountainSpec.StampSize;
        return (S / 2.0 + u / size * S, S / 2.0 + v / size * S);
    }

    // ---------------------------------------------------------------- craters and cracks (wasteland)

    readonly List<Action> lateAnomalies = new();
    // exclusion footprints of anomalies (grid meters cx, cz, radius): lakes stay clear so they neither destroy anomalies nor plunge into sinkholes/craters
    readonly List<(double X, double Z, double Radius)> anomalyZones = new();

    /// true if a lake at (cx, cz) with shape would overlap an anomaly (checked in PlaceLakes, PlaceSnowLakes, NewRiverLake;
    /// anomalyZones is independent of Cell, so preview and full world make the same decision)
    bool OverlapsAnomaly(double cx, double cz, LakeShape shape)
    {
        foreach (var (ax, az, ar) in anomalyZones)
        {
            double dx = ax - cx, dz = az - cz;
            double d2 = dx * dx + dz * dz;
            double maxR = shape.Radius * 2.2 + ar + 35;
            if (d2 >= maxR * maxR) continue;
            double need = shape.R(Math.Atan2(dz, dx)) + ar + 35;
            if (d2 < need * need) return true;
        }
        return false;
    }
    float[] wasteBefore, wasteDelta, wasteBreakSd;
    byte[] wasteHardBefore;
    public WastelandDamage WastelandDamage { get; private set; }

    void PlaceCratersAndCracks()
    {
        foreach (var a in lateAnomalies) a();
        PlaceForestRocks();
        PlaceWastelandCratersAndCracks(trackDamage: Cell <= 4);
    }

    /// Wasteland-only bomb craters, bomb carpets, crater_01 stamps and earthquake cracks (also reused when reconstructing
    /// wasteland damage for existing YAMEE worlds that were generated before yamee_wasteland.bin existed).
    void PlaceWastelandCratersAndCracks(bool trackDamage)
    {
        CraterCount = CrackCount = 0;
        wasteBefore = wasteDelta = wasteBreakSd = null;
        wasteHardBefore = null;
        WastelandDamage = null;
        if (spec.Shares[4] <= 0) return;
        if (trackDamage)
        {
            wasteBefore = (float[])coarse.Clone();
            wasteHardBefore = (byte[])hard.Clone();
            wasteBreakSd = new float[N * N];
            Array.Fill(wasteBreakSd, -16f);
        }
        var r = new Random(spec.SeedValue + 404);
        int total = Math.Max(1, spec.Shares.Sum());
        double km2 = (double)size * size / 1e6 * spec.Shares[4] / total;
        bool InWaste(double x, double z) => x > 50 && z > 50 && x < size - 50 && z < size - 50 && BiomeAt(x, z) == 4;
        (double, double)? Spot()
        {
            for (int k = 0; k < 60; k++)
            {
                double x = r.NextDouble() * size, z = r.NextDouble() * size;
                if (InWaste(x, z)) return (x, z);
            }
            return null;
        }

        // single bomb craters: many small, some medium, rarely large
        foreach (var (perKm2, rMin, rMax, depthPerR) in new[] { (85.0, 2.5, 8.0, 0.4), (14.0, 10.0, 24.0, 0.32), (0.8, 35.0, 65.0, 0.25) })
        {
            int n = (int)Math.Round(km2 * perKm2);
            for (int k = 0; k < n; k++)
            {
                if (Spot() is not (double x, double z)) break;
                double rad = rMin + r.NextDouble() * (rMax - rMin);
                Crater(x, z, rad, rad * depthPerR * (0.7 + 0.6 * r.NextDouble()), r.Next(1000));
            }
        }
        // bomb carpets: rows of 5–12 craters
        for (int k = 0, n = (int)Math.Round(km2 * 4); k < n; k++)
        {
            if (Spot() is not (double x, double z)) break;
            double a = r.NextDouble() * Math.PI * 2, step = 9 + r.NextDouble() * 12;
            int count = 5 + r.Next(8);
            for (int c = 0; c < count; c++)
            {
                double px = x + Math.Cos(a) * step * c + (r.NextDouble() - 0.5) * 6, pz = z + Math.Sin(a) * step * c + (r.NextDouble() - 0.5) * 6;
                if (!InWaste(px, pz)) break;
                double rad = 3 + r.NextDouble() * 5;
                Crater(px, pz, rad, rad * 0.4 * (0.7 + 0.6 * r.NextDouble()), r.Next(1000));
            }
        }
        // crater fields from the game stamp crater_01 (channel 0 = height, channel 1 = opacity)
        var ch0 = LoadRawChannel(spec.GameFolder, "crater_01", 0);
        var ch1 = LoadRawChannel(spec.GameFolder, "crater_01", 1);
        if (ch0 != null && ch1 != null)
        {
            double baseLevel = ch0.Average();
            for (int k = 0, n = Math.Max(1, (int)Math.Round(km2 * 0.12)); k < n; k++)
            {
                if (Spot() is not (double x, double z)) break;
                double sz = 300 + r.NextDouble() * 250, a = r.NextDouble() * Math.PI * 2, f = 0.8 + 0.7 * r.NextDouble();
                bool mirror = r.Next(2) == 0;
                double reach = sz * 0.72;
                int i0 = Math.Max(0, (int)((x - reach) / Cell)), i1 = Math.Min(N - 1, (int)((x + reach) / Cell));
                int j0 = Math.Max(0, (int)((z - reach) / Cell)), j1 = Math.Min(N - 1, (int)((z + reach) / Cell));
                int sw = i1 - i0 + 1, sh = j1 - j0 + 1;
                bool[] broken = wasteBreakSd != null && sw > 0 && sh > 0 ? new bool[sw * sh] : null;
                ForCells(x, z, reach, (idx, gx, gz) =>
                {
                    var (fx, fy) = StampCoord(gx, gz, x, z, sz, a, mirror);
                    double m = SampleStamp(ch1, fx, fy) / 256.0;
                    if (m <= 0) return;
                    double d = (SampleStamp(ch0, fx, fy) - baseLevel) * Math.Min(1, m) * f;
                    coarse[idx] += (float)d;
                    if (broken != null && (d < -0.6 || d > 1.2))
                    {
                        int ci = idx % N - i0, cj = idx / N - j0;
                        if (ci >= 0 && cj >= 0 && ci < sw && cj < sh) broken[cj * sw + ci] = true;
                    }
                });
                if (broken != null) MergeStampBreak(broken, i0, j0, sw, sh);
                CraterCount += 8;
            }
        }
        // earthquake cracks: jagged, branching fissures, one side slightly raised (fault)
        for (int k = 0, n = Math.Max(1, (int)Math.Round(km2 * 0.55)); k < n; k++)
        {
            if (Spot() is not (double x, double z)) break;
            double len = 150 + r.NextDouble() * 550;
            Crack(x, z, r.NextDouble() * Math.PI * 2, len, 2 + r.NextDouble() * 3, 5 + r.NextDouble() * 9, 0.5 + r.NextDouble() * 2.5, r, 2);
            CrackCount++;
        }
    }

    /// Convert a boolean crater_01 stamp mask into signed distances (m) up to ±8 m and merge into wasteBreakSd.
    void MergeStampBreak(bool[] broken, int i0, int j0, int sw, int sh)
    {
        int r = Math.Max(1, (int)Math.Ceiling(8.0 / Cell));
        double halfCell = Cell * 0.5;
        for (int cj = 0; cj < sh; cj++)
            for (int ci = 0; ci < sw; ci++)
            {
                bool inside = broken[cj * sw + ci];
                double bestSq = double.MaxValue;
                int dj0 = Math.Max(-r, -cj), dj1 = Math.Min(r, sh - 1 - cj);
                int di0 = Math.Max(-r, -ci), di1 = Math.Min(r, sw - 1 - ci);
                for (int dj = dj0; dj <= dj1; dj++)
                    for (int di = di0; di <= di1; di++)
                    {
                        if (broken[(cj + dj) * sw + (ci + di)] == inside) continue;
                        double dSq = (di * di + dj * dj) * Cell * Cell;
                        if (dSq < bestSq) bestSq = dSq;
                    }
                if (bestSq == double.MaxValue)
                {
                    if (inside)
                    {
                        int idx = (j0 + cj) * N + (i0 + ci);
                        if (8f > wasteBreakSd[idx]) wasteBreakSd[idx] = 8f;
                    }
                    continue;
                }
                double dist = Math.Max(0.25, Math.Sqrt(bestSq) - halfCell);
                float sd = (float)(inside ? dist : -dist);
                int gidx = (j0 + cj) * N + (i0 + ci);
                if (sd > wasteBreakSd[gidx]) wasteBreakSd[gidx] = sd;
            }
    }

    void Crater(double cx, double cz, double rad, double depth, int seed)
    {
        CraterCount++;
        double rim = depth * 0.32;
        double deepest = 0;
        ForCells(cx, cz, rad * 2.4 + Cell, (idx, gx, gz) =>
        {
            double dx = gx - cx, dz = gz - cz, dist = Math.Sqrt(dx * dx + dz * dz);
            double a = Math.Atan2(dz, dx);
            double rr = rad * (1 + 0.12 * Math.Sin(3 * a + seed) + 0.07 * Math.Sin(5 * a + seed * 0.3));
            double t = dist / rr;
            double delta = 0;
            if (t < 1) delta -= depth * (1 - t * t);
            delta += rim * Math.Exp(-Math.Pow((t - 1) / 0.32, 2)) * (t < 1 ? 1 : 0.85);   // rim, flatter ejecta outside
            coarse[idx] += (float)delta;
            if (delta < deepest) deepest = delta;
        });
        if (wasteBreakSd != null && deepest <= -0.4)
        {
            ForCells(cx, cz, rad * 1.35 + 10 + Cell, (idx, gx, gz) =>
            {
                double dx = gx - cx, dz = gz - cz, dist = Math.Sqrt(dx * dx + dz * dz);
                double a = Math.Atan2(dz, dx);
                double rr = rad * (1 + 0.12 * Math.Sin(3 * a + seed) + 0.07 * Math.Sin(5 * a + seed * 0.3));
                double breakR = Math.Max(rr * 1.12, 3.0);
                float sd = (float)(breakR - dist);
                if (sd > wasteBreakSd[idx]) wasteBreakSd[idx] = sd;
            });
        }
    }

    /// crack as a zigzag path; depth and width run out at the ends; branches with half the depth
    void Crack(double x, double z, double dir, double length, double width, double depth, double offset, Random r, int branches)
    {
        var pts = new List<(double X, double Z)> { (x, z) };
        double a = dir, walked = 0;
        while (walked < length)
        {
            double step = 4 + r.NextDouble() * 7;
            a += (r.NextDouble() - 0.5) * 0.9;                 // jagged
            a += (dir - a) * 0.25;                             // but in the main direction
            x += Math.Cos(a) * step; z += Math.Sin(a) * step;
            if (x < 20 || z < 20 || x > size - 20 || z > size - 20) break;
            pts.Add((x, z));
            walked += step;
        }
        if (pts.Count < 3) return;
        double margin = width * 2 + 26;
        double minX = pts.Min(p => p.X) - margin, maxX = pts.Max(p => p.X) + margin, minZ = pts.Min(p => p.Z) - margin, maxZ = pts.Max(p => p.Z) + margin;
        int i0 = Math.Max(0, (int)(minX / Cell)), i1 = Math.Min(N - 1, (int)(maxX / Cell));
        int j0 = Math.Max(0, (int)(minZ / Cell)), j1 = Math.Min(N - 1, (int)(maxZ / Cell));
        int w = i1 - i0 + 1, h = j1 - j0 + 1;
        if (w <= 0 || h <= 0) return;
        var dist = new float[w * h];
        var side = new sbyte[w * h];
        var along = new float[w * h];
        Array.Fill(dist, float.MaxValue);
        double total = 0;
        var cum = new double[pts.Count];
        for (int s = 1; s < pts.Count; s++) cum[s] = total += Math.Sqrt(Math.Pow(pts[s].X - pts[s - 1].X, 2) + Math.Pow(pts[s].Z - pts[s - 1].Z, 2));
        for (int s = 1; s < pts.Count; s++)
        {
            var (ax, az) = pts[s - 1];
            var (bx, bz) = pts[s];
            double sx = bx - ax, sz = bz - az, l2 = sx * sx + sz * sz;
            int si0 = Math.Max(i0, (int)((Math.Min(ax, bx) - margin) / Cell)), si1 = Math.Min(i1, (int)((Math.Max(ax, bx) + margin) / Cell));
            int sj0 = Math.Max(j0, (int)((Math.Min(az, bz) - margin) / Cell)), sj1 = Math.Min(j1, (int)((Math.Max(az, bz) + margin) / Cell));
            for (int j = sj0; j <= sj1; j++)
                for (int i = si0; i <= si1; i++)
                {
                    double gx = (i + 0.5) * Cell, gz = (j + 0.5) * Cell;
                    double t = Math.Clamp(((gx - ax) * sx + (gz - az) * sz) / l2, 0, 1);
                    double px = ax + sx * t - gx, pz = az + sz * t - gz;
                    float d = (float)Math.Sqrt(px * px + pz * pz);
                    int li = (j - j0) * w + (i - i0);
                    if (d < dist[li])
                    {
                        dist[li] = d;
                        side[li] = (sbyte)(sx * (gz - az) - sz * (gx - ax) > 0 ? 1 : -1);
                        along[li] = (float)((cum[s - 1] + t * Math.Sqrt(l2)) / total);
                    }
                }
        }
        int seed = r.Next(1000);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                int li = (j - j0) * w + (i - i0);
                double d = dist[li];
                if (d > margin) continue;
                double u = along[li];
                double taper = Math.Sin(Math.PI * Math.Clamp(u, 0, 1));           // ends run out
                double gx = (i + 0.5) * Cell, gz = (j + 0.5) * Cell;
                double wv = width * (0.6 + 0.4 * taper) * (0.75 + 0.5 * (0.5 + 0.5 * detail.Noise(gx / 30 + seed, gz / 30)));
                int idx = j * N + i;
                double delta = 0;
                double dv = 0;
                if (d < wv || (wasteBreakSd != null && d < wv + 12))
                    dv = depth * taper * (0.7 + 0.6 * (0.5 + 0.5 * detail.Noise(gx / 18, gz / 18 + seed)));
                if (d < wv)
                    delta -= dv * Math.Sqrt(1 - (d / wv) * (d / wv));
                if (wasteBreakSd != null && dv > 0.5)
                {
                    double tip = SmoothStep(0.5, 2.0, dv);
                    double lip = Math.Max(wv + 1.8, 3.0) * tip;
                    float sd = (float)(lip - d);
                    if (sd > wasteBreakSd[idx]) wasteBreakSd[idx] = sd;
                }
                if (d < wv + 2.5) hard[idx] = 1;   // walls stand steep
                // fault: one side raised by "offset", run out after 25 m
                if (side[li] > 0) delta += offset * taper * (1 - SmoothStep(wv, wv + 25, d));
                coarse[idx] += (float)delta;
            }
        // branches
        for (int b = 0; b < branches; b++)
        {
            var p = pts[r.Next(pts.Count / 4, Math.Max(pts.Count / 4 + 1, pts.Count * 3 / 4))];
            Crack(p.X, p.Z, dir + (r.Next(2) == 0 ? 1 : -1) * (0.5 + r.NextDouble() * 0.6), length * (0.2 + r.NextDouble() * 0.25),
                width * 0.6, depth * 0.6, 0, r, 0);
        }
    }

    /// Capture the exact height delta of wasteland craters & cracks after Erode(4).
    void CaptureWastelandDelta()
    {
        if (wasteBefore == null || wasteHardBefore == null || wasteBreakSd == null) return;
        ErodeGrid(wasteBefore, wasteHardBefore, 4);
        wasteDelta = new float[N * N];
        Parallel.For(0, N * N, i => wasteDelta[i] = coarse[i] - wasteBefore[i]);
        wasteBefore = null;
        wasteHardBefore = null;
    }

    /// Finalize WastelandDamage after lakes & rivers are placed (water areas keep no crater/crack break).
    void FinalizeWastelandDamage()
    {
        if (wasteDelta == null || wasteBreakSd == null) return;
        if (lakeOf != null)
        {
            for (int i = 0; i < N * N; i++)
                if (lakeOf[i] != 0) { wasteDelta[i] = 0; wasteBreakSd[i] = -16f; }
        }
        WastelandDamage = WastelandDamage.FromGrid(size, N, wasteDelta, wasteBreakSd);
        wasteDelta = null;
        wasteBreakSd = null;
    }

    /// Reconstruct WastelandDamage for an existing YAMEE world without yamee_wasteland.bin using yamee_generator.txt.
    public static WastelandDamage ReconstructWastelandDamage(WorldData w, string gameFolder)
    {
        var parsed = WastelandDamage.ParseGeneratorFile(w.Folder, gameFolder, w.Size);
        if (parsed is not (var spec, bool hasLayout)) return null;
        if (!hasLayout)
        {
            // older yamee_generator.txt omitted BiomeLayout: test layout 0 vs 1 against biomes.png
            int Match(int lay)
            {
                var s = new GeneratorSpec { Size = spec.Size, Seed = spec.Seed, Shares = (int[])spec.Shares.Clone(), BiomeCoherence = spec.BiomeCoherence, BiomeLayout = lay };
                var g = new WorldGenerator(s);
                g.GrowBiomes();
                int ok = 0, step = Math.Max(32, w.Size / 20);
                for (int z = step / 2; z < w.Size; z += step)
                    for (int x = step / 2; x < w.Size; x += step)
                        if (g.BiomeAt(x, z) == w.BiomeAt(x - w.Half, z - w.Half)) ok++;
                return ok;
            }
            if (Match(1) > Match(0)) spec.BiomeLayout = 1;
        }
        var gen = new WorldGenerator(spec);
        gen.Cell = 4;
        gen.N = (int)Math.Round(spec.Size / gen.Cell);
        gen.GrowBiomes();
        gen.coarse = new float[gen.N * gen.N];
        gen.hard = new byte[gen.N * gen.N];
        gen.PlaceWastelandCratersAndCracks(trackDamage: true);
        if (gen.wasteBreakSd == null) return null;
        // zero out cells in water
        for (int j = 0; j < gen.N; j++)
            for (int i = 0; i < gen.N; i++)
            {
                int wx = (int)((i + 0.5) * gen.Cell) - w.Half, wz = (int)((j + 0.5) * gen.Cell) - w.Half;
                if (w.WaterAt(wx, wz)) { int idx = j * gen.N + i; gen.coarse[idx] = 0; gen.wasteBreakSd[idx] = -16f; }
            }
        return WastelandDamage.FromGrid(spec.Size, gen.N, gen.coarse, gen.wasteBreakSd);
    }

    // ---------------------------------------------------------------- anomalies

    void PlaceAnomalies()
    {
        var r = new Random(spec.SeedValue + 909);
        double km2 = (double)size * size / 1e6;
        int want = Math.Max(1, (int)Math.Round(km2 / 12 * spec.Anomalies));
        if (spec.Anomalies <= 0) return;
        var canyon = LoadRawChannel(spec.GameFolder, "canyon_01", 1);
        int half = size / 2;
        for (int k = 0, attempts = 0; k < want && attempts < want * 50; attempts++)
        {
            double margin = Math.Min(400, size * 0.1);
            double x = margin + r.NextDouble() * (size - 2 * margin), z = margin + r.NextDouble() * (size - 2 * margin);
            if (Anomalies.Any(a => Math.Abs(a.X + half - x) + Math.Abs(a.Z + half - z) < 900)) continue;
            int b = BiomeAt(x, z);
            // fitting kinds per biome
            var kinds = b switch
            {
                0 => new[] { "Meteor crater", "Sinkhole", "Layered escarpment", "Rock needles" },
                1 => new[] { "Basalt columns", "Rock needles", "Meteor crater", "Terraced hill" },
                2 => new[] { "Rock needles", "Terraced hill", "Canyon", "Layered escarpment", "Meteor crater" },
                3 => new[] { "Basalt columns", "Sinkhole", "Rock needles", "Meteor crater" },
                _ => new[] { "Meteor crater", "Canyon", "Rock needles", "Layered escarpment" },
            };
            string kind = kinds[r.Next(kinds.Length)];
            if (kind == "Canyon" && canyon == null) kind = "Meteor crater";
            if (Anomalies.Any(a => a.Kind == kind) && r.NextDouble() < 0.6) continue;   // rather different ones
            // meteorite crater, gorge, strata step at once (the rain erosion gives them gullies), columns, needles, terraces and sinkhole only
            // afterwards (otherwise the erosion washes them away); own randomness per anomaly so that the order shifts nothing
            var ar = new Random(r.Next());
            double ax = x, az = z;
            switch (kind)
            {
                case "Meteor crater": Meteor(x, z, ar); break;
                case "Layered escarpment": Escarpment(x, z, ar); break;
                case "Canyon": Canyon(x, z, ar, canyon); break;
                case "Sinkhole": lateAnomalies.Add(() => Sinkhole(ax, az, ar)); break;
                case "Rock needles": lateAnomalies.Add(() => Needles(ax, az, ar)); break;
                case "Basalt columns": lateAnomalies.Add(() => Columns(ax, az, ar)); break;
                case "Terraced hill": lateAnomalies.Add(() => Terraces(ax, az, ar)); break;
            }
            Anomalies.Add((kind, (int)x - half, (int)z - half));
            k++;
        }
    }

    void Meteor(double cx, double cz, Random r)
    {
        double rad = 110 + r.NextDouble() * 120, depth = 22 + r.NextDouble() * 18, rim = 9 + r.NextDouble() * 7;
        anomalyZones.Add((cx, cz, rad));
        bool peak = rad > 150;
        int seed = r.Next(1000);
        ForCells(cx, cz, rad * 2.2, (idx, gx, gz) =>
        {
            double dx = gx - cx, dz = gz - cz, dist = Math.Sqrt(dx * dx + dz * dz);
            double a = Math.Atan2(dz, dx);
            double rr = rad * (1 + 0.06 * Math.Sin(3 * a + seed) + 0.04 * AngNoise(a, 4, seed));
            double t = dist / rr;
            double delta = 0;
            if (t < 1) delta -= depth * (1 - Math.Pow(t, 2.6));                                 // flat ground, steep wall
            delta += rim * Math.Exp(-Math.Pow((t - 1) / 0.18, 2)) * (t < 1 ? 1 : 1);
            if (t > 1) delta += rim * 0.35 * Math.Exp(-(t - 1) * 2.5);                          // ejecta
            if (peak && t < 0.22) delta += depth * 0.55 * Math.Pow(1 - t / 0.22, 1.5);          // central mountain
            coarse[idx] += (float)delta;
            if (t > 0.8 && t < 1.08) hard[idx] = 1;
        });
    }

    void Sinkhole(double cx, double cz, Random r)
    {
        double rad = 18 + r.NextDouble() * 24, depth = 25 + r.NextDouble() * 20;
        anomalyZones.Add((cx, cz, rad * 1.4));
        int seed = r.Next(1000);
        ForCells(cx, cz, rad * 1.8, (idx, gx, gz) =>
        {
            double dx = gx - cx, dz = gz - cz, dist = Math.Sqrt(dx * dx + dz * dz);
            double a = Math.Atan2(dz, dx);
            double t = dist / (rad * (1 + 0.15 * Math.Sin(2 * a + seed) + 0.08 * Math.Sin(5 * a + seed)));
            double delta = 0;
            if (t < 1) delta = -depth * (t < 0.75 ? 1 - 0.15 * t : SmoothStep(1, 0.75, t));    // vertical walls, rubble on the ground
            else delta = -2 * Math.Exp(-(t - 1) * 4);                                             // rim sinks a little
            coarse[idx] += (float)delta;
            if (t > 0.6 && t < 1.1) hard[idx] = 1;
        });
    }

    void Needles(double cx, double cz, Random r)
    {
        anomalyZones.Add((cx, cz, 120));
        int n = 8 + r.Next(12);
        for (int k = 0; k < n; k++)
        {
            double a = r.NextDouble() * Math.PI * 2, d = Math.Sqrt(r.NextDouble()) * 120;
            double x = cx + Math.Cos(a) * d, z = cz + Math.Sin(a) * d;
            double rad = 4 + r.NextDouble() * 7, height = 25 + r.NextDouble() * 35;
            int seed = r.Next(1000);
            ForCells(x, z, rad * 1.6, (idx, gx, gz) =>
            {
                double dx = gx - x, dz = gz - z, dist = Math.Sqrt(dx * dx + dz * dz);
                double aa = Math.Atan2(dz, dx);
                double t = dist / (rad * (1 + 0.2 * Math.Sin(3 * aa + seed)));
                if (t >= 1.5) return;
                // steep tower with rubble foot
                double v = t < 1 ? 1 - Math.Pow(t, 4) * 0.35 : 0.65 * Math.Pow(1 - (t - 1) / 0.5, 2);
                double top = 1 + 0.25 * detail.Noise(gx / 6 + seed, gz / 6);
                coarse[idx] += (float)(height * v * top);
                if (t < 1.1) hard[idx] = 1;
            });
        }
    }

    void Columns(double cx, double cz, Random r)
    {
        double rad = 70 + r.NextDouble() * 60, cellSize = 10 + r.NextDouble() * 5, baseH = 4 + r.NextDouble() * 5;
        anomalyZones.Add((cx, cz, rad));
        int seed = r.Next(100000);
        ForCells(cx, cz, rad, (idx, gx, gz) =>
        {
            double dx = gx - cx, dz = gz - cz, dist = Math.Sqrt(dx * dx + dz * dz);
            double t = dist / rad;
            if (t >= 1) return;
            // hexagon grid: find the nearest center
            double q = (dx * Math.Sqrt(3) / 3 - dz / 3) / cellSize, rr = (2.0 / 3 * dz) / cellSize;
            double cxq = q, czr = rr, cyq = -q - rr;
            double rq = Math.Round(cxq), rr2 = Math.Round(czr), ry = Math.Round(cyq);
            double dq = Math.Abs(rq - cxq), dr = Math.Abs(rr2 - czr), dy = Math.Abs(ry - cyq);
            if (dq > dr && dq > dy) rq = -rr2 - ry; else if (dr > dy) rr2 = -rq - ry;
            uint hsh = (uint)((int)rq * 73856093 ^ (int)rr2 * 19349663 ^ seed);
            hsh ^= hsh >> 13; hsh *= 0x5bd1e995; hsh ^= hsh >> 15;
            double rand = (hsh & 0xFFFF) / 65535.0;
            double fall = 1 - SmoothStep(0.55, 1, t);
            coarse[idx] += (float)((baseH + rand * 9) * fall);
            if (fall > 0.2) hard[idx] = 1;
        });
    }

    void Terraces(double cx, double cz, Random r)
    {
        double rad = 120 + r.NextDouble() * 90, height = 40 + r.NextDouble() * 25;
        anomalyZones.Add((cx, cz, rad));
        int steps = 4 + r.Next(3), seed = r.Next(1000);
        ForCells(cx, cz, rad * 1.15, (idx, gx, gz) =>
        {
            double dx = gx - cx, dz = gz - cz, dist = Math.Sqrt(dx * dx + dz * dz);
            double a = Math.Atan2(dz, dx);
            double t = dist / (rad * (1 + 0.18 * Math.Sin(2 * a + seed) + 0.1 * AngNoise(a, 3, seed)));
            if (t >= 1) return;
            double f = 1 - t;                                                     // 0 at the edge, 1 in the middle
            double level = Math.Floor(f * steps + 0.15 * detail.Noise(gx / 40, gz / 40 + seed)) / steps;
            double frac = f * steps - Math.Floor(f * steps);
            double v = level + SmoothStep(0.82, 1, frac) / steps;                 // steps with a short steep edge
            coarse[idx] += (float)(height * Math.Clamp(v, 0, 1) * (1 - SmoothStep(0.92, 1, t) * 0.5));
            if (frac > 0.75) hard[idx] = 1;
        });
    }

    void Escarpment(double cx, double cz, Random r)
    {
        double len = 300 + r.NextDouble() * 180, wid = 160 + r.NextDouble() * 110, height = 28 + r.NextDouble() * 22;
        anomalyZones.Add((cx, cz, Math.Max(len, wid) * 0.5));
        double a = r.NextDouble() * Math.PI * 2, ca = Math.Cos(a), sa = Math.Sin(a);
        int seed = r.Next(1000);
        ForCells(cx, cz, Math.Max(len, wid), (idx, gx, gz) =>
        {
            double dx = gx - cx, dz = gz - cz;
            double u = dx * ca + dz * sa, v = -dx * sa + dz * ca;               // u along (rise), v across
            double edge = len / 2 + 14 * detail.Noise(v / 45 + seed, 0.3);        // frayed break edge
            double s = (u + len / 2) / (edge + len / 2);
            if (s <= 0 || u > edge + 14) return;
            double side = 1 - SmoothStep(wid / 2 - 30, wid / 2, Math.Abs(v));
            if (side <= 0) return;
            double rise = Math.Min(1, s);
            double drop = u > edge ? 1 - SmoothStep(edge, edge + 10, u) : 1;      // almost vertical edge
            coarse[idx] += (float)(height * rise * side * drop);
            if (u > edge - 4 && u < edge + 12 && side > 0.3) hard[idx] = 1;
        });
    }

    void Canyon(double cx, double cz, Random r, float[] stamp)
    {
        double sz = 700 + r.NextDouble() * 400, depth = 30 + r.NextDouble() * 20, a = r.NextDouble() * Math.PI * 2;
        anomalyZones.Add((cx, cz, sz * 0.42));
        bool mirror = r.Next(2) == 0;
        float max = stamp.Max();
        if (max <= 0) return;
        ForCells(cx, cz, sz * 0.72, (idx, gx, gz) =>
        {
            var (fx, fy) = StampCoord(gx, gz, cx, cz, sz, a, mirror);
            double v = SampleStamp(stamp, fx, fy) / max;
            if (v <= 0) return;
            coarse[idx] -= (float)(depth * v);
            if (v > 0.08) hard[idx] = 1;
        });
    }
}

/// rocks in the pine forest (2026-11-11, the user's request: steep at about 2 % of the places so that you recognize locations):
/// rock walls (step in the slope, face points downhill), single rock towers and rock domes. Placed after the rain erosion and
/// marked as hard rock so that they stay sharp. Number per km² of pine forest × "Ruggedness" slider.
public sealed partial class WorldGenerator
{
    public int ForestRockCount { get; private set; }

    void PlaceForestRocks()
    {
        ForestRockCount = 0;
        if (spec.Shares[0] <= 0 || spec.Rough <= 0) return;
        var r = new Random(spec.SeedValue + 606);
        int total = Math.Max(1, spec.Shares.Sum());
        double km2 = (double)size * size / 1e6 * spec.Shares[0] / total * Math.Min(spec.Rough, 2);
        (double, double)? Spot()
        {
            for (int k = 0; k < 60; k++)
            {
                double x = 100 + r.NextDouble() * (size - 200), z = 100 + r.NextDouble() * (size - 200);
                if (BiomeAt(x, z) != 0) continue;
                Span<double> w = stackalloc double[5];
                Weights(x, z, w);
                if (w[0] < 0.6) continue;   // not at the biome edge
                return (x, z);
            }
            return null;
        }
        for (int k = 0, n = (int)Math.Round(km2 * 9); k < n; k++)
            if (Spot() is (double x, double z)) { CliffBand(x, z, r); ForestRockCount++; }
        for (int k = 0, n = (int)Math.Round(km2 * 1.6); k < n; k++)
            if (Spot() is (double x, double z)) { RockTower(x, z, r); ForestRockCount++; }
        for (int k = 0, n = (int)Math.Round(km2 * 1.2); k < n; k++)
            if (Spot() is (double x, double z)) { RockDome(x, z, r); ForestRockCount++; }
    }

    /// rock wall: step across the slope, on top the terrain runs out flat to the back, the ends run out sideways
    void CliffBand(double cx, double cz, Random r)
    {
        double len = 80 + r.NextDouble() * 180, height = 10 + r.NextDouble() * 22, back = 50 + r.NextDouble() * 90;
        // direction uphill from the terrain (wall faces downhill); random on flat ground
        double gxs = SampleCoarse(cx + 20, cz) - SampleCoarse(cx - 20, cz), gzs = SampleCoarse(cx, cz + 20) - SampleCoarse(cx, cz - 20);
        double up = Math.Abs(gxs) + Math.Abs(gzs) > 1 ? Math.Atan2(gzs, gxs) : r.NextDouble() * Math.PI * 2;
        double ux = Math.Cos(up), uz = Math.Sin(up);
        int seed = r.Next(1000);
        double bend = (r.NextDouble() - 0.5) * 0.004;   // slightly curved
        ForCells(cx, cz, len / 2 + back + 20, (idx, gx, gz) =>
        {
            double dx = gx - cx, dz = gz - cz;
            double u = dx * ux + dz * uz, v = -dx * uz + dz * ux;   // u uphill, v along the wall
            if (Math.Abs(v) > len / 2 + 15) return;
            // wall course with projections and gullies
            double front = bend * v * v + 9 * detail.Noise(v / 35 + seed, 1.3) + 3 * detail.Noise(v / 9 + seed, 4.1);
            double along = 1 - SmoothStep(len / 2 - 25, len / 2 + 15, Math.Abs(v));                 // ends run out
            double hgt = height * along * (0.75 + 0.5 * (0.5 + 0.5 * detail.Noise(v / 50 + seed, 7.7)));
            double s = u - front;
            // half height: raised on top (lens-shaped running out into the slope), half height: lowered below (rubble slope runs
            // out downhill) – so the wall stands in the slope instead of as a block on it
            double delta;
            if (s >= 0)
            {
                double e = Math.Sqrt(Math.Pow(v / (len / 2 + 10), 2) + Math.Pow(s / back, 2));
                delta = hgt * 0.5 * SmoothStep(-2, 3, s) * (1 - SmoothStep(0.25, 1, e));
            }
            else
            {
                double apron = 20 + hgt * 1.2;
                double e = Math.Sqrt(Math.Pow(v / (len / 2 + 10), 2) + Math.Pow(s / apron, 2));
                delta = -hgt * 0.5 * (1 - SmoothStep(0.15, 1, e)) * (0.9 + 0.2 * detail.Noise(gx / 8 + seed, gz / 8));
            }
            delta *= along;
            coarse[idx] += (float)delta;
            if (s > -3 && s < 6 && along > 0.2) hard[idx] = 1;
        });
    }

    /// rock tower: free-standing column, 15–35 m high, with one or two small neighbors – visible from afar
    void RockTower(double cx, double cz, Random r)
    {
        int parts = 1 + r.Next(3);
        for (int p = 0; p < parts; p++)
        {
            double a = r.NextDouble() * Math.PI * 2, d = p == 0 ? 0 : 12 + r.NextDouble() * 18;
            double x = cx + Math.Cos(a) * d, z = cz + Math.Sin(a) * d;
            double rad = (p == 0 ? 7 : 4) + r.NextDouble() * 5, height = (p == 0 ? 18 : 8) + r.NextDouble() * (p == 0 ? 17 : 10);
            int seed = r.Next(1000);
            ForCells(x, z, rad * 2.2, (idx, gx, gz) =>
            {
                double dx = gx - x, dz = gz - z, dist = Math.Sqrt(dx * dx + dz * dz);
                double t = dist / (rad * (1 + 0.22 * AngNoise(Math.Atan2(dz, dx), 1.5, seed)));
                if (t >= 2.2) return;
                double v = t < 1 ? 1 - 0.25 * Math.Pow(t, 6) : 0.75 * Math.Pow(Math.Max(0, 1 - (t - 1) / 1.2), 2.5);   // tower + rubble foot
                coarse[idx] += (float)(height * v * (1 + 0.12 * detail.Noise(gx / 5 + seed, gz / 5)));
                if (t < 1.15) hard[idx] = 1;
            });
        }
    }

    /// rock dome: round rock hump with steep sides and rounded top (like a granite rock)
    void RockDome(double cx, double cz, Random r)
    {
        double rad = 22 + r.NextDouble() * 30, height = 10 + r.NextDouble() * 14;
        int seed = r.Next(1000);
        ForCells(cx, cz, rad * 1.8, (idx, gx, gz) =>
        {
            double dx = gx - cx, dz = gz - cz, dist = Math.Sqrt(dx * dx + dz * dz);
            double t = dist / (rad * (1 + 0.25 * AngNoise(Math.Atan2(dz, dx), 1.3, seed) + 0.08 * AngNoise(Math.Atan2(dz, dx), 4, seed + 3)));
            if (t >= 1.8) return;
            double v = t < 1 ? Math.Sqrt(Math.Max(0, 1 - Math.Pow(t, 5))) : 0.15 * Math.Max(0, 1 - (t - 1) / 0.8);
            coarse[idx] += (float)(height * v * (1 + 0.08 * detail.Noise(gx / 7, gz / 7 + seed)));
            if (t > 0.6 && t < 1.1) hard[idx] = 1;
        });
    }
}
