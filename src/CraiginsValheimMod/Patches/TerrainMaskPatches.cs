using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Ported from an older working ValheimNoMist mod of mine (ValheimNoMistMain.cs,
    /// class WorldGeneratorGetMistlandsHeight_Patch). Verified against the current game's
    /// assembly_valheim.dll (2026-02-19 build) - WorldGenerator.GetMistlandsHeight,
    /// GetBaseHeight, AddRivers and the m_offset3 field all still match exactly.
    ///
    /// Replaces Mistlands' own craggy height/mask generation with the same base-height +
    /// river computation the smoother biomes use, so Mistlands is less jagged/more
    /// traversable.
    ///
    /// One change from the original: the old code called a custom `DUtils.PerlinNoise`/
    /// `DUtils.Clamp01` double-precision helper that no longer exists in this game version
    /// (checked - not present anywhere in assembly_valheim.dll). Swapped in UnityEngine's own
    /// `Mathf.PerlinNoise`/`Mathf.Clamp01` (float precision) instead, which is what that
    /// helper almost certainly wrapped in the first place. Everything else is unchanged.
    ///
    /// Not yet tested in-game - toggle via the "SmoothMistlandsTerrain" config setting if it
    /// looks wrong and you want to fall back to vanilla generation.
    /// </summary>
    [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetMistlandsHeight))]
    internal static class TerrainMaskPatches
    {
        private static readonly MethodInfo GetBaseHeight =
            typeof(WorldGenerator).GetMethod("GetBaseHeight", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo AddRivers =
            typeof(WorldGenerator).GetMethod("AddRivers", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo Offset3 =
            typeof(WorldGenerator).GetField("m_offset3", BindingFlags.NonPublic | BindingFlags.Instance);

        private static bool Prefix(WorldGenerator __instance, ref float __result, float wx, float wy, out Color mask)
        {
            if (!Plugin.SmoothMistlandsTerrain.Value)
            {
                mask = default;
                return true;
            }

            float offset3 = (float)Offset3.GetValue(__instance);
            float baseHeight = (float)GetBaseHeight.Invoke(__instance, new object[] { wx, wy, false });

            float wx2 = wx;
            float wy2 = wy;
            wx += 100000f + offset3;
            wy += 100000f + offset3;

            float n = Mathf.PerlinNoise(wx * 0.01f, wy * 0.01f) * Mathf.PerlinNoise(wx * 0.02f, wy * 0.02f);
            n += Mathf.PerlinNoise(wx * 0.05f, wy * 0.05f) * Mathf.PerlinNoise(wx * 0.1f, wy * 0.1f) * n * 0.5f;
            baseHeight += n * 0.1f;

            baseHeight = (float)AddRivers.Invoke(__instance, new object[] { wx2, wy2, baseHeight });
            baseHeight += Mathf.PerlinNoise(wx * 0.1f, wy * 0.1f) * 0.01f;
            __result = baseHeight + Mathf.PerlinNoise(wx * 0.4f, wy * 0.4f) * 0.003f;

            n = n > 0f ? (float)Math.Pow(n, 1.5) : n;
            float clamped = Mathf.Clamp01(n * 7f);
            float alpha = 1f - clamped * 1.2f;
            mask = new Color(0f, 0f, 0f, alpha);
            return false;
        }
    }
}
