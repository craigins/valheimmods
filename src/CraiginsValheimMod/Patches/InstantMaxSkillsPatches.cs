using HarmonyLib;

namespace CraiginsValheimMod.Patches
{
    /// <summary>
    /// Any skill gain takes the skill straight to 100.
    ///
    /// Skills.Skill.Raise adds the gain to m_accumulator, and once it reaches the next level's
    /// requirement (0.5 * (level + 1)^1.5 + 0.5, 500.5 points for 99 to 100) it adds one level and
    /// zeroes the accumulator, throwing the rest away. So a bigger gain alone would still only give
    /// one level per action. Instead the prefix sets the level to 99 and fills the accumulator past
    /// the last requirement, and vanilla's own Raise takes it to 100 and returns true, so
    /// Skills.RaiseSkill still shows the level-up message and calls Player.OnSkillLevelup as usual.
    ///
    /// Skills you have never used stay at 0 until the first time they gain anything. Dying still
    /// takes away levels, and the next gain puts them back to 100.
    ///
    /// Runs on the player's own game, so it is per-player and the server doesn't need it.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal static class InstantMaxSkillsPatches
    {
        [HarmonyPatch(typeof(Skills.Skill), nameof(Skills.Skill.Raise))]
        private static class Skill_Raise_Patch
        {
            private static void Prefix(Skills.Skill __instance)
            {
                if (!Plugin.InstantMaxSkills.Value || __instance.m_level >= 100f)
                {
                    return;
                }
                __instance.m_level = 99f;
                __instance.m_accumulator = 1000f;
            }
        }
    }
}
