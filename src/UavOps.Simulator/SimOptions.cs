namespace UavOps.Simulator;

/// <summary>The "Simulator" section of appsettings.json.</summary>
public sealed class SimOptions
{
    public const string SectionName = "Simulator";

    /// <summary>UavOps.Agent's fleet hub - the same endpoint the real fleet app connects to.</summary>
    public string HostHubUrl { get; set; } = "http://localhost:5262/uavCommandHub";

    /// <summary>The AOI database to draw zones from. Blank means the same default McpMoav uses.</summary>
    public string AoiDatabasePath { get; set; } = "";

    /// <summary>Sim seconds per real second; changeable live from the page.</summary>
    public double TimeScale { get; set; } = 1;

    public int TickMilliseconds { get; set; } = 100;
    public int StatePushMilliseconds { get; set; } = 200;

    /// <summary>A waypoint counts as reached within this distance.</summary>
    public double ArrivalRadiusMeters { get; set; } = 20;

    /// <summary>Radius of the holding circle a UAV flies when it has nowhere to go.</summary>
    public double OrbitRadiusMeters { get; set; } = 400;

    /// <summary>The payload camera's horizontal field of view at zoom 1 (widest), and how far it
    /// zooms: SetPayloadZoom divides the field of view by the zoom. Reported in telemetry, so
    /// McpMoav plans search lanes from what the camera actually sees.</summary>
    public double PayloadWideHorizontalFovDeg { get; set; } = 40;
    public double PayloadMaxZoom { get; set; } = 30;

    /// <summary>Size of a zoom close-up (<c>/api/uavs/{tail}/zoom</c>), square.</summary>
    public int ZoomPixels { get; set; } = 512;

    /// <summary>How far from the UAV the payload can slew and zoom onto a point.</summary>
    public double MaxZoomRangeMeters { get; set; } = 5000;
    public int CameraWidth { get; set; } = 1280;
    public int CameraHeight { get; set; } = 960;

    /// <summary>How much consecutive survey frames overlap along track (0.3 = 30% of a frame),
    /// so an object on a frame's edge is whole in the next one.</summary>
    public double SurveyFrameOverlap { get; set; } = 0.3;

    /// <summary>Survey frames kept per UAV for the detector to pull.</summary>
    public int SurveyFramesKept { get; set; } = 400;

    /// <summary>The live camera view's frame rate (wall clock).</summary>
    public double LiveCameraFps { get; set; } = 5;

    /// <summary><c>Onboard</c>: the onboard detection service (UavOps.Onboard.Detector) searches
    /// the camera frames with a vision model. <c>Simulated</c>: objects are "seen" by tag match
    /// inside the footprint, no model needed.</summary>
    public string Detector { get; set; } = "Onboard";

    /// <summary>The onboard computer: everything onboard (detector, tracker, executive, verifier
    /// VLM) runs there - the Jetson Orin Nano by default. Point it elsewhere (a local
    /// UavOps.Onboard.Detector for offline dev) by changing this one setting.</summary>
    public string OnboardUrl { get; set; } = "http://192.168.1.154:5280";

    /// <summary><c>SignalR</c> (default): talk to the onboard computer through the ground agent's
    /// onboard hub (<see cref="OnboardHubUrl"/>); both sides dial the agent, so the onboard computer
    /// needs no open port. <c>Http</c>: call <see cref="OnboardUrl"/> directly.</summary>
    public string OnboardLink { get; set; } = "SignalR";

    public bool UsesOnboardHub => OnboardLink.Equals("SignalR", StringComparison.OrdinalIgnoreCase);

    /// <summary>The ground agent's onboard hub: the host of <see cref="HostHubUrl"/>, at
    /// <see cref="UavOps.Onboard.Contracts.OnboardLink.HubPath"/>.</summary>
    public string OnboardHubUrl =>
        new Uri(new Uri(HostHubUrl), UavOps.Onboard.Contracts.OnboardLink.HubPath).ToString();

    /// <summary>This simulator's own address as the onboard computer reaches it (video, frames,
    /// payload control, callbacks). Blank: this machine's LAN address on the route to
    /// <see cref="OnboardUrl"/>, and the port it listens on.</summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>The live video the onboard computer's every-frame detector reads (sim time is
    /// rendered, wall-clock rate), while a UAV's onboard agent is looking; frames kept per UAV.</summary>
    public double OnboardVideoFps { get; set; } = 10;
    public int VideoFramesKept { get; set; } = 60;

    public bool UsesOnboardDetector => Detector.Equals("Onboard", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fills in a blank <see cref="PublicBaseUrl"/>: this machine's address on the route to the
    /// onboard computer (asked of the OS by "connecting" a UDP socket, which sends nothing), with the
    /// port this app listens on. The onboard computer is another machine on the LAN, so localhost
    /// wouldn't reach back here.
    /// </summary>
    public void ResolvePublicBaseUrl(string? listenUrls)
    {
        if (!string.IsNullOrWhiteSpace(PublicBaseUrl))
            return;
        var port = 5270;
        var first = listenUrls?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is not null && Uri.TryCreate(first.Replace("0.0.0.0", "localhost").Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var listen))
            port = listen.Port;
        var host = "localhost";
        if (Uri.TryCreate(OnboardUrl, UriKind.Absolute, out var onboard) && !onboard.IsLoopback)
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
                socket.Connect(onboard.Host, onboard.Port);
                host = ((System.Net.IPEndPoint)socket.LocalEndPoint!).Address.ToString();
            }
            catch (System.Net.Sockets.SocketException)
            {
                // No route: fall back to localhost, which works when the onboard service is local.
            }
        }
        PublicBaseUrl = $"http://{host}:{port}";
    }

    /// <summary>How much drawn traffic streets get, as a fraction of a busy town's (1). Where a real
    /// aerial photo covers the ground its own vehicles are the traffic, and none is drawn.</summary>
    public double TrafficDensity { get; set; } = 0.15;

    /// <summary>Where moving traffic drives: around each of these points (default: the UAV base and
    /// both zones, and 999's forward point by ZoneB), <see cref="TrafficAreaRadiusMeters"/> each way.
    /// The roads are read from the offline map once at startup.</summary>
    public List<TrafficArea> TrafficAreas { get; set; } = [];
    public double TrafficAreaRadiusMeters { get; set; } = 6000;

    /// <summary>Moving traffic over the real aerial photos too. Off by default: the photos have their
    /// own vehicles, and a drawn red car driving through a zone is one more hit for a "red car" search.</summary>
    public bool TrafficOverPhotos { get; set; }

    /// <summary>The payload's slew time before a close-up (sim seconds): a close-up of a candidate is
    /// taken as at the survey frame it was seen in plus this, so a moving car is still in it.</summary>
    public double CloseUpDelaySeconds { get; set; } = 0.5;

    /// <summary>Real vehicles cut from the aerial photos, for scenario objects to look like.</summary>
    public List<Imagery.VehiclePhotoConfig> VehiclePhotos { get; set; } = [];
}

/// <summary>Something on the ground to find: what the camera draws (a vehicle of
/// <see cref="Kind"/> in <see cref="Color"/>, facing <see cref="HeadingDeg"/>) and what the
/// simulated detector matches (<see cref="Tags"/>). With <see cref="SpeedKmh"/> above 0 it drives
/// the road it was placed on (<see cref="World.GroundWorld"/>); Lat/Lng/HeadingDeg are then where it
/// was placed, and <see cref="World.GroundWorld.ObjectsAt"/> gives where it is.</summary>
public sealed record ScenarioObject(string Id, string Label, IReadOnlyList<string> Tags, double Lat, double Lng,
    string Kind, string Color, double HeadingDeg, string? Photo = null, double SpeedKmh = 0, double DriveMeters = ScenarioObject.DefaultDriveMeters)
{
    /// <summary>How far a driving object goes along its road each way from where it was placed,
    /// back and forth: far enough to be seen moving, short enough to stay in its search area.</summary>
    public const double DefaultDriveMeters = 800;
}

/// <summary>The "Scenario" section: <c>Objects</c>, plus anything placed from the page.</summary>
public sealed class ScenarioOptions
{
    public const string SectionName = "Scenario";
    public List<ScenarioObjectConfig> Objects { get; set; } = [];
}

public sealed class TrafficArea
{
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
}

public sealed class ScenarioObjectConfig
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string? Kind { get; set; }
    public string? Color { get; set; }
    public double? HeadingDeg { get; set; }
    /// <summary>A <c>Simulator:VehiclePhotos</c> name; default: the first photo of this kind and colour.</summary>
    public string? Photo { get; set; }
    /// <summary>Above 0: it drives the nearest road back and forth at this speed. 0: parked.</summary>
    public double SpeedKmh { get; set; }
    /// <summary>How far it drives each way from where it was placed (default 800 m).</summary>
    public double? DriveMeters { get; set; }
}

public sealed class ScenarioStore
{
    private readonly object _lock = new();
    private readonly List<ScenarioObject> _objects = [];
    private int _nextId = 1;

    private readonly IReadOnlyList<Imagery.VehiclePhotoConfig> _photos;

    public ScenarioStore(ScenarioOptions options, IReadOnlyList<Imagery.VehiclePhotoConfig>? photos = null)
    {
        _photos = photos ?? [];
        foreach (var o in options.Objects)
            Add(o.Label, o.Tags, o.Lat, o.Lng, string.IsNullOrWhiteSpace(o.Id) ? null : o.Id, o.Kind, o.Color, o.HeadingDeg, o.Photo, o.SpeedKmh, o.DriveMeters);
    }

    public IReadOnlyList<ScenarioObject> All()
    {
        lock (_lock) return _objects.ToList();
    }

    /// <summary>Tags default to the label's words, so "white van" is found by "white van"; kind and
    /// colour default to the label's words too ("red car" is a red car), and heading to a fixed
    /// angle per object.</summary>
    public ScenarioObject Add(string label, IEnumerable<string>? tags, double lat, double lng, string? id = null,
        string? kind = null, string? color = null, double? headingDeg = null, string? photo = null, double speedKmh = 0,
        double? driveMeters = null)
    {
        var tagList = (tags ?? []).Concat(TextWords.Of(label)).Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0).Distinct().ToList();
        var words = TextWords.Of(label).ToList();
        kind ??= words.FirstOrDefault(w => Enum.TryParse<Camera.VehicleKind>(w, ignoreCase: true, out _)) ?? "car";
        color ??= words.FirstOrDefault(Camera.VehicleSprites.Colors.ContainsKey) ?? "grey";
        lock (_lock)
        {
            var objectId = id ?? $"obj-{_nextId++}";
            // Stable across runs (string.GetHashCode isn't).
            var heading = headingDeg ?? objectId.Aggregate(17u, (h, c) => unchecked(h * 31 + c)) % 360;
            // A real photo of a vehicle like this one, when there is one.
            photo ??= _photos.FirstOrDefault(p => p.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)
                                                 && p.Color.Equals(color, StringComparison.OrdinalIgnoreCase))?.Name;
            var obj = new ScenarioObject(objectId, label.Trim(), tagList, lat, lng, kind.ToLowerInvariant(), color.ToLowerInvariant(), heading, photo,
                Math.Clamp(speedKmh, 0, 150), Math.Clamp(driveMeters ?? ScenarioObject.DefaultDriveMeters, 50, 20_000));
            _objects.RemoveAll(o => o.Id == obj.Id);
            _objects.Add(obj);
            return obj;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock) return _objects.RemoveAll(o => o.Id == id) > 0;
    }
}

public static class TextWords
{
    private static readonly HashSet<string> Filler = ["a", "an", "the", "any", "some", "for", "of"];

    /// <summary>Lowercase words, filler dropped: "a White Van" → white, van.</summary>
    public static IEnumerable<string> Of(string text) =>
        text.ToLowerInvariant()
            .Split(c => !char.IsLetterOrDigit(c))
            .Where(w => w.Length > 0 && !Filler.Contains(w));

    private static string[] Split(this string text, Func<char, bool> isSeparator)
    {
        var words = new List<string>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || isSeparator(text[i]))
            {
                if (i > start)
                    words.Add(text[start..i]);
                start = i + 1;
            }
        }
        return words.ToArray();
    }
}
