using System.Windows;

namespace MapEditor.Map;

/// Art der Kette im gezeichneten Bereich: Gebirgskette (Kämme und Gipfel), Hügelkette (rund, niedrig), Hochland (fast flache Oberseite)
public enum RangeKind { Mountains, Hills, Highland }

/// Ausläufer am Rand des Bereichs: sanft (lang und flach, darf über die Linie hinausreichen), steil (schnell hoch),
/// abgehackt (Felsstufen mit Wänden), gemischt (wechselt entlang des Rands, abgehackt nur selten)
public enum RangeEdge { Soft, Steep, Cliff, Mixed }

/// Gebirgskette im frei gezeichneten Bereich (Wunsch des Nutzers 4.11.2026). Der Bereich wird als Abstandsfeld zum Rand gerechnet:
/// Je weiter innen, desto höher (Hüllkurve mit der Form des Ausläufers), darauf ein Rauschen, das Kämme und Gipfel bildet.
/// Der höchste Punkt der Kette erreicht genau die eingestellte Höhe. Am Rand der Zeichnung ist die Änderung 0, außer bei sanften
/// Ausläufern (sie dürfen außerhalb der Linie auslaufen, wenn innen zu wenig Platz ist) und bei den Felsstufen (Wand gleich hinter dem Rand).
public sealed class RangeSpec
{
    public RangeKind Kind;
    public RangeEdge Edge = RangeEdge.Mixed;
    /// Höhe des höchsten Gipfels über dem Gelände (m); negativ = Senke
    public double Height = 90;
    /// Breite des Hangs vom Rand nach innen (m); bei schmalem Bereich höchstens die halbe Breite (sanfte Hänge reichen nach außen)
    public double Foot = 150;
    /// Abstand der Kämme und Gipfel (m)
    public double Spacing = 250;
    /// Zerklüftung in Prozent: feines Rauschen
    public double Rough = 30;
    /// Schroffheit in Prozent: spitze Gipfel und Felsgrate auf den hohen Teilen (mit glatteren Stellen dazwischen)
    public double Crag = 60;
    public int Seed;
    /// geglätteter Umriss des Bereichs (Welt-Koordinaten)
    public List<Point> Outline = new();

    /// kleinste Fläche (m²)
    public const double MinArea = 2500;

    /// Wie weit (m) die Kette außerhalb der gezeichneten Linie auslaufen darf: nur sanfte Ausläufer, höchstens eine Hangbreite
    public double Spill => Edge is RangeEdge.Steep or RangeEdge.Cliff ? 0 : Math.Min(Foot, 400);

    /// Handgezeichneten Umriss aufbereiten: Zittern der Hand glätten (Ecken abrunden), Doppelpunkte entfernen
    public static List<Point> PrepareOutline(IReadOnlyList<Point> raw, bool round = true, int iterations = 3)
    {
        var pts = new List<Point>();
        foreach (var p in raw)
            if (pts.Count == 0 || (p - pts[^1]).Length >= 1) pts.Add(p);
        if (pts.Count > 1 && (pts[0] - pts[^1]).Length < 1) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3 || !round) return pts;   // round = false: Umriss bleibt kantig (Seen)
        // Ecken abschneiden (Chaikin), dreimal: aus Strecken werden runde Kurven
        for (int it = 0; it < iterations; it++)
        {
            var q = new List<Point>(pts.Count * 2);
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                q.Add(new Point(a.X * 0.75 + b.X * 0.25, a.Y * 0.75 + b.Y * 0.25));
                q.Add(new Point(a.X * 0.25 + b.X * 0.75, a.Y * 0.25 + b.Y * 0.75));
            }
            pts = q;
        }
        return pts;
    }

    /// Fläche des Umrisses (m²)
    public static double Area(IReadOnlyList<Point> poly)
    {
        double s = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(s) / 2;
    }

    static double N(int x, int z, double size, int seed) => RoadApplier.Octave(x, z, size, seed);

    static double Sm(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// Stufe bei Abstand c vom Rand: steigt über etwa 6 m von 0 auf 1
    static double Step(double dd, double c) => Sm((dd - c) / 6 + 0.5);

    /// Kammlinie eines Rauschens: 1 dort, wo es 0 ist, nach beiden Seiten abfallend (scharfer Knick)
    static double Ridged(double n) => Math.Clamp(1 - Math.Abs(n) * 1.7, 0, 1);

    /// Höhenänderung (m) für alle Zellen des Rasters. sd = vorzeichenbehafteter Abstand jeder Zelle zum Rand (m): innen positiv,
    /// außen negativ (nur bis Spill Meter gerechnet); (gx0, gz0) = Weltkoordinate der Zelle 0.
    public float[] Shape(float[] sd, int gw, int gh, int gx0, int gz0)
    {
        int n = gw * gh;
        var res = new float[n];
        float dm = 0;
        for (int g = 0; g < n; g++) if (sd[g] > dm) dm = sd[g];
        if (dm < 2) return res;
        double spill = Spill;
        double denom = Math.Max(8, Math.Min(Foot, dm));   // Hangbreite für steil und abgehackt
        // sanfte Hänge: reichen auch nach außen, wenn innen weniger Platz ist als die Hangbreite (mindestens 30 % der Hangbreite)
        double denomSoft = Math.Max(8, Foot);
        double extSoft = spill > 0 ? Math.Min(spill, Math.Max(Foot - dm, 0.3 * Foot)) : 0;
        double styleScale = Math.Max(180, denom * 1.5);   // Größe der Bereiche, in denen die Ausläufer gleich sind (bei „gemischt“)
        double cg = Math.Clamp(Crag, 0, 300) / 100;
        int s = Seed;
        var rowMax = new double[gh];

        Parallel.For(0, gh, gz =>
        {
            double mx = 0;
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx;
                double sv = sd[g];
                if (sv <= -spill) continue;
                int x = gx0 + gx, z = gz0 + gz;

                // Rand unregelmäßig: der Abstand wird mit Rauschen verschoben (Buchten und Zungen)
                double warp = (0.18 * N(x, z, 35, s + 10) + 0.06 * N(x, z, 12, s + 11)) * denom;
                double dd = sv + warp;

                // Anteile der drei Ausläuferformen an dieser Stelle (gemischt: viel sanft und steil, abgehackt nur selten)
                double wSoft = 0, wSteep = 0, wCliff = 0;
                switch (Edge)
                {
                    case RangeEdge.Soft: wSoft = 1; break;
                    case RangeEdge.Steep: wSteep = 1; break;
                    case RangeEdge.Cliff: wCliff = 1; break;
                    default:
                        double m = 0.5 + 0.5 * Math.Clamp(N(x, z, styleScale, s + 20) * 1.4, -1, 1);
                        wSoft = 1 - Sm((m - 0.30) / 0.25);
                        wCliff = Sm((m - 0.74) / 0.14);
                        wSteep = Math.Max(0, 1 - wSoft - wCliff);
                        break;
                }
                double p = 0;
                if (wSoft > 0)
                {
                    double ddS = dd + extSoft;
                    if (ddS > 0) p += wSoft * Math.Pow(Sm(ddS / denomSoft), 1.15);   // lang und flach, oben rund
                }
                if ((wSteep > 0 || wCliff > 0) && dd > 0)
                {
                    double t = Math.Min(1, dd / denom);
                    if (wSteep > 0) p += wSteep * (1 - Math.Pow(1 - t, 2.4));       // schnell hoch, oben rund
                    if (wCliff > 0)
                    {
                        // drei Felsstufen mit Wänden (Lage je nach Ort verschoben), dazwischen leicht ansteigend
                        double c1 = 3 + 5 * N(x, z, 45, s + 30);
                        double c2 = 0.38 * denom + 0.14 * denom * N(x, z, 60, s + 31);
                        double c3 = 0.72 * denom + 0.10 * denom * N(x, z, 70, s + 32);
                        p += wCliff * (0.28 * Step(dd, c1) + 0.27 * Step(dd, c2) + 0.25 * Step(dd, c3) + 0.2 * t);
                    }
                }
                if (p <= 0) continue;

                // Oberflächenform: Kämme und Gipfel (Gebirge), runde Rücken (Hügel) oder fast eben (Hochland)
                double mod;
                switch (Kind)
                {
                    case RangeKind.Hills:
                    {
                        double a = N(x, z, Spacing, s), b = N(x, z, Spacing * 0.45, s + 1), c = N(x, z, Spacing * 0.2, s + 3);
                        mod = Math.Clamp(0.55 + 0.75 * a + 0.25 * b + 0.12 * c, 0.1, 1.15);   // deutliche Rücken mit Mulden dazwischen
                        break;
                    }
                    case RangeKind.Highland:
                        mod = 0.92 + 0.12 * N(x, z, Spacing, s);
                        break;
                    default:
                    {
                        double n1 = N(x, z, Spacing, s), n2 = N(x, z, Spacing * 0.4, s + 1), n3 = N(x, z, Spacing * 0.75, s + 2);
                        double r1 = Math.Pow(Ridged(n1), 1.8);   // Kammlinien dort, wo das Rauschen 0 ist, schmal und spitz
                        double ridge = r1 * (0.6 + 0.4 * Ridged(n2));
                        mod = 0.22 + 0.78 * ridge * (0.8 + 0.25 * n3);
                        break;
                    }
                }
                double v = p * mod;
                if (cg > 0 && v > 0)
                {
                    // schroff: Gipfel spitzer (die hohen Teile wachsen relativ), darauf Felsgrate und Rinnen aus Kammlinien-Rauschen
                    // in drei Größen; "Fels" blendet glatte Stellen dazwischen ein, damit nicht alles gleich zerklüftet ist
                    v = Math.Pow(v, 1 + 0.3 * cg);
                    double rock = 0.35 + 0.65 * Sm(0.5 + 1.2 * N(x, z, Spacing * 0.8, s + 50));
                    double crag = 0.5 * Ridged(N(x, z, 45, s + 51)) + 0.3 * Ridged(N(x, z, 18, s + 52)) + 0.2 * Ridged(N(x, z, 8, s + 53));
                    v += cg * 0.4 * (crag - 0.45) * Math.Pow(v, 1.3) * rock;
                }
                res[g] = (float)v;
                if (v > mx) mx = v;
            }
            rowMax[gz] = mx;
        });

        double max = rowMax.Max();
        if (max <= 0) return res;
        double scale = Height / max;   // der höchste Punkt erreicht genau die eingestellte Höhe
        double ah = Math.Abs(Height);

        // feine Zerklüftung, nur wo die Kette schon Höhe hat
        Parallel.For(0, gh, gz =>
        {
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx;
                if (res[g] == 0) continue;
                double d = res[g] * scale;
                if (Rough > 0)
                {
                    int x = gx0 + gx, z = gz0 + gz;
                    // Rauschen in vier Größen (90, 40, 16, 7 m): Buckel, Rücken, Felsen, Kleinzeug; Stärke wächst mit der Höhe,
                    // hat aber auch bei niedrigen Hügeln eine Mindeststärke (sonst bleiben sie glatt)
                    double noise = 0.4 * N(x, z, 90, s + 40) + 0.3 * N(x, z, 40, s + 41) + 0.2 * N(x, z, 16, s + 42) + 0.1 * N(x, z, 7, s + 43);
                    d += Rough / 100 * (0.25 * ah + 2) * noise * Math.Min(1, Math.Abs(d) / ah * 6);
                }
                res[g] = (float)d;
            }
        });
        return res;
    }
}
