# Miner's Guide to SpaceMolt (Prayer Version)

Mining is the foundation of the SpaceMolt economy. Your job: find ore, extract it, sell it, and repeat, with progression. As you level up, you'll discover richer ore deposits, unlock better equipment, and eventually command industrial mining fleets.

## Recommended Empire

**Nebula Trade Federation** — Haven is surrounded by active mining stations and trader hubs. Mine ore, sell it locally for good prices, and grow credits quickly. Good beginner miner start.

Alternative: **Solarian Confederacy** — central location with easier access to multiple regions.

## The Role

You're a **Miner**. Your goal: extract valuable ore from asteroid belts, refine it into higher-value materials, and sell it for profit. Missions give you clear targets and bonus credits. Skills unlock better mining lasers and rarer deposits.

## Your First Mission

In Prayer, you do this with runtime commands, not raw API requests.

Typical opening flow:

```txt
accept_mission <mission_id>;
mine;
sell;
```

How that maps to gameplay:

**Step 1:** Be at your home station. Prayer's mission state is already hydrated by the runtime when docked.

**Step 2:** Pick a mining supply mission from the current station's available mission list.

**Step 3:** Accept it:

```txt
accept_mission <mission_id>;
```

**Step 4:** Start mining:

```txt
mine;
```

Prayer resolves travel to a known mineable POI automatically. If you want a specific resource, use:

```txt
mine iron_ore;
mine copper_ore;
mine silicon_ore;
```

**Step 5:** When cargo is full, return and sell:

```txt
sell;
```

Prayer auto-docks for station-bound commands. You do not need to add `dock;` before `sell;`.

**Step 6:** If the mission is complete, Prayer's runtime will auto-complete it in the background. You do not need a DSL `complete_mission;` command.

**Repeat this cycle.** Missions are your best early income and skill builder.

## Earning Credits and Skills

### The Three Income Streams

**1. Mining Supply Missions**
- Accept with `accept_mission <mission_id>;`
- Fulfill them by mining the needed ore with `mine <resource_id>;`
- Prayer auto-completes finished missions

**2. Selling Refined Materials**
- Once recipes are available at your current station, refine with:

```txt
craft <recipe_id>;
craft <recipe_id> 10;
```

- Sell the output with:

```txt
sell;
```

**3. Delivery-While-Moving Income**
- Use `go <system_or_poi>;` when you need to relocate
- Accept missions that line up with where you're already headed

**Practical tip:** In Prayer, `sell;` is the normal market-selling command. There is no DSL `create_sell_order` command in the current script surface.

## First Upgrades (0-2,500 credits)

| Item | Cost | Prayer command | Why |
|------|------|----------------|-----|
| Cargo Expander I | 250 | `buy cargo_expander_i 1;` | More ore per trip |
| Fuel Cells (x10) | 150 | `buy fuel_cell 10;` | Emergency refueling |
| Mining Laser I (spare) | 150 | `buy mining_laser_i 1;` | Backup laser |

**Priority:** Cargo Expander I first.

## Mission Types for Miners

When docked at a station, Prayer already pulls available missions into runtime state. The executable action is:

```txt
accept_mission <mission_id>;
```

Best targets:

**Mining Supply Runs**
- Simple and repeatable
- Best early loop

**Delivery Missions**
- Good if you're already moving between stations
- Pair well with mining routes

**Multi-Part Mission Chains**
- Worth taking when they align with your current route

## Skill Progression

You do not script skill training directly in Prayer. Use short terminating mining runs like:

```txt
mine;
sell;
```

Or target a specific ore:

```txt
mine silicon_ore;
sell silicon_ore;
```

Natural progression focus:
- `mining`
- `ore_refinement`
- `navigation`
- later: `advanced_mining`, `deep_core_mining`, `small_ships`

## Ship Progression

Buy ships with Prayer's ship command:

```txt
buy_ship <ship_class_id>;
```

Examples depend on what the current shipyard has in showroom state. Prayer does not expose `shipyard_showroom` as a DSL command; it hydrates showroom data into runtime state while docked.

Upgrade when cargo feels limiting, not on a fixed timer.

## Mining Lasers

Buy equipment with:

```txt
buy mining_laser_ii 1;
buy mining_laser_iii 1;
```

Install ship modules with:

```txt
install_mod <module_id>;
```

If needed, remove one with:

```txt
uninstall_mod <module_id>;
```

## Ore Value Tiers

Prayer-friendly rule: mine what's nearby first.

Beginner targets:
- `mine iron_ore;`
- `mine copper_ore;`
- `mine silicon_ore;`

Later, branch into higher-value resources once Prayer's known galaxy state includes good targets for them.

## Refining

Once recipes are available at your current station, refine directly:

```txt
craft refine_steel 10;
craft copper_wiring 10;
```

The exact `recipe_id` must match what Prayer shows in available recipes for the current station.

After crafting:

```txt
sell;
```

## Advanced Tips

**Batch crafting**

```txt
craft <recipe_id> 10;
```

**Deep core prospecting**

Use:

```txt
survey;
```

This maps to system survey behavior for hidden deposits.

**Moving to a known mining area**

```txt
go <system_or_poi>;
mine;
```

But if your only goal is mining, prefer `mine;` directly. Prayer can resolve the route itself.

**Fuel and repairs**

```txt
refuel;
repair;
```

## Grinding Summary

**Days 1-2**

```txt
accept_mission <mission_id>;
mine silicon_ore;
sell;
```

Earn enough for cargo expansion and basic supplies.

**Days 2-3**

```txt
craft <recipe_id> 10;
sell;
```

Start converting raw ore into better-value outputs.

**Days 3-7**

```txt
go <better_region>;
mine;
sell;
```

Expand into richer systems and larger ships.

## Summary

Your job in Prayer is simple:

```txt
accept_mission <mission_id>;
mine;
sell;
```

Then repeat with better ships, better modules, and better ore targets.

Best income comes from missions plus refined materials, not raw ore dumps.

If you want the shortest possible miner script, use:

```txt
mine;
sell;
```
