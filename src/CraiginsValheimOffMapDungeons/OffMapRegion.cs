using System;
using System.Collections.Generic;
using UnityEngine;

namespace CraiginsValheimMod.OffMap
{
    /// <summary>
    /// Which locations get moved, and where their interiors go.
    ///
    /// THE REGION. A square of zones past the north-west edge of the world: x -255..-176,
    /// z 176..255. Same reasoning as the instances plugin's region (InstanceRegion), and bounded
    /// the same way: past zone ~164 to clear the 10,500m playable circle, and inside +-256 because
    /// ZoneSystem.SectorToIndex collapses anything further out into SectorZero, the shared
    /// out-of-bounds bucket. It is deliberately disjoint from the instance region (x 176..255), so
    /// the two plugins can never hand out the same zone and neither one's teleport redirect fires
    /// for the other's zones.
    ///
    /// SLOTS, NOT EVERY ZONE. Standing in a zone loads its neighbours out to the near simulation
    /// distance (2 zones by default) and distant objects out to near + far (4). Interiors packed
    /// into adjacent zones would load each other, which is the exact thing this plugin exists to
    /// stop. So only every SlotSpacing-th zone is used: 10 x 10 = 100 slots, against Mörkhalla's
    /// cap of 40 per world, with 8 zones between neighbours - enough for simulation settings up to
    /// near + far = 7.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class OffMapRegion
    {
        private const int RegionMinX = -255;
        private const int RegionMaxX = -176;
        private const int RegionMinZ = 176;
        private const int RegionMaxZ = 255;

        private const int SlotSpacing = 8;
        private const int SlotsPerRow = 10;
        public const int SlotCount = SlotsPerRow * SlotsPerRow;

        /// <summary>First slot sits 4 zones in from the region's inner corner (-176, 176).</summary>
        private const int FirstSlotX = -180;
        private const int FirstSlotZ = 180;

        /// <summary>
        /// Location prefabs whose interiors are moved. Matched against ZoneLocation.m_prefab.Name,
        /// which is also what LocationProxy hashes into its ZDO.
        ///
        /// Only locations using m_useCustomInteriorTransform can be moved: that is the code path that
        /// places the interior relative to the zone centre, and it is the one call the spawn hook
        /// redirects. A location without it would be left where it is, not broken.
        /// </summary>
        private static readonly HashSet<string> RelocatedNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MorkBorg" };

        private static HashSet<int> _relocatedHashes;

        public static bool IsRelocated(string prefabName)
        {
            return prefabName != null && RelocatedNames.Contains(prefabName);
        }

        public static bool IsRelocatedHash(int locationHash)
        {
            if (_relocatedHashes == null)
            {
                _relocatedHashes = new HashSet<int>();
                foreach (string name in RelocatedNames)
                {
                    _relocatedHashes.Add(name.GetStableHashCode());
                }
            }
            return _relocatedHashes.Contains(locationHash);
        }

        public static Vector2s SlotZone(int slot)
        {
            int col = slot % SlotsPerRow;
            int row = slot / SlotsPerRow;
            return new Vector2s(FirstSlotX - col * SlotSpacing, FirstSlotZ + row * SlotSpacing);
        }

        public static bool IsOffMapZone(Vector2s zone)
        {
            return zone.x >= RegionMinX && zone.x <= RegionMaxX
                && zone.y >= RegionMinZ && zone.y <= RegionMaxZ;
        }

        public static bool IsOffMapPosition(Vector3 point)
        {
            return IsOffMapZone(ZoneSystem.GetZone(point));
        }

        /// <summary>
        /// The whole-zone shift that carries an interior from its entrance's zone to its slot. Only
        /// ever whole zones, so every position inside keeps its offset from the zone centre - which
        /// is what the dungeon's own bounds and room seeds are measured from.
        /// </summary>
        public static Vector3 Shift(Vector2s fromZone, Vector2s toZone)
        {
            Vector3 d = ZoneSystem.GetZonePos(toZone) - ZoneSystem.GetZonePos(fromZone);
            d.y = 0f;
            return d;
        }
    }

    /// <summary>
    /// The fields this plugin writes into the world, which is how clients learn what the server did.
    ///
    /// On the LocationProxy (the entrance's zone, so readable before anyone goes in):
    ///   InteriorZone - centre of the zone the interior was moved to.
    ///
    /// On the DungeonGenerator (the interior's zone, so readable while inside):
    ///   ExteriorPos / ExteriorRot / Location - where the entrance is, and which location it is, so
    ///     the exit and the interior's stand-in Location can be rebuilt without the entrance loaded.
    ///   Seed - only on dungeons moved after they were generated. See DungeonGenerator_GetSeed_Patch.
    /// </summary>
    internal static class OffMapKeys
    {
        public static readonly int InteriorZone = "cvm_offmap_interior_zone".GetStableHashCode();
        public static readonly int ExteriorPos = "cvm_offmap_exterior_pos".GetStableHashCode();
        public static readonly int ExteriorRot = "cvm_offmap_exterior_rot".GetStableHashCode();
        public static readonly int Location = "cvm_offmap_location".GetStableHashCode();
        public static readonly int Seed = "cvm_offmap_seed".GetStableHashCode();
    }

    /// <summary>
    /// Hands out slots, on the server only.
    ///
    /// The assignment is stored on each entrance's LocationProxy (OffMapKeys.InteriorZone), so it
    /// survives restarts and the table here is only ever a cache of what the world already says.
    /// It is rebuilt from the world at every server start (OffMapMigration) before anything can
    /// spawn, and after that the only writer is Allocate.
    /// </summary>
    internal static class OffMapAllocator
    {
        private static readonly Dictionary<Vector2s, Vector2s> ByExterior = new Dictionary<Vector2s, Vector2s>();
        private static readonly HashSet<Vector2s> Used = new HashSet<Vector2s>();

        public static void Reset()
        {
            ByExterior.Clear();
            Used.Clear();
        }

        public static void MarkUsed(Vector2s exteriorZone, Vector2s interiorZone)
        {
            ByExterior[exteriorZone] = interiorZone;
            Used.Add(interiorZone);
        }

        /// <summary>Lowest free slot. Returns false when all are taken, in which case the dungeon stays vanilla.</summary>
        public static bool Allocate(Vector2s exteriorZone, out Vector2s interiorZone)
        {
            if (ByExterior.TryGetValue(exteriorZone, out interiorZone))
            {
                return true;
            }
            for (int slot = 0; slot < OffMapRegion.SlotCount; slot++)
            {
                Vector2s zone = OffMapRegion.SlotZone(slot);
                if (!Used.Contains(zone))
                {
                    MarkUsed(exteriorZone, zone);
                    interiorZone = zone;
                    return true;
                }
            }
            Jotunn.Logger.LogWarning($"All {OffMapRegion.SlotCount} off-map slots are in use - the dungeon at zone {exteriorZone} keeps its vanilla interior.");
            interiorZone = default(Vector2s);
            return false;
        }

        public static void Release(Vector2s exteriorZone)
        {
            Vector2s zone;
            if (ByExterior.TryGetValue(exteriorZone, out zone))
            {
                ByExterior.Remove(exteriorZone);
                Used.Remove(zone);
            }
        }
    }
}
