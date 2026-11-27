using System.Windows;

namespace MapEditor.Map;

/// Roads over rivers (the user's request 2026-11-19, FLUSS_ANALYSE.md point 2): before planning, places for a bridge are looked
/// for along the rivers (Bridge.Find: a piece of river flowing roughly north–south or west–east, narrow, not at a lake). For the search such a
/// place is a jump from the cell in front of one approach straight to the cell in front of the other one (like an intersection
/// over a road). A road that uses it gets the bridge (NetRoad.Bridges): the line runs straight over approach, bridge and approach,
/// and NetworkBuilder builds the bridge first and lets the road end on the approaches (narrowed to the 10 m of the bridge).
public sealed partial class RoadNetwork
{
    /// bridge pieces of the mod YAMEE_Bridges and the terrain columns of the end piece; set by the main window and the tests.
    /// null = no bridges (rivers stay obstacles)
    /// (since 2026-11-24 all active kits of the window "Bridge mods", see BridgeKits)
    public static BridgeKits Bridges { get; set; }

    /// YAMEE_Bridges alone (tests and older callers)
    public sealed record BridgeKit(PrefabDef End, PrefabDef Tile, int TerrainColumns)
    {
        public int EndLength => End.SizeX;
        public static implicit operator BridgeKits(BridgeKit k)
        {
            if (k == null) return null;
            var d = BridgeKitDef.Yamee(k.EndLength, k.TerrainColumns);
            d.EndDef = k.End; d.TileDef = k.Tile;
            return new BridgeKits(new[] { d });
        }
    }

    /// widest water a planned bridge may span (m): 4 middle pieces of the 10x10 kit
    internal const int BridgeMaxWater = 54;
    internal const int BridgeMaxTiles = 4;   // the same for the 10x10 kit (tests)

    /// a place where a road may cross the river
    sealed class BridgeSpot
    {
        public BridgeSite Site;
        public Point A, B;             // outer ends of the approaches (low / high side along the span)
        public Point LeadA, LeadB;     // end of the straight piece in front of them
        public int CellA, CellB;
        public float Cost;
        public bool Used;
    }

    readonly List<BridgeSpot> bridgeSpots = new();
    bool[] waterCell;                          // water in the cell (BuildGrid)
    internal const double BridgeLead = 24;              // straight road in front of the approach (m)
    const float BridgePenalty = 120;           // a bridge costs this much more than the same length of road (250 until 2026-11-19: too few bridges)
    const double BridgeSpacing = 400;          // bridges at least this far apart (m; 600 until 2026-11-19)
    internal const double BridgeBank = 12;              // terrain in front of the approach at most this far above/below the approach (m; 8 until 2026-11-19)
    internal const double NetMaxAngle = 35;             // river at most so far off a compass direction (the bridge tool: Bridge.MaxAngle 25°)

    /// test output: how often a bridge place failed at which check
    public static Dictionary<string, int> BridgeRejects { get; } = new();
    /// test output: centers of the bridge places of the last planning
    public static List<Point> LastBridgeSpots { get; private set; } = new();
    static void BRej(string reason) { lock (BridgeRejects) BridgeRejects[reason] = BridgeRejects.GetValueOrDefault(reason) + 1; }

    /// places for bridges: at every water cell near the edge of the water a water point in it is tried (Bridge.Find sees a straight narrow
    /// river there); of places closer than 60 m to each other the straightest and narrowest one stays. Loosened 2026-11-19 (the user: too
    /// few roads crossed rivers): every 16 m instead of 32 m, at a water point instead of the cell middle (the middle often missed the water),
    /// rivers up to NetMaxAngle off a compass direction, bank up to BridgeBank, cheaper and closer bridges
    void FindBridgeSpots()
    {
        bridgeSpots.Clear();
        if (Bridges == null || waterCell == null) return;
        var kits = Bridges;
        var prefabs = w.PrefabSnapshot();
        var candidates = new List<BridgeSite>();
        Parallel.For(2, gw - 2, () => new List<BridgeSite>(), (j, _, local) =>
        {
            for (int i = 2; i < gw - 2; i++)
            {
                int g = j * gw + i;
                if (!waterCell[g]) continue;
                bool dry = false;
                for (int dj = -2; dj <= 2 && !dry; dj++)
                    for (int di = -2; di <= 2 && !dry; di++) if (!waterCell[(j + dj) * gw + i + di]) dry = true;
                if (!dry) continue;   // inside a lake
                // a water point in the cell, the nearest to its middle
                var c = Center(g);
                Point? at = null; double best = double.MaxValue;
                for (int dz = -7; dz <= 7; dz += 2)
                    for (int dx = -7; dx <= 7; dx += 2)
                    {
                        int x = (int)Math.Floor(c.X + dx), z = (int)Math.Floor(c.Y + dz);
                        if (!w.WaterAt(x, z) || dx * dx + dz * dz >= best) continue;
                        best = dx * dx + dz * dz; at = new Point(x + 0.5, z + 0.5);
                    }
                if (at is not Point p) continue;
                // roads of the network are asphalt: kits for asphalt (gravel roads of Old West towns take the same places)
                var site = kits.Find(w, p.X, p.Y, false, NetMaxAngle, prefabs, BridgeMaxWater);
                if (site.TooWide) { BRej("too wide"); continue; }
                if (site.Problem != null)
                {
                    BRej(site.RiverAngle > NetMaxAngle ? $"slant {(int)site.RiverAngle / 5 * 5}°+" : "find: " + (site.Problem.Length > 45 ? site.Problem[..45] : site.Problem));
                    continue;
                }
                // not at a lake (mouth): share of water around the bridge
                var sc = site.Center;
                int wet = 0, all = 0;
                for (double dz = -96; dz <= 96; dz += C)
                    for (double dx = -96; dx <= 96; dx += C)
                    {
                        int q = CellOf(sc.X + dx, sc.Y + dz);
                        if (q < 0) continue;
                        all++;
                        if (waterCell[q]) wet++;
                    }
                if (all == 0 || wet > 0.35 * all) { BRej("lake (water share)"); continue; }
                if (!RiverGoesOn(w, site)) { BRej("pond (no river up- or downstream)"); continue; }
                local.Add(site);
            }
            return local;
        }, local => { if (local.Count > 0) lock (candidates) candidates.AddRange(local); });
        var found = new List<BridgeSpot>();
        foreach (var site in candidates.OrderBy(s => s.RiverAngle + 6 * s.Tiles).ThenBy(s => s.Center.X).ThenBy(s => s.Center.Y))
        {
            if (found.Any(o => (o.Site.Center - site.Center).Length < 60)) { BRej("near another place"); continue; }
            var spot = Spot(site, prefabs);
            if (spot != null) found.Add(spot);
        }
        bridgeSpots.AddRange(found);
        LastBridgeSpots = found.Select(f => f.Site.Center).ToList();
        Log?.Invoke($"bridge spots: {bridgeSpots.Count}");
    }

    /// a river, not a pond: the water goes on 60 m up- and downstream (a slanted river is up to 45 m further to the side there;
    /// Rifane: a bridge over a pond of 40 m). Also used by the wilderness paths.
    internal static bool RiverGoesOn(WorldData w, BridgeSite site)
    {
        bool Goes(int sg)
        {
            double tt = site.MidT + sg * 60, mid = site.S0 + site.Length / 2.0;
            for (double ds = -45; ds <= 45; ds += 3)
            {
                double x = site.AlongX ? mid + ds : tt, z = site.AlongX ? tt : mid + ds;
                int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
                if (w.Contains(ix, iz) && w.WaterMask[w.Index(ix, iz)] == site.Level) return true;
            }
            return false;
        }
        return Goes(1) && Goes(-1);
    }

    /// geometry of the place; null = the straight pieces in front of the approaches do not fit (water, road, building, steep bank)
    BridgeSpot Spot(BridgeSite site, IReadOnlyList<PlacedPrefab> prefabs)
    {
        double t = site.MidT;
        Point At(double s) => site.AlongX ? new Point(s, t) : new Point(t, s);
        var u = site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
        var spot = new BridgeSpot { Site = site, A = At(site.S0 - Bridge.Approach), B = At(site.S0 + site.Length + Bridge.Approach) };
        spot.LeadA = spot.A - u * BridgeLead;
        spot.LeadB = spot.B + u * BridgeLead;
        spot.CellA = CellOf(spot.LeadA.X, spot.LeadA.Y);
        spot.CellB = CellOf(spot.LeadB.X, spot.LeadB.Y);
        if (spot.CellA < 0 || spot.CellB < 0 || edge[spot.CellA] || edge[spot.CellB]) { BRej("map edge"); return null; }
        double height = site.Deck - 1 + site.ApproachLift;
        float extra = 0;
        foreach (var (from, to, sg) in new[] { (spot.A, spot.LeadA, -1), (spot.B, spot.LeadB, 1) })
        {
            // the straight piece: no water, no road, no building (Bridge.Find checked the approach itself)
            for (double d = 0; d <= BridgeLead + 8; d += 2)
                for (double off = -7; off <= 7; off += 3.5)
                {
                    var p = from + u * (sg * d) + new Vector(-u.Y, u.X) * off;
                    int ix = (int)Math.Floor(p.X), iz = (int)Math.Floor(p.Y);
                    if (!w.Contains(ix, iz) || w.WaterAt(ix, iz)) { BRej("lead: water"); return null; }
                    if (w.RoadAt(ix, iz) != 0) { BRej("lead: road"); return null; }
                }
            if (prefabs.Any(pf => pf.Def != null && pf.X - 8 < Math.Max(from.X, to.X) && pf.X + pf.SizeX + 8 > Math.Min(from.X, to.X)
                    && pf.Z - 8 < Math.Max(from.Y, to.Y) && pf.Z + pf.SizeZ + 8 > Math.Min(from.Y, to.Y))) { BRej("lead: building"); return null; }
            // the road has to come down into the river valley or up onto a dam: not too far
            double dh = Math.Abs(Sample(hb, to.X, to.Y) - height);
            if (dh > BridgeBank) { BRej($"bank {(int)Math.Min(dh, 30) / 4 * 4}+ m"); return null; }
            extra += (float)(15 * Math.Max(0, dh - 2));
        }
        spot.Cost = (float)((spot.LeadB - spot.LeadA).Length + BridgePenalty + 20 * site.Tiles + 4 * site.RiverAngle + extra);
        return spot;
    }

    /// River crossings (the user's request 2026-11-19: too few roads crossed rivers): the planner connects the towns by the cheapest way,
    /// so a river was only crossed where that happened to be cheaper than going around it (1–2 bridges per map out of 13–56 places). Here every
    /// free bridge place is tried as a cross connection of its own: from the road network (a junction or free town exit) to the cell in front
    /// of one approach, over the bridge, and from the other approach on to the network. It is kept when the way over the network from one end
    /// to the other is at least CrossingRatio times as long as the new road (otherwise the bridge brings nothing) and the new road is at
    /// most CrossingMaxLength long.
    const double CrossingMaxLength = 4500, CrossingMinSaving = 800;
    const float CrossingSearchCost = 8000;

    int AddRiverCrossings(List<NetTown> asphalt)
    {
        if (bridgeSpots.Count == 0 || plan.Roads.Count == 0) return 0;
        double ratio = spec.Loops switch { 0 => 2.0, 2 => 1.3, _ => 1.5 };
        int made = 0;
        foreach (var b in bridgeSpots.OrderBy(s => s.Cost).ToList())
        {
            if (b.Used) continue;
            // planned roads within reach on both sides (otherwise no search: about 2 × 2 searches per place cost time)
            double reach = CrossingMaxLength / 2;
            if (!plan.Roads.Any(r => r.Samples.Any(s => (s - b.LeadA).Length < reach)) || !plan.Roads.Any(r => r.Samples.Any(s => (s - b.LeadB).Length < reach))) { BRej("crossing: no road within reach"); continue; }
            BuildTaps();
            int q = bridgeSpots.IndexOf(b);
            if (!jumps.TryGetValue(b.CellA, out var ja) || !ja.Any(j => j.K == -2 - q)) continue;   // place no longer free (other bridge near, road over it)
            var u = b.LeadB - b.LeadA;
            // both directions: from the network to side A and on from side B, or the other way round
            foreach (var (near, far, dir) in new[] { (b.CellA, b.CellB, DirOf(u.X, u.Y)), (b.CellB, b.CellA, DirOf(-u.X, -u.Y)) })
            {
                if (TryRiverCrossing(b, near, far, dir, ratio, asphalt)) { made++; break; }
            }
        }
        Log?.Invoke($"river crossings: {made}");
        return made;
    }

    bool TryRiverCrossing(BridgeSpot b, int near, int far, int dir, double ratio, List<NetTown> asphalt)
    {
        // 1. from the network (junctions and free exits of connected towns) to the cell in front of the near approach, driving towards the bridge
        var sources = new List<(int Cell, int Dir, float Cost)>();
        var what = new List<object>();
        foreach (var e in asphalt.Where(t => t.Connected).SelectMany(t => t.Exits).Where(Open))
            foreach (var (d, extra) in ExitTurns(DirOf(e.DX, e.DZ), 1)) { sources.Add((e.Cell, d, extra)); what.Add(e); }
        foreach (var t in taps)
            for (int k = -2; k <= 2; k++) { sources.Add((t.Cell, (t.Dir + k + Dirs) % Dirs, JunctionPenalty + (Math.Abs(k) == 2 ? 60 : 0))); what.Add(t); }
        if (sources.Count == 0) return false;
        allowed = new HashSet<NetExit>(exitList);
        var roads = what.Select(o => (o as Tap)?.Road).ToArray();
        heur = GoalHeuristic(new[] { near });   // A*: one target cell
        int found;
        try { found = Search(sources, s => s / Dirs == near && DirDiff(s % Dirs, dir) <= 1, false, CrossingSearchCost, roads); }
        finally { heur = null; }
        if (found < 0) { BRej("crossing: no way from the network to the river"); return false; }
        if (Curly(found)) { BRej("crossing: curl on the way to the river"); return false; }
        var start = what[source[found]];
        var cells1 = Trace(found);
        if (cells1.Contains(far)) { BRej("crossing: came over the river already"); return false; }   // came over the river already (another bridge or around)
        double len1 = PathLength(found);
        // network node where the new road starts
        int startNode = start is Tap st ? st.Road.Nodes[st.K] : ((NetExit)start).Town.Id;
        Point origin = start is Tap s0 ? s0.P : new Point(((NetExit)start).X, ((NetExit)start).Z);
        var net = GraphDistances(startNode);
        double bridgeLen = (b.LeadB - b.LeadA).Length;

        // 2. from the cell in front of the far approach on to the network
        Search(new List<(int, int, float)> { (far, dir, 0) }, null, false, CrossingSearchCost, start is Tap t0 ? new[] { t0.Road } : null);
        double best = 0, bestRatio = 0; object target = null; int targetState = -1;
        string why = "nothing reached on the other side";
        void Consider(object o, int cell, int arrive, double extra, double dn, Point at)
        {
            if (cell < 0 || dn == double.MaxValue || (at - origin).Length < 300) return;
            var (cost, state) = Arrival(cell, arrive);
            if (cost == float.MaxValue || Curly(state)) return;
            double c = len1 + bridgeLen + PathLength(state) + extra;
            if (c > CrossingMaxLength) { why = "too long"; return; }
            if (cost > 3 * c + 600) { why = "too expensive"; return; }
            if (dn < ratio * c || dn - c < CrossingMinSaving) { why = $"saves too little (best ratio {Math.Max(bestRatio, dn / c):0.0})"; bestRatio = Math.Max(bestRatio, dn / c); return; }
            if (o is Tap tt && CrossedRoads(state).Contains(tt.Road)) return;
            if (ParallelLength(state, c) > 250) return;
            for (int s = state; s >= 0; s = parent[s]) if (s / Dirs == near) return;   // back over the bridge
            double score = dn / c;
            if (score > best) { best = score; target = o; targetState = state; }
        }
        foreach (var e in plan.Towns.Where(t => t.Connected && !t.OldWest).SelectMany(t => t.Exits).Where(Open))
            if (e != start) Consider(e, e.Cell, DirOf(-e.DX, -e.DZ), ApproachLength, net[e.Town.Id], new Point(e.X, e.Z));
        foreach (var t in taps)
            if (!ReferenceEquals(t, start) && (start is not Tap ts || ts.Road != t.Road || Math.Abs(ts.K - t.K) > 20))
                Consider(t, t.Cell, t.Arrive, ApproachLength + JunctionPenalty, net[t.Road.Nodes[t.K]], t.P);
        if (target == null) { BRej("crossing: " + (why.StartsWith("saves") ? "saves too little" : why)); Log?.Invoke($"crossing {b.Site.Center.X:0}/{b.Site.Center.Y:0}: {why}"); return false; }
        var cells2 = Trace(targetState);   // starts with far
        // both ends of the new road (junction or town exit, their height is fixed) must be reachable from the approach height with 90 % of the
        // usual gradient (Kawuno: junction 100 m away and 12 m higher, the road climbed 20 % right behind the bridge); the terrain between may
        // need a cut or a fill (generator rivers lie in valleys 8–13 m deep), only not more than the highest gradient + 8 m
        double height = b.Site.Deck - 1 + b.Site.ApproachLift;
        double EndHeight(object o) => o is NetExit ex ? ex.Tile?.TerrainHeight ?? Sample(hb, ex.X, ex.Z) : Sample(hb, ((Tap)o).P.X, ((Tap)o).P.Y);
        bool Ramp(IEnumerable<int> fromBridge, object end)
        {
            Point prev = Center(fromBridge.First()); double d = BridgeLead;
            foreach (int c in fromBridge)
            {
                d += (Center(c) - prev).Length; prev = Center(c);
                if (Math.Abs(hb[c] - height) > spec.MaxGrade * d + 8) return false;
            }
            return Math.Abs(EndHeight(end) - height) <= 0.9 * spec.Grade * (d + ApproachLength);
        }
        if (!Ramp(Enumerable.Reverse(cells1), start) || !Ramp(cells2, target)) { BRej("crossing: too steep out of the river valley"); return false; }
        var cells = new List<int>(cells1);
        cells.AddRange(cells2);   // cells1 ends with near, the jump near → far is the bridge
        cells.Reverse();                       // AddRoad expects the source (target of the crossing) first
        Log?.Invoke($"river crossing over {b.Site.Center.X:0}/{b.Site.Center.Y:0}: ratio {best:0.0}");
        if (start is NetExit se)
            AddRoad(se, cells, target, false, false, loop: true);
        else
        {
            var tp = (Tap)start;
            var dv = Center(tp.Cell) - tp.P; dv.Normalize();
            var pseudo = new NetExit { Town = null, X = tp.P.X, Z = tp.P.Y, DX = dv.X, DZ = dv.Y, Width = 16, Cell = tp.Cell };
            AddRoad(pseudo, cells, target, false, false, loop: true, startTap: tp);
        }
        return plan.Roads[^1].Bridges.Count > 0;
    }

    /// length of the searched path up to a state (cell centers, jumps included)
    double PathLength(int state)
    {
        double c = 0;
        for (int s = state; s >= 0 && parent[s] >= 0; s = parent[s]) c += (Center(s / Dirs) - Center(parent[s] / Dirs)).Length;
        return c;
    }

    /// enter the free bridge places into the jumps of the search (Road = null, K = −2 − number of the place)
    void AddBridgeJumps()
    {
        for (int q = 0; q < bridgeSpots.Count; q++)
        {
            var b = bridgeSpots[q];
            if (b.Used || !Passable(b.CellA) || !Passable(b.CellB)) continue;
            if (bridgeSpots.Any(o => o.Used && (o.Site.Center - b.Site.Center).Length < BridgeSpacing)) continue;
            var u = b.LeadB - b.LeadA;
            int ab = DirOf(u.X, u.Y), ba = DirOf(-u.X, -u.Y);
            if (!jumps.TryGetValue(b.CellA, out var la)) jumps[b.CellA] = la = new();
            if (!jumps.TryGetValue(b.CellB, out var lb)) jumps[b.CellB] = lb = new();
            la.Add((b.CellB, b.Cost, b.Site.Center, null, -2 - q, ab));
            lb.Add((b.CellA, b.Cost, b.Site.Center, null, -2 - q, ba));
        }
    }

    /// the jump cells[q − 1] → cells[q] is a bridge: the line goes straight over approach, bridge and approach (points in driving
    /// order, all fixed); the center of the previous cell is replaced by the start of the straight piece
    bool AddBridgePoints(List<int> cells, int q, int k, List<Point> pts, List<bool> fixedAt, List<BridgeSite> bridges) =>
        AddBridgePoints(cells[q - 1], k, pts, fixedAt, bridges);

    /// fromCell = cell where the jump starts; bridges = null: editing by hand (nothing is marked as used, see Remeasure)
    bool AddBridgePoints(int fromCell, int k, List<Point> pts, List<bool> fixedAt, List<BridgeSite> bridges)
    {
        if (k > -2 || -2 - k >= bridgeSpots.Count) return false;
        var b = bridgeSpots[-2 - k];
        bool ab = fromCell == b.CellA;
        var seq = ab ? new[] { b.LeadA, b.A, b.B, b.LeadB } : new[] { b.LeadB, b.B, b.A, b.LeadA };
        if (pts.Count > 1 && !fixedAt[^1] && (pts[^1] - seq[0]).Length < 2 * C) { pts.RemoveAt(pts.Count - 1); fixedAt.RemoveAt(fixedAt.Count - 1); }
        foreach (var p in seq) { pts.Add(p); fixedAt.Add(true); }
        if (bridges == null) return true;
        bridges.Add(b.Site);
        b.Used = true;
        nodesUsed.Add(b.Site.Center);
        Log?.Invoke($"bridge at {b.Site.Center.X:0}/{b.Site.Center.Y:0} ({b.Site.Tiles} middle piece(s))");
        return true;
    }

    /// editing by hand: the bridge places that no other road of the plan uses (nor one within BridgeSpacing) become jumps
    void AddEditBridgeJumps(NetworkPlan plan, NetRoad road)
    {
        var taken = plan.Roads.Where(r => r != road).SelectMany(r => r.Bridges).Select(b => b.Center).ToList();
        var keep = bridgeSpots.Select(b => b.Used).ToList();
        for (int q = 0; q < bridgeSpots.Count; q++) bridgeSpots[q].Used = taken.Any(c => (c - bridgeSpots[q].Site.Center).Length < BridgeSpacing);
        // AddBridgeJumps also skips places near used ones: here only the places of other roads count
        for (int q = 0; q < bridgeSpots.Count; q++)
        {
            var b = bridgeSpots[q];
            if (b.Used || !Passable(b.CellA) || !Passable(b.CellB)) continue;
            var u = b.LeadB - b.LeadA;
            if (!jumps.TryGetValue(b.CellA, out var la)) jumps[b.CellA] = la = new();
            if (!jumps.TryGetValue(b.CellB, out var lb)) jumps[b.CellB] = lb = new();
            la.Add((b.CellB, b.Cost, b.Site.Center, null, -2 - q, DirOf(u.X, u.Y)));
            lb.Add((b.CellA, b.Cost, b.Site.Center, null, -2 - q, DirOf(-u.X, -u.Y)));
        }
        for (int q = 0; q < bridgeSpots.Count; q++) bridgeSpots[q].Used = keep[q];
    }

    /// the bridges a road runs over (after a change by hand): places whose approaches and middle lie on its line, from the places found
    /// and the bridges it had before, in order along the road
    List<BridgeSite> BridgesOn(NetRoad r)
    {
        var res = new List<(double Along, BridgeSite Site)>();
        if (r.Points == null || r.Points.Count < 2) return new();
        bool OnLine(BridgeSite site)
        {
            double t = site.MidT;
            Point At(double s) => site.AlongX ? new Point(s, t) : new Point(t, s);
            var pa = At(site.S0 - Bridge.Approach); var pb = At(site.S0 + site.Length + Bridge.Approach);
            return Project(r.Points, site.Center).Off <= 2 && Project(r.Points, pa).Off <= 2 && Project(r.Points, pb).Off <= 2;
        }
        // a bridge swapped by hand (BridgeSwap) keeps its kit when the road was laid again over the place found by the planner
        // (that place has the kit of the random choice and another length): the straight piece is set for the swapped bridge again
        foreach (var own in r.Bridges.Where(s => !OnLine(s)).ToList())
        {
            var spot = bridgeSpots.Select(b => b.Site).FirstOrDefault(s => (s.Center - own.Center).Length < 3 && OnLine(s));
            if (spot == null) continue;
            var line = BridgeSwap.ReplaceOnLine(r.Points, spot, own);
            if (line != null) r.Points = line;
        }
        foreach (var site in r.Bridges.Concat(bridgeSpots.Select(b => b.Site)))
        {
            if (res.Any(x => (x.Site.Center - site.Center).Length < 3)) continue;
            if (!OnLine(site)) continue;
            res.Add((Project(r.Points, site.Center).Along, site));
        }
        return res.OrderBy(x => x.Along).Select(x => x.Site).ToList();
    }

    /// along-road zone of a bridge with its approaches and the straight pieces (+ pad): for measuring the terrain along the road
    internal static bool NearBridge(NetRoad r, Point p, double pad = 0)
    {
        foreach (var b in r.Bridges)
        {
            var c = b.Center;
            double along = b.AlongX ? Math.Abs(p.X - c.X) : Math.Abs(p.Y - c.Y), across = b.AlongX ? Math.Abs(p.Y - c.Y) : Math.Abs(p.X - c.X);
            if (along <= b.Length / 2.0 + Bridge.Approach + BridgeLead + pad && across <= b.Width / 2.0 + 7 + pad) return true;
        }
        return false;
    }
}
