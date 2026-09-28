using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.OffMap
{
    /// <summary>
    /// Moves a relocated location's interior while it is being spawned.
    ///
    /// HOW VANILLA PLACES THE INTERIOR. For a location with m_useCustomInteriorTransform,
    /// ZoneSystem.SpawnLocation temporarily moves the prefab's interior transform so the dungeon
    /// generator lands at `GetZonePos(GetZone(pos))` plus the prefab's own offsets - the centre of
    /// the entrance's zone, 5000m up. Everything in the interior follows from that one number: the
    /// networked children are instantiated relative to it, the generator's bounds and seed come from
    /// wherever the generator ends up, and the room positions it saves are absolute. So the hook is a
    /// single call: the transpiler swaps that GetZonePos for InteriorZonePos, which returns the slot's
    /// zone centre instead while a relocated location is being spawned.
    ///
    /// The same method runs in three modes and all three need it. Full and Ghost are the server
    /// creating the location (Ghost when the zone is generated for a remote player), and Client is a
    /// LocationProxy building the non-networked parts on each machine - including the entrance
    /// teleport's target, which is how the entrance finds the moved interior.
    ///
    /// WHERE THE DECISION COMES FROM. The server allocates a slot and writes it onto the new
    /// LocationProxy (LocationProxy_SetLocation_Patch). Clients never allocate: in Client mode the
    /// slot is read back off that proxy. So a client's config cannot disagree with the server.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class OffMapSpawnPatches
    {
        internal sealed class SpawnContext
        {
            public Vector2s InteriorZone;
            public Vector3 ExteriorPos;
            public Quaternion ExteriorRot;
            public int LocationHash;
            public ZoneSystem.SpawnMode Mode;
        }

        /// <summary>The relocated spawn in progress, or null. Nested spawns save and restore it.</summary>
        internal static SpawnContext Current;

        /// <summary>The LocationProxy whose client-side spawn is in progress, or null.</summary>
        private static ZDO _currentProxy;

        /// <summary>
        /// Set when the transpiler found its one call. Without it the interior would not move, but the
        /// proxy and generator would still be tagged as if it had, and clients would teleport into
        /// an empty zone - so nothing is relocated unless this is true.
        /// </summary>
        internal static bool TranspilerApplied;

        public static Vector3 InteriorZonePos(Vector2s zone)
        {
            SpawnContext ctx = Current;
            return ctx != null ? ZoneSystem.GetZonePos(ctx.InteriorZone) : ZoneSystem.GetZonePos(zone);
        }

        private static SpawnContext Resolve(ZoneSystem.ZoneLocation location, Vector3 pos, Quaternion rot, ZoneSystem.SpawnMode mode)
        {
            if (!TranspilerApplied || location == null || location.m_prefab == null)
            {
                return null;
            }
            string name = location.m_prefab.Name;
            if (!OffMapRegion.IsRelocated(name))
            {
                return null;
            }

            Vector2s interior;
            if (mode == ZoneSystem.SpawnMode.Client)
            {
                Vector3 zonePos;
                if (_currentProxy == null || !_currentProxy.GetVec3(OffMapKeys.InteriorZone, out zonePos))
                {
                    return null;
                }
                interior = ZoneSystem.GetZone(zonePos);
            }
            else
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer() || !OffMapPlugin.Enabled.Value)
                {
                    return null;
                }
                if (!OffMapAllocator.Allocate(ZoneSystem.GetZone(pos), out interior))
                {
                    return null;
                }
                Jotunn.Logger.LogInfo($"Placing {name} at {pos} with its interior in off-map zone {interior}.");
            }

            return new SpawnContext
            {
                InteriorZone = interior,
                ExteriorPos = pos,
                ExteriorRot = rot,
                LocationHash = name.GetStableHashCode(),
                Mode = mode,
            };
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnLocation),
            new[] { typeof(ZoneSystem.ZoneLocation), typeof(int), typeof(Vector3), typeof(Quaternion),
                    typeof(ZoneSystem.SpawnMode), typeof(List<GameObject>), typeof(bool) })]
        private static class ZoneSystem_SpawnLocation_Patch
        {
            private static void Prefix(ZoneSystem.ZoneLocation location, Vector3 pos, Quaternion rot,
                ZoneSystem.SpawnMode mode, out SpawnContext __state)
            {
                __state = Current;
                Current = Resolve(location, pos, rot, mode);
            }

            private static Exception Finalizer(Exception __exception, SpawnContext __state)
            {
                Current = __state;
                return __exception;
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo vanilla = AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetZonePos), new[] { typeof(Vector2s) });
                MethodInfo ours = AccessTools.Method(typeof(OffMapSpawnPatches), nameof(InteriorZonePos));

                List<CodeInstruction> code = new List<CodeInstruction>(instructions);
                int hits = 0;
                foreach (CodeInstruction instruction in code)
                {
                    if (instruction.Calls(vanilla))
                    {
                        hits++;
                    }
                }

                // Exactly one call is the 1.0.16 shape. Anything else means the method changed under
                // us, and guessing which call is the interior one is how a dungeon ends up half in
                // one zone and half in another - so leave the method alone and relocate nothing.
                if (hits != 1)
                {
                    Jotunn.Logger.LogError($"ZoneSystem.SpawnLocation has {hits} GetZonePos calls, expected 1 - not patching.");
                    TranspilerApplied = false;
                    return code;
                }

                foreach (CodeInstruction instruction in code)
                {
                    if (instruction.Calls(vanilla))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = ours;
                    }
                }
                TranspilerApplied = true;
                return code;
            }
        }

        /// <summary>
        /// Records the slot on the new entrance. Runs before SetLocation's own body because that body
        /// can spawn the client-side parts immediately (spawnNow, on a host), and those read it.
        /// </summary>
        [HarmonyPatch(typeof(LocationProxy), nameof(LocationProxy.SetLocation))]
        private static class LocationProxy_SetLocation_Patch
        {
            private static void Prefix(LocationProxy __instance)
            {
                SpawnContext ctx = Current;
                if (ctx == null || ctx.Mode == ZoneSystem.SpawnMode.Client || __instance.m_nview == null)
                {
                    return;
                }
                ZDO zdo = __instance.m_nview.GetZDO();
                if (zdo != null)
                {
                    zdo.Set(OffMapKeys.InteriorZone, ZoneSystem.GetZonePos(ctx.InteriorZone));
                }
            }
        }

        /// <summary>
        /// Makes the proxy's ZDO visible to Resolve while the proxy builds its location, and afterwards
        /// switches off the copy of the exit teleport the proxy just built.
        ///
        /// That copy is part of the location prefab, so the proxy builds it - in the moved interior,
        /// correctly placed, because the entrance teleport's target lives beside it. But it belongs to
        /// the proxy, which is destroyed as soon as the entrance zone unloads, i.e. right after anyone
        /// goes inside. The working exit is InteriorShell's, which lives with the interior. The proxy's
        /// copy keeps its transform (it is still the entrance's target) and only loses its trigger, so
        /// the two can't both fire.
        /// </summary>
        [HarmonyPatch(typeof(LocationProxy), nameof(LocationProxy.SpawnLocation))]
        private static class LocationProxy_SpawnLocation_Patch
        {
            private static void Prefix(LocationProxy __instance, out ZDO __state)
            {
                __state = _currentProxy;
                _currentProxy = __instance.m_nview != null ? __instance.m_nview.GetZDO() : null;
            }

            private static void Postfix(LocationProxy __instance, bool __result)
            {
                ZDO zdo = _currentProxy;
                if (!__result || zdo == null || __instance.m_instance == null || !zdo.GetVec3(OffMapKeys.InteriorZone, out _))
                {
                    return;
                }
                Location location = __instance.m_instance.GetComponent<Location>();
                if (location == null || location.m_interiorTransform == null)
                {
                    return;
                }
                foreach (Teleport teleport in location.m_interiorTransform.GetComponentsInChildren<Teleport>(true))
                {
                    foreach (Collider collider in teleport.GetComponents<Collider>())
                    {
                        collider.enabled = false;
                    }
                }
            }

            private static Exception Finalizer(Exception __exception, ZDO __state)
            {
                _currentProxy = __state;
                return __exception;
            }
        }

        /// <summary>
        /// Tags the generator with where its entrance is, once it has been placed and generated on the
        /// server, and builds its shell if this machine is going to keep the object (Full mode; Ghost
        /// objects are discarded straight after).
        /// </summary>
        [HarmonyPatch(typeof(DungeonGenerator), nameof(DungeonGenerator.Generate), new[] { typeof(ZoneSystem.SpawnMode) })]
        private static class DungeonGenerator_Generate_Patch
        {
            private static void Postfix(DungeonGenerator __instance, ZoneSystem.SpawnMode mode)
            {
                SpawnContext ctx = Current;
                if (ctx == null || mode == ZoneSystem.SpawnMode.Client || __instance.m_nview == null)
                {
                    return;
                }
                ZDO zdo = __instance.m_nview.GetZDO();
                if (zdo == null)
                {
                    return;
                }
                zdo.Set(OffMapKeys.ExteriorPos, ctx.ExteriorPos);
                zdo.Set(OffMapKeys.ExteriorRot, ctx.ExteriorRot);
                zdo.Set(OffMapKeys.Location, ctx.LocationHash);

                if (mode == ZoneSystem.SpawnMode.Full)
                {
                    InteriorShell.Ensure(__instance);
                }
            }
        }

        /// <summary>
        /// Keeps a moved dungeon's decoration the same as before it moved.
        ///
        /// Mörkhalla's generator has m_addBaseSeedToRandomSpawn, so every RandomSpawn in its rooms -
        /// including the non-networked walls and props each client builds for itself - is seeded from
        /// GetSeed(), and GetSeed() is derived from the generator's world position. A dungeon generated
        /// in the old spot and moved afterwards would give clients a different seed from the one the
        /// server used for the networked objects, so the two halves of each room would disagree.
        /// OffMapMigration stores the original seed on the generator, and this returns it.
        ///
        /// Dungeons that were relocated as they spawned don't need it: server and clients both derive
        /// the seed from the new position, so they already agree.
        /// </summary>
        [HarmonyPatch(typeof(DungeonGenerator), nameof(DungeonGenerator.GetSeed))]
        private static class DungeonGenerator_GetSeed_Patch
        {
            private static bool Prefix(DungeonGenerator __instance, ref int __result)
            {
                if (__instance.m_hasGeneratedSeed || DungeonGenerator.m_forceSeed != int.MinValue || __instance.m_nview == null)
                {
                    return true;
                }
                ZDO zdo = __instance.m_nview.GetZDO();
                int seed;
                if (zdo == null || !zdo.GetInt(OffMapKeys.Seed, out seed))
                {
                    return true;
                }
                __instance.m_generatedSeed = seed;
                __instance.m_hasGeneratedSeed = true;
                __result = seed;
                return false;
            }
        }

        /// <summary>Builds the shell for a moved dungeon whenever its generator is loaded.</summary>
        [HarmonyPatch(typeof(DungeonGenerator), nameof(DungeonGenerator.Awake))]
        private static class DungeonGenerator_Awake_Patch
        {
            private static void Postfix(DungeonGenerator __instance)
            {
                InteriorShell.Ensure(__instance);
            }
        }
    }
}
