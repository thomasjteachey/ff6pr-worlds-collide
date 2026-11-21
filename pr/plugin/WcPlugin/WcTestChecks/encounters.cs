// The encounters check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_encounters(string name)
        {
                // WC's (the SNES's) random battles (WcEncounters): on the World of Balance south of
                // Narshe (zone 10), the cell's SNES rate, and the hook's danger counter over 3000 steps
                // at that rate: the mean steps per battle against the SNES's (exact means from
                // tests/test_wc.py's model); then walking on with the hook live: the game counts the
                // steps through it and a battle comes from a step. (The game's CheckEncount can't be
                // called from managed code: its "in List" parameter's type doesn't load.)
                var means = new Dictionary<int, double>
                {
                    [0xC0] = 23.35, [0x60] = 33.61, [0x180] = 16.22,                 // world: normal, low, high
                    [0x70] = 31.1, [0x40] = 41.3, [0x160] = 17.09, [0x200] = 13.86, // other maps
                };
                int battlesBefore = 0, stepsBefore = 0, setUpBefore = 0;
                string scaledBefore = "";
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        WcEncounters.Paused = true;
                        Check(WcEvents.GotoMap(WcEvents.WobMap, 1), "encounters: warp on foot to the WoB map (Narshe's door)");
                        return true;
                    }),
                    (6, () => { Walk(0, -1, 2.5f); return true; }),   // south, off the town's cells
                    (4f, () =>
                    {
                        var player = Field()?.player;
                        if (player == null) { Check(false, "encounters: no player"); WcEncounters.Paused = false; return false; }
                        int mapNow = UserDataManager.Instance()?.CurrentMapId ?? 0;
                        int rate = WcEncounters.RateHere(Field(), mapNow, player, false, out string where, out int ground);
                        // a step's work, timed: the field search a step used to make (FindObjectOfType<FieldMap>:
                        // walking stuttered on the Deck) and the rate lookup it makes now
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        for (int i = 0; i < 200; i++) Object.FindObjectOfType<FieldMap>();
                        double findMs = sw.Elapsed.TotalMilliseconds / 200;
                        var fcNow = Field();
                        sw.Restart();
                        for (int i = 0; i < 200; i++) WcEncounters.RateHere(fcNow, mapNow, player, false, out _, out _);
                        double rateMs = sw.Elapsed.TotalMilliseconds / 200;
                        Plugin.L.LogInfo($"[wc test] encounters: per step, the old field search {findMs:0.000} ms, the rate lookup now {rateMs:0.000} ms");
                        Check(rateMs < 0.5, $"encounters: a step's rate lookup {rateMs:0.000} ms (the old field search alone {findMs:0.000} ms)");
                        Check(rate == 0x60 || rate == 0xC0, $"encounters: zone 10's rate at {WcEvents.PlayerCell()}: {rate:X} ({where}; the game's ground value {ground})");
                        var rng = new System.Random(1234);
                        var lens = new List<int>();
                        int danger = 0, since = 0;
                        for (int i = 0; i < 3000; i++)
                        {
                            since++;
                            if (!WcEncounters.Advance(danger, rate, rng.Next(256), out danger)) continue;
                            lens.Add(since);
                            since = 0;
                            danger = 0;
                        }
                        double mean = lens.Count > 0 ? lens.Average() : 0, want = means.GetValueOrDefault(rate, 0);
                        Check(want > 0 && lens.Count >= 50 && Math.Abs(mean - want) <= 0.12 * want,
                              $"encounters: {lens.Count} battles in 3000 steps at rate {rate:X}: {mean:0.0} steps per battle (SNES {want}); shortest {(lens.Count > 0 ? lens.Min() : 0)}, longest {(lens.Count > 0 ? lens.Max() : 0)}");
                        WcEncounters.Paused = false;
                        battlesBefore = WcEncounters.Battles;
                        stepsBefore = WcEncounters.Steps;
                        scaledBefore = WcScaling.LastReport;
                        // the danger near its top (high byte 240: a battle on 94% of steps), so the walk's
                        // next steps bring one through the game's own CheckEncount and battle start (the
                        // mean steps per battle are checked above; Narshe's door leaves a few tiles to walk)
                        var lot = (HarmonyLib.AccessTools.Property(typeof(Last.Map.FieldController), "encountLot")?.GetValue(Field())
                                   as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Last.Map.EncounterLot>();
                        Check(lot != null, "encounters: the field's encounter lot");
                        if (req.GetValueOrDefault("vanilla", "0") == "1")
                        {
                            // the PR's own random battle (the hook off, its threshold reached): whether the
                            // field's encounter start runs headless at all
                            WcEncounters.Off = true;
                            lot?.SetRestoreData(1, 1);
                            Plugin.L.LogInfo("[wc test] encounters: the hook off, the PR's own threshold reached");
                        }
                        else
                        {
                            lot?.SetRestoreData(0xF000, 70000);
                            WcEncounters.CancelForTest = true;   // the battle's start can't run headless
                            setUpBefore = WcEncounters.SetUp;
                        }
                        Walk(0, -1, 10);   // on with the hook live: the game's own steps and battle start
                        return true;
                    }),
                };
                // every 10 s: a battle from a step ends it; else on in another direction (2 minutes at most)
                var dirs = new[] { new Vector2(1, 0), new Vector2(0, -1), new Vector2(-1, 0), new Vector2(0, 1) };
                for (int k = 0; k < 12; k++)
                {
                    int round = k;
                    steps.Add((10f, () =>
                    {
                        // the game's own CheckEncount set the hook's battle up (its party picked, the
                        // battle preloaded); the test calls it off there (headless, the start would end the run)
                        bool fought = WcEncounters.SetUp > setUpBefore || (WcEncounters.Battles > battlesBefore && WcScaling.LastReport != scaledBefore);
                        if (fought || round == 11)
                        {
                            WcEncounters.CancelForTest = false;
                            Check(WcEncounters.Steps > stepsBefore, $"encounters: the walk's steps went through the hook ({WcEncounters.Steps - stepsBefore}; last {WcEncounters.Last})");
                            Check(fought, $"encounters: walking, the game set up a battle from a step ({WcEncounters.SetUp - setUpBefore} set up, {WcEncounters.Battles - battlesBefore} rolled, after {WcEncounters.Steps - stepsBefore} steps, {round * 10 + 10} s; last {WcEncounters.Last}; at {WcEvents.PlayerCell()})");
                            return false;
                        }
                        var d = dirs[round % dirs.Length];
                        Plugin.L.LogInfo($"[wc test] encounters: {WcEncounters.Steps - stepsBefore} steps, {WcEncounters.Battles - battlesBefore} battles so far; last {WcEncounters.Last}");
                        Walk(d.x, d.y, 10);
                        return true;
                    }));
                }
                return steps;
            }
    }
}
