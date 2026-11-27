using System.Windows;

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
    /// junctions: optional pre-captured snapshots of existing roads around ProtectZones (captured once before slicing a long road)
    public static Stats Apply(WorldData w, WorldEdits e, RoadResult r, IProgress<string> progress, int from = 0, int to = -1, int stride = 1,
        List<JunctionPatch> junctions = null)
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
        progress.Report(Loc.T("Calculating distances to the road …"), 0.02);
        int bx0 = (int)Math.Floor(ks.Min(k => path[k].X) - reach) - 1, bz0 = (int)Math.Floor(ks.Min(k => path[k].Z) - reach) - 1;
        int bw = (int)Math.Ceiling(ks.Max(k => path[k].X) + reach) + 2 - bx0, bh = (int)Math.Ceiling(ks.Max(k => path[k].Z) + reach) + 2 - bz0;
        int rad = (int)Math.Ceiling(reach) + 1;
        bool whole = r.IsRemoval && s.Reshape;
        int T = whole ? Math.Max(bw, bh) : Tile;
        float[] gd = null;
        int[] gi = null;
        bool asphalt = s.Surface == Surface.Asphalt;
        bool blendLegs = !r.IsRemoval;
        var dmg = s.BreakInWasteland && !r.IsRemoval ? w.GetWastelandDamage() : null;
        double[] la = null, lb = null, lc = null;
        int changed = 0, painted = 0;
        string blendText = r.IsRemoval ? Loc.T("Removing surface and blending terrain …") : Loc.T("Blending terrain and painting surface …");
        progress.Report(blendText, 0.05);
        // protect existing roads (2026-10-28): slope and end of a new road no longer bend their area
        // (before, kinks of 13–15 % beside a junction); the height there is set by the adjustment at the intersection
        bool protectRoads = s.PaintSurface && !r.IsRemoval;
        int pn = path.Count;
        double sx = path[Math.Min(4, pn - 1)].X - path[0].X, sz = path[Math.Min(4, pn - 1)].Z - path[0].Z;
        double tex = path[pn - 1].X - path[Math.Max(0, pn - 5)].X, tez = path[pn - 1].Z - path[Math.Max(0, pn - 5)].Z;
        double sl = Math.Max(1e-6, Math.Sqrt(sx * sx + sz * sz)), el = Math.Max(1e-6, Math.Sqrt(tex * tex + tez * tez));
        // behind the end or before the start (more than 0.5 m in driving direction beyond the end point)
        bool BeyondEnd(int k, int x, int z) =>
            path[pn - 1].Dist - path[k].Dist <= 12 && ((x + 0.5 - path[pn - 1].X) * tex + (z + 0.5 - path[pn - 1].Z) * tez) / el > 0.5;
        bool BeyondStart(int k, int x, int z) =>
            path[k].Dist <= 12 && ((x + 0.5 - path[0].X) * sx + (z + 0.5 - path[0].Z) * sz) / sl < -0.5;
        bool Beyond(int k, int x, int z) =>
            k == pn - 1 && ((x + 0.5 - path[k].X) * tex + (z + 0.5 - path[k].Z) * tez) / el > 0.5 ||
            k == 0 && ((x + 0.5 - path[0].X) * sx + (z + 0.5 - path[0].Z) * sz) / sl < -0.5;
        bool Clipped(int k, int x, int z) => r.ClipEnd && BeyondEnd(k, x, z) || r.ClipStart && BeyondStart(k, x, z);
        // end on an existing road (T-junction: an intersection zone covers the end point). The round slope cap of a free end then
        // reached across the other road and pulled the terrain on its far side to the junction height, while the road itself kept its
        // height – a ditch or wall of 1–1.5 m along its far edge (the user's photos 2026-11-26, Buzovi-Land 1170/2250). There nothing
        // is changed: cells behind the end and on the far side of the other road's centre line keep their height.
        var joinStart = r.ClipStart ? null : JoinAxis(w, r, 0, protectRoads);
        var joinEnd = r.ClipEnd ? null : JoinAxis(w, r, pn - 1, protectRoads);
        if (protectRoads && junctions == null && r.ProtectZones is { Count: > 0 }) junctions = CaptureJunctions(w, r);
        if (junctions is { Count: 0 }) junctions = null;
        // (also beside the end: at an oblique junction the slope of the last waypoints reaches across the other road as well)
        bool BehindJoin(int k, int x, int z)
        {
            static bool Far((Point C, double Nx, double Nz, double FarSide)? j, int x, int z) =>
                j is { } v && ((x + 0.5 - v.C.X) * v.Nx + (z + 0.5 - v.C.Y) * v.Nz) * v.FarSide > 0;
            return path[k].Dist <= JoinReach && Far(joinStart, x, z)
                || path[pn - 1].Dist - path[k].Dist <= JoinReach && Far(joinEnd, x, z);
        }
        int tilesX = (bw + T - 1) / T, tilesZ = (bh + T - 1) / T, totalTiles = Math.Max(1, tilesX * tilesZ), tileIdx = 0;
        for (int tz = 0; tz < bh; tz += T)
            for (int tx = 0; tx < bw; tx += T)
            {
                if (totalTiles > 1) progress.Report(blendText, 0.05 + 0.93 * tileIdx / totalTiles);
                tileIdx++;
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

                // slope between two legs of the road (the user's report 2026-11-16, Darobe-Land 480 W 2340 N): in the inner corner of a
                // sharp bend every cell took only its nearest waypoint – on the bisector that switches from one leg to the other, and
                // where one leg falls and the other rises a wall of 5 m stood there. Second pass: every waypoint up to LegRange farther
                // than the nearest one contributes with weight exp(−Δd / LegScale) (its height and its slope factor at the cell's
                // distance) – on a single straight leg that is the same as before (symmetric around the nearest waypoint), between
                // two legs a soft transition.
                if (blendLegs)
                {
                    if (la == null || la.Length < gw * gh) { la = new double[gw * gh]; lb = new double[gw * gh]; lc = new double[gw * gh]; }
                    Array.Clear(la, 0, gw * gh); Array.Clear(lb, 0, gw * gh); Array.Clear(lc, 0, gw * gh);
                    foreach (int k in ks)
                    {
                        double px = path[k].X, pz = path[k].Z;
                        int cx = (int)Math.Floor(px) - gx0, cz = (int)Math.Floor(pz) - gz0;
                        if (cx + rad < 0 || cx - rad >= gw || cz + rad < 0 || cz - rad >= gh) continue;
                        double ke = r.WidthAt(k) / 2 + s.Shoulder + s.GrassEdge, kb = Math.Max(0.5, r.SlopeWidth[k]), kt0 = r.Profile[k] * 256;
                        double csk = r.CrossSlope == null ? 0 : r.CrossSlope[k];
                        var (knx, knz) = csk != 0 ? RoadPlanner.Normal(path, k) : (0.0, 0.0);
                        int z0 = Math.Max(cz - rad, 0), z1 = Math.Min(cz + rad, gh - 1), x0 = Math.Max(cx - rad, 0), x1 = Math.Min(cx + rad, gw - 1);
                        for (int gz = z0; gz <= z1; gz++)
                            for (int gx = x0; gx <= x1; gx++)
                            {
                                int g = gz * gw + gx;
                                if (gi[g] < 0) continue;
                                double dmin = gd[g];
                                double ex = gx0 + gx + 0.5 - px, ez = gz0 + gz + 0.5 - pz;
                                double dd = Math.Sqrt(ex * ex + ez * ez) - dmin;
                                if (dd > LegRange) continue;
                                double wt = Math.Exp(-dd / LegScale);
                                double uk = Math.Clamp(dmin <= ke ? 1 : 1 - (dmin - ke) / kb, 0, 1);
                                uk = uk * uk * (3 - 2 * uk);
                                double kt = csk != 0 ? kt0 + csk * Math.Clamp(ex * knx + ez * knz, -ke, ke) * 256 : kt0;
                                la[g] += wt; lb[g] += wt * kt * uk; lc[g] += wt * uk;
                            }
                    }
                }

                float[] fill = whole ? NaturalFill(w, r, gx0, gz0, gw, gh, gd, gi, maxHalf, progress, Clipped) : null;

                for (int gz = 0; gz < gh; gz++)
                    for (int gx = 0; gx < gw; gx++)
                    {
                        int g = gz * gw + gx, k = gi[g];
                        if (k < from || k >= to) continue;   // also k < 0 (no waypoint nearby)
                        double d = gd[g], bl = r.SlopeWidth[k];
                        double half = r.WidthAt(k) / 2, paintEdge = half + s.Shoulder, edge = paintEdge + s.GrassEdge;   // grass edge: terrain only, no surface; width may change along the road (taper)
                        // beyond the slope of the nearest waypoint: unchanged – unless another leg of the road still reaches here
                        bool far = d > edge + bl;
                        if (far && (!blendLegs || lc[g] <= 1e-6)) continue;
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
                            if (Clipped(k, x, z)) continue;
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
                        // the height between the waypoints, at the cell's own place along the road (the user's game test 2026-11-17: with the
                        // height of the nearest waypoint – they are 1 m apart – a steep road became a staircase of 1 m steps whose edges ran
                        // obliquely across the roadway because the road is never exactly parallel to the grid; on 14 % that were 14 cm steps)
                        int kn = Math.Min(pn - 1, k + 1), kp = Math.Max(0, k - 1);
                        double ux = path[kn].X - path[kp].X, uz = path[kn].Z - path[kp].Z, tl = Math.Sqrt(ux * ux + uz * uz);
                        double ta = tl > 1e-9 ? ((x + 0.5 - path[k].X) * ux + (z + 0.5 - path[k].Z) * uz) / tl : 0;
                        double at = Math.Clamp(path[k].Dist + Math.Clamp(ta, -1.5, 1.5), 0, path[pn - 1].Dist);
                        int i1 = r.IndexAt(at), i0 = Math.Max(0, i1 - 1);
                        double span = path[i1].Dist - path[i0].Dist, f = span > 1e-9 ? Math.Clamp((at - path[i0].Dist) / span, 0, 1) : 0;
                        double target = (r.Profile[i0] + (r.Profile[i1] - r.Profile[i0]) * f) * 256;
                        // cross slope at intersections: lateral distance to the center (at most up to the edge) times slope
                        double cslope = r.CrossSlope == null ? 0 : r.CrossSlope[i0] + (r.CrossSlope[i1] - r.CrossSlope[i0]) * f;
                        if (cslope != 0)
                        {
                            var (nx, nz) = RoadPlanner.Normal(path, k);
                            double side = Math.Clamp((x + 0.5 - path[k].X) * nx + (z + 0.5 - path[k].Z) * nz, -edge, edge);
                            target += cslope * side * 256;
                        }
                        double dw = 0, dH = 0;
                        if (dmg != null)
                        {
                            double ww = w.WastelandWeight(x + 0.5, z + 0.5);
                            if (ww > 0)
                            {
                                dw = r.DamageWeight(x + 0.5, z + 0.5) * ww;
                                if (dw > 0) dH = dmg.DeltaAt(x + 0.5, z + 0.5) * dw * 256.0;
                            }
                        }
                        double oPre = o - dH;
                        // smooth corner transition at junctions (the user's photos 2026-11-27, Wimovo-Land 782 W 1703 N):
                        // beside a protected existing road, a steep or oblique joining road otherwise pulled the terrain 1 m next to the
                        // frozen road edge straight to its own height (1.2 m cliff wall). Cap the target height relative to the existing
                        // road's smooth edge height by CornerSlope * distance (C1-smooth; leaves normal <= 14 % perpendicular junctions
                        // untouched and preserves the existing road's undisturbed cut/fill slope where u -> 0).
                        double cornerDist = double.MaxValue, cornerRef = 0;
                        if (junctions != null && w.RoadMask[hi] == 0)
                        {
                            for (int jp = 0; jp < junctions.Count; jp++)
                                if (junctions[jp].TryReference(x, z, out double cd, out double ch) && cd < cornerDist)
                                {
                                    cornerDist = cd;
                                    cornerRef = ch;
                                }
                        }
                        double effTarget = cornerDist <= CornerReach ? CornerCap(target, cornerRef, cornerDist) : target;
                        double nhPre = far ? oPre : oPre + (effTarget - oPre) * u;
                        if (blendLegs && d > edge && la[g] > 0)
                        {
                            // from the edge outwards faded in over LegFade (the roadway edge itself stays exactly at the road height)
                            double v = Math.Clamp((d - edge) / LegFade, 0, 1);
                            v = v * v * (3 - 2 * v);
                            if (far) v = 1;
                            double legNh;
                            if (cornerDist <= CornerReach && lc[g] > 1e-9)
                            {
                                double legTarget = CornerCap(lb[g] / lc[g], cornerRef, cornerDist);
                                legNh = oPre + (legTarget - oPre) * (lc[g] / la[g]);
                            }
                            else legNh = oPre + (lb[g] - oPre * lc[g]) / la[g];
                            nhPre += (legNh - nhPre) * v;
                        }
                        double nh = nhPre + dH;
                        var nv = (ushort)Math.Clamp(Math.Round(nh), 0, 65535);
                        // rebuild of an existing road (heights only) or colinear street joint: the road continues behind its ends – change nothing there
                        // (2026-10-28: the round end cap made 12 m of the road horizontal each, with edges of 13–16 %; 2026-11-27: at a bridge-swap joint
                        // it also poked asphalt spikes past the curving old road edge)
                        if ((!s.PaintSurface && Beyond(k, x, z)) || Clipped(k, x, z)) continue;
                        bool inZone = false;
                        foreach (var (zc, zr) in r.ProtectZones)
                            if ((x + 0.5 - zc.X) * (x + 0.5 - zc.X) + (z + 0.5 - zc.Y) * (z + 0.5 - zc.Y) <= zr * zr) { inZone = true; break; }
                        bool behind = BehindJoin(k, x, z);
                        bool keep = protectRoads && w.RoadMask[hi] != 0 && (s.KeepRoads || d > edge || Beyond(k, x, z) || inZone)
                            || behind;
                        if (!keep && nv != w.Heights[hi]) { e.SetHeight(hi, nv); changed++; }
                        if (!s.PaintSurface || behind) continue;   // rebuild at an intersection: heights only; T-junction: do not paint across the far side of the crossed road

                        // wasteland break (bomb craters & earthquake cracks, only in biome 4 = wasteland):
                        // interrupt surface cleanly with ragged edges (gravel shoulder stays ONLY at the outer road edge, never
                        // around the crater); scatter small broken roadway fragments (asphalt or gravel) inside the crater/crack
                        if (dmg != null && dw > 0.05 && w.BiomeAt(x, z) == 4)
                        {
                            double sd = dmg.BreakSdAt(x + 0.5, z + 0.5);
                            if (sd > -4.5)
                            {
                                double ragged = 1.5 * Octave(x, z, 7, 101) + 0.8 * Octave(x, z, 3, 211);
                                double eff = sd - (1 - dw) * 6.0 + ragged;
                                if (eff > 0)
                                {
                                    // inside crater / crack: never paint gravel shoulder; only scattered roadway fragments
                                    // separated from the broken lip (eff > 1.2) inside the original roadway width (d <= half)
                                    bool fragment = false;
                                    if (d <= half && eff > 1.2)
                                    {
                                        double frag = 0.60 * Octave(x, z, 3.6, 409) + 0.40 * Octave(x, z, 1.8, 613);
                                        double thresh = (asphalt ? 0.28 : 0.35) + 0.08 * Math.Clamp((eff - 1.2) / 8.0, 0, 1);
                                        fragment = frag > thresh;
                                    }
                                    if (!fragment) continue;
                                }
                            }
                        }

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

    /// slope between two legs of a road: weight scale (m), waypoints farther than the nearest one by more than LegRange do not count,
    /// fade-in from the roadway edge outwards (m)
    const double LegScale = 4, LegRange = 24, LegFade = 6;
    /// so far (m along the road) from an end on an existing road the terrain on that road's far side stays unchanged
    const int JoinReach = 60;
    /// corner transition at junctions: window radius around each ProtectZone (m), max distance from the existing road edge (m),
    /// and max slope rising/falling away from that edge (22 %; C1-smooth cap leaves <= 14.3 % perpendicular climbs untouched)
    const int JunctionPatchRadius = 44;
    const double CornerReach = 24.0, CornerSlope = 0.22;

    static double SmoothCap(double val, double cap, double pad)
    {
        double a = Math.Abs(val);
        if (a <= cap - pad) return val;
        if (a >= cap + pad) return Math.Sign(val) * cap;
        double t = (a - (cap - pad)) / (2 * pad);
        return Math.Sign(val) * (cap - pad * (1 - t) * (1 - t));
    }

    static double CornerCap(double val256, double ref256, double dmin)
    {
        // Smooth convex cap: tight (22–32 %) right beside the frozen road edge (d = 1..5 m) so no cliff wall forms, opening smoothly
        // to ~11 m by d = 24 m so steep hillside branches nearby never hit an abrupt snap-back cliff at the outer edge of CornerReach.
        double cap = (CornerSlope * dmin + 0.010 * dmin * dmin) * 256.0;
        double capped = ref256 + SmoothCap(val256 - ref256, cap, 0.35 * cap);
        if (dmin > CornerReach - 4)
        {
            double ft = (dmin - (CornerReach - 4)) / 4.0;
            capped += (val256 - capped) * (ft * ft * (3 - 2 * ft));
        }
        return capped;
    }

    /// Snapshot of existing road surface and pre-damage heights around one junction/crossing (captured before the new road is painted,
    /// so sliced roads never mistake their own earlier slices for an existing road).
    public sealed class JunctionPatch
    {
        readonly int x0, z0, s;
        readonly byte[] mask;
        readonly float[] heights;   // pre-damage heights in 256ths of a meter (valid where mask != 0)
        readonly float[] chamfer;

        internal JunctionPatch(WorldData w, Point center, bool breakInWasteland)
        {
            const int r = JunctionPatchRadius;
            s = 2 * r + 1;
            x0 = (int)Math.Floor(center.X) - r;
            z0 = (int)Math.Floor(center.Y) - r;
            mask = new byte[s * s];
            heights = new float[s * s];
            chamfer = new float[s * s];
            for (int lz = 0; lz < s; lz++)
                for (int lx = 0; lx < s; lx++)
                {
                    int g = lz * s + lx, wx = x0 + lx, wz = z0 + lz;
                    if (w.Contains(wx, wz) && w.RoadMask[w.Index(wx, wz)] != 0)
                    {
                        mask[g] = w.RoadMask[w.Index(wx, wz)];
                        heights[g] = (float)(w.PreDamageHeightAt(wx, wz, breakInWasteland) * 256.0);
                        chamfer[g] = 0;
                    }
                    else chamfer[g] = 1e9f;
                }
            void Relax(int g, int q, float step) { if (chamfer[q] + step < chamfer[g]) chamfer[g] = chamfer[q] + step; }
            for (int lz = 0; lz < s; lz++)
                for (int lx = 0; lx < s; lx++)
                {
                    int g = lz * s + lx;
                    if (lx > 0) Relax(g, g - 1, 1);
                    if (lz > 0) { Relax(g, g - s, 1); if (lx > 0) Relax(g, g - s - 1, 1.414f); if (lx < s - 1) Relax(g, g - s + 1, 1.414f); }
                }
            for (int lz = s - 1; lz >= 0; lz--)
                for (int lx = s - 1; lx >= 0; lx--)
                {
                    int g = lz * s + lx;
                    if (lx < s - 1) Relax(g, g + 1, 1);
                    if (lz < s - 1) { Relax(g, g + s, 1); if (lx < s - 1) Relax(g, g + s + 1, 1.414f); if (lx > 0) Relax(g, g + s - 1, 1.414f); }
                }
        }

        internal bool TryReference(int wx, int wz, out double dmin, out double hOld256)
        {
            dmin = double.MaxValue;
            hOld256 = 0;
            int lx = wx - x0, lz = wz - z0;
            if ((uint)lx >= (uint)s || (uint)lz >= (uint)s) return false;
            int g = lz * s + lx;
            float cd = chamfer[g];
            if (cd <= 0 || cd > CornerReach + 2) return false;
            int look = (int)Math.Ceiling(Math.Min(CornerReach + 1, cd + 1));
            int z1 = Math.Max(0, lz - look), z2 = Math.Min(s - 1, lz + look);
            int x1 = Math.Max(0, lx - look), x2 = Math.Min(s - 1, lx + look);
            double minSq = double.MaxValue;
            for (int qz = z1; qz <= z2; qz++)
            {
                int dz = qz - lz, dz2 = dz * dz, row = qz * s;
                for (int qx = x1; qx <= x2; qx++)
                {
                    if (mask[row + qx] == 0) continue;
                    int dx = qx - lx;
                    double sq = dx * dx + dz2;
                    if (sq < minSq) minSq = sq;
                }
            }
            if (minSq == double.MaxValue) return false;
            dmin = Math.Sqrt(minSq);
            if (dmin > CornerReach) return false;
            // smooth weighted mean of existing road pixels within dmin + 2 m (same washboard-free weighting as WildernessGenerator.FeatherJunction)
            double lim = dmin + 2.0, limSq = lim * lim;
            int wlook = (int)Math.Ceiling(lim);
            int wz1 = Math.Max(0, lz - wlook), wz2 = Math.Min(s - 1, lz + wlook);
            int wx1 = Math.Max(0, lx - wlook), wx2 = Math.Min(s - 1, lx + wlook);
            double sum = 0, wsum = 0;
            for (int qz = wz1; qz <= wz2; qz++)
            {
                int dz = qz - lz, dz2 = dz * dz, row = qz * s;
                for (int qx = wx1; qx <= wx2; qx++)
                {
                    int q = row + qx;
                    if (mask[q] == 0) continue;
                    int dx = qx - lx;
                    double sq = dx * dx + dz2;
                    if (sq >= limSq) continue;
                    double wt = lim - Math.Sqrt(sq);
                    sum += wt * heights[q];
                    wsum += wt;
                }
            }
            if (wsum <= 0) return false;
            hOld256 = sum / wsum;
            return true;
        }
    }

    internal static List<JunctionPatch> CaptureJunctions(WorldData w, RoadResult r)
    {
        if (r == null || !r.Settings.PaintSurface || r.IsRemoval || r.ProtectZones == null || r.ProtectZones.Count == 0) return null;
        var list = new List<JunctionPatch>(r.ProtectZones.Count);
        foreach (var (zc, _) in r.ProtectZones)
            list.Add(new JunctionPatch(w, zc, r.Settings.BreakInWasteland));
        return list;
    }

    /// end k (first or last waypoint) of the new road lies on an existing road (inside one of its intersection zones): centre of
    /// that road, its normal (main axis of the road pixels within 8 m of the zone centre, measured before the new road is painted)
    /// and the sign that marks its far side (away from the new road). null = free end or no road there.
    static (Point C, double Nx, double Nz, double FarSide)? JoinAxis(WorldData w, RoadResult r, int k, bool protectRoads)
    {
        if (!protectRoads || r.ProtectZones == null || r.Path.Count < 3) return null;
        var p = r.Path[k];
        Point? zc = null;
        foreach (var (c, rad) in r.ProtectZones)
            if ((p.X - c.X) * (p.X - c.X) + (p.Z - c.Y) * (p.Z - c.Y) <= (rad + 1) * (rad + 1)) { zc = c; break; }
        if (zc is not Point zp) return null;
        // direction of the other road: the line through the zone centre (±30 m) with the most road surface (a main-axis fit over a
        // small circle fails on a wide road – the circle is all surface)
        bool Paved(double x, double z)
        {
            int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
            return w.Contains(ix, iz) && w.RoadMask[w.Index(ix, iz)] != 0;
        }
        double best = -1, axis = 0;
        for (int a = 0; a < 180; a += 3)
        {
            double ca = Math.Cos(a * Math.PI / 180), sa = Math.Sin(a * Math.PI / 180);
            int cnt = 0;
            for (int t = -30; t <= 30; t++) if (Paved(zp.X + ca * t, zp.Y + sa * t)) cnt++;
            if (cnt > best) { best = cnt; axis = a * Math.PI / 180; }
        }
        if (best < 20) return null;
        double nx = -Math.Sin(axis), nz = Math.Cos(axis);
        // side of the new road: the waypoint 15 m inside the road (or its other end if it is shorter)
        var q = r.Path[r.IndexAt(k == 0 ? Math.Min(15, r.Length) : Math.Max(0, r.Length - 15))];
        double own = (q.X - zp.X) * nx + (q.Z - zp.Y) * nz;
        if (Math.Abs(own) < 2) return null;   // runs along the other road: no T-junction
        // continues the other road (re-laid road, bridge swap: the new road takes over from the old one at its joint): no T-junction
        // either – the side "behind" it would be one whole side of the new road (the user's report 2026-11-27, Land-Of-Fear 3561/2211:
        // the gravel edge stayed 2 m above the new road for 60 m)
        double ax = Math.Cos(axis), az = Math.Sin(axis), dx = q.X - p.X, dz = q.Z - p.Z, dl = Math.Sqrt(dx * dx + dz * dz);
        if (dl > 1 && Math.Abs(dx * ax + dz * az) / dl > Math.Cos(35 * Math.PI / 180)) return null;
        return (zp, nx, nz, -Math.Sign(own));
    }

    // removal: fill the old road bed as the terrain all around dictates (Laplace fill: every point = mean of
    // its neighbors, edges and preserved intersections fixed – fills cuts, removes embankments), then slight unevenness
    // at the strength of the terrain beside it. Result in meters per grid cell (valid only in the area).
    static float[] NaturalFill(WorldData w, RoadResult r, int gx0, int gz0, int gw, int gh, float[] gd, int[] gi, double half, IProgress<string> progress,
        Func<int, int, int, bool> clipped = null)
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
                if (gd[g] >= half + r.SlopeWidth[k] || r.InTile(x + 0.5, z + 0.5) || r.KeptRoadDistance(x + 0.5, z + 0.5) <= 0 || clipped?.Invoke(k, x, z) == true) continue;
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
