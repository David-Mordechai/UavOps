#!/usr/bin/env python3
"""
Minimal single-agent baseline test, deliberately independent of UavOps.Agent's own
multi-agent architecture (no BrainAgent/MoavAgent/CreatePlan/leaf-agent split, no
delegation, no pronoun hand-off between agents, no shared TailNumberResolutionScope).

One agent. All tools. One flat system prompt. A tiny in-memory fake fleet (3 UAVs).
Talks directly to a real OpenAI-compatible chat completions endpoint over plain HTTP
(no .NET, no Microsoft.Extensions.AI, no OpenAI SDK - just urllib + json, so there is
zero possibility of any app-level bug touching this run).

Built 2026-09-06 to answer: are UavOps.Agent's live-observed reliability problems
(fabricated fleet-wide success claims, forced tool-choice refusals) caused by the
model/backend, or by the app's own multi-agent architecture? Against
Qwen/Qwen3.8-27B-FP8 (the model in production at the time), this script's answer was
8/8 fully correct runs - see opentask.txt's "Decisive experiment" section for the full
writeup. That result does NOT necessarily hold for other models - re-run this against
each candidate model before drawing conclusions about any of them.

Runs a real agentic tool-calling loop (send -> execute any tool calls -> send results
back -> repeat until a final plain-text answer), exactly like a normal single-agent
function-calling assistant would be built. tool_choice is "auto" (not forced) since
answering a greeting with no tool call is correct behavior for a normal assistant -
forcing was a UavOps.Agent-specific structural choice, not what's being tested here.

Usage:
  python3 single_agent_test.py [N] [--model MODEL] [--host HOST] [--temperature T]
                                [--no-thinking-kwarg] [--no-parallel-tool-calls]

  N                       Number of independent repeats (default 1). Fleet state resets
                           between runs.
  --model MODEL           Model name as the backend expects it in the "model" field.
  --host HOST             Base URL, e.g. http://192.168.1.155:8000 (vLLM) or
                           http://localhost:11434 (Ollama's OpenAI-compatible endpoint).
  --temperature T         Sampling temperature to request (default 0). Some backends
                           override this server-side regardless (e.g. vLLM
                           --override-generation-config) - check your launch config.
  --no-thinking-kwarg     Omit chat_template_kwargs={"enable_thinking": false} - not
                           every model/template supports this field; some may error if
                           it's present at all.
  --no-parallel-tool-calls
                           Omit parallel_tool_calls from the request - not every
                           OpenAI-compatible backend accepts this field.

Examples:
  # The model this script validated 8/8 against:
  python3 single_agent_test.py 8 --model Qwen/Qwen3.8-27B-FP8 --host http://192.168.1.155:8000

  # A previous candidate model on the same vLLM host:
  python3 single_agent_test.py 8 --model nvidia/Qwen3.6-35B-A3B-NVFP4 --host http://192.168.1.155:8000

  # The original default model, via Ollama:
  python3 single_agent_test.py 8 --model granite4.1:3b --host http://localhost:11434 --no-thinking-kwarg --no-parallel-tool-calls
"""
import argparse
import json
import sys
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")

# ---------------------------------------------------------------------------
# Fake fleet state - the ONLY source of truth this script trusts. Printed in
# full at the end of each run so the model's claims can be checked against
# real state, not just read back as text.
# ---------------------------------------------------------------------------
FLEET = {
    "UAV-1": {"lat": 31.801447, "lng": 34.643497, "speedKts": 105, "altitudeFt": 4000, "mode": "Orbiting", "payloadLockedOn": None},
    "UAV-2": {"lat": 31.798000, "lng": 34.639000, "speedKts": 105, "altitudeFt": 4000, "mode": "Orbiting", "payloadLockedOn": None},
    "UAV-3": {"lat": 31.805000, "lng": 34.648000, "speedKts": 105, "altitudeFt": 4000, "mode": "Orbiting", "payloadLockedOn": None},
}

KNOWN_POINTS = {"alpha": (31.81, 34.66), "bravo": (31.79, 34.62), "home": (31.80, 34.64)}


def _resolve_point(name):
    key = name.lower().replace("target ", "").strip()
    return KNOWN_POINTS.get(key)


def tool_list_fleet():
    return [{"tailNumber": t, "mode": s["mode"], "lat": s["lat"], "lng": s["lng"]} for t, s in FLEET.items()]


def tool_get_telemetry(tailNumber):
    if tailNumber not in FLEET:
        return {"error": f"Unknown UAV '{tailNumber}'."}
    return dict(FLEET[tailNumber])


def tool_navigate(tailNumber, location):
    if tailNumber not in FLEET:
        return {"error": f"Unknown UAV '{tailNumber}'."}
    point = _resolve_point(location)
    if point is None:
        return {"error": f"Unknown location '{location}'."}
    FLEET[tailNumber]["lat"], FLEET[tailNumber]["lng"] = point
    FLEET[tailNumber]["mode"] = "Transiting"
    return dict(FLEET[tailNumber])


def tool_set_speed(tailNumber, speedKts):
    if tailNumber not in FLEET:
        return {"error": f"Unknown UAV '{tailNumber}'."}
    FLEET[tailNumber]["speedKts"] = speedKts
    return dict(FLEET[tailNumber])


def tool_set_altitude(tailNumber, altitudeFt):
    if tailNumber not in FLEET:
        return {"error": f"Unknown UAV '{tailNumber}'."}
    FLEET[tailNumber]["altitudeFt"] = altitudeFt
    return dict(FLEET[tailNumber])


def tool_point_payload(tailNumber, location):
    if tailNumber not in FLEET:
        return {"error": f"Unknown UAV '{tailNumber}'."}
    if _resolve_point(location) is None:
        return {"error": f"Unknown location '{location}'."}
    FLEET[tailNumber]["payloadLockedOn"] = location
    return dict(FLEET[tailNumber])


TOOL_IMPLS = {
    "ListFleet": lambda args: tool_list_fleet(),
    "GetTelemetry": lambda args: tool_get_telemetry(args["tailNumber"]),
    "Navigate": lambda args: tool_navigate(args["tailNumber"], args["location"]),
    "SetSpeed": lambda args: tool_set_speed(args["tailNumber"], args["speedKts"]),
    "SetAltitude": lambda args: tool_set_altitude(args["tailNumber"], args["altitudeFt"]),
    "PointPayload": lambda args: tool_point_payload(args["tailNumber"], args["location"]),
}

TOOLS = [
    {"type": "function", "function": {
        "name": "ListFleet",
        "description": "List all known UAVs by tail number with a brief status summary for each.",
        "parameters": {"type": "object", "properties": {}}
    }},
    {"type": "function", "function": {
        "name": "GetTelemetry",
        "description": "Get a UAV's current position, speed, altitude, and mode.",
        "parameters": {"type": "object", "properties": {
            "tailNumber": {"type": "string", "description": "The tail number of the UAV to query, e.g. 'UAV-1'. Must be one of the known UAVs."}
        }, "required": ["tailNumber"]}
    }},
    {"type": "function", "function": {
        "name": "Navigate",
        "description": "Send a UAV to a named location.",
        "parameters": {"type": "object", "properties": {
            "tailNumber": {"type": "string", "description": "The tail number of the UAV to command, e.g. 'UAV-1'. Must be one of the known UAVs."},
            "location": {"type": "string", "description": "Name of a known point, e.g. 'home', 'alpha', 'bravo'."}
        }, "required": ["tailNumber", "location"]}
    }},
    {"type": "function", "function": {
        "name": "SetSpeed",
        "description": "Change a UAV's target cruise speed.",
        "parameters": {"type": "object", "properties": {
            "tailNumber": {"type": "string", "description": "The tail number of the UAV to command, e.g. 'UAV-1'. Must be one of the known UAVs."},
            "speedKts": {"type": "integer", "description": "Target speed in knots."}
        }, "required": ["tailNumber", "speedKts"]}
    }},
    {"type": "function", "function": {
        "name": "SetAltitude",
        "description": "Change a UAV's target altitude.",
        "parameters": {"type": "object", "properties": {
            "tailNumber": {"type": "string", "description": "The tail number of the UAV to command, e.g. 'UAV-1'. Must be one of the known UAVs."},
            "altitudeFt": {"type": "integer", "description": "Target altitude in feet."}
        }, "required": ["tailNumber", "altitudeFt"]}
    }},
    {"type": "function", "function": {
        "name": "PointPayload",
        "description": "Point a UAV's sensor/gimbal at a named location.",
        "parameters": {"type": "object", "properties": {
            "tailNumber": {"type": "string", "description": "The tail number of the UAV to command, e.g. 'UAV-1'. Must be one of the known UAVs."},
            "location": {"type": "string", "description": "Name of a known point to look at, e.g. 'home', 'alpha', 'bravo'."}
        }, "required": ["tailNumber", "location"]}
    }},
]

SYSTEM_PROMPT = """You are a single UAV fleet operations assistant. You handle everything directly - \
introducing yourself, answering questions about the fleet, and commanding UAVs - using the tools \
provided. There is no other agent or system; you must call the real tools yourself to get real \
information or take real action. Never claim a fact about the fleet or an action was taken unless \
you actually called the matching tool this turn and are reporting its real result.

Tail numbers:
- Never guess a tail number. If you don't already know the fleet's real tail numbers this turn, call ListFleet first.
- When the operator refers to the whole fleet collectively ('all of them', 'every UAV', 'all three', etc.), call the relevant tool once per real UAV tail number (from ListFleet) - never invent a placeholder like 'ALL', and never skip any UAV.

Multi-part requests:
- Count how many distinct actions the operator asked for, across however many UAVs are involved, and call every matching tool for every UAV - e.g. 'fly all three to alpha, set speed 250 and altitude 3000, and point their payloads there' with 3 known UAVs is 12 tool calls (Navigate + SetSpeed + SetAltitude + PointPayload, once each, for each of the 3 UAVs).
- You may call multiple tools in the same turn.

Summaries:
- After acting, write a short, accurate summary based only on the real tool results you just received - name every UAV you actually acted on, and never claim one was included if you did not call a tool for it.
"""


def call_model(messages, args):
    payload = {
        "model": args.model,
        "messages": messages,
        "tools": TOOLS,
        "tool_choice": "auto",
        "temperature": args.temperature,
        "max_tokens": 4096,
    }
    if not args.no_parallel_tool_calls:
        payload["parallel_tool_calls"] = True
    if not args.no_thinking_kwarg:
        payload["chat_template_kwargs"] = {"enable_thinking": False}

    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(f"{args.host}/v1/chat/completions", data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as resp:
        return json.loads(resp.read())


def run_turn(messages, user_text, args, max_tool_rounds=6):
    messages.append({"role": "user", "content": user_text})
    print(f"\n{'='*70}\nOperator: {user_text}\n{'='*70}")

    for round_num in range(max_tool_rounds):
        body = call_model(messages, args)
        choice = body["choices"][0]
        msg = choice["message"]
        tool_calls = msg.get("tool_calls") or []

        if not tool_calls:
            content = msg.get("content") or ""
            print(f"\nAssistant (final): {content}")
            messages.append({"role": "assistant", "content": content})
            return

        assistant_msg = {"role": "assistant", "content": msg.get("content"), "tool_calls": tool_calls}
        messages.append(assistant_msg)

        print(f"\n-- round {round_num}: {len(tool_calls)} tool call(s) --")
        for call in tool_calls:
            name = call["function"]["name"]
            try:
                call_args = json.loads(call["function"]["arguments"])
            except json.JSONDecodeError as e:
                print(f"  MALFORMED ARGS for {name}: {call['function']['arguments']!r} ({e})")
                messages.append({"role": "tool", "tool_call_id": call["id"], "content": json.dumps({"error": "malformed arguments"})})
                continue

            impl = TOOL_IMPLS.get(name)
            result = {"error": f"Unknown tool '{name}'."} if impl is None else impl(call_args)

            print(f"  {name}({call_args}) -> {result}")
            messages.append({"role": "tool", "tool_call_id": call["id"], "content": json.dumps(result)})

    print("!! Exceeded max tool rounds without a final answer.")


def reset_fleet():
    for state in FLEET.values():
        state["speedKts"] = 105
        state["altitudeFt"] = 4000
        state["mode"] = "Orbiting"
        state["payloadLockedOn"] = None


def run_once(args):
    reset_fleet()
    messages = [{"role": "system", "content": SYSTEM_PROMPT}]

    run_turn(messages, "hi my name is David and I am today Operator", args)
    run_turn(messages, "What uavs do we have?", args)
    run_turn(messages, "fly all of them to target alpha and set speed to 250 and altitude to 3000 to all of them also point there payloads there", args)

    all_correct = all(
        s["speedKts"] == 250 and s["altitudeFt"] == 3000 and s["mode"] == "Transiting" and s["payloadLockedOn"] == "alpha"
        for s in FLEET.values()
    )
    return all_correct, {t: dict(s) for t, s in FLEET.items()}


def parse_args():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("n", nargs="?", type=int, default=1, help="Number of independent repeats (default 1).")
    p.add_argument("--model", default="nvidia/Qwen3.6-35B-A3B-NVFP4", help="Model name as sent in the 'model' field.")
    p.add_argument("--host", default="http://10.10.77.57:8000", help="Backend base URL (no trailing /v1).")
    p.add_argument("--temperature", type=float, default=0, help="Sampling temperature to request.")
    p.add_argument("--no-thinking-kwarg", action="store_true", help="Omit chat_template_kwargs={'enable_thinking': false}.")
    p.add_argument("--no-parallel-tool-calls", action="store_true", help="Omit parallel_tool_calls from the request.")
    return p.parse_args()


def main():
    args = parse_args()
    print(f"Model: {args.model}   Host: {args.host}   Temperature: {args.temperature}")

    results = []
    for i in range(args.n):
        print(f"\n\n########## RUN {i} ##########")
        ok, final_state = run_once(args)
        print(f"\n{'='*70}\nFINAL FAKE FLEET STATE (ground truth) - run {i}\n{'='*70}")
        for tail, state in final_state.items():
            print(f"  {tail}: {state}")
        print(f"\nRUN {i}: ALL 3 UAVs CORRECTLY UPDATED: {ok}")
        results.append(ok)

    print(f"\n\n{'='*70}\nSUMMARY ({args.model}): {sum(results)}/{args.n} runs fully correct\n{'='*70}")


if __name__ == "__main__":
    main()
