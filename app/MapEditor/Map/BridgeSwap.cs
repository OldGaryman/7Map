using System.Windows;

namespace MapEditor.Map;

/// one bridge kit for a place: Site.Problem == null = it fits there
public sealed record BridgeOption(BridgeKitDef Kit, BridgeSite Site, bool Current);

/// a bridge built on the map: its pieces (end – middle pieces – end) and the place they form
public sealed class BuiltBridge
{
    public BridgeSite Site;
    public BridgeKitDef Kit;
    public List<PlacedPrefab> Pieces = new();
}

/// result of swapping a built bridge
public sealed class BridgeSwapResult
{
    public BridgeSite Site;
    public int Roads;            // sides with a road that was joined again (0–2)
    public NetRoad Road;         // summary of the road over the bridge (null = none)
    public List<NetRoad> BranchRoads = new();   // branches adjusted/connected in front of the bridge
    public int Branches => BranchRoads.Count;
    public List<string> Warnings = new();
}

/// Swap a bridge for one of another kit (the user's request 2026-11-25): in the plan of "Generate roads" and of the wilderness POIs the
/// bridge and the straight piece of the line over it are replaced; a bridge built on the map keeps its existing road intact —
/// the deck height is preserved whenever the new kit allows it, any remaining height difference is faded smoothly along the existing
/// road and its side branches via RoadReprofile (pure relative height delta, zero road demolition), and only the short width taper
/// right behind each new approach adjusts the road surface.
public static class BridgeSwap
{
    /// the river may run this far off a compass direction (the place was accepted before, a built bridge made the river straight)
    public const double MaxAngle = 45;
    /// legacy public constant kept for reference (corridor & tests)
    public const double JoinLength = 100;
    /// how far outward the existing road is traced on each side to plan a smooth height ramp and find side branches
    const double MaxTrace = 260;
    /// distance beyond the approach where the road has reached its normal width (outside any previous bridge taper)
    const double TaperMeasure = 60;

    static double AlongOf(bool alongX, Point p) => alongX ? p.X : p.Y;
    static double AcrossOf(bool alongX, Point p) => alongX ? p.Y : p.X;
    static double CenterS(BridgeSite b) => b.S0 + b.Length / 2.0;
    static Vector Axis(BridgeSite b) => b.AlongX ? new Vector(1, 0) : new Vector(0, 1);
    static bool Is(string a, string b) => !string.IsNullOrEmpty(a) && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// the same place with another kit: centered on the old bridge, same direction, same surface, keeping deck height when the new kit allows it
    public static BridgeSite Refit(WorldData w, BridgeSite old, BridgeKitDef kit, IReadOnlyList<PlacedPrefab> prefabs = null, Func<int, int, bool> roadIgnored = null)
    {
        var c = old.Center;
        // The existing bridge already straightened the river banks to its water width, so margin = 0 prevents repeated multi-piece swaps
        // from adding +1 middle piece (+10 m river width) on every swap until the river turns into a lake.
        var s = Bridge.Find(w, c.X, c.Y, kit, MaxAngle, prefabs, roadIgnored, narrow: true, margin: 0);   // a too wide river is narrowed (the user asks first)
        s.Gravel = old.Gravel;
        if (s.Problem != null) return s;
        if (s.AlongX != old.AlongX) { s.Problem = Loc.T("This bridge would run in another direction here."); return s; }
        // Preserve the existing deck height whenever the new kit's piers/underwater bed allow it so swapping compatible bridges
        // causes 0 m height change on the road and banks.
        int minDeck = s.Level + s.Clearance;
        int maxDeck = Math.Max(minDeck, s.Level + s.DeckLayer - 1 - (int)Math.Ceiling(Math.Max(0, s.BedAbove)));
        if (old.Deck > 0) s.Deck = Math.Clamp(old.Deck, minDeck, maxDeck);
        // centered on the first bridge of this place (always the same middle: swapping back gives exactly the old place)
        double aS = old.AnchorS ?? CenterS(old), aT = old.AnchorT ?? old.MidT;
        s.AnchorS = aS; s.AnchorT = aT;
        s.S0 = (int)Math.Floor(aS - s.Length / 2.0 + 0.5);
        s.T0 = (int)Math.Floor(aT - s.Width / 2.0 + 0.5);
        return s;
    }

    /// every kit for the place of a bridge (the current one first is not required: in the order given)
    public static List<BridgeOption> Options(WorldData w, BridgeSite old, IEnumerable<BridgeKitDef> kits, IReadOnlyList<PlacedPrefab> prefabs = null, Func<int, int, bool> roadIgnored = null)
    {
        string cur = old.Kit ?? BridgeKitDef.YameeId;
        return kits.Select(k => k.Id == cur ? new BridgeOption(k, old, true) : new BridgeOption(k, Refit(w, old, k, prefabs, roadIgnored), false)).ToList();
    }

    /// The straight piece of a planned line over a bridge (lead, approach, bridge, approach, lead – see RoadNetwork.AddBridgePoints) for
    /// another bridge at the same place. Points beyond the old piece that a longer bridge covers are left out (the line would go back
    /// there). null = the line does not run over the bridge.
    public static List<Point> ReplaceOnLine(List<Point> line, BridgeSite old, BridgeSite nu)
    {
        if (line == null || line.Count < 2) return null;
        bool ax = old.AlongX;
        double cs = CenterS(old), mt = old.MidT;
        double newReach = nu.Length / 2.0 + Bridge.Approach + RoadNetwork.BridgeLead;
        bool Near(Point p) => Math.Abs(AcrossOf(ax, p) - mt) < 3;
        int k = -1;
        double best = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            double s0 = AlongOf(ax, line[i]) - cs, s1 = AlongOf(ax, line[i + 1]) - cs;
            if (s0 * s1 > 0 || !Near(line[i]) || !Near(line[i + 1])) continue;
            double d = Math.Abs(s0) + Math.Abs(s1);
            if (d < best) { best = d; k = i; }
        }
        if (k < 0) return null;
        int sg = AlongOf(ax, line[k + 1]) > AlongOf(ax, line[k]) ? 1 : -1;
        bool Covered(Point p) => Math.Abs(AlongOf(ax, p) - cs) <= newReach + 2 && Math.Abs(AcrossOf(ax, p) - mt) < 40;
        int j0 = k, j1 = k + 1;
        while (j0 > 1 && Covered(line[j0 - 1])) j0--;
        while (j1 < line.Count - 2 && Covered(line[j1 + 1])) j1++;
        var (a, b, _) = NetworkBuilder.ApproachEnds(nu);
        var u = Axis(nu);
        var seq = new List<Point> { a - u * RoadNetwork.BridgeLead, a, b, b + u * RoadNetwork.BridgeLead };
        if (sg < 0) seq.Reverse();
        bool OnPiece(Point p) => Near(p) && Math.Abs(AlongOf(ax, p) - cs) <= newReach + 2;
        if (j0 == 0 && !OnPiece(line[0])) j0 = 1;
        if (j1 == line.Count - 1 && !OnPiece(line[^1])) j1 = line.Count - 2;
        var res = new List<Point>();
        res.AddRange(line.Take(j0));
        res.AddRange(seq);
        res.AddRange(line.Skip(j1 + 1));
        return res;
    }

    static double LineLength(List<Point> pts)
    {
        double l = 0;
        for (int i = 1; i < pts.Count; i++) l += (pts[i] - pts[i - 1]).Length;
        return l;
    }

    /// swap a bridge of a planned road: bridge and line; false = the line does not run over it
    public static bool InPlan(NetRoad road, int index, BridgeSite nu)
    {
        var old = road.Bridges[index];
        var line = ReplaceOnLine(road.Points, old, nu);
        if (line == null) return false;
        nu.Gravel = old.Gravel;
        road.Points = line;
        road.Bridges[index] = nu;
        road.Samples = RoadNetwork.Resample(line, 16);
        road.Length = road.Samples.Count > 1 ? (road.Samples.Count - 1) * 16.0 : 0;
        return true;
    }

    /// swap a bridge of a planned wilderness path
    public static bool InPlan(WildPath path, int index, BridgeSite nu)
    {
        var old = path.Bridges[index];
        var line = ReplaceOnLine(path.Points, old, nu);
        if (line == null) return false;
        nu.Gravel = old.Gravel;
        if (path.FixedHeights != null) path.FixedHeights = null;   // they belonged to the old points (only the volcano paths have them)
        path.Points = line;
        path.Bridges[index] = nu;
        path.Length = LineLength(line);
        return true;
    }

    /// Reads the actual water level under a built bridge from WaterMask (so a bridge whose deck was kept above Level + Clearance
    /// still reports the true river level on subsequent swaps). Falls back to Deck - Clearance if no water is found.
    static int WaterLevelUnder(WorldData w, BridgeSite site)
    {
        var counts = new Dictionary<byte, int>();
        int s0 = site.S0 + site.LandUnder, s1 = site.S0 + site.Length - 1 - site.LandUnder;
        int t0 = site.T0, t1 = site.T0 + site.Width - 1;
        for (int s = s0; s <= s1; s++)
            for (int t = t0; t <= t1; t++)
            {
                int x = site.AlongX ? s : t, z = site.AlongX ? t : s;
                if (!w.Contains(x, z)) continue;
                byte lv = w.WaterMask[w.Index(x, z)];
                if (lv != 0) counts[lv] = counts.TryGetValue(lv, out int c) ? c + 1 : 1;
            }
        if (counts.Count == 0)
        {
            int cx = (int)Math.Floor(site.Center.X), cz = (int)Math.Floor(site.Center.Y);
            for (int dz = -8; dz <= 8; dz++)
                for (int dx = -8; dx <= 8; dx++)
                {
                    if (!w.Contains(cx + dx, cz + dz)) continue;
                    byte lv = w.WaterMask[w.Index(cx + dx, cz + dz)];
                    if (lv != 0) counts[lv] = counts.TryGetValue(lv, out int c) ? c + 1 : 1;
                }
        }
        return counts.Count > 0 ? counts.OrderByDescending(kv => kv.Value).First().Key : site.Deck - site.Clearance;
    }

    /// the bridge a placed piece belongs to (pieces of one kit in a row at the same height); null = not a whole bridge
    public static BuiltBridge FromPieces(WorldData w, PlacedPrefab clicked, IEnumerable<BridgeKitDef> kits, IReadOnlyList<PlacedPrefab> prefabs = null)
    {
        if (clicked?.Def == null) return null;
        prefabs ??= w.PrefabSnapshot();
        prefabs = prefabs.Where(p => p.Def != null).ToList();
        foreach (var kit in kits.Where(k => Is(k.End, clicked.Name) || Is(k.Tile, clicked.Name)))
        {
            bool isEnd = Is(kit.End, clicked.Name);
            int turn = isEnd ? kit.Turn : kit.TileTurn;
            bool alongX = (((clicked.Rotation - turn) % 4 + 4) % 4) % 2 == 0;
            int across = alongX ? clicked.Z : clicked.X;
            int S(PlacedPrefab p) => alongX ? p.X : p.Z;
            int L(PlacedPrefab p) => alongX ? p.SizeX : p.SizeZ;
            var row = prefabs.Where(p => p.Y == clicked.Y && (alongX ? p.Z : p.X) == across && (Is(kit.End, p.Name) || (!kit.Single && Is(kit.Tile, p.Name)))).ToList();
            var chain = new List<PlacedPrefab> { clicked };
            // along the row as long as the next piece joins exactly
            for (var cur = clicked; ;)
            {
                var prev = row.FirstOrDefault(p => !chain.Contains(p) && S(p) + L(p) == S(cur));
                if (prev == null || (Is(kit.End, cur.Name) && cur != clicked)) break;
                chain.Insert(0, prev); cur = prev;
                if (Is(kit.End, prev.Name)) break;
            }
            for (var cur = clicked; ;)
            {
                var next = row.FirstOrDefault(p => !chain.Contains(p) && S(p) == S(cur) + L(cur));
                if (next == null || (Is(kit.End, cur.Name) && cur != clicked)) break;
                chain.Add(next); cur = next;
                if (Is(kit.End, next.Name)) break;
            }
            // a clicked end piece took pieces on both sides: keep the side with the middle pieces
            if (!kit.Single && isEnd && chain.Count > 1)
            {
                int k = chain.IndexOf(clicked);
                if (k > 0 && k < chain.Count - 1)
                    chain = Is(kit.Tile, chain[k + 1].Name) ? chain.Skip(k).ToList() : chain.Take(k + 1).ToList();
            }
            if (kit.Single ? chain.Count != 1 || !Is(kit.End, chain[0].Name)
                : chain.Count < 3 || !Is(kit.End, chain[0].Name) || !Is(kit.End, chain[^1].Name) || chain.Skip(1).Take(chain.Count - 2).Any(p => !Is(kit.Tile, p.Name)))
                continue;
            var site = new BridgeSite();
            kit.ApplyTo(site);
            site.AlongX = alongX;
            site.S0 = S(chain[0]);
            site.T0 = across;
            site.Tiles = kit.Single ? 0 : chain.Count - 2;
            site.Deck = clicked.Y + site.DeckLayer;
            if (site.Length != S(chain[^1]) + L(chain[^1]) - site.S0) continue;
            site.Level = WaterLevelUnder(w, site);
            // surface of the approaches: a gravel bridge has gravel in the middle of its approaches, an asphalt bridge asphalt
            var (a, b, _) = NetworkBuilder.ApproachEnds(site);
            var u = Axis(site);
            byte ka = w.RoadAt((int)Math.Floor((a + u * 10).X), (int)Math.Floor((a + u * 10).Y));
            byte kb = w.RoadAt((int)Math.Floor((b - u * 10).X), (int)Math.Floor((b - u * 10).Y));
            site.Gravel = ka != WorldData.Asphalt && kb != WorldData.Asphalt && (ka == WorldData.Gravel || kb == WorldData.Gravel);
            site.RiverWidth = site.WaterWidth;
            return new BuiltBridge { Site = site, Kit = kit, Pieces = chain };
        }
        return null;
    }

    /// road cells that do not count as obstacles for the new bridge: the old bridge with its approaches and the road in front of them
    public static Func<int, int, bool> OldCorridor(BridgeSite old)
    {
        double cs = CenterS(old), mt = old.MidT, reach = old.Length / 2.0 + Bridge.Approach + JoinLength;
        return (x, z) =>
        {
            var p = new Point(x + 0.5, z + 0.5);
            return Math.Abs(AlongOf(old.AlongX, p) - cs) <= reach && Math.Abs(AcrossOf(old.AlongX, p) - mt) <= 22;
        };
    }

    /// Traced existing road on one side of a built bridge (from the old approach end outwards)
    sealed class TracedSide
    {
        public List<Point> Line;        // from old approach end outwards, every 2 m
        public double[] LocalHalf;      // clean local total surface half-width per point in Line (without junction mouth spikes)
        public double TotalWidth;       // nominal total surface width of the road beyond any previous approach taper
        public double Width;            // roadway width (asphalt without its 1 m gravel edges; gravel = full width)
        public bool Gravel;
        public byte Kind => Gravel ? WorldData.Gravel : WorldData.Asphalt;
    }

    static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    /// Traces the existing road outward from `oldEnd` without modifying anything. Returns null if no road continues past the approach.
    static TracedSide TraceSide(WorldData w, RoadScan scan, Point oldEnd, Vector outward, BridgeSite old)
    {
        var p3 = oldEnd + outward * 3;
        var p5 = oldEnd + outward * 5;
        if (w.RoadAt((int)Math.Floor(p3.X), (int)Math.Floor(p3.Y)) == 0
            && w.RoadAt((int)Math.Floor(p5.X), (int)Math.Floor(p5.Y)) == 0)
            return null;

        var f = RoadFollower.Extend(scan, oldEnd, outward, MaxTrace, old.Width, false, adaptWidth: true);
        var pts = f.Points.ToList();
        if (f.Stop == "tile") pts = pts.Take(Math.Max(0, pts.Count - 4)).ToList();
        byte kind = old.Gravel ? WorldData.Gravel : WorldData.Asphalt;
        int keepTo = pts.Count;
        var rawRuns = new List<double>();
        for (int i = 0; i < pts.Count; i++)
        {
            double d = 2.0 * (i + 1);
            var c = pts[i];
            byte k = w.RoadAt((int)Math.Floor(c.X), (int)Math.Floor(c.Y));
            if (k == 0 && d > 4) { keepTo = i; break; }
            if (k != kind && d > 4) { keepTo = Math.Max(0, i - 3); break; }
            var along = pts[Math.Min(pts.Count - 1, i + 3)] - (i >= 3 ? pts[i - 3] : oldEnd);
            if (along.Length < 1e-6) along = outward;
            along.Normalize();
            var n = new Vector(-along.Y, along.X);
            var (lo, hi) = RoadFollower.Run(scan, c, n, 22);
            rawRuns.Add(hi - lo + 1);
        }
        pts = pts.Take(keepTo).ToList();
        rawRuns = rawRuns.Take(keepTo).ToList();
        if (pts.Count < 3) return null;

        // Filter out localized junction mouth widenings without clipping monotonic approach tapers (e.g. 10 m → 18 m):
        // a mouth spike rises sharply above BOTH the road before it and the road after it (or above the normal road width).
        int m = rawRuns.Count;
        var sortedAll = rawRuns.OrderBy(x => x).ToList();
        var farRaw = new List<double>();
        for (int i = 0; i < m; i++) if (2.0 * (i + 1) > TaperMeasure) farRaw.Add(rawRuns[i]);
        double normalWidth = farRaw.Count >= 5 ? Median(farRaw) : sortedAll[sortedAll.Count / 2];
        double normalCap = Math.Max(old.RoadWidth, normalWidth) + 3.0;
        var isSpike = new bool[m];
        for (int i = 0; i < m; i++)
        {
            double minBefore = double.MaxValue, minAfter = double.MaxValue;
            for (int j = Math.Max(0, i - 10); j < i; j++) minBefore = Math.Min(minBefore, rawRuns[j]);
            for (int j = i + 1; j <= Math.Min(m - 1, i + 10); j++) minAfter = Math.Min(minAfter, rawRuns[j]);
            double surround = minBefore < double.MaxValue && minAfter < double.MaxValue
                ? Math.Max(minBefore, minAfter)
                : Math.Min(minBefore, minAfter);
            isSpike[i] = rawRuns[i] > normalCap || (surround < double.MaxValue && rawRuns[i] > surround * 1.35 + 3.0);
        }
        var clean = new double[m];
        for (int i = 0; i < m; i++)
        {
            if (!isSpike[i]) { clean[i] = rawRuns[i]; continue; }
            int prev = i - 1; while (prev >= 0 && isSpike[prev]) prev--;
            int next = i + 1; while (next < m && isSpike[next]) next++;
            if (prev >= 0 && next < m) clean[i] = rawRuns[prev] + (rawRuns[next] - rawRuns[prev]) * ((i - prev) / (double)(next - prev));
            else if (prev >= 0) clean[i] = rawRuns[prev];
            else if (next < m) clean[i] = rawRuns[next];
            else clean[i] = Math.Min(rawRuns[i], normalCap);
        }
        var localHalf = new double[m];
        for (int i = 0; i < m; i++)
        {
            double s = 0; int cnt = 0;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(m - 1, i + 2); j++) { s += clean[j]; cnt++; }
            localHalf[i] = Math.Clamp(s / (2.0 * cnt), 1.5, 16.0);
        }

        // Measure nominal road width beyond any previous bridge approach taper
        var farRuns = new List<double>();
        for (int i = 0; i < m; i++)
            if (2.0 * (i + 1) > TaperMeasure && !isSpike[i]) farRuns.Add(clean[i]);
        double total = farRuns.Count > 0
            ? Median(farRuns)
            : clean.OrderBy(x => x).ElementAt(Math.Min(m - 1, m / 4));
        bool gravel = old.Gravel;
        double width = gravel
            ? Math.Clamp(Math.Round(total + 0.5, MidpointRounding.AwayFromZero), 3, 12)
            : Math.Clamp(Math.Round(total - 1.5, MidpointRounding.AwayFromZero), 4, 24);
        return new TracedSide { Line = pts, LocalHalf = localHalf, TotalWidth = total, Width = width, Gravel = gravel };
    }

    /// Builds the control polyline and per-point surface half-widths for RoadReprofile.PlanBridgeRamp,
    /// starting 4 m inside the new approach (so RoadReprofile.Apply's endpoint cut never leaves a seam at nuEnd)
    /// and continuing smoothly onto the traced existing road.
    static (List<Point> Pts, double[] Half, double StartDist, double Shift) BuildRampLine(
        TracedSide side, Point oldEnd, Point nuEnd, Vector outward, BridgeSite old, BridgeSite nu)
    {
        const double leadIn = 4.0;
        double shift = (nuEnd - oldEnd) * outward;   // > 0: nu is longer (nuEnd further out); < 0: nu is shorter (nuEnd closer to river)
        var pts = new List<Point>();
        var half = new List<double>();
        double nuHalf = nu.RoadWidth / 2.0;
        double oldHalf = old.RoadWidth / 2.0;

        pts.Add(nuEnd - outward * leadIn);
        half.Add(Math.Max(nuHalf, oldHalf));
        pts.Add(nuEnd);
        half.Add(Math.Max(nuHalf, shift <= 0 ? oldHalf : SampleHalfAt(side, shift)));

        if (shift < -1.0)
        {
            // nu is shorter than old: add straight 2 m steps across the gap [nuEnd .. oldEnd]
            double gap = -shift;
            for (double d = 2.0; d < gap - 0.5; d += 2.0)
            {
                pts.Add(nuEnd + outward * d);
                half.Add(oldHalf);
            }
            pts.Add(oldEnd);
            half.Add(oldHalf);
            for (int i = 0; i < side.Line.Count; i++)
            {
                if ((side.Line[i] - pts[^1]).Length < 0.8) continue;
                pts.Add(side.Line[i]);
                half.Add(side.LocalHalf[i]);
            }
        }
        else
        {
            // nu is same length or longer than old: skip points of side.Line that lie behind nuEnd
            double skip = Math.Max(0, shift) + 1.0;
            for (int i = 0; i < side.Line.Count; i++)
            {
                double along = (side.Line[i] - oldEnd) * outward;
                if (along < skip && i < side.Line.Count - 2) continue;
                if ((side.Line[i] - pts[^1]).Length < 0.8) continue;
                pts.Add(side.Line[i]);
                half.Add(side.LocalHalf[i]);
            }
        }
        return (pts, half.ToArray(), leadIn, shift);
    }

    static double SampleHalfAt(TracedSide side, double dist)
    {
        if (side.LocalHalf.Length == 0) return 4;
        int idx = Math.Clamp((int)Math.Round(dist / 2.0) - 1, 0, side.LocalHalf.Length - 1);
        return side.LocalHalf[idx];
    }

    /// Clears road surface ONLY (ZERO terrain height edits!) inside the old bridge's approach/span area that is covered
    /// by the new bridge+approaches or by overhanging width/length of the old approach. Protected branch cells are never touched.
    static void ClearOldApproachSurface(WorldData w, WorldEdits e, BridgeSite old, BridgeSite nu, bool hasRoadA, bool hasRoadB, HashSet<(int, int)> branchCells)
    {
        int oldS0 = old.S0 - Bridge.Approach, oldS1 = old.S0 + old.Length + Bridge.Approach - 1;
        int nuS0 = nu.S0 - Bridge.Approach, nuS1 = nu.S0 + nu.Length + Bridge.Approach - 1;
        // Always clear inside nu's exact bridge+approach span (Bridge.Build will freshly paint nu's exact approaches),
        // plus inside old's bridge piece span (s ∈ [old.S0 .. old.S0 + old.Length - 1]), plus any orphan approach if a side has no road.
        int clearS0 = hasRoadA ? Math.Min(nuS0, old.S0) : Math.Min(nuS0, oldS0);
        int clearS1 = hasRoadB ? Math.Max(nuS1, old.S0 + old.Length - 1) : Math.Max(nuS1, oldS1);
        int t0 = Math.Min(old.T0, nu.T0) - 2;
        int t1 = Math.Max(old.T0 + old.Width - 1, nu.T0 + nu.Width - 1) + 2;
        for (int s = clearS0; s <= clearS1; s++)
            for (int t = t0; t <= t1; t++)
            {
                int x = nu.AlongX ? s : t, z = nu.AlongX ? t : s;
                if (!w.Contains(x, z)) continue;
                int i = w.Index(x, z);
                if (w.RoadMask[i] == 0) continue;
                if (branchCells.Contains((x, z))) continue;
                e.SetSplat(x, z, WorldEdits.EmptyPixel);
                e.SetRoad(i, 0);
            }
    }

    /// Collects all cells covered by detected side branches (from j.Center outward) so neither approach surface cleanup
    /// nor narrow-bridge tapering can ever slice a gap into a branch mouth.
    static HashSet<(int, int)> CollectBranchCells(ReprofilePlan plan)
    {
        var cells = new HashSet<(int, int)>();
        if (plan?.Junctions == null) return cells;
        foreach (var j in plan.Junctions)
            foreach (var arm in j.Arms)
            {
                double rad = arm.Width / 2.0 + 0.6;
                int ir = (int)Math.Ceiling(rad);
                double walked = 0;
                double maxWalk = arm.Start + 28;
                for (int i = 1; i < arm.Points.Count && walked <= maxWalk; i++)
                {
                    var a = arm.Points[i - 1];
                    var v = arm.Points[i] - a;
                    double len = v.Length;
                    if (len < 1e-6) continue;
                    int steps = Math.Max(1, (int)Math.Ceiling(len / 0.5));
                    for (int k = 0; k <= steps; k++)
                    {
                        var p = a + v * (k / (double)steps);
                        int cx = (int)Math.Floor(p.X), cz = (int)Math.Floor(p.Y);
                        for (int dz = -ir; dz <= ir; dz++)
                            for (int dx = -ir; dx <= ir; dx++)
                            {
                                double ex = cx + dx + 0.5 - p.X, ez = cz + dz + 0.5 - p.Y;
                                if (ex * ex + ez * ez <= rad * rad) cells.Add((cx + dx, cz + dz));
                            }
                    }
                    walked += len;
                }
            }
        return cells;
    }

    /// Short surface-only width taper right behind one approach end of the new bridge:
    /// - Smoothly transitions from `nu.RoadWidth` at `nuEnd` to the existing road's measured width over ~18–56 m.
    /// - Trimming (narrower bridge): clears only outer excess road pixels with ZERO terrain height changes, never cutting into branch mouths.
    /// - Widening / gap fill (wider or shorter bridge): paints gravel or asphalt+1m-gravel edge and gently levels ONLY newly paved
    ///   (previously unpaved) cells + an 8 m unpaved shoulder blend without ever altering existing road centerline heights.
    static void AdjustApproachSurface(WorldData w, WorldEdits e, RoadScan scan, BridgeSite old, BridgeSite nu,
        TracedSide side, ReprofilePlan plan, Point nuEnd, Vector outward, double shift, HashSet<(int, int)> branchCells)
    {
        if (plan?.Path == null || plan.Path.Count < 4) return;
        var path = plan.Path;
        double startDist = plan.RunIn;
        double avail = path[^1].Dist - startDist;
        if (avail < 4) return;

        double nuHalf = nu.RoadWidth / 2.0;
        double gapLen = Math.Max(0, -shift);
        double widthDiff = Math.Max(Math.Abs(nu.RoadWidth - side.TotalWidth), Math.Abs(nu.RoadWidth - old.RoadWidth));
        // If same width and no gap, only touch a minimal 6 m collar right at nuEnd to ensure 0 gap
        double taperLen = (gapLen < 0.5 && widthDiff < 0.6)
            ? Math.Min(6.0, avail)
            : Math.Clamp(gapLen + widthDiff * 4.0 + 16.0, 18.0, Math.Min(56.0, avail - 2.0));
        if (taperLen < 2) return;

        // Collect waypoints covering [startDist .. startDist + taperLen]
        int i0 = plan.IndexAt(startDist);
        int i1 = plan.IndexAt(Math.Min(path[^1].Dist, startDist + taperLen));
        if (i1 <= i0) return;

        // Measure post-ramp road centerline height & banking at every waypoint in [i0 .. i1], smoothed to avoid integer-grid jitter
        int wn = i1 - i0 + 1;
        var rawH = new double[wn];
        var rawCross = new double[wn];
        var existHalfAt = new double[wn];
        for (int k = 0; k < wn; k++)
        {
            int pi = i0 + k;
            var q = path[pi];
            rawH[k] = w.HeightAt((int)Math.Floor(q.X), (int)Math.Floor(q.Z));
            existHalfAt[k] = plan.EdgeAt != null ? Math.Max(1.5, plan.EdgeAt[pi] - 0.5) : side.TotalWidth / 2.0;
            var (nx, nz) = RoadPlanner.Normal(path, pi);
            double sample = Math.Max(1.5, Math.Min(existHalfAt[k] - 1.0, nuHalf - 1.0));
            double hl = w.HeightAt((int)Math.Floor(q.X + nx * sample), (int)Math.Floor(q.Z + nz * sample));
            double hr = w.HeightAt((int)Math.Floor(q.X - nx * sample), (int)Math.Floor(q.Z - nz * sample));
            rawCross[k] = Math.Clamp((hl - hr) / (2.0 * sample), -0.12, 0.12);
        }
        var centerH = new double[wn];
        var crossAt = new double[wn];
        for (int k = 0; k < wn; k++)
        {
            double sh = 0, sc = 0; int cnt = 0;
            for (int j = Math.Max(0, k - 8); j <= Math.Min(wn - 1, k + 8); j++) { sh += rawH[j]; sc += rawCross[j]; cnt++; }
            centerH[k] = sh / cnt;
            crossAt[k] = sc / cnt;
        }

        // Bounding box around the taper waypoints
        double maxHalf = Math.Max(nuHalf, existHalfAt.Max()) + 4.0;
        double bx0 = double.MaxValue, bx1 = double.MinValue, bz0 = double.MaxValue, bz1 = double.MinValue;
        for (int pi = i0; pi <= i1; pi++)
        {
            bx0 = Math.Min(bx0, path[pi].X); bx1 = Math.Max(bx1, path[pi].X);
            bz0 = Math.Min(bz0, path[pi].Z); bz1 = Math.Max(bz1, path[pi].Z);
        }
        int x0 = (int)Math.Floor(bx0 - maxHalf) - 2, x1 = (int)Math.Ceiling(bx1 + maxHalf) + 2;
        int z0 = (int)Math.Floor(bz0 - maxHalf) - 2, z1 = (int)Math.Ceiling(bz1 + maxHalf) + 2;

        // Exact box of nu's bridge + approaches (never overwrite what Bridge.Build just built)
        int nuEx0 = nu.S0 - Bridge.Approach, nuEx1 = nu.S0 + nu.Length + Bridge.Approach - 1;
        int nuEt0 = nu.T0, nuEt1 = nu.T0 + nu.Width - 1;
        bool InNewBridgeOrApproach(int x, int z)
        {
            int s = nu.AlongX ? x : z, t = nu.AlongX ? z : x;
            return s >= nuEx0 && s <= nuEx1 && t >= nuEt0 && t <= nuEt1;
        }

        var newlyPaved = new List<(int X, int Z, double H)>();
        var toPaint = new List<(int X, int Z, byte Kind)>();
        var toTrim = new List<(int X, int Z)>();
        var touchedSurface = new HashSet<(int, int)>();
        var endPt = new Point(path[i1].X, path[i1].Z);
        var endDir = new Vector(path[i1].X - path[Math.Max(i0, i1 - 4)].X, path[i1].Z - path[Math.Max(i0, i1 - 4)].Z);
        if (endDir.Length < 1e-6) endDir = outward; else endDir.Normalize();

        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                if (!w.Contains(x, z) || InNewBridgeOrApproach(x, z)) continue;
                var cell = new Point(x + 0.5, z + 0.5);
                // Stay strictly on the outward side of nuEnd (don't reach backward into nu's approach flank)
                if ((cell - nuEnd) * outward < 0) continue;
                // Stay strictly before the end plane of the taper
                if ((cell - endPt) * endDir > 0.5) continue;

                // Nearest waypoint in [i0 .. i1] (step by 2 = 0.5 m)
                int bestK = -1; double bestD2 = double.MaxValue;
                for (int pi = i0; pi <= i1; pi += 2)
                {
                    double dx = cell.X - path[pi].X, dz = cell.Y - path[pi].Z;
                    double d2 = dx * dx + dz * dz;
                    if (d2 < bestD2) { bestD2 = d2; bestK = pi; }
                }
                if (bestK < 0) continue;
                // Refine to exact 0.25 m waypoint
                for (int pi = Math.Max(i0, bestK - 2); pi <= Math.Min(i1, bestK + 2); pi++)
                {
                    double dx = cell.X - path[pi].X, dz = cell.Y - path[pi].Z;
                    double d2 = dx * dx + dz * dz;
                    if (d2 < bestD2) { bestD2 = d2; bestK = pi; }
                }
                double dist = Math.Sqrt(bestD2);
                if (dist > maxHalf) continue;

                int lk = bestK - i0;
                double d = Math.Max(0, path[bestK].Dist - startDist);
                double uT = Smooth(d / taperLen);
                double exH = existHalfAt[lk];
                double targetHalf = nuHalf + (exH - nuHalf) * uT;
                double paintHalf = targetHalf;
                int idx = w.Index(x, z);
                byte cur = w.RoadMask[idx];
                bool inBranch = branchCells.Contains((x, z));

                if (dist <= paintHalf)
                {
                    if (cur == 0)
                    {
                        // Newly paved cell (widening or gap fill): record for painting and gentle leveling
                        if (w.WaterMask[idx] != 0) continue;   // never pave onto river water
                        bool isEdge = nu.Gravel || dist > paintHalf - 1.0;
                        toPaint.Add((x, z, isEdge ? WorldData.Gravel : WorldData.Asphalt));
                        var (nx, nz) = RoadPlanner.Normal(path, bestK);
                        double lat = (cell.X - path[bestK].X) * nx + (cell.Y - path[bestK].Z) * nz;
                        double targetH = centerH[lk] + crossAt[lk] * Math.Clamp(lat, -paintHalf, paintHalf);
                        newlyPaved.Add((x, z, targetH));
                        touchedSurface.Add((x, z));
                    }
                    else if (!nu.Gravel && !inBranch)
                    {
                        if (dist <= paintHalf - 1.0 && cur == WorldData.Gravel)
                        {
                            // Previously gravel shoulder is now inside widened asphalt core
                            toPaint.Add((x, z, WorldData.Asphalt));
                            touchedSurface.Add((x, z));
                        }
                        else if (dist > paintHalf - 1.0 && cur == WorldData.Asphalt && targetHalf < exH - 0.15)
                        {
                            // Narrowed asphalt road: convert new outer 1 m edge to gravel shoulder
                            toPaint.Add((x, z, WorldData.Gravel));
                            touchedSurface.Add((x, z));
                        }
                    }
                }
                else if (cur != 0 && !inBranch && !scan.InTile(cell.X, cell.Y)
                    && (targetHalf < exH - 0.15 || d <= Math.Max(4.0, gapLen + 2.0))
                    && dist <= Math.Max(exH, old.RoadWidth / 2.0) + 4.5)
                {
                    // Narrower bridge: trim outer excess road pixels (ZERO height edits!)
                    // Verify continuous road from centerline on the intact pre-edit RoadMask so a separate parallel road across grass is never erased.
                    bool connected = true;
                    double stepCheck = Math.Max(1, dist);
                    for (double f = 1.0; f < dist - 0.5; f += 1.0)
                    {
                        double rx = path[bestK].X + (cell.X - path[bestK].X) * (f / stepCheck);
                        double rz = path[bestK].Z + (cell.Y - path[bestK].Z) * (f / stepCheck);
                        if (!scan.Road(rx, rz)) { connected = false; break; }
                    }
                    if (connected)
                    {
                        toTrim.Add((x, z));
                        touchedSurface.Add((x, z));
                    }
                }
            }

        // Apply deferred surface paints and trims after the read pass so live RoadMask mutations never strand outer edge pixels.
        foreach (var (px, pz, pk) in toPaint)
        {
            e.SetSplat(px, pz, pk == WorldData.Gravel ? WorldEdits.GravelPixel : WorldEdits.AsphaltPixel);
            e.SetRoad(w.Index(px, pz), pk);
        }
        foreach (var (tx, tz) in toTrim)
        {
            e.SetSplat(tx, tz, WorldEdits.EmptyPixel);
            e.SetRoad(w.Index(tx, tz), 0);
        }

        // Clean 1 px gravel shoulder guarantee for asphalt roads inside the taper area:
        // no asphalt cell touched (or bordering a trimmed cell) may directly touch bare ground.
        if (!nu.Gravel && touchedSurface.Count > 0)
        {
            var toGravel = new List<(int X, int Z)>();
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    if (!w.Contains(x, z) || InNewBridgeOrApproach(x, z)) continue;
                    if (w.RoadAt(x, z) != WorldData.Asphalt) continue;
                    bool nearTouch = touchedSurface.Contains((x, z))
                        || touchedSurface.Contains((x + 1, z)) || touchedSurface.Contains((x - 1, z))
                        || touchedSurface.Contains((x, z + 1)) || touchedSurface.Contains((x, z - 1));
                    if (!nearTouch) continue;
                    if (w.RoadAt(x + 1, z) == 0 || w.RoadAt(x - 1, z) == 0 || w.RoadAt(x, z + 1) == 0 || w.RoadAt(x, z - 1) == 0)
                        toGravel.Add((x, z));
                }
            foreach (var (gx, gz) in toGravel)
            {
                e.SetSplat(gx, gz, WorldEdits.GravelPixel);
                e.SetRoad(w.Index(gx, gz), WorldData.Gravel);
            }
        }

        // Gently level ONLY newly paved cells (+ short 8 m unpaved shoulder blend), NEVER altering existing road heights.
        if (newlyPaved.Count > 0)
        {
            var prefabs = w.PrefabSnapshot();
            bool InPrefab(int x, int z)
            {
                for (int k = 0; k < prefabs.Length; k++)
                {
                    var pf = prefabs[k];
                    if (pf.Def == null) continue;
                    if (x >= pf.X - 1 && x <= pf.X + pf.SizeX && z >= pf.Z - 1 && z <= pf.Z + pf.SizeZ) return true;
                }
                return false;
            }
            var newSet = new HashSet<(int, int)>();
            int nx0 = int.MaxValue, nx1 = int.MinValue, nz0 = int.MaxValue, nz1 = int.MinValue;
            foreach (var (px, pz, ph) in newlyPaved)
            {
                newSet.Add((px, pz));
                nx0 = Math.Min(nx0, px); nx1 = Math.Max(nx1, px);
                nz0 = Math.Min(nz0, pz); nz1 = Math.Max(nz1, pz);
                if (!InPrefab(px, pz))
                    e.SetHeight(w.Index(px, pz), (ushort)Math.Clamp(Math.Round(ph * 256), 0, 65535));
            }
            const int shoulderR = 8;
            for (int z = nz0 - shoulderR; z <= nz1 + shoulderR; z++)
                for (int x = nx0 - shoulderR; x <= nx1 + shoulderR; x++)
                {
                    if (!w.Contains(x, z) || newSet.Contains((x, z))) continue;
                    int idx = w.Index(x, z);
                    if (w.RoadMask[idx] != 0 || w.WaterMask[idx] != 0 || InPrefab(x, z)) continue;
                    double bestD2 = double.MaxValue, bestH = 0;
                    foreach (var (px, pz, ph) in newlyPaved)
                    {
                        double dx = x - px, dz = z - pz;
                        double d2 = dx * dx + dz * dz;
                        if (d2 < bestD2) { bestD2 = d2; bestH = ph; }
                    }
                    double d = Math.Sqrt(bestD2);
                    if (d >= shoulderR) continue;
                    double weight = 1.0 - Smooth(d / shoulderR);
                    double curH = w.Heights[idx] / 256.0;
                    double nextH = curH + (bestH - curH) * weight;
                    var nv = (ushort)Math.Clamp(Math.Round(nextH * 256), 0, 65535);
                    if (nv != w.Heights[idx]) e.SetHeight(idx, nv);
                }
        }
    }

    /// Converts RoadReprofile's detected junctions/arms inside the active ramp or taper zone into NetRoad entries
    /// for BridgeSwapResult.BranchRoads (outer end → junction center, matching the existing test/inspection contract).
    static void AddBranchSummaries(BridgeSwapResult res, ReprofilePlan plan, double activeDist)
    {
        if (plan?.Junctions == null) return;
        foreach (var j in plan.Junctions)
        {
            if (j.Dist > activeDist && !j.Followed) continue;
            foreach (var arm in j.Arms)
            {
                if (arm.Points.Count < 2) continue;
                double reach = j.Followed ? Math.Min(arm.Length, arm.Start + arm.Fade) : Math.Min(arm.Length, Math.Max(arm.Start + 24, 40));
                var clipped = new List<Point> { arm.Points[0] };
                double walked = 0;
                for (int i = 1; i < arm.Points.Count; i++)
                {
                    var a = arm.Points[i - 1];
                    var v = arm.Points[i] - a;
                    double l = v.Length;
                    if (walked + l >= reach)
                    {
                        if (l > 1e-6) clipped.Add(a + v * ((reach - walked) / l));
                        break;
                    }
                    clipped.Add(arm.Points[i]);
                    walked += l;
                }
                if (clipped.Count < 2) continue;
                // Reverse so Points[0] is outer end and Points[^1] is junction center (same order as NetRoad branches),
                // preserving the mouth entry points clipped[2], clipped[1], clipped[0] so angled branches never chord across grass.
                var pts = new List<Point>();
                for (int i = clipped.Count - 1; i >= 2; i -= 4) pts.Add(clipped[i]);
                for (int i = Math.Min(2, clipped.Count - 1); i >= 0; i--)
                    if (pts.Count == 0 || (pts[^1] - clipped[i]).Length > 0.5) pts.Add(clipped[i]);
                bool gravel = arm.Kind == WorldData.Gravel;
                double bw = gravel
                    ? Math.Clamp(Math.Round(arm.Width - 0.25, MidpointRounding.AwayFromZero), 3, 12)
                    : Math.Clamp(Math.Round(arm.Width - 1.5, MidpointRounding.AwayFromZero), 4, 16);
                res.BranchRoads.Add(new NetRoad
                {
                    Points = pts,
                    Gravel = gravel,
                    Width = bw,
                    Manual = true,
                    Junction = j.Center,
                    Samples = RoadNetwork.Resample(pts, 16),
                    Length = LineLength(pts),
                    OnOldRoad = true,
                });
            }
        }
    }

    /// Swap a built bridge (inside an edit step): keeps the existing road intact, preserves the deck height when the new kit allows it,
    /// smoothly fades any height difference via RoadReprofile (dragging side branches along without touching their surface), builds the new
    /// bridge with its ground and approaches, and applies only a short width taper right behind each approach.
    public static BridgeSwapResult Build(WorldData w, WorldEdits e, BuiltBridge old, BridgeKitDef kit, NetworkSpec spec, IProgress<string> progress)
    {
        var prefabs = w.PrefabSnapshot();
        // the pieces may be other objects after undo/redo: find them again by name and place
        var pieces = old.Pieces.Select(p => prefabs.FirstOrDefault(q => q.Name == p.Name && q.X == p.X && q.Y == p.Y && q.Z == p.Z && q.Rotation == p.Rotation)).ToList();
        if (pieces.Any(p => p == null)) return null;
        var others = prefabs.Where(p => !pieces.Contains(p)).ToList();
        var nu = Refit(w, old.Site, kit, others, OldCorridor(old.Site));
        if (nu.Problem != null) return null;

        var scan = new RoadScan(w);
        var (oldA, oldB, oldH) = NetworkBuilder.ApproachEnds(old.Site);
        var (nuA, nuB, nuH) = NetworkBuilder.ApproachEnds(nu);
        var u = Axis(old.Site);
        double maxGrade = Math.Max(spec.MaxGrade, 0.9 * spec.ShortMaxGrade);

        var sideA = TraceSide(w, scan, oldA, -u, old.Site);
        var sideB = TraceSide(w, scan, oldB, u, old.Site);
        var res = new BridgeSwapResult { Site = nu };

        progress?.Report(Loc.T("Removing the old bridge …"));
        foreach (var p in pieces) e.RemovePrefab(p);

        // Plan relative height ramps on each side BEFORE Bridge.Build while the intact road & approach heights are untouched.
        ReprofilePlan planA = null, planB = null;
        double shiftA = (nuA - oldA) * (-u), shiftB = (nuB - oldB) * u;
        var branchCells = new HashSet<(int, int)>();

        if (sideA != null)
        {
            var (pts, half, startDist, sh) = BuildRampLine(sideA, oldA, nuA, -u, old.Site, nu);
            double? overrideH = sh <= 0.5 ? oldH : null;
            planA = RoadReprofile.PlanBridgeRamp(w, scan, pts, startDist, nuH, Math.Max(2, (sideA.TotalWidth - 2) / 2), half, maxGrade, sideA.Kind, overrideH);
            foreach (var cell in CollectBranchCells(planA)) branchCells.Add(cell);
        }
        if (sideB != null)
        {
            var (pts, half, startDist, sh) = BuildRampLine(sideB, oldB, nuB, u, old.Site, nu);
            double? overrideH = sh <= 0.5 ? oldH : null;
            planB = RoadReprofile.PlanBridgeRamp(w, scan, pts, startDist, nuH, Math.Max(2, (sideB.TotalWidth - 2) / 2), half, maxGrade, sideB.Kind, overrideH);
            foreach (var cell in CollectBranchCells(planB)) branchCells.Add(cell);
        }

        // If the new bridge is > 20 m shorter per side than the old one, [nuEnd .. oldEnd] may overlap old's bridge end piece
        // (TerrainDrop dip or river water): pre-level that strip to oldH before RoadReprofile applies Delta so no dip remains.
        foreach (var (side, nuEnd, oldEnd, outward, sh) in new[] { (sideA, nuA, oldA, -u, shiftA), (sideB, nuB, oldB, u, shiftB) })
        {
            if (side == null || sh >= -1.0) continue;
            double gap = -sh;
            int halfW = (int)Math.Ceiling(Math.Max(old.Site.Width, nu.Width) / 2.0) + 1;
            var n = new Vector(-outward.Y, outward.X);
            for (double d = 0; d <= gap; d += 0.5)
                for (int lat = -halfW; lat <= halfW; lat++)
                {
                    var p = nuEnd + outward * d + n * lat;
                    int x = (int)Math.Floor(p.X), z = (int)Math.Floor(p.Y);
                    if (!w.Contains(x, z)) continue;
                    int s = old.Site.AlongX ? x : z;
                    if (s < old.Site.S0 || s >= old.Site.S0 + old.Site.Length) continue;
                    int idx = w.Index(x, z);
                    e.SetHeight(idx, (ushort)Math.Clamp(Math.Round(oldH * 256), 0, 65535));
                    if (w.WaterMask[idx] != 0) e.SetWater(idx, 0);
                }
        }

        // Apply pure relative height transitions (0 height edits when nuH == oldH and level!)
        var quiet = new Progress<string>(_ => { });
        if (planA != null && planA.Changes)
        {
            progress?.Report(Loc.T("Changing the heights …"));
            RoadReprofile.Apply(w, e, planA, quiet);
            res.Warnings.AddRange(planA.Warnings);
        }
        if (planB != null && planB.Changes)
        {
            progress?.Report(Loc.T("Changing the heights …"));
            RoadReprofile.Apply(w, e, planB, quiet);
            res.Warnings.AddRange(planB.Warnings);
        }

        // Clear old approach surface ONLY (no terrain height deletion) so Bridge.Build paints nu's exact approaches cleanly.
        ClearOldApproachSurface(w, e, old.Site, nu, sideA != null, sideB != null, branchCells);

        // Build the new bridge, riverbed, banks, and approaches (existing roads outside nu's exact approach box are protected by RoadMask != 0).
        progress?.Report(Loc.T("Building the bridge …"));
        bool keepBanks = e.KeepBanksNow;
        e.KeepBanksNow = false;
        Bridge.Build(w, e, nu, kit.EndDef, kit.TileDef);
        e.KeepBanksNow = keepBanks;

        // Short surface width taper right behind each approach end
        if (sideA != null)
            AdjustApproachSurface(w, e, scan, old.Site, nu, sideA, planA, nuA, -u, shiftA, branchCells);
        if (sideB != null)
            AdjustApproachSurface(w, e, scan, old.Site, nu, sideB, planB, nuB, u, shiftB, branchCells);

        // Populate summary fields for UI and tests
        if (sideA != null || sideB != null)
        {
            var pts = new List<Point>();
            if (sideA != null)
            {
                int takeA = Math.Min(sideA.Line.Count, (int)(JoinLength / 2));
                for (int i = takeA - 1; i >= 0; i -= 4) pts.Add(sideA.Line[i]);
                pts.Add(nuA);
            }
            else pts.Add(nuA);
            pts.Add(nuB);
            if (sideB != null)
            {
                int takeB = Math.Min(sideB.Line.Count, (int)(JoinLength / 2));
                for (int i = 0; i < takeB; i += 4) pts.Add(sideB.Line[i]);
                if (takeB > 0 && (pts[^1] - sideB.Line[takeB - 1]).Length > 1) pts.Add(sideB.Line[takeB - 1]);
            }
            var first = sideA ?? sideB;
            var last = sideA != null ? sideB : null;
            bool gravel = last != null && first.Gravel != last.Gravel ? first.Width < last.Width ? last.Gravel : first.Gravel : first.Gravel;
            double width = last != null ? Math.Max(first.Width, last.Width) : first.Width;
            res.Road = new NetRoad
            {
                Points = pts,
                Gravel = gravel,
                Width = width,
                Manual = true,
                Bridges = new List<BridgeSite> { nu },
                Length = LineLength(pts),
            };
            res.Roads = (sideA != null ? 1 : 0) + (sideB != null ? 1 : 0);

            if (planA != null) AddBranchSummaries(res, planA, Math.Max(60, planA.RunIn + 56));
            if (planB != null) AddBranchSummaries(res, planB, Math.Max(60, planB.RunIn + 56));
        }

        return res;
    }
}
