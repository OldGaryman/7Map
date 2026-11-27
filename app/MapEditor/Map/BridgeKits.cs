using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapEditor.Map;

/// A bridge kit (the user's request 2026-11-24: other bridge mods besides YAMEE_Bridges, each with a check box, analysed so that it fits,
/// usable for asphalt and/or gravel roads). Either an end piece + a middle piece that can be lined up (like YAMEE_Bridges) or a bridge
/// of one piece with a fixed length. All values that Bridge.Find/Build need come from here (before 2026-11-24 they were fixed in the code
/// for the 10x10 kit; the values of that kit were found in the user's game tests 21–25, see FLUSS_ANALYSE.md).
/// Lengths along the span, "across" = width of the road. Orientation: Turn = rotation that turns the outer end of the end piece to the east
/// (single piece: rotation that lays the span along x); TileTurn = rotation that lays the middle piece along x.
public sealed class BridgeKitDef
{
    public const string YameeId = "yamee_10x10", GameId = "game_rwg_bridge", WoodId = "yamee_wood1", RwgId = "yamee_rwg";
    public const string RwgEndName = "yamee_rwg_bridge_end_01";
    /// copy of the game's rwg_bridge_tile_01 in YAMEE_Bridges with YOffset 0 (copies no air, 2026-11-25); without it the game's piece
    public const string RwgTileName = "yamee_rwg_bridge_tile_01";
    public const string WoodName = "yamee_bridge_wood1";

    public string Id = "", Label = "", Mod = "";
    public string End = "", Tile;                // prefab names; Tile null/empty = bridge of one piece
    public bool Active, Asphalt = true, Gravel = true, Tested;
    public int Turn, TileTurn;
    /// the user turned the end pieces round (the analysis took the wrong end as the outer one)
    public bool Flip;
    public int Width = 10, EndLength = 13, TileLength = 10;
    /// the road on the bridge is so much narrower than the piece on each side (railings, walls, beams: bridge_wood1 1 m, the user 2026-11-24);
    /// null = not known yet (older bridges.json): then 0 like before. The only values besides the check boxes the user changes in the window
    public int? RoadInset;
    /// columns at each side of a one-piece bridge without own terrain on its land part (rebuilt bridge_concrete_2: 2); there the ground lies like the approach
    public int SideInset;
    /// first air layer above the deck inside the piece (10 = the road lies on layer 9)
    public int DeckLayer = 10;
    /// deck layer above the water level (m)
    public int Clearance = 4;
    /// from the outer end of the end piece so many meters are land (the road goes on under the piece), then the water begins
    public int LandUnder = 6;
    /// outer columns of the end piece with their own terrain blocks one layer below the deck
    public int TerrainColumns = 3;
    public int MaxTiles = 8;
    public double RoadLift = 0, ApproachLift = 0.18, TerrainDrop = 0.51;
    /// river bed under the bridge at least so many meters above the bottom of the piece (1.5: layer 0 is copied air, Brueckentest45;
    /// pieces with YOffset 0 copy no air: 0 = down to the lowest point of the piece – the user 2026-11-25 after Brueckentest50)
    public double BedAbove = 1.5;
    /// analysis: why the kit cannot be used (null = usable)
    public string Problem;
    /// what the analysis found (shown in the window)
    public string Notes;

    [JsonIgnore] public PrefabDef EndDef, TileDef;
    [JsonIgnore] public string EndImage, TileImage;   // preview pictures (.jpg next to the .xml)
    [JsonIgnore] public bool Single => string.IsNullOrEmpty(Tile);
    /// width of the road (asphalt with its gravel edges) on the bridge and its approaches
    [JsonIgnore] public int RoadWidth => Math.Max(4, Width - 2 * (RoadInset ?? 0));
    [JsonIgnore] public bool Builtin => Id == YameeId || Id == GameId || Id == WoodId || Id == RwgId || RebuiltOf(End) != null && Id == End;
    /// the user may take it out of the list: all kits except our own (YAMEE_Bridges); the bridge of the game too (the user's request 2026-11-25)
    [JsonIgnore] public bool Removable => !Builtin || Id == GameId;
    /// only in bridges.json: a kit that comes by itself (the bridge of the game) was removed by the user and stays away
    public bool Removed;

    /// one-piece bridges of the game rebuilt in the mod YAMEE_Bridges by tools/make_game_bridge after the rules of BRUECKENBAU.md (the user's
    /// request 2026-11-24): terrain surface over the whole width at both ends, solid terrainFiller below it (layer 0 air), the designers'
    /// density trick at the joint, no terrain over the water. Piece name = "yamee_" + name in the game; the kit id is the piece name
    public static readonly (string Game, string Label, bool Gravel, bool Broken)[] Rebuilt =
    {
        ("bridge_concrete_1", "YAMEE concrete bridge 1 (bridge_concrete_1)", false, false),
        ("bridge_concrete_2", "YAMEE concrete bridge 2 (bridge_concrete_2)", false, false),
        ("bridge_asphalt1", "YAMEE truss bridge (bridge_asphalt1)", false, false),
        ("bridge_asphalt_broken", "YAMEE broken truss bridge (bridge_asphalt_broken)", false, true),
        ("bridge_wood2", "YAMEE wooden bridge 2 (bridge_wood2)", true, false),
    };
    /// the game bridge a rebuilt piece comes from (null = not one of them)
    public static string RebuiltOf(string piece) =>
        piece != null && piece.StartsWith("yamee_", StringComparison.OrdinalIgnoreCase)
            ? Rebuilt.Select(r => r.Game).FirstOrDefault(g => g.Equals(piece[6..], StringComparison.OrdinalIgnoreCase)) : null;
    [JsonIgnore] public bool Found => EndDef != null && (Single || TileDef != null);
    [JsonIgnore] public bool Usable => Found && Problem == null;
    /// narrowest and widest water the bridge spans (m)
    [JsonIgnore] public int MinWater => Single ? EndLength - 2 * LandUnder : 2 * (EndLength - LandUnder) + TileLength;
    [JsonIgnore] public int MaxWater => Single ? EndLength - 2 * LandUnder : 2 * (EndLength - LandUnder) + TileLength * MaxTiles;
    public bool For(bool gravel) => gravel ? Gravel : Asphalt;
    public BridgeKitDef Clone() => (BridgeKitDef)MemberwiseClone();

    /// the kit of the mod YAMEE_Bridges with the values from the game tests (end piece edited by the user: its length and terrain columns come from the piece)
    public static BridgeKitDef Yamee(int endLength = 13, int terrainColumns = 3) => new()
    {
        Id = YameeId, Label = "YAMEE Bridges 10×10", Mod = "YAMEE_Bridges", End = Bridge.EndName, Tile = Bridge.TileName,
        Active = true, Tested = true, EndLength = endLength, TerrainColumns = terrainColumns, LandUnder = Math.Max(1, endLength - 7),
    };

    /// the covered wooden bridge of the game (bridge_wood1, 39 × 11 m) rebuilt in the mod YAMEE_Bridges (the user's request 2026-11-24):
    /// its land part at both ends (4 m since 2026-11-24, before 11 m) is solid terrainFiller (−128) up to one layer below the deck, like the 10x10 end piece of the
    /// tests 24/25 (in the game the whole underside was terrainFillerAdaptive: holes and water where earth belongs, tests 26/27).
    /// The deck (half plates) lies on layer 9, the road beside it on layer 8 (deck − 1). The analysis would take the roof as the deck.
    public static BridgeKitDef YameeWood(int terrainColumns = 4) => new()
    {
        Id = WoodId, Label = Loc.T("YAMEE wooden bridge (bridge_wood1)"), Mod = "YAMEE_Bridges", End = WoodName, Tile = null,
        Active = true, Width = 11, RoadInset = 1, EndLength = 39, TileLength = 0, DeckLayer = 9, Clearance = 4, LandUnder = 4,   // land 4 m: the river may be 7 m wider on each side than the 11 m land part (the user in Brueckentest30, 2026-11-24)
        TerrainColumns = terrainColumns, Turn = 0, TileTurn = 0,
        Notes = Loc.T("One piece, 39 m long: water up to 31 m wide. Land part with solid terrain (made by YAMEE from bridge_wood1)."),
    };

    public void ApplyTo(BridgeSite s)
    {
        s.Kit = Id; s.EndPiece = End; s.TilePiece = Tile; s.Single = Single;
        s.EndLength = EndLength; s.TileLength = TileLength; s.Width = Width; s.RoadInset = Math.Clamp(RoadInset ?? 0, 0, (Width - 4) / 2); s.SideInset = Math.Clamp(SideInset, 0, (Width - 4) / 2); s.LandUnder = LandUnder;
        s.Clearance = Clearance; s.DeckLayer = DeckLayer; s.TerrainColumns = TerrainColumns; s.Turn = (Turn + (Flip && !Single ? 2 : 0)) % 4; s.TileTurn = TileTurn;
        s.RoadLift = RoadLift; s.ApproachLift = ApproachLift; s.TerrainDrop = TerrainDrop; s.BedAbove = BedAbove;
    }

    /// values that the analysis (or the user) sets; copied when a kit is analysed again
    public void CopyValuesFrom(BridgeKitDef k)
    {
        Turn = k.Turn; TileTurn = k.TileTurn; Width = k.Width; RoadInset = k.RoadInset; SideInset = k.SideInset; EndLength = k.EndLength; TileLength = k.TileLength; DeckLayer = k.DeckLayer;
        Clearance = k.Clearance; LandUnder = k.LandUnder; TerrainColumns = k.TerrainColumns; MaxTiles = k.MaxTiles;
        RoadLift = k.RoadLift; ApproachLift = k.ApproachLift; TerrainDrop = k.TerrainDrop; BedAbove = k.BedAbove; Problem = k.Problem; Notes = k.Notes;
    }
}

/// the blocks of a prefab (.blocks.nim + .tts, see PrefabSurface)
sealed class PieceBlocks
{
    public int SX, SY, SZ;
    byte[] t;
    readonly HashSet<int> terrain = new(), air = new() { 0 }, thin = new(), road = new();

    public static PieceBlocks Read(string xmlFile)
    {
        if (xmlFile == null) return null;
        string tts = Path.ChangeExtension(xmlFile, ".tts"), nim = Path.ChangeExtension(xmlFile, null) + ".blocks.nim";
        if (!File.Exists(tts) || !File.Exists(nim)) return null;
        var p = new PieceBlocks();
        using (var br = new BinaryReader(File.OpenRead(nim)))
        {
            br.ReadInt32();
            int n = br.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int id = br.ReadInt32();
                string name = Encoding.ASCII.GetString(br.ReadBytes(br.ReadByte()));
                if (name.StartsWith("terr", StringComparison.Ordinal)) p.terrain.Add(id);
                if (name == "terrAsphalt" || name == "terrGravel") p.road.Add(id);
                if (name.Equals("air", StringComparison.OrdinalIgnoreCase)) p.air.Add(id);
                // half blocks and plates: a deck of them lies in the lower part of its layer (YAMEE_Bridges: concreteShapes:cubeHalf)
                if (name.Contains("half", StringComparison.OrdinalIgnoreCase) || name.Contains("plate", StringComparison.OrdinalIgnoreCase) || name.Contains("slab", StringComparison.OrdinalIgnoreCase)) p.thin.Add(id);
            }
        }
        p.t = File.ReadAllBytes(tts);
        if (p.t.Length < 14) return null;
        p.SX = BitConverter.ToInt16(p.t, 8); p.SY = BitConverter.ToInt16(p.t, 10); p.SZ = BitConverter.ToInt16(p.t, 12);
        if (p.SX <= 0 || p.SY <= 0 || p.SZ <= 0 || p.t.Length < 14 + 4L * p.SX * p.SY * p.SZ) return null;
        return p;
    }

    int Id(int x, int y, int z) => (int)(BitConverter.ToUInt32(t, 14 + 4 * (x + y * SX + z * SX * SY)) & 0x7FFF);
    public bool Solid(int x, int y, int z) => x >= 0 && x < SX && y >= 0 && y < SY && z >= 0 && z < SZ && !air.Contains(Id(x, y, z));
    public bool Terrain(int x, int y, int z) => x >= 0 && x < SX && y >= 0 && y < SY && z >= 0 && z < SZ && terrain.Contains(Id(x, y, z));
    public bool Thin(int x, int y, int z) => x >= 0 && x < SX && y >= 0 && y < SY && z >= 0 && z < SZ && thin.Contains(Id(x, y, z));
    /// road terrain (terrAsphalt, terrGravel): the surface the approach road meets
    public bool Road(int x, int y, int z) => x >= 0 && x < SX && y >= 0 && y < SY && z >= 0 && z < SZ && road.Contains(Id(x, y, z));
    /// highest solid block of a column, −1 = none
    public int Top(int x, int z)
    {
        for (int y = SY - 1; y >= 0; y--) if (Solid(x, y, z)) return y;
        return -1;
    }
}

/// Reads the blocks of the pieces and works out how a kit fits (span axis, width, deck, outer end, terrain columns, water under the end).
/// The heights in the game (RoadLift, ApproachLift, TerrainDrop) cannot be seen in the blocks: they start with the values of YAMEE_Bridges.
public static class BridgeAnalyzer
{
    public static BridgeKitDef Analyze(BridgeKitDef kit, string endXml, string tileXml)
    {
        var k = kit.Clone();
        k.Problem = null;
        var notes = new List<string>();
        var e = PieceBlocks.Read(endXml);
        var t = k.Single ? null : PieceBlocks.Read(tileXml);
        if (e == null) { k.Problem = Loc.T("The block files of the end piece were not found (.tts and .blocks.nim next to the .xml)."); return k; }
        if (!k.Single && t == null) { k.Problem = Loc.T("The block files of the middle piece were not found (.tts and .blocks.nim next to the .xml)."); return k; }
        // span axis: the end piece and the middle piece are equally wide across the road
        bool spanX;
        if (t != null)
        {
            bool xEq = e.SX == t.SX, zEq = e.SZ == t.SZ;
            if (xEq && !zEq) spanX = false;
            else if (zEq && !xEq) spanX = true;
            else if (xEq && zEq) spanX = RailingSpanX(t);
            else { k.Problem = Loc.F("End piece ({0} × {1}) and middle piece ({2} × {3}) are not equally wide.", e.SX, e.SZ, t.SX, t.SZ); return k; }
        }
        else spanX = e.SX != e.SZ ? e.SX > e.SZ : RailingSpanX(e);
        int Span(PieceBlocks p) => spanX ? p.SX : p.SZ;
        int Across(PieceBlocks p) => spanX ? p.SZ : p.SX;
        bool Solid(PieceBlocks p, int s, int y, int a) => spanX ? p.Solid(s, y, a) : p.Solid(a, y, s);
        bool Terr(PieceBlocks p, int s, int y, int a) => spanX ? p.Terrain(s, y, a) : p.Terrain(a, y, s);
        int Top(PieceBlocks p, int s, int a) => spanX ? p.Top(s, a) : p.Top(a, s);
        k.Width = Across(e);
        k.EndLength = Span(e);
        k.TileLength = t != null ? Span(t) : 0;
        if (k.Width < 6 || k.Width > 32) { k.Problem = Loc.F("The road on the bridge would be {0} m wide (6–32 m possible).", k.Width); return k; }
        if (t != null && k.TileLength < 3) { k.Problem = Loc.T("The middle piece is too short."); return k; }
        // deck: most common top inside the piece (the railings stand at the edges; the markings in the middle of the road of
        // YAMEE_Bridges lie one layer higher and only on a few columns, so the middle line alone gave one layer too much)
        var deckPiece = t ?? e;
        var tops = new Dictionary<int, int>();
        for (int s = 1; s < Span(deckPiece) - 1; s++)
            for (int a = 1; a < Across(deckPiece) - 1; a++)
            {
                int top = Top(deckPiece, s, a);
                if (top >= 0) tops[top] = tops.GetValueOrDefault(top) + 1;
            }
        if (tops.Count == 0) { k.Problem = Loc.T("No road deck found in the blocks."); return k; }
        int deckTop = tops.OrderByDescending(p => p.Value).ThenByDescending(p => p.Key).First().Key;
        // the deck layer counts like the game tests of YAMEE_Bridges: a layer of half blocks on top is the first "air" layer (piece at
        // deck − 10 with its cubeHalf deck on layer 10), a deck of full blocks has the air one layer higher
        int thinTop = 0, allTop = 0;
        for (int s = 1; s < Span(deckPiece) - 1; s++)
            for (int a = 1; a < Across(deckPiece) - 1; a++)
            {
                if (Top(deckPiece, s, a) != deckTop) continue;
                allTop++;
                if (spanX ? deckPiece.Thin(s, deckTop, a) : deckPiece.Thin(a, deckTop, s)) thinTop++;
            }
        k.DeckLayer = 2 * thinTop > allTop ? deckTop : deckTop + 1;
        // a bridge of the game rebuilt by tools/make_game_bridge: the deck lies on the terrain surface at the outer end, the land part is where
        // the terrainFiller reaches down to two layers below the surface (the run-out under the first rows over the water lies only one layer below;
        // decks of half blocks or with beams on top would give other values); SideInset = outer columns without own terrain (bridge_concrete_2)
        int rebuiltLand = -1;
        bool rebuilt = BridgeKitDef.RebuiltOf(k.End) != null;
        // any bridge of one piece whose end is road terrain (terrAsphalt/terrGravel – the approach meets it): the deck is one layer above it.
        // The most common top in the middle gave one layer too much for bridge_asphalt_broken (14 instead of 13: the bridge stood 1 m too low,
        // Buzovi-Land, the user 2026-11-25) and bridge_asphalt1 (11 instead of 10) – half blocks and steel parts on the truss deck
        if (k.Single)
        {
            int surface = -1;
            for (int y = e.SY - 1; y >= 0 && surface < 0; y--) if (Terr(e, 0, y, Across(e) / 2)) surface = y;
            bool roadEnd = surface >= 2 && (spanX ? e.Road(0, surface, Across(e) / 2) : e.Road(Across(e) / 2, surface, 0));
            if (surface >= 2 && (roadEnd || rebuilt)) k.DeckLayer = surface + 1;
            if (surface >= 2 && rebuilt)
            {
                bool LandRow(int s) { for (int a = 0; a < Across(e); a++) if (Terr(e, s, surface - 2, a)) return true; return false; }   // a pier may stand in the middle
                int Run(bool high) { int n = 0; while (n < k.EndLength / 2 && LandRow(high ? k.EndLength - 1 - n : n)) n++; return n; }
                // the longer end (the user's asphalt_broken of 2026-11-25: 6 rows of land at one end, 7 at the other): the bridge is built
                // symmetrically, and terrain of the piece over a water row keeps the water out of that column (the game puts water in after the
                // prefabs, from their topmost terrain block) – an extra row of land under the shorter end does no harm
                rebuiltLand = Math.Max(Run(false), Run(true));
                // terrain only in the 2 layers under the deck (the rule of the 10x10; the user's concrete_2 of 2026-11-25): a land row has terrain
                // on the surface and the layer below it in one column (the run-out over the water only one layer)
                if (rebuiltLand == 0)
                {
                    bool Land2(int s) { for (int a = 0; a < Across(e); a++) if (Terr(e, s, surface, a) && Terr(e, s, surface - 1, a)) return true; return false; }
                    int Run2(bool high) { int n = 0; while (n < k.EndLength / 2 && Land2(high ? k.EndLength - 1 - n : n)) n++; return n; }
                    rebuiltLand = Math.Max(Run2(false), Run2(true));
                }
                int Side(bool high) { int n = 0; while (n < Across(e) / 2 && !Terr(e, 0, surface, high ? Across(e) - 1 - n : n)) n++; return n; }
                k.SideInset = Math.Min(Side(false), Side(true));
            }
        }
        if (k.DeckLayer < 3) notes.Add(Loc.T("The deck lies very low in the piece (hardly room for pillars) – check the values."));
        // outer end: terrain blocks one layer below the deck, otherwise the end with more blocks under the deck (abutment)
        int L = k.EndLength, ea = Across(e) / 2;
        int CountTerr(bool high)
        {
            int n = 0;
            for (int i = 0; i < L; i++) { int s = high ? L - 1 - i : i; if (!Terr(e, s, k.DeckLayer - 1, ea)) break; n++; }
            return n;
        }
        int Below(bool high)
        {
            int n = 0;
            for (int i = 0; i < Math.Min(3, L); i++)
                for (int a = 0; a < Across(e); a++)
                    for (int y = 0; y < k.DeckLayer - 1; y++) if (Solid(e, high ? L - 1 - i : i, y, a)) n++;
            return n;
        }
        int hiT = CountTerr(true), loT = CountTerr(false);
        bool outerHigh = hiT != loT ? hiT > loT : Below(true) >= Below(false);
        k.TerrainColumns = k.Single ? Math.Min(hiT, loT) : Math.Max(hiT, loT);
        if (k.Single) { k.Turn = spanX ? 0 : 1; k.TileTurn = 0; }
        else
        {
            k.Turn = spanX ? (outerHigh ? 0 : 2) : (outerHigh ? 3 : 1);
            k.TileTurn = spanX ? 0 : 1;   // the middle piece runs along the same axis as the end piece
        }
        // land under the end: from the outer end inwards up to the first column that is open below the deck (water can flow under it)
        int Land(bool fromHigh)
        {
            for (int i = 0; i < L; i++)
            {
                int s = fromHigh ? L - 1 - i : i;
                bool open = true;
                for (int y = 0; y < Math.Max(1, k.DeckLayer - 3) && open; y++) if (Solid(e, s, y, ea)) open = false;
                if (open) return i;
            }
            return -1;
        }
        int land = k.Single ? Math.Min(Land(true), Land(false)) : Land(outerHigh);
        int fallback = Math.Max(k.TerrainColumns + 3, (int)Math.Round((k.Single ? 0.15 : 0.45) * L));
        k.LandUnder = land > k.TerrainColumns ? land : fallback;
        k.LandUnder = Math.Clamp(k.LandUnder, 1, k.Single ? Math.Max(1, L / 2 - 3) : Math.Max(1, L - 1));
        // rebuilt bridge: the ground of the map lies at deck − 1.51 under the whole land part (Brueckentest41)
        if (rebuiltLand > 0) { k.LandUnder = rebuiltLand; k.TerrainColumns = rebuiltLand; }
        if (k.Single && L - 2 * k.LandUnder < 4) { k.Problem = Loc.T("The bridge is too short to span water."); return k; }
        k.Clearance = Math.Clamp(k.DeckLayer - 6, 2, 12);
        // rebuilt bridge: the beams under the deck in the middle third of the span stay above the water (bridge_concrete_1/_2 and bridge_wood2 have
        // their deck only 2–4 m above the bottom of the piece); columns that reach down to layer 0 are piers and may stand in the water
        if (rebuiltLand > 0)
        {
            int hang = -1;
            for (int s = L / 3; s < L - L / 3; s++)
                for (int a = 0; a < Across(e); a++)
                {
                    if (Solid(e, s, 0, a)) continue;
                    for (int y = 1; y < k.DeckLayer - 1; y++) if (Solid(e, s, y, a)) { if (hang < 0 || y < hang) hang = y; break; }
                }
            if (hang >= 0) k.Clearance = Math.Clamp(Math.Max(k.Clearance, k.DeckLayer - hang + 1), 2, 12);
        }
        // road width: the road reaches from railing to railing (wall to wall), with its gravel edge under them; what sticks out beyond the
        // outermost railing (bridge_wood1: a plank of 1 m on each side outside its walls, the user 2026-11-24: "1 m too wide on each side") is not road.
        // A column is a railing when blocks stand in the 3 layers above the road surface in most rows of the middle third of the span.
        // Same on both sides (the road stays in the middle). 10x10 kit, rwg_bridge: 0, bridge_wood1: 1, asphalt_broken: 1.
        // A railing is at least 2 of these 3 layers high: the 1-block girder beside the truss of asphalt_broken made the road 1 m too narrow
        // on each side (the user 2026-11-25, Brueckentest47); the truss itself stands in the outer column only at its posts
        int sp = Span(deckPiece), ac = Across(deckPiece), s0 = sp / 3, s1 = Math.Max(sp / 3, 2 * sp / 3 - 1);
        // only rows with a deck count (asphalt_broken has a gap in the middle)
        bool DeckRow(int s) { for (int a = 1; a < ac - 1; a++) if (Top(deckPiece, s, a) == deckTop) return true; return false; }
        int deckRows = 0;
        for (int s = s0; s <= s1; s++) if (DeckRow(s)) deckRows++;
        bool Railing(int a)
        {
            int rows = 0;
            for (int s = s0; s <= s1; s++)
            {
                if (deckRows > 0 && !DeckRow(s)) continue;
                int high = 0;
                for (int y = deckTop + 1; y <= deckTop + 3; y++) if (Solid(deckPiece, s, y, a)) high++;
                if (high >= 2) rows++;
            }
            // walls and railings stand in most rows; a truss only at its posts (asphalt_broken: 2 of 11 deck rows) – a few tall rows are enough
            return rows >= Math.Max(2, (deckRows > 0 ? deckRows : s1 - s0 + 1) / 8);
        }
        int left = 0, right = 0;
        while (left < ac && !Railing(left)) left++;
        while (right < ac && !Railing(ac - 1 - right)) right++;
        int inset = left >= ac ? 0 : Math.Min(left, right);   // no railing at all: the whole width
        k.RoadInset = Math.Clamp(inset, 0, Math.Max(0, (k.Width - 4) / 2));
        notes.Add(k.RoadInset > 0
            ? Loc.F("Road {0} m wide ({1} m less than the piece on each side), deck {2} m above the bottom of the piece.", k.RoadWidth, k.RoadInset, k.DeckLayer)
            : Loc.F("Road {0} m wide, deck {1} m above the bottom of the piece.", k.Width, k.DeckLayer));
        notes.Add(k.Single ? Loc.F("One piece, {0} m long: water up to {1} m wide.", L, k.MinWater)
                           : Loc.F("End piece {0} m, middle piece {1} m: water {2}–{3} m wide.", L, k.TileLength, k.MinWater, k.MaxWater));
        notes.Add(k.TerrainColumns > 0 ? Loc.F("{0} column(s) of own terrain at the end.", k.TerrainColumns) : Loc.T("No own terrain at the end."));
        // the game copies the air of a prefab into the world below layer −YOffset even with CopyAirBlocks False, with True everywhere
        // (Prefab.CopyBlocksIntoChunkNoEntities, read 2026-11-25): such a piece wipes the river bed and the banks under the bridge (Brueckentest44)
        if (AirWipes(endXml) || (!k.Single && AirWipes(tileXml)))
            notes.Add(Loc.T("Warning: the piece copies its air into the ground (CopyAirBlocks True or YOffset below −1) – the terrain under the bridge goes missing in the game. Our pieces use YOffset −1 and CopyAirBlocks False."));
        k.Notes = string.Join(" ", notes);
        return k;
    }

    /// the piece's xml makes the game copy air into the ground under the bridge: CopyAirBlocks True, or YOffset below −1 (air of the layers
    /// below −YOffset is always copied). The good pieces (10x10, rwg) have YOffset −1 and CopyAirBlocks False
    public static bool AirWipes(string xmlFile)
    {
        try
        {
            if (xmlFile == null || !File.Exists(xmlFile)) return false;
            var doc = XmlSafe.Load(xmlFile);
            string Prop(string name) => doc.DocumentElement?.SelectNodes("property")?.Cast<System.Xml.XmlElement>()
                .FirstOrDefault(p => p.GetAttribute("name") == name)?.GetAttribute("value");
            bool copyAir = string.Equals(Prop("CopyAirBlocks"), "true", StringComparison.OrdinalIgnoreCase);
            int yOffset = int.TryParse(Prop("YOffset"), out int y) ? y : 0;
            return copyAir || yOffset < -1;
        }
        catch { return false; }
    }

    /// the game copies no air of the piece at all: CopyAirBlocks False and YOffset ≥ 0 (air is copied only below layer −YOffset,
    /// Prefab.CopyBlocksIntoChunkNoEntities, read 2026-11-25). Then the river bed may lie down to the bottom of the piece (no 1 m
    /// hollow from layer 0 – Brueckentest49, wood2 and concrete_1)
    public static bool NoAirCopied(string xmlFile)
    {
        try
        {
            if (xmlFile == null || !File.Exists(xmlFile)) return false;
            var doc = XmlSafe.Load(xmlFile);
            string Prop(string name) => doc.DocumentElement?.SelectNodes("property")?.Cast<System.Xml.XmlElement>()
                .FirstOrDefault(p => p.GetAttribute("name") == name)?.GetAttribute("value");
            bool copyAir = string.Equals(Prop("CopyAirBlocks"), "true", StringComparison.OrdinalIgnoreCase);
            int yOffset = int.TryParse(Prop("YOffset"), out int y) ? y : 0;
            return !copyAir && yOffset >= 0;
        }
        catch { return false; }
    }

    /// pieces as long as wide: the line across the road meets the railings (higher blocks at both edges), the line along the road is flat
    static bool RailingSpanX(PieceBlocks p)
    {
        double Rise(bool alongX)
        {
            int n = alongX ? p.SX : p.SZ, c = Math.Max(1, (alongX ? p.SZ : p.SX) / 4);   // a quarter line: not on the markings in the middle
            var tops = Enumerable.Range(0, n).Select(i => alongX ? p.Top(i, c) : p.Top(c, i)).ToArray();
            var inner = tops.Skip(1).Take(Math.Max(1, n - 2)).OrderBy(v => v).ToArray();
            double med = inner[inner.Length / 2];
            return Math.Max(tops[0], tops[^1]) - med;
        }
        double rx = Rise(true), rz = Rise(false);
        // the line along x rises at the edges → x is across → span along z
        return rx != rz ? rx < rz : p.SX >= p.SZ;
    }

    /// outer columns of the end piece with terrain blocks one layer below the deck (YAMEE_Bridges: outer end at high x)
    public static int TerrainColumns(string xmlFile, int deckLayer = 10)
    {
        var p = PieceBlocks.Read(xmlFile);
        if (p == null || deckLayer < 1 || deckLayer >= p.SY) return 0;
        int lz = p.SZ / 2, n = 0;
        for (int lx = p.SX - 1; lx >= 0 && p.Terrain(lx, deckLayer - 1, lz); lx--) n++;
        return n;
    }
}

/// The active kits for building (set by the main window from bridges.json and the prefabs of the game; the tests set them directly).
/// Several kits for one surface: each bridge place takes them in an order mixed by its position (the user's decision 2026-11-24:
/// "randomly mixed"), the first that fits the river is used.
public sealed class BridgeKits
{
    public List<BridgeKitDef> Kits { get; } = new();
    public BridgeKits(IEnumerable<BridgeKitDef> kits) => Kits.AddRange(kits.Where(k => k.Usable));

    public bool Any(bool gravel) => Kits.Any(k => k.For(gravel));
    public BridgeKitDef Get(string id) => Kits.FirstOrDefault(k => k.Id == id);

    /// kits for the surface in an order that depends on the place (stable for the same place, a mix over the map)
    public IEnumerable<BridgeKitDef> Order(bool gravel, double x, double z)
    {
        var list = Kits.Where(k => k.For(gravel)).ToList();
        if (list.Count == 0) list = Kits.ToList();   // a gravel road with only asphalt kits (or the other way round) still gets a bridge
        int gx = (int)Math.Floor(x / 64), gz = (int)Math.Floor(z / 64);
        return list.OrderBy(k => Hash(gx, gz, k.Id)).ThenBy(k => k.Id, StringComparer.Ordinal);
    }

    static uint Hash(int a, int b, string s)
    {
        uint h = 2166136261;
        void Add(int v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= 16777619; } }
        Add(a); Add(b);
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    /// a bridge place at the point with the first kit (in the mixed order) that fits; maxWater = widest water allowed (planners)
    public BridgeSite Find(WorldData w, double x, double z, bool gravel, double maxAngle = Bridge.MaxAngle, IReadOnlyList<PlacedPrefab> prefabs = null, int maxWater = int.MaxValue)
    {
        BridgeSite first = null;
        foreach (var k in Order(gravel, x, z))
        {
            var s = Bridge.Find(w, x, z, k, maxAngle, prefabs);
            if (s.Problem == null && s.WaterWidth > maxWater)
            {
                s.Problem = Loc.F("The river is {0:0} m wide here – too wide for a bridge (at most {1} m).", s.RiverWidth, maxWater);
                s.TooWide = true;
            }
            if (s.Problem == null) return s;
            first ??= s;
            if (s.RiverProblem) break;   // the river itself does not fit: the other kits do not help
        }
        return first ?? new BridgeSite { Problem = Loc.T("No bridge kit is active. Choose one under Settings › Mods › Bridge mods."), RiverProblem = true };
    }

    /// the pieces of a bridge (old plans without a kit: YAMEE_Bridges)
    public (PrefabDef End, PrefabDef Tile) Defs(BridgeSite b)
    {
        var k = (b.Kit != null ? Get(b.Kit) : null) ?? Kits.FirstOrDefault(q => q.End.Equals(b.EndPiece ?? "", StringComparison.OrdinalIgnoreCase)) ?? Get(BridgeKitDef.YameeId);
        return k == null ? (null, null) : (k.EndDef, k.TileDef);
    }

    /// names of all pieces of all known kits (to recognise bridges on the map)
    static readonly HashSet<string> pieceNames = new(StringComparer.OrdinalIgnoreCase);
    public static void RememberPieces(IEnumerable<BridgeKitDef> kits)
    {
        lock (pieceNames)
            foreach (var k in kits) { if (!string.IsNullOrEmpty(k.End)) pieceNames.Add(k.End); if (!string.IsNullOrEmpty(k.Tile)) pieceNames.Add(k.Tile); }
    }
    /// pieces of the kits that come by themselves (known before the list of kits is read: the map is loaded first)
    static readonly HashSet<string> builtinPieces = new(new[]
    {
        Bridge.EndName, Bridge.TileName, BridgeKitDef.RwgEndName, BridgeKitDef.RwgTileName, BridgeKitDef.WoodName, "rwg_bridge_end_01", "rwg_bridge_tile_01",
    }.Concat(BridgeKitDef.Rebuilt.Select(r => "yamee_" + r.Game)), StringComparer.OrdinalIgnoreCase);

    public static bool IsBridgePiece(string name)
    {
        if (name == null) return false;
        if (name.StartsWith("yamee_part_bridge", StringComparison.OrdinalIgnoreCase) || builtinPieces.Contains(name)) return true;
        lock (pieceNames) return pieceNames.Contains(name);
    }
}

/// bridges.json in the data folder (next to user.ini): the kits the user added and the check boxes of all kits
public static class BridgeKitStore
{
    static readonly JsonSerializerOptions Options = new() { IncludeFields = true, WriteIndented = true };

    public static string FilePath
    {
        get
        {
            string env = Environment.GetEnvironmentVariable("MAPEDITOR_SETTINGS");
            return Path.Combine(env != null ? Path.GetDirectoryName(Path.GetFullPath(env)) : AppSettings.ConfigFolder, "bridges.json");
        }
    }

    public static List<BridgeKitDef> LoadSaved()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<BridgeKitDef>>(File.ReadAllText(FilePath), Options) ?? new();
                RemovedIds = list.Where(k => k.Removed).Select(k => k.Id).ToHashSet();
                return list.Where(k => !k.Removed).ToList();
            }
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
        RemovedIds = new();
        return new();
    }

    /// kits that come by itself and that the user removed (kept in bridges.json as { Id, Removed: true })
    public static HashSet<string> RemovedIds { get; private set; } = new();

    /// take a kit that comes by itself out of the list for good
    public static void MarkRemoved(BridgeKitDef k) { if (k.Builtin) RemovedIds.Add(k.Id); }

    public static void Save(IEnumerable<BridgeKitDef> kits)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        var list = kits.ToList();
        list.AddRange(RemovedIds.Where(id => !list.Any(k => k.Id == id)).Select(id => new BridgeKitDef { Id = id, Removed = true, Label = id }));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Options), new UTF8Encoding(false));
    }

    /// mod folder of a prefab file ("…\Mods\<name>\…"), otherwise the game
    public static string ModOf(string file)
    {
        if (file == null) return "";
        var parts = file.Split('\\', '/');
        int i = Array.FindIndex(parts, p => p.Equals("Mods", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : "7 Days to Die";
    }

    /// all kits: YAMEE_Bridges (if its pieces are in the game folder), the bridge of the game and the kits added by hand,
    /// with the check boxes from bridges.json; pieces described with describe(name) (null = not found)
    public static List<BridgeKitDef> All(IReadOnlyDictionary<string, string> files, Func<string, PrefabDef> describe)
    {
        var saved = LoadSaved();
        var res = new List<BridgeKitDef>();
        string F(string n) => n == null ? null : files?.GetValueOrDefault(n);
        // 1. YAMEE_Bridges: values from the game tests, length and terrain columns of the (edited) end piece
        if (F(Bridge.EndName) != null && F(Bridge.TileName) != null)
        {
            var end = describe(Bridge.EndName);
            var y = BridgeKitDef.Yamee(end?.SizeX ?? 13, BridgeAnalyzer.TerrainColumns(F(Bridge.EndName)));
            y.Mod = ModOf(F(Bridge.EndName));
            var s = saved.FirstOrDefault(k => k.Id == y.Id);
            if (s != null) { y.Active = s.Active; y.Asphalt = s.Asphalt; y.Gravel = s.Gravel; y.RoadInset = s.RoadInset ?? 0; }   // the width of the road may be changed (the user 2026-11-24)
            res.Add(y);
        }
        // 2. the bridge pieces of the game (RWG does not use them; 14 m wide)
        const string gEnd = "rwg_bridge_end_01", gTile = "rwg_bridge_tile_01";
        if (F(gEnd) != null && F(gTile) != null && !RemovedIds.Contains(BridgeKitDef.GameId))
        {
            var g = saved.FirstOrDefault(k => k.Id == BridgeKitDef.GameId)
                ?? BridgeAnalyzer.Analyze(new BridgeKitDef { Id = BridgeKitDef.GameId, Label = Loc.T("Bridge of the game (rwg_bridge)"), End = gEnd, Tile = gTile }, F(gEnd), F(gTile));
            // the river may be 1 m wider on each side than the analysis says (the user in Brueckentest30, 2026-11-24)
            if (!saved.Any(k => k.Id == BridgeKitDef.GameId)) g.LandUnder = Math.Max(1, g.LandUnder - 1);
            g.RoadInset ??= InsetOf(g, F(gEnd), F(gTile));
            g.Mod = "7 Days to Die";
            res.Add(g);
        }
        // 2b. the bridge of the game with a rebuilt end piece in YAMEE_Bridges (the user's request 2026-11-24, Brueckentest30: the concrete
        // deck stood as an edge above the approach): the outer 2 rows are terrAsphalt on the deck layer with terrainFiller below, like the
        // approach of bridge_concrete_1 (tools/make_rwg_bridge_end); middle piece of the game. Land 3 m like the bridge of the game
        string rTile = F(BridgeKitDef.RwgTileName) != null ? BridgeKitDef.RwgTileName : gTile;
        if (F(BridgeKitDef.RwgEndName) != null && F(rTile) != null)
        {
            var r = saved.FirstOrDefault(k => k.Id == BridgeKitDef.RwgId);
            if (r == null)
            {
                r = BridgeAnalyzer.Analyze(new BridgeKitDef { Id = BridgeKitDef.RwgId, Label = Loc.T("YAMEE bridge of the game (rwg_bridge, asphalt at the ends)"),
                    End = BridgeKitDef.RwgEndName, Tile = rTile }, F(BridgeKitDef.RwgEndName), F(rTile));
                r.LandUnder = 3;
                r.Active = true;   // all bridges of our mod are on at first, except the broken one (the user 2026-11-25, after the ground was made smooth)
            }
            // the ground of the map lies at deck − 1.51 under the whole land part, also under the first concrete row (density −100): with deck − 1 the block
            // above the concrete kept only half air from the map and the terrain poked up as a dirt strip (Brueckentest41, the user 2026-11-24: "geschafft")
            r.TerrainColumns = Math.Max(r.TerrainColumns, r.LandUnder);
            r.RoadInset ??= InsetOf(r, F(BridgeKitDef.RwgEndName), F(rTile));
            r.End = BridgeKitDef.RwgEndName; r.Tile = rTile;
            r.Mod = ModOf(F(BridgeKitDef.RwgEndName));
            res.Add(r);
        }
        // 3. the rebuilt wooden bridge of YAMEE_Bridges (values and check boxes as saved, the user may change them)
        if (F(BridgeKitDef.WoodName) != null)
        {
            var wd = saved.FirstOrDefault(k => k.Id == BridgeKitDef.WoodId) ?? BridgeKitDef.YameeWood(BridgeAnalyzer.TerrainColumns(F(BridgeKitDef.WoodName), 9));
            wd.End = BridgeKitDef.WoodName; wd.Tile = null;
            wd.RoadInset ??= 1;   // the road of bridge_wood1 is 1 m narrower than the piece on each side (the user 2026-11-24)
            wd.Mod = ModOf(F(BridgeKitDef.WoodName));
            res.Add(wd);
        }
        // 3b. the one-piece bridges of the game rebuilt in YAMEE_Bridges (tools/make_game_bridge): analysed once, then values and check boxes as saved;
        // on at first (the user 2026-11-25), wooden bridges for gravel roads, the broken bridge only on request (roads end at its gap)
        foreach (var (game, label, gravel, broken) in BridgeKitDef.Rebuilt)
        {
            string piece = "yamee_" + game;
            if (F(piece) == null) continue;
            // the values always come from the blocks (a rebuilt piece takes effect at once); from bridges.json only the check boxes and the road width
            var saved1 = saved.FirstOrDefault(k => k.Id == piece);
            var rb = BridgeAnalyzer.Analyze(new BridgeKitDef { Id = piece, Label = Loc.T(label), End = piece, Tile = null, Asphalt = !gravel, Gravel = gravel || broken }, F(piece), null);
            if (broken) rb.Notes = Loc.T("Broken in the middle: the road ends at the gap (for lost places).") + " " + rb.Notes;
            if (saved1 != null)
            {
                rb.Active = saved1.Active; rb.Asphalt = saved1.Asphalt; rb.Gravel = saved1.Gravel; rb.Tested = saved1.Tested;
                if (saved1.RoadInset != null) rb.RoadInset = saved1.RoadInset;
            }
            else rb.Active = !broken;
            rb.End = piece; rb.Tile = null;
            rb.Mod = ModOf(F(piece));
            res.Add(rb);
        }
        // 4. kits added by hand (also when their mod is gone: then marked as not found)
        foreach (var k in saved.Where(k => !k.Builtin)) res.Add(k);
        // pieces with YOffset 0 copy no air: the river bed may lie as deep as the lowest point of the piece (the user 2026-11-25 after
        // Brueckentest50: "the water may be as deep as the lowest prefab point"); with YOffset −1 layer 0 is copied air, bed 1.5 m above it
        foreach (var k in res)
            k.BedAbove = BridgeAnalyzer.NoAirCopied(F(k.End)) && (k.Single || BridgeAnalyzer.NoAirCopied(F(k.Tile))) ? 0 : 1.5;
        foreach (var k in res) Resolve(k, files, describe);
        BridgeKits.RememberPieces(res);
        return res;
    }

    /// width of the road of an older saved kit (bridges.json without RoadInset): from the blocks
    static int InsetOf(BridgeKitDef k, string endXml, string tileXml) => BridgeAnalyzer.Analyze(k, endXml, tileXml).RoadInset ?? 0;

    /// pieces and preview pictures of a kit from the prefab files of the game
    public static void Resolve(BridgeKitDef k, IReadOnlyDictionary<string, string> files, Func<string, PrefabDef> describe)
    {
        string F(string n) => string.IsNullOrEmpty(n) ? null : files?.GetValueOrDefault(n);
        k.EndDef = F(k.End) != null ? describe(k.End) : null;
        k.TileDef = !k.Single && F(k.Tile) != null ? describe(k.Tile) : null;
        k.EndImage = F(k.End) != null ? Path.ChangeExtension(F(k.End), ".jpg") : null;
        k.TileImage = !k.Single && F(k.Tile) != null ? Path.ChangeExtension(F(k.Tile), ".jpg") : null;
    }
}
