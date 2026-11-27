using System.Windows;

namespace MapEditor;

/// Information messages (information only, one "OK"). With the check mark "Disable all info windows" (the user's request 2026-11-20)
/// they can be switched off for good (settings.InfoWindowsOff); the tab Settings switches them on again. The parameter key remains
/// so that the calls stay unchanged.
public static class Msg
{
    public static void Info(Window owner, AppSettings settings, string key, string text, string title)
    {
        if (settings.InfoWindowsOff) return;
        MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Information,
            "Disable all info windows. Can be enabled again in Settings.", off => { settings.InfoWindowsOff = off; settings.Save(); });
    }
}
