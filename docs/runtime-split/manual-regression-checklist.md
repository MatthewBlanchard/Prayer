# Runtime Split Manual Regression Checklist

Use this checklist after each migration step in `docs/runtime-split/MIDDLE_RUNTIME_SPLIT_PLAN.md`.

## Preconditions

- Use the same bot account and similar in-game location where possible.
- Start with a clean `Prayer` service launch.
- Keep `docs/runtime-split/smoke-scripts.md` open and run the scripts in order.
- Use the same external client path each run (existing app UI or direct HTTP calls) for consistent comparisons.

## 0) API contract sanity checks

1. Confirm Prayer health endpoint returns success.
2. Create/select a runtime session via Prayer API.
3. Send a basic runtime command (`set_script` or equivalent).
4. Fetch runtime snapshot/status and verify the update is visible.

Expected:
- Prayer endpoints are reachable and state transitions are reflected through the API.

## 1) DSL parse/normalize checks

1. Load `smoke-01-halt-only`.
2. Confirm script is accepted and runtime enters script mode.
3. Execute one tick and confirm runtime halts with a halt-related status.
4. Load `smoke-02-control-flow`.
5. Confirm parser accepts `if`, `until`, and `halt` without syntax errors.
6. Confirm invalid boolean token still fails (negative check with `if NOT_A_FLAG { halt; }`).

Expected:
- Valid scripts load without parse errors.
- Invalid boolean token produces a parse/format error.

## 2) Checkpoint save/restore checks

1. Load `smoke-03-multiturn-go` and allow at least one step to execute.
2. Stop Prayer process while script state is non-empty.
3. Restart Prayer and reconnect using same runtime identity/session backing.
4. Confirm startup/session restore reports checkpoint restore success.
5. Confirm runtime resumes from prior script context (not reset to empty script).

Expected:
- Checkpoint file is read and restore succeeds.
- If checkpoint is intentionally corrupted, Prayer should fail restore gracefully and start halted.

## 3) Multi-turn continuation checks (`go`, `mine`)

1. Run `smoke-03-multiturn-go`.
2. Observe that `go` may take multiple ticks/actions before completion.
3. Confirm runtime continues active command instead of selecting a new script step mid-route.
4. Run `smoke-04-multiturn-mine`.
5. Confirm `mine` continues across ticks until completion/stop condition.

Expected:
- Active multi-turn command remains active until it reports finished.
- After completion, runtime proceeds to next script step.

## 4) Docked enrichments checks

1. Dock at a station and refresh state.
2. Confirm docked panels/fields populate:
   - storage credits/items
   - market/deals
   - own buy/sell orders
   - shipyard lines/listings
   - available missions
3. Undock and refresh state.
4. Confirm docked-only fields reset to empty/default.

Expected:
- Docked enrichments present only when docked at station.
- Undocked snapshot does not retain stale docked-only values.

## 5) Retry and resilience spot-check

1. During scripted run, force one transient failure (e.g. a command failing due to temporary state).
2. Confirm runtime retries failing script step up to configured limit and then skips.

Expected:
- Retry message includes attempt counter.
- Runtime loop continues after retries exhausted.

## 6) Boundary enforcement spot-check

1. Confirm runtime command execution path does not reference concrete `SpaceMoltHttpClient` directly.
2. Confirm command implementations are owned by runtime layer (not app/UI layer).
3. Confirm infra layer remains responsible for concrete SpaceMolt transport details.

Expected:
- Code ownership matches the boundary document and split plan.
