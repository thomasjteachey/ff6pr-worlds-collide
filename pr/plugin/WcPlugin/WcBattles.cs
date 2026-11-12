using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Last.Interpreter;
using Last.Interpreter.Instructions;
using Last.Management;

namespace WorldsCollide.PR
{
    // WC's boss / dragon / statue shuffles (-bbs -bbr -drloc -stloc -bmbd -srp3) and random fixed
    // encounters (-fer): the seed's event battles that changed, as PR monster parties (wc_seed.py
    // battle_remap). The PR's events name their parties in the script: Encount lists up to four
    // candidates (iValues 0-3), EncountBoss one (iValues 0). Both are script commands
    // (External.Misc), so the swap happens there: the party ids are replaced for the call and put
    // back after it (the script's operands are shared by every run of that script).
    static class WcBattles
    {
        internal static string LastSwap = "";

        public static void Patch(Harmony h)
        {
            int n = 0;
            foreach (var (name, count) in new[] { ("Encount", 4), ("EncountBoss", 1) })
            {
                var m = AccessTools.Method(typeof(global::Last.Interpreter.Instructions.External.Misc), name);
                if (m == null) continue;
                h.Patch(m, prefix: new HarmonyMethod(typeof(WcBattles), count == 4 ? nameof(Encount_Prefix) : nameof(Boss_Prefix)),
                        postfix: new HarmonyMethod(typeof(WcBattles), nameof(Restore_Postfix)));
                n++;
            }
            Plugin.L.LogInfo($"[wc] battles: {n} of 2 encounter commands hooked");
            // the background a battle is built on (the headless monster-box test reads it)
            var create = AccessTools.Method(typeof(global::Last.Battle.InstantiateManager), "Create");
            if (create != null) h.Patch(create, postfix: new HarmonyMethod(typeof(WcBattles), nameof(Create_Postfix)));
            var bgId = AccessTools.Method(typeof(global::Last.Battle.BattleEnemyInstanceData), "GetBackgroundId");
            if (bgId != null) h.Patch(bgId, postfix: new HarmonyMethod(typeof(WcBattles), nameof(BackgroundId_Postfix)));
            var act = AccessTools.Method(typeof(Serial.FF6.Calc.FunctionProvider), "CreateActFunction");
            if (act != null) h.Patch(act, prefix: new HarmonyMethod(typeof(WcBattles), nameof(Act_Prefix)));
        }

        // The Cultists' Tower's magic-only status goes with the place, as on the SNES (the tower's
        // events set battle event bit $3EBB bit 0; the PR puts its SysCall in the tower parties'
        // battle script 9024). The seed gives a swapped party the tower's script where the event is
        // the tower's and the target has no script of its own, and the default where the target is a
        // tower party fought elsewhere (wc_seed.place_script). A boss with a battle script of its own
        // (Air Force, Chadarnook, Hidon...) keeps its script; for it, the status comes from here: the
        // game's own SysCall (SystemCall.Current.ZealotTowerConditionStart, which reads only the
        // battle's player units, never its MainCore: tools/decomp/zealot), as the battle is first seen
        // with its units (BattleStarted, from WcScaling.Update, where the monsters get scaled), before
        // its intro ends. (A Harmony prefix on External.Battle.WaitToBattleStart, where sc_b_2024 runs
        // it, threw at patch time in the headless test, 2026-11-12, and took the battle hooks after it.)
        const int TowerScript = 9024;
        static bool towerPending;
        static float towerPendingAt;
        internal static int TowerStarts;   // tests: how many times the status was given

        static int ScriptOf(int party) =>
            global::Last.Data.Master.MasterManager.Instance?.GetData<global::Last.Data.Master.MonsterParty>(party)?.ScriptNameId ?? -1;

        static void TowerStart()
        {
            if (!towerPending) return;
            towerPending = false;
            try
            {
                if (UnityEngine.Time.realtimeSinceStartup - towerPendingAt > 60) return;   // not this battle's
                global::Last.Interpreter.Instructions.SystemCall.Current.ZealotTowerConditionStart(new MainCore());
                TowerStarts++;
                Plugin.L.LogInfo("[wc] battle: the Cultists' Tower's magic-only status for a boss with its own battle script");
            }
            catch (Exception e) { WcRunner.WarnOnce("tower-status", e); }
        }

        // a battle to start when the field is free (WC's -chrm boxes): EncountBoss, as a script
        // would run it (the same call the headless battle test makes)
        static int queued;
        static float queuedAt;
        static bool direct;    // a battle the plugin starts with the seed's own party (no event remap)

        internal static void Queue(int party)
        {
            queued = party;
            queuedAt = UnityEngine.Time.realtimeSinceStartup;
        }

        // every second (WcObjectives.Poll): the queued battle once no event or battle runs
        internal static string StartQueued()
        {
            if (queued <= 0) return "";
            var t = UnityEngine.Time.realtimeSinceStartup;
            if (t - queuedAt > 60) { queued = 0; return "queued battle dropped (the field stayed busy)"; }
            var b = global::Last.Battle.BattlePlugManager.Instance();
            if ((b != null && b.IsBattle()) || WcEvents.EventBusy() || UnityEngine.Object.FindObjectOfType<global::FieldMap>() == null || t - queuedAt < 1.5f)
                return "";
            int party = queued;
            queued = 0;
            var mc = new MainCore();
            var op = new global::Last.Interpreter.Instructions.Format.Operands();
            op.iValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(8);
            op.rValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<double>(8);
            op.sValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(8);
            for (int k = 0; k < 8; k++) op.sValues[k] = "";
            op.iValues[0] = party;
            var mn = new global::Last.Interpreter.Instructions.Format.Mnemonic();
            mn.mnemonic = "EncountBoss";
            mn.operands = op;
            mc.currentInstruction = mn;
            // the box's party is the seed's own: not an event's to remap (a box whose party a
            // shuffled event battle also names fought that event's replacement, with no monsters:
            // the features test, 2026-11-06)
            direct = true;
            try { global::Last.Interpreter.Instructions.External.Misc.EncountBoss(mc); }
            finally { direct = false; }
            var what = $"monster-in-a-box: party {party}";
            Plugin.L.LogInfo("[wc] " + what);
            return what;
        }

        // WC's "Suplex A Train" (battle/suplex_train_check.py): Sabin's Suplex (the PR's Meteor
        // Strike, ability 134) on the Phantom Train (monster 263), wherever WC put it
        const int Suplex = 134, PhantomTrain = 263;

        public static void Act_Prefix(global::Last.Battle.BattleActData battleActData)
        {
            try
            {
                if (WcTest.Running) RecordAct(battleActData);
                if (!Active() || battleActData?.abilityList == null || battleActData.abilityList.Count == 0) return;
                int ability = battleActData.abilityList[0]?.Id ?? 0;
                // -lel: a lore an enemy uses (Lore abilities 140-163) is learned after the battle
                if (ability >= 140 && ability <= 163 && battleActData.AttackUnitData?.TryCast<global::BattlePlayerData>() == null)
                    WcRages.SeenLore(ability);
                if (ability != Suplex) return;
                var b = global::Last.Battle.BattlePlugManager.Instance();
                var enemies = b?.GetEnemyUnits();
                if (enemies != null && enemies.Any(e => e?.GetMonster()?.Id == PhantomTrain)) WcTrack.SetQuest("Suplex A Train");
            }
            catch (Exception e) { WcRunner.WarnOnce("suplex", e); }
        }

        // tests: every act built in a battle (attacker's character status, command, targets: e for a
        // monster, c<status> for a character)
        internal static readonly List<(int Who, int Command, string Targets)> Acts = new List<(int, int, string)>();

        static void RecordAct(global::Last.Battle.BattleActData act)
        {
            try
            {
                int who = act?.AttackUnitData?.GetOwnedCharacterData()?.characterStatusId ?? -1;
                var parts = new List<string>();
                var targets = act?.TargetUnitDatas;
                for (int i = 0; targets != null && i < targets.Length; i++)
                {
                    var t = targets[i];
                    if (t == null) continue;
                    var owned = t.TryCast<global::Last.Battle.BattleEnemyData>() != null ? null : t.GetOwnedCharacterData();
                    parts.Add(owned != null ? "c" + owned.characterStatusId : "e");
                }
                Acts.Add((who, act?.Command?.Id ?? 0, string.Join(" ", parts)));
            }
            catch (Exception) { }
        }

        // the replacement for a vanilla party, or the party itself. Several candidates: one picked
        // by the seed and the party (the same each time in a seed, as WC keeps fixed encounters)
        internal static int Map(int party)
        {
            var s = WcSeeds.Active;
            if (s == null || s.Battles.Count == 0 || !s.Battles.TryGetValue(party, out var to) || to.Length == 0) return party;
            if (to.Length == 1) return to[0];
            int h = party * 31;
            foreach (var c in s.SeedText ?? "") h = h * 33 + c;
            return to[(h & 0x7fffffff) % to.Length];
        }

        static bool Active() => Plugin.WcMode && WcSeeds.Active != null &&
                                WcEvents.Flag(DataStorage.Category.kScenarioFlag4, WcEvents.WcGameFlag) == 1;

        // the event battle about to start: the party it fights -> the party the script named (WC's
        // battle quests count the event's battle, whatever WC put there); set by the next battle
        static readonly Dictionary<int, int> pending = new Dictionary<int, int>();
        internal static int CurrentOriginal = -1;

        // a battle starts (WcScaling sees it): its party (events leave the battle's MonsterParty
        // unset: then the event's candidate whose line-up holds the monsters on screen), and the
        // script's party it came from
        internal static int BattleStarted(global::Last.Battle.BattlePlugManager battle)
        {
            int party = battle?.MonsterParty?.Id ?? -1;
            if (party < 0 && pending.Count > 0)
            {
                var ids = battle?.GetEnemyUnits()?.Select(e => e?.GetMonster()?.Id ?? 0).Where(i => i > 0).ToList() ?? new List<int>();
                foreach (var cand in pending.Keys)
                {
                    var members = Members(cand);
                    if (ids.Count > 0 && ids.GroupBy(i => i).All(g => members.Count(m => m == g.Key) >= g.Count())) { party = cand; break; }
                }
                if (party < 0 && pending.Keys.Count == 1) party = pending.Keys.First();
            }
            CurrentOriginal = party >= 0 && pending.TryGetValue(party, out var o) ? o : -1;
            pending.Clear();
            TowerStart();
            return party;
        }

        internal static List<int> Members(int party)
        {
            var list = new List<int>();
            var row = global::Last.Data.Master.MasterManager.Instance?.GetData<global::Last.Data.Master.MonsterParty>(party);
            if (row == null) return list;
            for (int k = 1; k <= 9; k++)
                if (typeof(global::Last.Data.Master.MonsterParty).GetProperty("Monster" + k)?.GetValue(row) is int m && m > 0) list.Add(m);
            return list;
        }

        internal static int BuiltParty = -1, BuiltBackground = -1, PartyBackground = -1;

        public static void Create_Postfix(int __0, int __1)
        {
            BuiltParty = __0;
            BuiltBackground = __1;
        }

        // a battle built with background 0 (a script's Encount) takes its party's: this
        public static void BackgroundId_Postfix(int __result) => PartyBackground = __result;

        static int[] Swap(MainCore mc, int count)
        {
            towerPending = false;
            if (!Active() || direct) return null;
            // a monster-in-a-box: the port gave its script the seed's battle (port_chests), which
            // the remap (by party) could only make wrong
            var script = global::Last.Interpreter.Integrator.Instance?.scriptName;
            if (script != null && WcSeeds.Active.Boxes.Contains(script)) return null;
            var iv = mc?.op?.iValues;
            if (iv == null) return null;
            pending.Clear();
            int[] saved = null;
            var log = new List<string>();
            for (int k = 0; k < count && k < iv.Length; k++)
            {
                int party = iv[k];
                if (party <= 0) continue;
                int to = Map(party);
                pending[to] = party;
                if (to == party) continue;
                if (ScriptOf(party) == TowerScript && ScriptOf(to) != TowerScript)
                {
                    towerPending = true;
                    towerPendingAt = UnityEngine.Time.realtimeSinceStartup;
                }
                if (saved == null)
                {
                    saved = new int[count];
                    for (int j = 0; j < count && j < iv.Length; j++) saved[j] = iv[j];
                }
                iv[k] = to;
                log.Add($"{party} -> {to}");
            }
            if (saved != null)
            {
                LastSwap = string.Join(", ", log);
                Plugin.L.LogInfo($"[wc] battle: party {LastSwap}");
            }
            return saved;
        }

        public static void Encount_Prefix(MainCore mc, out int[] __state)
        {
            __state = null;
            try { __state = Swap(mc, 4); } catch (Exception e) { WcRunner.WarnOnce("battle-swap", e); }
        }

        public static void Boss_Prefix(MainCore mc, out int[] __state)
        {
            __state = null;
            try { __state = Swap(mc, 1); } catch (Exception e) { WcRunner.WarnOnce("battle-swap", e); }
        }

        public static void Restore_Postfix(MainCore mc, int[] __state)
        {
            if (__state == null) return;
            try
            {
                var iv = mc.op.iValues;
                for (int k = 0; k < __state.Length && k < iv.Length; k++) iv[k] = __state[k];
            }
            catch (Exception e) { WcRunner.WarnOnce("battle-restore", e); }
        }
    }
}
