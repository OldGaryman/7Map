using System.Windows;

namespace MapEditor.Map;

/// Towns at a river (the user's request 2026-11-19): the main part lies on one bank, a district on the other bank, the road between
/// them crosses the river on a bridge from the mod YAMEE_Bridges (Bridge.cs). One town per river (water body) if there is room;
/// otherwise the town is placed as usual. Bridges only run exactly west–east or north–south, so the place of the bridge is a piece
/// of the river that flows roughly in a compass direction (Bridge.Find); the river is made straight there when the bridge is built.
public sealed partial class CityGenerator
{
    /// length of the end piece of the bridge and its terrain columns (set by the main window when the mod is there); 0 = no bridges
    public int BridgeEndLength { get; set; }
    public int BridgeTerrainColumns { get; set; }
    /// the active bridge kits (window "Bridge mods", since 2026-11-24); null = only YAMEE_Bridges from BridgeEndLength
    public BridgeKits BridgeKits { get; set; }
    bool HasBridges => BridgeKits?.Kits.Count > 0 || (BridgeKits == null && BridgeEndLength > 0);

    sealed class RiverSpot
    {
        public BridgeSite Site;
        public int Body;          // number of the water body (river with its lakes)
        public double Score;
        public double Height => Site.Deck - 1 + Site.ApproachLift;   // height of the approach roads
        // end of the approach on the low / high side (coordinate along the span) and the middle across
        public int EndLo => Site.S0 - Bridge.Approach;
        public int EndHi => Site.S0 + Site.Length + Bridge.Approach;
        public double T => Site.MidT;
        public Point At(double s, double t) => Site.AlongX ? new Point(s, t) : new Point(t, s);
        /// side index (Dir) pointing away from the river on the low (sgn −1) or high (sgn +1) side
        public int OutSide(int sgn) => Site.AlongX ? (sgn < 0 ? 3 : 1) : (sgn < 0 ? 0 : 2);
        public (int X0, int Z0, int X1, int Z1) Zone(int pad)
        {
            int s0 = EndLo - pad, s1 = EndHi + pad, t0 = Site.T0 - Bridge.Approach - pad, t1 = Site.T0 + Site.Width + Bridge.Approach + pad;
            return Site.AlongX ? (s0, t0, s1, t1) : (t0, s0, t1, s1);
        }
    }

    /// planning a town at a river: the spot and the side of the main part (−1 = low side along the span, +1 = high side)
    sealed record RiverCtx(RiverSpot Spot, int Sgn);

    RiverCtx river;   // the town currently planned (null = normal town)
    readonly Dictionary<string, int> riverFails = new();
    void RiverFail(string why) => riverFails[why] = riverFails.GetValueOrDefault(why) + 1;

    /// places for bridges: water cells at the edge of a water body, where Bridge.Find sees a straight river in a compass direction
    List<RiverSpot> FindRiverSpots()
    {
        // water bodies on the coarse grid (4 neighbors)
        var label = new int[cn * cn];
        int bodies = 0;
        var stack = new Stack<int>();
        for (int g = 0; g < label.Length; g++)
        {
            if (!cw[g] || label[g] != 0) continue;
            label[g] = ++bodies;
            stack.Push(g);
            while (stack.Count > 0)
            {
                int k = stack.Pop(), kx = k % cn, kz = k / cn;
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = kx + dx, nz = kz + dz;
                    if (nx < 0 || nz < 0 || nx >= cn || nz >= cn) continue;
                    int q = nz * cn + nx;
                    if (cw[q] && label[q] == 0) { label[q] = bodies; stack.Push(q); }
                }
            }
        }
        var spots = new List<RiverSpot>();
        for (int cz = 2; cz < cn - 2; cz += 2)
            for (int cx = 2; cx < cn - 2; cx += 2)
            {
                int g = cz * cn + cx;
                if (!cw[g]) continue;
                // inside a lake (water everywhere around) there is no river
                bool dry = false;
                for (int dz = -2; dz <= 2 && !dry; dz++)
                    for (int dx = -2; dx <= 2 && !dry; dx++) if (!cw[(cz + dz) * cn + cx + dx]) dry = true;
                if (!dry) continue;
                double x = cx * Coarse - w.Half + Coarse / 2.0, z = cz * Coarse - w.Half + Coarse / 2.0;
                var site = BridgeKits != null ? BridgeKits.Find(w, x, z, false, Bridge.MaxAngle, null, 34) : Bridge.Find(w, x, z, BridgeEndLength, BridgeTerrainColumns);
                if (site.Problem != null || site.WaterWidth > 34) continue;   // narrow river: like a 10x10 bridge with 1–2 middle pieces (34 m of water)
                // not at a lake (mouth): share of water within about 100 m around the bridge
                var sc = site.Center;
                int wet = 0, all = 0;
                for (double dz = -96; dz <= 96; dz += Coarse)
                    for (double dx = -96; dx <= 96; dx += Coarse) { all++; if (cw[CoarseIndex(sc.X + dx, sc.Y + dz)]) wet++; }
                double share = (double)wet / all;
                if (share > 0.3) continue;
                var spot = new RiverSpot { Site = site, Body = label[g], Score = site.RiverAngle / 5 + site.Tiles + 20 * share };
                var c = site.Center;
                if (spots.Any(o => o.Body == spot.Body && (o.Site.Center - c).Length < 40)) continue;
                spots.Add(spot);
            }
        Log?.Invoke($"river spots: {spots.Count} in {spots.Select(s => s.Body).Distinct().Count()} water bodies");
        return spots;
    }

    /// one town per river (water body) with a bridge, before the other towns; returns how many towns of each type were placed
    Dictionary<TownType, int> PlaceRiverTowns(List<CityPlan> plans, IProgress<string> progress)
    {
        var placed = new Dictionary<TownType, int>();
        if (!spec.RiverTowns) return placed;
        if (!HasBridges)
        {
            Problems.Add(Loc.T("Towns at rivers: no bridge kit is active (Settings › Mods › Bridge mods)."));
            return placed;
        }
        var spots = FindRiverSpots();
        var bodyGroups = spots.GroupBy(s => s.Body).OrderBy(_ => rnd.Next()).ToList();
        int bodies = bodyGroups.Count, towns = 0, bodyIdx = 0;
        foreach (var body in bodyGroups)
        {
            CityPlan plan = null;
            foreach (var spot in body.OrderBy(s => s.Score + rnd.NextDouble() * 2).Take(10))
            {
                var c = spot.Site.Center;
                if (Near(c.X - 100, c.Y - 100, c.X + 100, c.Y + 100, 450)) continue;
                int biome = BiomeAt(w, c.X, c.Y);
                // a type of this biome that still has towns left; medium sizes first (a big city rarely fits between river and lakes)
                var types = spec.Types.Where(t => t.Count > placed.GetValueOrDefault(t) && t.TilesMax >= 3 && t.Biomes.Contains(biome))
                    .OrderBy(t => Math.Abs((t.TilesMin + t.TilesMax) / 2.0 - 8) + rnd.NextDouble() * 4).ToList();
                foreach (var type in types)
                {
                    progress.Report(Loc.F("Planning towns: {0} at a river …", type.Label), bodyIdx / (double)Math.Max(1, bodies));
                    plan = PlaceRiverTown(type, spot);
                    if (plan == null) continue;
                    placed[type] = placed.GetValueOrDefault(type) + 1;
                    towns++;
                    plans.Add(plan);
                    blocked.Add((plan.X0, plan.Z0, plan.X1, plan.Z1, int.MaxValue));
                    plannedRoads.AddRange(plan.Roads.Select(r => r.Points));
                    plannedCorridors.AddRange(plan.Corridors);
                    break;
                }
                if (plan != null) break;
            }
            bodyIdx++;
            Log?.Invoke($"river body {body.Key}: {(plan != null ? "town " + plan.Type.Label : "no town")} ({body.Count()} spots)");
            Log?.Invoke("  reasons: " + string.Join(", ", riverFails.Select(f => $"{f.Key} {f.Value}")));
            riverFails.Clear();
        }
        if (towns < bodies) Problems.Add(Loc.F("Towns at rivers: {0} of {1} river(s) got a town with a bridge (no room at the others).", towns, bodies));
        return placed;
    }

    CityPlan PlaceRiverTown(TownType type, RiverSpot spot)
    {
        int n = rnd.Next(Math.Max(3, type.TilesMin), type.TilesMax + 1);
        int maxParts = Math.Clamp(Math.Min(spec.MaxParts, n <= 4 ? 1 : n <= 9 ? 2 : 3), 1, 3);
        int partCount = rnd.Next((maxParts + 1) / 2, maxParts + 1);
        partCount = Math.Max(1, partCount);
        var partSizes = Enumerable.Range(0, partCount).Select(_ => 1 + rnd.Next(Math.Clamp(n / 5, 1, 3))).ToList();
        int mainTarget = Math.Max(1, n - partSizes.Sum());
        double half = Math.Sqrt(mainTarget) * Tile / 2.0;
        elevatedTown = false;
        terraceTown = false;
        // the bridge with its approaches stays free of tiles (as a blocked area: tiles keep 60 m, the roads 6 m)
        var zone = spot.Zone(0);
        var entry = (zone.X0, zone.Z0, zone.X1, zone.Z1, int.MaxValue);
        blocked.Add(entry);
        try
        {
            foreach (int sgn in rnd.Next(2) == 0 ? new[] { -1, 1 } : new[] { 1, -1 })
            {
                int end = sgn < 0 ? spot.EndLo : spot.EndHi;
                foreach (double dist in new[] { 70.0, 130, 190 })
                    foreach (double lat in new[] { 0.0, -90, 90, -180, 180 })
                    {
                        var c = spot.At(end + sgn * (dist + half), spot.T + lat);
                        int biome = BiomeAt(w, c.X, c.Y);
                        if (!type.Biomes.Contains(biome)) continue;
                        river = new RiverCtx(spot, sgn);
                        minTiles = Math.Max(2, (int)Math.Ceiling(type.TilesMin * 0.6));
                        int failsBefore = riverFails.Values.Sum();
                        var plan = Layout(type, mainTarget, partSizes, c.X, c.Y, biome);
                        if (plan == null && riverFails.Values.Sum() == failsBefore) RiverFail("no main part (room, biome, earthwork)");
                        river = null;
                        if (plan == null) continue;
                        plan.Bridge = spot.Site;
                        Log?.Invoke($"river town {type.Label} at {c.X:0},{c.Y:0}: {plan.Tiles.Count} tiles, bridge {spot.Site.Center.X:0},{spot.Site.Center.Y:0}");
                        return plan;
                    }
            }
        }
        finally { river = null; }
        blocked.Remove(entry);
        return null;
    }

    /// is a tile of the main part on its own bank, at least RoadRoom beyond the end of the bridge approach (room for the road to come
    /// straight onto the bridge – closer, it had to make a loop)?
    bool OnMainBank(int x0, int z0)
    {
        if (river == null) return true;
        var s = river.Spot;
        double a0 = s.Site.AlongX ? x0 : z0;
        return river.Sgn > 0 ? a0 >= s.EndHi + RoadRoom : a0 + Tile <= s.EndLo - RoadRoom;
    }

    const int RoadRoom = 60;

    /// main part close enough to its bridge end for a road with the allowed gradient?
    bool MainReachesBridge(Unit main)
    {
        var s = river.Spot;
        var e = s.At(river.Sgn < 0 ? s.EndLo : s.EndHi, s.T);
        var r = main.Rect;
        double dx = Math.Max(0, Math.Max(r.X0 - e.X, e.X - r.X1)), dz = Math.Max(0, Math.Max(r.Z0 - e.Y, e.Y - r.Z1));
        double d = Math.Sqrt(dx * dx + dz * dz);
        double dh = Math.Abs(main.Floor - 0.85 - s.Height);
        return d >= dh / spec.RoadGrade + 20 && d <= 320;   // room to descend straight (with 0.75 × the road wound in a loop)
    }

    /// the district on the other bank: natural height, little earthwork, close to the other bridge end and reachable from it
    Unit FindBridgeUnit(Unit main, List<Unit> others, int m, HashSet<int> biomes)
    {
        var s = river.Spot;
        int far = -river.Sgn;
        int end = far < 0 ? s.EndLo : s.EndHi;
        var candidates = new List<(int X, int Z, (int, int)[] Shape, int Floor, double Score)>();
        foreach (var shape in Shapes(Math.Clamp(m, 1, 3)))
        {
            int sw = (shape.Max(q => q.Item1) + 1) * Tile, sd = (shape.Max(q => q.Item2) + 1) * Tile;
            int along = s.Site.AlongX ? sw : sd, across = s.Site.AlongX ? sd : sw;
            for (int gap = RoadRoom; gap <= 240; gap += 15)
                for (int lat = -180; lat <= 180; lat += 30)
                {
                    int a0 = far > 0 ? end + gap : end - gap - along;
                    int t0 = (int)Math.Round(s.T + lat - across / 2.0);
                    int x0 = s.Site.AlongX ? a0 : t0, z0 = s.Site.AlongX ? t0 : a0;
                    var rect = (x0, z0, x0 + sw, z0 + sd);
                    if (others.Append(main).Any(o => RectGap(rect, o.Rect) < UnitGap)) continue;
                    bool ok = true;
                    double sum = 0;
                    int cnt = 0;
                    foreach (var (i, j) in shape)
                    {
                        if (!TileAreaOk(x0 + i * Tile, z0 + j * Tile, biomes)) { ok = false; break; }
                        for (int z = z0 + j * Tile + 8; z < z0 + (j + 1) * Tile; z += Coarse)
                            for (int x = x0 + i * Tile + 8; x < x0 + (i + 1) * Tile; x += Coarse) { sum += ch[CoarseIndex(x, z)]; cnt++; }
                    }
                    if (!ok) continue;
                    double mean = sum / cnt;
                    int floor = (int)Math.Round(mean + 0.85);
                    double dh = Math.Abs(floor - 0.85 - s.Height);
                    if (gap + Math.Abs(lat) * 0.5 < dh / spec.RoadGrade + 20) continue;
                    // room for an S-curve when the district lies beside the bridge axis (closer, the road wound in a loop – Zumoza 8k)
                    double off = Math.Abs(s.T - (t0 + across / 2.0));
                    if (gap < 1.3 * off + 44) continue;
                    if (Math.Abs(floor - main.Floor) > DeltaMaxNow + 10) continue;
                    double dev = 0;
                    foreach (var (i, j) in shape)
                        for (int z = z0 + j * Tile + 8; z < z0 + (j + 1) * Tile; z += Coarse)
                            for (int x = x0 + i * Tile + 8; x < x0 + (i + 1) * Tile; x += Coarse) dev += Math.Abs(ch[CoarseIndex(x, z)] - mean);
                    dev /= cnt;
                    if (dev > 8) continue;
                    candidates.Add((x0, z0, shape, floor, dev + gap / 30.0 + Math.Abs(lat) / 15.0 + dh / 3 + rnd.NextDouble() * 1.5));   // in line with the bridge: the road comes straight
                }
        }
        foreach (var cand in candidates.OrderBy(c => c.Score).Take(30))
        {
            var u = new Unit { OX = cand.X, OZ = cand.Z, BridgeSpot = s };
            foreach (var (i, j) in cand.Shape) u.Cells[(i, j)] = new Cell { I = i, J = j };
            u.Floor = FloorOf(u);
            if (u.Cells.Values.All(c => EarthworkOk(ProbeTile(u.X(c), u.Z(c), u.Floor)) && !WaterAround(ProbeTile(u.X(c), u.Z(c), u.Floor))))
            {
                Log?.Invoke($"river district at {cand.X},{cand.Z}: floor {u.Floor}, approach {s.Height:0.0}, score {cand.Score:0.0}");
                return u;
            }
        }
        return null;
    }

    /// link over the bridge: an exit of the main part that points towards its bridge end, an exit of the district towards the other end
    Link BridgeLink(Unit main, Unit u)
    {
        var s = u.BridgeSpot;
        (Cell C, int Side) Best(Unit part, int sgn)
        {
            var e = s.At(sgn < 0 ? s.EndLo : s.EndHi, s.T);
            int outSide = s.OutSide(sgn);
            (Cell, int) best = (null, 0);
            double bestScore = double.MaxValue;
            foreach (var c in part.Cells.Values)
                for (int side = 0; side < 4; side++)
                {
                    if (c == main.Inner || !Free(part, c, side) || Bits(c.Mask) >= 4) continue;
                    var (px, pz) = SideMid(part, c, side);
                    double dx = e.X - px, dz = e.Y - pz, d = Math.Max(1, Math.Sqrt(dx * dx + dz * dz));
                    double toward = (Dir[side].DI * dx + Dir[side].DJ * dz) / d;
                    if (toward < -0.2) continue;   // exit points away from the bridge
                    // best: an exit that faces the river and lies close to the bridge axis (then the road runs straight onto the bridge)
                    double lateral = s.Site.AlongX ? Math.Abs(pz - s.T) : Math.Abs(px - s.T);
                    double score = d + 1.5 * lateral + (1 - toward) * 60 + (side == (outSide + 2) % 4 ? 0 : 80);
                    if (score < bestScore) { bestScore = score; best = (c, side); }
                }
            return best;
        }
        var (ca, sa) = Best(main, river?.Sgn ?? MainSign(main, s));
        var (cb, sb) = Best(u, -(river?.Sgn ?? MainSign(main, s)));
        return ca == null || cb == null ? null : new Link(main, ca, sa, u, cb, sb);
    }

    static int MainSign(Unit main, RiverSpot s)
    {
        var (x, z) = main.Center;
        double sc = s.Site.AlongX ? x : z;
        return sc < (s.EndLo + s.EndHi) / 2.0 ? -1 : 1;
    }

    /// road from an exit of a part to the end of the bridge approach on side sgn (arrives straight along the bridge)
    CityRoad RouteToBridge(List<PlacedPrefab> townTiles, List<List<Point>> roads, List<(int X0, int Z0, int X1, int Z1)> corridors,
        PlacedPrefab ta, (double X, double Z, double Width, byte Kind) ea, int sa, RiverSpot s, int sgn)
    {
        var e = s.At(sgn < 0 ? s.EndLo : s.EndHi, s.T);
        int outSide = s.OutSide(sgn);
        // the end of the approach as a "tile exit" pointing away from the river; a stand-in tile at the height of the approach
        // (CheckRoute compares the heights of both ends)
        var stand = new PlacedPrefab { Name = "bridge", Def = Probe, X = (int)e.X, Z = (int)e.Y, Y = (int)Math.Round(s.Height) + 1 };
        var eb = (e.X, e.Y, (double)s.Site.RoadWidth, WorldData.Asphalt);
        var points = RouteBetween(townTiles, roads, corridors, ta, stand, ea, sa, eb, outSide);
        if (points == null) return null;
        // no loops: the direction of the road may turn by at most 200° in all (an S-curve and the turns out of the tile need less)
        double turn = 0;
        for (int i = 1; i + 1 < points.Count; i++)
        {
            var a = points[i] - points[i - 1]; var b = points[i + 1] - points[i];
            if (a.Length < 0.5 || b.Length < 0.5) continue;
            turn += Math.Abs(Vector.AngleBetween(a, b));
        }
        if (turn > 200) { Log?.Invoke($"river road with loop ({turn:0}°) rejected"); return null; }
        // the last point lies 1 m on the approach; one more point further on it so that the road ends straight on the bridge axis
        var inward = new Vector(-Dir[outSide].DI, -Dir[outSide].DJ);
        points.Add(new Point(e.X + inward.X * 6, e.Y + inward.Y * 6));
        return new CityRoad(points, ea.Width, Math.Min(ea.Width, s.Site.RoadWidth), ea.Kind == WorldData.Gravel ? Surface.Gravel : Surface.Asphalt, s.Height);
    }
}
