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
  orchestration (agent-as-tool delegation, rooted at `BrainAgent`, which routes each request to
  either live fleet operations or the training simulator), a reflection-based tool catalog, a
  confirmation gate for mutating actions, structured tool-call logging, and a static SPA
  (`wwwroot/`). Answers UAV operations via either an in-memory simulation
  (`Agents/MoavAgent/Simulation/`, default) or a SignalR bridge
  (`Agents/MoavAgent/Operations/Remote/`) to a connected fleet-commanding app, and drives the
  separate training-simulator environment (a local VMware host/VM, plus lesson scripts run
  directly on this machine) via `Agents/SimulatorAgent/` — see "Architecture" below. `.cs` files
  under `Agents/` are arranged per agent branch — `Agents/MoavAgent/` (the live-fleet plumbing:
  operations, simulation, the UAV-specific SignalR hub) and `Agents/SimulatorAgent/` (the real
  VMware/lesson-runner implementation) — while genuinely cross-cutting infrastructure used by every
  branch (`AgentFactory`, delegation, retrieval, `Tooling/`, `Options/`, `Hubs/ChatHub.cs`) stays at
  the `Agents/`/top level. `BrainAgent` is a pure router with no files of its own. The fake/dev
  stand-in simulator implementation (`FakeSimulatorService`/`FakeLessonExecutor`) and the shared
  interfaces/DTOs both the real and fake implementations depend on (`ISimulatorService`,
  `OperationResult`, etc.) live in two more small projects, `src/UavOps.Agent.Simulator.Fake` and
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

Requires the .NET 8 SDK and Ollama running locally with the main chat model (`ollama pull granite4.1:3b`) pulled.

For the semantic agent-retrieval index, the application supports two modes:
1. **Ollama (Default)**: Pull the retrieval embedding model (`ollama pull nomic-embed-text`) in your local Ollama instance.
2. **In-Memory**: Set `"EmbeddingModel": "InMemory"` in `appsettings.json` to run embeddings completely in-process (recomputes deterministic, normalized 384-dimensional vectors seeded by string hashes). This is highly recommended for offline/standalone development with zero external dependencies.

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
`AgentFactory.BuildMainAgentForTurn` builds the *entire* agent graph fresh for that turn, rooted at
`BrainAgent` (an `AIAgent` from Microsoft.Agents.AI). `BrainAgent` is a pure router: it decides
*live fleet* (`MoavAgent`) vs. *training simulator* (`SimulatorAgent`) and delegates to exactly
one. `MoavAgent` is what used to be the whole entry point (`MainAgent`) before the simulator branch
existed — it still fans out to the four live-fleet specialists (`FlightControlAgent`,
`PayloadControlAgent`, `MissionAgent`, `GdtControlAgent`). `SimulatorAgent` delegates to
`SimulatorInfrastructureAgent`, which owns the VMware/VM/local-lesson tools (see "Simulator
infrastructure" below).

- **Agents are rebuilt every turn**, not cached, because every tool instance closes over that
  turn's `correlationId` so logs/traces are attributable end-to-end. Only the underlying
  `IChatClient` per Ollama model is cached across turns (`AgentFactory._chatClients`).
- **Delegation is "agent-as-tool"**, not a hardcoded router: `DelegateAgentTool` wraps a domain
  `AIAgent` as an `AIFunction` that its parent calls like any other tool — any agent can hold
  these, not just the root. Each call is logged exactly like a real fleet-command call, attributed
  to whichever agent actually built the tool (not a fixed name).
- **Two ways an agent picks its delegates**, both in `AgentFactory.BuildAgentRecursive`:
  - **Explicit (`children:` in YAML)** — an author-declared, deterministic list, always honored
    regardless of anything else. Used for `BrainAgent`/`MoavAgent`/`SimulatorAgent` specifically
    *because* the live-vs-simulator split is safety-relevant and must never depend on embedding
    similarity noise — same reasoning `ChatConfirmationParser` already uses a fixed vocabulary
    instead of LLM classification for yes/no approvals. `children: []` marks a deliberate leaf.
  - **Embedding retrieval (`AgentRetrievalIndex`), the fallback** — for any agent that omits
    `children:` entirely. Every such agent's `description` is embedded once at startup; each turn,
    the operator's raw text is embedded once and the parent's candidates are the top-K
    (`Retrieval:MaxDelegatesPerAgent`) most similar non-visited agents, recursed up to
    `Retrieval:MaxDelegationDepth` levels (`visited` prevents cycles/self-delegation). This is a
    leftover-but-live extension point from an earlier iteration of this design — nothing in the
    current roster actually falls through to it (every agent in the tree declares `children:`
    explicitly, even leaves), but it's kept rather than deleted for any future agent added without
    an explicit position in the tree. Requires `Ollama:EmbeddingModel` — either a real Ollama
    embedding model (e.g. `nomic-embed-text`) or the literal `"InMemory"` for a zero-dependency,
    deterministic, offline `IEmbeddingGenerator` (`Agents/InMemoryEmbeddingGenerator.cs`).
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
  calls (e.g. `SetSpeed` + `SetAltitude`, or a parent agent delegating to two children at once)
  and run them concurrently instead of one at a time.
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
- **Logging**: every tool call — including agent-to-agent delegation at any level — goes through
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
the file, so a filename/field mismatch can't happen), loaded by `AgentConfigLoader` at startup,
which searches recursively (`SearchOption.AllDirectories`) so the files can be nested into
per-agent subfolders that mirror `Agents/`'s layout purely for organization (`BrainAgent/`,
`MoavAgent/` — the four live-fleet specialists plus `MoavAgent.yaml` itself, `SimulatorAgent/` —
itself plus `SimulatorInfrastructureAgent.yaml`); an agent's position in that folder tree plays no
role in the agent graph (that's `children:`, below). No code changes needed to change what an
agent can do or how it's described to the model:

- `instructions` — the agent's system prompt.
- `description` — shown to a parent as this agent's tool description whenever it's delegated to,
  *and* embedded for retrieval ranking when the agent doesn't declare `children:` (required and
  validated non-blank for every agent except `BrainAgent`, the root).
- `temperature` — sampling temperature (lower = more consistent tool-calling decisions for a
  small model).
- `children` — optional explicit, ordered list of this agent's delegates. Omit entirely to fall
  back to embedding retrieval (see above); include (even as `children: []`) to make delegation
  deterministic instead — `BrainAgent`, `MoavAgent`, and `SimulatorAgent` all declare this.
- `tools[]` — operation-backed tools this agent may call. Each entry: `operation` (the tool name
  the LLM sees; for `kind: Operation` — the default — must match a method name on
  `IOperationService` or `ISimulatorService` exactly, case-sensitive), `description`,
  `parameters` (name → description shown to the model — must cover every parameter the operation
  needs that isn't in `fixedParameters`), `fixedParameters` (name → literal value sent every call,
  never shown to the model), `requiresConfirmation` (default `false`; see the confirmation gate
  above — independent per tool, so e.g. `ReturnToLaunch`/`RunSimulatorLesson` can require approval
  while others don't). `kind: OperatorPrompt` instead builds a bespoke ask-the-operator-and-wait
  tool (`Agents/SimulatorAgent/AskOperatorChoiceTool.cs`) — not resolved against any catalog — see
  `SimulatorInfrastructureAgent.yaml`'s `AskOperatorWhichLesson` tool for the only current example.

When editing agent instructions, note the existing prompts are deliberately explicit about not
letting the model invent tail numbers, guess/convert units, or resolve pronouns across
agent-to-agent handoffs (each delegate only sees the instruction text its parent gives it, not the
full conversation) — preserve that style if you touch them.

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
alone wasn't a reliable enough fix). `SimulatorInfrastructureAgent.yaml` tells the model to report
only that the lesson started, never a final outcome, in that same turn.

A single `SimulatorLessonJobProcessor` (`BackgroundService`, registered once regardless of
backend) consumes the queue one job at a time under its own lifetime token (app shutdown only —
not any individual chat request's, so a browser disconnecting mid-lesson can no longer affect a
run already handed to the queue). For each job it: (1) runs `ILessonExecutor.ExecuteAsync` — the
deterministic "what happened" step (`LocalLessonExecutor` for the Real backend, wrapping
`ILocalLessonRunner` and moving the old unhealthy-container detection here — substring match on
`Restarting (` / `Dead`, and `Exited (N)` for non-zero `N`, `Exited (0)` excluded since it's the
normal state for a one-shot/init container; `FakeLessonExecutor` for Fake, a short `Task.Delay`
then a canned outcome — full raw output is logged here for debugging and never passed further);
then (2) builds a **tools-stripped** instance of `SimulatorInfrastructureAgent`
(`AgentFactory.BuildPersonaOnlyAgent` — calls the private `BuildAgent` directly with no tools,
bypassing `BuildAgentRecursive` entirely) and runs it once with a synthetic instruction built from
the concise outcome, to produce the "simple terms" sentence in the same voice as the real agent —
tool-free specifically so it cannot re-trigger anything even if it misreads the prompt; then (3)
pushes the resulting text via the **existing** `ReceiveChatMessage` SignalR event under a fresh
correlationId. `chat.js` needed no changes for this — it already renders any `ReceiveChatMessage`
as a new bubble the first time it sees a correlationId, so the summary appears as a new,
unprompted message in the thread with no frontend work at all.

**Deterministic lesson pre-selection** (`AskOperatorChoiceTool`): the instructions used to say the
model *may* skip asking which lesson to run if the operator already named one — discretion a small
model didn't reliably exercise (it asked anyway). Fixed the same way this codebase already fixes
this class of problem — deterministically, not via model judgment: `AskOperatorChoiceTool` now
receives the turn's raw operator text (threaded through `AgentFactory.BuildAgentRecursive`
alongside the retrieval `query`) and auto-resolves without ever prompting when **exactly one**
offered choice appears in it (matched against the full lesson filename or the name without
`.ps1`); zero or multiple matches falls back to the real prompt, the safe default for anything
ambiguous.

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
OperatorPrompt` in YAML — see "Configuring agents" above) backed by `OperatorPromptGate`
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
