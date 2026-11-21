// The getitem check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_getitem(string name)
        {
                // the game's own GetItem op in a script, as vanilla's story pick-ups give gil (the Cave to
                // the Sealed Gate's sc_e_0453: 293 gil): the porter's scripted chests give their contents
                // that way in mods without the WC plugin (BNW)
                int had = -1;
                return new List<(float, Func<bool>)>
                {
                    (0, () => { Check(WcEvents.GotoMap(408, 1), "getitem: to the Cave to the Sealed Gate (map 408)"); return true; }),
                    (4, () =>
                    {
                        had = UserDataManager.Instance()?.OwendGil ?? -1;
                        Check(WcEvents.RunScript("sc_e_0453"), "getitem: sc_e_0453 ran");
                        return true;
                    }),
                    (5, () =>
                    {
                        int now = UserDataManager.Instance()?.OwendGil ?? -1;
                        Check(now == had + 293, $"getitem: gil {had} -> {now} (GetItem [1, 293])");
                        return false;
                    }),
                };
            }
    }
}
