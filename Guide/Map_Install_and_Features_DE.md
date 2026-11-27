<!--
  Vorlage für den Export (Start › Export › „Karte als .zip“). Diesen Kommentar lässt YAMEE beim Export weg.
  Platzhalter, die YAMEE beim Export einsetzt:
    {MAP_NAME}      Name der Karte (= Ordnername)
    {GAME_VERSION}  Spielfassung des Projekts, z. B. „3.4“ oder „2.6 mit Undead Legacy“
    {WORLD_FOLDER}  passender Weltordner, z. B. %APPDATA%\7DaysToDie\GeneratedWorlds\
  Bereiche nur mit Brücken (Mod liegt bei) bzw. nur ohne Brücken – die Markierungen jeweils allein auf einer Zeile:
    {IF_BRIDGES} … {END_BRIDGES}
    {IF_NO_BRIDGES} … {END_NO_BRIDGES}
-->
# Karte „{MAP_NAME}“ installieren

**Voraussetzung:** 7 Days to Die {GAME_VERSION}.

{IF_BRIDGES}
Im Download liegen zwei Ordner: die Karte `{MAP_NAME}` und die Mod `YAMEE_Bridges`.

1. **Mod:** `YAMEE_Bridges` nach `<Spielordner>\Mods\` kopieren (Steam meist `...\steamapps\common\7 Days To Die\Mods`). Direkt darin müssen `ModInfo.xml` und `Prefabs` liegen, keine zusätzliche Ordnerebene.
2. **Karte:** `{MAP_NAME}` nach `{WORLD_FOLDER}` kopieren.
{END_BRIDGES}
{IF_NO_BRIDGES}
Im Download liegt der Ordner der Karte `{MAP_NAME}`.

1. **Karte:** `{MAP_NAME}` nach `{WORLD_FOLDER}` kopieren.
{END_NO_BRIDGES}

   Fehlt `GeneratedWorlds`, einfach anlegen. Die Dateien `*_processed*`, `*_half.png` und `checksums.txt` erzeugt das Spiel selbst.

**Hinweise**
{IF_BRIDGES}
- Ohne die Mod fehlen die Brücken (das Spiel kennt die Teile nicht). Sie ändert sonst nichts am Spiel.
- Server: Mod und Karte müssen auf dem Server liegen, die Mod zusätzlich bei jedem Spieler.
{END_BRIDGES}
{IF_NO_BRIDGES}
- Server: Die Karte muss auf dem Server liegen.
{END_NO_BRIDGES}
- Beim ersten Laden verarbeitet das Spiel die Karte („Processing world files“).
- Fehlersuche: `%APPDATA%\7DaysToDie\logs\output_log_client__*.txt`.
- Eigene YAMEE-Projekte kommen mit „Projekt ins Spiel“ in den richtigen Ordner; manuelles Kopieren ist nur für weitergegebene Karten nötig.
