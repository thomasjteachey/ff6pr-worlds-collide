// The empty_chest check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_empty_chest(string name)
        {
                // a chest the PR keeps empty that the seed filled (port_chests.fill_empty): South
                // Figaro's basement room (map 52); opened, its script gives the seed's item and line
                const string Rel = "map_20071/Assets/GameAssets/Serial/Res/Map/Map_20071/Map_20071_14/sc_e_no_treasure_32.json";
                int item = -1, num = 0;
                try
                {
                    var path = Path.Combine(WcSeeds.Active?.Dir ?? "", "files", Rel);
                    if (File.Exists(path))
                        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
                            foreach (var m in doc.RootElement.GetProperty("Mnemonics").EnumerateArray())
                            {
                                if (m.GetProperty("mnemonic").GetString() != "SysCall") continue;
                                var call = m.GetProperty("operands").GetProperty("sValues")[0].GetString().Split(' ');
                                if (call[0] != "WC:item") continue;
                                item = int.Parse(call[1]);
                                num = int.Parse(call[2]);
                                break;
                            }
                }
                catch (Exception e) { Check(false, $"empty_chest: the seed's script: {e.Message}"); }
                int Count()   // every kind (equipment too: SearchOwnedItem finds items only)
                {
                    int n = 0;
                    var all = item < 0 ? null : UserDataManager.Instance()?.GetAllOwnedItemsClone();
                    if (all != null) foreach (var it in all) if (it != null && it.ContentId == item) n += it.Count;
                    return n;
                }
                int had = 0;
                return new List<(float, Func<bool>)>
                {
                    (0, () => { Check(WcEvents.GotoMap(52, 1), "warp to South Figaro's basement room (map 52)"); return true; }),
                    (3, () =>
                    {
                        had = Count();
                        WcEvents.TestMessages.Clear();
                        Check(item >= 0, $"empty_chest: the seed fills it (item {item} x{num})");
                        if (item >= 0) Check(WcEvents.RunScript("sc_e_no_treasure_32"), "empty_chest: its script ran");
                        return true;
                    }),
                    (4, () =>
                    {
                        if (item >= 0)
                            Check(Count() == had + num && WcEvents.TestMessages.Any(m => m.StartsWith("WC_REWARD: <CT>Obtained")),
                                  $"empty_chest: item {item} {had} -> {Count()}; lines {string.Join(" | ", WcEvents.TestMessages)}");
                        return true;
                    }),
                };
            }
    }
}
