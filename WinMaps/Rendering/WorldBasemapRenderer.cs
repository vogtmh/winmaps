using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Data.Json;
using Windows.Storage;
using Windows.UI;
using WinMaps.Download;

namespace WinMaps.Rendering
{
    /// <summary>
    /// Draws a coarse world basemap as a fallback for areas with no downloaded map:
    /// filled landmasses + thin country borders (bundled Natural Earth 110m country polygons)
    /// plus populated-place dots and labels (compact Natural Earth 10m cities/towns asset).
    /// Everything is projected with the live map's Mercator viewport. Land rings are culled by
    /// geographic bounding box and the projected geometry is cached per viewport, and places are
    /// filtered by scalerank-vs-zoom, so panning stays cheap on low-end devices (Lumia 735/950 XL).
    /// </summary>
    internal class WorldBasemapRenderer
    {
        private sealed class Ring
        {
            public double[] Lat;
            public double[] Lon;
            public double MinLat, MaxLat, MinLon, MaxLon;
        }

        private struct Place
        {
            public string Name;
            public double Lat;
            public double Lon;
            public int ScaleRank; // 0 = largest cities, 10 = smallest towns
        }

        private List<Ring> _rings;
        private List<Place> _places;
        private bool _loading;

        public bool IsReady { get; private set; }

        // Cached projected geometry keyed on viewport state
        private CanvasGeometry _cachedGeo;
        private double _cLat, _cLon, _cZoom, _cW, _cH;

        /// <summary>
        /// Lazily loads the coarse Natural Earth data. Safe to call repeatedly; only the first
        /// call does work. Failures (missing asset) leave the renderer not-ready and silent.
        /// </summary>
        public async Task EnsureLoadedAsync()
        {
            if (IsReady || _loading) return;
            _loading = true;
            try
            {
                var ne = new NaturalEarthIndex();
                await ne.LoadAsync("110m");

                var rings = new List<Ring>();
                foreach (var country in ne.GetAllCountries())
                {
                    if (country.Geometry == null) continue;
                    foreach (var ring in country.Geometry)
                    {
                        if (ring.Count < 3) continue;
                        var r = new Ring
                        {
                            Lat = new double[ring.Count],
                            Lon = new double[ring.Count],
                            MinLat = double.MaxValue,
                            MaxLat = double.MinValue,
                            MinLon = double.MaxValue,
                            MaxLon = double.MinValue
                        };
                        for (int i = 0; i < ring.Count; i++)
                        {
                            double lat = ring[i].Lat, lon = ring[i].Lon;
                            r.Lat[i] = lat;
                            r.Lon[i] = lon;
                            if (lat < r.MinLat) r.MinLat = lat;
                            if (lat > r.MaxLat) r.MaxLat = lat;
                            if (lon < r.MinLon) r.MinLon = lon;
                            if (lon > r.MaxLon) r.MaxLon = lon;
                        }
                        rings.Add(r);
                    }
                }
                _rings = rings;
                IsReady = true;
            }
            catch { /* asset missing/unreadable — basemap stays disabled */ }
            finally { _loading = false; }

            // Populated places are optional: a failure here must not disable the land basemap.
            try
            {
                var file = await StorageFile.GetFileFromApplicationUriAsync(
                    new Uri("ms-appx:///Assets/NaturalEarth/populated-places.json"));
                string json = await FileIO.ReadTextAsync(file);
                var arr = JsonArray.Parse(json);

                var places = new List<Place>(arr.Count);
                foreach (var item in arr)
                {
                    var a = item.GetArray();
                    if (a.Count < 4) continue;
                    places.Add(new Place
                    {
                        Name = a.GetStringAt(0),
                        Lon = a.GetNumberAt(1),
                        Lat = a.GetNumberAt(2),
                        ScaleRank = (int)a.GetNumberAt(3)
                    });
                }
                _places = places;
            }
            catch { /* places asset missing — land basemap still renders */ }
        }

        /// <summary>
        /// Fills the target with the theme's ocean color, then draws land polygons + borders.
        /// Does nothing until the data is ready, so the normal background clear shows in the
        /// brief moment before the coarse geometry has loaded (no ocean-blue flash).
        /// </summary>
        public void Draw(CanvasDrawingSession ds, ICanvasResourceCreator rc, MapViewport vp, MapTheme theme)
        {
            if (!IsReady || _rings == null) return;

            ds.Clear(theme.WaterColor);

            EnsureGeometry(rc, vp);
            if (_cachedGeo != null)
            {
                ds.FillGeometry(_cachedGeo, theme.Background);
                ds.DrawGeometry(_cachedGeo, theme.PlaceLabelColorLight, 0.5f);
            }

            DrawPlaces(ds, vp, theme);
        }

        /// <summary>
        /// Draws populated-place dots + labels. Places are filtered by scalerank vs zoom so that
        /// only major cities show when zoomed out, with smaller towns appearing as you zoom in.
        /// At the default close zoom this gives users "something" (the nearest city/town) even
        /// when no detailed map is downloaded for the area.
        /// </summary>
        private void DrawPlaces(CanvasDrawingSession ds, MapViewport vp, MapTheme theme)
        {
            if (_places == null || _places.Count == 0) return;

            int maxSr = MaxScaleRank(vp.Zoom);

            var (minLat, maxLat, minLon, maxLon) = vp.GetBounds();
            bool lonWraps = minLon > maxLon; // viewport crosses the antimeridian

            Color textColor = theme.PlaceLabelColor;
            Color haloColor = theme.PlaceLabelHaloColor;
            Color dotColor = theme.PlaceLabelColorLight;

            // Reuse one text format per size bucket instead of allocating per place.
            var fmt = new CanvasTextFormat[4];
            try
            {
                for (int b = 0; b < 4; b++)
                {
                    fmt[b] = new CanvasTextFormat
                    {
                        FontSize = BucketFont(b),
                        HorizontalAlignment = CanvasHorizontalAlignment.Center,
                        VerticalAlignment = CanvasVerticalAlignment.Center,
                        FontWeight = (b == 0)
                            ? Windows.UI.Text.FontWeights.SemiBold
                            : Windows.UI.Text.FontWeights.Normal
                    };
                }

                foreach (var pl in _places)
                {
                    if (pl.ScaleRank > maxSr) continue;

                    if (!lonWraps)
                    {
                        if (pl.Lat < minLat || pl.Lat > maxLat) continue;
                        if (pl.Lon < minLon || pl.Lon > maxLon) continue;
                    }

                    var (x, y) = vp.GeoToScreen(pl.Lat, pl.Lon);
                    if (x < -80 || x > vp.ScreenWidth + 80 ||
                        y < -40 || y > vp.ScreenHeight + 40)
                        continue;

                    // Place marker dot
                    ds.FillCircle(x, y, 3f, dotColor);

                    var tf = fmt[Bucket(pl.ScaleRank)];
                    float ly = y + 9f; // label sits just below the dot

                    // Text halo via offset copies, matching the detailed-map label style.
                    ds.DrawText(pl.Name, x - 1, ly, haloColor, tf);
                    ds.DrawText(pl.Name, x + 1, ly, haloColor, tf);
                    ds.DrawText(pl.Name, x, ly - 1, haloColor, tf);
                    ds.DrawText(pl.Name, x, ly + 1, haloColor, tf);
                    ds.DrawText(pl.Name, x, ly, textColor, tf);
                }
            }
            finally
            {
                foreach (var f in fmt) f?.Dispose();
            }
        }

        // Highest scalerank (least important place) to show at a given zoom level.
        // scalerank 0 = megacities, 10 = small towns. Below ~zoom 10 everything is shown.
        private static int MaxScaleRank(double zoom)
        {
            int sr = (int)Math.Round((zoom - 3) * 1.4);
            if (sr < 0) sr = 0;
            if (sr > 10) sr = 10;
            return sr;
        }

        private static int Bucket(int scaleRank)
        {
            if (scaleRank <= 1) return 0;
            if (scaleRank <= 3) return 1;
            if (scaleRank <= 6) return 2;
            return 3;
        }

        private static float BucketFont(int bucket)
        {
            switch (bucket)
            {
                case 0: return 15f;
                case 1: return 13f;
                case 2: return 12f;
                default: return 11f;
            }
        }

        private void EnsureGeometry(ICanvasResourceCreator rc, MapViewport vp)
        {
            bool unchanged = _cachedGeo != null &&
                             _cLat == vp.CenterLat && _cLon == vp.CenterLon &&
                             _cZoom == vp.Zoom && _cW == vp.ScreenWidth && _cH == vp.ScreenHeight;
            if (unchanged) return;

            var (minLat, maxLat, minLon, maxLon) = vp.GetBounds();
            // A small margin avoids popping rings in/out right at the screen edge.
            double mLat = (maxLat - minLat) * 0.1;
            double mLon = (maxLon - minLon) * 0.1;
            minLat -= mLat; maxLat += mLat;
            minLon -= mLon; maxLon += mLon;
            bool lonWraps = minLon > maxLon; // viewport crosses the antimeridian

            _cachedGeo?.Dispose();
            _cachedGeo = null;

            CanvasPathBuilder pb = null;
            try
            {
                foreach (var ring in _rings)
                {
                    // Cheap geographic-bbox cull (skipped when the viewport wraps the antimeridian)
                    if (!lonWraps)
                    {
                        if (ring.MaxLat < minLat || ring.MinLat > maxLat) continue;
                        if (ring.MaxLon < minLon || ring.MinLon > maxLon) continue;
                    }

                    if (pb == null) pb = new CanvasPathBuilder(rc);

                    var p0 = vp.GeoToScreen(ring.Lat[0], ring.Lon[0]);
                    pb.BeginFigure(p0.x, p0.y);
                    for (int i = 1; i < ring.Lat.Length; i++)
                    {
                        var p = vp.GeoToScreen(ring.Lat[i], ring.Lon[i]);
                        pb.AddLine(p.x, p.y);
                    }
                    pb.EndFigure(CanvasFigureLoop.Closed);
                }

                if (pb != null)
                    _cachedGeo = CanvasGeometry.CreatePath(pb);
            }
            finally
            {
                pb?.Dispose();
            }

            _cLat = vp.CenterLat; _cLon = vp.CenterLon;
            _cZoom = vp.Zoom; _cW = vp.ScreenWidth; _cH = vp.ScreenHeight;
        }
    }
}
