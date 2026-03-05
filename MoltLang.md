# MoltLang

MoltLang is a small command DSL used by the SpaceMoltLLM agent to produce executable scripts.

## Quick Facts

- Commands are statement-based and must end with `;`.
- Blocks are supported via `repeat { ... }`, `if <FLAG> { ... }`, and `until <FLAG> { ... }`.
- `halt;` is a built-in statement that pauses autonomous execution.
- Only one boolean flag currently exists: `MISSION_COMPLETE`.
- Arguments are tokenized as either identifiers (`foo_bar-1`) or integers (`123`).
- No quoted strings, no comments, and no commas.

## Lexical Rules

- Identifier regex: `[A-Za-z_][A-Za-z0-9_-]*`
- Integer regex: `[0-9]+`
- Keywords are case-insensitive (`repeat`, `if`, `until`, `halt`).
- Command names are normalized to lowercase internally.

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

## Runtime Control-Flow Semantics

- `repeat { ... }`
  - Infinite loop: the block body is replayed forever.
- `if FLAG { ... }`
  - Condition is evaluated when entering the block.
  - If false, runtime skips directly to matching `if` end.
- `until FLAG { ... }`
  - Condition is evaluated on entry and at block end.
  - If true at entry, body is skipped.
  - If false at end, body is replayed.
- `halt;`
  - Sets engine halted state and waits for user input.

## Boolean Flags

- `MISSION_COMPLETE`
  - Evaluates true when there are no active missions OR at least one active mission is marked completed.

## Command Signatures (as parser currently accepts)

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

## Enum-backed Keywords

- Mining target enum (`mine`): `asteroid_belt`, `asteroid`, `gas_cloud`, `ice_field`
- Cargo keyword enum (`sell`/`stash`): `cargo`

## Validation Behavior

- Parse-time checks:
  - Unknown command names are rejected.
  - Missing/extra arguments are rejected according to command grammar.
  - Unknown boolean flags are rejected.
- Parser compatibility quirk:
  - A command token can be accepted in collapsed form (`commandNameArg`) only for commands with exactly one identifier-like argument.
  - Example shape: `goalpha_centauri;` may normalize to `go alpha_centauri;`.
- Runtime fuzzy checks (state-aware):
  - When game state is available, typed args are fuzzy-validated against known systems, POIs, items, and enums.
  - Invalid values can produce "Did you mean ..." errors.

## Important Current Caveat

Several commands require arguments at runtime, but currently declare no DSL arg grammar (`IDslCommandGrammar` not implemented), so the parser accepts only their zero-arg forms:

- `switch_ship`, `install_mod`, `uninstall_mod`, `buy_ship`, `buy_listed_ship`, `commission_quote`, `commission_ship`, `sell_ship`, `list_ship_for_sale`

Practically, these commands may parse but then return runtime usage errors due to missing arguments.

## Examples

```moltlang
repeat {
  survey;
  mine carbon_ore;
  go sol;
  sell cargo;
}
```

```moltlang
until MISSION_COMPLETE {
  mine asteroid_belt;
  go alpha_centauri;
  accept_mission m_123;
}
if MISSION_COMPLETE {
  halt;
}
```
