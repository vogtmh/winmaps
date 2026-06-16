using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
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
        private List<CachedPoi> _cachedPois;
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
            _cachedPois = null;
        }

        public void Draw(CanvasDrawingSession ds, ICanvasResourceCreator rc)
        {
            if (_cachedWays == null || _cachedWays.Count == 0) return;

            // Layer order: parks → water → road outlines → roads
            // Batch all ways of same color into a single CanvasGeometry for massive draw-call reduction

            // Parks: batch by park color
            DrawBatchedAreas(ds, rc, (int)Pbf.OsmElementType.Park,
                w => _theme.GetParkColor(w.SubType));

            // Water: single color
            DrawBatchedAreas(ds, rc, (int)Pbf.OsmElementType.Water,
                w => _theme.WaterColor);

            // Road outlines (only at zoom >= 13)
            if (_viewport.Zoom >= 13)
            {
                DrawBatchedLines(ds, rc, (int)Pbf.OsmElementType.Road,
                    w => _theme.GetRoadOutlineColor(w.SubType),
                    w => { float wid = GetRoadWidth(w.SubType, _viewport.Zoom); return wid >= 2 ? wid + 2 : -1; });
            }

            // Road fills
            DrawBatchedLines(ds, rc, (int)Pbf.OsmElementType.Road,
                w => _theme.GetRoadColor(w.SubType),
                w => GetRoadWidth(w.SubType, _viewport.Zoom));

            // POIs (drawn on top of everything)
            if (_cachedPois != null && _viewport.Zoom >= 15)
            {
                DrawPois(ds);
            }
        }

        private void DrawBatchedAreas(CanvasDrawingSession ds, ICanvasResourceCreator rc,
            int typeFilter, Func<CachedWay, Color> colorFunc)
        {
            // Group by color, build one combined geometry per color
            var batches = new Dictionary<uint, List<CachedWay>>();

            foreach (var way in _cachedWays)
            {
                if (way.Type != typeFilter || way.ScreenX.Length < 3) continue;
                uint colorKey = ColorToUint(colorFunc(way));
                if (!batches.TryGetValue(colorKey, out var list))
                {
                    list = new List<CachedWay>();
                    batches[colorKey] = list;
                }
                list.Add(way);
            }

            foreach (var kvp in batches)
            {
                Color color = UintToColor(kvp.Key);
                using (var pb = new CanvasPathBuilder(rc))
                {
                    foreach (var way in kvp.Value)
                    {
                        pb.BeginFigure(way.ScreenX[0], way.ScreenY[0]);
                        for (int i = 1; i < way.ScreenX.Length; i++)
                            pb.AddLine(way.ScreenX[i], way.ScreenY[i]);
                        pb.EndFigure(way.IsClosed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
                    }
                    using (var geo = CanvasGeometry.CreatePath(pb))
                    {
                        ds.FillGeometry(geo, color);
                    }
                }
            }
        }

        private void DrawBatchedLines(CanvasDrawingSession ds, ICanvasResourceCreator rc,
            int typeFilter, Func<CachedWay, Color> colorFunc, Func<CachedWay, float> widthFunc)
        {
            // Group by (color, width), build one combined geometry per group
            var batches = new Dictionary<ulong, (Color color, float width, List<CachedWay> ways)>();

            foreach (var way in _cachedWays)
            {
                if (way.Type != typeFilter || way.ScreenX.Length < 2) continue;
                float w = widthFunc(way);
                if (w < 0) continue; // skip (used for outline filter)
                Color c = colorFunc(way);

                // Combine color + quantized width into a single key
                uint ck = ColorToUint(c);
                uint wk = (uint)(w * 10); // 0.1px granularity
                ulong key = ((ulong)ck << 32) | wk;

                if (!batches.TryGetValue(key, out var batch))
                {
                    batch = (c, w, new List<CachedWay>());
                    batches[key] = batch;
                }
                batch.ways.Add(way);
            }

            foreach (var kvp in batches)
            {
                var (color, width, ways) = kvp.Value;
                using (var pb = new CanvasPathBuilder(rc))
                {
                    foreach (var way in ways)
                    {
                        pb.BeginFigure(way.ScreenX[0], way.ScreenY[0]);
                        for (int i = 1; i < way.ScreenX.Length; i++)
                            pb.AddLine(way.ScreenX[i], way.ScreenY[i]);
                        pb.EndFigure(CanvasFigureLoop.Open);
                    }
                    using (var geo = CanvasGeometry.CreatePath(pb))
                    {
                        ds.DrawGeometry(geo, color, width, _roundStroke);
                    }
                }
            }
        }

        private static uint ColorToUint(Color c)
        {
            return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        }

        private static Color UintToColor(uint v)
        {
            return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
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

                // Minimum squared pixel distance to keep a point (simplification threshold)
                // More aggressive at lower zoom where detail isn't visible
                float minDistSq;
                if (queryZoom < 10) minDistSq = 4.0f;       // 2px
                else if (queryZoom < 13) minDistSq = 1.0f;  // 1px
                else minDistSq = 0.25f;                      // 0.5px

                var result = new List<CachedWay>(ways.Count);
                foreach (var (type, subType, points) in ways)
                {
                    if (points.Count < 2) continue;

                    // Pre-compute screen coordinates + simplify
                    var sx = new List<float>(points.Count);
                    var sy = new List<float>(points.Count);

                    var (firstX, firstY) = _viewport.GeoToScreen(points[0].lat, points[0].lon);
                    sx.Add(firstX);
                    sy.Add(firstY);
                    float lastKeptX = firstX, lastKeptY = firstY;

                    for (int i = 1; i < points.Count; i++)
                    {
                        var (px, py) = _viewport.GeoToScreen(points[i].lat, points[i].lon);

                        // Always keep the last point; skip intermediate points too close
                        if (i < points.Count - 1)
                        {
                            float dx = px - lastKeptX;
                            float dy = py - lastKeptY;
                            if (dx * dx + dy * dy < minDistSq)
                                continue;
                        }

                        sx.Add(px);
                        sy.Add(py);
                        lastKeptX = px;
                        lastKeptY = py;
                    }

                    if (sx.Count < 2) continue;

                    // Determine if area is closed
                    bool isClosed = false;
                    if (type != (int)Pbf.OsmElementType.Road && sx.Count >= 3)
                    {
                        float cdx = sx[0] - sx[sx.Count - 1];
                        float cdy = sy[0] - sy[sy.Count - 1];
                        isClosed = (cdx * cdx + cdy * cdy) < 4.0f; // within 2px
                    }

                    result.Add(new CachedWay
                    {
                        Type = type,
                        SubType = subType,
                        ScreenX = sx.ToArray(),
                        ScreenY = sy.ToArray(),
                        IsClosed = isClosed
                    });
                }

                return result;
            });

            List<CachedPoi> newCachedPois = null;
            if (queryZoom >= 15)
            {
                newCachedPois = await Task.Run(() =>
                {
                    var pois = _db.QueryPois(queryMinLat, queryMaxLat, queryMinLon, queryMaxLon);
                    var result = new List<CachedPoi>(pois.Count);
                    foreach (var (type, subType, name, lat, lon) in pois)
                    {
                        result.Add(new CachedPoi
                        {
                            Type = type,
                            SubType = subType,
                            Name = name,
                            Lat = lat,
                            Lon = lon
                        });
                    }
                    return result;
                });
            }

            _cachedWays = newCachedWays;
            _cachedPois = newCachedPois;
            _isLoading = false;

            // If a reload was requested while we were loading, re-run
            if (_pendingReload)
            {
                _pendingReload = false;
                await EnsureCacheAsync();
            }
        }

        // ---- Drawing helpers ----

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
            public float[] ScreenX;
            public float[] ScreenY;
            public bool IsClosed;
        }

        private class CachedPoi
        {
            public string Type;
            public string SubType;
            public string Name;
            public double Lat;
            public double Lon;
        }

        // ---- POI rendering ----

        private void DrawPois(CanvasDrawingSession ds)
        {
            if (_cachedPois == null || _cachedPois.Count == 0) return;

            float fontSize = _viewport.Zoom >= 17 ? 12f : 10f;
            float dotRadius = _viewport.Zoom >= 17 ? 4f : 3f;

            // Grid-based label deconfliction: divide screen into cells
            int cellSize = 80; // pixels per cell
            int cols = (int)(_viewport.ScreenWidth / cellSize) + 1;
            int rows = (int)(_viewport.ScreenHeight / cellSize) + 1;
            var occupied = new bool[cols * rows];

            using (var textFormat = new CanvasTextFormat
            {
                FontSize = fontSize,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
                VerticalAlignment = CanvasVerticalAlignment.Center
            })
            {
                foreach (var poi in _cachedPois)
                {
                    var (x, y) = _viewport.GeoToScreen(poi.Lat, poi.Lon);

                    // Skip if off screen
                    if (x < -20 || x > _viewport.ScreenWidth + 20 ||
                        y < -20 || y > _viewport.ScreenHeight + 20)
                        continue;

                    // Draw dot (color by POI type)
                    Color dotColor = _theme.GetPoiColor(poi.Type);
                    ds.FillCircle(x, y, dotRadius + 1, _theme.PoiHaloColor);
                    ds.FillCircle(x, y, dotRadius, dotColor);

                    // Draw label if we have a name and the cell isn't occupied
                    if (!string.IsNullOrEmpty(poi.Name))
                    {
                        int col = (int)(x / cellSize);
                        int row = (int)(y / cellSize);
                        if (col >= 0 && col < cols && row >= 0 && row < rows)
                        {
                            int cellIdx = row * cols + col;
                            if (!occupied[cellIdx])
                            {
                                occupied[cellIdx] = true;
                                float textX = x + dotRadius + 3;
                                float textY = y;

                                // Text with halo effect: draw dark outline then light text
                                ds.DrawText(poi.Name, textX - 1, textY, _theme.PoiHaloColor, textFormat);
                                ds.DrawText(poi.Name, textX + 1, textY, _theme.PoiHaloColor, textFormat);
                                ds.DrawText(poi.Name, textX, textY - 1, _theme.PoiHaloColor, textFormat);
                                ds.DrawText(poi.Name, textX, textY + 1, _theme.PoiHaloColor, textFormat);
                                ds.DrawText(poi.Name, textX, textY, _theme.PoiTextColor, textFormat);
                            }
                        }
                    }
                }
            }
        }
    }
}
