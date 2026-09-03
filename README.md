# Craigins Valheim Mod

BepInEx + Jotunn mod project for Valheim.

## Layout

- `src/CraiginsValheimMod/` - the mod itself.
  - `Plugin.cs` - BepInEx plugin entry point. Binds two config toggles
    (`SmoothMistlandsTerrain`, `RemoveMistlandsFog`) and runs `Harmony.PatchAll()`.
  - `Patches/`:
    - `TerrainMaskPatches.cs` - Mistlands terrain generation using the smoother base-height
      algorithm instead of its own craggy mask. Ported from your old `ValheimNoMist` project
      (found on H:) and verified against the current `assembly_valheim.dll` - method targets
      match exactly, only the removed `DUtils` noise helper was swapped for `Mathf`.
    - `AtmospherePatches.cs` - removes Mistlands ground mist (`Mister`/`MistEmitter`). Also
      ported from `ValheimNoMist`, also verified against the current assembly. **Wisp light
      radius has no `TODO` stub filled in** - no source for it was found anywhere on H:, so
      it still needs to be built (or found) from scratch.
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

## Multiple Valheim installs on this machine

Two other installs were found under `H:\Programs\Steam\steamapps\common\` (`Valheim`,
`ValheimBak`, `ValheimFresh`) already carrying BepInEx and an old InSlimVML loader - this
project targets the clean install at
`E:\Programs\Steam\steamapps\common\Valheim` instead, where a fresh BepInEx
5.4.2333 was installed. If you'd rather use one of the H: installs, update
`LocalPaths.props` and re-run `tools/install-bepinex.ps1` if it needs a newer BepInEx.
