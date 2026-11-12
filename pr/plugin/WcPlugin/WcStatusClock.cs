using System;

namespace WorldsCollide.PR
{
    [Flags]
    enum SnesStatus
    {
        None = 0, Haste = 1, Slow = 2, Stop = 4, Poison = 8, Regen = 16, Sap = 32,
        Sleep = 64, Reflect = 128, Freeze = 256, Doom = 512,
    }

    // The SNES's status clock (vanilla FF6's; WC doesn't change it), one per fighter. No game types,
    // so the offline tests (Wc.Tests) run it as the plugin does (WcStatusTicks: the game's side).
    //  * C2/111B: battle time moves every 2 frames (NTSC 60.0988 Hz); each move C2/5A83 visits one of
    //    16 slots in turn: fighters 0-9 (party 0-3, monsters 4-9), so a fighter every 32 frames.
    //  * a visit adds $3ADD to the fighter's counter $3ADC: 64, Haste 84, Slow 32 (C2/09D2, Slow first);
    //    past 256 it makes a status step.
    //  * a step (C2/5AA3): a stopped fighter's only counts Stop down ($3AF1; at 0 Stop goes and the
    //    timers below count once, C2/5B06). Otherwise: Condemned counts down ($3B05, C2/5AB8: death as
    //    it reaches 1); then the tick flagged by an earlier step is sent (C2/5B4F: Poison's first;
    //    Regen's flag ticks Sap if the fighter has it, else Regen), unless a tick it sent hasn't landed
    //    yet; then the timers count (C2/5B06: Reflect $3F0C, Freeze $3F0D, Sleep $3CF9; each status goes
    //    at 0); then the step index $3AF0 (random at the battle's start, C2/2457) moves on and the
    //    8-step table at C2/5AEA flags Regen/Sap at 0 and 4, Poison at 2.
    //  * the timers start when a status is set (the on-set handlers, C2/46B0 table): Sleep 18 (C2/4634),
    //    Stop 18 (C2/467D), Reflect 26 (C2/4687), Freeze 34 (C2/4691); Condemned (C2/463F, then
    //    C2/09B4) from the attacker's level L: 81 - (L + random 0..L-1), or 20 past 60. A fighter's
    //    statuses at the battle's start (equipment, innate) are written directly: no timer.
    // So, normal speed: Regen/Sap every 512 frames (8.52 s), Poison every 1024 (17.04 s); a step
    // 128 frames (2.13 s): Sleep and Stop 38.3 s, Reflect 55.4, Freeze 72.4, Condemned 19-80 steps;
    // Haste x84/64, Slow x1/2; the battle speed setting has no part in it. Measured on the SNES
    // (vanilla ROM in the windowless harness, tools/snes_status_ticks.py, 2026-11-12): steps every
    // 128-131 frames, Regen and Sap 515-516, Poison 1031 (the engine's few lag frames); a jumping
    // character's ticks land in the air on time; a Stop of 18 steps holds every tick.
    sealed class SnesStatusClock
    {
        internal const double FramesPerSecond = 60.0988;
        internal const double TickSeconds = 2 / FramesPerSecond;
        internal const int Slots = 16, Fighters = 10, PartySlots = 4;
        internal const int AddNormal = 64, AddHaste = 84, AddSlow = 32;
        internal const string StepTable = "R-P-R---";   // C2/5AEA: $5B45 Regen, $5B3B Poison, $5AE8 nothing
        internal const int SleepSteps = 18, StopSteps = 18, ReflectSteps = 26, FreezeSteps = 34;

        public int Counter, Step;
        public bool RegenFlag, PoisonFlag;
        public bool DueRegen, DueSap, DuePoison;
        public bool Waiting => DueRegen || DueSap || DuePoison;
        // the timers (0: none running); DoomLeft = steps to Condemned's end ($3B05 - 1; -1: not this
        // clock's: the game's own countdown)
        public int StopLeft, SleepLeft, ReflectLeft, FreezeLeft, DoomLeft = -1;
        public SnesStatus Expired;       // timers that ran out on this visit (the game takes the status off)
        public int DoomLevel = -1;       // the level of the last attacker that tried Condemned on this fighter
        public bool DoomSynced;          // the game's countdown has been set to DoomLeft
        SnesStatus had;
        bool seen;

        public SnesStatusClock(int step) { Step = step & 0xFF; }

        // C2/09B4: Condemned's count from the attacker's level (C2/4B65: random 0..L-1)
        internal static int CondemnedCount(int level, Random rng)
        {
            int l = Math.Clamp(level, 0, 255);
            int ee = l + (l > 0 ? rng.Next(l) : 0);
            return ee <= 60 ? 81 - ee : 20;
        }

        // A visit (C2/5A97); true when it made a status step.
        public bool Visit(SnesStatus s, Random rng)
        {
            bool Has(SnesStatus f) => (s & f) != 0;
            var gained = seen ? s & ~had : SnesStatus.None;     // at the start or on arrival: no timers
            var lost = had & ~s;
            seen = true;
            had = s;
            if ((gained & SnesStatus.Stop) != 0) StopLeft = StopSteps;
            if ((gained & SnesStatus.Sleep) != 0) SleepLeft = SleepSteps;
            if ((gained & SnesStatus.Reflect) != 0) ReflectLeft = ReflectSteps;
            if ((gained & SnesStatus.Freeze) != 0) FreezeLeft = FreezeSteps;
            if ((gained & SnesStatus.Doom) != 0 && DoomLevel >= 0) { DoomLeft = CondemnedCount(DoomLevel, rng) - 1; DoomSynced = false; }
            DoomLevel = -1;     // a try that didn't land (the act is over by the next visit)
            if ((lost & SnesStatus.Stop) != 0) StopLeft = 0;
            if ((lost & SnesStatus.Sleep) != 0) SleepLeft = 0;
            if ((lost & SnesStatus.Reflect) != 0) ReflectLeft = 0;
            if ((lost & SnesStatus.Freeze) != 0) FreezeLeft = 0;
            if ((lost & SnesStatus.Doom) != 0) { DoomLeft = -1; DoomSynced = false; }
            // a tick whose status has gone lands on nothing (the SNES's queued action does nothing)
            if (DuePoison && !Has(SnesStatus.Poison)) DuePoison = false;
            if (DueRegen && !Has(SnesStatus.Regen)) DueRegen = false;
            if (DueSap && !Has(SnesStatus.Sap)) DueSap = false;

            Counter += Has(SnesStatus.Slow) ? AddSlow : Has(SnesStatus.Haste) ? AddHaste : AddNormal;
            if (Counter < 256) return false;
            Counter -= 256;
            if (StopLeft > 0)
            {
                if (--StopLeft > 0) return true;
                Expired |= SnesStatus.Stop;
                CountTimers();
                return true;
            }
            if (Has(SnesStatus.Stop)) return true;     // a Stop with no timer (there from the start) holds the clock
            if (DoomLeft > 0) DoomLeft--;
            if (!Waiting)
            {
                if (PoisonFlag)
                {
                    PoisonFlag = false;
                    if (Has(SnesStatus.Poison)) DuePoison = true;
                }
                else if (RegenFlag)
                {
                    RegenFlag = false;
                    if (Has(SnesStatus.Sap)) DueSap = true;
                    else if (Has(SnesStatus.Regen)) DueRegen = true;
                }
            }
            CountTimers();
            Step = (Step + 1) & 0xFF;
            char k = StepTable[Step & 7];
            if (k == 'R') RegenFlag = true;
            else if (k == 'P') PoisonFlag = true;
            return true;
        }

        void CountTimers()
        {
            if (ReflectLeft > 0 && --ReflectLeft == 0) Expired |= SnesStatus.Reflect;
            if (FreezeLeft > 0 && --FreezeLeft == 0) Expired |= SnesStatus.Freeze;
            if (SleepLeft > 0 && --SleepLeft == 0) Expired |= SnesStatus.Sleep;
        }
    }

    // The 16-slot round of visits in real time (the battle's own clock: it waits while the game's does).
    sealed class SnesStatusRound
    {
        double carry;
        public int Tick;

        // Moves the round on by `seconds`; visit(slot) for each fighter slot (0-9) visited.
        public void Advance(double seconds, Action<int> visit)
        {
            carry += Math.Max(0, seconds);
            while (carry >= SnesStatusClock.TickSeconds)
            {
                carry -= SnesStatusClock.TickSeconds;
                int slot = Tick++ & (SnesStatusClock.Slots - 1);
                if (slot < SnesStatusClock.Fighters) visit(slot);
            }
        }

        public void Reset() { carry = 0; Tick = 0; }
    }
}
