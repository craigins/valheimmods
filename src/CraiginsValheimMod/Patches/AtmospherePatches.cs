using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

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
    /// FixedMistlandsFog is the companion: the ground mist above is a separate system from
    /// the weather fog (EnvSetup.m_fogDensity*, which EnvMan writes to RenderSettings every
    /// FixedUpdate), so with the mist gone Mistlands is left with its three weathers'
    /// modest distance fog (Mistlands_clear/rain/thunder: 0.04/0.05/0.02-0.03/0.04 at
    /// night/morning/day/evening, vs. Meadows "Misty" at 0.15/0.10/0.02/0.10). This pins
    /// every Mistlands weather to one density for all times of day, default 0.2 - the same
    /// value Sunken Crypts use. Rain and thunder still roll as normal; only their fog
    /// density changes.
    ///
    /// Those weathers are not in EnvMan's own list - ZoneSystem.SetupLocations appends them
    /// from _LocationList_Mistlands via EnvMan.AppendBiomeSetup, so that's where they're
    /// intercepted. Vanilla values are remembered so the setting can be turned off or
    /// re-tuned in-game without a restart.
    ///
    /// WispLightClearsFog: an equipped Wisplight applies the SE_Demister status effect to the
    /// wearer (that's what spawns the following wisp ball and its ParticleSystemForceField).
    /// While the local player has that effect, the weather fog density EnvMan just computed
    /// in SetEnv is faded to zero, in any biome. The fade avoids a visible pop on equip/unequip.
    /// Only the equippable counts - placed wisp torches are Demister components with no
    /// status effect, so they don't trigger this.
    ///
    /// AshlandsBrightness: the Ashlands are dark because their four weathers are lit dimly, not
    /// because of fog or m_alwaysDark (which is off on all four). Against Meadows "Clear" the
    /// sun colour is about half as bright (0.52/0.38/0.31 vs 1.0/0.77/0.48 by day), the night
    /// light intensity is 0.4-0.6 instead of 1.0, and night ambient is ~0.30 luminance instead
    /// of 0.37. Net, the usual weather (Ashlands_ashrain) gets about two thirds of Meadows'
    /// direct sunlight and about a third of its moonlight - full table in
    /// docs/GAME_CONSTANTS.md. The setting is one multiplier over each
    /// weather's ambient colour, sun/moon light intensity and fog colours, day and night, so
    /// the whole scene moves together like an exposure change and the hue stays Ashlands red.
    /// Fog colour is included on purpose: brightening only the lights leaves a lit foreground
    /// against a haze that is still dark. Fog density, clouds and weather effects are left
    /// alone. 1 is vanilla. Caught in the same place and kept reversible the same way as the
    /// Mistlands fog above.
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

        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.AppendBiomeSetup))]
        private static class EnvManAppendBiomeSetup_Patch
        {
            private static void Postfix(BiomeEnvSetup biomeEnv)
            {
                if (biomeEnv.m_biome == Heightmap.Biome.Mistlands)
                {
                    foreach (EnvEntry entry in biomeEnv.m_environments)
                    {
                        MistlandsFog.Track(entry.m_env);
                    }
                    MistlandsFog.Apply();
                }
                else if (biomeEnv.m_biome == Heightmap.Biome.AshLands)
                {
                    foreach (EnvEntry entry in biomeEnv.m_environments)
                    {
                        AshlandsLight.Track(entry.m_env);
                    }
                    AshlandsLight.Apply();
                }
            }
        }

        /// <summary>
        /// The Mistlands weather EnvSetups and their vanilla fog densities
        /// (night, morning, day, evening), so the override is reversible at runtime.
        /// </summary>
        internal static class MistlandsFog
        {
            private static readonly Dictionary<EnvSetup, float[]> Tracked = new Dictionary<EnvSetup, float[]>();

            public static void Track(EnvSetup env)
            {
                if (env == null || Tracked.ContainsKey(env))
                {
                    return;
                }
                Tracked[env] = new[]
                {
                    env.m_fogDensityNight, env.m_fogDensityMorning, env.m_fogDensityDay, env.m_fogDensityEvening
                };
            }

            /// <summary>Re-applies the current config to every tracked weather. Safe to call any time.</summary>
            public static void Apply()
            {
                bool on = Plugin.FixedMistlandsFog.Value;
                float density = Mathf.Max(0f, Plugin.MistlandsFogDensity.Value);
                foreach (KeyValuePair<EnvSetup, float[]> pair in Tracked)
                {
                    EnvSetup env = pair.Key;
                    float[] vanilla = pair.Value;
                    env.m_fogDensityNight = on ? density : vanilla[0];
                    env.m_fogDensityMorning = on ? density : vanilla[1];
                    env.m_fogDensityDay = on ? density : vanilla[2];
                    env.m_fogDensityEvening = on ? density : vanilla[3];
                }
            }
        }

        /// <summary>
        /// The Ashlands weather EnvSetups and their vanilla lighting, so the brightness
        /// multiplier is always applied to the original values and is reversible at runtime.
        /// </summary>
        internal static class AshlandsLight
        {
            private sealed class Vanilla
            {
                public Color AmbNight, AmbDay;
                public float LightDay, LightNight;
                public Color FogNight, FogMorning, FogDay, FogEvening;
                public Color FogSunNight, FogSunMorning, FogSunDay, FogSunEvening;
            }

            private static readonly Dictionary<EnvSetup, Vanilla> Tracked = new Dictionary<EnvSetup, Vanilla>();

            public static void Track(EnvSetup env)
            {
                if (env == null || Tracked.ContainsKey(env))
                {
                    return;
                }
                Tracked[env] = new Vanilla
                {
                    AmbNight = env.m_ambColorNight,
                    AmbDay = env.m_ambColorDay,
                    LightDay = env.m_lightIntensityDay,
                    LightNight = env.m_lightIntensityNight,
                    FogNight = env.m_fogColorNight,
                    FogMorning = env.m_fogColorMorning,
                    FogDay = env.m_fogColorDay,
                    FogEvening = env.m_fogColorEvening,
                    FogSunNight = env.m_fogColorSunNight,
                    FogSunMorning = env.m_fogColorSunMorning,
                    FogSunDay = env.m_fogColorSunDay,
                    FogSunEvening = env.m_fogColorSunEvening,
                };
            }

            /// <summary>Re-applies the current config to every tracked weather. Safe to call any time.</summary>
            public static void Apply()
            {
                float b = Mathf.Max(0f, Plugin.AshlandsBrightness.Value);
                foreach (KeyValuePair<EnvSetup, Vanilla> pair in Tracked)
                {
                    EnvSetup env = pair.Key;
                    Vanilla v = pair.Value;
                    env.m_ambColorNight = Scale(v.AmbNight, b);
                    env.m_ambColorDay = Scale(v.AmbDay, b);
                    env.m_lightIntensityDay = v.LightDay * b;
                    env.m_lightIntensityNight = v.LightNight * b;
                    env.m_fogColorNight = Scale(v.FogNight, b);
                    env.m_fogColorMorning = Scale(v.FogMorning, b);
                    env.m_fogColorDay = Scale(v.FogDay, b);
                    env.m_fogColorEvening = Scale(v.FogEvening, b);
                    env.m_fogColorSunNight = Scale(v.FogSunNight, b);
                    env.m_fogColorSunMorning = Scale(v.FogSunMorning, b);
                    env.m_fogColorSunDay = Scale(v.FogSunDay, b);
                    env.m_fogColorSunEvening = Scale(v.FogSunEvening, b);
                }
            }

            /// <summary>Scales the colour channels only; Color * float would scale alpha too.</summary>
            private static Color Scale(Color c, float b)
            {
                return new Color(c.r * b, c.g * b, c.b * b, c.a);
            }
        }

        [HarmonyPatch(typeof(EnvMan), "SetEnv")]
        private static class EnvManSetEnv_Patch
        {
            /// <summary>0 = vanilla fog, 1 = fully cleared. Moves toward its target once per SetEnv call.</summary>
            private static float s_clear;

            private static void Postfix(float dt)
            {
                float target = Plugin.WispLightClearsFog.Value && LocalPlayerHasWispLight() ? 1f : 0f;
                float fade = Mathf.Max(0.01f, Plugin.WispLightFogFadeSeconds.Value);
                s_clear = Mathf.MoveTowards(s_clear, target, dt / fade);
                if (s_clear > 0f)
                {
                    RenderSettings.fogDensity *= 1f - s_clear;
                }
            }

            private static bool LocalPlayerHasWispLight()
            {
                Player player = Player.m_localPlayer;
                if (player == null)
                {
                    return false;
                }
                foreach (StatusEffect se in player.GetSEMan().GetStatusEffects())
                {
                    if (se is SE_Demister)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        // TODO: wisp light radius - no existing implementation found. Once you can point at
        // the wisp prefab/light component name (or we find it together via the decompiler),
        // this is a straightforward Light.range/intensity postfix patch.
    }
}
