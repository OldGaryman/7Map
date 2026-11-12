using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MapEditor.Controls;

/// Ruler at the edge of the map (View → Rulers, the user's request, 2026-11-04): meters from the map center, east–west at the top,
/// south–north on the left. Follows zoom and panning of the map (checks on every frame whether the view has changed).
public sealed class MapRuler : FrameworkElement
{
    public MapView Map { get; set; }
    public bool Vertical { get; set; }

    double lastScale, lastShift;
    double lastSize;

    public MapRuler()
    {
        ClipToBounds = true;   // do not draw the labels at the edge into the neighboring areas (left side)
        IsVisibleChanged += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            if (IsVisible) CompositionTarget.Rendering += OnFrame;
        };
    }

    // view (zoom, panning) changed? Then redraw
    void OnFrame(object sender, EventArgs e)
    {
        if (Map == null || !Map.HasWorld) return;
        double shift = Vertical ? Map.ToScreenY(0) : Map.ToScreenX(0);
        double size = Vertical ? Map.ActualHeight : Map.ActualWidth;
        if (Map.Scale == lastScale && shift == lastShift && size == lastSize) return;
        lastScale = Map.Scale; lastShift = shift; lastSize = size;
        InvalidateVisual();
    }

    // "nice" step size (1, 2, 5 × 10^k), at least as large as requested
    static double NiceStep(double min)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(min)));
        foreach (double m in new[] { 1.0, 2.0, 5.0, 10.0 })
            if (p * m >= min) return p * m;
        return p * 10;
    }

    static Brush Res(string key, Brush fallback) => Application.Current.TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var back = Res("PanelBrush", Brushes.WhiteSmoke);
        var line = Res("LineBrush", Brushes.Gray);
        var text = Res("SubtleTextBrush", Brushes.DimGray);
        dc.DrawRectangle(back, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Map == null || !Map.HasWorld || Map.Scale <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;

        var pen = new Pen(line, 1);
        var tick = new Pen(text, 1);
        double scale = Map.Scale;
        double step = NiceStep(80 / scale);     // labeled ticks at least 80 pixels apart
        double minor = step / 5;
        double a = Vertical ? Map.ToWorldZ(ActualHeight) : Map.ToWorldX(0), b = Vertical ? Map.ToWorldZ(0) : Map.ToWorldX(Map.ActualWidth);
        if (a > b) (a, b) = (b, a);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface("Segoe UI");

        if (minor * scale >= 7)
            for (double v = Math.Floor(a / minor) * minor; v <= b; v += minor)
            {
                double s = Vertical ? Map.ToScreenY(v) : Map.ToScreenX(v);
                if (Vertical) dc.DrawLine(tick, new Point(ActualWidth - 4, s), new Point(ActualWidth, s));
                else dc.DrawLine(tick, new Point(s, ActualHeight - 4), new Point(s, ActualHeight));
            }
        for (double v = Math.Floor(a / step) * step; v <= b; v += step)
        {
            double s = Vertical ? Map.ToScreenY(v) : Map.ToScreenX(v);
            var ft = new FormattedText(Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10, text, dpi);
            if (Vertical)
            {
                dc.DrawLine(tick, new Point(ActualWidth - 10, s), new Point(ActualWidth, s));
                dc.PushTransform(new RotateTransform(-90, 1, s));
                dc.DrawText(ft, new Point(4, s + 1));
                dc.Pop();
            }
            else
            {
                dc.DrawLine(tick, new Point(s, ActualHeight - 10), new Point(s, ActualHeight));
                dc.DrawText(ft, new Point(s + 3, 1));
            }
        }
        // edge to the map
        if (Vertical) dc.DrawLine(pen, new Point(ActualWidth - 0.5, 0), new Point(ActualWidth - 0.5, ActualHeight));
        else dc.DrawLine(pen, new Point(0, ActualHeight - 0.5), new Point(ActualWidth, ActualHeight - 0.5));
    }
}
