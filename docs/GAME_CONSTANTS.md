# Extracted Valheim constants

Reference values pulled from the game on 2026-09-05, against the **2026-02-19 (pre-1.0)**
build. Re-extract after the 1.0 patch on 2026-09-09 before trusting any of it.

Two sources, and confusing them gives wrong answers:

- **Code** — `assembly_valheim.dll`, via `dotnet tool install --global ilspycmd`, then
  `ilspycmd -o <outdir> -p <path>/valheim_Data/Managed/assembly_valheim.dll`.
- **Prefab-serialized values** — *not in the DLL*. The `Character.cs` class defaults
  (`m_walkSpeed = 5`, `m_runSpeed = 20`) are placeholders the Player prefab overrides with
  1.6 / 7.0. Read them with UnityPy from the addressable bundles under
  `valheim_Data/StreamingAssets/SoftRef/Bundles/`; grep `SoftRef/manifest_extended` for an
  asset path to find its bundle. Most gameplay prefabs are in **`c4210710`**.

---

## Unit scale

**1 Unity unit = 1 metre**, confirmed from collider geometry (all prefabs at scale 1,1,1):

| Prefab | CapsuleCollider |
|---|---|
| Player | h 1.85, r 0.49 |
| Greyling | h 1.50 |
| Boar | h 1.40, r 0.50 |
| Troll | h 6.61, r 1.00 |

`UpdateWalking` assigns `m_body.linearVelocity` directly, so speeds are literal m/s.

## World extent

| | |
|---|---|
| Playable radius | 10,000 m (`WorldGenerator.worldSize`) |
| Hard edge | 10,500 m (`waterEdge`) |
| Diameter / area | 20 km / ~314 km² |
| Zone size | 64 m (`m_width × m_scale`; shipped prefab is width 32, so 2 m vertex spacing) |
| Water level | y = 30 (`ZoneSystem.c_WaterLevel`) |
| Height mapping | world Y = normalized × 200 (`GetHeightMultiplier`), zones at y = 0 — so **normalized 0.15 is the waterline**, 0.4 the mountain threshold |

Cross-check: the 76,470-zone pregen run × 64² m = 313.2 km² vs π × 10,000² = 314.2 km².

See `src/CraiginsValheimMod/WorldGen/DESIGN_NOTES.md` for the seven places the edge is
enforced and the Ashlands/Deep North gap geometry.

## Player movement (Player.prefab)

| Field | Value | Applied as |
|---|---|---|
| `m_walkSpeed` | 1.6 m/s | no modifiers |
| `m_speed` (jog) | 4.0 m/s | `× (1 + equipMod)` — **no skill factor** |
| `m_runSpeed` | 7.0 m/s | `× (1 + runSkill × 0.25) × (1 + equipMod × 1.5)` |
| `m_swimSpeed` | 2.0 m/s | Swim skill changes stamina drain, not speed |
| `m_crouchSpeed` | 2.0 m/s | also used when encumbered |
| `m_acceleration` | 0.8 | reaches jog speed in ~0.1–0.15 s |
| `m_baseHP` / `m_baseStamina` | 25 / 50 | |
| `m_runStaminaDrain` | 8.0/s | `× Lerp(1, 0.5, runSkill)` — halves at skill 100 |
| `m_swimStaminaDrain` | 6.0 → 3.0/s | lerped by Swim skill |
| `m_maxCarryWeight` | 300 | |

At Run skill 0 with no gear, a sprint is 7.0 m/s but lasts **6.25 s (~44 m)** on 50 stamina.
The only sustainable overland pace is the 4.0 m/s jog, which is skill-independent.

**Measured in-game 2026-09-05** (flat meadows, rags only): 79.03 m in 20.86 s = 3.79 m/s,
confirming the 4.0 m/s jog. Residual is start/stop timing.

Two behaviours worth knowing:
- `moveDir` is normalized **only inside the `m_running` branch**, so on a gamepad jog speed
  scales directly with stick deflection.
- `ProjectOnPlane(vector, groundNormal).normalized * magnitude` — speed is held *along the
  ground surface*, so horizontal progress drops on any slope.

## Equipment movement modifiers

Summed across all equipped slots; feeds `GetJogSpeedFactor` at ×1 and `GetRunSpeedFactor` at
**×1.5**.

| Modifier | Items |
|---|---|
| −20% | ShieldIronSquare |
| −15% | All sledges and battleaxes |
| −10% | All tower shields, ShieldSerpentscale |
| −5% | Most one-handers, bows, crossbows, **torch**, pickaxes, ArmorIronChest, ArmorFlametalChest |
| −2% | Mage and Root chest/legs |
| +3% | Fenring chest/legs/helmet — the only positives in the game |

Rags and leather armour are 0%.

## Boats

Speed is emergent, not a field. Both thrust and drag are applied per physics step with mass
factored in, so **hull mass cancels out of top speed** entirely.

```
v_max = sqrt( windAngleFactor × windLerp × m_sailForceFactor / (m_dampingForward × num4) )
num4  = 9.81 / (50 × m_force)        # submersion at float equilibrium
```

| | `m_sailForceFactor` | `m_dampingForward` | `m_force` | mass |
|---|---|---|---|---|
| Raft | 0.05 | 0.005 | 0.5 | 1000 |
| Karve | 0.03 | 0.001 | 1.0 | 1000 |
| VikingShip | 0.05 | 0.001 | 1.0 | 2000 |
| VikingShip_Ashlands | 0.085 | 0.002 | 1.0 | 3000 |

Derived top speeds (m/s) — **not measured in-game**, and idealised: the model assumes
`Physics.gravity` is 9.81 and ignores wave losses and the fraction of `AddForceAtPosition`
that becomes rotation rather than linear velocity. Treat as upper bounds.

| | Full sail, max wind | Min wind | Half sail | Rowing |
|---|---|---|---|---|
| Raft | 4.2 | 2.1 | 3.0 | 2.3 |
| Karve | 10.3 | 5.2 | 7.3 | 4.5 |
| VikingShip | 13.4 | 6.7 | 9.4 | 4.5 |
| Drakkar | 12.3 | 6.2 | 8.7 | 3.6 |

The rowing column is weakest — rowing thrust is scaled by `fixedDeltaTime` while drag is not,
so it assumes a 0.02 s step. Sail figures are timestep-independent.

From `GetWindAngleFactor`: a **beam reach is marginally faster than a tailwind** (crosswind
gives factor 1.0 but the thrust vector sits 45° off heading, netting 0.707 forward vs the
tailwind's 0.70), and sailing into wind is **exactly zero**, not merely slow.
`Trailership` has `m_sailForceFactor = 0` — no sail, it is towed.

## Travel times across the 20 km diameter

| Pace | Centre → edge (10 km) | Full diameter (20 km) |
|---|---|---|
| Walk 1.6 | 1 h 44 m | 3 h 28 m |
| Jog 4.0 | 42 min | 83 min |
| Raft | 39 min | 79 min |
| Karve | 16 min | 32 min |
| VikingShip | 12.5 min | 25 min |

Straight-line floors, not travel estimates — slopes, tacking and terrain all push real journeys
well past these.

## Dungeon interiors and the vertical budget

Three different numbers get called "the dungeon height cap"; only the last one actually
caps anything.

| Number | Where | What it really is |
|---|---|---|
| `y > 3000` | `Character.InInterior(Vector3)` | A boolean "am I indoors". Nothing above it is special-cased further. |
| `+5000` | `Location.Awake` | Where an interior is *parked*: `(zoneCentre.x, location.y + 5000, zoneCentre.z)`. A convention, not a limit. |
| `m_zoneSize` | `DungeonGenerator` prefab | The real cap. `IsInsideDungeon` rejects any room whose 8 corners leave `Bounds(m_zoneCenter, m_zoneSize)`. |

At runtime `Generate()` overwrites the serialized `m_zoneCenter` with
`GetZonePos(zone)` in XZ and `transform.position.y - m_originalPosition.y` in Y — and
`m_originalPosition` is `(0,0,0)` on every shipped prefab — so **the box is centred on the
generator's own world position** and follows it anywhere. The serialized `m_zoneCenter.y = 50`
only applies in the editor, where `ZoneSystem.instance` is null.

The interior EnvZone trigger is the interior prefab scaled to `(64, 500, 64)` — a 500 m tall
column, which is where a ±250 m band around the interior is a safe filter for "belongs to this
dungeon".

Extracted from `c4210710`, 2026-09-07. Algorithm 0 = Dungeon, 2 = CampRadial (surface camps,
listed for contrast — they sit in the terrain, not in an interior).

| Generator | Alg | Rooms min–max | `m_zoneSize` (X, **Y**, Z) | CustomInteriorTransform |
|---|---|---|---|---|
| DG_ForestCrypt | 0 | 20–40 | 64, **64**, 64 | no |
| DG_SunkenCrypt | 0 | 20–30 | 64, **64**, 64 | no |
| DG_Cave | 0 | 3–64 | 64, **256**, 64 | yes |
| DG_Hildir_Cave | 0 | 10–64 | 64, **256**, 64 | yes |
| DG_Hildir_ForestCrypt | 0 | 20–40 | 64, **256**, 64 | yes |
| DG_DvergrTown | 0 | 16–96 | 64, **256**, 64 | yes |
| DG_DvergrBoss | 0 | 512–512 | 64, **256**, 64 | yes |
| DG_Hildir_PlainsFortress | 0 | 256–512 | 32, **132**, 32 | yes |
| DG_GoblinCamp | 2 | 15–25 | 64, 64, 64 | no |
| DG_MeadowsVillage | 2 | 20–30 | 64, 64, 64 | no |
| DG_MeadowsFarm | 2 | 20–30 | 64, 64, 64 | no |
| DG_AshlandRuins | 2 | 20–30 | 64, 64, 64 | no |
| DG_FortressRuins | 2 | 20–30 | 64, 64, 64 | no |

So the cap is per-prefab and Iron Gate already raised it 4× for the cave-family dungeons.
Note the XZ extent is 64 — exactly one zone — on all but the Plains fortress, which uses 32.
That is not a coincidence: **ZDO sectors are computed from XZ only** (`ZDO.SetSector` →
`ZoneSystem.GetZone`, `Vector2s`), and so are `ZNetScene.InActiveArea` / `OutsideActiveArea`
and `ZDOMan.FindSectorObjects`. Nothing in the engine partitions by height. Growing a dungeon
upward is therefore free; growing it sideways past ±32 m spills rooms into a neighbouring
zone's sector, where they load and unload independently of the rest of the dungeon.

Other height-sensitive code worth knowing:

- `RandEventSystem` skips anything at `y > 3000` — no raids in interiors, at any altitude.
- `ZSyncTransform` rescues objects that fall below `y = -5000`. There is no upper equivalent.
- `Heightmap` is `RenderGroup.Overworld`, which `RenderGroupSystem` disables whenever the local
  player is `InInterior()` — so the surface isn't drawn beneath an interior regardless of height.
- `SnapToGround.SnappAll()` runs at the end of every `Generate()`, and `GetGroundData` raycasts
  from `p + up*5000` for 10,000 m. At the vanilla +5000 band that window still reaches the
  terrain, so a `SnapToGround` on a dungeon prop would be yanked to the surface; above
  y = 10,000 the raycast misses everything and the else-branch leaves `p.y` untouched.
- `Pet.cs` has an easter egg gated on `4000 < y < 5090` — harmless, but a reminder that the
  5000 band is hardcoded in more places than `Location.Awake`.

## Dungeon spawners do not respawn

`CreatureSpawner.m_respawnTimeMinuts = 20f` in the DLL is a **class default that every
shipped spawner overrides**, and reading it as fact gives exactly the wrong answer. The gate is:

```csharp
// CreatureSpawner.UpdateSpawner
if ((m_respawnTimeMinuts <= 0f) & alreadySpawned) return;   // one-shot, forever
```

Extracted from `c4210710`, 2026-09-08. Respawn minutes, by prefab:

| Value | Prefabs |
|---|---|
| **0 (never respawns)** | Every standard spawner — Draugr, Draugr_Elite, Draugr_Ranged, Skeleton, Skeleton_poison, Ghost, Blob, BlobElite, Greydwarf(+Elite/Shaman), Goblin(+Archer/Brute/Shaman), Dverger*, Seeker, SeekerBrute, Fenring, Cultist, Charred*, Morgen, Wraith, Ulv, Tick, Troll, StoneGolem, Hatchling, FallenValkyrie, Bat, imp, Leech_cave, Boar, Chicken, Hen |
| 1 | Spawner_Location_Greydwarf / _Elite / _Shaman (surface camps) |
| 10 | Spawner_Fish4 |
| 5 | Spawner_imp_respawn |
| 60 | Spawner_Draugr_respawn_30, Spawner_Skeleton_respawn_30, Spawner_BlobTar_respawn_30, Spawner_BogWitchKvastur_respawn_30 |
| 240 | Spawner_Seeker_respawn_240, Spawner_SeekerBrute_respawn_240, Spawner_Tick_stared_respawn_240 |

The respawning variants are separate, explicitly-named prefabs. **A dungeon's creature census
is fixed and finite** — which is why a crypt can permanently deny you a Ghost or Draugr trophy,
and why regenerating the dungeon is the only vanilla-compatible fix.

`SpawnArea` is the other spawner type and behaves differently — continuous, on an interval,
but capped both concurrently (`m_maxNear`) and for its whole lifetime (`m_maxTotal`):

| Prefab | Interval s | maxNear | maxTotal |
|---|---|---|---|
| Spawner_DraugrPile | 5 | 2 | 100 |
| BonePileSpawner | 5 | 2 | 100 |
| Spawner_GreydwarfNest | 10 | 3 | 100 |
| EvilHeart_Forest / _Swamp | 20 | 3 | 30 |
| Spawner_CharredStone(+_Elite/_event) | 10 | 3 | 100 |
| Spawner_CharredCross | 25 | 4 | 100 |
| Spawner_Kvastur | 60 | 1 | 1 |

So "clear every enemy" is a reachable, stable state in a dungeon — the `CreatureSpawner`s are
one-shot and the `SpawnArea` piles are destructible (and exhaust at 100 anyway).

## Weather fog density (EnvSetup.m_fogDensity*, game 1.0.16)

EnvMan's own `m_environments`/`m_biomes` hold only the original 21 weathers and 6 biomes.
Mistlands, Ashlands and Deep North weather is **appended at runtime** by
`ZoneSystem.SetupLocations` from the `_LocationList_*` prefabs (bundle `d59cfac`) via
`EnvMan.AppendEnvironment`/`AppendBiomeSetup` - read the `LocationList` MonoBehaviours, not
EnvMan, to see them. Ground mist (`Mister`/`ParticleMist`) is a separate system from this fog.

Density is night / morning / day / evening; weight is the share of the biome's weather roll.

| Weather | Biome (weight) | Fog density | Wet |
|---|---|---|---|
| Mistlands_clear | Mistlands (1.5) | 0.04 / 0.05 / 0.02 / 0.04 | no |
| Mistlands_rain | Mistlands (0.1) | 0.04 / 0.05 / 0.03 / 0.04 | yes |
| Mistlands_thunder | Mistlands (0.1) | 0.04 / 0.05 / 0.03 / 0.04 | yes |
| Clear | Meadows (5.0), Ocean (1.0) | 0.01 / 0.01 / 0.003 / 0.01 | no |
| Misty | Meadows (0.2), Black Forest (0.1), Plains (0.4), Ocean (0.1) | 0.15 / 0.10 / 0.02 / 0.10 | no |
| Rain / LightRain / ThunderStorm | Meadows, Black Forest, Ocean | 0.03 flat | yes |
| SnowStorm | Mountain (1.0) | 0.05 flat | no |
| Ashlands_ashrain | Ashlands (1.5) | 0.03 / 0.05 / 0.02 / 0.01 | no |
| Twilight_SnowStorm | Deep North (0.5) | 0.06 flat | no |
| SunkenCrypt, DN_Bossroom | interior | 0.20 flat | no |

0.15 (Misty at night) is the heaviest outdoor fog the game ships; 0.2 exists only in interiors.

## Ashlands lighting (EnvSetup, game 1.0.16)

Why the Ashlands are dark. None of the four weathers sets `m_alwaysDark`, and their fog is
no denser than ordinary rain - the biome is simply lit dimly. Same source as the fog table
above (`_LocationList_Ashlands`, bundle `d59cfac`). Meadows `Clear` is given for comparison.

Colours are r / g / b. "Direct" is light intensity times the luminance of the sun colour
(0.2126 r + 0.7152 g + 0.0722 b), which is what actually lands on a surface.

| Weather | Weight | Ambient day | Ambient night | Light day / night | Sun colour day | Sun colour night | Direct day / night |
|---|---|---|---|---|---|---|---|
| Ashlands_ashrain | 1.5 | 0.67 / 0.46 / 0.43 | 0.20 / 0.33 / 0.34 | 2.2 / 0.4 | 0.52 / 0.38 / 0.31 | 0.17 / 0.37 / 0.63 | 0.90 / 0.14 |
| Ashlands_CinderRain | 0.2 | 0.41 / 0.44 / 0.48 | 0.36 / 0.27 / 0.25 | 1.6 / 0.5 | 0.49 / 0.20 / 0.18 | 0.42 / 0.45 / 0.56 | 0.41 / 0.22 |
| Ashlands_misty | 0.1 | 0.50 / 0.54 / 0.58 | 0.20 / 0.33 / 0.33 | 1.0 / 0.6 | 0.52 / 0.44 / 0.34 | 0.49 / 0.52 / 0.66 | 0.45 / 0.32 |
| Ashlands_storm | 0.05 | 0.41 / 0.44 / 0.48 | 0.36 / 0.27 / 0.25 | 1.5 / 1.5 | 0.49 / 0.20 / 0.18 | 0.42 / 0.45 / 0.56 | 0.39 / 0.67 |
| Clear (Meadows) | - | 0.46 / 0.57 / 0.71 | 0.36 / 0.37 / 0.49 | 1.7 / 1.0 | 1.00 / 0.77 / 0.48 | 0.36 / 0.38 / 0.49 | 1.36 / 0.39 |

Ambient luminance is 0.44-0.54 by day and 0.29-0.31 by night in the Ashlands, against 0.56
and 0.37 for `Clear`. So the usual weather, ashrain, has about two thirds of Meadows' direct
sunlight and about a third of its moonlight, with ambient only 10-20% lower. The other three
weathers are darker by day: under half of Meadows' direct light.

`AshlandsBrightness` multiplies the ambient colours, both light intensities and all eight fog
colours of these four weathers. Fog density, sun colour and cloud opacity are not changed.

## Resting, Rested and health regeneration (game 1.0.16)

`Player.UpdateFood` heals every 10 s by the sum of `m_foodRegen` over the foods eaten, times
the multiplier from `SEMan.ModifyHealthRegen`. No food eaten means no regeneration at all.
`SE_Stats.ModifyHealthRegen` is additive above 1 (`mult += m - 1`), so the two effects below
give x3.5 together, not x4.5.

| Effect | Class | When | Health | Stamina | Eitr | Duration |
|---|---|---|---|---|---|---|
| Resting | SE_Cozy | near a fire, sitting or sheltered, unnoticed, not cold / wet / burning | x3 | x4 | x4 | while the conditions hold; grants Rested after 20 s |
| Rested | SE_Rested | carried afterwards | x1.5 | x2 | x2 | 480 s + 60 s per comfort level above 1 |

`RestingHealthRegenMultiplier` scales the finished health multiplier while Resting is active.

## Taming (Tameable, game 1.0.16)

Every tameable prefab has the same values: Asksvin, Boar, Lox, Moose and Wolf, plus the
ones that start tamed.

| Field | Value |
|---|---|
| `m_tamingTime` | 1800 s |
| `m_fedDuration` | 600 s (Skeleton_Friendly: 30 s) |
| tick (`TamingUpdate`) | every 3 s, on the ZDO owner |
| `m_tamingSpeedMultiplierRange` | 60 m |
| `m_tamingBoostMultiplier` | 2, per nearby player with the TamingBoost attribute |

The only source of TamingBoost is the tamer mead (`Potion_tamer`, 600 s). The tick only runs
while the animal is fed, not alerted and loaded. `TamingSpeedMultiplier` scales the tick
before the mead's boost is applied.

## Harpoon (SpearChitin, game 1.0.16)

Bundle `c4210710`. The code defaults in `SE_Harpooned.cs` are placeholders; the `Harpooned`
asset overrides most of them.

| Where | Field | Value | Code default |
|---|---|---|---|
| SpearChitin `m_shared` | `m_damages` | 10 pierce, nothing per level (`m_maxQuality` 1) | |
| | `m_attackForce` | 20 | |
| | `m_backstabBonus` | 1 | |
| | `m_attackStatusEffect` | `Harpooned`, chance 1 | |
| | `m_attack.m_projectileVel` | 30 | |
| projectile_chitinharpoon | `m_hitFriendly` | off | |
| | `m_noDamageFriendly` | off | |
| | `m_dodgeable` / `m_blockable` | off / off | |
| | `m_hitNoise` | 40 | |
| Harpooned (SE_Harpooned) | `m_maxDistance` | 40 m | 30 |
| | `m_breakDistance` | 8 m | 4 |
| | `m_pullSpeed` | 1000 | 5 |
| | `m_pullForce` / `m_forcePower` | 1 / 2 | 0 / 2 |
| | `m_staminaDrain` | 0.1 per 0.1 s, times pull and target mass | 10 |
| | `m_ttl` | 0 (lasts until broken or released) | |

The projectile's own damage, push force and status effect are blank on the prefab;
`Projectile.Setup` fills them from the weapon's HitData at throw time.

With `m_hitFriendly` off, `Projectile.IsValidTarget` rejects a target that is not the
thrower's enemy unless the thrower has PvP enabled. A tamed animal is never a player's enemy,
so a vanilla harpoon passes through tames with PvP off, and hits, damages and hooks them with
PvP on. `HarpoonHitsTamedWithoutPvP` removes the PvP requirement and
`HarpoonNoDamageToTamed` removes the damage.

## Vines (Vine + Pickable, game 1.0.16)

Bundle `c4210710`. VineAsh is the vineberry vine, VineGreen is ivy. A vine is a grid of
segments, each its own prefab instance with its own Pickable.

| Field | VineAsh | VineGreen |
|---|---|---|
| `m_size` (segment spacing) | 1.5 m | 1.5 m |
| `m_maxBerriesWithinBlocker` | 1 | 0 |
| BerryBlocker box (deep x tall x wide) | 0.44 x 9.11 x 4.86 m | same |
| `m_growTime` / `m_growTimePerBranch` | 120 s / 120 s | 100 s / 100 s |
| `m_growCheckTime` | 45 s | 45 s |
| `m_growCheckChance` / per branch | 0.5 / -0.495 | 0.7 / -0.495 |
| `m_growChance` | 1 | 1 |
| `m_closeEndChancePerBranch` / per height / max | 0.973 / 0.305 / 0.98 | same |
| `m_maxGrowWidth` / per height / ignore chance | 0.8 / 0.25 / 0.291 | same |
| `m_growSides` / `m_growUp` / `m_growDown` | on / on / off | same |
| `m_randomOffset` | 0.6 | 0.6 |
| `m_minScale` / `m_maxScale` | 0.75 / 1.3 | 0.75 / 1.2 |
| Pickable item, `m_amount` | Vineberry, 3 | Vineberry, 3 |
| Pickable bonus drop (20%, 1-3 stacks of 1-3) | VineberrySeeds | VineGreenSeeds |
| `m_respawnTimeMinutes` | 200 | 200 |
| `m_respawnTimeInitMin` / `Max` | 0 / 150 | 0 / 150 |
| `m_defaultPicked` | on | on |
| `m_hideWhenPicked` | its own `Berries` child | VineAsh's `Berries`, not its own |

Saplings (VineAsh_sapling, VineGreen_sapling): `m_growTime` 200-300 s, `m_growRadius` 0.5,
`m_growRadiusVines` 1.8 (no existing vine within 1.8 m), `m_attachDistance` 1.8, cultivated
ground required.

How berries are decided (`Vine.CheckBerryBlocker`, run by the segment's owner):

- Pickable checks every 60 s. Once 200 minutes of world time have passed since the picked
  time, it asks the vine.
- The vine counts other segments with berries whose colliders overlap the BerryBlocker box.
  A segment's own collider is 1.2 m square, so another fruiting segment blocks within about
  3 m sideways and 5 m up or down, on the same wall face. The count must be below
  `m_maxBerriesWithinBlocker`. Ivy's limit is 0, so ivy never passes.
- The segment must also know of two or more neighbours. That is `m_vineState`, which is
  NonSerialized and never in the ZDO. It is set when a segment is grown (the side it grew
  from) and when `CheckGrow` finds a vine in its block sensor. After a reload only left,
  right and above can be relearned, because `m_growDown` is off.
- A failed check calls `Pickable.SetPicked(true)`, which writes the picked time as now. So
  every failure costs another 200 minutes.
- A fresh Pickable has no picked time, and `UpdateRespawn` backdates it by a random 0 to
  15000 minutes (`m_respawnTimeInitMax` x 100). So a new segment is checked within a minute
  of sprouting, fails for lack of neighbours, and starts its first 200 minutes then.
- The physics query buffer holds 20 colliders and the mask includes building pieces, so on
  a crowded wall a fruiting neighbour can be missed.

`VineberryIgnoresAdjacency` and `IvyIgnoresAdjacency` replace the check: a segment passes
unless it sprouted less than one respawn time ago.

## Grappling hook and reloading weapons (game 1.0.16)

Bundle `c4210710`. Every item whose primary attack has `m_requiresReload` set:

| Item | `m_reloadTime` | `m_blockReloadTime` | Reload drain | Skill |
|---|---|---|---|---|
| GrapplingHook | 2 s | 1.1 s | none | none |
| Crossbows (Arbalest, Ripper and Gold families, 8 items) | 3.5 s | 0 | 1 stamina per s | Crossbows |
| StaffLightning | 1.9 s | 0 | 25 eitr per s | Elemental magic |

`ItemData.GetWeaponLoadingTime` halves the reload time at skill 100. The grappling hook has
no skill, so its reload is always 2 s. No secondary attack requires a reload.

GrapplingHook item: 10 pierce, `m_attackForce` 20, 15 stamina per shot for either attack,
projectile speed 40, animation `crossbow_fire`, reload animation `reload_crossbow`,
durability 300, one quality level.

| Field | GrapplingPoint (primary) | GrapplingPointSecondary |
|---|---|---|
| Spawned by | Projectile_GrapplingHook | Projectile_GrapplingHook_secondary |
| `Method` | ConstantVelocity | ConstantVelocity |
| `m_pullForce` | 20 | 15 |
| `m_maxLength` | 60 m | 70 m |
| `m_jumpOnDone` | 10 | 0 |
| `m_ttlSE` | 2 s | 3 s |
| `m_FOVTarget` | 110 | none |
| `m_repellingInForce` | 5 | 40 |
| `m_closeBreakDist` / `m_breakEarlyTime` | 1 m / 0.5 s | same |

| Field | Projectile_GrapplingHook (primary) | Projectile_GrapplingHook_secondary |
|---|---|---|
| `m_ttl` | 1 s | 1 s |
| `m_gravity` | 10 | 5 |
| `m_drag` | 0 | 0 |
| `m_spawnOnTtl` | off | off |
| `m_stayTTL` / `m_stayAfterHitStatic` | 1 s / off | same |
| `m_rayRadius` | 0 | 0 |
| `m_doOwnerRaytest` | on | on |
| `m_hitNoise` | 40 | 40 |

Both attacks fire at `m_projectileVel` 40 with `m_launchAngle` 0 and `m_projectileAccuracy` 0,
from 1.5 m up, 1 m forward and 0.2 m to the right of the player. `Player` does not override
`GetAimDir`, so the hook leaves along the look direction exactly.

Both projectiles live 1 s, so the reach of a shot is about 40 m, and gravity pulls the hook
about 5 m below the look direction in that time (2.5 m for the secondary). A hook whose
lifetime runs out is destroyed without attaching. `GrapplingHookNoGravity` zeroes `m_gravity`
on the fired hook. `GrapplingHookNoRangeLimit` raises `m_ttl` to 60 s and sets the point's
`m_maxLength` to infinity.

With those limits gone the reach is bounded by what is loaded. Terrain and objects exist only
for the zones around the player: `ZoneSystem.CreateLocalZones` and `ZDOMan.FindSectorObjects`
both use `SimulationDistance.NearSimulationDistance`, which is 2 zones of 64 m at the original
setting. That is 128 to 192 m along an axis, depending on where in their zone the player
stands. `ZNetScene.RemoveObjects` destroys any object whose zone has left that set, and its ZDO
too when it is not persistent, which the hook and the point are not. The check is by zone, so
height never triggers it. Ownership is only handed over for persistent ZDOs
(`ZDOMan.ReleaseNearbyZDOS`), so the hook stays with whoever fired it.

A reload cannot be queued while `Player.m_blockReload` is above zero (set to
`m_blockReloadTime` on each shot) or while `Player.m_grappling` is, which a live grappling
point resets to 0.2 every frame. `GrapplingHookNoReload` skips the reload altogether.
