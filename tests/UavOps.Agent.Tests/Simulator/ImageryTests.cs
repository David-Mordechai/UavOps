using FluentAssertions;
using UavOps.Agent.Mission;
using UavOps.Simulator.Imagery;

namespace UavOps.Agent.Tests.Simulator;

/// <summary>The real-photo ground: UTM conversion, reading the GeoTIFFs, and placing them. The
/// photos are fetched locally (scripts/fetch-imagery.ps1) and git-ignored, so the tests that read
/// them pass trivially without them.</summary>
public class ImageryTests
{
    private static readonly string ImageryDir = Path.Combine(RepoRoot(), "src", "UavOps.Simulator", "imagery");

    [Theory]
    [InlineData(31.3465, 35.0503)]
    [InlineData(32.0676, 34.9190)]
    [InlineData(-33.9, 18.4)]
    public void Utm_RoundTrips(double lat, double lng)
    {
        var zone = (int)Math.Floor((lng + 180) / 6) + 1;
        var north = lat >= 0;

        var (e, n) = Utm.FromLatLng(lat, lng, zone, north);
        var (lat2, lng2) = Utm.ToLatLng(e, n, zone, north);

        lat2.Should().BeApproximately(lat, 1e-8);
        lng2.Should().BeApproximately(lng, 1e-8);
    }

    [Fact]
    public void Utm_MatchesAKnownPoint()
    {
        // 32 N on zone 36's central meridian (33 E): easting exactly 500 000 m, northing k0 x the
        // WGS84 meridian arc to 32 N (3 541 852.43 m, integrated numerically) = 3 540 435.69 m.
        var (e, n) = Utm.FromLatLng(32, 33, 36, true);

        e.Should().BeApproximately(500000, 0.001);
        n.Should().BeApproximately(3540435.69, 0.05);
    }

    [Fact]
    public void Photos_AreRead_AndPlacedWhereTheyAre()
    {
        if (!File.Exists(Path.Combine(ImageryDir, "yatir-road.tif")))
            return;

        var imagery = new ImageryLayer(ImageryDir);

        var yatir = imagery.Find("yatir-road")!;
        yatir.Tiff.UtmZone.Should().Be(36);
        yatir.Tiff.PixelSizeX.Should().BeApproximately(0.042, 0.002);
        yatir.Tiff.Levels.Should().HaveCountGreaterThan(4);
        imagery.Covers(new GeoPoint(31.3465, 35.0503)).Should().BeTrue("alpha is on the Yatir road");
        imagery.Covers(new GeoPoint(31.80, 34.65)).Should().BeFalse();

        // The pixel under alpha (on the road) is opaque photo; a corner of the image is no-data.
        var level = yatir.Tiff.Levels[^3];
        var (e, n) = Utm.FromLatLng(31.3465, 35.0503, yatir.Tiff.UtmZone, yatir.Tiff.North);
        var (col, row) = yatir.Tiff.UtmToPixel(e, n);
        var (x, y) = ((int)(col / level.Scale), (int)(row / level.Scale));
        Alpha(level, x, y).Should().Be(255);
        Alpha(level, level.Width - 2, level.Height - 2).Should().Be(0);
    }

    [Fact]
    public void VehiclePhotos_AreCutOut_FacingUp_AtTheirSize()
    {
        if (!File.Exists(Path.Combine(ImageryDir, "route-443.tif")))
            return;

        var photos = new VehiclePhotos(new ImageryLayer(ImageryDir),
        [
            new VehiclePhotoConfig { Name = "white-van", Image = "route-443", Col = 13201, Row = 7691, LengthPx = 275, WidthPx = 125, HeadingDeg = 270, Kind = "van", Color = "white" }
        ]);

        var van = photos.Get("white-van");

        van.Should().NotBeNull();
        van!.Value.MetersPerPixel.Should().BeApproximately(0.02, 0.002);
        van.Value.Image.Height.Should().BeGreaterThan(van.Value.Image.Width, "rotated so its length runs up the image");
        photos.Matching("van", "white")!.Name.Should().Be("white-van");
        photos.Get("no-such-photo").Should().BeNull();
    }

    private static byte Alpha(GeoTiff.Level level, int x, int y)
    {
        using var tile = level.DecodeTile(x / level.TileWidth, y / level.TileHeight);
        if (tile is null)
            return 0;
        using var bitmap = SkiaSharp.SKBitmap.FromImage(tile);
        return bitmap.GetPixel(x % level.TileWidth, y % level.TileHeight).Alpha;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UavOps.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
