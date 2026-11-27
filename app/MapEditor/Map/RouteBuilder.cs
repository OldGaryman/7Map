using System.Windows;

namespace MapEditor.Map;

/// everything a route consists of when it is applied (the user's request 2026-11-17, road generation from point to point)
public sealed class RouteJob
{
    public List<NetRoad> Roads = new();           // in build order: connectors of tiles, the pieces of the route, paths to buildings
    public List<PlacedPrefab> Tiles = new();      // gateway tiles (T, X, rest stop)
    public List<List<Point>> Remove = new();      // old surface to remove first (center line), with its width
    public List<double> RemoveWidth = new();
    public bool KeepCrossings = true;             // keep crossing roads during Remove (false when BridgeSwap re-lays branches itself)
    public bool ClipRemoveEnd;                    // clip Remove cleanly at the outer joint plane so the untouched road and hillside beyond it stay intact
    public bool ReshapeRemove = true;             // fill the removed road bed naturally (false when BridgeSwap immediately rebuilds on the same road bed)
}

/// Builds a route as one edit step: first the old surface goes (re-laid road, the piece of an existing road under a junction tile),
/// with the road bed blended into the terrain; then tiles and roads like "Build roads" (NetworkBuilder).
public static class RouteBuilder
{
    const int SliceLength = 1600;

    public static NetworkBuildResult Build(WorldData w, WorldEdits e, RouteJob job, NetworkSpec spec, IProgress<string> progress,
        IReadOnlyList<(PlacedPrefab Building, List<PlacedPrefab> Parts)> lotBuildings = null)
    {
        e.GuardBanks();
        var quiet = new Progress<string>(_ => { });
        static double PolyLen(List<Point> pts) { double l = 0; for (int i = 1; i < pts.Count; i++) l += (pts[i] - pts[i - 1]).Length; return l; }
        double remLen = job.Remove.Where(p => p.Count >= 2).Sum(PolyLen);
        double addLen = job.Roads.Sum(r => 60.0 + Math.Max(40.0, r.Length)) + job.Tiles.Count * 140.0;
        double remShare = remLen > 0 ? Math.Clamp(remLen / Math.Max(1.0, remLen + addLen), 0.12, 0.50) : 0.0;
        double remDone = 0;
        for (int k = 0; k < job.Remove.Count; k++)
        {
            var pts = job.Remove[k];
            if (pts.Count < 2) continue;
            double len = PolyLen(pts);
            progress.Report(Loc.T("Removing the old road …"), remShare * (remDone / Math.Max(1.0, remLen)));
            var s = RoadSettings.Remove();
            s.Width = job.RemoveWidth[k] + 2;
            s.KeepCrossings = job.KeepCrossings;
            s.Reshape = job.ReshapeRemove;
            var r = RoadPlanner.Compute(w, NetworkBuilder.Even(pts, 12), s);
            if (r == null) { remDone += len; continue; }
            if (job.ClipRemoveEnd) r.ClipEnd = true;
            for (int from = 0; from < r.Path.Count; from += SliceLength)
                RoadApplier.Apply(w, e, r, quiet, from, Math.Min(r.Path.Count, from + SliceLength), 4);
            remDone += len;
        }
        // where a new road meets an existing one (end on a road, crossing): remember the surface there, afterwards the new surface beside
        // the old one runs out onto its heights (the user's game test 2026-11-17, Darobe-Land: edges of up to 0.5 m where the new
        // road, built as a tilted plane, met the old road with its own banking and curve) – as with the wilderness paths
        var joints = new List<(Point P, (byte[] Mask, ushort[] Heights) Snap, uint[] Splat)>();
        int jr = WildernessGenerator.JunctionWindow, js = 2 * jr + 1;
        // a road laid again on an old one (NetRoad.OnOldRoad, branch after a bridge swap) gets new heights: its old surface is not put back
        // (only at its outer start, where it keeps its old height anyway)
        static double Dist(List<Point> l, Point p)
        {
            double best = double.MaxValue;
            for (int i = 1; i < l.Count; i++)
            {
                var a = l[i - 1]; var v = l[i] - a; double ll = v.LengthSquared;
                double t = ll < 1e-9 ? 0 : Math.Clamp((p - a) * v / ll, 0, 1);
                best = Math.Min(best, (a + v * t - p).Length);
            }
            return best;
        }
        var relaid = job.Roads.Where(r => r.OnOldRoad && r.Points.Count >= 2).ToList();
        // its old surface only leaves the road mask (heights unchanged): otherwise the junction protection keeps the old heights at its mouth.
        // Only clear strictly AHEAD of Points[0] (starting 1 m along the branch toward the mouth) – never behind Points[0] (otherwise a
        // crescent hole is cut behind its start: Land-Of-Fear 3561/2153), and not 6 m ahead either (otherwise a sloped old branch drops
        // ~0.7 m over those 6 m before UniformGrade takes over, leaving a step at the Seam edge: Land-Of-Fear -627/1985).
        static List<Point> TrimStart(List<Point> l, double skip)
        {
            double walked = 0;
            for (int i = 1; i < l.Count; i++)
            {
                var v = l[i] - l[i - 1];
                double seg = v.Length;
                if (walked + seg >= skip)
                {
                    double t = seg < 1e-9 ? 0 : (skip - walked) / seg;
                    var res = new List<Point> { l[i - 1] + v * t };
                    for (int j = i; j < l.Count; j++) res.Add(l[j]);
                    return res;
                }
                walked += seg;
            }
            return new List<Point>();
        }
        foreach (var r in relaid)
        {
            var fwd = r.Points[1] - r.Points[0];
            if (fwd.Length < 1e-6) continue;
            fwd.Normalize();
            var clearPts = TrimStart(r.Points, 1);
            if (clearPts.Count < 2) continue;
            double reach = r.Width / 2 + 3.5;
            int x0 = (int)Math.Floor(clearPts.Min(p => p.X) - reach), x1 = (int)Math.Ceiling(clearPts.Max(p => p.X) + reach);
            int z0 = (int)Math.Floor(clearPts.Min(p => p.Y) - reach), z1 = (int)Math.Ceiling(clearPts.Max(p => p.Y) + reach);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    if (!w.Contains(x, z)) continue;
                    var c = new Point(x + 0.5, z + 0.5);
                    if ((c - r.Points[0]) * fwd < 1 || Dist(clearPts, c) > reach) continue;
                    int i = w.Index(x, z);
                    if (w.RoadMask[i] == 0) continue;
                    e.SetSplat(x, z, WorldEdits.EmptyPixel);
                    e.SetRoad(i, 0);
                }
        }
        void Joint(Point p)
        {
            if (joints.Any(j => (j.P - p).Length < 20)) return;
            if (relaid.Any(r => Dist(r.Points, p) < jr && (p - r.Points[0]).Length > 20)) return;
            var snap = WildernessGenerator.Snapshot(w, p, jr);
            // surface of the old road too: the gravel edge of the new road must not be painted across its asphalt (a line across the road in the game)
            var splat = new uint[js * js];
            int sx0 = (int)Math.Floor(p.X) - jr, sz0 = (int)Math.Floor(p.Y) - jr;
            for (int g = 0; g < js * js; g++)
                if (snap.Mask[g] != 0 && w.Contains(sx0 + g % js, sz0 + g / js)) splat[g] = e.SplatAt(sx0 + g % js, sz0 + g / js);
            joints.Add((p, snap, splat));
        }
        foreach (var r in job.Roads)
        {
            foreach (var x in new[] { r.From, r.To })
                if (x?.Street == true) Joint(new Point(x.X, x.Z));
            foreach (var sc in NetworkBuilder.StreetCrossings(w, r.Points, new List<Point>())) Joint(sc.P);
        }
        var plan = new NetworkPlan();
        plan.NodeTiles.AddRange(job.Tiles);
        plan.Roads.AddRange(job.Roads);
        var res = NetworkBuilder.Build(w, e, plan, spec, progress.Slice(remShare, 0.98), lotBuildings);
        foreach (var (p, snap, splat) in joints)
        {
            // the old road keeps exactly its heights and its surface (the new one only joins it) …
            int x0 = (int)Math.Floor(p.X) - jr, z0 = (int)Math.Floor(p.Y) - jr;
            for (int g = 0; g < js * js; g++)
            {
                int x = x0 + g % js, z = z0 + g / js;
                if (snap.Mask[g] == 0 || !w.Contains(x, z)) continue;
                int i = w.Index(x, z);
                if (w.Heights[i] != snap.Heights[g]) e.SetHeight(i, snap.Heights[g]);
                // surface: only asphalt of the old road comes back (as in the asphalt tool, gravel never paints over asphalt); its gravel
                // edge that the new road paved stays asphalt (the user's game test 2026-11-17: a gravel strip across the mouth)
                if (snap.Mask[g] == WorldData.Asphalt && w.RoadMask[i] != WorldData.Asphalt) { e.SetSplat(x, z, splat[g]); e.SetRoad(i, WorldData.Asphalt); }
            }
            // corners of the mouth: gravel edge pixels (of the old or the new road) between old and new asphalt become asphalt.
            // Repeated until nothing changes: a paved pixel can put the next gravel pixel between old and new asphalt
            // (test 2026-11-24: one gravel pixel was left in the corner after a single pass)
            for (int pass = 0; pass < 8; pass++)
            {
            var pave = new List<(int X, int Z)>();
            for (int g = 0; g < js * js; g++)
            {
                int x = x0 + g % js, z = z0 + g / js;
                if (!w.Contains(x, z) || w.RoadMask[w.Index(x, z)] != WorldData.Gravel || snap.Mask[g] == WorldData.Asphalt) continue;
                bool oldA = false, newA = false;
                // 4-connected neighbors only: diagonal neighbors falsely turned outer gravel-shoulder pixels at colinear joints into asphalt spikes (user photo 2026-11-27, Minimi -301,-250)
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx, nz = z + dz, ng = (nz - z0) * js + (nx - x0);
                    if (!w.Contains(nx, nz) || nx < x0 || nz < z0 || nx >= x0 + js || nz >= z0 + js || w.RoadMask[w.Index(nx, nz)] != WorldData.Asphalt) continue;
                    if (snap.Mask[ng] == WorldData.Asphalt) oldA = true; else if (snap.Mask[ng] == 0) newA = true;   // new = where there was no road before
                }
                if (oldA && newA) pave.Add((x, z));
            }
            foreach (var (x, z) in pave) { e.SetSplat(x, z, WorldEdits.AsphaltPixel); e.SetRoad(w.Index(x, z), WorldData.Asphalt); }
            if (pave.Count == 0) break;
            }
            // keep the 1-pixel gravel shoulder continuous where a clipped joint cut nicked an old gravel-edge cell next to asphalt
            for (int g = 0; g < js * js; g++)
            {
                int x = x0 + g % js, z = z0 + g / js;
                if (snap.Mask[g] != WorldData.Gravel || !w.Contains(x, z) || w.RoadMask[w.Index(x, z)] != 0) continue;
                bool nextA = false;
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    if (w.Contains(x + dx, z + dz) && w.RoadMask[w.Index(x + dx, z + dz)] == WorldData.Asphalt) { nextA = true; break; }
                if (nextA) { e.SetSplat(x, z, WorldEdits.GravelPixel); e.SetRoad(w.Index(x, z), WorldData.Gravel); }
            }
            // … and the new surface runs out onto it over 6 m
            Seam(w, e, p, snap);
        }
        return res;
    }

    const int SeamReach = 4;   // new surface up to this far beside the old one runs out onto it (m)

    /// The new surface beside the old road runs out onto the old road's surface: for every new or changed pixel up to SeamReach m from the
    /// old surface a plane is fitted through the old pixels within 5 m (so a road tilted across – up to 15 % per metre on Darobe-Land –
    /// is continued with its slope; the height of the nearest old pixel made dents there), and the pixel is blended from that plane
    /// (at the seam) to its own height (SeamReach away).
    internal static void Seam(WorldData w, WorldEdits e, Point c, (byte[] Mask, ushort[] Heights) snap, int r = WildernessGenerator.JunctionWindow)
    {
        int s = 2 * r + 1, x0 = (int)Math.Floor(c.X) - r, z0 = (int)Math.Floor(c.Y) - r;
        bool Old(int x, int z) => x >= 0 && z >= 0 && x < s && z < s && snap.Mask[z * s + x] != 0;
        var updates = new List<(int I, ushort H)>();
        for (int z = 0; z < s; z++)
            for (int x = 0; x < s; x++)
            {
                if (Old(x, z) || !w.Contains(x0 + x, z0 + z)) continue;
                int i = w.Index(x0 + x, z0 + z);
                bool changed = w.Heights[i] != snap.Heights[z * s + x];
                if (!changed && w.RoadAt(x0 + x, z0 + z) == 0) continue;   // untouched terrain stays
                // distance to the old surface and plane through the old pixels nearby (least squares, relative to this pixel)
                double dmin = double.MaxValue, n = 0, sx = 0, sz = 0, sh = 0, sxx = 0, szz = 0, sxz = 0, sxh = 0, szh = 0;
                for (int dz = -5; dz <= 5; dz++)
                    for (int dx = -5; dx <= 5; dx++)
                    {
                        if (!Old(x + dx, z + dz)) continue;
                        double d = Math.Sqrt(dx * dx + dz * dz);
                        if (d > 5) continue;
                        dmin = Math.Min(dmin, d);
                        double h = snap.Heights[(z + dz) * s + x + dx] / 256.0;
                        n++; sx += dx; sz += dz; sh += h; sxx += dx * dx; szz += dz * dz; sxz += dx * dz; sxh += dx * h; szh += dz * h;
                    }
                if (dmin > SeamReach || n < 6) continue;
                // solve h = a + b·dx + c·dz
                double mx = sx / n, mz = sz / n, mh = sh / n;
                double cxx = sxx / n - mx * mx, czz = szz / n - mz * mz, cxz = sxz / n - mx * mz, cxh = sxh / n - mx * mh, czh = szh / n - mz * mh;
                double det = cxx * czz - cxz * cxz;
                double planeH = mh;
                if (Math.Abs(det) > 1e-6)
                {
                    double b = (cxh * czz - czh * cxz) / det, cc = (czh * cxx - cxh * cxz) / det;
                    planeH = mh - b * mx - cc * mz;   // value at dx = dz = 0
                }
                double t = Math.Clamp((dmin - 1) / (SeamReach - 1), 0, 1), k = t * t * (3 - 2 * t);
                double hNew = w.Heights[i] / 256.0;
                double hv = planeH + (hNew - planeH) * k;
                updates.Add((i, (ushort)Math.Clamp(Math.Round(hv * 256), 0, 65535)));
            }
        foreach (var (i, h) in updates) if (w.Heights[i] != h) e.SetHeight(i, h);
    }

    /// profile of one road for the preview (height chart): planned like when building, without connections to other new roads
    public static RoadResult Preview(WorldData w, NetRoad road, NetworkSpec spec)
    {
        var plan = new NetworkPlan();
        plan.Roads.Add(road);
        return NetworkBuilder.PlanRoad(w, plan, road, spec, new Dictionary<NetRoad, RoadResult>(), new NetworkBuildResult());
    }
}
