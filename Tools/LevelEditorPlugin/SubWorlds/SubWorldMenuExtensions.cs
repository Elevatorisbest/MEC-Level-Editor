using Frosty.Core;

namespace LevelEditorPlugin.SubWorlds;

/// <summary>Map &gt; Sub-worlds to Load.</summary>
public class SubWorldPickerMenuExtension : MenuExtension
{
    public override string TopLevelMenuName => "Map";
    public override string MenuItemName => "Sub-worlds to Load";

    public override RelayCommand MenuItemClicked => new RelayCommand(o => SubWorldPickerWindow.ShowSettings());
}

/// <summary>Map &gt; Export Asset List.</summary>
public class AssetListExportMenuExtension : MenuExtension
{
    public override string TopLevelMenuName => "Map";
    public override string MenuItemName => "Export Asset List";

    public override RelayCommand MenuItemClicked => new RelayCommand(o => AssetListExporter.ExportWithDialog());
}
