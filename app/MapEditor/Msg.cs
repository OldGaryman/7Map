using System.Windows;

namespace MapEditor;

/// Information messages (information only, one "OK"). Since 2026-11-10 there is no "Do not show this message again" any more
/// (the user's request): every message always appears. The parameters settings and key remain so that the calls stay unchanged.
public static class Msg
{
    public static void Info(Window owner, AppSettings settings, string key, string text, string title) =>
        MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
