using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UavOps.Agent.McpMoav;

/// <summary>
/// Locations that exist only because something happened at runtime, today only search detections
/// ("the white van", "detection 1"), so the operator can target them by name: "send 998 to the
/// white van". <see cref="MoavTools"/> checks this before a location reaches either
/// <c>IOperationService</c> backend and rewrites a match to a <c>"lat,lng"</c> literal, which both
/// accept (see <c>KnownPoints.TryResolve</c>). Known points win, so a detection can't shadow
/// 'alpha' or 'home'. The newest registration of a name wins. Filled by
/// <see cref="MissionEventService"/>.
/// </summary>
public sealed class DetectionPointRegistry
{
    private readonly ConcurrentDictionary<string, (double Lat, double Lng)> _points = new();

    public void Register(double lat, double lng, params string[] names)
    {
        foreach (var name in names)
        {
            var key = Key(name);
            if (key.Length > 0)
                _points[key] = (lat, lng);
        }
    }

    public bool TryResolve(string name, out string latLng)
    {
        if (_points.TryGetValue(Key(name), out var point))
        {
            latLng = FormatLatLng(point.Lat, point.Lng);
            return true;
        }

        latLng = "";
        return false;
    }

    /// <summary>The literal form backends parse: "31.81234,34.66123".</summary>
    public static string FormatLatLng(double lat, double lng) =>
        string.Create(CultureInfo.InvariantCulture, $"{lat:F5},{lng:F5}");

    private static readonly Regex LeadingFiller = new(@"^(the|a|an|target)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // "The White  Van" and "white van" are the same point; "the"/"target" are filler, the way
    // KnownPoints already treats "target alpha".
    private static string Key(string name) =>
        LeadingFiller.Replace(Whitespace.Replace(name.Trim(), " "), "").ToLowerInvariant();
}
