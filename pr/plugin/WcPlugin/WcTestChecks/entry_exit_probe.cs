// The entry_exit_probe check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_entry_exit_probe(string name)
        {
                // what the PR's own conditions do to a monster (for WC's F5 entries/exits): Escape (3) on A,
                // then lifted; NotTargeting (667) on B; does the party still hit B, does A come back, does
                // the battle end when C dies with B untargetable and A gone
                int party = 0, winsAt = 0, turnsAtAdd = 0, addedCell = 0;
                BattleEnemyData A = null, B = null, C = null;
                int hpB = 0, hpC = 0;
                string State(BattleEnemyData e)
                {
                    if (e == null) return "none";
                    var b = BattlePlugManager.Instance();
                    var pr = e.BattleUnitDataInfo?.Parameter;
                    var conds = new List<string>();
                    if (pr?.CurrentConditionList != null) foreach (var c in pr.CurrentConditionList) if (c != null) conds.Add(((int)c.ConditionType).ToString());
                    bool listed = false, dust = false;
                    if (b != null)
                    {
                        foreach (var u in b.GetEnemyUnits()) if (u != null && u.Pointer == e.Pointer) listed = true;
                        foreach (var u in b.GetDustEnemyUnits()) if (u != null && u.Pointer == e.Pointer) dust = true;
                    }
                    var ent = b?.EventCommand?.GetEnemyEntity(e.UniqueId);
                    return $"unique {e.UniqueId} HP {pr?.CurrentHP} conditions [{string.Join(",", conds)}] listed {listed} dust {dust} entity {(ent == null ? "none" : ent.gameObject.activeInHierarchy.ToString())}";
                }
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        foreach (var kv in MasterManager.Instance.GetList<global::Last.Data.Master.MonsterParty>())
                        {
                            var p = kv.Value;
                            if (p == null || p.Id >= 600) continue;
                            var ms = new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6, p.Monster7, p.Monster8, p.Monster9 }.Where(m => m > 0).ToArray();
                            if (ms.Length == 3) { party = p.Id; break; }
                        }
                        Check(party > 0, $"entry_exit_probe: a party of three ({party})");
                        if (party <= 0) return false;
                        winsAt = WcScaling.Victories;
                        WcBattles.Queue(party);
                        return true;
                    }),
                    (8, () =>
                    {
                        var b = BattlePlugManager.Instance();
                        Check(b != null && b.IsBattle(), "entry_exit_probe: in battle");
                        if (b == null) return false;
                        var es = new List<BattleEnemyData>();
                        foreach (var u in b.GetEnemyUnits()) { var ed = u?.TryCast<BattleEnemyData>(); if (ed != null) es.Add(ed); }
                        es = es.OrderBy(x => x.UniqueId).ToList();
                        if (es.Count < 3) { Check(false, $"entry_exit_probe: three monsters ({es.Count})"); return false; }
                        A = es[0]; B = es[1]; C = es[2];
                        foreach (var e in es) { var pr = e.BattleUnitDataInfo.Parameter; if (pr.BaseMaxHp < 60000) pr.BaseMaxHp = 60000; pr.CurrentHP = 50000; }
                        recorder = () =>
                        {
                            var bb = BattlePlugManager.Instance();
                            if (bb == null || !bb.IsBattle()) return;
                            foreach (var u in bb.GetPlayerUnits())
                            {
                                var pr = u?.BattleUnitDataInfo?.Parameter;
                                if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999) { if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999; pr.CurrentHP = 9999; }
                            }
                        };
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: before: A " + State(A) + " | B " + State(B) + " | C " + State(C));
                        b.EventCommand.AddConditionEnemy(A.UniqueId, 3);
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: A given Escape (3): " + State(A));
                        return true;
                    }),
                    (3, () =>
                    {
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: A 3 s after Escape: " + State(A));
                        var b = BattlePlugManager.Instance();
                        b?.EventCommand?.AddConditionEnemy(B.UniqueId, 667);
                        hpB = B.BattleUnitDataInfo.Parameter.CurrentHP; hpC = C.BattleUnitDataInfo.Parameter.CurrentHP;
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: B given NotTargeting (667): " + State(B));
                        return true;
                    }),
                    (12, () =>
                    {
                        int dB = hpB - B.BattleUnitDataInfo.Parameter.CurrentHP, dC = hpC - C.BattleUnitDataInfo.Parameter.CurrentHP;
                        Plugin.L.LogInfo($"[wc test] entry_exit_probe: 12 s of the party on auto: B (untargetable) lost {dB} HP, C lost {dC}; A {State(A)}");
                        var b = BattlePlugManager.Instance();
                        b?.EventCommand?.RecoveryConditionEnemy(A.UniqueId, 3);
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: A's Escape lifted: " + State(A));
                        return true;
                    }),
                    (4, () =>
                    {
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: A 4 s later: " + State(A) + " | B " + State(B) + " | C " + State(C));
                        // a monster brought in the PR's way (its AddEnemy abilities: EventCommand.InBattleEnemy)
                        // into a cell no monster has: does it appear there and take turns?
                        var b = BattlePlugManager.Instance();
                        var used = new HashSet<int>();
                        foreach (var u in b.GetEnemyUnits()) { var ed = u?.TryCast<BattleEnemyData>(); if (ed != null) used.Add(ed.UniqueId); }
                        int cell = Enumerable.Range(1, 9).FirstOrDefault(k => !used.Contains(k));
                        int mon = A.GetMonster()?.Id ?? 0;
                        turnsAtAdd = WcEnemyAi.Turns;
                        Plugin.L.LogInfo($"[wc test] entry_exit_probe: InBattleEnemy({mon}, cell {cell}); enemies before: {string.Join(",", used.OrderBy(x => x))}");
                        if (cell > 0 && mon > 0) b.EventCommand.InBattleEnemy(mon, cell);
                        addedCell = cell;
                        return true;
                    }),
                    (8, () =>
                    {
                        var b = BattlePlugManager.Instance();
                        var now = new List<string>();
                        BattleEnemyData added = null;
                        foreach (var u in b.GetEnemyUnits())
                        {
                            var ed = u?.TryCast<BattleEnemyData>();
                            if (ed == null) continue;
                            now.Add($"{ed.UniqueId}:{ed.GetMonster()?.Id}");
                            if (ed.UniqueId == addedCell) added = ed;
                        }
                        Plugin.L.LogInfo($"[wc test] entry_exit_probe: 8 s after InBattleEnemy: enemies {string.Join(",", now)}; the new one: {State(added)}; " +
                                         $"turns since {WcEnemyAi.Turns - turnsAtAdd} (last {WcEnemyAi.LastTurn})");
                        return true;
                    }),
                    (1, () =>
                    {
                        // A gone again, B untargetable: C's death alone ends the battle?
                        var b = BattlePlugManager.Instance();
                        b?.EventCommand?.AddConditionEnemy(A.UniqueId, 3);
                        C.BattleUnitDataInfo.Parameter.CurrentHP = 1;
                        winsAt = WcScaling.Victories;
                        return true;
                    }),
                    (15, () =>
                    {
                        var b = BattlePlugManager.Instance();
                        bool won = WcScaling.Victories > winsAt;
                        Plugin.L.LogInfo($"[wc test] entry_exit_probe: 15 s after C at 1 HP: won {won}, in battle {b != null && b.IsBattle()}; A {State(A)} | B {State(B)} | C {State(C)}");
                        recorder = null;
                        if (b != null && b.IsBattle() && !won && b.BattleEndJugment != null)
                        {
                            b.BattleEndJugment.SetEnabled(true);
                            b.BattleEndJugment.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal;
                            b.BattleEndJugment.isForce = true;
                        }
                        return true;
                    }),
                    (6, () => false),
                };
            }
    }
}
