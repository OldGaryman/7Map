using System.Windows;

namespace MapEditor;

/// Kleines Fenster „Spawnpunkte“ (Knopf „Liste …“ im Kasten Spawnpunkte, Wunsch des Nutzers): Schließen blendet es nur aus.
/// Zeilen und Knöpfe füllt das Hauptfenster.
public partial class SpawnWindow : Window
{
    /// true erst beim Beenden des Programms, sonst wird das Fenster nur versteckt
    public bool AllowClose { get; set; }

    public SpawnWindow() { InitializeComponent(); Loc.Translate(this); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }
}
