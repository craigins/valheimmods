using System.Collections.Generic;
using CraiginsValheimMod.Dungeons;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Enforces a minimum room count on generated dungeons, so you stop finding crypts that turn
    /// out to be two rooms and a dead end.
    ///
    /// Vanilla builds a dungeon in DungeonGenerator.PlaceRooms:
    ///     for (int i = 0; i &lt; m_maxRooms; i++) {
    ///         PlaceOneRoom(mode);
    ///         if (CheckRequiredRooms() &amp;&amp; m_placedRooms.Count >= m_minRooms) return;
    ///     }
    /// and PlaceOneRoom picks an open connection, tries up to 10 candidate rooms against it, and
    /// returns false if there's no open connection left or all 10 collided. Nothing retries or
    /// backtracks. So a tiny dungeon has one of two causes, and they need different fixes:
    ///
    ///   1. It stopped early because the per-prefab m_minRooms floor was already satisfied.
    ///      Raising that floor (and m_maxRooms, which is really the iteration budget) makes it
    ///      keep going. That's the Prefix below.
    ///
    ///   2. It painted itself into a corner - every connection closed off after a room or two,
    ///      so no amount of extra iterations can help. The only lever is a different layout,
    ///      which means a different seed. That's the Postfix below: re-run generation with
    ///      derived seeds and keep the roomiest result.
    ///
    /// The Postfix hangs off GenerateRooms rather than Generate because of where the state lives.
    /// Generate calls GenerateRooms, then Save() - which serialises the actual room list (id,
    /// position, rotation) into the ZDO - and only then clears the static m_placedRooms /
    /// m_openConnections / m_doorConnections lists. So GenerateRooms is the one window where the
    /// result is both readable and still changeable before it's persisted.
    ///
    /// Re-running it is *almost* what Generate itself does - Clear(), empty the static lists,
    /// re-seed Random, build again - with one addition Generate doesn't need. Clear() only
    /// destroys children of the generator's transform, i.e. the room shells; a room's actual
    /// contents are instantiated unparented and survive it. Generate never notices because it
    /// only ever runs on a dungeon that doesn't exist yet, but a reroll does, so each discarded
    /// attempt has to have its contents destroyed explicitly (DiscardAttemptContents below) or
    /// they accumulate in the interior underneath the layout that finally wins.
    ///
    /// Only Algorithm.Dungeon is touched. The CampGrid/CampRadial algorithms (Fuling villages and
    /// friends) don't go through PlaceRooms and don't use m_minRooms/m_maxRooms at all.
    ///
    /// NOT TESTED IN-GAME, and OFF by default for a reason worth understanding: a dungeon's
    /// layout is generated once, when its zone generates, and Save() bakes it into the world
    /// permanently. Turning this on later does nothing for dungeons that already exist, and
    /// turning it off later doesn't undo ones built with it. Whatever value is set when a zone
    /// generates is the value that world lives with - which matters most if you're about to run
    /// 'pregenerateworld', since that bakes every dungeon in the map in one go.
    /// </summary>
    internal static class DungeonPatches
    {
        /// <summary>
        /// Raise the per-prefab floor so generation doesn't stop early. m_maxRooms comes up with
        /// it because it's the loop's iteration count, not a cap on rooms - leaving it below the
        /// requested minimum would make that minimum unreachable no matter how many rerolls run.
        /// </summary>
        [HarmonyPatch(typeof(DungeonGenerator), nameof(DungeonGenerator.PlaceRooms))]
        private static class DungeonGeneratorPlaceRooms_Patch
        {
            private static void Prefix(DungeonGenerator __instance)
            {
                int min = Plugin.MinDungeonRooms.Value;
                if (min <= 0 || __instance.m_algorithm != DungeonGenerator.Algorithm.Dungeon)
                {
                    return;
                }

                if (__instance.m_minRooms < min)
                {
                    __instance.m_minRooms = min;
                }
                if (__instance.m_maxRooms < __instance.m_minRooms)
                {
                    __instance.m_maxRooms = __instance.m_minRooms;
                }
            }
        }

        /// <summary>
        /// If the finished layout is still short, reroll it. Runs before Generate's Save(), so
        /// whichever attempt wins is the one written to the world.
        /// </summary>
        [HarmonyPatch(typeof(DungeonGenerator), nameof(DungeonGenerator.GenerateRooms))]
        private static class DungeonGeneratorGenerateRooms_Patch
        {
            /// <summary>Regenerate() calls back into the patched method - don't recurse.</summary>
            private static bool _rerolling;

            private static void Postfix(DungeonGenerator __instance, ZoneSystem.SpawnMode mode)
            {
                int min = Plugin.MinDungeonRooms.Value;
                if (min <= 0 || _rerolling)
                {
                    return;
                }
                if (__instance.m_algorithm != DungeonGenerator.Algorithm.Dungeon)
                {
                    return;
                }

                int attempts = Mathf.Max(0, Plugin.MaxDungeonRerolls.Value);
                int target = Mathf.Min(min, __instance.m_maxRooms);
                int best = DungeonGenerator.m_placedRooms.Count;
                if (attempts == 0 || best >= target)
                {
                    return;
                }

                int baseSeed = __instance.m_generatedSeed;
                int bestSeed = baseSeed;
                bool holdingBest = true;

                _rerolling = true;
                try
                {
                    for (int i = 1; i <= attempts && best < target; i++)
                    {
                        // Deterministic derivation - the same world seed has to keep producing
                        // the same dungeons on every run and on every machine.
                        int seed = unchecked(baseSeed + i * (int)0x9E3779B9);
                        Regenerate(__instance, seed, mode);

                        int count = DungeonGenerator.m_placedRooms.Count;
                        holdingBest = count > best;
                        if (holdingBest)
                        {
                            best = count;
                            bestSeed = seed;
                        }
                    }

                    // The last attempt wasn't the winner, so rebuild the one that was.
                    if (!holdingBest)
                    {
                        Regenerate(__instance, bestSeed, mode);
                    }
                    __instance.m_generatedSeed = bestSeed;
                }
                finally
                {
                    _rerolling = false;
                }

                if (best < target)
                {
                    Jotunn.Logger.LogDebug(
                        $"MinDungeonRooms: best of {attempts + 1} layouts was {best} rooms, short of {target}. " +
                        "Some dungeon prefabs simply can't reach that many rooms.");
                }
            }

            /// <summary>
            /// Throw away the attempt built so far and build another one: destroy its contents
            /// and its rooms, empty the static bookkeeping lists, re-seed, generate again.
            /// </summary>
            private static void Regenerate(DungeonGenerator dungeon, int seed, ZoneSystem.SpawnMode mode)
            {
                DiscardAttemptContents(dungeon, mode);
                dungeon.Clear();
                DungeonGenerator.m_placedRooms.Clear();
                DungeonGenerator.m_openConnections.Clear();
                DungeonGenerator.m_doorConnections.Clear();
                Random.InitState(seed);
                dungeon.GenerateRooms(mode);
            }

            /// <summary>
            /// Destroys the networked contents of the attempt we're about to throw away.
            ///
            /// Clear() alone isn't enough, and this is the whole reason the method exists.
            /// Clear() destroys children of the generator's transform, which is only the room
            /// shells; PlaceRoom instantiates each room's ZNetView objects - chests, spawners,
            /// torches - UNPARENTED, and PlaceDoors does the same with doors. Without this,
            /// every discarded layout would leave its full contents floating in the interior and
            /// the winning layout would be built on top of all of them.
            ///
            /// Skipped in Ghost mode, where there is by construction nothing to clean up:
            /// ZoneSystem.SpawnLocation wraps the whole Generate call in StartGhostInit /
            /// FinishGhostInit so no ZDOs are created at all, and PlaceRoom destroys each clone
            /// on the spot. That's the mode 'pregenerateworld' uses, so the common path pays
            /// nothing for this.
            ///
            /// Player-built pieces are preserved, which only matters when this runs underneath
            /// 'resetdungeon' on a dungeon somebody has built in - that command promises to keep
            /// them, and a reroll must not quietly break the promise.
            ///
            /// The IsServer check is belt-and-braces: GenerateRooms is only ever reached in Full
            /// or Ghost mode, both of which are server-side (a client rebuilds a dungeon from
            /// saved room data in DungeonGenerator.Load, never by generating it). Destroying
            /// world ZDOs is a server operation regardless, so assert it rather than assume it.
            /// </summary>
            private static void DiscardAttemptContents(DungeonGenerator dungeon, ZoneSystem.SpawnMode mode)
            {
                if (mode == ZoneSystem.SpawnMode.Ghost || ZNetScene.instance == null || ZDOMan.instance == null)
                {
                    return;
                }
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                {
                    return;
                }

                DungeonInterior.Collect(dungeon, preservePlayerBuilt: true, _discarded, out int _);
                if (_discarded.Count > 0)
                {
                    DungeonInterior.Destroy(_discarded);
                }
                _discarded.Clear();
            }

            /// <summary>Reused across attempts - a reroll can run this up to MaxDungeonRerolls times per dungeon.</summary>
            private static readonly List<ZDO> _discarded = new List<ZDO>();
        }
    }
}
