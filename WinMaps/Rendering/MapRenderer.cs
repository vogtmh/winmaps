using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;
using WinMaps.Data;

namespace WinMaps.Rendering
{
    internal class MapRenderer
    {
        private readonly MapDatabase _db;
        private readonly MapViewport _viewport;
        private MapTheme _theme;

        public MapTheme Theme
        {
            get { return _theme; }
            set { _theme = value; }
        }

        // Cached way geometries for the current viewport
        private List<CachedWay> _cachedWays;
        private double _cacheMinLat, _cacheMaxLat, _cacheMinLon, _cacheMaxLon;
        private double _cacheZoom;
        private const double CacheMarginFactor = 0.5;

        private bool _isLoading;
        private bool _pendingReload;
        private readonly CanvasStrokeStyle _roundStroke;

        public MapRenderer(MapDatabase db, MapViewport viewport, MapTheme theme = null)
        {
            _db = db;
            _viewport = viewport;
            _theme = theme ?? MapTheme.Light;
            _roundStroke = new CanvasStrokeStyle
            {
                LineJoin = CanvasLineJoin.Round,
                StartCap = CanvasCapStyle.Round,
                EndCap = CanvasCapStyle.Round
            };
        }

        public void InvalidateCache()
        {
            _cachedWays = null;
        }

        public void Draw(CanvasDrawingSession ds, ICanvasResourceCreator rc)
        {
            if (_cachedWays == null || _cachedWays.Count == 0) return;

            // Layer order: parks → water → road outlines → roads
            foreach (var way in _cachedWays)
            {
                if (way.Type == (int)Pbf.OsmElementType.Park)
                    DrawArea(ds, rc, way, _theme.GetParkColor(way.SubType));
            }

            foreach (var way in _cachedWays)
            {
                if (way.Type == (int)Pbf.OsmElementType.Water)
                    DrawArea(ds, rc, way, _theme.WaterColor);
            }

            // Road outlines (drawn first, thicker, darker)
            if (_viewport.Zoom >= 13)
            {
                foreach (var way in _cachedWays)
                {
                    if (way.Type != (int)Pbf.OsmElementType.Road) continue;
                    float width = GetRoadWidth(way.SubType, _viewport.Zoom);
                    if (width >= 2)
                    {
                        DrawPolyline(ds, rc, way.Points, _theme.GetRoadOutlineColor(way.SubType), width + 2);
                    }
                }
            }

            // Road fills
            foreach (var way in _cachedWays)
            {
                if (way.Type != (int)Pbf.OsmElementType.Road) continue;
                float width = GetRoadWidth(way.SubType, _viewport.Zoom);
                DrawPolyline(ds, rc, way.Points, _theme.GetRoadColor(way.SubType), width);
            }
        }

        public async Task EnsureCacheAsync()
        {
            if (_isLoading)
            {
                _pendingReload = true;
                return;
            }

            double width = _viewport.ScreenWidth;
            double height = _viewport.ScreenHeight;
            if (width <= 0 || height <= 0) return;

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

            double queryMinLat = _cacheMinLat, queryMaxLat = _cacheMaxLat;
            double queryMinLon = _cacheMinLon, queryMaxLon = _cacheMaxLon;
            double queryZoom = _cacheZoom;

            _isLoading = true;

            var newCachedWays = await Task.Run(() =>
            {
                var ways = _db.QueryWaysForZoom(queryMinLat, queryMaxLat, queryMinLon, queryMaxLon, queryZoom);

                var result = new List<CachedWay>(ways.Count);
                foreach (var (type, subType, points) in ways)
                {
                    result.Add(new CachedWay
                    {
                        Type = type,
                        SubType = subType,
                        Points = points
                    });
                }

                return result;
            });

            _cachedWays = newCachedWays;
            _isLoading = false;

            // If a reload was requested while we were loading, re-run
            if (_pendingReload)
            {
                _pendingReload = false;
                await EnsureCacheAsync();
            }
        }

        // ---- Drawing helpers ----

        private void DrawArea(CanvasDrawingSession ds, ICanvasResourceCreator rc,
            CachedWay way, Color fillColor)
        {
            if (way.Points.Count < 3) return;

            var first = way.Points[0];
            var last = way.Points[way.Points.Count - 1];
            bool isClosed = Math.Abs(first.lat - last.lat) < 0.0000001 &&
                           Math.Abs(first.lon - last.lon) < 0.0000001;

            if (isClosed)
            {
                using (var pb = new CanvasPathBuilder(rc))
                {
                    var (x0, y0) = _viewport.GeoToScreen(way.Points[0].lat, way.Points[0].lon);
                    pb.BeginFigure(x0, y0);
                    for (int i = 1; i < way.Points.Count; i++)
                    {
                        var (x, y) = _viewport.GeoToScreen(way.Points[i].lat, way.Points[i].lon);
                        pb.AddLine(x, y);
                    }
                    pb.EndFigure(CanvasFigureLoop.Closed);

                    using (var geo = CanvasGeometry.CreatePath(pb))
                    {
                        ds.FillGeometry(geo, fillColor);
                    }
                }
            }
            else
            {
                DrawPolyline(ds, rc, way.Points, fillColor, 2);
            }
        }

        private void DrawPolyline(CanvasDrawingSession ds, ICanvasResourceCreator rc,
            List<(double lat, double lon)> points, Color color, float width)
        {
            if (points.Count < 2) return;

            using (var pb = new CanvasPathBuilder(rc))
            {
                var (x0, y0) = _viewport.GeoToScreen(points[0].lat, points[0].lon);
                pb.BeginFigure(x0, y0);
                for (int i = 1; i < points.Count; i++)
                {
                    var (x, y) = _viewport.GeoToScreen(points[i].lat, points[i].lon);
                    pb.AddLine(x, y);
                }
                pb.EndFigure(CanvasFigureLoop.Open);

                using (var geo = CanvasGeometry.CreatePath(pb))
                {
                    ds.DrawGeometry(geo, color, width, _roundStroke);
                }
            }
        }

        public void DrawGpsPosition(CanvasDrawingSession ds, double lat, double lon, double accuracy)
        {
            var (x, y) = _viewport.GeoToScreen(lat, lon);

            if (accuracy > 0 && accuracy < 500)
            {
                double metersPerPixel = _viewport.MetersPerPixel;
                float radiusPixels = (float)(accuracy / metersPerPixel);
                if (radiusPixels > 3 && radiusPixels < 500)
                {
                    ds.FillCircle(x, y, radiusPixels, _theme.GpsAccuracyFill);
                    ds.DrawCircle(x, y, radiusPixels, _theme.GpsAccuracyStroke, 1);
                }
            }

            // Halo + dot
            ds.FillCircle(x, y, 8, _theme.GpsDotHalo);
            ds.FillCircle(x, y, 6, _theme.GpsDotFill);
        }

        // ---- LOD filtering ----

        // ---- Style: road widths (kept here since they're zoom-dependent) ----
        private float GetRoadWidth(string subType, double zoom)
        {
            float baseWidth;
            switch (subType)
            {
                case "motorway":
                case "motorway_link":
                    baseWidth = 4.0f; break;
                case "trunk":
                case "trunk_link":
                    baseWidth = 3.5f; break;
                case "primary":
                case "primary_link":
                    baseWidth = 3.0f; break;
                case "secondary":
                case "secondary_link":
                    baseWidth = 2.5f; break;
                case "tertiary":
                case "tertiary_link":
                    baseWidth = 2.0f; break;
                case "residential":
                case "living_street":
                case "unclassified":
                    baseWidth = 1.5f; break;
                case "service":
                    baseWidth = 1.0f; break;
                case "footway":
                case "path":
                case "cycleway":
                case "track":
                    baseWidth = 0.8f; break;
                case "pedestrian":
                    baseWidth = 1.5f; break;
                default:
                    baseWidth = 1.0f; break;
            }

            if (zoom >= 16) return baseWidth * 2.5f;
            if (zoom >= 14) return baseWidth * 1.8f;
            if (zoom >= 12) return baseWidth * 1.2f;
            if (zoom >= 10) return baseWidth * 0.8f;
            return Math.Max(baseWidth * 0.5f, 1.0f);
        }

        private class CachedWay
        {
            public int Type;
            public string SubType;
            public List<(double lat, double lon)> Points;
        }
    }
}
