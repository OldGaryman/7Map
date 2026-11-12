using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MapEditor.Map;

/// The images of the map (1 pixel = 2 m, row 0 = north): terrain with biomes, relief only, water, roads.
/// After a change Update() redraws only the affected section.
public sealed class MapLayers
{
    public const int MetersPerPixel = 2;
    public int Pixels { get; private set; }
    public BitmapSource Terrain => terrain;
    public BitmapSource Relief => relief;
    public BitmapSource Water { get; private set; }
    public BitmapSource Roads => roads;
    BitmapSource terrain, relief, roads;
    float hMin, hMax;   // height range when creating (for the brightening by height, stays the same on updates)

    const double Cell = MetersPerPixel, Exaggeration = 2.0;
    static readonly double Zen = 45 * Math.PI / 180, Az = (360 - 315 + 90) % 360 * Math.PI / 180;

    public static MapLayers Build(WorldData w, IProgress<string> progress)
    {
        int n = w.Size / MetersPerPixel;
        var l = new MapLayers { Pixels = n };

        progress.Report(Loc.T("Calculating terrain relief …"));
        var shade = l.Shading(w, n);
        var terrain = new byte[n * n * 4];
        Parallel.For(0, n, py =>
        {
            for (int px = 0; px < n; px++) TerrainPixel(w, px, py, shade[py * n + px], terrain, (py * n + px) * 4);
        });
        // relief as a grayscale image: one byte per pixel instead of four (saves memory)
        var relief = BitmapSource.Create(n, n, 96, 96, PixelFormats.Gray8, null, shade, n);
        relief.Freeze();
        l.relief = relief;
        l.terrain = PngTools.ToBitmap(terrain, n);

        progress.Report(Loc.T("Drawing roads and water …"));
        var water = Shrink(w, w.WaterMask, n);
        var wo = new byte[n * n * 4];
        var ro = new byte[n * n * 4];
        for (int i = 0, p = 0; i < n * n; i++, p += 4)
            if (water[i] != 0) { wo[p] = 214; wo[p + 1] = 160; wo[p + 2] = 92; wo[p + 3] = (byte)Math.Min(255, water[i] * 2); }
        Parallel.For(0, n, py =>
        {
            for (int px = 0; px < n; px++) RoadPixel(w, px, py, ro, (py * n + px) * 4);
        });
        l.Water = PngTools.ToBitmap(wo, n);
        l.roads = PngTools.ToBitmap(ro, n);
        return l;
    }

    /// redraw section x0..x1, z0..z1 (world coordinates, meters) – call only on the UI thread
    public void Update(WorldData w, int x0, int z0, int x1, int z1)
    {
        int n = Pixels, f = MetersPerPixel;
        // 1 pixel margin: the shading depends on the neighbors
        int px0 = Math.Clamp((x0 + w.Half) / f - 1, 0, n - 1), px1 = Math.Clamp((x1 + w.Half) / f + 1, 0, n - 1);
        int py0 = Math.Clamp((w.Half - 1 - z1) / f - 1, 0, n - 1), py1 = Math.Clamp((w.Half - 1 - z0) / f + 1, 0, n - 1);
        int rw = px1 - px0 + 1, rh = py1 - py0 + 1;
        var t = new byte[rw * rh * 4];
        var s = new byte[rw * rh];
        var ro = new byte[rw * rh * 4];
        var wa = new byte[rw * rh * 4];   // water: per pixel the largest blue value of the 2 × 2 meters
        double range = Math.Max(1, hMax - hMin);
        double At(int x, int y) => AverageHeight(w, Math.Clamp(x, 0, n - 1), Math.Clamp(y, 0, n - 1));
        for (int py = py0; py <= py1; py++)
            for (int px = px0; px <= px1; px++)
            {
                int i = (py - py0) * rw + px - px0;
                byte v = ShadePixel(At(px - 1, py - 1), At(px, py - 1), At(px + 1, py - 1), At(px - 1, py), At(px, py), At(px + 1, py),
                    At(px - 1, py + 1), At(px, py + 1), At(px + 1, py + 1), hMin, range);
                s[i] = v;
                TerrainPixel(w, px, py, v, t, i * 4);
                RoadPixel(w, px, py, ro, i * 4);
                byte wv = 0;
                for (int dz = 0; dz < f; dz++)
                    for (int dx = 0; dx < f; dx++)
                        wv = Math.Max(wv, w.WaterMask[w.Index(px * f - w.Half + dx, w.Half - 1 - py * f - dz)]);
                if (wv != 0) { wa[i * 4] = 214; wa[i * 4 + 1] = 160; wa[i * 4 + 2] = 92; wa[i * 4 + 3] = (byte)Math.Min(255, wv * 2); }
            }
        var rect = new Int32Rect(px0, py0, rw, rh);
        Writable(ref terrain).WritePixels(rect, t, rw * 4, 0);
        Writable(ref relief).WritePixels(rect, s, rw, 0);
        Writable(ref roads).WritePixels(rect, ro, rw * 4, 0);
        var water = Water;
        Writable(ref water).WritePixels(rect, wa, rw * 4, 0);
        Water = water;
    }

    // on the first update the frozen image becomes a writable one (copy, afterwards the old one is freed)
    static WriteableBitmap Writable(ref BitmapSource b)
    {
        if (b is WriteableBitmap wb) return wb;
        wb = new WriteableBitmap(b);
        b = wb;
        return wb;
    }

    // biome color, darkened or brightened with the relief (BGRA)
    static void TerrainPixel(WorldData w, int px, int py, byte shade, byte[] o, int at)
    {
        var c = BiomePalette.Display[w.BiomeAt(px * MetersPerPixel - w.Half, w.Half - 1 - py * MetersPerPixel)];
        double f = 0.45 + 0.65 * shade / 255.0;
        o[at] = (byte)Math.Min(255, c[2] * f); o[at + 1] = (byte)Math.Min(255, c[1] * f); o[at + 2] = (byte)Math.Min(255, c[0] * f); o[at + 3] = 255;
    }

    // road per pixel = maximum of the block (narrow paths stay visible); asphalt dark gray, gravel brown (BGRA)
    static void RoadPixel(WorldData w, int px, int py, byte[] o, int at)
    {
        int f = MetersPerPixel, size = w.Size;
        byte v = 0;
        for (int dy = 0; dy < f; dy++)
        {
            int row = (size - 1 - (py * f + dy)) * size + px * f;
            for (int dx = 0; dx < f; dx++) v = Math.Max(v, w.RoadMask[row + dx]);
        }
        if (v == WorldData.Asphalt) { o[at] = 62; o[at + 1] = 58; o[at + 2] = 58; o[at + 3] = 255; }
        else if (v == WorldData.Gravel) { o[at] = 82; o[at + 1] = 128; o[at + 2] = 166; o[at + 3] = 255; }
        else o[at] = o[at + 1] = o[at + 2] = o[at + 3] = 0;
    }

    // shrink the mask (1 m, row 0 = south) to n × n, per block the maximum, row 0 = north
    static byte[] Shrink(WorldData w, byte[] mask, int n)
    {
        int f = w.Size / n, size = w.Size;
        var res = new byte[n * n];
        Parallel.For(0, n, py =>
        {
            for (int dy = 0; dy < f; dy++)
            {
                int row = (size - 1 - (py * f + dy)) * size;
                for (int x = 0; x < size; x++)
                {
                    byte v = mask[row + x];
                    if (v > res[py * n + x / f]) res[py * n + x / f] = v;
                }
            }
        });
        return res;
    }

    // mean height (m) of the pixel px, py (dtm: row 0 = south)
    static float AverageHeight(WorldData w, int px, int py)
    {
        int f = MetersPerPixel, size = w.Size;
        double s = 0;
        for (int dy = 0; dy < f; dy++)
        {
            int row = size - 1 - (py * f + dy);
            for (int dx = 0; dx < f; dx++) s += w.Heights[row * size + px * f + dx];
        }
        return (float)(s / (f * f) / 256.0);
    }

    // shading (light from the northwest) from the averaged heights, flat land about equally bright, height slightly brightened
    byte[] Shading(WorldData w, int n)
    {
        var h = new float[n * n];
        Parallel.For(0, n, py =>
        {
            for (int px = 0; px < n; px++) h[py * n + px] = AverageHeight(w, px, py);
        });
        hMin = h.Min(); hMax = h.Max();
        float min = hMin;
        double range = Math.Max(1, hMax - hMin);
        var res = new byte[n * n];
        float At(int x, int y) => h[Math.Clamp(y, 0, n - 1) * n + Math.Clamp(x, 0, n - 1)];
        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
                res[y * n + x] = ShadePixel(At(x - 1, y - 1), At(x, y - 1), At(x + 1, y - 1), At(x - 1, y), h[y * n + x], At(x + 1, y),
                    At(x - 1, y + 1), At(x, y + 1), At(x + 1, y + 1), min, range);
        });
        return res;
    }

    // a b c / d m e / g hh i = 3 × 3 heights around the pixel (top row = north)
    static byte ShadePixel(double a, double b, double c, double d, double m, double e, double g, double hh, double i, double min, double range)
    {
        double dzdx = ((c + 2 * e + i) - (a + 2 * d + g)) / (8 * Cell);
        double dzdy = ((g + 2 * hh + i) - (a + 2 * b + c)) / (8 * Cell);
        double slope = Math.Atan(Exaggeration * Math.Sqrt(dzdx * dzdx + dzdy * dzdy));
        double aspect = Math.Atan2(dzdy, -dzdx);
        double sh = Math.Max(0, Math.Cos(Zen) * Math.Cos(slope) + Math.Sin(Zen) * Math.Sin(slope) * Math.Cos(Az - aspect));
        double elev = (m - min) / range;
        return (byte)Math.Clamp(255 * (0.84 * sh / Math.Cos(Zen) + 0.16 * elev), 0, 255);
    }
}
