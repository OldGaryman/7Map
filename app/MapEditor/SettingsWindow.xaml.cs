using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MapEditor;

/// Einstellungen: jede Art hat ihr eigenes Fenster ohne Reiter – Pfade und Info/Sprache. Die Werte der Werkzeuge (Straßen,
/// Radiergummi, Biome …) stehen seit 10.11.2026 in den Werkzeugen selbst (links) und werden dort sofort in user.ini gespeichert.
public partial class SettingsWindow : Window
{
    readonly AppSettings settings;

    public SettingsWindow(AppSettings settings, int page = 0)
    {
        InitializeComponent();
        Loc.Translate(this);
        this.settings = settings;
        ProjectBox.Text = settings.ProjectPath;   // immer der vollständige Pfad (Wunsch des Nutzers)
        SetGameAuto(settings.GameFolder == null);
        if (settings.GameFolder != null) GameBox.Text = settings.GameFolder;
        // Version aus dem Projekt (<Version> in MapEditor.csproj), wie sie auch der Installer zeigt
        var v = typeof(App).Assembly.GetName().Version;
        VersionText.Text = Loc.F("YAMEE version {0}", $"{v.Major}.{v.Minor}.{v.Build}") + (AppSettings.IsDev ? " (Dev)" : "");
        SelectPage(page);
    }

    FrameworkElement[] pages;

    /// Seite zeigen (0 Pfade, 1 Info und Sprache)
    public void SelectPage(int index)
    {
        pages ??= new FrameworkElement[] { PagePaths, PageOther };
        string[] titles = { "Paths", "About YAMEE" };
        double[] widths = { 760, 520 };
        index = Math.Clamp(index, 0, pages.Length - 1);
        for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        Title = Loc.T(titles[index]);
        Width = widths[index];
        // „Über YAMEE“ hat nichts zu speichern: nur „OK“
        bool about = index == 1;
        CancelButton.Visibility = SaveButton.Visibility = about ? Visibility.Collapsed : Visibility.Visible;
        OkButton.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsCancel = !about; SaveButton.IsDefault = !about;
        OkButton.IsCancel = OkButton.IsDefault = about;
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { ErrorLog.Write(ex); }
        e.Handled = true;
    }

    static void Mark(TextBox box, bool ok)
    {
        if (ok) box.ClearValue(BorderBrushProperty);
        else box.BorderBrush = System.Windows.Media.Brushes.Red;
    }

    // ---------- Pfade ----------

    // Spielordner automatisch: Feld ausgegraut mit dem gefundenen Pfad; sonst normal mit dem gewählten Pfad
    void SetGameAuto(bool auto)
    {
        GameAutoCheck.IsChecked = auto;
        GameBox.IsEnabled = !auto;
        if (auto) GameBox.Text = AppSettings.GuessGameFolder() ?? Loc.T("(not found – please use \"Browse …\")");
    }

    void GameAuto_Click(object sender, RoutedEventArgs e) => SetGameAuto(GameAutoCheck.IsChecked == true);

    void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Choose the folder of all projects") };
        if (Directory.Exists(ProjectBox.Text.Trim())) dlg.InitialDirectory = ProjectBox.Text.Trim();
        if (dlg.ShowDialog(this) == true) ProjectBox.Text = dlg.FolderName;
    }

    // „Durchsuchen …“ beim Spielordner schaltet „automatisch“ aus (Wunsch des Nutzers)
    void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Choose the 7 Days to Die folder (it contains Data\\Prefabs)") };
        string start = GameBox.Text.Trim();
        if (Directory.Exists(start)) dlg.InitialDirectory = start;
        if (dlg.ShowDialog(this) != true) return;
        SetGameAuto(false);
        GameBox.Text = dlg.FolderName;
    }

    // ---------- Speichern ----------

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Apply()) DialogResult = true;
    }

    // Eingaben prüfen und in die Einstellungen schreiben; false = Fehler (rot markiert)
    bool Apply()
    {
        string project = ProjectBox.Text.Trim();
        bool projectOk = project != "" && Path.IsPathRooted(project);
        // der Ordner muss sich anlegen lassen (ungültige Zeichen, Laufwerk fehlt, kein Schreibrecht): sonst scheitert erst das
        // Anlegen eines Projekts mit einer unklaren Meldung (Prüfbericht S3)
        if (projectOk)
        {
            try { Directory.CreateDirectory(Path.GetFullPath(project)); }
            catch (Exception) { projectOk = false; }
        }
        Mark(ProjectBox, projectOk);
        bool gameOk = GameAutoCheck.IsChecked == true || Directory.Exists(Path.Combine(GameBox.Text.Trim(), "Data", "Prefabs"));
        Mark(GameBox, gameOk);
        if (!projectOk || !gameOk)
        {
            ErrorText.Text = Loc.T("Please check the paths marked in red (folder of all projects: full path; game folder: it must contain Data\\Prefabs).");
            ErrorText.Visibility = Visibility.Visible;
            return false;
        }
        settings.SetProjectPath(Path.GetFullPath(project));
        settings.GameFolder = GameAutoCheck.IsChecked == true ? null : GameBox.Text.Trim();
        settings.Save();
        return true;
    }
}
