namespace UavOps.Agent.Options;

public sealed class UavApiOptions
{
    public const string SectionName = "UavApi";

    public required string BaseUrl { get; init; }
    public required string OpenApiUrl { get; init; }

    /// <summary>Optional "Bearer &lt;token&gt;"-style header value sent on every call to the UAV app.</summary>
    public string? AuthHeader { get; init; }
}
