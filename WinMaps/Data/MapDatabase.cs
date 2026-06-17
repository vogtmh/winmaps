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

        /// <summary>The file path this database was opened from.</summary>
        public string DbPath => _dbPath;

        // Prepared statement for import
        private SqliteCommand _insertWayCmd;
        private SqliteCommand _insertPoiCmd;
        private SqliteCommand _insertPlaceCmd;

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
                Execute("PRAGMA synchronous=NORMAL"); // safe with WAL; OFF risks corruption on OS crash
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
                    geometry_simple BLOB,
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

            Execute(@"
                CREATE TABLE IF NOT EXISTS pois (
                    id INTEGER PRIMARY KEY,
                    type TEXT NOT NULL,
                    subtype TEXT,
                    name TEXT,
                    lat REAL NOT NULL,
                    lon REAL NOT NULL
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS places (
                    id INTEGER PRIMARY KEY,
                    place_type TEXT NOT NULL,
                    name TEXT NOT NULL,
                    lat REAL NOT NULL,
                    lon REAL NOT NULL
                )");

            // Tracks which Geofabrik regions have been imported into this country DB.
            // Deletion is always at the country-file level, so no per-region foreign keys needed.
            Execute(@"
                CREATE TABLE IF NOT EXISTS regions (
                    id          TEXT PRIMARY KEY,
                    name        TEXT NOT NULL,
                    import_date TEXT NOT NULL
                )");
        }

        /// <summary>Returns true if the given Geofabrik region ID is already recorded in this DB.</summary>
        public bool HasRegion(string regionId)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM regions WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", regionId);
                return (long)cmd.ExecuteScalar() > 0;
            }
        }

        /// <summary>Records a successfully-imported region in the regions table.</summary>
        public void InsertRegion(string regionId, string name)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "INSERT OR REPLACE INTO regions(id, name, import_date) VALUES(@id, @name, @date)";
                cmd.Parameters.AddWithValue("@id", regionId);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@date", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Returns all regions recorded in this country DB, sorted by name.</summary>
        public List<(string id, string name)> GetRegions()
        {
            var result = new List<(string, string)>();
            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, name FROM regions ORDER BY name";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            result.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }
            }
            catch { /* regions table may not exist in old DBs */ }
            return result;
        }

        /// <summary>Synchronous open — for use during migration (avoids async deadlocks).</summary>
        public void OpenSync()
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();
            Execute("PRAGMA journal_mode=WAL");
            Execute("PRAGMA synchronous=NORMAL"); // safe with WAL; OFF risks corruption on OS crash
            Execute("PRAGMA cache_size=-32000");
            Execute("PRAGMA temp_store=MEMORY");
            Execute("PRAGMA page_size=4096");
            Execute("PRAGMA mmap_size=67108864");
        }

        /// <summary>
        /// Runs SQLite's quick_check PRAGMA. Returns true if the database is intact.
        /// On a corrupted or partially-written WAL this returns false rather than throwing.
        /// </summary>
        public bool CheckIntegrity()
        {
            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA quick_check";
                    var result = cmd.ExecuteScalar() as string;
                    return result == "ok";
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Checkpoints the WAL and truncates it to zero length.
        /// Call after large write operations (e.g. spatial index creation) so the
        /// next startup doesn't have to replay a multi-hundred-MB WAL file.
        /// </summary>
        public void Checkpoint()
        {
            try { Execute("PRAGMA wal_checkpoint(TRUNCATE)"); }
            catch { }
        }

        public void CreateSpatialIndex()
        {
            Execute("CREATE INDEX IF NOT EXISTS idx_ways_bounds ON ways(min_lat, max_lat, min_lon, max_lon)");
            Execute("CREATE INDEX IF NOT EXISTS idx_ways_type_bounds ON ways(type, min_lat, max_lat, min_lon, max_lon)");
            Execute("CREATE INDEX IF NOT EXISTS idx_pois_coords ON pois(lat, lon)");
            Execute("CREATE INDEX IF NOT EXISTS idx_places_coords ON places(lat, lon)");
        }

        /// <summary>
        /// Drops the spatial indexes so that bulk inserts don't have to maintain them
        /// row-by-row. Call before importing; call CreateSpatialIndex() when done.
        /// </summary>
        public void DropSpatialIndex()
        {
            Execute("DROP INDEX IF EXISTS idx_ways_bounds");
            Execute("DROP INDEX IF EXISTS idx_ways_type_bounds");
            Execute("DROP INDEX IF EXISTS idx_pois_coords");
            Execute("DROP INDEX IF EXISTS idx_places_coords");
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
            _insertWayCmd.CommandText = @"INSERT OR IGNORE INTO ways(id, type, subtype, geometry, geometry_simple, min_lat, max_lat, min_lon, max_lon) 
                                          VALUES(@id, @type, @sub, @geo, @geoSimple, @minLat, @maxLat, @minLon, @maxLon)";
            _insertWayCmd.Parameters.Add(new SqliteParameter("@id", SqliteType.Integer));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@type", SqliteType.Integer));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@sub", SqliteType.Text));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@geo", SqliteType.Blob));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@geoSimple", SqliteType.Blob));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@minLat", SqliteType.Real));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@maxLat", SqliteType.Real));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@minLon", SqliteType.Real));
            _insertWayCmd.Parameters.Add(new SqliteParameter("@maxLon", SqliteType.Real));
            _insertWayCmd.Prepare();

            _insertPoiCmd = _connection.CreateCommand();
            _insertPoiCmd.CommandText = @"INSERT OR IGNORE INTO pois(id, type, subtype, name, lat, lon)
                                          VALUES(@id, @type, @sub, @name, @lat, @lon)";
            _insertPoiCmd.Parameters.Add(new SqliteParameter("@id", SqliteType.Integer));
            _insertPoiCmd.Parameters.Add(new SqliteParameter("@type", SqliteType.Text));
            _insertPoiCmd.Parameters.Add(new SqliteParameter("@sub", SqliteType.Text));
            _insertPoiCmd.Parameters.Add(new SqliteParameter("@name", SqliteType.Text));
            _insertPoiCmd.Parameters.Add(new SqliteParameter("@lat", SqliteType.Real));
            _insertPoiCmd.Parameters.Add(new SqliteParameter("@lon", SqliteType.Real));
            _insertPoiCmd.Prepare();

            _insertPlaceCmd = _connection.CreateCommand();
            _insertPlaceCmd.CommandText = @"INSERT OR IGNORE INTO places(id, place_type, name, lat, lon)
                                            VALUES(@id, @pt, @name, @lat, @lon)";
            _insertPlaceCmd.Parameters.Add(new SqliteParameter("@id", SqliteType.Integer));
            _insertPlaceCmd.Parameters.Add(new SqliteParameter("@pt", SqliteType.Text));
            _insertPlaceCmd.Parameters.Add(new SqliteParameter("@name", SqliteType.Text));
            _insertPlaceCmd.Parameters.Add(new SqliteParameter("@lat", SqliteType.Real));
            _insertPlaceCmd.Parameters.Add(new SqliteParameter("@lon", SqliteType.Real));
            _insertPlaceCmd.Prepare();
        }

        public void DisposeInsertStatement()
        {
            _insertWayCmd?.Dispose();
            _insertWayCmd = null;
            _insertPoiCmd?.Dispose();
            _insertPoiCmd = null;
            _insertPlaceCmd?.Dispose();
            _insertPlaceCmd = null;
        }

        public void InsertWay(long id, int type, string subType, byte[] geometry, byte[] geometrySimple,
            double minLat, double maxLat, double minLon, double maxLon, SqliteTransaction tx)
        {
            _insertWayCmd.Transaction = tx;
            _insertWayCmd.Parameters["@id"].Value = id;
            _insertWayCmd.Parameters["@type"].Value = type;
            _insertWayCmd.Parameters["@sub"].Value = (object)subType ?? DBNull.Value;
            _insertWayCmd.Parameters["@geo"].Value = geometry;
            _insertWayCmd.Parameters["@geoSimple"].Value = (object)geometrySimple ?? DBNull.Value;
            _insertWayCmd.Parameters["@minLat"].Value = minLat;
            _insertWayCmd.Parameters["@maxLat"].Value = maxLat;
            _insertWayCmd.Parameters["@minLon"].Value = minLon;
            _insertWayCmd.Parameters["@maxLon"].Value = maxLon;
            _insertWayCmd.ExecuteNonQuery();
        }

        public void InsertPoi(long id, string type, string subType, string name,
            double lat, double lon, SqliteTransaction tx)
        {
            _insertPoiCmd.Transaction = tx;
            _insertPoiCmd.Parameters["@id"].Value = id;
            _insertPoiCmd.Parameters["@type"].Value = type;
            _insertPoiCmd.Parameters["@sub"].Value = (object)subType ?? DBNull.Value;
            _insertPoiCmd.Parameters["@name"].Value = (object)name ?? DBNull.Value;
            _insertPoiCmd.Parameters["@lat"].Value = lat;
            _insertPoiCmd.Parameters["@lon"].Value = lon;
            _insertPoiCmd.ExecuteNonQuery();
        }

        public void InsertPlace(long id, string placeType, string name,
            double lat, double lon, SqliteTransaction tx)
        {
            _insertPlaceCmd.Transaction = tx;
            _insertPlaceCmd.Parameters["@id"].Value = id;
            _insertPlaceCmd.Parameters["@pt"].Value = placeType;
            _insertPlaceCmd.Parameters["@name"].Value = name;
            _insertPlaceCmd.Parameters["@lat"].Value = lat;
            _insertPlaceCmd.Parameters["@lon"].Value = lon;
            _insertPlaceCmd.ExecuteNonQuery();
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
        /// Returns (id, type, subtype, latSpan, lonSpan).
        /// </summary>
        public List<(long id, int type, string subType, double latSpan, double lonSpan)> QueryWaysInBounds(
            double minLat, double maxLat, double minLon, double maxLon, int typeFilter = -1)
        {
            var result = new List<(long, int, string, double, double)>();

            using (var cmd = _connection.CreateCommand())
            {
                string sql = @"SELECT id, type, subtype, (max_lat - min_lat), (max_lon - min_lon)
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
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.GetDouble(3), reader.GetDouble(4)));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Queries ways with LOD filtering pushed into SQL.
        /// Returns geometry directly — no second round trip needed.
        /// </summary>
        public List<(int type, string subType, List<(double lat, double lon)> points)> QueryWaysForZoom(
            double minLat, double maxLat, double minLon, double maxLon, double zoom)
        {
            var result = new List<(int, string, List<(double, double)>)>();

            // Build SQL with LOD conditions pushed into WHERE clause
            // Road=0, Water=1, Park=2
            // Area size uses (max-min) extent in degrees as a proxy for screen-space size
            // For Z<14, use COALESCE(geometry_simple, geometry) to read the smaller pre-simplified blob
            string geoCol = zoom < 14 ? "COALESCE(geometry_simple, geometry)" : "geometry";
            string sql;
            if (zoom < 4)
            {
                // Continental view: motorway only + huge areas
                sql = $@"SELECT type, subtype, {geoCol} FROM ways
                        WHERE max_lat >= @minLat AND min_lat <= @maxLat
                          AND max_lon >= @minLon AND min_lon <= @maxLon
                          AND (
                            (type = 0 AND subtype IN ('motorway'))
                            OR ((type = 1 OR type = 2) AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.5)
                          )";
            }
            else if (zoom < 6)
            {
                // Country view: motorway/trunk + very large areas
                sql = $@"SELECT type, subtype, {geoCol} FROM ways
                        WHERE max_lat >= @minLat AND min_lat <= @maxLat
                          AND max_lon >= @minLon AND min_lon <= @maxLon
                          AND (
                            (type = 0 AND subtype IN ('motorway','trunk'))
                            OR ((type = 1 OR type = 2) AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.05)
                          )";
            }
            else if (zoom < 8)
            {
                // Regional view: motorway/trunk + large areas
                sql = $@"SELECT type, subtype, {geoCol} FROM ways
                        WHERE max_lat >= @minLat AND min_lat <= @maxLat
                          AND max_lon >= @minLon AND min_lon <= @maxLon
                          AND (
                            (type = 0 AND subtype IN ('motorway','trunk','motorway_link','trunk_link'))
                            OR ((type = 1 OR type = 2) AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.005)
                          )";
            }
            else if (zoom < 10)
            {
                // Add primary roads + large areas + village-scale buildings
                sql = $@"SELECT type, subtype, {geoCol} FROM ways
                        WHERE max_lat >= @minLat AND min_lat <= @maxLat
                          AND max_lon >= @minLon AND min_lon <= @maxLon
                          AND (
                            (type = 0 AND subtype IN ('motorway','trunk','primary','motorway_link','trunk_link','primary_link'))
                            OR ((type = 1 OR type = 2) AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.0002)
                            OR (type = 3 AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.00005)
                          )";
            }
            else if (zoom < 12)
            {
                // Add secondary roads, skip minor paths/tracks/service; medium+ areas + buildings
                sql = $@"SELECT type, subtype, {geoCol} FROM ways
                        WHERE max_lat >= @minLat AND min_lat <= @maxLat
                          AND max_lon >= @minLon AND min_lon <= @maxLon
                          AND (
                            (type = 0 AND subtype IN ('motorway','trunk','primary','secondary',
                                'motorway_link','trunk_link','primary_link','secondary_link'))
                            OR ((type = 1 OR type = 2) AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.00005)
                            OR (type = 3 AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.00005)
                          )";
            }
            else if (zoom < 13)
            {
                // Add tertiary/residential, track only (no footway/path/cycleway); smaller areas + buildings
                sql = $@"SELECT type, subtype, {geoCol} FROM ways
                        WHERE max_lat >= @minLat AND min_lat <= @maxLat
                          AND max_lon >= @minLon AND min_lon <= @maxLon
                          AND (
                            (type = 0 AND subtype NOT IN ('footway','path','cycleway','service'))
                            OR ((type = 1 OR type = 2) AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.00001)
                            OR (type = 3 AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.00001)
                          )";
            }
            else if (zoom < 14)
            {
                // All roads except footway/path/cycleway; small areas + buildings
                sql = $@"SELECT type, subtype, {geoCol} FROM ways
                        WHERE max_lat >= @minLat AND min_lat <= @maxLat
                          AND max_lon >= @minLon AND min_lon <= @maxLon
                          AND (
                            (type = 0 AND subtype NOT IN ('footway','path','cycleway'))
                            OR ((type = 1 OR type = 2) AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.000001)
                            OR (type = 3 AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.000001)
                          )";
            }
            else if (zoom < 16)
            {
                // Z14-15: skip tiny buildings and very small areas, exclude service roads
                sql = $@"SELECT type, subtype, {geoCol} FROM ways
                        WHERE max_lat >= @minLat AND min_lat <= @maxLat
                          AND max_lon >= @minLon AND min_lon <= @maxLon
                          AND (
                            (type = 0 AND subtype NOT IN ('service'))
                            OR (type = 1 OR type = 2)
                            OR (type = 3 AND (max_lat - min_lat) * (max_lon - min_lon) >= 0.0000001)
                          )";
            }
            else
            {
                // Z16+: Show everything
                sql = @"SELECT type, subtype, geometry FROM ways
                        WHERE max_lat >= @minLat AND min_lat <= @maxLat
                          AND max_lon >= @minLon AND min_lon <= @maxLon";
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@minLat", minLat);
                cmd.Parameters.AddWithValue("@maxLat", maxLat);
                cmd.Parameters.AddWithValue("@minLon", minLon);
                cmd.Parameters.AddWithValue("@maxLon", maxLon);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int type = reader.GetInt32(0);
                        string subType = reader.IsDBNull(1) ? null : reader.GetString(1);
                        byte[] blob = (byte[])reader["geometry"];
                        var points = new List<(double, double)>();
                        DecodeGeometry(blob, points);
                        if (points.Count >= 2)
                            result.Add((type, subType, points));
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
        /// Batch-fetches geometry for a list of way IDs. Returns a dictionary keyed by way ID.
        /// </summary>
        public Dictionary<long, List<(double lat, double lon)>> GetWayGeometryBatch(List<long> ids)
        {
            var result = new Dictionary<long, List<(double, double)>>(ids.Count);
            if (ids.Count == 0) return result;

            // Process in chunks to avoid huge SQL statements
            const int chunkSize = 500;
            for (int start = 0; start < ids.Count; start += chunkSize)
            {
                int count = Math.Min(chunkSize, ids.Count - start);

                using (var cmd = _connection.CreateCommand())
                {
                    var paramNames = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        paramNames[i] = "@id" + i;
                        cmd.Parameters.AddWithValue(paramNames[i], ids[start + i]);
                    }
                    cmd.CommandText = "SELECT id, geometry FROM ways WHERE id IN (" +
                        string.Join(",", paramNames) + ")";

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            long id = reader.GetInt64(0);
                            byte[] blob = (byte[])reader["geometry"];
                            var points = new List<(double, double)>();
                            DecodeGeometry(blob, points);
                            result[id] = points;
                        }
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

        /// <summary>
        /// Queries POIs within the given bounding box.
        /// </summary>
        public List<(string type, string subType, string name, double lat, double lon)> QueryPois(
            double minLat, double maxLat, double minLon, double maxLon)
        {
            var result = new List<(string, string, string, double, double)>();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT type, subtype, name, lat, lon FROM pois
                                    WHERE lat >= @minLat AND lat <= @maxLat
                                      AND lon >= @minLon AND lon <= @maxLon";
                cmd.Parameters.AddWithValue("@minLat", minLat);
                cmd.Parameters.AddWithValue("@maxLat", maxLat);
                cmd.Parameters.AddWithValue("@minLon", minLon);
                cmd.Parameters.AddWithValue("@maxLon", maxLon);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add((
                            reader.GetString(0),
                            reader.IsDBNull(1) ? null : reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.GetDouble(3),
                            reader.GetDouble(4)));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Queries places within the given bounding box, filtered by zoom level.
        /// </summary>
        public List<(string placeType, string name, double lat, double lon)> QueryPlaces(
            double minLat, double maxLat, double minLon, double maxLon, double zoom)
        {
            var result = new List<(string, string, double, double)>();

            string typeFilter;
            if (zoom < 9)
                typeFilter = "('city')";
            else if (zoom < 12)
                typeFilter = "('city','town')";
            else if (zoom < 14)
                typeFilter = "('city','town','village','suburb')";
            else
                typeFilter = "('city','town','village','suburb','hamlet')";

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT place_type, name, lat, lon FROM places" +
                                  " WHERE lat >= @minLat AND lat <= @maxLat" +
                                  "   AND lon >= @minLon AND lon <= @maxLon" +
                                  "   AND place_type IN " + typeFilter;
                cmd.Parameters.AddWithValue("@minLat", minLat);
                cmd.Parameters.AddWithValue("@maxLat", maxLat);
                cmd.Parameters.AddWithValue("@minLon", minLon);
                cmd.Parameters.AddWithValue("@maxLon", maxLon);

                try
                {
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add((
                                reader.GetString(0),
                                reader.GetString(1),
                                reader.GetDouble(2),
                                reader.GetDouble(3)));
                        }
                    }
                }
                catch { /* places table may not exist in old DBs */ }
            }

            return result;
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

        /// <summary>
        /// Simplifies a point list by dropping points closer than minDist (in degrees).
        /// Always keeps first and last point. Returns null if no simplification possible.
        /// </summary>
        public static byte[] SimplifyGeometry(List<(double lat, double lon)> points, double minDist)
        {
            if (points.Count <= 4) return null; // already simple enough

            double minDistSq = minDist * minDist;
            var simplified = new List<(double lat, double lon)>(points.Count / 2);
            simplified.Add(points[0]);
            double lastLat = points[0].lat, lastLon = points[0].lon;

            for (int i = 1; i < points.Count - 1; i++)
            {
                double dLat = points[i].lat - lastLat;
                double dLon = points[i].lon - lastLon;
                if (dLat * dLat + dLon * dLon >= minDistSq)
                {
                    simplified.Add(points[i]);
                    lastLat = points[i].lat;
                    lastLon = points[i].lon;
                }
            }
            simplified.Add(points[points.Count - 1]);

            // Only store if we actually reduced the point count meaningfully
            if (simplified.Count >= points.Count - 2) return null;
            return EncodeGeometry(simplified);
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
