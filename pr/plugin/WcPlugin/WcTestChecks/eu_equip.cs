// The eu_equip check's plan (2026-11-21): -eu's equip menu, and the code it used to share.
using System;
using System.Collections.Generic;
using System.Linq;
using Last.Data.Master;
using Last.Management;

namespace WorldsCollide.PR
{
    static partial class WcTest
    {
        // -eu (WC equipable_umaro): Umaro's equip menu changes every slot. The PR's lock,
        // CharacterInfomationData.IsChangeEquipmentOnlyAccessory ("id == 14"), shares its machine code (RVA 0x4421B0)
        // with three lambdas "v == BattleFlagType.NoARBattle" (ArBattleTopController, both LibraryInfoControllers):
        // WcRecruits swaps the class's vtable entry instead of hooking that code. With the seed's -eu on and then off
        // (its flags changed in memory for the test, then put back):
        //  * the game's question as its equip menus ask it (CharacterInfomationProvider, the virtual call): Umaro
        //    relics only = !-eu, Terra never;
        //  * the shared code as the game shipped it (83 FA 0E 0F 94 C0 C3: no detour written over it);
        //  * the three lambdas (called through their own method info, i.e. the shared code): NoARBattle -> true,
        //    NoGil -> false, whatever -eu is; and a monster party flagged NoARBattle (its BattleFlagGroup) reads
        //    the same with -eu on and off.
        static List<(float, Func<bool>)> Plan_eu_equip(string name)
        {
            const int Umaro = 14, Terra = 1;
            const Last.Defaine.Master.BattleFlagType NoAR = Last.Defaine.Master.BattleFlagType.NoARBattle;
            var lambdas = new (string Name, Func<Last.Defaine.Master.BattleFlagType, bool> Call)[]
            {
                ("ArBattleTopController <InitMonsterPartyList>b__52_3",
                 v => (Last.UI.Common.ArBattleTopController.__c.__9 ?? new Last.UI.Common.ArBattleTopController.__c())._InitMonsterPartyList_b__52_3(v)),
                ("LibraryInfoController (Touch) <SetData>b__21_3",
                 v => (Last.UI.Touch.LibraryInfoController.__c.__9 ?? new Last.UI.Touch.LibraryInfoController.__c())._SetData_b__21_3(v)),
                ("LibraryInfoController (KeyInput) <SetData>b__25_3",
                 v => (Last.UI.KeyInput.LibraryInfoController.__c.__9 ?? new Last.UI.KeyInput.LibraryInfoController.__c())._SetData_b__25_3(v)),
            };
            // a monster party whose battle flags include NoARBattle (MonsterParty.BattleFlagGroupId -> BattleFlagGroup rows)
            int arParty = 0;
            var arFlags = new List<int>();
            void FindArParty()
            {
                var groups = new Dictionary<int, List<int>>();
                foreach (var kv in MasterManager.Instance.GetList<BattleFlagGroup>())
                    if (kv.Value != null)
                    {
                        if (!groups.TryGetValue(kv.Value.GroupId, out var l)) groups[kv.Value.GroupId] = l = new List<int>();
                        l.Add(kv.Value.BattleFlagId);
                    }
                foreach (var kv in MasterManager.Instance.GetList<MonsterParty>())
                {
                    var p = kv.Value;
                    if (p == null || p.BattleFlagGroupId <= 0 || !groups.TryGetValue(p.BattleFlagGroupId, out var flags) || !flags.Contains((int)NoAR)) continue;
                    arParty = p.Id;
                    arFlags = flags;
                    break;
                }
            }
            Func<bool> run = () =>
            {
                var seed = WcSeeds.Active;
                Check(seed != null, "eu_equip: a WC seed is active");
                if (seed == null) return false;
                Check(WcRecruits.EquipSwapped, $"eu_equip: the class's vtable entry is the plugin's ({WcRecruits.EquipState})");
                var bytes = WcRecruits.SharedCodeBytes();
                Check(bytes == "83 FA 0E 0F 94 C0 C3", $"eu_equip: the shared code (RVA 0x4421B0) is the game's own: {bytes}");
                FindArParty();
                Check(arParty > 0, $"eu_equip: a monster party flagged NoARBattle: {arParty} (flags [{string.Join(" ", arFlags)}])");
                string keep = seed.Flags;
                bool seedEu = keep.Split(' ').Contains("-eu");
                var arRead = new Dictionary<bool, string>();
                try
                {
                    foreach (bool eu in new[] { true, false })
                    {
                        var rest = keep.Split(' ').Where(f => f != "-eu" && f.Length > 0);
                        seed.Flags = string.Join(" ", eu ? rest.Append("-eu") : rest);
                        string state = $"-eu {(eu ? "on" : "off")}{(eu == seedEu ? " (the seed's)" : " (for the test)")}";
                        bool? umaro = WcRecruits.MenuOnlyRelics(Umaro), terra = WcRecruits.MenuOnlyRelics(Terra);
                        Check(umaro == !eu && terra == false,
                              $"eu_equip ({state}): the equip menu's relics-only answer: Umaro {umaro?.ToString() ?? "no provider"}, Terra {terra?.ToString() ?? "no provider"}");
                        foreach (var (lname, call) in lambdas)
                        {
                            bool ar = call(NoAR), gil = call(Last.Defaine.Master.BattleFlagType.NoGil);
                            Check(ar && !gil, $"eu_equip ({state}): {lname}: NoARBattle {ar}, NoGil {gil} (the game's: True, False)");
                        }
                        arRead[eu] = string.Join(" ", arFlags.Select(f => $"{f}:{(lambdas[1].Call((Last.Defaine.Master.BattleFlagType)f) ? "AR" : "-")}"));
                    }
                }
                finally { seed.Flags = keep; }
                Check(arParty > 0 && arRead[true] == arRead[false] && arRead[true].Contains($"{(int)NoAR}:AR"),
                      $"eu_equip: party {arParty}'s battle flags read the same with -eu on [{arRead[true]}] and off [{arRead[false]}]");
                Check(WcRecruits.MenuOnlyRelics(Umaro) == !seedEu, $"eu_equip: the seed's flags back (-eu {(seedEu ? "on" : "off")})");
                return false;
            };
            return new List<(float, Func<bool>)> { (1f, run) };
        }
    }
}
