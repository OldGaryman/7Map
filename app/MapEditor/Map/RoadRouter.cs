using System.Windows;

namespace MapEditor.Map;

/// Path search for curved roads (the user's request, 2026-11-07, model: his pine forest city in Fidife-Land – districts
/// at their own height, roads in curves around the hill instead of straight ramps). A* over a 4 m grid with 16 driving directions; per step
/// 10 m straight or turned by 22.5° (smallest curve about 25 m, hairpins possible). Cost = length, rising sharply if the
/// terrain is steeper than the allowed gradient, a bit for cross slope (earthwork) and curves. So the road follows the
/// contour lines and winds if the height difference demands it. The exact longitudinal profile is calculated afterwards by the road brush.
public static class RoadRouter
{
    const int Cell = 4;          // grid (m)
    const double Step = 10;      // step length (m)
    const int Dirs = 16;

    static readonly (double X, double Z)[] Unit = Enumerable.Range(0, Dirs)
        .Select(k => (Math.Cos(k * Math.PI * 2 / Dirs), Math.Sin(k * Math.PI * 2 / Dirs))).ToArray();

    /// direction (0 = east, 4 = north, 8 = west, 12 = south) to a vector
    public static int DirIndex(double dx, double dz) => ((int)Math.Round(Math.Atan2(dz, dx) / (Math.PI * 2 / Dirs)) % Dirs + Dirs) % Dirs;

    /// path from start (driving direction startDir) to goal (arrival in direction goalDir); blocked = place not passable,
    /// penalty = factor on the cost (≥ 1, e.g. at already planned roads). adjust = height instead of the terrain (x, z, terrain → height). Returns the points (start … goal) or null.
    public static List<Point> Route(WorldData w, Point start, int startDir, Point goal, int goalDir, double maxGrade,
        Func<double, double, bool> blocked, Func<double, double, double> penalty = null, Func<double, double, double, double> adjust = null, double margin = 320)
    {
        int gx0 = (int)Math.Floor((Math.Min(start.X, goal.X) - margin) / Cell) * Cell, gz0 = (int)Math.Floor((Math.Min(start.Y, goal.Y) - margin) / Cell) * Cell;
        int gw = (int)Math.Ceiling((Math.Max(start.X, goal.X) + margin - gx0) / Cell), gh = (int)Math.Ceiling((Math.Max(start.Y, goal.Y) + margin - gz0) / Cell);
        int n = gw * gh;
        // height per cell (mean of 4 points, smooths small stuff), passable, cost factor
        var h = new float[n];
        var free = new bool[n];
        var factor = new float[n];
        Parallel.For(0, gh, gz =>
        {
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx;
                double x = gx0 + gx * Cell + Cell / 2.0, z = gz0 + gz * Cell + Cell / 2.0;
                bool inside = x > -w.Half + 20 && x < w.Half - 20 && z > -w.Half + 20 && z < w.Half - 20;
                h[g] = (float)((w.HeightAt((int)x - 1, (int)z - 1) + w.HeightAt((int)x + 1, (int)z - 1) + w.HeightAt((int)x - 1, (int)z + 1) + w.HeightAt((int)x + 1, (int)z + 1)) / 4);
                free[g] = inside && !w.WaterAt((int)x, (int)z) && !blocked(x, z);
                factor[g] = (float)(penalty?.Invoke(x, z) ?? 1);
            }
        });
        // measure steep walls (rocks, slope edges) before smoothing: no road there (slopes would become cliffs, and two
        // loops of a road on top of each other would give steps)
        var steep = new float[n];
        Parallel.For(1, gh - 1, gz =>
        {
            for (int gx = 1; gx < gw - 1; gx++)
            {
                int g = gz * gw + gx;
                steep[g] = Math.Max(Math.Abs(h[g + 1] - h[g - 1]), Math.Abs(h[g + gw] - h[g - gw])) / (2f * Cell);
            }
        });
        // smooth the terrain for the path search (about 20 m): the longitudinal profile evens out small bumps and gullies anyway; without smoothing
        // every bump counted as too steep and the roads made needless detours
        h = BoxBlur(h, gw, gh, 2);
        h = BoxBlur(h, gw, gh, 2);
        // height after shaping (e.g. near the tiles their ground height): adjust(x, z, terrain)
        if (adjust != null)
            Parallel.For(0, gh, gz =>
            {
                for (int gx = 0; gx < gw; gx++) { int g = gz * gw + gx; h[g] = (float)adjust(gx0 + gx * Cell + Cell / 2.0, gz0 + gz * Cell + Cell / 2.0, h[g]); }
            });
        int CellOf(double x, double z)
        {
            int cx = (int)Math.Floor((x - gx0) / Cell), cz = (int)Math.Floor((z - gz0) / Cell);
            return cx < 0 || cz < 0 || cx >= gw || cz >= gh ? -1 : cz * gw + cx;
        }
        (double X, double Z) Center(int g) => (gx0 + (g % gw) * Cell + Cell / 2.0, gz0 + (g / gw) * Cell + Cell / 2.0);

        // the search runs from 10 m after the start to 10 m before the goal, each in driving direction: so every curve begins and ends
        // only after the straight connecting piece (before, the road turned off there with a 4–6 m radius)
        var searchStart = new Point(start.X + Unit[startDir].X * 10, start.Y + Unit[startDir].Z * 10);
        var searchGoal = new Point(goal.X - Unit[goalDir].X * 10, goal.Y - Unit[goalDir].Z * 10);
        int s0 = CellOf(searchStart.X, searchStart.Y), goalCell = CellOf(searchGoal.X, searchGoal.Y);
        if (s0 < 0 || goalCell < 0) return null;
        double goalH = h[goalCell];
        var gScore = new float[n * Dirs];
        Array.Fill(gScore, float.MaxValue);
        var parent = new int[n * Dirs];
        var queue = new PriorityQueue<int, float>();
        int first = s0 * Dirs + startDir;
        gScore[first] = 0;
        parent[first] = -1;
        double Heuristic(int g)
        {
            var (x, z) = Center(g);
            double d = Math.Sqrt((x - searchGoal.X) * (x - searchGoal.X) + (z - searchGoal.Y) * (z - searchGoal.Y));
            return Math.Max(d, Math.Abs(goalH - h[g]) / maxGrade);   // for the height difference at least this length is needed
        }
        queue.Enqueue(first, (float)Heuristic(s0));
        int found = -1, expanded = 0;
        while (queue.TryDequeue(out int state, out float pr))
        {
            int g = state / Dirs, d = state % Dirs;
            if (pr > gScore[state] + Heuristic(g) * 1.3 + 0.01) continue;   // outdated entry
            if (++expanded > 3_000_000) break;
            var (cx, cz) = Center(g);
            for (int turn = -1; turn <= 1; turn++)
            {
                int nd = (d + turn + Dirs) % Dirs;
                double nx = cx + Unit[nd].X * Step, nz = cz + Unit[nd].Z * Step;
                int ng = CellOf(nx, nz);
                if (ng < 0 || !free[ng]) continue;
                int mid = CellOf(cx + Unit[nd].X * Step / 2, cz + Unit[nd].Z * Step / 2);
                if (mid < 0 || !free[mid]) continue;
                double grade = Math.Abs(h[ng] - h[g]) / Step;
                double cost = Step * (1 + (turn != 0 ? 0.12 : 0));
                // steeper than allowed: much more expensive (with some reserve for the smoothing of the longitudinal profile)
                double excess = grade - maxGrade * 0.85;
                if (excess > 0) cost += Step * (40 * excess + 400 * excess * excess);
                // cross slope of the terrain (slopes, earthwork)
                double px = -Unit[nd].Z, pz = Unit[nd].X;
                int l = CellOf(nx + px * 8, nz + pz * 8), r = CellOf(nx - px * 8, nz - pz * 8);
                if (l >= 0 && r >= 0)
                {
                    double cross = Math.Abs(h[l] - h[r]) / 16;
                    if (cross > 0.12) cost += Step * 3 * (cross - 0.12);
                }
                if (steep[ng] > 0.6) cost += Step * 6 * (steep[ng] - 0.6);
                cost *= Math.Max(factor[ng], factor[g]);
                int ns = ng * Dirs + nd;
                float tentative = (float)(gScore[state] + cost);
                if (tentative >= gScore[ns]) continue;
                gScore[ns] = tentative;
                parent[ns] = state;
                double dg = Math.Sqrt((nx - searchGoal.X) * (nx - searchGoal.X) + (nz - searchGoal.Y) * (nz - searchGoal.Y));
                int dd = Math.Abs(nd - goalDir) % Dirs;
                if (dg <= 5 && Math.Min(dd, Dirs - dd) <= 1) { found = ns; break; }
                queue.Enqueue(ns, (float)(tentative + Heuristic(ng) * 1.3));
            }
            if (found >= 0) break;
        }
        if (found < 0) return null;
        var cells = new List<Point>();
        for (int st = found; st >= 0; st = parent[st])
        {
            var (x, z) = Center(st / Dirs);
            cells.Add(new Point(x, z));
            if (st == first) break;
        }
        cells.Reverse();
        // grid points jump back and forth by up to 3 m; laid through them, the curve of the road brush gave hooks, zigzag and
        // places where the road ran briefly back over itself (step; Wahava-Land 2026-11-07). Therefore smooth.
        bool BlockedAt(Point p) { int g = CellOf(p.X, p.Y); return g < 0 || !free[g]; }
        return SmoothPath(cells, start, Unit[startDir], goal, Unit[goalDir], BlockedAt);
    }

    /// smooth the path: at start and goal 10 m straight in driving direction each, between them the grid points (the first/last 8 m dropped),
    /// densified to 2 m and lightly averaged (removes only the trembling of the grid, the curves stay); if the smoothed line hits blocked
    /// terrain, it is smoothed less. Then thinned out so that the control points are at least 10 m apart.
    internal static List<Point> SmoothPath(List<Point> raw, Point start, (double X, double Z) sd, Point goal, (double X, double Z) gd, Func<Point, bool> blocked)
    {
        var lead = new Point(start.X + sd.X * 10, start.Y + sd.Z * 10);
        var tail = new Point(goal.X - gd.X * 10, goal.Y - gd.Z * 10);
        var pts = new List<Point> { start, lead };
        foreach (var p in raw)
            if ((p - start).Length > 12 && (p - goal).Length > 12 && (p - lead).Length > 9 && (p - tail).Length > 9) pts.Add(p);
        pts.Add(tail);
        pts.Add(goal);
        // densify to 2 m
        var dense = new List<Point> { pts[0] };
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double len = (pts[i + 1] - pts[i]).Length;
            int steps = Math.Max(1, (int)Math.Ceiling(len / 2));
            for (int k = 1; k <= steps; k++) dense.Add(pts[i] + (pts[i + 1] - pts[i]) * (k / (double)steps));
        }
        // fixed: the straight pieces at start and goal
        var fixedAt = new bool[dense.Count];
        for (int i = 0; i < dense.Count; i++)
            fixedAt[i] = (dense[i] - start).Length <= 10.5 || (dense[i] - goal).Length <= 10.5;
        List<Point> result = null;
        foreach (int iterations in new[] { 10, 5, 0 })
        {
            var cur = dense.ToArray();
            for (int it = 0; it < iterations; it++)
            {
                var next = (Point[])cur.Clone();
                for (int i = 1; i < cur.Length - 1; i++)
                    if (!fixedAt[i]) next[i] = new Point((cur[i - 1].X + cur[i].X + cur[i + 1].X) / 3, (cur[i - 1].Y + cur[i].Y + cur[i + 1].Y) / 3);
                cur = next;
            }
            if (iterations > 0 && cur.Any(blocked)) continue;
            result = cur.ToList();
            break;
        }
        // thin out: Douglas-Peucker 1 m, then minimum distance 10 m (start, end and the straight pieces stay)
        var simple = Simplify(result, 1.0);
        var spaced = new List<Point> { simple[0] };
        for (int i = 1; i < simple.Count - 1; i++)
        {
            bool keep = (simple[i] - lead).Length < 0.5 || (simple[i] - tail).Length < 0.5;
            if (keep || ((simple[i] - spaced[^1]).Length >= 10 && (simple[i] - simple[^1]).Length >= 10)) spaced.Add(simple[i]);
        }
        spaced.Add(simple[^1]);
        if (!spaced.Any(p => (p - lead).Length < 0.5)) spaced.Insert(1, lead);
        if (!spaced.Any(p => (p - tail).Length < 0.5)) spaced.Insert(spaced.Count - 1, tail);
        // split long straight pieces (at most 20 m): the curve through the points (Catmull-Rom) otherwise overshoots with very
        // uneven spacing (235 m and then 5 m gave a loop with a 2 m radius)
        var even = new List<Point> { spaced[0] };
        for (int i = 1; i < spaced.Count; i++)
        {
            double len = (spaced[i] - spaced[i - 1]).Length;
            int parts = (int)Math.Ceiling(len / 20);
            for (int k = 1; k < parts; k++) even.Add(spaced[i - 1] + (spaced[i] - spaced[i - 1]) * (k / (double)parts));
            even.Add(spaced[i]);
        }
        return even;
    }

    // mean over (2r+1)² cells, separated by rows and columns
    static float[] BoxBlur(float[] src, int gw, int gh, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, gh, z =>
        {
            for (int x = 0; x < gw; x++)
            {
                double s = 0; int n = 0;
                for (int k = Math.Max(0, x - r); k <= Math.Min(gw - 1, x + r); k++) { s += src[z * gw + k]; n++; }
                tmp[z * gw + x] = (float)(s / n);
            }
        });
        Parallel.For(0, gw, x =>
        {
            for (int z = 0; z < gh; z++)
            {
                double s = 0; int n = 0;
                for (int k = Math.Max(0, z - r); k <= Math.Min(gh - 1, z + r); k++) { s += tmp[k * gw + x]; n++; }
                dst[z * gw + x] = (float)(s / n);
            }
        });
        return dst;
    }

    /// Douglas-Peucker: drop points that deviate at most tol from the connecting line
    public static List<Point> Simplify(List<Point> p, double tol)
    {
        if (p.Count < 3) return p;
        var keep = new bool[p.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, p.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double best = 0;
            int bi = -1;
            double dx = p[b].X - p[a].X, dz = p[b].Y - p[a].Y, len = Math.Sqrt(dx * dx + dz * dz);
            for (int i = a + 1; i < b; i++)
            {
                double d = len < 1e-9 ? (p[i] - p[a]).Length : Math.Abs((p[i].X - p[a].X) * dz - (p[i].Y - p[a].Y) * dx) / len;
                if (d > best) { best = d; bi = i; }
            }
            if (bi >= 0 && best > tol) { keep[bi] = true; stack.Push((a, bi)); stack.Push((bi, b)); }
        }
        return p.Where((_, i) => keep[i]).ToList();
    }
}
