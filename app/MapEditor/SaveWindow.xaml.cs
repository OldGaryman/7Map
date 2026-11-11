using System.Windows;
using System.Windows.Input;
using MapEditor.Map;

namespace MapEditor;

/// Fenster „Kartenänderungen in einen Spielstand übernehmen“: Spielstand wählen, Vorschau (Zahlen + Gebiete auf der Karte),
/// dann übernehmen. Verglichen wird ohne Auswahl mit dem Kartenstand, mit dem der Spielstand zuletzt lief (gemerkt, sonst die
/// Karte im Spiel). Übernehmen ändert den Spielstand und kopiert die Karte ins Spiel (daraus baut das Spiel die Gebiete neu).
public partial class SaveWindow : Window
{
    readonly string project, gameFolder, gameWorld;
    readonly Action<SaveDiff> showOnMap;
    readonly Func<Task<bool>> copyMapToGame;
    SaveDiff diff;

    public SaveWindow(string project, string worldName, string gameFolder, string gameWorld, Action<SaveDiff> showOnMap, Func<Task<bool>> copyMapToGame)
    {
        InitializeComponent();
        Loc.Translate(this);
        this.project = project;
        this.gameFolder = gameFolder;
        this.gameWorld = gameWorld;
        this.showOnMap = showOnMap;
        this.copyMapToGame = copyMapToGame;
        var saves = SaveUpdater.FindSaves(worldName);
        SaveBox.ItemsSource = saves.Select(s => new SaveItem(s)).ToList();
        SaveBox.SelectedIndex = saves.Count > 0 ? 0 : -1;
        NoSaveText.Visibility = saves.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveBox.SelectionChanged += (_, _) => ShowReference();
        PreviewButton.Click += async (_, _) => await Preview();
        ApplyButton.Click += async (_, _) => await ApplyToSave();
        Closed += (_, _) => showOnMap(null);
        ShowReference();
    }

    sealed record SaveItem(GameSave Save)
    {
        public override string ToString() => Loc.F("{0} – last played {1:yyyy-MM-dd HH:mm}", Save.Game, Save.LastPlayed);
    }

    GameSave Selected => (SaveBox.SelectedItem as SaveItem)?.Save;

    // gemerkter Stand des Spielstands, sonst die Karte, die gerade im Spiel liegt (damit lief er)
    string ReferenceFolder(GameSave s) => SaveUpdater.ReferenceOf(s) ?? (SaveUpdater.IsMapFolder(gameWorld) ? gameWorld : null);

    void ShowReference()
    {
        Reset();
        var s = Selected;
        PreviewButton.IsEnabled = s != null;
        if (s == null) { ReferenceText.Text = ""; return; }
        ReferenceText.Text = SaveUpdater.ReferenceOf(s) != null
            ? Loc.F("The map state this save last ran with is remembered ({0:yyyy-MM-dd HH:mm}). Only the changes since then are copied.", SaveUpdater.ReferenceTime(s))
            : gameWorld != null
                ? Loc.T("The save ran with the map that is currently in the game. The differences to the project are copied.")
                : Loc.T("The world was not found in the game – please use \"Copy to 7 Days to Die\" once.");
    }

    void Reset()
    {
        diff = null;
        ApplyButton.IsEnabled = false;
        ResultBox.Visibility = Visibility.Collapsed;
        StatusText.Text = "";
        showOnMap(null);
    }

    async Task Preview()
    {
        var s = Selected;
        if (s == null) return;
        Reset();
        string reference = ReferenceFolder(s);
        if (reference == null)
        {
            StatusText.Text = Loc.T("The world was not found in the game – please use \"Copy to 7 Days to Die\" once.");
            return;
        }
        PreviewButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        StatusText.Text = Loc.T("Comparing …");
        try
        {
            diff = await Task.Run(() => SaveUpdater.Compare(reference, project, s, gameFolder));
            ResultText.Text = Loc.F("Changed areas (16 × 16 m): {0:#,0}, of which stored in the save and reset: {1:#,0}\nTrees removed: {2:#,0} of {3:#,0}  ·  multi-block objects removed: {4:#,0} of {5:#,0}",
                diff.Chunks.Count, diff.StoredChunks.Count, diff.Trees, diff.TreesTotal, diff.Multiblocks, diff.MultiblocksTotal)
                + (diff.Notes.Count > 0 ? "\n" + string.Join("  ·  ", diff.Notes) : "");
            WarnText.Visibility = diff.StoredChunks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultBox.Visibility = Visibility.Visible;
            StatusText.Text = diff.Chunks.Count == 0 ? Loc.T("The save already runs with this map – nothing to copy.") : "";
            ApplyButton.IsEnabled = diff.Chunks.Count > 0;
            showOnMap(diff);
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("Comparing failed:") + " " + ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            PreviewButton.IsEnabled = true;
        }
    }

    async Task ApplyToSave()
    {
        var s = Selected;
        if (s == null || diff == null) return;
        if (GameSync.GameRunning())
        {
            MessageBox.Show(this, Loc.T("The game is still running. Please quit 7 Days to Die first."), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // bisherige Sicherungen nennen: jede ist eine Vollkopie und sie werden nie von allein gelöscht (Prüfbericht F39)
        var (oldCount, oldBytes) = await Task.Run(SaveUpdater.BackupSummary);
        string old = oldCount == 0 ? "" : "\n\n" + Loc.F("Earlier backups of saves: {0} ({1:#,0} MB) in {2}. Delete old ones there when you no longer need them.",
            oldCount, oldBytes / 1048576, SaveUpdater.BackupFolder);
        if (MessageBox.Show(this, Loc.F("Copy the map changes into \"{0}\" now?\n\n{1:#,0} stored area(s) are reset – what was built there in the game is lost. The save is backed up completely first. The map is also copied into the game.",
                s.Game, diff.StoredChunks.Count) + old, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ApplyButton.IsEnabled = PreviewButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        var progress = new Progress<string>(t => StatusText.Text = t);
        try
        {
            var d = diff;
            string backup = await Task.Run(() => SaveUpdater.Apply(d, project, s, progress));
            StatusText.Text = Loc.T("Copying the map into the game …");
            bool copied = await copyMapToGame();
            diff = null;
            showOnMap(null);
            ShowReference();
            StatusText.Text = Loc.F("Done. Backup of the save: {0}\nIn the game: continue the game \"{1}\".", backup, s.Game)
                + (copied ? "" : "\n" + Loc.T("The map could not be copied into the game – please use \"Copy to 7 Days to Die\"."));
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("Copying into the save failed:") + " " + ex.Message;
            ApplyButton.IsEnabled = diff != null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            PreviewButton.IsEnabled = true;
        }
    }
}
