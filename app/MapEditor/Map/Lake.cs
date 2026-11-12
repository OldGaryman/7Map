using System.Windows;

namespace MapEditor.Map;

/// settings for a lake (the user's request, 2026-11-05; measurements in WASSER_ANALYSE.md)
public sealed class LakeSpec
{
    /// water level in meters (blue value in splat4.png); RWG worlds always use 30
    public int Level = 30;
    /// greatest depth below the level (m)
    public double Depth = 6;
    /// width of the shore (m): so far from the edge inwards the ground is lowered to the greatest depth
    public double Shore = 14;
    /// unevenness of the lake bed (0 none … 3 strong)
    public int Bumps = 1;
    public int Seed;
    /// biome under the lake (index in BiomePalette.Names), -1 = biome stays
    public int Biome = -1;
    /// outline in world coordinates
    public List<Point> Outline = new();

    public const double MinArea = 400;
    public const int MinLevel = 2, MaxLevel = 250;
}

/// result: cells with water (m²), changed height points, left-out cells (roads, buildings, tiles), mean terrain
/// in the area before, largest raise (embankment) and lowering, greatest depth
/// result "fill crater": water cells (m²), number of basins, lowest and highest level, too large to calculate
public sealed record CraterStats(int Water, int Craters, int MinLevel, int MaxLevel, bool TooHuge);

public sealed record LakeStats(int Water, int Changed, int Spared, double MeanGround, double MaxRaise, double MaxCut, double MaxDepth);

/// place a lake or remove water. Water exists only as a blue value in splat4.png (= height of the level); the terrain is the basin.
/// So a lake needs both: ground below the level (profile like the lakes of the world generation: flat at the shore, deepest after about 15–20 m)
/// and blue pixels. Outside, the shore runs out softly into the existing terrain; if the land is lower than the
/// level, a dam results. Roads, buildings and tiles stay untouched (the lake has a hole there).
public static class Lake
{
    const int Bank = 60;   // greatest width of the shore outside (m)
    // RWG: water pixels never lie higher than level − 0.4 m, the land at the shore at least at level − 0.4 m
    /// how far above the level ground still gets water (m). The game rounds the ground height to whole blocks (block = height + 0.5 rounded down);
    /// the topmost water block lies one step above the level and flows into every dry neighboring cell whose topmost block is at level
    /// or below, i.e. at height < level + 0.5. Such cells got, as "dry", the biome and plants of the land (grass, bushes,
    /// forest floor) in the middle of the water. Therefore they get water themselves (underwater biome, topmost block dirt, no land plants).
    /// Fixed 1 m (the user's request 2026-11-06, before 0.45 and adjustable): so far above the level ground still gets water.
    public const double WetMargin = 1.0;
    const double Rim = 0;

    /// area of the cells: frame around the outline (with margin) and mask of the cells inside
    static (int gx0, int gz0, int gw, int gh, bool[] inside) Grid(WorldData w, IReadOnlyList<Point> outline, int margin)
    {
        double ax = outline.Min(p => p.X), bx = outline.Max(p => p.X), az = outline.Min(p => p.Y), bz = outline.Max(p => p.Y);
        int gx0 = Math.Max(-w.Half + 2, (int)Math.Floor(ax) - margin), gz0 = Math.Max(-w.Half + 2, (int)Math.Floor(az) - margin);
        int gx1 = Math.Min(w.Half - 3, (int)Math.Ceiling(bx) + margin), gz1 = Math.Min(w.Half - 3, (int)Math.Ceiling(bz) + margin);
        int gw = gx1 - gx0 + 1, gh = gz1 - gz0 + 1;
        var inside = new bool[Math.Max(0, gw * gh)];
        if (gw < 3 || gh < 3) return (gx0, gz0, 0, 0, inside);
        var xs = new List<double>();
        for (int gz = 0; gz < gh; gz++)
        {
            double z = gz0 + gz + 0.5;
            xs.Clear();
            for (int i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                if ((a.Y <= z && b.Y > z) || (b.Y <= z && a.Y > z)) xs.Add(a.X + (z - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int x0 = Math.Max(0, (int)Math.Ceiling(xs[k] - gx0 - 0.5)), x1 = Math.Min(gw - 1, (int)Math.Floor(xs[k + 1] - gx0 - 0.5));
                for (int gx = x0; gx <= x1; gx++) inside[gz * gw + gx] = true;
            }
        }
        return (gx0, gz0, gw, gh, inside);
    }

    /// water level of an existing body of water in or at most 3 m beside the area (most frequent blue value); null = none.
    /// When extending a lake or connecting a river, the new water takes over this level, otherwise there would be steps in the water.
    public static int? ExistingLevel(WorldData w, IReadOnlyList<Point> outline)
    {
        var (gx0, gz0, gw, gh, inside) = Grid(w, outline, 4);
        if (gw == 0) return null;
        var near = GroundShaper.Distance(inside, gw, gh);   // distance to the area
        var count = new Dictionary<int, int>();
        for (int g = 0; g < gw * gh; g++)
        {
            if (!inside[g] && near[g] > 3) continue;
            byte v = w.WaterMask[w.Index(gx0 + g % gw, gz0 + g / gw)];
            if (v != 0) count[v] = count.GetValueOrDefault(v) + 1;
        }
        return count.Count == 0 ? null : count.OrderByDescending(k => k.Value).First().Key;
    }

    /// river band around a center line: Catmull-Rom, a point about every 3 m, left and right half the width each with slight noise
    public static List<Point> Corridor(IReadOnlyList<Point> line, double width, int seed, double step = 3, double irregular = 0)
    {
        var c = new List<Point>();
        for (int i = 0; i < line.Count - 1; i++)
        {
            Point p0 = line[Math.Max(0, i - 1)], p1 = line[i], p2 = line[i + 1], p3 = line[Math.Min(line.Count - 1, i + 2)];
            int steps = Math.Max(1, (int)Math.Ceiling((p2 - p1).Length / step));
            for (int s = 0; s < steps; s++)
            {
                double t = (double)s / steps, t2 = t * t, t3 = t2 * t;
                c.Add(new Point(
                    0.5 * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                    0.5 * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)));
            }
        }
        c.Add(line[^1]);
        var left = new List<Point>(); var right = new List<Point>();
        var rnd = new Random(seed);
        // irregular = share of the width: every shore point moves at most this far left or right (12 m width, 10 % = 1.2 m)
        double Jit() => irregular * width * (rnd.NextDouble() * 2 - 1);
        for (int i = 0; i < c.Count; i++)
        {
            var d = c[Math.Min(c.Count - 1, i + 1)] - c[Math.Max(0, i - 1)];
            if (d.Length < 1e-6) continue;
            d.Normalize();
            var nrm = new Vector(-d.Y, d.X);
            double half = width / 2;
            bool end = i == 0 || i == c.Count - 1;   // start and end stay straight
            left.Add(c[i] + nrm * Math.Max(1.5, half + (end ? 0 : Jit())));
            right.Add(c[i] - nrm * Math.Max(1.5, half + (end ? 0 : Jit())));
        }
        right.Reverse();
        left.AddRange(right);
        return left;
    }

    /// cells that stay untouched: road or surface, footprint of buildings, parts and tiles (+ 1 m)
    static bool[] Protected(WorldData w, int gx0, int gz0, int gw, int gh)
    {
        var p = new bool[gw * gh];
        for (int g = 0; g < p.Length; g++)
            if (w.RoadMask[w.Index(gx0 + g % gw, gz0 + g / gw)] != 0) p[g] = true;
        foreach (var b in w.Prefabs)
        {
            if (b.X - 1 >= gx0 + gw || b.X + b.SizeX + 1 <= gx0 || b.Z - 1 >= gz0 + gh || b.Z + b.SizeZ + 1 <= gz0) continue;
            for (int z = Math.Max(b.Z - 1, gz0); z <= Math.Min(b.Z + b.SizeZ, gz0 + gh - 1); z++)
                for (int x = Math.Max(b.X - 1, gx0); x <= Math.Min(b.X + b.SizeX, gx0 + gw - 1); x++)
                    p[(z - gz0) * gw + x - gx0] = true;
        }
        return p;
    }

    static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// place a lake: basin, shore, water pixels
    public static LakeStats Place(WorldData w, WorldEdits e, LakeSpec spec)
    {
        e.SkipWaterSync();
        var (gx0, gz0, gw, gh, inside) = Grid(w, spec.Outline, Bank + 2);
        if (gw == 0) return new LakeStats(0, 0, 0, 0, 0, 0, 0);
        var prot = Protected(w, gx0, gz0, gw, gh);
        int n = gw * gh;
        var notInside = new bool[n];
        for (int g = 0; g < n; g++) notInside[g] = !inside[g];
        var dIn = GroundShaper.Distance(notInside, gw, gh);   // inside: distance to the edge
        var dOut = GroundShaper.Distance(inside, gw, gh);     // outside: distance to the lake
        double level = spec.Level, shoreH = level + WetMargin + 0.15;   // shore lies above the waterline (level + 1 m): everything below it gets water
        // craggy (the user's request 2026-11-05): unevenness in meters, three noise sizes; shore and ground fall off steeper
        double rough = 0.35 + 0.55 * spec.Bumps;   // m: 0.35 … 2.0
        double Rugged(int x, int z, int seed) =>
            RoadApplier.Octave(x, z, 22, seed) * 0.6 + RoadApplier.Octave(x, z, 8, seed + 11) * 0.8 + RoadApplier.Octave(x, z, 3, seed + 23) * 0.5;
        int water = 0, changed = 0, spared = 0;
        double sumGround = 0, maxRaise = 0, maxCut = 0, maxDepth = 0;
        int counted = 0;
        for (int g = 0; g < n; g++)
        {
            int x = gx0 + g % gw, z = gz0 + g / gw;
            int hi = w.Index(x, z);
            double ex = w.Heights[hi] / 256.0;
            double target;
            if (inside[g])
            {
                if (prot[g]) { spared++; continue; }
                sumGround += ex; counted++;
                double d = Math.Max(0, dIn[g] - 0.5);
                double t = Math.Min(1, d / Math.Max(1, spec.Shore));
                // steep drop at the shore: cubic instead of quadratic, plus rock steps and noise in meters
                // spec.Depth = water depth: height of the water above the lowest point of the ground
                double depth = Math.Max(0.1, spec.Depth - Rim) * (1 - (1 - t) * (1 - t) * (1 - t));
                depth += rough * Rugged(x, z, spec.Seed + 7) * (0.25 + 0.75 * Math.Min(1, t * 3)) * (spec.Bumps > 0 ? 1 : 0);
                depth = Math.Max(0, depth);
                target = Math.Min(ex, level - Rim - depth);
                maxDepth = Math.Max(maxDepth, level - target);
                e.SetWater(hi, (byte)spec.Level);
                water++;
            }
            else
            {
                if (dOut[g] > Bank || prot[g] || w.WaterMask[hi] != 0) continue;
                // shore: from the edge (height just above the level) softly into the existing terrain; the greater the difference,
                // the wider the transition (a dam is also raised here if the land is lower)
                double diff = ex - shoreH;
                // craggier: shorter transition, width and height with noise, edge jumps irregularly
                double wn = 1 + 0.45 * RoadApplier.Octave(x, z, 30, spec.Seed + 3);
                double width = Math.Clamp(Math.Max(spec.Shore, 1.1 * Math.Abs(diff)) * wn, 4, Bank);   // at least the set shore width
                if (dOut[g] >= width) continue;
                double u = dOut[g] / width;
                target = shoreH + diff * Math.Pow(Smooth(u), 0.8);
                if (spec.Bumps > 0) target += rough * 0.9 * Rugged(x, z, spec.Seed + 5) * Math.Sin(Math.PI * Math.Min(1, u * 1.2));
                if (dOut[g] < 4) target = Math.Max(target, level + WetMargin + 0.1);   // directly at the water never below the waterline (otherwise flooded land with grass and bushes would remain)
            }
            var hv = (ushort)Math.Clamp(Math.Round(target * 256), 0, 65535);
            if (hv != w.Heights[hi])
            {
                e.SetHeight(hi, hv);
                changed++;
                double dz = target - ex;
                maxRaise = Math.Max(maxRaise, dz);
                maxCut = Math.Max(maxCut, -dz);
            }
        }
        e.PaintWaterBiome(spec.Biome);
        return new LakeStats(water, changed, spared, counted > 0 ? sumGround / counted : 0, maxRaise, maxCut, maxDepth);
    }

    const int CraterHardMax = 20_000_000;

    /// planned fill: per start point a level and the cells (world index) that get water
    public sealed class CraterPlan
    {
        public List<(int level, int[] cells)> Groups = new();
        public long Total;        // cells (m²) in total
        public bool TooHuge;      // area larger than CraterHardMax: not calculated
    }

    /// plan a crater. The click point has ground height g; the level lies "height" meters above (g + height, rounded to whole meters).
    /// From there everything connected is searched whose ground lies below the level: the water runs over every such cell and keeps filling
    /// behind it until it meets ground that is higher than the level. All cells with ground < level + 1 m (WetMargin) get water: the game turns every water pixel into underwater ground
    /// (topmost block dirt, biome "underwater"). Roads and buildings do not count as obstacles: the
    /// water rises there too, exactly like in the game.
    public static CraterPlan PlanCraters(WorldData w, IReadOnlyList<Point> seeds, double height)
    {
        var plan = new CraterPlan();
        int size = w.Size, half = w.Half;
        var mark = new byte[size * size];   // 1 = visited in this flooding, 2 = already water from this plan
        double minDepth = -WetMargin;   // cells up to 1 m above the level also get water (see WetMargin)
        int strokeLevel = 0;
        foreach (var s in seeds)
        {
            int sx = (int)Math.Floor(s.X), sz = (int)Math.Floor(s.Y);
            if (Math.Abs(sx) >= half - 3 || Math.Abs(sz) >= half - 3) continue;
            int si = w.Index(sx, sz);
            if (mark[si] == 2 || w.WaterMask[si] != 0) continue;
            double g = w.Heights[si] / 256.0;
            // one stroke = one level (that of the first point), otherwise water surfaces with different levels would stand side by side and run into each other
            if (strokeLevel == 0) strokeLevel = Math.Min(LakeSpec.MaxLevel, Math.Max((int)Math.Floor(g + minDepth) + 1, (int)Math.Round(g + height)));
            int level = strokeLevel;
            if (g >= (level - minDepth)) continue;   // this point lies higher than the water
            double wet = (level - minDepth) * 256.0, pass = wet;   // the water runs over everything that gets water itself
            var visited = new List<int> { si };
            var cells = new List<int>();
            mark[si] = 1;
            bool huge = false;
            for (int q = 0; q < visited.Count; q++)
            {
                int c = visited[q];
                if (w.Heights[c] < wet) cells.Add(c);
                int cz = c / size, cx = c % size;
                if (cx < 3 || cz < 3 || cx >= size - 3 || cz >= size - 3) continue;   // map edge
                if (visited.Count > CraterHardMax) { huge = true; break; }
                for (int d = 0; d < 4; d++)
                {
                    int n = c + (d == 0 ? 1 : d == 1 ? -1 : d == 2 ? size : -size);
                    if (mark[n] != 0 || w.Heights[n] >= pass) continue;
                    mark[n] = 1;
                    visited.Add(n);
                }
            }
            foreach (int c in visited) if (mark[c] == 1) mark[c] = 0;
            if (huge) { plan.TooHuge = true; return plan; }
            foreach (int c in cells) mark[c] = 2;
            plan.Groups.Add((level, cells.ToArray()));
            plan.Total += cells.Count;
        }
        return plan;
    }

    /// fill crater: plan and set the water pixels. Only splat4.png changes, the terrain stays.
    public static CraterStats FillCraters(WorldData w, WorldEdits e, IReadOnlyList<Point> seeds, double height, int biome = -1)
    {
        var plan = PlanCraters(w, seeds, height);
        if (plan.TooHuge) return new CraterStats(0, 0, 0, 0, true);
        int minL = int.MaxValue, maxL = 0;
        foreach (var (level, cells) in plan.Groups)
        {
            foreach (int c in cells) e.SetWater(c, (byte)level);
            minL = Math.Min(minL, level); maxL = Math.Max(maxL, level);
        }
        e.PaintWaterBiome(biome);
        return new CraterStats((int)plan.Total, plan.Groups.Count, plan.Groups.Count == 0 ? 0 : minL, maxL, false);
    }

    /// remove water in the area: only the water pixels (splat4.png) are deleted, the terrain stays exactly as it is
    public static LakeStats Remove(WorldData w, WorldEdits e, IReadOnlyList<Point> outline, int bumps)
    {
        e.SkipWaterSync();
        var (gx0, gz0, gw, gh, inside) = Grid(w, outline, 2);
        if (gw == 0) return new LakeStats(0, 0, 0, 0, 0, 0, 0);
        int count = 0;
        for (int g = 0; g < gw * gh; g++)
        {
            if (!inside[g]) continue;
            int i = w.Index(gx0 + g % gw, gz0 + g / gw);
            if (w.WaterMask[i] == 0) continue;
            e.SetWater(i, 0);
            count++;
        }
        return new LakeStats(count, 0, 0, 0, 0, 0, 0);
    }
}
