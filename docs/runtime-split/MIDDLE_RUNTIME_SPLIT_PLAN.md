# Prayer Runtime Split Plan

Date: 2026-03-05

## Current progress

- Completed: `RuntimeHost` now depends on `IRuntimeTransport` (not concrete `SpaceMoltHttpClient`).
- Completed: runtime-facing rate-limit contract added (`RuntimeRateLimitException`) and mapped from infra adapters.
- Completed: runtime command and execution ownership moved under `src/MiddleRuntime`:
  - `src/MiddleRuntime/Commands/*`
  - `src/MiddleRuntime/Execution/CommandExecutionEngine.cs`
  - `src/MiddleRuntime/Agent/*`
- In progress: `Prayer` HTTP host scaffold added at `src/Prayer`.
- Completed: Prayer now runs real runtime worker sessions and exposes explicit runtime control endpoints (script, generate, execute, halt, loop, status, snapshot).
- In progress: App has switched to Prayer-only runtime command/loop routing and active-bot state polling via Prayer `/state`.
- In progress: Shared API DTO contract project introduced (`src/Prayer.Contracts`) and wired into both App and Prayer for session/command/snapshot payloads.
- Completed: Legacy Prayer `/ui` display payload path removed; App now renders from structured state contract.

## Goal

Extract the current in-process middle runtime into an HTTP service named `Prayer`, with clean boundaries:

- Runtime semantics in `Prayer` / `Prayer.Runtime`
- SpaceMolt transport details in `Prayer.Infra.SpaceMolt`
- UI/app as HTTP client of Prayer

## Scope guardrails (current phase)

- Target deployment is a single trusted operator/service network.
- Do not add authn/authz or tenant isolation work in this phase.
- Focus on runtime extraction, HTTP control surface, and client migration.

## Stage 1: Boundary hardening in current solution

1. Replace `RuntimeHost` dependency on concrete `SpaceMoltHttpClient` with `IRuntimeTransport`.
2. Remove runtime-layer catches/logic that depend on infra-only exception types.
3. Introduce `IRuntimeHost` and use it in app/session state.
4. Move runtime command semantics ownership into runtime layer:
   - Move `src/Core/Commands/*` to runtime-owned location.
   - Move `CommandExecutionEngine` into runtime-owned location.

Exit criteria:
- Runtime orchestration compiles with interface-only transport/state dependencies.
- No direct reference from runtime orchestration to concrete SpaceMolt client.

## Stage 2: Prayer service host scaffold

1. Create `Prayer` HTTP host project.
2. Add runtime session registry (create/start/stop/select sessions).
3. Expose endpoints for:
   - setting/generating/executing script
   - halt/interrupt
   - loop toggle
   - runtime snapshot and status feed
4. Wire runtime to infra adapters through DI.

Exit criteria:
- One runtime session can be controlled end-to-end over HTTP.

## Stage 3: Client migration

1. Update current app/UI to call Prayer endpoints instead of in-process channels/runtime objects.
2. Keep UI features intact while replacing internal runtime dispatch with HTTP calls.
3. Preserve checkpoint, status, and snapshot behavior through API contract.

Exit criteria:
- Existing UI works with Prayer as external runtime service.

## Stage 4: Packaging and operations

1. Split projects cleanly (`Prayer`, `Prayer.Runtime`, `Prayer.Infra.SpaceMolt` as needed).
2. Add dependency checks to enforce boundary direction.
3. Add health/readiness endpoints and basic observability (structured logs + metrics hooks).

Exit criteria:
- Prayer can run independently and serve multiple runtime sessions in a trusted single-operator environment.

## Regression discipline

After each stage, run `docs/runtime-split/manual-regression-checklist.md` using scripts in `docs/runtime-split/smoke-scripts.md`.
