using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace WorldsCollide.PR
{
    // WC's rage / lore / dance options:
    //  -srr / -slr / -sdr: random start rages / lores / dances (wc_seed.py start_skills): known by
    //        whoever holds the command (taught on joins and in the field sync; WcSkills shares them);
    //  -rnl: no Leap (the command is unavailable) and every won battle teaches its enemies' rages
    //        (data/rages.py no_leap: C2/4A07 after each battle). Rages are the party's in the SNES:
    //        ones won while nobody holds Rage wait for a holder (this session).
    static class WcRages
    {
        const int RageCommand = 23, LoreCommand = 17, DanceCommand = 22, LeapCommand = 24;
        internal static string LastRage = "";
        static readonly List<int> battleMonsters = new List<int>();
        static readonly HashSet<int> pending = new HashSet<int>();

        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(Serial.FF6.Management.CommandAvailableData), "IsAvailable");
            if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(WcRages), nameof(Available_Postfix)));
        }

        static bool On(string flag) => Plugin.WcMode && WcSeeds.Active != null && WcSeeds.Active.Flags.Split(' ').Contains(flag);

        public static void Available_Postfix(global::Last.Data.Master.Command command, ref bool __result)
        {
            if (__result && command != null && command.Id == LeapCommand && On("-rnl")) __result = false;
        }

        static List<global::Last.Data.User.OwnedCharacterData> Holders(int command)
        {
            var list = new List<global::Last.Data.User.OwnedCharacterData>();
            var owned = global::Last.Management.UserDataManager.Instance()?.OwnedCharacterList;
            if (owned == null) return list;
            foreach (var c in owned)
            {
                if (c == null || c.characterStatusId < 1 || c.characterStatusId > 14 || c.CommandList == null) continue;
                foreach (var x in c.CommandList) if (x != null && x.Id == command) { list.Add(c); break; }
            }
            return list;
        }

        static int TeachAll(int command, IEnumerable<int> abilities)
        {
            int n = 0;
            var ids = abilities.ToList();
            foreach (var c in Holders(command))
                foreach (var id in ids)
                    if (!WcSkills.Knows(c, id) && WcSkills.Teach(c, id)) n++;
            return n;
        }

        // the seed's start skills (and rages won while nobody held Rage) to the holders
        internal static string StartSkills()
        {
            var s = WcSeeds.Active;
            if (s == null || !Plugin.WcMode) return "";
            int n = 0;
            try
            {
                if (s.StartSkills.TryGetValue("rages", out var r)) n += TeachAll(RageCommand, r);
                if (s.StartSkills.TryGetValue("lores", out var l)) n += TeachAll(LoreCommand, l);
                if (s.StartSkills.TryGetValue("dances", out var d)) n += TeachAll(DanceCommand, d);
                if (pending.Count > 0 && Holders(RageCommand).Count > 0) { n += TeachAll(RageCommand, pending); pending.Clear(); }
                if (pendingLores.Count > 0 && Holders(LoreCommand).Count > 0) { n += TeachAll(LoreCommand, pendingLores); pendingLores.Clear(); }
            }
            catch (Exception e) { WcRunner.WarnOnce("start-skills", e); }
            if (n > 0)
            {
                LastRage = $"start skills: {n} taught";
                Plugin.L.LogInfo("[wc] " + LastRage);
            }
            return n > 0 ? LastRage : "";
        }

        static readonly HashSet<int> battleLores = new HashSet<int>();

        internal static void BattleStart(IEnumerable<int> monsters)
        {
            battleMonsters.Clear();
            battleMonsters.AddRange(monsters.Distinct());
            battleLores.Clear();
        }

        internal static void SeenLore(int ability) => battleLores.Add(ability);

        // WC's -lel (lores learnable without the Lore command): the lores enemies used in a won battle
        // go to the Lore holders (the PR teaches them only with Strago fighting)
        static void LearnLores()
        {
            if (battleLores.Count == 0 || !On("-lel")) { battleLores.Clear(); return; }
            var lores = battleLores.ToList();
            battleLores.Clear();
            if (Holders(LoreCommand).Count == 0) { foreach (var l in lores) pendingLores.Add(l); return; }
            int n = TeachAll(LoreCommand, lores);
            if (n > 0) { LastRage = $"-lel: lores {string.Join(",", lores)} ({n} taught)"; Plugin.L.LogInfo("[wc] " + LastRage); }
        }

        static readonly HashSet<int> pendingLores = new HashSet<int>();

        // a battle was won (WcTrack.BattleWon): -rnl teaches its enemies' rages (ability 799 + monster)
        internal static void BattleWon()
        {
            try { LearnLores(); } catch (Exception e) { WcRunner.WarnOnce("lores-won", e); }
            if (!On("-rnl") || battleMonsters.Count == 0) return;
            try
            {
                var mm = global::Last.Data.Master.MasterManager.Instance;
                var rages = battleMonsters.Where(m => m >= 1 && m <= 255).Select(m => 799 + m)
                                          .Where(id => mm.GetData<global::Last.Data.Master.Ability>(id) != null).ToList();
                battleMonsters.Clear();
                if (rages.Count == 0) return;
                if (Holders(RageCommand).Count == 0) { foreach (var r in rages) pending.Add(r); return; }
                int n = TeachAll(RageCommand, rages);
                if (n > 0) { LastRage = $"-rnl: rages {string.Join(",", rages)} ({n} taught)"; Plugin.L.LogInfo("[wc] " + LastRage); }
            }
            catch (Exception e) { WcRunner.WarnOnce("rages-won", e); }
        }
    }
}
