using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;

namespace CraiginsValheimMod.Instances
{
    /// <summary>
    /// Entry point for the instanced-dungeon plugin, shipped as its own DLL alongside the base
    /// mod.
    ///
    /// WHY A SEPARATE PLUGIN. Everything else in the base mod is a small, self-contained tweak
    /// to a world that already exists. Instances are the opposite: a subsystem that spawns
    /// zones outside the map, teleports players into them, runs its own RPC protocol and
    /// destroys itself afterwards. It is the piece most likely to be broken by a game update
    /// and the piece a server owner is most likely to want to run without - or to run alone.
    /// Splitting the DLL makes that a file you do or don't copy into BepInEx/plugins, rather
    /// than a config toggle in a plugin that is loaded and patched either way.
    ///
    /// The base mod is a hard dependency (BepInDependency below): instances are built on its
    /// dungeon primitives, and BepInEx guarantees load order from that attribute. Nothing in
    /// the base mod points back here, so the base mod runs perfectly well on its own.
    ///
    /// Harmony patches live with their subsystem: PatchAll() patches the calling assembly, so
    /// this plugin's instance patches are applied by this plugin, and vice versa - which is
    /// also what makes "just delete the DLL" a complete uninstall.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency("com.jotunn.jotunn")]
    [BepInDependency(Plugin.ModGuid)]
    public class InstancesPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.craigins.valheiminstances";
        public const string ModName = "Craigins Valheim Instances";
        public const string ModVersion = "0.5.0";

        public static InstancesPlugin Instance { get; private set; }

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<string> DefaultLocation;

        private readonly Harmony _harmony = new Harmony(ModGuid);

        private void Awake()
        {
            Instance = this;

            Enabled = Config.Bind(
                "Instances", "Enabled", false,
                "Enables temporary instanced dungeons: procedurally generated copies that live in their own " +
                "zone far outside the map, are entered by teleport, and are destroyed once everyone leaves. " +
                "Driven by the 'dungeoninstance' console command. Must be enabled on the SERVER, which spawns " +
                "and generates them; clients need it too, since the command runs there. Instance contents are " +
                "marked non-persistent, so they are never written to the world save and do not survive a " +
                "server restart - that is deliberate, not a limitation. Off by default while untested.");
            DefaultLocation = Config.Bind(
                "Instances", "DefaultLocation", "Crypt3",
                "Which location 'dungeoninstance open' uses when no location= is given. Must be a location " +
                "with a dungeon interior; run 'dungeoninstance locations' to see what this world actually has, " +
                "since prefab names are not guessable and vary by game version.");

            CommandManager.Instance.AddConsoleCommand(new DungeonInstanceCommand());

            _harmony.PatchAll();
            Logger.LogInfo($"{ModName} v{ModVersion} loaded");
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }
    }
}
