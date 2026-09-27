using System.Collections.Generic;
using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Lets the harpoon hook a tamed animal without hurting it, so it can be used to drag one
    /// around.
    ///
    /// WHAT VANILLA DOES. The harpoon (SpearChitin) throws `projectile_chitinharpoon` carrying
    /// 10 pierce, 20 push force and the `Harpooned` status effect. Everything arrives in one
    /// HitData: `Projectile.OnHit` builds it and calls `Character.Damage`, which sends
    /// `RPC_Damage` to the creature's owner, and that one method both attaches the status effect
    /// (from `m_statusEffectHash`) and applies the damage. So the line and the wound are the
    /// same hit, and the only way to have one without the other is to change the hit.
    ///
    /// NO DAMAGE (HarpoonNoDamageToTamed). A prefix on `Character.Damage` empties `m_damage`
    /// when the hit carries a harpoon status effect and the target is tamed. The status effect
    /// hash and the push force are left alone, so the line attaches and the animal is still
    /// nudged by the impact. With no damage, `Character.ApplyDamage` returns at its
    /// "total &lt;= 0.1" check, before the damage text, the stagger and `OnDamaged` - so the
    /// animal isn't hurt, staggered or startled. `Character.Damage` runs on the thrower's
    /// machine, before the RPC is sent, so only the thrower needs the mod; whoever owns the
    /// animal just receives a hit with no damage in it.
    ///
    /// HITTING A TAME AT ALL (HarpoonHitsTamedWithoutPvP). The projectile prefab has
    /// `m_hitFriendly` off, and `Projectile.IsValidTarget` then rejects any target that is not
    /// an enemy of the thrower unless the thrower has PvP enabled. A tamed animal is never a
    /// player's enemy (`BaseAI.IsEnemy`), so with PvP off a vanilla harpoon passes straight
    /// through it - no damage, but no line either. The postfix accepts a tamed animal as a
    /// target for a player's harpoon whatever the PvP setting. Also thrower-side: `OnHit` only
    /// runs for the projectile's owner.
    ///
    /// A harpoon is recognised by the type of the status effect it carries (`SE_Harpooned`),
    /// not by item or prefab name, so a modded harpoon built on the same effect is covered too.
    /// Players are never affected, tamed or otherwise - only non-player tamed characters.
    /// </summary>
    internal static class HarpoonTamedPatches
    {
        private static readonly Dictionary<int, bool> s_isHarpoonEffect = new Dictionary<int, bool>();

        /// <summary>Whether this status effect hash names an SE_Harpooned.</summary>
        private static bool IsHarpoonEffect(int hash)
        {
            if (hash == 0)
            {
                return false;
            }
            if (s_isHarpoonEffect.TryGetValue(hash, out bool known))
            {
                return known;
            }
            if (ObjectDB.instance == null)
            {
                // Nothing to look it up in yet - don't cache a guess.
                return false;
            }

            bool isHarpoon = ObjectDB.instance.GetStatusEffect(hash) is SE_Harpooned;
            s_isHarpoonEffect[hash] = isHarpoon;
            return isHarpoon;
        }

        private static bool IsTamedAnimal(Character character)
        {
            return character != null && !character.IsPlayer() && character.IsTamed();
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class CharacterDamage_Patch
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (!Plugin.HarpoonNoDamageToTamed.Value || hit == null)
                {
                    return;
                }
                if (!IsHarpoonEffect(hit.m_statusEffectHash) || !IsTamedAnimal(__instance))
                {
                    return;
                }

                // Damage only. m_statusEffectHash is what attaches the line, and m_pushForce is
                // the impact - both stay.
                hit.m_damage = default(HitData.DamageTypes);
            }
        }

        [HarmonyPatch(typeof(Projectile), "IsValidTarget")]
        private static class ProjectileIsValidTarget_Patch
        {
            private static void Postfix(Projectile __instance, IDestructible destr, ref bool __result)
            {
                if (__result || !Plugin.HarpoonHitsTamedWithoutPvP.Value)
                {
                    return;
                }

                Character owner = __instance.m_owner;
                if (owner == null || !owner.IsPlayer())
                {
                    return;
                }
                if (!IsHarpoonEffect(__instance.m_statusEffectHash))
                {
                    return;
                }
                if (IsTamedAnimal(destr as Character))
                {
                    __result = true;
                }
            }
        }
    }
}
