using System.Windows;

namespace MapEditor;

/// Small window with the data of a building (the user's request): position, build height, rotation, move, delete.
/// Closing only hides it; the main window fills content and buttons (MainWindow.PrefabEdit.cs).
public partial class PrefabWindow : Window
{
    /// true only when the program exits, otherwise the window is just hidden
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
