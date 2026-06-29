using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace WinMaps.Download
{
    internal class NaturalEarthCountry
    {
        public string Name;
        public string IsoA2;
        public string Continent;
        public List<List<(double Lat, double Lon)>> Geometry;
    }

    /// <summary>
    /// Parses Natural Earth GeoJSON country data bundled in the app assets.
    /// Provides continent grouping and ISO-based lookup for the world map view.
    /// </summary>
    internal class NaturalEarthIndex
    {
        private List<NaturalEarthCountry> _countries;
        private Dictionary<string, List<NaturalEarthCountry>> _byContinent;
        private Dictionary<string, NaturalEarthCountry> _byIso;

        public bool IsLoaded { get; private set; }

        public async Task LoadAsync()
        {
            var file = await StorageFile.GetFileFromApplicationUriAsync(
                new Uri("ms-appx:///Assets/NaturalEarth/ne_50m_admin_0_countries.geojson"));
            string json = await FileIO.ReadTextAsync(file);
            Parse(json);
            IsLoaded = true;
        }

        /// <summary>
        /// Loads Natural Earth country data at the given resolution ("50m" or "110m").
        /// The coarse 110m set is much lighter and is used for the world basemap fallback.
        /// </summary>
        public async Task LoadAsync(string resolution)
        {
            string asset = resolution == "110m"
                ? "ms-appx:///Assets/NaturalEarth/ne_110m_admin_0_countries.geojson"
                : "ms-appx:///Assets/NaturalEarth/ne_50m_admin_0_countries.geojson";
            var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(asset));
            string json = await FileIO.ReadTextAsync(file);
            Parse(json);
            IsLoaded = true;
        }

        public List<string> GetContinents()
        {
            if (_byContinent == null) return new List<string>();
            var result = new List<string>(_byContinent.Keys);
            result.Sort();
            return result;
        }

        public List<NaturalEarthCountry> GetCountriesByContinent(string continent)
        {
            if (_byContinent != null && _byContinent.TryGetValue(continent, out var list))
                return list;
            return new List<NaturalEarthCountry>();
        }

        public List<NaturalEarthCountry> GetAllCountries()
        {
            return _countries ?? new List<NaturalEarthCountry>();
        }

        public NaturalEarthCountry GetByIso(string isoA2)
        {
            if (_byIso != null && _byIso.TryGetValue(isoA2, out var country))
                return country;
            return null;
        }

        private void Parse(string json)
        {
            _countries = new List<NaturalEarthCountry>();
            _byContinent = new Dictionary<string, List<NaturalEarthCountry>>();
            _byIso = new Dictionary<string, NaturalEarthCountry>();

            var root = JsonObject.Parse(json);
            var features = root.GetNamedArray("features");

            for (uint i = 0; i < features.Count; i++)
            {
                var feature = features.GetObjectAt(i);
                var props = feature.GetNamedObject("properties");

                string name = props.GetNamedString("NAME", "");
                string isoA2 = props.GetNamedString("ISO_A2", "");
                string continent = props.GetNamedString("CONTINENT", "");

                // Russia is listed as "Europe" in Natural Earth but spans both continents.
                // Give it its own entry so Europe can be browsed without Russia dominating the view.
                if (isoA2 == "RU")
                    continent = "Russia";

                if (string.IsNullOrEmpty(name)) continue;
                if (continent == "Seven seas (open ocean)") continue;
                if (continent == "Antarctica") continue;

                var geometry = ParseGeometry(feature);
                if (geometry == null || geometry.Count == 0) continue;

                var country = new NaturalEarthCountry
                {
                    Name = name,
                    IsoA2 = isoA2,
                    Continent = continent,
                    Geometry = geometry
                };

                _countries.Add(country);

                if (!_byContinent.ContainsKey(continent))
                    _byContinent[continent] = new List<NaturalEarthCountry>();
                _byContinent[continent].Add(country);

                if (!string.IsNullOrEmpty(isoA2) && isoA2 != "-99")
                    _byIso[isoA2] = country;
            }
        }

        private static List<List<(double Lat, double Lon)>> ParseGeometry(JsonObject feature)
        {
            if (!feature.ContainsKey("geometry") ||
                feature.GetNamedValue("geometry").ValueType != JsonValueType.Object)
                return null;

            var geom = feature.GetNamedObject("geometry");
            string geomType = geom.GetNamedString("type", "");
            var coords = geom.GetNamedArray("coordinates");

            var rings = new List<List<(double Lat, double Lon)>>();

            if (geomType == "Polygon")
            {
                ParsePolygonOuterRing(coords, rings);
            }
            else if (geomType == "MultiPolygon")
            {
                for (uint p = 0; p < coords.Count; p++)
                    ParsePolygonOuterRing(coords.GetArrayAt(p), rings);
            }

            return rings;
        }

        /// <summary>
        /// Extracts only the outer ring (index 0) of a GeoJSON Polygon's coordinates.
        /// Holes are ignored because overlapping country features cover them anyway.
        /// </summary>
        private static void ParsePolygonOuterRing(JsonArray polygonCoords,
                                                   List<List<(double Lat, double Lon)>> rings)
        {
            if (polygonCoords.Count == 0) return;

            var ringCoords = polygonCoords.GetArrayAt(0);
            var ring = new List<(double Lat, double Lon)>((int)ringCoords.Count);
            for (uint p = 0; p < ringCoords.Count; p++)
            {
                var pt = ringCoords.GetArrayAt(p);
                double lon = pt.GetNumberAt(0); // GeoJSON: x = lon
                double lat = pt.GetNumberAt(1); // GeoJSON: y = lat
                ring.Add((lat, lon));
            }
            if (ring.Count >= 3)
                rings.Add(ring);
        }
    }
}
