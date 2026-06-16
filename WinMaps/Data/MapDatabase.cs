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

        // Prepared statement for import
        private SqliteCommand _insertWayCmd;

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
                Execute("PRAGMA synchronous=OFF");
                Execute("PRAGMA cache_size=-32000"); // 32MB cache
                Execute("PRAGMA temp_store=MEMORY");
                Execute("PRAGMA page_size=4096");
                Execute("PRAGMA mmap_size=67108864"); // 64MB mmap
            });
        }

        public void CreateSchema()
        {
            Execute(@"
                CREATE TABLE IF NOT EXISTS ways (
                    id INTEGER PRIMARY KEY,
                    type INTEGER NOT NULL,
                    subtype TEXT,
                    geometry BLOB NOT NULL,
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
        }

        public void CreateSpatialIndex()
        {
            Execute("CREATE INDEX IF NOT EXISTS idx_ways_bounds ON ways(min_lat, max_lat, min_lon, max_lon)");
        }

        public SqliteTransaction BeginTransaction()
        {
            return _connection.BeginTransaction();
        }

        /// <summary>
        /// Prepares the INSERT statement for reuse across many rows.
        /// Call once before starting batch inserts, and DisposeInsertStatement() when done.
        /// </summary>
        public void PrepareInsertStatement()
        {
            _insertWayCmd = _connection.CreateCommand();
            _insertWayCmd.CommandText = @"INSERT OR IGNORE INTO ways(id, type, subtype, geometry, min_lat, max_lat, min_lon, max_lon) 
                                          VALUES(@id, @type, @sub, @geo, @minLat, @maxLat, @minLon, @maxLon)";
            _insertWayCmd.Parameters.Add(new SqliteParameter("@id", SqliteType.Integer));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@type", SqliteType.Integer));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@sub", SqliteType.Text));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@geo", SqliteType.Blob));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@minLat", SqliteType.Real));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@maxLat", SqliteType.Real));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@minLon", SqliteType.Real));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@maxLon", SqliteType.Real));
            _insertWayCmd.Prepare();
        }

        public void DisposeInsertStatement()
        {
            _insertWayCmd?.Dispose();
            _insertWayCmd = null;
        }

        public void InsertWay(long id, int type, string subType, byte[] geometry,
            double minLat, double maxLat, double minLon, double maxLon, SqliteTransaction tx)
        {
            _insertWayCmd.Transaction = tx;
            _insertWayCmd.Parameters["@id"].Value = id;
            _insertWayCmd.Parameters["@type"].Value = type;
            _insertWayCmd.Parameters["@sub"].Value = (object)subType ?? DBNull.Value;
            _insertWayCmd.Parameters["@geo"].Value = geometry;
            _insertWayCmd.Parameters["@minLat"].Value = minLat;
            _insertWayCmd.Parameters["@maxLat"].Value = maxLat;
            _insertWayCmd.Parameters["@minLon"].Value = minLon;
            _insertWayCmd.Parameters["@maxLon"].Value = maxLon;
            _insertWayCmd.ExecuteNonQuery();
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
        /// </summary>
        public List<(long id, int type, string subType)> QueryWaysInBounds(
            double minLat, double maxLat, double minLon, double maxLon, int typeFilter = -1)
        {
            var result = new List<(long, int, string)>();

            using (var cmd = _connection.CreateCommand())
            {
                string sql = @"SELECT id, type, subtype 
                               FROM ways
                               WHERE max_lat >= @minLat AND min_lat <= @maxLat
                                 AND max_lon >= @minLon AND min_lon <= @maxLon";

                if (typeFilter >= 0)
                    sql += " AND type = @typeFilter";

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
        /// Gets all ways with their geometry in a single query (avoids per-way round trips).
        /// </summary>
        public List<(int type, string subType, List<(double lat, double lon)> points)> QueryWaysWithGeometry(
            double minLat, double maxLat, double minLon, double maxLon, int typeFilter = -1)
        {
            var result = new List<(int, string, List<(double, double)>)>();

            using (var cmd = _connection.CreateCommand())
            {
                string sql = @"SELECT type, subtype, geometry 
                               FROM ways
                               WHERE max_lat >= @minLat AND min_lat <= @maxLat
                                 AND max_lon >= @minLon AND min_lon <= @maxLon";

                if (typeFilter >= 0)
                    sql += " AND type = @typeFilter";

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
                        int type = reader.GetInt32(0);
                        string subType = reader.IsDBNull(1) ? null : reader.GetString(1);
                        byte[] blob = (byte[])reader["geometry"];
                        var points = new List<(double, double)>();
                        DecodeGeometry(blob, points);
                        result.Add((type, subType, points));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the node coordinates for a given way from its packed geometry blob.
        /// </summary>
        public List<(double lat, double lon)> GetWayGeometry(long wayId)
        {
            var result = new List<(double, double)>();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT geometry FROM ways WHERE id = @wid";
                cmd.Parameters.AddWithValue("@wid", wayId);

                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read() && !reader.IsDBNull(0))
                    {
                        byte[] blob = (byte[])reader["geometry"];
                        DecodeGeometry(blob, result);
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
                                    FROM ways";
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

        // ---- Geometry blob encoding/decoding ----
        // Format: packed little-endian doubles [lat0, lon0, lat1, lon1, ...]

        public static byte[] EncodeGeometry(List<(double lat, double lon)> points)
        {
            byte[] blob = new byte[points.Count * 16]; // 2 doubles * 8 bytes
            int offset = 0;
            foreach (var (lat, lon) in points)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(lat), 0, blob, offset, 8);
                offset += 8;
                Buffer.BlockCopy(BitConverter.GetBytes(lon), 0, blob, offset, 8);
                offset += 8;
            }
            return blob;
        }

        public static void DecodeGeometry(byte[] blob, List<(double lat, double lon)> output)
        {
            int count = blob.Length / 16;
            output.Capacity = Math.Max(output.Capacity, count);
            for (int i = 0; i < count; i++)
            {
                int off = i * 16;
                double lat = BitConverter.ToDouble(blob, off);
                double lon = BitConverter.ToDouble(blob, off + 8);
                output.Add((lat, lon));
            }
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
            DisposeInsertStatement();
            _connection?.Close();
            _connection?.Dispose();
        }
    }
}
