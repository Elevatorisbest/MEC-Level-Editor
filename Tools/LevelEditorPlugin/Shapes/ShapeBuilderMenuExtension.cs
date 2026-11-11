using Frosty.Core;

namespace LevelEditorPlugin.Shapes;

/// <summary>Map &gt; Shape Builder.</summary>
public class ShapeBuilderMenuExtension : MenuExtension
{
    public override string TopLevelMenuName => "Map";
    public override string MenuItemName => "Shape Builder";

    public override RelayCommand MenuItemClicked => new RelayCommand(o => ShapeBuilderWindow.ShowBuilder());
}
