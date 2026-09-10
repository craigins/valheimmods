using HarmonyLib;

namespace CraiginsValheimMod.Stargate
{
    /// <summary>
    /// Replaces the two things a player touches on a portal - its hover text and its E interaction -
    /// for gates only. Walking through a gate is left entirely to vanilla TeleportWorld.Teleport,
    /// which follows the Portal connection the server wrote.
    ///
    ///   [E]          dial: a text prompt for an address
    ///   [Shift+E]    disconnect (either end of a link can do it)
    ///
    /// The prompt is vanilla's own TextInput panel, the one portal tags and signs use - a
    /// placeholder until there's a proper dialer, but one that already works on keyboard and gamepad.
    ///
    /// Both actions need access to the gate's ward, the same check vanilla puts on changing a tag.
    /// Being dialed does not: a gate can always be reached. (An iris would be the thing to change that.)
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class StargatePatches
    {
        [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.GetHoverText))]
        private static class TeleportWorld_GetHoverText_Patch
        {
            private static bool Prefix(TeleportWorld __instance, ref string __result)
            {
                ZDO gate = StargatePiece.GateZdo(__instance);
                if (gate == null)
                {
                    return true;
                }
                __result = HoverText(gate);
                return false;
            }
        }

        [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Interact))]
        private static class TeleportWorld_Interact_Patch
        {
            private static bool Prefix(TeleportWorld __instance, Humanoid human, bool hold, bool alt, ref bool __result)
            {
                ZDO gate = StargatePiece.GateZdo(__instance);
                if (gate == null)
                {
                    return true;
                }

                __result = Interact(__instance, gate, human, hold, alt);
                return false;
            }
        }

        private static string HoverText(ZDO gate)
        {
            string text = "Stargate  " + StargateAddress.Format(StargateAddress.Of(gate));

            ZDOID partnerId = gate.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
            bool connected = !partnerId.IsNone();
            if (connected)
            {
                // The partner is usually far away, so its ZDO is often not on this client yet.
                // TeleportWorld.TargetFound requests it every half second while the gate is loaded,
                // so the address fills in on its own a moment later.
                ZDO partner = ZDOMan.instance.GetZDO(partnerId);
                text += partner != null
                    ? "\nConnected to " + StargateAddress.Format(StargateAddress.Of(partner))
                    : "\nConnected";
            }
            else
            {
                text += "\nIdle";
            }

            text += "\n[<color=yellow><b>$KEY_Use</b></color>] Dial";
            if (connected)
            {
                // Same key-name split vanilla uses for alt-interactions (see Tameable.GetHoverText).
                string altKey = ZInput.IsNonClassicFunctionality() && ZInput.IsGamepadActive() ? "$KEY_AltKeys" : "$KEY_AltPlace";
                text += "\n[<color=yellow><b>" + altKey + " + $KEY_Use</b></color>] Disconnect";
            }
            return Localization.instance.Localize(text);
        }

        private static bool Interact(TeleportWorld portal, ZDO gate, Humanoid human, bool hold, bool alt)
        {
            if (hold)
            {
                return false;
            }
            if (!PrivateArea.CheckAccess(portal.transform.position))
            {
                human.Message(MessageHud.MessageType.Center, "$piece_noaccess");
                return true;
            }

            if (alt)
            {
                if (gate.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal).IsNone())
                {
                    human.Message(MessageHud.MessageType.Center, "This gate isn't connected.");
                    return true;
                }
                StargateNetwork.Close(gate.m_uid);
                return true;
            }

            // Room for the dash: "ABC-DEF".
            TextInput.instance.RequestText(new DialPrompt(gate.m_uid), "Dial a gate address", StargateAddress.Length + 1);
            return true;
        }

        /// <summary>
        /// Receives the address typed into the TextInput panel. Holds the gate's ZDOID rather than the
        /// TeleportWorld, since the panel can outlive the gate's GameObject (walk away while typing).
        /// </summary>
        private class DialPrompt : TextReceiver
        {
            private readonly ZDOID _gate;

            public DialPrompt(ZDOID gate)
            {
                _gate = gate;
            }

            public string GetText()
            {
                return "";
            }

            public void SetText(string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                string code;
                if (!StargateAddress.TryParse(text, out code))
                {
                    if (Player.m_localPlayer != null)
                    {
                        Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                            $"'{text}' isn't a gate address - six symbols, like ABC-DEF.");
                    }
                    return;
                }
                StargateNetwork.Dial(_gate, code);
            }
        }
    }
}
