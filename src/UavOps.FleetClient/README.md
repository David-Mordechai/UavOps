# UavOps.FleetClient

A .NET Framework 4.7 class library that connects to `UavOps.Agent`'s operation hub
(`/uavCommandHub`) as a SignalR client and relays every incoming operation to an
[`IUavCommandHandler`](IUavCommandHandler.cs) you implement. This is the artifact a real
fleet-commanding application references — it has no SignalR/JSON/correlation-id code of its own
to write.

## Integrating

1. Implement `IUavCommandHandler` — one method per command (`Navigate`, `SetSpeed`,
   `ReturnToLaunch`, etc.), each returning a `CommandResult<T>` (`CommandResult<T>.Ok(value)` on
   success, `CommandResult<T>.Fail(message)` on failure — e.g. an unknown tail number or a
   hardware fault). These are plain, synchronous, hardware-facing calls.
2. Construct a `FleetClientConnection(hubUrl, handler)` and call `StartAsync()`. Subscribe to its
   `Connected`/`Disconnected`/`Reconnecting` events if you want to log connection state.
3. That's it — `FleetClientConnection` registers a handler for every command, invokes your
   `IUavCommandHandler` implementation (on a background thread, so a slow/blocking call doesn't
   stall the connection), and replies to `UavOps.Agent` with the result.

See `UavOps.MockFleetClient` (in this repo) for a minimal, complete example — it implements
`IUavCommandHandler` with stub logic only (no real fleet-state tracking), specifically to prove
this plumbing works end to end during development.

## Notes

- Uses `Newtonsoft.Json` internally to serialize `CommandResult<T>.Value` for the reply — callers
  of `IUavCommandHandler` never see JSON directly.
- The DTOs here (`Waypoint`, `TelemetrySnapshot`, `GdtLinkStatus`, `MissionStatus`, `UavSummary`)
  are net47-side copies of `UavOps.Agent.Operations`'s models (no common TFM between net47 and
  net8 worth introducing). Property names must stay in sync by convention if that side ever
  changes shape.
