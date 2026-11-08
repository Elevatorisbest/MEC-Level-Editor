using Frosty.Core;
using Frosty.Core.Managers;
using LevelEditorPlugin.BF1Core.Utils;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Entities;
using LevelEditorPlugin.Layers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace LevelEditorPlugin.Editors;

public class InstancesViewModel : Controls.IDockableItem, INotifyPropertyChanged
{
    public string Header => "Instances";
    public string UniqueId => "UID_LevelEditor_Instances";
    public string Icon => "Images/Instances.png";
    public IEnumerable<Entities.Entity> Instances
    {
        get => m_instances;
        set
        {
            if (m_instances != value)
            {
                m_instances = value;
                NotifyPropertyChanged();
            }
        }
    }
    public ICommand EntityDoubleClickedCommand { get; private set; }
    public Entities.Entity SelectedEntity
    {
        get => m_selectedEntity;
        set
        {
            if (m_selectedEntity != value)
            {
                // Selecting is not an undo step: it used to be one, and committing it cleared the
                // redo history, so Redo stopped working after a click in this list.
                m_selectedEntity = value;
                SelectedEntityChanged(m_selectedEntity);
                NotifyPropertyChanged();
            }
        }
    }

    public string FilterText
    {
        get => m_filterText;
        set
        {
            if (m_filterText != value)
            {
                m_filterText = value;
                NotifyPropertyChanged();
                ApplyFilter();
            }
        }
    }

    private IEditorProvider m_owner;
    private Entities.Entity m_selectedEntity;
    private IEnumerable<Entities.Entity> m_instances;

    private string m_filterText;
    private List<Entities.Entity> m_allEntities;

    public InstancesViewModel(IEditorProvider inOwner, Entity inCurrentSelection)
    {
        m_owner = inOwner;
        m_owner.Screen.SelectedEntityChanged += SelectedEntityChangedFromScreen;

        List<Layers.SceneLayer> layers = [];
        m_owner.RootLayer.CollectLayers(layers);

        EntityDoubleClickedCommand = new RelayCommand(EntityDoubleClicked);
        m_selectedEntity = inCurrentSelection;

        foreach (SceneLayer layer in layers)
            layer.VisibilityChanged += SceneLayerVisibilityChanged;

        UpdateEntityInstances();

        ////////////////

        MeshUtil.LayerReloaded += ScheduleRefresh;
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            Instances = m_allEntities;
            return;
        }

        var filter = FilterText.Trim();
        // a list, not a lazy query: the list box and the count binding enumerated the query again
        // on every access (over every entity in the level)
        Instances = m_allEntities
            .Where(e => e.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
    }

    private void SelectedEntityChangedFromScreen(object sender, Screens.SelectedEntityChangedEventArgs e)
    {
        SelectedEntity = e.Entity;
    }

    private void UpdateEntityInstances()
    {
        var entities = new List<Entity>();

        // the editor's own layer tree (see LayersViewModel.ReloadLayer; rebuilding it with
        // GetLayer() hid every non-AutoLoad sub-world)
        SceneLayer entityLayer = m_owner.RootLayer;
        if (entityLayer == null)
            return;

        // 获取最新的Entity集合
        entityLayer.CollectEntities(entities);

        Instances = entities;
        m_allEntities = entities;

        LevelEditorLog.Debug("Instances", $"Instances list rebuilt: {entities.Count} entities");
    }

    private void SceneLayerVisibilityChanged(object sender, Layers.LayerVisibilityChangedEventArgs e)
    {
        // showing/hiding a layer raises this once for every layer below it (hundreds for the
        // whole level), so the list is rebuilt once afterwards instead of each time
        ScheduleRefresh();
    }

    private bool refreshPending;

    private void ScheduleRefresh()
    {
        if (refreshPending)
            return;
        refreshPending = true;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            refreshPending = false;
            UpdateEntityInstances();
            ApplyFilter();
            NotifyPropertyChanged("Instances");
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void EntityDoubleClicked(object state)
    {
        SelectedEntity = state as Entities.Entity;
        m_owner.CenterOnSelection();

        // 双击时，当前实体所在Layer高亮
        if (SelectedEntity.Layer != null)
            SelectedEntity.Layer.IsSelected = true;
    }

    private void SelectedEntityChanged(Entities.Entity newSelection)
    {
        m_owner.SelectEntity(newSelection);
    }

    #region -- INotifyPropertyChanged --
    public event PropertyChangedEventHandler PropertyChanged;
    private void NotifyPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] String propertyName = "")
    {
        if (PropertyChanged != null)
        {
            PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    #endregion
}
