namespace MapEditor.Map;

/// Trägt eine geplante Straße in die Welt ein (wie tools/road.ps1 -Apply): Gelände angleichen, Belag malen – zunächst nur im
/// Speicher (WorldEdits), in die Dateien erst mit „Speichern“.
public static class RoadApplier
{
    public sealed record Stats(int HeightPoints, int Pixels);

    /// from/to: nur der Teil der Straße mit Wegpunkten from ≤ k &lt; to (to &lt; 0 = bis zum Ende) – lange Straßen stückweise
    /// eintragen (Straßen zwischen Städten, mehrere km: das Raster über die ganze Straße bräuchte Hunderte MB); die Abstände
    /// werden dafür auch aus den Wegpunkten davor/danach gerechnet, damit Pixel an der Grenze den richtigen Wegpunkt bekommen.
    /// stride: nur jeder n-te Wegpunkt zählt für die Abstände (Wegpunkte liegen alle 0,25 m; 4 = 1 m reicht für lange Straßen)
    public static Stats Apply(WorldData w, WorldEdits e, RoadResult r, IProgress<string> progress, int from = 0, int to = -1, int stride = 1)
    {
        var s = r.Settings;
        var path = r.Path;
        double maxHalf = s.Width / 2, reach = maxHalf + s.Shoulder + s.GrassEdge + RoadPlanner.MaxSlopeWidth + 2;   // Settings.Width = größte Breite
        if (to < 0 || to > path.Count) to = path.Count;
        from = Math.Clamp(from, 0, path.Count);
        if (from >= to) return new Stats(0, 0);
        // Wegpunkte, die für die Abstände zählen: der Teil selbst und 2 × reach davor und danach
        int k0 = from, k1 = to - 1;
        while (k0 > 0 && path[from].Dist - path[k0 - 1].Dist <= 2 * reach) k0--;
        while (k1 < path.Count - 1 && path[k1 + 1].Dist - path[to - 1].Dist <= 2 * reach) k1++;
        // immer dieselben Wegpunkte (Vielfache von stride, dazu der letzte), sonst bekommt ein Pixel an der Grenze zweier Stücke
        // in beiden Stücken einen Wegpunkt des jeweils anderen und wird von keinem bearbeitet (1 m breiter Streifen, Stufe bis 10 m)
        stride = Math.Max(1, stride);
        var ks = new List<int>();
        for (int k = k0 - k0 % stride; k <= k1; k += stride) ks.Add(k);
        if (k1 == path.Count - 1 && ks[^1] != k1) ks.Add(k1);

        // Raster um die Straße: kleinster Abstand zur Mittellinie und nächster Wegpunkt je Meter
        progress.Report(Loc.T("Calculating distances to the road …"));
        int gx0 = (int)Math.Floor(ks.Min(k => path[k].X) - reach) - 1, gz0 = (int)Math.Floor(ks.Min(k => path[k].Z) - reach) - 1;
        int gw = (int)Math.Ceiling(ks.Max(k => path[k].X) + reach) + 2 - gx0, gh = (int)Math.Ceiling(ks.Max(k => path[k].Z) + reach) + 2 - gz0;
        var gd = new float[gw * gh];
        var gi = new int[gw * gh];
        Array.Fill(gd, float.MaxValue);
        Array.Fill(gi, -1);
        int rad = (int)Math.Ceiling(reach) + 1;
        foreach (int k in ks)
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

        float[] fill = r.IsRemoval && s.Reshape ? NaturalFill(w, r, gx0, gz0, gw, gh, gd, gi, maxHalf, progress) : null;

        progress.Report(r.IsRemoval ? Loc.T("Removing surface and blending terrain …") : Loc.T("Blending terrain and painting surface …"));
        bool asphalt = s.Surface == Surface.Asphalt;
        int changed = 0, painted = 0;
        // Bestehende Straßen schützen (28.10.2026): Böschung und Ende einer neuen Straße verbiegen deren Fläche nicht mehr
        // (vorher Knicke von 13–15 % neben einer Einmündung); die Höhe dort legt die Anpassung an der Kreuzung fest
        bool protectRoads = s.PaintSurface && !r.IsRemoval;
        int pn = path.Count;
        double sx = path[Math.Min(4, pn - 1)].X - path[0].X, sz = path[Math.Min(4, pn - 1)].Z - path[0].Z;
        double tex = path[pn - 1].X - path[Math.Max(0, pn - 5)].X, tez = path[pn - 1].Z - path[Math.Max(0, pn - 5)].Z;
        double sl = Math.Max(1e-6, Math.Sqrt(sx * sx + sz * sz)), el = Math.Max(1e-6, Math.Sqrt(tex * tex + tez * tez));
        // hinter dem Ende bzw. vor dem Anfang (mehr als 0,5 m in Fahrtrichtung über den Endpunkt hinaus)
        bool Beyond(int k, int x, int z) =>
            k == pn - 1 && ((x + 0.5 - path[k].X) * tex + (z + 0.5 - path[k].Z) * tez) / el > 0.5 ||
            k == 0 && ((x + 0.5 - path[0].X) * sx + (z + 0.5 - path[0].Z) * sz) / sl < -0.5;
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx, k = gi[g];
                if (k < from || k >= to) continue;   // auch k < 0 (kein Wegpunkt in der Nähe)
                double d = gd[g], bl = r.SlopeWidth[k];
                double half = r.WidthAt(k) / 2, paintEdge = half + s.Shoulder, edge = paintEdge + s.GrassEdge;   // Rasenkante: nur Gelände, kein Belag; Breite kann sich entlang der Straße ändern (Verjüngen)
                if (d > edge + bl) continue;
                int x = gx0 + gx, z = gz0 + gz;
                if (!w.Contains(x, z)) continue;
                int hi = w.Index(x, z);
                // Stadt-Kacheln: Höhen gibt das Gebäude vor, die eigenen Straßen der Kachel erzeugt das Spiel (splat3_processed).
                // Nur unseren Belag in splat3.png malen bzw. entfernen; er zeigt sich überall, wo die Kachel nichts hinstellt.
                if (r.InTile(x + 0.5, z + 0.5))
                {
                    if (d > half || !s.PaintSurface) continue;
                    uint own = e.SplatAt(x, z);
                    if (r.IsRemoval)
                    {
                        if ((own & 0x00FFFF00) != 0) { e.SetSplat(x, z, WorldEdits.EmptyPixel); e.SetRoad(hi, 0); painted++; }
                    }
                    else if ((own & 0x00FFFF00) != 0 || w.RoadMask[hi] == 0)   // Straße der Kachel nicht übermalen
                    {
                        if (asphalt) { e.SetSplat(x, z, WorldEdits.AsphaltPixel); e.SetRoad(hi, WorldData.Asphalt); painted++; }
                        else if ((own >> 16 & 0xFF) == 0) { e.SetSplat(x, z, WorldEdits.GravelPixel); e.SetRoad(hi, WorldData.Gravel); painted++; }
                    }
                    continue;
                }
                double o = w.Heights[hi];
                double u = d <= edge ? 1 : 1 - (d - edge) / bl;
                u = Math.Clamp(u, 0, 1);
                u = u * u * (3 - 2 * u);

                if (r.IsRemoval)
                {
                    // erhaltene Kreuzungen: Weg bleibt, wie er ist; daneben weicher Übergang über die Böschungsbreite
                    // die Füllung schließt am Rand des Bereichs schon nahtlos an: kein zusätzlicher Übergang (gäbe eine Rinne)
                    if (fill != null) u = 1;
                    double kept = r.KeptRoadDistance(x + 0.5, z + 0.5, out byte keptKind);
                    if (kept <= 0)
                    {
                        // Belag des kreuzenden Wegs durchgehend (vorher lag dort die entfernte Straße darüber)
                        if (d <= half)
                        {
                            e.SetSplat(x, z, keptKind == WorldData.Asphalt ? WorldEdits.AsphaltPixel : WorldEdits.GravelPixel);
                            e.SetRoad(hi, keptKind);
                        }
                        continue;
                    }
                    if (kept < bl) { double v = kept / bl; u *= v * v * (3 - 2 * v); }
                    if (fill != null)
                    {
                        var hv = (ushort)Math.Clamp(Math.Round(o + (fill[g] * 256 - o) * u), 0, 65535);
                        if (hv != w.Heights[hi]) { e.SetHeight(hi, hv); changed++; }
                    }
                    // Belag löschen (leerer Pixel = alles 0)
                    if (d <= half && e.SplatAt(x, z) != WorldEdits.EmptyPixel)
                    {
                        e.SetSplat(x, z, WorldEdits.EmptyPixel);
                        e.SetRoad(hi, 0);
                        painted++;
                    }
                    continue;
                }

                // Gelände: auf Fahrbahn und Rand genau die Straßenhöhe, in der Böschung weich zum Gelände (Smoothstep)
                double target = r.Profile[k] * 256;
                // Querneigung an Kreuzungen: seitlicher Abstand zur Mitte (höchstens bis zum Rand) mal Neigung
                if (r.CrossSlope != null && r.CrossSlope[k] != 0)
                {
                    var (nx, nz) = RoadPlanner.Normal(path, k);
                    double side = Math.Clamp((x + 0.5 - path[k].X) * nx + (z + 0.5 - path[k].Z) * nz, -edge, edge);
                    target += r.CrossSlope[k] * side * 256;
                }
                var nv = (ushort)Math.Clamp(Math.Round(o + (target - o) * u), 0, 65535);
                // Umbau einer bestehenden Straße (nur Höhen): hinter seinen Enden geht die Straße weiter – dort nichts ändern
                // (28.10.2026: die runde Endkappe legte je 12 m der Straße waagerecht, mit Kanten von 13–16 %)
                if (!s.PaintSurface && Beyond(k, x, z)) continue;
                bool inZone = false;
                foreach (var (zc, zr) in r.ProtectZones)
                    if ((x + 0.5 - zc.X) * (x + 0.5 - zc.X) + (z + 0.5 - zc.Y) * (z + 0.5 - zc.Y) <= zr * zr) { inZone = true; break; }
                bool keep = protectRoads && w.RoadMask[hi] != 0 && (d > edge || Beyond(k, x, z) || inZone);
                if (!keep && nv != w.Heights[hi]) { e.SetHeight(hi, nv); changed++; }
                if (!s.PaintSurface) continue;   // Umbau an einer Kreuzung: nur Höhen

                // Belag (BGRA): Asphalt A255 R255 G0 B0, Schotter A255 R0 G255 B0; Schotter übermalt keinen Asphalt
                if (d <= half)
                {
                    uint px = e.SplatAt(x, z);
                    if (asphalt) { e.SetSplat(x, z, WorldEdits.AsphaltPixel); e.SetRoad(hi, WorldData.Asphalt); painted++; }
                    else if ((px >> 16 & 0xFF) == 0) { e.SetSplat(x, z, WorldEdits.GravelPixel); e.SetRoad(hi, Math.Max(w.RoadMask[hi], WorldData.Gravel)); painted++; }
                }
                else if (d <= paintEdge && (e.SplatAt(x, z) & 0x00FFFF00) == 0)
                {
                    e.SetSplat(x, z, WorldEdits.GravelPixel);
                    e.SetRoad(hi, Math.Max(w.RoadMask[hi], WorldData.Gravel));
                    painted++;
                }
            }
        return new Stats(changed, painted);
    }

    // Entfernen: altes Straßenbett so auffüllen, wie das Gelände ringsum es vorgibt (Laplace-Füllung: jeder Punkt = Mittel
    // seiner Nachbarn, Ränder und erhaltene Kreuzungen fest – füllt Einschnitte, trägt Dämme ab), dann leichte Unebenheit
    // in der Stärke des Geländes daneben. Ergebnis in Metern je Rasterzelle (nur im Bereich gültig).
    static float[] NaturalFill(WorldData w, RoadResult r, int gx0, int gz0, int gw, int gh, float[] gd, int[] gi, double half, IProgress<string> progress)
    {
        progress.Report(Loc.T("Filling the road bed naturally …"));
        int n = gw * gh;
        var h = new float[n];
        var free = new bool[n];
        int count = 0;
        for (int gz = 0; gz < gh; gz++)
            for (int gx = 0; gx < gw; gx++)
            {
                int g = gz * gw + gx, x = gx0 + gx, z = gz0 + gz;
                h[g] = (float)w.HeightAt(x, z);
                int k = gi[g];
                if (k < 0 || gx == 0 || gz == 0 || gx == gw - 1 || gz == gh - 1 || !w.Contains(x, z)) continue;
                if (gd[g] >= half + r.SlopeWidth[k] || r.InTile(x + 0.5, z + 0.5) || r.KeptRoadDistance(x + 0.5, z + 0.5) <= 0) continue;
                free[g] = true;
                count++;
                // Startwert: geradlinig quer zwischen beiden Rändern (beschleunigt das Rechnen)
                var (nx, nz) = RoadPlanner.Normal(r.Path, k);
                double side = (x + 0.5 - r.Path[k].X) * nx + (z + 0.5 - r.Path[k].Z) * nz, reach = half + r.SlopeWidth[k];
                double t = Math.Clamp((side + reach) / (2 * reach), 0, 1);
                h[g] = (float)(r.SideRight[k] * (1 - t) + r.SideLeft[k] * t);
            }
        if (count == 0) return h;
        var cells = new int[count];
        for (int g = 0, c = 0; g < n; g++) if (free[g]) cells[c++] = g;
        for (int it = 0; it < 600; it++)
        {
            float maxChange = 0;
            foreach (int g in cells)
            {
                float v = (h[g - 1] + h[g + 1] + h[g - gw] + h[g + gw]) / 4;
                float nv = h[g] + 1.85f * (v - h[g]);
                maxChange = Math.Max(maxChange, Math.Abs(nv - h[g]));
                h[g] = nv;
            }
            if (maxChange < 0.002f) break;
        }

        // Unebenheit des Geländes daneben: Abweichung vom Mittel der Punkte 3 m ringsum, gemessen in einem 8 m breiten
        // Streifen außerhalb des Bereichs
        double sum = 0;
        int m = 0;
        for (int gz = 3; gz < gh - 3; gz++)
            for (int gx = 3; gx < gw - 3; gx++)
            {
                int g = gz * gw + gx, k = gi[g];
                if (k < 0 || free[g]) continue;
                double edge = half + r.SlopeWidth[k];
                if (gd[g] < edge + 1 || gd[g] > edge + 8 || r.InTile(gx0 + gx + 0.5, gz0 + gz + 0.5)) continue;
                double avg = (h[g - 3] + h[g + 3] + h[g - 3 * gw] + h[g + 3 * gw]) / 4;
                sum += (h[g] - avg) * (h[g] - avg);
                m++;
            }
        double amp = Math.Clamp(m > 0 ? Math.Sqrt(sum / m) * 2 : 0.2, 0.1, 0.6);
        foreach (int g in cells)
        {
            int x = gx0 + g % gw, z = gz0 + g / gw;
            double bl = r.SlopeWidth[gi[g]], v = Math.Clamp((half + bl - gd[g]) / bl, 0, 1);   // zum Rand hin ausblenden
            h[g] += (float)(amp * v * v * (3 - 2 * v) * Noise(x, z));
        }
        return h;
    }

    // Wertrauschen in drei Stufen (16, 7 und 3 m), ungefähr -1 … 1, für jeden Ort immer gleich
    internal static double Noise(int x, int z) => 0.6 * Octave(x, z, 16, 11) + 0.3 * Octave(x, z, 7, 23) + 0.15 * Octave(x, z, 3, 37);

    internal static double Octave(int x, int z, double size, int seed)
    {
        double fx = x / size, fz = z / size;
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        double tx = fx - ix, tz = fz - iz;
        tx = tx * tx * (3 - 2 * tx); tz = tz * tz * (3 - 2 * tz);
        double a = Hash(ix, iz, seed), b = Hash(ix + 1, iz, seed), c = Hash(ix, iz + 1, seed), d = Hash(ix + 1, iz + 1, seed);
        return (a * (1 - tx) + b * tx) * (1 - tz) + (c * (1 - tx) + d * tx) * tz;
    }

    static double Hash(int x, int z, int seed)
    {
        unchecked
        {
            uint hsh = (uint)(x * 374761393 + z * 668265263 + seed * 144269);
            hsh = (hsh ^ (hsh >> 13)) * 1274126177;
            hsh ^= hsh >> 16;
            return hsh / (double)uint.MaxValue * 2 - 1;
        }
    }
}
