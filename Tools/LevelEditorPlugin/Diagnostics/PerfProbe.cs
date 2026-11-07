using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LevelEditorPlugin.Diagnostics;

/// <summary>
/// Measures one editor action (or a phase of one) and logs what it cost: wall time, CPU time of the
/// whole process (CPU much higher than wall = many threads busy; much lower = waiting, e.g. on the
/// disk or the page file), page faults, garbage collections and working-set change. Used to find
/// out why some actions are slow the first time.
///
/// Usage: <c>using (PerfProbe.Start("Copy", "copy to camera")) { ... }</c>, optionally with
/// <see cref="Phase"/> calls in between.
/// </summary>
public sealed class PerfProbe : IDisposable
{
    private readonly string category;
    private readonly string name;
    private readonly Snapshot start;
    private Snapshot phaseStart;
    private readonly StringBuilder phases = new StringBuilder();
    private bool disposed;

    private PerfProbe(string category, string name)
    {
        this.category = category;
        this.name = name;
        start = Snapshot.Take();
        phaseStart = start;
    }

    public static PerfProbe Start(string category, string name) => new PerfProbe(category, name);

    /// <summary>Ends the current phase (started at the previous Phase call or at Start).</summary>
    public void Phase(string phaseName)
    {
        Snapshot now = Snapshot.Take();
        phases.Append($"\n    {phaseName,-34} {Describe(phaseStart, now)}");
        phaseStart = now;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Snapshot end = Snapshot.Take();
        string text = $"{name}: {Describe(start, end)}";
        if (phases.Length > 0)
            text += phases.ToString();
        LevelEditorLog.Info(category, "Timing: " + text);
    }

    private static string Describe(Snapshot a, Snapshot b)
    {
        double wall = (b.Wall - a.Wall) * 1000.0 / Stopwatch.Frequency;
        double cpu = (b.Cpu - a.Cpu).TotalMilliseconds;
        long faults = (long)b.PageFaults - (long)a.PageFaults;
        long wsDelta = (b.WorkingSet - a.WorkingSet) / (1024 * 1024);
        string gcs = $"GC {b.Gen0 - a.Gen0}/{b.Gen1 - a.Gen1}/{b.Gen2 - a.Gen2}";
        return $"{wall,9:0} ms wall, {cpu,9:0} ms CPU ({(wall > 0.5 ? cpu / wall : 0):0.0} cores), {faults,9} page faults, {gcs}, working set {(wsDelta >= 0 ? "+" : "")}{wsDelta} MB";
    }

    private struct Snapshot
    {
        public long Wall;
        public TimeSpan Cpu;
        public uint PageFaults;
        public long WorkingSet;
        public int Gen0, Gen1, Gen2;

        public static Snapshot Take()
        {
            Snapshot s = new Snapshot
            {
                Wall = Stopwatch.GetTimestamp(),
                Gen0 = GC.CollectionCount(0),
                Gen1 = GC.CollectionCount(1),
                Gen2 = GC.CollectionCount(2),
            };
            try
            {
                using (Process p = Process.GetCurrentProcess())
                    s.Cpu = p.TotalProcessorTime;
                PROCESS_MEMORY_COUNTERS counters = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
                if (GetProcessMemoryInfo(GetCurrentProcess(), out counters, counters.cb))
                {
                    s.PageFaults = counters.PageFaultCount;
                    s.WorkingSet = (long)(ulong)counters.WorkingSetSize;
                }
            }
            catch
            {
                // measuring must never break the editor
            }
            return s;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, uint size);
}
