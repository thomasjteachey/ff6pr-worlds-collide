// The doma_start check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_doma_start(string name)
        {
                // walking into Doma (map 75) in the WoB with Cyan, the siege not on, the check open:
                // the siege scene (sc_e_0099_3) runs to its end and fades in. The user's Deck,
                // 2026-11-11: entering froze on the last frame (the map script had started the scene
                // with ChangeScript while the map was loading; the scene calls that map script itself)
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        if (!WcEvents.IsRecruited(3)) WcEvents.Join(3);
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag1, 72, 0);    // the siege not on
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, 57, 0);    // the check open
                        Check(WcEvents.GotoMap(75, 1), "doma_start: into Doma (map 75)");
                        return true;
                    }),
                    (40, () =>
                    {
                        var (black, why) = ScreenBlack();
                        bool busy = WcEvents.EventBusy();
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag1, 72) == 1, "doma_start: the siege is on (ScenarioFlag1 72)");
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 140) == 1, "doma_start: the siege scene ran (ScenarioFlag4 140)");
                        Check(!busy, $"doma_start: no scene left running ({Last.Interpreter.Integrator.Instance?.scriptName})");
                        Check(!black, $"doma_start: the screen faded in ({why})");
                        Check((UserDataManager.Instance()?.CurrentMapId ?? -1) == 75, $"doma_start: on Doma's map ({UserDataManager.Instance()?.CurrentMapId})");
                        return false;
                    }),
                };
            }
    }
}
