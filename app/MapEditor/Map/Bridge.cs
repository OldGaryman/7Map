using System.Windows;

namespace MapEditor.Map;

/// a place for a bridge over a river: the bridge spans along x (AlongX) or along z; S = coordinate along the span, T = across
public sealed class BridgeSite
{
    public bool AlongX;          // true: the bridge runs west–east (the river flows roughly north–south)
    public int S0, T0;           // smallest corner of the bridge (S0 along the span, T0 across, both world coordinates)
    public int Tiles;            // number of middle pieces (10 m each)
    public int Level, Deck;      // water level; deck layer = first air block on the bank (level + 4)
    public double RiverWidth;    // measured width of the water at the click (m)
    public double RiverAngle;    // deviation of the river from the nearest compass direction (degrees)
    /// height of the road above the whole meter below the deck (m). Rule found in test 21 reloaded with the user's 14-m end piece (terrainFiller
    /// −128 in two layers below the deck; user: "perfect", 2026-11-19): road exactly deck − 1, under the terrain columns deck − 1.51 (TerrainDrop),
    /// so that the block above the end piece's terrain blocks is full air (FLUSS_ANALYSE.md)
    public double RoadLift = 0;
    /// height of the approach road (outside the bridge) above deck − 1 (m): 0 was a little too low (test 24), +0.18 fits (test 25, user 2026-11-19)
    public double ApproachLift = 0.18;
    /// length of the end piece along the span (m; the user's piece: 13 since 2026-11-19, before 12)
    public int EndLength = 13;
    /// outer columns of the end piece with their own terrain blocks one layer below the deck (13-m piece: local x 10–12)
    public int TerrainColumns = 3;
    /// under these columns the stored height lies so deep that the block above the terrain blocks is full air (h < deck − 1.5; otherwise
    /// the half-air block from dtm.raw stays above them and the terrain bulges up – user's photo 2026-11-19)
    public double TerrainDrop = 0.51;
    /// name of the end piece (test 22: _end_02 = copy with density −64 on the topmost terrain blocks)
    public string EndPiece = Bridge.EndName;
    /// the bridge belongs to a gravel road: the approaches (and the ground under the end pieces) are gravel over the whole width instead of
    /// 8 m asphalt with 1 m gravel edges (the user's request 2026-11-19); the pieces of the mod stay the same
    public bool Gravel;
    /// values of the bridge kit (BridgeKitDef, since 2026-11-24; the defaults are those of YAMEE_Bridges, so older plans keep working)
    public string Kit;           // id of the kit, null = YAMEE_Bridges
    public string TilePiece = Bridge.TileName;
    public bool Single;          // bridge of one piece (no middle pieces)
    public int TileLength = 10, Width = 10, LandUnder = 6, Clearance = 4, DeckLayer = 10, Turn, TileTurn;
    /// the road (asphalt with its gravel edges) is so much narrower than the piece on each side (BridgeKitDef.RoadInset)
    public int RoadInset;
    /// columns at each side of the piece without its own terrain on the land part (rebuilt game bridges: bridge_concrete_2 2 m): there the ground
    /// lies at the height of the approach instead of deck − 1.51 (BridgeKitDef.SideInset)
    public int SideInset;
    /// river bed under the bridge at least so many meters above the bottom of the piece (BridgeKitDef.BedAbove)
    public double BedAbove = 1.5;
    /// width of the road on the bridge and its approaches
    public int RoadWidth => Width - 2 * RoadInset;
    /// middle of the first bridge at this place (along, across) when it was swapped for another kit (BridgeSwap): every kit is centered on it,
    /// so swapping back gives exactly the old place; null = the middle of this bridge
    public double? AnchorS, AnchorT;
    public string Problem;       // null = possible
    [System.Text.Json.Serialization.JsonIgnore] public bool RiverProblem;   // the river itself does not fit (another kit does not help)
    [System.Text.Json.Serialization.JsonIgnore] public bool TooWide;
    /// the river is narrowed to the bridge here (Find with narrow): its width before (m), 0 = not narrowed. Build makes the transition
    /// up- and downstream longer and the new banks natural (saved with the plan)
    public double NarrowFrom;
    /// how much the river is narrowed on each side (m)
    public double NarrowPerSide => NarrowFrom > 0 ? Math.Max(0, (NarrowFrom - WaterWidth) / 2.0) : 0;
    public int Length => Single ? EndLength : TileLength * Tiles + 2 * EndLength;
    public int WaterWidth => Length - 2 * LandUnder;
    /// middle of the road across the span
    public double MidT => T0 + Width / 2.0;
    public Point Center => AlongX ? new Point(S0 + Length / 2.0, MidT) : new Point(MidT, S0 + Length / 2.0);
}

public sealed record BridgeStats(int Changed, int Water, int Dried, int Pieces, int Approach);

/// Bridge from the 10x10 kit of the mod YAMEE_Bridges (end piece 13 × 10 with own terrain blocks, edited by the user, middle pieces 10 × 10; FLUSS_ANALYSE.md
/// test 14, best variant: water up to under the pillars, steps −1/−2 m, bank at deck layer − 0.67 m = a full terrain block,
/// otherwise there is a gap between terrain and bridge). Prefabs only turn by 90°, so a bridge runs exactly west–east or
/// north–south; there the river is made straight and exactly as wide as the bridge needs (10 m per middle piece + 2 × 7 m under the end pieces).
public static class Bridge
{
    public const string EndName = "yamee_part_bridge_10x10_end_01", TileName = "yamee_part_bridge_10x10_tile_01";
    public const double MaxAngle = 25;     // river at most so far away from a compass direction (degrees)
    public const int Approach = 20;        // straight approach on deck height behind each end (m)
    const int Margin = 3;                  // level strip beside the bridge (m)
    const int Feather = 25;                // transition of the river banks upstream/downstream into the existing terrain (m)
    const int Reach = 60;                  // work area around the bridge: approaches and banks are joined to the terrain within it (m)
    const double BankSlope = 1.5;          // bank at the water: 1 m higher per 1.5 m away from the water (about 34°, the user 2026-11-25)

    /// the river at a point: direction, width, place and size of the bridge
    public static BridgeSite Find(WorldData w, double px, double pz, int endLength = 13, int terrainColumns = 3, double maxAngle = MaxAngle, IReadOnlyList<PlacedPrefab> prefabs = null) =>
        Find(w, px, pz, BridgeKitDef.Yamee(endLength, terrainColumns), maxAngle, prefabs);

    /// the river at a point for a bridge of this kit. roadIgnored(x, z) = road there does not count as an obstacle (swapping a built
    /// bridge: its own approaches and the road in front of them go away first, BridgeSwap)
    public static BridgeSite Find(WorldData w, double px, double pz, BridgeKitDef kit, double maxAngle = MaxAngle, IReadOnlyList<PlacedPrefab> prefabs = null,
        Func<int, int, bool> roadIgnored = null, bool narrow = false, int margin = 2)
    {
        var site = new BridgeSite { RiverProblem = true };
        kit.ApplyTo(site);
        int cx = (int)Math.Floor(px), cz = (int)Math.Floor(pz);
        if (!w.Contains(cx, cz)) { site.Problem = Loc.T("Click onto a river."); return site; }
        int start = -1;
        for (int r = 0; r <= 8 && start < 0; r++)
            for (int dz = -r; dz <= r && start < 0; dz++)
                for (int dx = -r; dx <= r && start < 0; dx++)
                    if (w.Contains(cx + dx, cz + dz) && w.WaterMask[w.Index(cx + dx, cz + dz)] != 0) start = w.Index(cx + dx, cz + dz);
        if (start < 0) { site.Problem = Loc.T("There is no water here. Click onto a river."); return site; }
        int level = w.WaterMask[start];
        site.Level = level;
        site.Deck = level + site.Clearance;
        // water cells of this level within 30 m: main direction (principal axis)
        const int R = 30;
        double sx = 0, sz = 0; int n = 0;
        var cells = new List<(int x, int z)>();
        for (int dz = -R; dz <= R; dz++)
            for (int dx = -R; dx <= R; dx++)
            {
                if (dx * dx + dz * dz > R * R) continue;
                int x = cx + dx, z = cz + dz;
                if (!w.Contains(x, z) || w.WaterMask[w.Index(x, z)] != level) continue;
                cells.Add((x, z)); sx += x; sz += z; n++;
            }
        double mx = sx / n, mz = sz / n, cxx = 0, czz = 0, cxz = 0;
        foreach (var (x, z) in cells) { cxx += (x - mx) * (x - mx); czz += (z - mz) * (z - mz); cxz += (x - mx) * (z - mz); }
        double tr = cxx + czz, det = cxx * czz - cxz * cxz, disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
        double l1 = tr / 2 + disc, l2 = Math.Max(1e-6, tr / 2 - disc);
        if (l1 / l2 < 2.2) { site.Problem = Loc.T("This does not look like a river here (rather a lake or a bend). Click onto a straight piece of a river."); return site; }
        double ang = 0.5 * Math.Atan2(2 * cxz, cxx - czz);   // direction of the river
        double deg = ang * 180 / Math.PI;
        double toX = Math.Abs(Math.IEEERemainder(deg, 180)), toZ = Math.Abs(Math.IEEERemainder(deg - 90, 180));
        // river along x → bridge along z
        site.AlongX = toZ < toX;
        site.RiverAngle = Math.Min(toX, toZ);
        if (site.RiverAngle > maxAngle)
        {
            site.Problem = Loc.F("The river runs at a slant here ({0:0}° away from a compass direction). Bridges can only run north–south or west–east; look for a place where the river flows roughly in a compass direction (at most {1:0}°).", site.RiverAngle, maxAngle);
            return site;
        }
        // across the river (along the span) through the click: water extent on several lines, widest one
        int t = site.AlongX ? cz : cx;
        double best = 0, centerS = 0;
        for (int dt = -5; dt <= 5; dt++)
        {
            int tt = t + dt;
            int s = site.AlongX ? cx : cz;
            bool Wet(int ss) { int x = site.AlongX ? ss : tt, z = site.AlongX ? tt : ss; return w.Contains(x, z) && w.WaterMask[w.Index(x, z)] == level; }
            int s0 = s, s1 = s;
            if (!Wet(s)) continue;
            while (s0 > s - 200 && Wet(s0 - 1)) s0--;
            while (s1 < s + 200 && Wet(s1 + 1)) s1++;
            double width = s1 - s0 + 1;
            if (width > best) { best = width; centerS = (s0 + s1 + 1) / 2.0; }
        }
        site.RiverWidth = best;
        if (best < 4) { site.Problem = Loc.T("Click onto a river."); return site; }
        site.RiverProblem = false;   // from here on it depends on the kit
        int ends = 2 * (site.EndLength - site.LandUnder);   // water under both end pieces
        if (site.Single)
        {
            site.Tiles = 0;
            if (best + margin > site.WaterWidth)
            {
                site.TooWide = true;
                if (!narrow)
                {
                    site.Problem = Loc.F("The river is {0:0} m wide here – too wide for this bridge (at most {1} m).", best, site.WaterWidth - margin);
                    return site;
                }
            }
        }
        else
        {
            site.Tiles = Math.Clamp((int)Math.Ceiling((best + margin - ends) / (double)site.TileLength), 1, kit.MaxTiles);
            if (best + margin > site.TileLength * kit.MaxTiles + ends)
            {
                site.TooWide = true;
                if (!narrow)
                {
                    site.Problem = Loc.F("The river is {0:0} m wide here – too wide for a bridge (at most {1} m).", best, site.TileLength * kit.MaxTiles + ends);
                    return site;
                }
            }
        }
        // narrowing the river at the bridge (swapping by hand, the user's request 2026-11-26): at most to half its width, the transition
        // in Build gets longer the more it narrows
        if (site.TooWide)
        {
            if (site.WaterWidth < best / 2)
            {
                site.Problem = Loc.F("The river is {0:0} m wide here – too wide for this bridge even when narrowed (at most to half the width, the bridge spans {1} m of water).", best, site.WaterWidth);
                return site;
            }
            site.NarrowFrom = best;
        }
        site.S0 = (int)Math.Round(centerS - site.Length / 2.0);
        site.T0 = (int)Math.Round(t + 0.5 - site.Width / 2.0);
        // free: no buildings, tiles or roads where the bridge, the approaches and the straightened river go
        int a0 = site.S0 - Approach - 2, a1 = site.S0 + site.Length + Approach + 1, b0 = site.T0 - Margin - 2, b1 = site.T0 + site.Width + Margin + 1;
        foreach (var p in prefabs ?? w.PrefabSnapshot())
        {
            int ps0 = site.AlongX ? p.X : p.Z, ps1 = ps0 + (site.AlongX ? p.SizeX : p.SizeZ);
            int pt0 = site.AlongX ? p.Z : p.X, pt1 = pt0 + (site.AlongX ? p.SizeZ : p.SizeX);
            if (ps1 > a0 && ps0 <= a1 && pt1 > b0 && pt0 <= b1)
            {
                site.Problem = BridgeKits.IsBridgePiece(p.Name)
                    ? Loc.T("There is already a bridge here.")
                    : Loc.F("A building is in the way ({0}).", string.IsNullOrEmpty(p.Def?.Label) ? p.Name : p.Def.Label);
                return site;
            }
        }
        for (int s = a0; s <= a1; s++)
            for (int tt = b0; tt <= b1; tt++)
            {
                int x = site.AlongX ? s : tt, z = site.AlongX ? tt : s;
                if (!w.Contains(x, z) || Math.Abs(x) > w.Half - 60 || Math.Abs(z) > w.Half - 60) { site.Problem = Loc.T("Too close to the edge of the map."); return site; }
                if (w.RoadMask[w.Index(x, z)] != 0 && roadIgnored?.Invoke(x, z) != true) { site.Problem = Loc.T("A road is already in the way here. Build the bridge first and then connect the roads to it."); return site; }
            }
        return site;
    }

    /// target height across the river (s = coordinate along the span): water with a bed, two steps, bank at deck − 0.67 m
    static (double h, bool water) Profile(BridgeSite b, int s)
    {
        int w0 = b.S0 + b.LandUnder, w1 = b.S0 + b.Length - 1 - b.LandUnder;   // water from LandUnder on under each end piece (10x10 kit: local x 6)
        double bank = b.Deck - 1 + b.ApproachLift;   // level area: bank beside the road at the same height as the approach road (test 20)
        if (s >= w0 && s <= w1)
        {
            int dep = Math.Min(s - w0, w1 - s);
            // at least 1.5 m above the bottom of the piece: the game always copies the air of layer 0 into the ground (below layer −YOffset, YOffset −1;
            // Prefab.CopyBlocksIntoChunkNoEntities) – with the bed in that block a 1 m gap opened at the bottom under wood1 (bed 53.5, piece at 53,
            // Brueckentest45). From 1.5 m on, layer 0 lies under a full block of terrain and stays hidden. The piers end at layer 0 and stand in the bed.
            // The 10x10, rwg and broken have their bottom 6 m below the water: no change there
            // (BedAbove per kit.) Pieces with YOffset 0 copy no air: the bed may go down to the lowest point of the piece, deeper than the
            // usual river depth of 4.5 m (the user 2026-11-25 after Brueckentest50)
            double floor = b.BedAbove <= 0 ? b.Deck - b.DeckLayer : Math.Max(b.Level - 4.5, b.Deck - b.DeckLayer + b.BedAbove);
            return (Math.Max(b.Level - 1 - dep, floor), true);
        }
        if (s == w0 - 1 || s == w1 + 1) return (bank - 2, false);
        if (s == w0 - 2 || s == w1 + 2) return (bank - 1, false);
        return (bank, false);
    }

    static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    const int BlendLen = 32;               // smooth riverbank transition upstream and downstream of the bridge (m)
    /// length of the transition up- and downstream: BlendLen, where the river is narrowed 10 m per meter of narrowing on each side
    /// (32–120 m; with 32 m, and still with 6 m per meter, a narrowed river looked like an hourglass – pictures 2026-11-26)
    internal static int BlendFor(BridgeSite b) => b.NarrowPerSide > 0 ? Math.Clamp((int)Math.Round(10 * b.NarrowPerSide), BlendLen, 120) : BlendLen;

    /// measure the river's water span [r0, r1] along s for rows t outside the bridge exact area [et0, et1], then compute
    /// smoothly blended target spans [tw0, tw1] that join [w0, w1] at the bridge to the river without rectangular corners
    static Dictionary<int, (double tw0, double tw1, double r0, double r1, double u)> RiverBlend(WorldData w, BridgeSite b, int et0, int et1, int w0, int w1)
    {
        int blendLen = BlendFor(b);
        double nps = b.NarrowPerSide;
        var res = new Dictionary<int, (double, double, double, double, double)>();
        int L = b.Level;
        int wWidth = w1 - w0;
        foreach (int dir in new[] { -1, 1 })
        {
            int tStart = dir < 0 ? et0 : et1;
            var raw = new List<(int t, double r0, double r1)>();
            double mid = 0.5 * (w0 + w1);
            for (int d = 1; d <= blendLen; d++)
            {
                int t = tStart + dir * d;
                // find the contiguous water run of level L nearest to `mid`
                int seed = -1, bestDist = int.MaxValue;
                int sMin = (int)Math.Floor(mid) - 35, sMax = (int)Math.Ceiling(mid) + 35;
                for (int s = sMin; s <= sMax; s++)
                {
                    int x = b.AlongX ? s : t, z = b.AlongX ? t : s;
                    if (!w.Contains(x, z) || w.WaterMask[w.Index(x, z)] != L) continue;
                    int dist = Math.Abs(s - (int)Math.Round(mid));
                    if (dist < bestDist) { bestDist = dist; seed = s; }
                }
                if (bestDist > 22) break;
                int lo = seed, hi = seed;
                while (lo > seed - 50)
                {
                    int x = b.AlongX ? lo - 1 : t, z = b.AlongX ? t : lo - 1;
                    if (!w.Contains(x, z) || w.WaterMask[w.Index(x, z)] != L) break;
                    lo--;
                }
                while (hi < seed + 50)
                {
                    int x = b.AlongX ? hi + 1 : t, z = b.AlongX ? t : hi + 1;
                    if (!w.Contains(x, z) || w.WaterMask[w.Index(x, z)] != L) break;
                    hi++;
                }
                int span = hi - lo;
                if (span < 4 || span > Math.Max(55, wWidth + 28)) break;   // widened into a lake or pinched off
                raw.Add((t, lo, hi));
                mid = 0.5 * (lo + hi);
            }
            if (raw.Count < 4) continue;
            // smooth the measured river edges slightly so single-pixel shore bumps don't ripple, keeping the far end exact
            int n = raw.Count;
            var sm0 = new double[n];
            var sm1 = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (i == n - 1) { sm0[i] = raw[i].r0; sm1[i] = raw[i].r1; continue; }
                double a = 0, c = 0; int cnt = 0;
                for (int j = Math.Max(0, i - 2); j <= Math.Min(n - 1, i + 2); j++) { a += raw[j].r0; c += raw[j].r1; cnt++; }
                sm0[i] = a / cnt; sm1[i] = c / cnt;
            }
            for (int i = 0; i < n; i++)
            {
                double u = Smooth((double)(i + 1) / n);
                double tw0 = w0 + (sm0[i] - w0) * u;
                double tw1 = w1 + (sm1[i] - w1) * u;
                if (nps > 0)
                {
                    // narrowed river: the new banks wave a little (not two straight wedges), most in the middle of the transition, none at the
                    // bridge and where the transition meets the river; each bank on its own
                    double amp = Math.Min(3.0, 0.8 + 0.2 * nps) * 4 * u * (1 - u);
                    int tt = raw[i].t, key = b.AlongX ? b.S0 : b.T0;
                    tw0 -= amp * (RoadApplier.Octave(tt, key, 23, 911) * 0.7 + RoadApplier.Octave(tt, key, 9, 913) * 0.3);
                    tw1 += amp * (RoadApplier.Octave(tt, key, 23, 917) * 0.7 + RoadApplier.Octave(tt, key, 9, 919) * 0.3);
                }
                if (tw1 - tw0 < 4) continue;
                res[raw[i].t] = (tw0, tw1, raw[i].r0, raw[i].r1, u);
            }
        }
        return res;
    }

    /// shape the ground, set the water, the bridge pieces and the approaches (one edit step)
    public static BridgeStats Build(WorldData w, WorldEdits e, BridgeSite b, PrefabDef endDef, PrefabDef tileDef)
    {
        e.SkipWaterSync();
        int L = b.Level;
        int ex0 = b.S0 - Approach, ex1 = b.S0 + b.Length + Approach - 1;   // exact area along the span
        int et0 = b.T0 - Margin, et1 = b.T0 + b.Width - 1 + Margin;        // exact area across
        int w0 = b.S0 + b.LandUnder, w1 = b.S0 + b.Length - 1 - b.LandUnder;
        double bankH = b.Deck - 1 + b.ApproachLift;
        var blend = RiverBlend(w, b, et0, et1, w0, w1);
        // the ground around the bridge as ONE target surface, joined smoothly to the terrain (the user's wish 2026-11-25: no edges; design
        // agreed in the chat): 1. water, bed and approach as before, only noted; 2. distance of every point to the final water line;
        // 3. bank slope 1 : 1.5 from the dry edge (level + 1.15) as an upper limit, faded out towards the edge of the work area.
        // Before, single corrections with their own borders left walls of 5–10 m at the water beside the abutments
        int blendLen = BlendFor(b), nps = (int)Math.Ceiling(b.NarrowPerSide);
        int fadeEnd = Math.Max(Reach, blendLen + 20);   // the bank slope fades out up to here (across)
        int tPad = Math.Max(Reach, blendLen + 6);
        int sLo = ex0 - Reach, sHi = ex1 + Reach;
        foreach (var sp in blend.Values)
        {
            sLo = Math.Min(sLo, (int)Math.Floor(Math.Min(sp.tw0, sp.r0)) - Feather);
            sHi = Math.Max(sHi, (int)Math.Ceiling(Math.Max(sp.tw1, sp.r1)) + Feather);
        }
        int tLo = et0 - tPad, tHi = et1 + tPad, nS = sHi - sLo + 1, nT = tHi - tLo + 1;
        // prefabs outside the bridge are protected from the river transition
        var prefabBoxes = w.PrefabSnapshot();
        bool InPrefab(int x, int z)
        {
            for (int k = 0; k < prefabBoxes.Length; k++)
            {
                var p = prefabBoxes[k];
                if (x >= p.X - 1 && x <= p.X + p.SizeX && z >= p.Z - 1 && z <= p.Z + p.SizeZ) return true;
            }
            return false;
        }
        // per cell of the area: 0 = leave, 1 = fixed (bridge, road, approach), 2 = free ground; target height, water after the build
        var mode = new byte[nS * nT];
        var tgt = new double[nS * nT];
        var wetAfter = new bool[nS * nT];
        var touchedAt = new bool[nS * nT];
        int At(int s, int t) => (s - sLo) * nT + (t - tLo);
        // 1. water, bed and approach
        for (int t = tLo; t <= tHi; t++)
        {
            (double tw0, double tw1, double r0, double r1, double u) sp = default;
            bool hasBlend = (t < et0 || t > et1) && blend.TryGetValue(t, out sp);
            int dt = t < et0 ? et0 - t : t > et1 ? t - et1 : 0;
            for (int s = sLo; s <= sHi; s++)
            {
                int x = b.AlongX ? s : t, z = b.AlongX ? t : s;
                if (!w.Contains(x, z)) continue;
                int i = w.Index(x, z), j = At(s, t);
                int ds = s < ex0 ? ex0 - s : s > ex1 ? s - ex1 : 0;
                double h = w.Heights[i] / 256.0;
                bool origWet = w.WaterMask[i] == L;
                var (p, wet) = Profile(b, Math.Clamp(s, ex0, ex1));
                double target = h;
                bool shouldWet = origWet;
                bool touched = false;
                if (ds == 0 && dt == 0)
                {
                    target = p;
                    shouldWet = wet;
                    touched = true;
                    mode[j] = 1;
                }
                else
                {
                    if (w.RoadMask[i] != 0 || InPrefab(x, z)) continue;
                    mode[j] = 2;
                    // 1a. smooth riverbank transition upstream/downstream of the bridge (removes rectangular corners on slanted rivers)
                    bool inBlendWater = hasBlend && s >= Math.Round(sp.tw0) && s <= Math.Round(sp.tw1);
                    if (hasBlend && sp.u < 0.999)
                    {
                        if (inBlendWater)
                        {
                            // inside the smoothly guided river channel: deepen both newly carved dry bank and any shallow old shoreline rim
                            double dep = Math.Min(s - sp.tw0, sp.tw1 - s);
                            double bed = Math.Max(L - 0.8 - Math.Max(0, dep) * 0.7, L - 4.0);
                            // existing water: the deepening runs out towards the end of the transition (2026-11-26: river beds whose deepest
                            // line wanders to one side keep wide shallow shelves; deepened up to the last meter they left an underwater step of 1.5 m)
                            if (origWet && h > bed) bed = h + (bed - h) * (1 - Smooth((sp.u - 0.5) / 0.5));
                            if (!origWet || h > bed)
                            {
                                target = Math.Min(h, bed);
                                shouldWet = true;
                                touched = true;
                            }
                        }
                        else
                        {
                            double distBank = s < sp.tw0 ? sp.tw0 - s : s - sp.tw1;
                            // only fill water that lies strictly between the old river edge and the new bridge-aligned edge
                            // (never build a dry strip across water with a pocket of water trapped behind it)
                            bool inFillWedge = (s < sp.tw0 && sp.tw0 > sp.r0 + 0.5 && s >= sp.r0 - 1)
                                || (s > sp.tw1 && sp.tw1 < sp.r1 - 0.5 && s <= sp.r1 + 1);
                            if (origWet && inFillWedge && distBank <= Math.Max(24, nps + 6))
                            {
                                double edgeH = L + Lake.WetMargin + 0.15;
                                double rise = Math.Min(distBank * 0.65, Math.Max(0, bankH - edgeH));
                                if (nps > 0)
                                {
                                    // narrowed river: the new land rises from the new water edge to the old bank behind it (not up to the
                                    // approach: that left a step of 2 m where the old bank began – test "narrows a too wide river")
                                    int sb = s < sp.tw0 ? (int)Math.Floor(sp.r0) - 2 : (int)Math.Ceiling(sp.r1) + 2;
                                    int bx = b.AlongX ? sb : t, bz = b.AlongX ? t : sb;
                                    double oldBank = w.Contains(bx, bz) ? w.Heights[w.Index(bx, bz)] / 256.0 : edgeH;
                                    double span = Math.Max(2, s < sp.tw0 ? sp.tw0 - sp.r0 : sp.r1 - sp.tw1);
                                    double goal = Math.Clamp(oldBank, edgeH, Math.Max(edgeH, bankH));
                                    rise = (goal - edgeH) * Smooth(distBank / span);
                                }
                                target = Math.Max(h, edgeH + rise);
                                shouldWet = false;
                                touched = true;
                            }
                        }
                    }
                    // 1b. approach & abutment: from the exact area into the terrain, 1 : 2 on average (3 m of width per meter of height,
                    // 8–60 m; before 2 m per meter up to 25 m – at a hill 40 m higher the ground stood 2.4 m per meter)
                    if (dt <= Reach && ds <= Reach && !inBlendWater)
                    {
                        // when in the blend zone outside the water, match Profile's 2 m bank slope (-2 / -1 / 0) so there is no step at et0/et1
                        double pTarget = p;
                        if (hasBlend)
                        {
                            double distB = s < sp.tw0 ? sp.tw0 - s : s - sp.tw1;
                            pTarget = bankH - Math.Max(0, 3 - distB);
                        }
                        double d = Math.Sqrt(ds * ds + dt * dt);   // round corners
                        double baseH = touched ? target : h;
                        double width = Math.Min(Reach, Math.Max(8, 3 * Math.Abs(pTarget - baseH)));
                        if (d < width)
                        {
                            double t2 = baseH + (pTarget - baseH) * (1 - Smooth(d / width));
                            if (shouldWet && t2 > baseH) t2 = baseH;   // existing river water outside the transition is not filled in
                            if (!shouldWet) t2 = Math.Max(t2, Math.Min(baseH, L + Lake.WetMargin + 0.05));
                            target = t2;
                            shouldWet = target < L + Lake.WetMargin && shouldWet;
                            touched = true;
                        }
                    }
                    // 1c. under the bridge the bed follows the piece (deeper or shallower than the river beside it): beside the exact area it
                    // goes over smoothly into the bed of the river, 3 m per meter of difference (6–24 m) – before a step of up to 2 m
                    if (dt > 0 && ds == 0 && s >= w0 && s <= w1 && shouldWet)
                    {
                        double cur = touched ? target : h;
                        double width = Math.Clamp(3 * Math.Abs(p - cur) + 4, 6, 24);
                        if (dt < width)
                        {
                            target = cur + (p - cur) * (1 - Smooth(dt / width));
                            touched = true;
                        }
                    }
                }
                tgt[j] = target;
                wetAfter[j] = shouldWet;
                touchedAt[j] = touched;
            }
        }
        // 2. distance of every cell to the water after the build (chamfer distance; water outside the area counts as far away)
        var dist = new float[nS * nT];
        for (int s = sLo; s <= sHi; s++)
            for (int t = tLo; t <= tHi; t++)
            {
                int j = At(s, t), x = b.AlongX ? s : t, z = b.AlongX ? t : s;
                bool wet = mode[j] != 0 ? wetAfter[j] : w.Contains(x, z) && w.WaterMask[w.Index(x, z)] == L;
                dist[j] = wet ? 0 : 1e6f;
            }
        for (int pass = 0; pass < 2; pass++)
        {
            int da = pass == 0 ? -1 : 1;
            for (int a = pass == 0 ? 0 : nS - 1; a >= 0 && a < nS; a -= da)
                for (int c = pass == 0 ? 0 : nT - 1; c >= 0 && c < nT; c -= da)
                {
                    int j = a * nT + c;
                    float d = dist[j];
                    bool aOk = a + da >= 0 && a + da < nS, cOk = c + da >= 0 && c + da < nT, c2Ok = c - da >= 0 && c - da < nT;
                    if (aOk) d = Math.Min(d, dist[(a + da) * nT + c] + 1);
                    if (cOk) d = Math.Min(d, dist[a * nT + c + da] + 1);
                    if (aOk && cOk) d = Math.Min(d, dist[(a + da) * nT + c + da] + 1.414f);
                    if (aOk && c2Ok) d = Math.Min(d, dist[(a + da) * nT + c - da] + 1.414f);
                    dist[j] = d;
                }
        }
        // 3. bank slope 1 : 1.5 from the dry edge (level + 1.15) as an upper limit for all dry ground of the area except the bridge with its
        // road strip; full near the bridge (river channel BlendLen across, 10 m along), faded out smoothly to the edge of the area (Reach),
        // so that no rule stops at a border. Natural steep banks near the bridge are flattened too (the user's decision 2026-11-25)
        int changed = 0, water = 0, dried = 0;
        for (int s = sLo; s <= sHi; s++)
            for (int t = tLo; t <= tHi; t++)
            {
                int j = At(s, t);
                if (mode[j] == 0) continue;
                int x = b.AlongX ? s : t, z = b.AlongX ? t : s, i = w.Index(x, z);
                double target = tgt[j];
                bool shouldWet = wetAfter[j], touched = touchedAt[j];
                bool road = s >= ex0 && s <= ex1 && t >= b.T0 && t < b.T0 + b.Width;   // bridge and its approaches
                if (!shouldWet && !road)
                {
                    int dt = t < et0 ? et0 - t : t > et1 ? t - et1 : 0;
                    int ds = s < ex0 ? ex0 - s : s > ex1 ? s - ex1 : 0;
                    double wA = dt <= blendLen ? 1 : 1 - Smooth((dt - blendLen) / (double)(fadeEnd - blendLen));
                    double wB = ds <= 10 ? 1 : 1 - Smooth((ds - 10) / (double)(Reach - 10));
                    double cap = L + Lake.WetMargin + 0.15 + Math.Max(0, dist[j] - 1) / BankSlope;
                    // beside the bridge and its approach road the ground falls away from the road edge with the same slope at most (otherwise a
                    // step stood at the edge of the road near the water, 2.4 m at the rwg bridge)
                    int rs = Math.Clamp(s, ex0, ex1), rt = Math.Clamp(t, b.T0, b.T0 + b.Width - 1);
                    double dRoad = Math.Sqrt((double)(s - rs) * (s - rs) + (double)(t - rt) * (t - rt));
                    cap = Math.Max(cap, Profile(b, rs).h - dRoad / BankSlope);
                    double cut = Math.Max(0, target - cap) * wA * wB;
                    if (cut > 0.005)
                    {
                        target = Math.Max(target - cut, L + Lake.WetMargin + 0.05);
                        touched = true;
                    }
                }
                if (!touched) continue;
                int ds2 = s < ex0 ? ex0 - s : s > ex1 ? s - ex1 : 0, dt2 = t < et0 ? et0 - t : t > et1 ? t - et1 : 0;
                var v = (ushort)Math.Clamp(Math.Round(target * 256), 0, 65535);
                if (v != w.Heights[i]) { e.SetHeight(i, v); changed++; }
                if (shouldWet && w.WaterMask[i] != L) { e.SetWater(i, (byte)L); water++; }
                else if (!shouldWet && w.WaterMask[i] == L && (target >= L + Lake.WetMargin || (ds2 == 0 && dt2 == 0))) { e.SetWater(i, 0); dried++; }
            }
        // remove any thin dry slivers/spikes trapped inside the water (water on opposite sides or on >= 3 sides)
        int size = w.Size;
        for (int pass = 0; pass < 2; pass++)
            for (int s = sLo; s <= sHi; s++)
                for (int t = et0 - tPad; t <= et1 + tPad; t++)
                {
                    if (s >= ex0 && s <= ex1 && t >= et0 && t <= et1) continue;
                    int x = b.AlongX ? s : t, z = b.AlongX ? t : s;
                    if (!w.Contains(x, z) || Math.Abs(x) >= w.Half - 1 || Math.Abs(z) >= w.Half - 1) continue;
                    int i = w.Index(x, z);
                    if (w.WaterMask[i] != 0 || w.RoadMask[i] != 0 || InPrefab(x, z)) continue;
                    bool wE = w.WaterMask[i + 1] == L, wW = w.WaterMask[i - 1] == L, wN = w.WaterMask[i + size] == L, wS = w.WaterMask[i - size] == L;
                    int wc = (wE ? 1 : 0) + (wW ? 1 : 0) + (wN ? 1 : 0) + (wS ? 1 : 0);
                    if (wc >= 3 || (wE && wW) || (wN && wS))
                    {
                        double bed = Math.Min(w.Heights[i] / 256.0, L - 1.5);
                        e.SetHeight(i, (ushort)Math.Clamp(Math.Round(bed * 256), 0, 65535));
                        e.SetWater(i, (byte)L);
                        water++; changed++;
                    }
                }
        // no dry cell beside the water below the waterline in the changed area: in the exact area it gets water (bank of the straight
        // piece), outside it is raised to just above the waterline – flooding it would let the water run on into lower land behind a levee
        // (it filled that land up to the edge of this area, where the water then spilled over in the game)
        for (int s = sLo - 1; s <= sHi + 1; s++)
            for (int t = tLo - 1; t <= tHi + 1; t++)
            {
                int x = b.AlongX ? s : t, z = b.AlongX ? t : s;
                if (!w.Contains(x, z) || Math.Abs(x) >= w.Half - 1 || Math.Abs(z) >= w.Half - 1) continue;
                int i = w.Index(x, z);
                if (w.WaterMask[i] != 0 || w.Heights[i] >= (L + Lake.WetMargin) * 256 || w.RoadMask[i] != 0) continue;
                if (w.WaterMask[i + 1] != L && w.WaterMask[i - 1] != L && w.WaterMask[i + size] != L && w.WaterMask[i - size] != L) continue;
                bool exact = s >= ex0 && s <= ex1 && t >= et0 && t <= et1;
                if (exact) { e.SetWater(i, (byte)L); water++; }
                else { e.SetHeight(i, (ushort)Math.Round((L + Lake.WetMargin + 0.05) * 256)); changed++; }
            }
        // pieces: end – n × middle – end. Along x: west end turned 2, middle and east end 0; along z: south end 3, middle and north end 1
        // other kits: Turn turns the outer end of their end piece to the east, TileTurn lays the middle piece along x (BridgeKitDef);
        // a bridge of one piece is set once
        int y = b.Deck - b.DeckLayer, pieces = 0;
        PlacedPrefab Piece(string name, PrefabDef def, int s, int rot) =>
            new() { Name = name, Def = def, X = b.AlongX ? s : b.T0, Z = b.AlongX ? b.T0 : s, Y = y, Rotation = ((rot % 4) + 4) % 4 };
        if (b.Single) { e.AddPrefab(Piece(b.EndPiece, endDef, b.S0, b.Turn + (b.AlongX ? 0 : 1))); pieces++; }
        else
        {
            e.AddPrefab(Piece(b.EndPiece, endDef, b.S0, b.Turn + (b.AlongX ? 2 : 3))); pieces++;
            for (int k = 0; k < b.Tiles; k++) { e.AddPrefab(Piece(b.TilePiece ?? TileName, tileDef, b.S0 + b.EndLength + b.TileLength * k, b.TileTurn + (b.AlongX ? 0 : 1))); pieces++; }
            e.AddPrefab(Piece(b.EndPiece, endDef, b.S0 + b.EndLength + b.TileLength * b.Tiles, b.Turn + (b.AlongX ? 0 : 1))); pieces++;
        }
        // straight asphalt approaches as wide as the bridge (roads can be connected to them later). The user's test (2026-11-19): the road
        // must lie on a whole meter (deck layer − 1, e.g. 44.0 under a deck at 45) and the asphalt must go on under the end piece as far as
        // its bank part, otherwise the transition does not look clean. Under the end piece the ground lies at road height right up to the water
        // (5 m; the steps −1/−2 m stay only beside the bridge): the game does not shape the ground under parts (measured 2026-11-19: dtm_processed =
        // dtm.raw under the bridge; under POIs it writes a full block), with the steps the bridge seemed to float
        int approach = 0;
        foreach (var (a0, a1) in new[] { (b.S0 - Approach, b.S0 + b.LandUnder - 1), (b.S0 + b.Length - b.LandUnder, b.S0 + b.Length + Approach - 1) })
            for (int s = a0; s <= a1; s++)
                for (int t = b.T0; t <= b.T0 + b.Width - 1; t++)
                {
                    int x = b.AlongX ? s : t, z = b.AlongX ? t : s;
                    // beside the own terrain of the piece (SideInset: bridge_concrete_2 has none on its outer 2 m) the ground lies like the approach
                    bool side = t < b.T0 + b.SideInset || t > b.T0 + b.Width - 1 - b.SideInset;
                    bool under = !side && ((s >= b.S0 && s < b.S0 + b.TerrainColumns) || (s >= b.S0 + b.Length - b.TerrainColumns && s < b.S0 + b.Length));
                    bool inBridge = s >= b.S0 && s < b.S0 + b.Length;
                    double lift = under ? b.RoadLift - b.TerrainDrop : inBridge && !side ? b.RoadLift : b.ApproachLift;
                    e.SetHeight(w.Index(x, z), (ushort)Math.Round((b.Deck - 1 + lift) * 256));
                    // 8 m asphalt with 1 m gravel on each side: together exactly the width of the road on the bridge (10x10 kit: the whole
                    // piece), the gravel runs towards its pillars (the user's idea 2026-11-19; before 10 m asphalt and the gravel outside the bridge);
                    // beside a narrower road (RoadInset: railings, walls) the ground stays level but without surface. On a gravel road everything is gravel
                    int r0 = b.T0 + b.RoadInset, r1 = b.T0 + b.Width - 1 - b.RoadInset;
                    if (t < r0 || t > r1) { approach++; continue; }
                    bool edge = b.Gravel || t == r0 || t == r1;
                    e.SetSplat(x, z, edge ? WorldEdits.GravelPixel : WorldEdits.AsphaltPixel);
                    e.SetRoad(w.Index(x, z), edge ? WorldData.Gravel : WorldData.Asphalt);
                    approach++;
                }
        return new BridgeStats(changed, water, dried, pieces, approach);
    }
}

public static class BridgePiece
{
    /// number of outer columns of the end piece (counted from its outer end, local x = size − 1 downwards) that have a terrain block
    /// one layer below the deck (layer 9 of the 10x10 kit) in the middle row; 0 = none or no block file
    public static int TerrainColumns(string xmlFile, int deckLayer = 10) => BridgeAnalyzer.TerrainColumns(xmlFile, deckLayer);
}
