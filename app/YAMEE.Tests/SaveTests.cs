using MapEditor;
using MapEditor.Map;

namespace YameeTests;

/// Tests for applying map changes to a save game (SaveUpdater), for the layouts of 2.6 (+ Undead Legacy) and 3.4
static class SaveTests
{
    /// pairs "project folder|save folder" separated by ";" (optional, real data, only read)
    static IEnumerable<(string Project, string Save)> RealPairs()
    {
        string env = Environment.GetEnvironmentVariable("YAMEE_TEST_SAVES");
        if (string.IsNullOrEmpty(env)) yield break;
        foreach (var p in env.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = p.Split('|');
            if (parts.Length == 2) yield return (parts[0], parts[1]);
        }
    }

    [Test("Save update: an unchanged real world removes (almost) nothing from real saves (YAMEE_TEST_SAVES)")]
    static void RealUnchanged()
    {
        var pairs = RealPairs().ToList();
        if (pairs.Count == 0) Check.Skip("YAMEE_TEST_SAVES not set");
        string game = AppSettings.GuessGameFolder();
        foreach (var (project, saveFolder) in pairs)
        {
            var save = new GameSave(saveFolder, Path.GetFileName(project), Path.GetFileName(saveFolder), DateTime.Now);
            string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(saveFolder)));   // user folder of the save
            string gameWorld = Path.Combine(root, "GeneratedWorlds", Path.GetFileName(project));
            Check.True(SaveUpdater.Incompatibility(save, project, gameWorld) == null, "real save and real project are compatible: " + SaveUpdater.Incompatibility(save, project, gameWorld));
            Check.True(!SaveUpdater.ReferenceLost(save, project), "the remembered map state of the real save is found");
            var d = SaveUpdater.Compare(project, project, save, game);
            Console.WriteLine($"         {Path.GetFileName(saveFolder)}: chunks {d.Chunks.Count}, stored {d.StoredChunks.Count}, trees {d.Trees}/{d.TreesTotal}, multiblocks {d.Multiblocks}/{d.MultiblocksTotal}");
            Check.Equal(0, d.ChangedMeters, "no changed meters for an identical map");
            Check.True(d.Trees * 200L <= Math.Max(1, d.TreesTotal), $"at most 0.5 % of the trees may count as wrong ({d.Trees} of {d.TreesTotal})");
        }
    }

    // ---------- synthetic save (both layouts are the same: region 7rg v1, decoration/multiblocks 17-byte entries) ----------

    static string MakeSave(string dir, int version, params (int Cx, int Cz)[] chunks)
    {
        Directory.CreateDirectory(Path.Combine(dir, "Region"));
        // one region r.0.0 (chunks 0..31): header 4096 bytes, table 4096 bytes, one sector per chunk
        var region = new byte[8192 + 4096 * chunks.Length];
        "7rg"u8.CopyTo(region);
        region[3] = 1;
        for (int i = 0; i < chunks.Length; i++)
        {
            int idx = chunks[i].Cx + chunks[i].Cz * 32, o = 4096 + 4 * idx, sector = 2 + i;
            region[o] = (byte)sector;
            region[o + 3] = 1;
            "ttc"u8.CopyTo(region.AsSpan(sector * 4096 + 16));
        }
        File.WriteAllBytes(Path.Combine(dir, "Region", "r.0.0.7rg"), region);
        File.WriteAllBytes(Path.Combine(dir, "main.ttw"), new byte[] { 0x74, 0x74, 0x77, 0, (byte)version, 0, 0, 0 });
        Directory.CreateDirectory(Path.Combine(dir, "DynamicMeshes"));
        File.WriteAllText(Path.Combine(dir, "DynamicMeshes", "x.dat"), "x");
        return dir;
    }

    static void WriteList(string file, byte version, List<byte[]> entries)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(version);
        ms.Write(BitConverter.GetBytes(entries.Count));
        foreach (var e in entries) ms.Write(e);
        File.WriteAllBytes(file, ms.ToArray());
    }

    // tree: int16 z, y, x (+0x8000), float height, uint16 type at 12, byte 16 = origin
    static byte[] Tree(int x, int y, int z, ushort type, byte origin)
    {
        var e = new byte[17];
        BitConverter.GetBytes((ushort)(z + 0x8000)).CopyTo(e, 0);
        BitConverter.GetBytes((ushort)(y + 0x8000)).CopyTo(e, 2);
        BitConverter.GetBytes((ushort)(x + 0x8000)).CopyTo(e, 4);
        BitConverter.GetBytes(type).CopyTo(e, 12);
        e[16] = origin;
        return e;
    }

    static byte[] Multi(int x, int y, int z)
    {
        var e = new byte[17];
        BitConverter.GetBytes(x).CopyTo(e, 0);
        BitConverter.GetBytes(y).CopyTo(e, 4);
        BitConverter.GetBytes(z).CopyTo(e, 8);
        return e;
    }

    [Test("Save update: changed area resets only its stored chunks, trees and multiblocks; backup, rollback, pointer")]
    static void SyntheticApply()
    {
        if (GameSync.GameRunning()) Check.Skip("the game is running");
        string work = Path.Combine(Fixture.Temp, "savetest");
        Directory.CreateDirectory(work);
        string reference = Fixture.World, project = Path.Combine(work, "project");
        Directory.CreateDirectory(project);
        foreach (var f in Directory.GetFiles(reference)) File.Copy(f, Path.Combine(project, Path.GetFileName(f)), true);
        var w = WorldData.Load(project, null, new Progress<string>());
        int half = w.Size / 2;
        // raise the ground by 3 m on x,z = 0..31 (chunks 0,1 / 0,1 in world chunk numbers, x/16 floor)
        var dtm = File.ReadAllBytes(Path.Combine(project, "dtm.raw"));
        int n = w.Size;
        for (int z = 0; z < 32; z++)
            for (int x = 0; x < 32; x++)
            {
                int i = (z + half) * n + x + half;
                int h = (dtm[2 * i] | dtm[2 * i + 1] << 8) + 3 * 256;
                dtm[2 * i] = (byte)h; dtm[2 * i + 1] = (byte)(h >> 8);
            }
        File.WriteAllBytes(Path.Combine(project, "dtm.raw"), dtm);

        // save: stored chunks (0,0) changed, (1,1) changed, (5,5) not changed. World chunk 0 = region chunk 0 only for a positive start,
        // therefore use chunk numbers 0..1 (region r.0.0)
        string save = MakeSave(Path.Combine(work, "save"), 23, (0, 0), (1, 1), (5, 5));
        int Ground(int x, int z) { int i = (z + half) * n + x + half; return (dtm[2 * i] | dtm[2 * i + 1] << 8) / 256; }
        var trees = new List<byte[]>
        {
            Tree(5, Ground(5, 5) - 2, 5, 1, 0),        // landscape tree on raised ground: it is gone
            Tree(100, Ground(100, 100) + 1, 100, 1, 0),    // untouched
            Tree(8, Ground(8, 8) + 1, 8, 1, 2),            // tree of a building: stays (no building was removed)
        };
        WriteList(Path.Combine(save, "decoration.7dt"), 6, trees);
        WriteList(Path.Combine(save, "multiblocks.7dt"), 7, new List<byte[]> { Multi(3, 40, 3), Multi(90, 40, 90) });

        var gs = new GameSave(save, "Test", "Game", DateTime.Now);
        string game = AppSettings.GuessGameFolder();
        var d = SaveUpdater.Compare(reference, project, gs, game);
        Check.Equal(2, d.StoredChunks.Count, "stored chunks that changed (0,0) and (1,1)");
        Check.True(d.Chunks.Count >= 4, "all chunks of the 32 x 32 m area changed");
        Check.Equal(1, d.Trees, "one tree to remove");
        Check.Equal(1, d.Multiblocks, "one multiblock to remove");

        string before = Check.Hash(Path.Combine(save, "Region", "r.0.0.7rg"));
        string backup = SaveUpdater.Apply(d, project, gs, new Progress<string>());
        Check.True(Directory.Exists(backup) && Check.Hash(Path.Combine(backup, "Region", "r.0.0.7rg")) == before, "backup holds the old region file");
        var region = File.ReadAllBytes(Path.Combine(save, "Region", "r.0.0.7rg"));
        bool Stored(int cx, int cz) { int o = 4096 + 4 * (cx + cz * 32); return (region[o] | region[o + 1] | region[o + 2] | region[o + 3]) != 0; }
        Check.True(!Stored(0, 0) && !Stored(1, 1), "changed chunks reset");
        Check.True(Stored(5, 5), "unchanged chunk kept");
        Check.True(!Directory.Exists(Path.Combine(save, "DynamicMeshes")), "DynamicMeshes deleted");
        Check.Equal(2, BitConverter.ToInt32(File.ReadAllBytes(Path.Combine(save, "decoration.7dt")), 1), "trees left");
        Check.Equal(1, BitConverter.ToInt32(File.ReadAllBytes(Path.Combine(save, "multiblocks.7dt")), 1), "multiblocks left");
        var again = SaveUpdater.Compare(SaveUpdater.ReferenceOf(gs), project, gs, game);
        Check.Equal(0, again.Chunks.Count, "afterwards the save runs with this map - nothing more to do");
    }

    static string CopyWorld(string name)
    {
        string dir = Path.Combine(Fixture.Temp, "savetest", name);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(Fixture.World)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
        return dir;
    }

    [Test("Save update: water without a height change (lake, filled crater) reaches the stored chunks")]
    static void WaterOnly()
    {
        string project = CopyWorld("waterproject");
        var w = WorldData.Load(project, null, new Progress<string>());
        int n = w.Size, half = n / 2;
        PngWriter.Write(Path.Combine(project, "splat4.png"), n, n, (y, row) =>
        {
            Array.Clear(row);
            int z = n - 1 - y - half;   // image row 0 = north
            for (int x = -half; x < half; x++)
            {
                byte level = w.WaterMask[(z + half) * n + x + half];   // keep the existing lakes
                if (z >= 0 && z < 32 && x >= 0 && x < 32 && level == 0) level = 30;
                row[4 * (x + half) + 2] = level; row[4 * (x + half) + 3] = 255;   // PNG order RGBA: blue = byte 2
            }   // blue = water level, BGRA
        });
        string save = MakeSave(Path.Combine(Fixture.Temp, "savetest", "watersave"), 23, (0, 0), (5, 5));
        var d = SaveUpdater.Compare(Fixture.World, project, new GameSave(save, "Test", "G", DateTime.Now), AppSettings.GuessGameFolder());
        Check.True(d.ChangedMeters > 0 && d.ChangedMeters <= 1024, "only the new water counts: " + d.ChangedMeters);
        Check.Equal(1, d.StoredChunks.Count, "stored chunk (0,0) is affected, (5,5) is not");
    }

    [Test("Save update: pointer to a moved project is found again by its id; a lost state is reported")]
    static void MovedPointer()
    {
        string work = Path.Combine(Fixture.Temp, "savetest", "pointer");
        string project = Path.Combine(work, "NewPlace", "Project");
        Directory.CreateDirectory(project);
        foreach (var f in Directory.GetFiles(Fixture.World)) File.Copy(f, Path.Combine(project, Path.GetFileName(f)), true);
        string state = SaveUpdater.StoreState(project, project);
        string save = MakeSave(Path.Combine(work, "save"), 23);
        var gs = new GameSave(save, "Test", "G", DateTime.Now);
        Check.True(SaveUpdater.ReferenceOf(gs, project) == null && !SaveUpdater.ReferenceLost(gs, project), "no pointer: nothing remembered, nothing lost");
        SaveUpdater.SetReference(gs, Path.Combine(work, "OldPlace", "Project", "yamee_states", Path.GetFileName(state)));
        Check.Equal(state, SaveUpdater.ReferenceOf(gs, project), "state found again by its id");
        Check.True(!SaveUpdater.ReferenceLost(gs, project), "not lost");
        SaveUpdater.SetReference(gs, Path.Combine(work, "OldPlace", "yamee_states", "0123456789AB"));
        Check.True(SaveUpdater.ReferenceOf(gs, project) == null && SaveUpdater.ReferenceLost(gs, project), "unknown id: lost, no guessing");
    }

    [Test("Save update: a failure in the middle restores the save from the backup")]
    static void RollbackOnFailure()
    {
        if (GameSync.GameRunning()) Check.Skip("the game is running");
        string work = Path.Combine(Fixture.Temp, "savetest", "rollback");
        string reference = Fixture.World, project = CopyWorld("rollbackproject");
        var w = WorldData.Load(project, null, new Progress<string>());
        int n = w.Size, half = n / 2;
        var dtm = File.ReadAllBytes(Path.Combine(project, "dtm.raw"));
        for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++) { int i = (z + half) * n + x + half; int h = (dtm[2 * i] | dtm[2 * i + 1] << 8) + 768; dtm[2 * i] = (byte)h; dtm[2 * i + 1] = (byte)(h >> 8); }
        File.WriteAllBytes(Path.Combine(project, "dtm.raw"), dtm);
        string save = MakeSave(work, 23, (0, 0));
        WriteList(Path.Combine(save, "decoration.7dt"), 6, new List<byte[]> { Tree(100, 100, 100, 1, 0) });
        WriteList(Path.Combine(save, "multiblocks.7dt"), 7, new List<byte[]> { Multi(3, 40, 3) });
        var gs = new GameSave(save, "Test", "G", DateTime.Now);
        var d = SaveUpdater.Compare(reference, project, gs, AppSettings.GuessGameFolder());
        Check.Equal(1, d.StoredChunks.Count, "one stored chunk to reset");
        string region = Path.Combine(save, "Region", "r.0.0.7rg"), deco = Path.Combine(save, "decoration.7dt");
        string regionBefore = Check.Hash(region), decoBefore = Check.Hash(deco);
        // break the multiblock list after the comparison: the region is already reset when the error comes
        File.WriteAllBytes(Path.Combine(save, "multiblocks.7dt"), new byte[] { 99, 1, 0, 0, 0 });
        bool failed = false;
        try { SaveUpdater.Apply(d, project, gs, new Progress<string>()); }
        catch (InvalidDataException) { failed = true; }
        Check.True(failed, "Apply fails on the broken list");
        Check.Equal(regionBefore, Check.Hash(region), "region file restored");
        Check.Equal(decoBefore, Check.Hash(deco), "decoration list restored");
        Check.True(Directory.Exists(Path.Combine(save, "DynamicMeshes")), "DynamicMeshes still there");
        Check.True(SaveUpdater.ReferenceOf(gs, project) == null, "no new map state remembered after a failure");
    }

    static byte[] Ttw(int fileVersion, string text)
    {
        var b = new List<byte> { 0x74, 0x74, 0x77, 0 };
        b.AddRange(BitConverter.GetBytes(fileVersion));
        b.Add((byte)text.Length);
        b.AddRange(System.Text.Encoding.ASCII.GetBytes(text));
        b.AddRange(new byte[40]);
        return b.ToArray();
    }

    [Test("Save update: a save of another game version or of another user folder is refused")]
    static void Incompatible()
    {
        string work = Path.Combine(Fixture.Temp, "savetest", "incompat");
        string project = Path.Combine(work, "project");
        new WorldGenerator(Fixture.Spec("Incompat")).Write(project, null, new Progress<string>(), new GameVersion(3, 40, 10));
        string vanilla = Path.Combine(work, "7DaysToDie"), ul = Path.Combine(vanilla, "UndeadLegacy");
        string save26 = MakeSave(Path.Combine(ul, "Saves", "W", "G"), 22);
        File.WriteAllBytes(Path.Combine(save26, "main.ttw"), Ttw(22, "V 2.6 (b14)"));
        string save34 = MakeSave(Path.Combine(vanilla, "Saves", "W", "G"), 23);
        File.WriteAllBytes(Path.Combine(save34, "main.ttw"), Ttw(23, "V 3.4.0 (b10)"));
        string worldVanilla = Path.Combine(vanilla, "GeneratedWorlds", "W"), worldUl = Path.Combine(ul, "GeneratedWorlds", "W");
        var s26 = new GameSave(save26, "W", "G", DateTime.Now);
        var s34 = new GameSave(save34, "W", "G", DateTime.Now);
        Check.True(SaveUpdater.Incompatibility(s26, project, worldUl) != null, "2.6 save with a 3.4 map: refused");
        Check.True(SaveUpdater.Incompatibility(s34, project, worldVanilla) == null, "3.4 save with a 3.4 map: fine");
        Check.True(SaveUpdater.Incompatibility(s34, project, worldUl) != null, "3.4 save, map goes to the Undead Legacy folder: refused");
        Check.Equal(1, SaveUpdater.FindSaves("W", worldVanilla).Count, "only the saves of this user folder are listed");
        Check.Equal(save34, SaveUpdater.FindSaves("W", worldVanilla)[0].Folder, "the right one");
    }
    [Test("Save update: real saves - trees that no longer fit the changed map (e.g. on new roads) are removed on a copy (YAMEE_TEST_SAVES)")]
    static void RealApplyOnCopy()
    {
        var pairs = RealPairs().ToList();
        if (pairs.Count == 0) Check.Skip("YAMEE_TEST_SAVES not set");
        if (GameSync.GameRunning()) Check.Skip("the game is running");
        string game = AppSettings.GuessGameFolder();
        int index = 0;
        foreach (var (realProject, realSave) in pairs)
        {
            // everything on copies: the real project and the real save are never changed
            string work = Path.Combine(Fixture.Temp, "savereal" + ++index);
            string project = Path.Combine(work, Path.GetFileName(realProject)), save = Path.Combine(work, "save");
            Directory.CreateDirectory(project);
            foreach (var f in Directory.GetFiles(realProject)) File.Copy(f, Path.Combine(project, Path.GetFileName(f)), true);
            CopyTree(realSave, save);
            var gs = new GameSave(save, Path.GetFileName(project), Path.GetFileName(save), DateTime.Now);
            var d = SaveUpdater.Compare(project, project, gs, game);
            int treesBefore = d.TreesTotal, drop = d.Trees;
            SaveUpdater.Apply(d, project, gs, new Progress<string>());
            int treesAfter = BitConverter.ToInt32(File.ReadAllBytes(Path.Combine(save, "decoration.7dt")), 1);
            Console.WriteLine($"         {Path.GetFileName(realProject)}: trees {treesBefore} -> {treesAfter} (removed {drop})");
            Check.Equal(treesBefore - drop, treesAfter, "exactly the flagged trees are gone");
            var again = SaveUpdater.Compare(SaveUpdater.ReferenceOf(gs, project), project, gs, game);
            Check.Equal(0, again.Trees, "no tree left that does not fit the map");
            Check.Equal(0, again.Chunks.Count, "nothing more to do");
        }
    }

    static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var dir in Directory.GetDirectories(src)) CopyTree(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }
}
