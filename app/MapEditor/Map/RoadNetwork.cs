using System.Windows;

namespace MapEditor.Map;

/// settings for "Create roads" (the user's request 2026-11-07, step 3 after terrain and cities)
public sealed class NetworkSpec
{
    public double Grade = 0.14;      // usual gradient (above it it becomes more expensive)
    public double MaxGrade = 0.22;   // in narrow passages at most (decision of the user)
    public double ShortMaxGrade = 0.25;    // absolute exception: short stretches (at most ShortSteepLength) may be this steep (the user's request 2026-11-12)
    public double ShortSteepLength = 150;  // m; longer stretches never go beyond MaxGrade
    public double RoadFill = 1.2;      // embankment at most this high above the terrain (m); < 0 = old moving-average course
    public double RoadCut = 2.2;       // cut at most this deep (m)
    public double RoadEdgeFill = 2.0;  // on a slope: roadway at most this high above the lower edge (m)
    public int Loops = 1;           // cross connections: 0 few, 1 normal, 2 many
    public int Curves = 1;           // curviness: 0 little, 1 normal, 2 strong
    public int Cuts = 1;             // clearings: 0 never, 1 rarely, 2 more often
    public int NodeTiles = 1;        // node tiles (T/crossing tiles) at junctions and crossings: 0 none, 1 some, 2 many (the user's request 2026-11-13)
    public bool BreakInWasteland = true;   // wasteland only: interrupt roads at bomb craters & earthquake cracks (as if damage occurred after road building)
    public string Seed = "YAMEE";
    public NetworkSpec Clone() => (NetworkSpec)MemberwiseClone();
}

/// city from the tiles of the map (connected group including districts and gateway)
public sealed class NetTown
{
    public int Id;
    public List<PlacedPrefab> Tiles = new();
    public bool OldWest;
    public int X0, Z0, X1, Z1;
    public List<NetExit> Exits = new();
    public bool Connected;
    public bool Linked;            // already joined to roads that lead away from the town (existing road network), no free exit needed
    public bool Node;              // planned node tile between towns (RoadNetwork.Nodes), not on the map yet
    public double CX => (X0 + X1) / 2.0;
    public double CZ => (Z0 + Z1) / 2.0;
}

/// free road exit of a tile (no neighbor, no road in front)
public sealed class NetExit
{
    public NetTown Town;
    public PlacedPrefab Tile;
    public double X, Z, DX, DZ;    // middle at the tile edge, direction outwards
    public int Width;              // roadway of the tile at the exit (m)
    public bool Gravel, Gateway, Used;
    // connection to an existing town street (town connection): X/Z = middle of the road, DX/DZ = direction of the new road,
    // Width = roadway width there, StreetHalf = half width including edge
    public bool Street;
    public double Earthwork;       // ground along the road this far above (+) or below (−) the tile beyond the highest gradient (m, 0 = none)
    public double StreetHalf, StreetDX, StreetDZ;
    public PlacedPrefab Poi;       // entrance of a building (route tool, RoadNetwork.Route): the road builder takes over its ground height
    public double? Height;         // fixed height of the road at this end (route tool: outer end of a bridge approach)
    public BridgeEnd Bridge;       // route tool: the end is the outer end of this bridge approach (snap point like the road tools)
    public string Why;             // reason why the exit is not reachable (diagnosis)
    internal int Cell = -1;      // grid cell 44 m in front of the exit (-1 = not reachable)
    public bool Reachable => Cell >= 0;
    public Point Approach => new(X + DX * RoadNetwork.ApproachLength, Z + DZ * RoadNetwork.ApproachLength);
}

/// planned road of the network (center line from From to To or to the junction)
public sealed class NetRoad
{
    public List<Point> Points;
    public bool Gravel;
    public double Width;           // roadway (asphalt 16 m, gravel like the exit, at most 12 m)
    public bool Loop;              // cross connection (not needed to make every city reachable)
    public bool Feeder;            // feeder (second pass): free city exit to a nearby road
    public bool TownLink;          // town connection: short connection from a passing road to a town street
    public bool Cut;               // may cut through ridges with a clearing
    public bool Manual;            // drawn by hand in the plan (RoadNetwork.NewRoad)
    public double CurveBanking;    // route tool: banking in curves up to this (fraction, 0 = the natural cross slope of the builder)
    public bool ToEdge;            // from a town exit without another connection to the map edge, ends freely there (RoadNetwork.Edge)
    public bool OnOldRoad;         // laid again on an existing road (branch joined after a bridge swap): its old surface takes the new heights
    public NetExit From, To;       // To = null: ends at another road (junction)
    public Point? Junction;
    public double Length, MaxGrade, SteepShare, MaxCut;
    public Point? SteepAt, CutAt;   // where the gradient (terrain along the road) is largest, where the clearing is deepest
    public List<Point> Crossings = new();
    public List<NetRoad> CrossedRoads = new();   // per intersection the crossed road (same order as Crossings)
    public NetRoad StartRoad, EndRoad;           // road at which the road branches off at the start/end (null = city exit)
    public List<Point> Via = new();              // via points set by hand (in order along the road), see RoadNetwork.Reroute
    public List<BridgeSite> Bridges = new();      // bridges over rivers on this road (in order along the road), see RoadNetwork.Bridges
    internal List<Point> Samples;  // every 16 m
    internal int[] Nodes;          // node in the road graph per sample
}

public sealed class NetworkPlan
{
    public List<NetTown> Towns = new();
    public List<NetRoad> Roads = new();
    public List<Point> Junctions = new();
    public List<Point> CrossingPoints = new();
    public List<PlacedPrefab> NodeTiles = new();   // planned node tiles (T/crossing) between towns, built with the roads
    public List<string> Problems = new();
    public double StraightShare;   // share of the length that is practically straight (over 288 m at most 2 m deviation)
    public double Seconds;
}

/// plan the road network between the cities (stage 1: only plan, change nothing). Procedure:
/// 1. cities from the tiles (groups with at most a 330 m gap), free exits (no neighbor, no road in front).
/// 2. cost grid 16 m: length, gradient (cheap up to Grade, expensive up to MaxGrade, very expensive above), cross slope, rock walls;
///    valleys cheaper, hilltops more expensive (position relative to the surroundings, about 400 m); large-scale noise (240/600 m) so that the road
///    swings in curves even in the plain. Blocked: map edge 100 m, water, tiles (+24 m), buildings (+12 m), existing
///    roads, already planned roads (up to 26 m) – except at junctions and intersections (right-angled, distance ≥ 200 m).
/// 3. like Prim: from the largest city the city that connects most cheaply to the network so far is always connected –
///    to a free exit or sideways to a planned road (junction). Old West places last with gravel paths.
/// 4. cross connections where the path over the network would be much longer (≥ 1.8 × or 2.2 × between two roads); they may
///    cross other roads.
/// Some of the roads may cut through ridges with a clearing (terrain for the path search lowered up to 20 m).
public sealed partial class RoadNetwork
{
    const int C = 16;                           // grid (m)
    public const double ApproachLength = 44;    // straight piece in front of the exit or in front of the junction (m)
    /// roads (roadway including edge) stay at least this far from the map edge (the user's request 2026-11-08: not below
    /// 64 m, better 80 m); the center line therefore EdgeKeep + 9 m (half of the 16 m roadway + 1 m edge)
    public const double EdgeKeep = 80, EdgeCenter = EdgeKeep + 9;
    const double RoadBuffer = 26;               // center lines of foreign roads must not come this close (cell center)
    const double NodeSpacing = 250;             // distance between junctions/intersections/road ends (m; 200 m felt crowded)
    const double TownGap = 150;                 // no junction closer to a city (m)
    const double JunctionGrade = 0.07;           // junctions and intersections only where the road (terrain along it) is at most this steep
    const float JunctionPenalty = 80, CrossPenalty = 120, ThroughTown = 60;

    readonly WorldData w;
    readonly NetworkSpec spec;
    readonly Random rnd;
    readonly int gw, n, half;
    float[] hb, hcut, gradX, gradZ, mult;
    int[] reserveOwner;                        // cell in front of a free exit (number in exitList), -1 = none
    bool[] nearTown;                           // up to 100 m beside a city: more expensive (roads should not scrape along cities)
    readonly List<NetExit> exitList = new();
    HashSet<NetExit> allowed = new();          // exits whose forecourt the current search may use
    bool[] roadCell, tileCell;                 // existing surface or middle in a tile (per cell)
    bool[] blocked, roadBlock, edge;           // edge = edge strip 100 m (blocked except in front of city exits near the edge)
    // proximity to planned roads (the user's request 2026-11-08: roadways ran close beside each other over a long stretch):
    // distance to the nearest center line up to ParallelRange and its terrain height there
    float[] nearRoadD, nearRoadH;
    const double ParallelRange = 160, ParallelHeight = 12;   // on Rifane-Land built roads ran side by side at 100–200 m distance
    bool probing;               // trial planning without node tiles (only to find where junctions and crossings arise)
    NetworkPlan plan = new();   // the plan being made (for editing by hand: the plan of the window, see RoadNetwork.Nodes)
    readonly List<Point> nodesUsed = new();
    readonly List<List<(int To, double Cost)>> graph = new();

    static readonly (int DI, int DJ)[] Steps =
    {
        (1, 0), (2, 1), (1, 1), (1, 2), (0, 1), (-1, 2), (-1, 1), (-2, 1),
        (-1, 0), (-2, -1), (-1, -1), (-1, -2), (0, -1), (1, -2), (1, -1), (2, -1),
    };

    public static Action<string> Log;   // test output

    public RoadNetwork(WorldData world, NetworkSpec s)
    {
        w = world; spec = s;
        rnd = new Random(new GeneratorSpec { Seed = s.Seed + "/roads" }.SeedValue);
        half = w.Half; gw = w.Size / C; n = gw * gw;
    }

    int CellOf(double x, double z)
    {
        int i = (int)Math.Floor((x + half) / C), j = (int)Math.Floor((z + half) / C);
        return i < 0 || j < 0 || i >= gw || j >= gw ? -1 : j * gw + i;
    }
    Point Center(int c) => new(-half + (c % gw) * C + C / 2.0, -half + (c / gw) * C + C / 2.0);
    bool Passable(int c) => c >= 0 && !blocked[c] && !roadBlock[c];

    /// value of a grid field at a world place (bilinear)
    double Sample(float[] f, double x, double z)
    {
        double fx = (x + half) / C - 0.5, fz = (z + half) / C - 0.5;
        int i = Math.Clamp((int)Math.Floor(fx), 0, gw - 2), j = Math.Clamp((int)Math.Floor(fz), 0, gw - 2);
        double tx = Math.Clamp(fx - i, 0, 1), tz = Math.Clamp(fz - j, 0, 1);
        int g = j * gw + i;
        return (f[g] * (1 - tx) + f[g + 1] * tx) * (1 - tz) + (f[g + gw] * (1 - tx) + f[g + gw + 1] * tx) * tz;
    }

    public NetworkPlan Plan(IProgress<string> progress)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool hasProbe = spec.NodeTiles > 0 && TileDefs != null && !probing;
        progress.Report(Loc.T("Reading the terrain …"), 0.0);
        BuildGrid();
        FindTowns();
        FindStreetCrossings();
        FindBridgeSpots();
        // node tiles between the towns (like small towns; the network runs through them). First a trial planning without
        // tiles shows where junctions and crossings arise (the user's idea 2026-11-16, ToDo PART III); the tiles are put there
        NetworkPlan probe = null;
        double mainStart = hasProbe ? 0.30 : 0.08;
        if (hasProbe)
        {
            string trialText = Loc.T("Trial planning: where do junctions and crossings arise …");
            progress.Report(trialText, 0.05);
            var ps = spec.Clone(); ps.NodeTiles = 0;
            IProgress<string> trialProg = progress == null ? null : new ProbeProgress(progress, trialText, 0.05, 0.25);
            probe = new RoadNetwork(w, ps) { probing = true }.Plan(trialProg);
            progress.Report(Loc.T("Looking for places for node tiles …"), 0.25);
        }
        PlaceNodeSites(progress, probe);
        var asphalt = plan.Towns.Where(t => !t.OldWest).ToList();
        var oldWest = plan.Towns.Where(t => t.OldWest).ToList();
        foreach (var t in plan.Towns) { graph.Add(new()); }
        foreach (var t in plan.Towns.Where(t => !t.Node && !t.Linked && !t.Exits.Any(e => e.Cell >= 0)))
            plan.Problems.Add(Loc.F("Town at {0}: no free road exit at the town edge and no place on its own streets for a connection – it cannot be connected.", Pos(t.CX, t.CZ)));

        double Phase(double u) => mainStart + (0.99 - mainStart) * u;

        // 1. basic network: every city reachable (Prim with junctions)
        var start = asphalt.Where(t => t.Exits.Any(e => e.Cell >= 0)).OrderByDescending(t => t.Tiles.Count).FirstOrDefault();
        if (start != null)
        {
            start.Connected = true;
            while (true)
            {
                var left = asphalt.Where(t => !t.Connected && t.Exits.Any(e => e.Cell >= 0 && !e.Used)).ToList();
                if (left.Count == 0) break;
                int conn = asphalt.Count(t => t.Connected);
                double f = asphalt.Count > 1 ? Math.Clamp((double)conn / asphalt.Count, 0, 1) : 1;
                progress.Report(Loc.F("Connecting towns ({0} of {1}) …", conn, asphalt.Count), Phase(0.42 * f));
                if (!ConnectNext(left, false)) break;
            }
            foreach (var t in asphalt.Where(t => !t.Node && !t.Connected && t.Exits.Any(e => e.Cell >= 0)))
                plan.Problems.Add(Loc.F("Town at {0}: no way to the other towns was found (water, steep walls or other roads in the way).", Pos(t.CX, t.CZ)));
        }

        // 2. Old West places with gravel paths to the network
        var owList = oldWest.Where(t => t.Exits.Any(e => e.Cell >= 0)).ToList();
        for (int oi = 0; oi < owList.Count; oi++)
        {
            var t = owList[oi];
            progress.Report(Loc.T("Connecting Old West towns …"), Phase(0.42 + 0.05 * oi / Math.Max(1, owList.Count)));
            if (plan.Roads.Count == 0 && !plan.Towns.Any(o => o.Connected && !o.OldWest)) { plan.Problems.Add(Loc.F("Old West town at {0}: no road network to connect to.", Pos(t.CX, t.CZ))); continue; }
            if (!ConnectNext(new List<NetTown> { t }, true))
                plan.Problems.Add(Loc.F("Old West town at {0}: no way to the road network was found.", Pos(t.CX, t.CZ)));
        }

        // connections on town streets only for connecting the town, not for cross connections or feeders
        foreach (var t in plan.Towns)
            foreach (var e in t.Exits.Where(e => e.Street && !e.Used).ToList()) { e.Cell = -1; t.Exits.Remove(e); }

        // 3. cross connections
        progress.Report(Loc.T("Adding cross connections …"), Phase(0.48));
        AddLoops(asphalt);

        // 3b. river crossings: free bridge places as cross connections where they save a long way round
        progress.Report(Loc.T("Roads over rivers …"), Phase(0.62));
        if (!probing) AddRiverCrossings(asphalt);

        // 4. feeders: free city exits to roads that pass nearby
        progress.Report(Loc.T("Connecting free town exits to nearby roads …"), Phase(0.72));
        AddFeeders(asphalt);
        ConnectNodeExits();

        if (probing) { plan.Seconds = watch.Elapsed.TotalSeconds; return plan; }   // trial planning: junctions and crossings are known

        // 5. town connections: connect roads that pass close to town streets briefly
        progress.Report(Loc.T("Linking roads that pass close to towns …"), Phase(0.81));
        AddTownLinks(asphalt);

        // 5b. town exits near the map edge that found no connection: a road out to the edge
        progress.Report(Loc.T("Roads to the map edge …"), Phase(0.86));
        AddEdgeRoads();

        // 5c. whatever is still free: connected with relaxed rules (only what finds no way at all stays a dead end)
        progress.Report(Loc.T("Connecting the remaining town exits …"), Phase(0.90));
        ConnectRemaining();

        // 5d. post-check & repair: unused town exits whose approach was blocked by a passing road (connect directly if height and space fit)
        progress.Report(Loc.T("Checking and improving the network …"), Phase(0.96));
        RepairDeadEnds();

        // 6. node tiles: shape by the roads that use them (T, crossing, straight gateway, or dropped)
        FinishNodeTiles();

        foreach (var r in plan.Roads) Measure(r);
        double straight = 0, total = 0;
        foreach (var r in plan.Roads) { var (s, t) = Straightness(r.Samples); straight += s; total += t; }
        plan.StraightShare = total > 0 ? straight / total : 0;
        plan.Seconds = watch.Elapsed.TotalSeconds;
        return plan;
    }

    sealed class ProbeProgress : IWorkProgress
    {
        readonly IProgress<string> outer;
        readonly string text;
        readonly double from, span;
        public ProbeProgress(IProgress<string> outer, string text, double from, double to)
        {
            this.outer = outer; this.text = text; this.from = from; span = Math.Max(0, to - from);
        }
        public void Report(string value) => WorkCancel.ThrowIfRequested();
        public void Report(string _, double fraction) => outer.Report(text, from + span * Math.Clamp(fraction, 0, 1));
    }

    static string Pos(double x, double z) => Loc.F("{0} {1}, {2} {3}", Math.Abs(Math.Round(x)), x < 0 ? Loc.T("W") : Loc.T("E"), Math.Abs(Math.Round(z)), z < 0 ? Loc.T("S") : Loc.T("N"));

    // ---------------------------------------------------------------- grid

    void BuildGrid()
    {
        var h = new float[n];
        blocked = new bool[n];
        edge = new bool[n];
        roadBlock = new bool[n];
        nearRoadD = new float[n];
        Array.Fill(nearRoadD, float.MaxValue);
        nearRoadH = new float[n];
        reserveOwner = new int[n];
        Array.Fill(reserveOwner, -1);
        var water = new bool[n];
        var road = roadCell = new bool[n];
        waterCell = water;
        bool usePre = spec.BreakInWasteland && w.GetWastelandDamage() != null;
        Parallel.For(0, gw, j =>
        {
            for (int i = 0; i < gw; i++)
            {
                int x0 = -half + i * C, z0 = -half + j * C;
                double s = 0;
                bool wet = false, paved = false;
                for (int sz = 0; sz < 4; sz++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        int x = x0 + 2 + sx * 4, z = z0 + 2 + sz * 4;
                        s += usePre ? w.PreDamageHeightAt(x, z, true) : w.HeightAt(x, z);
                        wet |= w.WaterAt(x, z);
                        paved |= w.RoadAt(x, z) != 0;
                    }
                int g = j * gw + i;
                h[g] = (float)(s / 16);
                water[g] = wet;
                road[g] = paved;
                double cx = x0 + C / 2.0, cz = z0 + C / 2.0;
                if (cx < -half + 100 || cx > half - 100 || cz < -half + 100 || cz > half - 100) edge[g] = true;
            }
        });
        hb = Blur(h, 1);
        var large = Blur(hb, 6);
        var wide = Blur(hb, 12);
        hcut = new float[n];
        gradX = new float[n];
        gradZ = new float[n];
        mult = new float[n];
        int seed = new GeneratorSpec { Seed = spec.Seed + "/wander" }.SeedValue;
        double amp = spec.Curves switch { 0 => 0.15, 2 => 0.45, _ => 0.3 };
        Parallel.For(0, gw, j =>
        {
            for (int i = 0; i < gw; i++)
            {
                int g = j * gw + i;
                // clearing: ridges lowered up to 20 m (direction mean over about 200 m), valleys unchanged
                hcut[g] = hb[g] > large[g] ? Math.Max(hb[g] - 20, large[g]) : hb[g];
                int il = Math.Max(0, i - 1), ir = Math.Min(gw - 1, i + 1), jd = Math.Max(0, j - 1), ju = Math.Min(gw - 1, j + 1);
                gradX[g] = (hb[j * gw + ir] - hb[j * gw + il]) / ((ir - il) * C);
                gradZ[g] = (hb[ju * gw + i] - hb[jd * gw + i]) / ((ju - jd) * C);
                // valleys cheaper, hilltops more expensive; large-scale noise against long straights
                double tpi = Math.Clamp((hb[g] - wide[g]) / 25.0, -1, 1);
                double noise = 0.7 * RoadApplier.Octave(i, j, 600.0 / C, seed) + 0.3 * RoadApplier.Octave(i, j, 240.0 / C, seed + 7);
                mult[g] = (float)Math.Max(0.35, (1 + 0.2 * tpi) * (1 + amp * noise));
            }
        });
        // water and existing roads outside the tiles (+1 cell)
        var inTile = tileCell = new bool[n];
        foreach (var p in w.Prefabs.Where(p => p.Def != null && p.IsTile))
            Mark(inTile, p.X, p.Z, p.X + p.SizeX, p.Z + p.SizeZ, 0, centerOnly: true);
        for (int g = 0; g < n; g++)
        {
            bool isRoad = road[g] && !inTile[g];
            if (!water[g] && !isRoad) continue;
            int i = g % gw, j = g / gw, r = 1;
            for (int dj = -r; dj <= r; dj++)
                for (int di = -r; di <= r; di++)
                {
                    int a = i + di, b = j + dj;
                    if (di * di + dj * dj > 5 || a < 0 || b < 0 || a >= gw || b >= gw) continue;
                    if (water[g] && (Math.Abs(di) > 1 || Math.Abs(dj) > 1) && !isRoad) continue;
                    blocked[b * gw + a] = true;
                }
        }
        // existing roads outside the tiles (district and gateway roads of the cities) count for the surcharge "runs
        // alongside" like planned roads
        int pr = (int)Math.Ceiling(ParallelRange / C);
        for (int g = 0; g < n; g++)
        {
            if (!road[g] || inTile[g]) continue;
            int i = g % gw, j = g / gw;
            for (int dj = -pr; dj <= pr; dj++)
                for (int di = -pr; di <= pr; di++)
                {
                    int a = i + di, b = j + dj;
                    if (a < 0 || b < 0 || a >= gw || b >= gw) continue;
                    float d = (float)(C * Math.Sqrt(di * di + dj * dj));
                    int c = b * gw + a;
                    if (d < ParallelRange && d < nearRoadD[c]) { nearRoadD[c] = d; nearRoadH[c] = hb[g]; }
                }
        }
        // tiles and buildings
        foreach (var p in w.Prefabs.Where(p => p.Def != null))
        {
            int m = p.IsTile ? 24 : p.Def.Kind == PrefabKind.Part ? 4 : 12;
            Mark(blocked, p.X, p.Z, p.X + p.SizeX, p.Z + p.SizeZ, m);
        }
        // burnt-forest volcano: keep country roads off the cone, its prepared 5 m gravel ramp and both POI pads
        if (VolcanoSite.Load(w.Folder) is { } volcano)
        {
            void BlockCircle(double cx, double cz, double rad)
            {
                int i0 = Math.Max(0, (int)Math.Floor((cx - rad + half) / C));
                int i1 = Math.Min(gw - 1, (int)Math.Ceiling((cx + rad + half) / C));
                int j0 = Math.Max(0, (int)Math.Floor((cz - rad + half) / C));
                int j1 = Math.Min(gw - 1, (int)Math.Ceiling((cz + rad + half) / C));
                double r2 = rad * rad;
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        double wx = -half + (i + 0.5) * C, wz = -half + (j + 0.5) * C;
                        if ((wx - cx) * (wx - cx) + (wz - cz) * (wz - cz) <= r2) blocked[j * gw + i] = true;
                    }
            }
            void BlockCorridor(List<VolcanoPoint> line, double pad)
            {
                if (line == null || line.Count == 0) return;
                if (line.Count == 1) { BlockCircle(line[0].X, line[0].Z, pad); return; }
                for (int k = 1; k < line.Count; k++)
                {
                    var a = line[k - 1]; var b = line[k];
                    double dx = b.X - a.X, dz = b.Z - a.Z, len = Math.Sqrt(dx * dx + dz * dz);
                    int steps = Math.Max(1, (int)Math.Ceiling(len / (C * 0.5)));
                    for (int s = 0; s <= steps; s++)
                        BlockCircle(a.X + dx * s / steps, a.Z + dz * s / steps, pad);
                }
            }
            BlockCircle(volcano.CenterX, volcano.CenterZ, volcano.OuterRadius * 0.9);
            foreach (var pad in volcano.Pads) BlockCircle(pad.X, pad.Z, pad.Radius + 16);
            BlockCorridor(volcano.AccessPath, 16);
            BlockCorridor(volcano.RimConnector, 16);
        }
    }

    /// cell of the point a; if it is not usable, a usable neighbor whose center is within 12 m (ends often lie exactly on a cell border, so the
    /// cell is chosen by chance – Sodecu-Land 3136/-1193: the eastern one beside a lake was blocked, the western one free)
    int OpenCellNear(Point a, int c, Func<int, bool> ok)
    {
        if (c < 0 || ok(c)) return c;
        int gx = c % gw;
        var alt = new[] { c - 1, c + 1, c - gw, c + gw, c - gw - 1, c - gw + 1, c + gw - 1, c + gw + 1 }
            .Where(k => k >= 0 && k < n && Math.Abs(k % gw - gx) <= 1 && ok(k) && (Center(k) - a).Length <= 12)
            .OrderBy(k => (Center(k) - a).Length).ToList();
        return alt.Count > 0 ? alt[0] : c;
    }

    // cells that cover the rectangle (+ m) or whose center lies in it
    void Mark(bool[] f, double x0, double z0, double x1, double z1, double m, bool centerOnly = false)
    {
        x0 -= m; z0 -= m; x1 += m; z1 += m;
        double pad = centerOnly ? 0 : C / 2.0 - 0.01;
        int i0 = Math.Max(0, (int)Math.Floor((x0 - pad + half) / C - 0.5) - 1), i1 = Math.Min(gw - 1, (int)Math.Ceiling((x1 + pad + half) / C) + 1);
        int j0 = Math.Max(0, (int)Math.Floor((z0 - pad + half) / C - 0.5) - 1), j1 = Math.Min(gw - 1, (int)Math.Ceiling((z1 + pad + half) / C) + 1);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                double cx = -half + i * C + C / 2.0, cz = -half + j * C + C / 2.0;
                if (cx + pad > x0 && cx - pad < x1 && cz + pad > z0 && cz - pad < z1) f[j * gw + i] = true;
            }
    }

    float[] Blur(float[] src, int r)
    {
        var tmp = new float[n];
        var dst = new float[n];
        Parallel.For(0, gw, j =>
        {
            double s = 0; int c = 0;
            for (int k = 0; k <= Math.Min(gw - 1, r); k++) { s += src[j * gw + k]; c++; }
            for (int i = 0; i < gw; i++)
            {
                tmp[j * gw + i] = (float)(s / c);
                int add = i + r + 1, rem = i - r;
                if (add < gw) { s += src[j * gw + add]; c++; }
                if (rem >= 0) { s -= src[j * gw + rem]; c--; }
            }
        });
        Parallel.For(0, gw, i =>
        {
            double s = 0; int c = 0;
            for (int k = 0; k <= Math.Min(gw - 1, r); k++) { s += tmp[k * gw + i]; c++; }
            for (int j = 0; j < gw; j++)
            {
                dst[j * gw + i] = (float)(s / c);
                int add = j + r + 1, rem = j - r;
                if (add < gw) { s += tmp[add * gw + i]; c++; }
                if (rem >= 0) { s -= tmp[rem * gw + i]; c--; }
            }
        });
        return dst;
    }

    // ---------------------------------------------------------------- cities and exits

    static readonly (int DX, int DZ)[] FourDirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    void FindTowns(bool streetExits = true)
    {
        var tiles = w.Prefabs.Where(p => p.IsTile && p.Def != null).ToList();
        var parent = Enumerable.Range(0, tiles.Count).ToArray();
        int Root(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        // a city = adjoining tiles and tiles that are connected via roads (districts, gateway); distance alone is
        // not enough: two cities sometimes lie closer together than a district to the main part (Ninezo-Land)
        for (int a = 0; a < tiles.Count; a++)
            for (int b = a + 1; b < tiles.Count; b++)
                if (Gap(tiles[a], tiles[b]) <= 4) parent[Root(a)] = Root(b);
        var index = tiles.Select((t, k) => (t, k)).ToDictionary(p => p.t, p => p.k);
        const int TileBucket = 150;
        int tgw = (w.Size + TileBucket - 1) / TileBucket;
        var tileGrid = new List<int>[tgw * tgw];
        for (int k = 0; k < tiles.Count; k++)
        {
            var tk = tiles[k];
            int i0 = Math.Clamp((tk.X + half) / TileBucket, 0, tgw - 1);
            int i1 = Math.Clamp((tk.X + tk.SizeX + half) / TileBucket, 0, tgw - 1);
            int j0 = Math.Clamp((tk.Z + half) / TileBucket, 0, tgw - 1);
            int j1 = Math.Clamp((tk.Z + tk.SizeZ + half) / TileBucket, 0, tgw - 1);
            for (int gj = j0; gj <= j1; gj++)
                for (int gi = i0; gi <= i1; gi++)
                    (tileGrid[gj * tgw + gi] ??= new()).Add(k);
        }
        int TileAt(int x, int z)
        {
            int rx = x + half, rz = z + half;
            if ((uint)rx >= (uint)w.Size || (uint)rz >= (uint)w.Size) return -1;
            var l = tileGrid[(rz / TileBucket) * tgw + rx / TileBucket];
            if (l == null) return -1;
            for (int m = 0; m < l.Count; m++) if (tiles[l[m]].Contains(x + 0.5, z + 0.5)) return l[m];
            return -1;
        }
        if (w.TileExits.Count > 0)
        {
            const int BfsR = 900, BfsSpan = 2 * BfsR + 1;
            ushort[] visited = null;
            ushort stamp = 0;
            var queue = new Queue<(int X, int Z)>();
            foreach (var (x, z, tile) in w.TileExits)
            {
                if (!index.TryGetValue(tile, out int origin)) continue;
                var (dx, dz) = Outward(tile, x, z);
                int sx = (int)Math.Floor(x + dx * 2), sz = (int)Math.Floor(z + dz * 2);
                if (w.RoadAt(sx, sz) == 0) continue;
                int tStart = TileAt(sx, sz);
                if (tStart >= 0) { if (tStart != origin) parent[Root(origin)] = Root(tStart); continue; }
                visited ??= new ushort[BfsSpan * BfsSpan];
                if (++stamp == 0) { Array.Clear(visited); stamp = 1; }
                // follow the surface (at most 900 m; district roads wind, Ninezo-Land: 630 m)
                visited[BfsR * BfsSpan + BfsR] = stamp;
                int seenCount = 1;
                queue.Clear();
                queue.Enqueue((sx, sz));
                while (queue.Count > 0 && seenCount < 250000)
                {
                    var (cx, cz) = queue.Dequeue();
                    for (int d = 0; d < 4; d++)
                    {
                        int ax = cx + FourDirs[d].DX, az = cz + FourDirs[d].DZ;
                        int ox = ax - sx, oz = az - sz;
                        if (Math.Abs(ox) + Math.Abs(oz) > BfsR || w.RoadAt(ax, az) == 0) continue;
                        int vi = (oz + BfsR) * BfsSpan + (ox + BfsR);
                        if (visited[vi] == stamp) continue;
                        visited[vi] = stamp;
                        seenCount++;
                        int other = TileAt(ax, az);
                        if (other == origin) continue;
                        if (other >= 0) { parent[Root(origin)] = Root(other); continue; }
                        queue.Enqueue((ax, az));
                    }
                }
            }
        }
        foreach (var grp in Enumerable.Range(0, tiles.Count).GroupBy(Root))
        {
            var t = new NetTown { Id = plan.Towns.Count };
            t.Tiles.AddRange(grp.Select(k => tiles[k]));
            t.X0 = t.Tiles.Min(p => p.X); t.Z0 = t.Tiles.Min(p => p.Z);
            t.X1 = t.Tiles.Max(p => p.X + p.SizeX); t.Z1 = t.Tiles.Max(p => p.Z + p.SizeZ);
            t.OldWest = t.Tiles.Count(p => p.Name.Contains("_oldwest_", StringComparison.OrdinalIgnoreCase)) * 2 > t.Tiles.Count;
            plan.Towns.Add(t);
        }
        var townOf = new Dictionary<PlacedPrefab, NetTown>();
        foreach (var t in plan.Towns) foreach (var p in t.Tiles) townOf[p] = t;

        // block the edge strip 100 m – except up to 160 m in front of city exits near the edge, so that the road can make a curve there instead of
        // a right-angled kink (city at the south edge of Zutico-Land). There too the center line stays at least
        // EdgeCenter from the edge (the user's request 2026-11-08: roads not closer than 80 m to the map edge; before up to 30 m)
        var exitPoints = w.TileExits.Select(e => new Point(e.X, e.Z)).Where(p => half - Math.Max(Math.Abs(p.X), Math.Abs(p.Y)) < 260).ToList();
        for (int g = 0; g < n; g++)
        {
            if (!edge[g]) continue;
            var c = Center(g);
            bool relax = half - Math.Max(Math.Abs(c.X), Math.Abs(c.Y)) >= EdgeCenter + C / 2.0 && exitPoints.Any(p => (p - c).LengthSquared < 160 * 160);
            if (!relax) blocked[g] = true;
        }

        var pavedAt = new List<(NetTown Town, int X, int Z)>();   // surface in front of exits (start of LeadsAway)
        foreach (var (x, z, tile) in w.TileExits)
        {
            if (!townOf.TryGetValue(tile, out var town)) continue;
            var (dx, dz) = Outward(tile, x, z);
            // neighboring tile in front?
            if (tiles.Any(o => o != tile && o.Contains(x + dx * 3, z + dz * 3))) continue;
            var (width, total, surface) = w.TileExitWidth(tile, x, z);
            if (width <= 0) continue;
            // already a road in front (district, gateway or own road)?
            double px = -dz, pz = dx;
            bool paved = false;
            for (double d = 3; d <= 14 && !paved; d += 1)
                for (double l = -(total / 2.0 - 1); l <= total / 2.0 - 1 && !paved; l += 1)
                {
                    int ix = (int)Math.Floor(x + dx * d + px * l), iz = (int)Math.Floor(z + dz * d + pz * l);
                    paved = w.RoadAt(ix, iz) != 0;
                    if (paved) pavedAt.Add((town, ix, iz));
                }
            if (paved) continue;
            var e = new NetExit
            {
                Town = town, Tile = tile, X = x, Z = z, DX = dx, DZ = dz, Width = width, Gravel = surface == WorldData.Gravel,
                Gateway = tile.Name.Contains("gateway", StringComparison.OrdinalIgnoreCase),
            };
            var a = e.Approach;
            int c = CellOf(a.X, a.Y);
            // the exit often lies exactly on a cell border, so the cell of the approach point is chosen by chance (Sodecu-Land 3136/-1193: the
            // eastern one lay beside a lake and was blocked, the western one was free): then take a free cell whose center is within 12 m
            c = OpenCellNear(a, c, Passable);
            // free in front of the exit (straight piece up to the cell)?
            bool free = Passable(c);
            for (double d = 8; d < ApproachLength && free; d += 4)
            {
                int ix = (int)Math.Floor(x + dx * d), iz = (int)Math.Floor(z + dz * d);
                free = !w.WaterAt(ix, iz) && !tiles.Any(o => o.Contains(ix, iz));
                // no existing road across in front of the exit (Wahava-Land: district road 40 m in front of the exit, the straight
                // connecting piece ran over it without height adjustment – jump of 4 m)
                for (double l = -10; l <= 10 && free && d >= 15; l += 2)
                    free = w.RoadAt((int)Math.Floor(x + dx * d + px * l), (int)Math.Floor(z + dz * d + pz * l)) == 0;
            }
            if (free) e.Cell = c;
            else
            {
                // reason for the red point (shown by the "Close dead end" tool)
                var ac = c >= 0 ? Center(c) : a;
                e.Why = c < 0 ? Loc.T("the point 44 m in front of the exit is outside the map")
                    : edge[c] ? Loc.T("the point 44 m in front of the exit is too close to the map edge")
                    : roadBlock[c] ? Loc.T("a planned road is in the way")
                    : blocked[c] ? Loc.T("water, a road or a building is at the point 44 m in front of the exit")
                    : Loc.T("water, a tile or a road lies in front of the exit");
                e.Why += $" ({ac.X:0}, {ac.Y:0})";
            }
            town.Exits.Add(e);
        }
        // towns without a usable exit (the user's request 2026-11-12, stage 3 of "generate roads"): already joined to roads that lead
        // away (built network) = connected; otherwise places on its own town streets (between main part and districts) as exits
        // (only needed by Plan; ForEditing/ForRoutes ignore Street exits and Linked)
        if (streetExits)
        {
            var scan = new RoadScan(w);
            foreach (var t in plan.Towns)
            {
                if (t.Exits.Any(e => e.Cell >= 0)) continue;
                if (LeadsAway(t, pavedAt.Where(p => p.Town == t).Select(p => (p.X, p.Z)), tiles, TileAt, townOf)) { t.Linked = t.Connected = true; continue; }
                AddStreetExits(t, scan, tiles, false);
                if (!t.Exits.Any(e => e.Cell >= 0)) AddStreetExits(t, scan, tiles, true);   // no flat place: also ramps (tilted junction)
            }
        }
        nearTown = new bool[n];
        foreach (var t in plan.Towns) Mark(nearTown, t.X0, t.Z0, t.X1, t.Z1, 100);
        // keep the room in front of the free exits free if possible (later roads there): up to 110 m more expensive, up to 36 m
        // (the doorway in front of the 44 m approach cell) always blocked so no road scrapes right past a tile exit
        reserveNear = new bool[n];
        foreach (var e in plan.Towns.SelectMany(t => t.Exits).Where(e => e.Cell >= 0))
        {
            exitList.Add(e);
            double px = -e.DZ, pz = e.DX;
            for (double d = 8; d <= 110; d += 4)
                for (double l = -16; l <= 16; l += 4)
                {
                    int c = CellOf(e.X + e.DX * d + px * l, e.Z + e.DZ * d + pz * l);
                    if (c < 0) continue;
                    reserveOwner[c] = exitList.Count - 1;
                    if (d <= 36 && c != e.Cell) reserveNear[c] = true;
                }
        }
        foreach (var e in exitList) if (e.Cell >= 0) reserveNear[e.Cell] = false;
    }

    const double AwayDistance = 300;   // a road that reaches this far beyond the town area (or another town) leads away
    const double StreetExitGrade = 0.05;   // exits on town streets only where the street is at most this steep

    /// does the surface in front of the exits lead away from the town (existing road network)? Follows the road pixels outside the
    /// town's own tiles; district roads stay inside or close to the town area
    bool LeadsAway(NetTown t, IEnumerable<(int X, int Z)> starts, List<PlacedPrefab> tiles, Func<int, int, int> tileAt, Dictionary<PlacedPrefab, NetTown> townOf)
    {
        var seen = new HashSet<long>();
        var queue = new Queue<(int X, int Z)>();
        foreach (var s in starts) if (seen.Add(((long)s.X << 32) ^ (uint)s.Z)) queue.Enqueue(s);
        while (queue.Count > 0 && seen.Count < 400000)
        {
            var (cx, cz) = queue.Dequeue();
            double ox = Math.Max(0, Math.Max(t.X0 - cx, cx - t.X1)), oz = Math.Max(0, Math.Max(t.Z0 - cz, cz - t.Z1));
            if (ox * ox + oz * oz > AwayDistance * AwayDistance) return true;
            for (int d = 0; d < 4; d++)
            {
                int ax = cx + FourDirs[d].DX, az = cz + FourDirs[d].DZ;
                if (w.RoadAt(ax, az) == 0 || !seen.Add(((long)ax << 32) ^ (uint)az)) continue;
                int hit = tileAt(ax, az);
                if (hit >= 0)
                {
                    if (townOf[tiles[hit]] == t) continue;
                    return true;   // reaches another town
                }
                queue.Enqueue((ax, az));
            }
        }
        return false;
    }

    /// exits on the town's own streets outside the tiles (town without a free tile exit): straight places of the street, the
    /// new road leaves at a right angle; at most 6, at least 80 m apart, those pointing away from the town first
    void AddStreetExits(NetTown t, RoadScan scan, List<PlacedPrefab> tiles, bool relaxed)
    {
        var nearTiles = tiles.Where(q => q.X + q.SizeX > t.X0 - AwayDistance - 100 && q.X < t.X1 + AwayDistance + 100
            && q.Z + q.SizeZ > t.Z0 - AwayDistance - 100 && q.Z < t.Z1 + AwayDistance + 100).ToList();
        double TileGap(Point p) => nearTiles.Count == 0 ? double.MaxValue : nearTiles.Min(q =>
        {
            double dx = Math.Max(0, Math.Max(q.X - p.X, p.X - (q.X + q.SizeX))), dz = Math.Max(0, Math.Max(q.Z - p.Y, p.Y - (q.Z + q.SizeZ)));
            return Math.Sqrt(dx * dx + dz * dz);
        });
        var center = new Point(t.CX, t.CZ);
        var near = w.Prefabs.Where(p => p.Def != null && p.X + p.SizeX > t.X0 - AwayDistance - 100 && p.X < t.X1 + AwayDistance + 100
            && p.Z + p.SizeZ > t.Z0 - AwayDistance - 100 && p.Z < t.Z1 + AwayDistance + 100).ToList();
        var found = new List<(NetExit Exit, double Score)>();
        var spots = new List<Point>();
        int i0 = Math.Max(0, (int)Math.Floor((t.X0 - AwayDistance + half) / C));
        int i1 = Math.Min(gw - 1, (int)Math.Ceiling((t.X1 + AwayDistance + half) / C));
        int j0 = Math.Max(0, (int)Math.Floor((t.Z0 - AwayDistance + half) / C));
        int j1 = Math.Min(gw - 1, (int)Math.Ceiling((t.Z1 + AwayDistance + half) / C));
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                int g = j * gw + i;
                if (!roadCell[g] || tileCell[g]) continue;
                var c = Center(g);
                if (TownDistanceTo(t, c) > AwayDistance || plan.Towns.Any(o => o != t && TownDistanceTo(o, c) < TownDistanceTo(t, c))) continue;
                var spot = RoadFollower.Center(scan, c);
                if (spot == null || spot.Width < (t.OldWest ? 4 : 6) || spot.Width > 24 || (!t.OldWest && spot.Kind != WorldData.Asphalt)) continue;
                if (spots.Any(s => (s - spot.Center).Length < 20) || TileGap(spot.Center) < 30) continue;
                spots.Add(spot.Center);
                var a1 = RoadFollower.Axis(scan, spot.Center + spot.Dir * 15, 7);
                var a2 = RoadFollower.Axis(scan, spot.Center - spot.Dir * 15, 7);
                if (a1 == null || a2 == null || Math.Abs(Vector.Multiply(a1.Value, spot.Dir)) < 0.94 || Math.Abs(Vector.Multiply(a2.Value, spot.Dir)) < 0.94) continue;
                // flat place of the street: streets between main part and districts are mostly ramps – a junction there would be tilted
                double H(Point q) => w.HeightAt((int)Math.Floor(q.X), (int)Math.Floor(q.Y));
                double grade = Math.Abs(H(spot.Center + spot.Dir * 12) - H(spot.Center - spot.Dir * 12)) / 24;
                if (grade > (relaxed ? 2 : 1) * StreetExitGrade) continue;
                var normal = new Vector(-spot.Dir.Y, spot.Dir.X);
                foreach (int side in new[] { -1, 1 })
                {
                    var d = normal * side;
                    var e = new NetExit
                    {
                        Town = t, X = spot.Center.X, Z = spot.Center.Y, DX = d.X, DZ = d.Y, Street = true, Gravel = spot.Kind == WorldData.Gravel,
                        Width = (int)Math.Round(Math.Clamp(spot.Width - 2, t.OldWest ? 4 : 6, 16)),
                        StreetHalf = spot.Width / 2, StreetDX = spot.Dir.X, StreetDZ = spot.Dir.Y,
                    };
                    if (!StreetExitFree(e, near, relaxed)) continue;
                    var away = spot.Center - center;
                    double score = away.Length > 1 ? Vector.Multiply(d, away / away.Length) : 0;   // pointing away from the town: better
                    found.Add((e, score + away.Length / 1000 - grade * 10));   // flatter places first
                }
            }
        foreach (var (e, _) in found.OrderByDescending(f => f.Score))
        {
            if (t.Exits.Count(x => x.Street) >= 6) break;
            if (t.Exits.Any(x => x.Street && (new Point(x.X, x.Z) - new Point(e.X, e.Z)).Length < 80)) continue;
            e.Cell = CellOf(e.Approach.X, e.Approach.Y);
            t.Exits.Add(e);
        }
    }

    /// room for the straight connecting piece of a street exit: no water, road, building or tile beside it, cell passable
    bool StreetExitFree(NetExit e, List<PlacedPrefab> near, bool relaxed)
    {
        var a = e.Approach;
        int c = CellOf(a.X, a.Y);
        if (!Passable(c)) return false;
        double px = -e.DZ, pz = e.DX;
        double h0 = w.HeightAt((int)Math.Floor(e.X), (int)Math.Floor(e.Z));
        for (double d = e.StreetHalf + 2; d <= ApproachLength + 8; d += 2)
            for (double l = -8; l <= 8; l += 4)
            {
                double x = e.X + e.DX * d + px * l, z = e.Z + e.DZ * d + pz * l;
                int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
                if (w.WaterAt(ix, iz) || w.RoadAt(ix, iz) != 0) return false;
                // ground beside the connecting piece: not a slope or wall (the road leaves the street at its height)
                if (Math.Abs(w.HeightAt(ix, iz) - h0) > (relaxed ? 3 : 1.5) + (d - e.StreetHalf) * (relaxed ? spec.MaxGrade : spec.Grade)) return false;
                foreach (var p in near)
                {
                    double m = p.IsTile ? 10 : 4;
                    if (x > p.X - m && x < p.X + p.SizeX + m && z > p.Z - m && z < p.Z + p.SizeZ + m) return false;
                }
            }
        return true;
    }

    bool[] reserveNear;

    /// exit still usable: free and no planned road at its connecting piece
    bool Open(NetExit e) => !e.Used && e.Cell >= 0 && Passable(e.Cell);

    // direction outwards at an exit (middle of a tile side)
    static (double DX, double DZ) Outward(PlacedPrefab tile, double x, double z) =>
        Math.Abs(z - tile.Z) < 0.6 ? (0, -1) : Math.Abs(z - (tile.Z + tile.SizeZ)) < 0.6 ? (0, 1) : Math.Abs(x - tile.X) < 0.6 ? (-1, 0) : (1, 0);

    static double Gap(PlacedPrefab a, PlacedPrefab b)
    {
        double dx = Math.Max(0, Math.Max(a.X - (b.X + b.SizeX), b.X - (a.X + a.SizeX)));
        double dz = Math.Max(0, Math.Max(a.Z - (b.Z + b.SizeZ), b.Z - (a.Z + a.SizeZ)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    double TownDistance(double x, double z) => plan.Towns.Count == 0 ? double.MaxValue : plan.Towns.Min(t =>
    {
        double dx = Math.Max(0, Math.Max(t.X0 - x, x - t.X1)), dz = Math.Max(0, Math.Max(t.Z0 - z, z - t.Z1));
        return Math.Sqrt(dx * dx + dz * dz);
    });

    // ---------------------------------------------------------------- junctions and intersections

    /// place on a planned road where a new road can branch off at a right angle (Cell = cell 44 m beside it)
    sealed record Tap(NetRoad Road, int K, Point P, int Cell, int Side, int Dir)
    {
        public int Arrive => (Dir + 8) % Dirs;   // driving direction with which a road arrives here (towards the road)
    }

    List<Tap> taps = new();
    // intersection: from cell (Key) in direction Dir straight across the road to To
    Dictionary<int, List<(int To, float Cost, Point Mid, NetRoad Road, int K, int Dir)>> jumps = new();

    const int Dirs = 16;   // driving directions; direction k = k · 22.5° (0 = east, 4 = north), matching Steps
    static int DirOf(double dx, double dz) => ((int)Math.Round(Math.Atan2(dz, dx) / (Math.PI / 8)) % Dirs + Dirs) % Dirs;
    static int DirDiff(int a, int b) { int d = Math.Abs(a - b) % Dirs; return Math.Min(d, Dirs - d); }

    void BuildTaps(double townGap = TownGap, bool relaxed = false)
    {
        double spacing = relaxed ? RelaxedNodeSpacing : NodeSpacing, gradeLimit = relaxed ? 1.6 * JunctionGrade : JunctionGrade;
        taps = new List<Tap>();
        jumps = new();
        AddStreetJumps();
        AddBridgeJumps();
        foreach (var r in plan.Roads)
        {
            var s = r.Samples;
            for (int k = 3; k + 3 < s.Count; k++)
            {
                var p = s[k];
                if (nodesUsed.Any(q => (q - p).Length < spacing)) continue;
                if (TownDistance(p.X, p.Y) < townGap) continue;
                Vector a = s[k] - s[k - 3], b = s[k + 3] - s[k];
                if (a.Length < 1 || b.Length < 1 || Math.Abs(Vector.AngleBetween(a, b)) > 25) continue;
                // not on a steep stretch: the junction lies in the plane of the road, it would be tilted as much (measured 2026-11-12: the few
                // tilted junctions of 10–16 % all lay where the main road itself was that steep)
                if (Math.Abs(Sample(hb, s[k + 3].X, s[k + 3].Y) - Sample(hb, s[k - 3].X, s[k - 3].Y)) > gradeLimit * (s[k + 3] - s[k - 3]).Length) continue;
                var d = s[k + 3] - s[k - 3]; d.Normalize();
                var nrm = new Vector(-d.Y, d.X);
                double hp = Sample(hb, p.X, p.Y);
                int[] side = new int[2];
                for (int q = 0; q < 2; q++)
                {
                    int sg = q == 0 ? 1 : -1;
                    var ap = p + nrm * (sg * ApproachLength);
                    int c = CellOf(ap.X, ap.Y);
                    side[q] = -1;
                    if (!Passable(c) || Math.Abs(Sample(hb, ap.X, ap.Y) - hp) > ApproachLength * spec.Grade * 1.5) continue;
                    side[q] = c;
                    if (!r.Gravel) taps.Add(new Tap(r, k, p, c, sg, DirOf(nrm.X * sg, nrm.Y * sg)));
                }
                // intersection: from one side straight to the other
                if (side[0] >= 0 && side[1] >= 0)
                {
                    float cost = (float)(2 * ApproachLength + CrossPenalty);
                    if (!jumps.TryGetValue(side[0], out var l0)) jumps[side[0]] = l0 = new();
                    if (!jumps.TryGetValue(side[1], out var l1)) jumps[side[1]] = l1 = new();
                    l0.Add((side[1], cost, p, r, k, DirOf(-nrm.X, -nrm.Y)));
                    l1.Add((side[0], cost, p, r, k, DirOf(nrm.X, nrm.Y)));
                }
            }
        }
    }

    // ---------------------------------------------------------------- path search on the grid

    // state = cell · 16 + driving direction: per step the road may turn at most 22.5° (the user's hint 2026-11-07:
    // without driving direction the search turned arbitrarily at every 16 m step, dense zigzags arose on slopes). Smallest curve thus
    // about 40–90 m radius, a hairpin needs about 150 m width.
    float[] dist;
    int[] parent;
    int[] source;   // number of the source from which a state was reached
    /// lower bound of the remaining cost per cell up to the nearest target (null = plain Dijkstra), see GoalHeuristic
    float[] heur;
    float minMult = -1;

    /// A* lower bound for a search to these target cells (measured 2026-11-12: "Connecting towns" took 73 % of the planning,
    /// because the search spread in all directions from the whole network). Every step costs at least its length × the smallest
    /// cost factor of the map (all other surcharges are ≥ 1), so straight-line distance × that factor never overestimates –
    /// the search still finds the cheapest path, it only looks at far fewer cells. Distance as a two-pass chamfer
    /// transform (1, 4/3 per cell), scaled by 0.95 so that knight moves (√5 against 7/3) never exceed their real length.
    float[] GoalHeuristic(IEnumerable<int> goalCells)
    {
        if (minMult < 0) { float m = float.MaxValue; foreach (float v in mult) m = Math.Min(m, v); minMult = m; }
        var d = new float[n];
        Array.Fill(d, float.MaxValue);
        bool any = false;
        foreach (int c in goalCells) if (c >= 0) { d[c] = 0; any = true; }
        if (!any) return null;
        const float D = 4f / 3;
        for (int j = 0; j < gw; j++)
            for (int i = 0; i < gw; i++)
            {
                int g = j * gw + i; float v = d[g];
                if (i > 0) v = Math.Min(v, d[g - 1] + 1);
                if (j > 0)
                {
                    v = Math.Min(v, d[g - gw] + 1);
                    if (i > 0) v = Math.Min(v, d[g - gw - 1] + D);
                    if (i < gw - 1) v = Math.Min(v, d[g - gw + 1] + D);
                }
                d[g] = v;
            }
        for (int j = gw - 1; j >= 0; j--)
            for (int i = gw - 1; i >= 0; i--)
            {
                int g = j * gw + i; float v = d[g];
                if (i < gw - 1) v = Math.Min(v, d[g + 1] + 1);
                if (j < gw - 1)
                {
                    v = Math.Min(v, d[g + gw] + 1);
                    if (i < gw - 1) v = Math.Min(v, d[g + gw + 1] + D);
                    if (i > 0) v = Math.Min(v, d[g + gw - 1] + D);
                }
                d[g] = v;
            }
        float k = 0.95f * C * minMult;
        for (int g = 0; g < n; g++) d[g] *= k;
        return d;
    }

    /// Quick test whether no target state can be reached from the sources (measured 2026-11-12: a search without a reachable
    /// target looks at the whole map, about 2.8 million states and 1 s on 8 km; in "Connecting towns" that happened 12 times,
    /// mostly at exits that can only be reached with a stronger kink). Searches backwards from the targets over the same moves as
    /// Search (cells passable, knight moves without corner cutting, forecourts of other exits, junctions without the "own road"
    /// rule – a little more than Search allows, so "unreachable" is always right). Enclosed targets are recognized after a few
    /// steps; if more than cap states are reachable backwards, the answer is "don't know" (false) and the normal search runs.
    bool Unreachable(IEnumerable<int> goalStates, List<(int Cell, int Dir, float Cost)> sources, int cap = 60000)
    {
        var src = new HashSet<int>();
        foreach (var (c, dir, _) in sources) if (c >= 0) src.Add(c * Dirs + dir);
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (int s in goalStates) if (seen.Add(s)) { if (src.Contains(s)) return false; queue.Enqueue(s); }
        // junctions backwards: target cell → (start cell, driving direction of the jump)
        var back = new Dictionary<int, List<(int From, int Dir)>>();
        foreach (var (from, list) in jumps)
            foreach (var jp in list)
            {
                if (!back.TryGetValue(jp.To, out var l)) back[jp.To] = l = new();
                l.Add((from, jp.Dir));
            }
        void Visit(int s, ref bool hit) { if (seen.Add(s)) { if (src.Contains(s)) hit = true; queue.Enqueue(s); } }
        while (queue.Count > 0)
        {
            if (seen.Count > cap) return false;
            int st = queue.Dequeue();
            int g = st / Dirs, dir = st % Dirs;
            bool hit = false;
            // normal step into g with driving direction dir: came from g − Steps[dir] with direction dir − turn
            if (Passable(g) && !reserveNear[g])
            {
                var (di, dj) = Steps[dir];
                int a = g % gw, b = g / gw, i = a - di, j = b - dj;
                if (i >= 0 && j >= 0 && i < gw && j < gw)
                {
                    bool ok = true;
                    if (Math.Abs(di) == 2 || Math.Abs(dj) == 2)
                    {
                        int si = Math.Sign(di), sj = Math.Sign(dj);
                        int m1 = Math.Abs(di) == 2 ? j * gw + i + si : (j + sj) * gw + i;
                        int m2 = Math.Abs(di) == 2 ? (j + dj) * gw + i + si : (j + sj) * gw + i + di;
                        ok = Passable(m1) && Passable(m2);
                    }
                    if (ok)
                        for (int turn = -1; turn <= 1; turn++) Visit((j * gw + i) * Dirs + (dir - turn + Dirs) % Dirs, ref hit);
                }
            }
            // jump over a road into g
            if (back.TryGetValue(g, out var froms))
                foreach (var (from, jd) in froms)
                    if (jd == dir)
                        for (int t = -1; t <= 1; t++) Visit(from * Dirs + (dir + t + Dirs) % Dirs, ref hit);
            if (hit) return false;
        }
        return true;
    }

    /// Dijkstra from the sources (cell, driving direction); stop = state at which to stop (true), maxCost = limit;
    /// sourceRoads[q] = road at which source q branches off (must not be crossed). With heur set: A* (same result, fewer cells).
    int Search(List<(int Cell, int Dir, float Cost)> sources, Func<int, bool> stop, bool cut, float maxCost = float.MaxValue, NetRoad[] sourceRoads = null, bool noCross = false)
    {
        dist ??= new float[n * Dirs];
        parent ??= new int[n * Dirs];
        source ??= new int[n * Dirs];
        Array.Fill(dist, float.MaxValue);
        var h = cut ? hcut : hb;
        var est = heur;
        var queue = new PriorityQueue<int, float>();
        for (int q = 0; q < sources.Count; q++)
        {
            var (c, dir, cost) = sources[q];
            if (c < 0) continue;
            int s = c * Dirs + dir;
            if (cost >= dist[s]) continue;
            dist[s] = cost; parent[s] = -1; source[s] = q;
            queue.Enqueue(s, est == null ? cost : cost + est[c]);
        }
        double G = spec.Grade, Gm = Math.Max(spec.MaxGrade, G + 0.01);
        while (queue.TryDequeue(out int st, out float f))
        {
            float d = dist[st];
            if (f > (est == null ? d : d + est[st / Dirs])) continue;   // outdated entry (a cheaper way was found meanwhile)
            if (stop != null && stop(st)) return st;
            if (d > maxCost) break;
            int g = st / Dirs, dir = st % Dirs;
            int i = g % gw, j = g / gw;
            for (int turn = -1; turn <= 1; turn++)
            {
                int ndir = (dir + turn + Dirs) % Dirs;
                var (di, dj) = Steps[ndir];
                int a = i + di, b = j + dj;
                if (a < 0 || b < 0 || a >= gw || b >= gw) continue;
                int ng = b * gw + a;
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
                float nd = (float)(d + cost);
                int ns = ng * Dirs + ndir;
                if (nd < dist[ns]) { dist[ns] = nd; parent[ns] = st; source[ns] = source[st]; queue.Enqueue(ns, est == null ? nd : nd + est[ns / Dirs]); }
            }
            // intersection: only roughly at a right angle across the road
            if (jumps.TryGetValue(g, out var list))
                foreach (var (to, cost, _, jr, jk, jd) in list)
                {
                    if (DirDiff(dir, jd) > 1) continue;
                    if (sourceRoads != null && jr != null && sourceRoads[source[st]] == jr) continue;
                    if (noCross && jk >= -1) continue;   // feeders: never across a planned road or town street (bridges stay allowed)
                    float nd = d + cost;
                    int ns = to * Dirs + jd;
                    if (nd < dist[ns]) { dist[ns] = nd; parent[ns] = st; source[ns] = source[st]; queue.Enqueue(ns, est == null ? nd : nd + est[ns / Dirs]); }
                }
        }
        return -1;
    }

    /// cost of one step g → ng (di/dj cells, turn = change of the driving direction); NaN = not allowed (forecourt of a
    /// foreign exit). Shared by the search of the planner and rerouting by hand.
    double StepCost(int g, int ng, int di, int dj, int turn, float[] h, double G, double Gm)
    {
        double len = C * Math.Sqrt(di * di + dj * dj);
        double grade = Math.Abs(h[ng] - h[g]) / len;
        // turning steps count steeper: curves preferably where the terrain is flat (the builder flattens tight curves, see RoadPlanner.CurveFactors)
        if (turn != 0) grade *= 1.25;
        double cost = len * (turn != 0 ? 1.02 : 1);
        if (grade > 0.6 * G) { double t = (Math.Min(grade, G) - 0.6 * G) / (0.4 * G); cost += len * t * t; }
        if (grade > G) { double t = (Math.Min(grade, Gm) - G) / (Gm - G); cost += len * (2 + 6 * t * t); }
        if (grade > Gm) cost += len * (8 + 400 * (grade - Gm));
        // cross slope (slope roads are possible but cost earthwork) and rock walls
        double ux = di * C / len, uz = dj * C / len;
        double gx = gradX[ng], gz = gradZ[ng];
        double cross = Math.Abs(-gx * uz + gz * ux);
        if (cross > 0.18) cost += len * 6 * (cross - 0.18);
        double slope = Math.Sqrt(gx * gx + gz * gz);
        if (slope > 0.7) cost += len * 10 * (slope - 0.7);
        cost *= 0.5 * (mult[g] + mult[ng]);
        if (reserveNear[ng]) return double.NaN;
        bool own = reserveOwner[ng] >= 0 && allowed.Contains(exitList[reserveOwner[ng]]);
        if (reserveOwner[ng] >= 0 && !own) cost *= 4;
        else if (nearTown[ng] && !own) cost *= 1.8;
        // alongside a planned road (up to 160 m, similar height): more expensive, the closer and the more level, the more; with a
        // large height difference (slope road above the other) no surcharge
        if (nearRoadD[ng] < ParallelRange)
        {
            double closeness = 1 - Math.Clamp((nearRoadD[ng] - RoadBuffer) / (ParallelRange - RoadBuffer), 0, 1);
            double level = 1 - Math.Clamp(Math.Abs(h[ng] - nearRoadH[ng]) / ParallelHeight, 0, 1);
            cost *= 1 + 1.5 * closeness * closeness * level;   // 50 m: × 2, 100 m: × 1.3, 140 m: × 1.03
        }
        return cost;
    }

    /// cheapest arrival in a cell with roughly this driving direction: ±22.5° free, up to ±45° for a surcharge
    /// (right-angled forced gave curls in front of junctions): (cost incl. surcharge, state)
    (float Cost, int State) Arrival(int cell, int dir)
    {
        float best = float.MaxValue; int state = -1;
        for (int t = -2; t <= 2; t++)
        {
            int s = cell * Dirs + (dir + t + Dirs) % Dirs;
            if (dist[s] == float.MaxValue) continue;
            float c = dist[s] + (Math.Abs(t) == 2 ? 60 : 0);
            if (c < best) { best = c; state = s; }
        }
        return (best, state);
    }

    /// path curls in or crosses itself: (1) within 24 steps more than about 250° in the same direction, or
    /// (2) the traced path visits the same cell twice, comes within 24 m of an earlier non-adjacent point, or crosses itself
    bool Curly(int state)
    {
        var dirs = new List<int>();
        var pts = new List<Point>();
        var seen = new HashSet<int>();
        for (int s = state; s >= 0; s = parent[s])
        {
            int c = s / Dirs;
            if (!seen.Add(c)) return true;
            dirs.Add(s % Dirs);
            pts.Add(Center(c));
        }
        for (int a = 0; a + 1 < dirs.Count; a++)
        {
            int sum = 0;
            for (int b = a; b + 1 < dirs.Count && b < a + 24; b++)
            {
                sum += ((dirs[b] - dirs[b + 1] + Dirs + Dirs / 2) % Dirs) - Dirs / 2;
                if (Math.Abs(sum) > 11) return true;
            }
        }
        return HasSelfLoop(pts);
    }

    /// Does a traced path cross itself or come within 24 m of an earlier non-adjacent point (e.g. a wide 360° loop)?
    /// Uses cumulative arc length (>= 56 m) in addition to index distance so dense 5 m splines (RoadLine.Fit) are never falsely rejected!
    internal static bool HasSelfLoop(List<Point> pts)
    {
        if (pts == null || pts.Count < 5) return false;
        var arc = new double[pts.Count];
        for (int i = 1; i < pts.Count; i++) arc[i] = arc[i - 1] + (pts[i] - pts[i - 1]).Length;
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 4; j < pts.Count; j++)
            {
                if (arc[j] - arc[i] < 56.0) continue;
                if ((pts[i] - pts[j]).LengthSquared < 24 * 24) return true;
            }
        return SelfCrossing(pts) != null;
    }

    /// cells from the source to the state (source first)
    List<int> Trace(int state)
    {
        var cells = new List<int>();
        for (int s = state; s >= 0; s = parent[s]) cells.Add(s / Dirs);
        cells.Reverse();
        return cells;
    }

    // ---------------------------------------------------------------- build the network

    /// connect a city from targets to the network so far (the cheapest); gravel = Old West gravel path
    bool ConnectNext(List<NetTown> targets, bool gravel)
    {
        BuildTaps();
        var exits = targets.SelectMany(t => t.Exits).Where(Open).ToList();
        bool cut = DrawCut();
        Dictionary<int, NetExit> goal = null;
        List<object> what = null;
        int found = -1;
        // first without a kink in front of the exits (curve from 44 m before); only if that does not work (map edge, neighboring city) up to 90°
        // kink (the user's hint 2026-11-07: the kink was too cheap and gave ugly corners at exits with room)
        // if the path found curls in (loop over itself because it has to arrive exactly in the direction of the city and there was no room for a
        // curve – Wahava-Land/Ninezo-Land: crossing with itself, jumps up to 4 m), next attempt with a more oblique
        // approach or kink; if everything stays curled, the first path applies
        (NetExit Exit, List<int> Cells, object From, bool Cut)? first = null;
        heur = GoalHeuristic(exits.Select(e => e.Cell));   // A*: the targets are known here (no maxCost in these searches)
        try
        {
        foreach (int spread in new[] { 1, 2, 4 })
        {
            var sources = new List<(int Cell, int Dir, float Cost)>();
            what = new List<object>();
            foreach (var e in plan.Towns.Where(t => t.Connected && !t.OldWest).SelectMany(t => t.Exits).Where(Open))
            {
                // gravel path rather to a road than to a 16 m exit
                foreach (var (dir, extra) in ExitTurns(DirOf(e.DX, e.DZ), spread)) { sources.Add((e.Cell, dir, (gravel ? 600 : 0) + extra)); what.Add(e); }
            }
            foreach (var t in taps)
                for (int k = -2; k <= 2; k++) { sources.Add((t.Cell, (t.Dir + k + Dirs) % Dirs, JunctionPenalty + (Math.Abs(k) == 2 ? 60 : 0))); what.Add(t); }
            if (sources.Count == 0) return false;
            // target: arrive in front of an exit, driving roughly in the direction of the city
            goal = new Dictionary<int, NetExit>();
            foreach (var e in exits)
                for (int t = -spread; t <= spread; t++) goal.TryAdd(e.Cell * Dirs + (DirOf(-e.DX, -e.DZ) + t + Dirs) % Dirs, e);
            allowed = new HashSet<NetExit>(what.OfType<NetExit>().Concat(exits));
            // a junction must not cross its own road (otherwise it branches off on the wrong side and crosses right after)
            var roads = what.Select(o => (o as Tap)?.Road).ToArray();
            // target not reachable at all with this approach: skip the search that would look at the whole map
            // (the clearing only changes costs, not which cells are passable: then the second search without it fails too)
            if (Unreachable(goal.Keys, sources)) { cut = false; continue; }
            found = Search(sources, s => goal.ContainsKey(s), cut, sourceRoads: roads);
            if (found < 0 && cut) { cut = false; found = Search(sources, s => goal.ContainsKey(s), false, sourceRoads: roads); }
            if (found < 0) continue;
            var candidate = (goal[found], Trace(found), what[source[found]], cut);
            if (!Curly(found)) { first = candidate; break; }
            first ??= candidate;
        }
        }
        finally { heur = null; }
        if (first == null) return false;
        var (exit, cells, from, usedCut) = first.Value;
        AddRoad(exit, cells, from, gravel, usedCut, loop: false);
        exit.Town.Connected = true;
        return true;
    }

    /// driving directions when leaving an exit: straight and ±22.5° free; sharper turns carry an increasing penalty so that
    /// 67.5° / 90° kinks are only chosen when there is genuinely no room for a wider curve (map edge, neighboring city)
    static IEnumerable<(int Dir, float Extra)> ExitTurns(int dir, int spread)
    {
        for (int t = -spread; t <= spread; t++)
        {
            int a = Math.Abs(t);
            float extra = a <= 1 ? 0f : a == 2 ? 120f : a == 3 ? 350f : 700f;
            yield return ((dir + t + Dirs) % Dirs, extra);
        }
    }

    /// roads that the path crosses up to this state (jumps over a road)
    List<NetRoad> CrossedRoads(int state)
    {
        var list = new List<NetRoad>();
        for (int s = state; s >= 0 && parent[s] >= 0; s = parent[s])
        {
            int a = parent[s] / Dirs, b = s / Dirs;
            if (IsNeighbour(a, b) || !jumps.TryGetValue(a, out var js)) continue;
            foreach (var j in js) if (j.To == b) { if (j.Road != null) list.Add(j.Road); break; }
        }
        return list;
    }

    bool DrawCut() => rnd.NextDouble() < spec.Cuts switch { 0 => 0, 2 => 0.35, _ => 0.15 };

    /// enter road: from exit (new city) over the cells (source first) to the source (exit or junction)
    void AddRoad(NetExit exit, List<int> cells, object to, bool gravel, bool cut, bool loop, Tap startTap = null)
    {
        // line from the source to the target, then reverse (the road begins at the exit of the new city)
        var pts = new List<Point>();
        var fixedAt = new List<bool>();
        void Add(Point p, bool f) { pts.Add(p); fixedAt.Add(f); }
        Point srcAnchor, srcApproach;
        if (to is NetExit se) { srcAnchor = new Point(se.X, se.Z); srcApproach = se.Approach; }
        else { var t = (Tap)to; srcAnchor = t.P; srcApproach = Center(t.Cell); }
        // only the first 24 m at the exit/at the junction are fixed straight; behind it the smoothing may round the curve
        // (the user's hint 2026-11-07: a hard corner arose at the end of the fixed 44 m piece)
        const double Straight = 24;
        Point Along(Point a, Point b) => a + (b - a) * Math.Min(1, Straight / Math.Max(1e-6, (b - a).Length));
        Add(srcAnchor, true);
        Add(Along(srcAnchor, srcApproach), true);
        Add(srcApproach, false);
        var crossings = new List<(Point P, NetRoad Road, int K)>();
        var bridges = new List<BridgeSite>();
        for (int q = 0; q < cells.Count; q++)
        {
            var c = Center(cells[q]);
            if (q > 0 && jumps.TryGetValue(cells[q - 1], out var list) && list.FirstOrDefault(j => j.To == cells[q]) is var jp && jp.Cost > 0
                && !IsNeighbour(cells[q - 1], cells[q]))
            {
                if (AddBridgePoints(cells, q, jp.K, pts, fixedAt, bridges)) continue;   // bridge over a river
                // intersection: straight across the road
                fixedAt[^1] = true;
                Add(jp.Mid, true);
                Add(c, true);
                if (jp.Road != null) crossings.Add((jp.Mid, jp.Road, jp.K));
                else Log?.Invoke($"street crossing at {jp.Mid.X:0}/{jp.Mid.Y:0}");
                continue;
            }
            if ((c - srcApproach).Length < 14 || (c - exit.Approach).Length < 14) continue;
            Add(c, false);
        }
        var exitPoint = new Point(exit.X, exit.Z);
        Add(exit.Approach, false);
        Add(Along(exitPoint, exit.Approach), true);
        Add(exitPoint, true);
        pts.Reverse(); fixedAt.Reverse();
        var line = KeepOffEdge(Smooth(pts, fixedAt, new Random(new GeneratorSpec { Seed = spec.Seed + "/meander/" + plan.Roads.Count }.SeedValue), (x, z) => Sample(hb, x, z)));

        var r = new NetRoad
        {
            Points = line, Gravel = gravel, Loop = loop, Cut = cut, From = exit,
            Width = gravel ? Math.Clamp(exit.Width, 6, 12) : 16,
        };
        bridges.Reverse();   // the points were collected from the source, the road begins at the exit
        r.Bridges.AddRange(bridges);
        if (to is NetExit te) { r.To = te; te.Used = true; if (te.Street) plan.Junctions.Add(srcAnchor); }
        else { var t = (Tap)to; r.Junction = t.P; r.EndRoad = t.Road; plan.Junctions.Add(t.P); nodesUsed.Add(t.P); }
        if (startTap != null) { r.StartRoad = startTap.Road; plan.Junctions.Add(startTap.P); nodesUsed.Add(startTap.P); }
        exit.Used = true;
        if (exit.Street) plan.Junctions.Add(exitPoint);   // starts on a town street
        foreach (var (p, cr, _) in crossings) { r.Crossings.Add(p); r.CrossedRoads.Add(cr); plan.CrossingPoints.Add(p); nodesUsed.Add(p); }
        nodesUsed.Add(line[0]); nodesUsed.Add(line[^1]);
        r.Samples = Resample(line, 16);
        r.Length = r.Samples.Count > 1 ? (r.Samples.Count - 1) * 16.0 : 0;
        plan.Roads.Add(r);
        BlockAlong(line);
        AddToGraph(r, to, crossings, startTap);
    }

    /// center line at least EdgeCenter from the map edge (the smoothing can pull it beyond the grid cells); the fixed
    /// connecting pieces at the ends (up to 30 m) stay as they are – a city exit cannot be moved
    List<Point> KeepOffEdge(List<Point> line, double freeEnd = 30)
    {
        double lim = half - EdgeCenter, total = 0;
        for (int q = 1; q < line.Count; q++) total += (line[q] - line[q - 1]).Length;
        var res = new List<Point>(line.Count);
        double walked = 0;
        for (int q = 0; q < line.Count; q++)
        {
            if (q > 0) walked += (line[q] - line[q - 1]).Length;
            var p = line[q];
            if (walked > 30 && total - walked > freeEnd) p = new Point(Math.Clamp(p.X, -lim, lim), Math.Clamp(p.Y, -lim, lim));
            res.Add(p);
        }
        return res;
    }

    bool IsNeighbour(int a, int b) { int di = Math.Abs(a % gw - b % gw), dj = Math.Abs(a / gw - b / gw); return di <= 2 && dj <= 2; }

    // block cells near the new road (other roads only via junctions and intersections); remember distance and
    // terrain height of the road up to ParallelRange (surcharge for roads that run alongside)
    void BlockAlong(List<Point> line)
    {
        for (int q = 0; q + 1 < line.Count; q++)
        {
            Point a = line[q], b = line[q + 1];
            int i0 = Math.Max(0, (int)((Math.Min(a.X, b.X) - ParallelRange + half) / C) - 1), i1 = Math.Min(gw - 1, (int)((Math.Max(a.X, b.X) + ParallelRange + half) / C) + 1);
            int j0 = Math.Max(0, (int)((Math.Min(a.Y, b.Y) - ParallelRange + half) / C) - 1), j1 = Math.Min(gw - 1, (int)((Math.Max(a.Y, b.Y) + ParallelRange + half) / C) + 1);
            var ab = b - a;
            double l2 = ab.LengthSquared;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int c = j * gw + i;
                    var p = Center(c);
                    double t = l2 < 1e-9 ? 0 : Math.Clamp(((p - a) * ab) / l2, 0, 1);
                    var foot = a + ab * t;
                    double d = (p - foot).Length;
                    if (d <= RoadBuffer) roadBlock[c] = true;
                    if (d < ParallelRange && d < nearRoadD[c]) { nearRoadD[c] = (float)d; nearRoadH[c] = (float)Sample(hb, foot.X, foot.Y); }
                }
        }
    }

    static double SegDist(Point p, Point a, Point b)
    {
        var ab = b - a; double l2 = ab.LengthSquared;
        double t = l2 < 1e-9 ? 0 : Math.Clamp(((p - a) * ab) / l2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// character of a road's line (the user's request 2026-11-16: roads looked "exact", more variety): Reach = length over
    /// which a kink of the searched path is spread into a curve (m), Dev = how far the line may leave the searched corridor (m),
    /// Swing = factor on the sideways swing on straight stretches, Wave = factor on its wave length, Across = how steep across
    /// the ground the swing may still be. Each road draws its own: calm, normal, lively or winding (the user 2026-11-16: the first
    /// version was "a bit too tame", roads need not be the same everywhere).
    internal readonly record struct RoadShape(double Reach, double Dev, double Swing, double Wave = 1, double Across = 0.08)
    {
        public static readonly RoadShape Default = new(38, 8, 1);
        public static RoadShape Draw(Random rnd)
        {
            double u = rnd.NextDouble(), v = rnd.NextDouble(), s = rnd.NextDouble(), w = rnd.NextDouble();
            return u < 0.2 ? new(48 + 14 * v, 11 + 3 * v, 0.7 + 0.7 * s, 1.1 + 0.3 * w)             // calm: wide curves, little swing
                : u < 0.55 ? new(34 + 12 * v, 8 + 3 * v, 1.5 + 1.0 * s, 0.85 + 0.3 * w, 0.10)      // normal
                : u < 0.82 ? new(26 + 10 * v, 7 + 2 * v, 2.4 + 1.1 * s, 0.7 + 0.25 * w, 0.12)      // lively: closer curves, more swing
                : new(22 + 8 * v, 7 + 2 * v, 3.4 + 1.4 * s, 0.5 + 0.2 * w, 0.14);                  // winding: long S-curves on open ground
        }
    }

    // smooth the line: densify to 4 m, smoothing spline through the corridor (fixed points stay), then thin out
    /// meander: random source for the road's shape and a slight sideways swing on straight stretches (null = default shape, no swing)
    List<Point> Smooth(List<Point> pts, List<bool> fixedAt, Random meander = null, Func<double, double, double> terrain = null, MeanderStyle style = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var res = SmoothCore(pts, fixedAt, meander, terrain, style);
        SmoothCalls++; SmoothMs += watch.ElapsedMilliseconds;
        return res;
    }
    internal static long SmoothMs; internal static int SmoothCalls;   // for tests

    List<Point> SmoothCore(List<Point> pts, List<bool> fixedAt, Random meander, Func<double, double, double> terrain, MeanderStyle style)
    {
        var shape = meander != null ? RoadShape.Draw(meander) : RoadShape.Default;
        terrain ??= (x, z) => Sample(hb, x, z);
        var dense = new List<Point> { pts[0] };
        var fx = new List<bool> { true };
        for (int q = 0; q + 1 < pts.Count; q++)
        {
            double len = (pts[q + 1] - pts[q]).Length;
            int steps = Math.Max(1, (int)Math.Ceiling(len / 4));
            bool both = fixedAt[q] && fixedAt[q + 1];
            for (int k = 1; k <= steps; k++)
            {
                dense.Add(pts[q] + (pts[q + 1] - pts[q]) * (k / (double)steps));
                fx.Add(k == steps ? fixedAt[q + 1] : both);
            }
        }
        fx[^1] = true;
        // how far a point may leave the corridor: less on a hillside (sideways there means up or down the slope)
        var dev = new double[dense.Count];
        for (int q = 0; q < dense.Count; q++)
        {
            int a = Math.Max(0, q - 3), b = Math.Min(dense.Count - 1, q + 3);
            var d = dense[b] - dense[a];
            if (d.Length < 1e-6) { dev[q] = 2; continue; }
            d.Normalize();
            var nv = new Vector(-d.Y, d.X) * 12;
            double across = Math.Abs(terrain(dense[q].X + nv.X, dense[q].Y + nv.Y) - terrain(dense[q].X - nv.X, dense[q].Y - nv.Y)) / 24;
            // roads to the map edge run along the 80 m line there, where they cannot give way: close to their path
            dev[q] = shape.Dev * (style == MeanderStyle.Restless ? 0.4 : 1) * Math.Clamp(1 - across / 0.3, 0.25, 1);
            // at sharp bends (e.g. tight exit turns or space-constrained corners) give the spline more room to round the
            // corner softly, even on a hillside (NewlyBlocked still keeps it off tiles, buildings, water and other roads)
            if (style != MeanderStyle.Restless)
            {
                int p0 = Math.Max(0, q - 6), p1 = Math.Min(dense.Count - 1, q + 6);
                Vector v1 = dense[q] - dense[p0], v2 = dense[p1] - dense[q];
                if (v1.Length > 4 && v2.Length > 4)
                {
                    double bend = Math.Abs(Vector.AngleBetween(v1, v2));
                    if (bend > 25)
                        dev[q] = Math.Max(dev[q], Math.Min(16, shape.Dev + (bend - 25) * 0.15));
                }
            }
        }
        bool Bad(bool[] f, Point p) { int c = CellOf(p.X, p.Y); return c < 0 || f[c]; }
        bool NewlyBlocked(Point from, Point p) =>
            (Bad(blocked, p) && !Bad(blocked, from)) || (Bad(roadBlock, p) && !Bad(roadBlock, from)) || (Bad(edge, p) && !Bad(edge, from));
        var spline = RoadLine.Spline(dense, fx.ToArray(), Math.Pow(shape.Reach / 4, 4), q => dev[q], (q, p) => NewlyBlocked(dense[q], p));
        if (spline != null)
        {
            // roads to the map edge are restless already; the others swing as their shape says
            if (meander != null)
                Meander(spline, fx, meander, terrain, style ?? MeanderStyle.Normal with
                {
                    Amp = MeanderStyle.Normal.Amp * shape.Swing, Wave = shape.Wave, Across = shape.Across,
                }, NewlyBlocked);
            return RoadRouter.Simplify(spline.ToList(), 0.5);
        }
        SplineFallbacks++;
        return SmoothOld(pts, fixedAt, meander, terrain, style);
    }

    internal static int SplineFallbacks;   // lines that had to use the old smoothing (for tests)

    // old smoothing (fallback): densify to 4 m, mean over neighbors (fixed points stay), then thin out
    static List<Point> SmoothOld(List<Point> pts, List<bool> fixedAt, Random meander = null, Func<double, double, double> terrain = null, MeanderStyle style = null)
    {
        var dense = new List<Point> { pts[0] };
        var fx = new List<bool> { true };
        for (int q = 0; q + 1 < pts.Count; q++)
        {
            double len = (pts[q + 1] - pts[q]).Length;
            int steps = Math.Max(1, (int)Math.Ceiling(len / 4));
            bool both = fixedAt[q] && fixedAt[q + 1];
            for (int k = 1; k <= steps; k++)
            {
                dense.Add(pts[q] + (pts[q + 1] - pts[q]) * (k / (double)steps));
                fx.Add(k == steps ? fixedAt[q + 1] : both);
            }
        }
        var cur = dense.ToArray();
        for (int it = 0; it < 10; it++)
        {
            var next = (Point[])cur.Clone();
            for (int q = 1; q + 1 < cur.Length; q++)
                if (!fx[q]) next[q] = new Point((cur[q - 1].X + 2 * cur[q].X + cur[q + 1].X) / 4, (cur[q - 1].Y + 2 * cur[q].Y + cur[q + 1].Y) / 4);
            cur = next;
        }
        if (meander != null && terrain != null) Meander(cur, fx, meander, terrain, style ?? MeanderStyle.Normal);
        return RoadRouter.Simplify(cur.ToList(), 0.5);
    }

    const double MeanderAmp = 4;   // sideways swing on straight stretches up to this far (m; the user's hint 2026-11-12: roads "look symmetric")

    /// slight, wide sideways swing on straight stretches (points every 4 m): two waves of 260–420 m and 150–230 m with random phase,
    /// only where the road hardly turns over ±100 m, faded out within 150 m of fixed points (exits, junctions, crossings)
    /// how strongly a road swings sideways: amplitude (m), length of the waves (factor), start and length of the fade-in behind fixed
    /// points (m), how steep across the ground may be (fraction) – roads to the map edge swing more (the user's request 2026-11-14)
    internal sealed record MeanderStyle(double Amp, double Wave, double FadeStart, double FadeLength, double Across)
    {
        public static readonly MeanderStyle Normal = new(MeanderAmp, 1, 50, 100, 0.08);
        public static readonly MeanderStyle Restless = new(11, 0.5, 20, 50, 0.16);
    }

    /// blockedAt (from, to): true where the swing would move a point onto ground the road may not use (tiles, buildings, other
    /// roads, map edge) – there the swing is taken back smoothly
    static void Meander(Point[] cur, List<bool> fx, Random rnd, Func<double, double, double> terrain, MeanderStyle st, Func<Point, Point, bool> blockedAt = null)
    {
        int n = cur.Length;
        if (n < 30) return;
        var dist = new double[n];
        for (int q = 1; q < n; q++) dist[q] = dist[q - 1] + (cur[q] - cur[q - 1]).Length;
        // distance along the road to the nearest fixed point
        var toFixed = new double[n];
        double last = double.NegativeInfinity;
        for (int q = 0; q < n; q++) { if (fx[q]) last = dist[q]; toFixed[q] = dist[q] - last; }
        last = double.PositiveInfinity;
        for (int q = n - 1; q >= 0; q--) { if (fx[q]) last = dist[q]; toFixed[q] = Math.Min(toFixed[q], last - dist[q]); }
        double l1 = (260 + 160 * rnd.NextDouble()) * st.Wave, l2 = (150 + 80 * rnd.NextDouble()) * st.Wave, p1 = rnd.NextDouble() * 2 * Math.PI, p2 = rnd.NextDouble() * 2 * Math.PI;
        var offset = new double[n];
        for (int q = 1; q + 1 < n; q++)
        {
            int a = Math.Max(0, q - 25), b = Math.Min(n - 1, q + 25);
            Vector v1 = cur[q] - cur[a], v2 = cur[b] - cur[q];
            if (v1.Length < 1 || v2.Length < 1) continue;
            double turn = Math.Abs(Vector.AngleBetween(v1, v2));
            double straight = Math.Clamp(1 - turn / 20, 0, 1);
            double fade = Math.Clamp((toFixed[q] - st.FadeStart) / st.FadeLength, 0, 1);
            // only on level ground: on a hillside the swing would cut into the slope (measured: clearing 11 → 26 m)
            var dn = cur[b] - cur[a]; dn.Normalize();
            var nv = new Vector(-dn.Y, dn.X) * 12;
            double across = Math.Abs(terrain(cur[q].X + nv.X, cur[q].Y + nv.Y) - terrain(cur[q].X - nv.X, cur[q].Y - nv.Y)) / 24;
            straight *= Math.Clamp(1 - across / st.Across, 0, 1);
            fade = fade * fade * (3 - 2 * fade);
            offset[q] = st.Amp * straight * fade * (0.65 * Math.Sin(2 * Math.PI * dist[q] / l1 + p1) + 0.35 * Math.Sin(2 * Math.PI * dist[q] / l2 + p2));
        }
        // smooth the offset (the straightness changes in steps), then shift sideways
        for (int it = 0; it < 6; it++)
            for (int q = 1; q + 1 < n; q++) offset[q] = (offset[q - 1] + 2 * offset[q] + offset[q + 1]) / 4;
        var src = (Point[])cur.Clone();
        var side = new Vector[n];
        for (int q = 1; q + 1 < n; q++)
        {
            var d = src[q + 1] - src[q - 1];
            if (d.Length < 1e-6) continue;
            d.Normalize();
            side[q] = new Vector(-d.Y, d.X);
        }
        // where the swing hits forbidden ground, take it back over ±60 m (a few rounds, the smoothing spreads it)
        var keep = Enumerable.Repeat(1.0, n).ToArray();
        for (int round = 0; round < 4 && blockedAt != null; round++)
        {
            bool any = false;
            for (int q = 1; q + 1 < n; q++)
                if (!fx[q] && Math.Abs(offset[q] * keep[q]) >= 0.01 && blockedAt(src[q], src[q] + side[q] * (offset[q] * keep[q])))
                {
                    any = true;
                    for (int k = Math.Max(1, q - 15); k <= Math.Min(n - 2, q + 15); k++) keep[k] = 0;
                }
            if (!any) break;
            for (int it = 0; it < 8; it++)
                for (int q = 1; q + 1 < n; q++) keep[q] = Math.Min(keep[q], (keep[q - 1] + 2 * keep[q] + keep[q + 1]) / 4);
        }
        for (int q = 1; q + 1 < n; q++)
        {
            double o = offset[q] * keep[q];
            if (fx[q] || Math.Abs(o) < 0.01) continue;
            cur[q] = src[q] + side[q] * o;
        }
    }

    internal static List<Point> Resample(List<Point> line, double step)
    {
        var res = new List<Point> { line[0] };
        double carry = 0;
        for (int q = 0; q + 1 < line.Count; q++)
        {
            var a = line[q]; var b = line[q + 1];
            double len = (b - a).Length, pos = step - carry;
            while (pos <= len) { res.Add(a + (b - a) * (pos / len)); pos += step; }
            carry = len - (pos - step);
        }
        if ((res[^1] - line[^1]).Length > 1) res.Add(line[^1]);
        return res;
    }

    // ---------------------------------------------------------------- road graph (for cross connections)

    int NewNode() { graph.Add(new()); return graph.Count - 1; }
    void Link(int a, int b, double cost) { graph[a].Add((b, cost)); graph[b].Add((a, cost)); }

    void AddToGraph(NetRoad r, object to, List<(Point P, NetRoad Road, int K)> crossings, Tap startTap)
    {
        r.Nodes = new int[r.Samples.Count];
        for (int k = 0; k < r.Samples.Count; k++)
        {
            r.Nodes[k] = NewNode();
            if (k > 0) Link(r.Nodes[k - 1], r.Nodes[k], (r.Samples[k] - r.Samples[k - 1]).Length);
        }
        if (startTap != null) Link(r.Nodes[0], startTap.Road.Nodes[startTap.K], 0);
        else Link(r.Nodes[0], r.From.Town.Id, ThroughTown);
        if (to is NetExit te) Link(r.Nodes[^1], te.Town.Id, ThroughTown);
        else if (to is Tap t) Link(r.Nodes[^1], t.Road.Nodes[t.K], 0);   // null: free end (road to the map edge)
        foreach (var (p, road, k) in crossings)
        {
            int best = 0;
            for (int q = 1; q < r.Samples.Count; q++) if ((r.Samples[q] - p).Length < (r.Samples[best] - p).Length) best = q;
            Link(r.Nodes[best], road.Nodes[k], 0);
        }
    }

    double[] GraphDistances(int from) => GraphDistances(new[] { (from, 0.0) });

    double[] GraphDistances(IEnumerable<(int Node, double D0)> from)
    {
        var d = new double[graph.Count];
        Array.Fill(d, double.MaxValue);
        var q = new PriorityQueue<int, double>();
        foreach (var (f, d0) in from) if (d0 < d[f]) { d[f] = d0; q.Enqueue(f, d0); }
        while (q.TryDequeue(out int a, out double da))
        {
            if (da > d[a]) continue;
            foreach (var (b, c) in graph[a])
                if (da + c < d[b]) { d[b] = da + c; q.Enqueue(b, d[b]); }
        }
        return d;
    }

    /// path over the network from an exit: through the town (Manhattan distance + 20 m) to the exits at which roads
    /// already begin, and further over these. The city node alone (ThroughTown 60 m) underestimates the detour with large
    /// towns – an exit on the other side then counted as "right next to" the own access.
    double[] ExitDistances(NetExit e)
    {
        var src = new List<(int, double)>();
        double Manhattan(double x, double z) => Math.Abs(x - e.X) + Math.Abs(z - e.Z) + 20;
        foreach (var r in plan.Roads)
        {
            if (r.From?.Town == e.Town) src.Add((r.Nodes[0], Manhattan(r.From.X, r.From.Z)));
            if (r.To?.Town == e.Town) src.Add((r.Nodes[^1], Manhattan(r.To.X, r.To.Z)));
        }
        return src.Count > 0 ? GraphDistances(src) : GraphDistances(e.Town.Id);
    }

    // ---------------------------------------------------------------- cross connections

    void AddLoops(List<NetTown> asphalt)
    {
        int connected = asphalt.Count(t => t.Connected);
        if (connected < 3 || plan.Roads.Count == 0) return;
        int wanted = (int)Math.Round(connected * (spec.Loops switch { 0 => 0.15, 2 => 0.6, _ => 0.35 }));
        int made = 0;
        // detour over the network: network length / length of the new road
        double townRatio = spec.Loops switch { 0 => 1.8, 2 => 1.3, _ => 1.5 }, roadRatio = spec.Loops switch { 0 => 2.2, 2 => 1.5, _ => 1.8 };
        // a) from free exits of connected cities
        foreach (var e in asphalt.Where(t => t.Connected).SelectMany(t => t.Exits).Where(Open).OrderBy(_ => rnd.Next()).ToList())
        {
            if (made >= wanted) break;
            if (e.Used) continue;
            if (TryLoop(ExitTurns(DirOf(e.DX, e.DZ), 1).Select(t => (e.Cell, t.Dir, t.Extra)).ToList(), e.Town.Id, townRatio, 250, e, null)) made++;
        }
        // b) between two roads (junction → junction or free exit)
        for (int attempt = 0; attempt < 25 && made < wanted; attempt++)
        {
            BuildTaps();
            if (taps.Count == 0) break;
            var t = taps[rnd.Next(taps.Count)];
            if (TryLoop(Enumerable.Range(-2, 5).Select(k => (t.Cell, (t.Dir + k + Dirs) % Dirs, JunctionPenalty + (Math.Abs(k) == 2 ? 60f : 0f))).ToList(), t.Road.Nodes[t.K], roadRatio, 400, null, t)) made++;
        }
    }

    /// second pass (the user's request 2026-11-08): every still free exit of a connected city gets a
    /// feeder with a junction if a planned road passes nearby (path at most FeederLength) and the path
    /// over the network to it would be at least 1.5 times as long – otherwise it is the city's own access and brings nothing.
    const double FeederLength = 1200;   // 700 m found only 1 feeder on Rifane-Land (most nearby roads 0.8–1.2 km path)

    void AddFeeders(List<NetTown> asphalt)
    {
        if (plan.Roads.Count == 0) return;
        // node tiles have their own pass (ConnectNodeExits) and are adapted/dropped by FinishNodeTiles if an exit is not needed;
        // the town dead-end pass below (90° kinks, fork into the own access road) must not run on them
        var towns = asphalt.Where(t => !t.Node && t.Connected).ToList();
        // first two free exits of neighboring cities with each other (otherwise a feeder of one provides for the other)
        foreach (var e in towns.SelectMany(t => t.Exits).Where(Open).OrderBy(_ => rnd.Next()).ToList())
        {
            if (!Open(e)) continue;
            if (TryLoop(ExitTurns(DirOf(e.DX, e.DZ), 4).Select(t => (e.Cell, t.Dir, t.Extra)).ToList(), e.Town.Id, DeadEndRatio, 0, e, null,
                    maxLen: DeadEndLength, maxCost: 9000, net: ExitDistances(e), exitsOnly: true, maxEarthwork: DeadEndEarthwork, noCross: true))
                MarkFeeder();
        }
        foreach (var e in towns.SelectMany(t => t.Exits).Where(Open).OrderBy(_ => rnd.Next()).ToList())
        {
            if (!Open(e)) continue;   // meanwhile occupied or built over by an earlier feeder
            if (TryLoop(ExitTurns(DirOf(e.DX, e.DZ), 1).Select(t => (e.Cell, t.Dir, t.Extra)).ToList(), e.Town.Id, 1.5, 0, e, null,
                    maxLen: FeederLength, tapsOnly: true, townGap: TownGap, maxCost: 5000, noCross: true))
                MarkFeeder();
        }
        // dead ends (the user's hint 2026-11-08: 9 open exits on Rifane-Land were too many): what is still open afterwards
        // gets a more generous attempt – curve right at the exit (up to 90°, in front of rock walls), junction also close to
        // cities, connection also to a free exit of another city, already from 1.15 times the detour over the network
        foreach (var e in towns.SelectMany(t => t.Exits).Where(Open).OrderBy(_ => rnd.Next()).ToList())
        {
            if (!Open(e)) continue;
            var start = ExitTurns(DirOf(e.DX, e.DZ), 4).Select(t => (e.Cell, t.Dir, t.Extra)).ToList();
            if (TryLoop(start, e.Town.Id, DeadEndRatio, 0, e, null, maxLen: DeadEndLength, townGap: DeadEndGap, maxCost: 9000, net: ExitDistances(e), maxEarthwork: DeadEndEarthwork, noCross: true)
                // further away (free land in front of the exit): up to 3 km, detour at least 1.3 times
                || TryLoop(start, e.Town.Id, 1.3, 0, e, null, maxLen: 3000, townGap: DeadEndGap, maxCost: 16000, net: ExitDistances(e), maxEarthwork: DeadEndEarthwork, noCross: true)
                // finally a fork: shortly behind the town into a nearby road (also the own access), at most 700 m
                || TryLoop(start, e.Town.Id, 0, 0, e, null, maxLen: 700, tapsOnly: true, townGap: DeadEndGap, maxCost: 5000, net: ExitDistances(e), maxEarthwork: DeadEndEarthwork, noCross: true))
                MarkFeeder();
        }
        // Old West places: further exits with a gravel path to the nearest road
        foreach (var e in plan.Towns.Where(t => t.OldWest && t.Connected).SelectMany(t => t.Exits).Where(Open).ToList())
        {
            if (!Open(e)) continue;
            if (TryLoop(ExitTurns(DirOf(e.DX, e.DZ), 4).Select(t => (e.Cell, t.Dir, t.Extra)).ToList(), e.Town.Id, DeadEndRatio, 0, e, null,
                    maxLen: DeadEndLength, tapsOnly: true, townGap: DeadEndGap, maxCost: 9000, gravel: true, net: ExitDistances(e), maxEarthwork: DeadEndEarthwork, noCross: true))
                MarkFeeder();
        }

        void MarkFeeder()
        {
            plan.Roads[^1].Loop = false;
            plan.Roads[^1].Feeder = true;
        }
    }

    const double DeadEndRatio = 1.15, DeadEndLength = 1600, DeadEndGap = 60, DeadEndEarthwork = 6;

    // ---------------------------------------------------------------- town connections

    /// town connections (the user's request 2026-11-08, example Rifane-Land 1124/−1084 → 1192/−1049): if a planned road
    /// passes close to the town streets of a city (roads outside the tiles: districts, gateway) without being connected
    /// there, it is connected with a short, straight piece (40–220 m) to the town street – if the path over the network
    /// would be at least three times as long and 400 m longer. At most two per city.
    const double LinkLength = 220;

    void AddTownLinks(List<NetTown> asphalt)
    {
        if (plan.Roads.Count == 0) return;
        var scan = new RoadScan(w);
        var tiles = plan.Towns.SelectMany(t => t.Tiles).ToList();
        double TileGap(Point p) => tiles.Count == 0 ? double.MaxValue : tiles.Min(t =>
        {
            double dx = Math.Max(0, Math.Max(t.X - p.X, p.X - (t.X + t.SizeX))), dz = Math.Max(0, Math.Max(t.Z - p.Y, p.Y - (t.Z + t.SizeZ)));
            return Math.Sqrt(dx * dx + dz * dz);
        });
        // places on existing town streets (asphalt, not in tiles), per city at most one every 24 m
        var ports = new Dictionary<NetTown, List<RoadSpot>>();
        for (int g = 0; g < n; g++)
        {
            if (!roadCell[g] || tileCell[g]) continue;
            var c = Center(g);
            var town = asphalt.Where(t => t.Connected).OrderBy(t => TownDistanceTo(t, c)).FirstOrDefault();
            if (town == null || TownDistanceTo(town, c) > 300) continue;
            var spot = RoadFollower.Center(scan, c);
            if (spot == null || spot.Kind != WorldData.Asphalt || spot.Width < 6 || spot.Width > 24 || TileGap(spot.Center) < 30) continue;
            // straight stretch (no curve, no junction): direction 15 m before and behind almost the same
            var a1 = RoadFollower.Axis(scan, spot.Center + spot.Dir * 15, 7);
            var a2 = RoadFollower.Axis(scan, spot.Center - spot.Dir * 15, 7);
            if (a1 == null || a2 == null || Math.Abs(Vector.Multiply(a1.Value, spot.Dir)) < 0.94 || Math.Abs(Vector.Multiply(a2.Value, spot.Dir)) < 0.94) continue;
            if (!ports.TryGetValue(town, out var list)) ports[town] = list = new();
            if (list.Any(s => (s.Center - spot.Center).Length < 24)) continue;
            list.Add(spot);
        }
        foreach (var (town, list) in ports.OrderBy(kv => kv.Key.Id))
        {
            for (int made = 0; made < 2; made++)
            {
                var net = GraphDistances(town.Id);
                (RoadSpot Spot, Vector Dir, NetRoad Road, int K, double Len, double Saving) best = default;
                foreach (var spot in list)
                {
                    double hs = w.HeightAt((int)Math.Floor(spot.Center.X), (int)Math.Floor(spot.Center.Y));
                    var sn = new Vector(-spot.Dir.Y, spot.Dir.X);
                    foreach (var r in plan.Roads)
                    {
                        if (r.Gravel) continue;
                        var s = r.Samples;
                        for (int k = 3; k + 3 < s.Count; k++)
                        {
                            var v = s[k] - spot.Center;
                            double len = v.Length;
                            if (len < 40 || len > LinkLength) continue;
                            double dn = net[r.Nodes[k]];
                            if (dn == double.MaxValue || dn < 3 * len || dn - len < 400) continue;
                            v /= len;
                            if (Math.Abs(Vector.Multiply(v, sn)) < 0.87) continue;               // roughly at a right angle from the town street (±30°)
                            var rd = s[k + 3] - s[k - 3]; rd.Normalize();
                            if (Math.Abs(Vector.Multiply(v, rd)) > 0.57) continue;                // and onto the road (±35°)
                            Vector a = s[k] - s[k - 3], b = s[k + 3] - s[k];
                            if (Math.Abs(Vector.AngleBetween(a, b)) > 40) continue;              // no tight curve there
                            if (nodesUsed.Any(q => (q - s[k]).Length < 120) || TileGap(s[k]) < 40) continue;
                            double rise = Math.Abs(w.HeightAt((int)Math.Floor(s[k].X), (int)Math.Floor(s[k].Y)) - hs);
                            if (rise > len * spec.MaxGrade) continue;                             // short: up to the gradient "narrow passage" (town street often on a step)
                            double score = (dn - len) * (1 - 0.5 * rise / (len * spec.MaxGrade));   // flatter connection preferred
                            if (score <= best.Saving) continue;
                            if (!LinkFree(spot, s[k], r)) continue;
                            best = (spot, v, r, k, len, score);
                        }
                    }
                }
                if (best.Road == null) break;
                AddTownLink(town, best.Spot, best.Dir, best.Road, best.K);
                list.RemoveAll(s => (s.Center - best.Spot.Center).Length < NodeSpacing);
            }
        }
    }

    /// stretch of a town connection free? No water, no buildings/tiles, no other road (existing or planned)
    /// between the edge of the town street and the planned road
    bool LinkFree(RoadSpot spot, Point to, NetRoad target)
    {
        var v = to - spot.Center;
        double len = v.Length;
        v /= len;
        double from = spot.Width / 2 + 2;
        double h0 = w.HeightAt((int)Math.Floor(spot.Center.X), (int)Math.Floor(spot.Center.Y)), h1 = w.HeightAt((int)Math.Floor(to.X), (int)Math.Floor(to.Y));
        for (double t = from; t <= len - 10; t += 2)
        {
            var q = spot.Center + v * t;
            int ix = (int)Math.Floor(q.X), iz = (int)Math.Floor(q.Y);
            if (w.WaterAt(ix, iz) || w.RoadAt(ix, iz) != 0) return false;
            // terrain in between roughly on the straight connection (at most 6 m cut/fill, no bumps in between)
            if (Math.Abs(w.HeightAt(ix, iz) - (h0 + (h1 - h0) * t / len)) > 6) return false;
        }
        var nv = new Vector(-v.Y, v.X);
        foreach (var p in w.Prefabs)
        {
            if (p.Def == null) continue;
            double m = p.IsTile ? 10 : 4;
            for (double t = from; t <= len; t += 4)
                for (int side = -1; side <= 1; side++)
                {
                    var q = spot.Center + v * t + nv * (side * 9);
                    if (q.X > p.X - m && q.X < p.X + p.SizeX + m && q.Y > p.Z - m && q.Y < p.Z + p.SizeZ + m) return false;
                }
        }
        foreach (var r in plan.Roads)
        {
            if (r == target) continue;
            for (int k = 0; k + 1 < r.Points.Count; k++)
                if (SegDist(r.Points[k], spot.Center, to) < 30 || SegDist(r.Points[k + 1], spot.Center, to) < 30) return false;
        }
        return true;
    }

    void AddTownLink(NetTown town, RoadSpot spot, Vector dir, NetRoad road, int k)
    {
        var p = road.Samples[k];
        double len = (p - spot.Center).Length;
        var line = new List<Point>();
        int steps = Math.Max(2, (int)Math.Ceiling(len / 12));
        for (int q = 0; q <= steps; q++) line.Add(spot.Center + (p - spot.Center) * (q / (double)steps));
        var from = new NetExit
        {
            Town = town, X = spot.Center.X, Z = spot.Center.Y, DX = dir.X, DZ = dir.Y, Street = true,
            Width = (int)Math.Round(Math.Clamp(spot.Width - 2, 6, 16)), StreetHalf = spot.Width / 2, StreetDX = spot.Dir.X, StreetDZ = spot.Dir.Y, Used = true,
        };
        var tap = new Tap(road, k, p, -1, 0, DirOf(-dir.X, -dir.Y));
        // short town connections keep the width of the town street instead of flaring to 16 m like a funnel (Wimovo-Land 2026-11-27)
        var r = new NetRoad { Points = line, Width = from.Width, From = from, TownLink = true, Junction = p, EndRoad = road };
        plan.Junctions.Add(p);
        plan.Junctions.Add(spot.Center);
        nodesUsed.Add(p); nodesUsed.Add(spot.Center);
        r.Samples = Resample(line, 16);
        r.Length = r.Samples.Count > 1 ? (r.Samples.Count - 1) * 16.0 : 0;
        plan.Roads.Add(r);
        BlockAlong(line);
        AddToGraph(r, tap, new(), null);
    }

    bool TryLoop(List<(int Cell, int Dir, float Cost)> start, int startNode, double ratio, double minCost, NetExit startExit, Tap startTap,
        double maxLen = double.MaxValue, bool tapsOnly = false, double townGap = 300, float maxCost = 9000, bool gravel = false, double[] net = null, bool exitsOnly = false, double maxEarthwork = double.MaxValue, bool relaxed = false, bool noCross = false)
    {
        BuildTaps(Math.Min(TownGap, townGap), relaxed);
        if (startTap != null) taps.RemoveAll(t => (t.P - startTap.P).Length < NodeSpacing);
        net ??= GraphDistances(startNode);
        allowed = new HashSet<NetExit>(exitList);
        bool cut = DrawCut();
        Search(start, null, cut, maxCost, startTap == null ? null : Enumerable.Repeat(startTap.Road, start.Count).ToArray(), noCross);
        double best = 0, maxRatio = 0; object target = null; int targetState = -1;
        void Consider(object o, int cell, int arrive, double extra, double dn)
        {
            if (cell < 0 || dn == double.MaxValue) return;
            var (cost, state) = Arrival(cell, arrive);
            if (cost == float.MaxValue) return;
            // length of the new road (trace the path back) instead of the costs: they contain gradient, terrain and curves
            double c = extra;
            for (int s = state; s >= 0 && parent[s] >= 0; s = parent[s]) c += (Center(s / Dirs) - Center(parent[s] / Dirs)).Length;
            if (c > maxLen || (relaxed ? cost > 8 * c + 1500 : cost > 3 * c + 400) || Curly(state)) return;   // too long, very steep/expensive path or curl: better not
            if (o is Tap tt && CrossedRoads(state).Contains(tt.Road)) return;   // crosses the road at which it is to arrive
            // a node tile exit must not loop back onto a road that already leaves the same node tile (nor a junction gateway before the last pass)
            if (startExit?.Town?.Node == true && o is Tap ow && (ow.Road.From?.Town == startExit.Town || ow.Road.To?.Town == startExit.Town)) return;
            if (!relaxed && startExit?.Tile != null && JunctionGateway(startExit.Tile) && o is Tap gw && (gw.Road.From?.Tile == startExit.Tile || gw.Road.To?.Tile == startExit.Tile)) return;
            if (WrapsAroundNode(startExit, state, fromStart: true) || (o is NetExit te && WrapsAroundNode(te, state, fromStart: false))) return;
            if (ParallelLength(state, c) > (relaxed ? 500 : 250)) return;   // runs close beside another road for a long time
            if (maxEarthwork < double.MaxValue && startExit?.Tile != null && Earthwork(startExit, state, o as NetExit) > maxEarthwork) return;
            if (c >= minCost) maxRatio = Math.Max(maxRatio, dn / c);
            if (c < minCost || dn < ratio * c) return;
            double score = dn / c;
            if (score > best) { best = score; target = o; targetState = state; }
        }
        Point origin = startExit != null ? new Point(startExit.X, startExit.Z) : startTap.P;
        foreach (var e in plan.Towns.Where(t => !tapsOnly && t.Connected && !t.OldWest && (startExit == null || t != startExit.Town)).SelectMany(t => t.Exits).Where(Open))
            Consider(e, e.Cell, DirOf(-e.DX, -e.DZ), ApproachLength, net[e.Town.Id]);
        foreach (var t in exitsOnly ? new List<Tap>() : taps)
            if ((t.P - origin).Length >= (relaxed ? RelaxedNodeSpacing : NodeSpacing) && (startExit == null || TownDistanceTo(startExit.Town, t.P) >= townGap))
                Consider(t, t.Cell, t.Arrive, ApproachLength + JunctionPenalty, net[t.Road.Nodes[t.K]]);
        Log?.Invoke($"{(tapsOnly ? "feeder" : "loop")} from {origin.X:0}/{origin.Y:0}: taps in reach {taps.Count(t => Arrival(t.Cell, t.Arrive).Cost < float.MaxValue)}, reached {Enumerable.Range(0, n * Dirs).Count(c => dist[c] < float.MaxValue)} states, taps {taps.Count}, best {best:0.00}, max ratio {maxRatio:0.00}");
        if (target == null) return false;
        var cells = Trace(targetState);   // from the start to the target
        cells.Reverse();                 // AddRoad expects: source (target of the cross connection) first
        if (startExit != null)
        {
            AddRoad(startExit, cells, target, gravel, cut && !gravel, loop: true);
            return true;
        }
        // start at a road: as a pseudo exit at the junction
        var p = startTap.P;
        var dir = Center(startTap.Cell) - p; dir.Normalize();
        var pseudo = new NetExit { Town = null, X = p.X, Z = p.Y, DX = dir.X, DZ = dir.Y, Width = 16, Cell = startTap.Cell };
        AddRoad(pseudo, cells, target, false, cut, loop: true, startTap: startTap);
        return true;
    }


    /// Does the path up to `state` wrap around a node tile to enter/leave the wrong exit?
    bool WrapsAroundNode(NetExit exit, int state, bool fromStart)
    {
        if (exit?.Town == null || !exit.Town.Node || exit.Tile == null) return false;
        var cells = Trace(state);
        if (!fromStart) cells.Reverse();
        return WrapsAroundTile(exit.Tile, exit.DX, exit.DZ, cells.Select(Center), ApproachLength);
    }

    /// Does a road leaving `tile` in direction (dx, dz) wrap around the tile (pass close behind the tile center or head
    /// more than ~110° backwards within the first 700 m)? Used to prevent node-tile exits from connecting to the wrong side.
    internal static bool WrapsAroundTile(PlacedPrefab tile, double dx, double dz, IEnumerable<Point> points, double startWalked = 0)
    {
        double cx = tile.X + tile.SizeX / 2.0, cz = tile.Z + tile.SizeZ / 2.0;
        double walked = startWalked;
        Point prev = default;
        bool hasPrev = false;
        foreach (var p in points)
        {
            if (hasPrev) walked += (p - prev).Length;
            prev = p; hasPrev = true;
            if (walked > 700) break;
            double vx = p.X - cx, vz = p.Y - cz;
            double along = vx * dx + vz * dz;
            double ex = Math.Max(0, Math.Max(tile.X - p.X, p.X - (tile.X + tile.SizeX)));
            double ez = Math.Max(0, Math.Max(tile.Z - p.Y, p.Y - (tile.Z + tile.SizeZ)));
            if (ex * ex + ez * ez < 240 * 240 && along < -20) return true;
            if (walked >= 200)
            {
                double r = Math.Sqrt(vx * vx + vz * vz);
                if (r > 1 && along / r < -0.35) return true;
            }
        }
        return false;
    }

    /// earthwork in front of the city exits of a path (like the check in Measure): how far the terrain along the first
    /// 320 m lies above/below the tile than even the highest gradient allows (m). For the dead-end attempts: an
    /// exit that points directly at a rock wall should not cut into it (Rifane-Land 3050/−2609: steps up to 8 m).
    double Earthwork(NetExit start, int state, NetExit end)
    {
        var cells = new List<int>();
        for (int s = state; s >= 0; s = parent[s]) cells.Add(s / Dirs);
        cells.Reverse();
        double worst = 0;
        foreach (var (e, list) in new[] { (start, cells), (end, Enumerable.Reverse(cells).ToList()) })
        {
            if (e?.Tile == null) continue;
            double floor = e.Tile.TerrainHeight, walked = ApproachLength;
            for (int q = 0; q < list.Count && walked <= 320; q++)
            {
                if (q > 0) walked += (Center(list[q]) - Center(list[q - 1])).Length;
                var p = Center(list[q]);
                worst = Math.Max(worst, Math.Abs(Sample(hb, p.X, p.Y) - floor) - walked * spec.MaxGrade);
            }
        }
        return worst;
    }
    /// length (m) over which the path up to this state runs closer than 120 m and at a similar height (under 10 m difference) beside another
    /// road – without the first and last 150 m (there it connects to the exit or junction)
    double ParallelLength(int state, double total)
    {
        var cells = new List<int>();
        for (int s = state; s >= 0; s = parent[s]) cells.Add(s / Dirs);
        double walked = 0, close = 0;
        for (int q = 1; q < cells.Count; q++)
        {
            double step = (Center(cells[q]) - Center(cells[q - 1])).Length;
            walked += step;
            if (walked < 150 || total - walked < 150) continue;
            int c = cells[q];
            if (nearRoadD[c] < 120 && Math.Abs(hb[c] - nearRoadH[c]) < 10) close += step;
        }
        return close;
    }

    static double TownDistanceTo(NetTown t, Point p)
    {
        double dx = Math.Max(0, Math.Max(t.X0 - p.X, p.X - t.X1)), dz = Math.Max(0, Math.Max(t.Z0 - p.Y, p.Y - t.Z1));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    // ---------------------------------------------------------------- evaluation

    void Measure(NetRoad r)
    {
        var s = r.Samples;
        var h = r.Cut ? hcut : hb;
        int steep = 0, count = 0;
        // gradient over 96 m (the road brush smooths the longitudinal profile over 100 m), without the straight connecting pieces at the ends
        for (int k = 3; k + 9 < s.Count; k++)
        {
            if (r.Bridges.Count > 0 && (NearBridge(r, s[k]) || NearBridge(r, s[k + 6]) || NearBridge(r, s[k + 3]))) continue;   // river valley under the bridge
            double g = Math.Abs(Sample(h, s[k + 6].X, s[k + 6].Y) - Sample(h, s[k].X, s[k].Y)) / 96;
            if (g > r.MaxGrade) { r.MaxGrade = g; r.SteepAt = s[k + 3]; Log?.Invoke($"   max {g * 100:0.0} % at {s[k].X:0}/{s[k].Y:0} ({k * 16} of {(s.Count - 1) * 16} m)"); }
            if (g > spec.Grade + 0.005) steep++;
            count++;
        }
        r.SteepShare = count > 0 ? steep / (double)count : 0;
        // short roads (no 96 m window above): gradient from end to end (tile height at exits, otherwise the terrain) – shows a
        // steep exception (up to ShortMaxGrade) in the list of places to check
        if (count == 0 && s.Count >= 2)
        {
            double len = Math.Max(1, (s.Count - 1) * 16.0);
            double End(NetExit e, Point p) => e?.Tile != null ? e.Tile.TerrainHeight : Sample(hb, p.X, p.Y);
            double g = Math.Abs(End(r.To, r.Points[^1]) - End(r.From, r.Points[0])) / len;
            if (g > r.MaxGrade) { r.MaxGrade = g; r.SteepAt = r.Points[r.Points.Count / 2]; }
            if (g > spec.Grade + 0.005) r.SteepShare = 1;
        }
        // connection to a city exit: terrain along the road (48–320 m) so far above/below the tile that even the
        // highest gradient does not suffice (hint; stage 2 has to fill or cut there)
        foreach (var (e, fromStart) in new[] { (r.From, true), (r.To, false) })
        {
            if (e?.Tile == null) continue;
            double floor = e.Tile.TerrainHeight, worst = 0, at = 0;
            for (int k = 3; k <= 20 && k < s.Count; k++)
            {
                var p = fromStart ? s[k] : s[s.Count - 1 - k];
                double need = Math.Abs(Sample(hb, p.X, p.Y) - floor) - k * 16 * spec.MaxGrade;
                if (need > worst) { worst = need; at = Sample(hb, p.X, p.Y) - floor; }
            }
            e.Earthwork = worst > 3 ? at : 0;
            string note = Loc.F("Town exit at {0}: the ground along the road is up to {1:0} m {2} than the tile – earthworks needed there.",
                Pos(e.X, e.Z), Math.Abs(at), at > 0 ? Loc.T("higher") : Loc.T("lower"));
            if (worst > 3 && !plan.Problems.Contains(note)) plan.Problems.Add(note);
        }
        if (r.Cut)
            foreach (var p in s)
            {
                double c = Sample(hb, p.X, p.Y) - Sample(hcut, p.X, p.Y);
                if (c > r.MaxCut) { r.MaxCut = c; r.CutAt = p; }
            }
    }

    /// (straight length, whole length): samples whose window over 288 m deviates at most 2 m from the chord
    static (double Straight, double Total) Straightness(List<Point> s)
    {
        if (s.Count < 19) return (0, Math.Max(0, (s.Count - 1) * 16.0));
        var straight = new bool[s.Count];
        for (int k = 0; k + 18 < s.Count; k++)
        {
            Point a = s[k], b = s[k + 18];
            bool ok = true;
            for (int q = k + 1; q < k + 18 && ok; q++) ok = SegDist(s[q], a, b) <= 2;
            if (ok) for (int q = k; q <= k + 18; q++) straight[q] = true;
        }
        return (straight.Count(x => x) * 16.0, (s.Count - 1) * 16.0);
    }
}
