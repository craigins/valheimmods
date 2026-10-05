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
    /// NO IDOLS. At the forge, Player.HaveRequirementItems and ConsumeResources skip every
    /// requirement whose m_upgraderResource doesn't match the station's m_upgrader, so a forge
    /// refinement costs only the recipe's idol entries (Upgrader0-7 Weapon/Armor) and nothing
    /// else. Their count comes from Piece.Requirement.GetAmount, which for an upgrader resource is
    /// m_amount plus m_amountPerLevel scaled by the level. A postfix returns 0 for upgrader
    /// resources, so the check passes with none in the bag, nothing is taken, and the requirement
    /// list hides the line (it only shows entries with an amount above 0). Refining is then free.
    /// DoCrafting still finds the idol entry in the recipe itself for its chances, which doesn't
    /// need the item in your inventory.
    ///
    /// NO CAP. There is no level cap to lift: DoCrafting, RequiredCraftingStation and the recipe
    /// list all exempt an m_upgrader station from m_maxQuality and station level, so the rising
    /// idol cost was the only limit, and quality is sent as a ushort.
    ///
    /// Runs on the crafting player's own game, so it is per-player and the server doesn't need it.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class ForgeOfPotentialPatches
    {
        [HarmonyPatch(typeof(Piece.Requirement), nameof(Piece.Requirement.GetAmount))]
        private static class Requirement_GetAmount_Patch
        {
            private static void Postfix(Piece.Requirement __instance, ref int __result)
            {
                if (__instance.m_upgraderResource && Plugin.ForgeOfPotentialNoIdols.Value)
                {
                    __result = 0;
                }
            }
        }

        // A class, not a tuple: the game's runtime has no System.ValueTuple, and a tuple in a
        // patch signature makes Harmony's PatchAll throw, which stops every patch in the mod.
        private sealed class SavedChances
        {
            public ItemDrop.ItemData.SharedData Shared;
            public float Upgrade;
            public float Break;
        }

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        private static class InventoryGui_DoCrafting_Patch
        {
            private static void Prefix(InventoryGui __instance, Player player,
                out List<SavedChances> __state)
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
                    __state ??= new List<SavedChances>();
                    __state.Add(new SavedChances { Shared = shared, Upgrade = shared.m_upgradeChance, Break = shared.m_breakChance });
                    shared.m_upgradeChance = 1f;
                    shared.m_breakChance = 0f;
                }
            }

            private static void Finalizer(List<SavedChances> __state)
            {
                if (__state == null)
                {
                    return;
                }
                // Backwards, so an item listed twice ends on its original values.
                for (int i = __state.Count - 1; i >= 0; i--)
                {
                    __state[i].Shared.m_upgradeChance = __state[i].Upgrade;
                    __state[i].Shared.m_breakChance = __state[i].Break;
                }
            }
        }
    }
}
