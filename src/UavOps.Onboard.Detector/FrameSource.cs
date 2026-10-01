using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using UavOps.Onboard.Contracts;

namespace UavOps.Onboard.Detector;

public sealed record CameraFrame(FrameTelemetry Telemetry, byte[] Jpeg);

/// <summary>Where camera frames come from. Today the aircraft's survey frames over HTTP
/// (<see cref="HttpSurveyFrameSource"/>); on the Jetson, a camera stream with KLV telemetry
/// would implement this without changing <see cref="SearchTaskRunner"/>.</summary>
public interface IFrameSource
{
    /// <summary>The next frame after <paramref name="afterSeq"/>, or null if none arrived in time.</summary>
    Task<CameraFrame?> NextAsync(string sourceUrl, long afterSeq, CancellationToken cancellationToken);
}

/// <summary>The payload's zoom: a close-up JPEG of a ground point, or null if it can't take one
/// (out of range, no zoom).</summary>
public interface IZoomCamera
{
    /// <summary>A close-up of a ground point. <paramref name="frameSeq"/> is the survey frame the
    /// point was seen in: a moving object is looked at as it was then (the payload tracking it
    /// while it slews), not wherever it has driven to since.</summary>
    Task<byte[]?> CaptureAsync(string zoomUrl, double lat, double lng, double widthMeters, int pixels, CancellationToken cancellationToken,
        long? frameSeq = null);
}

/// <summary>Where each object found goes: the aircraft's callback.</summary>
public interface IDetectionSink
{
    Task SendAsync(string callbackUrl, OnboardDetection detection, CancellationToken cancellationToken);
}

/// <summary>Long-polls <c>{source}/next?after=N</c>: a JPEG with its telemetry in
/// <see cref="FrameHeaders"/>, or 204 when no new frame came in time.</summary>
public sealed class HttpSurveyFrameSource(HttpClient http, DetectorOptions options) : IFrameSource
{
    public async Task<CameraFrame?> NextAsync(string sourceUrl, long afterSeq, CancellationToken cancellationToken)
    {
        var url = $"{sourceUrl.TrimEnd('/')}/next?after={afterSeq}&waitMs={options.FramePollWaitMs}";
        using var response = await http.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;
        response.EnsureSuccessStatusCode();
        var jpeg = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new CameraFrame(ReadTelemetry(response), jpeg);
    }

    private static FrameTelemetry ReadTelemetry(HttpResponseMessage response)
    {
        string Header(string name) =>
            response.Headers.TryGetValues(name, out var values) ? values.First()
            : throw new InvalidDataException($"Frame response is missing the {name} header.");
        double D(string name) => double.Parse(Header(name), CultureInfo.InvariantCulture);
        return new FrameTelemetry(
            long.Parse(Header(FrameHeaders.Seq), CultureInfo.InvariantCulture),
            DateTime.Parse(Header(FrameHeaders.CapturedAtUtc), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            D(FrameHeaders.Lat), D(FrameHeaders.Lng), D(FrameHeaders.AltitudeFt), D(FrameHeaders.HeadingDeg), D(FrameHeaders.HFovDeg),
            int.Parse(Header(FrameHeaders.Width), CultureInfo.InvariantCulture),
            int.Parse(Header(FrameHeaders.Height), CultureInfo.InvariantCulture),
            response.Headers.TryGetValues(FrameHeaders.MissionId, out var mission) ? mission.First() : null);
    }
}

public sealed class HttpZoomCamera(HttpClient http) : IZoomCamera
{
    public async Task<byte[]?> CaptureAsync(string zoomUrl, double lat, double lng, double widthMeters, int pixels, CancellationToken cancellationToken,
        long? frameSeq = null)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"{zoomUrl}?lat={lat:R}&lng={lng:R}&widthMeters={widthMeters:F1}&pixels={pixels}");
        if (frameSeq is { } seq)
            url += string.Create(CultureInfo.InvariantCulture, $"&seq={seq}");
        using var response = await http.GetAsync(url, cancellationToken);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken) : null;
    }
}

public sealed class HttpDetectionSink(HttpClient http) : IDetectionSink
{
    public async Task SendAsync(string callbackUrl, OnboardDetection detection, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(callbackUrl, detection, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
