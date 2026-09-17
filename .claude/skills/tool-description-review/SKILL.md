---
name: tool-description-review
description: >
  Use this skill whenever editing, adding, or reviewing model-facing tool descriptions or agent
  instructions in this repo - src/UavOps.Agent.Mcp*/ToolsConfig.yaml (tool/parameter descriptions,
  serverInstructions) and src/UavOps.Agent/Agents/BrainAgent.yaml. Also use it proactively after
  fixing a live-reproduced tool-selection or instruction-following bug by editing one of these
  files (the exact situation that motivated this skill: a one-line patch that fixes today's bug
  but leaves the description internally bloated or newly inconsistent with a sibling tool). Not
  for C# code review, and not for one-off typo fixes unrelated to a description's actual content
  or structure.
---

# Tool & Instruction Description Review

## Why this exists

This project's tool descriptions get patched reactively: a live test or a real operator session
finds a fabrication/tool-confusion bug, one sentence gets added to fix it, and the change ships.
Individually each patch is justified. Accumulated, they produce exactly two failure modes already
observed live in this codebase:

1. **Bloat** - a single parameter description (e.g. `tailNumber` across every Moav tool) grows
   into a multi-sentence paragraph doing several unrelated jobs (format, `'ALL'` handling, subset
   handling, a reliability caveat) until no single sentence gets the model's full attention.
2. **Cross-tool collision** - two different tools' descriptions independently reach for the same
   real-world word for two different things. `Navigate`'s own parameter description listed
   `'home'` as an example location, while `ReturnToLaunch` never mentioned "home" or "base" at
   all - so any operator phrasing using "home" pulled the model toward the wrong tool roughly 7
   times out of 8, live-verified. Neither description was "wrong" in isolation; the collision only
   exists across the two.

Both are invisible from inside a single description in isolation. This skill's whole point is
reading the tool surface as a set, not one description at a time.

## When invoked, do this in order

### 1. Read the whole tool surface together, not just the file being edited

Read all of these in one pass before judging any single description:
- `src/UavOps.Agent.McpMoav/ToolsConfig.yaml`
- `src/UavOps.Agent.McpWatchdog/ToolsConfig.yaml`
- `src/UavOps.Agent.McpSimulator/ToolsConfig.yaml`
- `src/UavOps.Agent/Agents/BrainAgent.yaml`

A collision between two tools in the *same* domain file is common (Navigate/ReturnToLaunch), but
don't assume it can't span domains - BrainAgent's own general instructions apply across all three.

### 2. Check every description against the 4-things checklist

For each `description:` and each `parameters:` entry, confirm it actually answers:
- **What** the tool does.
- **When** to use it (what real operator phrasing should trigger it).
- **When NOT to use it** - explicitly, if there's any plausible sibling tool or phrasing overlap.
  This is the one most often missing, and it's what caused the Navigate/ReturnToLaunch bug.
- **What each parameter means**, including format and edge cases - but see the bloat check below
  before adding more text here.

### 3. Cross-reference terminology across every tool read in step 1

Build a mental (or actually written, in your findings) index of the concrete nouns each
description uses for locations, states, and actions (e.g. "home", "base", "launch point",
"orbiting", "returning"). Flag any word that appears as a literal example or key term in more
than one tool's description, unless those two tools already explicitly cross-reference each other
("use X, not Y, when...", the way the fixed Navigate/ReturnToLaunch pair now does). An unflagged
collision here is the single highest-value thing this skill exists to catch.

### 4. Check for the bloat anti-pattern - but NEVER trim uniformly across tools

A description or parameter doc is doing too many jobs if it reads as a list of unrelated
clauses stitched together, especially separated by "also" / "additionally" / a second "if the
request...". When you find one, consider: does the model actually need every clause in the same
place, or can format/basic identity live in the parameter description while behavioral rules
(subset handling, `'ALL'` handling) live in `serverInstructions:` once for the whole domain
instead of repeated near-verbatim on every single tool?

**Before trimming the same repeated text across multiple tools, check each tool individually for
its own fragility history first** - live-reproduced the hard way: an identical `tailNumber`
parameter paragraph was repeated verbatim across 11 Moav tools, genuinely bloated, and trimming it
uniformly (pointing all 11 at `serverInstructions` instead) held at a real 8/8 on 10 of them but
dropped `ReturnToLaunch` specifically to 7/8 - the model started asking "which UAVs?" instead of
resolving "the rest of the fleet" itself, on the ONE tool this exact codebase already had a
documented real production incident about (see `ReturnRemainingFleetLiveTests`'s own doc comment).
Uniform-looking duplication can be protecting one specific fragile tool while being genuine, safe-
to-cut bloat on every other tool sharing that same text. The fix was never "trim it" or "keep it"
as a single decision for all 11 - it was: trim the 10 with no fragility history, keep the one with
real evidence (past incident + a fresh regression when tested) that it needs the reinforcement
right next to the parameter, not just once further away in `serverInstructions`. Don't just keep
appending when you find bloat, either - but don't apply one trim decision to every tool that
happens to share the same duplicated text before checking whether they're equally fragile.

**"Keep the reinforcement" does not mean "keep all of it" - find the minimal load-bearing
subset.** A first pass at this fix kept `ReturnToLaunch`'s *entire* original paragraph (the full
`'ALL'`/pronoun/unnamed-UAV logic, all restated verbatim) plus a "See serverInstructions above"
pointer sentence on every one of the other 10 tools - itself flagged, correctly, as still
needlessly duplicating information the model already gets once via `serverInstructions` on every
turn regardless of which tools retrieval offers. Re-tested with two further cuts: the 10 non-
fragile tools' `tailNumber` trimmed to bare format/identity only (no pointer sentence at all -
it added tokens without adding anything the model could act on, since `serverInstructions` is
always present either way), and `ReturnToLaunch` trimmed to *only* the one sentence about
resolving a named subset into a single comma-separated call - dropping the restated `'ALL'`/
pronoun/unnamed-UAV logic, which is fully covered in `serverInstructions` and was never the part
that broke. Re-verified 8/8 on both `ReturnRemainingFleetLiveTests` and
`FullShiftScenarioLiveTests` after this second, tighter cut. Lesson: when evidence says a tool
needs inline reinforcement, that's a reason to keep the *specific sentence the evidence points
to*, not a license to keep the whole original paragraph - re-test after narrowing it down, since
the minimal version may still hold.

### 5. Check for stale bug-history text

If a sentence exists ONLY to explain why a past bug happened ("you have been unreliable at..."),
prefer keeping the *rule* in the model-facing text but moving the *history/evidence* into this
YAML file's own comments (this repo's C# files already do this well - match that convention).
The model doesn't need to be told about its own past failures to follow a rule correctly; future
maintainers reading the YAML do need the "why," but as a comment, not as tokens the model reads
on every single turn.

This can pull in the opposite direction from step 4's fragility check above, but don't assume a
fragile tool's history clause has to stay verbatim just because it's fragile - `ReturnToLaunch`'s
own "since you have been unreliable at..." clause was moved into a YAML comment (per this step)
in the same pass that also narrowed the model-facing sentence down to the minimal load-bearing
rule (per step 4's follow-up finding), and it still measured 8/8 - the history text was never
part of what made the rule work; only the specific behavioral instruction was. Don't "clean up" a
fragile tool's proven-working text on this principle alone without re-running its live test after
the change, though - on a tool step 4 already flagged as fragility-proven, principle 5 is a
nice-to-have that comes second to whatever the real test result says. On the 10 non-fragile
tools, apply it freely.

### 6. Every description must be a multi-line block scalar, never a single-line quoted string

Write every `description:` and every `parameters:` sub-field as a YAML folded block scalar
(`>-`), wrapped at a readable width (roughly 80-100 columns), not as one long single-line
double-quoted string. A folded scalar's line breaks collapse to spaces at parse time, so this is
a pure readability fix - the actual string value the model receives is unchanged - but a
100+ word single-line string is unreadable without horizontal scrolling, which makes every check
in this skill harder to actually do by eye. Apply this retroactively too: if you're already
touching a tool's description for some other reason and it's still single-line, reformat it to a
block scalar in the same edit, don't leave it as tech debt for the next person to hit. Watch two
real YAML gotchas when reformatting: a blank line inside a folded scalar becomes a paragraph
break (a literal newline) in the parsed string, and a line indented further than the rest breaks
the fold for that line - avoid both by keeping every wrapped line at the same indentation with no
blank lines, unless a real paragraph break is actually wanted. Verify the reformat didn't
accidentally change content: rebuild and run the fast suite at minimum after reformatting.

### 7. Confirm live-test coverage exists

For any tool whose description or behavior actually changed, confirm a `Category=Live` test in
`tests/UavOps.Agent.Tests/Agents/` exercises that tool with a real assertion against backend
ground truth (see `ReturnRemainingFleetLiveTests`, `RepeatedFleetQueryLiveTests`,
`FullShiftScenarioLiveTests` for the established pattern - independently re-querying the real
tool/state afterward, never just checking the response text sounds right). If none exists and the
change is non-trivial, say so as a finding rather than silently skipping verification.

## Output

Report findings the same way a code review would: one item per issue, naming the file(s)/tool(s)
involved, what the issue is, and a concrete suggested rewrite - not just "this could be tighter."
If asked to apply fixes, make the edits, then:

1. `dotnet build UavOps.sln` (stop any running `UavOps.Agent.exe` you started yourself first if it
   locks the build - never stop one you didn't start without asking).
2. `dotnet test tests/UavOps.Agent.Tests --filter "Category!=Live"` - must stay green.
3. Run whichever `Category=Live` test(s) actually exercise the changed tool(s) at the project's
   real repeat count (`LiveTestSupport.RepeatCount`, default 8 - a single pass proves nothing here,
   this project's own established standard). `FullShiftScenarioLiveTests` is the broadest one
   available and worth running for any change touching Moav/Watchdog/Simulator tool descriptions.
4. Report the before/after pass rate, not just "tests passed" - the whole reason this skill exists
   is that a description can look fine and still measurably change real tool-selection behavior.
   Anything less than every repeat passing (e.g. 7/8) is a real regression to root-cause and fix,
   not a result to accept, round up, or explain away as "probably fine" - see step 4's own
   `ReturnToLaunch` example for what actually finding and fixing that gap looks like in practice.
