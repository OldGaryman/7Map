using System.Windows;

namespace MapEditor.Map;

public sealed record EraseStats(int Pixels, int HeightPoints, double Roughness);

/// Radiergummi (Wunsch des Nutzers): Belag unter dem Pinselstrich löschen und nur dort, wo Belag war (plus ein schmaler Rand
/// daneben), den Boden natürlich machen – Laplace-Füllung aus dem Gelände ringsum und leichte Unebenheit wie beim Entfernen.
/// Gelände ohne Belag bleibt unverändert; in Stadt-Kacheln wird nur unser Belag aus splat3.png entfernt, die Höhen bleiben. Die Änderung landet zuerst nur im Speicher (WorldEdits).
public static class Eraser
{
    /// größte Rasterfläche eines Strichs (16 Mio. Zellen = 4.000 × 4.000 m, ca. 400 MB)
    const long MaxCells = 16_000_000;

    /// square = eckiger Pinsel (Quadrat mit Kantenlänge 2 · radius, an den Achsen ausgerichtet), sonst rund
    /// bumps = Stufe der Unebenheit (0 = keine, 1 = leicht, 2 = mittel, 3 = stark)
    public static EraseStats Erase(WorldData w, WorldEdits e, IReadOnlyList<Point> stroke, double radius, double rim, bool natural, bool square = false, double bumps = 1)
    {
        if (stroke.Count == 0) return new EraseStats(0, 0, 0);
        int margin = (int)Math.Ceiling(radius) + 13;   // 8 m Messstreifen für die Unebenheit + 5 m
        int gx0 = (int)Math.Floor(stroke.Min(p => p.X)) - margin, gz0 = (int)Math.Floor(stroke.Min(p => p.Y)) - margin;
        int gw = (int)Math.Ceiling(stroke.Max(p => p.X)) + margin - gx0, gh = (int)Math.Ceiling(stroke.Max(p => p.Y)) + margin - gz0;
        // Das Raster umfasst den ganzen Strich samt Rand; es braucht ca. 25 Byte je Zelle. Ein sehr langer, schräger Strich
        // würde Gigabyte belegen und das Programm abstürzen lassen (Prüfbericht F15): lieber in kleineren Strichen radieren.
        if ((long)gw * gh > MaxCells) throw new InvalidOperationException(Loc.T("The stroke covers too large an area – please erase in smaller strokes."));
        int n = gw * gh;
        var tiles = w.Prefabs.Where(p => p.IsTile && p.X < gx0 + gw && p.X + p.SizeX > gx0 && p.Z < gz0 + gh && p.Z + p.SizeZ > gz0).ToList();
        bool InTile(int x, int z) => tiles.Any(t => t.Contains(x + 0.5, z + 0.5));

        // Zellen unter dem Pinsel: Abstand zum Strich (Linie durch die Mauspositionen) höchstens radius
        var brush = new bool[n];
        int r = (int)Math.Ceiling(radius) + 1;
        if (square)
        {
            // Quadrat an jeder Stelle des Strichs (alle 0,5 m)
            for (int k = 0; k < stroke.Count; k++)
            {
                var a = stroke[k];
                var b = k + 1 < stroke.Count ? stroke[k + 1] : a;
                int samples = Math.Max(1, (int)Math.Ceiling((b - a).Length / 0.5));
                for (int q = 0; q <= samples; q++)
                {
                    double cx = a.X + (b.X - a.X) * q / samples, cz = a.Y + (b.Y - a.Y) * q / samples;
                    int x0 = Math.Max(0, (int)Math.Ceiling(cx - radius - 0.5) - gx0), x1 = Math.Min(gw - 1, (int)Math.Floor(cx + radius - 0.5) - gx0);
                    int z0 = Math.Max(0, (int)Math.Ceiling(cz - radius - 0.5) - gz0), z1 = Math.Min(gh - 1, (int)Math.Floor(cz + radius - 0.5) - gz0);
                    for (int gz = z0; gz <= z1; gz++)
                        for (int gx = x0; gx <= x1; gx++) brush[gz * gw + gx] = true;
                }
            }
        }
        else
        for (int k = 0; k < stroke.Count; k++)
        {
            var a = stroke[k];
            var b = k + 1 < stroke.Count ? stroke[k + 1] : a;
            int x0 = (int)Math.Floor(Math.Min(a.X, b.X)) - r - gx0, x1 = (int)Math.Ceiling(Math.Max(a.X, b.X)) + r - gx0;
            int z0 = (int)Math.Floor(Math.Min(a.Y, b.Y)) - r - gz0, z1 = (int)Math.Ceiling(Math.Max(a.Y, b.Y)) + r - gz0;
            double dx = b.X - a.X, dz = b.Y - a.Y, len2 = dx * dx + dz * dz;
            for (int gz = Math.Max(z0, 0); gz <= Math.Min(z1, gh - 1); gz++)
                for (int gx = Math.Max(x0, 0); gx <= Math.Min(x1, gw - 1); gx++)
                {
                    double px = gx0 + gx + 0.5, pz = gz0 + gz + 0.5;
                    double t = len2 > 0 ? Math.Clamp(((px - a.X) * dx + (pz - a.Y) * dz) / len2, 0, 1) : 0;
                    double ex = px - a.X - t * dx, ez = pz - a.Y - t * dz;
                    if (ex * ex + ez * ez <= radius * radius) brush[gz * gw + gx] = true;
                }
        }

        // Belag unter dem Pinsel (splat3.png oder Straßenmaske). In Kacheln nur unser eigener Belag aus splat3.png: die Straßen
        // der Kachel erzeugt das Spiel selbst (splat3_processed.png), und die Höhen gibt dort das Gebäude vor (usable = false)
        var road = new bool[n];
        var usable = new bool[n];
        int roadCount = 0;
        for (int g = 0; g < n; g++)
        {
            if (!brush[g]) continue;
            int x = gx0 + g % gw, z = gz0 + g / gw;
            if (!w.Contains(x, z)) continue;
            bool own = (e.SplatAt(x, z) & 0x00FFFF00) != 0;
            if (InTile(x, z))
            {
                if (own) { road[g] = true; roadCount++; }
                continue;
            }
            usable[g] = true;
            if (own || w.RoadMask[w.Index(x, z)] != 0) { road[g] = true; roadCount++; }
        }
        if (roadCount == 0) return new EraseStats(0, 0, 0);

        int changed = 0;
        double amp = 0;
        if (natural)
        {
            // frei = Belag und der Rand daneben (höchstens rim Meter), nur unter dem Pinsel; Füllung und Unebenheit wie beim
            // Löschen von Gebäuden (GroundShaper)
            var dRoad = GroundShaper.Distance(road, gw, gh);
            var free = new bool[n];
            for (int g = 0; g < n; g++)
            {
                int gx = g % gw, gz = g / gw;
                if (usable[g] && dRoad[g] <= rim && gx > 0 && gz > 0 && gx < gw - 1 && gz < gh - 1) free[g] = true;
            }
            (changed, amp) = GroundShaper.FillNatural(w, e, gx0, gz0, gw, gh, free, bumps, InTile);
        }

        for (int g = 0; g < n; g++)
        {
            if (!road[g]) continue;
            int x = gx0 + g % gw, z = gz0 + g / gw;
            e.SetSplat(x, z, WorldEdits.EmptyPixel);
            e.SetRoad(w.Index(x, z), 0);
        }
        return new EraseStats(roadCount, changed, amp);
    }
}
