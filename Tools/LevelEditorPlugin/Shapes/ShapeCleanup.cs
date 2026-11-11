using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.BF1Core.Utils;
using LevelEditorPlugin.Diagnostics;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace LevelEditorPlugin.Shapes;

/// <summary>
/// Takes every Shape Builder shape out of the project: placed references to shape prefabs (and
/// their network registry entries), mesh variation entries of shape meshes, then the added shape
/// assets themselves (prefab, mesh ebx, MeshSet resource, chunks) with Frosty's revert, which
/// removes added assets. Deleting only the placed object is not enough when a shape breaks the
/// game: its assets are still in the level's bundle and are loaded anyway.
/// The open level has to be reopened afterwards (its entities still show the old state).
/// </summary>
public static class ShapeCleanup
{
    public static string RemoveAll()
    {
        string marker = "/" + ShapeAssetBuilder.FolderName + "/";
        List<EbxAssetEntry> shapeAssets = App.AssetManager.EnumerateEbx()
            .Where(e => e.IsAdded && e.Name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        if (shapeAssets.Count == 0)
            return "There are no shapes in this project.";

        HashSet<Guid> prefabs = new HashSet<Guid>(shapeAssets.Where(e => TypeLibrary.IsSubClassOf(e.Type, "PrefabBlueprint")).Select(e => e.Guid));
        HashSet<Guid> meshes = new HashSet<Guid>(shapeAssets.Where(e => TypeLibrary.IsSubClassOf(e.Type, "MeshAsset")).Select(e => e.Guid));
        HashSet<Guid> all = new HashSet<Guid>(shapeAssets.Select(e => e.Guid));

        // 1. references in modified blueprints, and variation entries in modified databases
        Dictionary<Guid, HashSet<Guid>> removedByFile = new Dictionary<Guid, HashSet<Guid>>();
        int references = 0, variations = 0, registryEntries = 0;
        List<EbxAssetEntry> modified = App.AssetManager.EnumerateEbx(modifiedOnly: true).Where(e => !all.Contains(e.Guid)).ToList();
        foreach (EbxAssetEntry entry in modified)
        {
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            object root = asset.RootObject;
            bool changed = false;

            if (TypeLibrary.IsSubClassOf(root, "PrefabBlueprint"))
            {
                List<PointerRef> objects = ((dynamic)root).Objects;
                for (int i = objects.Count - 1; i >= 0; i--)
                {
                    if (objects[i].Type != PointerRefType.Internal || !TypeLibrary.IsSubClassOf(objects[i].Internal, "ReferenceObjectData"))
                        continue;
                    PointerRef blueprint = ((dynamic)objects[i].Internal).Blueprint;
                    if (blueprint.Type != PointerRefType.External || !prefabs.Contains(blueprint.External.FileGuid))
                        continue;
                    object removed = objects[i].Internal;
                    AssetClassGuid id = ((dynamic)removed).GetInstanceGuid();
                    if (!removedByFile.TryGetValue(entry.Guid, out HashSet<Guid> set))
                        removedByFile[entry.Guid] = set = new HashSet<Guid>();
                    set.Add(id.ExportedGuid);
                    objects.RemoveAt(i);
                    asset.RemoveObject(removed);
                    references++;
                    changed = true;
                }
            }
            else if (root.GetType().Name == "MeshVariationDatabase")
            {
                IList entries = (IList)((dynamic)root).Entries;
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (meshes.Contains(BundleAdder.MeshOf(entries[i])))
                    {
                        entries.RemoveAt(i);
                        variations++;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                asset.Update();
                App.AssetManager.ModifyEbx(entry.Name, asset);
            }
        }

        // 2. network registry entries of the removed references
        if (removedByFile.Count > 0)
        {
            foreach (EbxAssetEntry entry in modified.Where(e => e.Type == "NetworkRegistryAsset"))
            {
                EbxAsset registry = App.AssetManager.GetEbx(entry);
                List<PointerRef> objects = ((dynamic)registry.RootObject).Objects;
                int before = objects.Count;
                objects.RemoveAll(p => p.Type == PointerRefType.External
                    && removedByFile.TryGetValue(p.External.FileGuid, out HashSet<Guid> ids) && ids.Contains(p.External.ClassGuid));
                if (objects.Count != before)
                {
                    registryEntries += before - objects.Count;
                    registry.Update();
                    App.AssetManager.ModifyEbx(entry.Name, registry);
                }
            }
        }

        // 3. the shape assets (revert removes added assets, with their linked resources and chunks)
        foreach (EbxAssetEntry entry in shapeAssets)
            App.AssetManager.RevertAsset(entry);

        string text = $"Removed {shapeAssets.Count} shape asset(s), {references} placed shape(s), {registryEntries} network registry entr(ies) and {variations} mesh variation entr(ies). Save the project and reopen the level.";
        LevelEditorLog.Info("Shapes", text);
        return text;
    }
}
