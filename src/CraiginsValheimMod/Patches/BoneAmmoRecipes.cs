using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Extra recipes for black metal bolts and black metal missiles made from 1 core wood and
    /// 5 bone fragments.
    ///
    /// Each one is a copy of the vanilla recipe with only the ingredients swapped, so it is
    /// crafted at the same station and gives the same item in the same amount. In game 1.0.16
    /// those are:
    ///   Recipe_BoltBlackmetal - BoltBlackmetal x20 at the black forge, level 2
    ///                           (2 BlackMetal, 8 Wood, 2 Feathers)
    ///   Recipe_TurretBolt     - TurretBolt x20 at the artisan table, level 1
    ///                           (10 Wood, 1 BlackMetal)
    /// Copying rather than hard-coding keeps them following any change a game update makes.
    ///
    /// The copies sit beside the vanilla recipes, so both show in the crafting list. The game
    /// tracks known recipes by the item they make, not by recipe, so learning either one
    /// unlocks both - and since a player learns a recipe once they have seen all its
    /// ingredients and own its station, these unlock as soon as you have core wood, bone
    /// fragments and the station, without ever having held black metal. Nothing is saved to
    /// the world or the character, so removing the mod just removes the recipes.
    ///
    /// Crafting is checked only by the crafting player, so only they need this.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class BoneAmmoRecipes
    {
        private static readonly Dictionary<string, string> Copies = new Dictionary<string, string>
        {
            { "Recipe_BoltBlackmetal", "Recipe_BoltBlackmetal_Bone" },
            { "Recipe_TurretBolt", "Recipe_TurretBolt_Bone" },
        };

        public static void Register()
        {
            ItemManager.OnItemsRegistered += Add;
        }

        private static void Add()
        {
            if (!Plugin.BoneBlackMetalAmmo.Value)
            {
                return;
            }
            ObjectDB db = ObjectDB.instance;
            ItemDrop coreWood = GetItem(db, "RoundLog");
            ItemDrop bone = GetItem(db, "BoneFragments");
            if (coreWood == null || bone == null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> copy in Copies)
            {
                if (db.m_recipes.Exists(r => r != null && r.name == copy.Value))
                {
                    continue;
                }
                Recipe original = db.m_recipes.Find(r => r != null && r.name == copy.Key);
                if (original == null)
                {
                    Jotunn.Logger.LogWarning($"{copy.Key} not found; {copy.Value} not added");
                    continue;
                }
                Recipe recipe = Object.Instantiate(original);
                recipe.name = copy.Value;
                recipe.m_resources = new[]
                {
                    new Piece.Requirement { m_resItem = coreWood, m_amount = 1, m_recover = true },
                    new Piece.Requirement { m_resItem = bone, m_amount = 5, m_recover = true },
                };
                db.m_recipes.Add(recipe);
            }
        }

        private static ItemDrop GetItem(ObjectDB db, string prefabName)
        {
            GameObject prefab = db.GetItemPrefab(prefabName);
            ItemDrop item = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (item == null)
            {
                Jotunn.Logger.LogWarning($"{prefabName} not found; bone ammo recipes not added");
            }
            return item;
        }
    }
}
