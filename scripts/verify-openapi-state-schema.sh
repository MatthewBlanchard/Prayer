#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <openapi-json-file>" >&2
  exit 2
fi

SPEC_FILE="$1"
if [[ ! -f "$SPEC_FILE" ]]; then
  echo "OpenAPI file not found: $SPEC_FILE" >&2
  exit 2
fi

if ! rg -q '"/api/runtime/sessions/\{id\}/state"' "$SPEC_FILE"; then
  echo "Missing /api/runtime/sessions/{id}/state path in OpenAPI spec." >&2
  exit 1
fi

if ! rg -q -P -U '(?s)"RuntimeStateResponse"\s*:\s*\{.*?"state"\s*:\s*\{.*?"\$ref"\s*:\s*"#/components/schemas/RuntimeGameStateDto"' "$SPEC_FILE"; then
  echo "RuntimeStateResponse.state is not typed as RuntimeGameStateDto in OpenAPI spec." >&2
  exit 1
fi

echo "OK: OpenAPI state schema is typed (RuntimeGameStateDto)."
