using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Dungeons
{
    /// <summary>
    /// Keeps the room geometry a machine has drawn for a dungeon in step with the layout saved
    /// in the generator's ZDO, so a regenerated dungeon appears at once instead of after the
    /// player has walked far enough away for the generator to be destroyed and re-created.
    ///
    /// WHY THIS IS NEEDED. A dungeon has two halves that get to a client by different routes.
    /// Its networked contents - chests, spawners, doors, pickables - are ZDOs, so a reset's
    /// destroy-and-recreate reaches every client within a frame. Its room shells are not: they
    /// are plain instantiated prefabs that each machine places for itself in
    /// DungeonGenerator.Awake (Load -> LoadRoomPrefabsAsync -> Spawn -> PlaceRoom in Client
    /// mode), from the room list in ZDOVars.s_roomData, and nothing ever re-reads that list.
    /// A client that already had the generator loaded - anyone standing at the entrance, which
    /// is exactly who asks for a reset - therefore keeps the OLD shells around the NEW contents:
    /// chests floating where the old layout had no platform, bones on a wall that isn't there.
    ///
    /// The generator's ZDO is in the client's active area, so the rewritten room list arrives
    /// as a normal ZDO update; its DataRevision changes with it. A cheap once-a-second poll for
    /// that (the same way Door watches its own state) is enough to notice, and the rebuild is
    /// vanilla's own load path re-run: Clear() drops the old shells, Load() re-reads the list,
    /// LoadRoomPrefabsAsync() places the new ones once the prefabs are in. The room-data bytes
    /// are compared before rebuilding because DataRevision also moves for unrelated writes to
    /// the same ZDO (ownership, the seed cleanup Save() does), and a needless rebuild would
    /// flicker every wall around the player.
    ///
    /// Attached to every DungeonGenerator as it wakes. It does nothing until room data changes,
    /// which only a reset does, so it costs one ZDO field read per second per loaded dungeon.
    /// </summary>
    internal sealed class DungeonShellRefresh : MonoBehaviour
    {
        private const float PollInterval = 1f;

        private DungeonGenerator _dungeon;
        private ZNetView _nview;
        private uint _revision;
        private byte[] _roomData;

        [HarmonyPatch(typeof(DungeonGenerator), "Awake")]
        private static class DungeonGenerator_Awake_Patch
        {
            private static void Postfix(DungeonGenerator __instance)
            {
                if (__instance.GetComponent<DungeonShellRefresh>() == null)
                {
                    __instance.gameObject.AddComponent<DungeonShellRefresh>();
                }
            }
        }

        private void Awake()
        {
            _dungeon = GetComponent<DungeonGenerator>();
            _nview = GetComponent<ZNetView>();
            Snapshot();
            InvokeRepeating(nameof(Poll), PollInterval, PollInterval);
        }

        /// <summary>
        /// Record the layout currently drawn as the one to compare against. Execute() calls this
        /// after rebuilding a live generator, whose shells are already the new ones; without it
        /// the host would tear down and redraw the rooms it just placed.
        /// </summary>
        public static void MarkCurrent(DungeonGenerator dungeon)
        {
            DungeonShellRefresh refresh = dungeon != null ? dungeon.GetComponent<DungeonShellRefresh>() : null;
            if (refresh != null)
            {
                refresh.Snapshot();
            }
        }

        private void Snapshot()
        {
            ZDO zdo = _nview != null ? _nview.GetZDO() : null;
            if (zdo == null)
            {
                return;
            }
            _revision = zdo.DataRevision;
            zdo.GetByteArray(ZDOVars.s_roomData, out _roomData);
        }

        private void Poll()
        {
            if (_dungeon == null || _nview == null || !_nview.IsValid())
            {
                return;
            }

            ZDO zdo = _nview.GetZDO();
            if (zdo.DataRevision == _revision)
            {
                return;
            }
            _revision = zdo.DataRevision;

            zdo.GetByteArray(ZDOVars.s_roomData, out byte[] data);
            if (SameBytes(data, _roomData))
            {
                return;
            }
            _roomData = data;

            Rebuild();
        }

        private void Rebuild()
        {
            Jotunn.Logger.LogInfo($"Dungeon layout changed for {DungeonReset.Describe(_dungeon)} - redrawing its rooms.");

            _dungeon.Clear();
            // Drop any room prefab references and the loading-in-zone mark from the previous
            // load before starting another, exactly as OnDestroy would.
            _dungeon.ReleaseHeldReferences();
            _dungeon.Load();
            if (_dungeon.m_loadedRooms != null && _dungeon.m_loadedRooms.Length != 0)
            {
                _dungeon.LoadRoomPrefabsAsync();
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
