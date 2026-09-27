using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Removes the grappling hook's reload, so it can be fired whenever an attack can start.
    ///
    /// WHAT VANILLA DOES. The grappling hook's primary attack has `m_requiresReload` set, the same
    /// mechanism as the crossbows and the lightning staff. `Attack.Start` refuses to fire unless
    /// `Player.IsWeaponLoaded()`, and firing unloads the weapon (`ResetLoadedWeapon`). Every frame
    /// `Player.UpdateWeaponLoading` sees an unloaded weapon and queues a reload action, which
    /// plays `reload_crossbow` for `m_reloadTime` (2 s - the hook has no skill, so it is never
    /// shortened) and only then calls `SetWeaponLoaded`. Two more things hold the reload back:
    /// `QueueReloadAction` does nothing while `m_blockReload` is running (1.1 s after each shot,
    /// from `m_blockReloadTime`) or while `m_grappling` is - which a live grappling point keeps
    /// topped up every frame. So the wait is the flight, plus the whole pull, plus 2 s.
    /// The secondary attack needs no reload in vanilla and is not changed.
    ///
    /// WHAT THIS DOES. A prefix on `UpdateWeaponLoading` marks the hook loaded straight away and
    /// skips the original, so no reload is ever queued. Nothing else is touched: the weapon is
    /// still "a weapon that needs loading" as far as the game is concerned, it is just always
    /// loaded. The shot still costs its stamina and still has to wait for the previous attack
    /// animation, and equipping still takes its usual moment.
    ///
    /// Loading goes through the game's own `SetWeaponLoaded`, which also writes the loaded flag to
    /// the player's ZDO. The model on the weapon (`WeaponLoadState`) reads that flag, so the hook
    /// is drawn sitting in the launcher at all times, for everyone, even while it is out on the
    /// line.
    ///
    /// Being loaded during a pull is new: in vanilla the only thing you can fire mid-grapple is
    /// the secondary. Firing the primary again is handled by the game as it stands -
    /// `GrapplingPoint.Activate` breaks the local player's previous point when a new one attaches.
    ///
    /// A grappling hook is recognised by what its projectile leaves behind - a prefab with a
    /// `GrapplingPoint` on it, via `Projectile.m_spawnOnHit` - not by item name, so crossbows and
    /// the lightning staff keep their reload.
    ///
    /// Only the local player's own weapon is handled, and only on their machine. The server and
    /// other players need nothing.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class GrapplingHookPatches
    {
        private static readonly Dictionary<ItemDrop.ItemData.SharedData, bool> s_isGrapplingHook =
            new Dictionary<ItemDrop.ItemData.SharedData, bool>();

        private static bool IsGrapplingHook(ItemDrop.ItemData weapon)
        {
            ItemDrop.ItemData.SharedData shared = weapon.m_shared;
            if (shared == null || shared.m_attack == null)
            {
                return false;
            }
            if (s_isGrapplingHook.TryGetValue(shared, out bool known))
            {
                return known;
            }

            GameObject fired = shared.m_attack.m_attackProjectile;
            Projectile projectile = fired != null ? fired.GetComponent<Projectile>() : null;
            bool isHook = projectile != null
                && projectile.m_spawnOnHit != null
                && projectile.m_spawnOnHit.GetComponent<GrapplingPoint>() != null;

            s_isGrapplingHook[shared] = isHook;
            return isHook;
        }

        [HarmonyPatch(typeof(Player), "UpdateWeaponLoading")]
        private static class PlayerUpdateWeaponLoading_Patch
        {
            private static bool Prefix(Player __instance, ItemDrop.ItemData weapon)
            {
                if (!Plugin.GrapplingHookNoReload.Value || weapon == null || __instance != Player.m_localPlayer)
                {
                    return true;
                }
                if (!weapon.m_shared.m_attack.m_requiresReload || !IsGrapplingHook(weapon))
                {
                    return true;
                }

                if (__instance.m_weaponLoaded != weapon)
                {
                    // A reload can already be in the queue if the setting was switched on mid-reload.
                    __instance.CancelReloadAction();
                    __instance.SetWeaponLoaded(weapon);
                }
                return false;
            }
        }
    }
}
