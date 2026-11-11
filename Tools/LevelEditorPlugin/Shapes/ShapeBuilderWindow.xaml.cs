using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.BF1Core.Utils;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Entities;
using LevelEditorPlugin.Screens;
using SharpDX;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Entity = LevelEditorPlugin.Entities.Entity;

namespace LevelEditorPlugin.Shapes;

/// <summary>
/// Map &gt; Shape Builder: makes brand-new objects (box, plane, ramp, stairs, cylinder, corner)
/// with a chosen game material and MEC collision boxes, placed in front of the camera. After
/// placing, move/rotate them with the gizmos like any other object. See ShapeAssetBuilder.
/// </summary>
public partial class ShapeBuilderWindow : FrostyDockableWindow
{
    private const string RecentSurfacesKey = "LevelEditor.Shapes.RecentSurfaces";
    private const int MaxRecentSurfaces = 12;

    private static ShapeBuilderWindow instance;

    private readonly Dictionary<ShapeKind, RadioButton> kindButtons = new Dictionary<ShapeKind, RadioButton>();
    private ShapeKind kind = ShapeKind.Box;
    private bool ready;

    private static readonly (string Label, float Step)[] SnapSteps =
    {
        ("Off", 0f), ("0.05 m", 0.05f), ("0.1 m", 0.1f), ("0.25 m", 0.25f), ("0.5 m", 0.5f), ("1 m", 1f),
    };

    public static void ShowBuilder()
    {
        if (instance == null)
            instance = new ShapeBuilderWindow();
        if (!instance.IsVisible)
            instance.Show();
        if (instance.WindowState == WindowState.Minimized)
            instance.WindowState = WindowState.Normal;
        instance.Activate();
    }

    public ShapeBuilderWindow()
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow;

        foreach (ShapeKind k in Enum.GetValues(typeof(ShapeKind)))
        {
            RadioButton button = new RadioButton
            {
                Content = k.ToString(),
                GroupName = "ShapeKind",
                Margin = new Thickness(kindButtons.Count == 0 ? 0 : -1, 0, 0, 0),
                IsChecked = k == kind,
                Tag = k,
                ToolTip = KindHelp(k),
            };
            button.Checked += (s, e) =>
            {
                kind = (ShapeKind)((RadioButton)s).Tag;
                UpdateFields();
            };
            kindButtons[k] = button;
            Panel_Kinds.Children.Add(button);
        }

        foreach ((string label, float _) in SnapSteps)
            ComboBox_Snap.Items.Add(label);
        ComboBox_Snap.SelectedIndex = 3; // 0.25 m

        foreach (ShapeSurface surface in LoadRecentSurfaces())
            ComboBox_Surface.Items.Add(surface);
        if (ComboBox_Surface.Items.Count > 0)
            ComboBox_Surface.SelectedIndex = 0;

        ready = true;
        UpdateFields();
    }

    private static string KindHelp(ShapeKind k)
    {
        switch (k)
        {
            case ShapeKind.Box: return "A solid block: walls, floors, platforms, pillars.";
            case ShapeKind.Plane: return "A flat surface facing up (one-sided), with a collision slab under it.";
            case ShapeKind.Ramp: return "A wedge rising towards its back (+Z).";
            case ShapeKind.Stairs: return "Solid stairs rising towards their back (+Z).";
            case ShapeKind.Cylinder: return "A round column; width is its diameter.";
            case ShapeKind.Corner: return "An L-shaped wall piece: arms along the back and the left side.";
            default: return "";
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        instance = null;
    }

    // --- fields ----------------------------------------------------------------------------

    private void UpdateFields()
    {
        if (!ready)
            return;
        bool plane = kind == ShapeKind.Plane, cylinder = kind == ShapeKind.Cylinder;
        SetVisible(Label_Height, TextBox_Height, !plane);
        SetVisible(Label_Depth, TextBox_Depth, !cylinder);
        SetVisible(Label_Steps, TextBox_Steps, kind == ShapeKind.Stairs);
        SetVisible(Label_Segments, TextBox_Segments, cylinder);
        SetVisible(Label_Thickness, TextBox_Thickness, kind == ShapeKind.Corner);
        Label_Width.Text = cylinder ? "Diameter" : "Width (X)";
        UpdateInfo();
    }

    private static void SetVisible(UIElement label, UIElement field, bool visible)
    {
        label.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        field.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Field_Changed(object sender, EventArgs e)
    {
        if (ready)
            UpdateInfo();
    }

    private void UpdateInfo()
    {
        ShapeParameters p = ReadParameters(out string error);
        if (p == null)
        {
            Text_ShapeInfo.Text = error;
        }
        else
        {
            ShapeMesh mesh = ShapeGenerator.BuildMesh(p);
            int boxes = ShapeGenerator.BuildCollision(p).Count;
            Text_ShapeInfo.Text = $"{p.Width:0.##} × {(p.Kind == ShapeKind.Plane ? 0 : p.Height):0.##} × {(p.Kind == ShapeKind.Cylinder ? p.Width : p.Depth):0.##} m, {mesh.TriangleCount} triangles, {boxes} collision box(es). Origin at the bottom centre.";
        }

        ShapeSurface surface = ComboBox_Surface.SelectedItem as ShapeSurface;
        string warning = surface == null ? null : SurfaceWarning(surface);
        Text_SurfaceInfo.Text = surface == null
            ? "No surface yet: select an object in the level and press 'From selected object'."
            : $"Material '{surface.SectionName}' of {surface.MeshName}" + (warning != null ? "\n" + warning : "");
    }

    private static float Snap(float value, float step) => step > 0 ? (float)(Math.Round(value / step) * step) : value;

    private ShapeParameters ReadParameters(out string error)
    {
        error = null;
        float step = SnapSteps[Math.Max(0, ComboBox_Snap.SelectedIndex)].Step;
        if (!TryFloat(TextBox_Width.Text, out float width) || !TryFloat(TextBox_Height.Text, out float height) || !TryFloat(TextBox_Depth.Text, out float depth)
            || !TryFloat(TextBox_Thickness.Text, out float thickness) || !TryFloat(TextBox_TextureScale.Text, out float textureScale)
            || !int.TryParse(TextBox_Steps.Text, out int steps) || !int.TryParse(TextBox_Segments.Text, out int segments))
        {
            error = "Enter numbers (metres) in every field.";
            return null;
        }
        ShapeParameters p = new ShapeParameters
        {
            Kind = kind,
            Width = Snap(width, step),
            Height = kind == ShapeKind.Plane ? 0 : Snap(height, step),
            Depth = kind == ShapeKind.Cylinder ? Snap(width, step) : Snap(depth, step),
            Thickness = Snap(thickness, step),
            Steps = steps,
            Segments = segments,
            TextureScale = textureScale,
        };
        error = ShapeGenerator.Validate(p);
        return error == null ? p : null;
    }

    private static bool TryFloat(string text, out float value) =>
        float.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // --- surface ---------------------------------------------------------------------------

    private void Button_FromSelection_Click(object sender, RoutedEventArgs e)
    {
        Entity selected = BF1Core.MainWindow.SelectedEntity;
        if (selected == null)
        {
            Text_Status.Text = "Select an object in the level first (click it in the view).";
            return;
        }
        Assets.MeshAsset mesh = FindMesh(selected);
        if (mesh == null)
        {
            Text_Status.Text = $"{selected.DisplayName} has no static mesh to take a material from. Pick a wall, floor or prop.";
            return;
        }

        List<ShapeSurface> surfaces;
        try
        {
            surfaces = SurfacesOf(mesh);
        }
        catch (Exception ex)
        {
            Text_Status.Text = "Could not read the mesh's materials: " + ex.Message;
            LevelEditorLog.Warning("Shapes", $"Reading materials of {mesh.Name} failed", ex);
            return;
        }
        if (surfaces.Count == 0)
        {
            Text_Status.Text = $"{Path.GetFileName(mesh.Name)} is not a rigid mesh with named materials; pick another object.";
            return;
        }

        // the selected mesh's materials first, then the recent ones
        List<ShapeSurface> recent = ComboBox_Surface.Items.OfType<ShapeSurface>().Where(r => r.MeshGuid != mesh.FileGuid).ToList();
        ComboBox_Surface.Items.Clear();
        foreach (ShapeSurface s in surfaces.Concat(recent))
            ComboBox_Surface.Items.Add(s);
        ComboBox_Surface.SelectedIndex = 0;
        Text_Status.Text = $"{surfaces.Count} material(s) of {Path.GetFileName(mesh.Name)} added to the list.";
    }

    /// <summary>The first static mesh at or below an entity (prefabs, static model group members).</summary>
    private static Assets.MeshAsset FindMesh(Entity start)
    {
        Queue<(Entity, int)> queue = new Queue<(Entity, int)>();
        queue.Enqueue((start, 0));
        int visited = 0;
        while (queue.Count > 0 && visited++ < 2000)
        {
            (Entity entity, int depth) = queue.Dequeue();
            switch (entity)
            {
                case StaticModelEntity model when model.Mesh != null:
                    return model.Mesh;
                case MeshProxyEntity proxy when proxy.Mesh != null:
                    return proxy.Mesh;
                case StaticModelGroupElementEntity element when element.Inner != null:
                    queue.Enqueue((element.Inner, depth + 1));
                    break;
                case ReferenceObject reference when depth < 6:
                    foreach (Entity child in reference.GetAllEntity())
                        queue.Enqueue((child, depth + 1));
                    break;
            }
        }
        return null;
    }

    private static List<ShapeSurface> SurfacesOf(Assets.MeshAsset mesh)
    {
        List<ShapeSurface> result = new List<ShapeSurface>();
        EbxAssetEntry entry = App.AssetManager.GetEbxEntry(mesh.FileGuid);
        if (entry == null || !FrostySdk.TypeLibrary.IsSubClassOf(entry.Type, "RigidMeshAsset"))
            return result;
        ResAssetEntry res = App.AssetManager.GetResEntry((ulong)mesh.Data.MeshSetResource);
        if (res == null)
            return result;
        MeshSetPlugin.Resources.MeshSet meshSet = App.AssetManager.GetResAs<MeshSetPlugin.Resources.MeshSet>(res);
        if (meshSet.Lods.Count == 0)
            return result;
        List<MeshSetPlugin.Resources.MeshSetSection> sections = meshSet.Lods[0].Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            if (string.IsNullOrEmpty(sections[i].Name))
                continue;
            result.Add(new ShapeSurface { MeshGuid = entry.Guid, MeshName = entry.Name, SectionIndex = i, SectionName = sections[i].Name });
        }
        return result;
    }

    /// <summary>Surfaces from special meshes (water, glass, decals, …) use shaders that may need more than plain geometry.</summary>
    internal static string SurfaceWarning(ShapeSurface surface)
    {
        string name = (surface.MeshName + " " + surface.SectionName).ToLowerInvariant();
        foreach (string word in new[] { "water", "glass", "decal", "emissive", "light", "ui/", "fx", "vista", "sky", "cloud", "foliage", "cable", "wire" })
        {
            if (name.Contains(word))
                return $"Warning: this looks like a special material ('{word}'); its shader may not work on a plain shape. Prefer a wall, floor or crate material.";
        }
        return null;
    }

    private static List<ShapeSurface> LoadRecentSurfaces()
    {
        string text = Config.Get(RecentSurfacesKey, "", ConfigScope.Game);
        return text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(ShapeSurface.Parse).Where(s => s != null && App.AssetManager.GetEbxEntry(s.MeshGuid) != null).ToList();
    }

    private static void RememberSurface(ShapeSurface surface)
    {
        List<ShapeSurface> list = LoadRecentSurfaces();
        list.RemoveAll(s => s.MeshGuid == surface.MeshGuid && s.SectionIndex == surface.SectionIndex);
        list.Insert(0, surface);
        Config.Add(RecentSurfacesKey, string.Join("\n", list.Take(MaxRecentSurfaces).Select(s => s.Serialize())), ConfigScope.Game);
        Config.Save();
    }

    // --- creating --------------------------------------------------------------------------

    private void Button_Create_Click(object sender, RoutedEventArgs e)
    {
        ShapeParameters p = ReadParameters(out string error);
        if (p == null)
        {
            Text_Status.Text = error;
            return;
        }
        ShapeSurface surface = ComboBox_Surface.SelectedItem as ShapeSurface;
        if (surface == null)
        {
            Text_Status.Text = "Pick a surface first: select an object in the level and press 'From selected object'.";
            return;
        }
        LevelEditorScreen screen = BF1Core.MainWindow.SelectedScreen ?? LevelEditorScreen.Active;
        LevelEditorCamera camera = LevelEditorScreen.EditorCamera;
        if (screen == null || camera == null)
        {
            Text_Status.Text = "Open a level in the level editor first (and click into its view once).";
            return;
        }
        bool intoRoot = Radio_TargetRoot.IsChecked == true;
        SpawnTarget target = intoRoot ? SpawnTarget.LevelRoot(screen) : SpawnTarget.SameAs(BF1Core.MainWindow.SelectedEntity);
        if (target == null)
        {
            Text_Status.Text = intoRoot ? "The level root was not found; is a level open?" : "Select an object in the level first (the shape goes into its layer), or choose 'Into the level root'.";
            return;
        }

        ShapeOptions options = new ShapeOptions
        {
            Collision = CheckBox_Collision.IsChecked == true,
            CantStand = CheckBox_CantStand.IsChecked == true,
            CantClimb = CheckBox_CantClimb.IsChecked == true,
            CantVault = CheckBox_CantVault.IsChecked == true,
            CantWallrun = CheckBox_CantWallrun.IsChecked == true,
        };

        Matrix placement = Placement(p, camera);
        Button_Create.IsEnabled = false;
        Text_Status.Text = "Creating…";
        try
        {
            // on the UI thread like other placing: entities, layers and panels are updated here
            ShapeResult result = ShapeAssetBuilder.Create(p, surface, options, target, screen, placement);
            RememberSurface(surface);
            screen.EditorProvider?.SelectEntity(result.Placed);
            Text_Status.Text = $"Created {Path.GetFileName(result.Prefab.Name)}. Move or rotate it with the gizmos (Ctrl+Z removes it). Bundles: the new assets were added to {target.Description}'s bundle.";
            App.NotificationManager.Show($"Created {Path.GetFileName(result.Prefab.Name)}");
        }
        catch (Exception ex)
        {
            Exception inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            // a half-made shape leaves unused new assets in the project; they do no harm
            Text_Status.Text = "Could not create the shape: " + inner.Message;
            LevelEditorLog.Error("Shapes", $"Creating {p.Describe()} failed", inner);
        }
        finally
        {
            Button_Create.IsEnabled = true;
        }
    }

    private void Button_RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (FrostyMessageBox.Show("Delete every shape from this project?\n\nPlaced shapes, their mesh variation entries and all shape assets are removed. Afterwards save the project and reopen the level.", "Shape Builder", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        try
        {
            Text_Status.Text = ShapeCleanup.RemoveAll();
        }
        catch (Exception ex)
        {
            Text_Status.Text = "Could not delete the shapes: " + ex.Message;
            LevelEditorLog.Error("Shapes", "Deleting all shapes failed", ex);
        }
    }

    /// <summary>In front of the camera, origin (bottom centre) so the shape's middle is in view.</summary>
    private Matrix Placement(ShapeParameters p, LevelEditorCamera camera)
    {
        Matrix view = camera.WorldMatrix;
        Vector3 eye = view.TranslationVector;
        Vector3 look = Vector3.Normalize(view.Backward); // the level view culls along Backward
        float size = Math.Max(p.Width, Math.Max(p.Height, p.Depth));
        float distance = TryFloat(TextBox_Distance.Text, out float d) && d > 0 ? d : Math.Max(4.0f, size * 1.5f);
        Vector3 center = eye + look * distance;
        Vector3 origin = center - new Vector3(0, p.Height / 2, 0);

        float yaw = 0;
        if (CheckBox_FaceCamera.IsChecked == true && Math.Abs(look.X) + Math.Abs(look.Z) > 1e-4f)
        {
            yaw = (float)Math.Atan2(look.X, look.Z);              // local +Z towards where the camera looks
            yaw = (float)(Math.Round(yaw / (Math.PI / 2)) * (Math.PI / 2));
        }
        return Matrix.RotationY(yaw) * Matrix.Translation(origin);
    }
}
