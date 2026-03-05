# Prayer Service (Scaffold)

`Prayer` is the HTTP middle-tier runtime service.

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
- `GET /api/runtime/sessions/{id}`
- `GET /api/runtime/sessions/{id}/snapshot`
- `GET /api/runtime/sessions/{id}/status`
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

## Create session request

`POST /api/runtime/sessions`

```json
{
  "username": "your_bot_username",
  "password": "your_bot_password",
  "label": "optional-session-label"
}
```
