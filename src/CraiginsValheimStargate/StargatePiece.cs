using System.Collections.Generic;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;

namespace CraiginsValheimMod.Stargate
{
    /// <summary>
    /// The gate piece, and the two patches that make vanilla treat it as a portal for everything
    /// except pairing.
    ///
    /// A gate is a clone of portal_wood, so it keeps TeleportWorld and with it the whole vanilla
    /// teleport: the walk-in trigger, the item check, the "connected" glow and sound, and the exit
    /// placement. Vanilla reads the destination from one place only - the ZDO's Portal connection
    /// (ZDOExtraData.ConnectionType.Portal) - and a gate stores its link there too. That also buys
    /// persistence for free: ZDOIDs are reassigned on every load (ZDO.Load calls m_uid.SetID), and
    /// ZDOMan's private load-time ConnectPortals re-links Portal connections by their saved hash, not
    /// by tag. So a gate link survives a restart exactly the way a portal link does.
    ///
    /// What must NOT happen is vanilla pairing. Game.ConnectPortals runs on the server every 5s,
    /// pairs unconnected portals with equal tags, and disconnects any portal whose partner's tag
    /// differs from its own. Gates carry no tag, so left alone vanilla would pair every idle gate with
    /// every untagged portal and tear down every dialed link within five seconds. Pairing reads its
    /// list from ZDOMan.GetPortalList, so gates are filtered out of that list.
    ///
    /// Why keep gates registered as portals at all, rather than as ordinary pieces vanilla ignores?
    /// Because Game.PortalPrefabHash is what puts a ZDO in ZDOMan.m_portalObjects - an always-current
    /// list of every portal in the world on the server, which is exactly the address book dialing
    /// needs. The alternative is a prefab scan over every sector in the world per dial.
    /// </summary>
    internal static class StargatePiece
    {
        public const string PrefabName = "CVM_Stargate";
        private const string BasePrefab = "portal_wood";

        public static readonly int PrefabHash = PrefabName.GetStableHashCode();

        public static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable += AddPiece;
        }

        private static void AddPiece()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= AddPiece;

            // Requirements and Category are left unset on purpose: PieceConfig.Apply only overwrites
            // them when set, so the gate inherits portal_wood's recipe and hammer category as-is
            // rather than a guess at them.
            var config = new PieceConfig
            {
                Name = "Stargate",
                Description = "Has an address of its own. Dial another gate's address to link the two; " +
                              "the link holds until either end disconnects or another gate dials in.",
                PieceTable = PieceTables.Hammer,
            };

            var piece = new CustomPiece(PrefabName, BasePrefab, config);
            if (piece.PiecePrefab == null)
            {
                Jotunn.Logger.LogError($"Could not clone '{BasePrefab}' - no stargate piece this session.");
                return;
            }

            TeleportWorld portal = piece.PiecePrefab.GetComponent<TeleportWorld>();
            if (portal == null)
            {
                Jotunn.Logger.LogError($"'{BasePrefab}' has no TeleportWorld any more - no stargate piece this session.");
                return;
            }
            portal.m_allowAllItems = StargatePlugin.AllowAllItems.Value;

            PieceManager.Instance.AddPiece(piece);
        }

        public static bool IsGate(ZDO zdo)
        {
            return zdo != null && zdo.GetPrefab() == PrefabHash;
        }

        /// <summary>The gate's ZDO, or null if this portal isn't a gate (or isn't networked yet).</summary>
        public static ZDO GateZdo(TeleportWorld portal)
        {
            if (portal == null || portal.m_nview == null)
            {
                return null;
            }
            ZDO zdo = portal.m_nview.GetZDO();
            return IsGate(zdo) ? zdo : null;
        }

        /// <summary>
        /// Every gate in the world. Complete only on the server - a client's ZDOMan holds just the
        /// sectors near it. Reads GetPortals rather than GetPortalList, which this plugin filters.
        /// </summary>
        public static List<ZDO> AllGates()
        {
            var gates = new List<ZDO>();
            if (ZDOMan.instance == null)
            {
                return gates;
            }
            foreach (List<ZDO> sector in ZDOMan.instance.GetPortals().Values)
            {
                foreach (ZDO zdo in sector)
                {
                    if (IsGate(zdo))
                    {
                        gates.Add(zdo);
                    }
                }
            }
            return gates;
        }

        /// <summary>
        /// Game.Awake builds PortalPrefabHash from its serialized m_portalPrefabs list. It runs before
        /// ZNet.Start loads the world (all Awakes precede any Start in the scene), so the hash is in
        /// place before ZDOMan.Load sorts saved ZDOs into the portal list - on clients too, before any
        /// ZDO arrives from the server.
        /// </summary>
        [HarmonyPatch(typeof(Game), "Awake")]
        private static class Game_Awake_Patch
        {
            private static void Postfix(Game __instance)
            {
                if (!__instance.PortalPrefabHash.Contains(PrefabHash))
                {
                    __instance.PortalPrefabHash.Add(PrefabHash);
                }
            }
        }

        /// <summary>
        /// Keeps gates out of vanilla tag pairing. The only callers are Game.ConnectPortals (the 5s
        /// pairing loop) and ZDOMan.ConvertPortals (a one-off fixup of pre-connection-format worlds,
        /// keyed on tags) - neither should ever touch a gate.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.GetPortalList))]
        private static class ZDOMan_GetPortalList_Patch
        {
            private static void Postfix(List<ZDO> __result)
            {
                __result.RemoveAll(IsGate);
            }
        }
    }
}
