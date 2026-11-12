using System.Windows;

namespace MapEditor.Map;

/// result of "Build roads"
public sealed class NetworkBuildResult
{
    public int Roads, HeightPoints, Pixels;
    public double Length, MaxGrade, MaxCut, MaxFill;
    public double KinkLength;
    public double FillLength, LongestFill;  // embankment stretches (> 1.5 m above the terrain): total and longest
    public int SteepRoads;                // roads that had to become steeper than the usual gradient
    public List<string> Warnings = new();
}

/// Build roads between the cities (stage 2, the user's request, 2026-11-07): every planned road of the network (RoadNetwork)
/// is entered like a road-brush road – longitudinal profile with smoothing, embankments, surface. In the order of the plan:
/// each road only connects to earlier planned (so already built) roads.
/// • City exit: start 1 m in the tile (tile height, 4 m horizontal – automatic of the road brush), narrow exits
///   (8 m) widen to 16 m over 48 m.
/// • Junction (start/end on a built road): height of the road there, the piece on it and 2 m before fixed, tilted sideways
///   like the road rises there (at most 12 %); on its area the new road writes no heights.
/// • Intersection in the middle of the route: likewise, over the width of the crossed road + 3 m per side.
/// • Gradient: first with the usual gradient; if the cut/fill becomes deeper than 12 m (25 m for clearings), with the
///   gradient for narrow passages.
/// Long roads are entered piece by piece (RoadApplier with from/to), otherwise the grid would need hundreds of MB.
public static class NetworkBuilder
{
    const double TaperLength = 48;   // widening from the exit width to 16 m (m)
    const int SliceLength = 1600;    // waypoints per piece when entering (0.25 m → 400 m)

    public static NetworkBuildResult Build(WorldData w, WorldEdits e, NetworkPlan plan, NetworkSpec spec, IProgress<string> progress)
    {
        var res = new NetworkBuildResult();
        var built = new Dictionary<NetRoad, RoadResult>();
        var quiet = new Progress<string>(_ => { });
        for (int q = 0; q < plan.Roads.Count; q++)
        {
            var road = plan.Roads[q];
            progress?.Report(Loc.F("Building road {0} of {1} ({2:0.0} km) …", q + 1, plan.Roads.Count, road.Length / 1000));
            var rr = PlanRoad(w, road, spec, built, res);
            if (rr == null) { res.Warnings.Add(Loc.F("Road at {0} could not be built.", Pos(road.Points[0]))); continue; }
            for (int from = 0; from < rr.Path.Count; from += SliceLength)
            {
                var st = RoadApplier.Apply(w, e, rr, quiet, from, Math.Min(rr.Path.Count, from + SliceLength), 4);
                res.HeightPoints += st.HeightPoints;
                res.Pixels += st.Pixels;
            }
            built[road] = rr;
            res.Roads++;
            res.Length += rr.Length;
            res.MaxGrade = Math.Max(res.MaxGrade, rr.MaxGrade);
            res.MaxCut = Math.Max(res.MaxCut, rr.MaxCut);
            res.MaxFill = Math.Max(res.MaxFill, rr.MaxFill);
            res.FillLength += rr.FillLength;
            res.KinkLength += rr.KinkLength;
            res.LongestFill = Math.Max(res.LongestFill, rr.LongestFill);
            if (rr.MaxGrade > spec.Grade + 0.005) res.SteepRoads++;
            // buildings on the roadway – without those on lots of a tile (at a city exit the road starts 1 m in the
            // tile, everything stays as it is there)
            foreach (var o in rr.Obstacles.Where(o => o.Edge < 0 && !o.Prefab.IsTile && !InTile(w, o.Prefab)).Take(3))
                res.Warnings.Add(Loc.F("Road at {0} runs over {1}.", Pos(new Point(o.Prefab.X + o.Prefab.SizeX / 2.0, o.Prefab.Z + o.Prefab.SizeZ / 2.0)), o.Prefab.Name));
            if (rr.WaterArea > 50) res.Warnings.Add(Loc.F("Road at {0} touches water ({1} m²).", Pos(road.Points[road.Points.Count / 2]), rr.WaterArea));
        }
        return res;
    }

    static bool InTile(WorldData w, PlacedPrefab p)
    {
        double cx = p.X + p.SizeX / 2.0, cz = p.Z + p.SizeZ / 2.0;
        return w.Prefabs.Any(t => t.IsTile && t.Contains(cx, cz));
    }

    static string Pos(Point p) => Loc.F("{0} {1}, {2} {3}", Math.Abs(Math.Round(p.X)), p.X < 0 ? Loc.T("W") : Loc.T("E"), Math.Abs(Math.Round(p.Y)), p.Y < 0 ? Loc.T("S") : Loc.T("N"));

    /// height, direction and gradient of a built road at the place nearest to p
    internal static (double Height, double DX, double DZ, double Grade, double Half) At(RoadResult r, Point p)
    {
        int best = 0; double bd = double.MaxValue;
        for (int k = 0; k < r.Path.Count; k += 2)
        {
            double d = (r.Path[k].X - p.X) * (r.Path[k].X - p.X) + (r.Path[k].Z - p.Y) * (r.Path[k].Z - p.Y);
            if (d < bd) { bd = d; best = k; }
        }
        int a = r.IndexAt(Math.Max(0, r.Path[best].Dist - 10)), b = r.IndexAt(Math.Min(r.Length, r.Path[best].Dist + 10));
        double len = Math.Max(1, r.Path[b].Dist - r.Path[a].Dist);
        var (nx, nz) = RoadPlanner.Normal(r.Path, best);
        return (r.Profile[best], nz, -nx, (r.Profile[b] - r.Profile[a]) / len, r.WidthAt(best) / 2 + r.Settings.Shoulder);
    }

    static RoadResult PlanRoad(WorldData w, NetRoad road, NetworkSpec spec, Dictionary<NetRoad, RoadResult> built, NetworkBuildResult res)
    {
        // control points: 1 m in the start/end tile, between them the planned line every 12 m (even spacing, otherwise
        // the curve of the road brush overshoots)
        var pts = new List<Point>();
        if (road.From?.Tile != null) pts.Add(new Point(road.From.X - road.From.DX, road.From.Z - road.From.DZ));
        pts.AddRange(Even(road.Points, 12));
        if (road.To?.Tile != null) pts.Add(new Point(road.To.X - road.To.DX, road.To.Z - road.To.DZ));

        var s = road.Gravel ? RoadSettings.Gravel() : RoadSettings.Asphalt();
        s.Width = road.Width;
        s.Shoulder = road.Gravel ? 0 : 1;
        s.Smooth = road.Gravel ? 40 : 100;
        s.Blend = road.Gravel ? 8 : 12;
        s.CutSlope = 1.5;
        if (spec.RoadFill >= 0) { s.TubeFill = spec.RoadFill; s.TubeCut = spec.RoadCut; s.TubeEdgeFill = spec.RoadEdgeFill; }

        // width per control point: narrow city exits widen to the full width over TaperLength
        var dist = new double[pts.Count];
        for (int k = 1; k < pts.Count; k++) dist[k] = dist[k - 1] + (pts[k] - pts[k - 1]).Length;
        double total = dist[^1];
        double? startW = !road.Gravel && (road.From?.Tile != null || road.From?.Street == true) && road.From.Width < road.Width ? road.From.Width : null;
        double? endW = !road.Gravel && road.To?.Tile != null && road.To.Width < road.Width ? road.To.Width : null;
        List<double?> widths = null;
        if (startW != null || endW != null)
        {
            widths = new List<double?>();
            for (int k = 0; k < pts.Count; k++)
            {
                double wv = road.Width;
                if (startW is double a && dist[k] < TaperLength) { double t = dist[k] / TaperLength; wv = Math.Min(wv, a + (road.Width - a) * t * t * (3 - 2 * t)); }
                if (endW is double b && total - dist[k] < TaperLength) { double t = (total - dist[k]) / TaperLength; wv = Math.Min(wv, b + (road.Width - b) * t * t * (3 - 2 * t)); }
                widths.Add(wv);
            }
        }

        // connections to built roads: start/end (junction) and intersections
        var heights = new double?[pts.Count];
        var joints = new List<(Point P, NetRoad Other, bool AtStart, bool AtEnd)>();
        if (road.StartRoad != null) joints.Add((pts[0], road.StartRoad, true, false));
        if (road.EndRoad != null) joints.Add((pts[^1], road.EndRoad, false, true));
        for (int c = 0; c < road.Crossings.Count && c < road.CrossedRoads.Count; c++) joints.Add((road.Crossings[c], road.CrossedRoads[c], false, false));
        var known = joints.Where(j => built.ContainsKey(j.Other)).Select(j => (j.P, Info: At(built[j.Other], j.P), j.AtStart, j.AtEnd)).ToList();
        // town connection: starts on an existing town street – its height, gradient and width as with a built road
        if (road.From?.Street == true)
        {
            var f = road.From;
            double H(double x, double z) => w.HeightAt((int)Math.Floor(x), (int)Math.Floor(z));
            double grade = (H(f.X + f.StreetDX * 10, f.Z + f.StreetDZ * 10) - H(f.X - f.StreetDX * 10, f.Z - f.StreetDZ * 10)) / 20;
            known.Add((pts[0], (H(f.X, f.Z), f.StreetDX, f.StreetDZ, grade, f.StreetHalf), true, false));
        }
        foreach (var j in known)
        {
            if (j.AtStart) heights[0] = j.Info.Height;
            if (j.AtEnd) heights[^1] = j.Info.Height;
        }

        RoadResult Compute(double grade)
        {
            s.MaxGrade = grade;
            var r0 = RoadPlanner.Compute(w, pts, s, heights, widths: widths);
            if (r0 == null || known.Count == 0) return r0;
            var fixes = new List<(double, double)>();
            var cross = new List<(double, double, double)>();
            var zones = new List<(Point, double)>();
            foreach (var (p, info, atStart, atEnd) in known)
            {
                double d = atStart ? 0 : atEnd ? r0.Length : r0.Path[Nearest(r0, p)].Dist;
                double reach = info.Half + 2;
                if (atStart) for (double x = 0; x <= reach + 0.01; x += 1) fixes.Add((x, info.Height));
                else if (atEnd) for (double x = 0; x <= reach + 0.01; x += 1) fixes.Add((r0.Length - x, info.Height));
                else for (double x = -reach - 1; x <= reach + 1.01; x += 1) fixes.Add((d + x, info.Height));
                // tilted sideways like the crossed road rises there (its share across the new road)
                var (nx, nz) = RoadPlanner.Normal(r0.Path, r0.IndexAt(d));
                double slope = Math.Clamp(info.Grade * (info.DX * nx + info.DZ * nz), -0.12, 0.12);
                if (Math.Abs(slope) > 0.001) cross.Add((d, slope, info.Half + 3));
                zones.Add((p, info.Half + 1));
            }
            var r1 = RoadPlanner.Compute(w, pts, s, heights, fixes, cross, widths);
            if (r1 != null) r1.ProtectZones = zones;
            return r1;
        }

        var r = Compute(spec.Grade);
        if (r == null) return null;
        double limit = road.Cut ? 25 : 12;
        // embankments count double (they stand out, wide slope): cut/fill too deep or long embankment stretch → check with the higher
        // gradient whether it gives less earthwork
        double Earth(RoadResult q) => Math.Max(q.MaxCut, q.MaxFill);
        if ((Math.Max(r.MaxCut, r.MaxFill) > limit || r.LongestFill > 300) && spec.MaxGrade > spec.Grade)
        {
            var steeper = Compute(spec.MaxGrade);
            if (steeper != null && Earth(steeper) + steeper.LongestFill / 200 < Earth(r) + r.LongestFill / 200) r = steeper;
        }
        return r;
    }

    static int Nearest(RoadResult r, Point p)
    {
        int best = 0; double bd = double.MaxValue;
        for (int k = 0; k < r.Path.Count; k++)
        {
            double d = (r.Path[k].X - p.X) * (r.Path[k].X - p.X) + (r.Path[k].Z - p.Y) * (r.Path[k].Z - p.Y);
            if (d < bd) { bd = d; best = k; }
        }
        return best;
    }

    /// line at even spacing (at most step), start and end stay
    internal static List<Point> Even(List<Point> line, double step)
    {
        double total = 0;
        for (int q = 1; q < line.Count; q++) total += (line[q] - line[q - 1]).Length;
        int parts = Math.Max(1, (int)Math.Ceiling(total / step));
        double each = total / parts;
        var res = new List<Point> { line[0] };
        double target = each, walked = 0;
        for (int q = 1; q < line.Count && res.Count < parts; q++)
        {
            var a = line[q - 1]; var b = line[q];
            double len = (b - a).Length;
            while (len > 0 && walked + len >= target - 1e-9 && res.Count < parts)
            {
                res.Add(a + (b - a) * ((target - walked) / len));
                target += each;
            }
            walked += len;
        }
        res.Add(line[^1]);
        return res;
    }
}
