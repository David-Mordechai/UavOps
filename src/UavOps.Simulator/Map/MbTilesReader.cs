using Microsoft.Data.Sqlite;

namespace UavOps.Simulator.Map;

/// <summary>
/// Reads an MBTiles file (SQLite): the offline satellite basemap and terrain that
/// scripts/build-satellite.ps1 builds. Tiles are asked for in XYZ numbering (MBTiles stores rows
/// flipped). One connection per read, from SQLite's pool: reads come from many requests at once.
/// </summary>
public sealed class MbTilesReader
{
    private readonly string _connectionString;

    public MbTilesReader(string path)
    {
        // Changes when the file is rebuilt: the page puts it in tile URLs, since browsers keep
        // tiles for a day and would otherwise mix an old build's tiles into a new one.
        Version = File.GetLastWriteTimeUtc(path).Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, value FROM metadata";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            Metadata[reader.GetString(0)] = reader.GetString(1);
    }

    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Version { get; }

    public int MaxZoom => int.TryParse(Metadata.GetValueOrDefault("maxzoom"), out var z) ? z : 14;
    public int MinZoom => int.TryParse(Metadata.GetValueOrDefault("minzoom"), out var z) ? z : 0;
    public string Attribution => Metadata.GetValueOrDefault("attribution") ?? "";

    public byte[]? Get(int z, int x, int y)
    {
        if (z < 0 || z > 30)
            return null;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tile_data FROM tiles WHERE zoom_level = $z AND tile_column = $x AND tile_row = $y";
        command.Parameters.AddWithValue("$z", z);
        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$y", (1 << z) - 1 - y);
        return command.ExecuteScalar() as byte[];
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}

/// <summary>The offline satellite basemap and terrain, each null until built.</summary>
public sealed record SatelliteData(MbTilesReader? Basemap, MbTilesReader? Terrain);
