using System.Windows;

namespace MapEditor.Map;

/// Straßenpixel abfragen, Stadt-Kacheln ausgenommen (ihre Straßen gehören zur Kachel und werden nicht bearbeitet)
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

/// Mitte einer vorhandenen Straße an einer Stelle: Mittelpunkt quer zur Straße, Richtung, Breite (alles zusammen mit Rändern)
public sealed record RoadSpot(Point Center, Vector Dir, double Width, byte Kind);

/// Ergebnis von Extend: Punkte entlang der Straßenmitte (alle 2 m), gemessene Breite, Länge und Grund des Endes
/// (null = volle Länge, "end" = Straße endet, "tile" = Stadt-Kachel, "junction" = querende Straße)
public sealed record FollowResult(List<Point> Points, double Width, double Length, string Stop);

/// Findet die Mitte einer vorhandenen Straße und folgt ihr ein Stück (Wunsch des Nutzers 2.11.2026: Straße bearbeiten).
/// Quer zur Straße wird abgetastet, die Mitte ist die Mitte des zusammenhängenden Belags.
public static class RoadFollower
{
    const double Step = 0.5;       // Schritt beim Abtasten quer zur Straße (m)
    const double MaxHalf = 40;     // so breit darf eine Straße höchstens sein (m)

    /// Mitte der Straße nahe dem Klickpunkt (bis 12 m); null, wenn dort keine Straße (außerhalb von Kacheln) liegt
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
            // höchstens 16 m je Seite: an großen Kreuzungen und Plätzen ist keine Mitte erkennbar, dann bleibt der Punkt in der Nähe des Klicks
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

    /// Richtung der Straße (Hauptachse der Straßenpixel im Umkreis); null bei zu wenig Pixeln
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

    /// Ausdehnung des zusammenhängenden Belags quer zur Richtung n, von p aus (lo ≤ 0 ≤ hi, in Metern); Lücken bis 1 m zählen mit
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

    /// Folgt der Straße von from aus in Richtung dir über length Meter (alle 2 m ein Punkt, immer auf die Mitte gerückt).
    /// Wird die Straße plötzlich viel breiter (querende Straße), geht es geradeaus weiter – oder, mit stopAtJunction, hört es davor auf.
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
            // Belag quer zur Richtung suchen (bis etwas mehr als die halbe Breite seitlich)
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
                c = q;   // querende Straße: geradeaus weiter
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
