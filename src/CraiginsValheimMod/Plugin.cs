using BepInEx;
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

        private readonly Harmony _harmony = new Harmony(ModGuid);

        private void Awake()
        {
            _harmony.PatchAll();
            Logger.LogInfo($"{ModName} v{ModVersion} loaded");
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }
    }
}
