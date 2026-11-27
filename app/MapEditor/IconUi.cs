using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MapEditor;

/// Helpers for the icon font (Bootstrap Icons, Assets\bootstrap-icons.ttf); the characters are in Icons (Icons.cs)
public static class IconUi
{
    /// font with the icons; for buttons and texts built in code (in XAML: style IconText)
    static FontFamily font;
    public static FontFamily Font => font ??= new(new Uri("pack://application:,,,/YAMEE;component/"), "./Assets/#bootstrap-icons");

    /// Button with only an icon (e.g. trash can in lists): without border, with tooltip
    public static Button IconButton(string icon, string toolTip, object tag = null, double size = 14)
    {
        var b = new Button
        {
            Content = icon, FontFamily = Font, FontSize = size, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = toolTip, Tag = tag, VerticalAlignment = VerticalAlignment.Center
        };
        return b;
    }

    /// Column header of a sortable list: text, with a small arrow behind it for the selected column
    public static void SetSortHead(TextBlock head, string text, bool sorted, bool descending)
    {
        head.Inlines.Clear();
        head.Inlines.Add(new Run(text));
        if (sorted)
            head.Inlines.Add(new Run(" " + (descending ? Icons.CaretDownFill : Icons.CaretUpFill)) { FontFamily = Font, FontSize = head.FontSize * 0.85 });
    }

    static Cursor eyedropper;
    /// mouse pointer "pipette" (lake tool: pick the water level), the tip at the bottom left is the click point
    public static Cursor EyedropperCursor => eyedropper ??= IconCursor(Icons.Eyedropper, 0, 1);

    /// mouse pointer from an icon of the font: black with a white outline (visible on every map), 32 × 32;
    /// (hx, hy) = click point as a share of the icon (0,0 = top left, 0,1 = bottom left)
    public static Cursor IconCursor(string icon, double hx, double hy, FontFamily family = null)
    {
        try
        {
            const int S = 32;
            var text = new FormattedText(icon, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(family ?? Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 26, Brushes.Black, 1.0);
            var geo = text.BuildGeometry(new Point(0, 0));
            var b = geo.Bounds;
            double k = (S - 4) / Math.Max(b.Width, b.Height);
            var tf = new TransformGroup();
            tf.Children.Add(new TranslateTransform(-b.X, -b.Y));
            tf.Children.Add(new ScaleTransform(k, k));
            tf.Children.Add(new TranslateTransform(2, 2));
            geo.Transform = tf;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawGeometry(null, new Pen(Brushes.White, 2.5) { LineJoin = PenLineJoin.Round }, geo);
                dc.DrawGeometry(Brushes.Black, null, geo);
            }
            var bmp = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var png = new MemoryStream();
            enc.Save(png);
            // .cur file in memory: header, one entry with the click point, the PNG image (Windows Vista and later)
            int x = (int)Math.Round(2 + hx * b.Width * k), y = (int)Math.Round(2 + hy * b.Height * k);
            var cur = new MemoryStream();
            using (var w = new BinaryWriter(cur, System.Text.Encoding.ASCII, true))
            {
                w.Write((short)0); w.Write((short)2); w.Write((short)1);
                w.Write((byte)S); w.Write((byte)S); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)Math.Clamp(x, 0, S - 1)); w.Write((short)Math.Clamp(y, 0, S - 1));
                w.Write((int)png.Length); w.Write(22);
                w.Write(png.ToArray());
            }
            cur.Position = 0;
            return new Cursor(cur);
        }
        catch { return Cursors.Cross; }
    }
}
