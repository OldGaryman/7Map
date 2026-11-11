using System.Xml;

namespace MapEditor;

/// XML-Dateien laden ohne DTD-Verarbeitung: Dateien aus fremden Weltordnern oder Mods können keine verschachtelten
/// Entitäten („Billion Laughs“) enthalten, die den Speicher füllen (Prüfbericht F11). Die Dateien des Spiels haben
/// keine DTD, es ändert sich also nichts; eine DTD wird übersprungen statt abzulehnen.
public static class XmlSafe
{
    public static XmlDocument Load(string file)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(file, settings);
        var doc = new XmlDocument { XmlResolver = null };
        doc.Load(reader);
        return doc;
    }
}
