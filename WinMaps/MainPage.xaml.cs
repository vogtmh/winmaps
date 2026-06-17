using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.Storage;
using Windows.System.Display;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
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
        public string SizeText { get; set; }
        public Visibility UseVisibility { get; set; }
    }

    // View model for Browse list
    internal class BrowseRegionItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Visibility DownloadVisibility { get; set; }
        public Visibility DrillVisibility { get; set; }
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

        // GPS smoothing: interpolate displayed position toward target
        private double _gpsTargetLat = double.NaN;
        private double _gpsTargetLon = double.NaN;
        private double _gpsTargetAccuracy = 0;
        private DispatcherTimer _gpsAnimTimer;
        private const double GpsSmoothFactor = 0.25; // lerp fraction per tick (higher = faster snap)

        // Pan state
        private bool _isPanning = false;
        private Point _panStart;

        // Active region for download/import
        private MapRegion _selectedRegion;
        private string _activeMapId;

        // Map manager
        private GeofabrikIndex _geofabrikIndex;
        private GeofabrikGeoIndex _geoIndex;
        private NaturalEarthIndex _naturalEarthIndex;
        private Stack<string> _browseStack; // parent IDs for back navigation
        private Stack<string> _worldMapStack; // navigation for world map back

        // Theme
        private MapTheme _currentTheme;
        private string _lastRenderError;

        public MainPage()
        {
            this.InitializeComponent();
            _viewport = new MapViewport();
            _downloadManager = new MapDownloadManager();
            _cts = new CancellationTokenSource();
            _geofabrikIndex = new GeofabrikIndex();
            _geoIndex = new GeofabrikGeoIndex();
            _naturalEarthIndex = new NaturalEarthIndex();
            _browseStack = new Stack<string>();
            _worldMapStack = new Stack<string>();
            _currentTheme = LoadSavedTheme();

            this.Loaded += MainPage_Loaded;
            this.Unloaded += MainPage_Unloaded;
            Application.Current.Suspending += OnAppSuspending;
            Application.Current.Resuming += OnAppResuming;

            RestoreViewport();
            ApplyThemeBackground();

            Windows.UI.Core.SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
        }

        private void OnBackRequested(object sender, Windows.UI.Core.BackRequestedEventArgs e)
        {
            // Menu is open — dismiss it
            if (MenuPanel.Visibility == Visibility.Visible)
            {
                CloseMenu();
                e.Handled = true;
                return;
            }

            // Preferences panel
            if (PreferencesPanel.Visibility == Visibility.Visible)
            {
                PreferencesPanel.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }

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

        private void OnAppSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            // Checkpoint the WAL so that if the OS terminates the suspended process,
            // the next startup doesn't have to replay a large WAL file.
            SaveViewport();
            var deferral = e.SuspendingOperation.GetDeferral();
            try { _db?.Checkpoint(); }
            catch { }
            finally { deferral.Complete(); }
        }

        private void OnAppResuming(object sender, object e)
        {
            // Re-initialize GPS after suspend — the geolocator stops delivering updates
            if (_geolocator != null)
            {
                _geolocator.PositionChanged -= OnGpsPositionChanged;
                _geolocator = null;
                StartGps();
            }
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Application.Current.Suspending -= OnAppSuspending;
            Application.Current.Resuming -= OnAppResuming;
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
                        if (!_db.CheckIntegrity())
                            throw new InvalidOperationException("Integrity check failed.");
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
                        // DB is corrupt or unreadable — remove it so startup doesn't crash next time
                        DeleteAndUnregisterMap(_activeMapId);
                    }
                }
            }

            // No valid active map — check if any maps exist
            if (mapNames.Count > 0)
            {
                // Try to use the first available map; skip and delete any that are corrupted
                foreach (var kv in mapNames)
                {
                    string dbPath = GetDbPathForMap(kv.Key);
                    if (!File.Exists(dbPath)) continue;
                    try
                    {
                        await SwitchToMap(kv.Key);
                        return; // success
                    }
                    catch
                    {
                        // SwitchToMap already called DeleteAndUnregisterMap on corruption
                        mapNames = GetMapNames(); // refresh after removal
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
            var countryNames = GetMapNames();       // country key → display name
            var installedIds = GetInstalledRegionIds();
            var regionCountryMap = GetRegionCountryMap();
            foreach (string dbFile in Directory.GetFiles(mapsPath, "*.osm.db"))
            {
                string fileName = Path.GetFileName(dbFile);
                // "germany.osm.db" → "germany"
                string countryKey = fileName.Substring(0, fileName.Length - ".osm.db".Length);

                if (string.IsNullOrEmpty(countryKey)) continue;

                if (!countryNames.ContainsKey(countryKey))
                {
                    // Build display name from key: "north-america" → "North America"
                    string displayName = string.Join(" ", countryKey.Split('-')
                        .Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w.Substring(1) : ""));
                    SaveMapName(countryKey, displayName);

                    if (GetActiveMapId() == null)
                        SetActiveMapId(countryKey);
                }

                // Restore installed_regions and region_country_map from this DB's regions table
                try
                {
                    var tempDb = new MapDatabase(dbFile);
                    tempDb.OpenSync();
                    var regions = tempDb.GetRegions();
                    tempDb.Dispose();

                    foreach (var (id, _) in regions)
                    {
                        if (!installedIds.Contains(id))
                            installedIds.Add(id);
                        regionCountryMap[id] = countryKey;
                    }
                }
                catch { }
            }

            SaveInstalledRegionIds(installedIds);
            SaveRegionCountryMap(regionCountryMap);
        }

        // ---- Map file helpers ----

        /// <summary>
        /// Returns the country key for a region ID.
        /// Checks the stored region→country map first (works offline),
        /// then the GeofabrikIndex parent chain if loaded,
        /// then falls back to slash-splitting for old-format IDs.
        /// </summary>
        private string GetCountryKey(string regionId)
        {
            var map = GetRegionCountryMap();
            if (map.TryGetValue(regionId, out var stored)) return stored;
            if (_geofabrikIndex != null && _geofabrikIndex.IsLoaded)
                return _geofabrikIndex.GetCountryId(regionId);
            // Fallback for old slash-format IDs ("europe/germany/...") — not used with current catalog
            var parts = regionId.Split('/');
            return parts.Length >= 2 ? parts[1] : parts[0];
        }

        /// <summary>
        /// Derives a display name for a country key.
        /// Tries the Geofabrik index first; falls back to title-casing the key.
        /// </summary>
        private string GetCountryDisplayName(string countryKey, string regionId)
        {
            // Build the expected country geo-ID: "europe" + "/" + "germany" = "europe/germany"
            if (!string.IsNullOrEmpty(regionId))
            {
                var parts = regionId.Split('/');
                if (parts.Length >= 2)
                {
                    string countryGeoId = parts[0] + "/" + countryKey;
                    var geo = _geofabrikIndex.GetRegion(countryGeoId);
                    if (geo != null) return geo.Name;
                    // If the region itself IS the country node (2 parts)
                    geo = _geofabrikIndex.GetRegion(regionId);
                    if (geo != null && geo.ParentId != null &&
                        _geofabrikIndex.GetRegion(geo.ParentId)?.ParentId == null)
                        return geo.Name;
                }
            }
            // Fallback: "north-america" → "North America"
            return string.Join(" ", countryKey.Split('-')
                .Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w.Substring(1) : ""));
        }

        private string GetMapsFolder()
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            string mapsPath = Path.Combine(localFolder, "Maps");
            if (!Directory.Exists(mapsPath))
                Directory.CreateDirectory(mapsPath);
            return mapsPath;
        }

        // Country DB lives at Maps/{countryKey}.osm.db (flat, not nested)
        private string GetDbPathForMap(string countryKey)
        {
            return Path.Combine(GetMapsFolder(), countryKey + ".osm.db");
        }

        private string GetPbfPathForMap(string regionId)
        {
            return Path.Combine(GetMapsFolder(), regionId + ".osm.pbf");
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

        // map_names: country key → country display name (e.g. "germany" → "Germany")
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

        // installed_regions: flat JSON array of all installed Geofabrik region IDs
        private HashSet<string> GetInstalledRegionIds()
        {
            var result = new HashSet<string>();
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.ContainsKey("installed_regions"))
            {
                string json = settings.Values["installed_regions"] as string;
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        var arr = Windows.Data.Json.JsonArray.Parse(json);
                        foreach (var v in arr)
                            result.Add(v.GetString());
                    }
                    catch { }
                }
            }
            return result;
        }

        private void SaveInstalledRegionIds(HashSet<string> ids)
        {
            var arr = new Windows.Data.Json.JsonArray();
            foreach (var id in ids)
                arr.Add(Windows.Data.Json.JsonValue.CreateStringValue(id));
            ApplicationData.Current.LocalSettings.Values["installed_regions"] = arr.Stringify();
        }

        private void AddInstalledRegion(string regionId, string countryKey)
        {
            var ids = GetInstalledRegionIds();
            ids.Add(regionId);
            SaveInstalledRegionIds(ids);

            var map = GetRegionCountryMap();
            map[regionId] = countryKey;
            SaveRegionCountryMap(map);
        }

        private void RemoveInstalledRegionsForCountry(string countryKey)
        {
            var ids = GetInstalledRegionIds();
            var map = GetRegionCountryMap();
            ids.RemoveWhere(id => GetCountryKey(id) == countryKey);
            foreach (var key in map.Keys.Where(k => map[k] == countryKey).ToList())
                map.Remove(key);
            SaveInstalledRegionIds(ids);
            SaveRegionCountryMap(map);
        }

        // region_country_map: regionId → countryKey (e.g. "stuttgart-regbez" → "germany")
        private Dictionary<string, string> GetRegionCountryMap()
        {
            var result = new Dictionary<string, string>();
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.ContainsKey("region_country_map"))
            {
                string json = settings.Values["region_country_map"] as string;
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        var obj = Windows.Data.Json.JsonObject.Parse(json);
                        foreach (var kv in obj)
                            result[kv.Key] = kv.Value.GetString();
                    }
                    catch { }
                }
            }
            return result;
        }

        private void SaveRegionCountryMap(Dictionary<string, string> map)
        {
            var obj = new Windows.Data.Json.JsonObject();
            foreach (var kv in map)
                obj[kv.Key] = Windows.Data.Json.JsonValue.CreateStringValue(kv.Value);
            ApplicationData.Current.LocalSettings.Values["region_country_map"] = obj.Stringify();
        }

        // ---- Map Manager UI ----

        private void BtnMapManager_Click(object sender, RoutedEventArgs e)
        {
            ShowMenu();
        }

        private void ShowMenu()
        {
            MenuScrim.Visibility = Visibility.Visible;
            MenuPanel.Visibility = Visibility.Visible;
            MenuTranslate.Y = 400;
            MenuSlideIn.Begin();
        }

        private void CloseMenu(Action afterClose = null)
        {
            MenuSlideOut.Completed -= OnMenuSlideOutCompleted;
            _menuSlideOutAction = afterClose;
            MenuSlideOut.Completed += OnMenuSlideOutCompleted;
            MenuSlideOut.Begin();
        }

        private Action _menuSlideOutAction;

        private void OnMenuSlideOutCompleted(object sender, object e)
        {
            MenuSlideOut.Completed -= OnMenuSlideOutCompleted;
            MenuPanel.Visibility = Visibility.Collapsed;
            MenuScrim.Visibility = Visibility.Collapsed;
            _menuSlideOutAction?.Invoke();
            _menuSlideOutAction = null;
        }

        private void MenuScrim_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            CloseMenu();
        }

        private void BtnMenuManageMaps_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu(() => ShowMapManager(startInBrowse: false));
        }

        private void BtnMenuThemes_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu(() =>
            {
                ThemePanel.Visibility = Visibility.Visible;
                RefreshThemeList();
            });
        }

        private void BtnMenuPreferences_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu(() => ShowPreferences());
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
            var names = GetMapNames();           // country key → country display name
            var installedIds = GetInstalledRegionIds();
            var items = new List<DownloadedMapItem>();
            long totalBytes = 0;

            foreach (var kv in names)
            {
                string countryKey = kv.Key;
                string dbPath = GetDbPathForMap(countryKey);
                if (!File.Exists(dbPath)) continue;

                bool isActive = countryKey == _activeMapId;
                long fileSize = new FileInfo(dbPath).Length;
                totalBytes += fileSize;

                // List which regions are installed in this country DB
                var countryRegions = installedIds
                    .Where(id => GetCountryKey(id) == countryKey)
                    .ToList();
                string regionsText = countryRegions.Count > 0
                    ? string.Join(", ", countryRegions.Select(id =>
                        id.Split('/').LastOrDefault() ?? id))
                    : "";

                items.Add(new DownloadedMapItem
                {
                    Id = countryKey,
                    Name = kv.Value,
                    StatusText = (isActive ? "\u25cf Active" : "") +
                                 (regionsText.Length > 0 ? (isActive ? " \u2014 " : "") + regionsText : ""),
                    SizeText = FormatFileSize(fileSize),
                    UseVisibility = isActive ? Visibility.Collapsed : Visibility.Visible
                });
            }

            LvMyMaps.ItemsSource = items;
            TxtNoMaps.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtTotalSize.Text = items.Count > 0 ? $"Total: {FormatFileSize(totalBytes)}" : "";
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1073741824L) return $"{bytes / 1073741824.0:F1} GB";
            if (bytes >= 1048576L) return $"{bytes / 1048576.0:F1} MB";
            if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
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
            try
            {
                await _db.OpenAsync();
                if (!_db.CheckIntegrity())
                    throw new InvalidOperationException($"Map database for '{mapId}' failed integrity check.");
            }
            catch
            {
                _db?.Dispose();
                _db = null;
                DeleteAndUnregisterMap(mapId);
                throw;
            }

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

        /// <summary>
        /// Deletes DB files for a country map and removes it from all LocalSettings.
        /// Safe to call on a DB that is already disposed/null.
        /// </summary>
        private void DeleteAndUnregisterMap(string countryKey)
        {
            try
            {
                string dbPath = GetDbPathForMap(countryKey);
                if (File.Exists(dbPath)) File.Delete(dbPath);
                if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
                if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
            }
            catch { }
            RemoveMapName(countryKey);
            RemoveInstalledRegionsForCountry(countryKey);
            if (_activeMapId == countryKey)
            {
                _activeMapId = null;
                ApplicationData.Current.LocalSettings.Values.Remove("active_map_id");
            }
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

            // Delete files — deletes entire country DB (all regions for this country)
            await Task.Run(() =>
            {
                try
                {
                    string dbPath = GetDbPathForMap(mapId);
                    if (File.Exists(dbPath)) File.Delete(dbPath);
                    if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
                    if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
                    // Note: per-region PBF files (if any) are left; they live in nested sub-paths
                }
                catch { }
            });

            RemoveMapName(mapId);
            RemoveInstalledRegionsForCountry(mapId);
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
            WorldMapView.Visibility = Visibility.Collapsed;
            _browseStack.Clear();
            ShowBrowseLevel(null); // show continents
        }

        // ---- World Map view ----

        private void BtnWorldMap_Click(object sender, RoutedEventArgs e)
        {
            MyMapsView.Visibility = Visibility.Collapsed;
            BrowseView.Visibility = Visibility.Collapsed;
            WorldMapView.Visibility = Visibility.Visible;
            _worldMapStack.Clear();
            ShowWorldMapLevel("world");
        }

        private void BtnWorldMapBack_Click(object sender, RoutedEventArgs e)
        {
            if (_worldMapStack.Count == 0)
            {
                // At world root — go back to My Maps
                WorldMapView.Visibility = Visibility.Collapsed;
                MyMapsView.Visibility = Visibility.Visible;
                return;
            }
            string prev = _worldMapStack.Pop();
            if (prev == "world")
            {
                ShowWorldMapLevel("world");
            }
            else if (prev != null && prev.StartsWith("continent:"))
            {
                string continent = prev.Substring("continent:".Length);
                ShowWorldMapLevel("continent", continent);
            }
            else
            {
                ShowWorldMapLevel("world");
            }
        }

        // The NE countries currently drawn on the canvas (levels 1 & 2), or null at level 3
        private List<NaturalEarthCountry> _worldMapNECountries;
        // The Geofabrik regions drawn at country level (level 3), or null at levels 1 & 2
        private List<GeofabrikRegion> _worldMapGfRegions;
        // "world", "continent", or "country"
        private string _worldMapLevel = "world";
        // Selected continent name (for level 2, e.g. "Europe")
        private string _worldMapContinent;
        // Selected country ISO + Geofabrik ID (for level 3)
        private string _worldMapCountryIso;
        private string _worldMapCountryGfId;
        // Current viewport for the canvas (lat/lon bounds being displayed)
        private (double MinLat, double MinLon, double MaxLat, double MaxLon) _worldMapViewport
            = (-85, -180, 85, 180);

        private async void ShowWorldMapLevel(string level, string param = null)
        {
            // Ensure Geofabrik catalog is loaded (needed for color logic at all levels)
            if (!_geofabrikIndex.IsLoaded)
            {
                TxtWorldMapLoading.Visibility = Visibility.Visible;
                TxtWorldMapLoading.Text = "Loading catalog…";
                try { await _geofabrikIndex.LoadAsync(); }
                catch { TxtWorldMapLoading.Text = "Failed to load catalog."; return; }
            }

            // Ensure Natural Earth data is loaded
            if (!_naturalEarthIndex.IsLoaded)
            {
                TxtWorldMapLoading.Visibility = Visibility.Visible;
                TxtWorldMapLoading.Text = "Loading map data…";
                try { await _naturalEarthIndex.LoadAsync(); }
                catch { TxtWorldMapLoading.Text = "Failed to load map shapes."; return; }
            }
            TxtWorldMapLoading.Visibility = Visibility.Collapsed;

            _worldMapLevel = level;
            _worldMapNECountries = null;
            _worldMapGfRegions = null;

            if (level == "world")
            {
                // Show all countries, colored by continent status
                _worldMapNECountries = _naturalEarthIndex.GetAllCountries();
                _worldMapViewport = (-60, -180, 85, 180);
                TxtWorldMapTitle.Text = "World";
            }
            else if (level == "continent")
            {
                _worldMapContinent = param;
                _worldMapNECountries = _naturalEarthIndex.GetCountriesByContinent(param);

                // Compute viewport from country bounds
                ComputeViewportFromNE(_worldMapNECountries);

                TxtWorldMapTitle.Text = param;
            }
            else if (level == "country")
            {
                _worldMapCountryIso = param;
                // Find Geofabrik region by ISO and get its children
                var gfRegion = _geofabrikIndex.GetRegionByIso(param);
                if (gfRegion != null)
                {
                    _worldMapCountryGfId = gfRegion.Id;

                    // Ensure geometry is loaded for Geofabrik subdivisions
                    if (!_geoIndex.IsLoaded)
                    {
                        TxtWorldMapLoading.Visibility = Visibility.Visible;
                        TxtWorldMapLoading.Text = "Loading region details…";
                        await _geoIndex.LoadAsync(_geofabrikIndex);
                        TxtWorldMapLoading.Visibility = Visibility.Collapsed;
                    }

                    var children = _geofabrikIndex.GetChildren(gfRegion.Id);
                    _worldMapGfRegions = new List<GeofabrikRegion>();
                    foreach (var child in children)
                        if (child.Geometry != null && child.Geometry.Count > 0)
                            _worldMapGfRegions.Add(child);

                    if (_worldMapGfRegions.Count == 0)
                    {
                        // No subdivisions with geometry — show the country itself
                        _worldMapGfRegions = null;
                        var neCountry = _naturalEarthIndex.GetByIso(param);
                        if (neCountry != null)
                        {
                            _worldMapNECountries = new List<NaturalEarthCountry> { neCountry };
                            ComputeViewportFromNE(_worldMapNECountries);
                        }
                        else
                        {
                            FallBackToBrowse(gfRegion.Id);
                            return;
                        }
                    }
                    else
                    {
                        // Compute viewport from Geofabrik children
                        ComputeViewportFromGf(_worldMapGfRegions);
                    }

                    TxtWorldMapTitle.Text = gfRegion.Name;
                }
                else
                {
                    // No Geofabrik match — go back
                    return;
                }
            }

            BtnWorldMapBack.Visibility = Visibility.Visible;
            WorldMapCanvas.Invalidate();
        }

        private void ComputeViewportFromNE(List<NaturalEarthCountry> countries)
        {
            double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
            foreach (var c in countries)
            {
                var filtered = GetFilteredGeometry(c);
                if (filtered == null) continue;
                foreach (var ring in filtered)
                    foreach (var p in ring)
                    {
                        if (p.Lat < minLat) minLat = p.Lat;
                        if (p.Lat > maxLat) maxLat = p.Lat;
                        if (p.Lon < minLon) minLon = p.Lon;
                        if (p.Lon > maxLon) maxLon = p.Lon;
                    }
            }
            // Add 2° padding
            if (maxLat > minLat && maxLon > minLon)
                _worldMapViewport = (minLat - 2, minLon - 2, maxLat + 2, maxLon + 2);
            else
                _worldMapViewport = (-85, -180, 85, 180);
        }

        private void ComputeViewportFromGf(List<GeofabrikRegion> regions)
        {
            double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
            foreach (var r in regions)
            {
                if (r.HasBbox)
                {
                    if (r.BboxMinLat < minLat) minLat = r.BboxMinLat;
                    if (r.BboxMaxLat > maxLat) maxLat = r.BboxMaxLat;
                    if (r.BboxMinLon < minLon) minLon = r.BboxMinLon;
                    if (r.BboxMaxLon > maxLon) maxLon = r.BboxMaxLon;
                }
                else if (r.Geometry != null)
                {
                    foreach (var ring in r.Geometry)
                        foreach (var p in ring)
                        {
                            if (p.Lat < minLat) minLat = p.Lat;
                            if (p.Lat > maxLat) maxLat = p.Lat;
                            if (p.Lon < minLon) minLon = p.Lon;
                            if (p.Lon > maxLon) maxLon = p.Lon;
                        }
                }
            }
            if (maxLat > minLat && maxLon > minLon)
                _worldMapViewport = (minLat - 1, minLon - 1, maxLat + 1, maxLon + 1);
            else
                _worldMapViewport = (-85, -180, 85, 180);
        }

        private void FallBackToBrowse(string regionId)
        {
            WorldMapView.Visibility = Visibility.Collapsed;
            BrowseView.Visibility = Visibility.Visible;
            _browseStack.Clear();
            _browseStack.Push(regionId);
            ShowBrowseLevel(regionId);
        }

        private void WorldMapCanvas_Draw(
            Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl sender,
            Microsoft.Graphics.Canvas.UI.Xaml.CanvasDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            float W = (float)sender.ActualWidth;
            float H = (float)sender.ActualHeight;

            // Fill background (ocean)
            ds.FillRectangle(0, 0, W, H,
                Windows.UI.Color.FromArgb(255, 30, 40, 58));

            var (minLat, minLon, maxLat, maxLon) = _worldMapViewport;
            double latSpan = maxLat - minLat;
            double lonSpan = maxLon - minLon;
            if (latSpan <= 0 || lonSpan <= 0) return;

            float padding = 16f;
            float availW = W - 2 * padding;
            float availH = H - 2 * padding;
            // Fit-to-contain: use whichever dimension constrains first
            float aspect = (float)(latSpan / lonSpan);
            float drawW, drawH;
            if (availW * aspect <= availH)
            {
                // Width-constrained
                drawW = availW;
                drawH = availW * aspect;
            }
            else
            {
                // Height-constrained
                drawH = availH;
                drawW = availH / aspect;
            }
            float offsetX = padding + (availW - drawW) / 2f;
            float offsetY = padding + (availH - drawH) / 2f;

            Func<double, double, (float x, float y)> project = (lat, lon) =>
            {
                float x = offsetX + (float)((lon - minLon) / lonSpan * drawW);
                float y = offsetY + (float)((maxLat - lat) / latSpan * drawH);
                return (x, y);
            };

            if (_worldMapLevel == "country" && _worldMapGfRegions != null)
            {
                DrawGeofabrikRegions(ds, sender, project, W, H, drawW);
            }
            else if (_worldMapNECountries != null)
            {
                DrawNaturalEarthCountries(ds, sender, project, W, H, drawW);
            }
        }

        private void DrawNaturalEarthCountries(
            Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
            Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl sender,
            Func<double, double, (float x, float y)> project,
            float W, float H, float drawW)
        {
            var installedIds = GetInstalledRegionIds();

            // Precompute continent-level download status for world view
            Dictionary<string, (int total, int installed)> continentStatus = null;
            if (_worldMapLevel == "world")
            {
                continentStatus = new Dictionary<string, (int total, int installed)>();
                foreach (var continent in _naturalEarthIndex.GetContinents())
                {
                    int total = 0, installed = 0;
                    foreach (var c in _naturalEarthIndex.GetCountriesByContinent(continent))
                    {
                        var gfr = _geofabrikIndex.GetRegionByIso(c.IsoA2);
                        if (gfr == null) continue;
                        var leaves = _geofabrikIndex.GetLeaves(gfr.Id);
                        total += leaves.Count;
                        foreach (var l in leaves)
                            if (installedIds.Contains(l.Id)) installed++;
                    }
                    continentStatus[continent] = (total, installed);
                }
            }

            var stroke = Windows.UI.Color.FromArgb(255, 100, 120, 140);

            foreach (var country in _worldMapNECountries)
            {
                if (country.Geometry == null) continue;

                // Determine fill color
                Windows.UI.Color fill;
                if (_worldMapLevel == "world")
                {
                    // Color by continent
                    var cs = continentStatus != null && continentStatus.ContainsKey(country.Continent)
                        ? continentStatus[country.Continent]
                        : (total: 0, installed: 0);
                    fill = ChooseMapColor(cs.total, cs.installed);
                }
                else
                {
                    // Continent level — color by individual country
                    var gfr = _geofabrikIndex.GetRegionByIso(country.IsoA2);
                    if (gfr != null)
                    {
                        var leaves = _geofabrikIndex.GetLeaves(gfr.Id);
                        int total = leaves.Count, installed = 0;
                        foreach (var l in leaves)
                            if (installedIds.Contains(l.Id)) installed++;
                        fill = ChooseMapColor(total, installed);
                    }
                    else
                        fill = ChooseMapColor(0, 0);
                }

                var geo = GetFilteredGeometry(country);
                if (geo == null) continue;
                DrawPolygonRings(ds, sender, project, geo, fill, stroke);
            }
        }

        private void DrawGeofabrikRegions(
            Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
            Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl sender,
            Func<double, double, (float x, float y)> project,
            float W, float H, float drawW)
        {
            var installedIds = GetInstalledRegionIds();
            var stroke = Windows.UI.Color.FromArgb(255, 100, 120, 140);

            // Sort by area — largest first so smaller regions draw on top
            var sorted = new List<GeofabrikRegion>(_worldMapGfRegions);
            sorted.Sort((a, b) => ComputeRingArea(b.Geometry).CompareTo(ComputeRingArea(a.Geometry)));

            foreach (var region in sorted)
            {
                if (region.Geometry == null) continue;

                var leaves = _geofabrikIndex.GetLeaves(region.Id);
                int total = leaves.Count, installed = 0;
                foreach (var l in leaves)
                    if (installedIds.Contains(l.Id)) installed++;

                var fill = ChooseMapColor(total, installed);
                DrawPolygonRings(ds, sender, project, region.Geometry, fill, stroke);
            }
        }

        private void DrawPolygonRings(
            Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
            Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl sender,
            Func<double, double, (float x, float y)> project,
            List<List<(double Lat, double Lon)>> geometry,
            Windows.UI.Color fill, Windows.UI.Color stroke)
        {
            foreach (var ring in geometry)
            {
                if (ring.Count < 3) continue;
                var normRing = NormalizeRingLons(ring);

                using (var pb = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(sender))
                {
                    var first = project(normRing[0].Lat, normRing[0].Lon);
                    pb.BeginFigure(first.x, first.y);
                    for (int p = 1; p < normRing.Count; p++)
                    {
                        var pt = project(normRing[p].Lat, normRing[p].Lon);
                        pb.AddLine(pt.x, pt.y);
                    }
                    pb.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Closed);

                    using (var geo = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pb))
                    {
                        ds.FillGeometry(geo, fill);
                        ds.DrawGeometry(geo, stroke, 0.5f);
                    }
                }
            }
        }

        private static Windows.UI.Color ChooseMapColor(int total, int installed)
        {
            if (total == 0 || installed == 0)
                return Windows.UI.Color.FromArgb(255, 58, 76, 100);   // grey-blue — none
            else if (installed >= total)
                return Windows.UI.Color.FromArgb(255, 50, 140, 70);   // green — complete
            else
                return Windows.UI.Color.FromArgb(255, 190, 125, 35);  // orange — partial
        }

        /// <summary>
        /// Returns only the mainland rings of a geometry: the largest ring plus any rings
        /// within a distance threshold of the largest ring's centroid (nearby islands).
        /// Filters out distant overseas territories (e.g. French Guiana, Réunion)
        /// while naturally preserving transcontinental countries like Russia.
        /// </summary>
        private static List<List<(double Lat, double Lon)>> GetMainlandRings(
            List<List<(double Lat, double Lon)>> geometry)
        {
            if (geometry == null || geometry.Count <= 1) return geometry;

            // Find largest ring by point count (good enough proxy for area)
            List<(double Lat, double Lon)> largest = geometry[0];
            foreach (var ring in geometry)
                if (ring.Count > largest.Count) largest = ring;

            // Centroid of largest ring
            double cLat = 0, cLon = 0;
            foreach (var p in largest) { cLat += p.Lat; cLon += p.Lon; }
            cLat /= largest.Count;
            cLon /= largest.Count;

            // Keep rings within 30° of mainland centroid
            var result = new List<List<(double Lat, double Lon)>>();
            foreach (var ring in geometry)
            {
                double rLat = 0, rLon = 0;
                foreach (var p in ring) { rLat += p.Lat; rLon += p.Lon; }
                rLat /= ring.Count;
                rLon /= ring.Count;

                if (Math.Abs(rLat - cLat) < 30 && Math.Abs(rLon - cLon) < 30)
                    result.Add(ring);
            }
            return result.Count > 0 ? result : geometry;
        }

        /// <summary>
        /// Returns the filtered geometry for a country at any map level.
        /// Always uses mainland filtering to exclude distant overseas territories
        /// while preserving transcontinental countries like Russia.
        /// For Oceania, additionally drops rings in negative longitudes so the
        /// view focuses on Australia / NZ / PNG rather than scattered Pacific islands.
        /// </summary>
        private List<List<(double Lat, double Lon)>> GetFilteredGeometry(NaturalEarthCountry country)
        {
            if (country.Geometry == null) return null;
            var rings = GetMainlandRings(country.Geometry);
            if (rings == null) return null;

            // Oceania: keep only rings with positive-longitude centroids
            if (country.Continent == "Oceania")
            {
                var pos = new List<List<(double Lat, double Lon)>>();
                foreach (var ring in rings)
                {
                    double sumLon = 0;
                    foreach (var p in ring) sumLon += p.Lon;
                    if (sumLon / ring.Count >= 0) pos.Add(ring);
                }
                return pos.Count > 0 ? pos : null;
            }
            return rings;
        }

        /// <summary>
        /// Approximates the geographic area of all rings in a geometry
        /// using the bounding-box area of each ring (for sort ordering only).
        /// </summary>
        private static double ComputeRingArea(List<List<(double Lat, double Lon)>> geometry)
        {
            if (geometry == null) return 0;
            double area = 0;
            foreach (var ring in geometry)
            {
                if (ring.Count < 3) continue;
                double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
                foreach (var p in ring)
                {
                    if (p.Lat < minLat) minLat = p.Lat;
                    if (p.Lat > maxLat) maxLat = p.Lat;
                    if (p.Lon < minLon) minLon = p.Lon;
                    if (p.Lon > maxLon) maxLon = p.Lon;
                }
                area += (maxLat - minLat) * (maxLon - minLon);
            }
            return area;
        }

        /// <summary>
        /// Computes the visual centroid of a geometry for label placement.
        /// Uses the average of all ring points. For antimeridian-crossing polygons
        /// the longitudes are normalized first.
        /// </summary>
        private static (double lat, double lon) ComputeGeometryCentroid(
            List<List<(double Lat, double Lon)>> geometry, double vpMinLon, double vpMaxLon)
        {
            double sumLat = 0, sumLon = 0;
            int count = 0;
            foreach (var ring in geometry)
            {
                var norm = NormalizeRingLons(ring);
                foreach (var p in norm)
                {
                    sumLat += p.Lat;
                    sumLon += p.Lon;
                    count++;
                }
            }
            if (count == 0) return (0, 0);
            double avgLon = sumLon / count;
            // Wrap back into viewport range
            double vpCenter = (vpMinLon + vpMaxLon) / 2.0;
            while (avgLon > vpCenter + 180) avgLon -= 360;
            while (avgLon < vpCenter - 180) avgLon += 360;
            return (sumLat / count, avgLon);
        }

        /// <summary>
        /// Normalizes a ring's longitude sequence so that each consecutive delta stays
        /// within [-180, +180]. Polygons that cross the antimeridian (e.g. Russia, Asia)
        /// will have longitudes that go beyond ±180 after normalization; those out-of-range
        /// portions are simply clipped by the canvas, which is visually correct.
        /// </summary>
        private static List<(double Lat, double Lon)> NormalizeRingLons(
            List<(double Lat, double Lon)> ring)
        {
            if (ring.Count < 2) return ring;
            var result = new List<(double Lat, double Lon)>(ring.Count);
            result.Add(ring[0]);
            double prevLon = ring[0].Lon;
            for (int i = 1; i < ring.Count; i++)
            {
                double lon = ring[i].Lon;
                double delta = lon - prevLon;
                if (delta > 180.0) lon -= 360.0;
                else if (delta < -180.0) lon += 360.0;
                result.Add((ring[i].Lat, lon));
                prevLon = lon;
            }
            return result;
        }

        private void WorldMapCanvas_PointerPressed(object sender,
            Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(WorldMapCanvas).Position;
            float W = (float)WorldMapCanvas.ActualWidth;
            float H = (float)WorldMapCanvas.ActualHeight;

            var (minLat, minLon, maxLat, maxLon) = _worldMapViewport;
            double latSpan = maxLat - minLat;
            double lonSpan = maxLon - minLon;
            float padding = 16f;
            float availW = W - 2 * padding;
            float availH = H - 2 * padding;
            float aspect = (float)(latSpan / lonSpan);
            float drawW, drawH;
            if (availW * aspect <= availH)
            {
                drawW = availW;
                drawH = availW * aspect;
            }
            else
            {
                drawH = availH;
                drawW = availH / aspect;
            }
            float offsetX = padding + (availW - drawW) / 2f;
            float offsetY = padding + (availH - drawH) / 2f;

            // Unproject tap to lat/lon
            double tapLon = minLon + ((pt.X - offsetX) / drawW) * lonSpan;
            double tapLat = maxLat - ((pt.Y - offsetY) / drawH) * latSpan;

            if (_worldMapLevel == "country" && _worldMapGfRegions != null)
            {
                // Hit test against Geofabrik regions
                foreach (var region in _worldMapGfRegions)
                {
                    if (region.Geometry == null) continue;
                    if (PointInGeometry(tapLat, tapLon, region.Geometry))
                    {
                        OnWorldMapGfRegionTapped(region);
                        return;
                    }
                }
            }
            else if (_worldMapNECountries != null)
            {
                // Hit test against Natural Earth countries (filtered to exclude overseas)
                foreach (var country in _worldMapNECountries)
                {
                    var filtered = GetFilteredGeometry(country);
                    if (filtered == null) continue;
                    if (PointInGeometry(tapLat, tapLon, filtered))
                    {
                        OnWorldMapNECountryTapped(country);
                        return;
                    }
                }
            }
        }

        private static bool PointInGeometry(double lat, double lon,
            List<List<(double Lat, double Lon)>> geometry)
        {
            foreach (var ring in geometry)
            {
                if (PointInRing(lat, lon, ring)) return true;
            }
            return false;
        }

        private static bool PointInRing(double lat, double lon,
            List<(double Lat, double Lon)> ring)
        {
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = ring[i].Lon, yi = ring[i].Lat;
                double xj = ring[j].Lon, yj = ring[j].Lat;
                if (((yi > lat) != (yj > lat)) &&
                    (lon < (xj - xi) * (lat - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
            return inside;
        }

        private void OnWorldMapNECountryTapped(NaturalEarthCountry country)
        {
            if (_worldMapLevel == "world")
            {
                // Drill into this continent
                _worldMapStack.Push("world");
                ShowWorldMapLevel("continent", country.Continent);
            }
            else if (_worldMapLevel == "continent")
            {
                // Drill into this country's subdivisions
                var gfr = _geofabrikIndex.GetRegionByIso(country.IsoA2);
                if (gfr != null && _geofabrikIndex.HasChildren(gfr.Id))
                {
                    _worldMapStack.Push("continent:" + _worldMapContinent);
                    ShowWorldMapLevel("country", country.IsoA2);
                }
                else if (gfr != null)
                {
                    // Leaf country — zoom into it on the map
                    _worldMapStack.Push("continent:" + _worldMapContinent);
                    ShowWorldMapLevel("country", country.IsoA2);
                }
            }
            else if (_worldMapLevel == "country")
            {
                // Tapped the leaf country polygon — open browse list for download
                var gfr = _geofabrikIndex.GetRegionByIso(country.IsoA2);
                if (gfr != null)
                    FallBackToBrowse(gfr.Id);
            }
        }

        private void OnWorldMapGfRegionTapped(GeofabrikRegion region)
        {
            // At country level — open the browse list for this subdivision
            FallBackToBrowse(region.Id);
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
            }
            else
            {
                TxtBrowseTitle.Text = _geofabrikIndex.GetBreadcrumb(parentId);
                BtnBrowseBack.Visibility = Visibility.Visible;
            }

            var children = parentId == null
                ? _geofabrikIndex.GetRoots()
                : _geofabrikIndex.GetChildren(parentId);

            var items = new List<BrowseRegionItem>();

            foreach (var region in children)
            {
                bool isContinent = _geofabrikIndex.IsContinent(region.Id);
                bool hasChildren = _geofabrikIndex.HasChildren(region.Id);

                items.Add(new BrowseRegionItem
                {
                    Id = region.Id,
                    Name = region.Name,
                    DownloadVisibility = (isContinent && hasChildren) ? Visibility.Collapsed : Visibility.Visible,
                    DrillVisibility = hasChildren ? Visibility.Visible : Visibility.Collapsed
                });
            }

            LvBrowse.ItemsSource = items;
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
            if (_browseStack.Count == 0)
            {
                // Already at continent root — go back to My Maps
                BrowseView.Visibility = Visibility.Collapsed;
                MyMapsView.Visibility = Visibility.Visible;
                RefreshMyMapsList();
                return;
            }

            _browseStack.Pop();

            if (_browseStack.Count > 0)
                ShowBrowseLevel(_browseStack.Peek());
            else
                ShowBrowseLevel(null); // back to continents root
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

            string countryKey = _geofabrikIndex.GetCountryId(regionId);

            // If region has children, collect leaves and offer batch download
            if (_geofabrikIndex.HasChildren(regionId))
            {
                var leaves = _geofabrikIndex.GetLeaves(regionId);
                string leafNames = string.Join("\n", leaves.Select(l => "  \u2022 " + l.Name));
                var dialog = new MessageDialog(
                    $"This will download all {leaves.Count} sub-regions of \"{geoRegion.Name}\" one by one:\n\n{leafNames}",
                    "Download All Sub-regions");
                dialog.Commands.Add(new UICommand("Download All"));
                dialog.Commands.Add(new UICommand("Cancel"));
                dialog.DefaultCommandIndex = 0;
                dialog.CancelCommandIndex = 1;
                var result = await dialog.ShowAsync();
                if (result.Label != "Download All") return;

                var regions = leaves.Select(l =>
                {
                    var r = MapRegion.FromGeofabrik(l);
                    r.CountryKey = countryKey;
                    return r;
                }).ToList();

                MapManagerPanel.Visibility = Visibility.Collapsed;
                await StartBatchDownloadAndImport(regions);
                return;
            }

            _selectedRegion = MapRegion.FromGeofabrik(geoRegion);
            // Resolve country via parent chain (IDs have no slashes in Geofabrik JSON)
            _selectedRegion.CountryKey = countryKey;

            // If this exact region is already installed, ask before re-downloading
            var installedIds = GetInstalledRegionIds();
            if (installedIds.Contains(regionId))
            {
                var dialog = new MessageDialog(
                    $"\"{geoRegion.Name}\" is already installed. Download and re-import it?",
                    "Already Downloaded");
                dialog.Commands.Add(new UICommand("Re-download"));
                dialog.Commands.Add(new UICommand("Cancel"));
                dialog.DefaultCommandIndex = 1;
                dialog.CancelCommandIndex = 1;
                var result = await dialog.ShowAsync();
                if (result.Label != "Re-download") return;
            }

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
                await DownloadAndImportRegionAsync(_selectedRegion, _cts.Token);
                FinalizeMapAfterImport(_selectedRegion.CountryKey);
            }
            catch (OperationCanceledException)
            {
                TxtOverlayStatus.Text = "Cancelled. Cleaning up...";
                await CleanupPartialData();
                TxtOverlayStatus.Text = "Cancelled.";
                TxtOverlayDetail.Text = "";

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
            catch (Download.DownloadStalledException)
            {
                TxtOverlayStatus.Text = "Download stalled — no data received for 10 seconds.";
                TxtOverlayDetail.Text = "The partial download is kept. Tap Retry to resume.";
                BtnDownload.Content = "Retry";
                BtnDownload.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                TxtOverlayStatus.Text = GetDownloadErrorMessage(ex);
                TxtOverlayDetail.Text = "";
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

        private async Task StartBatchDownloadAndImport(List<MapRegion> regions)
        {
            OverlayPanel.Visibility = Visibility.Visible;
            BtnDownload.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Visible;
            _cts = new CancellationTokenSource();

            var displayRequest = new DisplayRequest();
            displayRequest.RequestActive();

            int total = regions.Count;
            int done = 0;
            string lastCountryKey = regions.Count > 0 ? regions[0].CountryKey : null;

            InitBatchSegmentBar(total);
            UpdateBatchSegments(0, 0);

            try
            {
                foreach (var region in regions)
                {
                    _selectedRegion = region; // keep in sync so CleanupPartialData works
                    UpdateBatchSegments(done, done);
                    lastCountryKey = region.CountryKey;

                    await DownloadAndImportRegionAsync(region, _cts.Token);
                    done++;
                }

                UpdateBatchSegments(done, -1); // all green
                if (lastCountryKey != null)
                    FinalizeMapAfterImport(lastCountryKey);
            }
            catch (OperationCanceledException)
            {
                TxtOverlayStatus.Text = "Cancelled. Cleaning up...";
                await CleanupPartialData();
                TxtOverlayStatus.Text = $"Cancelled after {done} of {total} regions.";
                TxtOverlayDetail.Text = "";

                if (GetMapNames().Count == 0)
                {
                    OverlayPanel.Visibility = Visibility.Collapsed;
                    ShowMapManager(startInBrowse: true);
                }
                else
                {
                    // Already-completed regions are kept; just close overlay
                    if (lastCountryKey != null && done > 0)
                        FinalizeMapAfterImport(lastCountryKey);
                    else
                        OverlayPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Download.DownloadStalledException)
            {
                TxtOverlayStatus.Text = $"Stalled on region {done + 1} of {total} — no data for 10 s.";
                TxtOverlayDetail.Text = "The partial download is kept. You can retry manually.";
                BtnDownload.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                TxtOverlayStatus.Text = $"Error on region {done + 1} of {total}: {GetDownloadErrorMessage(ex)}";
                TxtOverlayDetail.Text = "";
                BtnDownload.Visibility = Visibility.Collapsed; // no retry for batch
            }
            finally
            {
                BatchSegmentBar.Visibility = Visibility.Collapsed;
                BatchSegmentBar.Children.Clear();
                _batchSegmentBorders = null;
                BtnCancel.Visibility = Visibility.Collapsed;
                BtnCancel.IsEnabled = true;
                displayRequest.RequestRelease();
            }
        }

        private Border[] _batchSegmentBorders;

        private void InitBatchSegmentBar(int count)
        {
            BatchSegmentBar.Children.Clear();
            _batchSegmentBorders = new Border[count];

            // Fit segments into ~336px (400 MaxWidth − 2×32 padding), 3px gap between
            int segWidth = Math.Max(6, Math.Min(40, (336 - (count - 1) * 3) / count));

            for (int i = 0; i < count; i++)
            {
                var b = new Border
                {
                    Width = segWidth,
                    Height = 10,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
                    Background = new SolidColorBrush(Color.FromArgb(255, 158, 158, 158))
                };
                BatchSegmentBar.Children.Add(b);
                _batchSegmentBorders[i] = b;
            }

            BatchSegmentBar.Visibility = Visibility.Visible;
        }

        private void UpdateBatchSegments(int completedCount, int currentIndex)
        {
            if (_batchSegmentBorders == null) return;
            for (int i = 0; i < _batchSegmentBorders.Length; i++)
            {
                Color c;
                if (i < completedCount)
                    c = Color.FromArgb(255, 76, 175, 80);       // green — done
                else if (i == currentIndex)
                    c = Color.FromArgb(255, 100, 181, 246);     // light blue — in progress
                else
                    c = Color.FromArgb(255, 158, 158, 158);     // grey — pending
                _batchSegmentBorders[i].Background = new SolidColorBrush(c);
            }
        }

        /// <summary>
        /// Downloads and imports a single region into the shared country DB.
        /// Caller is responsible for UI setup (overlay visible, CTS) and teardown.
        /// </summary>
        private async Task DownloadAndImportRegionAsync(MapRegion region, CancellationToken ct)
        {
            string pbfPath = await _downloadManager.GetExistingMapPath(region);

            if (pbfPath == null)
            {
                TxtOverlayTitle.Text = "Downloading...";
                TxtOverlayStatus.Text = region.Name;
                ProgressOverlay.Value = 0;
                TxtOverlayDetail.Text = "";

                _downloadManager.OnProgress += OnDownloadProgress;
                pbfPath = await _downloadManager.DownloadAsync(region, ct);
                _downloadManager.OnProgress -= OnDownloadProgress;
            }

            TxtOverlayTitle.Text = "Importing...";
            TxtOverlayStatus.Text = region.Name;
            ProgressOverlay.Value = 0;
            TxtOverlayDetail.Text = "";

            string dbPath = await _downloadManager.GetDatabasePath(region);

            // Open the correct country DB.
            // If a different DB is currently open (e.g. the previously-active map), close it
            // first so we don't accidentally import into the wrong file.
            if (_db == null || _db.DbPath != dbPath)
            {
                _renderer = null;    // prevent rendering with a DB that's about to be closed
                _db?.Dispose();
                _db = null;
                _db = new MapDatabase(dbPath);
                await _db.OpenAsync();
            }

            var importer = new MapImporter();
            importer.OnProgress += OnImportProgress;
            await importer.ImportAsync(pbfPath, _db, ct, region.Id, region.Name);
            importer.OnProgress -= OnImportProgress;

            string countryKey = region.CountryKey;
            string countryDisplayName = GetCountryDisplayName(countryKey, region.Id);
            SaveMapName(countryKey, countryDisplayName);
            SetActiveMapId(countryKey);
            AddInstalledRegion(region.Id, countryKey);
        }

        /// <summary>
        /// Centers the viewport on the current DB bounds and shows the map.
        /// Called once after all regions in a (batch) import have completed.
        /// </summary>
        private void FinalizeMapAfterImport(string countryKey)
        {
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

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            BtnCancel.IsEnabled = false;
            TxtOverlayStatus.Text = "Cancelling...";
        }

        private async Task CleanupPartialData()
        {
            // Do NOT delete the country DB — it may already contain other successfully imported regions.
            // The incomplete region was never recorded in the regions table (InsertRegion is called only
            // at the end of a successful import), so it simply doesn't appear as installed.
            // Partially committed ways in the DB are harmless: INSERT OR IGNORE skips them on re-import.
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

                    // Delete the downloaded PBF and its .partial temp file
                    string partialPath = Path.Combine(mapsFolder.Path, region.FileName + ".partial");
                    if (File.Exists(partialPath)) File.Delete(partialPath);

                    string pbfPath = Path.Combine(mapsFolder.Path, region.FileName);
                    if (File.Exists(pbfPath)) File.Delete(pbfPath);

                    // Country DB is intentionally preserved
                }
                catch { }
            });
        }

        private static string GetDownloadErrorMessage(Exception ex)
        {
            if (ex is OperationCanceledException)
                return "Download cancelled.";

            if (ex is Download.HttpStatusException httpEx)
            {
                switch (httpEx.StatusCode)
                {
                    case 403: return "Download blocked (403 Forbidden). The server refused the request.";
                    case 404: return "File not found on server (404). The map may have moved or been renamed.";
                    case 429: return "Too many requests (429). Please wait a moment and retry.";
                    case 503: return "Server unavailable (503). Please try again later.";
                    default:  return $"Download failed (HTTP {httpEx.StatusCode}). Please check your connection and retry.";
                }
            }

            if (ex is System.Net.Http.HttpRequestException)
                return "Download failed. Please check your internet connection and retry.";

            if (ex is IOException)
                return $"Storage error: {ex.Message}";

            return $"Download failed: {ex.Message}";
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

        // ---- Preferences ----

        private void ShowPreferences()
        {
            PreferencesPanel.Visibility = Visibility.Visible;

            // App version from package identity
            var ver = Windows.ApplicationModel.Package.Current.Id.Version;
            TxtAppVersion.Text = $"WinMaps {ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";
            TxtBuildDate.Text = $"Built {BuildInfo.Date}";

            RefreshTempSizes();
        }

        private void BtnClosePreferences_Click(object sender, RoutedEventArgs e)
        {
            PreferencesPanel.Visibility = Visibility.Collapsed;
        }

        private void RefreshTempSizes()
        {
            string mapsFolder = GetMapsFolder();

            // PBF files
            long pbfBytes = GetFolderSize(mapsFolder, "*.osm.pbf");
            TxtPbfSize.Text = pbfBytes > 0
                ? $"{pbfBytes / 1048576.0:F1} MB"
                : "None";

            // Partial downloads
            long partialBytes = GetFolderSize(mapsFolder, "*.partial");
            TxtPartialSize.Text = partialBytes > 0
                ? $"{partialBytes / 1048576.0:F1} MB"
                : "None";

            // Geofabrik cache JSON files
            long cacheBytes = GetFolderSize(mapsFolder, "*.json");
            TxtCacheSize.Text = cacheBytes > 0
                ? $"{cacheBytes / 1048576.0:F1} MB"
                : "None";
        }

        private static long GetFolderSize(string folder, string pattern)
        {
            try
            {
                long total = 0;
                foreach (var f in Directory.GetFiles(folder, pattern))
                    total += new FileInfo(f).Length;
                return total;
            }
            catch { return 0; }
        }

        private void BtnClearPbf_Click(object sender, RoutedEventArgs e)
        {
            DeleteFilesInMapsFolder("*.osm.pbf");
            RefreshTempSizes();
        }

        private void BtnClearPartial_Click(object sender, RoutedEventArgs e)
        {
            DeleteFilesInMapsFolder("*.partial");
            RefreshTempSizes();
        }

        private void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            DeleteFilesInMapsFolder("*.json");
            // Invalidate in-memory indexes so they re-download when needed
            _geofabrikIndex = new GeofabrikIndex();
            _geoIndex = new GeofabrikGeoIndex();
            RefreshTempSizes();
        }

        private void DeleteFilesInMapsFolder(string pattern)
        {
            try
            {
                string mapsFolder = GetMapsFolder();
                foreach (var f in Directory.GetFiles(mapsFolder, pattern))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
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
                // If the cache is empty but we now have valid dimensions, trigger a load.
                // This handles the startup race where RedrawMap ran before layout completed.
                if (!_renderer.HasCachedData && sender.ActualWidth > 0 && sender.ActualHeight > 0)
                {
                    RedrawMap();
                }

                try
                {
                    _renderer.Draw(args.DrawingSession, sender);

                    if (!double.IsNaN(_gpsLat))
                    {
                        _renderer.DrawGpsPosition(args.DrawingSession, _gpsLat, _gpsLon, _gpsAccuracy);
                    }
                }
                catch (Exception ex)
                {
                    _lastRenderError = ex.ToString();
                }
            }
        }

        private async void RedrawMap()
        {
            try
            {
                // Show stale cached data immediately so the UI never freezes during pans.
                // Only works when zoom hasn't changed — Mercator coords are zoom-dependent,
                // so stale data at a different zoom renders at the wrong scale.
                bool zoomSame = _renderer != null && Math.Abs(_renderer.CacheZoom - _viewport.Zoom) < 0.01;
                if (zoomSame)
                    MapCanvas.Invalidate();
                TxtZoom.Text = $"Z{_viewport.Zoom:F0}";
                TxtStatus.Text = $"{_viewport.CenterLat:F4}° N, {_viewport.CenterLon:F4}° E";
                SaveViewport();

                // Then reload data in the background and repaint when ready
                if (_renderer != null)
                {
                    await _renderer.EnsureCacheAsync();
                    MapCanvas.Invalidate();
                }
            }
            catch (Exception ex)
            {
                _lastRenderError = ex.ToString();
            }
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
            UpdateGpsIcon();
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
            UpdateGpsIcon();
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
            UpdateGpsIcon();
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

        private void UpdateGpsIcon()
        {
            GpsIcon.Foreground = _followGps
                ? new SolidColorBrush(Colors.Gold)
                : new SolidColorBrush(Colors.White);
        }

        private void BtnGps_Click(object sender, RoutedEventArgs e)
        {
            if (!double.IsNaN(_gpsLat))
            {
                _viewport.CenterLat = _gpsLat;
                _viewport.CenterLon = _gpsLon;
                _followGps = true;
                UpdateGpsIcon();
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
            double newLat = coord.Point.Position.Latitude;
            double newLon = coord.Point.Position.Longitude;
            double newAcc = coord.Accuracy;

            _gpsTargetLat = newLat;
            _gpsTargetLon = newLon;
            _gpsTargetAccuracy = newAcc;

            // First fix: snap immediately, no animation
            if (double.IsNaN(_gpsLat))
            {
                _gpsLat = newLat;
                _gpsLon = newLon;
                _gpsAccuracy = newAcc;
            }

            if (!_initialGpsCentered)
            {
                _viewport.CenterLat = newLat;
                _viewport.CenterLon = newLon;
                _initialGpsCentered = true;
                _followGps = true;
                UpdateGpsIcon();
            }

            // Start animation timer if not already running
            if (_gpsAnimTimer == null)
            {
                _gpsAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                _gpsAnimTimer.Tick += GpsAnimTimer_Tick;
                _gpsAnimTimer.Start();
            }
        }

        private void GpsAnimTimer_Tick(object sender, object e)
        {
            if (double.IsNaN(_gpsTargetLat))
            {
                _gpsAnimTimer.Stop();
                _gpsAnimTimer = null;
                return;
            }

            // Lerp displayed position toward target
            double dLat = _gpsTargetLat - _gpsLat;
            double dLon = _gpsTargetLon - _gpsLon;

            // Stop animating when close enough (sub-pixel at any zoom)
            if (dLat * dLat + dLon * dLon < 1e-14)
            {
                _gpsLat = _gpsTargetLat;
                _gpsLon = _gpsTargetLon;
                _gpsAccuracy = _gpsTargetAccuracy;
                _gpsAnimTimer.Stop();
                _gpsAnimTimer = null;

                if (_followGps)
                {
                    _viewport.CenterLat = _gpsLat;
                    _viewport.CenterLon = _gpsLon;
                }
                MapCanvas.Invalidate();
                return;
            }

            _gpsLat += dLat * GpsSmoothFactor;
            _gpsLon += dLon * GpsSmoothFactor;
            _gpsAccuracy += (_gpsTargetAccuracy - _gpsAccuracy) * GpsSmoothFactor;

            if (_followGps)
            {
                _viewport.CenterLat = _gpsLat;
                _viewport.CenterLon = _gpsLon;
            }

            MapCanvas.Invalidate();
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
                _viewport.Zoom = 16; // always reset to Z16 on start to avoid crash at high zoom levels
            }
        }

        private bool HasSavedViewport()
        {
            return ApplicationData.Current.LocalSettings.Values.ContainsKey("viewport_lat");
        }
    }
}
