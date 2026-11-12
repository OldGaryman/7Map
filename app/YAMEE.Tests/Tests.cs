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

    public static GeneratorSpec Spec(string seed) => new() { Name = "Test", Size = 2048, Seed = seed, Lakes = 2, SpawnCount = 8 };

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

    /// own copy of the test world, loaded (changes and saving do not touch the other tests)
    public static WorldData LoadCopy()
    {
        string dst = Path.Combine(Temp, "copy" + ++copies, "Test");
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(World)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
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
    [Test("World generator: same start value gives the same world")]
    static void GeneratorDeterministic()
    {
        string a = Path.Combine(Fixture.Temp, "genA"), b = Path.Combine(Fixture.Temp, "genB");
        new WorldGenerator(Fixture.Spec("Same")).Write(a, null, new Progress<string>());
        new WorldGenerator(Fixture.Spec("Same")).Write(b, null, new Progress<string>());
        foreach (var f in new[] { "dtm.raw", "biomes.png", "splat4.png", "spawnpoints.xml" })
            Check.Equal(Check.Hash(Path.Combine(a, f)), Check.Hash(Path.Combine(b, f)), f);
    }

    [Test("World generator: the world can be loaded")]
    static void GeneratedWorldLoads()
    {
        var w = Fixture.LoadCopy();
        Check.Equal(2048, w.Size, "size");
        Check.True(w.Spawns.Count > 0, "no spawn points");
        Check.True(w.Heights.Any(h => h > 0), "all heights 0");
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
        Check.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(w.Folder), "Backups")), "backup before saving");
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

    // ---------- optional: with a real map (environment variable YAMEE_TEST_WORLD = project folder with towns) ----------

    [Test("Road network (real map): planning twice gives the same network, building can be undone exactly")]
    static void RoadNetworkRealMap()
    {
        string folder = Environment.GetEnvironmentVariable("YAMEE_TEST_WORLD");
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "dtm.raw")))
            Check.Skip("set YAMEE_TEST_WORLD to a project folder with towns (it is only read)");
        string copy = Path.Combine(Fixture.Temp, "real", Path.GetFileName(folder.TrimEnd('\\')));
        Directory.CreateDirectory(copy);
        foreach (var f in Directory.GetFiles(folder)) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
        var w = WorldData.Load(copy, AppSettings.GuessGameFolder(), new Progress<string>());
        var spec = new NetworkSpec { Seed = "test" };
        string Plan() => string.Join("|", new RoadNetwork(w, spec).Plan(new Progress<string>()).Roads
            .Select(r => string.Join(" ", r.Points.Select(p => $"{p.X:0.000},{p.Y:0.000}"))));
        Check.Equal(Plan(), Plan(), "network");
        var plan = new RoadNetwork(w, spec).Plan(new Progress<string>());
        var e = new WorldEdits(w);
        string before = Fixture.State(w);
        e.Run("net", "net", ed => NetworkBuilder.Build(w, ed, plan, spec, new Progress<string>()));
        e.Undo();
        Check.Equal(before, Fixture.State(w), "after undo");
    }
}
