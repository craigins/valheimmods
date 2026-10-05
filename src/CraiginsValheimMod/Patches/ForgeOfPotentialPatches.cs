using System.Collections.Generic;
using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Upgrading at the Forge of Potential always succeeds.
    ///
    /// The forge is a CraftingStation with m_upgrader set, and InventoryGui.DoCrafting rolls the
    /// upgrade there. It takes the recipe's first resource marked m_upgraderResource and draws
    /// r = Random.Range(0, 1): success (level + 1) if that item's m_upgradeChance >= r, otherwise
    /// the item is destroyed (half the recoverable materials back, m_breakReturnIngreientsAmount)
    /// if m_breakChance >= 1 - r, otherwise it drops a level. The class defaults are 0.65 and 0.1.
    ///
    /// Those chances live on the resource item's shared data, which every copy of that item shares,
    /// so the prefix sets the upgrader resources' m_upgradeChance to 1 and m_breakChance to 0 for
    /// this one call only and the finalizer puts the old values back. Random.Range(0, 1) never
    /// returns more than 1, so every roll succeeds, with the usual success message and effects.
    /// Nothing else reads these fields.
    ///
    /// Runs on the crafting player's own game, so it is per-player and the server doesn't need it.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class ForgeOfPotentialPatches
    {
        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        private static class InventoryGui_DoCrafting_Patch
        {
            private static void Prefix(InventoryGui __instance, Player player,
                out List<(ItemDrop.ItemData.SharedData shared, float upgrade, float breakChance)> __state)
            {
                __state = null;
                if (!Plugin.ForgeOfPotentialAlwaysSucceeds.Value || __instance.m_craftRecipe == null)
                {
                    return;
                }
                CraftingStation station = player.GetCurrentCraftingStation();
                if (station == null || !station.m_upgrader)
                {
                    return;
                }
                foreach (Piece.Requirement requirement in __instance.m_craftRecipe.m_resources)
                {
                    if (!requirement.m_upgraderResource || requirement.m_resItem == null)
                    {
                        continue;
                    }
                    ItemDrop.ItemData.SharedData shared = requirement.m_resItem.m_itemData.m_shared;
                    __state ??= new List<(ItemDrop.ItemData.SharedData, float, float)>();
                    __state.Add((shared, shared.m_upgradeChance, shared.m_breakChance));
                    shared.m_upgradeChance = 1f;
                    shared.m_breakChance = 0f;
                }
            }

            private static void Finalizer(List<(ItemDrop.ItemData.SharedData shared, float upgrade, float breakChance)> __state)
            {
                if (__state == null)
                {
                    return;
                }
                // Backwards, so an item listed twice ends on its original values.
                for (int i = __state.Count - 1; i >= 0; i--)
                {
                    __state[i].shared.m_upgradeChance = __state[i].upgrade;
                    __state[i].shared.m_breakChance = __state[i].breakChance;
                }
            }
        }
    }
}
