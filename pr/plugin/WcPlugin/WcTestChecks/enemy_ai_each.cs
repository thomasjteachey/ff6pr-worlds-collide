// The enemy_ai_each@ check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_enemy_ai_each(string name)
        {
                // several monsters' fights in one run (PR monster ids after the @, "+"-separated; the request
                // splits checks at commas): each monster's first party, 30 s on auto with both sides kept alive
                // (monsters get HP no early party takes in a hit, topped up), its turns, counters and
                // entries/exits logged; then "Nk" lets the party kill the monster, "No" its partners, and 15 s
                // more show what the deaths bring in; the battle is then ended as the PR's BattleForceExit
                // ends one. A fight whose monsters took no turn fails.
                var list = name.Substring(14).Split('+', ' ').Where(x => x.Trim().Length > 0).Select(x => x.Trim()).ToList();
                var steps = new List<(float, Func<bool>)>();
                int turnsAt = 0, countersAt = 0, entriesAt = 0, finalsAt = 0, party = 0;
                var exposed = new HashSet<int>();   // unique ids no longer kept alive
                var low = new HashSet<int>();       // unique ids held at 2000 HP (their low-HP branches)
                string Enemies(BattlePlugManager b)
                {
                    var l = new List<string>();
                    foreach (var e in b.GetEnemyUnits())
                    {
                        var ed = e?.TryCast<BattleEnemyData>();
                        if (ed == null) continue;
                        var pr = ed.BattleUnitDataInfo?.Parameter;
                        var conds = new List<string>();
                        if (pr?.CurrentConditionList != null) foreach (var c in pr.CurrentConditionList) if (c != null) conds.Add(((int)c.ConditionType).ToString());
                        l.Add($"{ed.UniqueId}:{ed.GetMonster()?.Id} HP {pr?.CurrentHP} [{string.Join(",", conds)}]");
                    }
                    return string.Join("; ", l);
                }
                recorder = () =>
                {
                    var b = BattlePlugManager.Instance();
                    if (b == null || !b.IsBattle()) return;
                    foreach (var u in b.GetPlayerUnits())
                    {
                        var pr = u?.BattleUnitDataInfo?.Parameter;
                        if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999) { if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999; pr.CurrentHP = 9999; }
                    }
                    foreach (var u in b.GetEnemyUnits())
                    {
                        var ed = u?.TryCast<BattleEnemyData>();
                        var pr = ed?.BattleUnitDataInfo?.Parameter;
                        if (pr == null || pr.CurrentHP <= 0 || exposed.Contains(ed.UniqueId)) continue;
                        if (pr.BaseMaxHp < 30000) pr.BaseMaxHp = 30000;
                        int max = low.Contains(ed.UniqueId) ? 2000 : pr.ConfirmedMaxHp();
                        if (pr.CurrentHP < max) pr.CurrentHP = max;
                    }
                };
                foreach (var item in list)
                {
                    char mode = char.IsLetter(item[item.Length - 1]) ? item[item.Length - 1] : ' ';
                    int monster = int.Parse(mode == ' ' ? item : item.Substring(0, item.Length - 1));
                    steps.Add((1f, () =>
                    {
                        party = 0;
                        exposed.Clear();
                        low.Clear();
                        foreach (var kv in MasterManager.Instance.GetList<global::Last.Data.Master.MonsterParty>())
                        {
                            var p = kv.Value;
                            if (p == null || p.Id >= 2000) continue;
                            if (new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6, p.Monster7, p.Monster8, p.Monster9 }.Contains(monster)) { party = p.Id; break; }
                        }
                        Check(party > 0, $"enemy_ai_each@{monster}: a party with it ({party})");
                        if (party <= 0) return true;
                        turnsAt = WcEnemyAi.Turns; countersAt = WcEnemyAi.Counters; entriesAt = WcEnemyAi.Entries; finalsAt = WcEnemyAi.Finals;
                        WcBattles.Queue(party);
                        return true;
                    }));
                    steps.Add((7f, () =>
                    {
                        if (party <= 0) return true;
                        var b = BattlePlugManager.Instance();
                        Check(b != null && b.IsBattle(), $"enemy_ai_each@{monster}: in battle (party {party})");
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                        if (b != null) Plugin.L.LogInfo($"[wc test] enemy_ai_each@{monster}: start: {Enemies(b)}");
                        return true;
                    }));
                    for (int k = 0; k < 11; k++)
                    {
                        int round = k;
                        steps.Add((5f, () =>
                        {
                            if (party <= 0) return true;
                            var b = BattlePlugManager.Instance();
                            var ui = Last.UI.BattleUIManager.Instance;
                            if (ui != null && !ui.IsAuto()) ui.SetIsAuto(true);
                            if (b == null || !b.IsBattle()) return true;
                            Plugin.L.LogInfo($"[wc test] enemy_ai_each@{monster}: {5 * (round + 1)} s: {Enemies(b)}");
                            if (round == 5 && mode != ' ')
                            {
                                // the deaths: the monster itself (k) or its partners (o), at 1 HP for the party to finish
                                foreach (var u in b.GetEnemyUnits())
                                {
                                    var ed = u?.TryCast<BattleEnemyData>();
                                    if (ed == null) continue;
                                    bool it = ed.GetMonster()?.Id == monster;
                                    if ((mode == 'o') == it) continue;
                                    var pr = ed.BattleUnitDataInfo.Parameter;
                                    if (mode == 'l') { low.Add(ed.UniqueId); pr.CurrentHP = 2000; continue; }
                                    exposed.Add(ed.UniqueId);
                                    if (pr.CurrentHP > 1) pr.CurrentHP = 1;
                                }
                                Plugin.L.LogInfo($"[wc test] enemy_ai_each@{monster}: {(mode == 'k' ? "it at 1 HP" : mode == 'l' ? "it held at 2000 HP" : "its partners at 1 HP")} " +
                                                 $"(units {string.Join(",", exposed.Concat(low))})");
                            }
                            if (round < 10) return true;
                            int t = WcEnemyAi.Turns - turnsAt;
                            Check(t > 0, $"enemy_ai_each@{monster}: its fight's monsters took WC turns ({t}; {WcEnemyAi.Counters - countersAt} counters, " +
                                         $"{WcEnemyAi.Entries - entriesAt} entries/exits, last {WcEnemyAi.LastEntry}; {WcEnemyAi.Finals - finalsAt} deaths)");
                            var j = b.BattleEndJugment;
                            if (j != null) { j.SetEnabled(true); j.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal; j.isForce = true; }
                            return true;
                        }));
                    }
                    steps.Add((8f, () =>
                    {
                        var b = BattlePlugManager.Instance();
                        if (b != null && b.IsBattle()) Plugin.L.LogInfo($"[wc test] enemy_ai_each@{monster}: still in battle 8 s after the forced end");
                        return true;
                    }));
                }
                steps.Add((1f, () => { recorder = null; return true; }));
                return steps;
            }
    }
}
