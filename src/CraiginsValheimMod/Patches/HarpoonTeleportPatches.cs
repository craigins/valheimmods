using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Brings whatever you have on the harpoon through a teleport with you, instead of leaving it
    /// behind and snapping the line.
    ///
    /// WHERE THE LINK LIVES. The harpoon is `SE_Harpooned`, a StatusEffect attached to the
    /// *creature*, whose `m_attacker` points back at the puller. It's applied in `Character.Damage`
    /// from the weapon's `m_statusEffectHash`, and `RPC_Damage` returns early unless
    /// `m_nview.IsOwner()` - so the effect object only exists on the machine that owns the creature.
    /// Nothing about it is written to the ZDO. That's workable because `ZDOMan.ReleaseZDOS` hands
    /// each peer ownership of the ZDOs in its own active area every 2s, and a harpooned creature is
    /// by definition within `m_maxDistance` (30m) of the puller, so it's almost always ours. When it
    /// isn't - another player standing closer owns it - the effect isn't on this machine, we find
    /// nothing, and the harpoon behaves exactly as vanilla does today.
    ///
    /// WHY UpdateTeleport AND NOT TeleportTo. `Player.TeleportTo` only *starts* a teleport: it sets
    /// `m_teleporting` and the target, then returns. The move itself happens in
    /// `Player.UpdateTeleport` two seconds later, and that method may re-position the player several
    /// more times while it waits for the destination to load (up to 15s), including a final y-snap
    /// onto solid ground and a bail-out back to the origin when the exit is blocked. Following the
    /// player from a postfix here covers every one of those without having to know which branch ran.
    ///
    /// HOW THE CREATURE MOVES. Base `Character.TeleportTo` is a stub returning false - only Player
    /// overrides it - so a creature is moved the way vanilla moves the player during the intro carry
    /// (`SyncPlayer`): set the transform, set the Rigidbody position, then `ZSyncTransform.SyncNow()`,
    /// which runs `OwnerSync` and publishes the position to the ZDO immediately. Other clients snap
    /// rather than interpolate past 5m (`ZSyncTransform.SyncPosition`), so a cross-map jump reads as
    /// a clean snap and not a slide across the world.
    ///
    /// The creature is translated by the same delta as the player, so the distance between them is
    /// unchanged and `SE_Harpooned`'s own break test - `distance - m_baseDistance > m_breakDistance`
    /// - never fires. That's deliberately all this does: no clobbering of the effect's pull baseline,
    /// so dragging behaves the same after a teleport as before it.
    ///
    /// Harpooned *players* are skipped: a player's character is owned by the person playing it, and
    /// yanking someone else's body from this client would fight their own movement. Bosses can't be
    /// harpooned at all - `SE_Harpooned.SetAttacker` breaks the line immediately for them.
    /// </summary>
    internal static class HarpoonTeleportPatches
    {
        private struct TeleportState
        {
            public bool Teleporting;
            public Vector3 From;
        }

        [HarmonyPatch(typeof(Player), "UpdateTeleport")]
        private static class PlayerUpdateTeleport_Patch
        {
            private static void Prefix(Player __instance, out TeleportState __state)
            {
                __state = new TeleportState
                {
                    Teleporting = __instance.IsTeleporting(),
                    From = __instance.transform.position,
                };
            }

            private static void Postfix(Player __instance, TeleportState __state)
            {
                if (!Plugin.HarpoonTeleport.Value || !__state.Teleporting)
                {
                    return;
                }

                // Only the owning client runs a teleport, and only it can move the creature.
                if (__instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner())
                {
                    return;
                }

                Vector3 delta = __instance.transform.position - __state.From;
                if (delta.sqrMagnitude < 0.0001f)
                {
                    return;
                }

                Character harpooned = FindHarpooned(__instance);
                if (harpooned != null)
                {
                    Move(harpooned, delta);
                }
            }
        }

        /// <summary>The creature this player currently has on the harpoon, or null.</summary>
        private static Character FindHarpooned(Player player)
        {
            foreach (Character character in Character.GetAllCharacters())
            {
                if (character == null || character.IsPlayer() || character.m_seman == null)
                {
                    continue;
                }

                foreach (StatusEffect effect in character.m_seman.GetStatusEffects())
                {
                    SE_Harpooned harpoon = effect as SE_Harpooned;
                    if (harpoon != null && harpoon.m_attacker == player)
                    {
                        return character;
                    }
                }
            }
            return null;
        }

        private static void Move(Character character, Vector3 delta)
        {
            ZNetView nview = character.m_nview;
            if (nview == null || !nview.IsValid())
            {
                return;
            }

            // Taking ownership is what makes the write authoritative; vanilla does the same to any
            // object it force-moves. Harmless even when we already own it.
            nview.ClaimOwnership();

            Vector3 target = character.transform.position + delta;
            character.transform.position = target;
            if (character.m_body != null)
            {
                character.m_body.position = target;
                character.m_body.linearVelocity = Vector3.zero;
                Physics.SyncTransforms();
            }

            ZSyncTransform sync = character.GetComponent<ZSyncTransform>();
            if (sync != null)
            {
                sync.SyncNow();
            }
            else
            {
                // No ZSyncTransform (unusual for a creature) - write the ZDO directly so the
                // position still reaches everyone else.
                nview.GetZDO().SetPosition(target);
            }
        }
    }
}
