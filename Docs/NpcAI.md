# EVESharp NPC AI System — Standings-Aware Aggression

## Overview

The NPC AI system adds standings-aware behavior to NPC entities (category 11 / `Entity`).
NPCs query the `chrNPCStandings` table to determine whether a nearby player is hostile,
neutral, or friendly. Hostile players are engaged; friendly ones are ignored. If a player's
standing changes at runtime (e.g. via `/setstanding`), the NPC re-evaluates and may
disengage mid-combat.

This mirrors how CCP's live EVE servers could have implemented faction-aware NPC behavior —
every piece of infrastructure (standings data, movement commands, aggression broadcasts,
NPC AI attributes) already existed in the Apocrypha client/server codebase.

---

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│                    DestinyManager                         │
│  (per solar system, 1-second tick loop)                   │
│                                                          │
│  Tick()                                                  │
│    ├─ 1. Drain command queue                             │
│    ├─ 2. Process movement (Goto/Follow/Orbit/Warp/Stop)  │
│    └─ 3. ProcessNpcAi()          ◄── NEW                 │
│           ├─ For each NPC entity:                        │
│           │   ├─ Idle    → scan for hostiles             │
│           │   ├─ Combat  → orbit + attack                │
│           │   ├─ Pursuit → chase fleeing target          │
│           │   └─ Departing → return to spawn             │
│           │                                              │
│           └─ Standing check via StandingDB               │
│               └─ chrNPCStandings table                   │
└──────────────────────────────────────────────────────────┘
```

### Key Files

| File | Purpose |
|------|---------|
| `EVESharp.Destiny/NpcAiState.cs` | NPC activity state enum |
| `EVESharp.Destiny/BubbleEntity.cs` | NPC AI fields on the runtime entity |
| `EVESharp.Node/Services/Space/DestinyManager.cs` | AI tick logic (state machine) |
| `EVESharp.Node/Services/Space/SolarSystemDestinyManager.cs` | Per-system manager registry |
| `EVESharp.Node/Services/Space/DestinyBroadcaster.cs` | OnTarget notification broadcast |
| `EVESharp.EVE/OldDatabase/StandingDB.cs` | Standing database queries |
| `EVESharp.Node/Services/Network/slash.cs` | `/entity deploy` GM command |
| `EVESharp.Node/Services/Space/LevelEditor.cs` | Dungeon/level editor NPC spawning |

---

## NPC AI State Machine

```
                  ┌─────────┐
          ┌──────►│  IDLE   │◄──────────────────────┐
          │       └────┬────┘                        │
          │            │ hostile player               │ arrived at spawn
          │            │ detected                     │
          │            ▼                              │
          │       ┌─────────┐    target too far  ┌────┴─────┐
          │       │ COMBAT  ├───────────────────►│ PURSUIT  │
          │       └────┬────┘                    └────┬─────┘
          │            │                              │
          │            │ target lost /                 │ chase distance
          │            │ standing improved /           │ exceeded /
          │            │ chase distance exceeded       │ target lost
          │            ▼                              │
          │       ┌──────────┐                        │
          └───────┤DEPARTING │◄───────────────────────┘
                  └──────────┘
                  (return to spawn)
```

### State Details

#### IDLE (`NpcAiState.Idle = 0`)
- Scans for hostile players every **3 seconds** (`NPC_SCAN_INTERVAL`)
- For each player within `AttackRange`:
  - Queries `StandingDB.GetNpcStandingToCharacter(factionID, characterID)`
  - If standing **< AggroStandingThreshold** (default 0.0) → player is hostile
- Engages the **closest hostile** player
- On engagement:
  - Transitions to **Combat**
  - Issues orbit command at `OrbitRange`
  - Broadcasts `OnTarget("add", npcID)` so the client shows targeting indicators

#### COMBAT (`NpcAiState.Combat = 1`)
- Orbits target at `OrbitRange` (from `entityFlyRange` attribute)
- Attacks on a randomized cooldown between `AttackDelayMin` and `AttackDelayMax`
- Every 3 seconds, re-checks the target's standing:
  - If standing improved to >= threshold → **disengage**
- If target moves beyond `AttackRange × 1.5` → transition to **Pursuit**
- If NPC's distance from spawn exceeds `ChaseMaxDistance` → **disengage**
- If target leaves the system or is destroyed → **disengage**

#### PURSUIT (`NpcAiState.Pursuit = 6`)
- Follows the target using `CmdFollowBall`
- If target returns within `AttackRange` → transition back to **Combat**
- If distance from spawn exceeds `ChaseMaxDistance` → **disengage**
- If target is lost → **disengage**

#### DEPARTING (`NpcAiState.Departing = 4`)
- NPC flies back to its `SpawnPosition` using `CmdGotoPoint`
- When within **5,000m** (`NPC_LEASH_RETURN`) of spawn → transition to **Idle**
- On arrival, NPC stops and resumes scanning

---

## Standing System Integration

### How Standings Are Checked

The NPC AI uses the existing `chrNPCStandings` database table:

```sql
SELECT standing FROM chrNPCStandings
WHERE characterID = @playerCharacterID
  AND fromID = @npcFactionID
```

- `fromID` = the NPC's faction (e.g. 500010 = Serpentis)
- `characterID` = the player character
- Returns the faction's opinion of the player (range: -10.0 to +10.0)

### Standing Thresholds

| Standing Value | Meaning | NPC Behavior |
|---------------|---------|--------------|
| >= 5.0 | Excellent (dark blue) | Ignored — friendly |
| 0.0 to 5.0 | Good (light blue) | Ignored — friendly |
| 0.0 | Neutral (gray) | Ignored — neutral |
| -5.0 to 0.0 | Bad (orange) | **ENGAGED — hostile** |
| <= -5.0 | Terrible (red) | **ENGAGED — hostile** |

The threshold is configurable per NPC via `BubbleEntity.AggroStandingThreshold` (default: 0.0).

### Dynamic Standing Changes

If a player's standing changes while an NPC is in combat:
- The NPC re-checks standings every `NPC_SCAN_INTERVAL` (3 seconds)
- If the new standing is >= threshold, the NPC **disengages** and returns home
- This enables gameplay like: raise Serpentis standing → Serpentis NPCs stop attacking you

---

## GM Commands

### `/entity deploy <qty> <typeID> [factionID]`

Spawns NPC entities with full standings-aware AI.

**Parameters:**
| Parameter | Required | Description |
|-----------|----------|-------------|
| `qty` | Yes | Number of NPCs to spawn (1–100) |
| `typeID` | Yes | Entity type ID (must be category 11 / Entity) |
| `factionID` | No | Faction ID for standing checks. If omitted, defaults to 0 (no standing checks, attacks everyone) |

**Examples:**
```
/entity deploy 3 23707              Spawn 3 Serpentis NPCs (no faction — hostile to all)
/entity deploy 3 23707 500010       Spawn 3 Serpentis NPCs (faction=Serpentis, checks standings)
/entity deploy 5 21638 500011       Spawn 5 Angel Cartel NPCs (faction=Angel Cartel)
/entity deploy 1 23707 500001       Spawn 1 NPC loyal to Caldari State
```

**Common Faction IDs:**

| Faction ID | Faction Name |
|-----------|--------------|
| 500001 | Caldari State |
| 500002 | Minmatar Republic |
| 500003 | Amarr Empire |
| 500004 | Gallente Federation |
| 500010 | Serpentis |
| 500011 | Angel Cartel |
| 500012 | Blood Raiders |
| 500013 | Guristas |
| 500014 | Sansha's Nation |

### `/setstanding <fromID> <toID> <value> <reason>`

Modifies standings between entities. Use this to change how NPCs react to a player.

**Examples:**
```
/setstanding 500010 140000001 5.0 "Befriended Serpentis"
    → Serpentis (500010) now has +5.0 standing toward player 140000001
    → Serpentis NPCs will STOP attacking this player

/setstanding 500010 140000001 -8.0 "Angered Serpentis"
    → Serpentis now has -8.0 standing toward this player
    → Serpentis NPCs will ATTACK on sight
```

### `/unspawn [range=N]`

Destroys spawned entities within range (default 50km). Works on NPC entities.

```
/unspawn                  Destroy all spawned entities within 50km
/unspawn range=100000     Destroy all within 100km
```

---

## NPC AI Parameters (from dgmTypeAttributes)

When an NPC is spawned, its AI parameters are read from the entity type's attributes
in the `dgmTypeAttributes` database table. These are the same attributes CCP defined
for NPC behavior in the original game.

| Attribute | ID | BubbleEntity Field | Default | Description |
|-----------|----|--------------------|---------|-------------|
| `entityAttackRange` | 247 | `AttackRange` | 50,000m | Detection/engagement radius |
| `entityFlyRange` | 416 | `OrbitRange` | 8,000m | Preferred orbit distance in combat |
| `entityChaseMaxDistance` | 665 | `ChaseMaxDistance` | 100,000m | Max distance from spawn before giving up |
| `entityAttackDelayMin` | 475 | `AttackDelayMin` | 3.0s | Minimum time between attacks |
| `entityAttackDelayMax` | 476 | `AttackDelayMax` | 6.0s | Maximum time between attacks |
| `entityCruiseSpeed` | 508 | `MaxVelocity` | 200 m/s | NPC movement speed |
| `maxVelocity` | 37 | `MaxVelocity` | 200 m/s | Fallback if no cruise speed |
| `agility` | 70 | `Agility` | 1.0 | Turn/acceleration rate |
| `mass` | 4 | `Mass` | 1,000,000 | Entity mass |
| `radius` | 162 | `Radius` | (type) | Entity radius |

If an attribute is not present on the type, the default value is used.

### Other NPC Attributes (defined but not yet used by AI)

These attributes exist in the database and are available for future AI expansion:

| Attribute | ID | Purpose |
|-----------|----|---------|
| `entityMissileTypeID` | 507 | Which missile type the NPC fires |
| `entityWarpScrambleChance` | 504 | Probability of NPC warp-disrupting target |
| `entityFactionLoss` | 562 | Standing loss when player kills this NPC |
| `entitySecurityStatusKillBonus` | 252 | Security status gain on kill |
| `entityKillBounty` | 481 | ISK bounty awarded on kill |
| `entityBracketColour` | 798 | Override bracket color in client |
| `entityDroneCount` | 423 | Number of drones NPC can deploy |
| `entityChaseMaxDelay` | 580 | Delay before starting pursuit |
| `entityChaseMaxDuration` | 582 | Maximum pursuit time |
| `entityMaxWanderRange` | 584 | Idle patrol radius |

---

## Client-Side Effects

### What the player sees when an NPC engages:

1. **NPC starts orbiting** the player's ship (visible movement)
2. **OnTarget notification** sent — client may show targeting indicator
3. **NPC movement is broadcast** via `DoDestinyUpdate` to all players in the system
4. Other players in the same system see the NPC chasing the target

### What happens when the NPC disengages:

1. **OnTarget("clear")** notification sent — targeting indicator removed
2. **NPC flies back** to its spawn position (visible via GotoPoint broadcast)
3. NPC stops at spawn and resumes idle scanning

---

## Dungeon System Integration

NPCs spawned via the Level Editor (`/dungeon play` or the `keeper` service) also
get standings-aware AI. The dungeon's `FactionID` is automatically applied to
spawned Entity-category objects.

```csharp
// DungeonData sample: Serpentis Hideout
Dungeons[102] = new DungeonDefinition
{
    DungeonID = 102,
    DungeonName = "Serpentis Hideout",
    FactionID = 500010,     // ← This faction is applied to spawned NPCs
    ArchetypeID = 1,
    RoomIDs = new List<int> { 2002, 2003 }
};
```

When `LevelEditor.SpawnRoomObjects()` creates entities:
- If the entity is category `Entity` (11), NPC AI parameters are populated
- The dungeon's `FactionID` is set on the `BubbleEntity`
- The NPC begins scanning for hostile players immediately

---

## Database Tables

### `chrNPCStandings`
Stores NPC faction standings toward player characters.

| Column | Type | Description |
|--------|------|-------------|
| `characterID` | int | Player character ID |
| `fromID` | int | NPC faction/entity ID |
| `standing` | double | Standing value (-10.0 to +10.0) |

### `crpNPCCorporations`
Maps NPC corporations to their parent faction.

| Column | Type | Description |
|--------|------|-------------|
| `corporationID` | int | NPC corporation ID |
| `factionID` | int | Parent faction ID |

### `npcStandings`
NPC-to-NPC faction standings (used by `GetNPCNPCStandings`).

| Column | Type | Description |
|--------|------|-------------|
| `fromID` | int | Source faction |
| `toID` | int | Target faction |
| `standing` | double | Standing value |

---

## Example Gameplay Scenario

### Scenario: Player enters Serpentis space

1. GM spawns Serpentis NPCs: `/entity deploy 5 23707 500010`
2. Player undocks — beyonce sends DoDestinyUpdate with NPC entities visible
3. NPCs are in **Idle** state, scanning every 3 seconds
4. Player has default standing of 0.0 with Serpentis → **neutral, not attacked**
5. GM lowers standing: `/setstanding 500010 140000001 -5.0 "Enemy of Serpentis"`
6. Next NPC scan detects standing -5.0 < 0.0 → **hostile!**
7. Closest NPC transitions to **Combat**, orbits player at ~8,000m
8. Other NPCs also detect the player and engage
9. Player warps away → NPCs switch to **Pursuit**, then **Departing**
10. NPCs return to spawn positions and resume **Idle**

### Scenario: Player befriends a faction

1. Player is being attacked by Serpentis NPCs (standing = -5.0)
2. GM raises standing: `/setstanding 500010 140000001 3.0 "Completed mission"`
3. Within 3 seconds, NPCs re-check standings: 3.0 >= 0.0 → **friendly!**
4. NPCs disengage, broadcast `OnTarget("clear")`, fly back to spawn
5. Player is now safe among Serpentis NPCs

---

## Technical Notes

### Thread Safety
- `DestinyManager` uses `ConcurrentDictionary` for entity storage
- NPC AI runs on the same timer thread as physics (no race conditions)
- `StandingDB` queries are synchronous but short-lived

### Performance
- NPC scans happen every 3 seconds, not every tick (reduces DB load)
- Standing queries are single-row lookups with indexed columns
- Entity iteration uses `ConcurrentDictionary` snapshot semantics

### Limitations
- **Damage is not yet implemented** — NPCs engage and orbit but don't apply damage
  (placeholder log message: `[NpcAI] X attacks Y`). Damage would require integration
  with the dogma/effects system.
- **No loot drops** — NPC death/destruction not yet handled
- **No warp scrambling** — `entityWarpScrambleChance` attribute exists but isn't used
- **No drone support** — `entityDroneCount` attribute exists but isn't used
- **No wandering** — Idle NPCs stay at spawn; `entityMaxWanderRange` isn't used yet

### Future Expansion Points
1. **Damage system** — Read weapon attributes, apply DPS via dogma
2. **Bounty payouts** — Read `entityKillBounty`, credit ISK on kill
3. **Standing loss on kill** — Read `entityFactionLoss`, adjust standings
4. **Warp disruption** — Check `entityWarpScrambleChance` per attack tick
5. **Idle patrol** — Use `entityMaxWanderRange` for random movement when idle
6. **Aggro transfer** — NPCs switch targets when receiving damage from other players
