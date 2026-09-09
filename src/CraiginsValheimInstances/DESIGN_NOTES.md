# Instanced dungeons

Temporary, procedurally generated dungeons that exist outside the authored world, are entered
through a portal, and are destroyed when they are finished with. The long-term purpose is quests:
kill something in the open world, it drops a unique key, the key opens a dungeon that promises
loot, and something bad happens if you never clear it.

This document is the design. `docs/GAME_CONSTANTS.md` holds the extracted engine values it leans
on; anything asserted here about vanilla behaviour is verifiable there or in the cited source.

---

## 1. The one idea the whole design rests on

**Valheim partitions the world by XZ and never by height.**

`ZDO.SetSector` derives a ZDO's sector from `ZoneSystem.GetZone(pos)`, which reads `x` and `z` and
throws `y` away. So do `ZNetScene.InActiveArea`, `ZNetScene.OutsideActiveArea` and
`ZDOMan.FindSectorObjects`. Nothing anywhere partitions by altitude.

Two consequences drive everything below:

- **Altitude gives isolation in space but not in loading.** Parking an instance 20 km above a
  player's current zone does guarantee it can never overlap a real dungeon - but it puts the
  instance in that zone's sector, so every other player crossing that patch of ground
  instantiates the whole thing. Height alone is the wrong answer.
- **A separate XZ zone gives real isolation.** One instance per zone, in a region nobody walks
  through, means the instance shares a sector with nothing. Reaping becomes "destroy this
  sector's contents" instead of surgery next to objects that matter.

So instances go in **their own XZ zone, at altitude**. XZ for isolation, altitude for
`Character.InInterior()` - a bare `pos.y > 3000f`, which is what suppresses raids
(`RandEventSystem` skips `y > 3000`), gives the interior environment, and switches off
`RenderGroup.Overworld` so the ocean below isn't drawn under the floor.

## 2. Address space

Instances live in a square region of zones just outside the +-10,500 m playable circle, at zones
176-255 on both axes.

**There is a hard ceiling on how far out that region may sit, and it is easy to miss.** Before
Valheim 1.0 there was none - zone IDs were `Vector2i` and sectors were keyed by the raw pair, so
the region sat at zone 2000 on the "further away is safer" theory. 1.0 replaced zone IDs with
`Vector2s` and, more importantly, started addressing sectors through
`ZoneSystem.SectorToIndex`, which packs a zone into one uint as `(y + 256) * 512 + (x + 256)`
and returns **index 0 for anything outside +-256**. Index 0 is `SectorZero`, the sentinel
`ZDO.SetSector` stamps on out-of-bounds objects (`OutsideZones = sectorIndex.Sector == 0`). A
region at zone 2000 therefore puts every instance in one shared sector alongside every genuinely
out-of-bounds ZDO in the world - destroying the isolation property in section 1 that the whole
design rests on, and handing the reaper other people's objects via `FindSectorObjects`.

So the region is bounded on both sides: past zone ~164 to clear the playable world, and inside
zone 256 to stay addressable. Zones 176-255 satisfy both with ~750 m of margin at the world edge.
`InstanceRegion.RegionOrigin + RegionSize` must stay `<= 256`; nothing fails loudly if it doesn't.

**The zone is derived from the instance id, not allocated from a list.**

```
zone = regionOrigin + hash(instanceId) mod regionSize, linear probe on collision
```

A free-list would have to be rebuilt at every server start by scanning for live instances. A
derived mapping is stateless: the same id lands in the same zone forever, and the only question
ever asked is "does a live instance already claim this zone", which the instance record answers.
With an 80 x 80 region that is 6,400 slots against a handful of concurrent instances, so the
probe effectively never runs.

Altitude is a single constant well above the vanilla interior band at `location.y + 5000`. Above
`y = 10,000` there is a small bonus: `ZoneSystem.GetGroundData` raycasts from `p + up*5000` for
10,000 m, so it misses everything and any `SnapToGround` in a room prefab becomes inert instead of
yanking props down to the sea floor.

## 3. Spawning

The instance is a real `Location`, spawned through the game's own path:

```csharp
DungeonGenerator.m_forceSeed = seed;                       // consumed once by GetSeed()
ZoneSystem.instance.SpawnLocation(loc, seed, pos, rot, SpawnMode.Full, spawned);
```

Facts that make this safe, all verified in `ZoneSystem.SpawnLocation`:

- It performs **no terrain modification**. `m_clearArea` is applied in `PlaceLocations`
  (ZoneSystem.cs:1791), not here, so spawning at altitude touches no heightmap.
- It instantiates the location's `ZNetView` children at `pos + rot * localPos` and calls
  `DungeonGenerator.Generate(mode)` on the generator child, which resolves its seed through
  `GetSeed()` - and `GetSeed()` consumes the public static `m_forceSeed` if set. That is exactly
  how vanilla's own `dungeonseed` console command works (Terminal.cs:1200).
- It creates a `LocationProxy` so clients can reconstruct the exterior. That proxy is one more
  ZDO in the instance zone and is swept with everything else.
- It does **not** touch `m_locationInstances`, so nothing about the placement is recorded in
  persistent world-generation state.

**Never use `ZoneSystem.TestSpawnLocation`** (the `spawnlocation` console command). It sets
`m_didZoneTest`, which makes `ZoneSystem.SkipSaving()` return true - world saving is silently
disabled until the process restarts, with only a MessageHud line as warning.

### Generation overrides

`m_themes` is the big lever for variety: `SetupAvailableRooms()` runs inside `Generate()` and
selects rooms with `(room.m_theme & m_themes) != None`, so one location can produce crypt, cave,
dvergr or goblin content. With `m_minRooms`/`m_maxRooms` as a size dial, quest variety becomes
theme x seed x size x boss x loot table, with no new art.

**As implemented**, the overrides are written onto the shared prefab asset immediately before
`SpawnLocation` and restored in a `finally`. This is unlovely - it mutates a shared asset - but
`SpawnLocation` gives no window between instantiating the generator and calling `Generate` on it,
and it is the same save-mutate-restore pattern `SpawnLocation` itself uses on the asset's transform
and `m_interiorTransform`. The consequence to respect is that it must not run concurrently with
ordinary zone generation.

**The durable form** is Jotunn's `ZoneManager.CreateClonedLocation(name, baseName)`, which clones a
vanilla location wholesale - exterior, interior prefab, wired `DungeonGenerator`,
`m_useCustomInteriorTransform` - with `LocationConfig.Quantity = 0` so world generation never
places it naturally. The clone is then ours to modify permanently, and the shared-asset mutation
goes away. Cloning has to happen during `ZoneManager.OnVanillaLocationsAvailable`, which Jotunn
raises inside `RegisterLocations(ZoneSystem)` immediately before it injects everything in its
`Locations` dictionary, so a location added during the event is picked up by that same pass. This
is the first thing to do when variants become named content rather than console arguments.

## 4. Lifecycle

```
   Open --> Armed --> (occupied) --> Cleared --> Empty --> Reaped
              ^                                     |
              +------------ re-entry ---------------+
```

- **Open** - zone derived, location spawned, generated, contents marked. Nobody inside yet.
- **Armed** - set the first time a player is *observed* inside.
- **Cleared** - the objective predicate has been satisfied. **Latched**: once true it stays true.
- **Reaped** - destroyed. Requires `armed && empty && cleared`, or expiry.

### Occupancy is derived, never counted

The obvious design is a refcount: increment on enter, decrement on exit, destroy at zero. It
leaks, because every failure mode is a *lost decrement* rather than a missing counter - a player
who disconnects inside, dies and respawns at a bed, or is moved by anything that didn't go through
the portal. A refcount that leaks is worse than none, because the instance is then pinned open by
a number that looks authoritative.

So occupancy is not stored. The server asks, every few seconds:

```csharp
foreach (ZNetPeer peer in ZNet.instance.GetPeers())
    if (ZoneSystem.GetZone(peer.GetRefPos()) == instance.Zone && peer.GetRefPos().y > 3000f)
        occupied = true;
```

There is no decrement to lose, so nothing can go stale. The **armed** flag is the one bit of the
refcount idea that is worth keeping: without it, a freshly opened instance reads as empty and gets
reaped during the player's teleport in.

### Why latching matters, and why less than I first thought

Dungeon `CreatureSpawner`s do **not** respawn. `m_respawnTimeMinuts = 20f` in the DLL is a class
default that every shipped spawner overrides with 0, and the gate is
`if ((m_respawnTimeMinuts <= 0f) & alreadySpawned) return;` - one-shot, permanently. That is why a
crypt can deny you a Ghost trophy forever, and it means "kill everything" is a genuinely reachable,
stable objective. The only continuous spawners are `SpawnArea` (`Spawner_DraugrPile`,
`BonePileSpawner`: 5 s interval, `maxNear` 2, `maxTotal` 100) and those are destructible and
exhaust anyway. Latching is cheap insurance for that case rather than a workaround for a mechanic
that fights back. Full table in `docs/GAME_CONSTANTS.md`.

## 5. Objectives

An objective is a predicate over the instance's ZDOs, evaluated by the same poll that already
checks occupancy - so adding a type costs a function, not a pass.

The enabler is that **creatures can be inspected without being instantiated**: `ZDO.GetPrefab()`
gives a hash, `ZNetScene.GetPrefab(hash)` gives the prefab, and `GetComponent<Character>()` /
`IsBoss()` are ordinary lookups from there. The reaper never needs the zone loaded.

| Objective | Test |
|---|---|
| `None` | always satisfied - a pure "go in, come out" instance |
| `BossDead` | `ZDOMan.instance.GetZDO(bossId) == null` |
| `NoLiveCreatures` | no ZDO in the zone resolves to a prefab with a `Character` |
| `Timer` | wall-clock since open |

`BossDead` is the robust one and the one to build first. `Character.OnDeath` ends in
`ZNetScene.instance.Destroy(gameObject)`, which removes the ZDO - so the ZDO's disappearance *is*
the death signal, it is authoritative on the server, and checking it is one dictionary lookup with
no RPC and no event subscription to miss.

## 6. Persistence: two lifecycle classes

**Ephemeral instances** (no deadline, no consequences) mark every ZDO in the zone
`Persistent = false`. Two pieces of vanilla behaviour then do the cleanup:

- `ZDOMan.GetSaveClone()` copies only `Persistent` ZDOs, so the instance is never written to disk
  and cannot outlive the server process.
- `ZDOMan.RemoveOrphanNonPersistentZDOS()` runs on every peer disconnect and destroys
  non-persistent ZDOs whose owner is not a connected peer. The server's own session id counts as
  connected, so server-owned contents survive normally - but anything a departing player owned is
  swept on the spot. That is the "logged out inside the dungeon" case, handled by code that
  already ships.

This is *ephemeral by construction* rather than remember-to-clean, and it is the right default.

**Quest instances** cannot use it, because a quest with a deadline and consequences has to survive
a restart. Those get an **anchor**: a persistent surface object whose single ZDO holds the whole
quest record as primitives -

```
seed, variant, objectiveType, bossZdoId, deadlineTicks, cleared, instanceZoneX/Y, ownerId
```

- and which acts as the garbage-collection root. Interior contents are reachable iff their anchor
exists and is incomplete; a boot-time and periodic sweep over `ZDOMan.m_objectsByID` frees any
instance zone whose anchor is gone. (The server holds every ZDO in memory regardless of what is
loaded, so that sweep is cheap and does not need zones loaded.)

The anchor is also the natural home for the failure behaviour: "monster swarms spawn from the
location" is a `SpawnArea` armed when the deadline passes, and `EvilHeart_Forest` (20 s interval,
`maxNear` 3, `maxTotal` 30) is a ready-made template. Note the anchor sits below y=3000, so normal
surface spawn rules apply - including `m_spawnInPlayerBase`, which wants setting deliberately.

## 7. Quest identity and keys

Items are prefab plus stack, so two key items are indistinguishable by default. Identity goes in
`ItemDrop.ItemData.m_customData`, a `Dictionary<string, string>` Valheim serializes in all three
paths that matter: `Inventory` save/load (survives logout), `ItemDrop.SaveToZDO` (survives being
dropped on the ground), and the indexed variant (survives a chest).

**The id is the authority, not the item.** `ItemData.Clone()` copies `m_customData`, so a
duplicated key carries the *same* id - it is a second copy of one claim, not a second quest. The
server issues the id when the key drops and marks it consumed when it is used, so a duplicate
presents an already-consumed id and is refused. Duplication never has to be detected. The inverse
design - generating the id when the key is *used* - would hand out free instances to anyone with a
dupe, so this ordering is load-bearing.

Because keys are unique, only one player can open a given quest and there is only ever one anchor
for it. **"One instance per party" is therefore the default behaviour, not a feature**, which is
fortunate, because Valheim has no group primitive at all. The roster of who has been inside is
bookkeeping - who to credit, who to evacuate on expiry - not access control. Anyone may walk in.
If that ever needs locking down, the anchor's ZDO can hold an allowlist.

## 8. Getting in and out

**In**: `Player.TeleportTo(pos, rot, distantTeleport: true)`, which waits on
`ZNetScene.IsAreaReady`. A loading pause entering an instance is normal for the genre.

The destination is **the generator's own transform position**, which is exactly the doorway of the
dungeon's first room: `PlaceStartRoom` calls
`CalculateRoomPosRot(entrance, transform.position, transform.rotation, ...)`, positioning the start
room so its entrance *connection* lands on the generator. That holds for every theme.

It is deliberately not found by looking for the location's `Teleport` pair, because of a detail
worth remembering: in `SpawnMode.Full` the server instantiates **only the location's `ZNetView`
children**. The non-networked geometry - which is where the `Teleport`s live - is never built on a
dedicated server. It appears on clients only, via the `LocationProxy` and `SpawnMode.Client`. The
generator has a `ZNetView`, so it is always there.

**Out**: the interior's exit `Teleport` points at `m_targetPoint` on the location's exterior, which
for an instance is a crypt entrance floating at altitude with no ground under it. So the exit is
intercepted: `Teleport.Interact` is patched, and a teleport whose destination is inside an instance
zone is redirected to the player's stored return point.

The return point is written to `Player.m_customData` on entry. That is client-side state, but it
is serialized with the player profile, so it survives a logout inside an instance - which is
precisely when it is needed.

## 9. Failure modes

| Failure | Handling |
|---|---|
| Player disconnects inside | Poll sees the zone empty; `RemoveOrphanNonPersistentZDOS` sweeps what they owned |
| Player dies inside, respawns at bed | Objective unmet, so a quest instance survives and they can return for the tombstone |
| Server restart, ephemeral instance | Never saved; gone. Stated rule, not a bug |
| Server restart, quest instance | Anchor persists; sweep reconciles orphaned zones against live anchors |
| Logout inside, instance gone on return | Return point in `Player.m_customData`; spawn is relocated |
| Instance wiped with someone inside | Occupants are teleported out *before* the wipe, never merely refused |
| Player cannot beat the objective | Absolute expiry, plus an explicit abandon action |

**Tombstones are the one that will bite first.** `Player.CreateTombStone` puts the entire inventory
in an object inside the instance; wiping the instance deletes it permanently. Either the interior
persists once entered, or tombstones are relocated to the anchor / return point on wipe. This
design takes the second: it is a small rule and it also covers "the instance expired while I was
logged off".

## 10. Scope

**Implemented now** - the core that everything else layers onto, and the part that either works
immediately or teaches us something:

| Piece | File |
|---|---|
| derived address space, altitude, region membership | `InstanceRegion.cs` |
| the live record and the objective enum | `DungeonInstance.cs` |
| spawn with forced seed and theme/size overrides, mark non-persistent, poll, reap | `DungeonInstanceManager.cs` |
| client request -> server authority -> teleport, and the operations themselves | `InstanceNetwork.cs` |
| exit redirect, logout-point rewrite, return point storage | `InstanceTeleportPatches.cs` |
| `dungeoninstance` console command | `DungeonInstanceCommand.cs` |

**Deliberately deferred** - none of it changes the above:

- quest anchors, persistent quest records, the boot sweep
- key items, id issuance and consumption
- objectives beyond `None`: the enum, the latch and the call site all exist, so each remaining
  objective is one predicate in `EvaluateObjective`
- cloned-location variants, replacing the shared-prefab override (section 3)
- deadlines, swarms, loot tables
- tombstone relocation
- evacuating occupants before a forced wipe: reaping currently only ever happens on an empty
  instance, and `close` refuses while anyone is inside, so nothing can strand a player yet - but an
  expiry that fires on an occupied instance will need it

**NOT TESTED IN-GAME.** Every claim about vanilla behaviour here is read from the decompiled
assembly or extracted from prefabs; none of it has been run.
