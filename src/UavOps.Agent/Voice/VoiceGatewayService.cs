using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using UavOps.Agent.Options;

namespace UavOps.Agent.Voice;

/// <summary>
/// Everything that used to be Python business logic inside GX10's `stt-parakeet`/`tts-chatterbox`
/// containers, now living here instead — see <c>CLAUDE.md</c>'s "Voice STT/TTS evaluation"
/// section. GX10 itself now runs only bare model-inference servers (two `whisper-server`
/// instances for STT, one per language — see <see cref="VoiceOptions"/> — plus the slimmed
/// Chatterbox-Turbo TTS container); this class does the wire-format adaptation, language
/// routing, and the grammar-fix pass that used to be GX10-side Python.
///
/// The grammar-fix pass reuses the exact same <c>Func&lt;string, string?, IChatClient&gt;</c>
/// factory/<see cref="AgentConfig"/> BrainAgent itself is built from (see <c>Program.cs</c>) —
/// same GX10 chat vLLM, same model, no new configuration needed. It's the same fail-soft contract
/// the Python `fix_grammar()` it replaces already guaranteed: any failure (timeout, connection
/// refused, malformed response) falls back to the raw, uncorrected transcript rather than
/// breaking voice control entirely.
/// </summary>
public sealed class VoiceGatewayService
{
    // Domain-aware, not just generic proofreading - a live-observed case this prompt used to
    // miss: "Point there, payloads there." (should be "Point their payloads there.") went
    // through unfixed, and "UABs" (a mishearing of "UAVs", the one domain term guaranteed to
    // come up in nearly every command) was left alone entirely - the old generic prompt had no
    // way to know that's the term being misheard. Naming the domain and the exact term directly
    // gives the model the context to fix both classes of error, while the "do not change
    // numbers/tail numbers/locations" instruction stays exactly as strict as before.
    private const string GrammarFixSystemPrompt =
        "You proofread speech-to-text transcripts from a UAV (drone) fleet control voice " +
        "interface. Fix grammar, punctuation, and disfluencies, including homophone/word-choice " +
        "errors that break grammar (e.g. 'there' used where 'their' is required). The fleet is " +
        "always referred to as 'UAVs' - correct any mishearing of that term (e.g. 'UABs', 'U " +
        "A B's', 'UAB's') to 'UAVs'. Do NOT change numbers, tail numbers, coordinates, or other " +
        "factual/quantitative content. Output ONLY the corrected transcript, nothing else.";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly VoiceOptions _options;
    private readonly Func<string, string?, IChatClient> _chatClientFactory;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<VoiceGatewayService> _logger;

    public VoiceGatewayService(
        IHttpClientFactory httpClientFactory,
        VoiceOptions options,
        Func<string, string?, IChatClient> chatClientFactory,
        AgentConfig agentConfig,
        ILogger<VoiceGatewayService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _chatClientFactory = chatClientFactory;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    /// <summary>Forwards a WAV clip to the real bare inference endpoint for <paramref name="language"/>
    /// ("en" or "he" — anything else falls back to English), then runs the grammar-fix pass on the
    /// raw transcript it gets back. Matches whisper-server's own `/inference` wire shape almost
    /// exactly (multipart `file` in, `{"text": ...}` out) — confirmed directly against the real
    /// server, so this is mostly pass-through plus the language routing and grammar-fix step.
    /// Returns both the corrected text and the untouched raw transcript — the operator-facing UI
    /// shows the raw text alongside the corrected one whenever grammar-fix actually changed
    /// something, so a correction is visible rather than silent (a real ask: seeing the tool calls
    /// come out right despite a garbled-looking display transcript, with no visibility into
    /// whether/what got fixed along the way).</summary>
    public async Task<(string Text, string RawText)> TranscribeAsync(Stream wavStream, string fileName, string language, CancellationToken cancellationToken)
    {
        var endpoint = string.Equals(language, "he", StringComparison.OrdinalIgnoreCase)
            ? _options.SttHebrewEndpoint
            : _options.SttEnglishEndpoint;

        using var content = new MultipartFormDataContent
        {
            { new StreamContent(wavStream), "file", string.IsNullOrEmpty(fileName) ? "clip.wav" : fileName },
            { new StringContent("json"), "response_format" }
        };

        var client = _httpClientFactory.CreateClient("SttInference");
        using var response = await client.PostAsync($"{endpoint.TrimEnd('/')}/inference", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var rawText = body.TryGetProperty("text", out var textProp) ? (textProp.GetString() ?? "").Trim() : "";

        var corrected = await FixGrammarAsync(rawText, cancellationToken);
        return (corrected, rawText);
    }

    /// <summary>Forwards a synthesis request to GX10's Chatterbox-Turbo TTS endpoint and returns
    /// the raw WAV bytes + content-type as-is — no other adaptation needed, this endpoint's wire
    /// shape already matches our own public contract exactly. <see cref="SpaceOutBareNumbers"/> is
    /// applied to <paramref name="input"/> first — see that method's own doc comment for why.</summary>
    public async Task<(byte[] Bytes, string ContentType)> SynthesizeAsync(string input, double exaggeration, double cfgWeight, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("TtsInference");
        var payload = new { input = SpaceOutBareNumbers(input), exaggeration, cfg_weight = cfgWeight };
        using var response = await client.PostAsJsonAsync($"{_options.TtsEndpoint.TrimEnd('/')}/v1/audio/speech", payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "audio/wav";
        return (bytes, contentType);
    }

    private async Task<string> FixGrammarAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        try
        {
            var chatClient = _chatClientFactory(_agentConfig.Model ?? "", _agentConfig.Provider);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, GrammarFixSystemPrompt),
                new(ChatRole.User, text),
            };
            var response = await chatClient.GetResponseAsync(messages, new ChatOptions { Temperature = 0 }, cancellationToken);
            var corrected = response.Text.Trim();
            return string.IsNullOrEmpty(corrected) ? text : corrected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Grammar-fix LLM call failed, returning raw transcript");
            return text;
        }
    }

    // Matches a standalone run of 3+ digits NOT immediately followed by a unit word - real,
    // live-verified this session: Chatterbox-Turbo mispronounces a bare compact tail number (e.g.
    // "998") ambiguously enough that two completely unrelated STT engines (whisper.cpp/large-v3
    // and NeMo Parakeet) independently mis-transcribed the SAME synthesized audio in matching ways
    // (e.g. both heard "999" as broken-up digit groups) - proving the problem is TTS
    // pronunciation, not STT decoding. Space-separating the digits before synthesis (e.g. "9 9 8")
    // fixed it for all three tail numbers tested, confirmed via a pure digital TTS-to-STT round
    // trip (no speakers/mic in the loop). Deliberately does NOT touch a number followed by a unit
    // (knots/feet/percent/degrees) - those are genuine quantities BrainAgent should still speak
    // naturally ("two hundred knots", not "two zero zero knots"); only a bare number with no unit
    // is treated as a tail number in this app's domain.
    private static readonly Regex BareNumberPattern = new(
        @"\b\d{3,}\b(?!\s*(?:knots?|feet|percent|degrees?|%))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string SpaceOutBareNumbers(string text) =>
        BareNumberPattern.Replace(text, m => string.Join(' ', m.Value.ToCharArray()));
}
