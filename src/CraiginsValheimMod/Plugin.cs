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
        public const string ModVersion = "0.7.2";

        public static Plugin Instance { get; private set; }

        public static ConfigEntry<bool> SmoothMistlandsTerrain;
        public static ConfigEntry<bool> RemoveMistlandsFog;
        public static ConfigEntry<bool> FixedMistlandsFog;
        public static ConfigEntry<float> MistlandsFogDensity;
        public static ConfigEntry<bool> WispLightClearsFog;
        public static ConfigEntry<float> WispLightFogFadeSeconds;
        public static ConfigEntry<float> AshlandsBrightness;

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
        public static ConfigEntry<bool> HarpoonTeleport;
        public static ConfigEntry<bool> HarpoonNoDamageToTamed;
        public static ConfigEntry<bool> HarpoonHitsTamedWithoutPvP;
        public static ConfigEntry<bool> GrapplingHookNoReload;
        public static ConfigEntry<bool> GrapplingHookNoGravity;
        public static ConfigEntry<bool> GrapplingHookNoRangeLimit;
        public static ConfigEntry<bool> BlastFurnaceSmeltsAll;
        public static ConfigEntry<bool> VineberryIgnoresAdjacency;
        public static ConfigEntry<bool> IvyIgnoresAdjacency;
        public static ConfigEntry<float> RestingHealthRegenMultiplier;
        public static ConfigEntry<float> TamingSpeedMultiplier;

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
            FixedMistlandsFog = Config.Bind(
                "Atmosphere", "FixedMistlandsFog", false,
                "Pins the weather fog density of every Mistlands weather (clear, rain, thunder) to " +
                "MistlandsFogDensity for all times of day. Meant to pair with RemoveMistlandsFog: the " +
                "ground mist goes, and ordinary weather fog takes its place. Rain and thunder still occur.");
            MistlandsFogDensity = Config.Bind(
                "Atmosphere", "MistlandsFogDensity", 0.2f,
                new ConfigDescription(
                    "Fog density used by FixedMistlandsFog. For scale: vanilla Mistlands weather is 0.02-0.05, " +
                    "the Meadows 'Misty' weather peaks at 0.15 at night, and Sunken Crypts are 0.2.",
                    new AcceptableValueRange<float>(0f, 1f)));
            WispLightClearsFog = Config.Bind(
                "Atmosphere", "WispLightClearsFog", false,
                "While you have a Wisplight equipped, weather fog is removed entirely, in every biome. " +
                "Applies to the wearer's own view only; placed wisp torches don't count.");
            WispLightFogFadeSeconds = Config.Bind(
                "Atmosphere", "WispLightFogFadeSeconds", 1f,
                new ConfigDescription(
                    "Seconds the fog takes to fade out/in when the Wisplight is equipped/unequipped.",
                    new AcceptableValueRange<float>(0.01f, 10f)));
            AshlandsBrightness = Config.Bind(
                "Atmosphere", "AshlandsBrightness", 1f,
                new ConfigDescription(
                    "How bright the Ashlands are, as a multiplier on the ambient light, sun and moon light " +
                    "and fog colour of every Ashlands weather, day and night. 1 is vanilla, above 1 is " +
                    "brighter, below 1 is darker. For scale: the usual Ashlands weather has about two " +
                    "thirds of the sunlight and about a third of the moonlight of clear Meadows weather, " +
                    "so around 1.5 gives Meadows-like days and 2 to 3 gives Meadows-like nights. Applies " +
                    "to your own view only, and takes effect as soon as the setting changes.",
                    new AcceptableValueRange<float>(0.1f, 4f)));
            FixedMistlandsFog.SettingChanged += (_, __) => Patches.AtmospherePatches.MistlandsFog.Apply();
            MistlandsFogDensity.SettingChanged += (_, __) => Patches.AtmospherePatches.MistlandsFog.Apply();
            AshlandsBrightness.SettingChanged += (_, __) => Patches.AtmospherePatches.AshlandsLight.Apply();

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
                "Removes the growth-space/roof/proximity checks on planted crops. Vine saplings " +
                "(vineberry, ivy) still need a wall within reach, since that is where the vine grows.");
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
            HarpoonTeleport = Config.Bind(
                "QualityOfLife", "HarpoonTeleport", true,
                "Brings whatever you have on the harpoon through a teleport with you, instead of " +
                "leaving it behind and snapping the line. The creature is moved by the same amount " +
                "you are, so the line never goes taut and dragging behaves the same afterwards. " +
                "Client-side and needs no server support: it works by moving a creature your own " +
                "client already owns, which a harpooned one is unless another player is standing " +
                "closer. Harpooned players are left alone, and bosses can't be harpooned at all.");
            HarpoonNoDamageToTamed = Config.Bind(
                "QualityOfLife", "HarpoonNoDamageToTamed", true,
                "The harpoon does no damage to tamed animals but still attaches to them, so you can " +
                "drag one without hurting it. The animal is still nudged by the impact. Wild " +
                "creatures and players are hit as normal. Only the person throwing the harpoon " +
                "needs this; the server does not.");
            HarpoonHitsTamedWithoutPvP = Config.Bind(
                "QualityOfLife", "HarpoonHitsTamedWithoutPvP", true,
                "Lets your harpoon hook tamed animals while your PvP is off. In vanilla a harpoon " +
                "flies straight through a tamed animal unless you have PvP enabled, so without this " +
                "you have to turn PvP on before you can hook one. Turn it off if you would rather " +
                "your harpoon kept passing through your animals to reach what is behind them. Only " +
                "the person throwing the harpoon needs this; the server does not.");
            GrapplingHookNoReload = Config.Bind(
                "QualityOfLife", "GrapplingHookNoReload", true,
                "The grappling hook never needs reloading, so it is ready to fire again at once - " +
                "including while you are still being pulled, which starts a new pull and drops the " +
                "old one. In vanilla it reloads for 2 seconds, and only once the pull has ended. " +
                "Each shot still costs its stamina. The hook is always drawn loaded in the " +
                "launcher. Crossbows and the lightning staff still reload. Affects only your own " +
                "character; the server does not need it.");
            GrapplingHookNoGravity = Config.Bind(
                "QualityOfLife", "GrapplingHookNoGravity", true,
                "The grappling hook flies in a dead straight line along where you are looking, " +
                "instead of dropping as it goes. In vanilla it sags about 5 metres over its " +
                "flight (half that for the secondary shot). Applies to both of the hook's shots. " +
                "Affects only your own shots; the server does not need it.");
            GrapplingHookNoRangeLimit = Config.Bind(
                "QualityOfLife", "GrapplingHookNoRangeLimit", true,
                "The grappling hook keeps flying until it hits something, and the line never " +
                "snaps for being too long. In vanilla the hook vanishes after 1 second of flight, " +
                "about 40 metres, and the line breaks beyond 60 metres (70 for the secondary " +
                "shot). The hook can still only catch on what the game has loaded around you, " +
                "which is roughly 130 to 190 metres out at the original simulation distance. It " +
                "flies at its usual speed, so a long shot takes a few seconds to land. A hook " +
                "that hits nothing is removed after 60 seconds. Affects only your own shots; the " +
                "server does not need it.");
            BlastFurnaceSmeltsAll = Config.Bind(
                "QualityOfLife", "BlastFurnaceSmeltsAll", true,
                "Lets the blast furnace smelt everything the regular smelter does (copper, tin, iron, " +
                "silver and their scrap), on top of its own ores, so you only need one. Fuel, speed and " +
                "capacity stay the blast furnace's own. Every player needs it on, since whoever loads ore " +
                "and whoever's game is running the furnace both check what it accepts - and so does a " +
                "dedicated server, which runs furnaces near the world centre when nobody is close and " +
                "would otherwise use up the queued ore without making bars. Read when the " +
                "world loads, so a change needs you to rejoin.");
            VineberryIgnoresAdjacency = Config.Bind(
                "QualityOfLife", "VineberryIgnoresAdjacency", true,
                "Every segment of a vineberry vine grows berries. In vanilla a segment only fruits " +
                "if no other segment with berries is within about 3 m sideways or 5 m above or " +
                "below it on the same wall, and only if it has two neighbouring segments - so a " +
                "whole plant carries one cluster at a time. Both rules are removed. The respawn " +
                "time is unchanged: each segment fruits once every 200 minutes, and a newly grown " +
                "segment waits that long for its first berries. Whoever's game is running the vine " +
                "applies this - normally the nearest player - so everyone near the vines needs " +
                "the same value.");
            IvyIgnoresAdjacency = Config.Bind(
                "QualityOfLife", "IvyIgnoresAdjacency", true,
                "The same for ivy. In vanilla ivy never grows berries, because it allows no " +
                "berries at all near a segment. With this on, every ivy segment grows vineberries " +
                "on the same 200 minute timer, and picking them can also drop ivy seeds. Turn it " +
                "off to keep ivy purely decorative. Everyone near the ivy needs the same value, " +
                "and players without the mod will not see the berries on it.");
            RestingHealthRegenMultiplier = Config.Bind(
                "QualityOfLife", "RestingHealthRegenMultiplier", 2f,
                new ConfigDescription(
                    "Multiplies health regeneration while you have the Resting effect - near a fire and " +
                    "either sitting or under shelter, with no enemy aware of you. 1 is vanilla, 2 heals " +
                    "twice as fast. For getting back to full health after a respawn. The Rested buff you " +
                    "carry around afterwards is not changed, and neither are stamina or eitr. Health " +
                    "regeneration comes from the food you have eaten, so you still need to eat. Affects " +
                    "only your own character.",
                    new AcceptableValueRange<float>(1f, 20f)));
            TamingSpeedMultiplier = Config.Bind(
                "QualityOfLife", "TamingSpeedMultiplier", 3f,
                new ConfigDescription(
                    "How fast animals tame. 1 is vanilla, which is 30 minutes for every animal; 3 makes it " +
                    "10 minutes. Animals still have to be fed and calm, with someone nearby. Stacks with the " +
                    "tamer mead, which doubles it again. Whoever's game is running the animal applies " +
                    "this - normally the nearest player - so everyone near the pen needs the same value, " +
                    "and so does a dedicated server for a pen near the world centre.",
                    new AcceptableValueRange<float>(0.1f, 100f)));

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
