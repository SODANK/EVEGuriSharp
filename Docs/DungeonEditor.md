# Dungeon Editor (GM Tool)

The Dungeon Editor is a built-in EVE Apocrypha client tool that allows Game Masters to design,
preview, and spawn dungeon encounters in space. EVESharp implements the full server-side backend
for this tool.

## Prerequisites

### Account Role

The account must have `ROLE_DUNGEONMASTER` (1073741824). Since GM slash commands also require
`ROLE_ADMIN` (32), grant both:

```sql
UPDATE account SET role = role | 32 | 1073741824 WHERE accountID = <id>;
```

The combined role value for a fresh account is `1073741856`.

### Must Be In Space

The dungeon editor and all spawn operations require the character to be undocked in space.

---

## Opening the Editor

1. Undock into space.
2. Press **Ctrl+Shift+D** to open the Dungeon Editor window.

> **Note:** Ctrl+Alt+D does NOT open the editor. The binding is Ctrl+Shift+D, defined in
> the client's `commandsvc.py`.

---

## Slash Commands

All dungeon operations are also available via `/dungeon` in chat. These use actual room IDs
(visible via `/dungeon list`), unlike the editor UI which uses indices.

| Command | Description |
|---------|-------------|
| `/dungeon list` | List all dungeons with their rooms and IDs |
| `/dungeon create <name> [archetypeID] [factionID]` | Create a new empty dungeon |
| `/dungeon addroom <dungeonID> <roomName>` | Add a room to a dungeon |
| `/dungeon addobject <roomID> <typeID> [x y z]` | Add an object to a room |
| `/dungeon play <dungeonID> [roomID]` | Spawn a room's objects at your position |
| `/dungeon reset` | Despawn all dungeon entities you spawned |

### Archetype IDs

| ID | Name |
|----|------|
| 1 | Combat |
| 2 | Mining |
| 3 | Exploration |
| 4 | Mission |

### Faction IDs

| ID | Name |
|----|------|
| 500001 | Caldari State |
| 500002 | Minmatar Republic |
| 500003 | Amarr Empire |
| 500004 | Gallente Federation |
| 500010 | Serpentis |
| 500011 | Angel Cartel |
| 500012 | Blood Raiders |
| 500013 | Guristas |
| 500014 | Sansha's Nation |

### Example Workflow

```
/dungeon create PirateAmbush 1 500010     -- Creates dungeon [201] "PirateAmbush" (Combat, Serpentis)
/dungeon addroom 201 Entrance             -- Creates room [2001] "Entrance" in dungeon 201
/dungeon addroom 201 BossRoom             -- Creates room [2002] "BossRoom" in dungeon 201
/dungeon addobject 2001 23707 10000 0 0   -- Adds a Serpentis NPC at offset (10000, 0, 0)
/dungeon addobject 2001 12235 0 0 5000    -- Adds a structure at offset (0, 0, 5000)
/dungeon addobject 2002 23707 0 5000 0    -- Adds a boss NPC in the second room
/dungeon list                              -- Verify the dungeon structure
/dungeon play 201 2001                     -- Spawn the Entrance room at your ship's position
/dungeon reset                             -- Clean up all spawned entities
```

After creating a dungeon via slash commands, open Ctrl+Shift+D and it will appear in the
editor's dungeon list.

---

## Editor UI Tabs

### Dungeons Tab
- Filter dungeons by **Archetype** and **Faction** dropdowns.
- Select a dungeon to see its rooms listed below.
- Select a room, then use the action buttons:
  - **Play** -- Spawns the room's objects at your ship's position.
  - **Edit** -- Same as Play but also sets the editing state.
  - **Goto Room** -- Sets the active room for editing.
  - **Reset** -- Despawns all entities you've spawned.

### Room Objects Tab
- Shows objects in the currently selected room.
- Allows selecting objects in the 3D viewport.

### Transform Tab
- Align, distribute, jitter, rotate, and scale selected objects.
- These modify object positions/rotations in the in-memory DungeonData.

### Settings Tab
- Cursor clamping, aggression radius display, free-look camera toggle.

### Palette Tab
- Browse available object types grouped by inventory category.
- Add new objects to the current room from the palette.
- Categories include: Large Collidable Objects, Beacons, Cargo Containers,
  Asteroids (Veldspar, Scordite), Clouds, Billboards, and more.

### Templates Tab
- Save arrangements of selected objects as reusable templates.
- Load templates to stamp object groups into a room.

---

## Architecture

### Server-Side Files

```
Server/EVESharp.Node/Services/Space/
    DungeonData.cs       -- In-memory data store (dungeons, rooms, objects, templates)
    dungeon.cs           -- Data query service (eve.RemoteSvc('dungeon'))
    keeper.cs            -- Level editor provider (eve.RemoteSvc('keeper'))
    LevelEditor.cs       -- Bound service for spawning (one per GM session)
    DestinyEventBuilder.cs -- Builds AddBalls/RemoveBalls destiny updates (shared)
    DestinyBroadcaster.cs  -- Sends DoDestinyUpdate notifications (shared)

Server/EVESharp.Node/Services/Network/
    slash.cs             -- /dungeon command handler

Server/EVESharp.Node/
    Program.cs           -- DI registration (lines 329-331)
    Services/ServiceManager.cs -- Service properties (lines 111-112, 178-179, 239-240)
```

### Client-Side Files (reference, not modified)

```
Tools/evedec/decompiled/eve-6.14.101786/compiled.code/
    ui/inflight/dungeoneditor.py   -- Main editor window UI
    parklife/scenarioMgr.py        -- Scenario service (bridges UI to server)
    parklife/dungeonHelper.py      -- Helper API
    ui/services/commandsvc.py      -- Ctrl+Shift+D hotkey binding
```

### Service Chain

```
Client UI (dungeoneditor.py)
    |
    v
scenarioMgr.py  ------>  eve.RemoteSvc('dungeon')  -- Data queries
    |                         |
    |                         v
    |                     dungeon.cs (Service)
    |                         - DEGetDungeons, DEGetRooms, GetArchetypes, etc.
    |                         - DEGetTemplates, DEGetRoomObjectPaletteData
    |                         - AddObject, RemoveObject, CopyObject
    |                         - EditObjectXYZ, EditObjectYawPitchRoll, EditObjectRadius
    |                         - Template CRUD operations
    |
    +----->  eve.RemoteSvc('keeper').GetLevelEditor()
                  |
                  v
              keeper.cs (ClientBoundService, global)
                  - Creates and binds a LevelEditor instance per GM
                  |
                  v
              LevelEditor.cs (ClientBoundService, bound per character)
                  - Bind()
                  - PlayDungeon(dungeonID, roomIndex)
                  - EditDungeon(dungeonID, roomName=roomIndex)
                  - GotoRoom(roomIndex)
                  - Reset()
                  - GetCurrentlyEditedRoomID()
```

### Data Model

```
DungeonData (Singleton, in-memory, ConcurrentDictionary-backed)
    |
    +-- Dungeons: {DungeonID -> DungeonDefinition}
    |       DungeonID, DungeonName, FactionID, ArchetypeID, List<RoomID>
    |
    +-- Rooms: {RoomID -> RoomDefinition}
    |       RoomID, DungeonID, RoomName, ShortName, List<ObjectID>
    |
    +-- Objects: {ObjectID -> DungeonObject}
    |       ObjectID, RoomID, ObjectName, TypeID, X/Y/Z, Yaw/Pitch/Roll, Radius, IsLocked
    |
    +-- Templates: {TemplateID -> DungeonTemplate}
    |       TemplateID, TemplateName, Description, UserID, UserName, List<ObjectID>
    |
    +-- Archetypes: {ArchetypeID -> ArchetypeEntry}
    +-- Factions: {FactionID -> FactionEntry}
    |
    +-- EditingRooms: {CharacterID -> RoomID}       (per-GM state)
    +-- SpawnedEntities: {CharacterID -> List<ItemID>}  (tracks what to clean up)
```

All data is **in-memory only** and resets on server restart. The sample data (two dungeons,
one template) is populated in `DungeonData.PopulateSampleData()`.

### ID Ranges

| Type | Starting ID | Allocated By |
|------|-------------|--------------|
| Dungeon | 200+ | `NextDungeonID()` |
| Room | 2000+ | `NextRoomID()` |
| Object | 20000+ | `NextObjectID()` |
| Template | 500+ | `NextTemplateID()` |
| Sample Dungeons | 101-102 | Hardcoded |
| Sample Rooms | 2001-2003 | Hardcoded |
| Sample Objects | 20001-20006 | Hardcoded |

---

## Technical Notes

### Room Index vs Room ID

The client's dungeon editor UI sends a **1-based list index** as the room identifier, not the
actual `roomID` from the server data. When you select the first room in the list, the client
sends `room=1`, not `room=2002`.

`LevelEditor.ResolveRoomID()` handles this translation:
1. If the value matches an actual roomID in the database, use it directly.
2. Otherwise, treat it as a 1-based index into the dungeon's room list.
3. Fall back to the first room if the index is out of range.

The `/dungeon play` slash command uses actual room IDs (not indices).

### Named Argument (kwargs) Limitation

`Service.FindSuitableMethod()` has a `break` after matching the first named argument, so
methods called with **multiple kwargs** from the client must use a zero-arg C# signature and
read from `call.NamedPayload` manually. This affects:
- `DEGetDungeons` (archetypeID, factionID, dungeonID)
- `DEGetRooms` (dungeonID)
- `EditObjectXYZ` (objectID, x, y, z)
- `EditObjectYawPitchRoll` (objectID, yaw, pitch, roll)
- `EditObjectRadius` (objectID, radius)

### Rowset Format

`DEGetTemplates` returns a `util.Rowset` with `RowClass = new PyToken("util.Row")`. Using
`PyString` instead of `PyToken` crashes the client because the Python unmarshaler keeps it as
a string, and `Rowset.__getitem__` tries to call `self.RowClass(header, line)` which fails
with `TypeError: 'str' object is not callable`.

### Entity Spawning Pattern

Objects are spawned using the same pattern as `/spawn`:
1. `DogmaItems.CreateItem<ItemEntity>()` to create the database item.
2. Set position (player position + object offset) and `Persist()`.
3. Create `BubbleEntity` with appropriate mode/flags.
4. `DestinyManager.RegisterEntity()` to add to server tracking.
5. `DestinyEventBuilder.BuildAddBalls()` + `SendNotification("DoDestinyUpdate")` to
   broadcast to clients so the entity actually renders.

Step 5 is critical -- without the AddBalls broadcast, entities exist server-side but are
invisible to clients. Cleanup uses `RemoveBalls` in the same way.

### Bound Service Binding

`keeper.GetLevelEditor()` manually creates and binds the `LevelEditor` instance:
1. `new LevelEditor(manager, session, charID, ...)` -- uses the auto-binding constructor
   `base(manager, session, objectID)` which calls `manager.BindService()`.
2. Constructs `BoundServiceInformation` as `PyTuple(2) { boundString, guid }`.
3. Sets `call.ResultOutOfBounds["OID+"]` for the client's OID tracking.
4. Returns `PySubStruct(PySubStream(boundInfo))`.

The client then calls `ed.Bind()` on the returned object (a no-op on the server side).

---

## Sample Data

The server ships with two pre-built dungeons for testing:

### Dungeon 101: Asteroid Field Alpha
- Archetype: Mining (2)
- Faction: None
- **Room 2001 "Main Field"** (3 objects):
  - Veldspar Asteroid 1 (typeID 1230) at offset (5000, 0, 0)
  - Veldspar Asteroid 2 (typeID 1230) at offset (-3000, 1000, 2000)
  - Scordite Asteroid (typeID 1232) at offset (0, -2000, 4000)

### Dungeon 102: Serpentis Hideout
- Archetype: Combat (1)
- Faction: Serpentis (500010)
- **Room 2002 "Entrance"** (2 objects):
  - Serpentis Sentry (typeID 23707) at offset (10000, 0, 0)
  - Serpentis Bunker (typeID 12235) at offset (0, 0, 5000)
- **Room 2003 "Boss Chamber"** (1 object):
  - Serpentis Commander (typeID 23707) at offset (0, 5000, 0)

### Template 501: Basic Asteroid Cluster
- Contains object references to the 3 asteroids from room 2001.

---

## Limitations

- **In-memory only.** All dungeon data is lost on server restart. There is no database
  persistence layer for dungeon definitions.
- **No client UI for dungeon/room creation.** The Apocrypha client's editor can only browse
  and modify existing dungeons. New dungeons and rooms must be created via `/dungeon create`
  and `/dungeon addroom` slash commands.
- **Single-node only.** The `DungeonData` singleton lives in the Node process. Multi-node
  cluster deployments would need shared state (not implemented).
- **No dungeon deletion via slash.** Dungeons and rooms can be created but not deleted through
  commands. They will naturally disappear on restart.
- **Object offsets are relative.** When a room is played/spawned, object positions are offsets
  from the player's current ship position, not absolute coordinates.
