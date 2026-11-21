using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Last.Data.Master;
using Last.Data.User;
using Last.Interpreter;
using Last.Management;
using SysCall = Last.Interpreter.Instructions.SystemCall.Current;

namespace WorldsCollide.PR
{
    // Join levels as WC's SNES game has them (pr/SNES_PR_DISCREPANCIES.md, "Join levels"; coordinator, 2026-11-20).
    // The PR levels every join of a character who isn't in the roster (SystemCall.Current.JoinCharacter ->
    // CorrectLevel(id, isFirstTimeJoin); BNW's JoinLevels.cs has the decompile): the average of the other available
    // characters, plus correctLevelAtFirstTime on a first join, raise only, with that level's exp and stat-ups.
    // WC's SNES game never does: its recruit (WC instruction/c0.py recruit_character) replaces vanilla's character
    // init, so a recruit keeps the seed's start level (-stl), or with -sal takes the average of those recruited
    // before (WcRecruits.Joined, right after the join); a rejoin keeps its level. WC's only event-77 averaging
    // (Mobliz WoR, Moogle Defense, with -sal) is on characters joining for the first time, which WcRecruits averages.
    // Before this (WC test logs, -sal seeds): Cyan at 5 and Mog at 8 right after joining (average 3 + the PR's
    // offsets +2/+5), put back to 3 by -sal; without -sal they kept that level with FreshRecruit's start-level HP,
    // and any recruited character rejoining the roster was raised to the others' average.
    // Config [Rules] JoinLevels = SNES (default) / PR. WcTest check=snes_rules runs SelfTest.
    static class WcJoinLevels
    {
        internal static bool Snes = true;   // Plugin.cs: config [Rules] JoinLevels
        internal static string LastJoin = "";

        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(SysCall), "CorrectLevel");
            if (m == null) { Plugin.L.LogWarning("[wc] no Current.CorrectLevel: the PR's join levelling stays"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(WcJoinLevels), nameof(CorrectLevel_Prefix)));
            Plugin.L.LogInfo($"[wc] join levels: {(Snes ? "SNES (the PR's join averaging off in WC games)" : "the PR's")}");
        }

        static bool WcGame() => Plugin.WcMode && WcEvents.Flag(DataStorage.Category.kScenarioFlag4, WcEvents.WcGameFlag) == 1;

        public static bool CorrectLevel_Prefix(int characterStatusId, bool isFirstTimeJoin)
        {
            try
            {
                if (!Snes || !WcGame() || characterStatusId < 1 || characterStatusId > 14) return true;
                LastJoin = $"character {characterStatusId} {(isFirstTimeJoin ? "first join" : "rejoin")}: level {Level(characterStatusId)} kept " +
                           $"(the PR would raise it to {PrTarget(characterStatusId, isFirstTimeJoin)})";
                Plugin.L.LogInfo("[wc] join levels: " + LastJoin);
                return false;
            }
            catch (Exception e) { WcRunner.WarnOnce("join-levels", e); return true; }
        }

        static OwnedCharacterData Find(int id, bool all)
        {
            var list = UserDataManager.Instance()?.GetOwnedCharactersClone(all);
            if (list != null) foreach (var c in list) if (c != null && c.characterStatusId == id) return c;
            return null;
        }

        static int Level(int id) => Find(id, true)?.Parameter?.ConfirmedLevel() ?? 0;

        // what CorrectLevel would have made it: the other available characters' average (1 when none), plus the
        // first-join offset; raise only
        static int PrTarget(int id, bool first)
        {
            int sum = 0, n = 0;
            var list = UserDataManager.Instance()?.GetOwnedCharactersClone(false);
            if (list != null)
                foreach (var c in list)
                {
                    if (c == null || c.characterStatusId == id || c.characterStatusId >= 15 || c.Parameter == null) continue;
                    sum += c.Parameter.AddtionalLevel;
                    n++;
                }
            int target = n == 0 ? 1 : sum / n;
            if (first)
            {
                var table = SysCall.correctLevelAtFirstTime;
                if (table != null && id - 1 < table.Length) target += table[id - 1];
            }
            return Math.Max(Level(id), Math.Min(99, Math.Max(1, target)));
        }

        // WcTest (check=snes_rules): the game's own JoinCharacter. A recruited character rejoining the roster keeps
        // its level (the others raised well past it first, so the PR's averaging would show); a first join without
        // -sal keeps the seed's start level. Changes the roster and levels for this run only (test runs never save).
        internal static string SelfTest()
        {
            var udm = UserDataManager.Instance();
            var seed = WcSeeds.Active;
            if (udm == null || seed == null || !WcGame()) return "join levels: no WC game loaded";
            var lines = new List<string>();
            bool ok = true;
            void Check(bool pass, string line) { ok &= pass; lines.Add((pass ? "ok: " : "FAILED: ") + line); }
            string flags = seed.Flags;
            try
            {
                var roster = new List<OwnedCharacterData>();
                foreach (var o in udm.GetOwnedCharactersClone(false))
                    if (o != null && o.characterStatusId >= 1 && o.characterStatusId <= 14 && o.Parameter != null && WcEvents.IsRecruited(o.characterStatusId))
                        roster.Add(o);
                if (roster.Count < 2) return $"join levels: need two recruited characters in the roster (have {roster.Count})";
                var rejoiner = roster.OrderBy(o => o.Parameter.ConfirmedLevel()).First();
                int id = rejoiner.characterStatusId, before = rejoiner.Parameter.ConfirmedLevel();
                // the others raised past it (the live data: OwnedCharacterList, not the clones)
                foreach (var c in udm.OwnedCharacterList)
                    if (c != null && c.characterStatusId != id && roster.Any(r => r.characterStatusId == c.characterStatusId) &&
                        c.Parameter.ConfirmedLevel() < before + 10)
                        WcRecruits.RaiseTo(c, Math.Min(99, before + 10));
                SysCall.SecedeCharacter(id, false);
                LastJoin = "";
                SysCall.JoinCharacter(id, -1, (Last.Defaine.User.CorpsId)1, false, false, false);
                bool back = Find(id, false) != null;
                Check(back && Level(id) == before && Snes,
                      $"rejoin: character {id} level {before} -> {Level(id)} (WC keeps it){(back ? "" : " NOT back in the roster")}; {LastJoin}");

                // a first join without -sal: the seed's start level
                int newcomer = Enumerable.Range(1, 14).FirstOrDefault(n => !WcEvents.IsRecruited(n) && Find(n, false) == null);
                if (newcomer > 0)
                {
                    seed.Flags = string.Join(" ", flags.Split(' ').Where(f => f != "-sal"));
                    int start = MasterManager.Instance.GetData<CharacterStatus>(newcomer)?.Lv ?? 0;
                    LastJoin = "";
                    WcEvents.Join(newcomer);
                    Check(Level(newcomer) == start,
                          $"first join without -sal: character {newcomer} at level {Level(newcomer)}, the seed's start level {start}; {LastJoin}");
                }
                else lines.Add("skipped the first join: everyone is recruited");
            }
            catch (Exception e) { ok = false; lines.Add("FAILED: " + e.GetType().Name + ": " + e.Message); }
            finally { seed.Flags = flags; }
            return $"join levels: self-test {(ok ? "ok" : "FAILED")}; " + string.Join("; ", lines);
        }
    }
}
