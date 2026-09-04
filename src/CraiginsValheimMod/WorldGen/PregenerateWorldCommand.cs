using Jotunn.Entities;

namespace CraiginsValheimMod.WorldGen
{
    /// <summary>
    /// Console command: "pregenerateworld". Server-only (see OnlyServer). Kicks off
    /// WorldPregenerator as a coroutine on the plugin's own MonoBehaviour so it runs across
    /// many frames instead of freezing the server for the whole operation.
    /// </summary>
    internal class PregenerateWorldCommand : ConsoleCommand
    {
        public override string Name => "pregenerateworld";

        public override string Help =>
            "Force-generates every not-yet-generated zone in the world, spiralling outward from " +
            "the world origin (can take hours on a full-size map), and saves as it goes. Run this " +
            "BEFORE copying a world to a " +
            "dedicated server, and after enabling any terrain patches (e.g. SmoothMistlandsTerrain) " +
            "you want baked into the result - terrain height is fixed at generation time.";

        public override bool OnlyServer => true;

        public override void Run(string[] args)
        {
            if (WorldPregenerator.IsRunning)
            {
                Console.instance.Print("pregenerateworld: already running - check the log for progress.");
                return;
            }

            Console.instance.Print("pregenerateworld: starting - this can take a long time, watch the log for progress.");
            Plugin.Instance.StartCoroutine(
                WorldPregenerator.Run(
                    Plugin.PregenZonesPerTick.Value,
                    Plugin.PregenSaveEveryNZones.Value,
                    Plugin.PregenTerrainLookahead.Value));
        }
    }
}
