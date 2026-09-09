using System.Collections.Generic;
using CraiginsValheimMod.Dungeons;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Instances
{
    /// <summary>
    /// Server-side lifecycle for instanced dungeons: open one, poll it, reap it.
    ///
    /// Everything here runs on the server and only on the server. The world's ZDOs live there,
    /// DungeonGenerator.Save() writes to the generator's ZDO, and peer reference positions - which
    /// is how occupancy is determined - are only known there.
    ///
    /// OCCUPANCY IS DERIVED, NEVER COUNTED. The obvious design is an enter/exit refcount, and it
    /// leaks: every failure mode (disconnect inside, die and respawn at a bed, moved by anything
    /// that didn't go through the portal) is a LOST DECREMENT rather than a missing counter, and a
    /// refcount that leaks is worse than none because the instance is then pinned open by a number
    /// that looks authoritative. So nothing is stored - the poll asks ZNet.GetPeers() where
    /// everyone is. There is no decrement to lose. See DESIGN_NOTES.md section 4.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class DungeonInstanceManager
    {
        /// <summary>How often the reaper looks at the world. Cheap - it walks a peer list.</summary>
        private const float PollInterval = 5f;

        /// <summary>
        /// How long an instance must read as empty before it's reaped, once armed. Covers a player
        /// mid-teleport or a peer whose reference position hasn't updated yet.
        /// </summary>
        private const float EmptyGraceSeconds = 20f;

        /// <summary>
        /// How long an instance nobody ever entered is kept. Without this, an open that fails on
        /// the client side (a refused teleport, a disconnect during the loading screen) would hold
        /// a zone until the server restarts.
        /// </summary>
        private const float UnvisitedTimeoutSeconds = 300f;

        private static readonly Dictionary<int, DungeonInstance> _instances = new Dictionary<int, DungeonInstance>();
        private static readonly List<GameObject> _spawnScratch = new List<GameObject>();
        private static int _nextId = 1;
        private static float _nextPollAt;

        public static bool IsServer
        {
            get { return ZNet.instance != null && ZNet.instance.IsServer(); }
        }

        public static IEnumerable<DungeonInstance> All
        {
            get { return _instances.Values; }
        }

        public static int Count
        {
            get { return _instances.Count; }
        }

        public static DungeonInstance Get(int id)
        {
            DungeonInstance instance;
            return _instances.TryGetValue(id, out instance) ? instance : null;
        }

        public static DungeonInstance ForZone(Vector2s zone)
        {
            foreach (DungeonInstance instance in _instances.Values)
            {
                if (instance.Zone == zone)
                {
                    return instance;
                }
            }
            return null;
        }

        public static DungeonInstance ForPosition(Vector3 point)
        {
            return ForZone(ZoneSystem.GetZone(point));
        }

        // ---- opening ----------------------------------------------------------------------

        /// <summary>
        /// Spawns and generates a new instance. Returns null with a player-facing reason on
        /// failure; nothing is left behind in that case.
        /// </summary>
        public static DungeonInstance Open(InstanceRequest request, out string error)
        {
            error = null;

            if (!IsServer)
            {
                error = "instances can only be opened on the server.";
                return null;
            }
            if (ZoneSystem.instance == null || ZNetScene.instance == null || ZDOMan.instance == null)
            {
                error = "the world isn't loaded yet.";
                return null;
            }
            if (request == null || string.IsNullOrEmpty(request.LocationName))
            {
                error = "no location given.";
                return null;
            }

            ZoneSystem.ZoneLocation location = FindLocation(request.LocationName);
            if (location == null)
            {
                error = $"no location named '{request.LocationName}'. Try 'dungeoninstance locations'.";
                return null;
            }

            location.m_prefab.Load();
            try
            {
                Location prefabLocation = location.m_prefab.Asset != null
                    ? location.m_prefab.Asset.GetComponent<Location>()
                    : null;

                if (prefabLocation == null || !prefabLocation.m_hasInterior || prefabLocation.m_generator == null)
                {
                    error = $"'{request.LocationName}' has no dungeon interior - there'd be nowhere to teleport to.";
                    return null;
                }
                if (prefabLocation.m_generator.m_algorithm != DungeonGenerator.Algorithm.Dungeon)
                {
                    error = $"'{request.LocationName}' uses the {prefabLocation.m_generator.m_algorithm} " +
                            "algorithm - that's a surface camp, which needs terrain under it.";
                    return null;
                }

                int id = _nextId++;
                Vector2s zone = InstanceRegion.ZoneFor(id, z => ForZone(z) != null);
                Vector3 origin = InstanceRegion.OriginFor(zone);
                int seed = request.Seed != 0 ? request.Seed : NewSeed();

                Spawn(location, prefabLocation.m_generator, request, origin, seed);

                DungeonGenerator generator = DungeonReset.FindLoadedInZone(zone);

                var instance = new DungeonInstance
                {
                    Id = id,
                    Zone = zone,
                    Origin = origin,
                    Arrival = ArrivalFor(generator, origin),
                    LocationName = DungeonReset.LocationName(location),
                    Seed = seed,
                    Themes = request.Themes,
                    RoomCount = generator != null ? DungeonInterior.GetSavedRoomCount(generator) : -1,
                    Objective = InstanceObjective.None,
                    OpenedAt = Time.time,
                    LastOccupiedAt = Time.time,
                };
                instance.Cleared = EvaluateObjective(instance);

                int marked = MarkEphemeral(zone);
                _instances[id] = instance;

                Jotunn.Logger.LogInfo(
                    $"Opened dungeon instance {instance.Describe()} at {origin} - " +
                    $"arrival {instance.Arrival}, {marked} object(s) marked non-persistent.");

                return instance;
            }
            finally
            {
                location.m_prefab.Release();
            }
        }

        /// <summary>
        /// The actual spawn. Two things here are worth knowing:
        ///
        /// m_forceSeed is how the layout seed is chosen. SpawnLocation calls the no-argument
        /// DungeonGenerator.Generate(mode), which resolves its seed through GetSeed() - and
        /// GetSeed() consumes the public static m_forceSeed if it's set, then resets it. That is
        /// exactly how vanilla's own 'dungeonseed' console command works (Terminal.cs:1200). The
        /// seed passed to SpawnLocation itself only drives RandomSpawn selection.
        ///
        /// The theme/room overrides are written onto the SHARED prefab asset and restored
        /// afterwards, because SpawnLocation gives us no window between instantiating the
        /// generator and calling Generate on it. That's the same save-mutate-restore pattern
        /// SpawnLocation itself uses on the asset's transform and m_interiorTransform, so it is at
        /// least an idiom the game already relies on - but it is why this must not run concurrently
        /// with ordinary zone generation, and why the restore is in a finally.
        ///
        /// Note SpawnLocation performs NO terrain modification - m_clearArea is applied by
        /// PlaceLocations (ZoneSystem.cs:1791), not here - which is what makes spawning a location
        /// 20km above the sea floor safe.
        /// </summary>
        private static void Spawn(ZoneSystem.ZoneLocation location, DungeonGenerator generator,
            InstanceRequest request, Vector3 origin, int seed)
        {
            Room.Theme savedThemes = generator.m_themes;
            int savedMin = generator.m_minRooms;
            int savedMax = generator.m_maxRooms;

            try
            {
                if (request.Themes != Room.Theme.None)
                {
                    generator.m_themes = request.Themes;
                }
                if (request.MinRooms > 0)
                {
                    generator.m_minRooms = request.MinRooms;
                }
                if (request.MaxRooms > 0)
                {
                    generator.m_maxRooms = request.MaxRooms;
                }

                DungeonGenerator.m_forceSeed = seed;
                _spawnScratch.Clear();
                ZoneSystem.instance.SpawnLocation(
                    location, seed, origin, Quaternion.identity, ZoneSystem.SpawnMode.Full, _spawnScratch);
            }
            finally
            {
                generator.m_themes = savedThemes;
                generator.m_minRooms = savedMin;
                generator.m_maxRooms = savedMax;

                // GetSeed() clears this itself once consumed; resetting covers the path where
                // generation never happened and would otherwise poison the next natural dungeon.
                DungeonGenerator.m_forceSeed = int.MinValue;
                _spawnScratch.Clear();
            }
        }

        // ---- closing ----------------------------------------------------------------------

        /// <summary>
        /// Destroys an instance and everything in it. The zone belongs to this instance alone, so
        /// this sweeps the whole sector above the interior floor rather than doing the careful
        /// height-band work DungeonInterior has to do inside a real world zone.
        /// </summary>
        public static bool Close(int id, string reason)
        {
            DungeonInstance instance = Get(id);
            if (instance == null)
            {
                return false;
            }

            var doomed = new List<ZDO>();
            var sector = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(instance.Zone, new SimulationDistance(0, 0), sector);
            foreach (ZDO zdo in sector)
            {
                if (zdo != null && zdo.GetPosition().y > InstanceRegion.InteriorFloor)
                {
                    doomed.Add(zdo);
                }
            }

            int destroyed = DungeonInterior.Destroy(doomed);
            _instances.Remove(id);

            Jotunn.Logger.LogInfo(
                $"Closed dungeon instance {instance.Describe()} ({reason}) - destroyed {destroyed} object(s).");
            return true;
        }

        public static int CloseAll(string reason)
        {
            var ids = new List<int>(_instances.Keys);
            int closed = 0;
            foreach (int id in ids)
            {
                if (Close(id, reason))
                {
                    closed++;
                }
            }
            return closed;
        }

        // ---- the reaper -------------------------------------------------------------------

        /// <summary>
        /// Driven from Game.Update rather than a coroutine so it keeps running on a dedicated
        /// server with no players connected, and stops cleanly when the game object goes away.
        /// </summary>
        [HarmonyPatch(typeof(Game), "Update")]
        private static class Game_Update_Patch
        {
            private static void Postfix()
            {
                Tick();
            }
        }

        public static void Tick()
        {
            if (!IsServer || _instances.Count == 0)
            {
                return;
            }
            if (Time.time < _nextPollAt)
            {
                return;
            }
            _nextPollAt = Time.time + PollInterval;

            List<int> reap = null;

            foreach (DungeonInstance instance in _instances.Values)
            {
                string who;
                if (IsOccupied(instance, out who))
                {
                    instance.Armed = true;
                    instance.LastOccupiedAt = Time.time;
                    continue;
                }

                if (!instance.Armed)
                {
                    if (Time.time - instance.OpenedAt > UnvisitedTimeoutSeconds)
                    {
                        (reap ?? (reap = new List<int>())).Add(instance.Id);
                    }
                    continue;
                }

                // Armed and empty. Cleared is latched - once satisfied it stays satisfied - which
                // is what keeps a respawning SpawnArea from un-clearing a dungeon the player has
                // already finished with.
                if (!instance.Cleared && EvaluateObjective(instance))
                {
                    instance.Cleared = true;
                }

                if (instance.Cleared && Time.time - instance.LastOccupiedAt > EmptyGraceSeconds)
                {
                    (reap ?? (reap = new List<int>())).Add(instance.Id);
                }
            }

            if (reap == null)
            {
                return;
            }
            foreach (int id in reap)
            {
                DungeonInstance instance = Get(id);
                Close(id, instance != null && !instance.Armed ? "never visited" : "empty");
            }
        }

        /// <summary>
        /// Is anybody standing in this instance? Checks the host's own player as well as connected
        /// peers, because on a listen server the host isn't in GetPeers().
        /// </summary>
        public static bool IsOccupied(DungeonInstance instance, out string who)
        {
            if (Player.m_localPlayer != null && Contains(instance, Player.m_localPlayer.transform.position))
            {
                who = "the host";
                return true;
            }

            if (ZNet.instance != null)
            {
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                {
                    if (peer != null && Contains(instance, peer.GetRefPos()))
                    {
                        who = string.IsNullOrEmpty(peer.m_playerName) ? "a connected player" : peer.m_playerName;
                        return true;
                    }
                }
            }

            who = null;
            return false;
        }

        private static bool Contains(DungeonInstance instance, Vector3 point)
        {
            return point.y > InstanceRegion.InteriorFloor && ZoneSystem.GetZone(point) == instance.Zone;
        }

        // ---- helpers ----------------------------------------------------------------------

        /// <summary>
        /// Looks a location up by prefab name. Deliberately not ZoneSystem.GetLocation(string),
        /// which throws a NullReferenceException when it walks past an enabled location with an
        /// invalid prefab - a state mods can easily produce and which has nothing to do with us.
        /// </summary>
        public static ZoneSystem.ZoneLocation FindLocation(string name)
        {
            if (ZoneSystem.instance == null)
            {
                return null;
            }

            foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
            {
                if (location == null)
                {
                    continue;
                }
                if (string.Equals(DungeonReset.LocationName(location), name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return location;
                }
            }
            return null;
        }

        /// <summary>
        /// Where a player entering should land: the doorway of the dungeon's first room.
        ///
        /// The generator's own transform is that doorway, exactly. PlaceStartRoom calls
        /// CalculateRoomPosRot(entrance, transform.position, transform.rotation, ...), which
        /// positions the start room so its entrance CONNECTION sits on the generator's position -
        /// so the generator marks the threshold a player walking in would step through, at floor
        /// level, in every dungeon regardless of theme.
        ///
        /// Deliberately not found by looking for the location's Teleport pair. In SpawnMode.Full
        /// the server only instantiates the location's ZNetView children (ZoneSystem.SpawnLocation)
        /// - the non-networked geometry, which is where the Teleports live, is never built on a
        /// dedicated server. It only appears on clients, via the LocationProxy and SpawnMode.Client.
        /// The generator has a ZNetView and is therefore always present here.
        /// </summary>
        private static Vector3 ArrivalFor(DungeonGenerator generator, Vector3 origin)
        {
            if (generator != null)
            {
                // A nudge up so we land on the floor rather than in it.
                return generator.transform.position + Vector3.up * 0.5f;
            }

            Jotunn.Logger.LogError(
                "No DungeonGenerator found after spawning an instance - entry point is a guess. " +
                "The location probably didn't generate; check the log above for generation errors.");
            return origin + Vector3.up * 5000f;
        }

        /// <summary>
        /// Whether the instance's objective is satisfied. Only <see cref="InstanceObjective.None"/>
        /// exists so far; the others are named so the reaper's shape is settled, and each is one
        /// predicate over the zone's ZDOs when it's time to write them.
        ///
        /// The reason those predicates will be cheap: a ZDO can be classified without instantiating
        /// anything. ZDO.GetPrefab() gives a hash, ZNetScene.GetPrefab(hash) gives the prefab, and
        /// GetComponent&lt;Character&gt;() / IsBoss() follow from there - so the reaper never needs
        /// the zone loaded. And Character.OnDeath ends in ZNetScene.Destroy, so a boss's ZDO simply
        /// ceasing to exist IS the death signal.
        /// </summary>
        private static bool EvaluateObjective(DungeonInstance instance)
        {
            switch (instance.Objective)
            {
                case InstanceObjective.None:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Marks everything in the instance non-persistent, which is what makes an instance
        /// ephemeral by construction rather than by remembering to clean up:
        ///
        ///   - ZDOMan.GetSaveClone() copies only Persistent ZDOs, so the instance is never written
        ///     to the world save and cannot outlive the server process.
        ///   - ZDOMan.RemoveOrphanNonPersistentZDOS() runs on every peer disconnect and destroys
        ///     non-persistent ZDOs whose owner isn't a connected peer. The server's own session id
        ///     counts as connected, so server-owned contents are untouched - but anything a
        ///     departing player owned is swept on the spot, which is the "logged out inside the
        ///     dungeon" case handled for free.
        ///
        /// The flag is only set locally and isn't pushed as a data revision. That's sufficient
        /// because both behaviours above are server-side, and the server is what saves.
        /// </summary>
        private static int MarkEphemeral(Vector2s zone)
        {
            var sector = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), sector);

            int marked = 0;
            foreach (ZDO zdo in sector)
            {
                if (zdo != null && zdo.GetPosition().y > InstanceRegion.InteriorFloor)
                {
                    zdo.Persistent = false;
                    marked++;
                }
            }
            return marked;
        }

        /// <summary>
        /// Deliberately not UnityEngine.Random: generation is driven entirely by Random.InitState,
        /// and drawing from that same generator would perturb world determinism for no reason.
        /// </summary>
        public static int NewSeed()
        {
            int seed = new System.Random().Next(int.MinValue, int.MaxValue);
            return seed != 0 ? seed : 1;
        }
    }
}
