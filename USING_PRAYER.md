# Using Prayer: Building a Client

This guide is the quick practical rundown for building a client on top of Prayer.

## Mental model

Your client does not talk to SpaceMolt directly.

It talks to Prayer:

`Client -> Prayer -> SpaceMolt`

Prayer handles session lifecycle, runtime execution, and state snapshots.  
Your client handles UX, user input, and rendering.

## 1. Connect to Prayer

Set your Prayer base URL (default local):

- `http://localhost:5000/`

Health check:

- `GET /health`

## 2. Bootstrap client startup

At startup, fetch:

- `GET /api/llm/catalog`
- `GET /api/preferences/llm`
- `GET /api/preferences/bots`

Use these to prefill your client UI (model selection, saved bots, etc).

## 3. Create or register a runtime session

Login flow:

- `POST /api/runtime/sessions`

Register flow:

- `POST /api/runtime/sessions/register`

Keep the returned `sessionId` in your client state. This is your handle for all runtime endpoints.

## 4. Apply runtime config

If you support model switching:

- `PUT /api/runtime/sessions/{id}/llm`

If you want to persist default selection:

- `PUT /api/preferences/llm`

If you save bot credentials:

- `PUT /api/preferences/bots`

## 5. Drive the control loop

You have two common options:

- High-level command endpoint: `POST /api/runtime/sessions/{id}/commands`
- Explicit script endpoints:
  - `POST /api/runtime/sessions/{id}/script`
  - `POST /api/runtime/sessions/{id}/script/generate`
  - `POST /api/runtime/sessions/{id}/script/execute`
  - `POST /api/runtime/sessions/{id}/halt`
  - `POST /api/runtime/sessions/{id}/save-example`

## 6. Poll state for UI

Prayer is designed for polling clients.

Primary endpoint:

- `GET /api/runtime/sessions/{id}/state`

Long-poll variant (recommended):

- `GET /api/runtime/sessions/{id}/state?since=<version>&wait_ms=<timeout>`

Behavior:

- `200` with state when version has advanced
- `204` when timeout expires with no change
- `X-Prayer-State-Version` response header contains current version
- Active ship data is nested at `state.ship` (for example `state.ship.fuel`, `state.ship.cargoUsed`)

Recommended loop shape:

- Keep one long-poll loop per `sessionId`
- Re-issue the request immediately after each response
- Track `X-Prayer-State-Version` and pass it back as `since`
- Keep command handling on a separate worker/task so controls stay responsive

Also useful:

- `GET /api/runtime/sessions/{id}/snapshot` (compact runtime summary)
- `GET /api/runtime/sessions/{id}/status` (status line history)

Typical polling cadence:

- Long-poll wait of ~500ms to 1500ms is a good baseline
- Lower values increase responsiveness but also request volume

## 7. Observe performance and API behavior

For SpaceMolt call stats:

- `GET /api/runtime/sessions/{id}/spacemolt/stats`

Useful server logs:

- `log/spacemolt_api.log`
- `log/spacemolt_api_stats.log`

## 8. Clean up sessions

When your client removes a bot/session:

- `DELETE /api/runtime/sessions/{id}`

## Suggested client architecture

- Keep a local store keyed by `sessionId`.
- Run one state-poll worker per session.
- Keep command dispatch separate from state polling.
- Treat Prayer as source of truth for runtime state.
- Surface Prayer error bodies directly to users/dev logs.
- Make model/session changes explicit in UI status messages.

## Minimal flow example (API sequence)

1. `GET /health`
2. `GET /api/llm/catalog`
3. `POST /api/runtime/sessions`
4. `PUT /api/runtime/sessions/{id}/llm` (optional)
5. `POST /api/runtime/sessions/{id}/script/generate`
6. `POST /api/runtime/sessions/{id}/script/execute`
7. `GET /api/runtime/sessions/{id}/state` (poll)
8. `POST /api/runtime/sessions/{id}/halt` (as needed)
9. `DELETE /api/runtime/sessions/{id}` (cleanup)

## Scope notes

Current deployment scope is trusted/internal.  
Prayer currently does not enforce authn/authz or tenant isolation.
