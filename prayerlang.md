# PrayerLang Prompt Reference

This document mirrors what Prayer sends to the script-generation model today.
It is derived from:

- `src/Prayer/MiddleRuntime/Agent/AgentPrompt.cs`
- `src/Prayer/MiddleRuntime/DSL/DSL.cs`
- `src/Prayer/MiddleRuntime/DSL/DslConditionCatalog.cs`
- `src/Prayer/MiddleRuntime/Commands/CommandCatalog.cs`

## Base system prompt

```txt
You are an autonomous agent playing the online game SpaceMolt. Pursue the user objective with short, deterministic DSL scripts. Avoid redundant movement and setup steps. Do not add dock before commands that can auto-dock. Do not add go before mine; use mine or mine <resource_id> directly so runtime can resolve navigation.
```

## Prompt scaffold sent to the model

At generation time, Prayer builds a chat-style prompt like this:

```txt
<|start_header_id|>system<|end_header_id|>
{BaseSystemPrompt}
You write DSL scripts for this game agent.
Output only DSL script text. No markdown fences and no explanation.
Terminate every command with a semicolon (;).
Use only the DSL syntax implied by the examples.
Do not invent unsupported commands.
<|eot_id|><|start_header_id|>user<|end_header_id|>
Attempt: {attemptNumber}

User request:
{userInput}

{stateContextBlock}

{dslCommandReferenceBlock}
Prompt -> script examples:
{examplesBlock}

{retryContext_if_previous_attempt_failed}
Generate a DSL script now.
Checklist:
- every command ends with ;
- blocks are allowed only as: repeat { ... }, if <CONDITION> { ... }, until <CONDITION> { ... }
- avoid explicit dock unless user explicitly asks for dock
- avoid explicit go before mine; use mine or mine <resource_id>
- no markdown fence
Return only the script text.
<|eot_id|><|start_header_id|>assistant<|end_header_id|>
```

## DSL reference block (injected into prompt)

Prayer injects a generated DSL reference block with:

- Commands from `CommandCatalog` (each rendered as a DSL signature ending in `;`)
- `halt;`
- Keywords and block forms
- Condition token list
- Short examples

The block includes these rule lines exactly:

```txt
- Blocks are supported via: repeat { ... }
- Conditional blocks are supported via: if <CONDITION> { ... }
- Until blocks are supported via: until <CONDITION> { ... }
- All commands still end with ';' inside repeat blocks.
```

Current condition tokens:

- Numeric: `FUEL()`, `CREDITS()`

## Current command surface (catalog)

As of this repo state, command catalog entries are:

- `mine`
- `survey`
- `go`
- `accept_mission`
- `abandon_mission`
- `dock`
- `repair`
- `sell`
- `buy`
- `cancel_buy`
- `cancel_sell`
- `retrieve`
- `stash`
- `switch_ship`
- `install_mod`
- `uninstall_mod`
- `buy_ship`
- `buy_listed_ship`
- `commission_quote`
- `commission_ship`
- `commission_status`
- `sell_ship`
- `list_ship_for_sale`
- `wait`
- `halt` (DSL keyword/command)

## Argument patterns seen in prompt logs

From `src/Prayer/log/llm.log` prompt blocks, the command reference shown to the model uses signatures like:

- `go <destination>;`
- `mine <resource?>;`
- `sell <item?>;`
- `buy <item> <count>;`
- `retrieve <item> <count?>;`
- `stash <item?>;`
- `list_ship_for_sale <ship> <price>;`
- `wait <count?>;`

Observed aliases in prompt/logs:

- `stash` is the storage-deposit command.
- `retrieve` is the storage-withdraw command.
- `sell;` is the default way to sell all cargo in generated outputs.

## Dynamic blocks explained

- `stateContextBlock`: live summarized game/runtime state used for grounded generation.
- `examplesBlock`: prompt-to-script examples (seeded from `seed/script_generation_examples.json` and cached at `cache/script_generation_examples.json`).
- `retryContext`: included only after a failed parse/validation attempt; contains previous error + previous script and asks for correction.

## Control flow semantics (from runtime logs)

Execution behavior in `src/Prayer/log/ast_walker.log`:

- Scripts execute as frames (`Root`, `If`, `Repeat`) with explicit push/pop events.
- `if`:
  - Condition evaluated once when encountered (`if_visit ... known=True value=...`).
  - Body frame is entered only if condition is true.
- `repeat`:
  - Body frame is revisited continuously (`repeat_visit`, `frame_push` of `Repeat`).
  - This is an intentional infinite loop construct until externally halted or replaced.
- Root script ends when all statements are exhausted (`frame_complete` then `step_scan_end`).

Validation/normalization behavior:

- `src/Prayer/log/script_normalization.log` shows formatting normalization (e.g., splitting one-line scripts into canonical multiline form).
- `src/Prayer/log/script_compile_failures.log` shows strict parse failures when syntax is invalid (example: condition function format mismatch in an earlier run).

Argument validation behavior:

- `src/Prayer/log/go_arg_validation.log` records rejected destinations (for example `sol`, `sol_central`) when not present in known systems/POIs/candidates.
- This is part of why the DSL favors grounded identifiers from the provided state context.

## Language rationale

PrayerLang is intentionally narrow so generation is easy to constrain and execution is predictable:

- Determinism: small command surface + explicit control flow reduce ambiguous plans.
- Verifiability: scripts can be parsed, normalized, logged, and replayed step-by-step.
- Safety: unsupported commands/args fail fast instead of triggering opaque model behavior.
- Interruptibility: runtime can halt, replace script text, and resume with visible state.
- Grounding: prompts include current systems/POIs/items so identifiers come from live game context.

## Notes

- This file is a human-readable mirror, not executable grammar.
- If prompt behavior changes, regenerate this file from the source files listed at top.
- Runtime logs used for this version:
  - `src/Prayer/log/llm.log`
  - `src/Prayer/log/planner_prompts.log`
  - `src/Prayer/log/script_writer_context.log`
  - `src/Prayer/log/ast_walker.log`
  - `src/Prayer/log/script_normalization.log`
  - `src/Prayer/log/script_compile_failures.log`
  - `src/Prayer/log/go_arg_validation.log`
