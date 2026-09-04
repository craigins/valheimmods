using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// "Everything floats" - redesigned from scratch, not ported. My old ValheimNoMist
    /// attempt (ValheimNoMistMain.cs, the commented-out
    /// WaterVolumeOnTriggerEnter_Patch/EverythingFloats toggle) never worked
    /// because of how it was built, not because the idea was wrong:
    ///
    ///   1. Whether an item floats is baked into its prefab (does it have a Floating component
    ///      or not) - nothing in ItemDrop's own code adds one at runtime. Ore/metal is shipped
    ///      without Floating on purpose (Valheim wants you to ship/cart it). The old patch
    ///      tried to attach Floating reactively, the moment something touched a WaterVolume
    ///      trigger - fragile and mistimed instead of just fixing the prefab once.
    ///   2. WaterVolume.OnTriggerEnter only fires for colliders whose Rigidbody makes Unity
    ///      register the trigger - objects with no Rigidbody never call it at all, so
    ///      "everything" was structurally unreachable that way regardless of anything else.
    ///   3. For objects it could reach, it searched up to 5 parents for a ZNetView to borrow
    ///      (Floating.FixedUpdate gates all buoyancy on m_nview.IsOwner()) instead of using the
    ///      object's own - either redundant (a standalone item already has its own ZNetView at
    ///      the top of that search) or silently failing (nothing found within 5 levels).
    ///
    /// This version instead adds Floating in a Postfix on ItemDrop.Awake, on the same
    /// GameObject, after ItemDrop has already set up its own Rigidbody/ZNetView/collider. That
    /// means Floating.Awake()'s own GetComponent&lt;Rigidbody&gt;()/GetComponent&lt;ZNetView&gt;()
    /// calls just work - no manual field patching, no parent walking, no timing races. Verified
    /// against the current assembly_valheim.dll: ItemDrop.Awake always sets up m_nview when a
    /// Rigidbody is present, matching what Floating expects.
    /// </summary>
    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.Awake))]
    internal static class ItemDropAwake_Patch
    {
        private static void Postfix(ItemDrop __instance)
        {
            if (!Plugin.EverythingFloats.Value)
            {
                return;
            }

            // Floating needs a Rigidbody on the same GameObject (its own Awake reads
            // GetComponent<Rigidbody>()) - skip anything that doesn't have one rather than
            // adding a component that would silently do nothing.
            if (__instance.GetComponent<Rigidbody>() == null)
            {
                return;
            }

            if (__instance.GetComponent<Floating>() != null)
            {
                return;
            }

            __instance.gameObject.AddComponent<Floating>();
        }
    }
}
