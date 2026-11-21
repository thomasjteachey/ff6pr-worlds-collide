// The ability_scaling check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_ability_scaling(string name)
        {
                // WC's ability scaling (-ase) as monsters act (WcAbilityScaling; the user's seed
                // gaadl68ix7j4, 2026-11-11: its Sandhorse always cast Wind Slash). (1) the tier
                // formula against WC's assembly (expected digits from a model of
                // enemy_script_custom_commands.py: rolls 0, 0x19, 0x59, 0xb2, 0xf2); (2) in a battle
                // with a Sandhorse (its script casts WIND1 = ability 7613): the act the game builds
                // for it, many times, holds WC's spell mix for its level, aimed at the party;
                // (3) its own AI turns swap the spell too.
                int swapsBefore = 0, natural = 0, party = 0;
                BattleUnitData horse = null;
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        const string want = "00012 00123 01234 00012 00123 01234 00123 01234 12345 00123 01234 12345 01234 12345 23456 12345 23456 34567 34567 45678 56789 56789 6789a 789ab 6789a 789ab 89abb 6789a 789ab 89abb 6789a 789ab 89abb 6789a 789ab 89abb 00012 00123 01234 00012 00123 01234 00012 00123 01234 00123 01234 12345 00123 01234 12345 01234 12345 23456 23456 34567 45678 56789 6789a 789ab 56789 6789a 789ab 56789 6789a 789ab 6789a 789ab 89abb 6789a 789ab 89abb";
                        var got = new List<string>();
                        foreach (var f in new[] { 2.0, 2.5 })
                            foreach (var lv in new[] { 1, 4, 5, 9, 10, 16, 27, 39, 40, 43, 44, 99 })
                                for (int off = 0; off < 3; off++)
                                    got.Add(string.Concat(new[] { 0, 0x19, 0x59, 0xb2, 0xf2 }.Select(r => WcAbilityScaling.Tier(lv, off, f, r).ToString("x"))));
                        Check(string.Join(" ", got) == want, $"ability scaling: WC's tier formula ({string.Join(" ", got)})");
                        var s = WcSeeds.Active?.Scaling;
                        Check(s != null && s.Base == 7600 && s.Count == 36 && s.Tiers.Length == 12,
                              $"ability scaling: the seed's tiers ({(s == null ? "none" : $"base {s.Base}, {s.Tiers.Length} categories, factor {s.Factor}")})");
                        foreach (var kv in MasterManager.Instance.GetList<MonsterParty>())
                        {
                            var p = kv.Value;
                            if (p == null) continue;
                            if (new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6 }.Contains(96)) { party = p.Id; break; }
                        }
                        Check(party > 0, $"ability scaling: a party with a Sandhorse ({party})");
                        if (party <= 0 || s == null) return false;
                        WcBattles.Queue(party);
                        Plugin.L.LogInfo($"[wc test] ability_scaling: party {party} queued");
                        return true;
                    }),
                    (8, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        Check(battle != null && battle.IsBattle(), "ability scaling: in battle");
                        horse = battle?.GetEnemyUnits()?.ToArray().FirstOrDefault(u => u?.GetMonster()?.Id == 96);
                        var act = horse?.BattleUnitDataInfo?.ActSelectController?.GetAct()?.TryCast<ActSelectEnemy>();
                        var create = HarmonyLib.AccessTools.Method(typeof(ActSelectEnemy), "CreateActData");
                        if (act == null || create == null) { Check(false, $"ability scaling: the Sandhorse's act ({horse != null}, {act != null}, {create != null})"); return false; }
                        var s = WcSeeds.Active.Scaling;
                        int level = horse.BattleUnitDataInfo.Parameter.ConfirmedLevel();
                        // WC's odds for each spell: tier = base + 1 (WIND1) + (x - 2), weights 25/64/89/64/14 of 256
                        double per = s.Factor + 3;
                        int baseTier = level >= (int)(per * 8) ? 8 : level * 2 / (int)(per * 2);
                        var odds = new Dictionary<int, double>();
                        int[] weights = { 25, 64, 89, 64, 14 };
                        for (int x = 0; x < 5; x++)
                        {
                            var pool = s.Tiers[4][Math.Clamp(baseTier + 1 + x - 2, 0, 11)];
                            foreach (var a in pool) odds[a] = odds.GetValueOrDefault(a) + weights[x] / 256.0 / pool.Length;
                        }
                        swapsBefore = WcAbilityScaling.Swaps;
                        var seen = new Dictionary<int, int>();
                        int n = 400, aimed = 0;
                        for (int i = 0; i < n; i++)
                        {
                            var made = create.Invoke(act, new object[] { s.Base + 13, null, BattleActData.ActType.Normal }) as BattleActData;
                            int id = made?.abilityList != null && made.abilityList.Count > 0 ? made.abilityList[0]?.Id ?? 0 : 0;
                            seen[id] = seen.GetValueOrDefault(id) + 1;
                            var t = made?.TargetUnitDatas;
                            if (t != null && t.Length > 0 && t.All(u => u?.TryCast<BattlePlayerData>() != null)) aimed++;
                        }
                        string mix = string.Join(", ", seen.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value / (double)n:0.00} (WC {odds.GetValueOrDefault(kv.Key):0.00})"));
                        Plugin.L.LogInfo($"[wc test] ability_scaling: Sandhorse Lv {level}, base tier {baseTier}: {mix}");
                        Check(WcAbilityScaling.Swaps - swapsBefore == n, $"ability scaling: every act swapped ({WcAbilityScaling.Swaps - swapsBefore} of {n})");
                        Check(seen.Keys.All(odds.ContainsKey), $"ability scaling: only spells of WC's tiers for Lv {level} ({mix})");
                        Check(odds.All(kv => Math.Abs(seen.GetValueOrDefault(kv.Key) / (double)n - kv.Value) < 0.08), $"ability scaling: WC's odds ({mix})");
                        Check(aimed == n, $"ability scaling: every act aimed at the party ({aimed} of {n})");
                        swapsBefore = WcAbilityScaling.Swaps;
                        return true;
                    }),
                    (25, () =>
                    {
                        natural = WcAbilityScaling.Swaps - swapsBefore;
                        Check(natural > 0, $"ability scaling: the Sandhorse's own turns swapped its spell ({natural}; last: {WcAbilityScaling.LastSwap})");
                        return false;
                    }),
                };
            }
    }
}
