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

    public string OnboardDetectorUrl { get; set; } = "http://localhost:5280";

    /// <summary>This simulator's own address as the detector should reach it (frames, callbacks).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5270";

    public bool UsesOnboardDetector => Detector.Equals("Onboard", StringComparison.OrdinalIgnoreCase);

    /// <summary>How much drawn traffic streets get, as a fraction of a busy town's (1). Where a real
    /// aerial photo covers the ground its own vehicles are the traffic, and none is drawn.</summary>
    public double TrafficDensity { get; set; } = 0.15;

    /// <summary>Real vehicles cut from the aerial photos, for scenario objects to look like.</summary>
    public List<Imagery.VehiclePhotoConfig> VehiclePhotos { get; set; } = [];
}

/// <summary>Something on the ground to find: what the camera draws (a vehicle of
/// <see cref="Kind"/> in <see cref="Color"/>, facing <see cref="HeadingDeg"/>) and what the
/// simulated detector matches (<see cref="Tags"/>).</summary>
public sealed record ScenarioObject(string Id, string Label, IReadOnlyList<string> Tags, double Lat, double Lng,
    string Kind, string Color, double HeadingDeg, string? Photo = null);

/// <summary>The "Scenario" section: <c>Objects</c>, plus anything placed from the page.</summary>
public sealed class ScenarioOptions
{
    public const string SectionName = "Scenario";
    public List<ScenarioObjectConfig> Objects { get; set; } = [];
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
            Add(o.Label, o.Tags, o.Lat, o.Lng, string.IsNullOrWhiteSpace(o.Id) ? null : o.Id, o.Kind, o.Color, o.HeadingDeg, o.Photo);
    }

    public IReadOnlyList<ScenarioObject> All()
    {
        lock (_lock) return _objects.ToList();
    }

    /// <summary>Tags default to the label's words, so "white van" is found by "white van"; kind and
    /// colour default to the label's words too ("red car" is a red car), and heading to a fixed
    /// angle per object.</summary>
    public ScenarioObject Add(string label, IEnumerable<string>? tags, double lat, double lng, string? id = null,
        string? kind = null, string? color = null, double? headingDeg = null, string? photo = null)
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
            var obj = new ScenarioObject(objectId, label.Trim(), tagList, lat, lng, kind.ToLowerInvariant(), color.ToLowerInvariant(), heading, photo);
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
