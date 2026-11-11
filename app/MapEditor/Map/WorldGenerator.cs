using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MapEditor.Map;

/// Einstellungen des Terraingenerators (Wunsch des Nutzers 6.11.2026: neue Welt ohne Städte, Straßen und POIs, wenig Wasser)
public sealed class GeneratorSpec
{
    public string Name = "New World";
    /// Kantenlänge in Metern (2048 … 10240, Vielfaches von 1024)
    public int Size = 8192;
    public string Seed = "YAMEE";
    /// Anteile der Biome in Prozent (Reihenfolge wie BiomePalette: Kiefernwald, Verbrannter Wald, Wüste, Schnee, Ödland);
    /// Standard = Werte des Spiels (Generation.Forest … in map_info.xml, an den Welten nachgemessen)
    public int[] Shares = { 18, 18, 21, 21, 22 };
    /// Faktoren 0 … 2 (1 = wie im Spiel gemessen)
    public double Mountains = 1.4, Hills = 1.3, Rough = 1.2, Valleys = 1.2, Erosion = 1.2;
    /// Spielordner (für die Höhenstempel in DataStamps); null = nur Rauschen
    public string GameFolder;
    /// 0 = keine Seen, 1 = wenige, 2 = einige, 3 = viele
    public int Lakes = 2;
    /// einzigartige Formationen: 0 = keine, 1 = wenige (ca. 1 je 12 km²), 2 = einige, 3 = viele
    public int Anomalies = 2;
    public int SpawnCount = 32;

    public int SeedValue
    {
        get
        {
            // stabil über Programmversionen (string.GetHashCode ist je Programmstart zufällig): FNV-1a
            uint h = 2166136261;
            foreach (char c in Seed ?? "") { h ^= c; h *= 16777619; }
            return (int)(h & 0x7FFFFFFF);
        }
    }

    public string Describe() => string.Format(CultureInfo.InvariantCulture,
        "Name={0}\r\nSize={1}\r\nSeed={2}\r\nShares={3}\r\nMountains={4:0.##}\r\nHills={5:0.##}\r\nRough={6:0.##}\r\nLakes={7}\r\nSpawnCount={8}\r\nValleys={9:0.##}\r\nErosion={10:0.##}\r\nStamps={11}\r\nAnomalies={12}\r\nGenerator=4\r\n",
        Name, Size, Seed, string.Join(",", Shares), Mountains, Hills, Rough, Lakes, SpawnCount, Valleys, Erosion, GameFolder != null, Anomalies);
}

/// Erzeugt eine ganze Welt (Höhen, Biome, Seen, Spawnpunkte) aus Rauschen – ohne Städte, Straßen und Gebäude.
/// Alles ist in Metern beschrieben, damit die kleine Vorschau (grobes Raster) dieselbe Landschaft zeigt wie die fertige Welt.
/// Ablauf: Biome wachsen von Keimpunkten auf ihren Anteil (Raster 32 m) → Geländeform je Biom (Grundhöhe, Hügel,
/// Gebirgsrücken, Tafelberge in der Wüste; Übergänge an den Biomgrenzen weich) auf einem 4-m-Raster → Hangrutschung
/// (Thermal-Erosion) → Seen (Becken + Wasserspiegel) → volle Auflösung (bikubisch + feines Rauschen) → Dateien.
public sealed partial class WorldGenerator
{
    // Werte je Biom (Index wie BiomePalette). Aus TERRAIN_ANALYSE.md: Gipfelrelief Kiefer 41–55 m, Brand 71–85,
    // Wüste 41–46 (Plateaus), Schnee 89–96, Ödland flach; Boden 41–49 m.
    // 7.11.2026 (Rückmeldung des Nutzers: „sehr brav“): Berge deutlich höher und häufiger, Täler, überall Wellen im Gelände
    static readonly double[] BaseOff = { 4, 2, 3, 10, 0 };
    static readonly double[] HillAmp = { 26, 22, 16, 28, 22 };   // 9.11.2026: Ödland hügeliger
    static readonly double[] RollAmp = { 7, 7, 6, 8, 10 };
    static readonly double[] MountAmp = { 95, 135, 45, 175, 40 };
    static readonly double[] MountCover = { 0.38, 0.48, 0.22, 0.62, 0.16 };
    static readonly double[] ValleyAmp = { 22, 26, 14, 30, 10 };
    static readonly double[] PlateauAmp = { 0, 0, 60, 0, 0 };   // 10.11.2026: Stand 0.73 (55) + 10 % (Wunsch des Nutzers)
    static readonly double[] RoughAmp = { 0.55, 0.7, 0.75, 0.7, 0.45 };   // Wüste schroffer

    const int BiomeCell = 32;   // Raster der Biom-Regionen in Metern
    const double GroundLevel = 40;

    readonly GeneratorSpec spec;
    readonly int size;
    readonly Random rnd;
    readonly Perlin cont, hills, mmask, ridge, plat, warpX, warpZ, cost, detail, peakVar, rolls, valley, crag;

    // Biom-Regionen (Raster BiomeCell, Zeile 0 = Süden) und weiche Gewichte für die Geländeform
    int nb;
    byte[] region;
    float[][] weight;

    // grobes Raster
    public int N { get; private set; }
    public double Cell { get; private set; }
    float[] coarse;
    int[] lakeOf;        // 0 = kein See, sonst Nummer + 1 (nur Zellen im Becken samt 8 m Rand)
    float[] calm;        // 0 am See … 1 weit weg: Dämpfung des feinen Rauschens
    public readonly List<LakeInfo> LakeList = new();

    public sealed record LakeInfo(double X, double Z, double Radius, int Level, double Depth);

    public WorldGenerator(GeneratorSpec spec)
    {
        this.spec = spec;
        size = spec.Size;
        int seed = spec.SeedValue;
        rnd = new Random(seed);
        cont = new Perlin(seed + 1); hills = new Perlin(seed + 2); mmask = new Perlin(seed + 3); ridge = new Perlin(seed + 4);
        plat = new Perlin(seed + 5); warpX = new Perlin(seed + 6); warpZ = new Perlin(seed + 7); cost = new Perlin(seed + 8);
        detail = new Perlin(seed + 9); peakVar = new Perlin(seed + 10); rolls = new Perlin(seed + 11); valley = new Perlin(seed + 12); crag = new Perlin(seed + 13);
    }

    // ---------------------------------------------------------------- Biome

    void GrowBiomes()
    {
        nb = size / BiomeCell;
        int n = nb * nb;
        region = new byte[n];
        var dist = new double[n];
        Array.Fill(dist, double.MaxValue);
        var owner = new sbyte[n];
        Array.Fill(owner, (sbyte)-1);
        int total = spec.Shares.Sum();
        if (total <= 0) total = 1;
        var quota = spec.Shares.Select(s => (long)Math.Round((double)s / total * n)).ToArray();
        var count = new long[5];
        var pq = new PriorityQueue<(int idx, int b), double>();
        var seeds = new List<(double x, double z)>();
        double areaKm2 = (double)size * size / 1e6;

        // Keimpunkte: Kiefernwald in der Mitte (dort spawnt man meistens), Schnee eher im Norden, Wüste eher im Süden
        var order = new[] { 0, 3, 2, 1, 4 };
        foreach (int b in order)
        {
            if (spec.Shares[b] <= 0) continue;
            int k = Math.Max(1, (int)Math.Round(spec.Shares[b] / (double)total * areaKm2 / 7));
            for (int s = 0; s < k; s++)
            {
                double bx = 0, bz = 0, minDist = size * 0.2;
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    if (b == 0 && s == 0) { bx = size / 2.0; bz = size / 2.0; break; }
                    double lo = b == 3 ? 0.55 : b == 2 ? 0.05 : 0.05, hi = b == 3 ? 0.95 : b == 2 ? 0.45 : 0.95;
                    bx = size * (0.05 + 0.9 * rnd.NextDouble());
                    bz = size * (lo + (hi - lo) * rnd.NextDouble());
                    if (seeds.All(p => Math.Abs(p.x - bx) + Math.Abs(p.z - bz) >= minDist)) break;
                    if (attempt % 40 == 39) minDist *= 0.7;
                }
                seeds.Add((bx, bz));
                int ix = Math.Clamp((int)(bx / BiomeCell), 0, nb - 1), iz = Math.Clamp((int)(bz / BiomeCell), 0, nb - 1);
                int idx = iz * nb + ix;
                if (dist[idx] > 0) { dist[idx] = 0; pq.Enqueue((idx, b), 0); }
            }
        }
        if (pq.Count == 0) { Array.Fill(region, (byte)0); return; }

        // Wachsen (Dijkstra mit verrauschten Kosten → unregelmäßige Grenzen), jedes Biom bis zu seinem Anteil
        var stepCost = new double[n];
        Parallel.For(0, nb, iz =>
        {
            for (int ix = 0; ix < nb; ix++)
            {
                double x = (ix + 0.5) * BiomeCell, z = (iz + 0.5) * BiomeCell;
                stepCost[iz * nb + ix] = 1 + 6 * Math.Clamp(0.5 + 1.5 * cost.Fbm(x / 600, z / 600, 4), 0, 1);
            }
        });
        var leftover = new List<(int idx, int b, double d)>();
        Grow(pq, dist, owner, stepCost, quota, count, leftover);
        // Rest (ein Biom war eingeschlossen oder Rundung): ohne Grenze weiterwachsen
        if (owner.Any(o => o < 0))
        {
            var pq2 = new PriorityQueue<(int idx, int b), double>();
            for (int i = 0; i < n; i++)
                if (owner[i] >= 0) pq2.Enqueue((i, owner[i]), 0);
            var noLimit = Enumerable.Repeat(long.MaxValue, 5).ToArray();
            Array.Fill(dist, double.MaxValue);
            for (int i = 0; i < n; i++) if (owner[i] >= 0) dist[i] = 0;
            var o2 = (sbyte[])owner.Clone();
            for (int i = 0; i < n; i++) if (owner[i] >= 0) o2[i] = -1;
            Grow(pq2, dist, o2, stepCost, noLimit, new long[5], leftover);
            for (int i = 0; i < n; i++) if (owner[i] < 0) owner[i] = o2[i];
        }
        for (int i = 0; i < n; i++) region[i] = (byte)Math.Max((sbyte)0, owner[i]);

        // Grenzen glätten (Mehrheit 3 × 3, zweimal)
        for (int pass = 0; pass < 2; pass++)
        {
            var copy = (byte[])region.Clone();
            Parallel.For(0, nb, iz =>
            {
                Span<int> votes = stackalloc int[5];
                for (int ix = 0; ix < nb; ix++)
                {
                    votes.Clear();
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = Math.Clamp(ix + dx, 0, nb - 1), z = Math.Clamp(iz + dz, 0, nb - 1);
                            votes[copy[z * nb + x]]++;
                        }
                    int best = copy[iz * nb + ix];
                    for (int b = 0; b < 5; b++) if (votes[b] > votes[best]) best = b;
                    if (votes[best] >= 5) region[iz * nb + ix] = (byte)best;
                }
            });
        }

        // Anteile angleichen (10.11.2026, Hibape-Land: Ödland 15,5 % statt 22 %, Schnee 25,7 % statt 21 %)
        Rebalance(region, quota, stepCost);

        // Kartenmitte (0,0) immer Kiefernwald (Wunsch des Nutzers 10.11.2026): Der Kiefernwald wächst zwar von der Mitte aus, aber
        // die Verwirbelung der Grenzen (BiomeAt, bis ca. 350 m) konnte ein anderes Biom dorthin schieben. Deshalb die Zellen, auf die
        // BiomeAt im Umkreis von 150 m um die Mitte tatsächlich schaut, auf Kiefernwald setzen.
        if (spec.Shares[0] > 0)
        {
            double c = size / 2.0;
            for (double dz = -CenterForest - 4; dz <= CenterForest + 4; dz += 2)
                for (double dx = -CenterForest - 4; dx <= CenterForest + 4; dx += 2)
                    if (dx * dx + dz * dz <= (CenterForest + 4) * (CenterForest + 4))   // dicht abgetastet, sonst fällt ein Punkt zwischen zwei Proben in eine andere Zelle
                        region[RegionIndex(c + dx, c + dz)] = 0;
        }

        // weiche Gewichte (zweimal Kastenfilter, Radius 8 Zellen = 256 m)
        weight = new float[5][];
        for (int b = 0; b < 5; b++)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = region[i] == b ? 1 : 0;
            w = BoxBlur(w, nb, 8);
            weight[b] = BoxBlur(w, nb, 8);
        }
    }

    /// Gleicht die Flächen an die Anteile an: Zellen an der Grenze zu einem zu kleinen Biom wechseln von einem zu großen Biom
    /// hinüber (in Runden, die verrauschtesten Kosten zuerst, damit die Grenzen unregelmäßig bleiben).
    /// Ursache der Abweichung: Wachstum stoppt beim Anteil, eingeschlossene Biome bleiben zu klein, den Rest nehmen die offenen.
    void Rebalance(byte[] reg, long[] quota, double[] noise)
    {
        int n = reg.Length;
        var count = new long[5];
        foreach (byte r in reg) count[r]++;
        long tol = Math.Max(1, n / 500);   // 0,2 % der Karte
        for (int round = 0; round < 600; round++)
        {
            var deficit = new long[5];
            long worst = 0;
            for (int b = 0; b < 5; b++) { deficit[b] = quota[b] - count[b]; worst = Math.Max(worst, Math.Abs(deficit[b])); }
            if (worst <= tol) return;
            var cand = new List<(double key, int idx, int to)>();
            for (int iz = 0; iz < nb; iz++)
                for (int ix = 0; ix < nb; ix++)
                {
                    int i = iz * nb + ix, o = reg[i];
                    if (deficit[o] >= worst) continue;
                    int best = -1;
                    for (int k = 0; k < 4; k++)
                    {
                        int x = ix + (k == 0 ? 1 : k == 1 ? -1 : 0), z = iz + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (x < 0 || z < 0 || x >= nb || z >= nb) continue;
                        int t = reg[z * nb + x];
                        if (t != o && deficit[t] - deficit[o] >= 2 && (best < 0 || deficit[t] > deficit[best])) best = t;
                    }
                    if (best >= 0) cand.Add((noise[i] + rnd.NextDouble() * 4, i, best));
                }
            if (cand.Count == 0) return;
            cand.Sort((a, b) => a.key.CompareTo(b.key));
            int take = Math.Max(1, cand.Count / 6);
            int moved = 0;
            foreach (var c in cand)
            {
                if (moved >= take) break;
                int o = reg[c.idx];
                if (deficit[c.to] - deficit[o] < 2) continue;
                reg[c.idx] = (byte)c.to;
                count[o]--; count[c.to]++; deficit[o]++; deficit[c.to]--;
                moved++;
            }
            if (moved == 0) return;
        }
    }

    void Grow(PriorityQueue<(int idx, int b), double> pq, double[] dist, sbyte[] owner, double[] stepCost, long[] quota, long[] count,
        List<(int, int, double)> _)
    {
        Span<int> ox = stackalloc int[] { 1, -1, 0, 0 }, oz = stackalloc int[] { 0, 0, 1, -1 };
        while (pq.TryDequeue(out var item, out double d))
        {
            int idx = item.idx, b = item.b;
            if (owner[idx] >= 0) continue;
            if (count[b] >= quota[b]) continue;
            owner[idx] = (sbyte)b;
            count[b]++;
            int ix = idx % nb, iz = idx / nb;
            for (int k = 0; k < 4; k++)
            {
                int x = ix + ox[k], z = iz + oz[k];
                if (x < 0 || z < 0 || x >= nb || z >= nb) continue;
                int j = z * nb + x;
                if (owner[j] >= 0) continue;
                double nd = d + stepCost[j];
                pq.Enqueue((j, b), nd);
            }
        }
    }

    static float[] BoxBlur(float[] src, int n, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, n, z =>
        {
            for (int x = 0; x < n; x++)
            {
                float s = 0;
                for (int k = -r; k <= r; k++) s += src[z * n + Math.Clamp(x + k, 0, n - 1)];
                tmp[z * n + x] = s / (2 * r + 1);
            }
        });
        Parallel.For(0, n, z =>
        {
            for (int x = 0; x < n; x++)
            {
                float s = 0;
                for (int k = -r; k <= r; k++) s += tmp[Math.Clamp(z + k, 0, n - 1) * n + x];
                dst[z * n + x] = s / (2 * r + 1);
            }
        });
        return dst;
    }

    /// Biom an der Stelle (gx, gz = Meter ab der Südwest-Ecke); Grenzen über Verschiebung verwirbelt
    public int BiomeAt(double gx, double gz) => region[RegionIndex(gx, gz)];

    /// Halbmesser um die Kartenmitte, in dem immer Kiefernwald ist (Meter)
    const double CenterForest = 150;

    /// Zelle der Biom-Regionen, auf die BiomeAt für die Stelle (gx, gz) schaut (Grenzen über Verschiebung verwirbelt)
    int RegionIndex(double gx, double gz)
    {
        double wx = gx + 320 * warpX.Fbm(gx / 900, gz / 900, 4) + 25 * warpX.Noise(gx / 60 + 17.3, gz / 60);
        double wz = gz + 320 * warpZ.Fbm(gx / 900, gz / 900, 4) + 25 * warpZ.Noise(gx / 60, gz / 60 + 5.1);
        int ix = Math.Clamp((int)(wx / BiomeCell), 0, nb - 1), iz = Math.Clamp((int)(wz / BiomeCell), 0, nb - 1);
        return iz * nb + ix;
    }

    void Weights(double gx, double gz, Span<double> w)
    {
        // gleiche Verwirbelung wie BiomeAt, damit Berge zu den sichtbaren Biomgrenzen passen (nur die grobe, die feine ist im Weichzeichnen ohnehin weg)
        double ox = 320 * warpX.Fbm(gx / 900, gz / 900, 4), oz = 320 * warpZ.Fbm(gx / 900, gz / 900, 4);
        gx += ox; gz += oz;
        double fx = gx / BiomeCell - 0.5, fz = gz / BiomeCell - 0.5;
        int x0 = (int)Math.Floor(fx), z0 = (int)Math.Floor(fz);
        double tx = fx - x0, tz = fz - z0;
        int xa = Math.Clamp(x0, 0, nb - 1), xb = Math.Clamp(x0 + 1, 0, nb - 1), za = Math.Clamp(z0, 0, nb - 1), zb = Math.Clamp(z0 + 1, 0, nb - 1);
        for (int b = 0; b < 5; b++)
        {
            var a = weight[b];
            w[b] = (a[za * nb + xa] * (1 - tx) + a[za * nb + xb] * tx) * (1 - tz) + (a[zb * nb + xa] * (1 - tx) + a[zb * nb + xb] * tx) * tz;
        }
    }

    // ---------------------------------------------------------------- Geländeform

    /// Höhe der Landschaft vor Erosion und Seen (Meter) an der Stelle (gx, gz)
    public double RawHeight(double gx, double gz, out double roughness)
    {
        Span<double> w = stackalloc double[5];
        Weights(gx, gz, w);
        double baseOff = 0, hillAmp = 0, rollAmp = 0, mountAmp = 0, cover = 0, platAmp = 0, valleyAmp = 0;
        roughness = 0;
        for (int b = 0; b < 5; b++)
        {
            baseOff += w[b] * BaseOff[b]; hillAmp += w[b] * HillAmp[b]; rollAmp += w[b] * RollAmp[b]; mountAmp += w[b] * MountAmp[b];
            cover += w[b] * MountCover[b]; platAmp += w[b] * PlateauAmp[b]; roughness += w[b] * RoughAmp[b]; valleyAmp += w[b] * ValleyAmp[b];
        }
        double h = GroundLevel + baseOff + 10 * cont.Fbm(gx / 2600, gz / 2600, 3);
        h += hillAmp * spec.Hills * (0.5 + 0.5 * Math.Clamp(hills.Fbm(gx / 520, gz / 520, 4) * 1.8, -1, 1));
        // kleinere Wellen (ca. 250 m) überall, damit es kaum große glatte Flächen gibt
        h += rollAmp * Math.Min(spec.Hills, 1.5) * Math.Clamp(rolls.Fbm(gx / 260, gz / 260, 3) * 1.8, -1, 1);
        double m = MountMask(gx, gz, cover);
        double mountPart = 0;
        if (m > 0 && mountAmp > 0.5)
        {
            // Gebirgsgegenden: weiche Maske (Anteil ≈ cover), darin Rücken aus „ridged“ Rauschen
            double r = Math.Min(1, ridge.Ridged(gx / 720, gz / 720, 6) * 1.25);
            double vary = 0.75 + 0.25 * peakVar.Fbm(gx / 1100, gz / 1100, 2) * 2;
            // mit Spielstempeln trägt das Rauschen nur 70 % (der Rest kommt aus den Stempeln)
            mountPart = mountAmp * spec.Mountains * m * vary * (0.15 + 0.85 * Math.Pow(r, 1.4)) * (stampBuckets != null ? 0.7 : 1);
        }
        // Höhenstempel des Spiels (Data\Stamps), wie der Berg-Pinsel: bringen Rinnen, Grate und Felsstufen mit
        var (stampMount, stampHill) = StampSum(gx, gz);
        mountPart += stampMount;
        h += mountPart + stampHill;
        // Schroffheit wie bei der Gebirgskette: Grate und Felsstufen (45/18/8 m), fleckig verteilt, stärker je höher der Berg
        if (spec.Rough > 0 && mountPart + stampHill > 1)
        {
            double rel = Math.Min(mountPart + 0.6 * stampHill, 130);
            double patch = 0.3 + 0.7 * SmoothStep(-0.35, 0.35, crag.Noise(gx / 260 + 7.7, gz / 260));
            double c1 = crag.Ridged(gx / 45, gz / 45, 3), c2 = crag.Ridged(gx / 18 + 5.3, gz / 18, 2), c3 = crag.Noise(gx / 8 + 1.1, gz / 8);
            h += spec.Rough * patch * rel * (0.11 * (c1 - 0.42) + 0.05 * (c2 - 0.42) + 0.014 * c3);
        }
        if (valleyAmp > 0.5 && spec.Valleys > 0)
        {
            // Täler: schmale, gewundene Linien (Nulldurchgänge eines Rauschens), in Gebirgen tiefer
            double vx = gx + 160 * valley.Noise(gx / 450 + 5.5, gz / 450), vz = gz + 160 * valley.Noise(gx / 450, gz / 450 + 21.2);
            double line = 1 - Math.Min(1, Math.Abs(valley.Fbm(vx / 1500, vz / 1500, 2) * 2.4));
            double k = Math.Min(spec.Valleys, 3);
            double cut = Math.Pow(line, 4);
            if (k > 1)
            {
                // ab 100 % ein zweites, feineres Netz von Seitentälern
                double line2 = 1 - Math.Min(1, Math.Abs(valley.Fbm(vx / 750 + 77.7, vz / 750, 2) * 2.4));
                cut = Math.Max(cut, (k - 1) * 0.5 * Math.Pow(line2, 6));
            }
            // Tal = Gelände sinkt zur Talsohle (knapp unter der Grundhöhe): im Gebirge tiefe Schluchten, im Flachland flache Mulden
            // Talsohle wellig, Wände unterschiedlich weit abgetragen (sonst wirken Schluchten wie glatt gefräst)
            double floor = GroundLevel + baseOff - valleyAmp * 0.35 * k + 7 * valley.Noise(gx / 170 + 9.1, gz / 170);
            double strength = Math.Min(1, cut * (0.55 + 0.45 * k) * (0.82 + 0.3 * valley.Noise(gx / 70 + 3.3, gz / 70 + 1.7)));
            double beforeCut = h;
            if (h > floor) h += (floor - h) * strength;
            else h -= valleyAmp * 0.15 * strength;
            // Talflanken nicht glatt: Felsbänder und Grate je nach Einschnitttiefe
            double cutDepth = beforeCut - h;
            if (spec.Rough > 0 && cutDepth > 2)
                h += spec.Rough * Math.Min(cutDepth, 90) * (0.07 * (crag.Ridged(gx / 38 + 9.1, gz / 38, 3) - 0.42) + 0.02 * crag.Noise(gx / 12, gz / 12 + 3.3));
        }
        if (platAmp > 0.5 && spec.Mountains > 0)
            h += platAmp * spec.Mountains * PlateauShape(gx, gz);
        // Gipfel weich unter der Grenze des Spiels halten (Höhen bis 255 m)
        if (h > 200) h = 200 + 45 * (1 - Math.Exp(-(h - 200) / 45));
        return Math.Max(h, 4);
    }

    /// Tafelberge der Wüste (0 … ca. 1,3): zwei Stufen mit steilem Rand. Ränder ausgefranst (feineres Rauschen) und je Stelle
    /// verschieden steil, Höhe der Stufen schwankt. 10.11.2026 (Wunsch des Nutzers): Form wie Version 0.73 (ohne die Steilwände
    /// und Grate aus 0.74/0.75), dafür ca. 10 % höher (PlateauAmp 60) und mehr Abwechslung: in etwa einem Viertel der Gegenden
    /// sind die Plateaus deutlich niedriger (bis 55 %).
    double PlateauShape(double gx, double gz)
    {
        double v = plat.Fbm(gx / 850, gz / 850, 4) * 2.2 + 0.14 * plat.Noise(gx / 120 + 41.7, gz / 120) + 0.05 * plat.Noise(gx / 35, gz / 35 + 9.2);
        double edge = 0.04 + 0.14 * (0.5 + 0.5 * Math.Clamp(plat.Noise(gx / 500 + 3.3, gz / 500 + 8.8) * 1.6, -1, 1));
        double tall = 1 + 0.25 * plat.Noise(gx / 700 + 13.1, gz / 700);
        double low = 1 - 0.45 * SmoothStep(0.05, 0.3, plat.Noise(gx / 1300 + 21.7, gz / 1300 + 4.2));
        double t1 = Threshold(Math.Min(0.6, 0.3 * Math.Sqrt(spec.Mountains)));
        return tall * low * (0.55 * SmoothStep(t1, t1 + edge, v) + 0.45 * SmoothStep(t1 + 0.35, t1 + 0.35 + edge, v));
    }

    /// Gebirgsmaske 0 … 1 an der Stelle (cover = gewichteter Anteil der Biome; &lt; 0 = selbst aus den Gewichten holen)
    double MountMask(double gx, double gz, double cover = -1)
    {
        if (spec.Mountains <= 0) return 0;
        if (cover < 0)
        {
            Span<double> w = stackalloc double[5];
            Weights(gx, gz, w);
            cover = 0;
            for (int b = 0; b < 5; b++) cover += w[b] * MountCover[b];
        }
        if (cover <= 0.001) return 0;
        double cov = Math.Min(0.9, cover * Math.Sqrt(spec.Mountains));
        double v = mmask.Fbm(gx / 1700, gz / 1700, 3) * 2.2;
        double th = Threshold(cov);
        return SmoothStep(th - 0.2, th + 0.3, v);
    }

    // ---------------------------------------------------------------- Höhenstempel des Spiels

    sealed class StampPlace
    {
        public float[] Data;
        public double X, Z, Radius, Cos, Sin, Height, Steep;
        public bool Mirror, Hill;
    }

    const double StampBucket = 256;
    List<StampPlace>[] stampBuckets;
    int stampBucketsN;
    static readonly Dictionary<string, float[]> stampCache = new();

    static float[] CachedStamp(string game, MountainKind kind, int variant)
    {
        string key = game + "|" + kind + "|" + variant;
        lock (stampCache)
        {
            if (!stampCache.TryGetValue(key, out var s))
            {
                try { s = MountainSpec.LoadStamp(game, kind, variant); } catch (Exception) { s = null; }
                stampCache[key] = s;
            }
            return s;
        }
    }

    /// Stempel verteilen (wie das Spiel: Gebirge im Schnee, verbrannten und Kiefernwald, Plateaus in der Wüste, Hügel überall);
    /// Gebirgsstempel bevorzugt in der Gebirgsmaske. Ohne Spielordner gibt es keine Stempel (dann nur Rauschen).
    void PlaceStamps()
    {
        stampBuckets = null;
        string game = spec.GameFolder;
        if (game == null || (spec.Mountains <= 0 && spec.Hills <= 0)) return;
        var stamps = new Dictionary<MountainKind, float[][]>();
        foreach (var k in new[] { MountainKind.Mountain, MountainKind.Plateau, MountainKind.Hill })
            stamps[k] = new[] { CachedStamp(game, k, 0), CachedStamp(game, k, 1) }.Where(s => s != null).ToArray();
        if (stamps.Values.All(a => a.Length == 0)) return;
        // je Biom: (Art, Anzahl je km², Höhe in m bei 100 %)
        // je Biom: (Art, Anzahl je km², Höhe in m bei 100 %, Steilheit von … bis: Potenz der Form, > 1 = spitzer)
        // 9.11.2026 (Wunsch des Nutzers): Wüste mit einzelnen spitzen Bergen, Ödland hügeliger, Steilheit stärker gestreut
        var table = new (MountainKind Kind, double PerKm2, double Height, double SteepMin, double SteepMax)[][]
        {
            new[] { (MountainKind.Mountain, 0.55, 60.0, 0.8, 1.5), (MountainKind.Hill, 1.2, 22.0, 0.8, 1.4) },
            new[] { (MountainKind.Mountain, 0.8, 85.0, 0.8, 1.6), (MountainKind.Hill, 0.8, 20.0, 0.8, 1.4) },
            new[] { (MountainKind.Plateau, 0.9, 55.0, 0.85, 1.4), (MountainKind.Mountain, 0.3, 75.0, 1.4, 2.6), (MountainKind.Hill, 0.9, 18.0, 0.8, 1.9) },
            new[] { (MountainKind.Mountain, 1.1, 100.0, 0.8, 1.6), (MountainKind.Hill, 0.5, 22.0, 0.8, 1.4) },
            new[] { (MountainKind.Hill, 1.5, 20.0, 0.7, 1.7), (MountainKind.Mountain, 0.12, 50.0, 0.9, 1.6) },
        };
        int total = Math.Max(1, spec.Shares.Sum());
        double areaKm2 = (double)size * size / 1e6;
        var r = new Random(spec.SeedValue + 31);
        var list = new List<StampPlace>();
        for (int b = 0; b < 5; b++)
        {
            foreach (var (kind, perKm2, height, steepMin, steepMax) in table[b])
            {
                bool hill = kind == MountainKind.Hill;
                double amount = hill ? spec.Hills : spec.Mountains;
                if (amount <= 0 || stamps[kind].Length == 0) continue;
                int n = (int)Math.Round(spec.Shares[b] / (double)total * areaKm2 * perKm2 * Math.Sqrt(amount));
                for (int i = 0; i < n; i++)
                {
                    for (int attempt = 0; attempt < 40; attempt++)
                    {
                        double x = r.NextDouble() * size, z = r.NextDouble() * size;
                        if (BiomeAt(x, z) != b) continue;
                        // Gebirge und Plateaus vor allem dort, wo die Gebirgsmaske ist (einzelne auch außerhalb)
                        if (!hill && MountMask(x, z) < 0.25 && r.NextDouble() > (b == 2 || b == 4 ? 0.6 : 0.2)) continue;   // Wüste/Ödland: Einzelberge auch außerhalb
                        double diameter = kind switch { MountainKind.Mountain => 450 + r.NextDouble() * 700, MountainKind.Plateau => 400 + r.NextDouble() * 550, _ => 280 + r.NextDouble() * 450 };
                        double a = r.NextDouble() * Math.PI * 2;
                        list.Add(new StampPlace
                        {
                            Data = stamps[kind][r.Next(stamps[kind].Length)],
                            X = x, Z = z, Radius = diameter / 2, Cos = Math.Cos(a), Sin = Math.Sin(a), Mirror = r.Next(2) == 0,
                            Height = height * amount * (kind == MountainKind.Plateau ? 0.35 + 0.65 * r.NextDouble() : 0.5 + 0.5 * r.NextDouble()),   // Plateaus: auch deutlich niedrigere
                            Steep = steepMin + (steepMax - steepMin) * r.NextDouble(), Hill = hill,
                        });
                        break;
                    }
                }
            }
        }
        stampBucketsN = (int)Math.Ceiling(size / StampBucket);
        stampBuckets = new List<StampPlace>[stampBucketsN * stampBucketsN];
        foreach (var s in list)
        {
            int x0 = Math.Max(0, (int)((s.X - s.Radius) / StampBucket)), x1 = Math.Min(stampBucketsN - 1, (int)((s.X + s.Radius) / StampBucket));
            int z0 = Math.Max(0, (int)((s.Z - s.Radius) / StampBucket)), z1 = Math.Min(stampBucketsN - 1, (int)((s.Z + s.Radius) / StampBucket));
            for (int bz = z0; bz <= z1; bz++)
                for (int bx = x0; bx <= x1; bx++)
                    (stampBuckets[bz * stampBucketsN + bx] ??= new()).Add(s);
        }
        StampCount = list.Count;
    }

    public int StampCount { get; private set; }

    (double Mount, double Hill) StampSum(double gx, double gz)
    {
        if (stampBuckets == null) return (0, 0);
        int bx = Math.Clamp((int)(gx / StampBucket), 0, stampBucketsN - 1), bz = Math.Clamp((int)(gz / StampBucket), 0, stampBucketsN - 1);
        var list = stampBuckets[bz * stampBucketsN + bx];
        if (list == null) return (0, 0);
        double mount = 0, hill = 0;
        const int S = MountainSpec.StampSize;
        foreach (var s in list)
        {
            double dx = gx - s.X, dz = gz - s.Z;
            double rr = Math.Sqrt(dx * dx + dz * dz) / s.Radius;
            if (rr >= 1) continue;
            double u = dx * s.Cos - dz * s.Sin, v = dx * s.Sin + dz * s.Cos;
            if (s.Mirror) u = -u;
            double fx = S / 2.0 + u / (2 * s.Radius) * S, fy = S / 2.0 + v / (2 * s.Radius) * S;
            if (fx < 0 || fy < 0 || fx >= S - 1 || fy >= S - 1) continue;
            int ix = (int)fx, iy = (int)fy;
            double tx = fx - ix, ty = fy - iy;
            int i = iy * S + ix;
            var d = s.Data;
            double val = (d[i] * (1 - tx) + d[i + 1] * tx) * (1 - ty) + (d[i + S] * (1 - tx) + d[i + S + 1] * tx) * ty;
            if (val <= 0) continue;
            double edge = rr <= 0.75 ? 1 : 1 - SmoothStep(0.75, 1, rr);
            double add = s.Height * Math.Pow(val, s.Steep) * edge;
            if (s.Hill) hill += add; else mount += add;
        }
        return (mount, hill);
    }

    /// Schwelle für ein Rauschen (Fbm × 2,2, etwa normalverteilt mit Streuung 0,45), über der der Anteil cov liegt
    static double Threshold(double cov)
    {
        cov = Math.Clamp(cov, 0.01, 0.99);
        // Näherung der Umkehrfunktion der Normalverteilung (Abramowitz/Stegun)
        double p = 1 - cov, q = p < 0.5 ? p : 1 - p;
        double t = Math.Sqrt(-2 * Math.Log(q));
        double z = t - (2.515517 + 0.802853 * t + 0.010328 * t * t) / (1 + 1.432788 * t + 0.189269 * t * t + 0.001308 * t * t * t);
        if (p < 0.5) z = -z;
        return z * 0.45;
    }

    static double SmoothStep(double a, double b, double v)
    {
        double t = Math.Clamp((v - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    float[] roughCoarse;

    /// Grobes Raster rechnen (cell Meter je Zelle): Biome, Form, Erosion, Seen. Für die Vorschau mit großem cell.
    public void BuildCoarse(double cell, IProgress<string> progress = null)
    {
        Cell = cell;
        N = (int)Math.Round(size / cell);
        progress?.Report(Loc.T("Growing biomes …"));
        GrowBiomes();
        PlaceStamps();
        progress?.Report(Loc.T("Shaping hills and mountains …"));
        coarse = new float[N * N];
        roughCoarse = new float[N * N];
        hard = new byte[N * N];
        Parallel.For(0, N, j =>
        {
            for (int i = 0; i < N; i++)
            {
                double h = RawHeight((i + 0.5) * cell, (j + 0.5) * cell, out double r);
                coarse[j * N + i] = (float)h;
                roughCoarse[j * N + i] = (float)r;
                hard[j * N + i] = Hardness((i + 0.5) * cell, (j + 0.5) * cell);
            }
        });
        progress?.Report(Loc.T("Letting slopes settle (erosion) …"));
        Erode(cell <= 8 ? 40 : 6);
        progress?.Report(Loc.T("Unique formations …"));
        PlaceAnomalies();
        if (cell <= 4 && spec.Erosion > 0)
        {
            // Regen: Tropfen graben Rinnen in die Hänge und lagern unten ab (nur in der fertigen Welt, die Vorschau ist zu grob)
            progress?.Report(Loc.T("Rain erosion (gullies on the slopes) …"));
            var before = (float[])coarse.Clone();
            HydraulicErode((int)(N * (long)N / 5 * spec.Erosion), spec.SeedValue + 55);
            // Sicherung: Erosion verändert den Boden höchstens um −15 … +8 m (sonst seltene tiefe Löcher, wo viele Tropfen zusammenlaufen)
            Parallel.For(0, N * N, i => coarse[i] = Math.Clamp(coarse[i], before[i] - 15, before[i] + 8));
            before = null;
        }
        progress?.Report(Loc.T("Craters and cracks …"));
        PlaceCratersAndCracks();
        Erode(4);
        progress?.Report(Loc.T("Placing lakes …"));
        PlaceLakes();
    }

    /// Hangrutschung: wo zwei Nachbarn steiler als ca. 38° zueinander stehen, rutscht Material nach unten (massenerhaltend)
    void Erode(int iterations)
    {
        // Böschungswinkel je Gesteinsart (hard): 0 normal 38°, 1 hart 72°, 2 fest 52°, 3 weich 30°
        var tal = new[] { 38.0, 72, 52, 30 }.Select(deg => (float)(Math.Tan(deg * Math.PI / 180) * Cell)).ToArray();
        var delta = new float[N * N];
        for (int it = 0; it < iterations; it++)
        {
            Parallel.For(0, N, j =>
            {
                for (int i = 0; i < N; i++)
                {
                    int idx = j * N + i;
                    float h = coarse[idx], d = 0;
                    // hartes Gestein (Steilwände, Felsnadeln, Risse): darf viel steiler stehen; zählt, wenn eine der beiden Zellen hart ist
                    float ti = tal[hard[idx]];
                    if (i > 0) d += Flow(coarse[idx - 1], h, Math.Max(ti, tal[hard[idx - 1]]));
                    if (i < N - 1) d += Flow(coarse[idx + 1], h, Math.Max(ti, tal[hard[idx + 1]]));
                    if (j > 0) d += Flow(coarse[idx - N], h, Math.Max(ti, tal[hard[idx - N]]));
                    if (j < N - 1) d += Flow(coarse[idx + N], h, Math.Max(ti, tal[hard[idx + N]]));
                    delta[idx] = d;
                }
            });
            Parallel.For(0, N * N, idx => coarse[idx] += delta[idx]);
        }
    }

    /// Tropfen-Erosion (nach dem bekannten Verfahren „hydraulic erosion“ mit Tropfen): Jeder Tropfen läuft bergab, nimmt
    /// je nach Gefälle, Tempo und Wassermenge Material auf und lagert es ab, wenn er langsamer wird. Viele Tropfen = Rinnen,
    /// Grate und Schwemmfächer. Die Tropfen laufen parallel; gleichzeitige Zugriffe auf dieselbe Zelle sind selten und harmlos.
    void HydraulicErode(int attempts, int seed)
    {
        // Werte nach Versuchen (7.11.2026, in DropletsInTile): längere, verzweigte Rinnen statt kurzer Kerben
        // Pinsel zum Abtragen: Radius 2 Zellen, Gewicht fällt nach außen ab
        var brush = new List<(int dx, int dy, float w)>();
        float sum = 0;
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= 3.2) continue;
                float w = (float)(3.2 - dist);
                brush.Add((dx, dy, w)); sum += w;
            }
        var br = brush.Select(b => (b.dx, b.dy, w: b.w / sum)).ToArray();
        float[] H = coarse;
        int n = N;
        double cell = Cell;
        // Gleicher Startwert = gleiche Welt: Kacheln (128 Zellen) im Schachbrett aus 4 Farben; gleichzeitig laufen nur Kacheln einer Farbe,
        // jeder Tropfen bleibt in seiner Kachel ± halbe Kachel (dann berühren sich gleichzeitig laufende Bereiche nie), eigener Zufall je Kachel.
        // Zwei Durchgänge mit um eine halbe Kachel versetztem Raster, damit an den Kachelgrenzen keine Nähte bleiben.
        const int T = 128;
        int tilesN = n / T + 2;
        for (int pass = 0; pass < 2; pass++)
            for (int color = 0; color < 4; color++)
            {
                int off = pass * T / 2;
                var tiles = new List<(int tx, int ty)>();
                for (int ty = 0; ty < tilesN; ty++)
                    for (int tx = 0; tx < tilesN; tx++)
                        if (((tx & 1) | ((ty & 1) << 1)) == color) tiles.Add((tx, ty));
                int p = pass, c = color;
                Parallel.ForEach(tiles, tile => DropletsInTile(tile.tx * T - off, tile.ty * T - off, T, p, c, attempts / 2, seed, H, n, cell, br));
            }
    }

    void DropletsInTile(int x0, int y0, int T, int pass, int color, int attemptsAll, int seed, float[] H, int n, double cell, (int dx, int dy, float w)[] br)
    {
        const double inertia = 0.3, capFactor = 3, minCap = 0.01, erodeSpeed = 0.12, depositSpeed = 0.2, evaporate = 0.012;
        const int maxSteps = 90;
        int lx = Math.Max(3, x0), hx = Math.Min(n - 4, x0 + T), ly = Math.Max(3, y0), hy = Math.Min(n - 4, y0 + T);
        if (hx <= lx || hy <= ly) return;
        // erlaubter Bereich des Tropfens (halbe Kachel Rand, abzüglich Pinsel)
        double ax = Math.Max(3, x0 - T / 2 + 4), bx2 = Math.Min(n - 4, x0 + T + T / 2 - 4), ay = Math.Max(3, y0 - T / 2 + 4), by2 = Math.Min(n - 4, y0 + T + T / 2 - 4);
        var r = new Random(unchecked(seed + pass * 1000003 + (x0 + 2 * T) * 7919 + (y0 + 2 * T) * 104729));
        int per = (int)((long)attemptsAll * (hx - lx) * (hy - ly) / ((long)n * n));
        {
            for (int a = 0; a < per; a++)
            {
                double x = lx + r.NextDouble() * (hx - lx), y = ly + r.NextDouble() * (hy - ly);
                var (h0, gx0, gy0) = Gradient(H, n, x, y);
                // Tropfen vor allem dort, wo es Gefälle gibt (in der Ebene passiert ohnehin nichts)
                if (Math.Sqrt(gx0 * gx0 + gy0 * gy0) / cell < 0.08 && r.NextDouble() > 0.03) continue;
                double dx = 0, dy = 0, speed = 1, water = 1, sediment = 0;
                for (int step = 0; step < maxSteps; step++)
                {
                    int ix = (int)x, iy = (int)y;
                    double fx = x - ix, fy = y - iy;
                    var (h, gx, gy) = Gradient(H, n, x, y);
                    dx = dx * inertia - gx * (1 - inertia);
                    dy = dy * inertia - gy * (1 - inertia);
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-9) break;
                    dx /= len; dy /= len;
                    x += dx; y += dy;
                    if (x < ax || y < ay || x >= bx2 || y >= by2) break;
                    double dh = Gradient(H, n, x, y).h - h;
                    double capacity = Math.Max(-dh * speed * water * capFactor, minCap);
                    if (sediment > capacity || dh > 0)
                    {
                        double dep = dh > 0 ? Math.Min(dh, sediment) : (sediment - capacity) * depositSpeed;
                        sediment -= dep;
                        int i = iy * n + ix;
                        H[i] += (float)(dep * (1 - fx) * (1 - fy));
                        H[i + 1] += (float)(dep * fx * (1 - fy));
                        H[i + n] += (float)(dep * (1 - fx) * fy);
                        H[i + n + 1] += (float)(dep * fx * fy);
                    }
                    else
                    {
                        // am Kartenrand (letzte 60 Zellen) ausblenden: dort verlassen Tropfen die Karte und würden Gräben ziehen
                        double edgeFade = Math.Min(1, Math.Min(Math.Min(ix, iy), Math.Min(n - 1 - ix, n - 1 - iy)) / 60.0);
                        double er = Math.Min((capacity - sediment) * erodeSpeed, -dh) * edgeFade;
                        foreach (var (bx, by, w) in br) H[(iy + by) * n + ix + bx] -= (float)(er * w);
                        sediment += er;
                    }
                    // Tempo aus dem Gefälle (je Zelle gemessen, in Metern je Meter)
                    speed = Math.Min(6, Math.Sqrt(Math.Max(0.01, speed * speed - dh / cell * 4)));
                    water *= 1 - evaporate;
                }
            }
        }
    }

    static (double h, double gx, double gy) Gradient(float[] H, int n, double x, double y)
    {
        int ix = (int)x, iy = (int)y;
        double fx = x - ix, fy = y - iy;
        int i = iy * n + ix;
        double a = H[i], b = H[i + 1], c = H[i + n], d = H[i + n + 1];
        double gx = (b - a) * (1 - fy) + (d - c) * fy;
        double gy = (c - a) * (1 - fx) + (d - b) * fx;
        double h = a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) + c * (1 - fx) * fy + d * fx * fy;
        return (h, gx, gy);
    }

    static float Flow(float neighbor, float h, float talus)
    {
        float diff = neighbor - h;
        if (diff > talus) return (diff - talus) * 0.12f;
        if (diff < -talus) return (diff + talus) * 0.12f;
        return 0;
    }

    double SampleCoarse(double gx, double gz)
    {
        double fx = gx / Cell - 0.5, fz = gz / Cell - 0.5;
        int x0 = (int)Math.Floor(fx), z0 = (int)Math.Floor(fz);
        double tx = fx - x0, tz = fz - z0;
        int xa = Math.Clamp(x0, 0, N - 1), xb = Math.Clamp(x0 + 1, 0, N - 1), za = Math.Clamp(z0, 0, N - 1), zb = Math.Clamp(z0 + 1, 0, N - 1);
        return (coarse[za * N + xa] * (1 - tx) + coarse[za * N + xb] * tx) * (1 - tz) + (coarse[zb * N + xa] * (1 - tx) + coarse[zb * N + xb] * tx) * tz;
    }

    // ---------------------------------------------------------------- Seen

    /// Seen in flachen Gegenden: unregelmäßiger Umriss, Spiegel 1 m unter dem tiefsten Punkt am Ufer, Becken bis „Tiefe“.
    /// Die Auswahl (Lage, Größe) hängt nur vom Zufall und vom Gelände ab, nicht vom Raster – Vorschau und Welt haben dieselben Seen.
    void PlaceLakes()
    {
        LakeList.Clear();
        lakeOf = new int[N * N];
        calm = new float[N * N];
        Array.Fill(calm, 1f);
        if (spec.Lakes <= 0) return;
        double perKm2 = spec.Lakes switch { 1 => 1 / 18.0, 2 => 1 / 8.0, _ => 1 / 4.0 };
        int want = Math.Max(spec.Lakes, (int)Math.Round((double)size * size / 1e6 * perKm2));   // auch kleine Welten: 1/2/3 Seen
        var lr = new Random(spec.SeedValue + 77);
        double margin = Math.Min(500, size * 0.1);
        for (int attempt = 0; attempt < want * 60 && LakeList.Count < want; attempt++)
        {
            double cx = margin + lr.NextDouble() * (size - 2 * margin), cz = margin + lr.NextDouble() * (size - 2 * margin);
            // Größe stark gestreut: viele kleine, einige große (7.11.2026, Wunsch des Nutzers: weniger gleichmäßig)
            double radius = 30 + Math.Pow(lr.NextDouble(), 1.6) * (spec.Lakes >= 3 ? 190 : 150);
            double depth = 2.5 + lr.NextDouble() * 7;
            var shape = LakeShape.Random(lr, radius);
            // Seen gehören in Mulden: vom Zufallspunkt bergab gehen (auch Bergseen in Senken zwischen den Gipfeln)
            (cx, cz) = Downhill(cx, cz, margin);
            if (LakeList.Any(l => Math.Sqrt((l.X - cx) * (l.X - cx) + (l.Z - cz) * (l.Z - cz)) < l.Radius * 1.5 + radius * 1.5 + 350)) continue;
            var (min, max) = RimRange(cx, cz, shape);
            if (max - min > 12)
            {
                // steile Umgebung (Gebirge): kleiner Bergsee
                shape = shape.Scaled(0.55);
                (min, max) = RimRange(cx, cz, shape);
            }
            if (max - min > 40) continue;
            int level = (int)Math.Floor(min) - 1;
            if (level < 3 || level > 235) continue;
            LakeList.Add(new LakeInfo(cx, cz, shape.Radius, level, depth));
            Carve(LakeList.Count, cx, cz, shape, level, depth, lr);
        }
    }

    (double Min, double Max) RimRange(double cx, double cz, LakeShape shape)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int k = 0; k < 40; k++)
        {
            double a = k * Math.PI * 2 / 40;
            double rr = shape.R(a);
            foreach (double off in new[] { 0.0, 10, 20, 30 })   // Uferlinie ist bis ca. 14 m ausgefranst
            {
                // aus dem fertigen Gelände (nach der Erosion: Rinnen können den Rand senken)
                double h = SampleCoarse(cx + Math.Cos(a) * (rr + off), cz + Math.Sin(a) * (rr + off));
                min = Math.Min(min, h); max = Math.Max(max, h);
            }
        }
        return (min, max);
    }

    /// in Schritten von 30 m bergab bis zu einer Mulde (höchstens 40 Schritte)
    (double, double) Downhill(double x, double z, double margin)
    {
        double h = RawHeight(x, z, out _);
        for (int step = 0; step < 40; step++)
        {
            double bx = x, bz = z, bh = h;
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4, nx = x + 30 * Math.Cos(a), nz = z + 30 * Math.Sin(a);
                if (nx < margin || nz < margin || nx > size - margin || nz > size - margin) continue;
                double nh = RawHeight(nx, nz, out _);
                if (nh < bh - 0.05) { bx = nx; bz = nz; bh = nh; }
            }
            if (bh >= h) break;
            x = bx; z = bz; h = bh;
        }
        return (x, z);
    }

    /// Umriss eines Sees: Ellipse (gestreckt, gedreht) mit zufälligen Buchten (Wellen 2.–8. Ordnung)
    sealed class LakeShape
    {
        public double Radius, Stretch, Axis;
        public double[] Amp = new double[9], Phase = new double[9];

        public static LakeShape Random(Random r, double radius)
        {
            var s = new LakeShape { Radius = radius, Stretch = 1 + Math.Pow(r.NextDouble(), 1.5) * 1.3, Axis = r.NextDouble() * Math.PI };
            for (int k = 2; k <= 8; k++)
            {
                s.Amp[k] = r.NextDouble() * 0.42 / Math.Pow(k, 0.85);
                s.Phase[k] = r.NextDouble() * Math.PI * 2;
            }
            return s;
        }

        public LakeShape Scaled(double f)
        {
            var s = (LakeShape)MemberwiseClone();
            s.Radius *= f;
            return s;
        }

        public double R(double a)
        {
            // Ellipse mit Halbachsen Radius·√Stretch und Radius/√Stretch (Fläche wie ein Kreis mit Radius)
            double t = a - Axis, ra = Radius * Math.Sqrt(Stretch), rb = Radius / Math.Sqrt(Stretch);
            double c = Math.Cos(t) / ra, s = Math.Sin(t) / rb;
            double e = 1 / Math.Sqrt(c * c + s * s);
            double w = 1;
            for (int k = 2; k <= 8; k++) w += Amp[k] * Math.Sin(k * a + Phase[k]);
            return e * Math.Max(0.35, w);
        }
    }

    /// Abstand zur Uferlinie (innen +, außen −), Umriss mit Rauschen in drei Größen; gleich im groben und im feinen Raster
    double LakeEdgeDistance(double cx, double cz, LakeShape shape, int seed, double gx, double gz)
    {
        double dx = gx - cx, dz = gz - cz, dist = Math.Sqrt(dx * dx + dz * dz);
        double d = shape.R(Math.Atan2(dz, dx)) - dist;
        return d + 9 * detail.Noise(gx / 45 + seed, gz / 45) + 4 * detail.Noise(gx / 16, gz / 16 + seed) + 1.5 * detail.Noise(gx / 6 + seed, gz / 6 + 3.3);
    }

    /// Mindestneigung der Uferböschung zwischen Spiegel − 0,3 m und Spiegel + 1,6 m (Anteil, 0,30 = 30 %)
    const double BankSlope = 0.30;

    void Carve(int id, double cx, double cz, LakeShape shape, int level, double depth, Random lr)
    {
        double reach = shape.Radius * 2.6 + 90;
        int i0 = Math.Max(0, (int)((cx - reach) / Cell)), i1 = Math.Min(N - 1, (int)((cx + reach) / Cell));
        int j0 = Math.Max(0, (int)((cz - reach) / Cell)), j1 = Math.Min(N - 1, (int)((cz + reach) / Cell));
        // manchmal eine Insel (nur in größeren Seen)
        bool island = shape.Radius > 70 && lr.NextDouble() < 0.4;
        double ia = lr.NextDouble() * Math.PI * 2, idist = lr.NextDouble() * 0.45;
        double ix = cx + Math.Cos(ia) * shape.R(ia) * idist, iz = cz + Math.Sin(ia) * shape.R(ia) * idist;
        double ir = 8 + lr.NextDouble() * shape.Radius * 0.22, ih = 1.5 + lr.NextDouble() * 4;
        int seed = lr.Next(1000);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                double gx = (i + 0.5) * Cell, gz = (j + 0.5) * Cell;
                double dx = gx - cx, dz = gz - cz;
                double a = Math.Atan2(dz, dx);
                double rr = shape.R(a);
                // > 0 = innen; Uferlinie ausgefranst (8.11.2026, Wunsch des Nutzers: Ufer zu künstlich): Rauschen in drei Größen
                double d = LakeEdgeDistance(cx, cz, shape, seed, gx, gz);
                // Uferart entlang des Ufers: Steilufer (−) … Strand mit Flachwasser (+)
                double kind = SmoothStep(-0.35, 0.35, AngNoise(a, 1.7, seed) + 0.25 * detail.Noise(gx / 120, gz / 120 + seed));
                int idx = j * N + i;
                float h = coarse[idx];
                // Uferhang auf hohen Seiten (Bergseen): Gelände höchstens so hoch wie ein Hang mit 30–45° vom Ufer aus,
                // sonst stünde dort eine senkrechte Wand
                double grade = 0.6 + 0.4 * (0.5 + 0.5 * AngNoise(a, 2, seed + 11));
                double bank = level + 1.5 + grade * Math.Max(0, -d);
                if (h > bank && d > -70) { coarse[idx] = (float)bank; h = (float)bank; }
                if (d > 0)
                {
                    // Ufer verschieden breit (flach auslaufend bis steil), an hohen Seiten breiter, Boden uneben
                    double shore = Math.Max(5 + 40 * kind, 0.8 * (h - level));
                    double deep = depth * (0.65 + 0.7 * (0.5 + 0.5 * detail.Noise(gx / 45 + seed, gz / 45)));
                    // am Strand ein Flachwasser-Streifen (0,4–0,8 m tief), dann erst tiefer
                    double shelf = 25 * kind;
                    double bottom = d < shelf ? level - 0.4 - 0.4 * (d / Math.Max(shelf, 1)) : level - 0.8 - deep * SmoothStep(0, 1, Math.Min((d - shelf) / (0.5 * rr), 1));
                    double t = SmoothStep(0, 1, Math.Min(d / shore, 1));
                    double target = h + (bottom - h) * t;
                    // Mindestneigung (11.11.2026, Hinweis des Nutzers: flaches Ufer gab gezackte Wasserkante, Terrassen und nassen Streifen
                    // ohne Wasser): von Spiegel + 1,6 m am Rand fällt der Hang mit BankSlope bis Spiegel − 0,3 m, danach flaches Wasser.
                    // So liegt die Wassergrenze (Spiegel + WetMargin) auf einem Hang und nicht auf einer fast ebenen Stufe.
                    target = Math.Min(target, Math.Max(level + 1.6 - BankSlope * d, level - 0.3));
                    if (island)
                    {
                        // Insel unregelmäßig (Umriss aus Rauschen, nicht kreisrund)
                        double di = Math.Sqrt((gx - ix) * (gx - ix) + (gz - iz) * (gz - iz));
                        double irr = ir * 2.2 * (1 + 0.45 * AngNoise(Math.Atan2(gz - iz, gx - ix), 1.2, seed + 5) + 0.15 * AngNoise(Math.Atan2(gz - iz, gx - ix), 3, seed + 9));
                        if (di < irr) target = Math.Max(target, level - 3 + (ih + 3) * SmoothStep(0, 1, 1 - di / irr) * (0.75 + 0.5 * (0.5 + 0.5 * detail.Noise(gx / 9, gz / 9 + seed))));
                    }
                    if (target < h) coarse[idx] = (float)target;
                }
                else
                {
                    // Ufer sicher über dem Spiegel (sonst liefe der See aus), leicht ansteigend statt flacher Ring
                    // nur der unmittelbare Uferrand (sonst entsteht ein Wall um den See; Lücken weiter draußen dichtet FillWater ab)
                    double floorH = level + 1.6;   // über der Wassergrenze (Spiegel + 1 m), damit Rauschen dort keine Wasserflecken macht
                    if (d > -6 && h < floorH) { coarse[idx] = (float)floorH; h = (float)floorH; }
                    // Strand: Land fällt sanft zum Wasser ab (ca. 12 %), nur absenken
                    // Strand schmal bis breit und uneben (glatte Streifen wirkten künstlich)
                    double beachW = (4 + 24 * kind) * (0.6 + 0.8 * (0.5 + 0.5 * detail.Noise(gx / 60 + seed, gz / 60 + 4.4)));
                    double beachH = level + 1.6 + 0.12 * (-d) + 0.5 * (0.5 + 0.5 * detail.Noise(gx / 13, gz / 13 + seed));
                    if (kind > 0.15 && -d < beachW && h > beachH) coarse[idx] = (float)(h + (beachH - h) * kind * (1 - SmoothStep(0, beachW, -d)));
                }
                if (d > -20 * Math.Max(1, Cell / 4)) lakeOf[idx] = id;
                // Ufer kaum ruhiger als das Land (Lücken im Ufer dichtet FillWater ab)
                // am Ufer ruhig: Rauschen verschiebt sonst die Wassergrenze
                calm[idx] = Math.Min(calm[idx], (float)(d > 0 ? 0.3 + 0.45 * SmoothStep(2, 20, d) : 0.3 + 0.7 * SmoothStep(2, 34, -d)));
            }
    }

    // ---------------------------------------------------------------- Vorschau

    /// Vorschaubild (BGRA, n × n, Zeile 0 = Norden): Biomfarbe mit Schummerung, Seen blau
    public byte[] RenderPreview(out int n, bool heightColors = false)
    {
        n = N;
        var px = new byte[N * N * 4];
        Parallel.For(0, N, row =>
        {
            int j = N - 1 - row;
            for (int i = 0; i < N; i++)
            {
                int idx = j * N + i;
                float h = coarse[idx];
                double hx = coarse[j * N + Math.Min(i + 1, N - 1)] - coarse[j * N + Math.Max(i - 1, 0)];
                double hz = coarse[Math.Min(j + 1, N - 1) * N + i] - coarse[Math.Max(j - 1, 0) * N + i];
                // Licht von Nordwesten, überhöht (im groben Raster wirken Hänge sonst zu flach)
                double shade = Math.Clamp(0.85 + (-hx + hz) / (2 * Cell) * 1.6, 0.3, 1.3);
                double r, g, bl;
                if (heightColors)
                {
                    // Höhenstufen: tief grün → braun → grau → weiß
                    double t = Math.Clamp((h - 30) / 190.0, 0, 1);
                    (r, g, bl) = t < 0.15 ? (110, 160, 90) : t < 0.35 ? (160, 170, 105) : t < 0.6 ? (170, 140, 100) : t < 0.8 ? (150, 140, 135) : (235, 235, 240);
                }
                else
                {
                    var c = BiomePalette.Display[BiomeAt((i + 0.5) * Cell, (j + 0.5) * Cell)];
                    (r, g, bl) = (c[0], c[1], c[2]);
                }
                int lake = lakeOf[idx];
                if (lake > 0 && h < LakeList[lake - 1].Level + Lake.WetMargin) { r = 70; g = 120; bl = 200; shade = 1; }
                int o = (row * N + i) * 4;
                px[o] = (byte)Math.Clamp(bl * shade, 0, 255);
                px[o + 1] = (byte)Math.Clamp(g * shade, 0, 255);
                px[o + 2] = (byte)Math.Clamp(r * shade, 0, 255);
                px[o + 3] = 255;
            }
        });
        return px;
    }

    public (double Min, double Max) HeightRange()
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (float h in coarse) { if (h < min) min = h; if (h > max) max = h; }
        return (min, max);
    }

    // ---------------------------------------------------------------- Welt schreiben

    public sealed record Result(string Folder, int Lakes, long WaterArea, int Spawns, double MinHeight, double MaxHeight, TimeSpan Time,
        List<(string Kind, int X, int Z)> Anomalies, int Craters, int Cracks);

    /// Ganze Welt in den (leeren) Ordner schreiben
    public Result Write(string folder, string gameWorlds, IProgress<string> progress)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        BuildCoarse(size <= 8192 ? 2 : 2.5, progress);   // 2 m: Rinnen und Felsstufen der Stempel bleiben sichtbar
        Directory.CreateDirectory(folder);

        progress.Report(Loc.T("Computing heights at full resolution …"));
        var heights = new ushort[(long)size * size];
        FullResolution(heights);
        coarse = null; roughCoarse = null;   // Speicher frei für die nächsten Schritte

        progress.Report(Loc.T("Filling lakes with water …"));
        var water = new byte[(long)size * size];
        long wet = FillWater(heights, water);

        progress.Report(Loc.T("Choosing spawn points …"));
        var spawns = ChooseSpawns(heights, water);

        progress.Report(Loc.F("Writing {0} …", "dtm.raw"));
        using (var fs = File.Create(Path.Combine(folder, "dtm.raw")))
        {
            const int chunk = 1 << 20;
            var bytes = new byte[chunk * 2];
            for (long start = 0; start < heights.LongLength; start += chunk)
            {
                int len = (int)Math.Min(chunk, heights.LongLength - start);
                Buffer.BlockCopy(heights, (int)(start * 2), bytes, 0, len * 2);
                fs.Write(bytes, 0, len * 2);
            }
        }
        double minH = double.MaxValue, maxH = double.MinValue;
        foreach (ushort v in heights) { if (v < minH) minH = v; if (v > maxH) maxH = v; }

        progress.Report(Loc.F("Writing {0} …", "splat4.png"));
        PngWriter.Write(Path.Combine(folder, "splat4.png"), size, size, (row, buf) =>
        {
            long src = (long)(size - 1 - row) * size;   // PNG Zeile 0 = Norden
            Array.Clear(buf);
            for (int x = 0; x < size; x++) buf[x * 4 + 2] = water[src + x];   // RGBA: Blau = Spiegel
        });
        water = null;
        progress.Report(Loc.F("Writing {0} …", "splat3.png"));
        PngWriter.Write(Path.Combine(folder, "splat3.png"), size, size, (row, buf) => Array.Clear(buf));

        progress.Report(Loc.F("Writing {0} …", "biomes.png"));
        int bn = size / 8;
        PngWriter.Write(Path.Combine(folder, "biomes.png"), bn, bn, (row, buf) =>
        {
            for (int x = 0; x < bn; x++)
            {
                var (r, g, b) = BiomePalette.SourceColor(BiomeAt((x + 0.5) * 8, size - (row + 0.5) * 8));
                buf[x * 4] = r; buf[x * 4 + 1] = g; buf[x * 4 + 2] = b; buf[x * 4 + 3] = 255;
            }
        });
        int rn = size / 32;
        PngWriter.Write(Path.Combine(folder, "radiation.png"), rn, rn, (row, buf) => Array.Clear(buf));

        progress.Report(Loc.T("Writing world description …"));
        var utf8Bom = new UTF8Encoding(true);
        File.WriteAllText(Path.Combine(folder, "prefabs.xml"), "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<prefabs>\r\n</prefabs>\r\n", utf8Bom);
        WorldData.WriteSpawnFile(Path.Combine(folder, WorldData.SpawnFile), spawns);
        WorldData.WriteSpawnFile(Path.Combine(folder, "spawnpoints_original.xml"), spawns);
        WorldData.WriteSpawnFile(Path.Combine(folder, WorldData.PlaySpawnFile), spawns);   // derselbe Punkt zum Spielen und Testen
        WorldData.WriteSpawnFile(Path.Combine(folder, WorldData.TestSpawnFile), spawns);
        var (mainTtw, version) = TemplateFiles(gameWorlds);
        File.WriteAllBytes(Path.Combine(folder, "main.ttw"), mainTtw);
        File.WriteAllText(Path.Combine(folder, "map_info.xml"), MapInfo(version), utf8Bom);
        // Lage der Anomalien dazuschreiben, damit man sie im Spiel findet
        File.WriteAllText(Path.Combine(folder, "yamee_generator.txt"), spec.Describe() +
            string.Concat(Anomalies.Select(a => $"Anomaly={a.Kind} at {a.X},{a.Z}\r\n")) + $"Craters={CraterCount}\r\nCracks={CrackCount}\r\n", utf8Bom);
        return new Result(folder, LakeList.Count, wet, spawns.Count, minH / 256, maxH / 256, watch.Elapsed, Anomalies.ToList(), CraterCount, CrackCount);
    }

    /// Grobes Raster bikubisch auf 1 m, dazu feines Rauschen (Buckel), je Biom und Hangneigung verschieden stark, an Seen ruhig
    void FullResolution(ushort[] heights)
    {
        int n = size;
        // Gewichte der Spalten einmal vorrechnen (gleich für alle Zeilen)
        var ix = new int[n * 4];
        var wx = new float[n * 4];
        for (int x = 0; x < n; x++) CubicTaps((x + 0.5) / Cell - 0.5, ix, wx, x * 4);
        // Rauheit je grober Zelle (Biom × Hangneigung × Regler × Ruhe am See)
        var amp = new float[N * N];
        Parallel.For(0, N, j =>
        {
            for (int i = 0; i < N; i++)
            {
                int idx = j * N + i;
                double hx = coarse[j * N + Math.Min(i + 1, N - 1)] - coarse[j * N + Math.Max(i - 1, 0)];
                double hz = coarse[Math.Min(j + 1, N - 1) * N + i] - coarse[Math.Max(j - 1, 0) * N + i];
                double slope = Math.Sqrt(hx * hx + hz * hz) / (2 * Cell);
                amp[idx] = (float)(roughCoarse[idx] * spec.Rough * (1 + 1.8 * Math.Min(slope, 1)) * calm[idx]);
            }
        });
        Parallel.For(0, n, z =>
        {
            Span<int> iz = stackalloc int[4];
            Span<float> wz = stackalloc float[4];
            CubicTaps((z + 0.5) / Cell - 0.5, iz, wz, 0);
            int za = Math.Clamp((int)Math.Floor((z + 0.5) / Cell - 0.5), 0, N - 1), zb = Math.Min(za + 1, N - 1);
            double tz = Math.Clamp((z + 0.5) / Cell - 0.5 - za, 0, 1);
            long row = (long)z * n;
            for (int x = 0; x < n; x++)
            {
                double h = 0;
                int o = x * 4;
                for (int a = 0; a < 4; a++)
                {
                    int r = iz[a] * N;
                    h += wz[a] * (wx[o] * coarse[r + ix[o]] + wx[o + 1] * coarse[r + ix[o + 1]] + wx[o + 2] * coarse[r + ix[o + 2]] + wx[o + 3] * coarse[r + ix[o + 3]]);
                }
                // Rauheit bilinear
                int xa = ix[o + 1], xb = ix[o + 2];
                double tx = Math.Clamp((x + 0.5) / Cell - 0.5 - Math.Floor((x + 0.5) / Cell - 0.5), 0, 1);
                double am = (amp[za * N + xa] * (1 - tx) + amp[za * N + xb] * tx) * (1 - tz) + (amp[zb * N + xa] * (1 - tx) + amp[zb * N + xb] * tx) * tz;
                if (am > 0.01)
                    h += am * (0.6 * detail.Noise(x / 23.0, z / 23.0) + 0.3 * detail.Noise(x / 9.0 + 31.7, z / 9.0) + 0.15 * detail.Noise(x / 4.0, z / 4.0 + 57.1)) * 1.6;
                heights[row + x] = (ushort)Math.Clamp(Math.Round(h * 256), 256 * 2, 65535);
            }
        });
    }

    /// Catmull-Rom: vier Stützstellen und Gewichte für die Stelle f (in Zellen)
    void CubicTaps(double f, Span<int> idx, Span<float> w, int o)
    {
        int i = (int)Math.Floor(f);
        double t = f - i;
        double t2 = t * t, t3 = t2 * t;
        w[o] = (float)(-0.5 * t3 + t2 - 0.5 * t);
        w[o + 1] = (float)(1.5 * t3 - 2.5 * t2 + 1);
        w[o + 2] = (float)(-1.5 * t3 + 2 * t2 + 0.5 * t);
        w[o + 3] = (float)(0.5 * t3 - 0.5 * t2);
        for (int k = 0; k < 4; k++) idx[o + k] = Math.Clamp(i - 1 + k, 0, N - 1);
    }

    /// Wasser: je See von der Mitte aus alle Meter fluten, deren Boden unter Spiegel + WetMargin liegt (nur im Becken),
    /// Blauwert = Spiegel (wie das Krater-Werkzeug; so stehen keine trockenen Büsche im Flachwasser)
    public long SealedCells { get; private set; }

    long FillWater(ushort[] heights, byte[] water)
    {
        long total = 0;
        SealedCells = 0;
        long sealedN = 0;
        for (int k = 0; k < LakeList.Count; k++)
        {
            var l = LakeList[k];
            int id = k + 1;
            double limit = (l.Level + Lake.WetMargin) * 256;
            // Startpunkte: Mitte jeder groben Zelle des Beckens, die deutlich unter dem Spiegel liegt
            // (die Seemitte kann eine Insel sein; Buchten können vom Rest getrennt sein)
            var queue = new Queue<long>();
            int reach = (int)(l.Radius * 2.6 / Cell) + 2, ci0 = (int)(l.X / Cell), cj0 = (int)(l.Z / Cell);
            for (int cj = Math.Max(0, cj0 - reach); cj <= Math.Min(N - 1, cj0 + reach); cj++)
                for (int ci = Math.Max(0, ci0 - reach); ci <= Math.Min(N - 1, ci0 + reach); ci++)
                {
                    if (lakeOf[cj * N + ci] != id) continue;
                    int fx =Math.Min(size - 1, (int)((ci + 0.5) * Cell)), fz = Math.Min(size - 1, (int)((cj + 0.5) * Cell));
                    long s = (long)fz * size + fx;
                    if (water[s] != 0 || heights[s] >= (l.Level - 0.5) * 256) continue;
                    water[s] = (byte)l.Level;
                    queue.Enqueue(s);
                }
            while (queue.Count > 0)
            {
                long p = queue.Dequeue();
                total++;
                int x = (int)(p % size), z = (int)(p / size);
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= size || nz >= size) continue;
                    long q = (long)nz * size + nx;
                    if (water[q] != 0 || heights[q] >= limit) continue;
                    int ci = Math.Min(N - 1, (int)(nx / Cell)), cj = Math.Min(N - 1, (int)(nz / Cell));
                    if (lakeOf[cj * N + ci] != id)
                    {
                        // außerhalb des Beckens und tiefer als der Spiegel: Lücke im Ufer (z. B. Erosionsrinne) – abdichten,
                        // sonst stünde trockenes Land neben dem Wasser unter der Wassergrenze
                        heights[q] = (ushort)Math.Min(65535, limit + 13);
                        sealedN++;
                        continue;
                    }
                    water[q] = (byte)l.Level;
                    queue.Enqueue(q);
                }
            }
        }
        SealedCells = sealedN;
        return total;
    }

    /// Spawnpunkte: flache, trockene Stellen im mittleren Teil der Karte, bevorzugt Kiefernwald, mit Abstand zueinander
    List<SpawnPoint> ChooseSpawns(ushort[] heights, byte[] water)
    {
        // Seit 0.97: nur ein Spawnpunkt genau in der Mitte (Wunsch des Nutzers); die übrigen kommen erst am Schluss
        // (Knopf „Spawn points“ nach Städten, Straßen und Wildnis-POIs, siehe SpawnPlacer)
        long mid = (long)(size / 2) * size + size / 2;
        double level = Math.Max(heights[mid] / 256.0, water[mid] != 0 ? water[mid] + 1 : 0);
        return new List<SpawnPoint> { new SpawnPoint { X = 0, Z = 0, Y = Math.Ceiling(level), Heading = 0 } };
    }

    /// frühere Auswahl mehrerer Spawnpunkte (nicht mehr benutzt)
    List<SpawnPoint> ChooseSpawnsMany(ushort[] heights, byte[] water)
    {
        var list = new List<SpawnPoint>();
        var sr = new Random(spec.SeedValue + 99);
        int half = size / 2;
        double spacing = size / Math.Sqrt(Math.Max(1, spec.SpawnCount)) * 0.55;
        for (int pass = 0; pass < 4 && list.Count < spec.SpawnCount; pass++)
        {
            for (int attempt = 0; attempt < 4000 && list.Count < spec.SpawnCount; attempt++)
            {
                int gx = (int)(size * (0.15 + 0.7 * sr.NextDouble())), gz = (int)(size * (0.15 + 0.7 * sr.NextDouble()));
                int b = BiomeAt(gx, gz);
                if (pass == 0 && b != 0) continue;              // zuerst nur Kiefernwald
                if (pass == 1 && (b == 3 || b == 1)) continue;   // dann ohne Schnee und Brand
                if (list.Any(s => Math.Abs(s.X + half - gx) + Math.Abs(s.Z + half - gz) < spacing)) continue;
                if (!Flat(heights, gx, gz)) continue;
                if (NearWater(water, gx, gz, 40)) continue;
                double h = heights[(long)gz * size + gx] / 256.0;
                list.Add(new SpawnPoint { X = gx - half, Z = gz - half, Y = Math.Ceiling(h), Heading = sr.Next(0, 360) });
            }
            spacing *= 0.7;
        }
        return list;
    }

    bool Flat(ushort[] heights, int gx, int gz)
    {
        if (gx < 3 || gz < 3 || gx >= size - 3 || gz >= size - 3) return false;
        int min = int.MaxValue, max = int.MinValue;
        for (int dz = -3; dz <= 3; dz++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int v = heights[(long)(gz + dz) * size + gx + dx];
                min = Math.Min(min, v); max = Math.Max(max, v);
            }
        return max - min < 256 * 1.2;
    }

    bool NearWater(byte[] water, int gx, int gz, int r)
    {
        for (int dz = -r; dz <= r; dz += 4)
            for (int dx = -r; dx <= r; dx += 4)
            {
                int x = gx + dx, z = gz + dz;
                if (x < 0 || z < 0 || x >= size || z >= size) continue;
                if (water[(long)z * size + x] != 0) return true;
            }
        return false;
    }

    // main.ttw: bis auf die Kennung am Ende bei allen Welten gleich (verglichen: Cezuxoma und East Pozesogu Valley, V 2.6 b14).
    // Vorlage = main.ttw einer vorhandenen Welt im Spiel (passt zur Spielversion), sonst die eingebaute.
    const string TtwTemplate = "dHR3ABYAAAALViAyLjYgKGIxNCkBAAAAAgAAAAYAAAAOAAAAAAAAAAAAAAAAAAAAH4V7QhAAAAAQAAAAEAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD//////////wAAAAA8AAAACgAAAAAAAAAAAAAAAAAAAAAAAADgLgAAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAcAAAAHAAAABAAAAAAAAAAEAAAAAAAAAAQAAAAAAAAABAAAACA=";

    static (byte[] Ttw, string Version) TemplateFiles(string gameWorlds)
    {
        byte[] prefix = Convert.FromBase64String(TtwTemplate);
        string version = "V.2.6.14";
        try
        {
            var dirs = new List<string>();
            if (gameWorlds != null && Directory.Exists(gameWorlds)) dirs.AddRange(Directory.GetDirectories(gameWorlds));
            var newest = dirs.Where(d => File.Exists(Path.Combine(d, "main.ttw")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "main.ttw"))).FirstOrDefault();
            if (newest != null)
            {
                var b = File.ReadAllBytes(Path.Combine(newest, "main.ttw"));
                // Aufbau prüfen: „ttw“, am Ende Länge 32 + 32 Zeichen Kennung
                if (b.Length > 40 && b[0] == 't' && b[1] == 't' && b[2] == 'w' && b[b.Length - 33] == 32) prefix = b[..^32];
                var info = Path.Combine(newest, "map_info.xml");
                if (File.Exists(info))
                {
                    var doc = XmlSafe.Load(info);
                    var v = doc.SelectSingleNode("//property[@name='GameVersion']/@value")?.Value;
                    if (!string.IsNullOrWhiteSpace(v)) version = v;
                }
            }
        }
        catch (Exception) { }   // dann die eingebaute Vorlage
        var id = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N").ToUpperInvariant());
        return ([.. prefix, .. id], version);
    }

    string MapInfo(string version)
    {
        string P(string n, string v) => $"  <property name=\"{n}\" value=\"{System.Security.SecurityElement.Escape(v)}\" />\r\n";
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<MapInfo>\r\n");
        sb.Append(P("Scale", "1"));
        sb.Append(P("HeightMapSize", $"{size},{size}"));
        sb.Append(P("Modes", "Survival,SurvivalSP,SurvivalMP,Creative"));
        sb.Append(P("FixedWaterLevel", "false"));
        sb.Append(P("RandomGeneratedWorld", "true"));
        sb.Append(P("GameVersion", version));
        sb.Append(P("Generation.Seed", spec.Seed ?? ""));
        sb.Append(P("Seed", spec.SeedValue.ToString(CultureInfo.InvariantCulture)));
        sb.Append(P("Generation.Towns", "None"));
        sb.Append(P("Generation.Wilderness", "None"));
        sb.Append(P("Generation.Lakes", spec.Lakes switch { 0 => "None", 1 => "Few", 2 => "Default", _ => "Many" }));
        sb.Append(P("Generation.Rivers", "None"));
        sb.Append(P("Generation.Cracks", "None"));
        sb.Append(P("Generation.Craters", "None"));
        string[] keys = { "Forest", "BurntForest", "Desert", "Snow", "Wasteland" };
        for (int b = 0; b < 5; b++) sb.Append(P("Generation." + keys[b], spec.Shares[b].ToString(CultureInfo.InvariantCulture)));
        sb.Append("</MapInfo>");
        return sb.ToString();
    }
}

/// Perlin-Rauschen (Gradienten auf dem Ganzzahlgitter), Werte etwa −1 … 1
public sealed class Perlin
{
    readonly int[] p = new int[512];

    public Perlin(int seed)
    {
        var r = new Random(seed);
        var a = Enumerable.Range(0, 256).ToArray();
        for (int i = 255; i > 0; i--) { int j = r.Next(i + 1); (a[i], a[j]) = (a[j], a[i]); }
        for (int i = 0; i < 512; i++) p[i] = a[i & 255];
    }

    public double Noise(double x, double y)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double xf = x - xi, yf = y - yi;
        int X = xi & 255, Y = yi & 255;
        double u = Fade(xf), v = Fade(yf);
        int aa = p[p[X] + Y], ab = p[p[X] + Y + 1], ba = p[p[X + 1] + Y], bb = p[p[X + 1] + Y + 1];
        double x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        double x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Lerp(x1, x2, v);
    }

    /// Summe von Oktaven (Frequenz ×2, Stärke ×0,5), normiert auf etwa −1 … 1 (meist ±0,5)
    public double Fbm(double x, double y, int octaves)
    {
        double sum = 0, amp = 1, norm = 0, f = 1;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Noise(x * f + o * 19.19, y * f + o * 7.73);
            norm += amp; amp *= 0.5; f *= 2;
        }
        return sum / norm;
    }

    /// „Ridged multifractal“: scharfe Grate, Werte 0 … 1
    public double Ridged(double x, double y, int octaves)
    {
        double sum = 0, amp = 1, norm = 0, f = 1, weight = 1;
        for (int o = 0; o < octaves; o++)
        {
            double n = 1 - Math.Abs(Noise(x * f + o * 11.3, y * f + o * 3.7));
            n *= n;
            n *= weight;
            weight = Math.Clamp(n * 2, 0, 1);
            sum += n * amp;
            norm += amp; amp *= 0.5; f *= 2;
        }
        return sum / norm;
    }

    static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    static double Grad(int h, double x, double y) => (h & 7) switch
    {
        0 => x + y, 1 => -x + y, 2 => x - y, 3 => -x - y,
        4 => x, 5 => -x, 6 => y, _ => -y,
    };
}

/// Schreibt PNG (RGBA, 8 Bit) Zeile für Zeile – ohne das ganze Bild im Speicher (8192² RGBA wären 256 MB)
public static class PngWriter
{
    static readonly uint[] crcTable = MakeTable();

    public static void Write(string file, int width, int height, Action<int, byte[]> fillRow)
    {
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                fillRow(y, row);
                z.WriteByte(0);   // Filter: keiner
                z.Write(row, 0, row.Length);
            }
        }
        string tmp = file + ".neu";
        using (var fs = File.Create(tmp))
        {
            fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            BeU32(ihdr, 0, (uint)width); BeU32(ihdr, 4, (uint)height);
            ihdr[8] = 8; ihdr[9] = 6;   // 8 Bit, RGBA
            Chunk(fs, "IHDR", ihdr, 0, 13);
            var data = compressed.GetBuffer();
            int len = (int)compressed.Length;
            for (int off = 0; off < len; off += 1 << 20) Chunk(fs, "IDAT", data, off, Math.Min(1 << 20, len - off));
            if (len == 0) Chunk(fs, "IDAT", data, 0, 0);
            Chunk(fs, "IEND", Array.Empty<byte>(), 0, 0);
        }
        File.Move(tmp, file, true);
    }

    static void Chunk(Stream s, string type, byte[] data, int off, int len)
    {
        var head = new byte[8];
        BeU32(head, 0, (uint)len);
        Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        s.Write(head, 0, 8);
        s.Write(data, off, len);
        uint c = 0xFFFFFFFF;
        for (int i = 4; i < 8; i++) c = crcTable[(c ^ head[i]) & 0xFF] ^ (c >> 8);
        for (int i = 0; i < len; i++) c = crcTable[(c ^ data[off + i]) & 0xFF] ^ (c >> 8);
        var tail = new byte[4];
        BeU32(tail, 0, c ^ 0xFFFFFFFF);
        s.Write(tail, 0, 4);
    }

    static void BeU32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
