using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Three changes to the grappling hook, each with its own setting: no reload, no gravity on
    /// the hook in flight, and no limit on how far it reaches.
    ///
    /// NO RELOAD (`GrapplingHookNoReload`)
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
    /// NO GRAVITY (`GrapplingHookNoGravity`)
    ///
    /// WHAT VANILLA DOES. The hook is an ordinary `Projectile`. Every physics step
    /// `Projectile.FixedUpdate` adds `m_gravity` downwards to its velocity: 10 for the primary
    /// hook and 5 for the secondary, against a launch speed of 40. Nothing compensates for it -
    /// the attack's `m_launchAngle` is 0 - so the hook leaves along the look direction and sags
    /// below it, by about 5 m (2.5 m for the secondary) over the 1 s it lives. `m_drag` is
    /// already 0.
    ///
    /// WHAT THIS DOES. A postfix on `Projectile.Setup` sets `m_gravity` to 0 on that one hook, so
    /// it keeps the velocity it was fired with and flies dead straight along the look direction.
    /// The prefab is not touched.
    ///
    /// NO RANGE LIMIT (`GrapplingHookNoRangeLimit`)
    ///
    /// WHAT VANILLA DOES. Two separate things cap the reach.
    ///   1. The hook's lifetime. `Projectile.m_ttl` is 1 s on both hooks, and at speed 40 that is
    ///      about 40 m of flight. When it runs out the hook is destroyed without attaching
    ///      (`m_spawnOnTtl` is off).
    ///   2. The line's length. `GrapplingPoint.Update` breaks the line whenever the player is
    ///      further than `m_maxLength` from the point: 60 m for the primary, 70 m for the
    ///      secondary. A hook that landed beyond that would attach and snap on the same frame.
    ///
    /// WHAT THIS DOES. The same `Projectile.Setup` postfix replaces the 1 s lifetime with
    /// <see cref="MissedHookLifetime"/>, and a prefix on `GrapplingPoint.Activate` sets that
    /// point's `m_maxLength` to infinity, so the length check can never pass.
    ///
    /// The lifetime is made long rather than switched off (`m_ttl` of 0 means "never expires").
    /// A hook fired at the sky has nothing to hit, and the game only tidies up objects by where
    /// they are on the map, not by height, so one fired straight up would climb for as long as
    /// the player stayed in the area, still being sent to the server every frame. The lifetime
    /// is only there to collect those. It is far longer than any shot that has something to hit
    /// can take, so it is not a range limit in practice.
    ///
    /// WHAT STILL LIMITS THE REACH. The hook can only catch on what exists on this machine:
    /// terrain and objects are only there for the zones loaded around the player
    /// (`ZoneSystem.CreateLocalZones`, `ZNetScene.CreateDestroyObjects`). At the original
    /// simulation distance that is the player's zone and two more in every direction, so between
    /// 128 m and 192 m depending on where in their zone the player stands; the game's simulation
    /// distance setting moves it. A hook that crosses that edge is removed by the game
    /// (`ZNetScene.RemoveObjects`), as any object is. The hook's speed is unchanged, so a long
    /// shot takes a while to arrive - 100 m is 2.5 s - and the pull is unchanged too, at 20 m/s.
    /// The line is still broken by everything else that breaks it: blocking, attacking, putting
    /// the launcher away, or making no headway for `m_breakEarlyTime`.
    ///
    /// ALL THREE
    ///
    /// A grappling hook is recognised by what its projectile leaves behind - a prefab with a
    /// `GrapplingPoint` on it, via `Projectile.m_spawnOnHit` - not by item name, so crossbows and
    /// the lightning staff keep their reload and every other projectile keeps its flight.
    ///
    /// Only the local player's own weapon and shots are handled, and only on their machine. A
    /// projectile and a grappling point are both simulated by whoever fired them, so the server
    /// and other players need nothing.
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

        /// <summary>
        /// How long a hook that hits nothing is left flying before it is removed, in seconds. At
        /// the hook's speed of 40 this is 2.4 km, well past anything that can be loaded.
        /// </summary>
        private const float MissedHookLifetime = 60f;

        private static bool IsGrapplingHook(Projectile projectile)
        {
            return projectile.m_spawnOnHit != null
                && projectile.m_spawnOnHit.GetComponent<GrapplingPoint>() != null;
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class ProjectileSetup_Patch
        {
            private static void Postfix(Projectile __instance, Character owner)
            {
                bool noGravity = Plugin.GrapplingHookNoGravity.Value;
                bool noRangeLimit = Plugin.GrapplingHookNoRangeLimit.Value;
                if (!noGravity && !noRangeLimit)
                {
                    return;
                }
                if (owner == null || owner != Player.m_localPlayer || !IsGrapplingHook(__instance))
                {
                    return;
                }

                if (noGravity)
                {
                    __instance.m_gravity = 0f;
                }
                if (noRangeLimit)
                {
                    // Setup runs straight after the hook is created, so this is the whole flight.
                    // Once the hook lands the game replaces it with m_stayTTL, as it always has.
                    __instance.m_ttl = MissedHookLifetime;
                }
            }
        }

        [HarmonyPatch(typeof(GrapplingPoint), nameof(GrapplingPoint.Activate))]
        private static class GrapplingPointActivate_Patch
        {
            private static void Prefix(GrapplingPoint __instance, Character character)
            {
                if (!Plugin.GrapplingHookNoRangeLimit.Value)
                {
                    return;
                }
                if (character == null || character != Player.m_localPlayer)
                {
                    return;
                }

                // Only this point, not the prefab. The check is "distance > m_maxLength".
                __instance.m_maxLength = float.PositiveInfinity;
            }
        }
    }
}
