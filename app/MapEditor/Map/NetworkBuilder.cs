using System.Windows;

namespace MapEditor.Map;

/// result of "Build roads"
public sealed class NetworkBuildResult
{
    public int Roads, HeightPoints, Pixels, NodeTiles, NodeBuildings, Bridges;
    public double Length, MaxGrade, MaxCut, MaxFill;
    public double KinkLength;
    public double FillLength, LongestFill;  // embankment stretches (> 1.5 m above the terrain): total and longest
    public int SteepRoads;                // roads that had to become steeper than the usual gradient
    public List<string> Warnings = new();
}

/// Build roads between the cities (stage 2, the user's request, 2026-11-07): every planned road of the network (RoadNetwork)
/// is entered like a road-brush road – longitudinal profile with smoothing, embankments, surface. In the order of the plan:
/// each road only connects to earlier planned (so already built) roads.
/// • City exit: start 1 m in the tile (tile height, 4 m horizontal – automatic of the road brush), narrow exits
///   (8 m) widen to 16 m over 48 m.
/// • Junction (start/end on a built road): height of the road there, the piece on it and 2 m before fixed, tilted sideways
///   like the road rises there (at most 12 %); on its area the new road writes no heights.
/// • Intersection in the middle of the route: likewise, over the width of the crossed road + 3 m per side.
/// • Gradient: first with the usual gradient; if the cut/fill becomes deeper than 12 m (25 m for clearings), with the
///   gradient for narrow passages.
/// Long roads are entered piece by piece (RoadApplier with from/to), otherwise the grid would need hundreds of MB.
public static class NetworkBuilder
{
    const double TaperLength = 48;   // widening from the exit width to 16 m (m)
    const double GravelBridgeTaper = 32;   // a narrow gravel path widens to the approach over this length (m)
    const double BridgeTaper = 64;   // the road narrows to the bridge over this length (longer than at town exits: 6 m less width)
    const int SliceLength = 1600;    // waypoints per piece when entering (0.25 m → 400 m)
    // a road is at most this steep where a later road branches off or crosses it; the later road tilts sideways with it
    // (2026-11-26, the user's request: 4 % flattened steep roads too much and gave big embankments – now the same rule as the road brush)
    const double JunctionFlat = Junctions.AutoCrossMax;
    const double JunctionReach = 14;     // +- this far around the junction (half the width of a branch road + 6 m)

    /// lotBuildings: buildings (with their parts) for the lots of the node tiles, chosen by the main window (index of the buildings);
    /// null = lots stay empty
    public static NetworkBuildResult Build(WorldData w, WorldEdits e, NetworkPlan plan, NetworkSpec spec, IProgress<string> progress,
        IReadOnlyList<(PlacedPrefab Building, List<PlacedPrefab> Parts)> lotBuildings = null)
    {
        e.GuardBanks();   // the road cuts must not lower the banks of lakes
        var res = new NetworkBuildResult();
        var bridgeItems = plan.Roads.SelectMany(r => r.Bridges.Select(b => (B: b, Gravel: r.Gravel))).ToList();
        static double RoadWeight(NetRoad r) => 60.0 + Math.Max(40.0, r.Length);
        double wTiles = plan.NodeTiles.Count * 140.0;
        double wBridges = bridgeItems.Count * 120.0;
        double wRoads = plan.Roads.Sum(RoadWeight);
        double totalWork = Math.Max(1.0, wTiles + wBridges + wRoads);
        double doneWork = 0.0;
        double Frac() => Math.Clamp(doneWork / totalWork, 0.0, 0.99);

        // node tiles first (step 2, the user's request 2026-11-13): tile, its surface, level ground with a soft transition like the
        // towns – the roads then start 1 m in the tile at its height
        for (int ti = 0; ti < plan.NodeTiles.Count; ti++)
        {
            var tile = plan.NodeTiles[ti];
            progress.Report(Loc.T("Placing node tiles …"), Frac());
            e.AddPrefab(tile);
            e.AddTileSurface(tile);
            CityGenerator.ShapeTerrain(w, e, new CityPlan { Tiles = { tile }, X0 = tile.X, Z0 = tile.Z, X1 = tile.X + tile.SizeX, Z1 = tile.Z + tile.SizeZ });
            res.NodeTiles++;
            doneWork += 140.0;
        }
        // bridges over rivers (before the roads, which end on their approaches); the bank guard is off meanwhile: the bridge makes
        // the river straight and digs its bed (like the towns at a river)
        var kit = RoadNetwork.Bridges;
        for (int bi = 0; bi < bridgeItems.Count; bi++)
        {
            var (b, gravel) = bridgeItems[bi];
            b.Gravel = gravel;   // a gravel road gets gravel approaches
            var (endDef, tileDef) = kit?.Defs(b) ?? (null, null);
            if (endDef == null || (!b.Single && tileDef == null)) { res.Warnings.Add(Loc.F("Bridge at {0} not built: the pieces of its bridge kit were not found (Settings › Mods › Bridge mods).", Pos(b.Center))); doneWork += 120.0; continue; }
            progress.Report(bridgeItems.Count > 1 ? Loc.F("Building bridge {0} of {1} …", bi + 1, bridgeItems.Count) : Loc.T("Building bridges …"), Frac());
            bool keep = e.KeepBanksNow;
            e.KeepBanksNow = false;
            Bridge.Build(w, e, b, endDef, tileDef);
            e.KeepBanksNow = keep;
            res.Bridges++;
            doneWork += 120.0;
        }
        var built = new Dictionary<NetRoad, RoadResult>();
        var quiet = new Progress<string>(_ => { });
        for (int q = 0; q < plan.Roads.Count; q++)
        {
            var road = plan.Roads[q];
            double rw = RoadWeight(road);
            string roadText = Loc.F("Building road {0} of {1} ({2:0.0} km) …", q + 1, plan.Roads.Count, road.Length / 1000);
            progress.Report(roadText, Frac());
            var rr = PlanRoad(w, plan, road, spec, built, res);
            if (rr == null) { res.Warnings.Add(Loc.F("Road at {0} could not be built.", Pos(road.Points[0]))); doneWork += rw; continue; }
            var pieces = Pieces(rr, road.Bridges).ToList();
            var junctions = RoadApplier.CaptureJunctions(w, rr);
            int totalPts = Math.Max(1, pieces.Sum(p => Math.Max(0, p.To - p.From)));
            int donePts = 0;
            foreach (var (a, b) in pieces)
                for (int from = a; from < b; from += SliceLength)
                {
                    int to = Math.Min(b, from + SliceLength);
                    var st = RoadApplier.Apply(w, e, rr, quiet, from, to, 4, junctions);
                    res.HeightPoints += st.HeightPoints;
                    res.Pixels += st.Pixels;
                    donePts += to - from;
                    if (donePts < totalPts)
                        progress.Report(roadText, Math.Clamp((doneWork + rw * donePts / totalPts) / totalWork, 0.0, 0.99));
                }
            doneWork += rw;
            built[road] = rr;
            res.Roads++;
            res.Length += rr.Length;
            res.MaxGrade = Math.Max(res.MaxGrade, rr.MaxGrade);
            res.MaxCut = Math.Max(res.MaxCut, rr.MaxCut);
            res.MaxFill = Math.Max(res.MaxFill, rr.MaxFill);
            res.FillLength += rr.FillLength;
            res.KinkLength += rr.KinkLength;
            res.LongestFill = Math.Max(res.LongestFill, rr.LongestFill);
            if (rr.MaxGrade > spec.Grade + 0.005) res.SteepRoads++;
            if (rr.MaxGrade > spec.MaxGrade + 0.005)
                res.Warnings.Add(Loc.F("Road at {0}: exception – {1:0} % steep (allowed {2:0} %, short stretches up to {3:0} %).", Pos(road.Points[road.Points.Count / 2]), rr.MaxGrade * 100, spec.MaxGrade * 100, spec.ShortMaxGrade * 100));
            // buildings on the roadway – without those on lots of a tile (at a city exit the road starts 1 m in the
            // tile, everything stays as it is there)
            foreach (var o in rr.Obstacles.Where(o => o.Edge < 0 && !o.Prefab.IsTile && !InTile(w, o.Prefab) && !IsBridgePiece(o.Prefab)).Take(3))
                res.Warnings.Add(Loc.F("Road at {0} runs over {1}.", Pos(new Point(o.Prefab.X + o.Prefab.SizeX / 2.0, o.Prefab.Z + o.Prefab.SizeZ / 2.0)), o.Prefab.Name));
            // water under the bridges does not count (bridge length × width of the road there)
            int bridgeWater = road.Bridges.Sum(b => b.Length * (int)(road.Width + 2));
            if (rr.WaterArea > 50 + bridgeWater) res.Warnings.Add(Loc.F("Road at {0} touches water ({1} m²).", Pos(road.Points[road.Points.Count / 2]), rr.WaterArea));
        }
        // buildings on the lots of the node tiles last (like the towns: later rows of prefabs.xml overwrite earlier ones)
        foreach (var (b, parts) in lotBuildings ?? Array.Empty<(PlacedPrefab, List<PlacedPrefab>)>())
        {
            e.AddPrefab(b);
            foreach (var q in parts) e.AddPrefab(q);
            res.NodeBuildings++;
        }
        return res;
    }

    /// buildings for the lots of the node tiles: like in the game a roadside building (East Nipabu: roadside_checkpoint, hotel, motel) –
    /// first roadside_*, then motel/hotel, then gas station/truck stop, otherwise any fitting one (never a trader); as few repeats as possible
    public static List<(PlacedPrefab Building, List<PlacedPrefab> Parts)> NodeLotBuildings(NetworkPlan plan, IReadOnlyDictionary<string, PrefabDef> index,
        Func<PlacedPrefab, string, List<PlacedPrefab>> partsOf, string seed)
    {
        var res = new List<(PlacedPrefab, List<PlacedPrefab>)>();
        if (index == null) return res;
        var rnd = new Random(new GeneratorSpec { Seed = seed + "/nodelots" }.SeedValue);
        var use = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static int Rank(string n) => n.StartsWith("roadside_", StringComparison.OrdinalIgnoreCase) ? 0
            : n.Contains("motel", StringComparison.OrdinalIgnoreCase) || n.Contains("hotel", StringComparison.OrdinalIgnoreCase) ? 1
            : n.Contains("gas_station", StringComparison.OrdinalIgnoreCase) || n.Contains("truck", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        foreach (var tile in plan.NodeTiles)
            foreach (var lot in TileLots.Of(tile))
            {
                var fits = index.Where(kv => !kv.Key.StartsWith("trader_", StringComparison.OrdinalIgnoreCase) && TileLots.Fits(kv.Value, lot))
                    .Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                if (fits.Count == 0) continue;
                int best = fits.Min(Rank);
                string name = fits.Where(k => Rank(k) == best).OrderBy(k => use.GetValueOrDefault(k) * 10 + rnd.NextDouble()).First();
                use[name] = use.GetValueOrDefault(name) + 1;
                var b = TileLots.Place(lot, name, index[name]);
                res.Add((b, partsOf?.Invoke(b, lot.District) ?? new List<PlacedPrefab>()));
            }
        return res;
    }

    static bool IsBridgePiece(PlacedPrefab p) => BridgeKits.IsBridgePiece(p.Name);

    /// the road overlaps the approach of a bridge this far (m); further in the bridge itself and its approaches stay as Bridge.Build made them
    const double BridgeOverlap = 6;

    /// outer ends of the approaches of a bridge (low side, high side along the span) and the height of the approach road
    internal static (Point A, Point B, double Height) ApproachEnds(BridgeSite b)
    {
        double t = b.MidT;
        Point At(double s) => b.AlongX ? new Point(s, t) : new Point(t, s);
        return (At(b.S0 - Bridge.Approach), At(b.S0 + b.Length + Bridge.Approach), b.Deck - 1 + b.ApproachLift);
    }

    /// along the road: from where to where the bridges lie with their approaches (distance from the start, sorted)
    internal static List<(double From, double To, double Height)> BridgeZones(RoadResult r, IEnumerable<BridgeSite> bridges)
    {
        var res = new List<(double, double, double)>();
        foreach (var b in bridges)
        {
            var (pa, pb, h) = ApproachEnds(b);
            double da = r.Path[Nearest(r, pa)].Dist, db = r.Path[Nearest(r, pb)].Dist;
            res.Add((Math.Min(da, db), Math.Max(da, db), h));
        }
        return res.OrderBy(z => z.Item1).ToList();
    }

    /// waypoint ranges of the road that are entered: everything except the bridges (the road reaches BridgeOverlap onto each approach)
    internal static List<(int From, int To)> Pieces(RoadResult r, IEnumerable<BridgeSite> bridges)
    {
        var res = new List<(int, int)>();
        int start = 0;
        foreach (var (a, b, _) in BridgeZones(r, bridges))
        {
            int stop = r.IndexAt(Math.Clamp(a + BridgeOverlap, 0, r.Length));
            if (stop > start) res.Add((start, stop));
            start = Math.Max(start, r.IndexAt(Math.Clamp(b - BridgeOverlap, 0, r.Length)) + 1);
        }
        if (start < r.Path.Count) res.Add((start, r.Path.Count));
        return res;
    }

    static bool InTile(WorldData w, PlacedPrefab p)
    {
        double cx = p.X + p.SizeX / 2.0, cz = p.Z + p.SizeZ / 2.0;
        return w.Prefabs.Any(t => t.IsTile && t.Contains(cx, cz));
    }

    static string Pos(Point p) => Loc.F("{0} {1}, {2} {3}", Math.Abs(Math.Round(p.X)), p.X < 0 ? Loc.T("W") : Loc.T("E"), Math.Abs(Math.Round(p.Y)), p.Y < 0 ? Loc.T("S") : Loc.T("N"));

    /// height, direction and gradient of a built road at the place nearest to p
    internal static (double Height, double DX, double DZ, double Grade, double Half) At(RoadResult r, Point p)
    {
        int best = 0; double bd = double.MaxValue;
        for (int k = 0; k < r.Path.Count; k += 2)
        {
            double d = (r.Path[k].X - p.X) * (r.Path[k].X - p.X) + (r.Path[k].Z - p.Y) * (r.Path[k].Z - p.Y);
            if (d < bd) { bd = d; best = k; }
        }
        int a = r.IndexAt(Math.Max(0, r.Path[best].Dist - 10)), b = r.IndexAt(Math.Min(r.Length, r.Path[best].Dist + 10));
        double len = Math.Max(1, r.Path[b].Dist - r.Path[a].Dist);
        var (nx, nz) = RoadPlanner.Normal(r.Path, best);
        return (r.Profile[best], nz, -nx, (r.Profile[b] - r.Profile[a]) / len, r.WidthAt(best) / 2 + r.Settings.Shoulder);
    }

    internal static RoadResult PlanRoad(WorldData w, NetworkPlan plan, NetRoad road, NetworkSpec spec, Dictionary<NetRoad, RoadResult> built, NetworkBuildResult res)
    {
        // control points: 1 m in the start/end tile, between them the planned line every 12 m (even spacing, otherwise
        // the curve of the road brush overshoots)
        var pts = new List<Point>();
        if (road.From?.Tile != null) pts.Add(new Point(road.From.X - road.From.DX, road.From.Z - road.From.DZ));
        pts.AddRange(Even(road.Points, 12));
        if (road.To?.Tile != null) pts.Add(new Point(road.To.X - road.To.DX, road.To.Z - road.To.DZ));

        var s = road.Gravel ? RoadSettings.Gravel() : RoadSettings.Asphalt();
        s.Width = road.Width;
        s.Shoulder = road.Gravel ? 0 : 1;
        s.GrassEdge = 1;   // 1 m level verge of biome ground, so the slope never starts right at the road edge
        s.Smooth = road.Gravel ? 40 : 100;
        s.Blend = road.Gravel ? 8 : 12;
        s.CutSlope = 1.5;
        s.CurveGrade = true;   // flatter in tight curves (the user's photos 2026-11-20: curve + gradient too strong)
        s.KeepRoads = true;    // existing roads keep their heights everywhere (OnOldRoad cleared its own old surface first, so the new bridge road stays protected)
        if (road.OnOldRoad) { s.UniformGrade = true; s.FlatStart = 0; s.FlatEnd = 0; }   // one even gradient from the old branch to the new road (the ground there is the bed of the removed road)
        s.BreakInWasteland = spec.BreakInWasteland;
        if (spec.RoadFill >= 0) { s.TubeFill = spec.RoadFill; s.TubeCut = spec.RoadCut; s.TubeEdgeFill = spec.RoadEdgeFill; }

        // width per control point: narrow city exits widen to the full width over TaperLength
        var dist = new double[pts.Count];
        for (int k = 1; k < pts.Count; k++) dist[k] = dist[k - 1] + (pts[k] - pts[k - 1]).Length;
        double total = dist[^1];
        double? startW = !road.Gravel && (road.From?.Tile != null || road.From?.Street == true) && road.From.Width < road.Width ? road.From.Width : null;
        double? endW = !road.Gravel && (road.To?.Tile != null || road.To?.Street == true) && road.To.Width < road.Width ? road.To.Width : null;
        // bridges: the road is narrowed to the width of the bridge over TaperLength in front of the approaches
        var bridgeAt = new List<(double From, double To, double Width)>();
        foreach (var b in road.Bridges)
        {
            var (pa, pb, _) = ApproachEnds(b);
            int ka = 0, kb = 0;
            for (int k = 1; k < pts.Count; k++)
            {
                if ((pts[k] - pa).LengthSquared < (pts[ka] - pa).LengthSquared) ka = k;
                if ((pts[k] - pb).LengthSquared < (pts[kb] - pb).LengthSquared) kb = k;
            }
            bridgeAt.Add((Math.Min(dist[ka], dist[kb]), Math.Max(dist[ka], dist[kb]), b.RoadWidth - 2.0));
        }
        List<double?> widths = null;
        if (startW != null || endW != null || bridgeAt.Count > 0)
        {
            widths = new List<double?>();
            for (int k = 0; k < pts.Count; k++)
            {
                double wv = road.Width;
                if (startW is double a && dist[k] < TaperLength) { double t = dist[k] / TaperLength; wv = Math.Min(wv, a + (road.Width - a) * t * t * (3 - 2 * t)); }
                if (endW is double b && total - dist[k] < TaperLength) { double t = (total - dist[k]) / TaperLength; wv = Math.Min(wv, b + (road.Width - b) * t * t * (3 - 2 * t)); }
                foreach (var (z0, z1, bw) in bridgeAt)
                {
                    double off = Math.Max(0, Math.Max(z0 - dist[k], dist[k] - z1));
                    if (road.Gravel)
                    {
                        // a gravel path has no gravel edge: it widens to the whole approach (all gravel, as wide as the bridge)
                        double full = bw + 2;
                        if (off >= GravelBridgeTaper || full <= road.Width) continue;
                        double tg = off / GravelBridgeTaper;
                        wv = Math.Max(wv, full + (road.Width - full) * tg * tg * (3 - 2 * tg));
                        continue;
                    }
                    if (off >= BridgeTaper || bw >= road.Width) continue;
                    double t = off / BridgeTaper;
                    wv = Math.Min(wv, bw + (road.Width - bw) * t * t * (3 - 2 * t));
                }
                widths.Add(wv);
            }
        }

        // connections to built roads: start/end (junction) and intersections
        var heights = new double?[pts.Count];
        var joints = new List<(Point P, NetRoad Other, bool AtStart, bool AtEnd)>();
        if (road.StartRoad != null) joints.Add((pts[0], road.StartRoad, true, false));
        if (road.EndRoad != null) joints.Add((pts[^1], road.EndRoad, false, true));
        for (int c = 0; c < road.Crossings.Count && c < road.CrossedRoads.Count; c++) joints.Add((road.Crossings[c], road.CrossedRoads[c], false, false));
        // places where roads built later branch off or cross this one
        var later = new List<Point>();
        foreach (var o in plan.Roads)
        {
            if (o == road || o.Points.Count < 2) continue;
            if (o.StartRoad == road) later.Add(o.Points[0]);
            if (o.EndRoad == road) later.Add(o.Junction ?? o.Points[^1]);
            for (int c = 0; c < o.Crossings.Count && c < o.CrossedRoads.Count; c++) if (o.CrossedRoads[c] == road) later.Add(o.Crossings[c]);
        }
        var known = joints.Where(j => built.ContainsKey(j.Other)).Select(j => (j.P, Info: At(built[j.Other], j.P), j.AtStart, j.AtEnd)).ToList();
        // town connection or exit on a town street: starts/ends on an existing town street – its height, gradient and width as with a built road
        double H(double x, double z) => w.PreDamageHeightAt(x, z, spec.BreakInWasteland);
        foreach (var (f, atStart) in new[] { (road.From, true), (road.To, false) })
        {
            if (f?.Street != true) continue;
            double grade = (H(f.X + f.StreetDX * 10, f.Z + f.StreetDZ * 10) - H(f.X - f.StreetDX * 10, f.Z - f.StreetDZ * 10)) / 20;
            known.Add((atStart ? pts[0] : pts[^1], (H(f.X, f.Z), f.StreetDX, f.StreetDZ, grade, f.StreetHalf), atStart, !atStart));
        }
        // crossings with existing streets (town streets between the parts of a town, see RoadNetwork.Streets): like a crossing
        // with a built road – at the height of the street, tilted with its gradient, the street itself stays unchanged
        // the approaches of the bridges are asphalt too, but no street to cross
        var bridgePoints = road.Bridges.SelectMany(b => { var (pa, pb, _) = ApproachEnds(b); return new[] { pa, pb, b.Center, pa + (b.Center - pa) * 0.25, pb + (b.Center - pb) * 0.25 }; }).ToList();
        foreach (var ex in new[] { road.From, road.To }) if (ex?.Bridge != null) bridgePoints.Add(new Point(ex.X, ex.Z));   // the approach is no crossing street
        var noBreak = bridgePoints.Count > 0 ? bridgePoints.Select(p => (p, 12.0)).ToList() : null;
        var taken = known.Select(j => j.P).Concat(later).Concat(bridgePoints).ToList();
        foreach (var sc in StreetCrossings(w, road.Points, taken, spec.BreakInWasteland))
            known.Add((sc.P, (sc.Height, sc.DX, sc.DZ, sc.Grade, sc.Half), false, false));
        foreach (var j in known)
        {
            // for existing streets (From/To.Street), RoadPlanner's GroundAt computes the exact DamageWeight-consistent height at pts[0]/pts[^1];
            // only previously built roads in the same plan (StartRoad/EndRoad) dictate an explicit end height here
            if (j.AtStart && road.From?.Street != true) heights[0] = j.Info.Height;
            if (j.AtEnd && road.To?.Street != true) heights[^1] = j.Info.Height;
        }
        // end at the outer end of a bridge approach (route tool): at its height, 4 m level like the road tools
        if (road.From?.Height is double hs) { heights[0] = hs; if (s.FlatStart < 0) s.FlatStart = 4; }
        if (road.To?.Height is double he) { heights[^1] = he; if (s.FlatEnd < 0) s.FlatEnd = 4; }

        RoadResult Compute(double grade)
        {
            s.MaxGrade = grade;
            var r0 = RoadPlanner.Compute(w, pts, s, heights, widths: widths, noBreakZones: noBreak);
            if (r0 == null) return null;
            var fixes = new List<(double, double)>();
            // later roads branch off or cross here: flatten this road there to at most JunctionFlat (like the road brush's
            // "junction level up to"), so that the junction does not lie on a steep, tilted piece
            foreach (var lp in later)
            {
                int k = Nearest(r0, lp);
                double d = r0.Path[k].Dist;
                if (d < 20 || d > r0.Length - 20) continue;
                int a = r0.IndexAt(Math.Max(0, d - 10)), b = r0.IndexAt(Math.Min(r0.Length, d + 10));
                double g = (r0.Profile[b] - r0.Profile[a]) / Math.Max(1, r0.Path[b].Dist - r0.Path[a].Dist);
                if (Math.Abs(g) <= JunctionFlat) continue;
                double gc = Math.Clamp(g, -JunctionFlat, JunctionFlat);
                for (double x = -JunctionReach; x <= JunctionReach + 0.01; x += 2) fixes.Add((d + x, r0.Profile[k] + gc * x));
            }
            // bridges: from the outer end of one approach to the other at the height of the approach road (level), rounded into the course
            var bridgeEnds = new List<(double, int, double, double)>();
            var levels = BridgeZones(r0, road.Bridges);
            foreach (var (za, zb, zh) in levels)
            {
                for (double x = za; x <= zb + 0.01; x += 1) fixes.Add((x, zh));
                bridgeEnds.Add((za, -1, zh, 0));
                bridgeEnds.Add((zb, 1, zh, 0));
            }
            var cross = new List<(double, double, double)>();
            var zones = new List<(Point, double)>();
            var ends = new List<(double Dist, int Sign, double H, double Grade)>(bridgeEnds);   // ends of the level pieces (for rounding)
            var hFixed = (double?[])heights.Clone();
            bool clipStart = false, clipEnd = false;
            // horizontal piece behind a tile/building entrance: round its end into the course as well (the user's photos 2026-11-21, UL-Land-Of-Fear
            // -403/106: flat, then a short steep ramp down, then flat again – a knee at the end of the 4 m level piece)
            if (r0.FlatStartUsed > 0 && r0.FlatStartUsed < r0.Length - 2)
                ends.Add((r0.FlatStartUsed, 1, r0.Profile[r0.IndexAt(r0.FlatStartUsed)], 0));
            if (r0.FlatEndUsed > 0 && r0.FlatEndUsed < r0.Length - 2)
                ends.Add((r0.Length - r0.FlatEndUsed, -1, r0.Profile[r0.IndexAt(r0.Length - r0.FlatEndUsed)], 0));
            if (known.Count == 0 && fixes.Count == 0 && ends.Count == 0) return r0;
            // measure the existing road's real sideways banking along (nx, nz) into the untouched road beyond a colinear joint
            double MeasureBank(Point jp, double nx, double nz, double outX, double outZ, double roadHalf)
            {
                double mh = Math.Max(2, Math.Min(roadHalf - 1.5, road.Width / 2));
                double sum = 0; int cnt = 0;
                for (double off = 0; off <= 6; off += 2)
                {
                    double qx = jp.X + outX * off, qz = jp.Y + outZ * off;
                    if (w.RoadAt((int)Math.Floor(qx + nx * mh), (int)Math.Floor(qz + nz * mh)) == 0
                        || w.RoadAt((int)Math.Floor(qx - nx * mh), (int)Math.Floor(qz - nz * mh)) == 0) continue;
                    sum += (H(qx + nx * mh, qz + nz * mh) - H(qx - nx * mh, qz - nz * mh)) / (2 * mh);
                    cnt++;
                }
                return cnt > 0 ? Math.Clamp(sum / cnt, -0.15, 0.15) : 0;
            }
            (double OutX, double OutZ) OutDir(bool atStart)
            {
                int pn0 = r0.Path.Count;
                double dx = atStart ? r0.Path[0].X - r0.Path[Math.Min(4, pn0 - 1)].X : r0.Path[pn0 - 1].X - r0.Path[Math.Max(0, pn0 - 5)].X;
                double dz = atStart ? r0.Path[0].Z - r0.Path[Math.Min(4, pn0 - 1)].Z : r0.Path[pn0 - 1].Z - r0.Path[Math.Max(0, pn0 - 5)].Z;
                double l = Math.Sqrt(dx * dx + dz * dz);
                return l > 1e-6 ? (dx / l, dz / l) : (1, 0);
            }
            foreach (var (p, info, atStart, atEnd) in known)
            {
                double d = atStart ? 0 : atEnd ? r0.Length : r0.Path[Nearest(r0, p)].Dist;
                var (nx, nz) = RoadPlanner.Normal(r0.Path, r0.IndexAt(d));
                bool isStreet = (atStart && road.From?.Street == true) || (atEnd && road.To?.Street == true);
                bool colinear = isStreet && Math.Abs(info.DX * nx + info.DZ * nz) < 0.5;
                // laid again on an old road (branch after a bridge swap): one even gradient between the fixed end heights, tilted sideways
                // at its mouth to match the longitudinal grade of the new road, and keeping the new road protected at the junction
                if (road.OnOldRoad && (atStart || atEnd))
                {
                    if (atEnd)
                    {
                        double dEnd = r0.Length;
                        var (enx, enz) = RoadPlanner.Normal(r0.Path, r0.IndexAt(dEnd));
                        double eslope = Math.Clamp(info.Grade * (info.DX * enx + info.DZ * enz), -0.12, 0.12);
                        if (Math.Abs(eslope) > 0.001) cross.Add((dEnd, eslope, info.Half + 3));
                        zones.Add((p, info.Half + 1));
                    }
                    else if (colinear)
                    {
                        var (ox, oz) = OutDir(true);
                        hFixed[0] = r0.Terrain[0];   // pin to the existing branch height (overrides any nearby POI TerrainHeight)
                        double bank = MeasureBank(p, nx, nz, ox, oz, info.Half);
                        if (Math.Abs(bank) > 0.001) cross.Add((0, bank, Math.Max(4, info.Half)));
                        clipStart = true;
                    }
                    continue;
                }
                if (colinear)
                {
                    // Colinear joint continuing seamlessly into an untouched existing road (bridge swap / re-lay road, user photos 2026-11-27
                    // Minimi -301,-250): match the untouched road's real sideways banking (CrossSlope) and longitudinal slope, and clip the
                    // round end cap cleanly at the joint plane so no mound or asphalt spikes stick out into the old road.
                    var (ox, oz) = OutDir(atStart);
                    double hp = atStart ? r0.Terrain[0] : r0.Terrain[^1];
                    double gIn = atStart
                        ? Math.Clamp((H(p.X, p.Y) - H(p.X + ox * 6, p.Y + oz * 6)) / 6.0, -s.MaxGrade, s.MaxGrade)
                        : Math.Clamp((H(p.X + ox * 6, p.Y + oz * 6) - H(p.X, p.Y)) / 6.0, -s.MaxGrade, s.MaxGrade);
                    if (atStart)
                    {
                        hFixed[0] = hp;
                        fixes.Add((1, hp + gIn));
                        fixes.Add((2, hp + gIn * 2));
                        ends.Add((2, 1, hp + gIn * 2, gIn));
                    }
                    else
                    {
                        hFixed[^1] = hp;
                        fixes.Add((r0.Length - 1, hp - gIn));
                        fixes.Add((r0.Length - 2, hp - gIn * 2));
                        ends.Add((r0.Length - 2, -1, hp - gIn * 2, gIn));
                    }
                    double bank = MeasureBank(p, nx, nz, ox, oz, info.Half);
                    if (Math.Abs(bank) > 0.001) cross.Add((d, bank, Math.Max(4, info.Half)));
                    zones.Add((p, info.Half + 1));
                    if (atStart) clipStart = true; else clipEnd = true;
                    continue;
                }
                // On the surface of the other road the new road lies exactly on it (its real heights along the new centre line, with its
                // banking and crown – the user's game test 2026-11-17: a level piece at the height of the middle of the other road rose
                // above its lower edge, the road went up a little and right down again behind the junction). From the edge of the other
                // road on, the new road continues with the gradient it has there (rounded into its course after the first pass).
                double T(double dist) => r0.Terrain[r0.IndexAt(Math.Clamp(dist, 0, r0.Length))];
                double edge = info.Half;
                double EdgeGrade(double de, int sign) => Math.Clamp((T(de) - T(de - 3 * sign)) / (3.0 * sign), -s.MaxGrade, s.MaxGrade);
                void Piece(double from, double to) { for (double x = Math.Max(0, from); x <= Math.Min(r0.Length, to) + 0.01; x += 1) fixes.Add((x, T(x))); }
                if (atStart) { Piece(0, edge); ends.Add((edge, 1, T(edge), EdgeGrade(edge, 1))); }
                else if (atEnd) { Piece(r0.Length - edge, r0.Length); ends.Add((r0.Length - edge, -1, T(r0.Length - edge), EdgeGrade(r0.Length - edge, -1))); }
                else
                {
                    Piece(d - edge, d + edge);
                    ends.Add((d + edge, 1, T(d + edge), EdgeGrade(d + edge, 1)));
                    ends.Add((d - edge, -1, T(d - edge), EdgeGrade(d - edge, -1)));
                }
                // tilted sideways like the crossed road rises there (its share across the new road)
                double slope = Math.Clamp(info.Grade * (info.DX * nx + info.DZ * nz), -0.12, 0.12);
                if (Math.Abs(slope) > 0.001) cross.Add((d, slope, info.Half + 3));
                zones.Add((p, info.Half + 1));
            }
            var r1 = RoadPlanner.Compute(w, pts, s, hFixed, fixes, cross, widths, levels, noBreak);
            if (r1 != null && ends.Count > 0)
            {
                // round the knee between the level piece and the course (vertical curve): a cubic from the level piece (its gradient) to the
                // course of the first pass (its height and gradient there); as short as possible, but nowhere steeper than allowed
                double Pr(double dist) => r1.Profile[r1.IndexAt(Math.Clamp(dist, 0, r1.Length))];
                var more = new List<(double, double)>(fixes);
                foreach (var (d0, sg, h0, g0) in ends)
                {
                    // at a bridge the road leaves the level approach gently (at least 32 m; 16 m gave a ramp right behind the approach); if the
                    // course climbs at the full gradient right behind it, the curve may be a third steeper for a short way (at most the gradient of short exceptions)
                    bool atBridge = bridgeEnds.Any(x => x.Item1 == d0 && x.Item2 == sg);
                    double allowed = atBridge ? Math.Max(s.MaxGrade, Math.Min(1.35 * s.MaxGrade, Math.Max(spec.MaxGrade, spec.ShortMaxGrade))) : s.MaxGrade;
                    foreach (double L in atBridge ? new[] { 32.0, 45, 60, 80, 100, 120 } : new[] { 16.0, 24, 32, 45, 60, 80 })
                    {
                        double d1 = d0 + sg * L;
                        if (d1 <= 2 || d1 >= r1.Length - 2) break;
                        double h1 = Pr(d1), g1 = (Pr(d1 + 2) - Pr(d1 - 2)) / 4;
                        double a = Math.Min(d0, d1), b = Math.Max(d0, d1), ha = sg > 0 ? h0 : h1, hb = sg > 0 ? h1 : h0, ga = sg > 0 ? g0 : g1, gb = sg > 0 ? g1 : g0;
                        var curve = new List<(double, double)>();
                        double steepest = 0, prev = ha;
                        for (double x = a + 1; x < b; x += 1)
                        {
                            double u = (x - a) / (b - a), u2 = u * u, u3 = u2 * u;
                            double h = (2 * u3 - 3 * u2 + 1) * ha + (u3 - 2 * u2 + u) * (b - a) * ga + (-2 * u3 + 3 * u2) * hb + (u3 - u2) * (b - a) * gb;
                            steepest = Math.Max(steepest, Math.Abs(h - prev));
                            prev = h;
                            curve.Add((x, h));
                        }
                        double lim = atBridge ? Math.Max(allowed, Math.Min(1.35 * Math.Abs(g1), Math.Max(spec.MaxGrade, spec.ShortMaxGrade))) : allowed;
                        if (steepest > Math.Max(lim, Math.Max(Math.Abs(g0), Math.Abs(g1))) + 0.01) continue;
                        more.AddRange(curve);
                        break;
                    }
                }
                if (more.Count > fixes.Count && RoadPlanner.Compute(w, pts, s, hFixed, more, cross, widths, levels, noBreak) is RoadResult r2) r1 = r2;
            }
            if (r1 != null)
            {
                if (zones.Count > 0) r1.ProtectZones = zones;
                r1.ClipStart = clipStart;
                r1.ClipEnd = clipEnd;
            }
            return r1;
        }

        // exception (the user's request 2026-11-12): short roads may become up to ShortMaxGrade steep to save earthwork (reported as a warning)
        bool shortRoad = total <= spec.ShortSteepLength;
        var r = Compute(spec.Grade);
        if (r == null) return null;
        double limit = road.Cut ? 25 : 12;
        // embankments count double (they stand out, wide slope): cut/fill too deep or long embankment stretch → check with the higher
        // gradient whether it gives less earthwork
        double Earth(RoadResult q) => Math.Max(q.MaxCut, q.MaxFill);
        if ((Math.Max(r.MaxCut, r.MaxFill) > limit || r.LongestFill > 300) && spec.MaxGrade > spec.Grade)
        {
            var steeper = Compute(spec.MaxGrade);
            if (steeper != null && Earth(steeper) + steeper.LongestFill / 200 < Earth(r) + r.LongestFill / 200) r = steeper;
        }
        if (shortRoad && spec.ShortMaxGrade > spec.MaxGrade && Earth(r) > 3)
        {
            var steepest = Compute(spec.ShortMaxGrade);
            if (steepest != null && Earth(steepest) < Earth(r) - 1) r = steepest;
        }
        // calm places: junctions and crossings (with earlier and later roads), ends at town exits and town streets
        var calm = known.Select(j => j.P).Concat(later).Concat(bridgePoints).ToList();
        if (road.CurveBanking > 0) NaturalCross(w, r, road, calm, road.CurveBanking, curvesOnly: true);   // route tool option: banking in curves only
        else NaturalCross(w, r, road, calm, road.Gravel ? 0.03 : 0.025);
        return r;
    }

    /// places where the line crosses an existing road (surface outside the tiles) that is not a known junction/crossing:
    /// stretches of surface along the line up to 30 m long (longer = the line runs along the road, not across), not within
    /// 40 m of the ends and not within 30 m of a known joint. Center, direction and width of the street there.
    internal static List<(Point P, double Height, double DX, double DZ, double Grade, double Half)> StreetCrossings(WorldData w, List<Point> line, List<Point> taken, bool breakInWasteland = false)
    {
        var res = new List<(Point, double, double, double, double, double)>();
        if (line == null || line.Count < 2) return res;
        double total = 0;
        for (int q = 1; q < line.Count; q++) total += (line[q] - line[q - 1]).Length;
        if (total < 100) return res;
        var tiles = w.PrefabSnapshot().Where(p => p.IsTile).ToList();
        bool Paved(Point p)
        {
            int ix = (int)Math.Floor(p.X), iz = (int)Math.Floor(p.Y);
            return w.RoadAt(ix, iz) != 0 && !tiles.Any(t => t.Contains(p.X, p.Y));
        }
        double H(Point p) => w.PreDamageHeightAt(p.X, p.Y, breakInWasteland);
        RoadScan scan = null;
        var found = new List<Point>();
        double walked = 0, runStart = -1;
        Point runFrom = default;
        for (int q = 1; q < line.Count; q++)
        {
            var a = line[q - 1]; var seg = line[q] - a;
            double len = seg.Length;
            for (double t = 0; t < len; t += 1)
            {
                double d = walked + t;
                var p = a + seg * (t / len);
                bool on = d > 40 && d < total - 40 && Paved(p);
                if (on && runStart < 0) { runStart = d; runFrom = p; }
                else if (!on && runStart >= 0)
                {
                    double runLen = d - runStart;
                    var mid = runFrom + (p - runFrom) * 0.5;
                    runStart = -1;
                    if (runLen > 30 || taken.Any(k => (k - mid).Length < 30) || found.Any(k => (k - mid).Length < 30)) continue;
                    scan ??= new RoadScan(w);
                    var spot = RoadFollower.Center(scan, mid);
                    if (spot == null || (spot.Center - mid).Length > 12) continue;
                    found.Add(mid);
                    double grade = (H(spot.Center + spot.Dir * 10) - H(spot.Center - spot.Dir * 10)) / 20;
                    res.Add((spot.Center, H(spot.Center), spot.Dir.X, spot.Dir.Y, grade, spot.Width / 2 + 1));
                }
            }
            walked += len;
        }
        return res;
    }

    /// gentle natural cross slope along the whole road (the user's hint 2026-11-12: the roads "look symmetric"): in curves the inner
    /// side lower (radius 100 m = full value), on a hillside slightly towards the valley (30 % of the side slope of the terrain), plus a
    /// slow swing (wave 110–170 m, ±0.8 %); together at most max, averaged over ±32 m. Calm (0) around junctions/crossings (their
    /// own cross slope stays) and at the first/last 40 m at town exits and town streets, faded in over 25 m.
    /// curvesOnly (route tool, the user's request 2026-11-17): only the banking in curves, full value from a radius of 80 m (tighter
    /// curves: also max), no hillside share and no swing
    static void NaturalCross(WorldData w, RoadResult r, NetRoad road, List<Point> calm, double max, bool curvesOnly = false)
    {
        var path = r.Path;
        int n = path.Count;
        if (n < 400 || r.CrossSlope == null) return;
        const int Stride = 16;   // every 4 m
        int m = (n - 1) / Stride + 1;
        var raw = new double[m];
        var rnd = new Random(HashCode.Combine(Math.Round(path[0].X), Math.Round(path[0].Z), Math.Round(path[^1].X), Math.Round(path[^1].Z)));
        double wave = 110 + 60 * rnd.NextDouble(), phase = rnd.NextDouble() * 2 * Math.PI;
        for (int q = 0; q < m; q++)
        {
            int k = q * Stride;
            int a = Math.Max(0, k - 80), b = Math.Min(n - 1, k + 80), a2 = Math.Max(0, k - 40), b2 = Math.Min(n - 1, k + 40);
            // curvature: change of the driving direction over ±20 m
            double h1 = Math.Atan2(path[a2].Z - path[a].Z, path[a2].X - path[a].X), h2 = Math.Atan2(path[b].Z - path[b2].Z, path[b].X - path[b2].X);
            double dh = h2 - h1;
            while (dh > Math.PI) dh -= 2 * Math.PI;
            while (dh < -Math.PI) dh += 2 * Math.PI;
            double len = Math.Max(1, path[b].Dist - path[a].Dist);
            double bank = -dh / len * (curvesOnly ? 80 * max : 2.5);   // left turn (angle grows) = left (inner) side lower
            // hillside: terrain beside the road
            var (nx, nz) = RoadPlanner.Normal(path, k);
            double d = r.WidthAt(k) / 2 + 8;
            double hl = w.PreDamageHeightAt(path[k].X + nx * d, path[k].Z + nz * d, r.Settings.BreakInWasteland);
            double hr = w.PreDamageHeightAt(path[k].X - nx * d, path[k].Z - nz * d, r.Settings.BreakInWasteland);
            double side = 0.3 * (hl - hr) / (2 * d);
            double swing = 0.008 * Math.Sin(2 * Math.PI * path[k].Dist / wave + phase);
            raw[q] = curvesOnly ? Math.Clamp(bank, -max, max) : Math.Clamp(bank + Math.Clamp(side, -max, max) + swing, -max, max);
        }
        var avg = new double[m];
        for (int q = 0; q < m; q++)
        {
            double s = 0; int c = 0;
            for (int t = Math.Max(0, q - 8); t <= Math.Min(m - 1, q + 8); t++) { s += raw[t]; c++; }
            avg[q] = s / c;
        }
        // calm factor: 0 near junctions/crossings and at exits, smoothstep over 25 m
        var calmAt = calm.Select(p => path[Nearest(r, p)].Dist).ToList();
        bool calmStart = road.From != null || road.StartRoad != null, calmEnd = road.To != null || road.EndRoad != null;
        double total = path[^1].Dist;
        for (int k = 0; k < n; k++)
        {
            if (r.CrossSlope[k] != 0) continue;   // cross slope of a junction applies
            double dist = path[k].Dist, near = double.MaxValue;
            foreach (double cd in calmAt) near = Math.Min(near, Math.Abs(dist - cd) - 22);
            if (calmStart) near = Math.Min(near, dist - 40);
            if (calmEnd) near = Math.Min(near, total - dist - 40);
            double f = Math.Clamp(near / 25, 0, 1);
            if (f <= 0) continue;
            f = f * f * (3 - 2 * f);
            double t = (double)k / Stride;
            int q0 = Math.Min(m - 1, (int)t), q1 = Math.Min(m - 1, q0 + 1);
            r.CrossSlope[k] = f * (avg[q0] + (avg[q1] - avg[q0]) * (t - q0));
        }
    }

    static int Nearest(RoadResult r, Point p)
    {
        int best = 0; double bd = double.MaxValue;
        for (int k = 0; k < r.Path.Count; k++)
        {
            double d = (r.Path[k].X - p.X) * (r.Path[k].X - p.X) + (r.Path[k].Z - p.Y) * (r.Path[k].Z - p.Y);
            if (d < bd) { bd = d; best = k; }
        }
        return best;
    }

    /// line at even spacing (at most step), start and end stay
    internal static List<Point> Even(List<Point> line, double step)
    {
        double total = 0;
        for (int q = 1; q < line.Count; q++) total += (line[q] - line[q - 1]).Length;
        int parts = Math.Max(1, (int)Math.Ceiling(total / step));
        double each = total / parts;
        var res = new List<Point> { line[0] };
        double target = each, walked = 0;
        for (int q = 1; q < line.Count && res.Count < parts; q++)
        {
            var a = line[q - 1]; var b = line[q];
            double len = (b - a).Length;
            while (len > 0 && walked + len >= target - 1e-9 && res.Count < parts)
            {
                res.Add(a + (b - a) * ((target - walked) / len));
                target += each;
            }
            walked += len;
        }
        res.Add(line[^1]);
        return res;
    }
}
