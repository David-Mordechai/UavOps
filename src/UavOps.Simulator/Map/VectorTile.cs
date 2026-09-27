using System.Text;

namespace UavOps.Simulator.Map;

public enum GeometryType { Unknown = 0, Point = 1, LineString = 2, Polygon = 3 }

/// <summary>One map feature: its type, its string/number properties, and its geometry as rings
/// (polygons) or lines, in tile coordinates 0..<see cref="VectorTileLayer.Extent"/>.</summary>
public sealed record VectorTileFeature(GeometryType Type, IReadOnlyDictionary<string, object> Properties, List<List<(int X, int Y)>> Parts)
{
    public string? Get(string key) => Properties.TryGetValue(key, out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    public double GetNumber(string key, double fallback) =>
        Properties.TryGetValue(key, out var v) && v is IConvertible c ? c.ToDouble(System.Globalization.CultureInfo.InvariantCulture) : fallback;
}

public sealed record VectorTileLayer(string Name, int Extent, List<VectorTileFeature> Features);

/// <summary>
/// A minimal Mapbox Vector Tile (protobuf) decoder: layers, features, properties and geometry
/// commands. Enough to draw the ground under the simulated camera; no dependency on a protobuf
/// library.
/// </summary>
public static class VectorTileDecoder
{
    public static Dictionary<string, VectorTileLayer> Decode(byte[] tile, IReadOnlySet<string>? onlyLayers = null)
    {
        var layers = new Dictionary<string, VectorTileLayer>(StringComparer.Ordinal);
        var reader = new ProtoReader(tile, 0, tile.Length);
        while (reader.Next(out var field, out var wire))
        {
            if (field == 3 && wire == 2)
            {
                var (start, end) = reader.LengthDelimited();
                var layer = DecodeLayer(tile, start, end, onlyLayers);
                if (layer is not null)
                    layers[layer.Name] = layer;
            }
            else
            {
                reader.Skip(wire);
            }
        }
        return layers;
    }

    private static VectorTileLayer? DecodeLayer(byte[] buf, int start, int end, IReadOnlySet<string>? onlyLayers)
    {
        string name = "";
        var extent = 4096;
        var keys = new List<string>();
        var values = new List<object>();
        var rawFeatures = new List<(int Start, int End)>();

        var reader = new ProtoReader(buf, start, end);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    name = reader.String();
                    if (onlyLayers is not null && !onlyLayers.Contains(name))
                        return null;
                    break;
                case 2 when wire == 2:
                    rawFeatures.Add(reader.LengthDelimited());
                    break;
                case 3 when wire == 2:
                    keys.Add(reader.String());
                    break;
                case 4 when wire == 2:
                    var (vs, ve) = reader.LengthDelimited();
                    values.Add(DecodeValue(buf, vs, ve));
                    break;
                case 5 when wire == 0:
                    extent = (int)reader.Varint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        var features = new List<VectorTileFeature>(rawFeatures.Count);
        foreach (var (fs, fe) in rawFeatures)
            features.Add(DecodeFeature(buf, fs, fe, keys, values));
        return new VectorTileLayer(name, extent, features);
    }

    private static VectorTileFeature DecodeFeature(byte[] buf, int start, int end, List<string> keys, List<object> values)
    {
        var type = GeometryType.Unknown;
        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        var parts = new List<List<(int, int)>>();

        var reader = new ProtoReader(buf, start, end);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 2 when wire == 2:
                    var tags = reader.PackedVarints();
                    for (var i = 0; i + 1 < tags.Count; i += 2)
                    {
                        var k = (int)tags[i];
                        var v = (int)tags[i + 1];
                        if (k < keys.Count && v < values.Count)
                            properties[keys[k]] = values[v];
                    }
                    break;
                case 3 when wire == 0:
                    type = (GeometryType)reader.Varint();
                    break;
                case 4 when wire == 2:
                    parts = DecodeGeometry(reader.PackedVarints());
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }
        return new VectorTileFeature(type, properties, parts);
    }

    private static List<List<(int X, int Y)>> DecodeGeometry(List<ulong> commands)
    {
        var parts = new List<List<(int, int)>>();
        List<(int, int)>? current = null;
        int x = 0, y = 0, i = 0;
        while (i < commands.Count)
        {
            var command = (uint)commands[i++];
            var id = command & 0x7;
            var count = command >> 3;
            if (id == 7)
            {
                // ClosePath: drawing a polygon closes it anyway.
                continue;
            }
            for (var n = 0; n < count && i + 1 < commands.Count; n++)
            {
                x += ZigZag((uint)commands[i++]);
                y += ZigZag((uint)commands[i++]);
                if (id == 1)
                {
                    current = [];
                    parts.Add(current);
                }
                current?.Add((x, y));
            }
        }
        return parts;
    }

    private static object DecodeValue(byte[] buf, int start, int end)
    {
        var reader = new ProtoReader(buf, start, end);
        object value = "";
        while (reader.Next(out var field, out var wire))
        {
            value = field switch
            {
                1 => reader.String(),
                2 => BitConverter.ToSingle(reader.Fixed(4)),
                3 => BitConverter.ToDouble(reader.Fixed(8)),
                4 => (long)reader.Varint(),
                5 => (double)reader.Varint(),
                6 => (long)ZigZag64(reader.Varint()),
                7 => reader.Varint() != 0,
                _ => SkipAndKeep(reader, wire, value)
            };
        }
        return value;
    }

    private static object SkipAndKeep(ProtoReader reader, int wire, object value)
    {
        reader.Skip(wire);
        return value;
    }

    private static int ZigZag(uint n) => (int)(n >> 1) ^ -(int)(n & 1);
    private static long ZigZag64(ulong n) => (long)(n >> 1) ^ -(long)(n & 1);

    private sealed class ProtoReader(byte[] buf, int pos, int end)
    {
        private int _pos = pos;

        public bool Next(out int field, out int wire)
        {
            if (_pos >= end)
            {
                field = wire = 0;
                return false;
            }
            var key = Varint();
            field = (int)(key >> 3);
            wire = (int)(key & 7);
            return true;
        }

        public ulong Varint()
        {
            ulong result = 0;
            for (var shift = 0; ; shift += 7)
            {
                var b = buf[_pos++];
                result |= (ulong)(b & 0x7f) << shift;
                if (b < 0x80)
                    return result;
            }
        }

        public (int Start, int End) LengthDelimited()
        {
            var length = (int)Varint();
            var start = _pos;
            _pos += length;
            return (start, start + length);
        }

        public string String()
        {
            var (s, e) = LengthDelimited();
            return Encoding.UTF8.GetString(buf, s, e - s);
        }

        public ReadOnlySpan<byte> Fixed(int size)
        {
            var span = buf.AsSpan(_pos, size);
            _pos += size;
            return span;
        }

        public List<ulong> PackedVarints()
        {
            var (s, e) = LengthDelimited();
            var saved = _pos;
            _pos = s;
            var list = new List<ulong>();
            while (_pos < e)
                list.Add(Varint());
            _pos = saved;
            return list;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: _pos += 8; break;
                case 2: LengthDelimited(); break;
                case 5: _pos += 4; break;
                default: throw new InvalidDataException($"Unsupported protobuf wire type {wire}.");
            }
        }
    }
}
