using Frosty.Core;
using FrostySdk;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace LevelEditorPlugin.AssetBrowser;

/// <summary>User settings for thumbnails (saved in Frosty's config).</summary>
public static class ThumbnailSettings
{
    private const string FolderKey = "LevelEditor_ThumbnailFolder";
    private const string StoredSizeKey = "LevelEditor_ThumbnailStoredSize";
    private const string KeyLightKey = "LevelEditor_ThumbnailKeyLight";
    private const string MaxCacheKey = "LevelEditor_ThumbnailMaxCacheMb";
    private const string OnDemandKey = "LevelEditor_ThumbnailOnDemand";
    private const string TileSizeKey = "LevelEditor_BrowserTileSize";

    /// <summary>Bump when the way thumbnails are drawn changes, so old pictures are not mixed with new ones.</summary>
    public const int RendererVersion = 1;
    public const int JpegQuality = 86;

    /// <summary>
    /// Where the cache file lives. Not inside the editor's own folder by default: each build is
    /// unpacked into a new folder, and the thumbnails should survive that.
    /// </summary>
    public static string Folder
    {
        get
        {
            string folder = Get(FolderKey, "");
            return string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder;
        }
        set => Set(FolderKey, value ?? "");
    }

    public static string DefaultFolder
    {
        get
        {
            try
            {
                string root = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
                if (!string.IsNullOrEmpty(root))
                    return Path.Combine(root, "FrostyLevelEditorCache", "Thumbnails");
            }
            catch (Exception)
            {
            }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LevelEditorThumbnails");
        }
    }

    /// <summary>Stored picture size in pixels: 128 or 256.</summary>
    public static int StoredSize
    {
        get => Get(StoredSizeKey, 256) <= 128 ? 128 : 256;
        set => Set(StoredSizeKey, value <= 128 ? 128 : 256);
    }

    /// <summary>A light from the upper left in addition to the ambient light probe.</summary>
    public static bool KeyLight
    {
        get => Get(KeyLightKey, true);
        set => Set(KeyLightKey, value);
    }

    /// <summary>The cache file never grows past this size.</summary>
    public static int MaxCacheMegabytes
    {
        get => Math.Max(16, Math.Min(Get(MaxCacheKey, 1024), 64 * 1024));
        set => Set(MaxCacheKey, Math.Max(16, Math.Min(value, 64 * 1024)));
    }

    /// <summary>Draw missing thumbnails for the assets shown in the browser, without a batch run.</summary>
    public static bool GenerateWhileBrowsing
    {
        get => Get(OnDemandKey, true);
        set => Set(OnDemandKey, value);
    }

    public static int TileSize
    {
        get => Math.Max(72, Math.Min(Get(TileSizeKey, 128), 256));
        set => Set(TileSizeKey, Math.Max(72, Math.Min(value, 256)));
    }

    /// <summary>File name for the current profile and render settings.</summary>
    public static string CacheFileName => $"{ProfilesLibrary.ProfileName}_r{RendererVersion}_{StoredSize}px_{(KeyLight ? "key" : "nokey")}.thumbs";

    private static T Get<T>(string key, T fallback)
    {
        try
        {
            return Config.Get(key, fallback);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static void Set(string key, object value)
    {
        try
        {
            Config.Add(key, value);
            Config.Save();
        }
        catch (Exception ex)
        {
            LevelEditorLog.Warning("Thumbnails", $"Could not save setting {key}", ex);
        }
    }
}

/// <summary>Progress of a batch run. A copy; read it as often as needed.</summary>
public struct ThumbnailBatchProgress
{
    public bool Active;
    public bool Paused;
    public int Total;
    public int Done;
    public int Rendered;
    public int NoMesh;
    public int Failed;
    public int AlreadyCached;
    public string Current;
    /// <summary>Why the run ended or cannot continue (cache full, ...); null otherwise.</summary>
    public string Message;
    public double AverageMilliseconds;
    public long AverageImageBytes;
}

/// <summary>
/// Thumbnails for the asset browser: looks them up in the cache file, keeps decoded pictures in
/// memory (bounded), has missing ones drawn, and runs batch generation.
/// All public members are for the UI thread unless noted.
/// </summary>
public static class ThumbnailService
{
    /// <summary>Decoded pictures kept in memory, by total pixel bytes.</summary>
    private const long MemoryBudgetBytes = 160L * 1024 * 1024;
    /// <summary>On-demand requests waiting to be drawn; older ones are dropped beyond this.</summary>
    private const int MaxPendingOnDemand = 48;

    private static readonly object sync = new object();
    private static ThumbnailCache cache;
    private static string cacheError;

    // decoded pictures, most recently used first
    private static readonly Dictionary<Guid, LinkedListNode<KeyValuePair<Guid, BitmapSource>>> loaded = new Dictionary<Guid, LinkedListNode<KeyValuePair<Guid, BitmapSource>>>();
    private static readonly LinkedList<KeyValuePair<Guid, BitmapSource>> loadedOrder = new LinkedList<KeyValuePair<Guid, BitmapSource>>();
    private static long loadedBytes;

    private static readonly HashSet<Guid> loading = new HashSet<Guid>();
    /// <summary>Results that could not be written to the cache file (no file, or it is full): kept for this session so they are not drawn over and over.</summary>
    private static readonly Dictionary<Guid, ThumbnailInfo> sessionInfo = new Dictionary<Guid, ThumbnailInfo>();
    private static readonly LinkedList<RenderJob> onDemand = new LinkedList<RenderJob>();
    private static readonly HashSet<Guid> onDemandAssets = new HashSet<Guid>();

    // batch
    private static List<EbxAssetEntry> batchList;
    private static int batchIndex;
    private static bool batchRegenerate;
    private static bool batchJobOutstanding;
    private static int batchGeneration;
    private static ThumbnailBatchProgress batch;
    private static double batchMillisecondsTotal;
    private static long batchImageBytesTotal;

    /// <summary>A thumbnail (or its "no mesh"/"failed" status) became available. Raised on the UI thread.</summary>
    public static event Action<Guid> ThumbnailReady;

    /// <summary>Why the cache file could not be opened, or null.</summary>
    public static string CacheError
    {
        get { lock (sync) return cacheError; }
    }

    /// <summary>True while a level view is open and visible, which is what draws thumbnails.</summary>
    public static bool CanRender => ThumbnailRenderer.Instance.IsDriven;

    /// <summary>The cache for the current settings; null when it cannot be opened (see <see cref="CacheError"/>).</summary>
    public static ThumbnailCache Cache
    {
        get
        {
            lock (sync)
            {
                if (cache != null || cacheError != null)
                    return cache;
                try
                {
                    string path = Path.Combine(ThumbnailSettings.Folder, ThumbnailSettings.CacheFileName);
                    cache = new ThumbnailCache(path, ThumbnailSettings.MaxCacheMegabytes * 1024L * 1024L);
                    RecoverFromCrash(cache);
                    LevelEditorLog.Info("Thumbnails", $"Cache {path}: {cache.Count} thumbnails, {cache.FileBytes / (1024.0 * 1024.0):0.0} MB");
                }
                catch (Exception ex)
                {
                    cacheError = ex.Message;
                    LevelEditorLog.Warning("Thumbnails", $"Could not open the thumbnail cache in {ThumbnailSettings.Folder}", ex);
                }
                return cache;
            }
        }
    }

    /// <summary>
    /// Call after changing a setting that decides which cache file is used (folder, stored size,
    /// key light) or its size limit. Stops a running batch.
    /// </summary>
    public static void ReloadSettings()
    {
        StopBatch("Stopped: thumbnail settings changed.");
        CancelOnDemand();
        lock (sync)
        {
            cache?.Dispose();
            cache = null;
            cacheError = null;
            sessionInfo.Clear();
            loaded.Clear();
            loadedOrder.Clear();
            loadedBytes = 0;
        }
    }

    /// <summary>The decoded picture if it is in memory; otherwise null (call <see cref="Request"/>).</summary>
    public static BitmapSource TryGetLoaded(Guid asset)
    {
        lock (sync)
        {
            if (!loaded.TryGetValue(asset, out LinkedListNode<KeyValuePair<Guid, BitmapSource>> node))
                return null;
            loadedOrder.Remove(node);
            loadedOrder.AddFirst(node);
            return node.Value.Value;
        }
    }

    /// <summary>What the cache knows about an asset (status, size); false when it has no record.</summary>
    public static bool TryGetInfo(Guid asset, out ThumbnailInfo info)
    {
        ThumbnailCache c = Cache;
        if (c != null && c.TryGetInfo(asset, out info))
            return true;
        lock (sync)
            return sessionInfo.TryGetValue(asset, out info);
    }

    /// <summary>
    /// Asks for an asset's thumbnail: loads it from the cache file, or has it drawn when it is
    /// not there (if <see cref="ThumbnailSettings.GenerateWhileBrowsing"/> is on).
    /// <see cref="ThumbnailReady"/> is raised when it is available.
    /// <paramref name="force"/> has it drawn even when drawing while browsing is switched off.
    /// </summary>
    public static void Request(EbxAssetEntry entry, bool force = false)
    {
        if (entry == null)
            return;
        Guid asset = entry.Guid;
        ThumbnailCache c = Cache;

        if (c != null && c.TryGetInfo(asset, out ThumbnailInfo info))
        {
            if (info.Status != ThumbnailStatus.Ok)
                return; // nothing to show; the tile shows the status
            lock (sync)
            {
                if (loaded.ContainsKey(asset) || !loading.Add(asset))
                    return;
            }
            Task.Run(() =>
            {
                BitmapSource image = null;
                try
                {
                    byte[] data = c.ReadImage(asset);
                    if (data != null)
                        image = ThumbnailImage.Decode(data);
                }
                catch (Exception ex)
                {
                    LevelEditorLog.Warning("Thumbnails", $"Reading the thumbnail of {entry.Name} failed", ex);
                }
                if (image == null)
                    c.Remove(asset); // unreadable record: draw it again on the next request
                lock (sync)
                {
                    loading.Remove(asset);
                    if (image != null)
                        Remember(asset, image);
                }
                if (image != null)
                    RaiseReady(asset);
            });
            return;
        }

        if (!force && !ThumbnailSettings.GenerateWhileBrowsing)
            return;

        RenderJob job;
        List<RenderJob> dropped = null;
        lock (sync)
        {
            if (sessionInfo.ContainsKey(asset) || !onDemandAssets.Add(asset))
                return;
            job = new RenderJob
            {
                Asset = entry,
                Shrink = ThumbnailRenderer.RenderSize / ThumbnailSettings.StoredSize,
                KeyLight = ThumbnailSettings.KeyLight,
            };
            job.Completed = result => OnRendered(job, result, false, 0);
            onDemand.AddLast(job);
            while (onDemand.Count > MaxPendingOnDemand)
            {
                // the oldest request is the one scrolled furthest away
                RenderJob oldest = onDemand.First.Value;
                onDemand.RemoveFirst();
                onDemandAssets.Remove(oldest.Asset.Guid);
                (dropped ??= new List<RenderJob>()).Add(oldest);
            }
        }
        if (dropped != null)
            ThumbnailRenderer.Instance.RemoveQueued(dropped.Contains);
        ThumbnailRenderer.Instance.Enqueue(job, true);
    }

    /// <summary>Drops all on-demand requests that have not been drawn yet.</summary>
    public static void CancelOnDemand()
    {
        lock (sync)
        {
            onDemand.Clear();
            onDemandAssets.Clear();
        }
        ThumbnailRenderer.Instance.RemoveQueued(j => !j.Preview && !IsBatchJob(j));
    }

    private static readonly HashSet<RenderJob> batchJobs = new HashSet<RenderJob>();

    private static bool IsBatchJob(RenderJob job)
    {
        lock (sync)
            return batchJobs.Contains(job);
    }

    /// <summary>Forgets an asset's thumbnail so it is drawn again on the next request.</summary>
    public static void Invalidate(Guid asset)
    {
        Cache?.Remove(asset);
        lock (sync)
        {
            sessionInfo.Remove(asset);
            if (loaded.TryGetValue(asset, out LinkedListNode<KeyValuePair<Guid, BitmapSource>> node))
            {
                loadedBytes -= PixelBytes(node.Value.Value);
                loadedOrder.Remove(node);
                loaded.Remove(asset);
            }
        }
    }

    /// <summary>
    /// Empties the cache: the current file and the files of other settings of this game profile.
    /// Returns the number of bytes freed.
    /// </summary>
    public static long ClearCache()
    {
        StopBatch("Stopped: the cache was cleared.");
        CancelOnDemand();
        long freed = 0;
        ThumbnailCache c = Cache;
        lock (sync)
        {
            sessionInfo.Clear();
            loaded.Clear();
            loadedOrder.Clear();
            loadedBytes = 0;
        }
        if (c != null)
        {
            freed += Math.Max(0, c.FileBytes - 8);
            c.Clear();
        }
        try
        {
            string folder = ThumbnailSettings.Folder;
            if (Directory.Exists(folder))
            {
                string current = c?.FilePath;
                foreach (string file in Directory.GetFiles(folder, ProfilesLibrary.ProfileName + "_r*.thumbs"))
                {
                    if (string.Equals(file, current, StringComparison.OrdinalIgnoreCase))
                        continue;
                    long length = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += length;
                }
            }
        }
        catch (Exception ex)
        {
            LevelEditorLog.Warning("Thumbnails", "Could not delete old thumbnail cache files", ex);
        }
        LevelEditorLog.Info("Thumbnails", $"Cache cleared, {freed / (1024.0 * 1024.0):0.0} MB freed");
        return freed;
    }

    /// <summary>Bytes used by all thumbnail cache files of this game profile.</summary>
    public static long DiskBytes()
    {
        long total = 0;
        try
        {
            string folder = ThumbnailSettings.Folder;
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.GetFiles(folder, ProfilesLibrary.ProfileName + "_r*.thumbs"))
                    total += new FileInfo(file).Length;
            }
        }
        catch (Exception)
        {
        }
        return total;
    }

    // --- preview ---------------------------------------------------------------------------

    /// <summary>
    /// Draws an asset from the given angle at full size. <paramref name="done"/> is called on the
    /// UI thread with the picture (null when there is nothing to show) and the result details.
    /// A newer request replaces one that has not started.
    /// </summary>
    internal static void RequestPreview(EbxAssetEntry entry, float yaw, float pitch, float zoom, Action<BitmapSource, RenderResult> done)
    {
        RenderJob job = new RenderJob
        {
            Asset = entry,
            Preview = true,
            Yaw = yaw,
            Pitch = pitch,
            Zoom = zoom,
            Shrink = 1,
            KeyLight = ThumbnailSettings.KeyLight,
        };
        job.Completed = result =>
        {
            BitmapSource image = null;
            if (result.Status == ThumbnailStatus.Ok && result.Pixels != null)
                image = ThumbnailImage.FromPixels(result.Pixels, result.Size);
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => done(image, result)));
        };
        ThumbnailRenderer.Instance.RequestPreview(job);
    }

    // --- batch -----------------------------------------------------------------------------

    public static ThumbnailBatchProgress BatchProgress
    {
        get
        {
            lock (sync)
            {
                ThumbnailBatchProgress copy = batch;
                copy.AverageMilliseconds = batch.Rendered > 0 ? batchMillisecondsTotal / batch.Rendered : 0;
                copy.AverageImageBytes = batch.Rendered > 0 ? batchImageBytesTotal / batch.Rendered : 0;
                return copy;
            }
        }
    }

    /// <summary>
    /// Starts drawing thumbnails for <paramref name="assets"/>, one after another, skipping those
    /// already in the cache unless <paramref name="regenerate"/> is set.
    /// </summary>
    public static void StartBatch(IEnumerable<EbxAssetEntry> assets, bool regenerate)
    {
        StopBatch(null);
        if (Cache == null)
        {
            lock (sync)
                batch = new ThumbnailBatchProgress { Message = "The cache file could not be opened: " + cacheError };
            return;
        }
        lock (sync)
        {
            batchList = new List<EbxAssetEntry>(assets);
            batchIndex = 0;
            batchRegenerate = regenerate;
            batchGeneration++;
            batchMillisecondsTotal = 0;
            batchImageBytesTotal = 0;
            batch = new ThumbnailBatchProgress { Active = true, Total = batchList.Count };
        }
        LevelEditorLog.Info("Thumbnails", $"Batch started: {batchList.Count} assets{(regenerate ? " (regenerating)" : "")}");
        SubmitNextBatchJob();
    }

    public static void PauseBatch()
    {
        lock (sync)
        {
            if (batch.Active)
                batch.Paused = true;
        }
    }

    public static void ResumeBatch()
    {
        lock (sync)
        {
            if (!batch.Active || !batch.Paused)
                return;
            batch.Paused = false;
        }
        SubmitNextBatchJob();
    }

    public static void StopBatch(string message)
    {
        lock (sync)
        {
            if (!batch.Active)
                return;
            batch.Active = false;
            batch.Paused = false;
            batch.Current = null;
            batch.Message = message ?? "Stopped.";
            batchGeneration++;
            batchJobOutstanding = false;
            batchList = null;
        }
        ThumbnailRenderer.Instance.RemoveQueued(IsBatchJob);
        lock (sync)
            batchJobs.Clear();
    }

    /// <summary>Queues the next asset of the batch that still needs a thumbnail. Any thread.</summary>
    private static void SubmitNextBatchJob()
    {
        ThumbnailCache c = Cache;
        RenderJob job = null;
        bool finished = false;
        lock (sync)
        {
            if (!batch.Active || batch.Paused || batchJobOutstanding || batchList == null)
                return;

            while (batchIndex < batchList.Count)
            {
                EbxAssetEntry entry = batchList[batchIndex++];
                if (!batchRegenerate && c != null && c.Contains(entry.Guid))
                {
                    batch.Done++;
                    batch.AlreadyCached++;
                    continue;
                }
                int generation = batchGeneration;
                job = new RenderJob
                {
                    Asset = entry,
                    Shrink = ThumbnailRenderer.RenderSize / ThumbnailSettings.StoredSize,
                    KeyLight = ThumbnailSettings.KeyLight,
                };
                RenderJob captured = job;
                job.Completed = result => OnRendered(captured, result, true, generation);
                batch.Current = entry.Filename;
                batchJobOutstanding = true;
                batchJobs.Add(job);
                break;
            }

            if (job == null)
            {
                batch.Active = false;
                batch.Current = null;
                batch.Message = $"Finished: {batch.Rendered} drawn, {batch.NoMesh} without a mesh, {batch.Failed} failed, {batch.AlreadyCached} were already cached.";
                batchList = null;
                finished = true;
            }
        }

        if (job != null)
            ThumbnailRenderer.Instance.Enqueue(job, false);
        else if (finished)
            LevelEditorLog.Info("Thumbnails", "Batch " + BatchProgress.Message);
    }

    /// <summary>
    /// A drawing finished (render thread). Encoding and the file write happen on a pool thread
    /// so the level view is not held up.
    /// </summary>
    private static void OnRendered(RenderJob job, RenderResult result, bool isBatch, int generation)
    {
        Task.Run(() =>
        {
            Guid asset = result.Asset;
            byte[] encoded = null;
            BitmapSource image = null;
            bool stored = false;
            try
            {
                if (result.Status == ThumbnailStatus.Ok && result.Pixels != null)
                {
                    encoded = ThumbnailImage.EncodeJpeg(result.Pixels, result.Size, ThumbnailSettings.JpegQuality);
                    if (!isBatch)
                        image = ThumbnailImage.FromPixels(result.Pixels, result.Size);
                }
                ThumbnailCache c = Cache;
                if (c != null)
                    stored = c.Put(asset, result.Status, result.Dimensions.X, result.Dimensions.Y, result.Dimensions.Z, result.MeshCount, encoded);
            }
            catch (Exception ex)
            {
                LevelEditorLog.Warning("Thumbnails", $"Storing the thumbnail of {job.Asset.Name} failed", ex);
            }

            bool currentBatch = false;
            lock (sync)
            {
                if (!stored)
                {
                    sessionInfo[asset] = new ThumbnailInfo
                    {
                        Status = result.Status,
                        SizeX = result.Dimensions.X,
                        SizeY = result.Dimensions.Y,
                        SizeZ = result.Dimensions.Z,
                        MeshCount = result.MeshCount,
                    };
                }
                if (isBatch)
                {
                    batchJobs.Remove(job);
                    currentBatch = generation == batchGeneration;
                    if (currentBatch)
                    {
                        batchJobOutstanding = false;
                        batch.Done++;
                        switch (result.Status)
                        {
                            case ThumbnailStatus.Ok:
                                batch.Rendered++;
                                batchMillisecondsTotal += result.Milliseconds;
                                batchImageBytesTotal += encoded?.Length ?? 0;
                                break;
                            case ThumbnailStatus.NoMesh:
                                batch.NoMesh++;
                                break;
                            default:
                                batch.Failed++;
                                break;
                        }
                    }
                }
                else
                {
                    onDemandAssets.Remove(asset);
                    onDemand.Remove(job);
                    if (image != null)
                        Remember(asset, image);
                }
            }

            RaiseReady(asset);

            if (currentBatch)
            {
                if (!stored)
                    StopBatch($"Stopped: the cache file is full (limit {ThumbnailSettings.MaxCacheMegabytes} MB) or could not be written. Raise the limit to continue.");
                else
                    SubmitNextBatchJob();
            }
        });
    }

    // --- assets that take the editor down ---------------------------------------------------

    /// <summary>
    /// Name of a small file that holds the asset being drawn right now. If the editor dies while
    /// drawing (a crash inside the graphics driver or a mesh loader cannot be caught), the file is
    /// still there at the next start and that asset is stored as failed, so a generation run
    /// does not stop at the same asset every time.
    /// </summary>
    private static string MarkerPath
    {
        get
        {
            ThumbnailCache c = Cache;
            return c == null ? null : c.FilePath + ".drawing";
        }
    }

    /// <summary>Render thread, just before an asset is loaded and drawn.</summary>
    internal static void NoteDrawing(EbxAssetEntry asset)
    {
        try
        {
            string path = MarkerPath;
            if (path != null)
                File.WriteAllText(path, asset.Guid.ToString() + "\n" + asset.Name);
        }
        catch (Exception)
        {
            // only a safety net; drawing goes on without it
        }
    }

    /// <summary>Render thread, after the asset was drawn or failed in a way that was caught.</summary>
    internal static void NoteDrawingDone()
    {
        try
        {
            string path = MarkerPath;
            if (path != null && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Stores the asset a previous session died on as failed. Called when the cache is opened.</summary>
    private static void RecoverFromCrash(ThumbnailCache c)
    {
        string path = c.FilePath + ".drawing";
        try
        {
            if (!File.Exists(path))
                return;
            string[] lines = File.ReadAllLines(path);
            File.Delete(path);
            if (lines.Length > 0 && Guid.TryParse(lines[0], out Guid asset))
            {
                c.Put(asset, ThumbnailStatus.Failed, 0, 0, 0, 0, null);
                LevelEditorLog.Warning("Thumbnails", $"The editor closed unexpectedly while drawing {(lines.Length > 1 ? lines[1] : asset.ToString())}. It is marked as failed and skipped from now on (use 'Redraw thumbnail' to try it again).");
            }
        }
        catch (Exception ex)
        {
            LevelEditorLog.Warning("Thumbnails", "Could not read the marker of the last drawn asset", ex);
        }
    }

    // --- helpers ---------------------------------------------------------------------------

    private static long PixelBytes(BitmapSource image) => (long)image.PixelWidth * image.PixelHeight * 4;

    /// <summary>Adds a decoded picture to the in-memory set and drops the least recently used ones over budget. Caller holds the lock.</summary>
    private static void Remember(Guid asset, BitmapSource image)
    {
        if (loaded.TryGetValue(asset, out LinkedListNode<KeyValuePair<Guid, BitmapSource>> existing))
        {
            loadedBytes -= PixelBytes(existing.Value.Value);
            loadedOrder.Remove(existing);
            loaded.Remove(asset);
        }
        LinkedListNode<KeyValuePair<Guid, BitmapSource>> node = loadedOrder.AddFirst(new KeyValuePair<Guid, BitmapSource>(asset, image));
        loaded[asset] = node;
        loadedBytes += PixelBytes(image);

        while (loadedBytes > MemoryBudgetBytes && loadedOrder.Count > 1)
        {
            LinkedListNode<KeyValuePair<Guid, BitmapSource>> last = loadedOrder.Last;
            loadedBytes -= PixelBytes(last.Value.Value);
            loaded.Remove(last.Value.Key);
            loadedOrder.RemoveLast();
        }
    }

    private static void RaiseReady(Guid asset)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => ThumbnailReady?.Invoke(asset)));
    }
}
