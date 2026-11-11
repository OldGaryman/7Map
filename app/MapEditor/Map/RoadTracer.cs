using System.Windows;

namespace MapEditor.Map;

/// Ergebnis der Verfolgung: Stützpunkte entlang der Straßenmitte, gemessene Breite (inkl. 1 m Zugabe je Seite), Belag
public sealed record TraceResult(List<Point> Points, double Width, double Length, Surface Kind);

/// Folgt einer vorhandenen Straße (Asphalt und Schotter, splat3 + processed) vom Start- zum Endpunkt.
/// Kürzester Weg über Straßenpixel, wobei Pixel nahe am Rand teuer sind – so läuft der Weg in der Mitte
/// und an Kreuzungen geradeaus weiter. Stadt-Kacheln werden ausgelassen.
public static class RoadTracer
{
    const int MaxGrid = 3000;

    public static TraceResult Trace(WorldData w, Point a, Point b, out string error)
    {
        error = null;
        var tiles = w.Prefabs.Where(p => p.IsTile).ToList();
        bool Usable(int x, int z) => w.RoadAt(x, z) != 0 && !tiles.Any(t => t.Contains(x + 0.5, z + 0.5));
        var sa = Snap(a, Usable);
        var sb = Snap(b, Usable);
        if (sa == null) { error = Loc.T("There is no road outside a city tile near the start point (up to 20 m)."); return null; }
        if (sb == null) { error = Loc.T("There is no road outside a city tile near the end point (up to 20 m)."); return null; }
        var (ax, az) = sa.Value;
        var (bx, bz) = sb.Value;

        // Suchbereich um beide Punkte (Straßen dürfen Bögen machen)
        double dist = Math.Sqrt((double)(ax - bx) * (ax - bx) + (double)(az - bz) * (az - bz));
        int pad = (int)Math.Max(150, dist * 0.75);
        int x0 = Math.Max(-w.Half, Math.Min(ax, bx) - pad), x1 = Math.Min(w.Half - 1, Math.Max(ax, bx) + pad);
        int z0 = Math.Max(-w.Half, Math.Min(az, bz) - pad), z1 = Math.Min(w.Half - 1, Math.Max(az, bz) + pad);
        if (x1 - x0 >= MaxGrid || z1 - z0 >= MaxGrid) { error = Loc.T("Start and end are too far apart (at most about 2 km)."); return null; }
        int gw = x1 - x0 + 1, gh = z1 - z0 + 1, n = gw * gh;
        var localTiles = tiles.Where(t => t.X <= x1 && t.X + t.SizeX >= x0 && t.Z <= z1 && t.Z + t.SizeZ >= z0).ToList();
        var road = new bool[n];
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int x = x0 + gx, z = z0 + gz;
                road[gz * gw + gx] = w.RoadMask[w.Index(x, z)] != 0 && !localTiles.Any(t => t.Contains(x + 0.5, z + 0.5));
            }

        // Abstand zum Straßenrand (Chamfer 3-4, /3 = Meter)
        var dt = new int[n];
        for (int i = 0; i < n; i++) dt[i] = road[i] ? int.MaxValue / 2 : 0;
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int i = gz * gw + gx;
                if (dt[i] == 0) continue;
                int v = dt[i];
                if (gx > 0) v = Math.Min(v, dt[i - 1] + 3); else v = Math.Min(v, 3);
                if (gz > 0) v = Math.Min(v, dt[i - gw] + 3); else v = Math.Min(v, 3);
                if (gx > 0 && gz > 0) v = Math.Min(v, dt[i - gw - 1] + 4);
                if (gx < gw - 1 && gz > 0) v = Math.Min(v, dt[i - gw + 1] + 4);
                dt[i] = v;
            }
        for (int gz = gh - 1; gz >= 0; gz--)
            for (int gx = gw - 1; gx >= 0; gx--)
            {
                int i = gz * gw + gx;
                if (dt[i] == 0) continue;
                int v = dt[i];
                if (gx < gw - 1) v = Math.Min(v, dt[i + 1] + 3); else v = Math.Min(v, 3);
                if (gz < gh - 1) v = Math.Min(v, dt[i + gw] + 3); else v = Math.Min(v, 3);
                if (gx < gw - 1 && gz < gh - 1) v = Math.Min(v, dt[i + gw + 1] + 4);
                if (gx > 0 && gz < gh - 1) v = Math.Min(v, dt[i + gw - 1] + 4);
                dt[i] = v;
            }

        // kürzester Weg (Dijkstra, 8 Nachbarn); Kosten je Schritt steigen zum Rand hin stark an
        int start = (az - z0) * gw + ax - x0, goal = (bz - z0) * gw + bx - x0;
        var cost = new float[n];
        Array.Fill(cost, float.MaxValue);
        var prev = new int[n];
        var queue = new PriorityQueue<int, float>();
        cost[start] = 0;
        queue.Enqueue(start, 0);
        int[] ddx = { 1, -1, 0, 0, 1, 1, -1, -1 }, ddz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        while (queue.TryDequeue(out int i, out float c))
        {
            if (c > cost[i]) continue;
            if (i == goal) break;
            int gx = i % gw, gz = i / gw;
            for (int k = 0; k < 8; k++)
            {
                int nx = gx + ddx[k], nz = gz + ddz[k];
                if (nx < 0 || nz < 0 || nx >= gw || nz >= gh) continue;
                int j = nz * gw + nx;
                if (!road[j]) continue;
                double m = dt[j] / 3.0;
                float nc = c + (float)((k < 4 ? 1 : 1.4142) * (1 + 8 / (m * m)));
                if (nc < cost[j]) { cost[j] = nc; prev[j] = i; queue.Enqueue(j, nc); }
            }
        }
        if (cost[goal] == float.MaxValue) { error = Loc.T("Start and end are not connected by a continuous road."); return null; }

        var cells = new List<int>();
        for (int i = goal; ; i = prev[i]) { cells.Add(i); if (i == start) break; }
        cells.Reverse();

        // Breite: Median des Randabstands in der Mitte; Belag: was überwiegt
        var half = cells.Select(i => dt[i] / 3.0).OrderBy(v => v).ToList();
        double width = Math.Clamp(Math.Round(2 * half[half.Count / 2] + 2), 4, 40);
        int asphalt = cells.Count(i => w.RoadMask[w.Index(x0 + i % gw, z0 + i / gw)] == WorldData.Asphalt);
        var kind = asphalt * 2 >= cells.Count ? Surface.Asphalt : Surface.Gravel;

        // Treppenstufen der Pixel glätten (±4 m), dann ausdünnen (Douglas-Peucker, 1 m)
        var raw = cells.Select(i => new Point(x0 + i % gw + 0.5, z0 + i / gw + 0.5)).ToList();
        var smooth = new List<Point>(raw.Count);
        for (int k = 0; k < raw.Count; k++)
        {
            int lo = Math.Max(0, k - 4), hi = Math.Min(raw.Count - 1, k + 4);
            double sx = 0, sz = 0;
            for (int q = lo; q <= hi; q++) { sx += raw[q].X; sz += raw[q].Y; }
            smooth.Add(new Point(sx / (hi - lo + 1), sz / (hi - lo + 1)));
        }
        smooth[0] = raw[0];
        smooth[^1] = raw[^1];
        var keep = new bool[smooth.Count];
        keep[0] = keep[^1] = true;
        Simplify(smooth, 0, smooth.Count - 1, 1.0, keep);
        var pts = smooth.Where((_, k) => keep[k]).Select(p => new Point(Math.Round(p.X, 1), Math.Round(p.Y, 1))).ToList();
        double len = 0;
        for (int k = 1; k < smooth.Count; k++) len += (smooth[k] - smooth[k - 1]).Length;
        return new TraceResult(pts, width, len, kind);
    }

    // nächstes Straßenpixel im Umkreis von 20 m
    static (int, int)? Snap(Point p, Func<int, int, bool> usable)
    {
        int cx = (int)Math.Floor(p.X), cz = (int)Math.Floor(p.Y);
        (int, int)? best = null;
        double bd = double.MaxValue;
        for (int dz = -20; dz <= 20; dz++)
            for (int dx = -20; dx <= 20; dx++)
            {
                double d = dx * dx + dz * dz;
                if (d > 400 || d >= bd || !usable(cx + dx, cz + dz)) continue;
                bd = d; best = (cx + dx, cz + dz);
            }
        return best;
    }

    static void Simplify(List<Point> p, int a, int b, double tol, bool[] keep)
    {
        if (b <= a + 1) return;
        var d = p[b] - p[a];
        double len = d.Length, best = -1;
        int bi = -1;
        for (int k = a + 1; k < b; k++)
        {
            var e = p[k] - p[a];
            double dist = len < 1e-9 ? e.Length : Math.Abs(d.X * e.Y - d.Y * e.X) / len;
            if (dist > best) { best = dist; bi = k; }
        }
        if (best <= tol) return;
        keep[bi] = true;
        Simplify(p, a, bi, tol, keep);
        Simplify(p, bi, b, tol, keep);
    }
}
