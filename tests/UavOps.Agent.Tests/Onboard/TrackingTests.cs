using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using UavOps.Agent.Mission;
using UavOps.Onboard.Contracts;
using UavOps.Onboard.Detector;
using UavOps.Onboard.Detector.Autonomy;
using UavOps.Onboard.Detector.Perception;

namespace UavOps.Agent.Tests.Onboard;

/// <summary>The onboard autonomy stack's deterministic parts: ground-plane tracking, the target
/// made structured, colour from pixels, and the executive's search → verify → track → reacquire loop
/// (with a scripted detector and verifier - the real ones run on the Jetson).</summary>
public class TrackingTests
{
    private static readonly GeoPoint Origin = new(31.34, 35.04);
    private static readonly DateTime T0 = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private static GroundObservation Obs(GeoProjection p, double east, double north, double score = 0.8, string cls = "Car", string? colour = "red") =>
        new(p.ToGeo(new Vec2(east, north)), cls, score, colour, new BoundingBox(0, 0, 10, 10), 4.5, 1.5);

    [Fact]
    public void Tracker_FollowsADrivingCar_AsOneTrack_WithItsSpeedAndHeading()
    {
        var tracker = new GroundTracker(Origin);
        var p = tracker.Projection;
        for (var i = 0; i <= 50; i++) // 5 s at 10 fps, 12 m/s east
            tracker.Update([Obs(p, 12 * i * 0.1, 0)], T0.AddSeconds(i * 0.1), i, _ => true);

        var track = tracker.Tracks.Should().ContainSingle().Subject;
        track.State.Should().Be(TrackState.Confirmed);
        track.SpeedMps.Should().BeApproximately(12, 1);
        track.HeadingDeg.Should().BeApproximately(90, 5);
        track.Label.Should().Be("T-1");
    }

    [Fact]
    public void Tracker_KeepsTwoCrossingCarsApart()
    {
        var tracker = new GroundTracker(Origin);
        var p = tracker.Projection;
        // One drives east, one north, both through (0,0) at t = 3 s.
        for (var i = 0; i <= 60; i++)
        {
            var t = i * 0.1;
            tracker.Update([Obs(p, 10 * (t - 3), 0), Obs(p, 0, 10 * (t - 3))], T0.AddSeconds(t), i, _ => true);
        }

        tracker.Tracks.Should().HaveCount(2);
        var east = tracker.Tracks.Single(tr => tr.VelocityLocal.X > 5);
        var north = tracker.Tracks.Single(tr => tr.VelocityLocal.Y > 5);
        east.PositionLocal.X.Should().BeApproximately(30, 3);
        north.PositionLocal.Y.Should().BeApproximately(30, 3);
    }

    [Fact]
    public void Tracker_CoastsThenLoses_ATrackMissedInView_ButNotOneOutOfView()
    {
        var tracker = new GroundTracker(Origin) { CoastSeconds = 2 };
        var p = tracker.Projection;
        for (var i = 0; i < 5; i++)
            tracker.Update([Obs(p, 0, 0)], T0.AddSeconds(i * 0.1), i, _ => true);
        var track = tracker.Tracks.Single();

        tracker.Update([], T0.AddSeconds(1), 10, _ => false); // camera looking elsewhere
        track.State.Should().Be(TrackState.Confirmed);

        tracker.Update([], T0.AddSeconds(1.5), 11, _ => true);
        track.State.Should().Be(TrackState.Coasting);
        tracker.Update([], T0.AddSeconds(3), 12, _ => true);
        track.State.Should().Be(TrackState.Lost);
    }

    [Fact]
    public void Tracker_TheLockedTarget_IsNeverContinuedByAnotherVehicle()
    {
        var tracker = new GroundTracker(Origin);
        var p = tracker.Projection;
        for (var i = 0; i < 5; i++)
            tracker.Update([Obs(p, 0, 0, colour: "red")], T0.AddSeconds(i * 0.1), i, _ => true);
        var target = tracker.Tracks.Single();
        target.Accepts = o => o.Colour == "red";

        // The red car disappears under trees; a white car passes right where it was.
        for (var i = 5; i < 15; i++)
            tracker.Update([Obs(p, 2, 0, colour: "white")], T0.AddSeconds(i * 0.1), i, _ => true);

        target.PositionLocal.Length.Should().BeLessThan(1, "the target track must not slide onto the white car");
        target.State.Should().NotBe(TrackState.Confirmed);
    }

    /// <summary>Measured on the Jetson: the locked red car's sighting fell just outside the 99% gate,
    /// started a second track, and that track took every later sighting while the target coasted
    /// on and was declared lost with the car in view.</summary>
    [Fact]
    public void Tracker_TheLockedTarget_KeepsItsSightings_WhenOneFallsOutsideTheGate()
    {
        var tracker = new GroundTracker(Origin);
        var p = tracker.Projection;
        for (var i = 0; i < 20; i++) // parked: the filter settles tight around it
            tracker.Update([Obs(p, 0, 0)], T0.AddSeconds(i * 0.1), i, _ => true);
        var target = tracker.Tracks.Single();
        target.Accepts = o => o.Colour == "red";

        // It pulls away: the sightings land 12 m off the prediction, then follow on from there.
        for (var i = 20; i < 60; i++)
            tracker.Update([Obs(p, 12 + (i - 20) * 0.5, 0)], T0.AddSeconds(i * 0.1), i, _ => true);

        tracker.Tracks.Should().ContainSingle("no second track may take the target's sightings").Which.Should().BeSameAs(target);
        target.State.Should().Be(TrackState.Confirmed);
        target.PositionLocal.X.Should().BeApproximately(12 + 39 * 0.5, 2);
    }

    [Fact]
    public void Tracker_FoldsADuplicateOfTheLockedTargetBackIn_ButNotAnotherCar()
    {
        var tracker = new GroundTracker(Origin);
        var p = tracker.Projection;
        for (var i = 0; i < 20; i++)
            tracker.Update([Obs(p, 0, 0)], T0.AddSeconds(i * 0.1), i, _ => true);
        var target = tracker.Tracks.Single();
        target.Accepts = o => o.Colour == "red";

        // Unseen for 1.1 s, then 30 m away: outside even the wide gate of a filter that had it
        // parked, but within driving reach. A new track starts there, and is folded back in.
        tracker.Update([], T0.AddSeconds(2.5), 20, _ => true);
        tracker.Update([Obs(p, 30, 0)], T0.AddSeconds(3.0), 21, _ => true);

        tracker.Tracks.Should().ContainSingle().Which.Should().BeSameAs(target);
        target.PositionLocal.X.Should().BeApproximately(30, 0.5);
        target.State.Should().Be(TrackState.Confirmed);

        // A white car nearby is never folded in.
        tracker.Update([Obs(p, 30.5, 0), Obs(p, 34, 0, colour: "white")], T0.AddSeconds(3.1), 22, _ => true);
        tracker.Tracks.Should().HaveCount(2);
        tracker.Tracks.Single(t => t != target).Colour.Should().Be("white");
    }

    /// <summary>Measured on the Jetson: sightings the target's filter rejected (a class flip) had
    /// grown into tracks of their own, which then kept the car's sightings once the target coasted.</summary>
    [Fact]
    public void Tracker_FoldsAnOlderTrackOfTheTarget_BackIn_OnceTheTargetHasBeenMissedASecond()
    {
        var tracker = new GroundTracker(Origin);
        var p = tracker.Projection;
        for (var i = 0; i < 20; i++) // the target at 0, and a red "truck" track 20 m east (the same car, misread)
            tracker.Update([Obs(p, 0, 0), Obs(p, 20, 0, cls: "Truck")], T0.AddSeconds(i * 0.1), i, _ => true);
        tracker.Tracks.Should().HaveCount(2);
        var target = tracker.Tracks.Single(t => t.Class == "Car");
        target.Accepts = o => TargetSpec.Parse("red car")!.CouldStillBe(o.Class, o.Colour);

        tracker.Update([Obs(p, 20, 0, cls: "Truck")], T0.AddSeconds(2.0), 20, _ => true);
        tracker.Tracks.Should().HaveCount(2, "missed for only 0.1 s: the other track may be another red car");

        for (var i = 21; i <= 32; i++)
            tracker.Update([Obs(p, 20, 0, cls: "Truck")], T0.AddSeconds(i * 0.1), i, _ => true);
        tracker.Tracks.Should().ContainSingle().Which.Should().BeSameAs(target);
        target.State.Should().Be(TrackState.Confirmed);
        target.PositionLocal.X.Should().BeApproximately(20, 1);
    }

    /// <summary>The lock comes after a 2-3 s close-up check and a payload slew: counted from its last
    /// sighting, the target was lost a second after the camera first looked for it.</summary>
    [Fact]
    public void Tracker_CountsTheCoastWhileLooking_NotSinceLastSeen()
    {
        var tracker = new GroundTracker(Origin) { CoastSeconds = 6 };
        var p = tracker.Projection;
        for (var i = 0; i < 5; i++)
            tracker.Update([Obs(p, 0, 0)], T0.AddSeconds(i * 0.1), i, _ => true);
        var target = tracker.Tracks.Single();
        target.Accepts = _ => true;

        // 5 s looking elsewhere (verifying, slewing), then 5 s looking where it should be.
        for (var i = 1; i <= 50; i++)
            tracker.Update([], T0.AddSeconds(0.4 + i * 0.1), 100 + i, _ => false);
        for (var i = 1; i <= 50; i++)
            tracker.Update([], T0.AddSeconds(5.4 + i * 0.1), 200 + i, _ => true);
        target.State.Should().Be(TrackState.Coasting, "only 5 s of the 6 s coast were spent looking");

        tracker.Update([Obs(p, 1, 0)], T0.AddSeconds(10.5), 300, _ => true);
        target.State.Should().Be(TrackState.Confirmed);
        target.MissedInViewSeconds.Should().Be(0);
    }

    [Fact]
    public void Tracker_NeverJumpsFurtherThanATargetCouldDrive()
    {
        var tracker = new GroundTracker(Origin) { MaxSpeedMps = 30, ReachMarginMeters = 5 };
        var p = tracker.Projection;
        for (var i = 0; i < 5; i++)
            tracker.Update([Obs(p, 0, 0)], T0.AddSeconds(i * 0.1), i, _ => true);
        var track = tracker.Tracks.Single();

        tracker.Update([Obs(p, 40, 0)], T0.AddSeconds(0.6), 6, _ => true); // 40 m in 0.2 s: impossible

        track.PositionLocal.Length.Should().BeLessThan(1);
        tracker.Tracks.Should().HaveCount(2, "the far sighting starts a track of its own");
    }

    [Fact]
    public void Tracker_ConfirmsACar_SeenInOnlyHalfTheFrames()
    {
        var tracker = new GroundTracker(Origin);
        var p = tracker.Projection;
        // Detected every other frame at 10 fps, as a small car from 4,000 ft is.
        for (var i = 0; i < 9; i++) // ends on a hit
            tracker.Update(i % 2 == 0 ? [Obs(p, 0, 0)] : [], T0.AddSeconds(i * 0.1), i, _ => true);

        tracker.Tracks.Should().ContainSingle().Which.State.Should().Be(TrackState.Confirmed);
    }

    [Fact]
    public void Tracker_DropsATentativeTrack_MissedForLongerThanItsGrace()
    {
        var tracker = new GroundTracker(Origin) { TentativeSeconds = 1 };
        var p = tracker.Projection;
        tracker.Update([Obs(p, 0, 0)], T0, 0, _ => true);
        tracker.Update([], T0.AddSeconds(0.5), 1, _ => true);
        tracker.Tracks.Single().State.Should().Be(TrackState.Tentative);
        tracker.Update([], T0.AddSeconds(1.2), 2, _ => true);
        tracker.Tracks.Single().State.Should().Be(TrackState.Lost);
    }

    [Fact]
    public void Tracker_AWeakDetectionContinuesATrack_ButNeverStartsOne()
    {
        var tracker = new GroundTracker(Origin) { HighScore = 0.5 };
        var p = tracker.Projection;
        tracker.Update([Obs(p, 0, 0, score: 0.3)], T0, 0, _ => true);
        tracker.Tracks.Should().BeEmpty();

        tracker.Update([Obs(p, 0, 0, score: 0.8)], T0.AddSeconds(0.1), 1, _ => true);
        tracker.Update([Obs(p, 0.5, 0, score: 0.3)], T0.AddSeconds(0.2), 2, _ => true);
        tracker.Tracks.Should().ContainSingle().Which.Hits.Should().Be(2);
    }

    [Fact]
    public void TargetSpec_TurnsWordsIntoClassesAndColours()
    {
        var van = TargetSpec.Parse("a white van")!;
        van.Classes.Should().Contain("Van");
        van.CouldBe("Van", "white").Should().BeTrue();
        van.CouldBe("SUV", "gray").Should().BeTrue();   // from above: easily confused; the verifier decides
        van.CouldBe("Van", "red").Should().BeFalse();
        van.CouldBe("Person", "white").Should().BeFalse();

        TargetSpec.Parse("red cars")!.CouldBe("Car", "red").Should().BeTrue();

        // Once locked: the class may flip (measured: the zoomed red car read "Truck" and "Bus"), the colour may not.
        var red = TargetSpec.Parse("red car")!;
        red.CouldBe("Truck", "red").Should().BeFalse("a search doesn't propose trucks for a car");
        red.CouldStillBe("Truck", "red").Should().BeTrue();
        red.CouldStillBe("Bus", "red").Should().BeTrue();
        red.CouldStillBe("Truck", "white").Should().BeFalse();
        red.CouldStillBe("Person", "red").Should().BeFalse();
        TargetSpec.Parse("pick-up truck")!.Classes.Should().Contain("Pickup Truck");
        TargetSpec.Parse("power grid antenna").Should().BeNull();
    }

    [Fact]
    public void LostTargetSearch_CoversAGrowingDisc_AroundWhereTheTargetShouldBe()
    {
        var search = new LostTargetSearch(Origin, new Vec2(10, 0), T0, 40, 6, 8, 160);
        var projection = new GeoProjection(Origin);

        projection.ToLocal(search.Center(T0.AddSeconds(4))).X.Should().BeApproximately(40, 1, "dead-reckoned east at 10 m/s");
        projection.ToLocal(search.Center(T0.AddSeconds(60))).X.Should().BeApproximately(80, 1, "but no further than 8 s of it");
        search.RadiusMeters(T0.AddSeconds(30)).Should().BeApproximately(40 + 10 * 30, 0.1);

        // The looks cover the disc: every point of it is within a look's reach (half its width).
        var pattern = LostTargetSearch.Pattern(300, 160);
        for (var x = -300.0; x <= 300; x += 25)
        for (var y = -300.0; y <= 300; y += 25)
            if (x * x + y * y <= 300 * 300)
                pattern.Min(p => (p - new Vec2(x, y)).Length).Should().BeLessThan(90);
    }

    [Theory]
    [InlineData(200, 30, 30, "red")]
    [InlineData(240, 240, 240, "white")]
    [InlineData(20, 20, 25, "black")]
    [InlineData(38, 45, 70, "black")]   // a black roof in daylight, faintly blue
    [InlineData(20, 45, 140, "blue")]   // a real dark-blue car stays blue
    [InlineData(40, 70, 190, "blue")]
    [InlineData(130, 130, 132, "gray")]
    public void ColourNamer_NamesAFlatColour(byte r, byte g, byte b, string expected)
    {
        using var bitmap = new SKBitmap(40, 40);
        bitmap.Erase(new SKColor(r, g, b));
        ColourNamer.Name(bitmap, new BoundingBox(0, 0, 1000, 1000)).Should().Be(expected);
    }

    [Fact]
    public void ColourNamer_IgnoresWindscreens_OnARedCar()
    {
        using var bitmap = new SKBitmap(40, 40);
        bitmap.Erase(new SKColor(190, 25, 30));
        using (var canvas = new SKCanvas(bitmap))
            canvas.DrawRect(12, 8, 16, 10, new SKPaint { Color = new SKColor(15, 15, 20) }); // dark glass in the middle
        ColourNamer.Name(bitmap, new BoundingBox(0, 0, 1000, 1000)).Should().Be("red");
    }

    // ----- The executive, end to end with scripted parts -----

    [Fact]
    public async Task Executive_FindsVerifiesLocksAndTracks_ThenLosesAndReacquires()
    {
        var world = new ScriptedWorld();
        var verifier = new ScriptedVerifier(isTarget: true);
        var payload = new RecordingPayload();
        var tracks = new RecordingTrackSink();
        var detections = new RecordingDetections();
        var now = T0;
        var task = new SearchTask("998", "m1", "ZoneA", "red car", 0.5, "http://sim/frames", 0, "http://sim/detections",
            ZoomUrl: null, Track: true, VideoSourceUrl: "http://sim/video", PayloadUrl: "http://sim/payload", TrackCallbackUrl: "http://sim/tracks");
        var runner = new TrackingRunner(task, TargetSpec.Parse("red car")!, world, new NoFrames(), new NoZoom(), verifier, payload, tracks, detections,
            new PerceptionOptions { CoastSeconds = 1.5, ReportIntervalSeconds = 1 }, NullLogger.Instance, () => now);

        async Task Run(double fromSeconds, double toSeconds, bool visible)
        {
            for (var t = fromSeconds; t < toSeconds; t += 0.1)
            {
                now = T0.AddSeconds(t);
                world.Visible = visible;
                world.CarEast = 8 * t; // 8 m/s east
                await runner.ProcessAsync(world.Frame(now), CancellationToken.None);
                await runner.Verification;
            }
        }

        await Run(0, 2, visible: true);
        runner.Phase.Should().Be(ExecutivePhase.Tracking);
        detections.Sent.Should().ContainSingle().Which.TrackId.Should().Be("T-1");
        tracks.Sent.Should().Contain(r => r.State == TargetTrackStates.Tracking && r.TrackId == "T-1");
        payload.Points.Should().NotBeEmpty();
        payload.Zooms.Should().Contain(new PerceptionOptions().TrackGroundWidthMeters);

        await Run(2, 5, visible: false); // under trees
        tracks.Sent.Should().Contain(r => r.State == TargetTrackStates.Lost);
        runner.Phase.Should().Be(ExecutivePhase.Reacquiring);

        await Run(5, 8, visible: true);
        runner.Phase.Should().Be(ExecutivePhase.Tracking);
        tracks.Sent.Last().TrackId.Should().Be("T-1"); // the same id carries on
        tracks.Sent.Last().State.Should().Be(TargetTrackStates.Tracking);
        verifier.Calls.Should().Be(2); // once to find it, once to take it back - never per frame
    }

    [Fact]
    public async Task Executive_NeverLocks_OnACandidateTheVerifierRejects()
    {
        var world = new ScriptedWorld();
        var tracks = new RecordingTrackSink();
        var detections = new RecordingDetections();
        var now = T0;
        var task = new SearchTask("998", "m1", "ZoneA", "red car", 0.5, "f", 0, "d", Track: true, VideoSourceUrl: "v", PayloadUrl: "p", TrackCallbackUrl: "t");
        var verifier = new ScriptedVerifier(isTarget: false);
        var runner = new TrackingRunner(task, TargetSpec.Parse("red car")!, world, new NoFrames(), new NoZoom(), verifier,
            new RecordingPayload(), tracks, detections, new PerceptionOptions(), NullLogger.Instance, () => now);

        for (var t = 0.0; t < 3; t += 0.1)
        {
            now = T0.AddSeconds(t);
            world.CarEast = 8 * t;
            await runner.ProcessAsync(world.Frame(now), CancellationToken.None);
            await runner.Verification;
        }

        runner.Phase.Should().Be(ExecutivePhase.Searching);
        detections.Sent.Should().BeEmpty();
        tracks.Sent.Should().BeEmpty();
        verifier.Calls.Should().Be(1); // a rejected track isn't asked about again
    }

    /// <summary>One red car on the ground, seen by a camera fixed over the origin; the "detector"
    /// projects it into the frame with the same camera model the real pipeline uses.</summary>
    private sealed class ScriptedWorld : IObjectDetector
    {
        private static readonly byte[] Jpeg = MakeJpeg();
        private FrameTelemetry? _frame;
        private long _seq;

        public double CarEast { get; set; }
        public bool Visible { get; set; } = true;
        public string Name => "scripted";
        public int InputSize => 640;

        public CameraFrame Frame(DateTime at)
        {
            _frame = new FrameTelemetry(++_seq, at, Origin.Lat, Origin.Lng, 1000, 0, 20, 640, 480, "m1");
            return new CameraFrame(_frame, Jpeg);
        }

        public IReadOnlyList<DetectedObject> Detect(SKBitmap image, double minScore)
        {
            if (!Visible)
                return [];
            var camera = new CameraModel(_frame!);
            var car = new GeoProjection(Origin).ToGeo(new Vec2(CarEast, 0));
            var (x, y) = camera.GeoToPixel(car);
            var half = 2.25 / camera.MetersPerPixel;
            return [new DetectedObject("Car", 0.8, new BoundingBox(
                (x - half) / camera.Width * 1000, (y - half) / camera.Height * 1000,
                (x + half) / camera.Width * 1000, (y + half) / camera.Height * 1000))];
        }

        /// <summary>A red frame: every box in it reads red.</summary>
        private static byte[] MakeJpeg()
        {
            using var bitmap = new SKBitmap(640, 480);
            bitmap.Erase(new SKColor(200, 30, 30));
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
            return data.ToArray();
        }
    }

    private sealed class ScriptedVerifier(bool isTarget) : IVerifier
    {
        private int _calls;
        public int Calls => _calls;

        public Task<Verdict> VerifyAsync(byte[] closeUp, double metersAcross, string target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new Verdict(isTarget, isTarget ? "red car" : "red truck", 5));
        }
    }

    private sealed class RecordingPayload : IPayloadControl
    {
        public ConcurrentQueue<(double Lat, double Lng)> Points { get; } = new();
        public ConcurrentQueue<double> Zooms { get; } = new();

        public Task PointAtAsync(SearchTask task, double lat, double lng, CancellationToken cancellationToken)
        {
            Points.Enqueue((lat, lng));
            return Task.CompletedTask;
        }

        public Task ZoomAsync(SearchTask task, double groundWidthMeters, CancellationToken cancellationToken)
        {
            Zooms.Enqueue(groundWidthMeters);
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(SearchTask task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingTrackSink : ITrackSink
    {
        public List<TargetTrackReport> Sent { get; } = [];

        public Task SendAsync(string callbackUrl, TargetTrackReport report, CancellationToken cancellationToken)
        {
            lock (Sent)
                Sent.Add(report);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDetections : IDetectionSink
    {
        public List<OnboardDetection> Sent { get; } = [];

        public Task SendAsync(string callbackUrl, OnboardDetection detection, CancellationToken cancellationToken)
        {
            lock (Sent)
                Sent.Add(detection);
            return Task.CompletedTask;
        }
    }

    private sealed class NoFrames : IFrameSource
    {
        public Task<CameraFrame?> NextAsync(string sourceUrl, long afterSeq, CancellationToken cancellationToken) => Task.FromResult<CameraFrame?>(null);
    }

    private sealed class NoZoom : IZoomCamera
    {
        public Task<byte[]?> CaptureAsync(string zoomUrl, double lat, double lng, double widthMeters, int pixels, CancellationToken cancellationToken,
            long? frameSeq = null) => Task.FromResult<byte[]?>(null);
    }
}
