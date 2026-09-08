using System;
using System.Globalization;
using Jotunn.Entities;
using UnityEngine;

namespace CraiginsValheimMod.Instances
{
    /// <summary>
    /// Console command: "dungeoninstance". Usable from a client as well as the server - unlike
    /// 'resetdungeon', which is OnlyServer - because opening an instance ends in a teleport that
    /// has to happen on the asking player's own client. Everything authoritative still runs on the
    /// server; see InstanceNetwork.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal class DungeonInstanceCommand : ConsoleCommand
    {
        public override string Name => "dungeoninstance";

        public override string Help =>
            "Opens, lists and closes temporary instanced dungeons - procedurally generated copies " +
            "that live in their own zone far outside the map and are destroyed once everyone leaves. " +
            "Usage: dungeoninstance open [location=<name>] [seed=<n>] [theme=<Theme>] [rooms=<min>-<max>] | " +
            "enter <id> | out | list | locations | close <id|all>. 'locations' lists what can be " +
            "instanced in this world - location names are not guessable. 'theme' overrides which room " +
            "set is used (Crypt, SunkenCrypt, Cave, ForestCrypt, DvergerTown, ...), which is how one " +
            "location produces different dungeons. 'out' teleports you back if an exit ever fails. " +
            "Requires Instances.Enabled on the server.";

        public override void Run(string[] args)
        {
            if (!InstancesPlugin.Enabled.Value)
            {
                Print("dungeoninstance: instanced dungeons are disabled (Instances.Enabled).");
                return;
            }

            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

            switch (verb)
            {
                case "open":
                    Open(args);
                    return;

                case "enter":
                    Submit(InstanceNetwork.FormatSimple("enter", Argument(args, 1)));
                    return;

                case "close":
                    Close(args);
                    return;

                case "list":
                    Submit("list");
                    return;

                case "locations":
                    Submit("locations");
                    return;

                case "out":
                    Out();
                    return;

                default:
                    Print($"dungeoninstance: unknown subcommand '{verb}'. See 'help dungeoninstance'.");
                    return;
            }
        }

        private void Open(string[] args)
        {
            var request = new InstanceRequest();

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                int split = arg.IndexOf('=');
                if (split <= 0)
                {
                    Print($"dungeoninstance: don't understand '{arg}'.");
                    return;
                }

                string key = arg.Substring(0, split).ToLowerInvariant();
                string value = arg.Substring(split + 1);

                switch (key)
                {
                    case "location":
                        request.LocationName = value;
                        break;

                    case "seed":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out request.Seed))
                        {
                            Print($"dungeoninstance: '{value}' isn't a number.");
                            return;
                        }
                        break;

                    case "theme":
                        Room.Theme theme;
                        if (!TryParseThemes(value, out theme))
                        {
                            Print($"dungeoninstance: '{value}' isn't a room theme. Valid: " +
                                  string.Join(", ", Enum.GetNames(typeof(Room.Theme))));
                            return;
                        }
                        request.Themes = theme;
                        break;

                    case "rooms":
                        if (!TryParseRange(value, out request.MinRooms, out request.MaxRooms))
                        {
                            Print($"dungeoninstance: '{value}' isn't a room range like 20-40.");
                            return;
                        }
                        break;

                    default:
                        Print($"dungeoninstance: unknown option '{key}'.");
                        return;
                }
            }

            Submit(InstanceNetwork.FormatOpen(request));
        }

        private void Close(string[] args)
        {
            string target = Argument(args, 1);
            if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                Submit("closeall");
                return;
            }

            int id;
            if (!int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                Print("dungeoninstance: close needs an instance id, or 'all'.");
                return;
            }
            Submit(InstanceNetwork.FormatSimple("close", id.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Purely local escape hatch. Doesn't ask the server anything, because the situation it
        /// exists for is "the exit didn't work" - which is exactly when the round trip is the thing
        /// you don't want to depend on.
        /// </summary>
        private void Out()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Print("dungeoninstance: no local player.");
                return;
            }
            if (!InstanceRegion.IsInstancePosition(player.transform.position))
            {
                Print("dungeoninstance: you aren't in an instance.");
                return;
            }

            Vector3 home;
            if (!InstanceReturn.TryResolveExit(player, out home))
            {
                Print("dungeoninstance: no return point and no home point recorded - use 'goto' to get out.");
                return;
            }

            player.TeleportTo(home, player.transform.rotation, distantTeleport: true);
            InstanceReturn.Clear(player);
            Print($"dungeoninstance: returning to {home}.");
        }

        private static void Submit(string payload)
        {
            InstanceNetwork.Submit(payload, message => Print("dungeoninstance: " + message));
        }

        private static string Argument(string[] args, int index)
        {
            return args.Length > index ? args[index] : "";
        }

        /// <summary>Accepts one theme or several, "Cave+Crypt" style, since m_themes is a bitmask.</summary>
        private static bool TryParseThemes(string value, out Room.Theme themes)
        {
            themes = Room.Theme.None;
            foreach (string part in value.Split('+', ','))
            {
                Room.Theme one;
                if (!Enum.TryParse(part.Trim(), true, out one))
                {
                    return false;
                }
                themes |= one;
            }
            return themes != Room.Theme.None;
        }

        private static bool TryParseRange(string value, out int min, out int max)
        {
            min = 0;
            max = 0;

            string[] parts = value.Split('-');
            if (parts.Length != 2)
            {
                return false;
            }
            return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out min)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out max)
                && min > 0 && max >= min;
        }

        /// <summary>
        /// A dedicated server has no Console.instance, and this command says a lot worth keeping.
        /// Everything goes to the log either way.
        /// </summary>
        private static void Print(string message)
        {
            if (Console.instance != null)
            {
                Console.instance.Print(message);
            }
            Jotunn.Logger.LogInfo(message);
        }
    }
}
