namespace MapEditor.Map;

/// Bauplatz einer Stadt-Kachel in Weltkoordinaten (Südwest-Ecke, Breite in x, Tiefe in z)
public sealed record TileLot(PlacedPrefab Tile, LotMarker Marker, string District, int X, int Z, int Width, int Depth);

/// Bauplätze der Stadt-Kacheln (rwg_tile_*) und welche Gebäude darauf passen (Wunsch des Nutzers: Gebäude in Kacheln austauschen).
/// An der Originalwelt geprüft (29.10.2026, metest `tilepois`): alle 1.590 bebauten Plätze erfüllen ohne Ausnahme
/// – Gebäude genau so groß wie der Platz (gedreht), – Tag = Viertel aus dem Kachelnamen (rwg_tile_countrytown_… → countrytown),
/// – Zusatz-Tag des Platzes (nodiagonal, culdesac …) hat auch das Gebäude, – Drehung = Platz + Kachel + RotationToFaceNorth.
public static class TileLots
{
    /// Viertel einer Kachel: erstes Wort nach „rwg_tile_“ (countrytown, residential, downtown …)
    public static string District(PlacedPrefab tile) =>
        tile.Name.Length > 9 ? tile.Name[9..].Split('_')[0].ToLowerInvariant() : "";

    /// alle Bauplätze einer Kachel in Weltkoordinaten (Rechteck wie die Kachel gedreht, kleinste Ecke)
    public static IEnumerable<TileLot> Of(PlacedPrefab tile)
    {
        if (tile.Def == null) yield break;
        string district = District(tile);
        foreach (var m in tile.Def.Lots)
        {
            var (ax, az) = PlacedPrefab.Rotate(m.X, m.Z, tile.Rotation, tile.Def.SizeX, tile.Def.SizeZ);
            var (bx, bz) = PlacedPrefab.Rotate(m.X + m.SizeX, m.Z + m.SizeZ, tile.Rotation, tile.Def.SizeX, tile.Def.SizeZ);
            int x0 = (int)Math.Round(Math.Min(ax, bx)), z0 = (int)Math.Round(Math.Min(az, bz));
            yield return new TileLot(tile, m, district, tile.X + x0, tile.Z + z0,
                (int)Math.Round(Math.Abs(bx - ax)), (int)Math.Round(Math.Abs(bz - az)));
        }
    }

    /// Bauplatz, auf dem das Gebäude steht (Mitte der Grundfläche liegt darin); null = keiner
    public static TileLot Under(WorldData w, PlacedPrefab p)
    {
        if (p.Def == null || p.IsTile || p.Def.Kind == PrefabKind.Part) return null;
        double cx = p.X + p.SizeX / 2.0, cz = p.Z + p.SizeZ / 2.0;
        foreach (var t in w.Prefabs)
        {
            if (!t.IsTile || !t.Contains(cx, cz)) continue;
            foreach (var lot in Of(t))
                if (cx > lot.X && cx < lot.X + lot.Width && cz > lot.Z && cz < lot.Z + lot.Depth) return lot;
        }
        return null;
    }

    /// Drehung eines Gebäudes auf dem Platz (die Vorderseite zeigt zur Straße)
    public static int RotationFor(TileLot lot, PrefabDef d) => ((lot.Marker.Rotation + lot.Tile.Rotation + (d.RotationToFaceNorth ?? 0)) % 4 + 4) % 4;

    /// passt das Gebäude auf den Platz? Viertel-Tag, Zusatz-Tag und genau die Größe des Platzes (in seiner Drehung)
    public static bool Fits(PrefabDef d, TileLot lot)
    {
        if (d == null || !d.Found || d.Kind is PrefabKind.Part or PrefabKind.Tile) return false;
        if (!d.Tags.Contains(lot.District)) return false;
        if (!string.IsNullOrEmpty(lot.Marker.Tag) && !d.Tags.Contains(lot.Marker.Tag)) return false;
        bool turned = RotationFor(lot, d) % 2 == 1;
        return (turned ? d.SizeZ : d.SizeX) == lot.Width && (turned ? d.SizeX : d.SizeZ) == lot.Depth;
    }

    /// neues Gebäude auf dem Platz: Lage = Platz, Drehung wie das Spiel, Höhe = Kachel-y + Platz-y + YOffset des Gebäudes
    public static PlacedPrefab Place(TileLot lot, string name, PrefabDef d) => new()
    {
        Name = name, Def = d, X = lot.X, Z = lot.Z, Rotation = RotationFor(lot, d),
        Y = lot.Tile.Y + lot.Marker.Y + d.YOffset,
    };
}
