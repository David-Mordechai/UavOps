using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UavOps.Agent.Mission;
using UavOps.Onboard.Contracts;
using UavOps.Onboard.Detector;

namespace UavOps.Agent.Tests.Onboard;

/// <summary>The onboard detector's deterministic parts: reading the model, matching the target,
/// placing boxes on the ground, and not reporting one object twice.</summary>
public class DetectionPipelineTests
{
    [Theory]
    [InlineData("[{\"label\": \"white van\", \"bbox_2d\": [100, 200, 150, 260], \"confidence\": 0.9}]")]
    [InlineData("```json\n[{\"label\": \"white van\", \"bbox_2d\": [100, 200, 150, 260], \"confidence\": 0.9}]\n```")]
    [InlineData("Here you go: [{\"label\": \"white van\", \"bbox_2d\": [150, 260, 100, 200], \"confidence\": 0.9}] done")]
    public void Parse_ReadsBoxes_FencedOrNot_AndOrdersCorners(string answer)
    {
        var hit = DetectionParser.Parse(answer, 0.5).Should().ContainSingle().Subject;

        hit.Label.Should().Be("white van");
        hit.Box.Should().Be(new BoundingBox(100, 200, 150, 260));
    }

    [Theory]
    [InlineData("[{\"bbox_2d\": [100, 200, 1500, 260]}]")]  // out of range
    [InlineData("[{\"bbox_2d\": [100, 200, 100, 260]}]")]   // zero width
    [InlineData("[{\"bbox_2d\": [100, 200, 150]}]")]        // three numbers
    [InlineData("I can't see anything like that.")]
    [InlineData("[]")]
    public void Parse_DropsWhatIsntAValidBox(string answer) =>
        DetectionParser.Parse(answer, 0).Should().BeEmpty();

    [Fact]
    public void Parse_AcceptsBareBoxes_AndFiltersByConfidence()
    {
        DetectionParser.Parse("[[10, 10, 20, 20], [30, 30, 40, 40]]", 0).Should().HaveCount(2);
        DetectionParser.Parse("[{\"bbox_2d\": [10, 10, 20, 20], \"confidence\": 0.3}]", 0.5).Should().BeEmpty();
    }

    [Theory]
    [InlineData("```json\n{\"colour\": \"white\", \"type\": \"pickup\", \"confidence\": 0.95}\n```", "white pickup")]
    [InlineData("It has an open bed. {\"color\": \"White\", \"type\": \"Pickup\", \"confidence\": 0.9}", "white pickup")]
    public void ParseDescription_ReadsColourAndType(string answer, string expected) =>
        DetectionParser.ParseDescription(answer)!.Text.Should().Be(expected);

    [Theory]
    [InlineData("white van", "white van", true)]
    [InlineData("a white van", "white van", true)]
    [InlineData("white van", "white car", false)]
    [InlineData("white van", "silver van", false)]
    [InlineData("grey truck", "gray truck", true)]
    [InlineData("white vans", "white van", true)]
    [InlineData("van", "white van", true)]
    [InlineData("", "white van", false)]
    public void TargetMatcher_NeedsEveryTargetWord(string target, string description, bool matches) =>
        TargetMatcher.Matches(target, description).Should().Be(matches);

    [Fact]
    public void Tracker_ReportsAnObjectOnce_PerMissionAndPrompt()
    {
        var tracker = new DetectionTracker(25);
        var van = new GeoPoint(31.81397, 34.66524);

        tracker.Observe("m1", "white van", van, 0.9).Should().NotBeNull();
        tracker.Observe("m1", "white van", new GeoPoint(31.81398, 34.66526), 0.95).Should().BeNull("~2 m away: the same van");
        tracker.Observe("m1", "white van", new GeoPoint(31.81500, 34.66524), 0.9).Should().NotBeNull("~115 m away: another one");
        tracker.Observe("m2", "white van", van, 0.9).Should().NotBeNull("a new mission starts fresh");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(180)]
    [InlineData(271)]
    public void CameraModel_PixelAndGround_RoundTrip(double heading)
    {
        var camera = new CameraModel(31.81, 34.66, 1000, heading, 20, 1280, 960);
        var ground = camera.PixelToGeo(1000, 200);

        var (x, y) = camera.GeoToPixel(ground);

        x.Should().BeApproximately(1000, 0.01);
        y.Should().BeApproximately(200, 0.01);
        camera.PixelToGeo(640, 480).Should().Be(new GeoPoint(31.81, 34.66));
    }

    [Fact]
    public void CameraModel_TopOfTheImageIsTheHeading()
    {
        // Heading east: a point ahead of the UAV (east of it) is above the centre of the image.
        var camera = new CameraModel(31.81, 34.66, 1000, 90, 20, 1280, 960);

        var (x, y) = camera.GeoToPixel(new GeoPoint(31.81, 34.6602));

        x.Should().BeApproximately(640, 1);
        y.Should().BeLessThan(480);
        camera.GroundWidthMeters.Should().BeApproximately(107.5, 0.5, "2·1000 ft·tan(10°)");
    }

    [Fact]
    public async Task Runner_ChecksCandidatesUpClose_AndReportsOnlyMatches_Once()
    {
        // Frame centred on the "van"; the model proposes two candidates, the close-up says the
        // first is a white van and the second a white car.
        var telemetry = new FrameTelemetry(7, DateTime.UtcNow, 31.81, 34.66, 1000, 0, 20, 1280, 960, "m1");
        var model = new ScriptedModel(
            candidates: "[{\"label\": \"white vehicle\", \"bbox_2d\": [490, 490, 510, 510]}, {\"label\": \"white vehicle\", \"bbox_2d\": [100, 100, 120, 120]}]",
            describe: box => box ? "{\"colour\": \"white\", \"type\": \"van\", \"confidence\": 0.9}" : "{\"colour\": \"white\", \"type\": \"car\", \"confidence\": 0.9}");
        var zoom = new RecordingZoom();
        var sink = new RecordingSink();
        var task = new SearchTask("997", "m1", "ZoneA", "white van", 0.5, "frames", 0, "callback", "zoom");
        var runner = new SearchTaskRunner(task, null!, model, sink, new DetectionTracker(25), new DetectorOptions(), NullLogger.Instance, zoom);
        var frame = new CameraFrame(telemetry, [0xFF]);

        await runner.AnalyzeAsync(frame, CancellationToken.None);
        await runner.AnalyzeAsync(frame with { Telemetry = telemetry with { Seq = 8 } }, CancellationToken.None);

        var detection = sink.Sent.Should().ContainSingle("the second frame sees the same van").Subject;
        detection.Label.Should().Be("white van");
        detection.FrameSeq.Should().Be(7);
        GeoProjection.DistanceMeters(new GeoPoint(detection.Lat, detection.Lng), new GeoPoint(31.81, 34.66)).Should().BeLessThan(1);
        zoom.Captures.Should().Be(2, "each candidate is zoomed on once; the second frame reuses what was seen");
    }

    private sealed class ScriptedModel(string candidates, Func<bool, string> describe) : IVisionModel
    {
        private int _describes;

        public Task<VisionAnswer> AskAsync(byte[] jpeg, string question, CancellationToken cancellationToken)
        {
            if (question.StartsWith("Search target", StringComparison.Ordinal))
                return Task.FromResult(new VisionAnswer(candidates, 10));
            // Zoom images are tagged by RecordingZoom: 1 = the centre candidate.
            var centre = jpeg.Length == 1 && jpeg[0] == 1;
            Interlocked.Increment(ref _describes);
            return Task.FromResult(new VisionAnswer(describe(centre), 5));
        }
    }

    private sealed class RecordingZoom : IZoomCamera
    {
        private int _captures;
        public int Captures => _captures;

        public Task<byte[]?> CaptureAsync(string zoomUrl, double lat, double lng, double widthMeters, int pixels, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _captures);
            var centre = GeoProjection.DistanceMeters(new GeoPoint(lat, lng), new GeoPoint(31.81, 34.66)) < 5;
            return Task.FromResult<byte[]?>([(byte)(centre ? 1 : 2)]);
        }
    }

    private sealed class RecordingSink : IDetectionSink
    {
        public List<OnboardDetection> Sent { get; } = [];

        public Task SendAsync(string callbackUrl, OnboardDetection detection, CancellationToken cancellationToken)
        {
            lock (Sent)
                Sent.Add(detection);
            return Task.CompletedTask;
        }
    }
}
