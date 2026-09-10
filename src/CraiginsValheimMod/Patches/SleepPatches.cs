using System.Reflection;
using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// "Sleep anywhere/anytime" cluster, ported from my older ValheimNoMist mod
    /// (ValheimNoMistMain.cs). All patch targets verified against the current assembly_valheim.dll before porting -
    /// no changes needed, ported as-is.
    /// </summary>
    internal static class SleepPatches
    {
        [HarmonyPatch(typeof(Game), nameof(Game.UpdateSleeping))]
        private static class GameUpdateSleeping_Patch
        {
            private static readonly MethodInfo EverybodyIsTryingToSleep =
                typeof(Game).GetMethod("EverybodyIsTryingToSleep", BindingFlags.NonPublic | BindingFlags.Instance);

            private static void Postfix(Game __instance)
            {
                if (!Plugin.SleepAnyways.Value || !ZNet.instance.IsServer())
                {
                    return;
                }

                // This postfix re-implements vanilla's trigger with the "is it night" gate
                // removed, so every *other* guard vanilla has must be copied here too or we'd
                // skip to morning in a situation the game deliberately refuses to.
                //
                // The m_lastSleepTime cooldown is the one that bites. On the tick a skip ends,
                // vanilla sets m_sleeping = false and sends SleepStop - and this postfix runs
                // straight after, in the same call, while every player's ZDO still says in-bed
                // because the SleepStop hasn't reached them yet. Without the cooldown that
                // re-triggers immediately and skips a whole extra day (it's already morning),
                // doubling the time everyone spends on the black screen.
                //
                // The CinematicsManager check mirrors one Valheim 1.0 added to UpdateSleeping.
                if (!__instance.m_sleeping
                    && !EnvMan.instance.IsTimeSkipping()
                    && !CinematicsManager.IsPlaying()
                    && ZNet.instance.GetTimeSeconds() - __instance.m_lastSleepTime >= 10.0
                    && (bool)EverybodyIsTryingToSleep.Invoke(__instance, null))
                {
                    EnvMan.instance.SkipToMorning();
                    __instance.m_sleeping = true;
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "SleepStart");
                }
            }
        }

        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.CanSleep))]
        private static class EnvManCanSleep_Patch
        {
            private static void Postfix(ref bool __result)
            {
                if (Plugin.SleepAnyways.Value)
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(Bed), nameof(Bed.CheckEnemies))]
        private static class BedCheckEnemies_Patch
        {
            private static void Postfix(ref bool __result)
            {
                if (Plugin.SleepAnyways.Value)
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(Bed), nameof(Bed.CheckExposure))]
        private static class BedCheckExposure_Patch
        {
            private static void Postfix(ref bool __result)
            {
                if (Plugin.SleepAnyways.Value)
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(Bed), nameof(Bed.CheckFire))]
        private static class BedCheckFire_Patch
        {
            private static void Postfix(ref bool __result)
            {
                if (Plugin.SleepAnyways.Value)
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(Bed), nameof(Bed.CheckWet))]
        private static class BedCheckWet_Patch
        {
            private static void Postfix(ref bool __result)
            {
                if (Plugin.SleepAnyways.Value)
                {
                    __result = true;
                }
            }
        }
    }
}
