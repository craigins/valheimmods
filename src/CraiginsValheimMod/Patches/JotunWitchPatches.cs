using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Slows the Hexe (JotunWitch) down. Three separate settings, all applied here.
    ///
    /// Each attack is an item with its own m_aiAttackInterval, measured from
    /// ItemData.m_lastAttackTime, which Attack.Start stamps as the cast begins. MonsterAI and
    /// Humanoid.EquipBestWeapon both compare against that stamp. On top of that MonsterAI
    /// requires Humanoid.GetTimeSinceLastAttack() >= m_minAttackInterval, and that timer is held
    /// at 0 while in an attack, so it already counts from the end of the previous attack.
    ///
    /// Vanilla witch: magic blast 3.0 s interval on a cast of about 3.1 s (3.167 s clip, exit at
    /// 0.9, 0.25 s blend), lightning bolt 3.0 s on 2.7 s, and four dodges (left, right, up, down)
    /// at 6.0 s each, all equipped at once, with m_minAttackInterval 0. So a spell is ready again
    /// as its cast ends, she never pauses between attacks, and with four independent dodge
    /// timers there is nearly always a dodge ready to fill any gap.
    ///
    ///   CooldownAfterCast   - re-stamps the current attack's weapon every tick while she is in
    ///                         an attack, so the last stamp is the tick it ends and every
    ///                         interval counts from there. A stagger ends the attack early, so
    ///                         the cooldown counts from the stagger.
    ///   SharedCooldown      - sets her m_minAttackInterval, a pause after every attack.
    ///   DodgesShareCooldown - copies a dodge's stamp onto all four dodges, so one dodge puts
    ///                         the others on cooldown too.
    ///
    /// Humanoid.UpdateAttack only runs on the ZDO owner, which is also where her AI runs, so
    /// whoever owns the witch (normally the nearest player, or a dedicated server near the world
    /// centre) needs the mod. Settings are read every tick, so changes apply at once.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UpdateAttack))]
    internal static class JotunWitchPatches
    {
        private static readonly int JotunWitchHash = "JotunWitch".GetStableHashCode();

        private const string DodgeAnimationPrefix = "attack_dodge";

        private static void Postfix(Humanoid __instance)
        {
            ZDO zdo = __instance.m_nview.GetZDO();
            if (zdo == null || zdo.GetPrefab() != JotunWitchHash)
            {
                return;
            }

            if (__instance.m_baseAI is MonsterAI ai)
            {
                ai.m_minAttackInterval = Plugin.JotunWitchSharedCooldown.Value;
            }

            Attack attack = __instance.m_currentAttack;
            if (attack == null || !__instance.InAttack())
            {
                return;
            }
            ItemDrop.ItemData weapon = attack.GetWeapon();
            if (weapon == null)
            {
                return;
            }

            if (Plugin.JotunWitchCooldownAfterCast.Value)
            {
                weapon.m_lastAttackTime = Time.time;
            }

            if (Plugin.JotunWitchDodgesShareCooldown.Value && IsDodge(weapon))
            {
                foreach (ItemDrop.ItemData item in __instance.m_inventory.GetAllItems())
                {
                    if (item != weapon && IsDodge(item))
                    {
                        item.m_lastAttackTime = weapon.m_lastAttackTime;
                    }
                }
            }
        }

        private static bool IsDodge(ItemDrop.ItemData item)
        {
            string animation = item.m_shared.m_attack?.m_attackAnimation;
            return animation != null && animation.StartsWith(DodgeAnimationPrefix);
        }
    }
}
