// The veldt check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_veldt(string name)
        {
                // the Veldt opens a fight when its party is met elsewhere; a WC seed's encounters are
                // per-area copies of a formation's party (2000+): meeting one opens its formation's
                // Veldt fight (WcVeldt; the user's seed had a Veldt with nothing to fight)
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        var be = global::Last.Map.Encount.BeastFieldEncount.Instance();
                        var table = be?.targetGroupPartyIdTable;
                        Check(be != null && table != null && table.Count > 0, $"veldt: the Veldt's table ({table?.Count ?? 0} parties)");
                        if (be == null || table == null || be.openFlags == null) return false;
                        int copy = -1, src = -1, flag = -1, copies = 0;
                        foreach (var kv in MasterManager.Instance.GetList<MonsterParty>())
                        {
                            if (WcVeldt.Formation(kv.Key) < 0) continue;
                            copies++;
                            int s = WcVeldt.Source(kv.Key);
                            if (copy > 0 || !table.ContainsKey(s)) continue;
                            int f = table[s].PartyTable.flag_index;
                            if (f < 0 || f >= be.openFlags.Length || be.openFlags[f]) continue;   // open already
                            copy = kv.Key; src = s; flag = f;
                        }
                        Check(copy > 0, $"veldt: of the seed's {copies} copied parties, {copy} copies {src}, a closed Veldt fight (flag {flag})");
                        if (copy < 0) return false;
                        be.EncounterMonsterParty(copy);
                        Check(be.openFlags[flag], $"veldt: meeting party {copy} opened the Veldt fight of {src} (flag {flag}; {WcVeldt.Mapped})");
                        return false;
                    }),
                };
            }
    }
}
