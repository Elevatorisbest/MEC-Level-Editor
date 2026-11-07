using Frosty.Core;
using LevelEditorPlugin.BF1Core.Utils;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Entities;
using LevelEditorPlugin.Layers;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace LevelEditorPlugin.Editors;

public class LayersViewModel : Controls.IDockableItem, INotifyPropertyChanged
{
    public string Header => "Layers";
    public string UniqueId => "UID_LevelEditor_Layers";
    public string Icon => "Images/Layers.png";

    private List<Layers.SceneLayer> m_layers;
    public List<Layers.SceneLayer> Layers
    {
        get => m_layers;
        set
        {
            if (m_layers != value)
            {
                m_layers = value;
                NotifyPropertyChanged();
            }
        }
    }

    private IEditorProvider owner;

    public LayersViewModel(IEditorProvider inOwner)
    {
        owner = inOwner;

        ///////////////////

        //Layers.Add(owner.RootLayer);

        //List<Layers.SceneLayer> layers = new List<Layers.SceneLayer>();
        //owner.RootLayer.CollectLayers(layers);

        //owner.RootLayer.SelectionChanged += SelectedLayerChanged;
        //foreach (SceneLayer layer in layers)
        //    layer.SelectionChanged += SelectedLayerChanged;

        ///////////////////

        ReloadLayer();

        ////////////////

        // copy/delete add or remove entities in the existing layers; the tree itself does not change
    }

    private void SelectedLayerChanged(object sender, Layers.LayerSelectionChangedEventArgs e)
    {
        if (e.Selected)
            owner.SelectLayer(e.Layer);

        LevelEditorLog.Debug("Layers", $"SelectedLayerChanged: {e.Layer.LayerName} {e.Layer.IsSelected}");
    }

    private void ReloadLayer()
    {
        // The editor's own layer tree. This used to call GetLayer() again, which built a second,
        // fresh tree: every sub-world that is not AutoLoad started hidden again (hiding the world
        // after each copy/delete), the Instances panel listened to a different tree than the one
        // shown here, and rebuilding took about a second.
        SceneLayer entityLayer = owner.RootLayer;
        if (entityLayer == null)
            return;

        {
            Layers = [entityLayer];
            NotifyPropertyChanged("Layers");
        }

        {
            // Every reload used to add the handler again, so after N copies/deletes one click on a
            // layer selected it N times. Remove first so each layer has it once.
            List<Layers.SceneLayer> layers = [];
            entityLayer.CollectLayers(layers);
            entityLayer.SelectionChanged -= SelectedLayerChanged;
            entityLayer.SelectionChanged += SelectedLayerChanged;
            foreach (SceneLayer layer in layers)
            {
                layer.SelectionChanged -= SelectedLayerChanged;
                layer.SelectionChanged += SelectedLayerChanged;
            }
        }
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
