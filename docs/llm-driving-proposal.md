# LLM Driving Proposal

## Goal

Keep the control loop simple while giving the model enough context to make good decisions.

## Core Design

1. Inject `overview` into prompt context every tick automatically.
2. Include a `directory` in that overview so the model knows what state paths exist.
3. Expose one read tool: `state(path: string)`.
4. Expose one intent tool: `prayer_generate_script(instruction: string)`.
5. Execute scripts deterministically outside the LLM.

## Turn Contract

Per tick:

1. Runtime injects:
   - `overview`
   - `directory`
   - `last_result`
   - `recent_events`
   - `objective`
2. Model optionally calls `state(path)` for drill-down data.
3. Model must end with exactly one `prayer_generate_script(...)` call.
4. Runtime executes returned script and captures result.
5. Next tick repeats with updated context.

## Injected Overview Format

`overview` should be compact and high-signal only.

```json
{
  "overview": {
    // important info like 
  },
  "directory": {
    "ship": "Modules, cooldowns, cargo, fitting",
    "navigation": "Location, route, warp options, hazards",
    "poi": "Nearby entities and distances",
    "trade": "Cargo, prices, stash, deals, orders, spread",
    "missions": "Active mission state and objectives"
  }
}
```

## `state(path)` Behavior

- Dot-path reads only (for example: `combat.primary_target`, `market.orders`).
- Deterministic truncation and stable ordering.
- Return metadata:
  - `path_found`
  - `truncated`
  - `total_items` (when applicable)

Example response shape:

```json
{
  "path": "combat",
  "path_found": true,
  "truncated": false,
  "value": {}
}
```

## Capability Contract (What the Model Can Do)

Provide a short action capability list in prompt context so the model knows valid script operations. Keep this very simple just list the DSL verbs.

## Prompt Rules

- Start from injected `overview` and `directory`.
- Use `state(path)` only when extra detail is needed.
- End each tick with exactly one `prayer_generate_script(...)`.
- Keep prayer instructions concrete and short.
- Prefer survival when uncertain.

## Runtime Limits

- Max drill-down calls per tick (start with `3`).
- One prayer call per tick.
- Per-tick timeout and token budget.
- Stop or fallback after repeated execution failures.

## Logging and Replay

Log per tick:

- injected `overview`/`directory`
- `state(path)` calls and responses
- prayer text
- generated script
- execution result
- token and latency metrics

Store logs in replayable order:

`tick -> context -> tool calls -> prayer -> script -> result`

## Why This Approach

- Fast MVP implementation.
- Cleaner than full raw-state dumping.
- Better guidance than overview-only without drill-down.
- Keeps execution deterministic while using LLM strategy.

## Evolution Path

1. Start with this minimal interface.
2. Measure most-used `state(path)` lookups.
3. Promote hot paths to dedicated tools later (`get_status`, `get_ship`, etc.).
4. Keep `state(path)` as fallback and debugging interface.
