using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Instances
{
    /// <summary>
    /// The client/server split for instanced dungeons.
    ///
    /// The work has to happen where the world lives: spawning a location, generating a dungeon and
    /// destroying ZDOs are all server operations. But the person asking is usually on a client, and
    /// the teleport at the end has to happen on that client's own Player. So a request goes up as a
    /// routed RPC, the server does everything authoritative, and the reply either prints a message
    /// or carries a destination back.
    ///
    /// Requests are a pipe-separated string rather than typed RPC arguments so that adding an
    /// operation doesn't mean registering another RPC and keeping both ends in step. There are only
    /// three registered names, and they never change.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class InstanceNetwork
    {
        private const string RpcRequest = "CVM_InstanceRequest";
        private const string RpcMessage = "CVM_InstanceMessage";
        private const string RpcEnter = "CVM_InstanceEnter";

        private static bool _registered;

        /// <summary>
        /// ZRoutedRpc is constructed in ZNet, so registration can't happen at plugin load.
        /// Game.Start runs once the net layer is up, on both client and dedicated server.
        ///
        /// KNOWN BUG, NOT YET FIXED: ZNet.Awake builds a brand-new ZRoutedRpc for every world
        /// joined, but _registered is a static bool that is never reset. Leave a world and join
        /// another (or the same one) without restarting the game, and the new ZRoutedRpc never gets
        /// these handlers - requests go out and nothing answers. Dedicated servers are unaffected
        /// (one world per process); clients and hosts are. Fix: remember the ZRoutedRpc instance
        /// registered on instead of a bool, as StargateNetwork does.
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
                ZRoutedRpc.instance.Register<string>(RpcRequest, OnRequest);
                ZRoutedRpc.instance.Register<string>(RpcMessage, OnMessage);
                ZRoutedRpc.instance.Register<Vector3, string>(RpcEnter, OnEnter);
                _registered = true;
            }
        }

        // ---- entry points -----------------------------------------------------------------

        /// <summary>
        /// Runs an operation, wherever we happen to be. On the server it executes directly; on a
        /// client it goes up as an RPC and the answer arrives asynchronously through OnMessage /
        /// OnEnter. Deliberately not a loopback RPC on the server - the direct path is one fewer
        /// thing to be wrong about, and it keeps console output synchronous.
        /// </summary>
        public static void Submit(string payload, Action<string> localPrint)
        {
            if (DungeonInstanceManager.IsServer)
            {
                Execute(payload, localPrint, (pos, message) =>
                {
                    localPrint(message);
                    TeleportLocalPlayer(pos);
                });
                return;
            }

            if (ZRoutedRpc.instance == null)
            {
                localPrint("not connected to a server.");
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcRequest, payload);
        }

        private static void OnRequest(long sender, string payload)
        {
            if (!DungeonInstanceManager.IsServer)
            {
                return;
            }
            if (!InstancesPlugin.Enabled.Value)
            {
                Reply(sender, "Instanced dungeons are disabled on this server.");
                return;
            }

            Jotunn.Logger.LogInfo($"Instance request from peer {sender}: {payload}");
            Execute(payload, message => Reply(sender, message), (pos, message) => Enter(sender, pos, message));
        }

        private static void Reply(long peer, string message)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcMessage, message);
        }

        private static void Enter(long peer, Vector3 position, string message)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcEnter, position, message);
        }

        private static void OnMessage(long sender, string message)
        {
            Print(message);
        }

        private static void OnEnter(long sender, Vector3 position, string message)
        {
            Print(message);
            TeleportLocalPlayer(position);
        }

        /// <summary>
        /// The client half of entering. The return point is written before the teleport, not after,
        /// so a disconnect mid-teleport still leaves a way home. It goes in Player.m_customData,
        /// which Valheim serializes with the player profile - so it survives logging out inside an
        /// instance, which is exactly when it's needed.
        /// </summary>
        private static void TeleportLocalPlayer(Vector3 position)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }

            if (!InstanceRegion.IsInstancePosition(player.transform.position))
            {
                InstanceReturn.Set(player, player.transform.position);
            }

            player.TeleportTo(position, player.transform.rotation, distantTeleport: true);
        }

        private static void Print(string message)
        {
            if (Console.instance != null)
            {
                Console.instance.Print(message);
            }
            else if (Player.m_localPlayer != null)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
            }
            Jotunn.Logger.LogInfo(message);
        }

        // ---- operations -------------------------------------------------------------------

        public static string FormatOpen(InstanceRequest request)
        {
            return string.Join("|", new[]
            {
                "open",
                request.LocationName ?? "",
                request.Seed.ToString(CultureInfo.InvariantCulture),
                ((int)request.Themes).ToString(CultureInfo.InvariantCulture),
                request.MinRooms.ToString(CultureInfo.InvariantCulture),
                request.MaxRooms.ToString(CultureInfo.InvariantCulture),
            });
        }

        public static string FormatSimple(string verb, string argument)
        {
            return string.IsNullOrEmpty(argument) ? verb : verb + "|" + argument;
        }

        /// <summary>
        /// Server-side execution of one request. Reports through the callbacks rather than
        /// printing, so the same code serves a local host console and a remote client.
        /// </summary>
        private static void Execute(string payload, Action<string> reply, Action<Vector3, string> enter)
        {
            string[] parts = (payload ?? "").Split('|');
            string verb = parts.Length > 0 ? parts[0] : "";

            switch (verb)
            {
                case "open":
                    ExecuteOpen(parts, reply, enter);
                    return;

                case "enter":
                    ExecuteEnter(parts, reply, enter);
                    return;

                case "close":
                    ExecuteClose(parts, reply);
                    return;

                case "closeall":
                    reply($"closed {DungeonInstanceManager.CloseAll("closeall")} instance(s).");
                    return;

                case "list":
                    reply(DescribeInstances());
                    return;

                case "locations":
                    reply(DescribeLocations());
                    return;

                default:
                    reply($"unknown instance operation '{verb}'.");
                    return;
            }
        }

        private static void ExecuteOpen(string[] parts, Action<string> reply, Action<Vector3, string> enter)
        {
            var request = new InstanceRequest
            {
                LocationName = parts.Length > 1 ? parts[1] : null,
                Seed = ParseInt(parts, 2),
                Themes = (Room.Theme)ParseInt(parts, 3),
                MinRooms = ParseInt(parts, 4),
                MaxRooms = ParseInt(parts, 5),
            };

            if (string.IsNullOrEmpty(request.LocationName))
            {
                request.LocationName = InstancesPlugin.DefaultLocation.Value;
            }

            string error;
            DungeonInstance instance = DungeonInstanceManager.Open(request, out error);
            if (instance == null)
            {
                reply("could not open an instance: " + error);
                return;
            }

            enter(instance.Arrival, $"Opened instance {instance.Describe()}.");
        }

        private static void ExecuteEnter(string[] parts, Action<string> reply, Action<Vector3, string> enter)
        {
            DungeonInstance instance = DungeonInstanceManager.Get(ParseInt(parts, 1));
            if (instance == null)
            {
                reply("no such instance.");
                return;
            }
            enter(instance.Arrival, $"Entering instance {instance.Describe()}.");
        }

        private static void ExecuteClose(string[] parts, Action<string> reply)
        {
            int id = ParseInt(parts, 1);
            DungeonInstance instance = DungeonInstanceManager.Get(id);
            if (instance == null)
            {
                reply("no such instance.");
                return;
            }

            string who;
            if (DungeonInstanceManager.IsOccupied(instance, out who))
            {
                reply($"{who} is still inside instance #{id} - they'd be left standing in nothing 20km up.");
                return;
            }

            DungeonInstanceManager.Close(id, "closed by request");
            reply($"closed instance #{id}.");
        }

        private static string DescribeInstances()
        {
            if (DungeonInstanceManager.Count == 0)
            {
                return "no instances open.";
            }

            var text = new StringBuilder();
            text.Append($"{DungeonInstanceManager.Count} instance(s) open:");
            foreach (DungeonInstance instance in DungeonInstanceManager.All)
            {
                string who;
                bool occupied = DungeonInstanceManager.IsOccupied(instance, out who);
                text.Append($"\n  {instance.Describe()}  age={Mathf.RoundToInt(Time.time - instance.OpenedAt)}s");
                text.Append(occupied ? $"  OCCUPIED ({who})" : instance.Armed ? "  empty" : "  never visited");
            }
            return text.ToString();
        }

        /// <summary>
        /// Every location in this world that has a dungeon interior we could instance. Exists
        /// because location prefab names are not guessable and getting one wrong is otherwise a
        /// silent "no such location".
        /// </summary>
        private static string DescribeLocations()
        {
            if (ZoneSystem.instance == null)
            {
                return "the world isn't loaded yet.";
            }

            var names = new List<string>();
            foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
            {
                if (location == null || !location.m_prefab.IsValid)
                {
                    continue;
                }

                GameObject asset = location.m_prefab.Asset;
                if (asset == null)
                {
                    // Not loaded right now. Interior radius is the cheap proxy for "has an
                    // interior" and is on the ZoneLocation itself, so it works without the asset.
                    if (location.m_interiorRadius > 0f)
                    {
                        names.Add(Dungeons.DungeonReset.LocationName(location) + "  (not loaded)");
                    }
                    continue;
                }

                Location component = asset.GetComponent<Location>();
                if (component == null || !component.m_hasInterior || component.m_generator == null)
                {
                    continue;
                }
                names.Add($"{Dungeons.DungeonReset.LocationName(location)}  " +
                          $"[{component.m_generator.m_algorithm}, themes={component.m_generator.m_themes}, " +
                          $"rooms={component.m_generator.m_minRooms}-{component.m_generator.m_maxRooms}]");
            }

            if (names.Count == 0)
            {
                return "no locations with dungeon interiors found.";
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return $"{names.Count} location(s) with dungeon interiors:\n  " + string.Join("\n  ", names.ToArray());
        }

        private static int ParseInt(string[] parts, int index)
        {
            int value;
            if (parts.Length > index && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
            return 0;
        }
    }
}
