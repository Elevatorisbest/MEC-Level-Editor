using System;
using System.Collections.Generic;
using System.IO;

namespace LevelEditorPlugin.AssetBrowser;

/// <summary>What is known about one asset's thumbnail.</summary>
public enum ThumbnailStatus : byte
{
    /// <summary>An image is stored.</summary>
    Ok = 0,
    /// <summary>The blueprint has no mesh that the editor can draw (logic-only prefab, effect, ...).</summary>
    NoMesh = 1,
    /// <summary>Loading or drawing failed; see the level editor log.</summary>
    Failed = 2,
}

/// <summary>One stored thumbnail record (without the image bytes).</summary>
public struct ThumbnailInfo
{
    public ThumbnailStatus Status;
    /// <summary>Size of the asset's bounding box in metres (X, Y, Z).</summary>
    public float SizeX, SizeY, SizeZ;
    public int MeshCount;
    internal long ImageOffset;
    internal int ImageLength;
}

/// <summary>
/// All thumbnails of one game profile and one set of render settings in a single file, so the
/// cache is one item on disk instead of tens of thousands of small files.
///
/// Layout: header (magic "MECT", format version), then records appended one after another:
/// asset GUID (16 bytes), status (1), size X/Y/Z (3 floats), mesh count (int), image length (int),
/// image bytes (JPEG). A later record for the same GUID replaces an earlier one. A record that
/// was cut off (crash while writing) is dropped when the file is opened.
///
/// Thread-safe. The file never grows past <see cref="MaxBytes"/>.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    private const uint Magic = 0x5443454D; // "MECT"
    private const int FormatVersion = 1;
    private const int HeaderSize = 8;
    private const int RecordHeaderSize = 16 + 1 + 12 + 4 + 4;
    /// <summary>Largest image accepted for one record; anything bigger means a corrupt length.</summary>
    private const int MaxImageBytes = 8 * 1024 * 1024;

    private readonly object sync = new object();
    private readonly Dictionary<Guid, ThumbnailInfo> index = new Dictionary<Guid, ThumbnailInfo>();
    private FileStream stream;
    private long wastedBytes;

    public string FilePath { get; }

    /// <summary>The file is not allowed to grow past this many bytes; <see cref="Put"/> then returns false.</summary>
    public long MaxBytes { get; set; }

    public ThumbnailCache(string filePath, long maxBytes)
    {
        FilePath = filePath;
        MaxBytes = maxBytes;
        Open();
    }

    public int Count
    {
        get { lock (sync) return index.Count; }
    }

    /// <summary>Size of the cache file on disk.</summary>
    public long FileBytes
    {
        get { lock (sync) return stream?.Length ?? 0; }
    }

    /// <summary>Bytes taken by records that were replaced later (freed by <see cref="Clear"/>).</summary>
    public long WastedBytes
    {
        get { lock (sync) return wastedBytes; }
    }

    public bool IsFull
    {
        get { lock (sync) return stream != null && stream.Length >= MaxBytes; }
    }

    private void Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        bool valid = false;
        if (stream.Length >= HeaderSize)
        {
            byte[] header = new byte[HeaderSize];
            stream.Position = 0;
            ReadExact(header, 0, HeaderSize);
            valid = BitConverter.ToUInt32(header, 0) == Magic && BitConverter.ToInt32(header, 4) == FormatVersion;
        }

        if (!valid)
        {
            // new file, or one from another format: start empty
            stream.SetLength(0);
            stream.Position = 0;
            stream.Write(BitConverter.GetBytes(Magic), 0, 4);
            stream.Write(BitConverter.GetBytes(FormatVersion), 0, 4);
            stream.Flush();
            return;
        }

        // read the record headers; stop at the first one that does not fit and cut the file there
        byte[] record = new byte[RecordHeaderSize];
        long position = HeaderSize;
        long length = stream.Length;
        while (position + RecordHeaderSize <= length)
        {
            stream.Position = position;
            ReadExact(record, 0, RecordHeaderSize);

            byte[] guidBytes = new byte[16];
            Buffer.BlockCopy(record, 0, guidBytes, 0, 16);
            byte status = record[16];
            int imageLength = BitConverter.ToInt32(record, 33);
            if (status > (byte)ThumbnailStatus.Failed || imageLength < 0 || imageLength > MaxImageBytes
                || position + RecordHeaderSize + imageLength > length)
                break;

            ThumbnailInfo info = new ThumbnailInfo
            {
                Status = (ThumbnailStatus)status,
                SizeX = BitConverter.ToSingle(record, 17),
                SizeY = BitConverter.ToSingle(record, 21),
                SizeZ = BitConverter.ToSingle(record, 25),
                MeshCount = BitConverter.ToInt32(record, 29),
                ImageOffset = position + RecordHeaderSize,
                ImageLength = imageLength,
            };
            Guid guid = new Guid(guidBytes);
            if (index.TryGetValue(guid, out ThumbnailInfo old))
                wastedBytes += RecordHeaderSize + old.ImageLength;
            index[guid] = info;
            position += RecordHeaderSize + imageLength;
        }

        if (position < length)
            stream.SetLength(position);
    }

    private void ReadExact(byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = stream.Read(buffer, offset, count);
            if (read <= 0)
                throw new EndOfStreamException();
            offset += read;
            count -= read;
        }
    }

    public bool Contains(Guid asset)
    {
        lock (sync)
            return index.ContainsKey(asset);
    }

    public bool TryGetInfo(Guid asset, out ThumbnailInfo info)
    {
        lock (sync)
            return index.TryGetValue(asset, out info);
    }

    /// <summary>The stored image (JPEG bytes), or null when there is none.</summary>
    public byte[] ReadImage(Guid asset)
    {
        lock (sync)
        {
            if (stream == null || !index.TryGetValue(asset, out ThumbnailInfo info) || info.ImageLength <= 0)
                return null;
            byte[] data = new byte[info.ImageLength];
            stream.Position = info.ImageOffset;
            ReadExact(data, 0, data.Length);
            return data;
        }
    }

    /// <summary>
    /// Stores a record. Returns false (and stores nothing) when the file would grow past
    /// <see cref="MaxBytes"/>.
    /// </summary>
    public bool Put(Guid asset, ThumbnailStatus status, float sizeX, float sizeY, float sizeZ, int meshCount, byte[] image)
    {
        int imageLength = image?.Length ?? 0;
        if (imageLength > MaxImageBytes)
            return false;

        lock (sync)
        {
            if (stream == null)
                return false;
            long position = stream.Length;
            if (position + RecordHeaderSize + imageLength > MaxBytes)
                return false;

            byte[] record = new byte[RecordHeaderSize + imageLength];
            Buffer.BlockCopy(asset.ToByteArray(), 0, record, 0, 16);
            record[16] = (byte)status;
            Buffer.BlockCopy(BitConverter.GetBytes(sizeX), 0, record, 17, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(sizeY), 0, record, 21, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(sizeZ), 0, record, 25, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(meshCount), 0, record, 29, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(imageLength), 0, record, 33, 4);
            if (imageLength > 0)
                Buffer.BlockCopy(image, 0, record, RecordHeaderSize, imageLength);

            stream.Position = position;
            stream.Write(record, 0, record.Length);
            stream.Flush();

            if (index.TryGetValue(asset, out ThumbnailInfo old))
                wastedBytes += RecordHeaderSize + old.ImageLength;
            index[asset] = new ThumbnailInfo
            {
                Status = status,
                SizeX = sizeX,
                SizeY = sizeY,
                SizeZ = sizeZ,
                MeshCount = meshCount,
                ImageOffset = position + RecordHeaderSize,
                ImageLength = imageLength,
            };
            return true;
        }
    }

    /// <summary>Forgets one asset (its bytes stay in the file until <see cref="Clear"/>).</summary>
    public void Remove(Guid asset)
    {
        lock (sync)
        {
            if (index.TryGetValue(asset, out ThumbnailInfo old))
            {
                wastedBytes += RecordHeaderSize + old.ImageLength;
                index.Remove(asset);
            }
        }
    }

    /// <summary>Empties the cache file.</summary>
    public void Clear()
    {
        lock (sync)
        {
            index.Clear();
            wastedBytes = 0;
            if (stream == null)
                return;
            stream.SetLength(HeaderSize);
            stream.Flush();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            stream?.Dispose();
            stream = null;
        }
    }
}
