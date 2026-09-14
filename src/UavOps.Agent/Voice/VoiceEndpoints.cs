namespace UavOps.Agent.Voice;

/// <summary>
/// The public wire contract for voice — deliberately identical to what GX10 used to expose
/// directly (see <c>eval/voice-test-harness/providers.js</c>'s "custom" provider: JSON
/// <c>{"input": "..."}</c> → WAV for TTS, multipart WAV upload → <c>{"text": "..."}</c> for STT)
/// so nothing downstream (the harness, or a future real client) needs to change beyond pointing at
/// this host instead of GX10 directly.
/// </summary>
public static class VoiceEndpoints
{
    public static void MapVoiceEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/audio/speech", async (SpeechRequest request, VoiceGatewayService voice, CancellationToken cancellationToken) =>
        {
            var (bytes, contentType) = await voice.SynthesizeAsync(request.Input, request.Exaggeration, request.CfgWeight, cancellationToken);
            return Results.File(bytes, contentType);
        });

        app.MapPost("/v1/audio/transcriptions", async (HttpRequest httpRequest, VoiceGatewayService voice, CancellationToken cancellationToken) =>
        {
            var form = await httpRequest.ReadFormAsync(cancellationToken);
            var file = form.Files["file"];
            if (file is null)
            {
                return Results.BadRequest(new { error = "Missing 'file' form field." });
            }

            // Defaults to English — Hebrew isn't exercised by any real traffic yet, but the
            // endpoint is already routable to GX10's Hebrew whisper-server the moment it's needed
            // (see VoiceOptions.SttHebrewEndpoint).
            var language = httpRequest.Query["language"].FirstOrDefault() ?? "en";

            await using var stream = file.OpenReadStream();
            var (text, rawText) = await voice.TranscribeAsync(stream, file.FileName, language, cancellationToken);
            // rawText is additive - existing consumers (e.g. eval/voice-test-harness) that only
            // read "text" are unaffected; the chat UI uses rawText to show when/what grammar-fix
            // changed instead of applying a correction silently.
            return Results.Json(new { text, rawText });
        });
    }

    private sealed record SpeechRequest(string Input, double Exaggeration = 0.5, double CfgWeight = 0.5);
}
