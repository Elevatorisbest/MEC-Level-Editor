using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.BF1Core.Utils;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Screens;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LevelEditorPlugin.AssetBrowser;

/// <summary>One asset in the browser's list.</summary>
public sealed class AssetTile : INotifyPropertyChanged
{
    private bool isSelected;
    private double size;

    public AssetTile(EbxAssetEntry entry, double size)
    {
        Entry = entry;
        this.size = size;
        IsPrefab = AssetCatalog.IsPrefab(entry);
    }

    public EbxAssetEntry Entry { get; }
    public bool IsPrefab { get; }
    public string Name => Entry.Filename;
    public string ToolTip => $"{Entry.Name}\n{Entry.Type}";
    public string KindText => IsPrefab ? "PREFAB" : "";
    public string StarText => SpawnHistory.IsFavourite(Entry.Name) ? "★" : "";

    /// <summary>Width of the tile and height of its picture, in pixels.</summary>
    public double Size
    {
        get => size;
        set
        {
            if (size == value)
                return;
            size = value;
            Raise(nameof(Size));
        }
    }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
                return;
            isSelected = value;
            Raise(nameof(IsSelected));
        }
    }

    /// <summary>
    /// The picture. Only tiles that are on screen are bound, so reading this is what asks for a
    /// thumbnail to be loaded or drawn: assets that are never scrolled to cost nothing.
    /// </summary>
    public BitmapSource Thumbnail
    {
        get
        {
            BitmapSource image = ThumbnailService.TryGetLoaded(Entry.Guid);
            if (image == null)
                ThumbnailService.Request(Entry);
            return image;
        }
    }

    /// <summary>Shown in place of the picture while there is none.</summary>
    public string StatusText
    {
        get
        {
            if (ThumbnailService.TryGetLoaded(Entry.Guid) != null)
                return "";
            if (ThumbnailService.TryGetInfo(Entry.Guid, out ThumbnailInfo info))
            {
                switch (info.Status)
                {
                    case ThumbnailStatus.NoMesh: return "no mesh";
                    case ThumbnailStatus.Failed: return "failed";
                    default: return "...";
                }
            }
            return ThumbnailSettings.GenerateWhileBrowsing ? "..." : "not generated";
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void RefreshThumbnail()
    {
        Raise(nameof(Thumbnail));
        Raise(nameof(StatusText));
    }

    public void RefreshStar() => Raise(nameof(StarText));
}

/// <summary>One row of tiles. The list is virtualized by rows.</summary>
public sealed class AssetTileRow
{
    public List<AssetTile> Tiles { get; } = new List<AssetTile>();
}

/// <summary>
/// The asset browser: every placeable blueprint of the game as a thumbnail, with folders, search,
/// favourites and recents, a preview that can be turned, placing into the level, and control over
/// thumbnail generation and the cache.
/// </summary>
public partial class AssetBrowserWindow : FrostyDockableWindow
{
    private static AssetBrowserWindow instance;

    /// <summary>Opens the browser, or brings the open one to the front.</summary>
    public static void ShowBrowser()
    {
        if (instance == null)
        {
            using (PerfProbe.Start("Browser", "create asset browser window"))
                instance = new AssetBrowserWindow();
        }
        if (!instance.IsVisible)
            instance.Show();
        if (instance.WindowState == WindowState.Minimized)
            instance.WindowState = WindowState.Normal;
        instance.Activate();
    }

    private readonly bool ready;
    private List<EbxAssetEntry> allAssets;
    private List<EbxAssetEntry> listed = new List<EbxAssetEntry>();
    private readonly Dictionary<Guid, AssetTile> tiles = new Dictionary<Guid, AssetTile>();
    private string folder = "";
    private int columns;
    private double tileSize;
    private AssetTile selected;
    private bool changingFolderList;

    private readonly DispatcherTimer searchTimer;
    private readonly DispatcherTimer layoutTimer;
    private readonly DispatcherTimer statusTimer;

    // preview camera
    private float previewYaw = ThumbnailRenderer.DefaultYaw;
    private float previewPitch = ThumbnailRenderer.DefaultPitch;
    private float previewZoom = 1.0f;
    private bool previewDragging;
    private Point previewDragStart;
    private float previewDragYaw;
    private float previewDragPitch;
    private bool previewPending;
    private bool previewWanted;

    public AssetBrowserWindow()
    {
        InitializeComponent();

        tileSize = ThumbnailSettings.TileSize;
        Slider_TileSize.Value = tileSize;
        CheckBox_OnDemand.IsChecked = ThumbnailSettings.GenerateWhileBrowsing;
        CheckBox_KeyLight.IsChecked = ThumbnailSettings.KeyLight;
        Radio_Stored128.IsChecked = ThumbnailSettings.StoredSize == 128;
        Radio_Stored256.IsChecked = ThumbnailSettings.StoredSize != 128;
        TextBox_MaxCache.Text = ThumbnailSettings.MaxCacheMegabytes.ToString(CultureInfo.InvariantCulture);
        TextBox_CacheFolder.Text = ThumbnailSettings.Folder;
        ComboBox_BundleMode.ItemsSource = BundleAdder.LevelNames;
        ComboBox_BundleMode.SelectedIndex = BundleAdder.Level;

        searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        searchTimer.Tick += (s, e) =>
        {
            searchTimer.Stop();
            RefreshList();
        };
        layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        layoutTimer.Tick += (s, e) =>
        {
            layoutTimer.Stop();
            Relayout(false);
        };
        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        statusTimer.Tick += (s, e) => UpdateStatus();

        ThumbnailService.ThumbnailReady += OnThumbnailReady;
        SpawnHistory.Changed += OnSpawnHistoryChanged;

        // same shortcuts as the tools window: they act on the level editor's history and selection
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => Editors.SpatialEditor.Active?.UndoFromOutside(false)), Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => Editors.SpatialEditor.Active?.UndoFromOutside(true)), Key.Y, ModifierKeys.Control));

        ready = true;
        UpdateDetails();
        UpdateStatus();
        LoadCatalog();
    }

    private async void LoadCatalog()
    {
        try
        {
            allAssets = await AssetCatalog.LoadAsync();
        }
        catch (Exception ex)
        {
            LevelEditorLog.Error("Browser", "Listing the game's blueprints failed", ex);
            TextBlock_ListMessage.Text = "Listing the game's blueprints failed: " + ex.Message;
            return;
        }
        RefreshFolders();
        RefreshList();
    }

    // --- list ------------------------------------------------------------------------------

    private bool ViewIsAll => Radio_ViewAll.IsChecked == true;

    private AssetKinds SelectedKinds
    {
        get
        {
            AssetKinds kinds = AssetKinds.None;
            if (CheckBox_Objects.IsChecked == true)
                kinds |= AssetKinds.Objects;
            if (CheckBox_Prefabs.IsChecked == true)
                kinds |= AssetKinds.Prefabs;
            return kinds;
        }
    }

    /// <summary>The assets named in the favourites or recents list that still exist, in that order.</summary>
    private static List<EbxAssetEntry> FromNames(IEnumerable<string> names)
    {
        List<EbxAssetEntry> result = new List<EbxAssetEntry>();
        foreach (string name in names)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            if (entry != null)
                result.Add(entry);
        }
        return result;
    }

    private void RefreshFolders()
    {
        if (allAssets == null)
            return;
        changingFolderList = true;
        try
        {
            ListBox_Folders.ItemsSource = ViewIsAll ? AssetCatalog.SubFolders(allAssets, folder) : null;
            ListBox_Folders.IsEnabled = ViewIsAll;
            Button_FolderUp.IsEnabled = ViewIsAll && folder.Length > 0;
            Button_FolderRoot.IsEnabled = ViewIsAll && folder.Length > 0;
            TextBlock_Folder.Text = !ViewIsAll ? "(folders apply to 'All assets')" : folder.Length == 0 ? "(all folders)" : folder;
        }
        finally
        {
            changingFolderList = false;
        }
    }

    private void RefreshList()
    {
        if (allAssets == null)
            return;

        string search = TextBox_Search.Text?.Trim() ?? "";
        List<EbxAssetEntry> source;
        string viewFolder = "";
        if (Radio_ViewFavourites.IsChecked == true)
            source = FromNames(SpawnHistory.Favourites);
        else if (Radio_ViewRecent.IsChecked == true)
            source = FromNames(SpawnHistory.Recent);
        else
        {
            source = allAssets;
            viewFolder = folder;
        }

        List<EbxAssetEntry> result = AssetCatalog.Filter(source, viewFolder, search, SelectedKinds);
        if (CheckBox_HideNoMesh.IsChecked == true)
            result = result.Where(e => !(ThumbnailService.TryGetInfo(e.Guid, out ThumbnailInfo info) && info.Status == ThumbnailStatus.NoMesh)).ToList();
        listed = result;

        // requests for tiles that are about to disappear are no longer wanted
        ThumbnailService.CancelOnDemand();
        Relayout(true);

        if (listed.Count == 0)
        {
            TextBlock_ListMessage.Text = Radio_ViewFavourites.IsChecked == true && search.Length == 0
                ? "No favourites yet. Pick an asset and press the Favourite button."
                : Radio_ViewRecent.IsChecked == true && search.Length == 0
                    ? "Nothing placed yet. Assets you place show up here."
                    : "No asset matches.";
        }
        else
        {
            TextBlock_ListMessage.Text = "";
        }
        UpdateStatus();
    }

    /// <summary>
    /// Builds the rows of tiles for the current list and width. With <paramref name="listChanged"/>
    /// false, nothing is done unless the number of tiles per row changed.
    /// </summary>
    private void Relayout(bool listChanged)
    {
        double width = Items_Rows.ActualWidth - SystemParameters.VerticalScrollBarWidth - 4;
        // a tile is its width plus a 3 px margin on each side
        int newColumns = Math.Max(1, (int)Math.Floor(width / (tileSize + 6)));
        if (!listChanged && newColumns == columns)
            return;
        columns = newColumns;

        Guid selectedGuid = selected?.Entry.Guid ?? Guid.Empty;
        tiles.Clear();
        List<AssetTileRow> rows = new List<AssetTileRow>((listed.Count + columns - 1) / columns);
        AssetTileRow row = null;
        AssetTile reselect = null;
        foreach (EbxAssetEntry entry in listed)
        {
            if (row == null || row.Tiles.Count >= columns)
            {
                row = new AssetTileRow();
                rows.Add(row);
            }
            AssetTile tile = new AssetTile(entry, tileSize);
            if (entry.Guid == selectedGuid)
            {
                tile.IsSelected = true;
                reselect = tile;
            }
            row.Tiles.Add(tile);
            tiles[entry.Guid] = tile;
        }
        // the picked asset stays picked (details and preview) even when it is no longer listed
        if (reselect != null)
            selected = reselect;
        Items_Rows.ItemsSource = rows;
    }

    private void OnThumbnailReady(Guid asset)
    {
        if (tiles.TryGetValue(asset, out AssetTile tile))
            tile.RefreshThumbnail();
        if (selected != null && selected.Entry.Guid == asset)
            UpdateDetails();
    }

    private void OnSpawnHistoryChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (AssetTile tile in tiles.Values)
                tile.RefreshStar();
            if (!ViewIsAll)
                RefreshList();
            UpdateDetails();
        }));
    }

    private void TextBox_Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready)
            return;
        searchTimer.Stop();
        searchTimer.Start();
    }

    private void Button_ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        TextBox_Search.Text = "";
        TextBox_Search.Focus();
    }

    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready)
            return;
        RefreshFolders();
        RefreshList();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready)
            return;
        RefreshList();
    }

    private void Slider_TileSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready)
            return;
        tileSize = Math.Round(e.NewValue);
        ThumbnailSettings.TileSize = (int)tileSize;
        Relayout(true);
    }

    private void Items_Rows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!ready || !e.WidthChanged)
            return;
        layoutTimer.Stop();
        layoutTimer.Start();
    }

    // --- folders ---------------------------------------------------------------------------

    private void SetFolder(string newFolder)
    {
        folder = newFolder ?? "";
        RefreshFolders();
        RefreshList();
    }

    private void ListBox_Folders_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changingFolderList)
            return;
        if (ListBox_Folders.SelectedItem is AssetFolder sub)
            SetFolder(sub.FullPath);
    }

    private void Button_FolderUp_Click(object sender, RoutedEventArgs e) => SetFolder(AssetCatalog.ParentFolder(folder));

    private void Button_FolderRoot_Click(object sender, RoutedEventArgs e) => SetFolder("");

    // --- picking, details and preview ------------------------------------------------------

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!((sender as FrameworkElement)?.DataContext is AssetTile tile))
            return;
        Select(tile);
        if (e.ClickCount == 2)
            Place(tile.Entry);
        e.Handled = true;
    }

    private void Select(AssetTile tile)
    {
        if (selected == tile)
            return;
        bool otherAsset = selected == null || tile == null || selected.Entry.Guid != tile.Entry.Guid;
        if (selected != null)
            selected.IsSelected = false;
        selected = tile;
        if (selected != null)
            selected.IsSelected = true;
        ShowPlaceStatus(null);
        if (otherAsset)
        {
            previewYaw = ThumbnailRenderer.DefaultYaw;
            previewPitch = ThumbnailRenderer.DefaultPitch;
            previewZoom = 1.0f;
            Image_Preview.Source = null;
            TextBlock_PreviewSize.Text = "";
            TextBlock_PreviewMessage.Text = selected == null ? "Click an asset to see it here." : "Drawing...";
            // a request for the previous asset that has not started is replaced by this one
            previewPending = false;
            RequestPreview();
        }
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        bool has = selected != null;
        Button_Place.IsEnabled = has;
        Button_Favourite.IsEnabled = has;
        Button_CopyPath.IsEnabled = has;
        Button_Redraw.IsEnabled = has;
        if (!has)
        {
            TextBlock_AssetName.Text = "(nothing picked)";
            TextBox_AssetPath.Text = "";
            TextBlock_AssetInfo.Text = "";
            Button_Favourite.Content = "★ Favourite";
            return;
        }

        EbxAssetEntry entry = selected.Entry;
        TextBlock_AssetName.Text = entry.Filename;
        TextBox_AssetPath.Text = entry.Name;
        Button_Favourite.Content = SpawnHistory.IsFavourite(entry.Name) ? "★ Remove favourite" : "★ Favourite";

        string info = entry.Type;
        if (ThumbnailService.TryGetInfo(entry.Guid, out ThumbnailInfo stored))
        {
            switch (stored.Status)
            {
                case ThumbnailStatus.Ok:
                    info += $"\nSize: {FormatSize(stored.SizeX, stored.SizeY, stored.SizeZ)}\nMeshes: {stored.MeshCount}";
                    break;
                case ThumbnailStatus.NoMesh:
                    info += "\nNo mesh the editor can draw (logic, effect, sound or similar). It can still be placed.";
                    break;
                default:
                    info += "\nDrawing this asset failed; the level editor log has the reason.";
                    break;
            }
        }
        TextBlock_AssetInfo.Text = info;
    }

    private static string FormatSize(float x, float y, float z)
    {
        return string.Format(CultureInfo.InvariantCulture, "X={0:0.00} m  Y={1:0.00} m  Z={2:0.00} m", x, y, z);
    }

    /// <summary>
    /// Asks for the picked asset to be drawn from the current angle. Only one request is under
    /// way at a time; a change made meanwhile is drawn right after it.
    /// </summary>
    private void RequestPreview()
    {
        if (selected == null)
            return;
        if (previewPending)
        {
            previewWanted = true;
            return;
        }
        previewPending = true;
        previewWanted = false;
        EbxAssetEntry entry = selected.Entry;
        ThumbnailService.RequestPreview(entry, previewYaw, previewPitch, previewZoom, (image, result) => OnPreview(entry, image, result));
    }

    private void OnPreview(EbxAssetEntry entry, BitmapSource image, RenderResult result)
    {
        previewPending = false;
        if (selected != null && selected.Entry.Guid == entry.Guid)
        {
            switch (result.Status)
            {
                case ThumbnailStatus.Ok:
                    Image_Preview.Source = image;
                    TextBlock_PreviewMessage.Text = "";
                    TextBlock_PreviewSize.Text = "Size: " + FormatSize(result.Dimensions.X, result.Dimensions.Y, result.Dimensions.Z)
                        + (result.Truncated ? "\nLarge prefab: only part of it is drawn" : "");
                    break;
                case ThumbnailStatus.NoMesh:
                    Image_Preview.Source = null;
                    TextBlock_PreviewSize.Text = "";
                    TextBlock_PreviewMessage.Text = "This asset has no mesh the editor can draw.";
                    break;
                default:
                    Image_Preview.Source = null;
                    TextBlock_PreviewSize.Text = "";
                    TextBlock_PreviewMessage.Text = "Could not draw this asset.\n" + (result.Error ?? "");
                    break;
            }
        }
        if (previewWanted)
            RequestPreview();
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (selected == null)
            return;
        if (e.ClickCount == 2)
        {
            previewYaw = ThumbnailRenderer.DefaultYaw;
            previewPitch = ThumbnailRenderer.DefaultPitch;
            previewZoom = 1.0f;
            RequestPreview();
            return;
        }
        previewDragging = true;
        previewDragStart = e.GetPosition(Border_Preview);
        previewDragYaw = previewYaw;
        previewDragPitch = previewPitch;
        Border_Preview.CaptureMouse();
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!previewDragging)
            return;
        previewDragging = false;
        Border_Preview.ReleaseMouseCapture();
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!previewDragging)
            return;
        Point p = e.GetPosition(Border_Preview);
        // dragging across the whole picture turns the asset by about 230 degrees
        previewYaw = previewDragYaw - (float)((p.X - previewDragStart.X) / Border_Preview.ActualWidth * 4.0);
        previewPitch = Math.Max(-1.4f, Math.Min(1.4f, previewDragPitch + (float)((p.Y - previewDragStart.Y) / Border_Preview.ActualHeight * 3.0)));
        RequestPreview();
    }

    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (selected == null)
            return;
        previewZoom = Math.Max(0.25f, Math.Min(6.0f, previewZoom * (e.Delta > 0 ? 1.15f : 1.0f / 1.15f)));
        RequestPreview();
        e.Handled = true; // do not scroll the side panel
    }

    // --- placing ---------------------------------------------------------------------------

    private void ShowPlaceStatus(string text) => TextBlock_PlaceStatus.Text = text ?? "";

    private void Place(EbxAssetEntry asset)
    {
        if (asset == null)
        {
            ShowPlaceStatus("Pick an asset first.");
            return;
        }
        LevelEditorScreen screen = BF1Core.MainWindow.SelectedScreen ?? LevelEditorScreen.Active;
        if (screen == null)
        {
            ShowPlaceStatus("Open a level in the level editor first (and click into its view once).");
            return;
        }
        bool intoRoot = Radio_TargetRoot.IsChecked == true;
        SpawnTarget target = intoRoot ? SpawnTarget.LevelRoot(screen) : SpawnTarget.SameAs(BF1Core.MainWindow.SelectedEntity);
        if (target == null && !intoRoot)
        {
            ShowPlaceStatus("Select an object in the level first (the new one goes into its layer), or choose 'Into the level root'.");
            return;
        }
        SpawnCheck check = SpawnUtil.Check(asset, target);
        if (!check.CanSpawn)
        {
            ShowPlaceStatus(check.Problem);
            return;
        }
        try
        {
            bool addToBundle = BundleAdder.Level > 0;
            Entities.Entity placed = SpawnUtil.Spawn(asset, target, screen, addToBundle, out string bundleNote);
            ShowPlaceStatus(bundleNote == null ? "Placed." : addToBundle ? "Placed. It was not loaded there, so it was " + bundleNote : "Placed. Warning: " + bundleNote);
            App.NotificationManager.Show($"Placed {asset.Filename} at the camera (Ctrl+Z to undo)");
            screen.EditorProvider?.SelectEntity(placed);
            LevelEditorLog.Info("Spawn", $"Target: {target.Description}");
        }
        catch (Exception ex)
        {
            ShowPlaceStatus("Could not place it: " + ex.Message);
            LevelEditorLog.Error("Spawn", $"Placing {asset.Name} failed", ex);
        }
    }

    private void Button_Place_Click(object sender, RoutedEventArgs e) => Place(selected?.Entry);

    private void ComboBox_BundleMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && ComboBox_BundleMode.SelectedIndex >= 0)
            BundleAdder.Level = ComboBox_BundleMode.SelectedIndex;
    }

    private void Button_Favourite_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null)
            return;
        SpawnHistory.ToggleFavourite(selected.Entry.Name); // the list and button follow through SpawnHistory.Changed
    }

    private void Button_CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null)
            return;
        try
        {
            Clipboard.SetText(selected.Entry.Name);
            ShowPlaceStatus("Path copied.");
        }
        catch (Exception ex)
        {
            ShowPlaceStatus("Could not copy: " + ex.Message);
        }
    }

    private void Button_Redraw_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null)
            return;
        ThumbnailService.Invalidate(selected.Entry.Guid);
        selected.RefreshThumbnail();
        ThumbnailService.Request(selected.Entry, true);
        RequestPreview();
    }

    // --- thumbnail generation and cache ----------------------------------------------------

    private void CheckBox_OnDemand_Click(object sender, RoutedEventArgs e)
    {
        ThumbnailSettings.GenerateWhileBrowsing = CheckBox_OnDemand.IsChecked == true;
        if (!ThumbnailSettings.GenerateWhileBrowsing)
            ThumbnailService.CancelOnDemand();
        Relayout(true);
    }

    private void StartBatch(List<EbxAssetEntry> assets)
    {
        if (assets == null || assets.Count == 0)
            return;
        ThumbnailService.StartBatch(assets, false);
        UpdateStatus();
    }

    private void Button_GenerateListed_Click(object sender, RoutedEventArgs e) => StartBatch(listed);

    private void Button_GenerateAll_Click(object sender, RoutedEventArgs e) => StartBatch(allAssets);

    private void Button_PauseResume_Click(object sender, RoutedEventArgs e)
    {
        ThumbnailBatchProgress progress = ThumbnailService.BatchProgress;
        if (!progress.Active)
            return;
        if (progress.Paused)
            ThumbnailService.ResumeBatch();
        else
            ThumbnailService.PauseBatch();
        UpdateStatus();
    }

    private void Button_Stop_Click(object sender, RoutedEventArgs e)
    {
        ThumbnailService.StopBatch("Stopped. Thumbnails drawn so far are kept; a new run continues where this one ended.");
        UpdateStatus();
    }

    /// <summary>After a setting changed that selects another cache file: show that file's thumbnails.</summary>
    private void SettingsChanged()
    {
        ThumbnailService.ReloadSettings();
        Relayout(true);
        UpdateDetails();
        UpdateStatus();
    }

    private void StoredSize_Click(object sender, RoutedEventArgs e)
    {
        int size = Radio_Stored128.IsChecked == true ? 128 : 256;
        if (size == ThumbnailSettings.StoredSize)
            return;
        ThumbnailSettings.StoredSize = size;
        SettingsChanged();
    }

    private void CheckBox_KeyLight_Click(object sender, RoutedEventArgs e)
    {
        ThumbnailSettings.KeyLight = CheckBox_KeyLight.IsChecked == true;
        SettingsChanged();
        RequestPreview();
    }

    private void Button_ApplyLimit_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TextBox_MaxCache.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int megabytes) || megabytes <= 0)
        {
            TextBox_MaxCache.Text = ThumbnailSettings.MaxCacheMegabytes.ToString(CultureInfo.InvariantCulture);
            return;
        }
        ThumbnailSettings.MaxCacheMegabytes = megabytes;
        TextBox_MaxCache.Text = ThumbnailSettings.MaxCacheMegabytes.ToString(CultureInfo.InvariantCulture);
        SettingsChanged();
    }

    private void Button_ApplyFolder_Click(object sender, RoutedEventArgs e)
    {
        string path = TextBox_CacheFolder.Text.Trim();
        try
        {
            if (path.Length > 0)
            {
                path = Path.GetFullPath(path);
                Directory.CreateDirectory(path);
            }
        }
        catch (Exception ex)
        {
            FrostyMessageBox.Show("That folder cannot be used: " + ex.Message, "Asset Browser");
            return;
        }
        ThumbnailSettings.Folder = path; // empty = the default folder
        TextBox_CacheFolder.Text = ThumbnailSettings.Folder;
        SettingsChanged();
    }

    private void Button_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = ThumbnailSettings.Folder;
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
        }
        catch (Exception ex)
        {
            FrostyMessageBox.Show("Could not open the folder: " + ex.Message, "Asset Browser");
        }
    }

    private void Button_ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (FrostyMessageBox.Show("Delete all stored thumbnails of this game? They can be generated again.", "Asset Browser", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        long freed = ThumbnailService.ClearCache();
        Relayout(true);
        UpdateDetails();
        UpdateStatus();
        App.NotificationManager.Show($"Thumbnail cache cleared ({FormatBytes(freed)} freed)");
    }

    // --- status ----------------------------------------------------------------------------

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return string.Format(CultureInfo.InvariantCulture, "{0:0.00} GB", bytes / (1024.0 * 1024 * 1024));
        if (bytes >= 1024 * 1024)
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", bytes / (1024.0 * 1024));
        return string.Format(CultureInfo.InvariantCulture, "{0:0} KB", bytes / 1024.0);
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds >= 3600)
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} h", seconds / 3600);
        if (seconds >= 60)
            return string.Format(CultureInfo.InvariantCulture, "{0:0} min", seconds / 60);
        return string.Format(CultureInfo.InvariantCulture, "{0:0} s", seconds);
    }

    private void UpdateStatus()
    {
        if (!ready)
            return;

        // renderer
        bool canRender = ThumbnailService.CanRender;
        TextBlock_RendererState.Text = canRender
            ? ""
            : "Thumbnails and the preview are drawn by the level view. Open a level and keep its tab visible; drawing continues as soon as it is.";

        // batch
        ThumbnailBatchProgress progress = ThumbnailService.BatchProgress;
        Button_PauseResume.IsEnabled = progress.Active;
        Button_PauseResume.Content = progress.Paused ? "Continue" : "Pause";
        Button_Stop.IsEnabled = progress.Active;
        Button_GenerateListed.IsEnabled = listed.Count > 0;
        Button_GenerateAll.IsEnabled = allAssets != null && allAssets.Count > 0;
        Progress_Batch.Value = progress.Total > 0 ? (double)progress.Done / progress.Total : 0;
        if (progress.Active)
        {
            string text = $"{(progress.Paused ? "Paused" : "Generating")}: {progress.Done} of {progress.Total}"
                + $" ({progress.Rendered} drawn, {progress.NoMesh} without a mesh, {progress.Failed} failed, {progress.AlreadyCached} already stored)";
            if (progress.Rendered >= 5)
            {
                // estimate from what this run has measured so far; the share of assets that need
                // drawing is assumed to stay the same for the rest
                int looked = progress.Done - progress.AlreadyCached;
                int remaining = progress.Total - progress.Done;
                double drawnShare = looked > 0 ? (double)progress.Rendered / looked : 1.0;
                double remainingDrawn = remaining * drawnShare;
                text += $"\nAbout {FormatBytes(progress.AverageImageBytes)} and {progress.AverageMilliseconds:0} ms per thumbnail."
                    + $" Estimated for the rest: {FormatBytes((long)(remainingDrawn * progress.AverageImageBytes))} more on disk, {FormatDuration(remainingDrawn * progress.AverageMilliseconds / 1000.0)} of drawing.";
            }
            if (!progress.Paused && !string.IsNullOrEmpty(progress.Current))
                text += "\nNow: " + progress.Current;
            if (!canRender && !progress.Paused)
                text += "\nWaiting for the level view.";
            TextBlock_BatchStatus.Text = text;
        }
        else
        {
            TextBlock_BatchStatus.Text = progress.Message ?? "";
        }

        // cache
        ThumbnailCache cache = ThumbnailService.Cache;
        if (cache == null)
        {
            TextBlock_CacheInfo.Text = "The cache file could not be opened: " + ThumbnailService.CacheError
                + "\nThumbnails are drawn but not stored. Choose another folder below.";
        }
        else
        {
            long limit = ThumbnailSettings.MaxCacheMegabytes * 1024L * 1024L;
            TextBlock_CacheInfo.Text = $"{cache.Count} thumbnails stored, {FormatBytes(cache.FileBytes)} of {FormatBytes(limit)} used"
                + (cache.IsFull ? " - FULL, raise the limit to store more" : "")
                + $"\nAll cache files of this game: {FormatBytes(ThumbnailService.DiskBytes())}";
        }

        // status line
        int total = allAssets?.Count ?? 0;
        TextBlock_Status.Text = allAssets == null
            ? "Listing the game's blueprints..."
            : $"{listed.Count} listed of {total} placeable assets"
                + (ThumbnailRenderer.Instance.QueuedCount > 0 ? $"   |   {ThumbnailRenderer.Instance.QueuedCount} thumbnail(s) waiting to be drawn" : "");
    }

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!ready)
            return;
        if (IsVisible)
        {
            statusTimer.Start();
            UpdateStatus();
        }
        else
        {
            statusTimer.Stop();
            ThumbnailService.CancelOnDemand();
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // keep the window (list position, picked asset, a running generation) and just hide it
        e.Cancel = true;
        Hide();
    }
}
