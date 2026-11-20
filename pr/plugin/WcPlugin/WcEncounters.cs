using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Entity.Field;
using PrKit.Ff6;   // SnesEncounters: the SNES's danger counter on the PR's steps; SnesEncounterRules: its numbers

namespace WorldsCollide.PR
{
    // WC's (the SNES's) random battles in place of the PR's step counter (the user, 2026-11-11: "can
    // you ensure the encounter rate is on parity with the WC snes"). WC has no encounter-rate options,
    // so WC's are vanilla FF6's: the kit's vanilla rules (SnesEncounterRules) and these tables. The hook
    // itself (the danger counter, the PR's move counter holding it in the save, the party draw, the
    // re-roll after a battle) was written here 2026-11-11 and is the kit's since 2026-11-20
    // (PrKit/Ff6/SnesEncounters.cs), so Brave New World runs its own rates the same way.
    // Rate codes (vanilla ROM, tests/test_wc.py WcEncounterRates): other maps by the PR monster set,
    // whose vanilla SNES pack belongs to one SNES map; world maps by zone; zone 10's forest (set 418) is
    // the one terrain that differs from its zone. The World of Ruin is all normal.
    static class WcEncounters
    {
        // PR monster set -> SNES rate code (not listed: normal): SNES maps 41, 262, 263, 264, 313, 315 low, 405 high
        internal static readonly Dictionary<int, int> SetCode = new Dictionary<int, int>
        {
            [136] = 1, [209] = 1, [210] = 1, [211] = 1, [240] = 1, [242] = 1, [295] = 2,
        };
        // World of Balance zone (y / 32 x 8 + x / 32) -> SNES rate code (not listed: normal)
        internal static readonly Dictionary<int, int> WobZoneCode = new Dictionary<int, int>
        {
            [5] = 1, [10] = 1, [12] = 1, [17] = 1, [20] = 1, [22] = 2, [29] = 2,
        };
        const int Zone10Forest = 418;   // zone 10's forest (SNES pack 2): normal

        sealed class Wc : SnesEncounterMod
        {
            public override bool Active => Plugin.WcMode && WcSeeds.Active != null;
            public override string Tag => "wc";
            public override int RateCode(in SnesEncounterStep s)
            {
                if (s.World == 1 || s.World == 2)
                {
                    int code = s.World == 1 ? WobZoneCode.GetValueOrDefault(s.Zone, 0) : 0;
                    if (s.World == 1 && s.Zone == 10 && s.Set == Zone10Forest) code = 0;
                    return code;
                }
                return SetCode.GetValueOrDefault(s.Set, 0);
            }
            public override void Warn(string key, Exception e) => WcRunner.WarnOnce(key, e);
            public override long PerfStart() => WcPerf.Now();
            public override void PerfLap(long t0) => WcPerf.Lap(9, "encounters", t0);
        }

        public static void Patch(Harmony h) => SnesEncounters.Patch(h, new Wc());

        // for the self-test (WcTest encounters)
        internal static bool Paused { get => SnesEncounters.Paused; set => SnesEncounters.Paused = value; }
        internal static bool Off { get => SnesEncounters.Off; set => SnesEncounters.Off = value; }
        internal static bool CancelForTest { get => SnesEncounters.CancelForTest; set => SnesEncounters.CancelForTest = value; }
        internal static int SetUp => SnesEncounters.SetUp;
        internal static int Steps => SnesEncounters.Steps;
        internal static int Battles => SnesEncounters.Battles;
        internal static string Last => SnesEncounters.Last;
        internal static bool Advance(int danger, int rate, int roll, out int after) => SnesEncounters.Advance(danger, rate, roll, out after);
        internal static int RateHere(Last.Map.FieldController fc, int mapId, FieldEntity entity, bool bangle, out string where, out int ground) =>
            SnesEncounters.RateHere(fc, mapId, entity, bangle, out where, out ground);
    }
}
