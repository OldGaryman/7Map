using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MapEditor.Map;

namespace MapEditor.Controls;

/// Longitudinal profile of the planned road: terrain (brown), road (dark), cut red, fill blue, intersections as vertical lines.
public sealed class ProfileChart : FrameworkElement
{
    const double Left = 52, Right = 14, Top = 26, Bottom = 22;
    RoadResult result;
    double? hover;

    public RoadResult Result { get => result; set { result = value; hover = null; InvalidateVisual(); } }
    /// distance from the start under the mouse (null = mouse not over the chart)
    public event Action<double?> HoverChanged;

    static readonly CultureInfo Num = Loc.Num;   // numbers always English (1,234.5), the user's request: app for everyone
    static readonly Brush TerrainFill = Frozen(new SolidColorBrush(Color.FromRgb(232, 216, 186)));
    static readonly Pen TerrainPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(150, 110, 60)), 1.2));
    static readonly Pen RoadPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(35, 35, 40)), 2));
    static readonly Pen CutPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(110, 220, 40, 30)), 1));
    static readonly Pen FillPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(110, 40, 90, 220)), 1));
    static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(225, 225, 220)), 1));
    static readonly Pen WarnPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(220, 30, 30)), 1.5) { DashStyle = DashStyles.Dash });
    static readonly Pen OkPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(40, 160, 60)), 1.5) { DashStyle = DashStyles.Dash });
    static readonly Pen HoverPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 255)), 1.5));
    static readonly Brush Gray = Frozen(new SolidColorBrush(Color.FromRgb(110, 110, 110)));

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    double minH, maxH, xs, ys;
    double X(double dist) => Left + dist * xs;
    double Y(double h) => Top + (maxH - h) * ys;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(ThemeManager.Brush("WindowBrush", Brushes.White), null, new Rect(RenderSize));
        double w = ActualWidth - Left - Right, h = ActualHeight - Top - Bottom;
        if (result == null || w < 50 || h < 30)
        {
            dc.DrawText(Text(Loc.T("Height profile: appears as soon as a road has at least two points."), 12, Gray), new Point(Left, Top));
            return;
        }
        var path = result.Path;
        int n = path.Count;
        minH = Math.Floor(Math.Min(result.Profile.Min(), result.Terrain.Min()) - 1);
        maxH = Math.Ceiling(Math.Max(result.Profile.Max(), result.Terrain.Max()) + 1);
        xs = w / Math.Max(1, result.Length);
        ys = h / (maxH - minH);

        // grid with labels
        double hs = Nice((maxH - minH) / 4);
        for (double v = Math.Ceiling(minH / hs) * hs; v <= maxH; v += hs)
        {
            dc.DrawLine(GridPen, new Point(Left, Y(v)), new Point(Left + w, Y(v)));
            var t = Text(v.ToString("0", Num) + " m", 11, Gray);
            dc.DrawText(t, new Point(Left - t.Width - 6, Y(v) - t.Height / 2));
        }
        double ds = Nice(result.Length / 8);
        for (double d = 0; d <= result.Length; d += ds)
        {
            dc.DrawLine(GridPen, new Point(X(d), Top), new Point(X(d), Top + h));
            var t = Text(d >= 1000 ? (d / 1000).ToString("0.##", Num) + " km" : d.ToString("0", Num) + " m", 11, Gray);
            dc.DrawText(t, new Point(X(d) - t.Width / 2, Top + h + 3));
        }

        // per screen column: fill the terrain, cut/fill between road and terrain
        var terrain = new StreamGeometry();
        var road = new StreamGeometry();
        using (var tc = terrain.Open())
        using (var rc = road.Open())
        {
            tc.BeginFigure(new Point(Left, Top + h), true, true);
            for (double px = 0; px <= w; px += 1)
            {
                int i = result.IndexAt(px / xs);
                double yr = Y(result.Profile[i]), yt = Y(result.Terrain[i]);
                tc.LineTo(new Point(Left + px, yt), true, false);
                if (px == 0) rc.BeginFigure(new Point(Left, yr), false, false); else rc.LineTo(new Point(Left + px, yr), true, true);
                if (Math.Abs(yr - yt) >= 1) dc.DrawLine(yt < yr ? CutPen : FillPen, new Point(Left + px, yr), new Point(Left + px, yt));
            }
            tc.LineTo(new Point(Left + w, Top + h), true, false);
        }
        terrain.Freeze(); road.Freeze();
        dc.PushOpacity(0.55);
        dc.DrawGeometry(TerrainFill, null, terrain);
        dc.Pop();
        dc.DrawGeometry(null, TerrainPen, terrain);
        dc.DrawGeometry(null, RoadPen, road);

        foreach (var c in result.Crossings)
        {
            dc.DrawLine(c.NeedsWork ? WarnPen : OkPen, new Point(X(c.Dist), Top), new Point(X(c.Dist), Top + h));
            var t = Text(c.KindName, 10, c.NeedsWork ? Brushes.Firebrick : Brushes.ForestGreen);
            dc.DrawText(t, new Point(X(c.Dist) - t.Width / 2, Top - t.Height - 1));
        }

        if (hover is double hd)
        {
            int i = result.IndexAt(hd);
            dc.DrawLine(HoverPen, new Point(X(path[i].Dist), Top), new Point(X(path[i].Dist), Top + h));
            int a = result.IndexAt(path[i].Dist - 10), b = result.IndexAt(path[i].Dist + 10);
            double grade = b > a ? Math.Abs(result.Profile[b] - result.Profile[a]) / (path[b].Dist - path[a].Dist) * 10 : 0;
            double diff = result.Profile[i] - result.Terrain[i];
            string what = diff < -0.05 ? Loc.F("cut {0:0.0} m", -diff) : diff > 0.05 ? Loc.F("fill {0:0.0} m", diff) : Loc.T("at terrain height");
            string s = result.IsRemoval
                ? Loc.F("{0:#,0} m:  terrain now {1:0.0} m · afterwards {2:0.0} m ({3:+0.0;-0.0;0} m)", path[i].Dist, result.Terrain[i], result.Profile[i], diff)
                : Loc.F("{0:#,0} m:  road {1:0.0} m · terrain {2:0.0} m · {3} · grade {4:0.0} m per 10 m",
                    path[i].Dist, result.Profile[i], result.Terrain[i], what, grade);
            var t = Text(s, 12, Brushes.Black);
            double x = Math.Min(Math.Max(Left, X(path[i].Dist) + 8), Left + w - t.Width - 6);
            dc.DrawRoundedRectangle(Brushes.White, HoverPen, new Rect(x - 4, Top + 2, t.Width + 8, t.Height + 4), 3, 3);
            dc.DrawText(t, new Point(x, Top + 4));
        }
    }

    // "nice" step size: 1, 2, 5 × 10^k
    static double Nice(double raw)
    {
        if (raw <= 0) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(raw))), f = raw / p;
        return (f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10) * p;
    }

    FormattedText Text(string s, double size, Brush brush) =>
        new(s, Num, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (result == null) return;
        double d = Math.Clamp((e.GetPosition(this).X - Left) / xs, 0, result.Length);
        hover = d;
        InvalidateVisual();
        HoverChanged?.Invoke(d);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        hover = null;
        InvalidateVisual();
        HoverChanged?.Invoke(null);
    }
}
