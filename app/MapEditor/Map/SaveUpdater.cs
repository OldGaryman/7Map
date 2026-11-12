namespace MapEditor.Map;

/// A save game of a world (folder Saves\&lt;world&gt;\&lt;game&gt; with main.ttw)
public sealed record GameSave(string Folder, string World, string Game, DateTime LastPlayed)
{
    public override string ToString() => Game;
}

/// result of the comparison: affected chunks (16 × 16 m), of them saved in the save game, trees and multiblock objects
public sealed class SaveDiff
{
    public HashSet<long> Chunks { get; } = new();
    public HashSet<long> StoredChunks { get; } = new();
    public int ChangedMeters, Trees, TreesTotal, Multiblocks, MultiblocksTotal;
    public List<string> Notes { get; } = new();
    internal bool[] Bad;   // per meter: new road/new building/biome changed or ground changed by more than the limit (trees gone)
    internal int Size;
    // check of every tree against the project state itself (also catches remains of earlier takeovers): roads, footprints now
    // and of removed buildings per meter, ground height, biome per meter (color) and per biome the tree species that really grow there
    internal byte[] Roads, Heights;
    internal bool[] Footprint, OldFootprint;
    internal Func<int, int, int> BiomeAt;
    internal HashSet<(int Biome, int Type)> Native;

    public static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
    public static (int Cx, int Cz) FromKey(long k) => ((int)(k >> 32), (int)(uint)k);
}

/// Apply map changes to an existing save game (the user's request, 2026-10-30; like tools/save_update.ps1).
/// Saved chunks are never generated again from the map by the game – therefore: reset all saved chunks with changes
/// (entry in the region file to 0), remove trees (decoration.7dt) on new roads/buildings, a changed biome or clearly
/// changed ground, remove multiblock objects (multiblocks.7dt) in reset chunks, delete DynamicMeshes.
/// It is compared with the map state with which the save game last ran – without a choice by the user (the user's request
/// 2026-10-30): map states lie once in the project (yamee_states\&lt;id&gt;), the save game refers to them
/// (yamee_mapstate.txt). On "Copy to 7 Days to Die" save games without a reference get the previous state in the game
/// (so they ran with it); a save game without a reference ran with the map that currently lies in the game.
public static class SaveUpdater
{
    const string PointerFile = "yamee_mapstate.txt", StatesFolder = "yamee_states";
    static readonly string[] ReferenceFiles = { "dtm.raw", "splat3.png", "biomes.png", "prefabs.xml" };

    /// folder with the backups of whole save games (before every "Apply to save game", one full copy each time)
    public static string BackupFolder => AppSettings.SaveBackupFolder;

    /// number and size of the existing backups: they grow without limit (70 MB to several GB per save game) and are never
    /// deleted automatically – the user decides (audit finding F39)
    public static (int Count, long Bytes) BackupSummary()
    {
        if (!Directory.Exists(BackupFolder)) return (0, 0);
        var dirs = Directory.GetDirectories(BackupFolder);
        long bytes = 0;
        foreach (var d in dirs)
            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)) bytes += new FileInfo(f).Length;
        return (dirs.Length, bytes);
    }

    /// save games of a world in all user folders of the game (also mods like Undead Legacy and dedicated server)
    public static List<GameSave> FindSaves(string worldName)
    {
        var roots = new List<string>();
        string user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie");
        if (Directory.Exists(user))
        {
            roots.Add(Path.Combine(user, "Saves"));
            roots.AddRange(Directory.GetDirectories(user).Select(d => Path.Combine(d, "Saves")));
        }
        foreach (var g in GameLocator.WorldFolders())   // …\GeneratedWorlds → Saves beside it
            roots.Add(Path.Combine(Path.GetDirectoryName(g), "Saves"));
        var list = new List<GameSave>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string dir = Path.Combine(root, worldName);
            if (!Directory.Exists(dir)) continue;
            foreach (var game in Directory.GetDirectories(dir))
            {
                string ttw = Path.Combine(game, "main.ttw");
                if (File.Exists(ttw)) list.Add(new GameSave(game, worldName, Path.GetFileName(game), File.GetLastWriteTime(ttw)));
            }
        }
        return list.OrderByDescending(s => s.LastPlayed).ToList();
    }

    /// folder with a map state (dtm.raw, splat3.png, biomes.png, prefabs.xml)?
    public static bool IsMapFolder(string folder) => folder != null && ReferenceFiles.Take(2).All(f => File.Exists(Path.Combine(folder, f)));

    /// map state with which the save game last ran (remembered); null = none remembered
    public static string ReferenceOf(GameSave s)
    {
        string pointer = Path.Combine(s.Folder, PointerFile);
        if (File.Exists(pointer))
        {
            string folder = File.ReadAllText(pointer).Trim();
            if (IsMapFolder(folder)) return folder;
        }
        // first version (2026-10-30) stored the state as a copy in the save game (e.g. FHo76): still valid
        string old = Path.Combine(s.Folder, OldReferenceFolder);
        return IsMapFolder(old) ? old : null;
    }
    const string OldReferenceFolder = "yamee_reference";

    public static DateTime? ReferenceTime(GameSave s)
    {
        string pointer = Path.Combine(s.Folder, PointerFile), old = Path.Combine(s.Folder, OldReferenceFolder);
        return File.Exists(pointer) ? File.GetLastWriteTime(pointer) : Directory.Exists(old) ? Directory.GetLastWriteTime(old) : null;
    }

    /// do two folders have the same map state?
    public static bool SameMap(string a, string b) => ReferenceFiles.All(f => FileHash(Path.Combine(a, f)) == FileHash(Path.Combine(b, f)));

    /// store the map state from mapFolder once in the project (same files = same folder); returns the folder
    public static string StoreState(string project, string mapFolder)
    {
        string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Concat(ReferenceFiles.Select(f => FileHash(Path.Combine(mapFolder, f))))))).Substring(0, 12);
        string dir = Path.Combine(project, StatesFolder, id);
        if (IsMapFolder(dir)) return dir;
        string tmp = dir + ".neu";
        Directory.CreateDirectory(tmp);
        foreach (var f in ReferenceFiles)
        {
            string src = Path.Combine(mapFolder, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(tmp, f), true);
        }
        Directory.Move(tmp, dir);
        return dir;
    }

    public static void SetReference(GameSave s, string stateFolder) => File.WriteAllText(Path.Combine(s.Folder, PointerFile), stateFolder);

    /// before overwriting the map in the game: remember the previous state in the game for save games of this world without a reference
    public static int RememberGameStateForSaves(string project, string worldName, string gameWorld)
    {
        if (gameWorld == null || !IsMapFolder(gameWorld)) return 0;
        var without = FindSaves(worldName).Where(s => ReferenceOf(s) == null).ToList();
        if (without.Count == 0) return 0;
        string state = StoreState(project, gameWorld);
        foreach (var s in without) SetReference(s, state);
        return without.Count;
    }

    static string FileHash(string file)
    {
        if (!File.Exists(file)) return "-";
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
    }

    /// differences between the starting state (reference) and the project; treeHeight = ground change from which trees must go (m)
    public static SaveDiff Compare(string reference, string project, GameSave save, string gameFolder, double treeHeight = 0.5)
    {
        int n = (int)Math.Round(Math.Sqrt(new FileInfo(Path.Combine(project, "dtm.raw")).Length / 2.0)), half = n / 2;
        if (new FileInfo(Path.Combine(reference, "dtm.raw")).Length != new FileInfo(Path.Combine(project, "dtm.raw")).Length)
            throw new InvalidDataException(Loc.T("The reference map has a different size than the project."));
        var d = new SaveDiff { Size = n, Bad = new bool[n * n] };
        var a = File.ReadAllBytes(Path.Combine(reference, "dtm.raw"));
        var b = File.ReadAllBytes(Path.Combine(project, "dtm.raw"));
        var ra = Roads(Path.Combine(reference, "splat3.png"), n);
        var rb = Roads(Path.Combine(project, "splat3.png"), n);
        int lim = (int)(treeHeight * 256);
        void Mark(int x, int z, bool bad)
        {
            if (x < -half || z < -half || x >= half || z >= half) return;
            d.Chunks.Add(SaveDiff.Key((int)Math.Floor(x / 16.0), (int)Math.Floor(z / 16.0)));
            if (bad) d.Bad[(z + half) * n + x + half] = true;
        }
        // heights and own roads meter by meter
        for (int i = 0; i < n * n; i++)
        {
            int ha = a[2 * i] | a[2 * i + 1] << 8, hb = b[2 * i] | b[2 * i + 1] << 8;
            if (ha == hb && ra[i] == rb[i]) continue;
            d.ChangedMeters++;
            Mark(i % n - half, i / n - half, Math.Abs(ha - hb) > lim || rb[i] != 0 && ra[i] == 0);
        }
        // biomes (8 m per pixel): changed = new vegetation, old trees gone
        string ba = Path.Combine(reference, "biomes.png"), bb = Path.Combine(project, "biomes.png");
        if (File.Exists(bb))
        {
            var kp = BiomeIndex(bb);
            int mp = n / kp.Size;
            d.BiomeAt = (x, z) => kp.Px[(half - 1 - z) / mp * kp.Size + (x + half) / mp];
        }
        if (File.Exists(ba) && File.Exists(bb))
        {
            var ka = BiomeIndex(ba); var kb = BiomeIndex(bb);
            if (ka.Size == kb.Size)
            {
                int m = n / ka.Size, changed = 0;
                for (int i = 0; i < ka.Px.Length; i++)
                {
                    if (ka.Px[i] == kb.Px[i]) continue;
                    changed++;
                    int bx = i % ka.Size, by = i / ka.Size;
                    for (int dz = 0; dz < m; dz++)
                        for (int dx = 0; dx < m; dx++) Mark(bx * m + dx - half, half - 1 - (by * m + dz), true);
                }
                if (changed > 0) d.Notes.Add(Loc.F("{0} biome pixel(s) changed", changed));
            }
        }
        // buildings: new, deleted, moved, rotated (with driveways); new footprint = trees gone
        string pa = Path.Combine(reference, "prefabs.xml"), pb = Path.Combine(project, "prefabs.xml");
        if (File.Exists(pa) && File.Exists(pb))
        {
            static string Key(PlacedPrefab p) => $"{p.Name.ToLowerInvariant()}|{p.X}|{p.Y}|{p.Z}|{p.Rotation}";
            var then = WorldData.ParsePrefabs(pa);
            var now = WorldData.ParsePrefabs(pb);
            var thenKeys = new HashSet<string>(then.Select(Key));
            var nowKeys = new HashSet<string>(now.Select(Key));
            var removed = then.Where(p => !nowKeys.Contains(Key(p))).ToList();
            var added = now.Where(p => !thenKeys.Contains(Key(p))).ToList();
            var defs = PrefabCatalog.Load(gameFolder, removed.Concat(now).Select(p => p.Name));
            // without the building descriptions of the game every building would be 20 × 20 m: wrong areas, so better abort
            // (audit finding F4)
            var unknown = removed.Concat(added).Where(p => !defs[p.Name].Found && defs[p.Name].Kind != PrefabKind.Tile).Select(p => p.Name).Distinct().ToList();
            if (unknown.Count > 0)
                throw new InvalidDataException(Loc.F("The size of {0} building(s) is unknown (e.g. {1}) – the game folder was not found or is incomplete. Please check the game folder in the settings.",
                    unknown.Count, unknown[0]));
            foreach (var (p, isNew) in removed.Select(p => (p, false)).Concat(added.Select(p => (p, true))))
            {
                p.Def = defs[p.Name];
                for (int z = p.Z - 1; z <= p.Z + p.SizeZ; z++)
                    for (int x = p.X - 1; x <= p.X + p.SizeX; x++) Mark(x, z, isNew);
            }
            if (removed.Count + added.Count > 0) d.Notes.Add(Loc.F("{0} building(s)/part(s) added, {1} removed or moved", added.Count, removed.Count));
            // footprints of all buildings, tiles and parts in the project (no landscape grows there, own trees may stay)
            // and of the removed/moved ones (their own trees must go)
            d.Footprint = new bool[n * n];
            d.OldFootprint = new bool[n * n];
            foreach (var (p, mask) in now.Select(p => (p, d.Footprint)).Concat(removed.Select(p => (p, d.OldFootprint))))
            {
                p.Def = defs[p.Name];
                if (!p.Def.Found) continue;   // size unknown
                for (int z = Math.Max(p.Z, -half); z < Math.Min(p.Z + p.SizeZ, half); z++)
                    for (int x = Math.Max(p.X, -half); x < Math.Min(p.X + p.SizeX, half); x++) mask[(z + half) * n + x + half] = true;
            }
        }
        d.Roads = rb;
        d.Heights = b;
        // tree species per biome from the save game itself: a species belongs in a biome if at least 5 % of all trees
        // of this species stand there (otherwise remains of an earlier biome or edge tree at a biome border)
        string decoFile = Path.Combine(save.Folder, "decoration.7dt");
        if (d.BiomeAt != null && File.Exists(decoFile))
        {
            var deco = File.ReadAllBytes(decoFile);
            var count = new Dictionary<(int, int), int>();
            var total = new Dictionary<int, int>();
            for (int k = 0, cnt = BitConverter.ToInt32(deco, 1); k < cnt && 5 + 17 * k + 17 <= deco.Length; k++)
            {
                var (x, z, type) = Tree(deco, 5 + 17 * k);
                if (x < -half || z < -half || x >= half || z >= half || FromPrefab(deco, 5 + 17 * k)) continue;
                var key = (d.BiomeAt(x, z), type);
                count[key] = count.GetValueOrDefault(key) + 1;
                total[type] = total.GetValueOrDefault(type) + 1;
            }
            d.Native = count.Where(kv => kv.Value * 20 >= total[kv.Key.Item2]).Select(kv => kv.Key).ToHashSet();
        }
        // chunks with trees that must go: an already generated chunk contains the tree as a block, so reset it too
        if (File.Exists(decoFile))
        {
            var deco = File.ReadAllBytes(decoFile);
            for (int k = 0, cnt = BitConverter.ToInt32(deco, 1); k < cnt && 5 + 17 * k + 17 <= deco.Length; k++)
                if (TreeBad(d, deco, 5 + 17 * k))
                {
                    var (x, z, _) = Tree(deco, 5 + 17 * k);
                    d.Chunks.Add(SaveDiff.Key((int)Math.Floor(x / 16.0), (int)Math.Floor(z / 16.0)));
                }
        }
        // of that, what is saved in the save game
        foreach (var (file, rx, rz) in RegionFiles(save.Folder))
        {
            using var fs = File.OpenRead(file);
            var table = new byte[4096];
            fs.Position = 4096;
            fs.ReadExactly(table);
            for (int i = 0; i < 1024; i++)
            {
                int cx = rx * 32 + i % 32, cz = rz * 32 + i / 32, o = 4 * i;
                if ((table[o] | table[o + 1] | table[o + 2] | table[o + 3]) != 0 && d.Chunks.Contains(SaveDiff.Key(cx, cz)))
                    d.StoredChunks.Add(SaveDiff.Key(cx, cz));
            }
        }
        (d.TreesTotal, d.Trees) = CountList(Path.Combine(save.Folder, "decoration.7dt"), (bytes, o) => TreeBad(d, bytes, o));
        (d.MultiblocksTotal, d.Multiblocks) = CountList(Path.Combine(save.Folder, "multiblocks.7dt"), (bytes, o) => MultiblockBad(d, bytes, o));
        return d;
    }

    /// change the save game (full backup beforehand) and remember the project state as the new starting state. Returns the backup folder.
    public static string Apply(SaveDiff d, string project, GameSave save, IProgress<string> progress)
    {
        if (GameSync.GameRunning()) throw new InvalidOperationException(Loc.T("The game is still running. Please quit 7 Days to Die first."));
        progress.Report(Loc.T("Backing up the save …"));
        string backup = Path.Combine(BackupFolder, $"{save.World}_{save.Game}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
        CopyDirectory(save.Folder, backup);
        progress.Report(Loc.T("Resetting changed areas …"));
        foreach (var (file, rx, rz) in RegionFiles(save.Folder))
        {
            var bytes = File.ReadAllBytes(file);
            bool changed = false;
            for (int i = 0; i < 1024; i++)
            {
                int o = 4096 + 4 * i;
                if (!d.StoredChunks.Contains(SaveDiff.Key(rx * 32 + i % 32, rz * 32 + i / 32))) continue;
                bytes[o] = bytes[o + 1] = bytes[o + 2] = bytes[o + 3] = 0;
                changed = true;
            }
            if (changed) File.WriteAllBytes(file, bytes);
        }
        progress.Report(Loc.T("Removing trees and objects …"));
        FilterList(Path.Combine(save.Folder, "decoration.7dt"), (bytes, o) => TreeBad(d, bytes, o));
        FilterList(Path.Combine(save.Folder, "multiblocks.7dt"), (bytes, o) => MultiblockBad(d, bytes, o));
        string meshes = Path.Combine(save.Folder, "DynamicMeshes");
        if (Directory.Exists(meshes)) Directory.Delete(meshes, true);   // distant view, the game rebuilds it (it is in the backup)
        progress.Report(Loc.T("Remembering the map state …"));
        SetReference(save, StoreState(project, project));
        return backup;
    }

    // ---------- Files of the save game ----------

    // Region\r.X.Z.7rg: from byte 4096 4 bytes per chunk (3 bytes sector, 1 byte count), index = x + z·32 within the region
    static IEnumerable<(string File, int Rx, int Rz)> RegionFiles(string save)
    {
        string dir = Path.Combine(save, "Region");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.GetFiles(dir, "r.*.7rg"))
        {
            var p = Path.GetFileNameWithoutExtension(f).Split('.');
            if (p.Length == 3 && int.TryParse(p[1], out int rx) && int.TryParse(p[2], out int rz) && new FileInfo(f).Length >= 8192) yield return (f, rx, rz);
        }
    }

    // decoration.7dt: 1 byte version, int32 count, 17 bytes each; position int16 z, y, x with offset 0x8000 (z first! checked
    // 2026-10-30: this way 99.7 % of the trees stand exactly on the ground, the other way round only 14 %), tree species uint16 from byte 12
    static (int X, int Z, int Type) Tree(byte[] b, int o) =>
        (BitConverter.ToUInt16(b, o + 4) - 0x8000, BitConverter.ToUInt16(b, o) - 0x8000, BitConverter.ToUInt16(b, o + 12));

    // byte 16 = 2: tree belongs to a building/tile (part of the prefab), 0 = tree of the landscape
    // (checked 2026-10-30 on the unchanged world East Pozesogu: in footprints only 2, outside almost only 0)
    static bool FromPrefab(byte[] b, int o) => b[o + 16] == 2;

    static bool TreeBad(SaveDiff d, byte[] b, int o)
    {
        var (x, z, type) = Tree(b, o);
        int half = d.Size / 2;
        if (x < -half || z < -half || x >= half || z >= half) return false;
        int i = (z + half) * d.Size + x + half;
        // trees of a building stay as long as the building stands there; the game sets them again with the building. Gone only in
        // the footprint of a removed/moved building (in the original world such trees also stand up to 50 m beside
        // every footprint, e.g. groups near cities – therefore no check "far away from buildings")
        if (FromPrefab(b, o))
            return d.OldFootprint != null && d.OldFootprint[i];
        if (d.Footprint != null && d.Footprint[i]) return true;   // landscape tree in building, tile or part
        if (d.Roads != null && d.Roads[i] != 0) return true;   // on road
        if (d.Heights != null)
        {
            double dh = (BitConverter.ToUInt16(b, o + 2) - 0x8000) - (d.Heights[2 * i] | d.Heights[2 * i + 1] << 8) / 256.0;
            if (dh < -0.5 || dh >= 2.5) return true;   // floats or sticks in the ground
        }
        // does not belong in this biome: at natural biome borders trees stand up to 7 m in the neighboring biome, so foreign only from 8 m
        if (d.Native != null && !d.Native.Contains((d.BiomeAt(x, z), type)))
        {
            bool near = false;
            for (int dz = -8; dz <= 8 && !near; dz += 2)
                for (int dx = -8; dx <= 8 && !near; dx += 2)
                    if (x + dx >= -half && z + dz >= -half && x + dx < half && z + dz < half && d.Native.Contains((d.BiomeAt(x + dx, z + dz), type))) near = true;
            if (!near) return true;
        }
        for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int xx = x + dx + half, zz = z + dz + half;
                if (xx >= 0 && zz >= 0 && xx < d.Size && zz < d.Size && d.Bad[zz * d.Size + xx]) return true;
            }
        return false;
    }

    // multiblocks.7dt: same structure, position int32 x, y, z; gone if the chunk is reset
    static bool MultiblockBad(SaveDiff d, byte[] b, int o) =>
        d.StoredChunks.Contains(SaveDiff.Key((int)Math.Floor(BitConverter.ToInt32(b, o) / 16.0), (int)Math.Floor(BitConverter.ToInt32(b, o + 8) / 16.0)));

    static (int Total, int Drop) CountList(string file, Func<byte[], int, bool> drop)
    {
        if (!File.Exists(file)) return (0, 0);
        var b = File.ReadAllBytes(file);
        int n = BitConverter.ToInt32(b, 1);
        if (5 + 17L * n != b.Length) throw new InvalidDataException(Loc.F("{0} has an unexpected format.", Path.GetFileName(file)));
        int dropped = 0;
        for (int k = 0; k < n; k++) if (drop(b, 5 + 17 * k)) dropped++;
        return (n, dropped);
    }

    static void FilterList(string file, Func<byte[], int, bool> drop)
    {
        if (!File.Exists(file)) return;
        var b = File.ReadAllBytes(file);
        int n = BitConverter.ToInt32(b, 1);
        if (5 + 17L * n != b.Length) throw new InvalidDataException(Loc.F("{0} has an unexpected format.", Path.GetFileName(file)));
        using var ms = new MemoryStream();
        ms.WriteByte(b[0]);
        ms.Write(new byte[4]);
        int kept = 0;
        for (int k = 0; k < n; k++)
        {
            int o = 5 + 17 * k;
            if (drop(b, o)) continue;
            ms.Write(b, o, 17);
            kept++;
        }
        var outBytes = ms.ToArray();
        BitConverter.GetBytes(kept).CopyTo(outBytes, 1);
        File.WriteAllBytes(file, outBytes);
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var dir in Directory.GetDirectories(src)) CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    // ---------- Map files ----------

    // own roads from splat3.png per meter (row 0 = south like dtm.raw): 0 none, 1 gravel, 2 asphalt
    static byte[] Roads(string png, int n)
    {
        var r = new byte[n * n];
        PngTools.ReadMask(png, n, r, (b, g, red) => red > 0 && red >= g ? WorldData.Asphalt : g > 0 ? WorldData.Gravel : (byte)0);
        return r;
    }

    static (int Size, int[] Px) BiomeIndex(string png)
    {
        var src = PngTools.Open(png);
        var px = new byte[src.PixelWidth * src.PixelHeight * 4];
        src.CopyPixels(px, src.PixelWidth * 4, 0);
        var k = new int[src.PixelWidth * src.PixelHeight];
        for (int i = 0; i < k.Length; i++) k[i] = px[4 * i] | px[4 * i + 1] << 8 | px[4 * i + 2] << 16;
        return (src.PixelWidth, k);
    }
}
