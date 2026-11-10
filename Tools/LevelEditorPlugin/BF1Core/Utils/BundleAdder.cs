using Frosty.Core;
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
        public int Ebx, Res, Chunks, AlreadyLoaded;
        public bool HitLimit;
        public override string ToString() => $"[{LevelNames[Level]}] added {Ebx} asset(s), {Res} resource(s), {Chunks} chunk(s) to {Bundle} ({AlreadyLoaded} dependencies were already loaded there){(HitLimit ? " - stopped at the size limit" : "")}";
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
        Queue<EbxAssetEntry> queue = new Queue<EbxAssetEntry>();
        queue.Enqueue(asset);

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
                result.Ebx++;
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

        LevelEditorLog.Info("Bundles", $"{asset.Name}: {result}");
        return result;
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
