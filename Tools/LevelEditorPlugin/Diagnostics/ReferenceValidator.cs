using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace LevelEditorPlugin.Diagnostics;

public enum IssueSeverity
{
    Error,
    Warning,
}

public sealed class ValidationIssue
{
    public IssueSeverity Severity;
    public string Asset;
    public string Location;
    public string Message;

    public override string ToString() => $"{(Severity == IssueSeverity.Error ? "ERROR  " : "warning")}  {Asset}\n           {Location}: {Message}";
}

public sealed class ValidationResult
{
    public readonly List<ValidationIssue> Issues = new List<ValidationIssue>();
    public int AssetsChecked;
    public int ReferencesChecked;
    public int ReferencingAssetsChecked;
    public TimeSpan Duration;
    public DateTime Time = DateTime.Now;

    public int Errors => Issues.Count(i => i.Severity == IssueSeverity.Error);
    public int Warnings => Issues.Count(i => i.Severity == IssueSeverity.Warning);

    public string Format()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Validation at {Time:HH:mm:ss}: {Errors} error(s), {Warnings} warning(s)");
        sb.AppendLine($"Checked {AssetsChecked} modified asset(s), {ReferencesChecked} reference(s), {ReferencingAssetsChecked} other asset(s) that reference changed ones, in {Duration.TotalSeconds:0.0} s.");
        sb.AppendLine();
        if (Issues.Count == 0)
        {
            sb.AppendLine("No broken references found.");
        }
        else
        {
            foreach (ValidationIssue issue in Issues.OrderBy(i => i.Severity).ThenBy(i => i.Asset))
                sb.AppendLine(issue.ToString());
        }
        sb.AppendLine();
        sb.AppendLine("What is checked: in every asset modified in this project, links between objects (including");
        sb.AppendLine("event/link/property connections) must point to objects that exist; links to other assets must");
        sb.AppendLine("point to assets that exist (and, for assets modified in this project, to objects that still");
        sb.AppendLine("exist in them); no two objects may share an instance ID; and objects removed from a modified");
        sb.AppendLine("asset must not still be referenced by other assets.");
        return sb.ToString();
    }
}

/// <summary>
/// Finds broken references caused by edits, before they reach the game. Only assets modified in
/// the current project are walked (plus assets that reference objects removed from them), so it
/// stays fast on big levels. Read-only: it never changes anything.
/// </summary>
public static class ReferenceValidator
{
    /// <summary>Last result (shown by the Validation panel).</summary>
    public static ValidationResult Last { get; private set; }
    public static event Action<ValidationResult> Completed;

    private const int MaxReferencingAssets = 400;
    private const int MaxIssues = 500;

    public static ValidationResult Run()
    {
        Stopwatch timer = Stopwatch.StartNew();
        ValidationResult result = new ValidationResult();
        Dictionary<Guid, HashSet<Guid>> exportedCache = new Dictionary<Guid, HashSet<Guid>>();

        List<EbxAssetEntry> modified = App.AssetManager.EnumerateEbx(modifiedOnly: true)
            .Where(e => e.HasModifiedData || e.IsAdded)
            .ToList();

        foreach (EbxAssetEntry entry in modified)
        {
            if (result.Issues.Count >= MaxIssues)
                break;
            try
            {
                CheckAsset(entry, result, exportedCache);
            }
            catch (Exception ex)
            {
                Add(result, IssueSeverity.Warning, entry.Name, "(asset)", $"could not be checked: {ex.GetType().Name}: {ex.Message}");
            }
        }

        timer.Stop();
        result.Duration = timer.Elapsed;
        Last = result;
        LevelEditorLog.WriteBlock($"Validation ({result.Errors} errors, {result.Warnings} warnings)", result.Format());
        Completed?.Invoke(result);
        return result;
    }

    private static void Add(ValidationResult result, IssueSeverity severity, string asset, string location, string message)
    {
        if (result.Issues.Count < MaxIssues)
            result.Issues.Add(new ValidationIssue { Severity = severity, Asset = asset, Location = location, Message = message });
    }

    private static void CheckAsset(EbxAssetEntry entry, ValidationResult result, Dictionary<Guid, HashSet<Guid>> exportedCache)
    {
        EbxAsset asset = App.AssetManager.GetEbx(entry);
        if (asset == null)
            return;
        result.AssetsChecked++;

        List<object> objects = asset.Objects.ToList();
        HashSet<object> present = new HashSet<object>(objects, ReferenceComparer.Instance);

        // duplicate instance IDs
        Dictionary<Guid, int> exportedCount = new Dictionary<Guid, int>();
        foreach (object obj in objects)
        {
            AssetClassGuid id = InstanceGuid(obj);
            if (id.IsExported)
            {
                exportedCount.TryGetValue(id.ExportedGuid, out int n);
                exportedCount[id.ExportedGuid] = n + 1;
            }
        }
        foreach (KeyValuePair<Guid, int> kv in exportedCount.Where(k => k.Value > 1))
            Add(result, IssueSeverity.Error, entry.Name, $"instance {kv.Key}", $"{kv.Value} objects share this instance ID (a copy that was not given a new ID)");

        // references from this asset
        for (int i = 0; i < objects.Count; i++)
        {
            object obj = objects[i];
            string where = Describe(obj, i);
            Walk(obj, "", 0, (reference, path) =>
            {
                result.ReferencesChecked++;
                if (reference.Type == PointerRefType.Internal)
                {
                    if (reference.Internal != null && !present.Contains(reference.Internal))
                        Add(result, IssueSeverity.Error, entry.Name, where + path, $"points to a {reference.Internal.GetType().Name} that is no longer in this asset (deleted?)");
                }
                else if (reference.Type == PointerRefType.External)
                {
                    CheckExternal(entry.Name, where + path, reference.External, result, exportedCache);
                }
            });
        }

        // objects removed from this asset that other assets may still reference
        if (!entry.IsAdded)
            CheckRemovedExports(entry, asset, result);
    }

    private static void CheckExternal(string assetName, string location, EbxImportReference reference, ValidationResult result, Dictionary<Guid, HashSet<Guid>> exportedCache)
    {
        EbxAssetEntry target = App.AssetManager.GetEbxEntry(reference.FileGuid);
        if (target == null)
        {
            Add(result, IssueSeverity.Error, assetName, location, $"points to asset {reference.FileGuid}, which does not exist");
            return;
        }

        // the object inside the target can only have gone missing if the target was edited here
        if (reference.ClassGuid == Guid.Empty || !target.IsModified)
            return;

        if (!exportedCache.TryGetValue(target.Guid, out HashSet<Guid> exported))
        {
            exported = ExportedGuids(App.AssetManager.GetEbx(target));
            exportedCache[target.Guid] = exported;
        }
        if (!exported.Contains(reference.ClassGuid))
            Add(result, IssueSeverity.Error, assetName, location, $"points to object {reference.ClassGuid} in {target.Name}, which no longer exists there");
    }

    private static void CheckRemovedExports(EbxAssetEntry entry, EbxAsset current, ValidationResult result)
    {
        EbxAsset original = App.AssetManager.GetEbx(entry, getUnmodifiedData: true);
        if (original == null)
            return;

        HashSet<Guid> removed = ExportedGuids(original);
        removed.ExceptWith(ExportedGuids(current));
        if (removed.Count == 0)
            return;

        List<EbxAssetEntry> referencing = App.AssetManager.EnumerateEbx()
            .Where(e => e.Guid != entry.Guid && e.ContainsDependency(entry.Guid))
            .ToList();

        if (referencing.Count > MaxReferencingAssets)
            Add(result, IssueSeverity.Warning, entry.Name, "(asset)", $"{removed.Count} object(s) removed; {referencing.Count} assets depend on this asset, only the first {MaxReferencingAssets} were checked");

        foreach (EbxAssetEntry other in referencing.Take(MaxReferencingAssets))
        {
            EbxAsset otherAsset;
            try
            {
                otherAsset = App.AssetManager.GetEbx(other);
            }
            catch (Exception)
            {
                continue;
            }
            if (otherAsset == null)
                continue;
            result.ReferencingAssetsChecked++;

            List<object> objects = otherAsset.Objects.ToList();
            for (int i = 0; i < objects.Count; i++)
            {
                string where = Describe(objects[i], i);
                Walk(objects[i], "", 0, (reference, path) =>
                {
                    if (reference.Type == PointerRefType.External && reference.External.FileGuid == entry.Guid && removed.Contains(reference.External.ClassGuid))
                        Add(result, IssueSeverity.Error, other.Name, where + path, $"points to object {reference.External.ClassGuid} that was removed from {entry.Name}");
                });
            }
        }
    }

    private static HashSet<Guid> ExportedGuids(EbxAsset asset)
    {
        HashSet<Guid> set = new HashSet<Guid>();
        if (asset == null)
            return set;
        foreach (object obj in asset.Objects)
        {
            AssetClassGuid id = InstanceGuid(obj);
            if (id.IsExported)
                set.Add(id.ExportedGuid);
        }
        return set;
    }

    private static AssetClassGuid InstanceGuid(object obj)
    {
        try
        {
            return ((dynamic)obj).GetInstanceGuid();
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static string Describe(object obj, int index)
    {
        AssetClassGuid id = InstanceGuid(obj);
        string name = obj.GetType().Name;
        return id.IsExported ? $"{name} {id.ExportedGuid}" : $"{name} #{index}";
    }

    // --- reflection walk over ebx objects -------------------------------------------------

    private static readonly Dictionary<Type, PropertyInfo[]> propertyCache = new Dictionary<Type, PropertyInfo[]>();

    private static PropertyInfo[] Properties(Type type)
    {
        lock (propertyCache)
        {
            if (!propertyCache.TryGetValue(type, out PropertyInfo[] props))
            {
                props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                    .ToArray();
                propertyCache[type] = props;
            }
            return props;
        }
    }

    private static bool IsEbxType(Type type) => type.Namespace != null && type.Namespace.StartsWith("FrostySdk.Ebx", StringComparison.Ordinal);

    /// <summary>
    /// Calls <paramref name="onReference"/> for every PointerRef in the object's fields, including
    /// inside structs and lists. Does not follow the references themselves (every object of the
    /// asset is walked separately).
    /// </summary>
    private static void Walk(object value, string path, int depth, Action<PointerRef, string> onReference)
    {
        if (value == null || depth > 16)
            return;

        if (value is PointerRef reference)
        {
            onReference(reference, path);
            return;
        }

        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string)
            return;

        if (value is IList list && type.IsGenericType)
        {
            Type element = type.GetGenericArguments()[0];
            if (element.IsPrimitive || element.IsEnum || element == typeof(string))
                return;
            for (int i = 0; i < list.Count; i++)
                Walk(list[i], $"{path}[{i}]", depth + 1, onReference);
            return;
        }

        // ebx objects at the top level, ebx structs below it
        if (!IsEbxType(type) || (depth > 0 && !type.IsValueType))
            return;

        foreach (PropertyInfo property in Properties(type))
        {
            Type pt = property.PropertyType;
            if (pt.IsPrimitive || pt.IsEnum || pt == typeof(string))
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
            Walk(child, path + "." + property.Name, depth + 1, onReference);
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
