using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MapEditor.Map;

/// Read the large world PNGs (WPF decoder: needs less memory than System.Drawing and also works while the game is running).
public static class PngTools
{
    public static BitmapSource Open(string file)
    {
        using var fs = File.OpenRead(file);
        var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource src = dec.Frames[0];
        if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        return src;
    }

    /// Reads a world PNG (size × size) into a mask with the same layout as dtm.raw (index (z + Half) · size + x + Half,
    /// row 0 = south). classify(b, g, r) returns 0 = nothing; existing values are only raised (several files together).
    public static void ReadMask(string file, int size, byte[] mask, Func<byte, byte, byte, byte> classify) =>
        ReadMask(file, size, mask, (b, g, r, a) => classify(b, g, r));

    /// as above, classify(b, g, r, a) additionally gets the alpha value
    public static void ReadMask(string file, int size, byte[] mask, Func<byte, byte, byte, byte, byte> classify)
    {
        var src = Open(file);
        if (src.PixelWidth != size || src.PixelHeight != size) throw new InvalidDataException(Loc.F("{0} does not have the map size {1}.", Path.GetFileName(file), size));
        const int strip = 256;
        var buf = new byte[size * 4 * strip];
        for (int y0 = 0; y0 < size; y0 += strip)
        {
            int rows = Math.Min(strip, size - y0);
            src.CopyPixels(new Int32Rect(0, y0, size, rows), buf, size * 4, 0);
            Parallel.For(0, rows, r =>
            {
                int o = (size - 1 - (y0 + r)) * size;
                for (int x = 0, i = r * size * 4; x < size; x++, i += 4)
                {
                    byte v = classify(buf[i], buf[i + 1], buf[i + 2], buf[i + 3]);
                    if (v > mask[o + x]) mask[o + x] = v;
                }
            });
        }
    }

    public static BitmapSource ToBitmap(byte[] bgra, int n)
    {
        var b = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, bgra, n * 4);
        b.Freeze();
        return b;
    }
}
