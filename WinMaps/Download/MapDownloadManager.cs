using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace WinMaps.Download
{
    internal class DownloadProgress
    {
        public long BytesReceived;
        public long TotalBytes;
        public double Percent;
    }

    internal class MapRegion
    {
        public string Name;
        public string Url;
        public string FileName;

        public static readonly MapRegion[] AvailableRegions = new[]
        {
            new MapRegion
            {
                Name = "Stuttgart Region (Germany)",
                Url = "https://download.geofabrik.de/europe/germany/baden-wuerttemberg/regierungsbezirk-stuttgart-latest.osm.pbf",
                FileName = "regierungsbezirk-stuttgart.osm.pbf"
            },
            new MapRegion
            {
                Name = "Baden-Württemberg (Germany)",
                Url = "https://download.geofabrik.de/europe/germany/baden-wuerttemberg-latest.osm.pbf",
                FileName = "baden-wuerttemberg.osm.pbf"
            },
            new MapRegion
            {
                Name = "Germany",
                Url = "https://download.geofabrik.de/europe/germany-latest.osm.pbf",
                FileName = "germany.osm.pbf"
            },
            new MapRegion
            {
                Name = "Saarland (Germany)",
                Url = "https://download.geofabrik.de/europe/germany/saarland-latest.osm.pbf",
                FileName = "saarland.osm.pbf"
            },
            new MapRegion
            {
                Name = "Bremen (Germany)",
                Url = "https://download.geofabrik.de/europe/germany/bremen-latest.osm.pbf",
                FileName = "bremen.osm.pbf"
            }
        };
    }

    internal class MapDownloadManager
    {
        private const int BufferSize = 65536; // 64KB read buffer

        public event Action<DownloadProgress> OnProgress;

        /// <summary>
        /// Downloads a PBF file to local storage. Supports resume via HTTP Range.
        /// Returns the full path to the downloaded file.
        /// </summary>
        public async Task<string> DownloadAsync(MapRegion region, CancellationToken ct)
        {
            var localFolder = ApplicationData.Current.LocalFolder;
            var mapsFolder = await localFolder.CreateFolderAsync("Maps",
                CreationCollisionOption.OpenIfExists);

            string filePath = Path.Combine(mapsFolder.Path, region.FileName);
            string tempPath = filePath + ".partial";

            long existingSize = 0;
            try
            {
                var fileInfo = new FileInfo(tempPath);
                if (fileInfo.Exists)
                    existingSize = fileInfo.Length;
            }
            catch { }

            using (var client = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(region.Url));

                if (existingSize > 0)
                {
                    request.Headers.Range =
                        new System.Net.Http.Headers.RangeHeaderValue(existingSize, null);
                }

                var response = await client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                long totalBytes = 0;

                if (response.Content.Headers.ContentLength.HasValue)
                {
                    if ((int)response.StatusCode == 206) // PartialContent
                    {
                        totalBytes = existingSize + response.Content.Headers.ContentLength.Value;
                    }
                    else
                    {
                        totalBytes = response.Content.Headers.ContentLength.Value;
                        existingSize = 0; // Server didn't support range, restart
                    }
                }

                using (var responseStream = await response.Content.ReadAsStreamAsync())
                {
                    FileMode mode = existingSize > 0 ? FileMode.Append : FileMode.Create;
                    using (var fileStream = new FileStream(tempPath, mode, FileAccess.Write, FileShare.None, BufferSize))
                    {
                        byte[] buffer = new byte[BufferSize];
                        long bytesReceived = existingSize;
                        int read;

                        while ((read = await responseStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            await fileStream.WriteAsync(buffer, 0, read, ct);
                            bytesReceived += read;

                            OnProgress?.Invoke(new DownloadProgress
                            {
                                BytesReceived = bytesReceived,
                                TotalBytes = totalBytes,
                                Percent = totalBytes > 0 ? (double)bytesReceived / totalBytes * 100.0 : 0
                            });
                        }
                    }
                }
            }

            // Rename .partial to final name
            if (File.Exists(filePath))
                File.Delete(filePath);
            File.Move(tempPath, filePath);

            return filePath;
        }

        /// <summary>
        /// Checks if a map file already exists for the given region.
        /// </summary>
        public async Task<string> GetExistingMapPath(MapRegion region)
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var mapsFolder = await localFolder.GetFolderAsync("Maps");
                var file = await mapsFolder.GetFileAsync(region.FileName);
                return file.Path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the path to the SQLite database for a given region.
        /// </summary>
        public async Task<string> GetDatabasePath(MapRegion region)
        {
            var localFolder = ApplicationData.Current.LocalFolder;
            var mapsFolder = await localFolder.CreateFolderAsync("Maps",
                CreationCollisionOption.OpenIfExists);

            return Path.Combine(mapsFolder.Path, Path.ChangeExtension(region.FileName, ".db"));
        }
    }
}
