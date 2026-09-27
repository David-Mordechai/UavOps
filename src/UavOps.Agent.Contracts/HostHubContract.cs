namespace UavOps.Agent.Contracts;

/// <summary>
/// Names used on the host's hub connection between UavOps.Agent and the MCP servers that connect
/// back to it (McpMoav's relay client, McpSimulator). Shared so neither side spells them twice.
/// </summary>
public static class HostHubContract
{
    /// <summary>Host methods an MCP server calls. Domain-agnostic: the server decides the words,
    /// the host only delivers them.</summary>
    public static class Methods
    {
        /// <summary><c>(string message, string? historyNote, string? spoken, string? voiceGroup)</c>:
        /// show <c>message</c> to the operator as-is, and if <c>historyNote</c> is given, add it and
        /// the message to BrainAgent's history before its next turn. <c>spoken</c> is a short form
        /// for the voice to say instead of the full text (e.g. without coordinates); messages that
        /// share a <c>voiceGroup</c> may be merged by the voice when several are waiting.</summary>
        public const string PostOperatorMessage = nameof(PostOperatorMessage);

        /// <summary><c>(string note, string message)</c>: add to BrainAgent's history only, shown to
        /// no one - for something the operator already knows.</summary>
        public const string AddHistoryNote = nameof(AddHistoryNote);

        /// <summary><c>(string instruction, double elapsedSeconds)</c>: have BrainAgent's persona
        /// (no tools) phrase <c>instruction</c> for the operator, then show the result.</summary>
        public const string PostPhrasedOperatorMessage = nameof(PostPhrasedOperatorMessage);
    }

    /// <summary>What the fleet app reports on its own, forwarded unchanged by the host to McpMoav's
    /// relay connection, which owns what they mean.</summary>
    public static class FleetEvents
    {
        /// <summary>Payload: <see cref="DetectionReport"/>.</summary>
        public const string Detection = "FleetDetection";

        /// <summary>Payload: <see cref="MissionEventReport"/>.</summary>
        public const string MissionEvent = "FleetMissionEvent";
    }
}
