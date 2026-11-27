using System.Windows;

namespace MapEditor;

/// Small window "Spawn points" ("List …" button in the spawn points box, the user's request): closing only hides it.
/// The main window fills rows and buttons.
public partial class SpawnWindow : Window
{
    /// true only when the program exits, otherwise the window is just hidden
    public bool AllowClose { get; set; }

    public SpawnWindow() { InitializeComponent(); Loc.Translate(this); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }
}
