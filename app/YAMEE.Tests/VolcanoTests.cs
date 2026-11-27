using System.Windows;
using MapEditor.Map;

namespace YameeTests;

static class VolcanoTests
{
    static Dictionary<string, PrefabDef> SyntheticMediumPois()
    {
        var cands = new Dictionary<string, PrefabDef>(StringComparer.OrdinalIgnoreCase);
        for (int k = 1; k <= 4; k++)
        {
            string pname = $"remnant_burnt_med_{k:00}";
            var d = new PrefabDef
            {
                SizeX = 42,
                SizeY = 14,
                SizeZ = 42,
                YOffset = -2,
                Tier = 2,
                Found = true
            };
            d.Tags.Add("wilderness");
            d.Exits.Add((21, 0));
            d.ExitSizes.Add((5, 5));
            cands[pname] = d;
        }
        return cands;
    }

    [Test("Volcano: always present in burnt forest even with Anomalies = 0")]
    static void AlwaysPresent()
    {
        var spec = new GeneratorSpec
        {
            Name = "VolcZeroAnom",
            Size = 4096,
            Seed = "VolcSeedA",
            Anomalies = 0,
            Lakes = 2,
            Rivers = 0,
            SpawnCount = 4
        };
        var g = new WorldGenerator(spec);
        g.BuildCoarse(4096 / 256.0);
        Check.True(g.Volcano != null, "Volcano must be generated when burnt forest share > 0 even if Anomalies = 0");
        Check.True(g.Volcano.NeedsWildernessPois, "NeedsWildernessPois flag must be true");
        Check.Equal(2, g.Volcano.Pads.Count, "Must prepare 2 medium POI pads (rim + mid-height)");
        Check.True(g.Volcano.AccessPath.Count >= 10, "Must prepare contour-hugging spiral access ramp");
        Check.True(g.Volcano.AccessPath.All(p => p.H.HasValue), "AccessPath must persist prepared spiral heights H");
        Check.True(g.Volcano.RimConnector.Count >= 2, "Must prepare mid-height branch connector");
        Check.Equal(2, g.Volcano.Landslides.Count, "Must plan always 2 landslides (1 of each type)");
        Check.True(g.Volcano.Landslides.Count(l => l.BuryFromAbove) == 1 && g.Volcano.Landslides.Count(l => !l.BuryFromAbove) == 1,
            "Planned landslides must include 1 downhill chute and 1 uphill debris slide");
        Check.True(g.Anomalies.Any(a => a.Kind == "Volcanic crater"), "Volcanic crater must be listed in Anomalies");

        // No burnt forest -> no volcano
        var noBurnt = new GeneratorSpec
        {
            Name = "NoBurnt",
            Size = 4096,
            Seed = "VolcSeedA",
            Shares = new[] { 40, 0, 20, 20, 20 },
            Anomalies = 2,
            Lakes = 1,
            Rivers = 0,
            SpawnCount = 4
        };
        var g2 = new WorldGenerator(noBurnt);
        g2.BuildCoarse(4096 / 256.0);
        Check.True(g2.Volcano == null, "No volcano should be generated when burnt forest share is 0");
    }

    [Test("Volcano: variance across seeds, map-size scaling, and continuous 12-20% spiral grade")]
    static void VarianceScalingAndGrade()
    {
        var sites = new List<VolcanoSite>();
        bool sawLake = false, sawDry = false;
        foreach (string seed in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta", "Iota", "Kappa" })
        {
            var g = new WorldGenerator(new GeneratorSpec
            {
                Name = "V_" + seed,
                Size = 4096,
                Seed = seed,
                Lakes = 2,
                Rivers = 0,
                SpawnCount = 4
            });
            g.BuildCoarse(4096 / 220.0);
            Check.True(g.Volcano != null, $"Volcano missing for seed {seed}");
            sites.Add(g.Volcano);
            if (g.Volcano.HasLake) sawLake = true; else sawDry = true;
            Check.True(g.Volcano.FloorHeight >= 10.0 && g.Volcano.FloorHeight <= 16.0,
                $"FloorHeight {g.Volcano.FloorHeight} out of 10..16 m for seed {seed}");
        }
        Check.True(sites.Select(s => s.CenterX).Distinct().Count() >= 4, "Volcano position must vary across seeds");
        Check.True(sites.Select(s => s.RimRadius).Distinct().Count() >= 4, "Volcano rim radius must vary across seeds");
        Check.True(sites.Select(s => s.RimHeight).Distinct().Count() >= 4, "Volcano rim height must vary across seeds");
        Check.True(sawLake && sawDry, "Volcano should vary between dry vent and inner crater lake across seeds");

        // Map size scaling + prepared spiral grade & fill check across 4k and 8k seeds (including Bolafo-Land, Vateri-Land, Hokoha-Land & Rahefu-Land)
        double avg4k = sites.Average(s => s.RimRadius);
        var radii8k = new List<double>();
        foreach (var (sz, seed) in new[] { (4096, "Alpha"), (8192, "Bolafo-Land"), (8192, "Vateri-Land"), (8192, "Hokoha-Land"), (8192, "Rahefu-Land") })
        {
            var g = new WorldGenerator(new GeneratorSpec
            {
                Name = $"V{sz}_{seed}",
                Size = sz,
                Seed = seed,
                Lakes = 2,
                Rivers = 0,
                SpawnCount = 4
            });
            g.BuildCoarse(sz / 256.0);
            Check.True(g.Volcano != null, $"{sz} volcano missing for {seed}");
            if (sz == 8192)
            {
                Check.True(g.Volcano.RimRadius >= 140, $"8k crater radius ({g.Volcano.RimRadius:0.0} m) should be >= 140 m");
                Check.True(g.Volcano.RimHeight <= 215.5, $"8k volcano rim ({g.Volcano.RimHeight:0.0} m) must stay <= 215 m");
                radii8k.Add(g.Volcano.RimRadius);
            }
            // Check 20 m sections along prepared AccessPath (excluding ±30 m around Pad 1 podest):
            // >= 90% in 12–20%, none > 20.5%, mean 14–18%, no uphill reversal
            VerifySpiralGrades(g.Volcano.AccessPath, g.Volcano.PodestDist, $"{sz}/{seed}");

            // A: Natural cut-in Hangweg: max embankment fill <= 2.5 m, >= 90% <= 1.0 m, downhill fill slope <= 40°
            VerifyRampFillAndSlope(g, $"{sz}/{seed}");
            VerifyFootLow(g, $"{sz}/{seed}");
            VerifyPlannedLandslidesNearMidPoi(g.Volcano, $"{sz}/{seed}");
        }
        // Wupevi-Land (2026-11-24): the spiral stopped on a flank bench at 88 m, 40 m above the plain, a cliff behind it
        var wupevi = new WorldGenerator(new GeneratorSpec
        {
            Name = "Wupevi", Size = 8192, Seed = "Wupevi-Land", Mountains = 1.1, Hills = 1.1, Rough = 1.1, Valleys = 1.1, Erosion = 1.1,
            Lakes = 3, Rivers = 0, BiomeLayout = 1, SpawnCount = 4
        });
        wupevi.BuildCoarse(8192 / 256.0);
        VerifyFootLow(wupevi, "8192/Wupevi-Land");
        Check.True(radii8k.Average() > avg4k * 1.1, $"8k volcano ({radii8k.Average():0.0}) should scale larger than 4k ({avg4k:0.0})");
    }

    /// the spiral ends down on the plain around the cone, never high up on the flank (otherwise no path reaches a road)
    static void VerifyFootLow(WorldGenerator g, string label)
    {
        double above = g.VolcanoFootAbovePlain();
        Console.WriteLine(FormattableString.Invariant($"       spiral foot {label}: {above:0.0} m above the plain"));
        Check.True(above <= 8.0, $"{label}: spiral foot {above:0.0} m above the plain outside the cone (at most 8 m)");
    }

    static void VerifyRampFillAndSlope(WorldGenerator g, string label)
    {
        var fills = g.VolcanoRampFills;
        Check.True(fills.Count >= 15, $"{label}: missing ramp fill diagnostics ({fills.Count})");
        double total = fills[^1].Dist;
        double podest = g.Volcano.PodestDist;
        var interior = fills.Where(f => f.Dist >= 30.0 && f.Dist <= total - 30.0
            && (podest <= 0 || Math.Abs(f.Dist - podest) > 30.0))
            .Select(f => f.H - f.PreGround).ToList();
        Check.True(interior.Count >= 10, $"{label}: too few interior ramp points for fill check");
        double maxFill = interior.Max();
        double fracSmall = interior.Count(v => v <= 1.0) / (double)interior.Count;
        Console.WriteLine(FormattableString.Invariant($"       ramp fill {label}: maxFill {maxFill:0.00} m, <=1m {fracSmall * 100:0}%, maxDownhillFillSlope {g.VolcanoMaxFillSlopeDeg:0.0} deg"));
        Check.True(maxFill <= 2.5, $"{label}: ramp fill {maxFill:0.00} m exceeds 2.5 m limit");
        Check.True(fracSmall >= 0.90, $"{label}: only {fracSmall * 100:0}% of ramp points have fill <= 1.0 m (need >= 90%)");
        Check.True(g.VolcanoMaxFillSlopeDeg <= 40.0, $"{label}: raised downhill embankment slope {g.VolcanoMaxFillSlopeDeg:0.0} deg exceeds 40 deg");
    }

    static void VerifySpiralGrades(List<VolcanoPoint> path, double podestDist, string label)
    {
        var pts = new List<(double D, double H)>();
        double d = 0;
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0)
            {
                double dx = path[i].X - path[i - 1].X, dz = path[i].Z - path[i - 1].Z;
                d += Math.Sqrt(dx * dx + dz * dz);
            }
            if (path[i].H is double h) pts.Add((d, h));
        }
        double total = pts[^1].D;
        double totalDrop = pts[0].H - pts[^1].H;
        Check.True(total >= 300, $"{label}: spiral too short ({total:0} m, drop {totalDrop:0} m)");
        Check.True(totalDrop >= 50, $"{label}: spiral must descend to the volcano foot (drop only {totalDrop:0} m)");
        var grades = new List<double>();
        for (double s = 30; s + 20 <= total - 30; s += 20)
        {
            double mid = s + 10.0;
            if (podestDist > 0 && Math.Abs(mid - podestDist) <= 30.0) continue; // exclude Pad 1 landing podest & transitions
            double h0 = SampleAt(pts, s), h1 = SampleAt(pts, s + 20);
            double g = (h0 - h1) / 20.0; // positive = downhill from rim to foot
            Check.True(g >= -0.001, $"{label}: uphill reversal at {s:0} m (grade {g * 100:0.0}%)");
            Check.True(g <= 0.205, $"{label}: section > 20.5% at {s:0} m (grade {g * 100:0.0}%)");
            grades.Add(g);
        }
        if (podestDist > 40 && podestDist < total - 40)
        {
            double pGrade = Math.Abs(SampleAt(pts, podestDist - 10) - SampleAt(pts, podestDist + 10)) / 20.0;
            Check.True(pGrade <= 0.05, $"{label}: Pad 1 landing podest prepared grade {pGrade * 100:0.0}% exceeds 5%");
        }
        Check.True(grades.Count >= 10, $"{label}: too few 20 m sections ({grades.Count})");
        double inBand = grades.Count(g => g >= 0.12 && g <= 0.20) / (double)grades.Count;
        double mean = grades.Average();
        // Horizontal smoothness check: no sharp zigzag kinks along the mountain spiral
        double maxTurnDeg = 0;
        for (int i = 1; i < path.Count - 1; i++)
        {
            double ax = path[i].X - path[i - 1].X, az = path[i].Z - path[i - 1].Z;
            double bx = path[i + 1].X - path[i].X, bz = path[i + 1].Z - path[i].Z;
            double la = Math.Sqrt(ax * ax + az * az), lb = Math.Sqrt(bx * bx + bz * bz);
            if (la < 2.0 || lb < 2.0) continue;
            double cosA = Math.Clamp((ax * bx + az * bz) / (la * lb), -1.0, 1.0);
            double deg = Math.Acos(cosA) * 180.0 / Math.PI;
            if (deg > maxTurnDeg) maxTurnDeg = deg;
        }
        Console.WriteLine(FormattableString.Invariant($"       spiral {label}: {total:0} m (drop {totalDrop:0} m), {grades.Count} sections, mean {mean * 100:0.0}%, min {grades.Min() * 100:0.0}%, max {grades.Max() * 100:0.0}%, in 12-20%: {inBand * 100:0}%, maxTurn {maxTurnDeg:0.0} deg"));
        Check.True(inBand >= 0.90, $"{label}: only {inBand * 100:0}% of 20 m sections in 12–20% (need >= 90%)");
        Check.True(mean >= 0.14 && mean <= 0.18, $"{label}: mean grade {mean * 100:0.0}% outside 14–18%");
        Check.True(maxTurnDeg <= 22.0, $"{label}: sharp horizontal zigzag kink ({maxTurnDeg:0.0} deg > 22 deg)");
    }

    static double SampleAt(List<(double D, double H)> pts, double dist)
    {
        for (int i = 1; i < pts.Count; i++)
            if (pts[i].D >= dist)
            {
                double t = Math.Clamp((dist - pts[i - 1].D) / Math.Max(1e-6, pts[i].D - pts[i - 1].D), 0, 1);
                return pts[i - 1].H + (pts[i].H - pts[i - 1].H) * t;
            }
        return pts[^1].H;
    }

    [Test("Volcano: deep floor, built 12-20% spiral, 2 dramatic landslides (both types), asphalt cutoff & exact undo")]
    static void FullWorldAndWilderness()
    {
        string dir = Path.Combine(Fixture.Temp, "VolcanoFull");
        var spec = new GeneratorSpec
        {
            Name = "VolcanoFull",
            Size = 4096,
            Seed = "VolcanoTest42",
            Lakes = 2,
            Rivers = 0,
            SpawnCount = 6,
            Anomalies = 1
        };
        var gen = new WorldGenerator(spec);
        gen.Write(dir, null, new Progress<string>());

        var site = VolcanoSite.Load(dir);
        Check.True(site != null, "yamee_volcano.json must be written and loadable");
        Check.True(site.NeedsWildernessPois, "NeedsWildernessPois flag must be true");
        Check.Equal(2, site.Pads.Count, "Must have 2 pads");

        var w = WorldData.Load(dir, null, new Progress<string>());
        Check.Equal(1, w.BiomeAt((int)Math.Round(site.CenterX), (int)Math.Round(site.CenterZ)), "Volcano center must be in burnt forest");

        // 1. Deep crater floor between 10 and 16 m
        double minCenterH = double.MaxValue;
        for (int dz = -20; dz <= 20; dz += 2)
            for (int dx = -20; dx <= 20; dx += 2)
                minCenterH = Math.Min(minCenterH, w.HeightAt((int)Math.Round(site.CenterX) + dx, (int)Math.Round(site.CenterZ) + dz));
        Check.True(minCenterH >= 9.5 && minCenterH <= 16.5, $"Crater floor height {minCenterH:0.0} m should be in 10..16 m");

        // 2. Pad 0 on crater rim, Pad 1 halfway up: inner building core (22 m radius) cleanly flat
        foreach (var pad in site.Pads)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int dz = -22; dz <= 22; dz += 4)
                for (int dx = -22; dx <= 22; dx += 4)
                {
                    if (dx * dx + dz * dz > 22 * 22) continue;
                    double h = w.HeightAt((int)Math.Round(pad.X) + dx, (int)Math.Round(pad.Z) + dz);
                    lo = Math.Min(lo, h);
                    hi = Math.Max(hi, h);
                }
            Check.True(hi - lo <= 2.5, $"Pad core at ({pad.X},{pad.Z}) is not flat: spread {hi - lo:0.00} m");
        }
        Check.True(site.Pads[0].Height > site.Pads[1].Height + 15,
            $"Pad 0 (rim {site.Pads[0].Height:0.0} m) must sit clearly above Pad 1 (mid-height {site.Pads[1].Height:0.0} m)");
        Check.True(site.Pads[1].Height > minCenterH + 20,
            $"Pad 1 (mid-height {site.Pads[1].Height:0.0} m) must sit clearly above crater floor ({minCenterH:0.0} m)");

        // 3. Volcano must tower above the rest of burnt forest mountains
        double maxOtherBurnt = 0;
        for (int wz = -w.Half + 40; wz < w.Half - 40; wz += 16)
            for (int wx = -w.Half + 40; wx < w.Half - 40; wx += 16)
            {
                if (w.BiomeAt(wx, wz) != 1) continue;
                double d = Math.Sqrt((wx - site.CenterX) * (wx - site.CenterX) + (wz - site.CenterZ) * (wz - site.CenterZ));
                if (d < site.OuterRadius + 60) continue;
                maxOtherBurnt = Math.Max(maxOtherBurnt, w.HeightAt(wx, wz));
            }
        Check.True(site.RimHeight > maxOtherBurnt,
            $"Volcano rim ({site.RimHeight:0.0} m) must be higher than other burnt forest mountains ({maxOtherBurnt:0.0} m)");

        // 4. Build 2 medium POIs + 5 m gravel spiral + ALWAYS 2 dramatic landslides (1 downhill chute + 1 uphill debris slide)
        var cands = SyntheticMediumPois();
        var wspec = new WildernessSpec { Density = 0.0, Seed = "VolcWild" };
        var wg = new WildernessGenerator(w, wspec, new WildernessRules(), cands, _ => null);
        var plan = wg.Plan(null);
        Check.Equal(2, plan.Pois.Count, "WildernessGenerator must place 2 medium POIs on the volcano");
        Check.Equal(2, plan.Paths.Count, "WildernessGenerator must plan 2 gravel paths for the volcano POIs");
        Check.True(plan.Paths.All(p => Math.Abs(p.Width - 5.0) < 0.01 && p.IsVolcano), "Volcano gravel paths must be 5 m wide and flagged IsVolcano");

        string stateBefore = Fixture.State(w);
        var preBuildH = (ushort[])w.Heights.Clone();
        var edits = new WorldEdits(w);
        WildernessBuildResult buildRes = null;
        edits.Run("volcano-wild", "", ed => buildRes = WildernessGenerator.Build(w, ed, plan, wspec, null));
        Check.Equal(2, buildRes.Pois, "Build must place both volcano POIs");
        Check.Equal(2, buildRes.Paths, "Build must construct both 5 m volcano gravel paths");

        // 4a. Verify BUILT spiral road profile (excluding ±30 m around Pad 1 podest junction) stays 12–20 %!
        var spiralRes = buildRes.Built.First(b => b.Path.IsVolcanoSpiral).Road;
        var branchBuilt = buildRes.Built.First(b => b.Path.IsVolcano && !b.Path.IsVolcanoSpiral);
        var branchRes = branchBuilt.Road;
        var joinPt = branchBuilt.Path.Points[^1];
        double joinDistOnSpiral = ClosestDistOnRoad(spiralRes, joinPt.X, joinPt.Y);

        var builtGrades = new List<double>();
        double spiralTotal = spiralRes.Path[^1].Dist;
        for (double s = 30; s + 20 <= spiralTotal - 30; s += 20)
        {
            double mid = s + 10.0;
            if (Math.Abs(mid - joinDistOnSpiral) <= 30.0) continue; // exclude Pad 1 landing podest & transitions
            double h0 = RoadProfileAt(spiralRes, s), h1 = RoadProfileAt(spiralRes, s + 20);
            double g = (h0 - h1) / 20.0;
            Check.True(g >= -0.002, $"Built volcano spiral has uphill reversal at {s:0} m ({g * 100:0.0}%)");
            Check.True(g <= 0.205, $"Built volcano spiral exceeds 20.5% at {s:0} m ({g * 100:0.0}%)");
            builtGrades.Add(g);
        }
        double builtInBand = builtGrades.Count(g => g >= 0.12 && g <= 0.20) / (double)builtGrades.Count;
        double builtMean = builtGrades.Average();
        Console.WriteLine(FormattableString.Invariant($"       volcano rim {site.RimHeight:0.0} m (other burnt {maxOtherBurnt:0.0} m), built spiral {spiralTotal:0} m: mean {builtMean * 100:0.0}%, min {builtGrades.Min() * 100:0.0}%, max {builtGrades.Max() * 100:0.0}%, in 12-20%: {builtInBand * 100:0}%, landslides {buildRes.VolcanoLandslides.Count}"));
        Check.True(builtInBand >= 0.90, $"Built spiral: only {builtInBand * 100:0}% of 20 m sections in 12–20%");
        Check.True(builtMean >= 0.14 && builtMean <= 0.18, $"Built spiral mean grade {builtMean * 100:0.0}% outside 14–18%");

        // 4a-D. Verify BOTH POIs sit seamlessly at exact TerrainHeight (no rectangular "set on top" curb)
        //       and connect cleanly to their gravel roads at exact TerrainHeight!
        for (int pi = 0; pi < 2; pi++)
        {
            var pf = plan.Pois[pi].Building;
            double targetH = pf.TerrainHeight;
            var edgeDiffs = new List<double>();
            for (int x = pf.X; x < pf.X + pf.SizeX; x += 2)
            {
                edgeDiffs.Add(Math.Abs(w.HeightAt(x, pf.Z - 1) - targetH));
                edgeDiffs.Add(Math.Abs(w.HeightAt(x, pf.Z + pf.SizeZ) - targetH));
            }
            for (int z = pf.Z; z < pf.Z + pf.SizeZ; z += 2)
            {
                edgeDiffs.Add(Math.Abs(w.HeightAt(pf.X - 1, z) - targetH));
                edgeDiffs.Add(Math.Abs(w.HeightAt(pf.X + pf.SizeX, z) - targetH));
            }
            double avgEdgeStep = edgeDiffs.Average();
            Console.WriteLine(FormattableString.Invariant($"       POI {pi}: padH {site.Pads[pi].Height:0.00}, terrainH {targetH:0.00}, avgEdgeStep {avgEdgeStep:0.00} m, maxEdgeStep {edgeDiffs.Max():0.00} m"));
            Check.True(avgEdgeStep <= 0.35, $"Volcano POI {pi} perimeter avg step {avgEdgeStep:0.00} m from TerrainHeight ({targetH:0.00} m) exceeds 0.35 m");
        }
        double poi0ExitDiff = Math.Abs(plan.Pois[0].Building.TerrainHeight - spiralRes.Profile[0]);
        Check.True(poi0ExitDiff <= 0.15, $"Pad 0 POI exit vs spiral start height diff {poi0ExitDiff:0.00} m exceeds 0.15 m");

        double branchLen = branchRes.Path[^1].Dist;
        double maxBranchGrade = 0;
        for (double s = 0; s + 5.0 <= branchLen; s += 5.0)
            maxBranchGrade = Math.Max(maxBranchGrade, Math.Abs(RoadProfileAt(branchRes, s + 5.0) - RoadProfileAt(branchRes, s)) / 5.0);
        double poi1TerrainH = plan.Pois[1].Building.TerrainHeight;
        double branchStartDiff = Math.Abs(poi1TerrainH - branchRes.Profile[0]);
        double podestGrade = Math.Abs(RoadProfileAt(spiralRes, joinDistOnSpiral + 10.0) - RoadProfileAt(spiralRes, joinDistOnSpiral - 10.0)) / 20.0;
        double maxPodestCross = MaxRoadCrossSlope(w, spiralRes, joinDistOnSpiral - 10.0, joinDistOnSpiral + 10.0, 2.5);
        double maxJunctionSlopeDeg = MaxNonUphillSlopeNear(w, site, joinPt.X, joinPt.Y, 10);
        // Pad 1 Y-fork regression check (Habegu-Land +170 m): clean junction angle (>= 40°) & no curb/step on gravel across the fork
        int bLast = branchRes.Path.Count - 1, bPrev = Math.Max(0, bLast - 6);
        double bDx = branchRes.Path[bLast].X - branchRes.Path[bPrev].X, bDz = branchRes.Path[bLast].Z - branchRes.Path[bPrev].Z;
        double bL = Math.Max(1e-6, Math.Sqrt(bDx * bDx + bDz * bDz));
        int sIdx = spiralRes.IndexAt(joinDistOnSpiral);
        int sA = Math.Max(0, sIdx - 6), sB = Math.Min(spiralRes.Path.Count - 1, sIdx + 6);
        double sDx = spiralRes.Path[sB].X - spiralRes.Path[sA].X, sDz = spiralRes.Path[sB].Z - spiralRes.Path[sA].Z;
        double sL = Math.Max(1e-6, Math.Sqrt(sDx * sDx + sDz * sDz));
        double forkAngleDeg = Math.Acos(Math.Clamp(Math.Abs((bDx * sDx + bDz * sDz) / (bL * sL)), 0, 1)) * 180.0 / Math.PI;
        double maxForkGravelStep = 0, maxForkEdgeStep = 0;
        int jx0 = (int)Math.Round(joinPt.X), jz0 = (int)Math.Round(joinPt.Y);
        for (int dz = -18; dz <= 18; dz++)
            for (int dx = -18; dx <= 18; dx++)
            {
                if (dx * dx + dz * dz > 18 * 18) continue;
                int wx = jx0 + dx, wz = jz0 + dz;
                if (!w.Contains(wx - 1, wz) || !w.Contains(wx + 1, wz) || !w.Contains(wx, wz - 1) || !w.Contains(wx, wz + 1)) continue;
                if (w.RoadAt(wx, wz) != WorldData.Gravel) continue;
                double h0 = w.HeightAt(wx, wz);
                foreach (var (nx, nz) in new[] { (wx + 1, wz), (wx - 1, wz), (wx, wz + 1), (wx, wz - 1) })
                {
                    double diff = Math.Abs(w.HeightAt(nx, nz) - h0);
                    if (w.RoadAt(nx, nz) == WorldData.Gravel) maxForkGravelStep = Math.Max(maxForkGravelStep, diff);
                    else maxForkEdgeStep = Math.Max(maxForkEdgeStep, diff);
                }
            }
        Console.WriteLine(FormattableString.Invariant($"       Pad 1 podest/branch: branchGrade {maxBranchGrade * 100:0.0}%, exitDiff {branchStartDiff:0.00} m (Pad0 {poi0ExitDiff:0.00} m), podestGrade {podestGrade * 100:0.0}%, cross {maxPodestCross * 100:0.0}%, juncSlope {maxJunctionSlopeDeg:0.0} deg, forkAngle {forkAngleDeg:0.0} deg, forkStep {maxForkGravelStep:0.00} m, edgeStep {maxForkEdgeStep:0.00} m"));
        Check.True(maxBranchGrade <= 0.08, $"Pad 1 driveway longitudinal grade {maxBranchGrade * 100:0.0}% exceeds 8%");
        Check.True(branchStartDiff <= 0.15, $"Pad 1 POI exit vs driveway start height diff {branchStartDiff:0.00} m exceeds 0.15 m");
        Check.True(podestGrade <= 0.05, $"Spiral podest longitudinal grade {podestGrade * 100:0.0}% exceeds 5%");
        Check.True(maxPodestCross <= 0.06, $"Spiral podest cross slope {maxPodestCross * 100:0.0}% exceeds 6%");
        Check.True(maxJunctionSlopeDeg <= 30.0, $"Pad 1 junction slope {maxJunctionSlopeDeg:0.0} deg exceeds 30 deg (excluding uphill cut-bank)");
        Check.True(forkAngleDeg >= 40.0, $"Pad 1 Y-fork junction angle {forkAngleDeg:0.0} deg is too shallow (< 40 deg, ArrivalOk rule)");
        Check.True(maxForkGravelStep <= 0.65, $"Pad 1 Y-fork has a {maxForkGravelStep:0.00} m step across the gravel roadway");
        Check.True(maxForkEdgeStep <= 0.65, $"Pad 1 Y-fork has a {maxForkEdgeStep:0.00} m vertical ledge directly beside the main gravel edge");

        // 4b. Verify ALWAYS 2 dramatic landslides: 1 horseshoe stepped downhill chute (B) + 1 uphill niche & long downhill debris tongue (C),
        //     BOTH positioned immediately before / after the middle POI (Pad 1)
        Check.Equal(2, buildRes.VolcanoLandslides.Count, "Build must always apply 2 landslides on the volcano spiral");
        Check.Equal(1, buildRes.VolcanoLandslides.Count(l => l.BuryFromAbove), "Must include exactly 1 uphill debris slide");
        Check.Equal(1, buildRes.VolcanoLandslides.Count(l => !l.BuryFromAbove), "Must include exactly 1 downhill open chute");
        {
            var relDists = buildRes.VolcanoLandslides.Select(ls => ClosestDistOnRoad(spiralRes, ls.X, ls.Z) - joinDistOnSpiral).ToList();
            Check.True(relDists.All(d => Math.Abs(d) <= 115.0),
                $"Both landslides must sit immediately before/after Pad 1 (<= 115 m from mid POI junction, got {string.Join(", ", relDists.Select(d => $"{d:+0;-0} m"))})");
            Check.True(relDists.Any(d => d < 0) && relDists.Any(d => d > 0),
                $"Expected one landslide immediately before Pad 1 and one immediately after Pad 1 (got {string.Join(", ", relDists.Select(d => $"{d:+0;-0} m"))})");
        }

        foreach (var ls in buildRes.VolcanoLandslides)
        {
            double maxDh = 0;
            double minU = double.MaxValue, maxU = double.MinValue;
            for (int u = -30; u <= 30; u++)
            {
                int wx = (int)Math.Round(ls.X + ls.DirX * u), wz = (int)Math.Round(ls.Z + ls.DirZ * u);
                if (!w.Contains(wx, wz)) continue;
                double dh = Math.Abs(w.HeightAt(wx, wz) - preBuildH[w.Index(wx, wz)] / 256.0);
                if (dh > maxDh) maxDh = dh;
                if (dh >= 1.5) { minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); }
            }
            double spanU = maxU - minU;
            double relMid = ClosestDistOnRoad(spiralRes, ls.X, ls.Z) - joinDistOnSpiral;
            Console.WriteLine(FormattableString.Invariant($"       landslide {(ls.BuryFromAbove ? "bury-uphill" : "downhill-chute")} at ({ls.X:0},{ls.Z:0}), midPOI {relMid:+0;-0} m: len {ls.Length:0} m, max |dh| {maxDh:0.0} m, affected span {spanU:0} m"));
            Check.True(maxDh >= 6.0, $"Landslide ({(ls.BuryFromAbove ? "bury" : "slump")}) must be dramatic (max |dh| {maxDh:0.0} m < 6 m)");
            Check.True(spanU >= 25.0, $"Landslide ({(ls.BuryFromAbove ? "bury" : "slump")}) must span >= 25 m along road (got {spanU:0} m)");

            if (!ls.BuryFromAbove)
            {
                // B: Scattered slid gravel streaks
                int chuteGravel = 0;
                for (int v = 4; v <= 45; v++)
                    for (int u = -12; u <= 12; u++)
                    {
                        int wx = (int)Math.Round(ls.X + ls.DirX * u + ls.DownX * v);
                        int wz = (int)Math.Round(ls.Z + ls.DirZ * u + ls.DownZ * v);
                        if (w.Contains(wx, wz) && w.RoadAt(wx, wz) == WorldData.Gravel) chuteGravel++;
                    }
                Check.True(chuteGravel >= 10, $"Downhill landslide chute must show scattered gravel streaks (got {chuteGravel} cells)");

                // B: Horseshoe lip shape (non-linear: StdDev >= 1.5 m, Max >= 4 m, center >= 3 m further uphill than ends)
                double hl = ls.Length * 0.5;
                var lipSamples = new List<(double U, double V)>();
                int uLim = (int)Math.Round(0.8 * hl);
                for (int u = -uLim; u <= uLim; u++)
                {
                    for (int v = -18; v <= 16; v++)
                    {
                        int wx = (int)Math.Round(ls.X + ls.DirX * u + ls.DownX * v);
                        int wz = (int)Math.Round(ls.Z + ls.DirZ * u + ls.DownZ * v);
                        if (!w.Contains(wx, wz)) continue;
                        double drop = preBuildH[w.Index(wx, wz)] / 256.0 - w.HeightAt(wx, wz);
                        if (drop > 2.0) { lipSamples.Add((u, v)); break; }
                    }
                }
                Check.True(lipSamples.Count >= 12, $"Downhill landslide lip too short ({lipSamples.Count} samples)");
                double meanU = lipSamples.Average(p => p.U), meanV = lipSamples.Average(p => p.V);
                double sUU = lipSamples.Sum(p => (p.U - meanU) * (p.U - meanU));
                double sUV = lipSamples.Sum(p => (p.U - meanU) * (p.V - meanV));
                double slopeB = sUU > 1e-6 ? sUV / sUU : 0.0;
                double interceptA = meanV - slopeB * meanU;
                var resids = lipSamples.Select(p => p.V - (interceptA + slopeB * p.U)).ToList();
                double stdDev = Math.Sqrt(resids.Average(r => r * r));
                double maxDev = resids.Max(r => Math.Abs(r));
                double centerLip = lipSamples.Where(p => Math.Abs(p.U) <= 0.25 * hl).Average(p => p.V);
                double endsLip = lipSamples.Where(p => Math.Abs(p.U) >= 0.55 * hl).Average(p => p.V);
                double bowDepth = endsLip - centerLip; // positive = center further uphill (more negative v)

                // B: No continuous wall section > 10 m long with > 70° slope
                double steepThresh = Math.Tan(70.0 * Math.PI / 180.0);
                int maxSteepRun = 0, curSteepRun = 0;
                for (int u = -uLim; u <= uLim; u++)
                {
                    bool has70 = false;
                    for (int v = -16; v <= 14; v++)
                    {
                        int wx = (int)Math.Round(ls.X + ls.DirX * u + ls.DownX * v);
                        int wz = (int)Math.Round(ls.Z + ls.DirZ * u + ls.DownZ * v);
                        if (!w.Contains(wx, wz)) continue;
                        if (Math.Abs(w.HeightAt(wx, wz) - preBuildH[w.Index(wx, wz)] / 256.0) < 1.5) continue;
                        double dh = Math.Max(
                            Math.Abs(w.HeightAt(wx + 1, wz) - w.HeightAt(wx - 1, wz)) * 0.5,
                            Math.Abs(w.HeightAt(wx, wz + 1) - w.HeightAt(wx, wz - 1)) * 0.5);
                        if (dh > steepThresh) { has70 = true; break; }
                    }
                    curSteepRun = has70 ? curSteepRun + 1 : 0;
                    if (curSteepRun > maxSteepRun) maxSteepRun = curSteepRun;
                }
                Console.WriteLine(FormattableString.Invariant($"       slump lip: std {stdDev:0.00} m, maxDev {maxDev:0.00} m, bow {bowDepth:0.00} m, max >70deg run {maxSteepRun} m"));
                Check.True(stdDev >= 1.5, $"Downhill slump lip too straight: StdDev {stdDev:0.00} m < 1.5 m");
                Check.True(maxDev >= 4.0, $"Downhill slump lip lacks irregularity: MaxDev {maxDev:0.00} m < 4.0 m");
                Check.True(bowDepth >= 3.0, $"Downhill slump lip must form a horseshoe arc (center only {bowDepth:0.00} m uphill vs ends, need >= 3.0 m)");
                Check.True(maxSteepRun <= 10, $"Downhill slump has continuous >70 deg vertical wall of {maxSteepRun} m (max 10 m)");

                // Bild 1 regression check: no narrow slot along the road edge beyond the landslide width (caused by uFlow sign flip)
                double maxFarEdgeDrop = 0;
                int uFar0 = (int)Math.Ceiling(hl * 1.55), uFar1 = (int)Math.Ceiling(hl * 2.4);
                for (int sign = -1; sign <= 1; sign += 2)
                    for (int u = uFar0; u <= uFar1; u += 2)
                        for (int v = 2; v <= 9; v++)
                        {
                            int wx = (int)Math.Round(ls.X + ls.DirX * (sign * u) + ls.DownX * v);
                            int wz = (int)Math.Round(ls.Z + ls.DirZ * (sign * u) + ls.DownZ * v);
                            if (!w.Contains(wx, wz)) continue;
                            double dFar = preBuildH[w.Index(wx, wz)] / 256.0 - w.HeightAt(wx, wz);
                            if (dFar > maxFarEdgeDrop) maxFarEdgeDrop = dFar;
                        }
                Check.True(maxFarEdgeDrop <= 1.0, $"Downhill slump carved an unintended narrow slot ({maxFarEdgeDrop:0.0} m deep) along the road edge past the slide width");
            }
            else
            {
                // C + Bild 1: MUST sit at a genuine steep uphill cliff wall, visibly broken out of the cliff face,
                //             with ZERO hole/trench between cliff wall and debris mound, plus long downhill debris tongue!
                double preRoadH = preBuildH[w.Index((int)Math.Round(ls.X), (int)Math.Round(ls.Z))] / 256.0;
                double preUp24H = preBuildH[w.Index((int)Math.Round(ls.X - ls.DownX * 24), (int)Math.Round(ls.Z - ls.DownZ * 24))] / 256.0;
                double cliffRise24 = preUp24H - preRoadH;
                Check.True(cliffRise24 >= 10.0, $"Uphill debris slide must sit below a real steep cliff wall (24 m uphill rise only {cliffRise24:0.0} m < 10 m)");

                // Verify NO trench/hole between steep cliff wall and debris heap along the center profile (v = -24 .. 0)
                double hAtRoad = w.HeightAt((int)Math.Round(ls.X), (int)Math.Round(ls.Z));
                double minBetween = double.MaxValue;
                for (int v = -24; v <= 0; v++)
                {
                    int wx = (int)Math.Round(ls.X + ls.DownX * v);
                    int wz = (int)Math.Round(ls.Z + ls.DownZ * v);
                    if (w.Contains(wx, wz)) minBetween = Math.Min(minBetween, w.HeightAt(wx, wz));
                }
                double trenchDip = hAtRoad - minBetween;
                Check.True(trenchDip <= 1.5, $"Uphill debris slide dug a hole/trench of {trenchDip:0.0} m between steep cliff wall and debris mound");

                double maxTrackLower = 0, maxCrownDrop = 0;
                for (int v = -46; v <= -8; v++)
                    for (int u = -22; u <= 22; u++)
                    {
                        int wx = (int)Math.Round(ls.X + ls.DirX * u + ls.DownX * v);
                        int wz = (int)Math.Round(ls.Z + ls.DirZ * u + ls.DownZ * v);
                        if (!w.Contains(wx, wz)) continue;
                        double lower = preBuildH[w.Index(wx, wz)] / 256.0 - w.HeightAt(wx, wz);
                        if (v >= -34) maxTrackLower = Math.Max(maxTrackLower, lower);
                        else maxCrownDrop = Math.Max(maxCrownDrop, lower);
                    }
                // Check crown drop across full upper headscarp zone (-72..-32)
                for (int v = -72; v <= -32; v++)
                    for (int u = -22; u <= 22; u++)
                    {
                        int wx = (int)Math.Round(ls.X + ls.DirX * u + ls.DownX * v);
                        int wz = (int)Math.Round(ls.Z + ls.DirZ * u + ls.DownZ * v);
                        if (!w.Contains(wx, wz)) continue;
                        double lower = preBuildH[w.Index(wx, wz)] / 256.0 - w.HeightAt(wx, wz);
                        maxCrownDrop = Math.Max(maxCrownDrop, lower);
                    }
                // Downhill debris tongue: 10..40 m past downhill road edge (~v in 13..43)
                int maxTongueWidth = 0;
                double maxTongueRaise = 0;
                for (int v = 13; v <= 43; v += 2)
                {
                    int widthHere = 0;
                    for (int u = -42; u <= 42; u++)
                    {
                        int wx = (int)Math.Round(ls.X + ls.DirX * u + ls.DownX * v);
                        int wz = (int)Math.Round(ls.Z + ls.DirZ * u + ls.DownZ * v);
                        if (!w.Contains(wx, wz)) continue;
                        double raise = w.HeightAt(wx, wz) - preBuildH[w.Index(wx, wz)] / 256.0;
                        if (raise > maxTongueRaise) maxTongueRaise = raise;
                        if (raise >= 1.5) widthHere++;
                    }
                    if (widthHere > maxTongueWidth) maxTongueWidth = widthHere;
                }
                Console.WriteLine(FormattableString.Invariant($"       bury slide: cliffRise24 {cliffRise24:0.0} m, trenchDip {trenchDip:0.00} m, trackLower {maxTrackLower:0.0} m, crownDrop {maxCrownDrop:0.0} m, downhillTongue raise {maxTongueRaise:0.0} m across {maxTongueWidth} m"));
                Check.True(maxTrackLower <= 5.0, $"Uphill debris slide track carved too deep ({maxTrackLower:0.0} m > 5.0 m)");
                Check.True(maxCrownDrop >= 4.0, $"Uphill debris slide upper headscarp too weak ({maxCrownDrop:0.0} m < 4.0 m)");
                Check.True(maxTongueRaise >= 1.5 && maxTongueWidth >= 15, $"Uphill debris slide downhill tongue too weak (max raise {maxTongueRaise:0.0} m, width>=1.5m {maxTongueWidth} m < 15 m)");
            }
        }

        // 4b-J. Landslide junction clearance check (Habegu-Land +222 m): EVERY landslide must leave >= 4 m of intact
        //       visible continuing gravel road past EVERY junction along the spiral (never looking like a curve!).
        VerifyLandslideJunctionClearance(w, spiralRes, new[] { (joinPt, joinDistOnSpiral, 2.5) }, buildRes.VolcanoLandslides, "Pad1");

        // 4c. Exact Undo
        edits.Undo();
        Check.Equal(stateBefore, Fixture.State(w), "Undo after building volcano POIs + spiral + 2 landslides must be exact");

        // 4d. Regression check (Habegu-Land +222 m): place a synthetic wilderness path joining the spiral RIGHT AT
        //     where the upper landslide was previously placed! Build MUST move/adapt the landslide so BOTH continuing
        //     legs of the spiral remain intact visible gravel for >= 4.5 m past the branch edge!
        {
            var prevUpper = buildRes.VolcanoLandslides.First(l => l.BuryFromAbove);
            double prevDist = ClosestDistOnRoad(spiralRes, prevUpper.X, prevUpper.Z);
            var sideEnd = new Point(prevUpper.X, prevUpper.Z);
            var sideStart = new Point(prevUpper.X + prevUpper.DownX * 26.0, prevUpper.Z + prevUpper.DownZ * 26.0);
            var extraPath = new WildPath
            {
                Points = new List<Point> { sideStart, new((sideStart.X + sideEnd.X) * 0.5, (sideStart.Y + sideEnd.Y) * 0.5), sideEnd },
                Target = plan.Paths.First(p => p.IsVolcanoSpiral),
                Width = 5.0,
                Length = 26.0
            };
            var planWithExtra = new WildernessPlan();
            planWithExtra.Pois.AddRange(plan.Pois);
            planWithExtra.Paths.AddRange(plan.Paths);
            planWithExtra.Paths.Add(extraPath);

            WildernessBuildResult buildExtra = null;
            edits.Run("volcano-extra-junc", "", ed => buildExtra = WildernessGenerator.Build(w, ed, planWithExtra, wspec, null));
            var spiralExtra = buildExtra.Built.First(b => b.Path.IsVolcanoSpiral).Road;
            double extraDist = ClosestDistOnRoad(spiralExtra, sideEnd.X, sideEnd.Y);
            double pad1ExtraDist = ClosestDistOnRoad(spiralExtra, joinPt.X, joinPt.Y);
            Check.Equal(2, buildExtra.VolcanoLandslides.Count, "Must still place 2 landslides when an extra wilderness path joins the upper spiral");
            VerifyLandslideJunctionClearance(w, spiralExtra, new[] { (joinPt, pad1ExtraDist, 2.5), (sideEnd, extraDist, 2.5) }, buildExtra.VolcanoLandslides, "ExtraUpperJunc");
            edits.Undo();
        }

        // 4e. Legacy shallow-RimConnector check (existing Habegu-Land map without regenerating world):
        //     Temporarily overwrite yamee_volcano.json's RimConnector with a ~10° grazing curve along the spiral,
        //     re-plan & build wilderness POIs, and verify it automatically rebuilds to >= 44° with NO edge step (> 0.65 m)!
        {
            var origConnector = site.RimConnector.ToList();
            try
            {
                var pEnd = origConnector[^1];
                var pStart = origConnector[0];
                double stx = sDx / sL, stz = sDz / sL;
                double inx = -stz, inz = stx;
                if (inx * (pStart.X - pEnd.X) + inz * (pStart.Z - pEnd.Z) < 0) { inx = -inx; inz = -inz; }
                double tSign = (pStart.X - pEnd.X) * stx + (pStart.Z - pEnd.Z) * stz >= 0 ? 1.0 : -1.0;
                double cx = pEnd.X + tSign * stx * 22.0 + inx * 3.0;
                double cz = pEnd.Z + tSign * stz * 22.0 + inz * 3.0;
                site.RimConnector = Enumerable.Range(0, 9).Select(b =>
                {
                    double t = b / 8.0, om = 1.0 - t;
                    return new VolcanoPoint(
                        Math.Round(om * om * pStart.X + 2 * om * t * cx + t * t * pEnd.X, 1),
                        Math.Round(om * om * pStart.Z + 2 * om * t * cz + t * t * pEnd.Z, 1),
                        pEnd.H);
                }).ToList();
                site.Save(dir);

                var wgLegacy = new WildernessGenerator(w, wspec, new WildernessRules(), cands, _ => null);
                var planLegacy = wgLegacy.Plan(null);
                WildernessBuildResult buildLegacy = null;
                edits.Run("volcano-legacy-rim", "", ed => buildLegacy = WildernessGenerator.Build(w, ed, planLegacy, wspec, null));
                var spLeg = buildLegacy.Built.First(b => b.Path.IsVolcanoSpiral).Road;
                var brLegBuilt = buildLegacy.Built.First(b => b.Path.IsVolcano && !b.Path.IsVolcanoSpiral);
                var brLeg = brLegBuilt.Road;
                var jLeg = brLegBuilt.Path.Points[^1];
                int bl1 = brLeg.Path.Count - 1, bl0 = Math.Max(0, bl1 - 6);
                double lbx = brLeg.Path[bl1].X - brLeg.Path[bl0].X, lbz = brLeg.Path[bl1].Z - brLeg.Path[bl0].Z;
                double lbl = Math.Max(1e-6, Math.Sqrt(lbx * lbx + lbz * lbz));
                int siLeg = spLeg.IndexAt(ClosestDistOnRoad(spLeg, jLeg.X, jLeg.Y));
                int siA = Math.Max(0, siLeg - 6), siB = Math.Min(spLeg.Path.Count - 1, siLeg + 6);
                double lsx = spLeg.Path[siB].X - spLeg.Path[siA].X, lsz = spLeg.Path[siB].Z - spLeg.Path[siA].Z;
                double lsl = Math.Max(1e-6, Math.Sqrt(lsx * lsx + lsz * lsz));
                double legAngle = Math.Acos(Math.Clamp(Math.Abs((lbx * lsx + lbz * lsz) / (lbl * lsl)), 0, 1)) * 180.0 / Math.PI;
                double maxLegEdge = 0;
                int ljx = (int)Math.Round(jLeg.X), ljz = (int)Math.Round(jLeg.Y);
                for (int dz = -18; dz <= 18; dz++)
                    for (int dx = -18; dx <= 18; dx++)
                    {
                        if (dx * dx + dz * dz > 18 * 18) continue;
                        int wx = ljx + dx, wz = ljz + dz;
                        if (!w.Contains(wx - 1, wz) || !w.Contains(wx + 1, wz) || !w.Contains(wx, wz - 1) || !w.Contains(wx, wz + 1)) continue;
                        if (w.RoadAt(wx, wz) != WorldData.Gravel) continue;
                        double h0 = w.HeightAt(wx, wz);
                        foreach (var (nx, nz) in new[] { (wx + 1, wz), (wx - 1, wz), (wx, wz + 1), (wx, wz - 1) })
                            maxLegEdge = Math.Max(maxLegEdge, Math.Abs(w.HeightAt(nx, nz) - h0));
                    }
                Console.WriteLine(FormattableString.Invariant($"       legacy shallow RimConnector rebuild: angle {legAngle:0.0} deg, maxEdgeStep {maxLegEdge:0.00} m"));
                Check.True(legAngle >= 42.0, $"Legacy shallow RimConnector must be rebuilt to >= 42 deg (got {legAngle:0.0} deg)");
                Check.True(maxLegEdge <= 0.65, $"Legacy shallow RimConnector left a {maxLegEdge:0.00} m edge step at the main road");
                edits.Undo();
            }
            finally
            {
                site.RimConnector = origConnector;
                site.Save(dir);
            }
        }

        // 5. Problem 1 check: pre-existing asphalt road crossing the lower volcano ramp -> gravel road terminates cleanly & 0 asphalt pixels damaged!
        int cutIdx = (int)(site.AccessPath.Count * 0.72);
        var cutPt = site.AccessPath[cutIdx];
        var prevPt = site.AccessPath[cutIdx - 1];
        double tx = cutPt.X - prevPt.X, tz = cutPt.Z - prevPt.Z;
        double tl = Math.Sqrt(tx * tx + tz * tz);
        double ax = -tz / tl, az = tx / tl; // perpendicular asphalt road across the lower ramp
        var asphaltPts = new List<Point>
        {
            new(cutPt.X - ax * 120, cutPt.Z - az * 120),
            new(cutPt.X, cutPt.Z),
            new(cutPt.X + ax * 120, cutPt.Z + az * 120)
        };
        var asphaltRes = RoadPlanner.Compute(w, asphaltPts, RoadSettings.Asphalt());
        edits.Run("test-asphalt", "", ed => RoadApplier.Apply(w, ed, asphaltRes, new Progress<string>()));

        var asphaltSnapshot = new List<(int Idx, ushort H, byte Road)>();
        for (int i = 0; i < w.RoadMask.Length; i++)
            if (w.RoadMask[i] == WorldData.Asphalt) asphaltSnapshot.Add((i, w.Heights[i], w.RoadMask[i]));
        Check.True(asphaltSnapshot.Count > 500, "Test asphalt road must be painted");

        var wg2 = new WildernessGenerator(w, wspec, new WildernessRules(), cands, _ => null);
        var plan2 = wg2.Plan(null);
        var spiralPlan2 = plan2.Paths.First(p => p.IsVolcanoSpiral);
        Check.Equal(WorldData.Asphalt, spiralPlan2.EndKind, "Spiral hitting pre-existing asphalt must terminate cleanly as asphalt junction");
        Check.True(spiralPlan2.Length < spiralTotal - 40, "Spiral must stop at first asphalt contact instead of continuing past it");

        WildernessBuildResult buildRes2 = null;
        edits.Run("volcano-wild-asphalt", "", ed => buildRes2 = WildernessGenerator.Build(w, ed, plan2, wspec, null));
        int asphaltDamaged = asphaltSnapshot.Count(a => w.RoadMask[a.Idx] != WorldData.Asphalt || Math.Abs(w.Heights[a.Idx] - a.H) > 256);
        Check.Equal(0, asphaltDamaged, "Volcano gravel road and landslides must not damage any asphalt road pixel");
        edits.Undo();
        edits.Undo();

        // 6. Bild 2 & +94m hairpin regression check: distant road ~300 m away from volcano foot (including when placed
        //    on the far side / opposite heading!) -> spiral MUST connect via FindPath with NO hairpin self-loop (HasSelfLoop == false),
        //    smooth spline seam, and NO >0.65 m vertical step across the built gravel roadway near the foot seam!
        var footPt = site.AccessPath[^1];
        double rvx = footPt.X - site.CenterX, rvz = footPt.Z - site.CenterZ;
        double rvl = Math.Sqrt(rvx * rvx + rvz * rvz);
        if (rvl < 1e-4) { rvx = 1; rvz = 0; } else { rvx /= rvl; rvz /= rvl; }
        double txR = -rvz, tzR = rvx;
        double farX = Math.Clamp(footPt.X + rvx * 300, -w.Half + 180, w.Half - 180);
        double farZ = Math.Clamp(footPt.Z + rvz * 300, -w.Half + 180, w.Half - 180);
        var farAsphalt = new List<Point>
        {
            new(farX - txR * 220, farZ - tzR * 220),
            new(farX, farZ),
            new(farX + txR * 220, farZ + tzR * 220)
        };
        var farRes = RoadPlanner.Compute(w, farAsphalt, RoadSettings.Asphalt());
        edits.Run("test-far-asphalt", "", ed => RoadApplier.Apply(w, ed, farRes, new Progress<string>()));
        var wg3 = new WildernessGenerator(w, wspec, new WildernessRules(), cands, _ => null);
        var plan3 = wg3.Plan(null);
        var spiralPlan3 = plan3.Paths.First(p => p.IsVolcanoSpiral);
        var endPt3 = spiralPlan3.Points[^1];
        bool endTouchesRoad = false;
        for (int dz = -6; dz <= 6 && !endTouchesRoad; dz++)
            for (int dx = -6; dx <= 6; dx++)
                if (w.Contains((int)Math.Round(endPt3.X) + dx, (int)Math.Round(endPt3.Y) + dz)
                    && w.RoadAt((int)Math.Round(endPt3.X) + dx, (int)Math.Round(endPt3.Y) + dz) == WorldData.Asphalt)
                    endTouchesRoad = true;
        bool hasSelfLoop3 = RoadNetwork.HasSelfLoop(NetworkBuilder.Even(spiralPlan3.Points, 12));
        // Build the connected spiral + tail and verify NO hairpin cliff step across the gravel surface near the foot seam!
        edits.Run("test-far-build", "", ed => WildernessGenerator.Build(w, ed, plan3, wspec, null));
        double maxFootGravelStep = 0;
        int fx0 = (int)Math.Round(footPt.X), fz0 = (int)Math.Round(footPt.Z);
        for (int dz = -35; dz <= 35; dz++)
            for (int dx = -35; dx <= 35; dx++)
            {
                int wx = fx0 + dx, wz = fz0 + dz;
                if (!w.Contains(wx, wz) || !w.Contains(wx + 1, wz) || !w.Contains(wx, wz + 1)) continue;
                if (w.RoadAt(wx, wz) != WorldData.Gravel) continue;
                double h0 = w.HeightAt(wx, wz);
                if (w.RoadAt(wx + 1, wz) == WorldData.Gravel) maxFootGravelStep = Math.Max(maxFootGravelStep, Math.Abs(w.HeightAt(wx + 1, wz) - h0));
                if (w.RoadAt(wx, wz + 1) == WorldData.Gravel) maxFootGravelStep = Math.Max(maxFootGravelStep, Math.Abs(w.HeightAt(wx, wz + 1) - h0));
            }
        Console.WriteLine(FormattableString.Invariant($"       distant road connection: spiral len {spiralPlan3.Length:0} m (base {spiralTotal:0} m), EndKind {spiralPlan3.EndKind}, touchesAsphalt {endTouchesRoad}, hasSelfLoop {hasSelfLoop3}, maxFootGravelStep {maxFootGravelStep:0.00} m"));
        Check.Equal(WorldData.Asphalt, spiralPlan3.EndKind, "Volcano spiral must connect to distant asphalt road via FindPath instead of ending dead");
        Check.True(endTouchesRoad, "Volcano spiral end point must actually reach the distant asphalt road");
        Check.True(!hasSelfLoop3, "Volcano spiral + connecting tail must NEVER self-loop or fold back into a hairpin (< 24 m)");
        Check.True(maxFootGravelStep <= 0.65, $"Volcano foot seam has a {maxFootGravelStep:0.00} m step across the gravel roadway (hairpin overlap!)");

        // 7. Regression check (Habegu-Land): RoadNetwork.HasSelfLoop must NEVER falsely reject straight 5 m RoadLine.Fit paths,
        //    and forcedVolcano Stage 2 (when spec.MaxPath < distance to road) MUST still connect cleanly!
        var straight5m = Enumerable.Range(0, 25).Select(i => new Point(i * 5.0, 0)).ToList();
        Check.True(!RoadNetwork.HasSelfLoop(straight5m), "HasSelfLoop must not falsely reject a straight 5 m RoadLine.Fit path");
        edits.Undo(); // undo test-far-build (keep test-far-asphalt!)
        var tightSpec = new WildernessSpec { Density = 0.4, MaxPath = 120, Seed = "HabeguCheck" };
        var wg4 = new WildernessGenerator(w, tightSpec, new WildernessRules(), cands, _ => null);
        var plan4 = wg4.Plan(null);
        var spiralPlan4 = plan4.Paths.First(p => p.IsVolcanoSpiral);
        Check.True(spiralPlan4.EndKind == WorldData.Asphalt || spiralPlan4.Target != null,
            $"Volcano spiral failed to connect in forcedVolcano mode (EndKind={spiralPlan4.EndKind}, Target={spiralPlan4.Target != null})");
        Check.True(!RoadNetwork.HasSelfLoop(spiralPlan4.Points), "Volcano forcedVolcano spline must not self-loop");
        edits.Undo(); // undo test-far-asphalt
    }

    [Test("Volcano: both landslides sit immediately before/after the middle POI and clear of city tiles")]
    static void LandslidesNearMiddlePoiAndClearOfCityTiles()
    {
        var cands = SyntheticMediumPois();
        var wspec = new WildernessSpec { Density = 0.0, Seed = "MidPoiSlide" };

        foreach (string seed in new[] { "Alpha", "Beta", "Gamma", "Delta" })
        {
            string dir = Path.Combine(Fixture.Temp, "VolcMid_" + seed);
            var gen = new WorldGenerator(new GeneratorSpec
            {
                Name = "VolcMid_" + seed,
                Size = 4096,
                Seed = seed,
                Lakes = 2,
                Rivers = 0,
                SpawnCount = 4
            });
            gen.Write(dir, null, new Progress<string>());
            var site = VolcanoSite.Load(dir);
            Check.True(site != null, $"{seed}: yamee_volcano.json missing");
            VerifyPlannedLandslidesNearMidPoi(site, "4096/" + seed);

            var w = WorldData.Load(dir, null, new Progress<string>());
            var wg = new WildernessGenerator(w, wspec, new WildernessRules(), cands, _ => null);
            var plan = wg.Plan(null);
            Check.Equal(2, plan.Pois.Count, $"{seed}: expected 2 volcano POIs");

            var edits = new WorldEdits(w);
            WildernessBuildResult res = null;
            edits.Run("volc-mid", "", ed => res = WildernessGenerator.Build(w, ed, plan, wspec, null));
            var spiral = res.Built.First(b => b.Path.IsVolcanoSpiral).Road;
            var branch = res.Built.First(b => b.Path.IsVolcano && !b.Path.IsVolcanoSpiral);
            var joinPt = branch.Path.Points[^1];
            double midDist = ClosestDistOnRoad(spiral, joinPt.X, joinPt.Y);

            Check.Equal(2, res.VolcanoLandslides.Count, $"{seed}: expected 2 landslides");
            Check.Equal(1, res.VolcanoLandslides.Count(l => l.BuryFromAbove), $"{seed}: expected 1 uphill debris slide");
            Check.Equal(1, res.VolcanoLandslides.Count(l => !l.BuryFromAbove), $"{seed}: expected 1 downhill slump");

            var rels = res.VolcanoLandslides.Select(ls => ClosestDistOnRoad(spiral, ls.X, ls.Z) - midDist).ToList();
            Console.WriteLine(FormattableString.Invariant($"       4096/{seed}: midPOI {midDist:0}/{spiral.Path[^1].Dist:0} m, landslides at {rels[0]:+0;-0} m ({(res.VolcanoLandslides[0].BuryFromAbove ? "bury" : "slump")}) and {rels[1]:+0;-0} m ({(res.VolcanoLandslides[1].BuryFromAbove ? "bury" : "slump")})"));
            Check.True(rels.All(d => Math.Abs(d) <= 115.0),
                $"{seed}: both landslides must sit immediately before/after middle POI (<= 115 m, got {rels[0]:+0;-0} m, {rels[1]:+0;-0} m)");
            Check.True(rels.Any(d => d < 0) && rels.Any(d => d > 0),
                $"{seed}: expected one landslide before and one after middle POI (got {rels[0]:+0;-0} m, {rels[1]:+0;-0} m)");
            VerifyLandslideJunctionClearance(w, spiral, new[] { (joinPt, midDist, 2.5) }, res.VolcanoLandslides, seed);

            // City-tile safety check on Alpha: place a 150x150 m city tile right downhill of the "after" landslide's
            // debris zone; Build must avoid placing debris into the city tile so the tile never cuts off the rubble!
            if (seed == "Alpha")
            {
                var afterSlide = res.VolcanoLandslides.First(ls => ClosestDistOnRoad(spiral, ls.X, ls.Z) > midDist);
                edits.Undo();

                var tileDef = new PrefabDef { SizeX = 150, SizeY = 10, SizeZ = 150, Kind = PrefabKind.Tile, Found = true };
                double tileCx = afterSlide.X + afterSlide.DownX * 115.0;
                double tileCz = afterSlide.Z + afterSlide.DownZ * 115.0;
                var cityTile = new PlacedPrefab
                {
                    Name = "rwg_tile_rural_straight",
                    Def = tileDef,
                    X = (int)Math.Round(tileCx - 75.0),
                    Z = (int)Math.Round(tileCz - 75.0),
                    Y = (int)Math.Round(w.HeightAt((int)Math.Round(tileCx), (int)Math.Round(tileCz)))
                };
                w.Prefabs.Add(cityTile);
                try
                {
                    WildernessBuildResult resTile = null;
                    edits.Run("volc-mid-tile", "", ed => resTile = WildernessGenerator.Build(w, ed, plan, wspec, null));
                    Check.Equal(2, resTile.VolcanoLandslides.Count, "Must still place 2 landslides when a city tile lies downhill");
                    foreach (var ls in resTile.VolcanoLandslides)
                    {
                        // Verify downhill debris zone (10..75 m) does not clip into the 150x150 m city tile!
                        bool clipsTile = false;
                        for (double v = 10; v <= 75 && !clipsTile; v += 10)
                            for (double u = -18; u <= 18 && !clipsTile; u += 10)
                            {
                                double px = ls.X + ls.DirX * u + ls.DownX * v;
                                double pz = ls.Z + ls.DirZ * u + ls.DownZ * v;
                                if (px >= cityTile.X - 6 && px <= cityTile.X + cityTile.SizeX + 6
                                    && pz >= cityTile.Z - 6 && pz <= cityTile.Z + cityTile.SizeZ + 6)
                                    clipsTile = true;
                            }
                        Check.True(!clipsTile, $"Landslide ({(ls.BuryFromAbove ? "bury" : "slump")}) debris zone clipped into city tile at ({cityTile.X},{cityTile.Z})");
                    }
                    edits.Undo();
                }
                finally
                {
                    w.Prefabs.Remove(cityTile);
                }
            }
            else
            {
                edits.Undo();
            }
        }
    }

    static void VerifyPlannedLandslidesNearMidPoi(VolcanoSite site, string label)
    {
        Check.Equal(2, site.Landslides.Count, $"{label}: must plan 2 landslides");
        var arc = new double[site.AccessPath.Count];
        for (int i = 1; i < site.AccessPath.Count; i++)
        {
            double dx = site.AccessPath[i].X - site.AccessPath[i - 1].X;
            double dz = site.AccessPath[i].Z - site.AccessPath[i - 1].Z;
            arc[i] = arc[i - 1] + Math.Sqrt(dx * dx + dz * dz);
        }
        double midArc = site.PodestDist;
        var rels = new List<double>();
        foreach (var ls in site.Landslides)
        {
            double bestD2 = double.MaxValue, bestA = 0;
            for (int i = 0; i < site.AccessPath.Count; i++)
            {
                double dx = site.AccessPath[i].X - ls.X, dz = site.AccessPath[i].Z - ls.Z;
                double d2 = dx * dx + dz * dz;
                if (d2 < bestD2) { bestD2 = d2; bestA = arc[i]; }
            }
            rels.Add(bestA - midArc);
        }
        Check.True(rels.All(d => Math.Abs(d) <= 125.0),
            $"{label}: planned landslides must sit immediately before/after Pad 1 (got {string.Join(", ", rels.Select(d => $"{d:+0;-0} m"))})");
        Check.True(rels.Any(d => d < 0) && rels.Any(d => d > 0),
            $"{label}: planned landslides should sit one before and one after Pad 1 (got {string.Join(", ", rels.Select(d => $"{d:+0;-0} m"))})");
    }

    static void VerifyLandslideJunctionClearance(
        WorldData w, RoadResult spiral, (Point Pt, double ArcDist, double RoadHalf)[] junctions,
        List<VolcanoLandslide> slides, string label)
    {
        double total = spiral.Path[^1].Dist;
        foreach (var j in junctions)
        {
            foreach (var ls in slides)
            {
                double dArc = Math.Abs(ClosestDistOnRoad(spiral, ls.X, ls.Z) - j.ArcDist);
                double dEuc = Math.Sqrt((ls.X - j.Pt.X) * (ls.X - j.Pt.X) + (ls.Z - j.Pt.Y) * (ls.Z - j.Pt.Y));
                double edgeClear = Math.Min(dArc, dEuc) - (j.RoadHalf + 1.5);
                double hl = ls.Length * 0.5;
                Check.True(edgeClear >= hl * 1.20 + 4.0,
                    $"{label}: landslide ({(ls.BuryFromAbove ? "bury" : "slump")}, len {ls.Length:0} m) too close to junction at ({j.Pt.X:0},{j.Pt.Y:0}): edgeClear {edgeClear:0.0} m < {hl * 1.20 + 4.0:0.0} m");
            }
            // Verify BOTH continuing legs of the spiral remain intact visible gravel for >= 4.5 m past the branch edge!
            double sFrom = Math.Max(0, j.ArcDist - (j.RoadHalf + 4.5));
            double sTo = Math.Min(total, j.ArcDist + (j.RoadHalf + 4.5));
            for (double s = sFrom; s <= sTo; s += 1.0)
            {
                int idx = spiral.IndexAt(s);
                int wx = (int)Math.Round(spiral.Path[idx].X), wz = (int)Math.Round(spiral.Path[idx].Z);
                if (!w.Contains(wx, wz)) continue;
                Check.Equal(WorldData.Gravel, w.RoadAt(wx, wz),
                    $"{label}: spiral gravel erased at dist {s - j.ArcDist:+0.0;-0.0} m from junction ({j.Pt.X:0},{j.Pt.Y:0}) — must keep >= 4.5 m visible past branch edge");
                double hDiff = Math.Abs(w.HeightAt(wx, wz) - spiral.Profile[idx]);
                Check.True(hDiff <= 0.45,
                    $"{label}: landslide deformed spiral by {hDiff:0.00} m at dist {s - j.ArcDist:+0.0;-0.0} m from junction ({j.Pt.X:0},{j.Pt.Y:0})");
            }
        }
    }

    static double RoadProfileAt(RoadResult r, double dist)
    {
        for (int i = 1; i < r.Path.Count; i++)
            if (r.Path[i].Dist >= dist)
            {
                double t = Math.Clamp((dist - r.Path[i - 1].Dist) / Math.Max(1e-6, r.Path[i].Dist - r.Path[i - 1].Dist), 0, 1);
                return r.Profile[i - 1] + (r.Profile[i] - r.Profile[i - 1]) * t;
            }
        return r.Profile[^1];
    }

    static double ClosestDistOnRoad(RoadResult r, double x, double z)
    {
        double bestD2 = double.MaxValue, bestDist = 0;
        for (int i = 0; i < r.Path.Count; i++)
        {
            double dx = r.Path[i].X - x, dz = r.Path[i].Z - z;
            double d2 = dx * dx + dz * dz;
            if (d2 < bestD2) { bestD2 = d2; bestDist = r.Path[i].Dist; }
        }
        return bestDist;
    }

    static double MaxRoadCrossSlope(WorldData w, RoadResult r, double s0, double s1, double halfSpan)
    {
        double maxCross = 0;
        for (int i = 1; i < r.Path.Count - 1; i++)
        {
            double d = r.Path[i].Dist;
            if (d < s0 || d > s1) continue;
            double tx = r.Path[i + 1].X - r.Path[i - 1].X, tz = r.Path[i + 1].Z - r.Path[i - 1].Z;
            double tl = Math.Sqrt(tx * tx + tz * tz);
            if (tl < 1e-4) continue;
            double nx = -tz / tl, nz = tx / tl;
            double hL = w.HeightAt((int)Math.Round(r.Path[i].X - nx * halfSpan), (int)Math.Round(r.Path[i].Z - nz * halfSpan));
            double hR = w.HeightAt((int)Math.Round(r.Path[i].X + nx * halfSpan), (int)Math.Round(r.Path[i].Z + nz * halfSpan));
            double cross = Math.Abs(hR - hL) / (2.0 * halfSpan);
            if (cross > maxCross) maxCross = cross;
        }
        return maxCross;
    }

    static double MaxNonUphillSlopeNear(WorldData w, VolcanoSite site, double jx, double jz, int radius)
    {
        double radX = jx - site.CenterX, radZ = jz - site.CenterZ;
        double radL = Math.Sqrt(radX * radX + radZ * radZ);
        if (radL < 1e-4) return 0;
        radX /= radL; radZ /= radL; // outward = downhill from volcano center
        int cx = (int)Math.Round(jx), cz = (int)Math.Round(jz);
        double maxDeg = 0;
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dz * dz > radius * radius) continue;
                // Exclude uphill cut-bank (inward toward volcano crater center by > 4 m)
                double radialDot = dx * radX + dz * radZ;
                if (radialDot < -4.0) continue;
                int wx = cx + dx, wz = cz + dz;
                if (!w.Contains(wx - 1, wz) || !w.Contains(wx + 1, wz) || !w.Contains(wx, wz - 1) || !w.Contains(wx, wz + 1)) continue;
                double gx = (w.HeightAt(wx + 1, wz) - w.HeightAt(wx - 1, wz)) * 0.5;
                double gz = (w.HeightAt(wx, wz + 1) - w.HeightAt(wx, wz - 1)) * 0.5;
                double deg = Math.Atan(Math.Sqrt(gx * gx + gz * gz)) * 180.0 / Math.PI;
                if (deg > maxDeg) maxDeg = deg;
            }
        return maxDeg;
    }
}
