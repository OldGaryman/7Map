namespace MapEditor.Map;

/// Enters a planned road into the world (like tools/road.ps1 -Apply): adjust the terrain, paint the surface – first only in
/// memory (WorldEdits), into the files only with "Save".
public static class RoadApplier
{
    public sealed record Stats(int HeightPoints, int Pixels);

    /// from/to: only the part of the road with waypoints from ≤ k &lt; to (to &lt; 0 = to the end) – enter long roads piece by piece
    /// (roads between cities, several km: the grid over the whole road would need hundreds of MB); the distances
    /// are therefore also calculated from the waypoints before/after, so that pixels at the border get the right waypoint.
    /// stride: only every n-th waypoint counts for the distances (waypoints are every 0.25 m; 4 = 1 m is enough for long roads)
    public static Stats Apply(WorldData w, WorldEdits e, RoadResult r, IProgress<string> progress, int from = 0, int to = -1, int stride = 1)
    {
        var s = r.Settings;
        var path = r.Path;
        double maxHalf = s.Width / 2, reach = maxHalf + s.Shoulder + s.GrassEdge + RoadPlanner.MaxSlopeWidth + 2;   // Settings.Width = largest width
        if (to < 0 || to > path.Count) to = path.Count;
        from = Math.Clamp(from, 0, path.Count);
        if (from >= to) return new Stats(0, 0);
        // waypoints that count for the distances: the part itself and 2 × reach before and after
        int k0 = from, k1 = to - 1;
        while (k0 > 0 && path[from].Dist - path[k0 - 1].Dist <= 2 * reach) k0--;
        while (k1 < path.Count - 1 && path[k1 + 1].Dist - path[to - 1].Dist <= 2 * reach) k1++;
        // always the same waypoints (multiples of stride, plus the last), otherwise a pixel at the border of two pieces gets
        // a waypoint of the respective other piece in both pieces and is processed by neither (1 m wide strip, step up to 10 m)
        stride = Math.Max(1, stride);
        var ks = new List<int>();
        for (int k = k0 - k0 % stride; k <= k1; k += stride) ks.Add(k);
        if (k1 == path.Count - 1 && ks[^1] != k1) ks.Add(k1);

        // grid around the road: smallest distance to the center line and nearest waypoint per meter.
        // In tiles of Tile × Tile m (finding F17: one grid over the rectangle around a long diagonal road needed several hundred MB).
        // Every cell is processed only with its own values and its nearest waypoint, and a tile looks at every waypoint whose
        // square (± rad) touches it – so the result is the same as with one grid. The natural fill of a removal needs the
        // whole area at once: one tile there.
        progress.Report(Loc.T("Calculating distances to the road …"));
        int bx0 = (int)Math.Floor(ks.Min(k => path[k].X) - reach) - 1, bz0 = (int)Math.Floor(ks.Min(k => path[k].Z) - reach) - 1;
        int bw = (int)Math.Ceiling(ks.Max(k => path[k].X) + reach) + 2 - bx0, bh = (int)Math.Ceiling(ks.Max(k => path[k].Z) + reach) + 2 - bz0;
        int rad = (int)Math.Ceiling(reach) + 1;
        bool whole = r.IsRemoval && s.Reshape;
        int T = whole ? Math.Max(bw, bh) : Tile;
        float[] gd = null;
        int[] gi = null;
        bool asphalt = s.Surface == Surface.Asphalt;
        int changed = 0, painted = 0;
        progress.Report(r.IsRemoval ? Loc.T("Removing surface and blending terrain …") : Loc.T("Blending terrain and painting surface …"));
        // protect existing roads (2026-10-28): slope and end of a new road no longer bend their area
        // (before, kinks of 13–15 % beside a junction); the height there is set by the adjustment at the intersection
        bool protectRoads = s.PaintSurface && !r.IsRemoval;
        int pn = path.Count;
        double sx = path[Math.Min(4, pn - 1)].X - path[0].X, sz = path[Math.Min(4, pn - 1)].Z - path[0].Z;
        double tex = path[pn - 1].X - path[Math.Max(0, pn - 5)].X, tez = path[pn - 1].Z - path[Math.Max(0, pn - 5)].Z;
        double sl = Math.Max(1e-6, Math.Sqrt(sx * sx + sz * sz)), el = Math.Max(1e-6, Math.Sqrt(tex * tex + tez * tez));
        // behind the end or before the start (more than 0.5 m in driving direction beyond the end point)
        bool Beyond(int k, int x, int z) =>
            k == pn - 1 && ((x + 0.5 - path[k].X) * tex + (z + 0.5 - path[k].Z) * tez) / el > 0.5 ||
            k == 0 && ((x + 0.5 - path[0].X) * sx + (z + 0.5 - path[0].Z) * sz) / sl < -0.5;
        for (int tz = 0; tz < bh; tz += T)
            for (int tx = 0; tx < bw; tx += T)
            {
                int gx0 = bx0 + tx, gz0 = bz0 + tz, gw = Math.Min(T, bw - tx), gh = Math.Min(T, bh - tz);
                if (gd == null || gd.Length < gw * gh) { gd = new float[gw * gh]; gi = new int[gw * gh]; }
                Array.Fill(gd, float.MaxValue, 0, gw * gh);
                Array.Fill(gi, -1, 0, gw * gh);
                bool any = false;
                foreach (int k in ks)
                {
                    double px = path[k].X, pz = path[k].Z;
                    int cx = (int)Math.Floor(px) - gx0, cz = (int)Math.Floor(pz) - gz0;
                    if (cx + rad < 0 || cx - rad >= gw || cz + rad < 0 || cz - rad >= gh) continue;   // square does not touch this tile
                    any = true;
                    // only the part of the square inside this tile (same order as before, the cells outside were skipped anyway)
                    int z0 = Math.Max(cz - rad, 0), z1 = Math.Min(cz + rad, gh - 1), x0 = Math.Max(cx - rad, 0), x1 = Math.Min(cx + rad, gw - 1);
                    for (int gz = z0; gz <= z1; gz++)
                    {
                        for (int gx = x0; gx <= x1; gx++)
                        {
                            double ex = gx0 + gx + 0.5 - px, ez = gz0 + gz + 0.5 - pz;
                            float d = (float)Math.Sqrt(ex * ex + ez * ez);
                            int g = gz * gw + gx;
                            if (d < gd[g]) { gd[g] = d; gi[g] = k; }
                        }
                    }
                }
                if (!any) continue;

                float[] fill = whole ? NaturalFill(w, r, gx0, gz0, gw, gh, gd, gi, maxHalf, progress) : null;

                for (int gz = 0; gz < gh; gz++)
                    for (int gx = 0; gx < gw; gx++)
                    {
                        int g = gz * gw + gx, k = gi[g];
                        if (k < from || k >= to) continue;   // also k < 0 (no waypoint nearby)
                        double d = gd[g], bl = r.SlopeWidth[k];
                        double half = r.WidthAt(k) / 2, paintEdge = half + s.Shoulder, edge = paintEdge + s.GrassEdge;   // grass edge: terrain only, no surface; width may change along the road (taper)
                        if (d > edge + bl) continue;
                        int x = gx0 + gx, z = gz0 + gz;
                        if (!w.Contains(x, z)) continue;
                        int hi = w.Index(x, z);
                        // city tiles: the heights are dictated by the building, the tile's own roads are created by the game (splat3_processed).
                        // Only paint/remove our surface in splat3.png; it shows everywhere the tile places nothing.
                        if (r.InTile(x + 0.5, z + 0.5))
                        {
                            if (d > half || !s.PaintSurface) continue;
                            uint own = e.SplatAt(x, z);
                            if (r.IsRemoval)
                            {
                                if ((own & 0x00FFFF00) != 0) { e.SetSplat(x, z, WorldEdits.EmptyPixel); e.SetRoad(hi, 0); painted++; }
                            }
                            else if ((own & 0x00FFFF00) != 0 || w.RoadMask[hi] == 0)   // do not paint over the road of the tile
                            {
                                if (asphalt) { e.SetSplat(x, z, WorldEdits.AsphaltPixel); e.SetRoad(hi, WorldData.Asphalt); painted++; }
                                else if ((own >> 16 & 0xFF) == 0) { e.SetSplat(x, z, WorldEdits.GravelPixel); e.SetRoad(hi, WorldData.Gravel); painted++; }
                            }
                            continue;
                        }
                        double o = w.Heights[hi];
                        double u = d <= edge ? 1 : 1 - (d - edge) / bl;
                        u = Math.Clamp(u, 0, 1);
                        u = u * u * (3 - 2 * u);

                        if (r.IsRemoval)
                        {
                            // preserved intersections: path stays as it is; beside it a soft transition over the slope width
                            // the fill already connects seamlessly at the edge of the area: no additional transition (would give a gully)
                            if (fill != null) u = 1;
                            double kept = r.KeptRoadDistance(x + 0.5, z + 0.5, out byte keptKind);
                            if (kept <= 0)
                            {
                                // surface of the crossing path continuous (before, the removed road lay over it there)
                                if (d <= half)
                                {
                                    e.SetSplat(x, z, keptKind == WorldData.Asphalt ? WorldEdits.AsphaltPixel : WorldEdits.GravelPixel);
                                    e.SetRoad(hi, keptKind);
                                }
                                continue;
                            }
                            if (kept < bl) { double v = kept / bl; u *= v * v * (3 - 2 * v); }
                            if (fill != null)
                            {
                                var hv = (ushort)Math.Clamp(Math.Round(o + (fill[g] * 256 - o) * u), 0, 65535);
                                if (hv != w.Heights[hi]) { e.SetHeight(hi, hv); changed++; }
                            }
                            // delete surface (empty pixel = everything 0)
                            if (d <= half && e.SplatAt(x, z) != WorldEdits.EmptyPixel)
                            {
                                e.SetSplat(x, z, WorldEdits.EmptyPixel);
                                e.SetRoad(hi, 0);
                                painted++;
                            }
                            continue;
                        }

                        // terrain: exactly the road height on roadway and edge, soft to the terrain in the slope (smoothstep)
                        double target = r.Profile[k] * 256;
                        // cross slope at intersections: lateral distance to the center (at most up to the edge) times slope
                        if (r.CrossSlope != null && r.CrossSlope[k] != 0)
                        {
                            var (nx, nz) = RoadPlanner.Normal(path, k);
                            double side = Math.Clamp((x + 0.5 - path[k].X) * nx + (z + 0.5 - path[k].Z) * nz, -edge, edge);
                            target += r.CrossSlope[k] * side * 256;
                        }
                        var nv = (ushort)Math.Clamp(Math.Round(o + (target - o) * u), 0, 65535);
                        // rebuild of an existing road (heights only): the road continues behind its ends – change nothing there
                        // (2026-10-28: the round end cap made 12 m of the road horizontal each, with edges of 13–16 %)
                        if (!s.PaintSurface && Beyond(k, x, z)) continue;
                        bool inZone = false;
                        foreach (var (zc, zr) in r.ProtectZones)
                            if ((x + 0.5 - zc.X) * (x + 0.5 - zc.X) + (z + 0.5 - zc.Y) * (z + 0.5 - zc.Y) <= zr * zr) { inZone = true; break; }
                        bool keep = protectRoads && w.RoadMask[hi] != 0 && (d > edge || Beyond(k, x, z) || inZone);
                        if (!keep && nv != w.Heights[hi]) { e.SetHeight(hi, nv); changed++; }
                        if (!s.PaintSurface) continue;   // rebuild at an intersection: heights only

                        // surface (BGRA): asphalt A255 R255 G0 B0, gravel A255 R0 G255 B0; gravel does not paint over asphalt
                        if (d <= half)
                        {
                            uint px = e.SplatAt(x, z);
                            if (asphalt) { e.SetSplat(x, z, WorldEdits.AsphaltPixel); e.SetRoad(hi, WorldData.Asphalt); painted++; }
                            else if ((px >> 16 & 0xFF) == 0) { e.SetSplat(x, z, WorldEdits.GravelPixel); e.SetRoad(hi, Math.Max(w.RoadMask[hi], WorldData.Gravel)); painted++; }
                        }
                        else if (d <= paintEdge && (e.SplatAt(x, z) & 0x00FFFF00) == 0)
                        {
                            e.SetSplat(x, z, WorldEdits.GravelPixel);
                            e.SetRoad(hi, Math.Max(w.RoadMask[hi], WorldData.Gravel));
                            painted++;
                        }
                    }
            }
        return new Stats(changed, painted);
    }

    /// tile size of the distance grid in meters (8 bytes per cell: 512 × 512 = 2 MB)
    const int Tile = 512;

    // removal: fill the old road bed as the terrain all around dictates (Laplace fill: every point = mean of
    // its neighbors, edges and preserved intersections fixed – fills cuts, removes embankments), then slight unevenness
    // at the strength of the terrain beside it. Result in meters per grid cell (valid only in the area).
    static float[] NaturalFill(WorldData w, RoadResult r, int gx0, int gz0, int gw, int gh, float[] gd, int[] gi, double half, IProgress<string> progress)
    {
        progress.Report(Loc.T("Filling the road bed naturally …"));
        int n = gw * gh;
        var h = new float[n];
        var free = new bool[n];
        int count = 0;
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx, x = gx0 + gx, z = gz0 + gz;
                h[g] = (float)w.HeightAt(x, z);
                int k = gi[g];
                if (k < 0 || gx == 0 || gz == 0 || gx == gw - 1 || gz == gh - 1 || !w.Contains(x, z)) continue;
                if (gd[g] >= half + r.SlopeWidth[k] || r.InTile(x + 0.5, z + 0.5) || r.KeptRoadDistance(x + 0.5, z + 0.5) <= 0) continue;
                free[g] = true;
                count++;
                // start value: straight across between both edges (speeds up the calculation)
                var (nx, nz) = RoadPlanner.Normal(r.Path, k);
                double side = (x + 0.5 - r.Path[k].X) * nx + (z + 0.5 - r.Path[k].Z) * nz, reach = half + r.SlopeWidth[k];
                double t = Math.Clamp((side + reach) / (2 * reach), 0, 1);
                h[g] = (float)(r.SideRight[k] * (1 - t) + r.SideLeft[k] * t);
            }
        if (count == 0) return h;
        var cells = new int[count];
        for (int g = 0, c = 0; g < n; g++) if (free[g]) cells[c++] = g;
        for (int it = 0; it < 600; it++)
        {
            float maxChange = 0;
            foreach (int g in cells)
            {
                float v = (h[g - 1] + h[g + 1] + h[g - gw] + h[g + gw]) / 4;
                float nv = h[g] + 1.85f * (v - h[g]);
                maxChange = Math.Max(maxChange, Math.Abs(nv - h[g]));
                h[g] = nv;
            }
            if (maxChange < 0.002f) break;
        }

        // unevenness of the terrain beside it: deviation from the mean of the points 3 m around, measured in an 8 m wide
        // strip outside the area
        double sum = 0;
        int m = 0;
        for (int gz = 3; gz < gh - 3; gz++)
            for (int gx = 3; gx < gw - 3; gx++)
            {
                int g = gz * gw + gx, k = gi[g];
                if (k < 0 || free[g]) continue;
                double edge = half + r.SlopeWidth[k];
                if (gd[g] < edge + 1 || gd[g] > edge + 8 || r.InTile(gx0 + gx + 0.5, gz0 + gz + 0.5)) continue;
                double avg = (h[g - 3] + h[g + 3] + h[g - 3 * gw] + h[g + 3 * gw]) / 4;
                sum += (h[g] - avg) * (h[g] - avg);
                m++;
            }
        double amp = Math.Clamp(m > 0 ? Math.Sqrt(sum / m) * 2 : 0.2, 0.1, 0.6);
        foreach (int g in cells)
        {
            int x = gx0 + g % gw, z = gz0 + g / gw;
            double bl = r.SlopeWidth[gi[g]], v = Math.Clamp((half + bl - gd[g]) / bl, 0, 1);   // fade out towards the edge
            h[g] += (float)(amp * v * v * (3 - 2 * v) * Noise(x, z));
        }
        return h;
    }

    // value noise in three levels (16, 7 and 3 m), roughly -1 … 1, always the same for every place
    internal static double Noise(int x, int z) => 0.6 * Octave(x, z, 16, 11) + 0.3 * Octave(x, z, 7, 23) + 0.15 * Octave(x, z, 3, 37);

    internal static double Octave(int x, int z, double size, int seed)
    {
        double fx = x / size, fz = z / size;
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        double tx = fx - ix, tz = fz - iz;
        tx = tx * tx * (3 - 2 * tx); tz = tz * tz * (3 - 2 * tz);
        double a = Hash(ix, iz, seed), b = Hash(ix + 1, iz, seed), c = Hash(ix, iz + 1, seed), d = Hash(ix + 1, iz + 1, seed);
        return (a * (1 - tx) + b * tx) * (1 - tz) + (c * (1 - tx) + d * tx) * tz;
    }

    static double Hash(int x, int z, int seed)
    {
        unchecked
        {
            uint hsh = (uint)(x * 374761393 + z * 668265263 + seed * 144269);
            hsh = (hsh ^ (hsh >> 13)) * 1274126177;
            hsh ^= hsh >> 16;
            return hsh / (double)uint.MaxValue * 2 - 1;
        }
    }
}
