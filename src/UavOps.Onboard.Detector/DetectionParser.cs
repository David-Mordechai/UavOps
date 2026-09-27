using System.Text.Json;
using System.Text.RegularExpressions;
using UavOps.Onboard.Contracts;

namespace UavOps.Onboard.Detector;

/// <summary>One object the model said it saw in a frame.</summary>
public sealed record FrameHit(string Label, BoundingBox Box, double Confidence);

/// <summary>What the model says one object is, from its close-up: "white" "van".</summary>
public sealed record ObjectDescription(string Colour, string Type, double Confidence)
{
    public string Text => $"{Colour} {Type}".Trim();
}

/// <summary>
/// Reads the model's answer: a JSON array of <c>{label, bbox_2d, confidence}</c> or of bare
/// <c>[x1, y1, x2, y2]</c> boxes, possibly inside
/// a ```json fence or with text around it. Anything that isn't a valid box (wrong length, out of
/// the 0-1000 range, zero size) is dropped rather than guessed at, and so is anything below the
/// search's minimum confidence. A model that leaves out confidence is taken at 0.5.
/// </summary>
public static partial class DetectionParser
{
    public static List<FrameHit> Parse(string answer, double minConfidence)
    {
        var json = ExtractArray(answer);
        if (json is null)
            return [];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        var hits = new List<FrameHit>();
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                // A bare box, [x1, y1, x2, y2], is a candidate with no label.
                if (item.ValueKind == JsonValueKind.Array)
                {
                    if (TryBoxValues(item, out var bare))
                        hits.Add(new FrameHit("object", bare, 0.5));
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object || !TryBox(item, out var box))
                    continue;
                var confidence = item.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(c.GetDouble(), 0, 1)
                    : 0.5;
                if (confidence < minConfidence)
                    continue;
                var label = item.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "object";
                hits.Add(new FrameHit(label.Trim(), box, confidence));
            }
        }
        return hits;
    }

    /// <summary>Reads <c>{"colour", "type", "confidence"}</c>; null if the answer isn't that.</summary>
    /// <summary>A structure close-up's answer, <c>{"what", "confidence"}</c>, as a description
    /// with no colour.</summary>
    public static ObjectDescription? ParseWhat(string answer) =>
        ReadObject(answer) is { } root && Str(root, "what") is { Length: > 0 } what
            ? new ObjectDescription("", what.ToLowerInvariant(), Confidence(root))
            : null;

    /// <summary>The value of <paramref name="field"/> in a JSON object answer: a string, or a bool
    /// as "true"/"false"; null if it isn't there.</summary>
    public static string? ParseField(string answer, string field)
    {
        if (ReadObject(answer) is not { } root || !root.TryGetProperty(field, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Trim(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static JsonElement? ReadObject(string answer)
    {
        var fenced = Fence().Match(answer);
        var text = fenced.Success ? fenced.Groups[1].Value : answer;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static double Confidence(JsonElement root) =>
        root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? Math.Clamp(c.GetDouble(), 0, 1) : 0.5;

    public static ObjectDescription? ParseDescription(string answer)
    {
        if (ReadObject(answer) is not { } root)
            return null;
        var colour = Str(root, "colour");
        if (colour.Length == 0)
            colour = Str(root, "color");
        var type = Str(root, "type");
        return type.Length == 0 ? null : new ObjectDescription(colour.ToLowerInvariant(), type.ToLowerInvariant(), Confidence(root));
    }

    private static bool TryBox(JsonElement item, out BoundingBox box)
    {
        box = new BoundingBox(0, 0, 0, 0);
        return (item.TryGetProperty("bbox_2d", out var b) || item.TryGetProperty("bbox", out b)) && TryBoxValues(b, out box);
    }

    private static bool TryBoxValues(JsonElement b, out BoundingBox box)
    {
        box = new BoundingBox(0, 0, 0, 0);
        if (b.ValueKind != JsonValueKind.Array || b.GetArrayLength() != 4 || b.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number))
            return false;
        var v = b.EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var (x1, x2) = (Math.Min(v[0], v[2]), Math.Max(v[0], v[2]));
        var (y1, y2) = (Math.Min(v[1], v[3]), Math.Max(v[1], v[3]));
        if (x1 < 0 || y1 < 0 || x2 > 1000 || y2 > 1000 || x2 - x1 < 1 || y2 - y1 < 1)
            return false;
        box = new BoundingBox(x1, y1, x2, y2);
        return true;
    }

    /// <summary>The first top-level JSON array in the text.</summary>
    private static string? ExtractArray(string text)
    {
        var fenced = Fence().Match(text);
        if (fenced.Success)
            text = fenced.Groups[1].Value;
        var start = text.IndexOf('[');
        if (start < 0)
            return null;
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '\\') i++;
                else if (ch == '"') inString = false;
                continue;
            }
            switch (ch)
            {
                case '"': inString = true; break;
                case '[': depth++; break;
                case ']':
                    if (--depth == 0)
                        return text[start..(i + 1)];
                    break;
            }
        }
        return null;
    }

    [GeneratedRegex(@"```(?:json)?\s*(.*?)```", RegexOptions.Singleline)]
    private static partial Regex Fence();
}
