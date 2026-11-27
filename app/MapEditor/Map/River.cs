using System.Windows;

namespace MapEditor.Map;

/// a lake found on the map: all connected water cells with the same level (index like dtm.raw)
public sealed class LakeInfo
{
    public int Level;
    public int[] Cells = Array.Empty<int>();
    public int X0, Z0, X1, Z1;   // frame in world coordinates
    public double CenterX, CenterZ;
    public bool TooLarge;        // more than MaxLakeCells: the sea or a huge water area
}

/// planned river between two lakes (FLUSS_ANALYSE.md, plan of 2026-11-18)
public sealed class RiverPlan
{
    public LakeInfo A, B;
    public int Level;                 // common level = the lower one of both lakes
    public List<Point> Line = new();  // center line from lake A to lake B (points about every 30–40 m, editable)
    public double Length;             // m
    public double MaxCut;             // deepest cut of the valley (ground above the bank height, m)
    public int LowGround;             // m of the path where the land lies below the bank (an embankment is needed)
    public string Problem;            // null = path found
}

/// settings for building a river
public sealed class RiverSpec
{
    public double WidthMin = 16, WidthMax = 28;   // water width (m), changes slowly along the river
    public double Depth = 4.5;                     // greatest water depth in the middle (m)
    public int Seed = 1;
    public int Biome = -1;                         // biome under the water, -1 = stays
    public Point ClickA, ClickB;                   // a point in each lake (to find them again when repeating the step)
    public List<Point> Line = new();
}

public sealed record RiverStats(int Water, int Changed, int Spared, double MaxCut, int Embankment, int Lowered, int LoweredBy, int Level, double Length);

/// River between two lakes. In the game every water is flat (blue value of splat4.png = level), so the river is a horizontal
/// channel: both lakes and the river get one common level, the lower one of the two lakes; the higher lake is dug deeper
/// (no dams). The path is searched like a road on an 8 m grid (16 directions, at most 22.5° turn per step) and prefers low
/// land; the valley is shaped like the rivers of the game 3.4 (FLUSS_ANALYSE.md section 3), but narrower (16–28 m water).
public static class River
{
    /// test output
    public static Action<string> Log;
    public const int MaxLakeCells = 30_000_000;
    public const int MaxLower = 8;          // the higher lake is lowered by at most so many meters
    const int Cell = 8;                     // grid of the path search (m)
    internal const double BankAbove = Lake.WetMargin + 0.15;   // height of the bank above the level directly at the water (everything below gets water)
    internal const int ValleyMax = 70;               // greatest distance from the water edge that still changes (m)

    /// the lake under a point: flooding over all water cells with the same level (4 neighbors); null = no water there
    public static LakeInfo FindLake(WorldData w, double px, double pz)
    {
        int x = (int)Math.Floor(px), z = (int)Math.Floor(pz);
        if (!w.Contains(x, z)) return null;
        int start = w.Index(x, z);
        byte level = w.WaterMask[start];
        if (level == 0)
        {
            // clicked just beside the water: nearest water cell within 6 m
            double best = double.MaxValue;
            for (int dz = -6; dz <= 6; dz++)
                for (int dx = -6; dx <= 6; dx++)
                {
                    if (!w.Contains(x + dx, z + dz) || w.WaterMask[w.Index(x + dx, z + dz)] == 0) continue;
                    double d = dx * dx + dz * dz;
                    if (d < best) { best = d; start = w.Index(x + dx, z + dz); }
                }
            if (best == double.MaxValue) return null;
            level = w.WaterMask[start];
        }
        int size = w.Size, half = w.Half;
        var seen = new HashSet<int> { start };
        var list = new List<int> { start };
        var info = new LakeInfo { Level = level, X0 = int.MaxValue, Z0 = int.MaxValue, X1 = int.MinValue, Z1 = int.MinValue };
        double sx = 0, sz = 0;
        for (int q = 0; q < list.Count; q++)
        {
            int c = list[q];
            int cx = c % size, cz = c / size;
            sx += cx - half; sz += cz - half;
            info.X0 = Math.Min(info.X0, cx - half); info.X1 = Math.Max(info.X1, cx - half);
            info.Z0 = Math.Min(info.Z0, cz - half); info.Z1 = Math.Max(info.Z1, cz - half);
            if (list.Count > MaxLakeCells) { info.TooLarge = true; break; }
            if (cx < 1 || cz < 1 || cx >= size - 1 || cz >= size - 1) continue;
            foreach (int n in new[] { c + 1, c - 1, c + size, c - size })
                if (w.WaterMask[n] == level && seen.Add(n)) list.Add(n);
        }
        info.Cells = list.ToArray();
        info.CenterX = sx / list.Count; info.CenterZ = sz / list.Count;
        return info;
    }

    // 16 directions (like the road network): straight, knight moves and diagonals
    static readonly (int dx, int dz)[] Dirs =
    {
        (1, 0), (2, 1), (1, 1), (1, 2), (0, 1), (-1, 2), (-1, 1), (-2, 1),
        (-1, 0), (-2, -1), (-1, -1), (-1, -2), (0, -1), (1, -2), (1, -1), (2, -1),
    };

    /// plan the path between two lakes. Costs per meter: 1 + cut (ground above the bank) + land lower than the bank (embankment)
    /// + slow noise (bends). Blocked: roads, buildings and tiles (+ margin), other water, 100 m map edge.
    public static RiverPlan Plan(WorldData w, LakeInfo a, LakeInfo b, int seed, CancellationToken cancel = default)
    {
        var plan = new RiverPlan { A = a, B = b, Level = Math.Min(a.Level, b.Level) };
        if (a.TooLarge || b.TooLarge) { plan.Problem = Loc.T("One of the waters is too large (sea): choose two lakes."); return plan; }
        if (a.Level == b.Level && a.Cells.Length > 0 && b.Cells.Length > 0 && a.Cells[0] == b.Cells[0]) { plan.Problem = Loc.T("Both points lie in the same lake."); return plan; }
        if (Math.Abs(a.Level - b.Level) > MaxLower)
        {
            plan.Problem = Loc.F("The water levels differ by {0} m (at most {1} m possible): the higher lake would have to be dug far too deep.", Math.Abs(a.Level - b.Level), MaxLower);
            return plan;
        }
        int L = plan.Level;
        int half = w.Half, edge = 100;
        int margin = 1200;
        int x0 = Math.Max(-half + edge, Math.Min(a.X0, b.X0) - margin), x1 = Math.Min(half - edge, Math.Max(a.X1, b.X1) + margin);
        int z0 = Math.Max(-half + edge, Math.Min(a.Z0, b.Z0) - margin), z1 = Math.Min(half - edge, Math.Max(a.Z1, b.Z1) + margin);
        int gw = (x1 - x0) / Cell, gh = (z1 - z0) / Cell;
        if (gw < 4 || gh < 4) { plan.Problem = Loc.T("No room for a river here."); return plan; }
        int n = gw * gh;
        var ground = new float[n];
        var kind = new byte[n];   // 0 land, 1 lake A, 2 lake B, 3 blocked
        var setA = new HashSet<int>(a.Cells); var setB = new HashSet<int>(b.Cells);
        for (int g = 0; g < n; g++)
        {
            int cx = x0 + (g % gw) * Cell, cz = z0 + (g / gw) * Cell;
            double sum = 0; int cA = 0, cB = 0, cW = 0, cR = 0;
            for (int k = 0; k < 4; k++)
            {
                int x = cx + 2 + (k & 1) * 4, z = cz + 2 + (k >> 1) * 4;
                int i = w.Index(x, z);
                sum += w.Heights[i] / 256.0;
                if (setA.Contains(i)) cA++;
                else if (setB.Contains(i)) cB++;
                else if (w.WaterMask[i] != 0) cW++;
                if (w.RoadMask[i] != 0) cR++;
            }
            ground[g] = (float)(sum / 4);
            kind[g] = cA > 0 ? (byte)1 : cB > 0 ? (byte)2 : (cW > 0 || cR > 0) ? (byte)3 : (byte)0;
        }
        // buildings and tiles: footprint + 24 m (tiles + 40 m) blocked; roads + 24 m
        void Block(double bx0, double bz0, double bx1, double bz1)
        {
            int ga = Math.Max(0, (int)Math.Floor((bx0 - x0) / Cell)), gb = Math.Min(gw - 1, (int)Math.Floor((bx1 - x0) / Cell));
            int ha = Math.Max(0, (int)Math.Floor((bz0 - z0) / Cell)), hb = Math.Min(gh - 1, (int)Math.Floor((bz1 - z0) / Cell));
            for (int gz = ha; gz <= hb; gz++)
                for (int gx = ga; gx <= gb; gx++)
                    if (kind[gz * gw + gx] == 0) kind[gz * gw + gx] = 3;
        }
        foreach (var p in w.PrefabSnapshot())
        {
            int m = p.IsTile ? 40 : 24;
            Block(p.X - m, p.Z - m, p.X + p.SizeX + m, p.Z + p.SizeZ + m);
        }
        var roadCells = new List<int>();
        for (int g = 0; g < n; g++) if (kind[g] == 3) roadCells.Add(g);
        foreach (int g in roadCells)
        {
            int gx = g % gw, gz = g / gw;
            for (int dz = -3; dz <= 3; dz++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    int xx = gx + dx, zz = gz + dz;
                    if (xx < 0 || zz < 0 || xx >= gw || zz >= gh) continue;
                    if (kind[zz * gw + xx] == 0) kind[zz * gw + xx] = 4;   // margin around roads/water (4 = blocked, not counted as water)
                }
        }
        double bankH = L + BankAbove;
        double StepCost(int g, double len)
        {
            if (kind[g] == 1) return len * 0.3;
            if (kind[g] == 2) return len;
            double h = ground[g] - bankH;
            int gx = g % gw, gz = g / gw;
            double cut = Math.Max(0, h), low = Math.Max(0, -h);
            if (cut > 35) return double.PositiveInfinity;
            double noise = 0.85 * (RoadApplier.Octave(x0 + gx * Cell, z0 + gz * Cell, 220, seed) + 1) + 0.45 * (RoadApplier.Octave(x0 + gx * Cell, z0 + gz * Cell, 95, seed + 7) + 1);
            if (low > 8) return double.PositiveInfinity;   // the river would need a dam higher than 8 m
            return len * (1 + noise + 0.05 * cut + 0.006 * cut * cut + 1.5 * low + 0.3 * low * low);
        }
        // heuristic: distance to the frame of lake B
        double H(int g)
        {
            double cx = x0 + (g % gw) * Cell + Cell / 2.0, cz = z0 + (g / gw) * Cell + Cell / 2.0;
            double dx = Math.Max(0, Math.Max(b.X0 - cx, cx - b.X1)), dz = Math.Max(0, Math.Max(b.Z0 - cz, cz - b.Z1));
            return Math.Sqrt(dx * dx + dz * dz);
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
                int xx = gx + Dirs[d].dx, zz = gz + Dirs[d].dz;
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
        if (queue.Count == 0) { plan.Problem = Loc.T("Lake A lies outside the area that can be searched."); return plan; }
        int goal = -1, popped = 0;
        while (queue.TryDequeue(out int s, out double pr))
        {
            int g = s / 16, d = s % 16;
            double c = cost[s];
            if (pr - H(g) > c + 1e-3) continue;   // outdated entry
            if (kind[g] == 2) { goal = s; break; }
            if ((++popped & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
            int gx = g % gw, gz = g / gw;
            for (int t = -1; t <= 1; t++)
            {
                int nd = (d + t + 16) % 16;
                var (dx, dz) = Dirs[nd];
                int xx = gx + dx, zz = gz + dz;
                if (xx < 0 || zz < 0 || xx >= gw || zz >= gh) continue;
                int ng = zz * gw + xx;
                if (kind[ng] >= 3) continue;
                if (kind[g] == 2 && kind[ng] == 1) continue;
                double len = Cell * Math.Sqrt(dx * dx + dz * dz);
                double sc = StepCost(ng, len);
                // knight moves pass between two cells: both must be free
                if (Math.Abs(dx) + Math.Abs(dz) == 3)
                {
                    int mx = gx + Math.Sign(dx) * (Math.Abs(dx) == 2 ? 1 : 0), mz = gz + Math.Sign(dz) * (Math.Abs(dz) == 2 ? 1 : 0);
                    if (kind[mz * gw + mx] >= 3) continue;
                }
                if (t != 0) sc += 0.025 * len;
                double nc = c + sc;
                int ns = ng * 16 + nd;
                if (nc >= cost[ns]) continue;
                cost[ns] = (float)nc; prev[ns] = s;
                queue.Enqueue(ns, nc + H(ng));
            }
        }
        Log?.Invoke($"river search: grid {gw}x{gh}, A {kind.Count(k => k == 1)}, B {kind.Count(k => k == 2)}, blocked {kind.Count(k => k >= 3)}, popped {popped}, L {L}, ground min {ground.Min():0.0} max {ground.Max():0.0}");
        if (goal < 0) { plan.Problem = Loc.T("No path found between the two lakes (roads, buildings, other water, very high mountains or land far below the water level are in the way)."); return plan; }
        var cells = new List<int>();
        for (int s = goal; s >= 0; s = prev[s]) cells.Add(s / 16);
        cells.Reverse();
        // drop the part inside lake A except the last cell (the river starts at the shore)
        int firstLand = cells.FindIndex(g => kind[g] != 1);
        if (firstLand > 1) cells.RemoveRange(0, firstLand - 1);
        var pts = cells.Select(g => new Point(x0 + (g % gw) * Cell + Cell / 2.0, z0 + (g / gw) * Cell + Cell / 2.0)).ToList();
        // a little further into both lakes, so that the mouth is open
        if (pts.Count >= 2)
        {
            var d0 = pts[0] - pts[1]; d0.Normalize(); pts.Insert(0, pts[0] + d0 * 12);
            var d1 = pts[^1] - pts[^2]; d1.Normalize(); pts.Add(pts[^1] + d1 * 12);
        }
        foreach (int g in cells)
        {
            double h = ground[g] - bankH;
            if (kind[g] == 0) { plan.MaxCut = Math.Max(plan.MaxCut, h); if (h < 0) plan.LowGround += Cell; }
        }
        var smooth = SmoothLine(pts);
        smooth = MeanderLine(smooth, seed, q =>
        {
            int gx = (int)Math.Floor((q.X - x0) / Cell), gz = (int)Math.Floor((q.Y - z0) / Cell);
            if (gx < 0 || gz < 0 || gx >= gw || gz >= gh) return false;
            int g = gz * gw + gx;
            if (kind[g] != 0) return false;
            double h = ground[g] - bankH;
            return h <= 35 && -h <= 8;
        });
        plan.Line = Simplify(smooth, 3);
        plan.Length = Length(Dense(plan.Line, 2));
        return plan;
    }

    /// raster steps away: densify to 2 m, average several times (bends stay, zigzag goes), ends stay
    internal static List<Point> SmoothLine(List<Point> pts)
    {
        var d = new List<Point> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            var v = pts[i] - pts[i - 1];
            int k = Math.Max(1, (int)Math.Ceiling(v.Length / 2));
            for (int j = 1; j <= k; j++) d.Add(pts[i - 1] + v * ((double)j / k));
        }
        var a = d.ToArray();
        for (int it = 0; it < 7; it++)
        {
            var b = (Point[])a.Clone();
            for (int i = 2; i < a.Length - 2; i++)
            {
                double x = 0, z = 0;
                for (int j = -2; j <= 2; j++) { x += a[i + j].X; z += a[i + j].Y; }
                b[i] = new Point(x / 5, z / 5);
            }
            a = b;
        }
        return a.ToList();
    }

    /// add gentle natural river meanders along the smoothed centerline where `ok` allows (keeps away from blocked cells, hills and low dams)
    internal static List<Point> MeanderLine(List<Point> pts, int seed, Func<Point, bool> ok)
    {
        int n = pts.Count;
        if (n < 10) return pts;
        var arc = new double[n];
        for (int i = 1; i < n; i++) arc[i] = arc[i - 1] + (pts[i] - pts[i - 1]).Length;
        double total = arc[^1];
        if (total < 160) return pts;
        var normals = new Vector[n];
        var raw = new double[n];
        var allow = new double[n];
        for (int i = 0; i < n; i++)
        {
            var tg = pts[Math.Min(n - 1, i + 1)] - pts[Math.Max(0, i - 1)];
            if (tg.Length < 1e-6) tg = new Vector(1, 0);
            tg.Normalize();
            normals[i] = new Vector(-tg.Y, tg.X);
            double eb = Smooth(Math.Min(arc[i], total - arc[i]) / 90);
            double wave = 15 * RoadApplier.Octave((int)arc[i], 0, 210, seed + 81) + 6 * RoadApplier.Octave((int)arc[i], 0, 95, seed + 83);
            raw[i] = eb * wave;
            if (Math.Abs(raw[i]) < 0.2) { allow[i] = 1; continue; }
            if (ok(pts[i] + normals[i] * raw[i])) allow[i] = 1;
            else if (ok(pts[i] + normals[i] * (raw[i] * 0.5))) allow[i] = 0.5;
            else allow[i] = 0;
        }
        // smooth `allow` over ±40 m (~20 points at 2 m) so the meander pulls back smoothly near obstacles
        int win = 20;
        var sm = new double[n];
        double run = 0; int cnt = 0;
        for (int i = -win; i < n + win; i++)
        {
            if (i + win < n) { run += allow[i + win]; cnt++; }
            if (i - win - 1 >= 0) { run -= allow[i - win - 1]; cnt--; }
            if (i >= 0 && i < n) sm[i] = run / Math.Max(1, cnt);
        }
        var res = new Point[n];
        for (int i = 0; i < n; i++) res[i] = pts[i] + normals[i] * (raw[i] * sm[i]);
        for (int it = 0; it < 2; it++)
        {
            var b = (Point[])res.Clone();
            for (int i = 2; i < n - 2; i++)
            {
                double x = 0, z = 0;
                for (int j = -2; j <= 2; j++) { x += res[i + j].X; z += res[i + j].Y; }
                b[i] = new Point(x / 5, z / 5);
            }
            res = b;
        }
        return res.ToList();
    }

    /// Douglas-Peucker, then at least about 25 m and at most 45 m between points (editable line)
    internal static List<Point> Simplify(List<Point> pts, double tol)
    {
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        void Rec(int i0, int i1)
        {
            if (i1 <= i0 + 1) return;
            var a = pts[i0]; var v = pts[i1] - a; double len = v.Length;
            int best = -1; double bd = tol;
            for (int i = i0 + 1; i < i1; i++)
            {
                var p = pts[i] - a;
                double dist = len < 1e-6 ? p.Length : Math.Abs(v.X * p.Y - v.Y * p.X) / len;
                if (dist > bd) { bd = dist; best = i; }
            }
            if (best < 0) return;
            keep[best] = true;
            Rec(i0, best); Rec(best, i1);
        }
        Rec(0, pts.Count - 1);
        var res = new List<Point> { pts[0] };
        double run = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            run += (pts[i] - pts[i - 1]).Length;
            bool last = i == pts.Count - 1;
            if (last || (keep[i] && run >= 25) || run >= 45) { if (!last && (pts[^1] - pts[i]).Length < 15) continue; res.Add(pts[i]); run = 0; }
        }
        return res;
    }

    /// Catmull-Rom through the points, a point about every "step" meters
    internal static List<Point> Dense(IReadOnlyList<Point> line, double step)
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
        if (line.Count > 0) c.Add(line[^1]);
        return c;
    }

    static double Length(List<Point> p)
    {
        double s = 0;
        for (int i = 1; i < p.Count; i++) s += (p[i] - p[i - 1]).Length;
        return s;
    }

    internal static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// per-point geometry and bank variety along a river (shared by River.Build and WorldGenerator.CarveRiver)
    internal sealed class RiverProfile
    {
        public double[] Arc = Array.Empty<double>();
        public Vector[] Normals = Array.Empty<Vector>();
        public double[] EndBlend = Array.Empty<double>();
        public double[] Curv = Array.Empty<double>();       // signed bend curvature (+ = bends left, so right side is outer cut bank)
        public double[][] Hw = new double[2][];             // half-width per side (0 = left, 1 = right)
        public double[][] Style = new double[2][];          // 0 = flat riverside meadow, ~0.33 = gentle, ~0.67 = medium/terraced, 1 = steep cliff
        public double[][] Terrace = new double[2][];        // 0..1 strength of a mid-height river terrace
        public double[][] BenchW = new double[2][];         // width of the flat meadow bench (m)
    }

    /// compute independent left/right half-widths and bank styles along a dense centerline
    internal static RiverProfile BuildProfile(List<Point> line, double widthMin, double widthMax, int seed, double bank0, Func<double, double, double> sampleGround, double step)
    {
        int np = line.Count;
        var prof = new RiverProfile
        {
            Arc = new double[np],
            Normals = new Vector[np],
            EndBlend = new double[np],
            Curv = new double[np],
        };
        for (int s = 0; s < 2; s++)
        {
            prof.Hw[s] = new double[np];
            prof.Style[s] = new double[np];
            prof.Terrace[s] = new double[np];
            prof.BenchW[s] = new double[np];
        }
        for (int k = 1; k < np; k++) prof.Arc[k] = prof.Arc[k - 1] + (line[k] - line[k - 1]).Length;
        double total = prof.Arc[^1];
        int cSpan = Math.Max(2, (int)Math.Round(24 / Math.Max(0.5, step)));
        for (int k = 0; k < np; k++)
        {
            var tg = line[Math.Min(np - 1, k + 1)] - line[Math.Max(0, k - 1)];
            if (tg.Length < 1e-6) tg = new Vector(1, 0);
            tg.Normalize();
            prof.Normals[k] = new Vector(-tg.Y, tg.X);
            prof.EndBlend[k] = Smooth(Math.Min(prof.Arc[k], total - prof.Arc[k]) / 40);
            // local curvature over ±24 m
            var t1 = line[k] - line[Math.Max(0, k - cSpan)];
            var t2 = line[Math.Min(np - 1, k + cSpan)] - line[k];
            if (t1.Length > 1 && t2.Length > 1)
            {
                t1.Normalize(); t2.Normalize();
                double cross = t1.X * t2.Y - t1.Y * t2.X;
                double dot = Math.Clamp(t1.X * t2.X + t1.Y * t2.Y, -1, 1);
                prof.Curv[k] = Math.Atan2(cross, dot);   // + = turning left (left = inner, right = outer)
            }
        }
        // asymmetric left/right half-widths (so shorelines are not parallel ribbons)
        // at least 5 m per side, for rivers under 10 m half the smallest width (before always 5 m: a river was never narrower than 10 m, and
        // with widthMax under about 8 m maxHw fell below minHw and Math.Clamp threw – "Connect lakes" allows 6 m)
        double minHw = Math.Max(Math.Min(5.0, widthMin * 0.5), widthMin * 0.36), maxHw = Math.Max(minHw, widthMax * 0.62);
        for (int k = 0; k < np; k++)
        {
            int a = (int)prof.Arc[k];
            double baseO = RoadApplier.Octave(a, 0, 240, seed + 31) * 0.65 + RoadApplier.Octave(a, 0, 85, seed + 37) * 0.35;
            double baseW = widthMin + (widthMax - widthMin) * (0.5 + 0.5 * baseO);
            double eb = prof.EndBlend[k];
            for (int side = 0; side < 2; side++)
            {
                double sg = side == 0 ? 1 : -1;
                double sideO = RoadApplier.Octave(a, side * 5381, 130, seed + 43) * 0.6 + RoadApplier.Octave(a, side * 5381, 44, seed + 47) * 0.4;
                double hw = baseW * 0.5 + sideO * 4.5;
                hw = Math.Clamp(hw, minHw, maxHw);
                prof.Hw[side][k] = hw * eb + (widthMax * 0.5) * (1 - eb);

                // terrain height beside this side of the river
                double rel = 0;
                foreach (double off in new[] { 12.0, 26, 42 })
                {
                    var q = line[k] + prof.Normals[k] * sg * (prof.Hw[side][k] + off);
                    rel += sampleGround(q.X, q.Y) - bank0;
                }
                rel /= 3;
                // outer bend (Prallhang) leans steeper/cliff, inner bend (Gleithang) leans flatter/meadow
                double outer = Math.Clamp(-sg * prof.Curv[k] * 2.4, -1, 1);
                double nSlow = RoadApplier.Octave(a, side * 7919, 190, seed + 41);
                double nMed = RoadApplier.Octave(a, side * 7919, 75, seed + 49);
                double raw = 0.5 + 0.48 * nSlow + 0.24 * nMed + 0.24 * outer;
                // in very high terrain (> 12 m above bank) favour steeper walls/terraces; in low/moderate terrain allow full range
                double highBias = 0.30 * Smooth((rel - 10) / 14);
                double lowBias = -0.18 * (1 - Smooth((rel - 2) / 6));
                prof.Style[side][k] = Math.Clamp(raw + highBias + lowBias, 0, 1);

                double tNoise = 0.5 + 0.5 * RoadApplier.Octave(a, side * 6133, 150, seed + 53);
                prof.Terrace[side][k] = Smooth((tNoise - 0.35) / 0.45);
                prof.BenchW[side][k] = 14 + 12 * (0.5 + 0.5 * RoadApplier.Octave(a, side * 4217, 170, seed + 59));
            }
        }
        // smooth style/terrace/bench along the river (~24 m window) so transitions are natural
        int win = Math.Max(1, (int)Math.Round(24 / Math.Max(0.5, step)));
        static void SmoothArr(double[] arr, int w)
        {
            int n = arr.Length;
            var sm = new double[n];
            double run = 0; int cnt = 0;
            for (int k = -w; k < n + w; k++)
            {
                if (k + w < n) { run += arr[k + w]; cnt++; }
                if (k - w - 1 >= 0) { run -= arr[k - w - 1]; cnt--; }
                if (k >= 0 && k < n) sm[k] = run / Math.Max(1, cnt);
            }
            Array.Copy(sm, arr, n);
        }
        for (int side = 0; side < 2; side++)
        {
            SmoothArr(prof.Style[side], win);
            SmoothArr(prof.Terrace[side], win);
            SmoothArr(prof.BenchW[side], win);
        }
        return prof;
    }

    /// subtle 2D shoreline edge wobble (m), tapered at the lake mouths
    internal static double EdgeWobble(int x, int z, int seed, double endBlend)
        => endBlend * (2.4 * RoadApplier.Octave(x, z, 28, seed + 71) + 1.1 * RoadApplier.Octave(x, z, 11, seed + 73));

    /// bank height profile above the water for distance u > 0 from the water edge, blending smoothly between:
    /// flat riverside meadow (style 0), gentle slope (0.33), medium/terraced bank (0.67) and steep cliff wall (1.0)
    internal static double BankWall(double u, int x, int z, int seed, double bank, double style, double terrace, double benchW)
    {
        double un = u * (1 + 0.30 * RoadApplier.Octave(x, z, 28, seed + 11));
        // A: flat riverside meadow / floodplain bench
        double flatSlope = 0.035 + 0.025 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 55, seed + 19));
        double flatRise = 0.24 + 0.12 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 48, seed + 21));
        double wFlat = bank + (un < benchW ? flatSlope * un : flatSlope * benchW + flatRise * (un - benchW))
            + 0.25 * RoadApplier.Octave(x, z, 18, seed + 23) * Math.Min(1, un / 10);

        // B: gentle rolling slope (14–24 %)
        double gSlope = 0.14 + 0.10 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 52, seed + 25));
        double wGentle = bank + gSlope * un + 0.45 * RoadApplier.Octave(x, z, 16, seed + 27) * Math.Min(1, un / 8);

        // C: medium-steep bank, optionally with a mid-height river terrace
        double wMed = bank + (un < 8 ? 0.85 * un : 6.8 + 0.38 * (un - 8))
            + 1.0 * RoadApplier.Octave(x, z, 14, seed + 13) * Math.Min(1, un / 6);
        if (terrace > 0.01)
        {
            double tRun = 7 + 4 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 65, seed + 33));
            double tH = 0.55 * tRun;
            double tShelf = 10 + 7 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 70, seed + 35));
            double wTer = un < tRun ? bank + 0.55 * un
                : un < tRun + tShelf ? bank + tH + 0.06 * (un - tRun)
                : bank + tH + 0.06 * tShelf + 0.48 * (un - tRun - tShelf);
            wTer += 0.5 * RoadApplier.Octave(x, z, 15, seed + 37) * Math.Min(1, un / 6);
            wMed += (wTer - wMed) * terrace;
        }

        // D: steep cliff wall (cut bank / Prallhang)
        double cSlope = 1.55 + 0.55 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 42, seed + 39));
        double cRun = 8 + 4 * (0.5 + 0.5 * RoadApplier.Octave(x, z, 58, seed + 41));
        double wCliff = bank + (un < cRun ? cSlope * un : cSlope * cRun + 0.55 * (un - cRun))
            + 1.4 * RoadApplier.Octave(x, z, 13, seed + 43) * Math.Min(1, un / 5);

        double wall;
        if (style < 0.33) wall = wFlat + (wGentle - wFlat) * Smooth(style / 0.33);
        else if (style < 0.67) wall = wGentle + (wMed - wGentle) * Smooth((style - 0.33) / 0.34);
        else wall = wMed + (wCliff - wMed) * Smooth((style - 0.67) / 0.33);
        return Math.Max(bank, wall);
    }

    /// cells that stay untouched: roads, buildings, parts and tiles (+ 1 m)
    static bool IsProtected(WorldData w, int i, HashSet<int> prefabCells) => w.RoadMask[i] != 0 || prefabCells.Contains(i);

    /// footprints (+1 m) of all prefabs near the line
    static HashSet<int> PrefabCells(WorldData w, List<Point> line)
    {
        var cells = new HashSet<int>();
        double lx0 = line.Min(p => p.X) - ValleyMax - 40, lx1 = line.Max(p => p.X) + ValleyMax + 40;
        double lz0 = line.Min(p => p.Y) - ValleyMax - 40, lz1 = line.Max(p => p.Y) + ValleyMax + 40;
        foreach (var p in w.PrefabSnapshot())
        {
            if (p.X + p.SizeX + 1 < lx0 || p.X - 1 > lx1 || p.Z + p.SizeZ + 1 < lz0 || p.Z - 1 > lz1) continue;
            for (int z = p.Z - 1; z <= p.Z + p.SizeZ; z++)
                for (int x = p.X - 1; x <= p.X + p.SizeX; x++)
                    if (w.Contains(x, z)) cells.Add(w.Index(x, z));
        }
        return cells;
    }

    sealed class Counts { public int Changed, Spared, Water, Embank; public double MaxCut; }

    static void Set(WorldData w, WorldEdits e, Counts c, int i, double h)
    {
        var v = (ushort)Math.Clamp(Math.Round(h * 256), 0, 65535);
        if (v == w.Heights[i]) return;
        e.SetHeight(i, v);
        c.Changed++;
    }

    /// build the river: lower the higher lake to the common level, shape the valley along the line, set the water pixels
    public static RiverStats Build(WorldData w, WorldEdits e, RiverSpec spec)
    {
        e.SkipWaterSync();
        var a = FindLake(w, spec.ClickA.X, spec.ClickA.Y);
        var b = FindLake(w, spec.ClickB.X, spec.ClickB.Y);
        if (a == null || b == null || spec.Line.Count < 2) return new RiverStats(0, 0, 0, 0, 0, 0, 0, 0, 0);
        int L = Math.Min(a.Level, b.Level);
        int size = w.Size, half = w.Half;
        var line = Dense(spec.Line, 1);
        var prefabCells = PrefabCells(w, line);
        var c = new Counts();

        // 1. the higher lake: dig it deeper by the difference, its shore follows over a few meters
        var hi = a.Level > b.Level ? a : b.Level > a.Level ? b : null;
        int lowered = 0, by = 0;
        if (hi != null)
        {
            by = hi.Level - L;
            foreach (int i in hi.Cells)
            {
                Set(w, e, c, i, w.Heights[i] / 256.0 - by);
                e.SetWater(i, (byte)L);
                lowered++;
            }
            double width = Math.Clamp(3.0 * by, 8, 40);
            int m = (int)Math.Ceiling(width) + 2;
            int gx0 = Math.Max(-half + 1, hi.X0 - m), gz0 = Math.Max(-half + 1, hi.Z0 - m);
            int gw = Math.Min(half - 2, hi.X1 + m) - gx0 + 1, gh = Math.Min(half - 2, hi.Z1 + m) - gz0 + 1;
            var mask = new bool[gw * gh];
            foreach (int i in hi.Cells) mask[(i / size - half - gz0) * gw + (i % size - half - gx0)] = true;
            var dist = GroundShaper.Distance(mask, gw, gh);
            for (int g = 0; g < mask.Length; g++)
            {
                if (mask[g] || dist[g] > width) continue;
                int i = w.Index(gx0 + g % gw, gz0 + g / gw);
                if (w.WaterMask[i] != 0 || IsProtected(w, i, prefabCells)) continue;
                double h = w.Heights[i] / 256.0;
                double t = h - by * (1 - Smooth(dist[g] / width));
                t = Math.Max(t, L + BankAbove);
                if (t < h) Set(w, e, c, i, t);
            }
        }

        // 2. and 3. the valley along the line
        var lakes = new HashSet<int>(a.Cells); lakes.UnionWith(b.Cells);
        double total = Valley(w, e, c, spec, line, L, lakes, prefabCells);
        c.Embank += FarBanks(w, e, spec, line);
        e.PaintWaterBiome(spec.Biome);
        return new RiverStats(c.Water, c.Changed, c.Spared, c.MaxCut, c.Embank, lowered, by, L, total);
    }

    /// tool "River": a river along a drawn line with a given water level, shaped like the rivers of the world generator
    /// (varied banks, depth changing along the river, deep pools in the outer bends). Water of the same level that the line
    /// touches stays; the ends of the line simply end in the valley (or in that water).
    public static RiverStats BuildDrawn(WorldData w, WorldEdits e, RiverSpec spec, int level)
    {
        e.SkipWaterSync();
        if (spec.Line.Count < 2) return new RiverStats(0, 0, 0, 0, 0, 0, 0, level, 0);
        var line = Dense(spec.Line, 1);
        var c = new Counts();
        double total = Valley(w, e, c, spec, line, level, new HashSet<int>(), PrefabCells(w, line));
        c.Embank += FarBanks(w, e, spec, line);
        e.PaintWaterBiome(spec.Biome);
        return new RiverStats(c.Water, c.Changed, c.Spared, c.MaxCut, c.Embank, 0, 0, level, total);
    }

    /// dry land in the 16 m chunks of the new water above the far water plane of the game (see FarWater)
    static int FarBanks(WorldData w, WorldEdits e, RiverSpec spec, List<Point> line)
    {
        int R = (int)Math.Ceiling(spec.WidthMax * 0.65 + 4 + ValleyMax);
        return FarWater.RaiseBanks(w, e, (int)Math.Floor(line.Min(p => p.X)) - R, (int)Math.Floor(line.Min(p => p.Y)) - R,
            (int)Math.Ceiling(line.Max(p => p.X)) + R, (int)Math.Ceiling(line.Max(p => p.Y)) + R);
    }

    /// depth along the river (like the rivers of the world generator, 2026-11-26): the set depth changes slowly between 70 and 100 %,
    /// deep pools add up to 40 %; the deepest water wanders and hugs the outer side of bends (shift = fraction of the half width, + = left)
    internal static (double[] Depths, double[] Shift) BedProfile(RiverProfile prof, double depth, int seed)
    {
        int np = prof.Arc.Length;
        var depths = new double[np];
        var shift = new double[np];
        for (int k = 0; k < np; k++)
        {
            int a = (int)prof.Arc[k];
            double pool = Math.Max(0, RoadApplier.Octave(a, 0, 110, seed + 57) - 0.4) / 0.6;
            depths[k] = depth * (0.7 + 0.3 * (0.5 + 0.5 * RoadApplier.Octave(a, 0, 380, seed + 51))) + 0.4 * depth * pool;
            shift[k] = Math.Clamp(0.28 * RoadApplier.Octave(a, 0, 170, seed + 61) - 0.30 * Math.Clamp(prof.Curv[k] * 2, -1, 1), -0.45, 0.45);
        }
        return (depths, shift);
    }

    /// depth of the bed below the level at a cell of the water (lat = side distance from the middle, + = left)
    internal static double BedDepth(double lat, double hw, double style, double depthHere, double shift, int x, int z, int seed)
    {
        double t = Math.Min(1.3, Math.Abs(lat / Math.Max(1, hw) - shift) / (1 + Math.Abs(shift)));
        double edge = Math.Min(0.35 + 0.95 * style, depthHere);   // shallow at flat meadows, deeper at steep walls
        double depth = edge + Math.Max(0, depthHere - edge) * Math.Pow(Math.Max(0, 1 - t * t), 0.7);
        depth += (0.6 * RoadApplier.Octave(x, z, 9, seed + 5) + 0.5 * RoadApplier.Octave(x, z, 23, seed + 7)) * Math.Max(0, 1 - t);
        return Math.Max(0.3, depth);
    }

    /// the valley along a dense (1 m) line: bed, banks, embankments, no dry land below the waterline; returns the length
    static double Valley(WorldData w, WorldEdits e, Counts c, RiverSpec spec, List<Point> line, int L, HashSet<int> lakes, HashSet<int> prefabCells)
    {
        int half = w.Half;
        int np = line.Count;
        double bank = L + BankAbove;
        var prof = BuildProfile(line, spec.WidthMin, spec.WidthMax, spec.Seed, bank,
            (gx, gz) => w.Contains((int)gx, (int)gz) ? w.HeightAt((int)gx, (int)gz) : bank, 1);
        var (depths, shift) = BedProfile(prof, spec.Depth, spec.Seed);
        double total = prof.Arc[^1];
        double maxR = spec.WidthMax * 0.65 + 4 + ValleyMax;
        int R = (int)Math.Ceiling(maxR);
        int bx0 = Math.Max(-half + 2, (int)Math.Floor(line.Min(p => p.X)) - R), bx1 = Math.Min(half - 3, (int)Math.Ceiling(line.Max(p => p.X)) + R);
        int bz0 = Math.Max(-half + 2, (int)Math.Floor(line.Min(p => p.Y)) - R), bz1 = Math.Min(half - 3, (int)Math.Ceiling(line.Max(p => p.Y)) + R);
        int bw = bx1 - bx0 + 1, bh = bz1 - bz0 + 1;
        var dmin = new float[bw * bh];
        var near = new int[bw * bh];
        Array.Fill(dmin, float.MaxValue);
        for (int k = 0; k < np; k++)
        {
            var p = line[k];
            int px = (int)Math.Floor(p.X), pz = (int)Math.Floor(p.Y);
            for (int z = Math.Max(bz0, pz - R); z <= Math.Min(bz1, pz + R); z++)
            {
                double dz = z + 0.5 - p.Y;
                for (int x = Math.Max(bx0, px - R); x <= Math.Min(bx1, px + R); x++)
                {
                    double dx = x + 0.5 - p.X;
                    double d2 = dx * dx + dz * dz;
                    if (d2 > (double)R * R) continue;
                    int g = (z - bz0) * bw + x - bx0;
                    if (d2 < (double)dmin[g] * dmin[g]) { dmin[g] = (float)Math.Sqrt(d2); near[g] = k; }
                }
            }
        }
        var wet = new bool[bw * bh];
        for (int g = 0; g < dmin.Length; g++)
        {
            if (dmin[g] == float.MaxValue) continue;
            int x = bx0 + g % bw, z = bz0 + g / bw;
            int i = w.Index(x, z);
            int kn = near[g];
            double d = dmin[g];
            double lat = (x + 0.5 - line[kn].X) * prof.Normals[kn].X + (z + 0.5 - line[kn].Y) * prof.Normals[kn].Y;
            int side = lat >= 0 ? 0 : 1;
            double hw = Math.Max(4.5, prof.Hw[side][kn] + EdgeWobble(x, z, spec.Seed, prof.EndBlend[kn]));
            double style = prof.Style[side][kn];
            double u = d - hw;   // distance from the water edge (negative = in the water)
            double h = w.Heights[i] / 256.0;
            bool prot = IsProtected(w, i, prefabCells);
            if (u <= 0)
            {
                if (prot) { c.Spared++; continue; }
                double target = Math.Min(h, L - BedDepth(lat, hw, style, depths[kn], shift[kn], x, z, spec.Seed));
                Set(w, e, c, i, target);
                if (w.WaterMask[i] != L) e.SetWater(i, (byte)L);
                wet[g] = true;
                c.Water++;
                continue;
            }
            if (lakes.Contains(i) || w.WaterMask[i] == L) { wet[g] = true; continue; }   // lake water stays
            if (prot) { c.Spared++; continue; }
            double wall = BankWall(u, x, z, spec.Seed, bank, style, prof.Terrace[side][kn], prof.BenchW[side][kn]);
            // land lower than the bank: embankment with its crest 1.3 m above the bank and 8 m wide, then down 1:2 (with a little noise) until it
            // meets the land. A crest only 0.1 m above the bank and 4 m wide disappeared in the coarse far terrain of the game: far away the
            // river water showed over the land behind it and vanished when coming close (Sodecu-Land 1872/-1310)
            double un = u * (1 + 0.25 * RoadApplier.Octave(x, z, 30, spec.Seed + 11));
            double levee = bank + 1.3 - Math.Max(0, un - 8) * 0.5 + 0.3 * RoadApplier.Octave(x, z, 11, spec.Seed + 17) * Math.Min(1, Math.Max(0, un - 8) / 4);
            double target2 = Math.Clamp(h, Math.Min(levee, wall), wall);
            if (target2 < h && u < 8) c.MaxCut = Math.Max(c.MaxCut, h - bank);
            if (target2 > h + 0.01) c.Embank++;
            // the outermost 15 m run out into the untouched terrain (no edge where the valley ends)
            double fade = Smooth((maxR - hw - u) / 15);
            target2 = h + (target2 - h) * fade;
            if (Math.Abs(target2 - h) > 0.004) Set(w, e, c, i, target2);
        }
        // no dry cell beside the water below the waterline (it would be flooded land with grass in the water)
        for (int g = 0; g < dmin.Length; g++)
        {
            if (wet[g] || dmin[g] == float.MaxValue) continue;
            int x = g % bw, z = g / bw;
            bool besideWater = (x > 0 && wet[g - 1]) || (x < bw - 1 && wet[g + 1]) || (z > 0 && wet[g - bw]) || (z < bh - 1 && wet[g + bw]);
            if (!besideWater) continue;
            int i = w.Index(bx0 + x, bz0 + z);
            if (w.Heights[i] >= (L + Lake.WetMargin) * 256.0 || IsProtected(w, i, prefabCells)) continue;
            Set(w, e, c, i, bank + 0.05);
            c.Embank++;
        }
        return total;
    }
}
