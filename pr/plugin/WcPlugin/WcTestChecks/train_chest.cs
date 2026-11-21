// The train_chest check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_train_chest(string name)
        {
                // the Phantom Train's rack (map 366): the PR's Tent pick-up stands for SNES chest 109,
                // and chest 110 beside it (the PR dropped it) joins it (port_chests.MERGED): picked
                // up, it gives both of the seed's items, each with its line
                const string Rel = "map_30441/Assets/GameAssets/Serial/Res/Map/Map_30441/Map_30441_10/sc_e_1107.json";
                var gifts = new List<(int Item, int Num)>();
                try
                {
                    var path = Path.Combine(WcSeeds.Active?.Dir ?? "", "files", Rel);
                    if (File.Exists(path))
                        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
                            foreach (var m in doc.RootElement.GetProperty("Mnemonics").EnumerateArray())
                            {
                                if (m.GetProperty("mnemonic").GetString() != "SysCall") continue;
                                var call = m.GetProperty("operands").GetProperty("sValues")[0].GetString().Split(' ');
                                if (call[0] == "WC:item") gifts.Add((int.Parse(call[1]), int.Parse(call[2])));
                            }
                }
                catch (Exception e) { Check(false, $"train_chest: the seed's script: {e.Message}"); }
                int Count(int item)
                {
                    int n = 0;
                    var all = UserDataManager.Instance()?.GetAllOwnedItemsClone();
                    if (all != null) foreach (var it in all) if (it != null && it.ContentId == item) n += it.Count;
                    return n;
                }
                var had = new Dictionary<int, int>();
                return new List<(float, Func<bool>)>
                {
                    (0, () => { Check(WcEvents.GotoMap(366, 4), "warp to the Phantom Train's rack car (map 366)"); return true; }),
                    (3, () =>
                    {
                        foreach (var g in gifts) had[g.Item] = Count(g.Item);
                        WcEvents.TestMessages.Clear();
                        // two in a seed that fills both rack chests (WC's shuffle may leave one empty)
                        Check(gifts.Count >= 1, $"train_chest: the pick-up gives {gifts.Count} of the seed's items ({string.Join(", ", gifts.Select(g => $"{g.Item} x{g.Num}"))})");
                        if (gifts.Count > 0) Check(WcEvents.RunScript("sc_e_1107"), "train_chest: its script ran");
                        return true;
                    }),
                    (4, () =>
                    {
                        var want = gifts.GroupBy(g => g.Item).ToDictionary(x => x.Key, x => x.Sum(g => g.Num));
                        bool got = want.All(kv => Count(kv.Key) == had[kv.Key] + kv.Value);
                        int lines = WcEvents.TestMessages.Count(m => m.StartsWith("WC_REWARD: <CT>Obtained"));
                        Check(got && lines == gifts.Count,
                              $"train_chest: {string.Join(", ", want.Select(kv => $"item {kv.Key} {had[kv.Key]} -> {Count(kv.Key)}"))}; lines {string.Join(" | ", WcEvents.TestMessages)}");
                        return true;
                    }),
                };
            }
    }
}
