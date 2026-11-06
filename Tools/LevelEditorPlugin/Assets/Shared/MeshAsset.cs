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
            LoadReport.MeshLoaded(
                (System.Diagnostics.Stopwatch.GetTimestamp() - diagStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                MeshData.UseTexturedRendering,
                MeshData.UntexturedBuildMilliseconds,
                MeshData.TexturedBuildMilliseconds);
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
