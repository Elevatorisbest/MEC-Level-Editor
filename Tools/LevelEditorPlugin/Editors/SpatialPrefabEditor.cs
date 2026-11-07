using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk.Interfaces;
using LevelEditorPlugin.Entities;
using LevelEditorPlugin.Screens;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using LevelEditorPlugin.Diagnostics;

namespace LevelEditorPlugin.Editors;

[TemplatePart(Name = PART_Renderer, Type = typeof(FrostyViewport))]
public class SpatialPrefabEditor : SpatialEditor
{
    private const string PART_Renderer = "PART_Renderer";

    private FrostyViewport viewport;

    public SpatialPrefabEditor(ILogger inLogger)
        : base(inLogger)
    {
        screen = new LevelEditorScreen(true);
        dockManager.LoadFromConfig("SpatialPrefabEditor", new Controls.DockManager.DockManagerConfigData()
        {
            Layouts =
            [
                new Controls.DockManager.DockLayoutData()
                {
                    UniqueId = "UID_LevelEditor_Layers",
                    IsVisible = true,
                    IsSelected = true,
                    Location = Controls.DockLocation.TopLeft
                },
                new Controls.DockManager.DockLayoutData()
                {
                    UniqueId = "UID_LevelEditor_Instances",
                    IsVisible = true,
                    IsSelected = true,
                    Location = Controls.DockLocation.BottomLeft
                },
                new Controls.DockManager.DockLayoutData()
                {
                    UniqueId = "UID_LevelEditor_Properties",
                    IsVisible = true,
                    IsSelected = true,
                    Location = Controls.DockLocation.TopRight
                },
                new Controls.DockManager.DockLayoutData()
                {
                    UniqueId = "UID_LevelEditor_Timeline",
                    IsVisible = false,
                    IsSelected = false,
                    Location = Controls.DockLocation.Bottom
                },
                new Controls.DockManager.DockLayoutData()
                {
                    UniqueId = DiagnosticsViewModel.Uid,
                    IsVisible = true,
                    IsSelected = false,
                    Location = Controls.DockLocation.BottomRight
                },
                new Controls.DockManager.DockLayoutData()
                {
                    UniqueId = ValidationViewModel.Uid,
                    IsVisible = true,
                    IsSelected = false,
                    Location = Controls.DockLocation.BottomRight
                },
                new Controls.DockManager.DockLayoutData()
                {
                    UniqueId = "UID_LevelEditor_Schematics",
                    IsVisible = false,
                    IsSelected = false,
                    Location = Controls.DockLocation.Floating,
                    FloatingData = new Controls.DockManager.DockLayoutFloatingData()
                    {
                         Width = 800,
                         Height = 400
                    }
                }
            ]
        });
    }

    public override List<ToolbarItem> RegisterToolbarItems()
    {
        return
        [
            new DockingToolbarItem("", "Show/Hide layers tab", "Images/Layers.png", new RelayCommand((o) => DockManager.AddItem(((DockingToolbarItem)o).Location, new LayersViewModel(this))), DockManager, "UID_LevelEditor_Layers"),
            new DockingToolbarItem("", "Show/Hide instances tab", "Images/Instances.png", new RelayCommand((o) => DockManager.AddItem(((DockingToolbarItem)o).Location, new InstancesViewModel(this, selectedEntity))), DockManager, "UID_LevelEditor_Instances"),
            new DockingToolbarItem("", "Show/Hide properties tab", "Images/Properties.png", new RelayCommand((o) => DockManager.AddItem(((DockingToolbarItem)o).Location, new PropertiesViewModel(this, selectedEntity))), DockManager, "UID_LevelEditor_Properties"),
            new DockingToolbarItem("", "Show/Hide timeline editor", "Images/Timeline.png", new RelayCommand((o) => DockManager.AddItem(((DockingToolbarItem)o).Location, new TimelineViewModel(this))), DockManager, "UID_LevelEditor_Timeline"),
            new DockingToolbarItem("", "Show/Hide diagnostics (load report, log file)", DiagnosticsViewModel.IconPath, new RelayCommand((o) => DockManager.AddItem(((DockingToolbarItem)o).Location, new DiagnosticsViewModel())), DockManager, DiagnosticsViewModel.Uid),
            new DockingToolbarItem("", "Show/Hide validation (broken references in edits)", ValidationViewModel.IconPath, new RelayCommand((o) => DockManager.AddItem(((DockingToolbarItem)o).Location, new ValidationViewModel())), DockManager, ValidationViewModel.Uid),
            new FloatingOnlyDockingToolbarItem("", "Show/Hide schematics editor", "Images/Schematics.png", new RelayCommand((o) => DockManager.AddItem(((DockingToolbarItem)o).Location, new SchematicsViewModel(this, rootLayer))), DockManager, "UID_LevelEditor_Schematics"),
            new DividerToolbarItem(),
            //new RegularToolbarItem("", "Capture thumbnail preview", "LevelEditorPlugin/Images/CaptureThumbnail.png", new RelayCommand((o) => { CaptureThumbnail(viewport); })),
            //new ToggleToolbarItem("", "Show/Hide thumbnail safezone", "LevelEditorPlugin/Images/ThumbnailSafezone.png", false, new RelayCommand((o) => { ShowThumbnailSafeZone(thumbnailBorder.Visibility == Visibility.Collapsed, viewport); }))
        ];
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        viewport = GetTemplateChild(PART_Renderer) as FrostyViewport;
        viewport.Screen = screen;
    }

    public override void Closed()
    {
        DockManager.SaveToConfig("SpatialPrefabEditor");
        viewport.Shutdown();

        base.Closed();
    }

    private void TimedDockLoad(LoadReport report, string name, System.Func<Controls.IDockableItem> create)
    {
        Stopwatch t = Stopwatch.StartNew();
        DockManager.AddItemOnLoad(create());
        report.AddTiming($"UI: {name}", t.Elapsed.TotalMilliseconds);
    }

    protected override void Initialize()
    {
        Stopwatch timer = new Stopwatch();
        timer.Start();

        LoadReport report = LoadReport.Begin(AssetEntry.Name, AssetEntry.Type);
        LevelEditor.EnsureMeshVariationsLoaded(report);

        FrostyTaskWindow.Show($"Loading {Path.GetFileName(AssetEntry.Name)}", "", (task) =>
        {
          try
          {
            currentLoadingState = new LoadingStateInfo()
            {
                Task = task,
                Logger = logger
            };

            FrostySdk.Ebx.SpatialPrefabReferenceObjectData objectData = new FrostySdk.Ebx.SpatialPrefabReferenceObjectData()
            {
                Blueprint = new FrostySdk.Ebx.PointerRef(new FrostySdk.IO.EbxImportReference()
                {
                    FileGuid = Asset.FileGuid,
                    ClassGuid = Asset.RootInstanceGuid
                })
            };

            world = new EntityWorld();
            SpatialPrefabReferenceObject refObj = (Entities.SpatialPrefabReferenceObject)Entities.Entity.CreateEntity(objectData, Asset.FileGuid, world);

            rootLayer = refObj.GetLayer();

            report.EndEntityPhase(true);
            viewport.SetPaused(true);
            screen.AddEntity(refObj);
            screen.ShowTaskWindow = true;
            viewport.SetPaused(false);

            editingWorld = refObj;
            currentLoadingState = null;

            Stopwatch initTimer = Stopwatch.StartNew();
            world.Initialize();
            report.AddTiming("world.Initialize (entity world setup)", initTimer.Elapsed.TotalMilliseconds);
          }
          catch (System.Exception ex)
          {
            // Logged for diagnostics; behaviour is unchanged (the exception still propagates).
            LoadReport.ExceptionDuringLoad($"loading {AssetEntry.Name}", ex);
            report.EndEntityPhase(false);
            throw;
          }
        });

        timer.Stop();
        logger.Log($"Loaded spatial prefab in {timer.Elapsed.ToString()}");

        // 让Layers在Instance后加载
        TimedDockLoad(report, "Instances panel", () => new InstancesViewModel(this, null));
        TimedDockLoad(report, "Layers panel", () => new LayersViewModel(this));
        TimedDockLoad(report, "Properties panel", () => new PropertiesViewModel(this, editingWorld));
        TimedDockLoad(report, "Timeline panel", () => new TimelineViewModel(this));
        TimedDockLoad(report, "Schematics panel", () => new SchematicsViewModel(this, rootLayer));
        TimedDockLoad(report, "Diagnostics panel", () => new DiagnosticsViewModel());
        TimedDockLoad(report, "Validation panel", () => new ValidationViewModel());
    }
}
