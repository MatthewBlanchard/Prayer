# Factions API — SpaceMolt Integration Proposal

Source: `docs/spacemolt/api.txt` + `docs/spacemolt/openapi.txt`

---

## 1. The SpaceMolt Factions API

### State calls (no DSL commands — data flows into `GameState`)

| API call | When fetched | Populates |
|---|---|---|
| `get_status` (existing) | every cycle | `player.faction_id`, `player.faction_rank` |
| `faction_get_invites()` | every cycle | `GameState.PendingFactionInvites[]` |
| `faction_list(limit?, offset?)` | cached, like item catalog | `GameState.KnownFactions[]` |
| `view_faction_storage()` | docked + in faction | `GameState.FactionStorage` |
| `faction_list_missions()` | docked + in faction | `GameState.FactionPostedMissions[]` |

### Mutation calls (each backed by a DSL command)

All are tick-rate-limited (`x-is-mutation: true`).

**Membership**
- `join_faction(faction_id)` — join via pending invite
- `leave_faction()` — leave current faction
- `faction_decline_invite(faction_id)` — decline a pending invite

**Credits & Items**
- `faction_deposit_credits(amount)` — move credits from wallet to faction storage
- `faction_withdraw_credits(amount)` — move credits from faction storage to wallet
- `faction_deposit_items(item_id, quantity)` — move items from cargo to faction storage
- `faction_withdraw_items(item_id, quantity)` — move items from faction storage to cargo

**Intel Sharing**
- `faction_submit_intel(systems)` — submit system scan data to faction map
- `faction_submit_trade_intel(stations)` — submit market observations to faction ledger

**Leadership** (permission-gated, nice-to-have)
- `faction_invite(player_id)` — send invite
- `faction_kick(player_id)` — remove member
- `faction_promote(player_id, role_id)` — change member role
- `faction_cancel_mission(template_id)` — cancel a posted mission

---

## 2. Prayer DSL — Proposed Changes

Commands are grouped by priority: **core** (any faction member), **officer** (permission-gated), **diplomacy**.

### 2.1 Core Commands

#### `JoinFactionCommand`
```
join_faction <faction_id>;
```
- No dock required.
- `IsAvailable`: `string.IsNullOrEmpty(state.PlayerFactionId) && state.PendingFactionInvites.Any(i => i.FactionId == cmd.Arg1)`.
- `ExecuteAsync`: `client.ExecuteCommandAsync("join_faction", new { faction_id = cmd.Arg1 })`.
- Arg override: `["join_faction"] = new[] { "faction_id" }`

#### `LeaveFactionCommand`
```
leave_faction;
```
- No dock required.
- `IsAvailable`: `!string.IsNullOrEmpty(state.PlayerFactionId)`.
- `ExecuteAsync`: `client.ExecuteCommandAsync("leave_faction")`.
- No arguments.

#### `DeclineFactionInviteCommand`
```
faction_decline_invite <faction_id>;
```
- No dock required.
- `IsAvailable`: `state.PendingFactionInvites.Length > 0`.
- `ExecuteAsync`: `client.ExecuteCommandAsync("faction_decline_invite", new { faction_id = cmd.Arg1 })`.
- Arg override: `["faction_decline_invite"] = new[] { "faction_id" }`

#### `FactionDepositCreditsCommand`
```
faction_deposit_credits <amount>;
```
- Extends `AutoDockSingleTurnCommand`, `RequiresStation = true`.
- `IsAvailableWhenDocked`: `!string.IsNullOrEmpty(state.PlayerFactionId) && state.Credits > 0`.
- `ExecuteDockedAsync`: `client.ExecuteCommandAsync("faction_deposit_credits", new { amount = cmd.Quantity })`.
- Arg override: `["faction_deposit_credits"] = new[] { "amount" }`

#### `FactionWithdrawCreditsCommand`
```
faction_withdraw_credits <amount>;
```
- Extends `AutoDockSingleTurnCommand`, `RequiresStation = true`.
- `IsAvailableWhenDocked`: `!string.IsNullOrEmpty(state.PlayerFactionId) && state.FactionStorage?.Credits > 0`.
- `ExecuteDockedAsync`: `client.ExecuteCommandAsync("faction_withdraw_credits", new { amount = cmd.Quantity })`.

#### `FactionDepositItemsCommand`
```
faction_deposit_items <item_id> <quantity>;
```
- Extends `AutoDockSingleTurnCommand`, `RequiresStation = true`.
- `IsAvailableWhenDocked`: `!string.IsNullOrEmpty(state.PlayerFactionId) && state.Ship.Cargo.Any()`.
- `ExecuteDockedAsync`: `client.ExecuteCommandAsync("faction_deposit_items", new { item_id = cmd.Arg1, quantity = cmd.Quantity ?? 1 })`.
- Arg override: `["faction_deposit_items"] = new[] { "item_id", "quantity" }`

#### `FactionWithdrawItemsCommand`
```
faction_withdraw_items <item_id> <quantity>;
```
- Extends `AutoDockSingleTurnCommand`, `RequiresStation = true`.
- `IsAvailableWhenDocked`: `!string.IsNullOrEmpty(state.PlayerFactionId) && state.FactionStorage?.Items.Any() == true`.
- `ExecuteDockedAsync`: `client.ExecuteCommandAsync("faction_withdraw_items", new { item_id = cmd.Arg1, quantity = cmd.Quantity ?? 1 })`.

### 2.2 Officer Commands (permission-gated)

Registered in `CommandCatalog`; `IsAvailable` only checks that the player is in a faction. Permission failures are returned server-side as error messages.

#### `FactionInviteCommand`
```
faction_invite <player_id>;
```
- No dock required. `IsAvailable`: `!string.IsNullOrEmpty(state.PlayerFactionId)`.
- `ExecuteAsync`: `client.ExecuteCommandAsync("faction_invite", new { player_id = cmd.Arg1 })`.

#### `FactionKickCommand`
```
faction_kick <player_id>;
```
- Same availability as invite.

#### `FactionSubmitIntelCommand`
```
faction_submit_intel;
```
- No dock required. `IsAvailable`: `!string.IsNullOrEmpty(state.PlayerFactionId)`.
- Submits current system scan data assembled from `state.Galaxy`. No user-supplied arguments.

### 2.4 Register in `CommandCatalog`

**File:** `src/Prayer/MiddleRuntime/Commands/CommandCatalog.cs`

```csharp
// Core
new JoinFactionCommand(),
new LeaveFactionCommand(),
new DeclineFactionInviteCommand(),
new FactionDepositCreditsCommand(),
new FactionWithdrawCreditsCommand(),
new FactionDepositItemsCommand(),
new FactionWithdrawItemsCommand(),
// Officer
new FactionInviteCommand(),
new FactionKickCommand(),
new FactionSubmitIntelCommand(),
```

### 2.5 DSL Examples for Agent Prompt

```
- join_faction faction_xyz;                  // join via existing invite
- leave_faction;                             // leave current faction
- faction_decline_invite faction_xyz;        // decline invite
- faction_deposit_credits 5000;              // move 5000 cr to faction bank
- faction_withdraw_credits 2000;             // pull 2000 cr from faction bank
- faction_deposit_items ore_iron 100;        // move 100 iron to faction storage
- faction_withdraw_items ore_iron 50;        // pull 50 iron from faction storage
- faction_invite player_xyz;                 // invite a player (officer+)
- faction_kick player_xyz;                   // kick a player (officer+)
- faction_submit_intel;                      // share current system scan with faction
- faction_set_ally faction_abc;              // mark as ally (diplomacy perm)
- faction_set_enemy faction_abc;             // mark as enemy
- faction_declare_war faction_abc;           // declare war (diplomacy perm)
- faction_propose_peace faction_abc;         // propose peace
- faction_accept_peace faction_abc;          // accept peace proposal
```

---

## 3. State Model — Proposed Changes

### 3.1 New Types

**File:** `src/Prayer.Shared/GameStateModels.cs`

```csharp
public class FactionInvite
{
    public string FactionId   { get; set; } = "";
    public string FactionName { get; set; } = "";
    public string FactionTag  { get; set; } = "";
    public string InvitedBy   { get; set; } = "";
}

public class FactionInfo
{
    public string Id             { get; set; } = "";
    public string Name           { get; set; } = "";
    public string Tag            { get; set; } = "";
    public string LeaderUsername { get; set; } = "";
    public int    MemberCount    { get; set; }
}

public class FactionStorageState
{
    public string FactionId   { get; set; } = "";
    public string FactionName { get; set; } = "";
    public string FactionTag  { get; set; } = "";
    public int    Credits     { get; set; }
    public Dictionary<string, ItemStack> Items { get; set; } = new();
}

public class FactionPostedMission
{
    public string TemplateId      { get; set; } = "";
    public string Title           { get; set; } = "";
    public string Type            { get; set; } = "";
    public int    Difficulty      { get; set; }
    public int    RewardCredits   { get; set; }
    public int    ActiveInstances { get; set; }
    public string PostedBy        { get; set; } = "";
}
```

### 3.2 `GameState` — New Properties

**File:** `src/Prayer.Shared/GameStateModels.cs` (partial `GameState`)

```csharp
public string                 PlayerFactionId       { get; set; } = "";
public string                 PlayerFactionRank     { get; set; } = "";
public FactionInvite[]        PendingFactionInvites { get; set; } = Array.Empty<FactionInvite>();
public FactionInfo[]          KnownFactions         { get; set; } = Array.Empty<FactionInfo>();
public FactionStorageState?   FactionStorage        { get; set; }
public FactionPostedMission[] FactionPostedMissions { get; set; } = Array.Empty<FactionPostedMission>();
```

---

## 4. Infra — Proposed Changes

### 4.1 `SpaceMoltGameStateAssembler` — New Fetches

**File:** `src/Prayer/Infra/SpaceMolt/SpaceMoltGameStateAssembler.cs`

Inside `BuildAsync`, after the player object is parsed:

```csharp
// Allegiance from existing player object
string playerFactionId   = player.TryGetProperty("faction_id",   out var fid)  ? fid.GetString()  ?? "" : "";
string playerFactionRank = player.TryGetProperty("faction_rank", out var frnk) ? frnk.GetString() ?? "" : "";

// Pending invites — every cycle
FactionInvite[] pendingInvites = Array.Empty<FactionInvite>();
try
{
    var inviteResult = await _owner.ExecuteAsync("faction_get_invites");
    if (inviteResult.TryGetProperty("invites", out var invArr))
        pendingInvites = invArr.EnumerateArray().Select(ParseFactionInvite).ToArray();
}
catch { /* best-effort */ }

// Storage + posted missions — docked + in faction only
FactionStorageState? factionStorage = null;
FactionPostedMission[] factionPostedMissions = Array.Empty<FactionPostedMission>();
if (docked && !string.IsNullOrEmpty(playerFactionId))
{
    try
    {
        var storageResult = await _owner.ExecuteAsync("view_faction_storage");
        factionStorage = ParseFactionStorage(storageResult);
    }
    catch { /* best-effort */ }

    try
    {
        var missionsResult = await _owner.ExecuteAsync("faction_list_missions");
        if (missionsResult.TryGetProperty("missions", out var mArr))
            factionPostedMissions = mArr.EnumerateArray().Select(ParseFactionPostedMission).ToArray();
    }
    catch { /* best-effort */ }
}
```

### 4.2 `SpaceMoltCatalogService` — Faction List Cache

**File:** `src/Prayer/Infra/SpaceMolt/SpaceMoltCatalogService.cs`

- Add `const string FactionListCacheFileKey = "factions_list";`
- Add `GetFactionListAsync(bool forceRefresh)` — calls `faction_list`, paginates if needed, saves to `AppPaths.FactionListCacheFile`.
- Add to `EnsureFreshCataloguesAsync()` (best-effort).

**File:** `src/Prayer.Shared/AppPaths.cs`

```csharp
public static string FactionListCacheFile =>
    Path.Combine(CacheDir, "factions_list.json");
```

### 4.3 `RuntimeStateBuilder`

**File:** `src/Prayer/MiddleRuntime/State/RuntimeStateBuilder.cs`

```csharp
state.PlayerFactionId       = playerFactionId;
state.PlayerFactionRank     = playerFactionRank;
state.PendingFactionInvites = pendingInvites;
state.KnownFactions         = knownFactions;
state.FactionStorage        = factionStorage;
state.FactionPostedMissions = factionPostedMissions;
```

---

## 5. State Rendering — Proposed Changes

**File:** `src/Prayer/Core/State/GameStateRendering.cs`

```csharp
private static string RenderFactionLlmMarkdown(GameState state)
{
    var sb = new StringBuilder();

    if (!string.IsNullOrEmpty(state.PlayerFactionId))
        sb.AppendLine($"## Faction: {state.PlayerFactionId} | Rank: {state.PlayerFactionRank}");

    if (state.PendingFactionInvites.Length > 0)
    {
        sb.AppendLine("### Pending Faction Invites");
        foreach (var inv in state.PendingFactionInvites)
            sb.AppendLine($"- [{inv.FactionTag}] {inv.FactionName} — id: {inv.FactionId} (from: {inv.InvitedBy})");
    }

    if (state.FactionStorage != null)
    {
        sb.AppendLine($"### Faction Storage ({state.FactionStorage.FactionName})");
        sb.AppendLine($"Credits: {state.FactionStorage.Credits:N0}");
        foreach (var item in state.FactionStorage.Items.Values)
            sb.AppendLine($"- {item.ItemId} x{item.Quantity}");
    }

    if (state.FactionPostedMissions.Length > 0)
    {
        sb.AppendLine("### Faction Posted Missions");
        foreach (var m in state.FactionPostedMissions)
            sb.AppendLine($"- [{m.TemplateId}] {m.Title} (diff: {m.Difficulty}, reward: {m.RewardCredits:N0} cr, active: {m.ActiveInstances})");
    }

    return sb.ToString();
}
```

Include in the main state prompt block after active missions.

---

## 6. Crowbar — Proposed Changes

### 6.1 `FactionUiModel`

**File:** `examples/Crowbar/App/AppUiModels.cs`

```csharp
public sealed record FactionInviteUiEntry(
    string FactionId, string FactionName, string FactionTag, string InvitedBy);

public sealed record FactionStorageItemUiEntry(string ItemId, string Name, int Quantity);

public sealed record FactionPostedMissionUiEntry(
    string TemplateId, string Title, string Type,
    int Difficulty, int RewardCredits, int ActiveInstances);

public sealed record FactionUiModel(
    string                          PlayerFactionId,
    string                          PlayerFactionRank,
    FactionInviteUiEntry[]          PendingInvites,
    int                             StorageCredits,
    FactionStorageItemUiEntry[]     StorageItems,
    FactionPostedMissionUiEntry[]   PostedMissions);
```

### 6.2 `AppUiStateBuilder`, `UiSnapshot`, `UiSnapshotPublisher`

Same pattern as `CraftingUiModel`: extend the `BuildUiState` return tuple, add `FactionModel?` to `UiSnapshot`, populate in `UiSnapshotPublisher`.

### 6.3 Web UI — Faction Panel

**File:** `examples/Crowbar/App/UI/Web/` (HTMX partial templates)

Tab is visible whenever `FactionModel != null`. Three sub-sections:

- **Membership** — faction ID, rank, quick-fill buttons for `leave_faction` / `join_faction` / `faction_decline_invite`
- **Storage** — credits balance + item list, quick-fill buttons for deposit/withdraw commands
- **Posted Missions** — list with template ID, difficulty, reward, active instance count; quick-fill for `faction_cancel_mission`

---

## 7. Summary — Touch Points

| Layer | File(s) | Change |
|---|---|---|
| SpaceMolt API | _(external)_ | `faction_get_invites`, `faction_list`, `view_faction_storage`, `faction_list_missions` (state); 15 mutation endpoints |
| State models | `GameStateModels.cs` | `FactionInvite`, `FactionInfo`, `FactionStorageState`, `FactionPostedMission`; `GameState`: `PlayerFactionId`, `PlayerFactionRank`, `PendingFactionInvites[]`, `KnownFactions[]`, `FactionStorage`, `FactionPostedMissions[]` |
| Cache paths | `AppPaths.cs` | `FactionListCacheFile` |
| Catalog service | `SpaceMoltCatalogService.cs` | `GetFactionListAsync`, `FactionListCacheFileKey`, `EnsureFreshCataloguesAsync` |
| State assembly | `SpaceMoltGameStateAssembler.cs` | Parse `faction_id`/`faction_rank` from player; fetch invites every cycle; fetch storage + posted missions when docked + in faction |
| State builder | `RuntimeStateBuilder.cs` | Assign all faction state fields |
| State rendering | `GameStateRendering.cs` | `RenderFactionLlmMarkdown()` |
| DSL commands | 15 new files | See §2 — core, officer, diplomacy groups |
| Command catalog | `CommandCatalog.cs` | Register all 15 commands |
| DSL parser | `DSL.cs` | `PromptArgNameOverrides` for relevant commands |
| Crowbar models | `AppUiModels.cs` | `FactionUiModel` + sub-records |
| Crowbar UI builder | `AppUiStateBuilder.cs` | `BuildFactionModel`, extend return tuple |
| Crowbar snapshot | `UiSnapshot.cs` | Add `FactionModel?` |
| Crowbar publisher | `UiSnapshotPublisher.cs` | Populate `FactionModel` |
| Crowbar web UI | `UI/Web/` templates | Faction panel: membership, storage, posted missions |
