# Asset browser

Branch `assetbrowser`. Open it from the **Map** menu ("Asset Browser") or with the button in the
tools window's asset section. Code: `Tools/LevelEditorPlugin/AssetBrowser/`.

## What it does

- Lists every placeable blueprint of the game (ObjectBlueprint and PrefabBlueprint assets) as
  thumbnails, with folders, search, type filters, favourites and recently placed.
- Thumbnails are standardised: same camera angle (framed from the asset's bounding box), same
  lighting, same backdrop, fixed exposure start. They are drawn with the level view's renderer and
  material shader, so they share its limits (building texture arrays, glass).
- Clicking an asset shows a 512 px preview that can be turned (drag), zoomed (wheel) and reset
  (double-click), with the asset's size in metres.
- Double-click an asset, or "Place at camera", to place it. Same target and Bundles options as the
  tools window.

## Thumbnails and the cache

- Thumbnails are drawn on the level view's render thread, at most one per frame. **A level has to
  be open with its tab visible**; otherwise requests wait.
- "Draw missing thumbnails while browsing": assets scrolled into view are drawn and stored.
- "Generate for listed assets" / "Generate for all assets": a run that can be paused, continued
  and stopped. Assets already stored are skipped, so a run continues where the last one ended,
  also after restarting Frosty. The status shows measured size and time per thumbnail and an
  estimate for the rest.
- One cache file per game profile and setting: `<folder>\<profile>_r<version>_<128|256>px_<key|nokey>.thumbs`.
  Default folder: `<drive of the editor>\FrostyLevelEditorCache\Thumbnails`, so it survives
  unpacking a new build. The folder, the size limit (default 1024 MB) and the stored size can be
  changed in the window. The file never grows past the limit; a run stops when it is reached.
- "Clear cache" deletes all thumbnail files of the game profile.
- Assets without a drawable mesh, and assets that failed, are stored as such and not drawn again
  ("Redraw thumbnail" retries one). If the editor dies while drawing an asset, that asset is
  marked failed at the next start.
- `ThumbnailSettings.RendererVersion` is part of the file name: bump it when the way thumbnails are
  drawn changes, and old pictures are no longer used.

## Not done

- Appearance (mesh variation) selector in the preview.
- "Will it show in game" marker per asset (depends on the target layer's bundles).
- Drag and drop from the browser into the level view.
