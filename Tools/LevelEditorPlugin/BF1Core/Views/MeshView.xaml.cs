using Frosty.Core;
using Frosty.Core.Managers;
using Frosty.Core.Viewport;
//using FrostySdk.Ebx;
using FrostySdk.IO;
using LevelEditorPlugin.BF1Core.Enums;
using LevelEditorPlugin.BF1Core.Native;
using LevelEditorPlugin.BF1Core.Utils;
using LevelEditorPlugin.Entities;
using LevelEditorPlugin.Managers;
using LevelEditorPlugin.Options;
using LevelEditorPlugin.Resources;
using LevelEditorPlugin.Screens;
using SharpDX;
using System;
using System.Collections.Generic;
using System.Linq;
using FrostySdk.Managers.Entries;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LevelEditorPlugin.Diagnostics;

namespace LevelEditorPlugin.BF1Core.Views;

/// <summary>
/// Interaction logic for MeshView.xaml
/// </summary>
public partial class MeshView : UserControl
{
    /// <summary>
    /// Entity selected in 3D window
    /// </summary>
    public Entity Entity { get; private set; }
    /// <summary>
    /// Level editor screen
    /// </summary>
    public LevelEditorScreen LevelEditorScreen { get; private set; }

    /// <summary>
    /// Copy model direction
    /// </summary>
    private CopyDirection copyDirection = CopyDirection.ZAxisAdd;

    private static readonly string[] ViewModeItems =
    [
        "Solid Color",
        "Default Lit",
        "Wireframe",
        "BaseColor",
        "SpecularColor",
        "Normals",
        "MaterialAO",
        "Smoothness",
        "Metallic",
        "Reflectance"
    ];

    public MeshView()
    {
        InitializeComponent();
        InitSpawnUi();

        HotKey.KeyDownEvent += HotKey_KeyDownEvent;
        HavokPhysicsData.PendingCommitChanged += OnPendingCommitChanged;

        ComboBox_ViewMode.ItemsSource = ViewModeItems;
        ComboBox_ViewMode.SelectedIndex = Math.Min(LevelEditorRenderingOptions.ViewModeIndex, ViewModeItems.Length - 1);

        ComboBox_RotateSnap.ItemsSource = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(LevelEditorViewFilter.RotateSnapSteps, v => v > 0 ? $"{v:0.#}°" : "Off"));
        ComboBox_RotateSnap.SelectedIndex = Math.Max(0, Math.Min(LevelEditorViewFilter.RotateSnapIndex, LevelEditorViewFilter.RotateSnapSteps.Length - 1));
        ComboBox_ScaleSnap.ItemsSource = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(LevelEditorViewFilter.ScaleSnapSteps, v => v > 0 ? $"{v:0.##}" : "Off"));
        ComboBox_ScaleSnap.SelectedIndex = Math.Max(0, Math.Min(LevelEditorViewFilter.ScaleSnapIndex, LevelEditorViewFilter.ScaleSnapSteps.Length - 1));
        SyncGizmoModeButtons();
        LevelEditorScreen.GizmoModeChanged += () => Dispatcher.BeginInvoke(new Action(SyncGizmoModeButtons));

        ComboBox_MoveSnap.ItemsSource = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(LevelEditorViewFilter.MoveSnapSteps, s => s > 0 ? $"{s:0.##} m" : "Off"));
        ComboBox_MoveSnap.SelectedIndex = Math.Max(0, Math.Min(LevelEditorViewFilter.MoveSnapIndex, LevelEditorViewFilter.MoveSnapSteps.Length - 1));

        ComboBox_ArrayLayer.ItemsSource = LevelEditorViewFilter.ArrayLayerModeNames;
        ComboBox_ArrayLayer.SelectedIndex = Math.Max(0, Math.Min(LevelEditorViewFilter.ArrayLayerMode, LevelEditorViewFilter.ArrayLayerModeNames.Length - 1));

        CheckBox_Bloom.IsChecked = LevelEditorViewFilter.Bloom;
        CheckBox_CollisionVolumes.IsChecked = LevelEditorViewFilter.CollisionVolumes;
        CheckBox_EmissiveOnly.IsChecked = LevelEditorViewFilter.LightCards;
        CheckBox_HelperIcons.IsChecked = LevelEditorViewFilter.HelperIcons;
        CheckBox_Instancing.IsChecked = LevelEditorViewFilter.Instancing;
        CheckBox_UiMeshes.IsChecked = LevelEditorViewFilter.UiMeshes;
        CheckBox_BlockAutomation.IsChecked = AutomationBlocker.Enabled;

        // Initialize terrain edit mode UI
        if (CheckBox_TerrainEditMode != null)
            CheckBox_TerrainEditMode.IsChecked = BF1Core.Utils.TerrainEditState.IsTerrainEditModeEnabled;
    }

    private void OnPendingCommitChanged()
    {
        Dispatcher.InvokeAsync(UpdateWriteHavokButtonState);
    }

    private bool syncingGizmoMode;

    private void SyncGizmoModeButtons()
    {
        syncingGizmoMode = true;
        Radio_GizmoMove.IsChecked = LevelEditorScreen.CurrentGizmoMode == GizmoMode.Move;
        Radio_GizmoRotate.IsChecked = LevelEditorScreen.CurrentGizmoMode == GizmoMode.Rotate;
        Radio_GizmoScale.IsChecked = LevelEditorScreen.CurrentGizmoMode == GizmoMode.Scale;
        syncingGizmoMode = false;
    }

    private void GizmoMode_Checked(object sender, RoutedEventArgs e)
    {
        if (syncingGizmoMode)
            return;
        if (sender == Radio_GizmoRotate) LevelEditorScreen.CurrentGizmoMode = GizmoMode.Rotate;
        else if (sender == Radio_GizmoScale) LevelEditorScreen.CurrentGizmoMode = GizmoMode.Scale;
        else LevelEditorScreen.CurrentGizmoMode = GizmoMode.Move;
    }

    private void ComboBox_RotateSnap_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ComboBox_RotateSnap.SelectedIndex >= 0)
            LevelEditorViewFilter.RotateSnapIndex = ComboBox_RotateSnap.SelectedIndex;
    }

    private void ComboBox_ScaleSnap_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ComboBox_ScaleSnap.SelectedIndex >= 0)
            LevelEditorViewFilter.ScaleSnapIndex = ComboBox_ScaleSnap.SelectedIndex;
    }

    private void ComboBox_MoveSnap_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ComboBox_MoveSnap.SelectedIndex >= 0)
            LevelEditorViewFilter.MoveSnapIndex = ComboBox_MoveSnap.SelectedIndex;
    }

    private void ComboBox_ArrayLayer_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ComboBox_ArrayLayer.SelectedIndex >= 0)
            LevelEditorViewFilter.ArrayLayerMode = ComboBox_ArrayLayer.SelectedIndex;
    }

    private void ViewFilter_Click(object sender, RoutedEventArgs e)
    {
        LevelEditorViewFilter.Bloom = CheckBox_Bloom.IsChecked == true;
        LevelEditorViewFilter.CollisionVolumes = CheckBox_CollisionVolumes.IsChecked == true;
        LevelEditorViewFilter.LightCards = CheckBox_EmissiveOnly.IsChecked == true;
        LevelEditorViewFilter.HelperIcons = CheckBox_HelperIcons.IsChecked == true;
        LevelEditorViewFilter.Instancing = CheckBox_Instancing.IsChecked == true;
        LevelEditorViewFilter.UiMeshes = CheckBox_UiMeshes.IsChecked == true;
    }

    // --- placing assets ---------------------------------------------------------------------

    private System.Windows.Threading.DispatcherTimer explorerTimer;
    private EbxAssetEntry shownExplorerAsset;

    private void InitSpawnUi()
    {
        RefreshSpawnList();
        SpawnHistory.Changed += () => Dispatcher.BeginInvoke(new Action(RefreshSpawnList));
        // the data explorer has no selection event we can reach from here; check twice a second
        explorerTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        explorerTimer.Tick += (s, e) =>
        {
            if (!IsVisible)
                return;
            EbxAssetEntry asset = App.SelectedAsset;
            if (asset == shownExplorerAsset)
                return;
            shownExplorerAsset = asset;
            TextBlock_ExplorerAsset.Text = asset == null ? "-" : $"{asset.Filename} ({asset.Type})";
            TextBlock_ExplorerAsset.ToolTip = asset?.Name;
        };
        explorerTimer.Start();
    }

    private void RefreshSpawnList()
    {
        string selected = ComboBox_SpawnList.SelectedItem as string;
        List<string> items = SpawnHistory.Favourites.Select(n => "\u2605 " + n)
            .Concat(SpawnHistory.Recent.Where(n => !SpawnHistory.IsFavourite(n)))
            .ToList();
        ComboBox_SpawnList.ItemsSource = items;
        if (selected != null && items.Contains(selected))
            ComboBox_SpawnList.SelectedItem = selected;
        else if (items.Count > 0)
            ComboBox_SpawnList.SelectedIndex = 0;
    }

    private void ShowSpawnStatus(string text)
    {
        TextBlock_SpawnStatus.Text = text ?? "";
        TextBlock_SpawnStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Place(EbxAssetEntry asset)
    {
        if (LevelEditorScreen == null)
        {
            ShowSpawnStatus("Open a level first.");
            return;
        }
        SpawnCheck check = SpawnUtil.Check(asset, Entity);
        if (!check.CanSpawn)
        {
            ShowSpawnStatus(check.Problem);
            return;
        }
        try
        {
            Entities.Entity placed = SpawnUtil.Spawn(asset, Entity, LevelEditorScreen);
            ShowSpawnStatus(check.BundleWarning != null ? "Placed. Warning: " + check.BundleWarning : null);
            App.NotificationManager.Show($"Placed {asset.Filename} at the camera (Ctrl+Z to undo)");
            LevelEditorScreen.EditorProvider?.SelectEntity(placed);
        }
        catch (Exception ex)
        {
            ShowSpawnStatus("Could not place it: " + ex.Message);
            LevelEditorLog.Error("Spawn", $"Placing {asset.Name} failed", ex);
        }
    }

    private void Button_PlaceExplorerAsset_Click(object sender, RoutedEventArgs e) => Place(App.SelectedAsset);

    private void Button_PlaceListAsset_Click(object sender, RoutedEventArgs e)
    {
        if (!(ComboBox_SpawnList.SelectedItem is string item))
        {
            ShowSpawnStatus("The list is empty: place something from the explorer first, or add a favourite.");
            return;
        }
        string name = item.StartsWith("\u2605 ") ? item.Substring(2) : item;
        EbxAssetEntry asset = App.AssetManager.GetEbxEntry(name);
        if (asset == null)
        {
            ShowSpawnStatus($"{name} was not found in this game.");
            return;
        }
        Place(asset);
    }

    private void Button_FavouriteExplorerAsset_Click(object sender, RoutedEventArgs e)
    {
        EbxAssetEntry asset = App.SelectedAsset;
        if (asset == null)
        {
            ShowSpawnStatus("Select an asset in the data explorer first.");
            return;
        }
        SpawnHistory.ToggleFavourite(asset.Name);
        App.NotificationManager.Show(SpawnHistory.IsFavourite(asset.Name) ? $"Added {asset.Filename} to favourites" : $"Removed {asset.Filename} from favourites");
    }

    private void CheckBox_BlockAutomation_Click(object sender, RoutedEventArgs e)
    {
        AutomationBlocker.Enabled = CheckBox_BlockAutomation.IsChecked == true;
        App.Logger.Log($"Level editor: blocking UI Automation {(AutomationBlocker.Enabled ? "on" : "off")} - takes effect after restarting Frosty");
    }

    private void ComboBox_ViewMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ComboBox_ViewMode.SelectedIndex >= 0)
        {
            LevelEditorRenderingOptions.ViewModeIndex = ComboBox_ViewMode.SelectedIndex;
        }
    }

    /// <summary>
    /// Handle hotkey press events
    /// </summary>
    private void HotKey_KeyDownEvent(Keys key)
    {
        if (UndoManager.Instance == null) return;

        this.Dispatcher.Invoke(() =>
        {
            // The hotkey poller reads the keyboard globally (GetAsyncKeyState). Ignore key
            // presses made while another application has focus, otherwise e.g. F5 in a web
            // browser would duplicate the selected mesh into the level.
            if (!System.Linq.Enumerable.Any(System.Linq.Enumerable.OfType<Window>(Application.Current.Windows), w => w.IsActive))
            {
                LevelEditorLog.Info("Hotkey", $"{key} ignored: Frosty is not the active application");
                return;
            }

            LevelEditorLog.Info("Hotkey", $"{key} pressed");
            switch (key)
            {
                case Keys.F1:
                    Button_SnapToCam_Click(null, null);
                    break;
            }
        });
    }

    /// <summary>
    /// Update selected entity info
    /// </summary>
    public void UpdateSelectedEntity(Entity entity, LevelEditorScreen screen)
    {
        Entity = entity;
        LevelEditorScreen = screen;

        UpdateUI();
    }

    /// <summary>
    /// Update UI
    /// </summary>
    public void UpdateUI()
    {
        updatingUi = true;
        try
        {
            UpdateUIFields();
        }
        finally
        {
            updatingUi = false;
            RememberShownTransform();
        }

        // Update terrain edit area UI (e.g. current selected vertex height)
        UpdateTerrainVertexUI();
    }

    private void UpdateUIFields()
    {
        if (Entity == null)
        {
            TextBlock_EntityName.Text = "Null";
            TextBlock_LayerName.Text = "Null";
            TextBlock_SubWorld.Text = "-";
            TextBlock_StreamWarning.Visibility = Visibility.Collapsed;

            TextBox_TransX.Text = "0.00000";
            TextBox_TransY.Text = "0.00000";
            TextBox_TransZ.Text = "0.00000";

            TextBox_RotateX.Text = "0.00000";
            TextBox_RotateY.Text = "0.00000";
            TextBox_RotateZ.Text = "0.00000";

            TextBox_ScaleX.Text = "1.00000";
            TextBox_ScaleY.Text = "1.00000";
            TextBox_ScaleZ.Text = "1.00000";

            UpdateWriteHavokButtonState();
        }
        else
        {
            TextBlock_EntityName.Text = Entity.DisplayName;
            TextBlock_LayerName.Text = Entity.Layer?.LayerName;
            try
            {
                TextBlock_SubWorld.Text = LevelEditorPlugin.Diagnostics.StreamingInspector.DescribeChain(Entity, out bool zoneStreamed);
                TextBlock_StreamWarning.Visibility = zoneStreamed ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception)
            {
                TextBlock_SubWorld.Text = "?";
                TextBlock_StreamWarning.Visibility = Visibility.Collapsed;
            }

            if (Entity is ISpatialEntity spatialEntity)
            {
                var matrix = spatialEntity.GetLocalTransform();
                _ = matrix.Decompose(out Vector3 scale, out _, out Vector3 translation);
                Vector3 euler = SharpDXUtils.ExtractEulerAngles(matrix);

                TextBox_TransX.Text = translation.X.ToString("0.00000");
                TextBox_TransY.Text = translation.Y.ToString("0.00000");
                TextBox_TransZ.Text = translation.Z.ToString("0.00000");

                TextBox_RotateX.Text = euler.X.ToString("0.00000");
                TextBox_RotateY.Text = euler.Y.ToString("0.00000");
                TextBox_RotateZ.Text = euler.Z.ToString("0.00000");

                TextBox_ScaleX.Text = scale.X.ToString("0.00000");
                TextBox_ScaleY.Text = scale.Y.ToString("0.00000");
                TextBox_ScaleZ.Text = scale.Z.ToString("0.00000");
            }

            UpdateWriteHavokButtonState();
        }
    }

    /// <summary>
    /// Update enabled state of the "Write Havok Transform to Res" button.
    /// </summary>
    private void UpdateWriteHavokButtonState()
    {
        if (Button_WriteHavokTransformsToRes != null)
        {
            Button_WriteHavokTransformsToRes.IsEnabled = HavokPhysicsData.HasAnyPendingCommit;
        }
    }

    /// <summary>
    /// Update vertex height display in MeshView based on current terrain edit state and selected vertex.
    /// </summary>
    private void UpdateTerrainVertexUI()
    {
        if (TextBox_SelectedVertexHeight == null || TextBox_NewVertexHeight == null)
            return;

        var selOpt = BF1Core.Utils.TerrainEditState.CurrentVertex;
        if (selOpt.HasValue && BF1Core.Utils.TerrainEditState.IsTerrainEditModeEnabled)
        {
            var sel = selOpt.Value;
            TextBox_SelectedVertexHeight.Text = sel.WorldPosition.Y.ToString("0.00000");

            // If new height is empty, default to current height for fine-tuning
            if (string.IsNullOrWhiteSpace(TextBox_NewVertexHeight.Text))
                TextBox_NewVertexHeight.Text = sel.WorldPosition.Y.ToString("0.00000");
        }
        else
        {
            TextBox_SelectedVertexHeight.Text = string.Empty;
            // Don't force clear NewVertexHeight; allows user to repeatedly apply same target height to multiple points
        }
    }

    // Transform fields. They used to write the transform on every TextChanged, including while
    // UpdateUI filled them in one by one after a selection change: selecting an object briefly gave
    // it a mix of its own and the previous object's values, re-rounded its rotation, marked its
    // layer as modified, and left a pending undo unit that a later drag committed. Now a refresh
    // never writes, and user edits are applied on Enter / leaving the box as one undo step, using
    // only the fields that were actually changed.
    private bool updatingUi;
    private readonly Dictionary<TextBox, string> shownTransformText = new Dictionary<TextBox, string>();

    private TextBox[] TransformBoxes => new[]
    {
        TextBox_TransX, TextBox_TransY, TextBox_TransZ,
        TextBox_RotateX, TextBox_RotateY, TextBox_RotateZ,
        TextBox_ScaleX, TextBox_ScaleY, TextBox_ScaleZ,
    };

    private void RememberShownTransform()
    {
        shownTransformText.Clear();
        foreach (TextBox box in TransformBoxes)
            shownTransformText[box] = box.Text;
    }

    private bool FieldChanged(TextBox box)
    {
        return !shownTransformText.TryGetValue(box, out string shown) || shown != box.Text;
    }

    private float FieldValue(TextBox box, float current)
    {
        return FieldChanged(box) && float.TryParse(box.Text, out float value) ? value : current;
    }

    private void TextBox_Transform_TextChanged(object sender, TextChangedEventArgs e)
    {
        // applied on Enter or when the box loses focus (TextBox_Transform_Commit)
    }

    private void TextBox_Transform_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyTransformFields();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            UpdateUI(); // discard the typed value
            e.Handled = true;
        }
    }

    private void TextBox_Transform_Commit(object sender, RoutedEventArgs e)
    {
        ApplyTransformFields();
    }

    /// <summary>Applies the changed transform fields to the selected entity as one undoable edit.</summary>
    private void ApplyTransformFields()
    {
        if (updatingUi || UndoManager.Instance == null || !(Entity is ISpatialEntity spatialEntity))
            return;

        bool anyChanged = false;
        foreach (TextBox box in TransformBoxes)
            anyChanged |= FieldChanged(box);
        if (!anyChanged)
            return;

        try
        {
            Matrix local = spatialEntity.GetLocalTransform();
            local.Decompose(out Vector3 scale, out Quaternion rotation, out Vector3 translation);

            translation = new Vector3(FieldValue(TextBox_TransX, translation.X), FieldValue(TextBox_TransY, translation.Y), FieldValue(TextBox_TransZ, translation.Z));

            if (FieldChanged(TextBox_RotateX) || FieldChanged(TextBox_RotateY) || FieldChanged(TextBox_RotateZ))
            {
                Vector3 euler = SharpDXUtils.ExtractEulerAngles(local);
                euler = new Vector3(
                    CoreUtil.NormalizeAngle(FieldValue(TextBox_RotateX, euler.X)),
                    CoreUtil.NormalizeAngle(FieldValue(TextBox_RotateY, euler.Y)),
                    CoreUtil.NormalizeAngle(FieldValue(TextBox_RotateZ, euler.Z)));
                float degToRad = (float)(Math.PI / 180.0);
                rotation = SharpDXUtils.CreateFromEulerAngles(euler.X * degToRad, euler.Y * degToRad, euler.Z * degToRad);
            }

            scale = new Vector3(FieldValue(TextBox_ScaleX, scale.X), FieldValue(TextBox_ScaleY, scale.Y), FieldValue(TextBox_ScaleZ, scale.Z));

            ApplyTransformAsOneEdit(spatialEntity, Matrix.Scaling(scale) * Matrix.RotationQuaternion(rotation) * Matrix.Translation(translation));
        }
        catch (Exception ex)
        {
            App.Logger.LogError($"Exception while modifying selected model coordinates: {ex.Message}");
        }

        UpdateUI();
    }

    /// <summary>
    /// Sets the entity's local transform as a single undo step (start an undo unit with the current
    /// transform, then commit it), discarding any stale pending unit first.
    /// </summary>
    private void ApplyTransformAsOneEdit(ISpatialEntity spatialEntity, Matrix newLocal)
    {
        UndoManager undo = UndoManager.Instance;
        if (undo == null)
            return;

        undo.PendingUndoUnit = null;
        spatialEntity.SetTransform(newLocal, suppressUpdate: true);
        spatialEntity.SetTransform(newLocal, suppressUpdate: false);
        spatialEntity.RequiresTransformUpdate = true;
        UpdateWriteHavokButtonState();
    }

    /// <summary>
    /// TextBox mouse wheel event
    /// </summary>
    private void TextBox_Transform_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (UndoManager.Instance == null) return;

        if (Entity == null)
            return;

        if (sender is TextBox textBox)
        {
            if (float.TryParse(textBox.Text, out float value))
            {
                float step;
                if (textBox.Name.StartsWith("TextBox_Rotate"))
                    step = 1.0f;
                else if (textBox.Name.StartsWith("TextBox_Scale"))
                    step = 0.1f;
                else
                    step = 0.1f; // translation

                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                    step *= 10.0f;
                if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                    step *= 0.1f;

                value += (e.Delta > 0) ? step : -step;

                textBox.Text = value.ToString("0.00000");

                if (CheckBox_UniformScale.IsChecked == true && textBox.Name.StartsWith("TextBox_Scale"))
                {
                    TextBox_ScaleX.Text = value.ToString("0.00000");
                    TextBox_ScaleY.Text = value.ToString("0.00000");
                    TextBox_ScaleZ.Text = value.ToString("0.00000");
                }

                ApplyTransformFields();
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Slider mouse wheel event
    /// </summary>
    private void Slider_Count_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (UndoManager.Instance == null) return;

        if (sender is Slider slider)
        {
            var value = slider.Value;

            if (value < slider.Minimum)
            {
                slider.Value = slider.Minimum;
                return;
            }
            if (value > slider.Maximum)
            {
                slider.Value = slider.Maximum;
                return;
            }

            var step = slider.SmallChange;
            value += (e.Delta > 0) ? step : -step;
            slider.Value = value;

            e.Handled = true;
        }
    }

    /// <summary>
    /// Move model to camera
    /// </summary>
    private void Button_SnapToCam_Click(object sender, RoutedEventArgs e)
    {
        if (UndoManager.Instance == null) return;

        if (Entity == null)
        {
            App.Logger.LogWarning("Current Entity is Null");
            return;
        }
        var camera = LevelEditorScreen.EditorCamera;
        if (camera == null)
        {
            App.Logger.LogWarning("Current level editor camera not found");
            return;
        }

        Button_SnapToCam.IsEnabled = false;

        if (Entity is ISpatialEntity spatialEntity)
        {
            var cameraPos = camera.WorldMatrix.TranslationVector;

            var matrix = spatialEntity.GetLocalTransform();
            _ = matrix.Decompose(out Vector3 scale, out Quaternion quaternion, out _);

            var newMatrix = Matrix.Scaling(scale) *
                Matrix.RotationQuaternion(quaternion) *
                Matrix.Translation(cameraPos);
            ApplyTransformAsOneEdit(spatialEntity, newMatrix);
            UpdateUI();
        }

        Button_SnapToCam.IsEnabled = true;

        CoreUtil.PlayAudio();
    }

    /// <summary>
    /// Reset rotation
    /// </summary>
    private void Button_ResetRotate_Click(object sender, RoutedEventArgs e)
    {
        TextBox_RotateX.Text = "0.0";
        TextBox_RotateY.Text = "0.0";
        TextBox_RotateZ.Text = "0.0";
        ApplyTransformFields();
    }

    /// <summary>
    /// Reset scale
    /// </summary>
    private void Button_ResetScale_Click(object sender, RoutedEventArgs e)
    {
        TextBox_ScaleX.Text = "1.0";
        TextBox_ScaleY.Text = "1.0";
        TextBox_ScaleZ.Text = "1.0";
        ApplyTransformFields();
    }

    /// <summary>
    /// Copy model to camera
    /// </summary>
    private async void Button_CopyMeshToCamera_Click(object sender, RoutedEventArgs e)
    {
        if (UndoManager.Instance == null) return;

        if (Entity == null)
        {
            App.Logger.LogWarning("Current Entity is Null");
            return;
        }
        if (LevelEditorScreen == null)
        {
            App.Logger.LogWarning("Current LevelEditorScreen is Null");
            return;
        }

        Button_CopyMeshToCamera.IsEnabled = false;
        App.Logger.Log("=================================");

        App.Logger.Log("Starting copy model to camera...");

        try
        {
            await MeshUtil.CopySelectedMesh(new CopyMeshOption()
            {
                Entity = Entity,
                LevelEditorScreen = LevelEditorScreen,
                IsCopyToCamera = true
            });
        }
        catch (Exception ex)
        {
            App.Logger.LogError($"Exception while copying model to camera: {ex.Message}");
            LevelEditorLog.Error("Copy", "Exception while copying model to camera", ex);
        }
        finally
        {
            App.Logger.Log("Copy model to camera operation completed");
        }

        App.Logger.Log("=================================");
        Button_CopyMeshToCamera.IsEnabled = true;

        CoreUtil.PlayAudio();
    }

    /// <summary>
    /// Delete selected entity
    /// </summary>
    private async void Button_DeleteSelectedEntity_Click(object sender, RoutedEventArgs e)
    {
        if (UndoManager.Instance == null) return;

        if (Entity == null)
        {
            App.Logger.LogWarning("Current Entity is Null");
            return;
        }
        if (LevelEditorScreen == null)
        {
            App.Logger.LogWarning("Current LevelEditorScreen is Null");
            return;
        }

        Button_DeleteSelectedEntity.IsEnabled = false;
        App.Logger.Log("=================================");

        App.Logger.Log("Starting delete selected entity...");

        try
        {
            await MeshUtil.DeleteSelectedMesh(new DeleteMeshOption()
            {
                Entity = Entity,
                LevelEditorScreen = LevelEditorScreen
            });
        }
        catch (Exception ex)
        {
            App.Logger.LogError($"Exception while deleting selected entity: {ex.Message}");
            LevelEditorLog.Error("Delete", "Exception while deleting selected entity", ex);
        }
        finally
        {
            App.Logger.Log("Delete selected entity operation completed");
        }

        App.Logger.Log("=================================");
        Button_DeleteSelectedEntity.IsEnabled = true;

        CoreUtil.PlayAudio();
    }

    /// <summary>
    /// One-click flatten terrain: modify binary height data and write back to Res and Chunk, without viewport update.
    /// </summary>
    private void Button_FlattenTerrain_Click(object sender, RoutedEventArgs e)
    {
        if (LevelEditorScreen?.EditorProvider?.RootLayer == null)
        {
            App.Logger.LogWarning("Unable to get current level (please ensure map is opened in level editor)");
            return;
        }

        List<Entity> entities = [];
        LevelEditorScreen.EditorProvider.RootLayer.CollectEntities(entities);
        TerrainEntity terrainEntity = entities.Find(e => e is TerrainEntity) as TerrainEntity;
        if (terrainEntity == null)
        {
            App.Logger.LogWarning("Current level has no terrain entity");
            return;
        }

        Button_FlattenTerrain.IsEnabled = false;
        App.Logger.Log("=================================");
        App.Logger.Log("Starting one-click flatten terrain...");

        try
        {
            float worldHeightY = 0.0f;
            if (TextBox_FlattenTerrainHeight != null &&
                float.TryParse(TextBox_FlattenTerrainHeight.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
            {
                worldHeightY = parsed;
            }

            bool ok = TerrainFlattenHelper.FlattenTerrain(terrainEntity, worldHeightY);
            if (ok)
                App.Logger.Log("One-click flatten terrain completed; please manually reopen map to view changes");
            else
                App.Logger.LogWarning("One-click flatten terrain was not executed or failed");
        }
        catch (Exception ex)
        {
            App.Logger.LogError($"One-click flatten terrain exception: {ex.Message}");
        }
        finally
        {
            App.Logger.Log("=================================");
            Button_FlattenTerrain.IsEnabled = true;
        }

        CoreUtil.PlayAudio();
    }

    /// <summary>
    /// Toggle terrain edit mode.
    /// </summary>
    private void CheckBox_TerrainEditMode_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = CheckBox_TerrainEditMode.IsChecked == true;
        BF1Core.Utils.TerrainEditState.IsTerrainEditModeEnabled = enabled;

        if (!enabled)
        {
            BF1Core.Utils.TerrainEditState.ClearSelection();
        }

        UpdateTerrainVertexUI();
        App.Logger.Log(enabled ? "Terrain edit mode enabled" : "Terrain edit mode disabled");
    }

    /// <summary>
    /// Apply new world height to currently selected terrain vertices (realtime rendering only, not written to res/chunk).
    /// </summary>
    private void Button_ApplyVertexHeight_Click(object sender, RoutedEventArgs e)
    {
        if (!BF1Core.Utils.TerrainEditState.IsTerrainEditModeEnabled)
        {
            App.Logger.LogWarning("Terrain edit mode is not enabled; cannot edit terrain vertices");
            return;
        }

        var selOpt = BF1Core.Utils.TerrainEditState.CurrentVertex;
        if (!selOpt.HasValue)
        {
            App.Logger.LogWarning("No terrain vertex is currently selected");
            return;
        }

        if (!float.TryParse(TextBox_NewVertexHeight.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float newWorldY))
        {
            App.Logger.LogWarning("Unable to parse new world height; please enter a valid number");
            return;
        }

        var sel = selOpt.Value;
        var chunk = sel.Chunk;
        if (chunk == null || sel.VertexIndex < 0 || sel.VertexIndex >= chunk.TerrainVertices.Count)
        {
            App.Logger.LogWarning("Current vertex selection is invalid");
            return;
        }

        // World -> Local
        Vector3 worldPos = sel.WorldPosition;
        worldPos.Y = newWorldY;

        Matrix invWorld = Matrix.Invert(sel.Proxy.Transform);
        Vector3 newLocalPos = Vector3.TransformCoordinate(worldPos, invWorld);

        var verts = chunk.TerrainVertices;
        var v = verts[sel.VertexIndex];
        v.Position = newLocalPos;
        verts[sel.VertexIndex] = v;

        chunk.MarkVerticesDirty();

        // Update cached WorldPosition for UI display
        sel.WorldPosition = worldPos;
        BF1Core.Utils.TerrainEditState.CurrentVertex = sel;

        // Record pending height edits (based on Heightfield node Id + sample index + target world height)
        int sampleIndex = BF1Core.Utils.TerrainVertexPicker.GetSampleIndexForVertex(sel.VertexIndex, chunk.Dim);
        if (sampleIndex >= 0)
        {
            BF1Core.Utils.TerrainEditState.AddOrUpdateEdit(chunk.NodeId, sampleIndex, newWorldY);
        }

        UpdateTerrainVertexUI();

        App.Logger.Log($"Vertex height modified to world Y={newWorldY:0.00000} (realtime rendering only, not written to res/chunk)");
    }

    /// <summary>
    /// Write terrain height to Res/Chunk (placeholder; no actual write, prompt only).
    /// </summary>
    private void Button_CommitTerrainHeights_Click(object sender, RoutedEventArgs e)
    {
        if (LevelEditorScreen?.EditorProvider?.RootLayer == null)
        {
            App.Logger.LogWarning("Unable to get current level (please ensure map is opened in level editor)");
            return;
        }

        // Find TerrainEntity for current level
        List<Entity> entities = [];
        LevelEditorScreen.EditorProvider.RootLayer.CollectEntities(entities);
        TerrainEntity terrainEntity = entities.Find(ei => ei is TerrainEntity) as TerrainEntity;
        if (terrainEntity == null)
        {
            App.Logger.LogWarning("Current level has no terrain entity; cannot write terrain height");
            return;
        }

        App.Logger.Log("=================================");
        App.Logger.Log("Starting to write terrain vertex edits from current session to Chunk...");

        try
        {
            TerrainHeightCommitHelper.CommitPendingEdits(terrainEntity);
        }
        catch (Exception ex)
        {
            App.Logger.LogError($"Failed to write terrain to Chunk: {ex.Message}");
        }
        finally
        {
            App.Logger.Log("Write terrain to Chunk operation completed (first version only handles non-persistent nodes).");
            App.Logger.Log("=================================");
        }

        CoreUtil.PlayAudio();
    }

    /// <summary>
    /// Mouse wheel adjusts new world height of current selected vertex and immediately applies to vertex.
    /// </summary>
    private void TextBox_NewVertexHeight_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!TerrainEditState.IsTerrainEditModeEnabled)
            return;

        if (!TerrainEditState.CurrentVertex.HasValue)
            return;

        if (!float.TryParse(TextBox_NewVertexHeight.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
            value = TerrainEditState.CurrentVertex.Value.WorldPosition.Y;

        float step = 0.1f;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            step *= 10.0f;

        value += (e.Delta > 0) ? step : -step;

        TextBox_NewVertexHeight.Text = value.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture);

        // Apply to vertex immediately on wheel for real-time preview
        Button_ApplyVertexHeight_Click(null, null);

        e.Handled = true;
    }

    /// <summary>
    /// Write modified Havok transforms from StaticModelGroup back to resources via ModifyRes.
    /// </summary>
    private void Button_WriteHavokTransformsToRes_Click(object sender, RoutedEventArgs e)
    {
        if (!HavokPhysicsData.HasAnyPendingCommit)
        {
            App.Logger.Log("No pending Havok Transform modifications to write back");
            return;
        }

        Button_WriteHavokTransformsToRes.IsEnabled = false;
        int count = HavokPhysicsData.CommitAllPendingToRes();
        App.Logger.Log($"Wrote {count} Havok physics resource Transform modifications back to Res");
        Button_WriteHavokTransformsToRes.IsEnabled = false; // Disabled after write; no pending items
        CoreUtil.PlayAudio();
    }

    /// <summary>
    /// Fix OBBCollision scale
    /// </summary>
    private void Button_FixOBBCollisionScale_Click(object sender, RoutedEventArgs e)
    {
        if (UndoManager.Instance == null) return;

        if (Entity == null)
        {
            App.Logger.LogWarning("Current Entity is Null");
            return;
        }
        if (!(Entity is OBBCollisionEntity))
        {
            App.Logger.LogWarning("Current Entity is not OBBCollisionEntity or PamOBBCollisionEntity type");
            return;
        }

        Button_FixOBBCollisionScale.IsEnabled = false;
        App.Logger.Log("=================================");

        App.Logger.Log("Starting fix OBBCollision scale...");

        try
        {
            var ebxAssetEnity = App.AssetManager.GetEbxEntry(Entity.FileGuid);
            EbxAsset ebxAsset = LoadedAssetManager.Instance.GetEbxAsset(Entity.FileGuid);
            dynamic rootObject = ebxAsset.RootObject;

            foreach (dynamic itemRef in rootObject.Objects)
            {
                Guid guid = CoreUtil.GetInternalClassGuid(itemRef);
                if (guid == Entity.InstanceGuid)
                {
                    var obbCollision = itemRef.Internal;

                    // FrostySdk.Ebx.LinearTransform
                    Matrix matrix = SharpDXUtils.FromLinearTransform(obbCollision.Transform);
                    _ = matrix.Decompose(out Vector3 scale, out Quaternion quaternion, out Vector3 translation);

                    // Break loop when first is found
                    obbCollision.HalfExtents.x *= scale.X;
                    obbCollision.HalfExtents.y *= scale.Y;
                    obbCollision.HalfExtents.z *= scale.Z;

                    var newMatrix = Matrix.Scaling(1.0f, 1.0f, 1.0f) *
                        Matrix.RotationQuaternion(quaternion) *
                        Matrix.Translation(translation);

                    // FrostySdk.Ebx.LinearTransform
                    obbCollision.Transform = Entity.MakeLinearTransform(newMatrix);
                    break;
                }
            }

            // Update and save changes
            ebxAsset.Update();
            App.AssetManager.ModifyEbx(ebxAssetEnity.Name, ebxAsset);
        }
        catch (Exception ex)
        {
            App.Logger.LogError($"Exception while fixing OBBCollision scale: {ex.Message}");
        }
        finally
        {
            UpdateUI();
            TextBox_Transform_TextChanged(null, null);

            App.Logger.Log("Fix OBBCollision scale operation completed");
        }

        App.Logger.Log("=================================");
        Button_FixOBBCollisionScale.IsEnabled = true;

        CoreUtil.PlayAudio();
    }

    /// <summary>
    /// Select copy model direction
    /// </summary>
    private void RadioButton_XYZAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton radioButton)
        {
            switch (radioButton.Name)
            {
                case "RadioButton_XAxisAdd":
                    copyDirection = CopyDirection.XAxisAdd;
                    break;
                case "RadioButton_YAxisAdd":
                    copyDirection = CopyDirection.YAxisAdd;
                    break;
                case "RadioButton_ZAxisAdd":
                    copyDirection = CopyDirection.ZAxisAdd;
                    break;
                case "RadioButton_XAxisSub":
                    copyDirection = CopyDirection.XAxisSub;
                    break;
                case "RadioButton_YAxisSub":
                    copyDirection = CopyDirection.YAxisSub;
                    break;
                case "RadioButton_ZAxisSub":
                    copyDirection = CopyDirection.ZAxisSub;
                    break;
            }
        }
    }

    /// <summary>
    /// Print selection info
    /// </summary>
    private void Button_PrintSelectInfo_Click(object sender, RoutedEventArgs e)
    {
        if (UndoManager.Instance == null) return;

        if (Entity == null)
        {
            App.Logger.LogWarning("Current Entity is Null");
            return;
        }

        Button_PrintSelectInfo.IsEnabled = false;
        App.Logger.Log("=================================");

        App.Logger.Log($"Entity.DisplayName: {Entity.DisplayName}");
        App.Logger.Log($"Entity.Layer.LayerName: {Entity.Layer.LayerName}");
        App.Logger.Log($"Entity.FileGuid: {Entity.FileGuid}");
        App.Logger.Log($"Entity.InstanceGuid: {Entity.InstanceGuid}");

        App.Logger.Log("----------");
        App.Logger.Log($"Entity.Parent.DisplayName: {Entity.Parent?.DisplayName}");
        App.Logger.Log($"Entity.Owner.DisplayName: {Entity.Owner?.DisplayName}");

        App.Logger.Log("----------");
        App.Logger.Log($"Entity.Parent.Layer.LayerName: {Entity.Parent?.Layer?.LayerName}");
        App.Logger.Log($"Entity.Owner.Layer.LayerName: {Entity.Owner?.Layer?.LayerName}");

        App.Logger.Log("----------");
        var ebxAssetEnity = App.AssetManager.GetEbxEntry(Entity.FileGuid);
        App.Logger.Log($"EbxAssetEnity Name: {ebxAssetEnity.Name}");

        if (Entity is ISpatialEntity spatialEntity)
        {
            App.Logger.Log("----------");

            var matrix1 = spatialEntity.GetTransform();
            App.Logger.Log($"Entity GetTransform: \t\t{matrix1}");

            var matrix2 = spatialEntity.GetLocalTransform();
            App.Logger.Log($"Entity GetLocalTransform: \t{matrix2}");
        }

        if (Entity is StaticModelGroupElementEntity staticModelGroupElementEntity)
        {
            App.Logger.Log("----------");

            App.Logger.Log($"Current Entity is StaticModelGroupElementEntity type");
            var transform = staticModelGroupElementEntity.Data.Transform;
            App.Logger.Log(transform.ToString());
        }

        App.Logger.Log("=================================");
        Button_PrintSelectInfo.IsEnabled = true;

        CoreUtil.PlayAudio();
    }

    /// <summary>
    /// Copy model
    /// </summary>
    private async void Button_CopyMesh_Click(object sender, RoutedEventArgs e)
    {
        if (UndoManager.Instance == null) return;

        if (Entity == null)
        {
            App.Logger.LogWarning("Current Entity is Null");
            return;
        }
        if (LevelEditorScreen == null)
        {
            App.Logger.LogWarning("Current LevelEditorScreen is Null");
            return;
        }

        Button_CopyMesh.IsEnabled = false;
        UniformGrid_CopyDirection.IsEnabled = false;
        ProgressBar_TaskState.IsIndeterminate = true;
        App.Logger.Log("=================================");

        var stopwatch = new Stopwatch();
        stopwatch.Start();
        App.Logger.Log("Starting copy selected mesh...");

        try
        {
            // Copy count
            var count = (int)Slider_CopyCount.Value;
            // Copy spacing
            var step = (float)Slider_CopyStep.Value;

            await MeshUtil.CopySelectedMesh(new CopyMeshOption()
            {
                Entity = Entity,
                LevelEditorScreen = LevelEditorScreen,
                IsCopyToCamera = false,
                Count = count,
                Step = step,
                CopyDirection = copyDirection
            });
        }
        catch (Exception ex)
        {
            App.Logger.LogError($"Exception while copying selected mesh: {ex.Message}");
            LevelEditorLog.Error("Copy", "Exception while copying selected mesh", ex);
        }
        finally
        {
            stopwatch.Stop();
            App.Logger.Log($"Copy selected mesh operation completed, elapsed: {stopwatch.ElapsedMilliseconds}ms");
        }

        App.Logger.Log("=================================");
        Button_CopyMesh.IsEnabled = true;
        UniformGrid_CopyDirection.IsEnabled = true;
        ProgressBar_TaskState.IsIndeterminate = false;

        CoreUtil.PlayAudio();
    }
}
