// The enemy_ai check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_enemy_ai(string name)
        {
                // every monster runs its WC script in the plugin (WcEnemyAi, SnesAi.cs; the user, 2026-11-12:
                // "please make all enemies/bosses behave like WC SNES"): Atma at Doma (the user's seed:
                // party 410 -> 20036), the party kept alive on auto. Its first turn is only the flash (no
                // act: the PR's "nothing"), its rotation casts WC's scaled placeholders (7601-7607 swapped
                // by WcAbilityScaling) or N. Cross, being hit runs its counter (the shared variable 3
                // counts hits; the 13th sets the charge: "Unknown light surrounded Atma!", two idle turns,
                // the big spell)
                int party = 0, turnsAt = 0, countersAt = 0;
                float lastDiag = 0;
                var turns = new List<string>();
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        Check(WcSeeds.Active?.SnesAi != null && WcSeeds.Active.SnesAi.Scripts.Count == 384,
                              $"enemy_ai: the seed carries every monster's script ({WcSeeds.Active?.SnesAi?.Scripts.Count ?? 0})");
                        // Atma = SNES 381 = PR monster 382, found by monster: where the seed's boss shuffle put it
                        // (a swapped event battle's party: the user's seed, Doma's 410 -> 20036), else its own
                        // party (483). Not Doma's 410 itself: a seed that keeps the Leader there (PR 335, kittest3
                        // 2026-11-20) fought the Leader, whose script has no low-HP branch, charge or death block
                        const int AtmaPr = 382;
                        bool HasAtma(int id)
                        {
                            var p = MasterManager.Instance.GetData<global::Last.Data.Master.MonsterParty>(id);
                            return p != null && new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6, p.Monster7, p.Monster8, p.Monster9 }.Contains(AtmaPr);
                        }
                        int want = WcSeeds.Active?.Battles.Values.SelectMany(x => x).Where(HasAtma).DefaultIfEmpty(0).Min() ?? 0;
                        if (want == 0)
                            foreach (var kv in MasterManager.Instance.GetList<global::Last.Data.Master.MonsterParty>())
                                if (kv.Value != null && kv.Value.Id < 2000 && (want == 0 || kv.Value.Id < want) && HasAtma(kv.Value.Id)) want = kv.Value.Id;
                        party = want;
                        Check(want > 0 && HasAtma(party), $"enemy_ai: Atma's party {party}");
                        turnsAt = WcEnemyAi.Turns; countersAt = WcEnemyAi.Counters;
                        WcBattles.Queue(party);
                        return true;
                    }),
                    (8, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        Check(battle != null && battle.IsBattle(), "enemy_ai: in battle");
                        var ui = Last.UI.BattleUIManager.Instance;
                        ui?.SetIsAuto(true);
                        // the party kept alive: what the monster does is the test, not the outcome
                        string lastTurn = "", lastCounter = "";
                        recorder = () =>
                        {
                            var b = BattlePlugManager.Instance();
                            if (b == null || !b.IsBattle()) return;
                            foreach (var p in b.GetPlayerUnits())
                            {
                                var pr = p?.BattleUnitDataInfo?.Parameter;
                                // no spell kills them: current HP is capped at max HP, so max HP too (as WcScaling sets monsters')
                                if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999)
                                {
                                    if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999;
                                    pr.CurrentHP = 9999;
                                }
                            }
                            // its low-HP rotation (FC 06 36 3C: HP < 7680) whatever HP WC's scaling gives it where it's fought
                            foreach (var e in b.GetEnemyUnits())
                            {
                                var pe = e?.BattleUnitDataInfo?.Parameter;
                                if (pe != null && pe.CurrentHP >= 7680) pe.CurrentHP = 7679;
                            }
                            if (WcEnemyAi.LastTurn != lastTurn) { lastTurn = WcEnemyAi.LastTurn; turns.Add(lastTurn); }
                            if (WcEnemyAi.LastCounter != lastCounter) lastCounter = WcEnemyAi.LastCounter;
                            // every 2 s: each monster's act-select state (where a stalled battle waits)
                            if (Time.realtimeSinceStartup - lastDiag > 2)
                            {
                                lastDiag = Time.realtimeSinceStartup;
                                var parts = new List<string>();
                                foreach (var e in b.GetEnemyUnits())
                                {
                                    var ed = e?.TryCast<BattleEnemyData>();
                                    var ctl = e?.BattleUnitDataInfo?.ActSelectController?.TryCast<ActSelectEnemyController>();
                                    if (ed == null) continue;
                                    parts.Add($"monster {ed.GetMonster()?.Id} unique {ed.UniqueId} HP {e.BattleUnitDataInfo?.Parameter?.CurrentHP} playedAI {ed.IsPlayedAI} " +
                                              (ctl == null ? "no controller" : $"turn {ctl.GetActTurn()} normal {ctl.IsReadyActNormal()} counter {ctl.IsReadyActCounter()} final {ctl.IsReadyActFinalAttack()} viewMes {ctl.IsViewMes()}/{ctl.IsViewMesAfterEffect()}"));
                                }
                                var hp = new List<string>();
                                foreach (var pu in b.GetPlayerUnits()) hp.Add($"{pu?.BattleUnitDataInfo?.Parameter?.CurrentHP}");
                                Plugin.L.LogInfo($"[wc test] enemy_ai state: turns {WcEnemyAi.Turns - turnsAt}, counters {WcEnemyAi.Counters - countersAt}; party HP {string.Join("/", hp)}; {string.Join("; ", parts)}");
                            }
                        };
                        return true;
                    }),
                };
                for (int k = 0; k < 24; k++)
                {
                    int round = k;
                    steps.Add((5f, () =>
                    {
                        var ui = Last.UI.BattleUIManager.Instance;
                        if (ui != null && !ui.IsAuto()) ui.SetIsAuto(true);
                        if (round < 23) return true;
                        recorder = null;
                        int t = WcEnemyAi.Turns - turnsAt, c = WcEnemyAi.Counters - countersAt;
                        Check(t >= 4, $"enemy_ai: Atma's turns ran its WC script ({t} turns; last: {WcEnemyAi.LastTurn})");
                        Check(c >= 1, $"enemy_ai: being hit ran its counter script ({c} counters; last: {WcEnemyAi.LastCounter})");
                        // at this level Atma's HP (4920) is under its 7680 check: its low-HP rotation from the
                        // first turn (WC's fire+1/water+1/earth+2, then fire+1/non-elem+1, ...), all placeholders
                        Check(turns.Count >= 4 && turns.Take(4).All(x => x.Contains("attack 37") || x.Contains("attack 46") || x.Contains("attack 41") || x.Contains("attack 4f")),
                              $"enemy_ai: its low-HP rotation casts WC's placeholders: {string.Join(" | ", turns.Take(4))}");
                        // the 13th hit's charge: "Unknown light surrounded Atma!" (the PR's own line for it)
                        Check(WcEnemyAi.LinesSeen.Contains("E0977_00_554_b_08"),
                              $"enemy_ai: the charge's battle line showed ({string.Join(", ", WcEnemyAi.LinesSeen)})");
                        foreach (var x in turns) Plugin.L.LogInfo("[wc test] enemy_ai turn: " + x);
                        return true;
                    }));
                }
                int finalsAt = 0, winsAt = 0;
                bool ended = false;
                steps.Add((1f, () =>
                {
                    finalsAt = WcEnemyAi.Finals;
                    winsAt = WcScaling.Victories;
                    foreach (var e in BattlePlugManager.Instance()?.GetEnemyUnits() ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BattleEnemyData>(0))
                    {
                        var pr = e?.BattleUnitDataInfo?.Parameter;
                        if (pr != null && pr.CurrentHP > 1) pr.CurrentHP = 1;   // the party's next hit kills it
                    }
                    return true;
                }));
                for (int k = 0; k < 8; k++)
                {
                    int round = k;
                    steps.Add((5f, () =>
                    {
                        bool over = WcScaling.Victories > winsAt;   // won (the result screen waits for a button headless)
                        if (ended || (!over && round < 7)) return true;
                        ended = true;
                        recorder = null;
                        Check(WcEnemyAi.Finals > finalsAt && WcEnemyAi.LastFinal.Contains("=> 735"),
                              $"enemy_ai: Atma's death runs its death block: everyone leaves like a boss ({WcEnemyAi.LastFinal})");
                        Check(over, "enemy_ai: the battle was won after Atma's death");
                        return true;
                    }));
                }
                return steps;
            }
    }
}
