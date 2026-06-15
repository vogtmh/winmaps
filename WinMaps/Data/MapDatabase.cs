using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace WinMaps.Data
{
    internal class MapDatabase : IDisposable
    {
        private SqliteConnection _connection;
        private readonly string _dbPath;

        public MapDatabase(string dbPath)
        {
            _dbPath = dbPath;
        }

        public async Task OpenAsync()
        {
            await Task.Run(() =>
            {
                _connection = new SqliteConnection($"Data Source={_dbPath}");
                _connection.Open();

                Execute("PRAGMA journal_mode=WAL");
                Execute("PRAGMA synchronous=NORMAL");
                Execute("PRAGMA cache_size=-8000"); // 8MB cache
                Execute("PRAGMA temp_store=MEMORY");
            });
        }

        public void CreateSchema()
        {
            Execute(@"
                CREATE TABLE IF NOT EXISTS nodes (
                    id INTEGER PRIMARY KEY,
                    lat REAL NOT NULL,
                    lon REAL NOT NULL
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS ways (
                    id INTEGER PRIMARY KEY,
                    type INTEGER NOT NULL,
                    subtype TEXT
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS way_nodes (
                    way_id INTEGER NOT NULL,
                    seq INTEGER NOT NULL,
                    node_id INTEGER NOT NULL,
                    PRIMARY KEY (way_id, seq)
                )");

            // Bounding box table for ways (for fast spatial queries)
            Execute(@"
                CREATE TABLE IF NOT EXISTS way_bounds (
                    way_id INTEGER PRIMARY KEY,
                    min_lat REAL NOT NULL,
                    max_lat REAL NOT NULL,
                    min_lon REAL NOT NULL,
                    max_lon REAL NOT NULL
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT
                )");

            // Index for way_nodes lookups
            Execute("CREATE INDEX IF NOT EXISTS idx_way_nodes_node ON way_nodes(node_id)");

            // Spatial index on way_bounds
            Execute("CREATE INDEX IF NOT EXISTS idx_way_bounds_lat ON way_bounds(min_lat, max_lat)");
            Execute("CREATE INDEX IF NOT EXISTS idx_way_bounds_lon ON way_bounds(min_lon, max_lon)");
        }

        public SqliteTransaction BeginTransaction()
        {
            return _connection.BeginTransaction();
        }

        public void InsertNode(long id, double lat, double lon, SqliteTransaction tx)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO nodes(id, lat, lon) VALUES(@id, @lat, @lon)";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@lat", lat);
                cmd.Parameters.AddWithValue("@lon", lon);
                cmd.ExecuteNonQuery();
            }
        }

        public void InsertWay(long id, int type, string subType, SqliteTransaction tx)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO ways(id, type, subtype) VALUES(@id, @type, @sub)";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@sub", (object)subType ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public void InsertWayNode(long wayId, int seq, long nodeId, SqliteTransaction tx)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO way_nodes(way_id, seq, node_id) VALUES(@wid, @seq, @nid)";
                cmd.Parameters.AddWithValue("@wid", wayId);
                cmd.Parameters.AddWithValue("@seq", seq);
                cmd.Parameters.AddWithValue("@nid", nodeId);
                cmd.ExecuteNonQuery();
            }
        }

        public void InsertWayBounds(long wayId, double minLat, double maxLat,
            double minLon, double maxLon, SqliteTransaction tx)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT OR IGNORE INTO way_bounds(way_id, min_lat, max_lat, min_lon, max_lon) 
                                    VALUES(@wid, @minLat, @maxLat, @minLon, @maxLon)";
                cmd.Parameters.AddWithValue("@wid", wayId);
                cmd.Parameters.AddWithValue("@minLat", minLat);
                cmd.Parameters.AddWithValue("@maxLat", maxLat);
                cmd.Parameters.AddWithValue("@minLon", minLon);
                cmd.Parameters.AddWithValue("@maxLon", maxLon);
                cmd.ExecuteNonQuery();
            }
        }

        public void SetMetadata(string key, string value)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "INSERT OR REPLACE INTO metadata(key, value) VALUES(@k, @v)";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@v", value);
                cmd.ExecuteNonQuery();
            }
        }

        public string GetMetadata(string key)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM metadata WHERE key=@k";
                cmd.Parameters.AddWithValue("@k", key);
                return cmd.ExecuteScalar() as string;
            }
        }

        public bool HasData()
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM ways";
                try
                {
                    long count = (long)cmd.ExecuteScalar();
                    return count > 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Gets all ways whose bounding box intersects the given viewport.
        /// Returns (wayId, type, subtype).
        /// </summary>
        public List<(long id, int type, string subType)> QueryWaysInBounds(
            double minLat, double maxLat, double minLon, double maxLon, int typeFilter = -1)
        {
            var result = new List<(long, int, string)>();

            using (var cmd = _connection.CreateCommand())
            {
                string sql = @"SELECT w.id, w.type, w.subtype 
                               FROM ways w
                               INNER JOIN way_bounds b ON w.id = b.way_id
                               WHERE b.max_lat >= @minLat AND b.min_lat <= @maxLat
                                 AND b.max_lon >= @minLon AND b.min_lon <= @maxLon";

                if (typeFilter >= 0)
                    sql += " AND w.type = @typeFilter";

                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@minLat", minLat);
                cmd.Parameters.AddWithValue("@maxLat", maxLat);
                cmd.Parameters.AddWithValue("@minLon", minLon);
                cmd.Parameters.AddWithValue("@maxLon", maxLon);
                if (typeFilter >= 0)
                    cmd.Parameters.AddWithValue("@typeFilter", typeFilter);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add((reader.GetInt64(0), reader.GetInt32(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2)));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the node coordinates for a given way, ordered by sequence.
        /// </summary>
        public List<(double lat, double lon)> GetWayGeometry(long wayId)
        {
            var result = new List<(double, double)>();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT n.lat, n.lon 
                                    FROM way_nodes wn 
                                    INNER JOIN nodes n ON wn.node_id = n.id 
                                    WHERE wn.way_id = @wid 
                                    ORDER BY wn.seq";
                cmd.Parameters.AddWithValue("@wid", wayId);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add((reader.GetDouble(0), reader.GetDouble(1)));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the overall bounding box of all data in the database.
        /// </summary>
        public (double minLat, double maxLat, double minLon, double maxLon)? GetBounds()
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT MIN(min_lat), MAX(max_lat), MIN(min_lon), MAX(max_lon) 
                                    FROM way_bounds";
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read() && !reader.IsDBNull(0))
                    {
                        return (reader.GetDouble(0), reader.GetDouble(1),
                                reader.GetDouble(2), reader.GetDouble(3));
                    }
                }
            }
            return null;
        }

        private void Execute(string sql)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }
}
