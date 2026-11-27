using System.Windows;

namespace MapEditor.Map;

/// Rivers between two lakes (FLUSS_ANALYSE.md, plan of 2026-11-18, steps 1–5; the user's request 2026-11-19).
/// In the game every water is flat, so a river is a horizontal channel: both lakes and the river get one common level (the lower
/// one), the higher lake is dug deeper (at most River.MaxLower m, no dams). Pairs: existing lakes 600–2,200 m apart (edge to edge)
/// with levels at most 8 m apart; if there are not enough, a partner lake is placed near a lake (hollow 800–2,500 m away).
/// The path is searched like in the tool "Connect lakes" (River.Plan), the valley has the same shape as River.Build,
/// but everything works on the coarse grid of the generator before the full resolution, so the preview shows the rivers too.
public sealed partial class WorldGenerator
{
    public sealed record RiverInfo(int LakeA, int LakeB, int Level, double Length, double MaxCut, List<Point> Line);
    public readonly List<RiverInfo> RiverList = new();

    /// test output of the river placement
    public static Action<string> RiverLog;
    bool[] nearRiver;   // plan cells near a river valley (other rivers keep away)

    const double RiverEdge = 150;   // m from the map edge
    const double RiverMaxCut = 30;  // deepest cut of the valley (ground above the bank)
    const double MinRiverLake = 70; // smallest lake (radius, m) at a river
    bool relax;   // second round when no river came about: relaxed limits
    double MinLake => relax ? 50 : MinRiverLake;
    double MaxCut => relax ? 40 : RiverMaxCut;
    double MaxRim => relax ? 24 : 16;

    void PlaceRivers()
    {
        RiverList.Clear();
        nearRiver = null;
        if (spec.Rivers <= 0 || spec.Lakes <= 0) return;
        if (planGrid == null) BuildPlanGrid();
        nearRiver = new bool[planN * planN];   // on the plan grid (the same in the preview and in the world)
        planLevel.Clear();
        // 1 / 2 / 3 rivers per 64 km² (8k map), at least one
        int want = Math.Max(1, (int)Math.Round((double)size * size / 64e6 * Math.Min(spec.Rivers, 3)));
        var rr = new Random(spec.SeedValue + 133);
        var used = new Dictionary<int, int>();   // lake number (1-based) → number of rivers at it (a lake with one river can get a second one: river chains)
        int Used(int k) => used.TryGetValue(k, out int c) ? c : 0;
        void Use(int k) => used[k] = Used(k) + 1;
        rejects.Clear();
        rootOf.Clear();
        for (int k = 0; k <= LakeList.Count; k++) rootOf.Add(k);
        int searches = 0, maxSearches = want * 8 + 6;

        // 1. pairs of existing lakes
        var pairs = new List<(int a, int b, double score)>();
        for (int i = 0; i < LakeList.Count; i++)
            for (int j = i + 1; j < LakeList.Count; j++)
            {
                var a = LakeList[i]; var b = LakeList[j];
                if (a.Radius < MinRiverLake || b.Radius < MinRiverLake) continue;   // a river between two ponds looks odd
                double gap = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z)) - a.Radius - b.Radius;
                int dl = Math.Abs(PlanLevelOf(i + 1) - PlanLevelOf(j + 1));
                if (gap < 600 || gap > 3500 || dl > River.MaxLower) continue;
                pairs.Add((i + 1, j + 1, gap + 80 * dl + 300 * rr.NextDouble()));
            }
        RiverLog?.Invoke($"rivers: want {want}, lakes {LakeList.Count}, pairs {pairs.Count}");
        foreach (var (a, b, _) in pairs.OrderBy(p => p.score))
        {
            if (RiverList.Count >= want || searches >= maxSearches) break;
            if (Used(a) > 0 || Used(b) > 0) continue;
            searches++;
            bool ok = TryRiver(a, b, rr);
            RiverLog?.Invoke($"  pair {a}-{b}: {(ok ? "river" : "no path")}");
            if (ok) { Use(a); Use(b); }
        }

        // 2. partner lakes: a new lake in a hollow 800–3,800 m from a lake with at most one river (then the river system grows longer:
        // lake – river – lake – river – lake), level at most 8 m apart and not below a lake that already has a river
        // (lakes that already have a river come first, so that a second river makes the system longer)
        double margin = Math.Max(RiverEdge + 100, Math.Min(500, size * 0.1));
        relax = false;
        var jitter = new Dictionary<int, double>();
        double Jitter(int k) { if (!jitter.TryGetValue(k, out double j)) jitter[k] = j = 60 * rr.NextDouble(); return j; }
        for (int round = 0; round < 2; round++)
        {
        if (round == 1)
        {
            // 4. no river at all although rivers were chosen (the user's request: at least one): once more with relaxed limits
            // (lakes closer together, smaller lakes, steeper surroundings, deeper cut, higher dams)
            if (RiverList.Count > 0) break;
            relax = true;
            maxSearches = searches + 24;
            RiverLog?.Invoke("  no river yet: relaxed limits");
        }
        var tried = new HashSet<int>();
        while (RiverList.Count < want && searches < maxSearches)
        {
            int a = Enumerable.Range(1, LakeList.Count).Where(k => !tried.Contains(k) && Used(k) <= 1 && LakeList[k - 1].Radius >= MinLake)
                .OrderByDescending(k => LakeList[k - 1].Radius + 150 * Used(k) + Jitter(k)).FirstOrDefault();
            if (a == 0) break;
            tried.Add(a);
            int b = PartnerLake(a, margin, rr, Used(a) > 0);   // one partner per lake (if the path fails, the new lake simply stays a lake)
            if (b == 0) continue;
            searches++;
            bool ok = TryRiver(a, b, rr);
            RiverLog?.Invoke($"  partner {a}-{b}: {(ok ? "river" : "no path")}");
            if (ok) { Use(a); Use(b); }
        }

        RiverLog?.Invoke("  rejected partner lakes: " + string.Join(", ", rejects.Select(r => $"{r.Key} {r.Value}")));
        // 3. still too few (few large lakes): a new pair of lakes, both in hollows
        for (int attempt = 0; attempt < (relax ? 200 : 60) && RiverList.Count < want && searches < maxSearches; attempt++)
        {
            double cx = margin + rr.NextDouble() * (size - 2 * margin), cz = margin + rr.NextDouble() * (size - 2 * margin);
            int a = NewRiverLake(cx, cz, margin, rr, -1);
            if (a == 0) continue;
            int b = PartnerLake(a, margin, rr, false);
            if (b == 0) continue;
            searches++;
            bool ok = TryRiver(a, b, rr);
            RiverLog?.Invoke($"  new pair {a}-{b}: {(ok ? "river" : "no path")}");
            if (ok) { Use(a); Use(b); }
        }
        }
        RiverLog?.Invoke("  rejected lakes in all: " + string.Join(", ", rejects.Select(r => $"{r.Key} {r.Value}")));
    }

    /// a new lake 600–3,500 m (edge to edge) from lake a with a level at most River.MaxLower apart (notBelow: not lower than lake a,
    /// because lake a already belongs to a river system with this level); 0 = none found
    int PartnerLake(int a, double margin, Random rr, bool notBelow)
    {
        var la = LakeList[a - 1];
        for (int attempt = 0; attempt < 40; attempt++)
        {
            double ang = rr.NextDouble() * Math.PI * 2, dist = la.Radius + (relax ? 500 : 800) + rr.NextDouble() * Math.Min(relax ? 1500 : 2800, size * (relax ? 0.2 : 0.3));   // small maps: shorter, otherwise mostly outside
            int b = NewRiverLake(la.X + Math.Cos(ang) * dist, la.Z + Math.Sin(ang) * dist, margin, rr, a, notBelow);
            if (b != 0) return b;
        }
        return 0;
    }

    /// a lake for a river in the hollow below the point (radius 80–250 m, not in steep mountains); near = partner lake or −1; 0 = no room
    int NewRiverLake(double cx, double cz, double margin, Random rr, int near, bool notBelow = false)
    {
        double radius = MinRiverLake + 10 + Math.Pow(rr.NextDouble(), 1.4) * 170;
        double depth = 2.5 + rr.NextDouble() * 6;
        var shape = LakeShape.Random(rr, radius);
        if (cx < margin || cz < margin || cx > size - margin || cz > size - margin) { Reject("edge"); return 0; }
        (cx, cz) = Downhill(cx, cz, margin);
        if (near > 0)
        {
            var la = LakeList[near - 1];
            double d = Math.Sqrt((cx - la.X) * (cx - la.X) + (cz - la.Z) * (cz - la.Z)) - la.Radius - radius;
            if (d < (relax ? 400 : 600) || d > 3500) { Reject("dist"); return 0; }
        }
        if (OverlapsVolcano(cx, cz, radius)) { Reject("anomaly"); return 0; }
        if (LakeList.Any(l => Math.Sqrt((l.X - cx) * (l.X - cx) + (l.Z - cz) * (l.Z - cz)) < l.Radius * 1.5 + radius * 1.5 + 250)) { Reject("spacing"); return 0; }
        if (NearRiver(cx, cz, radius * 2 + 60)) return 0;
        // decisions on the plan terrain (the same in the preview and in the world), the real level from the finished terrain
        var (min, max) = PlanRim(cx, cz, shape);
        if (max - min > MaxRim && radius * 0.7 >= MinLake) { shape = shape.Scaled(0.7); (min, max) = PlanRim(cx, cz, shape); }
        if (max - min > MaxRim) { Reject("rim"); return 0; }   // no small mountain lake at a river (plan terrain is rougher than the eroded one)
        if (OverlapsAnomaly(cx, cz, shape)) { Reject("anomaly"); return 0; }
        int plevel = (int)Math.Floor(min) - 1;
        if (near > 0 && Math.Abs(plevel - PlanLevelOf(near)) > River.MaxLower) Reject("level");
        if (plevel < 3 || plevel > 235 || (near > 0 && (Math.Abs(plevel - PlanLevelOf(near)) > River.MaxLower || (notBelow && plevel < PlanLevelOf(near))))) return 0;
        int level = LakeLevel(cx, cz, shape, plevel);
        LakeList.Add(new LakeInfo(cx, cz, shape.Radius, level, depth));
        Carve(LakeList.Count, cx, cz, shape, level, depth, rr);
        rootOf.Add(LakeList.Count);
        planLevel[LakeList.Count] = plevel;
        return LakeList.Count;
    }

    readonly Dictionary<string, int> rejects = new();
    void Reject(string why) { if (RiverLog != null) rejects[why] = rejects.GetValueOrDefault(why) + 1; }

    bool NearRiver(double gx, double gz, double r)
    {
        if (nearRiver == null) return false;
        int i0 = Math.Max(0, (int)((gx - r) / PlanCell)), i1 = Math.Min(planN - 1, (int)((gx + r) / PlanCell) + 1);
        int j0 = Math.Max(0, (int)((gz - r) / PlanCell)), j1 = Math.Min(planN - 1, (int)((gz + r) / PlanCell) + 1);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
                if (nearRiver[j * planN + i]) return true;
        return false;
    }

    bool PlanNear(double gx, double gz) => nearRiver[Math.Clamp((int)Math.Round(gz / PlanCell), 0, planN - 1) * planN + Math.Clamp((int)Math.Round(gx / PlanCell), 0, planN - 1)];

    // outline of every lake (Carve), for the lake membership in the river search
    readonly Dictionary<int, (double X, double Z, LakeShape Shape, int Seed)> lakeShapes = new();

    bool InLake(int k, double gx, double gz) => lakeShapes.TryGetValue(k, out var s) && LakeEdgeDistance(s.X, s.Z, s.Shape, s.Seed, gx, gz) > 4;

    // plan terrain: the landscape before erosion (RawHeight) on a 16 m grid – the same in the preview and in the world. All decisions
    // about rivers (which lakes, where partner lakes go, the path) are made on it, so that the preview shows the rivers of the world;
    // only the real water levels and the carving use the finished terrain.
    const double PlanCell = 16;
    float[] planGrid;
    int planN;
    readonly Dictionary<int, int> planLevel = new();

    void BuildPlanGrid()
    {
        planN = (int)(size / PlanCell) + 1;
        var grid = new float[planN * planN];
        Parallel.For(0, planN, j =>
        {
            for (int i = 0; i < planN; i++) grid[j * planN + i] = (float)RawHeight(i * PlanCell, j * PlanCell, out _);
        });
        planGrid = grid;
    }

    double PlanHeight(double gx, double gz)
    {
        double fx = Math.Clamp(gx / PlanCell, 0, planN - 1.001), fz = Math.Clamp(gz / PlanCell, 0, planN - 1.001);
        int i = (int)fx, j = (int)fz;
        double tx = fx - i, tz = fz - j;
        int o = j * planN + i;
        return (planGrid[o] * (1 - tx) + planGrid[o + 1] * tx) * (1 - tz) + (planGrid[o + planN] * (1 - tx) + planGrid[o + planN + 1] * tx) * tz;
    }

    /// like RimRange, on the plan terrain
    (double Min, double Max) PlanRim(double cx, double cz, LakeShape shape)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int k = 0; k < 40; k++)
        {
            double a = k * Math.PI * 2 / 40, rr = shape?.R(a) ?? 0;
            foreach (double off in new[] { 0.0, 10, 20, 30 })
            {
                double h = PlanHeight(cx + Math.Cos(a) * (rr + off), cz + Math.Sin(a) * (rr + off));
                min = Math.Min(min, h); max = Math.Max(max, h);
            }
        }
        return (min, max);
    }

    /// water level of a lake (or of its river system) on the plan terrain; lakes of PlaceLakes: from a circle with their radius
    int PlanLevelOf(int k)
    {
        if (planLevel.TryGetValue(k, out int v)) return v;
        var l = LakeList[k - 1];
        double min = double.MaxValue;
        for (int q = 0; q < 40; q++)
        {
            double a = q * Math.PI * 2 / 40;
            foreach (double off in new[] { 0.0, 10, 20, 30 })
                min = Math.Min(min, PlanHeight(l.X + Math.Cos(a) * (l.Radius + off), l.Z + Math.Sin(a) * (l.Radius + off)));
        }
        return planLevel[k] = (int)Math.Floor(min) - 1;
    }

    readonly List<int> rootOf = new();   // lake number → number of the lake whose cells (lakeOf) it shares since a river joined them
    int Root(int k) { while (k < rootOf.Count && rootOf[k] != k) k = rootOf[k]; return k; }

    int LakeAt(double gx, double gz)
    {
        int i = Math.Clamp((int)(gx / Cell), 0, N - 1), j = Math.Clamp((int)(gz / Cell), 0, N - 1);
        return lakeOf[j * N + i];
    }

    /// search the path between lake a and lake b (numbers 1-based), then lower the higher lake and carve the valley
    bool TryRiver(int a, int b, Random rr)
    {
        int ra = Root(a), rb = Root(b);
        int La = LakeList[ra - 1].Level, Lb = LakeList[rb - 1].Level, L = Math.Min(La, Lb);
        // the path is searched on the plan terrain with the plan levels (so the preview shows the same rivers as the world)
        int Lp = Math.Min(PlanLevelOf(a), PlanLevelOf(b));
        var line = SearchRiver(a, b, Lp, out double maxCut);
        RiverLog?.Invoke($"    search {a}-{b} L{L}/{Lp}: {(line == null ? "none" : $"{line.Count} points, cut {maxCut:0.0}")}");
        if (line == null || maxCut > MaxCut) return false;
        // the higher lake (or river system): dig it deeper
        if (La > L) LowerLake(ra, L);
        if (Lb > L) LowerLake(rb, L);
        double length = CarveRiver(ra, line, L, rr.Next(100000));
        // one water: lake b belongs to lake a from now on (FillWater floods both and the river from lake a)
        for (int k = 0; k < lakeOf.Length; k++) if (lakeOf[k] == rb) lakeOf[k] = ra;
        rootOf[rb] = ra;
        for (int k = 1; k <= LakeList.Count; k++) if (Root(k) == ra) planLevel[k] = Lp;
        RiverList.Add(new RiverInfo(a, b, L, length, maxCut, line));
        return true;
    }

    // 16 directions (like River.Plan): straight, knight moves and diagonals
    static readonly (int dx, int dz)[] RiverDirs =
    {
        (1, 0), (2, 1), (1, 1), (1, 2), (0, 1), (-1, 2), (-1, 1), (-2, 1),
        (-1, 0), (-2, -1), (-1, -1), (-1, -2), (0, -1), (1, -2), (1, -1), (2, -1),
    };

    /// like River.Plan: A* on a grid of at least 8 m, costs per meter = 1 + cut + land below the bank + slow noise (bends);
    /// blocked: other lakes (basin + edge), other rivers, the map edge. Returns the smoothed center line or null.
    List<Point> SearchRiver(int a, int b, int L, out double maxCut)
    {
        maxCut = 0;
        int ra = Root(a), rb = Root(b);
        var la = LakeList[a - 1]; var lb = LakeList[b - 1];
        double G = PlanCell;   // the same grid in the preview and in the world
        double reach = 600;
        double x0 = Math.Max(RiverEdge, Math.Min(la.X - la.Radius * 2.6, lb.X - lb.Radius * 2.6) - reach);
        double x1 = Math.Min(size - RiverEdge, Math.Max(la.X + la.Radius * 2.6, lb.X + lb.Radius * 2.6) + reach);
        double z0 = Math.Max(RiverEdge, Math.Min(la.Z - la.Radius * 2.6, lb.Z - lb.Radius * 2.6) - reach);
        double z1 = Math.Min(size - RiverEdge, Math.Max(la.Z + la.Radius * 2.6, lb.Z + lb.Radius * 2.6) + reach);
        int gw = (int)((x1 - x0) / G), gh = (int)((z1 - z0) / G);
        if (gw < 4 || gh < 4) return null;
        int n = gw * gh;
        var ground = new float[n];
        var kind = new byte[n];   // 0 land, 1 lake a, 2 lake b, 3 blocked
        var members = Enumerable.Range(1, LakeList.Count).Where(k => Root(k) == ra || Root(k) == rb).ToList();
        var others = Enumerable.Range(1, LakeList.Count).Where(k => Root(k) != ra && Root(k) != rb)
            .Select(k => (LakeList[k - 1].X, LakeList[k - 1].Z, R2: Math.Pow(LakeList[k - 1].Radius * 1.6 + 40, 2))).ToList();
        for (int g = 0; g < n; g++)
        {
            double cx = x0 + (g % gw + 0.5) * G, cz = z0 + (g / gw + 0.5) * G;
            ground[g] = (float)PlanHeight(cx, cz);
            // lake membership from the outline of the lakes (the same in the preview and in the world)
            if (members.Any(k => Root(k) == ra && InLake(k, cx, cz))) kind[g] = 1;
            else if (members.Any(k => Root(k) == rb && InLake(k, cx, cz))) kind[g] = 2;
            else if (others.Any(o => (cx - o.X) * (cx - o.X) + (cz - o.Z) * (cz - o.Z) < o.R2)) kind[g] = 3;   // other lakes (by their size, not by
            // the cells of the grid: the edge of the basin is wider in the coarse preview)
            else if (PlanNear(cx, cz)) kind[g] = 3;
        }
        if (!kind.Contains((byte)1) || !kind.Contains((byte)2)) return null;
        double bankH = L + River.BankAbove;
        // lowest land within about 24 m (the banks): a river on a hillside above lower land would need a dam on the low side
        int rc = Math.Max(1, (int)Math.Round(24 / G));
        var lowest = new float[n];
        for (int g = 0; g < n; g++)
        {
            int gx = g % gw, gz = g / gw;
            float m = ground[g];
            for (int dz = -rc; dz <= rc; dz++)
                for (int dx = -rc; dx <= rc; dx++)
                {
                    int xx = gx + dx, zz = gz + dz;
                    if (xx < 0 || zz < 0 || xx >= gw || zz >= gh || kind[zz * gw + xx] != 0) continue;
                    m = Math.Min(m, ground[zz * gw + xx]);
                }
            lowest[g] = m;
        }
        int ns = 7 + a * 31 + b * 17;
        double StepCost(int g, double len)
        {
            if (kind[g] == 1) return len * 0.3;
            if (kind[g] == 2) return len;
            double h = ground[g] - bankH;
            double cut = Math.Max(0, h), low = Math.Max(0, bankH - lowest[g]);
            if (cut > MaxCut + 5 || low > (relax ? 6 : 4)) return double.PositiveInfinity;   // no high dams (the tool allows 8 m)
            double cx = x0 + (g % gw) * G, cz = z0 + (g / gw) * G;
            double noise = 0.85 * (detail.Noise(cx / 220 + ns, cz / 220) + 1) + 0.45 * (detail.Noise(cx / 95, cz / 95 + ns) + 1);
            return len * (1 + noise + 0.05 * cut + 0.006 * cut * cut + 3 * low + 0.6 * low * low);
        }
        double H(int g)
        {
            double cx = x0 + (g % gw + 0.5) * G, cz = z0 + (g / gw + 0.5) * G;
            return Math.Max(0, Math.Sqrt((cx - lb.X) * (cx - lb.X) + (cz - lb.Z) * (cz - lb.Z)) - lb.Radius * 1.6);
        }
        var cost = new float[n * 16];
        Array.Fill(cost, float.PositiveInfinity);
        var prev = new int[n * 16];
        var queue = new PriorityQueue<int, double>();
        for (int g = 0; g < n; g++)
        {
            if (kind[g] != 1) continue;
            int gx = g % gw, gz = g / gw;
            bool border = false;
            for (int d = 0; d < 16; d += 4)
            {
                int xx = gx + RiverDirs[d].dx, zz = gz + RiverDirs[d].dz;
                if (xx >= 0 && zz >= 0 && xx < gw && zz < gh && kind[zz * gw + xx] != 1) border = true;
            }
            if (!border) continue;
            for (int d = 0; d < 16; d++)
            {
                int s = g * 16 + d;
                cost[s] = 0; prev[s] = -1;
                queue.Enqueue(s, H(g));
            }
        }
        int goal = -1;
        while (queue.TryDequeue(out int s, out double pr))
        {
            int g = s / 16, d = s % 16;
            double c = cost[s];
            if (pr - H(g) > c + 1e-3) continue;
            if (kind[g] == 2) { goal = s; break; }
            int gx = g % gw, gz = g / gw;
            for (int t = -1; t <= 1; t++)
            {
                int nd = (d + t + 16) % 16;
                var (dx, dz) = RiverDirs[nd];
                int xx = gx + dx, zz = gz + dz;
                if (xx < 0 || zz < 0 || xx >= gw || zz >= gh) continue;
                int ng = zz * gw + xx;
                if (kind[ng] == 3) continue;
                if (kind[g] == 2 && kind[ng] == 1) continue;
                if (Math.Abs(dx) + Math.Abs(dz) == 3)
                {
                    int mx = gx + Math.Sign(dx) * (Math.Abs(dx) == 2 ? 1 : 0), mz = gz + Math.Sign(dz) * (Math.Abs(dz) == 2 ? 1 : 0);
                    if (kind[mz * gw + mx] == 3) continue;
                }
                double len = G * Math.Sqrt(dx * dx + dz * dz);
                double sc = StepCost(ng, len);
                if (double.IsInfinity(sc)) continue;
                if (t != 0) sc += 0.025 * len;
                double nc = c + sc;
                int nst = ng * 16 + nd;
                if (nc >= cost[nst]) continue;
                cost[nst] = (float)nc; prev[nst] = s;
                queue.Enqueue(nst, nc + H(ng));
            }
        }
        if (goal < 0) return null;
        var cells = new List<int>();
        for (int s = goal; s >= 0; s = prev[s]) cells.Add(s / 16);
        cells.Reverse();
        int firstLand = cells.FindIndex(g => kind[g] != 1);
        if (firstLand > 1) cells.RemoveRange(0, firstLand - 1);
        foreach (int g in cells) if (kind[g] == 0) maxCut = Math.Max(maxCut, ground[g] - bankH);
        var pts = cells.Select(g => new Point(x0 + (g % gw + 0.5) * G, z0 + (g / gw + 0.5) * G)).ToList();
        if (pts.Count < 2) return null;
        // a little further into both lakes, so that the mouth is open
        var d0 = pts[0] - pts[1]; d0.Normalize(); pts.Insert(0, pts[0] + d0 * 12);
        var d1 = pts[^1] - pts[^2]; d1.Normalize(); pts.Add(pts[^1] + d1 * 12);
        var smooth = River.SmoothLine(pts);
        smooth = River.MeanderLine(smooth, ns, q =>
        {
            int gx = (int)Math.Floor((q.X - x0) / G), gz = (int)Math.Floor((q.Y - z0) / G);
            if (gx < 0 || gz < 0 || gx >= gw || gz >= gh) return false;
            int g = gz * gw + gx;
            if (kind[g] != 0) return false;
            double cut = ground[g] - bankH, low = bankH - lowest[g];
            return cut <= MaxCut && low <= (relax ? 6 : 4);
        });
        return River.Simplify(smooth, 3);
    }

    /// the higher lake: water cells lower by the difference, the shore follows over 3 m per meter (8–40 m), never below the bank
    void LowerLake(int id, int L)
    {
        var l = LakeList[id - 1];
        int by = l.Level - L;
        var mask = new bool[N * N];
        int i0 = N, i1 = -1, j0 = N, j1 = -1;
        for (int k = 0; k < mask.Length; k++)
        {
            if (lakeOf[k] != id || coarse[k] >= l.Level + Lake.WetMargin) continue;
            mask[k] = true;
            coarse[k] -= by;
            int i = k % N, j = k / N;
            i0 = Math.Min(i0, i); i1 = Math.Max(i1, i); j0 = Math.Min(j0, j); j1 = Math.Max(j1, j);
        }
        // the whole system (lakes joined by earlier rivers) gets the new level
        for (int k = 1; k <= LakeList.Count; k++) if (Root(k) == id) LakeList[k - 1] = LakeList[k - 1] with { Level = L };
        for (int r = 0; r < RiverList.Count; r++) if (Root(RiverList[r].LakeA) == id) RiverList[r] = RiverList[r] with { Level = L };
        if (i1 < 0) return;
        double width = Math.Clamp(3.0 * by, 8, 40);
        int m = (int)Math.Ceiling(width / Cell) + 2;
        i0 = Math.Max(0, i0 - m); j0 = Math.Max(0, j0 - m); i1 = Math.Min(N - 1, i1 + m); j1 = Math.Min(N - 1, j1 + m);
        int gw = i1 - i0 + 1, gh = j1 - j0 + 1;
        var sub = new bool[gw * gh];
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++) sub[(j - j0) * gw + i - i0] = mask[j * N + i];
        var dist = GroundShaper.Distance(sub, gw, gh);
        for (int g = 0; g < sub.Length; g++)
        {
            double dm = dist[g] * Cell;
            if (sub[g] || dm > width) continue;
            int k = (j0 + g / gw) * N + i0 + g % gw;
            double h = coarse[k];
            double t = Math.Max(h - by * (1 - SmoothStep(0, 1, dm / width)), L + 1.6);
            if (t < h) coarse[k] = (float)t;
        }
    }

    /// the valley along the line (like River.Build step 2 and 3, on the coarse grid); returns the length
    double CarveRiver(int lakeId, List<Point> pts, int L, int seed)
    {
        // 2026-11-19 / 2026-11-22: varied banks (flat meadows, gentle slopes, terraces, steep cliffs), asymmetric shorelines
        const double WidthMin = 14, WidthMax = 32;
        double step = Math.Clamp(Cell / 3, 1, 4);
        var line = River.Dense(pts, step);
        int np = line.Count;
        double bank = L + River.BankAbove;
        var prof = River.BuildProfile(line, WidthMin, WidthMax, seed, bank, SampleCoarse, step);
        double total = prof.Arc[^1];
        var depths = new double[np];
        var shift = new double[np];   // deepest line beside the middle (fraction of the half width, + = left)
        for (int k = 0; k < np; k++)
        {
            double pool = Math.Max(0, RoadApplier.Octave((int)prof.Arc[k], 0, 110, seed + 57) - 0.4) / 0.6;
            depths[k] = 3 + 4.5 * (0.5 + 0.5 * RoadApplier.Octave((int)prof.Arc[k], 0, 380, seed + 51)) + 1.8 * pool;
            // deepest water wanders and hugs the outer side of bends
            shift[k] = Math.Clamp(0.28 * RoadApplier.Octave((int)prof.Arc[k], 0, 170, seed + 61) - 0.30 * Math.Clamp(prof.Curv[k] * 2, -1, 1), -0.45, 0.45);
        }
        RiverLog?.Invoke($"    banks flat/mid/cliff: left {prof.Style[0].Count(v => v < 0.33) * 100.0 / np:0}/{prof.Style[0].Count(v => v >= 0.33 && v < 0.67) * 100.0 / np:0}/{prof.Style[0].Count(v => v >= 0.67) * 100.0 / np:0} %, "
            + $"right {prof.Style[1].Count(v => v < 0.33) * 100.0 / np:0}/{prof.Style[1].Count(v => v >= 0.33 && v < 0.67) * 100.0 / np:0}/{prof.Style[1].Count(v => v >= 0.67) * 100.0 / np:0} %, depth {depths.Min():0.0}–{depths.Max():0.0} m");
        double R = WidthMax * 0.65 + 4 + River.ValleyMax;
        int bi0 = Math.Max(0, (int)((line.Min(p => p.X) - R) / Cell)), bi1 = Math.Min(N - 1, (int)((line.Max(p => p.X) + R) / Cell));
        int bj0 = Math.Max(0, (int)((line.Min(p => p.Y) - R) / Cell)), bj1 = Math.Min(N - 1, (int)((line.Max(p => p.Y) + R) / Cell));
        int bw = bi1 - bi0 + 1, bh = bj1 - bj0 + 1;
        var dmin = new float[bw * bh];
        var near = new int[bw * bh];
        Array.Fill(dmin, float.MaxValue);
        int rc = (int)Math.Ceiling(R / Cell) + 1;
        for (int k = 0; k < np; k++)
        {
            var p = line[k];
            int pi = (int)(p.X / Cell), pj = (int)(p.Y / Cell);
            for (int j = Math.Max(bj0, pj - rc); j <= Math.Min(bj1, pj + rc); j++)
            {
                double dz = (j + 0.5) * Cell - p.Y;
                for (int i = Math.Max(bi0, pi - rc); i <= Math.Min(bi1, pi + rc); i++)
                {
                    double dx = (i + 0.5) * Cell - p.X;
                    double d2 = dx * dx + dz * dz;
                    if (d2 > R * R) continue;
                    int g = (j - bj0) * bw + i - bi0;
                    if (d2 < (double)dmin[g] * dmin[g]) { dmin[g] = (float)Math.Sqrt(d2); near[g] = k; }
                }
            }
        }
        // in the coarse preview (cells of about 20 m) the river would fall between the cell centers: at least 0.6 cells wide
        double minHalf = Cell > 4 ? Cell * 0.6 : 0;
        var wet = new bool[bw * bh];
        for (int g = 0; g < dmin.Length; g++)
        {
            if (dmin[g] == float.MaxValue) continue;
            int i = bi0 + g % bw, j = bj0 + g / bw, idx = j * N + i;
            int x = (int)((i + 0.5) * Cell), z = (int)((j + 0.5) * Cell);
            int kn = near[g];
            double d = dmin[g];
            double lat = ((i + 0.5) * Cell - line[kn].X) * prof.Normals[kn].X + ((j + 0.5) * Cell - line[kn].Y) * prof.Normals[kn].Y;   // + = left
            int side = lat >= 0 ? 0 : 1;
            double hw = Math.Max(Math.Max(4.5, prof.Hw[side][kn] + River.EdgeWobble(x, z, seed, prof.EndBlend[kn])), minHalf);
            double style = prof.Style[side][kn];
            double u = d - hw;
            double h = coarse[idx];
            int lk = lakeOf[idx];
            bool lakeWater = lk != 0 && h < LakeList[lk - 1].Level + Lake.WetMargin && LakeList[lk - 1].Level == L;
            if (u <= 0)
            {
                double t = Math.Min(1.3, Math.Abs(lat / Math.Max(1, hw) - shift[kn]) / (1 + Math.Abs(shift[kn])));
                double edge = 0.35 + 1.05 * style;   // shallow at flat meadows, deep right under steep walls
                double depth = edge + Math.Max(0, depths[kn] - edge) * Math.Pow(Math.Max(0, 1 - t * t), 0.7);
                depth += (0.6 * RoadApplier.Octave(x, z, 9, seed + 5) + 0.5 * RoadApplier.Octave(x, z, 23, seed + 7)) * Math.Max(0, 1 - t);
                coarse[idx] = (float)Math.Min(h, L - depth);
                wet[g] = true;
                lakeOf[idx] = lakeId;
                calm[idx] = Math.Min(calm[idx], 0.3f);
                continue;
            }
            if (lakeWater) { wet[g] = true; continue; }
            double wall = River.BankWall(u, x, z, seed, bank, style, prof.Terrace[side][kn], prof.BenchW[side][kn]);
            // land lower than the bank: a wide, low embankment that runs out flat into the land (the user's request 2026-11-19:
            // the narrow walls of a 1:2 dam looked like a gutter); crest 5–13 m, then 12–22 % down
            double un = u * (1 + 0.30 * RoadApplier.Octave(x, z, 28, seed + 11));
            double crest = 5 + 8 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 80, seed + 29));
            double fall = 0.12 + 0.1 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 50, seed + 31));
            double levee = bank + 0.1 - Math.Max(0, un - crest) * fall + 0.3 * RoadApplier.Octave(x, z, 11, seed + 17) * Math.Min(1, Math.Max(0, un - crest) / 4);
            double target = Math.Clamp(h, Math.Min(levee, wall), wall);
            double fade = SmoothStep(0, 1, (R - hw - u) / 15);
            target = h + (target - h) * fade;
            coarse[idx] = (float)target;
            if (u <= 6 && lk == 0) lakeOf[idx] = lakeId;
            calm[idx] = Math.Min(calm[idx], (float)(0.3 + 0.7 * SmoothStep(2, 34, u)));
        }
        // no dry cell beside the water below the waterline
        for (int g = 0; g < dmin.Length; g++)
        {
            if (wet[g] || dmin[g] == float.MaxValue) continue;
            int gx = g % bw, gz = g / bw;
            bool beside = (gx > 0 && wet[g - 1]) || (gx < bw - 1 && wet[g + 1]) || (gz > 0 && wet[g - bw]) || (gz < bh - 1 && wet[g + bw]);
            if (!beside) continue;
            int idx = (bj0 + gz) * N + bi0 + gx;
            if (coarse[idx] < L + Lake.WetMargin) coarse[idx] = (float)(bank + 0.05);
        }
        // other rivers keep away (valley + 150 m)
        double keep = R + 150;
        int kc = (int)Math.Ceiling(keep / PlanCell) + 1;
        foreach (var p in River.Dense(pts, 8))   // from the line points (not the grid), so the same in the preview and in the world
        {
            int pi = (int)Math.Round(p.X / PlanCell), pj = (int)Math.Round(p.Y / PlanCell);
            for (int j = Math.Max(0, pj - kc); j <= Math.Min(planN - 1, pj + kc); j++)
                for (int i = Math.Max(0, pi - kc); i <= Math.Min(planN - 1, pi + kc); i++)
                {
                    double dx = i * PlanCell - p.X, dz = j * PlanCell - p.Y;
                    if (dx * dx + dz * dz <= keep * keep) nearRiver[j * planN + i] = true;
                }
        }
        return total;
    }
}
