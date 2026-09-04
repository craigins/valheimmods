using HarmonyLib;

namespace CraiginsValheimMod.WorldGen
{
    /// <summary>
    /// Stops vanilla's own speculative zone pregeneration while 'pregenerateworld' is running.
    ///
    /// ZoneSystem.Update calls CreateGhostZones every frame for the local reference position and
    /// every connected peer, which walks a square around each of them and ghost-generates the
    /// first not-yet-generated zone it finds - in raster order from the square's corner. That
    /// races the pregenerator's deliberate centre-outward ordering, and the loser is whatever
    /// unique location (Haldor's camp, etc.) gets claimed by a corner zone before the spiral
    /// reaches its nearer candidate. See the ordering notes in WorldPregenerator.
    ///
    /// Suppressing it costs nothing: ghost zones are pure lookahead with no gameplay effect, and
    /// while the pregenerator runs we're already generating the entire map anyway. Its bool
    /// return is discarded by the only caller (it just gates whether to try the next peer), so
    /// returning false is equivalent to "found nothing to pregenerate here".
    ///
    /// Note this deliberately leaves CreateLocalZones alone - that one spawns the live zones
    /// players actually stand on, so blocking it would drop anyone connected through the world.
    /// WorldPregenerator warns at startup if anyone is out there to be affected.
    /// </summary>
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.CreateGhostZones))]
    internal static class GhostZoneSuppressionPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!WorldPregenerator.IsRunning)
            {
                return true;
            }

            __result = false;
            return false;
        }
    }
}
