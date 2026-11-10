using Frosty.Core.Viewport;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.DXGI;
using System;
using D3D11 = SharpDX.Direct3D11;

namespace LevelEditorPlugin.AssetBrowser;

/// <summary>
/// A render target without a window: lets a Frosty render screen draw into a square texture
/// that is then copied to memory. Same buffers and formats as FrostyViewport.
/// Only used on the render thread.
/// </summary>
internal sealed class OffscreenViewport : IViewport, IDisposable
{
    public SwapChain SwapChain => null;
    public D3D11.Device Device { get; }
    public D3D11.DeviceContext Context => Device.ImmediateContext;
    public D3D11.Texture2D ColorBuffer { get; private set; }
    public D3D11.RenderTargetView ColorBufferRTV { get; private set; }
    public D3D11.Texture2D DepthBuffer { get; private set; }
    public D3D11.DepthStencilView DepthBufferDSV { get; private set; }
    public D3D11.ShaderResourceView DepthBufferSRV { get; private set; }

    public int ViewportWidth { get; }
    public int ViewportHeight { get; }
    public float LastFrameTime => 1.0f / 60.0f;
    public float TotalTime => 0.0f;

    public Screen Screen { get; set; }

    private D3D11.Texture2D colorStaging;
    private D3D11.Texture2D depthStaging;
    private bool depthReadbackBroken;

    public OffscreenViewport(D3D11.Device device, int size)
    {
        Device = device;
        ViewportWidth = size;
        ViewportHeight = size;

        ColorBuffer = new D3D11.Texture2D(device, new D3D11.Texture2DDescription
        {
            BindFlags = D3D11.BindFlags.RenderTarget | D3D11.BindFlags.ShaderResource,
            Format = Format.B8G8R8A8_UNorm,
            Width = size,
            Height = size,
            MipLevels = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = D3D11.ResourceUsage.Default,
            OptionFlags = D3D11.ResourceOptionFlags.None,
            CpuAccessFlags = D3D11.CpuAccessFlags.None,
            ArraySize = 1
        }) { DebugName = "ThumbnailColor" };
        ColorBufferRTV = new D3D11.RenderTargetView(device, ColorBuffer);

        DepthBuffer = new D3D11.Texture2D(device, new D3D11.Texture2DDescription
        {
            ArraySize = 1,
            BindFlags = D3D11.BindFlags.DepthStencil | D3D11.BindFlags.ShaderResource,
            CpuAccessFlags = D3D11.CpuAccessFlags.None,
            Format = Format.R24G8_Typeless,
            Width = size,
            Height = size,
            MipLevels = 1,
            OptionFlags = D3D11.ResourceOptionFlags.None,
            SampleDescription = new SampleDescription(1, 0),
            Usage = D3D11.ResourceUsage.Default
        }) { DebugName = "ThumbnailDepth" };
        DepthBufferDSV = new D3D11.DepthStencilView(device, DepthBuffer, new D3D11.DepthStencilViewDescription
        {
            Dimension = D3D11.DepthStencilViewDimension.Texture2D,
            Flags = D3D11.DepthStencilViewFlags.None,
            Format = Format.D24_UNorm_S8_UInt,
            Texture2D = new D3D11.DepthStencilViewDescription.Texture2DResource { MipSlice = 0 }
        });
        DepthBufferSRV = new D3D11.ShaderResourceView(device, DepthBuffer, new D3D11.ShaderResourceViewDescription
        {
            Dimension = ShaderResourceViewDimension.Texture2D,
            Format = Format.R24_UNorm_X8_Typeless,
            Texture2D = new D3D11.ShaderResourceViewDescription.Texture2DResource { MipLevels = 1, MostDetailedMip = 0 }
        });

        colorStaging = new D3D11.Texture2D(device, new D3D11.Texture2DDescription
        {
            Width = size,
            Height = size,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            Usage = D3D11.ResourceUsage.Staging,
            SampleDescription = new SampleDescription(1, 0),
            BindFlags = D3D11.BindFlags.None,
            CpuAccessFlags = D3D11.CpuAccessFlags.Read,
            OptionFlags = D3D11.ResourceOptionFlags.None
        });

        try
        {
            depthStaging = new D3D11.Texture2D(device, new D3D11.Texture2DDescription
            {
                Width = size,
                Height = size,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R24G8_Typeless,
                Usage = D3D11.ResourceUsage.Staging,
                SampleDescription = new SampleDescription(1, 0),
                BindFlags = D3D11.BindFlags.None,
                CpuAccessFlags = D3D11.CpuAccessFlags.Read,
                OptionFlags = D3D11.ResourceOptionFlags.None
            });
        }
        catch (SharpDXException ex)
        {
            depthReadbackBroken = true;
            Diagnostics.LevelEditorLog.Warning("Thumbnails", "Depth copy texture could not be created; thumbnails get a black backdrop", ex);
        }
    }

    public System.Windows.Point TranslateMousePointToScreen(System.Windows.Point point) => point;

    /// <summary>
    /// Copies the finished frame to memory. <paramref name="bgra"/> receives size*size*4 bytes.
    /// <paramref name="coverage"/> receives one byte per pixel: 1 where something was drawn, 0 where
    /// the depth buffer is still empty. Returns false when coverage could not be read from the
    /// depth buffer; it is then estimated from the colour (pure black counts as empty).
    /// </summary>
    public bool ReadBack(byte[] bgra, byte[] coverage)
    {
        int width = ViewportWidth;
        int height = ViewportHeight;

        Context.CopyResource(ColorBuffer, colorStaging);
        DataBox box = Context.MapSubresource(colorStaging, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
        try
        {
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(box.DataPointer + y * box.RowPitch, bgra, y * width * 4, width * 4);
        }
        finally
        {
            Context.UnmapSubresource(colorStaging, 0);
        }

        bool fromDepth = false;
        if (!depthReadbackBroken && depthStaging != null)
        {
            try
            {
                Context.CopyResource(DepthBuffer, depthStaging);
                DataBox depthBox = Context.MapSubresource(depthStaging, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
                try
                {
                    int[] row = new int[width];
                    for (int y = 0; y < height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(depthBox.DataPointer + y * depthBox.RowPitch, row, 0, width);
                        int o = y * width;
                        for (int x = 0; x < width; x++)
                            coverage[o + x] = (row[x] & 0x00FFFFFF) == 0x00FFFFFF ? (byte)0 : (byte)1;
                    }
                }
                finally
                {
                    Context.UnmapSubresource(depthStaging, 0);
                }
                fromDepth = true;
            }
            catch (SharpDXException ex)
            {
                depthReadbackBroken = true;
                Diagnostics.LevelEditorLog.Warning("Thumbnails", "Depth buffer could not be read back; empty space is estimated from the colour instead", ex);
            }
        }

        if (!fromDepth)
        {
            for (int i = 0; i < width * height; i++)
            {
                int o = i * 4;
                coverage[i] = (bgra[o] | bgra[o + 1] | bgra[o + 2]) == 0 ? (byte)0 : (byte)1;
            }
        }
        return fromDepth;
    }

    public void Dispose()
    {
        depthStaging?.Dispose();
        colorStaging?.Dispose();
        DepthBufferSRV?.Dispose();
        DepthBufferDSV?.Dispose();
        DepthBuffer?.Dispose();
        ColorBufferRTV?.Dispose();
        ColorBuffer?.Dispose();
        depthStaging = null;
        colorStaging = null;
    }
}
