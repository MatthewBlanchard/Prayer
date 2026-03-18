# Activities Implementation Plan

This document is the implementation plan for adding the Activities planning layer
to Prayer, as specified in `activities.md`.

The goal is to add Activities as the new planning IR above the DSL without
breaking or replacing existing DSL execution.

---

## Overview

The work breaks into six phases:

1. Activity AST and parser
2. Builtin planner predicates
3. Activity planner (tree evaluation loop)
4. Integration into RuntimeHost
5. LLM generation for Activities
6. Native activity expansion (crafting)

Each phase is independently testable and can be merged incrementally.

---

## Phase 1 — Activity AST and Parser

### New files

```
src/Prayer/Activities/ActivityAst.cs
src/Prayer/Activities/ActivityParser.cs
```

### AST nodes

Define a minimal set of AST nodes for the activity language:

```csharp
// Top-level parsed unit
record ActivityProgram(IReadOnlyList<ActivityDefinition> Definitions);

// activity Name(p1, p2) { ... }
record ActivityDefinition(
    string Name,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<DslConditionAstNode> When,      // all must hold; empty = always applicable
    IReadOnlyList<DslConditionAstNode> Goal,      // all must hold; empty = never auto-completes
    IReadOnlyList<ActivityCall> Fulfillments      // Do("...") is just an ActivityCall to the Do builtin
);

// ChildActivity(arg1, arg2) inside fulfillments
record ActivityCall(string Name, IReadOnlyList<string> Arguments);
```

Parameters are strings throughout — text macro substitution happens at bind
time, not parse time.

### Parser

`ActivityParser` parses the compact textual syntax described in `activities.md`.

Reuse the existing `DslConditionCatalog` condition parser for `when` and `goal`
blocks. The activity parser only needs to handle:

- `activity <Name>(<params>) { ... }` blocks
- `when { <cond>; <cond>; ... }` — `;`-separated conditions in a `{ }` block
- `goal { <cond>; <cond>; ... }` — `;`-separated conditions in a `{ }` block
- `fulfillments { <Name>(<args>); ... }` — `;`-separated activity calls in a `{ }` block
- `Do("<string>")` inside `fulfillments` — parsed as a regular `ActivityCall`

No whitespace sensitivity anywhere. Block boundaries are `{` / `}`. Separators
are `;`. Trailing `;` before `}` is optional.

Inside condition argument lists, `,` separates arguments. The parser tracks
paren depth to distinguish argument commas from any future list separators.

No new parser combinator library is needed — the same Superpower-based approach
used in `DSL.cs` applies.

### Parameter substitution

Add a utility method:

```csharp
static string ExpandMacros(string template, IReadOnlyDictionary<string, string> bindings)
```

This replaces `$paramName` occurrences in `do("...")` strings, `when`/`goal`
condition argument positions, and child activity argument lists.

Substitution into condition AST nodes requires walking the condition tree and
replacing `DslMetricCallOperandAstNode` or argument values that match a bound
parameter name.

---

## Phase 2 — Builtin Planner Predicates

### New file

```
src/Prayer/Activities/ActivityPredicateCatalog.cs
```

### Condition evaluation

Activity `when` and `goal` conditions use the existing `DslBooleanEvaluator`
directly. All existing predicates are available in activities without any
changes:

| Predicate | Notes |
|---|---|
| `FUEL()` | Ship fuel percentage |
| `CREDITS()` | Player credits |
| `CARGO(item)` | Quantity in ship cargo |
| `STASH(poi, item)` | Quantity in station storage |
| `MISSION_COMPLETE(id)` | Mission completion check |

`DslBooleanEvaluator` is the primary evaluator for all conditions. No changes
needed to use it in the planner.

### New planning-only predicates

`ActivityPredicateCatalog` adds three predicates that only make sense at
planning time and have no DSL execution equivalent:

| Predicate | Evaluates to true when |
|---|---|
| `MINEABLE(item)` | At least one known POI in `GameState.POIs` can yield the item |
| `CRAFTABLE(item)` | `GameState.AvailableRecipes` contains a recipe that produces the item |
| `BUYABLE(item)` | `GameState.EconomyDeals` contains at least one buy offer for the item |

```csharp
static class ActivityPredicateCatalog
{
    public static bool Evaluate(string predicate, IReadOnlyList<string> args, GameState state);
}
```

The activity planner's condition evaluator calls `DslBooleanEvaluator` first.
If the predicate is unknown there, it falls back to `ActivityPredicateCatalog`.
Both catalogs remain separate code. `DslConditionCatalog` is not modified.

---

## Phase 3 — Activity Planner

### New file

```
src/Prayer/Activities/ActivityPlanner.cs
```

### Planner state

The planner holds the active activity tree as a stack of bound activity nodes.
Each node tracks:

```csharp
record BoundActivity(
    ActivityDefinition Definition,
    IReadOnlyDictionary<string, string> Bindings    // param -> resolved text value
    // No cursor — fulfillment selection restarts from the top each tick (BT-style)
);
```

The planner maintains a `Stack<BoundActivity>` representing the current path
from root to active leaf.

### Evaluation loop

The public method the runtime calls:

```csharp
Task<ActivityPlannerResult> TickAsync(GameState state, CancellationToken ct);
```

Returns one of:

```csharp
abstract record ActivityPlannerResult;
record Complete : ActivityPlannerResult;
record Blocked : ActivityPlannerResult;    // no applicable fulfillment found
record EmitDo(string Prompt) : ActivityPlannerResult;
```

Evaluation algorithm per tick:

```
1. If stack is empty → Complete
2. Peek top of stack → current activity
3. If current.Goal is non-empty and all conditions evaluate true → pop stack, go to 1
4. Walk fulfillments from the top (BT-style, no cursor):
     a. Bind child parameters from current bindings + args
     b. If child is the Do builtin → return EmitDo(expanded prompt)
     c. Evaluate all child.When conditions against state
     d. If child.Goal is non-empty and already satisfied → skip (already done)
     e. If all when conditions hold → push child onto stack, go to 2
5. If no fulfillment applicable → return Blocked
```

Step 3 (goal check before do) ensures a leaf re-evaluates its parent goal
before emitting another DSL chunk, which handles the case where a previous
chunk already satisfied the goal.

### Do("...") emission

When the planner returns `EmitDo`, the runtime passes the prompt to
`ScriptGenerationService` to generate a bounded DSL chunk (no loops). After
the chunk executes, the planner ticks again from the top of the tree.

### Do as a builtin activity

`Do` is a builtin activity — not special syntax and not a field on
`ActivityDefinition`. The planner recognizes it by name in step 4b of the
evaluation loop and treats it as a leaf that immediately emits its string
argument as a prompt. It requires no authored definition and can appear
anywhere a named activity call can: as a root or inside `fulfillments`.

```txt
Do("Mine iron ore and stash it at sol_central")
```

Parsed as `ActivityCall("Do", ["Mine iron ore and stash it at sol_central"])`.
The planner special-cases the name `Do` during fulfillment walking.

---

## Phase 4 — RuntimeHost Integration

### Changes to existing files

```
src/Prayer/MiddleRuntime/Host/RuntimeHost.cs
src/Prayer/MiddleRuntime/Agent/Agent.cs
src/Prayer/Program.cs
```

### New execution mode

`RuntimeHost` currently operates in script mode: a DSL script is loaded and
stepped until complete or halted.

Add an activity mode alongside script mode. The host checks which mode is
active on each iteration of `RunAsync`.

In activity mode:

```
1. Tick ActivityPlanner with current GameState
2. On Complete → mark session done, await new activity
3. On Blocked → log, optionally notify client, await intervention
4. On EmitDo(prompt):
     a. Generate bounded DSL (no loops) via ScriptGenerationService
     b. Load into CommandExecutionEngine
     c. Step to completion as normal
     d. Return to step 1
```

The DSL generation call for activity mode should set a flag or modify the
system prompt to indicate loops are prohibited in the output. This can be a
separate prompt variant (see Phase 5).

### New API endpoints

```
POST   /api/runtime/sessions/{id}/activity          - Set and activate an activity tree
GET    /api/runtime/sessions/{id}/activity          - Get current activity tree state
DELETE /api/runtime/sessions/{id}/activity          - Clear active activity tree
```

`POST /activity` body: raw activity text (same as `POST /script` body for DSL).

Add activity planner state to the session snapshot returned by
`GET /api/runtime/sessions/{id}/snapshot`.

### Agent.cs

Add `SetActivity(string activityText)` alongside the existing `SetScript()`.
This parses and loads the activity tree into the planner without immediately
executing.

---

## Phase 5 — LLM Generation for Activities

### Two generation problems

As noted in `activities.md`, activity generation and DSL leaf generation are
different problems and should not share an example pool.

| Problem | Input | Output |
|---|---|---|
| Activity tree generation | English objective | Activity tree text |
| DSL leaf generation | `do("...")` prompt + state | Bounded DSL (no loops) |

### New file

```
src/Prayer/MiddleRuntime/Agent/ActivityGenerationService.cs
```

Similar structure to `ScriptGenerationService` but:

- Different system prompt explaining the activity syntax and planner semantics
- Different RAG example store (activity-specific seed examples)
- Output is activity text, validated by `ActivityParser`
- Max tokens can be higher than DSL generation (activity trees can be taller)

### Activity generation system prompt

The prompt must convey:

- Available builtin activities (`Mine`, `Buy`, `Craft` and their signatures)
- `when`/`goal`/`fulfillments`/`do("...")` semantics
- That `do("...")` is a bounded leaf, not a planner
- That parameters are text macros, not variables
- Available planner predicates (`MINEABLE`, `CRAFTABLE`, `BUYABLE`, `STASH`, etc.)

### DSL leaf generation changes

The existing DSL generation prompt should be updated to explicitly state:

- `repeat`, `until` are not allowed in this output
- This is a bounded single-tactic chunk, not a full plan
- The current activity context (which leaf is executing and why)

Add a `GenerateBoundedDslChunkAsync(string doPrompt, GameState state)` method
to `ScriptGenerationService` or `ActivityGenerationService` that uses this
variant prompt.

### New endpoint

```
POST   /api/runtime/sessions/{id}/activity/generate
```

Body: plain English objective. Returns parsed + validated activity tree text,
and optionally activates it (via query param `?activate=true`).

### Seed examples

Add:

```
seed/activity_generation_examples.json
```

Seed with representative examples:

```json
[
  {
    "Prompt": "Acquire 500 iron ore and stash it at sol_central",
    "Activity": "activity AcquireIron {\n  goal { STASH(sol_central, iron_ore) >= 500 }\n  fulfillments { Mine(iron_ore, sol_central); Buy(iron_ore, sol_central) }\n}"
  }
]
```

### RAG store

Add `ActivityGenerationExampleStore.cs` mirroring
`ScriptGenerationExampleStore.cs` but pointing at the activity seed/cache files.

Cache at `cache/activity_generation_examples.json`.

---

## Phase 6 — Native Activity Expansion (Crafting)

This phase is deferred until Phases 1–5 are stable. It does not require
language changes.

### Design

`Craft(item, quantity, stashLocation)` is a native activity implemented in code
rather than authored text.

When the planner encounters a `Craft` activity call, it delegates expansion to:

```csharp
static class NativeCraftExpander
{
    public static IReadOnlyList<BoundActivity> Expand(
        string item, int quantity, string stashLocation, GameState state);
}
```

Expansion logic:

1. Look up recipe in `GameState.AvailableRecipes`
2. Calculate ingredient quantities needed (accounting for current inventory)
3. For each ingredient, emit an `Acquire(ingredient, qty, stashLocation)` child
4. Emit a final `do("Craft {quantity} {item} and stash it at {stashLocation}")` leaf

`Acquire` is a builtin activity that tries `Mine`, `Buy`, or `Craft`
(recursively) as fulfillments.

Cycle detection: track item names along the expansion path. If an item appears
twice, mark the branch blocked and log the cycle.

The expander runs at plan time, not parse time. The Activity language syntax
does not change to support this — it is purely a planner-level dispatch on the
activity name.

---

## File Map

```
src/Prayer/Activities/
  ActivityAst.cs                    [AST nodes]
  ActivityParser.cs                 [Parser]
  ActivityPredicateCatalog.cs       [MINEABLE, CRAFTABLE, BUYABLE]
  ActivityPlanner.cs                [Tree evaluation loop]
  NativeCraftExpander.cs            [Phase 6: crafting expansion]

src/Prayer/MiddleRuntime/Agent/
  ActivityGenerationService.cs      [LLM generation for activity trees]
  ActivityGenerationExampleStore.cs [RAG for activity examples]

src/Prayer/MiddleRuntime/Host/
  RuntimeHost.cs                    [Modified: add activity mode]

src/Prayer/MiddleRuntime/Agent/
  Agent.cs                          [Modified: add SetActivity()]
  ScriptGenerationService.cs        [Modified: add GenerateBoundedDslChunkAsync()]

src/Prayer/Program.cs               [Modified: add activity endpoints]

seed/
  activity_generation_examples.json [New seed examples]
```

---

## Order of Work

1. `ActivityAst.cs` — define nodes, no dependencies
2. `ActivityParser.cs` — parse activity text into AST, reuse condition parser
3. `ActivityPredicateCatalog.cs` — implement three predicates against `GameState`
4. `ActivityPlanner.cs` — evaluation loop, depends on 1–3
5. `RuntimeHost.cs` / `Agent.cs` — wire planner into execution loop
6. `Program.cs` — expose new endpoints
7. `ActivityGenerationService.cs` + seed examples — LLM generation for trees
8. `ScriptGenerationService.cs` prompt variant — bounded DSL generation
9. `NativeCraftExpander.cs` — Phase 6, deferred

Each step can be reviewed and merged independently. Steps 1–4 carry no
runtime risk as they add new files only.

---

## What Does Not Change

- `DSL.cs` and the DSL parser are unchanged
- `DslConditionCatalog.cs` is unchanged (new predicates live in `ActivityPredicateCatalog`)
- `CommandExecutionEngine.cs` is unchanged
- Existing script mode endpoints are unchanged
- `DslBooleanEvaluator.cs` gains a minor extension (fallback to activity predicates) but no breaking changes
- All existing RAG examples remain valid and are not merged with activity examples
