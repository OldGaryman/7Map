using System.Windows;

namespace MapEditor.Map;

/// Kind of the range in the drawn area: mountain range (ridges and peaks), hill range (round, low), highland (almost flat top)
public enum RangeKind { Mountains, Hills, Highland }

/// foothills at the edge of the area: gentle (long and flat, may extend beyond the line), steep (rises fast),
/// chopped (rock steps with walls), mixed (changes along the edge, chopped only rarely)
public enum RangeEdge { Soft, Steep, Cliff, Mixed }

/// Mountain range in a freely drawn area (the user's request, 2026-11-04). The area is calculated as a distance field to the edge:
/// the further inside, the higher (envelope with the shape of the foothills), on top a noise that forms ridges and peaks.
/// The highest point of the range reaches exactly the set height. At the edge of the drawing the change is 0, except for gentle
/// foothills (they may run out beyond the line if there is too little room inside) and for the rock steps (wall right behind the edge).
public sealed class RangeSpec
{
    public RangeKind Kind;
    public RangeEdge Edge = RangeEdge.Mixed;
    /// height of the highest peak above the terrain (m); negative = hollow
    public double Height = 90;
    /// width of the slope from the edge inwards (m); for a narrow area at most half the width (gentle slopes reach outwards)
    public double Foot = 150;
    /// distance of the ridges and peaks (m)
    public double Spacing = 250;
    /// ruggedness in percent: fine noise
    public double Rough = 30;
    /// craggedness in percent: sharp peaks and rock ridges on the high parts (with smoother places in between)
    public double Crag = 60;
    public int Seed;
    /// smoothed outline of the area (world coordinates)
    public List<Point> Outline = new();

    /// smallest area (m²)
    public const double MinArea = 2500;

    /// how far (m) the range may run out beyond the drawn line: only gentle foothills, at most one slope width
    public double Spill => Edge is RangeEdge.Steep or RangeEdge.Cliff ? 0 : Math.Min(Foot, 400);

    /// prepare a hand-drawn outline: smooth the trembling of the hand (round corners), remove double points
    public static List<Point> PrepareOutline(IReadOnlyList<Point> raw, bool round = true, int iterations = 3)
    {
        var pts = new List<Point>();
        foreach (var p in raw)
            if (pts.Count == 0 || (p - pts[^1]).Length >= 1) pts.Add(p);
        if (pts.Count > 1 && (pts[0] - pts[^1]).Length < 1) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3 || !round) return pts;   // round = false: outline stays angular (lakes)
        // cut corners (Chaikin), three times: segments become round curves
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

    /// area of the outline (m²)
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

    /// step at distance c from the edge: rises from 0 to 1 over about 6 m
    static double Step(double dd, double c) => Sm((dd - c) / 6 + 0.5);

    /// ridge line of a noise: 1 where it is 0, falling off to both sides (sharp kink)
    static double Ridged(double n) => Math.Clamp(1 - Math.Abs(n) * 1.7, 0, 1);

    /// height change (m) for all cells of the grid. sd = signed distance of every cell to the edge (m): positive inside,
    /// negative outside (calculated only up to Spill meters); (gx0, gz0) = world coordinate of cell 0.
    public float[] Shape(float[] sd, int gw, int gh, int gx0, int gz0)
    {
        int n = gw * gh;
        var res = new float[n];
        float dm = 0;
        for (int g = 0; g < n; g++) if (sd[g] > dm) dm = sd[g];
        if (dm < 2) return res;
        double spill = Spill;
        double denom = Math.Max(8, Math.Min(Foot, dm));   // slope width for steep and chopped
        // gentle slopes: also reach outwards if there is less room inside than the slope width (at least 30 % of the slope width)
        double denomSoft = Math.Max(8, Foot);
        double extSoft = spill > 0 ? Math.Min(spill, Math.Max(Foot - dm, 0.3 * Foot)) : 0;
        double styleScale = Math.Max(180, denom * 1.5);   // size of the areas in which the foothills are the same (with "mixed")
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

                // irregular edge: the distance is shifted with noise (bays and tongues)
                double warp = (0.18 * N(x, z, 35, s + 10) + 0.06 * N(x, z, 12, s + 11)) * denom;
                double dd = sv + warp;

                // shares of the three foothill shapes at this place (mixed: much gentle and steep, chopped only rarely)
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
                    if (ddS > 0) p += wSoft * Math.Pow(Sm(ddS / denomSoft), 1.15);   // long and flat, round on top
                }
                if ((wSteep > 0 || wCliff > 0) && dd > 0)
                {
                    double t = Math.Min(1, dd / denom);
                    if (wSteep > 0) p += wSteep * (1 - Math.Pow(1 - t, 2.4));       // rises fast, round on top
                    if (wCliff > 0)
                    {
                        // three rock steps with walls (position shifted depending on the place), slightly rising in between
                        double c1 = 3 + 5 * N(x, z, 45, s + 30);
                        double c2 = 0.38 * denom + 0.14 * denom * N(x, z, 60, s + 31);
                        double c3 = 0.72 * denom + 0.10 * denom * N(x, z, 70, s + 32);
                        p += wCliff * (0.28 * Step(dd, c1) + 0.27 * Step(dd, c2) + 0.25 * Step(dd, c3) + 0.2 * t);
                    }
                }
                if (p <= 0) continue;

                // surface shape: ridges and peaks (mountains), round backs (hills) or almost flat (highland)
                double mod;
                switch (Kind)
                {
                    case RangeKind.Hills:
                    {
                        double a = N(x, z, Spacing, s), b = N(x, z, Spacing * 0.45, s + 1), c = N(x, z, Spacing * 0.2, s + 3);
                        mod = Math.Clamp(0.55 + 0.75 * a + 0.25 * b + 0.12 * c, 0.1, 1.15);   // distinct backs with hollows in between
                        break;
                    }
                    case RangeKind.Highland:
                        mod = 0.92 + 0.12 * N(x, z, Spacing, s);
                        break;
                    default:
                    {
                        double n1 = N(x, z, Spacing, s), n2 = N(x, z, Spacing * 0.4, s + 1), n3 = N(x, z, Spacing * 0.75, s + 2);
                        double r1 = Math.Pow(Ridged(n1), 1.8);   // ridge lines where the noise is 0, narrow and sharp
                        double ridge = r1 * (0.6 + 0.4 * Ridged(n2));
                        mod = 0.22 + 0.78 * ridge * (0.8 + 0.25 * n3);
                        break;
                    }
                }
                double v = p * mod;
                if (cg > 0 && v > 0)
                {
                    // craggy: peaks sharper (the high parts grow relatively), on top rock ridges and gullies from ridge-line noise
                    // in three sizes; "rock" fades in smooth places in between so that not everything is equally rugged
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
        double scale = Height / max;   // the highest point reaches exactly the set height
        double ah = Math.Abs(Height);

        // fine ruggedness, only where the range already has height
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
                    // noise in four sizes (90, 40, 16, 7 m): bumps, backs, rocks, small stuff; strength grows with the height,
                    // but also has a minimum strength for low hills (otherwise they stay smooth)
                    double noise = 0.4 * N(x, z, 90, s + 40) + 0.3 * N(x, z, 40, s + 41) + 0.2 * N(x, z, 16, s + 42) + 0.1 * N(x, z, 7, s + 43);
                    d += Rough / 100 * (0.25 * ah + 2) * noise * Math.Min(1, Math.Abs(d) / ah * 6);
                }
                res[g] = (float)d;
            }
        });
        return res;
    }
}
