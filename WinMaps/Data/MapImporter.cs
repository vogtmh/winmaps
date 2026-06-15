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

            // ---- Pass 1: Nodes ----
            ReportProgress(ImportPhase.Nodes, 0, 0, 0);

            long nodeCount = 0;
            int batchCount = 0;

            using (var stream = File.OpenRead(pbfPath))
            {
                var parser = new OsmPbfParser();
                Microsoft.Data.Sqlite.SqliteTransaction tx = db.BeginTransaction();

                parser.OnNode += node =>
                {
                    db.InsertNode(node.Id, node.Lat, node.Lon, tx);
                    nodeCount++;
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
                    ReportProgress(ImportPhase.Nodes, pct, nodeCount, 0);
                };

                parser.Parse(stream, fileSize, ct);

                tx.Commit();
                tx.Dispose();
            }

            ReportProgress(ImportPhase.Nodes, 100, nodeCount, 0);

            // ---- Pass 2: Ways ----
            ReportProgress(ImportPhase.Ways, 0, nodeCount, 0);

            long wayCount = 0;
            batchCount = 0;

            // We need to collect way node refs and compute bounds after inserting
            var pendingBounds = new List<(long wayId, long[] nodeRefs)>();

            using (var stream = File.OpenRead(pbfPath))
            {
                var parser = new OsmPbfParser();
                Microsoft.Data.Sqlite.SqliteTransaction tx = db.BeginTransaction();

                parser.OnWay += way =>
                {
                    db.InsertWay(way.Id, (int)way.Type, way.SubType, tx);

                    for (int i = 0; i < way.NodeRefs.Length; i++)
                    {
                        db.InsertWayNode(way.Id, i, way.NodeRefs[i], tx);
                    }

                    pendingBounds.Add((way.Id, way.NodeRefs));

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
                    ReportProgress(ImportPhase.Ways, pct, nodeCount, wayCount);
                };

                parser.Parse(stream, fileSize, ct);

                tx.Commit();
                tx.Dispose();
            }

            ReportProgress(ImportPhase.Ways, 100, nodeCount, wayCount);

            // ---- Build way bounds ----
            ReportProgress(ImportPhase.BuildingIndex, 0, nodeCount, wayCount);

            int boundsProcessed = 0;
            int totalBounds = pendingBounds.Count;

            {
                var tx = db.BeginTransaction();
                batchCount = 0;

                foreach (var (wayId, nodeRefs) in pendingBounds)
                {
                    ct.ThrowIfCancellationRequested();

                    var geometry = db.GetWayGeometry(wayId);
                    if (geometry.Count > 0)
                    {
                        double minLat = double.MaxValue, maxLat = double.MinValue;
                        double minLon = double.MaxValue, maxLon = double.MinValue;

                        foreach (var (lat, lon) in geometry)
                        {
                            if (lat < minLat) minLat = lat;
                            if (lat > maxLat) maxLat = lat;
                            if (lon < minLon) minLon = lon;
                            if (lon > maxLon) maxLon = lon;
                        }

                        db.InsertWayBounds(wayId, minLat, maxLat, minLon, maxLon, tx);
                    }

                    boundsProcessed++;
                    batchCount++;

                    if (batchCount >= BatchSize)
                    {
                        tx.Commit();
                        tx.Dispose();
                        tx = db.BeginTransaction();
                        batchCount = 0;

                        double pct = totalBounds > 0 ? (double)boundsProcessed / totalBounds * 100.0 : 0;
                        ReportProgress(ImportPhase.BuildingIndex, pct, nodeCount, wayCount);
                    }
                }

                tx.Commit();
                tx.Dispose();
            }

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
