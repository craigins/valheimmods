using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Utils;

namespace CraiginsValheimMod.OffMap
{
    /// <summary>
    /// Entry point for the off-map dungeons plugin, shipped as its own DLL alongside the base mod.
    ///
    /// Moves the interior of heavy dungeons - currently only Mörkhalla (location prefab MorkBorg) -
    /// out of the zone its entrance stands in and into a zone of its own past the edge of the world.
    /// Vanilla puts the interior 5000m straight above the entrance, in the same zone, so the whole
    /// tower (around 50,000 objects, 7,000 of them networked) loads and simulates whenever anyone is
    /// near the gates, and is torn down in a single frame when they walk away. Once moved, it only
    /// exists while somebody is actually inside, and the teardown happens behind the loading screen
    /// of the exit teleport.
    ///
    /// Everything a client needs to follow a moved dungeon is written into the world's own objects
    /// (see OffMapKeys), so clients act on what the server decided rather than on their own config.
    /// The one setting here is the server's: whether to move dungeons at all.
    ///
    /// Every player needs this DLL. A player without it would walk through the gates to where the
    /// interior used to be and fall. NetworkCompatibility below makes Jotunn refuse that connection
    /// with a clear message instead.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency("com.jotunn.jotunn")]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.None)]
    public class OffMapPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.craigins.valheimoffmapdungeons";
        public const string ModName = "Craigins Valheim Off-Map Dungeons";
        public const string ModVersion = "0.7.6";

        public static ConfigEntry<bool> Enabled;

        private readonly Harmony _harmony = new Harmony(ModGuid);

        private void Awake()
        {
            Enabled = Config.Bind(
                "OffMapDungeons", "Enabled", true,
                "Moves Mörkhalla interiors into their own zone past the edge of the world, so the tower " +
                "only loads while someone is inside it. Applies to Mörkhallas generated from now on, and " +
                "moves Mörkhallas that already exist when the server starts. Read by the SERVER; clients " +
                "follow whatever the server did regardless of their own value. Turning it off later stops " +
                "new ones being moved but leaves already-moved ones where they are, still working. Do not " +
                "remove the DLL once a world has moved dungeons in it - their entrances would lead nowhere.");

            _harmony.PatchAll();
            if (!OffMapSpawnPatches.TranspilerApplied)
            {
                Logger.LogError("Could not hook ZoneSystem.SpawnLocation - this game version is not supported. " +
                    "No dungeons will be moved; already-moved ones still work.");
            }
            Logger.LogInfo($"{ModName} v{ModVersion} loaded");
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }
    }
}
