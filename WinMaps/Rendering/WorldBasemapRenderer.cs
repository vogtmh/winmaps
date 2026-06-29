using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using WinMaps.Download;

namespace WinMaps.Rendering
{
    /// <summary>
    /// Draws a coarse world basemap (filled landmasses + thin country borders) as a fallback
    /// for areas with no downloaded map. Uses the bundled Natural Earth 110m country polygons,
    /// projected with the live map's Mercator viewport. Rings are culled by geographic bounding
    /// box and the projected geometry is cached per viewport so panning stays cheap on low-end
    /// devices (Lumia 735/950 XL).
    /// </summary>
    internal class WorldBasemapRenderer
    {
        private sealed class Ring
        {
            public double[] Lat;
            public double[] Lon;
            public double MinLat, MaxLat, MinLon, MaxLon;
        }

        private List<Ring> _rings;
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
            if (_cachedGeo == null) return;

            ds.FillGeometry(_cachedGeo, theme.Background);
            ds.DrawGeometry(_cachedGeo, theme.PlaceLabelColorLight, 0.5f);
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
