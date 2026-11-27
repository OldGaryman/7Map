using System.Windows;

namespace MapEditor.Map;

/// Populates the burnt-forest volcano's two prepared pads (Pad 0 on the crater rim, Pad 1 halfway up)
/// with medium wilderness POIs (placed at exact pad elevation), builds the 5 m gravel spiral road (12–20 % monotonic grade
/// with a gentle 0–4 % landing podest and curved oblique driveway at Pad 1, +1 m downhill verge, terminating cleanly at any
/// pre-existing road), and applies ALWAYS 2 dramatic, realistic landslides (1 horseshoe stepped downhill chute + 1 shallow
/// uphill breakaway niche with a long 50–80 m downhill debris tongue).
public sealed partial class WildernessGenerator
{
    const double VolcanoRoadWidth = 5.0;
    VolcanoSite volcanoSiteRef;
    WildPath volcanoSpiralPath, volcanoBranchPath;
    bool volcanoSpiralConnected;

    void PlaceVolcanoPois(List<(string Name, PrefabDef Def, (int[] Biomes, double Bias, double Weight) Rule)> pool)
    {
        volcanoSiteRef = null;
        volcanoSpiralPath = null;
        volcanoBranchPath = null;
        volcanoSpiralConnected = false;

        var site = VolcanoSite.Load(w.Folder);
        if (site == null || !site.NeedsWildernessPois || site.Pads.Count == 0) return;
        volcanoSiteRef = site;

        var burntPool = pool.Where(c => c.Rule.Biomes == null || c.Rule.Biomes.Contains(1)).ToList();
        if (burntPool.Count == 0) return;

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WildPath rampPath = null;

        for (int padIdx = 0; padIdx < site.Pads.Count; padIdx++)
        {
            var pad = site.Pads[padIdx];
            int rCheck = (int)Math.Max(12, pad.Radius * 0.65);
            if (AnyRect((int)Math.Round(pad.X) - rCheck, (int)Math.Round(pad.Z) - rCheck, rCheck * 2, rCheck * 2, 0, g => building[g] || tile[g]))
                continue;

            var placed = TryPlaceOnVolcanoPad(pad, burntPool, usedNames, out var exitPt, out var exitOut);
            if (placed == null) continue;

            WildPath path;
            if (padIdx == 0 || rampPath == null)
            {
                path = BuildVolcanoAccessPath(site, exitPt, exitOut, placed.TerrainHeight, out bool connected);
                if (path != null)
                {
                    rampPath = path;
                    volcanoSpiralPath = path;
                    volcanoSpiralConnected = connected;
                }
            }
            else
            {
                path = BuildVolcanoRimPath(site, pad, exitPt, exitOut, placed.TerrainHeight, rampPath);
                if (path != null) volcanoBranchPath = path;
            }

            Accept(placed, 1, path);
            usedNames.Add(placed.Name);
            plan.PerBiome[1]++;
        }
    }

    /// Multi-stage road connection from the volcano spiral foot using YAMEE's own FindPath + HasSelfLoop rules:
    /// Temporarily blocks the incoming spiral corridor (20 m radius up to 24 m before foot) so FindPath can NEVER
    /// double back into a hairpin over the spiral, keeps startSpread = ±45°, ArrivalOk, Feasible and RoadNetwork.HasSelfLoop!
    WildPath FindVolcanoTail(VolcanoSite site, List<Point> spiralPts, Point foot, Vector footDir, double footH)
    {
        // Temporarily mark the incoming spiral (more than 36 m behind foot, 16 m radius) in block |= 2
        // so FindPath has a clean open start at foot+Lead yet can NEVER fold back into a hairpin over the spiral!
        var blockedCells = new List<int>();
        double walkBack = 0;
        for (int i = spiralPts.Count - 1; i >= 1; i--)
        {
            walkBack += (spiralPts[i] - spiralPts[i - 1]).Length;
            if (walkBack < 36.0) continue;
            var p = spiralPts[i - 1];
            int cx = (int)Math.Floor((p.X + half) / C), cz = (int)Math.Floor((p.Y + half) / C);
            const int RadCells = 2; // ~16 m clearance around incoming spiral (leaves foot + Lead completely open)
            for (int dz = -RadCells; dz <= RadCells; dz++)
                for (int dx = -RadCells; dx <= RadCells; dx++)
                {
                    if (dx * dx + dz * dz > RadCells * RadCells) continue;
                    int gx = cx + dx, gz = cz + dz;
                    if (gx < 0 || gz < 0 || gx >= gw || gz >= gw) continue;
                    int g = gz * gw + gx;
                    if ((block[g] & 2) == 0) { block[g] |= 2; blockedCells.Add(g); }
                }
        }

        bool CombinedOk(WildPath cand)
        {
            if (cand == null || cand.Points.Count < 2) return false;
            // Check RoadNetwork.HasSelfLoop across the combined seam (last ~200 m of spiral + full tail)
            var test = new List<Point>();
            int from = Math.Max(0, spiralPts.Count - 18);
            for (int i = from; i < spiralPts.Count; i++) test.Add(spiralPts[i]);
            foreach (var q in cand.Points.Skip(1))
                if ((q - test[^1]).Length >= 6.0) test.Add(q);
            return !RoadNetwork.HasSelfLoop(test);
        }

        try
        {
            var tail = FindPath(foot, footDir, footH, Math.Max(spec.MaxPath, 3000));
            if (CombinedOk(tail)) return tail;

            tail = FindPath(foot, footDir, footH, Math.Max(spec.MaxPath, w.Size), forcedVolcano: true);
            if (CombinedOk(tail)) return tail;

            // Stage 3: allow deeper cut/fill slack (8 m) at a steep volcano-foot ridge while keeping ALL geometry guards active
            tail = FindPath(foot, footDir, footH, Math.Max(spec.MaxPath, w.Size), forcedVolcano: true, startSlackOverride: 8.5);
            if (CombinedOk(tail)) return tail;

            // Stage 4: if foot + Lead lies on a blocked/water cell, probe strictly FORWARD (±28° max, NEVER backward U-turn!)
            Vector Rot(Vector v, double deg)
            {
                double r = deg * Math.PI / 180.0, c = Math.Cos(r), s = Math.Sin(r);
                return new Vector(v.X * c - v.Y * s, v.X * s + v.Y * c);
            }
            foreach (var dir in new[] { footDir, Rot(footDir, 28), Rot(footDir, -28) })
            {
                foreach (double step in new[] { 12.0, 24.0 })
                {
                    var probe = foot + dir * step;
                    if (!w.Contains((int)Math.Round(probe.X), (int)Math.Round(probe.Y))) continue;
                    double ph = w.PreDamageHeightAt(probe.X, probe.Y, spec.BreakInWasteland);
                    tail = FindPath(probe, dir, ph, Math.Max(spec.MaxPath, w.Size), forcedVolcano: true, startSlackOverride: 8.5);
                    if (tail != null && tail.Points.Count >= 2)
                    {
                        tail.Points.Insert(0, foot);
                        tail.Length = PolyLength(tail.Points);
                        if (CombinedOk(tail)) return tail;
                    }
                }
            }
            return null;
        }
        finally
        {
            foreach (int g in blockedCells) block[g] &= 1;
        }
    }

    /// Append tail to spiralPts/fixedH and smooth the seam (±36 m around the volcano foot) using YAMEE's RoadLine.Spline
    /// so the transition from the prepared spiral onto the connecting road is completely kink-free.
    void AppendSmoothTail(List<Point> pts, List<double?> fixedH, WildPath tail)
    {
        int seamIdx = pts.Count - 1;
        foreach (var q in tail.Points.Skip(1))
            if ((q - pts[^1]).Length >= 2.0)
            {
                pts.Add(q);
                fixedH?.Add(null);
            }

        // Resample & spline-smooth only the ±36 m window around seamIdx (leaving the prepared spiral & far road junction untouched!)
        int winStart = seamIdx;
        double back = 0;
        while (winStart > 2 && back < 36.0) { back += (pts[winStart] - pts[winStart - 1]).Length; winStart--; }
        int winEnd = seamIdx;
        double fwd = 0;
        while (winEnd < pts.Count - 2 && fwd < 36.0) { fwd += (pts[winEnd + 1] - pts[winEnd]).Length; winEnd++; }
        if (winEnd - winStart < 3) return;

        var sub = pts.GetRange(winStart, winEnd - winStart + 1);
        var dense = NetworkBuilder.Even(sub, 4.0);
        if (dense.Count < 8) return;
        var fix = new bool[dense.Count];
        fix[0] = fix[1] = fix[^2] = fix[^1] = true; // pin both ends + tangents!
        bool Blocked(int _, Point q)
        {
            int c = CellOf(q.X, q.Y);
            if (c < 0 || water[c] || (block[c] != 0 && !target[c])) return true;
            return cityTiles.Count > 0 && !target[c] && TooCloseToTile(q.X, q.Y, w.PreDamageHeightAt(q.X, q.Y, spec.BreakInWasteland), spec.PathWidth / 2.0 + spec.Verge + 1.0);
        }
        var splined = RoadLine.Spline(dense, fix, 1200.0, _ => 7.0, Blocked);
        if (splined == null) return;

        var replacement = NetworkBuilder.Even(splined.ToList(), 10.0);
        // Preserve fixed heights on the spiral side before seam, release them across the smoothed transition
        pts.RemoveRange(winStart, winEnd - winStart + 1);
        pts.InsertRange(winStart, replacement);
        if (fixedH != null)
        {
            fixedH.RemoveRange(winStart, winEnd - winStart + 1);
            for (int k = 0; k < replacement.Count; k++) fixedH.Insert(winStart + k, null);
        }
    }

    /// Called after all other wilderness POIs & paths are planned: if the volcano spiral hadn't connected yet
    /// (e.g. a nearby wilderness path or distant road wasn't reachable initially), connect it now via FindPath
    /// and keep plan.Paths build order valid!
    void EnsureVolcanoConnected()
    {
        if (volcanoSpiralPath == null || volcanoSpiralConnected || volcanoSiteRef == null || volcanoSpiralPath.Points.Count < 2)
            return;

        // Collect the entire transitive subtree of paths rooted at volcanoSpiralPath (including Pad 1's branch AND
        // any later wilderness POIs that connected onto the volcano spiral!) so FindPath connects strictly to the
        // EXTERNAL road network instead of looping back onto its own child POI!
        var volcanoTree = new HashSet<WildPath> { volcanoSpiralPath };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var p in plan.Paths)
                if (p.Target != null && volcanoTree.Contains(p.Target) && volcanoTree.Add(p)) grew = true;
        }
        var volcanoIds = new HashSet<int>();
        for (int i = 0; i < plan.Paths.Count; i++)
            if (volcanoTree.Contains(plan.Paths[i])) volcanoIds.Add(i + 1);

        // Temporarily clear all volcano-subtree cells from target[] so FindPath connects to an EXTERNAL road or path!
        var savedCells = new List<int>();
        for (int g = 0; g < target.Length; g++)
        {
            if (target[g] && volcanoIds.Contains(pathOf[g]))
            {
                target[g] = false;
                savedCells.Add(g);
            }
        }
        bool anyExternalTarget = false;
        for (int g = 0; g < target.Length; g++) if (target[g]) { anyExternalTarget = true; break; }

        if (anyExternalTarget)
        {
            dist = GroundShaper.Distance(target, gw, gw);
            var pts = volcanoSpiralPath.Points;
            var fixedH = volcanoSpiralPath.FixedHeights;
            var foot = pts[^1];
            Vector footDir = pts[^1] - pts[^2];
            if (footDir.Length > 1e-6) footDir.Normalize();
            else
            {
                footDir = new Vector(foot.X - volcanoSiteRef.CenterX, foot.Y - volcanoSiteRef.CenterZ);
                if (footDir.Length > 1e-6) footDir.Normalize(); else footDir = new Vector(1, 0);
            }
            double footH = w.PreDamageHeightAt(foot.X, foot.Y, spec.BreakInWasteland);
            var tail = FindVolcanoTail(volcanoSiteRef, pts, foot, footDir, footH);
            if (tail != null && tail.Points.Count >= 2)
            {
                AppendSmoothTail(pts, fixedH, tail);
                volcanoSpiralPath.Target = tail.Target;
                volcanoSpiralPath.EndKind = tail.EndKind;
                volcanoSpiralPath.EndDir = tail.EndDir;
                volcanoSpiralPath.EndHalf = tail.EndHalf;
                volcanoSpiralPath.Length = PolyLength(pts);
                volcanoSpiralPath.Bridges.AddRange(tail.Bridges);
                UseBridges(tail);
                volcanoSpiralConnected = true;

                // If we connected to a later-planned wilderness path, move the entire volcano subtree (preserving relative order)
                // right AFTER that target in plan.Paths so Build always builds target -> spiral -> children!
                if (tail.Target != null)
                {
                    int targetIdx = plan.Paths.IndexOf(tail.Target);
                    int spIdx = plan.Paths.IndexOf(volcanoSpiralPath);
                    if (targetIdx > spIdx && spIdx >= 0)
                    {
                        var moved = plan.Paths.Where(p => volcanoTree.Contains(p)).ToList();
                        foreach (var p in moved) plan.Paths.Remove(p);
                        int insertAt = plan.Paths.IndexOf(tail.Target) + 1;
                        plan.Paths.InsertRange(insertAt, moved);
                    }
                }
            }
        }

        foreach (int g in savedCells) target[g] = true;
        dist = GroundShaper.Distance(target, gw, gw);
    }

    PlacedPrefab TryPlaceOnVolcanoPad(
        VolcanoPad pad,
        List<(string Name, PrefabDef Def, (int[] Biomes, double Bias, double Weight) Rule)> burntPool,
        HashSet<string> usedNames,
        out Point bestExit,
        out Vector bestOut)
    {
        bestExit = default;
        bestOut = default;
        // prefer prefabs whose half-diagonal fits comfortably inside CoreFlatR (~29 m) + 6 m (diagonal <= 70 m -> up to ~50x50 m)
        double idealDiag = Math.Min(pad.Radius * 2.05, 70.0);
        double maxDiag = pad.Radius * 2.15;
        double maxSide = pad.Radius * 2.0;

        var bands = new[]
        {
            burntPool.Where(c => !usedNames.Contains(c.Name)
                && Math.Max(c.Def.SizeX, c.Def.SizeZ) is >= 32 and <= 54
                && Math.Min(c.Def.SizeX, c.Def.SizeZ) >= 24
                && Math.Sqrt(c.Def.SizeX * c.Def.SizeX + c.Def.SizeZ * c.Def.SizeZ) <= idealDiag).ToList(),
            burntPool.Where(c => !usedNames.Contains(c.Name)
                && Math.Max(c.Def.SizeX, c.Def.SizeZ) is >= 24 and <= 64
                && Math.Sqrt(c.Def.SizeX * c.Def.SizeX + c.Def.SizeZ * c.Def.SizeZ) <= maxDiag).ToList(),
            burntPool.Where(c => !usedNames.Contains(c.Name)
                && c.Def.SizeX <= maxSide && c.Def.SizeZ <= maxSide).ToList(),
        };

        var wantDir = new Vector(Math.Cos(pad.ExitAngle), Math.Sin(pad.ExitAngle));

        foreach (var band in bands)
        {
            if (band.Count == 0) continue;
            var remaining = new List<(string Name, PrefabDef Def, (int[] Biomes, double Bias, double Weight) Rule)>(band);
            for (int attempt = 0; attempt < 30 && remaining.Count > 0; attempt++)
            {
                var pick = Weighted(remaining);
                remaining.Remove(pick);

                PlacedPrefab bestP = null;
                double bestDot = double.NegativeInfinity;
                Point bExit = default;
                Vector bOut = default;

                for (int rot = 0; rot < 4; rot++)
                {
                    var p = new PlacedPrefab { Name = pick.Name, Def = pick.Def, Rotation = rot };
                    p.X = (int)Math.Round(pad.X - p.SizeX / 2.0);
                    p.Z = (int)Math.Round(pad.Z - p.SizeZ / 2.0);
                    if (p.X < -half + 40 || p.Z < -half + 40 || p.X + p.SizeX > half - 40 || p.Z + p.SizeZ > half - 40) continue;
                    if (AnyRect(p.X, p.Z, p.SizeX, p.SizeZ, 4, g => water[g] || building[g] || tile[g])) continue;

                    var parts = PrefabParts.For(p, partDef, null);
                    var exits = ExitsOf(p, parts);
                    if (exits.Count == 0) continue;

                    // Choose integer Y so the game's stamped TerrainHeight ((Y - YOffset - 1) * 257/256) matches pad.Height closely
                    int blockN = (int)Math.Round(pad.Height * 256.0 / 257.0);
                    p.Y = blockN + 1 + pick.Def.YOffset;

                    foreach (var ex in exits)
                    {
                        double dot = ex.Out.X * wantDir.X + ex.Out.Y * wantDir.Y;
                        if (dot > bestDot)
                        {
                            bestDot = dot;
                            bestP = p;
                            bExit = ex.E;
                            bOut = ex.Out;
                        }
                    }
                }

                if (bestP != null)
                {
                    bestExit = bExit;
                    bestOut = bOut;
                    return bestP;
                }
            }
        }
        return null;
    }

    /// Check if an existing road surface (asphalt or gravel from prior steps) lies within clearRadius of (x, z).
    bool FindExistingRoadNear(Point p, double clearRadius, out Point hit)
    {
        int r = (int)Math.Ceiling(clearRadius);
        int cx = (int)Math.Round(p.X), cz = (int)Math.Round(p.Y);
        double bestD2 = double.MaxValue;
        hit = default;
        for (int dz = -r; dz <= r; dz += 2)
            for (int dx = -r; dx <= r; dx += 2)
            {
                double d2 = dx * dx + dz * dz;
                if (d2 > clearRadius * clearRadius || d2 >= bestD2) continue;
                int wx = cx + dx, wz = cz + dz;
                if (w.Contains(wx, wz) && w.RoadAt(wx, wz) != 0)
                {
                    bestD2 = d2;
                    hit = new Point(wx, wz);
                }
            }
        return bestD2 < double.MaxValue;
    }

    WildPath BuildVolcanoAccessPath(VolcanoSite site, Point exitPt, Vector exitOut, double startH, out bool connected)
    {
        connected = false;
        var pts = new List<Point> { exitPt, exitPt + exitOut * Lead };
        var fixedH = new List<double?> { null, null };
        double contactRadius = VolcanoRoadWidth / 2.0 + spec.Verge + 2.5;

        if (site.AccessPath.Count >= 2)
        {
            var rampPts = site.AccessPath.Select(q => new Point(q.X, q.Z)).ToList();
            var rampH = site.AccessPath.Select(q => q.H).ToList();
            var lead = pts[^1];
            int startIdx = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < Math.Min(4, rampPts.Count); i++)
            {
                double d = (rampPts[i] - lead).Length;
                if (d < bestD) { bestD = d; startIdx = i; }
            }

            // Walk down the spiral; stop immediately at the FIRST contact with any pre-existing road so we NEVER run alongside/over asphalt!
            for (int i = startIdx; i < rampPts.Count; i++)
            {
                var prev = pts[^1];
                var next = rampPts[i];
                double segLen = (next - prev).Length;
                if (segLen < 3.0) continue;

                int subSteps = Math.Max(1, (int)Math.Ceiling(segLen / 3.0));
                for (int s = 1; s <= subSteps; s++)
                {
                    double frac = (double)s / subSteps;
                    var sample = new Point(prev.X + (next.X - prev.X) * frac, prev.Y + (next.Y - prev.Y) * frac);
                    if (!FindExistingRoadNear(sample, contactRadius, out var hit)) continue;

                    var spot = RoadFollower.Center(scan, hit);
                    Point endPt = spot != null ? spot.Center : hit;
                    while (pts.Count > 2 && (pts[^1] - endPt).Length < 7.0)
                    {
                        pts.RemoveAt(pts.Count - 1);
                        fixedH.RemoveAt(fixedH.Count - 1);
                    }
                    pts.Add(endPt);
                    fixedH.Add(null);

                    Vector fallbackDir = pts[^1] - pts[^2];
                    if (fallbackDir.Length > 1e-6) fallbackDir.Normalize(); else fallbackDir = exitOut;
                    byte hitKind = w.RoadAt((int)Math.Round(hit.X), (int)Math.Round(hit.Y));

                    connected = true;
                    return new WildPath
                    {
                        Points = pts,
                        FixedHeights = fixedH,
                        Width = VolcanoRoadWidth,
                        IsVolcano = true,
                        IsVolcanoSpiral = true,
                        Length = PolyLength(pts),
                        EndKind = spot != null ? spot.Kind : hitKind,
                        EndDir = spot != null ? spot.Dir : fallbackDir,
                        EndHalf = spot != null ? spot.Width / 2.0 : (hitKind == WorldData.Asphalt ? 9.0 : 3.0)
                    };
                }

                pts.Add(next);
                fixedH.Add(rampH[i]);
            }
        }

        // Connect from the volcano foot to the nearest existing road/path using YAMEE's multi-stage FindPath tool!
        var foot = pts[^1];
        Vector footDir = pts.Count >= 2 ? pts[^1] - pts[^2] : exitOut;
        if (footDir.Length > 1e-6) footDir.Normalize(); else footDir = exitOut;
        double footH = w.PreDamageHeightAt(foot.X, foot.Y, spec.BreakInWasteland);

        var tail = FindVolcanoTail(site, pts, foot, footDir, footH);
        if (tail != null && tail.Points.Count >= 2)
        {
            AppendSmoothTail(pts, fixedH, tail);
            tail.Points = pts;
            tail.FixedHeights = fixedH;
            tail.Width = VolcanoRoadWidth;
            tail.IsVolcano = true;
            tail.IsVolcanoSpiral = true;
            tail.Length = PolyLength(pts);
            connected = true;
            return tail;
        }

        if (pts.Count < 2) return null;
        return new WildPath
        {
            Points = pts,
            FixedHeights = fixedH,
            Width = VolcanoRoadWidth,
            IsVolcano = true,
            IsVolcanoSpiral = true,
            Length = PolyLength(pts),
            EndKind = WorldData.Gravel,
            EndDir = footDir,
            EndHalf = VolcanoRoadWidth / 2.0
        };
    }

    /// Smooth sweeping tangential driveway from Pad 1 (mid-height) onto the spiral's flat landing podest.
    /// Leaves the exit point unpinned so RoadPlanner connects at exact StartPoi.TerrainHeight (+ 4 m flat entrance),
    /// then smoothly transitions along the curved driveway to the spiral podest height.
    WildPath BuildVolcanoRimPath(VolcanoSite site, VolcanoPad pad, Point exitPt, Vector exitOut, double poiTerrainH, WildPath rampPath)
    {
        var pts = new List<Point> { exitPt };
        var fixedH = new List<double?> { null };

        if (site.RimConnector.Count >= 2)
        {
            var rimPts = site.RimConnector.Select(q => new Point(q.X, q.Z)).ToList();
            var rimH = site.RimConnector.Select(q => q.H ?? pad.Height).ToList();
            // straight lead out of the building exit along the flat forecourt before curving onto the driveway
            var firstBranch = rimPts[0];
            double dFirst = (firstBranch - exitPt).Length;
            if (dFirst > 4.5)
            {
                var leadPt = exitPt + exitOut * Math.Min(Lead, dFirst * 0.45);
                pts.Add(leadPt);
                fixedH.Add(poiTerrainH);
            }
            for (int i = 0; i < rimPts.Count; i++)
                if ((rimPts[i] - pts[^1]).Length >= 2.5)
                {
                    pts.Add(rimPts[i]);
                    fixedH.Add(rimH[i]);
                }
            var join = Nearest(rampPath.Points, pts[^1]);
            if ((join - pts[^1]).Length >= 4.0) { pts.Add(join); fixedH.Add(pad.Height); }
            else { pts[^1] = join; fixedH[^1] = pad.Height; }

            // Guard for existing maps (e.g. Habegu-Land) whose persisted RimConnector grazed the spiral at < 45°:
            // if the approach angle at join is < 44° or an interior point hugs the spiral within < 5.5 m, rebuild
            // a clean curved Bezier arriving at ~58° onto the spiral (obeying ArrivalOk >= 45°!).
            if (ShouldRebuildShallowRimBranch(pts, rampPath.Points, join, out var st))
            {
                var bestJoin = BestHeightMatchedJoin(rampPath.Points, join, poiTerrainH, 14.0);
                var inVec = new Vector(-st.Y, st.X);
                if (inVec.X * (exitPt.X - bestJoin.X) + inVec.Y * (exitPt.Y - bestJoin.Y) < 0) inVec = -inVec;
                double lat = Math.Max(10.0, (exitPt.X - bestJoin.X) * inVec.X + (exitPt.Y - bestJoin.Y) * inVec.Y);
                double along = (exitPt.X - bestJoin.X) * st.X + (exitPt.Y - bestJoin.Y) * st.Y;
                double tSign = along >= 0 ? 1.0 : -1.0;
                var ctrl = new Point(
                    bestJoin.X + inVec.X * Math.Clamp(lat * 0.55, 10.5, 16.0) + st.X * tSign * Math.Min(7.0, Math.Abs(along) * 0.4),
                    bestJoin.Y + inVec.Y * Math.Clamp(lat * 0.55, 10.5, 16.0) + st.Y * tSign * Math.Min(7.0, Math.Abs(along) * 0.4));
                var lead = exitPt + exitOut * Math.Min(Lead, Math.Max(4.0, (ctrl - exitPt).Length * 0.35));

                pts.Clear();
                fixedH.Clear();
                pts.Add(exitPt); fixedH.Add(null);
                if ((lead - exitPt).Length >= 3.0) { pts.Add(lead); fixedH.Add(poiTerrainH); }
                const int Steps = 7;
                var p0 = pts[^1];
                for (int b = 1; b <= Steps; b++)
                {
                    double t = (double)b / Steps, om = 1.0 - t;
                    var q = new Point(
                        om * om * p0.X + 2 * om * t * ctrl.X + t * t * bestJoin.X,
                        om * om * p0.Y + 2 * om * t * ctrl.Y + t * t * bestJoin.Y);
                    if ((q - pts[^1]).Length >= 2.5 || b == Steps) { pts.Add(q); fixedH.Add(pad.Height); }
                }
                pts[^1] = bestJoin;
                fixedH[^1] = pad.Height;
            }
        }
        else
        {
            var join = Nearest(rampPath.Points, exitPt);
            pts.Add(exitPt + exitOut * Math.Min(Lead, (join - exitPt).Length * 0.35));
            fixedH.Add(poiTerrainH);
            pts.Add(new Point((exitPt.X + join.X) * 0.5, (exitPt.Y + join.Y) * 0.5));
            fixedH.Add((poiTerrainH + pad.Height) * 0.5);
            pts.Add(join);
            fixedH.Add(pad.Height);
        }

        // Smooth linear ramp from exact poiTerrainH (after the 4 m flat RoadPlanner entrance) to target podest height
        double totalLen = PolyLength(pts);
        if (totalLen > 1e-3 && fixedH[^1] is double endH)
        {
            double walk = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                walk += (pts[i] - pts[i - 1]).Length;
                double t = Math.Clamp((walk - 4.0) / Math.Max(4.0, totalLen - 4.0), 0, 1);
                fixedH[i] = poiTerrainH + (endH - poiTerrainH) * t;
            }
        }

        return new WildPath
        {
            Points = pts,
            FixedHeights = fixedH,
            Target = rampPath,
            EndKind = 0,
            Width = VolcanoRoadWidth,
            IsVolcano = true,
            IsVolcanoSpiral = false,
            Length = PolyLength(pts)
        };
    }

    /// Check whether a prepared RimConnector approaches rampPts too shallowly (< 44°) or grazes parallel within 5.5 m.
    static bool ShouldRebuildShallowRimBranch(List<Point> branchPts, List<Point> rampPts, Point join, out Vector spiralTangent)
    {
        spiralTangent = new Vector(1, 0);
        if (branchPts.Count < 2 || rampPts.Count < 2) return false;

        // Local spiral unit tangent at join
        int bestIdx = 0;
        double bestD2 = double.MaxValue;
        for (int i = 0; i < rampPts.Count; i++)
        {
            double d2 = (rampPts[i] - join).LengthSquared;
            if (d2 < bestD2) { bestD2 = d2; bestIdx = i; }
        }
        var rPrev = rampPts[Math.Max(0, bestIdx - 1)];
        var rNext = rampPts[Math.Min(rampPts.Count - 1, bestIdx + 1)];
        var tv = rNext - rPrev;
        if (tv.Length > 1e-4) { tv.Normalize(); spiralTangent = tv; }

        // Measure approach direction ~10 m back along branchPts from join
        Point backPt = branchPts[0];
        double walk = 0;
        for (int i = branchPts.Count - 1; i >= 1; i--)
        {
            double seg = (branchPts[i] - branchPts[i - 1]).Length;
            if (walk + seg >= 10.0)
            {
                double t = seg > 1e-6 ? (10.0 - walk) / seg : 0;
                backPt = branchPts[i] + (branchPts[i - 1] - branchPts[i]) * t;
                break;
            }
            walk += seg;
            backPt = branchPts[i - 1];
        }
        var bv = join - backPt;
        if (bv.Length > 1e-4)
        {
            bv.Normalize();
            double angleDeg = Math.Acos(Math.Clamp(Math.Abs(bv.X * spiralTangent.X + bv.Y * spiralTangent.Y), 0, 1)) * 180.0 / Math.PI;
            if (angleDeg < 44.0) return true;
        }

        // Also check whether any interior point 6..22 m before join hugs rampPts closer than 5.5 m
        walk = 0;
        for (int i = branchPts.Count - 2; i >= 1; i--)
        {
            walk += (branchPts[i + 1] - branchPts[i]).Length;
            if (walk < 6.0) continue;
            if (walk > 22.0) break;
            var near = Nearest(rampPts, branchPts[i]);
            if ((near - branchPts[i]).Length < 5.5) return true;
        }
        return false;
    }

    /// Find the point on rampPts within ±maxShift of baseJoin whose pre-damage ground height best matches targetH.
    Point BestHeightMatchedJoin(List<Point> rampPts, Point baseJoin, double targetH, double maxShift)
    {
        Point best = baseJoin;
        double bestDiff = Math.Abs(w.PreDamageHeightAt(baseJoin.X, baseJoin.Y, spec.BreakInWasteland) - targetH);
        for (int i = 1; i < rampPts.Count; i++)
        {
            var a = rampPts[i - 1]; var ab = rampPts[i] - a;
            double len = ab.Length;
            if (len < 1e-4) continue;
            int steps = Math.Max(1, (int)Math.Ceiling(len / 2.0));
            for (int s = 0; s <= steps; s++)
            {
                var q = a + ab * ((double)s / steps);
                if ((q - baseJoin).Length > maxShift) continue;
                double diff = Math.Abs(w.PreDamageHeightAt(q.X, q.Y, spec.BreakInWasteland) - targetH);
                if (diff + 0.05 < bestDiff) { bestDiff = diff; best = q; }
            }
        }
        return best;
    }

    /// Post-path volcano POI re-levelling: restores exact building TerrainHeight under the footprint and a short 8 m collar
    /// while strictly sparing EVERY painted road plus a smooth 3.5..9.5 m road-edge fade so the spiral road's verge and
    /// junction embankment are NEVER undercut by a vertical ledge!
    static int RelevelVolcanoPoiSafe(WorldData w, WorldEdits e, WildPoi poi)
    {
        static double Smooth(double a, double b, double v)
        {
            double t = Math.Clamp((v - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        var p = poi.Building;
        double poiH = p.Def != null ? p.TerrainHeight : p.GroundHeight;
        var rects = new List<PlacedPrefab> { p };
        foreach (var q in poi.Parts)
            if (q.X < p.X || q.Z < p.Z || q.X + q.SizeX > p.X + p.SizeX || q.Z + q.SizeZ > p.Z + p.SizeZ)
                rects.Add(q);

        const int Collar = 8;
        const int RoadPad = 12;
        int rx0 = rects.Min(r => r.X), rz0 = rects.Min(r => r.Z);
        int rx1 = rects.Max(r => r.X + r.SizeX), rz1 = rects.Max(r => r.Z + r.SizeZ);
        int gx0 = Math.Max(-w.Half, rx0 - Collar - RoadPad), gz0 = Math.Max(-w.Half, rz0 - Collar - RoadPad);
        int gx1 = Math.Min(w.Half - 1, rx1 + Collar + RoadPad), gz1 = Math.Min(w.Half - 1, rz1 + Collar + RoadPad);
        int gw = gx1 - gx0 + 1, gh = gz1 - gz0 + 1;
        if (gw <= 0 || gh <= 0) return 0;

        var roadSeed = new bool[gw * gh];
        bool anyRoad = false;
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
                if (w.RoadAt(gx0 + gx, gz0 + gz) != 0) { roadSeed[gz * gw + gx] = true; anyRoad = true; }
        float[] roadDist = anyRoad ? GroundShaper.Distance(roadSeed, gw, gh) : null;

        int x0 = Math.Max(gx0, rx0 - Collar), x1 = Math.Min(gx1, rx1 + Collar);
        int z0 = Math.Max(gz0, rz0 - Collar), z1 = Math.Min(gz1, rz1 + Collar);
        int changed = 0;
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                if (!w.Contains(x, z) || w.WaterAt(x, z)) continue;
                int hi = w.Index(x, z);
                if (w.RoadMask[hi] != 0) continue;

                double cx = x + 0.5, cz = z + 0.5;
                double d = double.MaxValue;
                foreach (var r in rects)
                {
                    double dx = Math.Max(Math.Max(r.X - cx, cx - (r.X + r.SizeX)), 0);
                    double dz = Math.Max(Math.Max(r.Z - cz, cz - (r.Z + r.SizeZ)), 0);
                    double dr = Math.Sqrt(dx * dx + dz * dz);
                    if (dr < d) d = dr;
                }
                if (d >= Collar) continue;

                double wFoot = d == 0 ? 1.0 : 1.0 - Smooth(0.0, Collar, d);
                if (roadDist != null)
                {
                    float rd = roadDist[(z - gz0) * gw + (x - gx0)];
                    if (d > 0)
                    {
                        if (rd <= 3.5f) continue;
                        wFoot *= Smooth(3.5, 9.5, rd);
                    }
                    else if (rd <= 1.5f) continue;
                }
                if (wFoot <= 0.001) continue;

                double cur = w.Heights[hi] / 256.0;
                double nh = cur + (poiH - cur) * wFoot;
                var hv = (ushort)Math.Clamp(Math.Round(nh * 256.0), 0, 65535);
                if (hv != w.Heights[hi]) { e.SetHeight(hi, hv); changed++; }
            }
        return changed;
    }

    /// Smooth the V-fork pocket & main-road verge on the branch side of Pad 1's volcano junction so NO protruding
    /// rock tooth or sharp vertical cliff can remain right beside the main spiral road edge (even on existing maps!).
    static void SmoothVolcanoFork(WorldData w, WorldEdits e, Point c, Vector branchInDir, (byte[] Mask, ushort[] Heights) snap)
    {
        static double Smooth(double a, double b, double v)
        {
            double t = Math.Clamp((v - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        int r = JunctionWindow, s = 2 * r + 1;
        int x0 = (int)Math.Floor(c.X) - r, z0 = (int)Math.Floor(c.Y) - r;
        var dOld = new float[s * s];
        var srcOld = new int[s * s];
        var dNew = new float[s * s];
        var srcNew = new int[s * s];
        for (int g = 0; g < s * s; g++)
        {
            int wx = x0 + g % s, wz = z0 + g / s;
            bool isOld = snap.Mask[g] != 0;
            bool isNew = !isOld && w.Contains(wx, wz) && w.RoadAt(wx, wz) != 0;
            dOld[g] = isOld ? 0 : 1e9f; srcOld[g] = isOld ? g : -1;
            dNew[g] = isNew ? 0 : 1e9f; srcNew[g] = isNew ? g : -1;
        }
        static void Chamfer(float[] d, int[] src, int s)
        {
            void Relax(int g, int q, float step) { if (q < 0 || src[q] < 0) return; if (d[q] + step < d[g]) { d[g] = d[q] + step; src[g] = src[q]; } }
            for (int z = 0; z < s; z++)
                for (int x = 0; x < s; x++)
                {
                    int g = z * s + x;
                    if (x > 0) Relax(g, g - 1, 1);
                    if (z > 0) { Relax(g, g - s, 1); if (x > 0) Relax(g, g - s - 1, 1.414f); if (x < s - 1) Relax(g, g - s + 1, 1.414f); }
                }
            for (int z = s - 1; z >= 0; z--)
                for (int x = s - 1; x >= 0; x--)
                {
                    int g = z * s + x;
                    if (x < s - 1) Relax(g, g + 1, 1);
                    if (z < s - 1) { Relax(g, g + s, 1); if (x < s - 1) Relax(g, g + s + 1, 1.414f); if (x > 0) Relax(g, g + s - 1, 1.414f); }
                }
        }
        Chamfer(dOld, srcOld, s);
        Chamfer(dNew, srcNew, s);

        for (int z = 0; z < s; z++)
            for (int x = 0; x < s; x++)
            {
                int g = z * s + x;
                int wx = x0 + x, wz = z0 + z;
                if (!w.Contains(wx, wz) || w.RoadAt(wx, wz) != 0 || w.WaterAt(wx, wz)) continue;
                if (srcOld[g] < 0 || srcNew[g] < 0) continue;
                float do_ = dOld[g], dn = dNew[g];
                if (do_ > 11.0f || dn > 15.0f) continue;

                // Stay strictly on the branch side of the main road (never alter the valley drop-off opposite the junction!)
                double sideDot = (wx + 0.5 - c.X) * branchInDir.X + (wz + 0.5 - c.Y) * branchInDir.Y;
                if (sideDot < -2.0) continue;
                double sideFade = Smooth(-2.0, 2.5, sideDot);

                int qo = srcOld[g], qn = srcNew[g];
                double hOld = w.HeightAt(x0 + qo % s, z0 + qo / s);
                double hNew = w.HeightAt(x0 + qn % s, z0 + qn / s);
                double wo = 1.0 / (do_ + 0.4), wn = 1.0 / (dn + 0.4);
                double hFork = (hOld * wo + hNew * wn) / (wo + wn);

                double wPocket = (1.0 - Smooth(2.0, 11.0, do_)) * (1.0 - Smooth(2.5, 15.0, dn)) * sideFade;
                if (wPocket <= 0.001) continue;

                double cur = w.HeightAt(wx, wz);
                double h = cur + (hFork - cur) * wPocket;
                var hv = (ushort)Math.Clamp(Math.Round(h * 256.0), 0, 65535);
                int hi = w.Index(wx, wz);
                if (hv != w.Heights[hi]) e.SetHeight(hi, hv);
            }
    }

    /// Interpolate fixed prepared heights onto the resampled 10 m control points passed to RoadPlanner.
    /// Always leaves control point 0 (the POI exit) unpinned so RoadPlanner's built-in StartPoi.TerrainHeight + 4 m flat
    /// entrance takes effect; for the main spiral leaves 16 m at Pad 0 and 22 m at foot/junction; for Pad 1's branch
    /// interpolates smoothly toward the actual built spiral surface height (targetEndH) and leaves 8 m at the junction
    /// so RoadPlanner's junction fixes + cross-slope blend flush without any curb or bump.
    static void ApplyFixedSpiralHeights(List<Point> rawPts, List<double?> rawH, List<Point> evenPts, double?[] outHeights, bool isSpiral, double? targetEndH = null)
    {
        var rawDist = new double[rawPts.Count];
        for (int i = 1; i < rawPts.Count; i++) rawDist[i] = rawDist[i - 1] + (rawPts[i] - rawPts[i - 1]).Length;
        double totalRaw = rawDist[^1];
        if (totalRaw < 8) return;

        int lastFixedIdx = -1;
        for (int i = rawH.Count - 1; i >= 0; i--) if (rawH[i].HasValue) { lastFixedIdx = i; break; }
        if (lastFixedIdx <= 0) return;
        double spiralEndDist = rawDist[lastFixedIdx];

        double skipStart = isSpiral ? 16.0 : 6.0;
        double skipEnd = isSpiral ? 22.0 : 8.0;
        double? branchStartH = !isSpiral ? (rawH.Count > 1 && rawH[1].HasValue ? rawH[1] : rawH[lastFixedIdx]) : null;

        double walk = 0;
        for (int k = 0; k < evenPts.Count; k++)
        {
            if (k > 0) walk += (evenPts[k] - evenPts[k - 1]).Length;
            if (k == 0 || walk < skipStart) continue;
            if (walk > totalRaw - skipEnd) continue;
            if (isSpiral && walk > spiralEndDist - 12.0) continue;

            if (!isSpiral && targetEndH is double exactEndH && branchStartH is double startH)
            {
                double t = Math.Clamp((walk - 4.0) / Math.Max(4.0, totalRaw - 4.0), 0, 1);
                outHeights[k] = startH + (exactEndH - startH) * t;
                continue;
            }

            int hi = 1;
            while (hi < rawDist.Length - 1 && rawDist[hi] < walk) hi++;
            int lo = hi - 1;
            if (rawH[lo] is double h0 && rawH[hi] is double h1)
            {
                double seg = Math.Max(1e-6, rawDist[hi] - rawDist[lo]);
                double t = Math.Clamp((walk - rawDist[lo]) / seg, 0, 1);
                outHeights[k] = h0 + (h1 - h0) * t;
            }
            else if (rawH[hi] is double hOnly && walk >= skipStart)
            {
                outHeights[k] = hOnly;
            }
        }
    }

    /// Test helper: applies explicit landslide definitions directly (used by unit tests).
    public static void ApplyVolcanoLandslidesForTest(WorldData w, WorldEdits e, VolcanoSite site) =>
        ApplyLandslideList(w, e, site.Landslides);

    /// Collect EVERY junction along the volcano spiral road (Pad 1's branch, every other wilderness POI or connector
    /// path joining/touching the spiral, and the spiral's own external foot junction) so landslides ALWAYS keep at least
    /// 4+ m of intact visible continuing gravel road past every junction (never making a T-junction look like a curve!).
    static List<(Point Pt, double ArcDist, double RoadHalf)> CollectVolcanoSpiralJunctions(
        WildPath spiralPath, RoadResult spiralRoad, List<(WildPath Path, RoadResult Road)> allBuilt)
    {
        var list = new List<(Point Pt, double ArcDist, double RoadHalf)>();
        var sp = spiralRoad?.Path;
        if (sp == null || sp.Count < 2) return list;

        (Point Proj, double ArcDist, double DistSq) ClosestOnSpiral(Point p)
        {
            double bestD2 = double.MaxValue, bestArc = 0;
            Point bestPt = new(sp[0].X, sp[0].Z);
            for (int i = 0; i < sp.Count; i++)
            {
                double dx = sp[i].X - p.X, dz = sp[i].Z - p.Y;
                double d2 = dx * dx + dz * dz;
                if (d2 < bestD2) { bestD2 = d2; bestArc = sp[i].Dist; bestPt = new(sp[i].X, sp[i].Z); }
            }
            return (bestPt, bestArc, bestD2);
        }

        void AddJunction(Point rawPt, double roadHalf)
        {
            var (proj, arc, _) = ClosestOnSpiral(rawPt);
            double hw = Math.Max(2.5, roadHalf);
            for (int i = 0; i < list.Count; i++)
            {
                double dx = list[i].Pt.X - proj.X, dz = list[i].Pt.Y - proj.Y;
                if (dx * dx + dz * dz <= 16.0)
                {
                    if (hw > list[i].RoadHalf) list[i] = (list[i].Pt, list[i].ArcDist, hw);
                    return;
                }
            }
            list.Add((proj, arc, hw));
        }

        if (allBuilt != null)
        {
            foreach (var (other, _) in allBuilt)
            {
                if (other == null || other == spiralPath || other.Points == null || other.Points.Count < 2) continue;
                double hw = Math.Max(2.5, other.Width * 0.5);
                if (other.Target == spiralPath) AddJunction(other.Points[^1], hw);
                if (other.StartTarget == spiralPath) AddJunction(other.Points[0], hw);

                // Catch-all: any other built path whose start or end meets the spiral within 16 m
                if (ClosestOnSpiral(other.Points[^1]).DistSq <= 16.0 * 16.0) AddJunction(other.Points[^1], hw);
                if (ClosestOnSpiral(other.Points[0]).DistSq <= 16.0 * 16.0) AddJunction(other.Points[0], hw);
            }
        }

        // Also guard the spiral's own external junction at the foot (if connected to another road or path)
        if (spiralPath != null && spiralPath.Points != null && spiralPath.Points.Count >= 2
            && (spiralPath.Target != null || spiralPath.EndKind != 0))
        {
            double footHalf = spiralPath.EndHalf > 0 ? spiralPath.EndHalf : spiralPath.Width * 0.5;
            AddJunction(spiralPath.Points[^1], Math.Max(2.5, footHalf));
        }

        return list;
    }

    /// Choose and apply ALWAYS 2 dramatic landslides (1× horseshoe stepped downhill chute + 1× shallow uphill breakaway
    /// niche with a long 50–80 m downhill debris tongue) directly onto the actually built volcano spiral road,
    /// placing BOTH immediately before/after the middle POI (Pad 1) so city tiles near the volcano foot can never cut off
    /// the downhill debris, while keeping >= 4+ m of intact visible gravel road past EVERY junction.
    static void ApplyVolcanoLandslides(
        WorldData w, WorldEdits e, VolcanoSite site, RoadResult spiralRoad,
        IReadOnlyList<(Point Pt, double ArcDist, double RoadHalf)> junctions, List<VolcanoLandslide> appliedOut,
        Point? midPoiJoin = null)
    {
        var chosen = SelectLandslidesOnBuiltRoad(w, site, spiralRoad, junctions, midPoiJoin);
        if (chosen.Count == 0 && site.Landslides.Count > 0) chosen = site.Landslides;
        ApplyLandslideList(w, e, chosen, junctions);
        appliedOut?.AddRange(chosen);
    }

    /// Select ALWAYS 2 well-separated landslide spots (1 downhill chute + 1 uphill debris slide) on the actual built
    /// spiral road, positioned immediately before and immediately after the middle POI (Pad 1) so the downhill debris
    /// stays high on the volcano flank and is never sliced off by a city tile near the foot.
    static List<VolcanoLandslide> SelectLandslidesOnBuiltRoad(
        WorldData w, VolcanoSite site, RoadResult spiralRoad,
        IReadOnlyList<(Point Pt, double ArcDist, double RoadHalf)> junctions,
        Point? midPoiJoin = null)
    {
        var result = new List<VolcanoLandslide>();
        var path = spiralRoad?.Path;
        if (path == null || path.Count < 40) return result;

        int seed = ((int)Math.Round(site.CenterX) * 73856093) ^ ((int)Math.Round(site.CenterZ) * 19349663) ^ 99173;
        var pr = new Random(seed);

        double totalDist = path[^1].Dist;

        double ClosestArcOnPath(double x, double z)
        {
            double bestD2 = double.MaxValue, bestDist = totalDist * 0.48;
            for (int i = 0; i < path.Count; i++)
            {
                double dx = path[i].X - x, dz = path[i].Z - z;
                double d2 = dx * dx + dz * dz;
                if (d2 < bestD2) { bestD2 = d2; bestDist = path[i].Dist; }
            }
            return bestDist;
        }

        // Locate the middle POI (Pad 1) junction along the built spiral road
        double midArc;
        if (midPoiJoin is Point mp)
            midArc = ClosestArcOnPath(mp.X, mp.Y);
        else if (site.RimConnector != null && site.RimConnector.Count >= 2)
            midArc = ClosestArcOnPath(site.RimConnector[^1].X, site.RimConnector[^1].Z);
        else if (site.Pads != null && site.Pads.Count >= 2)
            midArc = ClosestArcOnPath(site.Pads[1].X, site.Pads[1].Z);
        else if (site.PodestDist > 0)
            midArc = Math.Clamp(site.PodestDist, 0, totalDist);
        else
            midArc = totalDist * 0.48;

        // Pre-filter non-Part prefabs (buildings, city tiles, bridges) near the volcano for fast clearance checks
        double prefReach = site.OuterRadius + 280.0;
        var nearbyPrefabs = w.Prefabs.Where(pf => pf.Def != null && pf.Def.Kind != PrefabKind.Part
            && pf.X + pf.SizeX >= site.CenterX - prefReach && pf.X <= site.CenterX + prefReach
            && pf.Z + pf.SizeZ >= site.CenterZ - prefReach && pf.Z <= site.CenterZ + prefReach).ToList();

        bool NearAsphalt(double wx, double wz, int rad)
        {
            int cx = (int)Math.Round(wx), cz = (int)Math.Round(wz);
            for (int dz = -rad; dz <= rad; dz += 3)
                for (int dx = -rad; dx <= rad; dx += 3)
                {
                    int x = cx + dx, z = cz + dz;
                    if (w.Contains(x, z) && w.RoadAt(x, z) == WorldData.Asphalt) return true;
                }
            return false;
        }

        bool NearBuilding(double wx, double wz, double padMargin)
        {
            for (int pi = 0; pi < nearbyPrefabs.Count; pi++)
            {
                var pf = nearbyPrefabs[pi];
                if (wx >= pf.X - padMargin && wx <= pf.X + pf.SizeX + padMargin
                    && wz >= pf.Z - padMargin && wz <= pf.Z + pf.SizeZ + padMargin) return true;
            }
            return false;
        }

        bool ZoneHitsPrefab(double x, double z, double tx, double tz, double nx, double nz,
            double vMin, double vMax, double halfU, double margin)
        {
            for (double v = vMin; v <= vMax; v += 12.0)
                for (double u = -halfU; u <= halfU; u += 12.0)
                {
                    double px = x + tx * u + nx * v;
                    double pz = z + tz * u + nz * v;
                    for (int pi = 0; pi < nearbyPrefabs.Count; pi++)
                    {
                        var pf = nearbyPrefabs[pi];
                        if (px >= pf.X - margin && px <= pf.X + pf.SizeX + margin
                            && pz >= pf.Z - margin && pz <= pf.Z + pf.SizeZ + margin)
                            return true;
                    }
                }
            return false;
        }

        bool ZoneHitsRoad(double x, double z, double tx, double tz, double nx, double nz,
            double vMin, double vMax, double halfU)
        {
            for (double v = vMin; v <= vMax; v += 6.0)
                for (double u = -halfU; u <= halfU; u += 6.0)
                {
                    int wx = (int)Math.Round(x + tx * u + nx * v);
                    int wz = (int)Math.Round(z + tz * u + nz * v);
                    if (w.Contains(wx, wz) && w.RoadAt(wx, wz) != 0) return true;
                }
            return false;
        }

        double MinJunctionClearance(int idx)
        {
            double best = Math.Min(path[idx].Dist, totalDist - path[idx].Dist);
            if (junctions == null) return best;
            double sx = path[idx].X, sz = path[idx].Z, sd = path[idx].Dist;
            for (int ji = 0; ji < junctions.Count; ji++)
            {
                var j = junctions[ji];
                double dArc = Math.Abs(sd - j.ArcDist);
                double dEuc = Math.Sqrt((sx - j.Pt.X) * (sx - j.Pt.X) + (sz - j.Pt.Y) * (sz - j.Pt.Y));
                double edgeClear = Math.Min(dArc, dEuc) - (j.RoadHalf + 1.5);
                if (edgeClear < best) best = edgeClear;
            }
            return best;
        }

        // Score a single candidate index along the spiral road
        (int Idx, double Dist, double DistFromMid, bool IsBefore, double Tx, double Tz, double Nx, double Nz,
            double UpRise24, double UpSlope, double DownDrop24, double CliffScore, bool UpClear, bool DownClear, bool DownRoadClear) ScoreCandidate(int idx)
        {
            int p0 = Math.Max(0, idx - 4), p1 = Math.Min(path.Count - 1, idx + 4);
            double tx = path[p1].X - path[p0].X, tz = path[p1].Z - path[p0].Z;
            double tl = Math.Sqrt(tx * tx + tz * tz);
            if (tl < 1e-4) { tx = 1; tz = 0; } else { tx /= tl; tz /= tl; }
            double nx = -tz, nz = tx;
            if (nx * (path[idx].X - site.CenterX) + nz * (path[idx].Z - site.CenterZ) < 0) { nx = -nx; nz = -nz; }
            double hPlus = w.HeightAt((int)Math.Round(path[idx].X + nx * 12), (int)Math.Round(path[idx].Z + nz * 12));
            double hMinus = w.HeightAt((int)Math.Round(path[idx].X - nx * 12), (int)Math.Round(path[idx].Z - nz * 12));
            if (hPlus > hMinus + 1.5) { nx = -nx; nz = -nz; }

            double AvgAt(double vOff)
            {
                double sum = 0;
                int cnt = 0;
                for (int u = -6; u <= 6; u += 6)
                {
                    int wx = (int)Math.Round(path[idx].X + tx * u + nx * vOff);
                    int wz = (int)Math.Round(path[idx].Z + tz * u + nz * vOff);
                    if (!w.Contains(wx, wz)) continue;
                    sum += w.HeightAt(wx, wz);
                    cnt++;
                }
                return cnt > 0 ? sum / cnt : w.HeightAt((int)Math.Round(path[idx].X), (int)Math.Round(path[idx].Z));
            }

            double hRoad = AvgAt(0);
            double hUp6 = AvgAt(-6), hUp18 = AvgAt(-18), hUp24 = AvgAt(-24), hUp34 = AvgAt(-34);
            double hDown24 = AvgAt(24);
            double upRise24 = hUp24 - hRoad;
            double upRise34 = hUp34 - hRoad;
            double upSlope = Math.Max((hUp18 - hUp6) / 12.0, (hUp24 - hUp6) / 18.0);
            double downDrop24 = hRoad - hDown24;
            double cliffScore = Math.Min(upRise24, 26.0) * 1.3 + Math.Min(upRise34, 38.0) * 0.6 + upSlope * 20.0;

            double sx = path[idx].X, sz = path[idx].Z;
            bool upClear = !ZoneHitsPrefab(sx, sz, tx, tz, nx, nz, -62.0, -10.0, 18.0, 10.0)
                        && !ZoneHitsRoad(sx, sz, tx, tz, nx, nz, -48.0, -16.0, 16.0);
            bool downClear = !ZoneHitsPrefab(sx, sz, tx, tz, nx, nz, 10.0, 82.0, 24.0, 10.0);
            bool downRoadClear = !ZoneHitsRoad(sx, sz, tx, tz, nx, nz, 18.0, 48.0, 16.0);

            double d = path[idx].Dist;
            return (idx, d, Math.Abs(d - midArc), d < midArc, tx, tz, nx, nz,
                upRise24, upSlope, downDrop24, cliffScore, upClear, downClear, downRoadClear);
        }

        // Progressive passes anchored strictly around the middle POI (Pad 1):
        // Pass 0: immediately before/after Pad 1 (<= 110 m from midArc), strict clearances & inside inner 90% cone.
        // Pass 1: slightly wider around Pad 1 (<= 170 m, e.g. when an extra junction sits right next to Pad 1), strict clearances.
        // Pass 2: <= 170 m around Pad 1 with relaxed clearances (joinEdgeClear = 28 m -> still >= 5 m intact gravel via SafeLengthFor).
        // Pass 3: last-resort fallback across the cone if one side was completely blocked by roads or city tiles.
        var passConfigs = new (double MaxMidDist, double EndMargin, double PadClear, double JoinEdgeClear, double BldClear, int AsphaltClear, double ConeFrac)[]
        {
            (110.0, 42.0, 18.0, 40.0, 24.0, 26, 0.90),
            (170.0, 42.0, 18.0, 40.0, 24.0, 26, 0.92),
            (170.0, 30.0, 8.0,  28.0, 14.0, 16, 0.95),
            (double.MaxValue, 30.0, 8.0, 28.0, 14.0, 16, 1.00)
        };

        var seenIdx = new HashSet<int>();
        var scored = new List<(int Idx, double Dist, double DistFromMid, bool IsBefore, double Tx, double Tz, double Nx, double Nz,
            double UpRise24, double UpSlope, double DownDrop24, double CliffScore, bool UpClear, bool DownClear, bool DownRoadClear)>();

        bool HasGoodTwoSidedPair()
        {
            bool beforeClear = scored.Any(c => c.IsBefore && c.DownClear);
            bool afterClear = scored.Any(c => !c.IsBefore && c.DownClear);
            if (!beforeClear || !afterClear) return false;
            bool beforeCliff = scored.Any(c => c.IsBefore && c.DownClear && c.UpClear && c.UpRise24 >= 12.0 && c.UpSlope >= 0.50);
            bool afterCliff = scored.Any(c => !c.IsBefore && c.DownClear && c.UpClear && c.UpRise24 >= 12.0 && c.UpSlope >= 0.50);
            return beforeCliff || afterCliff;
        }

        for (int pass = 0; pass < passConfigs.Length; pass++)
        {
            var cfg = passConfigs[pass];
            double maxConeR = site.OuterRadius * cfg.ConeFrac;

            for (int i = 0; i < path.Count; i += 3)
            {
                if (seenIdx.Contains(i)) continue;
                var sp = path[i];
                if (Math.Abs(sp.Dist - midArc) > cfg.MaxMidDist) continue;
                if (sp.Dist < cfg.EndMargin || sp.Dist > totalDist - cfg.EndMargin) continue;
                if (cfg.ConeFrac < 1.0)
                {
                    double dCenter = Math.Sqrt((sp.X - site.CenterX) * (sp.X - site.CenterX) + (sp.Z - site.CenterZ) * (sp.Z - site.CenterZ));
                    if (dCenter > maxConeR) continue;
                }
                if (w.RoadAt((int)Math.Round(sp.X), (int)Math.Round(sp.Z)) != WorldData.Gravel) continue;
                if (NearAsphalt(sp.X, sp.Z, cfg.AsphaltClear)) continue;
                if (NearBuilding(sp.X, sp.Z, cfg.BldClear)) continue;
                if (site.Pads.Any(p => (sp.X - p.X) * (sp.X - p.X) + (sp.Z - p.Z) * (sp.Z - p.Z) < (p.Radius + cfg.PadClear) * (p.Radius + cfg.PadClear))) continue;
                if (MinJunctionClearance(i) < cfg.JoinEdgeClear) continue;
                seenIdx.Add(i);
                scored.Add(ScoreCandidate(i));
            }

            if (HasGoodTwoSidedPair()) break;
            if (pass >= 2 && scored.Count >= 2 && scored.Max(c => c.Dist) - scored.Min(c => c.Dist) >= 60.0) break;
        }

        double SafeLengthFor(int idx, double wantLen)
        {
            // Keep at least 5.0 m of clearly visible intact gravel road between ANY junction edge and the outermost landslide edge (hl * 1.42)
            double avail = MinJunctionClearance(idx) - 5.0;
            double maxLen = Math.Max(26.0, (avail / 1.42) * 2.0);
            return Math.Round(Math.Clamp(wantLen, 26.0, maxLen), 1);
        }

        if (scored.Count == 0) return result;

        // Pick best uphill debris slide (BuryFromAbove) from a candidate list:
        // prefers genuine steep uphill cliff with clear uphill scar & downhill debris zone, as close as possible to Pad 1.
        (bool Found, int Tier, (int Idx, double Dist, double DistFromMid, bool IsBefore, double Tx, double Tz, double Nx, double Nz,
            double UpRise24, double UpSlope, double DownDrop24, double CliffScore, bool UpClear, bool DownClear, bool DownRoadClear) Pick)
            PickBestBury(List<(int Idx, double Dist, double DistFromMid, bool IsBefore, double Tx, double Tz, double Nx, double Nz,
                double UpRise24, double UpSlope, double DownDrop24, double CliffScore, bool UpClear, bool DownClear, bool DownRoadClear)> pool)
        {
            if (pool.Count == 0) return (false, 99, default);
            var tiers = new Func<(int Idx, double Dist, double DistFromMid, bool IsBefore, double Tx, double Tz, double Nx, double Nz,
                double UpRise24, double UpSlope, double DownDrop24, double CliffScore, bool UpClear, bool DownClear, bool DownRoadClear), bool>[]
            {
                c => c.DownClear && c.UpClear && c.UpRise24 >= 12.0 && c.UpSlope >= 0.50,
                c => c.DownClear && c.UpRise24 >= 12.0 && c.UpSlope >= 0.50,
                c => c.DownClear && c.UpClear && c.UpRise24 >= 8.0 && c.UpSlope >= 0.35,
                c => c.DownClear && c.UpRise24 >= 8.0 && c.UpSlope >= 0.35,
                c => c.DownClear,
                _ => true
            };
            for (int t = 0; t < tiers.Length; t++)
            {
                var sub = pool.Where(tiers[t]).ToList();
                if (sub.Count == 0) continue;
                double minDist = sub.Min(c => c.DistFromMid);
                // Stay immediately next to Pad 1 (within +24 m of the closest qualifying cliff spot), choosing the strongest cliff there
                var near = sub.Where(c => c.DistFromMid <= minDist + 24.0).OrderByDescending(c => c.CliffScore).ToList();
                return (true, t, near[0]);
            }
            return (false, 99, default);
        }

        // Pick best downhill slump (!BuryFromAbove) from a candidate list, well-separated from buryIdx:
        // prefers clear downhill debris zone & real downhill drop, as close as possible to Pad 1.
        (bool Found, int Tier, (int Idx, double Dist, double DistFromMid, bool IsBefore, double Tx, double Tz, double Nx, double Nz,
            double UpRise24, double UpSlope, double DownDrop24, double CliffScore, bool UpClear, bool DownClear, bool DownRoadClear) Pick)
            PickBestSlump(List<(int Idx, double Dist, double DistFromMid, bool IsBefore, double Tx, double Tz, double Nx, double Nz,
                double UpRise24, double UpSlope, double DownDrop24, double CliffScore, bool UpClear, bool DownClear, bool DownRoadClear)> pool, int buryIdx)
        {
            if (pool.Count == 0) return (false, 99, default);
            List<(int Idx, double Dist, double DistFromMid, bool IsBefore, double Tx, double Tz, double Nx, double Nz,
                double UpRise24, double UpSlope, double DownDrop24, double CliffScore, bool UpClear, bool DownClear, bool DownRoadClear)> sep = null;
            foreach (double gap in new[] { 80.0, 60.0, 42.0, 24.0 })
            {
                sep = pool.Where(c =>
                {
                    double dx = path[c.Idx].X - path[buryIdx].X, dz = path[c.Idx].Z - path[buryIdx].Z;
                    return dx * dx + dz * dz >= gap * gap && Math.Abs(path[c.Idx].Dist - path[buryIdx].Dist) >= gap;
                }).ToList();
                if (sep.Count > 0) break;
            }
            if (sep == null || sep.Count == 0) return (false, 99, default);
            var tiers = new Func<(int Idx, double Dist, double DistFromMid, bool IsBefore, double Tx, double Tz, double Nx, double Nz,
                double UpRise24, double UpSlope, double DownDrop24, double CliffScore, bool UpClear, bool DownClear, bool DownRoadClear), bool>[]
            {
                c => c.DownClear && c.DownRoadClear && c.DownDrop24 >= 7.0,
                c => c.DownClear && c.DownDrop24 >= 7.0,
                c => c.DownClear,
                c => c.DownDrop24 >= 7.0,
                _ => true
            };
            for (int t = 0; t < tiers.Length; t++)
            {
                var sub = sep.Where(tiers[t]).ToList();
                if (sub.Count == 0) continue;
                double minDist = sub.Min(c => c.DistFromMid);
                // Stay immediately next to Pad 1 (within +24 m of the closest qualifying slump spot), choosing the strongest downhill drop there
                var near = sub.Where(c => c.DistFromMid <= minDist + 24.0).OrderByDescending(c => c.DownDrop24).ToList();
                return (true, t, near[0]);
            }
            return (false, 99, default);
        }

        // Strongly prefer placing ONE landslide immediately before Pad 1 and ONE immediately after Pad 1,
        // comparing both type assignments (bury-before/slump-after vs. bury-after/slump-before).
        var beforePool = scored.Where(c => c.IsBefore).ToList();
        var afterPool = scored.Where(c => !c.IsBefore).ToList();

        var buryBefore = PickBestBury(beforePool);
        var slumpAfter = buryBefore.Found ? PickBestSlump(afterPool, buryBefore.Pick.Idx) : (Found: false, Tier: 99, Pick: default);

        var buryAfter = PickBestBury(afterPool);
        var slumpBefore = buryAfter.Found ? PickBestSlump(beforePool, buryAfter.Pick.Idx) : (Found: false, Tier: 99, Pick: default);

        bool pairAOk = buryBefore.Found && slumpAfter.Found;
        bool pairBOk = buryAfter.Found && slumpBefore.Found;

        (int Idx, double Dist, double DistFromMid, bool IsBefore, double Tx, double Tz, double Nx, double Nz,
            double UpRise24, double UpSlope, double DownDrop24, double CliffScore, bool UpClear, bool DownClear, bool DownRoadClear) buryPick, slumpPick;
        bool hasSlump = false;

        if (pairAOk && pairBOk)
        {
            int tierA = buryBefore.Tier * 10 + slumpAfter.Tier * 5;
            int tierB = buryAfter.Tier * 10 + slumpBefore.Tier * 5;
            double distA = buryBefore.Pick.DistFromMid + slumpAfter.Pick.DistFromMid;
            double distB = buryAfter.Pick.DistFromMid + slumpBefore.Pick.DistFromMid;
            bool pickA;
            if (tierA != tierB) pickA = tierA < tierB;
            else if (Math.Abs(distA - distB) > 16.0) pickA = distA < distB;
            else pickA = pr.Next(2) == 0;

            buryPick = pickA ? buryBefore.Pick : buryAfter.Pick;
            slumpPick = pickA ? slumpAfter.Pick : slumpBefore.Pick;
            hasSlump = true;
        }
        else if (pairAOk)
        {
            buryPick = buryBefore.Pick;
            slumpPick = slumpAfter.Pick;
            hasSlump = true;
        }
        else if (pairBOk)
        {
            buryPick = buryAfter.Pick;
            slumpPick = slumpBefore.Pick;
            hasSlump = true;
        }
        else
        {
            // Fallback when one side of Pad 1 is completely blocked (e.g. asphalt road or city tile right below Pad 1):
            // place both as close as possible to Pad 1 on the remaining valid side.
            var bAll = PickBestBury(scored);
            buryPick = bAll.Pick;
            var sAll = PickBestSlump(scored, buryPick.Idx);
            if (sAll.Found) { slumpPick = sAll.Pick; hasSlump = true; }
            else slumpPick = default;
        }

        result.Add(new VolcanoLandslide
        {
            X = Math.Round(path[buryPick.Idx].X, 1),
            Z = Math.Round(path[buryPick.Idx].Z, 1),
            DirX = Math.Round(buryPick.Tx, 4),
            DirZ = Math.Round(buryPick.Tz, 4),
            DownX = Math.Round(buryPick.Nx, 4),
            DownZ = Math.Round(buryPick.Nz, 4),
            Length = SafeLengthFor(buryPick.Idx, 30 + pr.NextDouble() * 14),
            Strength = Math.Round(7.6 + pr.NextDouble() * 2.8, 2),
            BuryFromAbove = true,
            Seed = pr.Next(100000)
        });

        if (hasSlump)
        {
            result.Add(new VolcanoLandslide
            {
                X = Math.Round(path[slumpPick.Idx].X, 1),
                Z = Math.Round(path[slumpPick.Idx].Z, 1),
                DirX = Math.Round(slumpPick.Tx, 4),
                DirZ = Math.Round(slumpPick.Tz, 4),
                DownX = Math.Round(slumpPick.Nx, 4),
                DownZ = Math.Round(slumpPick.Nz, 4),
                Length = SafeLengthFor(slumpPick.Idx, 38 + pr.NextDouble() * 6),
                Strength = Math.Round(7.6 + pr.NextDouble() * 3.0, 2),
                BuryFromAbove = false,
                Seed = pr.Next(100000)
            });
        }

        return result;
    }

    /// Trace a curved flow path following the actual terrain gradient (steepest descent or ascent) from (x0, z0),
    /// starting along a slightly oblique initial direction (initDx, initDz) so landslides never align artificially with the road.
    static List<(double X, double Z, double S, double Tx, double Tz)> TraceSlopeFlow(
        WorldData w, double x0, double z0, double initDx, double initDz, double maxDist, bool downhill)
    {
        const double Step = 8.0;
        var pts = new List<(double X, double Z, double S, double Tx, double Tz)> { (x0, z0, 0, initDx, initDz) };
        double cx = x0, cz = z0, dx = initDx, dz = initDz, s = 0;
        while (s < maxDist)
        {
            // sample smoothed terrain gradient at ±10 m
            int ix = (int)Math.Round(cx), iz = (int)Math.Round(cz);
            double hx = (w.HeightAt(ix + 10, iz) - w.HeightAt(ix - 10, iz)) / 20.0;
            double hz = (w.HeightAt(ix, iz + 10) - w.HeightAt(ix, iz - 10)) / 20.0;
            double gx = downhill ? -hx : hx, gz = downhill ? -hz : hz;
            double gl = Math.Sqrt(gx * gx + gz * gz);
            if (gl > 0.06)
            {
                gx /= gl; gz /= gl;
                // only follow gradient if it stays roughly forward (within ~55° of initial direction)
                if (gx * initDx + gz * initDz > 0.55)
                {
                    dx = 0.62 * dx + 0.38 * gx;
                    dz = 0.62 * dz + 0.38 * gz;
                    double dl = Math.Sqrt(dx * dx + dz * dz);
                    if (dl > 1e-4) { dx /= dl; dz /= dl; }
                }
            }
            cx += dx * Step;
            cz += dz * Step;
            s += Step;
            pts.Add((cx, cz, s, dx, dz));
        }
        return pts;
    }

    /// Project (wx, wz) onto a curved flow polyline, returning continuous signed (sAlong, lateral offset u).
    /// Extends segment 0 backward linearly (without clamping to 0) so there is never a seam at s = 0.
    static (double S, double U) ProjectOntoFlow(List<(double X, double Z, double S, double Tx, double Tz)> flow, double wx, double wz)
    {
        double bestD2 = double.MaxValue, bestS = 0, bestU = 0;
        for (int i = 1; i < flow.Count; i++)
        {
            var a = flow[i - 1];
            var b = flow[i];
            double vx = b.X - a.X, vz = b.Z - a.Z;
            double l2 = vx * vx + vz * vz;
            double tRaw = l2 < 1e-6 ? 0 : ((wx - a.X) * vx + (wz - a.Z) * vz) / l2;
            double t = i == 1 ? Math.Clamp(tRaw, -5.0, 1.0) : Math.Clamp(tRaw, 0.0, 1.0);
            double px = a.X + vx * t, pz = a.Z + vz * t;
            double dx = wx - px, dz = wz - pz;
            double d2 = dx * dx + dz * dz;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                double len = Math.Sqrt(l2);
                bestS = a.S + len * t;
                // lateral sign relative to segment tangent (tx, tz)
                double tx = len > 1e-6 ? vx / len : b.Tx, tz = len > 1e-6 ? vz / len : b.Tz;
                bestU = dx * (-tz) + dz * tx;
            }
        }
        return (bestS, bestU);
    }

    static void ApplyLandslideList(WorldData w, WorldEdits e, List<VolcanoLandslide> slides, IReadOnlyList<(Point Pt, double ArcDist, double RoadHalf)> junctions = null)
    {
        static double Smooth(double a, double b, double v)
        {
            double t = Math.Clamp((v - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        foreach (var ls in slides)
        {
            bool nearBuilding = w.Prefabs.Any(pf => pf.Def != null && pf.Def.Kind != PrefabKind.Part
                && ls.X >= pf.X - 18 && ls.X <= pf.X + pf.SizeX + 18 && ls.Z >= pf.Z - 18 && ls.Z <= pf.Z + pf.SizeZ + 18);
            if (nearBuilding) continue;

            var noise = new Perlin(ls.Seed);
            var sr = new Random(ls.Seed + 313);
            double hl = Math.Max(12.0, ls.Length * 0.5);
            int reach = 115;
            int cx = (int)Math.Round(ls.X), cz = (int)Math.Round(ls.Z);
            int x0 = Math.Max(-w.Half + 2, cx - reach), x1 = Math.Min(w.Half - 2, cx + reach);
            int z0 = Math.Max(-w.Half + 2, cz - reach), z1 = Math.Min(w.Half - 2, cz + reach);
            int bw = x1 - x0 + 1, bh = z1 - z0 + 1;
            if (bw <= 0 || bh <= 0) continue;

            // Rotate slide direction slightly oblique (±12°–20°) against the road normal so nothing runs straight parallel/perpendicular to the road
            double oblique = (sr.Next(2) == 0 ? 1 : -1) * (0.21 + sr.NextDouble() * 0.14);
            double co = Math.Cos(oblique), so = Math.Sin(oblique);
            double downDx = ls.DownX * co - ls.DownZ * so;
            double downDz = ls.DownX * so + ls.DownZ * co;

            // Snapshot base heights, protect asphalt strictly, and find maxUpV / maxDownV before any neighboring road turn
            var baseH = new double[bw * bh];
            var asphaltMask = new bool[bw * bh];
            bool hasAsphalt = false;
            double maxUpV = 62.0, maxDownV = 78.0;
            for (int wz = z0; wz <= z1; wz++)
                for (int wx = x0; wx <= x1; wx++)
                {
                    int idx = w.Index(wx, wz);
                    int bi = (wz - z0) * bw + (wx - x0);
                    baseH[bi] = w.Heights[idx] / 256.0;
                    byte rm = w.RoadMask[idx];
                    if (rm == WorldData.Asphalt) { asphaltMask[bi] = true; hasAsphalt = true; }
                    if (rm == 0) continue;
                    double dx = wx - ls.X, dz = wz - ls.Z;
                    double u = dx * ls.DirX + dz * ls.DirZ;
                    double v = dx * ls.DownX + dz * ls.DownZ;
                    if (Math.Abs(u) > hl * 1.9) continue;
                    if (v < -16 && -v - 7 < maxUpV) maxUpV = Math.Max(26, -v - 7);
                    if (v > 16 && v - 7 < maxDownV) maxDownV = Math.Max(34, v - 7);
                }

            float[] asphaltDist = hasAsphalt ? GroundShaper.Distance(asphaltMask, bw, bh) : null;

            // Trace terrain-following downhill flow (for both B's chute and C's long debris tongue!) and uphill flow (for C's shallow niche)
            var downFlow = TraceSlopeFlow(w, ls.X, ls.Z, downDx, downDz, maxDownV + 12, downhill: true);
            var upFlow = ls.BuryFromAbove ? TraceSlopeFlow(w, ls.X, ls.Z, -downDx, -downDz, maxUpV + 8, downhill: false) : null;
            // Ensure uFlow lateral sign ALWAYS matches ls.Dir (prevents uRoad + (-uRoad) cancelling to 0 at sDown=5m -> narrow road-edge slot!)
            double uFlowSign = (-downDz * ls.DirX + downDx * ls.DirZ) >= 0 ? 1.0 : -1.0;

            // Pre-generate deterministic rock spurs/coves along u (for B's horseshoe lip) and slump blocks / rolled boulders
            var spurU = new double[3];
            var spurAmp = new double[3];
            var spurWid = new double[3];
            for (int k = 0; k < 3; k++)
            {
                spurU[k] = (-0.65 + 0.65 * k + (sr.NextDouble() - 0.5) * 0.35) * hl;
                spurAmp[k] = (k % 2 == 0 ? 1 : -1) * (3.6 + sr.NextDouble() * 2.2);
                spurWid[k] = 3.8 + sr.NextDouble() * 3.0;
            }
            var boulders = new List<(double U, double S, double R, double H)>();
            int bCount = ls.BuryFromAbove ? 7 : 5;
            for (int k = 0; k < bCount; k++)
            {
                double bs = ls.BuryFromAbove ? 12.0 + sr.NextDouble() * (maxDownV * 0.72) : 2.5 + sr.NextDouble() * 14.0;
                double bu = (sr.NextDouble() - 0.5) * hl * (ls.BuryFromAbove ? 1.8 : 1.3);
                double br = 2.4 + sr.NextDouble() * 2.6;
                double bhgt = 1.4 + sr.NextDouble() * 1.6;
                boulders.Add((bu, bs, br, bhgt));
            }
            double lobePhase = sr.NextDouble() * Math.PI * 2;
            int lobeCount = 2 + sr.Next(2); // 2 or 3 debris fan lobes
            double hRoadCenter = baseH[(Math.Clamp((int)Math.Round(ls.Z) - z0, 0, bh - 1)) * bw + Math.Clamp((int)Math.Round(ls.X) - x0, 0, bw - 1)];
            double SampleBaseRoad(double uAlong)
            {
                int rx = Math.Clamp((int)Math.Round(ls.X + ls.DirX * uAlong) - x0, 0, bw - 1);
                int rz = Math.Clamp((int)Math.Round(ls.Z + ls.DirZ * uAlong) - z0, 0, bh - 1);
                return baseH[rz * bw + rx];
            }

            for (int wz = z0; wz <= z1; wz++)
                for (int wx = x0; wx <= x1; wx++)
                {
                    if (w.WaterAt(wx, wz)) continue;
                    int bi = (wz - z0) * bw + (wx - x0);
                    double asphaltGuard = 1.0;
                    if (asphaltDist != null)
                    {
                        float ad = asphaltDist[bi];
                        if (ad <= 2.5f) continue;
                        asphaltGuard = Smooth(2.5, 10.0, ad);
                    }
                    // Hard junction guard: ALWAYS preserve >= 4.5 m of completely intact, visible continuing gravel road
                    // past EVERY junction along the spiral so a junction + landslide never reads visually as a curve!
                    double juncGuard = 1.0;
                    if (junctions != null)
                    {
                        for (int ji = 0; ji < junctions.Count; ji++)
                        {
                            var j = junctions[ji];
                            double jdx = wx - j.Pt.X, jdz = wz - j.Pt.Y;
                            double clearR = j.RoadHalf + 4.5;
                            double fadeEnd = clearR + 5.0;
                            double d2 = jdx * jdx + jdz * jdz;
                            if (d2 >= fadeEnd * fadeEnd) continue;
                            double dj = Math.Sqrt(d2);
                            if (dj <= clearR) { juncGuard = 0.0; break; }
                            juncGuard = Math.Min(juncGuard, Smooth(clearR, fadeEnd, dj));
                        }
                        if (juncGuard <= 0.001) continue;
                    }
                    double roadGuard = asphaltGuard * juncGuard;

                    if (w.Prefabs.Any(pf => pf.Def != null && pf.Def.Kind != PrefabKind.Part
                        && wx >= pf.X - 8 && wx <= pf.X + pf.SizeX + 8 && wz >= pf.Z - 8 && wz <= pf.Z + pf.SizeZ + 8))
                        continue;

                    double dx = wx - ls.X, dz = wz - ls.Z;
                    double uRoad = dx * ls.DirX + dz * ls.DirZ;
                    double vRoad = dx * ls.DownX + dz * ls.DownZ;

                    double n1 = noise.Noise(wx / 12.0, wz / 12.0);
                    double n2 = noise.Noise(wx / 5.2 + 11.3, wz / 5.2 + 7.1);
                    double n3 = noise.Noise(wx / 2.5 + 23.7, wz / 2.5 + 3.9);

                    double dh = 0;
                    bool clearGravel = false;
                    bool addSlideGravel = false;

                    if (ls.BuryFromAbove)
                    {
                        // === C. UPHILL DEBRIS SLIDE: breakaway scar directly out of the steep uphill cliff wall, seamless
                        //        talus ramp leaning flush against the cliff foot (ZERO hole/trench!), buried road, and
                        //        LONG 50–80 m downhill debris tongue ===
                        if (vRoad < -maxUpV - 8 || vRoad > maxDownV + 16) continue;

                        double h0 = baseH[bi];
                        double hRoadU = Math.Abs(uRoad) <= hl * 1.4 ? SampleBaseRoad(uRoad) : hRoadCenter;
                        double scarDh = 0, coneDh = 0, tongueDh = 0;

                        double ruR = uRoad / (hl * (1.02 + 0.28 * n1 + 0.12 * n2));
                        double acrossR = Math.Abs(ruR) < 1.22 ? 1.0 - Smooth(0.24, 1.22, Math.Abs(ruR)) : 0.0;
                        double flowN = noise.Noise(uRoad / 3.8 + 5.1, vRoad / 11.0 + 2.3);
                        double lump = 0.76 + 0.48 * (0.5 + 0.5 * (0.50 * flowN + 0.32 * n2 + 0.18 * n3));
                        double pileOverRoad = ls.Strength * 0.96 * (1.0 - 0.36 * Smooth(-2.0, 9.0, vRoad)) * lump;
                        // Continuous talus-apron surface rising smoothly uphill at ~29–30° until it meets the steep cliff wall
                        double talusSurfaceH = hRoadU + pileOverRoad + Math.Max(0.0, -vRoad) * 0.56;

                        // 1) Seamless debris apron / burial cone leaning flush against the cliff foot (vRoad -30 .. +15 m):
                        //    strictly additive (never digs a hole/trench between the steep wall and the debris heap!)
                        if (vRoad >= -32.0 && vRoad <= 16.0 && acrossR > 0.001)
                        {
                            double fadeUp = 1.0 - Smooth(22.0, 32.0, -vRoad);
                            double fadeDown = 1.0 - Smooth(6.0, 15.0, vRoad);
                            double neededFill = Math.Max(0.0, talusSurfaceH - h0);
                            coneDh = neededFill * acrossR * fadeUp * fadeDown;
                        }

                        // 2) Visible breakaway scar carved ONLY into the steep cliff rock standing ABOVE the talus apron!
                        if (vRoad <= -4.0 && upFlow != null)
                        {
                            var (sUp, uUp) = ProjectOntoFlow(upFlow, wx, wz);
                            double wUp = hl * (0.64 + 0.32 * Smooth(maxUpV, 12.0, sUp)) * (1.0 + 0.32 * n1 + 0.14 * n2);
                            double ruUp = uUp / Math.Max(4.0, wUp);
                            if (sUp >= 10.0 && sUp <= maxUpV + 4 && Math.Abs(ruUp) < 1.18)
                            {
                                double acrossU = 1.0 - Smooth(0.28, 1.18, Math.Abs(ruUp));
                                // horseshoe-curved rocky crown in the steep cliff wall
                                double crownS = maxUpV - 6.0 * (1.0 - Math.Min(1.0, ruUp * ruUp)) + 3.0 * n1;
                                double trackEnv = Smooth(11.0, 18.0, sUp) * (1.0 - Smooth(crownS - 4.0, crownS + 2.5, sUp));
                                // shallow rough scar track on the upper wall (2.0–3.6 m)
                                double shallowBite = Math.Min(3.6, ls.Strength * 0.34) * acrossU * trackEnv * (0.78 + 0.38 * (0.5 + 0.5 * n2));
                                // dramatic concave rock breakaway headscarp directly in the steep cliff face (total 5–7.5 m bite!)
                                double scarpBand = Smooth(crownS - 15.0, crownS - 6.0, sUp) * (1.0 - Smooth(crownS - 3.5, crownS + 2.0, sUp));
                                double headBite = Math.Clamp(ls.Strength * 0.54, 3.6, 5.0) * acrossU * scarpBand * (0.85 + 0.30 * (0.5 + 0.5 * n2));
                                double rawBite = shallowBite + headBite;
                                // CRITICAL: clamp breakaway scar near the cliff foot so it NEVER dips below the talus apron meeting the wall!
                                double talusFloorH = hRoadU + ls.Strength * 0.96 * lump + Math.Min(22.0, Math.Max(0.0, -vRoad)) * 0.54;
                                double safeBite = Math.Max(0.0, h0 - talusFloorH);
                                scarDh = -Math.Min(rawBite, safeBite);
                            }
                        }

                        // 3) LONG DOWNHILL DEBRIS TONGUE spilling 50–80 m down the mountain along the steepest gradient!
                        if (vRoad >= -2.0)
                        {
                            var (sDown, uDown) = ProjectOntoFlow(downFlow, wx, wz);
                            if (sDown >= 2.0 && sDown <= maxDownV + 10.0)
                            {
                                // fan widens x1.35–1.55 downhill with 2–3 lobate debris fingers
                                double fanW = hl * (1.0 + 0.48 * Smooth(6.0, maxDownV * 0.85, sDown)) * (1.0 + 0.32 * n1 + 0.14 * n2);
                                double ruD = uDown / Math.Max(6.0, fanW);
                                if (Math.Abs(ruD) < 1.24)
                                {
                                    double acrossU = 1.0 - Smooth(0.22, 1.24, Math.Abs(ruD));
                                    double lobeWave = 0.74 + 0.52 * (0.5 + 0.5 * Math.Sin(ruD * Math.PI * lobeCount + lobePhase + 1.2 * n1));
                                    double startT = Smooth(1.0, 8.0, sDown);
                                    double endReach = maxDownV * (0.82 + 0.24 * lobeWave);
                                    double endT = 1.0 - Smooth(endReach * 0.58, endReach, sDown);
                                    // thickness: ~0.62*Strength (~5–6 m) at road edge tapering naturally down to ~1.2–2.0 m far downhill
                                    double thickProfile = 0.64 - 0.40 * Smooth(8.0, maxDownV * 0.78, sDown);
                                    double blockRough = 0.72 + 0.56 * (0.5 + 0.5 * (0.55 * n2 + 0.45 * n3));
                                    tongueDh = ls.Strength * thickProfile * acrossU * startT * endT * lobeWave * blockRough;

                                    // scattered big rolled boulders (1.5–3 m) along the downhill debris fan
                                    foreach (var b in boulders)
                                    {
                                        double bd2 = (uDown - b.U) * (uDown - b.U) + (sDown - b.S) * (sDown - b.S);
                                        if (bd2 < b.R * b.R)
                                            tongueDh += b.H * (1.0 - Smooth(0.0, b.R, Math.Sqrt(bd2)));
                                    }
                                }
                            }
                        }

                        double deposit = Math.Max(coneDh, tongueDh);
                        dh = (scarDh + deposit) * roadGuard;
                        clearGravel = juncGuard >= 0.35 && deposit * roadGuard >= 0.55 + 0.28 * n2 && vRoad >= -8.0 && vRoad <= 10.0;
                    }
                    else
                    {
                        // === B. DOWNHILL SLUMP: horseshoe curved breakaway lip, 3 stepped benches (no >70° straight wall!), oblique terrain-following chute + slid gravel ===
                        if (vRoad < -14.0 || vRoad > maxDownV + 16.0) continue;

                        var (sFlow, uFlowRaw) = ProjectOntoFlow(downFlow, wx, wz);
                        double uFlow = uFlowRaw * uFlowSign;
                        // Smoothly blend road-aligned (uRoad, vRoad) uphill into sign-aligned flow coordinates (uFlow, sFlow) downhill (zero seam!)
                        double flowBlend = Smooth(-6.0, 10.0, vRoad);
                        double uUse = uRoad + (uFlow - uRoad) * flowBlend;
                        double vUse = vRoad + (sFlow - vRoad) * flowBlend;

                        // irregular chute flanks (strong width noise + alternating rock/scree flank steepness)
                        double widthFactor = vUse < 14.0
                            ? 1.02 - 0.12 * Smooth(0.0, 14.0, vUse)
                            : 0.90 - 0.22 * Smooth(14.0, maxDownV, vUse);
                        double flankMod = 1.0 + 0.38 * n1 + 0.18 * n2;
                        double ru = uUse / Math.Max(6.0, hl * widthFactor * flankMod);
                        if (Math.Abs(ru) >= 1.28) continue;
                        double flankSteepStart = 0.16 + 0.34 * (0.5 + 0.5 * noise.Noise(vUse / 18.0 + 4.1, Math.Sign(uUse) * 3.7));
                        double acrossU = 1.0 - Smooth(flankSteepStart, 1.28, Math.Abs(ru));
                        if (acrossU <= 0.001) continue;

                        // 1) Horseshoe arcuate breakaway lip: center bites ~5.4 m further uphill than ends + 3-octave noise + 3 rock spurs/coves!
                        double uNorm = Math.Clamp(uUse / hl, -1.25, 1.25);
                        double arc = -2.2 - 5.4 * Math.Max(0.0, 1.0 - uNorm * uNorm);
                        double lipMultiNoise = 1.65 * noise.Noise(uUse / 14.0 + 2.3, ls.Seed * 0.03)
                                             + 0.95 * noise.Noise(uUse / 6.0 + 9.1, ls.Seed * 0.07)
                                             + 0.45 * noise.Noise(uUse / 2.5 + 17.4, ls.Seed * 0.11);
                        double spurOff = 0;
                        for (int k = 0; k < 3; k++)
                        {
                            double du = (uUse - spurU[k]) / spurWid[k];
                            spurOff += spurAmp[k] * Math.Exp(-du * du);
                        }
                        double lipV = arc + lipMultiNoise + spurOff;

                        // 2) Stepped descent (3 irregular rock steps of ~3–4.5 m each over ~20 m, slope ~50–65° — NO single 15 m vertical cliff!)
                        double dv = vUse - lipV;
                        double s1Shift = 1.8 * noise.Noise(uUse / 9.0 + 13.2, 1.1);
                        double s2Shift = 2.2 * noise.Noise(uUse / 7.5 + 27.4, 2.7);
                        double step1 = Smooth(-1.0, 4.2 + 1.0 * n2, dv);
                        double step2 = Smooth(7.2 + s1Shift, 12.2 + s1Shift + 1.0 * n2, dv);
                        double step3 = Smooth(15.2 + s2Shift, 20.8 + s2Shift + 1.2 * n2, dv);
                        double steppedOpen = 0.38 * step1 + 0.34 * step2 + 0.28 * step3;

                        double chuteEnd = 1.0 - Smooth(maxDownV * 0.72, maxDownV, vUse);
                        double gullyNoise = noise.Noise(uUse / 3.4 + 8.4, vUse / 13.0 + 1.7);
                        double depthVar = 0.78 + 0.44 * (0.5 + 0.5 * (0.58 * gullyNoise + 0.42 * n2));
                        double depthProfile = 1.18 - 0.30 * Smooth(16.0, maxDownV * 0.78, vUse);
                        double drop = -ls.Strength * 1.15 * acrossU * steppedOpen * chuteEnd * depthProfile * depthVar;

                        // Tilted slump blocks (3–6 m rocky blocks resting on the upper stepped benches right below the lip)
                        double blocks = 0;
                        if (dv > 2.0 && dv < 19.0)
                            foreach (var b in boulders)
                            {
                                double bd2 = (uUse - b.U) * (uUse - b.U) + (dv - b.S) * (dv - b.S);
                                if (bd2 < b.R * b.R)
                                    blocks += b.H * (1.0 - Smooth(0.0, b.R, Math.Sqrt(bd2))) * acrossU;
                            }

                        // Large displaced debris lobe piled up far down at the bottom of the curved chute
                        double toeEnv = Smooth(maxDownV * 0.74, maxDownV * 0.88, vUse) * (1.0 - Smooth(maxDownV * 0.94, maxDownV + 13.0, vUse));
                        double toe = ls.Strength * 0.62 * acrossU * toeEnv * (0.72 + 0.56 * (0.5 + 0.5 * n3));

                        dh = (drop + blocks + toe) * roadGuard;
                        clearGravel = juncGuard >= 0.35 && drop * roadGuard <= -0.50 - 0.28 * n2 && dv > -0.3;

                        // Scattered gravel streaks dragged 2–52 m down inside the curved chute ("ein wenig Schotter im Riss")
                        if (roadGuard > 0.9 && dv >= 2.0 && vUse >= 1.5 && vUse <= Math.Min(54.0, maxDownV * 0.80) && Math.Abs(ru) < 0.80 && drop <= -1.0)
                        {
                            double streak = 0.65 * noise.Noise(uUse / 2.3 + 17.3, vUse / 10.5 + 4.1)
                                          + 0.35 * noise.Noise(wx / 2.8 + 41.2, wz / 2.8 + 9.4);
                            double thresh = 0.16 + 0.22 * Smooth(5.0, 48.0, vUse);
                            if (streak > thresh) addSlideGravel = true;
                        }
                    }

                    if (Math.Abs(dh) < 0.02 && !clearGravel && !addSlideGravel) continue;
                    int idx = w.Index(wx, wz);
                    if (w.RoadMask[idx] == WorldData.Asphalt) continue;

                    if (Math.Abs(dh) >= 0.02)
                    {
                        double nh = Math.Clamp(baseH[bi] + dh, 10.0, 250.0);
                        ushort hv = (ushort)Math.Clamp(Math.Round(nh * 256.0), 0, 65535);
                        if (hv != w.Heights[idx]) e.SetHeight(idx, hv);
                    }
                    if (addSlideGravel)
                    {
                        e.SetSplat(wx, wz, WorldEdits.GravelPixel);
                        e.SetRoad(idx, WorldData.Gravel);
                    }
                    else if (clearGravel && w.RoadMask[idx] == WorldData.Gravel)
                    {
                        e.SetRoad(idx, 0);
                        e.SetSplat(wx, wz, WorldEdits.EmptyPixel);
                    }
                }
        }
    }

    static double PolyLength(List<Point> pts)
    {
        double len = 0;
        for (int i = 1; i < pts.Count; i++) len += (pts[i] - pts[i - 1]).Length;
        return len;
    }
}
