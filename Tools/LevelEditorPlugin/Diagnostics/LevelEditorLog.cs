using Frosty.Core;
using FrostySdk;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;

namespace LevelEditorPlugin.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// Session log for the level editor plugin. One file per Frosty session, written to
/// "LevelEditorLogs" next to FrostyEditor.exe. Thread-safe; never throws to callers.
/// </summary>
public static class LevelEditorLog
{
    private const int MaxSessionFilesKept = 20;

    private static readonly object sync = new object();
    private static StreamWriter writer;
    private static bool initFailed;
    private static bool handlersAttached;

    public static string LogDirectory { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LevelEditorLogs");
    public static string CurrentLogFile { get; private set; }

    /// <summary>Creates the session log file on first use. Safe to call repeatedly.</summary>
    public static void EnsureStarted()
    {
        bool attachHandlers = false;
        lock (sync)
        {
            if (writer != null || initFailed)
                return;

            try
            {
                Directory.CreateDirectory(LogDirectory);
                PruneOldLogs();

                CurrentLogFile = Path.Combine(LogDirectory, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                FileStream stream = new FileStream(CurrentLogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                attachHandlers = !handlersAttached;
                handlersAttached = true;
            }
            catch (Exception ex)
            {
                initFailed = true;
                writer = null;
                TryFrostyLog($"[LevelEditor] Could not create log file in {LogDirectory}: {ex.Message}");
                return;
            }
        }

        WriteHeader();

        if (attachHandlers)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
                Write(LogLevel.Error, "Unhandled", "Unobserved task exception", e.Exception);

            // The UI thread handler only logs; Frosty's own handler still shows the error box.
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                Application.Current.DispatcherUnhandledException += (s, e) =>
                    Write(LogLevel.Error, "Unhandled", "UI thread exception", e.Exception);
            }));
        }

        TryFrostyLog($"[LevelEditor] Diagnostics log: {CurrentLogFile}");
    }

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);
    public static void Warning(string category, string message, Exception ex = null) => Write(LogLevel.Warning, category, message, ex);
    public static void Error(string category, string message, Exception ex = null) => Write(LogLevel.Error, category, message, ex);

    /// <summary>Writes a multi-line block (e.g. a report) without per-line prefixes.</summary>
    public static void WriteBlock(string title, string text)
    {
        EnsureStarted();
        lock (sync)
        {
            if (writer == null)
                return;
            try
            {
                writer.WriteLine();
                writer.WriteLine($"===== {title} ({DateTime.Now:HH:mm:ss}) =====");
                writer.WriteLine(text);
                writer.WriteLine($"===== end of {title} =====");
                writer.WriteLine();
            }
            catch
            {
                // logging must never break the editor
            }
        }
    }

    public static void Write(LogLevel level, string category, string message, Exception ex)
    {
        EnsureStarted();
        lock (sync)
        {
            if (writer == null)
                return;
            try
            {
                string levelText = level switch
                {
                    LogLevel.Debug => "DBG",
                    LogLevel.Info => "INF",
                    LogLevel.Warning => "WRN",
                    _ => "ERR"
                };
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{levelText}] [T{Thread.CurrentThread.ManagedThreadId:D2}] {category}: {message}");
                if (ex != null)
                    writer.WriteLine(FormatException(ex));
            }
            catch
            {
                // logging must never break the editor
            }
        }
    }

    public static string FormatException(Exception ex)
    {
        StringBuilder sb = new StringBuilder();
        int depth = 0;
        while (ex != null && depth < 8)
        {
            sb.AppendLine($"    {(depth == 0 ? "Exception" : "Inner")}: {ex.GetType().FullName}: {ex.Message}");
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                foreach (string line in ex.StackTrace.Split('\n'))
                    sb.AppendLine("      " + line.TrimEnd('\r'));
            }
            ex = ex.InnerException;
            depth++;
        }
        return sb.ToString().TrimEnd();
    }

    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start("explorer.exe", $"\"{LogDirectory}\"");
        }
        catch (Exception ex)
        {
            Warning("Log", "Could not open log folder", ex);
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Write(LogLevel.Error, "Unhandled", $"Unhandled exception (terminating: {e.IsTerminating})", e.ExceptionObject as Exception);
    }

    private static void WriteHeader()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("MEC Level Editor diagnostics log");
        sb.AppendLine($"Started:        {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Build commit:   {ReadBuildCommit()}");
        sb.AppendLine($"Plugin version: {SafeGet(() => Assembly.GetExecutingAssembly().GetName().Version.ToString())}");
        sb.AppendLine($"Frosty version: {SafeGet(() => App.Version)}");
        sb.AppendLine($"Game profile:   {SafeGet(() => ProfilesLibrary.ProfileName)} (data version {SafeGet(() => ProfilesLibrary.DataVersion.ToString())})");
        sb.AppendLine($"Game path:      {SafeGet(() => App.FileSystemManager?.BasePath)}");
        sb.AppendLine($"OS:             {Environment.OSVersion} (64-bit OS: {Environment.Is64BitOperatingSystem}, 64-bit process: {Environment.Is64BitProcess})");
        sb.AppendLine($".NET CLR:       {Environment.Version}");
        sb.AppendLine($"CPU threads:    {Environment.ProcessorCount}");
        sb.AppendLine($"Editor folder:  {AppDomain.CurrentDomain.BaseDirectory}");
        WriteBlock("Session", sb.ToString().TrimEnd());
    }

    private static string ReadBuildCommit()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BUILD_COMMIT.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "(local build, no BUILD_COMMIT.txt)";
        }
        catch
        {
            return "(unreadable)";
        }
    }

    private static void PruneOldLogs()
    {
        try
        {
            FileInfo[] files = new DirectoryInfo(LogDirectory).GetFiles("session_*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToArray();
            foreach (FileInfo file in files.Skip(MaxSessionFilesKept - 1))
                file.Delete();
        }
        catch
        {
            // old logs that cannot be deleted are not a problem
        }
    }

    private static string SafeGet(Func<string> getter)
    {
        try
        {
            return getter() ?? "(null)";
        }
        catch (Exception ex)
        {
            return $"(unavailable: {ex.GetType().Name})";
        }
    }

    private static void TryFrostyLog(string message)
    {
        try
        {
            App.Logger?.Log("{0}", message);
        }
        catch
        {
        }
    }
}
