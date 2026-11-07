# Phase 1 roadmap proposal

Status: **proposal, waiting for your approval and ordering.** No feature code has been written.
Based on `docs/ANALYSIS.md`. References like "§4" point to sections there.

How this is organized:
- **Part A** ranks the features: value, risk, size, what you'd test, and dependencies.
- **Part B** splits them into milestones. Each milestone should be buildable by CI and testable by
  you in one evening (about 1–2 hours of your time).

Size scale (my implementation effort, excluding test round-trips): **S** = one milestone,
**M** = 2–3 milestones, **L** = 4 or more.

---

## Part A: ranked features

Ranking: things that make every later test cycle cheaper come first, then correctness of what
already exists, then new editing power. Pure performance work is last unless your first tests show
the editor is unusable on MEC levels.

| # | Feature | Value | Risk | Size | You'd test | Depends on |
|---|---|---|---|---|---|---|
| 1 | **Diagnostics**: log file per session, load report (entity counts by type, ebx types with no editor binding, missing assets, MEC asset-name checks for network registry / mesh variation DB, load timings, mesh/texture counts), a Diagnostics panel in the editor | Very high: turns every test into data | Low | S–M | Open 1–2 levels, send me the log file | CI green |
| 2 | **Fix known bugs** (§7): MEC-incompatible duplicate path (`ForceSeparateEnlightenSystem`), mesh double-dispose, render-state restore, `ClearFlags`, `FlagsLinkRealm`, `StaticModelEntity.Destroy`, null mesh check, toolbox button. Stop the BF1 hotkey task at Frosty startup | High | Low | S | Same levels; duplicate and delete one object; editor stays stable | 1 (to confirm fixes) |
| 3 | **Read-only validation**: dangling PointerRefs, connections to objects that no longer exist, duplicate instance GUIDs, references to assets not in the level's bundles, network-registry consistency. Report in a panel with click-to-select | High; also our safety net for every later editing feature | Low (reads only) | M | Run it on an untouched level (should be mostly clean), then on one where you deleted something with today's tool (should flag it) | 1 |
| 4 | **Viewport materials, step 1**: dump each mesh's material parameters and texture names to the log, then one lean mesh path (pos/normal/tangent/UV), a simple shader (diffuse + normal map + one directional light), and remove the duplicate mesh copy (§4) | High; your primary ask | Medium: MEC material parameter names are unknown until the dump | M | Screenshots of the same 2–3 viewpoints before/after; memory use from the diagnostics | 1 |
| 5 | **Viewport materials, step 2**: object/mesh variations so recoloured or retextured instances show correctly; selection outline in textured mode | High | Medium: variation lookup for MEC unverified (§2.3) | M | Screenshots of known variation-heavy spots you pick | 4 |
| 6 | **Scene tree**: hierarchy with layers/groups, search, type filters, show/hide/lock, multi-select in the tree, sync with viewport selection | High for accessibility | Low | M | Find objects by name/type; hide a layer; select several | 1 |
| 7 | **Multi-select and gizmos**: Ctrl/Shift click in viewport, translate/rotate/scale, world/local space, grid and angle snapping, undo/redo for all of it | High | Medium: refactor of `LevelEditorScreen` (§8.4) | L | Move/rotate/scale groups with snapping; undo/redo; save project, reopen, check | 6 |
| 8 | **Edit operations layer**: one game-aware service for spawn/duplicate/delete that wires references correctly (copy or clean up Property/Event/Link connections, network registry via a cached index instead of scanning all ebx, mesh-variation DB, bundle membership), each as an undoable unit. Replaces the BF1 tool window's copy/delete | Very high: the core of editing | **High**: Frostbite/MEC semantics for networking and bundles are partly unknown | L | Duplicate/delete in a few patterns, run validation (#3), export mod, check in game that objects appear/disappear | 3, 7 |
| 9 | **Asset browser**: list of spawnable blueprints (ObjectBlueprint, SpatialPrefab, …) with search, type filter, favorites, recents, thumbnails; drag into viewport to spawn (implements the empty `ProcessDrop`) | High | Medium: spawning inherits #8's risk | M | Find and spawn 3 objects; restart Frosty, favorites/recents persist | 8 |
| 10 | **Copy/paste of selections** within a level and between levels/sub-worlds | Medium–high | Medium: cross-blueprint references need remapping | M | Copy a group from one layer to another; validation stays clean | 8, 7 |
| 11 | **Reusable groups (prefab-style files)**: save a selection to a file (blueprint refs by path+GUID, relative transforms, overridden properties), load and place it anywhere, in any level | Medium–high | Medium: needs a stable file format; versioned from day one | M | Save a group, spawn it in another level, export, check in game | 10 |
| 12 | **Pre-export validation gate**: run #3 automatically before mod export and warn | Medium | Unknown: I haven't checked whether Frosty has a pre-export hook; fallback is a menu item "Validate then export" | S | Break something on purpose, try to export | 3 |
| 13 | **Open-world performance**: instancing of repeated meshes, spatial grid culling, deferred/limited-mip texture loading, per-sub-world load/unload toggles | Medium now; **high if load times or memory are bad** | Medium | L | Load times, FPS and memory from diagnostics on your largest level | 1, 4 |

Explicitly out of scope (from your brief): live in-game editing, matching game lighting or
physics, copying code from other editors.

---

## Part B: milestones (one evening each)

Each milestone ends with a green CI build and a short test script from me: what to download, what
to click, and what to send back.

| Milestone | Contents | Your test (summary) |
|---|---|---|
| **M0 Build is green** | Fix whatever the first CI runs report until the editor and LevelEditorPlugin build. No behaviour changes | Download the artifact, start Frosty, open one MEC level. Does it open, and does it look like your current build? |
| **M1 Diagnostics** | #1: session log file (in a `LevelEditorLogs` folder next to `FrostyEditor.exe`), load report, Diagnostics panel. English log messages | Open your 2–3 most important levels, send the log files and a screenshot of the panel |
| **M2 Stability fixes** | #2, informed by M1's logs | Repeat M1; duplicate and delete one object each; report any crash |
| **M3 Validation (read-only)** | #3 | Run validation on a clean level and on a deliberately broken one |
| **M4 Material dump + lean mesh path** | First half of #4: log material params/textures; new mesh path behind a toggle (old path still available) | Toggle between old/new path at fixed viewpoints, screenshots, memory numbers |
| **M5 Simple lit shader** | Second half of #4: diffuse + normal + sun; old duplicate mesh copy removed | Same viewpoints, screenshots |
| **M6 Variations + selection outline** | #5 | Screenshots at variation-heavy spots |
| **M7 Scene tree** | #6 | Search/filter/hide/multi-select checks |
| **M8 Multi-select + translate gizmo rework** | First part of #7 (selection set, translate for many objects, undo) | Move groups, undo/redo, save and reopen |
| **M9 Rotate/scale + snapping** | Second part of #7 | Same, with rotation/scale and snapping |
| **M10 Delete with reference cleanup** | First part of #8 (delete is the simplest operation and validation can check it) | Delete objects, validate, export, check in game |
| **M11 Duplicate with wiring** | Second part of #8 | Duplicate objects, validate, export, check in game |
| **M12 Spawn + asset browser** | #9 | Spawn from browser, favorites/recents persist |
| **M13 Copy/paste** | #10 | Cross-layer copy, validate |
| **M14 Group files** | #11 | Save/load a group across levels |
| **M15 Export gate** | #12 | Deliberate break, export blocked/warned |
| **M16+ Performance** | #13, split once we have numbers from M1 | Before/after numbers |

You can reorder anything whose dependencies allow it. Two choices you might want to make now:
- **Performance earlier?** If M1 shows MEC levels take many minutes or run out of memory, I'd move
  the cheapest performance item (deferred texture loading) right after M2.
- **BF1 tool window:** I propose hiding its menu entry in M2 and removing it once M10/M11 replace
  its copy/delete. Its terrain and Havok tools stay until we know whether you need them on MEC.

## What I need from you before M1

1. Approval of the analysis, and your ordering or changes to the list above.
2. Paths: your MEC install and your Frosty profile/cache folders (for test instructions only;
   I won't touch them).
3. Catalyst version (EA app / Origin / Steam, patch).
4. Which 2–3 levels or areas matter most. These become our fixed test cases.
5. What currently works or breaks for you in this plugin, if you've used it.
6. Whether to keep the other games' type SDK DLLs in the repo (`FrostySdk/Profiles`).

## Status (2026-11-07)

Done and verified in game: viewport performance (instancing, parallel culling), rendering
fixes, one-click launch for Steam + EA app copies, move/rotate/scale gizmos with snapping,
multi-select, typed transforms, undo/redo, automatic Havok write on save, Havok scale and
bounds. See FINDINGS.md.

Next, in order: validation of references before export; duplicate/delete with reference
rewiring; scene tree with search and filters; asset browser; prefab-style groups.
