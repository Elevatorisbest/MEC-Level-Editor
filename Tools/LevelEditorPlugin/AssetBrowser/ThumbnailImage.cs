using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LevelEditorPlugin.AssetBrowser;

/// <summary>Turns rendered pixels into the stored thumbnail image and back.</summary>
public static class ThumbnailImage
{
    // The standard backdrop every thumbnail gets: a dark vertical gradient (top to bottom).
    private const int TopR = 0x45, TopG = 0x49, TopB = 0x4F;
    private const int BottomR = 0x23, BottomG = 0x25, BottomB = 0x28;

    /// <summary>
    /// Shrinks a square render by <paramref name="factor"/> (averaging, which smooths the edges)
    /// and puts it on the standard backdrop. <paramref name="coverage"/> has one byte per source
    /// pixel: 1 where the object was drawn, 0 for empty space; null means "everything is object".
    /// Returns opaque BGRA pixels of size (sourceSize / factor) squared.
    /// </summary>
    public static byte[] Compose(byte[] sourceBgra, byte[] coverage, int sourceSize, int factor, out int size)
    {
        if (factor < 1)
            factor = 1;
        size = sourceSize / factor;
        byte[] result = new byte[size * size * 4];
        int samples = factor * factor;

        for (int y = 0; y < size; y++)
        {
            // backdrop colour of this row
            int t = size > 1 ? (y * 256) / (size - 1) : 0;
            int backR = TopR + ((BottomR - TopR) * t) / 256;
            int backG = TopG + ((BottomG - TopG) * t) / 256;
            int backB = TopB + ((BottomB - TopB) * t) / 256;

            for (int x = 0; x < size; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, covered = 0;
                for (int sy = 0; sy < factor; sy++)
                {
                    int row = (y * factor + sy) * sourceSize;
                    for (int sx = 0; sx < factor; sx++)
                    {
                        int pixel = row + x * factor + sx;
                        if (coverage != null && coverage[pixel] == 0)
                            continue;
                        int o = pixel * 4;
                        sumB += sourceBgra[o];
                        sumG += sourceBgra[o + 1];
                        sumR += sourceBgra[o + 2];
                        covered++;
                    }
                }

                int empty = samples - covered;
                int d = (y * size + x) * 4;
                result[d] = (byte)((sumB + backB * empty) / samples);
                result[d + 1] = (byte)((sumG + backG * empty) / samples);
                result[d + 2] = (byte)((sumR + backR * empty) / samples);
                result[d + 3] = 255;
            }
        }
        return result;
    }

    /// <summary>JPEG bytes for opaque BGRA pixels.</summary>
    public static byte[] EncodeJpeg(byte[] bgra, int size, int quality)
    {
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr32, null, bgra, size * 4);
        JpegBitmapEncoder encoder = new JpegBitmapEncoder { QualityLevel = Math.Max(1, Math.Min(100, quality)) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (MemoryStream stream = new MemoryStream())
        {
            encoder.Save(stream);
            return stream.ToArray();
        }
    }

    /// <summary>A frozen image (usable from any thread) decoded from stored bytes.</summary>
    public static BitmapSource Decode(byte[] encoded)
    {
        using (MemoryStream stream = new MemoryStream(encoded))
        {
            BitmapImage image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }

    /// <summary>A frozen image from opaque BGRA pixels (the live preview).</summary>
    public static BitmapSource FromPixels(byte[] bgra, int size)
    {
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr32, null, bgra, size * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
