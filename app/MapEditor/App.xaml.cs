using System.Windows;

namespace MapEditor;

public partial class App : Application
{
    public App()
    {
        // Zahlen überall englisch (auch ohne ausdrückliches Format), unabhängig von der Windows-Einstellung
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        // Sprache vor dem ersten Fenster laden (Einstellungen → Sprache, wirkt nach einem Neustart);
        // MAPEDITOR_LANG (Umgebungsvariable) überschreibt die Einstellung – zum Testen beider Sprachen
        Loc.Load(Environment.GetEnvironmentVariable("MAPEDITOR_LANG") ?? AppSettings.Load().Language);
        ThemeManager.Hook();
        // Unerwartete Fehler anzeigen statt das Programm zu beenden (die geplante Straße bleibt erhalten)
        DispatcherUnhandledException += (_, e) =>
        {
            string log = ErrorLog.Write(e.Exception);
            MessageBox.Show(Loc.T("An error occurred:") + "\n\n" + e.Exception.Message + "\n\n" + Loc.T("Please send this message (Ctrl+C copies it) to Claude.") +
                (log == null ? "" : "\n\n" + Loc.F("Details were saved in: {0}", log)),
                "YAMEE", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        // Fehler in Hintergrundaufgaben, die niemand abfängt, wenigstens ins Protokoll schreiben (Prüfbericht F9)
        TaskScheduler.UnobservedTaskException += (_, e) => { ErrorLog.Write(e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write(e.ExceptionObject as Exception);
    }
}

/// Protokoll der Fehler: %APPDATA%\YAMEE\error.log (Entwicklungsstand: YAMEE-Dev), mit Zeit und vollem Stack;
/// wird bei über 512 KB neu begonnen (die alte Datei bleibt als error.log.old)
public static class ErrorLog
{
    /// schreibt den Fehler ins Protokoll; liefert den Dateinamen (null, wenn das Schreiben nicht klappt)
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
