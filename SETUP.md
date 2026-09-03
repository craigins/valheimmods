# What's done vs. what needs you

## Done
- BepInEx 5.4.2333 (`denikson-BepInExPack_Valheim`) installed into
  `E:\Programs\Steam\steamapps\common\Valheim` (the vanilla install you pointed me at).
- Project scaffolded at `F:\valheimmod`: BepInEx plugin + Jotunn library, referencing your
  local game install and auto-publicizing `Assembly-CSharp.dll` at build time (no game files
  copied into the repo).
- `dotnet build` succeeds cleanly and deploys the built plugin to
  `BepInEx/plugins/CraiginsValheimMod/` automatically.
- Patch file stubs for the two known tweaks (Black Forest/Mistlands mask, wisp light + fog),
  and design notes (not code) for the Stargate portal idea.

## You need to do

1. **Verify the game actually launches modded.** I can't launch/observe a GUI game session
   myself. Start `valheim.exe` in
   `E:\Programs\Steam\steamapps\common\Valheim` directly (Steam's play button works too now
   that BepInEx is installed) and confirm:
   - A console window appears (BepInEx logging) if you use `-console`, or check
     `BepInEx\LogOutput.log` afterwards.
   - The log shows `Craigins Valheim Mod v0.1.0 loaded` and Jotunn initializing.

2. **Port your existing patches.** You mentioned you'd already built the Black Forest/Mistlands
   mask swap and the wisp light/fog tweaks before. I didn't try to reconstruct them from memory
   since Valheim's internals shift between versions and I'd rather you paste in code you know
   works than have me guess and hand you something subtly wrong. Drop them into
   `src/CraiginsValheimMod/Patches/TerrainMaskPatches.cs` and `AtmospherePatches.cs` - the TODOs
   there explain how to re-find current method names if the old ones no longer match (game may
   have updated since you last wrote them).

3. **A decompiler, for finding patch targets.** [dnSpy](https://github.com/dnSpyEx/dnSpy) or
   [ILSpy](https://github.com/icsharpcode/ILSpy) - I didn't install either since neither has an
   unattended/silent installer suitable for me to run without your interaction, and it's a
   personal tool choice. Point either at
   `src/CraiginsValheimMod/obj/Debug/publicized/Assembly-CSharp.dll` after your first
   `dotnet build`.

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
