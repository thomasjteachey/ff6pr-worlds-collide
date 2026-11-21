// The monster_box check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_monster_box(string name)
        {
                // a monster-in-a-box the seed changed (-cms moves packs between boxes, -fer changes
                // packs: both presets): opened, its battle is the one the port gave its script (the
                // plugin's event remap leaves box scripts alone), built on the box's own background
                // (port_chests.box_look), not the party's home one
                var boxes = new (string Script, string Folder, int Map, int Point, int Own)[]
                {
                    ("sc_e_0021", "map_20011/Assets/GameAssets/Serial/Res/Map/Map_20011/Map_20011_2", 5, 2, 516),
                    ("sc_e_0698", "map_30651/Assets/GameAssets/Serial/Res/Map/Map_30651/Map_30651_10", 465, 1, 434),
                    ("sc_e_0742", "map_30761/Assets/GameAssets/Serial/Res/Map/Map_30761/Map_30761_2", 489, 1, 127),
                    ("sc_e_0786", "map_30771/Assets/GameAssets/Serial/Res/Map/Map_30771/Map_30771_3", 495, 2, 236),
                    ("sc_e_0641", "map_30611/Assets/GameAssets/Serial/Res/Map/Map_30611/Map_30611_1", 433, 1, 212),
                    ("sc_e_0642", "map_30611/Assets/GameAssets/Serial/Res/Map/Map_30611/Map_30611_2", 434, 2, 212),
                };
                int Fights(string path)
                {
                    if (!File.Exists(path)) return -1;
                    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
                        foreach (var m in doc.RootElement.GetProperty("Mnemonics").EnumerateArray())
                        {
                            var mn = m.GetProperty("mnemonic").GetString();
                            if (mn == "Encount" || mn == "EncountBoss") return m.GetProperty("operands").GetProperty("iValues")[0].GetInt32();
                        }
                    return -1;
                }
                int Background(int party) => MasterManager.Instance.GetData<MonsterParty>(party)?.BattleBackgroundAssetId ?? -1;
                (string Script, string Folder, int Map, int Point, int Own) box = default;
                int want = -1;
                foreach (var b in boxes)
                {
                    int p = -1;
                    try { p = Fights(Path.Combine(WcSeeds.Active?.Dir ?? "", "files", b.Folder, b.Script + ".json")); }
                    catch (Exception e) { Check(false, $"monster_box: {b.Script}'s seed script: {e.Message}"); }
                    if (p < 0 || p == b.Own) continue;
                    // a box fighting a copy on its own background first (the port's parties from 2000)
                    if (want < 0 || (want < 2000 && p >= 2000)) { box = b; want = p; }
                }
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        Check(want > 0, $"monster_box: the seed changed a box's battle ({box.Script}: party {want}, vanilla {box.Own})");
                        if (want <= 0) return false;
                        Check(WcEvents.GotoMap(box.Map, box.Point), $"warp to {box.Script}'s map ({box.Map})");
                        return true;
                    }),
                    (4, () =>
                    {
                        WcBattles.BuiltParty = WcBattles.BuiltBackground = WcBattles.PartyBackground = -1;
                        Check(WcEvents.RunScript(box.Script), $"monster_box: {box.Script} ran (the box opened)");
                        return true;
                    }),
                    (12, () =>
                    {
                        var b = BattlePlugManager.Instance();
                        var ids = b?.GetEnemyUnits()?.Select(e => e?.GetMonster()?.Id ?? 0).Where(i => i > 0).ToList() ?? new List<int>();
                        var expect = WcBattles.Members(want);
                        Check(b != null && b.IsBattle() && WcBattles.BuiltParty == want && ids.Count > 0 && ids.All(expect.Contains),
                              $"monster_box: the battle is party {WcBattles.BuiltParty}, monsters [{string.Join(" ", ids)}] (the seed's {want}: [{string.Join(" ", expect)}])");
                        // a script's Encount builds the battle with background 0: the party's then
                        int shown = WcBattles.BuiltBackground > 0 ? WcBattles.BuiltBackground : WcBattles.PartyBackground;
                        Check(shown == Background(box.Own),
                              $"monster_box: on background {shown} (built with {WcBattles.BuiltBackground}, the party's {WcBattles.PartyBackground}), the box's {Background(box.Own)}");
                        return false;
                    }),
                };
            }
    }
}
