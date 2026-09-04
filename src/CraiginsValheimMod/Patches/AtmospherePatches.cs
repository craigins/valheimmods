using System.Collections.Generic;
using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Mist removal, ported from my older ValheimNoMist mod (ValheimNoMistMain.cs -
    /// the "RemoveMist" toggle: MisterOnEnable_Patch, MisterGet_Patch,
    /// MisterGetDemistersSorted_Patch, MistEmitterUpdate_Patch). Verified against the current
    /// assembly_valheim.dll - Mister.OnEnable/GetMisters/GetDemistersSorted/m_height/m_radius
    /// and MistEmitter.Update/SetEmit all still match exactly, ported unchanged.
    ///
    /// This fully removes ground mist rather than just thinning it (that's what the old code
    /// did too, despite the "opacity" framing) - drop the "1f -" below or scale m_height/
    /// m_radius instead of zeroing them if you want a partial reduction rather than an
    /// on/off toggle.
    ///
    /// No trace of a "wisp light radius" patch was found anywhere in the archived sources
    /// (source repos, decompiled reference project, or built plugin DLLs) - only this mist toggle and
    /// an unrelated "Death Recorder" mod. If you built that one before, it either wasn't saved
    /// separately or is somewhere I didn't find - happy to help re-locate it, or we can build
    /// it fresh once you point at the wisp light prefab/script name.
    /// </summary>
    internal static class AtmospherePatches
    {
        [HarmonyPatch(typeof(Mister), "OnEnable")]
        private static class MisterOnEnable_Patch
        {
            private static bool Prefix(Mister __instance)
            {
                if (!Plugin.RemoveMistlandsFog.Value)
                {
                    return true;
                }

                __instance.m_height = 0f;
                __instance.m_radius = 0f;
                return false;
            }
        }

        [HarmonyPatch(typeof(Mister), nameof(Mister.GetMisters))]
        private static class MisterGet_Patch
        {
            private static void Postfix(ref List<Mister> __result)
            {
                if (Plugin.RemoveMistlandsFog.Value)
                {
                    __result?.Clear();
                }
            }
        }

        [HarmonyPatch(typeof(Mister), nameof(Mister.GetDemistersSorted))]
        private static class MisterGetDemistersSorted_Patch
        {
            private static void Postfix(ref List<Mister> __result)
            {
                if (Plugin.RemoveMistlandsFog.Value)
                {
                    __result?.Clear();
                }
            }
        }

        [HarmonyPatch(typeof(MistEmitter), "Update")]
        private static class MistEmitterUpdate_Patch
        {
            private static bool Prefix(MistEmitter __instance)
            {
                if (Plugin.RemoveMistlandsFog.Value)
                {
                    __instance.SetEmit(false);
                }
                return true;
            }
        }

        // TODO: wisp light radius - no existing implementation found. Once you can point at
        // the wisp prefab/light component name (or we find it together via the decompiler),
        // this is a straightforward Light.range/intensity postfix patch.
    }
}
