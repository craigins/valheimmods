using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Two small visibility tweaks:
    ///   - Increase wisp light radius (the will-o'-the-wisp light source in Mistlands/Swamp).
    ///   - Reduce Mistlands fog/mist opacity.
    ///
    /// Not implemented yet — left as a stub for you to port your existing patch into. To
    /// re-find the current targets:
    ///   - Wisp light: search the publicized Assembly-CSharp (see TerrainMaskPatches.cs for how
    ///     to publicize) for "Wisplight" or the prefab's light component setup.
    ///   - Mistlands fog: look at EnvSetup/EnvironmentManager for the Mistlands biome's fog
    ///     density/color fields.
    /// </summary>
    internal static class AtmospherePatches
    {
        // TODO: port your existing wisp light radius patch here.

        // TODO: port your existing Mistlands fog opacity patch here.
    }
}
