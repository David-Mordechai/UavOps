namespace UavOps.Agent.Tooling;

/// <summary>
/// The "turn + history" retrieval query: the operator's message with just enough recent
/// conversation to make a bare follow-up ("999" answering "which UAV?") mean something.
///
/// Bounded by characters, newest messages first, because it is sent to the embedding model as one
/// input and that model has a context limit (8192 tokens on the GX10's Qwen3-Embedding server).
/// It used to be the whole kept history - up to 40 messages, including long detection and summary
/// messages - and a long session failed every turn with a 400 "maximum context length is 8192
/// tokens" from /v1/embeddings. Retrieval only needs the last exchange or two anyway.
/// </summary>
public static class RetrievalQuery
{
    public static string Build(IEnumerable<string> historyTexts, string turnText, int maxHistoryChars)
    {
        var budget = Math.Max(maxHistoryChars, 0);
        var kept = new List<string>();
        foreach (var message in historyTexts.Reverse())
        {
            if (budget <= 0)
                break;
            // A message longer than what's left is cut to its end, which holds its most recent part.
            var part = message.Length <= budget ? message : message[^budget..];
            kept.Add(part);
            budget -= part.Length + 1;
        }
        kept.Reverse();
        kept.Add(turnText);
        return string.Join("\n", kept);
    }
}
