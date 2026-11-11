using System.IO;

namespace MapEditor.Map;

/// Grundform eines Bergs oder Hügels: Gebirge und Plateau (Wüste) kommen aus den Stempeln des Spiels, Hügel ebenso, die Kuppe ist rund
public enum MountainKind { Mountain, Plateau, Hill, Dome }

/// Berg-/Hügel-Pinsel (Wunsch des Nutzers 4.11.2026): ganze Berge setzen. Die Form kommt aus den Höhenstempeln des Spiels
/// (Data\Stamps\*.raw: 512 × 512 Pixel, 3 Kanäle zu 16 Bit, Kanal 1 = Höhe, Meter = Wert / 256), mit denen auch die Weltgenerierung
/// arbeitet. Gemessen an My World, Wilibii County und North Ledulu Mountains: Gipfelrelief ≈ Stärke × Stempelspitze (siehe TERRAIN_ANALYSE.md).
public sealed class MountainSpec
{
    public MountainKind Kind;
    /// Stempel 1 oder 2 (0/1)
    public int Variant;
    /// Höhe des Gipfels über dem Gelände (m); negativ = Mulde
    public double Height = 80;
    /// Breite des Stempels (m); der Berg bleibt innerhalb des Kreises mit diesem Durchmesser
    public double Size = 600;
    /// Drehung in Grad (im Uhrzeigersinn) und Spiegeln
    public double Rotation;
    public bool Mirror;
    /// Steilheit in Prozent: 100 = Form des Stempels, mehr = spitzer Gipfel und flacher Fuß, weniger = breiter, runder
    public double Steep = 100;
    /// Zerklüftung in Prozent: feines Rauschen auf dem Berg (100 ≈ Höhe × 12 %)
    public double Rough = 30;
    public int Seed;
    /// Höhenstempel, auf 0…1 normiert (512 × 512); null bei der Kuppe
    public float[] Stamp;

    public const int StampSize = 512;
    public double Radius => Size / 2;

    /// Stempel für die Art laden; null = nicht gefunden (dann ist nur die Kuppe möglich)
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
        if (b.Length < n * 6) return null;   // 3 Kanäle zu 16 Bit
        var h = new float[n];
        float max = 0;
        for (int i = 0; i < n; i++)
        {
            float v = BitConverter.ToUInt16(b, i * 6 + 2);   // Kanal 1 = Höhe
            h[i] = v;
            if (v > max) max = v;
        }
        if (max <= 0) return null;
        for (int i = 0; i < n; i++) h[i] /= max;
        return h;
    }

    /// Höhenänderung (Meter) an der Stelle (x, z) für einen Berg mit Mittelpunkt (cx, cz); 0 außerhalb des Kreises
    public float Delta(double x, double z, double cx, double cz)
    {
        double dx = x - cx, dz = z - cz;
        double r = Math.Sqrt(dx * dx + dz * dz) / Radius;
        if (r >= 1) return 0;
        // in den Stempel zurückdrehen
        double a = Rotation * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
        double u = dx * ca - dz * sa, v = dx * sa + dz * ca;   // Drehung im Uhrzeigersinn (z zeigt nach Norden)
        if (Mirror) u = -u;
        double value;
        if (Stamp == null)
        {
            value = 0.5 + 0.5 * Math.Cos(Math.PI * r);   // runde Kuppe
        }
        else
        {
            double fx = StampSize / 2.0 + u / Size * StampSize, fy = StampSize / 2.0 + v / Size * StampSize;
            value = Sample(fx, fy);
        }
        if (value <= 0) return 0;
        double p = Math.Clamp(Steep, 20, 300) / 100;
        value = Math.Pow(value, p);
        // Rand: ab 80 % des Radius weich auf 0, damit der Berg nirgends eine Kante hat
        double edge = r <= 0.8 ? 1 : 1 - Smooth((r - 0.8) / 0.2);
        double d = Height * value;
        if (Rough > 0)
        {
            int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
            double noise = 0.5 * RoadApplier.Octave(ix, iz, 60, Seed) + 0.35 * RoadApplier.Octave(ix, iz, 24, Seed + 1) + 0.2 * RoadApplier.Octave(ix, iz, 9, Seed + 2);
            // nur dort, wo der Berg schon Höhe hat (am Fuß bleibt der Boden ruhig)
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
