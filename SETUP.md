# What's done vs. what needs you

## Done
- BepInEx 5.4.2333 (`denikson-BepInExPack_Valheim`) installed into
  `E:\Programs\Steam\steamapps\common\Valheim` (the vanilla install you pointed me at).
- Project scaffolded at `F:\valheimmod`: BepInEx plugin + Jotunn library, referencing your
  local game install and auto-publicizing `Assembly-CSharp.dll` at build time (no game files
  copied into the repo).
- `dotnet build` succeeds cleanly and deploys the built plugin to
  `BepInEx/plugins/CraiginsValheimMod/` automatically.
- Found your old mod source on the H: drive
  (`H:\Users\craig_000\source\repos\ValheimNoMist`) and ported the two working patches from
  it - Mistlands terrain smoothing and mist removal - into `Patches/TerrainMaskPatches.cs` and
  `Patches/AtmospherePatches.cs`, after verifying every patch target (method signatures,
  fields) still matches the current `assembly_valheim.dll` byte-for-byte in structure. Both
  are wired up to config toggles and should be ready to test.
- Design notes (not code) for the Stargate portal idea in `Stargate/DESIGN_NOTES.md`.

## You need to do

1. **Verify the game actually launches modded, and that the two patches behave.** I can't
   launch/observe a GUI game session myself. Start `valheim.exe` in
   `E:\Programs\Steam\steamapps\common\Valheim` directly (Steam's play button works too now
   that BepInEx is installed) and confirm:
   - A console window appears (BepInEx logging) if you use `-console`, or check
     `BepInEx\LogOutput.log` afterwards.
   - The log shows `Craigins Valheim Mod v0.1.0 loaded` and Jotunn initializing.
   - In the Mistlands: terrain reads as smoother than vanilla, and ground mist is gone. I
     verified the patches compile and target the right methods/fields, but I have no way to
     confirm the in-game *effect* still looks right after however many game updates since you
     last ran this code - the `DUtils` noise helper the old code relied on is gone (see
     `TerrainMaskPatches.cs` for what I substituted), so it's worth a visual check.

2. **Wisp light radius** - I searched your whole H: drive (source repos, the decompiled
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

5. **Git remote / backups**, if you want them - I initialized a local git repo only
   (see below); pushing anywhere is up to you.

6. **Thunderstore/Nexus publishing**, whenever you're ready to share the mod - needs your own
   account and API key, so that's a "when you want it" step, not something to set up now.

## Note on your existing installs

`H:\Programs\Steam\steamapps\common\` has three Valheim copies (`Valheim`, `ValheimBak`,
`ValheimFresh`), and the main one already has BepInEx plus an old **InSlimVML** loader folder
sitting alongside it. I left those alone entirely - this project only touched the vanilla
`E:\...\Valheim` install. If you want this project to target one of the H: installs instead
(e.g. to reuse existing configs), update `ValheimInstallDir` in `LocalPaths.props` and tell me -
mixing InSlimVML with BepInEx can cause conflicts, so that install may need cleanup first.
