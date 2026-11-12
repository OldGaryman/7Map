using System.Windows;

namespace MapEditor.Map;

/// Mountain exists only for the mountain brush (not in the selection list of the terrain brush)
public enum TerrainKind { Smooth, Raise, Lower, Flatten, Dents, Erode, Mountain, Range }

/// roads when shaping (step 2): fixed = stay unchanged, light = follow the terrain only 30 %, with = follow completely
public enum TerrainRoads { Fixed, Light, Include }

/// result of a stroke: changed height points, largest change (m) and cells under the brush that stayed protected
/// RoadScale &lt; 1: the effect near the road was reduced so that the gradient keeps the limit
public sealed record TerrainStats(int Points, double MaxChange, int Protected, double RoadGrade = 0, double RoadScale = 1, int MovedBuildings = 0, int StayedInTile = 0, int StayedShared = 0, int StayedEdge = 0, int Limited = 0, int TilesUnder = 0);

/// single buildings when shaping (step 3): fixed = stay and the brush runs out in front of them, light = follow the terrain 30 %
/// (whole meters), with = follow completely. When taking along, y in prefabs.xml moves by whole meters, the footprint by exactly that.
public enum TerrainPois { Fixed, Light, Include }

/// terrain brush (step 1, the user's request 2026-11-02): shape terrain on free area without disturbing the surroundings.
/// Every kind acts with a soft edge (weight 1 inside, 0 at the edge), so there are no steps. Roads, buildings and
/// city tiles stay unchanged; the brush runs out in front of them over 6 m. The weight is the maximum over the whole stroke:
/// dragging twice over the same place does not act twice (the result depends only on the stroke, not on the speed).
public static class TerrainBrush
{
    /// largest grid area of a stroke (9 million cells = 3,000 × 3,000 m)
    const long MaxCells = 9_000_000;
    /// run-out width in front of roads, buildings and tiles (m)
    const double ObstacleFade = 6;

    /// start values of the strength per kind: percent (smooth, level, weather) or meters (raise, lower, dents)
    public static readonly double[] DefaultStrength = { 50, 1, 1, 60, 0.5, 50 };

    public static bool IsPercent(TerrainKind k) => k is TerrainKind.Smooth or TerrainKind.Flatten or TerrainKind.Erode;
    public static double MaxStrength(TerrainKind k) => IsPercent(k) ? 100 : k == TerrainKind.Dents ? 5 : 20;
    public static double MinStrength(TerrainKind k) => IsPercent(k) ? 1 : 0.1;

    public static TerrainStats Apply(WorldData w, WorldEdits e, IReadOnlyList<Point> stroke, double radius, BrushShape shape,
        TerrainKind kind, double strength, double softness, TerrainRoads roads = TerrainRoads.Fixed,
        double maxRoadGrade = 0, bool roadTilt = false, TerrainPois pois = TerrainPois.Fixed,
        double? minHeight = null, double? maxHeight = null, int seed = 0, MountainSpec mountain = null, RangeSpec range = null)
    {
        if (stroke.Count == 0) return new TerrainStats(0, 0, 0);
        softness = Math.Clamp(softness, 0, 1);
        // mountain range: the stroke is the outline of the area (polygon), shape and edge come from the range itself
        if (range != null)
        {
            kind = TerrainKind.Range;
            stroke = range.Outline;
            radius = 0;
            shape = BrushShape.Round;
            softness = 0;
            strength = Math.Abs(range.Height);
            mountain = null;
        }
        // mountain: stroke = one point (the center), circle with the radius of the mountain, edge and shape come from the mountain itself
        else if (mountain != null)
        {
            kind = TerrainKind.Mountain;
            stroke = new[] { stroke[^1] };
            radius = mountain.Radius + 1;
            shape = BrushShape.Round;
            softness = 0;
            strength = Math.Abs(mountain.Height);
        }

        // run-out in front of buildings and tiles: at least 6 m. If roads may change and a gradient limit applies, it becomes so long
        // that the transition is at most as steep as allowed (otherwise the limit would have to take the whole effect away on the roads)
        double obstacleFade = ObstacleFade;
        if (roads != TerrainRoads.Fixed && maxRoadGrade > 0)
        {
            double estimate = kind is TerrainKind.Raise or TerrainKind.Lower ? strength * 1.2 : kind == TerrainKind.Dents ? strength : kind is TerrainKind.Mountain or TerrainKind.Range ? strength * 0.5 : 2;
            obstacleFade = Math.Clamp(estimate * 1.5 / maxRoadGrade, ObstacleFade, 40);
        }

        // thin out the stroke (dense mouse points do not change the result but cost computing time)
        var pts = new List<Point> { stroke[0] };
        double minStep = Math.Max(0.5, radius * 0.1);
        for (int k = 1; k < stroke.Count; k++)
            if ((stroke[k] - pts[^1]).Length >= minStep) pts.Add(stroke[k]);
        if (pts[^1] != stroke[^1]) pts.Add(stroke[^1]);

        int margin = (int)Math.Ceiling(radius) + Math.Max(24, (int)obstacleFade + 4) + (range != null ? (int)Math.Ceiling(range.Spill) : 0);   // room for blurring, run-out in front of obstacles and adjusting the road
        int gx0 = Math.Max(-w.Half, (int)Math.Floor(pts.Min(p => p.X)) - margin);
        int gz0 = Math.Max(-w.Half, (int)Math.Floor(pts.Min(p => p.Y)) - margin);
        int gx1 = Math.Min(w.Half, (int)Math.Ceiling(pts.Max(p => p.X)) + margin);
        int gz1 = Math.Min(w.Half, (int)Math.Ceiling(pts.Max(p => p.Y)) + margin);
        int gw = gx1 - gx0, gh = gz1 - gz0;
        if (gw < 5 || gh < 5) return new TerrainStats(0, 0, 0);
        if ((long)gw * gh > MaxCells) throw new InvalidOperationException(Loc.T("The stroke covers too large an area – please paint in smaller strokes."));
        int n = gw * gh;

        // weight of the brush: distance to the stroke → 1 inside, softly running out to 0 at the edge
        var brush = new float[n];
        int r = (int)Math.Ceiling(radius) + 1;
        bool[] polyMask = null;
        if (range != null)
        {
            FillPolygon(brush, pts, gx0, gz0, gw, gh);
            if (range.Spill > 0)
            {
                // gentle foothills may run out beyond the line: the area is extended by the run-out width
                polyMask = new bool[n];
                for (int g = 0; g < n; g++) polyMask[g] = brush[g] > 0.5f;
                var dOut = GroundShaper.Distance(polyMask, gw, gh);
                for (int g = 0; g < n; g++) if (!polyMask[g] && dOut[g] <= range.Spill) brush[g] = 1;
            }
        }
        else if (shape is not (BrushShape.Round or BrushShape.Square))
        {
            // other shapes: the shape is stamped along the stroke at a distance of at most 1/8 radius, per cell the
            // smallest norm counts (shape lies completely within the radius around the center)
            var spec = ShapeSpec.Create(shape, seed);
            double step = Math.Max(0.5, radius / 8);
            var centers = new List<Point> { pts[0] };
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                double len = (pts[k + 1] - pts[k]).Length;
                int parts = Math.Max(1, (int)Math.Ceiling(len / step));
                for (int q = 1; q <= parts; q++) centers.Add(pts[k] + (pts[k + 1] - pts[k]) * q / parts);
            }
            foreach (var c in centers)
            {
                int x0 = Math.Max(0, (int)Math.Floor(c.X - radius) - gx0), x1 = Math.Min(gw - 1, (int)Math.Ceiling(c.X + radius) - gx0);
                int z0 = Math.Max(0, (int)Math.Floor(c.Y - radius) - gz0), z1 = Math.Min(gh - 1, (int)Math.Ceiling(c.Y + radius) - gz0);
                for (int gz = z0; gz <= z1; gz++)
                    for (int gx = x0; gx <= x1; gx++)
                    {
                        double u = spec.Norm((gx0 + gx + 0.5 - c.X) / radius, (gz0 + gz + 0.5 - c.Y) / radius);
                        if (u >= 1) continue;
                        float wv = Falloff(u, softness);
                        int g = gz * gw + gx;
                        if (wv > brush[g]) brush[g] = wv;
                    }
            }
        }
        else
        for (int k = 0; k < pts.Count; k++)
        {
            var a = pts[k];
            var b = k + 1 < pts.Count ? pts[k + 1] : a;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X)) - r - gx0), x1 = Math.Min(gw - 1, (int)Math.Ceiling(Math.Max(a.X, b.X)) + r - gx0);
            int z0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, b.Y)) - r - gz0), z1 = Math.Min(gh - 1, (int)Math.Ceiling(Math.Max(a.Y, b.Y)) + r - gz0);
            double dx = b.X - a.X, dz = b.Y - a.Y, len2 = dx * dx + dz * dz;
            for (int gz = z0; gz <= z1; gz++)
                for (int gx = x0; gx <= x1; gx++)
                {
                    double px = gx0 + gx + 0.5, pz = gz0 + gz + 0.5;
                    double d = shape == BrushShape.Square ? ChebyshevToSegment(px - a.X, pz - a.Y, dx, dz) : Math.Sqrt(DistSq(px - a.X, pz - a.Y, dx, dz, len2));
                    double u = d / radius;
                    if (u >= 1) continue;
                    float wv = Falloff(u, softness);
                    int g = gz * gw + gx;
                    if (wv > brush[g]) brush[g] = wv;
                }
        }

        // obstacles: buildings/parts/tiles (plus 1 m) always stay fixed, in front of them the brush runs out over ObstacleFade meters.
        // roads: fixed (factor 0, run-out 6 m), light (30 %, run-out 10 m so that no edge appears beside the road) or with (100 %).
        var road = new bool[n];
        for (int g = 0; g < n; g++)
            if (w.RoadMask[w.Index(gx0 + g % gw, gz0 + g / gw)] != 0) road[g] = true;
        // 1 m left and right of the road also count as road (gravel edge, with gravel paths the edge strip)
        var d1 = GroundShaper.Distance(road, gw, gh);
        for (int g = 0; g < n; g++) if (d1[g] <= 1.5f) road[g] = true;
        // single buildings (POIs) may move with the terrain: footprint including driveways is then one unit
        var skipped = new List<(PlacedPrefab P, int Reason)>();   // 0 in city tile, 1 shares driveways, 2 at the grid edge
        var units = pois == TerrainPois.Fixed ? new List<PoiUnit>() : FindUnits(w, gx0, gz0, gx1, gz1, skipped);
        var unitMembers = new HashSet<PlacedPrefab>(units.SelectMany(u => u.Members));
        var obs = new bool[n];
        foreach (var p in w.Prefabs)
        {
            if (unitMembers.Contains(p)) continue;
            int x0 = Math.Max(gx0, p.X - 1), x1 = Math.Min(gx1, p.X + p.SizeX + 1);
            int z0 = Math.Max(gz0, p.Z - 1), z1 = Math.Min(gz1, p.Z + p.SizeZ + 1);
            for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++) obs[(z - gz0) * gw + x - gx0] = true;
        }
        var dObs = GroundShaper.Distance(obs, gw, gh);
        var dRoad = GroundShaper.Distance(road, gw, gh);
        double roadFactor = roads == TerrainRoads.Fixed ? 0 : roads == TerrainRoads.Light ? 0.3 : 1;
        double roadFade = roads == TerrainRoads.Light ? 10 : obstacleFade;
        // city tiles under the brush: always stay unchanged in height (counted only for the hint)
        int tilesUnder = 0;
        foreach (var t in w.Prefabs.Where(t => t.IsTile))
        {
            bool under = false;
            for (int z = Math.Max(t.Z, gz0); z < Math.Min(t.Z + t.SizeZ, gz1) && !under; z += 3)
                for (int x = Math.Max(t.X, gx0); x < Math.Min(t.X + t.SizeX, gx1); x += 3)
                    if (brush[(z - gz0) * gw + x - gx0] > 0.5f) { under = true; break; }
            if (under) tilesUnder++;
        }
        float CenterWeight(PlacedPrefab p)
        {
            int x = p.X + p.SizeX / 2 - gx0, z = p.Z + p.SizeZ / 2 - gz0;
            return x >= 0 && x < gw && z >= 0 && z < gh ? brush[z * gw + x] : 0;
        }
        var wt = new float[n];
        int active = 0, blocked = 0;
        for (int g = 0; g < n; g++)
        {
            if (brush[g] <= 0) continue;
            double t = Math.Clamp(dObs[g] / obstacleFade, 0, 1);
            double f = t * t * (3 - 2 * t);
            if (roads != TerrainRoads.Include)
            {
                double u = Math.Clamp(dRoad[g] / roadFade, 0, 1);
                f *= roadFactor + (1 - roadFactor) * (u * u * (3 - 2 * u));
            }
            wt[g] = brush[g] * (float)f;
            if (wt[g] > 0.001f) active++;
            if (brush[g] > 0.5f && (obs[g] || (roads == TerrainRoads.Fixed && road[g]))) blocked++;
        }
        if (active == 0) return new TerrainStats(0, 0, blocked, TilesUnder: tilesUnder);

        // fade out unevenness (noise) near roads: road and its edge stay smooth
        var calm = new float[n];
        for (int g = 0; g < n; g++) { double u = Math.Clamp(dRoad[g] / 8, 0, 1); calm[g] = (float)(u * u * (3 - 2 * u)); }

        var h = new float[n];
        for (int g = 0; g < n; g++) h[g] = w.Heights[w.Index(gx0 + g % gw, gz0 + g / gw)] / 256f;
        var h0 = (float[])h.Clone();

        switch (kind)
        {
            case TerrainKind.Smooth:
            {
                // three times mean over a strip ≈ Gauss; width grows with the brush (2–12 m)
                var blur = (float[])h.Clone();
                int br = (int)Math.Clamp(radius * 0.25, 2, 12);
                for (int pass = 0; pass < 3; pass++) BoxBlur(blur, gw, gh, br);
                float mix = (float)(strength / 100);
                for (int g = 0; g < n; g++) if (wt[g] > 0) h[g] += (blur[g] - h[g]) * wt[g] * mix;
                break;
            }
            case TerrainKind.Raise:
            case TerrainKind.Lower:
            {
                double sign = kind == TerrainKind.Raise ? 1 : -1;
                for (int g = 0; g < n; g++)
                    if (wt[g] > 0)
                        h[g] += (float)(sign * strength * wt[g] * (1 + 0.2 * calm[g] * RoadApplier.Noise(gx0 + g % gw, gz0 + g / gw)));
                break;
            }
            case TerrainKind.Flatten:
            {
                // target height = mean around the start of the stroke (5 × 5 m)
                int sx = (int)Math.Floor(pts[0].X) - gx0, sz = (int)Math.Floor(pts[0].Y) - gz0;
                double sum = 0;
                int m = 0;
                for (int dz = -2; dz <= 2; dz++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int x = Math.Clamp(sx + dx, 0, gw - 1), z = Math.Clamp(sz + dz, 0, gh - 1);
                        sum += h0[z * gw + x];
                        m++;
                    }
                float target = (float)(sum / m), pull = (float)(strength / 100);
                for (int g = 0; g < n; g++) if (wt[g] > 0) h[g] += (target - h[g]) * wt[g] * pull;
                break;
            }
            case TerrainKind.Dents:
                for (int g = 0; g < n; g++)
                    if (wt[g] > 0)
                    {
                        int x = gx0 + g % gw, z = gz0 + g / gw;
                        h[g] += (float)(strength * wt[g] * calm[g] * (RoadApplier.Noise(x, z) + 0.35 * RoadApplier.Octave(x, z, 2, 53)) / 1.2);
                    }
                break;
            case TerrainKind.Mountain:
            {
                double mcx = pts[0].X, mcz = pts[0].Y;
                for (int g = 0; g < n; g++)
                    if (wt[g] > 0)
                        h[g] += mountain.Delta(gx0 + g % gw + 0.5, gz0 + g / gw + 0.5, mcx, mcz) * wt[g];
                break;
            }
            case TerrainKind.Range:
            {
                // distance of every cell in the area to the edge; at the edge of the world the edge does not count as an edge (nothing falls off there)
                var inside = new bool[n];
                var outside = new bool[n];
                bool atW = gx0 <= -w.Half, atE = gx1 >= w.Half, atS = gz0 <= -w.Half, atN = gz1 >= w.Half;
                for (int g = 0; g < n; g++)
                {
                    int cx = g % gw, cz = g / gw;
                    inside[g] = polyMask != null ? polyMask[g] : brush[g] > 0.5f;
                    outside[g] = !inside[g] || (atW && cx == 0) || (atE && cx == gw - 1) || (atS && cz == 0) || (atN && cz == gh - 1);
                }
                var edgeDist = GroundShaper.Distance(outside, gw, gh);   // inside: distance to the edge
                if (polyMask != null)
                {
                    var outDist = GroundShaper.Distance(inside, gw, gh);   // outside: distance to the area, negative
                    for (int g = 0; g < n; g++) if (!inside[g]) edgeDist[g] = -outDist[g];
                }
                int blurR = (int)Math.Clamp(Math.Min(range.Foot, 200) / 12, 2, 10);   // take the trembling of the outlines out of the distance
                for (int pass = 0; pass < 2; pass++) BoxBlur(edgeDist, gw, gh, blurR);
                var rangeDelta = range.Shape(edgeDist, gw, gh, gx0, gz0);
                for (int g = 0; g < n; g++)
                    if (wt[g] > 0) h[g] += rangeDelta[g] * wt[g];
                break;
            }
            case TerrainKind.Erode:
            {
                // weather: where it is steeper than a scree slope (0.6 m per m), material slides down – edges become soft
                const float talus = 0.6f, rate = 0.2f;
                int iters = (int)Math.Ceiling(strength / 100 * 150);
                var delta = new float[n];
                int[] nb = { -1, 1, -gw, gw };
                for (int it = 0; it < iters; it++)
                {
                    Array.Clear(delta);
                    for (int gz = 1; gz < gh - 1; gz++)
                        for (int gx = 1; gx < gw - 1; gx++)
                        {
                            int g = gz * gw + gx;
                            if (wt[g] <= 0.001f) continue;
                            foreach (int o in nb)
                            {
                                int q = g + o;
                                if (wt[q] <= 0.001f) continue;
                                float diff = h[g] - h[q];
                                if (diff <= talus) continue;
                                float mv = rate * (diff - talus) * Math.Min(wt[g], wt[q]);
                                delta[g] -= mv;
                                delta[q] += mv;
                            }
                        }
                    for (int g = 0; g < n; g++) h[g] += delta[g];
                }
                break;
            }
        }

        // height limits (absolute, meters): what would come above or below is leveled softly (saturation over 1 m, no kink).
        // If a cell is already outside, the brush does not push it any further out.
        int limited = 0;
        if (minHeight != null || maxHeight != null)
            for (int g = 0; g < n; g++)
            {
                if (h[g] == h0[g]) continue;
                double v = h[g], top = maxHeight is double mx ? Math.Max(mx, h0[g]) : double.PositiveInfinity;
                double bottom = minHeight is double mn ? Math.Min(mn, h0[g]) : double.NegativeInfinity;
                const double T = 1.0;
                if (v > top - T) v = top - T + T * Math.Tanh((v - (top - T)) / T);
                if (v < bottom + T) v = bottom + T - T * Math.Tanh(((bottom + T) - v) / T);
                v = Math.Clamp(v, bottom, top);
                v = h[g] > h0[g] ? Math.Max(v, h0[g]) : Math.Min(v, h0[g]);   // saturation must never work against the direction of the brush
                if (Math.Abs(v - h[g]) > 0.01) limited++;
                h[g] = (float)v;
            }

        // keep roads smooth (only if they may change): height change d = h − h0
        double roadScale = 1;
        var moves = new List<(PoiUnit Unit, int K)>();
        if (roads != TerrainRoads.Fixed || units.Count > 0)
        {
            var d = new float[n];
            for (int g = 0; g < n; g++) d[g] = h[g] - h0[g];
            if (roads != TerrainRoads.Fixed)
            {
                var fObs = new float[n];
                for (int g = 0; g < n; g++) fObs[g] = (float)Smooth(dObs[g] / obstacleFade);
                if (roadTilt) SmoothTilt(d, road, dRoad, obs, gw, gh);
                else LevelAcrossWidth(d, road, dRoad, fObs, gw, gh);
                // limit the gradient of the road before the buildings are added (their transition is at most as steep anyway)
                if (maxRoadGrade > 0)
                {
                    for (int g = 0; g < n; g++) h[g] = h0[g] + d[g];
                    roadScale = LimitGrade(h, h0, road, obs, dRoad, gw, gh, maxRoadGrade);
                    for (int g = 0; g < n; g++) d[g] = h[g] - h0[g];
                }
            }
            // buildings: take whole meters along (y in prefabs.xml is an integer), footprint by exactly that, soft transition all around
            double poiFactor = pois == TerrainPois.Light ? 0.3 : 1;   
            foreach (var u in units)
            {
                double sum = 0;
                int cnt = 0;
                foreach (var (rx, rz, sx, sz) in u.Rects)
                    for (int z = Math.Max(rz, gz0); z < Math.Min(rz + sz, gz1); z++)
                        for (int x = Math.Max(rx, gx0); x < Math.Min(rx + sx, gx1); x++) { sum += d[(z - gz0) * gw + x - gx0]; cnt++; }
                if (cnt == 0) continue;
                double moveBy = sum / cnt * poiFactor;
                int k = Math.Abs(moveBy) < 0.5 ? 0 : (int)Math.Round(moveBy, MidpointRounding.AwayFromZero);
                // transition: at most as steep as the gradient limit of the roads (smoothing is 1.5 times as steep in the middle)
                double blend = Math.Clamp(Math.Abs(k) * (roads != TerrainRoads.Fixed && maxRoadGrade > 0 ? 1.5 / maxRoadGrade : 3.0), 6, 40);
                int ext = (int)Math.Ceiling(blend);
                int bx0 = u.Rects.Min(r => r.X) - ext, bx1 = u.Rects.Max(r => r.X + r.SX) + ext;
                int bz0 = u.Rects.Min(r => r.Z) - ext, bz1 = u.Rects.Max(r => r.Z + r.SZ) + ext;
                for (int z = Math.Max(bz0, gz0); z < Math.Min(bz1, gz1); z++)
                    for (int x = Math.Max(bx0, gx0); x < Math.Min(bx1, gx1); x++)
                    {
                        int g = (z - gz0) * gw + x - gx0;
                        double dist = u.Rects.Min(r => RectDist(r, x + 0.5, z + 0.5));
                        if (obs[g]) continue;   // tiles and fixed buildings always stay
                        if (dist <= 0) { d[g] = k; continue; }
                        if (dist >= blend) continue;
                        double f = 1 - Smooth(dist / blend);
                        d[g] += (float)((k - d[g]) * f);
                    }
                if (k != 0) moves.Add((u, k));
            }
            for (int g = 0; g < n; g++) h[g] = h0[g] + d[g];
        }
        foreach (var (u, k) in moves)
            foreach (var m in u.Members) e.MovePrefab(m, m.X, m.Y + k, m.Z, m.Rotation);

        int changed = 0;
        double maxChange = 0;
        for (int g = 0; g < n; g++)
        {
            if (h[g] == h0[g]) continue;
            int i = w.Index(gx0 + g % gw, gz0 + g / gw);
            var hv = (ushort)Math.Clamp(Math.Round(h[g] * 256.0), 0, 65535);
            if (hv == w.Heights[i]) continue;
            maxChange = Math.Max(maxChange, Math.Abs(hv - w.Heights[i]) / 256.0);
            e.SetHeight(i, hv);
            changed++;
        }
        // steepest gradient on road cells that changed (m per m, to the neighbors to the east and north)
        double grade = 0;
        if (roads != TerrainRoads.Fixed && maxRoadGrade <= 0)
            for (int gz = 0; gz < gh - 1; gz++)
                for (int gx = 0; gx < gw - 1; gx++)
                {
                    int g = gz * gw + gx;
                    if (!road[g] || h[g] == h0[g]) continue;
                    if (road[g + 1]) grade = Math.Max(grade, Math.Abs(h[g + 1] - h[g]));
                    if (road[g + gw]) grade = Math.Max(grade, Math.Abs(h[g + gw] - h[g]));
                }
        int Stayed(int reason) => skipped.Count(s => s.Reason == reason && CenterWeight(s.P) > 0.5f);
        return new TerrainStats(changed, maxChange, blocked, grade, roadScale, moves.Count, Stayed(0), Stayed(1), Stayed(2), limited, tilesUnder);
    }

    /// a building including its driveways (parts): rectangles in world coordinates
    sealed record PoiUnit(PlacedPrefab Poi, List<PlacedPrefab> Members, List<(int X, int Z, int SX, int SZ)> Rects);

    // movable buildings in the grid: real buildings and fillers (no tiles, parts) outside of city tiles
    static List<PoiUnit> FindUnits(WorldData w, int gx0, int gz0, int gx1, int gz1, List<(PlacedPrefab P, int Reason)> skipped)
    {
        var tiles = w.Prefabs.Where(p => p.IsTile).ToList();
        var list = new List<PoiUnit>();
        foreach (var p in w.Prefabs)
        {
            if (p.Def == null || p.Def.Kind is not (PrefabKind.Poi or PrefabKind.Filler) || p.IsTile) continue;
            if (p.X >= gx1 || p.X + p.SizeX <= gx0 || p.Z >= gz1 || p.Z + p.SizeZ <= gz0) continue;
            double cx = p.X + p.SizeX / 2.0, cz = p.Z + p.SizeZ / 2.0;
            if (tiles.Any(t => t.Contains(cx, cz) || (p.X < t.X + t.SizeX + 1 && p.X + p.SizeX > t.X - 1 && p.Z < t.Z + t.SizeZ + 1 && p.Z + p.SizeZ > t.Z - 1))) { skipped.Add((p, 0)); continue; }   // completely or partly in a tile: fixed
            var members = new List<PlacedPrefab> { p };
            members.AddRange(PrefabParts.Attached(w, p));
            // if the unit does not lie completely in the grid, it stays fixed (otherwise there would be a step at the grid edge)
            if (members.Any(m => m.X < gx0 + 2 || m.X + m.SizeX > gx1 - 2 || m.Z < gz0 + 2 || m.Z + m.SizeZ > gz1 - 2)) { skipped.Add((p, 2)); continue; }
            list.Add(new PoiUnit(p, members, members.Select(m => (m.X, m.Z, m.SizeX, m.SizeZ)).ToList()));
        }
        // buildings whose parts overlap are not separate units: parts belong to at most one building
        var seen = new HashSet<PlacedPrefab>();
        var result = new List<PoiUnit>();
        foreach (var u in list)
        {
            if (u.Members.Skip(1).Any(seen.Contains)) { skipped.Add((u.Poi, 1)); continue; }
            foreach (var m in u.Members.Skip(1)) seen.Add(m);
            result.Add(u);
        }
        return result;
    }

    static double RectDist((int X, int Z, int SX, int SZ) r, double cx, double cz)
    {
        double dx = Math.Max(Math.Max(r.X - cx, cx - (r.X + r.SX)), 0), dz = Math.Max(Math.Max(r.Z - cz, cz - (r.Z + r.SZ)), 0);
        return Math.Sqrt(dx * dx + dz * dz);
    }

    static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// change the road equally over the whole width, no matter how wide the brush is: on the road the mean of the change
    /// of all road cells around (about 14 m, more than the width) applies, beside it it runs out over 6 m into the own change.
    /// This keeps the cross section level; intersections and junctions follow the same continuous calculation.
    static void LevelAcrossWidth(float[] d, bool[] road, float[] dRoad, float[] fObs, int gw, int gh)
    {
        int n = gw * gh;
        var num = new float[n];
        var den = new float[n];
        for (int g = 0; g < n; g++) if (road[g]) { num[g] = d[g]; den[g] = 1; }
        for (int pass = 0; pass < 2; pass++) { BoxBlur(num, gw, gh, 7); BoxBlur(den, gw, gh, 7); }
        for (int g = 0; g < n; g++)
        {
            if (den[g] < 0.02f) continue;
            float level = num[g] / den[g] * fObs[g];   // at buildings and tiles the change runs out
            if (road[g]) d[g] = level;
            else
            {
                float f = 1 - (float)Smooth(dRoad[g] / 6);
                d[g] += (level - d[g]) * f;
            }
        }
    }

    /// road may tilt (the user's request): the change follows the brush but is lightly smoothed near the road (4 m)
    /// so that no steps and spikes appear at the edges
    static void SmoothTilt(float[] d, bool[] road, float[] dRoad, bool[] obs, int gw, int gh)
    {
        var blur = (float[])d.Clone();
        for (int pass = 0; pass < 2; pass++) BoxBlur(blur, gw, gh, 2);
        for (int g = 0; g < gw * gh; g++)
        {
            if (obs[g]) continue;
            float f = 1 - (float)Smooth(dRoad[g] / 4);
            d[g] += (blur[g] - d[g]) * f;
        }
    }

    /// limit the gradient on the road (cells at 1 m distance, also across): where two neighboring road cells would become steeper than
    /// allowed, their changes balance each other in pairs (projection, repeated until everything fits). So the limit acts only at
    /// the steep place and does not take the effect away from the whole stroke. Where the road was already steeper, the old value applies
    /// (plus 3 %); roads in tiles and under buildings do not count. The correction is faded out beside the road over 8 m.
    /// returns the remaining share of the change on the road (1 = nothing reduced).
    static double LimitGrade(float[] h, float[] h0, bool[] road, bool[] obs, float[] dRoad, int gw, int gh, double limit)
    {
        int n = gw * gh;
        var d = new float[n];
        for (int g = 0; g < n; g++) d[g] = h[g] - h0[g];
        var d0 = (float[])d.Clone();
        for (int it = 0; it < 600; it++)
        {
            bool changed = false;
            for (int gz = 0; gz < gh - 1; gz++)
                for (int gx = 0; gx < gw - 1; gx++)
                {
                    int g = gz * gw + gx;
                    if (!road[g] || obs[g]) continue;
                    if (road[g + 1] && !obs[g + 1]) changed |= Project(g, g + 1);
                    if (road[g + gw] && !obs[g + gw]) changed |= Project(g, g + gw);
                }
            if (!changed) break;
        }
        bool Project(int a, int b)
        {
            float h0d = h0[a] - h0[b];
            float allowed = Math.Max((float)limit, Math.Abs(h0d) + 0.03f) - 0.002f;
            float diff = d[a] - d[b], hi = allowed - h0d, lo = -allowed - h0d;
            if (diff > hi) { float e = (diff - hi) / 2; d[a] -= e; d[b] += e; return true; }
            if (diff < lo) { float e = (lo - diff) / 2; d[a] += e; d[b] -= e; return true; }
            return false;
        }

        // transfer the correction c on the road also to the ground beside it (averaged, running out over 8 m)
        var num = new float[n];
        var den = new float[n];
        double before = 0, after = 0;
        for (int g = 0; g < n; g++)
            if (road[g] && !obs[g]) { num[g] = d[g] - d0[g]; den[g] = 1; before += Math.Abs(d0[g]); after += Math.Abs(d[g]); }
        for (int pass = 0; pass < 2; pass++) { BoxBlur(num, gw, gh, 3); BoxBlur(den, gw, gh, 3); }
        for (int g = 0; g < n; g++)
        {
            if (!road[g] && !obs[g] && den[g] > 0.02f)
                d[g] += num[g] / den[g] * (1 - (float)Smooth(dRoad[g] / 8));
            h[g] = h0[g] + d[g];
        }
        return before > 0 ? after / before : 1;
    }

    /// fill polygon (row by row, even-odd rule): cells whose center lies inside get weight 1
    static void FillPolygon(float[] brush, List<Point> poly, int gx0, int gz0, int gw, int gh)
    {
        var xs = new List<double>();
        for (int gz = 0; gz < gh; gz++)
        {
            double z = gz0 + gz + 0.5;
            xs.Clear();
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                if ((a.Y <= z && b.Y > z) || (b.Y <= z && a.Y > z))
                    xs.Add(a.X + (z - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int x0 = Math.Max(0, (int)Math.Ceiling(xs[k] - gx0 - 0.5)), x1 = Math.Min(gw - 1, (int)Math.Floor(xs[k + 1] - gx0 - 0.5));
                for (int gx = x0; gx <= x1; gx++) brush[gz * gw + gx] = 1;
            }
        }
    }

    /// weight 1 up to (1 − softness) of the radius, then soft (smoothstep) to 0 at the edge
    static float Falloff(double u, double softness)
    {
        double inner = 1 - softness;
        if (u <= inner || softness <= 0) return 1;
        double t = (1 - u) / softness;
        return (float)(t * t * (3 - 2 * t));
    }

    static double DistSq(double px, double pz, double dx, double dz, double len2)
    {
        double t = len2 > 0 ? Math.Clamp((px * dx + pz * dz) / len2, 0, 1) : 0;
        double ex = px - t * dx, ez = pz - t * dz;
        return ex * ex + ez * ez;
    }

    // distance in the maximum norm (square brush) from the point to the segment: the function is convex, ternary search suffices
    static double ChebyshevToSegment(double px, double pz, double dx, double dz)
    {
        double lo = 0, hi = 1;
        for (int i = 0; i < 24; i++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (Cheb(px - m1 * dx, pz - m1 * dz) < Cheb(px - m2 * dx, pz - m2 * dz)) hi = m2; else lo = m1;
        }
        double t = (lo + hi) / 2;
        return Cheb(px - t * dx, pz - t * dz);
    }

    static double Cheb(double x, double z) => Math.Max(Math.Abs(x), Math.Abs(z));

    // mean over (2r+1) cells in both directions, edges repeated
    static void BoxBlur(float[] a, int gw, int gh, int r)
    {
        var tmp = new float[Math.Max(gw, gh)];
        float inv = 1f / (2 * r + 1);
        for (int gz = 0; gz < gh; gz++)
        {
            int row = gz * gw;
            float s = 0;
            for (int k = -r; k <= r; k++) s += a[row + Math.Clamp(k, 0, gw - 1)];
            for (int gx = 0; gx < gw; gx++)
            {
                tmp[gx] = s * inv;
                s += a[row + Math.Min(gx + r + 1, gw - 1)] - a[row + Math.Max(gx - r, 0)];
            }
            Array.Copy(tmp, 0, a, row, gw);
        }
        for (int gx = 0; gx < gw; gx++)
        {
            float s = 0;
            for (int k = -r; k <= r; k++) s += a[Math.Clamp(k, 0, gh - 1) * gw + gx];
            for (int gz = 0; gz < gh; gz++)
            {
                tmp[gz] = s * inv;
                s += a[Math.Min(gz + r + 1, gh - 1) * gw + gx] - a[Math.Max(gz - r, 0) * gw + gx];
            }
            for (int gz = 0; gz < gh; gz++) a[gz * gw + gx] = tmp[gz];
        }
    }
}
