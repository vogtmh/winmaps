using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.Storage;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using WinMaps.Data;
using WinMaps.Download;
using WinMaps.Rendering;

namespace WinMaps
{
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

        // Selected region
        private MapRegion _selectedRegion = MapRegion.AvailableRegions[0]; // Stuttgart

        public MainPage()
        {
            this.InitializeComponent();
            _viewport = new MapViewport();
            _downloadManager = new MapDownloadManager();
            _cts = new CancellationTokenSource();

            this.Loaded += MainPage_Loaded;
            this.Unloaded += MainPage_Unloaded;

            RestoreViewport();
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
            SaveViewport();
            _cts?.Cancel();
            _geolocator = null;
            _db?.Dispose();
            MapCanvas.RemoveFromVisualTree();
            MapCanvas = null;
        }

        private async Task InitializeAsync()
        {
            string dbPath = await _downloadManager.GetDatabasePath(_selectedRegion);
            _db = new MapDatabase(dbPath);

            try
            {
                await _db.OpenAsync();

                if (_db.HasData())
                {
                    _renderer = new MapRenderer(_db, _viewport);

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
                // Database doesn't exist or is corrupt
            }

            OverlayPanel.Visibility = Visibility.Visible;
            TxtOverlayStatus.Text = "No map data found. Download a region to get started.";
            BtnDownload.IsEnabled = true;
        }

        // ---- Download & Import ----

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            BtnDownload.IsEnabled = false;
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

                _renderer = new MapRenderer(_db, _viewport);

                var bounds = _db.GetBounds();
                if (bounds.HasValue)
                {
                    _viewport.ZoomToBounds(bounds.Value.minLat, bounds.Value.maxLat,
                        bounds.Value.minLon, bounds.Value.maxLon);
                }

                OverlayPanel.Visibility = Visibility.Collapsed;
                StartGps();
                RedrawMap();
            }
            catch (OperationCanceledException)
            {
                TxtOverlayStatus.Text = "Cancelled. Cleaning up...";
                await CleanupPartialData();
                TxtOverlayStatus.Text = "Cancelled.";
                TxtOverlayDetail.Text = "";
                BtnDownload.IsEnabled = true;
            }
            catch (Exception ex)
            {
                TxtOverlayStatus.Text = $"Error: {ex.Message}";
                BtnDownload.IsEnabled = true;
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
            // Close DB so the file can be deleted
            _db?.Dispose();
            _db = null;

            await Task.Run(async () =>
            {
                try
                {
                    var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                    var mapsFolder = await localFolder.GetFolderAsync("Maps");

                    // Delete partial download
                    string partialPath = System.IO.Path.Combine(mapsFolder.Path, _selectedRegion.FileName + ".partial");
                    if (System.IO.File.Exists(partialPath))
                        System.IO.File.Delete(partialPath);

                    // Delete completed PBF
                    string pbfPath = System.IO.Path.Combine(mapsFolder.Path, _selectedRegion.FileName);
                    if (System.IO.File.Exists(pbfPath))
                        System.IO.File.Delete(pbfPath);

                    // Delete database
                    string dbPath = System.IO.Path.Combine(mapsFolder.Path,
                        System.IO.Path.ChangeExtension(_selectedRegion.FileName, ".db"));
                    if (System.IO.File.Exists(dbPath))
                        System.IO.File.Delete(dbPath);

                    // WAL/SHM files
                    if (System.IO.File.Exists(dbPath + "-wal"))
                        System.IO.File.Delete(dbPath + "-wal");
                    if (System.IO.File.Exists(dbPath + "-shm"))
                        System.IO.File.Delete(dbPath + "-shm");
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

            // Start loading new data if we've panned outside the cache bounds
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

            // Start loading new data if we've panned/zoomed outside the cache bounds
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
