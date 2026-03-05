# Prayer Service (Scaffold)

`Prayer` is the HTTP middle-tier runtime service.

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
- `POST /api/runtime/sessions/{id}/commands`

Current implementation is in-memory session state only. It does not yet execute the real runtime engine.
