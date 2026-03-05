## MoltLang™ (SpaceMoltLLM DSL)

## Thesis
Agentic bot control works best when we steer models toward what they already reason well about: syntax and code.

Instead of forcing turn-by-turn action picking, we shift to long-horizon planning and hide the dirty implementation details inside the DSL/runtime layer.

That is how we go from natural language -> successful behavior.

MoltLang™ is a compact scripting language for the SpaceMoltLLM agent.  
It’s built for autonomous behavior and control flow, not direct endpoint mirroring.

## TL;DR
- Commands are statement-based and must end with `;`
- Blocks: `repeat { ... }`, `if <FLAG> { ... }`, `until <FLAG> { ... }`
- `halt;` pauses autonomous execution
- Current boolean flag: `MISSION_COMPLETE`
- Tokens are identifiers (`foo_bar-1`) or integers (`123`)
- No quoted strings, comments, or commas

## Not 1:1 with POST Requests
MoltLang™ commands represent intent, not single API calls.

Examples:
- `go sol;` can pathfind and run multiple movement calls
- `sell cargo;` can run many sell operations
- `mine carbon_ore;` may search POIs/systems and run multi-step actions
- Station commands may auto-dock first

So MoltLang™ is an orchestration layer above the HTTP API, not a thin command-to-POST wrapper.

## How We Use RAG Examples
- We keep a prompt->script example store and use retrieval to pull the best matches for new user requests.
- The system embeds the incoming prompt and stored prompts, scores similarity, and selects top examples (usually up to 5).
- Those examples are inserted directly into the script-generation prompt so the model sees concrete MoltLang™ patterns.
- After successful generations, we can save prompt->script pairs back into the store so the example set improves over time.

## Fuzzy Finding
- MoltLang™ does fuzzy matching on arguments like systems, POIs, items, and enum-like keywords.
- If the model/user gives a near miss (spacing, underscores, slight typo), we do not coerce or auto-remap it.
- Instead, we fail clearly and return a “Did you mean ...” suggestion with the closest valid value.
- This keeps behavior deterministic while still giving useful recovery guidance to the user/agent.

## Where This Is Going
- We plan to add richer control-flow primitives over time so scripts can express more nuanced long-horizon strategies without getting verbose.
- We also plan to add non-mutating state-inspection functions in a form like `function(arg, arg1)`.
- These functions would only read state (never execute game actions) and would be usable inside conditions like `if` and `until`.
- The goal is more expressive planning logic while keeping action execution safe and predictable.

## Lexical Rules
- Identifier: `[A-Za-z_][A-Za-z0-9_-]*`
- Integer: `[0-9]+`
- Keywords are case-insensitive: `repeat`, `if`, `until`, `halt`
- Command names are normalized to lowercase internally

## Grammar (EBNF-style)
```ebnf
script      := ws statement* ws
statement   := command_stmt | repeat_stmt | if_stmt | until_stmt | halt_stmt

command_stmt:= identifier (ws (identifier | integer))* ws ';'
repeat_stmt := 'repeat' ws '{' ws statement* ws '}'
if_stmt     := 'if' ws identifier ws '{' ws statement* ws '}'
until_stmt  := 'until' ws identifier ws '{' ws statement* ws '}'
halt_stmt   := 'halt' ws ';'

ws          := (' ' | '\t' | '\r' | '\n')*
```

## Runtime Semantics
- `repeat { ... }`: infinite replay of block body
- `if FLAG { ... }`: evaluate on entry; skip block if false
- `until FLAG { ... }`: evaluate on entry and block end; true on entry skips, false on end replays
- `halt;`: set halted state and wait for user input

## Boolean Flags
- `MISSION_COMPLETE`: true when there are no active missions, or at least one active mission is marked completed

## Command Signatures
- `abandon_mission <id>;`
- `accept_mission <id>;`
- `buy <item> <count>;`
- `cancel_buy <item>;`
- `cancel_sell <item>;`
- `go <destination>;`
- `mine <target_or_resource?>;`
- `retrieve <item> <count?>;`
- `sell <item_or_cargo?>;` (defaults to `cargo`)
- `stash <item_or_cargo>;`
- `ship_catalog;`
- `dock;`
- `survey;`
- `repair;`
- `commission_status;`
- `next;`
- `last;`
- `exit;`
- `switch_ship;`
- `install_mod;`
- `uninstall_mod;`
- `buy_ship;`
- `buy_listed_ship;`
- `commission_quote;`
- `commission_ship;`
- `sell_ship;`
- `list_ship_for_sale;`
- `halt;`

## Enum Keywords
- `mine`: `asteroid_belt`, `asteroid`, `gas_cloud`, `ice_field`
- `sell` / `stash`: `cargo`

## Example Scripts (from JSON example bank)
Prompt:
```text
- mine_resource: Mine 30 units of Iron Ore (0 / 30)
- finish_quest: Go to Central Nexus
```
Script:
```moltlang™
until MISSION_COMPLETE {
  mine iron_ore;
  go the_core;
  sell cargo;
}
```

Prompt:
```text
jump horizon then dock then jump nexus
```
Script:
```moltlang™
go horizon;
dock;
go nexus;
```

Prompt:
```text
buy 9 iron
```
Script:
```moltlang™
buy iron_ore 9;
```
