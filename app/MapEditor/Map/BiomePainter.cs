using System.Windows;

namespace MapEditor.Map;

/// Biome brush (the user's request, 2026-10-29): sets the chosen biome in biomes.png under the brush stroke.
/// Only the biomes change, heights, roads and buildings stay. biomes.png has 8 m per pixel (2k world 256², 8k world 1024²);
/// a pixel is set if its center lies under the brush, and always the pixel under every stroke point
/// (otherwise a brush under 8 m sometimes paints nothing at all).
public static class BiomePainter
{
    /// square = square brush (square with edge length 2 · radius), otherwise round; returns the number of changed pixels
    public static int Paint(WorldData w, WorldEdits e, IReadOnlyList<Point> stroke, double radius, bool square, int biome)
    {
        if (stroke.Count == 0) return 0;
        int n = w.BiomeSize;
        double m = (double)w.Size / n;   // meters per pixel
        byte b = (byte)biome;
        int changed = 0;
        void Set(int bx, int by)
        {
            if (bx < 0 || by < 0 || bx >= n || by >= n) return;
            int i = by * n + bx;
            if (w.Biomes[i] == b) return;
            e.SetBiome(i, b);
            changed++;
        }
        int Col(double x) => (int)Math.Floor((x + w.Half) / m);
        int Row(double z) => (int)Math.Floor((w.Half - z) / m);

        // For each stroke segment check only the pixels in its own surroundings (before: every pixel in the rectangle around the whole stroke against
        // all segments – with a long, diagonal stroke billions of checks and a hanging program, audit finding F14).
        // The result is the same: a pixel is set if any segment covers it.
        for (int k = 0; k < stroke.Count; k++)
        {
            var a = stroke[k];
            var c = k + 1 < stroke.Count ? stroke[k + 1] : a;
            int bx0 = Math.Max(0, Col(Math.Min(a.X, c.X) - radius)), bx1 = Math.Min(n - 1, Col(Math.Max(a.X, c.X) + radius));
            int by0 = Math.Max(0, Row(Math.Max(a.Y, c.Y) + radius)), by1 = Math.Min(n - 1, Row(Math.Min(a.Y, c.Y) - radius));
            for (int by = by0; by <= by1; by++)
                for (int bx = bx0; bx <= bx1; bx++)
                {
                    double cx = (bx + 0.5) * m - w.Half, cz = w.Half - (by + 0.5) * m;
                    if (Covered(a, c, cx, cz, radius, square)) Set(bx, by);
                }
        }
        foreach (var p in stroke) Set(Col(p.X), Row(p.Y));
        return changed;
    }

    // is the point under the stroke segment a–c? round: distance to the segment; square: square that moves from a to c
    static bool Covered(Point a, Point c, double x, double z, double radius, bool square)
    {
        double dx = c.X - a.X, dz = c.Y - a.Y, len2 = dx * dx + dz * dz;
        if (square)
        {
            // the square center moves from a to c: the point is inside if there is a t with |x − a.x − t·dx| ≤ r and |z − a.z − t·dz| ≤ r
            double lo = 0, hi = 1;
            return Range(x - a.X, dx, radius, ref lo, ref hi) && Range(z - a.Y, dz, radius, ref lo, ref hi);
        }
        double t = len2 > 0 ? Math.Clamp(((x - a.X) * dx + (z - a.Y) * dz) / len2, 0, 1) : 0;
        double ex = x - a.X - t * dx, ez = z - a.Y - t * dz;
        return ex * ex + ez * ez <= radius * radius;
    }

    // restricts [lo, hi] to the t for which |v − t·d| ≤ r holds; false = none
    static bool Range(double v, double d, double r, ref double lo, ref double hi)
    {
        if (Math.Abs(d) < 1e-9) return Math.Abs(v) <= r;
        double t1 = (v - r) / d, t2 = (v + r) / d;
        lo = Math.Max(lo, Math.Min(t1, t2));
        hi = Math.Min(hi, Math.Max(t1, t2));
        return lo <= hi;
    }
}
