# YAMEE – Yet Another Map Editor Extra

Map editor for 7 Days to Die (Windows). Author: Old Garyman 2026.

## 1.2.0 – 2026-11-12
- Large edit steps (generating towns, roads, wilderness POIs) need less memory until you save
- "Generate roads": planning the network is about twice as fast (same result)
- New project from the game: if copying stops (e.g. disk full), no half project is left behind, and a working copy that was put aside goes back to its place
- If a project cannot be opened, the previous one is opened again
- Deleting an entry of the edit order: if a later step cannot be recalculated afterwards, all steps are now kept
- Very long roads (several km) need only about half as much memory when applied
- The map with labels (from 75 % zoom) pans and zooms much more smoothly
- At low zoom too (whole map, many buildings), panning and zooming now run evenly without stutter
- No more short freeze when a zoom level is reached for the first time or when zooming out again after a change
- Your own values (user.ini) are no longer lost when another program briefly locks the file while saving

## 1.1.0 – 2026-11-11
- Everything in one place: projects (subfolder `Maps`), backups and settings live in one project folder. You choose it during installation or in the welcome window. Nothing is moved: copy existing projects and settings into it yourself
- Driveways without their own height offset (e.g. part_driveway_countrytown_12) were placed 1 m too high next to new buildings; fixed

## 1.0.2 – 2026-11-11
- Settings (About YAMEE) now states that YAMEE is licensed under the GPL-3.0, with links to the license text and the source code on GitHub
- The installer now also installs LICENSE.txt and COPYRIGHT.txt in the program folder

## 1.0.1 – 2026-11-11
- Generate world: lake shores now always slope down by at least 30 % (before, there were flat beaches). The water's edge in the game is smooth instead of jagged, and the wet strip without water at the shore is half as large

## 1.0.0 – 2026-11-11
- Generate roads: roads now stay close to the ground instead of running on embankments over long stretches (cut up to 2.2 m, fill up to 1.2 m, kinks smoothed out); in a test the embankment length dropped from 30 km to 6.5 km
- Generate world: the biome shares are now matched exactly
- New default values (e.g. asphalt 16 m wide, 8 % grade); all town values are in default.ini, with explanations

## 0.9.9 – 2026-11-10
- Generate world: the center of the map is always pine forest
- All windows and messages come in light and dark
- Grades are shown in percent everywhere
- Notices can no longer be switched off (every message appears again)

## 0.9.8 – 2026-11-10
- Every tool has its values on the left and saves them at once; "Reset values" exists for each tool separately
- Settings: only paths and language are left; all changeable values are in default.ini and user.ini
- Generate world: one spawn point in the middle of the map

## 0.9.7 – 2026-11-10
- A map without a world shows what you can do: open a project, create one from a game map or generate a new world
- World tab: the creation tools (world, towns, roads, wilderness POIs) form their own group
- Shorter messages with a hint for the next step; a hint to generate roads before placing wilderness POIs

## 0.9.6 – 2026-11-10
- Wilderness paths: 3–5 m wide depending on the building, with a level verge, cut into the ground on slopes

## 0.9.5 – 2026-11-10
- New: Generate wilderness POIs – cabins, camps, caves etc. outside the towns, following the game's rules, with gravel paths to the nearest road
- Buildings with randomly placed parts (e.g. signs) now get just one part instead of all stacked on top of each other

## 0.9.4 – 2026-11-09
- Towns: number per biome adjustable
- Towns also find room in steep mountains (main part cut into the slope as a terrace)

## 0.9.3 – 2026-11-08
- Towns: in about every second town the trader sits near the center

## 0.9.2 – 2026-11-08
- Generate roads: fewer dead ends
- Roads that run close to a town street are joined with a short link

## 0.9.1 – 2026-11-08
- Generate roads: roads run side by side less often
- Free town exits are joined to nearby roads

## 0.9.0 – 2026-11-08
- Generate roads: at least 80 m distance to the map edge

## 0.8.9 – 2026-11-07
- Towns: every town has 2–4 open connections for roads, pointing in different directions

## 0.8.8 – 2026-11-07
- Generate towns on a map that already has towns: choose between Replace, Add and Cancel
- Some German notices that were still in English are now translated

## 0.8.7 – 2026-11-07
- Towns: open connections no longer point to the map edge

## 0.8.6 – 2026-11-07
- Generate roads: the "Build roads" button writes the planned network into the map (cuts, embankments, slopes, connection to the town exits)

## 0.8.5 – 2026-11-07
- Generate roads: a road no longer crosses the road it branches from; round curves at town exits

## 0.8.4 – 2026-11-07
- Generate roads: wide curves instead of dense zigzags

## 0.8.3 – 2026-11-07
- New: Generate roads – plans a road network between the towns and shows it as a preview on the map

## 0.8.2 – 2026-11-07
- Towns: every town lies within one biome; roads no longer cross or touch each other
- Free space in front of the gateway; transition piece to the wide road (part_highway_transition)

## 0.8.1 – 2026-11-07
- Towns: location selectable (plains and valleys, mixed, hilltops); hilltops are leveled
- Steps between roads are smoothed out

## 0.8.0 – 2026-11-07
- Towns: the connecting roads run in calm curves instead of hooks and zigzags

## 0.7.9 – 2026-11-07
- Towns rebuilt: level main part plus districts at their natural height, gateway with a trader per biome, winding roads

## 0.7.8 – 2026-11-07
- New: Generate towns – places towns like the game does (town types per biome, number and size by map size)

## 0.7.7 – 2026-11-07
- Generate world: striking rock walls, rock towers and rock domes in the pine forest

## 0.7.6 – 2026-11-07
- Generate world: desert plateaus a little higher and partly lower, more variety

## 0.7.5 – 2026-11-07
- Generate world: hillier wasteland, rougher desert with single pointed mountains, more variety in steepness

## 0.7.4 – 2026-11-06
- Generate world: bomb craters and earthquake cracks in the wasteland, steep walls in the desert, rare anomalies (meteor crater, sinkhole, rock needles …)
- More natural lake shores

## 0.7.3 – 2026-11-06
- Generate world: mountains from the game's height stamps, rockier slopes, erosion by rain

## 0.7.2 – 2026-11-06
- Generate world: higher mountains, valleys and gorges, irregular lakes also in the mountains

## 0.7.1 – 2026-11-06
- New: Generate world – a completely new world (size 2–10 km, seed, biome shares, mountains, hills, lakes), for now without towns, roads and buildings

## 0.7.0 – 2026-11-06
- Water: all ground below the water level and a strip along the shore gets water (no more bushes in the middle of shallow water)

## 0.6.9 – 2026-11-06
- Lakes, rivers and "Fill craters": biome under water selectable

## 0.6.8 – 2026-11-06
- Remove water: removes only the water, the terrain stays

## 0.6.7 – 2026-11-06
- Water follows the ground in every editing step (eraser, roads, buildings, tiles)

## 0.6.6 – 2026-11-06
- Water follows the ground when you change the terrain with the brush, mountains or mountain ranges

## 0.6.5 – 2026-11-05
- The status bar shows the water level under the mouse

## 0.6.4 – 2026-11-05
- Fill craters: you only set the water height above the clicked point

## 0.6.3 – 2026-11-05
- Fill craters: water level and minimum depth selectable

## 0.6.2 – 2026-11-05
- Fill craters: water flows over thresholds; a click runs downhill to the lowest point

## 0.6.1 – 2026-11-05
- New: Fill craters (Water tab)

## 0.6.0 – 2026-11-05
- River: adjustable irregularity of the banks

## 0.5.9 – 2026-11-05
- River in two steps: first click the path, then edit the bank line

## 0.5.8 – 2026-11-05
- New: River tool; new bodies of water take over the water level of adjoining water

## 0.5.7 – 2026-11-05
- Lakes: water depth separate from the digging depth, shore width also works outside the water

## 0.5.6 – 2026-11-05
- Lakes: angular outline, rougher shores and lake bed

## 0.5.5 – 2026-11-05
- New: Water tab with the tools "Lake" and "Remove water"

## 0.5.4 – 2026-11-05
- New: "Grass edge" for asphalt and gravel – a level strip next to the road without surface

## 0.5.3 – 2026-11-05
- Building list: road connections with automatic width

## 0.5.2 – 2026-11-05
- Building list: "Biome" column and filter (what the game builds in which biome)
- Buildings can be placed even while a brush is switched on

## 0.5.1 – 2026-11-05
- Mountain ranges: separate options for roads and buildings underneath

## 0.5.0 – 2026-11-05
- Settings window in the new layout

## 0.4.9 – 2026-11-05
- The height profile at the bottom only appears when it is needed

## 0.4.8 – 2026-11-05
- The box above the map (road, edit road) in the uniform layout

## 0.4.7 – 2026-11-05
- Uniform layout: same row heights, label and field widths in all tools

## 0.4.6 – 2026-11-05
- Rulers easy to read, terrain brush neatly aligned

## 0.4.5 – 2026-11-05
- The left side can be resized and disappears when no tool is selected

## 0.4.4 – 2026-11-05
- All values on the left directly visible (no "More …"), slightly wider

## 0.4.3 – 2026-11-04
- The values of the selected tool are on the left, the tool toolbar is gone
- Building list on the right can be collapsed, lighter green like the logo, sharper text

## 0.4.2 – 2026-11-04
- Ribbon: each tab shows all its tools; new tabs Spawn points, View and Settings
- New: rulers at the map edge and dark theme

## 0.4.1 – 2026-11-04
- New interface with a ribbon (World, Terrain, Mountain ranges, Roads, Settings)
- Brushes, mountains and eraser take effect at once; a question appears if you switch tools while a road is unfinished

## 0.4.0 – 2026-11-04
- Bug fixed: clicking the text in the box above the map caused an error message

## 0.3.9 – 2026-11-04
- Mountain range: new value "Ruggedness" (pointed peaks, rocky ridges), jagged edges rarer, gentle foothills also beyond the line

## 0.3.8 – 2026-11-04
- Mountain range: the drawn area can be edited before applying (drag, insert, delete points)
- Stronger roughness, more distinct hill ridges

## 0.3.7 – 2026-11-04
- New: Mountain range – draw an area with the mouse and a mountain range, hill chain or highland is built inside it; foothills gentle, steep, jagged or mixed

## 0.3.6 – 2026-11-04
- Roads start and end at town tiles and building entrances level for 4 m, exactly at the game's height
- Warnings while building roads in a yellow box

## 0.3.5 – 2026-11-04
- New: place mountains and hills with one click (mountain range, plateau, hill, dome) – using the game's height stamps

## 0.3.4 – 2026-11-03
- New icons (Bootstrap Icons) in all buttons and building groups

## 0.3.3 – 2026-11-03
- Terrain brush: more shapes (diamond, hexagon, ellipse, cross, star, blob, random)

## 0.3.2 – 2026-11-03
- Edit road: crossing roads follow smoothly; entrances and tiles keep their height
- Automatic banking in curves
- Roads can become narrower or wider (width per point); connection to tiles in the right width
- All connections show their road width; more buildings have connection points
- Town tiles snap to neighboring tiles and show their road exits (green = matches)
- Bug fixed: a road sometimes joined a tile one step too low

## 0.3.1 – 2026-11-02
- New: edit an existing road – change height, grade and banking (cross slope) between markers

## 0.3.0 – 2026-11-02
- The height readout at the bottom left updates at once when the terrain changes

## 0.2.9 – 2026-11-02
- Terrain brush: town tiles always keep their height

## 0.2.8 – 2026-11-02
- Terrain brush: buildings can be taken along, height limits (min/max)

## 0.2.7 – 2026-11-02
- Terrain brush: leave roads fixed, affect them slightly or take them along; grade limit

## 0.2.6 – 2026-11-02
- New: terrain brush (smooth, raise, lower, level, dents, weather)

## 0.2.5 – 2026-11-02
- Insert, move, rotate, swap and delete town tiles
- New logo and program icon; new, calmer look in all windows
- Building list: tier as stars, sortable; biome legend on the map
- More stability: safer saving, backups and settings, error log, less memory use

## 0.2.0 – 2026-10-30
- Apply map changes to an existing saved game (preview, full backup)
- Notice when the world was changed outside of YAMEE
- Town tiles show roads even if the world was never loaded in the game
- Installer: notice about updates and version history

## 0.1.0 – 2026-10-30 (first release)
- Load game worlds as projects, undo/redo, backups
- Draw asphalt and gravel roads, eraser, biome brush
- Place, move, rotate, swap and delete buildings
- Spawn points for play and test
- German and English, automatic detection of the game
