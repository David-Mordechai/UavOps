using FluentAssertions;
using UavOps.Simulator.Map;

namespace UavOps.Agent.Tests.Simulator;

/// <summary>The simulated camera's map readers against the real offline map. The archive is
/// built locally (scripts/build-offline-map.ps1) and git-ignored, so these pass trivially without it.</summary>
public class MapDataTests
{
    private static readonly string ArchivePath = Path.Combine(RepoRoot(), "src", "UavOps.Simulator", "wwwroot", "map", "israel.pmtiles");

    [Fact]
    public void TileId_MatchesThePmTilesSpec()
    {
        PmTilesReader.TileId(0, 0, 0).Should().Be(0UL);
        PmTilesReader.TileId(1, 0, 0).Should().Be(1UL);
        PmTilesReader.TileId(1, 0, 1).Should().Be(2UL);
        PmTilesReader.TileId(1, 1, 1).Should().Be(3UL);
        PmTilesReader.TileId(1, 1, 0).Should().Be(4UL);
        PmTilesReader.TileId(2, 0, 0).Should().Be(5UL);
    }

    [Fact]
    public void ATownTile_HasRoadsAndBuildings()
    {
        if (!File.Exists(ArchivePath))
            return;

        using var reader = new PmTilesReader(ArchivePath);
        var (x, y) = TileMath.TileAt(31.8138, 34.6652, reader.MaxZoom);

        var tile = reader.GetTile(reader.MaxZoom, x, y);

        tile.Should().NotBeNull();
        var layers = VectorTileDecoder.Decode(tile!);
        layers.Should().ContainKey("transportation");
        layers.Should().ContainKey("building");
        layers["transportation"].Features.Should().Contain(f => f.Type == GeometryType.LineString && f.Get("class") != null);
        layers["building"].Features.Should().OnlyContain(f => f.Type == GeometryType.Polygon);
        layers["building"].Features.SelectMany(f => f.Parts).SelectMany(p => p)
            .Should().OnlyContain(p => p.X >= -512 && p.X <= 4608 && p.Y >= -512 && p.Y <= 4608);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UavOps.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
