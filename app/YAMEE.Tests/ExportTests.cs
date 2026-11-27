using System.Windows.Media;
using System.Windows.Media.Imaging;
using MapEditor;
using MapEditor.Map;

namespace YameeTests;

/// PNG export of the map image (2026-11-20: smaller files)
static class ExportTests
{
    [Test("Export: PNG writer gives the same picture as the WPF encoder, in a smaller file")]
    static void PngWriterRgb()
    {
        int n = 512;
        var px = new byte[n * n * 4];
        var rnd = new Random(5);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = (y * n + x) * 4;
                int v = (x / 2 + y / 3 + rnd.Next(6)) & 255;
                px[i] = (byte)v; px[i + 1] = (byte)(255 - v / 2); px[i + 2] = (byte)((x * y) >> 6); px[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px, n * 4);
        string dir = Path.Combine(Path.GetTempPath(), "YAMEE.Tests", "export");
        Directory.CreateDirectory(dir);
        string mine = Path.Combine(dir, "mine.png"), wpf = Path.Combine(dir, "wpf.png");
        PngWriter.WriteRgb(mine, bmp);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(wpf)) enc.Save(fs);
        long a = new FileInfo(mine).Length, b = new FileInfo(wpf).Length;
        Console.WriteLine($"       own {a} bytes, WPF {b} bytes");
        Check.True(a < b, "the own file is not smaller");
        var back = new FormatConvertedBitmap(new PngBitmapDecoder(new Uri(mine), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        var got = new byte[n * n * 4];
        back.CopyPixels(got, n * 4, 0);
        Check.True(got.AsSpan().SequenceEqual(px), "decoded picture differs");
    }

    /// folder with the real templates (Guide in the repository)
    static string GuideFolder([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        // above the test exe, otherwise above this source file (test built into another folder while YAMEE runs)
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) })
            for (var d = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Guide", MapExport.TemplateName("DE")))) return Path.Combine(d.FullName, "Guide");
        throw new DirectoryNotFoundException("Guide folder not found");
    }

    [Test("Export: progress of the .zip export reports real fractions that only go up")]
    static void MapZipProgress()
    {
        string root = Path.Combine(Path.GetTempPath(), "YAMEE.Tests", "mapzipprogress");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string project = Path.Combine(root, "P");
        Directory.CreateDirectory(project);
        File.WriteAllBytes(Path.Combine(project, "dtm.raw"), new byte[200_000]);
        File.WriteAllBytes(Path.Combine(project, "splat3.png"), new byte[100_000]);
        File.WriteAllText(Path.Combine(project, "prefabs.xml"), "<prefabs/>");
        var fractions = new List<double>();
        var p = new FractionLog(fractions);
        var img = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 8 * 4], 8 * 4);
        img.Freeze();
        MapExport.Write(project, "P", null, Path.Combine(root, "p.zip"), GuideFolder(), p, img);
        Check.True(fractions.Count >= 4, "too few progress reports");
        for (int i = 1; i < fractions.Count; i++) Check.True(fractions[i] >= fractions[i - 1], "progress goes back");
        Check.True(fractions[^1] >= 0.9, "progress does not reach the end");
    }

    sealed class FractionLog(List<double> list) : IWorkProgress
    {
        public void Report(string text) { }
        public void Report(string text, double fraction) => list.Add(fraction);
    }

    [Test("Export: map as .zip with bridge mod only when the map has bridges, guides filled for the game version")]
    static void MapZip()
    {
        string root = Path.Combine(Path.GetTempPath(), "YAMEE.Tests", "mapzip");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string project = Path.Combine(root, "Testland"), folder = Path.Combine(root, "Export");
        Directory.CreateDirectory(project);
        // export folder = real templates + a small fake mod
        foreach (var lang in MapExport.Languages)
            File.Copy(Path.Combine(GuideFolder(), MapExport.TemplateName(lang)), Path.Combine(Directory.CreateDirectory(folder).FullName, MapExport.TemplateName(lang)));
        string mod = Path.Combine(folder, MapExport.ModName, "Prefabs", "Bridges");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(folder, MapExport.ModName, "ModInfo.xml"), "<xml/>");
        File.WriteAllText(Path.Combine(mod, "yamee_part_bridge_10x10_end_01.xml"), "<prefab/>");
        File.WriteAllBytes(Path.Combine(project, "dtm.raw"), new byte[64]);
        File.WriteAllText(Path.Combine(project, "spawnpoints.xml"), "test");
        File.WriteAllText(Path.Combine(project, WorldData.PlaySpawnFile), "play");
        File.WriteAllText(Path.Combine(project, "yamee_project.json"), "{}");   // not part of the export

        void Prefabs(string name) => File.WriteAllText(Path.Combine(project, "prefabs.xml"),
            $"<prefabs>\n<decoration type=\"model\" name=\"{name}\" position=\"0,40,0\" rotation=\"0\" />\n</prefabs>");

        Dictionary<string, string> Read(string zip)
        {
            using var z = System.IO.Compression.ZipFile.OpenRead(zip);
            return z.Entries.ToDictionary(e => e.FullName, e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); });
        }

        // 1) with a bridge piece, 2.6 + Undead Legacy, and a 1024 × 1024 overview PNG
        Prefabs("yamee_part_bridge_10x10_end_01");
        int n = MapExport.ImageSize;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = (y * n + x) * 4;
                px[i] = (byte)(x & 255); px[i + 1] = (byte)(y & 255); px[i + 2] = (byte)((x + y) & 255); px[i + 3] = 255;
            }
        var mapImg = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px, n * 4);
        mapImg.Freeze();
        string zip = Path.Combine(root, "a.zip");
        var r = MapExport.Write(project, "Testland", new GameProfile(GameVersion.Parse("V 2.6 (b14)"), "UndeadLegacy"), zip, folder, mapImage: mapImg);
        var e = Read(zip);
        Check.True(r.Bridges, "bridges not detected");
        Check.True(e.ContainsKey("YAMEE_Bridges/ModInfo.xml") && e.ContainsKey("YAMEE_Bridges/Prefabs/Bridges/yamee_part_bridge_10x10_end_01.xml"), "mod missing");
        Check.True(e.ContainsKey("Testland_map_1024.png"), "overview PNG missing");
        using (var za = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            using var ms = new MemoryStream();
            za.GetEntry("Testland_map_1024.png")!.Open().CopyTo(ms);
            ms.Position = 0;
            var frame = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Check.Equal(1024, frame.PixelWidth, "PNG width");
            Check.Equal(1024, frame.PixelHeight, "PNG height");
            var back = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var got = new byte[n * n * 4];
            back.CopyPixels(got, n * 4, 0);
            Check.True(got.AsSpan().SequenceEqual(px), "PNG pixels differ");
        }
        Check.Equal("play", e["Testland/spawnpoints.xml"], "spawn points");
        Check.True(!e.Keys.Any(k => k.Contains("yamee_project")), "project file exported");
        string de = e["Map_Install_and_Features_DE.md"], en = e["Map_Install_and_Features_EN.md"];
        Check.True(de.Contains("2.6 mit Undead Legacy") && en.Contains("2.6 with Undead Legacy"), "version text");
        Check.True(de.Contains(@"%APPDATA%\7DaysToDie\UndeadLegacy\GeneratedWorlds\") && !de.Contains(@"7DaysToDie\GeneratedWorlds\"), "world folder");
        Check.True(de.Contains("`Testland`") && de.Contains("YAMEE_Bridges"), "map name / mod part");
        Check.True(!de.Contains("{") && !de.Contains("<!--") && !en.Contains("{") && !en.Contains("<!--"), "placeholder or comment left");

        // 2) without bridges, 3.4
        Prefabs("rwg_tile_rural_corner");
        zip = Path.Combine(root, "b.zip");
        r = MapExport.Write(project, "Testland", new GameProfile(GameVersion.Parse("V 3.4.0 (b10)"), null), zip, folder);
        e = Read(zip);
        Check.True(!r.Bridges && !e.Keys.Any(k => k.StartsWith("YAMEE_Bridges/")), "mod packed without bridges");
        de = e["Map_Install_and_Features_DE.md"];
        Check.True(de.Contains("7 Days to Die 3.4.") && de.Contains(@"%APPDATA%\7DaysToDie\GeneratedWorlds\") && !de.Contains("YAMEE_Bridges"), "guide without bridges");
        Console.WriteLine("       " + string.Join(" | ", de.Split('\n').Take(6)));
    }
}
