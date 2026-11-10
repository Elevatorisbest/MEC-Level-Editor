using Frosty.Core;

namespace LevelEditorPlugin.AssetBrowser;

/// <summary>Map &gt; Asset Browser.</summary>
public class AssetBrowserMenuExtension : MenuExtension
{
    public override string TopLevelMenuName => "Map";
    public override string MenuItemName => "Asset Browser";

    public override RelayCommand MenuItemClicked => new RelayCommand(o => AssetBrowserWindow.ShowBrowser());
}
