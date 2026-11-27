using System.IO;
using System.Windows;
using System.Windows.Input;
using MapEditor.Map;

namespace MapEditor;

/// Menu rebuild 2026-11-20 (the user's request): "New project" offers "New map" (a name; the map generator right away or later)
/// and "Open map" (world from the game). A project without a map yet only has a name (pendingName); the map generator
/// (Map > Creation > Map) gives it its map. The map name field in the tab Start renames the project.
public partial class MainWindow
{
    string pendingName;   // name of a new project that has no map yet (null = none)

    /// error text for a map name, null = the name is free
    string ValidateNewName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Loc.T("Please enter a name for the map.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
            return Loc.T("The name must not contain any of these characters: \\ / : * ? \" < > |");
        string folder = Path.Combine(settings.ProjectPath, name);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            return Loc.F("There already is a project \"{0}\".", name);
        return null;
    }

    /// a world of this name in the game (e.g. left behind by a renamed project) does not block the name: the user is asked,
    /// because "Project into the game" would overwrite it. true = the name may be used.
    bool ConfirmGameWorldName(Window owner, string name)
    {
        var profile = GameLocator.ProfileOf(settings.MainGameFolder);
        string game = GameSync.GuessGameWorld(name, profile);
        if (game == null) return true;
        return MessageBox.Show(owner, Loc.F("The game already has a world \"{0}\" (no project uses this name). It is overwritten when you copy this project into the game; save games of that world would then no longer fit.\n\n{1}\n\nUse the name anyway?", name, game),
            Loc.T("Map name"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    async void NewProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmSwitch(null)) return;
        var dlg = new NewProjectWindow(ValidateNewName, ConfirmGameWorldName) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (!dlg.NewMap) { await LoadFromGame(); return; }
        if (!await ConfirmUnsaved(Loc.T("New project"))) return;
        await StartNewProject(dlg.MapName, dlg.GoToGenerator);
    }

    /// a new project that only has a name: the open map is closed
    async Task StartNewProject(string name, bool generate)
    {
        if (world != null)
        {
            world = null;
            edits = null;
            CloseWorldWindows();
            Map.Clear();
            MemoryTools.Release();
        }
        pendingName = name;
        ShowPendingInfo();
        UpdateButtons();
        UpdateUndo();
        UpdateSpawns();
        if (generate)
        {
            ShowRibbonTab("map");   // the map generator belongs to the tab Map
            await RunGenerator();
        }
    }

    /// title row and message on the map for a project without a map
    void ShowPendingInfo()
    {
        if (pendingName == null) return;
        WorldName.Text = Loc.F("{0}  (no map yet)", pendingName);
        WorldFolderText.Text = "";
        MapNameBox.Text = pendingName;
        LoadingText.Text = Loc.F("The project \"{0}\" has no map yet.\n\nMap > Creation > Map creates one.", pendingName);
        ShowOverlayMessage();
        UpdateMenuState();
    }

    // ---------- Map name in the tab Start: renames the project ----------

    bool renaming;

    async void MapName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await ApplyMapName(); Keyboard.ClearFocus(); }
        else if (e.Key == Key.Escape) { e.Handled = true; ShowMapName(); Keyboard.ClearFocus(); }
    }

    async void MapName_LostFocus(object sender, RoutedEventArgs e) => await ApplyMapName();

    void ShowMapName() => MapNameBox.Text = world?.Name ?? pendingName ?? "";

    async Task ApplyMapName()
    {
        if (renaming || !ProjectExists) return;
        string old = world?.Name ?? pendingName;
        string name = MapNameBox.Text.Trim();
        if (name == old) return;
        string error = name.Equals(old, StringComparison.OrdinalIgnoreCase) ? null : ValidateNewName(name);
        if (error == null && string.IsNullOrWhiteSpace(name)) error = Loc.T("Please enter a name for the map.");
        if (error != null) { MessageBox.Show(this, error, Loc.T("Map name"), MessageBoxButton.OK, MessageBoxImage.Information); ShowMapName(); return; }
        if (!name.Equals(old, StringComparison.OrdinalIgnoreCase) && !ConfirmGameWorldName(this, name)) { ShowMapName(); return; }
        renaming = true;
        try
        {
            if (world == null) { pendingName = name; ShowPendingInfo(); return; }
            if (Map.Busy) { ShowMapName(); return; }
            if (MessageBox.Show(this, Loc.F("Rename the project \"{0}\" to \"{1}\"?\n\nThe project folder is renamed. The world in the game keeps its old name until you copy the project into the game again.", old, name),
                    Loc.T("Map name"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { ShowMapName(); return; }
            if (!await ConfirmSwitch(null) || !await ConfirmUnsaved(Loc.T("Map name"))) { ShowMapName(); return; }
            string oldFolder = world.Folder.TrimEnd('\\', '/');
            string newFolder = Path.Combine(Path.GetDirectoryName(oldFolder), name);
            // free the files first (the map stays open in memory only until the folder has been renamed)
            world = null;
            edits = null;
            CloseWorldWindows();
            Map.Clear();
            MemoryTools.Release();
            try
            {
                Directory.Move(oldFolder, string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase) ? oldFolder + "_rename_tmp" : newFolder);
                if (string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase)) Directory.Move(oldFolder + "_rename_tmp", newFolder);   // only the case of the letters changes
            }
            catch (Exception ex) when (!WorkCancel.IsCancel(ex))
            {
                ErrorLog.Write(ex);
                MessageBox.Show(this, Loc.T("The project could not be renamed:") + "\n" + ex.Message, Loc.T("Map name"), MessageBoxButton.OK, MessageBoxImage.Error);
                await OpenWorld(oldFolder);
                return;
            }
            settings.GameWorlds.Remove(old);   // the world in the game of the old name is no longer the one of this project
            settings.Save();
            await OpenWorld(newFolder);
        }
        finally { renaming = false; }
    }
}
