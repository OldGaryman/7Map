using System.Windows;

namespace MapEditor.Map;

/// Second pass for "Create wilderness POIs": automatically connects qualifying wilderness paths
/// (at their outer dead-end AND in their middle section) to nearby existing roads or other independent
/// wilderness paths – ignoring spec.MaxPath so through-paths and mid cross-links can close dead ends.
/// Single POIs with only a short stub path (< ShortSinglePathLimit) are intentionally left untouched.
/// Strictly enforces all YAMEE road rules: FindPath (±45° start spread, perpendicular 90° T-departure,
/// ArrivalOk ≥ 45°, Feasible, RoadLine.Fit / SmoothPath, RoadNetwork.HasSelfLoop) + two-ended junction
/// height/cross-slope/ProtectZones/FeatherJunction/SmoothSteps in PlanPath & Build.
public sealed partial class WildernessGenerator
{
    const double ShortSinglePathLimit = 250.0;   // single-POI path shorter than this stays a simple driveway stub
    const double ConnectorMaxStraight = 500.0;   // "nearby" reach (m), independent of spec.MaxPath
    const double ConnectorMaxPath = 600.0;       // max routed length (m) for 2nd-pass connectors (allows curves around hills)
    const double ConnectorMinLength = 35.0;      // minimum length so both T-junctions stay cleanly separated
    const double MinRootSeparation = 200.0;      // min distance to own component's road anchor (avoids tiny triangles)
    const double MinConnectorSpacing = 100.0;    // min spacing between newly added connector junctions
    const double MinMidEndMargin = 70.0;         // min distance from path ends for a mid-path cross-link
    const double MinConnectorClearance = 16.0;   // min clearance from existing paths outside 24 m junction zones

    void ConnectNearbyRoads(IProgress<string> progress)
    {
        var basePaths = plan.Paths.Where(p => !p.IsVolcano && p.Points != null && p.Points.Count >= 2).ToList();
        if (basePaths.Count == 0) { progress.Report(Loc.T("Connecting wilderness paths to nearby roads …"), 1.0); return; }
        progress.Report(Loc.T("Connecting wilderness paths to nearby roads …"), 0.0);

        // Component tracking (Union-Find over planned paths) + existing road anchors per component
        var parentComp = new Dictionary<WildPath, WildPath>();
        var compAnchors = new Dictionary<WildPath, List<Point>>();
        var compMembers = new Dictionary<WildPath, List<WildPath>>();
        WildPath FindComp(WildPath p)
        {
            while (parentComp[p] != p) { parentComp[p] = parentComp[parentComp[p]]; p = parentComp[p]; }
            return p;
        }
        void UnionComp(WildPath a, WildPath b)
        {
            var ra = FindComp(a); var rb = FindComp(b);
            if (ra == rb) return;
            parentComp[rb] = ra;
            compAnchors[ra].AddRange(compAnchors[rb]);
            compMembers[ra].AddRange(compMembers[rb]);
            compAnchors.Remove(rb);
            compMembers.Remove(rb);
        }

        foreach (var p in basePaths)
        {
            parentComp[p] = p;
            compAnchors[p] = p.Target == null ? new List<Point> { p.Points[^1] } : new List<Point>();
            compMembers[p] = new List<WildPath> { p };
        }
        var children = basePaths.ToDictionary(p => p, _ => new List<WildPath>());
        foreach (var p in basePaths)
            if (p.Target != null && parentComp.ContainsKey(p.Target))
            {
                children[p.Target].Add(p);
                UnionComp(p.Target, p);
            }

        // Determine qualifying components UP FRONT:
        // Skip single-POI trees with only a short path (< ShortSinglePathLimit).
        var qualifiedPaths = new HashSet<WildPath>();
        foreach (var (root, members) in compMembers)
        {
            double totalLen = members.Sum(m => m.Length);
            if (members.Count == 1 && totalLen < ShortSinglePathLimit) continue;
            foreach (var m in members) qualifiedPaths.Add(m);
        }
        if (qualifiedPaths.Count == 0) return;

        // Precompute distance from root road (outermost leaves first) and child junction arc-lengths per path
        var depthFromRoad = new Dictionary<WildPath, double>();
        double DepthOf(WildPath p)
        {
            if (depthFromRoad.TryGetValue(p, out double d)) return d;
            d = p.Length;
            if (p.Target != null && parentComp.ContainsKey(p.Target))
                d += ArcLengthToNearest(p.Target.Points, p.Points[^1], fromEnd: true) is double rem ? rem : 0;
            // Add full parent chain length to road for clean priority ordering
            var cur = p.Target;
            while (cur != null && parentComp.ContainsKey(cur))
            {
                d += cur.Length * 0.5;
                cur = cur.Target;
            }
            return depthFromRoad[p] = d;
        }

        var childDists = new Dictionary<WildPath, List<double>>();
        foreach (var p in basePaths)
        {
            var list = new List<double>();
            foreach (var ch in children[p])
                list.Add(ArcLengthToNearest(p.Points, ch.Points[^1], fromEnd: false));
            childDists[p] = list;
        }

        var usedJunctions = new List<Point>();
        var pathBounds = new Dictionary<WildPath, (double MinX, double MaxX, double MinZ, double MaxZ)>();
        static (double MinX, double MaxX, double MinZ, double MaxZ) BoundsOf(List<Point> pts)
        {
            double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                var q = pts[i];
                if (q.X < x0) x0 = q.X; if (q.X > x1) x1 = q.X;
                if (q.Y < z0) z0 = q.Y; if (q.Y > z1) z1 = q.Y;
            }
            return (x0, x1, z0, z1);
        }
        foreach (var p in plan.Paths)
            if (p.Points != null && p.Points.Count >= 2) pathBounds[p] = BoundsOf(p.Points);

        bool TryConnectAt(WildPath startPath, double arcFromStart)
        {
            if (!SampleAlong(startPath.Points, arcFromStart, out Point branchPt, out Vector tangent)) return false;
            if (NearBridge(startPath, branchPt, 24.0)) return false;
            if (usedJunctions.Any(u => (u - branchPt).LengthSquared < MinConnectorSpacing * MinConnectorSpacing)) return false;

            var compA = FindComp(startPath);
            var anchorsA = compAnchors[compA];
            // Never launch right next to our own existing road junction
            if (anchorsA.Any(a => (branchPt - a).LengthSquared < 80.0 * 80.0)) return false;

            var membersA = compMembers[compA];
            // Temporarily keep clearance (1 cell = 8 m) around own component's paths outside the 16 m launch circle
            var compClearance = BuildComponentClearance(membersA, branchPt, 16.0);

            bool TargetFilter(int g)
            {
                if (pathOf[g] > 0)
                {
                    int idx = pathOf[g] - 1;
                    if (idx < 0 || idx >= plan.Paths.Count) return false;
                    var tp = plan.Paths[idx];
                    return !tp.IsVolcano && parentComp.ContainsKey(tp) && FindComp(tp) != compA;
                }
                // Existing road cell from RoadMask: must lie cleanly away from our own road anchor(s)
                var c = Center(g);
                for (int i = 0; i < anchorsA.Count; i++)
                    if ((c - anchorsA[i]).LengthSquared < MinRootSeparation * MinRootSeparation) return false;
                return true;
            }

            double startH = w.PreDamageHeightAt(branchPt.X, branchPt.Y, spec.BreakInWasteland);
            var n1 = new Vector(-tangent.Y, tangent.X);
            var n2 = -n1;

            WildPath best = null;
            foreach (var dir in new[] { n1, n2 })
            {
                var cand = FindPath(branchPt, dir, startH, ConnectorMaxPath, forcedVolcano: false,
                    targetFilter: TargetFilter, extraBlock: g => compClearance.Contains(g), preferSpline: true);
                if (cand == null) continue;
                if (!ConnectorGeometryOk(cand, startPath, branchPt, compA, anchorsA, FindComp, parentComp, pathBounds)) continue;
                if (best == null || cand.Length < best.Length) best = cand;
            }
            if (best == null) return false;

            best.StartTarget = startPath;
            best.IsConnector = true;
            best.Width = startPath.Width;
            plan.Paths.Add(best);
            pathBounds[best] = BoundsOf(best.Points);
            RegisterPathCells(best, plan.Paths.Count);
            UseBridges(best);

            parentComp[best] = compA;
            compMembers[compA].Add(best);
            if (best.Target != null && parentComp.ContainsKey(best.Target))
                UnionComp(compA, best.Target);
            else if (best.Target == null)
                compAnchors[FindComp(compA)].Add(best.Points[^1]);

            usedJunctions.Add(branchPt);
            usedJunctions.Add(best.Points[^1]);
            return true;
        }

        // Sub-pass A ("am Ende"): close dead-end tips of qualifying leaf paths (outermost first)
        var leaves = qualifiedPaths.Where(p => children[p].Count == 0 && p.Length >= 50.0)
            .OrderByDescending(DepthOf).ToList();
        // Sub-pass B ("im mittleren Bereich"): add up to 1 mid-path cross-link per qualifying path
        var midCandidates = qualifiedPaths
            .Where(p => p.Length >= 220.0 || (children[p].Count > 0 && p.Length >= 110.0))
            .OrderByDescending(p => p.Length).ToList();

        int totalSteps = leaves.Count + midCandidates.Count;
        // Mid-path candidates test up to 3 positions (0.50/0.35/0.65), leaves test 1–2 positions.
        double totalWeight = Math.Max(1.0, leaves.Count * 1.1 + midCandidates.Count * 1.8);
        double doneWeight = 0.0;
        int doneSteps = 0;
        int stepMod = Math.Max(1, Math.Min(3, totalSteps / 40));
        void ReportStep()
        {
            if (totalSteps == 0) return;
            progress.Report(Loc.F("Connecting wilderness paths ({0} of {1}) …", Math.Min(doneSteps + 1, totalSteps), totalSteps),
                Math.Clamp(doneWeight / totalWeight, 0.0, 1.0));
        }

        foreach (var leaf in leaves)
        {
            if (doneSteps == 0 || doneSteps % stepMod == 0) ReportStep();
            else WorkCancel.ThrowIfRequested();
            double s1 = Math.Clamp(24.0, 18.0, leaf.Length * 0.40);
            if (leaf.Length - s1 >= 25.0 && !TryConnectAt(leaf, s1))
            {
                // Fallback slightly further back from the leaf POI if building/terrain blocked s1
                double s2 = Math.Min(38.0, leaf.Length * 0.48);
                if (s2 > s1 + 6.0 && leaf.Length - s2 >= 25.0)
                    TryConnectAt(leaf, s2);
            }
            doneSteps++;
            doneWeight += 1.1;
        }

        foreach (var p in midCandidates)
        {
            if (doneSteps == 0 || doneSteps % stepMod == 0) ReportStep();
            else WorkCancel.ThrowIfRequested();
            bool longPath = p.Length >= 220.0;
            double margin = longPath ? MinMidEndMargin : 40.0;
            double[] fracs = new[] { 0.50, 0.35, 0.65 };
            var chD = childDists[p];
            foreach (double frac in fracs)
            {
                double s = p.Length * frac;
                if (s < margin || p.Length - s < margin) continue;
                if (chD.Any(cd => Math.Abs(cd - s) < 40.0)) continue;
                if (TryConnectAt(p, s)) break;   // at most 1 mid-link per path
            }
            doneSteps++;
            doneWeight += 1.8;
        }
        progress.Report(Loc.T("Connecting wilderness paths to nearby roads …"), 1.0);
    }

    HashSet<int> BuildComponentClearance(List<WildPath> members, Point branchPt, double launchClearRadius)
    {
        var cells = new HashSet<int>();
        double r2 = launchClearRadius * launchClearRadius;
        foreach (var mp in members)
        {
            if (mp.Points == null) continue;
            for (int i = 1; i < mp.Points.Count; i++)
            {
                var a = mp.Points[i - 1]; var b = mp.Points[i];
                double len = (b - a).Length;
                int steps = Math.Max(1, (int)Math.Ceiling(len / 4.0));
                for (int k = 0; k <= steps; k++)
                {
                    var q = a + (b - a) * (k / (double)steps);
                    if ((q - branchPt).LengthSquared < r2) continue;
                    int cg = CellOf(q.X, q.Y);
                    if (cg < 0) continue;
                    int cx = cg % gw, cz = cg / gw;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, nz = cz + dz;
                            if (nx < 0 || nz < 0 || nx >= gw || nz >= gw) continue;
                            var cc = Center(nz * gw + nx);
                            if ((cc - branchPt).LengthSquared < r2) continue;
                            cells.Add(nz * gw + nx);
                        }
                }
            }
        }
        return cells;
    }

    bool ConnectorGeometryOk(WildPath cand, WildPath startPath, Point branchPt,
        WildPath compA, List<Point> anchorsA, Func<WildPath, WildPath> findComp, Dictionary<WildPath, WildPath> parentComp,
        Dictionary<WildPath, (double MinX, double MaxX, double MinZ, double MaxZ)> pathBounds)
    {
        if (cand.Points == null || cand.Points.Count < 2) return false;
        if (cand.Length < ConnectorMinLength || cand.Length > ConnectorMaxPath) return false;
        var end = cand.Points[^1];
        if ((end - branchPt).Length > ConnectorMaxStraight) return false;

        if (cand.Target == null)
        {
            foreach (var a in anchorsA)
                if ((end - a).LengthSquared < MinRootSeparation * MinRootSeparation) return false;
        }
        else
        {
            if (cand.Target.IsVolcano || !parentComp.ContainsKey(cand.Target) || findComp(cand.Target) == compA)
                return false;
            if (NearBridge(cand.Target, end, 22.0)) return false;
            // Do not join directly onto another POI's driveway entrance
            if (cand.Target.Poi != null && (end - cand.Target.Points[0]).Length < 18.0) return false;
        }

        // Self-loop check on the connector itself (< 24 m / self-crossing)
        var denseCand = NetworkBuilder.Even(cand.Points, 10.0);
        if (RoadNetwork.HasSelfLoop(denseCand)) return false;

        // Pre-filter other paths by exact AABB expanded by MinConnectorClearance
        double cMinX = double.MaxValue, cMaxX = double.MinValue, cMinZ = double.MaxValue, cMaxZ = double.MinValue;
        for (int i = 0; i < cand.Points.Count; i++)
        {
            var q = cand.Points[i];
            if (q.X < cMinX) cMinX = q.X; if (q.X > cMaxX) cMaxX = q.X;
            if (q.Y < cMinZ) cMinZ = q.Y; if (q.Y > cMaxZ) cMaxZ = q.Y;
        }
        var nearPaths = new List<WildPath>();
        foreach (var other in plan.Paths)
        {
            if (other.Points == null || other.Points.Count < 2) continue;
            if (pathBounds.TryGetValue(other, out var ob) &&
                (ob.MaxX + MinConnectorClearance < cMinX || ob.MinX - MinConnectorClearance > cMaxX ||
                 ob.MaxZ + MinConnectorClearance < cMinZ || ob.MinZ - MinConnectorClearance > cMaxZ))
                continue;
            nearPaths.Add(other);
        }

        // Check no unintended crossings or < MinConnectorClearance parallel grazing with any already-planned path
        // (including both arms of startPath beyond the 24 m T-junction zone, preventing hairpins & overlapping roadbeds)
        double minClr2 = MinConnectorClearance * MinConnectorClearance;
        double walked = 0;
        for (int i = 1; i < cand.Points.Count; i++)
        {
            var a = cand.Points[i - 1]; var b = cand.Points[i];
            double segLen = (b - a).Length;
            bool inStartZone = walked < 24.0;
            bool inEndZone = cand.Length - (walked + segLen) < 24.0;
            double sMinX = Math.Min(a.X, b.X) - MinConnectorClearance, sMaxX = Math.Max(a.X, b.X) + MinConnectorClearance;
            double sMinZ = Math.Min(a.Y, b.Y) - MinConnectorClearance, sMaxZ = Math.Max(a.Y, b.Y) + MinConnectorClearance;
            foreach (var other in nearPaths)
            {
                bool isStart = other == startPath;
                bool isEnd = other == cand.Target;
                if ((isStart && inStartZone) || (isEnd && inEndZone)) continue;
                if (pathBounds.TryGetValue(other, out var ob) &&
                    (ob.MaxX < sMinX || ob.MinX > sMaxX || ob.MaxZ < sMinZ || ob.MinZ > sMaxZ))
                    continue;
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

    static bool SampleAlong(List<Point> pts, double targetDist, out Point point, out Vector tangent)
    {
        point = default; tangent = default;
        if (pts == null || pts.Count < 2 || targetDist < 0) return false;
        double walked = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var ab = pts[i] - pts[i - 1];
            double len = ab.Length;
            if (len < 1e-6) continue;
            if (walked + len >= targetDist)
            {
                double t = (targetDist - walked) / len;
                point = pts[i - 1] + ab * t;
                tangent = ab / len;
                return true;
            }
            walked += len;
        }
        return false;
    }

    static double ArcLengthToNearest(List<Point> pts, Point p, bool fromEnd)
    {
        double walked = 0, bestDist = 0, bestD2 = double.MaxValue, total = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var ab = pts[i] - a;
            double l2 = ab.LengthSquared;
            double len = Math.Sqrt(l2);
            double t = l2 == 0 ? 0 : Math.Clamp(((p - a) * ab) / l2, 0, 1);
            var q = a + ab * t;
            double d2 = (q - p).LengthSquared;
            if (d2 < bestD2) { bestD2 = d2; bestDist = walked + len * t; }
            walked += len;
            total += len;
        }
        return fromEnd ? Math.Max(0, total - bestDist) : bestDist;
    }

    static bool NearBridge(WildPath path, Point p, double radius)
    {
        if (path.Bridges == null || path.Bridges.Count == 0) return false;
        double r2 = radius * radius;
        foreach (var b in path.Bridges)
        {
            var (pa, pb, _) = NetworkBuilder.ApproachEnds(b);
            if ((p - b.Center).LengthSquared < r2 || (p - pa).LengthSquared < r2 || (p - pb).LengthSquared < r2)
                return true;
        }
        return false;
    }

    static bool SegmentsIntersect(Point a, Point b, Point c, Point d)
    {
        static double Cross(Point u, Point v, Point w) => (v.X - u.X) * (w.Y - u.Y) - (v.Y - u.Y) * (w.X - u.X);
        double d1 = Cross(a, b, c), d2 = Cross(a, b, d);
        double d3 = Cross(c, d, a), d4 = Cross(c, d, b);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    static double SegmentDistanceSquared(Point a, Point b, Point c, Point d)
    {
        static double PtSeg(Point p, Point u, Point v)
        {
            var uv = v - u;
            double l2 = uv.LengthSquared;
            double t = l2 == 0 ? 0 : Math.Clamp(((p - u) * uv) / l2, 0, 1);
            return (p - (u + uv * t)).LengthSquared;
        }
        return Math.Min(Math.Min(PtSeg(a, c, d), PtSeg(b, c, d)), Math.Min(PtSeg(c, a, b), PtSeg(d, a, b)));
    }
}
