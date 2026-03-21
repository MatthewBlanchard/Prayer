# Activity-Based AI Architecture for MMO Agent

This document summarizes the architecture for an AI-controlled agent in an MMO-style environment (e.g., EVE-like). The system combines deterministic planning, hierarchical activities, heuristics, and LLM reasoning to produce reliable yet flexible behavior.

---

# Core Philosophy

The system separates responsibilities into three layers:

```
Character AI (motivation / strategy)
↓
Activity System (goal decomposition & planning)
↓
Execution Layer (commands sent to game API)
```

The AI **does not directly control the game**. Instead, it selects and generates activities. Deterministic systems evaluate feasibility and execute them.

This keeps the system:

* reliable
* interruptible
* scalable

---

# Key Concepts

## Activities

Activities represent **goals the agent wants to achieve**.

Each activity is a declarative specification:

```
Activity
    name
    parameters
    goal predicate
    requirements
    fulfillments
    instruction (leaf only)
```

Rules:

* Activities are **stateless**
* Completion is determined by evaluating the **goal predicate against world state**
* Activities either:

  * decompose into **sub-activities**
  * or execute an **instruction** (leaf node)

---

## Leaf Activities

A leaf activity is simply:

```
fulfillments = []
```

Leaf activities contain an instruction describing what should be done.

Example:

```
Activity: mine_resource(resource, qty)

Goal:
    has_item(resource, qty)

Instruction:
    "Mine {resource} until cargo contains {qty}"
```

The engine converts the instruction into actual game commands.

---

# Parameterized Activity Templates

Many activities are reusable patterns.

Examples:

```
craft(item)

acquire_resource(resource, qty)

buy_item(item, qty)

sell_item(item, qty)

travel_to(location)
```

Parameterized templates prevent the system from exploding into thousands of specific actions.

Example:

```
craft("railgun")
```

instead of:

```
craft_railgun
craft_engine
craft_drone
```

---

# Hierarchical Decomposition

Activities can fulfill other activities.

Example:

```
obtain_item("railgun")
  craft("railgun")
      acquire_resource("iron",200)
          mine_resource("iron",200)
      acquire_resource("copper",50)
          buy_resource("copper",50)
      perform_craft("railgun")
```

The planner expands activities until reaching leaf nodes.

---

# Difficulty Heuristic

Each activity receives an estimated **difficulty score**.

Leaf difficulty may depend on:

* travel distance
* time required
* resource availability
* market prices
* risk

Example:

```
mine_resource("iron",200) → difficulty 6
buy_item("iron",200) → difficulty 3
```

For composite activities:

```
difficulty(activity)
    = sum(difficulty(subactivities))
```

Difficulty is used to:

* prune impossible options
* rank candidate activities

---

# Candidate Activity Generation

The engine generates potential activities using templates.

Example candidate set:

```
mine_resource("iron",200)
run_trade_route("iron")
craft("railgun")
buy_item("railgun")
accept_mission("mining")
```

---

# Difficulty Pruning

Activities exceeding a difficulty threshold are removed.

Example:

```
craft("railgun") → difficulty 10 → removed
mine_resource("iron",200) → difficulty 4 → kept
```

The AI only sees **reasonable candidates**.

---

# RAG Retrieval

Embeddings can retrieve previously successful activities or strategies.

Example query context:

```
character role: miner
goal: increase credits
```

Retrieved activities:

```
mine_resource
sell_ore
run_mining_mission
```

These influence the candidate pool.

# Override Activities

Override activities act as **interrupt handlers**.

Examples:

```
ensure_fuel
repair_ship
ensure_cargo_space
escape_combat
```

Overrides are checked before executing normal activities.

Example:

```
fuel < 30%
→ run ensure_fuel
→ resume previous activity
```

---

# Execution Layer

Leaf activities produce instructions such as:

```
"Mine iron until cargo contains 200 iron."
```

These are converted by a **sentence-to-command translator** into game commands.

Example output:

```
travel asteroid_belt
mine
mine
mine
```

Execution loop:

```
observe world state
check overrides
choose activity
expand activity tree
execute leaf instruction
repeat
```

---

# Why This Works for MMO Environments

Pure GOAP planners struggle with large worlds due to massive action spaces.

This architecture avoids that by:

* using **activity templates**
* performing **goal decomposition**
* pruning via **difficulty heuristics**
* letting the AI **choose strategies instead of searching action graphs**

The result is a scalable planning system.

---

# Final Architecture

```
World State
↓
Candidate Activity Generator
↓
Difficulty Heuristic Pruning
↓
RAG Strategy Retrieval
↓
AI Chooses Activity
↓
Activity Planner Expands Tree
↓
Leaf Instruction
↓
Command Translator
↓
Game API
```

---

# Key Properties

The system is:

* stateless
* interruptible
* scalable
* AI-assisted
* deterministic at execution

This allows both **emergent role-playing behavior** and **stable automated operation** in a complex MMO environment.
