using System.Windows;

namespace MapEditor;

/// Small window "Edit order" ("☰ Steps" button at the top): closing only hides it.
/// The main window fills content and buttons.
public partial class StepsWindow : Window
{
    /// true only when the program exits, otherwise the window is just hidden
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
