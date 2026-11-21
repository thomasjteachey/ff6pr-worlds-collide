// The doma_play check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_doma_play(string name)
        {
                // Doma's siege played through for a side-by-side with WC on the SNES (the user, 2026-11-11:
                // "play through the doma siege event on both the WC snes and pr WC port and tell me of
                // any behavior/number/timing discrepancies"): into Doma (map 75) from its entrance with
                // Cyan recruited, the siege not on and the check open; a per-frame record (map, party cell,
                // a scene running, the screen black, messages, battle, flags, espers) as "[doma] t ..."
                // lines; once the party is free, the captain talked to (the game's own NPC action), the
                // battle on auto, then the reward and where the party ends up. Messages don't wait for a
                // button here (no input headless), so scene times are without reading time.
                float t0 = 0, freeAt = -1, battleAt = -1, endAt = -1, settledAt = -1;
                var seen = new Dictionary<string, string>();
                int msgs = 0;
                void Note(string k, string v)
                {
                    if (seen.TryGetValue(k, out var old) && old == v) return;
                    seen[k] = v;
                    Plugin.L.LogInfo($"[doma] {Time.realtimeSinceStartup - t0:0.00} s  {k} {v}");
                }
                void Record()
                {
                    var udm = UserDataManager.Instance();
                    var battle = BattlePlugManager.Instance();
                    bool inBattle = battle != null && battle.IsBattle();
                    Note("map", (udm?.CurrentMapId ?? -1).ToString());
                    if (!inBattle)
                    {
                        string cell = "-";
                        try { cell = WcEvents.PlayerCell()?.ToString() ?? "-"; } catch (Exception) { }   // mid map change
                        Note("cell", cell);
                    }
                    bool busy = WcEvents.EventBusy();
                    Note("scene", busy ? $"running {Last.Interpreter.Integrator.Instance?.scriptName}" : "idle");
                    var (black, _) = ScreenBlack();
                    Note("screen", black ? "black" : "shown");
                    Note("battle", inBattle ? $"party {battle.MonsterParty?.Id}" : "none");
                    if (inBattle) Note("monsters", WcScaling.LastReport);
                    Note("flags", $"siege {WcEvents.Flag(DataStorage.Category.kScenarioFlag1, 72)} scene {WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 140)} check {WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 57)}");
                    Note("espers", (udm?.OwnedMagicStoneList?.Count ?? 0).ToString());
                    while (msgs < WcEvents.TestMessages.Count) Plugin.L.LogInfo($"[doma] {Time.realtimeSinceStartup - t0:0.00} s  message   {WcEvents.TestMessages[msgs++]}");
                    // free: the siege scene has run (ScenarioFlag4 140) and nothing runs, the screen up, for a second
                    bool settled = !busy && !black && (udm?.CurrentMapId ?? -1) == 75 && WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 140) == 1;
                    if (!settled) settledAt = -1;
                    else if (settledAt < 0) settledAt = Time.realtimeSinceStartup;
                    if (freeAt < 0 && settledAt > 0 && Time.realtimeSinceStartup - settledAt > 1) freeAt = settledAt;
                    if (battleAt < 0 && inBattle) battleAt = Time.realtimeSinceStartup;
                }
                Last.Entity.Field.FieldEntity Npc(int script)
                {
                    var fc = Field();
                    for (int id = 0; id < 256; id++)
                    {
                        Last.Entity.Field.FieldEntity e = null;
                        try { e = fc?.GetFieldEntity(id); } catch (Exception) { }
                        int sid = -1;
                        try { sid = e?.Property?.TryCast<Last.Map.PropertyEvent>()?.ScriptId ?? -1; } catch (Exception) { }
                        if (sid == script) return e;
                    }
                    return null;
                }
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        if (!WcEvents.IsRecruited(3)) WcEvents.Join(3);
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag1, 72, 0);    // the siege not on
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, 57, 0);    // the check open
                        Plugin.L.LogInfo($"[doma] party {string.Join(" ", UserDataManager.Instance().GetPartyMemberData().ToArray().Select(c => $"{c.characterStatusId}:Lv{c.Parameter.ConfirmedLevel()}"))}");
                        // the World of Balance's point outside Doma's gate (157,86), below its exits (156/157,85)
                        Check(WcEvents.GotoMap(WcEvents.WobMap, 36), "doma_play: onto the World of Balance map outside Doma");
                        return true;
                    }),
                    (6, () =>
                    {
                        // north onto Doma's world map exit, as a player walks in
                        WcEvents.TestMessages.Clear();
                        t0 = Time.realtimeSinceStartup;
                        recorder = Record;
                        Plugin.L.LogInfo($"[doma] at {WcEvents.PlayerCell()} on map {UserDataManager.Instance()?.CurrentMapId}: walking north into Doma");
                        Walk(0, 1, 3);
                        return true;
                    }),
                };
                // the party free on the siege map: talk to the captain (sc_e_0110, script 139)
                for (int k = 0; k < 12; k++)
                {
                    steps.Add((2.5f, () =>
                    {
                        if (freeAt < 0) return true;
                        if (battleAt >= 0) return true;
                        var cap = Npc(139);
                        var cell = WcEvents.PlayerCell();
                        Plugin.L.LogInfo($"[doma] {Time.realtimeSinceStartup - t0:0.00} s  free at {freeAt - t0:0.00} s; party at {cell}; captain {(cap == null ? "not found" : cap.transform.position.ToString())}; soldiers {string.Join(" ", Enumerable.Range(131, 8).Select(s => Npc(s) != null ? "y" : "n"))}");
                        var ep = Object.FindObjectOfType<MainGame>()?.eventProcedure;
                        if (cap == null || ep == null) { Check(false, "doma_play: the captain to talk to"); return false; }
                        var act = ep.RequestEntityAction(cap);
                        WcEvents.Running = act != null;
                        Plugin.L.LogInfo($"[doma] {Time.realtimeSinceStartup - t0:0.00} s  talked to the captain: {(act == null ? "no action" : "his script")}");
                        battleAt = 0;   // asked: the battle comes next
                        return true;
                    }));
                }
                // the battle on auto until it ends, then what follows (up to 3 minutes)
                for (int k = 0; k < 36; k++)
                {
                    int round = k;
                    steps.Add((5f, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        bool inBattle = battle != null && battle.IsBattle();
                        if (inBattle)
                        {
                            var ui = Last.UI.BattleUIManager.Instance;
                            if (ui != null && !ui.IsAuto()) ui.SetIsAuto(true);
                            return true;
                        }
                        int check = WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 57);
                        if (check == 1 && !WcEvents.EventBusy() && !ScreenBlack().Item1)
                        {
                            if (endAt < 0) endAt = Time.realtimeSinceStartup;
                            if (Time.realtimeSinceStartup - endAt < 5) return true;
                            recorder = null;
                            Check(true, $"doma_play: done at {endAt - t0:0.00} s (free at {freeAt - t0:0.00} s); party at {WcEvents.PlayerCell()} on map {UserDataManager.Instance()?.CurrentMapId}");
                            return false;
                        }
                        if (round == 35) { recorder = null; Check(false, $"doma_play: no end in 3 minutes (check flag {check}, scene {Last.Interpreter.Integrator.Instance?.scriptName})"); return false; }
                        return true;
                    }));
                }
                return steps;
            }
    }
}
