# EVE Client Space Rendering: Server-Side Architecture

How the EVE Apocrypha client bootstraps a 3D space environment from server-provided data, and how EVESharp's server-side components cooperate to make it happen.

---

## Table of Contents

1. [High-Level Flow: Station to Space](#1-high-level-flow-station-to-space)
2. [Component Breakdown](#2-component-breakdown)
   - [ship.Undock()](#shipundock---the-trigger)
   - [michelle](#michelle---the-client-side-coordinator)
   - [beyonce](#beyonce---the-ballpark-authority)
   - [Ballpark](#ballpark---entity-registry)
   - [Destiny System](#destiny-system---physics--binary-encoding)
3. [The DoDestinyUpdate Notification](#3-the-dodestinyupdate-notification)
4. [Destiny Binary Format (Apocrypha)](#4-destiny-binary-format-apocrypha)
5. [Bubble System](#5-bubble-system)
6. [Movement Commands](#6-movement-commands)
7. [Incremental Updates vs Full State](#7-incremental-updates-vs-full-state)
8. [Data Flow Diagram](#8-data-flow-diagram)

---

## 1. High-Level Flow: Station to Space

When a player clicks "Undock" in the EVE client, the following sequence transforms a station UI into a fully rendered 3D space scene:

```
STATION UI                                               3D SPACE
   |                                                        ^
   | 1. Player clicks "Undock"                               |
   v                                                        |
ship.Undock() on server                                     |
   |                                                        |
   | 2. Session change: stationID=None, solarSystemID=X     |
   v                                                        |
Client gameui.OnSessionChanged()                            |
   |                                                        |
   | 3. GoInflight -> michelle.AddBallpark(ssid)             |
   v                                                        |
michelle.py creates native destiny.Ballpark                 |
   |                                                        |
   | 4. Park.__init__() -> moniker.GetBallPark(ssid)         |
   |    -> Moniker('beyonce', ssid)                          |
   v                                                        |
MachoBindObject -> server creates BOUND beyonce             |
   |                                                        |
   | 5. beyonce bound constructor:                           |
   |    - Builds Ballpark with ship + station + celestials   |
   |    - Encodes Destiny binary (all balls)                 |
   |    - Sends DoDestinyUpdate notification                 |
   v                                                        |
Client receives DoDestinyUpdate                             |
   |                                                        |
   | 6. Park.SetState(bag):                                  |
   |    - Parses destiny binary -> creates native balls      |
   |    - Reads slims -> attaches metadata to balls          |
   |    - DoBallsAdded -> 3D scene graph populated           |
   v                                                        |
eve.RemoteSvc('beyonce').GetFormations()                     |
   |                                                        |
   | 7. LoadFormations -> Park.Start() -> tick loop begins   |
   +--------------------------------------------------------+
```

**Key insight:** The client's space rendering engine (Trinity) does not receive 3D models or textures from the server. It receives:
- **Ball IDs** with positions and physics parameters (the destiny binary)
- **Slim items** with typeID/groupID metadata (tells the client WHICH model to load)

The client already has all 3D models, textures, and shaders locally. The server only tells it "object X of type Y is at position Z with velocity W."

---

## 2. Component Breakdown

### `ship.Undock()` - The Trigger

**File:** `EVESharp.Node/Services/Inventory/ship.cs:193`

The undock RPC is the entry point that transitions a character from station to space. It does NOT send any space state directly. Instead, it sets up the conditions for the client to request state on its own.

**Responsibilities:**
1. **Lookup station** - Gets the station's solar system, constellation, and region IDs
2. **Position the ship** - Sets the ship entity's coordinates to the undock point (station position + offset)
3. **Store undock context** - Saves the station ID via `SolarSystemDestinyMgr.SetUndockStation()` because the session change will clear StationID before beyonce runs
4. **Session change** - Sets `stationID = None`, `solarSystemID = X`, which the client interprets as "I'm now in space"

```
Session delta:
  stationID       = PyNone     (no longer in station)
  locationID      = solarSysID
  solarSystemID   = solarSysID
  solarSystemID2  = solarSysID
  constellationID = constID
  regionID        = regionID
  shipID          = shipID     (unchanged, carried forward)
```

**Critical design decision:** DoDestinyUpdate is NOT sent from `ship.Undock()`. Early attempts to send it here failed because the client's ballpark doesn't exist yet at this point - the client creates it in response to the session change. Sending state before the ballpark exists causes `RuntimeError: No ballpark for update` in `michelle.py`.

---

### `michelle` - The Client-Side Coordinator

**File:** `EVESharp.Node/Services/Space/michelle.cs`
**Service name:** `[ConcreteService("michelle")]`

Michelle is architecturally unusual: the heavy lifting happens **client-side** in `michelle.py`, while the server-side michelle service is an intentionally thin stub.

**What client-side michelle.py does (decompiled behavior):**
1. Receives the session change from `ship.Undock()`
2. `gameui.OnSessionChanged()` calls `GoInflight()` which calls `michelle.AddBallpark(solarSystemID)`
3. `AddBallpark()` creates a native `destiny.Ballpark` C++ object
4. The Ballpark's `__init__()` calls `moniker.GetBallPark(ssid)` which resolves to a `Moniker('beyonce', ssid)`
5. The moniker binding triggers `MachoBindObject` on the server, creating a bound beyonce instance
6. After binding, client calls `eve.RemoteSvc('beyonce').GetFormations()`
7. Loads formations, then calls `Park.Start()` to begin the physics tick loop
8. Queued `DoDestinyUpdate` notifications are processed in `DoPreTick()` after `Start()`

**What server-side michelle does:**
- `AddBallpark(solarSystemID)` - Updates session fields (`ballparkID`, `ballparkBroker = "beyonce"`)
- `GetBallpark(solarSystemID)` - Returns a `util.KeyVal{nodeID, service="beyonce", objectID}` descriptor pointing to the beyonce service
- `GetInitialState()` - Returns empty dict (state comes via DoDestinyUpdate notification, not this RPC)
- `DoDestinyUpdate()` - Debug stub; logs if the client ever calls this as an RPC instead of receiving it as a notification
- `AddBalls()` / `AddBalls2()` - No-op stubs; the client sometimes sends ball data back to the server

**Why is michelle so thin?** In EVE's architecture, the client runs its own destiny physics simulation. The server is authoritative for state, but the client predicts locally. Michelle is the client-side orchestrator for this local simulation.

---

### `beyonce` - The Ballpark Authority

**File:** `EVESharp.Node/Services/Space/beyonce.cs`
**Service name:** `[ConcreteService("beyonce")]`

Beyonce is the central service for space gameplay. It serves as both a **global** service (for `GetFormations()`) and a **bound** per-player service (for all ballpark operations). The name "beyonce" is the original CCP internal codename for the ballpark service.

#### Global Service (Unbound)

Created once at server startup. Handles:
- `GetFormations()` - Returns ship formation data (empty tuple in Apocrypha - formations were unused)

#### Bound Service (Per Player)

Created when the client binds via `Moniker('beyonce', solarSystemID)`. The bound constructor is where all the critical space setup happens:

```csharp
// Bound constructor flow:
1. Retrieve undock station ID (saved by ship.Undock before session cleared it)
2. Get or create DestinyManager for this solar system
3. Create Ballpark instance for this player
4. Add player's ship entity to ballpark + register as BubbleEntity
5. Add undock station entity to ballpark + register as BubbleEntity
6. Load ALL celestials in the solar system (sun, planets, moons, belts, gates, stations)
7. Send DoDestinyUpdate notification immediately
```

#### Client-Callable Methods on Bound beyonce

| Method | Purpose |
|--------|---------|
| `UpdateStateRequest()` | Resync - client calls during `Park.RequestReset()` |
| `GetInitialState()` | Alias for `UpdateStateRequest()` |
| `Stop()` | Stop ship movement |
| `FollowBall(ballID, range)` | Approach/follow another entity |
| `Orbit(entityID, range)` | Orbit another entity |
| `AlignTo(entityID)` | Align ship toward entity |
| `GotoDirection(x, y, z)` | Fly in a direction vector |
| `SetSpeedFraction(fraction)` | Set throttle (0.0 to 1.0) |
| `WarpToStuff(type, itemID)` | Initiate warp to an entity |
| `WarpToStuffAutopilot(itemID)` | Autopilot warp variant |
| `Dock(stationID)` | Dock at a station (reverse of undock) |
| `StargateJump(fromID, toID)` | Jump through stargate |
| `TeardownBallpark()` | Clean up when leaving space |

#### Snapshot Building

Beyonce's `BuildSnapshot()` constructs the complete state bag that the client's `Park.SetState()` expects:

```
util.KeyVal bag:
  aggressors     = {}                    (empty dict - no combat yet)
  droneState     = util.Rowset(empty)    (no drones deployed)
  solItem        = util.KeyVal           (solar system metadata)
  state          = PyBuffer              (destiny binary - the critical part)
  ego            = int                   (player's ship ball ID)
  slims          = [util.KeyVal, ...]    (metadata for each ball)
  damageState    = {shipID: (hp, stamp, repairing)}
  effectStates   = []                    (no active effects)
  allianceBridges = []                   (no jump bridges)
```

**Critical rule:** The solar system goes in `solItem` ONLY, never in `slims`. The client's `SetState()` iterates `slims` and checks if each `itemID` exists in `self.balls` (the native destiny ball map). The destiny binary parser does not create a ball for the solar system, so including it in slims causes `BallNotInPark` error.

---

### `Ballpark` - Entity Registry

**File:** `EVESharp.Node/Services/Space/Ballpark.cs`

A lightweight per-player container that tracks which `ItemEntity` objects should be visible to a specific character. It is NOT a physics simulation - it's a registry.

```csharp
public class Ballpark
{
    int SolarSystemID;
    int OwnerID;
    BubbleManager BubbleManager;
    Dictionary<int, ItemEntity> Entities;  // itemID -> entity

    void AddEntity(ItemEntity entity);
    BubbleEntity AddEntityWithBubble(ItemEntity entity, BubbleEntity bubbleEntity);
    bool TryGetEntity(int itemID, out ItemEntity ent);
}
```

**What goes into a Ballpark:**
- Player's ship
- Undock station
- All celestials in the solar system (sun, planets, moons, asteroid belts, stargates, stations)
- Eventually: other players' ships, NPCs, wrecks, containers, etc.

**What does NOT go into a Ballpark:**
- The solar system itself (that goes in `solItem` only)
- Items in other solar systems

---

### Destiny System - Physics & Binary Encoding

The Destiny system spans multiple files across two projects:

#### EVESharp.Destiny (Library)

| File | Purpose |
|------|---------|
| `DestinyBinaryEncoder.cs` | Serializes Ball lists into the Apocrypha wire format |
| `BallHeader.cs` | 38-byte ball header: ID, mode, radius, position, flags |
| `ExtraBallHeader.cs` | 25-byte extra header: mass, cloak, harmonic, corp/alliance |
| `BallData.cs` | 72-byte movement data: velocity, agility, speed fraction |
| `Vector3.cs` | 3D vector (3 doubles, 24 bytes) with math operations |
| `BubbleEntity.cs` | Mutable runtime entity with full movement state |
| `BubbleManager.cs` | Spatial partitioning into 500km-radius bubbles |
| `SystemBubble.cs` | Individual bubble: entity set + player tracking |
| Various state structs | `FollowState`, `WarpState`, `GotoState`, `OrbitState`, etc. |

#### EVESharp.Node/Services/Space (Server Logic)

| File | Purpose |
|------|---------|
| `DestinyManager.cs` | Per-solar-system tick loop (1 Hz), processes movement |
| `SolarSystemDestinyManager.cs` | Singleton registry, creates DestinyManagers on demand |
| `DestinyBroadcaster.cs` | Sends DoDestinyUpdate notifications via `solarsystemid2` routing |
| `DestinyEventBuilder.cs` | Builds individual event tuples (GotoPoint, Orbit, WarpTo, etc.) |
| `DestinyBallBuilder.cs` | Convenience factory for creating Ball structs from ItemEntity |

---

## 3. The DoDestinyUpdate Notification

This is the single most important data structure for space rendering. It tells the client everything it needs to create the 3D scene.

### Wire Format

```
PyTuple(3):
  [0] = events: PyList of (stamp, (methodName, (args...)))
  [1] = waitForBubble: PyBool (false)
  [2] = dogmaMessages: PyList (empty)
```

### SetState Event (Full Snapshot)

```
events = [
  (stamp, ('SetState', (util.KeyVal{
    aggressors:     {},
    droneState:     util.Rowset{...},
    solItem:        util.KeyVal{itemID, typeID=5, groupID=5, ...},
    state:          PyBuffer(destiny_binary_bytes),
    ego:            shipID,
    slims:          [util.KeyVal{itemID, typeID, groupID, ...}, ...],
    damageState:    {shipID: ((shield, armor, hull), stamp, repairing)},
    effectStates:   [],
    allianceBridges: []
  },)))
]
```

### Delivery Mechanism

DoDestinyUpdate is delivered as a **NOTIFICATION** packet, not an RPC response. It's broadcast via `solarsystemid2` routing, meaning all characters whose session has `solarSystemID2 = X` will receive it.

```
NotificationSender.SendNotification(
    "DoDestinyUpdate",      // notification type
    "solarsystemid2",       // routing key type
    solarSystemID,          // routing key value
    notificationData        // the PyTuple(3)
)
```

### Stamp Calculation

The stamp is critical - it must be > 0 or the client's `FlushState()` silently drops the event:

```csharp
long eveEpoch = new DateTime(2003, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
int stamp = (int)((DateTime.UtcNow.Ticks - eveEpoch) / 10000000 % int.MaxValue);
```

This produces seconds since EVE's epoch (Jan 1, 2003 UTC).

---

## 4. Destiny Binary Format (Apocrypha)

The `state` field in the SetState bag contains a binary blob that the client's native C++ destiny engine parses directly. This is NOT a Python-marshaled structure - it's a raw binary protocol.

### Packet Structure

```
[Header: 5 bytes]
  packetType: byte     (0 = full state, 1 = incremental)
  stamp:      int      (4 bytes, EVE epoch seconds)

[Ball 1]
  [BallHeader: 38 bytes]
    ItemId:   int      (4 bytes - entity/ball ID)
    Mode:     byte     (BallMode enum)
    Radius:   double   (8 bytes - Apocrypha uses double, NOT float!)
    Location: Vector3  (24 bytes - 3 doubles: X, Y, Z)
    Flags:    byte     (BallFlag enum)

  [ExtraBallHeader: 25 bytes, ONLY if Mode != Rigid]
    Mass:          double  (8 bytes - FIRST in Apocrypha)
    CloakMode:     byte    (1 byte)
    Harmonic:      ulong   (8 bytes - often 0xFFFFFFFFFFFFFFFF)
    CorporationId: int     (4 bytes)
    AllianceId:    int     (4 bytes)

  [BallData: 72 bytes, ONLY if IsFree flag is set]
    MaxVelocity:   double  (8 bytes)
    Velocity:      Vector3 (24 bytes)
    UnknownVec:    Vector3 (24 bytes - acceleration vector?)
    Agility:       double  (8 bytes)
    SpeedFraction: double  (8 bytes - throttle 0.0-1.0)

  [FormationId: 1 byte, ALWAYS present]
    0xFF = no formation

  [Mode-specific state, variable size]
    Stop/Rigid/Field: 0 bytes
    Follow/Orbit:     FollowState (12 bytes: int followId + double range)
    Goto:             GotoState (24 bytes: Vector3 target)
    Warp:             WarpState (44 bytes: Vector3 + int + double + int + int)
    Missile:          MissileState (44 bytes)
    Formation:        FormationState (16 bytes)
    Troll:            TrollState (4 bytes)
    Mushroom:         MushroomState (24 bytes)

  [MiniBalls, ONLY if HasMiniBalls flag set]
    count: short
    [MiniBall] * count: Vector3 offset + double radius = 32 bytes each

  [Name: variable, Apocrypha only]
    nameWords: byte    (0 = no name)
    chars:     nameWords * 2 bytes (Unicode)

[Ball 2]
  ... same structure ...

[Ball N]
  ...
```

### Ball Modes

```
Rigid     = static object (station, planet) - no ExtraBallHeader, no BallData
Stop      = stationary but movable (ship at rest)
Goto      = flying to a point
Follow    = approaching another entity
Orbit     = orbiting another entity
Warp      = warping to destination
Missile   = missile in flight
Field     = area effect
Troll     = tractor beam / web
Mushroom  = smartbomb / AOE expanding sphere
Formation = fleet formation movement
```

### Ball Flags

```
IsFree        = has BallData (velocity, throttle)
IsMassive     = has mass, affected by physics
IsInteractive = player can interact with it
IsGlobal      = visible system-wide (not just in bubble)
HasMiniBalls  = has sub-collision spheres
```

### Typical Undock Example

A player undocking produces 2+ balls minimum (ship + station), plus all celestials:

**Player ship (ego):**
- BallHeader (38) + ExtraBallHeader (25) + BallData (72) + FormationId (1) + Name (1) = **137 bytes**
- Mode = Stop, Flags = IsFree | IsMassive | IsInteractive

**Station:**
- BallHeader (38) + FormationId (1) + Name (1) = **40 bytes**
- Mode = Rigid, Flags = IsGlobal | IsMassive

**Celestials (planets, moons, etc.):**
- Same structure as station, 40 bytes each, Mode = Rigid

**Total for header + ship + station:** 5 + 137 + 40 = **182 bytes** (plus celestials)

---

## 5. Bubble System

EVE partitions space into "bubbles" - spherical regions 500 km in radius. Entities within the same bubble can see each other and receive each other's destiny updates.

### SystemBubble

```
Radius: 500,000 meters (500 km)
Contains: Dictionary<int, BubbleEntity>
Tracks: which characters (players) are in the bubble
```

### BubbleManager

Manages all bubbles within a solar system:
- **AddEntity** - Finds an existing bubble containing the position, or creates a new one centered on the entity
- **RemoveEntity** - Removes from bubble, cleans up empty bubbles
- **UpdateEntityBubble** - Called every tick; if an entity has moved out of its bubble, removes and re-adds to the correct bubble
- **GetBubbleForEntity** - Lookup which bubble an entity is in

### BubbleEntity

The mutable runtime representation of an entity in space. Unlike `ItemEntity` (which is a database-backed record), `BubbleEntity` has live physics state:

```
Position, Velocity         (mutable Vector3)
Mode                       (Stop, Goto, Follow, Orbit, Warp, ...)
MaxVelocity, SpeedFraction (throttle)
Agility, Mass, Radius      (physics parameters)
FollowTargetID, FollowRange (for Follow/Orbit modes)
GotoTarget                 (for Goto mode)
WarpTarget, WarpEffectStamp (for Warp mode)
```

---

## 6. Movement Commands

When the player issues a movement command (right-click > Approach, orbit, warp, etc.), the client calls a method on the bound beyonce service. Beyonce delegates to `DestinyManager`, which:

1. **Enqueues** the command (thread-safe via `ConcurrentQueue`)
2. **Next tick** (1 second later): dequeues and applies the command
3. **Broadcasts** a DoDestinyUpdate with the appropriate event (GotoPoint, FollowBall, Orbit, etc.)
4. **Processes physics** each tick until the movement completes

### DestinyManager Tick Loop

```
Every 1 second:
  1. Drain all pending commands from the queue
  2. For each non-rigid entity with speedFraction > 0:
     - Calculate new velocity based on mode (goto/follow/orbit/warp/stop)
     - Update position: pos += vel * dt
     - Check arrival conditions (stop if close enough)
     - Check bubble transitions (has entity moved to a different bubble?)
```

### Movement Modes

| Mode | Behavior |
|------|----------|
| **Stop** | Decelerate (halve velocity each tick), stop when < 1 m/s |
| **Goto** | Fly toward a point, stop within 15km |
| **Follow** | Fly toward another entity, slow down within range |
| **Orbit** | Approach to orbit range, then rotate perpendicular |
| **Warp** | Move at 3 AU/s toward target, stop on arrival |

---

## 7. Incremental Updates vs Full State

### Full State (SetState)

Sent when:
- Player first undocks (beyonce bound constructor)
- Player requests resync (`UpdateStateRequest`)
- Player jumps to a new system

Contains the ENTIRE ballpark: all balls, all slims, all metadata.

### Incremental Updates

Sent during gameplay when things change:
- `GotoPoint` - a ship started flying somewhere
- `FollowBall` - a ship started approaching
- `Orbit` - a ship started orbiting
- `SetSpeedFraction` - throttle changed
- `WarpTo` - a ship entered warp
- `AddBalls` - new entities entered the bubble (with destiny binary + slims)
- `RemoveBalls` - entities left the bubble

Each incremental update is a `(stamp, (methodName, args))` tuple delivered via the same `DoDestinyUpdate` notification mechanism.

---

## 8. Data Flow Diagram

```
+-------------------+
|   EVE Client      |
|                   |
|  +-----------+    |        +-------------------+
|  | gameui    |    |        |   EVESharp Server  |
|  +-----------+    |        |                    |
|       |           |        |  +-----------+     |
|       v           |  RPC   |  | ship      |     |
|  OnSessionChanged |<-------|--| .Undock()  |     |
|       |           |        |  +-----------+     |
|       v           |        |       |            |
|  GoInflight()     |        |   session change   |
|       |           |        |   + save station   |
|       v           |        |                    |
|  +-----------+    |        |  +-----------+     |
|  | michelle  |    |  RPC   |  | michelle  |     |
|  | .py       |----|------->|  | (stub)    |     |
|  +-----------+    |        |  +-----------+     |
|       |           |        |       |            |
|  AddBallpark()    |        |  session update    |
|  creates native   |        |  (ballparkBroker   |
|  destiny.Ballpark |        |   = "beyonce")     |
|       |           |        |                    |
|       v           |        |  +-----------+     |
|  Park.__init__()  |  BIND  |  | beyonce   |     |
|  GetBallPark() -->|------->|  | (bound)   |     |
|  Moniker.Bind()   |        |  +-----------+     |
|       |           |        |       |            |
|       |           |        |  +----v--------+   |
|       |           |        |  | Ballpark    |   |
|       |           |        |  | (entities)  |   |
|       |           |        |  +-------------+   |
|       |           |        |       |            |
|       |           |        |  +----v--------+   |
|       |           |        |  | Destiny     |   |
|       |           |        |  | BinaryEnc.  |   |
|       |           |        |  +-------------+   |
|       |           |        |       |            |
|       |           |  NOTIF |  +----v--------+   |
|       |<----------|--------|  | Broadcaster |   |
|       |           |        |  +-------------+   |
|       v           |        |                    |
|  DoDestinyUpdate  |        |  +-----------+     |
|  received         |        |  | Destiny   |     |
|       |           |        |  | Manager   |     |
|       v           |        |  | (tick 1Hz)|     |
|  Park.SetState()  |        |  +-----------+     |
|       |           |        |       |            |
|       v           |        |  +----v--------+   |
|  Parse destiny    |        |  | Bubble      |   |
|  binary -> balls  |        |  | Manager     |   |
|       |           |        |  +-------------+   |
|       v           |        |                    |
|  DoBallsAdded()   |        +-------------------+
|       |           |
|       v           |
|  +-----------+    |
|  | Trinity   |    |
|  | 3D Engine |    |
|  | renders   |    |
|  | scene     |    |
|  +-----------+    |
+-------------------+
```

### How the Client Decides What to Render

The client does NOT receive 3D models from the server. The pipeline is:

1. **Server sends:** `slimItem{typeID=587, groupID=25}` + destiny ball at position (X,Y,Z)
2. **Client looks up:** typeID 587 = Rifter (Minmatar Frigate)
3. **Client loads locally:** Rifter 3D model, textures, shader, LOD levels
4. **Client places:** model at position (X,Y,Z) with the ball's velocity/rotation
5. **Client animates:** thruster effects based on `speedFraction`, turret tracking, etc.

The `typeID` in the slim item is the bridge between server state and client rendering. Every ship, station, planet, and effect in EVE has a typeID that maps to local art assets.

### What the Client Receives vs What It Already Has

| From Server | Already on Client |
|-------------|-------------------|
| Ball ID + position | 3D models for every typeID |
| typeID / groupID | Textures and materials |
| Velocity vector | Shader programs |
| Speed fraction | Particle effects |
| Ball mode (Stop/Goto/Warp/...) | Sound effects |
| Owner/Corp/Alliance IDs | UI elements (brackets, overview) |
| Damage state (shield/armor/hull %) | Skybox / nebula backgrounds |
| Ship name | Station interiors |

---

## Appendix: ballparkSvc (Disabled)

**File:** `EVESharp.Node/Services/Space/ballparkSvc.cs`
**Service name:** `[ConcreteService("ballparkSvc_disabled")]`

This was an earlier attempt at the ballpark service, before all functionality was consolidated into beyonce. It is kept for reference but is NOT registered in the DI container. Key differences from beyonce:

- Only encoded the player's ship (no station, no celestials)
- Used stamp = 0 (caused client to silently drop state)
- Had no DestinyManager integration (no physics tick loop)
- Had no celestial loading
- Different `droneState` format (`rowClass` instead of `RowClass`)
