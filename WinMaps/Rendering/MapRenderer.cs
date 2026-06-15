using System;
using System.Collections.Generic;
using System.Numerics;
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

        // Cached way geometries for the current viewport
        private List<CachedWay> _cachedWays;
        private double _cacheMinLat, _cacheMaxLat, _cacheMinLon, _cacheMaxLon;
        private double _cacheZoom;
        private const double CacheMarginFactor = 0.3; // 30% margin around viewport

        // Way limit to prevent overwhelming the GPU on low-end devices
        private const int MaxWaysPerFrame = 8000;

        public MapRenderer(MapDatabase db, MapViewport viewport)
        {
            _db = db;
            _viewport = viewport;
        }

        public void InvalidateCache()
        {
            _cachedWays = null;
        }

        public void Draw(CanvasDrawingSession ds, float width, float height)
        {
            _viewport.ScreenWidth = width;
            _viewport.ScreenHeight = height;

            // Background color (light gray like paper map)
            ds.Clear(Color.FromArgb(255, 242, 239, 233));

            if (_db == null || !_db.HasData())
                return;

            EnsureCache();

            if (_cachedWays == null || _cachedWays.Count == 0)
                return;

            // Draw in layer order: parks -> water -> roads
            DrawLayer(ds, _cachedWays, (int)Pbf.OsmElementType.Park);
            DrawLayer(ds, _cachedWays, (int)Pbf.OsmElementType.Water);
            DrawLayer(ds, _cachedWays, (int)Pbf.OsmElementType.Road);
        }

        private void EnsureCache()
        {
            var bounds = _viewport.GetBounds();

            // Check if current cache covers the viewport and zoom hasn't changed much
            if (_cachedWays != null &&
                Math.Abs(_cacheZoom - _viewport.Zoom) < 0.01 &&
                bounds.minLat >= _cacheMinLat && bounds.maxLat <= _cacheMaxLat &&
                bounds.minLon >= _cacheMinLon && bounds.maxLon <= _cacheMaxLon)
            {
                return;
            }

            // Build new cache with margin
            double latSpan = bounds.maxLat - bounds.minLat;
            double lonSpan = bounds.maxLon - bounds.minLon;
            double latMargin = latSpan * CacheMarginFactor;
            double lonMargin = lonSpan * CacheMarginFactor;

            _cacheMinLat = bounds.minLat - latMargin;
            _cacheMaxLat = bounds.maxLat + latMargin;
            _cacheMinLon = bounds.minLon - lonMargin;
            _cacheMaxLon = bounds.maxLon + lonMargin;
            _cacheZoom = _viewport.Zoom;

            // Apply level-of-detail filtering
            int typeFilter = -1; // all types
            var ways = _db.QueryWaysInBounds(_cacheMinLat, _cacheMaxLat, _cacheMinLon, _cacheMaxLon, typeFilter);

            _cachedWays = new List<CachedWay>();
            int count = 0;

            foreach (var (id, type, subType) in ways)
            {
                // LOD: skip minor features at low zoom
                if (!ShouldDrawAtZoom(type, subType, _viewport.Zoom))
                    continue;

                if (count >= MaxWaysPerFrame)
                    break;

                var geometry = _db.GetWayGeometry(id);
                if (geometry.Count < 2)
                    continue;

                _cachedWays.Add(new CachedWay
                {
                    Type = type,
                    SubType = subType,
                    Points = geometry
                });
                count++;
            }
        }

        private bool ShouldDrawAtZoom(int type, string subType, double zoom)
        {
            if (type == (int)Pbf.OsmElementType.Road)
            {
                // At very low zoom, only show major roads
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
            else if (type == (int)Pbf.OsmElementType.Park || type == (int)Pbf.OsmElementType.Water)
            {
                // Always show water and parks (they're area features and usually few)
                return true;
            }

            return true;
        }

        private void DrawLayer(CanvasDrawingSession ds, List<CachedWay> ways, int type)
        {
            foreach (var way in ways)
            {
                if (way.Type != type)
                    continue;

                if (type == (int)Pbf.OsmElementType.Park)
                    DrawArea(ds, way, GetParkColor(way.SubType));
                else if (type == (int)Pbf.OsmElementType.Water)
                    DrawArea(ds, way, GetWaterColor(way.SubType));
                else
                    DrawRoad(ds, way);
            }
        }

        private void DrawArea(CanvasDrawingSession ds, CachedWay way, Color fillColor)
        {
            if (way.Points.Count < 3)
            {
                // Draw as line if not enough points for a polygon
                DrawPolyline(ds, way.Points, fillColor, 2);
                return;
            }

            // Check if it's a closed way (polygon)
            var first = way.Points[0];
            var last = way.Points[way.Points.Count - 1];
            bool isClosed = Math.Abs(first.lat - last.lat) < 0.0000001 &&
                           Math.Abs(first.lon - last.lon) < 0.0000001;

            if (isClosed)
            {
                // Draw filled polygon
                using (var pathBuilder = new CanvasPathBuilder(ds))
                {
                    var start = _viewport.GeoToScreen(way.Points[0].lat, way.Points[0].lon);
                    pathBuilder.BeginFigure(start.x, start.y);

                    for (int i = 1; i < way.Points.Count; i++)
                    {
                        var pt = _viewport.GeoToScreen(way.Points[i].lat, way.Points[i].lon);
                        pathBuilder.AddLine(pt.x, pt.y);
                    }

                    pathBuilder.EndFigure(CanvasFigureLoop.Closed);

                    using (var geom = CanvasGeometry.CreatePath(pathBuilder))
                    {
                        ds.FillGeometry(geom, fillColor);
                    }
                }
            }
            else
            {
                // Open way — draw as a line (e.g., waterway=river)
                DrawPolyline(ds, way.Points, fillColor, 2);
            }
        }

        private void DrawRoad(CanvasDrawingSession ds, CachedWay way)
        {
            Color color = GetRoadColor(way.SubType);
            float width = GetRoadWidth(way.SubType, _viewport.Zoom);
            Color outlineColor = GetRoadOutlineColor(way.SubType);
            float outlineWidth = width + 2;

            // Draw outline first (creates a bordered road effect)
            if (_viewport.Zoom >= 13 && width >= 2)
            {
                DrawPolyline(ds, way.Points, outlineColor, outlineWidth);
            }

            // Draw road fill
            DrawPolyline(ds, way.Points, color, width);
        }

        private void DrawPolyline(CanvasDrawingSession ds, List<(double lat, double lon)> points, Color color, float width)
        {
            for (int i = 0; i < points.Count - 1; i++)
            {
                var p1 = _viewport.GeoToScreen(points[i].lat, points[i].lon);
                var p2 = _viewport.GeoToScreen(points[i + 1].lat, points[i + 1].lon);

                // Skip segments entirely outside the viewport (simple clip)
                if ((p1.x < -100 && p2.x < -100) || (p1.y < -100 && p2.y < -100) ||
                    (p1.x > _viewport.ScreenWidth + 100 && p2.x > _viewport.ScreenWidth + 100) ||
                    (p1.y > _viewport.ScreenHeight + 100 && p2.y > _viewport.ScreenHeight + 100))
                    continue;

                ds.DrawLine(p1.x, p1.y, p2.x, p2.y, color, width);
            }
        }

        private Color GetRoadColor(string subType)
        {
            switch (subType)
            {
                case "motorway":
                case "motorway_link":
                    return Color.FromArgb(255, 233, 144, 160); // pinkish-red
                case "trunk":
                case "trunk_link":
                    return Color.FromArgb(255, 249, 178, 156); // orange
                case "primary":
                case "primary_link":
                    return Color.FromArgb(255, 252, 214, 164); // yellow-orange
                case "secondary":
                case "secondary_link":
                    return Color.FromArgb(255, 246, 250, 187); // light yellow
                case "tertiary":
                case "tertiary_link":
                    return Color.FromArgb(255, 255, 255, 255); // white
                case "residential":
                case "living_street":
                case "unclassified":
                    return Color.FromArgb(255, 255, 255, 255); // white
                case "service":
                    return Color.FromArgb(255, 255, 255, 255);
                case "pedestrian":
                    return Color.FromArgb(255, 221, 221, 238);
                case "footway":
                case "path":
                    return Color.FromArgb(255, 250, 128, 114); // salmon dashed
                case "cycleway":
                    return Color.FromArgb(255, 0, 68, 204); // blue
                case "track":
                    return Color.FromArgb(255, 177, 140, 75); // brown
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

        private float GetRoadWidth(string subType, double zoom)
        {
            float baseWidth;
            switch (subType)
            {
                case "motorway":
                case "motorway_link":
                    baseWidth = 4.0f;
                    break;
                case "trunk":
                case "trunk_link":
                    baseWidth = 3.5f;
                    break;
                case "primary":
                case "primary_link":
                    baseWidth = 3.0f;
                    break;
                case "secondary":
                case "secondary_link":
                    baseWidth = 2.5f;
                    break;
                case "tertiary":
                case "tertiary_link":
                    baseWidth = 2.0f;
                    break;
                case "residential":
                case "living_street":
                case "unclassified":
                    baseWidth = 1.5f;
                    break;
                case "service":
                    baseWidth = 1.0f;
                    break;
                case "footway":
                case "path":
                case "cycleway":
                case "track":
                    baseWidth = 0.8f;
                    break;
                case "pedestrian":
                    baseWidth = 1.5f;
                    break;
                default:
                    baseWidth = 1.0f;
                    break;
            }

            // Scale width with zoom level
            if (zoom >= 16) return baseWidth * 2.5f;
            if (zoom >= 14) return baseWidth * 1.8f;
            if (zoom >= 12) return baseWidth * 1.2f;
            if (zoom >= 10) return baseWidth * 0.8f;
            return Math.Max(baseWidth * 0.5f, 1.0f);
        }

        private Color GetWaterColor(string subType)
        {
            return Color.FromArgb(255, 170, 211, 223); // light blue
        }

        private Color GetParkColor(string subType)
        {
            switch (subType)
            {
                case "forest":
                case "wood":
                    return Color.FromArgb(255, 173, 209, 158); // darker green
                case "grass":
                case "meadow":
                case "farmland":
                    return Color.FromArgb(255, 205, 235, 176); // light green
                case "park":
                case "garden":
                    return Color.FromArgb(255, 200, 250, 204); // bright green
                default:
                    return Color.FromArgb(255, 195, 225, 178); // medium green
            }
        }

        /// <summary>
        /// Draws the GPS position indicator.
        /// </summary>
        public void DrawGpsPosition(CanvasDrawingSession ds, double lat, double lon, double accuracy)
        {
            var (x, y) = _viewport.GeoToScreen(lat, lon);

            // Draw accuracy circle
            if (accuracy > 0 && accuracy < 500)
            {
                double metersPerPixel = _viewport.MetersPerPixel;
                float radiusPixels = (float)(accuracy / metersPerPixel);
                if (radiusPixels > 3 && radiusPixels < 500)
                {
                    ds.FillCircle(x, y, radiusPixels, Color.FromArgb(40, 0, 120, 255));
                    ds.DrawCircle(x, y, radiusPixels, Color.FromArgb(80, 0, 120, 255), 1);
                }
            }

            // Draw position dot
            ds.FillCircle(x, y, 8, Color.FromArgb(255, 255, 255, 255));
            ds.FillCircle(x, y, 6, Color.FromArgb(255, 0, 120, 255));
        }

        private class CachedWay
        {
            public int Type;
            public string SubType;
            public List<(double lat, double lon)> Points;
        }
    }
}
