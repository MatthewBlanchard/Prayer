# Prayer Runtime Boundaries

## Target architecture

- `Client/UI` -> `Prayer` (HTTP service) -> `Prayer.Infra.SpaceMolt` -> `SpaceMolt`
- `Prayer` owns runtime orchestration, DSL semantics, command semantics, and execution control-flow.
- `Prayer.Infra.SpaceMolt` owns concrete SpaceMolt HTTP transport/session/retry/rate-limit behavior.
- Concrete `SpaceMoltHttpClient` stays in infra and must not leak into runtime orchestration code.

## Naming and component direction

- Service host name: `Prayer`.
- Runtime library namespace/project direction: `Prayer.Runtime` (or equivalent split of current middle runtime + command execution code).
- Infra adapter namespace/project direction: `Prayer.Infra.SpaceMolt`.

## Ownership rules

### Runtime (`Prayer` / `Prayer.Runtime`)

- Own `RuntimeHost` behavior and runtime session lifecycle.
- Own DSL parse/normalize/interpreter behavior.
- Own command catalog and command execution engine (including multi-turn command semantics like `go` and `mine`).
- Depend only on runtime contracts/interfaces for transport and state access.

### Infra (`Prayer.Infra.SpaceMolt`)

- Own `SpaceMoltHttpClient` and all concrete API payload/endpoint details.
- Own cache/session recovery and rate-limit handling specifics.
- Implement adapters required by runtime contracts.

### Client/App

- Be a client of Prayer HTTP endpoints, not an in-process owner of runtime internals.
- Own user-facing UI concerns, tab/session selection UX, and rendering.

## Immediate hardening tasks

- Replace `RuntimeHost` constructor dependency on `SpaceMoltHttpClient` with `IRuntimeTransport`.
- Remove direct infra exception dependencies from runtime host (e.g., map to runtime-level error contracts/events).
- Move command semantics code from `src/Core/Commands` + execution engine from `src/Core/Agent` into runtime ownership.
- Introduce `IRuntimeHost` and store that interface in app/session state instead of concrete host type.
