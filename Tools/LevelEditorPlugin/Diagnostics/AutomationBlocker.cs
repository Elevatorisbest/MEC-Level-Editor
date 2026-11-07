using Frosty.Core;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;

namespace LevelEditorPlugin.Diagnostics;

/// <summary>
/// Stops UI Automation clients (screen readers, voice/accessibility tools, and many overlays,
/// launchers and helper apps that read other programs' windows) from attaching to the editor's
/// windows.
///
/// Why: when such a client is attached, WPF reports every layout change of the editor's controls
/// to it, one cross-process call per element. With a level the size of SP_MainCity that froze the
/// editor for 40+ seconds when the Map menu was opened, and loaded the whole PC (music stuttered).
/// Seen in the UI freeze watchdog stack: AutomationPeer.UpdateSubtree ->
/// RaiseAutomationPropertyChangedEvent.
///
/// It only answers "no" to the UI Automation root request (WM_GETOBJECT with UiaRootObjectId) for
/// these windows; nothing outside the editor is changed. It can be turned off in the BF1 window
/// (takes effect after restarting Frosty), e.g. if you use a screen reader.
/// </summary>
public static class AutomationBlocker
{
    private const string ConfigKey = "LevelEditor_BlockUiAutomation";
    private const int WM_GETOBJECT = 0x003D;
    private const int UiaRootObjectId = -25;

    private static readonly HashSet<IntPtr> hooked = new HashSet<IntPtr>();
    private static bool? enabled;
    private static int blockedRequests;

    public static bool Enabled
    {
        get => enabled ??= Config.Get(ConfigKey, true, ConfigScope.Global);
        set
        {
            enabled = value;
            Config.Add(ConfigKey, value, ConfigScope.Global);
            Config.Save();
        }
    }

    private static bool classHandlerRegistered;

    /// <summary>
    /// Hooks every open window and every window opened later (call on the UI thread, as early as
    /// possible: a client that attached before this keeps its connection until Frosty restarts).
    /// </summary>
    public static void HookAllWindows()
    {
        if (!Enabled || Application.Current == null)
            return;
        if (!classHandlerRegistered)
        {
            classHandlerRegistered = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) => Hook(s as Window)));
        }
        foreach (Window window in Application.Current.Windows)
            Hook(window);
    }

    public static void Hook(Window window)
    {
        if (!Enabled || window == null)
            return;

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            // not shown yet: hook as soon as it has a window handle
            window.SourceInitialized += (s, e) => Hook(window);
            return;
        }

        lock (hooked)
        {
            if (!hooked.Add(handle))
                return;
        }
        HwndSource source = HwndSource.FromHwnd(handle);
        if (source == null)
            return;
        source.AddHook(WndProc);
        LevelEditorLog.Info("Automation", $"UI Automation blocked for window \"{window.Title}\"");
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETOBJECT && unchecked((int)(long)lParam) == UiaRootObjectId)
        {
            handled = true;
            if (++blockedRequests == 1)
                LevelEditorLog.Info("Automation", "A UI Automation client tried to attach to the editor and was refused (see AutomationBlocker)");
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }
}
