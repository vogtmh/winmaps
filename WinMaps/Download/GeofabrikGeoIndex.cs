using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace WinMaps.Download
{
    /// <summary>
    /// Loads geometry and bounding-box data from Geofabrik's index-v1.json (with-geom variant).
    /// After loading, geometry is patched directly onto the GeofabrikRegion objects that live in
    /// the provided GeofabrikIndex so the rest of the code needs no changes.
    /// </summary>
    internal class GeofabrikGeoIndex
    {
        private const string GeoIndexUrl = "https://download.geofabrik.de/index-v1.json";
        private const string GeoCacheFileName = "geofabrik-geo.json";

        public bool IsLoaded { get; private set; }

        /// <summary>
        /// Loads geometry data from local cache, falling back to network.
        /// Patches geometry onto regions in <paramref name="index"/>.
        /// </summary>
        public async Task LoadAsync(GeofabrikIndex index)
        {
            string json = await LoadFromCacheAsync();
            if (json == null)
                json = await FetchAndCacheAsync();

            if (json != null)
                ParseAndPatch(json, index);

            IsLoaded = true;
        }

        private void ParseAndPatch(string json, GeofabrikIndex index)
        {
            try
            {
                var root = JsonObject.Parse(json);
                var features = root.GetNamedArray("features");

                for (uint i = 0; i < features.Count; i++)
                {
                    var feature = features.GetObjectAt(i);
                    var props = feature.GetNamedObject("properties");

                    string id = props.GetNamedString("id", "");
                    if (string.IsNullOrEmpty(id)) continue;

                    var region = index.GetRegion(id);
                    if (region == null) continue; // not in catalog (no PBF)

                    // Parse bounding box: standard GeoJSON puts bbox at Feature level,
                    // not inside properties. [minLon, minLat, maxLon, maxLat]
                    if (feature.ContainsKey("bbox") &&
                        feature.GetNamedValue("bbox").ValueType == JsonValueType.Array)
                    {
                        var bbox = feature.GetNamedArray("bbox");
                        if (bbox.Count >= 4)
                        {
                            region.BboxMinLon = bbox.GetNumberAt(0);
                            region.BboxMinLat = bbox.GetNumberAt(1);
                            region.BboxMaxLon = bbox.GetNumberAt(2);
                            region.BboxMaxLat = bbox.GetNumberAt(3);
                            region.HasBbox = true;
                        }
                    }

                    // Parse geometry (Polygon or MultiPolygon)
                    if (!feature.ContainsKey("geometry") ||
                        feature.GetNamedValue("geometry").ValueType != JsonValueType.Object)
                        continue;

                    var geom = feature.GetNamedObject("geometry");
                    string geomType = geom.GetNamedString("type", "");
                    var coords = geom.GetNamedArray("coordinates");

                    var rings = new List<List<(double Lat, double Lon)>>();

                    if (geomType == "Polygon")
                    {
                        ParsePolygon(coords, rings);
                    }
                    else if (geomType == "MultiPolygon")
                    {
                        for (uint p = 0; p < coords.Count; p++)
                            ParsePolygon(coords.GetArrayAt(p), rings);
                    }

                    if (rings.Count > 0)
                        region.Geometry = rings;
                }
            }
            catch { /* malformed JSON — skip geometry */ }
        }

        private static void ParsePolygon(JsonArray polygonCoords,
                                          List<List<(double Lat, double Lon)>> rings)
        {
            // polygonCoords = array of rings; each ring = array of [lon, lat] pairs
            for (uint r = 0; r < polygonCoords.Count; r++)
            {
                var ringCoords = polygonCoords.GetArrayAt(r);
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

        private async Task<string> LoadFromCacheAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var mapsFolder = await localFolder.GetFolderAsync("Maps");
                var file = await mapsFolder.GetFileAsync(GeoCacheFileName);
                return await FileIO.ReadTextAsync(file);
            }
            catch { return null; }
        }

        private async Task<string> FetchAndCacheAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(120);
                    string json = await client.GetStringAsync(GeoIndexUrl);

                    // Cache it
                    var localFolder = ApplicationData.Current.LocalFolder;
                    var mapsFolder = await localFolder.CreateFolderAsync("Maps",
                        CreationCollisionOption.OpenIfExists);
                    var file = await mapsFolder.CreateFileAsync(GeoCacheFileName,
                        CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteTextAsync(file, json);

                    return json;
                }
            }
            catch { return null; }
        }
    }
}
