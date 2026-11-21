// The fc_trip check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_fc_trip(string name)
        {
                // The Floating Continent from the Blackjack's wheel (the user's Deck, 2026-11-12:
                // "flying to the floating continent just puts me back on the blackjack deck and i'm
                // able to take the wheel again"): (Head to the Floating Continent.), the party menu
                // (stood in for), the flight, then the trip's deck under attack: no wheel (entity 9,
                // ev_e_0421), the air force's entities (1: Sky Armor, 4: Chupon); WC's three air force
                // fights, each on its timer (the script's parties 176/177); Ultros/Chupon's trigger
                // (entities 23-25, stepped on: sc_e_0565), Ultros/Chupon (478), Air Force (460), the
                // landing (map 431, ScenarioFlag2 30). Each fight is ended by force a few seconds in
                // (BattleEndJugment.isForce; a won fight's result screen waits for a button
                // headless), and the game goes on with the fight's next script.
                const int Deck = 119, Continent = 431, Wheel = 9;
                int F2(int i) => WcEvents.Flag(DataStorage.Category.kScenarioFlag2, i);
                var fights = new List<int>();
                float fightAt = -1;
                bool inFight = false, triggerSeen = false, stepped = false, wheelSeen = false, attackSeen = false, landed = false;
                int deckChecks = 0, airFights = -1, walkTries = 0, quiet = 0;
                string State()
                {
                    var b = BattlePlugManager.Instance();
                    var integ = Integrator.Instance;
                    string at;
                    try { at = $"{WcEvents.PlayerCell()}"; } catch (Exception) { at = "? (the field changing)"; }
                    return $"battle {(b != null && b.IsBattle() ? "on" : "off")}, field {(Object.FindObjectOfType<FieldMap>() != null ? "yes" : "no")}, map {UserDataManager.Instance()?.CurrentMapId}, " +
                           $"at {at}, script '{integ?.scriptName}' (working {integ?.working}, main {integ?.IsMainCoreAlive()}, reserved {integ?.ExistsReserveScript()}), " +
                           $"ScenarioFlag2 29/30 = {F2(29)}/{F2(30)}";
                }
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        // Shadow joined: the Floating Continent's character gate (WcEvents.GateOpen)
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, 100 + 4, 1);
                        var lead = UserDataManager.Instance()?.GetPartyMemberData();
                        WcEvents.PickRecruit = lead != null && lead.Count > 0 ? lead[0].characterStatusId : 1;   // the menu's stand-in keeps the leader
                        Check((UserDataManager.Instance()?.CurrentMapId ?? -1) == Deck && FieldEntity(Wheel) == "+",
                              $"fc_trip: on the Blackjack's deck with its wheel (map {UserDataManager.Instance()?.CurrentMapId}, wheel {FieldEntity(Wheel)})");
                        WcEvents.TestAnswerKeys.Enqueue("E0559_00_119_c_01");
                        Check(WcEvents.RunScript("sc_e_BJoutboard"), "fc_trip: the wheel: (Head to the Floating Continent.)");
                        return true;
                    }),
                };
                bool Report()
                {
                    Check(deckChecks > 0 && attackSeen, $"fc_trip: the trip's deck was the air force's ({deckChecks} looks, Sky Armor/Chupon {(attackSeen ? "there" : "missing")}; ScenarioFlag2 29 = {F2(29)})");
                    Check(!wheelSeen, "fc_trip: no wheel on the trip's deck (ev_e_0421)");
                    var air = fights.Where(p => p == 176 || p == 177).Count();
                    Check(air == 3, $"fc_trip: WC's three air force fights ({air}; the script's parties [{string.Join(" ", fights)}])");
                    Check(triggerSeen, "fc_trip: Ultros/Chupon's trigger placed after them");
                    Check(fights.Contains(478) && fights.Contains(460) && fights.IndexOf(478) < fights.IndexOf(460),
                          $"fc_trip: Ultros/Chupon (478), then Air Force (460): [{string.Join(" ", fights)}]");
                    Check(landed, $"fc_trip: landed on the Floating Continent (map {UserDataManager.Instance()?.CurrentMapId}, ScenarioFlag2 30 = {F2(30)})");
                    return false;
                }
                for (int k = 0; k < 120; k++)
                {
                    bool last = k == 119;
                    steps.Add((2f, () =>
                    {
                        if (landed || last) return Report();
                        if (triggerSeen && quiet++ < 60) Plugin.L.LogInfo($"[wc test] fc_trip: {State()}");
                        var b = BattlePlugManager.Instance();
                        float now = Time.realtimeSinceStartup;
                        int map = UserDataManager.Instance()?.CurrentMapId ?? -1;
                        if (b != null && b.IsBattle())
                        {
                            if (!inFight)
                            {
                                inFight = true;
                                fightAt = now;
                                fights.Add(WcBattles.CurrentOriginal);
                                Plugin.L.LogInfo($"[wc test] fc_trip: fight {fights.Count}: the script's party {WcBattles.CurrentOriginal} (party {b.MonsterParty?.Id}; {WcBattles.LastSwap})");
                            }
                            else if (fights[fights.Count - 1] < 0 && WcBattles.CurrentOriginal > 0)
                            {
                                // seen before the plugin had noted the script's party
                                fights[fights.Count - 1] = WcBattles.CurrentOriginal;
                                Plugin.L.LogInfo($"[wc test] fc_trip: fight {fights.Count}: the script's party {WcBattles.CurrentOriginal}");
                            }
                            else if (now - fightAt > 4)
                            {
                                var j = b.BattleEndJugment;
                                if (j != null && !j.isForce)
                                {
                                    j.SetEnabled(true);
                                    j.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal;
                                    j.isForce = true;
                                    Plugin.L.LogInfo($"[wc test] fc_trip: fight {fights.Count} ended by force");
                                }
                            }
                            return true;
                        }
                        if (Object.FindObjectOfType<FieldMap>() == null) return true;   // between field and battle
                        inFight = false;
                        if (map == Deck && F2(29) == 1)
                        {
                            deckChecks++;
                            if (FieldEntity(Wheel) == "+") wheelSeen = true;
                            if (FieldEntity(1) == "+" || FieldEntity(4) == "+") attackSeen = true;
                            if (!triggerSeen && FieldEntity(23) == "+")
                            {
                                triggerSeen = true;
                                airFights = fights.Count;
                                Plugin.L.LogInfo($"[wc test] fc_trip: Ultros/Chupon's trigger placed after {fights.Count} fights");
                            }
                            if (triggerSeen && !stepped && !WcEvents.EventBusy())
                            {
                                // the trigger is a column at x 22, rows 6-8; the party stands at (14, 6):
                                // walk right onto it, as a player does (the touch runs sc_e_0565); a test-
                                // started script's fight may not get its next script
                                stepped = true;
                                walkTries++;
                                Walk(1, 0, 6);
                            }
                            else if (stepped && fights.Count == airFights && walkTries < 3 && !WcEvents.EventBusy() && walkUntil < 0)
                            {
                                walkTries++;
                                if (walkTries < 3) Walk(1, 0, 6);
                                else Check(WcEvents.RunScript("sc_e_0565"), $"fc_trip: the walk didn't reach the trigger (at {WcEvents.PlayerCell()}): its script run instead");
                            }
                        }
                        if (map == Continent && F2(30) == 1)
                        {
                            landed = true;
                            Plugin.L.LogInfo($"[wc test] fc_trip: landed on the Floating Continent after {fights.Count} fights");
                        }
                        return true;
                    }));
                }
                return steps;
            }
    }
}
