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
        public const string ModVersion = "0.7.11";

        public static ConfigEntry<bool> AllowAllItems;
        public static ConfigEntry<bool> SignDial;
        public static ConfigEntry<float> SignDialRange;

        private readonly Harmony _harmony = new Harmony(ModGuid);

        private void Awake()
        {
            AllowAllItems = Config.Bind(
                "Stargate", "AllowAllItems", false,
                "Lets players carry ore, metal and other normally non-teleportable items through a stargate, " +
                "like the stone portal. Off keeps the wooden portal's rules. Read when the piece is registered, " +
                "so a change needs a game restart. Enforced client-side - the same as vanilla portals - so each " +
                "client's own value is the one that applies to them.");

            SignDial = Config.Bind(
                "Stargate", "SignDial", true,
                "Shift+E on a sign dials the address written on it, using the nearest stargate. The sign " +
                "is read as a name, a space, then the address - 'Misty3 S58-ELN' dials S58-ELN. Colours " +
                "and other rich text tags on the sign are ignored. A sign with no address on it behaves " +
                "as usual. Only your own client needs this; the server does not.");
            SignDialRange = Config.Bind(
                "Stargate", "SignDialRange", 10f,
                new ConfigDescription(
                    "How far from the sign, in metres, a stargate can be and still be dialed from it. " +
                    "The nearest gate within this distance of the sign is the one that dials.",
                    new AcceptableValueRange<float>(1f, 50f)));

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
