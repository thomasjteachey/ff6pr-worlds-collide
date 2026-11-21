// The fade_selftest check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
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
        static List<(float, Func<bool>)> Plan_fade_selftest(string name)
        {
                // the black-screen check sees a black screen: faded out it says black, faded in not
                var black = new Color(0, 0, 0, 1);
                return new List<(float, Func<bool>)>
                {
                    (0, () => { Il2CppSystem.Fade.FadeManager.Instance.FadeOut(0.2f, black); return true; }),
                    (2, () => { var (b, s) = ScreenBlack(); Check(b, $"fade self-test: faded out reads black ({s})"); return true; }),
                    (0, () => { Il2CppSystem.Fade.FadeManager.Instance.FadeIn(0.2f, black); return true; }),
                    (2, () => { var (b, s) = ScreenBlack(); Check(!b, $"fade self-test: faded in reads not black ({s})"); return true; }),
                };
            }
    }
}
