using System.Text;
using System.Windows;
using MapEditor.Map;

namespace YameeTests;

/// bridge kits of other mods (window "Bridge mods", 2026-11-24): analysis of the blocks, mixed choice, building with other sizes
static class BridgeKitTests
{
    /// a piece as .xml/.tts/.blocks.nim: deck (concrete) on layer deck − 1 over the whole piece, railings at both edges across, a dashed
    /// marking in the middle one layer higher, terrain blocks instead of the deck in the outer columns (like YAMEE_Bridges), an abutment
    /// below the outer columns; span along x or z
    static string Piece(string dir, string name, int span, int across, int height, int deck, int terrainCols, bool outerHigh, bool spanX, bool abutment)
    {
        Directory.CreateDirectory(dir);
        int sx = spanX ? span : across, sz = spanX ? across : span;
        var blocks = new uint[sx * height * sz];
        void Set(int s, int y, int a, uint id) { int x = spanX ? s : a, z = spanX ? a : s; blocks[x + y * sx + z * sx * height] = id; }
        for (int s = 0; s < span; s++)
            for (int a = 0; a < across; a++)
            {
                int fromOuter = outerHigh ? span - 1 - s : s;
                Set(s, deck - 1, a, fromOuter < terrainCols ? 2u : 1u);
                if (a == 0 || a == across - 1) Set(s, deck, a, 1);   // railing
                if (a == across / 2 && s % 3 == 0) Set(s, deck, a, 1);   // marking
                if (abutment && fromOuter < 6) for (int y = 0; y < deck - 2; y++) Set(s, y, a, 1);
            }
        string xml = Path.Combine(dir, name + ".xml");
        File.WriteAllText(xml, $"<prefab><property name=\"PrefabSize\" value=\"{sx}, {height}, {sz}\" /><property name=\"YOffset\" value=\"-1\" /></prefab>");
        using (var bw = new BinaryWriter(File.Create(Path.Combine(dir, name + ".blocks.nim"))))
        {
            bw.Write(1); bw.Write(3);
            foreach (var (id, n) in new[] { (0, "air"), (1, "concreteBlock"), (2, "terrDirt") }) { bw.Write(id); var b = Encoding.ASCII.GetBytes(n); bw.Write((byte)b.Length); bw.Write(b); }
        }
        using (var bw = new BinaryWriter(File.Create(Path.Combine(dir, name + ".tts"))))
        {
            bw.Write(Encoding.ASCII.GetBytes("tts\0")); bw.Write(18);
            bw.Write((short)sx); bw.Write((short)height); bw.Write((short)sz);
            foreach (var v in blocks) bw.Write(v);
        }
        return xml;
    }

    [Test("Bridge kits: the analysis finds width, lengths, deck, outer end and terrain columns of end and middle piece")]
    static void Analyze()
    {
        string dir = Path.Combine(Fixture.Temp, "bridgekits");
        // like YAMEE_Bridges: end 13 × 10, middle 10 × 10, deck layer 10, 3 terrain columns at the high end
        string end = Piece(dir, "kit_end", 13, 10, 18, 10, 3, true, true, true), tile = Piece(dir, "kit_tile", 10, 10, 18, 10, 0, true, true, false);
        var k = BridgeAnalyzer.Analyze(new BridgeKitDef { Id = "a", End = "kit_end", Tile = "kit_tile" }, end, tile);
        Check.True(k.Problem == null, "usable: " + k.Problem);
        Check.Equal(10, k.Width, "width");
        Check.Equal(13, k.EndLength, "end length");
        Check.Equal(10, k.TileLength, "middle length");
        Check.Equal(10, k.DeckLayer, "deck layer");
        Check.Equal(3, k.TerrainColumns, "terrain columns");
        Check.Equal(0, k.Turn, "outer end east at rotation 0");
        Check.Equal(4, k.Clearance, "clearance");
        Check.Equal(6, k.LandUnder, "land under the end (abutment 6 m)");
        Console.WriteLine("       " + k.Notes);
        // outer end at low x → turned by 180°
        string end2 = Piece(dir, "kit_end2", 13, 10, 18, 10, 3, false, true, true);
        Check.Equal(2, BridgeAnalyzer.Analyze(new BridgeKitDef { End = "e", Tile = "t" }, end2, tile).Turn, "outer end west");
        // like the bridge of the game: 14 wide across x, 9 long along z, end and middle equally large; outer end at high z
        string gEnd = Piece(dir, "g_end", 9, 14, 14, 13, 0, true, false, true), gTile = Piece(dir, "g_tile", 9, 14, 14, 13, 0, true, false, false);
        var g = BridgeAnalyzer.Analyze(new BridgeKitDef { End = "g_end", Tile = "g_tile" }, gEnd, gTile);
        Check.True(g.Problem == null, "game bridge usable: " + g.Problem);
        Check.Equal(14, g.Width, "game bridge width (railings across x)");
        Check.Equal(9, g.EndLength, "game bridge end length");
        Check.Equal(3, g.Turn, "span along z, outer end north → rotation 3");
        Check.Equal(1, g.TileTurn, "middle piece along z → rotation 1");
        // one piece, 40 m long
        string single = Piece(dir, "single", 40, 12, 12, 9, 0, true, true, false);
        var s = BridgeAnalyzer.Analyze(new BridgeKitDef { End = "single" }, single, null);
        Check.True(s.Problem == null, "single usable: " + s.Problem);
        Check.True(s.Single && s.EndLength == 40 && s.MinWater >= 20, $"single piece: {s.EndLength} m, water {s.MinWater} m");
        // different widths of end and middle piece
        string narrow = Piece(dir, "narrow", 10, 8, 18, 10, 0, true, true, false);
        Check.True(BridgeAnalyzer.Analyze(new BridgeKitDef { End = "e", Tile = "t" }, end, narrow).Problem != null, "not equally wide → problem");
    }

    static WorldData RiverWorld(out WorldEdits e, out int level, int widthMin = 20, int widthMax = 22, WorldData world = null, int half = 300)
    {
        var w = world ?? Fixture.LoadCopy();
        e = new WorldEdits(w);
        Point a = new(-half, -100), b = new(half, -100);
        int lv = (int)Math.Floor(Math.Min(w.HeightAt(-half, -100), w.HeightAt(half, -100))) - 2;
        List<Point> Circle(Point c) => Enumerable.Range(0, 36).Select(k => new Point(c.X + 60 * Math.Cos(k * Math.PI / 18), c.Y + 60 * Math.Sin(k * Math.PI / 18))).ToList();
        e.Run("lake A", "lake A", ed => Lake.Place(w, ed, new LakeSpec { Level = lv, Bumps = 0, Outline = Circle(a) }));
        e.Run("lake B", "lake B", ed => Lake.Place(w, ed, new LakeSpec { Level = lv, Bumps = 0, Outline = Circle(b) }));
        e.Run("river", "river", ed => River.Build(w, ed, new RiverSpec { ClickA = a, ClickB = b, Line = new() { a, new(0, -100), b }, WidthMin = widthMin, WidthMax = widthMax }));
        level = lv;
        return w;
    }

    static BridgeKitDef Kit(string id, int width, int endLength, int tileLength, int clearance, int deck, bool single = false)
    {
        var k = new BridgeKitDef { Id = id, Label = id, End = id + "_end", Tile = single ? null : id + "_tile", Active = true, Width = width, EndLength = endLength,
            TileLength = tileLength, Clearance = clearance, DeckLayer = deck, LandUnder = single ? 8 : Math.Max(1, endLength - 7), TerrainColumns = 0 };
        k.EndDef = new PrefabDef { SizeX = endLength, SizeY = deck + 8, SizeZ = width, Found = true, Kind = PrefabKind.Part };
        k.TileDef = single ? null : new PrefabDef { SizeX = tileLength, SizeY = deck + 8, SizeZ = width, Found = true, Kind = PrefabKind.Part };
        return k;
    }

    [Test("Bridge kits: a 14 m wide kit builds its pieces, deck and 14 m approaches; a bridge of one piece is set once; undo exact")]
    static void BuildOtherKits()
    {
        var w = RiverWorld(out var e, out int level);
        string before = Fixture.State(w);
        var wide = new BridgeKits(new[] { Kit("wide", 14, 9, 9, 6, 13) });
        var site = wide.Find(w, 10, -100, false);
        Check.True(site.Problem == null, "possible: " + site.Problem);
        Check.Equal("wide", site.Kit, "kit");
        Check.Equal(level + 6, site.Deck, "deck = level + clearance");
        var (ed0, td0) = wide.Defs(site);
        int count = w.Prefabs.Count;
        e.Run("wide bridge", "", ed => Bridge.Build(w, ed, site, ed0, td0));
        Check.Equal(count + site.Tiles + 2, w.Prefabs.Count, "pieces");
        var added = w.Prefabs.Skip(count).ToList();
        Check.True(added.All(p => p.Y == site.Deck - 13), "pieces at deck − deck layer");
        Check.True(added.Count(p => p.Name == "wide_tile") == site.Tiles, "middle pieces");
        // the approach is 14 m wide: road on the outer row of the bridge, none beside it
        int sOut = site.S0 - 5;
        int Road(int t) { int x = site.AlongX ? sOut : t, z = site.AlongX ? t : sOut; return w.RoadAt(x, z); }
        Check.True(Road(site.T0) != 0 && Road(site.T0 + 13) != 0, "approach over the whole width");
        Check.True(Road(site.T0 + 6) == WorldData.Asphalt, "asphalt in the middle");
        e.Undo();
        Check.Equal(before, Fixture.State(w), "undo exact");
        // one piece of 40 m: the river (20–22 m) fits
        var one = new BridgeKits(new[] { Kit("one", 12, 40, 0, 4, 9, single: true) });
        var s1 = one.Find(w, 10, -100, false);
        Check.True(s1.Problem == null, "single possible: " + s1.Problem);
        Check.True(s1.Single && s1.Tiles == 0 && s1.Length == 40, "single site");
        count = w.Prefabs.Count;
        var (e1, t1) = one.Defs(s1);
        e.Run("single bridge", "", ed => Bridge.Build(w, ed, s1, e1, t1));
        Check.Equal(count + 1, w.Prefabs.Count, "one piece");
        e.Undo();
        Check.Equal(before, Fixture.State(w), "undo exact (single)");
    }

    [Test("Bridge kits: several kits are mixed over the map, only kits for the surface are taken, YAMEE_Bridges values unchanged")]
    static void Mix()
    {
        var a = Kit("a", 10, 13, 10, 4, 10); var b = Kit("b", 12, 13, 10, 4, 10); var g = Kit("g", 10, 13, 10, 4, 10);
        a.Gravel = false; b.Gravel = false; g.Asphalt = false;
        var kits = new BridgeKits(new[] { a, b, g });
        var first = new Dictionary<string, int>();
        for (int x = -2000; x < 2000; x += 64)
            for (int z = -2000; z < 2000; z += 64)
            {
                string id = kits.Order(false, x, z).First().Id;
                first[id] = first.GetValueOrDefault(id) + 1;
            }
        Check.True(!first.ContainsKey("g"), "gravel kit not used for asphalt");
        Check.True(first.GetValueOrDefault("a") > 300 && first.GetValueOrDefault("b") > 300, $"both asphalt kits used: a {first.GetValueOrDefault("a")}, b {first.GetValueOrDefault("b")}");
        Check.Equal("g", kits.Order(true, 0, 0).Single().Id, "gravel: only the gravel kit");
        Check.Equal(kits.Order(false, 100, 100).First().Id, kits.Order(false, 100, 100).First().Id, "same place, same kit");
        // the old way (YAMEE_Bridges only) gives the same site as before
        var w = RiverWorld(out _, out _);
        var old = Bridge.Find(w, 10, -100);
        var yk = BridgeKitDef.Yamee(); yk.EndDef = a.EndDef; yk.TileDef = a.TileDef;
        var now = new BridgeKits(new[] { yk }).Find(w, 10, -100, false);
        Check.True(old.Problem == null && now.Problem == null, "both possible");
        Check.True(old.S0 == now.S0 && old.T0 == now.T0 && old.Tiles == now.Tiles && old.Deck == now.Deck && old.Length == now.Length && old.WaterWidth == now.WaterWidth,
            $"same place: {old.S0}/{old.T0}/{old.Tiles} – {now.S0}/{now.T0}/{now.Tiles}");
    }

    [Test("Bridge kits: the analysis of the real pieces (YAMEE_Bridges, the bridges of the game) – needs the game folder")]
    static void RealPieces()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? MapEditor.GameLocator.Find()?.Folder;
        if (game == null) Check.Skip("game folder not found");
        var files = PrefabCatalog.AllFiles(game);
        bool any = false;
        foreach (var (end, tile) in new[] { (Bridge.EndName, Bridge.TileName), ("rwg_bridge_end_01", "rwg_bridge_tile_01"), ("part_bridge_10x10_end_01", "part_bridge_10x10_tile_01"),
                     ("bridge_concrete1", null), ("bridge_asphalt1", null), ("bridge_asphalt_broken", null), ("bridge_wood1", null), ("yamee_bridge_wood1", null), ("yamee_bridge_asphalt_broken", null), ("yamee_rwg_bridge_end_01", "rwg_bridge_tile_01"),
                     ("yamee_bridge_concrete_1", null), ("yamee_bridge_concrete_2", null), ("yamee_bridge_asphalt1", null), ("yamee_bridge_wood2", null) })
        {
            if (!files.ContainsKey(end) || (tile != null && !files.ContainsKey(tile))) continue;
            any = true;
            var k = BridgeAnalyzer.Analyze(new BridgeKitDef { End = end, Tile = tile }, files[end], tile == null ? null : files[tile]);
            Console.WriteLine($"       {end}{(tile != null ? " + " + tile : "")}: {(k.Problem ?? "ok")} – width {k.Width}, end {k.EndLength}, middle {k.TileLength}, deck {k.DeckLayer}, " +
                              $"road inset {k.RoadInset}, terrain {k.TerrainColumns}, turn {k.Turn}/{k.TileTurn}, land {k.LandUnder}, clearance {k.Clearance}, water {k.MinWater}–{k.MaxWater}");
            if (Environment.GetEnvironmentVariable("YAMEE_TEST_BRIDGE_DUMP") != null)
            {
                // cross section in the middle of the span of the deck piece: layers deck − 2 … deck + 4, # = solid, T = terrain, . = air
                var p = PieceBlocks.Read(files[tile ?? end]);
                bool alongX = k.Turn % 2 == 0;
                int span = alongX ? p.SX : p.SZ, across = alongX ? p.SZ : p.SX;
                foreach (int s in Environment.GetEnvironmentVariable("YAMEE_TEST_BRIDGE_DUMP") == "all" ? Enumerable.Range(0, span) : new[] { 2, span / 3, span / 2 })
                {
                Console.WriteLine($"         row {s}:");
                // YAMEE_TEST_BRIDGE_DUMP_PIECE = only this piece, all layers
                string onlyPiece = Environment.GetEnvironmentVariable("YAMEE_TEST_BRIDGE_DUMP_PIECE");
                if (onlyPiece != null && onlyPiece != end) continue;
                for (int y = onlyPiece != null ? p.SY - 1 : Math.Min(p.SY - 1, k.DeckLayer + 4); y >= (onlyPiece != null ? 0 : Math.Max(0, k.DeckLayer - 2)); y--)
                {
                    var line = new System.Text.StringBuilder($"         {y,3} ");
                    for (int a = 0; a < across; a++)
                    {
                        int x = alongX ? s : a, z = alongX ? a : s;
                        line.Append(p.Terrain(x, y, z) ? 'T' : p.Thin(x, y, z) ? 'h' : p.Solid(x, y, z) ? '#' : '.');
                    }
                    Console.WriteLine(line);
                }
                }
            }
            // the road reaches from railing to railing: the planks outside the walls of the wooden bridge are not road (the user 2026-11-24)
            if (end == "bridge_wood1" || end == BridgeKitDef.WoodName) Check.Equal(1, k.RoadInset ?? -1, end + " road 1 m narrower on each side");
            // asphalt_broken: the 1-block curb beside the truss is no railing, the road reaches from truss to truss (the user 2026-11-25)
            if (end == "yamee_bridge_asphalt_broken") Check.Equal(1, k.RoadInset ?? -1, end + " road 11 m (1 m narrower than the piece on each side)");
            if (end == Bridge.EndName || end == "rwg_bridge_end_01" || end == BridgeKitDef.RwgEndName) Check.Equal(0, k.RoadInset ?? -1, end + " road over the whole width");
            // one piece with road terrain at its ends: the deck is one layer above it (the most common top gave 14 for bridge_asphalt_broken: the bridge
            // stood 1 m too low in Buzovi-Land, the user 2026-11-25)
            if (end == "bridge_asphalt_broken") Check.Equal(13, k.DeckLayer, end + " deck layer (terrAsphalt on layer 12 at the ends)");
            if (end == "bridge_asphalt1") Check.Equal(10, k.DeckLayer, end + " deck layer (terrAsphalt on layer 9 at the ends)");
            // the game copies the air below layer −YOffset (and with CopyAirBlocks True everywhere) into the ground: our pieces must not (Brueckentest44)
            if (end.StartsWith("yamee_", StringComparison.Ordinal))
                Check.True(!BridgeAnalyzer.AirWipes(files[end]) && (tile == null || !BridgeAnalyzer.AirWipes(files[tile])), end + ": YOffset −1 or 0 and CopyAirBlocks False");
            if (end == "bridge_asphalt_broken" || end == "bridge_wood1") Check.True(BridgeAnalyzer.AirWipes(files[end]), end + ": the original copies air into the ground");
            if (end == BridgeKitDef.RwgEndName)
            {
                // rebuilt end piece: asphalt terrain on the deck layer at the outer end (z 0, where the guard rails end)
                Check.Equal(13, k.DeckLayer, "rwg deck layer (full concrete blocks on layer 12)");
                Check.Equal(2, k.TerrainColumns, "rwg terrain columns (2 rows of terrAsphalt)");
                Check.Equal(1, k.Turn, "rwg outer end at low z → rotation 1");
            }
            if (end == Bridge.EndName)
            {
                Check.True(k.Problem == null, "YAMEE_Bridges usable");
                Check.Equal(10, k.Width, "YAMEE_Bridges width");
                Check.Equal(10, k.DeckLayer, "YAMEE_Bridges deck layer");
                Check.Equal(0, k.Turn, "YAMEE_Bridges outer end east");
                Check.Equal(BridgeAnalyzer.TerrainColumns(files[end]), k.TerrainColumns, "YAMEE_Bridges terrain columns");
            }
        }
        if (!any) Check.Skip("no bridge pieces in the game folder");
    }
    [Test("Bridge kits: the rebuilt wooden bridge yamee_bridge_wood1 (one piece, solid land part) is found and built on a narrow river; YAMEE_TEST_WOOD_OUT writes a test world – needs the game folder")]
    static void WoodBridge()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? MapEditor.GameLocator.Find()?.Folder;
        if (game == null) Check.Skip("game folder not found");
        var files = PrefabCatalog.AllFiles(game);
        if (!files.ContainsKey(BridgeKitDef.WoodName)) Check.Skip("yamee_bridge_wood1 is not in the Mods folder");
        PrefabDef Describe(string n)
        {
            if (!files.TryGetValue(n, out var f)) return null;
            var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Part;
            return d.Found ? d : null;
        }
        var wood = BridgeKitStore.All(files, Describe).FirstOrDefault(k => k.Id == BridgeKitDef.WoodId);
        Check.True(wood != null && wood.Usable, "wooden bridge kit found: " + wood?.Problem);
        Check.True(wood.Single && wood.EndLength == 39 && wood.Width == 11 && wood.DeckLayer == 9 && wood.LandUnder == 4,
            $"values: {wood.EndLength} × {wood.Width}, deck {wood.DeckLayer}, land {wood.LandUnder}");
        Check.Equal(4, wood.TerrainColumns, "terrain columns = whole land part (4 m)");
        Check.Equal(31, wood.MinWater, "water up to 31 m");
        wood.Active = true;
        string outDir = Environment.GetEnvironmentVariable("YAMEE_TEST_WOOD_OUT");
        WorldData big = null;
        if (outDir != null)
        {
            // the game test gets a 4 km world (like the earlier bridge test worlds), named like the output folder
            string dir = Path.Combine(Fixture.Temp, "wood", Path.GetFileName(outDir));
            var spec = Fixture.Spec("Holzbruecke"); spec.Name = Path.GetFileName(outDir); spec.Size = 4096;
            var profile = MapEditor.GameLocator.ProfileOf(game);
            new WorldGenerator(spec).Write(dir, MapEditor.GameLocator.WorldsFolderFor(profile), new Progress<string>(), profile?.Version);
            if (profile?.Version != null) MapEditor.ProjectInfo.Set(dir, profile);
            big = WorldData.Load(dir, null, new Progress<string>());
        }
        var w = RiverWorld(out var e, out _, 9, 10, big);
        string before = Fixture.State(w);
        var kits = new BridgeKits(new[] { wood });
        var site = kits.Find(w, 90, -100, false);
        Check.True(site.Problem == null, "possible: " + site.Problem);
        int count = w.Prefabs.Count;
        var (ed, td) = kits.Defs(site);
        e.Run("wooden bridge", "", q => Bridge.Build(w, q, site, ed, td));
        Check.Equal(count + 1, w.Prefabs.Count, "one piece");
        var p = w.Prefabs[^1];
        Check.Equal(BridgeKitDef.WoodName, p.Name, "piece");
        Check.Equal(site.Deck - 9, p.Y, "piece at deck − 9 (YOffset −9)");
        double H(int s) { int x = site.AlongX ? s : site.T0 + 5, z = site.AlongX ? site.T0 + 5 : s; return w.HeightAt(x, z); }
        Check.True(Math.Abs(H(site.S0 - 5) - (site.Deck - 1 + 0.18)) < 0.01, $"approach at deck − 0.82: {H(site.S0 - 5):0.00}");
        Check.True(Math.Abs(H(site.S0 + 2) - (site.Deck - 1.51)) < 0.01, $"under the land part deck − 1.51: {H(site.S0 + 2):0.00}");
        // road 9 m: the outer meter on each side gets no surface (planks outside the walls)
        Check.Equal(1, site.RoadInset, "road inset of the wooden bridge");
        byte R(int t) { int s = site.S0 - 5, x = site.AlongX ? s : t, z = site.AlongX ? t : s; return w.RoadMask[w.Index(x, z)]; }
        Check.True(R(site.T0) == 0 && R(site.T0 + 10) == 0, "no surface on the outer meter");
        Check.True(R(site.T0 + 1) != 0 && R(site.T0 + 9) != 0 && R(site.T0 + 5) != 0, "road 9 m wide");
        Console.WriteLine($"       wooden bridge at {site.S0}/{site.T0} ({(site.AlongX ? "west–east" : "south–north")}), deck {site.Deck}, river {site.RiverWidth:0} m, water under it {site.WaterWidth} m");
        if (outDir == null)
        {
            e.Undo();
            Check.Equal(before, Fixture.State(w), "undo exact");
            return;
        }
        // test world for the game: the original bridge_wood1 upstream for comparison (same rules, but its underside is terrainFillerAdaptive)
        var orig = wood.Clone(); orig.Id = "orig_wood1"; orig.End = "bridge_wood1"; orig.EndDef = Describe("bridge_wood1");
        var origKits = new BridgeKits(new[] { orig });
        var s2 = origKits.Find(w, -90, -100, false);
        Check.True(s2.Problem == null, "original possible: " + s2.Problem);
        var (oe, ot) = origKits.Defs(s2);
        e.Run("original wooden bridge", "", q => Bridge.Build(w, q, s2, oe, ot));
        e.Save(null, new Progress<string>(), out _);
        // spawn point on the approach of the rebuilt bridge, looking along it
        w.Spawns.Clear();
        int sp = site.S0 - 15, tm = site.T0 + 5;
        double sx = site.AlongX ? sp : tm, sz = site.AlongX ? tm : sp;
        w.Spawns.Add(new SpawnPoint { X = sx + 0.5, Y = site.Deck + 1, Z = sz + 0.5, Heading = site.AlongX ? 90 : 0 });
        w.WritePlaySpawns();
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(w.Folder)) File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)), true);
        File.WriteAllText(Path.Combine(outDir, "bruecken.txt"),
            $"yamee_bridge_wood1 (Mod-Kopie): S0 {site.S0}, T0 {site.T0}, {(site.AlongX ? "West-Ost" : "Sued-Nord")}, Fahrbahn {site.Deck}\r\n" +
            $"bridge_wood1 (Original): S0 {s2.S0}, T0 {s2.T0}, {(s2.AlongX ? "West-Ost" : "Sued-Nord")}, Fahrbahn {s2.Deck}\r\n" +
            $"Spawnpunkt {sx + 0.5} / {site.Deck + 1} / {sz + 0.5}\r\n");
        Console.WriteLine($"       test world written to {outDir}; original at {s2.S0}/{s2.T0}");
    }

    [Test("Bridge kits: the rebuilt one-piece bridges of the game (yamee_bridge_concrete_1 … _wood2, tools/make_game_bridge) are analysed after the rebuild and built over a river – needs the game folder")]
    static void RebuiltGameBridges()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? MapEditor.GameLocator.Find()?.Folder;
        if (game == null) Check.Skip("game folder not found");
        var files = PrefabCatalog.AllFiles(game);
        PrefabDef Describe(string n)
        {
            if (!files.TryGetValue(n, out var f)) return null;
            var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Part;
            return d.Found ? d : null;
        }
        var all = BridgeKitStore.All(files, Describe);
        // values from the rebuild (deck = terrain surface at the end + 1, land = solid filler down to layer 1)
        var expect = new Dictionary<string, (int Width, int Length, int Deck, int Land)>
        {
            ["yamee_bridge_concrete_1"] = (13, 39, 7, 9), ["yamee_bridge_concrete_2"] = (13, 45, 5, 8), ["yamee_bridge_asphalt1"] = (15, 43, 10, 5),   // concrete_2 and asphalt1 as edited by the user 2026-11-25

            ["yamee_bridge_asphalt_broken"] = (13, 44, 13, 7), ["yamee_bridge_wood2"] = (9, 31, 7, 5),
        };
        int found = 0;
        foreach (var (piece, v) in expect)
        {
            var k = all.FirstOrDefault(q => q.Id == piece);
            if (k == null || PieceBlocks.Read(files.GetValueOrDefault(piece)) == null) continue;   // not there or incomplete in the mod (e.g. .blocks.nim missing)
            found++;
            Console.WriteLine($"       {piece}: {(k.Problem ?? "ok")} – width {k.Width}, road {k.RoadWidth}, length {k.EndLength}, deck {k.DeckLayer}, land {k.LandUnder}, terrain {k.TerrainColumns}, turn {k.Turn}, clearance {k.Clearance}, water {k.MinWater}");
            Check.True(k.Usable, piece + " usable: " + k.Problem);
            // on at first except the broken bridge (the user 2026-11-25; without a bridges.json)
            Check.True(k.Single && k.Builtin, piece + ": one piece, built in");
            if (!File.Exists(BridgeKitStore.FilePath)) Check.Equal(piece != "yamee_bridge_asphalt_broken", k.Active, piece + ": on at first, the broken bridge off");
            Check.Equal(v.Width, k.Width, piece + " width");
            Check.Equal(v.Length, k.EndLength, piece + " length");
            Check.Equal(v.Deck, k.DeckLayer, piece + " deck layer");
            Check.Equal(v.Land, k.LandUnder, piece + " land part");
            Check.Equal(k.LandUnder, k.TerrainColumns, piece + ": ground lowered under the whole land part");
            Check.Equal(piece == "yamee_bridge_wood2", k.Gravel && !k.Asphalt, piece + ": wooden bridge for gravel roads only");
        }
        if (found == 0) Check.Skip("the rebuilt game bridges are not in the Mods folder");
        // each one over a river, undo exact
        var w = RiverWorld(out var e, out _, 9, 10);
        string before = Fixture.State(w);
        int runs = 0;
        foreach (var k0 in all.Where(q => expect.ContainsKey(q.Id) && q.Usable))
        {
            var k = k0.Clone(); k.Active = true;
            var kits = new BridgeKits(new[] { k });
            var site = kits.Find(w, -100, -100, !k.Asphalt);
            Check.True(site.Problem == null, k.Id + ": " + site.Problem);
            var (ed, td) = kits.Defs(site);
            int count = w.Prefabs.Count;
            e.Run(k.Label, "", q => Bridge.Build(w, q, site, ed, td));
            Check.Equal(count + 1, w.Prefabs.Count, k.Id + ": one piece");
            Check.Equal(site.Deck - k.DeckLayer, w.Prefabs[^1].Y, k.Id + ": piece at deck − deck layer");
            double H(int s) { int xx = site.AlongX ? s : (int)site.MidT, zz = site.AlongX ? (int)site.MidT : s; return w.HeightAt(xx, zz); }
            Check.True(Math.Abs(H(site.S0 + 1) - (site.Deck - 1.51)) < 0.01, $"{k.Id}: ground under the land part at deck − 1.51: {H(site.S0 + 1):0.00}");
            Check.Equal(k.MinWater, site.WaterWidth, k.Id + ": water under the bridge");
            // bridge_concrete_2: 2 m on each side without own terrain (Brueckentest42: the plate stood out over the bank) – ground there like the approach
            // (since the user's concrete_2 of 2026-11-25, 13 m wide with terrain over the whole width: none in any piece)
            Check.Equal(0, site.SideInset, k.Id + ": side columns without own terrain");
            // river bed 1.5 m above the bottom of the piece (YOffset −1: layer 0 is copied air); pieces with YOffset 0 copy no air: down to the
            // bottom, concrete_1 0.5 m (the user 2026-11-25 after Brueckentest48/49)
            bool noAir = BridgeAnalyzer.NoAirCopied(files[k.End]);
            Check.Equal(!noAir ? 1.5 : 0, site.BedAbove, k.Id + ": river bed above the bottom of the piece");
            if (site.SideInset > 0)
            {
                int sx = site.AlongX ? site.S0 + 1 : site.T0, sz = site.AlongX ? site.T0 : site.S0 + 1;
                Check.True(Math.Abs(w.HeightAt(sx, sz) - (site.Deck - 1 + site.ApproachLift)) < 0.01, $"{k.Id}: ground beside the land part like the approach: {w.HeightAt(sx, sz):0.00}");
            }
            // layer 0 of the piece is air the game copies into the ground: the river bed lies at least 1.5 m above the bottom of the piece
            double bottom = site.Deck - k.DeckLayer, lowest = double.MaxValue;
            for (int s = site.S0 + site.LandUnder; s < site.S0 + site.Length - site.LandUnder; s++) lowest = Math.Min(lowest, H(s));
            Check.True(lowest >= bottom + site.BedAbove - 0.01, $"{k.Id}: bed {lowest:0.00} at least {site.BedAbove} m above the bottom of the piece {bottom} (layer 0 is air copied into the ground)");
            // pieces with YOffset 0: the bed goes down to the lowest point of the piece, also deeper than the river's own 4.5 m (the user 2026-11-25)
            if (noAir) Check.True(Math.Abs(lowest - bottom) < 0.01, $"{k.Id}: bed {lowest:0.00} down to the bottom of the piece {bottom}");
            e.Undo();
            Check.Equal(before, Fixture.State(w), k.Id + ": undo exact");
            runs++;
        }
        Check.Equal(found, runs, "every rebuilt bridge built");
    }

    [Test("Bridge kits: test world with every bridge kit side by side over one river (YAMEE_Bridges, bridge of the game, wooden bridge); YAMEE_TEST_BRIDGES_OUT writes it – needs the game folder")]
    static void AllKitsWorld()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? MapEditor.GameLocator.Find()?.Folder;
        if (game == null) Check.Skip("game folder not found");
        var files = PrefabCatalog.AllFiles(game);
        PrefabDef Describe(string n)
        {
            if (!files.TryGetValue(n, out var f)) return null;
            var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Part;
            return d.Found ? d : null;
        }
        var all = BridgeKitStore.All(files, Describe).Where(k => k.Usable).ToList();
        // YAMEE_TEST_KITS = kit ids (comma list): only these kits in the test world (the user 2026-11-25: paused bridges with old xml values stay out)
        string only = Environment.GetEnvironmentVariable("YAMEE_TEST_KITS");
        if (only != null) { var ids = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(); all = all.Where(k => ids.Contains(k.Id)).ToList(); }
        // the rebuilt rwg kit lowers the ground under its whole land part (Brueckentest41)
        var rwg = all.FirstOrDefault(k => k.Id == BridgeKitDef.RwgId);
        if (rwg != null) Check.Equal(rwg.LandUnder, rwg.TerrainColumns, "rwg kit: ground lowered under the whole land part");
        // YAMEE_TEST_RWG_VARIANTS = end pieces (comma list): only the rebuilt rwg bridge, once with each end piece (test pieces of the mod,
        // e.g. yamee_rwg_bridge_test_j1 … with less density at the joint, the user 2026-11-24 after Brueckentest39)
        string variants = Environment.GetEnvironmentVariable("YAMEE_TEST_RWG_VARIANTS");
        if (variants != null && rwg != null)
        {
            all = variants.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n =>
            {
                // "name@k": k terrain columns (ground lowered to deck − 1.51 under the outer k rows; Brueckentest40: under the first concrete row the
                // ground at deck − 1 leaves only half air above the −100 concrete, the terrain pokes up there)
                var parts = n.Split('@'); n = parts[0];
                var v = rwg.Clone(); v.Id = "rwg_" + n; v.Label = n; v.End = n;
                if (parts.Length > 1) { v.TerrainColumns = int.Parse(parts[1]); v.Id += "_" + parts[1]; v.Label += " @" + parts[1]; }
                BridgeKitStore.Resolve(v, files, Describe);
                Check.True(v.Found, n + " not found");
                return v;
            }).ToList();
        }
        if (all.Count == 0) Check.Skip("no bridge kit in the game folder");
        string outDir = Environment.GetEnvironmentVariable("YAMEE_TEST_BRIDGES_OUT");
        WorldData big = null;
        if (outDir != null)
        {
            string dir = Path.Combine(Fixture.Temp, "allkits", Path.GetFileName(outDir));
            var spec = Fixture.Spec("Brueckentest"); spec.Name = Path.GetFileName(outDir); spec.Size = 4096;
            var profile = MapEditor.GameLocator.ProfileOf(game);
            new WorldGenerator(spec).Write(dir, MapEditor.GameLocator.WorldsFolderFor(profile), new Progress<string>(), profile?.Version);
            if (profile?.Version != null) MapEditor.ProjectInfo.Set(dir, profile);
            big = WorldData.Load(dir, null, new Progress<string>());
        }
        // one river west–east (about 13 m wide, so that also the wooden bridge with 17 m of water fits), the kits 100 m apart (five bridges between the lakes).
        // YAMEE_TEST_BRIDGE_SPACING = distance between the bridges (m): the river gets as long as needed – 130 m keeps the work areas of the
        // bridges (60 m on each side) apart, with 62 m their transitions overlapped and made extra edges
        int spacing = int.TryParse(Environment.GetEnvironmentVariable("YAMEE_TEST_BRIDGE_SPACING"), out int sp0) && sp0 > 0 ? sp0 : 0;
        int half = spacing > 0 ? (all.Count - 1) * spacing / 2 + 140 : 300;
        var w = RiverWorld(out var e, out _, 9, 10, big, half);
        string before = Fixture.State(w);
        var lines = new List<string>();
        var built = new List<(BridgeKitDef Kit, BridgeSite Site)>();
        var edges = new List<(string Id, double Step)>();
        // the river between the lakes runs from x -240 to 240: up to five kits 100 m apart, more kits closer together (nine since the rebuilt game bridges)
        double step = all.Count <= 5 ? 100 : Math.Floor(440.0 / (all.Count - 1)), x = all.Count <= 5 ? -200 : -220;
        // YAMEE_TEST_BRIDGE_X = x of the first bridge (e.g. 0 for one bridge in the middle of the river, far from the lakes and other bridges)
        if (double.TryParse(Environment.GetEnvironmentVariable("YAMEE_TEST_BRIDGE_X"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double firstX)) x = firstX;
        if (spacing > 0) { step = spacing; x = -(all.Count - 1) * spacing / 2.0; }
        foreach (var kit in all)
        {
            var k = kit.Clone(); k.Active = true;
            var kits = new BridgeKits(new[] { k });
            var site = kits.Find(w, x, -100, k.Id == BridgeKitDef.WoodId || !k.Asphalt);
            Check.True(site.Problem == null, $"{k.Label} at {x}: {site.Problem}");
            site.Gravel = k.Id == BridgeKitDef.WoodId || !k.Asphalt;   // the wooden bridges get gravel approaches, the others asphalt
            var (ed, td) = kits.Defs(site);
            int count = w.Prefabs.Count;
            var pre = (ushort[])w.Heights.Clone();
            e.Run(k.Label, "", q => Bridge.Build(w, q, site, ed, td));
            Check.Equal(count + (site.Single ? 1 : site.Tiles + 2), w.Prefabs.Count, k.Label + ": pieces");
            Check.True(w.Prefabs.Skip(count).All(p => p.Y == site.Deck - site.DeckLayer), k.Label + ": pieces at deck − deck layer");
            double H(int s) { int xx = site.AlongX ? s : (int)site.MidT, zz = site.AlongX ? (int)site.MidT : s; return w.HeightAt(xx, zz); }
            double approach = H(site.S0 - 5);
            Check.True(Math.Abs(approach - (site.Deck - 1 + site.ApproachLift)) < 0.01, $"{k.Label}: approach {approach:0.00}");
            built.Add((k, site));
            // edges in the ground around the bridge (outside the piece): biggest step between neighbouring meters, and where
            {
                var (worst, worstWas, wx, wz, bankTop, bx, bz) = Edges(w, pre, site);
                Console.WriteLine($"       {k.Id}: biggest step around the bridge {worst:0.00} m (before {worstWas:0.00} m) at {wx}/{wz}, bank at the water up to {bankTop:0.00} m above the level at {bx}/{bz}");
                // YAMEE_TEST_EDGE_DUMP = kit id: heights around that bridge (every 2 m, rows = z from north to south)
                if (Environment.GetEnvironmentVariable("YAMEE_TEST_EDGE_DUMP") == k.Id)
                    for (int zz = site.S0 + site.Length + 50; zz >= site.S0 - 50; zz -= 2)
                    {
                        var line = new System.Text.StringBuilder($"{zz,5} ");
                        for (int xx = site.T0 - 44; xx <= site.T0 + site.Width + 44; xx += 2)
                        {
                            int i = w.Index(xx, zz);
                            line.Append($"{w.HeightAt(xx, zz),3:0}{(w.WaterMask[i] != 0 ? '~' : ' ')}");
                        }
                        Console.WriteLine(line);
                    }
                edges.Add((k.Id, worst));
            }
            string piece = site.Single ? site.EndPiece : site.EndPiece + " + " + site.Tiles + " × " + site.TilePiece;
            lines.Add($"{k.Label}: {piece}; x {(site.AlongX ? site.S0 : site.T0)}…{(site.AlongX ? site.S0 + site.Length : site.T0 + site.Width)}, " +
                      $"z {(site.AlongX ? site.T0 : site.S0)}…{(site.AlongX ? site.T0 + site.Width : site.S0 + site.Length)}, {(site.AlongX ? "West-Ost" : "Sued-Nord")}, " +
                      $"Fahrbahn {site.Deck}, Wasser {site.Level}, Breite {site.Width} m, Wasser darunter {site.WaterWidth} m, Zufahrten {(site.Gravel ? "Schotter" : "Asphalt")}");
            Console.WriteLine("       " + lines[^1]);
            x += step;
        }
        // YAMEE_TEST_GORGE = depth below the water level (m): the river and its lakes become a dry gorge with steep walls under the bridges
        // (the user 2026-11-25: "all bridges over gorges too – let's see how it looks"); only for the test world
        int steps = built.Count;
        if (double.TryParse(Environment.GetEnvironmentVariable("YAMEE_TEST_GORGE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gorge) && gorge > 0)
        {
            int L = built[0].Site.Level, drained = 0;
            ushort floor = (ushort)Math.Round((L - gorge) * 256);
            e.Run("Gorge", "", q =>
            {
                q.SkipWaterSync();
                for (int i = 0; i < w.WaterMask.Length; i++)
                {
                    if (w.WaterMask[i] != L) continue;
                    q.SetWater(i, 0);
                    if (w.Heights[i] > floor) q.SetHeight(i, floor);
                    drained++;
                }
            });
            steps++;
            Check.True(drained > 0, "gorge: river drained");
            lines.Add($"Schlucht: Fluss und Seen trocken, Boden {L - gorge:0.#} (Spiegel {L} − {gorge:0.#} m), {drained} m²");
            Console.WriteLine("       " + lines[^1]);
        }
        if (outDir == null)
        {
            for (int i = 0; i < steps; i++) e.Undo();
            Check.Equal(before, Fixture.State(w), "undo exact");
            return;
        }
        e.Save(null, new Progress<string>(), out _);
        // spawn point on the south approach of the middle bridge, looking north (all bridges in sight)
        var (_, mid) = built[built.Count / 2];
        w.Spawns.Clear();
        double sx = mid.AlongX ? mid.S0 - 15 : mid.MidT, sz = mid.AlongX ? mid.MidT : mid.S0 - 15;
        w.Spawns.Add(new SpawnPoint { X = Math.Floor(sx) + 0.5, Y = mid.Deck + 1, Z = Math.Floor(sz) + 0.5, Heading = mid.AlongX ? 90 : 0 });
        w.WritePlaySpawns();
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(w.Folder)) File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)), true);
        lines.Add($"Spawnpunkt {Math.Floor(sx) + 0.5} / {mid.Deck + 1} / {Math.Floor(sz) + 0.5} (Blick {(mid.AlongX ? "Ost" : "Nord")})");
        File.WriteAllText(Path.Combine(outDir, "bruecken.txt"), string.Join("\r\n", lines) + "\r\n");
        Console.WriteLine($"       test world written to {outDir}");
    }

    /// edges the build made in the ground around a bridge (outside the piece): the biggest new step between neighbouring meters (only where a
    /// cell changed and steeper than before; the shoreline is a step by rule and is left out) and the highest dry bank right at the water
    static (double Step, double Was, int X, int Z, double Bank, int BX, int BZ) Edges(WorldData w, ushort[] pre, BridgeSite site)
    {
        double worst = 0, worstWas = 0, bankTop = 0; int wx = 0, wz = 0, bx = 0, bz = 0;
        for (int s = site.S0 - 45; s <= site.S0 + site.Length + 45; s++)
            for (int t = site.T0 - 35; t <= site.T0 + site.Width + 35; t++)
            {
                if (s >= site.S0 - 1 && s <= site.S0 + site.Length && t >= site.T0 - 1 && t <= site.T0 + site.Width) continue;
                int xx = site.AlongX ? s : t, zz = site.AlongX ? t : s;
                int i0 = w.Index(xx, zz), iE = w.Index(xx + 1, zz), iN = w.Index(xx, zz + 1);
                foreach (int j in new[] { iE, iN })
                {
                    if (pre[i0] == w.Heights[i0] && pre[j] == w.Heights[j]) continue;
                    // the shoreline (water beside dry land) is a step by rule: dry land ≥ level + 1.15, the water's edge at level − 0.8
                    if ((w.WaterMask[i0] != 0) != (w.WaterMask[j] != 0))
                    {
                        // height of the dry bank right at the water above the water level (a vertical wall at the water when high)
                        double dry = (w.WaterMask[i0] != 0 ? w.Heights[j] : w.Heights[i0]) / 256.0 - site.Level;
                        if (dry > bankTop) { bankTop = dry; bx = xx; bz = zz; }
                        continue;
                    }
                    double d = Math.Abs(w.Heights[j] - w.Heights[i0]) / 256.0, was = Math.Abs(pre[j] - pre[i0]) / 256.0;
                    if (d > was + 0.01 && d > worst) { worst = d; worstWas = was; wx = xx; wz = zz; }
                }
            }
        return (worst, worstWas, wx, wz, bankTop, bx, bz);
    }

    [Test("Bridge kits: no edges in the ground around each bridge of the mod, alone in the middle of a river (new steps ≤ 1.5 m, bank at the water ≤ 3 m) – needs the game folder")]
    static void NoEdges()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? MapEditor.GameLocator.Find()?.Folder;
        if (game == null) Check.Skip("game folder not found");
        var files = PrefabCatalog.AllFiles(game);
        PrefabDef Describe(string n)
        {
            if (!files.TryGetValue(n, out var f)) return null;
            var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Part;
            return d.Found ? d : null;
        }
        var all = BridgeKitStore.All(files, Describe).Where(k => k.Usable && k.Mod == "YAMEE_Bridges").ToList();
        if (all.Count == 0) Check.Skip("no bridge of YAMEE_Bridges in the game folder");
        // the same generated 4-km world as the test world (the small fixture world is very hilly right beside its river)
        string dir = Path.Combine(Fixture.Temp, "edges", "Kantentest");
        var spec = Fixture.Spec("Brueckentest"); spec.Name = "Kantentest"; spec.Size = 4096;
        var profile = MapEditor.GameLocator.ProfileOf(game);
        new WorldGenerator(spec).Write(dir, MapEditor.GameLocator.WorldsFolderFor(profile), new Progress<string>(), profile?.Version);
        var big = WorldData.Load(dir, null, new Progress<string>());
        var w = RiverWorld(out var e, out _, 9, 10, big);
        string before = Fixture.State(w);
        foreach (var kit in all)
        {
            var k = kit.Clone(); k.Active = true;
            var kits = new BridgeKits(new[] { k });
            bool gravel = k.Id == BridgeKitDef.WoodId || !k.Asphalt;
            var site = kits.Find(w, 0, -100, gravel);
            Check.True(site.Problem == null, $"{k.Label}: {site.Problem}");
            site.Gravel = gravel;
            var (ed, td) = kits.Defs(site);
            var pre = (ushort[])w.Heights.Clone();
            e.Run(k.Label, "", q => Bridge.Build(w, q, site, ed, td));
            var r = Edges(w, pre, site);
            Console.WriteLine($"       {k.Id}: biggest new step {r.Step:0.00} m at {r.X}/{r.Z}, bank at the water {r.Bank:0.00} m at {r.BX}/{r.BZ}");
            Check.True(r.Step <= 1.5, $"{k.Id}: step of {r.Step:0.00} m at {r.X}/{r.Z}");
            // 3 m: right at the corner of the abutment of the high bridges (deck 7 m above the water) – a wall of 5–10 m before
            Check.True(r.Bank <= 3.0, $"{k.Id}: bank {r.Bank:0.00} m above the water at {r.BX}/{r.BZ}");
            e.Undo();
        }
        Check.Equal(before, Fixture.State(w), "undo exact");
    }

    [Test("Bridge kits: the bridge of the game can be removed and stays away, our own kits cannot be removed")]
    static void RemoveGameBridge()
    {
        string file = BridgeKitStore.FilePath;
        string before = File.Exists(file) ? File.ReadAllText(file) : null;
        try
        {
            var game = new BridgeKitDef { Id = BridgeKitDef.GameId, End = "rwg_bridge_end_01", Tile = "rwg_bridge_tile_01" };
            var user = new BridgeKitDef { Id = "user:a|b", End = "a", Tile = "b" };
            Check.True(game.Removable && user.Removable, "bridge of the game and kits added by hand removable");
            Check.True(!BridgeKitDef.Yamee().Removable && !BridgeKitDef.YameeWood().Removable, "own kits not removable");
            Check.True(!new BridgeKitDef { Id = BridgeKitDef.RwgId }.Removable, "rebuilt bridge of the game (our mod) not removable");
            BridgeKitStore.LoadSaved();
            BridgeKitStore.MarkRemoved(game);
            BridgeKitStore.Save(new[] { user });
            var saved = BridgeKitStore.LoadSaved();
            Check.True(saved.Count == 1 && saved[0].Id == user.Id, "removed entry not in the list");
            Check.True(BridgeKitStore.RemovedIds.Contains(BridgeKitDef.GameId), "removal remembered");
            var all = BridgeKitStore.All(new Dictionary<string, string>(), _ => null);
            Check.True(all.All(k => k.Id != BridgeKitDef.GameId), "bridge of the game stays away");
        }
        finally
        {
            if (before != null) File.WriteAllText(file, before, new UTF8Encoding(false)); else if (File.Exists(file)) File.Delete(file);
            BridgeKitStore.LoadSaved();
        }
    }
}
