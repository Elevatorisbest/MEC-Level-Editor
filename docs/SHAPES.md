# Shape Builder

**Map > Shape Builder** makes brand-new objects for building new areas: box, plane, ramp,
stairs, cylinder and corner (L-shaped wall piece), with a real game material and MEC collision.
Code: `Tools/LevelEditorPlugin/Shapes/`.

## Using it

1. Open a level and click into its view once.
2. **Surface:** select an object in the level whose look you want (a wall, a floor, a crate),
   press **From selected object** and pick one of its materials. Used surfaces stay in the list.
   Tiling materials (walls, floors, concrete, metal) look best: shapes get world-scale UVs, so the
   texture repeats every *Texture repeats every* metres on every face, whatever the size.
3. **Shape and size** in metres. The origin is the bottom centre. Ramps and stairs rise towards
   their back (+Z); a cylinder's width is its diameter; a plane faces up. *Round sizes to* snaps
   every size (default 0.25 m).
4. **Collision and movement:** collision boxes are on by default (see below). The *Can't stand /
   climb / vault / wallrun* flags are MEC's own and are set on the model and its boxes.
5. **Place:** into the level root (always loaded) or into the selected object's layer.
   **Create in front of the camera** places it at *Distance* (auto: so the whole shape is in
   view), turned to face away from the camera in 90° steps.
6. Move and rotate it with the gizmos like any other object. Ctrl+Z removes it.

Do not resize shapes with the scale gizmo: collision boxes may not follow a scale in game.
Make a new one with the size you want instead.

**Delete all shapes…** takes every shape out of the project (placed shapes, their network
registry and mesh variation entries, and the shape assets). Use it when a shape breaks the
game: deleting only the placed object leaves the shape's assets in the level's bundle, where the
game still loads them. Save the project and reopen the level afterwards.

Avoid special materials (water, glass, decals, emissive, sky…): their shaders may need more than
plain geometry. The window warns when a surface's name suggests one.

## What gets created

Per shape, in `<level folder>/LevelEditorShapes/` (for SP_MainCity:
`Levels/SP/SP_MainCity/LevelEditorShapes/Shape001_box_4x1x4` and `..._mesh`):

- **Mesh:** a copy of the surface's mesh (ebx, MeshSet resource, LOD chunks) with the generated
  geometry in the chosen material's section (other sections are emptied), new bounds and no
  occluder mesh. The geometry goes through a temporary FBX file and Frosty's normal FBX importer,
  which handles MEC's vertex formats. All LODs get the same geometry.
- **Mesh variation entry:** the surface mesh's entry, pointed at the new mesh, added to the
  target bundle's mesh variation database (MEC takes a mesh's textures from there).
- **Prefab** (SpatialPrefabBlueprint) with one static model (PamStaticModelEntityData, no Havok
  physics) and one PamOBBCollisionEntityData per collision box. Their field values (collision
  layer, surface material, …) are copied from existing objects of the open level; each collision
  box also gets a copy of that object's physics body (RigidBodyData), preferably one without a
  baked Havok shape. Flags get only the new id bits (no connection realm bits).
- **Placed reference** to the prefab in the target layer (undo, network registries as for placed
  props).
- **Bundles:** everything is added to the target layer's bundle, plus what it uses (shaders,
  textures) with "assets + resources + data chunks" (Test 3, verified in game for placed props),
  whatever the Bundles option is set to.

Undo removes the placed object; the new assets stay in the project (unused, harmless).

## Collision

MEC's `PamOBBCollisionEntity` boxes (oriented boxes; walkable, verified in game by moving an
existing one). Per shape:

| Shape | Boxes |
|---|---|
| Box | one box |
| Plane | a 0.3 m slab under the surface |
| Ramp | a 0.3 m slab tilted under the slope, and a slab at the high back |
| Stairs | one box per step, from the floor to the step's top |
| Cylinder | one box per pair of opposite sides, together covering the cylinder |
| Corner | one box per arm |

## History

- 993da38 (first version): shapes worked in the editor, but MEC crashed without an error on the
  loading screen. Suspects fixed in the next version: collision boxes without their physics body
  (the copy dropped the RigidBodyData the box's `PhysicsBodies` points to), and Flags high bits
  copied from a level object (realm bits of connections the new objects do not have). The test
  shape also used a water plane material.
- 27be4b6: no crash, collision works in game, but the model is invisible (also with other
  materials). New objects had id-only Flags. Next version: each new object keeps the Flags high
  byte of the level object it was copied from (the static model template had 0x1F), and the log
  lists which high bytes the level's own static models and collision boxes use, plus more of the
  template model's fields. If the model is still invisible, the next suspects are the mesh data
  written for MEC, the LOD/cull settings copied from the template mesh, and the variation entry.
- ab32c27: still invisible (collision works). The Flags high byte is not the cause.
- Next: a test choice in the Shape Builder ("Test (model invisible in game)") to split the
  problem. Make one shape of each kind side by side in one session and check which show in game:
  - *Normal*: new mesh with the shape's geometry;
  - *new mesh, unchanged copy of the surface's mesh* (`_testcopy`): duplicated mesh asset,
    resource and chunks plus the variation entry, no geometry import;
  - *no new mesh, use the surface's own mesh* (`_testoriginal`): same prefab and model setup,
    pointing at the existing mesh.

  `_testoriginal` invisible → the model/prefab setup is the problem. `_testoriginal` visible but
  `_testcopy` invisible → duplicating a mesh into the level is. Both visible, Normal invisible →
  the geometry import is. The log also describes the mesh layout before and after the import.
- ab6a7ac test result: Normal crashed on load (no error); `_testcopy` and `_testoriginal` both
  had working collision and an invisible model. So even the game's own unchanged mesh is
  invisible: the model object is wrong, not (only) the mesh. Found: the template static model has
  one component, and its `ClientRuntimeComponentCount`/`ServerRuntimeComponentCount` and
  `PhysicsPartInfos` count it, but the copy dropped internal objects, so the shape's model had no
  component while its counts said one. Next version: the model keeps copies of its internal
  objects (as the collision boxes do), and the template search prefers self-contained static
  models (no components, part infos, part links or runtime component counts). The log lists the
  template's component types and runtime counts. Whether the Normal crash has the same cause is
  not known yet.
- bd79e52 result: no crash any more; `_testoriginal` and Normal both invisible, collision works.
  The level has 0 self-contained static models (every standalone one has a PartComponentData);
  only ~160 standalone PamStaticModelEntityData exist among ~980k entities, while placing a game
  ObjectBlueprint is verified to show in game. Next: **Visibility test** button (6 objects in a
  row): 1 game blueprint placed directly; 2 a new prefab referencing it; 3 an unchanged copy of
  the blueprint; 4 the copy without PhysicsData; 5 the copy with the generated mesh; 6 a new
  prefab referencing 5 plus collision boxes. Which numbers show in game tells which step hides
  the model.

## Checked so far

- Geometry: every shape is closed and wound outwards (volume matches the shape, every triangle's
  winding agrees with its normal), tangents are perpendicular to normals, and the collision
  boxes cover the shape (4,000 random points per shape), in a test outside the editor.
- Winding: front faces are counter-clockwise with outward normals (`cross(v1-v0, v2-v0)` points
  out), derived from Frosty's renderer (back-face culling with X mirrored), which shows game
  meshes correctly.
- In game (27be4b6): collision boxes work; the model was invisible.

## Not done

- Editing a shape after it is made (change its size later): make a new one for now.
- More shapes (arches, curved ramps), face push/pull, per-face materials.
- Model import (planned separately).
