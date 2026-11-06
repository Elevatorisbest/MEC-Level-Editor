using Frosty.Core.Screens;
using System;
using System.Collections;
using System.Reflection;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;

namespace LevelEditorPlugin.Diagnostics;

/// <summary>
/// Reads FrostyPlugin's TextureLibrary (private cache of loaded textures) to report how many
/// textures are resident and roughly how much GPU memory they use. Diagnostics only.
/// </summary>
public static class TextureLibraryStats
{
    public static string Summarize(TextureLibrary library)
    {
        if (library == null)
            return "no texture library";

        try
        {
            FieldInfo field = typeof(TextureLibrary).GetField("textures", BindingFlags.Instance | BindingFlags.NonPublic);
            if (!(field?.GetValue(library) is IDictionary textures))
                return "texture cache not readable (FrostyPlugin changed?)";

            int count = 0, compressed = 0, uncompressed = 0;
            long totalBytes = 0, largestBytes = 0;
            string largestName = "";
            int largestWidth = 0, largestHeight = 0;

            foreach (DictionaryEntry entry in textures)
            {
                count++;
                object tuple = entry.Value;
                object resource = tuple?.GetType().GetProperty("Item1")?.GetValue(tuple);
                if (!(resource is D3D11.Texture2D texture))
                    continue;

                D3D11.Texture2DDescription desc = texture.Description;
                long bytes = EstimateBytes(desc);
                totalBytes += bytes;
                if (DXGI.FormatHelper.IsCompressed(desc.Format)) compressed++;
                else uncompressed++;

                if (bytes > largestBytes)
                {
                    largestBytes = bytes;
                    largestName = entry.Key as string ?? "?";
                    largestWidth = desc.Width;
                    largestHeight = desc.Height;
                }
            }

            return $"{count} loaded ({compressed} block-compressed, {uncompressed} uncompressed), " +
                   $"~{totalBytes / (1024.0 * 1024.0):N0} MB estimated GPU memory; largest {largestWidth}x{largestHeight} " +
                   $"~{largestBytes / (1024.0 * 1024.0):N1} MB ({largestName})";
        }
        catch (Exception ex)
        {
            return $"could not read texture cache: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static long EstimateBytes(D3D11.Texture2DDescription desc)
    {
        bool isCompressed = DXGI.FormatHelper.IsCompressed(desc.Format);
        int bitsPerPixel = DXGI.FormatHelper.SizeOfInBits(desc.Format);
        if (bitsPerPixel <= 0)
            bitsPerPixel = isCompressed ? 8 : 32;

        long total = 0;
        int width = desc.Width, height = desc.Height;
        int mips = Math.Max(1, desc.MipLevels);
        for (int mip = 0; mip < mips; mip++)
        {
            long w = width, h = height;
            if (isCompressed)
            {
                // block-compressed textures are stored in 4x4 blocks
                w = Math.Max(4, (w + 3) / 4 * 4);
                h = Math.Max(4, (h + 3) / 4 * 4);
            }
            total += w * h * bitsPerPixel / 8;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        return total * Math.Max(1, desc.ArraySize);
    }
}
