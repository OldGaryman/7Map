using System.Windows;

namespace MapEditor.Map;

/// Roads to the map edge (the user's request 2026-11-14): a town exit that found no connection in all the other passes and lies near
/// the map edge gets a road out to the edge – there it simply ends (in the game it leads "out of the map"). Town tiles themselves stay
/// as they are (the user's decision: tiles from "Generate towns" are never exchanged, unused exits are shown as dead ends).
/// The road keeps the 80 m to the edge (EdgeKeep) on its way like every other road and only runs straight out to the edge at its end.
public sealed partial class RoadNetwork
{
    /// exit at most this far from the map edge (m) – farther inside a road to the edge would be a long way to nowhere
    const double EdgeRoadReach = 1500;
    /// ends of two roads to the edge at least this far apart (m)
    const double EdgeRoadSpacing = 1000;
    /// tests: false = skip this pass
    public static bool EdgeRoadPass { get; set; } = true;

    void AddEdgeRoads()
    {
        if (!EdgeRoadPass) return;
        // the user's report 2026-11-16 (Darobe-Land 2986 W 3465 N): both side exits of a T gateway got their own road to the edge,
        // two roads ran side by side out of the map – at most one road to the edge per town, and their ends at least EdgeRoadSpacing apart
        var towns = new HashSet<NetTown>();
        var ends = new List<(int Side, Point P)>();
        foreach (var r in plan.Roads.Where(r => r.ToEdge && r.Points.Count > 0))
        {
            if (r.From?.Town != null) towns.Add(r.From.Town);
            ends.Add((EdgeOf(r.Points[^1].X, r.Points[^1].Y).Side, r.Points[^1]));
        }
        foreach (var e in plan.Towns.Where(t => !t.Node && t.Connected).SelectMany(t => t.Exits).Where(Open).OrderBy(e => EdgeOf(e.X, e.Z).Dist).ToList())
        {
            if (!Open(e)) continue;
            var (side, dist) = EdgeOf(e.X, e.Z);
            if (dist > EdgeRoadReach) continue;
            if (towns.Contains(e.Town)) { Log?.Invoke($"edge road from {e.X:0}/{e.Z:0}: town has one already"); continue; }
            allowed = new HashSet<NetExit> { e };
            heur = null;
            var start = ExitTurns(DirOf(e.DX, e.DZ), 4).Select(t => (e.Cell, t.Dir, t.Extra)).ToList();
            // less straight (the user's request 2026-11-14: "a bit more restless"): the road meets the edge a little to one side
            // (30–60 % of the distance), so it runs out at a slant with curves; if there is no way there, anywhere on this side
            double along = side is 0 or 2 ? e.X : e.Z;
            double target = Math.Clamp(along + (rnd.Next(2) == 0 ? -1 : 1) * dist * (0.3 + 0.3 * rnd.NextDouble()), -half + 300, half - 300);
            int found = Search(start, s => AtEdge(s, side, target), false, (float)(4 * EdgeRoadReach + 800));
            if (found < 0 || Curly(found)) found = Search(start, s => AtEdge(s, side), false, (float)(4 * EdgeRoadReach + 800));
            if (found < 0) { Log?.Invoke($"edge road from {e.X:0}/{e.Z:0}: edge not reached"); continue; }
            var cells = Trace(found);
            double len = 0;
            for (int q = 1; q < cells.Count; q++) len += (Center(cells[q]) - Center(cells[q - 1])).Length;
            if (Curly(found) || len > 2.5 * dist + 300)
            {
                Log?.Invoke($"edge road from {e.X:0}/{e.Z:0}: {(Curly(found) ? "curls" : $"too long ({len:0} m for {dist:0} m)")}");
                continue;
            }
            var lastCell = Center(cells[^1]);
            if (ends.Any(p => p.Side == side && (p.P - lastCell).Length < EdgeRoadSpacing))
            {
                Log?.Invoke($"edge road from {e.X:0}/{e.Z:0}: another road reaches the edge close by");
                continue;
            }
            AddEdgeRoad(e, cells, side);
            towns.Add(e.Town);
            ends.Add((side, plan.Roads[^1].Points[^1]));
            Log?.Invoke($"edge road from {e.X:0}/{e.Z:0}: {len:0} m to side {side}");
        }
    }

    /// junctions of the last pass at least this far apart (m; normally NodeSpacing)
    const double RelaxedNodeSpacing = 120;

    /// last pass (the user's wish 2026-11-16: "the rules must be relaxed, much should happen automatically" – after the stricter
    /// passes exits were left without a road, e.g. the second side of a T gateway next to its road to the edge): every town exit
    /// that is still free gets a road to the nearest sensible place – a junction on any road (also the own access, also close to
    /// the town, junctions only 120 m apart, steeper places allowed) or a free exit of another town; no detour required.
    /// Only what really finds no way (built over, water, rock walls all around) stays a dead end.
    void ConnectRemaining()
    {
        var open = plan.Towns.Where(t => !t.Node && t.Connected).SelectMany(t => t.Exits).Where(Open).ToList();
        foreach (var e in open)
        {
            if (!Open(e)) continue;
            var start = ExitTurns(DirOf(e.DX, e.DZ), 4).Select(t => (e.Cell, t.Dir, t.Extra)).ToList();
            bool gravel = e.Town.OldWest || e.Gravel;
            // first without crossing another road (the user's hint 2026-11-20: a feeder crossed the road right in front of the
            // town and joined the next one behind it – it should join the crossed road instead); crossing only as a last resort
            bool ok = false;
            foreach (bool noCross in new[] { true, false })
                if (ok = TryLoop(start, e.Town.Id, 0, 0, e, null, maxLen: 1500, tapsOnly: gravel, townGap: 30, maxCost: 16000, gravel: gravel,
                          net: ExitDistances(e), relaxed: true, noCross: noCross)
                      || TryLoop(start, e.Town.Id, 0, 0, e, null, maxLen: 3500, tapsOnly: gravel, townGap: 30, maxCost: 40000, gravel: gravel,
                          net: ExitDistances(e), relaxed: true, noCross: noCross)) break;
            if (ok) { plan.Roads[^1].Loop = false; plan.Roads[^1].Feeder = true; }
            Log?.Invoke($"last pass from {e.X:0}/{e.Z:0}: " + (ok ? $"{plan.Roads[^1].Length:0} m to {plan.Roads[^1].Points[^1].X:0}/{plan.Roads[^1].Points[^1].Y:0}" : "no way"));
        }
    }

    /// for tests: run RepairDeadEnds on a given plan
    internal int RepairDeadEnds(NetworkPlan p)
    {
        plan = p;
        BuildGrid();
        graph.Clear();
        foreach (var _ in plan.Towns) graph.Add(new());
        foreach (var r in plan.Roads)
        {
            r.Samples ??= Resample(r.Points, 16);
            r.Nodes = new int[r.Samples.Count];
            for (int k = 0; k < r.Samples.Count; k++) r.Nodes[k] = NewNode();
        }
        return RepairDeadEnds();
    }

    /// post-planning check & repair: if a town exit stayed unused (dead end) because a planned road passed close in front
    /// of it and blocked its 44 m approach cell, connect the exit directly to that passing road with a short, smooth
    /// T-junction – provided the terrain height difference, grade, angle and space fit
    internal int RepairDeadEnds()
    {
        if (plan.Roads.Count == 0) return 0;
        int repaired = 0;
        var dead = plan.Towns.Where(t => !t.Node && t.Connected).SelectMany(t => t.Exits).Where(e => !e.Used && !e.Street).ToList();
        foreach (var e in dead)
        {
            if (e.Used) continue;
            var origin = new Point(e.X, e.Z);
            var dir = new Vector(e.DX, e.DZ);
            double h0 = e.Tile != null ? e.Tile.TerrainHeight : w.HeightAt((int)Math.Floor(e.X), (int)Math.Floor(e.Z));
            bool gravel = e.Town.OldWest || e.Gravel;
            (NetRoad Road, int K, Point P, List<Point> Line, double Score) best = default;
            foreach (var r in plan.Roads)
            {
                if (!gravel && r.Gravel) continue;
                var s = r.Samples;
                for (int k = 2; k + 2 < s.Count; k++)
                {
                    var p = s[k];
                    var v = p - origin;
                    double len = v.Length;
                    if (len < 28 || len > 180) continue;
                    v /= len;
                    double forward = Vector.Multiply(v, dir);
                    if (forward < 0.77) continue;   // within ±40° of straight ahead
                    var rd = s[k + 2] - s[k - 2];
                    if (rd.Length < 1) continue;
                    rd.Normalize();
                    if (Math.Abs(Vector.Multiply(v, rd)) > 0.64) continue;   // meets the passing road at 50°–130°
                    Vector a = s[k] - s[k - 2], b = s[k + 2] - s[k];
                    if (a.Length < 1 || b.Length < 1 || Math.Abs(Vector.AngleBetween(a, b)) > 40) continue;
                    if (nodesUsed.Any(q => (q - p).Length < 50)) continue;
                    // height and grade: only connect directly where the passing road is at a compatible height and not on a steep ramp
                    double h1 = w.HeightAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
                    double rise = Math.Abs(h1 - h0);
                    double maxRise = len < 40 ? Math.Min(4.0, len * spec.MaxGrade) : len * spec.MaxGrade;
                    if (rise > maxRise) continue;
                    if (Math.Abs(Sample(hb, s[k + 2].X, s[k + 2].Y) - Sample(hb, s[k - 2].X, s[k - 2].Y)) > 1.6 * JunctionGrade * (s[k + 2] - s[k - 2]).Length) continue;
                    double score = len + 120 * (rise / Math.Max(1, len * spec.MaxGrade)) + 40 * (1 - forward);
                    if (best.Road != null && score >= best.Score) continue;
                    var line = forward >= 0.985 ? StraightLine(origin, p) : Curve(origin, dir, p, v);
                    if (!DeadEndLinkFree(line, e.Tile, h0, h1, r)) continue;
                    best = (r, k, p, line, score);
                }
            }
            if (best.Road == null) continue;
            var tap = new Tap(best.Road, best.K, best.P, -1, 0, DirOf(-(best.P - origin).X, -(best.P - origin).Y));
            var road = new NetRoad
            {
                Points = best.Line, Gravel = gravel, Width = gravel ? Math.Clamp(e.Width, 6, 12) : 16,
                From = e, Feeder = true, Junction = best.P, EndRoad = best.Road,
            };
            e.Used = true;
            plan.Junctions.Add(best.P);
            nodesUsed.Add(best.P); nodesUsed.Add(origin);
            road.Samples = Resample(best.Line, 16);
            road.Length = road.Samples.Count > 1 ? (road.Samples.Count - 1) * 16.0 : 0;
            plan.Roads.Add(road);
            BlockAlong(best.Line);
            AddToGraph(road, tap, new(), null);
            repaired++;
            Log?.Invoke($"dead-end repair from {e.X:0}/{e.Z:0}: {road.Length:0} m to {best.P.X:0}/{best.P.Y:0}");
        }
        return repaired;
    }

    static List<Point> StraightLine(Point a, Point b)
    {
        double len = (b - a).Length;
        int steps = Math.Max(2, (int)Math.Ceiling(len / 12));
        var line = new List<Point>(steps + 1);
        for (int q = 0; q <= steps; q++) line.Add(a + (b - a) * (q / (double)steps));
        return line;
    }

    /// is a short dead-end connector free of water, existing roads, buildings/tiles (except ownTile), other planned roads
    /// and steep terrain bumps?
    bool DeadEndLinkFree(List<Point> line, PlacedPrefab ownTile, double h0, double h1, NetRoad target)
    {
        double total = LineLength(line);
        if (total < 1) return false;
        double walked = 0;
        var samples = Resample(line, 2);
        for (int q = 0; q < samples.Count; q++)
        {
            if (q > 0) walked += (samples[q] - samples[q - 1]).Length;
            if (walked < 4 || total - walked < 8) continue;
            var pt = samples[q];
            int ix = (int)Math.Floor(pt.X), iz = (int)Math.Floor(pt.Y);
            if (w.WaterAt(ix, iz) || w.RoadAt(ix, iz) != 0) return false;
            if (Math.Abs(w.HeightAt(ix, iz) - (h0 + (h1 - h0) * (walked / total))) > 6) return false;
        }
        var prefabs = w.Prefabs.Where(p => p.Def != null && p != ownTile).ToList();
        var check = Resample(line, 4);
        double w2 = 0;
        for (int q = 0; q < check.Count; q++)
        {
            if (q > 0) w2 += (check[q] - check[q - 1]).Length;
            if (w2 < 6) continue;
            var pt = check[q];
            var tang = q + 1 < check.Count ? check[q + 1] - pt : pt - check[q - 1];
            if (tang.Length < 1e-6) continue;
            tang.Normalize();
            var nv = new Vector(-tang.Y, tang.X);
            foreach (var pf in prefabs)
            {
                double m = pf.IsTile ? 10 : 4;
                for (int side = -1; side <= 1; side++)
                {
                    var probe = pt + nv * (side * 9);
                    if (probe.X > pf.X - m && probe.X < pf.X + pf.SizeX + m && probe.Y > pf.Z - m && probe.Y < pf.Z + pf.SizeZ + m) return false;
                }
            }
            if (w2 >= 10 && total - w2 >= 18)
                foreach (var o in plan.Roads)
                {
                    if (o == target) continue;
                    for (int k = 0; k + 1 < o.Points.Count; k++)
                        if (SegDist(pt, o.Points[k], o.Points[k + 1]) < 24) return false;
                }
        }
        return true;
    }

    /// nearest map edge of a point: side 0 south, 1 east, 2 north, 3 west and the distance to it
    (int Side, double Dist) EdgeOf(double x, double z)
    {
        var d = new[] { z + half, half - x, half - z, x + half };
        int side = Array.IndexOf(d, d.Min());
        return (side, d[side]);
    }

    static (double X, double Z) Outward(int side) => side switch { 0 => (0, -1), 1 => (1, 0), 2 => (0, 1), _ => (-1, 0) };

    /// goal of the search: a passable cell next to the blocked edge strip of this side, driving roughly towards the edge
    bool AtEdge(int state, int side, double? target = null)
    {
        var c = Center(state / Dirs);
        var (s, d) = EdgeOf(c.X, c.Y);
        if (s != side || d > 100 + 2 * C) return false;
        if (target is double t && Math.Abs((side is 0 or 2 ? c.X : c.Y) - t) > 80) return false;
        var (ox, oz) = Outward(side);
        return DirDiff(state % Dirs, DirOf(ox, oz)) <= 1;
    }

    /// enter the road: exit → cells → straight out to the edge (free end, 2 m inside the map)
    void AddEdgeRoad(NetExit exit, List<int> cells, int side)
    {
        var pts = new List<Point>();
        var fixedAt = new List<bool>();
        void Add(Point p, bool f) { pts.Add(p); fixedAt.Add(f); }
        var exitPoint = new Point(exit.X, exit.Z);
        Add(exitPoint, true);
        Add(exitPoint + new Vector(exit.DX, exit.DZ) * 24, true);
        Add(exit.Approach, false);
        var crossings = new List<(Point P, NetRoad Road, int K)>();
        var bridges = new List<BridgeSite>();
        for (int q = 0; q < cells.Count; q++)
        {
            var c = Center(cells[q]);
            if (q > 0 && jumps.TryGetValue(cells[q - 1], out var list) && list.FirstOrDefault(j => j.To == cells[q]) is var jp && jp.Cost > 0
                && !IsNeighbour(cells[q - 1], cells[q]))
            {
                if (AddBridgePoints(cells, q, jp.K, pts, fixedAt, bridges)) continue;   // bridge over a river
                fixedAt[^1] = true;
                Add(jp.Mid, true);
                Add(c, true);
                if (jp.Road != null) crossings.Add((jp.Mid, jp.Road, jp.K));
                else Log?.Invoke($"street crossing at {jp.Mid.X:0}/{jp.Mid.Y:0}");
                continue;
            }
            if ((c - exit.Approach).Length < 14) continue;
            Add(c, false);
        }
        // straight out: from the last cell at a right angle to the edge
        var last = Center(cells[^1]);
        var (ox, oz) = Outward(side);
        double lim = half - 2;
        Point edgePoint = side switch { 0 => new Point(last.X, -lim), 1 => new Point(lim, last.Y), 2 => new Point(last.X, lim), _ => new Point(-lim, last.Y) };
        double out0 = (edgePoint - last).Length;
        Add(edgePoint - new Vector(ox, oz) * Math.Min(60, out0 * 0.5), true);
        Add(edgePoint, true);
        var line = KeepOffEdge(Smooth(pts, fixedAt, new Random(new GeneratorSpec { Seed = spec.Seed + "/meander/" + plan.Roads.Count }.SeedValue), (x, z) => Sample(hb, x, z), MeanderStyle.Restless),
            out0 + 20);
        bool gravel = exit.Town.OldWest || exit.Gravel;
        var r = new NetRoad { Points = line, Gravel = gravel, Width = gravel ? Math.Clamp(exit.Width, 6, 12) : 16, From = exit, ToEdge = true };
        r.Bridges.AddRange(bridges);
        exit.Used = true;
        foreach (var (p, cr, _) in crossings) { r.Crossings.Add(p); r.CrossedRoads.Add(cr); plan.CrossingPoints.Add(p); nodesUsed.Add(p); }
        nodesUsed.Add(line[0]);
        r.Samples = Resample(line, 16);
        r.Length = r.Samples.Count > 1 ? (r.Samples.Count - 1) * 16.0 : 0;
        plan.Roads.Add(r);
        BlockAlong(line);
        AddToGraph(r, null, crossings, null);
    }
}
