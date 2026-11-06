using Frosty.Core;
using Frosty.Core.Viewport;
using FrostySdk;
using FrostySdk.IO;
using LevelEditorPlugin.Managers;
using System;
using System.Collections.Generic;
using LevelEditorPlugin.Diagnostics;

namespace LevelEditorPlugin.Assets;

[AssetBinding(DataType = typeof(FrostySdk.Ebx.MeshAsset))]
public class MeshAsset : Asset, IAssetData<FrostySdk.Ebx.MeshAsset>
{
    public FrostySdk.Ebx.MeshAsset Data => data as FrostySdk.Ebx.MeshAsset;
    public Render.MeshRenderable MeshData { get; private set; }

    private GenericAsset<FrostySdk.Ebx.MeshLodGroup> lodGroup;

    public MeshAsset(Guid fileGuid, FrostySdk.Ebx.MeshAsset inData)
        : base(fileGuid, inData)
    {
        lodGroup = LoadedAssetManager.Instance.LoadAsset<GenericAsset<FrostySdk.Ebx.MeshLodGroup>>(this, Data.LodGroup);
    }

    public void LoadResource(RenderCreateState state)
    {
        if (MeshData == null)
        {
            long diagStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var resEntry = App.AssetManager.GetResEntry(Data.MeshSetResource);
            Resources.MeshSet meshSet = App.AssetManager.GetResAs<Resources.MeshSet>(resEntry);
            MeshMaterialCollection materials = null;

            EbxAsset meshEbxAsset = LoadedAssetManager.Instance.GetEbxAsset(FileGuid);
            if (meshEbxAsset != null)
            {
                try
                {
                    materials = new MeshMaterialCollection(meshEbxAsset, new FrostySdk.Ebx.PointerRef());
                    RecordMaterials(materials);
                }
                catch (Exception ex)
                {
                    LoadReport.MeshMaterialFailure("reading materials", ex);
                    materials = null;
                }
            }

            MeshData = new Render.MeshRenderable(state, meshSet, lodGroup.Data, materials, resEntry);
            MeshData.DebugMeshName = App.AssetManager.GetEbxEntry(FileGuid)?.Name;
            MeshData.DebugMaterials = materials;
            MeshData.IsLightCard = IsLightCard(materials, MeshData.DebugMeshName, out bool emissiveOnly);
            if (emissiveOnly)
                LoadReport.Current?.EmissiveOnlyMesh(MeshData.DebugMeshName ?? FileGuid.ToString(), MeshData.IsLightCard);
            LoadReport.MeshLoaded(
                (System.Diagnostics.Stopwatch.GetTimestamp() - diagStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                MeshData.UseTexturedRendering,
                MeshData.UntexturedBuildMilliseconds,
                MeshData.TexturedBuildMilliseconds);
        }
    }

    /// <summary>
    /// <paramref name="emissiveOnly"/>: the mesh has materials and all their textures are emissive
    /// (Emissive, EmissiveMask, ...). Such meshes are drawn in their emissive texture's colour.
    /// Returns true (light card, hidden by default) only for the flat ones: all materials use a
    /// ground-lights or decal shader, or the mesh is an "emissive plane". A real object that only
    /// has an emissive texture (e.g. a turret, cables) stays visible.
    /// </summary>
    private static bool IsLightCard(MeshMaterialCollection collection, string meshName, out bool emissiveOnly)
    {
        emissiveOnly = false;
        if (collection == null)
            return false;
        try
        {
            bool anyEmissive = false;
            bool allCardShaders = true;
            int materialCount = 0;
            foreach (MeshMaterial material in collection)
            {
                materialCount++;
                foreach (dynamic parameter in material.TextureParameters)
                {
                    string name = parameter.ParameterName;
                    if (!name.StartsWith("Emissive", StringComparison.OrdinalIgnoreCase))
                        return false;
                    anyEmissive = true;
                }

                string shader = material.Shader.Type == PointerRefType.External
                    ? App.AssetManager.GetEbxEntry(material.Shader.External.FileGuid)?.Name ?? ""
                    : "";
                if (shader.IndexOf("Ground_Lights", StringComparison.OrdinalIgnoreCase) < 0
                    && shader.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) < 0)
                    allCardShaders = false;
            }

            emissiveOnly = materialCount > 0 && anyEmissive;
            if (!emissiveOnly)
                return false;
            bool emissivePlane = meshName != null && meshName.IndexOf("emissiveplane", StringComparison.OrdinalIgnoreCase) >= 0;
            return allCardShaders || emissivePlane;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RecordMaterials(MeshMaterialCollection collection)
    {
        if (LoadReport.Current == null)
            return;
        try
        {
            string meshName = App.AssetManager.GetEbxEntry(FileGuid)?.Name ?? FileGuid.ToString();
            foreach (MeshMaterial material in collection)
            {
                List<string> names = new List<string>();
                foreach (dynamic parameter in material.TextureParameters)
                    names.Add((string)parameter.ParameterName);
                List<string> vectorNames = new List<string>();
                foreach (dynamic parameter in material.VectorParameters)
                    vectorNames.Add((string)parameter.ParameterName);

                string shaderName = null;
                if (material.Shader.Type == PointerRefType.External)
                    shaderName = App.AssetManager.GetEbxEntry(material.Shader.External.FileGuid)?.Name;
                else if (material.Shader.Type == PointerRefType.Internal)
                    shaderName = "(internal shader)";

                LoadReport.MaterialSeen(meshName, shaderName, names, vectorNames);
            }
        }
        catch (Exception ex)
        {
            LevelEditorLog.Debug("Material", $"Could not record materials: {ex.Message}");
        }
    }

    public override void Dispose()
    {
        if (MeshData != null)
            MeshData.Dispose();

        LoadedAssetManager.Instance.UnloadAsset(lodGroup);
    }
}

[AssetBinding(DataType = typeof(FrostySdk.Ebx.SkinnedMeshAsset))]
public class SkinnedMeshAsset : MeshAsset
{
    public SkinnedMeshAsset(Guid fileGuid, FrostySdk.Ebx.MeshAsset inData)
        : base(fileGuid, inData)
    {
    }
}

[AssetBinding(DataType = typeof(FrostySdk.Ebx.RigidMeshAsset))]
public class RigidMeshAsset : MeshAsset
{
    public RigidMeshAsset(Guid fileGuid, FrostySdk.Ebx.MeshAsset inData)
        : base(fileGuid, inData)
    {
    }
}

[AssetBinding(DataType = typeof(FrostySdk.Ebx.CompositeMeshAsset))]
public class CompositeMeshAsset : MeshAsset
{
    public CompositeMeshAsset(Guid fileGuid, FrostySdk.Ebx.MeshAsset inData)
        : base(fileGuid, inData)
    {
    }
}
