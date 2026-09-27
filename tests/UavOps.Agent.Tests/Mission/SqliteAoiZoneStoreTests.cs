using FluentAssertions;
using Microsoft.Data.Sqlite;
using UavOps.Agent.Mission;

namespace UavOps.Agent.Tests.Mission;

public sealed class SqliteAoiZoneStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "uavops-aoi-tests-" + Guid.NewGuid().ToString("N"));

    private SqliteAoiZoneStore NewStore() => new(Path.Combine(_directory, "aoi.db"));

    public void Dispose()
    {
        TempDirectory.DeleteSqliteFolder(_directory);
    }

    [Fact]
    public async Task NewDatabase_IsSeeded()
    {
        var zones = await NewStore().ListAsync(CancellationToken.None);

        zones.Select(z => z.Name).Should().Equal("ZoneA", "ZoneB");
        zones[0].Vertices.Should().HaveCount(30, "the seed's 31 GeoJSON points, the closing one dropped");
    }

    [Theory]
    [InlineData("ZoneA")]
    [InlineData("zonea")]
    [InlineData("zone a")]
    [InlineData("Zone-A")]
    [InlineData("AOI zone A")]
    public async Task Get_MatchesNamesLoosely(string name)
    {
        var zone = await NewStore().GetAsync(name, CancellationToken.None);

        zone!.Name.Should().Be("ZoneA");
    }

    [Fact]
    public async Task Get_UnknownZone_IsNull()
    {
        (await NewStore().GetAsync("ZoneZ", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Upsert_PersistsAcrossStores_AndSeedDoesNotRerun()
    {
        var zone = new AoiZone("ZoneC", [new(31.80, 34.60), new(31.80, 34.61), new(31.81, 34.61)]);
        await NewStore().UpsertAsync(zone, CancellationToken.None);

        var reopened = await NewStore().ListAsync(CancellationToken.None);

        reopened.Select(z => z.Name).Should().Equal("ZoneA", "ZoneB", "ZoneC");
        reopened[2].Vertices.Should().Equal(zone.Vertices);
    }

    [Fact]
    public void Summary_ReportsAreaAndBounds()
    {
        var zoneA = SqliteAoiZoneStore.LoadSeed().Single(z => z.Name == "ZoneA");

        var summary = zoneA.Summarize();

        // The ~1.3 km x ~190 m strip along the Yatir road.
        summary.AreaSqKm.Should().BeApproximately(0.224, 0.005);
        summary.SouthWest.Should().Be(new GeoPoint(31.343184, 35.044370));
        summary.NorthEast.Should().Be(new GeoPoint(31.350906, 35.054600));
    }

    [Fact]
    public async Task AFileSeededFromAnOlderSeed_GetsTheNewZones_AndKeepsOthers()
    {
        // A database from before the zones moved: old ZoneA, an operator's ZoneC, no seed version.
        var path = Path.Combine(_directory, "old.db");
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE aoi_zone (name TEXT PRIMARY KEY COLLATE NOCASE, name_key TEXT NOT NULL UNIQUE, polygon_geojson TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO aoi_zone VALUES ('ZoneA', 'zonea', '{"type":"Polygon","coordinates":[[[34.652,31.8075],[34.667,31.8075],[34.667,31.816],[34.652,31.8075]]]}', '2026-01-01');
                INSERT INTO aoi_zone VALUES ('ZoneC', 'zonec', '{"type":"Polygon","coordinates":[[[34.60,31.80],[34.61,31.80],[34.61,31.81],[34.60,31.80]]]}', '2026-01-01');
                """;
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var zones = await new SqliteAoiZoneStore(path).ListAsync(CancellationToken.None);

        zones.Select(z => z.Name).Should().Equal("ZoneA", "ZoneB", "ZoneC");
        zones[0].Vertices.Should().Equal(SqliteAoiZoneStore.LoadSeed().Single(z => z.Name == "ZoneA").Vertices);
    }
}
