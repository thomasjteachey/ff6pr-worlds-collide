// The enemy_ai_sweep, enemy_ai_sweep@ check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_enemy_ai_sweep(string name)
        {
                // many fights in a row (the plugin's hooks must never stall a battle): a spread of the
                // parties with a monster whose turns the plugin runs (the seed's list), each on auto for
                // a while with both sides kept alive, then ended the way the PR's BattleForceExit ends
                // one (BattleEndJugment.isForce); a fight whose monsters took no turn is a stall
                int want = name.Contains("@") ? int.Parse(name.Substring(name.IndexOf('@') + 1)) : 30;
                var parties = new List<int>();
                var report = new List<string>();
                var idle = new List<string>();
                int turnsAt = 0, countersAt = 0, current = 0, done = 0, notStarted = 0, notEnded = 0;
                string Mons(BattlePlugManager b)
                {
                    var ids = new List<string>();
                    foreach (var e in b.GetEnemyUnits()) ids.Add($"{e?.GetMonster()?.Id ?? 0}");
                    return string.Join("/", ids);
                }
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        var d = WcSeeds.Active?.SnesAi;
                        Check(d != null && d.Plugin.Count > 0, $"enemy_ai_sweep: the seed lists the monsters the plugin runs ({d?.Plugin.Count ?? 0})");
                        if (d == null || d.Plugin.Count == 0) return false;
                        var seenSets = new HashSet<string>();
                        var all = new List<int>();
                        foreach (var kv in MasterManager.Instance.GetList<global::Last.Data.Master.MonsterParty>())
                        {
                            var p = kv.Value;
                            if (p == null || p.Id >= 2000) continue;
                            var ms = new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6 }.Where(m => m > 0).ToArray();
                            if (ms.Length == 0 || !ms.Any(m => d.Plugin.Contains(m - 1))) continue;
                            if (!seenSets.Add(string.Join(",", ms.OrderBy(x => x)))) continue;
                            all.Add(p.Id);
                        }
                        all.Sort();
                        for (int i = 0; i < want && all.Count > 0; i++)
                        {
                            int at = (int)((long)i * all.Count / want);
                            if (!parties.Contains(all[at])) parties.Add(all[at]);
                        }
                        Plugin.L.LogInfo($"[wc test] enemy_ai_sweep: {parties.Count} of {all.Count} parties: {string.Join(", ", parties)}");
                        recorder = () =>
                        {
                            var b = BattlePlugManager.Instance();
                            if (b == null || !b.IsBattle()) return;
                            foreach (var u in b.GetPlayerUnits())
                            {
                                var pr = u?.BattleUnitDataInfo?.Parameter;
                                if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999) { if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999; pr.CurrentHP = 9999; }
                            }
                            // no win (the result screen waits for a button headless): enemies get HP no
                            // early party can take in one hit, topped up every frame
                            foreach (var u in b.GetEnemyUnits())
                            {
                                var pr = u?.BattleUnitDataInfo?.Parameter;
                                if (pr == null || pr.CurrentHP <= 0) continue;
                                if (pr.BaseMaxHp < 60000) pr.BaseMaxHp = 60000;
                                int max = pr.ConfirmedMaxHp();
                                if (pr.CurrentHP < max) pr.CurrentHP = max;
                            }
                        };
                        return true;
                    }),
                };
                for (int k = 0; k < want; k++)
                {
                    int i = k;
                    steps.Add((1f, () =>
                    {
                        if (i >= parties.Count) return true;
                        current = parties[i];
                        turnsAt = WcEnemyAi.Turns; countersAt = WcEnemyAi.Counters;
                        WcBattles.Queue(current);
                        return true;
                    }));
                    steps.Add((7f, () =>
                    {
                        if (i >= parties.Count) return true;
                        var b = BattlePlugManager.Instance();
                        if (b == null || !b.IsBattle()) { notStarted++; report.Add($"party {current}: did not start"); return true; }
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                        return true;
                    }));
                    steps.Add((18f, () =>
                    {
                        if (i >= parties.Count) return true;
                        var b = BattlePlugManager.Instance();
                        if (b == null || !b.IsBattle()) return true;
                        int t = WcEnemyAi.Turns - turnsAt, c = WcEnemyAi.Counters - countersAt;
                        string mons = Mons(b);
                        report.Add($"party {current} [{mons}]: {t} turns, {c} counters; last turn {WcEnemyAi.LastTurn}");
                        if (t == 0) idle.Add($"{current} [{mons}]");
                        // the judge honours isForce only when enabled and of the Normal type (a battle
                        // script can switch either: party 71's 9026 does)
                        var j = b.BattleEndJugment;
                        if (j != null)
                        {
                            j.SetEnabled(true);
                            j.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal;
                            j.isForce = true;
                        }
                        return true;
                    }));
                    for (int w = 0; w < 6; w++)
                    {
                        int wait = w;
                        steps.Add((3f, () =>
                        {
                            if (i >= parties.Count || i < done) return true;   // done: ended already
                            var b = BattlePlugManager.Instance();
                            bool still = b != null && b.IsBattle();
                            if (still && wait < 5) return true;
                            if (still) { notEnded++; report.Add($"party {current}: still in battle 18 s after the forced end"); }
                            done++;
                            return true;
                        }));
                    }
                }
                steps.Add((1f, () =>
                {
                    recorder = null;
                    foreach (var r in report) Plugin.L.LogInfo("[wc test] enemy_ai_sweep: " + r);
                    Check(parties.Count > 0 && notStarted == 0, $"enemy_ai_sweep: every fight started ({parties.Count - notStarted} of {parties.Count})");
                    Check(idle.Count == 0, $"enemy_ai_sweep: the monsters took WC turns in every fight (none in: {string.Join(", ", idle)})");
                    Check(notEnded == 0, $"enemy_ai_sweep: every fight ended when told to ({notEnded} did not)");
                    return true;
                }));
                return steps;
            }
    }
}
