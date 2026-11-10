using Frosty.Core;
using FrostySdk;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LevelEditorPlugin.AssetBrowser;

/// <summary>Which kinds of placeable blueprints to list.</summary>
[Flags]
public enum AssetKinds
{
    None = 0,
    /// <summary>ObjectBlueprint: a single object, usually one prop.</summary>
    Objects = 1,
    /// <summary>PrefabBlueprint and its subclasses: several objects placed together.</summary>
    Prefabs = 2,
    All = Objects | Prefabs,
}

/// <summary>A folder below the current one in the browser's folder list.</summary>
public sealed class AssetFolder
{
    public string Name { get; set; }
    public string FullPath { get; set; }
    public int Count { get; set; }
    public string Label => $"{Name}  ({Count})";
}

/// <summary>
/// The game's placeable blueprints (ObjectBlueprint and PrefabBlueprint assets), listed once per
/// session, with search and folder lookups for the asset browser.
/// </summary>
public static class AssetCatalog
{
    private static Task<List<EbxAssetEntry>> loading;

    /// <summary>All placeable blueprints, sorted by file name. The listing runs once, in the background.</summary>
    public static Task<List<EbxAssetEntry>> LoadAsync()
    {
        if (loading == null)
        {
            loading = Task.Run(() =>
            {
                using (PerfProbe.Start("Browser", "list blueprint assets (once per session)"))
                {
                    List<EbxAssetEntry> list = App.AssetManager.EnumerateEbx("ObjectBlueprint")
                        .Concat(App.AssetManager.EnumerateEbx("PrefabBlueprint"))
                        .GroupBy(e => e.Guid).Select(g => g.First())
                        .OrderBy(e => e.Filename, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    LevelEditorLog.Info("Browser", $"{list.Count} placeable blueprint assets");
                    return list;
                }
            });
        }
        return loading;
    }

    public static bool IsPrefab(EbxAssetEntry entry) => TypeLibrary.IsSubClassOf(entry.Type, "PrefabBlueprint");

    private static bool InFolder(EbxAssetEntry entry, string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return true;
        string name = entry.Name;
        return name.Length > folder.Length + 1
            && name[folder.Length] == '/'
            && name.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Assets below <paramref name="folder"/> (empty = everywhere) whose path contains every word
    /// of <paramref name="search"/>. With a search, assets whose file name matches come first and
    /// shorter names before longer ones; without, the order is by file name.
    /// </summary>
    public static List<EbxAssetEntry> Filter(List<EbxAssetEntry> all, string folder, string search, AssetKinds kinds)
    {
        string[] terms = (search ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<EbxAssetEntry> query = all.Where(e => InFolder(e, folder));
        if (kinds != AssetKinds.All)
            query = query.Where(e => IsPrefab(e) ? (kinds & AssetKinds.Prefabs) != 0 : (kinds & AssetKinds.Objects) != 0);
        if (terms.Length == 0)
            return query.ToList();

        return query
            .Where(e => terms.All(t => e.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(e => terms.All(t => e.Filename.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) ? 0 : 1)
            .ThenBy(e => e.Filename.Length)
            .ThenBy(e => e.Filename, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The folders directly below <paramref name="folder"/>, with how many assets each holds (at any depth).</summary>
    public static List<AssetFolder> SubFolders(List<EbxAssetEntry> all, string folder)
    {
        Dictionary<string, AssetFolder> result = new Dictionary<string, AssetFolder>(StringComparer.OrdinalIgnoreCase);
        int start = string.IsNullOrEmpty(folder) ? 0 : folder.Length + 1;
        foreach (EbxAssetEntry entry in all)
        {
            if (!InFolder(entry, folder))
                continue;
            string name = entry.Name;
            int end = name.IndexOf('/', start);
            if (end < 0)
                continue; // the asset sits directly in this folder
            string segment = name.Substring(start, end - start);
            if (!result.TryGetValue(segment, out AssetFolder sub))
            {
                sub = new AssetFolder { Name = segment, FullPath = name.Substring(0, end) };
                result.Add(segment, sub);
            }
            sub.Count++;
        }
        return result.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The folder above <paramref name="folder"/> ("" for a top-level folder).</summary>
    public static string ParentFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return "";
        int slash = folder.LastIndexOf('/');
        return slash < 0 ? "" : folder.Substring(0, slash);
    }
}
