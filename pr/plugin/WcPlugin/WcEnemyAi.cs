using System.Collections.Generic;
using HarmonyLib;
using PrKit.Ff6;   // SnesAi: the kit's FF6 AI interpreter; SnesAiHost: the battle it runs in

namespace WorldsCollide.PR
{
    // Every monster runs its Worlds Collide script the way the SNES runs it (the user, 2026-11-12: "please
    // make all enemies/bosses behave like WC SNES"): the seed carries each monster's SNES AI (main and
    // counter sections, tools/port_ai.py snes_ai_data), the kit's SnesAi.cs is FF6's interpreter and its
    // SnesAiHost the hooks that run it in the PR's battles (written here 2026-11-12..18, in the kit since
    // 2026-11-19 so Brave New World runs its own scripts the same way). This is WC's part: the seed's
    // data, its ability scaling, event battles' look sources, the seed's own battle lines.
    static class WcEnemyAi
    {
        sealed class Wc : SnesAiMod
        {
            public override SnesAiData Data => WcSeeds.Active?.SnesAi;
            public override bool Active => Plugin.WcMode && Data != null;
            public override string Tag => "wc";
            public override bool Verbose => WcTestState.Running;
            public override bool ScriptStatusEffects => false;   // not yet: WC's fights untested with them
            public override int PartySource(int party) =>
                WcSeeds.Active != null && WcSeeds.Active.LookSources.TryGetValue(party, out int ls) ? ls : party;
            public override void SetText(string key, string text) => WcEvents.SetText(key, text);

            // WC's scaled placeholders (WcAbilityScaling swaps them for a tier's spell when the act is made),
            // its special for WC's special tiers (5E-60), else the kit's map
            public override int AbilityFor(int snesMonster, int attack)
            {
                var d = Data;
                var sc = WcSeeds.Active?.Scaling;
                if (d != null && sc != null && attack != 0xEE && attack != 0xEF)
                {
                    if (attack >= 0x5E && attack <= 0x60) return SnesAiHost.BaseAbility(d, snesMonster, 0xEF);
                    int k = attack >= 0x36 && attack <= 0x50 ? attack - 0x36 : attack >= 0x55 && attack <= 0x5D ? attack - 0x3A : -1;
                    if (k >= 0 && k < sc.Count) return sc.Base + k;
                }
                return SnesAiHost.BaseAbility(d, snesMonster, attack);
            }
        }

        public static void Patch(Harmony h) => SnesAiHost.Patch(h, new Wc());
        internal static void Update() => SnesAiHost.Update();

        // for the self-test and the log
        internal static int Turns => SnesAiHost.Turns;
        internal static int Counters => SnesAiHost.Counters;
        internal static int Finals => SnesAiHost.Finals;
        internal static int Entries => SnesAiHost.Entries;
        internal static string LastTurn => SnesAiHost.LastTurn;
        internal static string LastCounter => SnesAiHost.LastCounter;
        internal static string LastFinal => SnesAiHost.LastFinal;
        internal static string LastEntry => SnesAiHost.LastEntry;
        internal static int LinesQueued => SnesAiHost.LinesQueued;
        internal static int LinesShown => SnesAiHost.LinesShown;
        internal static List<string> LinesSeen => SnesAiHost.LinesSeen;
    }
}
