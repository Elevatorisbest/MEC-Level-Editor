# Phase 0 Analysis: Frosty Level Editor plugin for Mirror's Edge Catalyst

Date: 2026-11-06. Status: Phase 0 report, committed to `dev`. §1 was updated after the repo
connection was fixed. The original findings are kept in §1.1 to §1.3 for the record.

How to read this report: statements are marked **[verified]** when I read the code or checked it
against data, **[inferred]** when they follow from the code but I could not run it, and
**[unknown]** when they need you, the game data, or an experiment to settle. I could not run Frosty,
build anything, or open game data in this phase.

What I read: the full plugin core (editors, entity base classes, ReferenceObject and the world/
sub-world loaders, asset and mesh managers, render proxies, mesh renderable, the screen/gizmo/
picking code, the BF1Core duplicate/delete tool, properties panel save path), plus the
supporting parts of FrostyPlugin (TextureLibrary, MeshMaterial/shader plumbing, UndoManager) and
MeshSetPlugin. The roughly 1,160 files of generated entity stubs under `GameSpecific/MirrorsEdgeCatalyst`
and the ~1,400 excluded BF1 files I sampled, not read one by one.

---

## 1. Git and environment state

### 1.0 Current state (after setup, 2026-11-06 01:46) [verified]
- Working repo on your PC: `D:\Claude MEC Level Editor\MEC-Level-Editor`. It's a git clone of
  `Elevatorisbest/MEC-Level-Editor` (private), with `HEAD` on `dev` and `dev` and `master` both
  tracking `origin`. There's no `upstream` remote yet.
- `origin/dev`: upstream history (`a5214ac`, `5e575eb`) plus your README punctuation commit
  `54fddb9`. All Phase 0+ work goes here.
- `origin/master` (the default branch): a single commit `05bd174` with **no shared history with
  `dev`**. Content differs from `dev` only in one README line. This matters if `dev` is ever merged
  into `master`. Untouched, per the rules.
- The Claude GitHub integration can now see the repo with push rights. I work from a cloud clone
  and push to `origin/dev`. You `git pull` in the D: clone to get the changes.
- The loose copy in `D:\Claude MEC Level Editor` (no `.git`) is now a duplicate of the repo
  contents. I don't use it.

### 1.1 Local folder (original finding) [verified]
- `D:\Claude MEC Level Editor` is **not a git repository**: there is no `.git` folder at the top level.
- All files have the same timestamp (2026-02-26 15:24 UTC). I compared 2,796 files (all of
  `Tools/`) byte for byte against the public upstream `memory1ac/FrostyLevelEditor_ME` at commit
  `5e575eb` ("optimize performance", 2026-02-26). **Every one is identical**. So the folder is
  almost certainly an unpacked GitHub ZIP of upstream HEAD with no local edits. I did not compare
  the other top-level folders file by file. Their structure matches upstream.
- Upstream has exactly two commits (`a5214ac` initialize, `5e575eb` optimize performance), both by
  memory1ac, on branch `master`.

### 1.2 Your private repo (original finding, since fixed) [verified]
- The GitHub integration on your Claude account **cannot see `Elevatorisbest/MEC-Level-Editor`**.
  `add_repo` returned "not found, or this session's GitHub credential doesn't have access".
  Listing your repos shows 12 repositories, all public, and that one is not among them.
- Probable cause [inferred]: the Claude GitHub App is installed on your account for selected
  repositories only, and the new private repo was not added. It could also be a typo in the
  name. Per your rule 6, I did not try any other credentials or methods.
- There is a public fork, `Elevatorisbest/FrostyToolsuite-MEC-World-Editor-test` (last pushed
  2026-09-07). I have not looked inside it. Please tell me whether it is related.

### 1.3 Shell on your PC [verified]
- The Linux shell that would run git and builds on your PC **failed to start**. The Claude desktop
  app reported: "this device has too little free disk space to set up the environment, which needs
  up to 17 GB". I could still list folders and copy files, which is how I did this phase. I did
  everything else in my cloud workspace on a fresh clone of upstream.

### 1.4 Build tools on this PC (from folder names only) [verified, but shallow]
| Item | Found |
|---|---|
| `C:\Program Files\Microsoft Visual Studio\2022` | folder exists, **no edition subfolders visible** (no Community/Professional/BuildTools) |
| `C:\Program Files (x86)\Microsoft Visual Studio\2019`, `\2022` | folders exist, **empty** |
| `...\Microsoft Visual Studio\Installer`, `\Shared` | present |
| .NET SDKs (`C:\Program Files\dotnet\sdk`) | 5.0.408, 8.0.100-preview.7, 8.0.408, 8.0.411, 8.0.425, 8.0.427 |
| .NET Framework reference assemblies | v4.8, v4.8.1 |

[inferred] Visual Studio itself looks **uninstalled or never fully installed** (empty year
folders are what an uninstall leaves behind). I could only see names, not files, so please confirm.

### 1.5 What the build actually needs [verified from project files]
- All C# projects target **.NET Framework 4.8** (`net48`) with WPF. LevelEditorPlugin uses C# 12
  (`LangVersion 12.0`), so it needs a recent compiler: the VS 2022 / .NET 8 SDK era, not VS 2019's
  stock compiler.
- The solution file is **VS 2022 format** (VisualStudioVersion 17.14). The README's "Visual Studio
  2019" is outdated for this fork.
- Configuration names are `Developer - Debug|x64`, `Release - Alpha/Beta/Final|x64`. The README's
  `DeveloperDebug` doesn't exist literally.
- **LevelEditorPlugin has a project reference to `FrostyHash.vcxproj`, which is C++/CLI with
  PlatformToolset v141 (VS 2017 toolset).** Building the plugin therefore needs VS with the
  "Desktop development with C++" workload, the **MSVC v141 toolset**, and **C++/CLI support for
  v141**. This is the most likely build blocker. `Shaders/FrostyShaders.vcxproj` uses v141/v143 as
  well. A plain `dotnet build` cannot build vcxproj files.
- The post-build step copies the plugin DLL into `FrostyEditor\bin\...\Plugins\`.
- The plugin references `FrostySdk/Profiles/MirrorsEdgeSDK.dll` (the EBX type SDK for MEC,
  6,138 types). It is committed in upstream.
- Oddity: every configuration defines `BF1` (not a MEC symbol), and code has `#if BF1`,
  `#if SWBF2`, `#if MASS_EFFECT` branches. So MEC builds currently take the BF1 branches.

**Build result: not attempted locally.** No VS/MSBuild toolchain is confirmed on the PC, the PC
shell is down, and my cloud workspace is Linux, which cannot build net48 WPF plus C++/CLI.
Instead, a GitHub Actions workflow (`.github/workflows/build.yml`, Windows Server 2022 + VS 2022)
builds the editor and plugins. Its results answer "does it build". See `docs/BUILD.md`.

---

## 2. Architecture

### 2.1 Registration with Frosty [verified, `Properties/AssemblyInfo.cs`]
- Asset definitions (which assets open in this editor): `LevelData`, `DetachedSubWorldData`,
  `ObjectBlueprint`, `LogicPrefabBlueprint`, `SpatialPrefabBlueprint`.
- Menu extensions: `EntityGeneratorExtension` (generates entity stub classes from the SDK),
  `DumpLayoutsToLogExtension`, `BlueprintAnalysisExtension`, `BF1CoreMenuExtension`.
- User shaders: `TerrainShader`, `GizmoShader`, `SpriteShader`, `LevelShader`
  (in `FrostyEditor/Shaders/*.hlsl/.xml/.bin`).
- Plugin author string: "GalaxyMan2015 and Cade/CrazyZhang And MemoryAc", version 2.0.0.0.

### 2.2 Main classes
| Layer | Classes | Role |
|---|---|---|
| Editors | `ToolbarAssetEditor` → `SpatialEditor` → `LevelEditor` (also `SpatialPrefabEditor`, `ObjectBlueprintEditor`, `LogicPrefabEditor`) | WPF asset editor hosted by Frosty. `LevelEditor` builds the dockable panels and runs loading. |
| Screen | `LevelEditorScreen : DeferredRenderScreen2` | D3D11 viewport: render loop, culling, picking, selection, translate gizmo. |
| Entities | `Entity` (abstract) → `ReferenceObject` → `SubWorldReferenceObject` → `WorldReferenceObject`; `LayerReferenceObject`, `ObjectReferenceObject`, `SpatialPrefabReferenceObject`; `StaticModelEntity`, `StaticModelGroupEntity`/`...ElementEntity`, `TerrainEntity`, lights, etc. | Editor-side mirror of the ebx object graph. Each wraps a `GameObjectData` instance. |
| Assets | `Asset`, `GenericAsset<T>`, `Blueprint`, `SubWorld`, `MeshAsset`, `TextureAsset`, `MeshVariationDatabase`, `NetworkRegistryAsset`, … | Wrappers around loaded ebx root objects. |
| Managers | `LoadedAssetManager` (singleton, ref-counted ebx cache), `LoadedMeshManager` (OBJ gizmo meshes), `SchematicsLayoutManager` | Shared caches |
| Render | `RenderProxy` → `ModelRenderProxy`, `SpriteRenderProxy`, `TerrainRenderProxy`; `MeshRenderable` | GPU-side objects per entity. |
| UI panels | `LayersViewModel`, `InstancesViewModel` (has a text filter), `PropertiesViewModel`, `LevelEditorToolboxViewModel`, `TimelineViewModel`, `TerrainLayersViewModel`, `SchematicsViewModel` (disabled in the level editor) | Docked panels via a custom `DockManager`. |
| BF1Core | `MainWindow`, `MeshView`, `MeshUtil`, `CoreUtil`, terrain helpers, `MapScript`, `EasyView` | A separate floating tool window (menu "Map → Battlefield 1 Map Editor") with transform fields, duplicate/delete, terrain vertex edits, Havok write-back, and BF1-specific scripts. |

### 2.3 Loading [verified]
1. `LevelEditor.Initialize()` runs inside a `FrostyTaskWindow` and creates a `WorldReferenceObject`
   from the opened `LevelData`.
2. `ReferenceObject` constructor → `Initialize()` loads the blueprint through `LoadedAssetManager`
   (by `PointerRef`) → `SpawnEntities()`: for a `PrefabBlueprint` it iterates `Objects`, creating an
   entity for each `GameObjectData`. For `ObjectBlueprint` it creates the single root object.
   Child `ReferenceObject`s recurse, so the **entire level, including every sub-world, is loaded
   eagerly in one pass**.
3. `Entity.CreateEntity` maps ebx data type → entity class through `[EntityBinding]`, by **exact type
   match only**. A data type with no binding (including a subclass of a bound type) gets **no
   entity** and is silently skipped (only `Debug.WriteLine`).
4. `SubWorldReferenceObject` also loads `<BundleName>_networkregistry_win32` and
   `<BundleName>/MeshVariationDb_Win32` by name. [unknown] These naming patterns come from
   BF1/SWBF-era games, and I don't know whether MEC uses the same names.
5. Layers (`SceneLayer`) are built from `ILayerEntity` entities, and render proxies are queued to the
   render thread via `screen.AddEntity`.
6. Static model groups: each `StaticModelGroupMemberData` instance becomes a synthetic
   `StaticModelGroupElementEntity`. Its transform comes from `InstanceTransforms` or, if those are
   empty, from the `HavokPhysicsData` resource (custom reverse-engineered parser,
   `Resources/Physics/HavokPhysicsData.cs`, ~1,300 lines).

### 2.4 Editing and saving [verified]
- Edits mutate the in-memory ebx objects held by `LoadedAssetManager`, then
  `LoadedAssetManager.UpdateAsset(blueprint)` calls `ebxAsset.Update()` +
  `App.AssetManager.ModifyEbx(name, asset)`. This is how Frosty marks an asset as modified in the
  project.
- Resources (Havok physics, terrain heights) are written with `ModifyRes`/`ModifyChunk` from the
  BF1Core helpers, only when you press their buttons.
- The plugin **does not write mods itself**. You save the Frosty project (`.fbproject`), and Frosty
  Editor's normal "Export Mod" writes the `.fbmod` containing every modified ebx/res/chunk. I did not
  trace FrostyModWriter in detail. That is standard Frosty behaviour.
- [inferred] **No bundle handling**: if a duplicated or edited object starts referencing a blueprint
  that is not already in the level's bundles, nothing adds it. In Frostbite that usually means the
  asset is not loaded in-game. [unknown] Whether this matters for MEC needs a test.

---

## 3. What the plugin already does

| Feature | State | Notes |
|---|---|---|
| Open LevelData / sub-worlds / prefabs / object blueprints | Works (per upstream) | Eager full load |
| Layer tree | Yes | Visibility toggles per layer, recursive |
| Instances list | Yes | **Text filter** exists. No multi-select. |
| Properties panel | Yes | Frosty property grid. Edits are saved via `ModifyEbx`. Interface "property values" are handled (creates new `PropertyValue_N` fields so shared values aren't clobbered). |
| Viewport | Yes | Deferred renderer from FrostyPlugin, sky, optional ground grid |
| Textured meshes | Partial | "View mode" option: 0 = solid colour, 1 = Default Lit (textured via MeshSetPlugin), plus debug modes (base colour, normals, …) |
| Picking | Yes | Bounding box then per-triangle ray test on LOD0 |
| Selection | **Single object only** | Selection highlight is a colour tint that only applies in solid-colour mode (see §4) |
| Gizmo | **Translate only**, world axes | No rotate/scale gizmo, no snapping. Rotation/scale editable as numbers in the BF1Core MeshView window or the property grid. |
| Undo/redo | Partial | Frosty `UndoManager` covers **moves** (`MoveEntityUndoUnit`, static-model-group variant). Asset-level undo state (`PopState`) is commented out. **No undo for duplicate/delete.** |
| Duplicate | Yes (BF1Core window) | "Copy mesh" N times with a step and axis, or "copy to camera" |
| Delete | Yes (BF1Core window) | Not for static-model-group elements ("blue meshes") |
| Spawn from asset browser | **No** | `LevelEditor.ProcessDrop` (drag from data explorer) is an empty stub |
| Asset browser / favourites / recents | **No** | Frosty's own Data Explorer only |
| Reusable groups / prefabs to file | **No** | |
| Validation | **No** | |
| Terrain | View, plus vertex height edit / flatten (BF1Core) | Written back with `ModifyRes`/`ModifyChunk`. Untested for MEC as far as I know. |
| Export | XML + FBX of static models and terrain OBJ (`ExportLevelAsXML`) | The toolbar button is commented out |
| Timeline, schematics | Code present | Schematics panel disabled in the level editor |
| Thumbnails | Yes | Writes PNG under the Frosty profile settings path |

## 4. Mesh and texture loading and rendering

How it works today [verified]:
1. `StaticModelEntity` (and Breakable/Dynamic/Banger/Debris/Cloth/Vegetation variants) loads its
   `MeshAsset` via `LoadedAssetManager`. MeshAssets are cached by ebx reference, so one mesh used 500
   times is loaded once.
2. `MeshAsset.LoadResource` builds a `MeshRenderable`, which does **two things**:
   - its own vertex buffers containing **position + normal only** (no UVs, no tangents; that code is
     commented out) for every LOD, plus a CPU copy of LOD0 for picking;
   - if the mesh's material collection loads, a **second, complete copy** of the mesh through
     MeshSetPlugin's `MeshRenderMesh`, which carries the real material/texture binding.
   So every textured mesh is parsed twice and exists twice in GPU memory.
3. Textures are loaded by FrostyPlugin's `TextureLibrary`: cached by asset name with ref-counting, **full
   mip chain uploaded synchronously during level load**.
4. The textured path uses Frosty's per-profile **fallback shader** (`Shaders/MeshFallback.hlsl`).
   It has a `PROFILE_20160607` (= MEC) branch that samples textures named `NRS`, `RSM` and `Ex` plus
   base colour. [unknown] How accurate that mapping is for MEC's materials.
5. LOD: picks LOD by `MeshLodGroup` distances. Culling: `CullScreenArea`, a global "<0.2% of
   screen" cull, and a frustum test.

Missing or broken:
- **Object variations are ignored for rendering** [verified]: `MeshMaterialCollection` is always
  created with an empty variation, although `StaticModelGroupElementEntity` resolves
  `ObjectVariation` from the MeshVariationDatabase. Re-coloured/re-textured variants will show the
  base mesh's textures. That directly conflicts with "textures that actually appear in-game".
- **Selection highlight doesn't work in textured mode** [verified]: `SetSelected` changes the
  LevelShader tint, but the textured path doesn't use LevelShader. There is a separate selection
  outline pass in the screen. Whether it covers textured meshes, I couldn't confirm without running it.
- **Possible double-dispose** [inferred]: `ModelRenderProxy.Dispose()` disposes the shared
  `MeshRenderable`, which is owned by the (shared, cached) `MeshAsset`, which disposes it again.
- **Possible render-state leak** [inferred]: the latest upstream commit removed the
  rasterizer-state save/restore in `MeshSetPlugin/Render/MeshRenderLod.cs`. A two-sided or
  wireframe state can then leak into the next draw.
- `StaticModelEntity.CreateRenderProxy` will throw if `Data.Mesh` is null (no null check).
- Static-model-group transforms rely on the custom Havok parser for groups without
  `InstanceTransforms`. [unknown] Whether that parser is correct for MEC's Havok version. Wrong
  parsing would show as objects at the origin or scrambled.

Gap to your target (diffuse + normal map + one directional light): the pieces exist (texture
loading, deferred lighting, a sun). What's missing is a lean dedicated mesh path with
position/normal/tangent/UV, variation-aware material resolution, and a simple shader, replacing the
duplicated mesh path.

## 5. How saved changes become a mod, and how references are kept

- Transform edits: written straight into the ebx data (`BlueprintTransform`, `Transform`, or a
  static-model-group member's `InstanceTransforms[i]`), then `ModifyEbx`. References are untouched,
  so this is the safest operation.
- Duplicate (`BF1Core/Utils/MeshUtil.CopySelectedMesh`) [verified]:
  - Normal objects: deep-copied through `FrostyClipboard` into the same blueprint, appended to
    `Objects`, transform offset.
  - Static-model-group elements: a **new `ObjectReferenceObjectData`** is created and fields set via
    `dynamic`, including `ForceSeparateEnlightenSystem`. **I checked `MirrorsEdgeSDK.dll`: MEC's
    `ObjectReferenceObjectData` has no `ForceSeparateEnlightenSystem` property.** [inferred] On MEC
    this path should therefore throw at runtime (dynamic binder error) and leave a half-modified
    asset. This is a BF1-specific assumption.
  - Network registry: scans **every ebx asset in the game** (`EnumerateEbx` + `ContainsDependency`)
    on each operation to find `NetworkRegistryAsset`s that depend on the blueprint, and appends a
    reference for objects with exported GUIDs. Slow on every click. [unknown] Whether MEC needs
    network-registry entries for offline single-player objects at all.
  - Not handled: copying or creating Property/Event/Link connections for the new object; bundle
    membership; MeshVariationDatabase entries.
- Delete (`DeleteSelectedMesh`) [verified]: removes the object from `Objects` and from network
  registries. It **does not remove Property/Event/Link connections that point at the deleted
  object**, so dangling references remain in the blueprint. That is exactly the class of bug your
  validation milestone should catch. Render proxies are removed from the list but not disposed.
- New instance GUIDs (`BF1Core/Utils/CoreUtil.CreateClassObjectRef`) use FrostySdk's
  `GenerateDeterministicGuid` over the asset's existing objects, which is the standard Frosty approach.
  For `DataBusPeer` types it also overwrites `Flags` with bits derived from the GUID.
  [unknown] Whether MEC expects that Flags layout (it is the same convention used for BF1/SWBF2).

## 6. Performance risks for a large open world

[verified unless noted]
- **Eager loading of everything**: all sub-worlds, layers, meshes and textures at open time, on one
  task, regardless of `AutoLoad`/streaming. MEC's city is large, so expect long loads and high RAM/VRAM.
- **Duplicated mesh data** (see §4) roughly doubles mesh memory for textured meshes.
- **No instancing**: one draw call per section per proxy, each with a constant-buffer update.
  Static model groups with thousands of identical instances are drawn one by one.
- **Linear per-frame culling** over all proxies: no spatial structure (BVH/grid), no
  occlusion culling.
- **Picking** sorts all visible instances and tests triangles on LOD0 on the CPU. OK for now.
- Full mip chains loaded for every texture. There is no texture streaming or mip-limit.
- Network-registry scan over all ebx on each duplicate/delete (§5).
- Undo units hold full matrices only, which is cheap. Not a risk.

## 7. Code quality, tests, known bugs

- **No automated tests** in the repo (`Plugins/TestPlugin` is a Frosty plugin, not a test project).
- **No log file**: logging goes to Frosty's in-app log window (`App.Logger`), only `crashlog.txt` on
  crash. Many log messages and comments are in Chinese, which will make your test reports harder
  to read unless translated.
- TODO/FIXME count in the plugin (excluding BF1 folder): **0**. Debt is marked instead with
  `// @temp`, `// @hack` and dated "change marker" comments.
- Heavy use of `dynamic` and reflection (field-name strings, private-method invocation on the
  viewport for thumbnails). These fail at runtime, not compile time, when a type differs between
  games.
- `PerformTemplateMagic` injects the toolbar template by walking Frosty's main window visual tree
  by hard-coded child indexes. It's fragile against any FrostyEditor UI change.
- Specific bugs found by reading:
  - `ReferenceObject.FlagsLinkRealm` shifts by 25, the same as `FlagsEventRealm`. It should
    almost certainly be a different bit.
  - `Entity.ClearFlags` uses XOR (`^=`), which *sets* a flag that wasn't set.
  - `StaticModelEntity.Destroy` doesn't call `base.Destroy()` (entity stays registered in the world).
  - `LevelEditor` "Show/Hide toolbox" button opens an `InstancesViewModel`, not the toolbox.
  - The BF1Core menu extension starts a global hotkey polling task (F1/F5) **at Frosty startup**,
    even if you never open the level editor. `MapScript`/`EasyView` contain hard-coded BF1 map
    paths and are useless for MEC.
  - `ModelRenderProxy` double-dispose and the missing render-state restore (§4).
- Structure: the generated entity stubs are reasonable. The BF1Core window duplicates functionality
  that belongs in the main editor (transform editing, duplicate/delete) and couples the screen to
  it through static calls (`MainWindow.UpdateSelectedEntity`).

## 8. Constraints and blockers for your goals

1. **Build environment** (§1.5): need VS 2022 + C++ workload + v141 toolset with C++/CLI. Without
   it, nothing I write can be tested.
2. **Git connection** (§1.2): the integration can't see your private repo.
3. **MEC-specific unknowns** that only test cycles can answer: network registry and mesh-variation
   asset names, Havok static-group parsing, bundle requirements for newly referenced assets,
   fallback-shader texture mapping.
4. **Single selection and single gizmo** are baked into `LevelEditorScreen` (`selectedProxies[0]`
   everywhere). Multi-select and rotate/scale gizmos mean refactoring this class.
5. **Duplicate/delete are in the BF1 tool window** and partly BF1-specific. Correct reference
   wiring needs a new, game-aware operations layer, which validation can share.
6. **Eager whole-world load**: large-world comfort probably needs per-sub-world load/unload, or at
   least deferred texture loading. That's a bigger change and should come after the basics.
7. **"No code from other editors"**: everything above can be built on Frosty's SDK and this plugin.
   I'll keep to that. Note that MeshSetPlugin (used for textured rendering) is part of this same
   Frosty toolsuite, so using it is within the rule.

## 9. Licensing [verified facts, not legal advice]

- `LICENSE.txt` at the repo root is the **MIT License template with placeholders left unfilled**:
  "Copyright (c) [year] [fullname]".
- `README.md` (root and plugin copy) states: "The Content, Name, Code, and all assets are licensed
  under a Creative Commons Attribution-NonCommercial-NoDerivatives 4.0 International License."
- These conflict: MIT allows modification and redistribution, while CC BY-NC-ND forbids
  distributing derivatives ("NoDerivatives"). Upstream's git history (two commits by memory1ac)
  gives no clarifying statement. The original FrostyToolsuite this is derived from is a separate
  project with its own license terms, which I did not check in this phase.
- What this implies for you: private work in a private repo is not affected by the "share" terms.
  **Publicly distributing your modified version** may be prohibited if the CC BY-NC-ND notice is the
  one that applies. The safe step before any public release is to ask the upstream author(s) which
  license applies. Also note: the repo commits `FrostySdk/Profiles/*.dll` (type SDKs generated from
  each game) and various third-party DLLs (FBX SDK, ffmpeg, NVIDIA GFSDK, renderdoc). Their
  redistribution terms are separate questions.

## 10. Files kept out of git

The existing `.gitignore` is the standard Visual Studio one. It already covers `bin/`, `obj/`,
`.vs/`, `*.user`, `*.log`, `*.pdb`. It did **not** cover Frosty/game output. These additions were
applied in commit `92b6372`, and no tracked file matched them (I checked with `git ls-files -ci`):
```
# Frosty / game data — never commit
*.fbproject
*.fbmod
*.fbpbak
*.cas
*.cat
*.toc
*.sb
**/Caches/
**/Mods/
**/Levels/          # output of ExportLevelAsXML (FBX/OBJ of game meshes)
**/Thumbnails/
crashlog.txt
**/LevelEditorLogs/
*.pat
.env
```
Already tracked in upstream and worth flagging (I have **not** changed anything): the generated
game type SDKs under `FrostySdk/Profiles/`, including `MirrorsEdgeSDK.dll`, which the plugin needs to
build. They contain type metadata derived from the games, not game assets. Whether you're
comfortable keeping them in your repo is your call.
