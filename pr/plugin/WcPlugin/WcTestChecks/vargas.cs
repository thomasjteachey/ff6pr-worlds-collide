// The vargas check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_vargas(string name)
        {
                // Vargas' fight in a WC game (wc_events boss_battle_events, 2026-11-12): WC blanks its SNES
                // battle events (Sabin appears, his Death Sentence, the Pummel), so the PR's (sc_b_0055
                // Event00/01) only mark themselves done: no Sabin joining, no one blown away, no line; Vargas
                // goes down as any boss (his PR AI's normal death). Party 436 itself (WcBattles.Queue: no
                // event remap), auto-battle, the party kept alive; the script's event variable is logged
                // (1 = Sabin's event taken, 2 = the Blitz lesson's); after 25 s the monsters drop to 1 HP.
                const int Party = 436;
                int winsAt = 0, units = -1, maxEvent = 0;
                float startAt = -1;
                bool started = false, won = false, changed = false;
                string lastUnits = "";
                bool Report()
                {
                    recorder = null;
                    Check(started, "vargas: the fight started (party 436)");
                    Check(maxEvent >= 2, $"vargas: the fight took Sabin's event and the Blitz lesson (its variable got to {maxEvent}: 1 = Sabin's, 2 = the lesson's)");
                    Check(!changed, $"vargas: the party stayed as it was: no Sabin joining, no one blown away ({lastUnits})");
                    var shown = WcEvents.TestStoryShown.Where(k => k.StartsWith("E0055_")).ToList();
                    Check(shown.Count == 0, $"vargas: no line of the fight shows in play ({string.Join(", ", shown)}; skipped {string.Join(", ", WcEvents.TestStorySkipped.Where(k => k.StartsWith("E0055_")))})");
                    Check(won, $"vargas: the fight was won (victories {WcScaling.Victories - winsAt})");
                    return false;
                }
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        WcEvents.TestStoryShown.Clear();
                        WcEvents.TestStorySkipped.Clear();
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
                        };
                        WcBattles.Queue(Party);
                        Plugin.L.LogInfo("[wc test] vargas: party 436 queued");
                        return true;
                    }),
                };
                for (int k = 0; k < 40; k++)
                {
                    bool last = k == 39;
                    steps.Add((3f, () =>
                    {
                        if (last) return Report();
                        if (WcScaling.Victories > winsAt) { won = true; return Report(); }
                        var b = BattlePlugManager.Instance();
                        if (b == null || !b.IsBattle()) return true;
                        float now = Time.realtimeSinceStartup;
                        var ui = Last.UI.BattleUIManager.Instance;
                        if (ui != null && !ui.IsAuto()) ui.SetIsAuto(true);
                        int n = 0;
                        var ids = new List<string>();
                        foreach (var p in b.GetPlayerUnits()) if (p != null) { n++; ids.Add($"{p.BattleUnitDataInfo?.Parameter?.CurrentHP}"); }
                        int ev = WcEvents.Flag(DataStorage.Category.kScriptLocalVariable, 1);
                        maxEvent = Math.Max(maxEvent, ev);
                        var foes = string.Join(" ", b.GetEnemyUnits().Select(e => $"{e?.GetMonster()?.Id}:{e?.BattleUnitDataInfo?.Parameter?.CurrentHP}"));
                        if (!started) { started = true; startAt = now; units = n; }
                        else if (n != units) changed = true;
                        lastUnits = $"{n} party units (at the start {units})";
                        Plugin.L.LogInfo($"[wc test] vargas: {now - startAt:0} s, event variable {ev}, {lastUnits}, monsters {foes}");
                        // the bears go down at once (Vargas can't be targeted while they stand: NotTargeting,
                        // sc_b_0055's start); his own hits start the events (the first with HP <= 10880: Sabin's,
                        // the next <= 10368: the Blitz lesson), so he drops to 1 HP only once both were taken
                        foreach (var e in b.GetEnemyUnits())
                        {
                            var pr = e?.BattleUnitDataInfo?.Parameter;
                            bool vargas = e?.GetMonster()?.Id == 260;
                            if (pr != null && pr.CurrentHP > 1 && (!vargas || maxEvent >= 2 || now - startAt > 60)) pr.CurrentHP = 1;
                        }
                        return true;
                    }));
                }
                return steps;
            }
    }
}
