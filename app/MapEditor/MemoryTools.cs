using System.Runtime;

namespace MapEditor;

public static class MemoryTools
{
    /// Gibt nach großen Arbeitsschritten (Welt laden, Straße übernehmen) den Speicher der Zwischenergebnisse an Windows zurück.
    /// Ohne das hält die App bis zu 2,5 GB fest – zu viel, wenn danach das Spiel die Welt einliest (Absturz „System out of memory“).
    public static void Release()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
