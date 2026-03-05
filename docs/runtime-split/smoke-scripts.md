# Runtime Split Smoke Scripts

Use these scripts for quick baseline checks. Replace placeholders like `<SYSTEM_ID>` and `<POI_ID>` with valid values in your map.

Execution path for migration validation:
- Load and execute these scripts through `Prayer` runtime endpoints.
- Observe runtime state/status via Prayer API (or via app UI if it is acting as a Prayer client).

## smoke-01-halt-only

```txt
halt;
```

Purpose: verifies minimal parser + halt execution path.

## smoke-02-control-flow

```txt
repeat {
  if MISSION_COMPLETE {
    halt;
  }
  until MISSION_COMPLETE {
    go <SYSTEM_ID>;
  }
}
```

Purpose: verifies `repeat`, `if`, `until`, and `halt` parse/normalize support in one script.

## smoke-03-multiturn-go

```txt
go <SYSTEM_ID>;
halt;
```

Purpose: verifies multi-tick continuation for `go` and proper post-completion step progression.

## smoke-04-multiturn-mine

```txt
mine;
halt;
```

Alternative variants:

```txt
mine asteroid_belt;
halt;
```

```txt
mine <RESOURCE_ID>;
halt;
```

Purpose: verifies multi-turn mining continuation and completion/stop messaging.

## smoke-05-docked-enrichment-cycle

```txt
go <STATION_POI_ID>;
dock;
halt;
```

Purpose: positions bot for docked enrichment verification in state/UI snapshot.

## smoke-06-checkpoint-resume

```txt
repeat {
  go <SYSTEM_ID>;
  mine;
}
```

Purpose: run briefly, restart app, and verify checkpoint restore resumes script context.
