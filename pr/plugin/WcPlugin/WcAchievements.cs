using System;
using HarmonyLib;

namespace WorldsCollide.PR
{
    // No achievements from a Worlds Collide game (user, 2026-11-06): the game's achievement
    // manager (Last.Management.AchievementManager) doesn't record progress or unlock anything
    // while a WC seed is active or a WC save is loaded. Every progress update goes through
    // SetProgress; UpdateUserStatus / UpdateUseAbility gather it, UploadUserStats sends it to
    // Steam. Vanilla play (no seed, the vanilla or BNW mode) keeps its achievements.
    static class WcAchievements
    {
        internal static int Blocked;

        public static void Patch(Harmony h)
        {
            var t = typeof(global::Last.Management.AchievementManager);
            int n = 0;
            foreach (var name in new[] { "SetProgress", "UpdateUserStatus", "UpdateUseAbility", "UploadUserStats" })
            {
                var m = AccessTools.Method(t, name);
                if (m == null) continue;
                h.Patch(m, prefix: new HarmonyMethod(typeof(WcAchievements), nameof(Skip_Prefix)));
                n++;
            }
            Plugin.L.LogInfo($"[wc] achievements: {n} of 4 entry points hooked (none from a WC game)");
        }

        // read once a second (WcObjectives.Poll), not in the hooks: the game calls the manager while
        // its flag storage is still being set up (DataStorage's static constructor), and reading the
        // flags from there recursed until the game crashed
        static volatile bool inWcGame;

        internal static bool InWcGame() => inWcGame;

        internal static void Refresh()
        {
            if (!Plugin.WcMode) { inWcGame = false; return; }
            inWcGame = WcSeeds.Active != null ||
                       WcEvents.Flag(global::Last.Interpreter.DataStorage.Category.kScenarioFlag4, WcEvents.WcGameFlag) == 1;
        }

        public static bool Skip_Prefix()
        {
            try
            {
                if (!inWcGame) return true;
                if (Blocked++ == 0) Plugin.L.LogInfo("[wc] achievements: off in a WC game");
                return false;
            }
            catch (Exception) { return true; }
        }
    }
}
