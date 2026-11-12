using System.Runtime;

namespace MapEditor;

public static class MemoryTools
{
    /// After big work steps (loading a world, applying a road) gives the memory of the intermediate results back to Windows.
    /// Without this the app holds on to up to 2.5 GB – too much when the game then reads the world in (crash "System out of memory").
    public static void Release()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
