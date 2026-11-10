using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.Diagnostics;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BundleType = FrostySdk.Managers.Entries.BundleType;

namespace LevelEditorPlugin.BF1Core.Utils;

/// <summary>
/// Makes an asset available where it is placed: the game only loads an asset when a bundle that
/// contains it is loaded, so a prop taken from another district is missing in game unless it (and
/// what it needs: meshes, textures, their resources and data chunks) is added to a bundle that is
/// loaded at the new place.
///
/// Walks the asset's dependencies and adds everything that is not already in a loaded bundle to
/// the target bundle. Uses Frosty's normal "add to bundle" (written into the mod), so nothing is
/// copied or renamed.
/// </summary>
public static class BundleAdder
{
    public sealed class Result
    {
        public string Bundle;
        public int Ebx, Res, Chunks, AlreadyLoaded, VariationEntries;
        public string VariationNote;
        public int FromVariations;
        public bool HitLimit;
        public override string ToString() => $"[{LevelNames[Level]}] added {Ebx} asset(s), {Res} resource(s), {Chunks} chunk(s), {VariationEntries} mesh variation entr(ies){(VariationNote != null ? $" ({VariationNote})" : "")}{(FromVariations > 0 ? $", {FromVariations} of the assets because a mesh variation entry uses them (textures)" : "")} to {Bundle} ({AlreadyLoaded} dependencies were already loaded there){(HitLimit ? " - stopped at the size limit" : "")}";
    }

    private const int MaxEbx = 1500;

    /// <summary>
    /// What gets added (experimental: adding everything made MEC freeze on the loading screen;
    /// these levels exist to find out which kind of addition the game does not accept).
    /// 0 = nothing, 1 = assets (ebx) only, 2 = assets + resources, 3 = assets + resources + chunks.
    /// </summary>
    public static int Level = 0;

    public static readonly string[] LevelNames =
    {
        "Off (object is placed, but may be missing in game)",
        "Test 1: assets only",
        "Test 2: assets + resources",
        "Test 3: assets + resources + data chunks",
    };

    /// <summary>
    /// Removes every add-to-bundle in the project (ebx, res, chunks). Used to recover a project
    /// whose bundle additions break the game. Note: also removes additions made by other tools.
    /// </summary>
    public static string RemoveAllAdditions()
    {
        int ebx = 0, res = 0, chunks = 0;
        foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(modifiedOnly: true).ToList())
        {
            if (e.AddedBundles.Count > 0) { e.AddedBundles.Clear(); e.IsDirty = true; ebx++; }
        }
        foreach (ResAssetEntry r in App.AssetManager.EnumerateRes(modifiedOnly: true).ToList())
        {
            if (r.AddedBundles.Count > 0) { r.AddedBundles.Clear(); r.IsDirty = true; res++; }
        }
        foreach (ChunkAssetEntry c in App.AssetManager.EnumerateChunks(modifiedOnly: true).ToList())
        {
            if (c.AddedBundles.Count > 0) { c.AddedBundles.Clear(); c.IsDirty = true; chunks++; }
        }
        string text = $"removed bundle additions from {ebx} asset(s), {res} resource(s), {chunks} chunk(s)";
        LevelEditorLog.Info("Bundles", text);
        return text;
    }

    /// <summary>The bundle a placed object should be loaded with: the target layer asset's own bundle.</summary>
    public static int TargetBundle(EbxAssetEntry targetLayer)
    {
        List<int> bundles = targetLayer.EnumerateBundles().ToList();
        foreach (int b in bundles)
        {
            BundleEntry entry = App.AssetManager.GetBundleEntry(b);
            if (entry != null && entry.Type == BundleType.SubLevel)
                return b;
        }
        return bundles.Count > 0 ? bundles[0] : -1;
    }

    /// <param name="loaded">Bundles that are loaded where the object is placed.</param>
    public static Result AddWithDependencies(EbxAssetEntry asset, int targetBundle, HashSet<int> loaded)
    {
        Result result = new Result { Bundle = App.AssetManager.GetBundleEntry(targetBundle)?.Name ?? targetBundle.ToString() };

        // shared bundles are loaded with their superbundle; treat them as available
        HashSet<int> available = new HashSet<int>(loaded) { targetBundle };
        foreach (BundleEntry bundle in App.AssetManager.EnumerateBundles(BundleType.SharedBundle))
        {
            int id = App.AssetManager.GetBundleId(bundle);
            if (id >= 0)
                available.Add(id);
        }

        HashSet<Guid> visited = new HashSet<Guid>();
        HashSet<ulong> visitedRes = new HashSet<ulong>();
        List<EbxAssetEntry> addedMeshes = new List<EbxAssetEntry>();
        Queue<EbxAssetEntry> queue = new Queue<EbxAssetEntry>();
        queue.Enqueue(asset);
        ProcessQueue(asset, queue, targetBundle, available, visited, visitedRes, addedMeshes, result);

        // Meshes are drawn with the materials listed for them in the mesh variation database of a
        // loaded bundle. A mesh moved into a bundle without its entry has no materials there.
        // The entry can name textures that nothing else refers to (verified for the city LOD
        // meshes in vistaMeshes: their textures are only referenced by VISTA_CityBuildings'
        // variation database), so what the entries point to is added as well.
        if (Level >= 2)
        {
            HashSet<Guid> referenced = new HashSet<Guid>();
            foreach (EbxAssetEntry mesh in MeshesOf(asset, addedMeshes))
            {
                foreach (object variation in CopyVariationEntries(mesh, targetBundle, result))
                    CollectExternalReferences(variation, 0, referenced);
            }
            int before = result.Ebx;
            foreach (Guid guid in referenced)
            {
                EbxAssetEntry dep = App.AssetManager.GetEbxEntry(guid);
                if (dep != null && !visited.Contains(dep.Guid))
                    queue.Enqueue(dep);
            }
            ProcessQueue(asset, queue, targetBundle, available, visited, visitedRes, null, result);
            result.FromVariations = result.Ebx - before;
        }

        LevelEditorLog.Info("Bundles", $"{asset.Name}: {result}");
        return result;
    }

    /// <summary>
    /// The meshes whose variation entries have to be in the target bundle's database: the ones
    /// added now, and (when an earlier addition is completed) the meshes of the asset that were
    /// added to the target before.
    /// </summary>
    private static IEnumerable<EbxAssetEntry> MeshesOf(EbxAssetEntry asset, List<EbxAssetEntry> addedNow)
    {
        HashSet<Guid> seen = new HashSet<Guid>();
        foreach (EbxAssetEntry mesh in addedNow)
        {
            if (seen.Add(mesh.Guid))
                yield return mesh;
        }

        // meshes in the asset's dependency tree that were added to bundles earlier (depth-limited)
        Queue<(EbxAssetEntry, int)> queue = new Queue<(EbxAssetEntry, int)>();
        HashSet<Guid> visited = new HashSet<Guid>();
        queue.Enqueue((asset, 0));
        while (queue.Count > 0)
        {
            (EbxAssetEntry entry, int depth) = queue.Dequeue();
            if (!visited.Add(entry.Guid) || visited.Count > MaxEbx)
                continue;
            if (TypeLibrary.IsSubClassOf(entry.Type, "MeshAsset"))
            {
                if (entry.AddedBundles.Count > 0 && seen.Add(entry.Guid))
                    yield return entry;
                continue; // a mesh's own dependencies are shaders and textures, not meshes
            }
            if (depth >= 6)
                continue;
            foreach (Guid dependency in entry.EnumerateDependencies())
            {
                EbxAssetEntry dep = App.AssetManager.GetEbxEntry(dependency);
                if (dep != null && entry.AddedBundles.Count > 0)
                    queue.Enqueue((dep, depth + 1));
            }
        }
    }

    /// <summary>
    /// Adds what is in the queue, and what it depends on, to the target bundle. Assets already in a
    /// bundle loaded there are skipped (except <paramref name="asset"/> itself).
    /// </summary>
    private static void ProcessQueue(EbxAssetEntry asset, Queue<EbxAssetEntry> queue, int targetBundle, HashSet<int> available,
        HashSet<Guid> visited, HashSet<ulong> visitedRes, List<EbxAssetEntry> addedMeshes, Result result)
    {
        while (queue.Count > 0)
        {
            EbxAssetEntry entry = queue.Dequeue();
            if (!visited.Add(entry.Guid))
                continue;
            if (entry != asset && entry.EnumerateBundles().Any(available.Contains))
            {
                result.AlreadyLoaded++;
                continue;
            }
            if (result.Ebx >= MaxEbx)
            {
                result.HitLimit = true;
                break;
            }

            if (Level < 1)
                break;
            if (entry.AddToBundle(targetBundle))
            {
                result.Ebx++;
                if (addedMeshes != null && TypeLibrary.IsSubClassOf(entry.Type, "MeshAsset"))
                    addedMeshes.Add(entry);
            }
            if (Level < 2)
            {
                foreach (Guid dependency in entry.EnumerateDependencies())
                {
                    EbxAssetEntry dep = App.AssetManager.GetEbxEntry(dependency);
                    if (dep != null && !visited.Contains(dep.Guid))
                        queue.Enqueue(dep);
                }
                continue;
            }

            EbxAsset ebx;
            try
            {
                ebx = App.AssetManager.GetEbx(entry);
            }
            catch (Exception ex)
            {
                LevelEditorLog.Warning("Bundles", $"Could not read {entry.Name}", ex);
                continue;
            }

            // resources referenced from the asset (mesh sets, textures, physics...)
            foreach (object obj in ebx.Objects)
                Walk(obj, 0, rid => AddRes(App.AssetManager.GetResEntry(rid), targetBundle, available, visitedRes, result), chunk => AddChunk(chunk, targetBundle, available, result));

            // mesh shader block depot (named after the mesh)
            AddRes(App.AssetManager.GetResEntry(entry.Name.ToLower() + "_mesh/blocks"), targetBundle, available, visitedRes, result);

            foreach (Guid dependency in entry.EnumerateDependencies())
            {
                EbxAssetEntry dep = App.AssetManager.GetEbxEntry(dependency);
                if (dep != null && !visited.Contains(dep.Guid))
                    queue.Enqueue(dep);
            }
        }
    }

    /// <summary>Asset references (PointerRef to another asset) anywhere in a variation entry: textures, shaders.</summary>
    private static void CollectExternalReferences(object value, int depth, HashSet<Guid> found)
    {
        if (value == null || depth > 12)
            return;
        if (value is PointerRef pointer)
        {
            if (pointer.Type == PointerRefType.External && pointer.External.FileGuid != Guid.Empty)
                found.Add(pointer.External.FileGuid);
            return;
        }
        Type type = value.GetType();
        if (value is IList list && type.IsGenericType)
        {
            Type element = type.GetGenericArguments()[0];
            if (element.IsPrimitive || element.IsEnum || element == typeof(string))
                return;
            foreach (object item in list)
                CollectExternalReferences(item, depth + 1, found);
            return;
        }
        if (type.Namespace == null || !type.Namespace.StartsWith("FrostySdk", StringComparison.Ordinal) || type.IsPrimitive || type.IsEnum)
            return;
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
                continue;
            Type propertyType = property.PropertyType;
            if (propertyType.IsPrimitive || propertyType.IsEnum || propertyType == typeof(string))
                continue;
            object child;
            try
            {
                child = property.GetValue(value);
            }
            catch (Exception)
            {
                continue;
            }
            CollectExternalReferences(child, depth + 1, found);
        }
    }

    private static EbxAssetEntry VariationDatabaseOf(int bundle)
    {
        BundleEntry entry = App.AssetManager.GetBundleEntry(bundle);
        return entry == null ? null : App.AssetManager.EnumerateEbx(entry).FirstOrDefault(e => e.Type == "MeshVariationDatabase");
    }

    private static Guid MeshOf(object variationEntry)
    {
        try
        {
            PointerRef mesh = (PointerRef)variationEntry.GetType().GetProperty("Mesh").GetValue(variationEntry);
            return mesh.Type == PointerRefType.External ? mesh.External.FileGuid : Guid.Empty;
        }
        catch (Exception)
        {
            return Guid.Empty;
        }
    }

    /// <summary>
    /// Copies the mesh's entries from a variation database of its original bundles. Returns the
    /// mesh's entries in the target database (copied now or already there).
    /// </summary>
    private static List<object> CopyVariationEntries(EbxAssetEntry mesh, int targetBundle, Result result)
    {
        EbxAssetEntry targetDbEntry = VariationDatabaseOf(targetBundle);
        if (targetDbEntry == null)
        {
            result.VariationNote = "target bundle has no mesh variation database";
            return new List<object>();
        }
        EbxAsset targetDb = App.AssetManager.GetEbx(targetDbEntry);
        IList targetEntries = (IList)((dynamic)targetDb.RootObject).Entries;
        List<object> existing = targetEntries.Cast<object>().Where(v => MeshOf(v) == mesh.Guid).ToList();
        if (existing.Count > 0)
            return existing; // already there

        foreach (int bundle in mesh.Bundles)
        {
            EbxAssetEntry sourceDbEntry = VariationDatabaseOf(bundle);
            if (sourceDbEntry == null || sourceDbEntry == targetDbEntry)
                continue;
            EbxAsset sourceDb = App.AssetManager.GetEbx(sourceDbEntry);
            List<object> found = ((IList)((dynamic)sourceDb.RootObject).Entries).Cast<object>().Where(v => MeshOf(v) == mesh.Guid).ToList();
            if (found.Count == 0)
                continue;

            foreach (object variation in found)
                targetEntries.Add(variation); // struct: a copy; its references are to other assets
            targetDb.Update();
            App.AssetManager.ModifyEbx(targetDbEntry.Name, targetDb);
            result.VariationEntries += found.Count;
            result.VariationNote = $"from {sourceDbEntry.Name} into {targetDbEntry.Name}";
            return found;
        }
        result.VariationNote = $"no variation database entry found for {mesh.Name}";
        return new List<object>();
    }

    private static void AddRes(ResAssetEntry res, int bundle, HashSet<int> available, HashSet<ulong> visited, Result result)
    {
        if (res == null || !visited.Add(res.ResRid))
            return;
        if (res.EnumerateBundles().Any(available.Contains))
            return;
        if (res.AddToBundle(bundle))
            result.Res++;
        if (Level < 3)
            return;

        string type = ((ResourceType)res.ResType).ToString();
        try
        {
            if (type == "MeshSet")
            {
                Resources.MeshSet meshSet = App.AssetManager.GetResAs<Resources.MeshSet>(res);
                foreach (var lod in meshSet.Lods)
                {
                    if (lod.ChunkId != Guid.Empty)
                        AddChunk(App.AssetManager.GetChunkEntry(lod.ChunkId), bundle, available, result);
                }
            }
            else if (type == "Texture")
            {
                using (FrostySdk.Resources.Texture texture = App.AssetManager.GetResAs<FrostySdk.Resources.Texture>(res))
                    AddChunk(App.AssetManager.GetChunkEntry(texture.ChunkId), bundle, available, result);
            }
        }
        catch (Exception ex)
        {
            LevelEditorLog.Warning("Bundles", $"Could not read the data chunks of {res.Name} ({type})", ex);
        }
    }

    private static void AddChunk(ChunkAssetEntry chunk, int bundle, HashSet<int> available, Result result)
    {
        if (chunk == null)
            return;
        // chunks listed in no bundle live in the superbundle's chunk list and are always there
        List<int> bundles = chunk.EnumerateBundles().ToList();
        if (bundles.Count == 0 || bundles.Any(available.Contains))
            return;
        if (chunk.AddToBundle(bundle))
            result.Chunks++;
    }

    private static readonly Dictionary<Type, PropertyInfo[]> propertyCache = new Dictionary<Type, PropertyInfo[]>();

    private static PropertyInfo[] Properties(Type type)
    {
        lock (propertyCache)
        {
            if (!propertyCache.TryGetValue(type, out PropertyInfo[] props))
            {
                props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.PropertyType.IsPrimitive && !p.PropertyType.IsEnum && p.PropertyType != typeof(string) && p.PropertyType != typeof(PointerRef))
                    .ToArray();
                propertyCache[type] = props;
            }
            return props;
        }
    }

    /// <summary>Finds ResourceRef fields and Guid fields named like a chunk id in an ebx object.</summary>
    private static void Walk(object value, int depth, Action<ulong> onResource, Action<ChunkAssetEntry> onChunk)
    {
        if (value == null || depth > 10)
            return;
        Type type = value.GetType();
        if (value is IList list && type.IsGenericType)
        {
            Type element = type.GetGenericArguments()[0];
            if (element.IsPrimitive || element.IsEnum || element == typeof(string) || element == typeof(PointerRef))
                return;
            foreach (object item in list)
                Walk(item, depth + 1, onResource, onChunk);
            return;
        }
        if (type.Namespace == null || !type.Namespace.StartsWith("FrostySdk", StringComparison.Ordinal))
            return;
        if (depth > 0 && !type.IsValueType)
            return;

        foreach (PropertyInfo property in Properties(type))
        {
            object child;
            try
            {
                child = property.GetValue(value);
            }
            catch (Exception)
            {
                continue;
            }
            if (child is ResourceRef reference)
            {
                ulong rid = reference;
                if (rid != 0)
                    onResource(rid);
            }
            else if (child is Guid guid)
            {
                if (guid != Guid.Empty && property.Name.IndexOf("Chunk", StringComparison.OrdinalIgnoreCase) >= 0)
                    onChunk(App.AssetManager.GetChunkEntry(guid));
            }
            else
            {
                Walk(child, depth + 1, onResource, onChunk);
            }
        }
    }
}
