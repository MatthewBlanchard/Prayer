# Runtime Split Baseline Observations

Date captured: 2026-03-05
Code baseline: current `main` workspace state before middle-runtime extraction.

## DSL parse and normalize behavior (`if`, `until`, `halt`)

- Parser accepts commands in the form `command [arg1] [arg2];`.
- Block forms currently supported:
  - `if <BOOLEAN_FLAG> { ... }`
  - `until <BOOLEAN_FLAG> { ... }`
- `halt;` is a first-class DSL command and is accepted by parser validation.
- Boolean flags currently validated against a fixed token set; observed token: `MISSION_COMPLETE`.
- Parse validation throws `FormatException` for invalid syntax, unknown commands, or invalid block conditions.
- `CommandExecutionEngine.SetScript(...)` parses to AST, translates to normalized command steps, and rewrites script through renderer.
- Normalization behavior:
  - command names are canonicalized through command syntax mapping
  - default args may be injected where syntax defines defaults
  - rendered normalized script is persisted as the execution script
- AST walker semantics in runtime:
  - `if` executes body when condition is true; if condition is unknown at runtime, body is entered
  - `until` executes body until condition becomes true; if condition is unknown at runtime, body is entered

## Checkpoint save/restore behavior

- Checkpoint writes are best-effort and non-fatal on failure.
- `AgentCheckpointStore.Save(...)` writes JSON via temp file + atomic move (`.tmp` then overwrite move).
- Checkpoint payload includes:
  - script text
  - halted state
  - current script line
  - active command marker + active command step snapshot
  - action memory queue
  - requeued steps
  - execution frame stack (kind/path/index/condition metadata)
- Execution memory is capped (latest 12 entries retained by runtime).
- Startup restore path (`Program.CreateBotSessionAsync`):
  - load checkpoint file
  - fetch current game state
  - attempt `agent.TryRestoreCheckpoint(...)`
  - on success, runtime resumes from persisted state and reports restore success
  - on failure/missing checkpoint, runtime starts halted (`Awaiting script input`)
- Restore behavior for active multi-turn command:
  - runtime does not restore command object instance
  - if checkpoint says there was an active command, the saved command step is requeued at the front

## Multi-turn command continuation behavior (`go`, `mine`)

- Runtime model (`CommandExecutionEngine`):
  - on first execution, `IMultiTurnCommand.StartAsync(...)` is called
  - command is kept as active command
  - each subsequent tick calls `ContinueAsync(...)`
  - command is considered complete only when `finished == true`
  - memory entry for the step is recorded only when command finishes
- `go` command observed behavior:
  - resolves target against local state first, then map snapshot
  - may issue multiple `undock`, `jump`, and `travel` actions across ticks
  - continues returning unfinished state while traversing route
  - completion messages include arrived/already-at/invalid-target/no-route outcomes
- `mine` command observed behavior:
  - supports local mining, typed mining target, and resource-search mode
  - may navigate/undock across multiple ticks before mining
  - continues until stop/completion condition (`cargo full`, depletion/stop reason, or explicit completion)

## Docked enrichments behavior

When docked at a station (`SpaceMoltGameStateAssembler.BuildAsync`):

- Fetches and merges station enrichments:
  - `view_storage`
  - `view_market`
  - `analyze_market` (logged)
  - `view_orders`
  - `shipyard_showroom` and `browse_ships` (if shipyard cache missing)
- Applies/uses station and catalogue cache:
  - station storage/market/orders/shipyard lines cached in memory
  - market and shipyard cache persisted to disk
  - ship catalogue page built from cached full catalogue when cache is fresh
- Mission enrichments:
  - always fetches active missions (`get_active_missions`)
  - when docked at station also fetches available missions (`get_missions`)
- Additional state enrichments always performed:
  - owned ships (`list_ships`)
  - notifications/chat snapshots from client buffers

When not docked at a station, docked-only fields are reset to empty/default values.
