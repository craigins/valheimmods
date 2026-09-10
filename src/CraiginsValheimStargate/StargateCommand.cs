using Jotunn.Entities;

namespace CraiginsValheimMod.Stargate
{
    /// <summary>
    /// Console command: "stargate list". Asks the server, since only it holds every gate.
    ///
    /// A cheat (needs devcommands, which on a server needs admin): it hands out every address in the
    /// world, which players are otherwise meant to learn by finding the gate.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal class StargateCommand : ConsoleCommand
    {
        public override string Name => "stargate";

        public override string Help =>
            "Lists every stargate in the world with its address, position and link. Usage: stargate list. " +
            "Dial and disconnect at the gate itself: E to dial, Shift+E to disconnect.";

        public override bool IsCheat => true;

        public override void Run(string[] args)
        {
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            if (verb != "list")
            {
                Print($"stargate: unknown subcommand '{verb}'. See 'help stargate'.");
                return;
            }
            if (ZRoutedRpc.instance == null)
            {
                Print("stargate: not in a world.");
                return;
            }
            StargateNetwork.RequestList();
        }

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
