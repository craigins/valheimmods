using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Stargate
{
    /// <summary>
    /// Dialing from a sign: Shift+E on a sign that carries a gate address dials it on the nearest
    /// gate, so an address book can be a wall of signs next to the gate.
    ///
    ///   sign text      "Misty3 S58-ELN"
    ///   [Shift+E]      the gate within SignDialRange of the sign dials S58-ELN
    ///
    /// READING THE SIGN. The text is split at the first space and the second part is the address;
    /// the first part is the name and is ignored. Signs are often coloured, so every rich text
    /// tag - anything between angle brackets, wherever it sits - is removed before the split:
    /// "&lt;color=red&gt;Misty3&lt;/color&gt; &lt;#00ff00&gt;S58-ELN" reads the same as the plain text. That
    /// also covers a tag with a space inside it, which would otherwise be taken for the split.
    /// If the second part holds more than the address ("Misty3 S58-ELN north side"), its first
    /// word is tried. The text is the raw string from the sign's ZDO, not the widget: the widget
    /// may have been censored or replaced with runes by the platform's content rules.
    ///
    /// A sign whose second part is not an address is left entirely to vanilla, so Shift+E on an
    /// ordinary sign still opens it for editing, as it does today. Plain E always edits.
    ///
    /// FINDING THE GATE. The nearest gate within range of the *sign*, not of the player, since
    /// the sign is the thing that was placed beside a gate. It is looked up among the objects
    /// this client has instantiated (`ZNetScene.m_instances`), so the gate is one the player can
    /// actually see. The portal list is not used: a client's copy is only as good as the ZDOs it
    /// has been sent, and an instantiated gate is the stronger guarantee.
    ///
    /// DIALING is the existing `CVM_StargateDial` RPC, unchanged - the sign is just another way
    /// of typing the address. So the server needs nothing new, and everything the server already
    /// does on a dial applies: an incoming connection drops the target's old link, the gate
    /// cannot dial itself, an unknown address is refused. Like dialing at the gate, it needs
    /// access to the gate's ward. Access to the sign's ward is not needed; reading is free.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class StargateSignPatches
    {
        private static readonly Regex RichTextTag = new Regex("<[^<>]*>", RegexOptions.Compiled);

        // The hover text is asked for every frame. The address is cheap to read, but finding a
        // gate walks every instantiated object, so that answer is kept for a moment.
        private const float GateLookupLifetime = 1f;
        private static Sign _lookupSign;
        private static float _lookupTime;
        private static ZDO _lookupGate;

        /// <summary>The address on a sign, or false if its second part isn't one.</summary>
        internal static bool TryReadAddress(string signText, out string code)
        {
            code = null;
            if (string.IsNullOrEmpty(signText))
            {
                return false;
            }

            string text = RichTextTag.Replace(signText, "").Trim();
            int split = IndexOfWhiteSpace(text);
            if (split < 0)
            {
                return false;
            }

            string second = text.Substring(split + 1).Trim();
            if (StargateAddress.TryParse(second, out code))
            {
                return true;
            }

            int end = IndexOfWhiteSpace(second);
            return end > 0 && StargateAddress.TryParse(second.Substring(0, end), out code);
        }

        private static int IndexOfWhiteSpace(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    return i;
                }
            }
            return -1;
        }

        private static string RawText(Sign sign)
        {
            ZDO zdo = sign.m_nview != null ? sign.m_nview.GetZDO() : null;
            return zdo != null ? zdo.GetString(ZDOVars.s_text, sign.m_defaultText) : null;
        }

        /// <summary>The nearest instantiated gate within range of the point, or null.</summary>
        private static ZDO NearestGate(Vector3 from, float range)
        {
            if (ZNetScene.instance == null)
            {
                return null;
            }

            ZDO nearest = null;
            float nearestDistance = range;
            foreach (KeyValuePair<ZDO, ZNetView> instance in ZNetScene.instance.m_instances)
            {
                if (!StargatePiece.IsGate(instance.Key) || instance.Value == null)
                {
                    continue;
                }
                float distance = Vector3.Distance(instance.Key.GetPosition(), from);
                if (distance <= nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = instance.Key;
                }
            }
            return nearest;
        }

        private static ZDO NearestGateCached(Sign sign)
        {
            if (_lookupSign != sign || Time.time - _lookupTime > GateLookupLifetime)
            {
                _lookupSign = sign;
                _lookupTime = Time.time;
                _lookupGate = NearestGate(sign.transform.position, StargatePlugin.SignDialRange.Value);
            }
            return _lookupGate;
        }

        [HarmonyPatch(typeof(Sign), nameof(Sign.Interact))]
        private static class Sign_Interact_Patch
        {
            private static bool Prefix(Sign __instance, Humanoid character, bool hold, bool alt, ref bool __result)
            {
                if (!alt || hold || !StargatePlugin.SignDial.Value || character != Player.m_localPlayer)
                {
                    return true;
                }

                string code;
                if (!TryReadAddress(RawText(__instance), out code))
                {
                    return true;
                }

                float range = StargatePlugin.SignDialRange.Value;
                ZDO gate = NearestGate(__instance.transform.position, range);
                if (gate == null)
                {
                    character.Message(MessageHud.MessageType.Center, $"No stargate within {range:0} m of this sign.");
                    __result = false;
                    return false;
                }
                if (!PrivateArea.CheckAccess(gate.GetPosition()))
                {
                    character.Message(MessageHud.MessageType.Center, "$piece_noaccess");
                    __result = true;
                    return false;
                }

                StargateNetwork.Dial(gate.m_uid, code);
                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(Sign), nameof(Sign.GetHoverText))]
        private static class Sign_GetHoverText_Patch
        {
            private static void Postfix(Sign __instance, ref string __result)
            {
                if (!StargatePlugin.SignDial.Value)
                {
                    return;
                }

                string code;
                if (!TryReadAddress(RawText(__instance), out code) || NearestGateCached(__instance) == null)
                {
                    return;
                }

                string altKey = ZInput.IsNonClassicFunctionality() && ZInput.IsGamepadActive() ? "$KEY_AltKeys" : "$KEY_AltPlace";
                __result += Localization.instance.Localize(
                    "\n[<color=yellow><b>" + altKey + " + $KEY_Use</b></color>] Dial " + StargateAddress.Format(code));
            }
        }
    }
}
