using System.Windows;
using System.Windows.Media.Imaging;
using MapEditor;
using MapEditor.Map;

namespace YameeTests;

/// test world: a small world (2 km) generated once with a fixed start value; every test gets its own copy
static class Fixture
{
    public static string Temp;
    static string world;
    static int copies;

    public static GeneratorSpec Spec(string seed) => new() { Name = "Test", Size = 2048, Seed = seed, Lakes = 2, Rivers = 0, SpawnCount = 8 };

    /// folder of the generated test world (generated at the first call)
    public static string World
    {
        get
        {
            if (world != null) return world;
            world = Path.Combine(Temp, "world");
            new WorldGenerator(Spec("YAMEE-Test")).Write(world, null, new Progress<string>());
            return world;
        }
    }

    static string flatWorld;

    /// the same test world without the volcano (the route tests were laid out before the generator set a volcano into the
    /// burnt forest; it now stands across their courses and its slopes block country roads)
    public static string WorldWithoutVolcano
    {
        get
        {
            if (flatWorld != null) return flatWorld;
            flatWorld = Path.Combine(Temp, "world_novolcano");
            var spec = Spec("YAMEE-Test"); spec.SkipVolcano = true;
            new WorldGenerator(spec).Write(flatWorld, null, new Progress<string>());
            return flatWorld;
        }
    }

    /// own copy of the test world, loaded (changes and saving do not touch the other tests)
    public static WorldData LoadCopy(bool volcano = true)
    {
        string dst = Path.Combine(Temp, "copy" + ++copies, "Test");
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(volcano ? World : WorldWithoutVolcano)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        return WorldData.Load(dst, null, new Progress<string>());
    }

    /// fingerprint of the map state in memory (heights, roads, water, biomes)
    public static string State(WorldData w) => $"{Check.Hash(w.Heights)}/{Check.Hash(w.RoadMask)}/{Check.Hash(w.WaterMask)}/{Check.Hash(w.Biomes)}";

    /// a road across the test world with a few curves
    public static RoadResult Road(WorldData w, double from, double to) =>
        RoadPlanner.Compute(w, new List<Point> { new(from, from + 40), new((from + to) / 2, (from + to) / 2 - 60), new(to, to) }, RoadSettings.Asphalt());
}

static class Tests
{
    [Test("Parts: markers with a district tag only in that district (remnant_house_04)")]
    static void PartMarkerTags()
    {
        // like remnant_house_04: driveway (countryresidential, 1 m deep) and driveway2 (residential, 5 m deep into the street)
        var def = new PrefabDef { SizeX = 42, SizeY = 12, SizeZ = 42, Found = true, YOffset = -1 };
        def.Parts.Add(new PartMarker(6, 0, -1, 4, 13, 1, new[] { "part_driveway_countryresidential_01" }, 2, 1, "driveway", "countryresidential"));
        def.Parts.Add(new PartMarker(5, 0, -5, 6, 13, 5, new[] { "part_driveway_residential_01" }, 2, 1, "driveway2", "residential"));
        def.Parts.Add(new PartMarker(30, 0, -1, 4, 13, 1, new[] { "part_any" }, 2, 1, "", ""));
        var house = new PlacedPrefab { Name = "remnant_house_04", Def = def, X = 100, Y = 50, Z = 200, Rotation = 0 };
        PrefabDef PartDef(string n) => new() { SizeX = 4, SizeZ = 4, Kind = PrefabKind.Part, Found = true };
        string Names(string district) => string.Join(",", PrefabParts.For(house, PartDef, district).Select(q => q.Name).OrderBy(n => n));
        Check.Equal("part_any,part_driveway_countryresidential_01", Names("countryresidential"), "countryresidential tile");
        Check.Equal("part_any,part_driveway_residential_01", Names("residential"), "residential tile");
        Check.Equal("part_any", Names(null), "wilderness");
        Check.Equal("part_any", Names("downtown"), "other district");
    }

    [Test("World generator: same start value gives the same world")]
    static void GeneratorDeterministic()
    {
        string a = Path.Combine(Fixture.Temp, "genA"), b = Path.Combine(Fixture.Temp, "genB");
        new WorldGenerator(Fixture.Spec("Same")).Write(a, null, new Progress<string>());
        new WorldGenerator(Fixture.Spec("Same")).Write(b, null, new Progress<string>());
        foreach (var f in new[] { "dtm.raw", "biomes.png", "splat4.png", "spawnpoints.xml" })
            Check.Equal(Check.Hash(Path.Combine(a, f)), Check.Hash(Path.Combine(b, f)), f);
    }

    [Test("World generator: desert as mesas, badlands (game 3.4) or mixed")]
    static void DesertStyles()
    {
        // pure desert world: badlands are lower and more evenly high than mesas; mixed lies in between (the small-scale relief is only printed)
        (double spread, double fine) Measure(int style)
        {
            var spec = Fixture.Spec("Dunes");
            spec.Size = 4096; spec.Shares = new[] { 0, 0, 100, 0, 0 }; spec.DesertStyle = style;
            var g = new WorldGenerator(spec);
            g.BuildCoarse(4096 / 256.0);
            var hs = new List<double>(); double fine = 0; int n = 0;
            for (double z = 300; z < 3800; z += 50)
                for (double x = 300; x < 3800; x += 50)
                {
                    double h = g.RawHeight(x, z, out _);
                    hs.Add(h);
                    // small-scale relief: deviation from the mean of the four neighbors 20 m away (a uniform slope gives 0)
                    double around = (g.RawHeight(x + 20, z, out _) + g.RawHeight(x - 20, z, out _) + g.RawHeight(x, z + 20, out _) + g.RawHeight(x, z - 20, out _)) / 4;
                    fine += Math.Abs(h - around); n++;
                }
            hs.Sort();
            return (hs[(int)(hs.Count * 0.95)] - hs[(int)(hs.Count * 0.05)], fine / n);
        }
        var mesas = Measure(0); var badlands = Measure(1); var mixed = Measure(2);
        Console.WriteLine($"       height spread p5…p95: mesas {mesas.spread:0.0} m, badlands {badlands.spread:0.0} m, mixed {mixed.spread:0.0} m;" +
            $" small-scale relief (20 m):{mesas.fine:0.00} / {badlands.fine:0.00} / {mixed.fine:0.00} m");
        Check.True(badlands.spread < mesas.spread, "badlands lower than mesas");
        Check.True(mixed.spread > badlands.spread && mixed.spread < mesas.spread, "mixed has both kinds");
    }

    [Test("World generator: snow as high mountains, glacial valleys, sharp peaks, tundra or mixed")]
    static void SnowStyles()
    {
        // pure snow world: share of flat ground (slope < 5 % over 20 m), steep ground (> 45°) and height spread p5…p95
        (double flat, double steep, double spread) Measure(int style)
        {
            var spec = Fixture.Spec("Frosty");
            spec.Size = 4096; spec.Shares = new[] { 0, 0, 0, 100, 0 }; spec.SnowStyle = style;
            var g = new WorldGenerator(spec);
            g.BuildCoarse(4096 / 256.0);
            var hs = new List<double>(); int flat = 0, steep = 0, n = 0;
            for (double z = 300; z < 3800; z += 40)
                for (double x = 300; x < 3800; x += 40)
                {
                    double h = g.RawHeight(x, z, out _);
                    hs.Add(h);
                    double gx = (g.RawHeight(x + 10, z, out _) - g.RawHeight(x - 10, z, out _)) / 20, gz = (g.RawHeight(x, z + 10, out _) - g.RawHeight(x, z - 10, out _)) / 20;
                    double s = Math.Sqrt(gx * gx + gz * gz);
                    if (s < 0.05) flat++;
                    if (s > 1) steep++;
                    n++;
                }
            hs.Sort();
            return (100.0 * flat / n, 100.0 * steep / n, hs[(int)(hs.Count * 0.95)] - hs[(int)(hs.Count * 0.05)]);
        }
        var r = Enumerable.Range(0, 5).Select(Measure).ToArray();
        string[] names = { "high mountains", "glacial valleys", "sharp peaks", "tundra", "mixed" };
        for (int i = 0; i < 5; i++) Console.WriteLine($"       {names[i]}: flat {r[i].flat:0.0} %, steep {r[i].steep:0.0} %, spread {r[i].spread:0} m");
        Check.True(r[1].flat > r[0].flat * 1.5, "glacial valleys have flat floors");
        Check.True(r[2].steep > r[0].steep, "sharp peaks are steeper");
        Check.True(r[3].spread < r[0].spread * 0.7 && r[3].flat > r[0].flat, "tundra is a flatter high plain");
        Check.True(r[4].flat > r[0].flat && r[4].spread > r[3].spread, "mixed has several kinds");
    }

    // biome patches (4-neighbor components, grid 64 m) and the share of cells in narrow places (neighbor 3 cells away of another biome on both sides)
    static (int patches, double narrow, double[] share) BiomeShape(double coherence, string seed)
    {
        var spec = Fixture.Spec(seed);
        spec.Size = 8192; spec.BiomeCoherence = coherence;
        var g = new WorldGenerator(spec);
        g.BuildCoarse(8192 / 420.0);
        int n = 128; double step = 8192.0 / n;
        var b = new int[n * n];
        for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) b[z * n + x] = g.BiomeAt((x + .5) * step, (z + .5) * step);
        var seen = new bool[n * n]; int patches = 0, big = 0;
        for (int s = 0; s < n * n; s++)
        {
            if (seen[s]) continue;
            patches++; int cnt = 0; var st = new Stack<int>(); st.Push(s); seen[s] = true;
            while (st.Count > 0)
            {
                int i = st.Pop(); cnt++; int ix = i % n, iz = i / n;
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int x = ix + dx, z = iz + dz;
                    if (x < 0 || z < 0 || x >= n || z >= n) continue;
                    int j = z * n + x;
                    if (!seen[j] && b[j] == b[s]) { seen[j] = true; st.Push(j); }
                }
            }
            if (cnt >= 25) big++;
        }
        int narrow = 0;
        for (int z = 6; z < n - 6; z++) for (int x = 6; x < n - 6; x++)
        {
            int c = b[z * n + x];
            bool h = b[z * n + x - 6] != c && b[z * n + x + 6] != c, v = b[(z - 6) * n + x] != c && b[(z + 6) * n + x] != c;
            if (h || v) narrow++;
        }
        if (Environment.GetEnvironmentVariable("YAMEE_ASCII") == seed)
        {
            var sbm = new System.Text.StringBuilder();
            for (int z = n - 1; z >= 0; z -= 2) { for (int x = 0; x < n; x++) sbm.Append("PBDSW"[b[z * n + x]]); sbm.AppendLine(); }
            Console.WriteLine(sbm);
        }
        var share = new double[5];
        foreach (int c in b) share[c] += 100.0 / (n * n);
        return (big, narrow * 100.0 / (n * n), share);
    }

    [Test("World generator: coherent biomes have fewer separate patches")]
    static void BiomesCoherent()
    {
        int p0 = 0, p1 = 0; double n0 = 0, n1 = 0, snowMin = 100;
        foreach (var seed in new[] { "Aa", "Bb", "Cc", "Dd" })
        {
            var a = BiomeShape(0, seed); var c = BiomeShape(0.7, seed);
            p0 += a.patches; p1 += c.patches; n0 += a.narrow; n1 += c.narrow;
            snowMin = Math.Min(snowMin, c.share[3]);
        }
        Console.WriteLine($"  patches {p0} -> {p1}, narrow places {n0:0.0} % -> {n1:0.0} % (sum of 4 worlds)");
        Check.True(p1 < p0, "not fewer patches");
        Check.True(snowMin > 17, "snow share too small");
    }

    [Test("World generator: ring layout - round pine forest in the middle, shares kept")]
    static void BiomesRing()
    {
        var spec = Fixture.Spec("Hotepu"); spec.Size = 8192; spec.BiomeLayout = 1; if (double.TryParse(Environment.GetEnvironmentVariable("YAMEE_COH"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cc)) spec.BiomeCoherence = cc;
        var g = new WorldGenerator(spec); g.BuildCoarse(8192 / 420.0);
        int n = 64; var share = new double[5]; var sb = new System.Text.StringBuilder();
        double far = 0; int cnt = 0;
        for (int z = n - 1; z >= 0; z--) { for (int x = 0; x < n; x++) { int b = g.BiomeAt((x + .5) * 128, (z + .5) * 128); share[b] += 100.0 / (n * n); if (b == 0) { double d = Math.Sqrt(Math.Pow((x + .5) * 128 - 4096, 2) + Math.Pow((z + .5) * 128 - 4096, 2)); far = Math.Max(far, d); cnt++; } if (z % 2 == 0) sb.Append("PBDSW"[b]); } if (z % 2 == 0) sb.AppendLine(); }
        if (Environment.GetEnvironmentVariable("YAMEE_RING") != null) Console.WriteLine(sb);
        Console.WriteLine("  shares " + string.Join(", ", share.Select(s => s.ToString("0.0"))) + "; pine reaches " + far.ToString("0") + " m, equal-area circle " + (Math.Sqrt(cnt * 128.0 * 128 / Math.PI)).ToString("0") + " m");
        for (int b = 0; b < 5; b++) Check.True(Math.Abs(share[b] - spec.Shares[b]) < 3, "share " + b + " = " + share[b]);
        Check.True(far < 1.8 * Math.Sqrt(cnt * 128.0 * 128 / Math.PI), "pine forest not round");
        Check.Equal(0, g.BiomeAt(4096, 4096), "center");
    }

    [Test("Map as image: 2048 × 2048 PNG without the test spawn point")]
    static void MapImageExport()
    {
        var w = Fixture.LoadCopy();
        var view = new MapEditor.Controls.MapView();
        view.SetWorld(w, MapLayers.Build(w, new Progress<string>()));
        byte[] Pixels(System.Windows.Media.Imaging.BitmapSource b)
        {
            var px = new byte[b.PixelWidth * b.PixelHeight * 4];
            b.CopyPixels(px, b.PixelWidth * 4, 0);
            return px;
        }
        var img = view.ExportImage(2048);
        Check.Equal(2048, img.PixelWidth, "width");
        Check.Equal(2048, img.PixelHeight, "height");
        var before = Pixels(img);
        w.Spawns.Add(new SpawnPoint { X = 100, Z = 100, Test = true });
        Check.True(before.SequenceEqual(Pixels(view.ExportImage(2048))), "test spawn point is in the image");
        w.Spawns.Add(new SpawnPoint { X = 100, Z = 100 });
        Check.True(!before.SequenceEqual(Pixels(view.ExportImage(2048))), "play spawn point missing in the image");
    }

    [Test("World generator: the world can be loaded")]
    static void GeneratedWorldLoads()
    {
        var w = Fixture.LoadCopy();
        Check.Equal(2048, w.Size, "size");
        Check.True(w.Spawns.Count > 0, "no spawn points");
        Check.True(w.Heights.Any(h => h > 0), "all heights 0");
    }

    [Test("World generator: mountain lakes have no rectangular cliff walls at the carve boundary")]
    static void MountainLakesNoCliffWall()
    {
        // the user's report 2026-11-22 (Hapohu-Land): a mountain lake in the burnt forest had a flat ramp
        // extending to the square reach box of Carve and dropping as a vertical cliff at the box edge
        string dir = Path.Combine(Fixture.Temp, "mountain_lakes");
        var spec = new GeneratorSpec { Name = "MtnLakes", Size = 4096, Seed = "Hapohu", Shares = new[] { 0, 100, 0, 0, 0 }, Mountains = 1.8, Lakes = 3, Rivers = 0, Anomalies = 0, SpawnCount = 4 };
        var gen = new WorldGenerator(spec);
        gen.Write(dir, null, new Progress<string>());
        Check.True(gen.LakeList.Count > 0, "at least one lake");
        var w = WorldData.Load(dir, null, new Progress<string>());
        double worstStep = 0;
        string worstInfo = "";
        foreach (var l in gen.LakeList)
        {
            double reach = l.Radius * 2.6 + 90;
            int x0 = Math.Max(1, (int)(l.X - reach - 8)), x1 = Math.Min(w.Size - 2, (int)(l.X + reach + 8));
            int z0 = Math.Max(1, (int)(l.Z - reach - 8)), z1 = Math.Min(w.Size - 2, (int)(l.Z + reach + 8));
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = z * w.Size + x;
                    if (w.WaterMask[i] != 0) continue;
                    double h = w.Heights[i] / 256.0;
                    double sx = w.WaterMask[i + 1] == 0 ? Math.Abs(w.Heights[i + 1] / 256.0 - h) : 0;
                    double sz = w.WaterMask[i + w.Size] == 0 ? Math.Abs(w.Heights[i + w.Size] / 256.0 - h) : 0;
                    double s = Math.Max(sx, sz);
                    if (s > worstStep)
                    {
                        worstStep = s;
                        double dist = Math.Sqrt((x - l.X) * (x - l.X) + (z - l.Z) * (z - l.Z));
                        worstInfo = $"lake R={l.Radius:0.0} L={l.Level} at ({x},{z}) dist={dist:0.0}/{reach:0.0} h={h:0.0} w={w.WaterMask[i]}";
                    }
                }
        }
        Console.WriteLine($"       {gen.LakeList.Count} mountain lakes, steepest 1 m step near lakes: {worstStep:0.00} m ({worstInfo})");
        Check.True(worstStep < 4.5, $"vertical cliff wall near mountain lake (step {worstStep:0.00} m)");
    }

    [Test("World generator: lakes stay clear of anomalies (sinkholes), keep realistic levels, and match preview")]
    static void LakesClearOfAnomalies()
    {
        // the user's report 2026-11-22 (Mepuse-Land): a lake overlapped a sinkhole (-1341,-26), dropping its level to 3 m
        // and spawning a detached water moat + 1-pixel sealing wall outside the shore berm
        string dir = Path.Combine(Fixture.Temp, "lakes_anomalies");
        var spec = new GeneratorSpec
        {
            Name = "MepuseCheck", Size = 4096, Seed = "Mepuse-Land",
            Shares = new[] { 19, 20, 19, 20, 22 }, Mountains = 1.5, Hills = 1.4, Rough = 1.4,
            Valleys = 1.3, Erosion = 1.4, Lakes = 3, Rivers = 2, Anomalies = 3,
            BiomeCoherence = 0.9, BiomeLayout = 1, DesertStyle = 2, SnowStyle = 4, SpawnCount = 8,
        };
        var gen = new WorldGenerator(spec);
        gen.Write(dir, null, new Progress<string>());
        Check.True(gen.LakeList.Count > 0, "at least one lake");
        Check.True(gen.Anomalies.Count > 0, "at least one anomaly");
        int half = spec.Size / 2;
        foreach (var l in gen.LakeList)
        {
            Check.True(l.Level >= 15, $"lake at ({l.X - half:0},{l.Z - half:0}) plunged to level {l.Level} m");
            foreach (var a in gen.Anomalies)
            {
                if (a.Kind == "Volcanic crater") continue;
                double d = Math.Sqrt(Math.Pow(l.X - (a.X + half), 2) + Math.Pow(l.Z - (a.Z + half), 2));
                Check.True(d > l.Radius + 20, $"lake at ({l.X - half:0},{l.Z - half:0}) R={l.Radius:0} overlaps {a.Kind} at ({a.X},{a.Z}) (dist {d:0} m)");
            }
        }
        // preview (coarse grid) makes the exact same lake and river placement decisions
        var pv = new WorldGenerator(spec);
        pv.BuildCoarse(spec.Size / 420.0);
        Check.Equal(gen.LakeList.Count, pv.LakeList.Count, "same lake count in preview and world");
        Check.Equal(gen.RiverList.Count, pv.RiverList.Count, "same river count in preview and world");
        Console.WriteLine($"       {gen.LakeList.Count} lakes (min level {gen.LakeList.Min(l => l.Level)} m), {gen.Anomalies.Count} anomalies, {gen.RiverList.Count} rivers, sealed {gen.SealedCells}");
    }

    [Test("Road: undo and redo give exactly the same map")]
    static void RoadUndoRedo()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        string before = Fixture.State(w);
        var r = Fixture.Road(w, -600, 600);
        e.Run("road", "road", ed => RoadApplier.Apply(w, ed, r, new Progress<string>()));
        string built = Fixture.State(w);
        Check.True(built != before, "the road changed nothing");
        e.Undo();
        Check.Equal(before, Fixture.State(w), "after undo");
        e.Redo();
        Check.Equal(built, Fixture.State(w), "after redo");
    }

    [Test("Road: long road without gaps in the surface (calculated in 512 m pieces)")]
    static void LongRoadSeamless()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        var r = Fixture.Road(w, -950, 950);   // about 2.7 km, the area spans several pieces
        e.Run("road", "road", ed => RoadApplier.Apply(w, ed, r, new Progress<string>()));
        double half = r.Settings.Width / 2 - 1;
        int missing = 0, checkedPoints = 0;
        for (int k = 8; k < r.Path.Count - 8; k += 4)
        {
            var (nx, nz) = RoadPlanner.Normal(r.Path, k);
            foreach (double side in new[] { -half, 0, half })
            {
                double x = r.Path[k].X + nx * side, z = r.Path[k].Z + nz * side;
                checkedPoints++;
                if (w.RoadAt((int)Math.Floor(x), (int)Math.Floor(z)) != WorldData.Asphalt) missing++;
            }
        }
        Check.True(checkedPoints > 1000, "too few points checked");
        Check.Equal(0, missing, "points on the roadway without asphalt");
    }

    [Test("Undo data: one entry per cell, the value before the first change")]
    static void UndoLogOncePerCell()
    {
        var log = new UndoLog<ushort>(1_000_000);
        log.Add(5, 100); log.Add(5, 200); log.Add(5, 300);
        Check.Equal(1, log.Count, "entries for one cell");
        Check.Equal((ushort)100, log.First().v, "kept value");
        for (int i = 0; i < 200_000; i++) { log.Add(i, 1); log.Add(i, 2); }   // beyond 64k entries: the bit field takes over
        Check.Equal(200_000, log.Count, "entries after many cells");
        Check.True(log.Contains(150_000) && !log.Contains(300_000), "Contains");
        log.Seal();
        Check.Equal(200_000, log.Count(), "entries after Seal");
    }

    [Test("Edit order: deleting an entry that a later step needs keeps all steps")]
    static void DeleteRollsBack()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        int i1 = w.Index(10, 10), i2 = w.Index(20, 20);
        ushort v1 = (ushort)(w.Heights[i1] + 512);
        string original = Fixture.State(w);
        e.Run("A", "A", ed => ed.SetHeight(i1, v1));
        e.Run("B needs A", "B", ed => { if (w.Heights[i1] != v1) throw new InvalidOperationException("A is missing"); ed.SetHeight(i2, (ushort)(w.Heights[i2] + 256)); });
        string withBoth = Fixture.State(w);
        bool failed = false;
        try { e.Delete(0); } catch (InvalidOperationException) { failed = true; }
        Check.True(failed, "deleting should report the error");
        Check.Equal(2, e.Steps.Count, "steps after the failed delete");
        Check.Equal(withBoth, Fixture.State(w), "map after the failed delete");
        e.Undo(); e.Undo();
        Check.Equal(original, Fixture.State(w), "after undoing everything");
    }

    [Test("Road: no step in the slope inside a sharp bend (one leg falls, the other rises)")]
    static void SharpBendNoStep()
    {
        // the user's report 2026-11-16 (Darobe-Land 480 W 2340 N): a road leaves a town exit, turns sharply (about 115°) and keeps
        // climbing – on the bisector of the inner corner the nearest waypoint switches between the legs, a 5 m wall stood there
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        double cx = 100, cz = 100;
        // flat ground 25 m above the road (deep cut, wide slopes – as on the hill at Darobe-Land)
        double ground = w.HeightAt((int)cx, (int)cz);
        for (int z = (int)cz - 200; z <= cz + 200; z++)
            for (int x = (int)cx - 200; x <= cx + 200; x++) w.Heights[w.Index(x, z)] = (ushort)((ground + 25) * 256);
        var pts = new List<Point> { new(cx + 120, cz), new(cx + 60, cz), new(cx, cz), new(cx - 30, cz - 52), new(cx - 60, cz - 104) };
        var s = RoadSettings.Asphalt();
        s.MaxGrade = 0.2;
        var r = RoadPlanner.Compute(w, pts, s, new double?[] { ground - 10, null, ground, null, ground + 10 });
        Check.True(r != null, "road not planned");
        e.Run("road", "road", ed => RoadApplier.Apply(w, ed, r, new Progress<string>()));
        // inner corner: cells beyond roadway, shoulder and verge, up to 50 m from the corner; the plain slope of this cut is about 1.2 m per meter, before the fix the bisector had 2.65 m
        double outer = r.Settings.Width / 2 + r.Settings.Shoulder + r.Settings.GrassEdge + 1;
        double worst = 0; string where = "";
        for (int z = (int)cz - 50; z <= cz; z++)
            for (int x = (int)cx - 10; x <= cx + 50; x++)
            {
                double dmin = r.Path.Min(p => Math.Sqrt((p.X - x - 0.5) * (p.X - x - 0.5) + (p.Z - z - 0.5) * (p.Z - z - 0.5)));
                if (dmin < outer) continue;
                double h = w.Heights[w.Index(x, z)] / 256.0;
                foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
                {
                    double d = Math.Abs(h - w.Heights[w.Index(x + dx, z + dz)] / 256.0);
                    if (d > worst) { worst = d; where = $"{x}/{z}"; }
                }
            }
        Check.True(worst < 1.5, $"step {worst:0.00} m at {where} in the inner corner");
    }

    [Test("Save: saved and loaded again gives the same map")]
    static void SaveAndReload()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        var r = Fixture.Road(w, -300, 300);
        e.Run("road", "road", ed => RoadApplier.Apply(w, ed, r, new Progress<string>()));
        string state = $"{Check.Hash(w.Heights)}/{Check.Hash(w.WaterMask)}";
        e.Save(null, new Progress<string>(), out _);
        var again = WorldData.Load(w.Folder, null, new Progress<string>());
        Check.Equal(state, $"{Check.Hash(again.Heights)}/{Check.Hash(again.WaterMask)}", "heights/water after loading");
        Check.True(again.RoadMask.Count(v => v == WorldData.Asphalt) > 1000, "asphalt in splat3.png after loading");
        Check.True(Directory.Exists(Path.Combine(w.Folder, "Backups", "Map")), "backup before saving");
    }

    [Test("Map images: after a change the reduced copies are the same as a rebuild")]
    static void MapLayersUpdate()
    {
        var w = Fixture.LoadCopy();
        var l = MapLayers.Build(w, new Progress<string>());
        int x0 = -301, z0 = -203, x1 = 157, z1 = 99;   // odd borders: checks the 2-pixel alignment
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                int i = w.Index(x, z);
                w.Heights[i] = (ushort)(w.Heights[i] + 128);
                if (Math.Abs(x - z) < 3) w.RoadMask[i] = WorldData.Asphalt;
                if (Math.Abs(x + z) < 5) w.WaterMask[i] = 30;
            }
        l.Update(w, x0, z0, x1, z1);
        var fresh = MapLayers.Build(w, new Progress<string>());
        for (int kind = 0; kind < 4; kind++)
            for (int level = 0; level <= MapLayers.ReducedLevels; level++)
                Check.Equal(Check.Hash(Pixels(fresh.Image(kind, level))), Check.Hash(Pixels(l.Image(kind, level))), $"image {kind}, level {level}");
    }

    [Test("Map images: low-level water (e.g. 3 m) is clearly visible, independent of elevation")]
    static void MapLayersLowWaterVisible()
    {
        var w = Fixture.LoadCopy();
        void Stamp(int cx, int cz, byte level, double depth)
        {
            for (int dz = -6; dz <= 6; dz++)
                for (int dx = -6; dx <= 6; dx++)
                {
                    int i = w.Index(cx + dx, cz + dz);
                    w.Heights[i] = (ushort)Math.Round((level - depth) * 256);
                    w.WaterMask[i] = level;
                }
        }
        // three water patches at very different elevations (3 m, 30 m, 90 m) with the same 1 m depth
        Stamp(-200, 0, 3, 1.0);
        Stamp(0, 0, 30, 1.0);
        Stamp(200, 0, 90, 1.0);
        var l = MapLayers.Build(w, new Progress<string>());
        var waterPx = Pixels(l.Water);
        (byte B, byte G, byte R, byte A) At(int wx, int wz)
        {
            int px = (wx + w.Half) / MapLayers.MetersPerPixel;
            int py = (w.Half - 1 - wz) / MapLayers.MetersPerPixel;
            int o = (py * l.Pixels + px) * 4;
            return (waterPx[o], waterPx[o + 1], waterPx[o + 2], waterPx[o + 3]);
        }
        var p3 = At(-200, 0);
        var p30 = At(0, 0);
        var p90 = At(200, 0);
        Check.True(p3.A >= 150, $"level 3 m water must be clearly visible (alpha {p3.A})");
        Check.Equal(p3.A, p30.A, "same depth at 3 m and 30 m gives same alpha");
        Check.Equal(p30.A, p90.A, "same depth at 30 m and 90 m gives same alpha");
        Check.Equal(p3.B, p90.B, "same depth gives same blue channel");
    }

    static byte[] Pixels(BitmapSource b)
    {
        int bpp = b.Format.BitsPerPixel / 8;
        var a = new byte[b.PixelWidth * b.PixelHeight * bpp];
        b.CopyPixels(a, b.PixelWidth * bpp, 0);
        return a;
    }

    [Test("user.ini: values are saved although the file is locked for a moment")]
    static void UserIniLocked()
    {
        var s = AppSettings.Load();
        s.EraserSize = 77; s.TerrainSize = 55; s.Save();
        string ini = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("MAPEDITOR_SETTINGS")), "user.ini");
        Check.True(File.Exists(ini), "user.ini was not written");
        var fs = new FileStream(ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(() => { Thread.Sleep(300); fs.Dispose(); });
        s.EraserSize = 66; s.Save();
        release.Wait();
        string text = File.ReadAllText(ini);
        Check.True(text.Contains("= 66"), "new value missing after the lock");
        Check.True(text.Contains("TerrainSize = 55"), "other value lost");
    }

    [Test("Road plan: saved and loaded again is the same, deleting keeps the references valid")]
    static void RoadPlanEdit()
    {
        var w = Fixture.LoadCopy();
        var town = new NetTown { Id = 1, X0 = -100, Z0 = -100, X1 = 100, Z1 = 100 };
        var exA = new NetExit { Town = town, X = 100, Z = 0, DX = 1, Width = 8 };
        var exB = new NetExit { Town = town, X = 0, Z = 100, DZ = 1, Width = 8 };
        town.Exits.AddRange(new[] { exA, exB });
        List<Point> Line(double x0, double z0, double x1, double z1) => new() { new(x0, z0), new((x0 + x1) / 2, (z0 + z1) / 2), new(x1, z1) };
        var main = new NetRoad { Points = Line(100, 0, 600, 0), Width = 16, From = exA, Length = 500 };
        var branch = new NetRoad { Points = Line(300, 0, 300, 400), Width = 16, StartRoad = main, Length = 400, Loop = true };
        var cross = new NetRoad { Points = Line(0, 100, 500, 300), Width = 16, From = exB, Length = 540 };
        cross.Crossings.Add(new Point(300, 200)); cross.CrossedRoads.Add(branch);
        var plan = new NetworkPlan { Towns = { town }, Roads = { main, branch, cross } };
        NetworkPlanEdit.Refresh(plan);
        var file = new NetworkPlanFile { Plan = plan, Spec = new NetworkSpec { Seed = "x", Grade = 0.1 } };

        NetworkPlanStore.Save(w, file);
        var back = NetworkPlanStore.Load(w, out string problem);
        Check.True(back != null, "not loaded: " + problem);
        Check.Equal(NetworkPlanStore.Serialize(file), NetworkPlanStore.Serialize(back), "after loading");
        Check.True(back.Plan.Roads[1].StartRoad == back.Plan.Roads[0], "junction reference lost");
        Check.True(back.Plan.Roads[2].CrossedRoads[0] == back.Plan.Roads[1], "crossing reference lost");
        Check.True(back.Plan.Roads[0].From == back.Plan.Towns[0].Exits[0], "exit not shared");

        // delete the main road, keep the branch as a dead end: the crossing with the branch stays
        var p2 = NetworkPlanStore.Deserialize(w, NetworkPlanStore.Serialize(file), out _).Plan;
        Check.Equal(1, NetworkPlanEdit.Delete(p2, p2.Roads[0], false), "deleted");
        Check.Equal(2, p2.Roads.Count, "roads left");
        Check.True(p2.Roads[0].StartRoad == null, "branch still refers to the deleted road");
        Check.True(!p2.Towns[0].Exits[0].Used && p2.Towns[0].Exits[1].Used, "exits used");
        Check.Equal(1, p2.CrossingPoints.Count, "crossings");

        // delete with the roads branching off: the crossing on the branch disappears as well
        var p3 = NetworkPlanStore.Deserialize(w, NetworkPlanStore.Serialize(file), out _).Plan;
        Check.Equal(2, NetworkPlanEdit.Delete(p3, p3.Roads[0], true), "deleted with branch");
        Check.Equal(0, p3.Roads[0].Crossings.Count, "crossing with the deleted branch");
        Check.Equal(0, p3.Junctions.Count, "junctions");
        Check.True(p3.Towns[0].Connected, "town still has a road");
    }

    /// picture for checking by eye (YAMEE_TEST_IMAGES = folder): relief, other roads grey, old course red, new blue, via points
    static void DrawRoute(WorldData w, string file, List<Point> old, List<Point> line, List<Point> vias, NetworkPlan plan)
    {
        var all = old.Concat(line).ToList();
        double x0 = all.Min(p => p.X) - 150, x1 = all.Max(p => p.X) + 150, z0 = all.Min(p => p.Y) - 150, z1 = all.Max(p => p.Y) + 150;
        double sc = 900 / Math.Max(x1 - x0, z1 - z0);
        int W = (int)((x1 - x0) * sc), H = (int)((z1 - z0) * sc);
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double wx = x0 + x / sc, wz = z1 - y / sc;
                double hx = w.HeightAt((int)(wx + 2), (int)wz) - w.HeightAt((int)(wx - 2), (int)wz), hz = w.HeightAt((int)wx, (int)(wz + 2)) - w.HeightAt((int)wx, (int)(wz - 2));
                byte v = (byte)Math.Clamp(170 + 25 * (hz - hx), 40, 250);
                int k = (y * W + x) * 4; px[k] = px[k + 1] = px[k + 2] = v; px[k + 3] = 255;
            }
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(BitmapSource.Create(W, H, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, W * 4), new Rect(0, 0, W, H));
            Point S(Point p) => new((p.X - x0) * sc, (z1 - p.Y) * sc);
            void Line(List<Point> l, System.Windows.Media.Brush br, double th) { for (int q = 1; q < l.Count; q++) dc.DrawLine(new System.Windows.Media.Pen(br, th), S(l[q - 1]), S(l[q])); }
            foreach (var r in plan.Roads) Line(r.Points, System.Windows.Media.Brushes.Gray, 2);
            Line(old, System.Windows.Media.Brushes.Red, 2);
            Line(line, System.Windows.Media.Brushes.Blue, 2.5);
            for (int k = 0; k < vias.Count; k++)
            {
                dc.DrawEllipse(System.Windows.Media.Brushes.White, new System.Windows.Media.Pen(System.Windows.Media.Brushes.Blue, 2), S(vias[k]), 6, 6);
                dc.DrawText(new System.Windows.Media.FormattedText((k + 1).ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface("Segoe UI"), 14, System.Windows.Media.Brushes.Black, 1), S(vias[k]) + new Vector(8, -8));
            }
        }
        var bmp = new RenderTargetBitmap(W, H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    static int SelfCrossings(List<Point> line)
    {
        int count = 0;
        for (int q = 0; q + 1 < line.Count; q++)
            for (int k = q + 2; k + 1 < line.Count; k++)
            {
                Vector r = line[q + 1] - line[q], s = line[k + 1] - line[k], ca = line[k] - line[q];
                double den = r.X * s.Y - r.Y * s.X;
                if (Math.Abs(den) < 1e-9) continue;
                double t = (ca.X * s.Y - ca.Y * s.X) / den, u = (ca.X * r.Y - ca.Y * r.X) / den;
                if (t > 0 && t < 1 && u > 0 && u < 1) count++;
            }
        return count;
    }

    [Test("Road plan (real map): rerouted roads with via points do not cross themselves")]
    static void RerouteNoLoops()
    {
        string folder = Environment.GetEnvironmentVariable("YAMEE_TEST_WORLD");
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, NetworkPlanStore.FileName)))
            Check.Skip("set YAMEE_TEST_WORLD to a project folder with a saved road plan with via points");
        string copy = Path.Combine(Fixture.Temp, "reroute", Path.GetFileName(folder.TrimEnd('\\')));
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(folder)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        var w = WorldData.Load(copy, AppSettings.GuessGameFolder(), new Progress<string>());
        var f = NetworkPlanStore.Deserialize(w, File.ReadAllText(Path.Combine(copy, NetworkPlanStore.FileName)), out string problem);
        Check.True(f != null, "plan: " + problem);
        var editor = RoadNetwork.ForEditing(w, f.Spec);
        RoadNetwork.Log = Console.WriteLine;
        int roads = 0;
        foreach (var road in f.Plan.Roads.Where(r => r.Via.Count > 0).ToList())
        {
            int before = SelfCrossings(road.Points);
            var line = editor.Reroute(f.Plan, road, road.Via, out problem);
            Check.True(line != null, "no course: " + problem);
            Console.WriteLine($"       road {f.Plan.Roads.IndexOf(road)}: {road.Via.Count} via points, self crossings {before} → {SelfCrossings(line)}, length {road.Length:0} → {RoadNetwork.Project(line, line[^1]).Along:0} m");
            if (Environment.GetEnvironmentVariable("YAMEE_TEST_IMAGES") is string imgDir) DrawRoute(w, Path.Combine(imgDir, $"reroute_{f.Plan.Roads.IndexOf(road)}.png"), road.Points, line, road.Via, f.Plan);
            Check.Equal(0, SelfCrossings(line), "self crossings");
            foreach (var v in road.Via) Check.True(RoadNetwork.Project(line, v).Off < RoadNetwork.ViaRadius + 12, "via point not passed");
            roads++;
        }
        if (roads == 0) Check.Skip("the saved plan has no via points");
    }

    [Test("Towns: places near the map edge rank lower (soft), not at all from EdgeSoft inward, shorter on small maps")]
    static void TownEdgeCost()
    {
        const int size = 8192, half = size / 2;
        double margin = 500;
        double Cost(double fromEdge) => CityGenerator.EdgeCost(-half + fromEdge, 0, margin, size, 1000);
        Check.Equal(0.0, Cost(1000), "at the soft limit");
        Check.Equal(0.0, Cost(3000), "inside");
        Check.True(Cost(500) > Cost(700) && Cost(700) > Cost(900) && Cost(900) > 0, "rising toward the edge");
        Check.True(Cost(900) < 0.1 * Cost(500), "a little inside the limit costs little");
        Check.Equal(0.0, CityGenerator.EdgeCost(-half + 500, 0, margin, size, 0), "0 = off");
        // 4k: limit size/6 = 683 m
        Check.Equal(0.0, CityGenerator.EdgeCost(-2048 + 700, 0, margin, 4096, 1000), "4k beyond 683 m");
        Check.True(CityGenerator.EdgeCost(-2048 + 550, 0, margin, 4096, 1000) > 0, "4k near the edge");
    }

    [Test("Towns: more earthwork gives a wider, gentler transition beside the tile (1.0 unchanged)")]
    static void TownEarthworkTransition()
    {
        (int Reach, double Steep, string State) Shape(double earthwork)
        {
            var w = Fixture.LoadCopy();
            var def = new PrefabDef { SizeX = 150, SizeZ = 150, Kind = PrefabKind.Tile, Found = true };
            double mean = 0; for (int z = -75; z < 75; z += 5) for (int x = -75; x < 75; x += 5) mean += w.HeightAt(x, z) / 900;
            var tile = new PlacedPrefab { Name = "rwg_tile_test", X = -75, Z = -75, Y = (int)Math.Round(mean) + 20, Def = def };   // 20 m above the ground
            var plan = new CityPlan { Tiles = { tile }, X0 = -75, Z0 = -75, X1 = 75, Z1 = 75 };
            var before = (ushort[])w.Heights.Clone();
            var e = new WorldEdits(w);
            e.Run("tile", "tile", ed => CityGenerator.ShapeTerrain(w, ed, plan, earthwork));
            int reach = 0; double steep = 0;
            for (int x = 76; x < 300; x++)   // east of the tile along z = 0
            {
                int i = w.Half * w.Size + x + w.Half;
                if (Math.Abs(w.Heights[i] - before[i]) > 13) reach = x - 75;   // changed by more than 5 cm
                steep = Math.Max(steep, Math.Abs(w.Heights[i + 4] - w.Heights[i]) / 256.0 / 4);
            }
            string state = Fixture.State(w);
            e.Undo();
            Check.Equal(Check.Hash(before), Check.Hash(w.Heights), "undone exactly");
            return (reach, steep, state);
        }
        var a = Shape(1); var a2 = Shape(1); var b = Shape(1.5);
        Console.WriteLine($"       transition 1.0: {a.Reach} m, steepest {a.Steep * 100:0} %; 1.5: {b.Reach} m, steepest {b.Steep * 100:0} %");
        Check.Equal(a.State, a2.State, "same result twice");
        Check.True(b.Reach > a.Reach, "1.5 reaches farther");
        Check.True(b.Steep <= a.Steep + 0.01, "1.5 not steeper");
    }

    [Test("Towns: seven size classes (2k–16k), the same town sizes in every biome, big cities by default only in the wasteland")]
    static void TownSizeClasses()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? AppSettings.GuessGameFolder();
        if (game == null) Check.Skip("no game folder found");
        var expect = new[] { (2048, "2k"), (4096, "4k"), (6144, "6k"), (8192, "8k"), (10240, "10k"), (12288, "12k"), (16384, "16k") };
        int previous = 0;
        foreach (var (size, cls) in expect)
        {
            var rules = CityRules.Load(game, size);
            Check.Equal(cls, rules.WorldClass, $"size class of {size} m");
            Check.Equal(21, rules.Types.Count, $"town rows for {cls} (4 sizes in 5 biomes + Old West)");
            for (int b = 0; b < 5; b++)
                foreach (var s in new[] { "countrytown", "town", "city", "citybig" })
                    Check.True(rules.Types.Count(t => t.Biomes.Single() == b && t.Size == s) == 1, $"{cls}: {s} in biome {b}");
            Check.True(rules.Types.Where(t => t.Size == "citybig" && t.Count > 0).All(t => t.Biomes[0] == 4), $"{cls}: big cities only in the wasteland");
            Check.True(rules.Types.All(t => t.Districts.Count > 0 && t.TilesMin >= 1 && t.TilesMax >= t.TilesMin), $"{cls}: districts and tiles");
            int total = rules.Types.Sum(t => t.Count);
            Check.True(total > previous, $"{cls}: more towns on larger maps ({total} <= {previous})");
            previous = total;
        }
    }

    [Test("Towns user.ini / default.ini: per-size-class persistence and reset to defaults")]
    static void TownCityTypesPerClassAndReset()
    {
        // verify all 7 size classes (2k–16k) via a minimal synthetic rwgmixer.xml (no game folder needed)
        string fakeGame = Path.Combine(Path.GetTempPath(), "yamee_test_game_" + Guid.NewGuid().ToString("N"));
        try
        {
            string cfg = Path.Combine(fakeGame, "Data", "Config");
            Directory.CreateDirectory(cfg);
            File.WriteAllText(Path.Combine(cfg, "rwgmixer.xml"), """
                <rwgmixer>
                    <township name="countrytown" />
                    <township name="town" />
                    <township name="city" />
                    <township name="citybig" />
                    <township name="oldwest" />
                    <district name="commercial"><property name="required_township" value="countrytown,town,city,citybig,oldwest" /></district>
                </rwgmixer>
                """);
            int prevTotal = 0;
            foreach (var (sz, cls) in new[] { (2048, "2k"), (4096, "4k"), (6144, "6k"), (8192, "8k"), (10240, "10k"), (12288, "12k"), (16384, "16k") })
            {
                Check.Equal(cls, CityRules.ClassOf(sz), $"ClassOf({sz})");
                var r = CityRules.Load(fakeGame, sz);
                Check.Equal(cls, r.WorldClass, $"WorldClass({sz})");
                Check.Equal(21, r.Types.Count, $"21 town rows for {cls}");
                Check.True(r.Types.Where(t => t.Size == "citybig" && t.Count > 0).All(t => t.Biomes[0] == 4), $"{cls}: big cities only in wasteland");
                Check.True(r.Types.All(t => t.TilesMin >= 1 && t.TilesMax >= t.TilesMin), $"{cls}: valid tile ranges");
                int total = r.Types.Sum(t => t.Count);
                Check.True(total > prevTotal, $"{cls}: total towns increases ({total} <= {prevTotal})");
                prevTotal = total;
            }
        }
        finally { try { Directory.Delete(fakeGame, true); } catch { } }
        // synthetic town types for two size classes (no game folder needed)
        List<TownType> Make(int ctForest, int cWasteland) => new()
        {
            new TownType { Name = "forest_countrytown", Size = "countrytown", Biomes = new[] { 0 }, PerBiome = true, Count = ctForest, TilesMin = 4, TilesMax = 7 },
            new TownType { Name = "wasteland_city", Size = "city", Biomes = new[] { 4 }, PerBiome = true, Count = cWasteland, TilesMin = 7, TilesMax = 14 },
        };
        var rules4 = Make(2, 1);
        var rules8 = Make(4, 3);

        // Save user override for 4k
        var cur4 = Make(5, 1);
        UserValues.RememberCityTypes(cur4, rules4, "4k");

        // Save user override for 8k -> must NOT erase 4k entries in user.ini
        var cur8 = Make(6, 3);
        UserValues.RememberCityTypes(cur8, rules8, "8k");

        var loaded4 = Make(2, 1);
        UserValues.ApplyCityTypes(loaded4, "4k");
        Check.Equal(5, loaded4[0].Count, "4k override preserved after saving 8k");

        var loaded8 = Make(4, 3);
        UserValues.ApplyCityTypes(loaded8, "8k");
        Check.Equal(6, loaded8[0].Count, "8k override loaded from user.ini");

        // ApplyCityTypeDefaults ignores user.ini and restores default.ini / built-in defaults
        var def8 = Make(4, 3);
        def8[0].Count = 99;   // simulate overridden value
        UserValues.EnsureCityTypes(rules8, "8k");
        UserValues.ApplyCityTypeDefaults(def8, "8k");
        Check.True(def8[0].Count != 6 && def8[0].Count != 99, $"ApplyCityTypeDefaults ignores user.ini (got {def8[0].Count})");

        // LoadDefaults ignores [Cities] overrides in user.ini
        var customSpec = new CitySpec { MaxParts = 1, Elevation = 2 };
        UserValues.Remember(customSpec);
        var fromUser = UserValues.Load(new CitySpec());
        Check.Equal(1, fromUser.MaxParts, "user.ini takes precedence on Load");
        var fromDefault = UserValues.LoadDefaults(new CitySpec());
        Check.True(fromDefault.MaxParts != 1, "LoadDefaults ignores user.ini");

        // Resetting 8k back to defaults removes 8k.* from user.ini while keeping 4k.*
        UserValues.RememberCityTypes(def8, rules8, "8k");
        var check4 = Make(2, 1);
        UserValues.ApplyCityTypes(check4, "4k");
        Check.Equal(5, check4[0].Count, "4k override still preserved after resetting 8k");
        var check8 = Make(4, 3);
        UserValues.ApplyCityTypes(check8, "8k");
        Check.Equal(def8[0].Count, check8[0].Count, "8k back to default after saving reset values");

        // Clean up temp user.ini overrides
        UserValues.Remember(fromDefault);
        var def4 = Make(2, 1);
        UserValues.ApplyCityTypeDefaults(def4, "4k");
        UserValues.RememberCityTypes(def4, rules4, "4k");
    }

    // ---------- optional: with a real map (environment variable YAMEE_TEST_WORLD = project folder with towns) ----------

    [Test("Road network (real map): node tiles – every exit gets exactly one road, roads run straight into it, plan saves and loads")]
    static void NodeTilesRealMap()
    {
        string folder = Environment.GetEnvironmentVariable("YAMEE_TEST_NODE_WORLD") ?? Environment.GetEnvironmentVariable("YAMEE_TEST_WORLD");
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? AppSettings.GuessGameFolder();
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "dtm.raw")) || game == null)
            Check.Skip("set YAMEE_TEST_NODE_WORLD to a project folder with towns and without country roads (it is only read)");
        var w = WorldData.Load(folder, game, new Progress<string>());
        var files = PrefabCatalog.AllFiles(game);
        PrefabDef Def(string n) { if (!files.TryGetValue(n, out var f)) return null; var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Tile; return d.Found ? d : null; }
        var defs = RoadNetwork.NodeTileNames.ToDictionary(n => n, Def);
        RoadNetwork.TileDefs = n => defs.GetValueOrDefault(n);
        try
        {
            // settings of the saved plan of the project (as the user planned), otherwise the defaults
            var spec = NetworkPlanStore.Load(w, out _)?.Spec ?? new NetworkSpec { Seed = w.Name };
            if (int.TryParse(Environment.GetEnvironmentVariable("YAMEE_TEST_NODES"), out int nt)) spec.NodeTiles = nt;
            else if (spec.NodeTiles == 0) spec.NodeTiles = 1;
            RoadNetwork.NodeExitPass = Environment.GetEnvironmentVariable("YAMEE_TEST_NODE_NOPASS") == null;
            RoadNetwork.NodeRejects.Clear();
            if (Environment.GetEnvironmentVariable("YAMEE_TEST_LOG") != null) RoadNetwork.Log = s => { if (s.StartsWith("node")) Console.WriteLine("         " + s); };
            var plan = new RoadNetwork(w, spec).Plan(new Progress<string>());
            RoadNetwork.Log = null;
            Console.WriteLine($"       {plan.Roads.Count} roads, {plan.Junctions.Count} junctions, {plan.CrossingPoints.Count} crossings; candidates failed at check: "
                + string.Join(", ", RoadNetwork.NodeRejects.OrderByDescending(k => k.Value).Select(k => $"{k.Key} ×{k.Value}")));
            Check.True(plan.NodeTiles.Count > 0, "no node tile placed");
            foreach (var t in plan.NodeTiles)
            {
                var town = plan.Towns.Single(q => q.Node && q.Tiles.Contains(t));
                foreach (var e in town.Exits)
                {
                    var roads = plan.Roads.Where(r => r.From == e || r.To == e).ToList();
                    Check.Equal(1, roads.Count, $"roads at exit {e.X:0}/{e.Z:0} of {t.Name} at {t.X}/{t.Z}");
                    var r = roads[0];
                    var pts = r.From == e ? r.Points : Enumerable.Reverse(r.Points).ToList();
                    Check.True((pts[0] - new Point(e.X, e.Z)).Length < 0.5, "road does not end at the exit");
                    var at30 = RoadNetwork.At(pts, 30).P;
                    double side = Math.Abs((at30.X - e.X) * e.DZ - (at30.Y - e.Z) * e.DX);
                    Check.True(side < 1, $"road not straight out of the exit ({side:0.0} m beside the axis 30 m out)");
                    Check.True(!RoadNetwork.WrapsAroundTile(t, e.DX, e.DZ, pts), $"road wraps around node tile at {t.X}/{t.Z} from exit {e.X:0}/{e.Z:0}");
                    var otherEnd = r.From == e ? r.EndRoad : r.StartRoad;
                    Check.True(otherEnd == null || (otherEnd.From?.Town != town && otherEnd.To?.Town != town),
                        $"road from node tile {t.X}/{t.Z} loops back onto another road of the same tile");
                }
                // no road crosses the tile area
                foreach (var r in plan.Roads)
                    foreach (var p in r.Points)
                        Check.True(!(p.X > t.X + 3 && p.X < t.X + t.SizeX - 3 && p.Y > t.Z + 3 && p.Y < t.Z + t.SizeZ - 3), $"road runs through the node tile at {t.X}/{t.Z}");
                Console.WriteLine($"       {t.Name} at {t.X}/{t.Z} (rotation {t.Rotation}, y {t.Y})");
            }
            // build order: a road only branches off roads before it; all references point into the plan
            for (int i = 0; i < plan.Roads.Count; i++)
            {
                var r = plan.Roads[i];
                if (r.StartRoad != null) Check.True(plan.Roads.IndexOf(r.StartRoad) is int s && s >= 0 && s < i, "start road after the road or missing");
                if (r.EndRoad != null) Check.True(plan.Roads.IndexOf(r.EndRoad) is int s2 && s2 >= 0 && s2 < i, "end road after the road or missing");
                Check.True(r.CrossedRoads.All(c => plan.Roads.Contains(c)), "crossed road missing");
            }
            var nodeRoads = plan.Roads.Where(r => plan.Towns.Any(q => q.Node && (q.Exits.Contains(r.From) || q.Exits.Contains(r.To)))).ToList();
            // the roads at the tiles are not more angular than the others (sharpest turn between two points of the line)
            var plain = new RoadNetwork(w, new NetworkSpec { Seed = w.Name, NodeTiles = 0 }).Plan(new Progress<string>());
            double plainTurn = plain.Roads.Max(r => SharpestTurn(r.Points)), nodeTurn = nodeRoads.Max(r => SharpestTurn(r.Points));
            Console.WriteLine($"       {plan.NodeTiles.Count} node tile(s), {plan.Roads.Count} roads ({plain.Roads.Count} without node tiles, {plan.Roads.Sum(r => r.Length) / 1000:0.0} against {plain.Roads.Sum(r => r.Length) / 1000:0.0} km); "
                + $"sharpest turn at node tiles {nodeTurn:0}°, without node tiles {plainTurn:0}°; unused exits {plan.Towns.Sum(t => t.Exits.Count(e => e.Reachable && !e.Used))} against {plain.Towns.Sum(t => t.Exits.Count(e => e.Reachable && !e.Used))}");
            Check.True(nodeTurn <= Math.Max(45, plainTurn + 5), $"roads at node tiles turn sharply ({nodeTurn:0}°)");
            Check.True(plan.Towns.Count(t => !t.Node && t.Connected) >= plain.Towns.Count(t => t.Connected), "a town lost its connection through the node tiles");
            var text = NetworkPlanStore.Serialize(new NetworkPlanFile { Plan = plan, Spec = spec });
            var back = NetworkPlanStore.Deserialize(w, text, out string problem);
            Check.True(back != null, "plan with node tiles not loaded: " + problem);
            Check.Equal(text, NetworkPlanStore.Serialize(back), "plan after loading");

            // step 3a: delete a road at a crossing tile by hand → the tile becomes a T (or goes), every remaining exit has one road
            void ExitsServed(NetworkPlan pl, string what)
            {
                foreach (var t in pl.NodeTiles)
                    foreach (var e in pl.Towns.Single(q => q.Node && q.Tiles.Contains(t)).Exits)
                    {
                        var rs = pl.Roads.Where(r => r.From == e || r.To == e).ToList();
                        Check.Equal(1, rs.Count, $"{what}: roads at exit {e.X:0}/{e.Z:0} of {t.Name}");
                        var pts = rs[0].From == e ? rs[0].Points : Enumerable.Reverse(rs[0].Points).ToList();
                        Check.True((pts[0] - new Point(e.X, e.Z)).Length < 0.5, $"{what}: road does not end at the exit of {t.Name}");
                    }
            }
            var copy = NetworkPlanStore.Deserialize(w, text, out _).Plan;
            var cross = copy.NodeTiles.FirstOrDefault(t => t.Name == RoadNetwork.NodeX);
            if (cross != null)
            {
                var ct = copy.Towns.Single(q => q.Node && q.Tiles.Contains(cross));
                var victim = copy.Roads.First(r => ct.Exits.Contains(r.From) || ct.Exits.Contains(r.To));
                NetworkPlanEdit.Delete(copy, victim, false);
                int changed = RoadNetwork.AdaptNodeTiles(w, copy, spec);
                var now = copy.NodeTiles.FirstOrDefault(t => t.X == cross.X && t.Z == cross.Z);
                Console.WriteLine($"       deleted a road at the crossing {cross.X}/{cross.Z}: {changed} tile(s) adapted, now {now?.Name ?? "removed"}");
                Check.True(changed >= 1 && now?.Name != RoadNetwork.NodeX, "crossing tile not adapted after deleting one of its roads");
                ExitsServed(copy, "after deleting");
            }

            // step 3b: move a node tile by hand (a few places around it): the roads follow to the new exits
            var editor = RoadNetwork.ForEditing(w, spec);
            var moveCopy = NetworkPlanStore.Deserialize(w, text, out _).Plan;
            var tileToMove = moveCopy.NodeTiles[0];
            string moveProblem = "no place tried";
            int mx = 0, mz = 0;
            foreach (var (ddx, ddz) in new[] { (80, 0), (-80, 0), (0, 80), (0, -80), (120, 120), (-120, -120), (40, 0), (0, 40) })
            {
                moveProblem = editor.MoveNodeTile(moveCopy, tileToMove, tileToMove.X + ddx, tileToMove.Z + ddz);
                if (moveProblem == null) { mx = ddx; mz = ddz; break; }
            }
            Console.WriteLine($"       moving the node tile at {tileToMove.X}/{tileToMove.Z}: {(moveProblem == null ? $"moved by {mx}/{mz} m" : moveProblem)}");
            Check.True(moveProblem == null, "node tile could not be moved anywhere: " + moveProblem);
            var movedTile = moveCopy.NodeTiles.Single(t => t.X == tileToMove.X + mx && t.Z == tileToMove.Z + mz);
            ExitsServed(moveCopy, "after moving");
            foreach (var r in moveCopy.Roads)
                foreach (var q in r.Points)
                    Check.True(!(q.X > movedTile.X + 3 && q.X < movedTile.X + movedTile.SizeX - 3 && q.Y > movedTile.Z + 3 && q.Y < movedTile.Z + movedTile.SizeZ - 3), "road runs through the moved node tile");
            var moveText = NetworkPlanStore.Serialize(new NetworkPlanFile { Plan = moveCopy, Spec = spec });
            Check.Equal(moveText, NetworkPlanStore.Serialize(NetworkPlanStore.Deserialize(w, moveText, out _)), "moved plan after loading");

            // step 3c (2026-11-16): exchange a node tile (T → X, then turn it, then X → T): its roads stay at exits, new exits are free
            var swapCopy = NetworkPlanStore.Deserialize(w, text, out _).Plan;
            var tTile = swapCopy.NodeTiles.FirstOrDefault(t => RoadNetwork.KindOf(t) == "T");
            if (tTile != null)
            {
                void Served(NetworkPlan pl, string what)
                {
                    foreach (var t in pl.NodeTiles)
                        foreach (var e in pl.Towns.Single(q => q.Node && q.Tiles.Contains(t)).Exits)
                        {
                            var rs = pl.Roads.Where(r => r.From == e || r.To == e).ToList();
                            Check.True(rs.Count <= 1, $"{what}: {rs.Count} roads at exit {e.X:0}/{e.Z:0} of {t.Name}");
                            if (rs.Count == 0) { Check.True(!e.Used, $"{what}: free exit marked as used"); continue; }
                            var pts = rs[0].From == e ? rs[0].Points : Enumerable.Reverse(rs[0].Points).ToList();
                            Check.True((pts[0] - new Point(e.X, e.Z)).Length < 0.5, $"{what}: road does not end at the exit of {t.Name}");
                        }
                }
                int roadsBefore = swapCopy.Roads.Count;
                string sp = editor.ChangeNodeTile(swapCopy, tTile, "X", false);
                var asX = swapCopy.NodeTiles.Single(t => t.X == tTile.X && t.Z == tTile.Z);
                Console.WriteLine($"       exchanging the T at {tTile.X}/{tTile.Z}: {sp ?? "now " + asX.Name}");
                Check.True(sp == null && RoadNetwork.KindOf(asX) == "X", "T could not be exchanged for a crossing: " + sp);
                Check.Equal(4, swapCopy.Towns.Single(q => q.Node && q.Tiles.Contains(asX)).Exits.Count, "exits of the crossing");
                Check.Equal(roadsBefore, swapCopy.Roads.Count, "roads after exchanging");
                Served(swapCopy, "after exchanging");
                sp = editor.ChangeNodeTile(swapCopy, asX, null, true);
                var turned = swapCopy.NodeTiles.Single(t => t.X == tTile.X && t.Z == tTile.Z);
                Console.WriteLine($"       turning it: {sp ?? "rotation " + asX.Rotation + " → " + turned.Rotation}");
                Check.True(sp == null && turned.Rotation == (asX.Rotation + 1) % 4, "crossing could not be turned: " + sp);
                Served(swapCopy, "after turning");
                sp = editor.ChangeNodeTile(swapCopy, turned, "T", false);
                Check.True(sp == null, "crossing could not be exchanged back for a T: " + sp);
                Served(swapCopy, "after exchanging back");
                var swapText = NetworkPlanStore.Serialize(new NetworkPlanFile { Plan = swapCopy, Spec = spec });
                Check.Equal(swapText, NetworkPlanStore.Serialize(NetworkPlanStore.Deserialize(w, swapText, out _)), "exchanged plan after loading");
            }

            // step 3d (2026-11-16): dragging roads – a point far beside the road never gives a road over itself (picture SG01);
            // close to a tile exit only with Alt (free) the road may change after 24 m instead of 44 m
            var dragCopy = NetworkPlanStore.Deserialize(w, text, out _).Plan;
            int refused = 0, rerouted = 0, nearNormal = 0, nearFree = 0;
            foreach (var road in dragCopy.Roads.Where(r => !r.Gravel && r.Length > 700).Take(12).ToList())
            {
                double len = RoadNetwork.Project(road.Points, road.Points[^1]).Along;
                foreach (var (at, side) in new[] { (0.5, 90.0), (0.3, -160.0), (0.6, 220.0) })
                {
                    var (p0, dir, _) = RoadNetwork.At(road.Points, len * at);
                    var via = p0 + new Vector(-dir.Y, dir.X) * side;
                    var l = editor.Reroute(dragCopy, road, new[] { via }, out string pr, out _);
                    if (l == null) { refused++; continue; }
                    rerouted++;
                    Check.Equal(0, SelfCrossings(l), $"self crossings after dragging road {dragCopy.Roads.IndexOf(road)} to {via.X:0}/{via.Y:0}");
                }
                if (road.From != null)
                {
                    var (p1, d1, _) = RoadNetwork.At(road.Points, 36);
                    var near = new[] { p1 + new Vector(-d1.Y, d1.X) * 6 };
                    if (editor.Reroute(dragCopy, road, near, out _, out _) != null) nearNormal++;
                    if (editor.Reroute(dragCopy, road, near, out _, out _, null, true) != null) nearFree++;
                }
            }
            Console.WriteLine($"       dragging roads: {rerouted} rerouted without crossing themselves, {refused} refused; point 36 m from a town exit: normal {nearNormal}, with Alt {nearFree}");
            Check.True(rerouted > 0, "no road could be dragged");
            Check.Equal(0, nearNormal, "a point 36 m from the exit was taken without Alt");
            Check.True(nearFree > 0, "with Alt no point 36 m from the exit could be taken");

            // step 2: build (tiles, level ground, roads, buildings on the lots) and undo exactly
            var index = files.Where(kv => kv.Key.StartsWith("roadside_", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("motel", StringComparison.OrdinalIgnoreCase)
                    || kv.Key.Contains("hotel", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("gas_station", StringComparison.OrdinalIgnoreCase))
                .Select(kv => (kv.Key, D: PrefabCatalog.Describe(kv.Value))).Where(x => x.D.Found).ToDictionary(x => x.Key, x => x.D, StringComparer.OrdinalIgnoreCase);
            var lots = NetworkBuilder.NodeLotBuildings(plan, index, null, spec.Seed);
            var ed = new WorldEdits(w);
            string before = Fixture.State(w);
            int prefabs = w.Prefabs.Count;
            // steps in the ground around the tiles (0–60 m outside): difference between neighboring metres, before and after
            double StepAt(int x, int z) { double h = w.HeightAt(x, z); return Math.Max(Math.Abs(w.HeightAt(x + 1, z) - h), Math.Abs(w.HeightAt(x, z + 1) - h)); }
            IEnumerable<(int X, int Z)> Around(PlacedPrefab t)
            {
                for (int z = t.Z - 60; z < t.Z + t.SizeZ + 60; z++)
                    for (int x = t.X - 60; x < t.X + t.SizeX + 60; x++)
                        if (!t.Contains(x, z) && !t.Contains(x + 1, z) && !t.Contains(x, z + 1)) yield return (x, z);
            }
            var stepBefore = new Dictionary<(int, int), double>();
            foreach (var t in plan.NodeTiles) foreach (var q in Around(t)) stepBefore[q] = StepAt(q.X, q.Z);
            var res = ed.Run("net", "net", x => NetworkBuilder.Build(w, x, plan, spec, new Progress<string>(), lots));
            Check.Equal(plan.NodeTiles.Count + lots.Count, w.Prefabs.Count - prefabs, "prefabs added (tiles and lot buildings)");
            Check.True(lots.Count > 0, "no building on the lots of the node tiles");
            double worstFloor = 0, worstExit = 0;
            foreach (var t in plan.NodeTiles)
            {
                Check.True(w.Prefabs.Contains(t), "node tile not on the map");
                for (int z = t.Z + 3; z < t.Z + t.SizeZ - 3; z += 7)
                    for (int x = t.X + 3; x < t.X + t.SizeX - 3; x += 7) worstFloor = Math.Max(worstFloor, Math.Abs(w.HeightAt(x, z) - t.GroundHeight));
                // road in front of every exit at the height of the tile (no step at the tile edge)
                foreach (var e in plan.Towns.Single(q => q.Node && q.Tiles.Contains(t)).Exits)
                    worstExit = Math.Max(worstExit, Math.Abs(w.HeightAt((int)Math.Floor(e.X + e.DX * 3), (int)Math.Floor(e.Z + e.DZ * 3)) - t.TerrainHeight));
            }
            double worstStep = 0, worstNew = 0; string stepAt = "", newAt = "";
            foreach (var ((x, z), d0) in stepBefore)
            {
                double d = StepAt(x, z);
                if (d > worstStep) { worstStep = d; stepAt = $"{x}/{z} (before {d0:0.00})"; }
                if (d - d0 > worstNew) { worstNew = d - d0; newAt = $"{x}/{z} ({d0:0.00} → {d:0.00})"; }
            }
            Console.WriteLine($"       around the node tiles (60 m): largest step {worstStep:0.00} m at {stepAt}; largest new step {worstNew:0.00} m at {newAt}");
            Console.WriteLine($"       built: {lots.Count} lot building(s) ({string.Join(", ", lots.Select(l => l.Building.Name).Distinct())}); ground under the tiles at most {worstFloor:0.00} m off, road 3 m in front of the exits at most {worstExit:0.00} m off the tile");
            Check.True(worstFloor < 0.05, $"ground under a node tile not level ({worstFloor:0.00} m)");
            Check.True(worstExit < 0.6, $"step between road and node tile ({worstExit:0.00} m)");
            Check.True(worstNew < 2.5, $"new step of {worstNew:0.00} m around a node tile at {newAt}");
            ed.Undo();
            Check.Equal(before, Fixture.State(w), "after undo");
            Check.Equal(prefabs, w.Prefabs.Count, "prefabs after undo");
        }
        finally { RoadNetwork.TileDefs = null; RoadNetwork.NodeExitPass = true; }
    }

    [Test("Road network (real map): line shape – curves without kinks, no steeper than before (measurement)")]
    static void RoadShapeRealMap()
    {
        var w = TownWorld();
        var spec = new NetworkSpec { Seed = w.Name, NodeTiles = 0 };
        var plan = new RoadNetwork(w, spec).Plan(new Progress<string>());
        RoadShapeReport(plan);
    }

    /// YAMEE_TEST_CITY_WORLD = project with a terrain copy: copied, reset, towns generated (without lot buildings, no country roads yet)
    static WorldData TownWorld()
    {
        var (w, game) = CityTestWorld();
        var rules = CityRules.Load(game, w.Size);
        var files = PrefabCatalog.AllFiles(game);
        var defs = files.Keys.Where(n => n.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(n => n, n => { var d = PrefabCatalog.Describe(files[n]); d.Kind = PrefabKind.Tile; return d; }, StringComparer.OrdinalIgnoreCase);
        var cspec = new CitySpec { Types = rules.Types.Select(t => t.Clone()).ToList(), Seed = w.Name, FillLots = false, TradersPerBiome = 0 };
        var towns = new CityGenerator(w, cspec, rules, defs.Keys, n => defs.TryGetValue(n, out var d) && d.Found ? d : null).Plan(new Progress<string>());
        new WorldEdits(w).Run("towns", "", ed =>
        {
            foreach (var p in towns) { foreach (var t in p.Tiles) { ed.AddPrefab(t); ed.AddTileSurface(t); } foreach (var q in p.Parts) ed.AddPrefab(q); CityGenerator.ShapeTerrain(w, ed, p); }
            foreach (var p in towns) foreach (var road in p.Roads) { var r = CityGenerator.PlanRoad(w, road, cspec.RoadGrade); if (r != null) RoadApplier.Apply(w, ed, r, new Progress<string>()); }
        });
        return w;
    }

    static void RoadShapeReport(NetworkPlan plan)
    {
        // curvature on 8 m chords (points every 4 m), without 50 m at both ends (straight connecting pieces)
        double len = 0, tight = 0, mid = 0, straight = 0, kink = 0;
        var radii = new List<double>();
        foreach (var r in plan.Roads)
        {
            // as the builder lays it: control points every 12 m, curve through them
            var path = RoadPlanner.BuildPath(NetworkBuilder.Even(r.Points, 12));
            var s = new List<Point>();
            double total = path[^1].Dist;
            for (int k = 0; k < path.Count; k++)
                if (path[k].Dist >= 50 && path[k].Dist <= total - 50 && (s.Count == 0 || path[k].Dist >= 50 + 4 * s.Count)) s.Add(new Point(path[k].X, path[k].Z));
            double minR = double.MaxValue;
            for (int q = 2; q + 2 < s.Count; q++)
            {
                double a = Math.Abs(Vector.AngleBetween(s[q] - s[q - 2], s[q + 2] - s[q])) * Math.PI / 180;
                double rad = a < 1e-6 ? 1e9 : 8 / a;
                // kink: curvature here far above the mean of ±40 m around (curve concentrated on a few meters)
                double around = 0; int cnt = 0;
                for (int k = Math.Max(2, q - 10); k <= Math.Min(s.Count - 3, q + 10); k++, cnt++)
                    around += Math.Abs(Vector.AngleBetween(s[k] - s[k - 2], s[k + 2] - s[k]));
                double here = Math.Abs(Vector.AngleBetween(s[q] - s[q - 2], s[q + 2] - s[q]));
                len += 4;
                if (rad < 60) tight += 4; else if (rad < 150) mid += 4;
                if (rad > 2000) straight += 4;
                if (here > 2.5 && here > 3 * around / cnt) kink += 4;
                minR = Math.Min(minR, rad);
            }
            if (minR < 1e8) radii.Add(minR);
        }
        radii.Sort();
        Console.WriteLine($"       {plan.Roads.Count} roads, {len / 1000:0.0} km measured; radius < 60 m {100 * tight / len:0.0} %, 60–150 m {100 * mid / len:0.0} %, "
            + $"nearly straight (> 2 km) {100 * straight / len:0.0} %, kinks {100 * kink / len:0.00} %; tightest curve per road median {radii[radii.Count / 2]:0} m, min {radii[0]:0} m");
        Console.WriteLine($"       steepest {plan.Roads.Max(r => r.MaxGrade) * 100:0.0} %, steep share mean {plan.Roads.Average(r => r.SteepShare) * 100:0.0} %, "
            + $"deepest clearing {plan.Roads.Max(r => r.MaxCut):0.0} m, practically straight {plan.StraightShare * 100:0.0} %, {plan.Seconds:0.0} s, old smoothing {RoadNetwork.SplineFallbacks}×, smooth {RoadNetwork.SmoothCalls} calls {RoadNetwork.SmoothMs} ms");
        foreach (var r in plan.Roads.OrderByDescending(r => r.MaxGrade).Take(3))
            Console.WriteLine($"         steep {r.MaxGrade * 100:0.0} % at {r.SteepAt?.X:0}/{r.SteepAt?.Y:0}, {r.Length:0} m{(r.Loop ? ", loop" : "")}{(r.Feeder ? ", feeder" : "")}{(r.Cut ? ", cut" : "")}{(r.ToEdge ? ", to edge" : "")}");
    }

    [Test("Road network (real map): exits near the map edge without a connection get a road out to the edge, town tiles stay")]
    static void EdgeRoadsRealMap()
    {
        string folder = Environment.GetEnvironmentVariable("YAMEE_TEST_NODE_WORLD") ?? Environment.GetEnvironmentVariable("YAMEE_TEST_WORLD");
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? AppSettings.GuessGameFolder();
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "dtm.raw")) || game == null)
            Check.Skip("set YAMEE_TEST_NODE_WORLD to a project folder with towns and without country roads (it is only read)");
        var w = WorldData.Load(folder, game, new Progress<string>());
        var files = PrefabCatalog.AllFiles(game);
        PrefabDef Def(string n) { if (!files.TryGetValue(n, out var f)) return null; var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Tile; return d.Found ? d : null; }
        var defs = RoadNetwork.NodeTileNames.ToDictionary(n => n, Def);
        RoadNetwork.TileDefs = n => defs.GetValueOrDefault(n);
        try
        {
            var spec = NetworkPlanStore.Load(w, out _)?.Spec ?? new NetworkSpec { Seed = w.Name, NodeTiles = 1 };
            RoadNetwork.EdgeRoadPass = false;
            var without = new RoadNetwork(w, spec).Plan(new Progress<string>());
            RoadNetwork.EdgeRoadPass = true;
            var streetCross = new List<Point>();
            RoadNetwork.Log = s =>
            {
                if (s.StartsWith("last pass") || s.StartsWith("edge road") || s.StartsWith("street")) Console.WriteLine("       " + s);
                if (s.StartsWith("street crossing at ") && s[19..].Split('/') is { Length: 2 } xy) streetCross.Add(new Point(double.Parse(xy[0]), double.Parse(xy[1])));
            };
            var plan = new RoadNetwork(w, spec).Plan(new Progress<string>());
            RoadNetwork.Log = null;
            int deadBefore = without.Towns.Where(t => !t.Node).Sum(t => t.Exits.Count(e => e.Reachable && !e.Used));
            int deadAfter = plan.Towns.Where(t => !t.Node).Sum(t => t.Exits.Count(e => e.Reachable && !e.Used));
            var edge = plan.Roads.Where(r => r.ToEdge).ToList();
            Console.WriteLine($"       dead ends {deadBefore} → {deadAfter}, {edge.Count} road(s) to the map edge: "
                + string.Join(", ", edge.Select(r => $"{r.Points[0].X:0}/{r.Points[0].Y:0} ({r.Length:0} m)")));
            Check.True(deadAfter <= deadBefore, "roads to the edge add no dead ends");
            // the user's report 2026-11-16: two roads side by side out of the map from one T gateway
            Check.Equal(edge.Count, edge.Select(r => r.From.Town).Distinct().Count(), "at most one road to the edge per town");
            for (int a = 0; a < edge.Count; a++)
                for (int b = a + 1; b < edge.Count; b++)
                    Check.True((edge[a].Points[^1] - edge[b].Points[^1]).Length >= 999, "two roads reach the edge close together");
            int half = w.Size / 2;
            foreach (var r in edge)
            {
                var end = r.Points[^1];
                Check.True(half - Math.Max(Math.Abs(end.X), Math.Abs(end.Y)) < 3, $"road from {r.Points[0].X:0}/{r.Points[0].Y:0} does not end at the edge");
                Check.True(r.From?.Tile != null && r.To == null && r.EndRoad == null, "road to the edge starts at a town exit and ends freely");
                // on its way it keeps the 80 m to the edge, except the straight piece out at the end (at most about 150 m)
                double walked = 0, total = 0;
                for (int q = 1; q < r.Points.Count; q++) total += (r.Points[q] - r.Points[q - 1]).Length;
                for (int q = 0; q < r.Points.Count; q++)
                {
                    if (q > 0) walked += (r.Points[q] - r.Points[q - 1]).Length;
                    if (walked > 30 && total - walked > 160)
                        Check.True(half - Math.Max(Math.Abs(r.Points[q].X), Math.Abs(r.Points[q].Y)) >= RoadNetwork.EdgeCenter - 1, "road to the edge runs along the edge");
                }
            }
            Check.True(plan.Roads.Where(r => !r.ToEdge).All(r => r.To != null || r.EndRoad != null), "other roads end at an exit or a road");
            var text = NetworkPlanStore.Serialize(new NetworkPlanFile { Plan = plan, Spec = spec });
            var back = NetworkPlanStore.Deserialize(w, text, out string problem);
            Check.True(back != null, "plan not loaded: " + problem);
            Check.Equal(edge.Count, back.Plan.Roads.Count(r => r.ToEdge), "roads to the edge after loading");
            // building does not touch the town tiles
            string tiles = string.Join("|", w.Prefabs.Where(p => p.IsTile).Select(p => $"{p.Name}/{p.X}/{p.Y}/{p.Z}/{p.Rotation}"));
            // dead ends on the map (View → Dead ends): before building every free town exit of the plan, afterwards the unused ones
            var townExits = plan.Towns.Where(t => !t.Node).SelectMany(t => t.Exits).Where(e => !e.Street && e.Tile != null).ToList();
            int marked = DeadEnds.Find(w).Count;
            var ed = new WorldEdits(w);
            ed.Run("net", "net", e => NetworkBuilder.Build(w, e, plan, spec, new Progress<string>()));
            int markedAfter = DeadEnds.Find(w).Count;
            // crossings of town streets: no step on the surface around them
            foreach (var sp in streetCross)
            {
                double worst = 0;
                for (int z = (int)sp.Y - 20; z <= sp.Y + 20; z++)
                    for (int x = (int)sp.X - 20; x <= sp.X + 20; x++)
                        foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
                            if (w.RoadAt(x, z) != 0 && w.RoadAt(x + dx, z + dz) != 0) worst = Math.Max(worst, Math.Abs(w.HeightAt(x, z) - w.HeightAt(x + dx, z + dz)));
                Console.WriteLine($"       street crossing {sp.X:0}/{sp.Y:0}: largest step on the surface {worst:0.00} m");
                Check.True(worst < 0.6, $"step {worst:0.00} m at the street crossing {sp.X:0}/{sp.Y:0}");
            }
            Console.WriteLine($"       dead ends marked on the map: {marked} before building, {markedAfter} after (unused exits in the plan: {townExits.Count(e => !e.Used)})");
            Check.Equal(townExits.Count, marked, "dead ends marked before building = free town exits of the plan");
            Check.Equal(townExits.Count(e => !e.Used), markedAfter, "dead ends marked after building = unused exits of the plan");
            Check.Equal(tiles, string.Join("|", w.Prefabs.Where(p => p.IsTile && !plan.NodeTiles.Contains(p)).Select(p => $"{p.Name}/{p.X}/{p.Y}/{p.Z}/{p.Rotation}")), "town tiles after building");
            foreach (var r in edge)
            {
                var end = r.Points[^1];
                Check.True(w.RoadAt((int)Math.Floor(end.X), (int)Math.Floor(end.Y)) != 0, $"no surface at the edge end {end.X:0}/{end.Y:0}");
            }
            ed.Undo();
        }
        finally { RoadNetwork.TileDefs = null; RoadNetwork.EdgeRoadPass = true; }
    }

    [Test("Road network (real map): planning twice gives the same network, building can be undone exactly")]
    static void RoadNetworkRealMap()
    {
        // YAMEE_TEST_WORLD = project with towns and without country roads; otherwise towns generated on YAMEE_TEST_CITY_WORLD
        string folder = Environment.GetEnvironmentVariable("YAMEE_TEST_WORLD");
        WorldData w;
        if (!string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "dtm.raw")))
        {
            string copy = Path.Combine(Fixture.Temp, "real", Path.GetFileName(folder.TrimEnd('\\')));
            Directory.CreateDirectory(copy);
            foreach (var f in Directory.GetFiles(folder)) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
            w = WorldData.Load(copy, AppSettings.GuessGameFolder(), new Progress<string>());
        }
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("YAMEE_TEST_CITY_WORLD"))) w = TownWorld();
        else { Check.Skip("set YAMEE_TEST_WORLD to a project folder with towns (it is only read)"); return; }
        var spec = new NetworkSpec { Seed = "test" };
        string Plan() => string.Join("|", new RoadNetwork(w, spec).Plan(new Progress<string>()).Roads
            .Select(r => string.Join(" ", r.Points.Select(p => $"{p.X:0.000},{p.Y:0.000}"))));
        Check.Equal(Plan(), Plan(), "network");
        var plan = new RoadNetwork(w, spec).Plan(new Progress<string>());
        // towns without a usable exit are either joined to roads leading away (built network), got an exit on a town street, or are reported
        foreach (var town in plan.Towns.Where(q => !q.Exits.Any(e => e.Reachable)))
            Check.True(town.Linked || plan.Problems.Any(p => p.Contains(Math.Abs(Math.Round(town.CX)).ToString())), $"town at {town.CX:0}/{town.CZ:0} without exit is neither linked nor reported");
        Console.WriteLine($"       {plan.Towns.Count} towns, {plan.Towns.Count(q => q.Linked)} joined to existing roads, {plan.Towns.SelectMany(q => q.Exits).Count(e => e.Street)} exit(s) on town streets used");
        foreach (var tl in plan.Roads.Where(r => r.TownLink))
        {
            Console.WriteLine($"       town link {tl.Length:0} m, width {tl.Width:0} m (street {tl.From?.Width ?? 0} m) -> {tl.Junction?.X:0}/{tl.Junction?.Y:0}");
            if (tl.From != null) Check.Equal(tl.From.Width, tl.Width, "town link keeps street width");
        }
        var e = new WorldEdits(w);
        string before = Fixture.State(w);
        Check.True(NetworkPlanEdit.ShareOnMap(w, plan) < 0.1, $"plan not on the map before building ({NetworkPlanEdit.ShareOnMap(w, plan):0.00})");
        e.Run("net", "net", ed => NetworkBuilder.Build(w, ed, plan, spec, new Progress<string>()));
        string built = Fixture.State(w);
        Check.True(NetworkPlanEdit.ShareOnMap(w, plan) > 0.9, $"plan on the map after building ({NetworkPlanEdit.ShareOnMap(w, plan):0.00})");
        e.Undo();
        Check.Equal(before, Fixture.State(w), "after undo");
        Check.True(NetworkPlanEdit.ShareOnMap(w, plan) < 0.1, "plan no longer on the map after undo");
        // saved and loaded plan (tiles found again by name and position) builds exactly the same
        string text = NetworkPlanStore.Serialize(new NetworkPlanFile { Plan = plan, Spec = spec });
        var loaded = NetworkPlanStore.Deserialize(w, text, out string problem);
        Check.True(loaded != null, "plan not loaded: " + problem);
        Check.Equal(text, NetworkPlanStore.Serialize(loaded), "plan after loading");
        e.Run("net", "net", ed => NetworkBuilder.Build(w, ed, loaded.Plan, loaded.Spec, new Progress<string>()));
        Check.Equal(built, Fixture.State(w), "built from the loaded plan");
        e.Undo();

        // reroute the longest road through a point beside its middle: the ends stay, the road runs exactly through the point,
        // junctions of other roads on it stay on it, and the changed plan can be built and undone
        var editor = RoadNetwork.ForEditing(w, spec);
        var road = plan.Roads.Where(r => r.Length > 600).OrderByDescending(r => r.Length).First();
        var old = road.Points.ToList();
        var deps = plan.Roads.Where(r => r.StartRoad == road || r.EndRoad == road).Select(r => r.StartRoad == road ? r.Points[0] : r.Junction ?? r.Points[^1]).ToList();
        List<Point> line = null; Point via = default;
        foreach (double off in new[] { 120.0, -120, 200, -200, 60, -60 })
        {
            var mid = old[old.Count / 2]; var nx = old[old.Count / 2 + 1] - old[old.Count / 2 - 1]; nx.Normalize();
            via = mid + new Vector(-nx.Y, nx.X) * off;
            line = editor.Reroute(plan, road, new List<Point> { via }, out _);
            if (line != null) break;
        }
        Check.True(line != null, "no reroute found");
        Check.True(RoadNetwork.Project(line, via).Off < RoadNetwork.ViaRadius + 12, "road does not pass the via point");
        Check.True((line[0] - old[0]).Length < 0.01 && (line[^1] - old[^1]).Length < 0.01, "ends moved");
        foreach (var d in deps.Where(d => { var t = RoadNetwork.Project(old, d).Along; return t > 70 && t < RoadNetwork.Project(old, old[^1]).Along - 70; }))
            Check.True(RoadNetwork.Project(line, d).Off < 1, "junction of another road left behind");
        road.Points = line; road.Via = new List<Point> { via };
        editor.Remeasure(road);
        NetworkPlanEdit.UpdateCrossings(plan, road);
        NetworkPlanEdit.Refresh(plan);
        var back = NetworkPlanStore.Deserialize(w, NetworkPlanStore.Serialize(new NetworkPlanFile { Plan = plan, Spec = spec }), out _);
        Check.Equal(1, back.Plan.Roads[plan.Roads.IndexOf(road)].Via.Count, "via point saved");
        e.Run("net2", "net2", ed => NetworkBuilder.Build(w, ed, plan, spec, new Progress<string>()));
        e.Undo();
        Check.Equal(before, Fixture.State(w), "after undo of the rerouted network");

        // stage 3: a new road from a free town exit (or a road) to the middle of another road; it starts and ends exactly
        // there, does not cross itself, and the network with it can be built and undone
        var freeExit = plan.Towns.SelectMany(t => t.Exits).FirstOrDefault(x => !x.Used && x.Reachable);
        var mids = plan.Roads.Where(r => r.Length > 600).Select(r => (Road: r, P: r.Points[r.Points.Count / 2])).ToList();
        var from = freeExit != null ? new Point(freeExit.X, freeExit.Z) : mids[0].P;
        // the nearest targets in turn (one may lie where no way is possible – depends on the plan)
        var s = RoadNetwork.FindEnd(plan, from, 30);
        RoadNetwork.NetEnd t = null; NetRoad added = null; string why = null;
        foreach (var cand in mids.Where(m => (m.P - from).Length > 700).OrderBy(m => (m.P - from).Length).Take(5))
        {
            t = RoadNetwork.FindEnd(plan, cand.P, 30);
            Check.True(s != null && t != null, "ends not found");
            added = editor.NewRoad(plan, s, t, out why);
            if (added != null) break;
        }
        Check.True(added != null, "no new road: " + why);
        Check.True((added.Points[0] - s.P).Length < 0.01 && (added.Points[^1] - t.P).Length < 0.01, "new road does not start/end at the places");
        Check.Equal(0, SelfCrossings(added.Points), "self crossings of the new road");
        Check.True(added.EndRoad == t.Road && added.Manual, "junction");
        plan.Roads.Add(added);
        NetworkPlanEdit.UpdateCrossings(plan, added);
        NetworkPlanEdit.Refresh(plan);
        Console.WriteLine($"       new road {added.Length:0} m from {(s.Exit != null ? "town exit" : "road")}, {added.Crossings.Count} crossing(s)");
        e.Run("net3", "net3", ed => NetworkBuilder.Build(w, ed, plan, spec, new Progress<string>()));
        e.Undo();
        Check.Equal(before, Fixture.State(w), "after undo of the network with the new road");

        // short connection: a free town exit with a planned road 90–250 m in front gets a direct curve (no loop over the road)
        var shortCase = plan.Towns.SelectMany(q => q.Exits).Where(x => !x.Used && x.Reachable)
            .SelectMany(x => new[] { 100.0, 150, 200 }.Select(d => (Exit: x, Place: RoadNetwork.FindRoadPlace(plan, new Point(x.X + x.DX * d, x.Z + x.DZ * d), 40, new List<NetRoad>()))))
            .FirstOrDefault(c => c.Place != null && (c.Place.P - new Point(c.Exit.X, c.Exit.Z)).Length is double sd && sd > 90 && sd < 250);
        if (shortCase.Place != null)
        {
            var sx = shortCase.Exit;
            var link = editor.NewRoad(plan, new RoadNetwork.NetEnd(sx, null, new Point(sx.X, sx.Z), new Vector(sx.DX, sx.DZ)), shortCase.Place, out string swhy);
            if (link != null)
            {
                Check.True(link.Length < 300 && (link.Points[^1] - shortCase.Place.P).Length < 0.01, "short connection ends at the place");
                Check.Equal(0, SelfCrossings(link.Points), "self crossings of the short connection");
                Console.WriteLine($"       short connection {RoadNetwork.Project(link.Points, link.Points[^1]).Along:0} m from a town exit to a road");
            }
            else Console.WriteLine($"       (short connection not possible there: {swhy})");
        }
        else Console.WriteLine("       (no free town exit with a planned road close in front – short connection not tested)");

        // move a junction: the end of a branching road goes onto another road (the middle of the longest road that does not
        // depend on it); the road then ends exactly there and the order of the plan stays buildable
        var branch = plan.Roads.First(r => r.EndRoad != null && r.Length > 300);
        var not = NetworkPlanEdit.DependsOn(plan, branch); not.Add(branch); not.Add(branch.EndRoad);
        var jEnd = branch.Points[^1];
        var other = plan.Roads.Where(r => !not.Contains(r) && r.Length > 600).OrderBy(r => (r.Points[r.Points.Count / 2] - jEnd).Length).First();
        var place = RoadNetwork.FindRoadPlace(plan, other.Points[other.Points.Count / 2], 30, not);
        var moved = editor.MoveJunction(plan, branch, false, place, out why, out var mv);
        Check.True(moved != null, "junction not moved: " + why);
        Check.True((moved[^1] - place.P).Length < 0.01, "road does not end at the new junction");
        branch.Points = moved; branch.Via = mv; branch.EndRoad = place.Road; branch.Junction = place.P;
        editor.Remeasure(branch);
        NetworkPlanEdit.UpdateCrossings(plan, branch);
        Check.True(NetworkPlanEdit.Reorder(plan), "order");
        Check.True(plan.Roads.IndexOf(place.Road) < plan.Roads.IndexOf(branch), "branch before the road it ends at");
        NetworkPlanEdit.Refresh(plan);
        Console.WriteLine($"       junction moved {(place.P - jEnd).Length:0} m, road now {branch.Length:0} m");

        // move a crossing 120 m along the crossed road: the road crosses exactly there
        // a crossing at least 200 m from the ends of the crossing road (the first and last 60 m of a road stay fixed)
        var crosser = plan.Roads.FirstOrDefault(r => r.Crossings.Count > 0 && r.Length > 400
            && RoadNetwork.Project(r.Points, r.Crossings[0]).Along is double ca && ca > 200 && ca < RoadNetwork.Project(r.Points, r.Points[^1]).Along - 200);
        if (crosser == null) Console.WriteLine("       (no crossing away from the road ends – moving a crossing not tested)");
        if (crosser != null)
        {
            var crossed = crosser.CrossedRoads[0];
            double at = RoadNetwork.Project(crossed.Points, crosser.Crossings[0]).Along, cl = RoadNetwork.Project(crossed.Points, crossed.Points[^1]).Along;
            // several distances in turn (a place may lie where no way is possible – depends on the plan)
            Point q = default; List<Point> rr = null, cv = null;
            foreach (double off in new[] { 120.0, -120, 80, -80, 160, -160 })
            {
                double to = at + off;
                if (to < 80 || to > cl - 80) continue;
                q = RoadNetwork.At(crossed.Points, to).P;
                rr = editor.Reroute(plan, crosser, crosser.Via, out why, out cv, new[] { (q, crossed) });
                if (rr != null) break;
            }
            Check.True(rr != null, "crossing not moved: " + why);
            Check.True(RoadNetwork.Project(rr, q).Off < 2, "road does not cross at the new place");
            Console.WriteLine($"       crossing moved {(q - crosser.Crossings[0]).Length:0} m, {cv.Count} via point(s)");
            crosser.Points = rr; crosser.Via = cv; editor.Remeasure(crosser);
            NetworkPlanEdit.UpdateCrossings(plan, crosser);
            NetworkPlanEdit.Refresh(plan);
        }
        e.Run("net4", "net4", ed => NetworkBuilder.Build(w, ed, plan, spec, new Progress<string>()));
        e.Undo();
        Check.Equal(before, Fixture.State(w), "after undo of the network with moved junction/crossing");
    }

    [Test("Cancel: an edit step stops at its next change and is undone completely")]
    static void CancelUndoesStep()
    {
        var w = Fixture.LoadCopy();
        var before = (ushort[])w.Heights.Clone();
        var e = new WorldEdits(w);
        WorkCancel.Reset();
        bool stopped = false;
        try
        {
            e.Run("raise", "raise", ed =>
            {
                for (int i = 0; i < 100000; i++)
                {
                    if (i == 50000) WorkCancel.Request();
                    ed.SetHeight(i, (ushort)(w.Heights[i] + 256));
                }
            });
        }
        catch (OperationCanceledException) { stopped = true; }
        WorkCancel.Reset();
        Check.True(stopped, "the step must stop after the cancel request");
        Check.True(w.Heights.AsSpan().SequenceEqual(before), "all heights must be back as before");
        Check.Equal(0, e.Steps.Count, "no step may remain");
        // afterwards the next step works as usual
        var s = e.Run("raise", "raise", ed => ed.SetHeight(5, (ushort)(w.Heights[5] + 256)));
        Check.Equal(1, e.Steps.Count, "the next step must work");
    }

    [Test("Earthwork next to a lake: banks stay, the lake does not run into the excavation")]
    static void BanksStayDry()
    {
        // a building far below the lake level 30 m from the bank (like a trader platform): without the bank guard the transition
        // would lower the bank and SyncWater would flood the excavation (YAMEE-Land at -3432/1901)
        int Run(int guard, out int bankChanged, out int waterAdded)   // 0 = none, 1 = bank guard of Flatten, 2 = WorldEdits.GuardBanks
        {
            var w = Fixture.LoadCopy();
            int size = w.Size, half = w.Half;
            // easternmost water cell of the first lake in the middle of the map: from there 30 m east
            int found = -1;
            for (int z = half - 400; z < half + 400 && found < 0; z += 8)
                for (int x = 100; x < size - 200 && found < 0; x++)
                    if (w.WaterMask[z * size + x] != 0 && w.WaterMask[z * size + x + 1] == 0 && w.WaterMask[z * size + x + 40] == 0) found = z * size + x;
            Check.True(found >= 0, "no lake in the test world");
            int wx = found % size - half, wz = found / size - half;
            int level = w.WaterMask[found];
            var p = new PlacedPrefab { Name = "test_building", X = wx + 30, Z = wz - 20, Y = 40, Def = new PrefabDef { SizeX = 40, SizeZ = 40 } };
            var before = (ushort[])w.Heights.Clone();
            var e = new WorldEdits(w);
            var step = e.Run("flatten", "flatten", ed => { if (guard == 2) ed.GuardBanks(); GroundShaper.Flatten(w, ed, p, level - 14, 0, null, false, guard == 1); });
            bankChanged = 0;
            for (int z = wz - 60; z <= wz + 60; z++)
                for (int x = wx - 6; x <= wx + 6; x++)
                    if (x >= 0 && z >= 0 && x < size - half && z < size - half && w.Heights[w.Index(x, z)] != before[w.Index(x, z)] && w.WaterMask[w.Index(x, z)] == 0) bankChanged++;
            waterAdded = step.WaterAdded;
            return bankChanged;
        }
        Run(0, out int changedWithout, out int waterWithout);
        Run(1, out int changedWith, out int waterWith);
        Run(2, out int changedGuard, out int waterGuard);
        Console.WriteLine($"       without guard: {changedWithout} bank cells lowered, {waterWithout} water cells added; with guard: {changedWith} / {waterWith}");
        Console.WriteLine($"       with GuardBanks: {changedGuard} / {waterGuard}");
        Check.Equal(0, waterWith, "water cells added with the bank guard");
        Check.Equal(0, waterGuard, "water cells added with GuardBanks");
        Check.Equal(0, changedGuard, "bank cells changed with GuardBanks");
        Check.Equal(0, changedWith, "bank cells changed with the bank guard");
    }

    [Test("Steps beside paths: natural cliffs stay, only new steps are evened out, the verge stays level")]
    static void SmoothStepsKeepsCliffs()
    {
        // Degaku-Land 2026-11-14: a wilderness path along a cliff edge – SmoothSteps filled the cliff as one even ramp
        // right up to the gravel (verge gone)
        int Run(bool cliffExisted, out bool vergeKept)
        {
            var w = Fixture.LoadCopy();
            int cx = 0, cz = 0;
            bool Dry(int x0, int z0) { for (int z = z0 - 60; z <= z0 + 60; z += 4) for (int x = x0 - 60; x <= x0 + 60; x += 4) if (w.WaterAt(x, z)) return false; return true; }
            for (int t = 0; t < 400 && !Dry(cx, cz); t++) { cx = (t % 20 - 10) * 40; cz = (t / 20 - 10) * 40; }
            Check.True(Dry(cx, cz), "no dry place in the test world");
            // west 40 m, cliff 4 m per m, then 60 m; gravel from cx + 6 to cx + 10, verge at cx + 5
            for (int z = cz - 60; z <= cz + 60; z++)
                for (int x = cx - 60; x <= cx + 60; x++)
                {
                    double h = x < cx ? 40 : x >= cx + 5 ? 60 : 40 + 4 * (x - cx + 1);
                    w.Heights[w.Index(x, z)] = (ushort)(h * 256);
                }
            var old = (ushort[])w.Heights.Clone();
            var e = new WorldEdits(w);
            int changed = 0;
            e.Run("path", "path", ed =>
            {
                for (int z = cz - 60; z <= cz + 60; z++)
                    for (int x = cx + 6; x <= cx + 10; x++) ed.SetRoad(w.Index(x, z), WorldData.Gravel);
                Func<int, int, double> before = cliffExisted ? (x, z) => old[w.Index(x, z)] / 256.0 : (x, z) => x < cx - 40 ? 40 : 60;
                changed = CityGenerator.SmoothSteps(w, ed, cx, cz - 20, cx + 10, cz + 20, 30, before, 1);
            });
            vergeKept = true;
            for (int z = cz - 20; z <= cz + 20; z++) if (w.Heights[w.Index(cx + 5, z)] != old[w.Index(cx + 5, z)]) vergeKept = false;
            return changed;
        }
        int natural = Run(true, out bool verge1), made = Run(false, out bool verge2);
        Console.WriteLine($"       natural cliff: {natural} cells changed; cliff made by the path: {made}");
        Check.Equal(0, natural, "cells changed at a natural cliff");
        Check.True(made > 0, "a step made by the path is evened out");
        Check.True(verge1 && verge2, "verge stays level");
    }

    [Test("Wilderness path joining a road obliquely: the road keeps its heights, no ledge beside its edge")]
    static void WildPathJoinsCleanly()
    {
        // Degaku-Land 2026-11-14 (1040/1983): an oblique gravel path wrote its height into the asphalt (holes up to 1 m) and left
        // a ledge beside the asphalt edge
        var w = Fixture.LoadCopy();
        int cx = 0, cz = 0;
        bool Dry(int x0, int z0) { for (int z = z0 - 120; z <= z0 + 120; z += 4) for (int x = x0 - 120; x <= x0 + 120; x += 4) if (w.WaterAt(x, z)) return false; return true; }
        for (int t = 0; t < 400 && !Dry(cx, cz); t++) { cx = (t % 20 - 10) * 40; cz = (t / 20 - 10) * 40; }
        Check.True(Dry(cx, cz), "no dry place in the test world");
        // terrain falls 8 % towards the west and south-east of the road; asphalt road 16 m (+1 m shoulder) diagonal to the north-east
        for (int z = cz - 120; z <= cz + 120; z++)
            for (int x = cx - 120; x <= cx + 120; x++)
                w.Heights[w.Index(x, z)] = (ushort)((50 + 0.08 * (x - cx) * 0.5 - 0.03 * (z - cz)) * 256);
        var e = new WorldEdits(w);
        e.Run("road", "road", ed =>
        {
            for (int z = cz - 110; z <= cz + 110; z++)
                for (int x = cx - 110; x <= cx + 110; x++)
                    if (Math.Abs((x + 0.5 - cx) - (z + 0.5 - cz)) / Math.Sqrt(2) <= 9) ed.SetRoad(w.Index(x, z), WorldData.Asphalt);
        });
        var old = (ushort[])w.Heights.Clone();
        var mask = (byte[])w.RoadMask.Clone();
        // gravel path from the west, joining at 45° and ending at the road center
        var pts = new List<Point>();
        for (int x = cx - 90; x <= cx; x += 10) pts.Add(new Point(x + 0.5, cz + 0.5));
        var path = new WildPath { Points = pts, Width = 4, EndKind = WorldData.Asphalt, EndDir = new Vector(Math.Sqrt(0.5), Math.Sqrt(0.5)), EndHalf = 9, Poi = new WildPoi { Building = new PlacedPrefab { Name = "test" } } };
        path.Poi.Path = path;
        var plan = new WildernessPlan();
        plan.Paths.Add(path);
        WildernessBuildResult res = null;
        e.Run("path", "path", ed => res = WildernessGenerator.Build(w, ed, plan, new WildernessSpec(), null));
        Check.Equal(1, res.Paths, "paths built");
        int roadChanged = 0;
        double ledge = 0, ledgeBefore = 0;
        for (int z = cz - 30; z <= cz + 30; z++)
            for (int x = cx - 30; x <= cx + 30; x++)
            {
                int i = w.Index(x, z);
                if (mask[i] != 0 && w.Heights[i] != old[i]) roadChanged++;
                foreach (int j in new[] { w.Index(x + 1, z), w.Index(x, z + 1) })
                {
                    ledge = Math.Max(ledge, Math.Abs(w.Heights[i] - w.Heights[j]) / 256.0);
                    ledgeBefore = Math.Max(ledgeBefore, Math.Abs(old[i] - old[j]) / 256.0);
                }
            }
        Console.WriteLine($"       road cells changed: {roadChanged}; largest step between neighbors ±30 m: before {ledgeBefore:0.00} m, after {ledge:0.00} m");
        Check.Equal(0, roadChanged, "road cells with changed height");
        Check.True(ledge <= 0.5, $"step beside the junction {ledge:0.00} m");
    }

    [Test("Wilderness path joining a road on an embankment: level landing, then no steeper than allowed (no drop behind the junction)")]
    static void WildPathLeavesDamGently()
    {
        // Buzovi-Land 2026-11-26 (1690/1154): road 2.2 m above the terrain, the path took over the 25–30 % embankment of the road
        // and dropped 1.3 m in 4 m right behind the junction
        var w = Fixture.LoadCopy();
        int cx = 0, cz = 0;
        bool Dry(int x0, int z0) { for (int z = z0 - 120; z <= z0 + 120; z += 4) for (int x = x0 - 120; x <= x0 + 120; x += 4) if (w.WaterAt(x, z)) return false; return true; }
        for (int t = 0; t < 400 && !Dry(cx, cz); t++) { cx = (t % 20 - 10) * 40; cz = (t / 20 - 10) * 40; }
        Check.True(Dry(cx, cz), "no dry place in the test world");
        // flat terrain 46 m, asphalt road north–south 16 m + 1 m shoulder (half 9 m) at 48.2 m, embankment 28 % down to the terrain
        double ground = 46, road = 48.2, half = 9;
        for (int z = cz - 120; z <= cz + 120; z++)
            for (int x = cx - 120; x <= cx + 120; x++)
            {
                double d = Math.Abs(x + 0.5 - cx);
                double h = d <= half ? road : Math.Max(ground, road - 0.28 * (d - half));
                w.Heights[w.Index(x, z)] = (ushort)(h * 256);
            }
        var e = new WorldEdits(w);
        e.Run("road", "road", ed =>
        {
            for (int z = cz - 110; z <= cz + 110; z++)
                for (int x = cx - 110; x <= cx + 110; x++)
                    if (Math.Abs(x + 0.5 - cx) <= half) ed.SetRoad(w.Index(x, z), WorldData.Asphalt);
        });
        // gravel path from the east, ending at the road center
        var pts = new List<Point>();
        for (int x = cx + 90; x >= cx; x -= 10) pts.Add(new Point(x + 0.5, cz + 0.5));
        // the half width given to the path reaches 4 m into the embankment (measured widths are not exact; before, everything up to it
        // counted as road and the path copied the embankment)
        var path = new WildPath { Points = pts, Width = 4, EndKind = WorldData.Asphalt, EndDir = new Vector(0, 1), EndHalf = half + 4, Poi = new WildPoi { Building = new PlacedPrefab { Name = "test" } } };
        path.Poi.Path = path;
        var plan = new WildernessPlan();
        plan.Paths.Add(path);
        var spec = new WildernessSpec();
        WildernessBuildResult res = null;
        e.Run("path", "path", ed => res = WildernessGenerator.Build(w, ed, plan, spec, null));
        Check.Equal(1, res.Paths, "paths built");
        double H(int x) => w.HeightAt(x, cz);
        double steepest = 0; int at = 0;
        for (int x = cx + (int)half; x < cx + 70; x++)
        {
            double g = Math.Abs(H(x + 1) - H(x));
            if (g > steepest) { steepest = g; at = x - cx; }
        }
        double landing = H(cx + (int)half + 2);
        Console.WriteLine($"       height 2 m behind the road edge {landing:0.00} m (road {road:0.00}); steepest metre behind the junction {steepest * 100:0.0} % at {at} m from the road center");
        Check.True(Math.Abs(landing - road) <= 0.15, $"landing behind the road edge {landing:0.00} m instead of about {road:0.00} m");
        Check.True(steepest <= spec.Grade, $"drop behind the junction {steepest * 100:0.0} % (allowed {spec.Grade * 100:0} %)");
    }

    /// dry test place with a straight asphalt road north–south (16 m + shoulder) at a fixed height; terrain = f(x, z)
    static WorldData RoadOnHill(out int cx, out int cz, Func<double, double, double> terrain, Func<double, double> roadHeight, double half = 9)
    {
        var w = Fixture.LoadCopy();
        int x0 = 0, z0 = 0;
        bool Dry(int a, int b) { for (int z = b - 130; z <= b + 130; z += 4) for (int x = a - 130; x <= a + 130; x += 4) if (w.WaterAt(x, z)) return false; return true; }
        for (int t = 0; t < 400 && !Dry(x0, z0); t++) { x0 = (t % 20 - 10) * 40; z0 = (t / 20 - 10) * 40; }
        Check.True(Dry(x0, z0), "no dry place in the test world");
        cx = x0; cz = z0;
        for (int z = z0 - 130; z <= z0 + 130; z++)
            for (int x = x0 - 130; x <= x0 + 130; x++)
            {
                double dx = x + 0.5 - x0, dz = z + 0.5 - z0;
                double h = Math.Abs(dx) <= half ? roadHeight(dz) : terrain(dx, dz);
                w.Heights[w.Index(x, z)] = (ushort)Math.Round(h * 256);
                w.RoadMask[w.Index(x, z)] = Math.Abs(dx) <= half && Math.Abs(dz) <= 120 ? WorldData.Asphalt : (byte)0;
            }
        return w;
    }

    [Test("T-junction: a road ending on another road leaves the terrain on that road's far side unchanged")]
    static void JunctionLeavesFarSide()
    {
        // Buzovi-Land 2026-11-26 (1170/2250): the round slope cap of the new road's end reached across the existing road and cut the hill
        // behind it down to the junction height while the road kept its height – a ditch/wall of 1–1.5 m along its far edge
        double half = 9, roadH = 50;
        var w = RoadOnHill(out int cx, out int cz, (dx, dz) => dx < 0 ? roadH + 0.25 * (-dx - half) : roadH - 0.15 * (dx - half), _ => roadH, half);
        var before = (ushort[])w.Heights.Clone();
        var e = new WorldEdits(w);
        var end = new Point(cx + 0.5, cz + 0.5);
        var pts = new List<Point> { new(cx + 100.5, cz + 0.5), new(cx + 50.5, cz + 0.5), end };
        var heights = new double?[] { null, null, roadH };
        var r = RoadPlanner.Compute(w, pts, RoadSettings.Asphalt(), heights);
        Check.True(r != null, "road computes");
        r.ProtectZones = new() { (end, half + 1) };
        e.Run("t", "t", ed => RoadApplier.Apply(w, ed, r, new Progress<string>()));
        int farChanged = 0, nearChanged = 0;
        for (int z = cz - 40; z <= cz + 40; z++)
            for (int x = cx - 60; x <= cx + 60; x++)
            {
                int i = w.Index(x, z);
                if (w.Heights[i] == before[i]) continue;
                if (x + 0.5 - cx < -half) farChanged++; else if (x + 0.5 - cx > half) nearChanged++;
            }
        Console.WriteLine($"       cells changed beyond the far edge of the road {farChanged}, on the side of the new road {nearChanged}");
        Check.Equal(0, farChanged, "cells changed behind the junction");
        Check.True(nearChanged > 500, "the new road was built on its own side");
        // a free end (no intersection zone) still runs out round into the terrain
        var w2 = RoadOnHill(out cx, out cz, (dx, dz) => roadH + 0.1 * dx, _ => roadH, half);
        Array.Clear(w2.RoadMask);
        var before2 = (ushort[])w2.Heights.Clone();
        var r2 = RoadPlanner.Compute(w2, new List<Point> { new(cx + 100.5, cz + 0.5), new(cx + 0.5, cz + 0.5) }, RoadSettings.Asphalt());
        new WorldEdits(w2).Run("f", "f", ed => RoadApplier.Apply(w2, ed, r2, new Progress<string>()));
        int behind = 0;
        for (int z = cz - 10; z <= cz + 10; z++) for (int x = cx - 8; x < cx - 1; x++) if (w2.Heights[w2.Index(x, z)] != before2[w2.Index(x, z)]) behind++;
        Check.True(behind > 0, "a free end keeps its round run-out");
    }

    [Test("Junctions: up to 12 % the existing road stays and the new road tilts with it; steeper roads are flattened to 12 % only")]
    static void JunctionAutoCrossSlope()
    {
        foreach (double grade in new[] { 0.09, 0.20 })
        {
            double half = 9;
            var w = RoadOnHill(out int cx, out int cz, (dx, dz) => 50 + grade * dz, dz => 50 + grade * dz, half);
            var end = new Point(cx + 0.5, cz + 0.5);
            var s = RoadSettings.Asphalt();
            double allowed = Math.Max(Math.Max(s.JunctionTolerance, s.MaxCrossSlope), Junctions.AutoCrossMax);
            var j = Junctions.Find(w, end, false, s.Width / 2 + s.Shoulder, allowed, k => k == Surface.Asphalt ? RoadSettings.Asphalt() : RoadSettings.Gravel(), new Vector(1, 0));
            Check.True(j != null, $"junction found ({grade:P0})");
            Console.WriteLine($"       road {grade * 100:0} %: measured {j.ExistingGrade * 100:0.0} %, at the junction {j.PlateauGrade * 100:0.0} %, rebuilt {(j.RebuildResult != null ? "yes" : "no")}");
            if (grade < Junctions.AutoCrossMax)
            {
                Check.True(j.RebuildResult == null, "a 9 % road is not rebuilt");
                Check.True(Math.Abs(Math.Abs(j.PlateauGrade) - grade) < 0.01, "the new road tilts with the full 9 %");
            }
            else
            {
                Check.True(j.RebuildResult != null, "a 20 % road is rebuilt at the junction");
                Check.True(Math.Abs(Math.Abs(j.PlateauGrade) - Junctions.AutoCrossMax) < 0.001, "flattened to 12 %, not further");
            }
        }
    }

    [Test("Oblique junction: existing road stays untouched in the acute corner and the terrain beside it slopes smoothly without a cliff")]
    static void JunctionObliqueCornerSmooth()
    {
        // Wimovo-Land 2026-11-27 (782 W 1703 N): short 56 m town link climbing ~11 % away from a 16 m highway in a cut at ~65–70°.
        // Before: (1) outside the 10 m ProtectZones circle the new road overwrote the highway's asphalt by 0.8 m, and (2) 1 m outside
        // the protected gravel edge it pulled the terrain up by 1.2 m (a jagged cliff along the road edge).
        foreach (double linkWidth in new[] { 16.0, 9.0 })
        {
            double half = 9, roadH = 50;
            // highway in a 3.5 m cut with 1 m level grass verge and gentle 25 % cut slope
            var w = RoadOnHill(out int cx, out int cz, (dx, dz) =>
            {
                double off = Math.Max(0, Math.Abs(dx) - half - 1);
                return roadH + Math.Min(3.5, 0.25 * off);
            }, _ => roadH, half);
            var beforeH = (ushort[])w.Heights.Clone();
            var beforeMask = (byte[])w.RoadMask.Clone();
            // build the existing highway as a NetRoad first (only its RoadResult is needed for EndRoad; heights already in w)
            var spec = new NetworkSpec { Grade = 0.14, MaxGrade = 0.22, ShortMaxGrade = 0.25, ShortSteepLength = 150, RoadFill = 2.2, RoadCut = 4.5, RoadEdgeFill = 2 };
            // oblique 56 m road arriving at ~65° from (cx + 51, cz - 24) to (cx, cz), climbing ~6 m over 56 m
            double ang = 65 * Math.PI / 180;
            double len = 56;
            double ax = cx + 0.5 + Math.Sin(ang) * len, az = cz + 0.5 - Math.Cos(ang) * len;
            // raise terrain near the far end so the link climbs ~6 m (like a town street on higher ground)
            for (int z = cz - 80; z <= cz + 40; z++)
                for (int x = cx + (int)half + 2; x <= cx + 100; x++)
                {
                    double t = Math.Clamp((x + 0.5 - (cx + half + 2)) / 45.0, 0, 1);
                    int i = w.Index(x, z);
                    double h = Math.Max(w.Heights[i] / 256.0, roadH + 6.0 * t);
                    w.Heights[i] = (ushort)Math.Round(h * 256);
                }
            beforeH = (ushort[])w.Heights.Clone();

            var mainPts = new List<Point> { new(cx + 0.5, cz - 80.5), new(cx + 0.5, cz + 0.5), new(cx + 0.5, cz + 80.5) };
            var mainRoad = new NetRoad { Points = mainPts, Width = 16, Length = 160 };
            var mainRes = RoadPlanner.Compute(w, mainPts, new RoadSettings { Surface = Surface.Asphalt, Width = 16, Shoulder = 1, GrassEdge = 1, MaxGrade = 0.14, Smooth = 100, Blend = 12, CutSlope = 1.5 });
            Check.True(mainRes != null, "main road computes");
            for (int k = 0; k < mainRes.Profile.Length; k++) mainRes.Profile[k] = roadH;

            var linkPts = new List<Point>();
            for (int q = 0; q <= 5; q++) linkPts.Add(new Point(ax + (cx + 0.5 - ax) * q / 5.0, az + (cz + 0.5 - az) * q / 5.0));
            var linkRoad = new NetRoad
            {
                Points = linkPts, Width = linkWidth, Length = len, TownLink = true, EndRoad = mainRoad, Junction = new Point(cx + 0.5, cz + 0.5),
                From = new NetExit { X = ax, Z = az, Street = true, Width = (int)linkWidth, StreetHalf = linkWidth / 2 + 1, StreetDX = 0, StreetDZ = 1 },
            };
            var plan = new NetworkPlan();
            plan.Roads.Add(mainRoad);
            plan.Roads.Add(linkRoad);

            var rr = NetworkBuilder.PlanRoad(w, plan, linkRoad, spec, new Dictionary<NetRoad, RoadResult> { [mainRoad] = mainRes }, new NetworkBuildResult());
            Check.True(rr != null, $"oblique link ({linkWidth} m) computes");
            Check.True(rr.WidthAt(rr.Path.Count - 1) <= linkWidth + 0.01, $"link does not flare beyond {linkWidth} m");
            var e = new WorldEdits(w);
            e.Run("link", "link", ed =>
            {
                var junc = RoadApplier.CaptureJunctions(w, rr);
                RoadApplier.Apply(w, ed, rr, new Progress<string>(), 0, -1, 4, junc);
            });

            int roadChanged = 0, farChanged = 0;
            double maxJump = 0, maxCornerJump = 0;
            for (int z = cz - 45; z <= cz + 35; z++)
                for (int x = cx - 40; x <= cx + 45; x++)
                {
                    int i = w.Index(x, z);
                    if (beforeMask[i] != 0 && w.Heights[i] != beforeH[i]) roadChanged++;
                    if (x + 0.5 - cx < -half && w.Heights[i] != beforeH[i]) farChanged++;
                    if (x + 0.5 - cx < half - 1 || x + 0.5 - cx > half + 18) continue;
                    double h = w.Heights[i] / 256.0;
                    double gx = Math.Abs(w.Heights[w.Index(x + 1, z)] / 256.0 - h);
                    double gz = Math.Abs(w.Heights[w.Index(x, z + 1)] / 256.0 - h);
                    double g = Math.Max(gx, gz);
                    if (g > maxJump) maxJump = g;
                    if (w.Heights[i] != beforeH[i] && g > maxCornerJump) maxCornerJump = g;
                }
            Console.WriteLine($"       width {linkWidth:0} m: existing road cells changed {roadChanged}, far side changed {farChanged}, max 1 m jump near junction {maxJump:0.00} m (changed cells {maxCornerJump:0.00} m)");
            Check.Equal(0, roadChanged, $"existing highway untouched ({linkWidth} m)");
            Check.Equal(0, farChanged, $"far side untouched ({linkWidth} m)");
            Check.True(maxJump <= 0.35, $"smooth corner without cliff ({linkWidth} m): got {maxJump:0.00} m");
        }
    }

    [Test("Wilderness path at a steep road: no washboard of small humps in the mouth")]
    static void WildPathMouthSmoothAtSteepRoad()
    {
        // Testit-Land 2026-11-26 (215/1483): town road climbing 14 %, gravel path leaving it nearly at a right angle – one-block humps
        // across the mouth (the softening used a chamfer distance and the single nearest road point)
        var w = Fixture.LoadCopy();
        int cx = 0, cz = 0;
        bool Dry(int x0, int z0) { for (int z = z0 - 130; z <= z0 + 130; z += 4) for (int x = x0 - 130; x <= x0 + 130; x += 4) if (w.WaterAt(x, z)) return false; return true; }
        for (int t = 0; t < 400 && !Dry(cx, cz); t++) { cx = (t % 20 - 10) * 60; cz = (t / 20 - 10) * 60; }
        Check.True(Dry(cx, cz), "no dry place in the test world");
        var rd = new Vector(Math.Cos(25 * Math.PI / 180), Math.Sin(25 * Math.PI / 180));
        var rn = new Vector(-rd.Y, rd.X);
        double half = 5.5;
        var e = new WorldEdits(w);
        e.Run("road", "road", ed =>
        {
            for (int z = cz - 130; z <= cz + 130; z++)
                for (int x = cx - 130; x <= cx + 130; x++)
                {
                    double a = (x + 0.5 - cx) * rd.X + (z + 0.5 - cz) * rd.Y, s = (x + 0.5 - cx) * rn.X + (z + 0.5 - cz) * rn.Y;
                    double road = 80 + 0.14 * a, h = Math.Abs(s) <= half ? road : road - 0.06 * (Math.Abs(s) - half);
                    ed.SetHeight(w.Index(x, z), (ushort)(h * 256));
                    if (Math.Abs(s) <= half) ed.SetRoad(w.Index(x, z), Math.Abs(s) <= half - 1 ? WorldData.Asphalt : WorldData.Gravel);
                }
        });
        var pts = new List<Point>();
        for (int d = 90; d >= 0; d -= 10) pts.Add(new Point(cx + 0.5 + rn.X * d, cz + 0.5 + rn.Y * d));
        var path = new WildPath { Points = pts, Width = 4, EndKind = WorldData.Asphalt, EndDir = rd, EndHalf = half, Poi = new WildPoi { Building = new PlacedPrefab { Name = "test" } } };
        path.Poi.Path = path;
        var plan = new WildernessPlan();
        plan.Paths.Add(path);
        WildernessBuildResult res = null;
        e.Run("path", "path", ed => res = WildernessGenerator.Build(w, ed, plan, new WildernessSpec(), null));
        Check.Equal(1, res.Paths, "paths built");
        int humps = 0; double worst = 0;
        for (int z = cz - 14; z <= cz + 14; z++)
            for (int x = cx - 14; x <= cx + 14; x++)
            {
                if (w.RoadAt(x, z) == WorldData.Asphalt) continue;
                double h = w.HeightAt(x, z), m = (w.HeightAt(x + 1, z) + w.HeightAt(x - 1, z) + w.HeightAt(x, z + 1) + w.HeightAt(x, z - 1)) / 4;
                worst = Math.Max(worst, Math.Abs(h - m));
                if (Math.Abs(h - m) > 0.12) humps++;
            }
        Console.WriteLine($"       humps over 0.12 m in the mouth: {humps}, largest {worst:0.00} m");
        Check.True(humps <= 2 && worst <= 0.2, $"{humps} humps, largest {worst:0.00} m");
    }

    [Test("Road profile: crests and dips are rounded calmly (no sharp change of gradient)")]
    static void CalmCrests()
    {
        // Degaku-Land 2026-11-14 (813/1833): +12 % straight into −13 % within 10 m – the crest looked like a hump in the game
        double Worst(double slope, bool dip, out double maxGrade, out double top)
        {
            var w = Fixture.LoadCopy();
            int cx = 0, cz = 0;
            bool Dry(int x0, int z0) { for (int z = z0 - 30; z <= z0 + 30; z += 4) for (int x = x0 - 200; x <= x0 + 200; x += 4) if (w.WaterAt(x, z)) return false; return true; }
            for (int t = 0; t < 400 && !Dry(cx, cz); t++) { cx = (t % 20 - 10) * 40; cz = (t / 20 - 10) * 40; }
            Check.True(Dry(cx, cz), "no dry place in the test world");
            for (int z = cz - 60; z <= cz + 60; z++)
                for (int x = cx - 220; x <= cx + 220; x++)
                {
                    double h = 60 + (dip ? 1 : -1) * slope * Math.Abs(x - cx);
                    w.Heights[w.Index(x, z)] = (ushort)(h * 256);
                }
            var pts = new List<Point>();
            for (int x = cx - 160; x <= cx + 160; x += 20) pts.Add(new Point(x, cz + 0.5));
            var s = RoadSettings.Asphalt();
            s.Width = 16; s.Shoulder = 1; s.GrassEdge = 1; s.Smooth = 100; s.Blend = 12; s.CutSlope = 1.5; s.MaxGrade = 0.12;
            s.TubeFill = 0.6; s.TubeCut = 1.8; s.TubeEdgeFill = 2.0;
            var r = RoadPlanner.Compute(w, pts, s);
            int mid = r.Path.FindIndex(p => p.X >= cx + 0.5);
            top = r.Profile[mid] - 60;   // height of the road above (dip) / below (crest) the top of the terrain
            // largest change of gradient between neighboring 4 m pieces, measured over 8 m apart (like driving over it)
            var at = new List<double>();
            for (double d = 0; d <= r.Length; d += 4) { int k = r.Path.FindIndex(p => p.Dist >= d); at.Add(r.Profile[k < 0 ? r.Path.Count - 1 : k]); }
            double worst = 0; maxGrade = 0;
            for (int i = 2; i < at.Count; i++)
            {
                double g1 = (at[i - 1] - at[i - 2]) / 4, g2 = (at[i] - at[i - 1]) / 4;
                worst = Math.Max(worst, Math.Abs(g2 - g1));
                maxGrade = Math.Max(maxGrade, Math.Abs(g2));
            }
            return worst;
        }
        // before (2026-11-14, rounding ±3 m once): crest of 20 % terrain 14.5 % change of gradient per 4 m
        var results = new List<(string, double, double)>();
        foreach (var (slope, dip) in new[] { (0.2, false), (0.2, true), (0.12, false), (0.08, true) })
        {
            double worst = Worst(slope, dip, out double maxG, out double top);
            string what = $"{(dip ? "dip" : "crest")} {slope * 100:0} %";
            Console.WriteLine($"       {what}: largest change of gradient per 4 m {worst * 100:0.0} %, steepest {maxG * 100:0.0} %, road at the top {top:+0.00;-0.00} m");
            results.Add((what, worst, maxG));
        }
        foreach (var (what, worst, maxG) in results)
        {
            Check.True(worst <= 0.03, $"change of gradient per 4 m at {what}: {worst * 100:0.0} %");
            Check.True(maxG <= 0.13, $"steepest piece at {what}: {maxG * 100:0.0} %");
        }
    }

    [Test("Road network: tight curves get a lower highest gradient, straight stretches the full one")]
    static void CurveGradeFactors()
    {
        // 200 m straight, a half circle with 30 m radius (hairpin), 200 m straight
        var pts = new List<Point>();
        for (int k = 0; k <= 10; k++) pts.Add(new(0, k * 20));
        for (int k = 1; k <= 12; k++) { double a = Math.PI * k / 12; pts.Add(new(30 - 30 * Math.Cos(a), 200 + 30 * Math.Sin(a))); }
        for (int k = 1; k <= 10; k++) pts.Add(new(60, 200 - k * 20));
        var path = RoadPlanner.BuildPath(pts);
        var f = RoadPlanner.CurveFactors(path);
        double straight = Enumerable.Range(0, path.Count).Where(i => path[i].Dist < 120).Min(i => f[i]);
        double hairpin = Enumerable.Range(0, path.Count).Where(i => path[i].Z > 210).Max(i => f[i]);
        Console.WriteLine($"       factor on the straight {straight:0.00}, in the hairpin at most {hairpin:0.00}");
        Check.True(straight > 0.99, $"straight stretch reduced to {straight:0.00}");
        Check.True(hairpin < 0.7, $"hairpin only reduced to {hairpin:0.00}");
    }

    [Test("Road with bridge: follows hills and valleys away from the bridge, ramps smoothly out of a deep valley")]
    static void BridgeRoadFollowsTerrain()
    {
        var w = Fixture.LoadCopy();
        int x0 = -500, z0 = 0;
        // 1) Bridge at d = 100..140 (h = 60), then a +15 m hill at d = 450 and a -10 m valley at d = 750, ending at h = 60
        for (int z = z0 - 60; z <= z0 + 60; z++)
            for (int x = x0 - 40; x <= x0 + 940; x++)
            {
                double d = x - x0;
                double hill = Math.Abs(d - 450) < 200 ? 15 * Math.Pow(Math.Cos((d - 450) / 200 * Math.PI / 2), 2) : 0;
                double valley = Math.Abs(d - 750) < 150 ? -10 * Math.Pow(Math.Cos((d - 750) / 150 * Math.PI / 2), 2) : 0;
                w.Heights[w.Index(x, z)] = (ushort)((60 + hill + valley) * 256);
            }
        var pts = new List<Point>();
        for (int d = 0; d <= 900; d += 20) pts.Add(new Point(x0 + d, z0 + 0.5));
        var s = RoadSettings.Asphalt();
        s.Width = 16; s.Shoulder = 1; s.GrassEdge = 1; s.Smooth = 100; s.Blend = 12; s.CutSlope = 1.5; s.MaxGrade = 0.12;
        s.TubeFill = 1.2; s.TubeCut = 1.8; s.TubeEdgeFill = 2.0;
        var fixes = new List<(double, double)>();
        for (double x = 100; x <= 140; x += 1) fixes.Add((x, 60));
        var levels = new List<(double, double, double)> { (100, 140, 60) };
        var r = RoadPlanner.Compute(w, pts, s, fixedAt: fixes, levels: levels);
        double hHill = r.Profile[r.IndexAt(450)], hValley = r.Profile[r.IndexAt(750)];
        Console.WriteLine($"       bridge road: hill {hHill:0.0} m (terrain 75), valley {hValley:0.0} m (terrain 50), cut {r.MaxCut:0.0} m, fill {r.MaxFill:0.0} m");
        Check.True(hHill > 71, $"road ignored hill after bridge: {hHill:0.0} m instead of ~75 m");
        Check.True(hValley < 53, $"road ignored valley after bridge: {hValley:0.0} m instead of ~50 m");
        Check.True(r.MaxCut <= 3.0 && r.MaxFill <= 2.5, $"excessive earthwork on gentle terrain: cut {r.MaxCut:0.0} m, fill {r.MaxFill:0.0} m");

        // 2) Bridge in a 10 m deep valley (bridge h = 50 at d = 200..240, banks at h = 60): climbs smoothly without a jump
        for (int z = z0 - 60; z <= z0 + 60; z++)
            for (int x = x0 - 40; x <= x0 + 540; x++)
            {
                double d = x - x0;
                double h = d >= 195 && d <= 245 ? 50 : 60;
                w.Heights[w.Index(x, z)] = (ushort)(h * 256);
            }
        var pts2 = new List<Point>();
        for (int d = 0; d <= 500; d += 20) pts2.Add(new Point(x0 + d, z0 + 0.5));
        var fixes2 = new List<(double, double)>();
        for (double x = 200; x <= 240; x += 1) fixes2.Add((x, 50));
        var levels2 = new List<(double, double, double)> { (200, 240, 50) };
        var r2 = RoadPlanner.Compute(w, pts2, s, fixedAt: fixes2, levels: levels2);
        double hBridge = r2.Profile[r2.IndexAt(220)], hAfter = r2.Profile[r2.IndexAt(244)], hTop = r2.Profile[r2.IndexAt(420)];
        Console.WriteLine($"       valley bridge: bridge {hBridge:0.0} m, 4 m behind {hAfter:0.0} m, plateau {hTop:0.0} m, maxGrade {r2.MaxGrade * 100:0.0} %");
        Check.True(Math.Abs(hBridge - 50) < 0.2, $"bridge height off: {hBridge:0.0} m");
        Check.True(hAfter - 50 < 1.0, $"jump right behind bridge approach: {hAfter - 50:0.00} m in 4 m");
        Check.True(Math.Abs(hTop - 60) < 1.5, $"did not reach plateau: {hTop:0.0} m");
        Check.True(r2.MaxGrade <= 0.135, $"too steep leaving valley bridge: {r2.MaxGrade * 100:0.0} %");
    }

    [Test("Road curve: no swing to the other side before a bend (control points at uneven distances)")]
    static void CurveNoSwing()
    {
        // like a town road: 1 m in the tile, 10 m straight to the north, then the route turns to the north-east with points 20 m apart
        var pts = new List<Point> { new(0, 0), new(0, 13), new(12, 27), new(26, 41), new(40, 55) };
        var path = RoadPlanner.BuildPath(pts);
        // the bend goes to the right (east): the line must not go west of x = 0 before it
        double worst = path.Max(p => -p.X);
        Console.WriteLine($"       largest swing to the left before the bend: {worst:0.00} m");
        Check.True(worst < 0.15, $"road swings {worst:0.00} m to the other side before the bend");
        // the curve still passes through every control point
        foreach (var p in pts) Check.True(path.Any(q => Math.Abs(q.X - p.X) < 0.01 && Math.Abs(q.Z - p.Y) < 0.01), $"control point {p} not on the line");
        // road network: 1 m in the tile, then the 24 m connecting piece as points every 12 m, then the bend
        var net = new List<Point> { new(0, 0), new(0, 1), new(0, 13), new(0, 25), new(9, 35), new(20, 44), new(32, 52) };
        double worstNet = RoadPlanner.BuildPath(net).Max(p => -p.X);
        Console.WriteLine($"       road network, largest swing to the left: {worstNet:0.00} m");
        Check.True(worstNet < 0.15, $"network road swings {worstNet:0.00} m to the other side before the bend");
        // the same at the end of the road (reversed)
        net.Reverse();
        double worstRev = RoadPlanner.BuildPath(net).Max(p => -p.X);
        Check.True(worstRev < 0.15, $"reversed: road swings {worstRev:0.00} m to the other side before the bend");
    }

    /// tightest curve of a center line as radius (m), measured as change of direction over 6 m; from/to = part of the road (m)
    static double TightestRadius(List<PathPoint> path, double from, double to)
    {
        double best = double.MaxValue;
        for (int i = 1, j = 1; i < path.Count; i++)
        {
            while (j < path.Count - 1 && path[j].Dist < path[i].Dist + 6) j++;
            if (j >= path.Count - 1 || path[i].Dist < from || path[j].Dist > to) continue;
            var a = new Vector(path[i].X - path[i - 1].X, path[i].Z - path[i - 1].Z);
            var b = new Vector(path[j + 1].X - path[j].X, path[j + 1].Z - path[j].Z);
            if (a.Length < 1e-6 || b.Length < 1e-6) continue;
            double turn = Math.Abs(Vector.AngleBetween(a, b)) * Math.PI / 180;
            if (turn > 1e-4) best = Math.Min(best, (path[j].Dist - path[i].Dist) / turn);
        }
        return best;
    }

    [Test("Town road: whole route without tight kinks – start, middle and end alike")]
    static void TownRoadWholeRoute()
    {
        var w = Fixture.LoadCopy();
        int wide = 0;
        double worst = double.MaxValue;
        int routes = 0;
        foreach (var (sx, sz, gx, gz) in new[] { (0.0, 200.0, 20.0, 40.0), (0, 200, 37, 40), (0, 200, 53, 40), (0, 200, 71, 40),
            (0, 200, -29, 40), (0, 200, -46, 40), (0, 200, -64, 40), (-300, -100, -150, -260), (300, 300, 420, 120), (-500, 400, -380, 260) })
        {
            var start = new Point(sx, sz); var goal = new Point(gx, gz);
            int fb = RoadLine.Fallbacks;
            var pts = RoadRouter.Route(w, start, 12, goal, 12, 0.18, (_, _) => false);
            if (pts == null) continue;
            routes++;
            var path = RoadPlanner.BuildPath(pts);
            double total = path[^1].Dist;
            if (RoadLine.Fallbacks != fb) Console.WriteLine("         (old smoothing)");
            double rs = TightestRadius(path, 0, 50), rm = TightestRadius(path, 50, total - 50), re = TightestRadius(path, total - 50, total);
            static string R(double r) => r > 9999 ? "straight" : $"{r:0} m";
            Console.WriteLine($"         {sx},{sz} -> {gx},{gz}: {total:0} m, tightest curve: start {R(rs)}, middle {R(rm)}, end {R(re)}");
            worst = Math.Min(worst, Math.Min(rs, Math.Min(rm, re)));
            if (Math.Min(rs, Math.Min(rm, re)) >= 30) wide++;
        }
        Console.WriteLine($"       {routes} routes, tightest curve overall {worst:0} m");
        Check.True(routes >= 8, "too few routes found");
        // before the rebuild (2026-11-13) every route had curves of 7–17 m; routes on steep slopes may still need tighter ones
        Check.True(wide >= 8, $"only {wide} of {routes} routes without curves under 30 m");
    }

    [Test("Towns (real map): T and crossing gateways sit at the town edge, every exit leads into the town or is open")]
    static void JunctionGatewaysRealMap()
    {
        var (w, game) = CityTestWorld();
        var rules = CityRules.Load(game, w.Size);
        var files = PrefabCatalog.AllFiles(game);
        var defs = files.Keys.Where(n => n.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(n => n, n => { var d = PrefabCatalog.Describe(files[n]); d.Kind = PrefabKind.Tile; return d; }, StringComparer.OrdinalIgnoreCase);
        var spec = new CitySpec { Types = rules.Types.Select(t => t.Clone()).ToList(), Seed = w.Name, FillLots = false, TradersPerBiome = 3, JunctionGateways = 1 };
        var gen = new CityGenerator(w, spec, rules, defs.Keys, n => defs.TryGetValue(n, out var d) && d.Found ? d : null);
        Check.True(gen.JunctionKindCount >= 2, $"only {gen.JunctionKindCount} T/crossing gateway kinds found in the game");
        if (Environment.GetEnvironmentVariable("YAMEE_TEST_LOG") != null)
            CityGenerator.Log = s => { if (s.StartsWith("junction")) Console.WriteLine("         " + s); };
        var plans = gen.Plan(new Progress<string>());
        CityGenerator.Log = null;
        int t = 0, x = 0, big = 0;
        foreach (var p in plans)
        {
            if (p.Tiles.Count(k => !k.Name.Contains("gateway")) >= 6) big++;
            var g = p.Gateway;
            if (g == null || w.TileSurface(g, out var exits) == null || exits.Count < 3) continue;
            if (exits.Count == 3) t++; else x++;
            // directly beside a tile of the same town, at its ground height
            var nb = p.Tiles.Where(o => o != g && (Math.Abs(o.X - g.X) == 150 && o.Z == g.Z || Math.Abs(o.Z - g.Z) == 150 && o.X == g.X)).ToList();
            Check.True(nb.Count >= 1, $"{g.Name} at {g.X},{g.Z} has no town tile beside it");
            Check.True(nb.Any(o => Math.Abs(o.TerrainHeight - g.TerrainHeight) < 0.01), $"{g.Name} at {g.X},{g.Z} not at the height of the town");
            foreach (var (ex, ez) in exits)
            {
                bool intoTown = p.Tiles.Any(o => o != g && ex >= o.X - 0.01 && ex <= o.X + 150.01 && ez >= o.Z - 0.01 && ez <= o.Z + 150.01);
                bool open = p.OpenExits.Any(o => Math.Abs(o.X - ex) < 3 && Math.Abs(o.Y - ez) < 3);
                Check.True(intoTown || open, $"{g.Name} at {g.X},{g.Z}: exit {ex:0},{ez:0} leads nowhere");
            }
            if (p.Trader != null) Check.True(g.Def.Lots.Any(l => l.SizeX == 60 && l.SizeZ == 60), $"trader on {g.Name} without a lot");
        }
        Console.WriteLine($"       {plans.Count} towns ({big} with 6+ tiles): {t} T gateways, {x} crossing gateways, {plans.Count(p => p.Trader != null)} traders");
        Check.True(t + x >= Math.Max(1, big / 3), $"only {t + x} T/crossing gateways in {big} towns with 6+ tiles");
    }

    /// largest change of direction between two consecutive pieces of a line (degrees; pieces under 1 m skipped)
    static double SharpestTurn(List<Point> pts)
    {
        double worst = 0;
        Vector? prev = null;
        for (int q = 1; q < pts.Count; q++)
        {
            var v = pts[q] - pts[q - 1];
            if (v.Length < 1) continue;
            if (prev is Vector p) worst = Math.Max(worst, Math.Abs(Vector.AngleBetween(p, v)));
            prev = v;
        }
        return worst;
    }

    /// YAMEE_TEST_CITY_WORLD = project with a terrain copy (yamee_terrain); it is copied, reset to the terrain copy and loaded
    static (WorldData W, string Game) CityTestWorld()
    {
        // YAMEE_TEST_GAME = other game folder (e.g. the 2.6 copy), otherwise the installed game
        string src = Environment.GetEnvironmentVariable("YAMEE_TEST_CITY_WORLD");
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? GameLocator.Find()?.Folder;
        if (string.IsNullOrEmpty(src) || !TerrainCopy.Exists(src) || game == null)
            Check.Skip("set YAMEE_TEST_CITY_WORLD to a project folder with a terrain copy (it is only read; needs the game folder)");
        string dst = Path.Combine(Fixture.Temp, "citytest", Path.GetFileName(src));
        if (Directory.Exists(dst)) Directory.Delete(dst, true);
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        Directory.CreateDirectory(TerrainCopy.Dir(dst));
        foreach (var f in Directory.GetFiles(TerrainCopy.Dir(src))) File.Copy(f, Path.Combine(TerrainCopy.Dir(dst), Path.GetFileName(f)));
        TerrainCopy.Restore(dst);
        return (WorldData.Load(dst, game, new Progress<string>()), game);
    }

    [Test("Towns (real map): town roads without tight curves")]
    static void TownRoadsRealMap()
    {
        // YAMEE_TEST_CITY_WORLD = project with a terrain copy (yamee_terrain); it is copied and reset to the terrain copy
        var (w, game) = CityTestWorld();
        var rules = CityRules.Load(game, w.Size);
        var files = PrefabCatalog.AllFiles(game);
        var defs = files.Keys.Where(n => n.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(n => n, n => { var d = PrefabCatalog.Describe(files[n]); d.Kind = PrefabKind.Tile; return d; }, StringComparer.OrdinalIgnoreCase);
        var spec = new CitySpec { Types = rules.Types.Select(t => t.Clone()).ToList(), Seed = w.Name, FillLots = false, TradersPerBiome = 0 };
        if (double.TryParse(Environment.GetEnvironmentVariable("YAMEE_TEST_JUNCTION"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double jg)) spec.JunctionGateways = jg;
        var gen = new CityGenerator(w, spec, rules, defs.Keys, n => defs.TryGetValue(n, out var d) && d.Found ? d : null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int gm0 = RoadRouter.GentleMissing, normal0 = RoadRouter.NormalUsed, fb0 = RoadLine.Fallbacks;
        var gaps = new List<(string Kind, double Gap)>();
        CityGenerator.Log = s =>
        {
            var p = s.Split(' ');
            if (p[0] == "unitgap") gaps.Add((p[1], double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)));
        };
        var plans = gen.Plan(new Progress<string>());
        CityGenerator.Log = null;
        foreach (var kind in new[] { "district", "gateway" })
        {
            var g = gaps.Where(x => x.Kind == kind).Select(x => x.Gap).OrderBy(x => x).ToList();
            if (g.Count > 0) Console.WriteLine($"       distance {kind} – main part ({g.Count}): mean {g.Average():0} m, median {g[g.Count / 2]:0} m, largest {g[^1]:0} m");
        }
        Console.WriteLine($"       searches without the gentle path: {RoadRouter.NormalUsed - normal0}, (gentle not found: {RoadRouter.GentleMissing - gm0}), without the smooth line: {RoadLine.Fallbacks - fb0}");
        var radii = new List<double>();
        foreach (var road in plans.SelectMany(p => p.Roads))
        {
            var path = RoadPlanner.BuildPath(road.Points);
            radii.Add(TightestRadius(path, 0, path[^1].Dist));
            if (radii[^1] < 25)
            {
                double tot = path[^1].Dist, rs = TightestRadius(path, 0, 30), re = TightestRadius(path, tot - 30, tot), rm = TightestRadius(path, 30, tot - 30);
                Console.WriteLine($"         road {road.Points[0].X:0},{road.Points[0].Y:0} -> {road.Points[^1].X:0},{road.Points[^1].Y:0}: {tot:0} m, {road.Points.Count} points, first 30 m {rs:0}, middle {rm:0}, last 30 m {re:0}");
            }
        }
        // distance of the road center to the tiles (without the first/last 25 m at the exits): roads running along tiles
        // (Bobilu-Land 2026-11-16: road edge about 1 m beside the tile, cut wall at the fence)
        var allTiles = plans.SelectMany(p => p.Tiles).ToList();
        double hugMeters = 0, closest = double.MaxValue, edgeOver = 0;
        int hugRoads = 0;
        foreach (var road in plans.SelectMany(p => p.Roads))
        {
            var path = RoadPlanner.BuildPath(road.Points);
            double tot = path[^1].Dist, near = 0;
            foreach (var q in path)
            {
                if (q.Dist < 25 || q.Dist > tot - 25) continue;
                double d = allTiles.Min(t => { double dx = Math.Max(0, Math.Max(t.X - q.X, q.X - (t.X + 150))), dz = Math.Max(0, Math.Max(t.Z - q.Z, q.Z - (t.Z + 150))); return Math.Sqrt(dx * dx + dz * dz); });
                closest = Math.Min(closest, d);
                if (d < 15) near += 1;
                if (d < Math.Max(road.WidthA, road.WidthB) / 2 + 3) edgeOver += 1;   // road edge (with shoulder) closer than 2 m to the tile
            }
            hugMeters += near;
            if (near > 20) hugRoads++;
        }
        Console.WriteLine($"       road center closer than 15 m to a tile: {hugMeters:0} m in total, {hugRoads} roads over 20 m; closest {closest:0.0} m; road edge within 2 m of a tile: {edgeOver:0} m");
        radii.Sort();
        int under25 = radii.Count(r => r < 25), under40 = radii.Count(r => r < 40);
        Console.WriteLine($"       {plans.Count} towns, {plans.Sum(p => p.PartFloors.Count)} districts, {plans.Sum(p => p.Tiles.Count)} tiles, {radii.Count} roads in {sw.Elapsed.TotalSeconds:0.0} s; tightest curve per road: " +
            $"median {radii[radii.Count / 2]:0} m, worst {radii[0]:0} m; under 25 m: {under25}, under 40 m: {under40}");
        Console.WriteLine("       worst ten: " + string.Join(", ", radii.Take(10).Select(r => $"{r:0}")));
        Check.True(radii[radii.Count / 2] >= 30, $"median of the tightest curve only {radii[radii.Count / 2]:0} m (before the rebuild: 9 m)");
    }

    [Test("Town road: the searched path starts exactly on the connecting line (no hook from the 4 m search grid)")]
    static void TownRoadGridOffset()
    {
        // tile exit at x = 0 heading south (z decreasing); the search grid has its cell centers at x = 2 (2 m east of the line):
        // the path runs south on x = 2 for 30 m, then bends to the west with a radius of about 60 m (like YAMEE-Land 1609/1188)
        static List<Point> Raw()
        {
            var r = new List<Point>();
            for (double z = -11; z >= -41; z -= 10) r.Add(new Point(2, z));
            for (double a = 0.17; a < 1.2; a += 0.17) r.Add(new Point(2 - 60 + 60 * Math.Cos(a), -41 - 60 * Math.Sin(a)));
            return r;
        }
        var start = new Point(0, 0); var goal = new Point(-150, -110);
        Point searchStart = new(0, -10), searchGoal = new(-140, -110);
        double Measure(List<Point> raw)
        {
            var pts = RoadRouter.SmoothPath(raw, start, (0, -1), goal, (-1, 0), _ => false);
            // the bend goes to the west: before it the road must not go east of the connecting line
            return RoadPlanner.BuildPath(pts).Max(p => p.X);
        }
        double before = Measure(Raw());
        var aligned = Raw();
        aligned.Add(new Point(-138, -111));
        RoadRouter.AlignToEnds(aligned, searchStart, searchGoal);
        aligned.RemoveAt(aligned.Count - 1);
        double after = Measure(aligned);
        Console.WriteLine($"       swing to the east before the bend: without alignment {before:0.00} m, with {after:0.00} m");
        Check.True(after < 0.2, $"road swings {after:0.00} m to the east before the bend");
    }

    [Test("Game versions: texts of log, main.ttw and map_info.xml are read alike")]
    static void GameVersionTexts()
    {
        var a = GameVersion.Parse("V 3.4.0 (b10)");
        Check.Equal(new GameVersion(3, 40, 10), a, "log 3.4");
        Check.Equal(a, GameVersion.Parse("V.3.40.10"), "map_info 3.4");
        Check.Equal(new GameVersion(3, 0, 259), GameVersion.Parse("V.3.0.259"), "map_info 3.0");
        Check.Equal(new GameVersion(3, 0, 259), GameVersion.Parse("V 3.0.0 (b259)"), "log 3.0");
        var b = GameVersion.Parse("V 2.6 (b14)");
        Check.Equal(new GameVersion(2, 6, 14), b, "log 2.6");
        Check.Equal(b, GameVersion.Parse("V.2.6.14"), "map_info 2.6");
        Check.Equal("V 3.4.0 (b10)", a.Display, "display 3.4");
        Check.Equal("3.4", a.Short, "short 3.4");
        Check.Equal("2.6", b.Short, "short 2.6");
        Check.Equal("V.2.6.14", b.MapInfo, "map_info text");
        Check.Equal("2.6 + Undead Legacy", new GameProfile(b, "UndeadLegacy").Text, "profile text");
        Check.True(new GameProfile(b, "UndeadLegacy").Matches(new GameProfile(new GameVersion(2, 6, 20), "undeadlegacy")), "same version and mod should match");
        Check.True(!new GameProfile(b, "UndeadLegacy").Matches(new GameProfile(b, null)), "mod differs");
    }

    [Test("Game versions: generated world in the format of 2.6 and 3.4 (main.ttw, map_info.xml)")]
    static void GeneratorFormats()
    {
        foreach (var v in new[] { new GameVersion(2, 6, 14), new GameVersion(3, 40, 10), new GameVersion(3, 10, 5) })
        {
            string dir = Path.Combine(Fixture.Temp, "fmt" + v.MapInfo);
            new WorldGenerator(Fixture.Spec("Fmt")).Write(dir, null, new Progress<string>(), v);
            var ttw = File.ReadAllBytes(Path.Combine(dir, "main.ttw"));
            Check.Equal(v.Display, GameVersion.TtwVersionText(ttw), "version text in main.ttw " + v);
            Check.Equal(v.Major >= 3 ? 23 : 22, (int)ttw[4], "file version of main.ttw " + v);
            Check.Equal(v.Major >= 3 ? 264 : 238, ttw.Length - v.Display.Length + (v.Major >= 3 ? 13 : 11), "length of main.ttw " + v);
            int after = 9 + v.Display.Length;
            Check.Equal(v.Minor, BitConverter.ToInt32(ttw, after + 8), "minor number in main.ttw " + v);
            Check.Equal(v, GameVersion.FromWorld(dir), "version of the world " + v);
            string info = File.ReadAllText(Path.Combine(dir, "map_info.xml"));
            Check.Equal(v.Major >= 3, info.Contains("SchemaVersion") && info.Contains("<property class=\"Generation\">"), "3.x format of map_info.xml " + v);
            Check.Equal(v.Major < 3, info.Contains("Generation.Seed"), "2.x format of map_info.xml " + v);
            System.Xml.Linq.XDocument.Parse(info);   // well-formed
            var w = WorldData.Load(dir, null, new Progress<string>());
            Check.Equal(2048, w.Size, "size " + v);
        }
    }

    [Test("Game versions: building names from Localization.txt (2.x) and Localization.csv (3.x)")]
    static void LocalizationFiles()
    {
        foreach (var ext in new[] { ".txt", ".csv" })
        {
            string game = Path.Combine(Fixture.Temp, "game" + ext.TrimStart('.'));
            Directory.CreateDirectory(Path.Combine(game, "Data", "Prefabs"));
            Directory.CreateDirectory(Path.Combine(game, "Data", "Config"));
            File.WriteAllText(Path.Combine(game, "Data", "Config", "Localization" + ext),
                "Key,File,Type,UsedInMainMenu,NoTranslate,KeepLoaded,english,Context / Alternate Text,german\n" +
                "house_old_ranch_01,POI,POIName,,x,x,The Wetz Residence,,\n");
            var labels = PrefabCatalog.Labels(game, new[] { "house_old_ranch_01" });
            Check.Equal("The Wetz Residence", labels.GetValueOrDefault("house_old_ranch_01"), "name from Localization" + ext);
        }
    }

    [Test("Game versions: mod of a world folder in the game")]
    static void ModOfWorld()
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie");
        Check.Equal("UndeadLegacy", GameLocator.ModOfWorldFolder(Path.Combine(data, "UndeadLegacy", "GeneratedWorlds", "My World")), "mod folder");
        Check.Equal<string>(null, GameLocator.ModOfWorldFolder(Path.Combine(data, "GeneratedWorlds", "My World")), "normal folder");
        Check.Equal<string>(null, GameLocator.ModOfWorldFolder(null), "no folder");
        Check.Equal(Path.Combine(data, "UndeadLegacy", "GeneratedWorlds"), GameLocator.WorldsFolderFor(new GameProfile(new GameVersion(2, 6, 14), "UndeadLegacy")), "worlds of the mod");
        Check.Equal(Path.Combine(data, "GeneratedWorlds"), GameLocator.WorldsFolderFor(new GameProfile(new GameVersion(3, 40, 10), null)), "normal worlds");
    }

    [Test("Game versions: version and mod of installed game folders (optional, YAMEE_TEST_GAMES)")]
    static void InstalledGames()
    {
        // folders separated by ";" – e.g. the installed 3.4 and a copy of 2.6 with Undead Legacy
        var folders = (Environment.GetEnvironmentVariable("YAMEE_TEST_GAMES") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (folders.Length == 0) Check.Skip("YAMEE_TEST_GAMES not set");
        foreach (var f in folders)
        {
            var p = GameLocator.ProfileOf(f);
            Check.True(p?.Version != null, "no version found in " + f);
            Console.WriteLine($"       {f}: {p.Version.Display}" + (p.Mod != null ? " + " + p.Mod : ""));
        }
    }

    [Test("Project check: missing buildings are found, replacements fit the lot or are similar in the wilderness")]
    static void ProjectCheckMissing()
    {
        var w = Fixture.LoadCopy();
        PrefabDef Def(PrefabKind k, int sx, int sz, int tier = 1, params string[] tags)
        {
            var d = new PrefabDef { Found = true, Kind = k, SizeX = sx, SizeZ = sz, Tier = tier, YOffset = -1 };
            foreach (var t in tags) d.Tags.Add(t);
            return d;
        }
        // a city tile with one lot 20 × 30 m at (10, 10), a building on it and two in the wilderness
        var tileDef = Def(PrefabKind.Tile, 150, 150);
        tileDef.Lots.Add(new LotMarker(10, 1, 10, 20, 30, 0, ""));
        var tile = new PlacedPrefab { Name = "rwg_tile_rural_test", Def = tileDef, X = 0, Y = 40, Z = 0 };
        var onLot = new PlacedPrefab { Name = "house_gone_01", Def = new PrefabDef { Kind = PrefabKind.Poi }, X = 10, Y = 40, Z = 10 };
        var wild = new PlacedPrefab { Name = "army_camp_10", Def = Def(PrefabKind.Poi, 40, 40, 3, "wilderness"), X = 400, Y = 40, Z = 400, Rotation = 1 };
        var fine = new PlacedPrefab { Name = "cabin_01", Def = Def(PrefabKind.Poi, 20, 20), X = -400, Y = 40, Z = -400 };
        var part = new PlacedPrefab { Name = "part_gone_sign", Def = new PrefabDef { Kind = PrefabKind.Part }, X = 500, Y = 40, Z = 500 };
        w.Prefabs.AddRange(new[] { tile, onLot, wild, fine, part });

        // the installed game: the tile and the cabin exist, the rest not; replacements of all kinds
        var target = new Dictionary<string, PrefabDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["rwg_tile_rural_test"] = tileDef, ["cabin_01"] = fine.Def,
            ["house_gone_01"] = new PrefabDef(), ["army_camp_10"] = new PrefabDef(), ["part_gone_sign"] = new PrefabDef { Kind = PrefabKind.Part },
        };
        var index = new Dictionary<string, PrefabDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["house_new_01"] = Def(PrefabKind.Poi, 20, 30, 1, "rural"),    // fits the lot
            ["house_new_02"] = Def(PrefabKind.Poi, 20, 31, 1, "rural"),    // 1 m too deep
            ["house_new_03"] = Def(PrefabKind.Poi, 20, 30, 1, "downtown"), // wrong district
            ["army_camp_11"] = Def(PrefabKind.Poi, 60, 60, 3, "wilderness"),
            ["site_camp_01"] = Def(PrefabKind.Poi, 40, 40, 3, "wilderness"),
            ["rubble_filler_01"] = Def(PrefabKind.Filler, 40, 40, 0),
        };
        // the wilderness building is missing in the game, but known in the project's own game folder (size 40 × 40)
        var r = ProjectCheck.Run(w, target, () => index);
        Check.Equal(3, r.Issues.Count, "issues");
        var lotIssue = r.Issues.Single(i => i.Name == "house_gone_01");
        Check.True(lotIssue.Lot != null && lotIssue.Lot.Width == 20 && lotIssue.Lot.Depth == 30, "lot of the missing building");
        Check.Equal("house_new_01", string.Join(",", lotIssue.Candidates), "only buildings that fit the lot");
        var wildIssue = r.Issues.Single(i => i.Name == "army_camp_10");
        Check.Equal("army_camp_11", wildIssue.Candidates.FirstOrDefault(), "same kind first");
        Check.True(!wildIssue.Candidates.Contains("rubble_filler_01"), "no filler for a building");
        var partIssue = r.Issues.Single(i => i.Name == "part_gone_sign");
        Check.True(!partIssue.CanReplace && partIssue.Kind == PrefabKind.Part, "parts are only shown");

        // replacements: on the lot like the game, in the wilderness around the same center
        var p1 = ProjectCheck.Replacement(w, onLot, lotIssue.Lot, "house_new_01", index["house_new_01"]);
        Check.Equal("10,10,0", $"{p1.X},{p1.Z},{p1.Rotation}", "position on the lot");
        Check.Equal(40 + 1 - 1, p1.Y, "height on the lot = tile y + lot y + YOffset");
        var p2 = ProjectCheck.Replacement(w, wild, null, "army_camp_11", index["army_camp_11"]);
        Check.Equal("390,390,1", $"{p2.X},{p2.Z},{p2.Rotation}", "same center and rotation in the wilderness");
        Check.Equal("army_camp", ProjectCheck.Base("army_camp_10"), "base name");
        Check.Equal("house_modern", ProjectCheck.Base("house_modern_34b"), "base name with letter");

        // replacing is one step that can be undone exactly
        var e = new WorldEdits(w);
        string before = string.Join(";", w.Prefabs.Select(p => $"{p.Name}@{p.X},{p.Y},{p.Z}"));
        e.Run("replace", "replace", ed => { ed.ReplacePrefab(onLot, p1); ed.ReplacePrefab(wild, p2); });
        Check.True(w.Prefabs.Contains(p1) && w.Prefabs.Contains(p2) && !w.Prefabs.Contains(onLot), "replaced");
        e.Undo();
        Check.Equal(before, string.Join(";", w.Prefabs.Select(p => $"{p.Name}@{p.X},{p.Y},{p.Z}")), "undo");
    }

    [Test("Road network: node tile wrap-around check rejects roads looping behind the tile and allows normal 90° turns")]
    static void NodeTileWrapAroundCheck()
    {
        var tile = new PlacedPrefab { Name = RoadNetwork.NodeT, Def = new PrefabDef { Found = true, Kind = PrefabKind.Tile, SizeX = 150, SizeZ = 150 }, X = 1775, Z = -3694 };
        // east exit at (1925, -3619), outward (+1, 0)
        var straightEast = new[] { new Point(1925, -3619), new Point(2050, -3619), new Point(2300, -3619) };
        var turnSouth = new[] { new Point(1925, -3619), new Point(1975, -3619), new Point(2000, -3720), new Point(2000, -4000) };
        var wrapUnderToWest = new[] { new Point(1925, -3619), new Point(1975, -3619), new Point(1960, -3760), new Point(1820, -3770), new Point(1600, -3750) };
        var wideWrapToWest = new[] { new Point(1925, -3619), new Point(1990, -3680), new Point(1900, -3950), new Point(1650, -3950) };
        Check.True(!RoadNetwork.WrapsAroundTile(tile, 1, 0, straightEast), "straight east road must be allowed");
        Check.True(!RoadNetwork.WrapsAroundTile(tile, 1, 0, turnSouth), "normal 90° turn south must be allowed");
        Check.True(RoadNetwork.WrapsAroundTile(tile, 1, 0, wrapUnderToWest), "road wrapping under the tile to the west must be rejected");
        Check.True(RoadNetwork.WrapsAroundTile(tile, 1, 0, wideWrapToWest), "road swinging wide behind the tile to the west must be rejected");
    }

    [Test("Road network: sharp 90° exit corner is rounded softly by the spline without leaving the exit axis")]
    static void ExitCornerSoftSpline()
    {
        // corridor with 24 m fixed straight going west from (0,0), then 90° turn south at (-44, 0)
        var r = new List<Point>();
        var fx = new List<bool>();
        for (int x = 0; x >= -44; x -= 4) { r.Add(new Point(x, 0)); fx.Add(x >= -24); }
        for (int z = -4; z >= -160; z -= 4) { r.Add(new Point(-44, z)); fx.Add(false); }
        fx[^1] = true;
        var p = RoadLine.Spline(r, fx.ToArray(), Math.Pow(38.0 / 4, 4), _ => 14.0, (_, _) => false);
        Check.True(p != null, "spline failed on 90° corner");
        var pts = p.ToList();
        // still straight at 30 m out of the exit
        var at30 = RoadNetwork.At(pts, 30).P;
        Check.True(Math.Abs(at30.Y) < 1.0, $"road bends too early at 30 m ({Math.Abs(at30.Y):0.00} m off axis)");
        // sharpest turn between 4 m segments must be gentle (under 12°, corresponding to R >= ~20 m)
        double maxTurn = 0;
        for (int i = 1; i + 1 < pts.Count; i++)
            maxTurn = Math.Max(maxTurn, Math.Abs(Vector.AngleBetween(pts[i] - pts[i - 1], pts[i + 1] - pts[i])));
        Console.WriteLine($"       90° exit corner: at 30 m {Math.Abs(at30.Y):0.00} m off axis, sharpest 4 m step turn {maxTurn:0.0}°");
        Check.True(maxTurn < 12, $"corner stayed too sharp ({maxTurn:0.0}° per 4 m step)");
    }

    [Test("Road network: wide 360° self-crossing loop is detected by HasSelfLoop, normal hairpin is allowed")]
    static void WideSelfLoopDetected()
    {
        // wide ~600 m loop leaving east from (0,0), swinging south-west, then heading north across itself
        var loop = new List<Point>
        {
            new(0, 0), new(80, 0), new(120, -60), new(80, -160), new(-20, -220),
            new(-100, -180), new(-80, -80), new(40, -20), new(60, 80), new(60, 200),
        };
        // normal hairpin turn (no self-crossing, stays >= 80 m apart)
        var hairpin = new List<Point>
        {
            new(0, 0), new(80, 0), new(140, 40), new(140, 110), new(80, 150), new(0, 150),
        };
        Check.True(RoadNetwork.HasSelfLoop(loop), "wide 360° self-crossing loop must be detected");
        Check.True(!RoadNetwork.HasSelfLoop(hairpin), "normal hairpin must be allowed");
    }

    [Test("Road network: post-planning dead-end repair connects blocked exits on flat ground and skips steep height steps")]
    static void DeadEndRepairByHeight()
    {
        var w = Fixture.LoadCopy();
        // flatten a 400 x 400 m area around (0, 0) to 50 m height
        for (int z = -200; z <= 200; z++)
            for (int x = -200; x <= 200; x++)
                w.Heights[w.Index(x, z)] = (ushort)(50 * 256);
        var tileDef = new PrefabDef { Found = true, Kind = PrefabKind.Tile, SizeX = 150, SizeZ = 150, YOffset = -1 };
        var tileFlat = new PlacedPrefab { Name = "rwg_tile_rural_test", Def = tileDef, X = -75, Y = 50, Z = 0 };
        var town = new NetTown { Id = 0, Connected = true, X0 = -75, Z0 = 0, X1 = 75, Z1 = 150 };
        town.Tiles.Add(tileFlat);
        // south exit at (0, 0) pointing south (0, -1), with a passing east-west road 50 m in front of it at z = -50
        var exitFlat = new NetExit { Town = town, Tile = tileFlat, X = 0, Z = 0, DX = 0, DZ = -1, Width = 16 };
        town.Exits.Add(exitFlat);
        var passing = new NetRoad { Points = new List<Point> { new(-150, -50), new(0, -50), new(150, -50) }, Width = 16 };
        var plan = new NetworkPlan { Towns = { town }, Roads = { passing } };
        var spec = new NetworkSpec { MaxGrade = 0.18 };

        int fixedFlat = new RoadNetwork(w, spec).RepairDeadEnds(plan);
        Check.Equal(1, fixedFlat, "flat dead-end exit must be connected to the passing road");
        Check.True(exitFlat.Used && plan.Roads.Count == 2 && plan.Roads[1].EndRoad == passing, "connector added to passing road");

        // now put a tile 25 m higher (way steeper than 18 % over 50 m): must NOT force a steep stub
        var tileHigh = new PlacedPrefab { Name = "rwg_tile_rural_test", Def = tileDef, X = -75, Y = 75, Z = 0 };
        var townHigh = new NetTown { Id = 0, Connected = true, X0 = -75, Z0 = 0, X1 = 75, Z1 = 150 };
        townHigh.Tiles.Add(tileHigh);
        var exitHigh = new NetExit { Town = townHigh, Tile = tileHigh, X = 0, Z = 0, DX = 0, DZ = -1, Width = 16 };
        townHigh.Exits.Add(exitHigh);
        var passing2 = new NetRoad { Points = new List<Point> { new(-150, -50), new(0, -50), new(150, -50) }, Width = 16 };
        var planHigh = new NetworkPlan { Towns = { townHigh }, Roads = { passing2 } };

        int fixedHigh = new RoadNetwork(w, spec).RepairDeadEnds(planHigh);
        Check.Equal(0, fixedHigh, "exit with 25 m height step over 50 m must not be force-connected");
        Check.True(!exitHigh.Used, "steep exit stays unused");
    }

    [Test("Wasteland damage: roads & paths break at earthquake cracks and bomb craters (wasteland only)")]
    static void WastelandCracksAndCratersBreakRoads()
    {
        var w = Fixture.LoadCopy();
        int n = w.Size / 4;
        // flatten terrain to 50 m and paint south half (z <= 150) as wasteland (biome 4), north half (z > 150) as pine forest (biome 0)
        for (int z = -w.Half; z < w.Half; z++)
            for (int x = -w.Half; x < w.Half; x++)
                w.Heights[w.Index(x, z)] = (ushort)(50 * 256);
        int bs = w.BiomeSize;
        for (int by = 0; by < bs; by++)
            for (int bx = 0; bx < bs; bx++)
            {
                double wz = w.Half - (by + 0.5) * w.Size / bs;
                w.Biomes[by * bs + bx] = (byte)(wz <= 150 ? 4 : 0);
            }

        // build synthetic wasteland damage grid (Cell = 4 m):
        // 1) earthquake crack along x = 0 (z = -200..400): half-width 4 m, depth -10 m, +2 m fault offset on east side (fading over 25 m)
        // 2) bomb crater at (120, 0) (wasteland) and (120, 300) (pine forest): radius 18 m, depth -6 m, +2 m rim
        var delta = new float[n * n];
        var breakSd = new float[n * n];
        Array.Fill(breakSd, -16f);
        static double Smooth(double a, double b, double v) { double t = Math.Clamp((v - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double wx = (i + 0.5) * 4 - w.Half, wz = (j + 0.5) * 4 - w.Half;
                int idx = j * n + i;
                double dH = 0, sd = -16;
                // crack along x = 0
                if (wz >= -200 && wz <= 400)
                {
                    double d = Math.Abs(wx);
                    const double wv = 4.0;
                    if (d < wv) dH -= 10.0 * Math.Sqrt(1 - (d / wv) * (d / wv));
                    if (wx > 0) dH += 2.0 * (1 - Smooth(wv, wv + 25, d));
                    sd = Math.Max(sd, wv + 1.8 - d);
                }
                // craters at (120, 0) and (120, 300)
                foreach (double cz in new[] { 0.0, 300.0 })
                {
                    double dist = Math.Sqrt((wx - 120) * (wx - 120) + (wz - cz) * (wz - cz));
                    double t = dist / 18.0;
                    if (t < 1) dH -= 6.0 * (1 - t * t);
                    dH += 2.0 * Math.Exp(-Math.Pow((t - 1) / 0.32, 2)) * (t < 1 ? 1 : 0.85);
                    sd = Math.Max(sd, 18.0 * 1.12 - dist);
                }
                delta[idx] = (float)dH;
                breakSd[idx] = (float)sd;
            }
        var dmg = WastelandDamage.FromGrid(w.Size, n, delta, breakSd);
        w.SetWastelandDamage(dmg);
        // bake the same delta into dtm.raw (like WorldGenerator did)
        for (int z = -220; z <= 420; z++)
            for (int x = -180; x <= 260; x++)
                w.Heights[w.Index(x, z)] = (ushort)Math.Clamp(Math.Round((50.0 + dmg.DeltaAt(x + 0.5, z + 0.5)) * 256), 0, 65535);

        // 1) Asphalt road across wasteland crack (x = 0) and bomb crater (x = 120) with BreakInWasteland = true
        var ed = new WorldEdits(w);
        var s = RoadSettings.Asphalt();
        s.BreakInWasteland = true;
        var pts = new List<Point> { new(-120, 0), new(0, 0), new(120, 0), new(220, 0) };
        var r = RoadPlanner.Compute(w, pts, s);
        Check.True(r != null, "wasteland asphalt road planned");
        // pre-damage planning: profile stays close to 50 m (doesn't plunge into the -10 m crack or -6 m crater)
        double minProf = r.Profile.Min(), maxProf = r.Profile.Max();
        Check.True(minProf > 49.0 && maxProf < 51.0, $"profile stayed level across damage ({minProf:0.0}..{maxProf:0.0} m)");
        ed.Run("wasteland asphalt", "test", e => RoadApplier.Apply(w, e, r, new Progress<string>()));

        // inside crack (x = -3..3) and inside bomb crater (dist <= 16): mostly bare wasteland ground, with scattered asphalt fragments
        // and ZERO gravel inside the asphalt roadway (gravel shoulder stays ONLY at the outer road edge!)
        int crackTotal = 0, crackAsphalt = 0, craterTotal = 0, craterAsphalt = 0, innerGravel = 0;
        for (int x = -15; x <= 145; x++)
            for (int z = -7; z <= 7; z++)
                if (w.RoadAt(x, z) == WorldData.Gravel) innerGravel++;
        Check.Equal(0, innerGravel, "no gravel inside asphalt roadway around/in crack or crater (shoulder only at outer road edge)");
        Check.Equal(WorldData.Gravel, w.RoadAt(-40, 8), "gravel shoulder intact at outer road edge");

        for (int x = -3; x <= 3; x++)
            for (int z = -7; z <= 7; z++) { crackTotal++; if (w.RoadAt(x, z) == WorldData.Asphalt) crackAsphalt++; }
        for (int x = 104; x <= 136; x++)
            for (int z = -7; z <= 7; z++)
            {
                if ((x - 120) * (x - 120) + z * z > 16 * 16) continue;
                craterTotal++;
                if (w.RoadAt(x, z) == WorldData.Asphalt) craterAsphalt++;
            }
        double crackFrac = (double)crackAsphalt / crackTotal, craterFrac = (double)craterAsphalt / craterTotal;
        Console.WriteLine($"       wasteland fragments: crack {crackAsphalt}/{crackTotal} ({crackFrac * 100:0.0} %), crater {craterAsphalt}/{craterTotal} ({craterFrac * 100:0.0} %)");
        Check.True(crackFrac < 0.25, $"earthquake crack mostly bare ground ({crackFrac * 100:0.0} % fragments)");
        Check.True(craterAsphalt > 0 && craterFrac < 0.25, $"bomb crater has scattered asphalt fragments ({craterAsphalt}/{craterTotal}, {craterFrac * 100:0.0} %)");
        // intact asphalt on both sides of crack & crater
        Check.Equal(WorldData.Asphalt, w.RoadAt(-40, 0), "asphalt intact west of crack");
        Check.Equal(WorldData.Asphalt, w.RoadAt(45, 0), "asphalt intact between crack and crater");
        Check.Equal(WorldData.Asphalt, w.RoadAt(180, 0), "asphalt intact east of crater");
        // terrain depths & fault offset preserved!
        Check.True(w.HeightAt(0, 0) < 42.0, $"earthquake crack depth preserved ({w.HeightAt(0, 0):0.0} m)");
        Check.True(w.HeightAt(120, 0) < 45.5, $"bomb crater depth preserved ({w.HeightAt(120, 0):0.0} m)");
        double westLip = w.HeightAt(-14, 0), eastLip = w.HeightAt(12, 0);
        Check.True(eastLip - westLip > 1.0, $"earthquake fault offset preserved across road (west {westLip:0.00} m, east {eastLip:0.00} m)");

        // idempotency: applying the same road a second time must not double-apply the crack/crater delta
        double hCrackBefore = w.HeightAt(0, 0), hEastBefore = w.HeightAt(12, 0);
        ed.Run("wasteland asphalt repeat", "test", e => RoadApplier.Apply(w, e, r, new Progress<string>()));
        Check.True(Math.Abs(w.HeightAt(0, 0) - hCrackBefore) < 0.05, "repeat apply does not double-cut crack");
        Check.True(Math.Abs(w.HeightAt(12, 0) - hEastBefore) < 0.05, "repeat apply does not double-lift fault");

        // 2) Gravel path across wasteland crack at z = -80
        var sg = RoadSettings.Gravel();
        sg.BreakInWasteland = true;
        var rg = RoadPlanner.Compute(w, new List<Point> { new(-120, -80), new(0, -80), new(80, -80) }, sg);
        ed.Run("wasteland gravel", "test", e => RoadApplier.Apply(w, e, rg, new Progress<string>()));
        int gCrackBare = 0, gCrackTotal = 0;
        for (int x = -3; x <= 3; x++)
            for (int z = -82; z <= -78; z++) { gCrackTotal++; if (w.RoadAt(x, z) == 0) gCrackBare++; }
        Check.True(gCrackBare >= gCrackTotal * 0.7, $"gravel path interrupted inside wasteland crack ({gCrackBare}/{gCrackTotal} bare)");
        Check.Equal(WorldData.Gravel, w.RoadAt(-40, -80), "gravel intact west of crack");
        Check.Equal(WorldData.Gravel, w.RoadAt(40, -80), "gravel intact east of crack");

        // 3) Pine forest (z = 300): BreakInWasteland = true must NOT break roads outside wasteland!
        var rf = RoadPlanner.Compute(w, new List<Point> { new(-120, 300), new(0, 300), new(120, 300), new(220, 300) }, s);
        ed.Run("forest asphalt", "test", e => RoadApplier.Apply(w, e, rf, new Progress<string>()));
        Check.Equal(WorldData.Asphalt, w.RoadAt(0, 300), "road in pine forest is NOT interrupted at crack");
        Check.Equal(WorldData.Asphalt, w.RoadAt(120, 300), "road in pine forest is NOT interrupted at crater");

        // 4) BreakInWasteland = false in wasteland (z = -140): builds intact continuous road as before
        var sOff = RoadSettings.Asphalt();
        sOff.BreakInWasteland = false;
        var rOff = RoadPlanner.Compute(w, new List<Point> { new(-120, -140), new(0, -140), new(80, -140) }, sOff);
        ed.Run("wasteland off", "test", e => RoadApplier.Apply(w, e, rOff, new Progress<string>()));
        Check.Equal(WorldData.Asphalt, w.RoadAt(0, -140), "BreakInWasteland = false keeps continuous road");

        // 5) City tile exit protection: a tile right beside the crack (exit at x = -6, z = 80) keeps its approach intact
        var tileDef = new PrefabDef { Found = true, Kind = PrefabKind.Tile, SizeX = 150, SizeZ = 150, YOffset = -1 };
        w.Prefabs.Add(new PlacedPrefab { Name = "rwg_tile_test", Def = tileDef, X = -156, Y = 50, Z = 5 });
        var rt = RoadPlanner.Compute(w, new List<Point> { new(-6, 80), new(60, 80) }, s);
        ed.Run("tile exit protection", "test", e => RoadApplier.Apply(w, e, rt, new Progress<string>()));
        Check.Equal(WorldData.Asphalt, w.RoadAt(0, 80), "road within 10 m of city tile exit stays intact");
    }

    [Test("Wasteland damage: WorldGenerator saves yamee_wasteland.bin and reconstructs on older worlds")]
    static void WastelandDamagePersistenceAndRealWorld()
    {
        string folder = Path.Combine(Fixture.Temp, "waste_world");
        var spec = Fixture.Spec("WasteBreak");
        spec.Shares = new[] { 0, 0, 0, 0, 100 };
        spec.Lakes = 0;
        var res = new WorldGenerator(spec).Write(folder, null, new Progress<string>());
        Check.True(res.Craters > 0 && res.Cracks > 0, $"generated wasteland features ({res.Craters} craters, {res.Cracks} cracks)");
        string binPath = Path.Combine(folder, WastelandDamage.FileName);
        Check.True(File.Exists(binPath), "yamee_wasteland.bin written by WorldGenerator");

        var w = WorldData.Load(folder, null, new Progress<string>());
        var dmg = w.GetWastelandDamage();
        Check.True(dmg != null && dmg.HasAny, "WastelandDamage loaded from yamee_wasteland.bin");

        // find a real deep wasteland crack or crater point (sd > 2.5 m, delta < -3 m) and route a road straight over it
        int hitX = 0, hitZ = 0;
        double deepest = 0;
        for (int z = -w.Half + 200; z < w.Half - 200; z += 8)
            for (int x = -w.Half + 200; x < w.Half - 200; x += 8)
            {
                if (w.BiomeAt(x, z) != 4) continue;
                double sd = dmg.BreakSdAt(x + 0.5, z + 0.5);
                double d = dmg.DeltaAt(x + 0.5, z + 0.5);
                if (sd > 2.5 && d < deepest) { deepest = d; hitX = x; hitZ = z; }
            }
        Check.True(deepest < -2.0, $"found real wasteland crater/crack (delta {deepest:0.0} m at {hitX}/{hitZ})");

        double origH = w.HeightAt(hitX, hitZ);
        var ed = new WorldEdits(w);
        var s = RoadSettings.Asphalt();
        s.BreakInWasteland = true;
        var r = RoadPlanner.Compute(w, new List<Point> { new(hitX - 120, hitZ), new(hitX, hitZ), new(hitX + 120, hitZ) }, s);
        ed.Run("real wasteland road", "test", e => RoadApplier.Apply(w, e, r, new Progress<string>()));
        int bare = 0, tot = 0;
        for (int dz = -3; dz <= 3; dz++)
            for (int dx = -3; dx <= 3; dx++) { tot++; if (w.RoadAt(hitX + dx, hitZ + dz) == 0) bare++; }
        Check.True(bare >= tot * 0.7, $"real wasteland crater/crack interrupts asphalt road ({bare}/{tot} bare)");
        Check.True(Math.Abs(w.HeightAt(hitX, hitZ) - origH) < 2.5, $"crater/crack floor stays carved ({w.HeightAt(hitX, hitZ):0.0} vs {origH:0.0} m)");

        // delete yamee_wasteland.bin and reload: must reconstruct deterministically from yamee_generator.txt!
        File.Delete(binPath);
        var w2 = WorldData.Load(folder, null, new Progress<string>());
        var recon = w2.GetWastelandDamage();
        Check.True(recon != null && recon.HasAny, "WastelandDamage reconstructed from yamee_generator.txt");
        Check.True(recon.BreakSdAt(hitX + 0.5, hitZ + 0.5) > 2.0 && recon.DeltaAt(hitX + 0.5, hitZ + 0.5) < -2.0,
            $"reconstructed damage matches same real crater/crack at {hitX}/{hitZ} (sd={recon.BreakSdAt(hitX + 0.5, hitZ + 0.5):0.0}, d={recon.DeltaAt(hitX + 0.5, hitZ + 0.5):0.0})");
    }
}
