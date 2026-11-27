using System.Text.Json;
using System.Windows;

namespace MapEditor.Map;

/// Saved road network plan (the user's request 2026-11-12: edit the planned network before building, keep the plan).
/// File yamee_roadplan.json in the project: towns with their free exits, all roads with points, kind, width and the
/// references between them (junctions, crossings) plus the settings used for building. The tiles of the exits are
/// stored by name and position; if one is missing on load (towns changed), the plan no longer fits and is not loaded.
public sealed class NetworkPlanFile
{
    public NetworkPlan Plan;
    public NetworkSpec Spec;
    public bool Built;          // already built once (building again would put the roads in twice)
    public DateTime? BuiltAt;
}

public static class NetworkPlanStore
{
    public const string FileName = "yamee_roadplan.json";
    static readonly JsonSerializerOptions Options = new() { IncludeFields = true, WriteIndented = false };

    public static string PathOf(WorldData w) => Path.Combine(w.Folder, FileName);

    /// settings as text (to see whether two plans were made with the same settings)
    public static string SpecText(NetworkSpec s) => JsonSerializer.Serialize(s, Options);

    // ---------- file format ----------
    sealed class Dto
    {
        public int Version = 1;
        public bool Built;
        public DateTime? BuiltAt;
        public NetworkSpec Spec;
        public List<TownDto> Towns = new();
        public List<ExitDto> Exits = new();
        public List<RoadDto> Roads = new();
        public double[] Junctions, Crossings;
        public List<TileRef> NodeTiles = new();   // planned node tiles (not on the map yet)
        public List<string> Problems = new();
        public double StraightShare, Seconds;
    }
    sealed class TownDto { public int Id, X0, Z0, X1, Z1; public bool OldWest, Connected, Linked, Node; public int[] Exits; }
    sealed class TileRef { public string Name; public int X, Y, Z, Rotation; }
    sealed class ExitDto
    {
        public int Town = -1; public TileRef Tile;
        public double X, Z, DX, DZ, StreetHalf, StreetDX, StreetDZ, Earthwork; public int Width, Cell;
        public bool Gravel, Gateway, Used, Street;
    }
    sealed class RoadDto
    {
        public double[] Points, Crossings, Via; public int[] CrossedRoads;
        public bool Gravel, Loop, Feeder, TownLink, Cut, Manual, ToEdge;
        public double Width, Length, MaxGrade, SteepShare, MaxCut;
        public double[] SteepAt, CutAt;
        public int From = -1, To = -1, StartRoad = -1, EndRoad = -1;
        public double[] Junction;
        public List<BridgeSite> Bridges;   // bridges over rivers on the road (RoadNetwork.Bridges)
    }

    static double[] Flat(IEnumerable<Point> pts) => pts.SelectMany(p => new[] { p.X, p.Y }).ToArray();
    static List<Point> Unflat(double[] a)
    {
        var res = new List<Point>();
        if (a != null) for (int k = 0; k + 1 < a.Length; k += 2) res.Add(new Point(a[k], a[k + 1]));
        return res;
    }

    /// plan as text (file content and snapshot for "undo" in the plan)
    public static string Serialize(NetworkPlanFile f)
    {
        var plan = f.Plan;
        var exits = new List<NetExit>();
        var exitIndex = new Dictionary<NetExit, int>();
        int ExitId(NetExit e)
        {
            if (e == null) return -1;
            if (!exitIndex.TryGetValue(e, out int i)) { i = exits.Count; exits.Add(e); exitIndex[e] = i; }
            return i;
        }
        var townIndex = plan.Towns.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
        var roadIndex = plan.Roads.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i);
        int RoadId(NetRoad r) => r != null && roadIndex.TryGetValue(r, out int i) ? i : -1;

        var dto = new Dto
        {
            Built = f.Built, BuiltAt = f.BuiltAt, Spec = f.Spec,
            Junctions = Flat(plan.Junctions), Crossings = Flat(plan.CrossingPoints),
            Problems = plan.Problems.ToList(), StraightShare = plan.StraightShare, Seconds = plan.Seconds,
            NodeTiles = plan.NodeTiles.Select(p => new TileRef { Name = p.Name, X = p.X, Y = p.Y, Z = p.Z, Rotation = p.Rotation }).ToList(),
        };
        foreach (var t in plan.Towns)
            dto.Towns.Add(new TownDto { Id = t.Id, X0 = t.X0, Z0 = t.Z0, X1 = t.X1, Z1 = t.Z1, OldWest = t.OldWest, Connected = t.Connected, Linked = t.Linked, Node = t.Node, Exits = t.Exits.Select(ExitId).ToArray() });
        foreach (var r in plan.Roads)
            dto.Roads.Add(new RoadDto
            {
                Points = Flat(r.Points), Crossings = Flat(r.Crossings), Via = Flat(r.Via), CrossedRoads = r.CrossedRoads.Select(RoadId).ToArray(),
                Gravel = r.Gravel, Loop = r.Loop, Feeder = r.Feeder, TownLink = r.TownLink, Cut = r.Cut, Manual = r.Manual, ToEdge = r.ToEdge,
                Width = r.Width, Length = r.Length, MaxGrade = r.MaxGrade, SteepShare = r.SteepShare, MaxCut = r.MaxCut,
                From = ExitId(r.From), To = ExitId(r.To), StartRoad = RoadId(r.StartRoad), EndRoad = RoadId(r.EndRoad),
                Junction = r.Junction is Point j ? new[] { j.X, j.Y } : null,
                SteepAt = r.SteepAt is Point sa ? new[] { sa.X, sa.Y } : null, CutAt = r.CutAt is Point ca ? new[] { ca.X, ca.Y } : null,
                Bridges = r.Bridges.Count > 0 ? r.Bridges.ToList() : null,
            });
        foreach (var e in exits)   // the list grows while exits of roads are added – all are in it now
            dto.Exits.Add(new ExitDto
            {
                Town = e.Town != null && townIndex.TryGetValue(e.Town, out int ti) ? ti : -1,
                Tile = e.Tile == null ? null : new TileRef { Name = e.Tile.Name, X = e.Tile.X, Y = e.Tile.Y, Z = e.Tile.Z, Rotation = e.Tile.Rotation },
                X = e.X, Z = e.Z, DX = e.DX, DZ = e.DZ, Width = e.Width, Gravel = e.Gravel, Gateway = e.Gateway, Used = e.Used,
                Street = e.Street, StreetHalf = e.StreetHalf, StreetDX = e.StreetDX, StreetDZ = e.StreetDZ, Cell = e.Cell, Earthwork = e.Earthwork,
            });
        return JsonSerializer.Serialize(dto, Options);
    }

    /// plan from text; null + reason if it does not fit the world (tiles missing)
    public static NetworkPlanFile Deserialize(WorldData w, string text, out string problem)
    {
        problem = null;
        var dto = JsonSerializer.Deserialize<Dto>(text, Options);
        if (dto == null || dto.Version != 1) { problem = Loc.T("The saved road plan has an unknown format."); return null; }
        var plan = new NetworkPlan
        {
            Junctions = Unflat(dto.Junctions), CrossingPoints = Unflat(dto.Crossings),
            Problems = dto.Problems ?? new(), StraightShare = dto.StraightShare, Seconds = dto.Seconds,
        };
        // planned node tiles: description from the game folder (RoadNetwork.TileDefs)
        foreach (var n in dto.NodeTiles ?? new())
        {
            var def = RoadNetwork.TileDefs?.Invoke(n.Name);
            if (def == null) { problem = Loc.T("The saved road plan contains node tiles that are not in the game folder. Please plan again."); return null; }
            plan.NodeTiles.Add(new PlacedPrefab { Name = n.Name, Def = def, X = n.X, Y = n.Y, Z = n.Z, Rotation = n.Rotation });
        }
        foreach (var t in dto.Towns)
        {
            var town = new NetTown { Id = t.Id, X0 = t.X0, Z0 = t.Z0, X1 = t.X1, Z1 = t.Z1, OldWest = t.OldWest, Connected = t.Connected, Linked = t.Linked, Node = t.Node };
            if (t.Node) town.Tiles.AddRange(plan.NodeTiles.Where(p => p.X == t.X0 && p.Z == t.Z0));
            plan.Towns.Add(town);
        }
        var tiles = w.Prefabs.Where(p => p.IsTile).Concat(plan.NodeTiles).ToList();
        var exits = new List<NetExit>();
        foreach (var e in dto.Exits)
        {
            PlacedPrefab tile = null;
            if (e.Tile != null)
            {
                tile = tiles.FirstOrDefault(p => p.X == e.Tile.X && p.Z == e.Tile.Z && p.Y == e.Tile.Y && p.Rotation == e.Tile.Rotation
                    && string.Equals(p.Name, e.Tile.Name, StringComparison.OrdinalIgnoreCase));
                if (tile == null) { problem = Loc.T("The towns on the map have changed since the road network was planned. Please plan again."); return null; }
            }
            exits.Add(new NetExit
            {
                Town = e.Town >= 0 && e.Town < plan.Towns.Count ? plan.Towns[e.Town] : null, Tile = tile,
                X = e.X, Z = e.Z, DX = e.DX, DZ = e.DZ, Width = e.Width, Gravel = e.Gravel, Gateway = e.Gateway, Used = e.Used,
                Street = e.Street, StreetHalf = e.StreetHalf, StreetDX = e.StreetDX, StreetDZ = e.StreetDZ, Cell = e.Cell, Earthwork = e.Earthwork,
            });
        }
        for (int t = 0; t < dto.Towns.Count; t++)
            foreach (int i in dto.Towns[t].Exits ?? Array.Empty<int>()) if (i >= 0 && i < exits.Count) plan.Towns[t].Exits.Add(exits[i]);
        NetExit Ex(int i) => i >= 0 && i < exits.Count ? exits[i] : null;
        foreach (var r in dto.Roads)
            plan.Roads.Add(new NetRoad
            {
                Points = Unflat(r.Points), Crossings = Unflat(r.Crossings), Via = Unflat(r.Via),
                Gravel = r.Gravel, Loop = r.Loop, Feeder = r.Feeder, TownLink = r.TownLink, Cut = r.Cut, Manual = r.Manual, ToEdge = r.ToEdge,
                Width = r.Width, Length = r.Length, MaxGrade = r.MaxGrade, SteepShare = r.SteepShare, MaxCut = r.MaxCut,
                From = Ex(r.From), To = Ex(r.To), Junction = r.Junction is { Length: 2 } j ? new Point(j[0], j[1]) : null,
                SteepAt = r.SteepAt is { Length: 2 } sa ? new Point(sa[0], sa[1]) : null, CutAt = r.CutAt is { Length: 2 } ca ? new Point(ca[0], ca[1]) : null,
                Bridges = r.Bridges ?? new(),
            });
        NetRoad Rd(int i) => i >= 0 && i < plan.Roads.Count ? plan.Roads[i] : null;
        for (int k = 0; k < dto.Roads.Count; k++)
        {
            var r = plan.Roads[k];
            r.StartRoad = Rd(dto.Roads[k].StartRoad);
            r.EndRoad = Rd(dto.Roads[k].EndRoad);
            foreach (int c in dto.Roads[k].CrossedRoads ?? Array.Empty<int>()) r.CrossedRoads.Add(Rd(c));
        }
        return new NetworkPlanFile { Plan = plan, Spec = dto.Spec ?? new NetworkSpec(), Built = dto.Built, BuiltAt = dto.BuiltAt };
    }

    public static void Save(WorldData w, NetworkPlanFile f)
    {
        string path = PathOf(w), tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(f));
        File.Move(tmp, path, true);
    }

    /// saved plan of the world; null = none (problem = null) or does not fit (problem = reason)
    public static NetworkPlanFile Load(WorldData w, out string problem)
    {
        problem = null;
        string path = PathOf(w);
        if (!File.Exists(path)) return null;
        try { return Deserialize(w, File.ReadAllText(path), out problem); }
        catch (Exception ex) { problem = Loc.T("The saved road plan could not be read.") + " " + ex.Message; return null; }
    }

    public static void Delete(WorldData w)
    {
        string path = PathOf(w);
        if (File.Exists(path)) File.Delete(path);
    }
}

/// Changing a planned network by hand (stage 1: delete, kind, width). Order and references stay valid for the builder:
/// a road only connects to roads earlier in the list.
/// place of the plan that should be looked at (list in the window, click = map jumps there)
public sealed record NetSpot(Point P, string Text);

public static class NetworkPlanEdit
{
    /// places to check (the user's request 2026-11-12, stage 4; shortened 2026-11-26): towns not connected and unused
    /// town exits (dead ends), within a kind from west to east
    public static List<NetSpot> Spots(NetworkPlan plan, NetworkSpec spec)
    {
        static string Pos(Point p) => Loc.F("{0} {1}, {2} {3}", Math.Abs(Math.Round(p.X)), p.X < 0 ? Loc.T("W") : Loc.T("E"), Math.Abs(Math.Round(p.Y)), p.Y < 0 ? Loc.T("S") : Loc.T("N"));
        var list = new List<NetSpot>();
        foreach (var t in plan.Towns.Where(t => !t.Connected).OrderBy(t => t.CX))
            list.Add(new NetSpot(new Point(t.CX, t.CZ), Loc.F("Town not connected ({0})", Pos(new Point(t.CX, t.CZ)))));
        foreach (var e in plan.Towns.SelectMany(t => t.Exits).Where(e => e.Reachable && !e.Used).OrderBy(e => e.X))
            list.Add(new NetSpot(new Point(e.X, e.Z), Loc.F("Unused town exit – dead end ({0})", Pos(new Point(e.X, e.Z)))));
        // steep places, clearings and town exits with earthworks are no longer listed (the user's request 2026-11-26: too much information)
        return list;
    }

    /// share (0..1) of sample points along the planned roads (every 20 m, without 60 m at the ends) that have
    /// asphalt or gravel on the map – tells whether a plan marked as built is really still on the map
    /// (towns replaced, terrain copy restored, backup loaded …: the roads are gone, the file still says "built")
    public static double ShareOnMap(WorldData w, NetworkPlan plan)
    {
        int n = 0, hit = 0;
        foreach (var r in plan.Roads)
        {
            var pts = r.Points;
            if (pts == null || pts.Count < 2) continue;
            double total = 0;
            for (int i = 1; i < pts.Count; i++) total += (pts[i] - pts[i - 1]).Length;
            for (double d = 60; d <= total - 60; d += 20)
            {
                var p = RoadNetwork.At(pts, d).P;
                n++;
                bool paved = false;
                for (int dz = -1; dz <= 1 && !paved; dz++)
                    for (int dx = -1; dx <= 1 && !paved; dx++)
                        paved = w.RoadAt((int)Math.Floor(p.X) + dx, (int)Math.Floor(p.Y) + dz) != 0;
                if (paved) hit++;
            }
        }
        return n == 0 ? 0 : (double)hit / n;
    }

    /// roads that branch off this road at their start or end (directly)
    public static List<NetRoad> Dependents(NetworkPlan plan, NetRoad road) =>
        plan.Roads.Where(r => r != road && (r.StartRoad == road || r.EndRoad == road)).ToList();

    /// delete a road; withDependents = also the roads that branch off it (and theirs), otherwise they keep a free end
    /// (dead end). Returns the number of deleted roads.
    public static int Delete(NetworkPlan plan, NetRoad road, bool withDependents)
    {
        var gone = new HashSet<NetRoad> { road };
        if (withDependents)
        {
            var queue = new Queue<NetRoad>(new[] { road });
            while (queue.Count > 0)
                foreach (var d in Dependents(plan, queue.Dequeue()))
                    if (gone.Add(d)) queue.Enqueue(d);
        }
        plan.Roads.RemoveAll(gone.Contains);
        foreach (var r in plan.Roads)
        {
            if (r.StartRoad != null && gone.Contains(r.StartRoad)) r.StartRoad = null;
            if (r.EndRoad != null && gone.Contains(r.EndRoad)) { r.EndRoad = null; r.Junction = null; }
            for (int c = r.CrossedRoads.Count - 1; c >= 0; c--)
                if (r.CrossedRoads[c] == null || gone.Contains(r.CrossedRoads[c]))
                {
                    r.CrossedRoads.RemoveAt(c);
                    if (c < r.Crossings.Count) r.Crossings.RemoveAt(c);
                }
        }
        Refresh(plan);
        return gone.Count;
    }

    /// change asphalt ↔ gravel; the width becomes the usual one of the kind
    public static void SetGravel(NetRoad road, bool gravel)
    {
        if (road.Gravel == gravel) return;
        road.Gravel = gravel;
        road.Width = gravel ? Math.Clamp(road.From?.Width ?? road.To?.Width ?? 8, 6, 12) : 16;
    }

    /// After a road got a new course: find its crossings with the other roads again. A crossing belongs to the road that is
    /// built later (it takes the height of the earlier one, see NetworkBuilder); near the ends of a road (junctions) nothing
    /// counts as a crossing.
    public static void UpdateCrossings(NetworkPlan plan, NetRoad road)
    {
        road.Crossings.Clear();
        road.CrossedRoads.Clear();
        foreach (var o in plan.Roads)
            for (int c = o.CrossedRoads.Count - 1; c >= 0; c--)
                if (o.CrossedRoads[c] == road) { o.CrossedRoads.RemoveAt(c); if (c < o.Crossings.Count) o.Crossings.RemoveAt(c); }
        int mine = plan.Roads.IndexOf(road);
        foreach (var o in plan.Roads)
        {
            if (o == road || o.Points.Count < 2) continue;
            var found = new List<Point>();
            for (int q = 0; q + 1 < road.Points.Count; q++)
                for (int k = 0; k + 1 < o.Points.Count; k++)
                {
                    if (Intersect(road.Points[q], road.Points[q + 1], o.Points[k], o.Points[k + 1]) is not Point p) continue;
                    if (NearEnd(road.Points, p) || NearEnd(o.Points, p) || found.Any(f => (f - p).Length < 30)) continue;
                    found.Add(p);
                }
            foreach (var p in found)
            {
                if (plan.Roads.IndexOf(o) < mine) { road.Crossings.Add(p); road.CrossedRoads.Add(o); }
                else { o.Crossings.Add(p); o.CrossedRoads.Add(road); }
            }
        }
    }

    /// roads that depend on this road: they branch off it, or off a road that does (they must be built after it)
    public static HashSet<NetRoad> DependsOn(NetworkPlan plan, NetRoad road)
    {
        var set = new HashSet<NetRoad>();
        var queue = new Queue<NetRoad>(new[] { road });
        while (queue.Count > 0)
        {
            var r = queue.Dequeue();
            foreach (var d in plan.Roads)
                if ((d.StartRoad == r || d.EndRoad == r) && set.Add(d)) queue.Enqueue(d);
        }
        return set;
    }

    /// Order of the roads after a junction moved: every road comes after the roads it branches off (the builder takes
    /// their height there), otherwise the order stays. Crossings are then found again for all roads (a crossing belongs to
    /// the road built later). false = the junctions form a circle (nothing changed).
    public static bool Reorder(NetworkPlan plan)
    {
        var done = new HashSet<NetRoad>();
        var order = new List<NetRoad>();
        while (order.Count < plan.Roads.Count)
        {
            var next = plan.Roads.FirstOrDefault(r => !done.Contains(r) && (r.StartRoad == null || done.Contains(r.StartRoad) || !plan.Roads.Contains(r.StartRoad))
                && (r.EndRoad == null || done.Contains(r.EndRoad) || !plan.Roads.Contains(r.EndRoad)));
            if (next == null) return false;
            done.Add(next);
            order.Add(next);
        }
        bool changed = !order.SequenceEqual(plan.Roads);
        plan.Roads.Clear();
        plan.Roads.AddRange(order);
        if (changed) foreach (var r in order) UpdateCrossings(plan, r);
        return true;
    }

    static bool NearEnd(List<Point> line, Point p) => (line[0] - p).Length < 25 || (line[^1] - p).Length < 25;

    static Point? Intersect(Point a, Point b, Point c, Point d)
    {
        Vector r = b - a, s = d - c;
        double den = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(den) < 1e-9) return null;
        Vector ca = c - a;
        double t = (ca.X * s.Y - ca.Y * s.X) / den, u = (ca.X * r.Y - ca.Y * r.X) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? a + r * t : null;
    }

    /// junction and crossing marks, used exits and connected towns from the roads
    public static void Refresh(NetworkPlan plan)
    {
        plan.Junctions.Clear();
        plan.CrossingPoints.Clear();
        foreach (var r in plan.Roads)
        {
            if (r.Points.Count == 0) continue;
            if (r.StartRoad != null || r.From?.Street == true) plan.Junctions.Add(r.Points[0]);
            if (r.EndRoad != null || r.To?.Street == true) plan.Junctions.Add(r.Junction ?? r.Points[^1]);
            plan.CrossingPoints.AddRange(r.Crossings);
        }
        var used = new HashSet<NetExit>(plan.Roads.SelectMany(r => new[] { r.From, r.To }).Where(e => e != null));
        foreach (var t in plan.Towns)
        {
            foreach (var e in t.Exits) e.Used = used.Contains(e);
            t.Connected = t.Linked || used.Any(e => e.Town == t);
        }
    }
}
