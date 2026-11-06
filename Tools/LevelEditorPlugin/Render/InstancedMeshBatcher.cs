using Frosty.Core;
using Frosty.Core.Attributes;
using Frosty.Core.Screens;
using Frosty.Core.Viewport;
using FrostySdk;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Render.Proxies;
using MeshSetPlugin.Render;
using SharpDX;
using System;
using System.Collections.Generic;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;

namespace LevelEditorPlugin.Render;

/// <summary>
/// Draws all visible copies of the same textured mesh LOD with one DrawIndexedInstanced call per
/// section, instead of one draw (and a full state setup) per object. The per-instance world
/// matrices go into one buffer per frame, read by the plugin's MEC fallback vertex shader
/// (Shaders/MecMeshFallbackVS.hlsl) at t4, with the group's start index in a constant buffer at b4.
///
/// Only used for Mirror's Edge Catalyst, in the deferred pass, for textured LODs whose sections
/// all use the fallback shader. Everything else is drawn one by one as before.
/// </summary>
internal sealed class InstancedMeshBatcher : IDisposable
{
    private struct InstanceConstants
    {
        public uint Base;
        public uint Instanced;
        public uint Padding0;
        public uint Padding1;
    }

    private static readonly Matrix MirrorX = Matrix.Scaling(-1, 1, 1);
    private static bool? supported;

    private readonly Dictionary<MeshRenderLod, List<Matrix>> groups = new Dictionary<MeshRenderLod, List<Matrix>>();
    private readonly List<MeshRenderLod> order = new List<MeshRenderLod>();
    private readonly Stack<List<Matrix>> freeLists = new Stack<List<Matrix>>();

    private D3D11.Buffer matrixBuffer;
    private D3D11.ShaderResourceView matrixView;
    private int capacity;
    private ConstantBuffer<InstanceConstants> constants;

    /// <summary>Objects and groups drawn by the last Flush (diagnostics).</summary>
    public int LastInstanceCount { get; private set; }
    public int LastGroupCount { get; private set; }

    /// <summary>
    /// True when the MEC vertex shader with instancing support is registered by this plugin.
    /// Without it, instanced draws would put every copy at the same place.
    /// </summary>
    public static bool Supported
    {
        get
        {
            if (supported == null)
            {
                bool ok = false;
                try
                {
                    ok = ProfilesLibrary.IsLoaded(ProfileVersion.MirrorsEdgeCatalyst)
                         && App.PluginManager.GetShader(ShaderType.VertexShader, "20160607") != null;
                }
                catch (Exception ex)
                {
                    LevelEditorLog.Warning("Render", "Could not check for the instancing vertex shader", ex);
                }
                supported = ok;
                LevelEditorLog.Info("Render", ok ? "Instanced drawing enabled (MEC fallback vertex shader from the level editor plugin)" : "Instanced drawing disabled (not MEC, or the plugin vertex shader is missing)");
            }
            return supported.Value;
        }
    }

    public void Begin()
    {
        foreach (MeshRenderLod lod in order)
        {
            List<Matrix> list = groups[lod];
            list.Clear();
            freeLists.Push(list);
        }
        groups.Clear();
        order.Clear();
    }

    /// <summary>Takes the instance into a batch if it can be instanced; returns false otherwise.</summary>
    public bool TryAdd(MeshRenderInstance instance)
    {
        if (!(instance.RenderMesh is ModelRenderProxy proxy))
            return false;

        MeshRenderLod lod = proxy.GetInstancedLod();
        if (lod == null)
            return false;

        if (!groups.TryGetValue(lod, out List<Matrix> list))
        {
            list = freeLists.Count > 0 ? freeLists.Pop() : new List<Matrix>();
            groups.Add(lod, list);
            order.Add(lod);
        }

        // Same world matrix as the per-object path: the shader reads rows of Transform * MirrorX
        // (the per-object path uploads MirrorX * transpose(Transform) into a column-major cbuffer).
        list.Add(instance.Transform * MirrorX);
        return true;
    }

    /// <summary>Uploads all collected matrices and draws every group.</summary>
    public void Flush(D3D11.DeviceContext context)
    {
        int total = 0;
        foreach (MeshRenderLod lod in order)
            total += groups[lod].Count;

        LastInstanceCount = total;
        LastGroupCount = order.Count;
        if (total == 0)
            return;

        EnsureCapacity(context.Device, total);

        context.MapSubresource(matrixBuffer, D3D11.MapMode.WriteDiscard, D3D11.MapFlags.None, out DataStream stream);
        foreach (MeshRenderLod lod in order)
        {
            List<Matrix> list = groups[lod];
            for (int i = 0; i < list.Count; i++)
                stream.Write(list[i]);
        }
        context.UnmapSubresource(matrixBuffer, 0);

        context.VertexShader.SetShaderResource(4, matrixView);

        uint start = 0;
        try
        {
            foreach (MeshRenderLod lod in order)
            {
                int count = groups[lod].Count;
                constants.UpdateData(context, new InstanceConstants { Base = start, Instanced = 1 });
                context.VertexShader.SetConstantBuffer(4, constants.Buffer);
                lod.RenderInstanced(context, count);
                start += (uint)count;
            }
        }
        finally
        {
            // unbound = zeros = "not instanced" for every later draw that uses the same shader
            context.VertexShader.SetConstantBuffer(4, null);
            context.VertexShader.SetShaderResource(4, null);
        }
    }

    private void EnsureCapacity(D3D11.Device device, int instances)
    {
        if (constants == null)
            constants = new ConstantBuffer<InstanceConstants>(device, new InstanceConstants());

        if (matrixBuffer != null && instances <= capacity)
            return;

        matrixView?.Dispose();
        matrixBuffer?.Dispose();

        capacity = Math.Max(4096, instances + instances / 2);
        int bytes = capacity * 64;
        matrixBuffer = new D3D11.Buffer(device, new D3D11.BufferDescription(bytes, D3D11.ResourceUsage.Dynamic,
            D3D11.BindFlags.ShaderResource, D3D11.CpuAccessFlags.Write, D3D11.ResourceOptionFlags.None, 0));
        matrixView = new D3D11.ShaderResourceView(device, matrixBuffer, new D3D11.ShaderResourceViewDescription
        {
            Format = DXGI.Format.R32G32B32A32_Float,
            Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new D3D11.ShaderResourceViewDescription.BufferResource { FirstElement = 0, ElementCount = capacity * 4 }
        });
        LevelEditorLog.Debug("Render", $"Instance matrix buffer sized for {capacity} instances");
    }

    public void Dispose()
    {
        matrixView?.Dispose();
        matrixBuffer?.Dispose();
        constants?.Dispose();
        matrixView = null;
        matrixBuffer = null;
        constants = null;
    }
}
