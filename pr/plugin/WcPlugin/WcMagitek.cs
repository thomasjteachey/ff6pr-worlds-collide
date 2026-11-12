using System;
using System.Linq;
using HarmonyLib;

namespace WorldsCollide.PR
{
    // WC's -com can deal MagiTek as a normal command (its random commands do). The PR's Magitek
    // (command 26) is the armor's: listed and usable only in Magitek armor (release_type 3,
    // condition 53; CommandAvailableData special-cases it), and every character keeps it as a
    // hidden slot. A seed made since 2026-11-06 names the characters WC dealt it to
    // (wc_seed.json "magitek"; the port gives them one Magitek slot, not the hidden one too):
    // for them, in a WC game, it is listed and usable anywhere, with the moves they own (the
    // PR's 4 basic beams; Terra's 4 more after "Magitek Upgrade", WcObjectives). Older seeds
    // name nobody and stay as they were.
    static class WcMagitek
    {
        internal const int Command = 26;

        public static void Patch(Harmony h)
        {
            var t = typeof(Serial.FF6.Management.CommandAvailableData);
            int n = 0;
            foreach (var (name, post) in new[] { ("IsCommandRelease", nameof(Release_Postfix)), ("IsAvailable", nameof(Available_Postfix)) })
            {
                var m = AccessTools.Method(t, name);
                if (m == null) continue;
                h.Patch(m, postfix: new HarmonyMethod(typeof(WcMagitek), post));
                n++;
            }
            Plugin.L.LogInfo($"[wc] MagiTek: {n} of 2 command checks hooked");
        }

        internal static bool Dealt(int characterStatusId)
        {
            var s = WcSeeds.Active;
            return Plugin.WcMode && s != null && s.Magitek.Contains(characterStatusId) &&
                   WcEvents.Flag(Last.Interpreter.DataStorage.Category.kScenarioFlag4, WcEvents.WcGameFlag) == 1;
        }

        public static void Release_Postfix(global::Last.Data.User.OwnedCharacterData targetData, global::Last.Data.Master.Command command, ref bool __result)
        {
            try
            {
                if (!__result && command != null && command.Id == Command && targetData != null && Dealt(targetData.characterStatusId)) __result = true;
            }
            catch (Exception e) { WcRunner.WarnOnce("magitek-release", e); }
        }

        public static void Available_Postfix(global::Last.Data.User.OwnedCharacterData targetData, global::Last.Data.Master.Command command, ref bool __result)
        {
            try
            {
                if (!__result && command != null && command.Id == Command && targetData != null && Dealt(targetData.characterStatusId)) __result = true;
            }
            catch (Exception e) { WcRunner.WarnOnce("magitek-available", e); }
        }
    }
}
