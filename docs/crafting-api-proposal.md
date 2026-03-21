# Crafting API — SpaceMolt Integration Proposal

## 1. The SpaceMolt Crafting API

### Endpoints

**`POST /craft`** — Execute a crafting action (mutation, 1 per tick / 10s)

```json
// Request payload
{ "type": "craft", "payload": { "recipe_id": "module_shield_basic", "quantity": 1 } }

// CraftResponse
{
  "action":      "craft",
  "recipe_id":   "module_shield_basic",
  "output_id":   "module_shield_basic",
  "output_name": "Basic Shield Module",
  "quantity":    1,
  "message":     "You crafted 1x Basic Shield Module."
}
```

**`POST /catalog`** with `type=recipes` — Browse available recipes (read-only)

```json
// Request payload
{ "type": "catalog", "payload": { "type": "recipes", "category": "Modules", "page": 1, "page_size": 20 } }
```

### Constraints

- Player must be **docked at a base that has a crafting service**.
- Materials are pulled from **cargo first**, then **station storage**.
- If cargo is full when the craft completes, output items **overflow to station storage**.
- `quantity` range: 1–10 (batch crafting saves actions; each batch still costs one tick).
- Output **quality depends on the player's Crafting skill level**.
- Ships expose a `passive_recipes` field — recipe IDs that are auto-processed from cargo each tick (not relevant to this DSL command).

---

## 2. Prayer DSL — Proposed Changes

### 2.1 New Command: `CraftCommand`

**File:** `src/Prayer/MiddleRuntime/Commands/CraftCommand.cs`

```
craft <recipe_id> <quantity?>;
```

Follows the same pattern as `RepairCommand` (no-arg, docked only) and `BuyCommand` (item + count args):

- Extends `AutoDockSingleTurnCommand` — auto-docks at the nearest station before executing.
- `RequiresStation = true`
- `IsAvailableWhenDocked`: returns `true` when docked and `AvailableRecipes` is non-empty.
- `ExecuteDockedAsync`: calls `client.ExecuteCommandAsync("craft", new { recipe_id = cmd.Arg1, quantity = cmd.Quantity ?? 1 })`.
- DSL syntax:
  ```csharp
  new DslCommandSyntax(ArgSpecs: new[]
  {
      new DslArgumentSpec(DslArgKind.Item, Required: true),          // recipe_id
      new DslArgumentSpec(DslArgKind.Integer, Required: false, DefaultValue: "1"),  // quantity
  })
  ```

### 2.2 Register in `CommandCatalog`

**File:** `src/Prayer/MiddleRuntime/Commands/CommandCatalog.cs`

Add `new CraftCommand()` to `CommandCatalog.All`.

### 2.3 DSL Parser — Prompt Arg Name Override

**File:** `src/Prayer/MiddleRuntime/DSL/DSL.cs` — `PromptArgNameOverrides` dictionary

```csharp
["craft"] = new[] { "recipe_id", "count" },
```

This makes the LLM reference block render `craft <recipe_id> <count?>;` instead of the generic `item`/`count` names.

### 2.4 DSL Examples for Agent Prompt

Add to `BuildPromptDslReferenceBlock()`:

```
- craft module_shield_basic;           // craft 1
- craft module_shield_basic 5;         // batch craft 5
```

---

## 3. State Model — Proposed Changes

### 3.1 Fetch Recipe Catalog During State Assembly

**File:** `src/Prayer/Infra/SpaceMolt/SpaceMoltGameStateAssembler.cs`

Inside the docked-station branch of `BuildAsync`, after the market and storage calls, add a recipe catalog fetch (best-effort, cached):

```csharp
// Fetch and cache recipe catalog when docked
var recipeCatalog = await _owner.CatalogService.GetFullRecipeCatalogByIdAsync(forceRefresh: false);
state.AvailableRecipes = recipeCatalog.Values.ToArray();
```

Only fetch when `state.CurrentPOI.HasCraftingService` (once the API exposes this flag; until then, fetch unconditionally and tolerate an empty result).

### 3.2 `SpaceMoltCatalogService` — Recipe Catalog Support

**File:** `src/Prayer/Infra/SpaceMolt/SpaceMoltCatalogService.cs`

- Add `const string FullRecipeCatalogueCacheFileKey = "recipes_full_catalog";`
- Add `const string RecipeCatalogByIdCacheFile` path in `AppPaths`
- Add `GetFullRecipeCatalogByIdAsync(bool forceRefresh)` — same pattern as items/ships
- Add the fetch to `EnsureFreshCataloguesAsync()`

### 3.3 `GameState` — New Property

**File:** `src/Prayer/Core/State/GameState.cs` (or wherever `GameState` is defined as a partial)

```csharp
public CatalogueEntry[] AvailableRecipes { get; set; } = Array.Empty<CatalogueEntry>();
```

### 3.4 `RuntimeStateBuilder` — Extended Docked Apply

**File:** `src/Prayer/MiddleRuntime/State/RuntimeStateBuilder.cs`

Extend `ApplyDockedStationState` signature to accept `CatalogueEntry[] availableRecipes` and assign it to state. Keeps all docked data flowing through one method.

## 4. Crowbar — Proposed Changes

Crowbar is the HTMX UI shell (`examples/Crowbar/`) that consumes Prayer's HTTP API and renders live state. It mirrors the same `GameState` structure through `AppUiStateBuilder`.

### 4.1 `CraftingUiModel` and `CraftingUiRecipe`

**File:** `examples/Crowbar/App/AppUiModels.cs`

```csharp
public sealed record CraftingUiRecipe(
    string Id,
    string Name,
    string Category,
    int? Tier,
    string MaterialsSummary,   // "iron_ore x5, copper_wire x2"
    string OutputSummary,      // "module_shield_basic x1"
    bool CanAfford);           // true if all materials are available in cargo+storage

public sealed record CraftingUiModel(
    string StationId,
    int CargoUsed,
    int CargoCapacity,
    CraftingUiRecipe[] Recipes);
```

### 4.2 `AppUiStateBuilder.BuildUiState` — Add Crafting

**File:** `examples/Crowbar/App/AppUiStateBuilder.cs`

Extend `BuildUiState` return tuple:

```csharp
public static (
    SpaceUiModel SpaceModel,
    TradeUiModel? TradeModel,
    ShipyardUiModel? ShipyardModel,
    CatalogUiModel? CatalogModel,
    CraftingUiModel? CraftingModel)       // NEW
    BuildUiState(GameState state)
```

Add `BuildCraftingModel(state)` — only non-null when `state.Docked && state.AvailableRecipes.Length > 0`:

```csharp
private static CraftingUiModel? BuildCraftingModel(GameState state)
{
    if (!state.Docked || state.AvailableRecipes.Length == 0)
        return null;

    var allItems = MergeCargoAndStorage(state);   // cargo + storage combined

    var recipes = state.AvailableRecipes
        .Select(r => new CraftingUiRecipe(
            r.Id,
            r.Name ?? r.Id,
            r.Category ?? "Unknown",
            r.Tier,
            BuildMaterialsSummary(r),
            BuildOutputSummary(r),
            CanAfford(r, allItems)))
        .OrderBy(r => r.Category)
        .ThenBy(r => r.Tier ?? int.MaxValue)
        .ThenBy(r => r.Name)
        .ToArray();

    return new CraftingUiModel(
        state.CurrentPOI?.Id ?? "(unknown)",
        state.Ship.CargoUsed,
        state.Ship.CargoCapacity,
        recipes);
}
```

### 4.3 `UiSnapshot` — Add `CraftingModel`

**File:** `examples/Crowbar/App/UiSnapshot.cs`

Add `CraftingUiModel? CraftingModel` field alongside the existing models.

### 4.4 `UiSnapshotPublisher` — Populate Crafting

**File:** `examples/Crowbar/App/UiSnapshotPublisher.cs`

In `PublishPrayerSnapshot`, extract `CraftingModel` from `AppUiStateBuilder.BuildUiState` and include it in the published snapshot.

### 4.5 Web UI — Crafting Panel

**File:** `examples/Crowbar/App/UI/Web/` (HTMX partial templates)

Add a crafting panel tab, shown only when `CraftingModel != null`. Displays:

- Station ID and cargo bar
- Filterable recipe list (by category/tier)
- Each recipe row: name, category, tier, materials, output, affordability indicator
- "Insert craft command" button that populates the script editor with `craft <recipe_id>;`

---

## 5. Summary — Touch Points

| Layer | File(s) | Change |
|---|---|---|
| SpaceMolt API | _(external)_ | `POST /craft`, `GET /catalog?type=recipes` |
| Catalog service | `SpaceMoltCatalogService.cs` | `GetFullRecipeCatalogByIdAsync`, cache key, `EnsureFreshCataloguesAsync` |
| State assembly | `SpaceMoltGameStateAssembler.cs` | Fetch recipe catalog when docked |
| State model | `GameState` (partial) | `AvailableRecipes CatalogueEntry[]` |
| State builder | `RuntimeStateBuilder.cs` | `ApplyDockedStationState` — accept + assign recipes |
| State rendering | `GameStateRendering.cs` | `RenderCraftingLlmMarkdown()` |
| DSL command | `CraftCommand.cs` _(new)_ | `craft <recipe_id> <count?>;` |
| Command catalog | `CommandCatalog.cs` | Register `CraftCommand` |
| DSL parser | `DSL.cs` | `PromptArgNameOverrides["craft"]` |
| Crowbar models | `AppUiModels.cs` | `CraftingUiModel`, `CraftingUiRecipe` |
| Crowbar UI builder | `AppUiStateBuilder.cs` | `BuildCraftingModel`, extend return tuple |
| Crowbar snapshot | `UiSnapshot.cs` | Add `CraftingModel` field |
| Crowbar publisher | `UiSnapshotPublisher.cs` | Populate `CraftingModel` |
| Crowbar web UI | `UI/Web/` templates | Crafting tab panel |
