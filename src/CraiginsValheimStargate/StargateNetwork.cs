using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Stargate
{
    /// <summary>
    /// Dialing and disconnecting, which have to happen on the server.
    ///
    /// Only the server can dial: it's the only peer holding every gate's ZDO (a client sees just the
    /// sectors around it), so it's the only one that can resolve an address to a gate - and the gate
    /// being dialed is usually nowhere near anyone. A client sends its gate's ZDOID and the address it
    /// typed, and the server does the lookup and the linking.
    ///
    /// THE RULE. Links have no timeout. Dialing A -> B first drops A's existing link and B's existing
    /// link (each partner is disconnected too, so nothing is left pointing at a gate that no longer
    /// points back), then links A and B. Disconnecting from either end drops both ends. So everything
    /// is a ZDO write the server can make whether or not either gate is loaded anywhere, and there is
    /// no per-gate state beyond vanilla's own Portal connection.
    ///
    /// Links are written through Game.ForceSetConnection - vanilla's own path, the one its pairing loop
    /// uses to settle a connection: take ownership, set the connection, ForceSendZDO so nearby clients
    /// see it at once. Taking ownership of a piece's ZDO is harmless, and vanilla does it to every
    /// portal it pairs.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class StargateNetwork
    {
        private const string RpcDial = "CVM_StargateDial";
        private const string RpcClose = "CVM_StargateClose";
        private const string RpcList = "CVM_StargateList";
        private const string RpcMessage = "CVM_StargateMessage";
        private const string RpcConsole = "CVM_StargateConsole";

        // The instance registered on, not a bool: ZNet builds a fresh ZRoutedRpc for every world
        // joined, so a flag set by the first world would leave the second without handlers.
        private static ZRoutedRpc _registeredOn;

        [HarmonyPatch(typeof(Game), "Start")]
        private static class Game_Start_Patch
        {
            private static void Postfix()
            {
                ZRoutedRpc rpc = ZRoutedRpc.instance;
                if (rpc == null || rpc == _registeredOn)
                {
                    return;
                }
                rpc.Register<ZDOID, string>(RpcDial, OnDial);
                rpc.Register<ZDOID>(RpcClose, OnClose);
                rpc.Register(RpcList, OnList);
                rpc.Register<string>(RpcMessage, OnMessage);
                rpc.Register<string>(RpcConsole, OnConsole);
                _registeredOn = rpc;
            }
        }

        // ---- client side ------------------------------------------------------------------

        // The single-argument-list overload targets the server. On a host, where we ARE the server,
        // InvokeRoutedRPC delivers to its own handler directly - so one path serves both.

        public static void Dial(ZDOID gate, string code)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcDial, gate, code);
        }

        public static void Close(ZDOID gate)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcClose, gate);
        }

        public static void RequestList()
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcList);
        }

        private static void OnMessage(long sender, string message)
        {
            if (Player.m_localPlayer != null)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
            }
            Jotunn.Logger.LogInfo(message);
        }

        private static void OnConsole(long sender, string message)
        {
            if (Console.instance != null)
            {
                Console.instance.Print(message);
            }
            Jotunn.Logger.LogInfo(message);
        }

        // ---- server side ------------------------------------------------------------------

        private static bool IsServer
        {
            get { return ZNet.instance != null && ZNet.instance.IsServer(); }
        }

        private static void OnDial(long sender, ZDOID gateId, string rawCode)
        {
            if (!IsServer)
            {
                return;
            }

            ZDO source = ZDOMan.instance.GetZDO(gateId);
            if (!StargatePiece.IsGate(source))
            {
                Reply(sender, "That gate isn't there any more.");
                return;
            }

            string code;
            if (!StargateAddress.TryParse(rawCode, out code))
            {
                Reply(sender, $"'{rawCode}' isn't a gate address.");
                return;
            }
            string shown = StargateAddress.Format(code);

            List<ZDO> matches = FindByAddress(code);
            if (matches.Count == 0)
            {
                Reply(sender, $"No gate answers at {shown}.");
                return;
            }
            if (matches.Count > 1)
            {
                // Two gates hashing to one address. Refusing is better than a coin toss, and the fix
                // is to move one of them.
                Jotunn.Logger.LogWarning($"Stargate address {shown} is shared by {matches.Count} gates - refusing to dial it.");
                Reply(sender, $"{shown} is ambiguous - more than one gate has it.");
                return;
            }

            ZDO target = matches[0];
            if (target == source)
            {
                Reply(sender, "A gate can't dial itself.");
                return;
            }
            if (Partner(source) == target.m_uid)
            {
                Reply(sender, $"Already connected to {shown}.");
                return;
            }

            Unlink(source);
            ZDO displaced = Unlink(target);
            Link(source, target.m_uid);
            Link(target, source.m_uid);

            string from = StargateAddress.Format(StargateAddress.Of(source));
            Jotunn.Logger.LogInfo($"Stargate {from} dialed {shown}" +
                                  (displaced != null ? $", dropping {shown}'s link to {StargateAddress.Format(StargateAddress.Of(displaced))}." : "."));
            Reply(sender, $"Connected to {shown}.");
        }

        private static void OnClose(long sender, ZDOID gateId)
        {
            if (!IsServer)
            {
                return;
            }

            ZDO gate = ZDOMan.instance.GetZDO(gateId);
            if (!StargatePiece.IsGate(gate))
            {
                Reply(sender, "That gate isn't there any more.");
                return;
            }
            if (Partner(gate).IsNone())
            {
                Reply(sender, "This gate isn't connected.");
                return;
            }

            ZDO partner = Unlink(gate);
            Reply(sender, partner != null
                ? $"Disconnected from {StargateAddress.Format(StargateAddress.Of(partner))}."
                : "Disconnected.");
        }

        private static void OnList(long sender)
        {
            if (!IsServer)
            {
                return;
            }

            List<ZDO> gates = StargatePiece.AllGates();
            if (gates.Count == 0)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcConsole, "stargate: no gates in this world.");
                return;
            }

            var entries = new List<string>();
            foreach (ZDO gate in gates)
            {
                Vector3 pos = gate.GetPosition();
                ZDOID partnerId = Partner(gate);
                ZDO partner = partnerId.IsNone() ? null : ZDOMan.instance.GetZDO(partnerId);
                string link = partnerId.IsNone() ? "idle"
                    : partner != null ? "-> " + StargateAddress.Format(StargateAddress.Of(partner))
                    : "-> (missing gate)";
                entries.Add(string.Format(CultureInfo.InvariantCulture, "{0}  at {1:0}, {2:0}, {3:0}  {4}",
                    StargateAddress.Format(StargateAddress.Of(gate)), pos.x, pos.y, pos.z, link));
            }
            entries.Sort(System.StringComparer.Ordinal);

            var text = new StringBuilder();
            text.Append($"stargate: {gates.Count} gate(s):");
            foreach (string entry in entries)
            {
                text.Append("\n  ").Append(entry);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcConsole, text.ToString());
        }

        private static List<ZDO> FindByAddress(string code)
        {
            var matches = new List<ZDO>();
            foreach (ZDO gate in StargatePiece.AllGates())
            {
                if (StargateAddress.Of(gate) == code)
                {
                    matches.Add(gate);
                }
            }
            return matches;
        }

        private static ZDOID Partner(ZDO gate)
        {
            return gate.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
        }

        /// <summary>
        /// Drops the gate's link, and its partner's link back to it. Returns the former partner, or
        /// null if there wasn't one (or it no longer exists).
        /// </summary>
        private static ZDO Unlink(ZDO gate)
        {
            ZDOID partnerId = Partner(gate);
            if (partnerId.IsNone())
            {
                return null;
            }

            Link(gate, ZDOID.None);

            ZDO partner = ZDOMan.instance.GetZDO(partnerId);
            if (partner != null && Partner(partner) == gate.m_uid)
            {
                Link(partner, ZDOID.None);
            }
            return partner;
        }

        private static void Link(ZDO gate, ZDOID target)
        {
            Game.instance.ForceSetConnection(gate, target);
        }

        private static void Reply(long peer, string message)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcMessage, message);
        }
    }
}
