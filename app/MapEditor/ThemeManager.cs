using System.Windows;
using System.Windows.Media;

namespace MapEditor;

/// Helles oder dunkles Design (Ansicht → Dunkles Design, Wunsch des Nutzers 4.11.2026). Alle Farben der Oberfläche sind Brush-Ressourcen
/// aus Theme.xaml; hier werden ihre Farben umgesetzt (dieselben Brush-Objekte, deshalb wirkt es sofort in allen Fenstern).
public static class ThemeManager
{
    // Schlüssel → (hell, dunkel)
    static readonly (string Key, string Light, string Dark)[] Palette =
    {
        // Grün wie im Logo (hellere Töne des Verlaufs #4CAF50 → #2E7D32, Wunsch des Nutzers 4.11.2026)
        ("AccentBrush", "#43A047", "#43A047"),
        ("AccentHoverBrush", "#4CAF50", "#4CAF50"),
        ("AccentSoftBrush", "#E3F1E4", "#24392A"),
        ("PanelBrush", "#F4F4F2", "#272B30"),
        ("LineBrush", "#D6D6D0", "#3A4148"),
        ("ControlBorderBrush", "#C3C9BD", "#4B535C"),
        ("ControlHoverBrush", "#EEF4EE", "#333A42"),
        ("ControlPressedBrush", "#DCEBDD", "#3C444D"),
        ("TextBrush", "#1F2420", "#E3E7EC"),
        ("SubtleTextBrush", "#5B645F", "#9BA4AE"),
        ("DisabledTextBrush", "#9AA39D", "#6B747D"),
        ("WindowBrush", "#FFFFFF", "#1E2125"),
        ("CardBrush", "#FFFFFF", "#272B30"),
        ("FieldBrush", "#FFFFFF", "#16191C"),
        ("SidebarBrush", "#EEF0EC", "#22262B"),
        ("TabBarBrush", "#E9ECE6", "#1A1D21"),
        ("ToolBoxBrush", "#F6F8F5", "#2D3238"),
        ("SoftLineBrush", "#E3E6E1", "#343A42"),
        ("HintTextBrush", "#7A837D", "#8D96A0"),
        ("MapBackBrush", "#E8E8E4", "#16191C"),
        ("OverlayBrush", "#CCFFFFFF", "#CC1E2125"),
        ("LegendBrush", "#E6FFFFFF", "#E6272B30"),
        ("ErrorBrush", "#B22222", "#FF8A80"),
        ("WarnBrush", "#D98A00", "#F0B040"),
        ("NoteBrush", "#FFF6D5", "#3A3524"),
        ("NoteBorderBrush", "#E5C55A", "#7A6A2C"),
        ("PurpleTextBrush", "#6A3FB0", "#B79CF0"),
        ("OrangeTextBrush", "#B25A00", "#E6A060"),
    };

    public static bool Dark { get; private set; }

    // ---------- Titelleiste der Fenster hell oder dunkel (Windows 10/11) ----------

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static bool hooked;

    /// einmal beim Start: jedes Fenster bekommt beim Laden die passende Titelleiste (auch Fenster, die erst später aufgehen)
    public static void Hook()
    {
        if (hooked) return;
        hooked = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => TitleBar((Window)s)));
    }

    static void TitleBar(Window w)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (handle == IntPtr.Zero) return;
            int on = Dark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));   // 19 = ältere Windows-10-Fassungen
        }
        catch (Exception) { }   // kein dunkler Rahmen möglich: Fenster bleibt, wie es ist
    }

    /// für Fenster, die im Code gebaut werden: Titelleiste jetzt oder sobald das Fenster ein Handle hat
    public static void StyleWindow(Window w)
    {
        Hook();
        w.SourceInitialized += (_, _) => TitleBar(w);
    }

    public static void Apply(bool dark)
    {
        Dark = dark;
        foreach (var (key, light, darkColor) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(dark ? darkColor : light);
            // neues Brush-Objekt eintragen: Formatvorlagen halten ihre Brushes eingefroren, deshalb sind alle Verweise DynamicResource
            Application.Current.Resources[key] = new SolidColorBrush(color);
        }
        Hook();
        foreach (Window w in Application.Current.Windows) TitleBar(w);   // schon offene Fenster
    }

    /// Farbe aus den Ressourcen (für Zeichnen im Code), sonst die Ersatzfarbe
    public static Brush Brush(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    public static Brush MapBack => Brush("MapBackBrush", Brushes.Gainsboro);
}
