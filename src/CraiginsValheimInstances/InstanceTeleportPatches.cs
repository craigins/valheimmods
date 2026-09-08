using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Instances
{
    /// <summary>
    /// Where a player came from before they entered an instance.
    ///
    /// Stored on Player.m_customData, which Valheim serializes with the player profile
    /// (Player.cs writes it in its save package). That matters: the one time this value is
    /// indispensable is when someone logs out inside an instance and comes back to a world where
    /// it no longer exists, and a static field wouldn't survive that.
    /// </summary>
    internal static class InstanceReturn
    {
        private const string Key = "cvm_instance_return";

        public static void Set(Player player, Vector3 point)
        {
            if (player == null)
            {
                return;
            }
            player.m_customData[Key] = string.Format(CultureInfo.InvariantCulture, "{0};{1};{2}", point.x, point.y, point.z);
        }

        public static bool TryGet(Player player, out Vector3 point)
        {
            point = Vector3.zero;
            if (player == null)
            {
                return false;
            }

            string raw;
            if (!player.m_customData.TryGetValue(Key, out raw) || string.IsNullOrEmpty(raw))
            {
                return false;
            }

            string[] parts = raw.Split(';');
            float x, y, z;
            if (parts.Length != 3
                || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            {
                return false;
            }

            point = new Vector3(x, y, z);
            return !InstanceRegion.IsInstancePosition(point);
        }

        public static void Clear(Player player)
        {
            if (player != null)
            {
                player.m_customData.Remove(Key);
            }
        }

        /// <summary>
        /// Best known way out, in descending order of confidence: where they entered from, then
        /// their home point (bed). Returns false when we have neither.
        /// </summary>
        public static bool TryResolveExit(Player player, out Vector3 point)
        {
            if (TryGet(player, out point))
            {
                return true;
            }

            if (Game.instance != null && Game.instance.GetPlayerProfile() != null)
            {
                Vector3 home = Game.instance.GetPlayerProfile().GetHomePoint();
                if (home != Vector3.zero && !InstanceRegion.IsInstancePosition(home))
                {
                    point = home;
                    return true;
                }
            }

            point = Vector3.zero;
            return false;
        }
    }

    /// <summary>
    /// Keeps a player from being stranded by an instance.
    ///
    /// THE EXIT. A dungeon's interior exit Teleport points at m_targetPoint on the location's
    /// exterior. For a normal dungeon that's a doorway in a hillside; for an instance it's a crypt
    /// entrance floating 20km up with no ground under it, so taking the vanilla exit would drop the
    /// player into an endless fall. Teleport.Interact is intercepted and any teleport whose
    /// destination is a non-interior position inside the instance region is redirected home.
    ///
    /// THE LOGOUT. PlayerProfile.SaveLogoutPoint stores the player's literal position, and
    /// Game.FindSpawnPoint puts them back there next login. Log out inside an instance and you'd
    /// return to a world where it no longer exists, at y=25000. So the logout point is rewritten to
    /// the return point instead. This is cheaper and more reliable than trying to rescue them after
    /// the fact on login.
    ///
    /// Both patches are client-side and no-op anywhere outside the instance region, so they cost
    /// nothing in ordinary play.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class InstanceTeleportPatches
    {
        [HarmonyPatch(typeof(Teleport), nameof(Teleport.Interact))]
        private static class Teleport_Interact_Patch
        {
            private static bool Prefix(Teleport __instance, Humanoid character, bool hold, ref bool __result)
            {
                if (hold || __instance == null || __instance.m_targetPoint == null)
                {
                    return true;
                }

                Vector3 target = __instance.m_targetPoint.transform.position;
                if (!InstanceRegion.IsInstancePosition(target) || Character.InInterior(target))
                {
                    // Not ours, or it's the entrance heading further in - both are fine as vanilla.
                    return true;
                }

                Player player = character as Player;
                if (player == null || player != Player.m_localPlayer)
                {
                    return true;
                }

                Vector3 home;
                if (!InstanceReturn.TryResolveExit(player, out home))
                {
                    player.Message(MessageHud.MessageType.Center,
                        "You don't remember the way back. Use 'dungeoninstance out'.");
                    __result = false;
                    return false;
                }

                __result = player.TeleportTo(home, player.transform.rotation, distantTeleport: true);
                if (__result)
                {
                    InstanceReturn.Clear(player);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SaveLogoutPoint))]
        private static class PlayerProfile_SaveLogoutPoint_Patch
        {
            private static bool Prefix(PlayerProfile __instance)
            {
                Player player = Player.m_localPlayer;
                if (player == null || !InstanceRegion.IsInstancePosition(player.transform.position))
                {
                    return true;
                }

                Vector3 home;
                if (!InstanceReturn.TryResolveExit(player, out home))
                {
                    // Nothing better to offer, so let vanilla record the instance position. The
                    // instance is likely still there on the next login; if it isn't, the console
                    // command is the way out.
                    return true;
                }

                __instance.SetLogoutPoint(home);
                Jotunn.Logger.LogInfo($"Logging out inside an instance - logout point rewritten to {home}.");
                return false;
            }
        }
    }
}
