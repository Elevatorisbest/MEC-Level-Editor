using System;
using System.Diagnostics;

namespace LevelEditorPlugin.Diagnostics;

/// <summary>
/// Per-viewport frame timing. Writes one summary line to the session log every 10 seconds while
/// the viewport is rendering: frame rate, and how much of each frame goes to culling
/// (CollectMeshInstances), sorting and issuing draw calls for meshes.
/// </summary>
public sealed class FrameStats
{
    private const double ReportIntervalSeconds = 10.0;

    private readonly string name;
    private long lastFrameTimestamp;
    private long windowStart;

    private int frames;
    private double sumFrameMs, maxFrameMs;
    private double sumCollectMs, sumSortMs, sumDrawMs;
    private long sumVisible, sumDraws, sumCandidates, sumInstanced, sumGroups;
    private double sumUpdateMs;
    private int lastProxyCount;

    public FrameStats(string name)
    {
        this.name = name;
    }

    /// <summary>Call once at the start of every rendered frame.</summary>
    public void FrameStarted()
    {
        long now = Stopwatch.GetTimestamp();
        if (lastFrameTimestamp != 0)
        {
            double frameMs = (now - lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency;
            // gaps over 2 s are pauses (tab hidden, loading), not slow frames
            if (frameMs < 2000.0)
            {
                frames++;
                sumFrameMs += frameMs;
                maxFrameMs = Math.Max(maxFrameMs, frameMs);
            }
        }
        lastFrameTimestamp = now;

        if (windowStart == 0)
            windowStart = now;
        else if ((now - windowStart) / (double)Stopwatch.Frequency >= ReportIntervalSeconds)
        {
            Flush();
            windowStart = now;
        }
    }

    public void Collected(double ms, int proxyCount, int visibleCount, int candidateCount)
    {
        sumCollectMs += ms;
        sumVisible += visibleCount;
        sumCandidates += candidateCount;
        lastProxyCount = proxyCount;
    }

    public void Updated(double ms) => sumUpdateMs += ms;

    public void Sorted(double ms) => sumSortMs += ms;

    public void Drawn(double ms, int drawCount, int instancedCount = 0, int groupCount = 0)
    {
        sumDrawMs += ms;
        sumDraws += drawCount;
        sumInstanced += instancedCount;
        sumGroups += groupCount;
    }

    private void Flush()
    {
        if (frames == 0)
            return;

        double avgFrame = sumFrameMs / frames;
        LevelEditorLog.Info("Frame",
            $"{name}: {1000.0 / avgFrame:N1} fps (frame avg {avgFrame:N1} ms, worst {maxFrameMs:N1} ms over {frames} frames) | " +
            $"proxy updates {sumUpdateMs / frames:N1} ms | " +
            $"culling {sumCollectMs / frames:N1} ms ({lastProxyCount} proxies, {sumCandidates / frames} tested, {sumVisible / frames} visible) | " +
            $"sorting {sumSortMs / frames:N1} ms | mesh draws {sumDrawMs / frames:N1} ms ({sumDraws / frames} instances, " +
            $"{sumInstanced / frames} of them instanced in {sumGroups / frames} batches)");

        frames = 0;
        sumFrameMs = maxFrameMs = sumCollectMs = sumSortMs = sumDrawMs = 0;
        sumVisible = sumDraws = sumCandidates = sumInstanced = sumGroups = 0;
        sumUpdateMs = 0;
    }
}
