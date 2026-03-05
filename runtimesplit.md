# Runtime Split Plan Tracker

This file is the working tracker for runtime extraction progress.
Detailed implementation plan remains in `MIDDLE_RUNTIME_SPLIT_PLAN.md`.

## Status (as of 2026-03-05)

- [x] Step 0: Baseline and safety harness
- [x] Step 1: Introduce middle-runtime contracts (no behavior changes)
- [x] Step 2: Add SpaceMolt adapter implementing new contracts
- [x] Step 3: Decouple command contracts from concrete client
- [x] Step 4: Decouple execution engine from concrete client
- [ ] Step 5: Move DSL package boundary into middle runtime
- [ ] Step 6: Extract runtime-state builder from SpaceMolt assembler
- [ ] Step 7: Introduce `IRuntimeStateProvider` and wire runtime to it
- [ ] Step 8: Create middle-runtime host facade
- [ ] Step 9: Move bot session ownership into runtime host
- [ ] Step 10: Enforce UI/runtime bot boundary (`{bot_id, command}` only)

## Step 0 completion evidence

- `docs/runtime-split/baseline-observations.md`
- `docs/runtime-split/manual-regression-checklist.md`
- `docs/runtime-split/smoke-scripts.md`

## Step 1 completion evidence

- `src/MiddleRuntime/Contracts/IRuntimeTransport.cs`
- `src/MiddleRuntime/Contracts/IRuntimeStateProvider.cs`
- `src/MiddleRuntime/Contracts/IRuntimeEngine.cs`
- `src/MiddleRuntime/Contracts/RuntimeCommandResult.cs`
- `src/MiddleRuntime/Contracts/RuntimeExecutionResult.cs`

## Step 2 completion evidence

- `src/Infra/SpaceMolt/SpaceMoltRuntimeTransportAdapter.cs`
- `src/App/Program.cs` (adapter instantiated alongside `SpaceMoltHttpClient`)
- `src/App/BotSession.cs` (stores optional runtime transport for future cutover steps)

## Step 3 completion evidence

- `src/Core/Commands/CommandContracts.cs` (`ISingleTurnCommand`/`IMultiTurnCommand` now accept `IRuntimeTransport`)
- `src/Core/Commands/*` (command implementations migrated from `SpaceMoltHttpClient` to `IRuntimeTransport`)
- `src/Infra/SpaceMolt/SpaceMoltHTTPClient.cs` (implements `IRuntimeTransport` compatibility surface to preserve current wiring)

## Step 4 completion evidence

- `src/Core/Agent/CommandExecutionEngine.cs` (`ExecuteAsync` now accepts `IRuntimeTransport`)
- `src/Core/Agent/Agent.cs` (agent execution path now forwards `IRuntimeTransport` to execution engine)

## Notes

- Current migration stance keeps session/retry/rate-limit handling in `SpaceMoltHttpClient` during early extraction steps to minimize behavioral risk.
- Runtime-layer session ownership can be introduced later as an explicit contract once transport decoupling is complete.
- Step 9 target state: all bot session lifecycle/lookup/mutation lives in runtime; app/UI no longer owns bot session internals.
- Step 10 target state: UI sends runtime commands as `{ bot_id, command }`; bot switching and active-selection state stay entirely in UI.
