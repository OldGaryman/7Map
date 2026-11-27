using System.Windows;
using MapEditor.Map;

namespace YameeTests;

static class WildernessConnectorTests
{
    static WorldData FlatRollingWorld(string name)
    {
        var w = Fixture.LoadCopy();
        int size = w.Size;
        Array.Clear(w.RoadMask);
        Array.Clear(w.WaterMask);
        Array.Clear(w.Biomes); // pine forest (0) everywhere
        w.Prefabs.Clear();
        // Gentle rolling hills (~35–42 m) so gradients are realistic while staying comfortably buildable
        for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++)
            {
                double h = 38.0 + 3.0 * Math.Sin(x * 0.008) * Math.Cos(z * 0.008) + 1.5 * Math.Sin((x + z) * 0.015);
                w.Heights[z * size + x] = (ushort)Math.Round(h * 256.0);
            }
        return w;
    }

    static Dictionary<string, PrefabDef> SyntheticWildernessPois()
    {
        var cands = new Dictionary<string, PrefabDef>(StringComparer.OrdinalIgnoreCase);
        for (int k = 1; k <= 10; k++)
        {
            var d = new PrefabDef
            {
                SizeX = 28,
                SizeY = 12,
                SizeZ = 28,
                YOffset = -2,
                Tier = 1 + (k % 3),
                Found = true,
                DuplicateRepeatDistance = 260
            };
            d.Tags.Add("wilderness");
            d.Exits.Add((14, 0));
            d.ExitSizes.Add((5, 5));
            cands[$"wild_cabin_{k:00}"] = d;
        }
        return cands;
    }

    static void PaintAsphaltLine(WorldData w, WorldEdits edits, Point a, Point b)
    {
        var r = RoadPlanner.Compute(w, new List<Point> { a, b }, RoadSettings.Asphalt());
        Check.True(r != null, "Synthetic asphalt road must compute");
        edits.Run("test-road", "", ed => RoadApplier.Apply(w, ed, r, new Progress<string>()));
    }

    static double ArcLengthFromStart(List<Point> pts, Point q)
    {
        double walked = 0, best = 0, bd2 = double.MaxValue;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var ab = pts[i] - a;
            double l2 = ab.LengthSquared;
            double len = Math.Sqrt(l2);
            double t = l2 == 0 ? 0 : Math.Clamp(((q - a) * ab) / l2, 0, 1);
            var p = a + ab * t;
            double d2 = (p - q).LengthSquared;
            if (d2 < bd2) { bd2 = d2; best = walked + len * t; }
            walked += len;
        }
        return best;
    }

    [Test("Wilderness connectors: end + mid links, ignores MaxPath, no steps/self-loops, exact undo")]
    static void EndAndMidConnectorsIgnoreMaxPath()
    {
        var w = FlatRollingWorld("WildConnGrid");
        var edits = new WorldEdits(w);

        // Realistic 1200 x 1200 m wilderness block bordered by 4 asphalt roads (like a real map corridor):
        // wilderness POIs form both short single-POI stubs near roads AND long/multi-POI paths reaching into the interior.
        PaintAsphaltLine(w, edits, new Point(-600, -820), new Point(-600, 820));
        PaintAsphaltLine(w, edits, new Point(600, -820), new Point(600, 820));
        PaintAsphaltLine(w, edits, new Point(-820, -600), new Point(820, -600));
        PaintAsphaltLine(w, edits, new Point(-820, 600), new Point(820, 600));

        var asphaltSnapshot = new List<(int Idx, ushort H)>();
        for (int i = 0; i < w.RoadMask.Length; i++)
            if (w.RoadMask[i] == WorldData.Asphalt) asphaltSnapshot.Add((i, w.Heights[i]));
        Check.True(asphaltSnapshot.Count > 5000, "Synthetic road network must be painted");

        string stateBefore = Fixture.State(w);
        var cands = SyntheticWildernessPois();

        // MaxPath = 320 m: long paths & chains reach across the 1200 m block; Pass 2 ignores MaxPath (can build > 320 m connectors)
        var spec = new WildernessSpec
        {
            Density = 10.0,
            MaxPath = 320,
            PathWidthMin = 3,
            PathWidth = 5,
            Verge = 1,
            Grade = 0.12,
            MaxGrade = 0.18,
            BreakInWasteland = false,
            Seed = "ConnTestSeed1"
        };

        var wg = new WildernessGenerator(w, spec, new WildernessRules(), cands, _ => null);
        var plan = wg.Plan(null);

        var basePaths = plan.Paths.Where(p => !p.IsConnector && !p.IsVolcano).ToList();
        var connectors = plan.Paths.Where(p => p.IsConnector).ToList();
        var hasChildDbg = new HashSet<WildPath>(basePaths.Where(p => p.Target != null).Select(p => p.Target));
        int shortSingleCnt = basePaths.Count(p => p.Target == null && !hasChildDbg.Contains(p) && p.Length < 250.0);
        int multiOrLongCnt = basePaths.Count - shortSingleCnt;
        Console.WriteLine(FormattableString.Invariant($"       basePaths={basePaths.Count}, shortSingle={shortSingleCnt}, multiOrLong={multiOrLongCnt}, connectors={connectors.Count}, maxBaseLen={(basePaths.Count > 0 ? basePaths.Max(p => p.Length) : 0):0}"));
        Check.True(basePaths.Count >= 12, $"Expected >= 12 base wilderness paths, got {basePaths.Count}");
        Check.True(connectors.Count >= 2, $"Expected >= 2 second-pass connectors, got {connectors.Count}");

        // Verify both END connectors and MID connectors are created
        int endConns = 0, midConns = 0, beyondMaxPath = 0;
        foreach (var c in connectors)
        {
            Check.True(c.StartTarget != null, "Every connector must reference StartTarget");
            Check.True(c.Poi == null, "Connector paths must not own a POI");
            double arc = ArcLengthFromStart(c.StartTarget.Points, c.Points[0]);
            if (arc <= 45.0) endConns++;
            else if (arc >= 45.0 && c.StartTarget.Length - arc >= 45.0) midConns++;
            if (c.Length > spec.MaxPath) beyondMaxPath++;

            // Geometry rules: no self-loop (< 24 m) and no sharp kinks (> 70 deg at 10 m spacing)
            var even = NetworkBuilder.Even(c.Points, 10.0);
            Check.True(!RoadNetwork.HasSelfLoop(even), "Connector path must never self-loop (< 24 m)");
            for (int i = 1; i + 1 < even.Count; i++)
            {
                var u = even[i] - even[i - 1]; var v = even[i + 1] - even[i];
                if (u.Length < 2 || v.Length < 2) continue;
                u.Normalize(); v.Normalize();
                double deg = Math.Acos(Math.Clamp(u * v, -1.0, 1.0)) * 180.0 / Math.PI;
                Check.True(deg <= 70.0, $"Connector has sharp kink ({deg:0.0} deg)");
            }
        }
        Console.WriteLine(FormattableString.Invariant($"       wilderness plan: {plan.Pois.Count} POIs, {basePaths.Count} base paths, {connectors.Count} connectors (end {endConns}, mid {midConns}, >MaxPath {beyondMaxPath})"));
        Check.True(endConns >= 1, $"Expected at least 1 end connector, got {endConns}");
        Check.True(midConns >= 1, $"Expected at least 1 mid-path connector, got {midConns}");
        Check.True(beyondMaxPath >= 1, $"Expected at least 1 connector longer than spec.MaxPath ({spec.MaxPath} m), proving MaxPath is ignored in pass 2");

        // Verify short single-POI stubs (< 250 m, no children) NEVER receive connectors
        var hasChild = new HashSet<WildPath>(basePaths.Where(p => p.Target != null).Select(p => p.Target));
        var shortSingles = basePaths.Where(p => p.Target == null && !hasChild.Contains(p) && p.Length < 250.0).ToList();
        foreach (var s in shortSingles)
            Check.True(!connectors.Any(c => c.StartTarget == s),
                $"Short single-POI path ({s.Length:0} m) must NOT receive a connector");

        // Build all POIs + paths + connectors in one edit step
        WildernessBuildResult res = null;
        edits.Run("wild-conn-build", "", ed => res = WildernessGenerator.Build(w, ed, plan, spec, null));
        Check.Equal(plan.Paths.Count, res.Paths, "All planned paths including connectors must build");
        Check.Equal(0, res.Warnings.Count, $"Unexpected build warnings: {string.Join("; ", res.Warnings)}");

        // Existing asphalt must stay completely intact
        int damagedAsphalt = asphaltSnapshot.Count(a => w.RoadMask[a.Idx] != WorldData.Asphalt || Math.Abs(w.Heights[a.Idx] - a.H) > 256);
        Check.Equal(0, damagedAsphalt, "Connectors must never damage existing asphalt roads");

        // Check max pixel-to-pixel vertical step across gravel roadway around every connector (including both junctions!)
        double maxGravelStep = 0;
        foreach (var c in connectors)
        {
            foreach (var pt in NetworkBuilder.Even(c.Points, 6.0))
            {
                int px = (int)Math.Round(pt.X), pz = (int)Math.Round(pt.Y);
                for (int dz = -6; dz <= 6; dz++)
                    for (int dx = -6; dx <= 6; dx++)
                    {
                        int wx = px + dx, wz = pz + dz;
                        if (!w.Contains(wx, wz) || !w.Contains(wx + 1, wz) || !w.Contains(wx, wz + 1)) continue;
                        if (w.RoadAt(wx, wz) != WorldData.Gravel) continue;
                        double h0 = w.HeightAt(wx, wz);
                        if (w.RoadAt(wx + 1, wz) == WorldData.Gravel) maxGravelStep = Math.Max(maxGravelStep, Math.Abs(w.HeightAt(wx + 1, wz) - h0));
                        if (w.RoadAt(wx, wz + 1) == WorldData.Gravel) maxGravelStep = Math.Max(maxGravelStep, Math.Abs(w.HeightAt(wx, wz + 1) - h0));
                    }
            }
        }
        Console.WriteLine(FormattableString.Invariant($"       built connectors: maxGravelStep {maxGravelStep:0.00} m, shortSingles untouched {shortSingles.Count}"));
        Check.True(maxGravelStep <= 0.85, $"Connector roadway/junction has vertical step {maxGravelStep:0.00} m > 0.85 m");

        // Exact Undo
        edits.Undo();
        Check.Equal(stateBefore, Fixture.State(w), "Undo after building wilderness POIs + connectors must be byte-exact");
    }

    [Test("Wilderness connectors: single short POI path stays untouched even with nearby road")]
    static void SingleShortPoiPathStaysUntouched()
    {
        var w = FlatRollingWorld("WildConnShortOnly");
        var edits = new WorldEdits(w);

        // Two parallel roads 420 m apart: every single POI placed near one road is within 420 m of the other,
        // yet standalone short single-POI stubs (< 250 m) must NOT get extended.
        PaintAsphaltLine(w, edits, new Point(-210, -820), new Point(-210, 820));
        PaintAsphaltLine(w, edits, new Point(210, -820), new Point(210, 820));

        var spec = new WildernessSpec
        {
            Density = 2.0,
            MaxPath = 180,   // every single path to a road is <= 180 m (< 250 m)
            PathWidthMin = 3,
            PathWidth = 5,
            Verge = 1,
            Grade = 0.12,
            MaxGrade = 0.18,
            BreakInWasteland = false,
            Seed = "ShortStubOnly"
        };

        var wg = new WildernessGenerator(w, spec, new WildernessRules(), SyntheticWildernessPois(), _ => null);
        var plan = wg.Plan(null);

        var basePaths = plan.Paths.Where(p => !p.IsConnector && !p.IsVolcano).ToList();
        var connectors = plan.Paths.Where(p => p.IsConnector).ToList();
        var hasChild = new HashSet<WildPath>(basePaths.Where(p => p.Target != null).Select(p => p.Target));
        var shortSingles = basePaths.Where(p => p.Target == null && !hasChild.Contains(p) && p.Length < 250.0).ToList();

        Check.True(shortSingles.Count >= 2, $"Expected >= 2 standalone short single-POI paths, got {shortSingles.Count}");
        foreach (var s in shortSingles)
            Check.True(!connectors.Any(c => c.StartTarget == s),
                $"Standalone short single-POI path ({s.Length:0} m) must not get a 2nd-pass connector");
    }

    sealed class RecordingProgress : MapEditor.IWorkProgress
    {
        readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        public List<(double Ms, string Text, double? Fraction)> Events { get; } = new();
        public void Report(string value) => Events.Add((sw.Elapsed.TotalMilliseconds, value, null));
        public void Report(string text, double fraction) => Events.Add((sw.Elapsed.TotalMilliseconds, text, fraction));
    }

    [Test("Wilderness progress: live status texts, monotonic calibrated fractions, Slice mapping")]
    static void ProgressTextsAndCalibration()
    {
        // 1. Verify Slice mapping
        var recSlice = new RecordingProgress();
        var sub = MapEditor.ProgressExtensions.Slice(recSlice, 0.25, 0.75);
        MapEditor.ProgressExtensions.Report(sub, "step-a", 0.0);
        MapEditor.ProgressExtensions.Report(sub, "step-b", 0.5);
        MapEditor.ProgressExtensions.Report(sub, "step-c", 1.0);
        Check.Equal(3, recSlice.Events.Count, "Slice should forward all 3 reports");
        Check.True(Math.Abs((recSlice.Events[0].Fraction ?? -1) - 0.25) < 1e-6, "Slice 0.0 -> 0.25");
        Check.True(Math.Abs((recSlice.Events[1].Fraction ?? -1) - 0.50) < 1e-6, "Slice 0.5 -> 0.50");
        Check.True(Math.Abs((recSlice.Events[2].Fraction ?? -1) - 0.75) < 1e-6, "Slice 1.0 -> 0.75");

        // 2. Measure real Plan & Build progress reports and wall-clock phase timings
        var w = FlatRollingWorld("WildProgCalib");
        var edits = new WorldEdits(w);
        PaintAsphaltLine(w, edits, new Point(-600, -820), new Point(-600, 820));
        PaintAsphaltLine(w, edits, new Point(600, -820), new Point(600, 820));
        PaintAsphaltLine(w, edits, new Point(-820, -600), new Point(820, -600));
        PaintAsphaltLine(w, edits, new Point(-820, 600), new Point(820, 600));

        var spec = new WildernessSpec
        {
            Density = 10.0,
            MaxPath = 320,
            PathWidthMin = 3,
            PathWidth = 5,
            Verge = 1,
            Grade = 0.12,
            MaxGrade = 0.18,
            BreakInWasteland = false,
            Seed = "ConnTestSeed1"
        };

        var planProg = new RecordingProgress();
        var swPlan = System.Diagnostics.Stopwatch.StartNew();
        var plan = new WildernessGenerator(w, spec, new WildernessRules(), SyntheticWildernessPois(), _ => null).Plan(planProg);
        double planTotalMs = swPlan.Elapsed.TotalMilliseconds;

        Check.True(planProg.Events.Count >= 6, $"Expected >= 6 plan progress events, got {planProg.Events.Count}");
        double prev = -1;
        foreach (var ev in planProg.Events)
        {
            Check.True(!string.IsNullOrWhiteSpace(ev.Text), "Plan progress text must not be empty");
            Check.True(ev.Fraction.HasValue, $"Plan progress '{ev.Text}' must report a real fraction");
            Check.True(ev.Fraction.Value >= 0.0 && ev.Fraction.Value <= 1.0, $"Plan fraction out of range: {ev.Fraction}");
            Check.True(ev.Fraction.Value + 1e-9 >= prev, $"Plan fraction went backwards: {prev:0.000} -> {ev.Fraction.Value:0.000} ({ev.Text})");
            prev = ev.Fraction.Value;
        }
        var firstPoiEv = planProg.Events.First(e => e.Text.Contains("wilderness POIs placed", StringComparison.OrdinalIgnoreCase));
        var firstConnEv = planProg.Events.First(e => e.Text.Contains("Connecting wilderness paths", StringComparison.OrdinalIgnoreCase));
        var lastConnEv = planProg.Events.Last(e => e.Text.Contains("Connecting wilderness paths", StringComparison.OrdinalIgnoreCase));
        double connFracSpan = lastConnEv.Fraction!.Value - firstConnEv.Fraction!.Value;
        Console.WriteLine(FormattableString.Invariant(
            $"       Plan ({planTotalMs:0} ms): grid 0..{firstPoiEv.Ms:0} ms ({firstPoiEv.Fraction:P0}), POIs {firstPoiEv.Ms:0}..{firstConnEv.Ms:0} ms ({firstPoiEv.Fraction:P0}..{firstConnEv.Fraction:P0}), connectors {firstConnEv.Ms:0}..{planTotalMs:0} ms ({firstConnEv.Fraction:P0}..{lastConnEv.Fraction:P0})"));
        Check.True(connFracSpan >= 0.35, $"Connector pass should span >= 35% of Plan bar, got {connFracSpan:P0}");

        var buildProg = new RecordingProgress();
        var swBuild = System.Diagnostics.Stopwatch.StartNew();
        edits.Run("wild-prog-build", "", ed => WildernessGenerator.Build(w, ed, plan, spec, buildProg));
        double buildTotalMs = swBuild.Elapsed.TotalMilliseconds;

        Check.True(buildProg.Events.Count >= 6, $"Expected >= 6 build progress events, got {buildProg.Events.Count}");
        prev = -1;
        foreach (var ev in buildProg.Events)
        {
            Check.True(!string.IsNullOrWhiteSpace(ev.Text), "Build progress text must not be empty");
            Check.True(ev.Fraction.HasValue, $"Build progress '{ev.Text}' must report a real fraction");
            Check.True(ev.Fraction.Value >= 0.0 && ev.Fraction.Value <= 1.0, $"Build fraction out of range: {ev.Fraction}");
            Check.True(ev.Fraction.Value + 1e-9 >= prev, $"Build fraction went backwards: {prev:0.000} -> {ev.Fraction.Value:0.000} ({ev.Text})");
            prev = ev.Fraction.Value;
        }
        var firstLevelEv = buildProg.Events.First(e => e.Text.Contains("Levelling the ground", StringComparison.OrdinalIgnoreCase));
        var firstPathEv = buildProg.Events.First(e => e.Text.Contains("Building path", StringComparison.OrdinalIgnoreCase));
        var lastPathEv = buildProg.Events.Last(e => e.Text.Contains("Building path", StringComparison.OrdinalIgnoreCase));
        double pathFracSpan = lastPathEv.Fraction!.Value - firstPathEv.Fraction!.Value;
        Console.WriteLine(FormattableString.Invariant(
            $"       Build ({buildTotalMs:0} ms): level {firstLevelEv.Ms:0}..{firstPathEv.Ms:0} ms ({firstLevelEv.Fraction:P0}..{firstPathEv.Fraction:P0}), paths {firstPathEv.Ms:0}..{buildTotalMs:0} ms ({firstPathEv.Fraction:P0}..{lastPathEv.Fraction:P0})"));
        Check.True(pathFracSpan >= 0.55, $"Building paths should span >= 55% of Build bar, got {pathFracSpan:P0}");
    }

    static RoadNetwork.BridgeKit SyntheticBridgeKit() => new(
        new PrefabDef { SizeX = 13, SizeY = 18, SizeZ = 10, Kind = PrefabKind.Part, Found = true },
        new PrefabDef { SizeX = 10, SizeY = 18, SizeZ = 10, Kind = PrefabKind.Part, Found = true },
        3);

    static void CarveStraightRiverNS(WorldData w, int centerX, int zFrom, int zTo, int halfWidth, byte level)
    {
        for (int z = zFrom; z <= zTo; z++)
            for (int x = centerX - halfWidth - 10; x <= centerX + halfWidth + 10; x++)
            {
                if (!w.Contains(x, z)) continue;
                int dx = Math.Abs(x - centerX);
                int idx = w.Index(x, z);
                if (dx <= halfWidth)
                {
                    double dep = Math.Min(halfWidth - dx + 1, 4.5);
                    w.Heights[idx] = (ushort)Math.Round((level - dep) * 256.0);
                    w.WaterMask[idx] = level;
                }
                else
                {
                    // Gentle bank rising smoothly from level+1.5 up to surrounding ~38 m
                    double bank = Math.Min(38.0, level + 1.5 + (dx - halfWidth) * 0.45);
                    double cur = w.Heights[idx] / 256.0;
                    double t = Math.Clamp((dx - halfWidth) / 10.0, 0, 1);
                    double h = bank + (cur - bank) * t;
                    w.Heights[idx] = (ushort)Math.Round(Math.Max(level + 1.2, h) * 256.0);
                    w.WaterMask[idx] = 0;
                }
            }
    }

    [Test("Wilderness bridge: builds at least one gravel bridge over a river even with roads on both banks")]
    static void EnsuresAtLeastOneBridgeWithRoadsOnBothBanks()
    {
        var w = FlatRollingWorld("WildBridgeBothBanks");
        var edits = new WorldEdits(w);

        // Straight north-south river along x = 0 (water level 34, width 19 m, z = -700..+700)
        CarveStraightRiverNS(w, 0, -700, 700, 9, 34);
        // Asphalt roads on BOTH banks (x = -340 and x = +340) so regular POIs on either bank don't need a bridge
        PaintAsphaltLine(w, edits, new Point(-340, -750), new Point(-340, 750));
        PaintAsphaltLine(w, edits, new Point(340, -750), new Point(340, 750));

        var asphaltSnapshot = new List<(int Idx, ushort H)>();
        for (int i = 0; i < w.RoadMask.Length; i++)
            if (w.RoadMask[i] == WorldData.Asphalt) asphaltSnapshot.Add((i, w.Heights[i]));

        string stateBefore = Fixture.State(w);
        int prefabsBefore = w.Prefabs.Count;
        var prevKit = RoadNetwork.Bridges;
        RoadNetwork.Bridges = SyntheticBridgeKit();
        try
        {
            var spec = new WildernessSpec
            {
                Density = 4.0,
                MaxPath = 260,
                PathWidthMin = 3,
                PathWidth = 5,
                Verge = 1,
                Grade = 0.12,
                MaxGrade = 0.18,
                BreakInWasteland = false,
                Seed = "BridgeBothBanks1"
            };

            var plan = new WildernessGenerator(w, spec, new WildernessRules(), SyntheticWildernessPois(), _ => null).Plan(null);
            var bridgePaths = plan.Paths.Where(p => p.Bridges.Count > 0).ToList();
            var bridges = bridgePaths.SelectMany(p => p.Bridges).ToList();
            Console.WriteLine(FormattableString.Invariant(
                $"       wilderness plan (roads both sides): {plan.Pois.Count} POIs, {plan.Paths.Count} paths, {bridges.Count} bridge(s)"));
            Check.True(bridges.Count >= 1, "Expected at least 1 gravel bridge across the river even when roads run on both banks");

            foreach (var bp in bridgePaths)
            {
                Check.True(!RoadNetwork.HasSelfLoop(NetworkBuilder.Even(bp.Points, 10.0)), "Bridge path must not self-loop");
                foreach (var b in bp.Bridges)
                {
                    Check.True(b.Tiles >= 1 && b.Tiles <= RoadNetwork.BridgeMaxTiles, $"Bridge tiles out of range: {b.Tiles}");
                    Check.True(b.RiverAngle <= RoadNetwork.NetMaxAngle, $"Bridge river angle too steep: {b.RiverAngle}");
                }
            }

            WildernessBuildResult res = null;
            edits.Run("wild-bridge-build", "", ed => res = WildernessGenerator.Build(w, ed, plan, spec, null));
            Check.Equal(bridges.Count, res.Bridges, "All planned wilderness bridges must be built");
            Check.Equal(0, res.Warnings.Count, $"Unexpected build warnings: {string.Join("; ", res.Warnings)}");

            // Verify every YAMEE bridge rule on the built world
            foreach (var bp in bridgePaths)
            {
                foreach (var b in bp.Bridges)
                {
                    Check.True(b.Gravel, "Wilderness bridge must use gravel approaches");
                    var c = b.Center;
                    int pieces = w.Prefabs.Count(q => q.Name.StartsWith("yamee_part_bridge", StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(q.X + 5 - c.X) < b.Length && Math.Abs(q.Z + 5 - c.Y) < b.Length);
                    Check.Equal(b.Tiles + 2, pieces, "Expected end + tiles + end bridge prefabs");

                    // Approach heights & gravel surface (both ends)
                    double t = b.T0 + 5, targetH = b.Deck - 1 + b.ApproachLift;
                    foreach (int s in new[] { b.S0 - Bridge.Approach, b.S0 - Bridge.Approach - 4, b.S0 + b.Length + Bridge.Approach - 1, b.S0 + b.Length + Bridge.Approach + 3 })
                    {
                        int x = b.AlongX ? s : (int)t, z = b.AlongX ? (int)t : s;
                        Check.True(Math.Abs(w.HeightAt(x, z) - targetH) < 0.30, $"Bridge approach height off: {w.HeightAt(x, z):0.00} vs {targetH:0.00}");
                        Check.Equal(WorldData.Gravel, w.RoadAt(x, z), "Wilderness bridge approach must be gravel");
                    }

                    // Widened gravel approach (at least 7 m wide 8 m ahead of approach)
                    int sv = b.S0 - Bridge.Approach - 8, cnt = 0;
                    for (int tt = b.T0 - 12; tt <= b.T0 + 21; tt++)
                        if ((b.AlongX ? w.RoadAt(sv, tt) : w.RoadAt(tt, sv)) != 0) cnt++;
                    Check.True(cnt >= 7, $"Gravel surface 8 m before bridge approach only {cnt} m wide");

                    // Zero water spills beside water near the bridge
                    int spills = 0;
                    int x0 = Math.Max(-w.Half + 1, (int)c.X - 150), x1 = Math.Min(w.Half - 2, (int)c.X + 150);
                    int z0 = Math.Max(-w.Half + 1, (int)c.Y - 150), z1 = Math.Min(w.Half - 2, (int)c.Y + 150);
                    for (int wz = z0; wz <= z1; wz++)
                        for (int wx = x0; wx <= x1; wx++)
                        {
                            int i = w.Index(wx, wz);
                            if (w.WaterMask[i] != 0) continue;
                            byte nl = Math.Max(Math.Max(w.WaterMask[i + 1], w.WaterMask[i - 1]), Math.Max(w.WaterMask[i + w.Size], w.WaterMask[i - w.Size]));
                            if (nl != 0 && w.Heights[i] / 256.0 < nl + 0.5) spills++;
                        }
                    Check.Equal(0, spills, "No dry cells below water level beside bridge");
                }

                // No water under the path outside bridge zones
                var road = new NetRoad { Points = bp.Points, Bridges = bp.Bridges };
                for (int q = 1; q < bp.Points.Count; q++)
                {
                    var a = bp.Points[q - 1]; var d = bp.Points[q] - a;
                    for (double f = 0; f < d.Length; f += 2)
                    {
                        var p = a + d * (f / d.Length);
                        if (RoadNetwork.NearBridge(road, p)) continue;
                        Check.True(!w.WaterAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y)), $"Water under bridge path outside bridge at {p.X:0},{p.Y:0}");
                    }
                }
            }

            // Existing asphalt on BOTH banks must stay completely undamaged
            int damagedAsphalt = asphaltSnapshot.Count(a => w.RoadMask[a.Idx] != WorldData.Asphalt || Math.Abs(w.Heights[a.Idx] - a.H) > 256);
            Check.Equal(0, damagedAsphalt, "Bridge connecting path must never damage existing asphalt roads on either bank");

            // Exact Undo
            edits.Undo();
            Check.Equal(stateBefore, Fixture.State(w), "Undo after building wilderness bridge must be byte-exact");
            Check.Equal(prefabsBefore, w.Prefabs.Count, "Prefabs count after undo must match");
        }
        finally { RoadNetwork.Bridges = prevKit; }
    }

    [Test("Wilderness bridge: respects 300 m spacing from existing bridges")]
    static void BridgeRespectsExistingBridgeSpacing()
    {
        var w = FlatRollingWorld("WildBridgeSpacing");
        var edits = new WorldEdits(w);

        // Short 360 m river with an existing bridge right in the middle: every point is within < 300 m
        CarveStraightRiverNS(w, 0, -180, 180, 9, 34);
        PaintAsphaltLine(w, edits, new Point(-320, -350), new Point(-320, 350));
        PaintAsphaltLine(w, edits, new Point(320, -350), new Point(320, 350));
        w.Prefabs.Add(new PlacedPrefab { Name = Bridge.TileName, X = -5, Z = -5, Y = 28, Def = new PrefabDef { SizeX = 10, SizeY = 18, SizeZ = 10, Kind = PrefabKind.Part, Found = true } });

        var prevKit = RoadNetwork.Bridges;
        RoadNetwork.Bridges = SyntheticBridgeKit();
        try
        {
            var spec = new WildernessSpec { Density = 3.0, MaxPath = 250, BreakInWasteland = false, Seed = "BridgeSpacing1" };
            var plan = new WildernessGenerator(w, spec, new WildernessRules(), SyntheticWildernessPois(), _ => null).Plan(null);
            int bridges = plan.Paths.Sum(p => p.Bridges.Count);
            Check.Equal(0, bridges, "Must not place a new wilderness bridge within 300 m of an existing bridge");
        }
        finally { RoadNetwork.Bridges = prevKit; }
    }

    static PlacedPrefab SyntheticCityTile(int x, int z, int groundBlockY)
    {
        var def = new PrefabDef { SizeX = 150, SizeY = 20, SizeZ = 150, YOffset = -2, Kind = PrefabKind.Tile, Found = true };
        return new PlacedPrefab { Name = "rwg_tile_commercial_straight", Def = def, X = x, Z = z, Y = groundBlockY + 1 + def.YOffset, Rotation = 0 };
    }

    static double RectDistToPrefab(double x, double z, PlacedPrefab p)
    {
        double dx = Math.Max(0, Math.Max(p.X - x, x - (p.X + p.SizeX)));
        double dz = Math.Max(0, Math.Max(p.Z - z, z - (p.Z + p.SizeZ)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    [Test("Wilderness paths: height-dependent city tile clearance keeps elevated paths farther away and avoids steep walls")]
    static void CityTileClearanceFormulaAndPaths()
    {
        // 1. Formula checks (default pathEdge = 2.5 + 1.0 = 3.5 m)
        double c0 = WildernessGenerator.TileClearance(0, 3.5);
        double c5 = WildernessGenerator.TileClearance(5, 3.5);
        double c10 = WildernessGenerator.TileClearance(10, 3.5);
        double c20 = WildernessGenerator.TileClearance(20, 3.5);
        double cHigh = WildernessGenerator.TileClearance(40, 3.5);
        Check.True(Math.Abs(c0 - 18.0) < 1e-6, $"dh=0 clearance should be 18 m, got {c0}");
        Check.True(Math.Abs(c5 - 33.0) < 1e-6, $"dh=5 clearance should be 33 m, got {c5}");
        Check.True(Math.Abs(c10 - 48.0) < 1e-6, $"dh=10 clearance should be 48 m, got {c10}");
        Check.True(Math.Abs(c20 - 78.0) < 1e-6, $"dh=20 clearance should be 78 m, got {c20}");
        Check.True(Math.Abs(cHigh - WildernessGenerator.MaxTileClearance) < 1e-6, $"Extreme dh should cap at {WildernessGenerator.MaxTileClearance}, got {cHigh}");
        Check.True(Math.Abs(WildernessGenerator.TileClearance(-10, 3.5) - c10) < 1e-6, "Formula must protect both higher and lower paths symmetrically");
        Check.True(WildernessGenerator.TileClearance(5, 4.5) > c5, "Wider path edge must require wider clearance");

        // 2. Realistic world: 150x150 city tile at (-75,-75)..(75,75) on tileH ~38.15 m,
        //    flat terrain to the south/west (dh ~0) and gently elevated +12 m hill to the north/east (dh ~12 m)
        var w = FlatRollingWorld("WildTileClearance");
        var edits = new WorldEdits(w);
        var tile = SyntheticCityTile(-75, -75, 38);
        double tileH = tile.TerrainHeight;
        for (int z = -w.Half; z < w.Half; z++)
            for (int x = -w.Half; x < w.Half; x++)
            {
                double d = RectDistToPrefab(x + 0.5, z + 0.5, tile);
                // Smoothly rise +12 m on the north/east side starting outside the tile, flat on the south/west side
                double neWeight = Math.Clamp(((x + 75.0) + (z + 75.0)) / 300.0, 0, 1);
                double ramp = Math.Clamp((d - 4.0) / 36.0, 0, 1);
                ramp = ramp * ramp * (3 - 2 * ramp);
                double h = tileH + 12.0 * neWeight * ramp;
                w.Heights[w.Index(x, z)] = (ushort)Math.Clamp(Math.Round(h * 256.0), 0, 65535);
            }
        w.Prefabs.Add(tile);

        // Surrounding asphalt roads closer to the tile so POIs and 2nd-pass connectors genuinely route past the tile edges
        PaintAsphaltLine(w, edits, new Point(-320, -640), new Point(-320, 640));
        PaintAsphaltLine(w, edits, new Point(320, -640), new Point(320, 640));
        PaintAsphaltLine(w, edits, new Point(-640, -240), new Point(640, -240));
        PaintAsphaltLine(w, edits, new Point(-640, 320), new Point(640, 320));
        // A road approaching the tile from the west right up to its edge (must not receive a wilderness junction within TileClearance)
        PaintAsphaltLine(w, edits, new Point(-320, 0), new Point(-76, 0));

        string stateBefore = Fixture.State(w);
        var heightsBefore = (ushort[])w.Heights.Clone();

        var spec = new WildernessSpec
        {
            Density = 12.0,
            MaxPath = 480,
            PathWidthMin = 3,
            PathWidth = 5,
            Verge = 1,
            Grade = 0.14,
            MaxGrade = 0.20,
            BreakInWasteland = false,
            Seed = "TileClear1"
        };
        var plan = new WildernessGenerator(w, spec, new WildernessRules(), SyntheticWildernessPois(), _ => null).Plan(null);
        Check.True(plan.Paths.Count >= 12, $"Expected >= 12 wilderness paths around the tile, got {plan.Paths.Count}");

        WildernessBuildResult res = null;
        edits.Run("wild-tile-clear", "", ed => res = WildernessGenerator.Build(w, ed, plan, spec, null));
        Check.Equal(plan.Paths.Count, res.Paths, "All planned paths must build");

        double pathEdge = spec.PathWidth / 2.0 + spec.Verge;
        double minDistFlat = double.MaxValue, minDistElevated = double.MaxValue;
        double worstViolation = 0;
        int nearPoints = 0;
        foreach (var (wp, road) in res.Built)
        {
            double endSkip = wp.Target == null ? wp.EndHalf : 0;
            for (int i = 0; i < road.Path.Count; i += 4)
            {
                var pt = road.Path[i];
                if (endSkip > 0 && road.Length - pt.Dist < endSkip) continue;
                double d = RectDistToPrefab(pt.X, pt.Z, tile);
                if (d > 160) continue;
                nearPoints++;
                double dh = Math.Abs(road.Profile[i] - tileH);
                double req = WildernessGenerator.TileClearance(dh, pathEdge);
                if (req - d > worstViolation) worstViolation = req - d;
                if (dh < 2.0) minDistFlat = Math.Min(minDistFlat, d);
                if (dh >= 8.0) minDistElevated = Math.Min(minDistElevated, d);
            }
        }
        Console.WriteLine(FormattableString.Invariant(
            $"       tile clearance: {plan.Paths.Count} paths, {nearPoints} near pts, minFlat={(minDistFlat < 1e9 ? $"{minDistFlat:0.0} m" : "-")}, minHigh(dh>=8m)={(minDistElevated < 1e9 ? $"{minDistElevated:0.0} m" : "-")}, worstViolation={worstViolation:0.00} m"));
        Check.True(nearPoints > 0, "Test must produce wilderness paths passing within 160 m of the city tile");
        Check.True(worstViolation <= 0.75, $"Built wilderness path violated height-dependent tile clearance by {worstViolation:0.00} m");
        if (minDistElevated < double.MaxValue)
            Check.True(minDistElevated >= 41.0, $"Elevated path (dh >= 8 m) came too close to tile: {minDistElevated:0.0} m");

        // No steep new step created in the 14 m collar right outside the city tile, and tile interior untouched
        int tileChanged = 0;
        double maxNewCollarStep = 0;
        for (int z = tile.Z - 14; z <= tile.Z + tile.SizeZ + 14; z++)
            for (int x = tile.X - 14; x <= tile.X + tile.SizeX + 14; x++)
            {
                int idx = w.Index(x, z);
                if (tile.Contains(x + 0.5, z + 0.5))
                {
                    if (w.Heights[idx] != heightsBefore[idx]) tileChanged++;
                    continue;
                }
                double d = RectDistToPrefab(x + 0.5, z + 0.5, tile);
                if (d > 14.0 || w.Heights[idx] == heightsBefore[idx]) continue;
                double h0 = w.Heights[idx] / 256.0, b0 = heightsBefore[idx] / 256.0;
                foreach (var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                {
                    int ni = w.Index(nx, nz);
                    double afterStep = Math.Abs(h0 - w.Heights[ni] / 256.0);
                    double beforeStep = Math.Abs(b0 - heightsBefore[ni] / 256.0);
                    if (afterStep > beforeStep + 0.05) maxNewCollarStep = Math.Max(maxNewCollarStep, afterStep);
                }
            }
        Check.Equal(0, tileChanged, "Wilderness paths must never change heights inside a city tile");
        Check.True(maxNewCollarStep <= 0.65, $"Wilderness path created a steep {maxNewCollarStep:0.00} m step right outside the city tile");

        edits.Undo();
        Check.Equal(stateBefore, Fixture.State(w), "Undo after building wilderness paths near a city tile must be byte-exact");
    }

    [Test("Wilderness paths: higher path is routed noticeably farther from a city tile than a flat path")]
    static void ElevatedPathRoutesWiderThanFlatPath()
    {
        double RunScenario(double elevationDelta)
        {
            var w = FlatRollingWorld("WildTileAB");
            var edits = new WorldEdits(w);
            var tile = SyntheticCityTile(-75, -75, 38);
            double tileH = tile.TerrainHeight;
            for (int z = -w.Half; z < w.Half; z++)
                for (int x = -w.Half; x < w.Half; x++)
                {
                    double d = RectDistToPrefab(x + 0.5, z + 0.5, tile);
                    double ramp = Math.Clamp((d - 6.0) / 40.0, 0, 1);
                    ramp = ramp * ramp * (3 - 2 * ramp);
                    double h = tileH + elevationDelta * ramp;
                    w.Heights[w.Index(x, z)] = (ushort)Math.Clamp(Math.Round(h * 256.0), 0, 65535);
                }
            w.Prefabs.Add(tile);

            // Road south of the tile and a single wilderness POI north of the tile: the shortest route must go around the tile's side!
            PaintAsphaltLine(w, edits, new Point(-450, -180), new Point(450, -180));
            // Block everything except a tight corridor around the east side so the path MUST pass the tile's east edge
            for (int z = -150; z <= 350; z++)
                for (int x = -450; x <= -76; x++)
                    w.WaterMask[w.Index(x, z)] = 30;

            var spec = new WildernessSpec
            {
                Density = 0.0,   // no random POIs; we test FindPath through a real POI via custom candidate right north of tile
                MaxPath = 900,
                PathWidthMin = 5,
                PathWidth = 5,
                Verge = 1,
                Grade = 0.16,
                MaxGrade = 0.22,
                BreakInWasteland = false,
                Seed = "TileAB"
            };
            // Restrict biome 0 to a small patch north of the tile (> 100 m from tile edge) so a single POI spawns there
            Array.Fill(w.Biomes, (byte)1);
            for (int z = 185; z <= 255; z++)
                for (int x = -36; x <= 36; x++)
                {
                    int bx = Math.Clamp((x + w.Half) * w.BiomeSize / w.Size, 0, w.BiomeSize - 1);
                    int by = Math.Clamp((w.Half - 1 - z) * w.BiomeSize / w.Size, 0, w.BiomeSize - 1);
                    w.Biomes[by * w.BiomeSize + bx] = 0;
                }
            var rules = new WildernessRules();
            rules.Adjusts.Add(new WildernessRules.Adjust("wild_cabin", new[] { 0 }, 0, 1));
            spec.Density = 120.0; // guarantees 1 POI in the tiny pine-forest patch
            var plan = new WildernessGenerator(w, spec, rules, SyntheticWildernessPois(), _ => null).Plan(null);
            Check.True(plan.Paths.Count >= 1, $"Expected path around tile for elevationDelta={elevationDelta:0} m");
            var path = plan.Paths[0];
            double minD = double.MaxValue;
            for (int i = 1; i < path.Points.Count; i++)
            {
                var a = path.Points[i - 1]; var b = path.Points[i];
                for (double t = 0; t <= 1.001; t += 0.1)
                {
                    var q = a + (b - a) * t;
                    minD = Math.Min(minD, RectDistToPrefab(q.X, q.Y, tile));
                }
            }
            // Build and verify terrain between tile edge and path stays smooth (no steep wall beside the tile)
            var beforeH = (ushort[])w.Heights.Clone();
            WildernessBuildResult buildRes = null;
            edits.Run("tile-ab-build", "", ed => buildRes = WildernessGenerator.Build(w, ed, plan, spec, null));
            Check.Equal(plan.Paths.Count, buildRes.Paths, $"All paths must build for elevationDelta={elevationDelta:0} m");
            double maxNewWall = 0;
            for (int z = tile.Z - 20; z <= tile.Z + tile.SizeZ + 20; z++)
                for (int x = tile.X - 20; x <= tile.X + tile.SizeX + 20; x++)
                {
                    if (tile.Contains(x + 0.5, z + 0.5)) continue;
                    int idx = w.Index(x, z);
                    if (w.Heights[idx] == beforeH[idx]) continue;
                    double h0 = w.Heights[idx] / 256.0, b0 = beforeH[idx] / 256.0;
                    foreach (var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                    {
                        int ni = w.Index(nx, nz);
                        double afterStep = Math.Abs(h0 - w.Heights[ni] / 256.0);
                        double beforeStep = Math.Abs(b0 - beforeH[ni] / 256.0);
                        if (afterStep > beforeStep + 0.05) maxNewWall = Math.Max(maxNewWall, afterStep);
                    }
                }
            Check.True(maxNewWall <= 0.65, $"Built path (dh={elevationDelta:0} m) caused a steep {maxNewWall:0.00} m step near the tile edge");
            return minD;
        }

        double flatDist = RunScenario(0.0);
        double midDist = RunScenario(6.0);
        double highDist = RunScenario(12.0);
        Console.WriteLine(FormattableString.Invariant(
            $"       A/B around city tile: flat(0m)={flatDist:0.0} m, mid(6m)={midDist:0.0} m, elevated(12m)={highDist:0.0} m"));
        Check.True(flatDist >= 18.0 - 0.5, $"Flat path came closer than base clearance 18 m: {flatDist:0.0} m");
        Check.True(midDist >= 36.0 - 0.5, $"Mid (+6 m) path came closer than 36 m clearance: {midDist:0.0} m");
        Check.True(highDist >= 54.0 - 0.5, $"Elevated (+12 m) path came closer than 54 m clearance: {highDist:0.0} m");
        Check.True(midDist > flatDist + 5.0 && highDist > midDist + 5.0,
            $"Clearance must grow steadily with path elevation: flat={flatDist:0.0} m, mid={midDist:0.0} m, high={highDist:0.0} m");
    }
}
