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

            // ---- Single pass: buffer nodes in memory, resolve ways inline ----
            // Phase 1 (reported as "Nodes"): collect all nodes into memory dictionary
            // Phase 2 (reported as "Ways"): when ways arrive, resolve coords + insert
            //
            // PBF block order guarantees: within each PrimitiveBlock, dense nodes
            // come before ways. But nodes referenced by a way may be in an earlier block.
            // So we do a single pass but buffer ALL nodes first (they arrive before ways
            // in well-formed PBF files from Geofabrik).

            ReportProgress(ImportPhase.Nodes, 0, 0, 0);

            var nodeBuffer = new Dictionary<long, (double lat, double lon)>();
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
                        nodeBuffer[node.Id] = (node.Lat, node.Lon);
                        nodeCount++;
                    };

                    parser.OnWay += way =>
                    {
                        // Resolve node refs to coordinates
                        var points = new List<(double lat, double lon)>(way.NodeRefs.Length);
                        double minLat = double.MaxValue, maxLat = double.MinValue;
                        double minLon = double.MaxValue, maxLon = double.MinValue;
                        bool allResolved = true;

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
                            else
                            {
                                allResolved = false;
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
                        double pct = total > 0 ? (double)pos / total * 100.0 : 0;
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

            // Free node buffer — no longer needed
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
