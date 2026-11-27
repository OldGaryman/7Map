<!--
  Template for the export (Home › Export › "Map as .zip"). YAMEE leaves this comment out when exporting.
  Placeholders YAMEE fills in when exporting:
    {MAP_NAME}      name of the map (= folder name)
    {GAME_VERSION}  game version of the project, e.g. "3.4" or "2.6 with Undead Legacy"
    {WORLD_FOLDER}  matching world folder, e.g. %APPDATA%\7DaysToDie\GeneratedWorlds\
  Parts only with bridges (the mod is included) or only without bridges – each marker alone on its line:
    {IF_BRIDGES} … {END_BRIDGES}
    {IF_NO_BRIDGES} … {END_NO_BRIDGES}
-->
# Installing the map "{MAP_NAME}"

**Requirement:** 7 Days to Die {GAME_VERSION}.

{IF_BRIDGES}
The download contains two folders: the map `{MAP_NAME}` and the mod `YAMEE_Bridges`.

1. **Mod:** Copy `YAMEE_Bridges` to `<game folder>\Mods\` (Steam usually `...\steamapps\common\7 Days To Die\Mods`). `ModInfo.xml` and `Prefabs` must sit directly inside it, with no extra folder level.
2. **Map:** Copy `{MAP_NAME}` to `{WORLD_FOLDER}`.
{END_BRIDGES}
{IF_NO_BRIDGES}
The download contains the map folder `{MAP_NAME}`.

1. **Map:** Copy `{MAP_NAME}` to `{WORLD_FOLDER}`.
{END_NO_BRIDGES}

   Create `GeneratedWorlds` if it does not exist. The game creates `*_processed*`, `*_half.png` and `checksums.txt` itself.

**Notes**
{IF_BRIDGES}
- Without the mod the bridges are missing (the game does not know the pieces). It changes nothing else.
- Servers: map and mod go on the server, the mod also on every player's PC.
{END_BRIDGES}
{IF_NO_BRIDGES}
- Servers: the map goes on the server.
{END_NO_BRIDGES}
- The first load runs "Processing world files".
- Troubleshooting: `%APPDATA%\7DaysToDie\logs\output_log_client__*.txt`.
- Your own YAMEE projects go to the right folder with "Project to game"; manual copying is only needed for maps you download.
