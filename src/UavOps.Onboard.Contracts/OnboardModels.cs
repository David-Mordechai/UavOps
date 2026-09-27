namespace UavOps.Onboard.Contracts;

// What the aircraft and its onboard detection service (a Jetson next to the camera) say to each
// other. The ground never sees these: the aircraft turns an OnboardDetection into the fleet app's
// DetectionReport, the same contract the host already handles.

/// <summary>"Look for this", sent by the aircraft when a search target is set (PUT /tasks/{tail}).</summary>
/// <param name="FrameSourceUrl">Where to pull camera frames from (see <see cref="FrameHeaders"/>).</param>
/// <param name="FromSeq">Only frames after this sequence number belong to this search.</param>
/// <param name="DetectionCallbackUrl">Where to POST each <see cref="OnboardDetection"/>.</param>
/// <param name="ZoomUrl">The payload's zoom (<c>GET ?lat=&amp;lng=&amp;widthMeters=</c>, a JPEG close-up
/// of that ground point); null if it has none, and candidates are cropped from the frame instead.</param>
public sealed record SearchTask(
    string TailNumber,
    string MissionId,
    string ZoneName,
    string Prompt,
    double MinConfidence,
    string FrameSourceUrl,
    long FromSeq,
    string DetectionCallbackUrl,
    string? ZoomUrl = null);

/// <summary>Where the camera was and how it was pointed when a frame was taken - what real UAV
/// video carries as MISB KLV metadata. The camera looks straight down, image top = heading.</summary>
public sealed record FrameTelemetry(
    long Seq,
    DateTime CapturedAtUtc,
    double Lat,
    double Lng,
    double AltitudeFt,
    double HeadingDeg,
    double HFovDeg,
    int Width,
    int Height,
    string? MissionId);

/// <summary>A box in a frame, normalized 0-1000 on both axes (x right, y down).</summary>
public sealed record BoundingBox(double X1, double Y1, double X2, double Y2)
{
    public double CenterX => (X1 + X2) / 2;
    public double CenterY => (Y1 + Y2) / 2;
}

/// <summary>One object found, already placed on the ground. Posted to the aircraft once per
/// object per search (the service tracks repeats across overlapping frames).</summary>
public sealed record OnboardDetection(
    string TailNumber,
    string MissionId,
    string ZoneName,
    string Prompt,
    string Label,
    double Confidence,
    double Lat,
    double Lng,
    DateTime DetectedAtUtc,
    string TrackId,
    long FrameSeq,
    BoundingBox Box,
    long ModelLatencyMs);

/// <summary>What the service is doing for one UAV (GET /tasks).</summary>
/// <param name="AnalyzedThroughSeq">Every frame up to this one has been analysed (frames are
/// analysed a few at a time, so later ones can finish first).</param>
public sealed record SearchTaskStatus(
    string TailNumber,
    string MissionId,
    string Prompt,
    long AnalyzedThroughSeq,
    int FramesAnalyzed,
    int Detections,
    long LastLatencyMs,
    string? LastError);

/// <summary>Response headers that carry <see cref="FrameTelemetry"/> alongside a JPEG frame.</summary>
public static class FrameHeaders
{
    public const string Seq = "X-Frame-Seq";
    public const string CapturedAtUtc = "X-Frame-Captured-Utc";
    public const string Lat = "X-Frame-Lat";
    public const string Lng = "X-Frame-Lng";
    public const string AltitudeFt = "X-Frame-Altitude-Ft";
    public const string HeadingDeg = "X-Frame-Heading-Deg";
    public const string HFovDeg = "X-Frame-HFov-Deg";
    public const string Width = "X-Frame-Width";
    public const string Height = "X-Frame-Height";
    public const string MissionId = "X-Frame-Mission-Id";
}
