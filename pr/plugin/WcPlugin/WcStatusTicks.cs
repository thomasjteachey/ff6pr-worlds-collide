using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Battle;
using UnityEngine;

namespace WorldsCollide.PR
{
    // WC's (the SNES's) status clock in place of the PR's timers (the user, 2026-11-12: "is hot/dot
    // numbers ticking at correct times (i.e. regen, poison, etc) for WC?", then "fix it and also ensure
    // it works the same with, say, jump/stop/etc", then the durations of Stop/Sleep/Freeze/Reflect/Doom
    // and Jump's air time). SnesStatusClock is the SNES's rule; this is the game's side.
    // The PR's (decompiled, GameAssembly.dll): Regen and Sap every 6 s whatever the Haste or Slow
    // (BattleUtility.CalcATBProgressCycleForVital type 2 = 6 / the battle speed), Poison 16 s / Haste 8 /
    // Slow 32 (type 1), the first tick at half, the battle speed setting (x0.5-x2) on all, a jumping
    // unit's Poison and Sap held (JumpConditionFunction.SetDamageStop). Timed statuses: a clock per
    // status gains BattleUtility.GetConditionDurationTime a frame and the status goes past condition.csv's
    // until_value (Stop and Reflect 30/60 by time, Sleep 30 or a hit, Freeze 60 or fire;
    // BattleConditionFunction.UpdateCondtitonRecovery); measured: Stop 31.3 s. Doom: its own countdown
    // from until_value (40), one every 2 s (Haste 1.33, Slow 4; DoomConditionFunction.GetCycleValue).
    // How, per unit in the SNES's 10 slots (4 party, 6 monsters; others keep the game's own timing):
    //  * the clock runs in BattleConditionController.Update (once a frame while the battle's time moves);
    //  * Regen/Sap/Poison: each tick function's own counter (Regen progressTime, Sap and Poison
    //    progressCount) is 0 before its update, or past any interval when the clock has a tick for it,
    //    so the game's own tick (amount, number, its checks) runs at the SNES's moment; Jump's hold off;
    //  * Stop, Sleep, Reflect, Freeze: GetConditionDurationTime is 0 for them (their time-outs never come;
    //    a hit still wakes, fire still thaws) and the clock takes them off when its timer runs out
    //    (BattleConditionController.InterruptRemoveCondition);
    //  * Condemned (Doom 404): the attacker's level is caught as it tries the status
    //    (AddConditionFunctionSerial.CheckForceMissCondition); once it lands, the game's countdown is set
    //    to the SNES's and moved one at each of the clock's steps (DoomConditionFunction's own
    //    progressTimer, so its number and its death are the game's). Doom with no caught level (at the
    //    start, from equipment) keeps the game's countdown.
    // Jump's air time: WcAtb (1.75 gauges while the Jump status is on).
    // Time runs while an action plays in both games (WcAtb's note), so the clock does too; it waits
    // only when the PR's battle does (Wait mode's menus, forced pauses). Left as the PR has it: the amounts.
    static class WcStatusTicks
    {
        const float Fire = 1e9f;
        internal const int SleepType = 7, StopType = 17, ReflectType = 413, FreezeType = 603, DoomType = 404, JumpType = 420;
        static readonly Dictionary<IntPtr, SnesStatusClock> clocks = new Dictionary<IntPtr, SnesStatusClock>();
        static readonly SnesStatusRound round = new SnesStatusRound();
        static readonly System.Random rng = new System.Random();
        static readonly BattleUnitData[] slots = new BattleUnitData[SnesStatusClock.Fighters];
        static readonly Action<int> visit = Visit;
        static int lastFrame = -1;
        internal static int Ticks, Steps, Removed;
        // the headless check's record (WcTest status_ticks / status_timers): battle time, slot, what
        internal static readonly List<(float Time, int Slot, string Kind)> Log = new List<(float, int, string)>();
        internal static float BattleTime;
        static readonly HashSet<string> noted = new HashSet<string>();

        internal static bool Active => Plugin.WcMode && WcSeeds.Active != null;

        public static void Patch(Harmony h)
        {
            Hook(h, typeof(Last.Battle.BattleConditionController), "Update", nameof(Controller_Prefix), null);
            Hook(h, typeof(RegenerationConditionFunction), "Update", nameof(Regen_Prefix), nameof(Regen_Postfix));
            Hook(h, typeof(SlipConditionFunction), "Update", nameof(Sap_Prefix), nameof(Sap_Postfix));
            Hook(h, typeof(PoisonConditionFunction), "UpdateSub", nameof(Poison_Prefix), nameof(Poison_Postfix));
            Hook(h, typeof(JumpConditionFunction), "SetDamageStop", nameof(JumpHold_Prefix), null);
            Hook(h, typeof(BattleUtility), "GetConditionDurationTime", null, nameof(Duration_Postfix));
            Hook(h, typeof(DoomConditionFunction), "Update", nameof(Doom_Prefix), null);
            Hook(h, typeof(Serial.FF6.Battle.AddConditionFunctionSerial), "CheckForceMissCondition", null, nameof(TryStatus_Postfix));
            Plugin.L.LogInfo("[wc] status ticks: the SNES's status clock hooked (Regen/Sap/Poison, Stop/Sleep/Reflect/Freeze, Condemned)");
        }

        static void Hook(Harmony h, Type t, string method, string prefix, string postfix)
        {
            var m = AccessTools.Method(t, method);
            if (m == null) throw new MissingMethodException(t.Name + "." + method);
            h.Patch(m, prefix: prefix == null ? null : new HarmonyMethod(typeof(WcStatusTicks), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(WcStatusTicks), postfix));
        }

        // every frame (WcRunner): a battle's clocks go with it
        // test only (WcTest turn_cycle): called every frame with the frame number
        internal static Action<int> FrameHook;

        internal static void Track()
        {
            if (FrameHook != null && WcTest.Running)
                try { FrameHook(Time.frameCount); } catch (Exception e) { WcRunner.WarnOnce("ticks.framehook", e); }
            if (clocks.Count == 0) return;
            var battle = BattlePlugManager.Instance();
            if (battle == null || !battle.IsBattle()) Reset();
        }

        static void Reset()
        {
            clocks.Clear();
            round.Reset();
            Array.Clear(slots, 0, slots.Length);
            inAir.Clear();
            BattleTime = 0;
        }

        public static void Controller_Prefix()
        {
            long t0 = 0;
            try
            {
                if (!Active) return;
                int f = Time.frameCount;
                if (f == lastFrame) return;
                lastFrame = f;
                var battle = BattlePlugManager.Instance();
                if (battle == null || !battle.IsBattle()) return;
                t0 = WcPerf.Now();
                Array.Clear(slots, 0, slots.Length);
                var party = battle.GetPlayerUnits();
                var foes = battle.GetEnemyUnits();
                if (party != null)
                    for (int i = 0; i < party.Length && i < SnesStatusClock.PartySlots; i++) slots[i] = party[i];
                if (foes != null)
                    for (int i = 0; i < foes.Length && i < SnesStatusClock.Fighters - SnesStatusClock.PartySlots; i++)
                        slots[SnesStatusClock.PartySlots + i] = foes[i];
                // every fighter's step index at random from the battle's start (C2/2457), monsters
                // that join later on their first visit
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i] != null && !clocks.ContainsKey(slots[i].Pointer))
                        clocks[slots[i].Pointer] = new SnesStatusClock(rng.Next(256));
                float dt = Mathf.Min(Time.deltaTime, 0.25f);
                BattleTime += dt;
                round.Advance(dt, visit);
                if (WcTest.Running) WatchJumps();
            }
            catch (Exception e) { WcRunner.WarnOnce("ticks", e); }
            finally { if (t0 != 0) WcPerf.Lap(12, "status-ticks", t0); }
        }

        internal static SnesStatus Read(BattleUnitData u)
        {
            var s = SnesStatus.None;
            var list = u?.BattleUnitDataInfo?.Parameter?.CurrentConditionList;
            if (list == null) return s;
            foreach (var c in list)
            {
                if (c == null) continue;
                switch (c.ConditionType)
                {
                    case 10: s |= SnesStatus.Poison; break;
                    case 15: s |= SnesStatus.Haste; break;           // Heist
                    case 16: s |= SnesStatus.Slow; break;
                    case StopType: s |= SnesStatus.Stop; break;
                    case 25: s |= SnesStatus.Regen; break;           // Regeneration
                    case 407: s |= SnesStatus.Sap; break;            // Slip (WC's Seizure)
                    case SleepType: s |= SnesStatus.Sleep; break;
                    case ReflectType: s |= SnesStatus.Reflect; break; // Reflex
                    case FreezeType: s |= SnesStatus.Freeze; break;
                    case DoomType: s |= SnesStatus.Doom; break;
                }
            }
            return s;
        }

        static void Visit(int slot)
        {
            var u = slots[slot];
            var p = u?.BattleUnitDataInfo?.Parameter;
            if (p == null || !clocks.TryGetValue(u.Pointer, out var c)) return;
            if (Last.Systems.ConditionUtility.IsOutBattle(p)) return;
            bool due = c.Waiting;
            int doomBefore = c.DoomLeft;
            var s = Read(u);
            if (c.Visit(s, rng)) Steps++;
            if (!due && c.Waiting) Note(slot, c.DuePoison ? "poison-due" : c.DueSap ? "sap-due" : "regen-due");
            if (doomBefore < 0 && c.DoomLeft >= 0) Note(slot, $"doom-set {c.DoomLeft}");
            if (c.Expired == SnesStatus.None) return;
            var expired = c.Expired;
            c.Expired = SnesStatus.None;
            var cc = BattlePlugManager.Instance()?.BattleConditionController;
            if (cc == null) return;
            foreach (var (flag, type, name) in new[] { (SnesStatus.Stop, StopType, "Stop"), (SnesStatus.Sleep, SleepType, "Sleep"),
                                                       (SnesStatus.Reflect, ReflectType, "Reflect"), (SnesStatus.Freeze, FreezeType, "Freeze") })
            {
                if ((expired & flag) == 0 || (s & flag) == 0) continue;
                cc.InterruptRemoveCondition(u, (Last.Defaine.ConditionType)type);
                Removed++;
                Note(slot, name + "-off");
                if (noted.Add("off-" + name))
                    Plugin.L.LogInfo($"[wc] status ticks: {name} off at the SNES's time (slot {slot}, battle time {BattleTime:0.00} s)");
            }
        }

        // test only (WcTest status_timers): each frame, who has the Jump status (its air time to the frame)
        static readonly HashSet<IntPtr> inAir = new HashSet<IntPtr>();
        static void WatchJumps()
        {
            for (int i = 0; i < slots.Length; i++)
            {
                var u = slots[i];
                if (u == null) continue;
                bool air = false;
                var list = u.BattleUnitDataInfo?.Parameter?.CurrentConditionList;
                if (list != null)
                    foreach (var c in list)
                        if (c != null && c.ConditionType == JumpType) { air = true; break; }
                if (air && inAir.Add(u.Pointer)) Note(i, "jump-up");
                else if (!air && inAir.Remove(u.Pointer)) Note(i, "jump-down");
            }
        }

        internal static SnesStatusClock Find(BattleUnitData u) =>
            u != null && clocks.TryGetValue(u.Pointer, out var c) ? c : null;

        static int SlotOf(BattleUnitData u)
        {
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] != null && u != null && slots[i].Pointer == u.Pointer) return i;
            return -1;
        }

        static void Note(int slot, string kind)
        {
            if (WcTest.Running) Log.Add((BattleTime, slot, kind));
        }

        static void Landed(BattleUnitData u, string kind)
        {
            Ticks++;
            int slot = SlotOf(u);
            Note(slot, kind);
            if (noted.Add(kind))
                Plugin.L.LogInfo($"[wc] status ticks: first {kind} tick at the SNES's time (slot {slot}, battle time {BattleTime:0.00} s)");
        }

        public static void Regen_Prefix(RegenerationConditionFunction __instance)
        {
            try
            {
                if (!Active || __instance == null) return;
                var c = Find(__instance.BattleUnitData);
                if (c != null) __instance.progressTime = c.DueRegen ? Fire : 0f;
            }
            catch (Exception e) { WcRunner.WarnOnce("ticks.regen", e); }
        }

        public static void Regen_Postfix(RegenerationConditionFunction __instance)
        {
            try
            {
                if (!Active || __instance == null) return;
                var c = Find(__instance.BattleUnitData);
                if (c != null && c.DueRegen && __instance.progressTime < Fire / 2)
                {
                    c.DueRegen = false;
                    Landed(__instance.BattleUnitData, "Regen");
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("ticks.regen", e); }
        }

        public static void Sap_Prefix(SlipConditionFunction __instance)
        {
            try
            {
                if (!Active || __instance == null) return;
                var c = Find(__instance.BattleUnitData);
                if (c != null) __instance.progressCount = c.DueSap ? Fire : 0f;
            }
            catch (Exception e) { WcRunner.WarnOnce("ticks.sap", e); }
        }

        public static void Sap_Postfix(SlipConditionFunction __instance)
        {
            try
            {
                if (!Active || __instance == null) return;
                var c = Find(__instance.BattleUnitData);
                if (c != null && c.DueSap && __instance.progressCount < Fire / 2)
                {
                    c.DueSap = false;
                    Landed(__instance.BattleUnitData, "Sap");
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("ticks.sap", e); }
        }

        public static void Poison_Prefix(PoisonConditionFunction __instance)
        {
            try
            {
                if (!Active || __instance == null) return;
                var c = Find(__instance.BattleUnitData);
                if (c != null) __instance.progressCount = c.DuePoison ? Fire : 0f;
            }
            catch (Exception e) { WcRunner.WarnOnce("ticks.poison", e); }
        }

        public static void Poison_Postfix(PoisonConditionFunction __instance)
        {
            try
            {
                if (!Active || __instance == null) return;
                var c = Find(__instance.BattleUnitData);
                if (c != null && c.DuePoison && __instance.progressCount < Fire / 2)
                {
                    c.DuePoison = false;
                    Landed(__instance.BattleUnitData, "Poison");
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("ticks.poison", e); }
        }

        // the SNES's ticks land on a jumping character: no hold on Poison and Sap while in the air
        public static bool JumpHold_Prefix() => !Active;

        // Stop, Sleep, Reflect and Freeze run on the clock's timers: the game's own time-outs never come
        public static void Duration_Postfix(BattleUnitData battleUnitData, Last.Data.Master.Condition currentCondition, ref float __result)
        {
            try
            {
                if (!Active || currentCondition == null || __result == 0f) return;
                int t = currentCondition.ConditionType;
                if ((t == StopType || t == SleepType || t == ReflectType || t == FreezeType) && Find(battleUnitData) != null)
                    __result = 0f;
            }
            catch (Exception e) { WcRunner.WarnOnce("ticks.duration", e); }
        }

        // the attacker's level as it tries Condemned on a target (C2/09B4 reads it when the status lands)
        public static void TryStatus_Postfix(Last.Data.CharacterParameterBase attackerParameter, Last.Data.CharacterParameterBase targetParameter,
                                             Last.Data.Master.Condition condition, bool __result)
        {
            try
            {
                if (!Active || __result || condition == null || condition.ConditionType != DoomType || attackerParameter == null || targetParameter == null) return;
                for (int i = 0; i < slots.Length; i++)
                {
                    var p = slots[i]?.BattleUnitDataInfo?.Parameter;
                    if (p == null || p.Pointer != targetParameter.Pointer) continue;
                    var c = Find(slots[i]);
                    if (c != null) c.DoomLevel = attackerParameter.ConfirmedLevel();
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("ticks.doomtry", e); }
        }

        // test only: as if an attacker of this level had tried Condemned
        internal static void SetDoomCaster(BattleUnitData target, int level)
        {
            var c = Find(target);
            if (c != null) c.DoomLevel = level;
        }

        // the game's Condemned countdown moved by the clock: set to the SNES's count once, then one
        // down whenever it's above the clock's (the game's own step: its number and, at 0, its death)
        public static void Doom_Prefix(DoomConditionFunction __instance)
        {
            try
            {
                if (!Active || __instance == null) return;
                var c = Find(__instance.BattleUnitData);
                if (c == null || c.DoomLeft < 0 || __instance.isFirstUpdate) return;
                if (!c.DoomSynced)
                {
                    __instance.remainingDoomCount = c.DoomLeft + 1;
                    c.DoomSynced = true;
                }
                __instance.progressTimer = __instance.remainingDoomCount > c.DoomLeft ? Fire : 0f;
            }
            catch (Exception e) { WcRunner.WarnOnce("ticks.doom", e); }
        }
    }
}
