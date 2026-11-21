// The enemy_ai@ check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PrKit;
using Last.Battle;
using Last.Data.Master;
using Last.Data.User;
using Last.Interpreter;
using Last.Management;
using Last.Map;
using UnityEngine;
using Object = UnityEngine.Object;
using PrKit.Ff6;   // SnesAi: the kit's FF6 AI interpreter
using PrKit.Seeds;

namespace WorldsCollide.PR
{
    static partial class WcTest
    {
        static List<(float, Func<bool>)> Plan_enemy_ai_2(string name)
        {
                // any monster that runs its WC script in the plugin: a party with it (PR monster id after
                // the @), the party kept alive on auto for 45 s: its turns and counters run the
                // interpreter (logged), then it dies (its death block, if any)
                int monster = int.Parse(name.Substring(9)), party = 0, turnsAt = 0, countersAt = 0, finalsAt = 0, winsAt = 0, queuedAt = 0, shownAt = 0, seenAt = 0;
                bool ended = false;
                var steps = new List<(float, Func<bool>)>
                {
                    (2, () =>
                    {
                        foreach (var kv in MasterManager.Instance.GetList<global::Last.Data.Master.MonsterParty>())
                        {
                            var p = kv.Value;
                            if (p == null || p.Id >= 2000) continue;
                            if (new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6, p.Monster7, p.Monster8, p.Monster9 }.Contains(monster)) { party = p.Id; break; }
                        }
                        Check(party > 0, $"enemy_ai@{monster}: a party with it ({party})");
                        if (party <= 0) return false;
                        turnsAt = WcEnemyAi.Turns; countersAt = WcEnemyAi.Counters; finalsAt = WcEnemyAi.Finals; winsAt = WcScaling.Victories;
                        queuedAt = WcEnemyAi.LinesQueued; shownAt = WcEnemyAi.LinesShown; seenAt = WcEnemyAi.LinesSeen.Count;
                        WcBattles.Queue(party);
                        return true;
                    }),
                    (8, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        Check(battle != null && battle.IsBattle(), $"enemy_ai@{monster}: in battle (party {party})");
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                        recorder = () =>
                        {
                            var b = BattlePlugManager.Instance();
                            if (b == null || !b.IsBattle()) return;
                            foreach (var p in b.GetPlayerUnits())
                            {
                                var pr = p?.BattleUnitDataInfo?.Parameter;
                                if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999) { if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999; pr.CurrentHP = 9999; }
                            }
                        };
                        return true;
                    }),
                };
                for (int k = 0; k < 9; k++)
                {
                    int round = k;
                    steps.Add((5f, () =>
                    {
                        var ui = Last.UI.BattleUIManager.Instance;
                        if (ui != null && !ui.IsAuto()) ui.SetIsAuto(true);
                        if (round < 8) return true;
                        Check(WcEnemyAi.Turns > turnsAt, $"enemy_ai@{monster}: its turns ran its WC script ({WcEnemyAi.Turns - turnsAt}; last {WcEnemyAi.LastTurn})");
                        Plugin.L.LogInfo($"[wc test] enemy_ai@{monster}: {WcEnemyAi.Counters - countersAt} counters (last {WcEnemyAi.LastCounter})");
                        if (WcEnemyAi.LinesQueued > queuedAt)
                            Check(WcEnemyAi.LinesShown > shownAt, $"enemy_ai@{monster}: its battle lines showed ({WcEnemyAi.LinesQueued - queuedAt} filed; " +
                                  $"shown: {string.Join(", ", WcEnemyAi.LinesSeen.Skip(seenAt))})");
                        foreach (var e in BattlePlugManager.Instance()?.GetEnemyUnits() ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BattleEnemyData>(0))
                        {
                            var pr = e?.BattleUnitDataInfo?.Parameter;
                            if (pr != null && pr.CurrentHP > 1) pr.CurrentHP = 1;
                        }
                        return true;
                    }));
                }
                for (int k = 0; k < 8; k++)
                {
                    int round = k;
                    steps.Add((5f, () =>
                    {
                        bool over = WcScaling.Victories > winsAt;
                        if (ended || (!over && round < 7)) return true;
                        ended = true;
                        recorder = null;
                        Plugin.L.LogInfo($"[wc test] enemy_ai@{monster}: {WcEnemyAi.Finals - finalsAt} deaths (last {WcEnemyAi.LastFinal}); " +
                                         (over ? "won" : "not won in time (an evasive monster the party keeps missing)"));
                        return true;
                    }));
                }
                return steps;
            }
    }
}
