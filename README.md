# UavOps.Agent

A small local-LLM agent layer for natural-language UAV command and control. It sits in front of
an existing UAV control application (its capabilities are discovered from that app's OpenAPI
spec) and exposes a single chat window. Four agents — `MainAgent`, `FlightControlAgent`,
`PayloadControlAgent`, `MissionAgent` — turn operator text into calls against that app's REST
API, using [Ollama](https://ollama.com) and a small local model (`granite4.1:3b` by default).

See `docs/plan.md` (or the plan this project was built from) for the full design rationale.

## Prerequisites

- .NET 8 SDK
- [Ollama](https://ollama.com) running locally with a model pulled, e.g. `ollama pull granite4.1:3b`
- No Docker required — everything here is `dotnet run`.

## Running it

This repo ships a simulated UAV control API (`UavOps.ControlApi`) that stands in for the real UAV
control application until one is available. Run both projects, each in its own terminal:

```bash
# Terminal 1 — the (simulated) UAV control application
dotnet run --project src/UavOps.ControlApi --urls http://localhost:5250

# Terminal 2 — the agent service + chat UI
dotnet run --project src/UavOps.Agent --urls http://localhost:5262
```

`UavOps.ControlApi` exposes its OpenAPI spec (Swashbuckle-generated) at
`/openapi/v1.json` and a browsable Swagger UI at `/swagger`.

Then open http://localhost:5262 and start typing commands, e.g.:

- `Set speed to 120 knots`
- `Climb to 5000 feet and point the camera at target alpha`
- `What's the current mission status?`

`UavOps.Agent` fetches and validates the UAV API's OpenAPI spec at startup — if the spec is
unreachable, or an agent's configured tool references a parameter that isn't described, the app
refuses to start and logs exactly what's wrong. Check `GET /healthz` for a quick sanity check
(model in use, UAV API base URL, number of operations discovered).

## Configuring agents

Everything about what an agent can do and how it's described to the model lives in
`src/UavOps.Agent/appsettings.json` — no code changes needed to retarget this at a different UAV
application or to change what an agent is told about a tool:

- `UavApi` — base URL and OpenAPI spec URL of the UAV control application (mock or real).
- `Ollama` — endpoint and default model.
- `ExecutionMode` — `"Direct"` (tools execute immediately) or `"Confirm"` (mutating tool calls —
  anything that isn't a GET — show an approve/decline card in the chat UI first, with a 60s
  timeout). This is hot-reloaded: editing appsettings.json takes effect on the next tool call, no
  restart needed.
- `Agents.<Name>.Instructions` — the agent's system prompt.
- `Agents.<Name>.Tools[]` — each entry names an OpenAPI `operationId` plus a hand-written
  `Description` and per-parameter `Parameters` descriptions. These are the *only* things the
  model sees for that tool — the upstream OpenAPI spec supplies only the mechanical HTTP
  contract (verb, path, parameter types), never the text shown to the model. Use
  `FixedParameters` for values that should always be sent but never exposed as something the
  model can choose (e.g. an internal header).
- `Agents.MainAgent.Delegates` — the list of domain agents MainAgent can hand a request to.

## Repo layout

```
src/UavOps.Agent/       the production service: chat hub, agent orchestration, OpenAPI-driven
                         tool catalog, confirmation gate, structured tool-call logging, static SPA
src/UavOps.ControlApi/  simulated dev/demo stand-in for the real UAV control application
tests/                  unit/integration tests plus the live-pipeline golden-command evals
```

## Logging

Every tool call — including MainAgent delegating to a domain agent — produces one structured log
line (tool name, arguments, result, duration, correlation ID) and one `ReceiveAgentTrace`
SignalR event so the same information shows up live in the chat UI's reasoning panel.
