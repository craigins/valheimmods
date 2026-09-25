using System;
using Object = UnityEngine.Object;
using System.Collections.Generic;
using UnityEngine;

namespace CraiginsValheimMod.Dungeons
{
    /// <summary>
    /// The authoritative "wipe this dungeon and rebuild it" operation, plus the checks that
    /// guard it. Server-side only - the world's ZDOs live there and DungeonGenerator.Save()
    /// writes to the generator's ZDO. The generator itself usually does NOT exist as an object
    /// on a dedicated server (see FindLoaded), so Acquire() materialises one from the ZDO.
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
        /// The DungeonGenerators that exist as GameObjects on this machine. ZNetScene only
        /// instantiates objects around ZNet's reference position, which is the local player on a
        /// client or a host, and the world origin on a dedicated server (Game.Start sets it to
        /// Vector3.zero and nothing ever moves it). So on a dedicated server this finds dungeons
        /// near (0,0) and nothing else - a player standing at an entrance three kilometres out
        /// has that dungeon instantiated on THEIR machine, never on the server's. Use Acquire()
        /// for anything that has to work there; this is the cheap "is it already live here" check
        /// and the basis of 'resetdungeon list'.
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

        public static DungeonGenerator FindLoadedInZone(Vector2s zone)
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

        /// <summary>
        /// A generator to operate on, plus whether it has to be thrown away afterwards. Dispose
        /// it when done - a transient one is registered with ZNetScene and would otherwise sit
        /// there until the scene's next cleanup pass noticed it was outside the active area.
        /// </summary>
        public sealed class Handle : IDisposable
        {
            public DungeonGenerator Dungeon;

            /// <summary>
            /// Built on demand from the ZDO rather than found live. Its rooms must be generated
            /// in Ghost mode (see Execute), and it's destroyed on Dispose.
            /// </summary>
            public bool Transient;

            public void Dispose()
            {
                if (Transient && Dungeon != null)
                {
                    Discard(Dungeon);
                    Dungeon = null;
                }
            }
        }

        /// <summary>
        /// The generator for a zone's dungeon: the live instance if this machine has one, else a
        /// transient one materialised from the generator's ZDO, which the server always has.
        ///
        /// This is the same trick vanilla uses to generate the world on a dedicated server: the
        /// server never instantiates a zone's objects for real, it ghost-spawns them
        /// (ZoneSystem.CreateGhostZones, per connected peer), which creates the ZDOs and destroys
        /// the GameObjects in the same frame. ZNetScene.CreateObject with the existing ZDO gives
        /// us a DungeonGenerator whose Awake() loads the saved room list from that ZDO and whose
        /// Save() will write the new one back to it, so every client reloads the same dungeon.
        /// </summary>
        public static Handle Acquire(Vector2s zone, out string error)
        {
            error = null;

            DungeonGenerator live = FindLoadedInZone(zone);
            if (live != null)
            {
                return new Handle { Dungeon = live, Transient = false };
            }

            ZDO zdo = FindGeneratorZdo(zone);
            if (zdo == null)
            {
                error = $"no generated dungeon in zone ({zone.x}, {zone.y}).";
                return null;
            }

            GameObject go = ZNetScene.instance.CreateObject(zdo);
            DungeonGenerator dungeon = go != null ? go.GetComponent<DungeonGenerator>() : null;
            if (dungeon == null)
            {
                if (go != null)
                {
                    Discard(go.GetComponent<ZNetView>());
                }
                error = $"couldn't instantiate the dungeon generator in zone ({zone.x}, {zone.y}).";
                return null;
            }

            return new Handle { Dungeon = dungeon, Transient = true };
        }

        /// <summary>
        /// The generator's ZDO in this zone: the one persistent object up in the interior whose
        /// prefab carries a DungeonGenerator. Interiors sit straight above their entrance in XZ
        /// and sectors ignore Y, so the entrance's zone is the right sector to scan.
        /// </summary>
        private static ZDO FindGeneratorZdo(Vector2s zone)
        {
            var sector = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), sector);

            foreach (ZDO zdo in sector)
            {
                if (zdo == null || zdo.GetPosition().y <= DungeonInterior.InteriorFloor)
                {
                    continue;
                }
                GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (prefab != null && prefab.GetComponent<DungeonGenerator>() != null)
                {
                    return zdo;
                }
            }
            return null;
        }

        /// <summary>
        /// Undo CreateObject the way ZNetScene.RemoveObjects does: unhook the ZDO (which clears
        /// its Created flag so a client's scene can still instantiate it), drop the registration,
        /// destroy the GameObject. NOT ZNetScene.Destroy - that destroys the ZDO too when we own
        /// it, and Execute just took ownership of it to Save().
        /// </summary>
        private static void Discard(DungeonGenerator dungeon)
        {
            Discard(dungeon.GetComponent<ZNetView>());
        }

        private static void Discard(ZNetView nview)
        {
            if (nview == null)
            {
                return;
            }
            ZDO zdo = nview.GetZDO();
            if (zdo != null)
            {
                nview.ResetZDO();
                ZNetScene.instance.m_instances.Remove(zdo);
            }
            Object.Destroy(nview.gameObject);
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
            Vector2s zone = DungeonInterior.ZoneOf(dungeon);
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
        ///   2. Call Generate(seed, mode), which is public and already does the whole rebuild
        ///      internally - Clear, re-seed Random, GenerateRooms, Save back to the same ZDO.
        ///      Generation isn't reimplemented here, just re-invoked.
        ///
        /// The spawn mode follows the handle. A live generator gets Full, and the rooms it
        /// places stay as real objects here. A transient one gets Ghost, exactly as the server's
        /// own zone generation does: every networked object in the new rooms still gets its ZDO
        /// (ZNetView.Awake creates it before checking the ghost flag), the GameObjects are
        /// destroyed in the same frame, and the ZDOs flow to whichever client is near. Full mode
        /// on a transient would leave hundreds of instantiated objects sitting outside the
        /// server's active area until ZNetScene's next pass swept them.
        ///
        /// Known limit, in both modes: a generator created from a ZDO has m_originalPosition at
        /// its default (it's only set by SpawnLocation, on first generation), and
        /// m_useCustomInteriorTransform dungeons - frost caves, Mistlands, Hildir's - use it to
        /// place their bounds. Crypts don't.
        /// </summary>
        public static Result Execute(Handle handle, int seed, bool preservePlayerBuilt)
        {
            DungeonGenerator dungeon = handle.Dungeon;
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

            // The layout seed we pass is not the only seed in play. PlaceRoom seeds each room's
            // RandomSpawn/RandomObject picks - which platform, which pile of bones - from the
            // room's position plus, when m_addBaseSeedToRandomSpawn is set, GetSeed(). Every
            // client redraws those picks itself from its own GetSeed(), which on an instance
            // built from a ZDO is the position-derived value. GetSeed() on THIS instance must
            // return the same thing, and it won't once m_hasGeneratedSeed is set: Generate()
            // stores our layout seed in m_generatedSeed first, and a live generator that has
            // been asked for its seed before would hand that straight back. So make it derive
            // the seed afresh. A transient is fresh already; a live one may not be.
            dungeon.m_hasGeneratedSeed = false;
            DungeonGenerator.m_forceSeed = int.MinValue;

            if (handle.Transient)
            {
                ZNetView.StartGhostInit();
                try
                {
                    dungeon.Generate(seed, ZoneSystem.SpawnMode.Ghost);
                }
                finally
                {
                    ZNetView.FinishGhostInit();
                }
            }
            else
            {
                dungeon.Generate(seed, ZoneSystem.SpawnMode.Full);
                // Its shells are the new ones now; don't let the refresher redraw them.
                DungeonShellRefresh.MarkCurrent(dungeon);
            }

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
