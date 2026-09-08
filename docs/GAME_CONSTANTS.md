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
