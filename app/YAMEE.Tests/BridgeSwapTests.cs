using System.Windows;
using MapEditor.Map;

namespace YameeTests;

/// swapping bridges (the user's request 2026-11-25): in the plan (bridge and straight piece of the line) and on the map (old bridge with its
/// approaches and the road in front of them away, new bridge with ground, road joined again)
static class BridgeSwapTests
{
    static BridgeKitDef Kit(string id, int width, int endLength, int tileLength, int clearance, int deck)
    {
        var k = new BridgeKitDef { Id = id, Label = id, End = id + "_end", Tile = id + "_tile", Active = true, Width = width, EndLength = endLength,
            TileLength = tileLength, Clearance = clearance, DeckLayer = deck, LandUnder = Math.Max(1, endLength - 7), TerrainColumns = 0 };
        k.EndDef = new PrefabDef { SizeX = endLength, SizeY = deck + 8, SizeZ = width, Found = true, Kind = PrefabKind.Part };
        k.TileDef = new PrefabDef { SizeX = tileLength, SizeY = deck + 8, SizeZ = width, Found = true, Kind = PrefabKind.Part };
        return k;
    }

    static WorldData RiverWorld(out WorldEdits e, int widthMin = 20, int widthMax = 22)
    {
        var w = Fixture.LoadCopy();
        e = new WorldEdits(w);
        const int half = 300;
        // gentle land like in most maps (the generated test world is hilly there: 40 m on 100 m beside the river): 3 % up to the north,
        // the river cuts its valley into it
        double h0 = Math.Max(45, w.HeightAt(0, -100));
        if (Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_HILLY") == null) e.Run("flat land", "", ed =>
        {
            for (int z = -480; z <= 280; z++)
                for (int x = -440; x <= 440; x++)
                {
                    double edge = Math.Min(Math.Min(x + 440, 440 - x), Math.Min(z + 480, 280 - z)) / 40.0;
                    double k = Math.Clamp(edge, 0, 1);
                    double target = h0 + 0.03 * (z + 100);
                    double cur = w.HeightAt(x, z);
                    ed.SetHeight(w.Index(x, z), (ushort)Math.Round((cur + (target - cur) * k) * 256));
                }
        });
        Point a = new(-half, -100), b = new(half, -100);
        int lv = (int)Math.Floor(Math.Min(w.HeightAt(-half, -100), w.HeightAt(half, -100))) - 2;
        List<Point> Circle(Point c) => Enumerable.Range(0, 36).Select(k => new Point(c.X + 60 * Math.Cos(k * Math.PI / 18), c.Y + 60 * Math.Sin(k * Math.PI / 18))).ToList();
        e.Run("lake A", "lake A", ed => Lake.Place(w, ed, new LakeSpec { Level = lv, Bumps = 0, Outline = Circle(a) }));
        e.Run("lake B", "lake B", ed => Lake.Place(w, ed, new LakeSpec { Level = lv, Bumps = 0, Outline = Circle(b) }));
        e.Run("river", "river", ed => River.Build(w, ed, new RiverSpec { ClickA = a, ClickB = b, Line = new() { a, new(0, -100), b }, WidthMin = widthMin, WidthMax = widthMax }));
        return w;
    }

    /// a planned line over a bridge like the planner makes it: free ground, lead, approach, approach, lead, free ground
    static List<Point> PlannedLine(BridgeSite s, double far = 160)
    {
        var (a, b, _) = NetworkBuilder.ApproachEnds(s);
        var u = s.AlongX ? new Vector(1, 0) : new Vector(0, 1);
        var lead = RoadNetwork.BridgeLead;
        var pts = new List<Point>();
        for (double d = far; d > lead + 1; d -= 16) pts.Add(a - u * d);
        pts.AddRange(new[] { a - u * lead, a, b, b + u * lead });
        for (double d = lead + 16; d <= far; d += 16) pts.Add(b + u * d);
        return pts;
    }

    static double Along(BridgeSite s, Point p) => s.AlongX ? p.X : p.Y;

    [Test("Bridge swap: a planned bridge gets another kit, the straight piece of the line fits the new bridge, back again gives the old line")]
    static void SwapInPlan()
    {
        var w = RiverWorld(out _);
        var ka = Kit("swap_a", 10, 13, 10, 4, 10); var kb = Kit("swap_b", 14, 9, 9, 6, 13);
        var site = Bridge.Find(w, 10, -100, ka, RoadNetwork.NetMaxAngle);
        Check.True(site.Problem == null, "place for kit a: " + site.Problem);
        var line = PlannedLine(site);
        var road = new NetRoad { Points = line.ToList(), Bridges = new() { site }, Width = 16 };
        var opts = BridgeSwap.Options(w, site, new[] { ka, kb });
        Check.True(opts.Single(o => o.Kit == ka).Current, "kit a is the current one");
        var ob = opts.Single(o => o.Kit == kb);
        Check.True(ob.Site.Problem == null, "kit b fits: " + ob.Site.Problem);
        Check.True(Math.Abs(ob.Site.Center.X - site.Center.X) <= 1 && Math.Abs(ob.Site.Center.Y - site.Center.Y) <= 1, "same middle");
        Check.True(BridgeSwap.InPlan(road, 0, ob.Site), "swapped in the plan");
        Check.Equal("swap_b", road.Bridges[0].Kit, "kit of the planned bridge");
        var (a, b, _) = NetworkBuilder.ApproachEnds(road.Bridges[0]);
        Check.True(road.Points.Any(p => (p - a).Length < 0.01) && road.Points.Any(p => (p - b).Length < 0.01), "line runs over the new approaches");
        for (int i = 1; i < road.Points.Count; i++)
            Check.True(Along(site, road.Points[i]) > Along(site, road.Points[i - 1]), $"line goes on along the bridge (point {i})");
        Check.True((road.Points[0] - line[0]).Length < 0.01 && (road.Points[^1] - line[^1]).Length < 0.01, "ends of the line stay");
        // and back
        var back = BridgeSwap.Options(w, road.Bridges[0], new[] { ka, kb }).Single(o => o.Kit == ka);
        Check.True(BridgeSwap.InPlan(road, 0, back.Site), "swapped back");
        Check.Equal(line.Count, road.Points.Count, "points after swapping back");
        Check.True(line.Zip(road.Points).All(x => (x.First - x.Second).Length < 0.01), "the old line again");
        // a wilderness path the same way
        var path = new WildPath { Points = line.ToList(), Bridges = new() { site } };
        Check.True(BridgeSwap.InPlan(path, 0, ob.Site), "wilderness path swapped");
        Check.Equal("swap_b", path.Bridges[0].Kit, "kit of the path's bridge");
    }

    [Test("Bridge swap: a planned line whose points do not lie exactly on the straight piece (one lead point 30 m further out, no approach points, 0.3 m off the axis) can be swapped too")]
    static void SwapInPlanLoose()
    {
        // the user's report 2026-11-26 (Buzovi-Land): 5 of 9 planned bridges said "the planned line does not run straight over this bridge"
        var w = RiverWorld(out _);
        var ka = Kit("swap_a", 10, 13, 10, 4, 10); var kb = Kit("swap_b", 14, 9, 9, 6, 13);
        var site = Bridge.Find(w, 10, -100, ka, RoadNetwork.NetMaxAngle);
        Check.True(site.Problem == null, "place for kit a: " + site.Problem);
        var (a, b, _) = NetworkBuilder.ApproachEnds(site);
        var u = site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
        var n = new Vector(-u.Y, u.X);
        var lead = RoadNetwork.BridgeLead;
        var line = new List<Point>();
        for (double d = 160; d > lead + 40; d -= 16) line.Add(a - u * d);
        line.Add(a - u * (lead + 30) + n * 0.3);
        line.Add(b + u * lead - n * 0.2);
        for (double d = lead + 16; d <= 160; d += 16) line.Add(b + u * d);
        var road = new NetRoad { Points = line.ToList(), Bridges = new() { site }, Width = 16 };
        var ob = BridgeSwap.Options(w, site, new[] { ka, kb }).Single(o => o.Kit == kb);
        Check.True(ob.Site.Problem == null, "kit b fits: " + ob.Site.Problem);
        Check.True(BridgeSwap.InPlan(road, 0, ob.Site), "swapped in the plan");
        var (na, nb, _) = NetworkBuilder.ApproachEnds(road.Bridges[0]);
        Check.True(road.Points.Any(p => (p - na).Length < 0.01) && road.Points.Any(p => (p - nb).Length < 0.01), "line runs over the new approaches");
        for (int i = 1; i < road.Points.Count; i++)
            Check.True(Along(site, road.Points[i]) > Along(site, road.Points[i - 1]), $"line goes on along the bridge (point {i})");
        Check.True((road.Points[0] - line[0]).Length < 0.01 && (road.Points[^1] - line[^1]).Length < 0.01, "ends of the line stay");
    }

    [Test("Bridge swap: a built bridge is found from its pieces and swapped – new pieces, ground and approaches, the road joined on both sides without steps; undo exact")]
    static void SwapBuilt()
    {
        var w = RiverWorld(out var e);
        var ka = Kit("swapb_a", 10, 13, 10, 4, 10); var kb = Kit("swapb_b", 14, 9, 9, 6, 13);
        BridgeKits.RememberPieces(new[] { ka, kb });
        var prevKits = RoadNetwork.Bridges;
        RoadNetwork.Bridges = new BridgeKits(new[] { ka, kb });
        try
        {
            var spec = RouteTests.Spec();
            // the flattest place along the river (the road climbs out of the valley on both sides; the test world is hilly)
            BridgeSite site = null; double bestDh = double.MaxValue;
            for (int x = -200; x <= 200; x += 10)
            {
                var s = Bridge.Find(w, x, -100, ka, RoadNetwork.NetMaxAngle);
                if (s.Problem != null) continue;
                double dh = new[] { -1, 1 }.Max(sg => Math.Abs(w.HeightAt((int)s.Center.X, (int)(s.Center.Y + sg * (s.Length / 2.0 + 130))) - (s.Deck - 1)));
                if (dh < bestDh) { bestDh = dh; site = s; }
            }
            Check.True(site != null, "no place for kit a");
            Console.WriteLine($"       bridge at {site.Center.X:0},{site.Center.Y:0}, terrain 130 m beyond the approaches up to {bestDh:0.0} m above/below the road");
            var road = new NetRoad { Points = PlannedLine(site, 260), Bridges = new() { site }, Width = 8, Manual = true };
            road.Length = (road.Points.Count - 1) * 16.0;
            var job = new RouteJob(); job.Roads.Add(road);
            e.Run("road with bridge", "", ed => RouteBuilder.Build(w, ed, job, spec, null));
            string before = Fixture.State(w);
            // largest step between neighbouring meters along the middle of the road and cells without surface (outside the water)
            (double Step, int Gaps) Along(BridgeSite b)
            {
                int tm = (int)Math.Floor(b.MidT);
                double st = 0; int g = 0;
                foreach (var (from, to) in new[] { (b.S0 - Bridge.Approach - 150, b.S0 + b.LandUnder - 1), (b.S0 + b.Length - b.LandUnder, b.S0 + b.Length + Bridge.Approach + 150) })
                    for (int s = from; s <= to; s++)
                    {
                        int x = b.AlongX ? s : tm, z = b.AlongX ? tm : s;
                        if (w.RoadAt(x, z) == 0) g++;
                        if (s > from) { int px = b.AlongX ? s - 1 : tm, pz = b.AlongX ? tm : s - 1; st = Math.Max(st, Math.Abs(w.HeightAt(x, z) - w.HeightAt(px, pz))); }
                    }
                return (st, g);
            }
            var (stepBefore, _) = Along(site);
            int count = w.Prefabs.Count;
            var piece = w.PrefabSnapshot().First(p => p.Name == "swapb_a_tile");
            var bb = BridgeSwap.FromPieces(w, piece, new[] { ka, kb });
            Check.True(bb != null, "bridge found from a middle piece");
            Check.True(bb.Site.S0 == site.S0 && bb.Site.T0 == site.T0 && bb.Site.Tiles == site.Tiles && bb.Site.Deck == site.Deck && bb.Site.AlongX == site.AlongX,
                $"same place: {bb.Site.S0}/{bb.Site.T0}/{bb.Site.Tiles}/{bb.Site.Deck} – {site.S0}/{site.T0}/{site.Tiles}/{site.Deck}");
            Check.Equal(site.Tiles + 2, bb.Pieces.Count, "pieces");
            Check.True(!bb.Site.Gravel, "asphalt approaches");
            Check.True(BridgeSwap.FromPieces(w, bb.Pieces[0], new[] { ka, kb })?.Pieces.Count == bb.Pieces.Count, "found from an end piece too");
            var others = w.PrefabSnapshot().Where(p => !bb.Pieces.Contains(p)).ToList();
            var opts = BridgeSwap.Options(w, bb.Site, new[] { ka, kb }, others, BridgeSwap.OldCorridor(bb.Site));
            var ob = opts.Single(o => o.Kit == kb);
            Check.True(ob.Site.Problem == null, "kit b fits the built place: " + ob.Site.Problem);
            BridgeSwapResult res = null;
            e.Run("swap", "", ed => res = BridgeSwap.Build(w, ed, bb, kb, spec, null));
            Check.True(res != null, "swapped");
            Console.WriteLine($"       new bridge {res.Site.Length} m, {res.Site.Tiles} middle piece(s), deck {res.Site.Deck}; roads joined {res.Roads}; " + string.Join(" | ", res.Warnings));
            if (Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_DUMP") != null && res.Road != null) foreach (var p in res.Road.Points) Console.WriteLine($"         {p.X:0.0},{p.Y:0.0}  h {w.HeightAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)):0.00}");
            Check.Equal(2, res.Roads, "road joined on both sides");
            Check.Equal(0, w.Prefabs.Count(p => p.Name.StartsWith("swapb_a")), "old pieces gone");
            Check.Equal(res.Site.Tiles + 2, w.Prefabs.Count(p => p.Name.StartsWith("swapb_b")), "new pieces");
            Check.Equal(count - (site.Tiles + 2) + res.Site.Tiles + 2, w.Prefabs.Count, "nothing else added or removed");
            // along the middle of the road: surface everywhere outside the bridge, the approach on deck − 1 + lift, no steps
            var nb = res.Site;
            double h = nb.Deck - 1 + nb.ApproachLift;
            int t = (int)Math.Floor(nb.MidT);
            var (maxStep, gaps) = Along(nb);
            Console.WriteLine($"       largest step along the road {maxStep:0.00} m (before the swap {stepBefore:0.00} m), cells without surface {gaps}");
            Check.Equal(0, gaps, "road surface along the middle");
            Check.True(maxStep < Math.Max(0.6, stepBefore + 0.2), $"no steps along the road ({maxStep:0.00} m, before {stepBefore:0.00} m)");
            foreach (int sv in new[] { nb.S0 - Bridge.Approach, nb.S0 + nb.Length + Bridge.Approach - 1 })
            {
                int x = nb.AlongX ? sv : t, z = nb.AlongX ? t : sv;
                Check.True(Math.Abs(w.HeightAt(x, z) - h) < 0.3, $"approach height {w.HeightAt(x, z):0.00} / {h:0.00}");
            }
            // the approach is as wide as the new bridge (14 m)
            int sOut = nb.S0 - 5, wide = 0;
            for (int tt = nb.T0; tt < nb.T0 + nb.Width; tt++) { int x = nb.AlongX ? sOut : tt, z = nb.AlongX ? tt : sOut; if (w.RoadAt(x, z) != 0) wide++; }
            Check.Equal(nb.Width, wide, "approach over the whole width of the new bridge");
            e.Undo();
            Check.Equal(before, Fixture.State(w), "undo exact");
            Check.Equal(count, w.Prefabs.Count, "pieces after undo");
        }
        finally { RoadNetwork.Bridges = prevKits; }
    }

    [Test("Bridge swap: a built gravel bridge stays gravel – the path in front of it keeps gravel, its width and stays without asphalt")]
    static void SwapBuiltGravel()
    {
        // the user's report 2026-11-27: swapping a bridge turned the gravel path in front of and behind it into (bad) asphalt
        var w = RiverWorld(out var e);
        var ka = Kit("swapg_a", 10, 13, 10, 4, 10); var kb = Kit("swapg_b", 14, 9, 9, 6, 13);
        ka.Gravel = kb.Gravel = true;
        BridgeKits.RememberPieces(new[] { ka, kb });
        var prevKits = RoadNetwork.Bridges;
        RoadNetwork.Bridges = new BridgeKits(new[] { ka, kb });
        try
        {
            var spec = RouteTests.Spec();
            BridgeSite site = null; double bestDh = double.MaxValue;
            for (int x = -200; x <= 200; x += 10)
            {
                var s = Bridge.Find(w, x, -100, ka, RoadNetwork.NetMaxAngle);
                if (s.Problem != null) continue;
                double dh = new[] { -1, 1 }.Max(sg => Math.Abs(w.HeightAt((int)s.Center.X, (int)(s.Center.Y + sg * (s.Length / 2.0 + 130))) - (s.Deck - 1)));
                if (dh < bestDh) { bestDh = dh; site = s; }
            }
            Check.True(site != null, "no place for kit a");
            site.Gravel = true;
            var road = new NetRoad { Points = PlannedLine(site, 260), Bridges = new() { site }, Width = 5, Gravel = true, Manual = true };
            road.Length = (road.Points.Count - 1) * 16.0;
            var job = new RouteJob(); job.Roads.Add(road);
            e.Run("path with bridge", "", ed => RouteBuilder.Build(w, ed, job, spec, null));
            // an asphalt road across the path about 100 m in front of one approach (where the joint of the swap lies)
            {
                var (pa, _, _) = NetworkBuilder.ApproachEnds(site);
                var u = site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
                var n = new Vector(-u.Y, u.X);
                var c = pa - u * 104;
                var cross = new NetRoad { Points = Enumerable.Range(-6, 13).Select(i => c + n * (i * 20.0)).ToList(), Width = 16, Manual = true };
                cross.Length = 240;
                var job2 = new RouteJob(); job2.Roads.Add(cross);
                e.Run("asphalt road across", "", ed => RouteBuilder.Build(w, ed, job2, spec, null));
            }
            // surface in a 40 m wide band along the path, outside the bridge itself
            (int Asphalt, int Gravel) Count(BridgeSite b)
            {
                int asp = 0, grv = 0, tm = (int)Math.Floor(b.MidT);
                for (int s = site.S0 - 260; s <= site.S0 + site.Length + 260; s++)
                {
                    if (s >= Math.Min(b.S0, site.S0) && s < Math.Max(b.S0 + b.Length, site.S0 + site.Length)) continue;
                    for (int t = tm - 20; t <= tm + 20; t++)
                    {
                        int x = b.AlongX ? s : t, z = b.AlongX ? t : s;
                        byte k = w.RoadAt(x, z);
                        if (k == WorldData.Asphalt) asp++; else if (k == WorldData.Gravel) grv++;
                    }
                }
                return (asp, grv);
            }
            var before = Count(site);
            var piece = w.PrefabSnapshot().First(p => p.Name == "swapg_a_tile");
            var bb = BridgeSwap.FromPieces(w, piece, new[] { ka, kb });
            Check.True(bb != null, "bridge found");
            Check.True(bb.Site.Gravel, "gravel approaches recognised");
            BridgeSwapResult res = null;
            e.Run("swap", "", ed => res = BridgeSwap.Build(w, ed, bb, kb, spec, null));
            Check.True(res != null, "swapped");
            var after = Count(res.Site);
            Console.WriteLine($"       before: asphalt {before.Asphalt} m², gravel {before.Gravel} m²; after: asphalt {after.Asphalt} m², gravel {after.Gravel} m²; road gravel {res.Road?.Gravel}, width {res.Road?.Width}; " + string.Join(" | ", res.Warnings));
            Check.True(res.Road != null && res.Road.Gravel, "the path over the new bridge is gravel");
            Check.True(after.Asphalt <= before.Asphalt, $"no new asphalt ({after.Asphalt} m², before {before.Asphalt} m²)");
            Check.Equal(5.0, res.Road.Width, "width of the path stays");
        }
        finally { RoadNetwork.Bridges = prevKits; }
    }

    [Test("Bridge swap: every real bridge kit is found again from each of its built pieces and can be swapped for another one – needs the game folder")]
    static void RealKits()
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
        if (all.Count < 2) Check.Skip("fewer than two bridge kits in the game folder");
        // pieces of the bridge kits are bridges when a map is loaded (own kind and group, not parts – the user 2026-11-26)
        var names = all.SelectMany(k => new[] { k.End, k.Tile }).Where(n => !string.IsNullOrEmpty(n)).ToList();
        var loaded = PrefabCatalog.Load(game, names);
        foreach (var n in names)
        {
            Check.Equal(PrefabKind.Bridge, loaded[n].Kind, n + ": kind when a map is loaded");
            Check.Equal("bridge", PrefabCategories.Of(n).Key, n + ": group in the building list");
        }
        var prevKits = RoadNetwork.Bridges;
        RoadNetwork.Bridges = new BridgeKits(all);
        try
        {
            var spec = RouteTests.Spec();
            foreach (var kit in all)
            {
                var w = RiverWorld(out var e);
                // narrow kits first: take the middle of the river; a kit that does not fit this river is skipped
                var site = Bridge.Find(w, 0, -100, kit, RoadNetwork.NetMaxAngle);
                if (site.Problem != null) { Console.WriteLine($"       {kit.Id}: {site.Problem}"); continue; }
                int count = w.Prefabs.Count;
                e.Run("bridge", "", ed => Bridge.Build(w, ed, site, kit.EndDef, kit.TileDef));
                var pieces = w.PrefabSnapshot().Skip(count).ToList();
                foreach (var p in pieces)
                {
                    var bb = BridgeSwap.FromPieces(w, p, all);
                    Check.True(bb != null, $"{kit.Id}: bridge not found from {p.Name} (rotation {p.Rotation})");
                    Check.True(bb.Kit.Id == kit.Id && bb.Site.S0 == site.S0 && bb.Site.T0 == site.T0 && bb.Site.Tiles == site.Tiles && bb.Site.Deck == site.Deck && bb.Pieces.Count == pieces.Count,
                        $"{kit.Id} from {p.Name}: {bb.Kit.Id} {bb.Site.S0}/{bb.Site.T0}/{bb.Site.Tiles}/{bb.Site.Deck} – {site.S0}/{site.T0}/{site.Tiles}/{site.Deck}");
                }
                // swap for every other kit that fits
                var bridge = BridgeSwap.FromPieces(w, pieces[0], all);
                var others = w.PrefabSnapshot().Where(p => !bridge.Pieces.Contains(p)).ToList();
                var fits = BridgeSwap.Options(w, bridge.Site, all, others, BridgeSwap.OldCorridor(bridge.Site)).Where(o => !o.Current && o.Site.Problem == null).ToList();
                Console.WriteLine($"       {kit.Id}: found from all {pieces.Count} piece(s); fits instead: {string.Join(", ", fits.Select(o => o.Kit.Id))}");
                var other = fits.FirstOrDefault();
                if (other == null) continue;
                string before = Fixture.State(w);
                BridgeSwapResult res = null;
                e.Run("swap", "", ed => res = BridgeSwap.Build(w, ed, bridge, other.Kit, spec, null));
                Check.True(res != null, $"{kit.Id} → {other.Kit.Id} swapped");
                var piece = w.PrefabSnapshot().First(p => p.Name.Equals(other.Kit.End, StringComparison.OrdinalIgnoreCase));
                Check.Equal(other.Kit.Id, BridgeSwap.FromPieces(w, piece, all)?.Kit.Id, $"{kit.Id} → {other.Kit.Id}: the new bridge is found");
                e.Undo();
                Check.Equal(before, Fixture.State(w), $"{kit.Id} → {other.Kit.Id}: undo exact");
            }
        }
        finally { RoadNetwork.Bridges = prevKits; }
    }

    [Test("Bridge swap (real map): every built bridge swapped for every other fitting kit keeps the surface of its road (YAMEE_TEST_SWAP_WORLD, only read)")]
    static void RealMapSurface()
    {
        string folder = Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_WORLD");
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? MapEditor.AppSettings.GuessGameFolder();
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "dtm.raw")) || game == null)
            Check.Skip("set YAMEE_TEST_SWAP_WORLD to a project folder with bridges (it is only read)");
        var files = PrefabCatalog.AllFiles(game);
        PrefabDef Describe(string n)
        {
            if (!files.TryGetValue(n, out var f)) return null;
            var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Part;
            return d.Found ? d : null;
        }
        var all = BridgeKitStore.All(files, Describe).Where(k => k.Usable).ToList();
        var w = WorldData.Load(folder, game, new Progress<string>());
        var e = new WorldEdits(w);
        var prevKits = RoadNetwork.Bridges;
        RoadNetwork.Bridges = new BridgeKits(all);
        try
        {
            var spec = MapEditor.UserValues.Load(new NetworkSpec { Seed = w.Name });
            var seen = new HashSet<string>();
            foreach (var p in w.PrefabSnapshot().ToList())
            {
                var bb = BridgeSwap.FromPieces(w, p, all);
                if (bb == null || !seen.Add($"{bb.Site.S0}/{bb.Site.T0}")) continue;
                if (Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_AT") is string at)
                {
                    var xy = at.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    if ((bb.Site.Center - new Point(xy[0], xy[1])).Length > 80) continue;
                }
                int tm = (int)Math.Floor(bb.Site.MidT);
                (int A, int G) Count()
                {
                    int a = 0, g = 0;
                    for (int s = bb.Site.S0 - 340; s <= bb.Site.S0 + bb.Site.Length + 340; s++)
                        for (int t = tm - 30; t <= tm + 30; t++)
                        {
                            if (s >= bb.Site.S0 - 30 && s < bb.Site.S0 + bb.Site.Length + 30) continue;
                            int x = bb.Site.AlongX ? s : t, z = bb.Site.AlongX ? t : s;
                            byte k = w.RoadAt(x, z);
                            if (k == WorldData.Asphalt) a++; else if (k == WorldData.Gravel) g++;
                        }
                    return (a, g);
                }
                var before = Count();
                // surface beside the road (branches): 10–30 m from its middle, from 30 m in front of the bridge outwards
                int Beside()
                {
                    int n = 0;
                    for (int s = bb.Site.S0 - 340; s <= bb.Site.S0 + bb.Site.Length + 340; s++)
                        for (int t = tm - 30; t <= tm + 30; t++)
                        {
                            if (Math.Abs(t - tm) < 10 || (s >= bb.Site.S0 - 30 && s < bb.Site.S0 + bb.Site.Length + 30)) continue;
                            int x = bb.Site.AlongX ? s : t, z = bb.Site.AlongX ? t : s;
                            if (w.RoadAt(x, z) != 0) n++;
                        }
                    return n;
                }
                int besideBefore = Beside();
                // largest step between neighbouring cells with surface around the bridge (edges and mouths included, not the bridge itself)
                (double, string) StepsAt()
                {
                    double m = 0; string at = "";
                    for (int s = bb.Site.S0 - 200; s <= bb.Site.S0 + bb.Site.Length + 200; s++)
                        for (int t = tm - 120; t <= tm + 120; t++)
                        {
                            if (s >= bb.Site.S0 - 12 && s < bb.Site.S0 + bb.Site.Length + 12 && Math.Abs(t - tm) < 30) continue;
                            int x = bb.Site.AlongX ? s : t, z = bb.Site.AlongX ? t : s;
                            if (w.RoadAt(x, z) == 0) continue;
                            // only real surface: slivers of 1 m (left over from old edits) are no roadway
                            int nb = (w.RoadAt(x - 1, z) != 0 ? 1 : 0) + (w.RoadAt(x + 1, z) != 0 ? 1 : 0) + (w.RoadAt(x, z - 1) != 0 ? 1 : 0) + (w.RoadAt(x, z + 1) != 0 ? 1 : 0);
                            if (nb < 3) continue;
                            if (w.RoadAt(x + 1, z) != 0 && Math.Abs(w.HeightAt(x + 1, z) - w.HeightAt(x, z)) > m) { m = Math.Abs(w.HeightAt(x + 1, z) - w.HeightAt(x, z)); at = $"{x},{z}"; }
                            if (w.RoadAt(x, z + 1) != 0 && Math.Abs(w.HeightAt(x, z + 1) - w.HeightAt(x, z)) > m) { m = Math.Abs(w.HeightAt(x, z + 1) - w.HeightAt(x, z)); at = $"{x},{z}"; }
                        }
                    return (m, at);
                }
                double Steps() => StepsAt().Item1;
                double stepsBefore = Steps();
                var others = w.PrefabSnapshot().Where(q => !bb.Pieces.Contains(q)).ToList();
                var kits = all.Where(k => bb.Site.Gravel ? k.Gravel : k.Asphalt).ToList();
                var fits = BridgeSwap.Options(w, bb.Site, kits, others, BridgeSwap.OldCorridor(bb.Site)).Where(o => !o.Current && o.Site.Problem == null).ToList();
                Console.WriteLine($"       bridge {bb.Kit.Id} at {bb.Site.Center.X:0},{bb.Site.Center.Y:0}, gravel {bb.Site.Gravel}: asphalt {before.A} m², gravel {before.G} m²; fits {string.Join(", ", fits.Select(o => o.Kit.Id))}");
                void Dump()
                {
                    if (Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_DUMP") == null) return;
                    for (int s = bb.Site.S0 - 340; s <= bb.Site.S0 + bb.Site.Length + 340; s += int.Parse(Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_DUMP")))
                    {
                        var sb = new System.Text.StringBuilder($"         s {s,6} ");
                        for (int t = tm - 25; t <= tm + 25; t++)
                        {
                            int x = bb.Site.AlongX ? s : t, z = bb.Site.AlongX ? t : s;
                            byte k = w.RoadAt(x, z);
                            sb.Append(k == WorldData.Asphalt ? 'A' : k == WorldData.Gravel ? 'g' : '.');
                        }
                        int xm = bb.Site.AlongX ? s : tm, zm = bb.Site.AlongX ? tm : s;
                        sb.Append($"  h {w.HeightAt(xm, zm):0.0}");
                        Console.WriteLine(sb);
                    }
                }
                void Probe()
                {
                    if (Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_PROBE") is not string pr) return;
                    var v = pr.Split(',').Select(int.Parse).ToArray();
                    for (int zz = v[1]; zz <= v[2]; zz += 1)
                    {
                        var sb = new System.Text.StringBuilder($"         z {zz} ");
                        for (int xx = v[0] - 12; xx <= v[0] + 12; xx++) sb.Append(w.RoadAt(xx, zz) == WorldData.Asphalt ? 'A' : w.RoadAt(xx, zz) == WorldData.Gravel ? 'g' : '.');
                        sb.Append("  h " + string.Join(" ", Enumerable.Range(v[0] - 6, 13).Select(xx => $"{w.HeightAt(xx, zz):0.0}")));
                        Console.WriteLine(sb);
                    }
                }
                Probe();
                Dump();
                foreach (var o in fits)
                {
                    BridgeSwapResult res = null;
                    e.Run("swap", "", ed => res = BridgeSwap.Build(w, ed, bb, o.Kit, spec, null));
                    var after = Count();
                    int besideAfter = Beside();
                    double stepsAfter = Steps();
                    Console.WriteLine($"           largest step on the surface around the bridge {stepsAfter:0.00} m at {StepsAt().Item2}, before {stepsBefore:0.00} m");
                    if (Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_STEPAT") != null) { var at2 = StepsAt().Item2.Split(',').Select(int.Parse).ToArray(); for (int zz = at2[1] - 2; zz <= at2[1] + 2; zz++) Console.WriteLine("             " + string.Join(" ", Enumerable.Range(at2[0] - 3, 7).Select(xx => $"{w.RoadAt(xx, zz)}/{w.HeightAt(xx, zz):0.00}"))); }
                    if (Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_ROW") != null) Console.WriteLine("           row: " + string.Join(" ", new[] { 3530, 3545, 3552, 3556, 3561, 3566, 3575, 3590 }.Select(x => $"{x}: {w.RoadAt(x, 2211)}/{w.HeightAt(x, 2211):0.00} {w.RoadAt(x, 2212)}/{w.HeightAt(x, 2212):0.00}")));
                    if (o.Kit.Id == (Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_KIT") ?? fits[0].Kit.Id)) Probe();
                    // the pieces joining branches: no steps along them (a line every metre, 1 m between neighbours)
                    foreach (var br in res?.BranchRoads ?? new())
                    {
                        double step = 0, prev = double.NaN; Point stepAt = default; var prof = new List<string>();
                        var line = br.Points;
                        for (int q = 1; q < line.Count; q++)
                        {
                            var v = line[q] - line[q - 1]; double l = v.Length;
                            for (double u = 0; u < l; u += 1)
                            {
                                var pq = line[q - 1] + v * (u / l);
                                int px = (int)Math.Floor(pq.X), pz = (int)Math.Floor(pq.Y);
                                double hh = w.HeightAt(px, pz);
                                if (!double.IsNaN(prev) && Math.Abs(hh - prev) > step) { step = Math.Abs(hh - prev); stepAt = pq; }
                                if (((int)u) % 4 == 0) prof.Add($"{pq.Y:0}:{hh:0.00}");
                                prev = hh;
                            }
                        }
                        Console.WriteLine($"           branch joined from {line[0].X:0},{line[0].Y:0}: {br.Length:0} m, largest step {step:0.00} m at {stepAt.X:0},{stepAt.Y:0}; " + string.Join(" ", prof) + $"; pts " + string.Join(" ", line.Select(q => $"{q.X:0},{q.Y:0}")) + $" street half {br.From?.StreetHalf:0.0} dir {br.From?.StreetDX:0.00},{br.From?.StreetDZ:0.00} out {br.From?.DX:0.00},{br.From?.DZ:0.00} width {br.Width}");
                        if (Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_PREVIEW") != null) { var pv = RouteBuilder.Preview(w, br, spec); if (pv != null) Console.WriteLine("           preview: " + string.Join(" ", Enumerable.Range(0, 12).Select(k => pv.Profile[Math.Min(pv.Profile.Length - 1, k * pv.Profile.Length / 11)]).Select(h => $"{h:0.00}"))); }
                        Check.True(step < 0.6, $"{bb.Kit.Id} → {o.Kit.Id}: branch joined without steps ({step:0.00} m)");
                    }
                    if (o == fits[0]) { Console.WriteLine("         after:"); Dump(); if (res?.Road != null) Console.WriteLine("         road: " + string.Join(" ", res.Road.Points.Select(q => $"{q.X:0.0},{q.Y:0.0}")) + $" from {res.Road.From?.X:0.0},{res.Road.From?.Z:0.0}"); }
                    Console.WriteLine($"         → {o.Kit.Id}: asphalt {after.A} m², gravel {after.G} m²; branches {res?.Branches}, road gravel {res?.Road?.Gravel}, width {res?.Road?.Width}, from {res?.Road?.From?.Width}/{res?.Road?.From?.Gravel} to {res?.Road?.To?.Width}/{res?.Road?.To?.Gravel}; {string.Join(" | ", res?.Warnings ?? new())}");
                    e.Undo();
                    Console.WriteLine($"           surface beside the road (branches) {besideAfter} m², before {besideBefore} m²");
                    int besideTol = 15 + Math.Max(0, bb.Site.Width - o.Kit.Width) * 10;   // a narrower kit narrows the curved main-road taper inside |t - tm| >= 10
                    Check.True(besideAfter >= besideBefore - besideTol, $"{bb.Kit.Id} → {o.Kit.Id}: branches keep their mouths ({besideAfter} m², before {besideBefore} m²)");
                    Check.True(!bb.Site.Gravel || after.A <= before.A + 20, $"{bb.Kit.Id} → {o.Kit.Id}: no new asphalt on a gravel path ({after.A} m², before {before.A} m²)");
                }
                if (Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_CHAIN") is string chainSpec)
                {
                    var ids = chainSpec.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    var cur = bb;
                    int stepNo = 0;
                    foreach (var cid in ids)
                    {
                        var nextKit = all.FirstOrDefault(k => k.Id == cid);
                        if (nextKit == null || nextKit.Id == cur.Kit.Id) continue;
                        stepNo++;
                        BridgeSwapResult cres = null;
                        e.Run("chain-swap", "", ed => cres = BridgeSwap.Build(w, ed, cur, nextKit, spec, null));
                        Check.True(cres != null, $"chain step {stepNo} → {cid}");
                        Console.WriteLine($"         CHAIN #{stepNo} → {cid}: largest step {Steps():0.00} m at {StepsAt().Item2}; branches {cres.Branches}");
                        Probe();
                        cur = w.PrefabSnapshot().Where(p => Math.Abs(p.X - bb.Site.Center.X) < 80 && Math.Abs(p.Z - bb.Site.Center.Y) < 80).Select(p => BridgeSwap.FromPieces(w, p, all)).FirstOrDefault(b => b != null);
                        Check.True(cur != null, $"chain step {stepNo} bridge re-found");
                    }
                    for (int u = 0; u < stepNo; u++) e.Undo();
                }
            }
        }
        finally { RoadNetwork.Bridges = prevKits; }
    }

    [Test("Bridge swap: why the real kits do not fit rivers of several widths (output only; YAMEE_TEST_SWAP_WHY=1) – needs the game folder")]
    static void WhyNot()
    {
        if (Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_WHY") == null) Check.Skip("only with YAMEE_TEST_SWAP_WHY");
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? MapEditor.GameLocator.Find()?.Folder;
        if (game == null) Check.Skip("game folder not found");
        var files = PrefabCatalog.AllFiles(game);
        PrefabDef Describe(string n)
        {
            if (!files.TryGetValue(n, out var f)) return null;
            var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Bridge;
            return d.Found ? d : null;
        }
        var all = BridgeKitStore.All(files, Describe).Where(k => k.Usable).ToList();
        foreach (var k in all)
            Console.WriteLine($"       kit {k.Id}: {(k.Single ? "one piece" : "end+middle")}, {k.Width} m wide, length {k.EndLength}/{k.TileLength}, water {k.MinWater}–{k.MaxWater} m, deck {k.Clearance} m above the water, land under {k.LandUnder}, asphalt {k.Asphalt}, gravel {k.Gravel}");
        foreach (var (lo, hi) in new[] { (10, 11), (15, 16), (20, 22), (30, 32), (40, 42) })
        {
            var w = RiverWorld(out _, lo, hi);
            var first = all.Select(k => Bridge.Find(w, 0, -100, k, RoadNetwork.NetMaxAngle)).FirstOrDefault(s => s.Problem == null);
            Console.WriteLine($"       river {lo}–{hi} m (measured {first?.RiverWidth ?? 0:0} m):");
            foreach (var k in all)
            {
                var s = Bridge.Find(w, 0, -100, k, RoadNetwork.NetMaxAngle);
                Console.WriteLine($"         {k.Id,-28} {(s.Problem == null ? $"fits: {s.Length} m, {s.Tiles} middle, water {s.WaterWidth} m" : s.Problem)}");
            }
        }
    }

    /// relief picture around a place (2 px per meter): hill shade, water blue, roads grey, bridge pieces dark – for looking at the ground
    static void Picture(WorldData w, Point c, int r, string file)
    {
        int n = 2 * r, px = 2;
        int W = n * px;
        var buf = new byte[W * W * 4];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int x = (int)c.X - r + i, z = (int)c.Y + r - 1 - j;
                double h = w.HeightAt(x, z), hx = w.HeightAt(x + 1, z) - h, hz = w.HeightAt(x, z + 1) - h;
                double shade = Math.Clamp(0.55 + 0.35 * (-hx + hz) / Math.Sqrt(1 + hx * hx + hz * hz) + 0.004 * (h - 40), 0.05, 1);
                byte R = (byte)(150 * shade), G = (byte)(175 * shade), B = (byte)(120 * shade);
                if (w.WaterAt(x, z)) { R = (byte)(40 * shade); G = (byte)(90 * shade); B = (byte)(200 * Math.Max(0.6, shade)); }
                if (w.RoadAt(x, z) != 0) { R = G = B = (byte)(w.RoadAt(x, z) == WorldData.Asphalt ? 70 : 150); }
                if (w.PrefabSnapshot().Any(p => BridgeKits.IsBridgePiece(p.Name) && p.Contains(x + 0.5, z + 0.5))) { R = 200; G = 60; B = 40; }
                for (int dy = 0; dy < px; dy++)
                    for (int dx = 0; dx < px; dx++)
                    {
                        int o = ((j * px + dy) * W + i * px + dx) * 4;
                        buf[o] = B; buf[o + 1] = G; buf[o + 2] = R; buf[o + 3] = 255;
                    }
            }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(W, W, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, buf, W * 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    [Test("Bridge swap: what the ground looks like when a short bridge narrows a wide river or a long bridge widens a narrow one (output only; YAMEE_TEST_SWAP_LOOK=<folder for pictures>) – needs the game folder")]
    static void Look()
    {
        string outDir = Environment.GetEnvironmentVariable("YAMEE_TEST_SWAP_LOOK");
        if (outDir == null) Check.Skip("only with YAMEE_TEST_SWAP_LOOK");
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? MapEditor.GameLocator.Find()?.Folder;
        if (game == null) Check.Skip("game folder not found");
        var files = PrefabCatalog.AllFiles(game);
        PrefabDef Describe(string n)
        {
            if (!files.TryGetValue(n, out var f)) return null;
            var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Bridge;
            return d.Found ? d : null;
        }
        var all = BridgeKitStore.All(files, Describe).Where(k => k.Usable).ToDictionary(k => k.Id);
        foreach (var (lo, hi, id) in new[] { (30, 32, "yamee_bridge_concrete_1"), (30, 32, "yamee_bridge_wood2"), (34, 35, "yamee_bridge_concrete_1"), (10, 11, "yamee_bridge_asphalt1"), (10, 11, "yamee_10x10") })
        {
            var w = RiverWorld(out var e, lo, hi);
            var kit = all[id];
            var site = Bridge.Find(w, 0, -100, kit, RoadNetwork.NetMaxAngle, narrow: true);
            string name = $"{id}_{lo}";
            if (site.Problem != null) { Console.WriteLine($"       {name}: {site.Problem}"); continue; }
            Picture(w, site.Center, 110, Path.Combine(outDir, name + "_before.png"));
            var pre = (ushort[])w.Heights.Clone();
            var preWater = (byte[])w.WaterMask.Clone();
            e.Run("bridge", "", ed => Bridge.Build(w, ed, site, kit.EndDef, kit.TileDef));
            // biggest new step outside the bridge, water cells added/removed
            double worst = 0; int added = 0, removed = 0;
            for (int s = site.S0 - 80; s <= site.S0 + site.Length + 80; s++)
                for (int t = site.T0 - 80; t <= site.T0 + site.Width + 80; t++)
                {
                    int x = site.AlongX ? s : t, z = site.AlongX ? t : s, i = w.Index(x, z);
                    if (w.WaterMask[i] != 0 && preWater[i] == 0) added++;
                    if (w.WaterMask[i] == 0 && preWater[i] != 0) removed++;
                    if (s >= site.S0 - 1 && s <= site.S0 + site.Length && t >= site.T0 - 1 && t <= site.T0 + site.Width) continue;
                    foreach (int j in new[] { w.Index(x + 1, z), w.Index(x, z + 1) })
                    {
                        if ((w.WaterMask[i] != 0) != (w.WaterMask[j] != 0)) continue;
                        double d = Math.Abs(w.Heights[j] - w.Heights[i]) / 256.0, was = Math.Abs(pre[j] - pre[i]) / 256.0;
                        if (d > was + 0.01) worst = Math.Max(worst, d);
                    }
                }
            Console.WriteLine($"       {name}: river {site.RiverWidth:0} m, bridge water {site.WaterWidth} m ({(site.TooWide ? "narrowed" : "fits")}); water +{added} / −{removed} m², biggest new step {worst:0.00} m");
            Picture(w, site.Center, 110, Path.Combine(outDir, name + "_after.png"));
        }
    }

    [Test("Bridge swap: a one-piece bridge narrows a too wide river (only when asked for, at most to half its width) – long transition, no steps, the river goes on, no dry land below the water level; undo exact")]
    static void NarrowRiver()
    {
        var w = RiverWorld(out var e, 30, 32);
        // one piece of 31 m with 8 m land under each end: 15 m of water
        var k = new BridgeKitDef { Id = "narrow_one", Label = "narrow_one", End = "narrow_one_end", Active = true, Width = 12, EndLength = 31, TileLength = 0,
            Clearance = 4, DeckLayer = 9, LandUnder = 8, TerrainColumns = 0 };
        k.EndDef = new PrefabDef { SizeX = 31, SizeY = 17, SizeZ = 12, Found = true, Kind = PrefabKind.Bridge };
        var plain = Bridge.Find(w, 0, -100, k, RoadNetwork.NetMaxAngle);
        Check.True(plain.Problem != null && plain.TooWide, "without narrowing: too wide");
        var site = Bridge.Find(w, 0, -100, k, RoadNetwork.NetMaxAngle, narrow: true);
        Check.True(site.Problem == null, "with narrowing possible: " + site.Problem);
        Check.True(site.NarrowFrom >= 26 && site.WaterWidth == 15, $"narrowed from {site.NarrowFrom:0} m to {site.WaterWidth} m");
        Console.WriteLine($"       river {site.NarrowFrom:0} m → {site.WaterWidth} m, {site.NarrowPerSide:0.0} m per bank, transition {Bridge.BlendFor(site)} m");
        // the swap list shows it as possible (orange), a 6 m bridge water would be more than half: not possible
        var opts = BridgeSwap.Options(w, Bridge.Find(w, 0, -100, Kit("wide_a", 10, 13, 10, 4, 10), RoadNetwork.NetMaxAngle), new[] { k });
        Check.True(opts[0].Site.Problem == null && opts[0].Site.NarrowFrom > 0, "swap option: narrowing");
        var tiny = new BridgeKitDef { Id = "tiny", Label = "tiny", End = "tiny_end", Active = true, Width = 10, EndLength = 22, TileLength = 0, Clearance = 4, DeckLayer = 9, LandUnder = 8 };
        tiny.EndDef = new PrefabDef { SizeX = 22, SizeY = 17, SizeZ = 10, Found = true, Kind = PrefabKind.Bridge };
        Check.True(Bridge.Find(w, 0, -100, tiny, RoadNetwork.NetMaxAngle, narrow: true).Problem != null, "6 m of water for a 28 m river: not possible");
        string before = Fixture.State(w);
        var pre = (ushort[])w.Heights.Clone();
        e.Run("bridge", "", ed => Bridge.Build(w, ed, site, k.EndDef, null));
        // biggest new step outside the bridge (water edges excepted: the shoreline is a step by rule)
        double worst = 0; int wx = 0, wz = 0; bool wWet = false;
        for (int s = site.S0 - 150; s <= site.S0 + site.Length + 150; s++)
            for (int t = site.T0 - 150; t <= site.T0 + site.Width + 150; t++)
            {
                if (s >= site.S0 - 1 && s <= site.S0 + site.Length && t >= site.T0 - 1 && t <= site.T0 + site.Width) continue;
                int x = site.AlongX ? s : t, z = site.AlongX ? t : s, i = w.Index(x, z);
                foreach (int j in new[] { w.Index(x + 1, z), w.Index(x, z + 1) })
                {
                    if ((w.WaterMask[i] != 0) != (w.WaterMask[j] != 0)) continue;
                    double d = Math.Abs(w.Heights[j] - w.Heights[i]) / 256.0, was = Math.Abs(pre[j] - pre[i]) / 256.0;
                    if (d > was + 0.01 && d > worst) { worst = d; wx = x; wz = z; wWet = w.WaterMask[i] != 0; }
                }
            }
        Console.WriteLine($"       biggest new step {worst:0.00} m at {wx}/{wz} ({(wWet ? "under water" : "dry")}), bridge {site.S0}…{site.S0 + site.Length} along, middle {site.MidT:0} across, along x {site.AlongX}");
        Check.True(worst <= 1.5, $"step of {worst:0.00} m");
        // the river goes on: water in every row across it (up- and downstream and under the bridge), at least as wide as under the bridge
        int narrowest = int.MaxValue;
        for (int t = (int)site.MidT - 140; t <= (int)site.MidT + 140; t += 4)
        {
            int n = 0;
            for (int s = site.S0 - 30; s <= site.S0 + site.Length + 30; s++) { int x = site.AlongX ? s : t, z = site.AlongX ? t : s; if (w.WaterAt(x, z)) n++; }
            narrowest = Math.Min(narrowest, n);
        }
        Check.True(narrowest >= site.WaterWidth - 1, $"river narrower than the bridge water somewhere ({narrowest} m)");
        Check.Equal(0, RiverTests.Spills(w, site.Center, 160), "dry cells below the water level beside the water");
        e.Undo();
        Check.Equal(before, Fixture.State(w), "undo exact");
    }

    [Test("Bridge ends: a built bridge gives a snap point at the outer end of each approach; a road ending there keeps the height of the approach, a too steep one reports its step")]
    static void BridgeEndSnap()
    {
        var w = RiverWorld(out var e);
        var ka = Kit("bend_a", 10, 13, 10, 4, 10);
        var site = Bridge.Find(w, 0, -100, ka, RoadNetwork.NetMaxAngle);
        Check.True(site.Problem == null, "place for the bridge: " + site.Problem);
        e.Run("bridge", "", ed => Bridge.Build(w, ed, site, ka.EndDef, ka.TileDef));
        var ends = BridgeEnds.Find(w, new[] { ka });
        Check.Equal(2, ends.Count, "approach ends");
        var (pa, _, h) = NetworkBuilder.ApproachEnds(site);
        var end = BridgeEnds.At(ends, pa);
        Check.True(end != null && Math.Abs(end.Height - h) < 0.01, "end found with the height of the approach");
        Check.True(BridgeEnds.At(ends, pa - end.Outward * 8) == end, "a point on the approach counts as the end too");
        Check.True(BridgeEnds.At(ends, pa + end.Outward * 30) == null, "30 m out is no bridge end");
        var s = RoadSettings.Gravel();
        s.FlatEnd = 4;
        // reachable: 60 m out, terrain there
        var far = pa + end.Outward * 60;
        var r = RoadPlanner.Compute(w, new[] { far, pa }, s, new double?[] { null, end.Height });
        Check.True(Math.Abs(r.Profile[^1] - h) < 0.05, $"road ends at the approach height: {r.Profile[^1]:0.00} – {h:0.00}");
        Check.Equal(0, r.Steps.Count, "steps when reachable");
        // not reachable: the far end fixed 20 m higher on 30 m
        var near = pa + end.Outward * 30;
        r = RoadPlanner.Compute(w, new[] { near, pa }, s, new double?[] { h + 20, end.Height });
        Check.True(r.Steps.Count > 0 && r.Steps.Max(st => st.Height) > 5, "too steep: the step is reported (" + string.Join(", ", r.Steps.Select(st => st.Height.ToString("0.0"))) + ")");
        // the old behaviour: the end on the approach free, it gives way and lies above the surface
        r = RoadPlanner.Compute(w, new[] { near, pa - end.Outward * 2 }, RoadSettings.Gravel(), new double?[] { h + 20, null });
        Check.True(r.Steps.Count > 0, "a free end that gave way on the approach is reported");
    }

    [Test("Bridge ends in the route tools: a click on the approach snaps to its outer end, the route starts there at the approach height")]
    static void BridgeEndRoute()
    {
        var w = RiverWorld(out var e);
        var ka = Kit("bend_r", 10, 13, 10, 4, 10);
        var site = Bridge.Find(w, 0, -100, ka, RoadNetwork.NetMaxAngle);
        Check.True(site.Problem == null, "place for the bridge: " + site.Problem);
        e.Run("bridge", "", ed => Bridge.Build(w, ed, site, ka.EndDef, ka.TileDef));
        var ends = BridgeEnds.Find(w, new[] { ka });
        var (pa, _, h) = NetworkBuilder.ApproachEnds(site);
        var be = BridgeEnds.At(ends, pa);
        var spec = RouteTests.Spec();
        var net = RoadNetwork.ForRoutes(w, spec);
        net.BridgeEndList = ends;
        var s = net.RouteEndAt(pa - be.Outward * 6, 20, out string problem);
        Check.True(s?.Exit?.Bridge == be, "click on the approach snaps to the bridge end: " + (s == null ? problem : RoadNetwork.EndText(s)));
        Check.True((s.P - pa).Length < 0.01 && Math.Abs(s.Exit.Height.Value - h) < 0.01, "point and height of the approach end");
        var t = RouteTests.FreeEnd(net, pa + be.Outward * 140);
        var list = net.RouteVariants(s, t, Array.Empty<Point>(), true, 6, null, out problem);
        Check.True(list.Count > 0, "route found: " + problem);
        var road = list[0].Road;
        Check.True(road.From?.Bridge == be, "route starts at the bridge end");
        var r = NetworkBuilder.PlanRoad(w, new NetworkPlan(), road, spec, new Dictionary<NetRoad, RoadResult>(), new NetworkBuildResult());
        Check.True(r != null && Math.Abs(r.Profile[0] - h) < 0.05, $"route starts at the approach height: {r?.Profile[0]:0.00} – {h:0.00}");
    }

    [Test("Bridge swap: a T-junction and an X-crossing near the bridge are reconnected cleanly (both arms, no bulge past the far road edge, no ridge)")]
    static void SwapWithCrossings()
    {
        var w = RiverWorld(out var e);
        var ka = Kit("swapc_a", 10, 13, 10, 4, 10);
        var kb = Kit("swapc_b", 10, 13, 10, 6, 12);   // 2 m higher deck so the road & branches have to re-grade
        BridgeKits.RememberPieces(new[] { ka, kb });
        var prevKits = RoadNetwork.Bridges;
        RoadNetwork.Bridges = new BridgeKits(new[] { ka, kb });
        try
        {
            var spec = RouteTests.Spec();
            BridgeSite site = null; double bestDh = double.MaxValue;
            for (int x = -200; x <= 200; x += 10)
            {
                var s = Bridge.Find(w, x, -100, ka, RoadNetwork.NetMaxAngle);
                if (s.Problem != null) continue;
                double dh = new[] { -1, 1 }.Max(sg => Math.Abs(w.HeightAt((int)s.Center.X, (int)(s.Center.Y + sg * (s.Length / 2.0 + 130))) - (s.Deck - 1)));
                if (dh < bestDh) { bestDh = dh; site = s; }
            }
            Check.True(site != null, "no place for kit a");
            var (pa, pb, _) = NetworkBuilder.ApproachEnds(site);
            var u = site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
            var n = new Vector(-u.Y, u.X);
            // main road (8 m asphalt) over the bridge
            var main = new NetRoad { Points = PlannedLine(site, 260), Bridges = new() { site }, Width = 8, Manual = true };
            main.Length = (main.Points.Count - 1) * 16.0;
            // wider T-branch (14 m asphalt) on one side 24 m beyond approach A (too close for the joint in front of it)
            var tMouth = pa - u * 24;
            var tBranch = new NetRoad { Points = Enumerable.Range(0, 10).Select(i => tMouth + n * (144 - i * 16)).ToList(), Width = 14, Manual = true, EndRoad = main, Junction = tMouth };
            tBranch.Length = 144;
            // X-crossing (10 m asphalt, both sides!) 36 m beyond approach B
            var xMouth = pb + u * 36;
            var xRoad = new NetRoad { Points = Enumerable.Range(-8, 17).Select(i => xMouth + n * (i * 16)).ToList(), Width = 10, Manual = true };
            xRoad.Length = 256;
            var job = new RouteJob();
            job.Roads.Add(main);
            job.Roads.Add(tBranch);
            job.Roads.Add(xRoad);
            e.Run("road with T and X", "", ed => RouteBuilder.Build(w, ed, job, spec, null));
            // verify the far side of the main road opposite the T-branch has no surface before the swap (just outside the 8 m road + 1 m gravel)
            int farCheck = 0;
            for (int off = -8; off <= 8; off++)
            {
                var p = tMouth + u * off - n * 7;
                if (w.RoadAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)) != 0) farCheck++;
            }
            Check.Equal(0, farCheck, "far side of T-junction empty before swap");

            var piece = w.PrefabSnapshot().First(p => p.Name == "swapc_a_tile");
            var bb = BridgeSwap.FromPieces(w, piece, new[] { ka, kb });
            Check.True(bb != null, "bridge found");
            BridgeSwapResult res = null;
            e.Run("swap", "", ed => res = BridgeSwap.Build(w, ed, bb, kb, spec, null));
            Check.True(res != null, "swapped");
            Check.True(res.Branches >= 3, $"T-branch + both arms of X-crossing reconnected (got {res.Branches})");
            // 1. no bulge sticking out behind the far side of the main road at the T-junction
            int farAfter = 0;
            for (int off = -8; off <= 8; off++)
            {
                var p = tMouth + u * off - n * 7;
                if (w.RoadAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)) != 0) farAfter++;
            }
            Check.Equal(0, farAfter, $"no bulge past the far road edge of the T-junction ({farAfter} cells)");
            // 2. no cliff across the main road at either mouth (T or X)
            double maxStep = 0;
            foreach (var mouth in new[] { tMouth, xMouth })
                for (int s = -16; s <= 16; s++)
                    for (int t = -5; t <= 5; t++)
                    {
                        var p = mouth + u * s + n * t;
                        int x = (int)Math.Floor(p.X), z = (int)Math.Floor(p.Y);
                        if (w.RoadAt(x, z) == 0) continue;
                        if (w.RoadAt(x + 1, z) != 0) maxStep = Math.Max(maxStep, Math.Abs(w.HeightAt(x + 1, z) - w.HeightAt(x, z)));
                        if (w.RoadAt(x, z + 1) != 0) maxStep = Math.Max(maxStep, Math.Abs(w.HeightAt(x, z + 1) - w.HeightAt(x, z)));
                    }
            Console.WriteLine($"       reconnected branches: {res.Branches}, max step across T/X mouths: {maxStep:0.00} m");
            Check.True(maxStep < 0.45, $"smooth surface across T and X mouths without cliff/ridge ({maxStep:0.00} m)");
            // 3. no crescent gap behind the outer start (Points[0]) of any reconnected branch (user screenshot 2026-11-27, Land-Of-Fear 3561/2153)
            foreach (var br in res.BranchRoads)
            {
                var p0 = br.Points[0];
                var fwd = br.Points[1] - p0;
                fwd.Normalize();
                var lat = new Vector(-fwd.Y, fwd.X);
                int empty = 0, missingAsphalt = 0;
                double halfCheck = Math.Max(2, br.Width / 2 - 1.5);
                for (double along = -14; along <= 14; along += 1)
                    for (double across = -halfCheck; across <= halfCheck; across += 1)
                    {
                        var q = p0 + fwd * along + lat * across;
                        byte k = w.RoadAt((int)Math.Floor(q.X), (int)Math.Floor(q.Y));
                        if (k == 0) empty++;
                        if (k != WorldData.Asphalt) missingAsphalt++;
                    }
                Check.Equal(0, empty, $"no empty gap around outer start {p0.X:0},{p0.Y:0} of reconnected branch ({empty} empty cells)");
                Check.Equal(0, missingAsphalt, $"continuous asphalt around outer start {p0.X:0},{p0.Y:0} of reconnected branch ({missingAsphalt} non-asphalt cells)");
            }
        }
        finally { RoadNetwork.Bridges = prevKits; }
    }

    [Test("Bridge swap: narrow (3 m) gravel branches (straight and angled) off an asphalt bridge road are detected and reconnected without gaps or widening")]
    static void SwapWithGravelBranch()
    {
        var w = RiverWorld(out var e);
        var ka = Kit("swapg_a", 12, 13, 10, 4, 10);
        var kb = Kit("swapg_b", 10, 13, 10, 6, 12);   // narrower & 2 m higher deck
        BridgeKits.RememberPieces(new[] { ka, kb });
        var prevKits = RoadNetwork.Bridges;
        RoadNetwork.Bridges = new BridgeKits(new[] { ka, kb });
        try
        {
            var spec = RouteTests.Spec();
            BridgeSite site = null; double bestDh = double.MaxValue;
            for (int x = -200; x <= 200; x += 10)
            {
                var s = Bridge.Find(w, x, -100, ka, RoadNetwork.NetMaxAngle);
                if (s.Problem != null) continue;
                double dh = new[] { -1, 1 }.Max(sg => Math.Abs(w.HeightAt((int)s.Center.X, (int)(s.Center.Y + sg * (s.Length / 2.0 + 130))) - (s.Deck - 1)));
                if (dh < bestDh) { bestDh = dh; site = s; }
            }
            Check.True(site != null, "no place for kit a");
            var (pa, pb, _) = NetworkBuilder.ApproachEnds(site);
            var u = site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
            var n = new Vector(-u.Y, u.X);
            // wide 16 m asphalt road over the bridge
            var main = new NetRoad { Points = PlannedLine(site, 260), Bridges = new() { site }, Width = 16, Manual = true };
            main.Length = (main.Points.Count - 1) * 16.0;
            // 1. straight 3 m gravel path branching off 24 m beyond approach A (like Minimi -376,-265)
            var m1 = pa - u * 24;
            var g1 = new NetRoad { Points = Enumerable.Range(0, 10).Select(i => m1 + n * (144 - i * 16)).ToList(), Width = 3, Gravel = true, Manual = true, EndRoad = main, Junction = m1 };
            g1.Length = 144;
            // 2. angled/curved 3 m gravel path branching off 28 m beyond approach B at ~55°
            var m2 = pb + u * 28;
            var dir2 = -n * 0.82 + u * 0.57;
            dir2.Normalize();
            var g2Pts = new List<Point>();
            for (int i = 9; i >= 1; i--) g2Pts.Add(m2 + dir2 * (i * 16) + u * (i > 4 ? (i - 4) * 2.5 : 0));
            g2Pts.Add(m2);
            var g2 = new NetRoad { Points = g2Pts, Width = 3, Gravel = true, Manual = true, EndRoad = main, Junction = m2 };
            g2.Length = 150;
            var job = new RouteJob();
            job.Roads.Add(main);
            job.Roads.Add(g1);
            job.Roads.Add(g2);
            e.Run("road with gravel branches", "", ed => RouteBuilder.Build(w, ed, job, spec, null));
            string before = Fixture.State(w);

            var piece = w.PrefabSnapshot().First(p => p.Name == "swapg_a_tile");
            var bb = BridgeSwap.FromPieces(w, piece, new[] { ka, kb });
            Check.True(bb != null, "bridge found");
            BridgeSwapResult res = null;
            e.Run("swap", "", ed => res = BridgeSwap.Build(w, ed, bb, kb, spec, null));
            Check.True(res != null, "swapped");
            Console.WriteLine($"       gravel branches reconnected: {res.Branches} (" + string.Join(", ", res.BranchRoads.Select(r => $"gravel={r.Gravel} w={r.Width}")) + ")");
            Check.Equal(2, res.Branches, $"both 3 m gravel branches (straight + angled) reconnected (got {res.Branches})");
            foreach (var br in res.BranchRoads)
            {
                Check.True(br.Gravel, "reconnected branch stays gravel");
                Check.True(br.Width <= 4, $"narrow 3 m gravel branch stays narrow (width {br.Width})");
                // continuous surface every 1 m from outer start - 10 m right into the main road (zero gap!)
                int gap = 0, badAsphalt = 0;
                double maxStep = 0;
                var line = br.Points;
                for (int q = 1; q < line.Count; q++)
                {
                    var v = line[q] - line[q - 1]; double l = v.Length;
                    double prevH = double.NaN;
                    for (double s = 0; s <= l; s += 1)
                    {
                        var pq = line[q - 1] + v * (l < 1e-6 ? 0 : s / l);
                        int px = (int)Math.Floor(pq.X), pz = (int)Math.Floor(pq.Y);
                        byte rk = w.RoadAt(px, pz);
                        if (rk == 0) gap++;
                        if ((pq - br.Junction.Value).Length > 12 && rk == WorldData.Asphalt) badAsphalt++;
                        double hh = w.HeightAt(px, pz);
                        if (!double.IsNaN(prevH)) maxStep = Math.Max(maxStep, Math.Abs(hh - prevH));
                        prevH = hh;
                    }
                }
                // also check behind outer start (Points[0]) for crescent gap
                var p0 = line[0];
                var fwd = line[1] - p0; fwd.Normalize();
                for (double s = -10; s <= 10; s += 1)
                {
                    var pq = p0 + fwd * s;
                    if (w.RoadAt((int)Math.Floor(pq.X), (int)Math.Floor(pq.Y)) == 0) gap++;
                }
                Console.WriteLine($"         branch from {p0.X:0},{p0.Y:0} to {br.Junction.Value.X:0},{br.Junction.Value.Y:0}: gap={gap}, badAsphalt={badAsphalt}, maxStep={maxStep:0.00} m");
                Check.Equal(0, gap, $"zero gap along reconnected gravel branch ({gap} empty cells)");
                Check.Equal(0, badAsphalt, $"no asphalt painted on gravel branch ({badAsphalt} cells)");
                Check.True(maxStep < 0.45, $"smooth gravel branch transition ({maxStep:0.00} m)");
            }
            e.Undo();
            Check.Equal(before, Fixture.State(w), "undo exact");
        }
        finally { RoadNetwork.Bridges = prevKits; }
    }

    [Test("Bridge swap: banked outer joints keep their sideways banking and clean edges without a mound or asphalt spikes")]
    static void SwapWithBankedJoint()
    {
        var w = RiverWorld(out var e);
        var ka = Kit("swapbk_a", 12, 13, 10, 4, 10);
        var kb = Kit("swapbk_b", 10, 13, 10, 6, 12);   // narrower & 2 m higher deck
        BridgeKits.RememberPieces(new[] { ka, kb });
        var prevKits = RoadNetwork.Bridges;
        RoadNetwork.Bridges = new BridgeKits(new[] { ka, kb });
        try
        {
            var spec = RouteTests.Spec();
            BridgeSite site = null; double bestDh = double.MaxValue;
            for (int x = -200; x <= 200; x += 10)
            {
                var s = Bridge.Find(w, x, -100, ka, RoadNetwork.NetMaxAngle);
                if (s.Problem != null) continue;
                double dh = new[] { -1, 1 }.Max(sg => Math.Abs(w.HeightAt((int)s.Center.X, (int)(s.Center.Y + sg * (s.Length / 2.0 + 130))) - (s.Deck - 1)));
                if (dh < bestDh) { bestDh = dh; site = s; }
            }
            Check.True(site != null, "no place for kit a");
            var (pa, pb, _) = NetworkBuilder.ApproachEnds(site);
            var u = site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
            var n = new Vector(-u.Y, u.X);
            var main = new NetRoad { Points = PlannedLine(site, 260), Bridges = new() { site }, Width = 16, Manual = true };
            main.Length = (main.Points.Count - 1) * 16.0;
            var job = new RouteJob();
            job.Roads.Add(main);
            e.Run("road", "", ed => RouteBuilder.Build(w, ed, job, spec, null));
            // Give the road around the outer joint (pb + u * 70..180) a strong 11 % sideways banking (like a hillside curve in Minimi -301,-250)
            var m1 = pb + u * 75;
            var m2 = pb + u * 165;
            var rp = RoadReprofile.Plan(w, new[] { m1, m2 }, new double?[] { null, null }, new ReprofileOptions { Even = true }, cross: new double?[] { 0.11, 0.11 });
            Check.True(rp.Error == null && rp.Changes, "apply banking: " + rp.Error);
            e.Run("bank", "", ed => RoadReprofile.Apply(w, ed, rp, new Progress<string>(_ => { })));
            string before = Fixture.State(w);

            var piece = w.PrefabSnapshot().First(p => p.Name == "swapbk_a_tile");
            var bb = BridgeSwap.FromPieces(w, piece, new[] { ka, kb });
            Check.True(bb != null, "bridge found");
            BridgeSwapResult res = null;
            e.Run("swap", "", ed => res = BridgeSwap.Build(w, ed, bb, kb, spec, null));
            Check.True(res?.Road != null, "swapped");
            var joint = res.Road.Points[^1];
            // Check smoothness across the banked joint (full 16 m road + 3 m roadside verge, ±15 m along the road)
            double maxStep = 0; int bareAsphalt = 0;
            for (int along = -15; along <= 15; along++)
                for (int across = -11; across <= 11; across++)
                {
                    var p = joint + u * along + n * across;
                    int x = (int)Math.Floor(p.X), z = (int)Math.Floor(p.Y);
                    if (!w.Contains(x, z)) continue;
                    byte rk = w.RoadAt(x, z);
                    if (rk == WorldData.Asphalt)
                    {
                        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                            if (w.RoadAt(x + dx, z + dz) == 0) bareAsphalt++;
                    }
                    if (Math.Abs(across) <= 10)
                    {
                        foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
                            if (w.RoadAt(x, z) != 0 || w.RoadAt(x + dx, z + dz) != 0)
                                maxStep = Math.Max(maxStep, Math.Abs(w.HeightAt(x + dx, z + dz) - w.HeightAt(x, z)));
                    }
                }
            // Also check banking right before the joint (-4 m) vs right after (+4 m)
            double BankAt(double off)
            {
                var p = joint + u * off;
                var pl = p + n * 7; var pr = p - n * 7;
                return (w.HeightAt((int)Math.Floor(pl.X), (int)Math.Floor(pl.Y)) - w.HeightAt((int)Math.Floor(pr.X), (int)Math.Floor(pr.Y))) / 14.0;
            }
            double bankBefore = BankAt(-4), bankAfter = BankAt(4);
            Console.WriteLine($"       banked joint at {joint.X:0},{joint.Y:0}: maxStep={maxStep:0.00} m, bareAsphalt={bareAsphalt}, bank -4m={bankBefore * 100:0.0} % / +4m={bankAfter * 100:0.0} %");
            Check.True(maxStep < 0.35, $"smooth banked outer joint without mound/cliff ({maxStep:0.00} m)");
            Check.Equal(0, bareAsphalt, $"no asphalt spikes touching bare ground at joint ({bareAsphalt})");
            Check.True(Math.Abs(bankBefore - bankAfter) < 0.03, $"banking continuous across joint ({bankBefore * 100:0.0} % vs {bankAfter * 100:0.0} %)");
            e.Undo();
            Check.Equal(before, Fixture.State(w), "undo exact");
        }
        finally { RoadNetwork.Bridges = prevKits; }
    }

    [Test("Bridge swap: repeated swaps on a wide curving road cut into a steep hillside do not build a growing mound, cliff, or lose a 3 m gravel branch")]
    static void SwapRepeatedInHillsideCut()
    {
        var w = RiverWorld(out var e);
        var ka = Kit("swaprep_a", 14, 13, 10, 4, 10);
        var kb = Kit("swaprep_b", 10, 13, 10, 5, 11);   // narrow 10 m & 1 m higher deck (like yamee_10x10)
        BridgeKits.RememberPieces(new[] { ka, kb });
        var prevKits = RoadNetwork.Bridges;
        RoadNetwork.Bridges = new BridgeKits(new[] { ka, kb });
        try
        {
            var spec = RouteTests.Spec();
            BridgeSite site = null; double bestDh = double.MaxValue;
            for (int x = -60; x <= 60; x += 10)
            {
                var s = Bridge.Find(w, x, -100, ka, RoadNetwork.NetMaxAngle);
                if (s.Problem != null) continue;
                double dh = new[] { -1, 1 }.Max(sg => Math.Abs(w.HeightAt((int)s.Center.X, (int)(s.Center.Y + sg * (s.Length / 2.0 + 130))) - (s.Deck - 1)));
                if (dh < bestDh) { bestDh = dh; site = s; }
            }
            Check.True(site != null, "no place for kit a");
            var (pa, pb, _) = NetworkBuilder.ApproachEnds(site);
            var u = site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
            var n = new Vector(-u.Y, u.X);

            // Raise a steep 5 m hillside beside the curving side A (like Minimi -562,-240) BEFORE building the road so the road is cut into the hillside
            var curvePts = new List<Point>();
            var straightA = pa - u * RoadNetwork.BridgeLead;
            var diagDir = -u * 0.78 + n * 0.625; diagDir.Normalize();
            var diagN = new Vector(-diagDir.Y, diagDir.X);
            // Build curving 16 m road: straight from approach B, over bridge, straight 24 m past approach A, then curving ~39° diagonally across the hillside
            var roadPts = new List<Point>();
            for (int k = 12; k >= 1; k--)
            {
                double t = Math.Clamp(k / 3.0, 0, 1);
                var p = straightA + (-u * (1 - t) + diagDir * t) * (k * 16.0);
                roadPts.Add(p);
                curvePts.Add(p);
            }
            roadPts.Add(straightA);
            roadPts.Add(pa);
            roadPts.Add(pb);
            for (int k = 1; k <= 12; k++) roadPts.Add(pb + u * (k * 16.0));

            // Raise terrain 5 m on the hillside (+n / +diagN) side of side A (outside the river)
            double hillX0 = curvePts.Min(p => p.X) - 40, hillX1 = curvePts.Max(p => p.X) + 40;
            double hillZ0 = curvePts.Min(p => p.Y) - 40, hillZ1 = curvePts.Max(p => p.Y) + 40;
            for (int z = (int)hillZ0; z <= (int)hillZ1; z++)
                for (int x = (int)hillX0; x <= (int)hillX1; x++)
                {
                    if (!w.Contains(x, z)) continue;
                    var q = new Point(x + 0.5, z + 0.5);
                    if ((q - pa) * (-u) < 28) continue;
                    // find signed lateral offset from the curving road centerline
                    double bestD = double.MaxValue, signed = 0;
                    for (int i = 1; i < curvePts.Count; i++)
                    {
                        var a0 = curvePts[i - 1]; var v = curvePts[i] - a0; double ll = v.LengthSquared;
                        double tt = ll < 1e-9 ? 0 : Math.Clamp((q - a0) * v / ll, 0, 1);
                        var cl = a0 + v * tt;
                        double d = (q - cl).Length;
                        if (d < bestD)
                        {
                            bestD = d;
                            var vn = new Vector(-v.Y, v.X); vn.Normalize();
                            signed = (q - cl) * vn;
                        }
                    }
                    // hillside rises +5 m on the positive lateral side starting 6 m from road center
                    if (signed > 6)
                    {
                        double rise = Math.Min(5.0, (signed - 6) * 0.35);
                        int idx = w.Index(x, z);
                        w.Heights[idx] = (ushort)Math.Clamp(w.Heights[idx] + (int)Math.Round(rise * 256), 0, 65535);
                    }
                }

            var main = new NetRoad { Points = roadPts, Bridges = new() { site }, Width = 16, Manual = true };
            main.Length = 420;
            // Also add a 3 m gravel branch 28 m past approach B (like Minimi -366,-263)
            var bm = pb + u * 28;
            var gr = new NetRoad { Points = Enumerable.Range(0, 10).Select(i => bm + n * (144 - i * 16)).ToList(), Width = 3, Gravel = true, Manual = true, EndRoad = main, Junction = bm };
            gr.Length = 144;
            var job = new RouteJob();
            job.Roads.Add(main);
            job.Roads.Add(gr);
            e.Run("hillside bridge road", "", ed => RouteBuilder.Build(w, ed, job, spec, null));

            // Record baseline road heights along the hillside cut before any swaps
            var probePts = Enumerable.Range(1, 7).Select(i => curvePts[Math.Min(curvePts.Count - 1, i)]).ToList();
            var baseH = probePts.Select(p => w.HeightAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y))).ToArray();
            string before = Fixture.State(w);

            // Swap 4 times in a row without undoing: ka → kb → ka → kb → ka
            var order = new[] { kb, ka, kb, ka };
            for (int step = 0; step < order.Length; step++)
            {
                var curKit = step == 0 ? ka : order[step - 1];
                var piece = w.PrefabSnapshot().First(p => p.Name == curKit.Tile);
                var bb = BridgeSwap.FromPieces(w, piece, new[] { ka, kb });
                Check.True(bb != null, $"step {step + 1}: bridge found");
                BridgeSwapResult res = null;
                e.Run($"swap {step + 1}", "", ed => res = BridgeSwap.Build(w, ed, bb, order[step], spec, null));
                if (res == null)
                {
                    var rf = BridgeSwap.Refit(w, bb.Site, order[step], w.PrefabSnapshot().Where(q => !bb.Pieces.Contains(q)).ToList(), BridgeSwap.OldCorridor(bb.Site));
                    Check.True(false, $"step {step + 1}: BridgeSwap.Build returned null (refit problem: {rf.Problem})");
                }
                Check.True(res.Road != null, $"step {step + 1}: road rebuilt ({string.Join(" | ", res.Warnings)})");
                Check.Equal(1, res.Branches, $"step {step + 1}: 3 m gravel branch stays reconnected (got {res.Branches})");
                // Check smoothness across the curving hillside approach & joint after every single swap
                double maxStep = 0;
                foreach (var p in res.Road.Points)
                    for (int dz = -8; dz <= 8; dz++)
                        for (int dx = -8; dx <= 8; dx++)
                        {
                            int x = (int)Math.Floor(p.X) + dx, z = (int)Math.Floor(p.Y) + dz;
                            if (!w.Contains(x, z) || w.RoadAt(x, z) == 0) continue;
                            if (w.Contains(x + 1, z) && w.RoadAt(x + 1, z) != 0) maxStep = Math.Max(maxStep, Math.Abs(w.HeightAt(x + 1, z) - w.HeightAt(x, z)));
                            if (w.Contains(x, z + 1) && w.RoadAt(x, z + 1) != 0) maxStep = Math.Max(maxStep, Math.Abs(w.HeightAt(x, z + 1) - w.HeightAt(x, z)));
                        }
                Console.WriteLine($"       repeat #{step + 1} → {order[step].Id}: maxStep={maxStep:0.00} m, branches={res.Branches}");
                Check.True(maxStep < 0.35, $"step {step + 1}: no cliff/step across curving hillside road ({maxStep:0.00} m)");
            }

            // After 4 swaps back to ka, check that no growing Kuppe/mound formed in the hillside cut
            double maxDrift = 0;
            for (int i = 0; i < probePts.Count; i++)
            {
                double hNow = w.HeightAt((int)Math.Floor(probePts[i].X), (int)Math.Floor(probePts[i].Y));
                maxDrift = Math.Max(maxDrift, Math.Abs(hNow - baseH[i]));
            }
            Console.WriteLine($"       max height drift after 4 swaps in 5 m hillside cut: {maxDrift:0.00} m");
            Check.True(maxDrift < 0.35, $"no growing mound/Kuppe after 4 repeated swaps in hillside cut (drift {maxDrift:0.00} m)");

            for (int step = 0; step < order.Length; step++) e.Undo();
            Check.Equal(before, Fixture.State(w), "undo exact across 4 repeated swaps");
        }
        finally { RoadNetwork.Bridges = prevKits; }
    }

    [Test("Bridge swap: compatible kits keep the existing deck height and change zero terrain heights along the existing road")]
    static void SwapSameDeckZeroHeightDrift()
    {
        var w = RiverWorld(out var e);
        var ka = Kit("swapsd_a", 14, 13, 10, 7, 13); ka.BedAbove = 1.5;   // Deck = L + 7
        var kb = Kit("swapsd_b", 10, 13, 10, 4, 10); kb.BedAbove = 1.5;   // Clearance 4, DeckLayer 10 → supports [L+4 .. L+7]
        BridgeKits.RememberPieces(new[] { ka, kb });
        var prevKits = RoadNetwork.Bridges;
        RoadNetwork.Bridges = new BridgeKits(new[] { ka, kb });
        try
        {
            var spec = RouteTests.Spec();
            var site = Bridge.Find(w, 0, -100, ka, RoadNetwork.NetMaxAngle);
            Check.True(site.Problem == null, "place for kit a: " + site.Problem);
            var main = new NetRoad { Points = PlannedLine(site, 260), Bridges = new() { site }, Width = 16, Manual = true };
            main.Length = (main.Points.Count - 1) * 16.0;
            var job = new RouteJob(); job.Roads.Add(main);
            e.Run("road", "", ed => RouteBuilder.Build(w, ed, job, spec, null));

            var preHeights = (ushort[])w.Heights.Clone();
            string before = Fixture.State(w);
            var piece = w.PrefabSnapshot().First(p => p.Name == "swapsd_a_tile");
            var bb = BridgeSwap.FromPieces(w, piece, new[] { ka, kb });
            Check.True(bb != null, "bridge found");
            BridgeSwapResult res = null;
            e.Run("swap", "", ed => res = BridgeSwap.Build(w, ed, bb, kb, spec, null));
            Check.True(res != null, "swapped");
            Check.Equal(bb.Site.Deck, res.Site.Deck, "compatible kit keeps existing deck height");

            // Verify 0 height cells changed along the road beyond the new bridge approaches (from 2 m past approach end to 200 m out)
            var (na, nb, _) = NetworkBuilder.ApproachEnds(res.Site);
            var u = res.Site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
            var n = new Vector(-u.Y, u.X);
            int changedRoadHeights = 0;
            foreach (var (end, dir) in new[] { (na, -u), (nb, u) })
                for (int d = 2; d <= 200; d++)
                    for (int lat = -6; lat <= 6; lat++)
                    {
                        var q = end + dir * d + n * lat;
                        int x = (int)Math.Floor(q.X), z = (int)Math.Floor(q.Y);
                        if (!w.Contains(x, z) || w.RoadAt(x, z) == 0) continue;
                        if (w.Heights[w.Index(x, z)] != preHeights[w.Index(x, z)]) changedRoadHeights++;
                    }
            Console.WriteLine($"       kept deck {bb.Site.Deck} → {res.Site.Deck}, changed road height cells beyond approaches: {changedRoadHeights}");
            Check.Equal(0, changedRoadHeights, $"zero road height changes when deck height is preserved ({changedRoadHeights} cells)");
            e.Undo();
            Check.Equal(before, Fixture.State(w), "undo exact");
        }
        finally { RoadNetwork.Bridges = prevKits; }
    }

    [Test("Bridge swap: a forced 2 m lower bridge smoothly lowers a 3 m gravel branch 15 m behind the approach without touching its surface")]
    static void SwapDropTwoMetersBranchAt15m()
    {
        var w = RiverWorld(out var e);
        var ka = Kit("swapdr_a", 12, 13, 10, 6, 12);
        var kb = Kit("swapdr_b", 12, 13, 10, 4, 5);   // flat kit: maxDeck = L + 4 → forced -2 m drop
        BridgeKits.RememberPieces(new[] { ka, kb });
        var prevKits = RoadNetwork.Bridges;
        RoadNetwork.Bridges = new BridgeKits(new[] { ka, kb });
        try
        {
            var spec = RouteTests.Spec();
            var site = Bridge.Find(w, 0, -100, ka, RoadNetwork.NetMaxAngle);
            Check.True(site.Problem == null, "place for kit a: " + site.Problem);
            var (pa, _, _) = NetworkBuilder.ApproachEnds(site);
            var u = site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
            var n = new Vector(-u.Y, u.X);
            var main = new NetRoad { Points = PlannedLine(site, 260), Bridges = new() { site }, Width = 12, Manual = true };
            main.Length = (main.Points.Count - 1) * 16.0;
            var mouth = pa - u * 15;
            var branch = new NetRoad { Points = Enumerable.Range(0, 10).Select(i => mouth + n * (144 - i * 16)).ToList(), Width = 3, Gravel = true, Manual = true, EndRoad = main, Junction = mouth };
            branch.Length = 144;
            var job = new RouteJob(); job.Roads.Add(main); job.Roads.Add(branch);
            e.Run("road with close branch", "", ed => RouteBuilder.Build(w, ed, job, spec, null));

            var preRoad = (byte[])w.RoadMask.Clone();
            double hMouthBefore = w.HeightAt((int)Math.Floor(mouth.X), (int)Math.Floor(mouth.Y));
            string before = Fixture.State(w);

            var piece = w.PrefabSnapshot().First(p => p.Name == "swapdr_a_tile");
            var bb = BridgeSwap.FromPieces(w, piece, new[] { ka, kb });
            Check.True(bb != null, "bridge found");
            BridgeSwapResult res = null;
            e.Run("swap -2m", "", ed => res = BridgeSwap.Build(w, ed, bb, kb, spec, null));
            Check.True(res != null, "swapped");
            Check.Equal(bb.Site.Deck - 2, res.Site.Deck, "forced -2 m deck drop");
            double hMouthAfter = w.HeightAt((int)Math.Floor(mouth.X), (int)Math.Floor(mouth.Y));
            Check.True(hMouthAfter < hMouthBefore - 1.0, $"mouth 15 m behind approach smoothly lowered ({hMouthBefore:0.00} → {hMouthAfter:0.00})");

            // Check branch surface is 100% untouched (0 gravel cells lost or turned to asphalt) and height transitions smoothly
            int surfaceChanged = 0; double maxStep = 0;
            for (int d = 8; d <= 130; d++)
            {
                var q = mouth + n * d;
                int x = (int)Math.Floor(q.X), z = (int)Math.Floor(q.Y);
                if (w.RoadMask[w.Index(x, z)] != preRoad[w.Index(x, z)]) surfaceChanged++;
                if (d > 8)
                {
                    var qp = mouth + n * (d - 1);
                    maxStep = Math.Max(maxStep, Math.Abs(w.HeightAt(x, z) - w.HeightAt((int)Math.Floor(qp.X), (int)Math.Floor(qp.Y))));
                }
            }
            Console.WriteLine($"       mouth 15 m behind bridge: {hMouthBefore:0.00} → {hMouthAfter:0.00} m, branch surface changed {surfaceChanged} px, maxStep {maxStep:0.00} m");
            Check.Equal(0, surfaceChanged, $"branch surface 100% untouched ({surfaceChanged} px changed)");
            Check.True(maxStep < 0.35, $"smooth ramp along branch ({maxStep:0.00} m)");
            e.Undo();
            Check.Equal(before, Fixture.State(w), "undo exact");
        }
        finally { RoadNetwork.Bridges = prevKits; }
    }
}
