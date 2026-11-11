using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.BF1Core.Utils;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Entities;
using LevelEditorPlugin.Screens;
using MeshSetPlugin.Fbx;
using SharpDX;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Entity = LevelEditorPlugin.Entities.Entity;
using MeshSet = MeshSetPlugin.Resources.MeshSet;
using MeshSetLod = MeshSetPlugin.Resources.MeshSetLod;

namespace LevelEditorPlugin.Shapes;

/// <summary>The look of a shape: one material (mesh section) of an existing rigid mesh.</summary>
public sealed class ShapeSurface
{
    public Guid MeshGuid { get; set; }
    public string MeshName { get; set; }
    public int SectionIndex { get; set; }
    public string SectionName { get; set; }

    public override string ToString() => $"{SectionName}  ({Path.GetFileName(MeshName)})";

    /// <summary>"meshGuid|section|sectionName|meshName" for the config.</summary>
    public string Serialize() => $"{MeshGuid}|{SectionIndex}|{SectionName}|{MeshName}";

    public static ShapeSurface Parse(string text)
    {
        string[] parts = (text ?? "").Split('|');
        if (parts.Length < 4 || !Guid.TryParse(parts[0], out Guid guid) || !int.TryParse(parts[1], out int section))
            return null;
        return new ShapeSurface { MeshGuid = guid, SectionIndex = section, SectionName = parts[2], MeshName = parts[3] };
    }
}

/// <summary>MEC parkour flags (PamStaticModelEntityData / PamOBBCollisionEntityData).</summary>
public sealed class ShapeOptions
{
    public bool Collision { get; set; } = true;
    public bool CantStand { get; set; }
    public bool CantClimb { get; set; }
    public bool CantVault { get; set; }
    public bool CantWallrun { get; set; }
}

public sealed class ShapeResult
{
    public EbxAssetEntry Prefab;
    public EbxAssetEntry Mesh;
    public Entity Placed;
    public string Summary;
}

/// <summary>
/// Creates a brand-new placeable object from a shape:
///  1. a new mesh asset: a copy of the surface's mesh (ebx, MeshSet resource, LOD chunks) in the
///     target layer's bundle, with its geometry replaced by the generated shape (written to a
///     temporary FBX and read with Frosty's normal FBX importer), new bounds, no occluder mesh;
///  2. a mesh variation database entry for it in the target bundle's database (the surface
///     mesh's entry, pointed at the new mesh), which is where MEC takes a mesh's textures from;
///  3. a new SpatialPrefabBlueprint holding a static model of the new mesh and, optionally, one
///     PamOBBCollisionEntity per collision box (MEC's box collision; walkable, verified in game);
///  4. a reference to the prefab placed in the target layer (through SpawnUtil: undo, network
///     registries, entity and render proxies), plus anything the new assets need added to the
///     target bundle (BundleAdder, assets + resources + chunks).
///
/// Field values of the static model and the collision boxes are copied from existing objects of
/// the loaded level (realm flags, collision layer, surface material, …) instead of zero defaults.
/// Undo removes the placed object; the new assets stay in the project.
/// </summary>
public static class ShapeAssetBuilder
{
    public const string FolderName = "LevelEditorShapes";

    public static ShapeResult Create(ShapeParameters p, ShapeSurface surface, ShapeOptions options, SpawnTarget target, LevelEditorScreen screen, Matrix placement)
    {
        string problem = ShapeGenerator.Validate(p);
        if (problem != null)
            throw new InvalidOperationException(problem);
        if (surface == null)
            throw new InvalidOperationException("Pick a surface first: select an object in the level and use one of its materials.");
        if (target == null || screen == null)
            throw new InvalidOperationException("Open a level first.");

        using PerfProbe probe = PerfProbe.Start("Shapes", $"create {p.Describe()}");

        EbxAssetEntry targetEntry = App.AssetManager.GetEbxEntry(target.FileGuid)
            ?? throw new InvalidOperationException("The target layer's asset was not found.");
        int bundle = BundleAdder.TargetBundle(targetEntry);
        if (bundle < 0)
            throw new InvalidOperationException($"{targetEntry.Name} is in no bundle, so new assets cannot be loaded with it.");

        EbxAssetEntry templateMesh = App.AssetManager.GetEbxEntry(surface.MeshGuid)
            ?? throw new InvalidOperationException($"The surface mesh {surface.MeshName} was not found.");

        Templates templates = Templates.Find(target.Root);

        // names: <level folder>/LevelEditorShapes/ShapeNNN_<description>
        string levelName = (screen.EditorProvider?.RootLayer?.Entity as ReferenceObject)?.Blueprint?.Name ?? targetEntry.Name;
        string folder = SubWorlds.SubWorldCatalog.LevelFolder(levelName);
        if (folder.Length == 0)
            folder = "Levels/";
        folder += FolderName + "/";
        int index = NextIndex(folder);
        string baseName = $"Shape{index:000}_{p.Describe()}";
        string meshName = (folder + baseName + "_mesh").ToLowerInvariant(); // meshes have lowercase names
        string prefabName = folder + baseName;

        ShapeMesh geometry = ShapeGenerator.BuildMesh(p);
        List<CollisionBox> boxes = options.Collision ? ShapeGenerator.BuildCollision(p) : new List<CollisionBox>();

        // 1. mesh
        EbxAssetEntry meshEntry = CreateMesh(templateMesh, surface, meshName, bundle, geometry, out EbxAsset meshAsset);
        probe.Phase("mesh");

        // 2. variation entry (textures)
        string variationNote = AddVariationEntry(templateMesh, meshEntry, meshAsset, bundle);
        try
        {
            Frosty.Core.Viewport.MeshVariationDb.LoadModifiedVariations();
        }
        catch (Exception ex)
        {
            LevelEditorLog.Warning("Shapes", "Could not refresh the editor's mesh variations; the shape may show default textures until the level is reopened", ex);
        }
        probe.Phase("variation entry");

        // 3. prefab
        EbxAssetEntry prefabEntry = CreatePrefab(prefabName, bundle, meshEntry, meshAsset, boxes, options, templates);
        probe.Phase("prefab");

        // 4. what the new assets use (shaders, textures, …) and placing
        SpawnCheck check = SpawnUtil.Check(prefabEntry, target);
        if (!check.CanSpawn)
            throw new InvalidOperationException(check.Problem);
        string bundleNote;
        int previousLevel = BundleAdder.Level;
        try
        {
            // new assets are useless unless the game loads them where they are placed:
            // always assets + resources + chunks (Test 3, verified in game) for shapes
            BundleAdder.Level = 3;
            // Start from the mesh: the adder always walks its start asset, but skips assets that
            // are already in the target bundle (the new mesh is), which would leave out the
            // template's shaders and textures. The prefab itself is already in the bundle.
            HashSet<int> loaded = check.LoadedBundles ?? new HashSet<int>();
            bundleNote = BundleAdder.AddWithDependencies(meshEntry, bundle, loaded).ToString();
            // and from the prefab: what the collision boxes' physics bodies use
            bundleNote += "; " + BundleAdder.AddWithDependencies(prefabEntry, bundle, loaded).ToString();
        }
        finally
        {
            BundleAdder.Level = previousLevel;
        }
        probe.Phase("bundles");

        Entity placed = SpawnUtil.Spawn(prefabEntry, target, screen, false, out _, placement);
        probe.Phase("place");

        string summary = $"Created {prefabName} ({geometry.TriangleCount} triangles, {boxes.Count} collision box(es), surface {surface}) in {targetEntry.Name}; {variationNote}; bundles: {bundleNote}";
        LevelEditorLog.Info("Shapes", summary);
        return new ShapeResult { Prefab = prefabEntry, Mesh = meshEntry, Placed = placed, Summary = summary };
    }

    private static int NextIndex(string folder)
    {
        int max = 0;
        foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx("SpatialPrefabBlueprint"))
        {
            if (!entry.Name.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                continue;
            string rest = entry.Name.Substring(folder.Length);
            if (rest.StartsWith("Shape", StringComparison.OrdinalIgnoreCase) && rest.Length >= 8 && int.TryParse(rest.Substring(5, 3), out int n))
                max = Math.Max(max, n);
        }
        return max + 1;
    }

    // --- mesh ------------------------------------------------------------------------------

    private static EbxAssetEntry CreateMesh(EbxAssetEntry template, ShapeSurface surface, string name, int bundle, ShapeMesh geometry, out EbxAsset asset)
    {
        EbxAsset templateAsset = App.AssetManager.GetEbx(template);
        if (!TypeLibrary.IsSubClassOf(templateAsset.RootObject.GetType().Name, "RigidMeshAsset"))
            throw new InvalidOperationException($"{template.Filename} is a {templateAsset.RootObject.GetType().Name}; only rigid meshes can be used as a surface.");

        // copy the ebx (same as Frosty's duplicate: write and read back), new file and root guid
        asset = CopyEbx(templateAsset);
        Guid fileGuid = NewEbxGuid();
        asset.SetFileGuid(fileGuid);
        dynamic root = asset.RootObject;
        root.Name = name;
        root.SetInstanceGuid(new AssetClassGuid(FrostySdk.Utils.GenerateDeterministicGuid(asset.Objects, (Type)root.GetType(), fileGuid), -1));
        EbxAssetEntry entry = App.AssetManager.AddEbx(name, asset, bundle);

        // copy the MeshSet resource and its LOD chunks into the target bundle
        ResAssetEntry templateRes = App.AssetManager.GetResEntry((ulong)root.MeshSetResource)
            ?? throw new InvalidOperationException($"The mesh resource of {template.Filename} was not found.");
        byte[] resBytes;
        using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(templateRes)))
            resBytes = reader.ReadToEnd();
        ResAssetEntry res = App.AssetManager.AddRes(name, FrostySdk.Managers.Entries.ResourceType.MeshSet, templateRes.ResMeta, resBytes, bundle);

        MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(res);
        if (meshSet.Lods.Count == 0)
            throw new InvalidOperationException($"{template.Filename} has no LODs.");
        if (surface.SectionIndex < 0 || surface.SectionIndex >= meshSet.Lods[0].Sections.Count || string.IsNullOrEmpty(meshSet.Lods[0].Sections[surface.SectionIndex].Name))
            throw new InvalidOperationException($"{template.Filename} has no material section {surface.SectionIndex} ({surface.SectionName}).");
        string sectionName = meshSet.Lods[0].Sections[surface.SectionIndex].Name;

        meshSet.FullName = res.Name;
        foreach (MeshSetLod lod in meshSet.Lods)
        {
            lod.Name = res.Name;
            if (lod.ChunkId != Guid.Empty)
            {
                ChunkAssetEntry templateChunk = App.AssetManager.GetChunkEntry(lod.ChunkId);
                byte[] chunkBytes;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(templateChunk)))
                    chunkBytes = reader.ReadToEnd();
                Guid chunkId = App.AssetManager.AddChunk(chunkBytes, NewChunkGuid(), null, bundle);
                lod.ChunkId = chunkId;
                res.LinkAsset(App.AssetManager.GetChunkEntry(chunkId));
            }
        }

        root.MeshSetResource = new ResourceRef(res.ResRid);
        // the occluder mesh hides what is behind the object in game: the template's would be wrong
        MeshUtil.SetIfPresent(root, "OccluderMeshResource", ResourceRef.Zero);
        MeshUtil.SetIfPresent(root, "NameHash", (uint)FrostySdk.Utils.HashString(name));
        entry.LinkAsset(res);
        App.AssetManager.ModifyRes(res.Name, meshSet);
        App.AssetManager.ModifyEbx(entry.Name, asset);

        // geometry: through a temporary FBX and Frosty's importer (handles every vertex format)
        string fbx = Path.Combine(Path.GetTempPath(), "FrostyLevelEditor", $"shape_{Guid.NewGuid():N}.fbx");
        try
        {
            SimpleFbxMeshWriter.Write(fbx, meshSet.Lods.Count, sectionName,
                geometry.Positions, geometry.Normals, geometry.Tangents, geometry.Binormals, geometry.Uvs, geometry.Indices);
            new MeshSetPlugin.FBXImporter(App.Logger).ImportFBX(fbx, meshSet, asset, entry, new MeshSetPlugin.FrostyMeshImportSettings());
        }
        finally
        {
            try { if (File.Exists(fbx)) File.Delete(fbx); } catch (Exception) { }
        }

        BoundingBox b = geometry.Bounds;
        meshSet.SetBoundingBox(new MeshSetPlugin.Resources.AxisAlignedBox
        {
            min = new MeshSetPlugin.Resources.Vec3 { x = b.Minimum.X, y = b.Minimum.Y, z = b.Minimum.Z },
            max = new MeshSetPlugin.Resources.Vec3 { x = b.Maximum.X, y = b.Maximum.Y, z = b.Maximum.Z },
        });
        App.AssetManager.ModifyRes(res.Name, meshSet);
        App.AssetManager.ModifyEbx(entry.Name, asset);
        return entry;
    }

    private static EbxAsset CopyEbx(EbxAsset source)
    {
        using (EbxBaseWriter writer = EbxBaseWriter.CreateWriter(new MemoryStream(), EbxWriteFlags.DoNotSort | EbxWriteFlags.IncludeTransient))
        {
            writer.WriteAsset(source);
            byte[] buffer = writer.ToByteArray();
            using (EbxReader reader = EbxReader.CreateReader(new MemoryStream(buffer)))
                return reader.ReadAsset<EbxAsset>();
        }
    }

    private static Guid NewEbxGuid()
    {
        byte[] random = new byte[16];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
        {
            do
                rng.GetBytes(random);
            while (App.AssetManager.GetEbxEntry(new Guid(random)) != null);
        }
        return new Guid(random);
    }

    private static Guid NewChunkGuid()
    {
        byte[] random = new byte[16];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
        {
            do
            {
                rng.GetBytes(random);
                random[15] |= 1; // as Frosty's duplicate does for chunk ids
            }
            while (App.AssetManager.GetChunkEntry(new Guid(random)) != null);
        }
        return new Guid(random);
    }

    // --- mesh variation database -----------------------------------------------------------

    /// <summary>
    /// Copies the template mesh's variation entries into the target bundle's database, pointed at
    /// the new mesh (references into the template mesh file are moved to the new file: the copy
    /// keeps the template's material object ids).
    /// </summary>
    private static string AddVariationEntry(EbxAssetEntry template, EbxAssetEntry newMesh, EbxAsset newMeshAsset, int bundle)
    {
        EbxAssetEntry targetDbEntry = BundleAdder.VariationDatabaseOf(bundle);
        if (targetDbEntry == null)
            return "WARNING: the target bundle has no mesh variation database, the shape will have no textures in game";

        EbxAsset templateAsset = App.AssetManager.GetEbx(template);
        Guid oldRoot = ((dynamic)templateAsset.RootObject).GetInstanceGuid().ExportedGuid;
        Guid newRoot = ((dynamic)newMeshAsset.RootObject).GetInstanceGuid().ExportedGuid;

        List<object> source = null;
        string sourceName = null;
        foreach (EbxAssetEntry dbEntry in new[] { targetDbEntry }.Concat(template.EnumerateBundles().Select(BundleAdder.VariationDatabaseOf)).Where(e => e != null))
        {
            EbxAsset db = App.AssetManager.GetEbx(dbEntry);
            List<object> found = ((IList)((dynamic)db.RootObject).Entries).Cast<object>().Where(v => BundleAdder.MeshOf(v) == template.Guid).ToList();
            if (found.Count > 0)
            {
                source = found;
                sourceName = dbEntry.Name;
                break;
            }
        }
        if (source == null)
            return $"WARNING: no mesh variation entry found for {template.Filename}, the shape will have no textures";

        PointerRef Remap(PointerRef pointer)
        {
            if (pointer.Type == PointerRefType.External && pointer.External.FileGuid == template.Guid)
            {
                Guid classGuid = pointer.External.ClassGuid == oldRoot ? newRoot : pointer.External.ClassGuid;
                return new PointerRef(new EbxImportReference { FileGuid = newMesh.Guid, ClassGuid = classGuid });
            }
            return pointer;
        }

        EbxAsset targetDb = App.AssetManager.GetEbx(targetDbEntry);
        IList entries = (IList)((dynamic)targetDb.RootObject).Entries;
        foreach (object variation in source)
            entries.Add(DataCopy.DeepCopy(variation, Remap));
        targetDb.Update();
        App.AssetManager.ModifyEbx(targetDbEntry.Name, targetDb);
        return $"{source.Count} variation entr(ies) from {sourceName} into {targetDbEntry.Name}";
    }

    // --- prefab ----------------------------------------------------------------------------

    private static EbxAssetEntry CreatePrefab(string name, int bundle, EbxAssetEntry meshEntry, EbxAsset meshAsset, List<CollisionBox> boxes, ShapeOptions options, Templates templates)
    {
        dynamic root = TypeLibrary.CreateObject("SpatialPrefabBlueprint");
        EbxAsset asset = new EbxAsset(root);
        Guid fileGuid = NewEbxGuid();
        asset.SetFileGuid(fileGuid);
        root.Name = name;
        if (templates.Prefab != null)
        {
            // header values of a real prefab of this level
            MeshUtil.SetIfPresent(root, "Flags", ((dynamic)templates.Prefab).Flags);
            MeshUtil.SetIfPresent(root, "TimeDeltaType", ((dynamic)templates.Prefab).TimeDeltaType);
        }

        // the static model (its internal objects, such as Havok physics made for the source's own
        // mesh, are left out)
        object model = templates.StaticModel != null
            ? DataCopy.CopyEntityData(templates.StaticModel, PointerRefPolicy)
            : TypeLibrary.CreateObject(TypeLibrary.GetType("PamStaticModelEntityData") != null ? "PamStaticModelEntityData" : "StaticModelEntityData");
        dynamic m = model;
        m.Mesh = new PointerRef(new EbxImportReference { FileGuid = meshEntry.Guid, ClassGuid = ((dynamic)meshAsset.RootObject).GetInstanceGuid().ExportedGuid });
        m.PhysicsData = new PointerRef(); // no Havok physics: collision comes from the boxes
        m.Transform = Entity.MakeLinearTransform(Matrix.Identity);
        MeshUtil.SetIfPresent(model, "Visible", true);
        MeshUtil.SetIfPresent(model, "Enabled", true);
        MeshUtil.SetIfPresent(model, "BoneCount", 0u);
        ApplyParkourFlags(model, options);
        AddChild(asset, root, model);

        // the collision boxes
        foreach (CollisionBox box in boxes)
        {
            // the collision box keeps copies of its own internal objects: its PhysicsBodies entry
            // (a RigidBodyData: realm, collision layer, physics material, motion type) is how the
            // game makes the collision. Dropping it (first version) likely crashed MEC on load.
            object obb = templates.Collision != null
                ? CopyWithInternals(templates.Collision, asset)
                : TypeLibrary.CreateObject("PamOBBCollisionEntityData");
            dynamic o = obb;
            o.Transform = Entity.MakeLinearTransform(box.Transform);
            o.HalfExtents = new FrostySdk.Ebx.Vec3 { x = box.HalfExtents.X, y = box.HalfExtents.Y, z = box.HalfExtents.Z };
            MeshUtil.SetIfPresent(obb, "Enabled", true);
            ApplyParkourFlags(obb, options);
            AddChild(asset, root, obb);
        }

        asset.Update();
        EbxAssetEntry entry = App.AssetManager.AddEbx(name, asset, bundle);
        App.AssetManager.ModifyEbx(name, asset);
        return entry;
    }

    private static void ApplyParkourFlags(object data, ShapeOptions options)
    {
        MeshUtil.SetIfPresent(data, "CantStand", options.CantStand);
        MeshUtil.SetIfPresent(data, "CantClimb", options.CantClimb);
        MeshUtil.SetIfPresent(data, "CantVault", options.CantVault);
        MeshUtil.SetIfPresent(data, "CantWallrun", options.CantWallrun);
    }

    /// <summary>Copied objects keep references to other assets but drop those into the source's own file.</summary>
    private static PointerRef PointerRefPolicy(PointerRef pointer) =>
        pointer.Type == PointerRefType.Internal ? new PointerRef() : pointer;

    private static void AddChild(EbxAsset asset, dynamic root, object data)
    {
        AddObject(asset, data);
        root.Objects.Add(new PointerRef(data));
    }

    /// <summary>
    /// Gives an object a new instance id and adds it to the asset. Flags get only the id bits (as
    /// CoreUtil.CreateClassObjectRef does for placed objects, which work in game): the high bits
    /// describe the realms of the object's property/event/link connections (see ReferenceObject),
    /// and new shape objects have none. The first version copied them from a level object
    /// (0x1F……), a suspect for the game crashing on load.
    /// </summary>
    private static void AddObject(EbxAsset asset, object data)
    {
        Type type = data.GetType();
        AssetClassGuid guid = new AssetClassGuid(FrostySdk.Utils.GenerateDeterministicGuid(asset.Objects, type, asset.FileGuid), -1);
        ((dynamic)data).SetInstanceGuid(guid);
        if (TypeLibrary.IsSubClassOf(data, "DataBusPeer"))
        {
            byte[] bytes = guid.ExportedGuid.ToByteArray();
            uint id = (uint)((bytes[2] << 16) | (bytes[1] << 8) | bytes[0]);
            type.GetProperty("Flags", BindingFlags.Public | BindingFlags.Instance)?.SetValue(data, id);
        }
        asset.AddObject(data);
    }

    /// <summary>
    /// Copies an object and every object of its own file it points to (recursively), adding the
    /// copies to <paramref name="asset"/> with new ids. References to other assets are kept.
    /// </summary>
    private static object CopyWithInternals(object source, EbxAsset asset)
    {
        Dictionary<object, object> copies = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        int depth = 0;
        PointerRef Pointer(PointerRef pointer)
        {
            if (pointer.Type != PointerRefType.Internal)
                return pointer;
            object target = pointer.Internal;
            if (copies.TryGetValue(target, out object existing))
                return existing == null ? new PointerRef() : new PointerRef(existing);
            if (depth > 16)
                return new PointerRef();
            copies[target] = null; // cycle guard: a reference back while copying becomes null
            depth++;
            object copy = DataCopy.DeepCopy(target, Pointer);
            depth--;
            AddObject(asset, copy);
            copies[target] = copy;
            return new PointerRef(copy);
        }
        return DataCopy.DeepCopy(source, Pointer);
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    // --- templates from the loaded level ---------------------------------------------------

    /// <summary>Existing objects of the level whose field values new objects start from.</summary>
    private sealed class Templates
    {
        public object StaticModel;
        public object Collision;
        public object Prefab;

        private static readonly Dictionary<object, Templates> cache = new Dictionary<object, Templates>();

        public static Templates Find(Entity anyEntityOfLevel)
        {
            Editors.EntityWorld world = anyEntityOfLevel?.World;
            if (world == null)
                return new Templates();
            lock (cache)
            {
                if (cache.TryGetValue(world, out Templates found))
                    return found;
            }

            Templates t = new Templates();
            int bestModel = -1;
            int bestCollision = -1;
            int scanned = 0;
            foreach (Entity entity in world.Entities)
            {
                if (++scanned > 400000 && t.StaticModel != null && t.Collision != null && t.Prefab != null)
                    break;
                object data = DataOf(entity);
                if (data == null)
                    continue;
                string typeName = data.GetType().Name;
                if (typeName == "PamStaticModelEntityData")
                {
                    // prefer models without physics and network ids: closest to what a shape is
                    dynamic d = data;
                    int score = 0;
                    try { if (((PointerRef)d.PhysicsData).Type == PointerRefType.Null) score += 2; } catch (Exception) { }
                    try { if ((uint)d.NetworkInfo.NetworkIdCount == 0) score += 1; } catch (Exception) { }
                    try { if ((bool)d.Visible && (bool)d.Enabled) score += 4; } catch (Exception) { }
                    if (score > bestModel)
                    {
                        bestModel = score;
                        t.StaticModel = data;
                    }
                }
                else if (typeName == "PamOBBCollisionEntityData")
                {
                    dynamic d = data;
                    int score = 0;
                    try { if ((bool)d.Enabled) score += 2; } catch (Exception) { }
                    try
                    {
                        // a physics body whose own Asset is empty (shape from HalfExtents) is what a
                        // resized box needs; a body with a baked Havok asset would keep its shape
                        IList bodies = (IList)d.PhysicsBodies;
                        if (bodies.Count == 1 && bodies[0] is PointerRef body && body.Type == PointerRefType.Internal)
                        {
                            score += 1;
                            if (((PointerRef)((dynamic)body.Internal).Asset).Type == PointerRefType.Null)
                                score += 8;
                        }
                    }
                    catch (Exception) { }
                    try { if (!(bool)d.CantStand && !(bool)d.CantClimb && !(bool)d.CantVault && !(bool)d.CantWallrun) score += 4; } catch (Exception) { }
                    if (score > bestCollision)
                    {
                        bestCollision = score;
                        t.Collision = data;
                    }
                }
                else if (t.Prefab == null && entity is SpatialPrefabReferenceObject prefabRef && prefabRef.Blueprint != null)
                {
                    t.Prefab = prefabRef.Blueprint.Data;
                }
            }

            LevelEditorLog.Info("Shapes", $"Templates from the level: static model {Describe(t.StaticModel)} (score {bestModel}), collision box {Describe(t.Collision)} (score {bestCollision}), prefab header {(t.Prefab != null ? "found" : "not found")}");
            lock (cache)
                cache[world] = t;
            return t;
        }

        private static string Describe(object data)
        {
            if (data == null)
                return "not found (defaults are used)";
            try
            {
                dynamic d = data;
                string extra = "";
                if (data.GetType().Name == "PamOBBCollisionEntityData")
                {
                    extra = $", {((IList)d.PhysicsBodies).Count} physics bodies, layer {d.CollisionLayer}, half extents {d.HalfExtents.x}/{d.HalfExtents.y}/{d.HalfExtents.z}";
                    foreach (object item in (IList)d.PhysicsBodies)
                    {
                        if (item is PointerRef body && body.Type == PointerRefType.Internal)
                        {
                            dynamic b = body.Internal;
                            PointerRef bodyAsset = b.Asset;
                            string assetText = bodyAsset.Type == PointerRefType.Null ? "none" : bodyAsset.Type == PointerRefType.External ? $"external {App.AssetManager.GetEbxEntry(bodyAsset.External.FileGuid)?.Name ?? bodyAsset.External.FileGuid.ToString()}" : $"internal {bodyAsset.Internal?.GetType().Name}";
                            extra += $"; body {body.Internal.GetType().Name} realm {b.Realm}, layer {b.CollisionLayer}, motion {b.MotionType}, asset {assetText}";
                        }
                        else if (item is PointerRef other)
                        {
                            extra += $"; body ref {other.Type}";
                        }
                    }
                }
                return $"{data.GetType().Name} flags 0x{(uint)d.Flags:X8}{extra}";
            }
            catch (Exception)
            {
                return data.GetType().Name;
            }
        }

        private static object DataOf(Entity entity)
        {
            // GetProperty("Data") would throw: entity classes redeclare Data with their own type
            try
            {
                return entity.GetRawData();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
