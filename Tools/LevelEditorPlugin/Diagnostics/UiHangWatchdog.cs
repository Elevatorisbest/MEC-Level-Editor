using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace LevelEditorPlugin.Diagnostics;

/// <summary>
/// Notices when the UI thread stops responding (the editor "freezes") and writes to the session
/// log how long it was blocked and, once per freeze, what the UI thread was doing at the time (its
/// call stack). Used to find freezes that happen outside the actions that have their own timing,
/// such as opening a menu or pressing Launch.
/// </summary>
public static class UiHangWatchdog
{
    private const int PollMs = 250;
    private const double ReportAfterSeconds = 1.0;
    private const double StackAfterSeconds = 1.5;
    private const int MaxStacksPerSession = 25;

    private static Thread watchdog;
    private static Thread uiThread;
    private static Dispatcher dispatcher;
    private static long lastPong;
    private static int stacksTaken;

    public static void EnsureStarted()
    {
        if (watchdog != null)
            return;
        Application app = Application.Current;
        if (app == null)
            return;

        dispatcher = app.Dispatcher;
        uiThread = dispatcher.Thread;
        lastPong = Stopwatch.GetTimestamp();
        watchdog = new Thread(Run) { IsBackground = true, Name = "Level editor UI watchdog", Priority = ThreadPriority.AboveNormal };
        watchdog.Start();
        LevelEditorLog.Info("Watchdog", "UI freeze watchdog started (logs freezes over 1 s with what the UI thread was doing)");
    }

    private static double SecondsSince(long timestamp) => (Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency;

    private static void Run()
    {
        bool inHang = false;
        bool stackTaken = false;
        long hangStart = 0;

        while (true)
        {
            Thread.Sleep(PollMs);
            try
            {
                long sent = Stopwatch.GetTimestamp();
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => Interlocked.Exchange(ref lastPong, Stopwatch.GetTimestamp())));

                double blocked = SecondsSince(Interlocked.Read(ref lastPong)) - PollMs / 1000.0;
                if (blocked > ReportAfterSeconds)
                {
                    if (!inHang)
                    {
                        inHang = true;
                        stackTaken = false;
                        hangStart = Interlocked.Read(ref lastPong);
                    }
                    if (!stackTaken && blocked > StackAfterSeconds && stacksTaken < MaxStacksPerSession)
                    {
                        stackTaken = true;
                        stacksTaken++;
                        string stack = CaptureUiStack();
                        LevelEditorLog.WriteBlock($"UI thread blocked for {blocked:0.0} s so far - what it is doing", stack);
                    }
                }
                else if (inHang)
                {
                    inHang = false;
                    double total = (Interlocked.Read(ref lastPong) - hangStart) / (double)Stopwatch.Frequency;
                    LevelEditorLog.Warning("Watchdog", $"UI thread was blocked for {total:0.0} s");
                }
            }
            catch (Exception)
            {
                // the watchdog must never break the editor
            }
        }
    }

#pragma warning disable CS0618 // Thread.Suspend/Resume and StackTrace(Thread) are obsolete but are the only way on .NET Framework
    private static string CaptureUiStack()
    {
        // A helper resumes the UI thread after a short time no matter what, so the editor cannot
        // stay frozen if taking the stack itself gets stuck.
        int resumed = 0;
        Thread rescue = new Thread(() =>
        {
            Thread.Sleep(1000);
            if (Interlocked.Exchange(ref resumed, 1) == 0)
            {
                try { uiThread.Resume(); } catch (Exception) { }
            }
        }) { IsBackground = true };

        try
        {
            uiThread.Suspend();
            rescue.Start();
            Stopwatch wait = Stopwatch.StartNew();
            while ((uiThread.ThreadState & System.Threading.ThreadState.Suspended) == 0 && wait.ElapsedMilliseconds < 300)
                Thread.Sleep(5);
            StackTrace trace = new StackTrace(uiThread, false);
            return trace.ToString();
        }
        catch (Exception ex)
        {
            return $"(could not read the UI thread's stack: {ex.GetType().Name}: {ex.Message})";
        }
        finally
        {
            if (Interlocked.Exchange(ref resumed, 1) == 0)
            {
                try { uiThread.Resume(); } catch (Exception) { }
            }
        }
    }
#pragma warning restore CS0618
}
