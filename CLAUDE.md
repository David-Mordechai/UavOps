# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A local-LLM agent layer for natural-language UAV command and control (.NET 8). A chat UI turns
operator text (e.g. "fly UAV-1 to target alpha and set speed to 200") into fleet, simulator, and
watchdog commands, via a single flat agent (`BrainAgent`) backed by an OpenAI-compatible chat
endpoint (Ollama locally by default, or a real OpenAI-compatible server such as vLLM — see
`AgentModels` in `appsettings.json`).

Every real operation across all three domains — live fleet ("Moav"), training simulator, watchdog
health/config — is now a tool on a separate **MCP server** (`ModelContextProtocol` SDK), not
in-process C#. `UavOps.Agent` (the host) spawns each as a child `dotnet exec` process over stdio,
discovers its tools, and hands them to BrainAgent. Nothing about what a tool does or how it's
described to the model lives in the host anymore: each MCP server owns its own domain logic *and*
its own `ToolsConfig.yaml` holding every AI-facing string (descriptions, parameter descriptions,
server-wide instructions) — editable without a rebuild, the same principle
`Agents/BrainAgent.yaml` already established for the host's own cross-cutting instructions. See
"Split BrainAgent's 3 domains into separate MCP servers" below for why and how.

Up to five server-side processes end to end, though only one (`UavOps.Agent`) is user-facing:

- **`src/UavOps.Agent`** — the host process: SignalR chat hub (`/chatHub`) for the browser,
  SignalR operation hub (`/uavCommandHub`) for a fleet-commanding client, the single persistent
  `BrainAgent` session, real semantic-embedding tool retrieval narrowing what's offered each turn,
  a confirmation gate for mutating actions, structured tool-call logging, and a static SPA
  (`wwwroot/`). It holds **no domain logic** of its own — only the cross-cutting machinery every
  domain needs (`Agents/AgentFactory.cs`, `Agents/MainAgentOrchestrator.cs`, `Hubs/ChatHub.cs`,
  `Tooling/`, `Options/`).
- **`src/UavOps.Agent.McpMoav`**, **`src/UavOps.Agent.McpWatchdog`**, **`src/UavOps.Agent.McpSimulator`**
  — one MCP server process per domain, each spawned as `UavOps.Agent`'s child. Each owns its real
  domain logic, its own `ToolsConfig.yaml`, and (where relevant) a `Fake` in-memory backend for
  zero-setup local dev — see each domain's own section below.
- **`src/UavOps.Agent.Contracts`** — shared, domain-agnostic types every project above references:
  `OperationResult`/`OperationError`, the domain interfaces (`IOperationService`,
  `ISimulatorInfraService`, `IWatchdogService`, `IWatchdogConfigService`), and the
  `McpToolsConfig`/`McpToolsBuilder` machinery that turns a `ToolsConfig.yaml` plus a plain static
  tool class into real MCP tools (see "Per-server YAML config" below).
- **`src/UavOps.Agent.Simulator.Fake`**, **`src/UavOps.Agent.Watchdog.Fake`** — the in-memory
  stand-in backends for the simulator and watchdog domains, each its own tiny project purely to
  avoid a circular project reference (`UavOps.Agent.McpSimulator`/`McpWatchdog` need to reference
  the fake to register it; the fake needs to implement interfaces from `Contracts`, not from the
  Mcp* project itself) — see "Simulator infrastructure" below for the full reasoning.
- **`src/UavOps.FleetClient`** (.NET Framework 4.7) — a class library a real fleet-commanding
  .NET Framework application references to connect to `UavOps.Agent`'s operation hub; see its
  own README. **`src/UavOps.MockFleetClient`** (.NET Framework 4.7) is a thin console app
  referencing that library with stub command handlers — the dev/test stand-in for the real app.

There is no separate REST API or OpenAPI spec anywhere in this system. Fleet operations are
declared as one C# interface (`IOperationService`, in `UavOps.Agent.Contracts`) and invoked
in-process (inside `UavOps.Agent.McpMoav`) or relayed over SignalR to a real fleet client — never
HTTP, and never routed back through the host beyond that one relay hop.

## Running it

Requires the .NET 8 SDK, Ollama running locally with the main chat model (`ollama pull
granite4.1:3b`) pulled, and a reachable **real embedding endpoint** for tool retrieval (see
below) — there is no offline/fake fallback for embeddings.

**Chat model**: `Ollama:DefaultModel` (`appsettings.json`) is the app-wide default; `AgentModels`
overrides BrainAgent specifically to point at any OpenAI-compatible endpoint instead (e.g. a vLLM
server) — set `AgentModels:Provider` to `"OpenAI"` and `AgentModels:Model`/`OpenAI:Endpoint`
accordingly, plus an API key via `dotnet user-secrets set "OpenAI:ApiKey" "..." --project
src/UavOps.Agent` (`AgentConfigValidator` fails fast at startup if this is missing).

**Tool retrieval embeddings** (`Embedding` section, required, no fallback): a real
`Qwen/Qwen3-Embedding-8B`-class model served on an OpenAI-compatible `/v1/embeddings` endpoint
(e.g. a second local vLLM instance). This is load-bearing for tool-call correctness, not a
nice-to-have — see `ToolRetrievalIndex`'s own doc comment — so an unreachable embedding endpoint
fails the app at boot (`Program.cs`), not silently at the first real operator turn. A hash-based
fake was deliberately removed after `eval/tool-retrieval-lab/` found it ranks tools uncorrelated
with meaning.

No Docker.

```bash
dotnet build UavOps.sln                               # builds the host AND all 3 MCP servers
dotnet run --project src/UavOps.Agent --urls http://localhost:5262
```

The host must be **built** before it's run (`dotnet build`/`dotnet run --project src/UavOps.Agent`
both trigger it via the `.sln`'s own Visual-Studio-style project dependencies, but a bare `dotnet
run` from inside `src/UavOps.Agent` alone will not build the 3 sibling Mcp* projects) — at startup
it spawns each as a child `dotnet exec <already-built-dll>` process (see "Dev vs. deploy MCP
server paths" below); a missing/stale sibling build fails the app immediately with a clear "failed
to connect to MCP server" error, not a silent missing-tools state.

Then open http://localhost:5262. By default (`OperationBackend: Simulated` in
`UavOps.Agent.McpMoav/appsettings.json`, `SimulatorBackend: Fake` / `WatchdogBackend: Fake` in
their own respective `appsettings.json`) every domain answers in-memory, no VMware/real watchdog
process required — only `UavOps.Agent` needs to be launched; it spawns the other three itself.
`GET /healthz` gives a quick sanity check (model in use, number of connected MCP servers, number
of MCP tools discovered, number of tools in the retrieval index).

To exercise the real (or mock) fleet-commanding path instead, set `OperationBackend: SignalR` in
**`src/UavOps.Agent.McpMoav/appsettings.json`** (not the host's — Moav's backend choice lives with
Moav now):

```bash
dotnet run --project src/UavOps.Agent --urls http://localhost:5262
# separately, once UavOps.Agent is up:
src/UavOps.MockFleetClient/bin/Debug/net47/UavOps.MockFleetClient.exe http://localhost:5262/uavCommandHub
```

## Build & test

```bash
dotnet build UavOps.sln                               # whole solution — host, all 3 MCP servers, both net47 projects
dotnet test tests/UavOps.Agent.Tests                   # unit tests — fast, no live deps, includes Category=Live ones by default
dotnet test tests/UavOps.Agent.Tests --filter "Category!=Live"   # fast-only: excludes the live model tests below
dotnet test tests/UavOps.Agent.Tests --filter "Category=Live"    # live model tests only — see "Live test infrastructure" below
dotnet test --filter "FullyQualifiedName~ConfirmationGateTests"   # single test class
dotnet test --filter "FullyQualifiedName~SomeMethodName"          # single test method
```

`src/UavOps.FleetClient` and `src/UavOps.MockFleetClient` target `net47` (via the
`Microsoft.NETFramework.ReferenceAssemblies` NuGet package, since this isn't a Windows-installed
targeting pack on every machine) but are ordinary SDK-style projects — `dotnet build` handles them
like any other project in the solution. `dotnet run` does **not** work for them (classic .NET
Framework binaries aren't launched that way) — run the built
`src/UavOps.MockFleetClient/bin/Debug/net47/UavOps.MockFleetClient.exe` directly.

`UavOps.sln` lists all 3 Mcp* projects directly (`dotnet sln add`), not just reachable transitively
via `UavOps.Agent.Tests.csproj`'s `ProjectReference`s — confirmed necessary the hard way: a project
only reachable transitively does **not** reliably inherit `-c Release` from `dotnet build
UavOps.sln -c Release` and silently stayed in `bin/Debug/net8.0`. `UavOps.Agent`'s own entry in the
`.sln` additionally lists the 3 Mcp* projects as Visual Studio "Project Dependencies" (a
build-ordering hint distinct from a compile-time `ProjectReference`, since the host never calls
their code directly — only spawns them as processes) so that F5-debugging the host in VS 2022
rebuilds them first too.

Two live/opt-in test surfaces exist, answering different questions:

- **`Category=Live` tests inside `tests/UavOps.Agent.Tests`** (`Agents/*LiveTests.cs`) drive the
  real live chat pipeline end to end (`MainAgentOrchestrator.HandleAsync`, real MCP child
  processes, real model, real embeddings) against hand-written scenarios, asserting real
  post-condition state (e.g. real fleet telemetry), not just "a plausible-looking summary was
  returned" — see "Live test infrastructure" below for how these are organized and made fast to
  iterate on.
- **`tests/UavOps.Agent.Evals`** is a **separate, opt-in** suite (not part of `UavOps.sln`, not
  part of the normal `dotnet test` inner loop) — it drives the pipeline over the real SignalR chat
  hub (not in-process) against a golden set of operator utterances in `eval/golden-commands/*.yaml`.
  It requires Ollama and `UavOps.Agent` actually running (`LiveDependenciesFixture` checks
  reachability and fails loudly, not silently, if something isn't up):

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
anywhere** — this replaced an earlier multi-agent tree (`BrainAgent` → per-domain leaf specialists)
after live testing and a pair of standalone baselines (`eval/single-agent-baseline/`, a pure-Python
HTTP script, and `eval/single-agent-baseline-dotnet/`, matching this app's own `Microsoft.Agents.AI`
construction path) established that the delegation-hop structure itself — not the model, not the
.NET Agent Framework — was the source of fabricated success claims (a leaf agent, or `BrainAgent`
itself, confidently reporting an action succeeded with zero underlying tool calls). Both baselines
scored 8/8 on the exact scenarios that fabricated live, using a flat, no-delegation design; a
standalone lab (`eval/tool-retrieval-lab/`) then validated that a flat design scales correctly to
far more tools than this app needs (1000 synthetic tools, 100% pass rate) once real embedding-based
retrieval narrows what's offered each turn — both were then ported into this app directly, and
later still hold true once every domain's tools moved out into separate MCP server processes (that
move changed *where* a tool executes, never *whether* the flat, no-delegation, real-retrieval
design is what BrainAgent itself sees or calls).

- **BrainAgent holds no tools of its own at rest** — every real tool is discovered from connected
  MCP servers (`AgentFactory.McpTools`, populated once at startup by `Program.cs`'s MCP connection
  loop) and wrapped fresh per turn (`AgentFactory.BuildAllTools`, one loop, no recursion, no
  per-domain branching in the host).
- **Tools are rebuilt fresh every turn**, not cached, because every tool instance closes over that
  turn's `correlationId` so logs/traces are attributable end-to-end. `BrainAgent` itself — and its
  one reused `AgentSession` — is the opposite: built once and cached for the app's lifetime (see
  "Persistent session" below); only its *tools* are swapped in per turn via
  `ChatClientAgentRunOptions`. The underlying `IChatClient` per model is also cached across turns
  (`AgentFactory._chatClients`); the raw `McpClientTool` list itself is fetched once at startup, not
  re-fetched per turn — connecting to another MCP server never adds a per-turn network/IPC hop.
- **Live semantic tool retrieval** (`ToolRetrievalIndex`, `Retrieval:TopK` in `appsettings.json`,
  default 10): `BuildToolsForTurn` embeds the operator's raw turn text and ranks it by cosine
  similarity against every tool's own `(name, description)` embedding (computed once at startup
  from a template tool list, `AgentFactory.BuildTemplateTools`), returning only the top-K most
  relevant tool names for that turn. This is the *only* thing narrowing what a turn sees — nothing
  is force-appended regardless of ranking. Requires a real embedding model
  (`Options/EmbeddingOptions.cs`, see "Running it" above) — a hash-based fake was found, in the
  standalone lab, to rank tools uncorrelated with meaning, which is actively dangerous once ranking
  quality is load-bearing for correctness rather than a nice-to-have.
- **`tool_choice` is left at its default, "auto" — never forced**, and there is no verified-retry
  loop. An earlier version of this flat design forced `tool_choice: "required"` on a turn's first
  completion plus a 3-attempt retry that rolled back BrainAgent's own history whenever a completion
  produced zero tool calls — built after a real, live-reproduced incident where BrainAgent answered
  an actionable fleet-wide command with a fully fabricated success report and zero tool calls,
  under the old multi-agent delegation architecture. Both were removed after direct, repeated
  evidence that neither is needed for the flat architecture that replaced that delegation tree: the
  two standalone baselines above, both using unforced `tool_choice: "auto"`, never exhibited the
  fabrication forcing was meant to prevent; meanwhile forcing actively broke a *different* real
  scenario (a bare greeting deterministically producing no tool call at all when forced — confirmed
  8/8 via a repeated live regression test, something auto-mode never did). Removing both fixed the
  greeting regression with no loss of the anti-fabrication property, since that property comes from
  the flat architecture itself, not from forcing or retrying. See
  `MainAgentOrchestrator`'s own doc comment for the full evidence trail, and
  `FlyAllFleetWideLiveTests.FlyAllFleetWideCommand_NeverClaimsSuccessWithoutRealMutation` for the
  current live regression coverage. What this does **not** change: `ConfirmationGate`/
  `OperatorPromptGate` requiring real operator sign-off, and `TailNumberDisambiguationTool`'s
  grounding of every tail number against the real fleet — those were never compensating for model
  unreliability, they're an intentional safety boundary and stay regardless of model quality.
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
- **Per-tool safety wrapping is decided from a tool's own JSON schema, not from which domain/server
  it came from** (`AgentFactory.BuildAllTools`): any MCP tool whose schema declares a `location`
  property gets wrapped in `LocationCanonicalizationTool`; any tool whose schema declares a
  `tailNumber` property gets wrapped in `TailNumberDisambiguationTool` (see "Tail-number
  disambiguation" below) — in that order, so a fan-out re-invocation inside the tail-number wrapper
  also sees the canonicalized location. This is why the host needs zero per-domain branching: it
  never has to know "this tool is a Moav tool" to decide how to wrap it.
- **Concurrent tool calls**: `FunctionInvokingChatClient(inner) { AllowConcurrentInvocation = true
  }` plus `ChatOptions.AllowMultipleToolCalls = true` lets one model turn batch several tool calls
  (e.g. `SetSpeed` + `SetAltitude`, or a fleet-wide fan-out across several UAVs) and run them
  concurrently instead of one at a time.
- **Confirmation gate** (`ConfirmationGate`): read directly off the MCP server's own protocol-native
  tool annotations (`Tool.Annotations.ReadOnlyHint`/`DestructiveHint`, set server-side via
  `McpServerToolCreateOptions.ReadOnly`/`Destructive` — see "Per-server YAML config" below), never a
  host-side name list. `ReadOnlyHint` and `DestructiveHint` are **independent** flags in the MCP
  spec — setting one does not imply the other — so `AgentFactory.RequiresConfirmation` returns
  `false` whenever `ReadOnlyHint == true`, and otherwise falls back to `DestructiveHint` (which
  defaults to `true`, "assume destructive", when a tool leaves it unset). Every tool across all 3
  servers has an explicit annotation specifically so that conservative default doesn't silently
  make everything require confirmation. This was a real, live-found bug: an earlier version only
  ever checked `DestructiveHint`, so every read-only query tool (`ListFleet`, `GetTelemetry`,
  `GetLinkStatus`, `GetMissionStatus`, `GetServicesHealth`, `ListConfigurations`,
  `ListConfiguredServices`, `ListSimulatorLessons`) silently required confirmation — never asserted
  as wrong by the live test suite (whose ground-truth checks bypass this wrapper entirely) but very
  likely the true cause of an earlier unexplained live-suite slowdown that had instead been chased
  as GPU/network load. `AgentGraphProjector` calls the exact same `RequiresConfirmation` method
  (not a separate copy) for its tooltips, specifically because the bug above happened when two
  copies of this check existed and only one got fixed.

  Independent of the annotation check above, confirmation is additionally gated by
  `ExecutionMode` (`"Confirm"` or `"Direct"`, read live from `IConfiguration` on every call so
  editing `appsettings.json` takes effect on the next tool call with no restart; defaults to
  `Confirm` if missing/invalid). The approval round-trip happens **in chat**: the prompt and its
  resolution are sent as ordinary `ReceiveChatMessage` events (under a correlationId of their own,
  so they render as their own bubble instead of overwriting the turn that triggered them), and a
  `ReceiveChoices` event carries `["Yes", "No"]` so `chat.js` can render them as clickable buttons.
  The operator's reply is parsed by `ChatConfirmationParser` (a small fixed yes/no vocabulary —
  deliberately not an LLM classification, since approving a UAV operation is safety-relevant and
  needs a deterministic, auditable interpretation). `ChatHub.SendMessage` offers every incoming
  message to `ConfirmationGate.TryHandleChatReplyAsync` before treating it as a new operation. Only
  one confirmation can be outstanding at a time (a `SemaphoreSlim` turnstile in `ConfirmationGate`)
  — with concurrent tool invocation enabled, two mutating calls could otherwise both need approval
  at once, which would make a bare "yes" reply ambiguous about which one it answers.
- **Logging**: every tool call goes through `ToolInvocationLogger`, producing one structured log
  line (tool, args, result, duration, correlation ID — persisted to both console and
  `logs/uavops-agent-<date>.log` via Serilog) and one `ReceiveAgentTrace` SignalR event, so the chat
  UI's reasoning panel and the eval suite's trace assertions see identical data.
- **SignalR concurrency note**: `MaximumParallelInvocationsPerClient = 10` is set once in
  `Program.cs` and covers both hubs mapped off it (`/chatHub`, `/uavCommandHub`) — needed because
  (a) the operator's chat reply to a pending confirmation is itself just another `SendMessage` call
  that must reach the hub while the *original* `SendMessage` call is still in flight, and (b)
  several operations can be in flight to a connected fleet client (or a Moav MCP server relaying
  over SignalR) at once, each needing to reply without queuing behind another still-processing
  reply. SignalR's default limit of 1 would otherwise queue these behind the in-progress call until
  they time out.

### Split BrainAgent's 3 domains into separate MCP servers

Fleet, simulator, and watchdog tools used to be reflected in-process from four C# interfaces
directly inside `UavOps.Agent`. They now each live in their own MCP server project
(`UavOps.Agent.McpMoav`/`McpWatchdog`/`McpSimulator`), spawned as a stdio child process and
connected to via the `ModelContextProtocol` SDK's `McpClient`/`StdioClientTransport`. Two
requirements drove this, independent of each other:

1. **Genuine process isolation per domain** — a domain's own dependencies (VMware/`vmrun` for the
   simulator, the Windows Service Control Manager for the watchdog) no longer need to be
   installed/reachable from the same process serving chat, and a domain crashing doesn't take the
   whole host down.
2. **Every AI-facing string must live in that domain's own YAML, not C# attributes** — changing a
   tool's description or a parameter's wording must never require a recompile, matching how
   `Agents/BrainAgent.yaml` already worked for the host's own instructions. This ruled out the
   MCP SDK's usual `[McpServerTool]`/`[Description]` attribute-based discovery
   (`WithToolsFromAssembly()`), since attribute text is compiled in.

**Per-server YAML config** (`src/UavOps.Agent.Mcp*/ToolsConfig.yaml`, one per server): each holds
a `serverInstructions:` block (folded into BrainAgent's system prompt once at startup — see
`Program.cs`'s MCP connection loop) and a `tools:` list, one entry per tool method — `operation`
(must match a real method name on that project's tool class, case-sensitive), `description`,
`readOnly`/`destructive` (nullable — a tool can be left unannotated to keep the SDK's own
conservative "assume destructive" default; only `RunSimulatorLesson` does today), and `parameters`
(name → description shown to the model). `src/UavOps.Agent.Contracts/McpToolsConfig.cs`
(`McpToolsConfigLoader.Load`, YamlDotNet, same `CamelCaseNamingConvention` as `BrainAgent.yaml`'s
own loader) parses this; `McpToolsBuilder.Build(toolsType, config, services)` does the actual
programmatic tool construction — verified directly against the installed `ModelContextProtocol`
2.2.0 SDK's own XML docs rather than assumed:

- `McpServerTool.Create(MethodInfo, target: null, McpServerToolCreateOptions { Name, Description,
  ReadOnly, Destructive, Services, SchemaCreateOptions })` builds one tool from a plain reflected
  **static** method with no attributes at all.
- `SchemaCreateOptions.ParameterDescriptionProvider` (a `Func<ParameterInfo, string?>`) supplies a
  parameter's description from the YAML `parameters` map during schema generation — the mechanism
  that replaces `[Description]` on a parameter.
- `Services` (a throwaway `IServiceProvider` built from the same `IServiceCollection` before
  `Build()`) only needs to reflect the same DI *registrations* as the real runtime provider, per
  the SDK's own docs — used solely to decide which parameters are DI-satisfied (and therefore
  excluded from the tool's JSON schema, e.g. an injected `IOperationService`), not to actually
  resolve anything at build time.
- `McpToolsBuilder.Build` fails fast (same discipline as `AgentConfigValidator`) if a method has no
  matching YAML entry, a YAML entry has no matching method, a description is empty, or a
  non-DI/non-`CancellationToken` parameter has no `parameters` entry — the MCP-side equivalent of
  the host's own `AgentConfigValidator`, scoped to one server's tool surface.
- Each project's `Program.cs` then does `.AddMcpServer(o => o.ServerInstructions =
  toolsConfig.ServerInstructions).WithStdioServerTransport().WithTools(tools)` — registering the
  pre-built tool instances directly, not `WithToolsFromAssembly()`.

Each `Mcp*Tools` class (`MoavTools`, `WatchdogTools`, `SimulatorTools`) is consequently just a
plain `static class` of methods with the DI-satisfied services and mechanical parameters as normal
C# parameters — no `[McpServerTool]`, no `[McpServerToolType]`, no `[Description]` anywhere.
Editing a tool's wording is a YAML edit and a process restart; adding a parameter's description
without one is a startup failure with a clear message naming exactly which tool/parameter is
missing it.

**Dev vs. deploy MCP server paths** (`Agents/BrainAgent.yaml`'s `mcpServers:` list, `Program.cs`):
each server entry's `args` reference a sibling project's build output with a `{configuration}`
placeholder, e.g. `["exec", "../UavOps.Agent.McpMoav/bin/{configuration}/net8.0/UavOps.Agent.McpMoav.dll"]`.
Two distinct resolution paths exist, deliberately not unified into one mechanism, because a dev
build's output and a real deployment's published output aren't just "Debug vs. Release" — they're
different directory *shapes* entirely (`dotnet publish` does not produce a `bin/<Configuration>/
net8.0/` tree at all — confirmed directly with a real `dotnet publish` run after an earlier version
of this exact reasoning got it wrong by assuming otherwise):

1. **Dev/debug default** (Visual Studio F5, `dotnet run`, or building this checkout in place):
   `{configuration}` is substituted at startup with whatever configuration *this host binary
   itself* was compiled as (`#if DEBUG`/`#else`, a compile-time constant) — so the same checked-in
   YAML always points at the matching sibling build, dev or Release, with no per-environment
   editing. Requires each Mcp* project to already be built with the *same* configuration as the
   host (`dotnet build UavOps.sln -c Release`, or VS's own build-ordering via the `.sln`'s Project
   Dependencies — see "Build & test" above).
2. **Deploy override**: `McpServerPaths:<name>` in `appsettings.json` (left blank by default —
   plain JSON has no comment syntax to explain that inline) is checked *first*, per server; when
   set (via `appsettings.Production.json`, or an `McpServerPaths__<name>` environment variable, set
   once by whatever deploys this app — never by hand-editing `BrainAgent.yaml`) it replaces that
   server's dll path outright, however different the real published layout is from the dev `bin/`
   tree. Validated end to end: publish to a flat folder, set the override, confirm it's used,
   delete the folder, confirm the resulting error names the override path itself (no silent
   fallback to the dev path), unset it, confirm the dev path resumes working.

**Cross-process capabilities a domain needs from the host** go through `ChatHub`'s own client-role
model, not a new mechanism: `UavOps.Agent.McpSimulator` and `UavOps.Agent.McpMoav` (under
`OperationBackend: SignalR`) each connect back to the host's `/chatHub`/`/uavCommandHub` as their
*own* SignalR client, alongside whatever else `ChatHub` is already doing for the browser and a real
fleet client. Two such capabilities exist today:

- `McpMoav` (`MoavRelayService`) calls one of a dozen typed `Relay*` methods on `ChatHub` (e.g.
  `RelayNavigate`) to reach a real connected fleet client — the exact same
  `IRemoteOperationBroker.SendAsync` round trip `UavOps.Agent` used to perform in-process before
  the Moav domain moved to MCP, invoked from the other side of a new process boundary now.
- `McpSimulator` (`SimulatorTools.AskOperatorWhichLesson`) calls `ChatHub.RelayAskOperatorChoice`
  to run the actual in-chat "which lesson do you mean?" round trip through the host's own
  `OperatorPromptGate` — the domain's own deterministic auto-resolve (`LessonChoiceResolver`, see
  "Deterministic lesson pre-selection" below) settles this without a host round trip whenever it
  can; the relay is only the fallback path.
- `McpSimulator` (`HubLessonOutcomeNotifier`) pushes a background lesson's outcome back via the
  existing `ReceiveChatMessage` event under a fresh correlationId (see "Background lesson
  execution" below) — this is the same mechanism, reused, not a third one.

`HubConnectionStarter` (a `BackgroundService` in `McpSimulator`) starts this connection with a
retrying loop rather than blocking the server's own startup on it — live-reproduced without this:
`await hubConnection.StartAsync()` called synchronously before serving any tools threw unhandled
and crashed the whole process whenever no host happened to be listening yet (e.g. under a test
harness with no real host running at all).

**The chat UI's Agent Graph tab** (`AgentGraphProjector`, `wwwroot/graph.js`) renders a real 3-tier
graph sourced from `AgentFactory.McpServerToolGroups` (not YAML): `BrainAgent` → each connected MCP
server (a `"server"`-typed node, edge kind `"connects"`) → each of that server's own tools (edge
kind `"uses"`), with every tool's real, live-discovered name/description/`requiresConfirmation`/
parameter list — nothing about a tool's graph representation is configured in the host.

### Tail-number disambiguation (`Tooling/TailNumberDisambiguationTool.cs`, `TailNumberResolutionScope.cs`)

Wraps any Moav tool whose schema has a `tailNumber` parameter. BrainAgent's own instructions
already say not to guess a tail number and to ask the operator instead when more than one UAV
exists — but a small local model doesn't reliably follow that (observed directly: it calls
`ListFleet`, sees the known tail numbers, and picks one anyway in the large majority of trials).
This tool makes the check deterministic: if the model's guessed `tailNumber` doesn't literally
appear anywhere in the operator's own turn text, and the fleet has more than one known UAV, the
guess is blocked and the operator is asked for real via `OperatorPromptGate`, offering every known
tail number plus the literal sentinel `ALL`.

- **`ALL`** is the model's own explicit, structured signal that every UAV is meant (per each tool's
  own parameter description: literal words like "all"/"every"/"both"/"the fleet", or a plural
  pronoun referring back to a fleet already listed earlier in the conversation). That judgment
  alone is never trusted to execute directly — it's still routed through a deterministic
  confirm-or-ask step (`ResolveAllUavsRequestAsync`) before `InvokeForAllUavsAsync` fans out to
  every known UAV, one real per-UAV invocation each, sequentially (not concurrently, so a
  `requiresConfirmation` operation like `ReturnToLaunch` asks once per UAV in a predictable order
  rather than racing several confirmation prompts). Both this fan-out and the ask-which-UAV
  resolution above are deduplicated per turn via `TailNumberResolutionScope` (`GetOrFanOutAsync`,
  keyed by tool name; `GetOrAskAsync`, see below) — without this, the model issuing the same tool
  call twice in one completion (a real, live-reproduced pattern) would independently re-fan-out
  across the whole fleet each time, multiplying real mutations (3 duplicate calls × 3 known UAVs =
  9 real invocations instead of 3, observed live).
- **`GetOrAskAsync` is keyed by the model's own guessed tail-number value**, not a single shared
  slot — fixed after a real, live-reported safety bug: with a single un-keyed slot, *any*
  ambiguous tail-number call in a turn reused the *first* call's answer regardless of what it
  actually guessed. Reported live: "bring the rest UAVs home" with UAV-1 already returned home
  issued two `ReturnToLaunch` calls (one intended for UAV-2, one for UAV-3); the first asked and
  resolved to whatever the operator answered, and the *second call silently inherited that same
  answer* instead of asking about its own, genuinely different guess — every subsequent
  `ReturnToLaunch` that turn executed against the same single UAV, and the model itself could see
  something was wrong (identical results for calls it made with different arguments) without
  knowing why. Keying the cache by the guessed value fixes this: two different guesses are two
  different UAVs as far as the scope is concerned, each gets its own independent prompt, while two
  calls that happen to guess the *same* value (the original, valid use case — e.g. `SetSpeed`
  immediately followed by `SetAltitude`, both defaulting to the same ungrounded guess for one
  truly ambiguous UAV) still correctly dedupe.
- **An explicit multi-target subset is a single comma-separated `tailNumber` value** (e.g.
  `"UAV-2,UAV-3"`), resolved deterministically against the real fleet and fanned out to *exactly*
  that set — added after live testing showed the model was unreliable at completing a
  multi-target request by issuing one separate tool call per remaining UAV (the "bring the rest
  home" scenario above: even with the keying fix, the model correctly resolved and executed the
  *first* call and then simply never attempted the second, 6 times out of 8 trials). Each tool's
  parameter description now tells the model to compute the real tail numbers for phrases like "the
  rest"/"the other two" itself (calling `ListFleet` first if needed) and pass them together in one
  call, rather than relying on it to issue — and remember to issue — a separate call per UAV.
  Deliberately **not** implemented as "treat 'the rest' as `ALL`": some operations (e.g.
  `UploadWaypoints`, `SetTrackingMode`) are not safe to redundantly re-apply to a UAV the operator
  meant to exclude, unlike `ReturnToLaunch`, so the subset mechanism only ever touches the UAVs
  named, never the whole fleet. Verified live: `ReturnRemainingFleetLiveTests` replays the exact
  reported conversation end to end and checks real fleet telemetry afterward — 2/8 verified
  successes before this fix, 8/8 after, each repeat also roughly 10x faster (no more waiting
  through a disambiguation-prompt round trip per remaining UAV).

### Live test infrastructure (`tests/UavOps.Agent.Tests/Agents/LiveTestSupport.cs`)

`Category=Live` scenarios (one per `[Fact]`-bearing class, e.g. `MultiTurnFleetOpsLiveTests`,
`ReturnRemainingFleetLiveTests`) each call `LiveTestSupport.BuildLiveOrchestrator()` to stand up a
real `MainAgentOrchestrator` against whatever backend/model `src/UavOps.Agent/appsettings.json`
(plus user secrets) actually configures — reading the exact same config the real app would, not a
hardcoded test model, since a fix validated against the wrong model proves nothing about deployed
behavior. Each repeat spawns fresh child MCP server processes (`McpClientGroup`, disposed via
`await using`), so no in-memory state leaks between repeats.

- **Split into separate classes, not methods of one class, specifically for real parallelism**:
  xUnit only parallelizes across separate test classes (each its own implicit "collection" by
  default) — methods within one class always run sequentially regardless of what else is true.
  With every scenario previously a method of one class, they always ran one after another even
  though each is fully independent; splitting them cut real wall-clock time for the full live
  suite substantially.
- **A real-time, file-based progress log** (`LiveTestSupport.ProgressLogPath`, `%TEMP%
  \uavops-live-test-progress.log`) exists because neither `ITestOutputHelper.WriteLine` nor plain
  `Console.WriteLine` streams live through `dotnet test` — confirmed empirically that VSTest only
  reports progress at whole-test-*case* granularity, batching everything written during a
  still-running test regardless of channel. `LiveLog` appends directly to this file (under a
  shared in-process lock — `File.AppendAllText` doesn't tolerate true concurrent writers on
  Windows, confirmed live when splitting into parallel classes first caused real `IOException`s on
  their first simultaneous write) so a live run's genuine current progress can be tailed at any
  time instead of waiting for a whole test, or the whole run, to finish.
- **`LIVE_TEST_REPEATS`** (env var, default 8 — this project's "a single pass proves nothing about
  reliability" standard) overrides how many times each scenario repeats:
  `LIVE_TEST_REPEATS=1 dotnet test tests/UavOps.Agent.Tests --filter Category=Live` runs the whole
  suite once each in a few minutes, for fast iteration while actively changing something; use the
  full default before considering a change actually validated.
- **`ApproveAnyPendingConfirmationAsync`/`AnswerOperatorPromptsAsync`** answer a pending
  `ConfirmationGate`/`OperatorPromptGate` round trip directly (these tests call
  `MainAgentOrchestrator.HandleAsync` with no real chat hub in the loop, so nothing else would ever
  answer one) — the latter takes an ordered list of answers, since a single turn can now
  legitimately open more than one independent operator prompt (see "Tail-number disambiguation"
  above).

### The Moav domain (`src/UavOps.Agent.McpMoav`)

`UavOps.Agent.Contracts`'s `IOperationService.cs` is the single source of truth for what a Moav
operation is — 12 async methods (`ListFleet`, `GetTelemetry`, `Navigate`, `SetSpeed`,
`SetAltitude`, `ReturnToLaunch`, `PointPayload`, `ResetPayload`, `UploadWaypoints`,
`GetMissionStatus`, `GetLinkStatus`, `SetTrackingMode`), each returning `Task<OperationResult>` —
a uniform, non-generic envelope (`Success`, `Error` enum, `ErrorMessage`, boxed `object? Value`) so
`MoavTools` can wrap any of the 12 with a one-line method (`ToResultText(await moav.Method(...))`)
with **no `dynamic`, no per-operation switch statement**. `ListFleet` keeps "Fleet" in its name
specifically because `Hubs/IOperationClientProxy.cs`'s matching method name is pinned by the
(unchanged) net47 `UavOps.FleetClient` wire contract.

`McpMoav`'s own `appsettings.json`'s `OperationBackend` (`"Simulated"` or `"SignalR"`) chooses
which `IOperationService` implementation its `Program.cs` registers:

- **`Simulated`** (default) — `SimulatedUavOperationService`, in-memory, no persistence, no
  external dependency. `KnownPoints.cs` (in `Contracts`, since `MainAgentOrchestrator` also needs
  it — see below) resolves named locations (e.g. `"target alpha"`, `"home"`) to lat/lng. This is
  what local dev and `UavOps.Agent.Evals` run against by default.
- **`SignalR`** — `MoavRelayService`, which calls one of the host's dozen typed `Relay*` methods on
  `ChatHub` per operation (see "Cross-process capabilities" above) — the same
  request/response-over-SignalR pattern `ConfirmationGate` uses (send a message to a connected
  client, await a correlated reply with a timeout), generalized in `IRemoteOperationBroker`/
  `RemoteOperationBroker` for multiple operations in flight at once: pending replies are keyed by
  correlation id in a `ConcurrentDictionary`, and messages target one specific connection
  (`Clients.Client(id)`) instead of broadcasting. Connection tracking lives in the broker, not the
  Hub (Hub instances are per-call/transient in SignalR) — last-writer-wins if a second client
  connects (logged as a warning), and a stale disconnect can't clobber a newer connection. Fails
  fast with no client connected, before any correlation id/timeout machinery is even created;
  otherwise times out after `RemoteOperation:TimeoutSeconds` (default 10s, configured on the
  *host*, since the broker lives there).

`KnownPoints.CanonicalizeText` is applied at two points: inside `LocationCanonicalizationTool`
(wrapping any Moav tool with a `location` parameter, so a fan-out re-invocation also sees the
canonical value) and again, as a final safety net, directly in `MainAgentOrchestrator.HandleAsync`
on the model's own response text — the one place common to every domain where operator-facing text
is finalized, so a location name (e.g. "target alpha" vs. bare "alpha") reads consistently
regardless of which domain a turn touched.

### Simulator infrastructure (`src/UavOps.Agent.McpSimulator`)

`UavOps.Agent.Contracts`'s `ISimulatorInfraService.cs` mirrors `IOperationService`'s shape exactly
(uniform `Task<OperationResult>`, `CancellationToken` last). Three operations, all local to the
machine `McpSimulator` runs on — no SSH/network hop anywhere in this domain, since the lesson
scripts live alongside the server, not on the VM: `EnsureVmwareHostRunning`/
`EnsureSimulatorVmRunning` (check-and-start-if-needed, via `IVmwareController` — local
`Process`/`vmrun.exe`, since there's no VMware .NET SDK) and `ListSimulatorLessons` (via
`ILessonLister`). `RunSimulatorLesson` (a fourth `SimulatorTools` method, backed by
`ISimulatorLessonJobQueue` directly rather than the infra service) doesn't run the lesson itself —
see "Background lesson execution" below. `LocalLessonRunner` validates every resolved lesson path
stays inside the configured lessons folder (`Path.GetFullPath` + prefix check) before running
anything — the lesson name ultimately originates from the operator's chat reply, so it's never
trusted as a bare filename.

**Virtual network adapters**: `EnsureSimulatorVmRunning` (re)connects every device name in
`SimulatorOptions.NetworkAdapterDeviceNames` (e.g. `"ethernet0"`, `"ethernet1"`) via `vmrun
connectNamedDevice` — VMware Workstation can silently fail to auto-connect one of several virtual
NICs on power-on (the greyed-out adapter you'd otherwise right-click → Connect in the UI). `vmrun`
has no documented way to *query* a device's live connection state, only to force a connect, so
this runs unconditionally rather than "check then fix" — reconnecting an already-connected adapter
is a harmless no-op. Each adapter gets up to `NetworkAdapterReconnectAttempts` tries
(`NetworkAdapterReconnectRetryDelaySeconds` apart), each attempt logged individually — added after
production showed two adapters failing on their only attempt with no way to tell from one data
point whether that's a permanent config issue or the adapter simply not being ready an instant
after power-on. Best-effort per adapter (one exhausting all attempts doesn't fail the whole "is the
VM ready" result — reported via `networkAdaptersFailedToReconnect`, not fatal).

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
turn. `SimulatorTools.RunSimulatorLesson` is deliberately thin: it just calls
`ISimulatorLessonJobQueue.Enqueue(...)` (a `System.Threading.Channels.Channel`-backed queue, not
just a "busy" flag — a second request while one is running waits its turn instead of being
rejected) and returns `{ status: "queued" }` immediately — the entire tool result the model sees
for that turn, so it never has to parse a docker dump (a real run's summary once claimed "no
errors reported" while the raw output plainly showed two containers crash-looping, `Restarting
(127)`, buried in ~30 lines of table text a small model didn't reliably scan — wording alone
wasn't a reliable enough fix). `ToolsConfig.yaml`'s `serverInstructions` tell the model to report
only that the lesson started, never a final outcome, in that same turn.

A single `SimulatorLessonJobProcessor` (`BackgroundService`, registered unconditionally) consumes
the queue one job at a time under its own lifetime token (this process's own shutdown only). For
each job it: (1) runs `ILessonExecutor.ExecuteAsync` — the deterministic "what happened" step
(`LocalLessonExecutor` for the Real backend, wrapping `ILocalLessonRunner`, substring-matching
`Restarting (` / `Dead`, and `Exited (N)` for non-zero `N` — `Exited (0)` excluded since it's the
normal state for a one-shot/init container; `FakeLessonExecutor` for Fake, a short `Task.Delay`
then a canned outcome — full raw output is logged here for debugging and never passed further);
then (2) calls back into the *host* over the `HubLessonOutcomeNotifier`/`ReceiveChatMessage`
mechanism (see "Cross-process capabilities" above) so `UavOps.Agent`'s own persona/model can phrase
the "simple terms" summary in BrainAgent's own voice — deliberately not phrased by
`UavOps.Agent.McpSimulator` itself, since only the host holds BrainAgent's actual persona/model.
`chat.js` needed no changes for this — it already renders any `ReceiveChatMessage` as a new bubble
the first time it sees a correlationId, so the summary appears as a new, unprompted message with
no frontend work at all.

**Deterministic lesson pre-selection** (`LessonChoiceResolver.TryAutoResolve`, called from
`SimulatorTools.AskOperatorWhichLesson` before ever relaying to the host): the instructions used to
say the model *may* skip asking which lesson to run if the operator already named one — discretion
a small model didn't reliably exercise (it asked anyway). Fixed deterministically instead:
`AskOperatorWhichLesson` receives the turn's raw operator text and auto-resolves without ever
prompting when **exactly one** offered choice appears in it (matched against the full lesson
filename or the name without `.ps1`); zero or multiple matches falls back to the real prompt (via
`ChatHub.RelayAskOperatorChoice` → the host's own `OperatorPromptGate`), the safe default for
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
and its failure messages fall back to stdout when stderr is empty — also observed in production: a
real `connectNamedDevice` failure had a nonzero exit code and completely empty stderr, and would
have silently discarded whatever `vmrun` actually wrote to stdout instead.

Connection details (`SimulatorOptions`, this project's own `appsettings.json`'s `Simulator`
section) are environment-specific and left blank by default — VMware/`vmrun` paths, the simulator
VM's `.vmx` path, the local lessons folder, the network adapter device names to reconnect, and
their retry attempt count/delay. `SimulatorBackend` (top-level key, default `Fake`) chooses between
the real implementation above and `FakeSimulatorInfraService`/`FakeLessonExecutor` (in
`src/UavOps.Agent.Simulator.Fake`, its own project, registered via
`ServiceCollectionExtensions.AddFakeSimulatorInfra()` — kept physically separable from the real
implementation, not just logically, and split into its own project specifically to avoid a
circular reference: both the Fake project and `UavOps.Agent.McpSimulator` need to implement/
reference the same interfaces, which live in `Contracts` for exactly this reason). The Fake backend
is a fixed in-memory lesson list plus "started"/"already running" bookkeeping, no VMware/process
calls at all — it still exercises the entire agent/chat/prompt/confirmation flow end to end with
nothing installed, same reasoning `Simulated` establishes for the Moav domain. Flip it to `Real`
once `SimulatorOptions` is filled in for an actual machine.

### Watchdog infrastructure (`src/UavOps.Agent.McpWatchdog`)

A domain covering health/process control of an externally-running watchdog process
(`IWatchdogService`, 4 ops: `GetServicesHealth`, `StartService`, `StopService`, `RestartService`)
plus editing the declarative service-definition files that decide what the watchdog launches as
its children (`IWatchdogConfigService`, 5 ops: `ListConfigurations`/`ListConfiguredServices`/
`AddConfiguredService`/`UpdateConfiguredService`/`RemoveConfiguredService`) — deliberately two
separate interfaces, since "is X healthy right now" and "change what X's config says to launch"
are materially different, more/less sensitive capabilities. Both live in `UavOps.Agent.Contracts`
and mirror `ISimulatorInfraService`'s shape.

- **`WatchdogBackend`** (this project's own `appsettings.json`, default `Fake`) chooses between the
  real implementation (`WatchdogService`/`WatchdogConfigService`, this project) and an in-memory
  fake (`src/UavOps.Agent.Watchdog.Fake`, its own project, registered via `AddFakeWatchdog()` — the
  same three-project split-to-avoid-a-circular-reference reasoning as the simulator domain above).
- **Health is polled in the background, never live per tool call**: `WatchdogHealthPoller`
  (`BackgroundService`, registered only under `WatchdogBackend.Real`) polls
  `WatchdogOptions.HealthCheckUrl` (a standard ASP.NET Core HealthChecks JSON endpoint) every
  `PollIntervalSeconds` and writes the result into `IWatchdogHealthStore`; `GetServicesHealth` is
  then a plain in-memory read of that cached snapshot, reporting an explicit
  `{ overallStatus: "Unknown", stale: true }` shape if nothing has been polled yet rather than
  blocking or failing. A blank `HealthCheckUrl` (the out-of-the-box default even under `Real`)
  disables the poller with one log line instead of retrying against nothing forever.
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
per command and replying to the host's operation hub with the result (or the exception message, if
your handler throws). See `src/UavOps.FleetClient/README.md`.

`UavOps.MockFleetClient` (net47 console app) is the dev/test stand-in: it references the library
and implements `IUavCommandHandler` with `EmptyCommandHandler` — logs each command to the console
and returns a hardcoded, correctly-shaped placeholder. **Deliberately does not simulate fleet
state** (position, speed, mission progress) — that's already covered by
`SimulatedUavOperationService`, and this mock exists only to prove the SignalR plumbing itself
works end to end.

**Manual smoke test** (no automated live two-process test exists for this — same reasoning as
`UavOps.Agent.Evals` being opt-in/manual): set `OperationBackend: SignalR` in
`src/UavOps.Agent.McpMoav/appsettings.json`, run `UavOps.Agent`, then separately run
`src/UavOps.MockFleetClient/bin/Debug/net47/UavOps.MockFleetClient.exe
http://localhost:5262/uavCommandHub`. Open http://localhost:5262 and type a command (e.g. "set
UAV-1 speed to 200"); confirm the mock's console logs the received command and the chat response
reflects its placeholder `TelemetrySnapshot`. Stop the mock and retry the same command to confirm
it fails fast (near-instant `"No fleet command client is connected."`, not a 10s hang).

## Ports (local dev)

- `UavOps.Agent`: `http://localhost:5262` (chat UI, `/chatHub`, `/uavCommandHub`, `/healthz`,
  `/api/agent-graph`)
- Ollama: `http://localhost:11434`
