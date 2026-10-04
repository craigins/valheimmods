using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Re-summons a boss aspect when Kall Fimbulbringer's second phase stalls.
    ///
    /// The boss has no script of its own. Phase 1 (FrozenKing) dies and its death effects
    /// spawn the ice crystal (FrozenKing_p2) plus a projectile. Every aspect's death effects
    /// fire the next projectile, in two branches off Eikthyr:
    ///   Eikthyr -> Elder -> Moder -> SeekerQueen
    ///   Eikthyr -> Bonemass -> Yagluth -> Fader
    /// A projectile falls from the dead aspect and, where it lands, makes a FrozenKing_P2_Spawn_X:
    /// a bare SpawnAbility with no ZNetView, alive only on the game that owned the dead aspect.
    /// It waits 3 s, snaps a point within 5 m to the ground from 10 m above, and gives up without
    /// a word if that point is over 100 m off. It never retries.
    ///
    /// The crystal has 7000 health, is immune to every damage type, never regenerates and is
    /// saved with the world. The only thing that hurts it is aspect_aoe_explosion, which every
    /// aspect leaves on death: 1000 m_nonPlayer damage in 130 m. So all seven aspects must die,
    /// and one lost link leaves the crystal stuck for good. Seen 2026-10-03: Fader never came.
    ///
    /// WATCHDOG. A component on each crystal, run by the crystal's ZDO owner (normally the
    /// nearest player). Once a second it checks whether the fight has stalled: crystal alive,
    /// health unchanged, a player within 100 m and no living aspect within 200 m. After
    /// KallAspectWatchdogSeconds of that (default 30, longer than a projectile's 20 s life plus
    /// the 3 s spawn delay) it spawns the missing aspects that start a branch, and their own
    /// deaths carry the chain on as vanilla does. Each needs to be killed as normal.
    ///
    /// WHICH ARE MISSING. Health says how many: one per 1000 left. To say which, each aspect's
    /// death is sent to the crystal's owner by RPC and kept as a bitmask in the crystal's ZDO.
    /// When the mask doesn't add up with the health - a crystal from before this mod, as with
    /// the stuck one, or a death on a game without the mod - the deepest aspects are assumed
    /// missing, Fader first. Either way the count always matches the health, so the crystal
    /// can't be overkilled into leaving extra aspects behind in phase 3.
    ///
    /// The aspect appears between the nearest player and the crystal, with the same summon
    /// effects as vanilla, read off the aspect's own projectile prefab.
    ///
    /// Everyone fighting Kall needs the mod, since whoever owns the crystal runs the watchdog
    /// and whoever owns an aspect reports its death. NOT TESTED IN-GAME.
    /// </summary>
    internal static class KallWatchdogPatches
    {
        internal static readonly string[] Aspects =
        {
            "Aspect_Eikthyr", "Aspect_Elder", "Aspect_Bonemass", "Aspect_Moder",
            "Aspect_Yagluth", "Aspect_SeekerQueen", "Aspect_Fader",
        };

        // The aspect whose death summons each one, by index into Aspects. -1: summoned by phase 1.
        internal static readonly int[] Predecessor = { -1, 0, 0, 1, 2, 3, 4 };

        // Which projectile summons each aspect, for its summon effects.
        internal static readonly string[] Projectiles =
        {
            "FrozenKing_P2_Projectile_Eikthyr", "FrozenKing_P2_Projectile_Elder",
            "FrozenKing_P2_Projectile_Bonemass", "FrozenKing_P2_Projectile_Moder",
            "FrozenKing_P2_Projectile_Yagluth", "FrozenKing_P2_Projectile_Queen",
            "FrozenKing_P2_Projectile_Fader",
        };

        // Order to assume aspects are missing in when the death record can't be trusted:
        // the ends of each branch first, since a chain breaks from where it stopped onwards.
        internal static readonly int[] AssumedMissingOrder = { 6, 5, 4, 3, 2, 1, 0 };

        internal const float DamagePerAspect = 1000f;

        private static readonly int CrystalHash = "FrozenKing_p2".GetStableHashCode();
        private static readonly int[] AspectHashes = BuildHashes();

        private static int[] BuildHashes()
        {
            int[] hashes = new int[Aspects.Length];
            for (int i = 0; i < Aspects.Length; i++)
            {
                hashes[i] = Aspects[i].GetStableHashCode();
            }
            return hashes;
        }

        internal static int AspectIndex(int prefabHash)
        {
            for (int i = 0; i < AspectHashes.Length; i++)
            {
                if (AspectHashes[i] == prefabHash)
                {
                    return i;
                }
            }
            return -1;
        }

        [HarmonyPatch(typeof(Character), "Awake")]
        private static class Character_Awake_Patch
        {
            private static void Postfix(Character __instance)
            {
                ZNetView nview = __instance.m_nview;
                if (nview == null || !nview.IsValid() || nview.GetZDO().GetPrefab() != CrystalHash)
                {
                    return;
                }
                __instance.gameObject.AddComponent<KallWatchdog>();
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
        private static class Character_OnDeath_Patch
        {
            private static void Prefix(Character __instance)
            {
                ZNetView nview = __instance.m_nview;
                if (nview == null || !nview.IsValid() || !nview.IsOwner())
                {
                    return;
                }
                int index = AspectIndex(nview.GetZDO().GetPrefab());
                if (index >= 0)
                {
                    KallWatchdog.ReportAspectDeath(__instance.transform.position, index);
                }
            }
        }
    }

    internal class KallWatchdog : MonoBehaviour
    {
        private const string RpcAspectDied = "cvm_KallAspectDied";
        private static readonly int DeadAspectsKey = "cvm_kall_dead_aspects".GetStableHashCode();

        private const float TickSeconds = 1f;
        private const float PlayerRange = 100f;
        private const float AspectRange = 200f;

        private static readonly List<KallWatchdog> s_instances = new List<KallWatchdog>();

        private Character m_character;
        private ZNetView m_nview;
        private float m_tickTimer;
        private float m_stalledSeconds;
        private float m_lastHealth = -1f;

        private void Awake()
        {
            m_character = GetComponent<Character>();
            m_nview = GetComponent<ZNetView>();
            m_nview.Register<int>(RpcAspectDied, RPC_AspectDied);
            s_instances.Add(this);
        }

        private void OnDestroy()
        {
            s_instances.Remove(this);
        }

        /// <summary>Sends an aspect's death to the owner of the nearest crystal.</summary>
        internal static void ReportAspectDeath(Vector3 position, int index)
        {
            KallWatchdog nearest = null;
            float nearestDistance = AspectRange;
            foreach (KallWatchdog watchdog in s_instances)
            {
                if (watchdog == null || !watchdog.m_nview.IsValid())
                {
                    continue;
                }
                float distance = Vector3.Distance(position, watchdog.transform.position);
                if (distance < nearestDistance)
                {
                    nearest = watchdog;
                    nearestDistance = distance;
                }
            }
            nearest?.m_nview.InvokeRPC(RpcAspectDied, index);
        }

        private void RPC_AspectDied(long sender, int index)
        {
            if (!m_nview.IsValid() || !m_nview.IsOwner() || index < 0 || index >= KallWatchdogPatches.Aspects.Length)
            {
                return;
            }
            ZDO zdo = m_nview.GetZDO();
            zdo.Set(DeadAspectsKey, zdo.GetInt(DeadAspectsKey) | (1 << index));
        }

        private void Update()
        {
            m_tickTimer += Time.deltaTime;
            if (m_tickTimer < TickSeconds)
            {
                return;
            }
            float elapsed = m_tickTimer;
            m_tickTimer = 0f;

            if (!Plugin.KallAspectWatchdog.Value || !m_nview.IsValid() || !m_nview.IsOwner()
                || m_character.IsDead())
            {
                m_stalledSeconds = 0f;
                return;
            }

            float health = m_character.GetHealth();
            if (health <= 0f || health != m_lastHealth)
            {
                m_lastHealth = health;
                m_stalledSeconds = 0f;
                return;
            }

            Player player = Player.GetClosestPlayer(transform.position, PlayerRange);
            if (player == null || AnyAspectAlive())
            {
                m_stalledSeconds = 0f;
                return;
            }

            m_stalledSeconds += elapsed;
            if (m_stalledSeconds < Plugin.KallAspectWatchdogSeconds.Value)
            {
                return;
            }
            m_stalledSeconds = 0f;
            Resummon(health, player);
        }

        private bool AnyAspectAlive()
        {
            foreach (BaseAI ai in BaseAI.BaseAIInstances)
            {
                Character character = ai.m_character;
                if (character == null || character.IsDead() || !ai.m_nview.IsValid())
                {
                    continue;
                }
                if (KallWatchdogPatches.AspectIndex(ai.m_nview.GetZDO().GetPrefab()) >= 0
                    && Vector3.Distance(character.transform.position, transform.position) < AspectRange)
                {
                    return true;
                }
            }
            return false;
        }

        private void Resummon(float health, Player player)
        {
            int count = KallWatchdogPatches.Aspects.Length;
            int remaining = Mathf.Clamp(Mathf.CeilToInt(health / KallWatchdogPatches.DamagePerAspect - 0.01f), 1, count);
            int dead = m_nview.GetZDO().GetInt(DeadAspectsKey);

            bool[] missing = new bool[count];
            int recordedMissing = 0;
            for (int i = 0; i < count; i++)
            {
                missing[i] = (dead & (1 << i)) == 0;
                if (missing[i])
                {
                    recordedMissing++;
                }
            }

            if (recordedMissing != remaining)
            {
                // The record doesn't match the health. Assume the deepest aspects are the missing
                // ones, preferring ones not recorded dead, so the count matches the health exactly.
                bool[] assumed = new bool[count];
                int chosen = 0;
                foreach (bool preferUnrecorded in new[] { true, false })
                {
                    foreach (int i in KallWatchdogPatches.AssumedMissingOrder)
                    {
                        if (chosen < remaining && !assumed[i] && (!preferUnrecorded || missing[i]))
                        {
                            assumed[i] = true;
                            chosen++;
                        }
                    }
                }
                missing = assumed;
            }

            // Summon only the first missing aspect of each branch; its death summons the rest.
            List<int> toSummon = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int predecessor = KallWatchdogPatches.Predecessor[i];
                if (missing[i] && (predecessor < 0 || !missing[predecessor]))
                {
                    toSummon.Add(i);
                }
            }

            for (int k = 0; k < toSummon.Count; k++)
            {
                float sideOffset = (k - (toSummon.Count - 1) * 0.5f) * 4f;
                Summon(toSummon[k], player, sideOffset);
            }
        }

        private void Summon(int index, Player player, float sideOffset)
        {
            string name = KallWatchdogPatches.Aspects[index];
            GameObject prefab = ZNetScene.instance.GetPrefab(name);
            if (prefab == null)
            {
                Jotunn.Logger.LogWarning($"Kall watchdog: no prefab named {name}, can't re-summon it.");
                return;
            }

            // Between the player and the crystal - the open floor they're fighting on - rather
            // than beside the player, where a wall or pillar could be.
            Vector3 playerPos = player.transform.position;
            Vector3 toCrystal = transform.position - playerPos;
            toCrystal.y = 0f;
            float distance = toCrystal.magnitude;
            Vector3 direction = distance > 0.1f ? toCrystal / distance : player.transform.forward;
            Vector3 side = Vector3.Cross(Vector3.up, direction);
            Vector3 point = playerPos + direction * Mathf.Min(6f, distance * 0.5f) + side * sideOffset;
            point.y = playerPos.y;
            if (ZoneSystem.instance.GetSolidHeight(point, out float height, 2) && Mathf.Abs(height - playerPos.y) < 3f)
            {
                point.y = height;
            }
            point.y += 0.2f;

            GameObject aspect = Object.Instantiate(prefab, point, Quaternion.LookRotation(-direction));
            aspect.GetComponent<BaseAI>()?.Alert();
            PlaySummonEffects(index, point);
            Jotunn.Logger.LogInfo($"Kall watchdog: phase 2 stalled at {m_character.GetHealth():0} health, re-summoned {name}.");
        }

        private static void PlaySummonEffects(int index, Vector3 point)
        {
            GameObject projectilePrefab = ZNetScene.instance.GetPrefab(KallWatchdogPatches.Projectiles[index]);
            Projectile projectile = projectilePrefab != null ? projectilePrefab.GetComponent<Projectile>() : null;
            if (projectile == null)
            {
                return;
            }
            projectile.m_hitEffects.Create(point, Quaternion.identity);
            SpawnAbility spawner = projectile.m_spawnOnHit != null ? projectile.m_spawnOnHit.GetComponent<SpawnAbility>() : null;
            spawner?.m_spawnEffects.Create(point, Quaternion.identity);
        }
    }
}
