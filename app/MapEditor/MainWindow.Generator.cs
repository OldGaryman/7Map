using System.IO;
using System.Windows;
using MapEditor.Map;

namespace MapEditor;

/// Terrain generator (the user's request, 2026-11-06): write a new world without cities, roads and buildings into the project folder
/// and open it. "Copy to 7 Days to Die" creates the folder in the game the first time.
public partial class MainWindow
{
    GeneratorSpec lastGenerator;

    async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmSwitch(null)) return;
        if (!await ConfirmUnsaved(Loc.T("Generate world"))) return;
        string projects = settings.ProjectPath;
        string gameFolder = settings.GameFolder ?? AppSettings.GuessGameFolder();
        var dlg = new GeneratorWindow(lastGenerator ?? UserValues.Load(new GeneratorSpec()), name => Directory.Exists(Path.Combine(projects, name)),
            name => GameSync.GuessGameWorld(name) != null, gameFolder) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var spec = dlg.Spec;
        lastGenerator = spec;
        UserValues.Remember(spec);
        string dst = Path.Combine(projects, spec.Name);
        if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any())
        {
            var a = MessageBox.Show(this, Loc.F("There already is a project \"{0}\":\n{1}\n\nYes = put the old project aside (it stays next to it as a backup) and create the new world\nNo = do nothing",
                spec.Name, dst), Loc.T("Generate world"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (a != MessageBoxResult.Yes) return;
            // delete nothing, only put it aside (like when creating from the game)
            string aside = dst + "_old_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            Directory.Move(dst, aside);
            Backups.MoveWithProject(dst, aside);
        }
        // remember the world folder in the game (does not exist yet – "Copy to 7 Days to Die" creates it)
        string generated = AppSettings.GuessGeneratedWorlds();
        Overlay.Visibility = Visibility.Visible;
        IProgress<string> progress = new Progress<string>(t => LoadingText.Text = Loc.T("Generating world …") + "\n" + t);
        string previous = world?.Folder;
        WorldGenerator.Result result;
        try
        {
            world = null;
            edits = null;
            Map.Clear();
            MemoryTools.Release();
            result = await Task.Run(() => new WorldGenerator(spec).Write(dst, generated, progress));
            try { TerrainCopy.Save(dst); } catch (Exception ex) { ErrorLog.Write(ex); }   // terrain copy for "Create cities → Replace"
            MemoryTools.Release();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            MessageBox.Show(this, Loc.T("Generating the world failed:") + "\n" + ex.Message, Loc.T("Generate world"), MessageBoxButton.OK, MessageBoxImage.Error);
            if (previous != null) await OpenWorld(previous); else Overlay.Visibility = Visibility.Collapsed;
            return;
        }
        if (generated != null)
        {
            settings.GameWorlds[spec.Name] = Path.Combine(generated, spec.Name);
            settings.Save();
        }
        await OpenWorld(dst);
        Msg.Info(this, settings, "world-created", Loc.F("The world \"{0}\" was created ({1} × {1} m, ground {2:0}–{3:0} m, {4} lake(s), {5} spawn point(s)).\n\n" +
            "Next step: Creation → Towns.",
            spec.Name, spec.Size, result.MinHeight, result.MaxHeight, result.Lakes, result.Spawns) + AnomalyText(result),
            Loc.T("Generate world"));
    }

    /// locations of the anomalies in game coordinates (E/W, N/S like the compass display in the game), plus craters and cracks
    static string AnomalyText(WorldGenerator.Result r)
    {
        string text = "";
        if (r.Craters > 0 || r.Cracks > 0) text += "\n\n" + Loc.F("Wasteland: {0} craters, {1} earthquake cracks.", r.Craters, r.Cracks);
        return text;
    }
}
