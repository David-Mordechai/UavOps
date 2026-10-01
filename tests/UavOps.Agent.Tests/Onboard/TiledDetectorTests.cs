using FluentAssertions;
using SkiaSharp;
using UavOps.Onboard.Contracts;
using UavOps.Onboard.Detector.Perception;

namespace UavOps.Agent.Tests.Onboard;

/// <summary>The detector on tiles of the full frame: which regions it runs on, boxes back in frame
/// coordinates, and one report for an object two tiles both see.</summary>
public class TiledDetectorTests
{
    [Fact]
    public void Tiles_2x2_OfTheSearchFrame_AreEqual_Overlap_AndCoverIt()
    {
        var tiles = TiledDetector.Tiles(1280, 960, 2, 2, 64);

        tiles.Should().Equal(
            new SKRectI(0, 0, 672, 512), new SKRectI(608, 0, 1280, 512),
            new SKRectI(0, 448, 672, 960), new SKRectI(608, 448, 1280, 960));
    }

    [Fact]
    public void Tiles_1x1_IsTheWholeFrame()
    {
        TiledDetector.Tiles(1280, 960, 1, 1, 64).Should().Equal(new SKRectI(0, 0, 1280, 960));
    }

    [Fact]
    public void Detect_RunsEveryTile_AndMapsBoxesBackToTheFrame()
    {
        // A car in the bottom-right tile only, at frame pixels (900-950, 700-725).
        var inner = new ScriptedRegions(region => region.Left == 608 && region.Top == 448
            ? [new DetectedObject("Car", 0.6, InTile(region, 900, 700, 950, 725))]
            : []);
        using var frame = new SKBitmap(1280, 960);

        var found = new TiledDetector(inner, 2, 2, 64).Detect(frame, 0.2);

        inner.Regions.Should().HaveCount(4);
        found.Should().ContainSingle();
        var box = found[0].Box;
        box.X1.Should().BeApproximately(900 / 1280.0 * 1000, 0.01);
        box.Y1.Should().BeApproximately(700 / 960.0 * 1000, 0.01);
        box.X2.Should().BeApproximately(950 / 1280.0 * 1000, 0.01);
        box.Y2.Should().BeApproximately(725 / 960.0 * 1000, 0.01);
    }

    [Fact]
    public void Detect_ACarOnASeam_SeenByBothTiles_IsReportedOnce_WithTheBetterScore()
    {
        // Frame pixels 620-660 wide: inside both top tiles' overlap (608-672). One tile calls it a
        // Car, the other an SUV.
        var inner = new ScriptedRegions(region => region.Top != 0
            ? []
            : region.Left == 0
                ? [new DetectedObject("Car", 0.4, InTile(region, 620, 100, 660, 120))]
                : [new DetectedObject("SUV", 0.7, InTile(region, 621, 100, 660, 121))]);
        using var frame = new SKBitmap(1280, 960);

        var found = new TiledDetector(inner, 2, 2, 64).Detect(frame, 0.2);

        found.Should().ContainSingle().Which.Score.Should().Be(0.7);
    }

    [Fact]
    public void Detect_ACarCutByATileEdge_InsideTheNeighboursWholeBox_IsReportedOnce()
    {
        // The left tile ends at 672: it sees only 650-672 of a car spanning 650-700.
        var inner = new ScriptedRegions(region => region.Top != 0
            ? []
            : region.Left == 0
                ? [new DetectedObject("Car", 0.3, InTile(region, 650, 100, 672, 120))]
                : [new DetectedObject("Car", 0.6, InTile(region, 650, 100, 700, 120))]);
        using var frame = new SKBitmap(1280, 960);

        new TiledDetector(inner, 2, 2, 64).Detect(frame, 0.2).Should().ContainSingle().Which.Score.Should().Be(0.6);
    }

    [Fact]
    public void Merge_KeepsTwoCarsParkedSideBySide()
    {
        var found = TiledDetector.Merge([
            new DetectedObject("Car", 0.6, new BoundingBox(100, 100, 120, 110)),
            new DetectedObject("Car", 0.5, new BoundingBox(119, 100, 139, 110))]);

        found.Should().HaveCount(2);
    }

    [Fact]
    public void InputSize_AsksForTheFullResolutionFrame()
    {
        new TiledDetector(new ScriptedRegions(_ => []), 2, 2, 64).InputSize.Should().Be(1280);
    }

    /// <summary>A box at frame pixels, as the inner detector reports it: normalized to the tile.</summary>
    private static BoundingBox InTile(SKRectI tile, double x1, double y1, double x2, double y2) => new(
        (x1 - tile.Left) / tile.Width * 1000, (y1 - tile.Top) / tile.Height * 1000,
        (x2 - tile.Left) / tile.Width * 1000, (y2 - tile.Top) / tile.Height * 1000);

    private sealed class ScriptedRegions(Func<SKRectI, IReadOnlyList<DetectedObject>> script) : IRegionDetector
    {
        public List<SKRectI> Regions { get; } = [];
        public string Name => "scripted";
        public int InputSize => 640;

        public IReadOnlyList<DetectedObject> Detect(SKBitmap image, double minScore) =>
            Detect(image, new SKRectI(0, 0, image.Width, image.Height), minScore);

        public IReadOnlyList<DetectedObject> Detect(SKBitmap image, SKRectI region, double minScore)
        {
            Regions.Add(region);
            return script(region);
        }
    }
}
