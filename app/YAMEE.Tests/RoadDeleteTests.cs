using System.Windows;
using MapEditor;
using MapEditor.Map;

namespace YameeTests;

/// delete a road between two junctions (2026-11-23)
static class RoadDeleteTests
{
    [Test("Delete road: a click marks the piece up to the junction, deleting it gives the remaining asphalt road back its gravel edge")]
    static void DeleteToJunction()
    {
        var w = Fixture.LoadCopy();
        var spec = RouteTests.Spec();
        var net = RoadNetwork.ForRoutes(w, spec);
        var a = RouteTests.FreeEnd(net, new Point(-700, 0));
        var b = RouteTests.FreeEnd(net, new Point(700, 0));
        var first = net.RouteOf(RoadNetwork.Orient(a, b.P), RoadNetwork.Orient(b, a.P), Array.Empty<Point>(), RouteStyle.Natural, false, 16, out string problem);
        Check.True(first != null, "first road: " + problem);
        var edits = new WorldEdits(w);
        var job = new RouteJob(); job.Roads.Add(first.Road);
        edits.Run("road", "road", ed => RouteBuilder.Build(w, ed, job, spec, null));
        net = RoadNetwork.ForRoutes(w, spec);
        var mid = RoadNetwork.At(first.Road.Points, first.Road.Length / 2).P;
        var onRoad = net.RouteEndAt(mid, 10, out problem);
        Check.True(onRoad?.Exit?.Street == true, "end on the road: " + problem);
        var c = RouteTests.FreeEnd(net, new Point(mid.X, mid.Y - 600));
        var second = net.RouteOf(RoadNetwork.Orient(c, onRoad.P), RoadNetwork.Orient(onRoad, c.P), Array.Empty<Point>(), RouteStyle.Natural, false, 12, out problem);
        Check.True(second != null, "second road: " + problem);
        job = new RouteJob(); job.Roads.Add(second.Road);
        edits.Run("route", "route", ed => RouteBuilder.Build(w, ed, job, spec, null));

        // the main road beside the junction before deleting: surface and heights in a band ±30 m along it
        var main = first.Road.Points;
        double at = RoadNetwork.Project(main, onRoad.P).Along;
        var band = new List<(int I, byte Kind, ushort H, double Off)>();
        var seen = new HashSet<int>();
        for (double d = at - 30; d <= at + 30; d += 0.5)
        {
            var p = RoadNetwork.At(main, d).P; var q = RoadNetwork.At(main, d + 1).P;
            var t = q - p; t.Normalize(); var n = new Vector(-t.Y, t.X);
            for (double o = -9.5; o <= 9.5; o += 0.5)
            {
                var x = p + n * o;
                int i = w.Index((int)Math.Floor(x.X), (int)Math.Floor(x.Y));
                if (seen.Add(i)) band.Add((i, w.RoadMask[i], w.Heights[i], o));
            }
        }

        // click in the middle of the second road
        var click = RoadNetwork.At(second.Road.Points, second.Road.Length / 2).P;
        var seg = RoadSegment.Find(w, click, out problem);
        Check.True(seg != null, "nothing marked: " + problem);
        Console.WriteLine($"       marked {seg.Length:0} m, {seg.Width:0.#} m wide, from {seg.StartStop} to {seg.EndStop}, {seg.Joints.Count} junction(s)");
        foreach (var j in seg.Joints) Console.WriteLine($"       junction {j.Center.X:0}/{j.Center.Y:0}: gravel edge {j.GravelWidth:0.#} m");
        Check.True(seg.StartStop == "junction" || seg.EndStop == "junction", "the piece does not end at the junction");
        Check.Equal(1, seg.Joints.Count, "junctions to repair");
        var joint = seg.Joints[0];
        Check.True((joint.Center - onRoad.P).Length < 15, "junction found at the wrong place");
        Check.True(joint.Gravel != null && joint.Kind == WorldData.Asphalt, "remaining road not recognised as asphalt road running across the mouth");
        Check.True(Math.Abs(joint.GravelWidth - 1) <= 0.6, "gravel edge not measured as about 1 m");
        Check.True(seg.Length > second.Road.Length * 0.8, "marked piece much shorter than the road");

        // across the main road at a place along it, towards the side of the deleted road: asphalt width, gravel edge, surface just beyond
        (double Asphalt, double Gravel, bool Beyond) Across(double d)
        {
            var p = RoadNetwork.At(main, d).P; var q = RoadNetwork.At(main, d + 1).P;
            var t = q - p; t.Normalize(); var n = new Vector(-t.Y, t.X);
            if (n * (seg.Joints[0].End - p) < 0) n = -n;
            byte K(double o) { var x = p + n * o; return w.RoadAt((int)Math.Floor(x.X), (int)Math.Floor(x.Y)); }
            double hi = 0, lo = 0;
            while (hi < 30 && K(hi + 0.25) == WorldData.Asphalt) hi += 0.25;
            while (lo < 30 && K(-lo - 0.25) == WorldData.Asphalt) lo += 0.25;
            double g = 0;
            while (g < 10 && K(hi + g + 0.25) == WorldData.Gravel) g += 0.25;
            bool beyond = false;
            for (double o = hi + g + 1; o <= hi + g + 3; o += 0.25) if (K(o) != 0) beyond = true;
            return (hi + lo, g, beyond);
        }
        string before = Fixture.State(w);
        var step = edits.Run("delete", "delete", ed => RoadSegment.Delete(w, ed, seg, null));
        string after = Fixture.State(w);
        Check.True(after != before, "deleting changed nothing");

        // the deleted road has no surface left (except the last metres in front of the junction)
        int left = 0, probes = 0;
        foreach (var p in RoadNetwork.Resample(second.Road.Points, 8))
        {
            if ((p - onRoad.P).Length < 25) continue;
            probes++;
            if (w.RoadAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)) != 0) left++;
        }
        Console.WriteLine($"       surface left on the deleted road: {left} of {probes} probes");
        Check.True(left <= 1, "the deleted road still has surface");

        // the main road: heights exactly as before; asphalt and gravel edge continuous across the old mouth (as 40–60 m away)
        int heightChanged = 0;
        foreach (var (i, kind, h, off) in band) if (kind != 0 && Math.Abs(off) <= 7 && w.Heights[i] != h) heightChanged++;
        var refs = new List<(double A, double G)>();
        foreach (double d in new[] { -60.0, -50, -40, 40, 50, 60 }) { var r0 = Across(at + d); refs.Add((r0.Asphalt, r0.Gravel)); }
        double refA = refs.Select(v => v.A).OrderBy(v => v).ElementAt(3), refG = refs.Select(v => v.G).OrderBy(v => v).ElementAt(3);
        int badA = 0, beyondN = 0, stations = 0;
        for (double d = -15; d <= 15; d += 1)
        {
            var (a2, g2, b2) = Across(at + d);
            stations++;
            if (Math.Abs(a2 - refA) > 1.5) badA++;
            if (b2) beyondN++;
        }
        // pixel check around the junction: asphalt never borders bare ground (missing gravel edge), and no gravel lies away from the asphalt
        int bare = 0, stray = 0;
        int jx = (int)Math.Floor(onRoad.P.X), jz = (int)Math.Floor(onRoad.P.Y);
        for (int z = jz - 25; z <= jz + 25; z++)
            for (int x = jx - 25; x <= jx + 25; x++)
            {
                if ((x - jx) * (x - jx) + (z - jz) * (z - jz) > 625) continue;
                byte k = w.RoadAt(x, z);
                if (k == WorldData.Asphalt && new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(o => w.RoadAt(x + o.Item1, z + o.Item2) == 0)) bare++;
                if (k == WorldData.Gravel)
                {
                    bool nearAsphalt = false;
                    for (int dz = -2; dz <= 2 && !nearAsphalt; dz++)
                        for (int dx = -2; dx <= 2 && !nearAsphalt; dx++) nearAsphalt = w.RoadAt(x + dx, z + dz) == WorldData.Asphalt;
                    if (!nearAsphalt) stray++;
                }
            }
        Console.WriteLine($"       main road: {heightChanged} heights changed; reference asphalt {refA:0.#} m, gravel {refG:0.##} m; across the mouth {badA} asphalt widths off, {beyondN} with surface beyond the edge (of {stations}); {bare} asphalt pixels on bare ground, {stray} gravel pixels away from the asphalt");
        Check.Equal(0, heightChanged, "heights of the remaining road changed");
        Check.True(badA <= 1, "asphalt of the remaining road is not as wide as beside the junction");
        Check.True(bare <= 2, "gravel edge of the remaining road is not continuous across the old mouth (asphalt next to bare ground)");
        Check.True(stray <= 2, "gravel painted away from the asphalt");
        Check.True(beyondN == 0, "surface of the old mouth left beside the remaining road");

        // no step where the natural ground meets the repaired road edge
        double step0 = 0;
        int cx = (int)joint.Center.X, cz = (int)joint.Center.Y;
        for (int z = cz - 25; z <= cz + 25; z++)
            for (int x = cx - 25; x <= cx + 25; x++)
                foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
                    if ((w.RoadAt(x, z) != 0) != (w.RoadAt(x + dx, z + dz) != 0))
                        step0 = Math.Max(step0, Math.Abs(w.HeightAt(x + dx, z + dz) - w.HeightAt(x, z)));
        Console.WriteLine($"       largest step at the edge of the remaining road: {step0:0.00} m");
        Check.True(step0 < 1.0, "step at the edge of the remaining road");

        edits.Undo();
        Check.Equal(before, Fixture.State(w), "after undo");
        edits.Redo();
        Check.Equal(after, Fixture.State(w), "after redo");

        // the other case (the user's test 2026-11-23, Kuwoto-Land): delete one half of the through road – the remaining arms (the other
        // half and the branch) meet at a corner; the corner keeps its asphalt with a gravel edge and both arms stay joined
        edits.Undo();
        var east = RoadNetwork.At(main, Math.Min(at + 200, first.Road.Length - 100)).P;
        var half = RoadSegment.Find(w, east, out problem);
        Check.True(half != null, "half of the through road not marked: " + problem);
        Console.WriteLine($"       half of the through road: {half.Length:0} m, from {half.StartStop} to {half.EndStop}, junctions: "
            + string.Join(", ", half.Joints.Select(j => j.Gravel == null ? "left (" + j.Why + ")" : "edge rebuilt")));
        var corner = half.Joints.FirstOrDefault(j => (j.Center - onRoad.P).Length < 30);
        Check.True(corner?.Gravel != null, "the corner of the remaining arms is not rebuilt");
        int bareCorner = BareAsphalt(w, corner.Center);
        edits.Run("delete half", "delete half", ed => RoadSegment.Delete(w, ed, half, null));
        Console.WriteLine($"       corner: arms joined {Connected(w, corner)}, asphalt pixels on bare ground {bareCorner} before, {BareAsphalt(w, corner.Center)} after");
        Check.True(Connected(w, corner), "the remaining arms are cut apart at the corner");
        Check.True(BareAsphalt(w, corner.Center) <= bareCorner + 2, "gravel edge missing at the corner");
    }

    /// asphalt pixels next to bare ground within 25 m of a place (a missing gravel edge)
    internal static int BareAsphalt(WorldData w, Point c)
    {
        int n = 0, cx = (int)Math.Floor(c.X), cz = (int)Math.Floor(c.Y);
        for (int z = cz - 25; z <= cz + 25; z++)
            for (int x = cx - 25; x <= cx + 25; x++)
                if ((x - cx) * (x - cx) + (z - cz) * (z - cz) <= 625 && w.RoadAt(x, z) == WorldData.Asphalt
                    && new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(o => w.RoadAt(x + o.Item1, z + o.Item2) == 0)) n++;
        return n;
    }

    /// the two arms of the remaining road are still joined by surface (search over road pixels near the junction)
    internal static bool Connected(WorldData w, SegmentJoint j)
    {
        int r = j.Window + 10, cx = (int)Math.Floor(j.Center.X), cz = (int)Math.Floor(j.Center.Y);
        (int, int) A = ((int)Math.Floor(j.Arm0.X), (int)Math.Floor(j.Arm0.Y)), B = ((int)Math.Floor(j.Arm2.X), (int)Math.Floor(j.Arm2.Y));
        if (w.RoadAt(A.Item1, A.Item2) == 0 || w.RoadAt(B.Item1, B.Item2) == 0) return false;
        var seen = new HashSet<(int, int)> { A };
        var q = new Queue<(int, int)>();
        q.Enqueue(A);
        while (q.Count > 0)
        {
            var (x, z) = q.Dequeue();
            if ((x, z) == B) return true;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, nz = z + dz;
                if (Math.Abs(nx - cx) > r || Math.Abs(nz - cz) > r || w.RoadAt(nx, nz) == 0 || !seen.Add((nx, nz))) continue;
                q.Enqueue((nx, nz));
            }
        }
        return false;
    }

    [Test("Delete road (real map): pieces up to junctions are found and deleted, the remaining roads keep a continuous edge")]
    static void DeleteRealMap()
    {
        string folder = Environment.GetEnvironmentVariable("YAMEE_TEST_ROUTE_WORLD");
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? AppSettings.GuessGameFolder();
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "dtm.raw")) || game == null)
            Check.Skip("set YAMEE_TEST_ROUTE_WORLD to a project folder with towns and roads (it is only read)");
        var w = WorldData.Load(folder, game, new Progress<string>());
        var edits = new WorldEdits(w);
        var scan = new RoadScan(w);
        var done = new List<Point>();
        int tried = 0, rebuilt = 0, kept = 0, broken = 0, worstBare = 0;
        double worstStep = 0;
        for (int z = -w.Half + 200; z < w.Half - 200 && rebuilt < 40; z += 97)
            for (int x = -w.Half + 200; x < w.Half - 200 && rebuilt < 40; x += 97)
            {
                if (!scan.Road(x + 0.5, z + 0.5) || done.Any(p => (p - new Point(x, z)).Length < 300)) continue;
                var seg = RoadSegment.Find(w, new Point(x + 0.5, z + 0.5), out _);
                if (seg == null || seg.Length > 1500 || !seg.Joints.Any(j => j.Gravel != null)) continue;
                tried++;
                done.Add(new Point(x, z));
                var before = (byte[])w.RoadMask.Clone();
                var bareBefore = seg.Joints.Select(j => BareAsphalt(w, j.Center)).ToList();
                edits.Run("delete", "delete", e => RoadSegment.Delete(w, e, seg, null));
                foreach (var j in seg.Joints)
                {
                    if (j.Gravel == null) { kept++; continue; }
                    int bareAdded = BareAsphalt(w, j.Center) - bareBefore[seg.Joints.IndexOf(j)];
                    worstBare = Math.Max(worstBare, bareAdded);
                    rebuilt++;
                    // steps between a paved pixel and the ground beside it around the junction
                    double step = 0;
                    int cx = (int)j.Center.X, cz = (int)j.Center.Y;
                    for (int zz = cz - 25; zz <= cz + 25; zz++)
                        for (int xx = cx - 25; xx <= cx + 25; xx++)
                            foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
                                if (w.Contains(xx + dx, zz + dz) && (w.RoadAt(xx, zz) != 0) != (w.RoadAt(xx + dx, zz + dz) != 0)
                                    && (before[w.Index(xx, zz)] != w.RoadMask[w.Index(xx, zz)] || before[w.Index(xx + dx, zz + dz)] != w.RoadMask[w.Index(xx + dx, zz + dz)]))
                                    step = Math.Max(step, Math.Abs(w.HeightAt(xx + dx, zz + dz) - w.HeightAt(xx, zz)));
                    worstStep = Math.Max(worstStep, step);
                    bool joined = Connected(w, j);
                    if (!joined) broken++;
                    Console.WriteLine($"       {seg.Length:0} m from {x}/{z}: junction {j.Center.X:0}/{j.Center.Y:0}, {(j.Kind == WorldData.Asphalt ? "asphalt" : "gravel")}, gravel edge {j.GravelWidth:0.#} m, step at the changed edge {step:0.00} m, {bareAdded} more asphalt pixels on bare ground{(joined ? "" : ", REMAINING ROAD CUT")}");
                }
                edits.Undo();
            }
        Console.WriteLine($"       {tried} pieces deleted (and undone), {rebuilt} junctions rebuilt, {kept} left as they are; largest step {worstStep:0.00} m");
        Check.True(tried > 0, "no piece of road between junctions found on the map");
        Check.True(worstStep < 1.5, "step at a rebuilt edge");
        Check.Equal(0, broken, "remaining roads cut at the junction");
        Check.True(worstBare <= 3, "gravel edge missing at a rebuilt junction (asphalt next to bare ground)");
    }
    [Test("Delete road (one place): marks and deletes the piece at YAMEE_TEST_DELETE = folder|x,z and shows the junctions as text")]
    static void DeleteAtPlace()
    {
        string v = Environment.GetEnvironmentVariable("YAMEE_TEST_DELETE");
        string game = Environment.GetEnvironmentVariable("YAMEE_TEST_GAME") ?? AppSettings.GuessGameFolder();
        if (string.IsNullOrEmpty(v) || !v.Contains('|') || game == null) Check.Skip("set YAMEE_TEST_DELETE = project folder|x,z (the folder is only read)");
        var parts = v.Split('|');
        var xz = parts[1].Split(',');
        var click = new Point(double.Parse(xz[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xz[1], System.Globalization.CultureInfo.InvariantCulture));
        var w = WorldData.Load(parts[0], game, new Progress<string>());
        var edits = new WorldEdits(w);
        if (parts.Length > 2 && parts[2] == "dump") { Console.WriteLine($"       around {click.X:0}/{click.Y:0}:"); int cx0 = (int)click.X, cz0 = (int)click.Y; for (int z = cz0 + 40; z >= cz0 - 40; z--) { var sb = new System.Text.StringBuilder("       "); for (int x = cx0 - 60; x <= cx0 + 60; x++) sb.Append(w.RoadAt(x, z) switch { 2 => '#', 1 => ':', _ => '.' }); Console.WriteLine(sb); } return; }
        if (parts.Length > 3)
        {
            // build one road of the saved road plan again (the one nearest to the place), e.g. to get back a road that was deleted
            var files = PrefabCatalog.AllFiles(game);
            PrefabDef Def(string n) { if (!files.TryGetValue(n, out var f)) return null; var d = PrefabCatalog.Describe(f); d.Kind = PrefabKind.Tile; return d.Found ? d : null; }
            var defs = RoadNetwork.NodeTileNames.ToDictionary(n => n, Def);
            RoadNetwork.TileDefs = n => defs.GetValueOrDefault(n);
            var file = NetworkPlanStore.Load(w, out string why);
            Check.True(file != null, "road plan: " + why);
            var road = file.Plan.Roads.OrderBy(r => RoadNetwork.Project(r.Points, click).Off).First();
            var plan = new NetworkPlan { Towns = file.Plan.Towns };
            // with it every road of the plan that ends on it (their junctions are built again too), in the order of the plan
            plan.Roads.AddRange(file.Plan.Roads.Where(r => r == road || new[] { r.Points[0], r.Points[^1] }.Any(p => RoadNetwork.Project(road.Points, p).Off < 30)));
            Console.WriteLine($"       {plan.Roads.Count} road(s) of the plan built again");
            edits.Run("rebuild", "rebuild", ed => NetworkBuilder.Build(w, ed, plan, file.Spec, new Progress<string>()));
            Console.WriteLine($"       rebuilt road of {road.Length:0} m from {road.Points[0].X:0}/{road.Points[0].Y:0} to {road.Points[^1].X:0}/{road.Points[^1].Y:0}");
            click = RoadNetwork.At(road.Points, RoadNetwork.Project(road.Points, click).Along).P;
        }
        var seg = RoadSegment.Find(w, click, out string problem);
        Check.True(seg != null, "nothing marked: " + problem);
        Console.WriteLine($"       marked {seg.Length:0} m, {seg.Width:0.#} m wide, from {seg.StartStop} at {seg.Points[0].X:0}/{seg.Points[0].Y:0} to {seg.EndStop} at {seg.Points[^1].X:0}/{seg.Points[^1].Y:0}");
        // A / G = edge line of asphalt / gravel through the mouth (before deleting), S = centers where the edges were measured
        var marks = new Dictionary<(int, int), char>();
        foreach (var j in seg.Joints.Where(j => j.Gravel != null))
        {
            for (double t = 0; t <= 1; t += 0.002)
            {
                var pg = j.Gravel.At(t); marks[((int)Math.Floor(pg.X), (int)Math.Floor(pg.Y))] = 'G';
                if (j.Asphalt != null) { var pa = j.Asphalt.At(t); marks[((int)Math.Floor(pa.X), (int)Math.Floor(pa.Y))] = 'A'; }
            }
            marks[((int)Math.Floor(j.Arm0.X), (int)Math.Floor(j.Arm0.Y))] = 'S';
            marks[((int)Math.Floor(j.Arm2.X), (int)Math.Floor(j.Arm2.Y))] = 'S';
        }
        bool marking = true;
        void Dump(Point c)
        {
            int cx = (int)c.X, cz = (int)c.Y;
            for (int z = cz + 40; z >= cz - 60; z--)
            {
                var sb = new System.Text.StringBuilder("       ");
                for (int x = cx - 50; x <= cx + 50; x++) sb.Append(marking && marks.TryGetValue((x, z), out char m) ? m : w.RoadAt(x, z) switch { 2 => '#', 1 => ':', _ => '.' });
                Console.WriteLine(sb);
            }
        }
        var ends = new List<Point> { seg.Points[0], seg.Points[^1] };
        string extra = Environment.GetEnvironmentVariable("YAMEE_TEST_DELETE_SHOW");   // "x,z": show this place too
        if (extra != null) { var t = extra.Split(','); ends.Add(new Point(double.Parse(t[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture))); }
        foreach (var j in seg.Joints)
            Console.WriteLine($"       junction {j.Center.X:0}/{j.Center.Y:0} end {j.End.X:0}/{j.End.Y:0}: {(j.Gravel == null ? "left as it is (" + j.Why + ")" : $"edge rebuilt, gravel {j.GravelWidth:0.#} m, asphalt curve {j.Asphalt?.B0} → {j.Asphalt?.B3}")}, window {j.Window}");
        foreach (var e in ends) { Console.WriteLine($"       before, around {e.X:0}/{e.Y:0}:"); Dump(e); }
        marking = false;
        int Paved() => seg.Points.Count(p => w.RoadAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)) != 0);
        int pavedBefore = Paved();
        var st = edits.Run("delete", "delete", ed => RoadSegment.Delete(w, ed, seg, null));
        Console.WriteLine($"       paved points of the piece: {pavedBefore} before, {Paved()} after (of {seg.Points.Count})");
        foreach (var e in ends) { Console.WriteLine($"       after, around {e.X:0}/{e.Y:0}:"); Dump(e); }
    }
}
