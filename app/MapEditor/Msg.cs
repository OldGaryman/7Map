using System.Windows;

namespace MapEditor;

/// Hinweismeldungen (nur Information, ein „OK“). Seit 10.11.2026 gibt es kein „Diese Meldung nicht mehr anzeigen“ mehr
/// (Wunsch des Nutzers): jede Meldung erscheint immer. Parameter settings und key bleiben, damit die Aufrufe unverändert sind.
public static class Msg
{
    public static void Info(Window owner, AppSettings settings, string key, string text, string title) =>
        MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
