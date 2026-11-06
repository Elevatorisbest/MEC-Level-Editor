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

    // cached: read every frame for every visible proxy
    private static bool? bloom, volumes, lightCards, icons;

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
    /// Meshes whose materials only have emissive textures (e.g. Ground_Lights_01/02: distant city
    /// light cards that cover the streets in the editor). Hidden by default.
    /// </summary>
    public static bool EmissiveOnlyMeshes
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
            return !EmissiveOnlyMeshes && model.MeshData != null && model.MeshData.IsEmissiveOnly;
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
