using System;
using System.Globalization;
using HarmonyLib;
using Last.Battle;
using Last.Management;
using Last.Systems;

namespace WorldsCollide.PR
{
    // WC's reward multipliers (Experience, Magic Points, Gold: -xpm / -mpm / -gpm, assembly in
    // WC): battle EXP and gil when the result is built (BattleResultProvider.Genelate), magic
    // points where the battle totals them (BattleAddExpPlug.GetAp). From the active seed's flags.
    // -nxppd (both presets): every survivor gets the battle's whole EXP, not a share (the PR splits
    // it between the party, BattleResultData.DistributionExpCount, before working out level-ups:
    // the total is multiplied by that count first).
    static class WcRewards
    {
        static string flagsFor;
        static int xp = 1, mp = 1, gp = 1;
        static bool noDivide;
        internal static string LastSplit = "";

        public static void Patch(Harmony h)
        {
            var gen = AccessTools.Method(typeof(BattleResultProvider), "Genelate");
            if (gen != null) h.Patch(gen, prefix: new HarmonyMethod(typeof(WcRewards), nameof(Result_Prefix)));
            var ap = AccessTools.Method(typeof(BattleAddExpPlug), "GetAp");
            if (ap != null) h.Patch(ap, postfix: new HarmonyMethod(typeof(WcRewards), nameof(Ap_Postfix)));
            Plugin.L.LogInfo($"[wc] rewards: result {(gen != null ? "hooked" : "not found")}, magic points {(ap != null ? "hooked" : "not found")}");
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

        public static void Result_Prefix(Il2CppSystem.Collections.Generic.IEnumerable<global::Last.Data.User.OwnedCharacterData> targets, ref int expValue, ref int gilValue)
        {
            if (!Plugin.WcMode || WcSeeds.Active == null) return;
            try
            {
                Refresh();
                if (xp != 1) expValue = (int)Math.Min(int.MaxValue / 2, (long)expValue * xp);
                if (noDivide)
                {
                    int n = Sharers(targets);
                    if (n > 1) expValue = (int)Math.Min(int.MaxValue / 2, (long)expValue * n);
                    LastSplit = $"EXP x{n} for {n} sharers";
                }
                if (gp != 1) gilValue = (int)Math.Min(int.MaxValue / 2, (long)gilValue * gp);
            }
            catch (Exception e) { WcRunner.WarnOnce("rewards", e); }
        }

        public static void Ap_Postfix(ref int __result)
        {
            if (!Plugin.WcMode || WcSeeds.Active == null) return;
            try
            {
                Refresh();
                if (mp != 1) __result = (int)Math.Min(int.MaxValue / 2, (long)__result * mp);
            }
            catch (Exception e) { WcRunner.WarnOnce("ap", e); }
        }
    }
}
