# Craigins Valheim Mod

BepInEx + Jotunn mod project for Valheim.

## Layout

- `src/CraiginsValheimMod/` - the mod itself.
  - `Plugin.cs` - BepInEx plugin entry point. Binds all config toggles and runs
    `Harmony.PatchAll()`.
  - `Patches/` - all ported from your old `ValheimNoMist` project (found on H:), with every
    patch target re-verified against the current `assembly_valheim.dll` before porting:
    - `TerrainMaskPatches.cs` - Mistlands terrain generation using the smoother base-height
      algorithm instead of its own craggy mask. Only the removed `DUtils` noise helper needed
      swapping (for `Mathf`); everything else matched exactly.
    - `AtmospherePatches.cs` - removes Mistlands ground mist (`Mister`/`MistEmitter`). **Wisp
      light radius has no implementation** - no source for it was found anywhere on H:, so it
      still needs to be built (or found) from scratch.
    - `QualityOfLifePatches.cs` - craft-anywhere, no death penalty, disable random events, no
      rain damage on roofed builds, plant-anywhere, and an off-by-default food-total patch
      (see the comment in that file - the game's `Player.Food` struct changed shape since this
      was written, so its original purpose may already be obsolete).
    - `SleepPatches.cs` - sleep regardless of nearby enemies/exposure/fire/wetness, skip to
      morning once everyone's trying to sleep.
    - `BuoyancyPatches.cs` - "everything floats" (ore/metal, etc.). Not a port - the old
      version never actually worked (see the comment in that file for the three reasons why),
      so this is a fresh implementation: a `Floating` component is added to any dropped item
      that doesn't already have one, in a postfix on `ItemDrop.Awake` (same GameObject, after
      its own Rigidbody/ZNetView are set up - no parent-walking or ownership hacks needed).
    - Intentionally **not** ported: `TeleportAll` (vanilla already allows this), a
      `SpawnSystem` patch that only ever did debug logging, and `WearNTear.GetMinSupport`
      (`NoSupportRequired`), which was already commented out and dead in the original.
  - `WorldGen/` - `pregenerateworld` console command. See **World pregeneration** below.
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

**Expect this to take a long time - very plausibly hours on a full-size map.** The real
bottleneck is `HeightmapBuilder`, the game's own terrain generator: it's a *single* background
thread processing one zone's terrain at a time. That's an engine-level constraint this mod
doesn't (and safely can't) work around. A default 10000m-radius world has on the order of
70-80k zones inside its circular boundary.

It's safe to interrupt: already-generated zones are skipped on the next run (progress is
saved periodically as it goes - see `PregenSaveEveryNZones` config, default every 2000 zones -
specifically so a crash or restart mid-run doesn't lose everything back to zero), and it does
a final synchronous save when done. Progress logs to the BepInEx console/log every ~10s.
Tune `ZonesPerTick` in config if needed, though it mostly won't change total runtime - it's
still gated by the same single-threaded terrain builder either way.

**Test this on a copy of your world first.** This mutates real save data at scale (tens of
thousands of zones); I verified the API against the current game build and the mechanism
matches vanilla's own, but I have not been able to run it myself (I can't launch/observe a
live game session) - there's no substitute for you watching it complete once on a disposable
copy before trusting it against a world you care about.

## Multiple Valheim installs on this machine

Two other installs were found under `H:\Programs\Steam\steamapps\common\` (`Valheim`,
`ValheimBak`, `ValheimFresh`) already carrying BepInEx and an old InSlimVML loader - this
project targets the clean install at
`E:\Programs\Steam\steamapps\common\Valheim` instead, where a fresh BepInEx
5.4.2333 was installed. If you'd rather use one of the H: installs, update
`LocalPaths.props` and re-run `tools/install-bepinex.ps1` if it needs a newer BepInEx.
