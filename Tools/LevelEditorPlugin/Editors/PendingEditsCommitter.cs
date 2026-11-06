using Frosty.Core;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Resources;
using System;
using System.Windows;

namespace LevelEditorPlugin.Editors;

/// <summary>
/// Writes in-memory edits that are not yet in the asset manager (Havok static-model-group
/// transforms) before every project save, autosave, mod export and launch. Without this, moves of
/// Havok-placed objects were only kept if "Write Havok Transform to Res" was pressed first.
/// </summary>
public static class PendingEditsCommitter
{
    private static bool hooked;

    public static void EnsureHooked()
    {
        if (hooked)
            return;
        hooked = true;
        FrostyProject.BeforeWrite += CommitPending;
    }

    private static void CommitPending()
    {
        if (!HavokPhysicsData.HasAnyPendingCommit)
            return;

        // ModifyRes and the entity data belong to the UI thread; autosave and the save task run on
        // other threads.
        Application app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
            app.Dispatcher.Invoke(CommitNow);
        else
            CommitNow();
    }

    private static void CommitNow()
    {
        if (!HavokPhysicsData.HasAnyPendingCommit)
            return;
        int count = HavokPhysicsData.CommitAllPendingToRes();
        LevelEditorLog.Info("Edit", $"Wrote {count} pending Havok physics transform change(s) before saving/exporting");
        App.Logger.Log($"Level editor: wrote {count} pending Havok transform change(s) before saving");
    }
}
