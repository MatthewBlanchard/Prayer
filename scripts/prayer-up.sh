#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PRAYER_URL="${PRAYER_BASE_URL:-http://localhost:5000}"
PRAYER_LOG="${PRAYER_LOG_PATH:-/tmp/prayer-dev.log}"

cleanup() {
  if [[ -n "${PRAYER_PID:-}" ]] && kill -0 "${PRAYER_PID}" 2>/dev/null; then
    kill "${PRAYER_PID}" 2>/dev/null || true
    wait "${PRAYER_PID}" 2>/dev/null || true
  fi
}

trap cleanup EXIT INT TERM

echo "Starting Prayer on ${PRAYER_URL}..."
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --project "${ROOT_DIR}/src/Prayer/Prayer.csproj" --urls "${PRAYER_URL}" >"${PRAYER_LOG}" 2>&1 &
PRAYER_PID=$!

echo "Prayer PID: ${PRAYER_PID}"
echo "Logs: ${PRAYER_LOG}"
echo "Waiting for Prayer health endpoint..."

if command -v curl >/dev/null 2>&1; then
  for _ in $(seq 1 60); do
    if curl -fsS "${PRAYER_URL}/health" >/dev/null; then
      echo "Prayer is healthy at ${PRAYER_URL}"
      break
    fi
    sleep 0.5
  done
fi

echo "Prayer running. Press Ctrl+C to stop."
wait "${PRAYER_PID}"
