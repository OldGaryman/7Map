using System.Windows;

namespace MapEditor.Map;

public sealed record EraseStats(int Pixels, int HeightPoints, double Roughness);

/// Eraser (the user's request): delete the surface under the brush stroke and make the ground natural only where there was surface (plus a narrow edge
/// beside it) – Laplace fill from the terrain all around and slight unevenness like when removing.
/// Terrain without surface stays unchanged; in city tiles only our surface from splat3.png is removed, the heights stay. The change first lands only in memory (WorldEdits).
public static class Eraser
{
    /// largest grid area of a stroke (16 million cells = 4,000 × 4,000 m, about 400 MB)
    const long MaxCells = 16_000_000;

    /// square = square brush (square with edge length 2 · radius, aligned to the axes), otherwise round
    /// bumps = level of unevenness (0 = none, 1 = light, 2 = medium, 3 = strong)
    public static EraseStats Erase(WorldData w, WorldEdits e, IReadOnlyList<Point> stroke, double radius, double rim, bool natural, bool square = false, double bumps = 1)
    {
        if (stroke.Count == 0) return new EraseStats(0, 0, 0);
        int margin = (int)Math.Ceiling(radius) + 13;   // 8 m measuring strip for the unevenness + 5 m
        int gx0 = (int)Math.Floor(stroke.Min(p => p.X)) - margin, gz0 = (int)Math.Floor(stroke.Min(p => p.Y)) - margin;
        int gw = (int)Math.Ceiling(stroke.Max(p => p.X)) + margin - gx0, gh = (int)Math.Ceiling(stroke.Max(p => p.Y)) + margin - gz0;
        // The grid covers the whole stroke including the edge; it needs about 25 bytes per cell. A very long, diagonal stroke
        // would take gigabytes and crash the program (audit finding F15): better erase in smaller strokes.
        if ((long)gw * gh > MaxCells) throw new InvalidOperationException(Loc.T("The stroke covers too large an area – please erase in smaller strokes."));
        int n = gw * gh;
        var tiles = w.Prefabs.Where(p => p.IsTile && p.X < gx0 + gw && p.X + p.SizeX > gx0 && p.Z < gz0 + gh && p.Z + p.SizeZ > gz0).ToList();
        bool InTile(int x, int z) => tiles.Any(t => t.Contains(x + 0.5, z + 0.5));

        // cells under the brush: distance to the stroke (line through the mouse positions) at most radius
        var brush = new bool[n];
        int r = (int)Math.Ceiling(radius) + 1;
        if (square)
        {
            // square at every point of the stroke (every 0.5 m)
            for (int k = 0; k < stroke.Count; k++)
            {
                var a = stroke[k];
                var b = k + 1 < stroke.Count ? stroke[k + 1] : a;
                int samples = Math.Max(1, (int)Math.Ceiling((b - a).Length / 0.5));
                for (int q = 0; q <= samples; q++)
                {
                    double cx = a.X + (b.X - a.X) * q / samples, cz = a.Y + (b.Y - a.Y) * q / samples;
                    int x0 = Math.Max(0, (int)Math.Ceiling(cx - radius - 0.5) - gx0), x1 = Math.Min(gw - 1, (int)Math.Floor(cx + radius - 0.5) - gx0);
                    int z0 = Math.Max(0, (int)Math.Ceiling(cz - radius - 0.5) - gz0), z1 = Math.Min(gh - 1, (int)Math.Floor(cz + radius - 0.5) - gz0);
                    for (int gz = z0; gz <= z1; gz++)
                        for (int gx = x0; gx <= x1; gx++) brush[gz * gw + gx] = true;
                }
            }
        }
        else
        for (int k = 0; k < stroke.Count; k++)
        {
            var a = stroke[k];
            var b = k + 1 < stroke.Count ? stroke[k + 1] : a;
            int x0 = (int)Math.Floor(Math.Min(a.X, b.X)) - r - gx0, x1 = (int)Math.Ceiling(Math.Max(a.X, b.X)) + r - gx0;
            int z0 = (int)Math.Floor(Math.Min(a.Y, b.Y)) - r - gz0, z1 = (int)Math.Ceiling(Math.Max(a.Y, b.Y)) + r - gz0;
            double dx = b.X - a.X, dz = b.Y - a.Y, len2 = dx * dx + dz * dz;
            for (int gz = Math.Max(z0, 0); gz <= Math.Min(z1, gh - 1); gz++)
                for (int gx = Math.Max(x0, 0); gx <= Math.Min(x1, gw - 1); gx++)
                {
                    double px = gx0 + gx + 0.5, pz = gz0 + gz + 0.5;
                    double t = len2 > 0 ? Math.Clamp(((px - a.X) * dx + (pz - a.Y) * dz) / len2, 0, 1) : 0;
                    double ex = px - a.X - t * dx, ez = pz - a.Y - t * dz;
                    if (ex * ex + ez * ez <= radius * radius) brush[gz * gw + gx] = true;
                }
        }

        // surface under the brush (splat3.png or road mask). In tiles only our own surface from splat3.png: the roads
        // of the tile are created by the game itself (splat3_processed.png), and the heights there are dictated by the building (usable = false)
        var road = new bool[n];
        var usable = new bool[n];
        int roadCount = 0;
        for (int g = 0; g < n; g++)
        {
            if (!brush[g]) continue;
            int x = gx0 + g % gw, z = gz0 + g / gw;
            if (!w.Contains(x, z)) continue;
            bool own = (e.SplatAt(x, z) & 0x00FFFF00) != 0;
            if (InTile(x, z))
            {
                if (own) { road[g] = true; roadCount++; }
                continue;
            }
            usable[g] = true;
            if (own || w.RoadMask[w.Index(x, z)] != 0) { road[g] = true; roadCount++; }
        }
        if (roadCount == 0) return new EraseStats(0, 0, 0);

        int changed = 0;
        double amp = 0;
        if (natural)
        {
            // free = surface and the edge beside it (at most rim meters), only under the brush; fill and unevenness like when
            // deleting buildings (GroundShaper)
            var dRoad = GroundShaper.Distance(road, gw, gh);
            var free = new bool[n];
            for (int g = 0; g < n; g++)
            {
                int gx = g % gw, gz = g / gw;
                if (usable[g] && dRoad[g] <= rim && gx > 0 && gz > 0 && gx < gw - 1 && gz < gh - 1) free[g] = true;
            }
            (changed, amp) = GroundShaper.FillNatural(w, e, gx0, gz0, gw, gh, free, bumps, InTile);
        }

        for (int g = 0; g < n; g++)
        {
            if (!road[g]) continue;
            int x = gx0 + g % gw, z = gz0 + g / gw;
            e.SetSplat(x, z, WorldEdits.EmptyPixel);
            e.SetRoad(w.Index(x, z), 0);
        }
        return new EraseStats(roadCount, changed, amp);
    }
}
