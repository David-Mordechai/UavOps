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

### Push-to-talk (joystick talk button)

Call `SetPushToTalkAsync(true)` when the operator presses the talk button and
`SetPushToTalkAsync(false)` when they release it. The `UavOps.Agent` chat tab the operator used
most recently turns its mic on at press, and at release stops, transcribes and sends the spoken
command, exactly like clicking the chat window's mic button twice. The result is `false` if nothing
acted on it: no chat window was open at press, or the mic wasn't on at release.

```csharp
joystick.TalkButtonDown += async () => await connection.SetPushToTalkAsync(true);
joystick.TalkButtonUp   += async () => await connection.SetPushToTalkAsync(false);
```

Safety nets: if this connection drops while the button is held, the host releases the mic itself,
and a recording started this way stops on its own after 60 seconds. The first time, the browser
asks for microphone permission. It also won't start audio in a tab that has never been clicked:
the chat window then shows a notice asking the operator to click it once.

See `UavOps.MockFleetClient` (in this repo) for a minimal, complete example — it implements
`IUavCommandHandler` with stub logic only (no real fleet-state tracking), specifically to prove
this plumbing works end to end during development.

## Notes

- Uses `Newtonsoft.Json` internally to serialize `CommandResult<T>.Value` for the reply — callers
  of `IUavCommandHandler` never see JSON directly.
- The DTOs here (`Waypoint`, `TelemetrySnapshot`, `GdtLinkStatus`, `MissionStatus`, `UavSummary`)
  are net47-side copies of `UavOps.Agent.Agents.MoavAgent.Operations`'s models (no common TFM between net47 and
  net8 worth introducing). Property names must stay in sync by convention if that side ever
  changes shape.
