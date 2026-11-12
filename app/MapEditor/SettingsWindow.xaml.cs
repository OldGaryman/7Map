using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MapEditor;

/// Settings: each kind has its own window without tabs – paths and info/language. Since 2026-11-10 the values of the tools (roads,
/// eraser, biomes …) are in the tools themselves (on the left) and are saved there immediately to user.ini.
public partial class SettingsWindow : Window
{
    readonly AppSettings settings;

    public SettingsWindow(AppSettings settings, int page = 0)
    {
        InitializeComponent();
        Loc.Translate(this);
        this.settings = settings;
        ProjectBox.Text = settings.ChosenFolder;   // always the full path (the user's request)
        SetGameAuto(settings.GameFolder == null);
        if (settings.GameFolder != null) GameBox.Text = settings.GameFolder;
        // version from the project (<Version> in MapEditor.csproj), as the installer shows it too
        var v = typeof(App).Assembly.GetName().Version;
        VersionText.Text = Loc.F("YAMEE version {0}", $"{v.Major}.{v.Minor}.{v.Build}") + (AppSettings.IsDev ? " (Dev)" : "");
        SelectPage(page);
    }

    FrameworkElement[] pages;

    /// show page (0 paths, 1 info and language)
    public void SelectPage(int index)
    {
        pages ??= new FrameworkElement[] { PagePaths, PageOther };
        string[] titles = { "Paths", "About YAMEE" };
        double[] widths = { 760, 520 };
        index = Math.Clamp(index, 0, pages.Length - 1);
        for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        Title = Loc.T(titles[index]);
        Width = widths[index];
        // "About YAMEE" has nothing to save: only "OK"
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

    // ---------- Paths ----------

    // game folder automatic: field grayed out with the found path; otherwise normal with the chosen path
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

    // "Browse …" at the game folder switches "automatic" off (the user's request)
    void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Choose the 7 Days to Die folder (it contains Data\\Prefabs)") };
        string start = GameBox.Text.Trim();
        if (Directory.Exists(start)) dlg.InitialDirectory = start;
        if (dlg.ShowDialog(this) != true) return;
        SetGameAuto(false);
        GameBox.Text = dlg.FolderName;
    }

    // ---------- Save ----------

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Apply()) DialogResult = true;
    }

    // check the input and write it into the settings; false = error (marked red)
    bool Apply()
    {
        string project = ProjectBox.Text.Trim();
        bool projectOk = project != "" && Path.IsPathRooted(project);
        // the folder must be creatable (invalid characters, drive missing, no write permission): otherwise only the
        // creation of a project fails later with an unclear message (audit finding S3)
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
