using System.Windows;

namespace MapEditor.Map;

/// Smooth center line for a searched road (the user's request 2026-11-13: town roads had kinks of 7–17 m radius – every
/// 10 m step of the path search may turn by 22.5°, and the old smoothing acted only over a few meters; the curve "broke
/// off" at the tile). The search now only gives the corridor; the line is a smoothing spline through it:
/// minimise  Σ Bend·|p[i-1] − 2p[i] + p[i+1]|²  +  Σ weight[i]·|p[i] − corridor[i]|²
/// with points every 2 m. The straight pieces at both tiles are fixed (two or more fixed points in a row also fix the
/// direction), so the line leaves and enters both tiles the same way. Where the line moves more than MaxDev from the
/// corridor or hits blocked ground, it is pulled closer there and solved again.
public static class RoadLine
{
    const double Spacing = 2;        // m between points
    const double Bend = 4000;        // weight of the bending (larger = wider curves); about 40–60 m radius on a 22.5° kink
    const double MaxDev = 6;         // furthest the line may leave the searched corridor (m)
    const double ControlStep = 5;    // spacing of the control points handed to the road brush (even: no overshoot)
    internal static int Fallbacks;   // number of searched roads that had to use the old smoothing (for tests)

    /// start/goal: points 1 m outside the tiles' straight piece begin (as in RoadRouter.SmoothPath); sd/gd = directions;
    /// raw = searched corridor from the end of the start straight to the end of the goal straight. Null = no fitting line.
    public static List<Point> Fit(List<Point> raw, Point start, Vector sd, Point goal, Vector gd, Func<Point, bool> blocked, double straight = 10)
    {
        if (raw.Count < 2) return null;
        sd.Normalize(); gd.Normalize();
        var lead = start + sd * straight;
        var tail = goal - gd * straight;
        // corridor polyline: start straight, searched path (without points inside the straight pieces), goal straight
        var poly = new List<Point> { start, lead };
        foreach (var q in raw)
            if ((q - lead).Length > 4 && (q - tail).Length > 4 && (q - start).Length > straight + 2 && (q - goal).Length > straight + 2) poly.Add(q);
        poly.Add(tail); poly.Add(goal);
        var r = Resample(poly, Spacing, out int leadIdx, out int tailIdx, lead, tail);
        int n = r.Count;
        if (n < 8 || leadIdx < 0) return null;
        var weight = new double[n];
        var fixedPt = new bool[n];
        for (int i = 0; i < n; i++)
        {
            fixedPt[i] = i <= leadIdx || i >= tailIdx;
            weight[i] = fixedPt[i] ? 1e9 : 1;
        }
        Point[] p = null;
        for (int round = 0; round < 12; round++)
        {
            p = Solve(r, weight);
            bool ok = true;
            var raise = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (fixedPt[i]) continue;
                double dev = DistanceToCorridor(r, i, p[i]);
                if (dev > MaxDev || blocked(p[i])) { ok = false; raise[i] = 1; }
            }
            if (!ok)
                // pull closer gently around the place (bell over ±8 points) – a sudden change of the weight gave a kink
                for (int i = 0; i < n; i++)
                    if (raise[i] > 0)
                        for (int k = Math.Max(0, i - 8); k <= Math.Min(n - 1, i + 8); k++)
                            if (!fixedPt[k]) weight[k] *= 1 + 2 * Math.Exp(-(k - i) * (k - i) / 18.0);
            if (ok) break;
            if (round == 11) return null;
        }
        // control points every ControlStep m along the line (exact start, end of the straight pieces and goal kept)
        var line = new List<Point>();
        double run = 0, next = 0;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) run += (p[i] - p[i - 1]).Length;
            bool keep = i == 0 || i == n - 1 || i == leadIdx || i == tailIdx || run >= next - 1e-6;
            // leave ControlStep before the goal free of extra points (no short last piece)
            if (keep && i != n - 1 && i != tailIdx && i > tailIdx - 1) keep = false;
            if (keep) { line.Add(p[i]); next = run + ControlStep; }
        }
        return line;
    }

    /// polyline at even spacing (about `step`); the indices of lead and tail are the points closest to them, set exactly
    static List<Point> Resample(List<Point> poly, double step, out int leadIdx, out int tailIdx, Point lead, Point tail)
    {
        var res = new List<Point> { poly[0] };
        for (int i = 0; i + 1 < poly.Count; i++)
        {
            double len = (poly[i + 1] - poly[i]).Length;
            int parts = Math.Max(1, (int)Math.Round(len / step));
            for (int k = 1; k <= parts; k++) res.Add(poly[i] + (poly[i + 1] - poly[i]) * (k / (double)parts));
        }
        leadIdx = Nearest(res, lead); tailIdx = Nearest(res, tail);
        res[leadIdx] = lead; res[tailIdx] = tail;
        if (tailIdx <= leadIdx + 2) { leadIdx = tailIdx = -1; }
        return res;
    }

    /// distance of q to the corridor polyline near index i (±20 points): the line may slide along the corridor
    static double DistanceToCorridor(List<Point> r, int i, Point q)
    {
        double best = double.MaxValue;
        for (int k = Math.Max(0, i - 20); k < Math.Min(r.Count - 1, i + 20); k++)
        {
            Vector ab = r[k + 1] - r[k];
            double l2 = ab.LengthSquared;
            double t = l2 < 1e-9 ? 0 : Math.Clamp(((q - r[k]) * ab) / l2, 0, 1);
            best = Math.Min(best, (q - (r[k] + ab * t)).Length);
        }
        return best;
    }

    static int Nearest(List<Point> pts, Point q)
    {
        int best = 0; double bd = double.MaxValue;
        for (int i = 0; i < pts.Count; i++) { double d = (pts[i] - q).LengthSquared; if (d < bd) { bd = d; best = i; } }
        return best;
    }

    /// smoothing spline for the road network (the user's request 2026-11-16: country roads looked "exact" – long straight
    /// pieces with short kinks between them, because the path search turns in 22.5° steps every 16 m). r = corridor at even
    /// spacing, fixedPt = points that must stay (two in a row also fix the direction), bend = stiffness (larger = wider curves),
    /// maxDev(i) = how far point i may leave the corridor, blocked(i, p) = the line must not go there. Null = no fitting line.
    public static Point[] Spline(List<Point> r, bool[] fixedPt, double bend, Func<int, double> maxDev, Func<int, Point, bool> blocked)
    {
        int n = r.Count;
        if (n < 5) return null;
        var weight = new double[n];
        for (int i = 0; i < n; i++) weight[i] = fixedPt[i] ? 1e9 : 1;
        for (int round = 0; round < 14; round++)
        {
            var p = Solve(r, weight, bend);
            bool ok = true;
            var raise = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (fixedPt[i]) continue;
                double dev = DistanceToCorridor(r, i, p[i]);
                if (dev <= maxDev(i) && !blocked(i, p[i])) continue;
                ok = false;
                raise[i] = true;
            }
            if (ok) return p;
            // pull closer gently around violating places (bell over ±10 points, max instead of stacking per point so corners stay soft)
            for (int k = 0; k < n; k++)
            {
                if (fixedPt[k]) continue;
                double boost = 0;
                for (int i = Math.Max(0, k - 10); i <= Math.Min(n - 1, k + 10); i++)
                    if (raise[i]) boost = Math.Max(boost, Math.Exp(-(k - i) * (k - i) / 30.0));
                if (boost > 0) weight[k] *= 1 + 2.5 * boost;
            }
        }
        return null;
    }

    static Point[] Solve(List<Point> r, double[] w) => Solve(r, w, Bend);

    /// (W + Bend·DᵀD) p = W r, D = second differences; pentadiagonal, solved per coordinate
    static Point[] Solve(List<Point> r, double[] w, double bendWeight)
    {
        int n = r.Count;
        // band matrix: a[i][k] for column i + k - 2 (k = 0..4)
        var a = new double[n, 5];
        for (int i = 0; i < n; i++) a[i, 2] = w[i];
        for (int j = 1; j + 1 < n; j++)
        {
            // second difference row: coefficients 1, -2, 1 at j-1, j, j+1
            int[] idx = { j - 1, j, j + 1 };
            double[] c = { 1, -2, 1 };
            for (int u = 0; u < 3; u++)
                for (int v = 0; v < 3; v++)
                    a[idx[u], idx[v] - idx[u] + 2] += bendWeight * c[u] * c[v];
        }
        var bx = new double[n]; var bz = new double[n];
        for (int i = 0; i < n; i++) { bx[i] = w[i] * r[i].X; bz[i] = w[i] * r[i].Y; }
        // Gaussian elimination on the band (symmetric positive definite, no pivoting needed)
        for (int i = 0; i < n; i++)
        {
            double piv = a[i, 2];
            for (int k = 1; k <= 2 && i + k < n; k++)
            {
                double f = a[i + k, 2 - k] / piv;
                if (f == 0) continue;
                for (int m = 0; m <= 2 && i + m < n; m++) a[i + k, 2 - k + m] -= f * a[i, 2 + m];
                bx[i + k] -= f * bx[i]; bz[i + k] -= f * bz[i];
            }
        }
        var x = new double[n]; var z = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double sx = bx[i], sz = bz[i];
            for (int m = 1; m <= 2 && i + m < n; m++) { sx -= a[i, 2 + m] * x[i + m]; sz -= a[i, 2 + m] * z[i + m]; }
            x[i] = sx / a[i, 2]; z[i] = sz / a[i, 2];
        }
        var p = new Point[n];
        for (int i = 0; i < n; i++) p[i] = new Point(x[i], z[i]);
        return p;
    }
}
