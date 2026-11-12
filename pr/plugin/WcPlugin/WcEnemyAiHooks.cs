using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.Battle;
using Last.Interpreter;
using UnityEngine;

namespace WorldsCollide.PR
{
    // WcEnemyAi, part 3: the hooks. A WC monster's PR script is a shim (tools/port_ai.py shim_script) whose
    // rows carry markers: LoopAct [MarkTurn] (its turn), CounterAct [MarkCounter] (after a hit),
    // FinalAttackAct [MarkFinal] (its death). When the PR's interpreter is about to run such a row for real
    // (its flag set, the turn's row: the checks BattleAI.LoopAct/CounterAct/FinalAttackAct make), the plugin
    // runs the monster's SNES script and rewrites the row's operands to what it does, then lets the PR's
    // own instruction queue it (its bookkeeping, act types, waits and battle lines stay the game's): the
    // marker stays first with weight 0 (LoopAct queues it 0 times; CounterAct/FinalAttackAct never pick a
    // weight-0 entry), the abilities follow with their counts (LoopAct can't hold one id twice), the
    // lines go in sValues (LoopAct files them for every ability it queues; ExecMsg shows them). An idle
    // turn is the PR's own "nothing" (696). Targets: CreateActData, first, takes each queued act's
    // targets from the plan. Variable/switch changes and timers apply at once.
    static partial class WcEnemyAi
    {
        internal const int MarkTurn = 7680, MarkCounter = 7681, MarkFinal = 7682, EraseAll = 735;
        static int battleSerial, fightSerial = -1;
        internal static int Turns, Counters, Finals;     // for the self-test and the log
        internal static string LastTurn = "", LastCounter = "", LastFinal = "";

        // PR "received command" (the AI's ForAiType) -> SNES battle command (ai_compile SNES_CMD_TO_PR)
        static readonly Dictionary<int, int> PrCmdToSnes = new Dictionary<int, int>
            { { 4, 0x00 }, { 2, 0x02 }, { 607, 0x05 }, { 608, 0x06 }, { 601, 0x07 }, { 611, 0x09 }, { 602, 0x0A }, { 504, 0x0C }, { 9, 0x19 } };
        // PR attribute -> SNES element bit (ai_compile ELEM_ATTR: bit i = attribute ElemAttr[i])
        static readonly int[] ElemAttr = { 1, 2, 3, 6, 8, 7, 4, 5 };

        static System.Reflection.MethodInfo getAse;

        public static void Patch(Harmony h)
        {
            var t = typeof(Last.Interpreter.Instructions.BattleAI);
            foreach (var (name, pre) in new[] { ("LoopAct", nameof(LoopAct_Prefix)), ("CounterAct", nameof(CounterAct_Prefix)), ("FinalAttackAct", nameof(FinalAttackAct_Prefix)) })
            {
                var m = AccessTools.Method(t, name);
                if (m == null) throw new MissingMethodException("BattleAI." + name);
                h.Patch(m, prefix: new HarmonyMethod(typeof(WcEnemyAi), pre));
            }
            getAse = AccessTools.Method(t, "GetActSelectEnemy") ?? throw new MissingMethodException("BattleAI.GetActSelectEnemy");
            var cad = AccessTools.Method(typeof(ActSelectEnemy), "CreateActData") ?? throw new MissingMethodException("ActSelectEnemy.CreateActData");
            h.Patch(cad, prefix: new HarmonyMethod(typeof(WcEnemyAi), nameof(CreateActData_Prefix)) { priority = Priority.First });
            // self-test: what the AI's ViewMes hands the message window (BattleAI.ViewMes puts the act's next
            // key into the running instruction's sValues, then runs the battle's Msg)
            var vm = AccessTools.Method(t, "ViewMes");
            if (vm != null) h.Patch(vm, postfix: new HarmonyMethod(typeof(WcEnemyAi), nameof(ViewMes_Postfix)));
            var create = AccessTools.Method(typeof(InstantiateManager), "Create");
            if (create != null) h.Patch(create, postfix: new HarmonyMethod(typeof(WcEnemyAi), nameof(Create_Postfix)));
            Plugin.L.LogInfo("[wc] enemy AI: monsters' turns, counters and deaths run their WC scripts");
        }

        static int battleParty;   // the party InstantiateManager.Create made the battle from

        public static void Create_Postfix(int __0)
        {
            battleSerial++;
            battleParty = __0;
        }

        public static void ViewMes_Postfix(BattleAICore __0, int __result)
        {
            try
            {
                var sv = __0?.op?.sValues;
                string k = sv != null && sv.Length > 0 ? sv[0] : "";
                // ViewMes runs again while the window shows the line: count it once per turn; and the
                // instruction keeps the last key it showed, so only when the staged act's lines hold it
                if (string.IsNullOrEmpty(k) || !FiledKeys.Contains(k) || (k == LastLine && shownAtTurn == Turns)) return;
                var dic = __0.msgExecuteInfos;
                int st = Staging();
                if (dic == null || !dic.ContainsKey(st) || dic[st] == null || !dic[st].Contains(k)) return;
                LastLine = k;
                shownAtTurn = Turns;
                LinesShown++;
                if (LinesSeen.Count < 200) LinesSeen.Add(k);
                if (WcTest.Running) Plugin.L.LogInfo($"[wc] enemy AI: battle line {k} shown");
            }
            catch (Exception e) { WcRunner.WarnOnce("enemy-ai-viewmes", e); }
        }

        static int Staging()
        {
            try { return BattlePlugManager.Instance()?.BattleActExection?.GetStagingAbilityId() ?? -1; } catch { return -2; }
        }

        // battle lines: the keys this plugin filed, and which of them the battle's message pass showed
        static readonly HashSet<string> FiledKeys = new HashSet<string>();
        internal static int LinesQueued, LinesShown;
        internal static readonly List<string> LinesSeen = new List<string>();
        static string LastLine = "";
        static int shownAtTurn = -1;

        static void Filed(List<string> lines)
        {
            if (lines.Count == 0) return;
            FiledKeys.UnionWith(lines);
            LinesQueued += lines.Count;
        }

        static bool Active => Plugin.WcMode && WcSeeds.Active?.SnesAi != null;

        static int Marker(BattleAICore bc)
        {
            var iv = bc?.op?.iValues;
            return iv != null && iv.Length > 0 ? iv[0] : 0;
        }

        // the planned targets of the acts a row is about to queue, by the unit that acts (in order)
        static readonly Dictionary<IntPtr, Queue<(int ability, Il2CppReferenceArray<BattleUnitData> targets)>> planned =
            new Dictionary<IntPtr, Queue<(int, Il2CppReferenceArray<BattleUnitData>)>>();

        // the row's operands: the marker (weight 0), then the plan's abilities with their counts, the lines
        static void Rewrite(BattleAICore bc, int marker, List<(int ability, Il2CppReferenceArray<BattleUnitData> targets)> q, List<string> lines, bool counts)
        {
            var ids = new List<int> { marker };
            var weights = new List<double> { 0 };
            foreach (var (ab, _) in q)
            {
                int at = ids.IndexOf(ab, 1);
                if (at > 0 && counts) weights[at] += 1;
                else if (at < 0) { ids.Add(ab); weights.Add(counts ? 1 : 100); }
            }
            var op = bc.op;
            op.iValues = new Il2CppStructArray<int>(ids.ToArray());
            op.rValues = new Il2CppStructArray<double>(weights.ToArray());
            op.sValues = new Il2CppStringArray(lines.ToArray());
            Filed(lines);
        }

        static void Plan(BattleUnitData me, List<(int ability, Il2CppReferenceArray<BattleUnitData> targets)> q)
        {
            if (me == null) return;
            var queue = new Queue<(int, Il2CppReferenceArray<BattleUnitData>)>();
            foreach (var x in q) if (x.targets != null) queue.Enqueue(x);
            planned[me.Pointer] = queue;
        }

        // first of the CreateActData prefixes: the act's targets from the plan (WcAbilityScaling swaps the
        // placeholder's spell after this)
        public static void CreateActData_Prefix(ActSelectEnemy __instance, ref int __0, ref Il2CppReferenceArray<BattleUnitData> __1)
        {
            try
            {
                var me = __instance?.BattleUnitData;
                if (me == null || !planned.TryGetValue(me.Pointer, out var queue) || queue.Count == 0) return;
                if (queue.Peek().ability != __0) return;
                __1 = queue.Dequeue().targets;
            }
            catch (Exception e) { WcRunner.WarnOnce("enemy-ai-targets", e); }
        }

        static ActSelectEnemyController Controller(BattleAICore bc) =>
            (getAse?.Invoke(null, new object[] { bc }) as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<ActSelectEnemyController>();

        // the battle's state: made fresh for each battle (InstantiateManager.Create), monsters by UniqueId
        // in slot order, each with its WC script
        static Fight EnsureFight()
        {
            if (Current != null && fightSerial == battleSerial) return Current;
            var f = new Fight();
            fightSerial = battleSerial;
            Current = f;
            var d = WcSeeds.Active?.SnesAi;
            var bm = BattlePlugManager.Instance();
            try
            {
                int party = battleParty > 0 ? battleParty : bm?.MonsterParty?.Id ?? 0;
                int src = WcSeeds.Active != null && WcSeeds.Active.LookSources.TryGetValue(party, out int ls) ? ls : party;
                f.Formation = d != null && d.Formations.TryGetValue(src, out int form) ? form : -1;
                // the cells' SNES slots: the battle's own party (its layout), else its look source's
                f.CellSlots = d != null && (d.PartySlots.TryGetValue(party, out var cs) || d.PartySlots.TryGetValue(src, out cs)) ? cs : null;
                f.Members = d != null && (d.PartyMembers.TryGetValue(party, out var pm) || d.PartyMembers.TryGetValue(src, out pm)) ? pm : null;
                if (f.Members != null)
                    for (int s = 0; s < f.Members.Length; s++)
                        if (f.Members[s] != null && f.Members[s][2] == 0) f.Hidden.Add(s);   // hidden from the start (shown by its script)
                if (d != null && f.Formation < 0 && d.Formations.TryGetValue(party, out int form2)) f.Formation = form2;
                var enemies = new List<BattleEnemyData>();
                foreach (var e in bm.GetEnemyUnits())
                {
                    var ed = e?.TryCast<BattleEnemyData>();
                    if (ed != null) enemies.Add(ed);
                }
                foreach (var ed in enemies.OrderBy(x => x.UniqueId)) Join(f, ed);
                Plugin.L.LogInfo($"[wc] enemy AI: party {party} (formation {f.Formation}): " +
                                 string.Join(", ", f.Slot.Select(kv => $"slot {kv.Value} = monster {f.SnesId[kv.Key]} (unique {kv.Key})")));
            }
            catch (Exception e) { WcRunner.WarnOnce("enemy-ai-fight", e); }
            return f;
        }

        static void Join(Fight f, BattleEnemyData ed)
        {
            if (f.Slot.ContainsKey(ed.UniqueId)) return;
            // the SNES slot of its cell (uniqueId = the party's cell 1-9), else the first free one
            int slot = f.CellSlots != null && ed.UniqueId > 0 && ed.UniqueId < f.CellSlots.Length ? f.CellSlots[ed.UniqueId] : -1;
            if (slot < 0 || f.Slot.ContainsValue(slot))
            {
                slot = -1;
                for (int s = 0; s < 6; s++) if (!f.Slot.ContainsValue(s)) { slot = s; break; }
            }
            if (slot < 0) return;
            var d = WcSeeds.Active?.SnesAi;
            int prId = ed.GetMonster()?.Id ?? 0;
            int id = d != null && d.Copies.TryGetValue(prId, out int orig) ? orig : prId - 1;
            f.Slot[ed.UniqueId] = slot;
            f.SnesId[ed.UniqueId] = id;
            if (d != null && d.Scripts.TryGetValue(id, out var script)) f.Monsters[ed.UniqueId] = SnesAiMonster.FromScript(script);
        }

        // the monster this core runs for (its unit's act-select controller is the core's)
        static BattleEnemyData Owner(ActSelectEnemyController c)
        {
            if (c == null) return null;
            var all = BattlePlugManager.Instance()?.GetEnemyUnits();
            if (all == null) return null;
            foreach (var e in all)
            {
                var ctrl = e?.BattleUnitDataInfo?.ActSelectController;
                if (ctrl != null && ctrl.Pointer == c.Pointer) return e;
            }
            return null;
        }

        static int UnitIndex(BattleUnitData x, BattleUnitData[] units)
        {
            if (x == null) return -1;
            for (int i = 0; i < 10; i++) if (units[i] != null && units[i].Pointer == x.Pointer) return i;
            return -1;
        }

        // battle lines: the PR's own lines (its keys: the game's text in the player's language), else the
        // seed's text under keys of the plugin's own; shown with the act
        static string[] LineKeys(int message)
        {
            var d = WcSeeds.Active?.SnesAi;
            if (d == null) return Array.Empty<string>();
            if (d.MessageKeys.TryGetValue(message, out var pr) && pr.Length > 0) return pr;
            if (!d.Messages.TryGetValue(message, out var lines) || lines.Length == 0) return Array.Empty<string>();
            var keys = new string[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                keys[i] = $"WC_BTL_{message:X2}_{i}";
                WcEvents.SetText(keys[i], lines[i]);
            }
            return keys;
        }

        // the interpreter's actions -> (ability, targets) acts and battle lines; everything else applied now
        static List<(int ability, Il2CppReferenceArray<BattleUnitData> targets)> Plan(Fight f, SnesAiMonster m, int snesId, int slot,
            List<SnesAiAction> acts, BattleUnitData[] units, List<string> lines, List<SnesAiAction> entries)
        {
            var q = new List<(int, Il2CppReferenceArray<BattleUnitData>)>();
            foreach (var a in acts)
            {
                switch (a.Op)
                {
                    case SnesAiOp.Attack:
                        {
                            int ab = AbilityFor(snesId, a.Attack);
                            if (ab > 0) q.Add((ab, TargetsOf(a.Targets, units)));
                            break;
                        }
                    case SnesAiOp.Item:      // F6 00: uses an item (the item's own effect, as Square ported Harvester's Hi-Potion)
                    case SnesAiOp.Throw:     // F6 01: throws a weapon (a hit with its power: Square's 740-756)
                    case SnesAiOp.Command:   // F4: Steal 693, Jump 1391, GP Rain 694 (Capture: Steal)
                        {
                            var d = WcSeeds.Active?.SnesAi;
                            var map = a.Op == SnesAiOp.Item ? d?.ItemUse : a.Op == SnesAiOp.Throw ? d?.Throws : d?.Commands;
                            int key = a.Op == SnesAiOp.Command ? a.Command : a.Attack;
                            if (map != null && map.TryGetValue(key, out int ab) && ab > 0) q.Add((ab, TargetsOf(a.Targets, units)));
                            else if (Seen.Add($"{a.Op}:{key}:{snesId}")) Plugin.L.LogInfo($"[wc] enemy AI: monster {snesId}: {a} has no PR ability");
                            break;
                        }
                    case SnesAiOp.ChangeVar:
                    case SnesAiOp.ChangeSwitch:
                        ApplyVars(f, m, a);
                        break;
                    case SnesAiOp.Misc:
                        ApplyMisc(f, slot, a);
                        break;
                    case SnesAiOp.Message:
                        lines.AddRange(LineKeys(a.B1));
                        break;
                    case SnesAiOp.EntryExit:
                        entries.Add(a);
                        break;
                    default:
                        if (Seen.Add($"{a.Op}:{snesId}")) Plugin.L.LogInfo($"[wc] enemy AI: monster {snesId}: {a} not done yet");
                        break;
                }
            }
            return q;
        }

        static string Show(List<(int ability, Il2CppReferenceArray<BattleUnitData> targets)> q) =>
            string.Join(", ", q.Select(x => $"{x.ability}" + (x.targets == null ? "" : $"->{x.targets.Length}")));

        // ---- a turn ---------------------------------------------------------------------------------------
        public static bool LoopAct_Prefix(BattleAICore __0, ref int __result)
        {
            if (!Active || Marker(__0) != MarkTurn) return true;
            try
            {
                var c = Controller(__0);
                // only the pass where the row really acts (BattleAI.LoopAct: the normal flag, its turn)
                if (c != null && c.IsReadyActNormal() && c.GetActTurn() == __0.turnCounter && Turn(__0, c))
                {
                    __0.turnCounter = __0.turnCounter + 1;   // the next row, as Act/LoopAct move on
                    __result = 0;
                    return false;
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("enemy-ai-turn", e); }
            return true;   // the PR's LoopAct queues what the row now holds
        }

        // the lines an act shows, filed under its ability as the PR's Act does with a row's keys (always, so
        // a line filed for an earlier act of the same ability never shows again: ViewMes reads the act's)
        static void FileLines(BattleAICore bc, int ability, List<string> lines)
        {
            var dic = bc.msgExecuteInfos;
            if (dic == null) return;
            var arr = new Il2CppStringArray(lines.ToArray());
            if (dic.ContainsKey(ability)) dic[ability] = arr; else dic.Add(ability, arr);
            Filed(lines);
        }

        // true: the turn's one act is queued here the way the PR's Act queues it (a normal act, its lines
        // shown: the battle's message pass only shows lines for those); false: several acts, the row now
        // holds them for the PR's LoopAct
        static bool Turn(BattleAICore bc, ActSelectEnemyController c)
        {
            var f = EnsureFight();
            var me = Owner(c);
            var q = new List<(int, Il2CppReferenceArray<BattleUnitData>)>();
            var lines = new List<string>();
            var acts = new List<SnesAiAction>();
            int snesId = -1, slot = 0;
            if (me != null)
            {
                Join(f, me);
                var units = Units();
                int uid = me.UniqueId;
                slot = f.Slot.TryGetValue(uid, out int s) ? s : 0;
                snesId = f.SnesId.TryGetValue(uid, out int sid) ? sid : -1;
                if (f.Monsters.TryGetValue(uid, out var m) && !f.Hidden.Contains(slot))
                {
                    acts = SnesAi.RunMain(m, 4 + slot, new View(f, units));
                    var entries = new List<SnesAiAction>();
                    q = Plan(f, m, snesId, slot, acts, units, lines, entries);
                    if (EraseAllIn(entries)) q.Insert(0, (EraseAll, null));
                    else
                    {
                        ApplyEntries(f, slot, snesId, entries);
                        if (entries.Count > 0 && AllGone(f)) q.Insert(0, (EraseAll, null));
                    }
                }
            }
            if (q.Count == 0) q.Add((Nothing, null));   // an idle turn: the PR's own "nothing"
            Turns++;
            LastTurn = $"monster {snesId} slot {slot}: {string.Join("; ", acts)} => {Show(q)}";
            if (WcTest.Running) Plugin.L.LogInfo("[wc] enemy AI turn: " + LastTurn);
            if (q.Count == 1)
            {
                c.SetActAbilityNormal(q[0].Item1, q[0].Item2, null);
                FileLines(bc, q[0].Item1, lines);
                return true;
            }
            Rewrite(bc, MarkTurn, q, lines, true);
            Plan(me, q);
            return false;
        }

        // ---- a counter --------------------------------------------------------------------------------------
        public static bool CounterAct_Prefix(BattleAICore __0, ref int __result)
        {
            if (!Active || Marker(__0) != MarkCounter) return true;
            try
            {
                var c = Controller(__0);
                if (c == null || !c.IsReadyActCounter()) return true;
                if (Counter(__0, c)) return true;   // the PR's CounterAct queues it (at the attacker unless self-targeted)
                __result = 0;
                return false;                        // no counterattack (its variables changed all the same)
            }
            catch (Exception e) { WcRunner.WarnOnce("enemy-ai-counter", e); return true; }
        }

        static bool Unresponsive(BattleUnitData me) =>
            HasCondition(me, 7) || HasCondition(me, 12) || HasCondition(me, 410) || HasCondition(me, 603) || HasCondition(me, 17) || HasCondition(me, 630);

        static bool Counter(BattleAICore bc, ActSelectEnemyController c)
        {
            var f = EnsureFight();
            var me = Owner(c);
            if (me == null || IsDead(me)) return false;
            Join(f, me);
            var units = Units();
            int uid = me.UniqueId, slot = f.Slot.TryGetValue(uid, out int s) ? s : 0, snesId = f.SnesId.TryGetValue(uid, out int sid) ? sid : -1;
            if (!f.Monsters.TryGetValue(uid, out var m)) return false;
            if (f.Hidden.Contains(slot)) return false;   // out of the battle (an attack already on its way hit it)
            // what hit it (the retaliation data its counter script reads)
            int who = UnitIndex(c.GetAttackUnitData() ?? c.GetRecevieUser(), units);
            f.Retal[slot] = who;
            f.PrevAttacker[slot] = who;
            f.RetalCmdUnit[slot] = who;
            f.RetalCmd[slot] = PrCmdToSnes.TryGetValue(c.GetRecevieForAiTypeId(), out int cmd) ? cmd : 0xFF;
            f.RetalAtk[slot] = SnesAttackOf(c.GetRecevieAbilityId());
            f.RetalItm[slot] = 0xFF;
            int elem = 0;
            var attrs = c.GetReceiveAttributeIds();
            if (attrs != null)
                foreach (var a in attrs)
                {
                    int bit = Array.IndexOf(ElemAttr, a);
                    if (bit >= 0) elem |= 1 << bit;
                }
            f.RetalElemUnit[slot] = elem != 0 ? who : -1;
            f.RetalElem[slot] = elem;
            var acts = SnesAi.RunCounter(m, 4 + slot, new View(f, units), Unresponsive(me));
            var lines = new List<string>();
            var entries = new List<SnesAiAction>();
            var q = Plan(f, m, snesId, slot, acts, units, lines, entries);
            if (EraseAllIn(entries)) q.Insert(0, (EraseAll, null));
            else
            {
                ApplyEntries(f, slot, snesId, entries);
                if (entries.Count > 0 && AllGone(f)) q.Insert(0, (EraseAll, null));
            }
            Counters++;
            LastCounter = $"monster {snesId} slot {slot} hit by unit {who} (cmd {f.RetalCmd[slot]:x2}, attack {f.RetalAtk[slot]:x2}, elements {elem:x2}): {string.Join("; ", acts)} => {Show(q)}";
            if (WcTest.Running) Plugin.L.LogInfo("[wc] enemy AI counter: " + LastCounter);
            if (q.Count == 0) return false;
            var first = new List<(int, Il2CppReferenceArray<BattleUnitData>)> { q[0] };
            Rewrite(bc, MarkCounter, first, lines, false);
            Plan(me, first);
            return true;
        }

        // PR ability -> SNES attack index (the seed's map; Battle/Fight -> EE)
        static int SnesAttackOf(int ability)
        {
            var d = WcSeeds.Active?.SnesAi;
            if (d == null || ability <= 0) return 0xFF;
            if (ability == 1) return 0xEE;
            int i = Array.IndexOf(d.Attacks, ability);
            return i >= 0 ? i : 0xFF;
        }

        // ---- a death --------------------------------------------------------------------------------------
        public static bool FinalAttackAct_Prefix(BattleAICore __0, ref int __result)
        {
            if (!Active || Marker(__0) != MarkFinal) return true;
            try
            {
                var c = Controller(__0);
                if (c == null || !c.IsReadyActFinalAttack()) return true;
                if (Final(__0, c)) return true;     // the PR's FinalAttackAct queues it
                __result = 0;
                return false;                       // just dies
            }
            catch (Exception e) { WcRunner.WarnOnce("enemy-ai-final", e); return true; }
        }

        static bool Final(BattleAICore bc, ActSelectEnemyController c)
        {
            var f = EnsureFight();
            var me = Owner(c);
            if (me == null) return false;
            Join(f, me);
            var units = Units();
            int uid = me.UniqueId, slot = f.Slot.TryGetValue(uid, out int s) ? s : 0, snesId = f.SnesId.TryGetValue(uid, out int sid) ? sid : -1;
            if (!f.Monsters.TryGetValue(uid, out var m)) return false;
            var acts = SnesAi.RunCounter(m, 4 + slot, new View(f, units), true);
            var lines = new List<string>();
            var entries = new List<SnesAiAction>();
            var q = Plan(f, m, snesId, slot, acts, units, lines, entries);
            // "everyone leaves/dies like a boss" (F5 xx 01/05 FF): the PR's erase-all, which ends the fight
            if (EraseAllIn(entries)) q.Insert(0, (EraseAll, null));
            else if (entries.Count > 0)
            {
                f.Deferred.Add((me, slot, snesId, entries, f.BattleTime + 2));   // after its death animation
                // a death that brings others in (Chaser's Trappers, a Flan's halves): the SNES keeps the
                // battle from ending meanwhile (its own F5 mode 4, or the shown monsters counting at once)
                if (entries.Any(e => e.B2 == 0 || e.B2 == 2 || e.B2 == 4)) Hold(f, BattlePlugManager.Instance(), 15);
            }
            Finals++;
            LastFinal = $"monster {snesId} slot {slot}: {string.Join("; ", acts)} => {Show(q)}";
            if (WcTest.Running) Plugin.L.LogInfo("[wc] enemy AI death: " + LastFinal);
            if (q.Count == 0) return false;
            var first = new List<(int, Il2CppReferenceArray<BattleUnitData>)> { q[0] };
            Rewrite(bc, MarkFinal, first, lines, false);
            Plan(me, first);
            return true;
        }
    }
}
