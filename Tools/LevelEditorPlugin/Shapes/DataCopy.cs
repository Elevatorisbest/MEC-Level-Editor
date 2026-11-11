using FrostySdk.Ebx;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LevelEditorPlugin.Shapes;

/// <summary>
/// Deep copies of ebx data objects (entity data, variation entries) for new assets.
/// Lists and Frosty data types are copied member by member; references (PointerRef) go through
/// a callback that can keep, change or drop them (a dropped list entry is left out).
/// </summary>
internal static class DataCopy
{
    // Frosty value types that are copied as they are
    private static readonly HashSet<string> Opaque = new HashSet<string>
    {
        "CString", "ResourceRef", "FileRef", "TypeRef", "BoxedValueRef", "AssetClassGuid", "Sha1", "Guid",
    };

    private static readonly Dictionary<Type, PropertyInfo[]> propertyCache = new Dictionary<Type, PropertyInfo[]>();

    /// <summary>A copy of an entity data object without its instance id (the caller sets one).</summary>
    public static object CopyEntityData(object source, Func<PointerRef, PointerRef> pointers) => DeepCopy(source, pointers);

    public static object DeepCopy(object value, Func<PointerRef, PointerRef> pointers) => Copy(value, pointers, 0);

    private static object Copy(object value, Func<PointerRef, PointerRef> pointers, int depth)
    {
        if (value == null)
            return null;
        if (depth > 32)
            throw new InvalidOperationException("Data nested too deeply to copy.");

        Type type = value.GetType();
        if (value is PointerRef pointer)
            return pointers(pointer);
        if (type.IsPrimitive || type.IsEnum || value is string || Opaque.Contains(type.Name))
            return value;

        if (value is IList list && type.IsGenericType)
        {
            IList copy = (IList)Activator.CreateInstance(type);
            foreach (object item in list)
            {
                object itemCopy = Copy(item, pointers, depth + 1);
                if (item is PointerRef original && original.Type != FrostySdk.IO.PointerRefType.Null
                    && itemCopy is PointerRef copied && copied.Type == FrostySdk.IO.PointerRefType.Null)
                    continue; // dropped reference
                copy.Add(itemCopy);
            }
            return copy;
        }

        if (type.Namespace == null || !type.Namespace.StartsWith("FrostySdk", StringComparison.Ordinal))
            return value;

        // a Frosty data type (class, or boxed struct): new instance, members copied
        object result = Activator.CreateInstance(type);
        foreach (PropertyInfo property in Properties(type))
        {
            object member;
            try
            {
                member = property.GetValue(value);
            }
            catch (Exception)
            {
                continue;
            }
            property.SetValue(result, Copy(member, pointers, depth + 1));
        }
        return result;
    }

    private static PropertyInfo[] Properties(Type type)
    {
        lock (propertyCache)
        {
            if (!propertyCache.TryGetValue(type, out PropertyInfo[] properties))
            {
                properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.Name != "__InstanceGuid")
                    .ToArray();
                propertyCache[type] = properties;
            }
            return properties;
        }
    }
}
