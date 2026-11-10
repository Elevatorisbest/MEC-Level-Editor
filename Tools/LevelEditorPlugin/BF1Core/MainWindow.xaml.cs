using LevelEditorPlugin.Entities;
using LevelEditorPlugin.Screens;
using System;
using System.ComponentModel;
using System.Windows;

namespace LevelEditorPlugin.BF1Core;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Current window instance for operating this window
    /// </summary>
    public static MainWindow WinInstance { get; private set; } = null;

    /// <summary>
    /// Constructor
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        // Ctrl+Z / Ctrl+Y / Delete work here too, on the level editor's history and selection
        // (text boxes keep their own Ctrl+Z and Delete)
        InputBindings.Add(new System.Windows.Input.KeyBinding(new Frosty.Core.RelayCommand(_ => Editors.SpatialEditor.Active?.UndoFromOutside(false)), System.Windows.Input.Key.Z, System.Windows.Input.ModifierKeys.Control));
        InputBindings.Add(new System.Windows.Input.KeyBinding(new Frosty.Core.RelayCommand(_ => Editors.SpatialEditor.Active?.UndoFromOutside(true)), System.Windows.Input.Key.Y, System.Windows.Input.ModifierKeys.Control));
        InputBindings.Add(new System.Windows.Input.KeyBinding(new Frosty.Core.RelayCommand(_ => Editors.SpatialEditor.Active?.DeleteSelectedFromOutside()), System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.None));

        WinInstance = this;
    }

    /// <summary>
    /// Window load event
    /// </summary>
    private void Window_Main_Loaded(object sender, RoutedEventArgs e)
    {
    }

    /// <summary>
    /// Window content rendered event
    /// </summary>
    private void Window_Main_ContentRendered(object sender, EventArgs e)
    {
    }

    /// <summary>
    /// Window closing event
    /// </summary>
    private void Window_Main_Closing(object sender, CancelEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
        e.Cancel = true;
    }

    /// <summary>
    /// Update selected entity
    /// </summary>
    public static void UpdateSelectedEntity(Entity entity, LevelEditorScreen screen)
    {
        if (WinInstance == null)
            return;

        WinInstance.TabItem_MeshView.UpdateSelectedEntity(entity, screen);
    }

    /// <summary>
    /// Update entity Transform info
    /// </summary>
    public static void UpdateEntityTransform()
    {
        if (WinInstance == null)
            return;

        WinInstance.TabItem_MeshView.UpdateUI();
    }
}
