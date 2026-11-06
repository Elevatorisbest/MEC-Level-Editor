using Frosty.Core.Viewport;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using D3D11 = SharpDX.Direct3D11;

namespace LevelEditorPlugin.Render.Proxies;

/// <summary>
/// Shared LevelShader parameters (pixel constant buffer + textures) for the solid-colour view
/// mode, one entry per colour and device. Previously every ModelRenderProxy created its own
/// constant buffer at load time, even in textured mode where it is never used: about 340,000
/// buffers for SP_MainCity.
/// </summary>
internal static class SolidColorParameterCache
{
    public sealed class Entry
    {
        public D3D11.Buffer PixelParameters;
        public D3D11.ShaderResourceView[] PixelTextures;
    }

    private static readonly ConditionalWeakTable<D3D11.Device, Dictionary<(float, float, float, float), Entry>> cache =
        new ConditionalWeakTable<D3D11.Device, Dictionary<(float, float, float, float), Entry>>();
    private static readonly object sync = new object();

    public static Entry Get(RenderCreateState state, ShaderPermutation permutation, float r, float g, float b, float a)
    {
        lock (sync)
        {
            Dictionary<(float, float, float, float), Entry> perDevice = cache.GetOrCreateValue(state.Device);
            (float, float, float, float) key = (r, g, b, a);
            if (perDevice.TryGetValue(key, out Entry entry))
                return entry;

            MeshMaterial material = new MeshMaterial();
            material.VectorParameters.Add(new FrostySdk.Ebx.VectorShaderParameter()
            {
                ParameterName = "Color",
                ParameterType = FrostySdk.Ebx.ShaderParameterType.ShaderParameterType_Vec4,
                Value = new FrostySdk.Ebx.Vec4() { x = r, y = g, z = b, w = a }
            });

            D3D11.Buffer pixelParameters = null;
            List<D3D11.ShaderResourceView> pixelTextures = new List<D3D11.ShaderResourceView>();
            permutation.AssignParameters(state, material, ref pixelParameters, ref pixelTextures);

            entry = new Entry { PixelParameters = pixelParameters, PixelTextures = pixelTextures.ToArray() };
            perDevice.Add(key, entry);
            return entry;
        }
    }
}
