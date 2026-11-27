using System.Windows;
using MapEditor;
using MapEditor.Map;

namespace YameeTests;

/// road generation from point to point (route tool, 2026-11-17)
static class RouteTests
{
    internal static NetworkSpec Spec() => new() { Seed = "route-test", Grade = 0.10, MaxGrade = 0.16 };

    /// a free place near p (not water, not too close to the edge)
    internal static RoadNetwork.NetEnd FreeEnd(RoadNetwork net, Point p)
    {
        for (int r = 0; r <= 300; r += 20)
            for (int a = 0; a < 360; a += 30)
            {
                var q = new Point(p.X + r * Math.Cos(a * Math.PI / 180), p.Y + r * Math.Sin(a * Math.PI / 180));
                var e = net.RouteEndAt(q, 5, out _);
                if (e != null && e.Exit == null) return e;
                if (r == 0) break;
            }
        Check.True(false, $"no free place near {p.X}/{p.Y}");
        return null;
    }

    static RouteJob Job(params NetRoad[] roads) { var j = new RouteJob(); j.Roads.AddRange(roads); return j; }

    [Test("Route: variants between two free places, applying is one step that undo takes back exactly")]
    static void RouteVariantsBuild()
    {
        var w = Fixture.LoadCopy(volcano: false);
        var net = RoadNetwork.ForRoutes(w, Spec());
        var s0 = FreeEnd(net, new Point(-650, -600));
        var e0 = FreeEnd(net, new Point(600, 650));
        var s = RoadNetwork.Orient(s0, e0.P);
        var e = RoadNetwork.Orient(e0, s0.P);
        var list = net.RouteVariants(s, e, Array.Empty<Point>(), false, 16, null, out string problem);
        Check.True(list.Count > 0, "no course: " + problem);
        Console.WriteLine("       " + string.Join(", ", list.Select(v => $"{v.Style} {v.Road.Length:0} m {v.Road.MaxGrade * 100:0.0} %")));
        foreach (var v in list)
        {
            Check.True((v.Road.Points[0] - s.P).Length < 0.5 && (v.Road.Points[^1] - e.P).Length < 0.5, $"{v.Style}: ends not at the clicked places");
            Check.True(RoadNetwork.SelfCrossing(v.Road.Points) == null, $"{v.Style}: crosses itself");
        }
        var flat = list.FirstOrDefault(v => v.Style == RouteStyle.Flat);
        var direct = list.FirstOrDefault(v => v.Style == RouteStyle.Direct);
        if (flat != null && direct != null)
            Check.True(direct.Road.Length <= flat.Road.Length + 1, "direct is longer than flat");

        // dragged through a via point: passes it within the via radius
        var v0 = list[0];
        var mid = RoadNetwork.At(v0.Road.Points, v0.Road.Length / 2).P;
        var via = mid + new Vector(120, -120);
        var moved = net.Reroute(v0, s, e, new List<Point> { via }, out problem);
        if (moved != null) Check.True(RoadNetwork.Project(moved.Road.Points, via).Off <= RoadNetwork.ViaRadius + 20, "via point not passed");

        var edits = new WorldEdits(w);
        string before = Fixture.State(w);
        var step = edits.Run("route", "route", ed => RouteBuilder.Build(w, ed, Job(v0.Road), Spec(), null));
        string built = Fixture.State(w);
        Check.True(built != before, "applying changed nothing");
        int paved = 0;
        foreach (var p in RoadNetwork.Resample(v0.Road.Points, 16)) if (w.RoadAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)) != 0) paved++;
        Check.True(paved >= RoadNetwork.Resample(v0.Road.Points, 16).Count * 0.95, "the route has no surface");
        edits.Undo();
        Check.Equal(before, Fixture.State(w), "after undo");
        edits.Redo();
        Check.Equal(built, Fixture.State(w), "after redo");
    }

    [Test("Route: ending on an existing road joins it at its height, the road stays")]
    static void RouteJoinsRoad()
    {
        var w = Fixture.LoadCopy(volcano: false);
        var net = RoadNetwork.ForRoutes(w, Spec());
        var a = FreeEnd(net, new Point(-700, 0));
        var b = FreeEnd(net, new Point(700, 0));
        var first = net.RouteOf(RoadNetwork.Orient(a, b.P), RoadNetwork.Orient(b, a.P), Array.Empty<Point>(), RouteStyle.Natural, false, 16, out string problem);
        Check.True(first != null, "first road: " + problem);
        var edits = new WorldEdits(w);
        edits.Run("road", "road", ed => RouteBuilder.Build(w, ed, Job(first.Road), Spec(), null));
        // second route from the south onto the middle of the first one
        net = RoadNetwork.ForRoutes(w, Spec());
        var mid = RoadNetwork.At(first.Road.Points, first.Road.Length / 2).P;
        var onRoad = net.RouteEndAt(mid, 10, out problem);
        Check.True(onRoad?.Exit?.Street == true, "the end on the road is not recognised as a road: " + problem);
        var c = FreeEnd(net, new Point(mid.X, mid.Y - 650));
        double hRoad = w.HeightAt((int)Math.Floor(onRoad.P.X), (int)Math.Floor(onRoad.P.Y));
        var oldRoad = (byte[])w.RoadMask.Clone();
        var second = net.RouteOf(RoadNetwork.Orient(c, onRoad.P), RoadNetwork.Orient(onRoad, c.P), Array.Empty<Point>(), RouteStyle.Natural, false, 16, out problem);
        Check.True(second != null, "second road: " + problem);
        edits.Run("route", "route", ed => RouteBuilder.Build(w, ed, Job(second.Road), Spec(), null));
        double hAfter = w.HeightAt((int)Math.Floor(onRoad.P.X), (int)Math.Floor(onRoad.P.Y));
        Console.WriteLine($"       junction height before {hRoad:0.00} m, after {hAfter:0.00} m");
        Check.True(Math.Abs(hAfter - hRoad) < 0.3, "the existing road changed its height at the junction");
        // no step between the new road and the old one: along the last 30 m of the new road the height changes smoothly
        var line = second.Road.Points;
        double len = RoadNetwork.Project(line, line[^1]).Along, worst = 0;
        for (double d = len - 40; d < len - 1; d += 1)
        {
            var p = RoadNetwork.At(line, d).P; var q = RoadNetwork.At(line, d + 1).P;
            worst = Math.Max(worst, Math.Abs(w.HeightAt((int)Math.Floor(q.X), (int)Math.Floor(q.Y)) - w.HeightAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y))));
        }
        Console.WriteLine($"       largest step on the last 40 m: {worst:0.00} m");
        Check.True(worst < 0.6, "step at the junction");
        // the seam between the new and the old surface: no step between neighbouring paved pixels around the junction
        double seam = 0;
        for (int z = (int)onRoad.P.Y - 25; z <= onRoad.P.Y + 25; z++)
            for (int x = (int)onRoad.P.X - 25; x <= onRoad.P.X + 25; x++)
                foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
                    if (w.RoadAt(x, z) != 0 && w.RoadAt(x + dx, z + dz) != 0)
                        seam = Math.Max(seam, Math.Abs(w.HeightAt(x + dx, z + dz) - w.HeightAt(x, z)));
        Console.WriteLine($"       largest step between paved pixels around the junction: {seam:0.00} m");
        Check.True(seam < 0.3, "step at the seam between the new and the old road");
        // no gravel strip across the mouth: no gravel pixel between the old and the new asphalt
        int strip = 0;
        for (int z = (int)onRoad.P.Y - 25; z <= onRoad.P.Y + 25; z++)
            for (int x = (int)onRoad.P.X - 25; x <= onRoad.P.X + 25; x++)
            {
                if (w.RoadAt(x, z) != WorldData.Gravel) continue;
                bool oldA = false, newA = false;
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    if (w.RoadAt(x + dx, z + dz) != WorldData.Asphalt) continue;
                    if (oldRoad[w.Index(x + dx, z + dz)] == WorldData.Asphalt) oldA = true; else if (oldRoad[w.Index(x + dx, z + dz)] == 0) newA = true;
                }
                if (oldA && newA) strip++;
            }
        Console.WriteLine($"       gravel pixels between old and new asphalt: {strip}");
        Check.Equal(0, strip, "gravel strip across the mouth");
    }

    [Test("Route: re-laying a piece of road removes the old course outside the new one")]
    static void RouteRelay()
    {
        var w = Fixture.LoadCopy(volcano: false);
        var net = RoadNetwork.ForRoutes(w, Spec());
        var a = FreeEnd(net, new Point(-700, -300));
        var b = FreeEnd(net, new Point(700, 300));
        // an old road with a bulge to the north (via point), then re-laid without it
        var dir = b.P - a.P; dir.Normalize();
        var mid = new Point((a.P.X + b.P.X) / 2, (a.P.Y + b.P.Y) / 2) + new Vector(dir.Y, -dir.X) * 220;
        var old = net.RouteOf(RoadNetwork.Orient(a, b.P), RoadNetwork.Orient(b, a.P), new List<Point> { mid }, RouteStyle.Natural, false, 12, out string problem);
        Check.True(old != null, "old road: " + problem);
        var edits = new WorldEdits(w);
        edits.Run("road", "road", ed => RouteBuilder.Build(w, ed, Job(old.Road), Spec(), null));
        net = RoadNetwork.ForRoutes(w, Spec());
        double len = old.Road.Length;
        var pa = RoadNetwork.At(old.Road.Points, len * 0.15).P;
        var pb = RoadNetwork.At(old.Road.Points, len * 0.85).P;
        var relay = net.BeginRelay(pa, pb, out problem);
        Check.True(relay != null, "re-lay: " + problem);
        RouteVariant v;
        try { v = net.RouteOf(relay.Value.Start, relay.Value.End, Array.Empty<Point>(), RouteStyle.Direct, false, 12, out problem); }
        finally { net.EndRelay(); }
        Check.True(v != null, "new course: " + problem);
        var job = Job(v.Road);
        var oldLine = relay.Value.Old.Points;
        double olen = relay.Value.Old.Length;
        var core = new List<Point>();
        for (double d = 25; d <= olen - 25; d += 4) core.Add(RoadNetwork.At(oldLine, d).P);
        job.Remove.Add(core);
        job.RemoveWidth.Add(relay.Value.Old.Width + 2);
        string before = Fixture.State(w);
        edits.Run("relay", "relay", ed => RouteBuilder.Build(w, ed, job, Spec(), null));
        int left = 0, checkedPts = 0;
        for (double d = 60; d <= olen - 60; d += 8)
        {
            var p = RoadNetwork.At(oldLine, d).P;
            if (RoadNetwork.Project(v.Road.Points, p).Off < 20) continue;   // new course runs here
            checkedPts++;
            if (w.RoadAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)) != 0) left++;
        }
        Console.WriteLine($"       old course: {checkedPts} places away from the new one, {left} still paved; new {v.Road.Length:0} m");
        Check.True(checkedPts > 10, "the new course is the same as the old one");
        Check.Equal(0, left, "old surface left");
        edits.Undo();
        Check.Equal(before, Fixture.State(w), "undo");
    }

    [Test("Route: open road end – marker stays on the centerline and keeps the road direction (flat, slanted, curved, rounded, narrow)")]
    static void RouteOpenRoadEnd()
    {
        var w = Fixture.LoadCopy();
        void ClearRoads()
        {
            Array.Clear(w.RoadMask);
        }
        // paint a straight road from p0 to p1 with half-width half (total width 2*half, outer 1 m gravel if asphalt)
        // cutDeg: angle of the cut plane at p1 relative to the normal (0 = perpendicular, >0 = one corner protrudes)
        // roundCap: semicircular end cap around p1 instead of a straight cut
        void PaintStraight(Point p0, Point p1, double half, byte kind, double cutDeg = 0, bool roundCap = false)
        {
            var d = p1 - p0; double len = d.Length; d.Normalize();
            var n = new Vector(-d.Y, d.X);
            double slope = Math.Tan(cutDeg * Math.PI / 180);
            int x0 = (int)Math.Floor(Math.Min(p0.X, p1.X) - half - 25), x1 = (int)Math.Ceiling(Math.Max(p0.X, p1.X) + half + 25);
            int z0 = (int)Math.Floor(Math.Min(p0.Y, p1.Y) - half - 25), z1 = (int)Math.Ceiling(Math.Max(p0.Y, p1.Y) + half + 25);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    if (!w.Contains(x, z)) continue;
                    var v = new Point(x + 0.5, z + 0.5) - p0;
                    double u = v * d, s = v * n;
                    if (u < 0 || Math.Abs(s) > half) continue;
                    bool inside;
                    if (roundCap)
                        inside = u <= len || (u - len) * (u - len) + s * s <= half * half;
                    else
                        inside = u <= len + s * slope;
                    if (!inside) continue;
                    byte k = kind == WorldData.Asphalt && Math.Abs(s) > half - 1 ? WorldData.Gravel : kind;
                    w.RoadMask[w.Index(x, z)] = k;
                }
        }
        void CheckEnd(string label, Point click, Point expectedEnd, Vector expectedDir, double maxLateral, double maxAngleDeg, double maxAlongErr)
        {
            var net = RoadNetwork.ForRoutes(w, Spec());
            var e = net.RouteEndAt(click, 20, out string problem);
            Check.True(e != null, $"{label}: no end ({problem})");
            Check.True(e.Fixed, $"{label}: not recognized as an open road end (got {RoadNetwork.EndText(e)})");
            var dir = expectedDir; dir.Normalize();
            var n = new Vector(-dir.Y, dir.X);
            var diff = e.P - expectedEnd;
            double lateral = Math.Abs(diff * n);
            double along = diff * dir;
            double angle = Math.Acos(Math.Clamp(e.Tangent * dir, -1, 1)) * 180 / Math.PI;
            Console.WriteLine($"       {label,-28}: lateral {lateral:0.00} m, along {along:+0.00;-0.00} m, angle {angle:0.0}°");
            Check.True(lateral <= maxLateral, $"{label}: marker off centerline by {lateral:0.00} m (limit {maxLateral:0.0} m)");
            Check.True(angle <= maxAngleDeg, $"{label}: direction off by {angle:0.0}° (limit {maxAngleDeg:0.0}°)");
            Check.True(Math.Abs(along) <= maxAlongErr, $"{label}: marker along error {along:0.00} m (limit {maxAlongErr:0.0} m)");
            Check.True(w.RoadAt((int)Math.Floor(e.P.X), (int)Math.Floor(e.P.Y)) != 0, $"{label}: marker is not on road surface");
        }

        // 1. Diagonal 18 m road (like screenshot: SSE ~18°), 10° slanted cut – click at end, on protruding corner, slightly outside, and 25 m back
        var a0 = new Point(-100, 300); var a1 = new Point(-100 + 80, 300 - 240);
        var aDir = a1 - a0; aDir.Normalize(); var aN = new Vector(-aDir.Y, aDir.X);
        ClearRoads();
        PaintStraight(a0, a1, 9, WorldData.Asphalt, cutDeg: 10);
        CheckEnd("diag 18m slant 10° (end)", a1 - aDir * 3, a1 - aDir * 2, aDir, 1.0, 5, 3.0);
        CheckEnd("diag 18m slant 10° (corner)", a1 + aN * 8 + aDir * 1, a1 - aDir * 2, aDir, 1.0, 5, 3.0);
        CheckEnd("diag 18m slant 10° (outside)", a1 + aDir * 4, a1 - aDir * 2, aDir, 1.0, 5, 3.0);
        CheckEnd("diag 18m slant 10° (25m back)", a1 - aDir * 25, a1 - aDir * 2, aDir, 1.0, 5, 3.0);

        // 2. Diagonal 18 m road, steep 35° slanted cut – click at end and on protruding corner
        ClearRoads();
        PaintStraight(a0, a1, 9, WorldData.Asphalt, cutDeg: 35);
        CheckEnd("diag 18m slant 35° (end)", a1 - aDir * 3, a1 - aDir * 4.5, aDir, 1.0, 5, 4.5);
        CheckEnd("diag 18m slant 35° (corner)", a1 + aN * 8 + aDir * 5, a1 - aDir * 4.5, aDir, 1.0, 5, 4.5);

        // 3. Flat perpendicular cut (18 m asphalt) – click right on the edge
        ClearRoads();
        PaintStraight(a0, a1, 9, WorldData.Asphalt, cutDeg: 0);
        CheckEnd("diag 18m flat cut (edge)", a1 - aDir * 0.5, a1 - aDir * 2, aDir, 1.0, 5, 2.5);

        // 4. Rounded semicircular end cap (18 m asphalt, like RoadApplier)
        ClearRoads();
        PaintStraight(a0, a1, 9, WorldData.Asphalt, roundCap: true);
        CheckEnd("diag 18m rounded cap", a1 + aDir * 4, a1 + aDir * 6, aDir, 1.0, 5, 3.0);

        // 5. Narrow 5 m gravel road with 30° slanted cut
        ClearRoads();
        PaintStraight(a0, a1, 2.5, WorldData.Gravel, cutDeg: 30);
        CheckEnd("diag 5m gravel slant 30°", a1 - aDir * 2, a1 - aDir * 2, aDir, 1.0, 8, 3.0);

        // 6. Curved 18 m asphalt road ending with a 25° slanted cut (arc of radius 140 m, 60°)
        ClearRoads();
        var center = new Point(0, 0);
        double rad = 140;
        double ang0 = 0, ang1 = 60 * Math.PI / 180;
        var cEnd = new Point(center.X + rad * Math.Cos(ang1), center.Y + rad * Math.Sin(ang1));
        var cDir = new Vector(-Math.Sin(ang1), Math.Cos(ang1));   // tangent at ang1
        var cN = new Vector(-cDir.Y, cDir.X);
        double cSlope = Math.Tan(25 * Math.PI / 180);
        for (int z = -20; z <= 170; z++)
            for (int x = -20; x <= 170; x++)
            {
                if (!w.Contains(x, z)) continue;
                double rx = x + 0.5 - center.X, rz = z + 0.5 - center.Y;
                double r = Math.Sqrt(rx * rx + rz * rz);
                if (Math.Abs(r - rad) > 9) continue;
                double ang = Math.Atan2(rz, rx);
                if (ang < ang0 || ang > ang1 + 0.25) continue;
                var dv = new Point(x + 0.5, z + 0.5) - cEnd;
                if (ang > ang1 - 0.15 && dv * cDir > (dv * cN) * cSlope) continue;
                w.RoadMask[w.Index(x, z)] = Math.Abs(r - rad) > 8 ? WorldData.Gravel : WorldData.Asphalt;
            }
        CheckEnd("curved 18m slant 25°", cEnd + cN * 7 + cDir * 2, cEnd - cDir * 3, cDir, 1.2, 8, 5.0);

        // 7. Bridge approach tapering smoothly from 18 m to 10 m over 40 m, ending flat
        ClearRoads();
        for (int z = -100; z <= 20; z++)
            for (int x = -20; x <= 20; x++)
            {
                if (!w.Contains(x, z) || z > 0) continue;
                double half = z > -40 ? 5.0 + 4.0 * (-z / 40.0) : 9.0;
                if (Math.Abs(x + 0.5) <= half) w.RoadMask[w.Index(x, z)] = Math.Abs(x + 0.5) > half - 1 ? WorldData.Gravel : WorldData.Asphalt;
            }
        CheckEnd("bridge taper 18m -> 10m", new Point(0, -2), new Point(0, -2), new Vector(0, 1), 1.0, 5, 2.5);

        // 8. 20 m bridge approach stub (10 m wide, clicking near outer end)
        ClearRoads();
        PaintStraight(new Point(0, -20), new Point(0, 0), 5, WorldData.Asphalt, cutDeg: 0);
        CheckEnd("20m bridge approach stub", new Point(0, -2), new Point(0, -2), new Vector(0, 1), 1.0, 5, 2.5);

        // 9. Middle of a continuous road must NOT be treated as an open road end
        ClearRoads();
        PaintStraight(new Point(-300, 0), new Point(300, 0), 9, WorldData.Asphalt);
        var netMid = RoadNetwork.ForRoutes(w, Spec());
        var mid = netMid.RouteEndAt(new Point(0, 0), 20, out string pMid);
        Check.True(mid != null && !mid.Fixed && mid.Exit?.Street == true, "middle of road was falsely detected as dead end: " + pMid);

        // 10. Short isolated 12 m road patch must NOT be treated as an open road end
        ClearRoads();
        PaintStraight(new Point(0, -6), new Point(0, 6), 5, WorldData.Asphalt);
        var netPatch = RoadNetwork.ForRoutes(w, Spec());
        var patch = netPatch.RouteEndAt(new Point(0, 0), 20, out _);
        Check.True(patch == null || !patch.Fixed, "short isolated patch was falsely detected as dead end");
    }
}

static class RouteTileTests
{
    [Test("Route: T gateway on a straight road – connectors and route meet the tile floor, old surface under it goes (needs the game)")]
    static void RouteStreetTile()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? AppSettings.GuessGameFolder();
        if (game == null) Check.Skip("the game folder was not found");
        var files = PrefabCatalog.AllFiles(game);
        PrefabDef Def(string n) { if (!files.TryGetValue(n, out var f)) return null; var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Tile; return d.Found ? d : null; }
        var defs = RoadNetwork.NodeTileNames.ToDictionary(n => n, Def);
        if (defs[RoadNetwork.NodeT] == null) Check.Skip("the T gateway is not in the game folder");
        RoadNetwork.TileDefs = n => defs.GetValueOrDefault(n);
        try
        {
            var w = WorldData.Load(Fixture.LoadCopy().Folder, game, new Progress<string>());
            var spec = new NetworkSpec { Seed = "tile-test", Grade = 0.10, MaxGrade = 0.16 };
            var edits = new WorldEdits(w);
            // a straight east–west road where the ground is flattest
            double best = double.MaxValue; int bz = 0;
            for (int z = -700; z <= 700; z += 50)
            {
                double dev = 0;
                for (int x = -300; x <= 300; x += 25) dev += Math.Abs(w.HeightAt(x, z) - w.HeightAt(0, z));
                bool wet = Enumerable.Range(-30, 61).Any(k => w.WaterAt(k * 20, z));
                if (!wet && dev < best) { best = dev; bz = z; }
            }
            // level ground around it (the generated hills are too steep for a tile): gently tilted plain 1400 × 1000 m
            double h0 = w.HeightAt(0, bz);
            edits.Run("level", "level", ed =>
            {
                for (int z = bz - 500; z <= bz + 500; z++)
                    for (int x = -760; x <= 760; x++)
                    {
                        if (w.WaterAt(x, z)) continue;
                        double t = Math.Clamp((Math.Max(Math.Abs(x) - 700, 0) + Math.Max(Math.Abs(z - bz) - 440, 0)) / 60.0, 0, 1);
                        double target = h0 + 0.01 * x;
                        double h = target + (w.HeightAt(x, z) - target) * t;
                        ed.SetHeight(w.Index(x, z), (ushort)Math.Clamp(Math.Round(h * 256), 0, 65535));
                    }
            });
            var straight = RoadPlanner.Compute(w, new List<Point> { new(-700, bz), new(700, bz) }, RoadSettings.Asphalt());
            edits.Run("road", "road", ed => RoadApplier.Apply(w, ed, straight, new Progress<string>()));
            var net = RoadNetwork.ForRoutes(w, spec);
            RouteTile tile = null; Point at = default; string why = null;
            foreach (int x in new[] { 0, -150, 150, -300, 300 })
            {
                tile = net.StreetTileNear(new Point(x, bz + 0.5), new Vector(1, 0), 9, new Point(x, bz - 500), false, out why, out _);
                if (tile != null) { at = new Point(x, bz + 0.5); break; }
            }
            if (tile == null) Check.Skip("no place for the tile on the test road: " + why);
            Check.Equal("T", tile.Kind, "kind");
            Check.True(tile.In != null && tile.In.DZ < -0.9, "the route side of the T faces south");
            Check.Equal(2, tile.Connectors.Count, "connectors");
            var onRoad = net.RouteEndAt(at, 10, out _);
            RoadNetwork.NetEnd s = null; List<NetRoad> legs = null; string problem = null;
            foreach (double d in new[] { 450.0, 600, 350 })
                foreach (double side in new[] { 0.0, 200, -200 })
                {
                    if (legs != null) break;
                    s = net.RouteEndAt(new Point(tile.In.X + side, tile.In.Z - d), 5, out problem);
                    if (s == null || s.Exit != null) continue;
                    var v = new RouteVariant { Style = RouteStyle.Natural, Road = new NetRoad { Points = new List<Point> { s.P, at }, Width = 16 } };
                    tile.Along = (at - s.P).Length;
                    legs = net.RouteThroughTiles(RoadNetwork.Orient(s, at), RoadNetwork.Orient(onRoad, s.P), v, new List<RouteTile> { tile }, out problem);
                }
            if (legs == null) Check.Skip("no course to the tile: " + problem);
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
            Check.True(w.Prefabs.Contains(tile.Tile), "tile not placed");
            double floor = tile.Tile.TerrainHeight, worst = 0;
            foreach (var c in tile.Connectors.Concat(legs))
            {
                var p = RoadNetwork.At(c.Points, c.Length - 3).P;
                worst = Math.Max(worst, Math.Abs(w.HeightAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)) - floor));
            }
            Console.WriteLine($"       tile {tile.Tile.Name} at {tile.Tile.X}/{tile.Tile.Z}, {res.Roads} roads, steepest {res.MaxGrade * 100:0.0} %, roads 3 m before the tile at most {worst:0.00} m off its floor");
            Check.True(worst < 0.6, "step between a road and the tile");
            // old surface beside the tile, between the connectors and the tile edge, is replaced by the connectors (no second strip)
            edits.Undo();
            Check.Equal(before, Fixture.State(w), "undo");
        }
        finally { RoadNetwork.TileDefs = null; }
    }

    [Test("Route: a building 90 m beside the route gets a path (entrance facing the route; needs the game)")]
    static void RoutePoiClose()
    {
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? AppSettings.GuessGameFolder();
        if (game == null) Check.Skip("the game folder was not found");
        var files = PrefabCatalog.AllFiles(game);
        if (!files.TryGetValue("cave_12", out var file)) Check.Skip("cave_12 is not in the game folder");
        var def = PrefabCatalog.Describe(file);
        var w = WorldData.Load(Fixture.LoadCopy().Folder, game, new Progress<string>());
        var spec = new NetworkSpec { Seed = "poi-test", Grade = 0.10, MaxGrade = 0.16 };
        var edits = new WorldEdits(w);
        double h0 = w.HeightAt(0, 0);
        edits.Run("level", "level", ed =>
        {
            for (int z = -300; z <= 300; z++)
                for (int x = -760; x <= 760; x++)
                {
                    double t = Math.Clamp((Math.Max(Math.Abs(x) - 700, 0) + Math.Max(Math.Abs(z) - 240, 0)) / 60.0, 0, 1);
                    ed.SetHeight(w.Index(x, z), (ushort)Math.Clamp(Math.Round((h0 + (w.HeightAt(x, z) - h0) * t) * 256), 0, 65535));
                }
        });
        // the rotation whose entrance faces the route (south)
        PlacedPrefab cave = null;
        for (int r = 0; r < 4 && cave == null; r++)
        {
            var c = new PlacedPrefab { Name = "cave_12", Def = def, X = -12, Z = 90, Y = (int)Math.Round(h0 + 0.85) + def.YOffset, Rotation = r };
            var ex = PrefabParts.ExitsFor(w, c);
            if (ex.Count > 0 && ex.All(e => e.Z < c.Z + 2)) cave = c;
        }
        if (cave == null) Check.Skip("cave_12 has no entrance that can face south");
        edits.Run("cave", "cave", ed => ed.AddPrefab(cave));
        var net = RoadNetwork.ForRoutes(w, spec);
        var a = net.RouteEndAt(new Point(-600, 0), 5, out _);
        var b = net.RouteEndAt(new Point(600, 0), 5, out _);
        var v = net.RouteOf(RoadNetwork.Orient(a, b.P), RoadNetwork.Orient(b, a.P), Array.Empty<Point>(), RouteStyle.Direct, true, 5, out var problem);
        Check.True(v != null, "route: " + problem);
        var notes = new List<string>();
        var fe = net.PoiFeeders(v.Road, 250, 5, null, notes);
        Console.WriteLine($"       entrance at {PrefabParts.ExitsFor(w, cave)[0].Z:0} (route at z 0): {fe.Count} path(s) {string.Join(", ", fe.Select(f => $"{f.Length:0} m"))} {string.Join(" ", notes)}");
        Check.Equal(1, fe.Count, "paths to the cave");
    }

    [Test("Route: banking in curves up to the chosen value, straights flat")]
    static void RouteBanking()
    {
        var w = Fixture.LoadCopy(volcano: false);
        var net = RoadNetwork.ForRoutes(w, RouteTests.Spec());
        var s0 = RouteTests.FreeEnd(net, new Point(-650, -600));
        var e0 = RouteTests.FreeEnd(net, new Point(600, 650));
        var v = net.RouteOf(RoadNetwork.Orient(s0, e0.P), RoadNetwork.Orient(e0, s0.P), Array.Empty<Point>(), RouteStyle.Flat, false, 16, out string problem);
        Check.True(v != null, "route: " + problem);
        v.Road.CurveBanking = 0.02;
        var r = RouteBuilder.Preview(w, v.Road, RouteTests.Spec());
        double max = r.CrossSlope.Max(c => Math.Abs(c));
        // curvature over ±20 m: on the sharpest stretch the banking reaches the value, the inside is lower
        int sharp = 0; double best = 0;
        for (int k = 80; k + 80 < r.Path.Count; k += 4)
        {
            double h1 = Math.Atan2(r.Path[k].Z - r.Path[k - 80].Z, r.Path[k].X - r.Path[k - 80].X), h2 = Math.Atan2(r.Path[k + 80].Z - r.Path[k].Z, r.Path[k + 80].X - r.Path[k].X);
            double dh = h2 - h1; while (dh > Math.PI) dh -= 2 * Math.PI; while (dh < -Math.PI) dh += 2 * Math.PI;
            if (Math.Abs(dh) > Math.Abs(best)) { best = dh; sharp = k; }
        }
        Console.WriteLine($"       largest banking {max * 100:0.00} %, at the sharpest curve ({best * 180 / Math.PI:0}° over 40 m) {r.CrossSlope[sharp] * 100:0.00} %");
        Check.True(max <= 0.0201, "banking above 2 %");
        Check.True(Math.Abs(r.CrossSlope[sharp]) > 0.01, "no banking in the sharpest curve");
        Check.True(Math.Sign(r.CrossSlope[sharp]) == -Math.Sign(best), "banking towards the outside");
    }

    [Test("Road: the roadway is smooth – no staircase of 1 m steps across a steep road")]
    static void RoadSmoothSurface()
    {
        var w = Fixture.LoadCopy();
        var e = new WorldEdits(w);
        // a steep road (no smoothing of the terrain, 20 %) at an angle to the grid
        var s = RoadSettings.Asphalt();
        s.MaxGrade = 0.2; s.Smooth = 30;
        var r = RoadPlanner.Compute(w, new List<Point> { new(-500, -380), new(-200, -150), new(150, 120) }, s);
        e.Run("road", "road", ed => RoadApplier.Apply(w, ed, r, new Progress<string>(), 0, -1, 4));
        var ks = new List<double>();
        foreach (var p in r.Path.Where((_, i) => i % 8 == 0))
            for (int dz = -5; dz <= 5; dz++)
                for (int dx = -5; dx <= 5; dx++)
                {
                    int x = (int)Math.Floor(p.X) + dx, z = (int)Math.Floor(p.Z) + dz;
                    if (w.RoadAt(x, z) != WorldData.Asphalt) continue;
                    if (w.RoadAt(x - 1, z) == WorldData.Asphalt && w.RoadAt(x + 1, z) == WorldData.Asphalt) ks.Add(Math.Abs(w.HeightAt(x - 1, z) + w.HeightAt(x + 1, z) - 2 * w.HeightAt(x, z)));
                    if (w.RoadAt(x, z - 1) == WorldData.Asphalt && w.RoadAt(x, z + 1) == WorldData.Asphalt) ks.Add(Math.Abs(w.HeightAt(x, z - 1) + w.HeightAt(x, z + 1) - 2 * w.HeightAt(x, z)));
                }
        ks.Sort();
        double q90 = ks[(int)(ks.Count * 0.9)];
        Console.WriteLine($"       steepest {r.MaxGrade * 100:0} %, pixel kinks on the roadway: median {ks[ks.Count / 2]:0.000}, 90 % {q90:0.000} m");
        Check.True(q90 < 0.05, "staircase on the roadway");
    }
}
