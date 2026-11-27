using System.Windows.Media.Imaging;
using System.Windows;
using MapEditor.Map;

namespace YameeTests;

static class RiverTests
{
    internal static List<Point> Circle(double cx, double cz, double r)
    {
        var o = new List<Point>();
        for (int k = 0; k < 36; k++) o.Add(new Point(cx + r * Math.Cos(k * Math.PI / 18), cz + r * Math.Sin(k * Math.PI / 18)));
        return o;
    }

    /// far water of the game (FarWater): dry cells in a 16 m chunk with water that lie below level + 0.77 m (there the far water plane shows
    /// over land), and cells beside water more than 0.6 m above all four neighbors (spikes). Array coordinates, inclusive.
    internal static (int Below, int Peaks) FarWaterProblems(WorldData w, int x0, int z0, int x1, int z1)
    {
        int size = w.Size, below = 0, peaks = 0;
        for (int cz = Math.Max(0, z0 / 16); cz <= Math.Min(size / 16 - 1, z1 / 16); cz++)
            for (int cx = Math.Max(0, x0 / 16); cx <= Math.Min(size / 16 - 1, x1 / 16); cx++)
            {
                int L = 0;
                for (int z = cz * 16; z < cz * 16 + 16; z++)
                    for (int x = cx * 16; x < cx * 16 + 16; x++) L = Math.Max(L, w.WaterMask[z * size + x]);
                if (L == 0) continue;
                for (int z = cz * 16; z < cz * 16 + 16; z++)
                    for (int x = cx * 16; x < cx * 16 + 16; x++)
                    {
                        int i = z * size + x;
                        if (w.WaterMask[i] == 0 && w.Heights[i] < (L + 0.77) * 256 && w.RoadMask[i] == 0) below++;
                        if (x < 1 || z < 1 || x >= size - 1 || z >= size - 1) continue;
                        int m = Math.Max(Math.Max(w.Heights[i + 1], w.Heights[i - 1]), Math.Max(w.Heights[i + size], w.Heights[i - size]));
                        if (w.Heights[i] - m > 0.6 * 256) peaks++;
                    }
            }
        return (below, peaks);
    }

    internal static void ExportProject(WorldData w, string dir, Point a, Point b)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(w.Folder)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
        var raw = new byte[w.Heights.Length * 2];
        Buffer.BlockCopy(w.Heights, 0, raw, 0, raw.Length);
        File.WriteAllBytes(Path.Combine(dir, "dtm.raw"), raw);
        int n = w.Size, half = w.Half;
        var px = new byte[n * n * 4];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                byte v = w.WaterMask[z * n + x];
                if (v == 0) continue;
                px[((n - 1 - z) * n + x) * 4] = v;   // row 0 = north, blue = level
            }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(n, n, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, n * 4)));
        using (var fs = File.Create(Path.Combine(dir, "splat4.png"))) enc.Save(fs);
        File.WriteAllText(Path.Combine(dir, "lakes.txt"), string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2},{3}", a.X, a.Y, b.X, b.Y));
    }

    /// dry cells beside water that lie below its level + 0.5 m around a place (radius r): there the water would spill over in the game
    internal static int Spills(WorldData w, Point c, int r)
    {
        int count = 0;
        for (int z = (int)c.Y - r; z <= (int)c.Y + r; z++)
            for (int x = (int)c.X - r; x <= (int)c.X + r; x++)
            {
                if (!w.Contains(x - 1, z - 1) || !w.Contains(x + 1, z + 1) || w.WaterMask[w.Index(x, z)] != 0) continue;
                int L = Math.Max(Math.Max(w.WaterMask[w.Index(x + 1, z)], w.WaterMask[w.Index(x - 1, z)]), Math.Max(w.WaterMask[w.Index(x, z + 1)], w.WaterMask[w.Index(x, z - 1)]));
                if (L != 0 && w.HeightAt(x, z) < L + 0.5) count++;
            }
        return count;
    }

    /// whole map for checking by eye: relief, water blue, towns grey, planned roads red (bridges thick), bridge places yellow
    internal static void DrawOverview(WorldData w, string file, NetworkPlan plan, List<Point> spots)
    {
        int W = 1400; double sc = W / (double)w.Size;
        var px = new byte[W * W * 4];
        for (int y = 0; y < W; y++)
            for (int x = 0; x < W; x++)
            {
                int wx = (int)(-w.Half + x / sc), wz = (int)(w.Half - 1 - y / sc);
                double hx = w.HeightAt(Math.Min(w.Half - 1, wx + 3), wz) - w.HeightAt(Math.Max(-w.Half, wx - 3), wz), hz = w.HeightAt(wx, Math.Min(w.Half - 1, wz + 3)) - w.HeightAt(wx, Math.Max(-w.Half, wz - 3));
                byte v = (byte)Math.Clamp(170 + 15 * (hz - hx), 30, 250);
                int k = (y * W + x) * 4;
                if (w.WaterAt(wx, wz)) { px[k] = 230; px[k + 1] = 120; px[k + 2] = 40; }
                else px[k] = px[k + 1] = px[k + 2] = v;
                px[k + 3] = 255;
            }
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(BitmapSource.Create(W, W, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, W * 4), new Rect(0, 0, W, W));
            Point S(Point p) => new((p.X + w.Half) * sc, (w.Half - p.Y) * sc);
            foreach (var t in w.Prefabs.Where(p => p.IsTile))
                dc.DrawRectangle(System.Windows.Media.Brushes.DimGray, null, new Rect(S(new Point(t.X, t.Z + t.SizeZ)), S(new Point(t.X + t.SizeX, t.Z))));
            var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Red, 2);
            foreach (var r in plan.Roads)
                for (int q = 1; q < r.Points.Count; q++) dc.DrawLine(pen, S(r.Points[q - 1]), S(r.Points[q]));
            foreach (var p in spots) dc.DrawEllipse(System.Windows.Media.Brushes.Yellow, null, S(p), 4, 4);
            foreach (var b in plan.Roads.SelectMany(r => r.Bridges)) dc.DrawEllipse(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.Lime, 3), S(b.Center), 9, 9);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        var bmp = new RenderTargetBitmap(W, W, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    /// surface around a place for checking by eye (4 px per m): asphalt dark grey, gravel brown, water blue, ground green, bridge pieces outlined
    internal static void DrawSurface(WorldData w, string file, Point c, int r)
    {
        int W = 8 * r;
        var px = new byte[W * W * 4];
        for (int y = 0; y < W; y++)
            for (int x = 0; x < W; x++)
            {
                int wx = (int)Math.Floor(c.X - r + x / 4.0), wz = (int)Math.Floor(c.Y + r - y / 4.0);
                int k = (y * W + x) * 4;
                byte b, g, rr;
                int road = w.RoadAt(wx, wz);
                if (road == WorldData.Asphalt) (b, g, rr) = ((byte)70, (byte)70, (byte)70);
                else if (road == WorldData.Gravel) (b, g, rr) = ((byte)60, (byte)130, (byte)170);
                else if (w.WaterAt(wx, wz)) (b, g, rr) = ((byte)220, (byte)120, (byte)40);
                else (b, g, rr) = ((byte)90, (byte)160, (byte)90);
                px[k] = b; px[k + 1] = g; px[k + 2] = rr; px[k + 3] = 255;
            }
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(BitmapSource.Create(W, W, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, W * 4), new Rect(0, 0, W, W));
            Point S(double x, double z) => new((x - c.X + r) * 4, (c.Y + r - z) * 4);
            foreach (var p in w.Prefabs.Where(p => p.Name.StartsWith("yamee_part_bridge")))
                dc.DrawRectangle(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.Red, 2), new Rect(S(p.X, p.Z + p.SizeZ), S(p.X + p.SizeX, p.Z)));
        }
        var bmp = new RenderTargetBitmap(W, W, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(dv);
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    /// picture for checking by eye: relief, water blue, line red
    internal static void Draw(WorldData w, string file, List<Point> line)
    {
        double x0 = line.Min(p => p.X) - 120, x1 = line.Max(p => p.X) + 120, z0 = line.Min(p => p.Y) - 120, z1 = line.Max(p => p.Y) + 120;
        double sc = 1200 / Math.Max(x1 - x0, z1 - z0);
        int W = (int)((x1 - x0) * sc), H = (int)((z1 - z0) * sc);
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int wx = (int)(x0 + x / sc), wz = (int)(z1 - y / sc);
                double hx = w.HeightAt(wx + 1, wz) - w.HeightAt(wx - 1, wz), hz = w.HeightAt(wx, wz + 1) - w.HeightAt(wx, wz - 1);
                byte v = (byte)Math.Clamp(170 + 40 * (hz - hx), 30, 250);
                int k = (y * W + x) * 4;
                if (w.WaterAt(wx, wz)) { px[k] = 230; px[k + 1] = (byte)(v * 0.5); px[k + 2] = (byte)(v * 0.3); }
                else px[k] = px[k + 1] = px[k + 2] = v;
                px[k + 3] = 255;
            }
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(BitmapSource.Create(W, H, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, W * 4), new Rect(0, 0, W, H));
            Point S(Point p) => new((p.X - x0) * sc, (z1 - p.Y) * sc);
            for (int q = 1; q < line.Count; q++) dc.DrawLine(new System.Windows.Media.Pen(System.Windows.Media.Brushes.Red, 1), S(line[q - 1]), S(line[q]));
        }
        var bmp = new RenderTargetBitmap(W, H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    /// cross sections at 25 %, 50 %, 75 % of the river: height above the level every 4 m from −60 to +60 m (W = water)
    internal static void Section(WorldData w, List<Point> line, int level)
    {
        foreach (double f in new[] { 0.25, 0.5, 0.75 })
        {
            int k = (int)(line.Count * f);
            var d = line[k + 1] - line[k - 1]; d.Normalize();
            var nrm = new Vector(-d.Y, d.X);
            var sb = new System.Text.StringBuilder($"       section {f:P0}: ");
            for (int s = -60; s <= 60; s += 4)
            {
                var p = line[k] + nrm * s;
                int x = (int)Math.Floor(p.X), z = (int)Math.Floor(p.Y);
                sb.Append(w.WaterAt(x, z) ? "W" : "").Append((w.HeightAt(x, z) - level).ToString("0.0")).Append(' ');
            }
            Console.WriteLine(sb);
        }
    }

    /// two points 600–800 m apart, ground differs by at most 4 m, the highest point between them as low as possible
    static (Point, Point) LowPair(WorldData w)
    {
        (Point, Point) best = default; double bestRidge = double.MaxValue;
        for (int ax = -700; ax <= 700; ax += 100)
            for (int az = -700; az <= 700; az += 100)
                for (int k = 0; k < 12; k++)
                {
                    double ang = k * Math.PI / 6, r = 700;
                    double bx = ax + r * Math.Cos(ang), bz = az + r * Math.Sin(ang);
                    if (Math.Abs(bx) > 750 || Math.Abs(bz) > 750) continue;
                    double ga = w.HeightAt(ax, az), gb = w.HeightAt((int)bx, (int)bz);
                    if (Math.Abs(ga - gb) > 4 || w.WaterAt(ax, az) || w.WaterAt((int)bx, (int)bz)) continue;
                    double ridge = 0;
                    for (int s = 1; s < 20; s++) { double dh = w.HeightAt((int)(ax + (bx - ax) * s / 20), (int)(az + (bz - az) * s / 20)) - Math.Min(ga, gb); ridge = Math.Max(ridge, Math.Max(dh, -3 * dh)); }
                    if (ridge < bestRidge) { bestRidge = ridge; best = (new Point(ax, az), new Point(bx, bz)); }
                }
        return best;
    }

    [Test("River: a narrow river (6–8 m, the smallest width of the tool) is built without error and stays narrow; undo exact")]
    static void NarrowRiver()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        // like the bridge tests: two lakes 600 m apart, the river on a straight line between them
        Point a = new(-300, -100), b = new(300, -100);
        int lv = (int)Math.Floor(Math.Min(w.HeightAt(-300, -100), w.HeightAt(300, -100))) - 2;
        e.Run("lake A", "lake A", ed => Lake.Place(w, ed, new LakeSpec { Level = lv, Bumps = 0, Outline = Circle(a.X, a.Y, 60) }));
        e.Run("lake B", "lake B", ed => Lake.Place(w, ed, new LakeSpec { Level = lv, Bumps = 0, Outline = Circle(b.X, b.Y, 60) }));
        string before = Fixture.State(w);
        RiverStats st = null;
        e.Run("river", "river", ed => st = River.Build(w, ed, new RiverSpec { ClickA = a, ClickB = b, Line = new() { a, new(0, -100), b }, WidthMin = 6, WidthMax = 8, Seed = 3 }));
        Check.True(st != null && st.Water > 0, "river has water");
        // width of the water across the river away from the lakes (x −150 … 150): at least 4 m, at most 12 m
        int narrowest = int.MaxValue, widest = 0;
        for (int x = -150; x <= 150; x += 10)
        {
            int n = 0;
            for (int z = -160; z <= -40; z++) if (w.WaterAt(x, z)) n++;
            narrowest = Math.Min(narrowest, n); widest = Math.Max(widest, n);
        }
        Console.WriteLine($"       water across the river: {narrowest}–{widest} m, {st.Water} m² in all");
        Check.True(narrowest >= 4, $"river continuous ({narrowest} m)");
        Check.True(widest <= 12, $"river stays narrow ({widest} m; before at least 10 m)");
        e.Undo();
        Check.Equal(before, Fixture.State(w), "undo exact");
    }

    [Test("River: connects two lakes with one level, no dry land below the waterline, undo exact")]
    static void RiverBetweenLakes()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        // two lakes about 700 m apart in low land with similar height (lowest ridge on the straight line between them)
        var (a, b) = LowPair(w);
        int la = (int)Math.Floor(w.HeightAt((int)a.X, (int)a.Y)) - 2;
        int lb = Math.Min(la + 4, (int)Math.Floor(w.HeightAt((int)b.X, (int)b.Y)) - 2);
        if (lb < la) (a, b, la, lb) = (b, a, lb, la);
        e.Run("lake A", "lake A", ed => Lake.Place(w, ed, new LakeSpec { Level = la, Depth = 6, Shore = 14, Bumps = 0, Seed = 1, Outline = Circle(a.X, a.Y, 70) }));
        e.Run("lake B", "lake B", ed => Lake.Place(w, ed, new LakeSpec { Level = lb, Depth = 6, Shore = 14, Bumps = 0, Seed = 2, Outline = Circle(b.X, b.Y, 70) }));
        string before = Fixture.State(w);
        var lakeA = River.FindLake(w, a.X, a.Y);
        var lakeB = River.FindLake(w, b.X, b.Y);
        Check.True(lakeA != null && lakeB != null, "lakes found");
        Check.Equal(la, lakeA.Level, "level of lake A");
        Check.Equal(lb, lakeB.Level, "level of lake B");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        River.Log = Console.WriteLine;
        var plan = River.Plan(w, lakeA, lakeB, 7);
        long planMs = sw.ElapsedMilliseconds;
        Check.True(plan.Problem == null, "path found: " + plan.Problem);
        Check.Equal(la, plan.Level, "common level");
        // YAMEE_TEST_RIVER_OUT = folder: the world with the two lakes (before the river) is written there as a project, plus lakes.txt with a point in each lake
        string outDir = Environment.GetEnvironmentVariable("YAMEE_TEST_RIVER_OUT");
        if (outDir != null) ExportProject(w, outDir, a, b);
        string img = Environment.GetEnvironmentVariable("YAMEE_TEST_IMAGES");
        if (img != null) Draw(w, Path.Combine(img, "river_before.png"), plan.Line);
        var spec = new RiverSpec { ClickA = a, ClickB = b, Line = plan.Line, Seed = 7 };
        RiverStats st = null;
        sw.Restart();
        e.Run("river", "river", ed => st = River.Build(w, ed, spec));
        Console.WriteLine($"       plan {planMs} ms, build {sw.ElapsedMilliseconds} ms: {plan.Line.Count} points, {st.Length:0} m, water {st.Water} m², changed {st.Changed}, " +
                          $"deepest cut {st.MaxCut:0.0} m, embankment {st.Embankment}, lake lowered by {st.LoweredBy} m ({st.Lowered} cells)");
        if (img != null) { Draw(w, Path.Combine(img, "river_after.png"), plan.Line); Section(w, River.Dense(plan.Line, 1), la); }
        // both lakes and the river are one water with level la
        var all = River.FindLake(w, a.X, a.Y);
        Check.True(all.Cells.Contains(w.Index((int)b.X, (int)b.Y)), "lake B is reached from lake A over water");
        Check.True(all.Cells.All(i => w.WaterMask[i] == la), "one level");
        // no dry cell beside the water below the waterline, every water cell below the waterline
        int size = w.Size, bad = 0, high = 0;
        foreach (int i in all.Cells)
        {
            if (w.Heights[i] >= (la + Lake.WetMargin) * 256) high++;
            foreach (int n in new[] { i + 1, i - 1, i + size, i - size })
                if (w.WaterMask[n] == 0 && w.Heights[n] < (la + Lake.WetMargin) * 256 && w.RoadMask[n] == 0) bad++;
        }
        Check.Equal(0, high, "water cells above the waterline");
        Check.Equal(0, bad, "dry cells below the waterline beside the water");
        // greatest step between neighbors on new dry land along the river (no cliffs from the shaping)
        e.Undo();
        Check.Equal(before, Fixture.State(w), "after undo");
    }

    [Test("World generator: rivers connect two lakes with one level, no dry land below the waterline, same seed = same world")]
    static void GeneratorRivers()
    {
        WorldGenerator gen = null;
        WorldGenerator.RiverLog = s => Console.WriteLine("       " + s);
        string dir = null, usedSeed = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (string seed in (Environment.GetEnvironmentVariable("YAMEE_TEST_RIVER_SEEDS") ?? "Flussi,Bruecke,Kawuno").Split(','))
        {
            dir = Path.Combine(Fixture.Temp, "rivers_" + seed, "Test");
            int genSize = int.Parse(Environment.GetEnvironmentVariable("YAMEE_TEST_RIVER_SIZE") ?? "4096");
            var spec = new GeneratorSpec { Name = "Test", Size = genSize, Seed = seed, Lakes = 2, Rivers = 2, SpawnCount = 8 };
            gen = new WorldGenerator(spec);
            usedSeed = seed;
            gen.Write(dir, null, new Progress<string>());
            Console.WriteLine($"       {seed}: {gen.LakeList.Count} lakes, {gen.RiverList.Count} river(s) " +
                string.Join(", ", gen.RiverList.Select(r => $"{r.Length:0} m L{r.Level} cut {r.MaxCut:0.0}")) + $" ({sw.ElapsedMilliseconds} ms)");
            sw.Restart();
            if (gen.RiverList.Count > 0 && Environment.GetEnvironmentVariable("YAMEE_TEST_RIVER_SEEDS") == null) break;
        }
        Check.True(gen.RiverList.Count > 0, "a river on one of the maps");
        var w = WorldData.Load(dir, null, new Progress<string>());
        int size = w.Size, half = w.Half;
        string img = Environment.GetEnvironmentVariable("YAMEE_TEST_IMAGES");
        int n = 0;
        foreach (var r in gen.RiverList)
        {
            var line = r.Line.Select(p => new Point(p.X - half, p.Y - half)).ToList();
            if (img != null) { Draw(w, Path.Combine(img, $"gen_river{++n}.png"), line); Section(w, River.Dense(line, 1), r.Level); }
            var lake = River.FindLake(w, line[0].X, line[0].Y);
            Check.True(lake != null, "water at the start of the river");
            Check.Equal(r.Level, lake.Level, "level");
            var set = new HashSet<int>(lake.Cells);
            Check.True(set.Contains(w.Index((int)Math.Floor(line[^1].X), (int)Math.Floor(line[^1].Y))), "end of the river reached over water");
            var mid = line[line.Count / 2];
            Check.True(set.Contains(w.Index((int)Math.Floor(mid.X), (int)Math.Floor(mid.Y))), "middle of the river is water of the same level");
            int high = 0, bad = 0;
            foreach (int i in lake.Cells)
            {
                if (w.Heights[i] >= (r.Level + Lake.WetMargin) * 256) high++;
                foreach (int nb in new[] { i + 1, i - 1, i + size, i - size })
                    if (w.WaterMask[nb] == 0 && w.Heights[nb] < (r.Level + Lake.WetMargin) * 256) bad++;
            }
            // thin walls ("gutter"): dry land at the water, but 8 m further out the land is below the water level again
            int thin = 0, shore = 0;
            foreach (int i in lake.Cells)
                foreach (int step in new[] { 1, -1, size, -size })
                {
                    if (w.WaterMask[i + step] != 0) continue;
                    shore++;
                    int far = i + 9 * step;
                    if (far >= 0 && far < w.Heights.Length && w.WaterMask[far] == 0 && w.Heights[far] < r.Level * 256) thin++;
                }
            Console.WriteLine($"       river {gen.RiverList.IndexOf(r) + 1}: shore cells {shore}, thin walls {thin} ({100.0 * thin / Math.Max(1, shore):0.0} %)");
            Check.True(thin <= shore / 200, $"thin walls at the water: {thin} of {shore}");
            Check.Equal(0, high, "water cells above the waterline");
            Check.Equal(0, bad, "dry cells below the waterline beside the water");
        }
        var (below, peaks) = FarWaterProblems(w, 0, 0, size - 1, size - 1);
        Console.WriteLine($"       far water: {gen.FarBanks} cells raised, dry below the far plane {below}, peaks at the water {peaks}");
        Check.Equal(0, below, "dry land below the far water plane in a chunk with water (water seen from afar over land)");
        Check.Equal(0, peaks, "single peaks at the water (spikes in the game)");
        // preview (coarse grid like in the window): shows rivers too
        var pv = new WorldGenerator(new GeneratorSpec { Name = "Test", Size = w.Size, Seed = usedSeed, Lakes = 2, Rivers = 2, SpawnCount = 8 });
        sw.Restart();
        pv.BuildCoarse(w.Size / 420.0);
        Console.WriteLine($"       preview: {pv.LakeList.Count} lakes, {pv.RiverList.Count} river(s) ({sw.ElapsedMilliseconds} ms)");
        // the preview shows the same rivers as the world (decisions on the plan terrain, the same in both)
        Check.Equal(gen.RiverList.Count, pv.RiverList.Count, "rivers in the preview");
        for (int k = 0; k < Math.Min(gen.RiverList.Count, pv.RiverList.Count); k++)
        {
            var (rw, rp) = (gen.RiverList[k], pv.RiverList[k]);
            Check.True((rw.Line[0] - rp.Line[0]).Length < 2 && (rw.Line[^1] - rp.Line[^1]).Length < 2 && Math.Abs(rw.Length - rp.Length) < 5,
                $"river {k + 1} in the preview like in the world ({rp.Length:0} / {rw.Length:0} m)");
        }
        if (Environment.GetEnvironmentVariable("YAMEE_TEST_RIVER_COMPARE") != null)
        {
            string L(WorldGenerator.LakeInfo l) => $"({l.X:0},{l.Z:0}) r{l.Radius:0} L{l.Level}";
            for (int k = 0; k < Math.Max(gen.LakeList.Count, pv.LakeList.Count); k++)
                Console.WriteLine($"         lake {k + 1}: world {(k < gen.LakeList.Count ? L(gen.LakeList[k]) : "-")}  preview {(k < pv.LakeList.Count ? L(pv.LakeList[k]) : "-")}");
            foreach (var (name, g) in new[] { ("world", gen), ("preview", pv) })
                foreach (var r in g.RiverList)
                    Console.WriteLine($"         {name} river {r.LakeA}-{r.LakeB}: {r.Line[0].X:0},{r.Line[0].Y:0} → {r.Line[^1].X:0},{r.Line[^1].Y:0} {r.Length:0} m L{r.Level}");
        }
        if (img != null)
        {
            var px = pv.RenderPreview(out int pn);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(pn, pn, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, pn * 4)));
            using var fs = File.Create(Path.Combine(img, "gen_preview.png"));
            enc.Save(fs);
        }
        // same start value = same world (also with rivers)
        string again = Path.Combine(Fixture.Temp, "rivers_again", "Test");
        var spec2 = new GeneratorSpec { Name = "Test", Size = w.Size, Seed = usedSeed, Lakes = 2, Rivers = 2, SpawnCount = 8 };
        new WorldGenerator(spec2).Write(again, null, new Progress<string>());
        foreach (var f in new[] { "dtm.raw", "splat4.png" })
            Check.Equal(Check.Hash(Path.Combine(dir, f)), Check.Hash(Path.Combine(again, f)), f);
    }

    [Test("Towns at rivers: main part on one bank, district on the other, roads to a bridge (needs the game folder and the mod YAMEE_Bridges)")]
    static void RiverTowns()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? MapEditor.GameLocator.Find()?.Folder;
        if (game == null) Check.Skip("game folder not found");
        var files = PrefabCatalog.AllFiles(game);
        if (!files.ContainsKey(Bridge.EndName) || !files.ContainsKey(Bridge.TileName)) Check.Skip("mod YAMEE_Bridges not in the game folder");
        PrefabDef Part(string n) { var d = PrefabCatalog.Describe(files[n]); d.Kind = PrefabKind.Part; return d; }
        var endDef = Part(Bridge.EndName); var tileDef = Part(Bridge.TileName);
        int size = int.Parse(Environment.GetEnvironmentVariable("YAMEE_TEST_RIVER_SIZE") ?? "4096");
        string seed = Environment.GetEnvironmentVariable("YAMEE_TEST_RIVER_SEEDS") ?? "Kawuno";
        string dir = Path.Combine(Fixture.Temp, "rivertown_" + seed, "Test");
        new WorldGenerator(new GeneratorSpec { Name = "Test", Size = size, Seed = seed, Lakes = 2, Rivers = 2, SpawnCount = 8 }).Write(dir, null, new Progress<string>());
        var w = WorldData.Load(dir, game, new Progress<string>());
        var rules = CityRules.Load(game, w.Size);
        var defs = files.Keys.Where(n => n.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(n => n, n => { var d = PrefabCatalog.Describe(files[n]); d.Kind = PrefabKind.Tile; return d; }, StringComparer.OrdinalIgnoreCase);
        var spec = new CitySpec { Types = rules.Types.Select(t => t.Clone()).ToList(), Seed = seed, FillLots = false, TradersPerBiome = 0, RiverTowns = Environment.GetEnvironmentVariable("YAMEE_TEST_NORIVERTOWN") == null };
        var gen = new CityGenerator(w, spec, rules, defs.Keys, n => defs.TryGetValue(n, out var d) && d.Found ? d : null)
        { BridgeEndLength = endDef.SizeX, BridgeTerrainColumns = BridgePiece.TerrainColumns(files[Bridge.EndName]) };
        if (Environment.GetEnvironmentVariable("YAMEE_TEST_LOG") != null) CityGenerator.Log = s => { if (s.StartsWith("river") || s.StartsWith("  ") || s.StartsWith("sites")) Console.WriteLine("         " + s); };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plans = gen.Plan(new Progress<string>());
        CityGenerator.Log = null;
        var rp = plans.Where(p => p.Bridge != null).ToList();
        foreach (var pr in gen.Problems) Console.WriteLine("         problem: " + pr);
        Console.WriteLine($"       {plans.Count} towns, {rp.Count} at a river ({sw.ElapsedMilliseconds} ms): " +
            string.Join("; ", rp.Select(p => $"{p.Type.Label} {p.Tiles.Count} tiles, bridge {p.Bridge.Center.X:0},{p.Bridge.Center.Y:0} {(p.Bridge.AlongX ? "W–E" : "N–S")}")));
        Check.True(rp.Count >= 1, "a town at a river");
        string before = Fixture.State(w);
        var e = new WorldEdits(w);
        int prefabs = w.Prefabs.Count;
        e.Run("towns", "", ed =>
        {
            ed.GuardBanks();
            foreach (var p in plans) { foreach (var t in p.Tiles) { ed.AddPrefab(t); ed.AddTileSurface(t); } foreach (var q in p.Parts) ed.AddPrefab(q); CityGenerator.ShapeTerrain(w, ed, p, spec.Earthwork); }
            foreach (var p in rp) { ed.KeepBanksNow = false; Bridge.Build(w, ed, p.Bridge, endDef, tileDef); ed.KeepBanksNow = true; }
            foreach (var p in plans) foreach (var road in p.Roads) { var r = CityGenerator.PlanRoad(w, road, spec.RoadGrade); if (r != null) RoadApplier.Apply(w, ed, r, new Progress<string>()); }
        });
        string img = Environment.GetEnvironmentVariable("YAMEE_TEST_IMAGES");
        foreach (var p in rp)
        {
            var b = p.Bridge;
            var c = b.Center;
            if (img != null) Draw(w, Path.Combine(img, $"rivertown_{c.X:0}_{c.Y:0}.png"), p.Roads.SelectMany(r => r.Points).Append(new Point(p.X0, p.Z0)).Append(new Point(p.X1, p.Z1)).ToList());
            Check.True(w.Prefabs.Count(q => q.Name.StartsWith("yamee_part_bridge") && Math.Abs(q.X + 5 - c.X) < b.Length && Math.Abs(q.Z + 5 - c.Y) < b.Length) == b.Tiles + 2, "bridge pieces");
            // tiles on both banks: some on each side of the river (along the span)
            double mid = b.AlongX ? c.X : c.Y;
            int lo = p.Tiles.Count(t => (b.AlongX ? t.X + 75 : t.Z + 75) < mid), hi = p.Tiles.Count - lo;
            Check.True(lo > 0 && hi > 0, $"tiles on both banks ({lo}/{hi})");
            // no water under a tile
            foreach (var t in p.Tiles)
                for (int z = t.Z; z < t.Z + 150; z += 5)
                    for (int x = t.X; x < t.X + 150; x += 5) Check.True(!w.WaterAt(x, z), $"water under tile {t.Name} at {x},{z}");
            // the two roads end on the approaches at their height, the water still flows under the bridge
            var bridgeRoads = p.Roads.Where(r => r.HeightB != null).ToList();
            Check.Equal(2, bridgeRoads.Count, "roads to the bridge");
            foreach (var r in bridgeRoads)
            {
                var end = r.Points[^1];
                double h = w.HeightAt((int)Math.Floor(end.X), (int)Math.Floor(end.Y));
                Console.WriteLine("         road: " + string.Join(" ", r.Points.Select(q => $"{q.X:0},{q.Y:0}")));
                Console.WriteLine($"         road end {end.X:0},{end.Y:0}: {h:0.00} (approach {r.HeightB:0.00}), asphalt {w.RoadAt((int)Math.Floor(end.X), (int)Math.Floor(end.Y))}");
                Check.True(Math.Abs(h - r.HeightB.Value) < 0.3, $"road end at the approach height: {h:0.00} / {r.HeightB:0.00}");
            }
            int across = b.AlongX ? (int)c.Y : (int)c.X;
            int wet = 0;
            for (int s = b.S0; s < b.S0 + b.Length; s++) if (b.AlongX ? w.WaterAt(s, across) : w.WaterAt(across, s)) wet++;
            Check.Equal(b.WaterWidth, wet, "water under the bridge");
            Check.Equal(0, Spills(w, c, 150), "dry cells below the water level beside the water near the bridge");
        }
        e.Undo();
        Check.Equal(before, Fixture.State(w), "after undo");
        Check.Equal(prefabs, w.Prefabs.Count, "prefabs after undo");
    }

    [Test("Roads between towns cross rivers only on bridges (needs the game folder and the mod YAMEE_Bridges)")]
    static void RoadsOverRivers()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? MapEditor.GameLocator.Find()?.Folder;
        if (game == null) Check.Skip("game folder not found");
        var files = PrefabCatalog.AllFiles(game);
        if (!files.ContainsKey(Bridge.EndName) || !files.ContainsKey(Bridge.TileName)) Check.Skip("mod YAMEE_Bridges not in the game folder");
        PrefabDef Part(string n) { var d = PrefabCatalog.Describe(files[n]); d.Kind = PrefabKind.Part; return d; }
        var endDef = Part(Bridge.EndName); var tileDef = Part(Bridge.TileName);
        int size = int.Parse(Environment.GetEnvironmentVariable("YAMEE_TEST_RIVER_SIZE") ?? "4096");
        string seed = Environment.GetEnvironmentVariable("YAMEE_TEST_RIVER_SEEDS") ?? "Kawuno";
        string dir = Path.Combine(Fixture.Temp, "riverroads_" + seed, "Test");
        new WorldGenerator(new GeneratorSpec { Name = "Test", Size = size, Seed = seed, Lakes = 2, Rivers = 2, SpawnCount = 8 }).Write(dir, null, new Progress<string>());
        var w = WorldData.Load(dir, game, new Progress<string>());
        var rules = CityRules.Load(game, w.Size);
        var defs = files.Keys.Where(n => n.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(n => n, n => { var d = PrefabCatalog.Describe(files[n]); d.Kind = PrefabKind.Tile; return d; }, StringComparer.OrdinalIgnoreCase);
        var cspec = new CitySpec { Types = rules.Types.Select(t => t.Clone()).ToList(), Seed = seed, FillLots = false, TradersPerBiome = 0, RiverTowns = false };
        var plans = new CityGenerator(w, cspec, rules, defs.Keys, n => defs.TryGetValue(n, out var d) && d.Found ? d : null).Plan(new Progress<string>());
        var e = new WorldEdits(w);
        e.Run("towns", "", ed =>
        {
            ed.GuardBanks();
            foreach (var p in plans) { foreach (var t in p.Tiles) { ed.AddPrefab(t); ed.AddTileSurface(t); } foreach (var q in p.Parts) ed.AddPrefab(q); CityGenerator.ShapeTerrain(w, ed, p, cspec.Earthwork); }
            foreach (var p in plans) foreach (var road in p.Roads) { var r = CityGenerator.PlanRoad(w, road, cspec.RoadGrade); if (r != null) RoadApplier.Apply(w, ed, r, new Progress<string>()); }
        });
        RoadNetwork.TileDefs = n => defs.TryGetValue(n, out var d) && d.Found ? d : null;
        RoadNetwork.Bridges = new RoadNetwork.BridgeKit(endDef, tileDef, BridgePiece.TerrainColumns(files[Bridge.EndName]));
        try
        {
            if (Environment.GetEnvironmentVariable("YAMEE_TEST_LOG") != null) RoadNetwork.Log = s => { if (s.StartsWith("bridge") || s.Contains("crossing")) Console.WriteLine("         " + s); };
            var spec = new NetworkSpec { Seed = seed };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RoadNetwork.BridgeRejects.Clear();
            var plan = new RoadNetwork(w, spec).Plan(new Progress<string>());
            RoadNetwork.Log = null;
            Console.WriteLine("       bridge places rejected (both plannings): " + string.Join(", ", RoadNetwork.BridgeRejects.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}")));
            var bridges = plan.Roads.SelectMany(r => r.Bridges).ToList();
            if (Environment.GetEnvironmentVariable("YAMEE_TEST_IMAGES") is string ovDir) DrawOverview(w, Path.Combine(ovDir, $"overview_{seed}.png"), plan, RoadNetwork.LastBridgeSpots);
            Console.WriteLine($"       {plans.Count} towns, {plan.Roads.Count} roads, {bridges.Count} bridge(s) ({sw.ElapsedMilliseconds} ms): "
                + string.Join("; ", bridges.Select(b => $"{b.Center.X:0},{b.Center.Y:0} {(b.AlongX ? "W–E" : "N–S")} {b.Tiles} piece(s)")));
            foreach (var pr in plan.Problems) Console.WriteLine("         problem: " + pr);
            Check.True(bridges.Count >= 1, "a road over a river");
            // the plan keeps the bridges when saved and loaded
            var text = NetworkPlanStore.Serialize(new NetworkPlanFile { Plan = plan, Spec = spec });
            var back = NetworkPlanStore.Deserialize(w, text, out string problem);
            Check.True(back != null, "plan loaded: " + problem);
            Check.Equal(bridges.Count, back.Plan.Roads.Sum(r => r.Bridges.Count), "bridges after loading");
            Check.Equal(bridges[0].S0, back.Plan.Roads.SelectMany(r => r.Bridges).First().S0, "bridge place after loading");
            // rerouted by hand (editor, like dragging the road): the new course crosses the river on a bridge again
            var editor = RoadNetwork.ForEditing(w, spec);
            var bridged = plan.Roads.First(r => r.Bridges.Count > 0);
            var oldLine = bridged.Points;
            var rerouted = editor.Reroute(plan, bridged, new List<Point>(), out string rp);
            Check.True(rerouted != null, "rerouted: " + rp);
            bridged.Points = rerouted;
            editor.Remeasure(bridged);
            Console.WriteLine($"       rerouted: {bridged.Bridges.Count} bridge(s) on the new course");
            Check.True(bridged.Bridges.Count >= 1, "bridge on the rerouted course");
            bridged.Points = oldLine;
            editor.Remeasure(bridged);
            Check.True(bridged.Bridges.Count >= 1, "bridge after restoring the course");

            string before = Fixture.State(w);
            int prefabs = w.Prefabs.Count;
            NetworkBuildResult res = null;
            sw.Restart();
            e.Run("roads", "", ed => res = NetworkBuilder.Build(w, ed, plan, spec, new Progress<string>()));
            Console.WriteLine($"       built {res.Roads} roads, {res.Bridges} bridge(s) in {sw.ElapsedMilliseconds} ms; " + string.Join(" | ", res.Warnings));
            Check.Equal(bridges.Count, res.Bridges, "bridges built");
            Check.True(!res.Warnings.Any(x => x.Contains("water") || x.Contains("yamee_part_bridge")), "no warning about water or bridge pieces");
            string img = Environment.GetEnvironmentVariable("YAMEE_TEST_IMAGES");
            foreach (var road in plan.Roads.Where(r => r.Bridges.Count > 0))
            {
                foreach (var b in road.Bridges)
                {
                    var c = b.Center;
                    if (img != null) Draw(w, Path.Combine(img, $"riverroad_{c.X:0}_{c.Y:0}.png"), road.Points.Where(p => (p - c).Length < 400).ToList());
                    if (img != null) DrawSurface(w, Path.Combine(img, $"surface_{c.X:0}_{c.Y:0}.png"), c, 110);
                    if (img != null)
                    {
                        // surface width across the straight line every 4 m in front of the low approach (asphalt + gravel)
                        var sb = new System.Text.StringBuilder("         surface width in front of the approach (every 4 m): ");
                        for (int d = 0; d <= 72; d += 4)
                        {
                            int s = b.S0 - Bridge.Approach - d, cnt = 0, asp = 0;
                            for (int tt = b.T0 - 12; tt <= b.T0 + 21; tt++) { int rd = b.AlongX ? w.RoadAt(s, tt) : w.RoadAt(tt, s); if (rd != 0) cnt++; if (rd == WorldData.Asphalt) asp++; }
                            sb.Append($"{asp}/{cnt} ");
                        }
                        Console.WriteLine(sb);
                    }
                    Check.Equal(b.Tiles + 2, w.Prefabs.Count(q => q.Name.StartsWith("yamee_part_bridge") && Math.Abs(q.X + 5 - c.X) < b.Length && Math.Abs(q.Z + 5 - c.Y) < b.Length), "bridge pieces");
                    int across = b.AlongX ? (int)c.Y : (int)c.X, wet = 0;
                    for (int s = b.S0; s < b.S0 + b.Length; s++) if (b.AlongX ? w.WaterAt(s, across) : w.WaterAt(across, s)) wet++;
                    Check.Equal(b.WaterWidth, wet, "water under the bridge");
                    Check.Equal(0, Spills(w, c, 150), "dry cells below the water level beside the water near the bridge");
                    int gx = b.AlongX ? b.S0 - 10 : b.T0, gz = b.AlongX ? b.T0 : b.S0 - 10;
                    Check.Equal(WorldData.Gravel, w.RoadAt(gx, gz), "gravel edge on the approach");
                    // the road meets the approach at its height, straight along the bridge
                    double t = b.T0 + 5, h = b.Deck - 1 + b.ApproachLift;
                    // course out of the valley along the straight piece (print only): height above the approach every 4 m
                    var lo = Enumerable.Range(1, 6).Select(k => { int s = b.S0 - Bridge.Approach - 4 * k; return w.HeightAt(b.AlongX ? s : (int)t, b.AlongX ? (int)t : s) - h; });
                    var hi = Enumerable.Range(1, 6).Select(k => { int s = b.S0 + b.Length + Bridge.Approach - 1 + 4 * k; return w.HeightAt(b.AlongX ? s : (int)t, b.AlongX ? (int)t : s) - h; });
                    Console.WriteLine("         beyond the approaches every 4 m: " + string.Join(" ", lo.Select(v => $"{v:+0.00;-0.00}")) + " | " + string.Join(" ", hi.Select(v => $"{v:+0.00;-0.00}")));
                    foreach (var jn in plan.Junctions.Concat(plan.CrossingPoints).Where(p => (p - c).Length < b.Length / 2.0 + 120)) Console.WriteLine($"         junction/crossing near the bridge at {jn.X:0},{jn.Y:0}");
                    foreach (int s in new[] { b.S0 - Bridge.Approach, b.S0 - Bridge.Approach - 4, b.S0 + b.Length + Bridge.Approach - 1, b.S0 + b.Length + Bridge.Approach + 3 })
                    {
                        int x = b.AlongX ? s : (int)t, z = b.AlongX ? (int)t : s;
                        double hh = w.HeightAt(x, z);
                        Console.WriteLine($"         bridge {c.X:0},{c.Y:0}: at {s - b.S0} along {hh - h:+0.00;-0.00} m, surface {w.RoadAt(x, z)}");
                        Check.True(Math.Abs(hh - h) < 0.3, $"road at the approach: {hh:0.00} / {h:0.00}");
                        Check.True(w.RoadAt(x, z) != 0, "asphalt at the approach");
                    }
                }
                // nowhere else water under the middle of the road
                for (int q = 1; q < road.Points.Count; q++)
                {
                    var a = road.Points[q - 1]; var d = road.Points[q] - a;
                    for (double f = 0; f < d.Length; f += 2)
                    {
                        var p = a + d * (f / d.Length);
                        if (RoadNetwork.NearBridge(road, p)) continue;
                        Check.True(!w.WaterAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)), $"water under the road at {p.X:0},{p.Y:0}");
                    }
                }
            }
            // gradient on the roadway near the bridges (no step from the valley down to the approach)
            e.Undo();
            Check.Equal(before, Fixture.State(w), "after undo");
            Check.Equal(prefabs, w.Prefabs.Count, "prefabs after undo");
        }
        finally { RoadNetwork.TileDefs = null; RoadNetwork.Bridges = null; RoadNetwork.Log = null; }
    }

    [Test("Route (asphalt and gravel) across a river takes a bridge (needs the game folder and the mod YAMEE_Bridges)")]
    static void RouteOverRiver()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? MapEditor.GameLocator.Find()?.Folder;
        if (game == null) Check.Skip("game folder not found");
        var files = PrefabCatalog.AllFiles(game);
        if (!files.ContainsKey(Bridge.EndName) || !files.ContainsKey(Bridge.TileName)) Check.Skip("mod YAMEE_Bridges not in the game folder");
        PrefabDef Part(string n) { var d = PrefabCatalog.Describe(files[n]); d.Kind = PrefabKind.Part; return d; }
        string seed = Environment.GetEnvironmentVariable("YAMEE_TEST_RIVER_SEEDS") ?? "Kawuno";
        string dir = Path.Combine(Fixture.Temp, "riverroute_" + seed, "Test");
        new WorldGenerator(new GeneratorSpec { Name = "Test", Size = 4096, Seed = seed, Lakes = 2, Rivers = 2, SpawnCount = 8 }).Write(dir, null, new Progress<string>());
        var w = WorldData.Load(dir, game, new Progress<string>());
        RoadNetwork.Bridges = new RoadNetwork.BridgeKit(Part(Bridge.EndName), Part(Bridge.TileName), BridgePiece.TerrainColumns(files[Bridge.EndName]));
        try
        {
            var spec = RouteTests.Spec();
            var net = RoadNetwork.ForRoutes(w, spec);
            var spots = RoadNetwork.LastBridgeSpots.ToList();
            Console.WriteLine($"       {spots.Count} bridge place(s)");
            Check.True(spots.Count > 0, "no bridge place on the map");
            foreach (bool gravel in new[] { false, true })
            {
                RouteVariant found = null;
                foreach (var c in spots)
                {
                    foreach (var u in new[] { new Vector(1, 0), new Vector(0, 1) })
                    {
                        RoadNetwork.NetEnd s0, e0;
                        try { s0 = RouteTests.FreeEnd(net, c - u * 260); e0 = RouteTests.FreeEnd(net, c + u * 260); }
                        catch (Exception) { continue; }
                        var s = RoadNetwork.Orient(s0, e0.P); var e = RoadNetwork.Orient(e0, s0.P);
                        var list = net.RouteVariants(s, e, Array.Empty<Point>(), gravel, gravel ? 5 : 16, null, out string problem);
                        Console.WriteLine($"       {(gravel ? "gravel" : "asphalt")} {c.X:0},{c.Y:0} {(u.X > 0 ? "W–E" : "N–S")}: " + (list.Count == 0 ? problem : string.Join(", ", list.Select(v => $"{v.Style} {v.Road.Length:0} m, {v.Road.Bridges.Count} bridge(s)"))));
                        found = list.FirstOrDefault(v => v.Road.Bridges.Count > 0);
                        if (found != null) break;
                    }
                    if (found != null) break;
                }
                Check.True(found != null, (gravel ? "gravel" : "asphalt") + " route without a bridge");
                var road = found.Road;
                var edits = new WorldEdits(w);
                string before = Fixture.State(w);
                int prefabs = w.Prefabs.Count;
                NetworkBuildResult res = null;
                var job = new RouteJob(); job.Roads.Add(road);
                edits.Run("route", "route", ed => res = RouteBuilder.Build(w, ed, job, spec, null));
                Console.WriteLine($"       built: {res.Bridges} bridge(s); " + string.Join(" | ", res.Warnings));
                Check.Equal(road.Bridges.Count, res.Bridges, "bridges built");
                Check.True(!res.Warnings.Any(x => x.Contains("water")), "warning about water");
                foreach (var b in road.Bridges)
                {
                    Check.Equal(b.Tiles + 2, w.Prefabs.Count(q => q.Name.StartsWith("yamee_part_bridge") && Math.Abs(q.X + 5 - b.Center.X) < b.Length && Math.Abs(q.Z + 5 - b.Center.Y) < b.Length), "bridge pieces");
                    double t = b.T0 + 5, h = b.Deck - 1 + b.ApproachLift;
                    foreach (int sv in new[] { b.S0 - Bridge.Approach, b.S0 - Bridge.Approach - 4, b.S0 + b.Length + Bridge.Approach - 1, b.S0 + b.Length + Bridge.Approach + 3 })
                    {
                        int x = b.AlongX ? sv : (int)t, z = b.AlongX ? (int)t : sv;
                        Check.True(Math.Abs(w.HeightAt(x, z) - h) < 0.3, $"road at the approach: {w.HeightAt(x, z):0.00} / {h:0.00}");
                        Check.True(w.RoadAt(x, z) != 0, "surface at the approach");
                    }
                    Check.Equal(0, Spills(w, b.Center, 150), "dry cells below the water level near the bridge");
                    // surface width in front of the approach (asphalt/all)
                    var sb = new System.Text.StringBuilder("         surface in front of the approach every 8 m: ");
                    for (int d = 0; d <= 72; d += 8)
                    {
                        int sv = b.S0 - Bridge.Approach - d, cnt = 0, asp = 0;
                        for (int tt = b.T0 - 12; tt <= b.T0 + 21; tt++) { int rd = b.AlongX ? w.RoadAt(sv, tt) : w.RoadAt(tt, sv); if (rd != 0) cnt++; if (rd == WorldData.Asphalt) asp++; }
                        sb.Append($"{asp}/{cnt} ");
                        if (d == 8) Check.True(cnt >= 7, $"surface 8 m in front of the approach only {cnt} m wide (no taper to the bridge)");
                    }
                    Console.WriteLine(sb);
                }
                for (int q = 1; q < road.Points.Count; q++)
                {
                    var a = road.Points[q - 1]; var d = road.Points[q] - a;
                    for (double f = 0; f < d.Length; f += 2)
                    {
                        var p = a + d * (f / d.Length);
                        if (RoadNetwork.NearBridge(road, p)) continue;
                        Check.True(!w.WaterAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)), $"water under the road at {p.X:0},{p.Y:0}");
                    }
                }
                edits.Undo();
                Check.Equal(before, Fixture.State(w), "after undo");
                Check.Equal(prefabs, w.Prefabs.Count, "prefabs after undo");
            }
        }
        finally { RoadNetwork.Bridges = null; }
    }

    [Test("River: water levels too far apart are refused")]
    static void RiverLevelsTooFar()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        Point a = new(-350, -150), b = new(350, 150);
        int la = (int)Math.Floor(w.HeightAt((int)a.X, (int)a.Y)) - 3;
        e.Run("lake A", "lake A", ed => Lake.Place(w, ed, new LakeSpec { Level = la, Bumps = 0, Outline = Circle(a.X, a.Y, 60) }));
        e.Run("lake B", "lake B", ed => Lake.Place(w, ed, new LakeSpec { Level = la + River.MaxLower + 3, Bumps = 0, Outline = Circle(b.X, b.Y, 60) }));
        var plan = River.Plan(w, River.FindLake(w, a.X, a.Y), River.FindLake(w, b.X, b.Y), 1);
        Check.True(plan.Problem != null, "refused");
    }

    [Test("River tool: a drawn line becomes a river valley like the generator (continuous, depth changes, no dry land below the waterline), undo exact")]
    static void DrawnRiver()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        var (a, b) = LowPair(w);
        var mid = new Point((a.X + b.X) / 2 + (b.Y - a.Y) * 0.15, (a.Y + b.Y) / 2 - (b.X - a.X) * 0.15);   // a bend
        var line = new List<Point> { a, mid, b };
        int level = (int)Math.Floor(Math.Min(w.HeightAt((int)a.X, (int)a.Y), w.HeightAt((int)b.X, (int)b.Y))) - 2;
        var old = (ushort[])w.Heights.Clone();
        string before = Fixture.State(w);
        RiverStats st = null;
        e.Run("river", "river", ed => st = River.BuildDrawn(w, ed, new RiverSpec { Line = line, WidthMin = 12, WidthMax = 18, Depth = 4, Seed = 5 }, level));
        Check.True(st != null && st.Water > 0, "river has water");
        Console.WriteLine($"       {st.Length:0} m, water {st.Water} m², changed {st.Changed}, cut {st.MaxCut:0.0} m, embankment {st.Embankment}");
        // continuous water along the line, one level; deepest bed every 10 m changes along the river
        var dense = River.Dense(line, 1);
        double minDeep = double.MaxValue, maxDeep = 0;
        int gaps = 0;
        for (int k = 20; k < dense.Count - 20; k += 10)
        {
            var d = dense[k + 1] - dense[k - 1]; d.Normalize();
            var nrm = new Vector(-d.Y, d.X);
            double deep = 0; bool any = false;
            for (int s = -12; s <= 12; s++)
            {
                var p = dense[k] + nrm * s;
                int x = (int)Math.Floor(p.X), z = (int)Math.Floor(p.Y);
                if (w.WaterMask[w.Index(x, z)] != level) continue;
                any = true;
                if (old[w.Index(x, z)] < level * 256) continue;   // the river runs through land that was lower before: it stays
                deep = Math.Max(deep, level - w.HeightAt(x, z));
            }
            if (!any) { gaps++; continue; }
            if (deep == 0) continue;
            minDeep = Math.Min(minDeep, deep); maxDeep = Math.Max(maxDeep, deep);
        }
        Console.WriteLine($"       deepest point across the river {minDeep:0.0}–{maxDeep:0.0} m");
        Check.Equal(0, gaps, "water continuous along the line");
        Check.True(maxDeep - minDeep > 0.8, $"depth changes along the river ({minDeep:0.0}–{maxDeep:0.0} m)");
        Check.True(maxDeep < 4 * 1.4 + 1.5, $"not deeper than the set depth with pools ({maxDeep:0.0} m)");
        var all = River.FindLake(w, mid.X, mid.Y);
        int size = w.Size, bad = 0, high = 0;
        foreach (int i in all.Cells)
        {
            if (w.Heights[i] >= (level + Lake.WetMargin) * 256) high++;
            foreach (int n in new[] { i + 1, i - 1, i + size, i - size })
                if (w.WaterMask[n] == 0 && w.Heights[n] < (level + Lake.WetMargin) * 256 && w.RoadMask[n] == 0) bad++;
        }
        Check.Equal(0, high, "water cells above the waterline");
        Check.Equal(0, bad, "dry cells below the waterline beside the water");
        int hf = w.Half;
        var (below, peaks) = FarWaterProblems(w, (int)line.Min(p => p.X) + hf - 60, (int)line.Min(p => p.Y) + hf - 60, (int)line.Max(p => p.X) + hf + 60, (int)line.Max(p => p.Y) + hf + 60);
        Console.WriteLine($"       far water: dry below the far plane {below}, peaks at the water {peaks}");
        Check.Equal(0, below, "dry land below the far water plane in a chunk with water");
        e.Undo();
        Check.Equal(before, Fixture.State(w), "undo exact");
    }

    /// shore measurements of a lake: step between a water cell and a dry neighbor, share of almost level cells near the waterline,
    /// dry cells below the waterline beside the water, mean width of the shallow water (≤ 1 m) per meter of shore
    internal static (double MaxStep, double MeanStep, double Flat, int Dry, double Shallow, double WetDry) Shore(WorldData w, int level, Point c, int r)
    {
        double E = level + Lake.WetMargin, maxStep = 0, sumStep = 0;
        int steps = 0, flat = 0, zone = 0, dry = 0, shallow = 0, wetDry = 0;
        for (int z = (int)c.Y - r; z <= (int)c.Y + r; z++)
            for (int x = (int)c.X - r; x <= (int)c.X + r; x++)
            {
                int i = w.Index(x, z);
                double h = w.Heights[i] / 256.0;
                bool wet = w.WaterMask[i] == level;
                if (wet && h <= level && h > level - 1) shallow++;   // shallow water: up to 1 m below the level
                if (wet && h > level) wetDry++;   // water pixel, but the ground lies above the level (no water to be seen, dirt instead of grass)
                if (Math.Abs(h - E) < 1.5 && w.RoadMask[i] == 0)
                {
                    zone++;
                    double g = 0;
                    foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) g = Math.Max(g, Math.Abs(w.HeightAt(x + dx, z + dz) - h));
                    if (g < 0.05) flat++;
                }
                if (!wet) continue;
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int j = w.Index(x + dx, z + dz);
                    if (w.WaterMask[j] != 0 || w.RoadMask[j] != 0) continue;
                    double hj = w.Heights[j] / 256.0;
                    if (hj < E) dry++;
                    double s = hj - h;
                    if (s > maxStep && Environment.GetEnvironmentVariable("YAMEE_TEST_SHORE_GRID") == "1" && s > 1.5) Console.WriteLine($"       step {s:0.00} m at {x},{z} (radius {Math.Sqrt((x - c.X) * (x - c.X) + (z - c.Y) * (z - c.Y)):0})");
                    maxStep = Math.Max(maxStep, s); sumStep += s; steps++;
                }
            }
        return (maxStep, steps > 0 ? sumStep / steps : 0, zone > 0 ? (double)flat / zone : 0, dry, steps > 0 ? (double)shallow / steps : 0, steps > 0 ? (double)wetDry / steps : 0);
    }

    /// mean |Laplacian| (m) of the ground between level − 0.5 and level + 2.5 m (stairs and teeth along the shore)
    internal static double ShoreRough(WorldData w, int level, Point c, int r)
    {
        double sum = 0; int n = 0;
        for (int z = (int)c.Y - r; z <= (int)c.Y + r; z++)
            for (int x = (int)c.X - r; x <= (int)c.X + r; x++)
            {
                double h = w.HeightAt(x, z);
                if (h < level - 0.5 || h > level + 2.5) continue;
                sum += Math.Abs(4 * h - w.HeightAt(x + 1, z) - w.HeightAt(x - 1, z) - w.HeightAt(x, z + 1) - w.HeightAt(x, z - 1));
                n++;
            }
        return n > 0 ? sum / n : 0;
    }

    [Test("Lake tool: the shore is a slope through the waterline (no wall, no level ring), beaches wider in shallow water than steep banks, the suggested level does not run out, undo exact")]
    static void LakeShore()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        Point c = new(0, 0);
        var spec = new LakeSpec { Depth = 5, Shore = 14, Bumps = 1, Seed = 11, Outline = Circle(c.X, c.Y, 120) };
        spec.Level = Lake.SuggestLevel(w, spec);
        var probe = Lake.Plan(w, spec);
        Console.WriteLine($"       suggested level {spec.Level} m (ground {w.HeightAt(0, 0):0.0} m): water {probe.Water} m², outside the outline {probe.Spread} m², runs out {probe.Leak} m²");
        Check.Equal(0, probe.Leak, "suggested level does not run out");
        string before = Fixture.State(w);
        var results = new List<(double MaxStep, double MeanStep, double Flat, int Dry, double Shallow, double WetDry)>();
        foreach (int style in new[] { 0, 1, 2 })
        {
            var s = spec.Clone(); s.ShoreStyle = style;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LakeStats st = null;
            e.Run("lake", "lake", ed => st = Lake.Place(w, ed, s));
            var m = Shore(w, s.Level, c, 200);
            var rough = ShoreRough(w, s.Level, c, 200);
            Console.WriteLine($"       style {style}: roughness at the waterline {rough:0.000} m");
            if (style == 2 && Environment.GetEnvironmentVariable("YAMEE_TEST_SHORE_GRID") == "1") ShoreLook.Print(w, c.X + 85, c.Y + 85, s.Level, 7);
            Check.True(rough < 0.16, $"smooth ground at the waterline like a generator lake (roughness {rough:0.000} m, generator lake in Buzovi-Land 0.156 m)");
            results.Add(m);
            Console.WriteLine($"       style {style}: {sw.ElapsedMilliseconds} ms, water {st.Water} m², deepest {st.MaxDepth:0.0} m, step at the waterline max {m.MaxStep:0.00} m / mean {m.MeanStep:0.00} m, " +
                              $"level cells near the waterline {m.Flat:P0}, shallow water {m.Shallow:0.0} m per m of shore, water pixels above the level {m.WetDry:0.0} m per m of shore, dry below the waterline {m.Dry}");
            Check.True(m.WetDry <= 4.5, $"narrow strip of water pixels above the level ({m.WetDry:0.0} m)");
            Check.True(st.Water > 0, "lake has water");
            Check.True(m.MaxStep <= 1.3, $"no wall at the waterline ({m.MaxStep:0.00} m)");
            Check.True(m.MeanStep <= 0.6, $"gentle waterline ({m.MeanStep:0.00} m on average)");
            Check.True(m.Flat < 0.12, $"no level ring at the waterline ({m.Flat:P0})");
            Check.Equal(0, m.Dry, "dry cells below the waterline beside the water");
            e.Undo();
            Check.Equal(before, Fixture.State(w), "undo exact");
        }
        Check.True(results[1].Shallow > results[2].Shallow * 1.5, $"beaches have more shallow water than steep banks ({results[1].Shallow:0.0} / {results[2].Shallow:0.0})");
    }

    [Test("Lake tool: the bed of a large lake varies (deeper middle, hollows, channels, shoals) – not one even floor")]
    static void LakeBed()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        var spec = new LakeSpec { Depth = 6, Shore = 14, Bumps = 0, Seed = 5, Outline = Circle(0, 0, 280) };
        spec.Level = Lake.SuggestLevel(w, spec);
        e.Run("lake", "lake", ed => Lake.Place(w, ed, spec));
        // water depth of the bed away from the shore (more than 40 m inside)
        var depths = new List<double>();
        for (int z = -240; z <= 240; z += 2)
            for (int x = -240; x <= 240; x += 2)
                if (x * x + z * z < 240 * 240 && w.WaterMask[w.Index(x, z)] == spec.Level) depths.Add(spec.Level + 1 - w.HeightAt(x, z));
        depths.Sort();
        double p10 = depths[depths.Count / 10], p50 = depths[depths.Count / 2], p90 = depths[depths.Count * 9 / 10];
        Console.WriteLine($"       level {spec.Level} m, water depth away from the shore: 10 % {p10:0.0} m, median {p50:0.0} m, 90 % {p90:0.0} m, deepest {depths[^1]:0.0} m");
        string img = Environment.GetEnvironmentVariable("YAMEE_TEST_IMAGES");
        if (img != null)
        {
            // water depth as a picture (light = shallow, dark = deep), relief shading on land
            int W = 640; var px = new byte[W * W * 4];
            for (int y = 0; y < W; y++)
                for (int x = 0; x < W; x++)
                {
                    int wx = x - W / 2, wz = W / 2 - y, k = (y * W + x) * 4;
                    double h = w.HeightAt(wx, wz);
                    if (w.WaterMask[w.Index(wx, wz)] != 0) { double t = Math.Clamp((spec.Level + 1 - h) / 12, 0, 1); px[k] = (byte)(255 - 140 * t); px[k + 1] = (byte)(200 - 160 * t); px[k + 2] = (byte)(120 - 110 * t); }
                    else { byte v = (byte)Math.Clamp(160 + 30 * (w.HeightAt(wx, wz + 1) - w.HeightAt(wx + 1, wz)), 40, 250); px[k] = px[k + 1] = px[k + 2] = v; }
                    px[k + 3] = 255;
                }
            Directory.CreateDirectory(img);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(W, W, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, W * 4)));
            using var fs = File.Create(Path.Combine(img, "lake_bed.png"));
            enc.Save(fs);
        }
        Check.True(p90 - p10 > 3, $"varied bed (10–90 % spread {p90 - p10:0.0} m)");
    }

    /// water depth as a picture (light = shallow, dark = deep), relief shading on land; W × W m around 0,0
    internal static void DrawLakeDepth(WorldData w, int level, string file, int W = 640)
    {
        var px = new byte[W * W * 4];
        for (int y = 0; y < W; y++)
            for (int x = 0; x < W; x++)
            {
                int wx = x - W / 2, wz = W / 2 - y, k = (y * W + x) * 4;
                double h = w.HeightAt(wx, wz);
                double sh = 30 * (w.HeightAt(wx, wz + 1) - w.HeightAt(wx + 1, wz));
                if (w.WaterMask[w.Index(wx, wz)] != 0) { double t = Math.Clamp((level + 1 - h) / 12, 0, 1); px[k] = (byte)Math.Clamp(255 - 140 * t + sh * 0.3, 0, 255); px[k + 1] = (byte)Math.Clamp(200 - 160 * t + sh * 0.3, 0, 255); px[k + 2] = (byte)Math.Clamp(120 - 110 * t + sh * 0.3, 0, 255); }
                else { byte v = (byte)Math.Clamp(160 + sh, 40, 250); px[k] = (byte)(v * 0.8); px[k + 1] = v; px[k + 2] = (byte)(v * 0.85); }
                px[k + 3] = 255;
            }
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(W, W, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, W * 4)));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    [Test("Lake tool: shapes on the bed (cliffs, cracks, rocks) and an island with a beach and a steep wall; no dry land below the waterline, smooth shores, undo exact")]
    static void LakeFeatures()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        var spec = new LakeSpec { Depth = 7, Shore = 14, Bumps = 1, Seed = 9, BedFeatures = 2, Island = true, Outline = Circle(0, 0, 280) };
        spec.Level = Lake.SuggestLevel(w, spec);
        var plan = Lake.Plan(w, spec);
        Console.WriteLine($"       level {spec.Level} m: {plan.Cliffs} cliffs, {plan.Cracks} cracks, {plan.Rocks} rock groups, island {plan.HasIsland}");
        Check.True(plan.Cliffs + plan.Cracks + plan.Rocks >= 3, "shapes on the bed");
        Check.True(plan.HasIsland, "island placed");
        string before = Fixture.State(w);
        e.Run("lake", "lake", ed => Lake.Place(w, ed, spec));
        string img = Environment.GetEnvironmentVariable("YAMEE_TEST_IMAGES");
        if (img != null) DrawLakeDepth(w, spec.Level, Path.Combine(img, "lake_features.png"));
        // the island: dry cells well inside the lake; gentle (beach) and steep (wall) parts
        int land = 0, gentle = 0, steep = 0;
        for (int z = -200; z <= 200; z++)
            for (int x = -200; x <= 200; x++)
            {
                if (x * x + z * z > 200 * 200 || w.WaterMask[w.Index(x, z)] != 0) continue;
                double h = w.HeightAt(x, z);
                if (h < spec.Level + Lake.WetMargin) continue;
                land++;
                double g = Math.Max(Math.Abs(w.HeightAt(x + 1, z) - w.HeightAt(x - 1, z)), Math.Abs(w.HeightAt(x, z + 1) - w.HeightAt(x, z - 1))) / 2;
                if (h < spec.Level + 3 && g < 0.2) gentle++;
                if (g > 0.9) steep++;
            }
        Console.WriteLine($"       island: {land} m² land, {gentle} m² gentle near the water (beach), {steep} m² steeper than 90 %");
        Check.True(land > 200, $"island has land ({land} m²)");
        Check.True(gentle > 20 && steep > 20, $"beach and steep wall ({gentle} / {steep} m²)");
        var m = Shore(w, spec.Level, new Point(0, 0), 300);
        if (Environment.GetEnvironmentVariable("YAMEE_TEST_SHORE_GRID") == "1")
            for (int z = -300; z <= 300; z++)
                for (int x = -300; x <= 300; x++)
                    if (w.WaterMask[w.Index(x, z)] == spec.Level && new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(o => w.WaterMask[w.Index(x + o.Item1, z + o.Item2)] == 0 && w.RoadMask[w.Index(x + o.Item1, z + o.Item2)] == 0 && w.HeightAt(x + o.Item1, z + o.Item2) - w.HeightAt(x, z) > 2)) { Console.WriteLine($"       big step at {x},{z}"); ShoreLook.Print(w, x, z, spec.Level, 5); x = z = 400; }
        double rough = ShoreRough(w, spec.Level, new Point(0, 0), 300);
        Console.WriteLine($"       shores: dry below the waterline {m.Dry}, step at the waterline max {m.MaxStep:0.00} m, roughness {rough:0.000} m");
        Check.Equal(0, m.Dry, "dry cells below the waterline beside the water");
        e.Undo();
        Check.Equal(before, Fixture.State(w), "undo exact");
    }

    [Test("Lake tool: a level above the land around runs out (red in the preview); with the embankment it stays in, no dry land below the waterline")]
    static void LakeDam()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        Point c = new(0, 0);
        var spec = new LakeSpec { Depth = 4, Shore = 14, Bumps = 0, Seed = 3, Outline = Circle(c.X, c.Y, 90) };
        spec.Level = Lake.SuggestLevel(w, spec) + 4;
        var plan = Lake.Plan(w, spec);
        Console.WriteLine($"       level {spec.Level} m: runs out {plan.Leak} m²");
        Check.True(plan.Leak > 0, "runs out without embankment");
        spec.Dam = true;
        var dam = Lake.Plan(w, spec);
        Console.WriteLine($"       with embankment: runs out {dam.Leak} m², raised up to {dam.MaxRaise:0.0} m");
        Check.Equal(0, dam.Leak, "embankment keeps the water in");
        var old = (ushort[])w.Heights.Clone();
        e.Run("lake", "lake", ed => Lake.Place(w, ed, spec));
        // embankment high enough that the water does not show over the land behind it from afar (crest 1.5 m above the waterline)
        double crest = 0; int raised = 0;
        for (int i = 0; i < old.Length; i++)
            if (w.Heights[i] > old[i] + 64 && w.WaterMask[i] == 0) { raised++; crest = Math.Max(crest, w.Heights[i] / 256.0 - spec.Level - Lake.WetMargin); }
        Console.WriteLine($"       embankment: {raised} m² raised, crest {crest:0.00} m above the waterline");
        Check.True(crest >= 1.4, $"crest high enough ({crest:0.00} m)");
        Check.Equal(0, Shore(w, spec.Level, c, 160).Dry, "dry cells below the waterline beside the water");
    }
}

static class BridgeTests
{
    [Test("Bridge: river made straight and as wide as the bridge, bank a full block below the deck, pieces placed, undo exact")]
    static void BridgeOverRiver()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        // a straight river west–east between two lakes on the same level
        Point a = new(-300, -100), b = new(300, -100);
        int level = (int)Math.Floor(Math.Min(w.HeightAt(-300, -100), w.HeightAt(300, -100))) - 2;
        List<Point> Circle(Point c) => Enumerable.Range(0, 36).Select(k => new Point(c.X + 60 * Math.Cos(k * Math.PI / 18), c.Y + 60 * Math.Sin(k * Math.PI / 18))).ToList();
        e.Run("lake A", "lake A", ed => Lake.Place(w, ed, new LakeSpec { Level = level, Bumps = 0, Outline = Circle(a) }));
        e.Run("lake B", "lake B", ed => Lake.Place(w, ed, new LakeSpec { Level = level, Bumps = 0, Outline = Circle(b) }));
        e.Run("river", "river", ed => River.Build(w, ed, new RiverSpec { ClickA = a, ClickB = b, Line = new() { a, new(0, -100), b }, WidthMin = 20, WidthMax = 22 }));
        string outDir = Environment.GetEnvironmentVariable("YAMEE_TEST_BRIDGE_OUT");
        if (outDir != null) RiverTests.ExportProject(w, outDir, new Point(10, -100), new Point(10, -100));
        string before = Fixture.State(w);
        var site = Bridge.Find(w, 10, -100);
        Check.True(site.Problem == null, "bridge possible: " + site.Problem);
        Check.True(!site.AlongX, "river along x → bridge north–south");
        Console.WriteLine($"       river {site.RiverWidth:0} m wide, {site.RiverAngle:0.0}°, {site.Tiles} middle piece(s), water {site.WaterWidth} m, deck {site.Deck}, S0 {site.S0}, T0 {site.T0}");
        var endDef = new PrefabDef { SizeX = 13, SizeY = 18, SizeZ = 10, YOffset = -1, Found = true, Kind = PrefabKind.Part };
        var tileDef = new PrefabDef { SizeX = 10, SizeY = 18, SizeZ = 10, YOffset = -1, Found = true, Kind = PrefabKind.Part };
        BridgeStats st = null;
        int count = w.Prefabs.Count;
        e.Run("bridge", "bridge", ed => st = Bridge.Build(w, ed, site, endDef, tileDef));
        Console.WriteLine($"       changed {st.Changed}, water +{st.Water} / −{st.Dried}, pieces {st.Pieces}, approach {st.Approach} m²");
        Check.Equal(count + site.Tiles + 2, w.Prefabs.Count, "pieces");
        string img = Environment.GetEnvironmentVariable("YAMEE_TEST_IMAGES");
        if (img != null)
        {
            var c = site.Center;
            RiverTests.Draw(w, Path.Combine(img, "bridge.png"), new List<Point> { new(c.X, c.Y - 60), new(c.X, c.Y + 60) });
            for (int s = site.S0 - 25; s <= site.S0 + site.Length + 25; s += 2) Console.Write($"{w.HeightAt(site.T0 + 5, s) - level:0.0}{(w.WaterAt(site.T0 + 5, s) ? "W" : "")} ");
            Console.WriteLine();
        }
        // middle line of the bridge: water exactly under the middle pieces and 7 m under each end piece, bank = deck − 0.67 at the ends
        int x = site.T0 + 5, wet = 0;
        for (int s = site.S0; s < site.S0 + site.Length; s++) if (w.WaterAt(x, s)) wet++;
        Check.Equal(site.WaterWidth, wet, "water width under the bridge");
        // road (approach and under the bank part of the end pieces): whole meter below the deck, asphalt; beside the road bank = deck − 0.67
        int E = site.EndLength;
        foreach (int s in new[] { site.S0 + 3, site.S0 + E - 8, site.S0 + site.Length - E + 7, site.S0 + site.Length - 4, site.S0 - 10, site.S0 + site.Length + 10 })
        {
            Check.True(Math.Abs(w.HeightAt(x, s) - (site.Deck - 1 + (s < site.S0 || s >= site.S0 + site.Length ? site.ApproachLift : site.RoadLift))) < 0.005, $"road at {s}: {w.HeightAt(x, s):0.00}");
            Check.Equal(WorldData.Asphalt, w.RoadAt(x, s), $"asphalt at {s}");
        }
        // under the terrain blocks of the end pieces (outer 3 m): below deck − 1.5, so that the block above them is full air
        foreach (int s in new[] { site.S0, site.S0 + 2, site.S0 + site.Length - 3, site.S0 + site.Length - 1 })
            Check.True(Math.Abs(w.HeightAt(x, s) - (site.Deck - 1 + site.RoadLift - site.TerrainDrop)) < 0.005, $"under terrain blocks at {s}: {w.HeightAt(x, s):0.00}");
        Check.True(w.WaterAt(x, site.S0 + E - 7) && w.RoadAt(x, site.S0 + E - 7) == 0, "water right after the ground under the end piece");
        Check.True(w.HeightAt(site.T0 - 2, site.S0 + E - 8) < site.Deck - 1.5, "steps beside the bridge");
        Check.Equal(0, RiverTests.Spills(w, site.Center, 120), "dry cells below the water level beside the water");
        Check.Equal(WorldData.Gravel, w.RoadAt(site.T0, site.S0 - 10), "gravel edge on the approach (towards the pillars)");
        Check.Equal(WorldData.Gravel, w.RoadAt(site.T0 + 9, site.S0 + site.Length + 10), "gravel edge on the other approach");
        Check.Equal(WorldData.Asphalt, w.RoadAt(site.T0 + 1, site.S0 - 10), "asphalt inside the gravel edge");
        Check.True(w.RoadAt(site.T0 - 1, site.S0 - 10) == 0, "no surface beside the approach");
        // middle pieces right after the end piece (13 m)
        var placed = w.Prefabs.Skip(count).ToList();
        Check.Equal(site.S0 + E, placed[1].Z, "first middle piece after the end piece");
        Check.True(Math.Abs(w.HeightAt(site.T0 - 2, site.S0) - (site.Deck - 1 + site.ApproachLift)) < 0.005, "bank beside the road at road height");
        // the river stays one water: from west of the bridge to east of it
        var lake = River.FindLake(w, a.X, a.Y);
        Check.True(lake.Cells.Contains(w.Index((int)b.X, (int)b.Y)), "river still connected under the bridge");
        // end pieces: outer end away from the river
        var pieces = w.Prefabs.Skip(count).ToList();
        Check.Equal(3, pieces[0].Rotation, "south end");
        Check.Equal(1, pieces[^1].Rotation, "north end");
        Check.Equal(site.Deck - 10, pieces[0].Y, "y");
        e.Undo();
        Check.Equal(before, Fixture.State(w), "after undo");
        Check.Equal(count, w.Prefabs.Count, "pieces after undo");
        // the same bridge for a gravel road: approaches gravel over the whole width, heights as with asphalt
        var gs = Bridge.Find(w, 10, -100);
        gs.Gravel = true;
        e.Run("gravel bridge", "gravel bridge", ed => Bridge.Build(w, ed, gs, endDef, tileDef));
        foreach (int s in new[] { gs.S0 - 10, gs.S0 + 3, gs.S0 + gs.Length - 4, gs.S0 + gs.Length + 10 })
            for (int t = gs.T0; t <= gs.T0 + 9; t++)
                Check.Equal(WorldData.Gravel, w.RoadAt(t, s), $"gravel at {t}/{s}");
        Check.True(Math.Abs(w.HeightAt(x, gs.S0 - 10) - (gs.Deck - 1 + gs.ApproachLift)) < 0.005, "gravel approach height");
        Check.Equal(count + gs.Tiles + 2, w.Prefabs.Count, "same pieces for gravel");
        e.Undo();
        Check.Equal(before, Fixture.State(w), "after undo of the gravel bridge");
    }

    [Test("River banks: varied profiles from flat meadows to steep walls, bridge spots remain")]
    static void RiverBankVariety()
    {
        string dir = Path.Combine(Fixture.Temp, "river_variety", "Test");
        var gen = new WorldGenerator(new GeneratorSpec { Name = "Test", Size = 4096, Seed = "Kawuno", Lakes = 2, Rivers = 2, SpawnCount = 8 });
        gen.Write(dir, null, new Progress<string>());
        Check.True(gen.RiverList.Count > 0, "at least one river");
        var w = WorldData.Load(dir, null, new Progress<string>());
        int half = w.Half;
        var r = gen.RiverList[0];
        var line = River.Dense(r.Line.Select(p => new Point(p.X - half, p.Y - half)).ToList(), 4);
        double minRise = double.MaxValue, maxRise = 0;
        int flatCount = 0, steepCount = 0, samples = 0, bridges = 0;
        for (int k = 15; k < line.Count - 15; k += 4)
        {
            var tg = line[k + 1] - line[k - 1];
            if (tg.Length < 1e-6) continue;
            tg.Normalize();
            var nrm = new Vector(-tg.Y, tg.X);
            if (k % 8 == 3 && Bridge.Find(w, line[k].X, line[k].Y, 13, 3, 35).Problem == null) bridges++;
            foreach (double sg in new[] { 1.0, -1.0 })
            {
                // find water edge along the normal
                double edge = -1;
                for (double d = 2; d <= 30; d += 1)
                {
                    var p = line[k] + nrm * (sg * d);
                    if (!w.Contains((int)p.X, (int)p.Y)) break;
                    if (!w.WaterAt((int)p.X, (int)p.Y)) { edge = d; break; }
                }
                if (edge < 0) continue;
                var p0 = line[k] + nrm * (sg * (edge + 1));
                var p1 = line[k] + nrm * (sg * (edge + 11));
                if (!w.Contains((int)p0.X, (int)p0.Y) || !w.Contains((int)p1.X, (int)p1.Y)) continue;
                if (w.WaterAt((int)p1.X, (int)p1.Y)) continue;
                double rise = w.HeightAt((int)p1.X, (int)p1.Y) - w.HeightAt((int)p0.X, (int)p0.Y);
                minRise = Math.Min(minRise, rise);
                maxRise = Math.Max(maxRise, rise);
                if (rise <= 1.2) flatCount++;
                if (rise >= 4.0) steepCount++;
                samples++;
            }
        }
        Console.WriteLine($"       bank rise over 10 m: {minRise:0.0}–{maxRise:0.0} m (flat <= 1.2 m: {flatCount}/{samples}, steep >= 4 m: {steepCount}/{samples}), bridge spots: {bridges}");
        Check.True(flatCount >= 10, $"flat riverside stretches present ({flatCount})");
        Check.True(steepCount >= 3, $"steep bank stretches present ({steepCount})");
        Check.True(bridges >= 3, $"bridge spots still found ({bridges})");
    }

    [Test("Bridge on slanted river: smooth riverbank transition without sharp corners, no spills, undo exact")]
    static void SlantedBridgeNoCorners()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        // create a straight 28°-slanted river between two lakes (like a diagonal river crossing)
        var a = new Point(-260, -240);
        var b = new Point(260, 40);
        int level = Math.Min((int)Math.Floor(w.HeightAt((int)a.X, (int)a.Y)), (int)Math.Floor(w.HeightAt((int)b.X, (int)b.Y))) - 4;
        e.Run("lake A", "", ed => Lake.Place(w, ed, new LakeSpec { Level = level, Depth = 6, Shore = 14, Bumps = 0, Seed = 1, Outline = RiverTests.Circle(a.X, a.Y, 60) }));
        e.Run("lake B", "", ed => Lake.Place(w, ed, new LakeSpec { Level = level, Depth = 6, Shore = 14, Bumps = 0, Seed = 2, Outline = RiverTests.Circle(b.X, b.Y, 60) }));
        var line = new List<Point> { a, new(-130, -170), new(0, -100), new(130, -30), b };
        e.Run("river", "", ed => River.Build(w, ed, new RiverSpec { ClickA = a, ClickB = b, Line = line, Seed = 3, WidthMin = 20, WidthMax = 22 }));
        string before = Fixture.State(w);
        var site = Bridge.Find(w, 0, -100, 13, 3, 35);
        Check.True(site.Problem == null, "slanted bridge found: " + site.Problem);
        Check.True(site.RiverAngle >= 20, $"river is slanted ({site.RiverAngle:0.0}°)");
        var endDef = new PrefabDef { SizeX = 13, SizeY = 18, SizeZ = 10, YOffset = -1, Found = true, Kind = PrefabKind.Part };
        var tileDef = new PrefabDef { SizeX = 10, SizeY = 18, SizeZ = 10, YOffset = -1, Found = true, Kind = PrefabKind.Part };
        e.Run("slanted bridge", "", ed => Bridge.Build(w, ed, site, endDef, tileDef));
        string img = Environment.GetEnvironmentVariable("YAMEE_TEST_IMAGES");
        if (img != null) RiverTests.DrawSurface(w, Path.Combine(img, "bridge_slanted.png"), site.Center, 65);
        // check shoreline continuity across the bridge boundary et0/et1: no abrupt jump > 3 m in a single 1 m step along the river
        int et0 = site.T0 - 3, et1 = site.T0 + 9 + 3;
        int w0 = site.S0 + site.EndLength - 7, w1 = site.S0 + site.EndLength + 10 * site.Tiles + 6;
        int maxJump = 0;
        foreach (int dir in new[] { -1, 1 })
        {
            int prevLo = w0, prevHi = w1;
            for (int d = 1; d <= 20; d++)
            {
                int t = (dir < 0 ? et0 : et1) + dir * d;
                int lo = int.MaxValue, hi = int.MinValue;
                for (int s = prevLo - 15; s <= prevHi + 15; s++)
                {
                    int x = site.AlongX ? s : t, z = site.AlongX ? t : s;
                    if (w.Contains(x, z) && w.WaterMask[w.Index(x, z)] == level) { lo = Math.Min(lo, s); hi = Math.Max(hi, s); }
                }
                if (lo > hi) break;
                maxJump = Math.Max(maxJump, Math.Max(Math.Abs(lo - prevLo), Math.Abs(hi - prevHi)));
                prevLo = lo; prevHi = hi;
            }
        }
        Console.WriteLine($"       slanted bridge {site.RiverAngle:0.0}°, max 1 m shoreline jump beside bridge: {maxJump} m");
        Check.True(maxJump <= 3, $"no sharp corner at the bridge edge (max jump {maxJump} m)");
        Check.Equal(0, RiverTests.Spills(w, site.Center, 120), "no spills beside slanted bridge");
        // no dry spikes trapped in water and no underwater ridges sticking up > 1.5 m above both neighbors
        int spikes = 0, ridges = 0;
        for (int t = et0 - 32; t <= et1 + 32; t++)
            for (int s = w0 - 20; s <= w1 + 20; s++)
            {
                int x = site.AlongX ? s : t, z = site.AlongX ? t : s;
                if (!w.Contains(x - 1, z - 1) || !w.Contains(x + 1, z + 1)) continue;
                int idx = w.Index(x, z);
                bool wE = w.WaterMask[idx + 1] == level, wW = w.WaterMask[idx - 1] == level, wN = w.WaterMask[idx + w.Size] == level, wS = w.WaterMask[idx - w.Size] == level;
                if (w.WaterMask[idx] == 0 && w.RoadMask[idx] == 0 && ((wE && wW) || (wN && wS))) spikes++;
                if (w.WaterMask[idx] == level && wE && wW && wN && wS)
                {
                    double hc = w.HeightAt(x, z);
                    double nbS = Math.Max(w.HeightAt(x, z - 1), w.HeightAt(x, z + 1));
                    double nbX = Math.Max(w.HeightAt(x - 1, z), w.HeightAt(x + 1, z));
                    if (hc - nbS > 1.5 || hc - nbX > 1.5) ridges++;
                }
            }
        Check.Equal(0, spikes, "no dry spikes trapped in water");
        Check.Equal(0, ridges, "no underwater ridges sticking up in the riverbed");
        var lake = River.FindLake(w, a.X, a.Y);
        Check.True(lake.Cells.Contains(w.Index((int)b.X, (int)b.Y)), "river still connected");
        e.Undo();
        Check.Equal(before, Fixture.State(w), "after undo");
    }
}

static class CursorTests
{
    [Test("Icon mouse pointer (pipette of the lake tool) is built from a font glyph, not the cross as fallback")]
    static void EyedropperCursor()
    {
        var c = MapEditor.IconUi.IconCursor("A", 0, 1, new System.Windows.Media.FontFamily("Segoe UI"));
        Check.True(c != null && c != System.Windows.Input.Cursors.Cross, "own pipette cursor");
    }
}

static class ShoreLook
{
    /// roughness of the ground near the waterline: mean |Laplacian| (m) of the cells between level − 0.5 and level + 2.5 m within r of c
    internal static (double Rough, int Cells, int Level) Roughness(WorldData w, double cx, double cz, int r)
    {
        int level = 0;
        for (int d = 0; d < r && level == 0; d++)
            for (int z = (int)cz - d; z <= (int)cz + d && level == 0; z++)
                for (int x = (int)cx - d; x <= (int)cx + d && level == 0; x++)
                    if (w.WaterMask[w.Index(x, z)] != 0) level = w.WaterMask[w.Index(x, z)];
        double sum = 0; int n = 0;
        for (int z = (int)cz - r; z <= (int)cz + r; z++)
            for (int x = (int)cx - r; x <= (int)cx + r; x++)
            {
                double h = w.HeightAt(x, z);
                if (h < level - 0.5 || h > level + 2.5) continue;
                sum += Math.Abs(4 * h - w.HeightAt(x + 1, z) - w.HeightAt(x - 1, z) - w.HeightAt(x, z + 1) - w.HeightAt(x, z - 1));
                n++;
            }
        return (n > 0 ? sum / n : 0, n, level);
    }

    /// heights around the nearest shore cell (relative to the level, W = water) for checking by eye
    internal static void Print(WorldData w, double cx, double cz, int level, int r = 7)
    {
        int bx = 0, bz = 0; double best = double.MaxValue;
        for (int z = (int)cz - 40; z <= (int)cz + 40; z++)
            for (int x = (int)cx - 40; x <= (int)cx + 40; x++)
                if (w.WaterMask[w.Index(x, z)] != 0 && (w.WaterMask[w.Index(x + 1, z)] == 0 || w.WaterMask[w.Index(x - 1, z)] == 0 || w.WaterMask[w.Index(x, z + 1)] == 0 || w.WaterMask[w.Index(x, z - 1)] == 0))
                {
                    double d = (x - cx) * (x - cx) + (z - cz) * (z - cz);
                    if (d < best) { best = d; bx = x; bz = z; }
                }
        for (int z = bz + r; z >= bz - r; z--)
        {
            var sb = new System.Text.StringBuilder("       ");
            for (int x = bx - r; x <= bx + r; x++)
                sb.Append((w.WaterMask[w.Index(x, z)] != 0 ? "W" : " ") + (w.HeightAt(x, z) - level).ToString("0.00").PadLeft(6));
            Console.WriteLine(sb);
        }
    }

    [Test("Shore look (real map, output only): roughness of the ground at the waterline at YAMEE_TEST_SHORE = folder|x,z|x,z")]
    static void Look()
    {
        string v = Environment.GetEnvironmentVariable("YAMEE_TEST_SHORE");
        if (string.IsNullOrEmpty(v)) Check.Skip("YAMEE_TEST_SHORE not set");
        var parts = v.Split('|');
        var w = WorldData.Load(parts[0], null, new Progress<string>());
        foreach (var p in parts.Skip(1))
        {
            var c = p.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var (rough, n, level) = Roughness(w, c[0], c[1], 30);
            Console.WriteLine($"       {c[0]},{c[1]}: level {level}, roughness at the waterline {rough:0.000} m ({n} cells)");
            Print(w, c[0], c[1], level);
        }
    }
}
