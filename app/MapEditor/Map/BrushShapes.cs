using System.Windows;

namespace MapEditor.Map;

/// Brush shapes (the user's request, 2026-11-03). Random = a new, irregular shape on every left click (with a random number
/// per stroke, so that repeating gives the same shape).
public enum BrushShape { Round, Square, Diamond, Hexagon, Ellipse, Cross, Star, Blob, Random }

/// Describes a shape in unit size (center 0,0, edge at norm 1, nowhere farther than 1 from the center).
/// All shapes are star-shaped around the center: norm grows along every ray from the center outwards.
public sealed class ShapeSpec
{
    readonly BrushShape shape;
    readonly double[] rho;   // edge radius per angle (360 values) for star, blob and random
    readonly double cos = 1, sin = 0, aspect = 1;

    ShapeSpec(BrushShape s, int seed)
    {
        shape = s;
        if (s is BrushShape.Star or BrushShape.Blob or BrushShape.Random)
        {
            rho = new double[360];
            var rnd = new Random(seed);
            double[] amp = new double[8], phase = new double[8];
            if (s == BrushShape.Random)
            {
                for (int k = 2; k < 8; k++) { amp[k] = rnd.NextDouble() * 0.30 / Math.Sqrt(k - 1); phase[k] = rnd.NextDouble() * Math.PI * 2; }
                aspect = 0.55 + 0.45 * rnd.NextDouble();
                double th = rnd.NextDouble() * Math.PI * 2;
                cos = Math.Cos(th); sin = Math.Sin(th);
            }
            for (int i = 0; i < 360; i++)
            {
                double f = i * Math.PI / 180, v;
                if (s == BrushShape.Star) v = 0.40 + 0.60 * Math.Pow(Math.Abs(Math.Cos(2.5 * (f - Math.PI / 2))), 0.7);
                else if (s == BrushShape.Blob) v = 0.72 + 0.28 * (0.5 * Math.Sin(2 * f + 1.3) + 0.3 * Math.Sin(3 * f + 0.4) + 0.2 * Math.Sin(5 * f + 2.1));
                else { v = 0.70; for (int k = 2; k < 8; k++) v += amp[k] * Math.Sin(k * f + phase[k]); v = Math.Max(0.25, v); }
                rho[i] = v;
            }
            double max = rho.Max();   // largest radius = 1
            for (int i = 0; i < 360; i++) rho[i] /= max;
        }
    }

    public static ShapeSpec Create(BrushShape s, int seed = 0) => new(s, seed);

    /// norm of a point (x, z in units of the radius): 0 in the center, 1 at the edge, above that outside
    public double Norm(double x, double z)
    {
        if (shape is BrushShape.Random)
        {
            double rx = x * cos + z * sin, rz = -x * sin + z * cos;   // rotate back
            x = rx; z = rz / aspect;
        }
        switch (shape)
        {
            case BrushShape.Round: return Math.Sqrt(x * x + z * z);
            case BrushShape.Square: return Math.Max(Math.Abs(x), Math.Abs(z));
            case BrushShape.Diamond: return Math.Abs(x) + Math.Abs(z);
            case BrushShape.Hexagon:
                return Math.Max(Math.Max(Math.Abs(x * 0.8660254 + z * 0.5), Math.Abs(-x * 0.8660254 + z * 0.5)), Math.Abs(z)) / 0.8660254;
            case BrushShape.Ellipse: return Math.Sqrt(x * x + (z / 0.5) * (z / 0.5));
            case BrushShape.Cross:
                return Math.Min(Math.Max(Math.Abs(x), Math.Abs(z) / 0.22), Math.Max(Math.Abs(z), Math.Abs(x) / 0.22));
            default:
                double d = Math.Sqrt(x * x + z * z);
                if (d < 1e-9) return 0;
                double ang = Math.Atan2(z, x) * 180 / Math.PI;
                if (ang < 0) ang += 360;
                int i0 = (int)ang % 360, i1 = (i0 + 1) % 360;
                double t = ang - Math.Floor(ang);
                return d / (rho[i0] * (1 - t) + rho[i1] * t);
        }
    }

    /// outline in unit size for the display (edge radius per angle found by bisection)
    public List<Point> Outline(int count = 72)
    {
        var list = new List<Point>(count);
        for (int i = 0; i < count; i++)
        {
            double a = i * 2 * Math.PI / count, c = Math.Cos(a), s = Math.Sin(a), lo = 0, hi = 1.6;
            for (int it = 0; it < 28; it++)
            {
                double mid = (lo + hi) / 2;
                if (Norm(mid * c, mid * s) <= 1) lo = mid; else hi = mid;
            }
            list.Add(new Point(lo * c, lo * s));
        }
        return list;
    }
}
