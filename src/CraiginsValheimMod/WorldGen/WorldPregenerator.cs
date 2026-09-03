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
    /// This is a genuinely long operation - the underlying HeightmapBuilder only ever processes
    /// one zone's terrain on a single background thread (not something this mod can safely
    /// speed up), so a full default-size world (10000m radius, ~65-80k zones inside the circular
    /// boundary depending on your zone size) can easily take hours. It logs progress, saves
    /// periodically as it goes (so a crash/restart doesn't lose everything - already-generated
    /// zones are skipped on the next run), and does a final synchronous save at the end.
    /// </summary>
    internal static class WorldPregenerator
    {
        public static bool IsRunning { get; private set; }

        public static IEnumerator Run(int zonesPerTick, int saveEveryNZones)
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
                yield return RunInternal(zonesPerTick, saveEveryNZones);
            }
            finally
            {
                IsRunning = false;
            }
        }

        private static IEnumerator RunInternal(int zonesPerTick, int saveEveryNZones)
        {
            ZoneSystem zoneSystem = ZoneSystem.instance;
            float zoneSize = zoneSystem.m_zoneSize;
            float radius = WorldGenerator.worldSize;
            int range = Mathf.CeilToInt(radius / zoneSize);

            var queue = new Queue<Vector2i>();
            int alreadyGenerated = 0;
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
                    queue.Enqueue(id);
                }
            }

            int toGenerate = queue.Count;
            Jotunn.Logger.LogInfo(
                $"pregenerateworld: {toGenerate} zones to generate ({alreadyGenerated} already done). " +
                "This can take a long time - progress logs every ~10s, and progress is saved periodically " +
                "so a restart won't lose it.");

            if (toGenerate == 0)
            {
                Jotunn.Logger.LogInfo("pregenerateworld: nothing to do, world is already fully generated.");
                yield break;
            }

            var stopwatch = Stopwatch.StartNew();
            var nextLog = TimeSpan.FromSeconds(10);
            int generated = 0;
            int sinceLastSave = 0;
            int consecutiveStalledPasses = 0;
            const int maxStalledPasses = 2000; // safety net against a genuine infinite hang

            while (queue.Count > 0)
            {
                int thisTick = Mathf.Min(zonesPerTick, queue.Count);
                int generatedThisTick = 0;
                for (int i = 0; i < thisTick; i++)
                {
                    Vector2i id = queue.Dequeue();
                    if (zoneSystem.SpawnZone(id, ZoneSystem.SpawnMode.Ghost, out _))
                    {
                        generated++;
                        generatedThisTick++;
                        sinceLastSave++;
                    }
                    else
                    {
                        // Terrain isn't ready yet (HeightmapBuilder's single background thread is
                        // still working on it, or hasn't gotten to it) - retry later.
                        queue.Enqueue(id);
                    }
                }

                consecutiveStalledPasses = generatedThisTick == 0 ? consecutiveStalledPasses + 1 : 0;
                if (consecutiveStalledPasses > maxStalledPasses)
                {
                    Jotunn.Logger.LogError(
                        $"pregenerateworld: made no progress for {maxStalledPasses} consecutive passes with " +
                        $"{queue.Count} zones still pending - aborting rather than hanging forever. " +
                        "Saving what's done so far.");
                    break;
                }

                if (sinceLastSave >= saveEveryNZones && !ZNet.instance.IsSaving())
                {
                    sinceLastSave = 0;
                    Jotunn.Logger.LogInfo($"pregenerateworld: checkpoint save at {generated}/{toGenerate} zones.");
                    ZNet.instance.Save(sync: false, saveOtherPlayerProfiles: true, waitForNextFrame: false);
                }

                if (stopwatch.Elapsed >= nextLog)
                {
                    nextLog += TimeSpan.FromSeconds(10);
                    Jotunn.Logger.LogInfo(
                        $"pregenerateworld: {generated}/{toGenerate} generated ({queue.Count} remaining), " +
                        $"elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}");
                }

                yield return null;
            }

            Jotunn.Logger.LogInfo(
                $"pregenerateworld: zone generation done ({generated}/{toGenerate}) in " +
                $"{stopwatch.Elapsed:hh\\:mm\\:ss}. Doing final save...");
            ZNet.instance.Save(sync: true, saveOtherPlayerProfiles: true, waitForNextFrame: false);
            Jotunn.Logger.LogInfo("pregenerateworld: final save complete. Safe to copy the world files now.");
        }
    }
}
