using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.BF1Core.Utils;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Entities;
using LevelEditorPlugin.Screens;
using SharpDX;
using System;
using System.Collections.Generic;
using System.Linq;
using Entity = LevelEditorPlugin.Entities.Entity;

namespace LevelEditorPlugin.Shapes;

/// <summary>
/// Visibility test: places six objects in a row (left to right, numbered in the log) that differ
/// in one step each, to find out which step makes a shape's model invisible in game while its
/// collision works. Uses the surface's own ObjectBlueprint (the game's prop that uses the mesh):
///  1. that blueprint placed directly (control; placing game blueprints is verified to show);
///  2. a new SpatialPrefabBlueprint holding a reference to that blueprint (tests the prefab);
///  3. an unchanged copy of the blueprint placed directly (tests copying a blueprint asset);
///  4. the copy with its Havok physics removed (tests PhysicsData = null);
///  5. the copy with the shape's generated mesh and no physics (tests the generated mesh);
///  6. a prefab holding a reference to 5 plus the collision boxes (the full shape this way).
/// </summary>
public static partial class ShapeAssetBuilder
{
    public static string RunVisibilityTest(ShapeParameters p, ShapeSurface surface, ShapeOptions options, SpawnTarget target, LevelEditorScreen screen, Matrix placement)
    {
        string problem = ShapeGenerator.Validate(p);
        if (problem != null)
            throw new InvalidOperationException(problem);
        if (surface == null)
            throw new InvalidOperationException("Pick a surface first: select an object in the level and use one of its materials.");
        if (target == null || screen == null)
            throw new InvalidOperationException("Open a level first.");

        EbxAssetEntry targetEntry = App.AssetManager.GetEbxEntry(target.FileGuid)
            ?? throw new InvalidOperationException("The target layer's asset was not found.");
        int bundle = BundleAdder.TargetBundle(targetEntry);
        if (bundle < 0)
            throw new InvalidOperationException($"{targetEntry.Name} is in no bundle, so new assets cannot be loaded with it.");
        EbxAssetEntry templateMesh = App.AssetManager.GetEbxEntry(surface.MeshGuid)
            ?? throw new InvalidOperationException($"The surface mesh {surface.MeshName} was not found.");

        EbxAssetEntry gameBlueprint = App.AssetManager.EnumerateEbx("ObjectBlueprint").FirstOrDefault(e => e.ContainsDependency(templateMesh.Guid) && StaticModelOf(e) != null)
            ?? throw new InvalidOperationException($"No ObjectBlueprint uses {templateMesh.Filename}; pick a surface from a prop placed through its blueprint.");

        using PerfProbe probe = PerfProbe.Start("Shapes", "visibility test");
        Templates templates = Templates.Find(target.Root);

        string levelName = (screen.EditorProvider?.RootLayer?.Entity as ReferenceObject)?.Blueprint?.Name ?? targetEntry.Name;
        string folder = SubWorlds.SubWorldCatalog.LevelFolder(levelName);
        if (folder.Length == 0)
            folder = "Levels/";
        folder += FolderName + "/";
        int run = 1;
        while (App.AssetManager.GetEbxEntry($"{folder}VisTest{run:00}_2_prefab_gameblueprint") != null)
            run++;
        string Name(int item, string what) => $"{folder}VisTest{run:00}_{item}_{what}";

        Vector3 right = Vector3.Normalize(new Vector3(placement.M11, placement.M12, placement.M13));
        float spacing = Math.Max(4f, Math.Max(p.Width, p.Depth) + 2f);
        Matrix At(int item) => placement * Matrix.Translation(right * spacing * (item - 3.5f));

        LevelEditorLog.Info("Shapes", $"Visibility test {run:00}: game blueprint {gameBlueprint.Name}, its static model: {Templates.Describe(StaticModelOf(gameBlueprint))}");

        List<string> report = new List<string>();
        int previousLevel = BundleAdder.Level;
        BundleAdder.Level = 3;
        try
        {
            // textures of the original mesh: its variation entry in the target bundle's database
            report.Add("variation entry (original mesh): " + AddVariationEntry(templateMesh, templateMesh, App.AssetManager.GetEbx(templateMesh), bundle));

            // 1. the game's blueprint, placed directly
            SpawnUtil.Spawn(gameBlueprint, target, screen, true, out string note1, At(1));
            report.Add($"1 (left): game blueprint placed directly; bundles: {note1 ?? "already loaded"}");

            // 2. a new prefab holding a reference to the game's blueprint
            EbxAssetEntry prefab2 = CreateReferencePrefab(Name(2, "prefab_gameblueprint"), bundle, gameBlueprint, new List<CollisionBox>(), options, templates);
            report.Add($"2: new prefab -> game blueprint; bundles: {PlaceNew(prefab2, bundle, target, screen, At(2))}");

            // 3. unchanged copy of the blueprint
            EbxAssetEntry bp3 = CopyBlueprint(gameBlueprint, Name(3, "copy"), bundle, null, null, true);
            report.Add($"3: copy of the blueprint, unchanged; bundles: {PlaceNew(bp3, bundle, target, screen, At(3))}");

            // 4. copy without physics
            EbxAssetEntry bp4 = CopyBlueprint(gameBlueprint, Name(4, "copy_nophysics"), bundle, null, null, false);
            report.Add($"4: copy, PhysicsData removed; bundles: {PlaceNew(bp4, bundle, target, screen, At(4))}");

            // 5. copy with the generated mesh, without physics
            ShapeMesh geometry = ShapeGenerator.BuildMesh(p);
            string meshName = Name(5, "mesh").ToLowerInvariant();
            EbxAssetEntry meshEntry = CreateMesh(templateMesh, surface, meshName, bundle, geometry, out EbxAsset meshAsset);
            report.Add("variation entry (generated mesh): " + AddVariationEntry(templateMesh, meshEntry, meshAsset, bundle));
            EbxAssetEntry bp5 = CopyBlueprint(gameBlueprint, Name(5, "copy_generatedmesh"), bundle, meshEntry, meshAsset, false);
            report.Add($"5: copy, generated mesh, no physics; bundles: {PlaceNew(bp5, bundle, target, screen, At(5))}; also from the mesh: {BundleAdder.AddWithDependencies(meshEntry, bundle, SpawnUtil.Check(bp5, target).LoadedBundles ?? new HashSet<int>())}");

            // 6. prefab -> 5, plus collision boxes
            List<CollisionBox> boxes = options.Collision ? ShapeGenerator.BuildCollision(p) : new List<CollisionBox>();
            EbxAssetEntry prefab6 = CreateReferencePrefab(Name(6, "prefab_shape"), bundle, bp5, boxes, options, templates);
            report.Add($"6 (right): new prefab -> blueprint 5 + {boxes.Count} collision box(es); bundles: {PlaceNew(prefab6, bundle, target, screen, At(6))}");
        }
        finally
        {
            BundleAdder.Level = previousLevel;
            try
            {
                Frosty.Core.Viewport.MeshVariationDb.LoadModifiedVariations();
            }
            catch (Exception ex)
            {
                LevelEditorLog.Warning("Shapes", "Could not refresh the editor's mesh variations", ex);
            }
        }

        string summary = $"Visibility test {run:00} placed in {targetEntry.Name}, left to right, {spacing:0.#} m apart:\n  " + string.Join("\n  ", report);
        LevelEditorLog.Info("Shapes", summary);
        return summary;
    }

    /// <summary>The static model data a blueprint's Object points at (internal), or null.</summary>
    private static object StaticModelOf(EbxAssetEntry blueprint)
    {
        try
        {
            EbxAsset asset = App.AssetManager.GetEbx(blueprint);
            PointerRef obj = ((dynamic)asset.RootObject).Object;
            if (obj.Type == PointerRefType.Internal && TypeLibrary.IsSubClassOf(obj.Internal, "StaticModelEntityData"))
                return obj.Internal;
        }
        catch (Exception)
        {
        }
        return null;
    }

    /// <summary>Adds what a new asset uses to the target bundle and places it.</summary>
    private static string PlaceNew(EbxAssetEntry blueprint, int bundle, SpawnTarget target, LevelEditorScreen screen, Matrix at)
    {
        SpawnCheck check = SpawnUtil.Check(blueprint, target);
        if (!check.CanSpawn)
            throw new InvalidOperationException(check.Problem);
        string note = BundleAdder.AddWithDependencies(blueprint, bundle, check.LoadedBundles ?? new HashSet<int>()).ToString();
        SpawnUtil.Spawn(blueprint, target, screen, false, out _, at);
        return note;
    }

    /// <summary>
    /// A copy of an ObjectBlueprint (with all its internal objects) under a new name and file guid,
    /// optionally with another mesh and without its Havok physics.
    /// </summary>
    private static EbxAssetEntry CopyBlueprint(EbxAssetEntry source, string name, int bundle, EbxAssetEntry mesh, EbxAsset meshAsset, bool keepPhysics)
    {
        EbxAsset asset = CopyEbx(App.AssetManager.GetEbx(source));
        Guid fileGuid = NewEbxGuid();
        asset.SetFileGuid(fileGuid);
        dynamic root = asset.RootObject;
        root.Name = name;
        root.SetInstanceGuid(new AssetClassGuid(FrostySdk.Utils.GenerateDeterministicGuid(asset.Objects, (Type)root.GetType(), fileGuid), -1));

        PointerRef objRef = root.Object;
        if (objRef.Type != PointerRefType.Internal)
            throw new InvalidOperationException($"{source.Name}'s Object is not inside the asset.");
        dynamic model = objRef.Internal;
        if (mesh != null)
            model.Mesh = new PointerRef(new EbxImportReference { FileGuid = mesh.Guid, ClassGuid = ((dynamic)meshAsset.RootObject).GetInstanceGuid().ExportedGuid });
        if (!keepPhysics)
            model.PhysicsData = new PointerRef();

        asset.Update();
        EbxAssetEntry entry = App.AssetManager.AddEbx(name, asset, bundle);
        App.AssetManager.ModifyEbx(name, asset);
        return entry;
    }

    /// <summary>A SpatialPrefabBlueprint holding an ObjectReferenceObjectData to a blueprint, plus collision boxes.</summary>
    private static EbxAssetEntry CreateReferencePrefab(string name, int bundle, EbxAssetEntry blueprint, List<CollisionBox> boxes, ShapeOptions options, Templates templates)
    {
        dynamic root = TypeLibrary.CreateObject("SpatialPrefabBlueprint");
        EbxAsset asset = new EbxAsset(root);
        Guid fileGuid = NewEbxGuid();
        asset.SetFileGuid(fileGuid);
        root.Name = name;
        ApplyPrefabHeader(root, templates);

        PointerRef reference = CoreUtil.CreateClassObjectRef(asset, "ObjectReferenceObjectData");
        dynamic r = reference.Internal;
        LinearTransform transform = Entity.MakeLinearTransform(Matrix.Identity);
        transform.Scale.x = 1; transform.Scale.y = 1; transform.Scale.z = 1;
        r.BlueprintTransform = transform;
        r.Blueprint = new PointerRef(new EbxImportReference { FileGuid = blueprint.Guid, ClassGuid = App.AssetManager.GetEbx(blueprint).RootInstanceGuid });
        MeshUtil.SetEnumIfPresent(r, "StreamRealm", "StreamRealm_None");
        MeshUtil.SetEnumIfPresent(r, "RadiosityTypeOverride", "RadiosityTypeOverride_None");
        MeshUtil.SetIfPresent(r, "LightmapResolutionScale", 1u);
        MeshUtil.SetIfPresent(r, "CastSunShadowEnable", true);
        MeshUtil.SetIfPresent(r, "CastReflectionEnable", true);
        MeshUtil.SetIfPresent(r, "CastEnvmapEnable", true);
        MeshUtil.SetIfPresent(r, "Excluded", false);
        root.Objects.Add(reference);

        AddBoxes(asset, root, boxes, options, templates);

        asset.Update();
        EbxAssetEntry entry = App.AssetManager.AddEbx(name, asset, bundle);
        App.AssetManager.ModifyEbx(name, asset);
        return entry;
    }
}
