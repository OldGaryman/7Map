using System.Windows;

namespace MapEditor;

/// Kleines Fenster „Bearbeitungsreihenfolge“ (Knopf „☰ Schritte“ oben): Schließen blendet es nur aus.
/// Inhalt und Knöpfe füllt das Hauptfenster.
public partial class StepsWindow : Window
{
    /// true erst beim Beenden des Programms, sonst wird das Fenster nur versteckt
    public bool AllowClose { get; set; }
    public event Action Hidden;

    public StepsWindow() { InitializeComponent(); Loc.Translate(this); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
        Hidden?.Invoke();
    }
}
