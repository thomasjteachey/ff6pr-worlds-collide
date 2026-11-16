using System;
using System.Collections.Generic;
using System.Linq;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // Umaro's attack odds as BNW has them (C2/5269, bnw_data.json "umaro"; readme "Yeti Attack
    // Odds"): Tackle, and Snowstorm / the ally toss when their relic is worn, each 75 of 255, Fight
    // the rest: none 70% Fight, one relic 40%, both 10%. (Powers are data: port.py
    // ability_overrides, Tackle 255, toss 382.)
    // The PR (decompiled tools/decomp/umaro, ActSelectUmalo.SetActDataUmalo): a table per
    // umaloType (0 none, 1 Rage Ring, 2 Blizzard Orb, 3 both, 4 Magitek) of ability -> percent,
    // inserted in the order below, then roll = CommonUtility.GetRand(100) and the first entry whose
    // running total passes the roll is used. The table is local to the method, so the plugin
    // picks the attack with BNW's weights and, during that one call, answers GetRand(100) with a
    // roll that lands on it in the PR's table. Magitek (type 4) keeps the PR's odds.
    static class UmaroRules
    {
        internal static bool Enabled = true;
        static readonly Random Rng = new Random();
        static int pendingRoll = -1;

        // the PR's tables (ability id, percent), in insertion order
        static readonly (int Ability, float Pct)[][] GameTables =
        {
            new[] { (1, 50f), (201, 50f) },
            new[] { (1, 25f), (201, 37.5f), (200, 37.5f) },
            new[] { (1, 25f), (201, 37.5f), (199, 37.5f) },
            new[] { (1, 13f), (201, 29f), (200, 29f), (199, 29f) },
        };

        public static void Apply(HarmonyLib.Harmony h)
        {
            if (!Enabled || EsperHooks.D.UmaroRows.Length != 4) return;
            Hooks.Patch(h, typeof(Last.Battle.ActSelectUmalo), "SetActDataUmalo", typeof(UmaroRules),
                prefix: nameof(Pick_Prefix), postfix: nameof(Pick_Postfix));
            Hooks.Patch(h, typeof(global::CommonUtility), "GetRand", typeof(UmaroRules),
                prefix: nameof(GetRand_Prefix), args: new[] { typeof(int) });
        }

        // BNW's pick for a relic set (0-3): an ability id
        internal static int BnwPick(int type, Func<int, int> rand)
        {
            var d = EsperHooks.D;
            var row = d.UmaroRows[type];
            var options = Enumerable.Range(0, Math.Min(row.Length, d.UmaroColumns.Length))
                .Where(i => row[i] != 0xFF).Select(i => (Ability: d.UmaroColumns[i], Weight: row[i])).ToList();
            int total = options.Sum(o => o.Weight);
            int x = rand(total);
            foreach (var o in options)
            {
                if (x < o.Weight) return o.Ability;
                x -= o.Weight;
            }
            return options.Last().Ability;
        }

        // the GetRand(100) answer that makes the PR's table pick this ability (-1: not in the table)
        internal static int RollFor(int type, int ability)
        {
            float before = 0;
            foreach (var (a, pct) in GameTables[type])
            {
                if (a == ability) return Math.Min(99, (int)Math.Ceiling(before));
                before += pct;
            }
            return -1;
        }

        public static void Pick_Prefix(Last.Battle.ActSelectUmalo __instance)
        {
            pendingRoll = -1;
            if (!Enabled || __instance == null) return;
            try
            {
                int type = (int)__instance.umaloType;
                if (type < 0 || type > 3) return;  // Magitek: the PR's own
                int pick = BnwPick(type, n => Rng.Next(n));
                pendingRoll = RollFor(type, pick);
                Hooks.NoteOnce("umaro-odds", () => $"[umaro] BNW odds: relic set {type} -> ability {pick} (roll {pendingRoll})");
            }
            catch (Exception e) { Hooks.WarnOnce("UmaroRules.Pick", e); }
        }

        public static void Pick_Postfix() => pendingRoll = -1;

        public static bool GetRand_Prefix(int max, ref int __result)
        {
            if (pendingRoll < 0 || max != 100) return true;
            __result = pendingRoll;
            pendingRoll = -1;
            return false;
        }

        // Startup check: BNW's odds per relic set over 25,600 picks, each pick's roll landing on it
        // in the PR's table, and the GetRand hook answering a pending roll.
        internal static string Check()
        {
            if (!Enabled || EsperHooks.D.UmaroRows.Length != 4) return "[umaro] odds: the game's own";
            var rng = new Random(1);
            var parts = new List<string>();
            for (int t = 0; t < 4; t++)
            {
                var counts = new Dictionary<int, int>();
                bool lands = true;
                for (int i = 0; i < 25600; i++)
                {
                    int a = BnwPick(t, n => rng.Next(n));
                    counts[a] = counts.TryGetValue(a, out var c) ? c + 1 : 1;
                    lands &= GamePick(t, RollFor(t, a)) == a;
                }
                parts.Add($"set {t}: " + string.Join(" ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value * 100.0 / 25600:F0}%")) +
                          (lands ? "" : " (a roll MISSES)"));
            }
            pendingRoll = 37;
            int got = global::CommonUtility.GetRand(100);
            pendingRoll = -1;
            int free = global::CommonUtility.GetRand(100);
            return $"[umaro] BNW odds {string.Join("; ", parts)} | GetRand hook {(got == 37 ? "ok" : $"NOT applied ({got})")}, otherwise random ({free})";
        }

        // what the PR's loop picks for a roll
        static int GamePick(int type, int roll)
        {
            float sum = 0;
            foreach (var (a, pct) in GameTables[type])
            {
                sum += pct;
                if (sum > roll) return a;
            }
            return 0;
        }
    }
}
