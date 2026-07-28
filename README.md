# UavOps.Agent

A small local-LLM agent layer for natural-language UAV command and control. A single chat window
turns operator text into operations, using [Ollama](https://ollama.com) and a small local
model (`granite4.1:3b` by default). Four agents — `MainAgent`, `FlightControlAgent`,
`PayloadControlAgent`, `MissionAgent`, `GdtControlAgent` — turn operator text into calls against
`IOperationService`, answered either by an in-memory simulation or relayed over SignalR to a
real (or mock) fleet-commanding application. The core plumbing is domain-agnostic — `UavOps.Agent`
is a general agentic tool-calling framework with a UAV domain currently plugged into it.

## Prerequisites

- .NET 8 SDK
- [Ollama](https://ollama.com) running locally with the required chat model pulled:
  - Chat model: `ollama pull granite4.1:3b`
- For semantic agent-retrieval embeddings:
  - **Ollama (Default)**: Pull the embedding model: `ollama pull nomic-embed-text`
  - **In-Memory (Offline)**: Set `"EmbeddingModel": "InMemory"` in `appsettings.json` to compute embeddings 100% in-process deterministically, requiring no external embedding model pull.
- No Docker required — everything here is `dotnet run`.

## Running it

```bash
dotnet run --project src/UavOps.Agent --urls http://localhost:5262
```

That's it — by default (`OperationBackend: Simulated`) `UavOps.Agent` answers operations with an
in-memory simulation, no other process needed. Setting `OperationBackend` to `SignalR` instead
routes every operation over a SignalR hub (`/uavCommandHub`) to a connected fleet-commanding
application — see `src/UavOps.FleetClient` (the client library a real .NET Framework app
references) and `src/UavOps.MockFleetClient` (a dev/test stand-in for it):

```bash
OperationBackend=SignalR dotnet run --project src/UavOps.Agent --urls http://localhost:5262
# separately:
src/UavOps.MockFleetClient/bin/Debug/net47/UavOps.MockFleetClient.exe http://localhost:5262/uavCommandHub
```

Then open http://localhost:5262 and start typing commands, e.g.:

- `Set speed to 120 knots`
- `Climb to 5000 feet and point the camera at target alpha`
- `What's the current mission status?`

`UavOps.Agent` validates every agent's config against `IOperationService` at startup (pure
reflection, no network call) — if a tool references an operation or parameter that doesn't exist,
the app refuses to start and logs exactly what's wrong. Check `GET /healthz` for a quick sanity
check (model in use, operation backend, number of operations discovered).

## Configuring agents

Everything about what an agent can do and how it's described to the model lives in
`src/UavOps.Agent/AgentsConfig/` — one YAML file per agent (the filename is the agent's name) —
no code changes needed to change what an agent is told about a tool:

- `instructions` — the agent's system prompt.
- `description` — shown to MainAgent as this agent's tool description when it's one of
  MainAgent's delegates.
- `temperature` — sampling temperature.
- `delegates` — the list of domain agents MainAgent can hand a request to (MainAgent only).
- `tools[]` — each entry names an `operation` (must match an `IOperationService` method exactly)
  plus a hand-written `description` and per-parameter `parameters` descriptions. These are the
  *only* things the model sees for that tool — the operation's own C# signature supplies only the
  mechanical parameter shape (names/types), never the text shown to the model. Use
  `fixedParameters` for values that should always be sent but never exposed as something the
  model can choose (e.g. an internal header). Set `requiresConfirmation: true` to require operator
  approval before that specific tool runs (default `false`) — this is per-tool, so you choose
  exactly which actions need a yes/no and which don't. Approval happens right in the chat, not as
  a button: the agent asks a plain-text yes/no question and the operator replies in the same
  message box ("yes"/"no" and a few common variants are recognized — see `ChatConfirmationParser`).

## Repo layout

```
src/UavOps.Agent/            the one server-side process: chat hub + operation hub, agent
                              orchestration, reflection-based tool catalog, confirmation gate,
                              structured tool-call logging, static SPA, YAML agent config
  Operations/                  IOperationService — the single source of truth for what an
                                "operation" is — plus the SignalR bridge (Operations/Remote/)
  Simulation/                  in-memory UAV/fleet simulation (default backend)
  AgentsConfig/                one YAML file per agent
src/UavOps.FleetClient/      (.NET Framework 4.7) client library the real fleet-commanding app
                              references to connect to UavOps.Agent's SignalR operation hub
src/UavOps.MockFleetClient/  (.NET Framework 4.7) dev/test stand-in for the real app — references
                              UavOps.FleetClient with stub (non-simulating) command handlers
tests/                       unit tests plus the live-pipeline golden-command evals
```

## Logging

Every tool call — including MainAgent delegating to a domain agent — produces one structured log
line (tool name, arguments, result, duration, correlation ID) and one `ReceiveAgentTrace`
SignalR event so the same information shows up live in the chat UI's reasoning panel.
