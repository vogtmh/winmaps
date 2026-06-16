using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinMaps.Pbf;

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

        public async Task ImportAsync(string pbfPath, MapDatabase db, CancellationToken ct)
        {
            await Task.Run(() => Import(pbfPath, db, ct), ct);
        }

        private void Import(string pbfPath, MapDatabase db, CancellationToken ct)
        {
            db.CreateSchema();

            long fileSize = new FileInfo(pbfPath).Length;

            // ---- Pass 1: Scan ways to collect referenced node IDs ----
            // Only decodes way node-refs (skips node lat/lon), so it's fast and lean.
            // The resulting HashSet is much smaller than buffering all nodes.
            ReportProgress(ImportPhase.Nodes, 0, 0, 0);

            var referencedNodeIds = new HashSet<long>();

            using (var stream = File.OpenRead(pbfPath))
            {
                var parser = new OsmPbfParser();

                parser.OnWay += way =>
                {
                    for (int i = 0; i < way.NodeRefs.Length; i++)
                        referencedNodeIds.Add(way.NodeRefs[i]);
                };

                parser.OnProgress += (pos, total) =>
                {
                    double pct = total > 0 ? (double)pos / total * 50.0 : 0; // 0-50%
                    ReportProgress(ImportPhase.Nodes, pct, 0, 0);
                };

                parser.Parse(stream, fileSize, ct);
            }

            // ---- Pass 2: Buffer only referenced nodes, resolve ways inline ----
            var nodeBuffer = new Dictionary<long, (double lat, double lon)>(referencedNodeIds.Count);
            long nodeCount = 0;
            long wayCount = 0;
            int batchCount = 0;

            db.PrepareInsertStatement();
            Microsoft.Data.Sqlite.SqliteTransaction tx = db.BeginTransaction();

            try
            {
                using (var stream = File.OpenRead(pbfPath))
                {
                    var parser = new OsmPbfParser();

                    parser.OnNode += node =>
                    {
                        if (referencedNodeIds.Contains(node.Id))
                        {
                            nodeBuffer[node.Id] = (node.Lat, node.Lon);
                        }
                        nodeCount++;
                    };

                    parser.OnWay += way =>
                    {
                        // Resolve node refs to coordinates
                        var points = new List<(double lat, double lon)>(way.NodeRefs.Length);
                        double minLat = double.MaxValue, maxLat = double.MinValue;
                        double minLon = double.MaxValue, maxLon = double.MinValue;

                        for (int i = 0; i < way.NodeRefs.Length; i++)
                        {
                            if (nodeBuffer.TryGetValue(way.NodeRefs[i], out var coord))
                            {
                                points.Add(coord);
                                if (coord.lat < minLat) minLat = coord.lat;
                                if (coord.lat > maxLat) maxLat = coord.lat;
                                if (coord.lon < minLon) minLon = coord.lon;
                                if (coord.lon > maxLon) maxLon = coord.lon;
                            }
                        }

                        // Need at least 2 points for a renderable way
                        if (points.Count < 2)
                            return;

                        byte[] geometry = MapDatabase.EncodeGeometry(points);
                        db.InsertWay(way.Id, (int)way.Type, way.SubType, geometry,
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

            // Free memory
            referencedNodeIds.Clear();
            nodeBuffer.Clear();
            nodeBuffer = null;

            // Build spatial index
            ReportProgress(ImportPhase.BuildingIndex, 50, nodeCount, wayCount);
            db.CreateSpatialIndex();

            db.SetMetadata("import_date", DateTime.UtcNow.ToString("O"));
            db.SetMetadata("node_count", nodeCount.ToString());
            db.SetMetadata("way_count", wayCount.ToString());
            db.SetMetadata("source", pbfPath);

            ReportProgress(ImportPhase.Done, 100, nodeCount, wayCount);
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
