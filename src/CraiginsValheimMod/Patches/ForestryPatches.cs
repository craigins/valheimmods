using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Moves tree seeds off the tree and onto its stump: felling a tree no longer drops seeds,
    /// and destroying the stump it leaves behind always drops one. Forestry stays sustainable,
    /// but you have to clear the stumps to keep it that way.
    ///
    /// How the vanilla paths work (all verified against the current assembly_valheim.dll):
    ///   - TreeBase.RPC_Damage, on the hit that takes the tree to 0 health, calls SpawnLog()
    ///     and then m_dropWhenDestroyed.GetDropList(), instantiating one item per list entry.
    ///   - TreeBase.SpawnLog() instantiates m_logPrefab and m_stubPrefab - so the tree prefab
    ///     itself names the stump it leaves, which is what the stump/seed mapping below is
    ///     built from.
    ///   - TreeLog.Destroy does the same GetDropList dance for the felled log.
    ///   - Stumps drop via DropOnDestroyed.OnDestroyed, which Awake() hooks onto
    ///     Destructible.m_onDestroyed. It too calls m_dropWhenDestroyed.GetDropList().
    ///
    /// Since all four paths funnel through DropTable.GetDropList(), one postfix there does the
    /// work; the patches on the callers just record which table is being rolled and why. The
    /// recorded value is the DropTable itself rather than a bare "in a tree" flag, so even if a
    /// marker were somehow left set it could only affect that one object's table, never an
    /// unrelated chest or creature drop.
    ///
    /// Note the stump seed is *added*, not rolled: DropTable.GetDropList(int) returns an empty
    /// list outright when Random.value beats m_dropChance, so a rolled seed routinely fails to
    /// appear at all. Wood amounts, stack sizes and Game.m_resourceRate scaling are untouched.
    ///
    /// Nothing here is hardcoded to prefab names. Seeds are whatever the game's own sapling
    /// pieces are planted from (any prefab with a Plant component, via its Piece.m_resources),
    /// and each stump is matched to a seed its own species actually dropped. A species whose
    /// stump can't be resolved keeps dropping seeds from the tree exactly as vanilla does, so
    /// this can never remove a seed from the game without putting it back somewhere.
    /// </summary>
    internal static class ForestryPatches
    {
        /// <summary>Stump prefab name -> the seed item that species used to drop.</summary>
        private static Dictionary<string, GameObject> _stumpSeeds;

        /// <summary>The seed items being redirected, i.e. the ones to strip from tree/log drops.</summary>
        private static HashSet<GameObject> _redirectedSeeds;

        private static bool _scanned;

        // Which drop table is mid-roll, and what to do with the result.
        private static DropTable _treeTable;
        private static DropTable _stumpTable;
        private static GameObject _stumpSeed;

        [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.RPC_Damage))]
        private static class TreeBaseRPCDamage_Patch
        {
            private static void Prefix(TreeBase __instance)
            {
                _treeTable = __instance.m_dropWhenDestroyed;
            }

            private static void Finalizer()
            {
                _treeTable = null;
            }
        }

        [HarmonyPatch(typeof(TreeLog), nameof(TreeLog.Destroy))]
        private static class TreeLogDestroy_Patch
        {
            private static void Prefix(TreeLog __instance)
            {
                _treeTable = __instance.m_dropWhenDestroyed;
            }

            private static void Finalizer()
            {
                _treeTable = null;
            }
        }

        [HarmonyPatch(typeof(DropOnDestroyed), nameof(DropOnDestroyed.OnDestroyed))]
        private static class DropOnDestroyedOnDestroyed_Patch
        {
            private static void Prefix(DropOnDestroyed __instance)
            {
                if (!Plugin.SeedsFromStumps.Value)
                {
                    return;
                }

                GameObject seed;
                if (StumpSeeds().TryGetValue(Utils.GetPrefabName(__instance.gameObject), out seed))
                {
                    _stumpTable = __instance.m_dropWhenDestroyed;
                    _stumpSeed = seed;
                }
            }

            private static void Finalizer()
            {
                _stumpTable = null;
                _stumpSeed = null;
            }
        }

        [HarmonyPatch(typeof(DropTable), nameof(DropTable.GetDropList), new Type[0])]
        private static class DropTableGetDropList_Patch
        {
            private static void Postfix(DropTable __instance, List<GameObject> __result)
            {
                if (!Plugin.SeedsFromStumps.Value || __instance == null || __result == null)
                {
                    return;
                }

                if (__instance == _stumpTable && _stumpSeed != null)
                {
                    if (!__result.Contains(_stumpSeed))
                    {
                        __result.Add(_stumpSeed);
                    }
                    return;
                }

                if (__instance == _treeTable)
                {
                    HashSet<GameObject> redirected = _redirectedSeeds;
                    if (redirected != null && redirected.Count > 0)
                    {
                        __result.RemoveAll(item => item != null && redirected.Contains(item));
                    }
                }
            }
        }

        /// <summary>
        /// Stump prefab name -> seed, built once from ZNetScene's prefab list. Returns an empty
        /// map (without caching) if called before the scene is populated, so it retries rather
        /// than caching nothing.
        /// </summary>
        private static Dictionary<string, GameObject> StumpSeeds()
        {
            if (_scanned)
            {
                return _stumpSeeds;
            }

            ZNetScene scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null)
            {
                return EmptyMap;
            }

            HashSet<string> seedItems = FindPlantableItems(scene);
            if (seedItems.Count == 0)
            {
                // Scene is up but nothing plantable was found - don't cache, in case this ran
                // before the prefab list was fully registered.
                return EmptyMap;
            }

            Dictionary<string, GameObject> stumps = new Dictionary<string, GameObject>();
            HashSet<GameObject> redirected = new HashSet<GameObject>();
            List<string> unmatched = new List<string>();

            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null)
                {
                    continue;
                }

                TreeBase tree = prefab.GetComponent<TreeBase>();
                if (tree == null || tree.m_stubPrefab == null)
                {
                    continue;
                }

                // The seed this species drops today - from the tree itself, or failing that
                // from the log it leaves behind.
                GameObject seed = FirstSeedIn(tree.m_dropWhenDestroyed, seedItems);
                if (seed == null && tree.m_logPrefab != null)
                {
                    TreeLog log = tree.m_logPrefab.GetComponent<TreeLog>();
                    if (log != null)
                    {
                        seed = FirstSeedIn(log.m_dropWhenDestroyed, seedItems);
                    }
                }

                if (seed == null)
                {
                    continue;
                }

                string stumpName = Utils.GetPrefabName(tree.m_stubPrefab);

                // No drop component means OnDestroyed never runs for this stump, so a redirected
                // seed would have nowhere to come from - leave that species on vanilla instead.
                if (tree.m_stubPrefab.GetComponent<DropOnDestroyed>() == null)
                {
                    if (!unmatched.Contains(stumpName))
                    {
                        unmatched.Add(stumpName);
                    }
                    continue;
                }

                if (!stumps.ContainsKey(stumpName))
                {
                    stumps[stumpName] = seed;
                }

                redirected.Add(seed);
            }

            _stumpSeeds = stumps;
            _redirectedSeeds = redirected;
            _scanned = true;

            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, GameObject> entry in stumps)
            {
                lines.Add($"{entry.Key} -> {entry.Value.name}");
            }
            lines.Sort(StringComparer.Ordinal);
            Jotunn.Logger.LogInfo(
                $"SeedsFromStumps: redirecting seeds to {stumps.Count} stump type(s): {string.Join(", ", lines.ToArray())}");
            if (unmatched.Count > 0)
            {
                Jotunn.Logger.LogWarning(
                    "SeedsFromStumps: these stumps have no DropOnDestroyed component and can't drop " +
                    $"anything, so their species keeps dropping seeds from the tree: {string.Join(", ", unmatched.ToArray())}");
            }

            return _stumpSeeds;
        }

        /// <summary>
        /// Every item the game's own sapling pieces are planted from, i.e. every tree seed.
        /// Derived from the game rather than a hardcoded list, so a new species in a future
        /// patch is picked up with no code change.
        /// </summary>
        private static HashSet<string> FindPlantableItems(ZNetScene scene)
        {
            HashSet<string> found = new HashSet<string>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null || prefab.GetComponent<Plant>() == null)
                {
                    continue;
                }

                Piece piece = prefab.GetComponent<Piece>();
                if (piece == null || piece.m_resources == null)
                {
                    continue;
                }

                foreach (Piece.Requirement requirement in piece.m_resources)
                {
                    if (requirement == null || requirement.m_resItem == null || requirement.m_amount <= 0)
                    {
                        continue;
                    }

                    found.Add(requirement.m_resItem.gameObject.name);
                }
            }

            return found;
        }

        private static GameObject FirstSeedIn(DropTable table, HashSet<string> seedItems)
        {
            if (table == null || table.m_drops == null)
            {
                return null;
            }

            foreach (DropTable.DropData drop in table.m_drops)
            {
                if (drop.m_item != null && seedItems.Contains(drop.m_item.name))
                {
                    return drop.m_item;
                }
            }

            return null;
        }

        private static readonly Dictionary<string, GameObject> EmptyMap = new Dictionary<string, GameObject>();
    }
}
