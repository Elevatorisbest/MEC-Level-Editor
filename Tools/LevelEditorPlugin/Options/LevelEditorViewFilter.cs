using Frosty.Core;
using LevelEditorPlugin.Render;
using LevelEditorPlugin.Render.Proxies;

namespace LevelEditorPlugin.Options;

/// <summary>
/// What the level editor viewport shows. Settings are saved per game in Frosty's config.
/// Hidden proxies are neither drawn nor clickable; nothing is removed from the level.
/// </summary>
public static class LevelEditorViewFilter
{
    private const string KeyBloom = "LevelEditorView_Bloom";
    private const string KeyVolumes = "LevelEditorView_CollisionVolumes";
    private const string KeyLightCards = "LevelEditorView_EmissiveOnlyMeshes";
    private const string KeyIcons = "LevelEditorView_HelperIcons";
    private const string KeyInstancing = "LevelEditorView_Instancing";
    private const string KeyUiMeshes = "LevelEditorView_UiMeshes";
    private const string KeyMoveSnap = "LevelEditorView_MoveSnapIndex";
    private const string KeyRotateSnap = "LevelEditorView_RotateSnapIndex";
    private const string KeyScaleSnap = "LevelEditorView_ScaleSnapIndex";

    /// <summary>Rotate snapping choices in degrees (0 = off).</summary>
    public static readonly float[] RotateSnapSteps = { 0.0f, 1.0f, 5.0f, 15.0f, 22.5f, 45.0f, 90.0f };
    /// <summary>Scale snapping choices as factor steps (0 = off).</summary>
    public static readonly float[] ScaleSnapSteps = { 0.0f, 0.05f, 0.1f, 0.25f, 0.5f };
    private static int? rotateSnapIndex, scaleSnapIndex;

    public static int RotateSnapIndex
    {
        get => rotateSnapIndex ??= Config.Get(KeyRotateSnap, 3, ConfigScope.Game);
        set
        {
            rotateSnapIndex = value;
            Config.Add(KeyRotateSnap, value, ConfigScope.Game);
            Config.Save();
            ApplySnapSettings();
        }
    }

    public static int ScaleSnapIndex
    {
        get => scaleSnapIndex ??= Config.Get(KeyScaleSnap, 0, ConfigScope.Game);
        set
        {
            scaleSnapIndex = value;
            Config.Add(KeyScaleSnap, value, ConfigScope.Game);
            Config.Save();
            ApplySnapSettings();
        }
    }

    private static float Pick(float[] steps, int index) => steps[System.Math.Max(0, System.Math.Min(index, steps.Length - 1))];

    /// <summary>Copies the saved snap settings to the gizmos.</summary>
    public static void ApplySnapSettings()
    {
        Screens.TranslateGizmo.SnapStep = Pick(MoveSnapSteps, MoveSnapIndex);
        Screens.RotateGizmo.SnapDegrees = Pick(RotateSnapSteps, RotateSnapIndex);
        Screens.ScaleGizmo.SnapStep = Pick(ScaleSnapSteps, ScaleSnapIndex);
    }

    /// <summary>Move snapping choices in metres (0 = off); index saved in config.</summary>
    public static readonly float[] MoveSnapSteps = { 0.0f, 0.05f, 0.1f, 0.25f, 0.5f, 1.0f, 2.0f, 4.0f };
    private static int? moveSnapIndex;

    public static int MoveSnapIndex
    {
        get => moveSnapIndex ??= Config.Get(KeyMoveSnap, 0, ConfigScope.Game);
        set
        {
            moveSnapIndex = value;
            Config.Add(KeyMoveSnap, value, ConfigScope.Game);
            Config.Save();
            ApplySnapSettings();
        }
    }
    private static bool? uiMeshes;

    /// <summary>Meshes with only UI/ shaders (in-game map, EchoPlaneBackground). Hidden by default.</summary>
    public static bool UiMeshes
    {
        get => uiMeshes ??= Config.Get(KeyUiMeshes, false, ConfigScope.Game);
        set => Set(KeyUiMeshes, ref uiMeshes, value);
    }
    private const string KeyArrayLayerMode = "LevelEditorView_ArrayLayerMode";

    /// <summary>
    /// Names for ArrayLayerMode (index = mode, see MecMeshFallback.hlsl ArrayLayer). Temporary,
    /// to find out which vertex data selects the layer of TA_ texture arrays on MEC.
    /// </summary>
    public static readonly string[] ArrayLayerModeNames =
    {
        "Auto (SubMaterialIndex 1st, else 0)",
        "SubMaterialIndex 1st",
        "SubMaterialIndex 2nd",
        "SubMaterialIndex 3rd",
        "SubMaterialIndex 4th",
        "UV0 X whole number",
        "UV0 Y whole number",
        "UV0 X whole, wrapped",
        "UV0 Y whole, wrapped",
        "UV0 X whole, negative wrapped",
        "UV0 Y whole, negative wrapped",
        "SubMaterialIndex 1st + UV0 X whole",
        "UV1 X",
        "Vertex colour R",
    };

    private static int? arrayLayerMode;

    public static int ArrayLayerMode
    {
        get => arrayLayerMode ??= Config.Get(KeyArrayLayerMode, 0, ConfigScope.Game);
        set
        {
            arrayLayerMode = value;
            Config.Add(KeyArrayLayerMode, value, ConfigScope.Game);
            Config.Save();
        }
    }

    // cached: read every frame for every visible proxy
    private static bool? bloom, volumes, lightCards, icons, instancing;

    /// <summary>Draw repeated meshes with one instanced draw (InstancedMeshBatcher). On by default.</summary>
    public static bool Instancing
    {
        get => instancing ??= Config.Get(KeyInstancing, true, ConfigScope.Game);
        set => Set(KeyInstancing, ref instancing, value);
    }

    /// <summary>Bloom post-process (glow around bright pixels). Off by default.</summary>
    public static bool Bloom
    {
        get => bloom ??= Config.Get(KeyBloom, false, ConfigScope.Game);
        set => Set(KeyBloom, ref bloom, value);
    }

    /// <summary>Collision boxes (OBBCollisionEntity, PamOBBCollisionEntity). Hidden by default.</summary>
    public static bool CollisionVolumes
    {
        get => volumes ??= Config.Get(KeyVolumes, false, ConfigScope.Game);
        set => Set(KeyVolumes, ref volumes, value);
    }

    /// <summary>
    /// Flat light cards (MeshRenderable.IsLightCard, e.g. Ground_Lights_01/02 that cover the streets
    /// in the editor, light-strip decals). Hidden by default.
    /// </summary>
    public static bool LightCards
    {
        get => lightCards ??= Config.Get(KeyLightCards, false, ConfigScope.Game);
        set => Set(KeyLightCards, ref lightCards, value);
    }

    /// <summary>Billboard icons for lights, locators, spawns etc. Shown by default.</summary>
    public static bool HelperIcons
    {
        get => icons ??= Config.Get(KeyIcons, true, ConfigScope.Game);
        set => Set(KeyIcons, ref icons, value);
    }

    private static void Set(string key, ref bool? cache, bool value)
    {
        cache = value;
        Config.Add(key, value, ConfigScope.Game);
        Config.Save();
    }

    /// <summary>True when the proxy's main mesh should not be drawn or picked.</summary>
    public static bool HidesMesh(RenderProxy proxy)
    {
        if (proxy is ModelRenderProxy model)
            return model.MeshData != null
                && ((!LightCards && model.MeshData.IsLightCard) || (!UiMeshes && model.MeshData.IsUiMesh));
        if (proxy is SpriteRenderProxy sprite)
            return sprite.IsCollisionVolume && !CollisionVolumes;
        return false;
    }

    /// <summary>True when the proxy's billboard icon should not be drawn or picked.</summary>
    public static bool HidesIcon(RenderProxy proxy)
    {
        return proxy is SpriteRenderProxy && !HelperIcons;
    }
}
