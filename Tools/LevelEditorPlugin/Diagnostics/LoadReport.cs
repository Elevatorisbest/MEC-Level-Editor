using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace LevelEditorPlugin.Diagnostics;

/// <summary>
/// Collects statistics while a level / prefab loads in the level editor and writes a readable
/// report to the session log when loading finishes.
///
/// Loading has two phases:
///   1. Entity phase  ("Loading ..." window): ebx is read and the entity graph is built.
///   2. Render phase  ("Preparing scene" window): render proxies, meshes and textures are created
///      on the render thread.
///
/// All static recording methods are no-ops when no load is in progress, and never throw.
/// </summary>
public sealed class LoadReport
{
    private const int MaxListedItems = 200;

    public static LoadReport Current { get; private set; }
    public static LoadReport Last { get; private set; }

    /// <summary>Raised (on an arbitrary thread) when a report has been completed.</summary>
    public static event Action<LoadReport> ReportCompleted;

    private sealed class SubWorldInfo
    {
        public string Name;
        public double Milliseconds;
        public int ChildCount;
        public int Depth;
    }

    private sealed class Memory
    {
        public long WorkingSet;
        public long PrivateBytes;
        public long Managed;
        public int Gen0, Gen1, Gen2;

        public static Memory Capture()
        {
            Memory m = new Memory();
            try
            {
                using (Process p = Process.GetCurrentProcess())
                {
                    m.WorkingSet = p.WorkingSet64;
                    m.PrivateBytes = p.PrivateMemorySize64;
                }
                m.Managed = GC.GetTotalMemory(false);
                m.Gen0 = GC.CollectionCount(0);
                m.Gen1 = GC.CollectionCount(1);
                m.Gen2 = GC.CollectionCount(2);
            }
            catch
            {
            }
            return m;
        }

        public override string ToString() =>
            $"working set {Mb(WorkingSet)}, private {Mb(PrivateBytes)}, managed heap {Mb(Managed)} (GCs so far: gen0 {Gen0}, gen1 {Gen1}, gen2 {Gen2})";
    }

    private readonly object sync = new object();
    private bool entityDone;
    private bool renderDone;
    private bool finished;
    private long entityEndedAt;
    private long renderStartedAt;

    public string AssetName { get; }
    public string AssetType { get; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public bool Failed { get; private set; }

    private readonly Stopwatch entityTimer = Stopwatch.StartNew();
    private readonly Stopwatch renderTimer = new Stopwatch();
    private readonly Memory memoryAtStart = Memory.Capture();
    private Memory memoryAfterEntities;
    private Memory memoryAfterRender;

    private readonly Dictionary<string, int> entityTypes = new Dictionary<string, int>();
    private readonly Dictionary<string, int> unboundDataTypes = new Dictionary<string, int>();
    private readonly Dictionary<string, int> proxyTypes = new Dictionary<string, int>();
    private readonly List<SubWorldInfo> subWorlds = new List<SubWorldInfo>();
    private readonly List<string> missingAssets = new List<string>();
    private readonly List<string> emissiveOnlyMeshes = new List<string>();
    private int lightCardMeshes;
    private readonly List<string> uiMeshes = new List<string>();
    private readonly Dictionary<string, int> havokHeaders = new Dictionary<string, int>();
    private int havokScaledInstances, havokInstances;

    /// <summary>Records one Havok static compound instance's scale class and unparsed header words.</summary>
    public void HavokInstanceHeader(SharpDX.Vector3 scale, uint w1, uint w2, uint w3, uint w4, uint w5)
    {
        bool scaled = Math.Abs(scale.X - 1) > 1e-4f || Math.Abs(scale.Y - 1) > 1e-4f || Math.Abs(scale.Z - 1) > 1e-4f;
        // grouped by the flags word only (the 4th word varies per instance)
        string key = $"{(scaled ? "scaled  " : "unscaled")} flags word {w1:X8}";
        lock (sync)
        {
            havokInstances++;
            if (scaled) havokScaledInstances++;
            havokHeaders.TryGetValue(key, out int n);
            havokHeaders[key] = n + 1;
        }
    }

    /// <summary>A mesh whose materials all use UI/ shaders (hidden by default).</summary>
    public void UiMesh(string meshName)
    {
        lock (sync)
            uiMeshes.Add(meshName);
    }
    private int missingAssetCount;
    private readonly List<string> nameLookups = new List<string>();
    private readonly Dictionary<string, int> meshMaterialFailures = new Dictionary<string, int>();
    private readonly List<string> exceptions = new List<string>();
    private int exceptionCount;

    private int totalEntities;
    private int subWorldDepth;

    private int staticGroups;
    private int staticMembers;
    private int staticInstancesFromTransforms;
    private int staticInstancesFromHavok;
    private int staticInstancesWithoutTransform;
    private int staticGroupsWithHavokData;
    private int objectVariationsResolved;
    private int objectVariationsUnresolved;

    private int meshesLoaded;
    private int meshesTextured;
    private int meshesUntextured;
    private double meshLoadMilliseconds;
    private double meshUntexturedMilliseconds;
    private double meshTexturedMilliseconds;
    private string textureSummary;
    private readonly List<string> timings = new List<string>();
    private readonly Dictionary<string, long[]> exclusiveTimes = new Dictionary<string, long[]>();

    // material diagnostics (which texture parameter names MEC materials use)
    private int materialsSeen, materialsWithoutDiffuse, materialsWithoutTextures;
    private readonly Dictionary<string, int> textureParamNames = new Dictionary<string, int>();
    private readonly Dictionary<string, int> vectorParamNames = new Dictionary<string, int>();
    private readonly Dictionary<string, int> vectorParamNamesNoDiffuse = new Dictionary<string, int>();
    private readonly Dictionary<string, int[]> materialShaders = new Dictionary<string, int[]>();
    private readonly List<string> noDiffuseExamples = new List<string>();
    private string gpuMemorySummary;
    private int totalProxies;

    private LoadReport(string assetName, string assetType)
    {
        AssetName = assetName;
        AssetType = assetType;
    }

    #region -- Lifecycle --

    public static LoadReport Begin(string assetName, string assetType)
    {
        LevelEditorLog.EnsureStarted();

        if (Current != null)
            LevelEditorLog.Warning("Load", $"Starting load of {assetName} while load of {Current.AssetName} had not finished; previous report discarded");

        Current = new LoadReport(assetName, assetType);
        LevelEditorLog.Info("Load", $"Loading {assetType} {assetName}. Memory: {Current.memoryAtStart}");
        return Current;
    }

    /// <summary>
    /// Called by the editor when the entity graph has been built (before the scene is handed to
    /// the renderer), or with <paramref name="succeeded"/> = false when loading threw.
    /// </summary>
    public void EndEntityPhase(bool succeeded)
    {
        bool finishNow;
        lock (sync)
        {
            if (entityDone)
                return;
            entityDone = true;
            entityTimer.Stop();
            entityEndedAt = Stopwatch.GetTimestamp();
            memoryAfterEntities = Memory.Capture();
            if (!succeeded)
                Failed = true;
            finishNow = !succeeded || renderDone;
        }

        LevelEditorLog.Info("Load", $"Entity phase finished in {Seconds(entityTimer.Elapsed.TotalMilliseconds)} ({totalEntities} entities, {subWorlds.Count} sub-worlds). Memory: {memoryAfterEntities}");

        if (finishNow)
            Finish();
    }

    /// <summary>Called on the render thread when the scene's render task starts.</summary>
    private List<KeyValuePair<string, int>> fallbackShaderStats;

    public void RenderStarting()
    {
        lock (sync)
        {
            if (renderTimer.IsRunning || renderDone)
                return;
            renderTimer.Start();
            renderStartedAt = Stopwatch.GetTimestamp();
        }
        Frosty.Core.Viewport.MecFallbackStats.Reset();
        LevelEditorLog.Info("Load", "Render phase started (creating render proxies, meshes and textures)");
    }

    /// <summary>Called on the render thread when the scene's render task finished.</summary>
    public void RenderFinished(IEnumerable<object> allProxies, Exception error = null)
    {
        bool finishNow;
        lock (sync)
        {
            if (renderDone)
                return;
            renderDone = true;
            renderTimer.Stop();
            memoryAfterRender = Memory.Capture();
            fallbackShaderStats = Frosty.Core.Viewport.MecFallbackStats.Snapshot();
            if (error != null)
                Failed = true;
            try
            {
                foreach (object proxy in allProxies)
                {
                    totalProxies++;
                    Increment(proxyTypes, proxy.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                LevelEditorLog.Warning("Load", "Could not count render proxies", ex);
            }
            finishNow = entityDone;
        }

        LevelEditorLog.Info("Load", $"Render phase finished in {Seconds(renderTimer.Elapsed.TotalMilliseconds)} ({totalProxies} render proxies). Memory: {memoryAfterRender}");

        if (finishNow)
            Finish();
    }

    private void Finish()
    {
        lock (sync)
        {
            if (finished)
                return;
            finished = true;
        }

        if (Current == this)
            Current = null;
        Last = this;

        string text = Format();
        LevelEditorLog.WriteBlock($"Load report: {AssetName}", text);

        try
        {
            Frosty.Core.App.Logger?.Log("[LevelEditor] Load report for {0} written to {1}", AssetName, LevelEditorLog.CurrentLogFile);
        }
        catch
        {
        }

        CompactAfterLoad();

        try
        {
            ReportCompleted?.Invoke(this);
        }
        catch (Exception ex)
        {
            LevelEditorLog.Warning("Load", "ReportCompleted handler failed", ex);
        }
    }

    /// <summary>
    /// Loading allocates many large temporary arrays (mesh chunks, texture data) on the large
    /// object heap, which .NET does not compact by default, so freed memory can stay committed.
    /// Run one compacting collection after a load and log how much it gave back.
    /// </summary>
    private void CompactAfterLoad()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Memory before = Memory.Capture();
                Stopwatch timer = Stopwatch.StartNew();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                timer.Stop();
                Memory after = Memory.Capture();
                LevelEditorLog.Info("Memory", $"Post-load compacting GC took {timer.Elapsed.TotalMilliseconds:N0} ms. " +
                    $"Before: {before}. After: {after}. Private bytes freed: {Mb(before.PrivateBytes - after.PrivateBytes)}");
            }
            catch (Exception ex)
            {
                LevelEditorLog.Warning("Memory", "Post-load compacting GC failed", ex);
            }
        });
    }

    #endregion

    #region -- Recording (static, no-op when not loading) --

    public static void EntityCreated(object entity)
    {
        LoadReport r = Current;
        if (r == null || entity == null)
            return;
        lock (r.sync)
        {
            r.totalEntities++;
            Increment(r.entityTypes, entity.GetType().Name);
        }
    }

    public static void UnboundDataType(Type dataType)
    {
        LoadReport r = Current;
        if (r == null || dataType == null)
            return;
        lock (r.sync)
            Increment(r.unboundDataTypes, dataType.Name);
    }

    /// <summary>A mesh resource whose materials only have emissive textures; light cards are hidden by default.</summary>
    public void EmissiveOnlyMesh(string meshName, bool lightCard)
    {
        lock (sync)
        {
            emissiveOnlyMeshes.Add((lightCard ? "hidden   " : "visible  ") + meshName);
            if (lightCard)
                lightCardMeshes++;
        }
    }

    public static void MissingAsset(string description)
    {
        LoadReport r = Current;
        if (r == null)
            return;
        lock (r.sync)
        {
            r.missingAssetCount++;
            if (r.missingAssets.Count < MaxListedItems)
                r.missingAssets.Add(description);
        }
    }

    /// <summary>Records whether an asset looked up by a naming convention exists (e.g. network registry).</summary>
    public static void NameLookup(string kind, string assetName, bool found)
    {
        LoadReport r = Current;
        if (r == null)
            return;
        lock (r.sync)
        {
            if (r.nameLookups.Count < MaxListedItems * 2)
                r.nameLookups.Add($"{(found ? "found  " : "MISSING")} {kind}: {assetName}");
        }
    }

    /// <summary>Returns a token to pass to <see cref="SubWorldFinished"/>; tracks nesting depth.</summary>
    public static long SubWorldStarting()
    {
        LoadReport r = Current;
        if (r == null)
            return 0;
        lock (r.sync)
            r.subWorldDepth++;
        return Stopwatch.GetTimestamp();
    }

    public static void SubWorldFinished(long token, string name, int childCount)
    {
        LoadReport r = Current;
        if (r == null || token == 0)
            return;
        double ms = (Stopwatch.GetTimestamp() - token) * 1000.0 / Stopwatch.Frequency;
        lock (r.sync)
        {
            r.subWorldDepth--;
            r.subWorlds.Add(new SubWorldInfo { Name = name, Milliseconds = ms, ChildCount = childCount, Depth = r.subWorldDepth });
        }
    }

    public static void StaticModelGroup(int members, int fromTransforms, int fromHavok, int withoutTransform, bool hasHavokData, int variationsResolved, int variationsUnresolved)
    {
        LoadReport r = Current;
        if (r == null)
            return;
        lock (r.sync)
        {
            r.staticGroups++;
            r.staticMembers += members;
            r.staticInstancesFromTransforms += fromTransforms;
            r.staticInstancesFromHavok += fromHavok;
            r.staticInstancesWithoutTransform += withoutTransform;
            if (hasHavokData)
                r.staticGroupsWithHavokData++;
            r.objectVariationsResolved += variationsResolved;
            r.objectVariationsUnresolved += variationsUnresolved;
        }
    }

    public static void MeshLoaded(double milliseconds, bool textured, double untexturedMs, double texturedMs)
    {
        LoadReport r = Current;
        if (r == null)
            return;
        lock (r.sync)
        {
            r.meshesLoaded++;
            r.meshLoadMilliseconds += milliseconds;
            r.meshUntexturedMilliseconds += untexturedMs;
            r.meshTexturedMilliseconds += texturedMs;
            if (textured) r.meshesTextured++;
            else r.meshesUntextured++;
        }
    }

    /// <summary>Texture library summary, set at the end of the render phase.</summary>
    public void SetTextureSummary(string summary)
    {
        lock (sync)
            textureSummary = summary;
    }

    /// <summary>
    /// Adds a named timing. If the report was already written (e.g. UI setup that finishes after
    /// the render phase), the timing goes to the log as a separate line instead.
    /// </summary>
    public void AddTiming(string name, double milliseconds)
    {
        string line = $"{name}: {Seconds(milliseconds)}";
        bool alreadyWritten;
        lock (sync)
        {
            alreadyWritten = finished;
            if (!alreadyWritten)
                timings.Add(line);
        }
        if (alreadyWritten)
            LevelEditorLog.Info("Load", $"{AssetName}: {line} (after the load report was written)");
    }

    /// <summary>Adds exclusive time (Stopwatch ticks) for a category; see <see cref="LoadTiming"/>.</summary>
    public static void AddExclusiveTime(string category, long ticks)
    {
        LoadReport r = Current;
        if (r == null)
            return;
        lock (r.sync)
        {
            if (!r.exclusiveTimes.TryGetValue(category, out long[] entry))
            {
                entry = new long[2];
                r.exclusiveTimes.Add(category, entry);
            }
            entry[0]++;
            entry[1] += ticks;
        }
    }

    public void SetGpuMemorySummary(string summary)
    {
        lock (sync)
            gpuMemorySummary = summary;
    }

    /// <summary>
    /// Records one mesh material. "Diffuse" follows Frosty's fallback shader for Mirror's Edge
    /// Catalyst, which only uses a colour texture whose parameter name starts with "Diffuse".
    /// </summary>
    public static void MaterialSeen(string meshName, string shaderName, List<string> textureParams, List<string> vectorParams)
    {
        LoadReport r = Current;
        if (r == null)
            return;

        bool hasDiffuse = textureParams.Exists(n => n != null && n.StartsWith("Diffuse"));
        lock (r.sync)
        {
            r.materialsSeen++;
            if (textureParams.Count == 0) r.materialsWithoutTextures++;
            if (!hasDiffuse) r.materialsWithoutDiffuse++;

            foreach (string name in textureParams)
                Increment(r.textureParamNames, name ?? "(null)");
            foreach (string name in vectorParams)
                Increment(hasDiffuse ? r.vectorParamNames : r.vectorParamNamesNoDiffuse, name ?? "(null)");

            string shaderKey = shaderName ?? "(no shader)";
            if (!r.materialShaders.TryGetValue(shaderKey, out int[] counts))
            {
                counts = new int[2];
                r.materialShaders.Add(shaderKey, counts);
            }
            counts[0]++;
            if (!hasDiffuse) counts[1]++;

            if (!hasDiffuse && textureParams.Count > 0 && r.noDiffuseExamples.Count < 40)
                r.noDiffuseExamples.Add($"{meshName} | shader {shaderKey} | textures: {string.Join(", ", textureParams)}");
        }
    }

    public static void MeshMaterialFailure(string stage, Exception ex)
    {
        LoadReport r = Current;
        string key = $"{stage}: {ex?.GetType().Name}: {ex?.Message}";
        if (r == null)
        {
            LevelEditorLog.Warning("Mesh", key);
            return;
        }
        bool first;
        lock (r.sync)
        {
            first = !r.meshMaterialFailures.ContainsKey(key);
            Increment(r.meshMaterialFailures, key);
        }
        if (first)
            LevelEditorLog.Warning("Mesh", $"Textured mesh path failed ({stage}); falling back to untextured. Further identical failures are only counted.", ex);
    }

    public static void ExceptionDuringLoad(string where, Exception ex)
    {
        // Nested entity creation rethrows through several levels; log the innermost only.
        const string loggedKey = "LevelEditorPlugin.Logged";
        try
        {
            if (ex != null)
            {
                if (ex.Data.Contains(loggedKey))
                    return;
                ex.Data[loggedKey] = true;
            }
        }
        catch
        {
            // some exception types have read-only Data; just log
        }

        LevelEditorLog.Error("Load", $"Exception in {where}", ex);
        LoadReport r = Current;
        if (r == null)
            return;
        lock (r.sync)
        {
            r.exceptionCount++;
            if (r.exceptions.Count < 50)
                r.exceptions.Add($"{where}: {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    #endregion

    #region -- Formatting --

    public string Format()
    {
        lock (sync)
        {
            StringBuilder sb = new StringBuilder();
            double entityMs = entityTimer.Elapsed.TotalMilliseconds;
            double renderMs = renderTimer.Elapsed.TotalMilliseconds;

            sb.AppendLine($"Asset:            {AssetName} ({AssetType})");
            sb.AppendLine($"Started:          {StartedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Result:           {(Failed ? "FAILED (see exceptions below / log)" : "loaded")}");
            sb.AppendLine($"Entity phase:     {Seconds(entityMs)}   (\"Loading ...\" window: read ebx, build entities)");
            sb.AppendLine($"Render phase:     {Seconds(renderMs)}   (\"Preparing scene\" window: meshes, textures, proxies)");
            double gapMs = (entityEndedAt != 0 && renderStartedAt > entityEndedAt)
                ? (renderStartedAt - entityEndedAt) * 1000.0 / Stopwatch.Frequency
                : 0.0;
            sb.AppendLine($"Between phases:   {Seconds(gapMs)}   (UI thread busy: world setup and panels, see \"Other timings\")");
            sb.AppendLine($"Total:            {Seconds(entityMs + gapMs + renderMs)}");
            sb.AppendLine();
            sb.AppendLine("Memory");
            sb.AppendLine($"  at start:         {memoryAtStart}");
            sb.AppendLine($"  after entities:   {(memoryAfterEntities?.ToString() ?? "n/a")}");
            sb.AppendLine($"  after render:     {(memoryAfterRender?.ToString() ?? "n/a")}");
            sb.AppendLine();

            sb.AppendLine($"GPU memory (DXGI): {gpuMemorySummary ?? "n/a"}");
            sb.AppendLine();

            if (exclusiveTimes.Count > 0)
            {
                long totalTicks = 0;
                foreach (long[] e in exclusiveTimes.Values)
                    totalTicks += e[1];
                sb.AppendLine($"Where load time went (exclusive time, nested work excluded; measured total {Seconds(totalTicks * 1000.0 / Stopwatch.Frequency)}, top 30)");
                foreach (KeyValuePair<string, long[]> kv in exclusiveTimes.OrderByDescending(k => k.Value[1]).Take(30))
                {
                    double ms = kv.Value[1] * 1000.0 / Stopwatch.Frequency;
                    sb.AppendLine($"  {Seconds(ms),10}  {kv.Value[0],8} x  {(ms * 1000.0 / Math.Max(1, kv.Value[0])),8:N1} us each  {kv.Key}");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"Entities: {totalEntities} total, {entityTypes.Count} types");
            AppendCounts(sb, entityTypes, 40);
            sb.AppendLine();

            int unboundTotal = unboundDataTypes.Values.Sum();
            sb.AppendLine($"Ebx object types with no editor entity class (not shown/edited in the viewport): {unboundTotal} objects, {unboundDataTypes.Count} types");
            AppendCounts(sb, unboundDataTypes, 60);
            sb.AppendLine();

            sb.AppendLine($"Sub-worlds: {subWorlds.Count} (times include nested sub-worlds; slowest 25)");
            foreach (SubWorldInfo sw in subWorlds.OrderByDescending(s => s.Milliseconds).Take(25))
                sb.AppendLine($"  {Seconds(sw.Milliseconds),10}  depth {sw.Depth}  {sw.ChildCount,6} objects  {sw.Name}");
            sb.AppendLine();

            sb.AppendLine("Static model groups");
            sb.AppendLine($"  groups: {staticGroups} ({staticGroupsWithHavokData} with Havok physics data), members: {staticMembers}");
            sb.AppendLine($"  instance transforms from InstanceTransforms: {staticInstancesFromTransforms}");
            sb.AppendLine($"  instance transforms from Havok data:         {staticInstancesFromHavok}");
            sb.AppendLine($"  instances with no transform source (placed at group origin): {staticInstancesWithoutTransform}");
            sb.AppendLine($"  object variations resolved: {objectVariationsResolved}, unresolved: {objectVariationsUnresolved}");
            sb.AppendLine();

            sb.AppendLine("Meshes");
            sb.AppendLine($"  mesh resources loaded: {meshesLoaded} ({meshesTextured} with textured path, {meshesUntextured} untextured only), total mesh load time {Seconds(meshLoadMilliseconds)}");
            sb.AppendLine($"    building untextured LODs at load (only meshes without a textured path; others on demand in solid-colour mode): {Seconds(meshUntexturedMilliseconds)}");
            sb.AppendLine($"    building textured mesh (geometry + material textures): {Seconds(meshTexturedMilliseconds)}");
            sb.AppendLine($"    other (reading the mesh resource, materials): {Seconds(Math.Max(0, meshLoadMilliseconds - meshUntexturedMilliseconds - meshTexturedMilliseconds))}");
            sb.AppendLine($"  textures: {textureSummary ?? "n/a"}");
            if (meshMaterialFailures.Count > 0)
            {
                sb.AppendLine("  textured path failures (count, reason):");
                AppendCounts(sb, meshMaterialFailures, 20);
            }
            sb.AppendLine($"  render proxies: {totalProxies}");
            AppendCounts(sb, proxyTypes, 20);
            sb.AppendLine();

            if (fallbackShaderStats != null && fallbackShaderStats.Count > 0)
            {
                sb.AppendLine("Fallback shader setup (mesh sections; a mesh used by several objects counts once)");
                foreach (KeyValuePair<string, int> kv in fallbackShaderStats)
                    sb.AppendLine($"  {kv.Value,9}  {kv.Key}");
                sb.AppendLine();
            }

            sb.AppendLine("Materials (texture parameter names; the MEC fallback shader only takes a colour texture named Diffuse*)");
            sb.AppendLine($"  materials: {materialsSeen}, without a Diffuse* texture: {materialsWithoutDiffuse}, without any texture: {materialsWithoutTextures}");
            sb.AppendLine("  texture parameter names (count):");
            AppendCounts(sb, textureParamNames, 60);
            sb.AppendLine("  vector parameter names in materials WITHOUT a Diffuse* texture (count):");
            AppendCounts(sb, vectorParamNamesNoDiffuse, 40);
            sb.AppendLine("  vector parameter names in materials with a Diffuse* texture (count):");
            AppendCounts(sb, vectorParamNames, 25);
            sb.AppendLine("  shaders (materials, of which without Diffuse*), top 40:");
            foreach (KeyValuePair<string, int[]> kv in materialShaders.OrderByDescending(k => k.Value[0]).Take(40))
                sb.AppendLine($"  {kv.Value[0],8} {kv.Value[1],8}  {kv.Key}");
            sb.AppendLine("  examples of materials with textures but no Diffuse*:");
            foreach (string e in noDiffuseExamples)
                sb.AppendLine("    " + e);
            sb.AppendLine();

            sb.AppendLine($"Emissive-only meshes: {emissiveOnlyMeshes.Count}, of which {lightCardMeshes} flat light cards (hidden unless View > Light cards is on); the rest are drawn in their emissive texture's colour");
            foreach (string m in emissiveOnlyMeshes.OrderBy(x => x).Take(60))
                sb.AppendLine("  " + m);
            if (emissiveOnlyMeshes.Count > 60)
                sb.AppendLine($"  ... {emissiveOnlyMeshes.Count - 60} more");
            sb.AppendLine();

            sb.AppendLine($"UI meshes (all materials use UI/ shaders; hidden unless View > UI meshes is on): {uiMeshes.Count}");
            foreach (string m in uiMeshes.OrderBy(x => x).Take(40))
                sb.AppendLine("  " + m);
            if (uiMeshes.Count > 40)
                sb.AppendLine($"  ... {uiMeshes.Count - 40} more");
            sb.AppendLine();

            sb.AppendLine($"Havok static compound instances: {havokInstances}, of which with scale other than 1: {havokScaledInstances}");
            sb.AppendLine("  instance flags word (w of the first transform row; 0x3F000000 | flags, HAS_SCALE = 0x08):");
            foreach (KeyValuePair<string, int> kv in havokHeaders.OrderBy(k => k.Key).Take(40))
                sb.AppendLine($"  {kv.Value,8}  {kv.Key}");
            sb.AppendLine();

            sb.AppendLine($"Missing assets (referenced but not found): {missingAssetCount}");
            foreach (string m in missingAssets.Take(50))
                sb.AppendLine("  " + m);
            if (missingAssetCount > 50)
                sb.AppendLine($"  ... {missingAssetCount - 50} more (first {missingAssets.Count} are kept in memory)");
            sb.AppendLine();

            sb.AppendLine("Assets looked up by naming convention (network registry / mesh variation database)");
            foreach (string n in nameLookups.Take(80))
                sb.AppendLine("  " + n);
            if (nameLookups.Count > 80)
                sb.AppendLine($"  ... {nameLookups.Count - 80} more");
            sb.AppendLine();

            if (timings.Count > 0)
            {
                sb.AppendLine("Other timings");
                foreach (string t in timings)
                    sb.AppendLine("  " + t);
                sb.AppendLine();
            }

            sb.AppendLine($"Exceptions during load: {exceptionCount}");
            foreach (string e in exceptions)
                sb.AppendLine("  " + e);

            return sb.ToString().TrimEnd();
        }
    }

    private static void AppendCounts(StringBuilder sb, Dictionary<string, int> counts, int max)
    {
        foreach (KeyValuePair<string, int> kv in counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(max))
            sb.AppendLine($"  {kv.Value,8}  {kv.Key}");
        if (counts.Count > max)
            sb.AppendLine($"  ... {counts.Count - max} more types");
    }

    private static void Increment(Dictionary<string, int> dict, string key)
    {
        dict.TryGetValue(key, out int value);
        dict[key] = value + 1;
    }

    private static string Seconds(double ms) => $"{ms / 1000.0:N2} s";
    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):N0} MB";

    #endregion
}
