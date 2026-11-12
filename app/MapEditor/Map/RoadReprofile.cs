using System.Windows;

namespace MapEditor.Map;

/// settings for "Edit road"
public sealed class ReprofileOptions
{
    /// true: intersections stay at their old height (the road runs through there); false: crossing roads are always pulled along
    /// (the user's request 2026-11-03: no restriction for intersections); only in front of a city tile the change becomes smaller
    public bool KeepJunctions;
    /// true: even slope between the markers (hills disappear); false: shape of the road stays, it is only
    /// raised or lowered
    public bool Even = true;
    /// calculate banking in curves automatically from the course of the road (only two markers needed); values entered by hand
    /// at markers take precedence. AutoCrossMax = highest banking (fraction)
    public bool AutoCross;
    public double AutoCrossMax = 0.08;
}

/// a branch of an intersection: the crossing road from the intersection outwards
public sealed class BranchArm
{
    public List<Point> Points = new();   // from the intersection on the edited road (every 2 m)
    public double Width;                 // measured width (surface)
    public double Length;                // length from the intersection (m)
    public string Stop;                  // null = full length, "end", "tile", "junction"
    public double Start;                 // up to here (from the intersection) the full height change applies: area of the edited road
    public double Fade;                  // length over which the change runs out (m)
    /// room for the transition: in front of a city tile or another intersection 6 m less (nothing may arrive there any more)
    public double Available => Length - Start - (Stop is "tile" or "junction" ? 6 : 0);
}

/// entrance of a building or road exit of a tile beside the edited road: the road keeps its old height there
/// because the building/tile does not move along (otherwise a step between driveway and road)
public sealed class AnchorPlan
{
    public double Dist;
    public double PinHalf;
    public string Name;
    public bool IsTile;
    public bool Pinnable = true;
}

/// intersection or junction on the edited stretch
public sealed class JunctionPlan
{
    public double Dist;
    public Point Center;
    public byte Kind;
    public bool Through;
    public List<BranchArm> Arms = new();
    public double Delta;       // height change of the edited road at this place (m)
    /// null = the road follows the change freely; otherwise highest change at this intersection (m, signed): 0 = height stays
    /// (the user's request), ≠ 0 = reduced because there is hardly room for the transition in front of a city tile
    public double? Limit;
    public bool Pinned => Limit == 0;
    public bool Limited => Limit != null && Limit != 0;
    public bool Pinnable = true;
    public string Why;         // reason if held or reduced
    public double PinHalf;     // so far (along the road) the limitation applies
    public bool Followed => !Pinned && Math.Abs(Delta) >= 0.03;
}

/// calculated change: course of the road, old and new height, intersections
public sealed class ReprofilePlan
{
    public string Error;
    public List<Point> Control;                 // control points with run-out before and after
    public List<PathPoint> Path;
    public int[] MarkerIndex;                   // waypoint per marker
    public int CoreFrom, CoreTo;                // waypoints of the first and last marker
    public double[] Old, OldSmooth, New, Delta, Blend;
    public double RoadHalf, Edge;               // half width of the surface (measured) or of the area with full change
    public byte Kind;
    public double[] MarkerOld, MarkerNew;       // height of the road at the markers before/after
    public double[] SegmentLength;              // length between marker i and i+1
    public double RunIn, RunOut;                // length of the run-out before the first / after the last marker
    public List<JunctionPlan> Junctions = new();
    public List<AnchorPlan> Anchors = new();   // entrances of buildings and tile exits beside the road
    public List<string> Warnings = new();
    public int AutoCurves;                       // number of curves with automatic banking
    public double MaxGrade, MaxGradeAt;
    /// at least one marker has a typed height; otherwise the road stays unchanged
    public bool AnyTarget;
    /// banking (superelevation, the user's request 2026-11-03): height difference per meter across the road, + = left side
    /// (seen in the direction of the markers) higher. OldCross = existing banking of the road, CrossDelta = change per waypoint
    public bool AnyCross;
    public double[] OldCross, CrossDelta, MarkerCrossOld, MarkerCrossNew;
    public bool[] MarkerCrossManual;   // banking of the marker typed by hand (takes precedence over the automatic)
    public double MaxCross;   // largest banking after the change (absolute value)
    public bool Changes => Error == null && (Delta != null && Delta.Any(d => Math.Abs(d) >= 0.02)
                                            || CrossDelta != null && CrossDelta.Any(c => Math.Abs(c) * Edge >= 0.02));
    public double Length => Path[^1].Dist;

    /// result for map and height profile (road in blue, run-out purple)
    public RoadResult ToResult()
    {
        var s = RoadSettings.For(Kind == WorldData.Asphalt ? Surface.Asphalt : Surface.Gravel);
        s.Width = Edge * 2;
        s.Shoulder = 0;
        var r = new RoadResult
        {
            Settings = s, Path = Path, Profile = New, Terrain = Old, SlopeWidth = Blend, ControlIndex = MarkerIndex,
            Length = Length, EditLook = true, CoreFrom = CoreFrom, CoreTo = CoreTo, MaxGrade = MaxGrade,
        };
        foreach (var j in Junctions)
        {
            int i = Math.Min(Path.Count - 1, IndexAt(j.Dist));
            r.Crossings.Add(new Crossing(j.Dist, j.Center.X, j.Center.Y, j.Kind, New[i], Old[i], 0, true));
            if (j.Pinned || j.Limited) r.HeldPoints.Add(j.Center);
        }
        return r;
    }

    public int IndexAt(double dist)
    {
        int lo = 0, hi = Path.Count - 1;
        while (lo < hi) { int m = (lo + hi) / 2; if (Path[m].Dist < dist) lo = m + 1; else hi = m; }
        return lo;
    }
}

/// "Edit road" (the user's request 2026-11-02): the user places markers in the middle of an existing road and defines
/// heights or slopes for them. The road between the markers gets the new longitudinal profile, before and after the change runs
/// out softly. Crossing roads follow the change (if they can do so without steep stretches), otherwise the intersection stays at the old height.
public static class RoadReprofile
{
    /// so many meters of run-out per meter of height change (peak gradient of the run-out ≈ 1.5 / 37.5 = 4 %)
    const double RunPerMeter = 37.5;
    const double MaxRun = 150, MinRun = 20, MinArm = 12;
    /// branching roads: so many meters of transition per meter of height change (softer than the run-out of the main road;
    /// peak ≈ 1.5 / 60 = 2.5 % additional gradient), at most MaxArm meters
    const double ArmPerMeter = 60, MaxArm = 250;
    /// so long the banking runs out before the first and after the last marker (m)
    const double CrossRun = 30;

    /// calculate the plan. targets: desired height per marker (null = stays). cross: desired banking per marker
    /// (fraction, null = stays). cache: remembered tracings between markers.
    public static ReprofilePlan Plan(WorldData w, IReadOnlyList<Point> markers, IReadOnlyList<double?> targets, ReprofileOptions o,
        Dictionary<(double, double, double, double), TraceResult> cache = null, RoadScan scan = null, IReadOnlyList<double?> cross = null)
    {
        var plan = new ReprofilePlan();
        if (markers.Count < 2) { plan.Error = Loc.T("Set at least two markers on the road."); return plan; }
        scan ??= new RoadScan(w);

        // 1. stretch between the markers (following the road center)
        var core = new List<Point>();
        var coreIdx = new List<int> { 0 };
        var widths = new List<double>();
        int asphalt = 0, gravel = 0;
        for (int i = 0; i + 1 < markers.Count; i++)
        {
            var key = (markers[i].X, markers[i].Y, markers[i + 1].X, markers[i + 1].Y);
            TraceResult t = null;
            if (cache == null || !cache.TryGetValue(key, out t))
            {
                t = RoadTracer.Trace(w, markers[i], markers[i + 1], out string err);
                if (t == null) { plan.Error = err; return plan; }
                if (cache != null) cache[key] = t;
            }
            if (t.Points.Count < 2 || t.Length < 1) { plan.Error = Loc.T("Two markers are too close together."); return plan; }
            int from = core.Count > 0 && (t.Points[0] - core[^1]).Length < 2.5 ? 1 : 0;
            for (int k = from; k < t.Points.Count; k++) core.Add(t.Points[k]);
            coreIdx.Add(core.Count - 1);
            widths.Add(t.Width);
            if (t.Kind == Surface.Asphalt) asphalt++; else gravel++;
        }
        widths.Sort();
        double width = widths[widths.Count / 2];
        plan.RoadHalf = Math.Max(2, (width - 2) / 2);
        plan.Edge = plan.RoadHalf + 1;
        plan.Kind = asphalt >= gravel ? WorldData.Asphalt : WorldData.Gravel;

        // 2. run-out before and after: as long as the change at the outer markers demands
        var corePath = RoadPlanner.BuildPath(core);
        var coreTerrain = Terrain(w, corePath);
        var coreSmooth = RoadPlanner.MovingAverage(corePath, coreTerrain, 6);
        var ci0 = ControlIndex(corePath, core.Count);
        double OldAt(int m) => coreSmooth[ci0[coreIdx[m]]];
        double d0 = Target(targets, 0, OldAt(0)) - OldAt(0), d1 = Target(targets, markers.Count - 1, OldAt(markers.Count - 1)) - OldAt(markers.Count - 1);
        double RunFor(double d) => Math.Abs(d) < 0.03 ? 0 : Math.Clamp(Math.Abs(d) * RunPerMeter, MinRun, MaxRun);
        // direction in which the road continues before the first or behind the last marker (10 m measuring stretch)
        var first = corePath[0];
        var beforeFirst = corePath[Math.Min(corePath.Count - 1, 40)];
        var last = corePath[^1];
        var beforeLast = corePath[Math.Max(0, corePath.Count - 41)];
        var dirStart = new Vector(first.X - beforeFirst.X, first.Z - beforeFirst.Z);
        var dirEnd = new Vector(last.X - beforeLast.X, last.Z - beforeLast.Z);
        // the banking also runs out before the first / after the last marker
        var coreCross = OldCross(w, corePath, Math.Max(1, plan.RoadHalf - 0.5));
        double CrossRunFor(int mk)
        {
            double was = coreCross[ci0[coreIdx[mk]]];
            return o.AutoCross || Math.Abs(Target(cross, mk, was) - was) >= 0.003 ? CrossRun : 0;
        }
        var ext0 = RoadFollower.Extend(scan, core[0], dirStart, Math.Max(RunFor(d0), CrossRunFor(0)), width, false);
        var ext1 = RoadFollower.Extend(scan, core[^1], dirEnd, Math.Max(RunFor(d1), CrossRunFor(markers.Count - 1)), width, false);
        var full = new List<Point>(ext0.Points.Count + core.Count + ext1.Points.Count);
        for (int k = ext0.Points.Count - 1; k >= 0; k--) full.Add(ext0.Points[k]);
        int shift = full.Count;
        full.AddRange(core);
        full.AddRange(ext1.Points);
        plan.Control = full;
        var path = plan.Path = RoadPlanner.BuildPath(full);
        int n = path.Count;
        var ci = ControlIndex(path, full.Count);
        plan.MarkerIndex = coreIdx.Select(c => ci[shift + c]).ToArray();
        plan.CoreFrom = plan.MarkerIndex[0];
        plan.CoreTo = plan.MarkerIndex[^1];
        plan.RunIn = path[plan.CoreFrom].Dist;
        plan.RunOut = path[n - 1].Dist - path[plan.CoreTo].Dist;

        // 3. old heights and target heights of the markers
        var old = plan.Old = Terrain(w, path);
        var oldS = plan.OldSmooth = RoadPlanner.MovingAverage(path, old, 6);
        int m = markers.Count;
        plan.AnyTarget = targets != null && targets.Any(t => t != null);
        plan.MarkerOld = new double[m];
        plan.MarkerNew = new double[m];
        plan.SegmentLength = new double[m - 1];
        plan.OldCross = OldCross(w, path, Math.Max(1, plan.RoadHalf - 0.5));
        plan.AnyCross = o.AutoCross || cross != null && cross.Any(t => t != null);
        plan.MarkerCrossOld = new double[m];
        plan.MarkerCrossNew = new double[m];
        plan.MarkerCrossManual = new bool[m];
        for (int k = 0; k < m; k++) plan.MarkerCrossManual[k] = cross != null && k < cross.Count && cross[k] != null;
        for (int k = 0; k < m; k++)
        {
            plan.MarkerOld[k] = oldS[plan.MarkerIndex[k]];
            plan.MarkerNew[k] = Target(targets, k, plan.MarkerOld[k]);
            plan.MarkerCrossOld[k] = plan.OldCross[plan.MarkerIndex[k]];
            plan.MarkerCrossNew[k] = Target(cross, k, plan.MarkerCrossOld[k]);
            if (k > 0) plan.SegmentLength[k - 1] = path[plan.MarkerIndex[k]].Dist - path[plan.MarkerIndex[k - 1]].Dist;
        }
        if (ext0.Length < RunFor(d0) - 4 && RunFor(d0) > 0)
            plan.Warnings.Add(Loc.F("Before the first marker there is only {0:0} m of road for the run-out ({1}).", ext0.Length, StopText(ext0.Stop)));
        if (ext1.Length < RunFor(d1) - 4 && RunFor(d1) > 0)
            plan.Warnings.Add(Loc.F("After the last marker there is only {0:0} m of road for the run-out ({1}).", ext1.Length, StopText(ext1.Stop)));

        // 4. intersections
        plan.Junctions = FindJunctions(scan, path, plan.RoadHalf);
        foreach (var j in plan.Junctions)
        {
            j.PinHalf = Math.Max(j.Arms.Count > 0 ? j.Arms.Max(a => a.Width) / 2 : 4, 3) + 3;
            // too close to a marker: the height cannot be held there
            foreach (int mi in plan.MarkerIndex)
                if (Math.Abs(path[mi].Dist - j.Dist) < j.PinHalf + 2) j.Pinnable = false;
            if (j.Dist < plan.RunIn || j.Dist > path[n - 1].Dist - plan.RunOut) j.Pinnable = false;   // in the run-out there is no hold point
            if (o.KeepJunctions && j.Pinnable) { j.Limit = 0; j.Why = Loc.T("kept as chosen"); }
        }
        // entrances of buildings and tile exits beside the road: the road stays at its old height there
        plan.Anchors = FindAnchors(w, plan);

        // 5. profile. Crossing roads always follow the change (no restriction, the user's request); only if a city tile begins right behind
        // and there is no room for a soft transition, the change becomes smaller at this intersection
        for (int iter = 0; iter < 6; iter++)
        {
            Profile(plan, o);
            bool changed = false;
            foreach (var j in plan.Junctions)
            {
                j.Delta = plan.Delta[plan.IndexAt(j.Dist)];
                if (j.Limit != null || !j.Pinnable) continue;
                double? allowed = TileAllowed(j);
                if (allowed is not double al || Math.Abs(j.Delta) <= al + 1e-6) continue;
                j.Limit = Math.Sign(j.Delta) * al;
                j.Why = Loc.T("a city tile is close");
                changed = true;
            }
            if (!changed) break;
        }
        foreach (var j in plan.Junctions)
        {
            if (j.Pinned || Math.Abs(j.Delta) < 0.05) continue;
            foreach (var a in j.Arms)
            {
                double needed = Math.Clamp(Math.Abs(j.Delta) * ArmPerMeter, MinArm, MaxArm);
                a.Fade = Math.Max(1, Math.Min(needed, a.Available));
            }
            double peak = j.Arms.Count == 0 ? 0 : j.Arms.Max(a => 1.5 * Math.Abs(j.Delta) / a.Fade);
            if (peak > 0.10)
                plan.Warnings.Add(Loc.F("Junction at {0:0}, {1:0}: the crossing road has little room, so it climbs steeply there (up to {2:0} %).", j.Center.X, j.Center.Y, peak * 100));
            if (j.Limit == null && !j.Pinnable && TileAllowed(j) is double al2 && Math.Abs(j.Delta) > al2 + 1e-6)
                plan.Warnings.Add(Loc.F("Junction at {0:0}, {1:0}: a city tile is close behind it, the change cannot be reduced here.", j.Center.X, j.Center.Y));
        }
        foreach (var a in plan.Anchors.Where(a => !a.Pinnable))
            if (Math.Abs(plan.Delta[plan.IndexAt(a.Dist)]) > 0.1)
                plan.Warnings.Add(Loc.F("{0} next to the road should keep its height, but a marker or the run-out lies there.", AnchorName(a)));

        // 6. banking, evaluation: slope width, steepest place, buildings nearby
        CrossProfile(plan, o);
        plan.Blend = SlopeWidths(plan);
        Grade(plan);
        if (plan.MaxGrade > 0.12)
            plan.Warnings.Add(Loc.F("The road becomes up to {0:0} % steep (at {1:#,0} m).", plan.MaxGrade * 100, plan.MaxGradeAt));
        if (plan.MaxCross > 0.12)
            plan.Warnings.Add(Loc.F("The banking becomes up to {0:0} % (across the road).", plan.MaxCross * 100));
        Obstacles(w, plan);
        return plan;
    }

    static string StopText(string stop) => stop switch
    {
        "end" => Loc.T("the road ends"),
        "tile" => Loc.T("a city tile begins"),
        "junction" => Loc.T("a junction"),
        _ => Loc.T("limit"),
    };

    static double Target(IReadOnlyList<double?> targets, int i, double fallback) =>
        targets != null && i < targets.Count && targets[i] is double t ? t : fallback;

    static double[] Terrain(WorldData w, List<PathPoint> path)
    {
        var t = new double[path.Count];
        for (int i = 0; i < t.Length; i++) t[i] = w.HeightAt((int)Math.Floor(path[i].X), (int)Math.Floor(path[i].Z));
        return t;
    }

    static int[] ControlIndex(List<PathPoint> path, int count)
    {
        var ci = new int[count];
        for (int i = 0; i < path.Count; i++)
            if (path[i].Segment + 1 < count) ci[path[i].Segment + 1] = i;
        for (int k = 1; k < count; k++) ci[k] = Math.Max(ci[k], ci[k - 1]);
        return ci;
    }

    static double Fade(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }

    /// softer running out (smoother in gradient and curvature than Fade): 1 → 0 with 6t⁵ − 15t⁴ + 10t³
    static double Fade5(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 1 - t * t * t * (t * (t * 6 - 15) + 10);
    }

    /// highest change at an intersection if a city tile begins right behind it: the transition of the crossing road may
    /// have at most 6 % additional gradient there (peak 1.5 × change / room). Null = no tile behind, no limit
    static double? TileAllowed(JunctionPlan j)
    {
        double? best = null;
        foreach (var a in j.Arms)
        {
            if (a.Stop != "tile") continue;
            double al = 0.06 * Math.Max(a.Available, 0) / 1.5;
            best = best == null ? al : Math.Min(best.Value, al);
        }
        return best;
    }

    static string AnchorName(AnchorPlan a) => a.IsTile ? Loc.F("The road exit of tile {0}", a.Name) : Loc.F("The entrance of {0}", a.Name);

    /// entrances and tile exits at most RoadHalf + 7 m beside the stretch
    static List<AnchorPlan> FindAnchors(WorldData w, ReprofilePlan p)
    {
        var res = new List<AnchorPlan>();
        var path = p.Path;
        double reach = p.RoadHalf + 7, pad = 40;
        double x0 = path.Min(q => q.X) - pad, x1 = path.Max(q => q.X) + pad, z0 = path.Min(q => q.Z) - pad, z1 = path.Max(q => q.Z) + pad;
        var exits = PrefabParts.AllExitsWithWidth(w, pf => pf.X <= x1 && pf.X + pf.SizeX >= x0 && pf.Z <= z1 && pf.Z + pf.SizeZ >= z0);
        foreach (var e in exits)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int k = 0; k < path.Count; k += 4)
            {
                double d = (path[k].X - e.X) * (path[k].X - e.X) + (path[k].Z - e.Z) * (path[k].Z - e.Z);
                if (d < bd) { bd = d; best = k; }
            }
            if (best < 0 || Math.Sqrt(bd) > reach) continue;
            var a = new AnchorPlan { Dist = path[best].Dist, PinHalf = Math.Max(3, e.Width / 2.0) + 3, Name = e.Owner.Name, IsTile = e.Owner.IsTile };
            if (res.Any(r => Math.Abs(r.Dist - a.Dist) < 6)) continue;
            foreach (int mi in p.MarkerIndex)
                if (Math.Abs(path[mi].Dist - a.Dist) < a.PinHalf + 2) a.Pinnable = false;
            if (a.Dist < p.RunIn || a.Dist > path[^1].Dist - p.RunOut) a.Pinnable = false;
            res.Add(a);
        }
        res.Sort((x, y) => x.Dist.CompareTo(y.Dist));
        return res;
    }

    // ---------- Profile ----------

    /// new height and change per waypoint. Between the markers a smooth curve through the target heights (and the held
    /// intersections at their old height), before and after the change of the outer marker runs out softly.
    static void Profile(ReprofilePlan p, ReprofileOptions o)
    {
        var path = p.Path;
        int n = path.Count;
        p.New = new double[n];
        p.Delta = new double[n];
        if (!p.AnyTarget)
        {
            // nothing entered yet: the road stays as it is
            Array.Copy(p.Old, p.New, n);
            return;
        }
        double sFirst = path[p.CoreFrom].Dist, sLast = path[p.CoreTo].Dist;
        // nodes: markers (target height or change) and held intersections (old height or change 0)
        var knots = new List<(double S, double V, bool Marker)>();
        for (int k = 0; k < p.MarkerIndex.Length; k++)
        {
            int mi = p.MarkerIndex[k];
            knots.Add((path[mi].Dist, o.Even ? p.MarkerNew[k] : p.MarkerNew[k] - p.MarkerOld[k], true));
        }
        // limited intersections (Limit = allowed change, 0 = height stays) and entrances (always 0)
        var holds = p.Junctions.Where(j => j.Limit != null && j.Pinnable).Select(j => (j.Dist, j.PinHalf, Delta: j.Limit.Value))
            .Concat(p.Anchors.Where(a => a.Pinnable).Select(a => (a.Dist, a.PinHalf, Delta: 0.0)));
        foreach (var (hd, half, hdelta) in holds)
            foreach (double off in new[] { -half, 0, half })
            {
                double s = hd + off;
                if (s <= sFirst + 1 || s >= sLast - 1) continue;
                knots.Add((s, o.Even ? p.OldSmooth[p.IndexAt(s)] + hdelta : hdelta, false));
            }
        knots.Sort((a, b) => a.S.CompareTo(b.S));
        var clean = new List<(double S, double V)>();
        foreach (var (s, v, marker) in knots)
        {
            if (clean.Count > 0 && s - clean[^1].S < 1.0)
            {
                if (marker) clean[^1] = (s, v);   // marker takes precedence over a hold point
                continue;
            }
            clean.Add((s, v));
        }
        var curve = new Pchip(clean);
        for (int i = 0; i < n; i++)
        {
            double s = path[i].Dist;
            if (s >= sFirst && s <= sLast)
            {
                double c = curve.At(s);
                p.Delta[i] = o.Even ? c - p.Old[i] : c;
            }
        }
        double endIn = p.Delta[p.CoreFrom], endOut = p.Delta[p.CoreTo];
        for (int i = 0; i < p.CoreFrom; i++) p.Delta[i] = endIn * Fade((sFirst - path[i].Dist) / Math.Max(1e-6, sFirst));
        for (int i = p.CoreTo + 1; i < n; i++) p.Delta[i] = endOut * Fade((path[i].Dist - sLast) / Math.Max(1e-6, path[n - 1].Dist - sLast));
        for (int i = 0; i < n; i++) p.New[i] = p.Old[i] + p.Delta[i];
    }

    /// existing banking per waypoint: height left minus right of the center line (half = distance to the center, still on the surface)
    /// divided by the distance, averaged over 10 m (the heights lie in the meter grid)
    static double[] OldCross(WorldData w, List<PathPoint> path, double half)
    {
        var c = new double[path.Count];
        for (int i = 0; i < c.Length; i++)
        {
            var (nx, nz) = RoadPlanner.Normal(path, i);
            double hl = w.HeightAt((int)Math.Floor(path[i].X + nx * half), (int)Math.Floor(path[i].Z + nz * half));
            double hr = w.HeightAt((int)Math.Floor(path[i].X - nx * half), (int)Math.Floor(path[i].Z - nz * half));
            c[i] = (hl - hr) / (2 * half);
        }
        return RoadPlanner.MovingAverage(path, c, 10);
    }

    /// banking by the curvature of the center line (like on a race track): in curves the road tilts to the inside of the curve
    /// (inside lower, outside higher), on straights 0. Banking = maximum value at curve radius ≤ 80 m, at a larger radius
    /// proportionally less; very wide curves (radius > about 500 m) stay level. The curvature is averaged over 40 m, so
    /// the slope builds up softly already before the curve and softly down afterwards. + = left side higher.
    static double[] AutoBanking(ReprofilePlan p, ReprofileOptions o)
    {
        var path = p.Path;
        int n = path.Count;
        const double MinRadius = 80, Dead = 0.015;
        double max = Math.Max(0.005, o.AutoCrossMax);
        // driving direction (mathematical angle, counterclockwise = increasing), over ±1 m, without a jump at ±180°
        var theta = new double[n];
        for (int i = 0; i < n; i++)
        {
            var a = path[Math.Max(0, i - 4)]; var b = path[Math.Min(n - 1, i + 4)];
            theta[i] = Math.Atan2(b.Z - a.Z, b.X - a.X);
            if (i > 0) theta[i] += Math.Round((theta[i - 1] - theta[i]) / (2 * Math.PI)) * 2 * Math.PI;
        }
        var kappa = new double[n];
        int step = 80;   // 20 m per side
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - step), b = Math.Min(n - 1, i + step);
            double ds = path[b].Dist - path[a].Dist;
            kappa[i] = ds > 1 ? (theta[b] - theta[a]) / ds : 0;
        }
        kappa = RoadPlanner.MovingAverage(path, kappa, 40);
        var res = new double[n];
        int curves = 0;
        bool inCurve = false;
        for (int i = 0; i < n; i++)
        {
            double c = -kappa[i] * MinRadius * max;
            c = Math.Clamp(c, -max, max);
            double a = Math.Abs(c);
            res[i] = Math.Sign(c) * Math.Max(0, a - Dead) * max / Math.Max(1e-6, max - Dead);
            if (i >= p.CoreFrom && i <= p.CoreTo)
            {
                bool now = Math.Abs(res[i]) > 0.02;
                if (now && !inCurve) curves++;
                inCurve = now;
            }
        }
        p.AutoCurves = curves;
        return res;
    }

    /// banking: smooth curve through the target values of the markers (unchanged ones keep their old value), before and after
    /// it runs out over at most CrossRun meters; at intersections it runs out (the crossing road stays level)
    static void CrossProfile(ReprofilePlan p, ReprofileOptions o)
    {
        var path = p.Path;
        int n = path.Count;
        p.CrossDelta = new double[n];
        p.MaxCross = 0;
        if (!p.AnyCross) return;
        double sFirst = path[p.CoreFrom].Dist, sLast = path[p.CoreTo].Dist;
        if (o.AutoCross)
        {
            // automatic: banking from the curvature of the road; values typed by hand at markers pull it there
            // (influence 40 m wide, running out softly)
            var auto = AutoBanking(p, o);
            for (int i = p.CoreFrom; i <= p.CoreTo; i++)
            {
                double c = auto[i];
                for (int k = 0; k < p.MarkerIndex.Length; k++)
                {
                    if (!p.MarkerCrossManual[k]) continue;
                    double diff = p.MarkerCrossNew[k] - auto[p.MarkerIndex[k]];
                    c += diff * Fade5(Math.Abs(path[i].Dist - path[p.MarkerIndex[k]].Dist) / 40);
                }
                p.CrossDelta[i] = c - p.OldCross[i];
            }
        }
        else
        {
            var knots = new List<(double S, double V)>();
            for (int k = 0; k < p.MarkerIndex.Length; k++)
            {
                double s = path[p.MarkerIndex[k]].Dist;
                if (knots.Count > 0 && s - knots[^1].S < 1.0) continue;
                knots.Add((s, o.Even ? p.MarkerCrossNew[k] : p.MarkerCrossNew[k] - p.MarkerCrossOld[k]));
            }
            var curve = new Pchip(knots);
            for (int i = 0; i < n; i++)
            {
                double s = path[i].Dist;
                if (s >= sFirst && s <= sLast)
                {
                    double c = curve.At(s);
                    p.CrossDelta[i] = o.Even ? c - p.OldCross[i] : c;
                }
            }
        }
        double endIn = p.CrossDelta[p.CoreFrom], endOut = p.CrossDelta[p.CoreTo];
        double lenIn = Math.Max(1e-6, Math.Min(CrossRun, sFirst)), lenOut = Math.Max(1e-6, Math.Min(CrossRun, path[n - 1].Dist - sLast));
        for (int i = 0; i < p.CoreFrom; i++) p.CrossDelta[i] = endIn * Fade((sFirst - path[i].Dist) / lenIn);
        for (int i = p.CoreTo + 1; i < n; i++) p.CrossDelta[i] = endOut * Fade((path[i].Dist - sLast) / lenOut);
        foreach (var j in p.Junctions)
            for (int i = 0; i < n; i++)
            {
                double f = Math.Clamp((Math.Abs(path[i].Dist - j.Dist) - j.PinHalf) / 12, 0, 1);
                p.CrossDelta[i] *= f * f * (3 - 2 * f);
            }
        for (int i = 0; i < n; i++) p.MaxCross = Math.Max(p.MaxCross, Math.Abs(p.OldCross[i] + p.CrossDelta[i]));
    }

    /// slope width per waypoint: the larger the change, the wider it blends into the terrain (5 m per meter, 8–40 m),
    /// then a moving maximum ±25 m so that the edge runs calmly
    static double[] SlopeWidths(ReprofilePlan p)
    {
        int n = p.Path.Count;
        var raw = new double[n];
        for (int i = 0; i < n; i++) raw[i] = Math.Clamp((Math.Abs(p.Delta[i]) + Math.Abs(p.CrossDelta[i]) * p.Edge) * 5, 8, 40);
        var res = new double[n];
        int hi = 0;
        var q = new LinkedList<int>();
        for (int k = 0; k < n; k++)
        {
            while (hi < n && p.Path[hi].Dist <= p.Path[k].Dist + 25)
            {
                while (q.Count > 0 && raw[q.Last.Value] <= raw[hi]) q.RemoveLast();
                q.AddLast(hi++);
            }
            while (p.Path[q.First.Value].Dist < p.Path[k].Dist - 25) q.RemoveFirst();
            res[k] = raw[q.First.Value];
        }
        return res;
    }

    static void Grade(ReprofilePlan p)
    {
        var path = p.Path;
        p.MaxGrade = 0;
        // measured over 10 m: the old heights lie in the meter grid, on shorter stretches the steps would be seen as a gradient
        for (int i = 40; i < path.Count; i += 4)
        {
            double g = Math.Abs(p.New[i] - p.New[i - 40]) / Math.Max(1e-6, path[i].Dist - path[i - 40].Dist);
            if (g > p.MaxGrade) { p.MaxGrade = g; p.MaxGradeAt = path[i].Dist; }
        }
    }

    static void Obstacles(WorldData w, ReprofilePlan p)
    {
        var path = p.Path;
        double reach = p.Edge + 42;
        double x0 = path.Min(q => q.X) - reach, x1 = path.Max(q => q.X) + reach, z0 = path.Min(q => q.Z) - reach, z1 = path.Max(q => q.Z) + reach;
        int count = 0;
        foreach (var pf in w.Prefabs)
        {
            if (pf.IsTile || pf.Def == null || pf.Def.Kind == PrefabKind.Part) continue;
            if (pf.X > x1 || pf.X + pf.SizeX < x0 || pf.Z > z1 || pf.Z + pf.SizeZ < z0) continue;
            for (int k = 0; k < path.Count; k += 8)
            {
                if (Math.Abs(p.Delta[k]) < 0.3) continue;
                double dx = Math.Max(Math.Max(pf.X - path[k].X, 0), path[k].X - (pf.X + pf.SizeX));
                double dz = Math.Max(Math.Max(pf.Z - path[k].Z, 0), path[k].Z - (pf.Z + pf.SizeZ));
                if (Math.Sqrt(dx * dx + dz * dz) < p.Edge + p.Blend[k]) { count++; break; }
            }
        }
        if (count > 0) p.Warnings.Add(Loc.F("{0} building(s) are close to the changed road. The ground under a building stays as it is, so a step can remain there.", count));
    }

    // ---------- Find intersections ----------

    /// paths that cross the stretch or join: search for surface just outside the surface on both sides.
    /// pieces on both sides at the same height of the stretch = intersection, one-sided = junction
    static List<JunctionPlan> FindJunctions(RoadScan scan, List<PathPoint> path, double roadHalf)
    {
        var res = new List<JunctionPlan>();
        int n = path.Count;
        double probe = roadHalf + 2.5, length = path[n - 1].Dist, endGap = 6;
        var runs = new List<(int side, int a, int b)>();
        foreach (int side in new[] { 1, -1 })
        {
            int start = -1, lastOn = -1, gap = 0;
            for (int k = 0; k < n; k += 4)
            {
                bool on = false;
                if (path[k].Dist > endGap && length - path[k].Dist > endGap)
                {
                    var (nx, nz) = RoadPlanner.Normal(path, k);
                    on = scan.Road(path[k].X + nx * probe * side, path[k].Z + nz * probe * side);
                }
                if (on) { if (start < 0) start = k; lastOn = k; gap = 0; }
                else if (start >= 0 && ++gap >= 2) { runs.Add((side, start, lastOn)); start = -1; gap = 0; }
            }
            if (start >= 0) runs.Add((side, start, lastOn));
        }
        runs.RemoveAll(u => path[u.b].Dist - path[u.a].Dist > 50);   // road runs alongside
        var used = new HashSet<int>();
        int Mid((int side, int a, int b) u) => (u.a + u.b) / 2;
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].side != 1) continue;
            int best = -1;
            double bd = 25;
            for (int j = 0; j < runs.Count; j++)
            {
                if (runs[j].side != -1 || used.Contains(j)) continue;
                double d = Math.Abs(path[Mid(runs[j])].Dist - path[Mid(runs[i])].Dist);
                if (d < bd) { bd = d; best = j; }
            }
            var members = new List<(int side, int a, int b)> { runs[i] };
            used.Add(i);
            if (best >= 0) { used.Add(best); members.Add(runs[best]); }
            res.Add(MakeJunction(scan, path, members, probe, roadHalf));
        }
        for (int i = 0; i < runs.Count; i++)
            if (!used.Contains(i)) res.Add(MakeJunction(scan, path, new List<(int, int, int)> { runs[i] }, probe, roadHalf));
        res.RemoveAll(j => j == null);
        res.Sort((x, y) => x.Dist.CompareTo(y.Dist));
        return res;
    }

    static JunctionPlan MakeJunction(RoadScan scan, List<PathPoint> path, List<(int side, int a, int b)> members, double probe, double roadHalf)
    {
        int mid = (int)members.Average(u => (u.a + u.b) / 2.0);
        var c = path[mid];
        var j = new JunctionPlan { Dist = c.Dist, Center = new Point(c.X, c.Z), Through = members.Count > 1 };
        foreach (var u in members)
        {
            int m = (u.a + u.b) / 2;
            var (nx, nz) = RoadPlanner.Normal(path, m);
            var normal = new Vector(nx * u.side, nz * u.side);
            var at = new Point(path[m].X + normal.X * probe, path[m].Z + normal.Y * probe);
            // direction of the branching path: main axis of the surface pixels around the mouth, without the edited road itself
            double sx = 0, sz = 0, sxx = 0, szz = 0, sxz = 0;
            int cnt = 0;
            for (int dz = -7; dz <= 7; dz++)
                for (int dx = -7; dx <= 7; dx++)
                {
                    if (dx * dx + dz * dz > 49) continue;
                    var q = new Point(at.X + dx, at.Y + dz);
                    if (!scan.Road(q.X, q.Y)) continue;
                    double lateral = (q.X - path[m].X) * normal.X + (q.Y - path[m].Z) * normal.Y;
                    if (lateral < roadHalf + 1) continue;
                    sx += dx; sz += dz; sxx += dx * dx; szz += dz * dz; sxz += dx * dz; cnt++;
                }
            var dir = normal;
            if (cnt >= 8)
            {
                double mx = sx / cnt, mz = sz / cnt;
                double a = 0.5 * Math.Atan2(2 * (sxz / cnt - mx * mz), (sxx / cnt - mx * mx) - (szz / cnt - mz * mz));
                dir = new Vector(Math.Cos(a), Math.Sin(a));
                if (dir.X * normal.X + dir.Y * normal.Y < 0) dir = -dir;
                if (dir.X * normal.X + dir.Y * normal.Y < 0.3) dir = normal;   // direction unclear: across the road
            }
            var perp = new Vector(-dir.Y, dir.X);
            var (lo, hi) = RoadFollower.Run(scan, at, perp);
            double armWidth = Math.Clamp(hi - lo + 1, 3, 40);
            var ext = RoadFollower.Extend(scan, at, dir, 260, armWidth, true);
            if (ext.Points.Count == 0) continue;
            var arm = new BranchArm { Width = ext.Width, Stop = ext.Stop, Start = roadHalf + 5 };
            // a branch that runs straight into another intersection has no room for a transition (part of the same node): leave out
            double reserve = ext.Points.Count * 2.0 + (at - j.Center).Length - arm.Start - (ext.Stop is "tile" or "junction" ? 6 : 0);
            if (ext.Stop == "junction" && reserve < 10) continue;
            arm.Points.Add(j.Center);
            arm.Points.Add(at);
            arm.Points.AddRange(ext.Points);
            double len = 0;
            for (int k = 1; k < arm.Points.Count; k++) len += (arm.Points[k] - arm.Points[k - 1]).Length;
            arm.Length = len;
            j.Arms.Add(arm);
            j.Kind = (byte)Math.Max(j.Kind, scan.W.RoadAt((int)Math.Floor(at.X), (int)Math.Floor(at.Y)));
        }
        if (j.Arms.Count == 0) return null;
        if (j.Kind == 0) j.Kind = WorldData.Gravel;
        return j;
    }

    // ---------- Apply ----------

    public sealed record Stats(int HeightPoints);

    /// enter the change into the world (heights only; the surface stays). For every cell: change of the edited road in the
    /// area of full effect, beside it faded out softly; at the intersections the change of the crossing road takes over softly.
    public static Stats Apply(WorldData w, WorldEdits e, ReprofilePlan p, IProgress<string> progress)
    {
        if (p.Error != null || !p.Changes) return new Stats(0);
        var path = p.Path;
        int pn = path.Count;
        double edge = p.Edge;
        progress?.Report(Loc.T("Calculating distances to the road …"));

        // arms of the dragged-along intersections in 1 m steps
        var arms = new List<(List<Point> Pts, double[] Along, double Delta, double Edge, double Bl, double Start, double Fade)>();
        foreach (var j in p.Junctions.Where(j => j.Followed))
            foreach (var a in j.Arms)
            {
                var pts = Resample(a.Points, 1);
                var along = new double[pts.Count];
                for (int k = 1; k < pts.Count; k++) along[k] = along[k - 1] + (pts[k] - pts[k - 1]).Length;
                arms.Add((pts, along, j.Delta, a.Width / 2 + 1, Math.Clamp(Math.Abs(j.Delta) * 6, 8, 45), a.Start, a.Fade));
            }

        // grid around everything
        double bx0 = path.Min(q => q.X), bx1 = path.Max(q => q.X), bz0 = path.Min(q => q.Z), bz1 = path.Max(q => q.Z);
        double reach = edge + 40 + 2;
        foreach (var a in arms)
        {
            double r = a.Edge + a.Bl + 2;
            bx0 = Math.Min(bx0, a.Pts.Min(q => q.X) - (r - reach)); bx1 = Math.Max(bx1, a.Pts.Max(q => q.X) + (r - reach));
            bz0 = Math.Min(bz0, a.Pts.Min(q => q.Y) - (r - reach)); bz1 = Math.Max(bz1, a.Pts.Max(q => q.Y) + (r - reach));
        }
        int gx0 = (int)Math.Floor(bx0 - reach) - 1, gz0 = (int)Math.Floor(bz0 - reach) - 1;
        int gw = (int)Math.Ceiling(bx1 + reach) + 2 - gx0, gh = (int)Math.Ceiling(bz1 + reach) + 2 - gz0;
        var gd = new float[gw * gh];
        var gi = new int[gw * gh];
        Array.Fill(gd, float.MaxValue);
        Array.Fill(gi, -1);
        var ids = new List<int>();
        for (int k = 0; k < pn; k += 2) ids.Add(k);
        if (ids[^1] != pn - 1) ids.Add(pn - 1);
        int rad = (int)Math.Ceiling(edge + 40 + 2) + 1;
        foreach (int k in ids)
        {
            double px = path[k].X, pz = path[k].Z;
            int cx = (int)Math.Floor(px) - gx0, cz = (int)Math.Floor(pz) - gz0;
            for (int dz = -rad; dz <= rad; dz++)
            {
                int gz = cz + dz;
                if (gz < 0 || gz >= gh) continue;
                for (int dx = -rad; dx <= rad; dx++)
                {
                    int gx = cx + dx;
                    if (gx < 0 || gx >= gw) continue;
                    double ex = gx0 + gx + 0.5 - px, ez = gz0 + gz + 0.5 - pz;
                    float d = (float)Math.Sqrt(ex * ex + ez * ez);
                    int g = gz * gw + gx;
                    if (d < gd[g]) { gd[g] = d; gi[g] = k; }
                }
            }
        }

        // change of the crossing roads (larger effect wins)
        var db = new float[gw * gh];
        var armCore = new bool[gw * gh];   // cells in the surface area of an arm
        foreach (var a in arms)
        {
            int an = a.Pts.Count;
            int ar = (int)Math.Ceiling(a.Edge + a.Bl) + 2;
            // only the rectangle around the arm (saves memory with long stretches with many intersections)
            int ax0 = Math.Max(0, (int)Math.Floor(a.Pts.Min(q => q.X)) - gx0 - ar), ax1 = Math.Min(gw - 1, (int)Math.Floor(a.Pts.Max(q => q.X)) - gx0 + ar);
            int az0 = Math.Max(0, (int)Math.Floor(a.Pts.Min(q => q.Y)) - gz0 - ar), az1 = Math.Min(gh - 1, (int)Math.Floor(a.Pts.Max(q => q.Y)) - gz0 + ar);
            int lw = ax1 - ax0 + 1, lh = az1 - az0 + 1;
            if (lw <= 0 || lh <= 0) continue;
            var ad = new float[lw * lh];
            var ai = new int[lw * lh];
            Array.Fill(ad, float.MaxValue);
            Array.Fill(ai, -1);
            for (int k = 0; k < an; k++)
            {
                int cx = (int)Math.Floor(a.Pts[k].X) - gx0, cz = (int)Math.Floor(a.Pts[k].Y) - gz0;
                for (int dz = -ar; dz <= ar; dz++)
                {
                    int gz = cz + dz;
                    if (gz < az0 || gz > az1) continue;
                    for (int dx = -ar; dx <= ar; dx++)
                    {
                        int gx = cx + dx;
                        if (gx < ax0 || gx > ax1) continue;
                        double ex = gx0 + gx + 0.5 - a.Pts[k].X, ez = gz0 + gz + 0.5 - a.Pts[k].Y;
                        float d = (float)Math.Sqrt(ex * ex + ez * ez);
                        int l = (gz - az0) * lw + gx - ax0;
                        if (d < ad[l]) { ad[l] = d; ai[l] = k; }
                    }
                }
            }
            for (int gz = az0; gz <= az1; gz++)
                for (int gx = ax0; gx <= ax1; gx++)
                {
                    int g = gz * gw + gx, l = (gz - az0) * lw + gx - ax0, k = ai[l];
                    if (k < 0) continue;
                    double d = ad[l];
                    if (d > a.Edge + a.Bl) continue;
                    int x = gx0 + gx, z = gz0 + gz;
                    if (!w.Contains(x, z)) continue;
                    if (k == an - 1 && d > a.Edge) continue;   // behind the end of the arm
                    double u = d <= a.Edge ? 1 : 1 - (d - a.Edge) / a.Bl;
                    u = u * u * (3 - 2 * u);
                    if (d <= a.Edge) armCore[g] = true;
                    double along = a.Along[k];
                    double value = a.Delta * (along <= a.Start ? 1 : Fade5((along - a.Start) / a.Fade));
                    double v = value * u;
                    if (Math.Abs(v) > Math.Abs(db[g])) db[g] = (float)v;
                }
        }

        // areas that stay unchanged: city tiles and footprints of buildings
        var keep = new bool[gw * gh];
        foreach (var pf in w.Prefabs)
        {
            if (pf.Def == null || pf.Def.Kind == PrefabKind.Part && !pf.Name.Contains("driveway", StringComparison.OrdinalIgnoreCase)) continue;
            int margin = pf.IsTile ? 0 : 1;
            int x0 = Math.Max(gx0, pf.X - margin), x1 = Math.Min(gx0 + gw - 1, pf.X + pf.SizeX - 1 + margin);
            int z0 = Math.Max(gz0, pf.Z - margin), z1 = Math.Min(gz0 + gh - 1, pf.Z + pf.SizeZ - 1 + margin);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++) keep[(z - gz0) * gw + x - gx0] = true;
        }

        // protection: roads that belong neither to the edited stretch nor to an arm, as well as tiles and buildings stay as they are.
        // Faded out softly (mean over 7 × 7 m) so that no step appears beside a thin surface strip or a house wall.
        var protect = new float[gw * gh];
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx, x = gx0 + gx, z = gz0 + gz;
                if (keep[g]) { protect[g] = 1; continue; }
                if (!w.Contains(x, z) || w.RoadMask[w.Index(x, z)] == 0) continue;
                // surface directly beside the stretch (up to 12 m) belongs to the intersection (junctions, parallel pieces) and follows softly with
                if (!armCore[g] && !(gi[g] >= 0 && gd[g] <= edge + 12)) protect[g] = 1;
            }
        protect = Blur(protect, gw, gh, 3);

        progress?.Report(Loc.T("Changing the heights …"));
        double sx = path[Math.Min(4, pn - 1)].X - path[0].X, sz = path[Math.Min(4, pn - 1)].Z - path[0].Z;
        double tex = path[pn - 1].X - path[Math.Max(0, pn - 5)].X, tez = path[pn - 1].Z - path[Math.Max(0, pn - 5)].Z;
        double sl = Math.Max(1e-6, Math.Sqrt(sx * sx + sz * sz)), el = Math.Max(1e-6, Math.Sqrt(tex * tex + tez * tez));
        int changed = 0;
        var df = new float[gw * gh];   // height change per cell (m); only afterwards smoothed and entered
        var soft = new bool[gw * gh];  // cells in the slope area (not in the surface area of the stretch or of a branch)
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx, k = gi[g];
                int x = gx0 + gx, z = gz0 + gz;
                if (!w.Contains(x, z) || keep[g]) continue;
                bool mainCore = k >= 0 && gd[g] <= edge;
                double um = 0, dm = 0;
                if (k >= 0)
                {
                    double d = gd[g], bl = p.Blend[k];
                    if (d <= edge + bl)
                    {
                        um = d <= edge ? 1 : 1 - (d - edge) / bl;
                        um = um * um * (3 - 2 * um);
                        // behind the ends of the stretch the road continues: change nothing there
                        bool beyond = k == pn - 1 && ((x + 0.5 - path[k].X) * tex + (z + 0.5 - path[k].Z) * tez) / el > 0.5
                                      || k == 0 && ((x + 0.5 - path[0].X) * sx + (z + 0.5 - path[0].Z) * sz) / sl < -0.5;
                        if (beyond) um = 0;
                        dm = p.Delta[k];
                        // banking: change × lateral distance to the center (at most up to the edge of the surface), + = left higher
                        if (p.CrossDelta[k] != 0)
                        {
                            var (nx, nz) = RoadPlanner.Normal(path, k);
                            dm += p.CrossDelta[k] * Math.Clamp((x + 0.5 - path[k].X) * nx + (z + 0.5 - path[k].Z) * nz, -edge, edge);
                        }
                    }
                }
                double free = 1 - protect[g];   // 0 = protected (tile, building, foreign road)
                double delta = um * dm * (mainCore ? 1 : free) + (1 - um) * db[g] * (armCore[g] ? 1 : free);
                df[g] = (float)delta;
                soft[g] = !mainCore && !armCore[g];
            }
        // in the slope smooth the change over 7 × 7 m: where the next waypoints of a tight curve lie far apart, otherwise a seam arises
        // (jump of the change along a line)
        var smooth = Blur(df, gw, gh, 3);
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx, x = gx0 + gx, z = gz0 + gz;
                if (!w.Contains(x, z) || keep[g]) continue;
                double delta = soft[g] ? smooth[g] : df[g];
                if (Math.Abs(delta) < 1.0 / 512) continue;
                int hi = w.Index(x, z);
                var nv = (ushort)Math.Clamp(Math.Round(w.Heights[hi] + delta * 256), 0, 65535);
                if (nv != w.Heights[hi]) { e.SetHeight(hi, nv); changed++; }
            }
        return new Stats(changed);
    }

    // mean over (2r+1) × (2r+1) cells, in two passes
    static float[] Blur(float[] src, int gw, int gh, int r)
    {
        var tmp = new float[src.Length];
        var res = new float[src.Length];
        float norm = 1f / (2 * r + 1);
        for (int z = 0; z < gh; z++)
            for (int x = 0; x < gw; x++)
            {
                float s = 0;
                for (int k = -r; k <= r; k++) { int xx = x + k; if (xx >= 0 && xx < gw) s += src[z * gw + xx]; }
                tmp[z * gw + x] = s * norm;
            }
        for (int z = 0; z < gh; z++)
            for (int x = 0; x < gw; x++)
            {
                float s = 0;
                for (int k = -r; k <= r; k++) { int zz = z + k; if (zz >= 0 && zz < gh) s += tmp[zz * gw + x]; }
                res[z * gw + x] = s * norm;
            }
        return res;
    }

    static List<Point> Resample(List<Point> line, double step)
    {
        var res = new List<Point> { line[0] };
        double carry = 0;
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var v = line[i] - a;
            double len = v.Length, t = step - carry;
            while (t <= len) { res.Add(a + v * (t / len)); t += step; }
            carry = len - (t - step);
        }
        if ((res[^1] - line[^1]).Length > 0.5) res.Add(line[^1]);
        return res;
    }

    // ---------- smooth curve through nodes (monotone cubic Hermite interpolation, without overshoot) ----------

    sealed class Pchip
    {
        readonly double[] x, y, m;

        public Pchip(List<(double S, double V)> k)
        {
            int n = k.Count;
            x = k.Select(q => q.S).ToArray();
            y = k.Select(q => q.V).ToArray();
            m = new double[n];
            if (n < 2) return;
            var h = new double[n - 1];
            var d = new double[n - 1];
            for (int i = 0; i < n - 1; i++) { h[i] = x[i + 1] - x[i]; d[i] = (y[i + 1] - y[i]) / h[i]; }
            if (n == 2) { m[0] = m[1] = d[0]; return; }
            for (int i = 1; i < n - 1; i++)
            {
                if (d[i - 1] * d[i] <= 0) { m[i] = 0; continue; }
                double w1 = 2 * h[i] + h[i - 1], w2 = h[i] + 2 * h[i - 1];
                m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
            }
            // at the ends the slope of the first or last section (the usual three-point formula would start steeper there)
            m[0] = d[0];
            m[n - 1] = d[n - 2];
        }

        public double At(double s)
        {
            int n = x.Length;
            if (n == 1) return y[0];
            if (s <= x[0]) return y[0];
            if (s >= x[n - 1]) return y[n - 1];
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (x[mid] <= s) lo = mid; else hi = mid; }
            double h = x[hi] - x[lo], t = (s - x[lo]) / h, t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * y[lo] + (t3 - 2 * t2 + t) * h * m[lo] + (-2 * t3 + 3 * t2) * y[hi] + (t3 - t2) * h * m[hi];
        }
    }
}
