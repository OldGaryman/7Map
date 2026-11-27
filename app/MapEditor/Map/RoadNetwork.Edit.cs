using System.Windows;

namespace MapEditor.Map;

/// Editing the planned network by hand, stage 2 (the user's request 2026-11-12): route a planned road through via points.
/// Uses the same cost grid and the same search as planning (driving direction per cell, at most 22.5° per 16 m step,
/// crossings only roughly at a right angle), so a rerouted road follows the same rules as a planned one. The first and
/// last FixedEnd metres stay as they are (connection to the town exit or the junction); junctions of other roads on this
/// road are passed exactly (they stay connected). Other planned roads are blocked like during planning and can only be
/// crossed; the crossings are found again from the new line afterwards (NetworkPlanEdit.UpdateCrossings).
public sealed partial class RoadNetwork
{
    /// first and last metres of a road that rerouting does not change
    public const double FixedEnd = 60;
    float[] baseNearD, baseNearH;

    /// network prepared for rerouting (cost grid and towns of this world); takes about as long as the start of planning
    public static RoadNetwork ForEditing(WorldData w, NetworkSpec spec)
    {
        var r = new RoadNetwork(w, spec);
        r.BuildGrid();
        r.FindTowns(streetExits: false);
        r.FindStreetCrossings();
        r.FindBridgeSpots();
        r.baseNearD = (float[])r.nearRoadD.Clone();
        r.baseNearH = (float[])r.nearRoadH.Clone();
        return r;
    }

    static double LineLength(List<Point> line)
    {
        double t = 0;
        for (int q = 1; q < line.Count; q++) t += (line[q] - line[q - 1]).Length;
        return t;
    }

    /// distance along the line of the point nearest to p, and that distance
    public static (double Along, double Off) Project(List<Point> line, Point p)
    {
        double walked = 0, best = double.MaxValue, along = 0;
        for (int q = 0; q + 1 < line.Count; q++)
        {
            var a = line[q]; var ab = line[q + 1] - a;
            double l2 = ab.LengthSquared, len = Math.Sqrt(l2);
            double t = l2 < 1e-9 ? 0 : Math.Clamp(((p - a) * ab) / l2, 0, 1);
            double d = (p - (a + ab * t)).Length;
            if (d < best) { best = d; along = walked + t * len; }
            walked += len;
        }
        return (along, best);
    }

    /// point and direction (unit vector) at a distance along the line
    public static (Point P, Vector Dir, int Index) At(List<Point> line, double dist)
    {
        double walked = 0;
        for (int q = 0; q + 1 < line.Count; q++)
        {
            var ab = line[q + 1] - line[q];
            double len = ab.Length;
            if (walked + len >= dist || q + 2 == line.Count)
            {
                double t = len < 1e-9 ? 0 : Math.Clamp((dist - walked) / len, 0, 1);
                if (len > 1e-9) ab /= len;
                return (line[q] + (line[q + 1] - line[q]) * t, ab, q);
            }
            walked += len;
        }
        return (line[^1], new Vector(1, 0), line.Count - 1);
    }

    /// junctions of other roads on this road (they must stay on it)
    static List<Point> JunctionsOn(NetworkPlan plan, NetRoad road)
    {
        var list = new List<Point>();
        foreach (var o in plan.Roads)
        {
            if (o == road || o.Points.Count == 0) continue;
            if (o.StartRoad == road) list.Add(o.Points[0]);
            if (o.EndRoad == road) list.Add(o.Junction ?? o.Points[^1]);
        }
        return list;
    }

    /// New course of the road through the via points (in this order). null + problem if there is no way.
    /// The plan itself is not changed (the caller enters the line).
    public List<Point> Reroute(NetworkPlan plan, NetRoad road, IList<Point> vias, out string problem) =>
        Reroute(plan, road, vias, out problem, out _);

    /// orderedVias = the via points of the user in the order in which the road passes them. pins = crossings with other
    /// roads that the user moved (place Q on road B): the road crosses B exactly there, at a right angle; afterwards the
    /// two points 44 m beside B on both sides become ordinary via points (so that later changes keep the crossing there).
    public List<Point> Reroute(NetworkPlan plan, NetRoad road, IList<Point> vias, out string problem, out List<Point> orderedVias,
        IList<(Point Q, NetRoad B)> pins = null, bool free = false)
    {
        problem = null;
        orderedVias = vias.ToList();
        var line = road.Points;
        double total = LineLength(line);
        double fa = FixedLength(road, true, free), fb = FixedLength(road, false, free);
        if (total < fa + fb + 40) { problem = Loc.T("This road is too short to be routed differently."); return null; }
        var (a, aDir, ai) = At(line, fa);
        var (b, bDir, bi) = At(line, total - fb);
        var head = line.Take(ai + 1).Append(a).ToList();
        var forced = JunctionsOn(plan, road).Where(p => { double t = Project(line, p).Along; return t > fa + 5 && t < total - fb - 5; });
        var tail = new List<Point> { b }; tail.AddRange(line.Skip(bi + 1));
        var result = RouteThrough(plan, road, road.From, road.To, head, a, aDir, b, bDir, tail, vias, forced, pins, road.Cut, out problem, out orderedVias);
        if (result == null) return null;
        foreach (var v in orderedVias)
            if ((v - line[0]).Length < fa + 8 || (v - line[^1]).Length < fb + 8)
            {
                problem = free ? Loc.F("The first {0:0} m and the last {1:0} m of this road stay as they are (connection to the town, tile or other road). Please choose a point further away.", fa, fb)
                    : Loc.F("The first {0:0} m and the last {1:0} m of this road stay as they are (connection to the town, tile or other road). Please choose a point further away, or hold Alt while dragging for more freedom.", fa, fb);
                return null;
            }
        return result;
    }

    /// metres at the start (atStart) or end of a road that rerouting keeps (the user's request 2026-11-16, ToDo PART III: roads
    /// directly at a junction or tile can be dragged; free = Alt held while dragging, more freedom): at a town or tile exit the
    /// straight piece out of the exit (44 m, with Alt 24 m – the margin of the tile), at a junction 24 m (with Alt 10 m)
    public static double FixedLength(NetRoad road, bool atStart, bool free)
    {
        var exit = atStart ? road.From : road.To;
        var other = atStart ? road.StartRoad : road.EndRoad;
        if (exit != null) return free ? 24 : ApproachLength;
        if (other != null) return free ? 10 : 24;
        return free ? 16 : 30;   // free end (map edge, dead end)
    }

    /// Search between the fixed head (ending at a) and the fixed tail (beginning at b) through via points (within ViaRadius),
    /// junctions of other roads (exactly) and moved crossings (pins). Shared by rerouting, drawing and moving junctions.
    List<Point> RouteThrough(NetworkPlan plan, NetRoad road, NetExit from, NetExit to, List<Point> head, Point a, Vector aDir,
        Point b, Vector bDir, List<Point> tail, IList<Point> vias, IEnumerable<Point> forced, IList<(Point Q, NetRoad B)> pins, bool cut,
        out string problem, out List<Point> orderedVias)
    {
        problem = null;
        orderedVias = vias.ToList();
        // waypoints: the via points of the user, the junctions of other roads on this road and the moved crossings, in the order
        // that gives the shortest way from the start over all points to the end (the user's hint 2026-11-12: with via points set
        // one after the other the order along the old, already bent line went back and forth and the road made loops)
        var pinAt = new Dictionary<Point, NetRoad>();
        foreach (var (q, br) in pins ?? Array.Empty<(Point, NetRoad)>()) pinAt[q] = br;
        var order = ShortestOrder(a, b, vias.Select(v => (v, false)).Concat(forced.Select(p => (p, true))).Concat(pinAt.Keys.Select(q => (q, true))).ToList());
        // expand a moved crossing into the two points 44 m beside the crossed road (first the one on the side the road comes from)
        var wps = new List<Point>();
        var radius = new List<double>();
        var user = new List<bool>();
        var crossJumps = new List<(Point P1, Point P2, Point Q, NetRoad B)>();
        foreach (var (p, f) in order)
        {
            if (pinAt.TryGetValue(p, out var br))
            {
                var (_, tan, _) = At(br.Points, Project(br.Points, p).Along);
                var nrm = new Vector(-tan.Y, tan.X);
                var prevP = wps.Count > 0 ? wps[^1] : a;
                var side = (prevP - p) * nrm >= 0 ? nrm : -nrm;
                Point p1 = p + side * ApproachLength, p2 = p - side * ApproachLength;
                wps.Add(p1); radius.Add(0); user.Add(true);
                wps.Add(p2); radius.Add(0); user.Add(true);
                crossJumps.Add((p1, p2, p, br));
                continue;
            }
            wps.Add(p); radius.Add(f ? 0 : ViaRadius); user.Add(!f);
        }

        PrepareGrid(plan, road, from, to);
        // a road with a moved crossing may cross it only there
        var pinned = new HashSet<NetRoad>(crossJumps.Select(j => j.B));
        if (pinned.Count > 0)
            foreach (var l in jumps.Values) l.RemoveAll(j => pinned.Contains(j.Road));
        foreach (var (p1, p2, q, br) in crossJumps)
        {
            int c1 = CellOf(p1.X, p1.Y), c2 = CellOf(p2.X, p2.Y);
            if (c1 < 0 || c2 < 0) continue;
            float cost = (float)(2 * ApproachLength + CrossPenalty);
            if (!jumps.TryGetValue(c1, out var l1)) jumps[c1] = l1 = new();
            l1.Add((c2, cost, q, br, 0, DirOf(p2.X - p1.X, p2.Y - p1.Y)));
        }
        // the fixed pieces at both ends are blocked like a foreign road (the new course must not run along them)
        if (head.Count > 1) BlockAlong(head);
        if (tail.Count > 1) BlockAlong(tail);
        // free the cells around the waypoints and the anchors (junction points lie next to the branching road; the points beside a
        // moved crossing only their own cell, otherwise the road could step across the crossed road at a slant)
        var crossSide = new HashSet<Point>(crossJumps.SelectMany(j => new[] { j.P1, j.P2 }));
        for (int k = 0; k < wps.Count; k++) Unblock(wps[k], crossSide.Contains(wps[k]) ? 12 : radius[k] == 0 ? 40 : 24);
        Unblock(a, 32); Unblock(b, 32);
        foreach (var t in wps.Append(a).Append(b))
        {
            int c = CellOf(t.X, t.Y);
            c = OpenCellNear(t, c, k => !blocked[k]);
            if (c < 0 || blocked[c]) { problem = Loc.F("The point at {0} lies on an obstacle (water, building, town or map edge).", Pos(t.X, t.Y)); return null; }
        }

        // One search over the whole course that has to pass the points in order (the user's hint 2026-11-12: searched
        // piece by piece, the road arrived at a point in a direction from which it could not turn towards the next point
        // and made loops over itself in the mountains). The search decides the direction at every point itself so that the
        // whole road becomes cheapest. A point set by hand only has to be passed within ViaRadius (afterwards it moves onto
        // the road): points close together at a sharp angle would otherwise force a loop because of the smallest curve
        // radius. Junctions of other roads and moved crossings must be met exactly.
        // The road must not cross itself (the user's request 2026-11-16, ToDo PART III, picture SG01: a point dragged to the side
        // made the road go out to it in a loop over itself). If it does, the points set by hand may be passed further away
        // (they then move onto the road), otherwise the change is refused.
        List<Point> result = null;
        bool crossed = false;
        foreach (double widen in new[] { 1.0, 2.5, 5.0 })
        {
            var rad = radius.Select(r => r * widen).ToArray();
            var path = LayeredSearch(a, aDir, wps, rad, b, bDir, cut);
            if (path == null && cut) path = LayeredSearch(a, aDir, wps, rad, b, bDir, false);
            if (path == null)
            {
                if (crossed) break;
                problem = Loc.T("No way found through these points (water, steep walls, towns or other roads in the way). Try points a little further apart or in another place.");
                return null;
            }
            var line = LineFromPath(head, path, wps, rad, b, tail.Skip(1).ToList());
            crossed = SelfCrossing(line) != null;
            if (!crossed) { result = line; break; }
            if (!radius.Any(r => r > 0)) break;   // no point set by hand that could move
        }
        if (result == null)
        {
            problem = Loc.T("Here the road would cross itself (it cannot turn round that tightly). Please put the point a little further from the road or somewhere else.");
            return null;
        }
        // the points set by hand move onto the road (the handles in the map then lie on it)
        orderedVias = wps.Where((_, k) => user[k]).Select(v => Nearest(result, v)).ToList();
        return result;
    }

    /// first place where a line crosses itself (two pieces that are not neighbors intersect); null = it does not
    public static Point? SelfCrossing(List<Point> line)
    {
        static double Cross(Vector u, Vector v) => u.X * v.Y - u.Y * v.X;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            Point p = line[i]; var r = line[i + 1] - p;
            double minX = Math.Min(p.X, line[i + 1].X), maxX = Math.Max(p.X, line[i + 1].X), minY = Math.Min(p.Y, line[i + 1].Y), maxY = Math.Max(p.Y, line[i + 1].Y);
            for (int j = i + 2; j + 1 < line.Count; j++)
            {
                Point q = line[j]; var s = line[j + 1] - q;
                if (Math.Max(q.X, line[j + 1].X) < minX || Math.Min(q.X, line[j + 1].X) > maxX || Math.Max(q.Y, line[j + 1].Y) < minY || Math.Min(q.Y, line[j + 1].Y) > maxY) continue;
                double d = Cross(r, s);
                if (Math.Abs(d) < 1e-9) continue;
                double t = Cross(q - p, s) / d, u = Cross(q - p, r) / d;
                if (t > 1e-6 && t < 1 - 1e-6 && u > 1e-6 && u < 1 - 1e-6) return p + r * t;
            }
        }
        return null;
    }

    /// grid for a search by hand: other planned roads blocked (crossing only at a right angle via jumps), the forecourts
    /// of the given exits allowed
    void PrepareGrid(NetworkPlan plan, NetRoad road, NetExit from, NetExit to)
    {
        Array.Copy(baseNearD, nearRoadD, n);
        Array.Copy(baseNearH, nearRoadH, n);
        Array.Clear(roadBlock);
        foreach (var o in plan.Roads)
        {
            if (o == road || o.Points.Count < 2) continue;
            o.Samples ??= Resample(o.Points, 16);
            BlockAlong(o.Points);
        }
        BuildEditJumps(plan, road);
        allowed = new HashSet<NetExit>(exitList.Where(e => Near(e, from) || Near(e, to)));
    }

    /// line of a road from the fixed head (ending at the search start a), the searched cells and the fixed tail
    /// (b, then tail): crossings straight across, junction waypoints exactly, smoothed like planned roads
    List<Point> LineFromPath(List<Point> head, List<(int Cell, int Layer)> path, List<Point> wps, double[] radius, Point b, List<Point> tail)
    {
        var pts = new List<Point>();
        var fixedAt = new List<bool>();
        void Add(Point p, bool f) { pts.Add(p); fixedAt.Add(f); }
        foreach (var hp in head) Add(hp, true);
        Point prev = head[^1];
        for (int q = 1; q < path.Count; q++)
        {
            var (cell, layer) = path[q];
            int lastLayer = path[q - 1].Layer;
            var c = Center(cell);
            bool crossed = false;
            if (jumps.TryGetValue(path[q - 1].Cell, out var list) && !IsNeighbour(path[q - 1].Cell, cell)
                && list.FirstOrDefault(j => j.To == cell) is var jp && jp.Cost > 0 && AddBridgePoints(path[q - 1].Cell, jp.K, pts, fixedAt, null))
            {
                prev = pts[^1];   // over the bridge: straight over approach, bridge and approach (the end of it lies in this cell)
                crossed = true;
            }
            else if (jumps.TryGetValue(path[q - 1].Cell, out list) && !IsNeighbour(path[q - 1].Cell, cell)
                && list.FirstOrDefault(j => j.To == cell) is var jp2 && jp2.Cost > 0)
            {
                fixedAt[^1] = true;   // straight across the other road
                Add(jp2.Mid, true);
                if (layer == lastLayer) { Add(c, true); prev = c; }   // otherwise the exact waypoint follows
                crossed = true;
            }
            if (layer > lastLayer)
            {
                // waypoints reached in this cell: junctions exactly, points set by hand near enough
                bool exact = false;
                for (int k = lastLayer; k < layer; k++) if (radius[k] == 0) { Add(wps[k], true); prev = wps[k]; exact = true; }
                if (!exact && !crossed && q < path.Count - 1) { Add(c, false); prev = c; }
                continue;
            }
            if (crossed || q == path.Count - 1) continue;
            var next = layer < wps.Count ? wps[layer] : b;
            if ((c - prev).Length < 10 || (c - next).Length < 10) continue;
            Add(c, false);
            prev = c;
        }
        Add(b, true);
        foreach (var tp in tail) Add(tp, true);
        // drop points that lie on top of each other (smoothing divides by the distance)
        for (int q = pts.Count - 1; q > 0; q--)
            if ((pts[q] - pts[q - 1]).Length < 0.5) { bool f = fixedAt[q] || fixedAt[q - 1]; pts.RemoveAt(q); fixedAt.RemoveAt(q); fixedAt[q - 1] = f; }
        return KeepOffEdge(Smooth(pts, fixedAt));
    }

    // ---------- stage 3: a new road between two places (the user's request 2026-11-12) ----------

    /// end of a new road: a free town exit, or a place on a planned road (junction; Tangent = direction of that road)
    public sealed record NetEnd(NetExit Exit, NetRoad Road, Point P, Vector Tangent)
    {
        /// dead end of an existing road: Tangent is the fixed direction out of the road end (the new road continues it)
        public bool Fixed { get; init; }
    }

    /// end at a clicked place: the nearest free town exit within tol, otherwise the nearest place on a planned road
    /// (at least 40 m from its ends); null = nothing there
    public static NetEnd FindEnd(NetworkPlan plan, Point p, double tol)
    {
        NetEnd best = null; double bd = tol;
        foreach (var e in plan.Towns.SelectMany(t => t.Exits).Where(e => !e.Used && e.Reachable))
        {
            double d = Math.Min((new Point(e.X, e.Z) - p).Length, (e.Approach - p).Length * 1.5);
            if (d < bd) { bd = d; best = new NetEnd(e, null, new Point(e.X, e.Z), new Vector(e.DX, e.DZ)); }
        }
        if (best != null) return best;
        foreach (var r in plan.Roads)
        {
            if (r.Points.Count < 2) continue;
            var (along, off) = Project(r.Points, p);
            double len = LineLength(r.Points);
            if (off >= bd || along < 40 || along > len - 40) continue;
            var (at, dir, _) = At(r.Points, along);
            bd = off;
            best = new NetEnd(null, r, at, dir);
        }
        return best;
    }

    /// direction in which the new road leaves this end (towards the other end): out of the town, or at a right angle
    /// away from the road
    const double ShortLinkLength = 250;   // up to this distance a direct curve between the ends is tried first

    /// direct curve from s to e (cubic Bézier: leaves s along os, arrives at e against oe), points every 8 m; null + reason if it
    /// turns too sharply, the height difference is too steep for the length, or water, a building, a tile or another planned road
    /// lies on the way
    List<Point> ShortLink(NetworkPlan plan, NetRoad road, NetEnd s, NetEnd e, Vector os, Vector oe, out string problem)
    {
        problem = Loc.T("Start and end are too close together, and a direct connection is not possible there (sharp bend, steep, or something in the way).");
        var v = e.P - s.P;
        double dist = v.Length;
        if (dist < 20) return null;
        var dir = v / dist;
        if (Vector.Multiply(os, dir) < 0.35 || Vector.Multiply(-oe, dir) < 0.35) return null;   // at most about 70° from the straight line at each end
        double k = dist * 0.4;
        Point c1 = s.P + os * k, c2 = e.P + oe * k;
        var line = new List<Point>();
        int steps = Math.Max(4, (int)Math.Ceiling(dist * 1.3 / 2));
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps, u = 1 - t;
            line.Add(new Point(u * u * u * s.P.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * e.P.X,
                               u * u * u * s.P.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * e.P.Y));
        }
        double len = LineLength(line);
        // gradient: tile height at an exit, otherwise the terrain
        double H(NetEnd n) => n.Exit?.Tile != null ? n.Exit.Tile.TerrainHeight : w.HeightAt((int)Math.Floor(n.P.X), (int)Math.Floor(n.P.Y));
        double rise = Math.Abs(H(e) - H(s));
        // short stretches may use the exception up to ShortMaxGrade (the user's request 2026-11-12)
        double allowed = len <= spec.ShortSteepLength ? Math.Max(spec.ShortMaxGrade, spec.MaxGrade) : spec.MaxGrade;
        if (rise > allowed * Math.Max(1, len - 8))
        {
            problem = Loc.F("Start and end are too close together for the height difference of {0:0} m (a direct connection would be steeper than {1:0} %).", rise, allowed * 100);
            return null;
        }
        // free on the way: no water, building or tile (the exit's own tile only in the first 2 m), no other planned road
        var near = w.Prefabs.Where(p => p.Def != null && p.X + p.SizeX > Math.Min(s.P.X, e.P.X) - dist && p.X < Math.Max(s.P.X, e.P.X) + dist
            && p.Z + p.SizeZ > Math.Min(s.P.Y, e.P.Y) - dist && p.Z < Math.Max(s.P.Y, e.P.Y) + dist).ToList();
        double walked = 0;
        for (int i = 1; i < line.Count; i++)
        {
            walked += (line[i] - line[i - 1]).Length;
            var q = line[i];
            if (walked > 3 && w.WaterAt((int)Math.Floor(q.X), (int)Math.Floor(q.Y))) return null;
            foreach (var p in near)
            {
                double m = p.IsTile ? 0 : 3;
                if (q.X > p.X - m && q.X < p.X + p.SizeX + m && q.Y > p.Z - m && q.Y < p.Z + p.SizeZ + m && !(p == s.Exit?.Tile && walked < 2) && !(p == e.Exit?.Tile && len - walked < 2)
                    && !(p == s.Exit?.Poi && walked < 8) && !(p == e.Exit?.Poi && len - walked < 8))
                    return null;
            }
            foreach (var r in plan.Roads)
            {
                if (r == road || r == s.Road || r == e.Road || r.Points.Count < 2) continue;
                if (Project(r.Points, q).Off < 16) return null;
            }
        }
        problem = null;
        // every 8 m, start and end exactly at the ends
        var res = new List<Point> { s.P };
        double next = 8, along = 0;
        for (int i = 1; i < line.Count; i++)
        {
            along += (line[i] - line[i - 1]).Length;
            if (along >= next && len - along > 4) { res.Add(line[i]); next = along + 8; }
        }
        res.Add(e.P);
        return res;
    }

    static Vector OutOf(NetEnd e, Point other)
    {
        if (e.Exit != null || e.Fixed) return e.Tangent;
        var nrm = new Vector(-e.Tangent.Y, e.Tangent.X);
        return (other - e.P) * nrm >= 0 ? nrm : -nrm;
    }

    /// course from end s to end e: straight piece of 44 m at both ends, in between the search through via points and
    /// junctions (road = the road whose course this is, or null for a new one)
    List<Point> Between(NetworkPlan plan, NetRoad road, NetEnd s, NetEnd e, IList<Point> vias, IEnumerable<Point> forced, bool cut,
        out string problem, out List<Point> orderedVias)
    {
        problem = null; orderedVias = vias.ToList();
        if ((s.Exit != null && s.Exit == e.Exit) || (s.Road != null && s.Road == e.Road && (s.P - e.P).Length < 300))
        { problem = Loc.T("Start and end are too close together."); return null; }
        Vector os = OutOf(s, e.P), oe = OutOf(e, s.P);
        // short connection (the user's hint 2026-11-12: a town exit 105 m in front of a road could not be connected, and slightly
        // further away the search drove a loop over the road and crossed it twice): a smooth curve directly between the ends
        if (vias.Count == 0 && !forced.Any() && (s.P - e.P).Length <= ShortLinkLength)
        {
            var direct = ShortLink(plan, road, s, e, os, oe, out string why);
            if (direct != null) return direct;
            if ((s.P - e.P).Length < 2 * ApproachLength + 30) { problem = why; return null; }
        }
        else if ((s.P - e.P).Length < 2 * ApproachLength + 30)
        { problem = Loc.T("Start and end are too close together."); return null; }
        var head = new List<Point> { s.P, s.P + os * 24, s.P + os * ApproachLength };
        var tail = new List<Point> { e.P + oe * ApproachLength, e.P + oe * 24, e.P };
        var line = RouteThrough(plan, road, s.Exit, e.Exit, head, head[^1], os, tail[0], -oe, tail, vias, forced, null, cut, out problem, out orderedVias);
        if (line == null && vias.Count == 0 && problem == Loc.T("No way found through these points (water, steep walls, towns or other roads in the way). Try points a little further apart or in another place."))
            problem = Loc.T("No way found between these two places (water, steep walls, towns or other roads in the way).");
        return line;
    }

    /// Move the junction of a road (atStart = its start, otherwise its end) to the place target on another planned road.
    /// The road is searched again between its ends (through its via points; junctions of other roads on it stay on it).
    /// The plan is not changed: returns the new line, or null + problem.
    public List<Point> MoveJunction(NetworkPlan plan, NetRoad road, bool atStart, NetEnd target, out string problem, out List<Point> orderedVias)
    {
        orderedVias = road.Via.ToList();
        NetEnd Keep(bool start)
        {
            var ex = start ? road.From : road.To;
            if (ex != null) return new NetEnd(ex, null, new Point(ex.X, ex.Z), new Vector(ex.DX, ex.DZ));
            var other = start ? road.StartRoad : road.EndRoad;
            var p = start ? road.Points[0] : road.Junction ?? road.Points[^1];
            if (other == null) { var d = start ? road.Points[1] - road.Points[0] : road.Points[^2] - road.Points[^1]; d.Normalize(); return new NetEnd(null, null, p, new Vector(d.Y, -d.X)); }
            var (_, tan, _) = At(other.Points, Project(other.Points, p).Along);
            return new NetEnd(null, other, p, tan);
        }
        var s = atStart ? target : Keep(true);
        var e = atStart ? Keep(false) : target;
        var forced = JunctionsOn(plan, road).Where(p => (p - road.Points[0]).Length > FixedEnd && (p - road.Points[^1]).Length > FixedEnd);
        return Between(plan, road, s, e, road.Via, forced, road.Cut, out problem, out orderedVias);
    }

    /// place on a planned road near p (at least 40 m from its ends), not on the given roads; null = none within tol
    public static NetEnd FindRoadPlace(NetworkPlan plan, Point p, double tol, ICollection<NetRoad> not)
    {
        NetEnd best = null; double bd = tol;
        foreach (var r in plan.Roads)
        {
            if (r.Points.Count < 2 || not.Contains(r)) continue;
            var (along, off) = Project(r.Points, p);
            double len = LineLength(r.Points);
            if (off >= bd || along < 40 || along > len - 40) continue;
            var (at, dir, _) = At(r.Points, along);
            bd = off;
            best = new NetEnd(null, r, at, dir);
        }
        return best;
    }

    /// New road from s to e with the same rules as planned roads (straight piece of 44 m at both ends, then the search).
    /// The plan is not changed; null + problem if there is no way.
    public NetRoad NewRoad(NetworkPlan plan, NetEnd s, NetEnd e, out string problem)
    {
        var line = Between(plan, null, s, e, Array.Empty<Point>(), Array.Empty<Point>(), false, out problem, out _);
        if (line == null) return null;
        bool gravel = s.Exit?.Gravel == true || e.Exit?.Gravel == true;
        int exitWidth = Math.Max(s.Exit?.Width ?? 0, e.Exit?.Width ?? 0);
        var r = new NetRoad
        {
            Points = line, Gravel = gravel, Width = gravel ? Math.Clamp(exitWidth, 6, 12) : 16, Manual = true,
            From = s.Exit, To = e.Exit, StartRoad = s.Road, EndRoad = e.Road, Junction = e.Road != null ? e.P : null,
        };
        Remeasure(r);
        return r;
    }

    /// A* from a (driving direction aDir) over all waypoints in order to b (arriving in direction bDir); state = cell ·
    /// driving direction · number of waypoints passed. Same steps and costs as the planner (StepCost, crossings via jumps).
    /// Only within a box around the points (800 m margin), so the search stays small. Returns (cell, waypoints passed)
    /// from a to b, or null.
    /// a via point set by hand counts as passed within this distance (m)
    public const double ViaRadius = 32;

    public static Point Nearest(List<Point> line, Point p)
    {
        Point best = line[0]; double bd = double.MaxValue;
        for (int q = 0; q + 1 < line.Count; q++)
        {
            var ab = line[q + 1] - line[q]; double l2 = ab.LengthSquared;
            double t = l2 < 1e-9 ? 0 : Math.Clamp(((p - line[q]) * ab) / l2, 0, 1);
            var f = line[q] + ab * t; double d = (p - f).LengthSquared;
            if (d < bd) { bd = d; best = f; }
        }
        return best;
    }

    List<(int Cell, int Layer)> LayeredSearch(Point a, Vector aDir, List<Point> wps, double[] radius, Point b, Vector bDir, bool cut)
    {
        int K = wps.Count;
        var wpCell = wps.Select(p => CellOf(p.X, p.Y)).ToArray();
        int aCell = OpenCellNear(a, CellOf(a.X, a.Y), k => !blocked[k]), bCell = OpenCellNear(b, CellOf(b.X, b.Y), k => !blocked[k]);
        if (aCell < 0 || bCell < 0 || wpCell.Any(c => c < 0)) return null;
        var all = wps.Append(a).Append(b).ToList();
        const double Margin = 800;
        int i0 = Math.Max(0, (int)Math.Floor((all.Min(p => p.X) - Margin + half) / C)), i1 = Math.Min(gw - 1, (int)Math.Floor((all.Max(p => p.X) + Margin + half) / C));
        int j0 = Math.Max(0, (int)Math.Floor((all.Min(p => p.Y) - Margin + half) / C)), j1 = Math.Min(gw - 1, (int)Math.Floor((all.Max(p => p.Y) + Margin + half) / C));
        int bw = i1 - i0 + 1, bh = j1 - j0 + 1, cells = bw * bh;
        long total = (long)cells * Dirs * (K + 1);
        if (total > 60_000_000) return null;
        var dist = new float[total];
        var par = new int[total];
        Array.Fill(dist, float.MaxValue);
        int Local(int g) => (g / gw - j0) * bw + (g % gw - i0);
        int Global(int l) => (l / bw + j0) * gw + l % bw + i0;
        int State(int layer, int g, int dir) => (layer * cells + Local(g)) * Dirs + dir;
        int Advance(int layer, int g)
        {
            while (layer < K && (g == wpCell[layer] || (radius[layer] > 0 && (Center(g) - wps[layer]).Length <= radius[layer]))) layer++;
            return layer;
        }
        if (minMult < 0) { float m = float.MaxValue; foreach (float v in mult) m = Math.Min(m, v); minMult = m; }
        double hk = 0.95 * minMult;
        // remaining straight-line length from waypoint k over the following ones to b
        var rest = new double[K + 1];
        // (lower bound: every point counts as reached at the edge of its radius)
        for (int k = K - 1; k >= 0; k--)
            rest[k] = Math.Max(0, ((k + 1 < K ? wps[k + 1] : b) - wps[k]).Length - radius[k] - (k + 1 < K ? radius[k + 1] : 0)) + rest[k + 1];
        float Est(int layer, int g) => (float)((Math.Max(0, ((layer < K ? wps[layer] : b) - Center(g)).Length - (layer < K ? radius[layer] : 0)) + rest[layer]) * hk);
        var h = cut ? hcut : hb;
        double G = spec.Grade, Gm = Math.Max(spec.MaxGrade, G + 0.01);
        int startDir = DirOf(aDir.X, aDir.Y), endDir = DirOf(bDir.X, bDir.Y);
        var queue = new PriorityQueue<int, float>();
        int startLayer = Advance(0, aCell);
        for (int t = -1; t <= 1; t++)
        {
            int s = State(startLayer, aCell, (startDir + t + Dirs) % Dirs);
            dist[s] = Math.Abs(t) * 20f; par[s] = -1;
            queue.Enqueue(s, dist[s] + Est(startLayer, aCell));
        }
        int found = -1;
        while (queue.TryDequeue(out int st, out float f))
        {
            float d = dist[st];
            int dir = st % Dirs, rs = st / Dirs, layer = rs / cells, g = Global(rs % cells);
            if (f > d + Est(layer, g) + 0.01f) continue;   // outdated entry
            if (layer == K && g == bCell && DirDiff(dir, endDir) <= 1) { found = st; break; }
            int i = g % gw, j = g / gw;
            void Relax(int ng, int ndir, double cost)
            {
                int nl = Advance(layer, ng);
                int ns = State(nl, ng, ndir);
                float nd = (float)(d + cost);
                if (nd < dist[ns]) { dist[ns] = nd; par[ns] = st; queue.Enqueue(ns, nd + Est(nl, ng)); }
            }
            for (int turn = -1; turn <= 1; turn++)
            {
                int ndir = (dir + turn + Dirs) % Dirs;
                var (di, dj) = Steps[ndir];
                int ni = i + di, nj = j + dj;
                if (ni < i0 || nj < j0 || ni > i1 || nj > j1) continue;
                int ng = nj * gw + ni;
                if (!Passable(ng)) continue;
                if (Math.Abs(di) == 2 || Math.Abs(dj) == 2)
                {
                    int si = Math.Sign(di), sj = Math.Sign(dj);
                    int m1 = Math.Abs(di) == 2 ? j * gw + i + si : (j + sj) * gw + i;
                    int m2 = Math.Abs(di) == 2 ? (j + dj) * gw + i + si : (j + sj) * gw + i + di;
                    if (!Passable(m1) || !Passable(m2)) continue;
                }
                double cost = StepCost(g, ng, di, dj, turn, h, G, Gm);
                if (double.IsNaN(cost)) continue;
                Relax(ng, ndir, cost);
            }
            if (jumps.TryGetValue(g, out var list))
                foreach (var (to, cost, _, _, _, jd) in list)
                {
                    if (DirDiff(dir, jd) > 1) continue;
                    int ti = to % gw, tj = to / gw;
                    if (ti < i0 || tj < j0 || ti > i1 || tj > j1) continue;
                    Relax(to, jd, cost);
                }
        }
        if (found < 0) return null;
        var res = new List<(int Cell, int Layer)>();
        for (int s = found; s >= 0; s = par[s]) { int r = s / Dirs; res.Add((Global(r % cells), r / cells)); }
        res.Reverse();
        Log?.Invoke($"   layered search: {res.Count} cells, box {bw}×{bh}, cost {dist[found]:0}");
        return res;
    }

    /// order of the points that gives the shortest polyline a → points → b (all orders up to 7 points, otherwise cheapest
    /// insertion); with equal length the given order stays
    static List<(Point P, bool Forced)> ShortestOrder(Point a, Point b, List<(Point P, bool Forced)> pts)
    {
        double Len(IList<(Point P, bool Forced)> order)
        {
            double s = 0; Point prev = a;
            foreach (var x in order) { s += (x.P - prev).Length; prev = x.P; }
            return s + (b - prev).Length;
        }
        if (pts.Count <= 1) return pts.ToList();
        if (pts.Count <= 7)
        {
            var best = pts.ToList(); double bestLen = Len(best) - 1e-6;
            var cur = new List<(Point P, bool Forced)>();
            var used = new bool[pts.Count];
            void Permute()
            {
                if (cur.Count == pts.Count) { double l = Len(cur); if (l < bestLen) { bestLen = l; best = cur.ToList(); } return; }
                for (int k = 0; k < pts.Count; k++)
                {
                    if (used[k]) continue;
                    used[k] = true; cur.Add(pts[k]);
                    Permute();
                    cur.RemoveAt(cur.Count - 1); used[k] = false;
                }
            }
            Permute();
            return best;
        }
        var route = new List<(Point P, bool Forced)>();
        foreach (var x in pts)
        {
            int at = 0; double cheapest = double.MaxValue;
            for (int k = 0; k <= route.Count; k++)
            {
                Point p0 = k == 0 ? a : route[k - 1].P, p1 = k == route.Count ? b : route[k].P;
                double add = (x.P - p0).Length + (p1 - x.P).Length - (p1 - p0).Length;
                if (add < cheapest) { cheapest = add; at = k; }
            }
            route.Insert(at, x);
        }
        return route;
    }

    static bool Near(NetExit internalExit, NetExit planExit) =>
        planExit != null && Math.Abs(internalExit.X - planExit.X) < 1 && Math.Abs(internalExit.Z - planExit.Z) < 1;

    void Unblock(Point p, double radius)
    {
        int r = (int)Math.Ceiling(radius / C), c0 = CellOf(p.X, p.Y);
        if (c0 < 0) return;
        int i0 = c0 % gw, j0 = c0 / gw;
        for (int dj = -r; dj <= r; dj++)
            for (int di = -r; di <= r; di++)
            {
                int i = i0 + di, j = j0 + dj;
                if (i < 0 || j < 0 || i >= gw || j >= gw) continue;
                if ((Center(j * gw + i) - p).Length <= radius) roadBlock[j * gw + i] = false;
            }
    }

    /// crossings over the other planned roads: wherever such a road runs fairly straight (like planning, but without the
    /// minimum distance to other junctions – the user decides where the road goes)
    void BuildEditJumps(NetworkPlan plan, NetRoad road)
    {
        jumps = new();
        AddStreetJumps();
        AddEditBridgeJumps(plan, road);
        foreach (var r in plan.Roads)
        {
            if (r == road) continue;
            var s = r.Samples ??= Resample(r.Points, 16);
            for (int k = 3; k + 3 < s.Count; k++)
            {
                var p = s[k];
                Vector va = s[k] - s[k - 3], vb = s[k + 3] - s[k];
                if (va.Length < 1 || vb.Length < 1 || Math.Abs(Vector.AngleBetween(va, vb)) > 25) continue;
                var d = s[k + 3] - s[k - 3]; d.Normalize();
                var nrm = new Vector(-d.Y, d.X);
                int c0 = CellOf(p.X + nrm.X * ApproachLength, p.Y + nrm.Y * ApproachLength);
                int c1 = CellOf(p.X - nrm.X * ApproachLength, p.Y - nrm.Y * ApproachLength);
                if (!Passable(c0) || !Passable(c1)) continue;
                float cost = (float)(2 * ApproachLength + CrossPenalty);
                if (!jumps.TryGetValue(c0, out var l0)) jumps[c0] = l0 = new();
                if (!jumps.TryGetValue(c1, out var l1)) jumps[c1] = l1 = new();
                l0.Add((c1, cost, p, r, k, DirOf(-nrm.X, -nrm.Y)));
                l1.Add((c0, cost, p, r, k, DirOf(nrm.X, nrm.Y)));
            }
        }
    }

    /// length, gradient and steep share of a road after a change (like after planning)
    public void Remeasure(NetRoad r)
    {
        r.Bridges = BridgesOn(r);
        r.Samples = Resample(r.Points, 16);
        r.Length = r.Samples.Count > 1 ? (r.Samples.Count - 1) * 16.0 : 0;
        r.MaxGrade = 0; r.SteepShare = 0; r.MaxCut = 0; r.SteepAt = r.CutAt = null;
        Measure(r);
    }
}
