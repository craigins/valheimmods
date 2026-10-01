using System.Collections.Generic;
using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Faster berry bush regrowth, and bushes that cannot be destroyed.
    ///
    /// Every berry bush in game 1.0.16 - BlueberryBush, RaspberryBush, CloudberryBush and the
    /// Deep North LingonberryBush - ships m_respawnTimeMinutes 300, one berry per pick, and no
    /// m_spawnCheck. Pickable.UpdateRespawn runs every 60 s on the ZDO owner and regrows the
    /// bush once ShouldRespawn sees more than m_respawnTimeMinutes of world time since the
    /// picked time stored in the ZDO. World time only runs on a dedicated server while someone
    /// is connected, and sleeping moves it forward. Nothing nearby - pieces, wards, portals -
    /// takes part in that check.
    ///
    /// REGROWTH. The Awake prefix sets the respawn time on each bush instance as it wakes,
    /// before Awake reads it. Nothing is written to the world: the picked time is the same ZDO
    /// value vanilla keeps, so a bush picked before the mod was installed regrows on the new
    /// timer, and removing the mod puts every bush back on 300 minutes.
    ///
    /// INDESTRUCTIBLE. Each bush also carries a Destructible with 30 health, normal damage from
    /// blunt, slash and pierce and double from chop, so any swing or area attack can flatten
    /// it. Vegetation is only placed when a zone is first generated, so a destroyed bush never
    /// comes back. The RPC_Damage prefix drops every hit on a berry bush and shows "immune",
    /// as vanilla does for a hit that does no damage. Destructible.Destroy is left alone: the
    /// only callers that are not reached through RPC_Damage are console commands and the
    /// cultivator, which only destroys unhealthy Plants.
    ///
    /// Bushes are told apart by the berry they give, not the prefab name, so the check does
    /// not have to strip "(Clone)". Vines give Vineberry and are left to VinePatches.
    ///
    /// Both run on the ZDO owner, normally the nearest player, so everyone near the bushes
    /// needs the same values, and so does a dedicated server for bushes near the world centre,
    /// which it runs itself when nobody is close. The respawn time is read when a bush loads,
    /// so a change applies to bushes loaded after it.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class BerryBushPatches
    {
        private static readonly HashSet<string> Berries = new HashSet<string>
        {
            "Blueberries", "Raspberry", "Cloudberry", "Lingonberry",
        };

        private static bool IsBerryBush(Pickable pickable)
        {
            return pickable != null
                && pickable.m_respawnTimeMinutes > 0f
                && pickable.m_itemPrefab != null
                && Berries.Contains(pickable.m_itemPrefab.name);
        }

        [HarmonyPatch(typeof(Pickable), "Awake")]
        private static class Pickable_Awake_Patch
        {
            private static void Prefix(Pickable __instance)
            {
                float minutes = Plugin.BerryBushRespawnMinutes.Value;
                if (minutes > 0f && IsBerryBush(__instance))
                {
                    __instance.m_respawnTimeMinutes = minutes;
                }
            }
        }

        [HarmonyPatch(typeof(Destructible), "RPC_Damage")]
        private static class Destructible_RPC_Damage_Patch
        {
            private static bool Prefix(Destructible __instance, HitData hit)
            {
                if (!Plugin.BerryBushesIndestructible.Value
                    || !IsBerryBush(__instance.GetComponent<Pickable>()))
                {
                    return true;
                }

                DamageText.instance.ShowText(HitData.DamageModifier.Immune, hit.m_point, 0f);
                return false;
            }
        }
    }
}
