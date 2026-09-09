using UnityEngine;

namespace CraiginsValheimMod.Instances
{
    /// <summary>
    /// What has to be true before an instance may be reaped. Only None is implemented; the rest
    /// are named because the reaper's shape depends on them existing, not because they work.
    /// See DungeonInstanceManager.EvaluateObjective and DESIGN_NOTES.md section 5.
    /// </summary>
    internal enum InstanceObjective
    {
        /// <summary>Satisfied immediately - a pure "go in, come out" instance.</summary>
        None = 0,

        /// <summary>A designated creature's ZDO no longer exists.</summary>
        BossDead,

        /// <summary>No ZDO in the zone resolves to a prefab with a Character.</summary>
        NoLiveCreatures,
    }

    /// <summary>
    /// What the server needs to open one instanced dungeon. Kept separate from the live record so
    /// the network layer has something small and obviously serializable to send.
    /// </summary>
    internal sealed class InstanceRequest
    {
        /// <summary>ZoneLocation name to spawn, e.g. "Crypt3". Required.</summary>
        public string LocationName;

        /// <summary>Layout seed. 0 means "pick one".</summary>
        public int Seed;

        /// <summary>Room theme override, or None to leave the location's own themes alone.</summary>
        public Room.Theme Themes;

        /// <summary>Room count overrides. 0 leaves the location's own values alone.</summary>
        public int MinRooms;
        public int MaxRooms;

        public InstanceRequest Clone()
        {
            return new InstanceRequest
            {
                LocationName = LocationName,
                Seed = Seed,
                Themes = Themes,
                MinRooms = MinRooms,
                MaxRooms = MaxRooms,
            };
        }
    }

    /// <summary>
    /// One live instanced dungeon, as the server sees it.
    ///
    /// This record is deliberately in-memory only. Instance contents are marked non-persistent
    /// (see DungeonInstanceManager.MarkEphemeral), so they are never written to the world save and
    /// cannot outlive the server process - which means a record that also dies with the process is
    /// consistent rather than lossy. Quest instances, when they arrive, get a persistent surface
    /// anchor ZDO instead; see DESIGN_NOTES.md section 6.
    /// </summary>
    internal sealed class DungeonInstance
    {
        public int Id;

        /// <summary>The zone this instance owns outright. Derived from Id, see InstanceRegion.</summary>
        public Vector2s Zone;

        /// <summary>Where the location was spawned. Its interior sits 5000 above this.</summary>
        public Vector3 Origin;

        /// <summary>Where a player entering should land - the interior side of the entrance.</summary>
        public Vector3 Arrival;

        public string LocationName;
        public int Seed;
        public Room.Theme Themes;
        public int RoomCount;

        /// <summary>Time.time when this was opened. Server uptime, not wall clock.</summary>
        public float OpenedAt;

        /// <summary>
        /// Set the first time a player is actually observed inside. Without it a freshly opened
        /// instance reads as empty and would be reaped during the player's teleport in. This is
        /// the one bit of an enter/exit refcount worth keeping - see DESIGN_NOTES.md section 4.
        /// </summary>
        public bool Armed;

        /// <summary>What has to happen before this instance may be reaped.</summary>
        public InstanceObjective Objective;

        /// <summary>
        /// Latched once the objective is satisfied: once true it stays true, so a respawning
        /// SpawnArea can't un-clear a dungeon the player has already finished with.
        /// </summary>
        public bool Cleared;

        /// <summary>Time.time when the instance was last seen occupied, or when it was opened.</summary>
        public float LastOccupiedAt;

        public string Describe()
        {
            return $"#{Id} '{LocationName}' zone=({Zone.x},{Zone.y}) seed={Seed} rooms={RoomCount}";
        }
    }
}
