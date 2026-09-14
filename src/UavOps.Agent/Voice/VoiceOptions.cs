namespace UavOps.Agent.Voice;

/// <summary>
/// Endpoints for the raw AI-model inference servers on GX10 — each one is deliberately just a
/// model-loading process with no business logic of its own (a bare `whisper-server` instance per
/// language for STT, the slimmed `tts-chatterbox` container for TTS). Everything else — wire-
/// format adaptation, language routing, the grammar-fix pass — lives in <see cref="VoiceGatewayService"/>,
/// not on GX10. Same plain-POCO/<c>SectionName</c>-const pattern as <see cref="Options.OpenAiOptions"/>/
/// <see cref="Options.EmbeddingOptions"/>.
/// </summary>
public sealed class VoiceOptions
{
    public const string SectionName = "Voice";

    /// <summary>Bare `whisper-server` instance running `ggml-large-v3.bin` (stock OpenAI weights) —
    /// the English/general-purpose model.</summary>
    public required string SttEnglishEndpoint { get; init; }

    /// <summary>Bare `whisper-server` instance running ivrit.ai's Hebrew-tuned `whisper-large-v3`
    /// checkpoint. Not exercised by any real traffic yet (Hebrew support is a future requirement),
    /// but already live on GX10 so it's ready when needed — see <c>CLAUDE.md</c>'s Voice section.</summary>
    public required string SttHebrewEndpoint { get; init; }

    /// <summary>The slimmed Chatterbox-Turbo TTS endpoint (unchanged from before this session's
    /// STT rework — its own CORS/business-logic trim is a separate, smaller cleanup).</summary>
    public required string TtsEndpoint { get; init; }

    public int HttpTimeoutSeconds { get; init; } = 30;
}
