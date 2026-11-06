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
    private long sumVisible, sumDraws;
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

    public void Collected(double ms, int proxyCount, int visibleCount)
    {
        sumCollectMs += ms;
        sumVisible += visibleCount;
        lastProxyCount = proxyCount;
    }

    public void Sorted(double ms) => sumSortMs += ms;

    public void Drawn(double ms, int drawCount)
    {
        sumDrawMs += ms;
        sumDraws += drawCount;
    }

    private void Flush()
    {
        if (frames == 0)
            return;

        double avgFrame = sumFrameMs / frames;
        LevelEditorLog.Info("Frame",
            $"{name}: {1000.0 / avgFrame:N1} fps (frame avg {avgFrame:N1} ms, worst {maxFrameMs:N1} ms over {frames} frames) | " +
            $"culling {sumCollectMs / frames:N1} ms ({lastProxyCount} proxies, {sumVisible / frames} visible) | " +
            $"sorting {sumSortMs / frames:N1} ms | mesh draws {sumDrawMs / frames:N1} ms ({sumDraws / frames} instances)");

        frames = 0;
        sumFrameMs = maxFrameMs = sumCollectMs = sumSortMs = sumDrawMs = 0;
        sumVisible = sumDraws = 0;
    }
}
