using System;
using HarmonyLib;
using Last.Battle;
using Last.Systems;
using Random = System.Random;

namespace WorldsCollide.PR
{
    // WC's Steal flags (data/steal.py, assembly in WC), on CalcControllerProvider.GetStealStatus:
    //  -sch "higher": StealValue = Level + 90 - target Level (vanilla's constant is 50); fails at
    //       0 or less, always works at 128+, else works if StealValue > random(0..99); the rare
    //       item 3/8 of the time (vanilla 1/8)
    //  -sca "always": always steals when the enemy has an item; rare 3/8 when it has both
    // Not done: the Thief Glove doubling under -sch.
    static class WcSteal
    {
        static readonly Random Rng = new Random();
        static string flagsFor;
        static int mode;  // 0 off, 1 higher, 2 always

        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(CalcControllerProvider), "GetStealStatus");
            if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(WcSteal), nameof(Steal_Postfix)));
            Plugin.L.LogInfo($"[wc] steal: {(m != null ? "hooked" : "not found")}");
        }

        static void Refresh()
        {
            var flags = WcSeeds.Active?.Flags ?? "";
            if (flags == flagsFor) return;
            flagsFor = flags;
            var v = new FlagValues(WcScreen.Defs);
            v.Parse(flags);
            mode = v.IsOn("-sca") ? 2 : v.IsOn("-sch") ? 1 : 0;
        }

        public static void Steal_Postfix(CalcControllerProvider __instance, ICalcResult __result)
        {
            if (!Plugin.WcMode || WcSeeds.Active == null || __result == null || __instance == null) return;
            try
            {
                Refresh();
                if (mode == 0 || __instance.userCharaData == null || __instance.attack == null) return;
                var monster = __instance.calcInfo?.targetMonster;
                if (monster == null) return;
                int common = monster.StealContentId1, rare = monster.StealContentId2;
                if (common == 0 && rare == 0) return;
                var res = __result.TryCast<CalcResult>();
                if (res == null) return;
                bool success;
                if (mode == 2) success = true;
                else
                {
                    int value = __instance.attack.ConfirmedLevel() + 90 - (__instance.target?.ConfirmedLevel() ?? 0);
                    success = value > 0 && (value >= 128 || value > Rng.Next(100));
                }
                int slot = rare != 0 && (common == 0 || Rng.Next(256) < 96) ? 2 : 1;
                __result.SetStatus(__result.GetValue(false), success ? HitType.Hit : HitType.Non, __result.GetHitCount(), __result.GetConditions(), false);
                res.SetStealContentId(success ? slot : 0);
            }
            catch (Exception e) { WcRunner.WarnOnce("steal", e); }
        }
    }
}
