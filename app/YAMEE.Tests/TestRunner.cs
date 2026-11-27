using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace YameeTests;

/// Very small test frame: every test is a method with [Test]; a failed Check throws, the runner counts and reports.
/// Exit code = number of failed tests (0 = all fine), so a script can react to it.
[AttributeUsage(AttributeTargets.Method)]
sealed class TestAttribute : Attribute
{
    public TestAttribute(string name) => Name = name;
    public string Name { get; }
}

sealed class SkipException : Exception
{
    public SkipException(string reason) : base(reason) { }
}

static class Check
{
    public static void True(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    public static void Skip(string reason) => throw new SkipException(reason);

    /// short fingerprint of an array (to compare whole maps)
    public static string Hash<T>(T[] data) where T : struct => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(data.AsSpan())))[..16];

    public static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))[..16];
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // own temporary folder for worlds and settings: the tests never touch the projects or settings of the user
        string temp = Path.Combine(Path.GetTempPath(), "YAMEE.Tests", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("MAPEDITOR_SETTINGS", Path.Combine(temp, "settings", "settings.json"));
        Directory.CreateDirectory(Path.Combine(temp, "settings"));
        Fixture.Temp = temp;

        string filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            .Select(m => (Method: m, Attr: (TestAttribute)Attribute.GetCustomAttribute(m, typeof(TestAttribute))))
            .Where(x => x.Attr != null && (filter == null || x.Attr.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Console.WriteLine($"YAMEE tests ({tests.Count}), temporary folder {temp}");
        Console.WriteLine();
        int passed = 0, failed = 0, skipped = 0;
        var total = Stopwatch.StartNew();
        foreach (var (method, attr) in tests)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                method.Invoke(null, null);
                passed++;
                Write(ConsoleColor.Green, "PASS", $"{attr.Name}  ({sw.ElapsedMilliseconds} ms)");
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is SkipException s)
            {
                skipped++;
                Write(ConsoleColor.DarkYellow, "SKIP", $"{attr.Name}: {s.Message}");
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                failed++;
                var inner = e.InnerException ?? e;
                Write(ConsoleColor.Red, "FAIL", $"{attr.Name}: {inner.Message}");
                if (inner is not Exception { Source: "YAMEE.Tests" }) Console.WriteLine(inner.StackTrace);
            }
        }
        Console.WriteLine();
        Write(failed == 0 ? ConsoleColor.Green : ConsoleColor.Red, failed == 0 ? "OK" : "ERRORS",
            $"{passed} passed, {failed} failed, {skipped} skipped in {total.Elapsed.TotalSeconds:0.0} s");
        try { Directory.Delete(temp, true); } catch (Exception) { }   // our own temporary files
        return failed;
    }

    static void Write(ConsoleColor color, string tag, string text)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write($"{tag,-7}");
        Console.ForegroundColor = old;
        Console.WriteLine(text);
    }
}
