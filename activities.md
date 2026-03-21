# Activities

This document defines the current design direction for Prayer Activities.

Activities are the planning layer above the existing DSL. They describe goals,
strategy choices, and bounded tactical prompts. The DSL remains the validated
execution language that actually drives the runtime.

The core pipeline is:

```txt
English intent
-> Activity tree
-> bounded DSL chunk
-> command execution
```

This is intentionally not:

```txt
English intent
-> giant DSL script
```

## Goals

Activities exist to solve problems the DSL should not own:

- hierarchical goal decomposition
- strategy selection like mine vs buy vs craft
- replanning as world state changes
- reusable parameterized planning patterns
- dynamic expansion for domains like crafting

The DSL still owns:

- concrete executable actions
- preflight validation of commands and arguments
- bounded control flow
- inspectable execution artifacts

## Layering

Prayer should be thought of as three layers:

```txt
Activities
-> DSL
-> Commands / SpaceMolt runtime
```

Responsibilities:

- `Activities`: decide what outcome to pursue and which strategy to try
- `DSL`: describe the next bounded action chunk
- `Commands`: perform concrete game operations

Activities are the planning IR.
DSL is the execution IR.

## Why Keep The DSL

The DSL still provides high-value guarantees:

- parse before execution
- validate `go` targets and other grounded arguments before execution
- reject unsupported syntax early
- provide correction feedback to generation
- keep the executed artifact short and inspectable

This is a major reason not to replace the DSL with Lua as the main artifact.
Lua may still be useful internally or for expert scripting later, but the
planner/runtime architecture benefits from a narrow validated language.

## Activity Model

Activities are declarative planning nodes.

An activity may contain:

- a name
- parameters
- an optional `when` block
- an optional `goal` block
- an optional `fulfillments` block

Minimal shape:

```txt
activity Name(param1, param2) {
  when { SOME_CONDITION(param1); SOME_OTHER_CONDITION(param2) > 0 }
  goal { SOME_OTHER_CONDITION(param2) > 50 }
  fulfillments { ChildA(param1); ChildB(param2) }
}
```

Leaf shape (using the `Do` builtin):

```txt
activity Name(param1) {
  when { SOME_CONDITION(param1) }
  fulfillments { Do("Perform some bounded tactic with $param1") }
}
```

## Semantics

### `when`

`when` is a `{ }`-delimited block of applicability conditions separated by
`;`, all of which must hold.

It answers:

- may this activity be selected right now?

It does not answer:

- is this activity complete?

Examples:

```txt
when { MINEABLE(iron_ore); FUEL() > 20 }
```

```txt
when { BUYABLE(iron_ore); CREDITS() >= 1000 }
```

Single condition:

```txt
when { CRAFTABLE(fuel_cell) }
```

### `goal`

`goal` is a `{ }`-delimited block of completion conditions separated by `;`,
all of which must hold.

It answers:

- has this objective been achieved?

`goal` is optional. An activity without a `goal` never self-completes — it
runs until a parent's goal is satisfied or it is removed. This is the natural
shape for leaf activities that simply do work in service of a parent goal.

When present, a fully satisfied `goal` hard-marks the activity complete. It is
not a temporary pause condition. If Prayer later needs persistent maintenance
behavior, that should be modeled separately rather than overloading `goal`.

Examples:

```txt
goal { STASH(sol_central, iron_ore) >= 500 }
```

```txt
goal { STASH(sol_central, iron_ore) >= 500; STASH(sol_central, fuel_cell) >= 50 }
```

### `fulfillments`

`fulfillments` is a `{ }`-delimited block of candidate child strategies
separated by `;`.

They are not imperative statements. They express possible ways for the parent
activity to make progress toward its goal.

Fulfillment selection follows behavior-tree semantics: on each tick the
planner starts from the top of the list, skips children whose `when`
conditions do not hold or whose `goal` is already satisfied, and pushes the
first applicable child. No cursor is persisted between ticks.

Example:

```txt
fulfillments { Mine(iron_ore, sol_central); Craft(iron_ore, sol_central); Buy(iron_ore, sol_central) }
```

Multiline is fine — the parser is not whitespace-sensitive:

```txt
fulfillments {
  Mine(iron_ore, sol_central);
  Craft(iron_ore, sol_central);
  Buy(iron_ore, sol_central)
}
```

### `Do("...")`

`Do` is a builtin activity and the leaf escape hatch. It accepts a single
string prompt and means:

- generate the next DSL chunk for this intent
- execute that chunk
- re-evaluate the activity tree afterward

DSL generated inside `Do` must not contain loops. This keeps each chunk
finite and inspectable.

`Do` can appear anywhere a named activity call can — as a root or inside
`fulfillments`. There is no syntactic difference between calling `Do` and
calling any other activity:

```txt
Do("Mine iron ore and stash it at sol_central")
```

## Conditions

Activities should reuse the existing DSL condition syntax and parser.

That means `goal` and `when` can use the same condition language already used
by DSL control flow. This keeps condition syntax compact, model-friendly, and
consistent across planning and execution.

Example:

```txt
goal STASH(sol_central, iron_ore) >= 500
when MINEABLE(iron_ore)
```

Expected planner predicates include:

- `MINEABLE(item)`
- `CRAFTABLE(item)`
- `BUYABLE(item)`

Predicates are builtins. New predicates are added to a fixed catalog in code,
not declared in the activity language itself. This keeps the predicate surface
controlled and avoids user-defined predicate complexity.

## Parameters

Activities support parameters, but not mutable variables.

Parameters are immutable macro-like values used for reuse. They can be expanded
into:

- `goal` conditions
- `when` conditions
- child activity arguments
- `Do("...")` prompt strings

Example:

```txt
activity Mine(item, stashLocation) {
  when { MINEABLE(item) }
  fulfillments { Do("Mine $item and stash it at $stashLocation") }
}
```

This is a parameterized activity template, not a programming language with
assignments or local mutation.

Parameters begin as plain text macros. They are substituted into `goal`,
`when`, child activity arguments, and `do("...")` strings by simple
interpolation. No type system is implied at this stage. Grounding and
validation of parameter values (e.g. confirming a location is known to the
runtime) is left to DSL validation and command execution, not to the activity
layer.

Current direction:

- support immutable parameters
- parameters are text macros, typed by usage context
- no reassignment
- no local variable mutation
- no general-purpose expression language beyond conditions

## What Lives In Activities Vs DSL

Activities should own long-lived orchestration.

DSL should own bounded execution.

This implies:

- Activities should handle repetition by reevaluating goals and fulfillments
- DSL should avoid being the owner of persistent loops
- `repeat` and `until` in the DSL should likely be removed from generated
  scripts or at least strongly de-emphasized once Activities are in place
- small local `if` guards may still be useful in DSL

The important rule is:

- Activities own "keep trying"
- DSL owns "do this next"

## Strategy Activities

Activities like `Mine`, `Buy`, and `Craft` should usually represent useful
outcomes, not isolated verbs.

For example, this is underspecified:

```txt
Mine(iron_ore, asteroid_belt_alpha)
```

because it raises:

- then what?
- where does the ore end up?

A better default shape is:

```txt
Mine(item, stashLocation)
Buy(item, stashLocation)
Craft(item, stashLocation)
```

These imply an outcome-oriented tactic such as:

- acquire the item
- route it into the target stash location

This keeps higher-level planning focused on useful state change rather than raw
actions.

## Example

Acquire 500 iron ore and stash it at `sol_central`:

```txt
activity AcquireAndStashIron {
  goal { STASH(sol_central, iron_ore) >= 500 }
  fulfillments {
    Mine(iron_ore, sol_central);
    Craft(iron_ore, sol_central);
    Buy(iron_ore, sol_central)
  }
}
```

Reusable tactic activities:

```txt
activity Mine(item, stashLocation) {
  when { MINEABLE(item) }
  fulfillments { Do("Mine $item and stash it at $stashLocation") }
}

activity Craft(item, stashLocation) {
  when { CRAFTABLE(item) }
  fulfillments { Do("Craft $item and stash it at $stashLocation") }
}

activity Buy(item, stashLocation) {
  when { BUYABLE(item) }
  fulfillments { Do("Buy $item and stash it at $stashLocation") }
}
```

Operationally:

1. Evaluate `AcquireAndStashIron.goal` — if satisfied, mark complete
2. Otherwise tick fulfillments from the top (BT-style)
3. Push first applicable child (`Mine`, `Craft`, or `Buy`) whose `when` holds
4. `Mine` has no `goal`, so it emits `Do(...)` which generates a bounded DSL chunk
5. Execute the chunk
6. Re-evaluate the tree from the top

Possible generated DSL chunk from `Mine(iron_ore, sol_central)`:

```txt
mine iron_ore;
stash iron_ore;
```

The exact DSL emitted can still depend on state grounding and runtime
normalization.

## Missions

Dynamic missions are a special case.

For missions, the game may already provide:

- a prompt or objective text
- a completion function or completion predicate

That means a mission activity can often be represented as a dynamic leaf or a
small AI-built tree rooted in the mission objective.

Two good options:

- mission-specific leaf activity with `goal MISSION_COMPLETE(mission_id)` and
  `do("...")`
- AI-generated Activity tree derived from mission objectives, still checked
  against the game's real completion signal

Important rule:

- game-provided completion remains the source of truth
- AI-generated structure is advisory planning, not authoritative completion

## Native Activities And Dynamic Expansion

Some activities should be native planner nodes implemented in code rather than
fully authored by the LLM or user.

Crafting is the clearest example.

### Why crafting is special

Crafting often requires:

- recipe lookup
- quantity math
- recursive ingredient expansion
- deficit calculation
- cycle detection
- choosing acquisition strategy for ingredients

That logic is dynamic, but deterministic. It should not depend on the LLM
inventing the right recursive structure every time.

### Native expansion model

A native activity like:

```txt
Craft(steel_plate, 50, sol_central)
```

may expand at planning time into a richer activity subtree based on recipe
data, inventory, and stash state.

Conceptually:

```txt
Craft(steel_plate, 50, sol_central)
-> Acquire(iron_ore, 100, sol_central)
-> Acquire(carbon, 50, sol_central)
-> Do("Craft 50 steel_plate and stash it at sol_central")
```

Important design point:

- expansion happens at the Activity layer
- expansion logic lives in planner/runtime code
- the Activity language itself should stay small

So yes, Activities can expand dynamically, but that does not mean the Activity
syntax should become a full recursive programming language.

## LLM-Facing Representation

Conditions and activity structure are more model-friendly in compact textual
syntax than in verbose JSON ASTs.

For example:

```txt
goal STASH(sol_central, iron_ore) >= 500
```

is much more natural for the model than a full JSON condition tree.

Current preferred boundary:

- LLM-facing: compact activity syntax
- runtime-internal: parsed AST / structured objects

This matches the existing DSL philosophy:

- textual artifact for generation and inspection
- parser and validator for runtime correctness

## AI Planning Guidance

The AI should plan Activities as constrained decomposition, not freeform
reasoning.

Good AI behavior:

- identify the top-level goal
- choose a small set of fitting activity templates
- fill parameters
- stop at useful leaves
- use `do("...")` only when a native/template activity is not enough

Bad AI behavior:

- invent deep arbitrary trees
- encode execution details that belong in DSL
- use open-ended `do("...")` where a known activity fits
- rebuild crafting trees that native expansion should own

RAG should likely be split by task:

- examples for `English/objectives -> Activity tree`
- examples for `leaf do(...) -> DSL`

These are different generation problems and should not share one undifferentiated
example pool.

## Design Constraints

To keep Activities useful and avoid rebuilding a worse general-purpose language:

- no mutable variables
- no reassignment
- no arbitrary loops in Activities
- no freeform recursive meta-programming in syntax
- no hidden long-running control regimes inside leaf execution

If a feature request can be solved by:

- a new built-in/native activity kind
- a new predicate
- a new reusable parameterized template

prefer that over making the language broadly more expressive.

## Summary

The intended direction is:

- Activities are the main planning layer
- DSL remains the bounded validated execution layer
- `goal` means hard completion (optional; absent = never self-completes)
- `when` means applicability
- `Do("...")` is the builtin leaf escape hatch — generates a bounded DSL chunk
- parameterized activity templates are supported
- conditions reuse the DSL condition parser
- fulfillment selection is BT-style: top-down each tick, no persistent cursor
- crafting and similar domains should use native Activity expansion in code

This gives Prayer:

- inspectable planning
- reusable hierarchical strategies
- dynamic domain expansion where needed
- preserved DSL validation and execution safety
