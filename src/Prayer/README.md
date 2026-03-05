# Prayer Service (Scaffold)

`Prayer` is the HTTP middle-tier runtime service.
Its external DTO contracts are moving into `src/Prayer.Contracts`.

## Deployment model (current)

- Single trusted operator environment.
- No authentication/authorization layer yet.
- No tenant isolation guarantees; treat access to Prayer as trusted access.

## Run

```bash
dotnet run --project src/Prayer/Prayer.csproj
```

## Current scaffold endpoints

- `GET /health`
- `GET /api/runtime/sessions`
- `POST /api/runtime/sessions`
- `POST /api/runtime/sessions/register`
- `GET /api/runtime/sessions/{id}`
- `GET /api/runtime/sessions/{id}/snapshot`
- `GET /api/runtime/sessions/{id}/status`
- `GET /api/runtime/sessions/{id}/state`
- `POST /api/runtime/sessions/{id}/script`
- `POST /api/runtime/sessions/{id}/script/generate`
- `POST /api/runtime/sessions/{id}/script/execute`
- `POST /api/runtime/sessions/{id}/halt`
- `POST /api/runtime/sessions/{id}/save-example`
- `PUT /api/runtime/sessions/{id}/loop`
- `POST /api/runtime/sessions/{id}/commands`

Current implementation executes real runtime sessions backed by:

- `SpaceMoltHttpClient` login/session transport
- `SpaceMoltAgent` + `RuntimeHost` worker loop
- Runtime command queues (`set_script`, `generate_script`, `execute_script`, `halt`, `save_example`, `loop_on`, `loop_off`)

The app now consumes Prayer as its runtime control plane (`PRAYER_BASE_URL` required).

## Create session request

`POST /api/runtime/sessions`

```json
{
  "username": "your_bot_username",
  "password": "your_bot_password",
  "label": "optional-session-label"
}
```

## Register session request

`POST /api/runtime/sessions/register`

```json
{
  "username": "new_bot_username",
  "empire": "your_empire",
  "registrationCode": "registration_code",
  "label": "optional-session-label"
}
```
