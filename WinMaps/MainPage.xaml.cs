using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.Storage;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using WinMaps.Data;
using WinMaps.Download;
using WinMaps.Rendering;

namespace WinMaps
{
    // View model for My Maps list
    internal class DownloadedMapItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string StatusText { get; set; }
        public Visibility UseVisibility { get; set; }
    }

    // View model for Browse list
    internal class BrowseRegionItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Visibility DownloadVisibility { get; set; }
        public Visibility DrillVisibility { get; set; }

        private string _sizeText = "";
        public string SizeText
        {
            get => _sizeText;
            set { _sizeText = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SizeText))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    }

    // View model for Theme list
    internal class ThemeListItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ActiveMarker { get; set; }
        public Windows.UI.Color PreviewColor { get; set; }
        public Windows.UI.Color PreviewRoadColor { get; set; }
    }

    public sealed partial class MainPage : Page
    {
        private MapViewport _viewport;
        private MapRenderer _renderer;
        private MapDatabase _db;
        private MapDownloadManager _downloadManager;
        private CancellationTokenSource _cts;

        // GPS
        private Geolocator _geolocator;
        private double _gpsLat = double.NaN;
        private double _gpsLon = double.NaN;
        private double _gpsAccuracy = 0;
        private bool _followGps = false;
        private bool _initialGpsCentered = false;

        // Pan state
        private bool _isPanning = false;
        private Point _panStart;

        // Active region for download/import
        private MapRegion _selectedRegion;
        private string _activeMapId;

        // Map manager
        private GeofabrikIndex _geofabrikIndex;
        private Stack<string> _browseStack; // parent IDs for back navigation

        // Theme
        private MapTheme _currentTheme;

        public MainPage()
        {
            this.InitializeComponent();
            _viewport = new MapViewport();
            _downloadManager = new MapDownloadManager();
            _cts = new CancellationTokenSource();
            _geofabrikIndex = new GeofabrikIndex();
            _browseStack = new Stack<string>();
            _currentTheme = LoadSavedTheme();

            this.Loaded += MainPage_Loaded;
            this.Unloaded += MainPage_Unloaded;
            Application.Current.Suspending += (s, args) => SaveViewport();

            RestoreViewport();
            ApplyThemeBackground();

            Windows.UI.Core.SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
        }

        private void OnBackRequested(object sender, Windows.UI.Core.BackRequestedEventArgs e)
        {
            if (ThemePanel.Visibility == Visibility.Visible)
            {
                ThemePanel.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (MapManagerPanel.Visibility == Visibility.Visible)
            {
                if (BrowseView.Visibility == Visibility.Visible && _browseStack.Count > 0)
                {
                    // Navigate up in browse hierarchy
                    BtnBrowseBack_Click(null, null);
                }
                else if (BrowseView.Visibility == Visibility.Visible && MyMapsView.Visibility == Visibility.Collapsed)
                {
                    // If we came from My Maps, go back to it; otherwise close
                    var names = GetMapNames();
                    if (names.Count > 0)
                    {
                        BrowseView.Visibility = Visibility.Collapsed;
                        MyMapsView.Visibility = Visibility.Visible;
                        RefreshMyMapsList();
                    }
                    else
                    {
                        MapManagerPanel.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    MapManagerPanel.Visibility = Visibility.Collapsed;
                }
                e.Handled = true;
            }
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                OverlayPanel.Visibility = Visibility.Visible;
                TxtOverlayTitle.Text = "Startup Error";
                TxtOverlayStatus.Text = ex.ToString();
                BtnDownload.Visibility = Visibility.Collapsed;
            }
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Windows.UI.Core.SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            SaveViewport();
            _cts?.Cancel();
            _geolocator = null;
            _db?.Dispose();
            MapCanvas.RemoveFromVisualTree();
            MapCanvas = null;
        }

        private async Task InitializeAsync()
        {
            // Migrate any pre-existing .db files that aren't registered
            MigrateUnregisteredMaps();

            // Find the active map
            _activeMapId = GetActiveMapId();
            var mapNames = GetMapNames();

            if (_activeMapId != null && mapNames.ContainsKey(_activeMapId))
            {
                string dbPath = GetDbPathForMap(_activeMapId);
                if (File.Exists(dbPath))
                {
                    _db = new MapDatabase(dbPath);
                    try
                    {
                        await _db.OpenAsync();
                        if (_db.HasData())
                        {
                            _renderer = new MapRenderer(_db, _viewport, _currentTheme);

                            var bounds = _db.GetBounds();
                            if (bounds.HasValue && !HasSavedViewport())
                            {
                                _viewport.CenterLat = (bounds.Value.minLat + bounds.Value.maxLat) / 2.0;
                                _viewport.CenterLon = (bounds.Value.minLon + bounds.Value.maxLon) / 2.0;
                            }

                            OverlayPanel.Visibility = Visibility.Collapsed;
                            StartGps();
                            RedrawMap();
                            return;
                        }
                    }
                    catch
                    {
                        _db?.Dispose();
                        _db = null;
                    }
                }
            }

            // No valid active map — check if any maps exist
            if (mapNames.Count > 0)
            {
                // Try to use the first available map
                foreach (var kv in mapNames)
                {
                    string dbPath = GetDbPathForMap(kv.Key);
                    if (File.Exists(dbPath))
                    {
                        await SwitchToMap(kv.Key);
                        return;
                    }
                }
            }

            // No maps at all — show map manager with browse view
            ShowMapManager(startInBrowse: true);
        }

        // ---- Migration for pre-existing maps ----

        private void MigrateUnregisteredMaps()
        {
            string mapsPath = GetMapsFolder();
            var mapNames = GetMapNames();

            foreach (string dbFile in Directory.GetFiles(mapsPath, "*.db"))
            {
                // Extract map ID: "stuttgart-regbez.osm.db" → "stuttgart-regbez"
                string fileName = Path.GetFileName(dbFile);
                string mapId;
                if (fileName.EndsWith(".osm.db", StringComparison.OrdinalIgnoreCase))
                    mapId = fileName.Substring(0, fileName.Length - ".osm.db".Length);
                else
                    mapId = Path.GetFileNameWithoutExtension(fileName);

                if (!mapNames.ContainsKey(mapId))
                {
                    string displayName = mapId
                        .Replace("-", " ")
                        .Replace("_", " ");
                    // Capitalize first letter of each word
                    var parts = displayName.Split(' ');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i].Length > 0)
                            parts[i] = char.ToUpper(parts[i][0]) + parts[i].Substring(1);
                    }
                    displayName = string.Join(" ", parts);

                    SaveMapName(mapId, displayName);

                    // If no active map is set, use this one
                    if (GetActiveMapId() == null)
                    {
                        SetActiveMapId(mapId);
                    }
                }
            }
        }

        // ---- Map file helpers ----

        private string GetMapsFolder()
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            string mapsPath = Path.Combine(localFolder, "Maps");
            if (!Directory.Exists(mapsPath))
                Directory.CreateDirectory(mapsPath);
            return mapsPath;
        }

        private string GetDbPathForMap(string mapId)
        {
            return Path.Combine(GetMapsFolder(), mapId + ".osm.db");
        }

        private string GetPbfPathForMap(string mapId)
        {
            return Path.Combine(GetMapsFolder(), mapId + ".osm.pbf");
        }

        // ---- Map metadata in LocalSettings ----

        private string GetActiveMapId()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.ContainsKey("active_map_id"))
                return settings.Values["active_map_id"] as string;
            return null;
        }

        private void SetActiveMapId(string id)
        {
            _activeMapId = id;
            ApplicationData.Current.LocalSettings.Values["active_map_id"] = id;
        }

        private Dictionary<string, string> GetMapNames()
        {
            var result = new Dictionary<string, string>();
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.ContainsKey("map_names"))
            {
                string json = settings.Values["map_names"] as string;
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        var obj = Windows.Data.Json.JsonObject.Parse(json);
                        foreach (var kv in obj)
                        {
                            result[kv.Key] = kv.Value.GetString();
                        }
                    }
                    catch { }
                }
            }
            return result;
        }

        private void SaveMapName(string id, string name)
        {
            var names = GetMapNames();
            names[id] = name;
            SaveMapNamesDict(names);
        }

        private void RemoveMapName(string id)
        {
            var names = GetMapNames();
            names.Remove(id);
            SaveMapNamesDict(names);
        }

        private void SaveMapNamesDict(Dictionary<string, string> names)
        {
            var obj = new Windows.Data.Json.JsonObject();
            foreach (var kv in names)
            {
                obj[kv.Key] = Windows.Data.Json.JsonValue.CreateStringValue(kv.Value);
            }
            ApplicationData.Current.LocalSettings.Values["map_names"] = obj.Stringify();
        }

        // ---- Map Manager UI ----

        private void BtnMapManager_Click(object sender, RoutedEventArgs e)
        {
            ShowMapManager(startInBrowse: false);
        }

        private void ShowMapManager(bool startInBrowse)
        {
            MapManagerPanel.Visibility = Visibility.Visible;

            if (startInBrowse)
            {
                MyMapsView.Visibility = Visibility.Collapsed;
                BrowseView.Visibility = Visibility.Visible;
                ShowBrowseLevel(null); // show continents
            }
            else
            {
                MyMapsView.Visibility = Visibility.Visible;
                BrowseView.Visibility = Visibility.Collapsed;
                RefreshMyMapsList();
            }
        }

        private void BtnCloseManager_Click(object sender, RoutedEventArgs e)
        {
            MapManagerPanel.Visibility = Visibility.Collapsed;
        }

        // ---- My Maps ----

        private void RefreshMyMapsList()
        {
            var names = GetMapNames();
            var items = new List<DownloadedMapItem>();

            foreach (var kv in names)
            {
                string dbPath = GetDbPathForMap(kv.Key);
                if (!File.Exists(dbPath)) continue;

                bool isActive = kv.Key == _activeMapId;
                items.Add(new DownloadedMapItem
                {
                    Id = kv.Key,
                    Name = kv.Value,
                    StatusText = isActive ? "● Active" : "",
                    UseVisibility = isActive ? Visibility.Collapsed : Visibility.Visible
                });
            }

            LvMyMaps.ItemsSource = items;
            TxtNoMaps.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BtnUseMap_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            string mapId = btn.Tag as string;
            if (mapId == null) return;

            await SwitchToMap(mapId);
            MapManagerPanel.Visibility = Visibility.Collapsed;
        }

        private async Task SwitchToMap(string mapId)
        {
            // Dispose current
            _renderer = null;
            _db?.Dispose();
            _db = null;

            string dbPath = GetDbPathForMap(mapId);
            _db = new MapDatabase(dbPath);
            await _db.OpenAsync();

            _renderer = new MapRenderer(_db, _viewport, _currentTheme);
            SetActiveMapId(mapId);

            // Center on new map bounds
            var bounds = _db.GetBounds();
            if (bounds.HasValue)
            {
                _viewport.CenterLat = (bounds.Value.minLat + bounds.Value.maxLat) / 2.0;
                _viewport.CenterLon = (bounds.Value.minLon + bounds.Value.maxLon) / 2.0;
            }

            OverlayPanel.Visibility = Visibility.Collapsed;
            _initialGpsCentered = false;
            StartGps();
            RedrawMap();
        }

        private async void BtnDeleteMap_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            string mapId = btn.Tag as string;
            if (mapId == null) return;

            var names = GetMapNames();
            string displayName = names.ContainsKey(mapId) ? names[mapId] : mapId;

            var dialog = new MessageDialog($"Delete \"{displayName}\"? This will remove all map data.", "Delete Map");
            dialog.Commands.Add(new UICommand("Delete"));
            dialog.Commands.Add(new UICommand("Cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;

            var result = await dialog.ShowAsync();
            if (result.Label != "Delete") return;

            // If this is the active map, dispose it first
            if (mapId == _activeMapId)
            {
                _renderer = null;
                _db?.Dispose();
                _db = null;
                _activeMapId = null;
                ApplicationData.Current.LocalSettings.Values.Remove("active_map_id");
            }

            // Delete files
            await Task.Run(() =>
            {
                try
                {
                    string dbPath = GetDbPathForMap(mapId);
                    if (File.Exists(dbPath)) File.Delete(dbPath);
                    if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
                    if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");

                    string pbfPath = GetPbfPathForMap(mapId);
                    if (File.Exists(pbfPath)) File.Delete(pbfPath);

                    string partialPath = pbfPath + ".partial";
                    if (File.Exists(partialPath)) File.Delete(partialPath);
                }
                catch { }
            });

            RemoveMapName(mapId);
            RefreshMyMapsList();

            // If no maps left, switch to browse
            var remaining = GetMapNames();
            if (remaining.Count == 0)
            {
                MyMapsView.Visibility = Visibility.Collapsed;
                BrowseView.Visibility = Visibility.Visible;
                ShowBrowseLevel(null);
            }
            else if (_activeMapId == null)
            {
                // Switch to first available map
                foreach (var kv in remaining)
                {
                    string dbPath = GetDbPathForMap(kv.Key);
                    if (File.Exists(dbPath))
                    {
                        await SwitchToMap(kv.Key);
                        RefreshMyMapsList();
                        break;
                    }
                }
            }
        }

        // ---- Browse (Geofabrik catalog) ----

        private void BtnAddMap_Click(object sender, RoutedEventArgs e)
        {
            MyMapsView.Visibility = Visibility.Collapsed;
            BrowseView.Visibility = Visibility.Visible;
            _browseStack.Clear();
            ShowBrowseLevel(null); // show continents
        }

        private async void ShowBrowseLevel(string parentId)
        {
            if (!_geofabrikIndex.IsLoaded)
            {
                TxtBrowseTitle.Text = "Loading catalog...";
                LvBrowse.ItemsSource = null;
                try
                {
                    await _geofabrikIndex.LoadAsync();
                }
                catch (Exception ex)
                {
                    TxtBrowseTitle.Text = "Error loading catalog";
                    LvBrowse.ItemsSource = new List<BrowseRegionItem>
                    {
                        new BrowseRegionItem { Name = ex.Message, DownloadVisibility = Visibility.Collapsed, DrillVisibility = Visibility.Collapsed }
                    };
                    return;
                }
            }

            // Update title
            if (parentId == null)
            {
                TxtBrowseTitle.Text = "Select Region";
                BtnBrowseBack.Visibility = Visibility.Collapsed;
            }
            else
            {
                TxtBrowseTitle.Text = _geofabrikIndex.GetBreadcrumb(parentId);
                BtnBrowseBack.Visibility = Visibility.Visible;
            }

            var children = parentId == null
                ? _geofabrikIndex.GetRoots()
                : _geofabrikIndex.GetChildren(parentId);

            var downloadedNames = GetMapNames();
            var items = new List<BrowseRegionItem>();

            foreach (var region in children)
            {
                bool isContinent = _geofabrikIndex.IsContinent(region.Id);
                bool hasChildren = _geofabrikIndex.HasChildren(region.Id);
                bool alreadyDownloaded = downloadedNames.ContainsKey(region.Id);

                items.Add(new BrowseRegionItem
                {
                    Id = region.Id,
                    Name = alreadyDownloaded ? region.Name + " ✓" : region.Name,
                    DownloadVisibility = ((isContinent && hasChildren) || alreadyDownloaded) ? Visibility.Collapsed : Visibility.Visible,
                    DrillVisibility = hasChildren ? Visibility.Visible : Visibility.Collapsed
                });
            }

            LvBrowse.ItemsSource = items;

            // Fetch sizes asynchronously
            FetchRegionSizes(items, children);
        }

        private async void FetchRegionSizes(List<BrowseRegionItem> items, List<GeofabrikRegion> regions)
        {
            using (var client = new System.Net.Http.HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);

                foreach (var region in regions)
                {
                    var item = items.Find(i => i.Id == region.Id);
                    if (item == null || item.DownloadVisibility == Visibility.Collapsed || string.IsNullOrEmpty(region.PbfUrl))
                        continue;

                    try
                    {
                        var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, new Uri(region.PbfUrl));
                        var response = await client.SendAsync(request);
                        if (response.Content.Headers.ContentLength.HasValue)
                        {
                            long bytes = response.Content.Headers.ContentLength.Value;
                            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                            {
                                item.SizeText = FormatFileSize(bytes);
                            });
                        }
                    }
                    catch { }
                }
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
            if (bytes >= 1024L * 1024)
                return $"{bytes / (1024.0 * 1024):F0} MB";
            if (bytes >= 1024L)
                return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }

        private void BtnDrillInto_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            string regionId = btn.Tag as string;
            if (regionId == null) return;

            // Get current parent from the stack or null
            string currentParent = _browseStack.Count > 0 ? _browseStack.Peek() : null;

            // Find the actual current level parent (the parent of items currently shown)
            // We need to push the ID we're drilling into
            _browseStack.Push(regionId);
            ShowBrowseLevel(regionId);
        }

        private void BtnBrowseBack_Click(object sender, RoutedEventArgs e)
        {
            if (_browseStack.Count > 0)
            {
                _browseStack.Pop(); // remove current level
            }

            if (_browseStack.Count > 0)
            {
                ShowBrowseLevel(_browseStack.Peek());
            }
            else
            {
                ShowBrowseLevel(null); // back to continents
            }
        }

        private async void BtnRefreshCatalog_Click(object sender, RoutedEventArgs e)
        {
            TxtBrowseTitle.Text = "Refreshing catalog...";
            LvBrowse.ItemsSource = null;
            try
            {
                await _geofabrikIndex.RefreshAsync();

                // Re-show current level
                string currentParent = _browseStack.Count > 0 ? _browseStack.Peek() : null;
                ShowBrowseLevel(currentParent);
            }
            catch (Exception ex)
            {
                TxtBrowseTitle.Text = "Refresh failed";
                LvBrowse.ItemsSource = new List<BrowseRegionItem>
                {
                    new BrowseRegionItem { Name = ex.Message, DownloadVisibility = Visibility.Collapsed, DrillVisibility = Visibility.Collapsed }
                };
            }
        }

        // ---- Download from Browse ----

        private async void BtnDownloadRegion_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            string regionId = btn.Tag as string;
            if (regionId == null) return;

            var geoRegion = _geofabrikIndex.GetRegion(regionId);
            if (geoRegion == null) return;

            _selectedRegion = MapRegion.FromGeofabrik(geoRegion);

            // Hide map manager, show download overlay
            MapManagerPanel.Visibility = Visibility.Collapsed;
            await StartDownloadAndImport();
        }

        // ---- Download & Import ----

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedRegion == null) return;
            await StartDownloadAndImport();
        }

        private async Task StartDownloadAndImport()
        {
            OverlayPanel.Visibility = Visibility.Visible;
            BtnDownload.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Visible;
            _cts = new CancellationTokenSource();

            var displayRequest = new DisplayRequest();
            displayRequest.RequestActive();

            try
            {
                string pbfPath = await _downloadManager.GetExistingMapPath(_selectedRegion);

                if (pbfPath == null)
                {
                    TxtOverlayTitle.Text = "Downloading...";
                    TxtOverlayStatus.Text = _selectedRegion.Name;
                    ProgressOverlay.Value = 0;
                    TxtOverlayDetail.Text = "";

                    _downloadManager.OnProgress += OnDownloadProgress;
                    pbfPath = await _downloadManager.DownloadAsync(_selectedRegion, _cts.Token);
                    _downloadManager.OnProgress -= OnDownloadProgress;
                }

                TxtOverlayTitle.Text = "Importing...";
                TxtOverlayStatus.Text = "Parsing map data — this may take several minutes.";
                ProgressOverlay.Value = 0;
                TxtOverlayDetail.Text = "";

                string dbPath = await _downloadManager.GetDatabasePath(_selectedRegion);
                _db?.Dispose();
                _db = new MapDatabase(dbPath);
                await _db.OpenAsync();

                var importer = new MapImporter();
                importer.OnProgress += OnImportProgress;
                await importer.ImportAsync(pbfPath, _db, _cts.Token);
                importer.OnProgress -= OnImportProgress;

                // Register this map
                SaveMapName(_selectedRegion.Id, _selectedRegion.Name);
                SetActiveMapId(_selectedRegion.Id);

                _renderer = new MapRenderer(_db, _viewport, _currentTheme);

                var bounds = _db.GetBounds();
                if (bounds.HasValue)
                {
                    _viewport.CenterLat = (bounds.Value.minLat + bounds.Value.maxLat) / 2.0;
                    _viewport.CenterLon = (bounds.Value.minLon + bounds.Value.maxLon) / 2.0;
                }

                OverlayPanel.Visibility = Visibility.Collapsed;
                _initialGpsCentered = false;
                StartGps();
                RedrawMap();
            }
            catch (OperationCanceledException)
            {
                TxtOverlayStatus.Text = "Cancelled. Cleaning up...";
                await CleanupPartialData();
                TxtOverlayStatus.Text = "Cancelled.";
                TxtOverlayDetail.Text = "";

                // If no maps exist, show browse again
                if (GetMapNames().Count == 0)
                {
                    OverlayPanel.Visibility = Visibility.Collapsed;
                    ShowMapManager(startInBrowse: true);
                }
                else
                {
                    OverlayPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                TxtOverlayStatus.Text = $"Error: {ex.Message}";
                TxtOverlayDetail.Text = "";

                // Show a way to retry or go back
                BtnDownload.Content = "Retry";
                BtnDownload.Visibility = Visibility.Visible;
            }
            finally
            {
                BtnCancel.Visibility = Visibility.Collapsed;
                BtnCancel.IsEnabled = true;
                displayRequest.RequestRelease();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            BtnCancel.IsEnabled = false;
            TxtOverlayStatus.Text = "Cancelling...";
        }

        private async Task CleanupPartialData()
        {
            _db?.Dispose();
            _db = null;

            if (_selectedRegion == null) return;

            var region = _selectedRegion;
            await Task.Run(async () =>
            {
                try
                {
                    var localFolder = ApplicationData.Current.LocalFolder;
                    var mapsFolder = await localFolder.GetFolderAsync("Maps");

                    string partialPath = Path.Combine(mapsFolder.Path, region.FileName + ".partial");
                    if (File.Exists(partialPath)) File.Delete(partialPath);

                    string pbfPath = Path.Combine(mapsFolder.Path, region.FileName);
                    if (File.Exists(pbfPath)) File.Delete(pbfPath);

                    string dbPath = Path.Combine(mapsFolder.Path, Path.ChangeExtension(region.FileName, ".db"));
                    if (File.Exists(dbPath)) File.Delete(dbPath);
                    if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
                    if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
                }
                catch { }
            });
        }

        private async void OnDownloadProgress(DownloadProgress p)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ProgressOverlay.Value = p.Percent;
                string mbReceived = (p.BytesReceived / 1048576.0).ToString("F1");
                string mbTotal = p.TotalBytes > 0 ? (p.TotalBytes / 1048576.0).ToString("F1") : "?";
                TxtOverlayDetail.Text = $"{mbReceived} / {mbTotal} MB";
            });
        }

        private async void OnImportProgress(ImportProgress p)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ProgressOverlay.Value = p.Percent;
                switch (p.Phase)
                {
                    case ImportPhase.Nodes:
                        TxtOverlayTitle.Text = "Importing Nodes...";
                        TxtOverlayDetail.Text = $"{p.NodesImported:N0} nodes ({p.Percent:F0}%)";
                        break;
                    case ImportPhase.Ways:
                        TxtOverlayTitle.Text = "Importing Ways...";
                        TxtOverlayDetail.Text = $"{p.WaysImported:N0} ways ({p.Percent:F0}%)";
                        break;
                    case ImportPhase.BuildingIndex:
                        TxtOverlayTitle.Text = "Building Index...";
                        TxtOverlayDetail.Text = $"{p.Percent:F0}%";
                        break;
                    case ImportPhase.Done:
                        TxtOverlayDetail.Text = $"Done: {p.NodesImported:N0} nodes, {p.WaysImported:N0} ways";
                        break;
                }
            });
        }

        // ---- Theme Selector ----

        private void BtnThemeSelector_Click(object sender, RoutedEventArgs e)
        {
            ThemePanel.Visibility = Visibility.Visible;
            RefreshThemeList();
        }

        private void BtnCloseTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemePanel.Visibility = Visibility.Collapsed;
        }

        private void RefreshThemeList()
        {
            var items = new List<ThemeListItem>();
            foreach (var theme in MapTheme.AllThemes)
            {
                items.Add(new ThemeListItem
                {
                    Id = theme.Id,
                    Name = theme.Name,
                    ActiveMarker = theme.Id == _currentTheme.Id ? "● Active" : "",
                    PreviewColor = theme.Background,
                    PreviewRoadColor = theme.GetRoadColor("motorway")
                });
            }
            LvThemes.ItemsSource = items;
            LvThemes.ItemClick -= LvThemes_ItemClick;
            LvThemes.ItemClick += LvThemes_ItemClick;
            LvThemes.IsItemClickEnabled = true;
        }

        private void LvThemes_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ThemeListItem;
            if (item == null) return;

            _currentTheme = MapTheme.GetById(item.Id);
            SaveTheme(_currentTheme.Id);
            ApplyThemeBackground();

            if (_renderer != null)
            {
                _renderer.Theme = _currentTheme;
                _renderer.InvalidateCache();
            }

            RefreshThemeList();
            ThemePanel.Visibility = Visibility.Collapsed;
            RedrawMap();
        }

        private MapTheme LoadSavedTheme()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.ContainsKey("theme_id"))
            {
                string id = settings.Values["theme_id"] as string;
                return MapTheme.GetById(id);
            }
            return MapTheme.Light;
        }

        private void SaveTheme(string themeId)
        {
            ApplicationData.Current.LocalSettings.Values["theme_id"] = themeId;
        }

        private void ApplyThemeBackground()
        {
            MapCanvas.ClearColor = _currentTheme.Background;
        }

        // ---- Map Rendering ----

        private void MapCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            _viewport.ScreenWidth = sender.ActualWidth;
            _viewport.ScreenHeight = sender.ActualHeight;

            if (_renderer != null)
            {
                _renderer.Draw(args.DrawingSession, sender);

                if (!double.IsNaN(_gpsLat))
                {
                    _renderer.DrawGpsPosition(args.DrawingSession, _gpsLat, _gpsLon, _gpsAccuracy);
                }
            }
        }

        private async void RedrawMap()
        {
            if (_renderer != null)
            {
                await _renderer.EnsureCacheAsync();
            }

            MapCanvas.Invalidate();
            TxtZoom.Text = $"Z{_viewport.Zoom:F0}";
            TxtStatus.Text = $"{_viewport.CenterLat:F4}° N, {_viewport.CenterLon:F4}° E";
            SaveViewport();
        }

        private void MapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _viewport.ScreenWidth = e.NewSize.Width;
            _viewport.ScreenHeight = e.NewSize.Height;
            RedrawMap();
        }

        // ---- Touch / Mouse Input ----

        private void MapCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(MapCanvas);
            _isPanning = true;
            _panStart = point.Position;
            ((UIElement)sender).CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void MapCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPanning) return;

            var point = e.GetCurrentPoint(MapCanvas);
            double dx = point.Position.X - _panStart.X;
            double dy = point.Position.Y - _panStart.Y;

            _viewport.Pan(dx, dy);
            _panStart = point.Position;
            _followGps = false;
            MapCanvas.Invalidate();

            RedrawMap();

            e.Handled = true;
        }

        private void MapCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isPanning = false;
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            RedrawMap();
            e.Handled = true;
        }

        private void MapCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(MapCanvas);
            int delta = point.Properties.MouseWheelDelta;
            double zoomDelta = delta > 0 ? 0.5 : -0.5;

            _viewport.ZoomAt(point.Position.X, point.Position.Y, zoomDelta);
            _followGps = false;
            RedrawMap();
            e.Handled = true;
        }

        private void MapCanvas_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            _viewport.Pan(e.Delta.Translation.X, e.Delta.Translation.Y);

            if (Math.Abs(e.Delta.Scale - 1.0f) > 0.001f)
            {
                double zoomDelta = Math.Log(e.Delta.Scale) / Math.Log(2.0) * 1.5;
                _viewport.ZoomAt(e.Position.X, e.Position.Y, zoomDelta);
            }

            _followGps = false;
            MapCanvas.Invalidate();

            RedrawMap();

            e.Handled = true;
        }

        // ---- Zoom Buttons ----

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _viewport.ZoomAt(_viewport.ScreenWidth / 2, _viewport.ScreenHeight / 2, 1);
            RedrawMap();
        }

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _viewport.ZoomAt(_viewport.ScreenWidth / 2, _viewport.ScreenHeight / 2, -1);
            RedrawMap();
        }

        // ---- GPS ----

        private void BtnGps_Click(object sender, RoutedEventArgs e)
        {
            if (!double.IsNaN(_gpsLat))
            {
                _viewport.CenterLat = _gpsLat;
                _viewport.CenterLon = _gpsLon;
                _followGps = true;
                RedrawMap();
            }
            else
            {
                StartGps();
            }
        }

        private async void StartGps()
        {
            try
            {
                var access = await Geolocator.RequestAccessAsync();
                if (access != GeolocationAccessStatus.Allowed)
                {
                    TxtStatus.Text = "Location access denied";
                    return;
                }

                _geolocator = new Geolocator
                {
                    DesiredAccuracy = PositionAccuracy.High,
                    MovementThreshold = 10,
                    ReportInterval = 3000
                };

                _geolocator.PositionChanged += OnGpsPositionChanged;

                var pos = await _geolocator.GetGeopositionAsync();
                UpdateGpsPosition(pos.Coordinate);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GPS error: {ex.Message}");
            }
        }

        private async void OnGpsPositionChanged(Geolocator sender, PositionChangedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                UpdateGpsPosition(args.Position.Coordinate);
            });
        }

        private void UpdateGpsPosition(Geocoordinate coord)
        {
            _gpsLat = coord.Point.Position.Latitude;
            _gpsLon = coord.Point.Position.Longitude;
            _gpsAccuracy = coord.Accuracy;

            if (!_initialGpsCentered)
            {
                _viewport.CenterLat = _gpsLat;
                _viewport.CenterLon = _gpsLon;
                _initialGpsCentered = true;
                _followGps = true;
            }
            else if (_followGps)
            {
                _viewport.CenterLat = _gpsLat;
                _viewport.CenterLon = _gpsLon;
            }

            RedrawMap();
        }

        // ---- Viewport persistence ----

        private void SaveViewport()
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["viewport_lat"] = _viewport.CenterLat;
            settings.Values["viewport_lon"] = _viewport.CenterLon;
            settings.Values["viewport_zoom"] = _viewport.Zoom;
        }

        private void RestoreViewport()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.ContainsKey("viewport_lat") &&
                settings.Values.ContainsKey("viewport_lon") &&
                settings.Values.ContainsKey("viewport_zoom"))
            {
                _viewport.CenterLat = (double)settings.Values["viewport_lat"];
                _viewport.CenterLon = (double)settings.Values["viewport_lon"];
                _viewport.Zoom = (double)settings.Values["viewport_zoom"];
            }
        }

        private bool HasSavedViewport()
        {
            return ApplicationData.Current.LocalSettings.Values.ContainsKey("viewport_lat");
        }
    }
}
