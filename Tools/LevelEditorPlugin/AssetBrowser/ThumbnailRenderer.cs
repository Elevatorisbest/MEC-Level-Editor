using Frosty.Core;
using Frosty.Core.Screens;
using Frosty.Core.Viewport;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.Diagnostics;
using MeshSetPlugin.Render;
using SharpDX;
using SharpDX.Mathematics.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using D3D11 = SharpDX.Direct3D11;

namespace LevelEditorPlugin.AssetBrowser;

/// <summary>A request to draw one asset.</summary>
internal sealed class RenderJob
{
    public EbxAssetEntry Asset;
    /// <summary>Preview: full render size, keeps the asset loaded for the next request.</summary>
    public bool Preview;
    /// <summary>Camera angles in radians around the asset, and zoom (1 = whole asset fits).</summary>
    public float Yaw = ThumbnailRenderer.DefaultYaw;
    public float Pitch = ThumbnailRenderer.DefaultPitch;
    public float Zoom = 1.0f;
    /// <summary>The picture is shrunk by this factor (2 or 4 for thumbnails, 1 for the preview).</summary>
    public int Shrink = 2;
    public bool KeyLight = true;
    /// <summary>Called on the render thread when done. Must not block.</summary>
    public Action<RenderResult> Completed;
}

internal sealed class RenderResult
{
    public Guid Asset;
    public ThumbnailStatus Status;
    /// <summary>Opaque BGRA pixels, Size x Size; null unless Status is Ok.</summary>
    public byte[] Pixels;
    public int Size;
    /// <summary>Bounding box size in metres.</summary>
    public Vector3 Dimensions;
    public int MeshCount;
    /// <summary>The blueprint had more meshes than the limit; the picture shows only part of it.</summary>
    public bool Truncated;
    public string Error;
    public double Milliseconds;
}

/// <summary>
/// Draws assets into an off-screen target for thumbnails and the preview.
///
/// Frosty has one Direct3D device whose drawing context may only be used by one thread at a
/// time, and the level view's render thread uses it every frame. So all work here runs on that
/// thread: the level view calls <see cref="Pump"/> once per frame and at most one asset is drawn
/// per call. Nothing is drawn while no level view is open and visible.
/// </summary>
internal sealed class ThumbnailRenderer
{
    public static ThumbnailRenderer Instance { get; } = new ThumbnailRenderer();

    /// <summary>Pixels per side of the off-screen render.</summary>
    public const int RenderSize = 512;
    public const float DefaultYaw = 0.61f;   // 35 degrees to the side
    public const float DefaultPitch = 0.38f; // 22 degrees from above
    private const float FieldOfView = 0.5236f; // 30 degrees
    /// <summary>Meshes beyond these limits are left out of a picture (huge prefabs).</summary>
    private const int MaxParts = 256;
    private const int MaxUniqueMeshes = 64;
    /// <summary>Textures are released after this many assets (they are never shared with the level view).</summary>
    private const int FlushAfterScenes = 12;

    private sealed class Scene
    {
        public Guid Asset;
        public List<MeshRenderMesh> Meshes = new List<MeshRenderMesh>();
        public List<MeshRenderInstance> Instances = new List<MeshRenderInstance>();
        public BoundingBox Bounds;
        public bool Truncated;
        public string Problem;

        public void Dispose()
        {
            foreach (MeshRenderMesh mesh in Meshes)
            {
                try { mesh.Dispose(); }
                catch (Exception ex) { LevelEditorLog.Warning("Thumbnails", "Releasing a mesh failed", ex); }
            }
            Meshes.Clear();
            Instances.Clear();
        }
    }

    private readonly object queueLock = new object();
    private readonly LinkedList<RenderJob> jobs = new LinkedList<RenderJob>();
    private RenderJob previewJob;

    // Used by the render thread of whichever level view is visible. Guarded by renderLock because
    // a level view that closes releases these from the UI thread.
    private readonly object renderLock = new object();
    private object owner;
    private D3D11.Device device;
    private OffscreenViewport viewport;
    private ThumbnailScreen screen;
    private Scene previewScene;
    private Scene workingScene;
    private int scenesSinceFlush;
    private bool broken;
    private byte[] pixelBuffer;
    private byte[] coverageBuffer;
    private Guid lastRenderedAsset;

    private long lastPumpTicks;

    private ThumbnailRenderer()
    {
    }

    /// <summary>True while a level view has been calling <see cref="Pump"/> within the last two seconds.</summary>
    public bool IsDriven
    {
        get
        {
            long last = Interlocked.Read(ref lastPumpTicks);
            return last != 0 && (Stopwatch.GetTimestamp() - last) < 2 * Stopwatch.Frequency;
        }
    }

    public int QueuedCount
    {
        get { lock (queueLock) return jobs.Count; }
    }

    /// <summary>Adds a thumbnail job; <paramref name="urgent"/> ones (visible in the browser) go first.</summary>
    public void Enqueue(RenderJob job, bool urgent)
    {
        lock (queueLock)
        {
            if (urgent)
                jobs.AddFirst(job);
            else
                jobs.AddLast(job);
        }
    }

    /// <summary>The preview shows one picture, so a newer request replaces one not started yet.</summary>
    public void RequestPreview(RenderJob job)
    {
        lock (queueLock)
            previewJob = job;
    }

    /// <summary>Removes queued (not yet started) thumbnail jobs for which <paramref name="match"/> is true.</summary>
    public List<RenderJob> RemoveQueued(Predicate<RenderJob> match)
    {
        List<RenderJob> removed = new List<RenderJob>();
        lock (queueLock)
        {
            LinkedListNode<RenderJob> node = jobs.First;
            while (node != null)
            {
                LinkedListNode<RenderJob> next = node.Next;
                if (match(node.Value))
                {
                    removed.Add(node.Value);
                    jobs.Remove(node);
                }
                node = next;
            }
        }
        return removed;
    }

    /// <summary>
    /// Called by the level view once per frame, on its render thread, before it draws. Draws at
    /// most one queued asset.
    /// </summary>
    public void Pump(object levelView, D3D11.Device currentDevice)
    {
        Interlocked.Exchange(ref lastPumpTicks, Stopwatch.GetTimestamp());

        RenderJob job;
        lock (queueLock)
        {
            if (previewJob != null)
            {
                job = previewJob;
                previewJob = null;
            }
            else if (jobs.Count > 0)
            {
                job = jobs.First.Value;
                jobs.RemoveFirst();
            }
            else
            {
                return;
            }
        }

        RenderResult result = new RenderResult { Asset = job.Asset.Guid, Status = ThumbnailStatus.Failed };
        long start = Stopwatch.GetTimestamp();
        ThumbnailService.NoteDrawing(job.Asset);
        try
        {
            lock (renderLock)
            {
                owner = levelView;
                Process(job, result, currentDevice);
            }
        }
        catch (ThreadAbortException)
        {
            // not the asset's fault
            ThumbnailService.NoteDrawingDone();
            // Frosty stops the render thread by aborting it when the level view is hidden or
            // closed. Put the job back so it is done when a view returns.
            lock (queueLock)
            {
                if (job.Preview)
                    previewJob = previewJob ?? job;
                else
                    jobs.AddFirst(job);
            }
            throw;
        }
        catch (Exception ex)
        {
            result.Status = ThumbnailStatus.Failed;
            result.Error = ex.GetType().Name + ": " + ex.Message;
            LevelEditorLog.Warning("Thumbnails", $"Drawing {job.Asset.Name} failed", ex);
            DropWorkingScene();
        }
        ThumbnailService.NoteDrawingDone();
        result.Milliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        try
        {
            job.Completed?.Invoke(result);
        }
        catch (ThreadAbortException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LevelEditorLog.Warning("Thumbnails", "Handling a finished thumbnail failed", ex);
        }
    }

    /// <summary>
    /// Called by a level view when it releases its buffers. Releases everything on the GPU if
    /// that view was the one drawing thumbnails last; another view creates it again when needed.
    /// </summary>
    public void Shutdown(object levelView)
    {
        lock (renderLock)
        {
            if (owner != levelView)
                return;
            owner = null;
            Shutdown();
        }
    }

    private void Shutdown()
    {
        try
        {
            DropWorkingScene();
            previewScene?.Dispose();
            previewScene = null;
            screen?.DisposeAll();
            viewport?.Dispose();
        }
        catch (Exception ex) when (!(ex is ThreadAbortException))
        {
            LevelEditorLog.Warning("Thumbnails", "Releasing the thumbnail renderer failed", ex);
        }
        screen = null;
        viewport = null;
        device = null;
        scenesSinceFlush = 0;
        lastRenderedAsset = Guid.Empty;
    }

    private void DropWorkingScene()
    {
        if (workingScene != null && workingScene != previewScene)
            workingScene.Dispose();
        workingScene = null;
    }

    private void EnsureCreated(D3D11.Device currentDevice)
    {
        if (screen != null && device == currentDevice)
            return;
        if (screen != null)
            Shutdown();

        using (PerfProbe.Start("Thumbnails", "create off-screen renderer"))
        {
            device = currentDevice;
            viewport = new OffscreenViewport(currentDevice, RenderSize);
            screen = new ThumbnailScreen();
            screen.SetViewport(viewport);
            viewport.Screen = screen;
            // same order as FrostyViewport
            screen.CreateSizeDependentBuffers();
            screen.CreateBuffers();
            pixelBuffer = new byte[RenderSize * RenderSize * 4];
            coverageBuffer = new byte[RenderSize * RenderSize];
        }
    }

    private void Process(RenderJob job, RenderResult result, D3D11.Device currentDevice)
    {
        if (broken)
        {
            result.Error = "The thumbnail renderer could not be created (see the log).";
            return;
        }

        try
        {
            EnsureCreated(currentDevice);
        }
        catch (Exception ex) when (!(ex is ThreadAbortException))
        {
            broken = true;
            LevelEditorLog.Error("Thumbnails", "Creating the off-screen renderer failed; thumbnails are off for this session", ex);
            try { Shutdown(); } catch (Exception) { }
            result.Error = "The thumbnail renderer could not be created: " + ex.Message;
            return;
        }

        // an aborted earlier job may have left a scene behind
        DropWorkingScene();

        D3D11.DeviceContext context = viewport.Context;
        RawViewportF[] savedViewports = context.Rasterizer.GetViewports<RawViewportF>();
        bool savedTxaa = GFSDK_TXAA.TxaaEnabled;
        GFSDK_TXAA.TxaaEnabled = false;
        try
        {
            Scene scene;
            if (previewScene != null && previewScene.Asset == job.Asset.Guid)
            {
                scene = previewScene;
            }
            else
            {
                if (job.Preview)
                {
                    previewScene?.Dispose();
                    previewScene = null;
                    scenesSinceFlush++;
                }
                MaybeFlushTextures();
                scene = LoadScene(job.Asset);
                if (job.Preview)
                    previewScene = scene;
            }
            workingScene = scene;

            result.MeshCount = scene.Meshes.Count;
            result.Truncated = scene.Truncated;
            if (scene.Instances.Count == 0)
            {
                result.Status = scene.Problem == null ? ThumbnailStatus.NoMesh : ThumbnailStatus.Failed;
                result.Error = scene.Problem;
                return;
            }

            Vector3 extent = scene.Bounds.Maximum - scene.Bounds.Minimum;
            result.Dimensions = extent;

            // camera
            Vector3 center = scene.Bounds.Minimum + extent * 0.5f;
            float pitch = MathUtil.Clamp(job.Pitch, -1.5f, 1.5f);
            Vector3 toCamera = new Vector3(
                (float)(Math.Sin(job.Yaw) * Math.Cos(pitch)),
                (float)Math.Sin(pitch),
                (float)(Math.Cos(job.Yaw) * Math.Cos(pitch)));
            Vector3 right = Vector3.Cross(Vector3.UnitY, toCamera);
            if (right.LengthSquared() < 1e-6f)
                right = Vector3.UnitX;
            right.Normalize();
            Vector3 up = Vector3.Cross(toCamera, right);
            up.Normalize();

            // distance at which all eight corners of the bounding box are inside the picture
            float tanHalf = (float)Math.Tan(FieldOfView * 0.5f);
            float distance = 0.0f;
            float radius = Math.Max(extent.Length() * 0.5f, 0.01f);
            foreach (Vector3 corner in scene.Bounds.GetCorners())
            {
                Vector3 v = corner - center;
                float side = Math.Max(Math.Abs(Vector3.Dot(v, right)), Math.Abs(Vector3.Dot(v, up)));
                distance = Math.Max(distance, Vector3.Dot(v, toCamera) + side / tanHalf);
            }
            distance = Math.Max(distance, radius * 0.5f) * 1.12f / MathUtil.Clamp(job.Zoom, 0.2f, 8.0f);
            Vector3 eye = center + toCamera * distance;
            float nearPlane = Math.Max(0.005f, (distance - radius) * 0.5f);
            float farPlane = distance + radius * 4.0f + 1.0f;

            // one light from the upper left of the camera on top of the light probe; its strength
            // grows with distance squared so every asset is lit the same whatever its size
            List<LightRenderInstance> lights = new List<LightRenderInstance>();
            if (job.KeyLight)
            {
                Vector3 lightDirection = toCamera * 0.8f + up * 0.9f - right * 0.7f;
                lightDirection.Normalize();
                float lightDistance = Math.Max(radius * 4.0f, 0.5f);
                const float illuminance = 500.0f;
                lights.Add(new LightRenderInstance
                {
                    Type = LightRenderType.Sphere,
                    Transform = Matrix.Translation(center + lightDirection * lightDistance),
                    Color = new Vector3(1.0f, 0.98f, 0.95f),
                    Intensity = illuminance * 4.0f * (float)Math.PI * lightDistance * lightDistance,
                    AttenuationRadius = lightDistance * 20.0f,
                    SphereRadius = 0.0f,
                    LightId = 0
                });
            }

            screen.SetScene(scene.Instances, lights, scene.Bounds);
            screen.SetCamera(eye, center, FieldOfView, nearPlane, farPlane);

            // The exposure adapts over frames: start from the same state for every asset and draw
            // a few frames with a long time step so it has settled. A preview of the asset that
            // was drawn last (dragging to rotate) is already settled.
            int frames = 2;
            if (lastRenderedAsset != job.Asset.Guid)
            {
                screen.ResetExposure();
                frames = 4;
            }
            context.Rasterizer.SetViewport(new SharpDX.Viewport(0, 0, RenderSize, RenderSize));
            for (int i = 0; i < frames; i++)
            {
                screen.Update(30.0);
                screen.Render();
            }
            lastRenderedAsset = job.Asset.Guid;

            viewport.ReadBack(pixelBuffer, coverageBuffer);
            result.Pixels = ThumbnailImage.Compose(pixelBuffer, coverageBuffer, RenderSize, job.Preview ? 1 : job.Shrink, out int size);
            result.Size = size;
            result.Status = ThumbnailStatus.Ok;
        }
        finally
        {
            // leave the device as the level view expects it
            screen?.SetScene(null, null, new BoundingBox());
            context.OutputMerger.SetRenderTargets((D3D11.DepthStencilView)null, (D3D11.RenderTargetView)null);
            context.Rasterizer.SetViewports(savedViewports);
            GFSDK_TXAA.TxaaEnabled = savedTxaa;

            if (workingScene != null && workingScene != previewScene)
            {
                workingScene.Dispose();
                scenesSinceFlush++;
            }
            workingScene = null;
        }
    }

    /// <summary>
    /// Mesh textures stay in the screen's texture library after a mesh is released. Empty it
    /// every few assets so memory stays flat during a long run. Only possible when no mesh is
    /// alive, so the preview's asset is released too (it reloads on its next request).
    /// </summary>
    private void MaybeFlushTextures()
    {
        if (scenesSinceFlush < FlushAfterScenes)
            return;
        if (previewScene != null)
        {
            previewScene.Dispose();
            previewScene = null;
        }
        screen.FlushTextures();
        scenesSinceFlush = 0;
    }

    private Scene LoadScene(EbxAssetEntry blueprint)
    {
        Scene scene = new Scene { Asset = blueprint.Guid };
        List<MeshPart> parts = BlueprintMeshResolver.Resolve(blueprint, MaxParts, out bool truncated);
        scene.Truncated = truncated;
        if (parts.Count == 0)
            return scene;

        RenderCreateState state = screen.CreateState;
        Dictionary<Guid, MeshRenderMesh> loaded = new Dictionary<Guid, MeshRenderMesh>();
        HashSet<Guid> failed = new HashSet<Guid>();
        bool first = true;

        foreach (MeshPart part in parts)
        {
            Guid meshGuid = part.Mesh.Guid;
            if (failed.Contains(meshGuid))
                continue;
            if (!loaded.TryGetValue(meshGuid, out MeshRenderMesh mesh))
            {
                if (loaded.Count >= MaxUniqueMeshes)
                {
                    scene.Truncated = true;
                    continue;
                }
                try
                {
                    mesh = LoadMesh(part.Mesh, state);
                }
                catch (Exception ex) when (!(ex is ThreadAbortException))
                {
                    failed.Add(meshGuid);
                    scene.Problem = $"{part.Mesh.Filename}: {ex.GetType().Name}: {ex.Message}";
                    LevelEditorLog.Warning("Thumbnails", $"Mesh {part.Mesh.Name} of {blueprint.Name} could not be loaded", ex);
                    continue;
                }
                if (mesh == null)
                {
                    failed.Add(meshGuid);
                    continue;
                }
                loaded.Add(meshGuid, mesh);
                scene.Meshes.Add(mesh);
            }

            MeshRenderLod lod = mesh.GetLod(0);
            if (lod == null)
                continue;
            scene.Instances.Add(new MeshRenderInstance { RenderMesh = lod, Transform = part.Transform });

            foreach (Vector3 corner in mesh.Bounds.GetCorners())
            {
                Vector3 p = Vector3.TransformCoordinate(corner, part.Transform);
                if (first)
                {
                    scene.Bounds = new BoundingBox(p, p);
                    first = false;
                }
                else
                {
                    scene.Bounds = new BoundingBox(Vector3.Min(scene.Bounds.Minimum, p), Vector3.Max(scene.Bounds.Maximum, p));
                }
            }
        }

        if (scene.Instances.Count > 0)
            scene.Problem = null; // a partly loaded prefab still gives a picture
        return scene;
    }

    private static MeshRenderMesh LoadMesh(EbxAssetEntry meshEntry, RenderCreateState state)
    {
        EbxAsset meshEbx = App.AssetManager.GetEbx(meshEntry);
        if (!(meshEbx?.RootObject is FrostySdk.Ebx.MeshAsset meshData))
            return null;
        ResAssetEntry resEntry = App.AssetManager.GetResEntry(meshData.MeshSetResource);
        if (resEntry == null)
            return null;
        MeshSetPlugin.Resources.MeshSet meshSet = App.AssetManager.GetResAs<MeshSetPlugin.Resources.MeshSet>(resEntry);
        if (meshSet == null || meshSet.Lods.Count == 0)
            return null;
        MeshMaterialCollection materials = new MeshMaterialCollection(meshEbx, new FrostySdk.Ebx.PointerRef());
        return new MeshRenderMesh(state, meshSet, materials, new MeshRenderSkeleton());
    }
}
