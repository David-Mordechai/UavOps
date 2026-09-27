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
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task NewDatabase_IsSeeded()
    {
        var zones = await NewStore().ListAsync(CancellationToken.None);

        zones.Select(z => z.Name).Should().Equal("ZoneA", "ZoneB");
        zones[0].Vertices.Should().HaveCount(8, "the closing GeoJSON vertex is dropped");
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

        // 1.5 x 1 km minus the 0.5 x 0.65 km notch.
        summary.AreaSqKm.Should().BeApproximately(1.5 - 0.325, 0.02);
        summary.SouthWest.Should().Be(new GeoPoint(31.80750, 34.65200));
        summary.NorthEast.Should().Be(new GeoPoint(31.81649, 34.66786));
    }
}
