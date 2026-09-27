using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Faster health regeneration while Resting - for getting back to full health after a
    /// respawn without waiting through minutes of food ticks.
    ///
    /// Two effects have similar names and only one is touched here:
    ///   - "Resting" (SE_Cozy): the state you are in while near a fire and either sitting or
    ///     under shelter, with nothing hostile aware of you and not cold, wet or burning
    ///     (Player.UpdateEnvStatusEffects). Prefab values: health regen x3, stamina x4, eitr x4.
    ///     It is removed the moment those conditions stop holding.
    ///   - "Rested" (SE_Rested): the long buff Resting grants after 20 s, which you carry
    ///     around afterwards (health x1.5, stamina x2, eitr x2). Deliberately left vanilla, so
    ///     this cannot speed up healing in a fight.
    ///
    /// How vanilla heals: Player.UpdateFood, every 10 s, sums m_foodRegen over the foods eaten,
    /// asks SEMan.ModifyHealthRegen for a multiplier and heals by the product. With no food
    /// eaten the sum is zero and nothing heals, whatever the multiplier.
    ///
    /// SE_Stats.ModifyHealthRegen is additive above 1 (mult += m - 1), so Resting contributes
    /// +2 and Rested +0.5: x3 with Resting alone, x3.5 with both. This postfix runs after every
    /// status effect has had its say and scales the finished multiplier, so "2" means the heal
    /// per tick is exactly double what vanilla would have given in the same situation, whatever
    /// else is active.
    ///
    /// SEMan.ModifyHealthRegen has one caller, Player.UpdateFood, which only runs for the local
    /// player - so this is per-player and needs nothing on the server. Stamina and eitr regen
    /// go through their own Modify* methods and are not changed.
    /// </summary>
    [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyHealthRegen))]
    internal static class RestingPatches
    {
        private static void Postfix(SEMan __instance, ref float regenMultiplier)
        {
            float factor = Plugin.RestingHealthRegenMultiplier.Value;
            if (factor == 1f || factor < 0f)
            {
                return;
            }

            if (__instance.HaveStatusEffect(SEMan.s_statusEffectResting))
            {
                regenMultiplier *= factor;
            }
        }
    }
}
