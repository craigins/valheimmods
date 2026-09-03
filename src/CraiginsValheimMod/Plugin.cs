using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace CraiginsValheimMod
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency("com.jotunn.jotunn")]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.craigins.valheimmod";
        public const string ModName = "Craigins Valheim Mod";
        public const string ModVersion = "0.1.0";

        public static ConfigEntry<bool> SmoothMistlandsTerrain;
        public static ConfigEntry<bool> RemoveMistlandsFog;

        private readonly Harmony _harmony = new Harmony(ModGuid);

        private void Awake()
        {
            SmoothMistlandsTerrain = Config.Bind(
                "Terrain", "SmoothMistlandsTerrain", true,
                "Generate Mistlands terrain with the smoother base-height algorithm instead of the default craggy mask, so it's easier to traverse.");
            RemoveMistlandsFog = Config.Bind(
                "Atmosphere", "RemoveMistlandsFog", true,
                "Removes the Mistlands ground mist (Mister/MistEmitter), so visibility isn't reduced.");

            _harmony.PatchAll();
            Logger.LogInfo($"{ModName} v{ModVersion} loaded");
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }
    }
}
