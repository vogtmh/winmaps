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
    }

    internal class GeofabrikIndex
    {
        private const string IndexUrl = "https://download.geofabrik.de/index-v1-nogeom.json";
        private const string CacheFileName = "geofabrik-index.json";

        private Dictionary<string, GeofabrikRegion> _regions;
        private Dictionary<string, List<GeofabrikRegion>> _childrenByParent;

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
        /// Returns a region by its ID, or null if not found.
        /// </summary>
        public GeofabrikRegion GetRegion(string id)
        {
            if (_regions != null && _regions.TryGetValue(id, out var region))
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

            var root = JsonObject.Parse(json);
            var features = root.GetNamedArray("features");

            for (uint i = 0; i < features.Count; i++)
            {
                var feature = features.GetObjectAt(i);
                var props = feature.GetNamedObject("properties");

                string id = props.GetNamedString("id", "");
                string name = props.GetNamedString("name", "");

                // Get parent (may not exist)
                string parentId = null;
                if (props.ContainsKey("parent"))
                {
                    parentId = props.GetNamedString("parent", null);
                }

                // Get PBF URL (skip entries without it)
                string pbfUrl = null;
                if (props.ContainsKey("urls"))
                {
                    var urls = props.GetNamedObject("urls");
                    if (urls.ContainsKey("pbf"))
                    {
                        pbfUrl = urls.GetNamedString("pbf", null);
                    }
                }

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                    continue;

                // Skip entries without a PBF URL entirely
                if (string.IsNullOrEmpty(pbfUrl))
                    continue;

                var region = new GeofabrikRegion
                {
                    Id = id,
                    Name = name,
                    ParentId = parentId,
                    PbfUrl = pbfUrl
                };

                _regions[id] = region;

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
