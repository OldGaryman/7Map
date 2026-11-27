using System.Windows;

namespace MapEditor.Map;

/// a gateway tile on a route (the user's request 2026-11-17): T at an end on an existing road, X where the route crosses an existing
/// road
public sealed class RouteTile
{
    public PlacedPrefab Tile;
    public string Kind;            // "T", "X"
    public NetExit In, Out;        // exit towards the start / towards the end of the route (null at a T at the start/end)
    public double Along;           // place along the route (m) – order of the tiles
    public List<NetRoad> Connectors = new();      // pieces that join the existing road to the tile
    public List<TraceResult> OldPieces = new();   // surface of the existing road that the tile and its connectors replace
}

public sealed partial class RoadNetwork
{
    const double TileAxisSlack = 40;      // degrees: the road must run this close to a tile axis (obliquely: led onto the axis in a curve)
    const double ConnectorLength = 60;    // connector from the old road to the tile exit, at least (m)
    const double MaxConnectorReach = 420; // the connector starts at most this far from the junction (m)
    const double MaxConnectorSlope = 0.73;   // tan 36°: the curve from the old road to the tile exit leaves the tile axis at most this steeply

    static string StopText(string stop) => stop == "junction" ? Loc.T("Another road joins the existing road too close to this place.")
        : stop == "tile" ? Loc.T("A town tile is too close to this place.") : Loc.T("The existing road ends too close to this place.");

    /// T or X tile where the route meets an existing road: at an end (end on a street: T, the route arrives at the side towards `from`)
    /// or where it crosses one (X). The existing road must run along a tile axis, straight and flat for about 135 m to both sides.
    public RouteTile StreetTile(Point at, Vector streetDir, double streetHalf, Point routeSide, bool crossing, out string problem)
    {
        problem = null;
        if (TileDefs == null) { problem = Loc.T("The tiles of the game were not found."); return null; }
        var dir = streetDir; dir.Normalize();
        if (RoadFollower.Axis(routeScan, at, Math.Max(12, streetHalf * 3)) is Vector wide) dir = wide;   // reliable direction (see WideSpot)
        dir.Normalize();
        var (axisX, off) = AxisOf(dir);
        if (off > TileAxisSlack) { problem = Loc.F("The road runs {0:0}° off north–south or east–west here; a tile only fits up to {1:0}°.", off, TileAxisSlack); return null; }
        var axis = axisX ? new Vector(1, 0) : new Vector(0, 1);
        if (dir * axis < 0) dir = -dir;
        // to both sides: the tile edge (73 m out along the axis) and the start of the connector. A road that runs obliquely (up to
        // TileAxisSlack) is led onto the tile axis in a gentle curve: the connector starts where the old road is at most MaxConnectorSlope m
        // sideways per metre in front of the tile (an S curve for an oblique road) (user's request 2026-11-17: no tile could be placed on a road 29° off the axis)
        var across = new Vector(-axis.Y, axis.X);
        var ends = new List<Point>();
        var edges = new List<Point>();
        var paths = new List<List<Point>>();
        foreach (int sg in new[] { 1, -1 })
        {
            var f = RoadFollower.Extend(routeScan, at, dir * sg, MaxConnectorReach + 4, streetHalf * 2, true);
            var edge = f.Points.FirstOrDefault(p => (p - at) * (axis * sg) >= 73);
            if (edge == default) { problem = StopText(f.Stop); return null; }
            edges.Add(edge);
            paths.Add(f.Points);
        }
        double shift = Math.Clamp((((edges[0] - at) * across) + ((edges[1] - at) * across)) / 2, -20, 20);
        for (int k = 0; k < 2; k++)
        {
            int sg = k == 0 ? 1 : -1;
            var end = paths[k].FirstOrDefault(p =>
            {
                double ax = (p - at) * (axis * sg) - 75, lat = Math.Abs((p - at) * across - shift);
                return ax >= ConnectorLength && lat <= MaxConnectorSlope * ax;   // the curve to the tile turns at most about 36° away from the axis
            });
            if (end == default)
            {
                double got = paths[k].Count > 0 ? (paths[k][^1] - at).Length : 0;
                problem = got < 200 ? StopText(RoadFollower.Extend(routeScan, at, dir * sg, MaxConnectorReach + 4, streetHalf * 2, true).Stop) : Loc.T("The existing road runs too obliquely or bends too much here: it cannot be led onto the tile in a gentle curve within 420 m.");
                return null;
            }
            ends.Add(end);
        }
        double H(Point p) => w.HeightAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
        if (Math.Abs(H(edges[0]) - H(edges[1])) / 146 > 0.05) { problem = Loc.T("The existing road is too steep here (more than 5 % over the tile)."); return null; }
        double hAt = (H(at) + H(edges[0]) + H(edges[1])) / 3;   // floor of the tile: the road in the middle and at both edges
        // the connectors must reach the old road without getting steeper than 10 %
        if (ends.Any(p => Math.Abs(H(p) - hAt) > 0.10 * Math.Max(ConnectorLength, (p - at).Length - 75))) { problem = Loc.T("The existing road rises or falls too much beside the tile."); return null; }
        var center = at + across * shift;
        int x0 = (int)Math.Round(center.X - 75), z0 = (int)Math.Round(center.Y - 75);
        if (!TileGroundOk(x0, z0, out problem, allowRoads: true)) return null;
        // sides: the street leaves on two opposite sides, the route on the side of routeSide (T) or both others (X)
        int sideA = SideOf(dir), sideB = (sideA + 2) % 4;
        var nrm = new Vector(-axis.Y, axis.X);
        int routeIn = SideOf((routeSide - at) * nrm >= 0 ? nrm : -nrm);
        int mask = 1 << sideA | 1 << sideB | 1 << routeIn;
        if (crossing) mask |= 1 << ((routeIn + 2) % 4);
        string name = crossing ? NodeX : NodeT;
        var tile = NodeTile(name, TileDefs(name), x0, z0, mask);
        if (tile == null) { problem = Loc.T("No rotation of the tile fits these roads."); return null; }
        tile.Y = (int)Math.Round(hAt + 0.85) + tile.Def.YOffset;   // at the height of the existing road
        var rt = new RouteTile { Tile = tile, Kind = crossing ? "X" : "T" };
        w.TileSurface(tile, out var exits);
        NetExit ExitOn(int side)
        {
            foreach (var (x, z) in exits)
                if (SideOfExit(tile, x, z) == side)
                {
                    var (dx, dz) = Outward(tile, x, z);
                    int width = w.TileExitWidth(tile, x, z).Width;
                    return new NetExit { Tile = tile, X = x, Z = z, DX = dx, DZ = dz, Width = width > 0 ? width : 16, Gateway = true };
                }
            return null;
        }
        // connectors: from the old road (60 m before the tile) to the exits on the street sides
        foreach (var (side, endP) in new[] { (sideA, ends[0]), (sideB, ends[1]) })
        {
            var te = ExitOn(side);
            if (te == null) { problem = Loc.T("No rotation of the tile fits these roads."); return null; }
            var spot0 = RoadFollower.Center(routeScan, endP);
            var spot = spot0 == null ? null : WideSpot(spot0);
            if (spot == null) { problem = Loc.T("The existing road ends too close to this place."); return null; }
            var tdir = new Point(te.X, te.Z) - spot.Center; tdir.Normalize();
            var sDir = spot.Dir; sDir.Normalize();
            var from = new NetExit
            {
                Street = true, X = spot.Center.X, Z = spot.Center.Y, StreetDX = sDir.X, StreetDZ = sDir.Y, StreetHalf = spot.Width / 2 + 1,
                Width = (int)Math.Round(Math.Max(3, spot.Width)), Gravel = spot.Kind == WorldData.Gravel, DX = tdir.X, DZ = tdir.Y,
            };
            var line = Curve(spot.Center, sDir * (sDir * tdir >= 0 ? 1 : -1), new Point(te.X, te.Z), new Vector(-te.DX, -te.DZ));
            // the curve leaves the old road (oblique road): nothing may stand in its way
            var blockers = w.PrefabSnapshot().Where(p => p.Def != null && p.Def.Kind != PrefabKind.Part && p != tile).ToList();
            foreach (var q in line.Where((_, i) => i % 2 == 0))
            {
                if (w.WaterAt((int)Math.Floor(q.X), (int)Math.Floor(q.Y))) { problem = Loc.T("There is water where the road would be led to the tile."); return null; }
                var b = blockers.FirstOrDefault(p => q.X > p.X - 4 && q.X < p.X + p.SizeX + 4 && q.Y > p.Z - 4 && q.Y < p.Z + p.SizeZ + 4);
                if (b != null) { problem = Loc.F("{0} is in the way where the road would be led to the tile.", b.Name); return null; }
            }
            var road = new NetRoad { Points = line, Width = Math.Max(te.Width, from.Width), Gravel = false, Manual = true, From = from, To = te };
            Remeasure(road);
            rt.Connectors.Add(road);
        }
        // old surface between the two connector starts (it is removed; the tile and the connectors replace it)
        var old = RoadTracer.Trace(w, rt.Connectors[0].Points[0], rt.Connectors[1].Points[0], out problem);
        if (old == null) return null;
        rt.OldPieces.Add(old);
        rt.In = ExitOn(routeIn);
        if (crossing) rt.Out = ExitOn((routeIn + 2) % 4);
        return rt;
    }

    /// like StreetTile, but if it does not fit at `at`, the nearest place along the existing road (up to 360 m to both sides) where it
    /// fits; moved = how far the junction moved (0 = at the place itself)
    public RouteTile StreetTileNear(Point at, Vector streetDir, double streetHalf, Point routeSide, bool crossing, out string problem, out double moved)
    {
        moved = 0;
        var rt = StreetTile(at, streetDir, streetHalf, routeSide, crossing, out problem);
        if (rt != null || crossing) return rt;
        string first = problem;
        var dir = streetDir; dir.Normalize();
        var sideA = RoadFollower.Extend(routeScan, at, dir, 364, streetHalf * 2, false).Points;
        var sideB = RoadFollower.Extend(routeScan, at, -dir, 364, streetHalf * 2, false).Points;
        for (int k = 20; k < Math.Max(sideA.Count, sideB.Count); k += 20)   // every 40 m
            foreach (var pts in new[] { sideA, sideB })
            {
                if (k >= pts.Count) continue;
                var spot0 = RoadFollower.Center(routeScan, pts[k]);
                if (spot0 == null) continue;
                var spot = WideSpot(spot0);
                rt = StreetTile(spot.Center, spot.Dir, spot.Width / 2 + 1, routeSide, false, out _);
                if (rt != null) { moved = k * 2; problem = null; return rt; }
            }
        problem = first;
        return null;
    }

    /// is the tile area free (no building, no water; roads only where allowRoads) and not too uneven?
    bool TileGroundOk(int x0, int z0, out string problem, bool allowRoads)
    {
        problem = null;
        if (x0 < -half + 150 || z0 < -half + 150 || x0 + 150 > half - 150 || z0 + 150 > half - 150) { problem = Loc.T("Too close to the map edge for a tile."); return false; }
        var pf = w.PrefabSnapshot().FirstOrDefault(p => p.Def != null && p.X < x0 + 160 && p.X + p.SizeX > x0 - 10 && p.Z < z0 + 160 && p.Z + p.SizeZ > z0 - 10);
        if (pf != null) { problem = Loc.F("{0} is in the way of the tile.", pf.Name); return false; }
        for (int z = z0; z <= z0 + 150; z += 6)
            for (int x = x0; x <= x0 + 150; x += 6)
            {
                if (w.WaterAt(x, z)) { problem = Loc.T("There is water where the tile would go."); return false; }
                if (!allowRoads && w.RoadAt(x, z) != 0) { problem = Loc.T("An existing road is in the way of the tile."); return false; }
            }
        var (dev, worst) = Flatness(x0, z0);
        if (dev > 4 || worst > 12) { problem = Loc.F("The ground is too uneven for a tile here (up to {0:0} m off).", worst); return false; }
        return true;
    }

    /// the nearer tile axis to a direction: (x axis?, degrees off it)
    static (bool AxisX, double Off) AxisOf(Vector d)
    {
        double a = Math.Abs(Math.Atan2(d.Y, d.X) * 180 / Math.PI) % 180;   // 0 … 180
        double offX = Math.Min(a, 180 - a), offZ = Math.Abs(90 - a);
        return offX <= offZ ? (true, offX) : (false, offZ);
    }

    /// side of a tile a direction leaves it (0 south, 1 east, 2 north, 3 west)
    static int SideOf(Vector d) => Math.Abs(d.X) >= Math.Abs(d.Y) ? (d.X > 0 ? 1 : 3) : (d.Y > 0 ? 2 : 0);

    /// smooth curve from a (leaving along da) to b (arriving along db), a point every 4 m
    static List<Point> Curve(Point a, Vector da, Point b, Vector db)
    {
        double dist = (b - a).Length, k = dist * 0.4;
        Point c1 = a + da * k, c2 = b - db * k;
        var line = new List<Point>();
        int steps = Math.Max(4, (int)Math.Ceiling(dist / 4));
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps, u = 1 - t;
            line.Add(new Point(u * u * u * a.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * b.X,
                               u * u * u * a.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * b.Y));
        }
        return line;
    }

    /// The route split at its tiles: s → first tile, tile → tile, … → e, each piece searched in the style of the route (through the via
    /// points that lie in its stretch). The tiles are obstacles for the search except in front of their exits. null + reason.
    public List<NetRoad> RouteThroughTiles(NetEnd s, NetEnd e, RouteVariant v, List<RouteTile> tiles, out string problem)
    {
        problem = null;
        var res = new List<NetRoad>();
        var saved = (bool[])blocked.Clone();
        try
        {
            foreach (var t in tiles)
            {
                Mark(blocked, t.Tile.X, t.Tile.Z, t.Tile.X + t.Tile.SizeX, t.Tile.Z + t.Tile.SizeZ, 24);
                foreach (var x in new[] { t.In, t.Out }.Where(x => x != null))
                    for (double d = 4; d <= ApproachLength + 20; d += 4)
                        for (double l = -16; l <= 16; l += 8)
                        {
                            int c = CellOf(x.X + x.DX * d - x.DZ * l, x.Z + x.DZ * d + x.DX * l);
                            if (c >= 0 && !saved[c]) blocked[c] = false;
                        }
            }
            var order = tiles.OrderBy(t => t.Along).ToList();
            var cur = s;
            double fromAlong = 0;
            var line = v.Road.Points;
            for (int k = 0; k <= order.Count; k++)
            {
                NetEnd next;
                double toAlong;
                if (k < order.Count)
                {
                    var t = order[k];
                    if (t.In == null) { cur = new NetEnd(t.Out, null, new Point(t.Out.X, t.Out.Z), new Vector(t.Out.DX, t.Out.DZ)); fromAlong = t.Along; continue; }   // T at the start
                    next = new NetEnd(t.In, null, new Point(t.In.X, t.In.Z), new Vector(t.In.DX, t.In.DZ));
                    toAlong = t.Along;
                }
                else { next = e; toAlong = double.MaxValue; }
                var vias = v.Via.Where(p => { double a = Project(line, p).Along; return a > fromAlong + 60 && a < toAlong - 60; }).ToList();
                var piece = RouteOf(Orient(cur, next.P), Orient(next, cur.P), vias, v.Style, v.Road.Gravel, v.Road.Width, out problem);
                if (piece == null) return null;
                res.Add(piece.Road);
                if (k < order.Count)
                {
                    var t = order[k];
                    if (t.Out == null) return res;   // T at the end
                    cur = new NetEnd(t.Out, null, new Point(t.Out.X, t.Out.Z), new Vector(t.Out.DX, t.Out.DZ));
                    fromAlong = t.Along;
                }
            }
            return res;
        }
        finally { blocked = saved; }
    }
}
