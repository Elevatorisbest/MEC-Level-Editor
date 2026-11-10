using Frosty.Core.Screens;
using Frosty.Core.Viewport;
using FrostySdk;
using LevelEditorPlugin.Entities;
using SharpDX;
using System.Collections.Generic;
using D3D11 = SharpDX.Direct3D11;

namespace LevelEditorPlugin.Render.Proxies;

public class ModelRenderProxy : RenderProxy
{
    protected MeshRenderable renderData;

    /// <summary>The (shared) mesh this proxy draws; used for diagnostics.</summary>
    public MeshRenderable MeshData => renderData;
    protected int lodIndex;

    // Shared by all proxies (owned by the screen's shader library); never disposed here.
    private ShaderPermutation permutation;
    private readonly RenderCreateState createState;
    private bool isSelected;

    private static GeometryDeclarationDesc GeometryDecl = GeometryDeclarationDesc.Create(new GeometryDeclarationDesc.Element[]
    {
        new GeometryDeclarationDesc.Element
        {
            Usage = VertexElementUsage.Pos,
            Format = VertexElementFormat.Float3
        },
        new GeometryDeclarationDesc.Element
        {
            Usage = VertexElementUsage.Normal,
            Format = VertexElementFormat.Float3
        }
    });

    public ModelRenderProxy(RenderCreateState state, ISpatialEntity owner, MeshRenderable meshData)
        : base(owner)
    {
        renderData = meshData;
        createState = state;

        RecalculateBoundingBox();

        // The solid-colour constant buffer is no longer created per proxy here; see Render.
        permutation = state.ShaderLibrary.GetUserShader("LevelShader", GeometryDecl);
        permutation.IsTwoSided = true;
        permutation.LoadShaders(state.Device);
    }

    public ModelRenderProxy(RenderCreateState state, MeshProxyEntity owner)
        : this(state, owner, owner.Mesh.MeshData)
    {
    }

    public ModelRenderProxy(RenderCreateState state, StaticModelEntity owner)
        : this(state, owner, owner.Mesh.MeshData)
    {
    }

#if true
    public ModelRenderProxy(RenderCreateState state, BreakableModelEntity owner)
      : this(state, owner, owner.Mesh.MeshData)
    {
    }

    public ModelRenderProxy(RenderCreateState state, DebrisCluster owner)
       : this(state, owner, owner.Mesh.MeshData)
    {
    }

    public ModelRenderProxy(RenderCreateState state, DynamicModelEntity owner)
       : this(state, owner, owner.Mesh.MeshData)
    {
    }

    public ModelRenderProxy(RenderCreateState state, BangerEntity owner)
       : this(state, owner, owner.Mesh.MeshData)
    {
    }
#endif

    public ModelRenderProxy(RenderCreateState state, ClothEntity owner)
        : this(state, owner, owner.Mesh.MeshData)
    {
    }

    public ModelRenderProxy(RenderCreateState state, VegetationTreeEntity owner)
        : this(state, owner, owner.Mesh.MeshData)
    {
    }

    public override void Render(D3D11.DeviceContext context, MeshRenderPath renderPath)
    {
        if (renderPath == MeshRenderPath.Deferred)
        {
            if (renderData.ShouldUseTexturedRendering)
            {
                // Textured rendering: each section uses its own material and texture (similar to MeshSetPlugin)
                ((MeshRenderBase)renderData.GetLod(lodIndex)).Render(context, renderPath);
            }
            else
            {
                // Non-textured rendering: uses LevelShader solid color (layer colour, yellow when selected)
                MeshRenderBase lod = renderData.GetLod(lodIndex) as MeshRenderBase;
                if (lod == null)
                    return;

                SolidColorParameterCache.Entry colorParams = GetSolidColorParameters();
                permutation.SetState(context, renderPath);
                context.PixelShader.SetShaderResources(1, colorParams.PixelTextures);
                context.PixelShader.SetConstantBuffer(2, colorParams.PixelParameters);

                lod.Render(context, renderPath);
            }
        }
    }

    /// <summary>
    /// The textured LOD chosen by the last GetInstance call, when it can be drawn instanced
    /// (see InstancedMeshBatcher); null when this proxy has to be drawn on its own.
    /// </summary>
    public MeshSetPlugin.Render.MeshRenderLod GetInstancedLod()
    {
        if (!renderData.ShouldUseTexturedRendering || !InstancedMeshBatcher.Supported)
            return null;
        MeshSetPlugin.Render.MeshRenderLod lod = renderData.TexturedMesh.GetLod(lodIndex);
        return (lod != null && lod.AllSectionsFallback) ? lod : null;
    }

    public override bool ShouldRender(float distToCamera, float screenSize)
    {
        return OwnerEntity.IsVisible && screenSize > renderData.CullScreenArea;
    }

    public override float MinScreenSize => renderData.CullScreenArea;

    public override MeshRenderInstance GetInstance(float distToCamera)
    {
        lodIndex = 0;
        for (lodIndex = 0; lodIndex < 5; lodIndex++)
        {
            if (distToCamera < renderData.LodDistances[lodIndex])
            {
                break;
            }
        }

        return new MeshRenderInstance() { RenderMesh = this, Transform = Transform };
    }

    public override void SetSelected(RenderCreateState state, bool newSelected)
    {
        isSelected = newSelected;
    }

    private SolidColorParameterCache.Entry GetSolidColorParameters()
    {
        if (isSelected)
            return SolidColorParameterCache.Get(createState, permutation, 1.0f, 1.0f, 0.0f, 1.0f);

        // 2026/01/19 Change marker: colour of the owning layer
        return SolidColorParameterCache.Get(createState, permutation,
            OwnerEntity.Owner?.Layer?.LayerColor.Red ?? 1.0f,
            OwnerEntity.Owner?.Layer?.LayerColor.Green ?? 1.0f,
            OwnerEntity.Owner?.Layer?.LayerColor.Blue ?? 1.0f,
            1.0f);
    }

    public override bool HitTest(Ray hitTestRay, out Vector3 hitLocation)
    {
        return renderData.HitTest(Transform, hitTestRay, out hitLocation);
    }

    /// <summary>The mesh's own bounds, placed by the proxy transform (follows rotation and scale).</summary>
    public override bool GetSelectionBounds(out BoundingBox localBounds, out Matrix world)
    {
        localBounds = renderData.Bounds;
        world = Transform;
        return true;
    }

    public override void RecalculateBoundingBox()
    {
        OrientedBoundingBox meshBbox = new OrientedBoundingBox(renderData.Bounds);
        meshBbox.Transform(Transform);

        BoundingBox = meshBbox.GetBoundingBox();
    }

    public override void Dispose()
    {
        // renderData is shared by every proxy using the same mesh and owned (and disposed) by
        // MeshAsset; the shader permutation and solid-colour parameters are shared by all
        // proxies. Nothing here is owned by this proxy.
        permutation = null;
    }
}
