namespace UavOps.Agent.Evals;

public sealed class EvalCase
{
    public string Name { get; set; } = "";
    public string Utterance { get; set; } = "";
    public bool ExpectNoToolCalls { get; set; }
    public List<ExpectedToolCall> ExpectedTools { get; set; } = [];

    public override string ToString() => Name; // shown as the xUnit test case display name
}

public sealed class ExpectedToolCall
{
    public string Agent { get; set; } = "";
    public string Tool { get; set; } = "";
    public Dictionary<string, string> ArgsContain { get; set; } = [];
}
