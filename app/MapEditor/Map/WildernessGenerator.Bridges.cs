using System.Windows;

namespace MapEditor.Map;

/// Gravel paths of the wilderness POIs over rivers (the user's request 2026-11-19): like "Generate roads", places for a bridge are looked
/// for along the rivers (Bridge.Find and the same checks: straight narrow piece of river, not at a lake, not a pond). For the path search
/// such a place is a jump from the cell in front of one approach straight to the cell in front of the other one. A path that uses it
/// gets the bridge (WildPath.Bridges); Build puts the bridge first and lets the path run level over approach, bridge and approach.
public sealed partial class WildernessGenerator
{
    sealed class WildBridge
    {
        public BridgeSite Site;
        public Point A, B, LeadA, LeadB;   // outer ends of the approaches, end of the straight piece in front of them
        public int CellA, CellB, Dir;      // cells in front of the approaches, driving direction A → B (0–15)
        public double Height;              // height of the approach road
        public float Cost;
        public bool Used;
    }

    readonly List<WildBridge> bridges = new();
    readonly Dictionary<int, List<int>> bridgeAt = new();

    const float BridgePenalty = 100;     // a bridge costs this much more than the same length of path (m)
    const double BridgeSpacing = 300;    // bridges at least this far apart (m; existing ones on the map included)
    /// a narrow gravel path widens to the whole approach of the bridge (its width) over BridgeTaper
    const double BridgeTaper = 32;

    /// test output: how often a bridge place failed at which check
    public static Dictionary<string, int> BridgeRejects { get; } = new();
    static void Rej(string reason) { lock (BridgeRejects) BridgeRejects[reason] = BridgeRejects.GetValueOrDefault(reason) + 1; }

    /// bridges planned so far (all paths)
    public int BridgeCount => plan.Paths.Sum(p => p.Bridges.Count);

    /// share of water around a point (every 16 m within ±96 m)
    double WaterShare(Point c)
    {
        int wet = 0, all = 0;
        for (double dz = -96; dz <= 96; dz += 16)
            for (double dx = -96; dx <= 96; dx += 16)
            {
                int q = CellOf(c.X + dx, c.Y + dz);
                if (q < 0) continue;
                all++;
                if (water[q]) wet++;
            }
        return all == 0 ? 1 : wet / (double)all;
    }

    /// bridge places along the rivers (every 16 m at the edge of the water); of places closer than 60 m the straightest and narrowest stays
    void FindBridges()
    {
        bridges.Clear(); bridgeAt.Clear();
        var kit = RoadNetwork.Bridges;
        if (kit == null) return;
        var existing = w.PrefabSnapshot().Where(p => BridgeKits.IsBridgePiece(p.Name))
            .Select(p => new Point(p.X + p.SizeX / 2.0, p.Z + p.SizeZ / 2.0)).ToList();
        var candidates = new List<BridgeSite>();
        for (int cz = 2; cz < gw - 2; cz += 2)
            for (int cx = 2; cx < gw - 2; cx += 2)
            {
                int g = cz * gw + cx;
                if (!water[g]) continue;
                bool dry = false;
                for (int dj = -2; dj <= 2 && !dry; dj++)
                    for (int di = -2; di <= 2 && !dry; di++) if (!water[g + dj * gw + di]) dry = true;
                if (!dry) continue;                          // inside a lake
                var c = Center(g);
                if (WaterShare(c) > 0.5) continue;           // shore of a lake (quick test)
                Point? at = null; double best = double.MaxValue;
                for (int dz = -3; dz <= 3; dz++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int x = (int)Math.Floor(c.X + dx), z = (int)Math.Floor(c.Y + dz);
                        if (!w.WaterAt(x, z) || dx * dx + dz * dz >= best) continue;
                        best = dx * dx + dz * dz; at = new Point(x + 0.5, z + 0.5);
                    }
                if (at is not Point p) continue;
                var site = kit.Find(w, p.X, p.Y, true, RoadNetwork.NetMaxAngle, null, RoadNetwork.BridgeMaxWater);   // wilderness paths are gravel
                if (site.TooWide) { Rej("too wide"); continue; }
                if (site.Problem != null) { Rej(site.RiverAngle > RoadNetwork.NetMaxAngle ? "slant" : "find"); continue; }
                if (WaterShare(site.Center) > 0.35) { Rej("lake (water share)"); continue; }
                if (!RoadNetwork.RiverGoesOn(w, site)) { Rej("pond"); continue; }
                if (existing.Any(q => (q - site.Center).Length < BridgeSpacing)) { Rej("bridge near"); continue; }
                candidates.Add(site);
            }
        foreach (var site in candidates.OrderBy(s => s.RiverAngle + 6 * s.Tiles).ThenBy(s => s.Center.X).ThenBy(s => s.Center.Y))
        {
            if (bridges.Any(o => (o.Site.Center - site.Center).Length < 60)) continue;
            var b = Spot(site);
            if (b == null) continue;
            int q = bridges.Count;
            bridges.Add(b);
            foreach (int cell in new[] { b.CellA, b.CellB })
            {
                if (!bridgeAt.TryGetValue(cell, out var l)) bridgeAt[cell] = l = new();
                l.Add(q);
            }
        }
        Log?.Invoke($"wilderness bridge places: {bridges.Count}");
    }

    /// geometry of the place; null = the straight pieces in front of the approaches do not fit (water, road, building, steep bank, edge)
    WildBridge Spot(BridgeSite site)
    {
        var (a, b, height) = NetworkBuilder.ApproachEnds(site);
        var u = site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
        var s = new WildBridge { Site = site, A = a, B = b, LeadA = a - u * RoadNetwork.BridgeLead, LeadB = b + u * RoadNetwork.BridgeLead, Height = height };
        s.CellA = CellOf(s.LeadA.X, s.LeadA.Y);
        s.CellB = CellOf(s.LeadB.X, s.LeadB.Y);
        if (s.CellA < 0 || s.CellB < 0 || block[s.CellA] != 0 || block[s.CellB] != 0 || water[s.CellA] || water[s.CellB]) { Rej("lead cell blocked"); return null; }
        double bridgeEdge = Math.Max(PathEdge, site.RoadWidth / 2.0 + spec.Verge);
        if (TooCloseToTile(s.A.X, s.A.Y, s.Height, bridgeEdge) || TooCloseToTile(s.B.X, s.B.Y, s.Height, bridgeEdge)
            || TooCloseToTile(s.LeadA.X, s.LeadA.Y, s.Height, bridgeEdge) || TooCloseToTile(s.LeadB.X, s.LeadB.Y, s.Height, bridgeEdge))
        { Rej("lead: tile"); return null; }
        s.Dir = site.AlongX ? 0 : 4;
        float extra = 0;
        foreach (var (from, to, sg) in new[] { (s.A, s.LeadA, -1), (s.B, s.LeadB, 1) })
        {
            for (double d = 0; d <= RoadNetwork.BridgeLead + 8; d += 2)
                for (double off = -7; off <= 7; off += 3.5)
                {
                    var p = from + u * (sg * d) + new Vector(-u.Y, u.X) * off;
                    int ix = (int)Math.Floor(p.X), iz = (int)Math.Floor(p.Y);
                    if (!w.Contains(ix, iz) || w.WaterAt(ix, iz)) { Rej("lead: water"); return null; }
                    if (w.RoadAt(ix, iz) != 0) { Rej("lead: road"); return null; }
                }
            if (AnyRect(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y), 8, g => building[g] || tile[g])) { Rej("lead: building"); return null; }
            double dh = Math.Abs(w.HeightAt((int)Math.Floor(to.X), (int)Math.Floor(to.Y)) - height);
            if (dh > RoadNetwork.BridgeBank) { Rej("bank"); return null; }
            extra += (float)(15 * Math.Max(0, dh - 2));
        }
        s.Cost = (float)((s.LeadB - s.LeadA).Length + BridgePenalty + 20 * site.Tiles + 4 * site.RiverAngle + extra);
        return s;
    }

    /// the straight pieces still free (a building placed after the search of the places, or a planned path over them)
    bool LeadsFree(WildBridge b)
    {
        foreach (var (from, to) in new[] { (b.A, b.LeadA), (b.B, b.LeadB) })
            for (double t = 0; t <= 1.001; t += 0.25)
            {
                var p = from + (to - from) * t;
                int g = CellOf(p.X, p.Y);
                if (g < 0 || building[g] || (block[g] & 1) != 0 || (t < 0.9 && target[g])) return false;
            }
        return true;
    }

    /// the jump g1 → g2 of the search is a bridge (and in which direction)
    (WildBridge B, bool FromA) BridgeBetween(int g1, int g2)
    {
        if (!bridgeAt.TryGetValue(g1, out var l)) return (null, false);
        foreach (int q in l)
        {
            var b = bridges[q];
            if (b.CellA == g1 && b.CellB == g2) return (b, true);
            if (b.CellB == g1 && b.CellA == g2) return (b, false);
        }
        return (null, false);
    }

    /// points of the path that lie on a bridge piece (lead, approach, approach, lead)
    static HashSet<Point> BridgePoints(WildPath path)
    {
        var set = new HashSet<Point>();
        foreach (var site in path.Bridges)
        {
            var (a, b, _) = NetworkBuilder.ApproachEnds(site);
            var u = site.AlongX ? new Vector(1, 0) : new Vector(0, 1);
            set.Add(a); set.Add(b); set.Add(a - u * RoadNetwork.BridgeLead); set.Add(b + u * RoadNetwork.BridgeLead);
        }
        return set;
    }

    /// after a path was accepted: its bridges and all places nearer than BridgeSpacing are taken; the bridge with its approaches and straight
    /// pieces is blocked for buildings and other paths
    void UseBridges(WildPath path)
    {
        foreach (var site in path.Bridges)
        {
            foreach (var b in bridges)
                if ((b.Site.Center - site.Center).Length < BridgeSpacing) b.Used = true;
            var (a, bb, _) = NetworkBuilder.ApproachEnds(site);
            double x0 = Math.Min(a.X, bb.X), z0 = Math.Min(a.Y, bb.Y);
            MarkRect(x0, z0, Math.Abs(bb.X - a.X), Math.Abs(bb.Y - a.Y), 8, g => { building[g] = true; block[g] |= 1; });
        }
    }

    const double MinBridgeLegLength = 25.0;       // room for curve & 32 m gravel taper behind the bridge lead
    const double MinBridgeEndSeparation = 250.0;  // avoid looping both ends back to the same road spot

    /// Second-pass river crossing (the user's request 2026-11-23): if no wilderness path crossed a river yet
    /// (common whenever roads already run on both sides of a river), try to build at least one gravel connecting
    /// path across a free, rule-compliant river bridge spot linking two existing roads or wilderness paths.
    void EnsureWildernessBridge(IProgress<string> progress)
    {
        if (BridgeCount > 0 || bridges.Count == 0 || RoadNetwork.Bridges == null) return;
        // Refresh distance field so newly added 2nd-pass connector paths are included as reachable targets
        dist = GroundShaper.Distance(target, gw, gw);
        double maxLeg = Math.Clamp(Math.Max(spec.MaxPath, 1200.0), 800.0, 2000.0);
        var cands = bridges
            .Where(b => !b.Used && LeadsFree(b) && dist[b.CellA] * C <= maxLeg && dist[b.CellB] * C <= maxLeg)
            .OrderBy(b => (dist[b.CellA] + dist[b.CellB]) * C + b.Cost)
            .ToList();
        if (cands.Count == 0) return;

        for (int idx = 0; idx < cands.Count; idx++)
        {
            var b = cands[idx];
            progress.Report(Loc.T("Looking for a river crossing for wilderness paths …"), idx / (double)cands.Count);
            if (b.Used || !LeadsFree(b)) continue;

            var u = b.LeadB - b.LeadA;
            if (u.Length < 1e-3) continue;
            u.Normalize();

            var bridgeCore = CellsAroundSegment(b.A, b.B, 10.0);
            var blockA = new HashSet<int>(bridgeCore);
            AddSegmentCells(blockA, b.B, b.LeadB, 10.0);

            bool TargetOk(int g, WildPath avoidPath, Point? avoidPoint)
            {
                var c = Center(g);
                if (avoidPoint is Point ap && (c - ap).LengthSquared < MinBridgeEndSeparation * MinBridgeEndSeparation)
                    return false;
                if (pathOf[g] > 0)
                {
                    int pi = pathOf[g] - 1;
                    if (pi < 0 || pi >= plan.Paths.Count) return false;
                    var tp = plan.Paths[pi];
                    if (tp.IsVolcano || (avoidPath != null && tp == avoidPath)) return false;
                    if (NearBridge(tp, c, 22.0)) return false;
                    if (tp.Poi != null && (c - tp.Points[0]).Length < 18.0) return false;
                }
                return true;
            }

            var legA = FindPath(b.LeadA, -u, b.Height, maxLeg, forcedVolcano: false,
                targetFilter: g => TargetOk(g, null, null),
                extraBlock: g => blockA.Contains(g),
                preferSpline: true, startSlackOverride: RoadNetwork.BridgeBank, allowBridges: false);
            if (legA == null || legA.Points == null || legA.Points.Count < 2 || legA.Length < MinBridgeLegLength) continue;
            var endA = legA.Points[^1];
            if (legA.Target != null && (NearBridge(legA.Target, endA, 22.0) || (legA.Target.Poi != null && (endA - legA.Target.Points[0]).Length < 18.0)))
                continue;

            var blockB = new HashSet<int>(bridgeCore);
            AddSegmentCells(blockB, b.A, b.LeadA, 10.0);
            AddPolylineCells(blockB, legA.Points, 16.0);

            var legB = FindPath(b.LeadB, u, b.Height, maxLeg, forcedVolcano: false,
                targetFilter: g => TargetOk(g, legA.Target, endA),
                extraBlock: g => blockB.Contains(g),
                preferSpline: true, startSlackOverride: RoadNetwork.BridgeBank, allowBridges: false);
            if (legB == null || legB.Points == null || legB.Points.Count < 2 || legB.Length < MinBridgeLegLength) continue;
            var endB = legB.Points[^1];
            if ((endB - endA).Length < MinBridgeEndSeparation) continue;
            if (legB.Target != null && (NearBridge(legB.Target, endB, 22.0) || (legB.Target.Poi != null && (endB - legB.Target.Points[0]).Length < 18.0)))
                continue;

            // Combine into one continuous path: endA → ... → LeadA → A → B → LeadB → ... → endB
            var pts = new List<Point>(legA.Points.Count + 4 + legB.Points.Count);
            for (int i = legA.Points.Count - 1; i >= 0; i--) pts.Add(legA.Points[i]);
            if ((pts[^1] - b.LeadA).Length > 0.5) pts.Add(b.LeadA);
            pts.Add(b.A);
            pts.Add(b.B);
            pts.Add(b.LeadB);
            int bStart = (legB.Points[0] - b.LeadB).Length < 0.5 ? 1 : 0;
            for (int i = bStart; i < legB.Points.Count; i++) pts.Add(legB.Points[i]);

            double length = 0;
            for (int i = 1; i < pts.Count; i++) length += (pts[i] - pts[i - 1]).Length;
            if (RoadNetwork.HasSelfLoop(NetworkBuilder.Even(pts, 10.0))) continue;
            if (!BridgePathClearOfExisting(pts, length, legA.Target, legB.Target)) continue;

            var path = new WildPath
            {
                Points = pts,
                Length = length,
                Width = Math.Clamp(spec.PathWidth, spec.PathWidthMin, 5.0),
                IsConnector = true,
                StartTarget = legA.Target,
                StartKind = legA.Target == null ? legA.EndKind : (byte)0,
                StartDir = legA.Target == null ? legA.EndDir : default,
                StartHalf = legA.Target == null ? legA.EndHalf : 0,
                Target = legB.Target,
                EndKind = legB.Target == null ? legB.EndKind : (byte)0,
                EndDir = legB.Target == null ? legB.EndDir : default,
                EndHalf = legB.Target == null ? legB.EndHalf : 0,
            };
            path.Bridges.Add(b.Site);
            plan.Paths.Add(path);
            RegisterPathCells(path, plan.Paths.Count);
            UseBridges(path);
            Log?.Invoke($"wilderness bridge link: {b.Site.Center.X:0},{b.Site.Center.Y:0} ({b.Site.Tiles} tiles, {length:0} m)");
            progress.Report(Loc.T("Looking for a river crossing for wilderness paths …"), 1.0);
            return;
        }
    }

    HashSet<int> CellsAroundSegment(Point a, Point b, double radius)
    {
        var cells = new HashSet<int>();
        AddSegmentCells(cells, a, b, radius);
        return cells;
    }

    void AddSegmentCells(HashSet<int> cells, Point a, Point b, double radius)
    {
        double len = (b - a).Length;
        int steps = Math.Max(1, (int)Math.Ceiling(len / 4.0));
        int radCells = (int)Math.Ceiling(radius / C);
        double r2 = radius * radius;
        for (int k = 0; k <= steps; k++)
        {
            var q = a + (b - a) * (k / (double)steps);
            int cg = CellOf(q.X, q.Y);
            if (cg < 0) continue;
            int cx = cg % gw, cz = cg / gw;
            for (int dz = -radCells; dz <= radCells; dz++)
                for (int dx = -radCells; dx <= radCells; dx++)
                {
                    int nx = cx + dx, nz = cz + dz;
                    if (nx < 0 || nz < 0 || nx >= gw || nz >= gw) continue;
                    int ng = nz * gw + nx;
                    if ((Center(ng) - q).LengthSquared <= r2) cells.Add(ng);
                }
        }
    }

    void AddPolylineCells(HashSet<int> cells, List<Point> pts, double radius)
    {
        for (int i = 1; i < pts.Count; i++) AddSegmentCells(cells, pts[i - 1], pts[i], radius);
    }

    bool BridgePathClearOfExisting(List<Point> pts, double totalLength, WildPath startTarget, WildPath endTarget)
    {
        double minClr2 = MinConnectorClearance * MinConnectorClearance;
        double pMinX = double.MaxValue, pMaxX = double.MinValue, pMinZ = double.MaxValue, pMaxZ = double.MinValue;
        for (int i = 0; i < pts.Count; i++)
        {
            var q = pts[i];
            if (q.X < pMinX) pMinX = q.X; if (q.X > pMaxX) pMaxX = q.X;
            if (q.Y < pMinZ) pMinZ = q.Y; if (q.Y > pMaxZ) pMaxZ = q.Y;
        }
        var nearPaths = new List<WildPath>();
        foreach (var other in plan.Paths)
        {
            if (other.Points == null || other.Points.Count < 2) continue;
            double oMinX = double.MaxValue, oMaxX = double.MinValue, oMinZ = double.MaxValue, oMaxZ = double.MinValue;
            for (int k = 0; k < other.Points.Count; k++)
            {
                var q = other.Points[k];
                if (q.X < oMinX) oMinX = q.X; if (q.X > oMaxX) oMaxX = q.X;
                if (q.Y < oMinZ) oMinZ = q.Y; if (q.Y > oMaxZ) oMaxZ = q.Y;
            }
            if (oMaxX + MinConnectorClearance < pMinX || oMinX - MinConnectorClearance > pMaxX ||
                oMaxZ + MinConnectorClearance < pMinZ || oMinZ - MinConnectorClearance > pMaxZ) continue;
            nearPaths.Add(other);
        }
        double walked = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i];
            double segLen = (b - a).Length;
            bool inStartZone = walked < 24.0;
            bool inEndZone = totalLength - (walked + segLen) < 24.0;
            foreach (var other in nearPaths)
            {
                bool isStart = other == startTarget;
                bool isEnd = other == endTarget;
                if ((isStart && inStartZone) || (isEnd && inEndZone)) continue;
                for (int j = 1; j < other.Points.Count; j++)
                {
                    var c = other.Points[j - 1]; var d = other.Points[j];
                    if (SegmentsIntersect(a, b, c, d)) return false;
                    if (SegmentDistanceSquared(a, b, c, d) < minClr2) return false;
                }
            }
            walked += segLen;
        }
        return true;
    }
}
