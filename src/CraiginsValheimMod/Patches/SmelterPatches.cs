using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Lets the blast furnace smelt everything the regular smelter does, so a base needs only one.
    ///
    /// What a smelter accepts is nothing but its Smelter.m_conversion list (from/to item pairs):
    /// IsItemAllowed, FindCookableItem, the hover text's item list and the output lookup all read it,
    /// and the queue in the ZDO holds item names that are resolved through it. So appending the
    /// smelter's pairs to the blast furnace's list is the whole change - fuel, speed and capacity stay
    /// the blast furnace's own.
    ///
    /// The lists are serialized on the prefabs, so they're edited there, once, as soon as ZNetScene
    /// has indexed its prefabs, and every furnace instantiated afterwards gets the longer list.
    /// Nothing is hardcoded beyond the two prefab names: whatever the smelter converts in this game
    /// version is what's copied.
    ///
    /// Which machine's list matters: the player loading ore checks their own copy before sending
    /// RPC_AddOre, and the furnace's owner - whichever nearby client the server handed it to -
    /// checks again and does the smelting. So every client needs this on. So does a dedicated
    /// server: its reference position stays at the origin, so it instantiates - and, with nobody
    /// nearby, owns - furnaces near the world centre. Without the pairs it would keep consuming the
    /// queued ore while Smelter.Spawn finds no conversion and makes nothing.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class SmelterPatches
    {
        private const string SmelterPrefab = "smelter";
        private const string BlastFurnacePrefab = "blastfurnace";

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetScene_Awake_Patch
        {
            private static void Postfix(ZNetScene __instance)
            {
                if (!Plugin.BlastFurnaceSmeltsAll.Value)
                {
                    return;
                }

                Smelter smelter = FindSmelter(__instance, SmelterPrefab);
                Smelter furnace = FindSmelter(__instance, BlastFurnacePrefab);
                if (smelter == null || furnace == null)
                {
                    return;
                }

                // Prefabs outlive the scene, so this runs again on every world join; the name check
                // keeps it from adding the same pairs twice.
                var accepted = new HashSet<string>();
                foreach (Smelter.ItemConversion conversion in furnace.m_conversion)
                {
                    if (conversion.m_from != null)
                    {
                        accepted.Add(conversion.m_from.gameObject.name);
                    }
                }

                var added = new List<string>();
                foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
                {
                    if (conversion.m_from == null || conversion.m_to == null ||
                        !accepted.Add(conversion.m_from.gameObject.name))
                    {
                        continue;
                    }
                    furnace.m_conversion.Add(new Smelter.ItemConversion
                    {
                        m_from = conversion.m_from,
                        m_to = conversion.m_to,
                    });
                    added.Add(conversion.m_from.gameObject.name);
                }

                if (added.Count > 0)
                {
                    Jotunn.Logger.LogInfo($"BlastFurnaceSmeltsAll: blast furnace now also takes {string.Join(", ", added.ToArray())}");
                }
            }
        }

        private static Smelter FindSmelter(ZNetScene scene, string prefabName)
        {
            GameObject prefab = scene.GetPrefab(prefabName);
            Smelter smelter = prefab != null ? prefab.GetComponent<Smelter>() : null;
            if (smelter == null)
            {
                Jotunn.Logger.LogWarning($"BlastFurnaceSmeltsAll: no Smelter prefab named '{prefabName}' - leaving the blast furnace as it is.");
            }
            return smelter;
        }
    }
}
