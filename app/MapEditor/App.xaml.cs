using System.Windows;
using System.Windows.Interop;

namespace MapEditor;

public partial class App : Application
{
    SingleInstance singleInstance;
    IntPtr mainWindowHandle;

    public App()
    {
        // numbers in English everywhere (also without an explicit format), independent of the Windows setting
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        // load the language before the first window (settings → language, takes effect after a restart);
        // MAPEDITOR_LANG (environment variable) overrides the setting – for testing both languages
        Loc.Load(Environment.GetEnvironmentVariable("MAPEDITOR_LANG") ?? AppSettings.Load().Language);
        ThemeManager.Hook();
        // show unexpected errors instead of ending the program (the planned road is kept)
        DispatcherUnhandledException += (_, e) =>
        {
            if (WorkCancel.IsCancel(e.Exception))   // cancelled with the button on the progress card: not an error (the step was undone)
            {
                e.Handled = true;
                (MainWindow as MainWindow)?.ShowStatus(Loc.T("Cancelled. Nothing was changed."));
                return;
            }
            string log = ErrorLog.Write(e.Exception);
            MessageBox.Show(Loc.T("An error occurred:") + "\n\n" + e.Exception.Message + "\n\n" + Loc.T("Please send this message (Ctrl+C copies it) to Claude.") +
                (log == null ? "" : "\n\n" + Loc.F("Details were saved in: {0}", log)),
                "YAMEE", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        // at least write errors in background tasks that nobody catches to the log (audit finding F9)
        TaskScheduler.UnobservedTaskException += (_, e) => { ErrorLog.Write(e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write(e.ExceptionObject as Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        singleInstance = SingleInstance.TryStart(() => SingleInstance.BringToFront(this, () => Volatile.Read(ref mainWindowHandle)));
        if (singleInstance == null)
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        var main = new MainWindow();
        main.SourceInitialized += (_, _) => Volatile.Write(ref mainWindowHandle, new WindowInteropHelper(main).Handle);
        main.Closed += (_, _) => Volatile.Write(ref mainWindowHandle, IntPtr.Zero);
        MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        singleInstance = null;
        base.OnExit(e);
    }
}

/// Error log: error.log in the data folder, with time and full stack;
/// started anew above 512 KB (the old file stays as error.log.old)
public static class ErrorLog
{
    /// writes the error to the log; returns the file name (null if writing fails)
    public static string Write(Exception ex)
    {
        if (ex == null) return null;
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder);
            string file = Path.Combine(AppSettings.DataFolder, "error.log");
            if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Move(file, file + ".old", true);
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] YAMEE {typeof(App).Assembly.GetName().Version}\r\n{ex}\r\n\r\n");
            return file;
        }
        catch (Exception) { return null; }
    }
}
