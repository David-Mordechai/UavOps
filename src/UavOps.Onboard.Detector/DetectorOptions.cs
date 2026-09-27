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

    /// <summary>Close-up size asked of the zoom. A vehicle still fills ~120 px at 320, and the
    /// model's image cost grows with pixels.</summary>
    public int ZoomPixels { get; set; } = 320;

    /// <summary>A candidate within this distance of an object already examined up close in this
    /// search is that object again (survey frames overlap), and its description is reused.</summary>
    public double SameObjectMeters { get; set; } = 4;

    /// <summary>Hits of the same prompt within this distance are the same object seen again.</summary>
    public double TrackRadiusMeters { get; set; } = 25;

    /// <summary>How long one pull for the next frame waits before asking again.</summary>
    public int FramePollWaitMs { get; set; } = 10000;
}
