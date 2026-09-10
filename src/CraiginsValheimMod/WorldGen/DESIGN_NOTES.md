# Authored continent — design notes (not implemented)

Goal: stop letting Perlin noise decide where the biomes go. Draw a biome map by hand (an image),
and have the game read that map instead of its own biome noise — so the continent has a designed
layout rather than a random one.

**Scope decision: biomes only, not heights.** An earlier option was to feed in a full external
heightmap and bypass terrain generation entirely. That was rejected — see
[Why not a full external heightmap](#appendix-why-not-a-full-external-heightmap) at the bottom
for what it would have cost. This document is about the biome-only version.

Everything below is verified against the current
decompiled `assembly_valheim.dll` (2026-02-19 build, via ILSpy). Line numbers are from that
decompile and will drift; class and method names are the durable part. Re-check after a game
update, the same way `TerrainMaskPatches` documents its provenance.

---

## Status — where this was left (2026-09-05)

**No code written.** Investigation only; everything here is decompile-verified but unbuilt.

Wider context: this exploration is scouting what it would take to use Valheim as the base for
a different game (a dynamic ARPG) with a fully authored world, not just to improve the mod.
That reframes two things — pregeneration becomes unnecessary (it only exists to freeze
noise-driven placement before players arrive), and the "every client needs the map file"
constraint dissolves once the client *is* the game rather than vanilla + a mod.

The governing rule for anything authored: it must be either **in the build on every client**
(terrain height, biome, generation rules — recomputed locally each load, never transmitted, so
they cost zero bandwidth at any world size) or **a ZDO** (objects, placement, terrain edits).
Nothing else crosses the wire.

Next session, in order:
1. Build the `dumpbaseheight` console command — the authoring canvas. Nothing else can be
   evaluated without it.
2. Decide the ocean-check ordering question in [Open questions](#open-questions).
3. Then the `BiomeMap` loader and the `GetBiome` prefix.

Related: `docs/GAME_CONSTANTS.md` for extracted world/movement/boat values and how to re-extract
them after the 1.0 patch.

---

## The patch point

There is exactly one:

```
WorldGenerator.GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
```

Public, instance, non-virtual — a Harmony prefix, same shape as the existing
`TerrainMaskPatches`. Every biome query in the game reaches it:

- `Heightmap.GetBiome` delegates to it for distant LOD and `waterAlwaysOcean` queries, and
  otherwise reads `m_cornerBiomes`, which `HeightmapBuilder.Build` filled from it.
- `Heightmap.FindBiome` / `FindBiomeClutter` (~31 call sites across `Player`, `Beehive`,
  `SpawnSystem`, `ClutterSystem`, …) go through `Heightmap`.
- `WorldGenerator.GetBiomeArea`, `GetHeight`, `GetPregenerationHeight` all call it directly.
- `Minimap.GenerateWorldMap` calls it directly.

So one prefix moves the whole game. There is no second biome source to keep in sync.

The three overloads (`GetBiome(Vector3)`, `GetBiome(float, float, …)`, `GetBiomeArea`) all funnel
into the `(float, float, float, bool)` one, so patch that and the others follow.

---

## The thing that surprised me: biome selects the height function

**Painting the biome map does change the terrain shape.** `GetBiomeHeight` switches on biome and
calls a different height function per biome, all layered on the same seed-derived
`GetBaseHeight`. So this is not "biome paint on fixed ground" — it's "biome paint that also
picks which sculpting rule applies to the seed's ground".

Useful frame: normalized height maps straight to world Y. `GetHeightMultiplier()` is a flat
`200f`, `ZoneSystem.GetZonePos` puts every zone at `y = 0`, so **world Y = normalized × 200**.
Water sits at `y = 30` (`ZoneSystem.c_WaterLevel`), i.e. **normalized 0.15 is the waterline**.

| Biome | Height rule | Normalized result |
|---|---|---|
| `Ocean` | `GetBaseHeight` verbatim, no noise | base |
| `BlackForest` | base + n·0.1 + rivers | base (the neutral one) |
| `Meadows` | as BlackForest, plus flattening above 0.15 scaled by `(1 − clamp01(base/0.4))·0.75` | base, softened |
| `Plains` | identical formula to Meadows (same 0.15 / 0.4 / 0.75 constants) | base, softened |
| `Mistlands` (vanilla) | base + n·0.4, terraced via `Ceil(h·400)/400` | base, craggy |
| `Mistlands` (our patch) | base + n·0.1 + rivers | base — behaves as BlackForest |
| `Swamp` | **constant 0.137** + n·0.03 + rivers. Base height never consulted | ~0.137–0.167 → y 27–33 |
| `Mountain` | base + **(base − 0.4)** + n·0.2 + `perlin·2·tilt` | doubles the *signed* excess over 0.4 |
| `DeepNorth` | base + `max(0, base − 0.4)`, then ×1.2 | clamped, so safe on low ground |
| `AshLands` | own function, plus a paint mask | special |

Everything is then multiplied by `200 × CreateAshlandsGap(wx,wy) × CreateDeepNorthGap(wx,wy)`
(the gap terms are skipped when `preGeneration` is true).

### Rules that fall out of that table

**Meadows / BlackForest / Plains / Mistlands / Ocean are freely interchangeable.** They're all
`base + gentle noise` and differ only in a flattening term. Repaint between them anywhere and
the ground barely moves — boundaries stay smooth. This is the safe palette, and it covers most
of what an authored continent needs.

**`SmoothMistlandsTerrain` already pulled Mistlands into that group.** Our patch replaces the
craggy terraced formula with `base + n·0.1 + rivers` — the BlackForest rule. Nice accident: with
the toggle on, Mistlands is a drop-in for any other Group-A biome. With it off, painting large
Mistlands regions will read as jagged and boundaries against Meadows will step.

**Swamp ignores its surroundings entirely.** A constant `0.137` puts the ground at y ≈ 27, about
3 m *below* the waterline, with up to ~6 m of noise on top — which is exactly why vanilla swamps
are ankle-deep water. Paint Swamp on a hillside and you get a flat marsh plateau at the waterline
with a cliff around it. **Swamps have to be painted where the seed's base height is already near
0.137**, or the boundary will be a wall. This is the single biggest authoring constraint.

**Mountain on low ground digs a hole.** The `(base − 0.4)` term is *signed and unclamped*. At
base 0.2 you get `0.2 + (0.2 − 0.4) = 0.0` → sea floor, not a peak. Painting Mountain only raises
terrain where base height already exceeds 0.4; below that it actively sinks it, and the
`perlin·2·tilt` term adds roughness on the way down. **Mountains can only be painted onto ground
the seed already made high.** (`DeepNorth` uses `max(0, …)` instead, so it doesn't have this
problem — it just multiplies everything by 1.2.)

**Leave AshLands and DeepNorth where vanilla puts them.** Two independent reasons:

1. `CreateAshlandsGap` / `CreateDeepNorthGap` multiply *every* height — whatever the biome — by a
   smoothstep that reaches 0 in a ±400 m band around the Ashlands and Deep North boundary rings.
   That's the ocean moat across the far south and far north. It's distance-based and applied
   after the biome switch, so painting land into that band still sinks it.
2. The statics in the next section bypass biome painting completely.

---

## What does *not* follow the biome map

These are `static` and purely distance-based. They never consult `GetBiome`, so a repainted map
and these will disagree:

| Static | Consulted by | Symptom if the map disagrees |
|---|---|---|
| `IsAshlands(x, y)` | `EnvMan` (weather), `Character` (heat damage) | Burning air outside the painted Ashlands, none inside |
| `IsDeepnorth(x, y)` | `EnvMan` | Wrong weather / ambience |
| `GetAshlandsOceanGradient` | `Ship`, `Character`, `Minimap` | Boat damage over the wrong water |
| `InForest` / `GetForestFactor` | `SpawnSystem`, `ClutterSystem`, `ZoneSystem`, `Minimap` | Tree density and spawn rules ignore the painted biome |

All four are patchable — Harmony handles statics fine — but each is a second front, and
`GetAshlandsOceanGradient` returns a *continuous* gradient, so faking it needs a distance field in
the map rather than a biome id. Recommendation for a first version: **don't paint AshLands or
DeepNorth at all.** Sample vanilla `GetBiome` for those and only override the inner biomes.

`GetForestFactor` is worth a second look separately — it's a cheap independent Fbm and adding a
forest-density layer to the map is a small, self-contained follow-up once the biome layer works.

---

## The coastline stays the seed's

`GetBiome` classifies as `Ocean` below `oceanLevel` (default 0.02), but the *visible* waterline is
normalized 0.15 (y = 30). Land is drawn wherever base height clears 0.15 regardless of what biome
is painted there — painting Meadows over deep water gives submerged meadows, not an island.

That band between 0.02 and 0.15 is worth knowing: classified as a land biome, actually underwater.
It's what vanilla's shallow near-shore water is.

Practical consequence: **you author biomes onto a coastline you don't control.** So the workflow
has to start from the seed, not from a blank canvas:

1. Pick a seed whose landmass shape you like (seed browsers, or the pregenerator here).
2. Export that seed's base-height field as a background image — a small console command that walks
   the world calling `GetBaseHeight` and writes a PNG. Mark the 0.15 waterline and the 0.4
   mountain line, since those are the two thresholds that constrain painting.
3. Paint biomes onto *that* canvas in an image editor, respecting the rules above.
4. Feed the painted image back in.

Step 2 is small and worth building first — without it, painting is guesswork.

---

## World extent — how big the canvas actually is

The playable disc is **10,000 m radius / 20 km across / ~314 km²**, with a hard edge at 10,500 m.
Cross-checked two ways: `WorldGenerator.worldSize`/`waterEdge`, and the `pregenerateworld` run,
whose 76,470 zones × 64² m = 313.2 km² against π × 10,000² = 314.2 km² — agreement to 0.3%.

That limit is **not** one constant. It is enforced independently in seven places, all of them
small and all Harmony-reachable:

| Where | What it does |
|---|---|
| `WorldGenerator.GetBaseHeight` | past 10,000 lerps to −0.2 by 10,500; past 10,490 to −2 |
| `WorldGenerator.GetBiomeHeight` | `Length > 10500` returns −400, before the biome switch |
| `WorldGenerator.GetEdgeHeight` | same ramp shape, applied at the rim |
| `Ship.ApplyEdgeForce` | past **10,420** adds force along `Normalize(position)` — *outward*. It shoves you over, it is not a wall |
| `Player.cs` edge force | the same treatment on foot |
| `WaterVolume` | water ceases to exist past 10,500 — the waterfall |
| `EnvMan` | `m_edgeOfWorldWidth` edge fog |
| `ZoneSystem` | `maxRange = 10000f` in location placement; random-zone pickers bounded the same way |

So "confined to 20 km" is a default, not a floor.

### The polar caps cost ~10% before you start

`CreateAshlandsGap` / `CreateDeepNorthGap` sink *all* terrain — whatever biome is painted — in a
±400 m band around rings at `Length(x, y∓4000) = 12000`. At x = 0 those cross **y = −8000**
(Ashlands) and **y = +8000** (Deep North), plus ~±100 m of `WorldAngle` wobble.

So the band available to normal biome painting is y ∈ (−8000, +8000) inside a 10,000 m disc. The
two caps beyond it are circular segments of ~16 km² each — together about **10% of the map**.
Reclaiming them means patching both gap functions to return 1.0 *and* the `IsAshlands` /
`IsDeepnorth` statics and their four consumers (see the table further up). Bounded work, but a
second front; not worth it for a first version.

### If a bigger world is ever wanted

Enlarging the disc is far cheaper than it looks, and much cheaper than multiple stitched worlds.
Float precision at 40 km is ~0.004 m and zone IDs are ints (625 zones across a 40 km world) —
neither is a constraint. The clamps above are the whole mechanical cost.

The real costs are elsewhere:

- **Content density, not code.** Every location prefab carries min/max distance-from-centre
  bounds tuned for a 10 km world, and `ZoneSystem` caps placement at `maxRange = 10000f`. Double
  the diameter and every boss and unique location still clusters in the inner disc, leaving a
  large empty ring. Retuning those bands is the actual work.
- **Minimap** is scaled to the world; textures and the cache would need rescaling.
- **Pregeneration time scales with area.** A 40 km diameter is ~306,000 zones; at the measured
  715 zones/min that is ~7 hours, and the observed rate degraded over a run, so realistically
  worse.

Multiple worlds joined by a portal solve a *different* problem. Valheim binds one world per
server process, so such a portal is a disconnect and reconnect to another instance: a loading
screen, not continuous space. No shared ZDOs across the seam, nothing visible from the far side,
no sailing across. Character and inventory travel (character saves are separate from world
saves); nothing else does. Choose it only if separate realms are actually wanted.

---

## Map format and sampling

- **Format**: 8-bit indexed or plain RGB PNG, one colour per `Heightmap.Biome` value, decoded once
  at load into a flat `byte[]`. Nearest-neighbour sampling — biomes are categorical, don't
  interpolate them.
- **Resolution**: the playable disc is 10,000 m radius (hard edge at 10,500). Biome boundaries are
  soft in effect — `HeightmapBuilder` corner-blends heights across a zone and `GetBiomeArea`
  samples on a 64 m cross — so biome resolution can be far coarser than terrain resolution.
  2048² over 21,000 m is ~10 m/px and is plenty; 4096² if you want fine coastline detail.
- **Must answer for arbitrary `(wx, wy)`**, not on a fixed grid: distant LOD (`TerrainLod.CreateMesh`)
  builds heightmaps at its own width/scale, and `Minimap` samples at its own resolution. An image
  lookup handles this; a per-zone lookup table would not.
- Zones are 64 m (`m_width × m_scale = 64`; the shipped prefab is width 32, so 2 m vertex spacing).
  The pregenerator already reads those off the zone prefab at runtime rather than assuming —
  do the same here.

---

## Constraints to design around

**Threading.** `HeightmapBuilder.Build` runs on its own worker thread. The sampler must be
thread-safe and allocation-light, and must not touch the Unity API — decode to a plain `byte[]` at
`WorldGenerator.Initialize` and treat it as immutable after that. No `Texture2D.GetPixel` on that
thread.

**Every client generates its own terrain.** On connect (`ZNet`, `RPC_PeerInfo`) a client receives
only name / seed / uid / `worldGenVersion` and calls `WorldGenerator.Initialize` itself. Biome and
terrain data are *never* transmitted. So the map file has to be installed byte-identical on the
server and on every client, or players get mismatched ground and collision. For the dedicated
server this is a distribution problem, not a generation problem — hash the map, advertise the hash,
and refuse the connection on mismatch rather than letting it fail silently.

**Terrain isn't saved, but placement is.** Verified: `ZoneSystem.SaveASync` writes only
generated-zone coordinates, global keys, and location instances (prefab name + position + placed
flag). `Heightmap.cs` contains **no reference to `ZPackage` or `ZDO` at all** — heightmap data is
never serialised and never networked. `Heightmap.Regenerate` rebuilds `m_heights` from
`HeightmapBuilder` on every load, keyed on centre/width/scale/worldGen. The only persisted terrain
deltas come through `TerrainComp` (player edits and location flattening), as ZDOs.

Two consequences, and the second one is easy to get backwards:

1. Changing the map on a live world re-shapes ground under existing buildings and leaves dungeons
   and vegetation where the old map put them. **The map has to be final before the
   `pregenerateworld` run** — same rule already documented for `MinDungeonRooms`.
2. **Pregeneration does not bake terrain, so it cannot substitute for installing the mod on
   clients.** Every client recomputes height and biome locally from the seed by calling
   `WorldGenerator` itself. A vanilla client on a modded server gets vanilla terrain geometry —
   and worse than merely different, because vegetation and locations were *placed* against the
   modded surface and are ZDOs that do sync, so an unmodded client sees trees and buildings
   floating above or sunk into ground that doesn't match them. What pregeneration actually locks
   in is content *placement*, which is server-authoritative; the surface underneath it is not.

**Minimap cache.** As documented in the README: `Minimap.GenerateWorldMap` renders straight from
`WorldGenerator` and caches to `<World>_mapTexCache` / `_heightTexCache` / `_forestMaskTexCache`,
keyed by world *name*, not seed. Clear those after any map change or you'll be looking at the old
continent.

**Location placement gets harder, not easier.** `ZoneSystem` places locations using `GetBiomeArea`
(9 `GetBiome` samples on a 64 m cross) and `GetTerrainDelta` (10 random `GetHeight` samples). Both
route through the patch, so they follow the map for free — but a hand-painted map has *more* biome
edges than noise does, and `GetBiomeArea` returns `Edge` at every one of them. Locations that
require `Median` will have fewer valid spots. Prefer large contiguous regions over intricate
borders, and watch the unique-location claim log the pregenerator already prints.

---

## Rough shape of the implementation

1. `BiomeMap` class — loads the PNG at `WorldGenerator.Initialize`, holds a `byte[]`, exposes a
   thread-safe `Heightmap.Biome Sample(float wx, float wy)`.
2. Harmony prefix on `GetBiome(float, float, float, bool)`:
   - Bail to vanilla if the config toggle is off or the map failed to load.
   - Preserve the vanilla guards in order: menu world, then `IsAshlands`, then `IsDeepnorth`, then
     the `waterAlwaysOcean` / `oceanLevel` ocean checks against base height.
   - Only then return the sampled biome.
3. Config: toggle + map path + expected hash. Log loudly on a load failure — silently falling back
   to vanilla generation and then pregenerating 76,000 zones would be an expensive mistake.
4. A `dumpbaseheight` console command for the authoring canvas (step 2 of the workflow above).

Deliberately *not* in a first version: painting AshLands/DeepNorth, the forest-factor layer, and
any height override.

---

## Open questions

- Does `GetBiome`'s ocean check need to run before or after the map sample? Before keeps coastlines
  coherent and is what's assumed above, but it means the map can't paint a lake into high ground.
- Should the map be authored in world space (origin at the centre, +Y north) or image space? Pick
  one and put it in a comment at the top of the loader — it will otherwise be re-derived wrongly
  every time.
- Worth a `worldGenVersion`-style stamp of our own in the save, so a world pregenerated against map
  v1 can refuse to load against map v2 instead of quietly regenerating half a continent.

---

## Appendix: why not a full external heightmap

Bypassing terrain generation entirely is *possible*, and not much more code — a second prefix on
`GetBiomeHeight` (keeping its `> 10,500` edge branch) and a no-op on `Pregenerate` (which
otherwise runs a 157×157 `GetBaseHeight` lake scan plus river placement that nothing would consult
any more). It was rejected because the costs land somewhere unhelpful:

- **Size.** Native fidelity is 2 m vertex spacing over a 20 km square — ~100 M samples, 200 MB at
  16-bit. Shipping something coarser to every client and interpolating means the fine terrain
  detail has to be reintroduced procedurally anyway.
- **Precision and desync.** Every client reconstructs terrain from the file independently.
  Interpolated heights make exact agreement a floating-point problem, where the biome-only version
  keeps heights a deterministic function of the seed and an integer biome id.
- **It buys less than it looks like.** Because biome already selects the height function, the
  biome map alone gives real control over the shape of the ground — mountains, marshes, plains
  flattening — for one patch instead of five.

Worth revisiting only if authored *coastlines* turn out to matter more than authored biome layout.
