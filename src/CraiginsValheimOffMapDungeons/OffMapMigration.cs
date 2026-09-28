using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.OffMap
{
    /// <summary>
    /// Moves dungeons that were generated before this plugin was installed, once, at server start.
    ///
    /// Runs straight after ZNet.ServerLoadWorld, when every ZDO is in memory and no zone has been
    /// loaded for anyone yet, so nothing it moves exists as a GameObject anywhere. A moved dungeon is
    /// marked on its proxy (OffMapKeys.InteriorZone), which is also what makes the pass idempotent:
    /// if the server stops before the next save, the world on disk is unchanged and the pass simply
    /// runs again.
    ///
    /// What moves: every ZDO above y = 3000 in the entrance's zone - vanilla puts nothing else there.
    /// Each is shifted by whole zones, which moves it between sectors; ZDOMan.AddToSector and
    /// RemoveFromSector mark both the old and new save chunks dirty for persistent objects, so the
    /// chunked 1.0 save writes it once, in its new place.
    ///
    /// Positions stored INSIDE objects move too. The generator's room list is absolute world
    /// positions, and creatures carry their spawn point and patrol point. Rather than list every
    /// such field, any Vec3 on a moved object that lies inside the old interior is shifted with it.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.ServerLoadWorld))]
    [HarmonyAfter("com.craigins.valheimmod")] // its DungeonRerollCleanup must see the dungeons where they were generated
    internal static class OffMapMigration
    {
        private static void Postfix()
        {
            OffMapAllocator.Reset();
            if (ZNet.m_loadError || ZDOMan.instance == null || ZoneSystem.instance == null)
            {
                return;
            }

            try
            {
                Run();
            }
            catch (System.Exception e)
            {
                Jotunn.Logger.LogError($"Moving existing dungeons failed: {e}");
            }
        }

        private static void Run()
        {
            int proxyPrefab = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
            List<ZDO> pending = new List<ZDO>();
            Dictionary<Vector2s, List<ZDO>> interiors = new Dictionary<Vector2s, List<ZDO>>();
            List<ZDO> high = new List<ZDO>();

            foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
            {
                if (zdo.GetPosition().y > 3000f)
                {
                    high.Add(zdo);
                    continue;
                }
                if (zdo.GetPrefab() != proxyPrefab || !OffMapRegion.IsRelocatedHash(zdo.GetInt(ZDOVars.s_location)))
                {
                    continue;
                }
                Vector2s exteriorZone = ZoneSystem.GetZone(zdo.GetPosition());
                Vector3 zonePos;
                if (zdo.GetVec3(OffMapKeys.InteriorZone, out zonePos))
                {
                    OffMapAllocator.MarkUsed(exteriorZone, ZoneSystem.GetZone(zonePos));
                }
                else
                {
                    pending.Add(zdo);
                    interiors[exteriorZone] = new List<ZDO>();
                }
            }

            if (pending.Count == 0 || !OffMapSpawnPatches.TranspilerApplied || !OffMapPlugin.Enabled.Value)
            {
                return;
            }

            foreach (ZDO zdo in high)
            {
                List<ZDO> list;
                if (interiors.TryGetValue(ZoneSystem.GetZone(zdo.GetPosition()), out list))
                {
                    list.Add(zdo);
                }
            }

            // Deterministic order, so the same world always gets the same slots.
            pending.Sort((a, b) =>
            {
                Vector2s za = ZoneSystem.GetZone(a.GetPosition());
                Vector2s zb = ZoneSystem.GetZone(b.GetPosition());
                return za.x != zb.x ? za.x.CompareTo(zb.x) : za.y.CompareTo(zb.y);
            });

            int moved = 0;
            foreach (ZDO proxy in pending)
            {
                Vector2s exteriorZone = ZoneSystem.GetZone(proxy.GetPosition());
                if (Move(proxy, exteriorZone, interiors[exteriorZone]))
                {
                    moved++;
                }
            }
            if (moved > 0)
            {
                Jotunn.Logger.LogInfo($"Moved {moved} existing dungeon interior(s) off the map.");
            }
        }

        private static bool Move(ZDO proxy, Vector2s exteriorZone, List<ZDO> contents)
        {
            ZDO generator = null;
            foreach (ZDO zdo in contents)
            {
                if (zdo.GetByteArray(ZDOVars.s_roomData) != null)
                {
                    generator = zdo;
                    break;
                }
            }
            if (generator == null)
            {
                Jotunn.Logger.LogWarning($"Dungeon entrance at {proxy.GetPosition()} has no generated interior - leaving it alone.");
                return false;
            }

            Vector2s interiorZone;
            if (!OffMapAllocator.Allocate(exteriorZone, out interiorZone))
            {
                return false;
            }
            Vector3 shift = OffMapRegion.Shift(exteriorZone, interiorZone);

            // Before anything moves: the seed vanilla derived from the generator's original position.
            generator.Set(OffMapKeys.Seed, VanillaSeed(generator.GetPosition()));
            generator.Set(OffMapKeys.ExteriorPos, proxy.GetPosition());
            generator.Set(OffMapKeys.ExteriorRot, proxy.GetRotation());
            generator.Set(OffMapKeys.Location, proxy.GetInt(ZDOVars.s_location));
            generator.Set(ZDOVars.s_roomData, ShiftRooms(generator.GetByteArray(ZDOVars.s_roomData), shift));

            foreach (ZDO zdo in contents)
            {
                ShiftStoredPositions(zdo, exteriorZone, shift);
                zdo.SetPosition(zdo.GetPosition() + shift);
            }

            proxy.Set(OffMapKeys.InteriorZone, ZoneSystem.GetZonePos(interiorZone));
            Jotunn.Logger.LogInfo($"Moved the interior of the dungeon at {proxy.GetPosition()} ({contents.Count} objects) to off-map zone {interiorZone}.");
            return true;
        }

        /// <summary>DungeonGenerator.GetSeed's own derivation, for a generator standing at <paramref name="position"/>.</summary>
        private static int VanillaSeed(Vector3 position)
        {
            int seed = WorldGenerator.instance.GetSeed();
            Vector2i zone = ZoneSystem.GetZone(position).ToVector2i();
            return seed + zone.x * 4271 + zone.y * -7187 + (int)position.x * -4271 + (int)position.y * 9187 + (int)position.z * -2134;
        }

        /// <summary>
        /// Rewrites DungeonGenerator's saved room list with every position shifted. The format is
        /// DungeonGenerator.Save's: a count, then per room a hash, a position and a rotation stored as
        /// euler angles. The rotation bytes are copied through untouched.
        /// </summary>
        private static byte[] ShiftRooms(byte[] data, Vector3 shift)
        {
            BinaryReader reader = new BinaryReader(new MemoryStream(data));
            MemoryStream output = new MemoryStream(data.Length);
            BinaryWriter writer = new BinaryWriter(output);

            int count = reader.ReadInt32();
            writer.Write(count);
            for (int i = 0; i < count; i++)
            {
                writer.Write(reader.ReadInt32());
                writer.Write(reader.ReadVector3() + shift);
                writer.Write(reader.ReadVector3());
            }
            writer.Flush();
            return output.ToArray();
        }

        private static void ShiftStoredPositions(ZDO zdo, Vector2s exteriorZone, Vector3 shift)
        {
            List<KeyValuePair<int, float>> floats;
            List<KeyValuePair<int, Vector3>> vec3s;
            List<KeyValuePair<int, Quaternion>> quats;
            List<KeyValuePair<int, int>> ints;
            List<KeyValuePair<int, long>> longs;
            List<KeyValuePair<int, string>> strings;
            List<KeyValuePair<int, byte[]>> bytes;
            ZDOConnection connection;
            ZDOExtraData.GetData(zdo.m_uid, out floats, out vec3s, out quats, out ints, out longs, out strings, out bytes, out connection);

            // Copied first: Set on the same ZDO while walking its live list would be walking a list
            // that is being written to.
            List<KeyValuePair<int, Vector3>> inside = new List<KeyValuePair<int, Vector3>>();
            foreach (KeyValuePair<int, Vector3> pair in vec3s)
            {
                if (pair.Value.y > 3000f && ZoneSystem.GetZone(pair.Value) == exteriorZone)
                {
                    inside.Add(pair);
                }
            }
            foreach (KeyValuePair<int, Vector3> pair in inside)
            {
                zdo.Set(pair.Key, pair.Value + shift);
            }
        }
    }
}
