using System;
using System.Collections.Generic;
using UnityEngine;

namespace CraiginsValheimMod.Dungeons
{
    /// <summary>
    /// Finding and clearing the contents of a generated dungeon interior.
    ///
    /// Dungeon interiors aren't in the terrain. Location.Awake instantiates the interior 5000m
    /// straight up from the entrance, centred on the ZONE centre in XZ:
    ///     Object.Instantiate(m_interiorPrefab,
    ///         new Vector3(zoneCenter.x, transform.position.y + 5000f, zoneCenter.z), ...)
    /// and Character.InInterior(pos) is a bare `pos.y > 3000f`. Two things follow, and both make
    /// this much easier than resetting anything on the surface:
    ///
    ///   - There is no terrain involved. No TerrainComp, no heightmap edits to unwind.
    ///   - ZDO sectors are computed from XZ only, so every interior ZDO lives in the SAME sector
    ///     as the surface entrance. "Everything in this dungeon" is one FindSectorObjects call
    ///     plus a height filter, not a spatial search.
    ///
    /// Why clearing has to be explicit: DungeonGenerator.Clear() only destroys children of the
    /// generator's transform, which is just the room shells. The networked contents - chests,
    /// spawners, doors, torches - are instantiated UNPARENTED in PlaceRoom:
    ///     GameObject gameObject = Object.Instantiate(zNetView.gameObject, position2, rotation);
    /// so Clear() walks straight past them and they survive as orphaned ZDOs. Anything that
    /// regenerates a dungeon in place has to sweep them itself first, or the new layout is built
    /// on top of the old one's furniture.
    /// </summary>
    internal static class DungeonInterior
    {
        /// <summary>The game's own definition of "in an interior" (Character.InInterior).</summary>
        public const float InteriorFloor = 3000f;

        /// <summary>
        /// How far from the generator's own altitude an object still counts as part of this
        /// dungeon. The generator sits up in the interior with it, and the interior EnvZone box
        /// is 500 tall, so half of that is generous without reaching into anything else that
        /// might one day be parked at a different offset in the same zone.
        /// </summary>
        private const float InteriorBandHalfHeight = 250f;

        public static Vector2i ZoneOf(DungeonGenerator dungeon)
        {
            return ZoneSystem.GetZone(dungeon.transform.position);
        }

        public static ZDO GetZdo(DungeonGenerator dungeon)
        {
            ZNetView nview = dungeon.GetComponent<ZNetView>();
            return nview != null ? nview.GetZDO() : null;
        }

        /// <summary>
        /// Room count as actually persisted, read back from the generator's own ZDO rather than
        /// from DungeonGenerator.m_placedRooms - that static list is cleared at the end of every
        /// Generate() and is empty at any moment we'd want to inspect a dungeon from outside.
        /// Save() writes the count as the first int of the s_roomData blob. -1 if never saved.
        /// </summary>
        public static int GetSavedRoomCount(DungeonGenerator dungeon)
        {
            ZDO zdo = GetZdo(dungeon);
            if (zdo != null && zdo.GetByteArray(ZDOVars.s_roomData, out byte[] data) && data.Length >= 4)
            {
                return BitConverter.ToInt32(data, 0);
            }
            return -1;
        }

        /// <summary>Is this world position inside this particular dungeon's interior?</summary>
        public static bool IsInside(DungeonGenerator dungeon, Vector3 point)
        {
            return point.y > InteriorFloor
                && Mathf.Abs(point.y - dungeon.transform.position.y) <= InteriorBandHalfHeight
                && ZoneSystem.GetZone(point) == ZoneOf(dungeon);
        }

        /// <summary>
        /// Every ZDO belonging to this dungeon's interior, excluding the generator's own ZDO -
        /// that one holds the room layout and has to survive to be rewritten by Save().
        ///
        /// Player-built pieces are identified by ZDOVars.s_creator, which Piece.SetCreator stamps
        /// with the builder's player ID and which is 0 on everything the world places itself. It's
        /// the one reliable discriminator available at the ZDO level - there's no way to tell a
        /// dungeon's own chest from a player's chest by prefab, since they're the same prefab.
        /// Loose ItemDrops are NOT distinguishable this way and are always swept.
        /// </summary>
        public static void Collect(DungeonGenerator dungeon, bool preservePlayerBuilt, List<ZDO> doomed, out int preserved)
        {
            doomed.Clear();
            preserved = 0;

            var sector = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(ZoneOf(dungeon), 0, 0, sector);

            ZDO self = GetZdo(dungeon);
            foreach (ZDO zdo in sector)
            {
                if (zdo == null || zdo == self)
                {
                    continue;
                }
                if (!IsInside(dungeon, zdo.GetPosition()))
                {
                    continue;
                }
                if (preservePlayerBuilt && zdo.GetLong(ZDOVars.s_creator, 0L) != 0L)
                {
                    preserved++;
                    continue;
                }
                doomed.Add(zdo);
            }
        }

        /// <summary>
        /// Destroys the given ZDOs for real - locally and on every client.
        ///
        /// Ownership is claimed first because both destroy paths are no-ops without it:
        /// ZDOMan.DestroyZDO is `if (zdo.IsOwner()) m_destroySendList.Add(...)` and silently does
        /// nothing otherwise, and ZNetScene.Destroy wraps the same check. A dungeon's contents are
        /// routinely owned by whichever client was last near them, so this matters in practice.
        /// </summary>
        public static int Destroy(List<ZDO> doomed)
        {
            long session = ZDOMan.GetSessionID();
            int destroyed = 0;

            foreach (ZDO zdo in doomed)
            {
                if (!zdo.IsOwner())
                {
                    zdo.SetOwner(session);
                }

                // Prefer the scene path when the object is actually instantiated here, so the
                // local GameObject goes away with the ZDO instead of lingering until the next
                // ZNetScene cleanup pass.
                ZNetView instance = ZNetScene.instance.FindInstance(zdo);
                if (instance != null)
                {
                    ZNetScene.instance.Destroy(instance.gameObject);
                }
                else
                {
                    ZDOMan.instance.DestroyZDO(zdo);
                }
                destroyed++;
            }

            return destroyed;
        }
    }
}
