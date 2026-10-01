using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace UavOps.Agent.Mission;

public interface IAoiZoneStore
{
    /// <summary>Null when no zone matches (see <see cref="AoiZoneNames.Key"/> for how names match).</summary>
    Task<AoiZone?> GetAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<AoiZone>> ListAsync(CancellationToken cancellationToken);
}

public static class AoiZoneNames
{
    /// <summary>
    /// Names match ignoring case, whitespace, dashes, underscores and a leading "AOI", so an
    /// operator saying "zone a" or "AOI Zone-A" finds ZoneA.
    /// </summary>
    public static string Key(string name)
    {
        var key = new string(name.Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '_').ToArray())
            .ToLowerInvariant();
        return key.Length > 3 && key.StartsWith("aoi", StringComparison.Ordinal) ? key[3..] : key;
    }
}

/// <summary>
/// AOI polygons in a SQLite file, one row per zone, polygon as GeoJSON text (so moving to a
/// spatial database later keeps the data as-is). The table is created and seeded from the
/// embedded aoi-seed.json the first time any process opens the file; McpMoav and
/// UavOps.Simulator open the same one. When the seed changes (<see cref="SeedVersion"/>), a file
/// seeded from an older one gets the seed's zones rewritten; zones with other names are kept.
/// </summary>
public sealed class SqliteAoiZoneStore : IAoiZoneStore
{
    /// <summary>Bump when aoi-seed.json changes. 2: the zones moved to open country with real
    /// aerial photos (Yatir forest road, Route 443). 3: ZoneA is the whole Yatir drone photo, and
    /// ZoneB is gone (operator's decision, 2026-10-01: one zone, three UAVs).</summary>
    public const int SeedVersion = 3;

    /// <summary>Seed zones that a later seed dropped: deleted when a file is upgraded (other zones
    /// with names not in the seed are kept).</summary>
    private static readonly string[] RetiredSeedZones = ["ZoneB"];

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteAoiZoneStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();
    }

    public string DatabasePath { get; }

    public async Task<AoiZone?> GetAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT name, polygon_geojson FROM aoi_zone WHERE name_key = $key";
        command.Parameters.AddWithValue("$key", AoiZoneNames.Key(name));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AoiZone(reader.GetString(0), GeoJsonPolygon.Parse(reader.GetString(1)))
            : null;
    }

    public async Task<IReadOnlyList<AoiZone>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT name, polygon_geojson FROM aoi_zone ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var zones = new List<AoiZone>();
        while (await reader.ReadAsync(cancellationToken))
            zones.Add(new AoiZone(reader.GetString(0), GeoJsonPolygon.Parse(reader.GetString(1))));
        return zones;
    }

    /// <summary>Adds or replaces a zone.</summary>
    public async Task UpsertAsync(AoiZone zone, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await WriteAsync(connection, zone, replace: true, cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await _initLock.WaitAsync(cancellationToken);
            try
            {
                if (!_initialized)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
                    await using var init = new SqliteConnection(_connectionString);
                    await init.OpenAsync(cancellationToken);
                    await InitializeAsync(init, cancellationToken);
                    _initialized = true;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    // IF NOT EXISTS / INSERT OR REPLACE: two processes may open the same file at the same time.
    private static async Task InitializeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS aoi_zone (
                name            TEXT PRIMARY KEY COLLATE NOCASE,
                name_key        TEXT NOT NULL UNIQUE,
                polygon_geojson TEXT NOT NULL,
                updated_utc     TEXT NOT NULL
            )
            """;
        await create.ExecuteNonQueryAsync(cancellationToken);

        var meta = connection.CreateCommand();
        meta.CommandText = "CREATE TABLE IF NOT EXISTS aoi_seed (version INTEGER NOT NULL)";
        await meta.ExecuteNonQueryAsync(cancellationToken);
        var read = connection.CreateCommand();
        read.CommandText = "SELECT MAX(version) FROM aoi_seed";
        var seeded = await read.ExecuteScalarAsync(cancellationToken) is long v ? v : 0;
        if (seeded >= SeedVersion)
            return;

        // A new file, or one seeded from an older aoi-seed.json: (re)write the seed's zones, and
        // drop the ones a newer seed retired.
        foreach (var zone in LoadSeed())
            await WriteAsync(connection, zone, replace: true, cancellationToken);
        foreach (var retired in RetiredSeedZones)
        {
            var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM aoi_zone WHERE name_key = $key";
            delete.Parameters.AddWithValue("$key", AoiZoneNames.Key(retired));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        var write = connection.CreateCommand();
        write.CommandText = "DELETE FROM aoi_seed; INSERT INTO aoi_seed VALUES ($v)";
        write.Parameters.AddWithValue("$v", SeedVersion);
        await write.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteAsync(SqliteConnection connection, AoiZone zone, bool replace, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"INSERT OR {(replace ? "REPLACE" : "IGNORE")} INTO aoi_zone VALUES ($name, $key, $polygon, $updated)";
        command.Parameters.AddWithValue("$name", zone.Name);
        command.Parameters.AddWithValue("$key", AoiZoneNames.Key(zone.Name));
        command.Parameters.AddWithValue("$polygon", GeoJsonPolygon.Write(zone.Vertices));
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The zones a new database starts with.</summary>
    public static IReadOnlyList<AoiZone> LoadSeed()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("UavOps.Agent.Mission.aoi-seed.json")
            ?? throw new InvalidOperationException("aoi-seed.json is not embedded in UavOps.Agent.Mission.");
        var seed = JsonNode.Parse(stream)!.AsArray();
        return seed.Select(entry => new AoiZone(
            entry!["name"]!.GetValue<string>(),
            GeoJsonPolygon.Parse(entry["polygon"]!.ToJsonString()))).ToList();
    }
}

/// <summary>A GeoJSON Polygon's outer ring ([lng, lat] pairs, closed) as open-ring vertices.</summary>
public static class GeoJsonPolygon
{
    public static IReadOnlyList<GeoPoint> Parse(string geoJson)
    {
        var node = JsonNode.Parse(geoJson)!;
        if (node["type"]?.GetValue<string>() != "Polygon")
            throw new FormatException("Expected a GeoJSON Polygon.");
        var ring = node["coordinates"]![0]!.AsArray()
            .Select(p => new GeoPoint(p![1]!.GetValue<double>(), p[0]!.GetValue<double>()))
            .ToList();
        if (ring.Count > 1 && ring[0] == ring[^1])
            ring.RemoveAt(ring.Count - 1);
        return ring;
    }

    public static string Write(IReadOnlyList<GeoPoint> vertices)
    {
        var ring = vertices.Append(vertices[0]).Select(v => new[] { v.Lng, v.Lat });
        return JsonSerializer.Serialize(new { type = "Polygon", coordinates = new[] { ring } });
    }
}
