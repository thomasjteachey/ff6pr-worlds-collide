// The veldt_reward check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_veldt_reward(string name)
        {
                // the dried meat's first-join branch (the Veldt battle's "ガウ初回予約", which the user's
                // seed took, 2026-11-11): the reward in the battle, no scene reserved (vanilla reserves
                // sc_e_0239: the Blackjack parked at (1, 1), Gau's scene map, the Crescent Mountain
                // cave)
                var reserve = HarmonyLib.AccessTools.Method(typeof(Last.Interpreter.Instructions.SystemCall.Current), "ReserveScriptGauFirstJoin");
                bool InParty(int status) => UserDataManager.Instance()?.GetPartyMemberData()?.ToArray().Any(c => c?.characterStatusId == status) == true;
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        if (!InParty(12)) WcEvents.Join(12);
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, 31, 0);   // the check open
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag1, 126, 0);
                        Last.Interpreter.Integrator.Instance?.CancelReservedScript();
                        Check(reserve != null && InParty(12) && WcEvents.VeldtRewardEncounter(),
                              $"veldt_reward: Gau in the party ({InParty(12)}), the check open: the reward encounter");
                        int before = WcEvents.GauFirstJoins;
                        reserve?.Invoke(null, new object[] { new Last.Interpreter.MainCore() });
                        var integ = Last.Interpreter.Integrator.Instance;
                        Check(WcEvents.GauFirstJoins == before + 1, "veldt_reward: the first-join reservation reached the plugin");
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 31) == 1 && (WcEvents.LastReward ?? "").StartsWith("veldt"),
                              $"veldt_reward: the reward given in the battle ({WcEvents.LastReward})");
                        Check(integ == null || !integ.ExistsReserveScript(),
                              $"veldt_reward: no script reserved ({(integ != null && integ.ExistsReserveScript() ? integ.reservedScript : "none")})");
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag4, WcEvents.PickParty) == 0, "veldt_reward: no party pick pending");
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag1, 126) == 1, "veldt_reward: Gau met at the Veldt (ScenarioFlag1 126)");
                        // its line waits for the battle's notice window (the battle script shows it next)
                        var text = MessageManager.Instance?.GetMessage("WC_REWARD", false) ?? "";
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag4, WcEvents.RewardLine) == 1 && text.Length > 0 && !text.Contains("<CT>"),
                              $"veldt_reward: the line waits for the battle window ('{text}')");
                        // (a Gau back from a Leap, out of the party: the game's own rejoin. Not
                        // reproduced here: turning his party entry off leaves him in the party)
                        return false;
                    }),
                };
            }
    }
}
