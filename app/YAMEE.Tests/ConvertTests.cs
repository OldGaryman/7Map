using MapEditor;
using MapEditor.Map;

namespace YameeTests;

static class ConvertTests
{
    [Test("Convert: main.ttw and map_info.xml of a 3.4 world become those of 2.6 (and back), ID and values kept")]
    static void ConvertFormat()
    {
        var v26 = new GameVersion(2, 6, 14);
        var v34 = new GameVersion(3, 40, 10);
        string Make(GameVersion v)
        {
            string dir = Path.Combine(Fixture.Temp, "conv" + v.MapInfo);
            new WorldGenerator(Fixture.Spec("Conv")).Write(dir, null, new Progress<string>(), v);
            return dir;
        }
        string d26 = Make(v26), d34 = Make(v34);
        foreach (var (from, to, v) in new[] { (d34, d26, v26), (d26, d34, v34) })
        {
            string copy = Path.Combine(Fixture.Temp, "convcopy" + v.MapInfo);
            Directory.CreateDirectory(copy);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)), true);
            File.WriteAllText(Path.Combine(copy, "checksums.txt"), "x");
            var oldTtw = File.ReadAllBytes(Path.Combine(copy, "main.ttw"));
            string backup = Path.Combine(copy, "bk");
            var changed = ProjectConvert.ConvertFormat(copy, new GameProfile(v, v.Major < 3 ? "UndeadLegacy" : null), null, backup);
            Check.True(changed.Contains("main.ttw") && changed.Contains("map_info.xml"), "files changed: " + string.Join(",", changed));
            Check.Equal(File.ReadAllText(Path.Combine(to, "map_info.xml")), File.ReadAllText(Path.Combine(copy, "map_info.xml")), "map_info.xml like a world of " + v);
            var ttw = File.ReadAllBytes(Path.Combine(copy, "main.ttw"));
            var want = File.ReadAllBytes(Path.Combine(to, "main.ttw"));
            Check.True(ttw[..^32].SequenceEqual(want[..^32]), "main.ttw like a world of " + v);
            Check.True(ttw[^32..].SequenceEqual(oldTtw[^32..]), "ID of main.ttw kept");
            Check.Equal(v, GameVersion.FromWorld(copy), "version of the world");
            Check.True(!File.Exists(Path.Combine(copy, "checksums.txt")) && File.Exists(Path.Combine(backup, "checksums.txt")), "game-made files moved to the backup");
            var info = ProjectInfo.Read(copy);
            Check.Equal(v.Display, info.GameVersion, "project file version");
            Check.Equal(v.Major < 3 ? "UndeadLegacy" : null, info.Mod, "project file mod");
            Check.Equal(0, ProjectConvert.ConvertFormat(copy, new GameProfile(v, info.Mod), null, backup).Count, "second run changes nothing");
        }
    }

    [Test("Project check: replace everything automatically – missing tile, building and part (needs the game)")]
    static void ReplaceEverything()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? AppSettings.GuessGameFolder();
        if (game == null) Check.Skip("the game folder was not found");
        var files = PrefabCatalog.AllFiles(game);
        var pois = new Dictionary<string, PrefabDef>(StringComparer.OrdinalIgnoreCase);
        var tiles = new List<(string, PrefabDef)>();
        foreach (var (name, file) in files)
        {
            string f = file.Replace('\\', '/').ToLowerInvariant();
            if (f.Contains("/parts/")) continue;
            var d = PrefabCatalog.Describe(file);
            if (!d.Found) continue;
            if (name.StartsWith("rwg_tile_", StringComparison.OrdinalIgnoreCase)) { d.Kind = PrefabKind.Tile; tiles.Add((name, d)); continue; }
            d.Kind = name.Contains("_filler", StringComparison.OrdinalIgnoreCase) ? PrefabKind.Filler : PrefabKind.Poi;
            pois[name] = d;
        }
        var known = tiles.FirstOrDefault(t => t.Item1.Equals("rwg_tile_rural_t", StringComparison.OrdinalIgnoreCase));
        if (known.Item2 == null) Check.Skip("rwg_tile_rural_t is not in the game folder");
        if (!pois.Keys.Any(k => k.StartsWith("army_camp_", StringComparison.OrdinalIgnoreCase))) Check.Skip("no army camp in the game folder");
        PrefabDef PartDef(string n)
        {
            if (!files.TryGetValue(n, out var file)) return null;
            var d = PrefabCatalog.Describe(file);
            d.Kind = PrefabKind.Part;
            return d.Found ? d : null;
        }

        var w = WorldData.Load(Fixture.LoadCopy().Folder, game, new Progress<string>());
        var tileK = new PlacedPrefab { Name = known.Item1, Def = known.Item2, X = 0, Z = 0, Rotation = 1, Y = 60 + known.Item2.YOffset };
        var tileM = new PlacedPrefab { Name = "rwg_tile_rural_t_99", Def = new PrefabDef { Kind = PrefabKind.Tile, SizeX = 150, SizeZ = 150 }, X = 150, Z = 0, Y = 55 };
        var camp = new PlacedPrefab { Name = "army_camp_99", Def = new PrefabDef { Kind = PrefabKind.Poi }, X = -600, Z = -600, Y = 50 };
        var part = new PlacedPrefab { Name = "part_fake_99", Def = new PrefabDef { Kind = PrefabKind.Part }, X = -300, Z = -300, Y = 50 };
        w.Prefabs.AddRange(new[] { tileK, tileM, camp, part });
        var before = w.Prefabs.Select(p => $"{p.Name}|{p.X}|{p.Y}|{p.Z}|{p.Rotation}").ToList();
        string state = Fixture.State(w);

        var e = new WorldEdits(w);
        ConvertReport report = null;
        e.Run("convert", "", ed => report = ProjectConvert.Run(w, ed, pois, tiles, PartDef, 1));
        Check.Equal(1, report.Tiles, "tiles replaced");
        Check.Equal(1, report.WildBuildings, "wilderness buildings replaced");
        Check.Equal(1, report.PartsRemoved, "parts removed");
        var tile = w.Prefabs.Single(p => p.IsTile && p.X == 150 && p.Z == 0);
        Check.True(tile.Name.StartsWith("rwg_tile_rural_", StringComparison.OrdinalIgnoreCase) && ProjectConvert.Shape(tile.Name) == "t", "same district and shape: " + tile.Name);
        Check.True(Math.Abs(tile.GroundHeight - tileK.GroundHeight) < 0.6, $"floor of the neighbor tile: {tile.GroundHeight:0.00} vs {tileK.GroundHeight:0.00}");
        // exit at the shared edge: both tiles or neither
        w.TileSurface(tileK, out var exK);
        w.TileSurface(tile, out var exM);
        bool a = exK.Any(q => Math.Abs(q.X - 150) < 1 && Math.Abs(q.Z - 75) < 1), b = exM.Any(q => Math.Abs(q.X - 150) < 1 && Math.Abs(q.Z - 75) < 1);
        Check.Equal(a, b, "exit at the shared edge");
        Check.True(w.Prefabs.Any(p => p.Name.StartsWith("army_camp_", StringComparison.OrdinalIgnoreCase) && p.Def is { Found: true }), "army camp replaced by an army camp");
        Check.True(!w.Prefabs.Contains(part) && !w.Prefabs.Contains(camp) && !w.Prefabs.Contains(tileM), "old entries gone");
        var check = ProjectCheck.Run(w, PrefabCatalog.Load(game, w.Prefabs.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList()), () => pois);
        Check.Equal(0, check.Issues.Count, "nothing left to check");

        e.Undo();
        Check.True(before.SequenceEqual(w.Prefabs.Select(p => $"{p.Name}|{p.X}|{p.Y}|{p.Z}|{p.Rotation}")), "undo restores the buildings");
        Check.Equal(state, Fixture.State(w), "undo restores the map");
    }
}
