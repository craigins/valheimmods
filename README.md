# Craigins Valheim Mod

BepInEx + Jotunn mod project for Valheim.

## Layout

Three plugins, built from one repo and released together: `CraiginsValheimMod.dll` (everything
below), `CraiginsValheimInstances.dll` (instanced dungeons only) and `CraiginsValheimStargate.dll`
(addressable portals only). The instances plugin depends on the base mod; the stargate plugin
depends on nothing but Jotunn; the base mod knows about neither, so it runs fine alone.

- `src/CraiginsValheimMod/` - the mod itself.
  - `Plugin.cs` - BepInEx plugin entry point. Binds all config toggles and runs
    `Harmony.PatchAll()`.
  - `Patches/` - mostly ported from an older `ValheimNoMist` project of mine (the exceptions
    are called out below), with every patch target verified against the current
    `assembly_valheim.dll`:
    - `TerrainMaskPatches.cs` - Mistlands terrain generation using the smoother base-height
      algorithm instead of its own craggy mask. Only the removed `DUtils` noise helper needed
      swapping (for `Mathf`); everything else matched exactly. Off by default
      (`SmoothMistlandsTerrain`).
    - `AtmospherePatches.cs` - removes Mistlands ground mist (`Mister`/`MistEmitter`). Off by
      default (`RemoveMistlandsFog`). **Wisp
      light radius has no implementation** - no source for it was found in the archive, so it
      still needs to be built (or found) from scratch.
    - `QualityOfLifePatches.cs` - craft-anywhere, no death penalty, disable random events (off
      by default - Valheim's own world settings can make raids rarer instead), no rain damage
      on roofed builds, plant-anywhere, and `NoFoodDecay` - food holds its full
      benefit for its whole duration instead of vanilla's steady decline (it still expires on
      schedule, the benefit just drops off in one step at the end).
    - `SleepPatches.cs` - sleep regardless of nearby enemies/exposure/fire/wetness, skip to
      morning once everyone's trying to sleep.
    - `BuoyancyPatches.cs` - "everything floats" (ore/metal, etc.). Not a port - the old
      version never actually worked (see the comment in that file for the three reasons why),
      so this is a fresh implementation: a `Floating` component is added to any dropped item
      that doesn't already have one, in a postfix on `ItemDrop.Awake` (same GameObject, after
      its own Rigidbody/ZNetView are set up - no parent-walking or ownership hacks needed).
    - `DungeonPatches.cs` - `MinDungeonRooms`, so you stop finding two-room dead ends. Raises
      the prefab's own room floor (and its iteration budget, which is what `m_maxRooms`
      actually is), and rerolls the layout with a derived seed when a dungeon still comes up
      short - some layouts close off all their connections after a room or two and no amount
      of extra iterations can help those. Each discarded attempt has its contents destroyed
      explicitly (via `Dungeons/DungeonInterior.cs`): `DungeonGenerator.Clear()` only removes
      the room shells, and a room's chests/spawners/doors are instantiated unparented, so
      without that they'd pile up in the interior under the layout that finally wins. Skipped
      in `SpawnMode.Ghost`, where the game already discards them itself - so `pregenerateworld`
      pays nothing for it. Off by default; **a dungeon's layout is baked into the world
      permanently when its zone first generates**, so this has to be set before the zone
      exists, and matters most before a `pregenerateworld` run - though `resetdungeon` can now
      apply it retroactively to one dungeon at a time.
    - `BreedingPatches.cs` - `UnlimitedBreeding`. Not a port - new. Lifts the nearby-population
      cap on tamed animals: `Procreation.Procreate` counts instances of its own prefab plus its
      offspring prefab within `m_totalCheckRange` and gives up once that reaches
      `m_maxCreatures`, which is why a full pen quietly stops producing. The cap is raised for
      the duration of the call and restored in a finalizer, so toggling the config off takes
      effect immediately, on already-spawned animals too. Feeding and the partner check are
      deliberately left vanilla.
    - `ForestryPatches.cs` - `SeedsFromStumps`. Not a port - new. Moves tree seeds off the tree
      and onto its stump: felling drops no seeds, destroying the stump always drops one, so
      sustainable forestry means clearing your stumps. All four drop paths (`TreeBase`,
      `TreeLog`, and the stump's `DropOnDestroyed`) funnel through `DropTable.GetDropList`, so
      one postfix there does the work and the patches on the callers just record which table is
      mid-roll. The seed is *added* rather than rolled - `GetDropList(int)` returns an empty
      list outright when `Random.value` beats `m_dropChance` - while wood amounts, stack sizes
      and `Game.m_resourceRate` scaling stay untouched. Nothing is hardcoded to prefab names:
      seeds are whatever the game's own sapling pieces are planted from (any prefab with a
      `Plant` component, via its `Piece.m_resources`), and each stump is matched to its species
      through `TreeBase.m_stubPrefab`, the same field `SpawnLog` instantiates. A species whose
      stump can't be resolved keeps dropping seeds from the tree, so a seed is never removed
      from the game without being put back somewhere.
    - Intentionally **not** ported: `TeleportAll` (vanilla already allows this), a
      `SpawnSystem` patch that only ever did debug logging, and `WearNTear.GetMinSupport`
      (`NoSupportRequired`), which was already commented out and dead in the original.
  - `Dungeons/` - wiping an already-generated dungeon interior and rebuilding it, via the
    `resetdungeon` console command or by using a Surtling core on a dungeon entrance. See
    **Resetting a dungeon** below. `DungeonInterior.cs` holds the "find and clear everything in
    this dungeon" primitive, `DungeonReset.cs` the checks and the rebuild both entry points
    share, `DungeonEntrancePatches.cs` the in-game interaction and its client/server RPC, and
    `DungeonProgression.cs` the boss-kill gate that keeps a biome's dungeons sealed until its
    boss is dead.
    (`AssemblyInfo.cs` shares these two classes with the instances plugin below via
    `InternalsVisibleTo` - they're internals shared with a known sibling, not a public API.)
  - `WorldGen/` - `pregenerateworld` console command, plus the ghost-zone suppression patch it
    relies on. See **World pregeneration** below. Also `WorldGen/DESIGN_NOTES.md` - notes on
    feeding an authored biome map into world generation instead of the game's own biome noise.
    Not implemented, but verified against the decompiled `assembly_valheim.dll`.
- `src/CraiginsValheimInstances/` - a **second plugin DLL**: temporary instanced dungeons,
  procedurally generated copies that live in their own zone far outside the map, are entered by
  teleport, and are destroyed once everyone leaves. See **Instanced dungeons** below, and
  `DESIGN_NOTES.md` in that folder for the full design including the quest layer that isn't
  built yet. `InstancesPlugin.cs` is its entry point, `InstanceRegion.cs` the address space,
  `DungeonInstanceManager.cs` the server-side spawn/poll/reap, `InstanceNetwork.cs` the
  client/server split, `InstanceTeleportPatches.cs` the exit and logout safety nets, and
  `DungeonInstanceCommand.cs` the console command.
- `src/CraiginsValheimStargate/` - a **third plugin DLL**: stargates, buildable portals with a
  fixed address that you dial instead of tagging. See **Stargates** below, and `DESIGN_NOTES.md`
  in that folder for how it sits on vanilla portal machinery (verified against 1.0.7).
  `StargatePlugin.cs` is its entry point, `StargatePiece.cs` the piece and the patches that keep
  gates out of vanilla pairing, `StargateAddress.cs` the addressing, `StargateNetwork.cs` the
  server-side dialing, `StargatePatches.cs` the hover text and interaction.
- `docs/GAME_CONSTANTS.md` - world extent, player movement, equipment modifiers and boat
  physics values extracted from the game (2026-02-19 build), plus how to re-extract them.
  Prefab-serialized tuning values are **not** in the DLL, so a decompiler alone gives wrong
  numbers - the file explains the two sources.
- `LocalPaths.props` - your machine's Valheim install path (gitignored). Copy from
  `LocalPaths.props.example` if it's missing.
- `tools/install-bepinex.ps1` - (re)installs BepInEx into a given Valheim folder.

## Building

```
dotnet build
```

This does three things automatically, using the path from `LocalPaths.props`:
1. References Valheim's game/Unity assemblies straight from your local install (no copies
   checked into this repo).
2. Publicizes `Assembly-CSharp.dll` **and** `assembly_valheim.dll` at build time (via
   `BepInEx.AssemblyPublicizer.MSBuild`) so patches can reach private/internal game members.
   Note: in Valheim, gameplay code (`WorldGenerator`, `Player`, `Mister`, etc.) actually lives
   in `assembly_valheim.dll` - `Assembly-CSharp.dll` itself is nearly empty (~23KB). Patch
   targets are almost always in `assembly_valheim`.
3. Copies each built DLL + PDB into `<Valheim>/BepInEx/plugins/<plugin name>/` so it's ready
   to test on next launch - `CraiginsValheimMod/`, `CraiginsValheimInstances/` and
   `CraiginsValheimStargate/`.

`dotnet build` at the repo root builds all three projects (they're all in `CraiginsValheimMod.slnx`).

**Jotunn has to be installed into the game separately.** The `JotunnLib` NuGet reference is
compile-time only - it does not put `Jotunn.dll` anywhere BepInEx will find it. Miss this step
and the mod builds and deploys perfectly but never loads:

```
Could not load [Craigins Valheim Mod x.y.z] because it has missing dependencies: com.jotunn.jotunn
```

```
./tools/install-bepinex.ps1 -ValheimPath "<your Valheim folder>"
./tools/install-jotunn.ps1  -ValheimPath "<your Valheim folder>"
```

Keep the `-Version` default in `tools/install-jotunn.ps1` in step with the `JotunnLib`
`<PackageReference>` in every csproj - a runtime Jotunn older than the one built against
will fail at a missing method rather than at load.

Build one on its own by naming its csproj. Installing is the same idea: copy
`CraiginsValheimMod.dll` into `BepInEx/plugins/`, and add `CraiginsValheimInstances.dll` next to
it only if you want instanced dungeons, `CraiginsValheimStargate.dll` only if you want stargates.
The instances plugin declares a BepInEx dependency on the base mod, so it refuses to load without
it rather than half-working. The stargate plugin needs only Jotunn.

## Releasing

All three plugins are versioned and released together, so bump the version in seven places -
`Plugin.ModVersion`, `InstancesPlugin.ModVersion`, `StargatePlugin.ModVersion`, all three csproj
`<Version>`s, and the log line quoted in `SETUP.md` - then tag and push:

```
git tag -a v0.5.0 -m "..."
git push origin main --follow-tags
```

`.github/workflows/release.yml` builds Release on a runner and attaches
`CraiginsValheimMod.dll`, `CraiginsValheimInstances.dll` and `CraiginsValheimStargate.dll` to the
release. If a release for that
tag already exists it just replaces the DLLs, so hand-written notes are never overwritten; if
not, it opens a **draft** to write notes into. Nothing is ever published automatically. A tag
whose version doesn't match either built assembly fails the build rather than shipping a
mislabelled DLL.

The runner has no Valheim install, so it takes its references from the **Valheim Dedicated
Server** (Steam app `896660`), which steamcmd can fetch anonymously and which ships the same
`assembly_valheim.dll` the client does - just under `valheim_server_Data`. That's why
`ValheimManagedDir` and `ValheimBepInExCoreDir` are overridable in the csproj. BepInEx core
comes from the same Thunderstore package `tools/install-bepinex.ps1` uses, so keep
`BEPINEX_VERSION` in the workflow in step with the `-Version` default in that script.

The game download is deliberately **not** cached. A stale cache would silently build against
the previous game version, which is exactly the failure that matters here - after a Valheim
update every patch target has to be re-verified against the new assembly, and a release built
on yesterday's assemblies would be worse than a slow build. A full run is under two minutes.

The workflow can also be run by hand (Actions -> Build and release -> Run workflow) to check
the build against the current live game version without cutting a release; on that path it
builds and uploads a run artifact, and skips everything release-related.

## Testing

Launch `valheim.exe` directly (not Steam's "play" button first time, though that works too once
BepInEx is installed - `winhttp.dll` in the game folder bootstraps it automatically). Add
`-console` to the launch options to get a visible BepInEx/game log window. Logs also land in
`<Valheim>/BepInEx/LogOutput.log`.

## Finding current patch targets

Valheim's internals change between updates, so don't trust old notes/decompiles blindly.
After a `dotnet build`, the publicized assemblies are cached at
`src/CraiginsValheimMod/obj/Debug/publicized/assembly_valheim.dll` (this is where most
gameplay types live - see above) and `...publicized/Assembly-CSharp.dll` - open either in
[ILSpy](https://github.com/icsharpcode/ILSpy) or [dnSpy](https://github.com/dnSpyEx/dnSpy) to
browse current class/method names.

## Resetting a dungeon

Console command `resetdungeon` (server-only) wipes an already-generated dungeon interior and
rebuilds it with a new layout. Vanilla bakes a dungeon's rooms into its ZDO the first time its
zone generates and never revisits them, so this is the only way to change a dungeon that
already exists - including applying `MinDungeonRooms` retroactively to one.

```
resetdungeon                    # nearest loaded dungeon
resetdungeon list               # what can be reset right now
resetdungeon list all           # every dungeon in the world, with coordinates
resetdungeon name=SunkenCrypt4  # target by location name (substring, case-insensitive)
resetdungeon zone=12,-34        # target by zone, no spaces
resetdungeon seed=12345         # a specific layout instead of a fresh random one
resetdungeon dry                # report what would happen, change nothing
resetdungeon force              # go ahead even though someone is inside, or the boss gate says no
resetdungeon wipebuilt          # also delete player-built pieces inside
```

### In game: use a Surtling core on the entrance

Set `Dungeons.ResetFromEntrance = true` (on the **server** - it does the work; on clients too,
so they get the hover hint) and a dungeon entrance gains a second line on its hover text:

```
[E] Enter
[Use Surtling core] Regenerate
```

Hover the entrance, use the core from your inventory, and the dungeon rebuilds. The core is
consumed only when the server reports success, so a refused reset - somebody still inside -
doesn't eat it. `ResetCostItem` (any prefab name in ObjectDB) and `ResetCostAmount` configure
the price.

### Gating regeneration on boss kills

`Dungeons.ResetBossGate` ties each biome's dungeons to its boss, so a farmable dungeon can't run
ahead of progression - no rerolling Black Forest crypts until the Elder is down, no Sunken Crypts
until Bonemass, no frost caves until Moder:

```
BlackForest=gd_king, Swamp=Bonemass, Mountain=Dragon, Plains=GoblinKing, Mistlands=SeekerQueen, AshLands=Fader
```

That's the default. Set it empty to allow regeneration everywhere. A locked entrance says so
instead of offering the trade:

```
[E] Enter
Sealed until The Elder falls
```

Bosses are named by **prefab**, not by global key, and the key is read off the prefab's
`m_defeatSetGlobalKey` at runtime. That matters: the five original bosses have their keys in the
`GlobalKeys` enum, but the Queen's and Fader's exist only as serialized strings inside the asset
bundles, so hardcoding them means guessing. Reading the prefab is right by construction, gives us
the boss's localized name for the hover text, and keeps working across game updates. A token that
isn't a boss prefab is used as a raw global key instead, so `Meadows=KilledTroll` works too.

The gate is **world-wide**, because that's what a boss kill is: `Character.OnDeath` calls
`ZoneSystem.SetGlobalKey`, which the server stores in the world save and pushes to every client,
so one player's kill unlocks the biome for everyone and both ends can answer the question without
extra syncing. Vanilla also queues a per-player copy of the same key, but only the local client
can read its own - the server has no view of another player's unique keys - so a per-player gate
would be an honour system.

The biome comes from `WorldGenerator.GetBiome` at the location's own position. `PlaceLocations`
tests that exact point against the location's allowed-biome mask when it places it, so asking the
same function about the same point gives back the biome that put the dungeon there - more precise
than reading the mask, which lists several biomes for some location types.

The server's value is the one enforced; clients use theirs only to draw the hover text. The
console command honours the gate as well, and `resetdungeon force` bypasses it.

This hangs off `Teleport.UseItem`, which is a literal `return false;` stub in vanilla - an
entire interaction verb sitting unused on exactly the right object. **A keybind was the obvious
alternative and doesn't work**: `Teleport.OnTriggerEnter` calls `Interact()` the moment you walk
into the entrance collider, so you're teleported before any "stand here and press something"
scheme can fire. Hold-E fails for the same reason (a quick E press teleports you first), and
alt+E would silently repurpose an existing interaction. Using an item works from a step back
while the entrance is only hovered, which is the one place the interaction is stable.

The interaction happens on the player's client and the rebuild has to happen where the world
lives, so the client validates locally, asks the server over a routed RPC, and the server does
the authoritative checks and the work. The server does *not* verify the client really held a
core - it has no view of a client's inventory, and Valheim's inventory is client-authoritative
throughout. Same trust model as the rest of the game; admin-gating it instead would just make
the cost decorative.

### Why interiors are easy to reset and surface locations aren't

Dungeon interiors aren't in the terrain. `Location.Awake` instantiates the interior 5000m
straight up from the entrance, centred on the *zone* centre in XZ, and `Character.InInterior`
is a bare `pos.y > 3000f`. So there's no `TerrainComp` and no heightmap edits to unwind - and
because ZDO sectors are computed from XZ only, every interior ZDO lives in the same sector as
the surface entrance. "Everything in this dungeon" is one `FindSectorObjects` call plus a
height filter.

The reset itself is two steps. `DungeonGenerator.Generate(seed, SpawnMode.Full)` is public and
already does the whole rebuild - `Clear`, re-seed `Random`, `GenerateRooms`, `Save` back to the
same ZDO - so generation isn't reimplemented, just re-invoked. What it *doesn't* do is clean up:
`Clear()` only destroys children of the generator's transform, which is the room shells. The
networked contents are instantiated **unparented** in `PlaceRoom`, so they survive `Clear()` as
orphaned ZDOs and the new layout gets built on top of the old one's furniture. Sweeping those
first is what `DungeonInterior` is for.

### Limits worth knowing before you use it

- **Only dungeons in a currently loaded zone can be targeted.** A `DungeonGenerator` exists as
  a GameObject only while its zone is live, and there's no supported way to ask `ZoneSystem`
  for an arbitrary one. Go stand at the dungeon you want; `resetdungeon list all` tells you
  where the others are.
- **Connected clients keep showing the old rooms** until they leave and re-enter the zone.
  `DungeonGenerator.Load()` runs only in `Awake` and nothing pushes a "your layout changed"
  message. Their *contents* are destroyed immediately, so in between it looks like an emptied
  version of the old dungeon. The command refuses to run while anyone is inside (`force`
  overrides) because they'd otherwise be left standing in a stale copy 5000m above the map.
- **Loot and monsters reroll too**, not just walls - `RandomSpawn.Randomize` is driven by a
  per-room seed derived from room position.
- **Player-built pieces inside are kept** by default, identified by the `creator` ZDO field
  that `Piece.SetCreator` stamps and that world-placed objects leave at 0. Loose dropped items
  are *not* distinguishable that way and are always swept.
- **It breaks determinism for that dungeon.** A reset dungeon no longer matches what fresh
  worldgen would produce for the world seed. The seed used is printed so a layout can be
  reproduced with `seed=`.
- Camps (`Algorithm.CampGrid` / `CampRadial` - Fuling villages and the like) are refused. Those
  sit in the terrain, not in an interior, and none of the above applies to them.

## Instanced dungeons

Temporary dungeons that aren't part of the world: a location is spawned into an otherwise unused
zone about 128 km from the map, generated with a chosen seed and room theme, entered by teleport,
and destroyed once everyone has left. Off by default (`Instances.Enabled`), and **untested**.

This is the one part of the repo that ships as a **separate plugin**,
`CraiginsValheimInstances.dll`, with its own config file
(`BepInEx/config/com.craigins.valheiminstances.cfg`). Everything else in the mod is a small
tweak to a world that already exists; instances spawn zones outside the map, teleport players
into them, run their own RPC protocol and reap themselves afterwards. That makes them the piece
most likely to break on a game update and the piece a server owner is most likely to want to run
without - so whether they're loaded at all is a file you copy or don't, not a toggle inside a
plugin that gets loaded and patched either way. Harmony patches follow their subsystem, so
deleting the DLL is a complete uninstall.

It's a hard dependency in one direction only: the instances plugin needs the base mod (for the
dungeon primitives in `Dungeons/`, and BepInEx loads it first because of that), while the base
mod has no idea it exists.

```
dungeoninstance locations                          # what this world can instance - names aren't guessable
dungeoninstance open                               # uses Instances.DefaultLocation
dungeoninstance open location=Crypt3 theme=Cave rooms=30-60 seed=12345
dungeoninstance list
dungeoninstance enter 1
dungeoninstance out                                # local escape hatch if an exit ever fails
dungeoninstance close 1 | close all
```

Unlike `resetdungeon` this works from a client: the server does everything authoritative and
replies with a destination, because the teleport has to happen on the asking player's own client.
Both sides need `Instances.Enabled` - the server to do the work, the client to run the command.

### Why a distant zone and not just a high altitude

The obvious way to keep an instance from colliding with real content is to put it above the
vanilla dungeon band. That does prevent overlap, but it doesn't isolate anything, because
**Valheim partitions the world by XZ and never by height** - `ZDO.SetSector`,
`ZNetScene.InActiveArea` and `ZDOMan.FindSectorObjects` all reduce a position to
`ZoneSystem.GetZone`, which discards `y`. An instance parked above a real zone joins that zone's
sector, so every other player crossing that ground instantiates the whole thing.

So instances get their own zone (isolation) *and* altitude (`Character.InInterior` is a bare
`y > 3000`, which is what suppresses raids, gives the interior environment, and stops the
overworld being drawn under the floor). The zone is derived from the instance id by hashing rather
than allocated from a list, so nothing needs rebuilding after a restart.

### They don't survive a server restart, on purpose

Every ZDO in an instance is marked `Persistent = false`. `ZDOMan.GetSaveClone()` copies only
persistent ZDOs, so an instance is never written to the world save and cannot outlive the server
process - and `ZDOMan.RemoveOrphanNonPersistentZDOS()`, which runs on every peer disconnect,
sweeps anything a departing player owned. That makes instances ephemeral *by construction* rather
than by remembering to clean up, and it means the "player logged out inside" case is handled by
code that already ships.

Quest instances, when they exist, will need the opposite and get a persistent surface anchor
instead. That's designed but not built; see `Instances/DESIGN_NOTES.md`.

### Limits worth knowing

- **Anything left in an instance is lost when it's reaped**, including a tombstone if you die
  inside. Nothing relocates them yet. This is the first thing to fix before real play.
- **Occupancy is polled, not counted.** The reaper asks `ZNet.GetPeers()` where everyone is every
  few seconds; an instance is destroyed 20 s after the last person leaves, or after 5 minutes if
  nobody ever entered. There is deliberately no enter/exit counter - every way one of those leaks
  is a lost decrement, and a leaked counter pins the instance open forever.
- **The exit portal is intercepted.** A dungeon's interior exit points at the location's exterior,
  which for an instance is a doorway floating 20 km up with nothing under it, so `Teleport.Interact`
  is patched to send you back where you came from instead. Logging out inside rewrites your logout
  point for the same reason.
- **Theme and room-count overrides are written onto the shared location prefab** and restored
  immediately after, because `SpawnLocation` leaves no window between instantiating the generator
  and generating. Don't open an instance while a `pregenerateworld` run is in flight.
- Only locations with a dungeon interior and `Algorithm.Dungeon` can be instanced; surface camps
  are refused, since they need terrain under them.

## Stargates

A buildable portal (hammer menu, same recipe as the wooden portal) with a fixed six-symbol
address instead of a tag. **Untested in-game.** Ships as its own plugin,
`CraiginsValheimStargate.dll`, which needs only Jotunn. Every client needs it as well as the
server, since it adds a piece.

```
[E]        Dial        - type another gate's address, e.g. ABC-DEF
[Shift+E]  Disconnect  - from either end of a link
```

- **Links don't time out.** Two dialed gates stay linked until either end disconnects.
- **An incoming connection wins.** If a third gate dials one of them, that gate drops its old link,
  and the gate it was linked to goes idle too. So a timer never has to run on a gate that nobody
  has loaded.
- **Addresses are learned by visiting.** Hovering a gate shows its address. `stargate list` (needs
  `devcommands`) prints every gate in the world with its link.
- **Dialing works across the map.** The server holds every gate's ZDO, so it can link a gate that
  nobody is anywhere near.

Gates are vanilla portals in all but pairing. Each link is stored in the same ZDO connection a
portal uses, so walking through, the item rules, the glow and persistence across restarts are
the game's own code. Vanilla's 5-second tag-pairing loop is patched to skip gates, because
otherwise it would tear every dialed link down. An address is a hash of the gate's position,
computed on demand, so nothing is stored and it never changes while the gate stands. See
`src/CraiginsValheimStargate/DESIGN_NOTES.md` for the engine details and what isn't built yet (a
real dialer, event-horizon visuals, an iris, instanced-dungeon addresses).

**Deconstruct gates before removing the plugin.** Their ZDOs sit in the world's portal list, and
without the plugin vanilla would try to pair them with untagged portals.

## World pregeneration

Console command `pregenerateworld` (server-only) force-generates every zone in the world up
front, instead of leaving zones lazily generated as players explore. Useful before copying a
world to a dedicated server.

**Run it *after* enabling any terrain-affecting config (`SmoothMistlandsTerrain`, etc.), and
*before* copying the world files.** Terrain height is computed once, at zone-generation time,
and baked permanently into that zone's save data - it is never recomputed later. Pregenerating
with the patch off (or generating the world some other way first) and then flipping the patch
on afterward does nothing for already-generated zones.

How it works: it drives `ZoneSystem.SpawnZone(id, SpawnMode.Ghost, ...)` across every zone
inside the world's 10000m radius - the exact same call vanilla itself already makes
continuously in the background near every player (`ZoneSystem.CreateGhostZones`) to
pregenerate zones just out of view. Ghost mode generates a zone (terrain, vegetation,
locations), marks it generated, then fully destroys everything it spawned - so this doesn't
accumulate live objects any more than normal play does, just across the whole map instead of
near players.

### Generation order: unique locations first, then a strict spiral out from the origin

**Why order matters.** A `ZoneLocation` flagged `m_unique` - Haldor's merchant camp being the
obvious one - gets *many* candidate instances seeded across the map at world-gen time, and the
first one to actually generate wins. `ZoneSystem.PlaceLocations` ends with:

```csharp
if (loc.m_location.m_unique) RemoveUnplacedLocations(loc.m_location);
```

and `RemoveUnplacedLocations` deletes every other unplaced instance of that same location from
`m_locationInstances`. So whichever zone generates first decides, permanently, where the single
merchant in this world lives. Generating from a map corner would strand it at the far edge.
(Boss altars and similar aren't affected - those are numerous and fixed by seed, not
first-come-first-served.)

Three things protect the ordering:

**1. A claim pass runs first.** Before the bulk work, it walks `m_locationInstances`, finds the
nearest-to-origin ungenerated candidate zone for each unclaimed unique location, and generates
just those - a handful of zones, done in the first seconds of the run. That settles every
once-per-world claim at the closest possible spot instead of leaving it to be won hours later
somewhere out in the spiral. Only the nearest candidate per location is needed: generating it
triggers `RemoveUnplacedLocations`, which clears the rest, and those zones then rejoin the
spiral as ordinary zones. Different unique locations never compete with each other, since
`RemoveUnplacedLocations` is per-`ZoneLocation`. Each claim is logged with its location name,
zone, and distance from spawn.

**2. Everything else generates in a strict spiral** outward from the origin (nearest zone centre
first, ties broken by angle) - never raster order from a corner. This covers anything else
order-dependent, including mod-added generation, and means an interrupted run leaves a
contiguous generated disc around spawn instead of a partial band along one edge.

"Strict" is load-bearing. `SpawnZone` returns false when the zone's terrain isn't built yet, so
the obvious approach - retry that zone later, move on to the next one meanwhile - lets later
zones overtake earlier ones and quietly destroys the ordering (that's what the first version of
this did). Instead the cursor parks on the head of the spiral until that exact zone spawns. To
stop that from serialising into a crawl, terrain for the next `TerrainLookahead` zones (default
8) is pre-requested *in spiral order* via `HeightmapBuilder.IsTerrainReady`, which queues the
build as a side effect - the same request `SpawnZone` itself makes, just pulled forward. The
build thread stays saturated; nothing generates out of order. The lookahead is clamped to 0-12
because the game trims its finished-terrain queue back to 16 entries, discarding the oldest.

**3. Vanilla's own background pregeneration is suppressed during the run.**
`ZoneSystem.Update` calls `CreateGhostZones` every frame around the local reference position and
every connected peer, ghost-generating zones in raster order from a square's corner - racing our
ordering for exactly the claims we care about. `GhostZoneSuppressionPatch` no-ops it while
`pregenerateworld` is running. That's free: ghost zones are pure lookahead with no gameplay
effect, and we're generating the whole map anyway.

`CreateLocalZones` is deliberately **not** suppressed - it spawns the live zones players stand
on, and blocking it would drop anyone connected through the world. So for a clean result, run
this on a dedicated server with nobody connected, or at least stand at spawn. The command warns
at startup if peers are connected or the reference position is far from the origin.

One extra guard: `SpawnZone` can also refuse a zone whose pending location prefab hasn't
finished loading, so a zone that never becomes spawnable would otherwise stall the whole run.
If the cursor sits on one zone for 60s it's set aside (logged) and retried in a final pass;
10 such zones in a row aborts the run and saves, on the assumption the terrain builder is
stuck rather than slow.

The command also refuses to start if `ZoneSystem.LocationsGenerated` is still false - starting
mid-`GenerateLocationsTimeSliced` would mean claiming unique locations from an incomplete
candidate list.

**Measured: 76,470 zones in 1h47m** on a default 10000m-radius world - about 715 zones/min
averaged over the run. Expect the rate to fall off as it goes; the first few minutes ran at
roughly 1250 zones/min, and the likeliest cause of the slowdown is checkpoint saves getting
more expensive as the world `.db` grows (raise `SaveEveryNZones` if you want to trade crash
recovery for speed). The real bottleneck is `HeightmapBuilder`, the game's own terrain
generator: it's a *single* background thread processing one zone's terrain at a time. That's
an engine-level constraint this mod doesn't (and safely can't) work around.

It's safe to interrupt: already-generated zones are skipped on the next run (progress is
saved periodically as it goes - see `PregenSaveEveryNZones` config, default every 2000 zones -
specifically so a crash or restart mid-run doesn't lose everything back to zero), and it does
a final synchronous save when done. Progress logs to the BepInEx console/log every ~10s,
including how far out from the origin the spiral has reached. Tune `ZonesPerTick` in config if
needed, though it mostly won't change total runtime - it's still gated by the same
single-threaded terrain builder either way.

### Clear the minimap cache afterwards

**After a pregeneration run, delete the world's cached minimap textures** - otherwise the map
shows the *old* terrain and won't match what you fly over. Quit the game and delete these
three files (no extension, sitting next to nothing else that matters - they're regenerated
caches, not save data):

```
<Steam>\userdata\<id>\892970\remote\worlds_local\<WorldName>_mapTexCache
<Steam>\userdata\<id>\892970\remote\worlds_local\<WorldName>_heightTexCache
<Steam>\userdata\<id>\892970\remote\worlds_local\<WorldName>_forestMaskTexCache
```

Why this is needed: `Minimap.GenerateWorldMap` renders the map pixel by pixel straight from
`WorldGenerator.GetBiome`/`GetBiomeHeight` - it never reads a single generated zone. So the map
is a picture of the *generator function*, while the world you walk on is *baked zone data*.
That render is then cached to disk, and `Minimap.Update` calls `TryLoadMinimapTextureData()`
first, only falling back to a fresh `GenerateWorldMap()` if the cache files are missing. A map
rendered before `SmoothMistlandsTerrain` was active therefore sticks around forever, showing
vanilla craggy Mistlands over terrain that was actually baked smooth. Deleting the cache is the
only way to force the re-render - `Minimap.ForceRegen` exists in the assembly but nothing calls
it, so there's no console command for it.

Two related things that are *not* bugs, and that clearing the cache won't change:

- The cache is keyed by **world name, not seed**. A previous world of the same name leaves its
  map behind for the new one.
- Terrain near villages, crypts and other locations won't match the map, because locations
  flatten their own ground when placed while the map only ever shows raw generator height. A
  fully pregenerated world has *every* location placed, so this shows up far more than in a
  normally-explored world.
- The minimap is rendered **client-side**, so this follows you onto the dedicated server: a
  client without `SmoothMistlandsTerrain` active draws vanilla craggy Mistlands on its map even
  though the server's terrain was baked smooth. If you want everyone's map to match the ground,
  everyone needs the mod - the pregenerated world alone isn't enough.

### Test this on a copy of your world first

This has now been run end to end once (a full default-size world, pre-1.0 build, completing
cleanly through the final save), but it mutates real save data at scale - tens of thousands of
zones, irreversibly - so watch it complete on a disposable copy of any world you care about
before pointing it at the real one.

## Multiple Valheim installs

If you keep more than one copy of the game around (a live one, a backup, a clean one), this
project builds against whichever is named by `ValheimInstallDir` in `LocalPaths.props`, and
deploys the plugin into that install's `BepInEx/plugins/`. Point it at a different copy by
editing that file and re-running `tools/install-bepinex.ps1` if that install needs a newer
BepInEx.

Prefer a copy that isn't already carrying another mod loader - an old **InSlimVML** folder
sitting alongside BepInEx can cause conflicts, and is worth cleaning up first.
