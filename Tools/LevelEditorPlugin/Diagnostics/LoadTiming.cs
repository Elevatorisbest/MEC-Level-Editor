using System.Collections.Generic;
using System.Diagnostics;

namespace LevelEditorPlugin.Diagnostics;

/// <summary>
/// Exclusive ("self") time measurement for nested work during a load: time spent in nested
/// measured sections is subtracted from the enclosing section, so the totals per category add up
/// to the time actually spent there. Only active while a <see cref="LoadReport"/> is collecting.
///
/// Usage: long t = LoadTiming.Enter(); try { ... } finally { LoadTiming.Exit(t, "category"); }
/// </summary>
internal static class LoadTiming
{
    [System.ThreadStatic]
    private static Stack<long> childTicks;

    public static long Enter()
    {
        if (LoadReport.Current == null)
            return 0;
        (childTicks ??= new Stack<long>()).Push(0);
        return Stopwatch.GetTimestamp();
    }

    public static void Exit(long start, string category)
    {
        if (start == 0)
            return;

        Stack<long> stack = childTicks;
        if (stack == null || stack.Count == 0)
            return;

        long total = Stopwatch.GetTimestamp() - start;
        long child = stack.Pop();
        if (stack.Count > 0)
            stack.Push(stack.Pop() + total);

        LoadReport.AddExclusiveTime(category, total - child);
    }
}
