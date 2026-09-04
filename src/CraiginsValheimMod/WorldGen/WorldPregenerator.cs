using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace CraiginsValheimMod.WorldGen
{
    /// <summary>
    /// Force-generates every zone in the world instead of leaving them for lazy/on-demand
    /// generation as players explore. Useful before copying a world to a dedicated server -
    /// especially with a terrain patch active (like TerrainMaskPatches' Mistlands smoothing),
    /// since terrain height gets baked into each zone's save data at generation time and never
    /// recomputed afterward. Run it, THEN copy the world files - not the other way around.
    ///
    /// Uses ZoneSystem.SpawnZone(id, SpawnMode.Ghost, ...) - the exact same call vanilla itself
    /// already makes constantly in the background near every player (ZoneSystem.CreateGhostZones)
    /// to pregenerate zones just outside view before you reach them. Ghost mode generates a
    /// zone's terrain/vegetation/locations, marks it generated, then fully destroys everything
    /// it just spawned - so driving it across the whole map doesn't accumulate live objects,
    /// same as vanilla's own ambient generation doesn't.
    ///
    /// GENERATION ORDER MATTERS, and that's what most of this file is about.
    ///
    /// A ZoneLocation flagged m_unique (Haldor's merchant camp being the obvious one) gets many
    /// candidate instances seeded across the map at world-gen time. The first one to actually
    /// generate wins: ZoneSystem.PlaceLocations finishes with
    ///     if (loc.m_location.m_unique) RemoveUnplacedLocations(loc.m_location);
    /// and RemoveUnplacedLocations deletes every other unplaced instance of that same location
    /// from m_locationInstances. So whichever zone we generate first decides, permanently, where
    /// the single merchant in this world lives. Generating from a map corner would strand it at
    /// the far edge. (Boss altars and the like aren't affected - those are numerous and fixed by
    /// seed, not first-come-first-served.)
    ///
    /// Three things protect that ordering:
    ///
    /// 1. A claim pass runs first, generating the nearest-to-origin candidate zone for each
    ///    unique location before anything else (see BuildUniqueLocationClaims). That resolves
    ///    every once-per-world claim in the first seconds of the run, at the closest possible
    ///    spot, instead of leaving it to be won hours later somewhere out in the spiral.
    ///
    /// 2. The bulk pass then walks every remaining zone in a strict spiral outward from the
    ///    origin (nearest zone centre first, ties broken by angle). "Strict" is load-bearing:
    ///    SpawnZone returns false while the zone's terrain isn't built, so the obvious approach -
    ///    retry it later, do the next zone meanwhile - lets later zones overtake earlier ones and
    ///    quietly shreds the ordering. Instead the cursor parks on the head of the spiral until
    ///    that exact zone spawns, and we keep HeightmapBuilder's single background thread fed by
    ///    pre-requesting terrain for the next few zones *in spiral order* (PrefetchTerrain).
    ///    Same throughput, ordering intact.
    ///
    /// 3. GhostZoneSuppressionPatch stops vanilla's own ZoneSystem.CreateGhostZones from
    ///    generating zones out from under us while the run is in progress.
    ///
    /// This is a genuinely long operation - HeightmapBuilder only ever processes one zone's
    /// terrain on a single background thread (not something this mod can safely speed up), so a
    /// full default-size world (10000m radius, ~65-80k zones inside the circular boundary
    /// depending on your zone size) can easily take hours. It logs progress, saves periodically
    /// as it goes (so a crash/restart doesn't lose everything - already-generated zones are
    /// skipped on the next run), and does a final synchronous save at the end.
    /// </summary>
    internal static class WorldPregenerator
    {
        public static bool IsRunning { get; private set; }

        /// <summary>
        /// Vanilla's HeightmapBuilder.BuildThread trims its finished-terrain queue (m_ready) back
        /// to 16 entries, evicting the oldest. Prefetching further ahead than that would start
        /// throwing away builds we're about to ask for, so cap the window well under it.
        /// </summary>
        private const int MaxLookahead = 12;

        /// <summary>
        /// How long the cursor will sit on one unspawnable zone before setting it aside and
        /// moving on. Beyond terrain readiness, SpawnZone also refuses a zone whose pending
        /// location prefab hasn't finished loading (PokeCanSpawnLocation) - normally a moment,
        /// but strict ordering means a zone that never becomes spawnable would otherwise stall
        /// the entire run. Set-aside zones are retried in a second pass at the end.
        /// </summary>
        private static readonly TimeSpan BlockedZoneTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Consecutive set-asides that mean something is broken rather than slow.</summary>
        private const int MaxConsecutiveBlockedZones = 10;

        public static IEnumerator Run(int zonesPerTick, int saveEveryNZones, int terrainLookahead)
        {
            if (IsRunning)
            {
                Jotunn.Logger.LogWarning("pregenerateworld: already running, ignoring duplicate request.");
                yield break;
            }

            if (!ZNet.instance.IsServer())
            {
                Jotunn.Logger.LogWarning("pregenerateworld: only makes sense on the server that owns the world - skipping.");
                yield break;
            }

            IsRunning = true;
            try
            {
                yield return RunInternal(zonesPerTick, saveEveryNZones, terrainLookahead);
            }
            finally
            {
                IsRunning = false;
            }
        }

        private static IEnumerator RunInternal(int zonesPerTick, int saveEveryNZones, int terrainLookahead)
        {
            ZoneSystem zoneSystem = ZoneSystem.instance;

            if (!zoneSystem.LocationsGenerated)
            {
                Jotunn.Logger.LogWarning(
                    "pregenerateworld: the world's locations haven't finished being placed yet (ZoneSystem is " +
                    "still running GenerateLocationsTimeSliced). Wait for that to finish and run this again - " +
                    "starting now would mean claiming unique locations from an incomplete candidate list.");
                yield break;
            }

            WarnAboutConcurrentZoneGeneration(zoneSystem);

            var ctx = new PassContext
            {
                Zones = zoneSystem,
                ZonePrefabHeightmap = zoneSystem.m_zonePrefab != null
                    ? zoneSystem.m_zonePrefab.GetComponentInChildren<Heightmap>()
                    : null,
                ZonesPerTick = Mathf.Max(1, zonesPerTick),
                SaveEveryNZones = Mathf.Max(1, saveEveryNZones),
                Lookahead = Mathf.Clamp(terrainLookahead, 0, MaxLookahead),
                Clock = Stopwatch.StartNew(),
                NextLog = TimeSpan.FromSeconds(10),
            };

            if (ctx.ZonePrefabHeightmap == null)
            {
                Jotunn.Logger.LogWarning(
                    "pregenerateworld: couldn't find the Heightmap on ZoneSystem's zone prefab, so terrain " +
                    "prefetching is off. Generation still works and stays in order, just slower.");
            }

            var blocked = new List<Vector2i>();

            // Pass 1 - settle the once-per-world claims at the closest candidate to spawn.
            List<Vector2i> claims = BuildUniqueLocationClaims(zoneSystem);
            if (claims.Count > 0)
            {
                ctx.BeginPass("unique locations", claims.Count);
                yield return GeneratePass(ctx, claims, blocked);
            }

            // Pass 2 - everything else, spiralling outward. Built after the claim pass so the
            // zones it just generated drop out via IsZoneGenerated.
            if (!ctx.Aborted)
            {
                List<Vector2i> ordered = BuildSpiralOrder(zoneSystem, WorldGenerator.worldSize, out int alreadyGenerated);
                ctx.BeginPass("spiral", ordered.Count);
                Jotunn.Logger.LogInfo(
                    $"pregenerateworld: {ordered.Count} zones to generate ({alreadyGenerated} already done), " +
                    "spiralling outward from the world origin. This can take a long time - progress logs " +
                    "every ~10s, and progress is saved periodically so a restart won't lose it.");

                if (ordered.Count > 0)
                {
                    yield return GeneratePass(ctx, ordered, blocked);
                }
            }

            // Pass 3 - anything that was never spawnable when we reached it in order.
            if (!ctx.Aborted && blocked.Count > 0)
            {
                var retry = blocked;
                blocked = new List<Vector2i>();
                ctx.BeginPass("retry", retry.Count);
                Jotunn.Logger.LogInfo($"pregenerateworld: retrying {retry.Count} zone(s) that were set aside earlier.");
                yield return GeneratePass(ctx, retry, blocked);
            }

            foreach (Vector2i id in blocked)
            {
                Jotunn.Logger.LogError($"pregenerateworld: zone ({id.x}, {id.y}) never became spawnable and was left ungenerated.");
            }

            Jotunn.Logger.LogInfo(
                $"pregenerateworld: zone generation {(ctx.Aborted ? "ABORTED" : "done")} " +
                $"({ctx.TotalGenerated} zones) in {ctx.Clock.Elapsed:hh\\:mm\\:ss}. Doing final save...");
            ZNet.instance.Save(sync: true, saveOtherPlayerProfiles: true, waitForNextFrame: false);
            Jotunn.Logger.LogInfo("pregenerateworld: final save complete. Safe to copy the world files now.");
        }

        /// <summary>
        /// Generates <paramref name="zones"/> strictly in the order given, parking on each zone
        /// until it actually spawns. Zones that stay unspawnable past BlockedZoneTimeout are
        /// appended to <paramref name="blockedOut"/> and skipped.
        /// </summary>
        private static IEnumerator GeneratePass(PassContext ctx, List<Vector2i> zones, List<Vector2i> blockedOut)
        {
            int cursor = 0;
            int consecutiveBlocked = 0;
            var blockedSince = new Stopwatch();

            while (cursor < zones.Count)
            {
                int spawnedThisTick = 0;
                while (cursor < zones.Count && spawnedThisTick < ctx.ZonesPerTick)
                {
                    // Deliberately no "try the next one instead" fallback: the whole point is
                    // that zone N+1 never generates before zone N.
                    if (!ctx.Zones.SpawnZone(zones[cursor], ZoneSystem.SpawnMode.Ghost, out _))
                    {
                        break;
                    }

                    cursor++;
                    spawnedThisTick++;
                    ctx.PassGenerated++;
                    ctx.TotalGenerated++;
                    ctx.SinceLastSave++;
                }

                if (spawnedThisTick > 0)
                {
                    blockedSince.Reset();
                    consecutiveBlocked = 0;
                }
                else if (!blockedSince.IsRunning)
                {
                    blockedSince.Restart();
                }
                else if (blockedSince.Elapsed >= BlockedZoneTimeout)
                {
                    Vector2i stuck = zones[cursor];
                    blockedOut.Add(stuck);
                    cursor++;
                    blockedSince.Reset();
                    consecutiveBlocked++;

                    Jotunn.Logger.LogWarning(
                        $"pregenerateworld: zone ({stuck.x}, {stuck.y}) still wasn't spawnable after " +
                        $"{BlockedZoneTimeout.TotalSeconds:0}s - setting it aside so one zone can't stall the " +
                        "whole run, and retrying it at the end.");

                    if (consecutiveBlocked >= MaxConsecutiveBlockedZones)
                    {
                        ctx.Aborted = true;
                        Jotunn.Logger.LogError(
                            $"pregenerateworld: {consecutiveBlocked} zones in a row failed to spawn - the terrain " +
                            "builder looks stuck rather than slow. Aborting and saving what's done so far.");
                        yield break;
                    }
                }

                // Keep HeightmapBuilder's single background thread busy on the zones we're about
                // to ask for, in the order we'll ask for them. IsTerrainReady queues the build as
                // a side effect when the zone isn't already built or pending, which is exactly the
                // request SpawnZone itself makes - so this only ever pulls work forward, never
                // duplicates it.
                PrefetchTerrain(ctx, zones, cursor, ctx.Lookahead);

                if (ctx.SinceLastSave >= ctx.SaveEveryNZones && !ZNet.instance.IsSaving())
                {
                    ctx.SinceLastSave = 0;
                    Jotunn.Logger.LogInfo($"pregenerateworld: checkpoint save at {ctx.TotalGenerated} zones.");
                    ZNet.instance.Save(sync: false, saveOtherPlayerProfiles: true, waitForNextFrame: false);
                }

                if (ctx.Clock.Elapsed >= ctx.NextLog)
                {
                    ctx.NextLog += TimeSpan.FromSeconds(10);
                    Vector2i at = zones[Mathf.Min(cursor, zones.Count - 1)];
                    Jotunn.Logger.LogInfo(
                        $"pregenerateworld [{ctx.Label}]: {ctx.PassGenerated}/{ctx.PassTotal} generated, " +
                        $"currently {ZoneSystem.GetZonePos(at).magnitude:0}m from the origin, " +
                        $"elapsed {ctx.Clock.Elapsed:hh\\:mm\\:ss}");
                }

                yield return null;
            }
        }

        private static void PrefetchTerrain(PassContext ctx, List<Vector2i> zones, int from, int count)
        {
            if (count <= 0 || ctx.ZonePrefabHeightmap == null)
            {
                return;
            }

            HeightmapBuilder builder = HeightmapBuilder.instance;
            WorldGenerator world = WorldGenerator.instance;
            if (builder == null || world == null)
            {
                return;
            }

            int end = Mathf.Min(zones.Count, from + count);
            for (int i = from; i < end; i++)
            {
                builder.IsTerrainReady(
                    ZoneSystem.GetZonePos(zones[i]),
                    ctx.ZonePrefabHeightmap.m_width,
                    ctx.ZonePrefabHeightmap.m_scale,
                    ctx.ZonePrefabHeightmap.IsDistantLod,
                    world);
            }
        }

        /// <summary>
        /// One zone per not-yet-claimed unique location - the candidate closest to the origin -
        /// ordered nearest-first.
        ///
        /// Only the nearest candidate per location is needed: generating it makes PlaceLocations
        /// spawn the location and call RemoveUnplacedLocations, which deletes every other
        /// candidate for that same location. The rest of those zones then become ordinary zones
        /// and get picked up by the spiral pass in their proper order. Different unique locations
        /// never compete with each other (RemoveUnplacedLocations is per-ZoneLocation), so the
        /// order among these few zones doesn't actually matter - it's sorted anyway for tidiness.
        /// </summary>
        private static List<Vector2i> BuildUniqueLocationClaims(ZoneSystem zoneSystem)
        {
            var nearest = new Dictionary<ZoneSystem.ZoneLocation, SpiralEntry>();

            foreach (KeyValuePair<Vector2i, ZoneSystem.LocationInstance> pair in zoneSystem.m_locationInstances)
            {
                ZoneSystem.LocationInstance instance = pair.Value;
                if (instance.m_placed || instance.m_location == null || !instance.m_location.m_unique)
                {
                    continue;
                }
                // An already-generated zone won't run PlaceLocations again, so it can't claim
                // anything - the claim has to go to the nearest candidate we can still generate.
                if (zoneSystem.IsZoneGenerated(pair.Key))
                {
                    continue;
                }

                var entry = MakeEntry(pair.Key);
                if (!nearest.TryGetValue(instance.m_location, out SpiralEntry best) || entry.Dist2 < best.Dist2)
                {
                    nearest[instance.m_location] = entry;
                }
            }

            // Sorted before logging as well as before generating: a Dictionary enumerates in
            // whatever order it likes, and a claim list that reads out of order looks like the
            // ordering is broken even when it isn't.
            var claims = new List<KeyValuePair<string, SpiralEntry>>(nearest.Count);
            foreach (KeyValuePair<ZoneSystem.ZoneLocation, SpiralEntry> pair in nearest)
            {
                claims.Add(new KeyValuePair<string, SpiralEntry>(pair.Key.m_prefabName, pair.Value));
            }
            claims.Sort((a, b) => SpiralComparison(a.Value, b.Value));

            if (claims.Count > 0)
            {
                Jotunn.Logger.LogInfo(
                    $"pregenerateworld: claiming {claims.Count} unique location(s) at their nearest-to-origin " +
                    "candidate zone, before the bulk pass, in this order:");
            }

            var ordered = new List<Vector2i>(claims.Count);
            foreach (KeyValuePair<string, SpiralEntry> claim in claims)
            {
                Jotunn.Logger.LogInfo(
                    $"pregenerateworld:   '{claim.Key}' at zone ({claim.Value.Id.x}, {claim.Value.Id.y}), " +
                    $"{ZoneSystem.GetZonePos(claim.Value.Id).magnitude:0}m from the origin.");
                ordered.Add(claim.Value.Id);
            }
            return ordered;
        }

        /// <summary>
        /// Every not-yet-generated zone inside the world radius, ordered nearest-first by zone
        /// centre distance from the origin, ties broken by angle - i.e. a spiral outward from
        /// the middle of the map.
        /// </summary>
        private static List<Vector2i> BuildSpiralOrder(ZoneSystem zoneSystem, float radius, out int alreadyGenerated)
        {
            int range = Mathf.CeilToInt(radius / zoneSystem.m_zoneSize);
            alreadyGenerated = 0;

            var pending = new List<SpiralEntry>();
            for (int y = -range; y <= range; y++)
            {
                for (int x = -range; x <= range; x++)
                {
                    var id = new Vector2i(x, y);
                    if (ZoneSystem.GetZonePos(id).magnitude >= radius)
                    {
                        continue;
                    }
                    if (zoneSystem.IsZoneGenerated(id))
                    {
                        alreadyGenerated++;
                        continue;
                    }
                    pending.Add(MakeEntry(id));
                }
            }

            pending.Sort(SpiralComparison);

            var ordered = new List<Vector2i>(pending.Count);
            foreach (SpiralEntry entry in pending)
            {
                ordered.Add(entry.Id);
            }
            return ordered;
        }

        private static SpiralEntry MakeEntry(Vector2i id)
        {
            return new SpiralEntry
            {
                Id = id,
                // Zone ids are a uniform grid, so grid distance orders identically to world
                // distance - and being integer it groups exact ties into clean rings for the
                // angle tiebreak to sweep through.
                Dist2 = id.x * id.x + id.y * id.y,
                Angle = Mathf.Atan2(id.y, id.x),
            };
        }

        /// <summary>
        /// vanilla's ZoneSystem.Update keeps calling CreateLocalZones every frame around the
        /// local reference position and every connected peer, generating zones in raster order
        /// regardless of what we're doing. GhostZoneSuppressionPatch handles the speculative
        /// half of that, but CreateLocalZones has to keep working or players fall through
        /// unloaded ground - so the only real fix is not having anyone out in the world.
        /// </summary>
        private static void WarnAboutConcurrentZoneGeneration(ZoneSystem zoneSystem)
        {
            int peers = ZNet.instance.GetPeers().Count;
            Vector3 refPos = ZNet.instance.GetReferencePosition();
            float localArea = zoneSystem.m_zoneSize * (zoneSystem.m_activeArea + zoneSystem.m_activeDistantArea);

            if (peers == 0 && refPos.magnitude <= localArea)
            {
                return;
            }

            Jotunn.Logger.LogWarning(
                $"pregenerateworld: {peers} peer(s) connected and the local reference position is " +
                $"{refPos.magnitude:0}m from the origin. Vanilla keeps generating zones around players while " +
                "this runs, out of order, which can hand a unique location (the merchant, etc.) to a zone far " +
                "from spawn before the spiral reaches it. For a clean result, run this on a dedicated server " +
                "with nobody connected, or at least stand at spawn.");
        }

        private static readonly Comparison<SpiralEntry> SpiralComparison = (a, b) =>
            a.Dist2 != b.Dist2 ? a.Dist2.CompareTo(b.Dist2) : a.Angle.CompareTo(b.Angle);

        private struct SpiralEntry
        {
            public Vector2i Id;
            public int Dist2;
            public float Angle;
        }

        private sealed class PassContext
        {
            public ZoneSystem Zones;
            public Heightmap ZonePrefabHeightmap;
            public int ZonesPerTick;
            public int SaveEveryNZones;
            public int Lookahead;

            public string Label;
            public int PassTotal;
            public int PassGenerated;

            public int TotalGenerated;
            public int SinceLastSave;
            public bool Aborted;
            public Stopwatch Clock;
            public TimeSpan NextLog;

            public void BeginPass(string label, int total)
            {
                Label = label;
                PassTotal = total;
                PassGenerated = 0;
            }
        }
    }
}
