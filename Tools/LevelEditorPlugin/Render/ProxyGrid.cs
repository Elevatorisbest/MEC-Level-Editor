using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Render.Proxies;
using SharpDX;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace LevelEditorPlugin.Render;

/// <summary>
/// Uniform grid on the X/Z plane over all render proxies, used to avoid testing every proxy
/// every frame (SP_MainCity: 357k proxies, ~36 ms per frame for culling).
///
/// The grid only produces candidates; the caller still runs its normal per-proxy tests, so what
/// is drawn does not change. A cell is skipped when its bounds are outside the view frustum, and
/// inside a cell (proxies sorted by "reach" = radius / own cull screen size, largest first) the
/// scan stops at the first proxy too small to pass its own ShouldRender cull test at the cell's
/// nearest distance; proxies below the caller's global minimum size are skipped.
///
/// Proxies whose bounds grow or move after the grid was built (edits), and proxies added later
/// (copies), are kept in an "always test" list. The grid is rebuilt when that list gets large or
/// when proxies are removed.
/// </summary>
internal sealed class ProxyGrid
{
    private const float CellSize = 64.0f;
    /// <summary>Proxies larger than this (terrain, huge buildings) are always tested.</summary>
    private const float LargeRadius = 256.0f;
    /// <summary>Rebuild once this many proxies have been moved out of their cells.</summary>
    public const int RebuildDynamicThreshold = 4096;

    private sealed class Cell
    {
        public BoundingBox Bounds;
        public bool HasBounds;
        public readonly List<RenderProxy> Proxies = new List<RenderProxy>();
    }

    private readonly List<Cell> cells = new List<Cell>();
    private readonly List<RenderProxy> alwaysTest = new List<RenderProxy>();
    private readonly HashSet<RenderProxy> dynamicProxies = new HashSet<RenderProxy>();
    private readonly object sync = new object();

    public int CellCount => cells.Count;
    public int DynamicCount { get { lock (sync) return dynamicProxies.Count; } }

    public static ProxyGrid Build(IReadOnlyList<RenderProxy> proxies)
    {
        Stopwatch timer = Stopwatch.StartNew();
        ProxyGrid grid = new ProxyGrid();
        Dictionary<long, Cell> byKey = new Dictionary<long, Cell>();
        int large = 0;

        foreach (RenderProxy proxy in proxies)
        {
            BoundingBox box = proxy.BoundingBox;
            Vector3 translation = proxy.Transform.TranslationVector;
            float radius = BoundingSphere.FromBox(box).Radius;
            float ownCull = proxy.MinScreenSize;

            proxy.Grid = grid;
            proxy.GridBounds = box;
            proxy.GridTranslation = translation;
            proxy.GridRadius = radius;
            // ShouldRender needs 2r / viewSize(distance) > ownCull, i.e. r / ownCull > viewSize / 2
            proxy.GridReach = ownCull > 0.0f ? radius / ownCull : float.MaxValue;
            proxy.GridDynamic = false;

            if (radius > LargeRadius || float.IsNaN(radius) || float.IsInfinity(radius))
            {
                grid.alwaysTest.Add(proxy);
                large++;
                continue;
            }

            Vector3 center = (box.Minimum + box.Maximum) * 0.5f;
            long cx = (long)Math.Floor(center.X / CellSize);
            long cz = (long)Math.Floor(center.Z / CellSize);
            long key = (cx << 32) ^ (cz & 0xFFFFFFFFL);

            if (!byKey.TryGetValue(key, out Cell cell))
            {
                cell = new Cell();
                byKey.Add(key, cell);
                grid.cells.Add(cell);
            }

            // cell bounds cover each proxy's box and its origin (distance is measured to the origin)
            BoundingBox withOrigin = BoundingBox.Merge(box, new BoundingBox(translation, translation));
            cell.Bounds = cell.HasBounds ? BoundingBox.Merge(cell.Bounds, withOrigin) : withOrigin;
            cell.HasBounds = true;
            cell.Proxies.Add(proxy);
        }

        foreach (Cell cell in grid.cells)
            cell.Proxies.Sort((a, b) => b.GridReach.CompareTo(a.GridReach));

        LevelEditorLog.Info("Render", $"Culling grid built in {timer.Elapsed.TotalMilliseconds:N0} ms: {proxies.Count} proxies, {grid.cells.Count} cells of {CellSize} m, {large} large proxies always tested");
        return grid;
    }

    /// <summary>Called by a proxy whenever its bounding box is set.</summary>
    public void OnBoundsChanged(RenderProxy proxy, BoundingBox newBox)
    {
        if (proxy.GridDynamic)
            return;

        // Still inside its original bounds with the same origin (e.g. sprites rescaling near the
        // camera): the cell data stays conservative, nothing to do.
        if (proxy.GridBounds.Contains(ref newBox) == ContainmentType.Contains &&
            proxy.Transform.TranslationVector == proxy.GridTranslation)
            return;

        MarkDynamic(proxy);
    }

    /// <summary>Always test this proxy from now on (moved, resized or added after the build).</summary>
    public void MarkDynamic(RenderProxy proxy)
    {
        lock (sync)
        {
            proxy.Grid = this;
            proxy.GridDynamic = true;
            if (dynamicProxies.Add(proxy))
                alwaysTest.Add(proxy);
        }
    }

    /// <summary>
    /// Adds every proxy that might pass the caller's tests to <paramref name="output"/>.
    /// </summary>
    /// <param name="minScreenSize">The caller's minimum screen size (0 = no size culling).</param>
    public void CollectCandidates(BoundingFrustum frustum, Vector3 cameraPosition, float minScreenSize, List<RenderProxy> output)
    {
        lock (sync)
        {
            foreach (Cell cell in cells)
            {
                if (frustum.Contains(cell.Bounds) == ContainmentType.Disjoint)
                    continue;

                // Half of the smaller view dimension at the cell's nearest point. Every proxy in the
                // cell is at least this far away, so this bounds how large it can appear.
                float halfView = 0.0f;
                BoundingBox bounds = cell.Bounds;
                float nearest = (float)Math.Sqrt(Math.Max(0.0f, DistanceSquared(ref bounds, ref cameraPosition)));
                if (nearest > 0.0f)
                    halfView = Math.Min(frustum.GetWidthAtDepth(nearest), frustum.GetHeightAtDepth(nearest)) * 0.5f;

                float radiusThreshold = minScreenSize * halfView;

                List<RenderProxy> list = cell.Proxies;
                for (int i = 0; i < list.Count; i++)
                {
                    RenderProxy proxy = list[i];
                    if (proxy.GridReach < halfView)
                        break; // sorted by reach: every remaining proxy is below its own cull size here
                    if (proxy.GridRadius < radiusThreshold || proxy.GridDynamic)
                        continue; // below the caller's global minimum size, or tested via alwaysTest
                    output.Add(proxy);
                }
            }

            output.AddRange(alwaysTest);
        }
    }

    private static float DistanceSquared(ref BoundingBox box, ref Vector3 point)
    {
        float dx = Math.Max(0.0f, Math.Max(box.Minimum.X - point.X, point.X - box.Maximum.X));
        float dy = Math.Max(0.0f, Math.Max(box.Minimum.Y - point.Y, point.Y - box.Maximum.Y));
        float dz = Math.Max(0.0f, Math.Max(box.Minimum.Z - point.Z, point.Z - box.Maximum.Z));
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Detaches all proxies from this grid (before a rebuild or when proxies are removed).</summary>
    public void Detach(IReadOnlyList<RenderProxy> proxies)
    {
        foreach (RenderProxy proxy in proxies)
        {
            if (proxy.Grid == this)
                proxy.Grid = null;
        }
    }
}
