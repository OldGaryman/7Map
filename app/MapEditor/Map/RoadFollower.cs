using System.Windows;

namespace MapEditor.Map;

/// Query road pixels, city tiles excluded (their roads belong to the tile and are not edited)
public sealed class RoadScan
{
    public readonly WorldData W;
    readonly List<PlacedPrefab> tiles;

    public RoadScan(WorldData w)
    {
        W = w;
        tiles = w.Prefabs.Where(p => p.IsTile).ToList();
    }

    public bool InTile(double x, double z)
    {
        foreach (var t in tiles) if (t.Contains(x, z)) return true;
        return false;
    }

    public bool Road(double x, double z) => W.RoadAt((int)Math.Floor(x), (int)Math.Floor(z)) != 0 && !InTile(x, z);
}

/// center of an existing road at one place: center point across the road, direction, width (everything including edges)
public sealed record RoadSpot(Point Center, Vector Dir, double Width, byte Kind);

/// result of Extend: points along the road center (every 2 m), measured width, length and reason for the end
/// (null = full length, "end" = road ends, "tile" = city tile, "junction" = crossing road)
public sealed record FollowResult(List<Point> Points, double Width, double Length, string Stop);

/// Finds the center of an existing road and follows it for a stretch (the user's request, 2026-11-02: edit road).
/// It is sampled across the road, the center is the middle of the connected surface.
public static class RoadFollower
{
    const double Step = 0.5;       // step when sampling across the road (m)
    const double MaxHalf = 40;     // a road may be at most this wide (m)

    /// center of the road near the click point (up to 12 m); null if there is no road there (outside of tiles)
    public static RoadSpot Center(RoadScan s, Point click)
    {
        Point? start = null;
        double best = double.MaxValue;
        int cx = (int)Math.Floor(click.X), cz = (int)Math.Floor(click.Y);
        for (int dz = -12; dz <= 12; dz++)
            for (int dx = -12; dx <= 12; dx++)
            {
                double d = dx * dx + dz * dz;
                if (d > 144 || d >= best || !s.Road(cx + dx + 0.5, cz + dz + 0.5)) continue;
                best = d;
                start = new Point(cx + dx + 0.5, cz + dz + 0.5);
            }
        if (start == null) return null;
        var p = start.Value;
        var dir = Axis(s, p, 7) ?? new Vector(1, 0);
        double width = 4;
        for (int it = 0; it < 3; it++)
        {
            var n = new Vector(-dir.Y, dir.X);
            // at most 16 m per side: at large intersections and squares no center is recognizable, then the point stays near the click
            var (lo, hi) = Run(s, p, n, 16);
            p += n * (lo + hi) / 2;
            width = hi - lo + 1;
            dir = Axis(s, p, 7) ?? dir;
        }
        int asphalt = 0, gravel = 0;
        for (int dz = -4; dz <= 4; dz++)
            for (int dx = -4; dx <= 4; dx++)
            {
                byte k = s.W.RoadAt((int)Math.Floor(p.X) + dx, (int)Math.Floor(p.Y) + dz);
                if (k == WorldData.Asphalt) asphalt++; else if (k == WorldData.Gravel) gravel++;
            }
        return new RoadSpot(p, dir, width, asphalt >= gravel ? WorldData.Asphalt : WorldData.Gravel);
    }

    /// direction of the road (main axis of the road pixels in the surroundings); null if too few pixels
    public static Vector? Axis(RoadScan s, Point p, double radius)
    {
        double sx = 0, sz = 0, sxx = 0, szz = 0, sxz = 0;
        int cnt = 0, r = (int)Math.Ceiling(radius);
        for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dz * dz > radius * radius || !s.Road(p.X + dx, p.Y + dz)) continue;
                sx += dx; sz += dz; sxx += dx * dx; szz += dz * dz; sxz += dx * dz; cnt++;
            }
        if (cnt < 8) return null;
        double mx = sx / cnt, mz = sz / cnt;
        double cxx = sxx / cnt - mx * mx, czz = szz / cnt - mz * mz, cxz = sxz / cnt - mx * mz;
        double a = 0.5 * Math.Atan2(2 * cxz, cxx - czz);
        return new Vector(Math.Cos(a), Math.Sin(a));
    }

    /// extent of the connected surface across the direction n, from p (lo ≤ 0 ≤ hi, in meters); gaps up to 1 m count too
    public static (double lo, double hi) Run(RoadScan s, Point p, Vector n, double maxHalf = MaxHalf)
    {
        double Walk(int sign)
        {
            double last = 0, t = 0;
            while (t < maxHalf)
            {
                t += Step;
                var q = p + n * (sign * t);
                if (s.Road(q.X, q.Y)) last = t;
                else if (t - last >= 1.0) break;
            }
            return last;
        }
        return (-Walk(-1), Walk(1));
    }

    /// Follows the road from from in direction dir over length meters (a point every 2 m, always moved to the center).
    /// If the road suddenly becomes much wider (crossing road), it continues straight – or, with stopAtJunction, stops before it.
    public static FollowResult Extend(RoadScan s, Point from, Vector dir, double length, double width, bool stopAtJunction)
    {
        var pts = new List<Point>();
        var widths = new List<double>();
        if (dir.Length < 1e-9 || length <= 0) return new FollowResult(pts, width, 0, null);
        dir.Normalize();
        var p = from;
        double travelled = 0;
        string stop = null;
        while (travelled + 2 <= length + 1e-6)
        {
            var q = p + dir * 2;
            if (!s.W.Contains(q.X, q.Y)) { stop = "end"; break; }
            if (s.InTile(q.X, q.Y)) { stop = "tile"; break; }
            var n = new Vector(-dir.Y, dir.X);
            // search the surface across the direction (up to a bit more than half the width sideways)
            double t0 = double.NaN;
            for (double t = 0; t <= width / 2 + 5; t += Step)
            {
                if (s.Road(q.X + n.X * t, q.Y + n.Y * t)) { t0 = t; break; }
                if (s.Road(q.X - n.X * t, q.Y - n.Y * t)) { t0 = -t; break; }
            }
            if (double.IsNaN(t0)) { stop = s.InTile(q.X + n.X * 3, q.Y + n.Y * 3) || s.InTile(q.X - n.X * 3, q.Y - n.Y * 3) ? "tile" : "end"; break; }
            var at = q + n * t0;
            var (lo, hi) = Run(s, at, n);
            double run = hi - lo + 1;
            Point c;
            if (run > width * 1.5 + 4)
            {
                if (stopAtJunction) { stop = "junction"; break; }
                c = q;   // crossing road: continue straight
            }
            else
            {
                c = at + n * (lo + hi) / 2;
                widths.Add(run);
            }
            var nd = c - p;
            if (nd.Length > 1e-6) { nd.Normalize(); var blend = dir * 0.6 + nd * 0.4; blend.Normalize(); dir = blend; }
            p = c;
            pts.Add(p);
            travelled += 2;
        }
        double w = width;
        if (widths.Count > 0) { widths.Sort(); w = widths[widths.Count / 2]; }
        return new FollowResult(pts, w, travelled, stop);
    }
}
