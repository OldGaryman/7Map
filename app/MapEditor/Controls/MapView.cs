using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MapEditor.Map;

namespace MapEditor.Controls;

/// Map area: shows the images of the world, buildings, spawn points and the planned road.
/// Mouse wheel = zoom to the mouse position, drag with the left mouse button = pan.
/// In edit mode: click = append point, click on the road = insert point, drag point = move, right click on a point = delete.
public sealed class MapView : FrameworkElement
{
    WorldData world;
    MapLayers layers;
    double scale = 0.1;          // screen pixels per meter
    double ox, oy;               // screen position of the northwest corner of the map (x = -Half, z = +Half)
    Point dragStart;
    double dragOx, dragOy;
    bool dragging, moved, fitPending;
    PlacedPrefab hovered;
    readonly List<int> exitWidths = new();   // width of the connection per entry in exits (m, 0 = unknown)
    readonly List<(Point P, PlacedPrefab Owner)> exits = new();   // entrances/exits of all buildings (world coordinates)

    // planned road
    readonly List<Point> roadPoints = new();   // control points in world coordinates (X = x, Y = z)
    RoadResult result;
    bool editMode;
    int dragHandle = -1, hoverHandle = -1;
    readonly List<double?> pointHeights = new();   // height set by hand per control point (null = automatic)
    int selectedPoint = -1;
    Point handleDownAt;
    bool handleMoved;
    double? profileMarker;

    /// heights of the control points set by hand (same order as RoadPoints)
    public IReadOnlyList<double?> PointHeights => pointHeights;
    readonly List<double?> pointCross = new();   // edit road: desired banking per marker (fraction, null = stays)
    public IReadOnlyList<double?> PointCross => pointCross;
    /// roadway width set by hand per control point (null = default width or width of the tile connection); in between the road tapers
    readonly List<double?> pointWidths = new();
    public IReadOnlyList<double?> PointWidths => pointWidths;

    public void SetPointWidth(int i, double? w)
    {
        if (i < 0 || i >= pointWidths.Count) return;
        pointWidths[i] = w;
        InvalidateVisual();
        RoadPointsChanged?.Invoke(true);
    }

    public void SetPointCross(int i, double? c)
    {
        if (i < 0 || i >= pointCross.Count) return;
        pointCross[i] = c;
        InvalidateVisual();
        RoadPointsChanged?.Invoke(true);
    }
    /// chosen control point (click without dragging), -1 = none
    public int SelectedPoint
    {
        get => selectedPoint;
        set { if (selectedPoint == value) return; selectedPoint = value; InvalidateVisual(); SelectedPointChanged?.Invoke(); }
    }
    public event Action SelectedPointChanged;

    public void SetPointHeight(int i, double? h)
    {
        if (i < 0 || i >= pointHeights.Count) return;
        pointHeights[i] = h;
        InvalidateVisual();
        RoadPointsChanged?.Invoke(true);
    }

    bool showBiomes = true, showWater = true, showRoads = true, showPrefabs = true, showParts;
    /// show parts (part_*: driveways, lamps, transitions …) at every zoom level; otherwise not at all
    public bool ShowParts { get => showParts; set { showParts = value; InvalidateVisual(); } }
    public bool ShowBiomes { get => showBiomes; set { showBiomes = value; InvalidateVisual(); } }
    public bool ShowWater { get => showWater; set { showWater = value; InvalidateVisual(); } }
    public bool ShowRoads { get => showRoads; set { showRoads = value; InvalidateVisual(); } }
    public bool ShowPrefabs { get => showPrefabs; set { showPrefabs = value; InvalidateVisual(); } }

    /// reports the world position under the mouse (null = outside the map)
    public event Action<MapPointer> PointerMoved;
    /// control points changed; true = change finished (mouse released), false = point is being dragged
    public event Action<bool> RoadPointsChanged;

    /// edit road: moves a click onto the center of the existing road (null = no road there, no point)
    public Func<Point, Point?> MarkerSnap { get; set; }
    /// edit road: number at which a new point is inserted (number of points = append at the end)
    public Func<Point, int> InsertIndex { get; set; }
    /// click without a road beneath (edit road)
    public event Action MarkerRejected;

    /// choose road: clicks are only reported (MapClicked), not set as control points
    public bool PickMode { get => pickMode; set { pickMode = value; if (!value) PickStart = null; InvalidateVisual(); } }
    bool pickMode;
    /// already chosen start point when choosing a road (green point)
    public Point? PickStart { get => pickStart; set { pickStart = value; InvalidateVisual(); } }
    Point? pickStart;
    public event Action<Point> MapClicked;

    /// eraser: drag with the left mouse button = erase (stroke is collected and reported on release), drag with the right mouse button = pan
    public bool EraseMode { get => eraseMode; set { eraseMode = value; stroke.Clear(); area.Clear(); areaDrag = areaHover = -1; erasing = false; InvalidateVisual(); } }
    bool eraseMode, erasing, panRight;
    /// brush sets only one stamp (mountain): the stroke consists of one point that moves along when dragging
    public bool SingleStamp { get; set; }
    /// draw an area freehand: the stroke is the outline of an area (shown closed and filled)
    public bool Lasso { get; set; }
    /// area is an open line (river): not closed, AreaWidth (m) shows the width
    public bool OpenArea { get; set; }
    public double AreaWidth { get; set; } = 10;

    // drawn area (mountain range): stays as a polygon with points after drawing and can be edited
    // before calculating: drag point = move, click on the edge = insert point, right click on a point = delete
    readonly List<Point> area = new();
    int areaDrag = -1, areaHover = -1;
    public IReadOnlyList<Point> Area => area;
    /// replace the area with new points (e.g. river path → shoreline)
    public void SetArea(IEnumerable<Point> pts)
    {
        area.Clear();
        area.AddRange(pts);
        areaDrag = areaHover = -1;
        InvalidateVisual();
        AreaChanged?.Invoke();
    }
    /// the area has changed (redrawn, point moved/inserted/deleted, deleted)
    public event Action AreaChanged;
    public void ClearArea()
    {
        if (area.Count == 0) return;
        area.Clear();
        areaDrag = areaHover = -1;
        InvalidateVisual();
        AreaChanged?.Invoke();
    }

    int HitArea(Point screen)
    {
        for (int i = area.Count - 1; i >= 0; i--)
            if ((ToScreen(area[i].X, area[i].Y) - screen).Length <= 9) return i;
        return -1;
    }

    // segment of the polygon (index of the start point) under the mouse pointer and the foot point in world coordinates; -1 = none
    int HitAreaEdge(Point screen, out Point foot)
    {
        foot = default;
        int best = -1;
        double bestD = 8;
        for (int i = 0; i < (OpenArea ? area.Count - 1 : area.Count); i++)
        {
            var a = ToScreen(area[i].X, area[i].Y);
            var b = ToScreen(area[(i + 1) % area.Count].X, area[(i + 1) % area.Count].Y);
            var ab = b - a;
            double len2 = ab.LengthSquared;
            double t = len2 > 0 ? Math.Clamp(((screen - a) * ab) / len2, 0, 1) : 0;
            var q = a + ab * t;
            double d = (screen - q).Length;
            if (d < bestD) { bestD = d; best = i; foot = new Point(ToWorldX(q.X), ToWorldZ(q.Y)); }
        }
        return best;
    }

    // reduce a hand stroke to a few points (Douglas-Peucker; tolerance 7 screen pixels, at most about 60 points)
    static List<Point> SimplifyOutline(List<Point> pts, double tol)
    {
        List<Point> Run(double t)
        {
            var keep = new bool[pts.Count];
            keep[0] = keep[^1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, pts.Count - 1));
            while (stack.Count > 0)
            {
                var (i0, i1) = stack.Pop();
                double far = 0;
                int fi = -1;
                var a = pts[i0];
                var ab = pts[i1] - a;
                double len2 = ab.LengthSquared;
                for (int i = i0 + 1; i < i1; i++)
                {
                    double tt = len2 > 0 ? Math.Clamp(((pts[i] - a) * ab) / len2, 0, 1) : 0;
                    double d = (pts[i] - (a + ab * tt)).Length;
                    if (d > far) { far = d; fi = i; }
                }
                if (fi >= 0 && far > t) { keep[fi] = true; stack.Push((i0, fi)); stack.Push((fi, i1)); }
            }
            var res = new List<Point>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) res.Add(pts[i]);
            return res;
        }
        var r = Run(tol);
        for (int k = 0; k < 6 && r.Count > 60; k++) r = Run(tol *= 1.6);
        return r;
    }
    /// radius of the brush (m)
    public double BrushRadius { get => brushRadius; set { brushRadius = value; InvalidateVisual(); } }
    double brushRadius = 8;
    /// square brush (square), otherwise round
    public bool BrushSquare { get => brushSquare; set { brushSquare = value; InvalidateVisual(); } }
    bool brushSquare;
    /// further brush shapes (terrain brush); Round/Square apply as before via BrushSquare
    public BrushShape Shape { get => shape; set { shape = value; shapeSpec = null; InvalidateVisual(); } }
    BrushShape shape;
    ShapeSpec shapeSpec;
    /// random number of the shape of the next stroke (random shape: a new one on every left click) and that of the running stroke
    public int StrokeSeed { get; private set; }
    int nextSeed = Random.Shared.Next();
    bool CustomShape => shape is not (BrushShape.Round or BrushShape.Square);
    /// color of the stroke when painting (biome brush: color of the biome); null = red of the eraser
    public Brush StrokeFill { get => strokeFill; set { strokeFill = value; InvalidateVisual(); } }
    Brush strokeFill;
    /// as long as true (calculation running), the eraser accepts no new stroke
    public bool Busy { get; set; }
    readonly List<Point> stroke = new();
    Point? brushAt;   // world position of the brush under the mouse
    public event Action<List<Point>> StrokeFinished;

    /// building chosen in the building list (highlighted blue)
    public PlacedPrefab Selected { get => selected; set { selected = value; InvalidateVisual(); } }
    PlacedPrefab selected;
    /// click (without dragging) on a building when no tool is active
    public event Action<PlacedPrefab> PrefabClicked;
    /// click (without dragging) on a spawn point when no tool is active (takes precedence over buildings)
    public event Action<SpawnPoint> SpawnClicked;

    // spawn point under the mouse (circle about 9 pixels, the test point lies on top)
    SpawnPoint HitSpawn(Point p) => world?.Spawns.OrderByDescending(s => s.Test)
        .FirstOrDefault(s => (ToScreen(s.X, s.Z) - p).Length <= 9);

    public IReadOnlyList<Point> RoadPoints => roadPoints;
    public bool EditMode { get => editMode; set { editMode = value; hoverHandle = -1; InvalidateVisual(); } }
    public RoadResult Result { get => result; set { result = value; InvalidateVisual(); } }
    /// place of the road (distance from the start) that is under the mouse in the height profile
    public double? ProfileMarker { get => profileMarker; set { profileMarker = value; InvalidateVisual(); } }

    static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE4)));
    static readonly Pen TilePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(200, 40, 80, 200)), 1));
    static readonly Pen PoiPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(220, 190, 40, 30)), 1));
    static readonly Brush PoiFill = Frozen(new SolidColorBrush(Color.FromArgb(40, 190, 40, 30)));
    static readonly Pen PartPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(230, 130, 40, 190)), 1));
    static readonly Brush PartFill = Frozen(new SolidColorBrush(Color.FromArgb(70, 150, 60, 210)));
    static readonly Pen HoverPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(255, 200, 0)), 2));
    static readonly Brush HoverFill = Frozen(new SolidColorBrush(Color.FromArgb(70, 255, 200, 0)));
    static readonly Brush SpawnFill = Frozen(new SolidColorBrush(Color.FromRgb(255, 255, 255)));
    static readonly Pen SpawnPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(216, 90, 48)), 3));
    static readonly Pen TestSpawnPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(83, 74, 183)), 3));
    static readonly Pen SlopePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(220, 120, 80, 30)), 1.2) { DashStyle = DashStyles.Dash });
    static readonly Brush AsphaltBrush = Frozen(new SolidColorBrush(Color.FromArgb(215, 205, 85, 30)));
    static readonly Brush GravelBrush = Frozen(new SolidColorBrush(Color.FromArgb(225, 230, 160, 40)));
    static readonly Brush GrassEdgeBrush = Frozen(new SolidColorBrush(Color.FromArgb(150, 120, 200, 90)));   // grass edge (terrain only, no surface)
    static readonly Brush RemoveBrush = Frozen(new SolidColorBrush(Color.FromArgb(110, 200, 0, 60)));
    static readonly Pen HandlePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(30, 30, 30)), 1.5));
    static readonly Brush StartFill = Frozen(new SolidColorBrush(Color.FromRgb(90, 190, 90)));
    static readonly Brush EndFill = Frozen(new SolidColorBrush(Color.FromRgb(225, 80, 70)));
    static readonly Pen WarnPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(220, 30, 30)), 3));
    static readonly Pen OkPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(40, 160, 60)), 3));
    static readonly Pen ObstaclePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(230, 0, 0)), 3));
    static readonly Pen MarkerPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 255)), 3));
    static readonly Pen SelectedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 110, 230)), 3));
    static readonly Brush SelectedFill = Frozen(new SolidColorBrush(Color.FromArgb(60, 0, 110, 230)));
    static readonly Brush ExitFill =Frozen(new SolidColorBrush(Color.FromRgb(0, 200, 220)));
    static readonly Pen BrushPen =Frozen(new Pen(new SolidColorBrush(Color.FromRgb(200, 0, 60)), 1.5));

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetWorld(WorldData w, MapLayers l)
    {
        world = w; layers = l; hovered = null; selected = null; selectedSpawn = null; placingSpawn = null;
        roadPoints.Clear(); pointHeights.Clear(); pointCross.Clear(); pointWidths.Clear(); selectedPoint = -1; result = null;
        ghost = null;
        movable = null; draggingPrefab = false; pendDX = pendDZ = 0;   // keep nothing from the previous world (otherwise its tile connections would remain)
        RebuildExits();
        FitAll();
    }

    /// start or end point near an entrance/exit (about 8 m, somewhat more when zoomed out) snaps there.
    /// with tiles 1 m inwards so that the road takes over the height of the tile.
    Point SnapToExit(Point w)
    {
        double tol = Math.Clamp(14 / scale, 8, 30), best = tol;
        Point? hit = null;
        foreach (var (p, owner) in exits)
        {
            double d = (p - w).Length;
            if (d > best) continue;
            best = d;
            hit = owner.IsTile ? new Point(Math.Clamp(p.X, owner.X + 1, owner.X + owner.SizeX - 1), Math.Clamp(p.Y, owner.Z + 1, owner.Z + owner.SizeZ - 1)) : p;
        }
        return hit ?? w;
    }

    bool OnExit(Point w) => exits.Any(e => (e.P - w).Length <= 1.5);

    // entrances/exits: small diamonds, from medium zoom and only when editing a road
    void DrawExits(DrawingContext dc)
    {
        if (!editMode || scale < 0.15) return;
        double x0 = ToWorldX(0), x1 = ToWorldX(ActualWidth), z1 = ToWorldZ(0), z0 = ToWorldZ(ActualHeight), r = 6;
        for (int i = 0; i < exits.Count; i++)
        {
            var (p, owner) = exits[i];
            if (p.X < x0 || p.X > x1 || p.Y < z0 || p.Y > z1) continue;
            var c = ToScreen(p.X, p.Y);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X, c.Y - r), true, true);
                ctx.PolyLineTo(new[] { new Point(c.X + r, c.Y), new Point(c.X, c.Y + r), new Point(c.X - r, c.Y) }, true, true);
            }
            dc.DrawGeometry(ExitFill, HandlePen, g);
            if (scale >= 0.4 && i < exitWidths.Count && exitWidths[i] > 0) DrawWidthLabel(dc, c, Loc.F("{0} m", exitWidths[i]));
        }
    }

    /// width of the road at a tile exit as text (e.g. "8 m"), null = unknown
    string ExitWidthText(PlacedPrefab tile, double x, double z)
    {
        int width = world.TileExitWidth(tile, x, z).Width;
        return width > 0 ? Loc.F("{0} m", width) : null;
    }

    /// road width of the connection (tile exit, driveway, entrance of a building) that lies at most 2.5 m beside the point
    /// (start/end of a new road); null = none
    public (int Width, byte Surface)? ExitNear(Point w)
    {
        if (world == null) return null;
        (int Width, byte Surface)? best = null;
        double bestScore = double.MaxValue;
        for (int i = 0; i < exits.Count && i < exitWidths.Count; i++)
        {
            var (p, owner) = exits[i];
            double dist = (p - w).Length;
            if (dist > 2.5 || exitWidths[i] <= 0) continue;
            // at the shared edge of two tiles two exits lie at the same point: the tile in which the point lies counts
            double score = dist + (owner.Contains(w.X, w.Y) ? 0 : 10);
            if (score >= bestScore) continue;
            bestScore = score;
            best = (exitWidths[i], 0);
        }
        return best;
    }
    // number in a white box beside a point (road width at the connection)
    void DrawWidthLabel(DrawingContext dc, Point c, string s)
    {
        var text = Text(s, 11, FontWeights.SemiBold);
        var box = new Rect(c.X + 9, c.Y - text.Height / 2 - 1, text.Width + 6, text.Height + 2);
        dc.DrawRoundedRectangle(LabelBack, ExitRing, box, 3, 3);
        dc.DrawText(text, new Point(box.X + 3, box.Y + 1));
    }

    // ---------- road connections of the city tiles (the user's request 2026-11-03) ----------
    // rule checked on the original world (489 adjoining tile pairs): at a shared edge either both tiles have
    // a road exit (449) or none (40), never only one (0). The districts of the neighbors, on the other hand, are mixed.

    readonly Dictionary<(string, int), List<(double X, double Z)>> tileExitCache = new();
    WorldData tileExitWorld;
    static readonly Brush ExitOk = Frozen(new SolidColorBrush(Color.FromRgb(30, 170, 70)));
    static readonly Brush ExitBad = Frozen(new SolidColorBrush(Color.FromRgb(220, 40, 40)));
    static readonly Brush ExitOpen = Frozen(new SolidColorBrush(Color.FromRgb(245, 170, 20)));
    static readonly Pen ExitRing = Frozen(new Pen(Brushes.White, 2));

    // exits of a tile relative to its southwest corner (calculated once per name and rotation)
    List<(double X, double Z)> LocalTileExits(PlacedPrefab t)
    {
        if (tileExitWorld != world) { tileExitCache.Clear(); tileExitWorld = world; }
        var key = (t.Name, ((t.Rotation % 4) + 4) % 4);
        if (!tileExitCache.TryGetValue(key, out var list))
        {
            var probe = new PlacedPrefab { Name = t.Name, Def = t.Def, X = 0, Z = 0, Rotation = t.Rotation };
            world.TileSurface(probe, out var ex);
            tileExitCache[key] = list = ex ?? new List<(double X, double Z)>();
        }
        return list;
    }

    // tile whose connections are shown: the one at the mouse pointer, the one just being moved or the chosen one
    PlacedPrefab TileFocus()
    {
        if (ghost != null && ghost.IsTile) return ghost;
        if (movable != null && movable.IsTile)
        {
            var (dx, dz) = MoveOffset;
            return dx == 0 && dz == 0 ? movable : new PlacedPrefab { Name = movable.Name, Def = movable.Def, Rotation = movable.Rotation, X = movable.X + dx, Z = movable.Z + dz };
        }
        return selected != null && selected.IsTile ? selected : null;
    }

    /// road exits of the tile in focus and its neighbors: green = meets an exit of the neighboring tile, red = neighboring tile
    /// without a matching exit (or vice versa), orange = open end without a neighboring tile
    void DrawTileRoads(DrawingContext dc)
    {
        var f = TileFocus();
        if (f?.Def == null || world == null) return;
        var old = movable != null && movable != f ? movable : null;   // the previous position of the tile just being dragged does not count
        var near = world.Prefabs.Where(p => p != f && p != old && p.IsTile && p.Def != null
            && p.X < f.X + f.SizeX + 160 && p.X + p.SizeX > f.X - 160 && p.Z < f.Z + f.SizeZ + 160 && p.Z + p.SizeZ > f.Z - 160).ToList();
        var mine = LocalTileExits(f).Select(e => (X: f.X + e.X, Z: f.Z + e.Z)).ToList();
        var theirs = world.TileExits.Where(e => near.Contains(e.Tile)).ToList();
        const double tol = 4;
        int ok = 0, bad = 0, open = 0;
        var dots = new List<(Point P, Brush B, string Label)>();
        foreach (var (x, z) in mine)
        {
            // side on which the exit lies, and a point just behind it
            double px = x, pz = z;
            if (Math.Abs(x - f.X) < 0.5) px -= 1; else if (Math.Abs(x - (f.X + f.SizeX)) < 0.5) px += 1;
            else if (Math.Abs(z - f.Z) < 0.5) pz -= 1; else pz += 1;
            var n = near.FirstOrDefault(t => t.Contains(px, pz));
            bool match = n != null && theirs.Any(e => e.Tile == n && Math.Abs(e.X - x) <= tol && Math.Abs(e.Z - z) <= tol);
            Brush b = n == null ? ExitOpen : match ? ExitOk : ExitBad;
            if (n == null) open++; else if (match) ok++; else bad++;
            dots.Add((ToScreen(x, z), b, ExitWidthText(f, x, z)));
        }
        foreach (var e in theirs)
        {
            bool touches = f.Contains(e.X + 1, e.Z) || f.Contains(e.X - 1, e.Z) || f.Contains(e.X, e.Z + 1) || f.Contains(e.X, e.Z - 1);
            bool match = mine.Any(m => Math.Abs(m.X - e.X) <= tol && Math.Abs(m.Z - e.Z) <= tol);
            if (touches && !match) bad++;
            dots.Add((ToScreen(e.X, e.Z), touches ? (match ? ExitOk : ExitBad) : ExitOpen, ExitWidthText(e.Tile, e.X, e.Z)));
        }
        foreach (var (p, b, label) in dots)
        {
            if (p.X <= -20 || p.Y <= -20 || p.X >= ActualWidth + 20 || p.Y >= ActualHeight + 20) continue;
            dc.DrawEllipse(b, ExitRing, p, 7, 7);
            if (label != null) DrawWidthLabel(dc, p, label);
        }
        // summary above the tile
        var r = PrefabRect(f);
        string s = mine.Count == 0 ? Loc.T("Roads: this tile has no road exits") : Loc.F("Roads: {0} connected · {1} not matching · {2} open", ok, bad, open);
        var text = Text(s, 12, FontWeights.SemiBold);
        double top = r.Top - text.Height - 30;
        dc.DrawRoundedRectangle(Brushes.White, bad > 0 ? new Pen(ExitBad, 2) : new Pen(ExitOk, 2), new Rect(r.Left, top, text.Width + 8, text.Height + 4), 3, 3);
        dc.DrawText(text, new Point(r.Left + 4, top + 2));
    }

    /// release the world and images (before reloading, so that memory is freed)
    public void Clear()
    {
        world = null; layers = null; hovered = null; result = null; selected = null; selectedSpawn = null; placingSpawn = null;
        roadPoints.Clear(); pointHeights.Clear(); pointCross.Clear(); pointWidths.Clear(); selectedPoint = -1;
        area.Clear(); areaDrag = areaHover = -1;
        ghost = null; movable = null; draggingPrefab = false; pendDX = pendDZ = 0;
        InvalidateVisual();
    }

    /// redraw the area x0..x1, z0..z1 (meters) after a change
    /// delete the shown eraser stroke (after the calculation)
    public void ClearStroke()
    {
        stroke.Clear();
        shapeSpec = null;   // the mouse pointer shows the shape of the next stroke again
        InvalidateVisual();
    }

    public void RefreshArea(int x0, int z0, int x1, int z1)
    {
        if (layers == null) return;
        layers.Update(world, x0, z0, x1, z1);
        InvalidateVisual();
    }

    /// new images after a change of the world, the section stays
    public void UpdateLayers(MapLayers l)
    {
        layers = l;
        InvalidateVisual();
    }

    public void SetRoadPoints(IEnumerable<Point> pts)
    {
        roadPoints.Clear();
        roadPoints.AddRange(pts);
        pointHeights.Clear();
        pointHeights.AddRange(roadPoints.Select(_ => (double?)null));
        pointCross.Clear(); pointWidths.Clear();
        pointCross.AddRange(roadPoints.Select(_ => (double?)null));
        pointWidths.AddRange(roadPoints.Select(_ => (double?)null));
        SelectedPoint = -1;
        InvalidateVisual();
    }

    public void FitAll()
    {
        if (world == null) return;
        if (ActualWidth == 0) { fitPending = true; return; }
        scale = Math.Min(ActualWidth, ActualHeight) / world.Size;
        ox = (ActualWidth - world.Size * scale) / 2;
        oy = (ActualHeight - world.Size * scale) / 2;
        UpdateScalingMode();
        InvalidateVisual();
    }

    /// shows the area x0..x1, z0..z1 (world coordinates) as large as possible
    public void ShowArea(double x0, double z0, double x1, double z1, double margin = 60)
    {
        if (world == null || ActualWidth == 0) return;
        double w = Math.Max(x1 - x0, 1) + 2 * margin, h = Math.Max(z1 - z0, 1) + 2 * margin;
        scale = Math.Clamp(Math.Min(ActualWidth / w, ActualHeight / h), Math.Min(ActualWidth, ActualHeight) / world.Size / 2, MaxScale);
        CenterOn((x0 + x1) / 2, (z0 + z1) / 2);
    }

    public void CenterOn(double x, double z, double minScale = 0)
    {
        if (world == null) return;
        scale = Math.Max(scale, minScale);
        ox = ActualWidth / 2 - (x + world.Half) * scale;
        oy = ActualHeight / 2 - (world.Half - z) * scale;
        UpdateScalingMode();
        InvalidateVisual();
    }

    public double ToScreenX(double x) => ox + (x + world.Half) * scale;
    public double ToScreenY(double z) => oy + (world.Half - z) * scale;
    public double ToWorldX(double sx) => (sx - ox) / scale - world.Half;
    public double ToWorldZ(double sy) => world.Half - (sy - oy) / scale;
    Point ToScreen(double x, double z) => new(ToScreenX(x), ToScreenY(z));

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (fitPending) { fitPending = false; FitAll(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(ThemeManager.MapBack, null, new Rect(RenderSize));   // also necessary so that the area receives mouse events
        if (world == null) return;
        var rect = new Rect(ox, oy, world.Size * scale, world.Size * scale);
        // image level: the copy that has to be shrunk at most 2 × (MapLayers, reduced copies)
        int level = 0;
        while (level < MapLayers.ReducedLevels && scale * MapLayers.MetersPerPixel * (1 << level) < 0.5) level++;
        dc.DrawImage(layers.Image(showBiomes ? 0 : 1, level), rect);
        if (showWater) dc.DrawImage(layers.Image(2, level), rect);
        if (showRoads) dc.DrawImage(layers.Image(3, level), rect);
        if (showPrefabs || showParts) DrawPrefabs(dc);
        DrawFreeLots(dc);
        if (selected != null)
        {
            var r = PrefabRect(selected);
            if (r.Width < 8) r.Inflate(4, 4);
            dc.DrawRectangle(SelectedFill, SelectedPen, r);
            DrawMoveHandle(dc);
        }
        DrawGhost(dc);
        DrawTileRoads(dc);
        DrawSpawns(dc);
        DrawExits(dc);
        DrawChunkMarks(dc);
        DrawNetwork(dc);
        DrawWilderness(dc);
        DrawRoad(dc);
        DrawBrush(dc);
        DrawHover(dc);
    }

    void DrawPrefabs(DrawingContext dc)
    {
        double x0 = ToWorldX(0), x1 = ToWorldX(ActualWidth), z1 = ToWorldZ(0), z0 = ToWorldZ(ActualHeight);
        // Small rectangles without anything inside (buildings and parts narrower than 6 pixels: no surface, arrow or label) are
        // collected into one shape per kind and drawn with one command (the user's request 2026-11-12: smoother at low zoom; measured:
        // about 2,700 single rectangles cost the graphics card so much that every second picture was dropped, 48 instead of 60 per second).
        // Tiles (only about 150–400) stay single in their order of prefabs.xml, so frames lie over or under buildings as before.
        StreamGeometry poiGeo = null, partGeo = null;
        StreamGeometryContext poiCtx = null, partCtx = null;
        static void AddRect(ref StreamGeometry g, ref StreamGeometryContext c, Rect r)
        {
            if (c == null) { g = new StreamGeometry { FillRule = FillRule.Nonzero }; c = g.Open(); }
            c.BeginFigure(r.TopLeft, true, true);
            c.PolyLineTo(new[] { r.TopRight, r.BottomRight, r.BottomLeft }, true, false);
        }
        var single = new List<(PlacedPrefab P, Rect R, Pen Pen, Brush Fill, int Pass)>();
        for (int pass = 0; pass < 2; pass++)
            foreach (var p in world.Prefabs)
            {
                bool part = p.Def.Kind == PrefabKind.Part;
                if (part != (pass == 1) || !IsShown(p)) continue;
                if (p.X > x1 || p.X + p.SizeX < x0 || p.Z > z1 || p.Z + p.SizeZ < z0) continue;
                Pen pen; Brush fill = null;
                switch (p.Def.Kind)
                {
                    case PrefabKind.Tile: if (scale < 0.04) continue; pen = TilePen; break;
                    case PrefabKind.Part: pen = PartPen; fill = PartFill; break;
                    default: pen = PoiPen; fill = PoiFill; break;
                }
                var r = PrefabRect(p);
                if (r.Width < 2 && r.Height < 2) r.Inflate(1, 1);
                if (pen == TilePen) single.Add((p, r, pen, fill, pass));
                else if (r.Width < 6 && part) AddRect(ref partGeo, ref partCtx, r);
                else if (r.Width < 6) AddRect(ref poiGeo, ref poiCtx, r);
                else single.Add((p, r, pen, fill, pass));
            }
        void Batch(StreamGeometry g, StreamGeometryContext c, Brush fill, Pen pen)
        {
            if (g == null) return;
            c.Close();
            g.Freeze();
            dc.DrawGeometry(fill, pen, g);
        }
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var (p, r, pen, fill, ps) in single)
            {
                if (ps != pass) continue;
                if (showRoads && pen != TilePen && r.Width >= 6 && SurfaceOf(p) is BitmapSource surface) dc.DrawImage(surface, r);
                dc.DrawRectangle(fill, pen, r);
                if (pen == PoiPen) DrawFrontArrow(dc, p, r);
                if (pen == PoiPen && scale >= LabelScale) DrawLabel(dc, p, r);
            }
            if (pass == 0) Batch(poiGeo, poiCtx, PoiFill, PoiPen);
            else Batch(partGeo, partCtx, PartFill, PartPen);
        }
    }

    static readonly Pen ArrowPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(210, 150, 25, 15)), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    // arrow from the center to the front (entrance) of the building (the user's request: direction visible); only if the rectangle is large enough
    // and the direction is known (RotationToFaceNorth). Screen: north up.
    static void DrawFrontArrow(DrawingContext dc, PlacedPrefab p, Rect r)
    {
        if (p.FrontDirection is not int dir || Math.Min(r.Width, r.Height) < 16) return;
        var (dx, dy) = dir switch { 0 => (0.0, 1.0), 1 => (1.0, 0.0), 2 => (0.0, -1.0), _ => (-1.0, 0.0) };
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        double len = (dx != 0 ? r.Width : r.Height) / 2 * 0.8, head = Math.Clamp(len * 0.3, 4, 12);
        var tail = new Point(c.X - dx * len * 0.3, c.Y - dy * len * 0.3);
        var tip = new Point(c.X + dx * len, c.Y + dy * len);
        dc.DrawLine(ArrowPen, tail, tip);
        dc.DrawLine(ArrowPen, tip, new Point(tip.X - dx * head + dy * head * 0.7, tip.Y - dy * head + dx * head * 0.7));
        dc.DrawLine(ArrowPen, tip, new Point(tip.X - dx * head - dy * head * 0.7, tip.Y - dy * head - dx * head * 0.7));
    }

    /// driveways/parts that would be added at this position when placing (PrefabParts.For); set by the main window
    public Func<PlacedPrefab, List<PlacedPrefab>> PartsFor { get; set; }

    // driveways in the outline when placing and moving (the user's request): surface and purple frame like the parts of the map
    void DrawPreviewParts(DrawingContext dc, PlacedPrefab p)
    {
        if (PartsFor == null) return;
        foreach (var q in PartsFor(p))
        {
            var r = PrefabRect(q);
            if (SurfaceOf(q) is BitmapSource surface) dc.DrawImage(surface, r);
            dc.DrawRectangle(PartFill, PartPen, r);
        }
    }

    /// preview "Apply to save game": changed areas (16 × 16 m); red = saved in the save game and will be reset
    public SaveDiff ChunkMarks { get => chunkMarks; set { chunkMarks = value; InvalidateVisual(); } }
    SaveDiff chunkMarks;
    static readonly Brush StoredChunkFill = Frozen(new SolidColorBrush(Color.FromArgb(120, 220, 30, 30)));
    static readonly Brush ChangedChunkFill = Frozen(new SolidColorBrush(Color.FromArgb(70, 240, 150, 30)));

    void DrawChunkMarks(DrawingContext dc)
    {
        if (chunkMarks == null) return;
        double x0 = ToWorldX(0), x1 = ToWorldX(ActualWidth), z1 = ToWorldZ(0), z0 = ToWorldZ(ActualHeight), size = Math.Max(1, 16 * scale);
        foreach (long k in chunkMarks.Chunks)
        {
            var (cx, cz) = SaveDiff.FromKey(k);
            if (cx * 16 > x1 || cx * 16 + 16 < x0 || cz * 16 > z1 || cz * 16 + 16 < z0) continue;
            var p = ToScreen(cx * 16, cz * 16 + 16);
            dc.DrawRectangle(chunkMarks.StoredChunks.Contains(k) ? StoredChunkFill : ChangedChunkFill, null, new Rect(p.X, p.Y, size, size));
        }
    }

    /// preview "Create roads" (stage 1, changes nothing): planned roads in roadway width (asphalt dark, cross connections
    /// purple, gravel paths brown, clearings with a red center line), junctions yellow, intersections magenta, free exits orange
    public NetworkPlan NetworkPreview { get => networkPreview; set { networkPreview = value; InvalidateVisual(); } }
    NetworkPlan networkPreview;
    static readonly Brush NetAsphalt = Frozen(new SolidColorBrush(Color.FromArgb(210, 35, 35, 45)));
    static readonly Brush NetLoop = Frozen(new SolidColorBrush(Color.FromArgb(210, 125, 45, 175)));
    static readonly Brush NetGravel = Frozen(new SolidColorBrush(Color.FromArgb(220, 160, 105, 45)));
    static readonly Pen NetCutPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(230, 50, 40)), 2) { DashStyle = DashStyles.Dash });
    static readonly Pen NetJunctionPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(250, 205, 0)), 3));
    static readonly Pen NetCrossPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(230, 0, 200)), 3));
    static readonly Brush NetFreeExit = Frozen(new SolidColorBrush(Color.FromRgb(245, 140, 0)));

    /// preview "Create wilderness POIs" (changes nothing): new buildings outlined in green, gravel paths brown in path width
    public WildernessPlan WildernessPreview { get => wildernessPreview; set { wildernessPreview = value; InvalidateVisual(); } }
    WildernessPlan wildernessPreview;
    static readonly Brush WildFill = Frozen(new SolidColorBrush(Color.FromArgb(70, 60, 200, 80)));
    static readonly Pen WildPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(30, 150, 50)), 2));

    void DrawWilderness(DrawingContext dc)
    {
        if (wildernessPreview == null) return;
        foreach (var r in wildernessPreview.Paths)
        {
            if (r.Points.Count < 2) continue;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(ToScreen(r.Points[0].X, r.Points[0].Y), false, false);
                ctx.PolyLineTo(r.Points.Skip(1).Select(p => ToScreen(p.X, p.Y)).ToList(), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(NetGravel, Math.Max(2, r.Width * scale)) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }
        foreach (var poi in wildernessPreview.Pois)
        {
            var b = poi.Building;
            var a = ToScreen(b.X, b.Z + b.SizeZ);
            var rect = new Rect(a.X, a.Y, Math.Max(3, b.SizeX * scale), Math.Max(3, b.SizeZ * scale));
            dc.DrawRectangle(WildFill, WildPen, rect);
        }
    }

    void DrawNetwork(DrawingContext dc)
    {
        if (networkPreview == null) return;
        foreach (var r in networkPreview.Roads)
        {
            if (r.Points.Count < 2) continue;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(ToScreen(r.Points[0].X, r.Points[0].Y), false, false);
                ctx.PolyLineTo(r.Points.Skip(1).Select(p => ToScreen(p.X, p.Y)).ToList(), true, true);
            }
            g.Freeze();
            var brush = r.Gravel ? NetGravel : r.Loop ? NetLoop : NetAsphalt;
            dc.DrawGeometry(null, new Pen(brush, Math.Max(3, (r.Width + 2) * scale)) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
            if (r.Cut) dc.DrawGeometry(null, NetCutPen, g);
        }
        double rad = Math.Max(6, 12 * scale);
        foreach (var p in networkPreview.Junctions) dc.DrawEllipse(null, NetJunctionPen, ToScreen(p.X, p.Y), rad, rad);
        foreach (var p in networkPreview.CrossingPoints) dc.DrawEllipse(null, NetCrossPen, ToScreen(p.X, p.Y), rad, rad);
        foreach (var e in networkPreview.Towns.SelectMany(t => t.Exits).Where(e => !e.Used))
            dc.DrawEllipse(e.Reachable ? NetFreeExit : Brushes.Red, HandlePen, ToScreen(e.X, e.Z), 5, 5);
    }

    /// name → prefab XML in the game folder (for the surface from the blocks); set by the main window
    public Func<string, string> PrefabFileOf { get; set; }
    readonly Dictionary<string, (byte[] Grid, int SX, int SZ)> surfaceGrids = new(StringComparer.OrdinalIgnoreCase);   // Grid null = no surface
    readonly Dictionary<(string, int), BitmapSource> surfaceImages = new();
    readonly HashSet<string> surfaceLoading = new(StringComparer.OrdinalIgnoreCase);

    // surface (asphalt/gravel) of a building from its blocks as an image in world orientation (PrefabSurface); read in the
    // background the first time and then redrawn. So it is visible at once after rotating/moving/swapping.
    BitmapSource SurfaceOf(PlacedPrefab p)
    {
        if (PrefabFileOf == null || p.IsTile || p.Def == null) return null;
        var key = (p.Name.ToLowerInvariant(), ((p.Rotation % 4) + 4) % 4);
        if (surfaceImages.TryGetValue(key, out var img)) return img;
        if (surfaceGrids.TryGetValue(p.Name, out var g))
        {
            // only if the block file is as large as the building (otherwise the position is wrong)
            img = g.Grid == null || g.SX != p.Def.SizeX || g.SZ != p.Def.SizeZ ? null : PrefabSurface.Image(g.Grid, g.SX, g.SZ, key.Item2);
            surfaceImages[key] = img;
            return img;
        }
        string file = PrefabFileOf(p.Name), name = p.Name;
        if (file == null) return null;   // list of the game files not loaded yet: once more later
        if (surfaceLoading.Add(name))
        {
            Task.Run(() =>
            {
                int sx = 0, sz = 0;
                byte[] grid = null;
                try { if (file != null) grid = PrefabSurface.Read(file, out sx, out sz); } catch (Exception) { grid = null; }
                return (grid, sx, sz);
            }).ContinueWith(t =>
            {
                surfaceGrids[name] = t.Result;
                surfaceLoading.Remove(name);
                InvalidateVisual();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        return null;
    }

    /// from this zoom level (1 = 100 % = 1 screen pixel per meter) name and internal name stand in the rectangle of the POIs
    const double LabelScale = 0.75;
    static readonly Brush LabelBack = Frozen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)));

    // name (bold) and internal name at the top left in the rectangle, cut off at the edge; only if at least one line fits in
    void DrawLabel(DrawingContext dc, PlacedPrefab p, Rect r)
    {
        if (r.Width < 30 || r.Height < 16) return;
        var title = TitleText(p, 11);
        var name = Caption(p) == p.Name ? null : Text(p.Name, 10);
        double w = Math.Min(Math.Max(title.Width, name?.Width ?? 0) + 6, r.Width - 2);
        double h = title.Height + (name != null && r.Height >= title.Height + name.Height + 6 ? name.Height : 0) + 2;
        dc.PushClip(new RectangleGeometry(new Rect(r.Left + 1, r.Top + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2))));
        dc.DrawRectangle(LabelBack, null, new Rect(r.Left + 1, r.Top + 1, w, h));
        DrawCachedText(dc, title, new Point(r.Left + 4, r.Top + 1));
        if (name != null && h > title.Height + 2) DrawCachedText(dc, name, new Point(r.Left + 4, r.Top + 1 + title.Height));
        dc.Pop();
    }

    // driveways appear with the buildings (the user's hint: otherwise not recognizable), other parts only with "Parts"
    bool IsShown(PlacedPrefab p) => p.Def.Kind == PrefabKind.Part ? showParts || showPrefabs && PrefabParts.IsDriveway(p) : showPrefabs;

    void DrawHover(DrawingContext dc)
    {
        if (hovered == null) return;
        var r = PrefabRect(hovered);
        dc.DrawRectangle(HoverFill, HoverPen, r);
        // always name and internal name (the user's request); without its own name only the internal one
        var title = TitleText(hovered, 12);
        var name = Caption(hovered) == hovered.Name ? null : Text(hovered.Name, 11);
        double h = title.Height + (name?.Height ?? 0);
        var box = new Rect(r.Left, r.Top - h - 6, Math.Max(title.Width, name?.Width ?? 0) + 8, h + 4);
        dc.DrawRoundedRectangle(Brushes.White, HoverPen, box, 3, 3);
        dc.DrawText(title, new Point(box.Left + 4, box.Top + 2));
        if (name != null) dc.DrawText(name, new Point(box.Left + 4, box.Top + 2 + title.Height));
    }

    FormattedText Text(string s, double size) => Text(s, size, FontWeights.Normal);

    // finished texts are kept (finding F28, measured 2026-11-12: at 100 % zoom over a town about 70 labels cost 25 ms per
    // picture, because every text was laid out anew and even the font was created anew; with the cache well under 1 ms).
    // Key = text, size, weight, stars; emptied when the screen scaling (DPI) changes or when there are too many entries.
    static readonly FontFamily LabelFont = new("Segoe UI");
    readonly Dictionary<(string Text, double Size, int Weight, int Stars), FormattedText> textCache = new();
    double textCacheDpi;

    FormattedText Text(string s, double size, FontWeight weight) => CachedText(s, size, weight, 0);

    FormattedText CachedText(string s, double size, FontWeight weight, int stars)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi != textCacheDpi || textCache.Count > 20000) { textCache.Clear(); textDrawings.Clear(); textCacheDpi = dpi; }
        var key = (s, size, weight.ToOpenTypeWeight(), stars);
        if (textCache.TryGetValue(key, out var t)) return t;
        t = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(LabelFont, FontStyles.Normal, weight, FontStretches.Normal),
            size, Brushes.Black, dpi);
        if (stars > 0) t.SetForegroundBrush(StarBrush, s.Length - stars, stars);
        textCache[key] = t;
        return t;
    }

    // DrawText lays out a FormattedText anew on every call (profiler: 96 % of the remaining time); the text drawn once into
    // a frozen drawing (glyphs) is only moved to its place afterwards. Only for texts from the cache above.
    readonly Dictionary<FormattedText, Drawing> textDrawings = new();

    void DrawCachedText(DrawingContext dc, FormattedText t, Point at)
    {
        if (!textDrawings.TryGetValue(t, out var d))
        {
            var g = new DrawingGroup();
            using (var c = g.Open()) c.DrawText(t, new Point(0, 0));
            g.Freeze();
            textDrawings[t] = d = g;
        }
        dc.PushTransform(new TranslateTransform(at.X, at.Y));
        dc.DrawDrawing(d);
        dc.Pop();
    }

    // name bold, behind it the tier as golden stars (the user's request 2026-10-31)
    FormattedText TitleText(PlacedPrefab p, double size)
    {
        string stars = PrefabDef.Stars(p.Def.Tier), caption = Caption(p);
        return CachedText(stars == "" ? caption : caption + "  " + stars, size, FontWeights.SemiBold, stars.Length);
    }
    static readonly Brush StarBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0x9A, 0x00)));

    static string Caption(PlacedPrefab p) => string.IsNullOrEmpty(p.Def.Label) ? p.Name : p.Def.Label;

    Rect PrefabRect(PlacedPrefab p) =>
        new(ToScreenX(p.X), ToScreenY(p.Z + p.SizeZ), p.SizeX * scale, p.SizeZ * scale);

    // play points orange, test point purple (on top); the one chosen in the spawn point list with a ring
    void DrawSpawns(DrawingContext dc)
    {
        foreach (var s in world.Spawns.OrderBy(s => s.Test)) DrawSpawn(dc, s, s.Test ? TestSpawnPen : SpawnPen);
        if (selectedSpawn != null) dc.DrawEllipse(null, SelectedPen, ToScreen(selectedSpawn.X, selectedSpawn.Z), 13, 13);
        if (placingSpawn != null) DrawSpawn(dc, placingSpawn, placingSpawn.Test ? TestSpawnPen : SpawnPen);
    }

    // dragging = view direction (to whole degrees, 0 = north, 90 = east); a small tremor when clicking changes nothing
    void AimSpawn(Point p)
    {
        if ((p - handleDownAt).Length <= 6) return;
        var c = ToScreen(placingSpawn.X, placingSpawn.Z);
        placingSpawn.Heading = Math.Round((Math.Atan2(p.X - c.X, c.Y - p.Y) * 180 / Math.PI + 360) % 360);
    }

    /// building that is being placed (the user's request): floats with the center at the mouse pointer, click = place,
    /// right click = cancel (the main window rotates with R and cancels with Esc). Position in Ghost.X/Z (whole meters).
    public PlacedPrefab Ghost
    {
        get => ghost;
        set { ghost = value; if (ghost != null) PlaceGhost(); InvalidateVisual(); }
    }
    PlacedPrefab ghost;
    /// let tiles snap to neighboring tiles and to the 150 m grid (switch in the window "Building"); Alt pressed reverses it
    public bool TileSnap { get; set; } = true;
    bool SnapTilesNow => TileSnap != ((Keyboard.Modifiers & ModifierKeys.Alt) != 0);
    Point ghostAt;   // center (world coordinates)
    public event Action<PlacedPrefab> GhostPlaced;
    public event Action GhostCancelled;

    /// rotate the outline by 90° (center stays)
    public void RotateGhost()
    {
        if (ghost == null) return;
        ghost.Rotation = (ghost.Rotation + 1) % 4;
        PlaceGhost();
        InvalidateVisual();
    }

    /// set the center of the outline (e.g. "Move" starts at the previous place)
    public void SetGhostCenter(double x, double z) { ghostAt = new Point(x, z); PlaceGhost(); InvalidateVisual(); }

    void PlaceGhost()
    {
        ghost.X = (int)Math.Round(ghostAt.X - ghost.SizeX / 2.0);
        ghost.Z = (int)Math.Round(ghostAt.Y - ghost.SizeZ / 2.0);
        if (ghost.IsTile && world != null && SnapTilesNow) (ghost.X, ghost.Z) = SnapTilePosition(ghost.X, ghost.Z, ghost.SizeX, ghost.SizeZ, null);   // tiles snap
    }

    /// snapping of a city tile (the user's request 2026-11-03: freely placed tiles must also be alignable to each other):
    /// per axis to the edge of a neighboring tile (flush beside or in line) if it is near enough; otherwise in the 150 m grid.
    /// neighbors only count if they are near on the other axis. self = the tile itself (when moving).
    (int X, int Z) SnapTilePosition(int x, int z, int sizeX, int sizeZ, PlacedPrefab self)
    {
        double tol = Math.Clamp(24 / scale, 6, 40);
        int bestX = x, bestZ = z;
        double dx = tol + 1e-9, dz = tol + 1e-9;
        bool snappedX = false, snappedZ = false;
        foreach (var t in world.Prefabs)
        {
            if (!t.IsTile || t == self) continue;
            bool nearZ = z < t.Z + t.SizeZ + tol && z + sizeZ > t.Z - tol, nearX = x < t.X + t.SizeX + tol && x + sizeX > t.X - tol;
            if (nearZ)
                foreach (int c in new[] { t.X - sizeX, t.X + t.SizeX, t.X })
                    if (Math.Abs(x - c) < dx) { dx = Math.Abs(x - c); bestX = c; snappedX = true; }
            if (nearX)
                foreach (int c in new[] { t.Z - sizeZ, t.Z + t.SizeZ, t.Z })
                    if (Math.Abs(z - c) < dz) { dz = Math.Abs(z - c); bestZ = c; snappedZ = true; }
        }
        return (snappedX ? bestX : world.SnapTile(x, false), snappedZ ? bestZ : world.SnapTile(z, true));
    }

    static readonly Pen GhostPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(20, 150, 70)), 2) { DashStyle = DashStyles.Dash });
    static readonly Brush GhostFill = Frozen(new SolidColorBrush(Color.FromArgb(70, 40, 190, 90)));

    void DrawGhost(DrawingContext dc)
    {
        if (ghost == null || world == null) return;
        var r = PrefabRect(ghost);
        if (SurfaceOf(ghost) is BitmapSource surface) dc.DrawImage(surface, r);   // surface already when placing (the user's request)
        DrawPreviewParts(dc, ghost);
        dc.DrawRectangle(GhostFill, GhostPen, r);
        // entrances/exits as points so that the direction is recognizable
        foreach (var e in PrefabParts.ExitsFor(world, ghost, null, false))
        {
            var c = ToScreen(e.X, e.Z);
            dc.DrawEllipse(ExitFill, HandlePen, c, 5, 5);
            if (e.Width > 0 && scale >= 0.4) DrawWidthLabel(dc, c, Loc.F("{0} m", e.Width));
        }
        DrawFrontArrow(dc, ghost, r);
        var text = Text(Caption(ghost), 12, FontWeights.SemiBold);
        dc.DrawRoundedRectangle(Brushes.White, GhostPen, new Rect(r.Left, r.Top - text.Height - 6, text.Width + 8, text.Height + 4), 3, 3);
        dc.DrawText(text, new Point(r.Left + 4, r.Top - text.Height - 4));
    }

    /// collect entrances/exits anew (after buildings were added, moved or deleted)
    public void RebuildExits()
    {
        exits.Clear();
        if (world == null) return;
        exitWidths.Clear();
        foreach (var e in PrefabParts.AllExitsWithWidth(world))   // also outer edges of the driveways and connections from the surface
        {
            exits.Add((new Point(e.X, e.Z), e.Owner));
            exitWidths.Add(e.Width);
        }
        // free lots: no building has its center in them
        freeLots.Clear();
        hoveredLot = null;
        var buildings = world.Prefabs.Where(p => !p.IsTile && p.Def != null && p.Def.Kind != PrefabKind.Part).ToList();
        foreach (var t in world.Prefabs.Where(p => p.IsTile))
        {
            var inTile = buildings.Where(b => t.Contains(b.X + b.SizeX / 2.0, b.Z + b.SizeZ / 2.0)).ToList();
            foreach (var lot in TileLots.Of(t))
                if (!inTile.Any(b => b.X + b.SizeX / 2.0 > lot.X && b.X + b.SizeX / 2.0 < lot.X + lot.Width && b.Z + b.SizeZ / 2.0 > lot.Z && b.Z + b.SizeZ / 2.0 < lot.Z + lot.Depth))
                    freeLots.Add(lot);
        }
        InvalidateVisual();
    }

    /// free lots of the city tiles: dashed with "+", click = build up (the user's request: after deleting, build up again
    /// fitting, without having to aim exactly)
    readonly List<TileLot> freeLots = new();
    TileLot hoveredLot;
    public event Action<TileLot> LotClicked;
    static readonly Pen LotPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(20, 150, 70)), 1.5) { DashStyle = DashStyles.Dash });
    static readonly Brush LotFill = Frozen(new SolidColorBrush(Color.FromArgb(45, 40, 190, 90)));
    static readonly Brush LotHoverFill = Frozen(new SolidColorBrush(Color.FromArgb(110, 40, 190, 90)));

    bool ShowLots => showPrefabs && !editMode && !eraseMode && ghost == null && spawnMode == null;
    Rect LotRect(TileLot l) => new(ToScreen(l.X, l.Z + l.Depth), ToScreen(l.X + l.Width, l.Z));

    TileLot FreeLotAt(double x, double z) => ShowLots
        ? freeLots.FirstOrDefault(l => x >= l.X && x < l.X + l.Width && z >= l.Z && z < l.Z + l.Depth && LotRect(l).Width >= 18) : null;

    void DrawFreeLots(DrawingContext dc)
    {
        if (!ShowLots) return;
        foreach (var lot in freeLots)
        {
            var r = LotRect(lot);
            if (r.Width < 18 || r.Right < 0 || r.Bottom < 0 || r.Left > ActualWidth || r.Top > ActualHeight) continue;
            dc.DrawRectangle(lot == hoveredLot ? LotHoverFill : LotFill, LotPen, r);
            var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            double s = Math.Min(10, r.Width / 4);
            dc.DrawLine(LotPen, new Point(c.X - s, c.Y), new Point(c.X + s, c.Y));
            dc.DrawLine(LotPen, new Point(c.X, c.Y - s), new Point(c.X, c.Y + s));
            if (lot != hoveredLot || r.Width < 90) continue;
            var text = Text(Loc.F("Free lot {0} × {1} m – click to build", lot.Width, lot.Depth), 11, FontWeights.SemiBold);
            dc.DrawText(text, new Point(c.X - text.Width / 2, c.Y + s + 4));
        }
    }

    /// building that may be moved with the move icon (only if it is also marked; the user's request)
    public PlacedPrefab Movable { get => movable; set { if (value != movable) pendDX = pendDZ = 0; movable = value; InvalidateVisual(); } }
    PlacedPrefab movable;
    /// move icon dragged and released: new southwest corner (x, z); it is only taken over in the window "Building"
    public event Action<PlacedPrefab, int, int> SelectedDragged;
    /// dragged, not yet confirmed move (m): the outline stays there dashed (the user's request)
    public (int DX, int DZ) PendingMove { get => (pendDX, pendDZ); set { (pendDX, pendDZ) = value; InvalidateVisual(); } }
    int pendDX, pendDZ;
    bool draggingPrefab;
    Point dragPrefabStart;
    int dragDX, dragDZ;

    (int DX, int DZ) MoveOffset => draggingPrefab ? (dragDX, dragDZ) : (pendDX, pendDZ);

    bool ShowMoveHandle => movable != null && movable == selected && !editMode && !eraseMode && spawnMode == null && ghost == null
                           && world != null && world.Prefabs.Contains(movable);

    Point MoveHandleCenter
    {
        get { var r = PrefabRect(movable); var (dx, dz) = MoveOffset; return new Point(r.X + r.Width / 2 + dx * scale, r.Y + r.Height / 2 - dz * scale); }
    }

    static readonly Pen MovePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 110, 230)), 2));

    // move icon (✥) in the center of the marked building; when dragging the new outline dashed
    void DrawMoveHandle(DrawingContext dc)
    {
        if (!ShowMoveHandle) return;
        var (ox, oz) = MoveOffset;
        if (ox != 0 || oz != 0)
        {
            var r = PrefabRect(movable);
            r.Offset(ox * scale, -oz * scale);
            if (SurfaceOf(movable) is BitmapSource surface) dc.DrawImage(surface, r);
            DrawPreviewParts(dc, new PlacedPrefab { Name = movable.Name, Def = movable.Def, X = movable.X + ox, Y = movable.Y, Z = movable.Z + oz, Rotation = movable.Rotation });
            dc.DrawRectangle(GhostFill, GhostPen, r);
            DrawFrontArrow(dc, movable, r);
        }
        var c = MoveHandleCenter;
        dc.DrawEllipse(Brushes.White, MovePen, c, 13, 13);
        // icon (four arrows) from the icon font, centered in the circle
        var glyph = new FormattedText(Icons.ArrowsMove, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(IconUi.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 17, MovePen.Brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(glyph, new Point(c.X - glyph.Width / 2, c.Y - glyph.Height / 2));
    }

    bool OverMoveHandle(Point p) => ShowMoveHandle && (p - MoveHandleCenter).Length <= 14;

    void DrawSpawn(DrawingContext dc, SpawnPoint s, Pen pen)
    {
        var c = ToScreen(s.X, s.Z);
        double a = s.Heading * Math.PI / 180;
        dc.DrawLine(pen, c, new Point(c.X + Math.Sin(a) * 16, c.Y - Math.Cos(a) * 16));
        dc.DrawEllipse(SpawnFill, pen, c, 6, 6);
    }

    /// set a spawn point: null = off, false = play point, true = test point. Click = place, drag with the mouse held = view direction.
    public bool? SpawnMode { get => spawnMode; set { spawnMode = value; placingSpawn = null; InvalidateVisual(); } }
    bool? spawnMode;
    SpawnPoint placingSpawn;   // point that is being set right now (until the mouse is released)
    /// finished spawn point (height still 0, set by the main window)
    public event Action<SpawnPoint> SpawnPlaced;
    /// point chosen in the spawn point list (ring)
    public SpawnPoint SelectedSpawn { get => selectedSpawn; set { selectedSpawn = value; InvalidateVisual(); } }
    SpawnPoint selectedSpawn;

    /// further planned pieces (rebuild of an existing road at an intersection), purple under the new road
    public List<RoadResult> ExtraResults { get => extraResults; set { extraResults = value; InvalidateVisual(); } }
    List<RoadResult> extraResults;
    static readonly Brush RebuildBrush = Frozen(new SolidColorBrush(Color.FromArgb(150, 130, 70, 200)));

    // road band with variable half width (taper)
    StreamGeometry Ribbon(List<PathPoint> path, int step, Func<int, double> half)
    {
        var idx = new List<int>();
        for (int k = 0; k < path.Count; k += step) idx.Add(k);
        if (idx[^1] != path.Count - 1) idx.Add(path.Count - 1);
        Point Edge(int k, int side)
        {
            var (nx, nz) = RoadPlanner.Normal(path, k);
            double o = side * half(k);
            return ToScreen(path[k].X + nx * o, path[k].Z + nz * o);
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(Edge(idx[0], 1), true, true);
            ctx.PolyLineTo(idx.Skip(1).Select(k => Edge(k, 1)).ToList(), true, true);
            ctx.PolyLineTo(Enumerable.Reverse(idx).Select(k => Edge(k, -1)).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    void DrawRoad(DrawingContext dc)
    {
        foreach (var x in extraResults ?? new List<RoadResult>())
        {
            int st = Math.Max(1, (int)(1 / (0.25 * scale)));
            var pen = new Pen(RebuildBrush, Math.Max(3, x.Settings.Width * scale)) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, Polyline(x.Path, st, k => ToScreen(x.Path[k].X, x.Path[k].Z)));
        }
        if (result != null && result.EditLook) DrawEditRoad(dc, result);
        else if (result != null)
        {
            var s = result.Settings;
            var path = result.Path;
            int step = Math.Max(1, (int)(1 / (0.25 * scale)));   // about one screen pixel distance
            double half = s.Width / 2 + s.Shoulder;

            // slope edges (dashed)
            foreach (int side in new[] { -1, 1 })
                dc.DrawGeometry(null, SlopePen, Polyline(path, step, k =>
                {
                    var (nx, nz) = RoadPlanner.Normal(path, k);
                    double o = side * (result.WidthAt(k) / 2 + s.Shoulder + s.GrassEdge + result.SlopeWidth[k]);
                    return ToScreen(path[k].X + nx * o, path[k].Z + nz * o);
                }));

            // grass edge (light green, wider than the roadway)
            if (s.GrassEdge > 0 && s.Surface != Surface.Remove)
                dc.DrawGeometry(GrassEdgeBrush, null, Ribbon(path, step, k => result.WidthAt(k) / 2 + s.Shoulder + s.GrassEdge));

            // roadway with edge in full width
            var brush =s.Surface switch { Surface.Asphalt => AsphaltBrush, Surface.Gravel => GravelBrush, _ => RemoveBrush };
            var road = new Pen(brush, Math.Max(3, 2 * half * scale))
            { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round };
            if (result.Widths == null) dc.DrawGeometry(null, road, Polyline(path, step, k => ToScreen(path[k].X, path[k].Z)));
            else dc.DrawGeometry(brush, null, Ribbon(path, step, k => result.WidthAt(k) / 2 + s.Shoulder));   // taper: width changes

            foreach (var o in result.Obstacles) dc.DrawRectangle(null, ObstaclePen, PrefabRect(o.Prefab));
            // removal: preserved intersections green, junctions (cut off at the edge) red
            foreach (var c in result.CrossRoads)
            {
                bool keep = c.Through && s.KeepCrossings;
                var pen = new Pen(keep ? OkPen.Brush : WarnPen.Brush, Math.Max(3, 2 * c.HalfWidth * scale)) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                dc.PushOpacity(0.6);
                dc.DrawLine(pen, ToScreen(c.A.X, c.A.Y), ToScreen(c.B.X, c.B.Y));
                dc.Pop();
            }
            foreach (var c in result.Crossings)
                dc.DrawEllipse(null, c.NeedsWork ? WarnPen : OkPen, ToScreen(c.X, c.Z), Math.Max(9, half * scale + 4), Math.Max(9, half * scale + 4));

            if (profileMarker is double d)
            {
                var p = path[result.IndexAt(d)];
                dc.DrawEllipse(Brushes.White, MarkerPen, ToScreen(p.X, p.Z), 7, 7);
            }
        }

        if (pickStart is Point ps) dc.DrawEllipse(StartFill, HandlePen, ToScreen(ps.X, ps.Y), 7, 7);
        if (!editMode && result != null) return;
        for (int i = 0; i < roadPoints.Count; i++)
        {
            var c = ToScreen(roadPoints[i].X, roadPoints[i].Y);
            Brush fill = i == 0 ? StartFill : i == roadPoints.Count - 1 ? EndFill : Brushes.White;
            double r = i == hoverHandle || i == dragHandle ? 8 : 6;
            // snapped at an entrance/exit: turquoise ring
            if (MarkerSnap != null) r += 3;
            if (MarkerSnap == null && (i == 0 || i == roadPoints.Count - 1) && OnExit(roadPoints[i])) dc.DrawEllipse(null, new Pen(ExitFill, 3), c, r + 4, r + 4);
            if (i == selectedPoint) dc.DrawEllipse(null, SelectedPen, c, r + 5, r + 5);
            dc.DrawEllipse(fill, HandlePen, c, r, r);
            if (MarkerSnap != null)
            {
                // edit road: number of the marker in the circle
                var nt = Text((i + 1).ToString(Loc.Num), 11);
                dc.DrawText(nt, new Point(c.X - nt.Width / 2, c.Y - nt.Height / 2));
            }
            // height set by hand: value beside the point
            double? hh = i < pointHeights.Count ? pointHeights[i] : null, cc = i < pointCross.Count ? pointCross[i] : null;
            double? ww = result?.Widths != null && i < result.ControlIndex.Length ? result.Widths[result.ControlIndex[i]] : null;
            if (hh != null || cc != null || ww != null)
            {
                var t = Text(string.Join(" · ", new[] { hh is double h ? h.ToString("0.0", Loc.Num) + " m" : null, cc is double q ? (q * 100).ToString("+0.#;-0.#;0", Loc.Num) + " %" : null, ww is double wv ? "↔ " + wv.ToString("0.#", Loc.Num) + " m" : null }.Where(s => s != null)), 11);
                var box = new Rect(c.X + r + 4, c.Y - t.Height / 2 - 1, t.Width + 6, t.Height + 2);
                dc.DrawRoundedRectangle(Brushes.White, SelectedPen, box, 3, 3);
                dc.DrawText(t, new Point(box.Left + 3, box.Top + 1));
            }
        }
    }

    static readonly Brush EditBrush = Frozen(new SolidColorBrush(Color.FromArgb(170, 30, 110, 230)));
    static readonly Pen HeldPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(235, 140, 0)), 3));

    // edit road: edited stretch blue, run-out before and after purple, intersections as rings
    // (green = the crossing road follows, orange = height stays)
    void DrawEditRoad(DrawingContext dc, RoadResult r)
    {
        var path = r.Path;
        int step = Math.Max(1, (int)(1 / (0.25 * scale)));
        double width = Math.Max(3, 2 * r.Settings.Width / 2 * scale);
        void Piece(int from, int to, Brush brush)
        {
            if (to <= from) return;
            var seg = path.GetRange(from, to - from + 1);
            var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, Polyline(seg, step, k => ToScreen(seg[k].X, seg[k].Z)));
        }
        int a = r.CoreFrom < 0 ? 0 : r.CoreFrom, b = r.CoreTo < 0 ? path.Count - 1 : r.CoreTo;
        Piece(0, a, RebuildBrush);
        Piece(b, path.Count - 1, RebuildBrush);
        Piece(a, b, EditBrush);
        double ring = Math.Max(9, r.Settings.Width / 2 * scale + 4);
        foreach (var c in r.Crossings) dc.DrawEllipse(null, OkPen, ToScreen(c.X, c.Z), ring, ring);
        foreach (var p in r.HeldPoints) dc.DrawEllipse(null, HeldPen, ToScreen(p.X, p.Y), ring + 3, ring + 3);
        if (profileMarker is double d)
        {
            var p = path[r.IndexAt(d)];
            dc.DrawEllipse(Brushes.White, MarkerPen, ToScreen(p.X, p.Z), 7, 7);
        }
    }

    void DrawBrush(DrawingContext dc)
    {
        if (!eraseMode) return;
        if (Lasso)
        {
            DrawLasso(dc);
            return;
        }
        if (CustomShape)
        {
            DrawCustomBrush(dc);
            return;
        }
        if (stroke.Count > 0)
        {
            var cap = brushSquare ? PenLineCap.Square : PenLineCap.Round;
            var pen = new Pen(strokeFill ?? RemoveBrush, Math.Max(2, 2 * brushRadius * scale)) { StartLineCap = cap, EndLineCap = cap, LineJoin = brushSquare ? PenLineJoin.Miter : PenLineJoin.Round };
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(ToScreen(stroke[0].X, stroke[0].Y), false, false);
                ctx.PolyLineTo(stroke.Skip(1).Append(stroke[^1]).Select(p => ToScreen(p.X, p.Y)).ToList(), true, true);
            }
            dc.DrawGeometry(null, pen, g);
        }
        if (brushAt is Point b)
        {
            double r = Math.Max(3, brushRadius * scale);
            var c = ToScreen(b.X, b.Y);
            if (brushSquare) dc.DrawRectangle(null, BrushPen, new Rect(c.X - r, c.Y - r, 2 * r, 2 * r));
            else dc.DrawEllipse(null, BrushPen, c, r, r);
        }
    }

    static readonly Pen AreaLinePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1));
    static readonly Pen LassoPen =Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x3E, 0x22)), 2) { DashStyle = DashStyles.Dash });

    /// true: the drawn area is not rounded off (lake: shore stays angular)
    public bool AngularArea { get; set; }

    // freehand area: closed, filled polygon; a small circle at the mouse pointer
    void DrawLasso(DrawingContext dc)
    {
        if (stroke.Count > 1)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(ToScreen(stroke[0].X, stroke[0].Y), !OpenArea, !OpenArea);
                ctx.PolyLineTo(stroke.Skip(1).Select(p => ToScreen(p.X, p.Y)).ToList(), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(strokeFill ?? RemoveBrush, LassoPen, g);
        }
        if (area.Count >= (OpenArea ? 1 : 3))
        {
            // finished area: the rounded area as it is calculated, plus the polygon with its points for editing
            if (OpenArea && area.Count >= 2)
            {
                // river: wide, semi-transparent band along the line (width in meters)
                var line = new StreamGeometry();
                using (var ctx = line.Open())
                {
                    ctx.BeginFigure(ToScreen(area[0].X, area[0].Y), false, false);
                    ctx.PolyLineTo(area.Skip(1).Select(p => ToScreen(p.X, p.Y)).ToList(), true, true);
                }
                line.Freeze();
                var band = new Pen(strokeFill ?? RemoveBrush, Math.Max(2, AreaWidth * scale)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, band, line);
            }
            else if (!OpenArea)
            {
            var smooth = RangeSpec.PrepareOutline(area, true, AngularArea ? 1 : 3);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(ToScreen(smooth[0].X, smooth[0].Y), true, true);
                ctx.PolyLineTo(smooth.Skip(1).Select(p => ToScreen(p.X, p.Y)).ToList(), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(strokeFill ?? RemoveBrush, LassoPen, g);
            }
            var poly = new StreamGeometry();
            using (var ctx = poly.Open())
            {
                ctx.BeginFigure(ToScreen(area[0].X, area[0].Y), false, !OpenArea);
                ctx.PolyLineTo(area.Skip(1).Select(p => ToScreen(p.X, p.Y)).ToList(), true, true);
            }
            poly.Freeze();
            dc.DrawGeometry(null, AreaLinePen, poly);
            for (int i = 0; i < area.Count; i++)
            {
                var c = ToScreen(area[i].X, area[i].Y);
                dc.DrawEllipse(Brushes.White, LassoPen, c, i == areaHover || i == areaDrag ? 7 : 5, i == areaHover || i == areaDrag ? 7 : 5);
            }
        }
        if (brushAt is Point b && areaDrag < 0) dc.DrawEllipse(null, BrushPen, ToScreen(b.X, b.Y), 5, 5);
    }

    // brush with a special shape: stroke = union of the stamped shapes (one area, no darkening), mouse pointer = outline
    void DrawCustomBrush(DrawingContext dc)
    {
        var spec = shapeSpec ??= ShapeSpec.Create(shape, shape == BrushShape.Random && stroke.Count > 0 ? StrokeSeed : nextSeed);
        var outline = spec.Outline();
        PathGeometry Stamp(Point worldCenter)
        {
            var c = ToScreen(worldCenter.X, worldCenter.Y);
            double r = Math.Max(3, brushRadius * scale);
            var fig = new PathFigure { StartPoint = new Point(c.X + outline[0].X * r, c.Y - outline[0].Y * r), IsClosed = true, IsFilled = true };
            foreach (var p in outline.Skip(1)) fig.Segments.Add(new LineSegment(new Point(c.X + p.X * r, c.Y - p.Y * r), true));
            return new PathGeometry(new[] { fig });
        }
        if (stroke.Count > 0)
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            double step = Math.Max(0.5, brushRadius / 6);
            group.Children.Add(Stamp(stroke[0]));
            for (int k = 0; k + 1 < stroke.Count; k++)
            {
                double len = (stroke[k + 1] - stroke[k]).Length;
                int parts = Math.Max(1, (int)Math.Ceiling(len / step));
                for (int q = 1; q <= parts; q++) group.Children.Add(Stamp(stroke[k] + (stroke[k + 1] - stroke[k]) * q / parts));
            }
            dc.DrawGeometry(strokeFill ?? RemoveBrush, null, group);
        }
        if (brushAt is Point b) dc.DrawGeometry(null, BrushPen, Stamp(stroke.Count > 0 ? stroke[^1] : b));
    }

    static StreamGeometry Polyline(List<PathPoint> path, int step, Func<int, Point> at)
    {
        var pts = new List<Point>(path.Count / step + 2);
        for (int k = 0; k < path.Count; k += step) pts.Add(at(k));
        if ((path.Count - 1) % step != 0) pts.Add(at(path.Count - 1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    // ---------- Mouse ----------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (world == null) return;
        var p = e.GetPosition(this);
        double wx = ToWorldX(p.X), wz = ToWorldZ(p.Y);
        double min = Math.Min(ActualWidth, ActualHeight) / world.Size / 2;
        scale = Math.Clamp(scale * (e.Delta > 0 ? 1.25 : 0.8), min, MaxScale);
        ox = p.X - (wx + world.Half) * scale;
        oy = p.Y - (world.Half - wz) * scale;
        UpdateScalingMode();
        InvalidateVisual();
        e.Handled = true;
    }

    /// zoom level: screen pixels per meter (1 = 100 %), at most 16 (1600 %; the selection list goes only to 1000 %, the user's request)
    public double Scale => scale;
    public bool HasWorld => world != null;
    public const double MaxScale = 16;
    /// zoom level changed (mouse wheel, Whole map, jump to a building …)
    public event Action ScaleChanged;

    /// set the zoom level; the center of the view stays
    public void SetScale(double s)
    {
        if (world == null || ActualWidth == 0) return;
        double cx = ToWorldX(ActualWidth / 2), cz = ToWorldZ(ActualHeight / 2);
        scale = Math.Clamp(s, Math.Min(ActualWidth, ActualHeight) / world.Size / 2, MaxScale);
        CenterOn(cx, cz);
    }

    // when zooming in sharp pixels (every meter recognizable), when zooming out smoothed; called on every zoom change.
    // Linear instead of HighQuality: HighQuality made the graphics card stand still 120–200 ms on the first use of a zoom level and
    // after every change; the reduced copies of MapLayers keep the shrinking at most 2 ×, so Linear looks clean.
    void UpdateScalingMode()
    {
        RenderOptions.SetBitmapScalingMode(this, scale * MapLayers.MetersPerPixel >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
        ScaleChanged?.Invoke();
    }

    int HitHandle(Point p)
    {
        for (int i = roadPoints.Count - 1; i >= 0; i--)
            if ((ToScreen(roadPoints[i].X, roadPoints[i].Y) - p).Length <= 9) return i;
        return -1;
    }

    Point ClampToWorld(Point screen) =>
        new(Math.Clamp(ToWorldX(screen.X), -world.Half + 1, world.Half - 2), Math.Clamp(ToWorldZ(screen.Y), -world.Half + 1, world.Half - 2));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (world == null) return;
        Focus();
        var p = e.GetPosition(this);
        if (OverMoveHandle(p))
        {
            // move icon: drag with the left mouse button held down (the user's request)
            // keep dragging from a not yet confirmed position
            dragPrefabStart = new Point(p.X - pendDX * scale, p.Y + pendDZ * scale);
            (dragDX, dragDZ) = (pendDX, pendDZ);
            draggingPrefab = true;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (spawnMode != null)
        {
            var at = ClampToWorld(p);
            placingSpawn = new SpawnPoint { X = Math.Round(at.X), Z = Math.Round(at.Y), Test = spawnMode.Value };
            handleDownAt = p;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (eraseMode && Lasso && area.Count > 0 && !Busy)
        {
            // edit an existing area: grab a point or insert a new one on the edge
            int hit = HitArea(p);
            if (hit < 0 && HitAreaEdge(p, out var foot) is int seg and >= 0)
            {
                area.Insert(seg + 1, foot);
                hit = seg + 1;
                AreaChanged?.Invoke();
            }
            if (hit >= 0)
            {
                areaDrag = hit;
                CaptureMouse();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (!OpenArea)
            {
                area.Clear();   // dragging elsewhere = draw a new area
                areaHover = -1;
                AreaChanged?.Invoke();
            }
        }
        if (eraseMode && Lasso && OpenArea)
        {
            // path (river): like with the road – click without dragging sets a point, dragging pans the map
            if (Busy) return;
            dragStart = p; dragOx = ox; dragOy = oy; dragging = true; moved = false;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (eraseMode)
        {
            if (Busy) return;
            erasing = true;
            stroke.Clear();
            stroke.Add(ClampToWorld(p));
            StrokeSeed = nextSeed;   // random shape: the shown shape applies to this stroke, the mouse pointer shows the next one afterwards
            if (shape == BrushShape.Random) { nextSeed = Random.Shared.Next(); shapeSpec = null; }
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (editMode && (dragHandle = HitHandle(p)) >= 0)
        {
            handleDownAt = p;
            handleMoved = false;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        dragStart = p; dragOx = ox; dragOy = oy; dragging = true; moved = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (draggingPrefab)
        {
            draggingPrefab = false;
            ReleaseMouseCapture();
            Cursor = null;
            (pendDX, pendDZ) = (dragDX, dragDZ);
            dragDX = dragDZ = 0;
            InvalidateVisual();
            if (movable != null) SelectedDragged?.Invoke(movable, movable.X + pendDX, movable.Z + pendDZ);
            return;
        }
        if (placingSpawn != null)
        {
            AimSpawn(e.GetPosition(this));   // also on release: fast dragging may report no movement in between
            var sp = placingSpawn;
            placingSpawn = null;
            ReleaseMouseCapture();
            InvalidateVisual();
            SpawnPlaced?.Invoke(sp);
            return;
        }
        if (areaDrag >= 0)
        {
            areaDrag = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            AreaChanged?.Invoke();
            return;
        }
        if (erasing && Lasso)
        {
            // area finished drawing: simplify to a few points and leave for editing
            erasing = false;
            ReleaseMouseCapture();
            var drawn = stroke.ToList();
            stroke.Clear();
            if (drawn.Count >= (OpenArea ? 2 : 3))
            {
                var simple = SimplifyOutline(drawn, Math.Max(3, 7 / scale));
                if (!OpenArea && simple.Count >= 3 && (simple[0] - simple[^1]).Length < 1) simple.RemoveAt(simple.Count - 1);
                if (simple.Count >= (OpenArea ? 2 : 3)) { area.Clear(); area.AddRange(simple); }
            }
            InvalidateVisual();
            AreaChanged?.Invoke();
            return;
        }
        if (erasing)
        {
            erasing = false;
            ReleaseMouseCapture();
            if (stroke.Count == 0) return;   // the stroke was discarded on the way (e.g. Esc during erasing; audit finding F26)
            var end = ClampToWorld(e.GetPosition(this));
            if (SingleStamp) stroke[0] = end;
            else if ((end - stroke[^1]).Length > 0.1) stroke.Add(end);
            StrokeFinished?.Invoke(stroke.ToList());
            return;
        }
        if (dragHandle >= 0)
        {
            int h = dragHandle;
            dragHandle = -1;
            ReleaseMouseCapture();
            if (handleMoved) RoadPointsChanged?.Invoke(true);
            else SelectedPoint = h;   // click without dragging chooses the point (for its height)
            return;
        }
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        Cursor = null;
        if (!moved && ghost != null) { GhostPlaced?.Invoke(ghost); return; }
        if (!moved && eraseMode && Lasso && OpenArea)
        {
            area.Add(ClampToWorld(e.GetPosition(this)));
            InvalidateVisual();
            AreaChanged?.Invoke();
            return;
        }
        if (!moved && pickMode) MapClicked?.Invoke(ClampToWorld(e.GetPosition(this)));
        else if (!moved && editMode) AddPoint(e.GetPosition(this));
        else if (!moved && !eraseMode && spawnMode == null && HitSpawn(e.GetPosition(this)) is SpawnPoint sp) SpawnClicked?.Invoke(sp);
        else if (!moved && hoveredLot != null) LotClicked?.Invoke(hoveredLot);
        else if (!moved && hovered != null) PrefabClicked?.Invoke(hovered);
    }

    // click on the road inserts a point in the section hit, otherwise it is appended at the end
    void AddPoint(Point screen)
    {
        var w = ClampToWorld(screen);
        int insertAt = roadPoints.Count;
        if (MarkerSnap != null)
        {
            // edit road: the point lands exactly on the center of the existing road, otherwise not at all
            if (MarkerSnap(w) is not Point snapped) { MarkerRejected?.Invoke(); return; }
            w = snapped;
            insertAt = Math.Clamp(InsertIndex?.Invoke(w) ?? roadPoints.Count, 0, roadPoints.Count);
        }
        else if (result != null && roadPoints.Count >= 2)
        {
            double tol = Math.Max(10 / scale, result.Settings.Width / 2 + result.Settings.Shoulder), best = double.MaxValue;
            foreach (var pp in result.Path)
            {
                double d = Math.Sqrt((pp.X - w.X) * (pp.X - w.X) + (pp.Z - w.Y) * (pp.Z - w.Y));
                if (d < best) { best = d; if (d <= tol) insertAt = pp.Segment + 1; }
            }
            insertAt = Math.Min(insertAt, roadPoints.Count);
        }
        if (MarkerSnap == null && insertAt == roadPoints.Count) w = SnapToExit(w);   // new end point (or the first point)
        roadPoints.Insert(insertAt, w);
        pointHeights.Insert(insertAt, null);
        pointCross.Insert(insertAt, null);
        pointWidths.Insert(insertAt, null);
        if (selectedPoint >= insertAt) selectedPoint++;
        InvalidateVisual();
        RoadPointsChanged?.Invoke(true);
    }

    // eraser: drag with the right mouse button = pan (the left one erases)
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        // eraser and building in hand: drag with the right mouse button = pan (with the building: click without dragging = cancel)
        if (world == null || (!eraseMode && ghost == null)) return;
        // ignore while drawing on the left or dragging a point: otherwise the map would take the mouse away and the stroke would be lost
        if (erasing || areaDrag >= 0 || draggingPrefab) { e.Handled = true; return; }
        dragStart = e.GetPosition(this); dragOx = ox; dragOy = oy; dragging = panRight = true; moved = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (panRight)
        {
            panRight = dragging = false;
            ReleaseMouseCapture();
            Cursor = null;
            e.Handled = true;
            if (!moved && ghost != null) GhostCancelled?.Invoke();
            if (!moved && Lasso && area.Count > (OpenArea ? 2 : 3) && HitArea(e.GetPosition(this)) is int ah and >= 0)
            {
                area.RemoveAt(ah);   // right click on a point deletes it
                areaHover = -1;
                InvalidateVisual();
                AreaChanged?.Invoke();
            }
            return;
        }
        if (!editMode) return;
        int h = HitHandle(e.GetPosition(this));
        if (h < 0) return;
        roadPoints.RemoveAt(h);
        pointHeights.RemoveAt(h);
        pointCross.RemoveAt(h);
        pointWidths.RemoveAt(h);
        if (selectedPoint == h) SelectedPoint = -1;
        else if (selectedPoint > h) selectedPoint--;
        hoverHandle = -1;
        InvalidateVisual();
        RoadPointsChanged?.Invoke(true);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (world == null) return;
        var p = e.GetPosition(this);
        if (eraseMode) brushAt = ClampToWorld(p);
        if (areaDrag >= 0)
        {
            area[areaDrag] = ClampToWorld(p);
            InvalidateVisual();
            return;
        }
        if (Lasso && eraseMode && !erasing && area.Count > 0)
        {
            int ah = HitArea(p);
            if (ah != areaHover) { areaHover = ah; InvalidateVisual(); }
        }
        if (draggingPrefab)
        {
            // move in whole meters
            dragDX = (int)Math.Round((p.X - dragPrefabStart.X) / scale);
            dragDZ = (int)Math.Round(-(p.Y - dragPrefabStart.Y) / scale);
            if (movable != null && movable.IsTile && SnapTilesNow)   // tiles snap
            {
                var (sx, sz) = SnapTilePosition(movable.X + dragDX, movable.Z + dragDZ, movable.SizeX, movable.SizeZ, movable);
                dragDX = sx - movable.X;
                dragDZ = sz - movable.Z;
            }
            Cursor = Cursors.SizeAll;
            InvalidateVisual();
            return;
        }
        if (ghost != null) { ghostAt = ClampToWorld(p); PlaceGhost(); Cursor = Cursors.Cross; InvalidateVisual(); }
        if (spawnMode != null)
        {
            Cursor = Cursors.Cross;
            if (placingSpawn != null) { AimSpawn(p); InvalidateVisual(); }
        }
        else if (eraseMode && !dragging)
        {
            if (erasing && SingleStamp) stroke[0] = brushAt.Value;   // mountain: only one point, dragging moves it until release
            else if (erasing && (brushAt.Value - stroke[^1]).Length >= (Lasso ? Math.Max(1.5, 4 / scale) : Math.Max(0.5, brushRadius / 4))) stroke.Add(brushAt.Value);
            Cursor = Cursors.Cross;
            InvalidateVisual();
        }
        else if (dragHandle >= 0)
        {
            if (!handleMoved && (p - handleDownAt).Length <= 3) return;   // a small tremor when clicking does not move
            handleMoved = true;
            var to = ClampToWorld(p);
            if (MarkerSnap != null) roadPoints[dragHandle] = MarkerSnap(to) ?? roadPoints[dragHandle];   // stays on the road center
            else roadPoints[dragHandle] = dragHandle == 0 || dragHandle == roadPoints.Count - 1 ? SnapToExit(to) : to;
            InvalidateVisual();
            RoadPointsChanged?.Invoke(false);
        }
        else if (dragging)
        {
            if ((p - dragStart).Length > 4) { moved = true; Cursor = Cursors.SizeAll; }
            if (moved)
            {
                ox = dragOx + p.X - dragStart.X;
                oy = dragOy + p.Y - dragStart.Y;
                InvalidateVisual();
            }
        }
        else if (pickMode) Cursor = Cursors.Cross;
        else if (editMode)
        {
            int h = HitHandle(p);
            if (h != hoverHandle) { hoverHandle = h; InvalidateVisual(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Cross;
        }
        else Cursor = OverMoveHandle(p) ? Cursors.SizeAll : null;

        double x = ToWorldX(p.X), z = ToWorldZ(p.Y);
        if (!world.Contains(x, z)) { SetHovered(null); PointerMoved?.Invoke(null); return; }

        PlacedPrefab hit = null;
        if (dragHandle < 0)
            foreach (var pf in world.Prefabs)   // smallest fitting building/part (only visible ones)
            {
                if (!IsShown(pf) || (pf.Def.Kind == PrefabKind.Tile && scale < 0.04)) continue;
                if (pf.Contains(x, z) && (hit == null || pf.SizeX * pf.SizeZ < hit.SizeX * hit.SizeZ)) hit = pf;
            }
        // above a free lot (in a tile) the lot counts, not the tile
        var lotHit = hit == null || hit.IsTile ? FreeLotAt(x, z) : null;
        if (lotHit != hoveredLot) { hoveredLot = lotHit; InvalidateVisual(); }
        if (lotHit != null) hit = null;
        Cursor = lotHit != null ? Cursors.Hand : Cursor == Cursors.Hand ? null : Cursor;
        SetHovered(hit);
        PointerMoved?.Invoke(new MapPointer((int)Math.Floor(x), (int)Math.Floor(z), hit));
    }

    // If the map loses the mouse in the middle of dragging (Alt+Tab, dialog), no release would arrive any more: eraser, dragging
    // and point moving would "hang" and continue without a pressed button (audit finding F27). With a normal release
    // the states are already reset before ReleaseMouseCapture is called.
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        bool any = dragging || erasing || draggingPrefab || placingSpawn != null || dragHandle >= 0 || panRight || areaDrag >= 0;
        if (!any) return;
        dragging = panRight = false;
        if (areaDrag >= 0) { areaDrag = -1; AreaChanged?.Invoke(); }
        if (erasing) stroke.Clear();   // incomplete stroke is discarded, nothing erased
        erasing = false;
        if (draggingPrefab) { dragDX = dragDZ = 0; }
        draggingPrefab = false;
        placingSpawn = null;
        dragHandle = -1;
        Cursor = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        brushAt = null;
        if (eraseMode) InvalidateVisual();
        SetHovered(null);
        PointerMoved?.Invoke(null);
    }

    void SetHovered(PlacedPrefab p)
    {
        if (p == hovered) return;
        hovered = p;
        InvalidateVisual();
    }
}

public sealed record MapPointer(int X, int Z, PlacedPrefab Prefab);
