using BepInEx;
using BepInEx.Configuration;
using CraiginsValheimMod.WorldGen;
using HarmonyLib;
using Jotunn.Managers;

namespace CraiginsValheimMod
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency("com.jotunn.jotunn")]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.craigins.valheimmod";
        public const string ModName = "Craigins Valheim Mod";
        public const string ModVersion = "0.1.0";

        public static Plugin Instance { get; private set; }

        public static ConfigEntry<bool> SmoothMistlandsTerrain;
        public static ConfigEntry<bool> RemoveMistlandsFog;

        public static ConfigEntry<bool> CraftAnywhere;
        public static ConfigEntry<bool> NoDeathPenalty;
        public static ConfigEntry<bool> DisableRandomEvents;
        public static ConfigEntry<bool> NoRainDamage;
        public static ConfigEntry<bool> PlantAnywhere;
        public static ConfigEntry<bool> SimplifiedFoodTotals;
        public static ConfigEntry<bool> SleepAnyways;
        public static ConfigEntry<bool> EverythingFloats;

        public static ConfigEntry<int> PregenZonesPerTick;
        public static ConfigEntry<int> PregenSaveEveryNZones;

        private readonly Harmony _harmony = new Harmony(ModGuid);

        private void Awake()
        {
            Instance = this;

            SmoothMistlandsTerrain = Config.Bind(
                "Terrain", "SmoothMistlandsTerrain", true,
                "Generate Mistlands terrain with the smoother base-height algorithm instead of the default craggy mask, so it's easier to traverse.");
            RemoveMistlandsFog = Config.Bind(
                "Atmosphere", "RemoveMistlandsFog", true,
                "Removes the Mistlands ground mist (Mister/MistEmitter), so visibility isn't reduced.");

            CraftAnywhere = Config.Bind(
                "QualityOfLife", "CraftAnywhere", true,
                "Skips the nearby-crafting-station check so you can craft from anywhere.");
            NoDeathPenalty = Config.Bind(
                "QualityOfLife", "NoDeathPenalty", true,
                "Removes the skill-level loss penalty on death.");
            DisableRandomEvents = Config.Bind(
                "QualityOfLife", "DisableRandomEvents", true,
                "Disables random world events (raids, etc.).");
            NoRainDamage = Config.Bind(
                "QualityOfLife", "NoRainDamage", true,
                "Stops weather from damaging indoor building pieces.");
            PlantAnywhere = Config.Bind(
                "QualityOfLife", "PlantAnywhere", true,
                "Removes the growth-space/roof/proximity checks on planted crops.");
            SimplifiedFoodTotals = Config.Bind(
                "QualityOfLife", "SimplifiedFoodTotals", false,
                "Recomputes HP/stamina/eitr as a straight sum of active foods' base values, bypassing whatever " +
                "the current game version does differently (freshness/decay, etc.). Off by default - see the " +
                "comment in QualityOfLifePatches.cs before enabling.");
            SleepAnyways = Config.Bind(
                "QualityOfLife", "SleepAnyways", true,
                "Lets you sleep regardless of nearby enemies/exposure/fire/wetness, and skips to morning as soon as everyone's trying to sleep.");
            EverythingFloats = Config.Bind(
                "QualityOfLife", "EverythingFloats", true,
                "Adds a Floating component to any dropped item that doesn't already have one (e.g. ore/metal, which vanilla deliberately sinks), so it floats instead of sinking.");

            PregenZonesPerTick = Config.Bind(
                "WorldPregeneration", "ZonesPerTick", 64,
                "How many zones the 'pregenerateworld' console command attempts per frame. Higher isn't " +
                "necessarily faster - the real bottleneck is the game's single background terrain-build thread.");
            PregenSaveEveryNZones = Config.Bind(
                "WorldPregeneration", "SaveEveryNZones", 2000,
                "How often 'pregenerateworld' checkpoint-saves progress, so a server restart mid-run doesn't lose it.");

            CommandManager.Instance.AddConsoleCommand(new PregenerateWorldCommand());

            _harmony.PatchAll();
            Logger.LogInfo($"{ModName} v{ModVersion} loaded");
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }
    }
}
