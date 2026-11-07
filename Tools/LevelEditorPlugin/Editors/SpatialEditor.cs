using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk.Interfaces;
using LevelEditorPlugin.Controls;
using LevelEditorPlugin.Entities;
using LevelEditorPlugin.Layers;
using LevelEditorPlugin.Screens;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Shapes;

namespace LevelEditorPlugin.Editors;

public interface IEditorProvider
{
    LevelEditorScreen Screen { get; }
    Layers.SceneLayer RootLayer { get; }

    void SelectLayer(Layers.SceneLayer newSelection);
    void SelectEntity(Entities.Entity newSelection);
    void CenterOnSelection();

    event EventHandler<SelectedEntityChangedEventArgs> SelectedEntityChanged;
    event EventHandler<SelectedLayerChangedEventArgs> SelectedLayerChanged;
}

public interface IWorldProvider
{
    EntityWorld World { get; }
}

[TemplatePart(Name = "PART_ThumbnailBorder", Type = typeof(Rectangle))]
public class SpatialEditor : ToolbarAssetEditor, IEditorProvider
{
    protected class LoadingStateInfo
    {
        public FrostyTaskWindow Task;
        public ILogger Logger;
    }
    protected static LoadingStateInfo currentLoadingState;

    public Layers.SceneLayer RootLayer => rootLayer;
    public Layers.SceneLayer SelectedLayer => selectedLayer;
    public LevelEditorScreen Screen => screen;
    public Controls.DockManager DockManager => dockManager;

    protected LevelEditorScreen screen;

    protected ReferenceObject editingWorld;
    protected Layers.SceneLayer rootLayer;
    protected EntityWorld world;

    protected Entity selectedEntity;
    protected Layers.SceneLayer selectedLayer;

    protected Controls.DockManager dockManager;

    protected Rectangle thumbnailBorder;

    public event EventHandler<SelectedEntityChangedEventArgs> SelectedEntityChanged;
    public event EventHandler<SelectedLayerChangedEventArgs> SelectedLayerChanged;

    static SpatialEditor()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SpatialEditor), new FrameworkPropertyMetadata(typeof(SpatialEditor)));
    }

    public SpatialEditor(ILogger inLogger)
        : base(inLogger)
    {
        dockManager = new Controls.DockManager(this);

        // Undo/redo anywhere in the level editor, including the 3D view (its key presses are
        // ordinary WPF key events that bubble up here). Text boxes handle their own Ctrl+Z first.
        Frosty.Core.RelayCommand undo = new Frosty.Core.RelayCommand(_ => UndoRedo(redo: false));
        Frosty.Core.RelayCommand redo = new Frosty.Core.RelayCommand(_ => UndoRedo(redo: true));
        InputBindings.Add(new System.Windows.Input.KeyBinding(undo, System.Windows.Input.Key.Z, System.Windows.Input.ModifierKeys.Control));
        InputBindings.Add(new System.Windows.Input.KeyBinding(redo, System.Windows.Input.Key.Y, System.Windows.Input.ModifierKeys.Control));
        InputBindings.Add(new System.Windows.Input.KeyBinding(redo, System.Windows.Input.Key.Z, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift));

        // edits made in this editor go into this editor's history even if another asset tab was
        // opened or closed in between
        PreviewMouseDown += (s, e) => MakeUndoCurrent();
        PreviewKeyDown += (s, e) => MakeUndoCurrent();
    }

    private void UndoRedo(bool redo)
    {
        MakeUndoCurrent();
        try
        {
            if (redo)
                Frosty.Core.Managers.UndoManager.Instance?.Redo();
            else
                Frosty.Core.Managers.UndoManager.Instance?.Undo();
            BF1Core.MainWindow.UpdateEntityTransform();
        }
        catch (Exception ex)
        {
            Diagnostics.LevelEditorLog.Error("Edit", redo ? "Redo failed" : "Undo failed", ex);
        }
    }

    public void SelectEntity(Entities.Entity newSelection)
    {
        if (newSelection != selectedEntity)
        {
            // Select the root world if nothing else is selected
            Entity tmpSelection = newSelection;
            if (tmpSelection == null)
                tmpSelection = editingWorld;

            SelectedEntityChanged?.Invoke(this, new SelectedEntityChangedEventArgs(tmpSelection, selectedEntity));
            selectedEntity = newSelection;
        }

        screen.SelectEntity(selectedEntity);
    }

    public void CenterOnSelection()
    {
        screen.CenterOnSelection();
    }

    public void SelectLayer(Layers.SceneLayer newSelection)
    {
        if (newSelection != selectedLayer)
        {
            SceneLayer oldSelection = selectedLayer;
            selectedLayer = newSelection;

            SelectedLayerChanged?.Invoke(this, new SelectedLayerChangedEventArgs(newSelection, oldSelection));
        }
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        thumbnailBorder = GetTemplateChild("PART_ThumbnailBorder") as Rectangle;
        PerformTemplateMagic();
    }

    public override void Closed()
    {
        DockManager.Shutdown();
        editingWorld.Destroy();

        base.Closed();
    }

    protected override void Reload()
    {
        DockManager.ShowFloatingWindows();
        screen.SetCamera();
    }

    protected override void Unload()
    {
        DockManager.HideFloatingWindows();
    }

    // @temp
    protected Layers.SceneLayer MakeFakeLayer()
    {
        string layerName = System.IO.Path.GetFileName(editingWorld.Blueprint.Name);
        Layers.SceneLayer layer = new Layers.SceneLayer(editingWorld, layerName, new SharpDX.Color(0.0f, 0.5f, 0.0f, 1.0f));

        List<Entity> entities = (List<Entity>)editingWorld.GetType().GetField("entities", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(editingWorld);
        foreach (Entity entity in entities)
        {
            if (entity is ILayerEntity)
            {
                ILayerEntity entityLayer = entity as ILayerEntity;
                Layers.SceneLayer childLayer = entityLayer.GetLayer();
                if (childLayer != null)
                    layer.ChildLayers.Add(childLayer);
            }
            else
            {
                layer.AddEntity(entity);
                entity.SetOwner(entity);
            }
        }

        return layer;
    }

    protected void ShowThumbnailSafeZone(bool show, FrostyViewport viewport)
    {
        thumbnailBorder.Visibility = (show) ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            double shortestSide = (viewport.ViewportHeight > viewport.ViewportWidth) ? viewport.ViewportWidth : viewport.ViewportHeight;
            thumbnailBorder.Width = shortestSide;
            thumbnailBorder.Height = shortestSide;
        }
    }

    protected void CaptureThumbnail(FrostyViewport viewport)
    {
        string thumbnailPath = $"{App.ProfileSettingsPath}/Thumbnails/{Asset.FileGuid}.png";
        FileInfo fi = new FileInfo(thumbnailPath);

        if (!Directory.Exists(fi.DirectoryName))
            Directory.CreateDirectory(fi.DirectoryName);

        double origWidth = viewport.ViewportWidth;
        double origHeight = viewport.ViewportHeight;

        viewport.SetPaused(true);
        Thread.Sleep(100);

        viewport.Width = 256;
        viewport.Height = 256;

        viewport.Measure(new Size(256, 256));
        viewport.Arrange(new Rect(0, 0, 256, 256));

        viewport.GetType().GetMethod("DisposeSizeDependentBuffers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(viewport, null);
        viewport.GetType().GetMethod("CreateSizeDependentBuffers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(viewport, null);

        screen.CaptureThumbnail(fi.FullName);

        viewport.Width = double.NaN;
        viewport.Height = double.NaN;

        viewport.Measure(new Size(origWidth, origHeight));
        viewport.Arrange(new Rect(0, 0, origWidth, origHeight));

        viewport.GetType().GetMethod("DisposeSizeDependentBuffers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(viewport, null);
        viewport.GetType().GetMethod("CreateSizeDependentBuffers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(viewport, null);
        viewport.SetPaused(false);

        App.NotificationManager.Show("Thumbnail created");
    }

    public static void UpdateTask(string status = null, double? progress = null)
    {
        if (currentLoadingState != null)
        {
            currentLoadingState.Task.Update(status, progress);
        }
    }
}
