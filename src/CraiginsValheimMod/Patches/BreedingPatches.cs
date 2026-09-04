using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Removes the nearby-population cap on tamed animal breeding.
    ///
    /// Verified against the current assembly_valheim.dll: Procreation.Procreate() counts nearby
    /// instances of its own prefab and of its offspring prefab, both within m_totalCheckRange,
    /// and bails out once the sum reaches m_maxCreatures:
    ///
    ///     int nearby = SpawnSystem.GetNrOfInstances(m_myPrefab,        pos, m_totalCheckRange, false, false)
    ///                + SpawnSystem.GetNrOfInstances(m_offspringPrefab, pos, m_totalCheckRange, false, false);
    ///     if (nearby &gt;= m_maxCreatures) return;
    ///
    /// That's the "too many nearby" stop - a pen full of boars quietly stops producing. Raising
    /// m_maxCreatures for the duration of the call removes it without touching anything else in
    /// the breeding path.
    ///
    /// Two gates in the same method are deliberately left alone:
    ///   - Tameable.IsHungry() - animals still have to be fed to breed, which is the part that
    ///     makes breeding a decision rather than a background process.
    ///   - The m_partnerCheckRange test, which needs two adults nearby (or one, for species with
    ///     m_noPartnerOffspring). Lifting the population cap is a different thing from letting a
    ///     lone animal breed by itself, so that stays vanilla.
    ///
    /// The original value is restored afterwards rather than being overwritten permanently, so
    /// turning the config off restores vanilla behaviour immediately - including for animals
    /// that are already spawned and loaded.
    /// </summary>
    [HarmonyPatch(typeof(Procreation), nameof(Procreation.Procreate))]
    internal static class BreedingPatches
    {
        private static void Prefix(Procreation __instance, out int __state)
        {
            __state = __instance.m_maxCreatures;
            if (Plugin.UnlimitedBreeding.Value)
            {
                __instance.m_maxCreatures = int.MaxValue;
            }
        }

        // Finalizer rather than Postfix so the cap is put back even if the method throws -
        // otherwise one exception would leave that animal permanently uncapped.
        private static void Finalizer(Procreation __instance, int __state)
        {
            __instance.m_maxCreatures = __state;
        }
    }
}
