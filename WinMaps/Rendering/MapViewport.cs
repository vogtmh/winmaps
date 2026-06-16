using System;
using Windows.Foundation;

namespace WinMaps.Rendering
{
    internal class MapViewport
    {
        // Center of the viewport in geographic coordinates
        public double CenterLat { get; set; }
        public double CenterLon { get; set; }

        // Zoom level (0 = whole world, 18 = street level)
        public double Zoom { get; set; }

        // Screen dimensions
        public double ScreenWidth { get; set; }
        public double ScreenHeight { get; set; }

        public const double MinZoom = 4;
        public const double MaxZoom = 19;

        public MapViewport()
        {
            // Default: Stuttgart center
            CenterLat = 48.7758;
            CenterLon = 9.1829;
            Zoom = 16;
        }

        /// <summary>
        /// Gets the number of pixels per degree of longitude at the current zoom level.
        /// </summary>
        private double PixelsPerDegreeLon
        {
            get
            {
                double tileSize = 256.0;
                double numTiles = Math.Pow(2, Zoom);
                return (numTiles * tileSize) / 360.0;
            }
        }

        /// <summary>
        /// Converts latitude to Mercator Y (in tile-space pixels at current zoom).
        /// </summary>
        private double LatToMercatorY(double lat)
        {
            double tileSize = 256.0;
            double numTiles = Math.Pow(2, Zoom);
            double latRad = lat * Math.PI / 180.0;
            double mercN = Math.Log(Math.Tan(Math.PI / 4.0 + latRad / 2.0));
            return (numTiles * tileSize / 2.0) - (mercN * numTiles * tileSize / (2.0 * Math.PI));
        }

        /// <summary>
        /// Converts longitude to Mercator X (in tile-space pixels at current zoom).
        /// </summary>
        private double LonToMercatorX(double lon)
        {
            double tileSize = 256.0;
            double numTiles = Math.Pow(2, Zoom);
            return ((lon + 180.0) / 360.0) * numTiles * tileSize;
        }

        /// <summary>
        /// Converts Mercator Y back to latitude.
        /// </summary>
        private double MercatorYToLat(double y)
        {
            double tileSize = 256.0;
            double numTiles = Math.Pow(2, Zoom);
            double mercN = (numTiles * tileSize / 2.0 - y) * (2.0 * Math.PI) / (numTiles * tileSize);
            return (2.0 * Math.Atan(Math.Exp(mercN)) - Math.PI / 2.0) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Converts Mercator X back to longitude.
        /// </summary>
        private double MercatorXToLon(double x)
        {
            double tileSize = 256.0;
            double numTiles = Math.Pow(2, Zoom);
            return (x / (numTiles * tileSize)) * 360.0 - 180.0;
        }

        /// <summary>
        /// Converts a geographic coordinate to screen position.
        /// (0,0) is top-left of the viewport.
        /// </summary>
        public (float x, float y) GeoToScreen(double lat, double lon)
        {
            double centerX = LonToMercatorX(CenterLon);
            double centerY = LatToMercatorY(CenterLat);

            double pointX = LonToMercatorX(lon);
            double pointY = LatToMercatorY(lat);

            float screenX = (float)(pointX - centerX + ScreenWidth / 2.0);
            float screenY = (float)(pointY - centerY + ScreenHeight / 2.0);

            return (screenX, screenY);
        }

        /// <summary>
        /// Converts a screen position to geographic coordinates.
        /// </summary>
        public (double lat, double lon) ScreenToGeo(double screenX, double screenY)
        {
            double centerX = LonToMercatorX(CenterLon);
            double centerY = LatToMercatorY(CenterLat);

            double mercX = screenX - ScreenWidth / 2.0 + centerX;
            double mercY = screenY - ScreenHeight / 2.0 + centerY;

            return (MercatorYToLat(mercY), MercatorXToLon(mercX));
        }

        /// <summary>
        /// Gets the geographic bounding box of the current viewport.
        /// </summary>
        public (double minLat, double maxLat, double minLon, double maxLon) GetBounds()
        {
            var topLeft = ScreenToGeo(0, 0);
            var bottomRight = ScreenToGeo(ScreenWidth, ScreenHeight);

            return (
                Math.Min(topLeft.lat, bottomRight.lat),
                Math.Max(topLeft.lat, bottomRight.lat),
                Math.Min(topLeft.lon, bottomRight.lon),
                Math.Max(topLeft.lon, bottomRight.lon)
            );
        }

        /// <summary>
        /// Pans the map by a screen-space delta.
        /// </summary>
        public void Pan(double deltaScreenX, double deltaScreenY)
        {
            double centerX = LonToMercatorX(CenterLon);
            double centerY = LatToMercatorY(CenterLat);

            centerX -= deltaScreenX;
            centerY -= deltaScreenY;

            CenterLon = MercatorXToLon(centerX);
            CenterLat = MercatorYToLat(centerY);

            // Clamp latitude to valid Mercator range
            CenterLat = Math.Max(-85.0, Math.Min(85.0, CenterLat));
            CenterLon = ((CenterLon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        }

        /// <summary>
        /// Zooms centered on a screen point.
        /// </summary>
        public void ZoomAt(double screenX, double screenY, double zoomDelta)
        {
            // Get the geographic position under the pointer before zooming
            var geoBeforeZoom = ScreenToGeo(screenX, screenY);

            Zoom = Math.Max(MinZoom, Math.Min(MaxZoom, Zoom + zoomDelta));

            // Get where that geographic position ends up on screen after zooming
            var screenAfterZoom = GeoToScreen(geoBeforeZoom.lat, geoBeforeZoom.lon);

            // Pan so that position stays under the pointer
            Pan(screenAfterZoom.x - screenX, screenAfterZoom.y - screenY);
        }

        /// <summary>
        /// Zooms to fit the given bounds with some padding.
        /// </summary>
        public void ZoomToBounds(double minLat, double maxLat, double minLon, double maxLon)
        {
            CenterLat = (minLat + maxLat) / 2.0;
            CenterLon = (minLon + maxLon) / 2.0;

            // Find zoom level that fits the bounds
            for (double z = MaxZoom; z >= MinZoom; z -= 0.5)
            {
                Zoom = z;
                var bounds = GetBounds();
                if (bounds.minLat <= minLat && bounds.maxLat >= maxLat &&
                    bounds.minLon <= minLon && bounds.maxLon >= maxLon)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Returns the approximate meters per pixel at the viewport center.
        /// Used for level-of-detail decisions.
        /// </summary>
        public double MetersPerPixel
        {
            get
            {
                double latRad = CenterLat * Math.PI / 180.0;
                double metersPerPixelAtEquator = 156543.03392 / Math.Pow(2, Zoom);
                return metersPerPixelAtEquator * Math.Cos(latRad);
            }
        }
    }
}
