// The reward_wallet check's plan (2026-11-21): what a won battle pays with -gpm / -xpm.
using System;
using System.Collections.Generic;
using System.Linq;
using Last.Battle;
using Last.Data.Master;
using Last.Management;
using UnityEngine;

namespace WorldsCollide.PR
{
    static partial class WcTest
    {
        // -gpm / -xpm (WcRewards): a won battle's gil reaches the wallet multiplied, once, and the result screen
        // shows the same amount; its EXP reaches the characters multiplied (and whole with -nxppd). An ordinary
        // party (formation 7 if it gives gil and EXP and has no battle flags, else the first such), the party kept
        // alive on auto, the monsters at 1 HP. Checked: the wallet's gain = GetGil's amount (the game's, with WC's
        // -xg scaling) x -gpm; the screen's gil = the wallet's gain; each standing character's EXP gain = GetExp's
        // amount x -xpm (/ the standing count without -nxppd). The PR's own Boost settings left at x1 and nobody
        // with an EXP / gil relic, as on a new game.
        static List<(float, Func<bool>)> Plan_reward_wallet(string name)
        {
            int party = 0, winsAt = 0, gilBefore = 0, lost = 0;
            bool won = false;
            var expBefore = new Dictionary<int, int>();
            bool Plain(MonsterParty p)
            {
                if (p == null || p.BattleFlagGroupId > 0) return false;
                var ms = new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6 }.Where(m => m > 0)
                    .Select(m => MasterManager.Instance.GetData<Monster>(m)).ToList();
                return ms.Count > 0 && ms.All(m => m != null && m.Gill > 0 && m.Exp > 0);
            }
            Dictionary<int, global::Last.Data.User.OwnedCharacterData> Owned()
            {
                var d = new Dictionary<int, global::Last.Data.User.OwnedCharacterData>();
                foreach (var c in UserDataManager.Instance().OwnedCharacterList) if (c != null) d[c.Id] = c;
                return d;
            }
            bool Report()
            {
                recorder = null;
                var udm = UserDataManager.Instance();
                int gp = WcRewards.GilMultiplier, xp = WcRewards.ExpMultiplier;
                bool whole = WcRewards.ExpNotSplit;
                Check(won, $"reward_wallet: party {party}'s battle was won");
                if (!won) return false;
                int gain = udm.OwendGil - gilBefore;
                Check(WcRewards.LastGilBase > 0 && gain == WcRewards.LastGilBase * gp - lost,
                      $"reward_wallet: the wallet got {gain} gil ({gilBefore} -> {udm.OwendGil}): the battle's {WcRewards.LastGilBase} x{gp} = " +
                      $"{WcRewards.LastGilPaid}{(lost > 0 ? $", less {lost} the party lost" : "")}");
                Check(WcRewards.LastResultGil == gain + lost,
                      $"reward_wallet: the result screen shows {WcRewards.LastResultGil} gil, the wallet's gain {gain + lost} (multiplied once, not twice)");
                var party1 = udm.GetPartyMemberData().ToArray().Where(c => c != null).Select(c => c.Id).Where(expBefore.ContainsKey).ToList();
                var owned = Owned();
                var standing = party1.Where(id => owned.TryGetValue(id, out var c) && c.Parameter.CurrentHP > 0).ToList();
                int n = Math.Max(1, standing.Count);
                int each = whole ? WcRewards.LastExpBase * xp : WcRewards.LastExpBase * xp / n;
                var gains = standing.Select(id => owned[id].CurrentExp - expBefore[id]).ToList();
                Check(WcRewards.LastExpBase > 0 && gains.Count > 0 && gains.All(g => g == each),
                      $"reward_wallet: EXP: the battle's {WcRewards.LastExpBase} x{xp}{(whole ? ", not split" : $" / {n}")} = {each} each; " +
                      $"the characters got [{string.Join(" ", gains)}] (Genelate got {WcRewards.LastExpPaid}, its result shows {WcRewards.LastResultExp})");
                return false;
            }
            var steps = new List<(float, Func<bool>)>
            {
                (0f, () =>
                {
                    party = Plain(MasterManager.Instance.GetData<MonsterParty>(7)) ? 7 : 0;
                    if (party == 0)
                        foreach (var kv in MasterManager.Instance.GetList<MonsterParty>())
                            if (kv.Value != null && kv.Value.Id > 0 && kv.Value.Id < 400 && Plain(kv.Value)) { party = kv.Value.Id; break; }
                    Check(party > 0, $"reward_wallet: an ordinary party with gil and EXP: {party}");
                    if (party <= 0) return false;
                    var udm = UserDataManager.Instance();
                    gilBefore = udm.OwendGil;
                    expBefore.Clear();
                    foreach (var c in udm.OwnedCharacterList) if (c != null) expBefore[c.Id] = c.CurrentExp;
                    WcRewards.LastGilBase = WcRewards.LastGilPaid = WcRewards.LastExpBase = WcRewards.LastExpPaid = -1;
                    WcRewards.LastResultGil = WcRewards.LastResultExp = -1;
                    winsAt = WcScaling.Victories;
                    recorder = () =>
                    {
                        var b = BattlePlugManager.Instance();
                        if (b == null || !b.IsBattle()) return;
                        foreach (var p in b.GetPlayerUnits())
                        {
                            var pr = p?.BattleUnitDataInfo?.Parameter;
                            if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999) { if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999; pr.CurrentHP = 9999; }
                        }
                        try { lost = Math.Max(lost, BattlePlugManager.Instance()?.BattleAddExpPlug?.GetPlayerSideLostGil() ?? 0); } catch (Exception) { }
                    };
                    WcBattles.Queue(party);
                    Plugin.L.LogInfo($"[wc test] reward_wallet: party {party} queued; gil {gilBefore}, -gpm x{WcRewards.GilMultiplier}, -xpm x{WcRewards.ExpMultiplier}");
                    return true;
                }),
            };
            for (int k = 0; k < 20; k++)
            {
                bool last = k == 19;
                steps.Add((3f, () =>
                {
                    if (WcScaling.Victories > winsAt) { won = true; return Report(); }
                    if (last) return Report();
                    var b = BattlePlugManager.Instance();
                    if (b == null || !b.IsBattle()) return true;
                    var ui = Last.UI.BattleUIManager.Instance;
                    if (ui != null && !ui.IsAuto()) ui.SetIsAuto(true);
                    foreach (var e in b.GetEnemyUnits())
                    {
                        var pr = e?.BattleUnitDataInfo?.Parameter;
                        if (pr != null && pr.CurrentHP > 1) pr.CurrentHP = 1;   // the party's next hits win it
                    }
                    return true;
                }));
            }
            return steps;
        }
    }
}
