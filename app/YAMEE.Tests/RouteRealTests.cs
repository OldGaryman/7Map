using System.Windows;
using MapEditor;
using MapEditor.Map;

namespace YameeTests;

/// route tool on a real map with towns and roads (optional: YAMEE_TEST_ROUTE_WORLD, the folder is only read)
static class RouteRealTests
{
    [Test("Route (real map): close a dead end, T gateway at an existing road, paths to buildings")]
    static void RouteRealMap()
    {
        string folder = Environment.GetEnvironmentVariable("YAMEE_TEST_ROUTE_WORLD");
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? AppSettings.GuessGameFolder();
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "dtm.raw")) || game == null)
            Check.Skip("set YAMEE_TEST_ROUTE_WORLD to a project folder with towns and roads (it is only read)");
        var w = WorldData.Load(folder, game, new Progress<string>());
        var files = PrefabCatalog.AllFiles(game);
        PrefabDef Def(string n) { if (!files.TryGetValue(n, out var f)) return null; var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Tile; return d.Found ? d : null; }
        var defs = RoadNetwork.NodeTileNames.ToDictionary(n => n, Def);
        RoadNetwork.TileDefs = n => defs.GetValueOrDefault(n);
        var spec = new NetworkSpec { Seed = w.Name, Grade = 0.10, MaxGrade = 0.16 };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var net = RoadNetwork.ForRoutes(w, spec);
        Console.WriteLine($"       grid read in {watch.ElapsedMilliseconds} ms, {net.FreeTownExits.Count()} free town exits");
        var edits = new WorldEdits(w);

        // 1. dead end: automatic target and a course to it
        var exit = net.FreeTownExits.FirstOrDefault();
        if (exit != null)
        {
            var start = new RoadNetwork.NetEnd(exit, null, new Point(exit.X, exit.Z), new Vector(exit.DX, exit.DZ));
            watch.Restart();
            var target = net.AutoTarget(start, false, 16, null, out string problem);
            Console.WriteLine($"       dead end at {exit.X:0}/{exit.Z:0}: target {(target == null ? problem : RoadNetwork.EndText(target) + $" at {target.P.X:0}/{target.P.Y:0}")} ({watch.ElapsedMilliseconds} ms)");
            if (target != null)
            {
                var list = net.RouteVariants(RoadNetwork.Orient(start, target.P), RoadNetwork.Orient(target, start.P), Array.Empty<Point>(), false, 16, null, out problem);
                Console.WriteLine("       variants: " + string.Join(", ", list.Select(v => $"{v.Style} {v.Road.Length:0} m {v.Road.MaxGrade * 100:0.0} %")));
                Check.True(list.Count > 0, "no course to the target: " + problem);
                var feeders = net.PoiFeeders(list[0].Road, 400, 4, null);
                Console.WriteLine($"       paths to buildings within 400 m: {feeders.Count}");
            }
        }


        // 2. T gateway: places on asphalt roads outside the towns (at most 6 that fit); the first with a free place beside it gets a
        // course to the tile and is built with its connectors
        var scan = new RoadScan(w);
        var reasons = new Dictionary<string, int>();
        var found = new List<(RouteTile Tile, Point At)>();
        int tried = 0;
        for (int z = -w.Half + 400; z < w.Half - 400 && found.Count < 6 && tried < 1500; z += 96)
            for (int x = -w.Half + 400; x < w.Half - 400 && found.Count < 6 && tried < 1500; x += 32)
            {
                if (w.RoadAt(x, z) != WorldData.Asphalt || scan.InTile(x, z)) continue;
                var spot = RoadFollower.Center(scan, new Point(x, z));
                if (spot == null || spot.Width < 6 || found.Any(f => (f.At - spot.Center).Length < 400)) continue;
                tried++;
                var side = spot.Center + new Vector(-spot.Dir.Y, spot.Dir.X) * 400;
                var rt = net.StreetTile(spot.Center, spot.Dir, spot.Width / 2 + 1, side, false, out string why);
                if (rt == null) { reasons[why] = reasons.GetValueOrDefault(why) + 1; continue; }
                found.Add((rt, spot.Center));
            }
        Console.WriteLine($"       T gateway: {tried} places tried, fits at {found.Count}; "
            + string.Join("; ", reasons.OrderByDescending(r => r.Value).Take(4).Select(r => $"{r.Value} × {r.Key}")));
        bool builtOne = false;
        foreach (var (tile, at) in found)
        {
            if (builtOne) break;
            var nrm = tile.In;
            var onRoad = net.RouteEndAt(at, 10, out string problem);
            RoadNetwork.NetEnd s = null;
            List<NetRoad> legs = null;
            foreach (double d in new[] { 600.0, 450, 800, 350 })
                foreach (double sideways in new[] { 0.0, 200, -200 })
                {
                    if (legs != null) break;
                    var far = new Point(nrm.X + nrm.DX * d - nrm.DZ * sideways, nrm.Z + nrm.DZ * d + nrm.DX * sideways);
                    s = net.RouteEndAt(far, 5, out problem);
                    if (s == null || s.Exit != null) continue;
                    var v = new RouteVariant { Style = RouteStyle.Natural, Road = new NetRoad { Points = new List<Point> { s.P, at }, Width = 16 } };
                    tile.Along = (at - s.P).Length;
                    legs = net.RouteThroughTiles(RoadNetwork.Orient(s, at), RoadNetwork.Orient(onRoad, s.P), v, new List<RouteTile> { tile }, out problem);
                }
            if (legs == null) { Console.WriteLine($"       tile at {at.X:0}/{at.Y:0}: no course to it ({problem})"); continue; }
            builtOne = true;
            var job = new RouteJob();
            job.Tiles.Add(tile.Tile);
            job.Roads.AddRange(tile.Connectors);
            job.Roads.AddRange(legs);
            foreach (var o in tile.OldPieces)
            {
                var pts = new List<Point>();
                for (double d = 8; d <= o.Length - 8; d += 4) pts.Add(RoadNetwork.At(o.Points, d).P);
                job.Remove.Add(pts); job.RemoveWidth.Add(o.Width);
            }
            string before = Fixture.State(w);
            var res = new NetworkBuildResult();
            edits.Run("tile", "tile", ed => res = RouteBuilder.Build(w, ed, job, spec, null));
            Console.WriteLine($"       built at {at.X:0}/{at.Y:0}: {res.Roads} roads, {res.NodeTiles} tile, steepest {res.MaxGrade * 100:0.0} %; " + string.Join(" ", res.Warnings));
            // the connectors and the route meet the tile at its height (no step at the tile edge)
            double floor = tile.Tile.TerrainHeight, worst = 0;
            foreach (var c in tile.Connectors.Concat(legs.Take(1)))
            {
                var p = RoadNetwork.At(c.Points, c.Length - 3).P;
                worst = Math.Max(worst, Math.Abs(w.HeightAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)) - floor));
            }
            Console.WriteLine($"       roads 3 m before the tile: at most {worst:0.00} m off the tile floor");
            Check.True(worst < 0.6, "step between road and tile");
            // the old road under the tile is gone outside the tile (only the connectors are paved there)
            edits.Undo();
            Check.Equal(before, Fixture.State(w), "undo");
        }
        RoadNetwork.TileDefs = null;
    }
}
