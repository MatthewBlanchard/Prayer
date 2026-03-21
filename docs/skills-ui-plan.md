# Skills UI — Implementation Plan

## Architecture context

The Crowbar UI talks to the Prayer **server** via HTTP (`PrayerApiClient`). The `CommandExecutionEngine` (where skills/overrides execute) lives in the Prayer server process, not in Crowbar. Wiring up the UI therefore has two layers: Prayer server changes and Crowbar frontend changes.

---

## 1. Skill library persistence — Prayer server

- Store the library as a `skills.prayer` file per-session under an existing data path (`AppPaths`)
- `RuntimeHost` loads it on startup: `SkillLibrary.Parse(file text)` → `engine.SetSkillLibrary(...)`
- `RuntimeHost` exposes `SetSkillLibrary(string prayerText)` which:
  1. Parses and validates the new text (`SkillLibrary.Parse`)
  2. Calls `engine.SetSkillLibrary(library)`
  3. Writes the canonical serialization back to disk (`library.Serialize()`)

---

## 2. New Prayer API endpoints

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/session/{id}/skills` | Returns current `skills.prayer` text as JSON |
| `POST` | `/session/{id}/skills` | Accepts new `.prayer` text, validates, applies, persists |

Server returns a 400 with a parse/validation error message if the submitted text is invalid.

---

## 3. `PrayerApiClient` additions — Crowbar

```csharp
Task<string> GetSkillLibraryAsync(string sessionId);
Task SetSkillLibraryAsync(string sessionId, string prayerText);
```

Both are thin wrappers over the two endpoints above.

---

## 4. New script-column tab: Skills

The script column currently has **Script | Roleplay** tabs. Add a **Skills** tab as a third entry.

The tab has two sections: **Skills** and **Overrides**.

### Skills section

A list of defined skills. Each row shows:
- Name + parameter signature (e.g. `RefuelAt(station: poi_id)`)
- Collapsed body (expandable)
- **Edit** and **Remove** buttons

At the bottom of the list: a raw-text textarea (`skill ... { ... }` syntax) + **Add** button. The user writes or pastes the skill definition directly. LLM-assisted generation is a follow-up.

### Overrides section

Same list style, but each row additionally has:
- **Enable / disable** toggle (checkbox)
- The condition displayed inline (e.g. `when FUEL() < 50`)
- **Up / Down** buttons for priority reordering

At the bottom: a raw-text textarea + **Add** button.

---

## 5. HTTP routes — `HtmxBotWindow`

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/partial/skills-library?bot_id=` | Renders the Skills tab content (HTMX poll) |
| `POST` | `/api/skills-save?bot_id=` | Sends full `.prayer` text to Prayer API, refreshes tab |
| `POST` | `/api/skills-toggle-override?bot_id=` | Toggles enabled on one override by name, re-serializes, saves |
| `POST` | `/api/skills-delete?bot_id=` | Removes one skill or override by name, re-serializes, saves |
| `POST` | `/api/skills-reorder-override?bot_id=` | Moves an override up or down by one position, saves |

All mutating routes respond with the re-rendered `/partial/skills-library` fragment so HTMX can swap it in-place.

---

## 6. Shell changes

Add **Skills** to the script-column tab bar alongside Script | Roleplay:

```
[ Script ] [ Roleplay ] [ Skills ]
```

This is distinct from the existing **Skills** tab in the state panel (which shows the player's in-game skill levels and is unrelated).

---

## Out of scope for this iteration

- **LLM-generated skill creation** — the "describe what you want" flow that sends a natural-language prompt to the LLM and gets back a `skill { }` block. Needs prompt engineering work.
- **CodeMirror inline body editor** — plain textarea is sufficient to start.
- **Drag-to-reorder overrides** — Up/Down buttons are simpler and sufficient.
- **Override visibility in the script generation prompt** — surfacing active overrides to the LLM when generating scripts so it doesn't duplicate their logic. Tracked in the original proposal as an open question.

---

## Open question

> The existing "Skills" tab in the state panel shows the player's in-game skills (mining level, etc.). The new skills tab lives in the script column tab bar instead, so there is no naming conflict. Confirm this placement is correct, or specify an alternative.
