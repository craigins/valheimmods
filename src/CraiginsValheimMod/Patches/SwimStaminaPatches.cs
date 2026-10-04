using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Swimming without running out of stamina.
    ///
    /// DRAIN. Player.OnSwimming, while you are moving in the water, uses
    /// Lerp(m_swimStaminaDrainMinSkill, m_swimStaminaDrainMaxSkill, swim skill) per second (5 at
    /// skill 0 down to 2 at 100 in the class defaults), plus the armour and status effect swim
    /// modifiers, which are both a fraction of that base. NoSwimStaminaDrain zeroes the two
    /// fields for the call and puts them back after, so every modifier scales zero, UseStamina(0)
    /// returns at once, and nothing about the player is changed outside the call. The swim skill
    /// still rises, since that runs on its own timer in the same method.
    ///
    /// REGEN. No drain is not enough on its own: Player.UpdateStats sets stamina regeneration to
    /// zero while you are swimming off the ground, so jumping in with an empty bar still drowns
    /// you (out of stamina in water costs 5% of max health a second). SwimStaminaRegen runs the
    /// same regeneration vanilla runs on land, in an UpdateStats postfix, only in that case:
    /// swimming, not touching the bottom, and none of the other things that stop regeneration
    /// (attacking, dodging, wall running, over-encumbered). It uses the vanilla formula, regen
    /// delay and status effect multipliers, so food and Rested work as normal. With the drain
    /// left on, it only helps while you are floating still, since every drain resets the delay.
    ///
    /// Both only run for the local player, so this is per-player and the server doesn't need it.
    /// Riding a swimming lox uses the lox's own stamina (Sadle.m_swimStaminaDrain) and is left
    /// vanilla.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class SwimStaminaPatches
    {
        [HarmonyPatch(typeof(Player), "OnSwimming")]
        private static class Player_OnSwimming_Patch
        {
            private static void Prefix(Player __instance, out Vector2 __state)
            {
                __state = new Vector2(__instance.m_swimStaminaDrainMinSkill, __instance.m_swimStaminaDrainMaxSkill);
                if (Plugin.NoSwimStaminaDrain.Value)
                {
                    __instance.m_swimStaminaDrainMinSkill = 0f;
                    __instance.m_swimStaminaDrainMaxSkill = 0f;
                }
            }

            private static void Postfix(Player __instance, Vector2 __state)
            {
                __instance.m_swimStaminaDrainMinSkill = __state.x;
                __instance.m_swimStaminaDrainMaxSkill = __state.y;
            }
        }

        [HarmonyPatch(typeof(Player), "UpdateStats", typeof(float))]
        private static class Player_UpdateStats_Patch
        {
            private static void Postfix(Player __instance, float dt)
            {
                if (!Plugin.SwimStaminaRegen.Value || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner())
                {
                    return;
                }
                // Vanilla regenerates in every other case already; this covers only the one it skips.
                if (!__instance.IsSwimming() || __instance.IsOnGround() || __instance.InAttack()
                    || __instance.InDodge() || __instance.m_wallRunning || __instance.IsEncumbered())
                {
                    return;
                }

                float maxStamina = __instance.GetMaxStamina();
                if (__instance.m_stamina >= maxStamina || __instance.m_staminaRegenTimer > 0f)
                {
                    return;
                }

                float regen = __instance.m_staminaRegen
                    + (1f - __instance.m_stamina / maxStamina) * __instance.m_staminaRegen * __instance.m_staminaRegenTimeMultiplier;
                float multiplier = 1f;
                __instance.m_seman.ModifyStaminaRegen(ref multiplier);
                regen *= multiplier;

                __instance.m_stamina = Mathf.Min(maxStamina, __instance.m_stamina + regen * dt * Game.m_staminaRegenRate);
                __instance.m_nview.GetZDO().Set(ZDOVars.s_stamina, __instance.m_stamina);
            }
        }
    }
}
