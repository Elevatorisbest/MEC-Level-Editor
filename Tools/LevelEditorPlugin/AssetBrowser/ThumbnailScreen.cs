using Frosty.Core.Screens;
using Frosty.Core.Viewport;
using Frosty.Core.Viewport.DXUT;
using SharpDX;
using System.Collections.Generic;

namespace LevelEditorPlugin.AssetBrowser;

/// <summary>
/// The render screen that draws one asset for a thumbnail or the preview: the same deferred
/// renderer and material shader as the level view, with nothing else in the scene (no ground,
/// grid, bloom, anti-aliasing history), so the same asset always gives the same picture.
/// Only used on the render thread.
/// </summary>
internal sealed class ThumbnailScreen : DeferredRenderScreen2
{
    private List<MeshRenderInstance> sceneMeshes = new List<MeshRenderInstance>();
    private List<LightRenderInstance> sceneLights = new List<LightRenderInstance>();
    private BoundingBox sceneBounds;

    // what base.CreateBuffers initialised, so DisposeAll releases exactly that
    private bool txaaInitialised;
    private bool hbaoInitialised;
    private bool shadowsInitialised;
    private bool buffersCreated;

    public ThumbnailScreen()
    {
        GroundVisible = false;
        GridVisible = false;
        BloomEnabled = false;
    }

    /// <summary>Device plus this screen's own texture and shader libraries, for building meshes.</summary>
    public RenderCreateState CreateState => RenderCreateState;

    public override void CreateBuffers()
    {
        if (camera == null)
        {
            camera = new FirstPersonCamera();
            camera.SetViewParams(new Vector3(0, 1.5f, 4.0f), new Vector3(0, 0, 0));
        }

        base.CreateBuffers();

        txaaInitialised = TXAAEnabled;
        hbaoInitialised = HBAOEnabled;
        shadowsInitialised = ShadowsEnabled;
        buffersCreated = true;

        // Same as the level view: no shadows, no ambient occlusion. No temporal anti-aliasing
        // either, because it blends in earlier frames (of the previous asset).
        TXAAEnabled = false;
        HBAOEnabled = false;
        ShadowsEnabled = false;

        // The sun only lights anything through the shadow pass, which is off. Lighting is the
        // default light probe plus the lights passed to SetScene.
        SunIntensity = 0.0f;
        GroundVisible = false;
        GridVisible = false;
        BloomEnabled = false;
    }

    /// <summary>What to draw from now on.</summary>
    public void SetScene(List<MeshRenderInstance> meshes, List<LightRenderInstance> lights, BoundingBox bounds)
    {
        sceneMeshes = meshes ?? new List<MeshRenderInstance>();
        sceneLights = lights ?? new List<LightRenderInstance>();
        sceneBounds = bounds;
    }

    /// <summary>
    /// Camera position and target in the asset's own coordinates. (The renderer mirrors X, as the
    /// level view does, so both are mirrored here.)
    /// </summary>
    public void SetCamera(Vector3 eye, Vector3 target, float fieldOfView, float nearPlane, float farPlane)
    {
        Vector3 mirror = new Vector3(-1, 1, 1);
        camera.SetViewParams(eye * mirror, target * mirror);
        camera.SetProjParams(fieldOfView, Viewport.ViewportWidth / (float)Viewport.ViewportHeight, nearPlane, farPlane);
    }

    /// <summary>
    /// Forgets the brightness the automatic exposure had settled on, so an asset's picture does
    /// not depend on which asset was drawn before it.
    /// </summary>
    public void ResetExposure()
    {
        toneMapTextures[5]?.Clear(Viewport.Context, new Color4(0.00177f, 0, 0, 0));
    }

    /// <summary>
    /// Releases every texture loaded for meshes (call only when no mesh built with
    /// <see cref="CreateState"/> is alive) and loads the light probe again.
    /// </summary>
    public void FlushTextures()
    {
        textureLibrary.Dispose();
        DistantLightProbe = textureLibrary.LoadTextureAsset("Resources/Textures/DefaultLightProbe.dds", true);
    }

    public override List<MeshRenderInstance> CollectMeshInstances()
    {
        // the base class adds to the list it gets, so hand out a copy
        return new List<MeshRenderInstance>(sceneMeshes);
    }

    public override List<LightRenderInstance> CollectLightInstances()
    {
        return sceneLights;
    }

    protected override BoundingBox CalcWorldBoundingBox()
    {
        return sceneBounds;
    }

    /// <summary>Releases all GPU resources of this screen.</summary>
    public void DisposeAll()
    {
        sceneMeshes = new List<MeshRenderInstance>();
        sceneLights = new List<LightRenderInstance>();
        if (!buffersCreated)
            return;
        buffersCreated = false;

        // the base class only releases the libraries whose flag is set
        TXAAEnabled = txaaInitialised;
        HBAOEnabled = hbaoInitialised;
        ShadowsEnabled = shadowsInitialised;

        DisposeSizeDependentBuffers();
        DisposeBuffers();
    }
}
