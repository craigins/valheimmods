using BepInEx;
using BepInEx.Configuration;
using CraiginsValheimMod.Dungeons;
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
        public const string ModVersion = "0.6.2";

        public static Plugin Instance { get; private set; }

        public static ConfigEntry<bool> SmoothMistlandsTerrain;
        public static ConfigEntry<bool> RemoveMistlandsFog;

        public static ConfigEntry<bool> CraftAnywhere;
        public static ConfigEntry<bool> NoDeathPenalty;
        public static ConfigEntry<bool> DisableRandomEvents;
        public static ConfigEntry<bool> NoRainDamage;
        public static ConfigEntry<bool> PlantAnywhere;
        public static ConfigEntry<bool> NoFoodDecay;
        public static ConfigEntry<bool> SleepAnyways;
        public static ConfigEntry<bool> EverythingFloats;
        public static ConfigEntry<bool> UnlimitedBreeding;
        public static ConfigEntry<bool> SeedsFromStumps;

        public static ConfigEntry<int> MinDungeonRooms;
        public static ConfigEntry<int> MaxDungeonRerolls;

        public static ConfigEntry<bool> DungeonResetFromEntrance;
        public static ConfigEntry<string> DungeonResetCostItem;
        public static ConfigEntry<int> DungeonResetCostAmount;
        public static ConfigEntry<string> DungeonResetBossGate;

        public static ConfigEntry<int> PregenZonesPerTick;
        public static ConfigEntry<int> PregenSaveEveryNZones;
        public static ConfigEntry<int> PregenTerrainLookahead;

        private readonly Harmony _harmony = new Harmony(ModGuid);

        private void Awake()
        {
            Instance = this;

            SmoothMistlandsTerrain = Config.Bind(
                "Terrain", "SmoothMistlandsTerrain", false,
                "Generate Mistlands terrain with the smoother base-height algorithm instead of the default craggy mask, so it's easier to traverse.");
            RemoveMistlandsFog = Config.Bind(
                "Atmosphere", "RemoveMistlandsFog", false,
                "Removes the Mistlands ground mist (Mister/MistEmitter), so visibility isn't reduced.");

            CraftAnywhere = Config.Bind(
                "QualityOfLife", "CraftAnywhere", true,
                "Skips the nearby-crafting-station check so you can craft from anywhere.");
            NoDeathPenalty = Config.Bind(
                "QualityOfLife", "NoDeathPenalty", true,
                "Removes the skill-level loss penalty on death.");
            DisableRandomEvents = Config.Bind(
                "QualityOfLife", "DisableRandomEvents", false,
                "Disables random world events (raids, etc.).");
            NoRainDamage = Config.Bind(
                "QualityOfLife", "NoRainDamage", true,
                "Stops weather from damaging indoor building pieces.");
            PlantAnywhere = Config.Bind(
                "QualityOfLife", "PlantAnywhere", true,
                "Removes the growth-space/roof/proximity checks on planted crops.");
            NoFoodDecay = Config.Bind(
                "QualityOfLife", "NoFoodDecay", true,
                "Keeps food at its full HP/stamina/eitr benefit for its whole duration instead of vanilla's " +
                "steady decline, so a meal is worth the same in its last minute as its first. Food still " +
                "expires on schedule - the benefit just drops off in one step at the end rather than fading.");
            SleepAnyways = Config.Bind(
                "QualityOfLife", "SleepAnyways", true,
                "Lets you sleep regardless of nearby enemies/exposure/fire/wetness, and skips to morning as soon as everyone's trying to sleep.");
            EverythingFloats = Config.Bind(
                "QualityOfLife", "EverythingFloats", true,
                "Adds a Floating component to any dropped item that doesn't already have one (e.g. ore/metal, which vanilla deliberately sinks), so it floats instead of sinking.");
            UnlimitedBreeding = Config.Bind(
                "QualityOfLife", "UnlimitedBreeding", true,
                "Removes the nearby-population cap on tamed animal breeding (Procreation.m_maxCreatures, " +
                "counted within m_totalCheckRange), so a full pen keeps producing instead of quietly " +
                "stopping. Animals still have to be fed and still need a partner nearby - only the " +
                "crowding limit is lifted, so watch your pen sizes and your framerate.");
            SeedsFromStumps = Config.Bind(
                "QualityOfLife", "SeedsFromStumps", true,
                "Moves tree seeds onto stumps: felling a tree drops no seeds, and destroying the stump it " +
                "leaves always drops one. Forestry stays sustainable but you have to clear the stumps, " +
                "which also tidies the ground for replanting. Wood amounts and stack sizes are untouched, " +
                "and each stump only ever drops a seed its own species already dropped - a species whose " +
                "stump can't be resolved keeps dropping seeds from the tree exactly as vanilla does.");

            MinDungeonRooms = Config.Bind(
                "Dungeons", "MinDungeonRooms", 0,
                "Minimum rooms a generated dungeon must have. 0 = off (vanilla). Raises the prefab's own " +
                "room floor, and rerolls the layout with a different seed if it still comes up short, so you " +
                "stop finding two-room dead ends. IMPORTANT: a dungeon's layout is baked into the world " +
                "permanently when its zone first generates - this only affects dungeons generated while it's " +
                "on, and can't be applied retroactively. Set it before running 'pregenerateworld'.");
            MaxDungeonRerolls = Config.Bind(
                "Dungeons", "MaxDungeonRerolls", 8,
                "How many extra layouts to try when a dungeon comes up under MinDungeonRooms. The roomiest " +
                "attempt wins. Each reroll is a full rebuild of that dungeon, so high values cost real time " +
                "during 'pregenerateworld'. Ignored when MinDungeonRooms is 0.");

            DungeonResetFromEntrance = Config.Bind(
                "Dungeons", "ResetFromEntrance", false,
                "Lets players rebuild a dungeon by using an item on its entrance (a Surtling core by " +
                "default), consumed on success. Uses Teleport.UseItem, which is an unused stub in vanilla, " +
                "so nothing else is affected - and it works while merely hovering the entrance, unlike a " +
                "keybind, which would fight the entrance's own walk-in-to-enter trigger. This must be enabled " +
                "on the SERVER, which does the actual work; enable it on clients too so they get the hover " +
                "hint. Refuses while anyone is inside, and keeps player-built pieces. Off by default - it's a " +
                "permanent, irreversible change to a world.");
            DungeonResetCostItem = Config.Bind(
                "Dungeons", "ResetCostItem", "SurtlingCore",
                "Prefab name of the item consumed to rebuild a dungeon from its entrance. Any item in " +
                "ObjectDB works (e.g. 'SurtlingCore', 'Ruby', 'DragonEgg'). The server's value is the one " +
                "that matters for what's accepted; clients use theirs only to decide what to send.");
            DungeonResetCostAmount = Config.Bind(
                "Dungeons", "ResetCostAmount", 1,
                "How many of ResetCostItem a rebuild costs. Minimum 1.");
            DungeonResetBossGate = Config.Bind(
                "Dungeons", "ResetBossGate",
                "BlackForest=gd_king, Swamp=Bonemass, Mountain=Dragon, Plains=GoblinKing, " +
                "Mistlands=SeekerQueen, AshLands=Fader",
                "Which boss a world must have killed before dungeons in a biome can be regenerated, as " +
                "'Biome=BossPrefab' pairs. Empty to allow regeneration everywhere. Boss prefab names: " +
                "Eikthyr, gd_king (the Elder), Bonemass, Dragon (Moder), GoblinKing (Yagluth), SeekerQueen, " +
                "Fader - the required key is read off the prefab, so it stays right across game updates. A " +
                "name that isn't a boss prefab is used as a raw global key instead (e.g. 'KilledTroll'). " +
                "Several rules may name the same biome, and all of them must be satisfied. The gate is " +
                "world-wide: one player's kill unlocks the biome for everyone, since that's what a boss " +
                "kill sets. The SERVER's value is what's enforced; clients use theirs only for the hover " +
                "text. 'resetdungeon force' in the console bypasses it.");

            PregenZonesPerTick = Config.Bind(
                "WorldPregeneration", "ZonesPerTick", 64,
                "How many zones the 'pregenerateworld' console command attempts per frame. Higher isn't " +
                "necessarily faster - the real bottleneck is the game's single background terrain-build thread.");
            PregenSaveEveryNZones = Config.Bind(
                "WorldPregeneration", "SaveEveryNZones", 2000,
                "How often 'pregenerateworld' checkpoint-saves progress, so a server restart mid-run doesn't lose it.");
            PregenTerrainLookahead = Config.Bind(
                "WorldPregeneration", "TerrainLookahead", 8,
                "How many zones ahead of the spiral 'pregenerateworld' pre-requests terrain for, to keep the " +
                "game's single background terrain-build thread busy without ever generating a zone out of order. " +
                "Clamped to 0-12: the game's finished-terrain queue only holds 16 entries before it starts " +
                "discarding the oldest, and going past that just makes it rebuild the same terrain twice.");

            CommandManager.Instance.AddConsoleCommand(new PregenerateWorldCommand());
            CommandManager.Instance.AddConsoleCommand(new ResetDungeonCommand());

            _harmony.PatchAll();
            Logger.LogInfo($"{ModName} v{ModVersion} loaded");
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }
    }
}
