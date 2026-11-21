// The atb check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_atb(string name)
        {
                // WC's (the SNES's) ATB (SnesAtb): the game's own BattleProgressATBFF6.ATBCalc per second
                // (its gauge: 0-100) for a party member and a monster at a sweep of Speeds, the monster
                // at each of the PR's battle speeds, Haste and Slow, against the SNES's numbers (C2/09D2
                // CalcSpeed, C2/111B UpdateBattleTime: expected values from tests/test_wc.py's model);
                // then the gauges' real fill from 0 for 2.5 s. Measured before SnesAtb: party
                // 0.260 x (Speed + 13), monsters 0.216 x (Speed + 21), Haste x1.5, the battle speed on both.
                int savedSpeed = -1, savedMode = -1;
                float t0 = 0, t0Game = 0;
                var g0 = new Dictionary<IntPtr, float>();
                var want0 = new Dictionary<IntPtr, double>();
                int[] speeds = { 1, 10, 30, 50, 100, 128 };
                double[] party = { 5.7773, 8.2533, 13.7555, 19.2577, 33.0133, 40.7164 };
                var monster = new Dictionary<int, double[]>   // the PR's battle speed 0-4 -> SNES Bat.Speed 6, 5, 3, 2, 1
                {
                    [0] = new[] { 3.0262, 4.1267, 7.1529, 9.904, 17.332, 21.4586 },
                    [1] = new[] { 3.5764, 4.952, 8.5284, 11.8298, 20.3582, 25.0351 },
                    [2] = new[] { 4.4018, 6.6027, 11.0044, 15.4062, 26.6857, 32.7381 },
                    [3] = new[] { 4.952, 7.428, 12.38, 17.332, 29.7119, 36.5897 },
                    [4] = new[] { 5.5022, 7.9782, 13.4804, 18.9826, 32.7381, 40.4412 },
                };
                bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol * Math.Max(1e-6, Math.Abs(b));
                return new List<(float, Func<bool>)>
                {
                    (0, () => { WcBattles.Queue(7); Plugin.L.LogInfo("[wc test] atb: party 7 queued"); return true; }),
                    (8, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        var prog = battle?.BattleProgress?.TryCast<BattleProgressATBFF6>();
                        var calc = HarmonyLib.AccessTools.Method(typeof(BattleProgressATBFF6), "ATBCalc");
                        var cfg = UserDataManager.Instance()?.Config;
                        if (prog == null || calc == null || cfg == null) { Check(false, $"atb: progress {prog != null}, calc {calc != null}, config {cfg != null}"); return false; }
                        savedSpeed = cfg.BattleSpeed;
                        savedMode = cfg.BattleModeIndex;
                        var hero = battle.GetPlayerUnits()?.ToArray().FirstOrDefault(u => u != null);
                        var foe = battle.GetEnemyUnits()?.ToArray().FirstOrDefault(u => u != null);
                        float Rate(BattleUnitData u) => (float)calc.Invoke(prog, new object[] { u }) / Mathf.Max(1e-6f, Time.deltaTime);
                        var hp = hero?.BattleUnitDataInfo?.Parameter;
                        var fp = foe?.BattleUnitDataInfo?.Parameter;
                        if (hp == null || fp == null) { Check(false, "atb: a party member and a monster"); return false; }
                        int heroAgi = hp.BaseAgility, foeAgi = fp.BaseAgility;
                        var got = new List<string>();
                        bool ok = true;
                        for (int i = 0; i < speeds.Length; i++)
                        {
                            hp.BaseAgility = speeds[i];
                            float r = Rate(hero);
                            got.Add($"{hp.ConfirmedAgility()}:{r:0.000}");
                            ok &= Near(r, party[i], 0.002);
                        }
                        Check(ok, $"atb: the party's fill per second = the SNES's ({string.Join(" ", got)}; SNES {string.Join(" ", party)})");
                        foreach (var kv in monster)
                        {
                            cfg.BattleSpeed = kv.Key;
                            got.Clear();
                            ok = true;
                            for (int i = 0; i < speeds.Length; i++)
                            {
                                fp.BaseAgility = speeds[i];
                                float r = Rate(foe);
                                got.Add($"{fp.ConfirmedAgility()}:{r:0.000}");
                                ok &= Near(r, kv.Value[i], 0.002);
                            }
                            Check(ok, $"atb: a monster's fill per second at the PR's battle speed {kv.Key} = the SNES's ({string.Join(" ", got)}; SNES {string.Join(" ", kv.Value)})");
                        }
                        cfg.BattleSpeed = savedSpeed;
                        fp.BaseAgility = foeAgi;
                        hp.BaseAgility = 50;
                        float normal = Rate(hero);
                        BattleUtility.AddCondition(hero, (Last.Defaine.ConditionType)15);   // Haste ("Heist")
                        float haste = Rate(hero);
                        BattleUtility.AddCondition(hero, (Last.Defaine.ConditionType)16);   // Slow (wins over Haste on the SNES)
                        float slow = Rate(hero);
                        Check(Near(normal, 19.2577, 0.002) && Near(haste, 25.2643, 0.002) && Near(slow, 9.6289, 0.002),
                              $"atb: Speed 50: {normal:0.000}/s, Haste {haste:0.000} (SNES 25.264: x1.3125), Slow {slow:0.000} (SNES 9.629: x0.5); statuses [{string.Join(",", hp.CurrentConditionList.ToArray().Select(c => c?.Id ?? 0))}]");
                        hp.BaseAgility = heroAgi;
                        // the gauges' real fill: Active mode, every gauge from 0, for 2.5 s
                        cfg.BattleModeIndex = 0;
                        t0 = Time.realtimeSinceStartup;
                        t0Game = Time.time;
                        foreach (var u in battle.GetPlayerUnits().ToArray().Cast<BattleUnitData>().Concat(battle.GetEnemyUnits().ToArray()))
                        {
                            if (u == null) continue;
                            prog.ChangeATBGaugeByUnitData(u, 0);
                            g0[u.Pointer] = prog.GetAtbGaugeByUnitData(u);
                            want0[u.Pointer] = Rate(u);
                        }
                        return true;
                    }),
                    (2.5f, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        var prog = battle?.BattleProgress?.TryCast<BattleProgressATBFF6>();
                        // the gauge fills by game time (ATBCalc x Time.deltaTime): a long frame's delta is
                        // capped (Time.maximumDeltaTime), so on a busy PC real seconds outrun it; the check
                        // uses game time and logs both (2026-11-20: 0.84 of real time, every unit alike)
                        float dtReal = Time.realtimeSinceStartup - t0, dt = Time.time - t0Game;
                        if (prog != null)
                            foreach (var u in battle.GetPlayerUnits().ToArray().Cast<BattleUnitData>().Concat(battle.GetEnemyUnits().ToArray()))
                            {
                                if (u == null || !g0.TryGetValue(u.Pointer, out var a)) continue;
                                float b = prog.GetAtbGaugeByUnitData(u);
                                // the rate for the statuses the unit has now (Haste and Slow together don't
                                // last: the game keeps one of them a moment after both are added)
                                var calc = HarmonyLib.AccessTools.Method(typeof(BattleProgressATBFF6), "ATBCalc");
                                double want = (float)calc.Invoke(prog, new object[] { u }) / Mathf.Max(1e-6f, Time.deltaTime);
                                string statuses = string.Join(",", u.BattleUnitDataInfo?.Parameter?.CurrentConditionList?.ToArray().Select(c => c?.Id ?? 0) ?? new int[0]);
                                Check(b < 99.9f && (Near((b - a) / dt, want, 0.15) || Near((b - a) / dt, want0[u.Pointer], 0.15)),
                                      $"atb: real fill {(u.TryCast<BattlePlayerData>() != null ? "party" : "monster")} speed {u.BattleUnitDataInfo?.Parameter?.ConfirmedAgility()} [{statuses}]: {a:0.0} -> {b:0.0} in {dt:0.00} s of game time ({dtReal:0.00} real) ({(b - a) / dt:0.000}/s; ATBCalc {want:0.000}/s, at the start {want0[u.Pointer]:0.000}/s)");
                            }
                        var cfg = UserDataManager.Instance()?.Config;
                        if (cfg != null && savedMode >= 0) cfg.BattleModeIndex = savedMode;
                        if (cfg != null && savedSpeed >= 0) cfg.BattleSpeed = savedSpeed;
                        return false;
                    }),
                };
            }
    }
}
