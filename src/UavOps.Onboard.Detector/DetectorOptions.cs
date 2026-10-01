namespace UavOps.Onboard.Detector;

/// <summary>The "Vlm" section: the vision-language model, behind an OpenAI-compatible
/// <c>/v1/chat/completions</c> (vLLM on the GX10 in dev; vLLM or llama.cpp's server on the Jetson).</summary>
public sealed class VlmOptions
{
    public const string SectionName = "Vlm";

    public string Endpoint { get; set; } = "http://localhost:8000/v1";
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxTokens { get; set; } = 400;

    /// <summary>Sent as <c>chat_template_kwargs.enable_thinking=false</c>: a reasoning model
    /// otherwise spends seconds per frame thinking before it answers.</summary>
    public bool DisableThinking { get; set; } = true;
}

/// <summary>
/// The "Perception" section: the fast every-frame pipeline (<see cref="Autonomy.TrackingRunner"/>).
/// Used when an engine is configured and present and the target is something the detector knows;
/// otherwise a search falls back to the vision-model pipeline (<see cref="SearchTaskRunner"/>).
/// </summary>
public sealed class PerceptionOptions
{
    public const string SectionName = "Perception";

    /// <summary>The TensorRT engine (built on the device by trtexec), and the model's HuggingFace
    /// config.json for its labels. Blank or missing: no fast pipeline.</summary>
    public string EnginePath { get; set; } = "";
    public string LabelsPath { get; set; } = "";

    /// <summary>Detections below this are ignored; from <see cref="HighScore"/> a detection can start
    /// a track, below it only continue one (ByteTrack).</summary>
    public double MinScore { get; set; } = 0.2;
    public double HighScore { get; set; } = 0.35;

    /// <summary>The detector runs on this grid of tiles of the full-resolution frame, neighbours
    /// overlapping by <see cref="TileOverlapPx"/> (<see cref="Perception.TiledDetector"/>): 2x2 gives
    /// a car twice the pixels per side. 1x1 runs on the whole frame shrunk to the engine's size.</summary>
    public int TileColumns { get; set; } = 2;
    public int TileRows { get; set; } = 2;
    public int TileOverlapPx { get; set; } = 64;

    /// <summary>Position noise floor for a detection placed on the ground (m); it grows with the
    /// frame's metres per pixel.</summary>
    public double MinPositionSigmaMeters { get; set; } = 1.5;

    /// <summary>A confirmed track missed in view coasts on its prediction this long, then is lost;
    /// lost tracks are forgotten after <see cref="TrackMemorySeconds"/>.</summary>
    public double CoastSeconds { get; set; } = 6;
    public double TrackMemorySeconds { get; set; } = 60;

    /// <summary>Verifier close-ups: 2.5× the object, within these bounds (m), this many pixels.</summary>
    public double MinCloseUpMeters { get; set; } = 14;
    public double MaxCloseUpMeters { get; set; } = 40;
    public int CloseUpPixels { get; set; } = 320;

    /// <summary>While tracking the payload shows about this much ground across (a car ~70 px at 1280),
    /// and this much while reacquiring - the search's own scale: zoomed out to 160 m a car was ~18 px
    /// for the detector, and the operator saw the red car on screen while the detector didn't.</summary>
    public double TrackGroundWidthMeters { get; set; } = 80;
    public double ReacquireGroundWidthMeters { get; set; } = 100;

    /// <summary>Gimbal pointing: aimed ahead by the loop's latency, re-aimed when the point moved more
    /// than the deadband or after the interval.</summary>
    public double PointLeadMs { get; set; } = 300;
    public double PointDeadbandMeters { get; set; } = 3;
    public double PointIntervalMs { get; set; } = 1000;

    /// <summary>Target position reports while tracking, at most this often (plus every state change).</summary>
    public double ReportIntervalSeconds { get; set; } = 1;

    /// <summary>Reacquiring: a candidate within this distance of where the target should be (plus
    /// half the distance it could have driven) is checked by the verifier; the prediction runs no
    /// further than <see cref="MaxPredictSeconds"/>.</summary>
    public double ReacquireRadiusMeters { get; set; } = 40;
    public double MaxPredictSeconds { get; set; } = 8;

    /// <summary>Lost: the search disc grows at least this fast (a parked car can drive off); each look
    /// of the search pattern is held this long; the aircraft gets the search centre this often; and
    /// after <see cref="ReacquireSeconds"/> the target is given up and the search resumes.</summary>
    public double MinAssumedSpeedMps { get; set; } = 6;
    public double LookDwellSeconds { get; set; } = 1.2;
    public double LostReportSeconds { get; set; } = 2;
    public double ReacquireSeconds { get; set; } = 90;

    /// <summary>Search without tracking: a found target that moved this far is reported again.</summary>
    public double MoveReportMeters { get; set; } = 50;

    public bool Enabled => !string.IsNullOrWhiteSpace(EnginePath) && File.Exists(EnginePath);
}

/// <summary>The "Detector" section.</summary>
public sealed class DetectorOptions
{
    public const string SectionName = "Detector";

    /// <summary>Frames sent to the model at once, per search.</summary>
    public int MaxConcurrentFrames { get; set; } = 6;

    /// <summary>Candidates per frame that get a close-up check; the rest are ignored.</summary>
    public int MaxCandidatesPerFrame { get; set; } = 20;

    /// <summary>A zoom close-up shows 2.5× the candidate's size, within these bounds (meters of ground).</summary>
    public double MinZoomWidthMeters { get; set; } = 14;
    public double MaxZoomWidthMeters { get; set; } = 40;

    /// <summary>For a structure (not a vehicle): 1.5× its size, within these bounds. Kept small on
    /// purpose, measured on ZoneB's real photo: at 30-40 m across the model named pylons, a sign
    /// gantry and a road bridge (seen in part) right; at 100-180 m it named whatever stood out in
    /// view instead - both pylons and the gantry became "road bridge".</summary>
    public double MinStructureZoomWidthMeters { get; set; } = 30;
    public double MaxStructureZoomWidthMeters { get; set; } = 40;

    /// <summary>Close-up size asked of the zoom. A vehicle still fills ~120 px at 320, and the
    /// model's image cost grows with pixels.</summary>
    public int ZoomPixels { get; set; } = 320;

    /// <summary>A candidate within this distance of an object already examined up close in this
    /// search is that object again (survey frames overlap), and its description is reused.</summary>
    public double SameObjectMeters { get; set; } = 4;

    /// <summary>Hits of the same prompt within this distance are the same object seen again.</summary>
    public double TrackRadiusMeters { get; set; } = 25;

    /// <summary>A vehicle can drive: within <see cref="TrackMemorySeconds"/> of the last sighting, a
    /// hit this much further per second away is still the same object (25 m/s = 90 km/h).</summary>
    public double MaxTargetSpeedMps { get; set; } = 25;
    public double TrackMemorySeconds { get; set; } = 20;

    /// <summary>A known object that has moved this far since it was last reported is reported
    /// again (same track id), so the operator's "send 998 to the red car" goes where it is now.</summary>
    public double MoveReportMeters { get; set; } = 50;

    /// <summary>How long one pull for the next frame waits before asking again.</summary>
    public int FramePollWaitMs { get; set; } = 10000;
}
