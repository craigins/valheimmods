# What's done vs. what needs you

## Done
- BepInEx 5.4.2333 (`denikson-BepInExPack_Valheim`) installed into the vanilla game install
  named by `ValheimInstallDir` in `LocalPaths.props`.
- Project scaffolded: BepInEx plugin + Jotunn library, referencing that local game install
  and auto-publicizing `Assembly-CSharp.dll` at build time (no game files copied into the
  repo).
- `dotnet build` succeeds cleanly and deploys the built plugin to
  `BepInEx/plugins/CraiginsValheimMod/` automatically.
- Found the old ValheimNoMist mod source archived on another drive and ported the two working
  patches from it - Mistlands terrain smoothing and mist removal - into `Patches/TerrainMaskPatches.cs` and
  `Patches/AtmospherePatches.cs`, after verifying every patch target (method signatures,
  fields) still matches the current `assembly_valheim.dll` byte-for-byte in structure. Both
  are wired up to config toggles and should be ready to test.
- Design notes (not code) for the Stargate portal idea in `Stargate/DESIGN_NOTES.md`.
- `pregenerateworld` console command (`WorldGen/`) - force-generates the whole map instead of
  lazy per-zone generation, for porting a fully-generated world (with the Mistlands terrain
  patch already baked in) to your dedicated server. Generates centre-outward so once-per-world
  locations (the merchant, etc.) land near spawn rather than at a map edge. See the README's
  **World pregeneration** section for how it works and its real time cost (measured: 76,470
  zones in 1h47m on a full-size map).
- Published to GitHub: <https://github.com/craigins/valheimmods> (public, MIT), with the
  built plugin attached to each tagged [release](https://github.com/craigins/valheimmods/releases).
  Note there's no CI build - compiling needs a local Valheim install for the game assemblies,
  which can't live on a hosted runner, so releases are built locally and uploaded.

## You need to do

1. **Verify the game actually launches modded, and that the two patches behave.** I can't
   launch/observe a GUI game session myself. Start `valheim.exe` from your game install
   directly (Steam's play button works too now that BepInEx is installed) and confirm:
   - A console window appears (BepInEx logging) if you use `-console`, or check
     `BepInEx\LogOutput.log` afterwards.
   - The log shows `Craigins Valheim Mod v0.5.0 loaded` and Jotunn initializing (plus
     `Craigins Valheim Instances v0.5.0 loaded` if that second plugin DLL is installed too).
   - In the Mistlands: terrain reads as smoother than vanilla, and ground mist is gone. I
     verified the patches compile and target the right methods/fields, but I have no way to
     confirm the in-game *effect* still looks right after however many game updates since you
     last ran this code - the `DUtils` noise helper the old code relied on is gone (see
     `TerrainMaskPatches.cs` for what I substituted), so it's worth a visual check.

2. **Wisp light radius** - I searched the whole archive drive (source repos, the decompiled
   reference project, and every built plugin DLL) and found no trace of this ever being
   built - only the mist/terrain patches and an unrelated "Death Recorder" mod. Either it
   wasn't saved separately, or it's somewhere I didn't think to look. Point me at it if you
   find it, or tell me the wisp prefab/light name and I'll build the patch from scratch.

3. **A decompiler, for finding new patch targets going forward.**
   [dnSpy](https://github.com/dnSpyEx/dnSpy) or [ILSpy](https://github.com/icsharpcode/ILSpy) -
   I didn't install either since neither has an unattended/silent installer suitable for me to
   run without your interaction, and it's a personal tool choice. Point either at
   `src/CraiginsValheimMod/obj/Debug/publicized/assembly_valheim.dll` after your first
   `dotnet build` (that's where most gameplay types actually live - not `Assembly-CSharp.dll`).

4. **Stargate portals** - genuinely a multi-session feature (new networked state, RPCs, custom
   UI). `Stargate/DESIGN_NOTES.md` has a starting architecture sketch, deliberately not code -
   say the word when you want to start building it and we can go class by class.

5. **Thunderstore/Nexus publishing**, whenever you're ready to share the mod more widely.
   GitHub Releases already covers "here's a DLL you can download"; Thunderstore is what gets
   you into mod managers (r2modman/Thunderstore app) and its own dependency resolution. It
   needs your own account and API key, plus a `manifest.json` and icon, so it's a "when you
   want it" step - say the word and I'll set the packaging up.

6. ~~**Test `pregenerateworld` on a copy of your world before trusting it for real.**~~ Done
   2026-09-04: a full run completed cleanly, 76,470 zones in 1h47m, through the final save.
   Still copy your world's `.db`/`.fwl` files somewhere safe before running it for real - the
   generation it bakes in is one-way - and note the run above was on the pre-1.0 build, so the
   real pregeneration still waits for the 1.0 patch (2026-09-09) and a re-verification of every
   patch target against the new `assembly_valheim.dll`.

7. **Figure out where this mod needs to be installed to actually run `pregenerateworld` against
   your dedicated server's world.** BepInEx was only installed into the regular game client -
   not into any dedicated server install (Valheim's dedicated server is normally a different
   executable/App ID from the
   regular client, `valheim_server.exe`, entirely separate from what I set up). Two ways to
   run pregeneration:
   - Install BepInEx + this mod directly on whatever runs the dedicated server, and run the
     console command there against the live world, or
   - Host the same world locally in a normal client session (this mod's already set up for
     that), run `pregenerateworld` there, then copy the resulting world files over to the
     server's save folder.
   Tell me which world/install you actually want pregenerated and I can help wire up whichever
   path makes sense.

## Note on other installs

If you keep several Valheim copies around, this project only ever touches the one named by
`ValheimInstallDir` in `LocalPaths.props` - nothing else is modified. Prefer a clean install:
mixing an old **InSlimVML** loader with BepInEx can cause conflicts, so an install already
carrying one may need cleanup before it's a good target.
