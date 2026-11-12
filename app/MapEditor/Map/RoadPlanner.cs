using System.Windows;

namespace MapEditor.Map;

/// Remove = remove existing surface (eraser) and adjust the road bed to the surroundings
public enum Surface { Asphalt, Gravel, Remove }

/// settings of a road. Default values = values tested in the game (marker 3 or gravel path adjustment).
public sealed class RoadSettings
{
    public Surface Surface;
    public double Width;        // roadway (m)
    public double Shoulder;     // gravel edge per side (m), asphalt only
    public double GrassEdge;    // grass edge per side (m): additionally leveled horizontally at road height, without surface (ramps into mountains/into the ground)
    public double MaxGrade;     // highest gradient (0.08 = 0.8 m per 10 m)
    public double Smooth;       // length of the smoothing window along the road (m)
    public double Blend;        // slope beside the road, at least (m)
    public double CutSlope;     // > 0: slope = height difference × CutSlope (at most MaxSlopeWidth)
    public bool Reshape = true; // removal only: fill the old road bed from the terrain all around
    public bool KeepCrossings = true;   // removal only: paths that run across the road are preserved
    public double JunctionTolerance = 0.02;   // intersections count as level up to this gradient (otherwise adjusted)
    public double MaxCrossSlope;              // so far the new road may be tilted sideways at intersections (fraction)
    public bool PaintSurface = true;   // false: only adjust the terrain, do not paint surface (rebuild of an existing road at an intersection)
    public double HeightOffset; // height offset of the whole road relative to the terrain (m, + = higher); connections to tiles/buildings stay
    public double OffsetRamp;   // > 0: the offset grows from both ends over this many meters (instead of up to the second/second-to-last control point)
    public double[] OffsetAlong;  // additional offset per 4 m along the road (index = distance / 4, m; wilderness paths: cut into the slope)
    public double FlatStart = -1;    // < 0 = automatic (4 m at tiles/buildings, otherwise 0); so many meters at the start the road stays horizontal at the height of the start (the user's request 2026-11-04)
    public double FlatEnd = -1;      // the same at the end
    public bool UniformGrade;   // even gradient from start to end (between fixed points), independent of the terrain
    /// tube (the user's request 2026-11-10: roads lay on embankments over long stretches): >= 0 = the longitudinal profile is the tautest
    /// line inside a tube around the terrain – at most TubeFill m above (embankment), at most TubeCut m below (cut),
    /// at the roadway edges at most TubeEdgeFill m above the lower edge. < 0 = off (moving average over Smooth).
    public double TubeFill = -1, TubeCut = 1.8, TubeEdgeFill = 2.0;

    // banking (the user's request 2026-10-28): asphalt more careful (17 m wide: 4 % = 0.7 m from edge to edge)
    public static RoadSettings Asphalt() => new() { Surface = Surface.Asphalt, Width = 16, Shoulder = 1, MaxGrade = 0.08, Smooth = 100, Blend = 12, CutSlope = 1.5, MaxCrossSlope = 0.04 };
    public static RoadSettings Gravel() => new() { Surface = Surface.Gravel, Width = 5, Shoulder = 0, MaxGrade = 0.10, Smooth = 20, Blend = 8, CutSlope = 1.5, MaxCrossSlope = 0.06 };
    public static RoadSettings Remove() => new() { Surface = Surface.Remove, Width = 20, Shoulder = 0, MaxGrade = 0.10, Smooth = 30, Blend = 10, CutSlope = 0 };
    public static RoadSettings For(Surface s) => s switch { Surface.Asphalt => Asphalt(), Surface.Gravel => Gravel(), _ => Remove() };
    public RoadSettings Clone() => (RoadSettings)MemberwiseClone();
}

/// point of the center line: position, distance from the start, number of the section between two control points
public readonly record struct PathPoint(double X, double Z, double Dist, int Segment);

/// place where the center line runs over an existing road
public sealed record Crossing(double Dist, double X, double Z, byte Kind, double RoadHeight, double TerrainHeight, double Length, bool AtEnd)
{
    public double Diff => RoadHeight - TerrainHeight;
    /// height difference too large: the crossed path has to be adjusted (connections at the ends excepted)
    public bool NeedsWork => !AtEnd && Math.Abs(Diff) > 1;
    public string KindName => Kind == WorldData.Asphalt ? "Asphalt" : "Gravel";
}

/// removal: path that crosses the area (Through: present on both sides, A–B = its axis across the area)
/// or joins only on one side (A = mouth at the edge, B = point on the line)
public sealed record CrossRoad(Point A, Point B, double HalfWidth, bool Through, double Dist, byte Kind);

/// building that lies closer to the road than the slope (Edge = distance to the roadway edge, negative = on the road)
public sealed record Obstacle(PlacedPrefab Prefab, double Edge, double Dist);

public sealed class RoadResult
{
    public RoadSettings Settings;
    public List<PathPoint> Path;
    public double FlatStartUsed, FlatEndUsed;   // actually horizontal meters at the start/end (automatic or input)
    public double[] Profile;      // height of the road per waypoint (m)
    public double[] Terrain;      // terrain under the center line (m)
    public double[] SlopeWidth;   // slope width per waypoint (m)
    /// roadway width per waypoint (m) with variable width (taper); null = Settings.Width everywhere, otherwise Settings.Width is the largest width
    public double[] Widths;
    public double WidthAt(int k) => Widths?[k] ?? Settings.Width;
    public int[] ControlIndex;    // waypoint per control point (for height and display of the individual points)
    /// banking per waypoint: height change per meter sideways in direction Normal() (0 = horizontal across)
    public double[] CrossSlope;
    /// intersections (center, radius): pixels of existing roads in them keep their height – the new road writes nothing there
    public List<(Point Center, double Radius)> ProtectZones = new();
    public double Length, MaxGrade, MaxCut, CutAt, MaxFill, FillAt;
    public double KinkLength;   // m with a kink in the longitudinal profile (> 4 % gradient change over 16 m)
    public double FillLength, LongestFill;  // m with more than 1.5 m embankment (total / longest stretch)
    /// minimum gradient needed between fixed points/ends; larger than Settings.MaxGrade = stretch too steep (warning)
    public double NeededGrade;
    public int WaterArea;         // m² water in the roadway area
    /// connection to a city tile at the start/end: the road starts/ends at the height of the tile (otherwise null)
    public PlacedPrefab StartTile, EndTile;
    /// start/end at the entrance/exit of a building: the road starts/ends at its ground height (otherwise null)
    public PlacedPrefab StartPoi, EndPoi;
    /// city tiles in the area of the road: stay unchanged (their roads and their ground belong to the tile)
    public List<PlacedPrefab> Tiles = new();
    /// removal: terrain height at the two edges of the area (left/right in drawing direction), smoothed
    public double[] SideLeft, SideRight;
    /// edit road: the map draws blue, outside of CoreFrom..CoreTo (run-out) purple; HeldPoints = held intersections
    public bool EditLook;
    public int CoreFrom = -1, CoreTo = -1;
    public List<Point> HeldPoints = new();
    public int RemoveAsphalt, RemoveGravel;   // m² surface in the area (without tiles)
    public List<CrossRoad> CrossRoads = new();
    /// removal: distance of the point to the axis of the nearest preserved path minus its half width (≤ 0 = on the path)
    public double KeptRoadDistance(double x, double z) => KeptRoadDistance(x, z, out _);

    /// as above, kind = surface of the nearest preserved path
    public double KeptRoadDistance(double x, double z, out byte kind)
    {
        double best = double.MaxValue;
        kind = 0;
        if (!Settings.KeepCrossings) return best;
        foreach (var c in CrossRoads)
        {
            if (!c.Through) continue;
            double dx = c.B.X - c.A.X, dz = c.B.Y - c.A.Y, l2 = dx * dx + dz * dz;
            double t = l2 == 0 ? 0 : Math.Clamp(((x - c.A.X) * dx + (z - c.A.Y) * dz) / l2, 0, 1);
            double ex = c.A.X + dx * t - x, ez = c.A.Y + dz * t - z;
            double d = Math.Sqrt(ex * ex + ez * ez) - c.HalfWidth;
            if (d < best) { best = d; kind = c.Kind; }
        }
        return best;
    }
    public bool IsRemoval => Settings.Surface == Surface.Remove;
    public bool InTile(double x, double z) => Tiles.Any(t => t.Contains(x, z));
    public List<Crossing> Crossings = new();
    public List<Obstacle> Obstacles = new();

    /// waypoint for the distance from the start
    public int IndexAt(double dist)
    {
        int lo = 0, hi = Path.Count - 1;
        while (lo < hi) { int m = (lo + hi) / 2; if (Path[m].Dist < dist) lo = m + 1; else hi = m; }
        return lo;
    }
}

/// calculates course, longitudinal profile, slopes and audit report of a road (like tools/road.ps1).
public static class RoadPlanner
{
    public const double MaxSlopeWidth = 40;
    public const double CrossFade = 25;   // so long the banking runs out after an intersection (m)
    const double SamplesPerMeter = 4;

    /// heights: height set by hand per control point (null = automatic); applies before tile and building connection
    /// fixedAt: fixed heights at places of the road (distance from the start, height), e.g. the level stretch at an intersection
    /// crossAt: banking at places of the road (distance, slope, half length with full slope); afterwards it runs out over
    /// CrossFade meters to 0
    public static RoadResult Compute(WorldData w, IReadOnlyList<Point> points, RoadSettings s, IReadOnlyList<double?> heights = null,
        IReadOnlyList<(double Dist, double Height)> fixedAt = null, IReadOnlyList<(double Dist, double Slope, double Half)> crossAt = null,
        IReadOnlyList<double?> widths = null)
    {
        var path = BuildPath(points);
        if (path.Count < 2) return null;
        var r = new RoadResult { Settings = s.Clone(), Path = path };
        int n = path.Count;
        r.Length = path[n - 1].Dist;
        r.Terrain = new double[n];
        for (int i = 0; i < n; i++) r.Terrain[i] = w.HeightAt((int)Math.Floor(path[i].X), (int)Math.Floor(path[i].Z));
        // control point k lies at the end of section k-1 (point 0 at the start)
        r.ControlIndex = new int[points.Count];
        for (int i = 0; i < n; i++)
            if (path[i].Segment + 1 < points.Count) r.ControlIndex[path[i].Segment + 1] = i;
        for (int k = 1; k < points.Count; k++) r.ControlIndex[k] = Math.Max(r.ControlIndex[k], r.ControlIndex[k - 1]);
        // taper (the user's request 2026-11-03): width per control point (null = default width), in between a soft transition
        // (smoothstep) over the whole section between two control points; before and after the width of the first/last stays
        if (!r.IsRemoval && widths != null && widths.Any(v => v != null))
        {
            var ctl = new List<(double Dist, double W)>();
            for (int k = 0; k < points.Count; k++) ctl.Add((path[r.ControlIndex[k]].Dist, (k < widths.Count ? widths[k] : null) ?? s.Width));
            r.Widths = new double[n];
            int seg = 0;
            for (int i = 0; i < n; i++)
            {
                double d = path[i].Dist;
                while (seg < ctl.Count - 2 && d > ctl[seg + 1].Dist) seg++;
                var (d0, w0) = ctl[seg];
                var (d1, w1) = ctl[Math.Min(seg + 1, ctl.Count - 1)];
                double t = d1 > d0 ? Math.Clamp((d - d0) / (d1 - d0), 0, 1) : 1;
                r.Widths[i] = w0 + (w1 - w0) * (t * t * (3 - 2 * t));
            }
            r.Settings.Width = r.Widths.Max();
        }
        double reach = r.Settings.Width / 2 + s.Shoulder + s.GrassEdge + MaxSlopeWidth + 2;
        double x0 = path.Min(p => p.X) - reach, x1 = path.Max(p => p.X) + reach, z0 = path.Min(p => p.Z) - reach, z1 = path.Max(p => p.Z) + reach;
        r.Tiles = w.Prefabs.Where(pf => pf.IsTile && pf.X <= x1 && pf.X + pf.SizeX >= x0 && pf.Z <= z1 && pf.Z + pf.SizeZ >= z0).ToList();
        if (r.IsRemoval)
        {
            Removal(w, r);
            Stats(r);
            Obstacles(w, r);
            return r;
        }
        // height offset (the user's request): the road follows the terrain shifted by the offset – but not at start
        // and end (there it connects to terrain, road, tile or building): full offset only from the second to the
        // second-to-last control point, linear in between; with only two points in the middle third
        var raw = new double[n];
        int c1 = points.Count >= 3 ? r.ControlIndex[1] : -1, c2 = points.Count >= 3 ? r.ControlIndex[points.Count - 2] : -1;
        double len = path[n - 1].Dist;
        for (int i = 0; i < n; i++)
        {
            double d = path[i].Dist, wgt;
            if (s.OffsetRamp > 0)
                wgt = Math.Min(d, len - d) / s.OffsetRamp;
            else if (c1 > 0 && c2 < n - 1)
                wgt = d < path[c1].Dist ? d / path[c1].Dist : d > path[c2].Dist ? (len - d) / (len - path[c2].Dist) : 1;
            else wgt = Math.Min(1, Math.Min(d, len - d) / (len / 3));
            double off = s.HeightOffset;
            if (s.OffsetAlong is { Length: > 0 } oa)
            {
                double f = Math.Clamp(d / 4, 0, oa.Length - 1);
                int j = Math.Min((int)f, oa.Length - 2 < 0 ? 0 : oa.Length - 2);
                off += oa.Length == 1 ? oa[0] : oa[j] + (oa[j + 1] - oa[j]) * Math.Clamp(f - j, 0, 1);
            }
            raw[i] = r.Terrain[i] + off * Math.Clamp(wgt, 0, 1);
        }
        // ends at a city tile: take over the height of the tile so that no step remains at the tile edge
        double h0 = raw[0], h1 = raw[n - 1];
        r.StartTile = TileAt(r.Tiles, path[0].X, path[0].Z);
        r.EndTile = TileAt(r.Tiles, path[n - 1].X, path[n - 1].Z);
        if (r.StartTile != null) h0 = w.TileHeight(r.StartTile, path[0].X, path[0].Z);
        if (r.EndTile != null) h1 = w.TileHeight(r.EndTile, path[n - 1].X, path[n - 1].Z);
        // piece of the road inside the start/end tile: exactly at tile height (the game writes the tile there)
        var fix = new double?[n];
        for (int i = 0; r.StartTile != null && i < n && r.StartTile.Contains(path[i].X, path[i].Z); i++) fix[i] = h0;
        for (int i = n - 1; r.EndTile != null && i >= 0 && r.EndTile.Contains(path[i].X, path[i].Z); i--) fix[i] = h1;
        // end at the entrance/exit of a building: ground height of the building in the game (not the terrain in front; at
        // survivor_site_06 it lay 2.3 m lower, the road started too low – the user's hint 2026-10-28)
        if (r.StartTile == null && (r.StartPoi = PoiAtExit(w, path[0])) != null) fix[0] = h0 = r.StartPoi.TerrainHeight;
        if (r.EndTile == null && (r.EndPoi = PoiAtExit(w, path[n - 1])) != null) fix[n - 1] = h1 = r.EndPoi.TerrainHeight;
        // heights of single points set by hand (the user's request) are fixed
        for (int k = 0; heights != null && k < heights.Count && k < points.Count; k++)
        {
            if (heights[k] is not double hk) continue;
            int i = r.ControlIndex[k];
            fix[i] = hk;
            if (i == 0) h0 = hk;
            if (i == n - 1) h1 = hk;
        }
        foreach (var (fd, fh) in fixedAt ?? Array.Empty<(double, double)>())
        {
            int i = r.IndexAt(Math.Clamp(fd, 0, r.Length));
            if (i > 0 && i < n - 1) fix[i] ??= fh;   // ends and already fixed points keep their value
        }
        // horizontal piece at the start/end (e.g. 2 m behind a tile): points at the height of the start or end fixed
        // automatic (value < 0): at city tiles and building entrances 4 m horizontal – otherwise the road already lies one block lower 2 m before
        // the tile edge (step at the concrete edge, Wilibii County 2026-11-04) because it arrives there at full gradient
        double flatA = s.FlatStart >= 0 ? s.FlatStart : (r.StartTile != null || r.StartPoi != null ? 4 : 0);
        double flatB = s.FlatEnd >= 0 ? s.FlatEnd : (r.EndTile != null || r.EndPoi != null ? 4 : 0);
        r.FlatStartUsed = flatA; r.FlatEndUsed = flatB;
        if (flatA + flatB < r.Length)
        {
            for (int i = 0; flatA > 0 && i < n && path[i].Dist <= flatA; i++) fix[i] ??= h0;
            for (int i = n - 1; flatB > 0 && i >= 0 && r.Length - path[i].Dist <= flatB; i--) fix[i] ??= h1;
        }
        // even gradient (the user's request 2026-10-28: after much deleting and redrawing the ground was
        // uneven): instead of the terrain a straight line from start to end along the stretch; fixed points (tiles, buildings,
        // intersections, by hand) divide it into sections with an even gradient each (distribution in Profile); offset stays
        if (s.UniformGrade)
            for (int i = 0; i < n; i++)
                raw[i] = h0 + (h1 - h0) * path[i].Dist / r.Length + (raw[i] - r.Terrain[i]);
        // do not smooth the straight line (smoothing would bend it at the ends)
        double[] tubeLo = null, tubeHi = null;
        if (s.TubeFill >= 0 && !s.UniformGrade)
        {
            tubeLo = new double[n]; tubeHi = new double[n];
            for (int i = 0; i < n; i++)
            {
                var (nx, nz) = Normal(path, i);
                double edge = r.WidthAt(i) / 2 + s.Shoulder;
                double a = w.HeightAt((int)Math.Floor(path[i].X + nx * edge), (int)Math.Floor(path[i].Z + nz * edge));
                double b = w.HeightAt((int)Math.Floor(path[i].X - nx * edge), (int)Math.Floor(path[i].Z - nz * edge));
                double off = raw[i] - r.Terrain[i];
                tubeLo[i] = raw[i] - s.TubeCut;
                // embankment limited relative to the terrain, but also relative to the lower roadway edge (slope: road not in the air on the valley side)
                tubeHi[i] = Math.Min(raw[i] + s.TubeFill, Math.Max(Math.Min(r.Terrain[i], Math.Min(a, b)) + off + s.TubeEdgeFill, tubeLo[i] + 0.3));
            }
        }
        r.Profile = Profile(path, raw, h0, h1, s.UniformGrade ? 0 : s.Smooth, s.MaxGrade, fix, out r.NeededGrade, tubeLo, tubeHi);
        r.CrossSlope = new double[n];
        foreach (var (cd, cs, ch) in crossAt ?? Array.Empty<(double, double, double)>())
            for (int i = 0; i < n; i++)
            {
                double off = Math.Abs(path[i].Dist - cd), wgt = off <= ch ? 1 : Math.Max(0, 1 - (off - ch) / CrossFade);
                wgt = wgt * wgt * (3 - 2 * wgt);
                if (wgt * Math.Abs(cs) > Math.Abs(r.CrossSlope[i])) r.CrossSlope[i] = cs * wgt;   // the stronger intersection applies
            }
        r.SlopeWidth = Slopes(w, path, r.Profile, s, r);
        Stats(r);
        Crossings(w, r);
        Water(w, r);
        Obstacles(w, r);
        return r;
    }

    // Catmull-Rom curve through the control points, sampled every 0.25 m
    public static List<PathPoint> BuildPath(IReadOnlyList<Point> p)
    {
        var path = new List<PathPoint>();
        double dist = 0, px = 0, pz = 0;
        for (int i = 0; i < p.Count - 1; i++)
        {
            Point p0 = p[Math.Max(0, i - 1)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(p.Count - 1, i + 2)];
            int steps = (int)Math.Ceiling((p2 - p1).Length * SamplesPerMeter);
            if (steps == 0) continue;
            for (int k = 0; k <= steps; k++)
            {
                if (path.Count > 0 && k == 0) continue;
                double t = (double)k / steps, t2 = t * t, t3 = t2 * t;
                double x = 0.5 * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
                double z = 0.5 * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
                if (path.Count > 0) dist += Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                path.Add(new PathPoint(x, z, dist, i));
                px = x; pz = z;
            }
        }
        return path;
    }

    // longitudinal profile: terrain under the center line, moving average, ends fixed (h0/h1: existing height or height of the tile),
    // gradient limited. fix: points with fixed height (in a tile). If the gradient is too steep for both ends, the end
    // without a tile gives way (pass last from the tile) so that no step remains at the tile edge (speed bump in the game)
    static double[] Profile(List<PathPoint> path, double[] raw, double h0, double h1, double window, double maxGrade, double?[] fix)
        => Profile(path, raw, h0, h1, window, maxGrade, fix, out _);

    /// needed: highest gradient that is at least needed between fixed points (and the ends). If it is larger than
    /// maxGrade, the gradient is raised for it (at most 2.5 times) and distributed evenly, instead of digging an end meters deep into
    /// the slope (2026-10-28: gravel path with 22 % gradient was lowered by 9 m at the start → jump to the old path)
    static double[] Profile(List<PathPoint> path, double[] raw, double h0, double h1, double window, double maxGrade, double?[] fix, out double needed,
        double[] tubeLo = null, double[] tubeHi = null)
    {
        int n = path.Count;
        needed = 0;
        int lastI = 0;
        double lastH = fix[0] ?? h0;
        for (int i = 1; i < n; i++)
        {
            double? hi = i == n - 1 ? fix[i] ?? h1 : fix[i];
            if (hi is not double hv) continue;
            double span = path[i].Dist - path[lastI].Dist;
            if (span > 2) needed = Math.Max(needed, Math.Abs(hv - lastH) / span);
            lastI = i; lastH = hv;
        }
        if (needed > maxGrade) maxGrade = Math.Min(needed * 1.05, maxGrade * 2.5);
        var prof = tubeLo != null ? TubeProfile(path, tubeLo, tubeHi, fix, h0, h1, window) : MovingAverage(path, raw, window);
        // distribute the correction between the fixed points (ends, tile pieces, heights set by hand) linearly,
        // so that the road rises softly to a set point instead of only shortly before it with the highest gradient
        var anchors = new List<(int i, double d)> { (0, h0 - prof[0]) };
        for (int i = 1; i < n - 1; i++) if (fix[i] is double f) anchors.Add((i, f - prof[i]));
        anchors.Add((n - 1, h1 - prof[n - 1]));
        for (int k = 0; k + 1 < anchors.Count; k++)
        {
            var (ia, da) = anchors[k];
            var (ib, db) = anchors[k + 1];
            double span = path[ib].Dist - path[ia].Dist;
            for (int i = ia; i < ib || (i == ib && k + 2 == anchors.Count); i++)   // each point only once
            {
                double t = span > 0 ? (path[i].Dist - path[ia].Dist) / span : 0;
                prof[i] += da * (1 - t) + db * t;
            }
        }
        // ends without a fixed value are "soft": the correction above puts them at h0/h1, but if the gradient to a fixed
        // point (tile, building, height set by hand) demands it, they give way – otherwise a steep wall would arise in front of the fixed point
        // (test 2026-10-28: point 22 m above the terrain gave 137 m per 10 m)
        void Forward()
        {
            if (fix[0] is double f0) prof[0] = f0;
            for (int i = 1; i < n; i++) { double lim = maxGrade * (path[i].Dist - path[i - 1].Dist); prof[i] = fix[i] ?? Math.Clamp(prof[i], prof[i - 1] - lim, prof[i - 1] + lim); }
        }
        void Backward()
        {
            if (fix[n - 1] is double f1) prof[n - 1] = f1;
            for (int i = n - 2; i >= 0; i--) { double lim = maxGrade * (path[i + 1].Dist - path[i].Dist); prof[i] = fix[i] ?? Math.Clamp(prof[i], prof[i + 1] - lim, prof[i + 1] + lim); }
        }
        bool startWins = fix[0] != null && fix[n - 1] == null;
        for (int pass = 0; pass < 6; pass++)
            if (startWins) { Backward(); Forward(); }
            else { Forward(); Backward(); }
        return prof;
    }

    /// tautest line (taut string) inside the tube lo..hi: straight where the terrain allows, otherwise along the edge of the tube
    /// ; no embankments over hollows, no overshoot. Fixed points and ends have lo = hi. Afterwards slight rounding of the
    /// kinks (window 30 % of window, at most 30 m) and resetting into the tube.
    static double[] TubeProfile(List<PathPoint> path, double[] lo, double[] hi, double?[] fix, double h0, double h1, double window)
    {
        int n = path.Count;
        var L = (double[])lo.Clone(); var H = (double[])hi.Clone();
        for (int i = 0; i < n; i++) if (fix[i] is double f) L[i] = H[i] = f;
        L[0] = H[0] = fix[0] ?? h0;
        L[n - 1] = H[n - 1] = fix[n - 1] ?? h1;
        // support points: every 4th waypoint (1 m) and all fixed points
        var ids = new List<int>();
        for (int i = 0; i < n; i++) if (i % 4 == 0 || i == n - 1 || L[i] == H[i]) ids.Add(i);
        int m = ids.Count;
        var X = new double[m]; var lo2 = new double[m]; var hi2 = new double[m];
        for (int k = 0; k < m; k++) { X[k] = path[ids[k]].Dist; lo2[k] = L[ids[k]]; hi2[k] = H[ids[k]]; }
        var y = new double[m];
        int a = 0; double ay = lo2[0]; y[0] = ay;
        while (a < m - 1)
        {
            double sLo = double.NegativeInfinity, sHi = double.PositiveInfinity;
            int iLo = a, iHi = a, next = -1; double ny = 0;
            for (int j = a + 1; j < m; j++)
            {
                double dx = X[j] - X[a];
                if (dx < 1e-9) continue;
                double sl = (lo2[j] - ay) / dx, sh = (hi2[j] - ay) / dx;
                if (sl > sLo) { sLo = sl; iLo = j; }
                if (sh < sHi) { sHi = sh; iHi = j; }
                if (sLo > sHi + 1e-12)
                {
                    if (iLo == j) { next = iHi; ny = hi2[iHi]; }   // line would have to rise beyond the upper point: kink at the upper point
                    else { next = iLo; ny = lo2[iLo]; }            // line would have to fall below the lower point: kink at the lower point
                    break;
                }
            }
            if (next < 0) { next = m - 1; ny = lo2[m - 1]; }
            if (next <= a) { next = a + 1; ny = Math.Clamp(ay, lo2[next], hi2[next]); }   // protection against standstill with double distances
            for (int k = a + 1; k <= next; k++) y[k] = ay + (ny - ay) * (X[k] - X[a]) / Math.Max(1e-9, X[next] - X[a]);
            a = next; ay = ny;
        }
        var prof = new double[n];
        for (int k = 0; k + 1 < m; k++)
            for (int i = ids[k]; i <= ids[k + 1]; i++)
            {
                double t = ids[k + 1] > ids[k] ? (double)(i - ids[k]) / (ids[k + 1] - ids[k]) : 0;
                prof[i] = y[k] + (y[k + 1] - y[k]) * t;
            }
        // round off kinks of the taut string (the user's hint 2026-11-11: slight steps in the game): smooth several times and
        // each time back into the tube – gives soft transitions that at most touch the edges of the tube
        double round = Math.Min(60, window * 0.6);
        if (round > 2)
        {
            for (int pass = 0; pass < 8; pass++)
            {
                for (int i = 0; i < n; i++) prof[i] = Math.Clamp(prof[i], L[i], H[i]);
                prof = MovingAverage(path, prof, round);   // smooth last, do not clamp back: no new kink at the tube edge
            }
            for (int i = 0; i < n; i++) if (L[i] == H[i]) prof[i] = L[i];   // fixed points and ends stay exactly
        }
        return prof;
    }

    // slope width per waypoint: wider with a deep cut/high embankment (height difference in the middle and at both edges
    // × CutSlope, at least Blend, at most MaxSlopeWidth), then a moving maximum over ±25 m so that the edge runs calmly
    static double[] Slopes(WorldData w, List<PathPoint> path, double[] prof, RoadSettings s, RoadResult r)
    {
        int n = path.Count;
        var raw = new double[n];
        // width per waypoint (taper)
        for (int k = 0; k < n; k++)
        {
            var (nx, nz) = Normal(path, k);
            double edge = r.WidthAt(k) / 2 + s.Shoulder + s.GrassEdge;
            double dh = 0;
            for (int side = -1; side <= 1; side++)
            {
                double x = path[k].X + nx * edge * side, z = path[k].Z + nz * edge * side;
                dh = Math.Max(dh, Math.Abs(prof[k] - w.HeightAt((int)Math.Floor(x), (int)Math.Floor(z))));
            }
            raw[k] = s.CutSlope > 0 ? Math.Max(s.Blend, Math.Min(MaxSlopeWidth, dh * s.CutSlope)) : s.Blend;
        }
        var res = new double[n];
        // moving maximum with window ±25 m (monotone queue)
        var q = new LinkedList<int>();
        int right = 0;
        for (int k = 0; k < n; k++)
        {
            while (right < n && path[right].Dist <= path[k].Dist + 25)
            {
                while (q.Count > 0 && raw[q.Last.Value] <= raw[right]) q.RemoveLast();
                q.AddLast(right++);
            }
            while (path[q.First.Value].Dist < path[k].Dist - 25) q.RemoveFirst();
            res[k] = raw[q.First.Value];
        }
        return res;
    }

    /// city tile in which the point lies or whose edge is at most 3 m away
    /// building (no tile) whose entrance/exit is at most 2 m from the point (snapped start/end)
    static PlacedPrefab PoiAtExit(WorldData w, PathPoint p)
    {
        // also the outer edges of the driveways (belong to the building)
        bool Near(PlacedPrefab pf) => !pf.IsTile && pf.Def != null
            && p.X >= pf.X - 30 && p.X <= pf.X + pf.SizeX + 30 && p.Z >= pf.Z - 30 && p.Z <= pf.Z + pf.SizeZ + 30;
        foreach (var (ex, ez, pf) in PrefabParts.AllExits(w, Near))
            if ((ex - p.X) * (ex - p.X) + (ez - p.Z) * (ez - p.Z) <= 4) return pf;
        return null;
    }

    static PlacedPrefab TileAt(List<PlacedPrefab> tiles, double x, double z)
    {
        foreach (var pf in tiles)
        {
            double dx = Math.Max(Math.Max(pf.X - x, 0), x - (pf.X + pf.SizeX));
            double dz = Math.Max(Math.Max(pf.Z - z, 0), z - (pf.Z + pf.SizeZ));
            if (dx * dx + dz * dz <= 9) return pf;
        }
        return null;
    }

    // removal: sample the terrain at both edges of the area (width/2 + slope) and smooth along the line;
    // the new height in the middle is the mean of both edges (without "level terrain" the terrain stays unchanged)
    static void Removal(WorldData w, RoadResult r)
    {
        var s = r.Settings;
        var path = r.Path;
        int n = path.Count;
        double reach = s.Width / 2 + s.Blend;
        var left = new double[n];
        var right = new double[n];
        for (int k = 0; k < n; k++)
        {
            var (nx, nz) = Normal(path, k);
            left[k] = w.HeightAt((int)Math.Floor(path[k].X + nx * reach), (int)Math.Floor(path[k].Z + nz * reach));
            right[k] = w.HeightAt((int)Math.Floor(path[k].X - nx * reach), (int)Math.Floor(path[k].Z - nz * reach));
        }
        r.SideLeft = MovingAverage(path, left, s.Smooth);
        r.SideRight = MovingAverage(path, right, s.Smooth);
        r.Profile = new double[n];
        for (int k = 0; k < n; k++) r.Profile[k] = s.Reshape ? (r.SideLeft[k] + r.SideRight[k]) / 2 : r.Terrain[k];
        r.SlopeWidth = Enumerable.Repeat(s.Blend, n).ToArray();

        // existing surface in the area, counted once per meter (tiles excepted)
        double half = s.Width / 2;
        var seen = new HashSet<long>();
        for (int k = 0; k < n; k++)
        {
            var (nx, nz) = Normal(path, k);
            for (double o = -half; o <= half; o += 0.5)
            {
                int x = (int)Math.Floor(path[k].X + nx * o), z = (int)Math.Floor(path[k].Z + nz * o);
                if (!w.Contains(x, z) || !seen.Add(((long)x << 32) ^ (uint)z) || r.InTile(x + 0.5, z + 0.5)) continue;
                byte kind = w.RoadAt(x, z);
                if (kind == WorldData.Asphalt) r.RemoveAsphalt++;
                else if (kind == WorldData.Gravel) r.RemoveGravel++;
            }
        }
        CrossRoads(w, r);
    }

    // paths that cross the area: search for surface just outside (2 m beside the edge) on both sides.
    // connected pieces = one path; left and right at the same height of the line = continuous crossing,
    // only on one side = junction. The first/last 12 m or one road width (continuation of the road itself) and very long pieces
    // (road runs alongside) do not count.
    static void CrossRoads(WorldData w, RoadResult r)
    {
        var path = r.Path;
        int n = path.Count;
        double probe = r.Settings.Width / 2 + 2, endGap = Math.Max(12, r.Settings.Width);
        var runs = new List<(int side, int a, int b)>();
        foreach (int side in new[] { 1, -1 })
        {
            int start = -1;
            for (int k = 0; k <= n; k += 4)
            {
                bool on = false;
                if (k < n && path[k].Dist > endGap && r.Length - path[k].Dist > endGap)
                {
                    var (nx, nz) = Normal(path, k);
                    double x = path[k].X + nx * probe * side, z = path[k].Z + nz * probe * side;
                    on = w.RoadAt((int)Math.Floor(x), (int)Math.Floor(z)) != 0 && !r.InTile(x, z);
                }
                if (on && start < 0) start = k;
                if (!on && start >= 0) { runs.Add((side, start, Math.Min(k - 4, n - 1))); start = -1; }
            }
        }
        runs.RemoveAll(u => path[u.b].Dist - path[u.a].Dist > 60);

        Point Mid((int side, int a, int b) u)
        {
            int m = (u.a + u.b) / 2;
            var (nx, nz) = Normal(path, m);
            return new Point(path[m].X + nx * probe * u.side, path[m].Z + nz * probe * u.side);
        }
        double Len((int side, int a, int b) u) => path[u.b].Dist - path[u.a].Dist + 1;
        var used = new HashSet<int>();
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].side != 1) continue;
            int mi = (runs[i].a + runs[i].b) / 2, best = -1;
            double bd = 30;
            for (int j = 0; j < runs.Count; j++)
            {
                if (runs[j].side != -1 || used.Contains(j)) continue;
                double d = Math.Abs(path[(runs[j].a + runs[j].b) / 2].Dist - path[mi].Dist);
                if (d < bd) { bd = d; best = j; }
            }
            if (best < 0) continue;
            used.Add(i); used.Add(best);
            Point a = Mid(runs[i]), b = Mid(runs[best]);
            // width of the crossing path: length of the piece along the line × sin(crossing angle)
            var (tx, tz) = Normal(path, mi);
            double ax = b.X - a.X, az = b.Y - a.Y, al = Math.Sqrt(ax * ax + az * az);
            double sin = al == 0 ? 1 : Math.Abs(ax * tx + az * tz) / al;   // normal ⟂ line: share across = sin of the angle to the line
            double hw = Math.Clamp((Len(runs[i]) + Len(runs[best])) / 2 * sin / 2 + 1, 2.5, 12);
            byte kind = Math.Max(w.RoadAt((int)Math.Floor(a.X), (int)Math.Floor(a.Y)), w.RoadAt((int)Math.Floor(b.X), (int)Math.Floor(b.Y)));
            r.CrossRoads.Add(new CrossRoad(a, b, hw, true, path[mi].Dist, kind == 0 ? WorldData.Gravel : kind));
        }
        for (int i = 0; i < runs.Count; i++)
        {
            if (used.Contains(i)) continue;
            int m = (runs[i].a + runs[i].b) / 2;
            r.CrossRoads.Add(new CrossRoad(Mid(runs[i]), new Point(path[m].X, path[m].Z), Len(runs[i]) / 2, false, path[m].Dist, 0));
        }
        r.CrossRoads.Sort((p, q) => p.Dist.CompareTo(q.Dist));
    }

    internal static double[] MovingAverage(List<PathPoint> path, double[] v, double window)
    {
        int n = path.Count, lo = 0, hi = 0;
        var res = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            while (hi < n && path[hi].Dist <= path[i].Dist + window / 2) { sum += v[hi]; hi++; }
            while (path[lo].Dist < path[i].Dist - window / 2) { sum -= v[lo]; lo++; }
            res[i] = sum / (hi - lo);
        }
        return res;
    }

    /// spawn point at the start of the road: about 30 m behind the connection on the roadway, y = road height rounded up,
    /// view along the road (0 = north, 90 = east)
    public static SpawnPoint SpawnAtStart(RoadResult r)
    {
        int k = r.IndexAt(Math.Min(30, r.Length / 3));
        var a = r.Path[Math.Max(0, k - 8)];
        var b = r.Path[Math.Min(r.Path.Count - 1, k + 8)];
        double heading = (Math.Atan2(b.X - a.X, b.Z - a.Z) * 180 / Math.PI + 360) % 360;
        return new SpawnPoint { X = Math.Round(r.Path[k].X), Y = Math.Ceiling(r.Profile[k]), Z = Math.Round(r.Path[k].Z), Heading = Math.Round(heading) };
    }

    /// unit vector across the road (to the left) at waypoint k
    public static (double nx, double nz) Normal(List<PathPoint> path, int k)
    {
        var a = path[Math.Max(0, k - 2)]; var b = path[Math.Min(path.Count - 1, k + 2)];
        double dx = b.X - a.X, dz = b.Z - a.Z, l = Math.Sqrt(dx * dx + dz * dz);
        if (l == 0) return (0, 0);
        return (-dz / l, dx / l);
    }

    static void Stats(RoadResult r)
    {
        var path = r.Path;
        int n = path.Count;
        for (int i = 4; i < n; i += 4)
        {
            double g = Math.Abs(r.Profile[i] - r.Profile[i - 4]) / (path[i].Dist - path[i - 4].Dist);
            if (g > r.MaxGrade) r.MaxGrade = g;
        }
        for (int i = 0; i < n; i++)
        {
            double d = r.Profile[i] - r.Terrain[i];
            if (d > r.MaxFill) { r.MaxFill = d; r.FillAt = path[i].Dist; }
            if (-d > r.MaxCut) { r.MaxCut = -d; r.CutAt = path[i].Dist; }
        }
        // kinks: gradient change of more than 4 % within 16 m (visible step/edge in the roadway)
        for (int i = 32; i + 32 < n; i += 4)
        {
            double s1 = (r.Profile[i] - r.Profile[i - 32]) / Math.Max(1, path[i].Dist - path[i - 32].Dist);
            double s2 = (r.Profile[i + 32] - r.Profile[i]) / Math.Max(1, path[i + 32].Dist - path[i].Dist);
            if (Math.Abs(s2 - s1) > 0.04) r.KinkLength += path[i + 4].Dist - path[i].Dist;
        }
        // embankment stretches: length over which the road lies more than 1.5 m above the terrain, and the longest connected one
        double run = 0;
        for (int i = 1; i < n; i++)
        {
            double step = path[i].Dist - path[i - 1].Dist;
            if (r.Profile[i] - r.Terrain[i] > 1.5) { run += step; r.FillLength += step; if (run > r.LongestFill) r.LongestFill = run; }
            else run = 0;
        }
    }

    // intersections: connected pieces of the center line on existing roads
    static void Crossings(WorldData w, RoadResult r)
    {
        var path = r.Path;
        int n = path.Count, start = -1;
        byte kind = 0;
        for (int i = 0; i <= n; i++)
        {
            byte k = i < n ? w.RoadAt((int)Math.Floor(path[i].X), (int)Math.Floor(path[i].Z)) : (byte)0;
            if (k != 0 && start < 0) { start = i; kind = k; }
            else if (k != 0 && k > kind) kind = k;
            if (k == 0 && start >= 0)
            {
                int m = (start + i - 1) / 2;
                double len = path[i - 1].Dist - path[start].Dist;
                bool atEnd = path[start].Dist < 15 || r.Length - path[i - 1].Dist < 15;
                r.Crossings.Add(new Crossing(path[m].Dist, path[m].X, path[m].Z, kind, r.Profile[m], r.Terrain[m], len, atEnd));
                start = -1;
            }
        }
    }

    // water in the roadway area: sample across for every meter along the road
    static void Water(WorldData w, RoadResult r)
    {
        double half = r.Settings.Width / 2 + r.Settings.Shoulder;
        int count = 0;
        for (int k = 0; k < r.Path.Count; k += (int)SamplesPerMeter)
        {
            var (nx, nz) = Normal(r.Path, k);
            for (double o = -(r.WidthAt(k) / 2 + r.Settings.Shoulder); o <= r.WidthAt(k) / 2 + r.Settings.Shoulder; o += 1)
                if (w.WaterAt((int)Math.Floor(r.Path[k].X + nx * o), (int)Math.Floor(r.Path[k].Z + nz * o))) count++;
        }
        r.WaterArea = count;
    }

    // buildings, tiles and parts closer to the road than the slope
    static void Obstacles(WorldData w, RoadResult r)
    {
        var path = r.Path;
        double half = r.Settings.Width / 2 + r.Settings.Shoulder + r.Settings.GrassEdge, reach = half + MaxSlopeWidth;
        double x0 = path.Min(p => p.X) - reach, x1 = path.Max(p => p.X) + reach, z0 = path.Min(p => p.Z) - reach, z1 = path.Max(p => p.Z) + reach;
        foreach (var pf in w.Prefabs)
        {
            if (pf.X > x1 || pf.X + pf.SizeX < x0 || pf.Z > z1 || pf.Z + pf.SizeZ < z0) continue;
            // connected tiles stay unchanged; when removing, tiles are not touched at all
            if (pf == r.StartTile || pf == r.EndTile || r.IsRemoval && pf.IsTile) continue;
            double best = double.MaxValue;
            int bi = 0;
            for (int k = 0; k < path.Count; k += 2)
            {
                double dx = Math.Max(Math.Max(pf.X - path[k].X, 0), path[k].X - (pf.X + pf.SizeX));
                double dz = Math.Max(Math.Max(pf.Z - path[k].Z, 0), path[k].Z - (pf.Z + pf.SizeZ));
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d < best) { best = d; bi = k; }
            }
            double edge = best - (r.WidthAt(bi) / 2 + r.Settings.Shoulder + r.Settings.GrassEdge);
            if (edge < r.SlopeWidth[bi]) r.Obstacles.Add(new Obstacle(pf, edge, path[bi].Dist));
        }
        r.Obstacles.Sort((a, b) => a.Edge.CompareTo(b.Edge));
    }
}
