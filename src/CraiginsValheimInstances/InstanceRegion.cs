using UnityEngine;

namespace CraiginsValheimMod.Instances
{
    /// <summary>
    /// The address space instanced dungeons live in.
    ///
    /// WHY A SEPARATE XZ REGION AND NOT JUST ALTITUDE. Valheim partitions the world by XZ and
    /// never by height: ZDO.SetSector derives a ZDO's sector from ZoneSystem.GetZone(pos), which
    /// reads x and z and throws y away, and so do ZNetScene.InActiveArea, OutsideActiveArea and
    /// ZDOMan.FindSectorObjects. Parking an instance 20km above a player's current zone would
    /// guarantee it can never overlap a real dungeon, but it would put the instance in that zone's
    /// sector - so every other player crossing that patch of ground would instantiate the whole
    /// thing, and reaping it would be surgery next to objects that matter.
    ///
    /// One instance per zone, in a region nobody walks through, means the instance shares a sector
    /// with nothing at all.
    ///
    /// WHY ALTITUDE AS WELL. Character.InInterior(pos) is a bare `pos.y > 3000f`, and that one
    /// boolean is what suppresses raids (RandEventSystem skips y > 3000), gives the interior
    /// environment, and switches off RenderGroup.Overworld so the ocean below isn't drawn under
    /// the floor. Above y = 10000 there's a bonus: ZoneSystem.GetGroundData raycasts from
    /// p + up*5000 for 10000m, so it misses everything and any SnapToGround in a room prefab
    /// becomes inert instead of yanking props down to the sea floor.
    ///
    /// WHY THE ZONE IS DERIVED AND NOT ALLOCATED. A free-list would have to be rebuilt at every
    /// server start by scanning for live instances. A derived mapping is stateless: the same id
    /// lands in the same zone forever, and the only question ever asked is "does a live instance
    /// already claim this zone".
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class InstanceRegion
    {
        /// <summary>
        /// Where instance locations are spawned. Their interiors end up 5000 higher again, because
        /// Location.Awake instantiates the interior at transform.position.y + 5000.
        /// </summary>
        public const float Altitude = 20000f;

        /// <summary>The game's own definition of "in an interior" (Character.InInterior).</summary>
        public const float InteriorFloor = 3000f;

        /// <summary>
        /// Bottom-left zone of the region.
        ///
        /// This USED to be zone 2000,2000 - roughly 128km out - on the reasoning that further away
        /// is safer. Valheim 1.0 made that wrong. Sectors are no longer addressed by their raw
        /// coordinates: ZoneSystem.SectorToIndex packs them into a single uint as
        /// `(y + 256) * 512 + (x + 256)`, and any zone outside +-256 fails the bounds check and
        /// collapses to index 0. Index 0 is not a harmless bucket - it is SectorZero, the sentinel
        /// ZDO.SetSector assigns to anything out of bounds (`OutsideZones = sectorIndex.Sector == 0`).
        /// At zone 2000 every instance would share one sector with every other instance AND with
        /// every genuinely out-of-bounds ZDO in the world, which is the exact opposite of the
        /// "an instance shares a sector with nothing" property this whole region exists to provide.
        /// FindSectorObjects would then hand the reaper other people's objects.
        ///
        /// So the region now lives at the top of the legal range instead. The playable world is
        /// 10,500m radius = zone +-164; zones 176-255 are past the world's edge with ~750m of
        /// margin and still inside the +-256 the sector index can represent.
        ///
        /// Keep RegionOrigin + RegionSize <= 256. That bound is the whole reason for these
        /// numbers, and nothing will fail loudly if it's exceeded - instances would just quietly
        /// start landing in SectorZero again.
        /// </summary>
        private const int RegionOriginX = 176;
        private const int RegionOriginY = 176;

        /// <summary>
        /// Region edge in zones. 80x80 is 6,400 slots against a handful of concurrent instances,
        /// so the collision probe in ZoneFor effectively never runs. Bounded by the sector index:
        /// RegionOrigin + RegionSize must not exceed 256.
        /// </summary>
        private const int RegionSize = 80;

        public static bool IsInstanceZone(Vector2s zone)
        {
            return zone.x >= RegionOriginX && zone.x < RegionOriginX + RegionSize
                && zone.y >= RegionOriginY && zone.y < RegionOriginY + RegionSize;
        }

        public static bool IsInstancePosition(Vector3 point)
        {
            return IsInstanceZone(ZoneSystem.GetZone(point));
        }

        /// <summary>
        /// The zone an instance id maps to. <paramref name="occupied"/> reports whether a zone is
        /// already claimed by a live instance; on a collision we walk to the next slot rather than
        /// keeping any allocation state of our own.
        /// </summary>
        public static Vector2s ZoneFor(int instanceId, System.Func<Vector2s, bool> occupied)
        {
            int slots = RegionSize * RegionSize;
            int start = SlotFor(instanceId);

            for (int i = 0; i < slots; i++)
            {
                int slot = (start + i) % slots;
                var zone = new Vector2s(RegionOriginX + slot % RegionSize, RegionOriginY + slot / RegionSize);
                if (occupied == null || !occupied(zone))
                {
                    return zone;
                }
            }

            // Every slot in the region taken. Not reachable in any realistic world, but returning
            // a wrong-but-plausible zone would silently stack two instances in one sector.
            throw new System.InvalidOperationException(
                $"No free instance zone: all {slots} slots in the instance region are claimed.");
        }

        /// <summary>Where the location itself is spawned, at the centre of its zone.</summary>
        public static Vector3 OriginFor(Vector2s zone)
        {
            Vector3 zonePos = ZoneSystem.GetZonePos(zone);
            return new Vector3(zonePos.x, Altitude, zonePos.z);
        }

        private static int SlotFor(int instanceId)
        {
            unchecked
            {
                // Knuth multiplicative hash plus a shift-xor, so sequential ids scatter across the
                // region instead of packing into one row. Which slot doesn't matter; only that the
                // mapping is stable across restarts.
                uint h = (uint)instanceId * 2654435761u;
                h ^= h >> 15;
                return (int)(h % (uint)(RegionSize * RegionSize));
            }
        }
    }
}
