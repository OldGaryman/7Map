using System.IO;
using System.Windows;
using MapEditor.Map;

namespace MapEditor;

/// Terraingenerator (Wunsch des Nutzers 6.11.2026): neue Welt ohne Städte, Straßen und Gebäude in den Projektordner schreiben
/// und öffnen. „Übernehmen in 7 Days to Die“ legt den Ordner im Spiel beim ersten Mal an.
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
            // nichts löschen, nur beiseitelegen (wie beim Anlegen aus dem Spiel)
            string aside = dst + "_old_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            Directory.Move(dst, aside);
            Backups.MoveWithProject(dst, aside);
        }
        // Weltordner im Spiel merken (gibt es noch nicht – „Übernehmen in 7 Days to Die“ legt ihn an)
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
            try { TerrainCopy.Save(dst); } catch (Exception ex) { ErrorLog.Write(ex); }   // Terrainkopie für „Städte erzeugen → Ersetzen“
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

    /// Fundorte der Anomalien in Spielkoordinaten (O/W, N/S wie die Kompassanzeige im Spiel), dazu Krater und Risse
    static string AnomalyText(WorldGenerator.Result r)
    {
        string text = "";
        if (r.Craters > 0 || r.Cracks > 0) text += "\n\n" + Loc.F("Wasteland: {0} craters, {1} earthquake cracks.", r.Craters, r.Cracks);
        return text;
    }
}
