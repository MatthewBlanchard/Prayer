# Architecture Review Status (validated 2026-03-05)

### Medium: `GameState` is both DTO and rendering/domain logic
**Status:** Still true.

`GameState` includes render/display formatting methods in addition to transport fields. This couples payload shape with presentation and makes external client portability less clear.

**Current refs**
- `src/Prayer/Core/State/GameStateRendering.cs:5`
- `src/Prayer/Core/State/GameStateRendering.cs:111`
- `src/Prayer/Core/State/GameStateRendering.cs:229`

### Medium: flat/ambiguous top-level shape
**Status:** Improved (partially resolved).

Active-ship scalars were moved under `GameState.Ship` / `RuntimeGameStateDto.Ship`, which removed the largest flat cluster (`Fuel/MaxFuel`, `Hull/MaxHull`, `Shield/MaxShield`, `CargoUsed/CargoCapacity`, etc.) from the top level.

Top-level naming ambiguity (`System` vs `Systems`) still exists and can be addressed in a separate rename pass.

**Current refs**
- `src/Prayer.Shared/GameStateModels.cs:12`
- `src/Prayer.Shared/GameStateModels.cs:16`
- `src/Prayer.Shared/GameStateModels.cs:22`
- `src/Prayer.Shared/GameStateModels.cs:63`

### Medium: weakly typed semantic fields
**Status:** Still true.

Several fields that look like enums/timestamps/structured payloads remain plain strings/ints (`AcceptedAt`, `Type`, `Online`, `PayloadJson`).

**Current refs**
- `src/Prayer.Shared/GameStateModels.cs:231`
- `src/Prayer.Shared/GameStateModels.cs:239`
- `src/Prayer.Shared/GameStateModels.cs:273`
- `src/Prayer.Shared/GameStateModels.cs:292`
