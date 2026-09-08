using System.Collections.Generic;
using UnityEngine;

namespace CraiginsValheimMod.Dungeons
{
    /// <summary>
    /// The authoritative "wipe this dungeon and rebuild it" operation, plus the checks that
    /// guard it. Server-side only - the world's ZDOs live there and DungeonGenerator.Save()
    /// writes to the generator's ZDO.
    ///
    /// Shared by the two things that can ask for a reset: the 'resetdungeon' console command and
    /// the dungeon-entrance interaction (DungeonEntrancePatches). Both funnel through Validate()
    /// and Execute() so the safety rules can't drift apart between them.
    /// </summary>
    internal static class DungeonReset
    {
        /// <summary>
        /// What a reset did (or would do). There's no success flag: Validate() is what decides
        /// whether a reset may happen, and Execute() is only reached once it has passed.
        /// </summary>
        internal struct Result
        {
            public int Seed;
            public int OldRooms;
            public int NewRooms;
            public int Destroyed;
            public int Preserved;
        }

        /// <summary>
        /// A DungeonGenerator exists as a GameObject only while its zone is loaded - ZoneSystem
        /// loads zones around player reference positions and there's no supported way to ask it
        /// for an arbitrary one. So every caller is limited to dungeons somebody is standing near.
        /// </summary>
        public static List<DungeonGenerator> FindLoaded()
        {
            var found = new List<DungeonGenerator>();
            foreach (DungeonGenerator dungeon in Object.FindObjectsByType<DungeonGenerator>(FindObjectsSortMode.None))
            {
                if (dungeon != null && DungeonInterior.GetZdo(dungeon) != null)
                {
                    found.Add(dungeon);
                }
            }
            return found;
        }

        public static DungeonGenerator FindLoadedInZone(Vector2i zone)
        {
            foreach (DungeonGenerator dungeon in FindLoaded())
            {
                if (DungeonInterior.ZoneOf(dungeon) == zone)
                {
                    return dungeon;
                }
            }
            return null;
        }

        public static string LocationName(DungeonGenerator dungeon)
        {
            if (ZoneSystem.instance != null
                && ZoneSystem.instance.m_locationInstances.TryGetValue(DungeonInterior.ZoneOf(dungeon), out ZoneSystem.LocationInstance instance)
                && instance.m_location != null)
            {
                return LocationName(instance.m_location);
            }
            return null;
        }

        public static string LocationName(ZoneSystem.ZoneLocation location)
        {
            return string.IsNullOrEmpty(location.m_prefabName) ? location.m_prefab.Name : location.m_prefabName;
        }

        public static string Describe(DungeonGenerator dungeon)
        {
            Vector2i zone = DungeonInterior.ZoneOf(dungeon);
            return $"'{LocationName(dungeon) ?? dungeon.name}' in zone ({zone.x}, {zone.y})";
        }

        /// <summary>
        /// Everything that has to be true before a dungeon may be rebuilt. Returns a
        /// player-facing reason when it isn't.
        /// </summary>
        public static bool Validate(DungeonGenerator dungeon, bool force, out string error)
        {
            error = null;

            if (dungeon == null)
            {
                error = "that dungeon isn't loaded any more.";
                return false;
            }

            // CampGrid/CampRadial are surface camps (Fuling villages and the like). They don't go
            // through PlaceRooms, they sit in the terrain rather than in an interior 5000m up, and
            // none of the interior-sweep logic applies to them.
            if (dungeon.m_algorithm != DungeonGenerator.Algorithm.Dungeon)
            {
                error = $"{Describe(dungeon)} uses the {dungeon.m_algorithm} algorithm, not Dungeon - " +
                        "that's a surface camp, which sits in the terrain and can't be reset this way.";
                return false;
            }

            if (!force && AnyoneInside(dungeon, out string who))
            {
                error = $"{who} is inside {Describe(dungeon)}. They'd be left standing in a stale copy of the " +
                        "old rooms 5000m above the map.";
                return false;
            }

            if (!force && !DungeonProgression.IsUnlocked(dungeon, out string requirement))
            {
                error = $"{Describe(dungeon)} is sealed until {requirement} falls (Dungeons.ResetBossGate). " +
                        "Pass 'force' to rebuild it anyway.";
                return false;
            }

            return true;
        }

        public static bool AnyoneInside(DungeonGenerator dungeon, out string who)
        {
            if (Player.m_localPlayer != null && DungeonInterior.IsInside(dungeon, Player.m_localPlayer.transform.position))
            {
                who = "you";
                return true;
            }

            if (ZNet.instance != null)
            {
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                {
                    if (peer != null && DungeonInterior.IsInside(dungeon, peer.GetRefPos()))
                    {
                        who = string.IsNullOrEmpty(peer.m_playerName) ? "a connected player" : peer.m_playerName;
                        return true;
                    }
                }
            }

            who = null;
            return false;
        }

        /// <summary>
        /// What a reset would do, without doing it. Same collection pass as Execute so a dry run
        /// can't disagree with the real thing.
        /// </summary>
        public static Result Preview(DungeonGenerator dungeon, int seed, bool preservePlayerBuilt)
        {
            var doomed = new List<ZDO>();
            DungeonInterior.Collect(dungeon, preservePlayerBuilt, doomed, out int preserved);

            return new Result
            {
                Seed = seed,
                OldRooms = DungeonInterior.GetSavedRoomCount(dungeon),
                NewRooms = -1,
                Destroyed = doomed.Count,
                Preserved = preserved,
            };
        }

        /// <summary>
        /// Wipe and rebuild. Two steps, and the first is the one vanilla doesn't do for you:
        ///
        ///   1. Destroy the interior's contents. DungeonGenerator.Clear() only removes room
        ///      shells; the chests/spawners/doors are unparented ZDOs that would otherwise
        ///      survive into the new layout. See DungeonInterior.
        ///   2. Call Generate(seed, SpawnMode.Full), which is public and already does the whole
        ///      rebuild internally - Clear, re-seed Random, GenerateRooms, Save back to the same
        ///      ZDO. Generation isn't reimplemented here, just re-invoked. Ghost would build the
        ///      layout and then destroy every object in it.
        /// </summary>
        public static Result Execute(DungeonGenerator dungeon, int seed, bool preservePlayerBuilt)
        {
            var doomed = new List<ZDO>();
            DungeonInterior.Collect(dungeon, preservePlayerBuilt, doomed, out int preserved);

            int oldRooms = DungeonInterior.GetSavedRoomCount(dungeon);
            int destroyed = DungeonInterior.Destroy(doomed);

            // Save() writes the new room list to this ZDO, which needs owning like any other.
            ZDO zdo = DungeonInterior.GetZdo(dungeon);
            if (zdo != null && !zdo.IsOwner())
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
            }

            dungeon.Generate(seed, ZoneSystem.SpawnMode.Full);

            // Keep GetSeed() (and vanilla's 'printseeds') honest about what actually built this
            // dungeon, instead of reporting the position-derived seed it no longer used.
            dungeon.m_hasGeneratedSeed = true;

            return new Result
            {
                Seed = seed,
                OldRooms = oldRooms,
                NewRooms = DungeonInterior.GetSavedRoomCount(dungeon),
                Destroyed = destroyed,
                Preserved = preserved,
            };
        }

        /// <summary>
        /// Deliberately not UnityEngine.Random: generation is driven entirely by Random.InitState,
        /// and pulling a value from that same generator would be one more thing perturbing world
        /// determinism for no reason.
        /// </summary>
        public static int NewSeed()
        {
            return new System.Random().Next(int.MinValue, int.MaxValue);
        }
    }
}
