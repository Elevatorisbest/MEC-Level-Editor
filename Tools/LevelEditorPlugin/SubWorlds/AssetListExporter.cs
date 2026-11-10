using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LevelEditorPlugin.SubWorlds;

/// <summary>
/// Writes every asset of the loaded game to tab-separated text files, for looking up what is
/// what without the editor (Excel, a text editor, grep):
///   &lt;name&gt;.tsv            kind (ebx/res), type, name, folder, bundles
///   &lt;name&gt;_subworlds.tsv  every level's sub-worlds with the category and group the
///                         sub-world picker gives them
/// </summary>
public static class AssetListExporter
{
    public static void ExportWithDialog()
    {
        Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export asset list",
            Filter = "Tab-separated text (*.tsv)|*.tsv|All files (*.*)|*.*",
            FileName = $"{ProfilesLibrary.ProfileName}_assets.tsv",
        };
        if (dialog.ShowDialog() != true)
            return;

        string path = dialog.FileName;
        string subWorldPath = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + "_subworlds.tsv");
        int ebxCount = 0, resCount = 0, subWorldCount = 0;
        Exception error = null;

        FrostyTaskWindow.Show("Exporting asset list", "", task =>
        {
            try
            {
                Dictionary<int, string> bundleNames = new Dictionary<int, string>();
                string Bundles(AssetEntry entry)
                {
                    return string.Join(";", entry.EnumerateBundles().Select(id =>
                    {
                        if (!bundleNames.TryGetValue(id, out string name))
                        {
                            name = App.AssetManager.GetBundleEntry(id)?.Name ?? ("#" + id);
                            bundleNames[id] = name;
                        }
                        return name;
                    }));
                }

                using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("Kind\tType\tName\tFolder\tBundles");
                    task.Update("Ebx assets");
                    foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        writer.WriteLine($"ebx\t{entry.Type}\t{entry.Name}\t{entry.Path}\t{Bundles(entry)}");
                        ebxCount++;
                    }
                    task.Update("Res assets");
                    foreach (ResAssetEntry entry in App.AssetManager.EnumerateRes().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        writer.WriteLine($"res\t{entry.Type}\t{entry.Name}\t{entry.Path}\t{Bundles(entry)}");
                        resCount++;
                    }
                }

                task.Update("Sub-worlds");
                List<string> subWorldNames = App.AssetManager.EnumerateEbx("SubWorldData").Select(e => e.Name).ToList();
                using (StreamWriter writer = new StreamWriter(subWorldPath, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("Level\tCategory\tGroup\tSubWorld\tAssetName");
                    foreach (string level in App.AssetManager.EnumerateEbx("LevelData").Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                    {
                        foreach (SubWorldInfo info in SubWorldCatalog.ForLevel(level, subWorldNames))
                        {
                            writer.WriteLine($"{level}\t{SubWorldCatalog.Info(info.Category).Title}\t{info.Group}\t{info.ShortName}\t{info.AssetName}");
                            subWorldCount++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        if (error != null)
        {
            LevelEditorLog.Error("Export", "Exporting the asset list failed", error);
            FrostyMessageBox.Show("Exporting the asset list failed: " + error.Message, "Export Asset List");
            return;
        }
        LevelEditorLog.Info("Export", $"Asset list: {ebxCount} ebx and {resCount} res assets to {path}; {subWorldCount} sub-worlds to {subWorldPath}");
        FrostyMessageBox.Show($"Wrote {ebxCount:N0} ebx and {resCount:N0} res assets to\n{path}\n\nand {subWorldCount:N0} level sub-worlds with their categories to\n{subWorldPath}", "Export Asset List");
    }
}
