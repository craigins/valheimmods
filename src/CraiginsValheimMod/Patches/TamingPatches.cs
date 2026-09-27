using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Faster taming.
    ///
    /// Every tameable prefab in 1.0.16 ships the same numbers: m_tamingTime 1800 s (30 min),
    /// m_fedDuration 600 s. Tameable.TamingUpdate runs every 3 s and, if the animal is fed,
    /// not alerted and not yet tame, calls DecreaseRemainingTime(3f). The time left lives in
    /// the ZDO (s_tameTimeLeft, defaulting to m_tamingTime), so it survives unloads and saves.
    ///
    /// The prefix scales that 3 s tick. Scaling the tick rather than m_tamingTime means:
    ///   - an animal already part-way through taming keeps the share of progress it has, since
    ///     its stored time left is simply worked off faster;
    ///   - the hover text's percentage, which divides time left by m_tamingTime, stays correct;
    ///   - removing the mod leaves nothing odd in the world - only ordinary time-left values.
    ///
    /// Vanilla's own boost is applied inside the method, after this: each player within
    /// m_tamingSpeedMultiplierRange (60 m) who has the TamingBoost attribute multiplies the
    /// tick by m_tamingBoostMultiplier (2). The only source is the tamer mead (Potion_tamer,
    /// 600 s). So the two stack: x3 here plus one mead is x6.
    ///
    /// The gates are left alone - the animal still has to be fed and calm, and still has to be
    /// loaded, which means somebody nearby. TamingUpdate only runs on the ZDO owner, normally
    /// the nearest player, so whoever is near the pen needs the mod, and so does a dedicated
    /// server for a pen near the world centre, which it runs itself when nobody is close.
    /// </summary>
    [HarmonyPatch(typeof(Tameable), nameof(Tameable.DecreaseRemainingTime))]
    internal static class TamingPatches
    {
        private static void Prefix(ref float time)
        {
            float factor = Plugin.TamingSpeedMultiplier.Value;
            if (factor > 0f)
            {
                time *= factor;
            }
        }
    }
}
