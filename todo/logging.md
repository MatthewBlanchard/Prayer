# Single-Writer Logging Proposal

## Goal
Eliminate file contention and duplicate/failed appends by routing all log writes through one in-process writer task.

## Design
1. Introduce `ILogSink` with `Enqueue(LogEvent evt)`.
2. Back implementation with `Channel<LogEvent>` (bounded).
3. Start one background `LogWriterService` task at app startup.
4. `LogWriterService` is the only code that opens/writes log files.
5. Existing `SpaceMoltHttpLogging.*` methods only create `LogEvent` and enqueue.

## Core Components
1. `LogEvent`
   - `DateTime TimestampUtc`
   - `LogKind Kind` (`BadRequest`, `Pathfind`, `ApiResponse`, etc.)
   - `string Message`
   - `string FilePath` (resolved from `AppPaths`)
2. `Channel<LogEvent>`
   - Bounded capacity (e.g. 5,000)
   - `FullMode = DropOldest` or `Wait` (choose based on reliability vs latency)
3. `LogWriterService`
   - Reads channel in loop
   - Appends with a persistent `StreamWriter` per file path
   - Flush policy: timed (e.g. every 250ms) and on shutdown
   - Handles IO exceptions with retry/backoff
4. `LoggingFacade` (`SpaceMoltHttpLogging`)
   - Formats entries
   - Calls `TryWrite` or `WriteAsync` to channel
   - No direct file IO

## Behavioral Policies
1. Backpressure
   - Prefer `DropOldest` for non-critical logs and emit dropped-count metric.
2. Shutdown
   - Complete channel, drain remaining events, flush and close writers.
3. Failure
   - Writer catches exceptions, retries, and emits internal health status.

## Benefits
1. No competing append operations.
2. Deterministic write ordering per process.
3. Centralized retry, buffering, and flush control.
4. Easier metrics/observability (`queued`, `dropped`, `write_failures`).

## Implementation Plan
1. Add `LogEvent`, `LogKind`, `ILogSink`, and `ChannelLogSink`.
2. Add `LogWriterService` hosted lifecycle (start/stop).
3. Refactor `SpaceMoltHttpLogging` methods to enqueue only.
4. Remove `AppendSafeAsync` and all direct `File.AppendAllTextAsync` calls.
5. Add counters/health logging.
6. Test:
   - high-concurrency stress
   - forced IO exceptions
   - graceful shutdown drain

## Open Decisions
1. Channel full mode (`Wait` vs `DropOldest`).
2. Flush cadence and max in-memory queue size.
3. Whether to rotate files in writer or defer to external tooling.
