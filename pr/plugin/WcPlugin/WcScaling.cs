using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using Last.Battle;
using Last.Data.Master;
using Last.Management;
using Random = System.Random;

namespace WorldsCollide.PR
{
    // WC's enemy scaling (battle/scaling.py, scaling_functions.py, load_enemy_level.py; assembly
    // in WC), applied to each battle's monsters once the battle is up:
    //  level  -ls{a,h,ce,ced,c,t,bd} F : scaled level = count x F (time: minutes / F), clamped to
    //         [3, -msl]; each monster's level = scaled + random(-1..2), at least 3
    //  HP/MP  -hm{...} F : value = (value / original level) x HP/MP scale level, when value > level
    //  EXP/gil -xg{...} F: the same with the EXP/gil scale level (BattleAddExpPlug.GetExp/GetGil)
    //  count: a = party average level, h = highest, ce = characters recruited + espers found,
    //  ced = + dragons defeated, c = checks done, t = play time, bd = bosses + dragons defeated.
    //  Dragons and the final battles aren't scaled unless -sed / -sfb (formation_flags.py).
    // Checks done and dragons defeated come from the Track (WcTrack); bosses defeated aren't
    // counted yet (0), so "bd" is dragons only. WC's objective results "Add Enemy / Dragon /
    // Final Levels" (WcObjectives.LevelBonus) add to the levels once complete.
    static class WcScaling
    {
        static readonly Random Rng = new Random();
        static readonly string[] Kinds = { "a", "h", "ce", "ced", "c", "t", "bd" };
        static readonly HashSet<int> Dragons = new HashSet<int> { 367, 368, 369, 370, 371, 372, 373, 482 };  // PR party = SNES formation + 1
        static readonly HashSet<int> Finals = new HashSet<int> { 472, 513, 514, 515 };

        sealed class Rule { public string Kind; public double Factor; }
        static string flagsFor;
        static Rule level, hpmp, xpgp;
        static int maxLevel = 99;
        static bool scaleDragons, scaleFinals;
        static IntPtr battleSeen;
        static int waitFrames;
        static int xpScaleLevel;   // for this battle's rewards (0 = not scaled)
        static int lastParty = -1; // this battle's monster party (boss objectives)
        public static string LastReport = "";

        public static void Patch(Harmony h)
        {
            var exp = AccessTools.Method(typeof(BattleAddExpPlug), "GetExp");
            var gil = AccessTools.Method(typeof(BattleAddExpPlug), "GetGil");
            if (exp != null) h.Patch(exp, postfix: new HarmonyMethod(typeof(WcScaling), nameof(Exp_Postfix)));
            if (gil != null) h.Patch(gil, postfix: new HarmonyMethod(typeof(WcScaling), nameof(Gil_Postfix)));
        }

        static void Refresh()
        {
            var flags = WcSeeds.Active?.Flags ?? "";
            if (flags == flagsFor) return;
            flagsFor = flags;
            var v = new FlagValues(WcScreen.Defs);
            v.Parse(flags);
            Rule Find(string prefix)
            {
                foreach (var k in Kinds)
                {
                    var flag = "-" + prefix + k;
                    if (!v.IsOn(flag)) continue;
                    var vals = v.Values(flag);
                    double f = vals.Count > 0 && double.TryParse(vals[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 1;
                    return new Rule { Kind = k, Factor = f };
                }
                return null;
            }
            level = Find("ls");
            hpmp = Find("hm");
            xpgp = Find("xg");
            maxLevel = v.IsOn("-msl") && v.Values("-msl").Count > 0 && int.TryParse(v.Values("-msl")[0], out int m) ? m : 99;
            scaleDragons = v.IsOn("-sed");
            scaleFinals = v.IsOn("-sfb");
            Plugin.L.LogInfo($"[wc] scaling: level {Describe(level)}, HP/MP {Describe(hpmp)}, EXP/gil {Describe(xpgp)}, max {maxLevel}" +
                             $"{(scaleDragons ? ", dragons too" : "")}{(scaleFinals ? ", final battles too" : "")}");
        }

        static readonly Dictionary<HashSet<int>, HashSet<int>> monstersOf = new Dictionary<HashSet<int>, HashSet<int>>();

        // monster ids in the given formations (MonsterParty.Monster1..), from the master data
        static HashSet<int> MonstersOf(HashSet<int> parties)
        {
            if (monstersOf.TryGetValue(parties, out var set)) return set;
            set = new HashSet<int>();
            var mm = MasterManager.Instance;
            foreach (var id in parties)
            {
                var row = mm?.GetData<MonsterParty>(id);
                if (row == null) continue;
                for (int k = 1; k <= 12; k++)
                {
                    var prop = typeof(MonsterParty).GetProperty("Monster" + k);
                    if (prop?.GetValue(row) is int m && m > 0) set.Add(m);
                }
            }
            monstersOf[parties] = set;
            return set;
        }

        static string Describe(Rule r) => r == null ? "off" : $"{r.Kind} x{r.Factor}";

        // the count a rule scales from, for the party in this battle
        static int Count(string kind, BattlePlugManager battle)
        {
            var udm = UserDataManager.Instance();
            switch (kind)
            {
                case "a":
                case "h":
                {
                    var lv = new List<int>();
                    foreach (var p in battle.GetPlayerUnits())
                    {
                        var c = p?.TryCast<BattlePlayerData>()?.ownedCharacterData;
                        if (c?.Parameter != null) lv.Add(c.Parameter.ConfirmedLevel());
                    }
                    if (lv.Count == 0) return 3;
                    return kind == "a" ? lv.Sum() / lv.Count : lv.Max();
                }
                case "ce":
                case "ced":
                {
                    // OwnedCharacterList contains records for characters the player has not
                    // recruited yet. Counting those records made a new WC game look as though it
                    // already had all fourteen characters, so -lsce/-lsced could leave early-game
                    // enemies near their original (late-game) level. The recruitment flags are
                    // the same source WC uses for gates and its Track counter.
                    int chars = WcTrack.CharactersFound();
                    int espers = udm?.OwnedMagicStoneList?.Count ?? 0;
                    return chars + espers + (kind == "ced" ? WcTrack.DragonsDefeated() : 0);
                }
                case "t":
                    return (int)((udm?.PlayTime ?? 0) / 60.0);
                case "c":
                    return WcTrack.Checks.Count(WcTrack.Done);
                case "bd":
                    return WcTrack.DragonsDefeated() + WcTrack.BossesDefeated();
                default:
                    return 0;
            }
        }

        static int ScaleLevel(Rule r, BattlePlugManager battle)
        {
            double count = Count(r.Kind, battle);
            double value = r.Kind == "t" ? count / Math.Max(0.5, r.Factor) : count * r.Factor;
            return Math.Clamp((int)value, 3, maxLevel);
        }

        public static void Update()
        {
            if (!Plugin.WcMode || WcSeeds.Active == null) return;
            var battle = BattlePlugManager.Instance();
            if (battle == null || !battle.IsBattle()) { battleSeen = IntPtr.Zero; return; }
            var enemies = battle.GetEnemyUnits();
            if (enemies == null || enemies.Length == 0 || battle.Pointer == battleSeen) return;
            // the formation is set a few frames after the monsters, if at all (battles started by
            // events/encounter calls can leave it unset): dragons/finals are also told by monster
            if (battle.MonsterParty == null && ++waitFrames < 30) return;
            waitFrames = 0;
            battleSeen = battle.Pointer;
            int started = battle.MonsterParty?.Id ?? -1;
            try { started = WcBattles.BattleStarted(battle); } catch (Exception e) { WcRunner.WarnOnce("battle-origin", e); }
            try { WcObjectives.BattleStart(battle); } catch (Exception e) { WcRunner.WarnOnce("battle-start", e); }
            xpScaleLevel = 0;
            try
            {
                Refresh();
                int party = battle.MonsterParty?.Id ?? started;
                lastParty = party;
                var ids = enemies.Select(e => e?.GetMonster()?.Id ?? 0).Where(i => i > 0).ToList();
                WcRages.BattleStart(ids);
                bool dragon = Dragons.Contains(party) || (ids.Count > 0 && ids.All(i => MonstersOf(Dragons).Contains(i)));
                bool final = Finals.Contains(party) || ids.Any(i => MonstersOf(Finals).Contains(i));
                bool boss = WcSeeds.Active?.Bosses.Any(b => b.Party == party) == true;
                int bonus = WcObjectives.LevelBonus(dragon ? "Add Dragon Levels" : final ? "Add Final Levels" : boss ? "Add Boss Levels" : "Add Enemy Levels");
                bool scaled = !((dragon && !scaleDragons) || (final && !scaleFinals));
                if ((level == null && hpmp == null && xpgp == null) || !scaled)
                {
                    if (bonus == 0)
                    {
                        if (!scaled) { LastReport = $"formation {party}: not scaled (dragon / final battle)"; Plugin.L.LogInfo("[wc] " + LastReport); }
                        return;
                    }
                    var added = new List<string>();
                    foreach (var e in enemies)
                    {
                        var p = e?.BattleUnitDataInfo?.Parameter;
                        if (p == null) continue;
                        p.BaseLevel = Math.Min(99, p.BaseLevel + bonus);
                        added.Add($"{e.GetMonster()?.Id}: Lv {p.BaseLevel}");
                    }
                    LastReport = $"formation {party}: objective levels +{bonus} only; {string.Join("; ", added)}";
                    Plugin.L.LogInfo("[wc] " + LastReport);
                    return;
                }
                int lvScale = level != null ? ScaleLevel(level, battle) : 0;
                int hpScale = hpmp != null ? ScaleLevel(hpmp, battle) : 0;
                xpScaleLevel = xpgp != null ? ScaleLevel(xpgp, battle) : 0;
                var report = new List<string>();
                foreach (var e in enemies)
                {
                    var p = e?.BattleUnitDataInfo?.Parameter;
                    var monster = e?.GetMonster();
                    if (p == null || monster == null) continue;
                    int origLv = Math.Max(1, monster.Lv);
                    if (hpmp != null)
                    {
                        if (monster.Hp > origLv) { p.BaseMaxHp = Math.Max(1, monster.Hp / origLv * hpScale); p.CurrentHP = p.BaseMaxHp; }
                        if (monster.Mp > origLv) { p.BaseMaxMp = monster.Mp / origLv * hpScale; p.CurrentMP = p.BaseMaxMp; }
                    }
                    if (level != null) p.BaseLevel = Math.Max(3, lvScale + Rng.Next(4) - 1);
                    if (bonus != 0) p.BaseLevel = Math.Min(99, p.BaseLevel + bonus);
                    report.Add($"{monster.Id}: Lv {origLv}->{p.BaseLevel}, HP {monster.Hp}->{p.BaseMaxHp}");
                }
                LastReport = $"formation {party}: level {lvScale}, HP/MP {hpScale}, EXP/gil {xpScaleLevel}; {string.Join("; ", report)}";
                Plugin.L.LogInfo("[wc] scaled " + LastReport);
            }
            catch (Exception e) { WcRunner.WarnOnce("scaling", e); }
        }

        static int ScaleReward(Func<Monster, int> value, int gameTotal)
        {
            if (xpScaleLevel <= 0 || gameTotal <= 0) return gameTotal;
            var list = BattlePlugManager.Instance()?.BattleAddExpPlug?.expeditionMonsters;
            if (list == null || list.Count == 0) return gameTotal;
            long orig = 0, scaled = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null) continue;
                int v = value(m), lv = Math.Max(1, m.Lv);
                orig += v;
                scaled += v > lv ? (long)(v / lv) * xpScaleLevel : v;
            }
            if (orig <= 0) return gameTotal;
            return (int)Math.Min(int.MaxValue / 4, gameTotal * scaled / orig);
        }

        internal static int Victories;   // battles won this session (the self-test's "the battle ended")

        public static void Exp_Postfix(ref int __result)
        {
            Victories++;
            if (!Plugin.WcMode) return;
            // the battle was won: WC's boss objectives (the party as it was at the battle's start)
            try { WcTrack.BattleWon(BattlePlugManager.Instance()?.MonsterParty?.Id ?? lastParty); } catch (Exception e) { WcRunner.WarnOnce("boss-won", e); }
            try { __result = ScaleReward(m => m.Exp, __result); } catch (Exception e) { WcRunner.WarnOnce("scale-exp", e); }
        }

        public static void Gil_Postfix(ref int __result)
        {
            if (!Plugin.WcMode) return;
            try { __result = ScaleReward(m => m.Gill, __result); } catch (Exception e) { WcRunner.WarnOnce("scale-gil", e); }
        }
    }
}
