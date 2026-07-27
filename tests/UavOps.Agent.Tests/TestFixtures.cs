using Microsoft.OpenApi.Models;

namespace UavOps.Agent.Tests;

/// <summary>Captures the outgoing HTTP request instead of making a real network call.</summary>
internal sealed class CapturingHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
    }
}

internal static class TestFixtures
{
    /// <summary>A minimal OpenAPI document with one operation ("SetSpeed") shaped like the real
    /// UavOps.ControlApi: a required path parameter (tailNumber) and a required body property
    /// (speedKts) — enough to exercise path/body parameter extraction and validation.</summary>
    public static OpenApiDocument SetSpeedDocument() => new()
    {
        Paths = new OpenApiPaths
        {
            ["/uavs/{tailNumber}/speed"] = new OpenApiPathItem
            {
                Operations = new Dictionary<OperationType, OpenApiOperation>
                {
                    [OperationType.Post] = new OpenApiOperation
                    {
                        OperationId = "SetSpeed",
                        Parameters =
                        [
                            new OpenApiParameter
                            {
                                Name = "tailNumber",
                                In = ParameterLocation.Path,
                                Required = true,
                                Schema = new OpenApiSchema { Type = "string" }
                            }
                        ],
                        RequestBody = new OpenApiRequestBody
                        {
                            Content = new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new OpenApiMediaType
                                {
                                    Schema = new OpenApiSchema
                                    {
                                        Type = "object",
                                        Required = new HashSet<string> { "speedKts" },
                                        Properties = new Dictionary<string, OpenApiSchema>
                                        {
                                            ["speedKts"] = new OpenApiSchema { Type = "integer" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    };
}
