using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;

namespace CraiginsValheimMod.Stargate
{
    /// <summary>
    /// Entry point for the stargate plugin, shipped as its own DLL alongside the base mod.
    ///
    /// A stargate is a buildable portal with a fixed address instead of a tag. Interact with one to
    /// dial another gate's address; the two stay linked until either end disconnects, or until some
    /// third gate dials one of them - an incoming connection always wins and drops whatever that gate
    /// was linked to before. There is deliberately no timeout: a timer would have to run on gates that
    /// usually aren't loaded anywhere.
    ///
    /// Separate from the base mod for the same reason instances are: it adds a piece, an RPC
    /// protocol and patches on vanilla portals, and a server owner should be able to leave all of
    /// that out by not copying a file. Unlike instances it needs nothing from the base mod, so there
    /// is no BepInDependency on it.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency("com.jotunn.jotunn")]
    public class StargatePlugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.craigins.valheimstargate";
        public const string ModName = "Craigins Valheim Stargate";
        public const string ModVersion = "0.6.2";

        public static ConfigEntry<bool> AllowAllItems;

        private readonly Harmony _harmony = new Harmony(ModGuid);

        private void Awake()
        {
            AllowAllItems = Config.Bind(
                "Stargate", "AllowAllItems", false,
                "Lets players carry ore, metal and other normally non-teleportable items through a stargate, " +
                "like the stone portal. Off keeps the wooden portal's rules. Read when the piece is registered, " +
                "so a change needs a game restart. Enforced client-side - the same as vanilla portals - so each " +
                "client's own value is the one that applies to them.");

            StargatePiece.Register();
            CommandManager.Instance.AddConsoleCommand(new StargateCommand());

            _harmony.PatchAll();
            Logger.LogInfo($"{ModName} v{ModVersion} loaded");
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }
    }
}
