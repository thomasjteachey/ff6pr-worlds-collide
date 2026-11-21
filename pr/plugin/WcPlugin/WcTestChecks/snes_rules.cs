// The snes_rules check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_snes_rules(string name)
        {
                // SNES behaviour the PR lost, put back for WC (WcSnesRules, pr/SNES_PR_DISCREPANCIES.md):
                // the x1.25 hit-rate list without Charm, and Umaro's toss of Mog a sure critical, run
                // through the game's act flow (SnesUmaro.TossSelfTest) in a real battle
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        var plus = PrKit.Ff6.SnesHitRates.Names(global::Last.Battle.CalcExecuteFF6.HitRatePlusConditions);
                        Check(!plus.Contains("Temptation") && plus.Contains("Blind"), $"snes_rules: the x1.25 hit-rate list {plus} ({WcSnesRules.HitRates})");
                        Plugin.L.LogInfo(PrKit.Ff6.SnesUmaro.Check());
                        WcBattles.Queue(7);
                        return true;
                    }),
                    (8, () =>
                    {
                        var line = PrKit.Ff6.SnesUmaro.TossSelfTest();
                        if (line != null) Plugin.L.LogInfo(line);
                        Check(line != null && line.Contains("self-test ok"), "snes_rules: " + (line ?? "no battle to test in"));
                        // the Offering's and the Genji Glove's penalties as the SNES has them (SnesRelicPenalties)
                        var relics = PrKit.Ff6.SnesRelicPenalties.SelfTest();
                        if (relics != null) Plugin.L.LogInfo(relics);
                        Check(relics != null && relics.Contains("self-test ok"), "snes_rules: " + (relics ?? "no battle for the relic penalties"));
                        // Suplex misses a monster that can't be suplexed (SnesSuplex)
                        var suplex = PrKit.Ff6.SnesSuplex.SelfTest();
                        if (suplex != null) Plugin.L.LogInfo(suplex);
                        Check(suplex != null && suplex.Contains("self-test ok"), "snes_rules: " + (suplex ?? "no battle for Suplex"));
                        // joins without the PR's level averaging, as WC's SNES recruit (WcJoinLevels)
                        var joins = WcJoinLevels.SelfTest();
                        Plugin.L.LogInfo("[wc test] " + joins);
                        Check(joins.Contains("self-test ok"), "snes_rules: " + joins);
                        return true;
                    }),
                };
            }
    }
}
