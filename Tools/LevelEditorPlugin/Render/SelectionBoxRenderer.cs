using Frosty.Core.Viewport;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Entities;
using LevelEditorPlugin.Render.Proxies;
using SharpDX;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;

namespace LevelEditorPlugin.Render;

/// <summary>
/// Draws 3D box outlines around the selected objects in the level view: one box per selected
/// entity (oriented with the entity, so it follows rotation and scale), and with more than one
/// entity selected, one world-aligned box around the whole selection.
///
/// Lines have a fixed width in pixels (expanded to quads by the geometry shader in
/// Shaders/SelectionBox.hlsl). Each box is drawn twice: faint where it is hidden behind scene
/// geometry, solid where it is visible. Runs on the render thread, from
/// LevelEditorScreen.PostProcessEditorComposite, after the scene and sprites and before the gizmos.
/// </summary>
internal sealed class SelectionBoxRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector4 Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix ViewProj;
        public Vector4 ViewParams; // viewport width, height, line width (px), alpha
    }

    private const int VertexStride = 28;

    // per-object boxes: orange; the box around a multi-selection: light blue
    private static readonly Vector4 ObjectColor = new Vector4(1.0f, 0.62f, 0.1f, 1.0f);
    private static readonly Vector4 GroupColor = new Vector4(0.35f, 0.8f, 1.0f, 1.0f);
    private const float LineWidthPixels = 2.0f;
    private const float HiddenAlpha = 0.3f;

    // the 12 edges of a box whose corner i has x from bit 0, y from bit 1, z from bit 2
    private static readonly int[] EdgeIndices = BuildEdgeIndices();

    private D3D11.VertexShader vertexShader;
    private D3D11.GeometryShader geometryShader;
    private D3D11.PixelShader pixelShader;
    private D3D11.InputLayout inputLayout;
    private D3D11.DepthStencilState visibleDepthState;
    private D3D11.DepthStencilState hiddenDepthState;
    private D3D11.BlendState blendState;
    private D3D11.RasterizerState rasterizerState;
    private ConstantBuffer<Constants> constants;
    private D3D11.Buffer vertexBuffer;
    private int vertexCapacity;

    private bool initialized;
    private bool failed;

    private readonly List<Vertex> vertices = new List<Vertex>();
    private readonly Vector3[] corners = new Vector3[8];

    private static int[] BuildEdgeIndices()
    {
        List<int> edges = new List<int>();
        for (int i = 0; i < 8; i++)
        {
            foreach (int bit in new[] { 1, 2, 4 })
            {
                if ((i & bit) == 0)
                {
                    edges.Add(i);
                    edges.Add(i | bit);
                }
            }
        }
        return edges.ToArray();
    }

    /// <summary>
    /// Box around the given proxies of one selected entity, in world space (8 corners, corner i has
    /// min/max x, y, z from bits 0, 1, 2 in the entity's frame). Returns false when none of the
    /// proxies has a box (terrain).
    /// </summary>
    public static bool TryGetEntityBox(Entity entity, IEnumerable<RenderProxy> proxies, Vector3[] worldCorners)
    {
        // The box is aligned with the entity's own axes. Without a usable transform, world axes.
        Matrix frame = Matrix.Identity;
        if (entity is ISpatialEntity spatial)
        {
            Matrix m = spatial.GetTransform();
            if (Math.Abs(m.Determinant()) > 1e-12f)
                frame = m;
        }
        Matrix toFrame = Matrix.Invert(frame);

        Vector3 min = new Vector3(float.MaxValue);
        Vector3 max = new Vector3(float.MinValue);
        bool any = false;
        Vector3[] local = new Vector3[8];
        foreach (RenderProxy proxy in proxies)
        {
            if (!proxy.GetSelectionBounds(out BoundingBox bounds, out Matrix world))
                continue;
            Matrix proxyToFrame = world * toFrame;
            GetCorners(bounds.Minimum, bounds.Maximum, local);
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = Vector3.TransformCoordinate(local[i], proxyToFrame);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            any = true;
        }
        if (!any || float.IsNaN(min.X + min.Y + min.Z + max.X + max.Y + max.Z))
            return false;

        GetCorners(min, max, worldCorners);
        for (int i = 0; i < 8; i++)
            worldCorners[i] = Vector3.TransformCoordinate(worldCorners[i], frame);
        return true;
    }

    private static void GetCorners(Vector3 min, Vector3 max, Vector3[] result)
    {
        for (int i = 0; i < 8; i++)
        {
            result[i] = new Vector3(
                (i & 1) != 0 ? max.X : min.X,
                (i & 2) != 0 ? max.Y : min.Y,
                (i & 4) != 0 ? max.Z : min.Z);
        }
    }

    /// <summary>
    /// Draws the boxes. <paramref name="entityBoxes"/> holds 8 world-space corners per selected
    /// entity (see <see cref="TryGetEntityBox"/>). Leaves the vertex, pixel and geometry shader,
    /// their constant buffer slot 0, input layout, topology and rasterizer state changed; the caller
    /// restores what it needs.
    /// </summary>
    public void Draw(D3D11.DeviceContext context, D3D11.Device device, Frosty.Core.Viewport.DXUT.BaseCamera camera, int viewportWidth, int viewportHeight, List<Vector3[]> entityBoxes)
    {
        if (entityBoxes.Count == 0 || camera == null || failed)
            return;
        if (!initialized && !Initialize(device))
            return;

        // Positions relative to the camera, in render space (X mirrored, as for every mesh drawn in
        // the level view), like the scene's camera-relative view-projection.
        Vector3 eye = camera.GetEyePt();
        vertices.Clear();

        Vector3 groupMin = new Vector3(float.MaxValue);
        Vector3 groupMax = new Vector3(float.MinValue);
        foreach (Vector3[] box in entityBoxes)
        {
            AddBox(box, eye, ObjectColor);
            for (int i = 0; i < 8; i++)
            {
                groupMin = Vector3.Min(groupMin, box[i]);
                groupMax = Vector3.Max(groupMax, box[i]);
            }
        }

        if (entityBoxes.Count > 1)
        {
            // a little larger than the objects so it does not run along their box edges
            float pad = 0.05f + (groupMax - groupMin).Length() * 0.01f;
            GetCorners(groupMin - new Vector3(pad), groupMax + new Vector3(pad), corners);
            AddBox(corners, eye, GroupColor);
        }

        EnsureVertexBuffer(device, vertices.Count);
        context.MapSubresource(vertexBuffer, D3D11.MapMode.WriteDiscard, D3D11.MapFlags.None, out DataStream stream);
        stream.WriteRange(vertices.ToArray());
        context.UnmapSubresource(vertexBuffer, 0);

        Matrix viewProj = camera.GetCrViewProjMatrix();
        viewProj.Transpose();

        context.InputAssembler.InputLayout = inputLayout;
        context.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.LineList;
        context.InputAssembler.SetVertexBuffers(0, new D3D11.VertexBufferBinding(vertexBuffer, VertexStride, 0));
        context.InputAssembler.SetIndexBuffer(null, DXGI.Format.Unknown, 0);

        context.VertexShader.Set(vertexShader);
        context.GeometryShader.Set(geometryShader);
        context.PixelShader.Set(pixelShader);
        context.VertexShader.SetConstantBuffer(0, constants.Buffer);
        context.GeometryShader.SetConstantBuffer(0, constants.Buffer);
        context.PixelShader.SetConstantBuffer(0, constants.Buffer);

        context.Rasterizer.State = rasterizerState;
        context.OutputMerger.BlendState = blendState;

        // hidden parts first (faint), then the visible parts on top
        constants.UpdateData(context, new Constants { ViewProj = viewProj, ViewParams = new Vector4(viewportWidth, viewportHeight, LineWidthPixels, HiddenAlpha) });
        context.OutputMerger.DepthStencilState = hiddenDepthState;
        context.Draw(vertices.Count, 0);

        constants.UpdateData(context, new Constants { ViewProj = viewProj, ViewParams = new Vector4(viewportWidth, viewportHeight, LineWidthPixels, 1.0f) });
        context.OutputMerger.DepthStencilState = visibleDepthState;
        context.Draw(vertices.Count, 0);

        context.GeometryShader.Set(null);
        context.GeometryShader.SetConstantBuffer(0, null);
    }

    private void AddBox(Vector3[] box, Vector3 eye, Vector4 color)
    {
        for (int i = 0; i < EdgeIndices.Length; i++)
        {
            Vector3 p = box[EdgeIndices[i]];
            vertices.Add(new Vertex { Position = new Vector3(-p.X, p.Y, p.Z) - eye, Color = color });
        }
    }

    private void EnsureVertexBuffer(D3D11.Device device, int count)
    {
        if (vertexBuffer != null && count <= vertexCapacity)
            return;

        vertexBuffer?.Dispose();
        vertexCapacity = Math.Max(256, count * 2);
        vertexBuffer = new D3D11.Buffer(device, new D3D11.BufferDescription(vertexCapacity * VertexStride,
            D3D11.ResourceUsage.Dynamic, D3D11.BindFlags.VertexBuffer, D3D11.CpuAccessFlags.Write, D3D11.ResourceOptionFlags.None, 0));
    }

    private bool Initialize(D3D11.Device device)
    {
        initialized = true;
        try
        {
            byte[] vs = ReadShader("LevelEditorPlugin.Shaders.SelectionBox.vso");
            vertexShader = new D3D11.VertexShader(device, vs);
            geometryShader = new D3D11.GeometryShader(device, ReadShader("LevelEditorPlugin.Shaders.SelectionBox.gso"));
            pixelShader = new D3D11.PixelShader(device, ReadShader("LevelEditorPlugin.Shaders.SelectionBox.pso"));
            inputLayout = new D3D11.InputLayout(device, vs, new[]
            {
                new D3D11.InputElement("POSITION", 0, DXGI.Format.R32G32B32_Float, 0, 0),
                new D3D11.InputElement("COLOR", 0, DXGI.Format.R32G32B32A32_Float, 12, 0),
            });

            D3D11.DepthStencilStateDescription depth = new D3D11.DepthStencilStateDescription
            {
                IsDepthEnabled = true,
                DepthWriteMask = D3D11.DepthWriteMask.Zero,
                DepthComparison = D3D11.Comparison.LessEqual,
                IsStencilEnabled = false,
            };
            visibleDepthState = new D3D11.DepthStencilState(device, depth);
            depth.DepthComparison = D3D11.Comparison.Greater;
            hiddenDepthState = new D3D11.DepthStencilState(device, depth);

            D3D11.BlendStateDescription blend = D3D11.BlendStateDescription.Default();
            blend.RenderTarget[0].IsBlendEnabled = true;
            blend.RenderTarget[0].SourceBlend = D3D11.BlendOption.SourceAlpha;
            blend.RenderTarget[0].DestinationBlend = D3D11.BlendOption.InverseSourceAlpha;
            blend.RenderTarget[0].BlendOperation = D3D11.BlendOperation.Add;
            blend.RenderTarget[0].SourceAlphaBlend = D3D11.BlendOption.One;
            blend.RenderTarget[0].DestinationAlphaBlend = D3D11.BlendOption.InverseSourceAlpha;
            blend.RenderTarget[0].AlphaBlendOperation = D3D11.BlendOperation.Add;
            blend.RenderTarget[0].RenderTargetWriteMask = D3D11.ColorWriteMaskFlags.All;
            blendState = new D3D11.BlendState(device, blend);

            D3D11.RasterizerStateDescription raster = D3D11.RasterizerStateDescription.Default();
            raster.CullMode = D3D11.CullMode.None;
            rasterizerState = new D3D11.RasterizerState(device, raster);

            constants = new ConstantBuffer<Constants>(device, new Constants());
            LevelEditorLog.Info("Render", "Selection box outlines ready");
            return true;
        }
        catch (Exception ex)
        {
            failed = true;
            LevelEditorLog.Warning("Render", "Selection box outlines disabled: could not create the shaders or states", ex);
            DisposeResources();
            return false;
        }
    }

    private static byte[] ReadShader(string resourceName)
    {
        using (Stream stream = typeof(SelectionBoxRenderer).Assembly.GetManifestResourceStream(resourceName))
        {
            if (stream == null)
                throw new InvalidOperationException($"Embedded shader {resourceName} not found");
            using (MemoryStream ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }

    private void DisposeResources()
    {
        vertexShader?.Dispose(); vertexShader = null;
        geometryShader?.Dispose(); geometryShader = null;
        pixelShader?.Dispose(); pixelShader = null;
        inputLayout?.Dispose(); inputLayout = null;
        visibleDepthState?.Dispose(); visibleDepthState = null;
        hiddenDepthState?.Dispose(); hiddenDepthState = null;
        blendState?.Dispose(); blendState = null;
        rasterizerState?.Dispose(); rasterizerState = null;
        constants?.Dispose(); constants = null;
        vertexBuffer?.Dispose(); vertexBuffer = null;
        vertexCapacity = 0;
    }

    public void Dispose()
    {
        DisposeResources();
        initialized = false;
    }
}
