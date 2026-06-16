using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace WinMaps.Download
{
    internal class GeofabrikRegion
    {
        public string Id;
        public string Name;
        public string ParentId; // null for continents
        public string PbfUrl;

        // Populated by GeofabrikGeoIndex (index-v1.json); null when only nogeom index is loaded.
        // Each element is one ring: list of (lat, lon) points.
        // MultiPolygons are flattened to multiple rings.
        public List<List<(double Lat, double Lon)>> Geometry;

        // Bounding box from index-v1.json: [minLon, minLat, maxLon, maxLat]
        public double BboxMinLon, BboxMinLat, BboxMaxLon, BboxMaxLat;
        public bool HasBbox;
    }

    internal class GeofabrikIndex
    {
        private const string IndexUrl = "https://download.geofabrik.de/index-v1-nogeom.json";
        private const string CacheFileName = "geofabrik-index.json";

        private Dictionary<string, GeofabrikRegion> _regions;
        private Dictionary<string, List<GeofabrikRegion>> _childrenByParent;
        private Dictionary<string, GeofabrikRegion> _regionByIso;

        public bool IsLoaded => _regions != null && _regions.Count > 0;

        /// <summary>
        /// Loads the Geofabrik index from cache, or downloads it if not cached.
        /// </summary>
        public async Task LoadAsync()
        {
            // Try loading from cache first
            string json = await LoadFromCacheAsync();
            if (json != null)
            {
                ParseIndex(json);
                return;
            }

            // Download and cache
            await RefreshAsync();
        }

        /// <summary>
        /// Downloads the Geofabrik index fresh, replacing any cached version.
        /// </summary>
        public async Task RefreshAsync()
        {
            using (var client = new HttpClient())
            {
                string json = await client.GetStringAsync(new Uri(IndexUrl));
                await SaveToCacheAsync(json);
                ParseIndex(json);
            }
        }

        /// <summary>
        /// Returns the top-level continents (regions with no parent), sorted by name.
        /// </summary>
        public List<GeofabrikRegion> GetRoots()
        {
            if (_childrenByParent == null) return new List<GeofabrikRegion>();

            if (_childrenByParent.TryGetValue("", out var roots))
                return roots;

            return new List<GeofabrikRegion>();
        }

        /// <summary>
        /// Returns direct children of a given parent region, sorted by name.
        /// </summary>
        public List<GeofabrikRegion> GetChildren(string parentId)
        {
            if (_childrenByParent == null) return new List<GeofabrikRegion>();

            if (_childrenByParent.TryGetValue(parentId, out var children))
                return children;

            return new List<GeofabrikRegion>();
        }

        /// <summary>
        /// Returns true if the given region has child regions.
        /// </summary>
        public bool HasChildren(string regionId)
        {
            return _childrenByParent != null &&
                   _childrenByParent.ContainsKey(regionId) &&
                   _childrenByParent[regionId].Count > 0;
        }

        /// <summary>
        /// Returns all leaf descendants of the given region (regions with no children),
        /// collected depth-first. If the region itself has no children it returns just itself.
        /// </summary>
        public List<GeofabrikRegion> GetLeaves(string regionId)
        {
            var result = new List<GeofabrikRegion>();
            CollectLeaves(regionId, result);
            return result;
        }

        private void CollectLeaves(string regionId, List<GeofabrikRegion> result)
        {
            if (!HasChildren(regionId))
            {
                var region = GetRegion(regionId);
                if (region != null) result.Add(region);
                return;
            }
            foreach (var child in GetChildren(regionId))
                CollectLeaves(child.Id, result);
        }

        /// <summary>
        /// Returns a region by its ID, or null if not found.
        /// </summary>
        public GeofabrikRegion GetRegion(string id)
        {
            if (_regions != null && _regions.TryGetValue(id, out var region))
                return region;
            return null;
        }

        /// <summary>
        /// Returns a region by its ISO 3166-1 alpha-2 code, or null if not found.
        /// </summary>
        public GeofabrikRegion GetRegionByIso(string isoA2)
        {
            if (_regionByIso != null && _regionByIso.TryGetValue(isoA2, out var region))
                return region;
            return null;
        }

        /// <summary>
        /// Returns true if the region is a continent (no parent) — not downloadable.
        /// </summary>
        public bool IsContinent(string id)
        {
            var region = GetRegion(id);
            return region != null && region.ParentId == null;
        }

        /// <summary>
        /// Returns the country-level region ID for any given region by walking up the
        /// parent chain until finding a region whose parent is a continent (no parent).
        /// "stuttgart-regbez" → "germany", "hamburg" → "germany", "germany" → "germany"
        /// </summary>
        public string GetCountryId(string regionId)
        {
            var region = GetRegion(regionId);
            if (region == null || region.ParentId == null) return regionId;
            var parent = GetRegion(region.ParentId);
            if (parent == null || parent.ParentId == null)
                return regionId; // this region's parent is a continent → this IS the country
            return GetCountryId(region.ParentId); // recurse up
        }

        /// <summary>
        /// Builds the breadcrumb path for a region (e.g., "Europe > Germany > Baden-Württemberg").
        /// </summary>
        public string GetBreadcrumb(string id)
        {
            var parts = new List<string>();
            string current = id;
            while (current != null)
            {
                var region = GetRegion(current);
                if (region == null) break;
                parts.Insert(0, region.Name);
                current = region.ParentId;
            }
            return string.Join(" › ", parts);
        }

        private void ParseIndex(string json)
        {
            _regions = new Dictionary<string, GeofabrikRegion>();
            _childrenByParent = new Dictionary<string, List<GeofabrikRegion>>();
            _regionByIso = new Dictionary<string, GeofabrikRegion>();

            var root = JsonObject.Parse(json);
            var features = root.GetNamedArray("features");

            for (uint i = 0; i < features.Count; i++)
            {
                var feature = features.GetObjectAt(i);
                var props = feature.GetNamedObject("properties");

                string id = props.GetNamedString("id", "");
                string name = props.GetNamedString("name", "");

                // Get parent (may not exist, or may be JSON null)
                string parentId = null;
                if (props.ContainsKey("parent") &&
                    props.GetNamedValue("parent").ValueType == JsonValueType.String)
                {
                    parentId = props.GetNamedString("parent");
                }

                // Get PBF URL (skip entries without it)
                string pbfUrl = null;
                if (props.ContainsKey("urls") &&
                    props.GetNamedValue("urls").ValueType == JsonValueType.Object)
                {
                    var urls = props.GetNamedObject("urls");
                    if (urls.ContainsKey("pbf") &&
                        urls.GetNamedValue("pbf").ValueType == JsonValueType.String)
                    {
                        pbfUrl = urls.GetNamedString("pbf");
                    }
                }

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                    continue;

                // Note: continents have no PBF URL but are kept in the index so that
                // GetRoots() and GetChildren() work correctly for hierarchy navigation.
                // PbfUrl will be null for continents — callers must check before downloading.

                var region = new GeofabrikRegion
                {
                    Id = id,
                    Name = name,
                    ParentId = parentId,
                    PbfUrl = pbfUrl
                };

                _regions[id] = region;

                // Build ISO → region index
                if (props.ContainsKey("iso3166-1:alpha2") &&
                    props.GetNamedValue("iso3166-1:alpha2").ValueType == JsonValueType.Array)
                {
                    var isoCodes = props.GetNamedArray("iso3166-1:alpha2");
                    for (uint j = 0; j < isoCodes.Count; j++)
                    {
                        string isoCode = isoCodes.GetStringAt(j);
                        if (!string.IsNullOrEmpty(isoCode))
                            _regionByIso[isoCode] = region;
                    }
                }

                // Build parent → children index
                string parentKey = parentId ?? "";
                if (!_childrenByParent.ContainsKey(parentKey))
                    _childrenByParent[parentKey] = new List<GeofabrikRegion>();

                _childrenByParent[parentKey].Add(region);
            }

            // Sort all children lists by name
            foreach (var list in _childrenByParent.Values)
            {
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
        }

        private async Task<string> LoadFromCacheAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var mapsFolder = await localFolder.GetFolderAsync("Maps");
                var file = await mapsFolder.GetFileAsync(CacheFileName);
                return await FileIO.ReadTextAsync(file);
            }
            catch
            {
                return null;
            }
        }

        private async Task SaveToCacheAsync(string json)
        {
            var localFolder = ApplicationData.Current.LocalFolder;
            var mapsFolder = await localFolder.CreateFolderAsync("Maps",
                CreationCollisionOption.OpenIfExists);
            var file = await mapsFolder.CreateFileAsync(CacheFileName,
                CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, json);
        }
    }
}
