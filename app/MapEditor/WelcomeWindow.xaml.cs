using System.Windows;
using Microsoft.Win32;

namespace MapEditor;

/// Window "Welcome" at the first start on a PC: game found (with version), world folder, project folder, language.
/// Without the game it still works (roads, biomes, spawn points); only the exact buildings are missing then.
public partial class WelcomeWindow : Window
{
    readonly AppSettings settings;
    readonly string autoGame = AppSettings.GuessGameFolder();

    public WelcomeWindow(AppSettings settings)
    {
        InitializeComponent();
        Loc.Translate(this);
        this.settings = settings;
        GameBox.Text = settings.GameFolder ?? autoGame ?? "";
        ProjectBox.Text = settings.ChosenFolder;
        if (AppSettings.IsPortable) { ProjectBox.IsReadOnly = true; ProjectBrowse.IsEnabled = false; }   // portable: always the folder Data next to the exe
        var languages = Loc.Available();
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = languages.FirstOrDefault(l => l.Id == Loc.Language) ?? languages[0];
        var worlds = GameLocator.WorldFolders();
        WorldsInfo.Text = worlds.Count == 0 ? Loc.T("Not found yet – they appear once a world has been generated in the game.") : string.Join("\n", worlds);
        ShowGameInfo();
        GameBox.TextChanged += (_, _) => ShowGameInfo();
        GameBrowse.Click += (_, _) => Browse(GameBox);
        ProjectBrowse.Click += (_, _) => Browse(ProjectBox);
        OkButton.Click += (_, _) => Apply();
    }

    // what was found in the game folder: version (game log) and Steam build, otherwise a hint
    void ShowGameInfo()
    {
        string folder = GameBox.Text.Trim();
        if (folder == "")
        {
            GameInfo.Text = Loc.T("7 Days to Die was not found. Roads, biomes and spawn points work anyway; for buildings please choose the game folder.");
            return;
        }
        if (!GameLocator.IsGameFolder(folder))
        {
            GameInfo.Text = Loc.T("This folder does not contain Data\\Prefabs – it is not the game folder.");
            return;
        }
        var found = GameLocator.Find();
        var parts = new List<string>();
        if (GameLocator.VersionFromLog() is string version) parts.Add(Loc.F("Version {0}", version));
        if (found != null && string.Equals(found.Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            if (found.BuildId != null) parts.Add(Loc.F("Steam build {0}", found.BuildId));
            if (found.Server) parts.Add(Loc.T("dedicated server"));
            parts.Add(Loc.T("found automatically"));
        }
        GameInfo.Text = "✓ " + string.Join("  ·  ", parts.DefaultIfEmpty(Loc.T("game folder")));
    }

    void Browse(System.Windows.Controls.TextBox box)
    {
        var dlg = new OpenFolderDialog();
        if (Directory.Exists(box.Text.Trim())) dlg.InitialDirectory = box.Text.Trim();
        if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
    }

    void Apply()
    {
        string game = GameBox.Text.Trim(), project = ProjectBox.Text.Trim();
        if (game != "" && !GameLocator.IsGameFolder(game))
        {
            ErrorText.Text = Loc.T("Please choose the game folder (it contains Data\\Prefabs) or leave the field empty.");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (project == "" || !Path.IsPathRooted(project))
        {
            ErrorText.Text = Loc.T("Please enter the full path of the project folder.");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        try { Directory.CreateDirectory(project); }
        catch (Exception ex)
        {
            ErrorText.Text = Loc.T("The project folder could not be created:") + " " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        // found folder = "automatic" (finds the game again after a move)
        settings.GameFolder = game == "" || string.Equals(game, autoGame, StringComparison.OrdinalIgnoreCase) ? null : game;
        settings.SetProjectPath(Path.GetFullPath(project));
        settings.Language = (LanguageBox.SelectedItem as Loc.LanguageInfo)?.Id ?? Loc.English;
        settings.Save();
        DialogResult = true;
    }
}
