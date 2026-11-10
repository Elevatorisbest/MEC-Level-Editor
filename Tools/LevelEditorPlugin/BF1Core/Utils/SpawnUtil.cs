using Frosty.Core;
using Frosty.Core.Managers;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Entities;
using LevelEditorPlugin.Managers;
using LevelEditorPlugin.Screens;
using System;
using System.Collections.Generic;
using System.Linq;
using Entity = LevelEditorPlugin.Entities.Entity;

namespace LevelEditorPlugin.BF1Core.Utils;

/// <summary>Where a placed object goes: a blueprint (layer / sub-world / level) and its scene layer.</summary>
public sealed class SpawnTarget
{
    public ReferenceObject Root;       // the reference object whose blueprint receives the object
    public Layers.SceneLayer Layer;    // scene layer for the panels and visibility
    public Guid FileGuid;              // the blueprint asset that is modified
    public string Description;

    /// <summary>The layer holding an existing object (the same place copies go).</summary>
    public static SpawnTarget SameAs(Entity anchor)
    {
        if (anchor == null)
            return null;
        ReferenceObject root = anchor.FindAncestor<ReferenceObject>();
        if (root == null)
            return null;
        return new SpawnTarget { Root = root, Layer = anchor.Layer, FileGuid = anchor.FileGuid, Description = $"the layer of {anchor.DisplayName}" };
    }

    /// <summary>The level's own blueprint: always loaded while the level is.</summary>
    public static SpawnTarget LevelRoot(LevelEditorScreen screen)
    {
        Layers.SceneLayer rootLayer = screen?.EditorProvider?.RootLayer;
        if (!(rootLayer?.Entity is ReferenceObject root) || root.Blueprint == null)
            return null;
        return new SpawnTarget { Root = root, Layer = rootLayer, FileGuid = root.Blueprint.FileGuid, Description = $"the level itself ({root.DisplayName}, always loaded)" };
    }
}

/// <summary>Result of checking whether a blueprint can be placed into a layer.</summary>
public sealed class SpawnCheck
{
    public bool CanSpawn;
    public string ReferenceType;   // ObjectReferenceObjectData etc.
    public string Problem;         // why it cannot be placed
    public string BundleWarning;   // set when the game might not have the asset loaded there
    public string TargetAsset;
    public EbxAssetEntry TargetEntry;
    public HashSet<int> LoadedBundles;
}

/// <summary>
/// Places a blueprint from the data explorer (an ObjectBlueprint such as a prop, or a prefab)
/// into the level, as a new reference object in the blueprint that holds the selected object.
/// </summary>
public static class SpawnUtil
{
    /// <summary>Which reference object type points at which kind of blueprint.</summary>
    private static string ReferenceTypeFor(string blueprintType)
    {
        if (TypeLibrary.IsSubClassOf(blueprintType, "SpatialPrefabBlueprint"))
            return "SpatialPrefabReferenceObjectData";
        if (TypeLibrary.IsSubClassOf(blueprintType, "PrefabBlueprint"))
            return "PrefabReferenceObjectData";
        if (TypeLibrary.IsSubClassOf(blueprintType, "ObjectBlueprint"))
            return "ObjectReferenceObjectData";
        return null;
    }

    public static SpawnCheck Check(EbxAssetEntry blueprint, SpawnTarget target)
    {
        SpawnCheck check = new SpawnCheck();
        if (blueprint == null)
        {
            check.Problem = "Select a blueprint in the data explorer first (e.g. an ObjectBlueprint of a prop).";
            return check;
        }
        if (target == null)
        {
            check.Problem = "No place to put it: select an object in the level (for 'same layer'), or open a level (for 'level root').";
            return check;
        }
        check.ReferenceType = ReferenceTypeFor(blueprint.Type);
        if (check.ReferenceType == null)
        {
            check.Problem = $"{blueprint.Type} cannot be placed directly. Pick an ObjectBlueprint, PrefabBlueprint or SpatialPrefabBlueprint (meshes are placed through their ObjectBlueprint).";
            return check;
        }
        if (TypeLibrary.GetType(check.ReferenceType) == null)
        {
            check.Problem = $"This game has no {check.ReferenceType}.";
            return check;
        }

        EbxAssetEntry targetEntry = App.AssetManager.GetEbxEntry(target.FileGuid);
        if (targetEntry == null)
        {
            check.Problem = "The target layer's asset was not found.";
            return check;
        }
        check.TargetAsset = targetEntry.Name;
        check.TargetEntry = targetEntry;

        // Heuristic: the game loads an asset when a bundle containing it is loaded. If the
        // blueprint shares no bundle with the target layer or the sub-worlds above it, it may
        // not be loaded where it is placed (invisible / missing in game).
        HashSet<int> loaded = new HashSet<int>(targetEntry.EnumerateBundles());
        for (Entity p = target.Root; p != null; p = p.Parent)
        {
            if (p is ReferenceObject reference && reference.Blueprint != null)
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(reference.Blueprint.FileGuid);
                if (e != null)
                    loaded.UnionWith(e.EnumerateBundles());
            }
        }
        check.LoadedBundles = loaded;
        if (!blueprint.EnumerateBundles().Any(loaded.Contains))
        {
            string bundles = string.Join(", ", blueprint.EnumerateBundles().Take(3).Select(b => App.AssetManager.GetBundleEntry(b)?.Name ?? b.ToString()));
            check.BundleWarning = $"{blueprint.Filename} is not in any bundle this layer or its sub-worlds load (it is in: {bundles}). It shows in the editor but may be missing in game.";
        }

        check.CanSpawn = true;
        return check;
    }

    /// <summary>
    /// Creates the reference object at the camera, its entity and render proxies, network
    /// registry entries, and an undo step. Returns the new entity.
    /// </summary>
    /// <summary>
    /// Adds the blueprint and what it needs to the target layer's bundle when it is not loaded
    /// there already. Returns a description, or null when nothing was needed.
    /// </summary>
    public static string MakeAvailable(EbxAssetEntry blueprint, SpawnCheck check)
    {
        if (check.BundleWarning == null || check.TargetEntry == null)
            return null;
        int bundle = BundleAdder.TargetBundle(check.TargetEntry);
        if (bundle < 0)
            return $"{check.TargetEntry.Name} is in no bundle; nothing was added.";
        using (PerfProbe.Start("Bundles", $"add {blueprint.Name} with dependencies"))
            return BundleAdder.AddWithDependencies(blueprint, bundle, check.LoadedBundles).ToString();
    }

    public static Entity Spawn(EbxAssetEntry blueprint, SpawnTarget target, LevelEditorScreen screen, bool addMissingToBundle, out string bundleNote)
    {
        SpawnCheck check = Check(blueprint, target);
        if (!check.CanSpawn)
            throw new InvalidOperationException(check.Problem);
        bundleNote = addMissingToBundle ? MakeAvailable(blueprint, check) : check.BundleWarning;

        var camera = LevelEditorScreen.EditorCamera;
        if (camera == null)
            throw new InvalidOperationException("The level editor camera was not found.");

        using PerfProbe probe = PerfProbe.Start("Spawn", $"place {blueprint.Name}");

        EbxAssetEntry targetEntry = App.AssetManager.GetEbxEntry(target.FileGuid);
        EbxAsset targetAsset = LoadedAssetManager.Instance.GetEbxAsset(target.FileGuid);
        if (targetAsset == null)
            throw new InvalidOperationException($"{targetEntry.Name} is not loaded in the editor.");
        dynamic root = targetAsset.RootObject;

        EbxAsset blueprintAsset = App.AssetManager.GetEbx(blueprint);
        Guid blueprintRootGuid = blueprintAsset.RootInstanceGuid;

        PointerRef newRef = CoreUtil.CreateClassObjectRef(targetAsset, check.ReferenceType);
        dynamic obj = newRef.Internal;

        LinearTransform transform = new LinearTransform();
        transform.right.x = 1; transform.up.y = 1; transform.forward.z = 1;
        transform.trans.x = camera.WorldMatrix.TranslationVector.X;
        transform.trans.y = camera.WorldMatrix.TranslationVector.Y;
        transform.trans.z = camera.WorldMatrix.TranslationVector.Z;
        transform.Translate.x = transform.trans.x;
        transform.Translate.y = transform.trans.y;
        transform.Translate.z = transform.trans.z;
        transform.Scale.x = 1; transform.Scale.y = 1; transform.Scale.z = 1;

        obj.BlueprintTransform = transform;
        obj.Blueprint = new PointerRef(new EbxImportReference { FileGuid = blueprint.Guid, ClassGuid = blueprintRootGuid });
        MeshUtil.SetEnumIfPresent(obj, "StreamRealm", "StreamRealm_None");
        MeshUtil.SetEnumIfPresent(obj, "RadiosityTypeOverride", "RadiosityTypeOverride_None");
        MeshUtil.SetIfPresent(obj, "LightmapResolutionScale", 1u);
        MeshUtil.SetIfPresent(obj, "CastSunShadowEnable", true);
        MeshUtil.SetIfPresent(obj, "CastReflectionEnable", true);
        MeshUtil.SetIfPresent(obj, "CastEnvmapEnable", true);
        MeshUtil.SetIfPresent(obj, "Excluded", false);
        MeshUtil.SetEnumIfPresent(obj, "LocalPlayerId", "LocalPlayerId_Invalid");

        root.Objects.Add(newRef);
        targetAsset.Update();
        App.AssetManager.ModifyEbx(targetEntry.Name, targetAsset);
        probe.Phase("create reference object");

        // network registries of the target asset list its exported objects
        if (CoreUtil.IsExportedPointerRef(newRef))
        {
            Guid exported = CoreUtil.GetInternalClassGuid(newRef);
            foreach (EbxAssetEntry registryEntry in NetworkRegistries.Referencing(targetEntry.Guid))
            {
                EbxAsset registry = App.AssetManager.GetEbx(registryEntry);
                ((dynamic)registry.RootObject).Objects.Add(new PointerRef(new EbxImportReference { FileGuid = targetEntry.Guid, ClassGuid = exported }));
                registry.Update();
                App.AssetManager.ModifyEbx(registryEntry.Name, registry);
            }
        }
        probe.Phase("network registries");

        ReferenceObject layerRoot = target.Root;
        Entity entity = layerRoot.MyCreateEntity((GameObjectData)newRef.Internal);
        if (target.Layer != null)
            target.Layer.AddEntity(entity);
        screen.AddEntity(entity);
        layerRoot.AddNewEntity(entity);
        probe.Phase("entity + render proxies");

        EntityDeletion.CommitEdit(new CopyEntitiesUndoUnit(new List<Entity> { entity }, screen, "Place"));
        SpawnHistory.Add(blueprint);
        MeshUtil.NotifyEntitiesChanged();

        LevelEditorLog.Info("Spawn", $"Placed {blueprint.Name} ({check.ReferenceType}) into {targetEntry.Name}" + (check.BundleWarning != null ? $" - warning: {check.BundleWarning}" : ""));
        return entity;
    }
}

/// <summary>Recently placed and favourite blueprints (saved in Frosty's config).</summary>
public static class SpawnHistory
{
    private const string RecentKey = "LevelEditor_RecentBlueprints";
    private const string FavouriteKey = "LevelEditor_FavouriteBlueprints";
    private const int MaxRecent = 15;

    public static event Action Changed;

    public static List<string> Recent => Load(RecentKey);
    public static List<string> Favourites => Load(FavouriteKey);

    public static void Add(EbxAssetEntry entry)
    {
        List<string> list = Load(RecentKey);
        list.Remove(entry.Name);
        list.Insert(0, entry.Name);
        if (list.Count > MaxRecent)
            list.RemoveRange(MaxRecent, list.Count - MaxRecent);
        Save(RecentKey, list);
    }

    public static bool IsFavourite(string name) => Load(FavouriteKey).Contains(name);

    public static void ToggleFavourite(string name)
    {
        List<string> list = Load(FavouriteKey);
        if (!list.Remove(name))
            list.Add(name);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        Save(FavouriteKey, list);
    }

    private static List<string> Load(string key)
    {
        string text = Config.Get(key, "", ConfigScope.Game);
        return text.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static void Save(string key, List<string> list)
    {
        Config.Add(key, string.Join("|", list), ConfigScope.Game);
        Config.Save();
        Changed?.Invoke();
    }
}
