# Prayer Runtime Boundaries

## Target architecture

- `Client/UI` -> `Prayer` (HTTP service) -> `SpaceMolt`
- `Prayer` owns runtime orchestration, DSL semantics, command semantics, execution control-flow, and SpaceMolt adapters in this phase.
- Concrete `SpaceMoltHttpClient` stays in infra and must not leak into runtime orchestration code.

## Deployment assumption (current)

- Prayer is deployed for a single trusted operator environment.
- Multi-user auth, tenant isolation, and public-internet hardening are out of scope for this phase.
- Multiple runtime sessions are still supported, but they are not treated as separate security tenants.
- App runtime control path is Prayer-only (`PRAYER_BASE_URL` optional; defaults to `http://localhost:5000/`); no local runtime fallback path.

## Naming and component direction

- Service host name: `Prayer`.
- Runtime and infra implementation currently live in the `Prayer` project.
- `Prayer.Runtime` / `Prayer.Infra.SpaceMolt` split remains optional future work.
- Shared API contract namespace/project direction: `Prayer.Contracts` (DTO-only, no runtime engine internals).

## Ownership rules

### Runtime (`Prayer`)

- Own `RuntimeHost` behavior and runtime session lifecycle.
- Own DSL parse/normalize/interpreter behavior.
- Own command catalog and command execution engine (including multi-turn command semantics like `go` and `mine`).
- Depend only on runtime contracts/interfaces for transport and state access.

### Infra (inside `Prayer` for now)

- Own `SpaceMoltHttpClient` and all concrete API payload/endpoint details.
- Own cache/session recovery and rate-limit handling specifics.
- Implement adapters required by runtime contracts.

### Client/App

- Be a client of Prayer HTTP endpoints, not an in-process owner of runtime internals.
- Own user-facing UI concerns, tab/session selection UX, and rendering.
- Render UI from Prayer structured runtime state payloads (`/state`), not server-rendered markdown payloads.

## Immediate hardening tasks

- Replace `RuntimeHost` constructor dependency on `SpaceMoltHttpClient` with `IRuntimeTransport`.
- Remove direct infra exception dependencies from runtime host (e.g., map to runtime-level error contracts/events).
- Keep command semantics and execution engine under `src/Prayer/MiddleRuntime/*`.
- Introduce `IRuntimeHost` and store that interface in app/session state instead of concrete host type.
