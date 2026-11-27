using System.Xml;

namespace MapEditor;

/// Load XML files without DTD processing: files from foreign world folders or mods cannot contain nested
/// entities ("Billion Laughs") that fill the memory (audit finding F11). The game's files have
/// no DTD, so nothing changes; a DTD is skipped instead of rejected.
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
