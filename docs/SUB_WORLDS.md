# Choosing which sub-worlds to load

Code: `Tools/LevelEditorPlugin/SubWorlds/`.

## How MEC splits SP_MainCity

Found in the game's asset list (Frosty cache, 603 SubWorldData assets):

- Every mission is its own sub-world of `Levels/SP/SP_MainCity`, named after the mission, with
  extra sub-worlds for its parts: `TheShard`, `TheShardApproach`, `TheShardBriefing`,
  `TheShardBriefingApproach`, `…Debriefing…`, and a few special ones (`KingdomExecutionScene`,
  `PrisonerXInterrogation`, `SavantExtraordinaireGetRope2`).
- Side content follows naming patterns: `<District>GridNode[Approach]`, `SecurityHub<District><n>`,
  `<District>CityAlert`, and open-world jobs `OWPh<phase><district>OppMiss<n>` /
  `OWPh<phase><district>Delivery<n>` (phase 2–7; Dt = Downtown, Anc/A = Anchor,
  Ct = Construction/Rezoning, Vw = The View).
- The city itself: `CentralCityProps_ZS`, `CentralCityBuildings_ZS`, `Ground_ZS` (roots of 341
  zone-streamed pieces named `…_ZS_…`), districts (`Anchor`, `DowntownArea3`, …), characters
  (`Dt_OM_Characters_Area01`, `Birdman`, `Icarus…`), environment (`VISTA_CityBuildings`,
  `VisualEnvironments`, backdrops, `LMS…` whose purpose is not confirmed), `MainLogic`.

Story and side mission names were matched against the released game's mission lists. "Other"
holds what matches no pattern (`Aline`, `Jukebox`, `Bauble…`).

## In the editor

- Opening a level with sub-worlds shows **Sub-worlds to Load**: all of the level's sub-worlds by
  category, story and side missions grouped per mission with their parts, open-world jobs per
  phase and district. Tick boxes on categories and groups tick everything below them. Search
  matches sub-world, mission and category names.
- Presets: **Everything** (as before), **City only** (no story/side missions, gridnodes, security
  hubs, city alerts, jobs), **None** (only objects placed directly in the level). The first time
  a level is opened the picker starts at City only; after that it starts at the saved choice.
- **Load** saves the choice for that level and loads it. **Load everything** loads all sub-worlds
  this once without changing the saved choice.
- "Show loaded mission and side content in the view": sub-worlds the game does not load at start
  (AutoLoad off) used to be loaded but hidden in the Layers panel. With this ticked, the mission
  and side content you load starts visible.
- "Ask each time a level is opened" off: the saved choice is used without asking. Change it with
  **Map > Sub-worlds to Load** (pick the level, Save; applies the next time the level is opened).
- The log says which sub-worlds were left out ("SubWorlds" lines).

Leaving a sub-world out only affects the editor. The reference stays in the level and the game
still loads it: edits change the ebx directly, nothing rebuilds a level's object list from what
is loaded. A sub-world inside one that is left out is not loaded either.

## Asset list export

**Map > Export Asset List** writes two tab-separated files (open in Excel or a text editor):

- `<name>.tsv`: every ebx and res asset with kind, type, full name, folder and bundles.
- `<name>_subworlds.tsv`: every level's sub-worlds with the category and group the picker uses.
