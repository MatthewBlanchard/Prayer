# Explore Command — SpaceMolt Integration Proposal

## 1. Goal

Add a first-class DSL command, `explore`, that:

- Finds the nearest unexplored system.
- Enters that system.
- Visits unexplored POIs in that system.
- Captures resource intel while traversing POIs.
- Repeats until the target system is fully explored, then completes.

This gives the agent a deterministic “map intel” primitive instead of forcing brittle `go`/`survey`/manual POI loops.

---

## 2. DSL and Runtime Surface

### 2.1 New Command: `ExploreCommand`

**File:** `src/Prayer/MiddleRuntime/Commands/ExploreCommand.cs`

```txt
explore;
```

- Type: `IMultiTurnCommand`
- Availability: `!string.IsNullOrWhiteSpace(state.System)`
- Help text:
  - `- explore → go to nearest unexplored system, visit unexplored POIs, collect resource intel`

### 2.2 Register in Command Catalog

**File:** `src/Prayer/MiddleRuntime/Commands/CommandCatalog.cs`

Add:

```csharp
new ExploreCommand(),
```

### 2.3 Prompt/DSL Reference

No argument override needed (no args), but add examples in prompt reference generation so model uses it for discovery tasks:

```txt
- explore;    // explore nearest unexplored system and its POIs
```

---

## 3. Behavioral Spec

### 3.1 “Unexplored” Definition

A system is explored when:

- The ship has been in that system at least once, and
- All currently known POIs in that system have been visited at least once during exploration.

A POI is explored when:

- `CurrentPOI.Id == targetPoiId` was observed in state while command was active.

### 3.2 High-Level Flow

On `StartAsync`:

1. Load persisted exploration memory.
2. If current system has unexplored POIs, set it as active system.
3. Else find nearest unexplored system by BFS distance from `state.System`.
4. If none found, return: `Exploration complete: no unexplored systems/POIs found.`

On each `ContinueAsync` tick:

1. If docked, `undock`.
2. If not in active system, `jump` one hop toward it (`FindRouteAsync` pattern from `GoCommand`/`MineCommand`).
3. If in active system, pick nearest unexplored POI in-system and `travel`.
4. After arriving at a new POI:
   - Mark POI explored.
   - Resource intel is captured by existing `get_system`/`get_poi` state assembly and `GalaxyStateHub.MergeResourceLocations`.
5. Once all POIs in active system are explored:
   - Optionally call `survey_system` once for that system (best-effort) to improve hidden resource discovery.
   - Mark system explored and finish command.

### 3.3 Safety/Exit Conditions

- If route is impossible, mark candidate as unreachable and choose next candidate.
- If fuel is below configurable reserve (for example `<= 1`), stop gracefully:
  - `Exploration paused: low fuel.`
- Any non-recoverable API error returns command failure result message.

---

## 4. State and Persistence

### 4.1 New Persistent Exploration Snapshot

Add lightweight persisted memory so exploration progress survives script swaps/restarts.

**New file:** `src/Prayer.Shared/ExplorationStateModels.cs`

```csharp
public sealed class ExplorationStateSnapshot
{
    public HashSet<string> ExploredSystems { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> ExploredPois { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> UnreachableSystems { get; set; } = new(StringComparer.Ordinal);
}
```

### 4.2 Storage Location

Add app path in `AppPaths`:

- `cache/exploration_state.json`

Implement repository helper similar to existing cache helpers:

- Load on command start.
- Save when marking system/POI explored or unreachable.

---

## 5. Implementation Plan

### Phase 1: Command + Pathing

- Implement `ExploreCommand` navigation loop using existing route-hop logic.
- Register command in `CommandCatalog`.
- Add DSL prompt example.

### Phase 2: Exploration Memory

- Add exploration snapshot model + cache load/save utilities.
- Wire `ExploreCommand` to persisted state.

### Phase 3: Intel Enrichment

- Add optional `survey_system` once per newly completed system.
- Emit clear result messages (`exploring <system>`, `visited <poi>`, `system complete`).

---

## 6. Testing Plan

### Unit Tests

- Nearest unexplored system selection prefers minimum BFS distance.
- In-system traversal marks POIs explored and finishes when complete.
- Route failure marks system unreachable and continues.
- Persisted snapshot reload restores progress.

### Runtime/Integration Checks

- Run `explore;` from a fresh cache: verify movement across systems and POIs.
- Confirm `Galaxy.Resources.PoisByResource` and `SystemsByResource` grow after exploration.
- Validate graceful stop on low fuel and resume after refuel.

---

## 7. Non-Goals (Initial Version)

- Full galaxy completion in a single command invocation.
- Risk-aware path scoring (hostile systems, PvP risk).
- Multi-ship cooperative exploration.

These can be added after validating core behavior and stability.
