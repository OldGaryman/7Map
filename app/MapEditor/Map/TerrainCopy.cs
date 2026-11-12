namespace MapEditor.Map;

/// Copy of the pure terrain map in the project folder (the user's request, 2026-11-07): terrain, biomes, water, own roads and
/// buildings as they were before the first cities. "Create cities → Replace" puts it back instead of deleting cities, roads and
/// POIs one by one. Created when a world is generated and before cities are created for the first time (if none exists yet).
/// Spawn points are not part of it (they are saved immediately and stay as the user set them).
public static class TerrainCopy
{
    public const string FolderName = "yamee_terrain";
    static readonly string[] Files = { "dtm.raw", "splat3.png", "splat4.png", "biomes.png", "prefabs.xml" };

    public static string Dir(string project) => Path.Combine(project, FolderName);

    /// is there a complete copy?
    public static bool Exists(string project) => Files.All(f => File.Exists(Path.Combine(Dir(project), f)));

    /// time of the copy (dtm.raw); null = none
    public static DateTime? Time(string project)
    {
        string f = Path.Combine(Dir(project), "dtm.raw");
        return File.Exists(f) ? File.GetLastWriteTime(f) : null;
    }

    /// store the map files of the project (as saved) as the terrain copy; an existing copy is replaced
    public static void Save(string project)
    {
        string dir = Dir(project);
        Directory.CreateDirectory(dir);
        foreach (var f in Files)
        {
            string src = Path.Combine(project, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(dir, f), true);
        }
    }

    /// put the terrain copy back into the project. The files the game created during the last test no longer fit afterwards and
    /// are removed (the game creates them again on the next load).
    public static void Restore(string project)
    {
        string dir = Dir(project);
        foreach (var f in Files) File.Copy(Path.Combine(dir, f), Path.Combine(project, f), true);
        foreach (var f in new[] { "dtm_processed.raw", "splat3_processed.png", "splat4_processed.png", "splat3_half.png", "splat4_half.png", "checksums.txt" })
        {
            string p = Path.Combine(project, f);
            if (File.Exists(p)) File.Delete(p);
        }
    }
}
