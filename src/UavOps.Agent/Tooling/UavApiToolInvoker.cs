using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UavOps.Agent.Tooling;

public sealed record UavApiCallResult(int StatusCode, string Body);

/// <summary>
/// Single choke point for every actual HTTP call to the UAV app. Both the tool logging and the
/// confirmation gate hook in around calls to this class, so there is exactly one place that
/// builds a request from a resolved operation + argument set.
/// </summary>
public sealed class UavApiToolInvoker(HttpClient httpClient)
{
    public async Task<UavApiCallResult> InvokeAsync(
        UavApiOperationDescriptor descriptor,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var path = descriptor.Path;
        var query = new List<string>();
        JsonObject? body = null;

        foreach (var p in descriptor.Parameters)
        {
            arguments.TryGetValue(p.Name, out var value);

            switch (p.Location)
            {
                case ParamLocation.Path:
                    path = path.Replace($"{{{p.Name}}}", Uri.EscapeDataString(ToRouteString(value)), StringComparison.Ordinal);
                    break;

                case ParamLocation.Query:
                    if (value is not null)
                    {
                        query.Add($"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(ToRouteString(value))}");
                    }
                    break;

                case ParamLocation.BodyProperty:
                    body ??= [];
                    body[p.Name] = value is null ? null : JsonSerializer.SerializeToNode(value);
                    break;
            }
        }

        var url = path + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        var httpMethod = new HttpMethod(descriptor.HttpMethod.ToString().ToUpperInvariant());

        using var request = new HttpRequestMessage(httpMethod, url);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return new UavApiCallResult((int)response.StatusCode, responseBody);
    }

    private static string ToRouteString(object? value) => value switch
    {
        null => "",
        JsonElement je => je.ToString(),
        _ => value.ToString() ?? ""
    };
}
