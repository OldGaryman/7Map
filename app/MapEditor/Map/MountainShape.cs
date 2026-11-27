using System.IO;

namespace MapEditor.Map;

/// Basic shape of a mountain or hill: mountains and plateau (desert) come from the stamps of the game, hills too, the dome is round
public enum MountainKind { Mountain, Plateau, Hill, Dome }

/// Mountain/hill brush (the user's request, 2026-11-04): place whole mountains. The shape comes from the height stamps of the game
/// (Data\Stamps\*.raw: 512 × 512 pixels, 3 channels of 16 bit, channel 1 = height, meters = value / 256), which the world generation
/// also uses. Measured on My World, Wilibii County and North Ledulu Mountains: peak relief ≈ strength × stamp peak (see TERRAIN_ANALYSE.md).
public sealed class MountainSpec
{
    public MountainKind Kind;
    /// stamp 1 or 2 (0/1)
    public int Variant;
    /// height of the peak above the terrain (m); negative = hollow
    public double Height = 80;
    /// width of the stamp (m); the mountain stays within the circle of this diameter
    public double Size = 600;
    /// rotation in degrees (clockwise) and mirroring
    public double Rotation;
    public bool Mirror;
    /// steepness in percent: 100 = shape of the stamp, more = sharper peak and flatter foot, less = wider, rounder
    public double Steep = 100;
    /// ruggedness in percent: fine noise on the mountain (100 ≈ height × 12 %)
    public double Rough = 30;
    public int Seed;
    /// height stamp, normalized to 0…1 (512 × 512); null for the dome
    public float[] Stamp;

    public const int StampSize = 512;
    public double Radius => Size / 2;

    /// load the stamp for the kind; null = not found (then only the dome is possible)
    public static float[] LoadStamp(string gameFolder, MountainKind kind, int variant)
    {
        string name = kind switch
        {
            MountainKind.Mountain => "mountains_0",
            MountainKind.Plateau => "desert_mountains_0",
            MountainKind.Hill => "hills_0",
            _ => null,
        };
        if (name == null || gameFolder == null) return null;
        string file = Path.Combine(gameFolder, "Data", "Stamps", name + (variant == 0 ? "1" : "2") + ".raw");
        if (!File.Exists(file)) return null;
        var b = File.ReadAllBytes(file);
        int n = StampSize * StampSize;
        if (b.Length < n * 6) return null;   // 3 channels of 16 bit
        var h = new float[n];
        float max = 0;
        for (int i = 0; i < n; i++)
        {
            float v = BitConverter.ToUInt16(b, i * 6 + 2);   // channel 1 = height
            h[i] = v;
            if (v > max) max = v;
        }
        if (max <= 0) return null;
        for (int i = 0; i < n; i++) h[i] /= max;
        return h;
    }

    /// height change (meters) at the place (x, z) for a mountain with center (cx, cz); 0 outside the circle
    public float Delta(double x, double z, double cx, double cz)
    {
        double dx = x - cx, dz = z - cz;
        double r = Math.Sqrt(dx * dx + dz * dz) / Radius;
        if (r >= 1) return 0;
        // rotate back into the stamp
        double a = Rotation * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
        double u = dx * ca - dz * sa, v = dx * sa + dz * ca;   // rotation clockwise (z points north)
        if (Mirror) u = -u;
        double value;
        if (Stamp == null)
        {
            value = 0.5 + 0.5 * Math.Cos(Math.PI * r);   // round dome
        }
        else
        {
            double fx = StampSize / 2.0 + u / Size * StampSize, fy = StampSize / 2.0 + v / Size * StampSize;
            value = Sample(fx, fy);
        }
        if (value <= 0) return 0;
        double p = Math.Clamp(Steep, 20, 300) / 100;
        value = Math.Pow(value, p);
        // edge: from 80 % of the radius softly to 0, so that the mountain has no edge anywhere
        double edge = r <= 0.8 ? 1 : 1 - Smooth((r - 0.8) / 0.2);
        double d = Height * value;
        if (Rough > 0)
        {
            int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
            double noise = 0.5 * RoadApplier.Octave(ix, iz, 60, Seed) + 0.35 * RoadApplier.Octave(ix, iz, 24, Seed + 1) + 0.2 * RoadApplier.Octave(ix, iz, 9, Seed + 2);
            // only where the mountain already has height (at the foot the ground stays calm)
            d += Math.Abs(Height) * Rough / 100 * 0.12 * noise * Math.Min(1, value * 6);
        }
        return (float)(d * edge);
    }

    double Sample(double fx, double fy)
    {
        if (fx < 0 || fy < 0 || fx >= StampSize - 1 || fy >= StampSize - 1) return 0;
        int ix = (int)fx, iy = (int)fy;
        double tx = fx - ix, ty = fy - iy;
        int i = iy * StampSize + ix;
        return (Stamp[i] * (1 - tx) + Stamp[i + 1] * tx) * (1 - ty) + (Stamp[i + StampSize] * (1 - tx) + Stamp[i + StampSize + 1] * tx) * ty;
    }

    static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
