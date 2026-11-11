using System.Windows;

namespace MapEditor;

/// Kleines Fenster mit den Daten eines Gebäudes (Wunsch des Nutzers): Lage, Bauhöhe, Drehung, Verschieben, Löschen.
/// Schließen blendet es nur aus; Inhalt und Knöpfe füllt das Hauptfenster (MainWindow.PrefabEdit.cs).
public partial class PrefabWindow : Window
{
    /// true erst beim Beenden des Programms, sonst wird das Fenster nur versteckt
    public bool AllowClose { get; set; }
    public event Action Hidden;

    public PrefabWindow() { InitializeComponent(); Loc.Translate(this); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
        Hidden?.Invoke();
    }
}
