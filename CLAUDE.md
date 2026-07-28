# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A local-LLM agent layer for natural-language UAV command and control (.NET 8). A chat UI turns
operator text (e.g. "fly UAV-1 to target alpha and set speed to 200") into fleet commands, via
[Ollama](https://ollama.com) and a small local model (`granite4.1:3b` by default). Everything
about which tools an agent exposes and how they're described to the model is config-driven
(`AgentsConfig/*.yaml`), not code.

Two processes end to end:

- **`src/UavOps.Agent`** — the one server-side process: SignalR chat hub (`/chatHub`) for the
  browser, SignalR operation hub (`/uavCommandHub`) for a fleet-commanding client, multi-agent
  orchestration (agent-as-tool delegation), a reflection-based tool catalog, a confirmation gate
  for mutating actions, structured tool-call logging, and a static SPA (`wwwroot/`). Answers
  operations via either an in-memory simulation (`Simulation/`, default) or a SignalR bridge
  (`Operations/Remote/`) to a connected fleet-commanding app — see "Architecture" below. The core
  plumbing (`Operations/`, `Hubs/`, `Tooling/`) is intentionally domain-agnostic — `UavOps.Agent`
  is a general agentic tool-calling framework that currently has a UAV domain plugged into it,
  not a UAV-specific system. Only `Simulation/` is genuinely UAV-domain logic.
- **`src/UavOps.FleetClient`** (.NET Framework 4.7) — a class library a real fleet-commanding
  .NET Framework application references to connect to `UavOps.Agent`'s operation hub; see its
  own README. **`src/UavOps.MockFleetClient`** (.NET Framework 4.7) is a thin console app
  referencing that library with stub command handlers — the dev/test stand-in for the real app.

There is no separate REST API or OpenAPI spec anywhere in this system — that was an earlier
design (a `UavOps.ControlApi` project) that got folded directly into `UavOps.Agent` once it became
clear the real backend transport is SignalR; a REST hop in between was pure indirection.
Operations are declared directly as C# method signatures (`Operations/IOperationService.cs`) and
invoked in-process or over SignalR — never HTTP.

## Running it

Requires the .NET 8 SDK and Ollama running locally with a model pulled (`ollama pull granite4.1:3b`).
No Docker.

```bash
dotnet run --project src/UavOps.Agent --urls http://localhost:5262
```

Then open http://localhost:5262. By default (`OperationBackend: Simulated` in `appsettings.json`)
this is the only process you need — operations are answered in-memory. `UavOps.Agent`
validates every agent's `AgentsConfig/*.yaml` against `IOperationService` **at startup**
(reflection, no network call) — if a tool references an operation/parameter that doesn't exist,
the app refuses to start and logs exactly what's wrong (see `AgentConfigValidator`). `GET /healthz`
gives a quick sanity check (model in use, operation backend, number of operations discovered).

To exercise the real (or mock) fleet-commanding path instead:

```bash
OperationBackend=SignalR dotnet run --project src/UavOps.Agent --urls http://localhost:5262
# separately, once UavOps.Agent is up:
src/UavOps.MockFleetClient/bin/Debug/net47/UavOps.MockFleetClient.exe http://localhost:5262/uavCommandHub
```

## Build & test

```bash
dotnet build                                          # whole solution — includes the two net47 projects
dotnet test tests/UavOps.Agent.Tests                   # unit tests (fast, no live deps)
dotnet test --filter "FullyQualifiedName~ConfirmationGateTests"   # single test class
dotnet test --filter "FullyQualifiedName~SomeMethodName"          # single test method
```

`src/UavOps.FleetClient` and `src/UavOps.MockFleetClient` target `net47` (via the
`Microsoft.NETFramework.ReferenceAssemblies` NuGet package, since this isn't a Windows-installed
targeting pack on every machine) but are ordinary SDK-style projects — `dotnet build` handles them
like any other project in the solution. `dotnet run` does **not** work for them (classic .NET
Framework binaries aren't launched that way) — run the built
`src/UavOps.MockFleetClient/bin/Debug/net47/UavOps.MockFleetClient.exe` directly.

`tests/UavOps.Agent.Evals` is a **separate, opt-in** suite (not part of `UavOps.sln`, not part of
the normal `dotnet test` inner loop) — it drives the real live chat pipeline end to end (SignalR)
against a golden set of operator utterances in `eval/golden-commands/*.yaml`. It requires Ollama
and `UavOps.Agent` actually running (`LiveDependenciesFixture` checks reachability and fails
loudly, not silently, if something isn't up):

```bash
dotnet test tests/UavOps.Agent.Evals
```

Each golden case asserts only what's objectively checkable — which tool got called, with which
arguments (as a substring match against the logged JSON args, e.g. `'"speedKts":220'`) — and
prints the full trace + final response regardless of pass/fail, since response *quality* needs
human (or on-request Claude) judgment, not just assertions. To add a case, add an entry to a YAML
file in `eval/golden-commands/`; no code changes needed.

## Architecture: UavOps.Agent

**Request flow**: `ChatHub.SendMessage` → `MainAgentOrchestrator.HandleAsync` →
`AgentFactory.BuildMainAgentForTurn` builds the *entire* agent graph fresh for that turn → MainAgent
(an `AIAgent` from Microsoft.Agents.AI) decides which domain agent(s) to delegate to.

- **Agents are rebuilt every turn**, not cached, because every tool instance closes over that
  turn's `correlationId` so logs/traces are attributable end-to-end. Only the underlying
  `IChatClient` per Ollama model is cached across turns (`AgentFactory._chatClients`).
- **Delegation is "agent-as-tool"**, not a hardcoded router: `DelegateAgentTool` wraps a domain
  `AIAgent` as an `AIFunction` that MainAgent calls like any other tool. MainAgent is configured
  (`MainAgent.yaml`'s `delegates`) with which domain agents it can hand requests to
  (`FlightControlAgent`, `PayloadControlAgent`, `MissionAgent`, `GdtControlAgent`). Each delegate
  call is logged exactly like a real fleet-command call.
- **Domain agent tools are reflected from `IOperationService`**, not HTTP-backed:
  `OperationTool` is an `AIFunction` for one `IOperationService` method, invoked in-process
  via `MethodInfo.Invoke` (no separate HTTP invoker class — there's no network hop to make).
  Critically, **the LLM never sees C# signatures or reflection details** — only the hand-written
  `Description` and per-parameter descriptions from `AgentsConfig/*.yaml`. `OperationCatalog`
  supplies only the mechanical parameter shape (names/CLR types), built once at startup by
  reflecting over `IOperationService` — no network call, no remotely-fetched spec.
  `AgentConfigValidator` cross-checks the two at startup: every configured tool's `operation` must
  match a real `IOperationService` method name, and every parameter must have either a
  `parameters` description or a `fixedParameters` value. Result JSON returned to the model is
  camelCase (`OperationTool.ResultSerializeOptions`), matching the casing already used in tool
  arguments and schemas — not the DTOs' native PascalCase.
- **Concurrent tool calls**: `FunctionInvokingChatClient(ollama) { AllowConcurrentInvocation =
  true }` plus `ChatOptions.AllowMultipleToolCalls = true` lets one model turn batch several tool
  calls (e.g. `SetSpeed` + `SetAltitude`, or MainAgent delegating to two domain agents at once)
  and run them concurrently instead of one at a time.
- **Confirmation gate** (`ConfirmationGate`): opt-in and per-tool, not inferred from anything
  about the command — a tool only goes through confirmation when its config sets
  `requiresConfirmation: true` *and* `ExecutionMode` is `"Confirm"` (`"Direct"` is a global
  override that skips confirmation entirely, e.g. for local dev). `ExecutionMode` is read live
  from `IConfiguration` on every call, so editing `appsettings.json` takes effect on the next tool
  call with no restart; it defaults to `Confirm` if the value is missing/invalid (fail-safe). The
  approval round-trip happens **in chat, not via UI buttons**: the prompt and its resolution are
  sent as ordinary `ReceiveChatMessage` events (under a correlationId of their own, so they render
  as their own bubble instead of overwriting the turn that triggered them — the turn's own bubble
  is repositioned to the end of the thread once its final answer lands, in `chat.js`, since it may
  have been created earlier from the first trace event and would otherwise sit above a
  confirmation exchange that happened later but before that final answer). The prompt text is
  built from the tool's human-authored `Description` and "name: value" arguments — never a
  function/operation name or raw JSON — since an operator shouldn't need to know internals to
  approve or decline an action. The operator's next plain-text reply is parsed by
  `ChatConfirmationParser` (a small fixed yes/no vocabulary — deliberately not an LLM
  classification, since approving a UAV operation is safety-relevant and needs a deterministic,
  auditable interpretation). `ChatHub.SendMessage` offers every incoming message to
  `ConfirmationGate.TryHandleChatReplyAsync` before treating it as a new operation, so a reply like
  "yes" is consumed as the answer to the pending confirmation rather than spawning a new agent
  turn. Only one confirmation can be outstanding at a time (a `SemaphoreSlim` turnstile in
  `ConfirmationGate`) — with concurrent tool invocation enabled, two mutating calls could
  otherwise both need approval at once, which would make a bare "yes" reply ambiguous about which
  one it answers.
- **Logging**: every tool call — including MainAgent→domain-agent delegation — goes through
  `ToolInvocationLogger`, producing one structured log line (tool, args, result, duration,
  correlation ID) and one `ReceiveAgentTrace` SignalR event, so the chat UI's reasoning panel and
  the eval suite's trace assertions see identical data.
- **SignalR concurrency note**: `MaximumParallelInvocationsPerClient = 10` is set once in
  `Program.cs` and covers both hubs mapped off it (`/chatHub`, `/uavCommandHub`) — needed because
  (a) the operator's chat reply to a pending confirmation is itself just another `SendMessage`
  call that must reach the hub while the *original* `SendMessage` call is still in flight, and
  (b) several operations can be in flight to the connected fleet client at once, each needing
  to reply via `SubmitCommandResult` without queuing behind another still-processing reply.
  SignalR's default limit of 1 would otherwise queue these behind the in-progress call until they
  time out.

### Configuring agents (`src/UavOps.Agent/AgentsConfig/*.yaml`)

One YAML file per agent (filename, without extension, is the agent's name — not a field inside
the file, so a filename/field mismatch can't happen), loaded by `AgentConfigLoader` at startup.
No code changes needed to change what an agent can do or how it's described to the model:

- `instructions` — the agent's system prompt.
- `description` — shown to MainAgent as this agent's tool description when it's one of
  MainAgent's delegates.
- `temperature` — sampling temperature (lower = more consistent tool-calling decisions for a
  small model).
- `delegates` — names of other agents this agent may delegate to (MainAgent only).
- `tools[]` — operation-backed tools this agent may call (domain agents only). Each entry:
  `operation` (must match an `IOperationService` method name exactly, case-sensitive — see
  `Operations/IOperationService.cs`), `description`, `parameters` (name → description shown to the
  model — must cover every parameter the operation needs that isn't in `fixedParameters`),
  `fixedParameters` (name → literal value sent every call, never shown to the model),
  `requiresConfirmation` (default `false`; see the confirmation gate above — independent per
  tool, so e.g. `ReturnToLaunch` can require approval while `SetSpeed` doesn't).

When editing agent instructions, note the existing prompts are deliberately explicit about not
letting the model invent tail numbers, guess/convert units, or resolve pronouns across
agent-to-agent handoffs (each delegate only sees the instruction text MainAgent gives it, not the
full conversation) — preserve that style if you touch them.

### The operation layer (`Operations/`, `Simulation/`, `Operations/Remote/`)

`Operations/IOperationService.cs` is the single source of truth for what an "operation" is — 12
async methods (`ListFleet`, `GetTelemetry`, `Navigate`, `SetSpeed`, `SetAltitude`,
`ReturnToLaunch`, `PointPayload`, `ResetPayload`, `UploadWaypoints`, `GetMissionStatus`,
`GetLinkStatus`, `SetTrackingMode`), each returning `Task<OperationResult>` — a uniform,
non-generic envelope (`Success`, `Error` enum, `ErrorMessage`, boxed `object? Value`) so
`OperationTool` can invoke any of the 12 via plain reflection with **no `dynamic`, no
per-operation switch statement**. Each concrete implementation stays fully typed internally and
only boxes to `object` at the return statement. Add a 13th operation by adding one method to the
interface — `OperationCatalog`/`AgentConfigValidator` pick it up automatically, nothing else
to hand-sync. This interface (and the hub/broker/catalog/tool machinery around it) is
intentionally domain-agnostic in naming; the method names themselves stay UAV-flavored on
purpose since they're domain data, and `Hubs/IOperationClientProxy.cs`'s matching method names
are additionally pinned by the (unchanged) net47 client's wire contract — `ListFleet` keeps
"Fleet" in its name for exactly that reason.

`appsettings.json`'s `OperationBackend` (`"Simulated"` or `"SignalR"`, read once at startup)
chooses which `IOperationService` implementation gets registered in `Program.cs`:

- **`Simulated`** (default) — `Simulation/SimulatedUavOperationService.cs`, in-memory, no
  persistence, no external dependency. `Simulation/KnownPoints.cs` resolves named locations
  (e.g. `"target alpha"`, `"home"`) to lat/lng for navigation and payload-pointing operations.
  This is what local dev and `UavOps.Agent.Evals` run against by default.
- **`SignalR`** — `Operations/Remote/RemoteOperationService.cs`, which relays each call over
  `Hubs/OperationHub.cs` (`/uavCommandHub`, always mapped regardless of backend) to whichever
  fleet command client is connected — the real .NET Framework 4.7 application, or
  `UavOps.MockFleetClient` standing in for it during dev — and awaits the reply.

**The SignalR bridge mechanic** (`Operations/Remote/RemoteOperationBroker.cs`) adapts the same
request/response-over-SignalR pattern `ConfirmationGate` uses (send a message to a connected
client, await a correlated `TaskCompletionSource` with a timeout, resolve it when the client
replies), generalized for multiple operations in flight at once (unlike `ConfirmationGate`'s
single-turnstile design): pending replies are keyed by correlation id in a
`ConcurrentDictionary`, and messages target one specific connection (`Clients.Client(id)`)
instead of broadcasting.

- **Hub contract is asymmetric**: server→client is 12 strongly-typed methods on
  `Hub<IOperationClientProxy>` (one per operation, `correlationId` always first parameter, and
  these signatures must stay byte-for-byte stable — `UavOps.FleetClient` depends on them);
  client→server reply is one generic `SubmitCommandResult(correlationId, success, errorMessage,
  resultJson)`, since reply shapes vary per operation.
- **Connection tracking lives in the broker**, not the Hub (Hub instances are per-call/transient
  in SignalR). Last-writer-wins if a second client connects (logged as a warning). On disconnect,
  the broker only clears its tracked connection id if it matches the disconnecting one (a stale
  disconnect can't clobber a newer connection), and when it does clear, it immediately fails
  every in-flight operation rather than making callers wait out the timeout.
- **Fails fast with no client connected** — checked before any correlation id/timeout machinery
  is even created. Timeout otherwise defaults to 10s (`RemoteOperation:TimeoutSeconds`).
- Broker failures (no client connected, timeout, client-reported error) are logged at `Warning`
  and returned as a real `OperationResult.Error`/`ErrorMessage` — since `IOperationService`
  is fully async and uniform (unlike the old split-interface design this replaced), no failure
  information is lost or flattened on the way back to the caller.

### `UavOps.FleetClient` / `UavOps.MockFleetClient`

`UavOps.FleetClient` (net47 class library) is what a real fleet-commanding application
references: implement its `IUavCommandHandler` (12 plain, synchronous, hardware-facing methods
returning `CommandResult<T>`) and construct `FleetClientConnection(hubUrl, handler)` — that class
owns all the SignalR connection/dispatch plumbing, invoking your handler on a background thread
per command and replying to `OperationHub` with the result (or the exception message, if your
handler throws). See `src/UavOps.FleetClient/README.md`.

`UavOps.MockFleetClient` (net47 console app) is the dev/test stand-in: it references the library
and implements `IUavCommandHandler` with `EmptyCommandHandler` — logs each command to the console
and returns a hardcoded, correctly-shaped placeholder. **Deliberately does not simulate fleet
state** (position, speed, mission progress) — that's already covered by
`SimulatedUavOperationService`, and this mock exists only to prove the SignalR plumbing itself
works end to end.

**Manual smoke test** (no automated live two-process test exists for this — same reasoning as
`UavOps.Agent.Evals` being opt-in/manual):

```bash
OperationBackend=SignalR dotnet run --project src/UavOps.Agent --urls http://localhost:5262
# separately:
src/UavOps.MockFleetClient/bin/Debug/net47/UavOps.MockFleetClient.exe http://localhost:5262/uavCommandHub
```

Open http://localhost:5262 and type a command (e.g. "set UAV-1 speed to 200"); there's no REST
surface to curl anymore, only the chat hub. Confirm the mock's console logs the received command
and the chat response reflects its placeholder `TelemetrySnapshot`. Stop the mock and retry the
same command to confirm it fails fast (near-instant `"No fleet command client is connected."`,
not a 10s hang).

## Ports (local dev)

- `UavOps.Agent`: `http://localhost:5262` (chat UI, `/chatHub`, `/uavCommandHub`, `/healthz`)
- Ollama: `http://localhost:11434`
