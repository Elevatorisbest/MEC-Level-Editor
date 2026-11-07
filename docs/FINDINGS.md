# Verified findings (Mirror's Edge Catalyst, Frosty profile 20160607)

Facts established by testing in the editor and in the game (Steam copy + EA app), as of
2026-11-07. "Verified" means observed in game or in the shipped data, not assumed.

## Launching the game with the project's mods

- Steam copies of MEC are started Steam -> EA app -> game, which drops Frosty's `-dataPath`
  argument. **Verified:** MEC reads the `GAME_DATA_DIR` environment variable instead.
- Platform `SteamWithEAApp` (Options > Launch Options, Platform Launching Enabled): shuts down
  Steam and the EA app, restarts Steam with `GAME_DATA_DIR=<game>\ModData\<pack>`, starts the
  game with `steam.exe -applaunch 1233570`, and restarts Steam without the variable when the
  game exits. **Verified** working, including the restore.
- Nothing in the game folder is changed beyond the `ModData` folder Frosty always writes.

## Static model groups and Havok collision (`static_instances`)

- 316k of SP_MainCity's objects are static-model-group members; 216k of them take their
  transform from a Havok `hknpStaticCompoundShape` resource, the rest from `InstanceTransforms`.
- **Verified:** moving, rotating and scaling such an object and writing the Havok resource
  moves its collision in game.
- Instance flags are a 24-bit integer packed into the w of the first transform row
  (`0x3F000000 | flags`). Shipped scaled instances: 64,376 of 64,794 have `0x4E` =
  HAS_TRANSLATION | HAS_ROTATION | HAS_SCALE (0x08) | IS_ENABLED. **Verified:** without
  HAS_SCALE the game ignores a written scale; with it, collision scales.
- The compound shape and its BVH tree each store an AABB (min, max) just before the tree
  data (read as `Unknown1`/`Unknown2`). **Verified:** collision outside the group's
  original AABB is ignored (a scaled bridge only collided up to the old boundary); growing
  both boxes to cover modified pieces fixes it. The editor uses the render mesh bounds in
  group space + 0.5 m.
- The tree nodes are reset on write (existing Frosty approach); this has worked in all tests.

## Editor behaviour fixed along the way

- Selecting an object used to modify it (side-panel transform fields wrote back while being
  filled in) and left stale undo units. Fixed; selecting is read-only now.
- The gizmo wrote world positions into a parent-relative transform (objects inside offset
  prefabs jumped). Fixed with a parent-space conversion; verified in game.

## Rendering (fallback shader)

- MEC normal maps are mostly BC5; `NormalDetailTiling/Strength` belong to the detail normal
  map, not the main one (Frosty's built-in MEC shader got this wrong).
- `TA_` textures are texture arrays. For building shaders (`SS_BuildingArray*`) the layer is
  **not** chosen by per-vertex data: `SubMaterialIndex` is all zero on the tested building and
  exceeds the layer count on another. The real rule is in the game's shader; open.
- Glass has no colour texture; drawn as a dark tint of `AbsorbtionColor` (approximation).

## Still unknown

- How building shaders pick the texture-array layer (see above).
- What the remaining Havok instance header words mean (tmp2..tmp5).
- Whether static models outside static groups (separate physics) follow scale.
