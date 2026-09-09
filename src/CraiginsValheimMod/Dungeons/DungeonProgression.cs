using System;
using System.Collections.Generic;
using UnityEngine;

namespace CraiginsValheimMod.Dungeons
{
    /// <summary>
    /// Boss-kill gating for dungeon regeneration: you can't rebuild a Black Forest crypt until
    /// somebody has killed the Elder, a Sunken Crypt until Bonemass, a frost cave until Moder.
    ///
    /// WHY GLOBAL KEYS. A boss death runs, in Character.OnDeath:
    ///     ZoneSystem.instance.SetGlobalKey(m_defeatSetGlobalKey);
    /// which routes to the server, is stored in the world save, and is pushed to every client on
    /// connect (RPC_GlobalKeys). So the same question - "has this world beaten the Elder?" - has
    /// the same answer on the server, which decides, and on the client, which draws the hover
    /// text. Nothing extra has to be tracked or synced.
    ///
    /// This is a WORLD-wide gate, not a per-player one: one player's kill unlocks regeneration
    /// for everyone on the server. Vanilla also queues a per-player copy of the same key
    /// (Player.m_addUniqueKeyQueue, checked with Player.HaveUniqueKey), but only the local
    /// client can read its own - the server has no view of another player's unique keys - so a
    /// per-player gate would be a client-side honour system.
    ///
    /// WHY BOSSES ARE NAMED BY PREFAB, NOT BY KEY. The five original bosses have their keys in
    /// the GlobalKeys enum (defeated_gdking, defeated_bonemass, ...), but the Queen's and
    /// Fader's are not in the DLL at all - m_defeatSetGlobalKey is a serialized field on the
    /// boss prefab, so the string only exists in the asset bundle. Naming the boss prefab and
    /// reading its own key at runtime is right by construction for every boss, including
    /// whatever 1.0 adds, and gives us the boss's localized name for free. A token that doesn't
    /// resolve to a prefab is used as a raw global key, so arbitrary keys still work.
    ///
    /// WHICH BIOME A DUNGEON IS IN. ZoneSystem places a location only where
    /// WorldGenerator.GetBiome(point) is in that location's allowed biome mask
    /// (ZoneSystem.PlaceLocations), so asking the same function about the same point gives back
    /// the biome that allowed the placement. That's cheaper and more honest than reading the
    /// mask, which can list several biomes for one location type.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class DungeonProgression
    {
        /// <summary>One "this biome needs that boss dead" rule, with its lazily resolved boss.</summary>
        private sealed class Gate
        {
            public Heightmap.Biome Biome;

            /// <summary>Boss prefab name as configured, or a raw global key if it isn't one.</summary>
            public string Token;

            public string Key;
            public string Display;
        }

        /// <summary>The config string these gates were parsed from, so edits are picked up live.</summary>
        private static string _parsedFrom;
        private static readonly List<Gate> _gates = new List<Gate>();

        /// <summary>
        /// May this dungeon be rebuilt yet? <paramref name="requirement"/> names the boss still
        /// standing in the way when it may not.
        ///
        /// Fails OPEN: a gate naming something that resolves to neither a prefab nor a key is
        /// logged and skipped rather than locking a dungeon nobody can unlock. A typo in a
        /// server config shouldn't leave players holding a Surtling core at an entrance with no
        /// way to find out why.
        /// </summary>
        public static bool IsUnlocked(DungeonGenerator dungeon, out string requirement)
        {
            if (dungeon == null)
            {
                requirement = null;
                return true;
            }
            return IsUnlocked(dungeon.transform.position, out requirement);
        }

        /// <summary>
        /// The same question asked of a position rather than a generator, so the hover text can
        /// answer it from the entrance the player is looking at. Finding the DungeonGenerator
        /// instead would mean a scene-wide FindObjectsByType every frame of the hover.
        /// </summary>
        public static bool IsUnlocked(Vector3 position, out string requirement)
        {
            requirement = null;

            List<Gate> gates = Gates();
            if (gates.Count == 0 || ZoneSystem.instance == null)
            {
                return true;
            }

            Heightmap.Biome biome = BiomeOf(position);
            if (biome == Heightmap.Biome.None)
            {
                return true;
            }

            foreach (Gate gate in gates)
            {
                if ((gate.Biome & biome) == 0 || !Resolve(gate))
                {
                    continue;
                }
                if (!ZoneSystem.instance.GetGlobalKey(gate.Key))
                {
                    requirement = gate.Display;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The biome a dungeon stands in, given any position belonging to it - its entrance on
        /// the surface, or its generator 5000m up. The interior sits directly above the entrance
        /// in XZ and GetBiome(Vector3) reads x and z only, so the altitude never matters; the
        /// location instance is preferred anyway because it's the exact point placement tested.
        /// </summary>
        private static Heightmap.Biome BiomeOf(Vector3 position)
        {
            if (WorldGenerator.instance == null)
            {
                return Heightmap.Biome.None;
            }

            Vector2s zone = ZoneSystem.GetZone(position);
            if (ZoneSystem.instance != null
                && ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance))
            {
                position = instance.m_position;
            }

            return WorldGenerator.instance.GetBiome(position);
        }

        /// <summary>
        /// Turn a gate's configured token into the global key that answers it and a name to show
        /// the player. Resolution is cached on the gate once it succeeds: GetHoverText runs every
        /// frame the entrance is hovered, and ZNetScene isn't up yet the first time this is asked
        /// during a load.
        /// </summary>
        private static bool Resolve(Gate gate)
        {
            if (gate.Key != null)
            {
                return true;
            }

            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(gate.Token) : null;
            Character boss = prefab != null ? prefab.GetComponent<Character>() : null;

            if (boss != null && !string.IsNullOrEmpty(boss.m_defeatSetGlobalKey))
            {
                gate.Key = boss.m_defeatSetGlobalKey;
                gate.Display = Localization.instance != null
                    ? Localization.instance.Localize(boss.m_name)
                    : gate.Token;
                return true;
            }

            // Not a boss prefab. Either it's a raw global key, or ZNetScene isn't loaded yet and
            // we should look again next time - tell those apart by whether the world knows the
            // key, which is the same question the caller is about to ask.
            if (ZNetScene.instance == null)
            {
                return false;
            }

            if (prefab != null)
            {
                Jotunn.Logger.LogWarning(
                    $"Dungeon boss gate '{gate.Token}' is a prefab with no m_defeatSetGlobalKey - " +
                    "that isn't a boss. Treating it as a raw global key.");
            }

            gate.Key = gate.Token;
            gate.Display = gate.Token;
            return true;
        }

        /// <summary>
        /// Parse "BlackForest=gd_king, Swamp=Bonemass, ..." into gates, re-parsing only when the
        /// config string itself changes so a live edit in the BepInEx manager takes effect.
        /// </summary>
        private static List<Gate> Gates()
        {
            string configured = Plugin.DungeonResetBossGate.Value ?? "";
            if (configured == _parsedFrom)
            {
                return _gates;
            }
            _parsedFrom = configured;
            _gates.Clear();

            foreach (string entry in configured.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split('=');
                if (parts.Length != 2)
                {
                    Jotunn.Logger.LogWarning($"Dungeon boss gate '{entry.Trim()}' isn't 'Biome=BossPrefab'. Ignored.");
                    continue;
                }

                string biomeName = parts[0].Trim();
                string token = parts[1].Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                if (!Enum.TryParse(biomeName, ignoreCase: true, out Heightmap.Biome biome)
                    || biome == Heightmap.Biome.None)
                {
                    Jotunn.Logger.LogWarning(
                        $"Dungeon boss gate names an unknown biome '{biomeName}'. Expected one of: " +
                        "Meadows, BlackForest, Swamp, Mountain, Plains, Mistlands, AshLands, DeepNorth, Ocean.");
                    continue;
                }

                _gates.Add(new Gate { Biome = biome, Token = token });
            }

            return _gates;
        }
    }
}
