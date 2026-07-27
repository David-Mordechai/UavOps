# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A local-LLM agent layer for natural-language UAV command and control (.NET 8). A chat UI turns
operator text (e.g. "fly UAV-1 to target alpha and set speed to 200") into REST calls against a
UAV control API, via [Ollama](https://ollama.com) and a small local model
(`granite4.1:3b` by default). Everything about which tools an agent exposes and how they're
described to the model is config-driven (`appsettings.json`), not code.

Two independently runnable services:

- **`src/UavOps.Agent`** — the production service: SignalR chat hub, multi-agent orchestration
  (agent-as-tool delegation), an OpenAPI-driven tool catalog, a confirmation gate for mutating
  actions, structured tool-call logging, and a static SPA (`wwwroot/`).
- **`src/UavOps.ControlApi`** — a simulated stand-in for the real UAV control application (in
  memory, no persistence). Exposes an OpenAPI spec that `UavOps.Agent` discovers its tools from.
  Will eventually be replaced/pointed at a real UAV control app; keep the `Simulated*` service
  names as-is so the placeholder is never mistaken for the real thing.

## Running it

Requires the .NET 8 SDK and Ollama running locally with a model pulled (`ollama pull granite4.1:3b`).
No Docker.

```bash
# Terminal 1 — the control API (mock UAV fleet)
dotnet run --project src/UavOps.ControlApi --urls http://localhost:5250

# Terminal 2 — the agent service + chat UI
dotnet run --project src/UavOps.Agent --urls http://localhost:5262
```

Then open http://localhost:5262. `UavOps.Agent` fetches and validates the control API's OpenAPI
spec **at startup** — if the spec is unreachable, or an agent's configured tool references an
operation/parameter that doesn't exist, the app refuses to start and logs exactly what's wrong
(see `AgentConfigValidator`). `GET /healthz` on the agent gives a quick sanity check (model in
use, control API base URL, number of OpenAPI operations discovered).

## Build & test

```bash
dotnet build                                          # whole solution
dotnet test tests/UavOps.Agent.Tests                   # agent unit tests (fast, no live deps)
dotnet test tests/UavOps.ControlApi.Tests               # control API integration tests (in-memory WebApplicationFactory)
dotnet test --filter "FullyQualifiedName~ConfirmationGateTests"   # single test class
dotnet test --filter "FullyQualifiedName~SomeMethodName"          # single test method
```

`tests/UavOps.Agent.Evals` is a **separate, opt-in** suite — it drives the real live chat pipeline
end to end (SignalR) against a golden set of operator utterances in
`eval/golden-commands/*.yaml`. It requires Ollama, `UavOps.ControlApi`, and `UavOps.Agent` all
actually running (`LiveDependenciesFixture` checks reachability and fails loudly, not silently,
if something isn't up) and is not part of the normal `dotnet test` inner loop — run it explicitly
with all three dependencies started:

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
  (`Agents.MainAgent.Delegates`) with which domain agents it can hand requests to
  (`FlightControlAgent`, `PayloadControlAgent`, `MissionAgent`, `GdtControlAgent`). Each delegate
  call is logged exactly like a real API tool call.
- **Domain agent tools are OpenAPI-backed**: `UavApiOperationTool` is an `AIFunction` for one
  OpenAPI `operationId` on the control API. Critically, **the LLM never sees the raw OpenAPI
  spec** — only the hand-written `Description` and per-parameter descriptions from
  `appsettings.json`. The OpenAPI spec supplies only the mechanical HTTP contract (verb, path,
  parameter types). `AgentConfigValidator` cross-checks the two at startup: every configured tool
  must resolve to a real operation, and every required OpenAPI parameter must have either a
  `Parameters` description or a `FixedParameters` value.
- **Concurrent tool calls**: `FunctionInvokingChatClient(ollama) { AllowConcurrentInvocation =
  true }` plus `ChatOptions.AllowMultipleToolCalls = true` lets one model turn batch several tool
  calls (e.g. `SetSpeed` + `SetAltitude`, or MainAgent delegating to two domain agents at once)
  and run them concurrently instead of one at a time.
- **Confirmation gate** (`ConfirmationGate`): opt-in and per-tool, not inferred from the HTTP
  verb — a tool only goes through confirmation when its config sets
  `"RequiresConfirmation": true` (see below) *and* `ExecutionMode` is `"Confirm"` (`"Direct"` is a
  global override that skips confirmation entirely, e.g. for local dev). `ExecutionMode` is read
  live from `IConfiguration` on every call, so editing `appsettings.json` takes effect on the next
  tool call with no restart; it defaults to `Confirm` if the value is missing/invalid (fail-safe).
  The approval round-trip happens **in chat, not via UI buttons**: the prompt and its resolution
  are sent as ordinary `ReceiveChatMessage` events (under a correlationId of their own, so they
  render as their own bubble instead of overwriting the turn that triggered them — the turn's own
  bubble is repositioned to the end of the thread once its final answer lands, in `chat.js`, since
  it may have been created earlier from the first trace event and would otherwise sit above a
  confirmation exchange that happened later but before that final answer). The prompt text is
  built from the tool's human-authored `Description` and "name: value" arguments — never the raw
  operationId or JSON — since an operator shouldn't need to know function names to approve or
  decline an action. The operator's next plain-text reply is parsed by `ChatConfirmationParser` (a
  small fixed yes/no vocabulary — deliberately not an LLM classification, since approving a UAV
  command is safety-relevant and needs a deterministic, auditable interpretation).
  `ChatHub.SendMessage` offers every incoming message to `ConfirmationGate.TryHandleChatReplyAsync`
  before treating it as a new command, so a reply like "yes" is consumed as the answer to the
  pending confirmation
  rather than spawning a new agent turn. Only one confirmation can be outstanding at a time (a
  `SemaphoreSlim` turnstile in `ConfirmationGate`) — with concurrent tool invocation enabled, two
  mutating calls could otherwise both need approval at once, which would make a bare "yes" reply
  ambiguous about which one it answers.
- **Logging**: every tool call — including MainAgent→domain-agent delegation — goes through
  `ToolInvocationLogger`, producing one structured log line (tool, args, result, duration,
  correlation ID) and one `ReceiveAgentTrace` SignalR event, so the chat UI's reasoning panel and
  the eval suite's trace assertions see identical data.
- **SignalR concurrency note**: `MaximumParallelInvocationsPerClient = 10` is set deliberately —
  the operator's chat reply to a pending confirmation (itself just another `SendMessage` call)
  must reach the hub on the same connection while that connection's *original* `SendMessage` call
  is still in flight, blocked awaiting that confirmation; SignalR's default limit of 1 would
  otherwise queue the reply behind the in-progress turn until it times out.

### Configuring agents (`src/UavOps.Agent/appsettings.json`)

No code changes needed to retarget this at a different UAV application or change what an agent
is told about a tool:

- `UavApi` — base URL and OpenAPI spec URL of the control API (mock or real).
- `Ollama` — endpoint and default model.
- `ExecutionMode` — `"Direct"` or `"Confirm"` (see above).
- `Agents.<Name>.Instructions` — the agent's system prompt.
- `Agents.<Name>.Tools[]` — each entry names an `OperationId` plus a hand-written `Description`
  and per-parameter `Parameters` descriptions — the only things the model sees for that tool. Use
  `FixedParameters` for values always sent but never exposed to the model (e.g. an internal
  header). Set `"RequiresConfirmation": true` on a tool to require operator approval before it
  runs (default `false`); this is independent per tool, so e.g. `ReturnToLaunch` can require
  approval while `SetSpeed` doesn't, regardless of both being mutating calls.
- `Agents.MainAgent.Delegates` — which domain agents MainAgent can hand a request to.

When editing agent instructions, note the existing prompts are deliberately explicit about not
letting the model invent tail numbers, guess/convert units, or resolve pronouns across
agent-to-agent handoffs (each delegate only sees the instruction text MainAgent gives it, not the
full conversation) — preserve that style if you touch them.

## Architecture: UavOps.ControlApi

A plain ASP.NET Core controller API (`Controllers/`) backed by in-memory singleton services
(`IUavFleetService` → `SimulatedUavFleetService`, `IGdtService` → `SimulatedGdtService`). Each
controller action has an explicit `[EndpointName("...")]` — that name is the `operationId` in the
generated OpenAPI spec, which is the string `UavOps.Agent`'s `Agents.<Name>.Tools[].OperationId`
config must match exactly. Renaming an `[EndpointName]` breaks agent config until updated on both
sides.

OpenAPI/Swagger is generated by Swashbuckle (`AddSwaggerGen`/`UseSwagger`/`UseSwaggerUI` in
`Program.cs`), not the ASP.NET Core built-in minimal-API OpenAPI support (that requires .NET 9+).
`UseSwagger` is configured with a custom `RouteTemplate` so the spec stays at
`/openapi/v1.json` — the exact URL `UavOps.Agent`'s `UavApi:OpenApiUrl` config already points at.
Browsable Swagger UI is at `/swagger`.

`KnownPoints` resolves named locations (e.g. `"target alpha"`, `"home"`) to lat/lng for
navigation and payload-pointing endpoints.

## Ports (local dev)

- `UavOps.ControlApi`: `http://localhost:5250`
- `UavOps.Agent`: `http://localhost:5262`
- Ollama: `http://localhost:11434`
