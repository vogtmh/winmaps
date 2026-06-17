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
        private List<CachedPlace> _cachedPlaces;
        private double _cacheMinLat, _cacheMaxLat, _cacheMinLon, _cacheMaxLon;
        private double _cacheZoom;
        public double CacheZoom => _cacheZoom;
        private const double CacheMarginFactor = 0.5;

        private bool _isLoading;
        private bool _pendingReload;
        private readonly CanvasStrokeStyle _roundStroke;
        private readonly CanvasStrokeStyle _dashedStroke;

        // Subtypes that should render as dashed lines
        private static readonly HashSet<string> DashedRoadSubTypes = new HashSet<string>
        {
            "footway", "path", "cycleway", "track"
        };

        // Per-frame offset to convert cached Mercator coords → screen coords
        private float _frameOffsetX, _frameOffsetY;

        // Shared per-frame label deconfliction grid (building labels + POIs)
        private const int LabelCellSize = 180;
        private bool[] _labelGrid;
        private int _labelGridCols, _labelGridRows;

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
            _dashedStroke = new CanvasStrokeStyle
            {
                LineJoin = CanvasLineJoin.Round,
                StartCap = CanvasCapStyle.Round,
                EndCap = CanvasCapStyle.Round,
                DashStyle = CanvasDashStyle.Dash,
                CustomDashStyle = new float[] { 2f, 4f }
            };
        }

        public void InvalidateCache()
        {
            _cachedWays = null;
            _cachedPois = null;
            _cachedPlaces = null;
        }

        public void Draw(CanvasDrawingSession ds, ICanvasResourceCreator rc)
        {
            if (_cachedWays == null || _cachedWays.Count == 0) return;

            // Compute viewport offset once per frame — converts cached Mercator coords to screen
            _frameOffsetX = (float)(-_viewport.LonToMercatorX(_viewport.CenterLon) + _viewport.ScreenWidth / 2.0);
            _frameOffsetY = (float)(-_viewport.LatToMercatorY(_viewport.CenterLat) + _viewport.ScreenHeight / 2.0);

            // Initialize shared label deconfliction grid for building labels + POIs
            _labelGridCols = (int)(_viewport.ScreenWidth / LabelCellSize) + 1;
            _labelGridRows = (int)(_viewport.ScreenHeight / LabelCellSize) + 1;
            _labelGrid = new bool[_labelGridCols * _labelGridRows];

            // Layer order: parks → water → buildings → road outlines → roads → building labels → POIs

            // Parks: batch by park color
            DrawBatchedAreas(ds, rc, (int)Pbf.OsmElementType.Park,
                w => _theme.GetParkColor(w.SubType));

            // Water bodies (filled areas) — only closed polygons like lakes, ponds
            DrawBatchedAreas(ds, rc, (int)Pbf.OsmElementType.Water,
                w => _theme.WaterColor,
                w => !IsLinearWaterway(w.SubType));

            // Linear waterways (rivers, streams) — drawn as lines
            DrawBatchedLines(ds, rc, (int)Pbf.OsmElementType.Water,
                w => _theme.WaterColor,
                w => GetWaterwayWidth(w.SubType, _viewport.Zoom));

            // Buildings geometry (fills; outlines only at zoom >= 15)
            if (_viewport.Zoom >= 10)
                DrawBuildings(ds, labelsOnly: false);

            // Road outlines (only at zoom >= 13, solid roads only — no outlines on dashed)
            if (_viewport.Zoom >= 13)
            {
                DrawBatchedLines(ds, rc, (int)Pbf.OsmElementType.Road,
                    w => _theme.GetRoadOutlineColor(w.SubType),
                    w => { float wid = GetRoadWidth(w.SubType, _viewport.Zoom); return wid >= 2 ? wid + 2 : -1; },
                    dashedFilter: false);
            }

            // Solid road fills
            DrawBatchedLines(ds, rc, (int)Pbf.OsmElementType.Road,
                w => _theme.GetRoadColor(w.SubType),
                w => GetRoadWidth(w.SubType, _viewport.Zoom),
                dashedFilter: false);

            // Dashed roads (footway, path, cycleway, track) — drawn after solid roads
            DrawBatchedLines(ds, rc, (int)Pbf.OsmElementType.Road,
                w => _theme.GetRoadColor(w.SubType),
                w => GetRoadWidth(w.SubType, _viewport.Zoom),
                dashedFilter: true);

            // Building labels on top of roads (zoom >= 17)
            if (_viewport.Zoom >= 17)
                DrawBuildings(ds, labelsOnly: true);

            // Place labels (cities, towns, villages)
            if (_cachedPlaces != null && _viewport.Zoom >= 6)
            {
                DrawPlaceLabels(ds);
            }

            // POIs on top of everything
            if (_cachedPois != null && _viewport.Zoom >= 17)
            {
                DrawPois(ds);
            }
        }

        private const int MaxWaysPerBatch = 500;

        private void DrawBuildings(CanvasDrawingSession ds, bool labelsOnly)
        {
            bool showLabels = _viewport.Zoom >= 17;
            float fontSize = 9f;

            using (var textFormat = (showLabels && labelsOnly) ? new CanvasTextFormat
            {
                FontSize = fontSize,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            } : null)
            {
                foreach (var way in _cachedWays)
                {
                    if (way.Type != (int)Pbf.OsmElementType.Building) continue;
                    if (way.MercX.Length < 3 || !way.IsClosed) continue;

                    float ox = _frameOffsetX, oy = _frameOffsetY;

                    if (!labelsOnly)
                    {
                        // Build path
                        using (var pb = new CanvasPathBuilder(ds))
                        {
                            pb.BeginFigure(way.MercX[0] + ox, way.MercY[0] + oy);
                            for (int i = 1; i < way.MercX.Length; i++)
                                pb.AddLine(way.MercX[i] + ox, way.MercY[i] + oy);
                            pb.EndFigure(CanvasFigureLoop.Closed);

                            using (var geo = CanvasGeometry.CreatePath(pb))
                            {
                                ds.FillGeometry(geo, _theme.BuildingFill);
                                if (_viewport.Zoom >= 15)
                                    ds.DrawGeometry(geo, _theme.BuildingStroke, 0.8f);
                            }
                        }
                    }
                    else if (showLabels && textFormat != null)
                    {
                        // Label named buildings — rendered above roads
                        var (name, _) = MapTheme.DecodeBuildingSubType(way.SubType);
                        if (string.IsNullOrEmpty(name)) continue;

                        // Centroid approximation: average of points
                        float cx = 0, cy = 0;
                        for (int i = 0; i < way.MercX.Length; i++) { cx += way.MercX[i]; cy += way.MercY[i]; }
                        cx = cx / way.MercX.Length + ox;
                        cy = cy / way.MercY.Length + oy;

                        // Skip if off screen
                        if (cx < 0 || cx > _viewport.ScreenWidth || cy < 0 || cy > _viewport.ScreenHeight) continue;

                        // Shared label deconfliction with POIs
                        int lCol = (int)(cx / LabelCellSize);
                        int lRow = (int)(cy / LabelCellSize);
                        if (lCol >= 0 && lCol < _labelGridCols && lRow >= 0 && lRow < _labelGridRows)
                        {
                            int lIdx = lRow * _labelGridCols + lCol;
                            if (_labelGrid[lIdx]) continue;
                            _labelGrid[lIdx] = true;
                        }

                        ds.DrawText(name, cx, cy, _theme.BuildingLabelColor, textFormat);
                    }
                }
            }
        }

        private void DrawBatchedAreas(CanvasDrawingSession ds, ICanvasResourceCreator rc,
            int typeFilter, Func<CachedWay, Color> colorFunc, Func<CachedWay, bool> filter = null)
        {
            // Group by color, build one combined geometry per color
            var batches = new Dictionary<uint, List<CachedWay>>();

            foreach (var way in _cachedWays)
            {
                if (way.Type != typeFilter || way.MercX.Length < 3) continue;
                if (filter != null && !filter(way)) continue;
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
                var allWays = kvp.Value;

                for (int chunk = 0; chunk < allWays.Count; chunk += MaxWaysPerBatch)
                {
                    int end = Math.Min(chunk + MaxWaysPerBatch, allWays.Count);
                    using (var pb = new CanvasPathBuilder(rc))
                    {
                        for (int w = chunk; w < end; w++)
                        {
                            var way = allWays[w];
                            pb.BeginFigure(way.MercX[0] + _frameOffsetX, way.MercY[0] + _frameOffsetY);
                            for (int i = 1; i < way.MercX.Length; i++)
                                pb.AddLine(way.MercX[i] + _frameOffsetX, way.MercY[i] + _frameOffsetY);
                            pb.EndFigure(way.IsClosed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
                        }
                        using (var geo = CanvasGeometry.CreatePath(pb))
                        {
                            ds.FillGeometry(geo, color);
                        }
                    }
                }
            }
        }

        private void DrawBatchedLines(CanvasDrawingSession ds, ICanvasResourceCreator rc,
            int typeFilter, Func<CachedWay, Color> colorFunc, Func<CachedWay, float> widthFunc,
            bool? dashedFilter = null)
        {
            // Group by (color, width), build one combined geometry per group
            var batches = new Dictionary<ulong, (Color color, float width, List<CachedWay> ways)>();

            foreach (var way in _cachedWays)
            {
                if (way.Type != typeFilter || way.MercX.Length < 2) continue;

                // Filter by dashed/solid if requested
                if (dashedFilter.HasValue)
                {
                    bool isDashed = DashedRoadSubTypes.Contains(way.SubType);
                    if (isDashed != dashedFilter.Value) continue;
                }

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

                for (int chunk = 0; chunk < ways.Count; chunk += MaxWaysPerBatch)
                {
                    int end = Math.Min(chunk + MaxWaysPerBatch, ways.Count);
                    using (var pb = new CanvasPathBuilder(rc))
                    {
                        for (int w = chunk; w < end; w++)
                        {
                            var way = ways[w];
                            pb.BeginFigure(way.MercX[0] + _frameOffsetX, way.MercY[0] + _frameOffsetY);
                            for (int i = 1; i < way.MercX.Length; i++)
                                pb.AddLine(way.MercX[i] + _frameOffsetX, way.MercY[i] + _frameOffsetY);
                            pb.EndFigure(CanvasFigureLoop.Open);
                        }
                        using (var geo = CanvasGeometry.CreatePath(pb))
                        {
                            var style = (dashedFilter == true) ? _dashedStroke : _roundStroke;
                            ds.DrawGeometry(geo, color, width, style);
                        }
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
                if (queryZoom < 6) minDistSq = 64.0f;        // 8px
                else if (queryZoom < 8) minDistSq = 36.0f;   // 6px
                else if (queryZoom < 10) minDistSq = 16.0f;  // 4px
                else if (queryZoom < 12) minDistSq = 4.0f;   // 2px
                else if (queryZoom < 14) minDistSq = 1.0f;   // 1px
                else minDistSq = 0.25f;                      // 0.5px

                var result = new List<CachedWay>(ways.Count);
                foreach (var (type, subType, points) in ways)
                {
                    if (points.Count < 2) continue;

                    // Pre-compute Mercator coordinates + simplify
                    var sx = new List<float>(points.Count);
                    var sy = new List<float>(points.Count);

                    float firstX = (float)_viewport.LonToMercatorX(points[0].lon);
                    float firstY = (float)_viewport.LatToMercatorY(points[0].lat);
                    sx.Add(firstX);
                    sy.Add(firstY);
                    float lastKeptX = firstX, lastKeptY = firstY;

                    for (int i = 1; i < points.Count; i++)
                    {
                        float px = (float)_viewport.LonToMercatorX(points[i].lon);
                        float py = (float)_viewport.LatToMercatorY(points[i].lat);

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
                        MercX = sx.ToArray(),
                        MercY = sy.ToArray(),
                        IsClosed = isClosed
                    });
                }

                return result;
            });

            List<CachedPoi> newCachedPois = null;
            if (queryZoom >= 17)
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

            List<CachedPlace> newCachedPlaces = null;
            if (queryZoom >= 6)
            {
                newCachedPlaces = await Task.Run(() =>
                {
                    var places = _db.QueryPlaces(queryMinLat, queryMaxLat, queryMinLon, queryMaxLon, queryZoom);
                    var result = new List<CachedPlace>(places.Count);
                    foreach (var (placeType, name, lat, lon) in places)
                    {
                        result.Add(new CachedPlace
                        {
                            PlaceType = placeType,
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
            _cachedPlaces = newCachedPlaces;
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
                    if (zoom < 14) return -1;
                    baseWidth = 0.8f; break;
                case "track":
                    if (zoom < 13) return -1;
                    baseWidth = 0.6f; break;
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

        private static bool IsLinearWaterway(string subType)
        {
            switch (subType)
            {
                case "river": case "stream": case "canal":
                case "ditch": case "drain": case "brook":
                    return true;
                default:
                    return false;
            }
        }

        private static float GetWaterwayWidth(string subType, double zoom)
        {
            float baseWidth;
            switch (subType)
            {
                case "river": baseWidth = 3.0f; break;
                case "canal": baseWidth = 2.5f; break;
                case "stream": case "brook":
                    if (zoom < 14) return -1;
                    baseWidth = 1.5f; break;
                case "ditch": case "drain":
                    if (zoom < 14) return -1;
                    baseWidth = 0.8f; break;
                default: baseWidth = 1.0f; break;
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
            public float[] MercX;
            public float[] MercY;
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

        private class CachedPlace
        {
            public string PlaceType;
            public string Name;
            public double Lat;
            public double Lon;
        }

        // ---- Place label rendering ----

        private static int PlaceTypeOrder(string placeType)
        {
            switch (placeType)
            {
                case "city": return 0;
                case "town": return 1;
                case "village": return 2;
                case "suburb": return 3;
                case "hamlet": return 4;
                default: return 5;
            }
        }

        private void DrawPlaceLabels(CanvasDrawingSession ds)
        {
            if (_cachedPlaces == null || _cachedPlaces.Count == 0) return;

            double zoom = _viewport.Zoom;
            bool useLight = zoom >= 14;
            Color textColor = useLight ? _theme.PlaceLabelColorLight : _theme.PlaceLabelColor;
            Color haloColor = _theme.PlaceLabelHaloColor;

            // Sort by importance so cities win deconfliction over towns, etc.
            _cachedPlaces.Sort((a, b) => PlaceTypeOrder(a.PlaceType).CompareTo(PlaceTypeOrder(b.PlaceType)));

            // Grid-based label deconfliction
            int cellSize = 140;
            int cols = (int)(_viewport.ScreenWidth / cellSize) + 1;
            int rows = (int)(_viewport.ScreenHeight / cellSize) + 1;
            var occupied = new bool[cols * rows];

            foreach (var place in _cachedPlaces)
            {
                float fontSize;
                switch (place.PlaceType)
                {
                    case "city": fontSize = 16f; break;
                    case "town": fontSize = 14f; break;
                    case "village": fontSize = 12f; break;
                    case "suburb": fontSize = 11f; break;
                    default: fontSize = 10f; break;
                }

                var (x, y) = _viewport.GeoToScreen(place.Lat, place.Lon);

                if (x < -60 || x > _viewport.ScreenWidth + 60 ||
                    y < -30 || y > _viewport.ScreenHeight + 30)
                    continue;

                // Check deconfliction grid
                int col = (int)(x / cellSize);
                int row = (int)(y / cellSize);
                if (col < 0 || col >= cols || row < 0 || row >= rows) continue;
                int cellIdx = row * cols + col;
                if (occupied[cellIdx]) continue;
                occupied[cellIdx] = true;

                using (var textFormat = new CanvasTextFormat
                {
                    FontSize = fontSize,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                    FontWeight = (place.PlaceType == "city") ?
                        Windows.UI.Text.FontWeights.SemiBold :
                        Windows.UI.Text.FontWeights.Normal
                })
                {
                    // Text halo: draw offset copies for readability
                    ds.DrawText(place.Name, x - 1, y, haloColor, textFormat);
                    ds.DrawText(place.Name, x + 1, y, haloColor, textFormat);
                    ds.DrawText(place.Name, x, y - 1, haloColor, textFormat);
                    ds.DrawText(place.Name, x, y + 1, haloColor, textFormat);
                    ds.DrawText(place.Name, x, y, textColor, textFormat);
                }
            }
        }

        // ---- POI rendering ----

        // POI subtypes that carry no useful information without a real name
        private static readonly System.Collections.Generic.HashSet<string> _genericPoiSubTypes =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "information", "post_box", "recycling", "bench", "telephone",
                "vending_machine", "waste_basket", "bicycle_parking", "waste_disposal",
                "letter_box", "drinking_water", "fire_hydrant", "bollard",
                "surveillance", "street_lamp", "clock", "manhole",
                "compressed_air", "charging_station", "atm",
                "parking_space", "parking_entrance", "motorcycle_parking",
                "toilets", "shelter", "hunting_stand",
                "give_box", "photo_booth", "bbq",
                "artwork", "vacant"
            };

        // POI subtypes that are always skipped (noise even with a name)
        private static readonly System.Collections.Generic.HashSet<string> _alwaysSkipPoiSubTypes =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "parking_entrance", "parking_space", "compressed_air",
                "motorcycle_parking", "vacant"
            };

        private void DrawPois(CanvasDrawingSession ds)
        {
            if (_cachedPois == null || _cachedPois.Count == 0) return;

            float fontSize = _viewport.Zoom >= 17 ? 12f : 10f;
            float dotRadius = _viewport.Zoom >= 17 ? 4f : 3f;

            // Use the shared label deconfliction grid (already used by building labels)
            // This prevents POIs and building labels from overlapping each other

            using (var textFormat = new CanvasTextFormat
            {
                FontSize = fontSize,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Bottom
            })
            {
                foreach (var poi in _cachedPois)
                {
                    // Skip POIs that are always noise
                    if (_alwaysSkipPoiSubTypes.Contains(poi.SubType))
                        continue;

                    // Skip POIs with no real name if their subtype is generic/uninformative
                    bool hasName = !string.IsNullOrEmpty(poi.Name);
                    if (!hasName && _genericPoiSubTypes.Contains(poi.SubType))
                        continue;

                    var (x, y) = _viewport.GeoToScreen(poi.Lat, poi.Lon);

                    // Skip if off screen
                    if (x < -20 || x > _viewport.ScreenWidth + 20 ||
                        y < -20 || y > _viewport.ScreenHeight + 20)
                        continue;

                    // Draw dot (color by POI type)
                    Color dotColor = _theme.GetPoiColor(poi.Type);
                    ds.FillCircle(x, y, dotRadius + 1, _theme.PoiHaloColor);
                    ds.FillCircle(x, y, dotRadius, dotColor);

                    // Draw label if the cell isn't occupied
                    string label = poi.Name;
                    if (string.IsNullOrEmpty(label))
                        label = poi.SubType;

                    if (!string.IsNullOrEmpty(label))
                    {
                        int col = (int)(x / LabelCellSize);
                        int row = (int)(y / LabelCellSize);
                        if (col >= 0 && col < _labelGridCols && row >= 0 && row < _labelGridRows)
                        {
                            int cellIdx = row * _labelGridCols + col;
                            if (!_labelGrid[cellIdx])
                            {
                                _labelGrid[cellIdx] = true;
                                float textX = x;
                                float textY = y - dotRadius - 2;

                                // Text with halo effect: draw dark outline then light text
                                ds.DrawText(label, textX - 1, textY, _theme.PoiHaloColor, textFormat);
                                ds.DrawText(label, textX + 1, textY, _theme.PoiHaloColor, textFormat);
                                ds.DrawText(label, textX, textY - 1, _theme.PoiHaloColor, textFormat);
                                ds.DrawText(label, textX, textY + 1, _theme.PoiHaloColor, textFormat);
                                ds.DrawText(label, textX, textY, _theme.PoiTextColor, textFormat);
                            }
                        }
                    }
                }
            }
        }
    }
}
