using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;
using GenerationTracker = CraiginsValheimMod.Patches.DungeonPatches.GenerationTracker;

namespace CraiginsValheimMod.Dungeons
{
    /// <summary>
    /// Removes what MinDungeonRooms left behind in dungeons generated before 0.7.3.
    ///
    /// Up to 0.7.2 the reroll skipped destroying its discarded attempts in Ghost mode, the mode
    /// 'pregenerateworld' and a dedicated server's zone generation use (see DungeonPatches). A
    /// rerolled dungeon generated that way holds every attempt at once: stacked where attempts
    /// agree (the start room is identical in all of them), loose where they don't - a railing
    /// across a staircase, a barricade in a doorway that seems to come back when it's broken
    /// because an identical one is underneath.
    ///
    /// Runs once per dungeon when the server loads the world, while no zone is loaded anywhere.
    /// For each dungeon with stacked objects it replays generation beside the real one - a
    /// throwaway generator at the same position, in Ghost mode, with the same seed and the same
    /// reroll rules - and records every object each attempt creates. That gives, for every
    /// (prefab, position, rotation), how many the final layout should have and how many all the
    /// attempts together put there. Where the world holds more than the final layout's share and
    /// the extra is explained by discarded attempts, the extra is removed, keeping the copies
    /// that have been changed in play (an opened chest, a damaged wall) first. Everything else
    /// is left alone: creatures that have walked off, dropped items, player builds, and any
    /// object the replay can't account for.
    ///
    /// The replay is only trusted if it reproduces the saved room list exactly. If it doesn't -
    /// the reroll settings changed since the dungeon generated, most likely - the dungeon is
    /// marked and left as it is.
    ///
    /// Needs MinDungeonRooms and MaxDungeonRerolls set to what they were when the world was
    /// generated. Dungeons moved by the off-map plugin are skipped; this runs before that
    /// plugin's own world-load pass, so a world gets cleaned before its dungeons move.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.ServerLoadWorld))]
    internal static class DungeonRerollCleanup
    {
        /// <summary>On the generator: 1 = checked (and cleaned if needed), 2 = replay didn't match, left alone.</summary>
        private static readonly int StateKey = "cvm_reroll_cleanup".GetStableHashCode();
        private const int Checked = 1;
        private const int Mismatch = 2;

        /// <summary>Written by the off-map plugin on dungeons it has moved.</summary>
        private static readonly int OffMapExteriorKey = "cvm_offmap_exterior_pos".GetStableHashCode();

        [HarmonyPriority(Priority.High)]
        private static void Postfix()
        {
            if (ZNet.m_loadError || ZDOMan.instance == null || ZoneSystem.instance == null || ZNetScene.instance == null)
            {
                return;
            }
            if (Plugin.MinDungeonRooms.Value <= 0 || Plugin.MaxDungeonRerolls.Value <= 0)
            {
                return;
            }

            try
            {
                Run();
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogError($"Cleaning up rerolled dungeons failed: {e}");
            }
        }

        private static void Run()
        {
            var generators = new List<ZDO>();
            foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
            {
                if (zdo.GetPosition().y > DungeonInterior.InteriorFloor
                    && zdo.GetByteArray(ZDOVars.s_roomData) != null
                    && zdo.GetInt(StateKey) == 0)
                {
                    generators.Add(zdo);
                }
            }
            if (generators.Count == 0)
            {
                return;
            }

            Stopwatch timer = Stopwatch.StartNew();
            var sector = new List<ZDO>();
            int cleaned = 0, removed = 0, mismatched = 0, skipped = 0;
            foreach (ZDO generator in generators)
            {
                if (generator.GetVec3(OffMapExteriorKey, out _))
                {
                    skipped++;
                    continue;
                }
                int count = Process(generator, sector, out bool mismatch);
                if (mismatch)
                {
                    mismatched++;
                }
                else if (count > 0)
                {
                    cleaned++;
                    removed += count;
                }
            }

            Jotunn.Logger.LogInfo(
                $"Checked {generators.Count} dungeon(s) for leftover reroll attempts in {timer.Elapsed.TotalSeconds:F1} s: " +
                $"removed {removed} object(s) from {cleaned}, {mismatched} couldn't be replayed exactly and were left alone" +
                (skipped > 0 ? $", {skipped} already moved off the map were skipped." : "."));
        }

        /// <summary>Returns the number of objects removed.</summary>
        private static int Process(ZDO generator, List<ZDO> sector, out bool mismatch)
        {
            mismatch = false;
            Vector3 generatorPos = generator.GetPosition();

            sector.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(generatorPos), new SimulationDistance(0, 0), sector);

            var existing = new Dictionary<Key, List<ZDO>>();
            int stacked = 0;
            foreach (ZDO zdo in sector)
            {
                if (zdo == generator || !DungeonInterior.IsInside(generatorPos, zdo.GetPosition()) || zdo.GetLong(ZDOVars.s_creator, 0L) != 0L)
                {
                    continue;
                }
                Key key = Key.Of(zdo);
                if (!existing.TryGetValue(key, out List<ZDO> list))
                {
                    list = new List<ZDO>(1);
                    existing[key] = list;
                }
                list.Add(zdo);
                if (list.Count > 1)
                {
                    stacked++;
                }
            }

            // A rerolled dungeon always has stacks: every attempt starts from the same room in the
            // same place. None means it was never rerolled, or was generated after the fix.
            if (stacked == 0)
            {
                generator.Set(StateKey, Checked);
                return 0;
            }

            if (!Replay(generator, out byte[] rooms, out Dictionary<Key, int> final, out Dictionary<Key, int> all, out string error))
            {
                Jotunn.Logger.LogWarning($"Couldn't replay the dungeon at {generatorPos}: {error}");
                return 0;
            }
            if (!SameRooms(generator.GetByteArray(ZDOVars.s_roomData), rooms))
            {
                Jotunn.Logger.LogWarning(
                    $"Replaying the dungeon at {generatorPos} gave a different layout from the saved one, so it was left " +
                    "alone. MinDungeonRooms or MaxDungeonRerolls probably differ from when it was generated.");
                generator.Set(StateKey, Mismatch);
                mismatch = true;
                return 0;
            }

            var doomed = new List<ZDOID>();
            foreach (KeyValuePair<Key, List<ZDO>> group in existing)
            {
                final.TryGetValue(group.Key, out int keep);
                all.TryGetValue(group.Key, out int placed);
                int extra = Mathf.Min(group.Value.Count, placed) - keep;
                if (extra <= 0)
                {
                    continue;
                }
                List<ZDO> copies = group.Value;
                copies.Sort((a, b) => b.DataRevision.CompareTo(a.DataRevision));
                for (int i = copies.Count - extra; i < copies.Count; i++)
                {
                    doomed.Add(copies[i].m_uid);
                }
            }

            // Removed outright rather than through DestroyZDO, which only queues the ID for the
            // next ZDOMan.Update - the off-map plugin's pass runs straight after this one and
            // would otherwise count and move them. Nobody is connected yet, so there's no one to
            // tell; RemoveFromSector marks the save chunk dirty so the next save drops them.
            foreach (ZDOID uid in doomed)
            {
                ZDOMan.instance.HandleDestroyedZDO(uid);
            }
            generator.Set(StateKey, Checked);
            Jotunn.Logger.LogInfo($"Removed {doomed.Count} leftover object(s) from discarded reroll attempts in the dungeon at {generatorPos}.");
            return doomed.Count;
        }

        /// <summary>
        /// Generates the dungeon again, in Ghost mode, on a throwaway generator at the real one's
        /// position, and counts what the final layout and all attempts together create. The
        /// throwaway and everything it made are removed before returning.
        /// </summary>
        private static bool Replay(ZDO generator, out byte[] rooms, out Dictionary<Key, int> final, out Dictionary<Key, int> all, out string error)
        {
            rooms = null;
            final = new Dictionary<Key, int>();
            all = new Dictionary<Key, int>();
            error = null;

            GameObject prefab = ZNetScene.instance.GetPrefab(generator.GetPrefab());
            DungeonGenerator template = prefab != null ? prefab.GetComponent<DungeonGenerator>() : null;
            if (template == null)
            {
                error = $"no dungeon generator prefab for hash {generator.GetPrefab()}.";
                return false;
            }
            if (template.m_algorithm != DungeonGenerator.Algorithm.Dungeon)
            {
                error = $"{prefab.name} uses the {template.m_algorithm} algorithm, which is never rerolled.";
                return false;
            }

            Vector3 originalPosition = OriginalPosition(ZoneSystem.GetZone(generator.GetPosition()));
            var created = new List<ZDO>();
            var discarded = new HashSet<ZDO>();
            GameObject go = null;

            GenerationTracker.Recorder = created;
            GenerationTracker.OnDiscard = attempt => discarded.UnionWith(attempt);
            ZNetView.StartGhostInit();
            _replaying = true;
            try
            {
                go = Object.Instantiate(prefab, generator.GetPosition(), generator.GetRotation());
                DungeonGenerator dungeon = go.GetComponent<DungeonGenerator>();
                // SpawnLocation sets this before generating, and custom-interior dungeons
                // (Mörkhalla among them) place their bounds with it.
                dungeon.m_originalPosition = originalPosition;
                dungeon.m_hasGeneratedSeed = false;
                DungeonGenerator.m_forceSeed = int.MinValue;
                dungeon.Generate(ZoneSystem.SpawnMode.Ghost);

                ZDO own = dungeon.m_nview.GetZDO();
                rooms = own.GetByteArray(ZDOVars.s_roomData);
                // Discarded attempts are still readable here: DungeonInterior.Destroy only queues
                // them with ZDOMan.DestroyZDO, and nothing runs ZDOMan.Update in between.
                foreach (ZDO zdo in created)
                {
                    if (zdo == own)
                    {
                        continue;
                    }
                    Key key = Key.Of(zdo);
                    all.TryGetValue(key, out int n);
                    all[key] = n + 1;
                    if (!discarded.Contains(zdo))
                    {
                        final.TryGetValue(key, out n);
                        final[key] = n + 1;
                    }
                }
            }
            finally
            {
                _replaying = false;
                ZNetView.FinishGhostInit();
                GenerationTracker.Recorder = null;
                GenerationTracker.OnDiscard = null;

                var uids = new List<ZDOID>(created.Count);
                foreach (ZDO zdo in created)
                {
                    uids.Add(zdo.m_uid);
                }
                foreach (ZDOID uid in uids)
                {
                    ZDOMan.instance.HandleDestroyedZDO(uid);
                }
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            if (rooms == null)
            {
                error = "the replay saved no rooms.";
                return false;
            }
            return true;
        }

        private static bool _replaying;

        /// <summary>
        /// Ghost-mode generation throws each object away with Object.Destroy, which Unity only
        /// carries out at the end of the frame. That's fine for one dungeon, but every replay here
        /// runs in the same frame - a Mörkhalla alone is nine attempts of about 7,000 objects, torches
        /// with looping audio among them - and the first version hung the game with a quarter of a
        /// million of them alive at once ("Ran out of virtual channels"). During a replay they go
        /// immediately instead; both call sites are the last use of the object.
        /// </summary>
        public static void GhostDestroy(Object obj)
        {
            if (_replaying)
            {
                Object.DestroyImmediate(obj);
            }
            else
            {
                Object.Destroy(obj);
            }
        }

        [HarmonyPatch]
        private static class DungeonGenerator_GhostDestroy_Patch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(DungeonGenerator), "PlaceRoom",
                    new[] { typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion), typeof(RoomConnection), typeof(ZoneSystem.SpawnMode) });
                yield return AccessTools.Method(typeof(DungeonGenerator), "PlaceDoors");
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                MethodInfo destroy = AccessTools.Method(typeof(Object), nameof(Object.Destroy), new[] { typeof(Object) });
                MethodInfo ours = AccessTools.Method(typeof(DungeonRerollCleanup), nameof(GhostDestroy));
                int hits = 0;
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.Calls(destroy))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = ours;
                        hits++;
                    }
                    yield return instruction;
                }
                if (hits != 1)
                {
                    Jotunn.Logger.LogWarning($"DungeonGenerator.{original.Name} has {hits} Object.Destroy calls, expected 1.");
                }
            }
        }

        /// <summary>What ZoneSystem.SpawnLocation gives a custom-interior generator as m_originalPosition.</summary>
        private static Vector3 OriginalPosition(Vector2s zone)
        {
            if (!ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance)
                || instance.m_location == null || instance.m_location.m_prefab == null)
            {
                return Vector3.zero;
            }

            instance.m_location.m_prefab.Load();
            try
            {
                GameObject asset = instance.m_location.m_prefab.Asset;
                Location location = asset != null ? asset.GetComponent<Location>() : null;
                if (location != null && location.m_useCustomInteriorTransform && location.m_interiorTransform != null && location.m_generator != null)
                {
                    return location.m_generator.transform.localPosition;
                }
                return Vector3.zero;
            }
            finally
            {
                instance.m_location.m_prefab.Release();
            }
        }

        /// <summary>Same rooms in the same order, positions and rotations within 1 cm and 0.01 degrees.</summary>
        private static bool SameRooms(byte[] saved, byte[] replayed)
        {
            if (saved == null || replayed == null)
            {
                return false;
            }
            var a = new System.IO.BinaryReader(new System.IO.MemoryStream(saved));
            var b = new System.IO.BinaryReader(new System.IO.MemoryStream(replayed));
            int count = a.ReadInt32();
            if (count != b.ReadInt32())
            {
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                if (a.ReadInt32() != b.ReadInt32()
                    || Vector3.Distance(a.ReadVector3(), b.ReadVector3()) > 0.01f
                    || Quaternion.Angle(a.ReadQuaternion(), b.ReadQuaternion()) > 0.01f)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>An object's identity for matching: prefab, position to 1 cm, rotation to about 0.1 degree.</summary>
        private readonly struct Key : IEquatable<Key>
        {
            private readonly int _prefab, _x, _y, _z, _qx, _qy, _qz, _qw;

            private Key(int prefab, Vector3 p, Quaternion q)
            {
                // q and -q are the same rotation.
                float sign = q.w < 0f ? -1f : 1f;
                _prefab = prefab;
                _x = Mathf.RoundToInt(p.x * 100f);
                _y = Mathf.RoundToInt(p.y * 100f);
                _z = Mathf.RoundToInt(p.z * 100f);
                _qx = Mathf.RoundToInt(q.x * sign * 1000f);
                _qy = Mathf.RoundToInt(q.y * sign * 1000f);
                _qz = Mathf.RoundToInt(q.z * sign * 1000f);
                _qw = Mathf.RoundToInt(q.w * sign * 1000f);
            }

            public static Key Of(ZDO zdo)
            {
                return new Key(zdo.GetPrefab(), zdo.GetPosition(), zdo.GetRotation());
            }

            public bool Equals(Key o)
            {
                return _prefab == o._prefab && _x == o._x && _y == o._y && _z == o._z
                    && _qx == o._qx && _qy == o._qy && _qz == o._qz && _qw == o._qw;
            }

            public override bool Equals(object obj)
            {
                return obj is Key k && Equals(k);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = _prefab;
                    h = h * 397 ^ _x;
                    h = h * 397 ^ _y;
                    h = h * 397 ^ _z;
                    h = h * 397 ^ _qx;
                    h = h * 397 ^ _qy;
                    h = h * 397 ^ _qz;
                    h = h * 397 ^ _qw;
                    return h;
                }
            }
        }
    }
}
