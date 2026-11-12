using System.Windows;
using System.Windows.Media;

namespace MapEditor;

/// Light or dark theme (View → Dark theme, the user's request, 2026-11-04). All colors of the interface are brush resources
/// from Theme.xaml; here their colors are set (the same brush objects, so it takes effect at once in all windows).
public static class ThemeManager
{
    // key → (light, dark)
    static readonly (string Key, string Light, string Dark)[] Palette =
    {
        // green like in the logo (lighter tones of the gradient #4CAF50 → #2E7D32, the user's request, 2026-11-04)
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

    // ---------- title bar of the windows light or dark (Windows 10/11) ----------

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static bool hooked;

    /// once at start: every window gets the matching title bar when loading (also windows that open later)
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
            if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));   // 19 = older Windows 10 versions
        }
        catch (Exception) { }   // no dark frame possible: the window stays as it is
    }

    /// for windows built in code: title bar now or as soon as the window has a handle
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
            // enter a new brush object: styles keep their brushes frozen, therefore all references are DynamicResource
            Application.Current.Resources[key] = new SolidColorBrush(color);
        }
        Hook();
        foreach (Window w in Application.Current.Windows) TitleBar(w);   // windows that are already open
    }

    /// color from the resources (for drawing in code), otherwise the fallback color
    public static Brush Brush(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    public static Brush MapBack => Brush("MapBackBrush", Brushes.Gainsboro);
}
