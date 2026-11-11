using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MapEditor.Map;

/// Asphalt und Schotter eines Gebäudes aus seinen eigenen Blöcken (Wunsch des Nutzers 30.10.2026: Belag schon in der Vorschau
/// und nach dem Drehen sehen, nicht erst nach dem nächsten Spieltest). Je Spalte zählt der oberste Geländeblock („terr…“):
/// terrAsphalt = Asphalt, terrGravel = Schotter. An der Originalwelt geprüft (metest `surf`, 3.819 Gebäude/Teile):
/// 99,4 % der so berechneten Fläche hat das Spiel genauso in splat3_processed.png, 98,0 % seiner Fläche wird getroffen.
/// Dateien: .blocks.nim (Namensliste) und .tts (14 Byte Kopf, Größe x/y/z als int16 ab Byte 8, dann uint32 je Block,
/// Typ = Wert & 0x7FFF, Index = x + y·X + z·X·Y).
public static class PrefabSurface
{
    /// Belag je Spalte, ungedreht (Index lx + lz·SizeX): 0 = nichts, 1 = Schotter, 2 = Asphalt; null = keine Blockdatei oder kein Belag
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

    /// Anschlüsse aus dem eigenen Belag eines Gebäudes (Wunsch des Nutzers 3.11.2026: oldwest_business_02 hat weder RoadExit noch
    /// Zufahrtsteil, aber einen 3 m breiten Schotterweg im Gebäude): Stellen, an denen schmaler Belag (2–12 m) den Rand der Grundfläche
    /// erreicht und wie ein Weg mindestens 3 m nach innen weiterläuft (Breite höchstens Länge + 2). An der Originalwelt (1.738 Gebäude)
    /// decken diese Stellen 1.032 der vorhandenen Fangpunkte ab; von 654 neuen hat bei 70 % eine Straße 1,5–3 m davor (77 % bis 7 m).
    /// Ergebnis ungedreht: Punkt am Rand (LX, LZ), Breite in m, Belag (1 Schotter, 2 Asphalt).
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
                // Korridor: in den nächsten Metern bleibt der zusammenhängende Belag um die Mitte höchstens Länge + 2 breit
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

    /// Bild des Belags in Welt-Ausrichtung (1 Pixel = 1 m, Zeile 0 = Norden) für die Drehung; Farben wie die Straßen der Karte
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
