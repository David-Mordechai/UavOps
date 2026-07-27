namespace UavOps.Agent.Tooling;

/// <summary>
/// Parses an operator's free-text chat reply to a pending confirmation as approve/decline.
/// Deliberately a small fixed vocabulary rather than an LLM classification — approving a UAV
/// command is safety-relevant, so the interpretation needs to be deterministic and auditable,
/// not a judgment call the model could get wrong.
/// </summary>
public static class ChatConfirmationParser
{
    private static readonly HashSet<string> Affirmative = new(StringComparer.OrdinalIgnoreCase)
    {
        "y", "yes", "yeah", "yea", "yep", "yup", "sure", "ok", "okay", "confirm", "confirmed",
        "approve", "approved", "go", "go ahead", "do it", "proceed", "affirmative"
    };

    private static readonly HashSet<string> Negative = new(StringComparer.OrdinalIgnoreCase)
    {
        "n", "no", "nope", "nah", "cancel", "stop", "abort", "deny", "denied",
        "decline", "declined", "don't", "dont", "negative"
    };

    public static bool TryParse(string text, out bool approved)
    {
        var normalized = text.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();

        if (Affirmative.Contains(normalized))
        {
            approved = true;
            return true;
        }

        if (Negative.Contains(normalized))
        {
            approved = false;
            return true;
        }

        approved = false;
        return false;
    }
}
