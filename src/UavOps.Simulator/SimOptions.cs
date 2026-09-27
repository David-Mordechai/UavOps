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

    public double CameraHorizontalFovDeg { get; set; } = 60;
}

/// <summary>Something on the ground the simulated detector can find.</summary>
public sealed record ScenarioObject(string Id, string Label, IReadOnlyList<string> Tags, double Lat, double Lng);

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
}

public sealed class ScenarioStore
{
    private readonly object _lock = new();
    private readonly List<ScenarioObject> _objects = [];
    private int _nextId = 1;

    public ScenarioStore(ScenarioOptions options)
    {
        foreach (var o in options.Objects)
            Add(o.Label, o.Tags, o.Lat, o.Lng, string.IsNullOrWhiteSpace(o.Id) ? null : o.Id);
    }

    public IReadOnlyList<ScenarioObject> All()
    {
        lock (_lock) return _objects.ToList();
    }

    /// <summary>Tags default to the label's words, so "white van" is found by "white van".</summary>
    public ScenarioObject Add(string label, IEnumerable<string>? tags, double lat, double lng, string? id = null)
    {
        var tagList = (tags ?? []).Concat(TextWords.Of(label)).Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0).Distinct().ToList();
        lock (_lock)
        {
            var obj = new ScenarioObject(id ?? $"obj-{_nextId++}", label.Trim(), tagList, lat, lng);
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
