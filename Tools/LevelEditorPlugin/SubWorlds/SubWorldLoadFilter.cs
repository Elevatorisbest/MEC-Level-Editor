using Frosty.Core;
using LevelEditorPlugin.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LevelEditorPlugin.SubWorlds;

/// <summary>
/// Which sub-worlds of a level are loaded when the level is opened.
///
/// The choice is stored per level as the list of sub-worlds to leave out (Frosty config, game
/// scope), so sub-worlds that are not in the picker (zone-streamed pieces, ones added later) are
/// always loaded. While a level loads, <see cref="ShouldSkip"/> tells ReferenceObject which
/// sub-world references not to create. Leaving a sub-world out only skips it in the editor: the
/// level's data is not changed (edits change the ebx directly, they never rebuild a level's object
/// list from what is loaded), so the reference stays in the level and the game still loads it.
/// </summary>
public static class SubWorldLoadFilter
{
    private const string ExcludedKeyPrefix = "LevelEditor.SubWorlds.Excluded.";
    private const string ChosenKeyPrefix = "LevelEditor.SubWorlds.Chosen.";
    private const string AskKey = "LevelEditor.SubWorlds.AskOnOpen";
    private const string ShowKey = "LevelEditor.SubWorlds.ShowOptional";
    private const string LastLevelKey = "LevelEditor.SubWorlds.LastLevel";

    // set while a level is loading (read from the loading thread)
    private static volatile HashSet<string> activeExcluded;
    private static volatile HashSet<string> activeShown;
    private static readonly List<string> skipped = new List<string>();

    /// <summary>Show the picker each time a level is opened (default on).</summary>
    public static bool AskOnOpen
    {
        get => Config.Get(AskKey, true, ConfigScope.Game);
        set { Config.Add(AskKey, value, ConfigScope.Game); Config.Save(); }
    }

    /// <summary>
    /// Show loaded mission and side content in the view. Without it, sub-worlds the game does not
    /// load at start (AutoLoad off) are loaded but start hidden in the Layers panel, as before.
    /// </summary>
    public static bool ShowOptionalContent
    {
        get => Config.Get(ShowKey, true, ConfigScope.Game);
        set { Config.Add(ShowKey, value, ConfigScope.Game); Config.Save(); }
    }

    public static string LastLevel
    {
        get => Config.Get(LastLevelKey, "", ConfigScope.Game);
        set { Config.Add(LastLevelKey, value ?? "", ConfigScope.Game); Config.Save(); }
    }

    /// <summary>True once a choice was saved for the level (otherwise the picker starts with "City only").</summary>
    public static bool HasSavedChoice(string levelName) => Config.Get(ChosenKeyPrefix + Key(levelName), false, ConfigScope.Game);

    public static HashSet<string> LoadExcluded(string levelName)
    {
        string text = Config.Get(ExcludedKeyPrefix + Key(levelName), "", ConfigScope.Game);
        return new HashSet<string>(text.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
    }

    public static void SaveExcluded(string levelName, IEnumerable<string> excluded)
    {
        Config.Add(ExcludedKeyPrefix + Key(levelName), string.Join("|", excluded.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)), ConfigScope.Game);
        Config.Add(ChosenKeyPrefix + Key(levelName), true, ConfigScope.Game);
        Config.Save();
    }

    private static string Key(string levelName) => (levelName ?? "").Replace('/', '.').ToLowerInvariant();

    /// <summary>
    /// Starts applying a choice for one level load. <paramref name="shown"/>: sub-worlds to show in
    /// the view even when the game would not load them at start (may be null).
    /// </summary>
    public static void Begin(HashSet<string> excluded, HashSet<string> shown)
    {
        lock (skipped)
            skipped.Clear();
        activeExcluded = excluded != null && excluded.Count > 0 ? new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase) : null;
        activeShown = shown != null && shown.Count > 0 ? new HashSet<string>(shown, StringComparer.OrdinalIgnoreCase) : null;
    }

    /// <summary>Stops applying the choice and logs what was left out.</summary>
    public static void End(string levelName)
    {
        bool wasActive = activeExcluded != null;
        activeExcluded = null;
        activeShown = null;
        if (!wasActive)
            return;

        string[] list;
        lock (skipped)
            list = skipped.ToArray();
        LevelEditorLog.Info("SubWorlds", list.Length == 0
            ? $"{levelName}: no sub-world was left out"
            : $"{levelName}: left out {list.Length} sub-world(s) by the load selection (Map > Sub-worlds to Load): {string.Join(", ", list.Select(SubWorldCatalog.ShortNameOf))}");
    }

    /// <summary>True when the sub-world reference with this BundleName should not be loaded now.</summary>
    public static bool ShouldSkip(string bundleName)
    {
        HashSet<string> excluded = activeExcluded;
        if (excluded == null || string.IsNullOrEmpty(bundleName) || !excluded.Contains(bundleName))
            return false;
        lock (skipped)
            skipped.Add(bundleName);
        return true;
    }

    /// <summary>True when a loaded sub-world should start visible although the game does not load it at start.</summary>
    public static bool ShouldShow(string subWorldName)
    {
        HashSet<string> shown = activeShown;
        return shown != null && !string.IsNullOrEmpty(subWorldName) && shown.Contains(subWorldName);
    }
}
