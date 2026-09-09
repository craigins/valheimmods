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

                // The CinematicsManager check mirrors one Valheim 1.0 added to UpdateSleeping
                // itself. This postfix re-implements vanilla's trigger with the "is it night"
                // gate removed, so any *other* guard vanilla grows has to be copied here too or
                // we'd skip to morning in a situation the game deliberately refuses to.
                if (!__instance.m_sleeping
                    && !EnvMan.instance.IsTimeSkipping()
                    && !CinematicsManager.IsPlaying()
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
