# Craigins Valheim Mod

BepInEx + Jotunn mod project for Valheim.

## Layout

- `src/CraiginsValheimMod/` - the mod itself.
  - `Plugin.cs` - BepInEx plugin entry point. Binds all config toggles and runs
    `Harmony.PatchAll()`.
  - `Patches/` - mostly ported from an older `ValheimNoMist` project of mine (the exceptions
    are called out below), with every patch target verified against the current
    `assembly_valheim.dll`:
    - `TerrainMaskPatches.cs` - Mistlands terrain generation using the smoother base-height
      algorithm instead of its own craggy mask. Only the removed `DUtils` noise helper needed
      swapping (for `Mathf`); everything else matched exactly.
    - `AtmospherePatches.cs` - removes Mistlands ground mist (`Mister`/`MistEmitter`). **Wisp
      light radius has no implementation** - no source for it was found in the archive, so it
      still needs to be built (or found) from scratch.
    - `QualityOfLifePatches.cs` - craft-anywhere, no death penalty, disable random events, no
      rain damage on roofed builds, plant-anywhere, and `NoFoodDecay` - food holds its full
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
      of extra iterations can help those. Off by default; **a dungeon's layout is baked into
      the world permanently when its zone first generates**, so this has to be set before the
      zone exists, and matters most before a `pregenerateworld` run.
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
  - `WorldGen/` - `pregenerateworld` console command, plus the ghost-zone suppression patch it
    relies on. See **World pregeneration** below.
  - `Stargate/DESIGN_NOTES.md` - notes on the addressable-portal ("Stargate") feature.
    Not implemented - a bigger feature to tackle separately.
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
3. Copies the built DLL + PDB into `<Valheim>/BepInEx/plugins/CraiginsValheimMod/` so it's ready
   to test on next launch.

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
