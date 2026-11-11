using System.Windows;

namespace MapEditor.Map;

/// Biom-Pinsel (Wunsch des Nutzers 29.10.2026): setzt unter dem Pinselstrich das gewählte Biom in biomes.png.
/// Nur die Biome ändern sich, Höhen, Straßen und Gebäude bleiben. biomes.png hat 8 m je Pixel (2k-Welt 256², 8k-Welt 1024²);
/// ein Pixel wird gesetzt, wenn seine Mitte unter dem Pinsel liegt, dazu immer das Pixel unter jedem Strichpunkt
/// (sonst malt ein Pinsel unter 8 m manchmal gar nichts).
public static class BiomePainter
{
    /// square = eckiger Pinsel (Quadrat mit Kantenlänge 2 · radius), sonst rund; liefert die Zahl geänderter Pixel
    public static int Paint(WorldData w, WorldEdits e, IReadOnlyList<Point> stroke, double radius, bool square, int biome)
    {
        if (stroke.Count == 0) return 0;
        int n = w.BiomeSize;
        double m = (double)w.Size / n;   // Meter je Pixel
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

        // Je Strichsegment nur die Pixel in dessen eigenem Umfeld prüfen (früher: jedes Pixel im Rechteck um den ganzen Strich gegen
        // alle Segmente – bei einem langen, schrägen Strich Milliarden Prüfungen und ein hängendes Programm, Prüfbericht F14).
        // Das Ergebnis ist dasselbe: ein Pixel wird gesetzt, wenn irgendein Segment es überdeckt.
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

    // liegt der Punkt unter dem Strichsegment a–c? rund: Abstand zur Strecke; eckig: Quadrat, das von a nach c wandert
    static bool Covered(Point a, Point c, double x, double z, double radius, bool square)
    {
        double dx = c.X - a.X, dz = c.Y - a.Y, len2 = dx * dx + dz * dz;
        if (square)
        {
            // Quadratmitte wandert von a nach c: Punkt liegt darin, wenn es ein t gibt mit |x − a.x − t·dx| ≤ r und |z − a.z − t·dz| ≤ r
            double lo = 0, hi = 1;
            return Range(x - a.X, dx, radius, ref lo, ref hi) && Range(z - a.Y, dz, radius, ref lo, ref hi);
        }
        double t = len2 > 0 ? Math.Clamp(((x - a.X) * dx + (z - a.Y) * dz) / len2, 0, 1) : 0;
        double ex = x - a.X - t * dx, ez = z - a.Y - t * dz;
        return ex * ex + ez * ez <= radius * radius;
    }

    // schränkt [lo, hi] auf die t ein, für die |v − t·d| ≤ r gilt; false = keins
    static bool Range(double v, double d, double r, ref double lo, ref double hi)
    {
        if (Math.Abs(d) < 1e-9) return Math.Abs(v) <= r;
        double t1 = (v - r) / d, t2 = (v + r) / d;
        lo = Math.Max(lo, Math.Min(t1, t2));
        hi = Math.Min(hi, Math.Max(t1, t2));
        return lo <= hi;
    }
}
