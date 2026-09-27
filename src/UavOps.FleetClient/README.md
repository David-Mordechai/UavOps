# UavOps.FleetClient

A class library (.NET Framework 4.7, plus .NET 8 for `UavOps.Simulator` and the tests) that connects to `UavOps.Agent`'s operation hub
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

### Locations

`Navigate` and `PointPayload` receive a `location` string that is either a named point (`home`,
`alpha`, `bravo`) or a `"lat,lng"` literal in decimal degrees, invariant culture, e.g.
`"31.81382,34.66519"`. The host sends the literal for a position that has no name, such as a
search detection the operator said to fly to ("send 998 to the white van"). Accept both.

### AOI search missions (optional)

To support search missions, also implement [`IUavMissionHandler`](IUavMissionHandler.cs) on the
same handler object; `FleetClientConnection` picks it up automatically. An app that doesn't
implement it keeps compiling and working unchanged, and the host gets an immediate "does not
support AOI search missions" failure for these commands.

A mission arrives in steps, each a separate command:

1. `UploadWaypoints` — the search route (a lawnmower sweep planned on the ground). This must
   **not** start flight.
2. `SetSearchTarget(tailNumber, SearchTargetRequest)` — hand the onboard agent what to look for
   (`Prompt`, e.g. "white van", with a `MissionId`, `ZoneName` and `MinConfidence`).
3. `StartMission(tailNumber)` — start flying the uploaded route; `Mode` becomes `"Searching"`.
   The operator confirms this step before it's sent.

Both return a `MissionStatus`; fill in its optional `CurrentWaypointIndex`, `ActiveMissionId` and
`SearchPrompt` so the host can see mission state.

Then report back, unprompted:

- `ReportDetectionAsync(DetectionReport)` when the onboard agent spots the target. The operator
  gets a chat message with the location and can send another UAV there. Reporting the same object
  again is harmless: the host ignores repeats within 100 m in the same mission. Keep searching
  afterwards; what happens next is the operator's call.
- `ReportMissionEventAsync(MissionEventReport)` with `Kind` = `MissionEventKinds.Completed` when
  the route has been flown to its end, or `Aborted` when a `Navigate`/`ReturnToLaunch`/new upload
  cut it short.

The host accepts these two calls only from the fleet connection (`/uavCommandHub`).

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
this plumbing works end to end during development; its **D** key sends a fake detection.
`UavOps.Simulator` is the full example: it flies the UAVs and plays the onboard detector.

## Notes

- Uses `Newtonsoft.Json` internally to serialize `CommandResult<T>.Value` for the reply — callers
  of `IUavCommandHandler` never see JSON directly.
- The DTOs here (`Waypoint`, `TelemetrySnapshot`, `MissionStatus`, `SearchTargetRequest`,
  `DetectionReport`, ...) are copies of `UavOps.Agent.Contracts`'s `OperationModels`, and command
  names mirror the host's `IOperationClientProxy`. `FleetContractDriftTests` in
  `UavOps.Agent.Tests` fails if either side changes without the other.
