using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace MapEditor.Map;

/// Height deltas and break mask of wasteland bomb craters and earthquake cracks (only in biome 4 = wasteland).
/// Stored on the 4 m coarse grid of WorldGenerator (row 0 = south, col 0 = west), saved gzip-compressed as
/// yamee_wasteland.bin alongside dtm.raw so road/path builders can plan on pre-catastrophe ground and cut the
/// craters and cracks cleanly back into the finished road (with ragged broken edges and fault offsets).
public sealed class WastelandDamage
{
    public const string FileName = "yamee_wasteland.bin";
    const int Magic = 0x31445759;   // "YWD1"
    const double SdRange = 8.0;     // break signed distance clamped to [-8, +8] m

    public int Size { get; }
    public int N { get; }
    public double Cell { get; }
    public bool HasAny { get; }

    readonly double half;
    readonly short[] delta;         // height delta in 1/256 m (same scale as dtm.raw)
    readonly byte[] breakField;     // signed distance into break zone: 128 = 0 m, > 128 = inside, < 128 = outside

    WastelandDamage(int size, int n, short[] delta, byte[] breakField, bool hasAny)
    {
        Size = size;
        N = n;
        Cell = (double)size / n;
        half = size / 2.0;
        this.delta = delta;
        this.breakField = breakField;
        HasAny = hasAny;
    }

    public static byte EncodeSd(double sd) =>
        (byte)Math.Clamp(Math.Round(sd / SdRange * 127.0 + 128.0), 0, 255);

    public static double DecodeSd(byte v) => (v - 128.0) / 127.0 * SdRange;

    /// Build from generator coarse arrays (delta in meters, signed break distance in meters).
    public static WastelandDamage FromGrid(int size, int n, float[] deltaMeters, float[] breakSdMeters)
    {
        int len = n * n;
        var d = new short[len];
        var b = new byte[len];
        bool any = false;
        for (int i = 0; i < len; i++)
        {
            short dv = (short)Math.Clamp(Math.Round(deltaMeters[i] * 256.0), short.MinValue, short.MaxValue);
            byte bv = EncodeSd(breakSdMeters[i]);
            d[i] = dv;
            b[i] = bv;
            if (dv != 0 || bv > 128) any = true;
        }
        return new WastelandDamage(size, n, d, b, any);
    }

    /// Bicubic interpolation of the height delta (m) at world coordinates (wx, wz) – matches WorldGenerator.FullResolution.
    public double DeltaAt(double wx, double wz)
    {
        if (!HasAny) return 0;
        double fx = (wx + half) / Cell - 0.5;
        double fz = (wz + half) / Cell - 0.5;
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        if (ix < -1 || iz < -1 || ix >= N || iz >= N) return 0;
        double tx = Math.Clamp(fx - ix, 0, 1), tz = Math.Clamp(fz - iz, 0, 1);

        int x0 = Math.Clamp(ix - 1, 0, N - 1), x1 = Math.Clamp(ix, 0, N - 1);
        int x2 = Math.Clamp(ix + 1, 0, N - 1), x3 = Math.Clamp(ix + 2, 0, N - 1);
        int z0 = Math.Clamp(iz - 1, 0, N - 1), z1 = Math.Clamp(iz, 0, N - 1);
        int z2 = Math.Clamp(iz + 1, 0, N - 1), z3 = Math.Clamp(iz + 2, 0, N - 1);

        // fast path: most of the map is outside wasteland craters/cracks
        int r0 = z0 * N, r1 = z1 * N, r2 = z2 * N, r3 = z3 * N;
        if (((ushort)delta[r1 + x1] | (ushort)delta[r1 + x2] | (ushort)delta[r2 + x1] | (ushort)delta[r2 + x2] |
             (ushort)delta[r0 + x1] | (ushort)delta[r0 + x2] | (ushort)delta[r3 + x1] | (ushort)delta[r3 + x2] |
             (ushort)delta[r1 + x0] | (ushort)delta[r1 + x3] | (ushort)delta[r2 + x0] | (ushort)delta[r2 + x3] |
             (ushort)delta[r0 + x0] | (ushort)delta[r0 + x3] | (ushort)delta[r3 + x0] | (ushort)delta[r3 + x3]) == 0)
            return 0;

        CubicWeights(tx, out double wx0, out double wx1, out double wx2, out double wx3);
        CubicWeights(tz, out double wz0, out double wz1, out double wz2, out double wz3);

        double h =
            wz0 * (wx0 * delta[r0 + x0] + wx1 * delta[r0 + x1] + wx2 * delta[r0 + x2] + wx3 * delta[r0 + x3]) +
            wz1 * (wx0 * delta[r1 + x0] + wx1 * delta[r1 + x1] + wx2 * delta[r1 + x2] + wx3 * delta[r1 + x3]) +
            wz2 * (wx0 * delta[r2 + x0] + wx1 * delta[r2 + x1] + wx2 * delta[r2 + x2] + wx3 * delta[r2 + x3]) +
            wz3 * (wx0 * delta[r3 + x0] + wx1 * delta[r3 + x1] + wx2 * delta[r3 + x2] + wx3 * delta[r3 + x3]);
        return h / 256.0;
    }

    /// Bilinear interpolation of the break signed distance (m) at world coordinates (wx, wz).
    /// Positive = inside a wasteland crater or earthquake crack; negative = outside (up to -8 m).
    public double BreakSdAt(double wx, double wz)
    {
        if (!HasAny) return -SdRange;
        double fx = (wx + half) / Cell - 0.5;
        double fz = (wz + half) / Cell - 0.5;
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        if (ix < -1 || iz < -1 || ix >= N || iz >= N) return -SdRange;
        double tx = Math.Clamp(fx - ix, 0, 1), tz = Math.Clamp(fz - iz, 0, 1);
        int x0 = Math.Clamp(ix, 0, N - 1), x1 = Math.Clamp(ix + 1, 0, N - 1);
        int z0 = Math.Clamp(iz, 0, N - 1), z1 = Math.Clamp(iz + 1, 0, N - 1);

        byte b00 = breakField[z0 * N + x0], b10 = breakField[z0 * N + x1];
        byte b01 = breakField[z1 * N + x0], b11 = breakField[z1 * N + x1];
        if ((b00 | b10 | b01 | b11) == 0) return -SdRange;

        double v = (b00 * (1 - tx) + b10 * tx) * (1 - tz) + (b01 * (1 - tx) + b11 * tx) * tz;
        return (v - 128.0) / 127.0 * SdRange;
    }

    /// True if any coarse cell inside the world-coordinate rectangle [x0, x1] × [z0, z1] lies in or right at a break zone.
    public bool HasBreakInRect(double x0, double z0, double x1, double z1, byte threshold = 120)
    {
        if (!HasAny) return false;
        int i0 = Math.Clamp((int)Math.Floor((x0 + half) / Cell), 0, N - 1);
        int i1 = Math.Clamp((int)Math.Floor((x1 + half) / Cell), 0, N - 1);
        int j0 = Math.Clamp((int)Math.Floor((z0 + half) / Cell), 0, N - 1);
        int j1 = Math.Clamp((int)Math.Floor((z1 + half) / Cell), 0, N - 1);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
                if (breakField[j * N + i] >= threshold) return true;
        return false;
    }

    static void CubicWeights(double t, out double w0, out double w1, out double w2, out double w3)
    {
        double t2 = t * t, t3 = t2 * t;
        w0 = -0.5 * t + t2 - 0.5 * t3;
        w1 = 1 - 2.5 * t2 + 1.5 * t3;
        w2 = 0.5 * t + 2 * t2 - 1.5 * t3;
        w3 = -0.5 * t2 + 0.5 * t3;
    }

    public void Save(string folder)
    {
        string path = Path.Combine(folder, FileName);
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var bw = new BinaryWriter(gz);
        bw.Write(Magic);
        bw.Write(Size);
        bw.Write(N);
        bw.Write(MemoryMarshal.AsBytes(delta.AsSpan()));
        bw.Write(breakField);
    }

    public static WastelandDamage Load(string folder, int expectedSize)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var br = new BinaryReader(gz);
            if (br.ReadInt32() != Magic) return null;
            int size = br.ReadInt32();
            int n = br.ReadInt32();
            if (size != expectedSize || n <= 0 || n > size || size % n != 0) return null;
            int len = n * n;
            var d = new short[len];
            var b = new byte[len];
            var dBytes = MemoryMarshal.AsBytes(d.AsSpan());
            int got = 0;
            while (got < dBytes.Length)
            {
                int r = gz.Read(dBytes.Slice(got));
                if (r <= 0) return null;
                got += r;
            }
            got = 0;
            while (got < b.Length)
            {
                int r = gz.Read(b, got, b.Length - got);
                if (r <= 0) return null;
                got += r;
            }
            bool any = false;
            for (int i = 0; i < len; i++)
                if (d[i] != 0 || b[i] > 128) { any = true; break; }
            return new WastelandDamage(size, n, d, b, any);
        }
        catch (Exception) { return null; }
    }

    /// Parse yamee_generator.txt for reconstructing wasteland craters & cracks on existing YAMEE worlds without yamee_wasteland.bin.
    internal static (GeneratorSpec Spec, bool HasLayout)? ParseGeneratorFile(string folder, string gameFolder, int expectedSize)
    {
        string file = Path.Combine(folder, "yamee_generator.txt");
        if (!File.Exists(file)) return null;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var spec = new GeneratorSpec { Size = expectedSize };
            bool hasSeed = false, hasShares = false, hasLayout = false, useStamps = true;
            foreach (var raw in File.ReadAllLines(file))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw[..eq].Trim(), v = raw[(eq + 1)..].Trim();
                switch (k)
                {
                    case "Size": if (int.TryParse(v, inv, out int sz) && sz != expectedSize) return null; break;
                    case "Seed": spec.Seed = v; hasSeed = true; break;
                    case "Shares":
                        var parts = v.Split(',');
                        if (parts.Length == 5 && parts.All(p => int.TryParse(p.Trim(), inv, out _)))
                        { spec.Shares = parts.Select(p => int.Parse(p.Trim(), inv)).ToArray(); hasShares = true; }
                        break;
                    case "BiomeCoherence": if (double.TryParse(v, inv, out double coh)) spec.BiomeCoherence = coh; break;
                    case "BiomeLayout": if (int.TryParse(v, inv, out int lay)) { spec.BiomeLayout = lay; hasLayout = true; } break;
                    case "Stamps": if (bool.TryParse(v, out bool st)) useStamps = st; break;
                }
            }
            if (!hasSeed || !hasShares || spec.Shares[4] <= 0) return null;
            spec.GameFolder = useStamps ? gameFolder : null;
            return (spec, hasLayout);
        }
        catch (Exception) { return null; }
    }
}
