using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinMaps.Pbf;
// Note: Array.BinarySearch used instead of HashSet/Dictionary for node lookup
// to avoid GC overhead of ~22 bytes per HashSet slot and ~32 bytes per Dictionary entry.

namespace WinMaps.Data
{
    internal enum ImportPhase
    {
        Nodes,
        Ways,
        BuildingIndex,
        Done
    }

    internal class ImportProgress
    {
        public ImportPhase Phase;
        public double Percent;
        public long NodesImported;
        public long WaysImported;
    }

    internal class MapImporter
    {
        private const int BatchSize = 50000;

        public event Action<ImportProgress> OnProgress;

        public async Task ImportAsync(string pbfPath, MapDatabase db, CancellationToken ct, string regionId, string regionName)
        {
            await Task.Run(() => Import(pbfPath, db, ct, regionId, regionName), ct);
        }

        private void Import(string pbfPath, MapDatabase db, CancellationToken ct, string regionId, string regionName)
        {
            db.CreateSchema();

            // Drop spatial indexes before inserting so SQLite doesn't have to maintain
            // them row-by-row during bulk import. They are rebuilt at the end.
            db.DropSpatialIndex();

            long fileSize = new FileInfo(pbfPath).Length;

            // ---- Pass 1: Scan ways to collect referenced node IDs ----
            // Collect raw refs into a List<long>, sort+dedup periodically to keep
            // memory bounded on low-RAM devices (Lumia 735 = 1GB).
            ReportProgress(ImportPhase.Nodes, 0, 0, 0);

            var nodeIdList = new List<long>();
            const int CompactThreshold = 2_000_000; // compact every 2M new refs
            int newSinceCompact = 0;

            using (var stream = File.OpenRead(pbfPath))
            {
                var parser = new OsmPbfParser();

                parser.OnWay += way =>
                {
                    for (int i = 0; i < way.NodeRefs.Length; i++)
                        nodeIdList.Add(way.NodeRefs[i]);

                    newSinceCompact += way.NodeRefs.Length;

                    // Periodically sort+dedup IN-PLACE to prevent unbounded memory growth.
                    // Only trigger when enough NEW refs accumulated since last compaction.
                    if (newSinceCompact >= CompactThreshold)
                    {
                        nodeIdList.Sort();
                        int w = 1;
                        for (int i = 1; i < nodeIdList.Count; i++)
                            if (nodeIdList[i] != nodeIdList[i - 1])
                                nodeIdList[w++] = nodeIdList[i];
                        nodeIdList.RemoveRange(w, nodeIdList.Count - w);
                        newSinceCompact = 0;
                    }
                };

                parser.OnProgress += (pos, total) =>
                {
                    double pct = total > 0 ? (double)pos / total * 50.0 : 0;
                    ReportProgress(ImportPhase.Nodes, pct, 0, 0);
                };

                parser.Parse(stream, fileSize, ct);
            }

            // Final sort and deduplicate into a compact array
            nodeIdList.Sort();
            long[] nodeIds = DeduplicateSorted(nodeIdList);
            nodeIdList.Clear();
            nodeIdList.TrimExcess(); // release list's internal array
            nodeIdList = null;
            GC.Collect(); // reclaim list memory before allocating coordinate buffers

            // Two parallel coordinate arrays indexed by position in nodeIds[].
            // No pointer overhead — GC scans them in O(1).
            // Using float (not double) to halve memory: ~0.5m precision is fine for rendering.
            float[] latBuf = new float[nodeIds.Length];
            float[] lonBuf = new float[nodeIds.Length];

            // ---- Pass 2: Fill coordinate arrays, resolve ways inline ----
            long nodeCount = 0;
            long wayCount = 0;
            int batchCount = 0;

            try
            {
                db.PrepareInsertStatement();
                Microsoft.Data.Sqlite.SqliteTransaction tx = db.BeginTransaction();

                try
                {
                    using (var stream = File.OpenRead(pbfPath))
                {
                    var parser = new OsmPbfParser();

                    parser.OnNode += node =>
                    {
                        int idx = Array.BinarySearch(nodeIds, node.Id);
                        if (idx >= 0)
                        {
                            latBuf[idx] = (float)node.Lat;
                            lonBuf[idx] = (float)node.Lon;
                        }
                        nodeCount++;
                    };

                    parser.OnWay += way =>
                    {
                        // Resolve node refs via binary search into sorted nodeIds[]
                        var points = new List<(double lat, double lon)>(way.NodeRefs.Length);
                        double minLat = double.MaxValue, maxLat = double.MinValue;
                        double minLon = double.MaxValue, maxLon = double.MinValue;

                        for (int i = 0; i < way.NodeRefs.Length; i++)
                        {
                            int idx = Array.BinarySearch(nodeIds, way.NodeRefs[i]);
                            if (idx >= 0)
                            {
                                double lat = latBuf[idx];
                                double lon = lonBuf[idx];
                                points.Add((lat, lon));
                                if (lat < minLat) minLat = lat;
                                if (lat > maxLat) maxLat = lat;
                                if (lon < minLon) minLon = lon;
                                if (lon > maxLon) maxLon = lon;
                            }
                        }

                        if (points.Count < 2)
                            return;

                        byte[] geometry = MapDatabase.EncodeGeometry(points);

                        // Pre-simplify geometry for low-zoom rendering (~0.0003° ≈ 33m ≈ 4px at Z12)
                        byte[] geoSimple = MapDatabase.SimplifyGeometry(points, 0.0003);

                        db.InsertWay(way.Id, (int)way.Type, way.SubType, geometry, geoSimple,
                            minLat, maxLat, minLon, maxLon, tx);

                        wayCount++;
                        batchCount++;

                        if (batchCount >= BatchSize)
                        {
                            tx.Commit();
                            tx.Dispose();
                            tx = db.BeginTransaction();
                            batchCount = 0;
                        }
                    };

                    parser.OnPoi += poi =>
                    {
                        db.InsertPoi(poi.Id, poi.Type, poi.SubType, poi.Name,
                            poi.Lat, poi.Lon, tx);

                        batchCount++;
                        if (batchCount >= BatchSize)
                        {
                            tx.Commit();
                            tx.Dispose();
                            tx = db.BeginTransaction();
                            batchCount = 0;
                        }
                    };

                    parser.OnPlace += place =>
                    {
                        db.InsertPlace(place.Id, place.PlaceType, place.Name,
                            place.Lat, place.Lon, tx);

                        batchCount++;
                        if (batchCount >= BatchSize)
                        {
                            tx.Commit();
                            tx.Dispose();
                            tx = db.BeginTransaction();
                            batchCount = 0;
                        }
                    };

                    parser.OnProgress += (pos, total) =>
                    {
                        double pct = total > 0 ? 50.0 + (double)pos / total * 50.0 : 50; // 50-100%
                        if (wayCount > 0)
                            ReportProgress(ImportPhase.Ways, pct, nodeCount, wayCount);
                        else
                            ReportProgress(ImportPhase.Nodes, pct, nodeCount, 0);
                    };

                    parser.Parse(stream, fileSize, ct);
                }

                    // Commit remaining batch
                    tx.Commit();
                    tx.Dispose();
                    tx = null;
                }
                finally
                {
                    tx?.Dispose();
                    db.DisposeInsertStatement();
                }
            }
            finally
            {
                // Always rebuild spatial indexes — even on cancellation or failure —
                // so the DB is never left in an unindexed state that breaks rendering.
                nodeIds = null;
                latBuf = null;
                lonBuf = null;
                GC.Collect();

                ReportProgress(ImportPhase.BuildingIndex, 50, nodeCount, wayCount);
                db.CreateSpatialIndex();
                db.Checkpoint();
            }

            // Only record success metadata after a clean import
            db.SetMetadata("import_date", DateTime.UtcNow.ToString("O"));
            db.SetMetadata("node_count", nodeCount.ToString());
            db.SetMetadata("way_count", wayCount.ToString());
            db.SetMetadata("source", pbfPath);

            db.InsertRegion(regionId, regionName);

            ReportProgress(ImportPhase.Done, 100, nodeCount, wayCount);
        }

        /// <summary>
        /// Deduplicates a pre-sorted List&lt;long&gt; into a compact long[] with no allocator overhead.
        /// </summary>
        private static long[] DeduplicateSorted(List<long> sorted)
        {
            if (sorted.Count == 0) return Array.Empty<long>();

            // Count unique values first
            int unique = 1;
            for (int i = 1; i < sorted.Count; i++)
                if (sorted[i] != sorted[i - 1]) unique++;

            long[] result = new long[unique];
            result[0] = sorted[0];
            int w = 1;
            for (int i = 1; i < sorted.Count; i++)
                if (sorted[i] != sorted[i - 1])
                    result[w++] = sorted[i];

            return result;
        }

        private void ReportProgress(ImportPhase phase, double percent, long nodes, long ways)
        {
            OnProgress?.Invoke(new ImportProgress
            {
                Phase = phase,
                Percent = percent,
                NodesImported = nodes,
                WaysImported = ways
            });
        }
    }
}
