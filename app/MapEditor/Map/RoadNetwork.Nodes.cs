using System.Windows;

namespace MapEditor.Map;

/// Node tiles (the user's request 2026-11-13, step 1 of 3: plan only): like in the game 3.4, where single gateway tiles stand
/// between the towns as road crossings (East Nipabu: 4 × rwg_tile_gateway_t with three country roads and 7 straight gateways,
/// on level ground, 300–600 m from the next town).
/// 1. Before the network is planned, level free places are chosen around which towns lie in at least three directions
///    (four: crossing). There a planned tile is put – T (rwg_tile_gateway_t, open towards the towns) or crossing
///    (rwg_tile_gateway_intersection) – as a small "town" with free exits.
/// 2. The planner connects it like a town; once connected, its free exits are cheap starting points for the next towns
///    (cheaper than a new junction), so roads run through it.
/// 3. Afterwards per tile: all exits used = stays; crossing with three = T; two opposite = straight gateway; two at a corner =
///    the tile is dropped and the two roads are joined; one or none = the tile (and its stub road) is dropped.
/// The tiles are not on the map yet (NetworkPlan.NodeTiles); building them is step 2.
public sealed partial class RoadNetwork
{
    /// description of a tile by name (game folder); set by the main window and the tests. Also used when a saved plan is loaded.
    public static Func<string, PrefabDef> TileDefs { get; set; }

    public const string NodeT = "rwg_tile_gateway_t", NodeX = "rwg_tile_gateway_intersection";
    /// straight gateways for a node with two opposite roads (with a 60 × 60 lot)
    public static readonly string[] NodeStraight = { "rwg_tile_gateway_straight", "rwg_tile_gateway_straight2", "rwg_tile_gateway_straight4" };
    public static IEnumerable<string> NodeTileNames => NodeStraight.Append(NodeT).Append(NodeX);

    const double NodeTownGap = 300;      // tile at least this far from towns (m; game: about 300 m)
    const double NodeTownFar = 3200;     // towns up to this far count as "around" the place
    const double NodeTownNear = 550;     // … and at least this far
    const double NodeSpacingMin = 1500;  // node tiles at least this far apart (m)

    /// test output: how often a place failed at which check
    public static Dictionary<string, int> NodeRejects { get; } = new();
    static void Rej(string reason) { lock (NodeRejects) NodeRejects[reason] = NodeRejects.GetValueOrDefault(reason) + 1; }

    // ---------------------------------------------------------------- 1. places before planning

    void PlaceNodeSites(IProgress<string> progress, NetworkPlan probe = null)
    {
        if (spec.NodeTiles <= 0 || TileDefs == null) return;
        var tDef = TileDefs(NodeT);
        var xDef = TileDefs(NodeX);
        if (tDef == null) return;
        progress?.Report(Loc.T("Looking for places for node tiles …"));
        var towns = plan.Towns.Where(t => !t.OldWest && !t.Node).ToList();
        if (towns.Count < 3) return;
        double km2 = w.Size / 1000.0 * (w.Size / 1000.0);
        int max = Math.Max(1, (int)Math.Round(km2 / (spec.NodeTiles >= 2 ? 7.0 : 14.0)));
        // first where the trial planning made junctions (T), crossings (X) and long straight stretches (I)
        if (probe != null) PlaceProbeSites(probe, max, tDef, xDef);
        int junctionTiles = plan.NodeTiles.Count(t => !NodeStraight.Contains(t.Name, StringComparer.OrdinalIgnoreCase));
        if (junctionTiles >= max) return;
        max -= junctionTiles;   // the rest with the older rule (towns in at least three directions)
        int before = plan.NodeTiles.Count;

        var sites = new List<(int X0, int Z0, int Missing, double Score)>();
        const int step = 75;
        for (int z0 = -half + 400; z0 + 150 <= half - 400; z0 += step)
            for (int x0 = -half + 400; x0 + 150 <= half - 400; x0 += step)
            {
                double cx = x0 + 75, cz = z0 + 75;
                // towns around: nearest town per side (0 south, 1 east, 2 north, 3 west), none too close
                var nearest = new double[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue };
                bool tooClose = false;
                foreach (var t in towns)
                {
                    double d = TownDistanceTo(t, new Point(cx, cz));
                    if (d < NodeTownGap + 75) { tooClose = true; break; }
                    if (d < NodeTownNear || d > NodeTownFar) continue;
                    double ang = Math.Atan2(t.CZ - cz, t.CX - cx) * 180 / Math.PI;   // east = 0, north = 90
                    int side = ang >= -45 && ang < 45 ? 1 : ang >= 45 && ang < 135 ? 2 : ang >= -135 && ang < -45 ? 0 : 3;
                    nearest[side] = Math.Min(nearest[side], d);
                }
                if (tooClose) { Rej("town near"); continue; }
                int sides = nearest.Count(d => d < double.MaxValue);
                if (sides < 3) { Rej("towns in fewer than three directions"); continue; }
                // free: no water, tile, building, map edge (blocked grid) and no existing surface on the tile and 40 m around it
                if (!NodeAreaFree(x0, z0)) { Rej("not free"); continue; }
                // level ground (the game puts them on flat land)
                var (dev, worst) = Flatness(x0, z0);
                if (dev > 2.5 || worst > 8) { Rej("ground not level"); continue; }
                int missing = sides == 4 && xDef != null ? -1 : Array.IndexOf(nearest, nearest.Max());
                double reach = nearest.Where(d => d < double.MaxValue).Sum() / 1000;
                sites.Add((x0, z0, missing, dev + 0.2 * worst + 0.6 * reach - (missing < 0 ? 1 : 0) + rnd.NextDouble()));
            }
        foreach (var s in sites.OrderBy(s => s.Score))
        {
            if (plan.NodeTiles.Count - before >= max) break;
            var c = new Point(s.X0 + 75, s.Z0 + 75);
            if (plan.NodeTiles.Any(t => (new Point(t.X + 75, t.Z + 75) - c).Length < NodeSpacingMin)) continue;
            bool cross = s.Missing < 0;
            var tile = NodeTile(cross ? NodeX : NodeT, cross ? xDef : tDef, s.X0, s.Z0, cross ? 0b1111 : 0b1111 & ~(1 << s.Missing));
            if (tile == null) { Rej("no rotation with these exits"); continue; }
            AddNodeTown(tile);
            Log?.Invoke($"node site {tile.X}/{tile.Z} {tile.Name} (rotation {tile.Rotation}), {plan.Towns[^1].Exits.Count(e => e.Cell >= 0)} reachable exits, {sites.Count} places in all");
        }
    }

    /// junction, crossing or straight stretch of the trial planning: place and the directions in which the roads leave it
    sealed record Knot(Point P, List<Vector> Arms, int Kind);   // Kind: 2 = straight (I), 3 = junction (T), 4 = crossing (X)

    const double ArmReach = 120;          // direction of a road arm measured over this length (m)
    const double ArmSlack = 35;           // an arm may deviate this much from the side of the tile it leaves (degrees)
    const double ProbeSpacing = 700;      // tiles from the trial planning at least this far apart (m)

    /// direction from the point at 'along' to the point ArmReach further (sign +1) or back (−1); null if the line is too short there
    static Vector? ArmOf(List<Point> line, double along, int sign)
    {
        double len = LineLength(line), to = along + sign * ArmReach;
        if (to < 0 || to > len) { if (Math.Abs(Math.Clamp(to, 0, len) - along) < 60) return null; to = Math.Clamp(to, 0, len); }
        var v = At(line, to).P - At(line, along).P;
        if (v.Length < 30) return null;
        v.Normalize();
        return v;
    }

    /// side of a tile an arm leaves (0 south, 1 east, 2 north, 3 west) and how many degrees it is off that side's direction
    static (int Side, double Off) SideOfArm(Vector v)
    {
        double ang = Math.Atan2(v.Y, v.X) * 180 / Math.PI;   // east = 0, north = 90
        int side = ang >= -45 && ang < 45 ? 1 : ang >= 45 && ang < 135 ? 2 : ang >= -135 && ang < -45 ? 0 : 3;
        double axis = side switch { 1 => 0, 2 => 90, 0 => -90, _ => 180 };
        double off = Math.Abs(((ang - axis) % 360 + 540) % 360 - 180);
        return (side, off);
    }

    /// knots of the trial planning: every junction (T), crossing (X) and straight stretches of long asphalt roads (I)
    static List<Knot> ProbeKnots(NetworkPlan probe)
    {
        var list = new List<Knot>();
        List<Vector> Both(NetRoad r, Point p)
        {
            double t = Project(r.Points, p).Along;
            var a = ArmOf(r.Points, t, 1); var b = ArmOf(r.Points, t, -1);
            return a is Vector va && b is Vector vb ? new List<Vector> { va, vb } : null;
        }
        foreach (var r in probe.Roads.Where(r => !r.Gravel && r.Points.Count > 1))
        {
            double len = LineLength(r.Points);
            if (r.StartRoad != null && Both(r.StartRoad, r.Points[0]) is List<Vector> s && ArmOf(r.Points, 0, 1) is Vector sa)
                list.Add(new Knot(r.Points[0], s.Append(sa).ToList(), 3));
            if (r.EndRoad != null)
            {
                var j = r.Junction ?? r.Points[^1];
                if (Both(r.EndRoad, j) is List<Vector> e && ArmOf(r.Points, Project(r.Points, j).Along, -1) is Vector ea)
                    list.Add(new Knot(j, e.Append(ea).ToList(), 3));
            }
            for (int k = 0; k < r.Crossings.Count && k < r.CrossedRoads.Count; k++)
                if (Both(r, r.Crossings[k]) is List<Vector> a && Both(r.CrossedRoads[k], r.Crossings[k]) is List<Vector> b)
                    list.Add(new Knot(r.Crossings[k], a.Concat(b).ToList(), 4));
            // straight stretches: every 150 m, not near the ends (towns, junctions)
            for (double t = 400; t <= len - 400; t += 150)
                if (Both(r, At(r.Points, t).P) is List<Vector> st && Vector.AngleBetween(st[0], -st[1]) is double bend && Math.Abs(bend) < 12)
                    list.Add(new Knot(At(r.Points, t).P, st, 2));
        }
        return list;
    }

    /// tiles at the knots of the trial planning: T at junctions, X at crossings (at most max of both), straight gateways (I) on
    /// long straight stretches (at most max as well); the place must be free, level and far enough from towns
    void PlaceProbeSites(NetworkPlan probe, int max, PrefabDef tDef, PrefabDef xDef)
    {
        var knots = ProbeKnots(probe);
        var towns = plan.Towns.Where(t => !t.Node).ToList();
        var cands = new List<(PlacedPrefab Tile, int Kind, double Score)>();
        foreach (var k in knots)
        {
            var sides = k.Arms.Select(SideOfArm).ToList();
            if (sides.Any(s => s.Off > (k.Kind == 2 ? 15 : ArmSlack))) { Rej("probe: road not along the tile axes"); continue; }
            int mask = sides.Aggregate(0, (m, s) => m | 1 << s.Side);
            if (System.Numerics.BitOperations.PopCount((uint)mask) != k.Arms.Count) { Rej("probe: two roads leave on one side"); continue; }
            if (k.Kind == 4 && xDef == null) continue;
            // the knot should lie near the middle of the tile; small shifts if the ground there is better
            (PlacedPrefab Tile, double Score)? best = null;
            for (int dz = -40; dz <= 40; dz += 20)
                for (int dx = -40; dx <= 40; dx += 20)
                {
                    int x0 = (int)Math.Round(k.P.X - 75 + dx), z0 = (int)Math.Round(k.P.Y - 75 + dz);
                    if (x0 < -half + 300 || z0 < -half + 300 || x0 + 150 > half - 300 || z0 + 150 > half - 300) continue;
                    var c = new Point(x0 + 75, z0 + 75);
                    if (towns.Any(t => TownDistanceTo(t, c) < NodeTownGap + 75)) { Rej("probe: town near"); continue; }
                    if (!NodeAreaFree(x0, z0)) { Rej("probe: not free"); continue; }
                    var (dev, worst) = Flatness(x0, z0);
                    if (dev > 2.5 || worst > 8) { Rej("probe: ground not level"); continue; }   // like the other node tiles (more gave steps at the tile corners)
                    double score = dev + 0.2 * worst + 0.01 * Math.Sqrt(dx * dx + dz * dz);
                    if (best != null && best.Value.Score <= score) continue;
                    PlacedPrefab tile;
                    if (k.Kind == 2)
                    {
                        var fits = NodeStraight.Select(nm => TileDefs(nm) is PrefabDef d ? NodeTile(nm, d, x0, z0, mask) : null).Where(t => t != null).ToList();
                        tile = fits.Count > 0 ? fits[rnd.Next(fits.Count)] : null;
                    }
                    else tile = k.Kind == 4 ? NodeTile(NodeX, xDef, x0, z0, 0b1111) : NodeTile(NodeT, tDef, x0, z0, mask);
                    if (tile == null) { Rej("probe: no rotation with these exits"); continue; }
                    best = (tile, score);
                }
            if (best != null) cands.Add((best.Value.Tile, k.Kind, best.Value.Score + rnd.NextDouble() - (k.Kind == 4 ? 1 : 0)));
        }
        int junctions = 0, straight = 0;
        // crossings and junctions first, then the straight ones (they need more room to the others)
        foreach (var (tile, kind, _) in cands.OrderBy(c => c.Kind == 2 ? 1 : 0).ThenBy(c => c.Score))
        {
            if (kind == 2 ? straight >= max : junctions >= max) continue;
            var c = new Point(tile.X + 75, tile.Z + 75);
            if (plan.NodeTiles.Any(t => (new Point(t.X + 75, t.Z + 75) - c).Length < ProbeSpacing)) continue;
            if (!NodeAreaFree(tile.X, tile.Z)) continue;   // a tile placed before blocks this place now
            AddNodeTown(tile);
            if (kind == 2) straight++; else junctions++;
            Log?.Invoke($"probe site {tile.X}/{tile.Z} {tile.Name} (rotation {tile.Rotation})");
        }
        Log?.Invoke($"probe: {knots.Count} knots, {cands.Count} places, {junctions} junction tiles, {straight} straight tiles");
    }

    bool NodeAreaFree(int x0, int z0)
    {
        for (double z = z0 - 40; z <= z0 + 190; z += C)
            for (double x = x0 - 40; x <= x0 + 190; x += C)
            {
                int c = CellOf(x, z);
                if (c < 0 || blocked[c] || roadCell[c]) return false;
            }
        if (spec.BreakInWasteland && w.GetWastelandDamage() is WastelandDamage dmg
            && dmg.HasBreakInRect(x0 - 10, z0 - 10, x0 + 160, z0 + 160, threshold: 175))
            return false;
        return true;
    }

    (double Dev, double Worst) Flatness(int x0, int z0)
    {
        double sum = 0; int cnt = 0;
        for (int z = z0 + 5; z < z0 + 150; z += 10) for (int x = x0 + 5; x < x0 + 150; x += 10) { sum += w.HeightAt(x, z); cnt++; }
        double mean = sum / cnt, dev = 0, worst = 0;
        for (int z = z0 + 5; z < z0 + 150; z += 10)
            for (int x = x0 + 5; x < x0 + 150; x += 10) { double d = Math.Abs(w.HeightAt(x, z) - mean); dev += d; worst = Math.Max(worst, d); }
        return (dev / cnt, worst);
    }

    // side of an exit point at the tile edge (0 south, 1 east, 2 north, 3 west)
    static int SideOfExit(PlacedPrefab t, double x, double z) =>
        Math.Abs(z - t.Z) < 0.6 ? 0 : Math.Abs(x - (t.X + t.SizeX)) < 0.6 ? 1 : Math.Abs(z - (t.Z + t.SizeZ)) < 0.6 ? 2 : 3;

    /// tile of this kind at x0/z0 in the rotation whose exits are exactly on the sides in mask; height from the ground
    PlacedPrefab NodeTile(string name, PrefabDef def, int x0, int z0, int mask)
    {
        if (def == null) return null;
        for (int r = 0; r < 4; r++)
        {
            var probe = new PlacedPrefab { Name = name, Def = def, X = x0, Z = z0, Rotation = r };
            if (w.TileSurface(probe, out var ex) == null) return null;
            int m = 0;
            foreach (var (x, z) in ex) m |= 1 << SideOfExit(probe, x, z);
            if (m != mask) continue;
            double sum = 0; int cnt = 0;
            for (int z = z0 + 5; z < z0 + probe.SizeZ; z += 10) for (int x = x0 + 5; x < x0 + probe.SizeX; x += 10) { sum += w.HeightAt(x, z); cnt++; }
            probe.Y = (int)Math.Round(sum / cnt + 0.85) + def.YOffset;
            return probe;
        }
        return null;
    }

    /// the tile as a small town with free exits (like FindTowns): forecourts reserved, tile blocked for other roads
    void AddNodeTown(PlacedPrefab tile)
    {
        var town = new NetTown { Id = plan.Towns.Count, X0 = tile.X, Z0 = tile.Z, X1 = tile.X + tile.SizeX, Z1 = tile.Z + tile.SizeZ, Node = true };
        town.Tiles.Add(tile);
        w.TileSurface(tile, out var ex);
        foreach (var (x, z) in ex)
        {
            var (dx, dz) = Outward(tile, x, z);
            int width = w.TileExitWidth(tile, x, z).Width;
            var e = new NetExit { Town = town, Tile = tile, X = x, Z = z, DX = dx, DZ = dz, Width = width > 0 ? width : 16, Gateway = true };
            int c = CellOf(e.Approach.X, e.Approach.Y);
            bool free = Passable(c);
            for (double d = 8; d < ApproachLength && free; d += 4)
            {
                int cc = CellOf(x + dx * d, z + dz * d);
                free = cc >= 0 && !blocked[cc] && !w.WaterAt((int)Math.Floor(x + dx * d), (int)Math.Floor(z + dz * d));
            }
            if (free) e.Cell = c;
            town.Exits.Add(e);
        }
        Mark(blocked, tile.X, tile.Z, tile.X + tile.SizeX, tile.Z + tile.SizeZ, 24);
        foreach (var e in town.Exits.Where(e => e.Cell >= 0))
        {
            blocked[e.Cell] = false;   // the approach cell in front of the exit stays usable
            exitList.Add(e);
            double px = -e.DZ, pz = e.DX;
            for (double d = 8; d <= 110; d += 4)
                for (double l = -16; l <= 16; l += 4)
                {
                    int c = CellOf(e.X + e.DX * d + px * l, e.Z + e.DZ * d + pz * l);
                    if (c < 0) continue;
                    if (d < 40) blocked[c] = false;   // the straight connecting piece runs through the margin of the tile
                    reserveOwner[c] = exitList.Count - 1;
                    if (d <= 36 && c != e.Cell) reserveNear[c] = true;
                }
        }
        foreach (var e in exitList) if (e.Cell >= 0) reserveNear[e.Cell] = false;
        plan.Towns.Add(town);
        plan.NodeTiles.Add(tile);
    }

    // ---------------------------------------------------------------- 2b. free exits of the node tiles

    /// every free exit of a connected node tile gets a road to the network (to a junction or a free town exit) – more generous than
    /// for town exits (no 700 m limit for the fork): a node tile is only worth something if roads really meet there
    /// (the user's hint 2026-11-13, Fosema-Land: a T with only one road stayed on the plan)
    /// tests: false = skip this pass (so that the adjustment of half-used tiles in FinishNodeTiles gets work)
    public static bool NodeExitPass { get; set; } = true;

    /// also the free exits of T and crossing gateways at a town edge (CitySpec.JunctionGateways; the user's hint 2026-11-14, Naciru-Land:
    /// both side exits of the crossing gateway at 2014/−3194 stayed without a road – in the game a country road starts at every free
    /// side of such a gateway, but for the feeders a road there was no shortcut in the network, so they were left out)
    static bool JunctionGateway(PlacedPrefab tile) => tile != null && (tile.Name.Equals(NodeT, StringComparison.OrdinalIgnoreCase)
        || tile.Name.StartsWith(NodeX, StringComparison.OrdinalIgnoreCase));

    void ConnectNodeExits()
    {
        if (!NodeExitPass) return;
        foreach (var e in plan.Towns.Where(t => t.Connected).SelectMany(t => t.Exits)
                     .Where(e => (e.Town.Node || JunctionGateway(e.Tile)) && Open(e)).OrderBy(_ => rnd.Next()).ToList())
        {
            if (!Open(e)) continue;
            var start = ExitTurns(DirOf(e.DX, e.DZ), 2).Select(t => (e.Cell, t.Dir, t.Extra)).ToList();
            // for a node tile, only connect a free exit if it is a real shortcut (otherwise FinishNodeTiles adapts or drops the tile)
            if (TryLoop(start, e.Town.Id, 1.15, 0, e, null, maxLen: 2500, townGap: DeadEndGap, maxCost: 14000, maxEarthwork: DeadEndEarthwork, noCross: true)
                || (!e.Town.Node && TryLoop(start, e.Town.Id, 0, 250, e, null, maxLen: 1200, townGap: DeadEndGap, maxCost: 7000, maxEarthwork: DeadEndEarthwork, noCross: true)))
            {
                plan.Roads[^1].Loop = false;
                plan.Roads[^1].Feeder = true;
            }
        }
    }

    // ---------------------------------------------------------------- editing by hand (step 3, the user's request 2026-11-14)

    /// after a road of the plan was deleted by hand: node tiles take the shape of the roads that still use them (crossing → T →
    /// straight gateway; at a corner or with one road the tile goes, its access is shortened) – the same rules as after planning.
    /// Returns the number of node tiles that changed or went.
    public static int AdaptNodeTiles(WorldData w, NetworkPlan plan, NetworkSpec spec)
    {
        if (plan.NodeTiles.Count == 0 || TileDefs == null) return 0;
        string before = string.Join("|", plan.NodeTiles.Select(t => $"{t.Name}/{t.X}/{t.Z}/{t.Rotation}"));
        // nothing to adapt: every exit of every node tile is still used
        if (plan.Towns.Where(t => t.Node).All(t => t.Exits.All(e => plan.Roads.Any(x => x.From == e || x.To == e)))) return 0;
        var r = new RoadNetwork(w, spec) { plan = plan };
        r.BuildGrid();   // joining two roads through a tile corner smooths the new line over the terrain grid (was null → crash 2026-11-19)
        r.FinishNodeTiles();
        var after = plan.NodeTiles.Select(t => $"{t.Name}/{t.X}/{t.Z}/{t.Rotation}").ToHashSet();
        return before.Split('|', StringSplitOptions.RemoveEmptyEntries).Count(k => !after.Contains(k));
    }

    /// Move a node tile by hand to x0/z0 (rotation stays): the place must be free and level enough, the roads of the tile are
    /// searched again to its exits (their far ends and via points stay). Changes the plan only on success; null = done,
    /// otherwise the reason.
    public string MoveNodeTile(NetworkPlan p, PlacedPrefab tile, int x0, int z0)
    {
        var town = p.Towns.FirstOrDefault(t => t.Node && t.Tiles.Contains(tile));
        if (town == null) return Loc.T("This is not a node tile of the plan.");
        int dx = x0 - tile.X, dz = z0 - tile.Z;
        if (dx == 0 && dz == 0) return null;
        int x1 = x0 + tile.SizeX, z1 = z0 + tile.SizeZ;
        if (x0 < -half + 200 || z0 < -half + 200 || x1 > half - 200 || z1 > half - 200) return Loc.T("Too close to the map edge.");
        foreach (var t in p.Towns.Where(t => t != town && !t.Node))
        {
            double ex = Math.Max(0, Math.Max(t.X0 - x1, x0 - t.X1)), ez = Math.Max(0, Math.Max(t.Z0 - z1, z0 - t.Z1));
            if (ex * ex + ez * ez < 150 * 150) return Loc.T("Too close to a town (at least 150 m).");
        }
        if (p.NodeTiles.Any(t => t != tile && t.X < x1 + 150 && t.X + t.SizeX > x0 - 150 && t.Z < z1 + 150 && t.Z + t.SizeZ > z0 - 150))
            return Loc.T("Too close to another node tile.");
        if (!NodeAreaFree(x0, z0)) return Loc.T("The place is not free (water, buildings, existing roads or the map edge).");
        var (dev, worst) = Flatness(x0, z0);
        if (dev > 4 || worst > 12) return Loc.F("The ground there is too uneven for a tile (on average {0:0.0} m, at most {1:0} m off).", dev, worst);
        var own = p.Roads.Where(r => town.Exits.Contains(r.From) || town.Exits.Contains(r.To)).ToList();
        foreach (var r in p.Roads.Except(own))
            foreach (var q in Resample(r.Points, 8))
                if (q.X > x0 - 20 && q.X < x1 + 20 && q.Y > z0 - 20 && q.Y < z1 + 20) return Loc.T("Another planned road runs there.");

        // new tile and exits; each road of the tile searched again to its moved exit (others see the new courses at once)
        var moved = new PlacedPrefab { Name = tile.Name, Def = tile.Def, X = x0, Z = z0, Rotation = tile.Rotation };
        double sum = 0; int cnt = 0;
        for (int z = z0 + 5; z < z1; z += 10) for (int x = x0 + 5; x < x1; x += 10) { sum += w.HeightAt(x, z); cnt++; }
        moved.Y = (int)Math.Round(sum / cnt + 0.85) + tile.Def.YOffset;
        var oldExits = town.Exits.ToDictionary(e => e, e => (e.X, e.Z, e.Cell));
        var lines = new Dictionary<NetRoad, (List<Point> Line, List<Point> Vias)>();
        var oldLines = new Dictionary<NetRoad, List<Point>>();
        void Restore()
        {
            foreach (var (ex, old) in oldExits) (ex.X, ex.Z, ex.Cell) = old;
            foreach (var (r, pts) in oldLines) r.Points = pts;
        }
        var keepBlock = (bool[])blocked.Clone();
        Mark(blocked, x0, z0, x1, z1, 6);   // the search must not cross the tile itself
        try
        {
            foreach (var e in town.Exits) { e.X += dx; e.Z += dz; e.Cell = CellOf(e.Approach.X, e.Approach.Y); }
            foreach (var r in own)
            {
                var e = town.Exits.Contains(r.From) ? r.From : r.To;
                var target = new NetEnd(e, null, new Point(e.X, e.Z), new Vector(e.DX, e.DZ));
                var line = MoveJunction(p, r, r.From == e, target, out string problem, out var vias);
                if (line == null) { Restore(); return problem ?? Loc.T("No way found for one of the roads of the tile."); }
                oldLines[r] = r.Points;
                r.Points = line;   // the next road of the tile sees this course (restored if a later one fails)
                lines[r] = (line, vias);
            }
        }
        catch { Restore(); throw; }
        finally { blocked = keepBlock; }
        // success: tile, town box and exits take the new place; the roads keep their new courses
        p.NodeTiles[p.NodeTiles.IndexOf(tile)] = moved;
        town.Tiles[0] = moved;
        town.X0 = x0; town.Z0 = z0; town.X1 = x1; town.Z1 = z1;
        foreach (var e in town.Exits) e.Tile = moved;
        foreach (var (r, (line, vias)) in lines)
        {
            r.Via = vias;
            Remeasure(r);
            NetworkPlanEdit.UpdateCrossings(p, r);
        }
        NetworkPlanEdit.Refresh(p);
        return null;
    }

    /// kind of a node tile: "X" crossing, "T" junction, "I" straight gateway
    public static string KindOf(PlacedPrefab tile) =>
        tile.Name.Equals(NodeX, StringComparison.OrdinalIgnoreCase) || tile.Name.StartsWith(NodeX, StringComparison.OrdinalIgnoreCase) ? "X"
        : tile.Name.Equals(NodeT, StringComparison.OrdinalIgnoreCase) ? "T" : "I";

    /// Change a node tile by hand (the user's request 2026-11-16, ToDo PART III): another kind (kind "X", "T", "I"; null = keep)
    /// and/or turned by 90° (turn). Every road of the tile is led to the exit that points most nearly its way (searched again
    /// like after moving the tile); exits without a road stay free – a new road can be drawn from them. Changes the plan only on
    /// success; null = done, otherwise the reason.
    public string ChangeNodeTile(NetworkPlan p, PlacedPrefab tile, string kind, bool turn)
    {
        var town = p.Towns.FirstOrDefault(t => t.Node && t.Tiles.Contains(tile));
        if (town == null || TileDefs == null) return Loc.T("This is not a node tile of the plan.");
        kind ??= KindOf(tile);
        var roads = town.Exits.Select(e => (Exit: e, Road: p.Roads.FirstOrDefault(r => r.From == e || r.To == e))).Where(x => x.Road != null).ToList();
        var center = new Point(tile.X + tile.SizeX / 2.0, tile.Z + tile.SizeZ / 2.0);
        // the way of every road: from the middle of the tile towards its point 200 m from the tile
        var ways = roads.Select(x =>
        {
            double len = LineLength(x.Road.Points), d = Math.Min(200, len);
            var far = At(x.Road.Points, x.Road.From == x.Exit ? d : len - d).P;
            var v = far - center;
            if (v.Length < 1) v = new Vector(x.Exit.DX, x.Exit.DZ);
            v.Normalize();
            return v;
        }).ToList();
        IEnumerable<string> names = kind == "X" ? new[] { NodeX } : kind == "T" ? new[] { NodeT }
            : KindOf(tile) == "I" ? new[] { tile.Name }.Concat(NodeStraight.Where(n => !n.Equals(tile.Name, StringComparison.OrdinalIgnoreCase))) : NodeStraight;
        int needExits = kind == "X" ? 4 : kind == "T" ? 3 : 2;
        if (roads.Count > needExits)
            return Loc.F("{0} roads use this tile, but this kind has only {1} exits. Delete a road of the tile first.", roads.Count, needExits);
        (PlacedPrefab Tile, List<(double X, double Z)> Exits, int[] Assign, double Cost)? best = null;
        foreach (var name in names)
        {
            var def = TileDefs(name);
            if (def == null) continue;
            var rotations = turn ? new[] { (tile.Rotation + 1) % 4 } : Enumerable.Range(0, 4).ToArray();
            foreach (int rot in rotations)
            {
                var probe = new PlacedPrefab { Name = name, Def = def, X = tile.X, Z = tile.Z, Rotation = rot };
                if (w.TileSurface(probe, out var ex) == null || ex.Count < roads.Count) continue;
                var dirs = ex.Select(q => { var (dx, dz) = Outward(probe, q.Item1, q.Item2); return new Vector(dx, dz); }).ToList();
                var (assign, cost) = BestAssignment(ways, dirs);
                if (assign == null) continue;
                if (name != names.First()) cost += 0.01;   // the same straight gateway if it fits as well
                if (best == null || cost < best.Value.Cost) best = (probe, ex.Select(q => (q.Item1, q.Item2)).ToList(), assign, cost);
            }
        }
        if (best == null) return Loc.T("No tile of this kind fits here (the game folder lacks it or it has too few exits).");
        var (newTile, newExits, map, _) = best.Value;
        if (newTile.Name.Equals(tile.Name, StringComparison.OrdinalIgnoreCase) && newTile.Rotation == tile.Rotation) return null;   // nothing to change
        newTile.Y = tile.Y - tile.Def.YOffset + newTile.Def.YOffset;   // same ground

        var old = town.Exits.ToDictionary(e => e, e => (e.X, e.Z, e.DX, e.DZ, e.Width, e.Cell, e.Tile));
        var oldLines = new Dictionary<NetRoad, List<Point>>();
        var lines = new Dictionary<NetRoad, (List<Point> Line, List<Point> Vias)>();
        void Restore()
        {
            foreach (var (e, o) in old) (e.X, e.Z, e.DX, e.DZ, e.Width, e.Cell, e.Tile) = o;
            foreach (var (r, pts) in oldLines) r.Points = pts;
        }
        var keepBlock = (bool[])blocked.Clone();
        Mark(blocked, tile.X, tile.Z, tile.X + tile.SizeX, tile.Z + tile.SizeZ, 6);   // the search must not cross the tile itself
        try
        {
            for (int i = 0; i < roads.Count; i++)
            {
                var (e, r) = roads[i];
                var (x, z) = newExits[map[i]];
                bool same = Math.Abs(e.X - x) < 0.6 && Math.Abs(e.Z - z) < 0.6;
                (e.DX, e.DZ) = Outward(newTile, x, z);
                e.X = x; e.Z = z; e.Tile = newTile;
                int width = w.TileExitWidth(newTile, x, z).Width;
                if (width > 0) e.Width = width;
                e.Cell = CellOf(e.Approach.X, e.Approach.Y);
                if (same) continue;
                var target = new NetEnd(e, null, new Point(e.X, e.Z), new Vector(e.DX, e.DZ));
                var line = MoveJunction(p, r, r.From == e, target, out string problem, out var vias);
                if (line == null) { Restore(); return problem ?? Loc.T("No way found for one of the roads of the tile."); }
                oldLines[r] = r.Points;
                r.Points = line;   // the next road of the tile sees this course
                lines[r] = (line, vias);
            }
        }
        catch { Restore(); throw; }
        finally { blocked = keepBlock; }
        // success: the exits with roads stay (moved), the others are replaced by the free exits of the new tile
        var keep = roads.Select(x => x.Exit).ToList();
        town.Exits.Clear();
        town.Exits.AddRange(keep);
        for (int k = 0; k < newExits.Count; k++)
        {
            if (map.Contains(k)) continue;
            var (x, z) = newExits[k];
            var (dx, dz) = Outward(newTile, x, z);
            int width = w.TileExitWidth(newTile, x, z).Width;
            var e = new NetExit { Town = town, Tile = newTile, X = x, Z = z, DX = dx, DZ = dz, Width = width > 0 ? width : 16, Gateway = true };
            int c = CellOf(e.Approach.X, e.Approach.Y);
            if (c >= 0 && !blocked[c]) e.Cell = c;
            town.Exits.Add(e);
        }
        p.NodeTiles[p.NodeTiles.IndexOf(tile)] = newTile;
        town.Tiles[0] = newTile;
        foreach (var (r, (line, vias)) in lines)
        {
            r.Via = vias;
            Remeasure(r);
            NetworkPlanEdit.UpdateCrossings(p, r);
        }
        NetworkPlanEdit.Refresh(p);
        return null;
    }

    /// roads → exits (every road its own exit) with the least turning: sum of (1 − cos) between the way of the road and the
    /// direction of the exit; all orders (at most 4 exits)
    static (int[] Assign, double Cost) BestAssignment(List<Vector> ways, List<Vector> dirs)
    {
        int[] best = null; double bestCost = double.MaxValue;
        var cur = new int[ways.Count];
        var used = new bool[dirs.Count];
        void Go(int i, double cost)
        {
            if (cost >= bestCost) return;
            if (i == ways.Count) { bestCost = cost; best = (int[])cur.Clone(); return; }
            for (int k = 0; k < dirs.Count; k++)
            {
                if (used[k]) continue;
                used[k] = true; cur[i] = k;
                Go(i + 1, cost + 1 - Vector.Multiply(ways[i], dirs[k]));
                used[k] = false;
            }
        }
        Go(0, 0);
        return (best, bestCost);
    }

    // ---------------------------------------------------------------- 3. after planning: tile by the roads that use it

    void FinishNodeTiles()
    {
        foreach (var town in plan.Towns.Where(t => t.Node).ToList())
        {
            var tile = town.Tiles[0];
            var used = town.Exits.Where(e => plan.Roads.Any(r => r.From == e || r.To == e)).ToList();
            int mask = used.Aggregate(0, (m, e) => m | 1 << SideOfExit(tile, e.X, e.Z));
            int all = town.Exits.Aggregate(0, (m, e) => m | 1 << SideOfExit(tile, e.X, e.Z));
            Log?.Invoke($"node {tile.X}/{tile.Z} {tile.Name}: {used.Count} of {town.Exits.Count} exits used");
            if (mask == all) continue;
            if (used.Count == 3 && TileDefs(NodeT) is PrefabDef tDef && NodeTile(NodeT, tDef, tile.X, tile.Z, mask) is PlacedPrefab t)
            { Retile(town, t, used); continue; }
            if (used.Count == 2 && (mask == 0b0101 || mask == 0b1010))
            {
                var straight = NodeStraight.Select(n => TileDefs(n) is PrefabDef d ? NodeTile(n, d, tile.X, tile.Z, mask) : null).Where(p => p != null).ToList();
                if (straight.Count > 0) { Retile(town, straight[rnd.Next(straight.Count)], used); continue; }
            }
            if (used.Count == 2 && JoinThrough(town, used[0], used[1])) { DropNode(town); continue; }
            if (used.Count == 2)
            {
                // not to be joined (junctions would form a circle): take away one of the two roads if no town is cut off by that,
                // then the tile with the other access is dropped like a tile with one road
                var pair = used.Select(e => (Exit: e, Road: plan.Roads.First(r => r.From == e || r.To == e))).ToList();
                var drop = pair.FirstOrDefault(p => CanTrim(p.Road));
                if (drop.Road == null) { Log?.Invoke($"node {tile.X}/{tile.Z}: kept with an unused exit"); continue; }
                TrimToNode(drop.Road, drop.Exit);
                used.Remove(drop.Exit);
            }
            if (used.Count <= 1)
            {
                // the road to the tile: gone, or (if other roads branch off it) shortened to the last of these junctions
                var stub = used.Select(e => (Exit: e, Road: plan.Roads.First(r => r.From == e || r.To == e))).FirstOrDefault();
                if (stub.Road != null && !CanTrim(stub.Road)) { Log?.Invoke($"node {tile.X}/{tile.Z}: kept, its road connects a town"); continue; }
                if (stub.Road != null) TrimToNode(stub.Road, stub.Exit);
                DropNode(town);
            }
        }
        NetworkPlanEdit.Refresh(plan);
    }

    /// may the road to a dropped node tile be taken away / shortened without cutting a town off the network?
    /// (shortened = the stretch behind the last junction falls away, which only carries the tile)
    bool CanTrim(NetRoad road) => NetworkPlanEdit.Dependents(plan, road).Count > 0 || TownGroups(road) == TownGroups(null);

    /// number of separate networks the towns (not node tiles) lie in – roads joined by their ends, junctions and crossings
    int TownGroups(NetRoad without)
    {
        var parent = new Dictionary<object, object>();
        object Root(object o) { if (!parent.TryGetValue(o, out var p)) { parent[o] = o; return o; } return p == o ? o : parent[o] = Root(p); }
        void Join(object a, object b) { if (a != null && b != null) parent[Root(a)] = Root(b); }
        foreach (var r in plan.Roads)
        {
            if (r == without) continue;
            Join(r, r.From?.Town); Join(r, r.To?.Town);
            if (r.StartRoad != without) Join(r, r.StartRoad);
            if (r.EndRoad != without) Join(r, r.EndRoad);
            foreach (var c in r.CrossedRoads) if (c != without) Join(r, c);
        }
        return plan.Towns.Where(t => !t.Node).Select(t => Root(t)).Distinct().Count();
    }

    /// road that leads only to a node tile that is dropped: without other roads on it it is deleted; otherwise it ends at the
    /// junction of the other road nearest to the tile (that road then simply continues it)
    void TrimToNode(NetRoad road, NetExit e)
    {
        var deps = NetworkPlanEdit.Dependents(plan, road);
        if (deps.Count == 0) { NetworkPlanEdit.Delete(plan, road, false); return; }
        if (road.From == e) Reverse(road);   // the tile at the end
        double cut = deps.Max(d => Project(road.Points, d.StartRoad == road ? d.Points[0] : d.Junction ?? d.Points[^1]).Along);
        road.Points = Slice(road.Points, 0, cut);
        road.To = null; road.EndRoad = null; road.Junction = null;
        // crossings on the cut-off piece fall away (on both sides)
        for (int k = road.Crossings.Count - 1; k >= 0; k--)
            if (Project(road.Points, road.Crossings[k]).Off > 2) { road.Crossings.RemoveAt(k); road.CrossedRoads.RemoveAt(k); }
        foreach (var o in plan.Roads)
            for (int k = o.CrossedRoads.Count - 1; k >= 0; k--)
                if (o.CrossedRoads[k] == road && Project(road.Points, o.Crossings[k]).Off > 2) { o.CrossedRoads.RemoveAt(k); o.Crossings.RemoveAt(k); }
        road.Via.RemoveAll(v => Project(road.Points, v).Off > 40);
        road.Samples = Resample(road.Points, 16);
        road.Length = road.Samples.Count > 1 ? (road.Samples.Count - 1) * 16.0 : 0;
    }

    void Retile(NetTown town, PlacedPrefab tile, List<NetExit> used)
    {
        plan.NodeTiles[plan.NodeTiles.IndexOf(town.Tiles[0])] = tile;
        town.Tiles[0] = tile;
        town.Exits.RemoveAll(e => !used.Contains(e));
        foreach (var e in town.Exits)
        {
            e.Tile = tile;
            int width = w.TileExitWidth(tile, e.X, e.Z).Width;
            if (width > 0) e.Width = width;
        }
    }

    void DropNode(NetTown town)
    {
        plan.NodeTiles.Remove(town.Tiles[0]);
        plan.Towns.Remove(town);
    }

    /// two roads end at a corner of the tile: one road through the tile area (curve) instead; false = not possible
    bool JoinThrough(NetTown town, NetExit a, NetExit b)
    {
        var ra = plan.Roads.FirstOrDefault(r => r.From == a || r.To == a);
        var rb = plan.Roads.FirstOrDefault(r => r.From == b || r.To == b);
        if (ra == null || rb == null || ra == rb) { Log?.Invoke($"node join {town.X0}/{town.Z0}: {(ra == rb ? "one road at both exits" : "road missing")}"); return false; }
        // the joined road takes its height at the far ends from the roads it branches off there; if one of them itself branches off
        // ra or rb (directly or further on), the junctions would form a circle – then not (checked before anything is changed)
        var startDep = ra.From == a ? ra.EndRoad : ra.StartRoad;
        var endDep = rb.To == b ? rb.StartRoad : rb.EndRoad;
        var after = NetworkPlanEdit.DependsOn(plan, ra);
        after.UnionWith(NetworkPlanEdit.DependsOn(plan, rb));
        after.Add(ra); after.Add(rb);
        if ((startDep != null && after.Contains(startDep)) || (endDep != null && after.Contains(endDep)))
        { Log?.Invoke($"node join {town.X0}/{town.Z0}: junctions would form a circle"); return false; }
        if (ra.From == a) Reverse(ra);   // ra ends at the tile
        if (rb.To == b) Reverse(rb);     // rb starts at the tile
        // without the straight pieces in the tile margin: the curve cuts the corner of the tile
        var pa = Slice(ra.Points, 0, Math.Max(0, LineLength(ra.Points) - 40));
        var pb = Slice(rb.Points, 40, LineLength(rb.Points));
        var pts = new List<Point>(pa);
        var fx = Enumerable.Repeat(true, pa.Count).ToList();
        for (int q = Math.Max(1, pa.Count - 6); q < pa.Count; q++) fx[q] = false;
        var corner = new Point((a.X + b.X) / 2, (a.Z + b.Z) / 2);   // middle between the two exits (inside the tile corner)
        pts.Add(corner); fx.Add(false);
        for (int q = 0; q < pb.Count; q++) { pts.Add(pb[q]); fx.Add(q >= 6); }
        var line = Smooth(pts, fx);
        var joined = new NetRoad
        {
            Points = line, Gravel = ra.Gravel, Width = Math.Max(ra.Width, rb.Width), Loop = ra.Loop && rb.Loop, Cut = ra.Cut || rb.Cut,
            From = ra.From, StartRoad = ra.StartRoad, To = rb.To, EndRoad = rb.EndRoad, Junction = rb.Junction,
        };
        joined.Via.AddRange(ra.Via); joined.Via.AddRange(rb.Via);
        joined.Bridges.AddRange(ra.Bridges); joined.Bridges.AddRange(rb.Bridges);
        foreach (var o in plan.Roads)
        {
            if (o.StartRoad == ra || o.StartRoad == rb) o.StartRoad = joined;
            if (o.EndRoad == ra || o.EndRoad == rb) o.EndRoad = joined;
            for (int k = 0; k < o.CrossedRoads.Count; k++) if (o.CrossedRoads[k] == ra || o.CrossedRoads[k] == rb) o.CrossedRoads[k] = joined;
        }
        int index = Math.Max(plan.Roads.IndexOf(ra), plan.Roads.IndexOf(rb));
        plan.Roads[index] = joined;
        plan.Roads.Remove(ra); plan.Roads.Remove(rb);
        joined.Samples = Resample(line, 16);
        joined.Length = joined.Samples.Count > 1 ? (joined.Samples.Count - 1) * 16.0 : 0;
        if (!NetworkPlanEdit.Reorder(plan)) { Log?.Invoke($"node join {town.X0}/{town.Z0}: junctions in a circle"); return false; }
        NetworkPlanEdit.UpdateCrossings(plan, joined);
        return true;
    }

    /// direction of a road turned round (start ↔ end, junction at the start ↔ at the end)
    static void Reverse(NetRoad r)
    {
        var oldFirst = r.Points[0];
        r.Points = Enumerable.Reverse(r.Points).ToList();
        (r.From, r.To) = (r.To, r.From);
        (r.StartRoad, r.EndRoad) = (r.EndRoad, r.StartRoad);
        r.Junction = r.EndRoad != null ? oldFirst : null;
        r.Via.Reverse();
        r.Samples = Resample(r.Points, 16);
    }

    /// piece of a line between two distances along it
    static List<Point> Slice(List<Point> line, double from, double to)
    {
        var res = new List<Point> { At(line, from).P };
        double walked = 0;
        for (int q = 0; q + 1 < line.Count; q++)
        {
            walked += (line[q + 1] - line[q]).Length;
            if (walked > from + 0.5 && walked < to - 0.5) res.Add(line[q + 1]);
        }
        res.Add(At(line, to).P);
        return res;
    }
}
