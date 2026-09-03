using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Ported from your older ValheimNoMist mod
    /// (H:\Users\craig_000\source\repos\ValheimNoMist\ValheimNoMist\ValheimNoMistMain.cs).
    /// Skipped from that file (not ported):
    ///   - TeleportAll (Humanoid.IsTeleportable) - vanilla Valheim already allows this now.
    ///   - SpawnSystem.IsSpawnPointGood patch - only ever printed debug logging, no actual
    ///     gameplay effect; not worth carrying forward.
    ///   - WearNTear.GetMinSupport (NoSupportRequired) - already commented out in the original,
    ///     an abandoned experiment, not a working feature.
    /// EverythingFloats (the other commented-out one, WaterVolume.OnTriggerEnter) is handled
    /// separately in BuoyancyPatches.cs - that one never worked, so it's a fresh implementation
    /// rather than a port. See the comment there for why.
    /// All patch targets below were verified against the current assembly_valheim.dll before
    /// porting.
    /// </summary>
    internal static class QualityOfLifePatches
    {
        [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.CheckUsable))]
        private static class CraftingStationCheckUsable_Patch
        {
            private static bool Prefix(ref bool __result)
            {
                if (!Plugin.CraftAnywhere.Value)
                {
                    return true;
                }

                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.HardDeath))]
        private static class PlayerHardDeath_Patch
        {
            private static void Postfix(ref bool __result)
            {
                if (Plugin.NoDeathPenalty.Value)
                {
                    __result = false;
                }
            }
        }

        [HarmonyPatch(typeof(RandEventSystem), nameof(RandEventSystem.GetPossibleRandomEvents))]
        private static class RandEventSystemGetPossibleRandomEvents_Patch
        {
            private static readonly List<KeyValuePair<RandomEvent, Vector3>> Empty = new List<KeyValuePair<RandomEvent, Vector3>>();

            private static bool Prefix(ref List<KeyValuePair<RandomEvent, Vector3>> __result)
            {
                if (!Plugin.DisableRandomEvents.Value)
                {
                    return true;
                }

                __result = Empty;
                return false;
            }
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.OnPlaced))]
        private static class WearNTearOnPlaced_Patch
        {
            private static void Postfix(WearNTear __instance)
            {
                if (Plugin.NoRainDamage.Value)
                {
                    __instance.m_noRoofWear = false;
                }
            }
        }

        [HarmonyPatch(typeof(Plant), nameof(Plant.HaveGrowSpace))]
        private static class PlantHaveGrowSpace_Patch
        {
            private static void Postfix(ref bool __result)
            {
                if (Plugin.PlantAnywhere.Value)
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(Plant), nameof(Plant.HaveRoof))]
        private static class PlantHaveRoof_Patch
        {
            private static void Postfix(ref bool __result)
            {
                if (Plugin.PlantAnywhere.Value)
                {
                    __result = false;
                }
            }
        }

        [HarmonyPatch(typeof(Plant), nameof(Plant.UpdateHealth))]
        private static class PlantUpdateHealth_Patch
        {
            private static bool Prefix(Plant __instance)
            {
                if (!Plugin.PlantAnywhere.Value)
                {
                    return true;
                }

                __instance.m_status = Plant.Status.Healthy;
                return false;
            }
        }

        /// <summary>
        /// In the old code this unconditionally replaced Player.GetTotalFoodValue, reading
        /// each active food's *shared item definition* values (food.m_item.m_shared.m_food /
        /// m_foodStamina / m_foodEitr) and summing them as HP/stamina/eitr.
        ///
        /// The game's Player.Food struct has since grown its own m_health/m_stamina/m_eitr
        /// fields directly - which strongly suggests vanilla Valheim now does roughly this
        /// same summation itself. That makes this patch's original purpose likely obsolete,
        /// and replacing the whole method also throws away whatever the current vanilla
        /// implementation does with food freshness/decay (Food.m_time) that this patch never
        /// accounted for. Ported for parity with the old field names, but left OFF by default
        /// (SimplifiedFoodTotals config) - flip it on and compare against vanilla in-game if
        /// you still want this behavior.
        /// </summary>
        [HarmonyPatch(typeof(Player), nameof(Player.GetTotalFoodValue))]
        private static class PlayerGetTotalFoodValue_Patch
        {
            private static bool Prefix(Player __instance, out float hp, out float stamina, out float eitr)
            {
                if (!Plugin.SimplifiedFoodTotals.Value)
                {
                    hp = 0f;
                    stamina = 0f;
                    eitr = 0f;
                    return true;
                }

                hp = __instance.m_baseHP;
                stamina = __instance.m_baseStamina;
                eitr = 0f;
                foreach (Player.Food food in __instance.m_foods)
                {
                    hp += food.m_health;
                    stamina += food.m_stamina;
                    eitr = food.m_eitr;
                }
                return false;
            }
        }
    }
}
