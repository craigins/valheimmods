using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Fall damage goes down as the jump skill goes up.
    ///
    /// Character.UpdateGroundContact, on landing, only for players and only past a 4 m drop,
    /// works out Clamp01((drop - 4) / 16) * 100 - so 100 damage at 20 m and above - and passes it
    /// through SEMan.ModifyFallDamage so status effects (the feather cape, Slow fall) can change it.
    /// A postfix on that call scales whatever is left by
    /// 1 - jump skill factor * FallDamageReductionAtMaxJump, so with the default of 1 a skill of 50
    /// halves it and 100 takes no fall damage at all. The skill factor is level / 100 and includes
    /// skill bonuses from gear and food, as vanilla's jump height does.
    ///
    /// UpdateGroundContact runs on the player's own game, so this is per-player and the server
    /// doesn't need it.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class FallDamagePatches
    {
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyFallDamage))]
        private static class SEMan_ModifyFallDamage_Patch
        {
            private static void Postfix(SEMan __instance, ref float damage)
            {
                float reduction = Plugin.FallDamageReductionAtMaxJump.Value;
                if (reduction <= 0f || damage <= 0f || !(__instance.m_character is Player player))
                {
                    return;
                }
                float skill = Mathf.Clamp01(player.GetSkillFactor(Skills.SkillType.Jump));
                damage *= Mathf.Clamp01(1f - skill * reduction);
            }
        }
    }
}
