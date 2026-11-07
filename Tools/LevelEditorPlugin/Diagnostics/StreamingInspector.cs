using LevelEditorPlugin.Entities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace LevelEditorPlugin.Diagnostics;

/// <summary>
/// Logs which sub-worlds (streamed parts of the level) a selected object belongs to. An object in
/// a sub-world only exists in the game - visuals and collision - while that sub-world is loaded,
/// so a stretched or moved object can lose its collision when the player walks out of the
/// sub-world's streaming area. MEC streams many of its prop sub-worlds with zone streamer rods
/// ("..._ZS_..." names).
/// </summary>
public static class StreamingInspector
{
    private static readonly HashSet<object> describedSubWorlds = new HashSet<object>();

    /// <summary>Short chain for the UI, e.g. "SP_MainCity > CentralCityProps_ZS_ZS_734 (zone streamed)".</summary>
    public static string DescribeChain(Entity entity, out bool zoneStreamed)
    {
        zoneStreamed = false;
        List<string> parts = new List<string>();
        foreach (ReferenceObject sub in SubWorlds(entity))
        {
            bool zs = IsZoneStreamed(sub);
            zoneStreamed |= zs;
            parts.Add(zs ? $"{sub.DisplayName} (zone streamed)" : sub.DisplayName);
        }
        parts.Reverse();
        return parts.Count == 0 ? "(no sub-world)" : string.Join(" > ", parts);
    }

    public static void LogSelection(Entity entity)
    {
        if (entity == null)
            return;

        string chain = DescribeChain(entity, out bool zoneStreamed);
        LevelEditorLog.Info("Streaming", $"{entity.DisplayName}: sub-worlds {chain}");

        foreach (ReferenceObject sub in SubWorlds(entity))
        {
            object data = sub.RawData;
            if (data == null)
                continue;
            lock (describedSubWorlds)
            {
                if (!describedSubWorlds.Add(data))
                    continue;
            }
            LevelEditorLog.WriteBlock($"Sub-world {sub.DisplayName}", DescribeData(sub, data));
        }
    }

    private static IEnumerable<ReferenceObject> SubWorlds(Entity entity)
    {
        for (Entity p = entity.Parent; p != null; p = p.Parent)
        {
            if (p is SubWorldReferenceObject || IsZoneStreamed(p) || (p.RawData?.GetType().Name.IndexOf("SubWorld", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
            {
                if (p is ReferenceObject reference)
                    yield return reference;
            }
        }
    }

    private static bool IsZoneStreamed(Entity e)
    {
        string type = e.RawData?.GetType().Name ?? "";
        return type.IndexOf("ZoneStreamer", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string DescribeData(ReferenceObject sub, object data)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Entity type: {sub.GetType().Name}, data type: {data.GetType().Name} (base {data.GetType().BaseType?.Name})");
        sb.AppendLine($"Blueprint: {sub.Blueprint?.Name ?? "(none)"}");
        foreach (PropertyInfo property in data.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
                continue;
            object value;
            try
            {
                value = property.GetValue(data);
            }
            catch (Exception)
            {
                continue;
            }
            sb.AppendLine($"  {property.Name} = {Format(value)}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Format(object value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string s:
                return $"\"{s}\"";
            case IList list:
                return $"list of {list.Count}" + (list.Count > 0 && list.Count <= 8 ? ": " + string.Join(", ", list.Cast<object>().Select(FormatShort)) : "");
            default:
                return FormatShort(value);
        }
    }

    private static string FormatShort(object value)
    {
        if (value == null)
            return "null";
        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is Guid || value is string)
            return value.ToString();
        if (value is FrostySdk.Ebx.PointerRef reference)
        {
            if (reference.Type == FrostySdk.IO.PointerRefType.Internal)
                return reference.Internal?.GetType().Name ?? "null";
            if (reference.Type == FrostySdk.IO.PointerRefType.External)
            {
                var entry = Frosty.Core.App.AssetManager.GetEbxEntry(reference.External.FileGuid);
                return $"-> {entry?.Name ?? reference.External.FileGuid.ToString()}";
            }
            return "null";
        }
        // ebx structs: one level of fields (Vec3, transforms, bounds...)
        if (type.IsValueType && type.Namespace != null && type.Namespace.StartsWith("FrostySdk", StringComparison.Ordinal))
        {
            IEnumerable<string> fields = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Take(12)
                .Select(p =>
                {
                    try { return $"{p.Name}={ShortValue(p.GetValue(value))}"; }
                    catch (Exception) { return p.Name + "=?"; }
                });
            return "{" + string.Join(", ", fields) + "}";
        }
        return type.Name;
    }

    private static string ShortValue(object value)
    {
        if (value == null)
            return "null";
        Type type = value.GetType();
        if (value is float f)
            return f.ToString("0.###");
        if (type.IsPrimitive || type.IsEnum || value is string || value is Guid)
            return value.ToString();
        if (type.IsValueType)
        {
            IEnumerable<string> fields = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && (p.PropertyType.IsPrimitive))
                .Take(4)
                .Select(p => { try { return ShortValue(p.GetValue(value)); } catch (Exception) { return "?"; } });
            string joined = string.Join(",", fields);
            return joined.Length > 0 ? $"({joined})" : type.Name;
        }
        return type.Name;
    }
}
