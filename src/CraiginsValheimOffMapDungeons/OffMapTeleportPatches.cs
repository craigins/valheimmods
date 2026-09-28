using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.OffMap
{
    /// <summary>
    /// Getting in and out of a moved interior.
    ///
    /// WHY VANILLA'S TELEPORT ISN'T ENOUGH. A dungeon door calls TeleportTo with distantTeleport
    /// false: in vanilla the interior is in the same zone, already loaded, so the player can be
    /// dropped straight onto the floor. Player.UpdateTeleport treats a non-distant teleport that
    /// finds no floor as blocked and puts the player back where they started. A moved interior is
    /// several kilometres away and not loaded until the player's position says so, so both doors
    /// have to be distant teleports - the loading screen, waiting for the area.
    ///
    /// Entrance: the door's target is the proxy's copy of the exit, which the spawn hook placed in
    /// the moved interior, so the only change is the distant flag. Exit: a ShellExit, whose
    /// destination was worked out from the stored entrance position because the entrance itself is
    /// not loaded while anyone is inside.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class OffMapTeleportPatches
    {
        /// <summary>How long past vanilla's 15 second give-up a teleport into a moved interior keeps waiting for its floor.</summary>
        private const float ExtraFloorWaitSeconds = 60f;

        [HarmonyPatch(typeof(Teleport), nameof(Teleport.Interact))]
        private static class Teleport_Interact_Patch
        {
            private static bool Prefix(Teleport __instance, Humanoid character, bool hold, ref bool __result)
            {
                if (hold || __instance == null || character == null)
                {
                    return true;
                }

                ShellExit exit = __instance.GetComponent<ShellExit>();
                if (exit != null)
                {
                    if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBossPortals) && character.InInterior()
                        && Location.IsInsideActiveBossDungeon(character.transform.position))
                    {
                        character.Message(MessageHud.MessageType.Center, "$msg_blockedbyboss");
                        __result = false;
                        return false;
                    }
                    __result = character.TeleportTo(exit.Destination, exit.Rotation, distantTeleport: true);
                    if (__result)
                    {
                        Game.instance.IncrementPlayerStat(PlayerStatType.PortalDungeonOut);
                    }
                    return false;
                }

                Teleport target = __instance.m_targetPoint;
                if (target == null
                    || !OffMapRegion.IsOffMapPosition(target.transform.position)
                    || OffMapRegion.IsOffMapPosition(__instance.transform.position))
                {
                    return true;
                }

                __result = character.TeleportTo(target.GetTeleportPoint(), target.transform.rotation, distantTeleport: true);
                if (__result)
                {
                    Game.instance.IncrementPlayerStat(PlayerStatType.PortalDungeonIn);
                    if (__instance.m_enterText.Length > 0)
                    {
                        MessageHud.instance.ShowBiomeFoundMsg(__instance.m_enterText, playStinger: false);
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// A moved interior's zone isn't ready until its rooms are.
        ///
        /// IsAreaReady only checks that every networked object in the zone has been instantiated. The
        /// dungeon's rooms are not networked objects - the generator loads their prefabs asynchronously
        /// after it is instantiated, and flags the zone as loading until they are placed
        /// (ZoneSystem.SetLoadingInZone). Without this, the teleport would look for a floor that isn't
        /// there yet.
        /// </summary>
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
        private static class ZNetScene_IsAreaReady_Patch
        {
            private static void Postfix(Vector3 point, ref bool __result)
            {
                if (!__result || ZoneSystem.instance == null)
                {
                    return;
                }
                Vector2s zone = ZoneSystem.GetZone(point);
                if (OffMapRegion.IsOffMapZone(zone) && ZoneSystem.instance.m_loadingObjectsInZones.ContainsKey(zone))
                {
                    __result = false;
                }
            }
        }

        /// <summary>
        /// Gives a slow machine longer to load the interior before the teleport gives up.
        ///
        /// A distant teleport that still has no floor after 15 seconds drops the player at the ground
        /// height below the target instead. Below a moved interior that is the sea bed past the edge of
        /// the world, where Player.EdgeOfWorldKill kills anyone under y = -10. Mörkhalla is by far the
        /// heaviest thing in the game to load, so while the target is off-map and still has no floor,
        /// the clock is held just short of 15 seconds, for up to another minute.
        /// </summary>
        [HarmonyPatch(typeof(Player), nameof(Player.UpdateTeleport))]
        private static class Player_UpdateTeleport_Patch
        {
            private static float _extraWait;

            private static void Prefix(Player __instance, float dt)
            {
                if (!__instance.m_teleporting || !__instance.m_distantTeleport)
                {
                    _extraWait = 0f;
                    return;
                }
                if (__instance.m_teleportTimer < 14f
                    || _extraWait > ExtraFloorWaitSeconds
                    || !OffMapRegion.IsOffMapPosition(__instance.m_teleportTargetPos))
                {
                    return;
                }
                float height;
                if (!ZoneSystem.instance.FindFloor(__instance.m_teleportTargetPos, out height))
                {
                    __instance.m_teleportTimer = 14f;
                    _extraWait += dt;
                }
            }
        }

        /// <summary>
        /// Building inside a moved interior is refused the same way it is inside a vanilla one.
        ///
        /// The stand-in Location in InteriorShell already covers this with vanilla's own radius, but
        /// only while the shell exists. This catches the rest of the interior's zone above y = 3000
        /// regardless - there is nothing else up there that anyone could legitimately be building on.
        /// </summary>
        [HarmonyPatch(typeof(Location), nameof(Location.IsInsideNoBuildLocation))]
        private static class Location_IsInsideNoBuildLocation_Patch
        {
            private static void Postfix(Vector3 point, ref bool __result)
            {
                if (!__result && Character.InInterior(point) && OffMapRegion.IsOffMapPosition(point))
                {
                    __result = true;
                }
            }
        }
    }
}
