using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Ported from my older ValheimNoMist mod (ValheimNoMistMain.cs).
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

        /// <summary>
        /// Replaces vanilla's health check so biome, cultivated ground, heat/cold, roof and
        /// spacing never hold a plant back.
        ///
        /// Vanilla UpdateHealth is not only a status check, though. For plants that attach to a
        /// building piece (m_attachDistance > 0: VineAsh_sapling and VineGreen_sapling, both 1.8)
        /// its last step is
        ///     GetClosestAttachPosRot(out m_attachPos, out m_attachRot, out m_attachNormal)
        /// and those three fields are where Grow() then spawns the vine. Skipping vanilla
        /// outright left them zeroed, so after the 200-300 s grow time the vine was created at
        /// the world origin - where Vine.CheckSupport found no wall and deleted it - while the
        /// sapling was destroyed as usual. The plant just vanished. So the wall lookup is kept:
        /// a vine has nowhere to grow without one, and with m_destroyIfCantGrow off the sapling
        /// simply waits, showing "needs a wall", until there is one.
        /// </summary>
        [HarmonyPatch(typeof(Plant), nameof(Plant.UpdateHealth))]
        private static class PlantUpdateHealth_Patch
        {
            private static bool Prefix(Plant __instance, double timeSincePlanted)
            {
                if (!Plugin.PlantAnywhere.Value)
                {
                    return true;
                }

                // Same 10 s grace vanilla gives a fresh plant, before it runs any check.
                if (timeSincePlanted >= 10.0
                    && __instance.m_attachDistance > 0f
                    && !__instance.GetClosestAttachPosRot(
                        out __instance.m_attachPos, out __instance.m_attachRot, out __instance.m_attachNormal))
                {
                    __instance.m_status = Plant.Status.NoAttachPiece;
                    return false;
                }

                __instance.m_status = Plant.Status.Healthy;
                return false;
            }
        }

        /// <summary>
        /// Holds food at its full benefit for the whole duration, instead of vanilla's steady
        /// decline, so a meal is worth the same in its last minute as its first.
        ///
        /// Vanilla decays food in Player.UpdateFood, once per second - not in
        /// GetTotalFoodValue:
        ///     food.m_time -= 1f;
        ///     float t = Mathf.Pow(Mathf.Clamp01(food.m_time / m_shared.m_foodBurnTime), 0.3f);
        ///     food.m_health  = m_shared.m_food        * t;
        ///     food.m_stamina = m_shared.m_foodStamina * t;
        ///     food.m_eitr    = m_shared.m_foodEitr    * t;
        /// GetTotalFoodValue then just sums those already-decayed per-food values onto
        /// m_baseHP/m_baseStamina. So the decay lives in Food.m_health/m_stamina/m_eitr, and
        /// reading those fields gets you the decayed number.
        ///
        /// That's the trap the first version of this patch fell into: it replaced
        /// GetTotalFoodValue with a line-for-line copy of vanilla that summed food.m_health
        /// etc., which changed nothing at all (and assigned rather than accumulated eitr, so
        /// only the last food in the list counted). Summing the *shared item definition*
        /// values instead - m_shared.m_food / m_foodStamina / m_foodEitr, the undecayed
        /// originals - is what actually holds the benefit flat.
        ///
        /// Food still expires: UpdateFood keeps counting m_time down and drops the food from
        /// m_foods when it runs out, at which point its contribution disappears in one step.
        /// The HUD bar reads m_time directly, so it drains normally either way.
        /// </summary>
        [HarmonyPatch(typeof(Player), nameof(Player.GetTotalFoodValue))]
        private static class PlayerGetTotalFoodValue_Patch
        {
            private static bool Prefix(Player __instance, out float hp, out float stamina, out float eitr)
            {
                if (!Plugin.NoFoodDecay.Value)
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
                    // Expired but not yet pulled from the list - contributes nothing.
                    if (food.m_time <= 0f)
                    {
                        continue;
                    }

                    ItemDrop.ItemData.SharedData shared = food.m_item.m_shared;
                    hp += shared.m_food;
                    stamina += shared.m_foodStamina;
                    eitr += shared.m_foodEitr;
                }
                return false;
            }
        }
    }
}
