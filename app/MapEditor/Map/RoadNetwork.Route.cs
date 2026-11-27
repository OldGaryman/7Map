using System.Windows;

namespace MapEditor.Map;

/// how the route search weighs length against gradient (the user's request 2026-11-17, road generation from point to point)
public enum RouteStyle
{
    Natural,     // like "Generate roads": follows valleys and slopes, gentle swings on open ground
    Direct,      // as little detour as possible: uses the highest gradient, may cut through ridges (clearing)
    Flat,        // as little gradient as possible: curves around hills, longer
    Switchback,  // serpentine: only when the others are too steep – hard limit at the usual gradient, zigzag up the slope
}

/// one proposed course of a route
public sealed class RouteVariant
{
    public RouteStyle Style;
    public NetRoad Road;
    public List<Point> Via = new();
}

/// Road generation from point to point (the user's request 2026-11-17): one road between two clicked places, searched with the cost
/// grid and the search of "Generate roads" (RoadNetwork.Edit) and built with NetworkBuilder. The ends can be a free town exit, an
/// entrance of a building, an existing road (junction at its height) or free ground (the road just ends there).
public sealed partial class RoadNetwork
{
    double routeGrade, routeMax;
    float[] multNatural, multPlain;
    RoadScan routeScan;
    /// ends of the built bridges (snap points; set by the main window before each use, BridgeEnds.Find)
    public IReadOnlyList<BridgeEnd> BridgeEndList;

    /// network prepared for routes (cost grid, towns with their free exits, street crossings); about as long as the start of planning
    public static RoadNetwork ForRoutes(WorldData w, NetworkSpec spec)
    {
        var r = ForEditing(w, spec);
        r.routeGrade = spec.Grade;
        r.routeMax = spec.MaxGrade;
        r.multNatural = r.mult;
        r.multPlain = new float[r.n];
        Array.Fill(r.multPlain, 1f);
        r.routeScan = new RoadScan(w);
        return r;
    }

    /// usual and highest gradient of the route search (changed in the tool without reading the terrain again)
    public void SetGrades(double grade, double max)
    {
        routeGrade = Math.Clamp(grade, 0.02, 0.5);
        routeMax = Math.Clamp(Math.Max(max, routeGrade + 0.01), 0.03, 0.6);
        SetStyle(RouteStyle.Natural);
    }

    /// towns of the world (for the preview: free exits as orange points)
    public List<NetTown> TownList => plan.Towns;

    /// towns of the world with their free exits (from FindTowns)
    public IEnumerable<NetExit> FreeTownExits => plan.Towns.SelectMany(t => t.Exits).Where(e => e.Cell >= 0 && !e.Street && !e.Used);

    /// costs of the search for this style (gradient limits, valley preference and noise); Natural restores the planner's values
    void SetStyle(RouteStyle style)
    {
        spec.Grade = routeGrade;
        spec.MaxGrade = routeMax;
        mult = multNatural;
        switch (style)
        {
            case RouteStyle.Direct:
                spec.Grade = Math.Max(routeGrade, 0.9 * routeMax);
                mult = multPlain;
                break;
            case RouteStyle.Flat:
                spec.Grade = Math.Max(0.03, 0.55 * routeGrade);
                spec.MaxGrade = Math.Max(spec.Grade + 0.01, routeGrade);
                mult = multPlain;
                break;
            case RouteStyle.Switchback:
                spec.Grade = Math.Max(0.03, 0.8 * routeGrade);
                spec.MaxGrade = Math.Max(spec.Grade + 0.005, routeGrade);
                mult = multPlain;
                break;
        }
        minMult = -1;
    }

    // ---------- ends ----------

    /// What lies at a clicked place: a free town exit, the entrance of a building (POI), an existing road outside the tiles, or free
    /// ground. tol = snap distance (m). null + reason if nothing can start there (water, inside a building or tile, map edge).
    /// The direction of a road or free end is set later by Orient (it depends on the other end).
    public NetEnd RouteEndAt(Point p, double tol, out string problem)
    {
        problem = null;
        // 0. outer end of a bridge approach (the user's photo 2026-11-26: the route took a point on the bridge piece itself, a click
        // beside it then gave "start and end are too close together"): the road continues the approach at its height
        BridgeEnd bridge = null; double bb = tol;
        foreach (var be in BridgeEndList ?? Array.Empty<BridgeEnd>())
        {
            double d = (be.At - p).Length;
            if (be.Covers(p)) d = Math.Min(d, 1);
            if (d < bb) { bb = d; bridge = be; }
        }
        if (bridge != null)
        {
            var o = bridge.Outward; o.Normalize();
            var bx = new NetExit { X = bridge.At.X, Z = bridge.At.Y, DX = o.X, DZ = o.Y, Width = bridge.Width, Gravel = bridge.Gravel, Height = bridge.Height, Bridge = bridge };
            return new NetEnd(bx, null, bridge.At, o);
        }
        // 1. free town exit (dead end)
        NetEnd best = null; double bd = tol;
        foreach (var e in FreeTownExits)
        {
            double d = (new Point(e.X, e.Z) - p).Length;
            if (d < bd) { bd = d; best = new NetEnd(e, null, new Point(e.X, e.Z), new Vector(e.DX, e.DZ)); }
        }
        if (best != null) return best;
        // 2. entrance of a building (not tiles: their exits are town exits; not parts)
        var near = w.PrefabSnapshot().Where(pf => pf.Def != null && !pf.IsTile && pf.Def.Kind != PrefabKind.Part && !BridgeKits.IsBridgePiece(pf.Name)
            && pf.X - 60 < p.X && pf.X + pf.SizeX + 60 > p.X && pf.Z - 60 < p.Y && pf.Z + pf.SizeZ + 60 > p.Y).ToList();
        foreach (var pf in near)
            foreach (var x in PrefabParts.ExitsFor(w, pf))
            {
                double d = (new Point(x.X, x.Z) - p).Length;
                if (d >= bd) continue;
                var (dx, dz) = OutOfBuilding(pf, x.X, x.Z);
                bd = d;
                var exit = new NetExit { Poi = pf, X = x.X, Z = x.Z, DX = dx, DZ = dz, Width = x.Width > 0 ? x.Width : 5 };
                best = new NetEnd(exit, null, new Point(x.X, x.Z), new Vector(dx, dz));
            }
        if (best != null) return best;
        // 3. existing road (outside the tiles)
        if (!routeScan.InTile(p.X, p.Y))
        {
            var spot = RoadFollower.Center(routeScan, p);
            if (spot != null && (spot.Center - p).Length <= Math.Max(tol, spot.Width / 2 + 2) && !routeScan.InTile(spot.Center.X, spot.Center.Y))
            {
                // the end of a road (e.g. in front of a bridge placed by hand): continue it instead of making a junction
                var deadEnd = DeadEndAt(WideSpot(spot), p);
                if (deadEnd != null) return deadEnd;
                return StreetEnd(StraightSpot(spot));
            }
        }
        // 4. free ground
        int ix = (int)Math.Floor(p.X), iz = (int)Math.Floor(p.Y);
        if (!w.Contains(ix, iz) || Math.Max(Math.Abs(p.X), Math.Abs(p.Y)) > half - EdgeCenter) { problem = Loc.T("Too close to the map edge."); return null; }
        if (w.WaterAt(ix, iz)) { problem = Loc.T("There is water here."); return null; }
        var inside = w.PrefabSnapshot().FirstOrDefault(pf => pf.Def != null && pf.Def.Kind != PrefabKind.Part && pf.Contains(p.X, p.Y));
        if (inside != null) { problem = inside.IsTile ? Loc.T("This is inside a town tile. Click one of its free exits (dead-end sign) instead.") : Loc.F("This is inside the building {0}. Click its entrance instead.", inside.Name); return null; }
        return new NetEnd(null, null, p, new Vector(1, 0));
    }

    /// Road spot with a reliable direction (the user's map Darobe-Land 2026-11-17: on a 16 m road the axis over 7 m around the click is a
    /// round patch, the direction was random – the new road left along the old one instead of across it and stood up to 5 m beside it):
    /// axis over a circle of 1.5 × the width, then centred across it again.
    public RoadSpot WideSpot(RoadSpot spot)
    {
        var dir = RoadFollower.Axis(routeScan, spot.Center, Math.Max(12, spot.Width * 1.5)) ?? spot.Dir;
        dir.Normalize();
        var n = new Vector(-dir.Y, dir.X);
        var (lo, hi) = RoadFollower.Run(routeScan, spot.Center, n, 24);
        var c = spot.Center + n * (lo + hi) / 2;
        return spot with { Center = c, Dir = dir, Width = Math.Max(3, hi - lo + 1) };
    }

    /// If the road ends within 45 m of this spot on exactly one side (and goes on for 16 m on the other), the end of the road as a fixed
    /// end pointing out of the road; otherwise null. (Was 10 m: a click a little before the end made a junction instead of continuing.)
    /// The end point and direction are taken from the full-width road behind the end, so a slanted, ragged or rounded end cannot pull the
    /// marker onto a protruding corner or tilt the continuation.
    const double DeadEndReach = 45;

    NetEnd DeadEndAt(RoadSpot s, Point click)
    {
        var dir0 = s.Dir; dir0.Normalize();
        // A click on a tapered corner underestimates s.Width, which would make Extend treat the full-width road behind it as a junction.
        double estW = Math.Max(s.Width, s.Kind == WorldData.Asphalt ? 18 : 8);
        bool TryAxis(Vector dir, out NetEnd result, out bool longRoad)
        {
            result = null;
            longRoad = false;
            dir.Normalize();
            var cands = new List<NetEnd>();
            foreach (int sg in new[] { 1, -1 })
            {
                var back = RoadFollower.Extend(routeScan, s.Center, dir * -sg, 44, estW, false);
                if (back.Length >= 26) longRoad = true;
                double roadW = Math.Max(3, back.Width);
                var f = RoadFollower.Extend(routeScan, s.Center, dir * sg, DeadEndReach + 6, roadW, false);
                if (f.Stop != "end" || f.Length > DeadEndReach) continue;
                double totalLen = back.Length + f.Length;
                bool longBack = back.Length >= 15 || (back.Stop == "tile" && back.Length >= 8);
                bool shortStrip = totalLen >= Math.Max(15, roadW * 1.4) && back.Length >= 6;
                if (!longBack && !shortStrip) continue;
                var candidate = EndFromBack(s.Center, dir * sg, back, f, roadW);
                if (candidate != null) cands.Add(candidate);
            }
            if (cands.Count == 1) { result = cands[0]; return true; }
            if (cands.Count == 2)
            {
                // Both ends are within 45 m (e.g. a 20 m bridge approach placed by hand): pick the end the user clicked near,
                // if it is clearly closer to the click than the opposite end.
                double d0 = (cands[0].P - click).Length, d1 = (cands[1].P - click).Length;
                if (Math.Min(d0, d1) <= 12 && Math.Abs(d0 - d1) >= 6)
                {
                    result = d0 < d1 ? cands[0] : cands[1];
                    return true;
                }
                result = null;
                return true;   // clicked near the middle of a short piece: not a clear end
            }
            return false;
        }
        if (TryAxis(dir0, out var res, out bool longRoad)) return res;
        // At the end of a wide road (e.g. 18 m) a small axis circle can point across the road instead of along it:
        // if dir0 did not follow a road for at least 26 m, retry with a wider circle (28 m) and the perpendicular.
        if (!longRoad)
        {
            if (RoadFollower.Axis(routeScan, s.Center, 28) is Vector wide && TryAxis(wide, out res, out _)) return res;
            if (TryAxis(new Vector(-dir0.Y, dir0.X), out res, out _)) return res;
        }
        return null;
    }

    /// Walks forward from the intact full-width road behind the end towards the open end, keeping only symmetric full-width
    /// cross-sections (so a slanted cut, protruding corner or rounded cap does not pull the centre line sideways), computes the
    /// outgoing tangent from that clean tail, and places the connection point on the centre line 2 m inside the open end.
    NetEnd EndFromBack(Point start, Vector approxOut, FollowResult back, FollowResult f, double roadW)
    {
        if (back.Points.Count == 0) return null;
        double wantBack = Math.Min(back.Length, Math.Max(12, 44 - f.Length));
        int idx = Math.Clamp((int)Math.Round(wantBack / 2) - 1, 0, back.Points.Count - 1);
        var anchor = back.Points[idx];
        var curDir = idx >= 3 ? back.Points[idx - 3] - anchor : idx >= 1 ? back.Points[0] - anchor : start - anchor;
        if (curDir.Length < 1e-6) curDir = approxOut;
        curDir.Normalize();

        var n0 = new Vector(-curDir.Y, curDir.X);
        var (lo0, hi0) = RoadFollower.Run(routeScan, anchor, n0, Math.Max(16, roadW * 1.5));
        double w0 = hi0 - lo0 + 1;
        double localW = roadW;
        var good = new List<(Point P, double W, double Dist)>();
        if (w0 >= 2.5 && w0 <= roadW * 1.45 + 3)
        {
            anchor += n0 * (lo0 + hi0) / 2;
            localW = w0;
            good.Add((anchor, w0, 0));
        }

        double maxWalk = wantBack + f.Length + 24;
        var p = anchor;
        double walked = 0;
        int missed = 0;
        while (walked + 2 <= maxWalk)
        {
            var q = p + curDir * 2;
            walked += 2;
            if (!routeScan.W.Contains(q.X, q.Y) || routeScan.InTile(q.X, q.Y)) break;
            var n = new Vector(-curDir.Y, curDir.X);
            if (!routeScan.Road(q.X, q.Y))
            {
                // Tiny lateral tolerance (1.5 m) for tight curves on intact road; never chase a protruding corner sideways.
                double t0 = double.NaN;
                for (double off = 0.5; off <= 1.5; off += 0.5)
                {
                    if (routeScan.Road(q.X + n.X * off, q.Y + n.Y * off)) { t0 = off; break; }
                    if (routeScan.Road(q.X - n.X * off, q.Y - n.Y * off)) { t0 = -off; break; }
                }
                if (double.IsNaN(t0))
                {
                    if (++missed >= 2) break;
                    p = q;
                    continue;
                }
                q += n * t0;
            }
            missed = 0;
            var (lo, hi) = RoadFollower.Run(routeScan, q, n, Math.Max(16, localW * 1.6));
            double run = hi - lo + 1;
            double shift = Math.Abs(lo + hi) / 2;
            // A symmetric taper (e.g. bridge approach) can drop 1 px on both sides at once (2 m total) while staying centred;
            // a slanted cut drops one side only and shifts the centre immediately.
            double minRun = shift <= 0.6
                ? Math.Max(2.5, Math.Min(localW * 0.84, localW - 2.2))
                : Math.Max(2.5, Math.Min(localW * 0.88, localW - 1.5));
            bool fullWidth = run >= minRun && run <= localW * 1.45 + 2 && shift <= Math.Max(1.0, localW * 0.08);
            if (fullWidth)
            {
                var c = q + n * (lo + hi) / 2;
                var nd = c - p;
                if (nd.Length > 1e-6)
                {
                    nd.Normalize();
                    var blend = curDir * 0.6 + nd * 0.4;
                    blend.Normalize();
                    curDir = blend;
                }
                p = c;
                localW = localW * 0.5 + run * 0.5;
                good.Add((p, run, walked));
            }
            else
            {
                // Crossing road (too wide) or slanted/tapered end (too narrow or one-sided): continue straight without drifting.
                p = q;
            }
        }
        if (good.Count < 3) return null;

        // Contiguous clean tail ending at the last full-width cross-section (stop at any gap > 6 m, e.g. a junction behind the end)
        int tailStart = good.Count - 1;
        while (tailStart > 0 && good[tailStart].Dist - good[tailStart - 1].Dist <= 6.1) tailStart--;
        // Step one point back from the very last accepted cross-section if enough tail exists, so a slight graze of a slanted
        // edge at the last 2 m step cannot bias the centerline or tangent.
        int refIdx = good.Count - tailStart >= 5 ? good.Count - 2 : good.Count - 1;
        var pRef = good[refIdx].P;
        double refDist = good[refIdx].Dist;
        double endW = good[refIdx].W;

        int AtBack(double meters)
        {
            int best = -1; double bestErr = double.MaxValue;
            for (int i = refIdx - 1; i >= tailStart; i--)
            {
                double d = refDist - good[i].Dist;
                double err = Math.Abs(d - meters);
                if (err < bestErr) { bestErr = err; best = i; }
                if (d > meters + 2) break;
            }
            return best;
        }

        var t = curDir;
        int i12 = AtBack(12);
        if (i12 >= 0 && refDist - good[i12].Dist >= 4)
        {
            var c1 = pRef - good[i12].P;
            if (c1.Length > 1e-6)
            {
                t = c1;
                int i24 = AtBack(24);
                if (i24 >= 0 && i24 < i12 && good[i12].Dist - good[i24].Dist >= 8)
                {
                    var c2 = good[i12].P - good[i24].P;
                    if (c2.Length > 1e-6)
                    {
                        var a = c1; a.Normalize();
                        var b = c2; b.Normalize();
                        if (a * b > 0.85) t = a + (a - b) * 0.5;   // continue a smooth curve; ignore sharp noise
                    }
                }
            }
        }
        if (t.Length < 1e-6) t = approxOut;
        t.Normalize();

        // Walk forward along the true centre line from pRef in 0.5 m steps:
        // dTip = where the centre line leaves the surface; dFull = last place where both sides still reach full width.
        var nEnd = new Vector(-t.Y, t.X);
        double halfReq = Math.Max(1.0, (endW - 1) * 0.43);
        double dTip = 0, dFull = 0;
        double maxReach = Math.Max(16, endW);
        for (double d = 0.5; d <= maxReach; d += 0.5)
        {
            var q = pRef + t * d;
            if (routeScan.Road(q.X, q.Y))
            {
                dTip = d;
                var (lo, hi) = RoadFollower.Run(routeScan, q, nEnd, Math.Max(12, endW));
                if (-lo >= halfReq && hi >= halfReq) dFull = d;
            }
            else if (d - dTip >= 1.5) break;
        }
        // 2 m back from where the centre line leaves the surface, and at most 2 m ahead of the last full-width cross-section
        // so a steep slanted cut or rounded cap neither leaves a notch on the shorter side nor drifts off-centre
        double offset = Math.Clamp(Math.Min(dTip - 2.0, dFull + 2.0), -(refDist - good[tailStart].Dist), dTip);
        var endP = pRef + t * offset;
        return new NetEnd(null, null, endP, t) { Fixed = true };
    }

    /// is the road straight and plain around this spot (no curve, no junction within ±20 m)?
    bool Straight(RoadSpot s)
    {
        foreach (int sg in new[] { 1, -1 })
        {
            var q = s.Center + s.Dir * (20 * sg);
            var a = RoadFollower.Axis(routeScan, q, Math.Max(10, s.Width * 1.2));
            if (a == null || Math.Abs(a.Value * s.Dir) < 0.94) return false;
            var n = new Vector(-s.Dir.Y, s.Dir.X);
            var (lo, hi) = RoadFollower.Run(routeScan, q, n, 30);
            if (hi - lo + 1 > s.Width * 1.5 + 3) return false;   // another road joins there
        }
        return true;
    }

    /// the clicked spot with a reliable direction; if the road bends there or another road joins, the nearest straight place up to
    /// 60 m along the road (a junction in a bend gave a sharp fork)
    public RoadSpot StraightSpot(RoadSpot spot)
    {
        var s = WideSpot(spot);
        if (Straight(s)) return s;
        for (int d = 8; d <= 60; d += 8)
            foreach (int sg in new[] { 1, -1 })
            {
                var f = RoadFollower.Extend(routeScan, s.Center, s.Dir * sg, d, s.Width, true);
                if (f.Length < d - 1 || f.Points.Count == 0) continue;
                var c = RoadFollower.Center(routeScan, f.Points[^1]);
                if (c == null || routeScan.InTile(c.Center.X, c.Center.Y)) continue;
                var cs = WideSpot(c);
                if (Straight(cs)) return cs;
            }
        return s;
    }

    /// end on an existing road: junction at the middle of the road (direction across it is set by Orient)
    NetEnd StreetEnd(RoadSpot spot)
    {
        var dir = spot.Dir; dir.Normalize();
        var exit = new NetExit
        {
            Street = true, X = spot.Center.X, Z = spot.Center.Y, StreetDX = dir.X, StreetDZ = dir.Y,
            Width = (int)Math.Round(Math.Max(3, spot.Width)), StreetHalf = spot.Width / 2 + 1, Gravel = spot.Kind == WorldData.Gravel,
            DX = -dir.Y, DZ = dir.X,
        };
        return new NetEnd(exit, null, spot.Center, new Vector(exit.DX, exit.DZ));
    }

    /// direction out of a building at an entrance: away from the nearest side of the building
    static (double DX, double DZ) OutOfBuilding(PlacedPrefab pf, double x, double z)
    {
        double s = Math.Abs(z - pf.Z), n = Math.Abs(z - (pf.Z + pf.SizeZ)), wv = Math.Abs(x - pf.X), e = Math.Abs(x - (pf.X + pf.SizeX));
        double m = Math.Min(Math.Min(s, n), Math.Min(wv, e));
        // an entrance outside the building (driveway): the side it lies beyond
        if (z < pf.Z) return (0, -1);
        if (z > pf.Z + pf.SizeZ) return (0, 1);
        if (x < pf.X) return (-1, 0);
        if (x > pf.X + pf.SizeX) return (1, 0);
        return m == s ? (0, -1) : m == n ? (0, 1) : m == wv ? (-1, 0) : (1, 0);
    }

    /// the end with its direction towards the other end: a road end leaves its road at a right angle on the side of the other end
    /// (along = re-laying: along the old road instead), a free end points straight at the other end
    public static NetEnd Orient(NetEnd end, Point other, Vector? along = null)
    {
        if (end.Exit?.Street == true)
        {
            var e = end.Exit;
            Vector n;
            if (along is Vector a) n = a;
            else
            {
                n = new Vector(-e.StreetDZ, e.StreetDX);
                if ((other - end.P) * n < 0) n = -n;
            }
            n.Normalize();
            var copy = new NetExit
            {
                Street = true, X = e.X, Z = e.Z, StreetDX = e.StreetDX, StreetDZ = e.StreetDZ, Width = e.Width, StreetHalf = e.StreetHalf,
                Gravel = e.Gravel, DX = n.X, DZ = n.Y,
            };
            return new NetEnd(copy, null, end.P, n);
        }
        if (end.Exit == null && end.Road == null && !end.Fixed)
        {
            var d = other - end.P;
            if (d.Length < 1e-6) return end;
            d.Normalize();
            return new NetEnd(null, null, end.P, new Vector(d.Y, -d.X));   // OutOf turns this tangent into d
        }
        return end;
    }

    /// short description of an end for the window
    public static string EndText(NetEnd e) =>
        e.Exit?.Bridge != null ? Loc.T("end of a bridge")
        : e.Exit?.Tile != null ? Loc.T("free town exit")
        : e.Exit?.Poi != null ? Loc.F("entrance of {0}", e.Exit.Poi.Name)
        : e.Exit?.Street == true ? (e.Exit.Gravel ? Loc.T("existing gravel road") : Loc.T("existing road"))
        : e.Fixed ? Loc.T("end of an existing road")
        : e.Road != null ? Loc.T("the new road")
        : Loc.T("free ground (the road ends there)");

    // ---------- courses ----------

    /// The proposed courses between s and e (through the via points set by hand): natural, direct and flat; if all of them are
    /// steeper than the usual gradient, a serpentine as well. Courses that are practically the same as an earlier one are left out.
    /// gravel/width: kind of the new road. problem = reason if not a single course was found.
    public List<RouteVariant> RouteVariants(NetEnd s, NetEnd e, IList<Point> vias, bool gravel, double width, IProgress<string> progress, out string problem)
    {
        problem = null;
        var list = new List<RouteVariant>();
        string lastProblem = null;
        var styles = new[] { RouteStyle.Natural, RouteStyle.Direct, RouteStyle.Flat };
        for (int si = 0; si < styles.Length; si++)
        {
            var style = styles[si];
            progress.Report(Loc.F("Searching the course ({0}) …", StyleName(style)), si * 0.28);
            var v = RouteOf(s, e, vias, style, gravel, width, out string p);
            if (v == null) { lastProblem ??= p; continue; }
            if (list.Any(o => SameCourse(o.Road.Points, v.Road.Points))) continue;
            list.Add(v);
        }
        if (list.Count > 0 && list.All(v => v.Road.MaxGrade > routeGrade + 0.01))
        {
            progress.Report(Loc.T("Searching a serpentine …"), 0.84);
            var sb = Serpentine(s, e, vias, gravel, width, list.Min(v => v.Road.MaxGrade));
            if (sb != null && !list.Any(o => SameCourse(o.Road.Points, sb.Road.Points))) list.Add(sb);
        }
        SetStyle(RouteStyle.Natural);
        if (list.Count == 0) problem = lastProblem ?? Loc.T("No way found between these two places (water, steep walls, towns or other roads in the way).");
        return list;
    }

    public static string StyleName(RouteStyle s) => s switch
    {
        RouteStyle.Direct => Loc.T("direct"),
        RouteStyle.Flat => Loc.T("flat"),
        RouteStyle.Switchback => Loc.T("serpentine"),
        _ => Loc.T("natural"),
    };

    /// one course in this style (null + reason)
    public RouteVariant RouteOf(NetEnd s, NetEnd e, IList<Point> vias, RouteStyle style, bool gravel, double width, out string problem, NetworkPlan context = null)
    {
        SetStyle(style);
        var saved = blocked;
        blocked = (bool[])blocked.Clone();
        try
        {
            // the straight piece out of an existing road or a building entrance runs through the strip the grid blocks around existing
            // roads and buildings (the user's hint 2026-11-17: "the point … lies on an obstacle" for a point he had not set)
            FreeApproach(s, e.P);
            FreeApproach(e, s.P);
            foreach (var (end, other, word) in new[] { (s, e.P, Loc.T("start")), (e, s.P, Loc.T("end")) })
            {
                if (end.Road != null) continue;
                var a = end.P + OutOf(end, other) * ApproachLength;
                int c = CellOf(a.X, a.Y);
                c = OpenCellNear(a, c, k => !blocked[k]);
                if (c < 0 || blocked[c])
                {
                    problem = Loc.F("From the {0} the road cannot get away: within about 45 m in the direction of the other end there is water, a building, a town or the map edge. Please drag the point of the {0} a little.", word);
                    return null;
                }
            }
            bool cut = style == RouteStyle.Direct;
            var line = Between(context ?? new NetworkPlan(), null, s, e, vias, Array.Empty<Point>(), cut, out problem, out var ordered);
            if (line == null) return null;
            var road = MakeRoad(s, e, line, gravel, width, cut);
            road.Via = ordered.ToList();
            return new RouteVariant { Style = style, Road = road, Via = ordered.ToList() };
        }
        finally { blocked = saved; SetStyle(RouteStyle.Natural); }
    }

    /// free the cells along the straight piece out of an end (road, building entrance, free ground) in a strip of ±14 m up to 60 m,
    /// except where there is water, a building or tile (+6 m) or the map edge
    void FreeApproach(NetEnd end, Point other)
    {
        if (end.Road != null || end.Exit?.Tile != null) return;   // junction on a new road / town exit: handled by the planner
        var dir = OutOf(end, other);
        var side = new Vector(-dir.Y, dir.X);
        var near = w.PrefabSnapshot().Where(p => p.Def != null && p.Def.Kind != PrefabKind.Part && p != end.Exit?.Poi
            && p.X - 80 < end.P.X && p.X + p.SizeX + 80 > end.P.X && p.Z - 80 < end.P.Y && p.Z + p.SizeZ + 80 > end.P.Y).ToList();
        for (double d = 4; d <= 60; d += 4)
            for (double l = -14; l <= 14; l += 7)
            {
                var q = end.P + dir * d + side * l;
                int c = CellOf(q.X, q.Y);
                if (c < 0 || edge[c] || !blocked[c]) continue;
                var cc = Center(c);
                bool wet = false;
                for (int sz = -1; sz <= 1 && !wet; sz++)
                    for (int sx = -1; sx <= 1 && !wet; sx++)
                        wet = w.WaterAt((int)Math.Floor(cc.X + sx * 6), (int)Math.Floor(cc.Y + sz * 6));
                if (wet) continue;
                if (near.Any(p => cc.X > p.X - 6 - C / 2.0 && cc.X < p.X + p.SizeX + 6 + C / 2.0 && cc.Y > p.Z - 6 - C / 2.0 && cc.Y < p.Z + p.SizeZ + 6 + C / 2.0)) continue;
                blocked[c] = false;
            }
    }

    /// course of an existing variant through other via points (dragging on the map)
    public RouteVariant Reroute(RouteVariant v, NetEnd s, NetEnd e, IList<Point> vias, out string problem, NetworkPlan context = null) =>
        RouteOf(s, e, vias, v.Style, v.Road.Gravel, v.Road.Width, out problem, context);

    NetRoad MakeRoad(NetEnd s, NetEnd e, List<Point> line, bool gravel, double width, bool cut)
    {
        var r = new NetRoad
        {
            Points = line, Gravel = gravel, Width = width, Manual = true, Cut = cut,
            From = s.Exit, To = e.Exit, StartRoad = s.Road, EndRoad = e.Road, Junction = e.Road != null ? e.P : null,
        };
        SetStyle(RouteStyle.Natural);   // gradient measured against the usual values
        Remeasure(r);
        return r;
    }

    /// two lines practically the same: on average less than 12 m apart (both ways)
    static bool SameCourse(List<Point> a, List<Point> b)
    {
        double Mean(List<Point> x, List<Point> y)
        {
            var s = Resample(x, 16);
            return s.Count == 0 ? 0 : s.Average(p => Project(y, p).Off);
        }
        return Mean(a, b) < 12 && Mean(b, a) < 12;
    }

    /// Serpentine: the search with a hard limit at the usual gradient; if it still is too steep, zigzag via points across the slope
    /// (legs about 2 × amplitude long, as many as the height difference needs). The cheaper of both sides is taken.
    RouteVariant Serpentine(NetEnd s, NetEnd e, IList<Point> vias, bool gravel, double width, double bestGrade)
    {
        // only worth showing if it is really flatter than the other variants
        var v = SerpentineCore(s, e, vias, gravel, width, bestGrade);
        return v != null && v.Road.MaxGrade < bestGrade - 0.01 ? v : null;
    }

    RouteVariant SerpentineCore(NetEnd s, NetEnd e, IList<Point> vias, bool gravel, double width, double bestGrade)
    {
        var plain = RouteOf(s, e, vias, RouteStyle.Switchback, gravel, width, out _);
        if (plain != null && plain.Road.MaxGrade <= routeGrade + 0.01) return plain;
        if (vias.Count > 0) return plain;   // points set by hand: they decide the course
        double H(Point p) => w.HeightAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
        var d = e.P - s.P;
        double len = d.Length;
        if (len < 150) return plain;
        d.Normalize();
        var nrm = new Vector(-d.Y, d.X);
        double need = Math.Abs(H(e.P) - H(s.P)) / Math.Max(0.03, 0.85 * routeGrade);
        if (need <= len * 1.05) return plain;
        RouteVariant best = plain;
        foreach (int legs in new[] { 2, 3, 4, 5 })
        {
            double amp = Math.Clamp(need / (2.0 * legs), 70, 320);
            foreach (int sign in new[] { 1, -1 })
            {
                var pts = new List<Point>();
                for (int i = 0; i < legs; i++)
                {
                    double t = (i + 0.5) / legs;
                    pts.Add(s.P + d * (len * t) + nrm * (amp * (i % 2 == 0 ? sign : -sign)));
                }
                if (pts.Any(q => CellOf(q.X, q.Y) is int c && (c < 0 || blocked[c]))) continue;
                var v = RouteOf(s, e, pts, RouteStyle.Switchback, gravel, width, out _);
                if (v == null) continue;
                v.Style = RouteStyle.Switchback;
                if (best == null || v.Road.MaxGrade < best.Road.MaxGrade - 0.005 || (Math.Abs(v.Road.MaxGrade - best.Road.MaxGrade) <= 0.005 && v.Road.Length < best.Road.Length))
                    best = v;
            }
            if (best != null && best.Road.MaxGrade <= routeGrade + 0.01) break;
        }
        // only worth showing if it is really flatter than the others
        return best != null && best.Road.MaxGrade < bestGrade - 0.01 ? best : null;
    }

    // ---------- close a dead end: target found automatically ----------

    /// Best target for a road from a free town exit (the user's request 2026-11-17, "close a dead end"): places on existing roads that
    /// lead away (not those of the own town) and free exits of other towns, tried in the order of the straight distance (at most 8);
    /// the cheapest course wins, but only if it is not much longer than needed.
    public NetEnd AutoTarget(NetEnd start, bool gravel, double width, IProgress<string> progress, out string problem)
    {
        // first only what does not belong to the own town; if nothing can be reached, the roads made for the town itself (district, gateway
        // and ring roads) count as well (the user's hint 2026-11-20, Sodecu-Land 2890/1876: the road 100 m in front of a gateway exit)
        var res = AutoTargetPass(start, gravel, width, progress, false, out problem);
        return res ?? AutoTargetPass(start, gravel, width, progress, true, out problem);
    }

    /// one pass of AutoTarget; allowOwn = also roads close to the tiles of the own town (from 70 m)
    NetEnd AutoTargetPass(NetEnd start, bool gravel, double width, IProgress<string> progress, bool allowOwn, out string problem)
    {
        problem = null;
        var own = start.Exit?.Town;
        var sp = start.P;
        // own town = within 100 m of one of its tiles (the bounding box of all tiles is useless: cities joined by roads form one big group)
        bool OwnTown(Point p) => !allowOwn && own != null && own.Tiles.Any(t => p.X > t.X - 100 && p.X < t.X + t.SizeX + 100 && p.Y > t.Z - 100 && p.Y < t.Z + t.SizeZ + 100);
        var cands = new List<(NetEnd End, double D)>();
        foreach (var e in FreeTownExits)
        {
            if (e.Town == own) continue;
            double d = (new Point(e.X, e.Z) - sp).Length;
            if (d > 200 && d < 3000) cands.Add((new NetEnd(e, null, new Point(e.X, e.Z), new Vector(e.DX, e.DZ)), d));
        }
        // places on existing roads: every 48 m along the surface outside the tiles, in front of the exit preferred
        var seen = new List<Point>();
        int r = (int)Math.Ceiling(2500.0 / C);
        int c0 = CellOf(sp.X, sp.Y);
        if (c0 >= 0)
            for (int dj = -r; dj <= r; dj++)
                for (int di = -r; di <= r; di++)
                {
                    int i = c0 % gw + di, j = c0 / gw + dj;
                    if (i < 0 || j < 0 || i >= gw || j >= gw) continue;
                    int g = j * gw + i;
                    if (!roadCell[g] || tileCell[g]) continue;
                    var cp = Center(g);
                    double d = (cp - sp).Length;
                    if (d < (allowOwn ? 70 : 120) || d > 2500 || OwnTown(cp) || seen.Any(q => (q - cp).LengthSquared < 48 * 48)) continue;
                    var spot = RoadFollower.Center(routeScan, cp);
                    if (spot == null || spot.Width < 3 || routeScan.InTile(spot.Center.X, spot.Center.Y)) continue;
                    seen.Add(cp);
                    cands.Add((StreetEnd(spot), d));
                }
        if (cands.Count == 0) { problem = Loc.T("There is no road and no other free town exit within 2.5 km."); return null; }
        // direction out of the exit counts: places behind the town are much more expensive to reach
        var dir = start.Tangent;
        double Score((NetEnd End, double D) c)
        {
            var v = c.End.P - sp; v.Normalize();
            return c.D * (1.6 - 0.6 * (v * dir));
        }
        NetEnd best = null; double bestCost = double.MaxValue;
        int tried = 0, totalTry = Math.Min(8, cands.Count);
        foreach (var c in cands.OrderBy(Score).Take(8))
        {
            progress.Report(Loc.F("Trying target {0} of {1} …", ++tried, totalTry), (tried - 1) / (double)Math.Max(1, totalTry));
            var s1 = Orient(start, c.End.P);
            var e1 = Orient(c.End, sp);
            var v = RouteOf(s1, e1, Array.Empty<Point>(), RouteStyle.Natural, gravel, width, out string why);
            Log?.Invoke($"auto target {c.End.P.X:0}/{c.End.P.Y:0} ({c.D:0} m): {(v == null ? why : v.Road.Length.ToString("0") + " m")}");
            if (v == null) continue;
            double cost = v.Road.Length * (1 + 4 * Math.Max(0, v.Road.MaxGrade - routeGrade)) + (c.End.Exit?.Tile != null ? -150 : 0);
            if (cost < bestCost) { bestCost = cost; best = c.End; }
        }
        if (best == null) problem = Loc.T("No way to a road or another town exit was found from this exit (water, steep walls or towns in the way).");
        return best;
    }

    // ---------- connect buildings along the route ----------

    /// Gravel paths from buildings near the route to the route (the user's request 2026-11-17): buildings (POI) whose entrance lies at
    /// most maxDist from the route (not near its ends) and has no surface in front yet. One path per building, from the entrance nearest
    /// to the route to a place on it; paths that are not found are left out.
    public List<NetRoad> PoiFeeders(NetRoad route, double maxDist, double width, IProgress<string> progress, List<string> notes = null)
    {
        var res = new List<NetRoad>();
        if (route?.Points == null || route.Points.Count < 2) return res;
        double total = Project(route.Points, route.Points[^1]).Along;
        var bx0 = route.Points.Min(p => p.X) - maxDist - 100; var bx1 = route.Points.Max(p => p.X) + maxDist + 100;
        var bz0 = route.Points.Min(p => p.Y) - maxDist - 100; var bz1 = route.Points.Max(p => p.Y) + maxDist + 100;
        var pois = w.PrefabSnapshot().Where(pf => pf.Def?.Kind == PrefabKind.Poi && !pf.IsTile && !InTileArea(pf)
            && pf.X < bx1 && pf.X + pf.SizeX > bx0 && pf.Z < bz1 && pf.Z + pf.SizeZ > bz0).ToList();
        var context = new NetworkPlan();
        context.Roads.Add(route);
        for (int pi = 0; pi < pois.Count; pi++)
        {
            var pf = pois[pi];
            if (pf == route.From?.Poi || pf == route.To?.Poi) continue;
            var exits = PrefabParts.ExitsFor(w, pf);
            if (exits.Count == 0) continue;
            var x = exits.OrderBy(q => Project(route.Points, new Point(q.X, q.Z)).Off).First();
            var xp = new Point(x.X, x.Z);
            var (along, off) = Project(route.Points, xp);
            if (off > maxDist || off < 6 || along < 60 || along > total - 60) { Log?.Invoke($"poi {pf.Name}: off {off:0} along {along:0} of {total:0}"); continue; }
            var (dx, dz) = OutOfBuilding(pf, x.X, x.Z);
            if (Paved(x.X + dx * 2, x.Z + dz * 2) || Paved(x.X + dx * 4, x.Z + dz * 4)) { Log?.Invoke($"poi {pf.Name}: already paved"); continue; }   // already connected
            progress.Report(Loc.F("Connecting {0} …", pf.Name), pi / (double)Math.Max(1, pois.Count));
            var exit = new NetExit { Poi = pf, X = x.X, Z = x.Z, DX = dx, DZ = dz, Width = x.Width > 0 ? x.Width : 5 };
            var s = new NetEnd(exit, null, xp, new Vector(dx, dz));
            var (at, tan, _) = At(route.Points, along);
            var e = new NetEnd(null, route, at, tan);
            double pathWidth = Math.Min(width, Math.Max(3, exit.Width));
            var v = RouteOf(s, e, Array.Empty<Point>(), RouteStyle.Natural, true, pathWidth, out string why, context);
            NetRoad path = v?.Road;
            if (path == null && off < 120 && StraightFree(xp, at, pf))
            {
                // close by and the curve did not fit (the user's map Darobe-Land: cave_12 90 m beside the route): a straight path
                path = new NetRoad { Points = new List<Point> { xp, at }, Gravel = true, Width = pathWidth, Manual = true, From = exit, EndRoad = route, Junction = at };
                Remeasure(path);
            }
            // too close for the search and no direct connection (e.g. the entrance faces away from the route): join the route a bit
            // further along, so that the normal search has room
            foreach (double shift in new[] { 90.0, -90, 150, -150 })
            {
                if (path != null || off >= 2 * ApproachLength + 30) break;
                double a2 = along + shift;
                if (a2 < 60 || a2 > total - 60) continue;
                var (at2, tan2, _) = At(route.Points, a2);
                var v2 = RouteOf(s, new NetEnd(null, route, at2, tan2), Array.Empty<Point>(), RouteStyle.Natural, true, pathWidth, out why, context);
                if (v2 != null) path = v2.Road;
            }
            if (path == null || path.Length > Math.Max(3 * off, off + 250)) { Log?.Invoke($"poi {pf.Name}: no course ({why}) len {path?.Length:0} off {off:0}"); notes?.Add(path == null ? Loc.F("No path to {0} found (water, steep walls or buildings in the way).", pf.Name) : Loc.F("No path to {0}: it would be {1:0} m long for {2:0} m distance (the entrance faces away from the route).", pf.Name, path.Length, off)); continue; }
            res.Add(path);
            context.Roads.Add(path);
        }
        return res;
    }

    bool Paved(double x, double z) => w.RoadAt((int)Math.Floor(x), (int)Math.Floor(z)) != 0;

    /// straight line a → b without water and without buildings (except own, the first 8 m) on it
    bool StraightFree(Point a, Point b, PlacedPrefab own)
    {
        double len = (b - a).Length;
        var near = w.PrefabSnapshot().Where(p => p.Def != null && p.Def.Kind != PrefabKind.Part
            && p.X < Math.Max(a.X, b.X) + 10 && p.X + p.SizeX > Math.Min(a.X, b.X) - 10 && p.Z < Math.Max(a.Y, b.Y) + 10 && p.Z + p.SizeZ > Math.Min(a.Y, b.Y) - 10).ToList();
        for (double d = 0; d <= len; d += 2)
        {
            var q = a + (b - a) * (d / Math.Max(1, len));
            if (w.WaterAt((int)Math.Floor(q.X), (int)Math.Floor(q.Y))) return false;
            if (near.Any(p => !(p == own && d < 8) && q.X > p.X - 3 && q.X < p.X + p.SizeX + 3 && q.Y > p.Z - 3 && q.Y < p.Z + p.SizeZ + 3)) return false;
        }
        return true;
    }

    bool InTileArea(PlacedPrefab pf)
    {
        double cx = pf.X + pf.SizeX / 2.0, cz = pf.Z + pf.SizeZ / 2.0;
        return routeScan.InTile(cx, cz);
    }

    // ---------- re-lay an existing road ----------

    /// state of the grid before re-laying (restored by EndRelay)
    bool[] relayBlocked;
    float[] relayNearD, relayNearH;
    List<(int From, int To, float Cost, Point Mid, int Dir)> relayJumps;

    /// Re-lay a piece of an existing road (the user's request 2026-11-17): the old road between a and b (traced along its surface) no
    /// longer blocks the search, so the new course may use its corridor; both ends become junctions with the old road that continue
    /// along it. Returns the trace (old course and width) and the two ends; EndRelay must be called afterwards.
    public (TraceResult Old, NetEnd Start, NetEnd End)? BeginRelay(Point a, Point b, out string problem)
    {
        var tr = RoadTracer.Trace(w, a, b, out problem);
        if (tr == null) return null;
        if (tr.Length < 200) { problem = Loc.T("The piece of road is too short to be laid differently (at least 200 m)."); return null; }
        var pts = tr.Points;
        var sa = RoadFollower.Center(routeScan, pts[0]);
        var sb = RoadFollower.Center(routeScan, pts[^1]);
        if (sa == null || sb == null) { problem = Loc.T("The ends of the piece are not on a road."); return null; }
        var (_, da, _) = At(pts, 20);
        var (_, db, _) = At(pts, tr.Length - 20);
        var s = Orient(StreetEnd(sa), pts[^1], da);
        var e = Orient(StreetEnd(sb), pts[0], -db);
        // free the corridor of the old road (except the first and last 40 m, which stay as they are)
        relayBlocked = (bool[])blocked.Clone();
        relayNearD = (float[])baseNearD.Clone();
        relayNearH = (float[])baseNearH.Clone();
        relayJumps = streetJumps.ToList();
        double halfW = tr.Width / 2 + 24;
        var core = Slice(pts, 40, tr.Length - 40);
        var freed = new HashSet<int>();
        if (core.Count >= 2)
        {
            double x0 = core.Min(p => p.X) - halfW, x1 = core.Max(p => p.X) + halfW, z0 = core.Min(p => p.Y) - halfW, z1 = core.Max(p => p.Y) + halfW;
            for (double z = z0; z <= z1; z += C)
                for (double x = x0; x <= x1; x += C)
                {
                    int c = CellOf(x, z);
                    if (c < 0 || edge[c]) continue;
                    if (Project(core, Center(c)).Off <= halfW) freed.Add(c);
                }
            foreach (int c in freed) blocked[c] = false;
            // water and buildings stay obstacles
            foreach (int c in freed)
            {
                var cp = Center(c);
                for (int sz = -1; sz <= 1 && !blocked[c]; sz++)
                    for (int sx = -1; sx <= 1 && !blocked[c]; sx++)
                        if (w.WaterAt((int)Math.Floor(cp.X + sx * 6), (int)Math.Floor(cp.Y + sz * 6))) blocked[c] = true;
            }
            foreach (var pf in w.PrefabSnapshot().Where(pf => pf.Def != null && pf.X < x1 + 30 && pf.X + pf.SizeX > x0 - 30 && pf.Z < z1 + 30 && pf.Z + pf.SizeZ > z0 - 30))
                Mark(blocked, pf.X, pf.Z, pf.X + pf.SizeX, pf.Z + pf.SizeZ, pf.IsTile ? 24 : pf.Def.Kind == PrefabKind.Part ? 4 : 12);
            // the old road no longer counts for "runs alongside", and it cannot be crossed (it disappears)
            var oldCells = new HashSet<int>(freed.Where(c => roadCell[c]));
            RecomputeNear(oldCells, x0 - ParallelRange, z0 - ParallelRange, x1 + ParallelRange, z1 + ParallelRange);
            streetJumps.RemoveAll(j => Project(core, j.Mid).Off < tr.Width / 2 + 10);
        }
        return (tr, s, e);
    }

    /// Paths that join the old road between from and to (m along it): after re-laying they would end in nothing (the user's map
    /// Nilicu-Land: three wilderness paths on 600 m). Each gets a short connection from where its surface stays (beside the removed
    /// road bed) to the nearest place on the new course (gravel, its width).
    public List<NetRoad> ReconnectBranches(List<Point> oldLine, double oldWidth, double from, double to, List<NetRoad> legs, IProgress<string> progress)
    {
        var res = new List<NetRoad>();
        var s = RoadSettings.Remove();
        s.Width = oldWidth + 2;
        s.KeepCrossings = true;
        var removal = RoadPlanner.Compute(w, NetworkBuilder.Even(oldLine, 12), s);
        if (removal == null) return res;
        var context = new NetworkPlan();
        context.Roads.AddRange(legs);
        foreach (var c in removal.CrossRoads.Where(c => !c.Through && c.Dist > from && c.Dist < to))
        {
            var outDir = c.A - c.B;   // from the middle of the old road out along the branch
            if (outDir.Length < 1) continue;
            outDir.Normalize();
            var spot = RoadFollower.Center(routeScan, c.A + outDir * 10);
            if (spot == null) { Log?.Invoke($"branch {c.A}: no road beside"); continue; }
            var (leg, d) = legs.Select(l => (l, Project(l.Points, spot.Center).Off)).OrderBy(x => x.Off).First();
            double legHalf = leg.Width / 2 + (leg.Gravel ? 0 : 1);
            if (d <= legHalf + 2) { Log?.Invoke($"branch {c.A}: on the new course"); continue; }
            progress?.Report(Loc.T("Connecting the paths of the old road …"));
            var along = Project(leg.Points, spot.Center).Along;
            double len = Project(leg.Points, leg.Points[^1]).Along;
            if (along < 40 || along > len - 40) { Log?.Invoke($"branch {c.A}: at the end of the new course"); continue; }
            var (at, tan, _) = At(leg.Points, along);
            var toward = at - spot.Center; toward.Normalize();
            var start = Orient(StreetEnd(spot), at, toward);
            double width = Math.Clamp(Math.Round(spot.Width - 1), 3, 8);
            NetRoad road;
            if (d < 60)
            {
                // close to the new course: a straight piece across the gap (the search needs at least about 20 m)
                road = new NetRoad { Points = new List<Point> { spot.Center, at }, Gravel = true, Width = width, Manual = true, From = start.Exit, EndRoad = leg, Junction = at };
                Remeasure(road);
            }
            else
            {
                var v = RouteOf(start, new NetEnd(null, leg, at, tan), Array.Empty<Point>(), RouteStyle.Natural, true, width, out _, context);
                if (v == null || v.Road.Length > Math.Max(3 * d, d + 250)) { Log?.Invoke($"branch {c.A}: no course ({d:0} m, {v?.Road.Length:0} m)"); continue; }
                road = v.Road;
            }
            res.Add(road);
            context.Roads.Add(road);
        }
        return res;
    }

    /// restore the grid after re-laying
    public void EndRelay()
    {
        if (relayBlocked == null) return;
        blocked = relayBlocked;
        Array.Copy(relayNearD, baseNearD, n);
        Array.Copy(relayNearH, baseNearH, n);
        streetJumps.Clear();
        streetJumps.AddRange(relayJumps);
        relayBlocked = null; relayNearD = relayNearH = null; relayJumps = null;
    }

    /// distance to the nearest existing road (for "runs alongside") again in a box, without the given road cells
    void RecomputeNear(HashSet<int> without, double x0, double z0, double x1, double z1)
    {
        int i0 = Math.Max(0, (int)Math.Floor((x0 + half) / C)), i1 = Math.Min(gw - 1, (int)Math.Floor((x1 + half) / C));
        int j0 = Math.Max(0, (int)Math.Floor((z0 + half) / C)), j1 = Math.Min(gw - 1, (int)Math.Floor((z1 + half) / C));
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++) { baseNearD[j * gw + i] = float.MaxValue; baseNearH[j * gw + i] = 0; }
        int pr = (int)Math.Ceiling(ParallelRange / C);
        for (int j = Math.Max(0, j0 - pr); j <= Math.Min(gw - 1, j1 + pr); j++)
            for (int i = Math.Max(0, i0 - pr); i <= Math.Min(gw - 1, i1 + pr); i++)
            {
                int g = j * gw + i;
                if (!roadCell[g] || tileCell[g] || without.Contains(g)) continue;
                for (int b = Math.Max(j0, j - pr); b <= Math.Min(j1, j + pr); b++)
                    for (int a = Math.Max(i0, i - pr); a <= Math.Min(i1, i + pr); a++)
                    {
                        float d = (float)(C * Math.Sqrt((a - i) * (a - i) + (b - j) * (b - j)));
                        int c = b * gw + a;
                        if (d < ParallelRange && d < baseNearD[c]) { baseNearD[c] = d; baseNearH[c] = hb[g]; }
                    }
            }
    }
}
