using System.Text.Json;
using System.Text.RegularExpressions;

namespace UavOps.Agent.McpMoav.Missions;

/// <summary>A plan's steps in plain words, for the operator to read before saying start, and for
/// the messages as steps run. Generic (any tool), with clearer wording for the common ones.</summary>
public static partial class MissionDescriber
{
    public static string Describe(MissionPlan plan) =>
        string.Join("\n", plan.Steps.Select(s => $"{s.Number}. {s.When.Describe()}: {DescribeAction(s.Do, s.Args)}."));

    public static string DescribeAction(string tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        var who = args.TryGetValue("tailNumbers", out var list) ? Who(list) : args.TryGetValue("tailNumber", out var one) ? Who(one) : null;
        string Arg(string name) => args.TryGetValue(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()) : "";
        return tool switch
        {
            "PrepareAoiSearch" => $"{who} search {Arg("zoneName")} for a {Arg("targetDescription")}" +
                                  (args.TryGetValue("track", out var t) && t.ValueKind == JsonValueKind.True ? " and track it once found" : "") +
                                  " (the zone split between them, routes uploaded)",
            "StartMission" => $"{who} start flying the search",
            "StopMission" => $"{who} stop the mission and hold where {(IsPlural(who) ? "they are" : "it is")}",
            "ReturnToLaunch" => $"{who} return home",
            "Navigate" => $"{who} fly to {Arg("location")}",
            "SetSpeed" => $"{who} set speed to {Arg("speedKts")} kts",
            "SetAltitude" => $"{who} climb or descend to {Arg("altitudeFt")} ft",
            "PointPayload" => $"{who} point the camera at {Arg("location")}",
            "ResetPayload" => $"{who} camera back to its default position",
            "SetPayloadZoom" => $"{who} zoom the camera to {Arg("zoom")}x",
            _ => $"{Humanize(tool)}{(who is null ? "" : " - " + who)}" +
                 string.Concat(args.Where(a => a.Key is not ("tailNumber" or "tailNumbers")).Select(a => $", {Humanize(a.Key).ToLowerInvariant()}: {a.Value}"))
        };
    }

    /// <summary>
    /// A step that ran, in as few words as the operator needs (shown, and the whole of what the
    /// voice says): the UAVs that actually did it by tail number, not by role ("997 returning home",
    /// not "the other UAVs return home"). The full wording stays in the plan and in history.
    /// </summary>
    public static string DescribeDone(string tool, IReadOnlyDictionary<string, JsonElement> args, IReadOnlyList<string> tails)
    {
        var who = tails.Count > 0
            ? (tails.Count == 1 ? tails[0] : string.Join(", ", tails.Take(tails.Count - 1)) + " and " + tails[^1])
            : args.TryGetValue("tailNumbers", out var list) ? Who(list) : args.TryGetValue("tailNumber", out var one) ? Who(one) : "";
        string Arg(string name) => args.TryGetValue(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()) : "";
        return tool switch
        {
            "PrepareAoiSearch" => $"{who} ready to search {Arg("zoneName")}",
            "StartMission" => $"{who} searching",
            "StopMission" => $"{who} holding",
            "ReturnToLaunch" => $"{who} returning home",
            "Navigate" => $"{who} flying to {Arg("location")}",
            "SetSpeed" => $"{who} at {Arg("speedKts")} kts",
            "SetAltitude" => $"{who} going to {Arg("altitudeFt")} ft",
            "PointPayload" => $"{who} camera on {Arg("location")}",
            "ResetPayload" => $"{who} camera reset",
            "SetPayloadZoom" => $"{who} camera at {Arg("zoom")}x",
            _ => DescribeAction(tool, args)
        };
    }

    /// <summary>"997 and 998", "the UAV that found it", "the other UAVs".</summary>
    public static string Who(JsonElement value)
    {
        var parts = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
            : (value.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var words = parts.Select(p => p switch
        {
            Roles.Finder => "the UAV that found it",
            Roles.Others => "the other UAVs",
            Roles.All => "all the mission's UAVs",
            _ => p
        }).ToList();
        return words.Count <= 1 ? string.Concat(words) : string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1];
    }

    private static bool IsPlural(string? who) => who is not null && (who.Contains(" and ") || who.StartsWith("the other") || who.StartsWith("all"));

    private static string Humanize(string identifier)
    {
        var words = HumanizeRegex().Split(identifier);
        var text = string.Join(' ', words.Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex HumanizeRegex();
}
