using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MapEditor;

/// Language of the interface (the user's request, 2026-10-29): the texts in the program are English; a language file
/// Languages\&lt;language&gt;.ini next to YAMEE.exe translates them. Each line "English text = translation", sections [ … ]
/// serve only for order, ; = comment, \n = line break, \= = equals sign in the text. If a translation is missing,
/// the English text remains.
/// Windows are translated after InitializeComponent with Translate(window) (Text, Content, Header, ToolTip, Title);
/// texts from the code go through T(…) or F(…) with placeholders {0}, {1} …
public static class Loc
{
    public const string English = "English";
    static Dictionary<string, string> map = new();
    /// number format English for all languages (1,234.5; the user's request: app for everyone). Input may use comma or point.
    public static readonly CultureInfo Num = CultureInfo.InvariantCulture;

    public static string Language { get; private set; } = English;
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Languages");

    /// language for the selection: Id = file name (German), Name = display in the language itself (Deutsch)
    public sealed record LanguageInfo(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// available languages: English plus every file in Languages\. The display name is provided by the line
    /// "LanguageName = …" in the file (if it is missing, the file name).
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

    /// load language; null = according to the Windows language (German if available, otherwise English)
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

    // \n = line break, \= = equals sign in the text (otherwise " = " would be the separator)
    static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\=", "=");

    /// translation of an English text (unknown = unchanged)
    public static string T(string english) => english != null && map.TryGetValue(english, out var t) ? t : english;

    /// translation with placeholders {0}, {1} … (numbers in English format)
    public static string F(string english, params object[] args) => string.Format(Num, T(english), args);

    // English original text per element and property (for switching the language at runtime) and the text set last
    sealed class Original { public string English, Shown; }
    static readonly ConditionalWeakTable<object, Dictionary<string, Original>> originals = new();

    /// translate all fixed texts of a window (logical tree: also tabs that are currently not visible);
    /// call directly after InitializeComponent, then it remembers the English originals
    public static void Translate(DependencyObject root) => Walk(root, true);

    /// translate again after a language change (Load). Texts that the code has set itself in the meantime stay
    /// untouched – the window has to rewrite those itself.
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
