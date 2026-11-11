using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MapEditor;

/// Meldungsfenster im hellen oder dunklen Design (Wunsch des Nutzers 10.11.2026: alle Fenster hell und dunkel). Steht im Namensraum
/// MapEditor und verdeckt damit System.Windows.MessageBox: alle bisherigen Aufrufe benutzen dieses Fenster, ohne dass sich etwas ändert.
/// Strg+C kopiert den Text, Esc = Abbrechen/Nein/OK, Enter = Standardknopf.
public static class MessageBox
{
    public static MessageBoxResult Show(string text) => Show(null, text, "YAMEE", MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(Window owner, string text) => Show(owner, text, "YAMEE", MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(Window owner, string text, string caption) => Show(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage image) => Show(null, text, caption, button, image);

    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton button, MessageBoxImage image)
    {
        if (owner != null && !owner.IsVisible) owner = null;
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
        var result = button == MessageBoxButton.YesNo || button == MessageBoxButton.YesNoCancel ? MessageBoxResult.None : MessageBoxResult.OK;
        var win = new Window
        {
            Title = caption, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, MaxWidth = 640, MaxHeight = 700, FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
        };
        win.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        win.SetResourceReference(Control.ForegroundProperty, "TextBrush");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        void Add(string label, MessageBoxResult r, bool isDefault, bool isCancel)
        {
            var b = new Button { Content = Loc.T(label), MinWidth = 84, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
            b.Click += (_, _) => { result = r; win.Close(); };
            buttons.Children.Add(b);
        }
        switch (button)
        {
            case MessageBoxButton.OK: Add("OK", MessageBoxResult.OK, true, true); break;
            case MessageBoxButton.OKCancel: Add("OK", MessageBoxResult.OK, true, false); Add("Cancel", MessageBoxResult.Cancel, false, true); break;
            case MessageBoxButton.YesNo: Add("Yes", MessageBoxResult.Yes, true, false); Add("No", MessageBoxResult.No, false, true); break;
            default: Add("Yes", MessageBoxResult.Yes, true, false); Add("No", MessageBoxResult.No, false, false); Add("Cancel", MessageBoxResult.Cancel, false, true); break;
        }

        // Symbol wie beim Windows-Fenster (Schrift Bootstrap Icons)
        (string glyph, string brushKey) = image switch
        {
            MessageBoxImage.Error => (Icons.XCircle, "ErrorBrush"),
            MessageBoxImage.Warning => (Icons.ExclamationTriangle, "WarnBrush"),
            MessageBoxImage.Question => (Icons.QuestionCircle, "AccentBrush"),
            MessageBoxImage.Information => (Icons.InfoCircle, "AccentBrush"),
            _ => (null, null),
        };
        var body = new DockPanel { Margin = new Thickness(22, 20, 22, 18) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);
        if (glyph != null)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = IconUi.Font, FontSize = 30, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0) };
            icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            DockPanel.SetDock(icon, Dock.Left);
            body.Children.Add(icon);
        }
        var msg = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center, MinWidth = 220 };
        body.Children.Add(new ScrollViewer { Content = msg, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560 });
        win.Content = body;
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { try { Clipboard.SetText(text); } catch (Exception) { } e.Handled = true; }
            else if (e.Key == Key.Escape && button == MessageBoxButton.YesNoCancel) { result = MessageBoxResult.Cancel; win.Close(); }
        };
        ThemeManager.StyleWindow(win);
        win.ShowDialog();
        return result;
    }
}
