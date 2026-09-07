# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A local-LLM agent layer for natural-language UAV command and control (.NET 8). A chat UI turns
operator text (e.g. "fly UAV-1 to target alpha and set speed to 200") into fleet, simulator, and
watchdog commands, via a single flat agent (`BrainAgent`) backed by an OpenAI-compatible chat
endpoint (Ollama locally by default, or a real OpenAI-compatible server such as vLLM — see
`AgentModels` in `appsettings.json`). Everything about what BrainAgent can do and how each tool is
described to the model is config-driven (`AgentsConfig/BrainAgent.yaml`), not code.

Two processes end to end:

- **`src/UavOps.Agent`** — the one server-side process: SignalR chat hub (`/chatHub`) for the
  browser, SignalR operation hub (`/uavCommandHub`) for a fleet-commanding client, a single flat
  `BrainAgent` holding every real operation across 4 domains (live fleet, training simulator,
  watchdog health, watchdog service configuration) directly as tools — no agent-to-agent
  delegation anywhere — narrowed each turn to a relevant top-K subset by real semantic-embedding
  tool retrieval, a reflection-based tool catalog, a confirmation gate for mutating actions,
  structured tool-call logging, and a static SPA (`wwwroot/`). Answers UAV operations via either an
  in-memory simulation (`Agents/MoavAgent/Simulation/`, default) or a SignalR bridge
  (`Agents/MoavAgent/Operations/Remote/`) to a connected fleet-commanding app, drives the separate
  training-simulator environment (a local VMware host/VM, plus lesson scripts run directly on this
  machine) via `Agents/SimulatorAgent/`, and monitors/configures a separately-running watchdog
  process via `Agents/MaintenanceAgent/` — see "Architecture" below. `.cs` files under `Agents/`
  are arranged per domain purely for organization — `Agents/MoavAgent/` (the live-fleet plumbing:
  operations, simulation, the UAV-specific SignalR hub), `Agents/SimulatorAgent/` (the real
  VMware/lesson-runner implementation), `Agents/MaintenanceAgent/` (the real watchdog
  health-polling/service-config implementation) — while genuinely cross-cutting infrastructure used
  by every domain (`AgentFactory`, tool retrieval, `Tooling/`, `Options/`, `Hubs/ChatHub.cs`) stays
  at the `Agents/`/top level; a domain folder's position plays no role in what BrainAgent can call —
  that's entirely `AgentsConfig/BrainAgent.yaml`'s `tools:` list. The fake/dev stand-in
  implementations for the simulator and watchdog domains (`FakeSimulatorService`/
  `FakeLessonExecutor`, `FakeWatchdogService`/`FakeWatchdogConfigService`) and the shared
  interfaces/DTOs the real and fake implementations both depend on (`ISimulatorService`,
  `IWatchdogService`, `IWatchdogConfigService`, `OperationResult`, etc.) live in three more small
  projects, `src/UavOps.Agent.Simulator.Fake`, `src/UavOps.Agent.Watchdog.Fake`, and
  `src/UavOps.Agent.Contracts` — see "Simulator infrastructure" below for why.
- **`src/UavOps.FleetClient`** (.NET Framework 4.7) — a class library a real fleet-commanding
  .NET Framework application references to connect to `UavOps.Agent`'s operation hub; see its
  own README. **`src/UavOps.MockFleetClient`** (.NET Framework 4.7) is a thin console app
  referencing that library with stub command handlers — the dev/test stand-in for the real app.

There is no separate REST API or OpenAPI spec anywhere in this system — that was an earlier
design (a `UavOps.ControlApi` project) that got folded directly into `UavOps.Agent` once it became
clear the real backend transport is SignalR; a REST hop in between was pure indirection.
Operations are declared directly as C# method signatures
(`Agents/MoavAgent/Operations/IOperationService.cs`) and invoked in-process or over SignalR — never
HTTP.

## Running it

Requires the .NET 8 SDK, Ollama running locally with the main chat model (`ollama pull
granite4.1:3b`) pulled, and a reachable **real embedding endpoint** for tool retrieval (see
below) — there is no offline/fake fallback for embeddings anymore.

**Chat model**: `Ollama:DefaultModel` (`appsettings.json`) is the app-wide default; `AgentModels`
overrides BrainAgent specifically to point at any OpenAI-compatible endpoint instead (e.g. a vLLM
server) — set `AgentModels:Provider` to `"OpenAI"` and `AgentModels:Model`/`OpenAI:Endpoint`
accordingly, plus an API key via `dotnet user-secrets set "OpenAI:ApiKey" "..." --project
src/UavOps.Agent` (`AgentConfigValidator` fails fast at startup if this is missing).

**Tool retrieval embeddings** (`Embedding` section, required, no `"InMemory"`/Ollama fallback
mode anymore): a real `Qwen/Qwen3-Embedding-8B`-class model served on an OpenAI-compatible
`/v1/embeddings` endpoint (e.g. a second local vLLM instance). This is load-bearing for tool-call
correctness, not a nice-to-have — see `ToolRetrievalIndex`'s own doc comment — so an unreachable
embedding endpoint fails the app at boot (`Program.cs`), not silently at the first real operator
turn. A hash-based fake was deliberately removed after `eval/tool-retrieval-lab/` found it ranks
tools uncorrelated with meaning.

No Docker.

```bash
dotnet run --project src/UavOps.Agent --urls http://localhost:5262
```

Then open http://localhost:5262. By default (`OperationBackend: Simulated`, `SimulatorBackend:
Fake`, `WatchdogBackend: Fake` in `appsettings.json`) this is the only process you need —
everything is answered in-memory, no VMware/real watchdog process required. `UavOps.Agent`
validates `AgentsConfig/BrainAgent.yaml`'s `tools:` list against all 4 real interfaces
(`IOperationService`/`ISimulatorService`/`IWatchdogService`/`IWatchdogConfigService`) **at
startup** (reflection, no network call) — if a tool references an operation/parameter that
doesn't exist, the app refuses to start and logs exactly what's wrong (see
`AgentConfigValidator`). `GET /healthz` gives a quick sanity check (model in use, each backend
selection, number of operations discovered per catalog, number of tools in the retrieval index).

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
`AgentFactory.GetOrCreatePersistentBrainAgentAsync` (the single, long-lived `BrainAgent`
`ChatClientAgent`, created once and reused for the app's entire lifetime — see "Persistent session"
below) + `AgentFactory.BuildToolsForTurn` (this turn's real, correlationId-scoped tool list,
narrowed by retrieval — see below) → one `agent.RunAsync` call. **No agent-to-agent delegation
anywhere** — this replaced an earlier multi-agent tree (`BrainAgent` → `MoavAgent`/
`SimulatorAgent`/`MaintenanceAgent` → per-domain leaf specialists) after live testing and a pair of
standalone baselines (`eval/single-agent-baseline/`, a pure-Python HTTP script, and
`eval/single-agent-baseline-dotnet/`, matching this app's own `Microsoft.Agents.AI` construction
path) established that the delegation-hop structure itself — not the model, not the .NET Agent
Framework — was the source of fabricated success claims (a leaf agent, or `BrainAgent` itself,
confidently reporting an action succeeded with zero underlying tool calls). Both baselines scored
8/8 on the exact scenarios that fabricated live, using a flat, no-delegation design; a standalone
lab (`eval/tool-retrieval-lab/`) then validated that a flat design scales correctly to far more
tools than this app needs (1000 synthetic tools, 100% pass rate) once real embedding-based
retrieval narrows what's offered each turn — both were then ported into this app directly.

- **BrainAgent holds every real operation across all 4 catalogs directly** — fleet (12 ops),
  simulator (4 ops + 1 `OperatorPrompt` tool), watchdog health (4 ops), watchdog config (5 ops) —
  as tools of one flat agent, each wrapped the same way regardless of domain (see "Domain tools",
  below). `AgentFactory.BuildAllTools` is one loop, no recursion.
- **Tools are rebuilt fresh every turn**, not cached, because every tool instance closes over that
  turn's `correlationId` so logs/traces are attributable end-to-end. `BrainAgent` itself — and its
  one reused `AgentSession` — is the opposite: built once and cached for the app's lifetime (see
  "Persistent session" below); only its *tools* are swapped in per turn via
  `ChatClientAgentRunOptions`. The underlying `IChatClient` per model is also cached across turns
  (`AgentFactory._chatClients`).
- **Live semantic tool retrieval** (`ToolRetrievalIndex`, `Retrieval:TopK` in `appsettings.json`,
  default 10): `BuildToolsForTurn` embeds the operator's raw turn text and ranks it by cosine
  similarity against every tool's own `(name, description)` embedding (computed once at startup
  from a template tool list, `AgentFactory.BuildTemplateTools`), returning only the top-K most
  relevant tool names for that turn. No `children:`/agent-selection concept exists anymore (that
  was `AgentRetrievalIndex`, deleted along with the delegation tree it served) — this is
  tool-level, not agent-level, retrieval, and it is the *only* thing narrowing what a turn sees;
  nothing is force-appended regardless of ranking. Requires a real embedding model
  (`Options/EmbeddingOptions.cs`, see "Running it" above) — a hash-based fake was found, in the
  standalone lab, to rank tools uncorrelated with meaning, which is actively dangerous once ranking
  quality is load-bearing for correctness rather than a nice-to-have.
- **`tool_choice` is left at its default, "auto" — never forced.** An earlier version of this flat
  design forced `tool_choice: "required"` on an agent's first completion of a turn
  (`RequireToolOnFirstTurnChatClient`, since deleted), carried over defensively from the old
  multi-agent design without re-validating whether it was still needed. It was not: forcing
  deterministically broke a bare greeting (confirmed 8/8 via a repeated live regression test) —
  producing no tool call at all when forced, something auto-mode never did — while the two
  standalone baselines above, both using unforced `tool_choice: "auto"`, never exhibited the
  fabrication forcing was meant to prevent in the first place. Removing forcing (and the
  verified-retry safety net that existed only to compensate for its failures) fixed the greeting
  regression with no loss of the anti-fabrication property, since that property turned out to come
  from the flat architecture itself, not from forcing.
- **Persistent session, and a real, live-reproduced fabrication bug it caused**: `BrainAgent` and
  its `AgentSession` are created once (`GetOrCreatePersistentBrainAgentAsync`, guarded by a
  `SemaphoreSlim` against concurrent first-use) and reused for the app's entire running lifetime —
  real multi-turn memory, not per-connection. Its history is bounded by
  `Memory:MaxHistoryMessages` (default 40) via `InMemoryChatHistoryProviderOptions.ChatReducer` —
  **not** `Microsoft.Extensions.AI`'s own `MessageCountingChatReducer`. That type was tried first
  and found, from its actual shipped source, to unconditionally exclude *every* message containing
  a `FunctionCallContent`/`FunctionResultContent` from its output, regardless of the target count —
  not "trim once you exceed N", but "never include a tool call or its result, ever". Once
  `BrainAgent`'s session ran even one reduction pass, every real tool call it had ever made became
  invisible to the model on the next completion, while its own past plain-text success claims
  remained — live-reproduced (`eval/single-agent-baseline-dotnet-persistent/`, one never-recreated
  session, real production model/prompt/tools): the first round of a repeated scenario succeeded
  with real tool calls, then **every round after that fabricated a confident success report with
  zero tool calls**, because the model's own history contained nothing but "operator asked, I said
  I did it" pairs with no tool trace at all to distinguish that pattern from one where it never
  called anything real. Fixed with `Agents/ToolCallAwareChatReducer.cs`, which bounds history the
  same way (target message count, plus the first system message) but operates on whole
  conversation turns, never individual messages, so a kept turn's tool call and its result can
  never be split apart or silently erased — the most recent turn is always kept in full even if it
  alone exceeds the target. Verified 8/8 on the same reproduction after the fix, and again live
  through the real chat UI, several rounds deep into real accumulated session history.
- **Domain tools are reflected from 4 interfaces**, not HTTP-backed: `OperationTool` is an
  `AIFunction` for one interface method (`IOperationService`, `ISimulatorService`,
  `IWatchdogService`, or `IWatchdogConfigService`), invoked in-process via `MethodInfo.Invoke` (no
  separate HTTP invoker class — there's no network hop to make). Critically, **the LLM never sees
  C# signatures or reflection details** — only the hand-written `description` and per-parameter
  descriptions from `AgentsConfig/BrainAgent.yaml`. Each interface has its own `OperationCatalog`
  instance supplying only the mechanical parameter shape (names/CLR types), built once at startup
  by reflecting over that interface — no network call, no remotely-fetched spec.
  `AgentConfigValidator` cross-checks all four at startup: every configured tool's `operation` must
  match a real method name on one of them, and every parameter must have either a `parameters`
  description or a `fixedParameters` value. Fleet-domain tools whose operation takes a `tailNumber`
  or `location` parameter get additional wrapping (`TailNumberDisambiguationTool`/
  `LocationCanonicalizationTool` — see the operation-layer section below); non-fleet tools don't,
  since only fleet operations can name a UAV the model might guess instead of asking. Result JSON
  returned to the model is camelCase (`OperationTool.ResultSerializeOptions`), matching the casing
  already used in tool arguments and schemas — not the DTOs' native PascalCase.
- **Concurrent tool calls**: `FunctionInvokingChatClient(inner) { AllowConcurrentInvocation = true
  }` plus `ChatOptions.AllowMultipleToolCalls = true` lets one model turn batch several tool calls
  (e.g. `SetSpeed` + `SetAltitude`, or a fleet-wide fan-out across several UAVs) and run them
  concurrently instead of one at a time.
- **Confirmation gate** (`ConfirmationGate`): opt-in and per-tool, not inferred from anything
  about the command — a tool only goes through confirmation when its config sets
  `requiresConfirmation: true` *and* `ExecutionMode` is `"Confirm"` (`"Direct"` is a global
  override that skips confirmation entirely, e.g. for local dev). `ExecutionMode` is read live
  from `IConfiguration` on every call, so editing `appsettings.json` takes effect on the next tool
  call with no restart; it defaults to `Confirm` if the value is missing/invalid (fail-safe). The
  approval round-trip happens **in chat**: the prompt and its resolution are sent as ordinary
  `ReceiveChatMessage` events (under a correlationId of their own, so they render as their own
  bubble instead of overwriting the turn that triggered them — the turn's own bubble is
  repositioned to the end of the thread once its final answer lands, in `chat.js`, since it may
  have been created earlier from the first trace event and would otherwise sit above a
  confirmation exchange that happened later but before that final answer). The prompt text is
  built from the tool's human-authored `Description` and "name: value" arguments — never a
  function/operation name or raw JSON — since an operator shouldn't need to know internals to
  approve or decline an action. Alongside that prompt, a `ReceiveChoices` event (under the same
  correlationId) carries `["Yes", "No"]` so `chat.js` can render them as clickable buttons on that
  bubble — clicking one submits that exact text through the same `SendMessage` path as typing it,
  so the two are indistinguishable to the backend. Either way, the operator's reply is parsed by
  `ChatConfirmationParser` (a small fixed yes/no vocabulary — deliberately not an LLM
  classification, since approving a UAV operation is safety-relevant and needs a deterministic,
  auditable interpretation). `ChatHub.SendMessage` offers every incoming message to
  `ConfirmationGate.TryHandleChatReplyAsync` before treating it as a new operation, so a reply like
  "yes" is consumed as the answer to the pending confirmation rather than spawning a new agent
  turn. Only one confirmation can be outstanding at a time (a `SemaphoreSlim` turnstile in
  `ConfirmationGate`) — with concurrent tool invocation enabled, two mutating calls could
  otherwise both need approval at once, which would make a bare "yes" reply ambiguous about which
  one it answers.
- **Logging**: every tool call goes through `ToolInvocationLogger`, producing one structured log
  line (tool, args, result, duration, correlation ID) and one `ReceiveAgentTrace` SignalR event, so
  the chat UI's reasoning panel and the eval suite's trace assertions see identical data.
- **SignalR concurrency note**: `MaximumParallelInvocationsPerClient = 10` is set once in
  `Program.cs` and covers both hubs mapped off it (`/chatHub`, `/uavCommandHub`) — needed because
  (a) the operator's chat reply to a pending confirmation is itself just another `SendMessage`
  call that must reach the hub while the *original* `SendMessage` call is still in flight, and
  (b) several operations can be in flight to the connected fleet client at once, each needing
  to reply via `SubmitCommandResult` without queuing behind another still-processing reply.
  SignalR's default limit of 1 would otherwise queue these behind the in-progress call until they
  time out.

### Configuring BrainAgent (`src/UavOps.Agent/AgentsConfig/BrainAgent.yaml`)

**One YAML file**, loaded by `AgentConfigLoader.Load` at startup — no filename-as-agent-name
convention, no recursive directory search, no per-agent subfolders; there is exactly one agent.
No code changes needed to change what BrainAgent can do or how a tool is described to the model:

- `instructions` — BrainAgent's system prompt. Covers domain-routing notes (which tools belong to
  which real-world domain, since retrieval can offer tools across all 4 at once), tail-number/
  pronoun-resolution rules, multi-part-request counting, simulator startup ordering, watchdog
  config rules, and reporting rules (never claim an action completed without a real tool call this
  turn — see "Persistent session" above for why this rule has real teeth now that history can't
  silently lose tool-call evidence).
- `temperature` — sampling temperature (lower = more consistent tool-calling decisions for a
  small model). `model`/`provider` are deliberately **not** read from this file — see
  `AgentConfig.Model`'s own doc comment; they come from `AgentModels` in `appsettings.json`
  instead, applied onto the loaded config in `Program.cs` before validation runs.
- `tools[]` — every real operation BrainAgent may call, across all 4 domains in one flat list.
  Each entry: `operation` (the tool name the LLM sees; for `kind: Operation` — the default — must
  match a method name on `IOperationService`/`ISimulatorService`/`IWatchdogService`/
  `IWatchdogConfigService` exactly, case-sensitive — `AgentConfigValidator` checks all four),
  `description`, `exampleUtterance` (a plausible operator phrase that would trigger this tool —
  required, shown on hover in the Agent Graph tab, and embedded alongside the description for
  retrieval ranking), `parameters` (name → description shown to the model — must cover every
  parameter the operation needs that isn't in `fixedParameters`), `fixedParameters` (name → literal
  value sent every call, never shown to the model), `requiresConfirmation` (default `false`; see
  the confirmation gate above — independent per tool). `kind: OperatorPrompt` instead builds a
  bespoke ask-the-operator-and-wait tool (`Agents/SimulatorAgent/AskOperatorChoiceTool.cs`) — not
  resolved against any catalog — see the `AskOperatorWhichLesson` tool for the only current
  example.

No `description`/`exampleUtterance`-at-the-agent-level or `children:` concept exists anymore —
both existed only to describe an agent to a *parent* agent (as a delegate tool, or for
agent-selection retrieval); with exactly one agent that delegates to nothing, neither purpose
applies. `AgentGraphProjector` reflects this: the Agent Graph tab renders one `BrainAgent` node
plus one node per real tool, a flat star graph — genuinely correct given the architecture, not a
UI regression.

When editing `instructions`, note the existing prompt is deliberately explicit about never
guessing a tail number/location, resolving pronouns and fleet-wide references
(`them`/`they`/`their`) from real conversation history rather than asking every time, and never
reporting an action as done without a matching tool call *this turn* — preserve that style if you
touch it; these rules are the main remaining defense against fabrication now that there's no
delegation hop to misroute at.

### The operation layer (`Agents/MoavAgent/Operations/`, `Agents/MoavAgent/Simulation/`, `Agents/MoavAgent/Operations/Remote/`)

`Agents/MoavAgent/Operations/IOperationService.cs` is the single source of truth for what an "operation" is — 12
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
purpose since they're domain data, and `Agents/MoavAgent/Hubs/IOperationClientProxy.cs`'s matching
method names are additionally pinned by the (unchanged) net47 client's wire contract —
`ListFleet` keeps "Fleet" in its name for exactly that reason. `OperationResult`/`OperationError`
themselves live in `src/UavOps.Agent.Contracts` rather than here — they're genuinely
domain-agnostic, shared verbatim by `ISimulatorService` (see "Simulator infrastructure" below).

`appsettings.json`'s `OperationBackend` (`"Simulated"` or `"SignalR"`, read once at startup)
chooses which `IOperationService` implementation gets registered in `Program.cs`:

- **`Simulated`** (default) — `Agents/MoavAgent/Simulation/SimulatedUavOperationService.cs`,
  in-memory, no persistence, no external dependency.
  `Agents/MoavAgent/Simulation/KnownPoints.cs` resolves named locations (e.g. `"target alpha"`,
  `"home"`) to lat/lng for navigation and payload-pointing operations. This is what local dev and
  `UavOps.Agent.Evals` run against by default.
- **`SignalR`** — `Agents/MoavAgent/Operations/Remote/RemoteOperationService.cs`, which relays
  each call over `Agents/MoavAgent/Hubs/OperationHub.cs` (`/uavCommandHub`, always mapped
  regardless of backend) to whichever fleet command client is connected — the real .NET Framework
  4.7 application, or `UavOps.MockFleetClient` standing in for it during dev — and awaits the
  reply.

**The SignalR bridge mechanic** (`Agents/MoavAgent/Operations/Remote/RemoteOperationBroker.cs`) adapts the same
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

### Simulator infrastructure (`Agents/SimulatorAgent/`, `OperatorPromptGate`)

`UavOps.Agent.Contracts`'s `ISimulatorService.cs` mirrors `IOperationService`'s shape exactly
(uniform `Task<OperationResult>`, `CancellationToken` last) so it plugs into the same
`OperationCatalog`/`OperationTool` reflection machinery via a second `OperationCatalog` instance
(`Program.cs` builds and validates both catalogs) — a second reflected interface for a second
domain, not a parallel mechanism. `ISimulatorService` lives in its own project (rather than beside
`IOperationService`) specifically so the Fake implementation below can reference just the
interface without pulling in the rest of `UavOps.Agent` — see "Two implementations, split across
three projects" further down. Four operations, all local to the machine `UavOps.Agent` runs
on — no SSH/network hop anywhere in this domain, since the lesson scripts live alongside the
agent, not on the VM: `EnsureVmwareHostRunning`/`EnsureSimulatorVmRunning` (check-and-start-if-
needed, via `IVmwareController` — local `Process`/`vmrun.exe`, since there's no VMware .NET SDK),
`ListSimulatorLessons` (via `ILocalLessonRunner`), and `RunSimulatorLesson` — see "Background
lesson execution" below, it doesn't run the lesson itself. `LocalLessonRunner` validates every
resolved lesson path stays inside the configured lessons folder (`Path.GetFullPath` + prefix
check) before running anything — the lesson name ultimately originates from the operator's chat
reply, so it's never trusted as a bare filename.

**Virtual network adapters**: `EnsureSimulatorVmRunning` (re)connects every device name in
`SimulatorOptions.NetworkAdapterDeviceNames` (e.g. `"ethernet0"`, `"ethernet1"`) via `vmrun
connectNamedDevice` — VMware Workstation can silently fail to auto-connect one of several virtual
NICs on power-on (the greyed-out adapter you'd otherwise right-click → Connect in the UI). `vmrun`
has no documented way to *query* a device's live connection state, only to force a connect, so
this runs unconditionally rather than "check then fix" — reconnecting an already-connected adapter
is a harmless no-op, same as clicking Connect on one that's already fine. Each adapter gets up to
`NetworkAdapterReconnectAttempts` tries (`NetworkAdapterReconnectRetryDelaySeconds` apart, both in
`SimulatorOptions`), each attempt logged individually — added after production showed two
adapters failing on their only attempt with no way to tell from one data point whether that's a
permanent config issue or the adapter simply not being ready an instant after power-on; the
per-attempt logs make that distinguishable after the fact. Best-effort per adapter (one exhausting
all attempts doesn't fail the whole "is the VM ready" result — reported via
`networkAdaptersFailedToReconnect`, not fatal).

**Readiness, not just "started"**: `Process.Start`/`vmrun start` returning doesn't mean VMware or
the guest OS is actually usable yet, so both Ensure* operations poll for real readiness before
returning success (`IVmwareController.WaitForHostReadyAsync`/`WaitForVmToolsRunningAsync`,
`SimulatorOptions.HostReadyTimeoutSeconds`/`VmToolsReadyTimeoutSeconds`/
`ReadyPollIntervalSeconds`), returning `OperationResult.Fail` on timeout rather than a false
"success":
- **Host**: polls `vmrun list` until it succeeds — there's no dedicated "is the host ready"
  command, but a successful `vmrun list` proves the VMware backend service is responsive, which is
  what actually matters for every subsequent `vmrun` call.
- **Guest**: polls `vmrun checkToolsState <vmx>` until it reports `"running"`. Deliberately *not*
  `vmrun getGuestIPAddress -wait` — verified against VMware's own vmrun command reference that
  `-wait` returns immediately (doesn't actually wait) if the network isn't ready yet, and it's
  separately documented as unreliable specifically in the `nogui`/headless mode `StartVmAsync`
  always uses. Adapter reconnection (above) deliberately happens *before* this wait, not after —
  `connectNamedDevice` only needs the VM powered on at the hypervisor level (not a booted guest),
  so fixing a flaky management NIC first avoids it skewing a network-dependent readiness check.

**Background lesson execution** (`SimulatorLessonJob`/`ISimulatorLessonJobQueue`/
`SimulatorLessonJobProcessor`/`ILessonExecutor`): a lesson script can take minutes and may itself
start other long-running services (e.g. `docker compose up`), and its output can run to several KB
of dense `docker ps`-style text — both a bad fit for blocking the model's synchronous tool-call
turn. `RunSimulatorLesson` on `SimulatorService`/`FakeSimulatorService` is deliberately
thin: it just calls `ISimulatorLessonJobQueue.Enqueue(...)` (a `System.Threading.Channels.Channel`-
backed queue, not just a "busy" flag — a second request while one is running waits its turn
instead of being rejected) and returns `{ status: "queued" }` immediately — the entire tool result
the model sees for that turn, so it never has to parse a docker dump (a real run's summary once
claimed "no errors reported" while the raw output plainly showed two containers crash-looping,
`Restarting (127)`, buried in ~30 lines of table text a small model didn't reliably scan — wording
alone wasn't a reliable enough fix). `BrainAgent.yaml`'s instructions tell the model to report only
that the lesson started, never a final outcome, in that same turn.

A single `SimulatorLessonJobProcessor` (`BackgroundService`, registered once regardless of
backend) consumes the queue one job at a time under its own lifetime token (app shutdown only —
not any individual chat request's, so a browser disconnecting mid-lesson can no longer affect a
run already handed to the queue). For each job it: (1) runs `ILessonExecutor.ExecuteAsync` — the
deterministic "what happened" step (`LocalLessonExecutor` for the Real backend, wrapping
`ILocalLessonRunner` and moving the old unhealthy-container detection here — substring match on
`Restarting (` / `Dead`, and `Exited (N)` for non-zero `N`, `Exited (0)` excluded since it's the
normal state for a one-shot/init container; `FakeLessonExecutor` for Fake, a short `Task.Delay`
then a canned outcome — full raw output is logged here for debugging and never passed further);
then (2) builds a **tools-stripped** instance of `BrainAgent` (`AgentFactory.BuildPersonaOnlyAgent`
— calls the private `BuildAgent` directly with no tools) and runs it once with a synthetic
instruction built from the concise outcome, to produce the "simple terms" sentence in the same
voice as the real agent — tool-free specifically so it cannot re-trigger anything even if it
misreads the prompt; then (3) pushes the resulting text via the **existing** `ReceiveChatMessage`
SignalR event under a fresh correlationId. `chat.js` needed no changes for this — it already renders any `ReceiveChatMessage`
as a new bubble the first time it sees a correlationId, so the summary appears as a new,
unprompted message in the thread with no frontend work at all.

**Deterministic lesson pre-selection** (`AskOperatorChoiceTool`): the instructions used to say the
model *may* skip asking which lesson to run if the operator already named one — discretion a small
model didn't reliably exercise (it asked anyway). Fixed the same way this codebase already fixes
this class of problem — deterministically, not via model judgment: `AskOperatorChoiceTool` now
receives the turn's raw operator text (threaded through `AgentFactory.BuildAllTools`/
`BuildToolsForTurn` alongside the retrieval query) and auto-resolves without ever prompting when
**exactly one** offered choice appears in it (matched against the full lesson filename or the name
without `.ps1`); zero or multiple matches falls back to the real prompt, the safe default for
anything ambiguous.

**PowerShell invocation details** (`LocalLessonRunner`, called from `LocalLessonExecutor`): lesson
scripts run via `powershell.exe -Command "[Console]::OutputEncoding =
[System.Text.Encoding]::UTF8; & '<path>'; exit $LASTEXITCODE"` rather than the simpler `-File
<path>`, for two reasons verified empirically against a real script: (1) `-File` inherits the
legacy console codepage for captured output, which corrupted non-ASCII bytes in production (e.g.
`docker ps`'s truncation ellipsis) — setting `[Console]::OutputEncoding` first, plus
`StandardOutputEncoding`/`StandardErrorEncoding = Encoding.UTF8` on the .NET side, fixes this;
(2) invoking via `& 'path'` inside `-Command` does **not** automatically propagate the script's
`exit N` as the process's own exit code the way `-File` does — confirmed directly (a script
calling `exit 7` otherwise surfaced here as exit `1`) — so `; exit $LASTEXITCODE` is required at
the end to forward it. `VmwareController` also sets UTF-8 on its captured streams for consistency,
and its failure messages fall back to stdout when stderr is empty (`FailureDetails`) — also
observed in production: a real `connectNamedDevice` failure had a nonzero exit code and completely
empty stderr, and would have silently discarded whatever `vmrun` actually wrote to stdout instead.

The fifth tool ("ask the operator which lesson to run") is deliberately **not** on
`ISimulatorService` — its whole job is to prompt-and-wait in chat, not to be a data
operation, so it's a hand-built `AIFunction` (`Agents/SimulatorAgent/AskOperatorChoiceTool.cs`, `kind:
OperatorPrompt` in YAML — see "Configuring BrainAgent" above) backed by `OperatorPromptGate`
(`Tooling/OperatorPromptGate.cs`) — the open-ended counterpart to `ConfirmationGate`: same in-chat
round-trip mechanics (its own `ReceiveChatMessage`/`ReceiveChoices` correlationId, its own
turnstile — `ReceiveChoices` here carries the offered lesson names so `chat.js` can render them as
clickable buttons, same as the yes/no case above), but resolves to the operator's chosen string
(matched against the offered list, by exact name or 1-based index) instead of yes/no.
`ChatHub.SendMessage` offers every incoming message to both gates
(`ConfirmationGate.TryHandleChatReplyAsync` then `OperatorPromptGate.TryHandleChatReplyAsync`)
before treating it as a new command — a known, accepted limitation is that the two gates are
independent turnstiles, so a confirmation and an operator-choice prompt pending at the exact same
moment could make a bare reply ambiguous about which one it answers (not expected in practice: the
simulator lesson flow's own gated tools are strictly sequential).

Connection details (`SimulatorOptions`, `appsettings.json`'s `Simulator` section) are
environment-specific and left blank by default — VMware/`vmrun` paths, the simulator VM's `.vmx`
path, the local lessons folder, the list of network adapter device names to reconnect, and their
retry attempt count/delay. `SimulatorBackend` (top-level config key, default `Fake`) chooses
between this real implementation and `FakeSimulatorService` — an in-memory stand-in (fixed fake
lesson list, "started"/"already running" bookkeeping, no VMware/process calls at all) that still
exercises the entire agent/chat/prompt/confirmation flow end to end with nothing installed, same
reasoning `OperationBackend: Simulated` already establishes on the UAV side. Flip it to `Real` once
`SimulatorOptions` is filled in for an actual machine.

**Two implementations, split across three projects**: `SimulatorService` (the `Real` backend) is
an ordinary class in `Agents/SimulatorAgent/`, alongside `VmwareController`/`LocalLessonRunner`/
`LocalLessonExecutor`/the job queue — no different from any other agent-specific code in
`UavOps.Agent`. `FakeSimulatorService`/`FakeLessonExecutor` (the `Fake` backend) instead live in
their own project, `src/UavOps.Agent.Simulator.Fake`, with their own IoC registration
(`ServiceCollectionExtensions.AddFakeSimulator(this IServiceCollection)`) — `Program.cs` calls that
one extension method when `SimulatorBackend == Fake` rather than registering the fake types itself.
This exists to keep the fake/dev-only implementation physically separable from the main app, not
just logically. The catch: both implementations need to implement `ISimulatorService`/
`ILessonExecutor`/`ISimulatorLessonJobQueue`/`SimulatorLessonJob` — if those lived in
`UavOps.Agent` itself, `UavOps.Agent.Simulator.Fake` would need to reference `UavOps.Agent` to
implement them, but `Program.cs` (in `UavOps.Agent`) also needs to reference
`UavOps.Agent.Simulator.Fake` to call `AddFakeSimulator` — a circular project reference, which
.NET disallows. `src/UavOps.Agent.Contracts` (also holding `OperationResult`/`OperationError`,
since those are equally domain-agnostic and used by both `IOperationService` and
`ISimulatorService`) breaks the cycle: both `UavOps.Agent` and `UavOps.Agent.Simulator.Fake`
reference `Contracts`, and only `UavOps.Agent` references `Simulator.Fake` — one direction, no
cycle.

### Watchdog infrastructure (`Agents/MaintenanceAgent/`)

A fourth, separately reflected domain — health/process control of an externally-running watchdog
process (`IWatchdogService`, 4 ops: `GetServicesHealth`, `StartService`, `StopService`,
`RestartService`) plus editing the declarative service-definition files that decide what the
watchdog launches as its children (`IWatchdogConfigService`, 5 ops:
`ListConfigurations`/`ListConfiguredServices`/`AddConfiguredService`/`UpdateConfiguredService`/
`RemoveConfiguredService`) — deliberately two separate interfaces, not one, since "is X healthy
right now" and "change what X's config says to launch" are materially different, more/less
sensitive capabilities. Both live in `UavOps.Agent.Contracts` and mirror `ISimulatorService`'s
shape (uniform `OperationResult`, `CancellationToken` last), plugging into the same
`OperationCatalog`/`OperationTool` reflection machinery via two more `OperationCatalog` instances.

- **`WatchdogBackend`** (top-level config key, default `Fake`) chooses between the real
  implementation (`Agents/MaintenanceAgent/WatchdogService.cs`/`WatchdogConfigService.cs`, this
  project) and an in-memory fake (`src/UavOps.Agent.Watchdog.Fake`, its own project, registered via
  `AddFakeWatchdog()` — the same three-project split-to-avoid-a-circular-reference reasoning as the
  simulator domain above) — same default-to-Fake-for-zero-setup-dev reasoning as `SimulatorBackend`.
- **Health is polled in the background, never live per tool call**: `WatchdogHealthPoller`
  (`BackgroundService`, registered only under `WatchdogBackend.Real`) polls
  `WatchdogOptions.HealthCheckUrl` (a standard ASP.NET Core HealthChecks JSON endpoint) every
  `PollIntervalSeconds` and writes the result into `IWatchdogHealthStore`; `GetServicesHealth` is
  then a plain in-memory read of that cached snapshot, reporting an explicit
  `{ overallStatus: "Unknown", stale: true }` shape if nothing has been polled yet rather than
  blocking or failing. A blank `HealthCheckUrl` (the out-of-the-box default even under `Real`,
  since it's environment-specific) disables the poller with one log line instead of retrying
  against nothing forever.
- **Health-entry names and real Windows service names are different strings, not assumed to
  match**: `WatchdogOptions.ServiceNameMap` maps the name the model sees (from
  `GetServicesHealth`) to the real Service Control Manager name `StartService`/`StopService`/
  `RestartService` need — a name missing from this map fails with a clear error rather than
  guessing a SCM name that doesn't exist.
- **Service-definition config files** (`IServiceConfigFileStore`, real implementation
  `ServiceConfigFileStore`): one YAML file per named configuration (e.g. `"Flight"`,
  `"Simulator"`) under `WatchdogOptions.ServiceConfigBasePath`, each a list of
  `ServiceConfigEntry` describing a child process the watchdog should launch/supervise.
  `AddConfiguredService`'s `executable` parameter is optional by design — when the operator didn't
  state a full path, `null` is passed and `IServiceExecutableLocator` infers one from the naming
  convention of sibling services already in that configuration, rather than the model inventing a
  path. `IExecutablePathResolver` expands environment-specific placeholder tokens (e.g.
  `%MoavHome%` → `C:\Moav`, via `WatchdogOptions.ExecutablePlaceholders`) only to verify the
  resulting path exists on disk before writing — the file itself always keeps the original,
  unexpanded placeholder, since the watchdog expands these tokens itself at its own runtime.
  `disabled` (not a separate `enabled` field — having both proved confusing for the same entry) is
  the one on/off toggle exposed to chat; every other optional field on `UpdateConfiguredService`
  means "leave unchanged" when `null`, never "clear it".

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
