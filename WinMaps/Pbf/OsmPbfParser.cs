using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinMaps.Pbf
{
    internal enum OsmElementType
    {
        Road = 0,
        Water = 1,
        Park = 2,
        Building = 3
    }

    internal struct OsmNode
    {
        public long Id;
        public double Lat;
        public double Lon;
    }

    internal struct OsmWay
    {
        public long Id;
        public OsmElementType Type;
        public string SubType; // highway=motorway, waterway=river, etc.
        public long[] NodeRefs;
    }

    internal struct OsmPoi
    {
        public long Id;
        public string Type;    // amenity, shop, tourism, etc.
        public string SubType; // restaurant, supermarket, hotel, etc.
        public string Name;
        public double Lat;
        public double Lon;
    }

    internal class OsmPbfParser
    {
        public event Action<OsmNode> OnNode;
        public event Action<OsmWay> OnWay;
        public event Action<OsmPoi> OnPoi;
        public event Action<long, long> OnProgress; // bytesRead, totalBytes

        private static readonly HashSet<string> GenericPoiSubTypes = new HashSet<string>
        {
            "information", "post_box", "recycling", "bench", "telephone",
            "vending_machine", "waste_basket", "bicycle_parking", "waste_disposal",
            "letter_box", "drinking_water", "fire_hydrant", "bollard",
            "surveillance", "street_lamp", "clock", "manhole"
        };

        private static readonly HashSet<string> PoiKeys = new HashSet<string>
        {
            "amenity", "shop", "tourism", "healthcare", "office"
        };

        private static readonly HashSet<string> RoadKeys = new HashSet<string>
        {
            "motorway", "trunk", "primary", "secondary", "tertiary",
            "motorway_link", "trunk_link", "primary_link", "secondary_link", "tertiary_link",
            "residential", "unclassified", "service", "living_street", "pedestrian",
            "track", "footway", "cycleway", "path"
        };

        public async Task ParseAsync(Stream stream, long totalLength, CancellationToken ct)
        {
            await Task.Run(() => Parse(stream, totalLength, ct), ct);
        }

        public void Parse(Stream stream, long totalLength, CancellationToken ct)
        {
            var reader = new PbfReader(stream);
            long lastProgressReport = 0;

            while (reader.CanRead)
            {
                ct.ThrowIfCancellationRequested();

                // Report progress every ~1MB
                long pos = stream.Position;
                if (pos - lastProgressReport > 1024 * 1024)
                {
                    OnProgress?.Invoke(pos, totalLength);
                    lastProgressReport = pos;
                }

                // Read BlobHeader + Blob (catch truncated file at any point)
                try
                {
                    int headerSize = reader.ReadInt32BigEndian();
                    byte[] headerData = reader.ReadBytes(headerSize);
                    ParseBlobHeader(headerData, out string type, out int dataSize);

                    byte[] blobData = reader.ReadBytes(dataSize);
                    byte[] uncompressed = DecompressBlob(blobData);

                    if (type == "OSMData")
                    {
                        ParsePrimitiveBlock(uncompressed, ct);
                    }
                }
                catch (EndOfStreamException)
                {
                    break; // End of file or truncated data
                }
            }

            // Final progress
            OnProgress?.Invoke(totalLength, totalLength);
        }

        private void ParseBlobHeader(byte[] data, out string type, out int dataSize)
        {
            type = null;
            dataSize = 0;

            using (var ms = new MemoryStream(data))
            {
                var reader = new PbfReader(ms);
                while (ms.Position < ms.Length)
                {
                    var (field, wire) = reader.ReadTag();
                    switch (field)
                    {
                        case 1: // type
                            int len = (int)reader.ReadVarInt();
                            type = Encoding.UTF8.GetString(reader.ReadBytes(len));
                            break;
                        case 3: // datasize
                            dataSize = (int)reader.ReadVarInt();
                            break;
                        default:
                            reader.SkipField(wire);
                            break;
                    }
                }
            }
        }

        private byte[] DecompressBlob(byte[] blobData)
        {
            byte[] raw = null;
            byte[] zlibData = null;
            int rawSize = 0;

            using (var ms = new MemoryStream(blobData))
            {
                var reader = new PbfReader(ms);
                while (ms.Position < ms.Length)
                {
                    var (field, wire) = reader.ReadTag();
                    switch (field)
                    {
                        case 1: // raw
                            int len = (int)reader.ReadVarInt();
                            raw = reader.ReadBytes(len);
                            break;
                        case 2: // raw_size
                            rawSize = (int)reader.ReadVarInt();
                            break;
                        case 3: // zlib_data
                            int zLen = (int)reader.ReadVarInt();
                            zlibData = reader.ReadBytes(zLen);
                            break;
                        default:
                            reader.SkipField(wire);
                            break;
                    }
                }
            }

            if (raw != null)
                return raw;

            if (zlibData != null)
            {
                byte[] result = new byte[rawSize];
                // zlib format: skip 2-byte header
                using (var compressed = new MemoryStream(zlibData, 2, zlibData.Length - 2))
                using (var deflate = new DeflateStream(compressed, CompressionMode.Decompress))
                {
                    int offset = 0;
                    while (offset < rawSize)
                    {
                        int read = deflate.Read(result, offset, rawSize - offset);
                        if (read <= 0) break;
                        offset += read;
                    }
                }
                return result;
            }

            throw new InvalidDataException("Blob contains no data");
        }

        private void ParsePrimitiveBlock(byte[] data, CancellationToken ct)
        {
            List<string> stringTable = null;
            int granularity = 100;
            long latOffset = 0;
            long lonOffset = 0;

            // First pass: extract string table and granularity
            var blockSegments = new List<(int field, int offset, int length)>();

            using (var ms = new MemoryStream(data))
            {
                var reader = new PbfReader(ms);
                while (ms.Position < ms.Length)
                {
                    var (field, wire) = reader.ReadTag();
                    switch (field)
                    {
                        case 1: // stringtable
                            int stLen = (int)reader.ReadVarInt();
                            int stStart = (int)ms.Position;
                            stringTable = ParseStringTable(data, stStart, stLen);
                            ms.Position = stStart + stLen;
                            break;
                        case 2: // primitivegroup
                            int pgLen = (int)reader.ReadVarInt();
                            blockSegments.Add((2, (int)ms.Position, pgLen));
                            ms.Position += pgLen;
                            break;
                        case 17: // granularity
                            granularity = (int)reader.ReadVarInt();
                            break;
                        case 19: // lat_offset
                            latOffset = (long)reader.ReadVarInt();
                            break;
                        case 20: // lon_offset
                            lonOffset = (long)reader.ReadVarInt();
                            break;
                        case 18: // date_granularity
                            reader.ReadVarInt();
                            break;
                        default:
                            reader.SkipField(wire);
                            break;
                    }
                }
            }

            if (stringTable == null)
                return;

            // Process primitive groups
            foreach (var seg in blockSegments)
            {
                ct.ThrowIfCancellationRequested();
                ParsePrimitiveGroup(data, seg.offset, seg.length, stringTable, granularity, latOffset, lonOffset);
            }
        }

        private List<string> ParseStringTable(byte[] data, int offset, int length)
        {
            var strings = new List<string>();
            int end = offset + length;

            using (var ms = new MemoryStream(data, offset, length))
            {
                var reader = new PbfReader(ms);
                while (ms.Position < ms.Length)
                {
                    var (field, wire) = reader.ReadTag();
                    if (field == 1 && wire == 2)
                    {
                        int len = (int)reader.ReadVarInt();
                        if (len == 0)
                        {
                            strings.Add("");
                        }
                        else
                        {
                            byte[] strBytes = reader.ReadBytes(len);
                            strings.Add(Encoding.UTF8.GetString(strBytes));
                        }
                    }
                    else
                    {
                        reader.SkipField(wire);
                    }
                }
            }

            return strings;
        }

        private void ParsePrimitiveGroup(byte[] data, int offset, int length,
            List<string> strings, int granularity, long latOffset, long lonOffset)
        {
            using (var ms = new MemoryStream(data, offset, length))
            {
                var reader = new PbfReader(ms);
                while (ms.Position < ms.Length)
                {
                    var (field, wire) = reader.ReadTag();
                    switch (field)
                    {
                        case 1: // nodes (non-dense)
                            int nLen = (int)reader.ReadVarInt();
                            ms.Position += nLen;
                            break;
                        case 2: // dense nodes
                            int dLen = (int)reader.ReadVarInt();
                            if (OnNode != null)
                            {
                                int dStart = offset + (int)ms.Position;
                                ParseDenseNodes(data, dStart, dLen, strings, granularity, latOffset, lonOffset);
                            }
                            ms.Position += dLen;
                            break;
                        case 3: // ways
                            int wLen = (int)reader.ReadVarInt();
                            int wStart = offset + (int)ms.Position;
                            ParseWay(data, wStart, wLen, strings);
                            ms.Position += wLen;
                            break;
                        case 4: // relations
                            int rLen = (int)reader.ReadVarInt();
                            ms.Position += rLen;
                            break;
                        default:
                            reader.SkipField(wire);
                            break;
                    }
                }
            }
        }

        private void ParseDenseNodes(byte[] blockData, int pgOffset, int pgLength,
            List<string> strings, int granularity, long latOffset, long lonOffset)
        {
            // We need to re-parse from the primitive group level to find DenseNodes fields
            // DenseNodes fields: 1=id(packed), 8=lat(packed), 9=lon(packed), 10=keys_vals(packed)
            byte[] idData = null; int idOff = 0, idLen = 0;
            byte[] latData = null; int latOff = 0, latLen = 0;
            byte[] lonData = null; int lonOff = 0, lonLen = 0;
            byte[] kvData = null; int kvOff = 0, kvLen = 0;

            using (var ms = new MemoryStream(blockData, pgOffset, pgLength))
            {
                var reader = new PbfReader(ms);
                while (ms.Position < ms.Length)
                {
                    var (field, wire) = reader.ReadTag();
                    switch (field)
                    {
                        case 1: // id - packed sint64
                            idLen = (int)reader.ReadVarInt();
                            idOff = pgOffset + (int)ms.Position;
                            idData = blockData;
                            ms.Position += idLen;
                            break;
                        case 8: // lat - packed sint64
                            latLen = (int)reader.ReadVarInt();
                            latOff = pgOffset + (int)ms.Position;
                            latData = blockData;
                            ms.Position += latLen;
                            break;
                        case 9: // lon - packed sint64
                            lonLen = (int)reader.ReadVarInt();
                            lonOff = pgOffset + (int)ms.Position;
                            lonData = blockData;
                            ms.Position += lonLen;
                            break;
                        case 10: // keys_vals - packed int32
                            kvLen = (int)reader.ReadVarInt();
                            kvOff = pgOffset + (int)ms.Position;
                            kvData = blockData;
                            ms.Position += kvLen;
                            break;
                        default:
                            reader.SkipField(wire);
                            break;
                    }
                }
            }

            if (idData == null || latData == null || lonData == null)
                return;

            // Decode parallel arrays (all delta-encoded)
            var ids = new List<long>();
            PbfReader.ReadPackedSignedVarInts(idData, idOff, idLen, v => ids.Add(v));

            var lats = new List<long>();
            PbfReader.ReadPackedSignedVarInts(latData, latOff, latLen, v => lats.Add(v));

            var lons = new List<long>();
            PbfReader.ReadPackedSignedVarInts(lonData, lonOff, lonLen, v => lons.Add(v));

            long runId = 0, runLat = 0, runLon = 0;
            int count = Math.Min(ids.Count, Math.Min(lats.Count, lons.Count));

            // Decode keys_vals for POI detection
            // Format: interleaved [key_idx, val_idx, key_idx, val_idx, ..., 0] per node
            // A 0 separates nodes
            var kvValues = new List<long>();
            if (kvData != null && OnPoi != null)
            {
                PbfReader.ReadPackedVarInts(kvData, kvOff, kvLen, v => kvValues.Add((long)v));
            }
            int kvPos = 0;

            for (int i = 0; i < count; i++)
            {
                runId += ids[i];
                runLat += lats[i];
                runLon += lons[i];

                double lat = 0.000000001 * (latOffset + (granularity * runLat));
                double lon = 0.000000001 * (lonOffset + (granularity * runLon));

                OnNode?.Invoke(new OsmNode { Id = runId, Lat = lat, Lon = lon });

                // Check tags for POI classification
                if (kvValues.Count > 0 && OnPoi != null)
                {
                    string poiType = null, poiSubType = null, poiName = null;

                    while (kvPos < kvValues.Count && kvValues[kvPos] != 0)
                    {
                        if (kvPos + 1 >= kvValues.Count) break;
                        int keyIdx = (int)kvValues[kvPos];
                        int valIdx = (int)kvValues[kvPos + 1];
                        kvPos += 2;

                        string key = (keyIdx < strings.Count) ? strings[keyIdx] : "";
                        string val = (valIdx < strings.Count) ? strings[valIdx] : "";

                        if (PoiKeys.Contains(key))
                        {
                            poiType = key;
                            poiSubType = val;
                        }
                        else if (key == "name")
                        {
                            poiName = val;
                        }
                    }
                    // Skip the 0 separator
                    if (kvPos < kvValues.Count) kvPos++;

                    if (poiType != null)
                    {
                        // Skip generic subtypes that carry no useful information without a name
                        if (string.IsNullOrEmpty(poiName) && GenericPoiSubTypes.Contains(poiSubType))
                            goto skipPoi;

                        OnPoi.Invoke(new OsmPoi
                        {
                            Id = runId,
                            Type = poiType,
                            SubType = poiSubType,
                            Name = poiName,
                            Lat = lat,
                            Lon = lon
                        });
                    }
                    skipPoi:;
                }
            }
        }

        private void ParseWay(byte[] blockData, int offset, int length, List<string> strings)
        {
            long id = 0;
            var keys = new List<uint>();
            var vals = new List<uint>();
            byte[] refsData = null; int refsOff = 0, refsLen = 0;

            using (var ms = new MemoryStream(blockData, offset, length))
            {
                var reader = new PbfReader(ms);
                while (ms.Position < ms.Length)
                {
                    var (field, wire) = reader.ReadTag();
                    switch (field)
                    {
                        case 1: // id
                            id = (long)reader.ReadVarInt();
                            break;
                        case 2: // keys (packed uint32)
                            int kLen = (int)reader.ReadVarInt();
                            int kStart = offset + (int)ms.Position;
                            PbfReader.ReadPackedVarInts(blockData, kStart, kLen, v => keys.Add((uint)v));
                            ms.Position += kLen;
                            break;
                        case 3: // vals (packed uint32)
                            int vLen = (int)reader.ReadVarInt();
                            int vStart = offset + (int)ms.Position;
                            PbfReader.ReadPackedVarInts(blockData, vStart, vLen, v => vals.Add((uint)v));
                            ms.Position += vLen;
                            break;
                        case 4: // info
                            int iLen = (int)reader.ReadVarInt();
                            ms.Position += iLen;
                            break;
                        case 8: // refs (packed sint64, delta-coded)
                            refsLen = (int)reader.ReadVarInt();
                            refsOff = offset + (int)ms.Position;
                            refsData = blockData;
                            ms.Position += refsLen;
                            break;
                        default:
                            reader.SkipField(wire);
                            break;
                    }
                }
            }

            // Classify the way by its tags
            OsmElementType? wayType = null;
            string subType = null;
            bool hasBuilding = false;
            string buildingName = null;
            string buildingHint = null;

            int tagCount = Math.Min(keys.Count, vals.Count);
            for (int i = 0; i < tagCount; i++)
            {
                string key = (keys[i] < strings.Count) ? strings[(int)keys[i]] : "";
                string val = (vals[i] < strings.Count) ? strings[(int)vals[i]] : "";

                if (key == "highway" && RoadKeys.Contains(val))
                {
                    wayType = OsmElementType.Road;
                    subType = val;
                    break;
                }
                else if (key == "waterway")
                {
                    wayType = OsmElementType.Water;
                    subType = val;
                    break;
                }
                else if (key == "natural" && (val == "water" || val == "wood" || val == "scrub"))
                {
                    wayType = (val == "water") ? OsmElementType.Water : OsmElementType.Park;
                    subType = val;
                    break;
                }
                else if (key == "water")
                {
                    wayType = OsmElementType.Water;
                    subType = val;
                    break;
                }
                else if (key == "leisure" && (val == "park" || val == "garden" || val == "nature_reserve"))
                {
                    wayType = OsmElementType.Park;
                    subType = val;
                    break;
                }
                else if (key == "landuse" && (val == "forest" || val == "grass" || val == "meadow" ||
                         val == "farmland" || val == "orchard" || val == "vineyard" ||
                         val == "recreation_ground" || val == "residential" ||
                         val == "industrial" || val == "commercial" || val == "retail"))
                {
                    wayType = OsmElementType.Park;
                    subType = val;
                    break;
                }
                else if (key == "building")
                    hasBuilding = true;
                else if (key == "name")
                    buildingName = val;
                else if ((key == "amenity" || key == "shop" || key == "tourism" || key == "office") && buildingHint == null)
                    buildingHint = val;
            }

            // Buildings: only if no higher-priority type matched
            if (wayType == null && hasBuilding)
            {
                wayType = OsmElementType.Building;
                // Encode as "name\x1fhint" to match Go tool format
                subType = (buildingName ?? "") + "\x1f" + (buildingHint ?? "");
            }

            if (wayType == null || refsData == null)
                return;

            // Decode delta-encoded node refs
            var nodeRefs = new List<long>();
            PbfReader.ReadPackedSignedVarInts(refsData, refsOff, refsLen, v => nodeRefs.Add(v));

            long[] refs = new long[nodeRefs.Count];
            long runRef = 0;
            for (int i = 0; i < nodeRefs.Count; i++)
            {
                runRef += nodeRefs[i];
                refs[i] = runRef;
            }

            OnWay?.Invoke(new OsmWay
            {
                Id = id,
                Type = wayType.Value,
                SubType = subType,
                NodeRefs = refs
            });
        }
    }
}
