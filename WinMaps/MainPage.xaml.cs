using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;
using Windows.Foundation;
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
            _cts?.Cancel();
            _geolocator = null;
            _db?.Dispose();
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
                    if (bounds.HasValue)
                    {
                        _viewport.ZoomToBounds(bounds.Value.minLat, bounds.Value.maxLat,
                            bounds.Value.minLon, bounds.Value.maxLon);
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
                TxtOverlayStatus.Text = "Download cancelled.";
                BtnDownload.IsEnabled = true;
            }
            catch (Exception ex)
            {
                TxtOverlayStatus.Text = $"Error: {ex.Message}";
                BtnDownload.IsEnabled = true;
            }
            finally
            {
                displayRequest.RequestRelease();
            }
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

        private void RedrawMap()
        {
            if (_renderer != null)
            {
                _renderer.Draw(MapCanvas);

                if (!double.IsNaN(_gpsLat))
                {
                    _renderer.DrawGpsPosition(MapCanvas, _gpsLat, _gpsLon, _gpsAccuracy);
                }
            }

            TxtZoom.Text = $"Z{_viewport.Zoom:F0}";
            TxtStatus.Text = $"{_viewport.CenterLat:F4}° N, {_viewport.CenterLon:F4}° E";
        }

        private void MapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _viewport.ScreenWidth = e.NewSize.Width;
            _viewport.ScreenHeight = e.NewSize.Height;
            _renderer?.InvalidateCache();
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
            _renderer?.InvalidateCache();
            RedrawMap();
            e.Handled = true;
        }

        private void MapCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isPanning = false;
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void MapCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(MapCanvas);
            int delta = point.Properties.MouseWheelDelta;
            double zoomDelta = delta > 0 ? 0.5 : -0.5;

            _viewport.ZoomAt(point.Position.X, point.Position.Y, zoomDelta);
            _followGps = false;
            _renderer?.InvalidateCache();
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
            _renderer?.InvalidateCache();
            RedrawMap();
            e.Handled = true;
        }

        // ---- Zoom Buttons ----

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _viewport.ZoomAt(_viewport.ScreenWidth / 2, _viewport.ScreenHeight / 2, 1);
            _renderer?.InvalidateCache();
            RedrawMap();
        }

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _viewport.ZoomAt(_viewport.ScreenWidth / 2, _viewport.ScreenHeight / 2, -1);
            _renderer?.InvalidateCache();
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
                _renderer?.InvalidateCache();
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

            if (_followGps)
            {
                _viewport.CenterLat = _gpsLat;
                _viewport.CenterLon = _gpsLon;
                _renderer?.InvalidateCache();
            }

            RedrawMap();
        }
    }
}
