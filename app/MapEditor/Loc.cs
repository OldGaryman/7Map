using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MapEditor;

/// Sprache der Oberfläche (Wunsch des Nutzers 29.10.2026): Die Texte im Programm sind englisch; eine Sprachdatei
/// Languages\&lt;Sprache&gt;.ini neben YAMEE.exe übersetzt sie. Jede Zeile „English text = Übersetzung“, Abschnitte [ … ]
/// dienen nur der Ordnung, ; = Kommentar, \n = Zeilenumbruch, \= = Gleichheitszeichen im Text. Fehlt eine Übersetzung,
/// bleibt der englische Text.
/// Fenster werden nach InitializeComponent mit Translate(window) übersetzt (Text, Content, Header, ToolTip, Title);
/// Texte aus dem Code laufen über T(…) bzw. F(…) mit Platzhaltern {0}, {1} …
public static class Loc
{
    public const string English = "English";
    static Dictionary<string, string> map = new();
    /// Zahlenformat für alle Sprachen englisch (1,234.5; Wunsch des Nutzers: App für alle). Eingaben dürfen Komma oder Punkt haben.
    public static readonly CultureInfo Num = CultureInfo.InvariantCulture;

    public static string Language { get; private set; } = English;
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Languages");

    /// Sprache für die Auswahl: Id = Dateiname (German), Name = Anzeige in der Sprache selbst (Deutsch)
    public sealed record LanguageInfo(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// verfügbare Sprachen: Englisch plus jede Datei in Languages\. Den Anzeigenamen liefert die Zeile
    /// „LanguageName = …“ in der Datei (fehlt sie, der Dateiname).
    public static List<LanguageInfo> Available()
    {
        var list = new List<LanguageInfo> { new(English, English) };
        if (!Directory.Exists(Folder)) return list;
        foreach (var file in Directory.GetFiles(Folder, "*.ini").OrderBy(f => f))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (id == English) continue;
            string name = File.ReadLines(file, Encoding.UTF8).Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("LanguageName = ", StringComparison.Ordinal))?["LanguageName = ".Length..].Trim();
            list.Add(new(id, string.IsNullOrEmpty(name) ? id : name));
        }
        return list;
    }

    /// Sprache laden; null = nach der Sprache von Windows (Deutsch, wenn vorhanden, sonst Englisch)
    public static void Load(string language)
    {
        language ??= CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "German" : English;
        map = new Dictionary<string, string>(StringComparer.Ordinal);
        Language = English;
        string file = Path.Combine(Folder, language + ".ini");
        if (language == English || !File.Exists(file)) return;
        foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line == "" || line.StartsWith(';') || line.StartsWith('[')) continue;
            int eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq <= 0) continue;
            string key = Unescape(line[..eq].Trim()), val = Unescape(line[(eq + 3)..].Trim());
            if (val != "") map[key] = val;
        }
        Language = language;
    }

    // \n = Zeilenumbruch, \= = Gleichheitszeichen im Text (sonst wäre „ = “ das Trennzeichen)
    static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\=", "=");

    /// Übersetzung eines englischen Textes (unbekannt = unverändert)
    public static string T(string english) => english != null && map.TryGetValue(english, out var t) ? t : english;

    /// Übersetzung mit Platzhaltern {0}, {1} … (Zahlen im englischen Format)
    public static string F(string english, params object[] args) => string.Format(Num, T(english), args);

    // englischer Originaltext je Element und Eigenschaft (für den Sprachwechsel zur Laufzeit) und der zuletzt gesetzte Text
    sealed class Original { public string English, Shown; }
    static readonly ConditionalWeakTable<object, Dictionary<string, Original>> originals = new();

    /// alle festen Texte eines Fensters übersetzen (logischer Baum: auch Reiter, die gerade nicht sichtbar sind);
    /// direkt nach InitializeComponent aufrufen, dann merkt es sich die englischen Originale
    public static void Translate(DependencyObject root) => Walk(root, true);

    /// nach einem Sprachwechsel (Load) erneut übersetzen. Texte, die der Code inzwischen selbst gesetzt hat, bleiben
    /// unberührt – die muss das Fenster selbst neu schreiben.
    public static void Retranslate(DependencyObject root) => Walk(root, false);

    static void Walk(object o, bool first)
    {
        switch (o)
        {
            case Window w: Handle(w, "Title", w.Title, v => w.Title = v, first); break;
            case TextBlock tb when tb.Inlines.Count > 1 || tb.Inlines.FirstInline is not Run:
                foreach (var run in tb.Inlines.OfType<Run>()) Handle(run, "Text", run.Text, v => run.Text = v, first);
                break;
            case TextBlock tb when System.Windows.Data.BindingOperations.GetBinding(tb, TextBlock.TextProperty) == null:
                Handle(tb, "Text", tb.Text, v => tb.Text = v, first);
                break;
        }
        if (o is HeaderedContentControl h && h.Header is string hs) Handle(h, "Header", hs, v => h.Header = v, first);
        if (o is ContentControl c && c.Content is string cs) Handle(c, "Content", cs, v => c.Content = v, first);
        if (o is FrameworkElement fe && fe.ToolTip is string ts) Handle(fe, "ToolTip", ts, v => fe.ToolTip = v, first);
        if (o is DependencyObject d)
            foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child, first);
    }

    static void Handle(object o, string property, string current, Action<string> set, bool first)
    {
        if (current == null) return;
        if (first)
        {
            var list = originals.GetOrCreateValue(o);
            if (list.ContainsKey(property)) return;
            var orig = new Original { English = current, Shown = T(current) };
            list[property] = orig;
            if (orig.Shown != current) set(orig.Shown);
        }
        else if (originals.TryGetValue(o, out var list) && list.TryGetValue(property, out var orig) && orig.Shown == current)
        {
            orig.Shown = T(orig.English);
            set(orig.Shown);
        }
    }
}
