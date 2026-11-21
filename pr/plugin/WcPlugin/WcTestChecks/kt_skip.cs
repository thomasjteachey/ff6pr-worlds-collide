// The kt_skip check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_kt_skip(string name)
        {
                // -fbs (both presets): Kefka's Tower's drop past the Demon statue (WC's Poltergeist:
                // map 564, the door at (36, 25), the ledge at (36, 28)) only once the Demon is beaten
                // (ScenarioFlag2 238); before, the drop pushes the party back (tools/wc_events.py)
                var cells = new Dictionary<int, UnityEngine.Vector3?>();
                var plan = new List<(float, Func<bool>)>();
                foreach (int beaten in new[] { 0, 1 })
                {
                    int b = beaten;
                    plan.Add((1f, () =>
                    {
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 238, b);
                        Check(WcEvents.GotoMap(564, 1), $"kt_skip: to the Demon's back door (from map 570), Demon {(b == 1 ? "beaten" : "standing")}");
                        return true;
                    }));
                    plan.Add((4f, () =>
                    {
                        // where things are at run time (cells, not the map file's tiles)
                        var fc = Field();
                        var found = new List<string>();
                        if (fc?.entityList != null)
                            foreach (var e in fc.entityList)
                            {
                                var pe = e?.Property?.TryCast<Last.Map.PropertyEvent>();
                                if (pe == null || pe.ScriptId <= 0) continue;
                                var c = fc.ConvertWorldPositionToCellPosition(e.transform.position);
                                found.Add($"{pe.ObjectType}/{pe.ActionId}:{pe.ScriptId}@({Mathf.Round(c.x)},{Mathf.Round(c.y)})");
                            }
                        Plugin.L.LogInfo($"[wc test] kt_skip: player at {WcEvents.PlayerCell()}, scripted {string.Join(" ", found)}");
                        return true;
                    }));
                    // one step onto the ledge (10, 9 at run time), then hands off: the test's stick
                    // reaches the field even while an event runs, which a player's can't
                    plan.Add((0.5f, () => { Walk(0, -1, 0.25f); return true; }));
                    plan.Add((3f, () =>
                    {
                        cells[b] = WcEvents.PlayerCell();
                        Plugin.L.LogInfo($"[wc test] kt_skip: Demon {(b == 1 ? "beaten" : "standing")}: walked south to {cells[b]} (map {UserDataManager.Instance()?.CurrentMapId})");
                        return true;
                    }));
                }
                plan.Add((0f, () =>
                {
                    bool on = WcSeeds.Active?.Flags.Split(' ').Contains("-fbs") == true;
                    var standing = cells.GetValueOrDefault(0); var beatenCell = cells.GetValueOrDefault(1);
                    Check(standing.HasValue && beatenCell.HasValue && (on ? standing.Value != beatenCell.Value : standing.Value == beatenCell.Value),
                          $"kt_skip (-fbs {(on ? "on" : "off")}): Demon standing -> {standing}, beaten -> {beatenCell} (the drop {(on ? "only once beaten" : "always open")})");
                    return false;
                }));
                return plan;
            }
    }
}
