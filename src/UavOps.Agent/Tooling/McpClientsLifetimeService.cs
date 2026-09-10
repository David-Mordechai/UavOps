using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;

namespace UavOps.Agent.Tooling;

/// <summary>
/// Disposes every connected MCP client (stopping its own child server process) during graceful host
/// shutdown. Registered as an <see cref="IHostedService"/> so the Generic Host properly `await`s
/// <see cref="StopAsync"/> as part of its own async shutdown sequence — this used to be a
/// synchronous
/// <c>app.Lifetime.ApplicationStopping.Register(() =&gt; ...DisposeAsync().GetAwaiter().GetResult())</c>
/// callback, a sync-over-async anti-pattern that real-world delayed the "Application is shutting
/// down..." log line (and everything after it) by ~16 seconds:
/// <see cref="CancellationTokenSource.Cancel"/> runs every registered callback synchronously on the
/// thread that triggers it, *ahead of* the host's own async shutdown machinery — fixed here.
///
/// The *remaining* per-client wait (proven via direct instrumentation, not assumed —
/// <c>HostOptions.ShutdownTimeout</c> was tested and confirmed to have zero effect on it) comes
/// from <c>StdioClientTransportOptions.ShutdownTimeout</c> on each client's own transport (see
/// <c>Program.cs</c>'s own comment there): the MCP C# SDK's server-side stdio transport never stops
/// its own hosting process on disconnect (confirmed against the SDK's own source and its official
/// sample), so <see cref="McpClient.DisposeAsync"/> always waits that full configured budget before
/// force-killing the child. Disposal here is concurrent (<see cref="Task.WhenAll"/>), not
/// sequential, so that wait happens once across all 3 clients, not 3 times in series.
/// </summary>
public sealed class McpClientsLifetimeService : IHostedService
{
    /// <summary>Populated once, after <c>Program.cs</c>'s MCP connection loop finishes connecting to
    /// every configured server — this instance is registered both as a plain singleton (so that loop
    /// can reach it) and as this app's one <see cref="IHostedService"/>, resolved once by the host at
    /// startup, so both refer to the exact same instance.</summary>
    public List<McpClient> Clients { get; } = [];

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(Clients.Select(client => client.DisposeAsync().AsTask()));
}
