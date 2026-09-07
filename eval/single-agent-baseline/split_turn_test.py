#!/usr/bin/env python3
"""
Split-turn variant of single_agent_test.py, built 2026-09-06 to answer a direct question: does
THIS SAME flat, single-agent, no-retrieval, no-delegation, tool_choice="auto" design (all 6 tools,
one system prompt, pure HTTP, zero UavOps.Agent code involved) also fabricate when the exact
compound request that scored 8/8 in single_agent_test.py is instead split across two SEPARATE
turns - matching the shape of two real live incidents against the real app (2026-09-06):

  1. "fly them all to target alpha at speed 250 and altitude 3000" (one turn, no payload mention)
     ... then, as a SEPARATE later turn ...
     "point their payloads there"
     -> live incident: zero tool calls logged, confident fabricated "done" claim.

  2. "What uavs do we have?" asked once early, then again after the fleet already moved.
     -> live incident: second answer repeated the first answer's stale pre-move data verbatim.

single_agent_test.py's own scenario bundles fly+speed+altitude+point-payload into ONE message and
never re-asks a status question after a state change - it has never actually tested either of
these two shapes. If this script ALSO fails at meaningful rates under the split-turn shape, that
is strong evidence the failure is a property of small-model behavior on split/follow-up requests
in general - present even with zero UavOps.Agent-specific code (no BrainAgent, no CreatePlan, no
retrieval, no persistent multi-hour session) - not something introduced by this app's own
architecture. If it instead scores as well as the bundled scenario, that would point back at
something UavOps.Agent-specific (retrieval narrowing, the persistent long-lived session
accumulating far more history than these short runs ever do, etc.) as the real differentiator.

Usage: same as single_agent_test.py - python3 split_turn_test.py [N] [--model MODEL] [--host HOST] ...
"""
import sys

from single_agent_test import (
    FLEET, reset_fleet, run_turn, parse_args,
)


def run_once(args):
    reset_fleet()
    messages = [{"role": "system", "content": __import__("single_agent_test").SYSTEM_PROMPT}]

    run_turn(messages, "hi my name is David and I am today Operator", args)
    run_turn(messages, "What uavs do we have?", args)
    run_turn(messages, "fly them all to target alpha at speed 250 and altitude 3000", args)
    run_turn(messages, "point their payloads there", args)
    # Second read-query, well after the fleet state changed - checks the staleness incident too.
    run_turn(messages, "What uavs do we have?", args)

    all_correct = all(
        s["speedKts"] == 250 and s["altitudeFt"] == 3000 and s["mode"] == "Transiting" and s["payloadLockedOn"] == "alpha"
        for s in FLEET.values()
    )
    return all_correct, {t: dict(s) for t, s in FLEET.items()}


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
        print(f"\nRUN {i}: ALL 3 UAVs CORRECTLY UPDATED (incl. payload via separate follow-up turn): {ok}")
        results.append(ok)

    print(f"\n\n{'='*70}\nSUMMARY ({args.model}): {sum(results)}/{args.n} runs fully correct\n{'='*70}")


if __name__ == "__main__":
    main()
