# Multi-turn fleet ops scenario (regression baseline)

Captured verbatim from a real, live chat session against `UavOps.Agent` on 2026-09-07 (flat
`BrainAgent`, `tool_choice: "auto"`, `ToolCallAwareChatReducer` fix already applied). Used as the
baseline the MCP domain-split (see `CLAUDE.md`, "Split BrainAgent's 3 domains into separate MCP
servers") must not regress — every real tool call below has a matching, verifiable trace entry;
the split changes *where* each tool executes, never *whether* it executes for real. Mirrored as a
repeated (8x), real-ground-truth-asserting test:
`tests/UavOps.Agent.Tests/Agents/LiveAgentResponseTests.cs`'s
`MultiTurnFleetOpsScenario_MatchesRealFleetState`.

## Transcript

**Operator:** hi, my name is David and i am today Operator

**BrainAgent** (4.31s): Hello David! I'm BrainAgent, your UAV fleet operations assistant. How can
I help you today?

---

**Operator:** What UAVs do we have?

**BrainAgent** (3.33s): We have three UAVs in our fleet: 997, 998, and 999. All of them are
currently in transiting mode and located at coordinates 31.801447, 34.643497.

Reasoning (1 step):
```
[BrainAgent] ListFleet({}) → [{"tailNumber":"997","mode":"Transiting","lat":31.801447,"lng":34.643497},{"tailNumber":"998","mode":"Transiting","lat":31.801447,"lng":34.643497},{"tailNumber":"999","mode":"Transiting","lat":31.801447,"lng":34.643497}] · 0.00s
```

---

**Operator:** fly them to target alpha at speed 250 and altitude 3000

**BrainAgent** (4.64s): All three UAVs (997, 998, and 999) have been commanded to fly to
alpha at a speed of 250 knots and an altitude of 3000 feet. They are currently transiting to their
destination.

Reasoning (9 steps):
```
[BrainAgent] Navigate({"tailNumber":"997","location":"alpha"}) → {...,"mode":"Transiting",...} · 0.00s
[BrainAgent] Navigate({"tailNumber":"998","location":"alpha"}) → {...,"mode":"Transiting",...} · 0.00s
[BrainAgent] Navigate({"tailNumber":"999","location":"alpha"}) → {...,"mode":"Transiting",...} · 0.00s
[BrainAgent] SetSpeed({"tailNumber":"997","speedKts":250}) → {...,"speedKts":250,...} · 0.00s
[BrainAgent] SetSpeed({"tailNumber":"998","speedKts":250}) → {...,"speedKts":250,...} · 0.00s
[BrainAgent] SetSpeed({"tailNumber":"999","speedKts":250}) → {...,"speedKts":250,...} · 0.00s
[BrainAgent] SetAltitude({"tailNumber":"997","altitudeFt":3000}) → {...,"altitudeFt":3000,...} · 0.00s
[BrainAgent] SetAltitude({"tailNumber":"998","altitudeFt":3000}) → {...,"altitudeFt":3000,...} · 0.00s
[BrainAgent] SetAltitude({"tailNumber":"999","altitudeFt":3000}) → {...,"altitudeFt":3000,...} · 0.00s
```

---

**Operator:** also point theirs payloads there

**BrainAgent** (3.32s): The payloads on all three UAVs (997, 998, and 999) are now pointed
at alpha.

Reasoning (3 steps):
```
[BrainAgent] PointPayload({"tailNumber":"997","location":"alpha"}) → {...,"payloadLockedOn":"alpha"} · 0.00s
[BrainAgent] PointPayload({"tailNumber":"998","location":"alpha"}) → {...,"payloadLockedOn":"alpha"} · 0.00s
[BrainAgent] PointPayload({"tailNumber":"999","location":"alpha"}) → {...,"payloadLockedOn":"alpha"} · 0.00s
```

---

**Operator:** bring them all home

**BrainAgent** (3.30s): All three UAVs (997, 998, and 999) are now returning to their launch
point. They are currently in "ReturningToLaunch" mode.

Reasoning (3 steps):
```
[BrainAgent] ReturnToLaunch({"tailNumber":"997"}) → {...,"mode":"ReturningToLaunch",...} · 0.00s
[BrainAgent] ReturnToLaunch({"tailNumber":"998"}) → {...,"mode":"ReturningToLaunch",...} · 0.00s
[BrainAgent] ReturnToLaunch({"tailNumber":"999"}) → {...,"mode":"ReturningToLaunch",...} · 0.00s
```

---

**Operator:** give please summary of today session

**BrainAgent** (4.06s): Here is a summary of today's session:

1. **Introduction**: You introduced yourself as David, the operator.
2. **Fleet Overview**: We confirmed that we have three UAVs in our fleet: 997, 998, and 999.
3. **Flight Commands**: All UAVs were flown to alpha at a speed of 250 knots and an altitude of
   3000 feet. The payloads on all UAVs were pointed at alpha.
4. **Return to Launch**: All UAVs were commanded to return to their launch point, and they are
   currently in "ReturningToLaunch" mode.

If you have any further commands or need additional assistance, feel free to let me know!

(No tool call for this turn — a pure recall question answered from real conversation history,
correctly matching what actually happened above.)

## Ground truth to assert (all 3 UAVs, after the "bring them all home" turn)

| Field | Expected |
|---|---|
| `speedKts` | 250 |
| `altitudeFt` | 3000 |
| `mode` | `ReturningToLaunch` |
| `payloadLockedOn` | `alpha` |
