using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MapEditor.Map;

/// Asphalt and gravel of a building from its own blocks (the user's request, 2026-10-30: see the surface already in the preview
/// and after rotating, not only after the next game test). Per column the topmost terrain block counts ("terr…"):
/// terrAsphalt = asphalt, terrGravel = gravel. Checked on the original world (metest `surf`, 3,819 buildings/parts):
/// 99.4 % of the area calculated this way the game has exactly like that in splat3_processed.png, 98.0 % of its area is hit.
/// Files: .blocks.nim (name list) and .tts (14-byte header, size x/y/z as int16 from byte 8, then uint32 per block,
/// type = value & 0x7FFF, index = x + y·X + z·X·Y).
public static class PrefabSurface
{
    /// surface per column, unrotated (index lx + lz·SizeX): 0 = nothing, 1 = gravel, 2 = asphalt; null = no block file or no surface
    public static byte[] Read(string xmlFile, out int sizeX, out int sizeZ)
    {
        sizeX = sizeZ = 0;
        string tts = Path.ChangeExtension(xmlFile, ".tts"), nim = Path.ChangeExtension(xmlFile, null) + ".blocks.nim";
        if (!File.Exists(tts) || !File.Exists(nim)) return null;
        var kinds = new Dictionary<int, (bool Terrain, byte Kind)>();
        using (var br = new BinaryReader(File.OpenRead(nim)))
        {
            br.ReadInt32();
            int n = br.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int id = br.ReadInt32();
                string name = Encoding.ASCII.GetString(br.ReadBytes(br.ReadByte()));
                if (name.StartsWith("terr", StringComparison.Ordinal))
                    kinds[id] = (true, name == "terrAsphalt" ? WorldData.Asphalt : name == "terrGravel" ? WorldData.Gravel : (byte)0);
            }
        }
        var t = File.ReadAllBytes(tts);
        int sx = BitConverter.ToInt16(t, 8), sy = BitConverter.ToInt16(t, 10), sz = BitConverter.ToInt16(t, 12);
        if (sx <= 0 || sy <= 0 || sz <= 0 || t.Length < 14 + 4L * sx * sy * sz) return null;
        var grid = new byte[sx * sz];
        bool any = false;
        for (int lz = 0; lz < sz; lz++)
            for (int lx = 0; lx < sx; lx++)
                for (int y = sy - 1; y >= 0; y--)
                {
                    int id = (int)(BitConverter.ToUInt32(t, 14 + 4 * (lx + y * sx + lz * sx * sy)) & 0x7FFF);
                    if (!kinds.TryGetValue(id, out var k)) continue;
                    grid[lx + lz * sx] = k.Kind;
                    any |= k.Kind != 0;
                    break;
                }
        sizeX = sx; sizeZ = sz;
        return any ? grid : null;
    }

    /// Connections from the building's own surface (the user's request, 2026-11-03: oldwest_business_02 has neither RoadExit nor
    /// driveway part, but a 3 m wide gravel path in the building): places where narrow surface (2–12 m) reaches the edge of the footprint
    /// and continues inwards like a path for at least 3 m (width at most length + 2). On the original world (1,738 buildings)
    /// these places cover 1,032 of the existing snap points; of 654 new ones, 70 % have a road 1.5–3 m in front of them (77 % up to 7 m).
    /// result unrotated: point at the edge (LX, LZ), width in m, surface (1 gravel, 2 asphalt).
    public static List<(double LX, double LZ, int Width, byte Kind)> EdgeExits(byte[] grid, int sx, int sz)
    {
        var list = new List<(double, double, int, byte)>();
        for (int side = 0; side < 4; side++)
        {
            int n = side < 2 ? sx : sz;
            byte At(int i, int d) => side switch { 0 => grid[i + d * sx], 1 => grid[i + (sz - 1 - d) * sx], 2 => grid[d + i * sx], _ => grid[(sx - 1 - d) + i * sx] };
            int a = 0;
            while (a < n)
            {
                if (At(a, 0) == 0) { a++; continue; }
                int b = a;
                while (b + 1 < n && At(b + 1, 0) != 0) b++;
                int ra = a, rb = b, len = b - a + 1;
                a = b + 1;
                if (len < 2 || len > 12) continue;
                // corridor: in the next meters the connected surface around the center stays at most length + 2 wide
                bool ok = true;
                int m = (ra + rb) / 2;
                for (int d = 1; d < 3 && ok; d++)
                {
                    if (At(m, d) == 0 && At(ra, d) == 0 && At(rb, d) == 0) { ok = false; break; }
                    int lo = m, hi = m;
                    if (At(m, d) == 0) lo = hi = At(ra, d) != 0 ? ra : rb;
                    while (lo > 0 && At(lo - 1, d) != 0) lo--;
                    while (hi < n - 1 && At(hi + 1, d) != 0) hi++;
                    if (hi - lo + 1 > len + 2) ok = false;
                }
                if (!ok) continue;
                int asphalt = 0;
                for (int i = ra; i <= rb; i++) if (At(i, 0) == WorldData.Asphalt) asphalt++;
                double mid = (ra + rb + 1) / 2.0;
                var (lx, lz) = side switch { 0 => (mid, 0.0), 1 => (mid, (double)sz), 2 => (0.0, mid), _ => ((double)sx, mid) };
                list.Add((lx, lz, len, asphalt * 2 >= len ? WorldData.Asphalt : WorldData.Gravel));
            }
        }
        return list;
    }

    /// image of the surface in world orientation (1 pixel = 1 m, row 0 = north) for the rotation; colors like the roads of the map
    public static BitmapSource Image(byte[] grid, int sx, int sz, int rotation)
    {
        bool turned = rotation % 2 == 1;
        int w = turned ? sz : sx, h = turned ? sx : sz;
        var px = new byte[w * h * 4];
        for (int lz = 0; lz < sz; lz++)
            for (int lx = 0; lx < sx; lx++)
            {
                byte k = grid[lx + lz * sx];
                if (k == 0) continue;
                var (wx, wz) = PlacedPrefab.Rotate(lx + 0.5, lz + 0.5, rotation, sx, sz);
                int x = (int)Math.Floor(wx), y = h - 1 - (int)Math.Floor(wz);
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                int at = (x + y * w) * 4;
                if (k == WorldData.Asphalt) { px[at] = 62; px[at + 1] = 58; px[at + 2] = 58; }
                else { px[at] = 82; px[at + 1] = 128; px[at + 2] = 166; }
                px[at + 3] = 255;
            }
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }
}
