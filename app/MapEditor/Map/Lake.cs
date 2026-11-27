using System.Windows;

namespace MapEditor.Map;

/// settings for a lake (the user's request, 2026-11-05; measurements in WASSER_ANALYSE.md)
public sealed class LakeSpec
{
    /// water level in meters (blue value in splat4.png); RWG worlds always use 30
    public int Level = 30;
    /// greatest depth below the level (m)
    public double Depth = 6;
    /// width of the shore (m): mean distance from the waterline to the full depth (steep banks shorter, beaches longer)
    public double Shore = 14;
    /// kind of the shore: 0 mixed, 1 mostly beaches, 2 mostly steep
    public int ShoreStyle;
    /// build a low, wide embankment where the land beside the lake is lower than the waterline (otherwise the water would run out)
    public bool Dam;
    /// unevenness of the lake bed (0 none … 3 strong)
    public int Bumps = 1;
    /// shapes on the bed of the lake: 0 none, 1 some, 2 many (small cliffs, cracks, rock groups; more in larger lakes)
    public int BedFeatures = 1;
    /// an island in the lake (one side beach, the other a steep wall), if the lake is large enough
    public bool Island;
    public int Seed;
    /// biome under the lake (index in BiomePalette.Names), -1 = biome stays
    public int Biome = -1;
    /// outline in world coordinates
    public List<Point> Outline = new();

    public const double MinArea = 400;
    public const int MinLevel = 2, MaxLevel = 250;

    public LakeSpec Clone() { var c = (LakeSpec)MemberwiseClone(); c.Outline = new List<Point>(Outline); return c; }
}

/// result: cells with water (m²), changed height points, left-out cells (roads, buildings, tiles), mean terrain
/// in the area before, largest raise (embankment) and lowering, greatest depth
/// result "fill crater": water cells (m²), number of basins, lowest and highest level, too large to calculate
public sealed record CraterStats(int Water, int Craters, int MinLevel, int MaxLevel, bool TooHuge);

public sealed record LakeStats(int Water, int Changed, int Spared, double MeanGround, double MaxRaise, double MaxCut, double MaxDepth, int Leak = 0, int Spread = 0, int Cliffs = 0, int Cracks = 0, int Rocks = 0, bool Island = false);

/// planned lake (before anything changes; also the live preview): frame, new heights and water cells
public sealed class LakePlan
{
    public int Gx0, Gz0, Gw, Gh;
    public float[] Target = Array.Empty<float>();   // new height, NaN = stays
    public byte[] Cells = Array.Empty<byte>();      // 1 = water, 2 = water would run out here (beyond the shore margin), 0 = dry
    public int Water, Leak, Spread, Spared;          // m² water, m² where it runs out, m² water outside the drawn outline, m² roads/buildings left alone
    public int Cliffs, Cracks, Rocks;                // shapes on the bed
    public bool HasIsland, IslandTooSmall;           // island placed; asked for but the lake is too small
    public double MeanGround, MaxDepth, MaxRaise, MaxCut;
}

/// place a lake or remove water. Water exists only as a blue value in splat4.png (= height of the level); the terrain is the basin.
/// So a lake needs both: ground below the level (profile like the lakes of the world generation: flat at the shore, deepest after about 15–20 m)
/// and blue pixels. Outside, the shore runs out softly into the existing terrain; if the land is lower than the
/// level, a dam results. Roads, buildings and tiles stay untouched (the lake has a hole there).
public static partial class Lake
{
    const int Bank = 60;   // greatest width of the shore outside (m)
    // RWG: water pixels never lie higher than level − 0.4 m, the land at the shore at least at level − 0.4 m
    /// how far above the level ground still gets water (m). The game rounds the ground height to whole blocks (block = height + 0.5 rounded down);
    /// the topmost water block lies one step above the level and flows into every dry neighboring cell whose topmost block is at level
    /// or below, i.e. at height < level + 0.5. Such cells got, as "dry", the biome and plants of the land (grass, bushes,
    /// forest floor) in the middle of the water. Therefore they get water themselves (underwater biome, topmost block dirt, no land plants).
    /// Fixed 1 m (the user's request 2026-11-06, before 0.45 and adjustable): so far above the level ground still gets water.
    public const double WetMargin = 1.0;

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

    /// kind of shore at a place (like WorldGenerator.Carve): 0 = steep bank … 1 = beach; changes slowly along the shore (about 150 m)
    internal static double ShoreKind(int x, int z, int seed)
    {
        double n = 0.8 * RoadApplier.Octave(x, z, 140, seed + 41) + 0.3 * RoadApplier.Octave(x, z, 55, seed + 43);
        double t = Math.Clamp((n + 0.35) / 0.7, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// shore kind with the chosen style: 0 mixed, 1 mostly beaches, 2 mostly steep
    static double StyledKind(double k, int style) => style == 1 ? 0.45 + 0.55 * k : style == 2 ? 0.55 * k : k;

    /// up to so far from the outline (m) the distance is measured exactly to its segments (the whole shaped shore: cut slopes up to Bank m
    /// outside showed stripes with the grid distance); further inside the lake the grid distance is enough
    const double ExactBand = Bank + 4;

    /// segments of a closed outline in buckets of 16 m, for the exact distance of a point to the outline (2026-11-26, the user's photo: the
    /// distance on the 1 m grid made stairs along slanted shores – the ground near the waterline was 3.6 times as rough as at a generator lake)
    sealed class EdgeIndex
    {
        const double B = 16;
        readonly Dictionary<(int, int), List<(Point A, Point C)>> buckets = new();

        public EdgeIndex(IReadOnlyList<Point> outline)
        {
            for (int i = 0; i < outline.Count; i++)
            {
                var a = outline[i]; var c = outline[(i + 1) % outline.Count];
                int i0 = (int)Math.Floor(Math.Min(a.X, c.X) / B), i1 = (int)Math.Floor(Math.Max(a.X, c.X) / B);
                int j0 = (int)Math.Floor(Math.Min(a.Y, c.Y) / B), j1 = (int)Math.Floor(Math.Max(a.Y, c.Y) / B);
                for (int j = j0; j <= j1; j++)
                    for (int k = i0; k <= i1; k++)
                    {
                        if (!buckets.TryGetValue((k, j), out var list)) buckets[(k, j)] = list = new();
                        list.Add((a, c));
                    }
            }
        }

        /// distance of (px, pz) to the outline; approx = a distance that is not much smaller than the real one (limits the search)
        public double Distance(double px, double pz, double approx)
        {
            double r = approx * 1.15 + 4, best = double.MaxValue;
            int i0 = (int)Math.Floor((px - r) / B), i1 = (int)Math.Floor((px + r) / B);
            int j0 = (int)Math.Floor((pz - r) / B), j1 = (int)Math.Floor((pz + r) / B);
            for (int j = j0; j <= j1; j++)
                for (int k = i0; k <= i1; k++)
                {
                    if (!buckets.TryGetValue((k, j), out var list)) continue;
                    foreach (var (a, c) in list)
                    {
                        double dx = c.X - a.X, dz = c.Y - a.Y, len2 = dx * dx + dz * dz;
                        double t = len2 > 0 ? Math.Clamp(((px - a.X) * dx + (pz - a.Y) * dz) / len2, 0, 1) : 0;
                        double ex = px - a.X - t * dx, ez = pz - a.Y - t * dz, d2 = ex * ex + ez * ez;
                        if (d2 < best) best = d2;
                    }
                }
            return best == double.MaxValue ? approx : Math.Sqrt(best);
        }
    }

    /// smallest slope of the ground where it passes the waterline (from WaterlineBand m below up to 1.5 m beside it), like WorldGenerator.BankSlope
    const double BankMin = 0.30, WaterlineBand = 1.3;

    /// profile of the bank as a share of the whole drop (u = slope × distance / drop): straight through the waterline, rounded
    /// into the lake bed (slope continuous, no edge)
    static double BankCurve(double u)
    {
        if (u <= 0.7) return u;
        if (u >= 1.3) return 1;
        double v = (u - 0.7) / 0.6;
        return 0.7 + 0.3 * (1 - (1 - v) * (1 - v));
    }

    /// Plan a lake (2026-11-26, new tool after the user's photos: the shore was a wall of 1.5–2.5 m with a zigzag of the pixel
    /// steps, and a flat ring lay at the waterline). The shore is now a contour line: the ground passes the waterline on a slope
    /// (beach 7–14 %, bank about (1 m + depth) / shore width, steep bank 90–200 %); the drawn outline is where this slope crosses
    /// the waterline (frayed with noise), and every cell below the waterline that is connected with the lake gets water (flooding,
    /// like "Fill craters"). Where the land beside the lake is lower than the waterline the water follows it up to a margin; beyond
    /// that it would run out (Leak) – then only with a low, wide embankment (spec.Dam). Roads, buildings and tiles stay untouched.
    public static LakePlan Plan(WorldData w, LakeSpec spec, CancellationToken cancel = default)
    {
        var plan = new LakePlan();
        var (gx0, gz0, gw, gh, inside) = Grid(w, spec.Outline, Bank + 2);
        plan.Gx0 = gx0; plan.Gz0 = gz0; plan.Gw = gw; plan.Gh = gh;
        if (gw == 0) return plan;
        int n = gw * gh;
        var prot = Protected(w, gx0, gz0, gw, gh);
        var notInside = new bool[n];
        for (int g = 0; g < n; g++) notInside[g] = !inside[g];
        var dIn = GroundShaper.Distance(notInside, gw, gh);
        var dOut = GroundShaper.Distance(inside, gw, gh);
        var edges = new EdgeIndex(spec.Outline);
        double maxIn = 1;
        for (int g = 0; g < n; g++) if (inside[g]) maxIn = Math.Max(maxIn, dIn[g]);
        cancel.ThrowIfCancellationRequested();
        double level = spec.Level, E = level + WetMargin;
        double depth = Math.Max(0.5, spec.Depth), shore = Math.Max(2, spec.Shore);
        double rough = spec.Bumps <= 0 ? 0 : 0.35 + 0.55 * spec.Bumps;
        double fray = Math.Min(4, 0.08 * maxIn);   // small lakes: the outline stays closer to the drawing
        double sHang = Math.Clamp((WetMargin + depth) / shore, 0.12, 1.0);
        double sBeach = Math.Max(0.07, sHang * 0.4), sSteep = Math.Clamp(sHang * 3, 0.8, 1.0);   // steep banks at most 100 % (like the generator; 200 % gave jagged rock walls in the game)
        double margin = Math.Max(12, shore * 0.5 + 8);   // so far beyond the outline the water may follow lower land
        int seed = spec.Seed;
        double bigLake = Math.Clamp((maxIn - 25) / 120, 0, 1);       // 0 small lake … 1 from about 145 m between shore and middle
        double bedScale = Math.Clamp(maxIn * 1.3, 90, 450);           // size of the hollows and shoals of the bed (m)
        var (features, island, islandTooSmall) = PlaceBedFeatures(spec, inside, dIn, gx0, gz0, gw, maxIn);
        plan.Cliffs = features.Count(f => f.Kind == 0); plan.Cracks = features.Count(f => f.Kind == 1); plan.Rocks = features.Count(f => f.Kind == 2);
        plan.HasIsland = island != null; plan.IslandTooSmall = islandTooSmall;
        var target = new float[n];
        var signed = new float[n];
        var heights = w.Heights;
        Parallel.For(0, gh, gz =>
        {
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx, x = gx0 + gx, z = gz0 + gz;
                double h = heights[w.Index(x, z)] / 256.0;
                double d = inside[g] ? dIn[g] - 0.5 : -(dOut[g] - 0.5);
                if (Math.Abs(d) < ExactBand) d = edges.Distance(x + 0.5, z + 0.5, Math.Abs(d)) * (inside[g] ? 1 : -1);   // near the shore exactly (the grid distance makes stairs)
                d += fray * (0.6 * RoadApplier.Octave(x, z, 45, seed + 61) + 0.3 * RoadApplier.Octave(x, z, 16, seed + 63) + 0.1 * RoadApplier.Octave(x, z, 6, seed + 65));
                signed[g] = (float)d;
                target[g] = float.NaN;
                if (prot[g]) continue;
                double k = StyledKind(ShoreKind(x, z, seed), spec.ShoreStyle);
                double s = k < 0.5 ? sSteep + (sHang - sSteep) * Smooth(k * 2) : sHang + (sBeach - sHang) * Smooth((k - 0.5) * 2);
                // lake bed (2026-11-26, the user's photo from the bottom of a large lake: everything even): the larger the lake, the more
                // variety – deeper towards the middle, wide hollows, deeper channels, single shoals that rise towards the surface
                double center = Smooth(Math.Max(0, d) / maxIn);   // 0 at the shore … 1 in the middle
                double relief = 0.6 * RoadApplier.Octave(x, z, bedScale, seed + 71) + 0.4 * RoadApplier.Octave(x, z, bedScale / 2.7, seed + 73);
                double channel = Math.Pow(1 - Math.Abs(RoadApplier.Octave(x, z, bedScale * 0.8, seed + 75)), 4);
                double shoal = Math.Max(0, RoadApplier.Octave(x, z, bedScale * 0.6, seed + 77) - 0.45) / 0.55;
                double deep = depth * (0.8 + 0.4 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 70, seed + 3)));
                deep *= 1 + bigLake * (0.9 * (center - 0.4) + 0.8 * relief + 0.6 * channel * center);
                deep *= 1 - bigLake * 0.75 * shoal * Smooth((center - 0.2) / 0.4);
                // relief of the whole bed like the land of the world generator, at a smaller scale (2026-11-26, the user's photo from the bottom:
                // still boring): several noise layers plus ridges (ridged noise like mountains), in proportion to the water depth; the set depth is a
                // guide – the deepest places may reach twice of it
                double fbm = 0.5 * RoadApplier.Octave(x, z, 90, seed + 83) + 0.3 * RoadApplier.Octave(x, z, 35, seed + 85) + 0.2 * RoadApplier.Octave(x, z, 14, seed + 87);
                double ridge = 1 - Math.Abs(RoadApplier.Octave(x, z, 55, seed + 81));
                ridge *= ridge;
                double gully = Math.Pow(1 - Math.Abs(RoadApplier.Octave(x, z, 70, seed + 89)), 6);   // narrow deeper gullies
                deep += depth * (0.55 * fbm - 0.45 * (ridge - 0.45) + 0.5 * gully) * (0.5 + 0.5 * bigLake) * Smooth(d / 25);
                deep = Math.Clamp(deep, 0.8, Math.Min(2 * depth, level - 1));   // never below height 1 (the bottom of the map)
                double drop = WetMargin + deep;   // from the waterline down to the bed
                s = Math.Max(s, drop / (0.6 * maxIn));   // small lakes: steeper, so that the bed is reached
                // below the water at most 60 % (a steeper bank would again be a wall of blocks at the waterline)
                double si = Math.Min(s, Math.Max(0.6, drop / (0.6 * maxIn)));
                double wb = 2 + 18 * k, up = Math.Clamp(s, 0.6, 1.0), sw = Math.Clamp(s, BankMin, 0.5);
                double Profile(double dd)
                {
                    if (dd > 0)
                    {
                        // at the waterline at least BankMin (the user's photo 2026-11-26: on beaches with 7–14 % the strip with water pixels but no
                        // visible water was up to 14 m wide, dirt instead of grass, its edge in the 1 m grid with right angles; the generator has the
                        // same rule since 2026-11-11): the first WaterlineBand m below the waterline steeper, then the gentle beach goes on
                        double s2 = Math.Max(si, BankMin), rest = Math.Max(0.2, drop - WaterlineBand);
                        return E - (s2 * dd <= WaterlineBand ? s2 * dd : WaterlineBand + rest * BankCurve(si * (dd - WaterlineBand / s2) / rest));
                    }
                    // above the waterline: the first 1.5 m 30–50 %, then the bank (beaches gentle for a while, then like a natural hillside)
                    double a2 = -dd;
                    return E + sw * Math.Min(a2, 1.5) + s * Math.Clamp(a2 - 1.5, 0, wb) + up * Math.Max(0, a2 - 1.5 - wb);
                }
                // the profile across the shore averaged over ±2 m (2026-11-26, the user's photo: where the slope changed within one or two meters
                // at the waterline, the 1 m grid turned the bend into teeth along slanted shores)
                double T0 = (Profile(d - 2) + 2 * Profile(d - 1) + 3 * Profile(d) + 2 * Profile(d + 1) + Profile(d + 2)) / 9;
                double T;
                if (d > 0)
                {
                    T = T0;
                    if (rough > 0) T += rough * Rugged(x, z, seed + 7) * Smooth(((E - T0) / drop - 0.8) / 0.6);   // unevenness only on the bed, the waterline stays clean
                    if (features.Count > 0)
                    {
                        // shapes on the bed (cliffs, cracks, rocks), faded in from 12 m off the shore; what rises stays at least 0.6 m under the level
                        double fd = 0;
                        foreach (var f in features) fd += f.Delta(x + 0.5, z + 0.5, x, z);
                        fd *= Smooth((d - 12) / 10);
                        double before = T;
                        T += fd;
                        T = Math.Max(T, Math.Max(1, level - 2 * depth));   // the set depth is a guide: at most twice as deep, never below height 1
                        if (fd > 0) T = Math.Min(T, Math.Max(before, level - 0.6));
                    }
                    T = Math.Min(h, T);
                    if (island != null && island.Height(x + 0.5, z + 0.5, x, z) is double ih && !double.IsNaN(ih) && E + ih > T) T = E + ih;
                }
                else
                {
                    // higher land is cut back to the slope, lower land stays (the water follows it, see Leak)
                    double a = -d;
                    double U = T0;
                    if (rough > 0 && a > 3) U += 0.4 * rough * Rugged(x, z, seed + 5) * Math.Min(1, (a - 3) / 6);
                    T = Math.Min(h, U);
                    T = h + (T - h) * (1 - Smooth((a - (Bank - 20)) / 20));   // the last 20 m run out into the land
                    if (spec.Dam && a < Bank)
                    {
                        // embankment (2026-11-26, the user's photo: the first one, 0.4–0.9 m above the waterline over 6 m, was too low – from afar the
                        // water showed over the land behind it, like with rivers at Sodecu-Land): rises within 3 m to 1.5 m above the waterline
                        // (level + 2.5 m), crest 10 m wide, then down 1 : 3 (with a little noise) until it meets the land; not faded out
                        double D = E + Math.Min(1.5, 0.35 + 0.4 * a) - Math.Max(0, a - 10) / 3 + (a > 10 ? 0.3 * RoadApplier.Octave(x, z, 11, seed + 17) * Math.Min(1, (a - 10) / 4) : 0);
                        if (D > T) T = D;
                    }
                }
                if (Math.Abs(T - h) >= 0.004) target[g] = (float)T;
            }
        });
        cancel.ThrowIfCancellationRequested();
        // flooding: water in every cell below the waterline that is connected with the lake
        double F(int g) { float t = target[g]; return float.IsNaN(t) ? heights[w.Index(gx0 + g % gw, gz0 + g / gw)] / 256.0 : t; }
        var cells = new byte[n];
        var queue = new Queue<int>();
        for (int g = 0; g < n; g++)
            if (signed[g] > 0 && !prot[g] && F(g) < E) { cells[g] = 1; queue.Enqueue(g); }
        while (queue.Count > 0)
        {
            int g = queue.Dequeue();
            int gx = g % gw, gz = g / gw;
            if (cells[g] == 2) continue;
            for (int k = 0; k < 4; k++)
            {
                int nx = gx + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = gz + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (nx < 0 || nz < 0 || nx >= gw || nz >= gh) continue;
                int ng = nz * gw + nx;
                if (cells[ng] != 0 || prot[ng] || F(ng) >= E) continue;
                bool edge = nx == 0 || nz == 0 || nx == gw - 1 || nz == gh - 1;
                cells[ng] = (byte)(signed[ng] < -margin || edge ? 2 : 1);
                queue.Enqueue(ng);
            }
        }
        double sum = 0; int count = 0;
        for (int g = 0; g < n; g++)
        {
            double h = heights[w.Index(gx0 + g % gw, gz0 + g / gw)] / 256.0;
            if (inside[g]) { if (prot[g]) plan.Spared++; else { sum += h; count++; } }
            if (cells[g] == 1)
            {
                plan.Water++;
                if (signed[g] <= 0) plan.Spread++;
                plan.MaxDepth = Math.Max(plan.MaxDepth, level - F(g));
            }
            else if (cells[g] == 2) plan.Leak++;
            if (!float.IsNaN(target[g]))
            {
                plan.MaxRaise = Math.Max(plan.MaxRaise, target[g] - h);
                plan.MaxCut = Math.Max(plan.MaxCut, h - target[g]);
            }
        }
        plan.MeanGround = count > 0 ? sum / count : 0;
        plan.Target = target;
        plan.Cells = cells;
        return plan;
    }

    /// place a lake: basin, shore, water pixels (see Plan)
    public static LakeStats Place(WorldData w, WorldEdits e, LakeSpec spec)
    {
        e.SkipWaterSync();
        var plan = Plan(w, spec);
        if (plan.Leak > 0 && !spec.Dam)
        {
            // the water would run out (the tool asks before; other callers get the embankment like the earlier lake tool):
            // never leave dry land below the waterline beside the water
            var dammed = spec.Clone();
            dammed.Dam = true;
            plan = Plan(w, dammed);
        }
        int changed = 0;
        int wetLine = (int)Math.Round((spec.Level + WetMargin) * 256);   // heights are stored in steps of 1/256 m: rounding must not move a cell across the waterline
        for (int g = 0; g < plan.Target.Length; g++)
        {
            int i = w.Index(plan.Gx0 + g % plan.Gw, plan.Gz0 + g / plan.Gw);
            float t = plan.Target[g];
            if (!float.IsNaN(t))
            {
                int r = (int)Math.Round(t * 256);
                if (plan.Cells[g] == 1) r = Math.Min(r, wetLine - 1);
                else if (t >= spec.Level + WetMargin) r = Math.Max(r, wetLine);
                var hv = (ushort)Math.Clamp(r, 0, 65535);
                if (hv != w.Heights[i]) { e.SetHeight(i, hv); changed++; }
            }
            if (plan.Cells[g] == 1 && w.WaterMask[i] != spec.Level) e.SetWater(i, (byte)spec.Level);
        }
        // dry land in the 16 m chunks of the lake above the far water plane of the game (see FarWater)
        changed += FarWater.RaiseBanks(w, e, plan.Gx0, plan.Gz0, plan.Gx0 + plan.Gw - 1, plan.Gz0 + plan.Gh - 1);
        e.PaintWaterBiome(spec.Biome);
        return new LakeStats(plan.Water, changed, plan.Spared, plan.MeanGround, plan.MaxRaise, plan.MaxCut, plan.MaxDepth, plan.Leak, plan.Spread, plan.Cliffs, plan.Cracks, plan.Rocks, plan.HasIsland);
    }

    /// water level for a drawn outline: the shore lies on the lower part of the land around it (15 % of the ground in a 4 m strip
    /// outside is lower), then lower step by step until the water does not run out (at most 30 m)
    public static int SuggestLevel(WorldData w, LakeSpec spec, CancellationToken cancel = default)
    {
        var (gx0, gz0, gw, gh, inside) = Grid(w, spec.Outline, 6);
        if (gw == 0) return spec.Level;
        var dOut = GroundShaper.Distance(inside, gw, gh);
        var ring = new List<double>();
        for (int g = 0; g < gw * gh; g++)
            if (!inside[g] && dOut[g] <= 4) ring.Add(w.Heights[w.Index(gx0 + g % gw, gz0 + g / gw)] / 256.0);
        if (ring.Count == 0) return spec.Level;
        ring.Sort();
        int level = Math.Clamp((int)Math.Floor(ring[(int)(ring.Count * 0.15)] - WetMargin), LakeSpec.MinLevel, LakeSpec.MaxLevel);
        var probe = spec.Clone();
        probe.Dam = false;
        for (int k = 0; k < 30 && level > LakeSpec.MinLevel; k++)
        {
            probe.Level = level;
            if (Plan(w, probe, cancel).Leak == 0) break;
            level--;
        }
        return level;
    }

    static double Rugged(int x, int z, int seed) =>
        RoadApplier.Octave(x, z, 22, seed) * 0.6 + RoadApplier.Octave(x, z, 8, seed + 11) * 0.8 + RoadApplier.Octave(x, z, 3, seed + 23) * 0.5;

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
