using Frosty.Core;
using Frosty.Core.Managers;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Entities;
using Entity = LevelEditorPlugin.Entities.Entity;
using LevelEditorPlugin.Managers;
using LevelEditorPlugin.Screens;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LevelEditorPlugin.BF1Core.Utils;

/// <summary>
/// Deletes an entity's object from its blueprint together with everything that points at it
/// (event/link/property connections and other objects' references), and remembers enough to put
/// all of it back exactly (undo of a delete, or of a copy).
/// </summary>
public sealed class DeletedEntity
{
    public Entity Entity;
    public ReferenceObject LayerRoot;
    public Layers.SceneLayer SceneLayer;
    public LevelEditorScreen Screen;
    public string AssetName;
    public EbxAsset Asset;
    public object RootList;           // the blueprint's Objects list (IList of PointerRef)
    public int RootIndex;
    public PointerRef RootRef;
    public List<object> RemovedObjects = new List<object>();
    public List<ReferenceChange> Changes = new List<ReferenceChange>();
    public List<(EbxAssetEntry entry, EbxAsset asset, IList list, int index, object item)> RegistryRemovals = new();
    public int ConnectionsRemoved;
    public int ReferencesCleared;
}

/// <summary>One removed list element or cleared property, reversible.</summary>
public sealed class ReferenceChange
{
    public object Owner;
    public PropertyInfo Property; // set for a property change
    public object OldValue;
    public IList List;            // set for a list removal
    public int Index;
    public object Item;
    public bool IsConnection;

    public void Revert()
    {
        if (List != null)
            List.Insert(Math.Min(Index, List.Count), Item);
        else
            Property.SetValue(Owner, OldValue);
    }
}

public static class EntityDeletion
{
    /// <summary>Puts an undo step into the level editor's history (not whatever history was last used).</summary>
    public static void CommitEdit(IUndoUnit unit)
    {
        Editors.SpatialEditor editor = Editors.SpatialEditor.Active;
        if (editor != null)
            editor.CommitEdit(unit);
        else
            UndoManager.Instance?.CommitUndo(unit);
    }

    /// <summary>
    /// The object that delete/copy should act on. Clicking a placed prop selects the mesh entity
    /// inside its ObjectBlueprint; that blueprint has no Objects list (and is shared by every
    /// copy of the prop), so go up to the reference object placed in the level or layer.
    /// </summary>
    public static Entity ResolveEditable(Entity entity)
    {
        for (Entity current = entity; current != null; current = current.Parent)
        {
            if (HasObjectsList(current.FileGuid))
                return current;
        }
        return entity;
    }

    private static bool HasObjectsList(Guid fileGuid)
    {
        try
        {
            object root = LoadedAssetManager.Instance.GetEbxAsset(fileGuid)?.RootObject;
            return root != null && root.GetType().GetProperty("Objects") != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Clears the selection in the editor (properties panel, gizmo, BF1 window).</summary>
    public static void Deselect(LevelEditorScreen screen)
    {
        if (screen.EditorProvider != null)
            screen.EditorProvider.SelectEntity(null);
        else
            screen.ClearSelection();
    }

    /// <summary>
    /// Removes the entity from its blueprint, the render view and the layers, plus every
    /// connection and reference to its object in the same blueprint, and its network registry
    /// entries. Returns the record needed to restore it.
    /// </summary>
    public static DeletedEntity Delete(Entity entity, LevelEditorScreen screen)
    {
        EbxAssetEntry entry = App.AssetManager.GetEbxEntry(entity.FileGuid);
        EbxAsset asset = LoadedAssetManager.Instance.GetEbxAsset(entity.FileGuid);
        dynamic root = asset.RootObject;
        IList rootList = (IList)root.Objects;

        int rootIndex = -1;
        for (int i = 0; i < rootList.Count; i++)
        {
            if (CoreUtil.GetInternalClassGuid((dynamic)rootList[i]) == entity.InstanceGuid)
            {
                rootIndex = i;
                break;
            }
        }
        if (rootIndex < 0)
        {
            // Already gone from the asset (e.g. deleted before, but something of it was still
            // shown). Clear what is left in the view and layers instead of failing.
            screen.DeleteEntityFromRenderProxy(entity);
            entity.FindAncestor<ReferenceObject>()?.DeleteEntity(entity);
            entity.Layer?.Entities.Remove(entity);
            LevelEditorLog.Warning("Delete", $"{entity.DisplayName} was no longer in {entry.Name}; removed what was left of it from the view");
            return null;
        }

        PointerRef rootRef = (PointerRef)rootList[rootIndex];
        object target = rootRef.Internal;
        Guid exportedGuid = CoreUtil.GetInternalClassGuid(rootRef);

        DeletedEntity record = new DeletedEntity
        {
            Entity = entity,
            LayerRoot = entity.FindAncestor<ReferenceObject>(),
            SceneLayer = entity.Layer,
            Screen = screen,
            AssetName = entry.Name,
            Asset = asset,
            RootList = rootList,
            RootIndex = rootIndex,
            RootRef = rootRef,
        };

        List<object> before = asset.Objects.ToList();

        // out of the blueprint's Objects list first, so the reference scan below does not count
        // that entry as a reference
        rootList.RemoveAt(rootIndex);

        // connections and references to the object (all objects of the asset, including the
        // blueprint's EventConnections/LinkConnections/PropertyConnections lists)
        foreach (object obj in before)
        {
            if (ReferenceEquals(obj, target))
                continue;
            RemoveReferences(obj, target, record);
        }

        asset.Update();

        HashSet<object> after = new HashSet<object>(asset.Objects, ReferenceComparer.Instance);
        record.RemovedObjects.AddRange(before.Where(o => !after.Contains(o)));
        App.AssetManager.ModifyEbx(entry.Name, asset);

        // network registries listing the object
        foreach (EbxAssetEntry registryEntry in NetworkRegistries.Referencing(entry.Guid))
        {
            EbxAsset registry = App.AssetManager.GetEbx(registryEntry);
            IList list = (IList)((dynamic)registry.RootObject).Objects;
            bool changed = false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                PointerRef item = (PointerRef)list[i];
                if (item.Type == PointerRefType.External && item.External.FileGuid == entity.FileGuid && item.External.ClassGuid == exportedGuid)
                {
                    record.RegistryRemovals.Add((registryEntry, registry, list, i, item));
                    list.RemoveAt(i);
                    changed = true;
                }
            }
            if (changed)
            {
                registry.Update();
                App.AssetManager.ModifyEbx(registryEntry.Name, registry);
            }
        }

        // view and layers
        screen.DeleteEntityFromRenderProxy(entity);
        record.LayerRoot?.DeleteEntity(entity);
        record.SceneLayer?.Entities.Remove(entity);

        LevelEditorLog.Info("Delete", $"Deleted {entity.DisplayName} ({exportedGuid}) from {entry.Name}: {record.RemovedObjects.Count} object(s) removed, {record.ConnectionsRemoved} connection(s) and {record.ReferencesCleared} other reference(s) to it removed, {record.RegistryRemovals.Count} network registry entr(ies) removed");
        return record;
    }

    /// <summary>Puts back everything <see cref="Delete"/> removed.</summary>
    public static void Restore(DeletedEntity record)
    {
        if (record == null)
            return;
        EbxAsset asset = record.Asset;
        foreach (object obj in record.RemovedObjects)
            asset.AddObject(obj);

        IList rootList = (IList)record.RootList;
        rootList.Insert(Math.Min(record.RootIndex, rootList.Count), record.RootRef);
        for (int i = record.Changes.Count - 1; i >= 0; i--)
            record.Changes[i].Revert();
        asset.Update();
        App.AssetManager.ModifyEbx(record.AssetName, asset);

        foreach (var group in record.RegistryRemovals.GroupBy(r => r.entry))
        {
            // removed from the highest index down; put back from the lowest up
            foreach (var removal in group.OrderBy(r => r.index))
                removal.list.Insert(Math.Min(removal.index, removal.list.Count), removal.item);
            var first = group.First();
            first.asset.Update();
            App.AssetManager.ModifyEbx(first.entry.Name, first.asset);
        }

        record.LayerRoot?.AddNewEntity(record.Entity);
        record.SceneLayer?.Entities.Add(record.Entity);
        record.Screen.AddEntity(record.Entity);

        LevelEditorLog.Info("Delete", $"Restored {record.Entity.DisplayName} in {record.AssetName} ({record.ConnectionsRemoved} connection(s), {record.ReferencesCleared} reference(s) put back)");
    }

    // --- references ------------------------------------------------------------------------

    private static readonly Dictionary<Type, PropertyInfo[]> propertyCache = new Dictionary<Type, PropertyInfo[]>();

    private static PropertyInfo[] Properties(Type type)
    {
        lock (propertyCache)
        {
            if (!propertyCache.TryGetValue(type, out PropertyInfo[] props))
            {
                props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.PropertyType.IsPrimitive && !p.PropertyType.IsEnum && p.PropertyType != typeof(string))
                    .ToArray();
                propertyCache[type] = props;
            }
            return props;
        }
    }

    private static bool PointsTo(PointerRef reference, object target) =>
        reference.Type == PointerRefType.Internal && ReferenceEquals(reference.Internal, target);

    /// <summary>True if a struct (e.g. an EventConnection) has a PointerRef field pointing at target.</summary>
    private static bool StructPointsTo(object value, object target)
    {
        foreach (PropertyInfo p in Properties(value.GetType()))
        {
            if (p.PropertyType != typeof(PointerRef))
                continue;
            try
            {
                if (PointsTo((PointerRef)p.GetValue(value), target))
                    return true;
            }
            catch (Exception)
            {
            }
        }
        return false;
    }

    private static bool IsEbxStruct(Type type) => type.IsValueType && type.Namespace != null && type.Namespace.StartsWith("FrostySdk", StringComparison.Ordinal);

    private static void RemoveReferences(object owner, object target, DeletedEntity record)
    {
        foreach (PropertyInfo property in Properties(owner.GetType()))
        {
            object value;
            try
            {
                value = property.GetValue(owner);
            }
            catch (Exception)
            {
                continue;
            }
            if (value == null)
                continue;

            if (value is PointerRef reference)
            {
                if (PointsTo(reference, target) && property.CanWrite)
                {
                    record.Changes.Add(new ReferenceChange { Owner = owner, Property = property, OldValue = reference });
                    property.SetValue(owner, new PointerRef());
                    record.ReferencesCleared++;
                }
                continue;
            }

            if (value is IList list && property.PropertyType.IsGenericType)
            {
                Type element = property.PropertyType.GetGenericArguments()[0];
                bool pointerList = element == typeof(PointerRef);
                bool structList = IsEbxStruct(element);
                if (!pointerList && !structList)
                    continue;
                bool connections = property.Name.EndsWith("Connections", StringComparison.Ordinal);
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object item = list[i];
                    bool hit = pointerList ? PointsTo((PointerRef)item, target) : StructPointsTo(item, target);
                    if (!hit)
                        continue;
                    record.Changes.Add(new ReferenceChange { Owner = owner, List = list, Index = i, Item = item, IsConnection = connections });
                    list.RemoveAt(i);
                    if (connections)
                        record.ConnectionsRemoved++;
                    else
                        record.ReferencesCleared++;
                }
                continue;
            }

            // a struct property holding references (one level deep)
            if (IsEbxStruct(value.GetType()) && property.CanWrite && StructPointsTo(value, target))
            {
                object copy = value; // boxed copy
                foreach (PropertyInfo p in Properties(copy.GetType()))
                {
                    if (p.PropertyType == typeof(PointerRef) && p.CanWrite && PointsTo((PointerRef)p.GetValue(copy), target))
                        p.SetValue(copy, new PointerRef());
                }
                record.Changes.Add(new ReferenceChange { Owner = owner, Property = property, OldValue = value });
                property.SetValue(owner, copy);
                record.ReferencesCleared++;
            }
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>Cached list of NetworkRegistryAsset entries (built once per session).</summary>
public static class NetworkRegistries
{
    private static List<EbxAssetEntry> all;

    public static List<EbxAssetEntry> Referencing(Guid assetGuid)
    {
        if (all == null)
        {
            using (PerfProbe.Start("Copy", "build network registry list (once per session)"))
                all = App.AssetManager.EnumerateEbx("NetworkRegistryAsset").ToList();
            LevelEditorLog.Info("Copy", $"{all.Count} network registry assets");
        }
        return all.Where(e => e.ContainsDependency(assetGuid)).ToList();
    }
}

/// <summary>Undo/redo of a delete.</summary>
public sealed class DeleteEntitiesUndoUnit : IRedoableUndoUnit
{
    public string Text { get; }
    private readonly List<DeletedEntity> records;
    private readonly LevelEditorScreen screen;

    public DeleteEntitiesUndoUnit(List<DeletedEntity> records, LevelEditorScreen screen)
    {
        this.records = records;
        this.screen = screen;
        Text = records.Count == 1 ? $"Delete {records[0].Entity.DisplayName}" : $"Delete {records.Count} entities";
    }

    public void Do()
    {
    }

    public void Undo()
    {
        for (int i = records.Count - 1; i >= 0; i--)
            EntityDeletion.Restore(records[i]);
        MeshUtil.NotifyEntitiesChanged();
    }

    public void Redo()
    {
        for (int i = 0; i < records.Count; i++)
            records[i] = EntityDeletion.Delete(records[i].Entity, screen);
        EntityDeletion.Deselect(screen);
        MeshUtil.NotifyEntitiesChanged();
    }
}

/// <summary>Undo/redo of a copy: undo deletes the copies, redo puts them back.</summary>
public sealed class CopyEntitiesUndoUnit : IRedoableUndoUnit
{
    public string Text { get; }
    private readonly List<Entity> copies;
    private readonly LevelEditorScreen screen;
    private List<DeletedEntity> removed;

    public CopyEntitiesUndoUnit(List<Entity> copies, LevelEditorScreen screen, string verb = "Copy")
    {
        this.copies = copies;
        this.screen = screen;
        Text = copies.Count == 1 ? $"{verb} {copies[0].DisplayName}" : $"{verb} {copies.Count} entities";
    }

    public void Do()
    {
    }

    public void Undo()
    {
        EntityDeletion.Deselect(screen);
        removed = new List<DeletedEntity>();
        for (int i = copies.Count - 1; i >= 0; i--)
            removed.Add(EntityDeletion.Delete(copies[i], screen));
        MeshUtil.NotifyEntitiesChanged();
    }

    public void Redo()
    {
        if (removed == null)
            return;
        for (int i = removed.Count - 1; i >= 0; i--)
            EntityDeletion.Restore(removed[i]);
        removed = null;
        MeshUtil.NotifyEntitiesChanged();
    }
}
