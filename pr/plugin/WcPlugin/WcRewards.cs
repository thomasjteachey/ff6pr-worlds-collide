using System;
using System.Globalization;
using HarmonyLib;
using Last.Battle;
using Last.Management;
using Last.Systems;
using PrKit.Seeds;

namespace WorldsCollide.PR
{
    // WC's reward multipliers (Experience, Magic Points, Gold: -xpm / -mpm / -gpm; WC battle/multipliers.py:
    // each enemy's EXP / gold times the value, after enemy scaling). Where the PR pays a won battle's rewards
    // (BattleController.StartWinResult, tools/decomp/bnw_batchc; tools/decomp/genelate; callers from
    // tools/ghidra/callers.py, 2026-11-21):
    //  gil: SaveGil pays the wallet from BattleAddExpPlug.GetGil; StartWinResult calls GetGil again for the
    //       result (BattleResultProvider.Genelate: the screen's number, nothing paid). Those are GetGil's only
    //       two callers, so -gpm multiplies GetGil's answer (after WcScaling's -xg postfix: Priority.Low) and
    //       the wallet and the screen get the same amount, once. (Genelate's gilValue used to be multiplied:
    //       the screen showed -gpm, the wallet got the plain amount.)
    //  EXP: StartWinResult hands GetExp's answer (its only caller) to Genelate, which pays it (each sharer's
    //       AdditionalExp, then OwnedCharacterClient.SetOwnedCharacter) and shows it: -xpm multiplies
    //       expValue there.
    //  magic points: StartWinResult builds BattleSkillUpInformation(AP = MonsterParty.GetAp, MagicPoint =
    //       MonsterParty.GetValue) and hands it to Genelate, which reads MagicPoint twice: once at its start
    //       for the result's GetMp (the screen's number), which it adds to each sharer's MagicLearningValue
    //       (the morph gauge), and once in each sharer's StatusUpProvider.Execution (an interface call; tools/
    //       decomp/genelate, statusup; Execution decompiled 2026-11-21), which adds learn rate x MagicPoint to
    //       each spell of the equipped esper (OwnedAbility.SkillLevel, to 100). WC (multipliers.py
    //       set_mp_multiplier): the battle's magic points x -mpm, at most 255; the morph gauge gets that / -mpm
    //       (the plain amount unless the 255 cap cut it). So -mpm sets MagicPoint to the morph's amount for
    //       Genelate's own read, to the multiplied amount while each Execution runs, puts it back after, and
    //       the result shows the multiplied amount. (BattleAddExpPlug.GetAp, hooked until 2026-11-21, has no
    //       callers.) -mpm 0: no magic points, the morph gauge as without the flag.
    // -nxppd (both presets): every survivor gets the battle's whole EXP, not a share (the PR splits
    // it between the party, BattleResultData.DistributionExpCount, before working out level-ups:
    // the total is multiplied by that count first).
    static class WcRewards
    {
        static string flagsFor;
        static int xp = 1, mp = 1, gp = 1;
        static bool noDivide;
        internal static string LastSplit = "";
        // the last battle's numbers, for the self-test (WcTest reward_wallet): GetGil's answer before and after
        // -gpm, Genelate's EXP in and out (-xpm, -nxppd) and what its result shows
        internal static int LastGilBase = -1, LastGilPaid = -1, LastExpBase = -1, LastExpPaid = -1, LastSharers = -1,
                            LastResultGil = -1, LastResultExp = -1;
        // (WcTest reward_mp) the battle's magic points, what each esper learning got (and how many times),
        // what the morph gauge's read got, and what the result shows
        internal static int LastMpBase = -1, LastMpPaid = -1, LastMpMorph = -1, LastMpPaidTimes = 0, LastResultMp = -1;
        // Genelate in progress with this battle's BattleSkillUpInformation (its Execution calls get -mpm)
        static BattleSkillUpInformation mpInfo;
        static int mpBase, mpPaid;

        public static void Patch(Harmony h)
        {
            var gen = AccessTools.Method(typeof(BattleResultProvider), "Genelate");
            if (gen != null) h.Patch(gen, prefix: new HarmonyMethod(typeof(WcRewards), nameof(Result_Prefix)),
                                     postfix: new HarmonyMethod(typeof(WcRewards), nameof(Result_Postfix)));
            var gil = AccessTools.Method(typeof(BattleAddExpPlug), "GetGil");
            if (gil != null) h.Patch(gil, postfix: new HarmonyMethod(typeof(WcRewards), nameof(Gil_Postfix)) { priority = Priority.Low });
            var learn = AccessTools.Method(typeof(StatusUpProvider), "Execution");
            if (learn != null) h.Patch(learn, prefix: new HarmonyMethod(typeof(WcRewards), nameof(Learning_Prefix)),
                                       postfix: new HarmonyMethod(typeof(WcRewards), nameof(Learning_Postfix)));
            Plugin.L.LogInfo($"[wc] rewards: result {(gen != null ? "hooked" : "not found")}, gil {(gil != null ? "hooked" : "not found")}, " +
                             $"esper learning {(learn != null ? "hooked" : "not found")}");
        }

        static void Refresh()
        {
            var flags = WcSeeds.Active?.Flags ?? "";
            if (flags == flagsFor) return;
            flagsFor = flags;
            xp = mp = gp = 1;
            var v = new FlagValues(WcScreen.Defs);
            v.Parse(flags);
            int Get(string flag) => v.IsOn(flag) && v.Values(flag).Count > 0 && int.TryParse(v.Values(flag)[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 1;
            xp = Get("-xpm");
            mp = Get("-mpm");
            gp = Get("-gpm");
            noDivide = v.IsOn("-nxppd");
            Plugin.L.LogInfo($"[wc] reward multipliers: EXP x{xp}, magic points x{mp}, gil x{gp}{(noDivide ? ", EXP not split" : "")}");
        }

        // the seed's -gpm / -xpm (1 outside a WC game), for the self-test's expected amounts
        internal static int GilMultiplier { get { if (!Plugin.WcMode || WcSeeds.Active == null) return 1; Refresh(); return gp; } }
        internal static int ExpMultiplier { get { if (!Plugin.WcMode || WcSeeds.Active == null) return 1; Refresh(); return xp; } }
        internal static bool ExpNotSplit { get { if (!Plugin.WcMode || WcSeeds.Active == null) return false; Refresh(); return noDivide; } }
        internal static int MpMultiplier { get { if (!Plugin.WcMode || WcSeeds.Active == null) return 1; Refresh(); return mp; } }

        // WC's magic points for a battle's base amount b with -mpm m (multipliers.py: b x m, at most 255; the
        // flag left out (1): the game's amount) and what the morph gauge gets (that / m; -mpm 0: the plain amount)
        internal static int MpPaid(int b, int m) => m == 1 ? b : m <= 0 || b <= 0 ? 0 : (int)Math.Min(255L, (long)b * m);
        internal static int MpMorph(int b, int m) => m == 1 || m <= 0 ? b : MpPaid(b, m) / m;

        // the characters the PR splits a battle's EXP between: the result's targets (the battle
        // passes the ones still standing; Genelate divides by their count, DistributionExpCount)
        internal static int Sharers(Il2CppSystem.Collections.Generic.IEnumerable<global::Last.Data.User.OwnedCharacterData> targets)
        {
            if (targets == null) return 0;
            var list = targets.TryCast<Il2CppSystem.Collections.Generic.List<global::Last.Data.User.OwnedCharacterData>>();
            if (list != null) return list.Count;
            // (not a list: the party members standing, as the battle picks them)
            int n = 0;
            var party = UserDataManager.Instance()?.GetPartyMemberData();
            if (party != null) foreach (var c in party) if (c?.Parameter != null && c.Parameter.CurrentHP > 0) n++;
            return n;
        }

        // EXP (Genelate pays it) and -mpm's magic points. Its gilValue is GetGil's answer, already multiplied (Gil_Postfix).
        public static void Result_Prefix(Il2CppSystem.Collections.Generic.IEnumerable<global::Last.Data.User.OwnedCharacterData> targets, ref int expValue,
                                         BattleSkillUpInformation battleSkillUpInformation)
        {
            mpInfo = null;
            if (!Plugin.WcMode || WcSeeds.Active == null) return;
            try
            {
                Refresh();
                LastMpBase = LastMpPaid = LastMpMorph = LastResultMp = -1;
                LastMpPaidTimes = 0;
                if (battleSkillUpInformation != null)
                {
                    mpBase = battleSkillUpInformation.MagicPoint;
                    mpPaid = MpPaid(mpBase, mp);
                    LastMpBase = mpBase;
                    LastMpMorph = MpMorph(mpBase, mp);
                    // Genelate's own read (the result's GetMp, the morph gauge): WC's morph amount
                    if (LastMpMorph != mpBase) battleSkillUpInformation.MagicPoint = LastMpMorph;
                    mpInfo = battleSkillUpInformation;
                }
            }
            catch (Exception e) { mpInfo = null; WcRunner.WarnOnce("rewards-mp", e); }
            try
            {
                Refresh();
                LastExpBase = expValue;
                LastSharers = -1;
                if (xp != 1) expValue = (int)Math.Min(int.MaxValue / 2, (long)expValue * xp);
                if (noDivide)
                {
                    int n = Sharers(targets);
                    LastSharers = n;
                    if (n > 1) expValue = (int)Math.Min(int.MaxValue / 2, (long)expValue * n);
                    LastSplit = $"EXP x{n} for {n} sharers";
                }
                LastExpPaid = expValue;
            }
            catch (Exception e) { WcRunner.WarnOnce("rewards", e); }
        }

        public static void Result_Postfix(global::Last.Data.BattleResultData __result)
        {
            var info = mpInfo;
            mpInfo = null;
            try
            {
                // put the battle's own amount back; the result shows what the espers' learning got
                if (info != null)
                {
                    if (info.MagicPoint != mpBase) info.MagicPoint = mpBase;
                    if (__result != null && mpPaid != LastMpMorph)
                    {
                        var cheat = CheatSettingsClient.Instance();
                        __result.GetMp = cheat != null ? cheat.GetResultMagicProficiency(mpPaid) : mpPaid;
                    }
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("rewards-mp-result", e); }
            if (!Plugin.WcMode || __result == null) return;
            try
            {
                LastResultGil = __result.GetGil;
                LastResultExp = __result.GetExp;
                LastResultMp = __result.GetMp;
            }
            catch (Exception e) { WcRunner.WarnOnce("rewards-result", e); }
        }

        // -mpm: each sharer's esper learning (StatusUpProvider.Execution, called by Genelate for each one,
        // reads MagicPoint once) gets the multiplied amount; put back after, for the next read
        public static void Learning_Prefix(BattleSkillUpInformation battleSkillUpInformation, out int __state)
        {
            __state = int.MinValue;
            var info = mpInfo;
            if (info == null || battleSkillUpInformation == null || battleSkillUpInformation.Pointer != info.Pointer) return;
            try
            {
                __state = battleSkillUpInformation.MagicPoint;
                if (__state != mpPaid) battleSkillUpInformation.MagicPoint = mpPaid;
                LastMpPaid = mpPaid;
                LastMpPaidTimes++;
            }
            catch (Exception e) { __state = int.MinValue; WcRunner.WarnOnce("rewards-mp-learning", e); }
        }

        public static void Learning_Postfix(BattleSkillUpInformation battleSkillUpInformation, int __state)
        {
            if (__state == int.MinValue || battleSkillUpInformation == null) return;
            try { if (battleSkillUpInformation.MagicPoint != __state) battleSkillUpInformation.MagicPoint = __state; }
            catch (Exception e) { WcRunner.WarnOnce("rewards-mp-learning-back", e); }
        }

        // -gpm: the battle's gil as both of GetGil's callers get it (the wallet's SaveGil, the result's)
        public static void Gil_Postfix(ref int __result)
        {
            if (!Plugin.WcMode || WcSeeds.Active == null) return;
            try
            {
                Refresh();
                LastGilBase = __result;
                if (gp != 1) __result = (int)Math.Min(int.MaxValue / 2, (long)__result * gp);
                LastGilPaid = __result;
            }
            catch (Exception e) { WcRunner.WarnOnce("gil", e); }
        }
    }
}
