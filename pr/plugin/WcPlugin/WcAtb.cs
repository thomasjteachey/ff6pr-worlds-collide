using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Battle;
using Last.Management;
using UnityEngine;

namespace WorldsCollide.PR
{
    // WC's (the SNES's) ATB fill in place of the PR's (the user, 2026-11-11: "is ATB/etc on parity
    // with WC too?"). WC doesn't change it, so WC's is vanilla FF6's.
    // SNES (C2/09D2 CalcSpeed, C2/111B UpdateBattleTime): every 2 frames (NTSC 60.0988 Hz) a gauge of
    // 65536 gains (Speed + 20) x k / 16, k = 96, Haste 126, Slow 48 (Slow wins); a monster's
    // Speed + 20 first becomes (Speed + 20) x (255 - 24 x battle speed) / 256 (high byte), battle
    // speed 0-5 = the menu's Bat.Speed 1-6 (default 3); characters aren't affected by it.
    // PR (measured headless 2026-11-11, WcTest "atb"; gauge 0-100 per second): party
    // 0.260 x (Speed + 13), monsters 0.216 x (Speed + 21) at its middle battle speed, Haste x1.5, and
    // its battle speed (BattleUtility.GetBattleSpeedByConfigValue: 0-4 = x0.5, 0.75, 1, 1.5, 2) scales
    // both sides. The SNES at Bat.Speed 3: party 0.275 x (Speed + 20), monsters x0.81 of that: the
    // party's turns came 11-37% slower than WC's (more at low Speed) while the monsters' matched.
    // BattleProgressATBFF6.ATBCalc (a unit's gauge increase this frame) answers the SNES's rate x
    // Time.deltaTime wherever the game gives the unit any increase (a stopped or fallen unit's 0 stays
    // 0). The PR's five battle speeds pick the SNES's monster speeds Bat.Speed 6, 5, 3, 2, 1 (slower to
    // faster, as the PR's setting reads; its middle one is the SNES's default).
    // Time runs while an action plays, in both: the SNES's battle-time counter ($3A3E) went on through
    // a Fight's attack (37 moves over its 86 frames: 1.43 s real, harness 2026-11-12), and the PR's FF6 update
    // (BattleProgressATBFF6.Update) stops only for BattleUIManager.IsWaiting / IsForceWaiting (Wait
    // mode's menus, forced pauses): measured headless (WcTest turn_cycle), a turn had no stopped time,
    // its attack 1.40 s real (the SNES's 1.43). An earlier note here said the PR stops; it doesn't.
    // (Auto-battle runs the PR's whole battle at 1.5x: BattleController.UpdateBattle, IsAuto.)
    // Left as the PR has it: the gauges' start (the PR: 50-80% at random; the SNES: Speed +
    // random(Speed) + a random 0-72 + 16 per empty slot, of 256).
    static class WcAtb
    {
        const double TicksPerSecond = 60.0988 / 2;
        static readonly int[] SnesBattleSpeedOf = { 5, 4, 2, 1, 0 };   // PR battle speed 0-4 -> SNES 0-5 (Bat.Speed 1-6)
        internal static int Calls;

        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(BattleProgressATBFF6), "ATBCalc");
            if (m == null) throw new MissingMethodException("BattleProgressATBFF6.ATBCalc");
            h.Patch(m, postfix: new HarmonyMethod(typeof(WcAtb), nameof(ATBCalc_Postfix)));
            var chant = AccessTools.Method(typeof(BattleProgressATBFF6), "GetChantingTime");
            if (chant == null) throw new MissingMethodException("BattleProgressATBFF6.GetChantingTime");
            h.Patch(chant, postfix: new HarmonyMethod(typeof(WcAtb), nameof(Chanting_Postfix)));
            Plugin.L.LogInfo("[wc] ATB: the SNES's fill rates and Jump's air time hooked");
        }

        // the SNES's gauge increase per second, in the PR's gauge (0-100)
        internal static double SnesRate(int speed, bool monster, int battleSpeed, bool haste, bool slow)
        {
            int k = slow ? 48 : haste ? 126 : 96;
            int s = (Math.Max(0, speed) + 20) & 0xFF;              // ADC #$14, 8 bits
            if (monster) s = s * (255 - 24 * Math.Clamp(battleSpeed, 0, 5)) >> 8;
            int perTick = s * k >> 4;
            return TicksPerSecond * perTick / 65536.0 * 100.0;
        }

        // Jump's air time (the user, 2026-11-12, with the status timers): the SNES's jumper lands when its
        // wait timer ($3AB4, gaining $3AC8 / 2 a move, C2/1193) reaches 224 x 256 (C2/0B7A: $322C = $E0):
        // 1.75 gauges; measured in the harness (Speed 33, $3AC8 318): 729-743 frames up, 722 by the rule.
        // The PR's lands when its landing act (ability 22, type 16 "ContinuousJumpEnd", ability_wait 30)
        // has chanted: BattleProgressATBFF6.GetChantingTime = 20 x wait / ((Agility / 2 + 20) x (Haste 2,
        // Slow 0.5) x the battle speed), counted down by frame time x the battle speed (UpdateChantTime;
        // decompiled 2026-11-12): 600 / (Agility / 2 + 20) s at the default speed, 14.46 s at Speed 43
        // (measured headless 16.05 s; SNES 10.10). Now the landing's chant is the SNES's air time.
        internal const int JumpWait = 224 * 256;

        internal static double SnesJumpSeconds(int speed, bool monster, int battleSpeed, bool haste, bool slow) =>
            SnesWaitSeconds(JumpWait >> 8, speed, monster, battleSpeed, haste, slow);

        // the SNES's wait timer: `wait` x 256, gaining half the ATB's add a move (C2/1193)
        internal static double SnesWaitSeconds(int wait, int speed, bool monster, int battleSpeed, bool haste, bool slow)
        {
            if (wait <= 0) return 0;
            int k = slow ? 48 : haste ? 126 : 96;
            int s = (Math.Max(0, speed) + 20) & 0xFF;
            if (monster) s = s * (255 - 24 * Math.Clamp(battleSpeed, 0, 5)) >> 8;
            int perTick = Math.Max(1, (s * k >> 4) >> 1);           // LSR: half the ATB's add
            int moves = (wait * 256 + perTick - 1) / perTick;
            return moves / TicksPerSecond;
        }

        // The wait between a character's command and its act (the user, 2026-11-12: "investigate and if
        // needed fix this"). The SNES's: the command's entry in the C2/067B table ($322C), then the wait
        // timer above; measured in the harness (Speed 33): Fight and Steal $10, ready 51-57 frames after
        // the input (52 by the rule), Magic $20 (136 frames); Jump goes up at once (its $E0 is the
        // landing); monsters act at once (their $322C: $FE, no wait), and so do Berserk, Muddle and Rage
        // turns. The PR's (BattleProgressATBFF6.GetChantingTime, decompiled 2026-11-12): for a
        // character's act, 20 x the ability's ability_wait / ((Agility / 2 + 20) x (Haste 2, Slow 0.5)
        // x the battle speed): Attack (wait 3) 1.64 s at Agility 33 (SNES 0.87), spells (5) 2.74 s
        // (SNES 1.73), items (0) none (SNES 0.87); monsters none. PR command -> the SNES table's value
        // (by the SNES command tools/port.py SNES_CMD maps to it):
        internal static readonly Dictionary<int, int> SnesCommandWait = new Dictionary<int, int>
        {
            [1] = 16, [2] = 16, [3] = 16, [5] = 16, [7] = 32, [8] = 0, [9] = 0, [10] = 16, [11] = 16,
            [12] = 16, [13] = 16, [14] = 16, [15] = 16, [16] = 16, [17] = 32, [18] = 16, [19] = 16,
            [20] = 16, [21] = 16, [22] = 16, [23] = 16, [24] = 16, [25] = 16, [26] = 16, [27] = 32,
            [28] = 32, [29] = 16, [31] = 32,
        };
        static readonly HashSet<int> unknownCommands = new HashSet<int>();

        public static void Chanting_Postfix(BattleActData battleActData, ref float __result)
        {
            try
            {
                if (!Plugin.WcMode || WcSeeds.Active == null || battleActData == null) return;
                var abilities = battleActData.abilityList;
                var ab = abilities != null && abilities.Count > 0 ? abilities[0] : null;
                var u = battleActData.AttackUnitData;
                var p = u?.BattleUnitDataInfo?.Parameter;
                if (ab == null || p == null) return;
                bool haste = false, slow = false, wild = false, dancing = false;
                var list = p.CurrentConditionList;
                if (list != null)
                    foreach (var c in list)
                    {
                        if (c == null) continue;
                        int t = c.ConditionType;
                        if (t == 15) haste = true;
                        else if (t == 16) slow = true;
                        else if (t == 410 || t == 12) wild = true;      // Berserk ("Bersak"), Confusion
                        else if (t == 619) dancing = true;              // Dance
                    }
                bool monster = u.TryCast<BattleEnemyData>() != null;
                int cfg = UserDataManager.Instance()?.Config?.BattleSpeed ?? 2;
                float speed = BattleUtility.GetBattleSpeedByConfigValue(cfg);   // the chant counts down at this rate
                float was = __result;
                if (ab.TypeId == 16)                                           // the landing (ContinuousJumpEnd)
                {
                    double air = SnesJumpSeconds(p.ConfirmedAgility(), monster, SnesBattleSpeed(), haste, slow);
                    __result = (float)(air * speed);
                    JumpChants++;
                    if (JumpChants == 1)
                        Plugin.L.LogInfo($"[wc] ATB: a landing's chant {was:0.00} -> {__result:0.00} (Speed {p.ConfirmedAgility()}: the SNES's {air:0.00} s in the air)");
                    return;
                }
                if (monster || battleActData.isManipulateAttack) return;         // monsters: none (the PR's 0)
                int cmd = battleActData.Command?.Id ?? 0;
                if (ab.TypeId == 15 || wild || battleActData.isRampageAct || (dancing && cmd == 22))
                {
                    __result = 0f;                                             // up at once / no wait
                    return;
                }
                if (battleActData.isCopiedAct) cmd = 25;                         // Mimic's own wait
                if (!CommandWaits)                                               // test only: the game's own
                {
                    if (WcTest.Running && cmd == 1) ChantLog.Add((WcStatusTicks.BattleTime, u.Pointer, cmd, was));
                    return;
                }
                if (!SnesCommandWait.TryGetValue(cmd, out int wait))
                {
                    if (unknownCommands.Add(cmd))
                        Plugin.L.LogInfo($"[wc] ATB: no SNES wait for command {cmd} (ability {ab.Id}, type {ab.TypeId}): the game's {was:0.00} stays");
                    return;
                }
                double secs = SnesWaitSeconds(wait, p.ConfirmedAgility(), false, 0, haste, slow);
                __result = (float)(secs * speed);
                CommandChants++;
                if (WcTest.Running) ChantLog.Add((WcStatusTicks.BattleTime, u.Pointer, cmd, (float)secs));
                if (CommandChants <= 3)
                    Plugin.L.LogInfo($"[wc] ATB: command {cmd} (ability {ab.Id}) waits {secs:0.00} s, the SNES's {wait} (the game's chant {was:0.00})");
            }
            catch (Exception e) { WcRunner.WarnOnce("atb.jump", e); }
        }
        internal static int JumpChants, CommandChants;
        internal static bool CommandWaits = true;      // the headless check turns it off to compare
        // test only (WcTest status_timers): battle time, unit, command, the SNES wait given
        internal static readonly List<(float Time, IntPtr Unit, int Command, float Wait)> ChantLog = new List<(float, IntPtr, int, float)>();

        internal static int SnesBattleSpeed()
        {
            int v = UserDataManager.Instance()?.Config?.BattleSpeed ?? 2;
            return v >= 0 && v < SnesBattleSpeedOf.Length ? SnesBattleSpeedOf[v] : 2;
        }

        public static void ATBCalc_Postfix(BattleUnitData __0, ref float __result)
        {
            long t0 = 0;
            try
            {
                if (!Plugin.WcMode || WcSeeds.Active == null || __0 == null || __result <= 0) return;
                t0 = WcPerf.Now();
                var p = __0.BattleUnitDataInfo?.Parameter;
                if (p == null) return;
                bool haste = false, slow = false;
                var list = p.CurrentConditionList;
                if (list != null)
                    foreach (var c in list)
                    {
                        if (c == null) continue;
                        if (c.ConditionType == 15) haste = true;        // ConditionType.Heist
                        else if (c.ConditionType == 16) slow = true;    // ConditionType.Slow
                    }
                bool monster = __0.TryCast<BattleEnemyData>() != null;
                __result = (float)(SnesRate(p.ConfirmedAgility(), monster, SnesBattleSpeed(), haste, slow) * Time.deltaTime);
                Calls++;
            }
            catch (Exception e) { WcRunner.WarnOnce("atb", e); }
            finally { if (t0 != 0) WcPerf.Lap(10, "atb", t0); }
        }
    }
}
