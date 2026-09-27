using System;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Lets every segment of a vineberry vine, and of ivy, grow berries, by removing the two
    /// adjacency rules in `Vine.CheckBerryBlocker`.
    ///
    /// WHAT VANILLA DOES. A vine is a grid of segments 1.5 m apart (`m_size`), each with its own
    /// Pickable: 3 vineberries, a 200 minute respawn, checked every 60 s by the segment's owner.
    /// Once the time is up, Pickable asks `m_spawnCheck`, which Vine wires to `CheckBerryBlocker`.
    /// That passes only if
    ///   - fewer than `m_maxBerriesWithinBlocker` other segments inside the BerryBlocker box have
    ///     berries. The box is 0.44 x 9.11 x 4.86 m against the wall face, so another fruiting
    ///     segment blocks within about 3 m sideways and 5 m up or down. VineAsh allows 1;
    ///     VineGreen (ivy) allows 0, which is why ivy never fruits at all; and
    ///   - the segment knows of two or more neighbours (`GetBranches() > 0`). That knowledge is
    ///     `m_vineState`, which is NonSerialized and never written to the ZDO, so it is lost on
    ///     every reload and relearned slowly - and "below" never, since `m_growDown` is off.
    /// A failed check calls `SetPicked(true)`, which restarts the 200 minutes.
    ///
    /// WHAT THIS DOES. The prefix answers the check itself, so neither rule applies and the timer
    /// is never restarted by a failure. The respawn time is untouched: each segment fruits once
    /// per 200 minutes, all of them independently.
    ///
    /// ONE RULE IS KEPT, in a different form. In vanilla a segment's first check comes within a
    /// minute of sprouting (a fresh Pickable backdates its picked time by a random amount far
    /// longer than the respawn time) and always fails, because a new segment has one neighbour at
    /// most - so its first berries are a full respawn time away. Without that failure, every new
    /// segment would sprout with berries on it. So a segment younger than its respawn time is
    /// refused, read from the plant time the vine already keeps in its ZDO. It is refused without
    /// `SetPicked`, so nothing is restarted and it fruits as soon as it is old enough.
    ///
    /// IVY. With its limit of zero lifted, ivy fruits like a vineberry vine: the Pickable on the
    /// VineGreen prefab is fully set up - 3 vineberries, with ivy seeds as the bonus drop - and
    /// the prefab has its own "Berries" child carrying the vineberry mesh. But its
    /// `m_hideWhenPicked` points at the Berries object of the *VineAsh prefab*, not its own, so
    /// fruiting ivy would show nothing and would switch the other prefab's object on and off
    /// instead. The ZNetScene postfix repoints any vine prefab whose `m_hideWhenPicked` lies
    /// outside its own hierarchy to its own child of the same name. That is done whatever the
    /// settings are: it changes nothing visible in vanilla, where ivy's berries are never shown.
    ///
    /// Ivy is told from vineberry by the data, not by prefab name: a vine whose limit is zero is
    /// one the game never lets fruit.
    ///
    /// The check runs on the segment's owner - normally the nearest player - so everyone who
    /// spends time near the vines needs the same settings. A player without the mod who owns a
    /// segment applies the vanilla rules to it, and sees no berries on ivy.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class VinePatches
    {
        private static bool IgnoresAdjacency(Vine vine)
        {
            return vine.m_maxBerriesWithinBlocker <= 0
                ? Plugin.IvyIgnoresAdjacency.Value
                : Plugin.VineberryIgnoresAdjacency.Value;
        }

        /// <summary>Whether this segment sprouted less than one respawn time ago.</summary>
        private static bool IsTooYoung(ZNetView nview, Pickable pickable)
        {
            ZDO zdo = nview.GetZDO();
            if (zdo == null || ZNet.instance == null)
            {
                return false;
            }

            long planted = zdo.GetLong(ZDOVars.s_plantTime, 0L);
            if (planted <= 0L)
            {
                return false;
            }

            TimeSpan age = ZNet.instance.GetTime() - new DateTime(planted);
            return age.TotalMinutes < pickable.m_respawnTimeMinutes;
        }

        [HarmonyPatch(typeof(Vine), "CheckBerryBlocker")]
        private static class VineCheckBerryBlocker_Patch
        {
            private static bool Prefix(Vine __instance, ref bool __result)
            {
                if (!IgnoresAdjacency(__instance))
                {
                    return true;
                }

                ZNetView nview = __instance.m_nview;
                Pickable pickable = __instance.m_pickable;
                if (pickable == null)
                {
                    return true;
                }

                // Vanilla's own two early outs: not ours to decide, or already has berries.
                if (!nview || !nview.IsOwner() || pickable.CanBePicked())
                {
                    __result = true;
                    return false;
                }

                // Vanilla recounts the neighbours here, and the growth code reads that count.
                __instance.UpdateBranches();

                __result = !IsTooYoung(nview, pickable);
                return false;
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetScene_Awake_Patch
        {
            private static void Postfix(ZNetScene __instance)
            {
                foreach (GameObject prefab in __instance.m_prefabs)
                {
                    if (prefab == null || prefab.GetComponent<Vine>() == null)
                    {
                        continue;
                    }

                    Pickable pickable = prefab.GetComponent<Pickable>();
                    GameObject shown = pickable != null ? pickable.m_hideWhenPicked : null;
                    if (shown == null || shown.transform.IsChildOf(prefab.transform))
                    {
                        continue;
                    }

                    Transform own = prefab.transform.Find(shown.name);
                    if (own == null)
                    {
                        Jotunn.Logger.LogWarning(
                            $"Vines: {prefab.name} shows its berries on an object outside the prefab " +
                            $"('{shown.name}') and has no child of that name - left as it is.");
                        continue;
                    }

                    pickable.m_hideWhenPicked = own.gameObject;
                    Jotunn.Logger.LogInfo($"Vines: {prefab.name} now shows its own '{own.name}' when it has berries.");
                }
            }
        }
    }
}
