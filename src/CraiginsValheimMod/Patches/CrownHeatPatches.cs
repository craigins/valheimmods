using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Wearing the Crown of Valheim stops Ashlands heat hurting you.
    ///
    /// Character.Update builds up two heat levels each frame: UpdateLava (standing in or near lava,
    /// and running about in the Ashlands by day) and UpdateAshlandsWater (being in the hot Ashlands
    /// sea). UpdateHeatDamage then, every 0.2 s, deals AshlandsLava or AshlandsOcean damage once
    /// either reaches the threshold, and in lava sets you burning. The prefix, for a player
    /// wearing HelmetCrownofValheim, zeroes both levels and skips that method, so none of it
    /// lands. Zeroing rather than just skipping matters: the heat would otherwise keep building
    /// under the crown and hit all at once the moment it came off. It also turns off the heat
    /// haze, which is driven by the same two levels.
    ///
    /// Fire damage from attacks, fire and the Burning effect from other sources are separate and
    /// left alone, and the lava still slows you down.
    ///
    /// UpdateHeatDamage only runs on whoever's game owns the character, so this is per-player and
    /// the server doesn't need it.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class CrownHeatPatches
    {
        private const string CrownPrefab = "HelmetCrownofValheim";

        [HarmonyPatch(typeof(Character), "UpdateHeatDamage")]
        private static class Character_UpdateHeatDamage_Patch
        {
            private static bool Prefix(Character __instance)
            {
                if (!Plugin.CrownOfValheimHeatImmunity.Value || !(__instance is Player player))
                {
                    return true;
                }
                ItemDrop.ItemData helmet = player.m_helmetItem;
                if (helmet == null || helmet.m_dropPrefab == null || helmet.m_dropPrefab.name != CrownPrefab)
                {
                    return true;
                }
                player.m_lavaHeatLevel = 0f;
                player.m_ashlandsOceanHeatLevel = 0f;
                return false;
            }
        }
    }
}
