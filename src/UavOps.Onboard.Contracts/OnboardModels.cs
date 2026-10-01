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
/// <param name="Track">Find and track: once the target is found and verified, lock the payload on it
/// and keep reporting where it is (<see cref="TargetTrackReport"/>) instead of searching on.</param>
/// <param name="VideoSourceUrl">The payload's live video (<c>{url}/next?after=N</c>, the newest frame,
/// same headers as survey frames), for the every-frame detector, and its close-ups
/// (<c>{url}/zoom</c>, like <paramref name="ZoomUrl"/> but <c>?seq=</c> is a video frame's number -
/// video and survey frames are numbered separately). Null: survey frames only.</param>
/// <param name="PayloadUrl">The payload's control, for the onboard computer to point and zoom it
/// (<see cref="PayloadPaths"/>). Null: it can't move the payload.</param>
/// <param name="TrackCallbackUrl">Where to POST each <see cref="TargetTrackReport"/>.</param>
public sealed record SearchTask(
    string TailNumber,
    string MissionId,
    string ZoneName,
    string Prompt,
    double MinConfidence,
    string FrameSourceUrl,
    long FromSeq,
    string DetectionCallbackUrl,
    string? ZoomUrl = null,
    bool Track = false,
    string? VideoSourceUrl = null,
    string? PayloadUrl = null,
    string? TrackCallbackUrl = null);

/// <summary>The states a tracked target is reported in.</summary>
public static class TargetTrackStates
{
    /// <summary>Seen in the last frames; the position is measured.</summary>
    public const string Tracking = "Tracking";

    /// <summary>Not seen for a moment (under trees, behind a building): the position is predicted.</summary>
    public const string Coasting = "Coasting";

    /// <summary>Not seen for a while: the onboard computer searches around where it should be; the
    /// position is the centre of that search (it moves with the target's last known motion).</summary>
    public const string Lost = "Lost";

    /// <summary>Not found again: given up. The aircraft resumes its search route.</summary>
    public const string Released = "Released";
}

/// <summary>Where a tracked target is, from the onboard computer to the aircraft: about once a
/// second while tracking, and on every state change. "The tool that gives the target's location."</summary>
public sealed record TargetTrackReport(
    string TailNumber,
    string MissionId,
    string ZoneName,
    string Prompt,
    string TrackId,
    string Label,
    string State,
    double Lat,
    double Lng,
    double SpeedMps,
    double? HeadingDeg,
    double Confidence,
    DateTime SeenAtUtc);

/// <summary>The payload control the onboard computer drives, relative to <see cref="SearchTask.PayloadUrl"/>
/// (JSON bodies). On a real UAV these are the gimbal's own commands.</summary>
public static class PayloadPaths
{
    /// <summary>POST <see cref="PointAtCommand"/>: hold this ground point in the centre.</summary>
    public const string Point = "point";

    /// <summary>POST <see cref="ZoomCommand"/>.</summary>
    public const string Zoom = "zoom";

    /// <summary>POST, no body: back to straight down, widest.</summary>
    public const string Release = "release";
}

public sealed record PointAtCommand(double Lat, double Lng);

/// <summary>Zoom so the frame shows about <paramref name="GroundWidthMeters"/> across.</summary>
public sealed record ZoomCommand(double GroundWidthMeters);

/// <summary>Per-stage timing of the every-frame loop, for GET /tasks (ms, recent average).</summary>
public sealed record PipelineTiming(double FetchMs, double DecodeMs, double DetectMs, double TrackMs, double Fps, int Tracks, string? Detector);

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
    long ModelLatencyMs,
    bool FromVideo = false);

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
    string? LastError,
    string? Phase = null,
    string? TargetTrackId = null,
    PipelineTiming? Timing = null);

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
