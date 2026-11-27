using System.IO;
using System.Windows;
using MapEditor.Map;

namespace MapEditor;

/// Terrain generator (the user's request, 2026-11-06): write a new world without cities, roads and buildings into the project folder
/// and open it. "Copy to 7 Days to Die" creates the folder in the game the first time.
public partial class MainWindow
{
    GeneratorSpec lastGenerator;

    async void GenerateButton_Click(object sender, RoutedEventArgs e) => await RunGenerator();

    /// Map > Creation > Map: only for a new project that has no map yet (the name was chosen in "New project")
    async Task RunGenerator()
    {
        if (pendingName == null || world != null) return;
        if (!await ConfirmSwitch(null)) return;
        string projects = settings.ProjectPath;
        // the new world is made for the main game folder: its version and mod decide the format of main.ttw/map_info.xml and
        // the world folder in the game (with Undead Legacy installed its own one; before 2026-11-13 always the mod folder if it existed)
        string gameFolder = settings.MainGameFolder;
        var gameProfile = GameLocator.ProfileOf(gameFolder);
        var dlg = new GeneratorWindow(lastGenerator ?? UserValues.Load(new GeneratorSpec()), name => Directory.Exists(Path.Combine(projects, name)),
            name => GameSync.GuessGameWorld(name, gameProfile) != null, gameFolder, pendingName) { Owner = this };
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
        }
        // remember the world folder in the game (does not exist yet – "Copy to 7 Days to Die" creates it)
        string generated = AppSettings.GuessGeneratedWorlds(gameFolder);
        ShowWork();
        IProgress<string> progress = BusyProgress(Loc.T("Generating world …"), cancelable: true, t => LoadingText.Text = Loc.T("Generating world …") + "\n" + t);
        string previous = world?.Folder;
        WorldGenerator.Result result;
        try
        {
            world = null;
            edits = null;
            Map.Clear();
            MemoryTools.Release();
            result = await Task.Run(() => new WorldGenerator(spec).Write(dst, generated, progress, gameProfile?.Version));
            if (gameProfile?.Version != null) ProjectInfo.Set(dst, gameProfile);   // the project belongs to this game version
            try { TerrainCopy.Save(dst); } catch (Exception ex) when (!WorkCancel.IsCancel(ex)) { ErrorLog.Write(ex); }   // terrain copy for "Create cities → Replace"
            MemoryTools.Release();
        }
        catch (Exception ex) when (!WorkCancel.IsCancel(ex))
        {
            ErrorLog.Write(ex);
            MessageBox.Show(this, Loc.T("Generating the world failed:") + "\n" + ex.Message, Loc.T("Generate world"), MessageBoxButton.OK, MessageBoxImage.Error);
            if (previous != null) await OpenWorld(previous); else ShowPendingInfo();
            return;
        }
        catch (OperationCanceledException)
        {
            // cancelled with the button on the progress card: the half-written map folder is removed, the project stays without a map
            try { if (Directory.Exists(dst)) Directory.Delete(dst, true); } catch (Exception ex) { ErrorLog.Write(ex); }
            if (previous != null) await OpenWorld(previous); else ShowPendingInfo();
            ShowStatus(Loc.T("Cancelled: the map was not created."));
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
            spec.Name, spec.Size, result.MinHeight, result.MaxHeight, result.Lakes, result.Spawns) + AnomalyText(result, spec.Size),
            Loc.T("Generate world"));
    }

    /// locations of the anomalies in game coordinates (E/W, N/S like the compass display in the game), plus craters and cracks
    static string AnomalyText(WorldGenerator.Result r, int size)
    {
        string text = "";
        if (r.Rivers.Count > 0)
            text += "\n\n" + Loc.F("{0} river(s) between lakes:", r.Rivers.Count) + string.Concat(r.Rivers.Select(v =>
                "\n• " + Loc.F("{0:0} m, water level {1} m, from {2} to {3}", v.Length, v.Level, Compass(v.Line[0], size), Compass(v.Line[^1], size))));
        if (r.Craters > 0 || r.Cracks > 0) text += "\n\n" + Loc.F("Wasteland: {0} craters, {1} earthquake cracks.", r.Craters, r.Cracks);
        return text;
    }

    /// place in game coordinates like the compass of the game: "512 E, 1,024 S"
    static string Compass(Point p, int size)
    {
        double x = p.X - size / 2, z = p.Y - size / 2;
        return Loc.F("{0:#,0} {1}, {2:#,0} {3}", Math.Abs(x), x < 0 ? "W" : "E", Math.Abs(z), z < 0 ? "S" : "N");
    }
}
