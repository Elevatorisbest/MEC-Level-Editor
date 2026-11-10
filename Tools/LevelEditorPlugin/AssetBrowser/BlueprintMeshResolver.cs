using Frosty.Core;
using Frosty.Core.Viewport;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using SharpDX;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace LevelEditorPlugin.AssetBrowser;

/// <summary>One mesh of a blueprint and where it sits relative to the blueprint's origin.</summary>
internal struct MeshPart
{
    public EbxAssetEntry Mesh;
    public Matrix Transform;
}

/// <summary>
/// Finds the meshes a blueprint shows, straight from the asset data and without creating
/// editor entities: an ObjectBlueprint has one object, a PrefabBlueprint a list of objects, and
/// objects can reference further blueprints. Anything with a "Mesh" reference counts (static,
/// dynamic and breakable models, mesh proxies, ...), wherever it sits in the object's
/// components.
/// </summary>
internal static class BlueprintMeshResolver
{
    /// <summary>How deep blueprints inside blueprints are followed.</summary>
    private const int MaxBlueprintDepth = 6;
    /// <summary>How deep components inside components are followed.</summary>
    private const int MaxObjectDepth = 12;

    private sealed class Walk
    {
        public List<MeshPart> Parts = new List<MeshPart>();
        public int MaxParts;
        public bool Truncated;
        /// <summary>Blueprints on the current path, to stop reference loops.</summary>
        public HashSet<Guid> BlueprintPath = new HashSet<Guid>();
        public int ObjectsVisited;
    }

    /// <summary>
    /// The meshes of <paramref name="blueprint"/> with their placement. <paramref name="truncated"/>
    /// is set when the blueprint has more than <paramref name="maxParts"/> meshes and the rest
    /// was left out.
    /// </summary>
    public static List<MeshPart> Resolve(EbxAssetEntry blueprint, int maxParts, out bool truncated)
    {
        Walk walk = new Walk { MaxParts = maxParts };
        VisitBlueprint(walk, blueprint, Matrix.Identity, 0);
        truncated = walk.Truncated;
        return walk.Parts;
    }

    private static void VisitBlueprint(Walk walk, EbxAssetEntry entry, Matrix parent, int depth)
    {
        if (entry == null || depth > MaxBlueprintDepth || walk.Truncated)
            return;
        if (!walk.BlueprintPath.Add(entry.Guid))
            return; // a blueprint that (indirectly) contains itself

        try
        {
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            object root = asset?.RootObject;
            if (root == null)
                return;

            // ObjectBlueprint: one object
            if (TryGet(root, "Object", out object single) && single is FrostySdk.Ebx.PointerRef singleRef)
                VisitPointer(walk, singleRef, parent, depth, 0);

            // PrefabBlueprint (and its subclasses): a list of objects
            if (TryGet(root, "Objects", out object list) && list is IEnumerable objects)
            {
                foreach (object item in objects)
                {
                    if (walk.Truncated)
                        break;
                    if (item is FrostySdk.Ebx.PointerRef itemRef)
                        VisitPointer(walk, itemRef, parent, depth, 0);
                }
            }
        }
        finally
        {
            walk.BlueprintPath.Remove(entry.Guid);
        }
    }

    private static void VisitPointer(Walk walk, FrostySdk.Ebx.PointerRef pointer, Matrix parent, int blueprintDepth, int objectDepth)
    {
        if (pointer.Type == PointerRefType.Internal && pointer.Internal != null)
            VisitObject(walk, pointer.Internal, parent, blueprintDepth, objectDepth);
    }

    private static void VisitObject(Walk walk, object data, Matrix parent, int blueprintDepth, int objectDepth)
    {
        if (data == null || walk.Truncated || objectDepth > MaxObjectDepth)
            return;
        // guard against runaway data; no real prop comes near this
        if (++walk.ObjectsVisited > 20000)
        {
            walk.Truncated = true;
            return;
        }

        // an object that places another blueprint (ObjectReferenceObjectData, PrefabReferenceObjectData, ...)
        if (TryGet(data, "Blueprint", out object blueprintValue) && blueprintValue is FrostySdk.Ebx.PointerRef blueprintRef)
        {
            if (IsExcluded(data))
                return;
            if (blueprintRef.Type == PointerRefType.External)
            {
                Matrix local = TryGet(data, "BlueprintTransform", out object blueprintTransform) ? ToMatrix(blueprintTransform) : Matrix.Identity;
                EbxAssetEntry target = App.AssetManager.GetEbxEntry(blueprintRef.External.FileGuid);
                if (target != null && (TypeLibrary.IsSubClassOf(target.Type, "ObjectBlueprint") || TypeLibrary.IsSubClassOf(target.Type, "PrefabBlueprint")))
                    VisitBlueprint(walk, target, local * parent, blueprintDepth + 1);
            }
            return;
        }

        Matrix world = parent;
        if (TryGet(data, "Transform", out object transform))
            world = ToMatrix(transform) * parent;

        if (TryGet(data, "Mesh", out object meshValue) && meshValue is FrostySdk.Ebx.PointerRef meshRef && meshRef.Type == PointerRefType.External)
        {
            EbxAssetEntry mesh = App.AssetManager.GetEbxEntry(meshRef.External.FileGuid);
            if (mesh != null && TypeLibrary.IsSubClassOf(mesh.Type, "MeshAsset"))
            {
                if (walk.Parts.Count >= walk.MaxParts)
                {
                    walk.Truncated = true;
                    return;
                }
                walk.Parts.Add(new MeshPart { Mesh = mesh, Transform = world });
            }
        }

        if (TryGet(data, "Components", out object componentList) && componentList is IEnumerable components)
        {
            foreach (object item in components)
            {
                if (walk.Truncated)
                    break;
                if (item is FrostySdk.Ebx.PointerRef componentRef)
                    VisitPointer(walk, componentRef, world, blueprintDepth, objectDepth + 1);
            }
        }
    }

    /// <summary>Reference objects can be switched off with their "Excluded" flag.</summary>
    private static bool IsExcluded(object data)
    {
        return TryGet(data, "Excluded", out object excluded) && excluded is bool flag && flag;
    }

    private static bool TryGet(object data, string name, out object value)
    {
        value = null;
        try
        {
            PropertyInfo property = data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || property.GetIndexParameters().Length != 0)
                return false;
            value = property.GetValue(data);
            return value != null;
        }
        catch (AmbiguousMatchException)
        {
            return false;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
    }

    private static Matrix ToMatrix(object linearTransform)
    {
        try
        {
            Matrix m = SharpDXUtils.FromLinearTransform(linearTransform);
            // a broken transform (all zero, NaN) would hide or blow up the whole picture
            if (float.IsNaN(m.M11) || float.IsNaN(m.M41) || float.IsInfinity(m.M41) || Math.Abs(m.Determinant()) < 1e-12f)
                return Matrix.Identity;
            return m;
        }
        catch (Exception)
        {
            return Matrix.Identity;
        }
    }
}
