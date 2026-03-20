# Skills & Overrides — DSL Extension Proposal

## Motivation

The current DSL executes a flat sequence of commands with `if`/`until` control flow. Two gaps exist:

1. **No reusable named routines.** Common sequences (navigate to a station, stash cargo, etc.) must be inlined everywhere or relied on from the LLM generating them correctly each time.
2. **No persistent safety behaviors.** There is no way to express "always go refuel when fuel drops below 50%" independently of whatever mission script is running.

This proposal introduces two new top-level DSL constructs to address both gaps.

---

## Construct 1: `skill`

A named, parameterized subscript. Called exactly like a built-in command. When invoked, pushes its own execution frame with arguments bound as `$name` variables. When the body exhausts, the frame pops and the caller resumes.

### Syntax

```
skill <Name>(<param>: <type>, ...) {
  <commands>
}
```

### Example

```
skill RefuelAt(station: poi_id) {
  go $station;
  dock;
}

skill StashAndReturn(dest: poi_id, item: item_id) {
  go $dest;
  stash $item;
  go $here;
}
```

### Call site

Skills appear as ordinary commands — no special syntax at the call site:

```
RefuelAt $nearest_station;
StashAndReturn my_base iron;
```

Arguments are positional and cast against the declared types using the same validation path as built-in command args.

### Parameter types

Reuse the existing `DslArgType` set: `poi_id`, `system_id`, `item_id`, `ship_id`, `mission_id`, `module_id`, `recipe_id`, `integer`, `any`.

---

## Construct 2: `override`

A named, argless subscript that fires automatically when a condition becomes true. Checked before each script step. When triggered, pushes its own execution frame ahead of the current script. When the body exhausts, the original script resumes where it left off.

### Syntax

```
override <Name> when <condition> {
  <commands>
}
```

### Example

```
override FuelCheck when FUEL() < 50 {
  go $nearest_station;
  dock;
}

override CargoFull when CARGO_PCT() >= 90 {
  go $nearest_station;
  dock;
  stash;
}
```

Conditions use the existing DSL condition grammar — any expression valid in an `if` or `until` header is valid here.

---

## Execution Semantics

### Stack frames

Both constructs push a new `ExecutionFrame` onto the existing frame stack in `CommandExecutionEngine`. Two new frame kinds are added:

| Kind | Pushed by | Popped when |
|---|---|---|
| `Skill` | Skill call site | Body exhausted |
| `Override` | Condition fires | Body exhausted |

The original frame stack is preserved underneath. When the skill/override frame pops, execution continues from the parent frame's current index — no requeue needed.

### Scoped MINED/STASHED counters

Each `Skill` or `Override` frame carries its own `MinedByItem` and `StashedByItem` counter dictionaries, isolated from the parent script's counters. On frame push, fresh empty counters are allocated. On frame pop, the parent's counters are restored. This means:

- `MINED(iron)` inside a skill reflects only what that skill invocation has mined.
- The parent script's `MINED(iron)` counter is unaffected by anything that happens inside a skill or override.

### Scoped `$` variables

Skill frames carry a binding map of `$param → resolved_value` populated from the call-site arguments. During command building, `$param` tokens are expanded from the frame's binding map before falling through to the global macro table (`$home`, `$here`, `$nearest_station`, etc.).

Override frames have no bindings (they are argless) but still isolate MINED/STASHED.

### Override triggering

Before each script step decision in `DecideScriptStepAsync`, the engine evaluates all registered overrides in declaration order. The first override whose condition is true and whose body is not already on the stack fires. Only one override fires per tick.

**Cooldown:** An override does not re-trigger while its own frame is already executing. Once the frame pops, it becomes eligible again on the next tick.

---

## Where overrides and skills are defined

Skills and overrides live in a **persistent skill library** — a separate file (e.g. `skills.prayer`) independent of any running mission script. They are never declared inline in a mission script.

This means:

- Skills and overrides survive script reloads and LLM regeneration.
- The library is managed through dedicated UI (add, edit, remove, enable/disable), not by editing scripts directly.
- Mission scripts treat skills as built-in commands — the LLM sees them listed in the command reference block and can call them freely without knowing their implementation.
- Overrides are always-on safety behaviors the user controls explicitly, not something the LLM can accidentally clobber.

### Library file format

A single `.prayer` file containing only `skill` and `override` blocks, parsed separately from mission scripts:

```
skill RefuelAt(station: poi_id) {
  go $station;
  dock;
}

override FuelCheck when FUEL() < 50 {
  RefuelAt $nearest_station;
}
```

This file is the **interchange format** — plain text, human-readable, and trivially copy-pasteable to share a full skill library between users or instances.

### AST as working representation

On load, the file is parsed into a `SkillLibrary` AST. All UI operations mutate the AST directly — adding a node, removing one, reordering overrides, editing a body. No string manipulation happens in the UI layer.

After any mutation the AST is serialized back to the file via the pretty-printer (extending the existing `DslPrettyPrinter`), keeping the file in sync. The round-trip is:

```
file (text) → parse → SkillLibrary AST → UI edits → pretty-print → file (text)
```

This means the file is always clean and canonical regardless of how it was edited. A library hand-written in a text editor and one built entirely through the UI are indistinguishable on disk.

### UI — Skills tab

A dedicated **Skills** tab in the main UI surfaces the library alongside the existing script and roleplay tabs. It has two sections: **Skills** and **Overrides**.

#### Adding a skill

The user describes what they want in natural language (e.g. "go to a station, dock, and refuel"). The LLM generates the skill definition — name, typed parameters, and body — using the current command reference and known macros as context. The user reviews, edits if needed, and confirms. The skill is appended to the library file and immediately available to scripts.

#### Adding an override

Same flow: the user describes a condition and desired response (e.g. "when I'm below 50% fuel, go to the nearest station and dock"). The LLM generates the `override` block. The user confirms.

#### Managing the library

- **Edit** — opens the raw skill/override body in an inline editor; changes are validated and saved on confirm
- **Remove** — deletes from library; any running script referencing the skill treats it as an unknown command on next invocation
- **Enable / disable override** — toggles without deleting; disabled overrides are loaded but never trigger
- **Reorder overrides** — drag to set trigger priority

---

## Open Questions

1. **After override body: resume or halt?** — Proposal assumes resume. If the override body ends with `halt;`, the engine halts as normal.
2. **Override ordering** — UI reorder controls priority; declaration order in file is the tiebreak.
3. **Recursive skills** — a skill calling itself should be detected at parse time and rejected.
4. **Override visibility to LLM** — when generating scripts, the prompt should include active overrides so the LLM knows not to duplicate their logic (e.g. no need to write low-fuel handling if an override already covers it).

---

## Implementation Sketch

### New types / changes

- `ExecutionFrameKind`: add `Skill`, `Override`
- `ExecutionFrame`: add `Bindings: Dictionary<string, string>?`, `MinedByItem`, `StashedByItem` (nullable; only allocated for Skill/Override frames)
- `DslAstNode`: add `DslSkillAstNode`, `DslOverrideAstNode`
- `DslAstProgram`: add `Skills`, `Overrides` collections alongside `Statements`
- `DslParser`: parse `skill` and `override` top-level blocks; register skills in a lookup used by `IsCommandAllowed` and `BuildCommandResult`
- `CommandExecutionEngine`: check overrides before each step; push Skill/Override frames; save/restore counters on push/pop
- `ApplyCountersToState`: when inside a Skill/Override frame, apply that frame's counters rather than the root counters
