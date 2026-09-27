using System.Net;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using UavOps.Agent.Options;
using UavOps.Agent.Voice;
using Xunit;

namespace UavOps.Agent.Tests.Voice;

/// <summary>The STT request carries whisper's initial prompt (the fleet's vocabulary) to the English
/// endpoint only - see <see cref="VoiceOptions.SttEnglishPrompt"/> for what it measurably fixes.</summary>
public class VoiceGatewaySttPromptTests
{
    private const string Prompt = "UAV fleet control commands, e.g. Point 998's payload at bravo.";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(Uri Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"Point 999's payload at the red car."}""") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static VoiceGatewayService Create(CapturingHandler handler, string prompt) =>
        new(new Factory(handler),
            new VoiceOptions { SttEnglishEndpoint = "http://stt-en", SttHebrewEndpoint = "http://stt-he", TtsEndpoint = "", SttEnglishPrompt = prompt },
            // The grammar-fix pass fails soft to the raw transcript; it isn't what's under test here.
            (_, _) => Substitute.For<IChatClient>(),
            new AgentConfig(),
            null!,
            NullLogger<VoiceGatewayService>.Instance);

    private static MemoryStream Wav() => new([1, 2, 3]);

    [Fact]
    public async Task English_SendsThePrompt()
    {
        var handler = new CapturingHandler();

        var (text, _) = await Create(handler, Prompt).TranscribeAsync(Wav(), "clip.wav", "en", CancellationToken.None);

        text.Should().Be("Point 999's payload at the red car.");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Url.Host.Should().Be("stt-en");
        handler.Requests[0].Body.Should().Contain("name=prompt").And.Contain(Prompt);
    }

    [Fact]
    public async Task Hebrew_NeverGetsTheEnglishPrompt()
    {
        var handler = new CapturingHandler();

        await Create(handler, Prompt).TranscribeAsync(Wav(), "clip.wav", "he", CancellationToken.None);

        handler.Requests[0].Url.Host.Should().Be("stt-he");
        handler.Requests[0].Body.Should().NotContain("name=prompt");
    }

    [Fact]
    public async Task ABlankPrompt_IsNotSent()
    {
        var handler = new CapturingHandler();

        await Create(handler, "").TranscribeAsync(Wav(), "clip.wav", "en", CancellationToken.None);

        handler.Requests[0].Body.Should().NotContain("name=prompt");
    }
}
