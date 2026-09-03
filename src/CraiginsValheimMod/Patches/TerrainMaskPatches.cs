using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Black Forest terrain generation uses a rougher/craggier height mask than Mistlands.
    /// Goal: make Black Forest terrain gen borrow the (smoother) Mistlands mask so the biome
    /// is more traversable.
    ///
    /// Not implemented yet — you mentioned you've built this before, so this is left as a stub
    /// rather than a guess. To re-find the patch target:
    ///   1. Build once (`dotnet build`) so Assembly-CSharp gets publicized into obj/.
    ///   2. Open that publicized DLL in ILSpy/dnSpy and search for "WorldGenerator" and
    ///      "Heightmap.Biome.BlackForest" to find the current height/mask sampling method
    ///      for the relevant game version.
    ///   3. Add a [HarmonyPatch] here targeting that method (prefix/postfix/transpiler as needed).
    /// </summary>
    // [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight))]
    internal static class TerrainMaskPatches
    {
        // TODO: port your existing Black Forest -> Mistlands mask patch here.
    }
}
