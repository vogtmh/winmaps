using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;
using WinMaps.Data;

namespace WinMaps.Rendering
{
    internal class MapRenderer
    {
        private readonly MapDatabase _db;
        private readonly MapViewport _viewport;

        // Cached way geometries for the current viewport
        private List<CachedWay> _cachedWays;
        private double _cacheMinLat, _cacheMaxLat, _cacheMinLon, _cacheMaxLon;
        private double _cacheZoom;
        private const double CacheMarginFactor = 0.3;

        // Way limit to prevent overwhelming low-end devices
        private const int MaxWaysPerFrame = 5000;

        public MapRenderer(MapDatabase db, MapViewport viewport)
        {
            _db = db;
            _viewport = viewport;
        }

        public void InvalidateCache()
        {
            _cachedWays = null;
        }

        public void Draw(Canvas canvas)
        {
            canvas.Children.Clear();

            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;
            if (width <= 0 || height <= 0) return;

            _viewport.ScreenWidth = width;
            _viewport.ScreenHeight = height;

            if (_db == null || !_db.HasData())
                return;

            EnsureCache();

            if (_cachedWays == null || _cachedWays.Count == 0)
                return;

            // Draw in layer order: parks -> water -> roads
            DrawLayer(canvas, _cachedWays, (int)Pbf.OsmElementType.Park);
            DrawLayer(canvas, _cachedWays, (int)Pbf.OsmElementType.Water);
            DrawLayer(canvas, _cachedWays, (int)Pbf.OsmElementType.Road);
        }

        private void EnsureCache()
        {
            var bounds = _viewport.GetBounds();

            if (_cachedWays != null &&
                Math.Abs(_cacheZoom - _viewport.Zoom) < 0.01 &&
                bounds.minLat >= _cacheMinLat && bounds.maxLat <= _cacheMaxLat &&
                bounds.minLon >= _cacheMinLon && bounds.maxLon <= _cacheMaxLon)
            {
                return;
            }

            double latSpan = bounds.maxLat - bounds.minLat;
            double lonSpan = bounds.maxLon - bounds.minLon;
            double latMargin = latSpan * CacheMarginFactor;
            double lonMargin = lonSpan * CacheMarginFactor;

            _cacheMinLat = bounds.minLat - latMargin;
            _cacheMaxLat = bounds.maxLat + latMargin;
            _cacheMinLon = bounds.minLon - lonMargin;
            _cacheMaxLon = bounds.maxLon + lonMargin;
            _cacheZoom = _viewport.Zoom;

            int typeFilter = -1;
            var ways = _db.QueryWaysWithGeometry(_cacheMinLat, _cacheMaxLat, _cacheMinLon, _cacheMaxLon, typeFilter);

            _cachedWays = new List<CachedWay>();
            int count = 0;

            foreach (var (type, subType, points) in ways)
            {
                if (!ShouldDrawAtZoom(type, subType, _viewport.Zoom))
                    continue;

                if (count >= MaxWaysPerFrame)
                    break;

                if (points.Count < 2)
                    continue;

                _cachedWays.Add(new CachedWay
                {
                    Type = type,
                    SubType = subType,
                    Points = points
                });
                count++;
            }
        }

        private bool ShouldDrawAtZoom(int type, string subType, double zoom)
        {
            if (type == (int)Pbf.OsmElementType.Road)
            {
                if (zoom < 8)
                    return subType == "motorway" || subType == "trunk";
                if (zoom < 10)
                    return subType == "motorway" || subType == "trunk" ||
                           subType == "primary" || subType == "motorway_link" || subType == "trunk_link";
                if (zoom < 12)
                    return subType != "footway" && subType != "cycleway" &&
                           subType != "path" && subType != "track" && subType != "service";
                if (zoom < 14)
                    return subType != "footway" && subType != "path";
            }

            return true;
        }

        private void DrawLayer(Canvas canvas, List<CachedWay> ways, int type)
        {
            foreach (var way in ways)
            {
                if (way.Type != type)
                    continue;

                if (type == (int)Pbf.OsmElementType.Park)
                    DrawArea(canvas, way, GetParkColor(way.SubType));
                else if (type == (int)Pbf.OsmElementType.Water)
                    DrawArea(canvas, way, GetWaterColor(way.SubType));
                else
                    DrawRoad(canvas, way);
            }
        }

        private void DrawArea(Canvas canvas, CachedWay way, Color fillColor)
        {
            var first = way.Points[0];
            var last = way.Points[way.Points.Count - 1];
            bool isClosed = way.Points.Count >= 3 &&
                           Math.Abs(first.lat - last.lat) < 0.0000001 &&
                           Math.Abs(first.lon - last.lon) < 0.0000001;

            if (isClosed)
            {
                var polygon = new Polygon();
                polygon.Fill = new SolidColorBrush(fillColor);

                var points = new PointCollection();
                foreach (var (lat, lon) in way.Points)
                {
                    var (x, y) = _viewport.GeoToScreen(lat, lon);
                    points.Add(new Point(x, y));
                }
                polygon.Points = points;

                Canvas.SetLeft(polygon, 0);
                Canvas.SetTop(polygon, 0);
                canvas.Children.Add(polygon);
            }
            else
            {
                DrawPolylineShape(canvas, way.Points, fillColor, 2);
            }
        }

        private void DrawRoad(Canvas canvas, CachedWay way)
        {
            Color color = GetRoadColor(way.SubType);
            double width = GetRoadWidth(way.SubType, _viewport.Zoom);

            if (_viewport.Zoom >= 13 && width >= 2)
            {
                Color outlineColor = GetRoadOutlineColor(way.SubType);
                DrawPolylineShape(canvas, way.Points, outlineColor, width + 2);
            }

            DrawPolylineShape(canvas, way.Points, color, width);
        }

        private void DrawPolylineShape(Canvas canvas, List<(double lat, double lon)> points, Color color, double width)
        {
            var polyline = new Polyline();
            polyline.Stroke = new SolidColorBrush(color);
            polyline.StrokeThickness = width;
            polyline.StrokeLineJoin = PenLineJoin.Round;
            polyline.StrokeStartLineCap = PenLineCap.Round;
            polyline.StrokeEndLineCap = PenLineCap.Round;

            var pointCollection = new PointCollection();
            bool anyVisible = false;

            foreach (var (lat, lon) in points)
            {
                var (x, y) = _viewport.GeoToScreen(lat, lon);
                pointCollection.Add(new Point(x, y));

                if (x > -200 && x < _viewport.ScreenWidth + 200 &&
                    y > -200 && y < _viewport.ScreenHeight + 200)
                {
                    anyVisible = true;
                }
            }

            if (!anyVisible) return;

            polyline.Points = pointCollection;
            Canvas.SetLeft(polyline, 0);
            Canvas.SetTop(polyline, 0);
            canvas.Children.Add(polyline);
        }

        public void DrawGpsPosition(Canvas canvas, double lat, double lon, double accuracy)
        {
            var (x, y) = _viewport.GeoToScreen(lat, lon);

            if (accuracy > 0 && accuracy < 500)
            {
                double metersPerPixel = _viewport.MetersPerPixel;
                double radiusPixels = accuracy / metersPerPixel;
                if (radiusPixels > 3 && radiusPixels < 500)
                {
                    var accCircle = new Ellipse();
                    accCircle.Width = radiusPixels * 2;
                    accCircle.Height = radiusPixels * 2;
                    accCircle.Fill = new SolidColorBrush(Color.FromArgb(40, 0, 120, 255));
                    accCircle.Stroke = new SolidColorBrush(Color.FromArgb(80, 0, 120, 255));
                    accCircle.StrokeThickness = 1;
                    Canvas.SetLeft(accCircle, x - radiusPixels);
                    Canvas.SetTop(accCircle, y - radiusPixels);
                    canvas.Children.Add(accCircle);
                }
            }

            var outerDot = new Ellipse();
            outerDot.Width = 16;
            outerDot.Height = 16;
            outerDot.Fill = new SolidColorBrush(Colors.White);
            Canvas.SetLeft(outerDot, x - 8);
            Canvas.SetTop(outerDot, y - 8);
            canvas.Children.Add(outerDot);

            var innerDot = new Ellipse();
            innerDot.Width = 12;
            innerDot.Height = 12;
            innerDot.Fill = new SolidColorBrush(Color.FromArgb(255, 0, 120, 255));
            Canvas.SetLeft(innerDot, x - 6);
            Canvas.SetTop(innerDot, y - 6);
            canvas.Children.Add(innerDot);
        }

        private Color GetRoadColor(string subType)
        {
            switch (subType)
            {
                case "motorway":
                case "motorway_link":
                    return Color.FromArgb(255, 233, 144, 160);
                case "trunk":
                case "trunk_link":
                    return Color.FromArgb(255, 249, 178, 156);
                case "primary":
                case "primary_link":
                    return Color.FromArgb(255, 252, 214, 164);
                case "secondary":
                case "secondary_link":
                    return Color.FromArgb(255, 246, 250, 187);
                case "tertiary":
                case "tertiary_link":
                case "residential":
                case "living_street":
                case "unclassified":
                case "service":
                    return Colors.White;
                case "pedestrian":
                    return Color.FromArgb(255, 221, 221, 238);
                case "footway":
                case "path":
                    return Color.FromArgb(255, 250, 128, 114);
                case "cycleway":
                    return Color.FromArgb(255, 0, 68, 204);
                case "track":
                    return Color.FromArgb(255, 177, 140, 75);
                default:
                    return Color.FromArgb(255, 200, 200, 200);
            }
        }

        private Color GetRoadOutlineColor(string subType)
        {
            switch (subType)
            {
                case "motorway":
                case "motorway_link":
                    return Color.FromArgb(255, 196, 80, 108);
                case "trunk":
                case "trunk_link":
                    return Color.FromArgb(255, 200, 130, 100);
                case "primary":
                case "primary_link":
                    return Color.FromArgb(255, 200, 170, 110);
                default:
                    return Color.FromArgb(255, 190, 190, 190);
            }
        }

        private double GetRoadWidth(string subType, double zoom)
        {
            double baseWidth;
            switch (subType)
            {
                case "motorway":
                case "motorway_link":
                    baseWidth = 4.0; break;
                case "trunk":
                case "trunk_link":
                    baseWidth = 3.5; break;
                case "primary":
                case "primary_link":
                    baseWidth = 3.0; break;
                case "secondary":
                case "secondary_link":
                    baseWidth = 2.5; break;
                case "tertiary":
                case "tertiary_link":
                    baseWidth = 2.0; break;
                case "residential":
                case "living_street":
                case "unclassified":
                    baseWidth = 1.5; break;
                case "service":
                    baseWidth = 1.0; break;
                case "footway":
                case "path":
                case "cycleway":
                case "track":
                    baseWidth = 0.8; break;
                case "pedestrian":
                    baseWidth = 1.5; break;
                default:
                    baseWidth = 1.0; break;
            }

            if (zoom >= 16) return baseWidth * 2.5;
            if (zoom >= 14) return baseWidth * 1.8;
            if (zoom >= 12) return baseWidth * 1.2;
            if (zoom >= 10) return baseWidth * 0.8;
            return Math.Max(baseWidth * 0.5, 1.0);
        }

        private Color GetWaterColor(string subType)
        {
            return Color.FromArgb(255, 170, 211, 223);
        }

        private Color GetParkColor(string subType)
        {
            switch (subType)
            {
                case "forest":
                case "wood":
                    return Color.FromArgb(255, 173, 209, 158);
                case "grass":
                case "meadow":
                case "farmland":
                    return Color.FromArgb(255, 205, 235, 176);
                case "park":
                case "garden":
                    return Color.FromArgb(255, 200, 250, 204);
                default:
                    return Color.FromArgb(255, 195, 225, 178);
            }
        }

        private class CachedWay
        {
            public int Type;
            public string SubType;
            public List<(double lat, double lon)> Points;
        }
    }
}
