namespace UavOps.Onboard.Detector;

/// <summary>
/// Whether a description ("white van") is the search target ("a white van"): every word of the
/// target, filler dropped and plurals folded, is in the description. Deterministic on purpose -
/// the model is asked what it sees, never whether it's the target (see <see cref="DetectionPrompt"/>).
/// </summary>
public static class TargetMatcher
{
    private static readonly HashSet<string> Filler = ["a", "an", "the", "any", "some", "for", "of", "one", "with", "coloured", "colored"];

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["grey"] = "gray",
        ["minivan"] = "van",
        ["lorry"] = "truck",
        ["automobile"] = "car",
        ["pick-up"] = "pickup",
        ["colour"] = "color"
    };

    public static bool Matches(string target, string description)
    {
        var wanted = Words(target).ToHashSet();
        if (wanted.Count == 0)
            return false;
        var seen = Words(description).ToHashSet();
        return wanted.All(seen.Contains);
    }

    private static IEnumerable<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split([' ', ',', '.', '-', '/', '\t', '\n', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Filler.Contains(w))
            .Select(w => Synonyms.GetValueOrDefault(w, w))
            .Select(w => w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") ? w[..^1] : w);
}

/// <summary>
/// Which way a target is searched for. Vehicles keep the colour-first candidates and word-matched
/// close-ups measured on vehicles (see <see cref="DetectionPrompt"/>); anything else - a pylon, a
/// bridge, a building - has no colour to filter by and many names for the same thing, so it gets
/// the structure prompts and a model comparison instead.
/// </summary>
public static class TargetKind
{
    private static readonly HashSet<string> VehicleWords =
        ["car", "van", "minivan", "pickup", "truck", "lorry", "bus", "motorcycle", "motorbike", "vehicle", "suv", "jeep", "taxi", "sedan", "hatchback"];

    public static bool IsVehicle(string target) =>
        target.ToLowerInvariant()
            .Split([' ', ',', '.', '-', '/', '\t', '\n', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") ? w[..^1] : w)
            .Any(VehicleWords.Contains);
}
