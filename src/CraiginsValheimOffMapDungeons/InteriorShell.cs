using UnityEngine;

namespace CraiginsValheimMod.OffMap
{
    /// <summary>
    /// The parts of an interior that vanilla hangs off the entrance, rebuilt beside a moved interior.
    ///
    /// A location's interior is not only its networked generator. Two more things come from the
    /// location prefab itself and are built client-side by the LocationProxy in the ENTRANCE's zone:
    ///
    ///   - the exit teleport (ExteriorGateway on Mörkhalla), and
    ///   - the Location component, whose Awake builds the interior's EnvZone (the "Morkhalla"
    ///     environment) and which vanilla looks up by zone for the no-build check, creature spawner
    ///     rules and dungeon material variants.
    ///
    /// Once the interior is in another zone, the entrance's zone unloads the moment anyone goes in,
    /// and both of those go with it. So each moved generator gets a shell: a stand-in Location
    /// positioned exactly as the real one would be relative to the interior (shifted by the same
    /// whole zones), and a copy of the exit teleport, both destroyed with the generator.
    ///
    /// Built wherever the generator is instantiated - clients, a host, and a dedicated server when a
    /// player is near - because the server runs creature spawners too and they consult the Location.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class InteriorShell
    {
        public static void Ensure(DungeonGenerator generator)
        {
            if (generator == null || generator.m_nview == null || generator.GetComponent<ShellLink>() != null)
            {
                return;
            }
            ZDO zdo = generator.m_nview.GetZDO();
            Vector3 exteriorPos;
            Quaternion exteriorRot;
            int locationHash;
            if (zdo == null
                || !zdo.GetVec3(OffMapKeys.ExteriorPos, out exteriorPos)
                || !zdo.GetQuaternion(OffMapKeys.ExteriorRot, out exteriorRot)
                || !zdo.GetInt(OffMapKeys.Location, out locationHash)
                || ZoneSystem.instance == null)
            {
                return;
            }

            ZoneSystem.ZoneLocation zoneLocation = ZoneSystem.instance.GetLocation(locationHash);
            if (zoneLocation == null || zoneLocation.m_prefab == null)
            {
                Jotunn.Logger.LogWarning($"Moved dungeon at {generator.transform.position} names location {locationHash}, which this game doesn't have.");
                return;
            }

            zoneLocation.m_prefab.Load();
            try
            {
                Build(generator, zoneLocation.m_prefab.Asset, exteriorPos, exteriorRot);
            }
            finally
            {
                zoneLocation.m_prefab.Release();
            }
        }

        private static void Build(DungeonGenerator generator, GameObject asset, Vector3 exteriorPos, Quaternion exteriorRot)
        {
            Location template = asset != null ? asset.GetComponent<Location>() : null;
            if (template == null || template.m_interiorTransform == null || template.m_generator == null)
            {
                return;
            }

            Teleport exitTemplate = null;
            foreach (Teleport t in template.m_interiorTransform.GetComponentsInChildren<Teleport>(true))
            {
                exitTemplate = t;
                break;
            }
            Teleport entranceTemplate = null;
            if (exitTemplate != null)
            {
                foreach (Teleport t in asset.GetComponentsInChildren<Teleport>(true))
                {
                    if (t.m_targetPoint == exitTemplate)
                    {
                        entranceTemplate = t;
                        break;
                    }
                }
            }

            Vector2s interiorZone = ZoneSystem.GetZone(generator.transform.position);
            Vector2s exteriorZone = ZoneSystem.GetZone(exteriorPos);
            Vector3 shift = OffMapRegion.Shift(exteriorZone, interiorZone);

            GameObject shell = new GameObject("OffMapInteriorShell");
            shell.SetActive(false);
            shell.transform.position = exteriorPos + shift;
            shell.transform.rotation = exteriorRot;

            // The stand-in. Location.Awake runs on SetActive below, with these fields already set, and
            // builds the EnvZone at this zone's centre, 5000 above the entrance's height - exactly
            // where vanilla would have, one shift over.
            Location standIn = shell.AddComponent<Location>();
            standIn.m_exteriorRadius = template.m_exteriorRadius;
            standIn.m_noBuild = template.m_noBuild;
            standIn.m_noBuildRadiusOverride = template.m_noBuildRadiusOverride;
            standIn.m_clearArea = false;
            standIn.m_discoverLabel = template.m_discoverLabel;
            standIn.m_applyRandomDamage = template.m_applyRandomDamage;
            standIn.m_hasInterior = template.m_hasInterior;
            standIn.m_interiorRadius = template.m_interiorRadius;
            standIn.m_interiorEnvironment = template.m_interiorEnvironment;
            standIn.m_interiorPrefab = template.m_interiorPrefab;
            standIn.m_interiorTransform = null;
            standIn.m_useCustomInteriorTransform = false;
            standIn.m_generator = generator;
            standIn.m_enemyMinLevelOverride = template.m_enemyMinLevelOverride;
            standIn.m_enemyMaxLevelOverride = template.m_enemyMaxLevelOverride;
            standIn.m_enemyLevelUpOverride = template.m_enemyLevelUpOverride;
            standIn.m_excludeEnemyLevelOverrideGroups = template.m_excludeEnemyLevelOverrideGroups;
            standIn.m_blockSpawnGroups = template.m_blockSpawnGroups;
            // MaterialVariationWorld would otherwise look up the biome at the stand-in's own position,
            // which is open ocean past the world's edge.
            standIn.m_biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(exteriorPos) : template.m_biome;

            if (exitTemplate != null && entranceTemplate != null)
            {
                BuildExit(shell, generator, template, exitTemplate, entranceTemplate, asset.transform, exteriorPos, exteriorRot);
            }
            else
            {
                Jotunn.Logger.LogWarning($"{asset.name} has no exit/entrance teleport pair - the moved interior at {generator.transform.position} has no exit.");
            }

            shell.SetActive(true);
            generator.gameObject.AddComponent<ShellLink>().Shell = shell;
        }

        private static void BuildExit(GameObject shell, DungeonGenerator generator, Location template,
            Teleport exitTemplate, Teleport entranceTemplate, Transform root, Vector3 exteriorPos, Quaternion exteriorRot)
        {
            // Where the exit sits relative to the generator. SpawnLocation zeroes the generator's local
            // position inside the interior transform before placing either, so the generator is the
            // interior's origin with its own local rotation, and the exit keeps its offset from that.
            Transform interior = template.m_interiorTransform;
            Quaternion generatorLocalRot = Quaternion.Inverse(interior.rotation) * template.m_generator.transform.rotation;
            Vector3 exitInInterior = interior.InverseTransformPoint(exitTemplate.transform.position);
            Quaternion exitRotInInterior = Quaternion.Inverse(interior.rotation) * exitTemplate.transform.rotation;
            Quaternion toGenerator = Quaternion.Inverse(generatorLocalRot);

            Vector3 exitPos = generator.transform.position + generator.transform.rotation * (toGenerator * exitInInterior);
            Quaternion exitRot = generator.transform.rotation * (toGenerator * exitRotInInterior);

            // Where the entrance sits relative to the location's root, placed at the real entrance.
            Vector3 entranceLocal = root.InverseTransformPoint(entranceTemplate.transform.position);
            Quaternion entranceLocalRot = Quaternion.Inverse(root.rotation) * entranceTemplate.transform.rotation;
            Vector3 entrancePos = exteriorPos + exteriorRot * entranceLocal;
            Quaternion entranceRot = exteriorRot * entranceLocalRot;

            GameObject exit = Object.Instantiate(exitTemplate.gameObject, exitPos, exitRot, shell.transform);
            exit.name = "OffMapExit";
            Teleport teleport = exit.GetComponent<Teleport>();
            // Points into the prefab asset, not the world. OffMapTeleportPatches routes this exit by
            // its ShellExit instead.
            teleport.m_targetPoint = null;

            ShellExit target = exit.AddComponent<ShellExit>();
            // Same arrival point vanilla uses: Teleport.GetTeleportPoint, one step out of the doorway.
            target.Destination = entrancePos + entranceRot * Vector3.forward - entranceRot * Vector3.up;
            target.Rotation = entranceRot;
        }
    }

    /// <summary>Where a shell's exit leads. Read by OffMapTeleportPatches.</summary>
    internal class ShellExit : MonoBehaviour
    {
        public Vector3 Destination;
        public Quaternion Rotation;
    }

    /// <summary>
    /// Ties a shell's lifetime to its generator. A component rather than a child object, because
    /// DungeonGenerator.Clear() destroys every child of the generator.
    /// </summary>
    internal class ShellLink : MonoBehaviour
    {
        public GameObject Shell;

        private void OnDestroy()
        {
            if (Shell != null)
            {
                Object.Destroy(Shell);
            }
        }
    }
}
