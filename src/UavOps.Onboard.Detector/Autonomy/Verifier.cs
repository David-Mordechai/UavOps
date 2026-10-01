namespace UavOps.Onboard.Detector.Autonomy;

/// <summary>What the verifier made of a close-up.</summary>
public sealed record Verdict(bool IsTarget, string? Description, long LatencyMs);

/// <summary>
/// The only place the onboard stack asks a language model anything: "what is this?" on a zoomed
/// close-up of one candidate, at the few moments that decide something (is this the target? is
/// this the same car after we lost it?). Never per frame - on the Jetson a small VLM takes seconds.
/// </summary>
public interface IVerifier
{
    Task<Verdict> VerifyAsync(byte[] closeUp, double metersAcross, string target, CancellationToken cancellationToken);
}

/// <summary>
/// Reuses the measured vehicle close-up question (<see cref="DetectionPrompt.Describe"/>): the model
/// names colour and type, and <see cref="TargetMatcher"/> compares that with the target word by
/// word - the model is never asked "is this the target?", which it answers yes too readily.
/// </summary>
public sealed class VlmVerifier(IVisionModel model) : IVerifier
{
    public async Task<Verdict> VerifyAsync(byte[] closeUp, double metersAcross, string target, CancellationToken cancellationToken)
    {
        var answer = await model.AskAsync(closeUp, DetectionPrompt.Describe(metersAcross), cancellationToken);
        var description = DetectionParser.ParseDescription(answer.Text);
        return new Verdict(description is not null && TargetMatcher.Matches(target, description.Text), description?.Text, answer.LatencyMs);
    }
}
