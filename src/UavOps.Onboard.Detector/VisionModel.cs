using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UavOps.Onboard.Detector;

/// <summary>The model's raw answer for one image, and how long it took.</summary>
public sealed record VisionAnswer(string Text, long LatencyMs);

/// <summary>Asks a vision-language model one question about one image.</summary>
public interface IVisionModel
{
    Task<VisionAnswer> AskAsync(byte[] jpeg, string question, CancellationToken cancellationToken);

    /// <summary>A question with no image (interpreting and comparing names).</summary>
    Task<VisionAnswer> AskTextAsync(string question, CancellationToken cancellationToken);
}

/// <summary>
/// An OpenAI-compatible chat-completions vision model: the image goes in as a <c>data:</c> URI
/// image part, the question as text, with <see cref="DetectionPrompt.System"/> as the system
/// message. Plain JSON over <see cref="HttpClient"/> rather than an SDK, so it can pass
/// server-specific fields (thinking off) and talks to vLLM and llama.cpp's server alike.
/// </summary>
public sealed class OpenAiVisionModel(HttpClient http, VlmOptions options) : IVisionModel
{
    public Task<VisionAnswer> AskAsync(byte[] jpeg, string question, CancellationToken cancellationToken) =>
        SendAsync(new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = DetectionPrompt.System },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) }
                    },
                    new JsonObject { ["type"] = "text", ["text"] = question }
                }
            }
        }, cancellationToken);

    public Task<VisionAnswer> AskTextAsync(string question, CancellationToken cancellationToken) =>
        SendAsync(new JsonArray { new JsonObject { ["role"] = "user", ["content"] = question } }, cancellationToken);

    private async Task<VisionAnswer> SendAsync(JsonArray messages, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["temperature"] = 0,
            ["max_tokens"] = options.MaxTokens,
            ["messages"] = messages
        };
        if (options.DisableThinking)
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint.TrimEnd('/') + "/chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        if (!string.IsNullOrEmpty(options.ApiKey))
            request.Headers.Authorization = new("Bearer", options.ApiKey);

        var stopwatch = Stopwatch.StartNew();
        using var response = await http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Vision model returned {(int)response.StatusCode}: {Truncate(json)}");

        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return new VisionAnswer(text, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Whether the model server answers at all (GET /models).</summary>
    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(options.Endpoint.TrimEnd('/') + "/models", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "..." : s;
}

/// <summary>
/// What the model is asked. Kept in one place because the wording decides what gets found.
///
/// Two questions, because one doesn't work, measured against the GX10's Qwen3.6 on rendered frames:
/// asked straight "find every white van", the model boxed white trucks and buses as vans and
/// missed the van itself. Asked to name one vehicle from a close-up crop with its real size given,
/// it named every candidate correctly (van, car, bus, truck). So: <see cref="Candidates"/> on the
/// whole frame (recall - anything that could be it), then <see cref="Describe"/> on each candidate's
/// crop, and the description is matched against the target by <see cref="TargetMatcher"/>, not by
/// asking the model yes/no, which it answers yes too readily.
/// </summary>
public static class DetectionPrompt
{
    public const string System =
        "You are the onboard vision system of a UAV. Each image is from its payload camera, looking straight down " +
        "(nadir). The top of the image is the UAV's direction of flight, not north. Objects are seen from directly above: " +
        "a vehicle is a small rectangle, a building is a flat roof with a shadow. You report only what you can actually " +
        "see, and you answer in JSON only.";

    /// <summary>
    /// First pass, for recall: every object of the target's colour, of any type. Asked only for
    /// "things like the target", the model reliably listed the single most target-like vehicle and
    /// stopped - on the frame with the white pickup it proposed the pickup 1 time in 5; asked for
    /// every vehicle of that colour, 5 in 5 (at the cost of ~14 candidates and a longer answer).
    /// </summary>
    public static string Candidates(string target) =>
        $"Search target: \"{target}\".\n" +
        "This is the first pass of a two-pass search. List EVERY object in this frame that has the search target's colour and " +
        "is the same general kind of thing (for a vehicle: cars, vans, pickups, trucks and buses of that colour all count), " +
        "whatever its exact type - each is checked up close afterwards, so do not judge the type now and do not leave any out. " +
        "Leave out objects of other colours.\n" +
        "Answer with a JSON array only, no other text: [{\"label\": \"<colour> <type>\", \"bbox_2d\": [x1, y1, x2, y2]}] " +
        "with coordinates normalized to 0-1000 (x to the right, y down); [] if there is none.";

    public static string Describe(double metersAcross) =>
            "A close-up of one spot from the payload camera, looking straight down; this image is " + metersAcross.ToString("F0", CultureInfo.InvariantCulture) +
            " m across. Describe the single object at the " +
            "centre: its colour and what it is. For a vehicle, type is one of car, van, pickup, truck, bus, motorcycle. From " +
            "above: a car (about 4.5 m long) shows a hood, a windshield, a roof, a rear window and a trunk; a van (about 5.5 m) " +
            "a short hood, a windshield and one long flat roof to the back; a pickup (about 5.3 m) a short cab and, behind it, an open cargo bed - a dark tray between light side walls, about a third of its length; a truck (8 m or " +
            "more) a cab and a long cargo box; a bus (about 12 m) one long roof. For anything else, a short noun.\n" +
            "Answer with JSON only: {\"colour\": \"...\", \"type\": \"...\", \"confidence\": 0.0-1.0}";

    // ---- Anything that isn't a vehicle (a pylon, a bridge, a building): see TargetKind ----
    // Measured on Route 443's real photo (ZoneB) with the GX10's Qwen3.6. Told the target while
    // looking at a close-up, the model called a road sign gantry both a "power grid antenna" and a
    // "road bridge", and a plain road a "road bridge" - it saw what it was told to look for. Asked
    // what the object is without the target, it named all six test spots right (electricity pylon
    // x2, road bridge, road sign gantry, car x2). Comparing that name to the operator's words is then
    // a question with no image: taken literally ("is an electricity pylon a power grid antenna?")
    // it said no, so the operator's words are first interpreted as the object they most likely mean
    // (Interpret: "power grid antenna" -> "transmission tower") and names are compared to that
    // (SameKind). A first live run over ZoneB found both real pylons and a mast, and also took the
    // pylons' "power line" cables for the target; with the connected-things rule, 131/132 right over
    // 11 phrasings x 12 seen objects ("cell tower" and cables included, correctly no match; the one
    // miss: "bridge" vs "road leading onto a bridge").
    // From straight above, a stretch of highway reads as a bridge: a live "road bridges" search
    // reported the real overpass and three stretches of road. DescribeObject therefore says what a
    // bridge is, and a structure needs two close-ups to agree (SearchTaskRunner.ConfirmStructureAsync).
    // Scored on 15 spots of ZoneB checked by eye (2 pylons, a mast, 2 points on the overpass, and 10
    // look-alikes: cables, a drainage channel, highway, road, a photo seam, a sign gantry...): both
    // pylons and the overpass found, 0 false hits (without the bridge sentence: 1 false hit).

    /// <summary>Once per search: what the operator most likely means, by its usual name.</summary>
    public static string Interpret(string target) =>
        $"A UAV operator asked its camera to find: \"{target}\". Operators use loose, informal names. What object do they " +
        "most likely mean, by its usual specific name, as it would be seen on the ground from above? Keep any colour they " +
        "gave. Answer with JSON only: {\"object\": \"<usual specific name>\"}";

    /// <summary>First pass for a structure: anything that could be it, of any colour; a large one
    /// may run off the frame.</summary>
    public static string StructureCandidates(string target, string meant) =>
        $"Search target: \"{target}\" (that is, a {meant}).\n" +
        "This is the first pass of a two-pass search. List EVERY object in this frame that could be the search target, or " +
        "part of one (a large structure may extend beyond the frame edge) - each is checked up close afterwards, so when in " +
        "doubt include it. Leave out things that clearly are something else.\n" +
        "Answer with a JSON array only, no other text: [{\"label\": \"<what it is>\", \"bbox_2d\": [x1, y1, x2, y2]}] " +
        "with coordinates normalized to 0-1000 (x to the right, y down); [] if there is none.";

    /// <summary>Second pass for a structure: what it is, without being told the target.</summary>
    public static string DescribeObject(double metersAcross) =>
        "A close-up of one spot from the payload camera, looking straight down; this image is " +
        metersAcross.ToString("F0", CultureInfo.InvariantCulture) + " m across. What is the object or structure at the " +
        "centre? Be specific about what it is for (e.g. a road sign gantry, an electricity pylon, a road bridge, a building, " +
        "a car). A road bridge carries a road over something else - another road, a river, a valley - that you can see " +
        "passing under it; a road on the ground, a wall or a kerb is not a bridge. " +
        "Answer with JSON only: {\"what\": \"<short specific noun phrase>\", \"confidence\": 0.0-1.0}";

    /// <summary>With no image: is what was seen the object the operator means?</summary>
    public static string SameKind(string target, string meant, string seen) =>
        $"A UAV operator is searching for \"{target}\", meaning: \"{meant}\". The onboard camera saw: \"{seen}\".\n" +
        "Is what was seen that object - the same kind of thing, judged by what it is and what it is for, not by the exact " +
        "words? Something that only looks similar, or a different thing next to it or connected to it (such as the cables " +
        "strung from a tower, or the road leading onto a bridge), is not a match. " +
        "Answer with JSON only: {\"match\": true/false}";
}
