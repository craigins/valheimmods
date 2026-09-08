using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Dungeons
{
    /// <summary>
    /// Lets a player rebuild a dungeon from the outside by using an item on its entrance -
    /// a Surtling core by default, consumed on success.
    ///
    /// WHY UseItem AND NOT A KEYBIND. A dungeon entrance is a Teleport, and Teleport.UseItem is
    /// a literal `return false;` stub in vanilla - nothing in the game uses it, so there's a
    /// whole interaction verb sitting unused on exactly the object we want. The alternative,
    /// binding a key at the entrance, fights the object itself: Teleport.OnTriggerEnter calls
    /// Interact() the moment you walk into the entrance collider, so you're teleported before
    /// any "stand here and press something" scheme can fire. Using an item works from a step
    /// back, while the entrance is merely hovered, which is the only place the interaction is
    /// stable. Vanilla's own hold-E is unusable for the same reason (a quick E press teleports
    /// you first), and alt+E would silently repurpose an existing interaction.
    ///
    /// CLIENT/SERVER SPLIT. The interaction happens on the player's client; the rebuild has to
    /// happen where the world lives. So the client validates locally (right object, right item,
    /// enough of it), asks the server over a routed RPC, and the server does the authoritative
    /// checks and the work. The core is consumed only when the server reports success, so a
    /// refused reset - someone still inside, say - doesn't eat it.
    ///
    /// The server does not verify the client actually held a core: it has no view of a client's
    /// inventory, and Valheim's inventory is client-authoritative throughout. A modified client
    /// could reset for free. That's the same trust model as the rest of the game, and the
    /// alternative (admin-gating) would make the cost decorative - a deliberate choice, not an
    /// oversight.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class DungeonEntrancePatches
    {
        private const string RpcRequest = "CVM_DungeonResetRequest";
        private const string RpcResult = "CVM_DungeonResetResult";

        /// <summary>How long to wait for the server's reply before letting the player try again.</summary>
        private const float RequestTimeout = 10f;

        private static bool _registered;
        private static float _requestSentAt = float.NegativeInfinity;
        private static string _pendingItemName;

        // ---- registration ----------------------------------------------------------------

        /// <summary>
        /// ZRoutedRpc is constructed in ZNet, so registration can't happen at plugin load.
        /// Game.Start runs once the net layer is up, on both client and dedicated server.
        /// </summary>
        [HarmonyPatch(typeof(Game), "Start")]
        private static class Game_Start_Patch
        {
            private static void Postfix()
            {
                if (_registered || ZRoutedRpc.instance == null)
                {
                    return;
                }
                ZRoutedRpc.instance.Register<int, int>(RpcRequest, OnResetRequested);
                ZRoutedRpc.instance.Register<bool, string>(RpcResult, OnResetResult);
                _registered = true;
            }
        }

        // ---- the interaction -------------------------------------------------------------

        [HarmonyPatch(typeof(Teleport), nameof(Teleport.UseItem))]
        private static class Teleport_UseItem_Patch
        {
            private static bool Prefix(Teleport __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
            {
                if (!Plugin.DungeonResetFromEntrance.Value || !IsDungeonEntrance(__instance))
                {
                    return true;
                }

                string costItem = ResolveCostItemName();
                if (costItem == null || item == null || item.m_shared.m_name != costItem)
                {
                    // Not our item. Fall through to vanilla, which shows "you can't use that on this".
                    return true;
                }

                // From here the interaction is ours: report it handled so the player doesn't also
                // get vanilla's "can't use on" message on top of whatever we say.
                __result = true;
                TryRequestReset(__instance, user, item);
                return false;
            }
        }

        /// <summary>Tells the player the entrance can be rebuilt, under vanilla's "[E] Enter" line.</summary>
        [HarmonyPatch(typeof(Teleport), nameof(Teleport.GetHoverText))]
        private static class Teleport_GetHoverText_Patch
        {
            private static void Postfix(Teleport __instance, ref string __result)
            {
                if (!Plugin.DungeonResetFromEntrance.Value || !IsDungeonEntrance(__instance))
                {
                    return;
                }

                string costItem = ResolveCostItemName();
                if (costItem == null)
                {
                    return;
                }

                // A locked dungeon says so instead of advertising a cost that would be refused.
                // Clients can answer this themselves: global keys are pushed to every peer.
                if (!DungeonProgression.IsUnlocked(__instance.transform.position, out string requirement))
                {
                    __result += $"\n<color=#c0c0c0>Sealed until {requirement} falls</color>";
                    return;
                }

                int amount = Mathf.Max(1, Plugin.DungeonResetCostAmount.Value);
                string itemLabel = Localization.instance.Localize(costItem);
                string cost = amount > 1 ? $"{amount}x {itemLabel}" : itemLabel;
                __result += $"\n[<color=yellow><b>Use {cost}</b></color>] Regenerate";
            }
        }

        /// <summary>
        /// The surface end of a dungeon, as opposed to the exit standing inside one: its target
        /// is up in the interior (Character.InInterior is a bare y > 3000) and it isn't.
        /// Ordinary player-built portals have no Teleport component at all, so they never reach
        /// this; a location with no interior has no second Teleport to point at.
        /// </summary>
        private static bool IsDungeonEntrance(Teleport teleport)
        {
            return teleport != null
                && teleport.m_targetPoint != null
                && !Character.InInterior(teleport.transform.position)
                && Character.InInterior(teleport.m_targetPoint.transform.position);
        }

        /// <summary>
        /// The configured cost item's shared name (the "$item_..." token), which is what
        /// ItemDrop.ItemData comparisons and Inventory lookups use - the same way Fireplace
        /// matches its fuel. Config holds the prefab name instead because that's the stable,
        /// greppable identifier; ObjectDB translates.
        /// </summary>
        private static string ResolveCostItemName()
        {
            string prefabName = Plugin.DungeonResetCostItem.Value;
            if (string.IsNullOrEmpty(prefabName) || ObjectDB.instance == null)
            {
                return null;
            }

            if (!ObjectDB.instance.TryGetItemPrefab(prefabName, out GameObject prefab) || prefab == null)
            {
                return null;
            }

            ItemDrop drop = prefab.GetComponent<ItemDrop>();
            return drop != null ? drop.m_itemData.m_shared.m_name : null;
        }

        private static void TryRequestReset(Teleport entrance, Humanoid user, ItemDrop.ItemData item)
        {
            if (Time.time - _requestSentAt < RequestTimeout)
            {
                user.Message(MessageHud.MessageType.Center, "Still waiting on the last regeneration request...");
                return;
            }

            // Checked here as well as on the server so a locked dungeon costs no round trip and
            // no waiting - the server's answer is still the one that decides.
            if (!DungeonProgression.IsUnlocked(entrance.transform.position, out string requirement))
            {
                user.Message(MessageHud.MessageType.Center, $"Sealed until {requirement} falls.");
                return;
            }

            int amount = Mathf.Max(1, Plugin.DungeonResetCostAmount.Value);
            if (user.GetInventory().CountItems(item.m_shared.m_name) < amount)
            {
                user.Message(MessageHud.MessageType.Center,
                    $"Need {amount}x {Localization.instance.Localize(item.m_shared.m_name)}");
                return;
            }

            // The zone is the whole address: interiors sit directly above their entrance in XZ,
            // so the entrance's zone is the dungeon's zone.
            Vector2i zone = ZoneSystem.GetZone(entrance.transform.position);

            _requestSentAt = Time.time;
            _pendingItemName = item.m_shared.m_name;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcRequest, zone.x, zone.y);
        }

        // ---- RPC handlers ----------------------------------------------------------------

        /// <summary>Server side: do the work, tell that one client how it went.</summary>
        private static void OnResetRequested(long sender, int zoneX, int zoneY)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }
            if (!Plugin.DungeonResetFromEntrance.Value)
            {
                Reply(sender, false, "Regenerating dungeons is disabled on this server.");
                return;
            }

            var zone = new Vector2i(zoneX, zoneY);
            DungeonGenerator dungeon = DungeonReset.FindLoadedInZone(zone);
            if (dungeon == null)
            {
                Reply(sender, false, "That dungeon isn't loaded on the server right now.");
                return;
            }

            // Asked before Validate only so the refusal can name the boss; Validate enforces the
            // same gate for the console path.
            if (!DungeonProgression.IsUnlocked(dungeon, out string requirement))
            {
                Reply(sender, false, $"Sealed until {requirement} falls.");
                return;
            }

            if (!DungeonReset.Validate(dungeon, force: false, out string error))
            {
                Jotunn.Logger.LogInfo($"Dungeon reset refused for peer {sender}: {error}");
                Reply(sender, false, DungeonReset.AnyoneInside(dungeon, out string who)
                    ? $"{who} is still inside - everyone has to be out first."
                    : "This dungeon can't be regenerated.");
                return;
            }

            DungeonReset.Result result = DungeonReset.Execute(dungeon, DungeonReset.NewSeed(), preservePlayerBuilt: true);

            Jotunn.Logger.LogInfo(
                $"Dungeon reset by peer {sender}: {DungeonReset.Describe(dungeon)} rebuilt with seed {result.Seed} - " +
                $"{result.OldRooms} rooms -> {result.NewRooms} rooms, destroyed {result.Destroyed} object(s), " +
                $"kept {result.Preserved} player-built.");

            Reply(sender, true, $"The dungeon shifts and reforms. ({result.NewRooms} rooms)");
        }

        private static void Reply(long peer, bool success, string message)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcResult, success, message);
        }

        /// <summary>
        /// Client side: the core is consumed here and nowhere else, so a refused reset costs
        /// nothing. Removing by name rather than by the original ItemData because the stack the
        /// player used may have moved or merged during the round trip.
        /// </summary>
        private static void OnResetResult(long sender, bool success, string message)
        {
            _requestSentAt = float.NegativeInfinity;

            Player player = Player.m_localPlayer;
            if (player == null)
            {
                _pendingItemName = null;
                return;
            }

            if (success && _pendingItemName != null)
            {
                player.GetInventory().RemoveItem(_pendingItemName, Mathf.Max(1, Plugin.DungeonResetCostAmount.Value));
            }
            _pendingItemName = null;

            player.Message(MessageHud.MessageType.Center, message);

            if (success)
            {
                player.Message(MessageHud.MessageType.TopLeft,
                    "The old layout is still drawn until you leave and return to this area.");
            }
        }
    }
}
