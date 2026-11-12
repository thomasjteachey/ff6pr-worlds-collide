using System;
using System.Collections.Generic;

namespace WorldsCollide.PR
{
    // FF6's monster AI script interpreter, ported from the SNES game's own code (bank C2 1A2F-2065:
    // everything8215/ff6 src/battle/ai_cmd.asm, ExecMonsterAction/ExecAIRetal in action_1/action_2.asm),
    // so every monster runs Worlds Collide's script exactly as the SNES does (the user, 2026-11-12:
    // "please make all enemies/bosses behave like WC SNES"). Pure logic: the battle comes in through
    // ISnesAiBattle and the result is the list of actions the monster queues, in order (attacks with their
    // targets, variable and switch changes, messages, entries/exits, misc effects).
    //
    // The turn model (ExecAI/DoNextAICmd): every turn starts at the top of the script. A condition (FC)
    // that fails resets the target and skips to the next FE (counting the skip in f5), or ends the turn if
    // FF comes first. The first command below FC the turn reaches jumps to the point the last wait saved
    // (f2) when the skip count equals the one saved with it (f4): a block that waits goes on next turn only
    // while the same conditions lead there. FD saves that point (f2) and ends the turn; FE and FF end it
    // and forget the count (f5 = $ff). Counter scripts run the same way with their own saved point/count.
    public enum SnesAiOp { Attack, Command, Item, Throw, ChangeVar, ChangeSwitch, Misc, Message, EntryExit, BattleEvent, Animation, ChangeBattle }

    public struct SnesAiAction
    {
        public SnesAiOp Op;
        public int Command;    // battle command for attacks (GetCmdForAI), items, or the F4 command
        public int Attack;     // attack index (spell/skill id) or item id
        public int Targets;    // FF6 target mask (bits 0-3 characters, 8-13 monsters); 0 = the attack's default
        public int B1, B2, B3; // the command's data bytes as written in the script
        public override string ToString() => Op switch
        {
            SnesAiOp.Attack => $"attack {Attack:x2} (cmd {Command:x2}) -> {Targets:x4}",
            SnesAiOp.Item or SnesAiOp.Throw => $"{Op} item {Attack:x2} -> {Targets:x4}",
            SnesAiOp.Command => $"command {Command:x2} -> {Targets:x4}",
            _ => $"{Op} {B1:x2} {B2:x2} {B3:x2}",
        };
    }

    // What the interpreter asks about the battle. Units: 0-3 the character slots, 4-9 the monster slots;
    // masks as FF6's: bit u for a character slot, bit 8 + (u - 4) for a monster slot.
    public interface ISnesAiBattle
    {
        int Rand(int n);                 // 0..n-1 (C2 RandA)
        int CharActor(int slot);         // the character in a party slot ($3ED8), -1 if none
        bool MonsterPresent(int slot);   // the monster flag ($2001): in this battle's formation, not removed
        int TargetableMask();            // CheckTargetsPresent: who an action can target right now
        bool Dead(int unit);             // status 1 Wound
        int Status(int unit, int word);  // word 0 = status 1|2 ($3EE4), 1 = status 3|4 ($3EF8)
        int Hp(int unit);
        int Mp(int unit);
        int Level(int unit);
        int WeakElements(int unit);
        bool CharPresentFlag(int slot);  // $3AA0.0 for target codes $48-$4B
        int BattleTimer();
        int MonsterTimer(int unit);
        int Formation();                 // $11E0
        int CharsAlive();                // $3A76
        int MonstersAlive();             // $3A77
        int MonsterTypesAlive();         // $3EB0 + 26
        int MonstersInBattleMask();      // $3A73: monsters this battle has (alive or dead)
        int MonstersAliveMask();         // $3A74 high byte: monsters alive
        bool GauRecruited();             // $1EDF Gau bit
        byte BattleVar(int i);           // 0x00-0x23 ($3EB0): shared by the battle's monsters
        // what hit this monster last (the retaliation data its counter script reads)
        int RetalTarget(int unit);       // any action: the unit that used it, -1 if none
        int RetalCmdTarget(int unit);    // the unit, -1 if none
        int RetalCommand(int unit);      // $3D48
        int RetalAttack(int unit);       // $3D49
        int RetalItem(int unit);
        int RetalElemTarget(int unit);
        int RetalElements(int unit);
        int PrevAttacker(int unit);      // $32E0: unit that last attacked it (target $45), -1 if none
        int TargettingTarget(int unit);  // $32F5 (target $4D), -1 if none; the interpreter clears it
        void ClearTargettingTarget(int unit);
    }

    // One monster's AI state across turns ($3254/$3268 starts, $3D0C/$3D20 points, $3240/$3241 counts,
    // its own variable byte for variables $24 and up).
    public sealed class SnesAiMonster
    {
        public byte[] Script = Array.Empty<byte>();  // main section then counter section
        public int MainStart, CounterStart = -1;     // -1: no counter section
        public int MainLoop, MainCount = 0xFF, CounterLoop, CounterCount = 0xFF;
        public byte Var;

        public static SnesAiMonster FromScript(byte[] script)
        {
            var m = new SnesAiMonster { Script = script, MainStart = 0 };
            // InitAI: the counter section starts after the main section's FF, if it has anything
            int end = SnesAi.FindEnd(script, 0, out int term);
            while (term != 0xFF && end < script.Length) end = SnesAi.FindEnd(script, end, out term);
            if (end < script.Length && script[end] != 0xFF) m.CounterStart = end;
            m.MainLoop = 0;
            m.CounterLoop = m.CounterStart < 0 ? 0 : m.CounterStart;
            return m;
        }
    }

    public static class SnesAi
    {
        static readonly int[] CmdSize = { 4, 2, 4, 3, 4, 4, 4, 2, 3, 4, 4, 3, 4, 1, 1, 1 };  // F0..FF (AICmdSizeTbl)

        // attack index -> battle command (GetCmdForAI: first attack of each range, from the top)
        static readonly int[] AttackFirst = { 0x36, 0x51, 0x55, 0x5D, 0x65, 0x7D, 0x82, 0x83, 0x8B, 0xEE, 0xF0 };
        static readonly int[] AttackCmd = { 0x19, 0x02, 0x07, 0x0A, 0x02, 0x09, 0x1B, 0x1D, 0x0C, 0x00, 0x02 };
        public const int CmdItem = 0x01, CmdThrow = 0x08, CmdMagic = 0x02;

        public static int CommandFor(int attack)
        {
            for (int i = AttackFirst.Length - 1; i >= 0; i--)
                if (attack >= AttackFirst[i]) return AttackCmd[i];
            return CmdMagic;
        }

        public static int MaskOf(int unit) => unit < 4 ? 1 << unit : 1 << (8 + unit - 4);

        static int UnitOf(int mask)
        {
            for (int u = 0; u < 10; u++) if ((mask & MaskOf(u)) != 0) return u;
            return -1;
        }

        static int RandBit(ISnesAiBattle b, int mask)
        {
            int n = 0;
            for (int i = 0; i < 16; i++) if ((mask >> i & 1) != 0) n++;
            if (n == 0) return 0;
            int k = b.Rand(n);
            for (int i = 0; i < 16; i++)
                if ((mask >> i & 1) != 0 && k-- == 0) return 1 << i;
            return 0;
        }

        // FindAIScriptEnd: from pos, the position after the next FE/FF; term = that byte
        public static int FindEnd(byte[] s, int pos, out int term)
        {
            while (pos < s.Length)
            {
                int c = s[pos++];
                if (c >= 0xFE) { term = c; return pos; }
                if (c >= 0xF0) pos += CmdSize[c - 0xF0] - 1;
            }
            term = 0xFF;
            return pos;
        }

        // CheckAITarget: the mask for target code `code` as monster `self` sees it, 0 if nobody valid.
        // filterPresent: actions filter by who can be targeted now (CheckTargetsPresent); conditions don't.
        public static int Target(ISnesAiBattle b, int self, int code, bool filterPresent)
        {
            int z = 0;
            for (int s = 0; s < 4; s++) if (b.CharActor(s) >= 0) z |= MaskOf(s);
            for (int s = 0; s < 6; s++) if (b.MonsterPresent(s)) z |= MaskOf(4 + s);
            if (filterPresent) z &= b.TargetableMask();
            int lo = z & 0xFF, hi = z & 0xFF00;
            int selfMask = MaskOf(self);
            int Dead()
            {
                int m = 0;
                for (int u = 0; u < 10; u++) if (b.Dead(u)) m |= MaskOf(u);
                return m;
            }
            int Reflect()
            {
                int m = 0;
                for (int u = 0; u < 10; u++) if ((b.Status(u, 1) & 0x0080) != 0) m |= MaskOf(u);   // status 3 bit 7: Reflect
                return m;
            }
            if (code < 0x30)                              // a specific character, wherever it stands
            {
                for (int s = 0; s < 4; s++)
                    if (b.CharActor(s) == code) return MaskOf(s);
                return 0;
            }
            if (code < 0x36)                              // monster slot 1-6
                return b.MonsterPresent(code - 0x30) ? MaskOf(4 + code - 0x30) : 0;
            switch (code)
            {
                case 0x36: return selfMask;
                case 0x37: return hi & ~selfMask;
                case 0x38: return hi;
                case 0x39: return RandBit(b, hi & ~selfMask);
                case 0x3A: return RandBit(b, hi);
                case 0x3B: return Dead() & 0xFF;
                case 0x3C: return RandBit(b, Dead() & 0xFF);
                case 0x3D: return Dead() & 0xFF00;
                case 0x3E: return RandBit(b, Dead() & 0xFF00);
                case 0x3F: return Reflect() & 0xFF;
                case 0x40: return RandBit(b, Reflect() & 0xFF);
                case 0x41: return Reflect() & 0xFF00;
                case 0x42: return RandBit(b, Reflect() & 0xFF00);
                case 0x43: return lo;
                case 0x44: return RandBit(b, lo);
                case 0x45:
                    {
                        int u = b.PrevAttacker(self);
                        return u >= 0 && u < 10 ? MaskOf(u) : 0;
                    }
                case 0x46: return z;
                case 0x47: return 0;
                case 0x48: case 0x49: case 0x4A: case 0x4B:
                    return b.CharPresentFlag(code - 0x48) ? MaskOf(code - 0x48) : 0;
                case 0x4C:
                    {
                        int others = z & ~selfMask;
                        return b.Rand(2) == 0 ? RandBit(b, others) : others;
                    }
                case 0x4D:
                    {
                        int u = b.TargettingTarget(self);
                        if (u < 0) return 0;
                        b.ClearTargettingTarget(self);
                        return MaskOf(u) & b.TargetableMask();
                    }
            }
            return 0;
        }

        sealed class Run
        {
            public ISnesAiBattle B;
            public SnesAiMonster M;
            public int Self;
            public int Fc, Fe;            // the current and the default target ($fc/$fe)
            public bool Unresponsive;     // $3A98 copy ($f8)
            public readonly List<SnesAiAction> Out = new List<SnesAiAction>();
        }

        // A normal turn (ExecMonsterAction). confused: the monster is muddled (its attacks get no target).
        public static List<SnesAiAction> RunMain(SnesAiMonster m, int self, ISnesAiBattle b, bool confused = false)
        {
            var r = new Run { B = b, M = m, Self = self };
            Exec(r, m.MainStart, ref m.MainLoop, ref m.MainCount, confused);
            return r.Out;
        }

        // A counter (ExecAIRetal). unresponsive: asleep, confused, berserk, frozen, stopped, charmed, or
        // died having targeted itself: only blocks with "if dead" / "if always" act ($3A98).
        public static List<SnesAiAction> RunCounter(SnesAiMonster m, int self, ISnesAiBattle b, bool unresponsive, bool confused = false, int targets = 0)
        {
            var r = new Run { B = b, M = m, Self = self, Unresponsive = unresponsive, Fc = targets, Fe = targets };
            if (m.CounterStart < 0) return r.Out;
            Exec(r, m.CounterStart, ref m.CounterLoop, ref m.CounterCount, confused);
            return r.Out;
        }

        static void Exec(Run r, int start, ref int loopAddr, ref int loopCount, bool confused)
        {
            var s = r.M.Script;
            int f0 = start, f2 = loopAddr, f4 = loopCount, f5 = 0;
            bool f8 = r.Unresponsive;
            for (int guard = 0; guard < 4096; guard++)
            {
                if (f0 >= s.Length) { f5 = 0xFF; break; }
                int c = s[f0];
                if (c < 0xFC && f5 == f4)
                {
                    f4 = 0xFF;            // resume where the last wait left off
                    f0 = f2;
                    if (f0 >= s.Length) { f5 = 0xFF; break; }
                    c = s[f0];
                }
                int size = c >= 0xF0 ? CmdSize[c - 0xF0] : 1;
                int b1 = f0 + 1 < s.Length ? s[f0 + 1] : 0, b2 = f0 + 2 < s.Length ? s[f0 + 2] : 0, b3 = f0 + 3 < s.Length ? s[f0 + 3] : 0;
                f0 += size;
                bool failed = false;
                if (f8 && c < 0xFC)
                    failed = true;        // unresponsive: actions only in blocks a dead/always check opened
                else if (c < 0xF0)
                    Queue(r, c, confused);
                else
                {
                    switch (c)
                    {
                        case 0xF0:
                            {
                                int a = Rand3(r, b1, b2, b3);
                                if (a != 0xFE) Queue(r, a, confused);
                                break;
                            }
                        case 0xF1:
                            r.Fc = Target(r.B, r.Self, b1, false);   // clc: the action filter applies later (default when 0)
                            break;
                        case 0xF4:
                            {
                                int cmd = Rand3(r, b1, b2, b3);
                                if (cmd != 0xFE) r.Out.Add(new SnesAiAction { Op = SnesAiOp.Command, Command = cmd, Targets = confused ? 0 : r.Fc, B1 = b1, B2 = b2, B3 = b3 });
                                break;
                            }
                        case 0xF6:
                            {
                                int item = Rand3(r, b2, b2, b3);       // the first item is twice as likely
                                if (item != 0xFE)
                                    r.Out.Add(new SnesAiAction { Op = b1 == 0 ? SnesAiOp.Item : SnesAiOp.Throw, Command = b1 == 0 ? CmdItem : CmdThrow, Attack = item, Targets = confused ? 0 : r.Fc, B1 = b1, B2 = b2, B3 = b3 });
                                break;
                            }
                        case 0xF2: r.Out.Add(new SnesAiAction { Op = SnesAiOp.ChangeBattle, B1 = b1, B2 = b2, B3 = b3 }); break;
                        case 0xF3: r.Out.Add(new SnesAiAction { Op = SnesAiOp.Message, B1 = b1, B2 = b2 }); break;
                        case 0xF5:
                            r.Out.Add(new SnesAiAction { Op = SnesAiOp.EntryExit, B1 = b1, B2 = b2, B3 = b3 != 0 ? b3 : MaskOf(r.Self) >> 8 });
                            break;
                        case 0xF7: r.Out.Add(new SnesAiAction { Op = SnesAiOp.BattleEvent, B1 = b1 }); break;
                        case 0xF8: r.Out.Add(new SnesAiAction { Op = SnesAiOp.ChangeVar, B1 = b1, B2 = b2 }); break;
                        case 0xF9: r.Out.Add(new SnesAiAction { Op = SnesAiOp.ChangeSwitch, B1 = b1, B2 = b2, B3 = b3 }); break;
                        case 0xFA: r.Out.Add(new SnesAiAction { Op = SnesAiOp.Animation, B1 = b1, B2 = b2, B3 = b3 }); break;
                        case 0xFB: r.Out.Add(new SnesAiAction { Op = SnesAiOp.Misc, B1 = b1, B2 = b2 }); break;
                        case 0xFC:
                            if (!Condition(r, b1, b2, b3, ref f8)) failed = true;
                            break;
                        case 0xFD:
                            f2 = f0;              // wait: go on from here next time
                            loopAddr = f2;
                            loopCount = f5;
                            return;
                        default:                  // FE/FF: end of this turn's block / script
                            f5 = 0xFF;
                            loopAddr = f2;
                            loopCount = f5;
                            return;
                    }
                }
                if (failed)
                {
                    r.Fc = r.Fe;                  // AICondFailed: back to the default target
                    int after = FindEnd(s, f0, out int term);
                    if (term == 0xFF) { f5 = 0xFF; break; }
                    f0 = after;
                    f5 = (f5 + 1) & 0xFF;
                    f8 = r.Unresponsive;          // ExecAICmd copies the unresponsive indicator again
                }
            }
            loopAddr = f2;
            loopCount = f5;
        }

        static int Rand3(Run r, int b1, int b2, int b3)
        {
            int k = r.B.Rand(3);
            return k == 0 ? b1 : k == 1 ? b2 : b3;
        }

        static void Queue(Run r, int attack, bool confused)
        {
            r.Out.Add(new SnesAiAction { Op = SnesAiOp.Attack, Attack = attack, Command = CommandFor(attack), Targets = confused ? 0 : r.Fc });
        }

        // IF_TARGET_VALID: sets the target to `code`'s (except self: no forced target); the unit tested
        static bool TargetValid(Run r, int code, out int unit)
        {
            int mask = Target(r.B, r.Self, code, false);
            unit = -1;
            if (mask == 0) return false;
            r.Fc = mask;
            unit = UnitOf(mask);
            if (code == 0x36) r.Fc = 0;
            return true;
        }

        static bool Condition(Run r, int kind, int a, int v, ref bool f8)
        {
            var b = r.B;
            switch (kind)
            {
                case 0x01: // IF_CMD: a command (a or v) was used against it
                    {
                        int u = b.RetalCmdTarget(r.Self);
                        if (u < 0) return false;
                        int cmd = b.RetalCommand(r.Self);
                        if (cmd != a && cmd != v) return false;
                        r.Fc = MaskOf(u);
                        return true;
                    }
                case 0x02: // IF_ATTACK
                    {
                        int u = b.RetalCmdTarget(r.Self);
                        if (u < 0) return false;
                        int at = b.RetalAttack(r.Self);
                        if (at != a && at != v) return false;
                        r.Fc = MaskOf(u);
                        return true;
                    }
                case 0x03: // IF_ITEM
                    {
                        int u = b.RetalCmdTarget(r.Self);
                        if (u < 0) return false;
                        int it = b.RetalItem(r.Self);
                        if (it != a && it != v) return false;
                        r.Fc = MaskOf(u);
                        return true;
                    }
                case 0x04: // IF_ELEMENT
                    {
                        int u = b.RetalElemTarget(r.Self);
                        if (u < 0 || (b.RetalElements(r.Self) & a) == 0) return false;
                        r.Fc = MaskOf(u);
                        return true;
                    }
                case 0x05: // IF_HIT: by anything
                    {
                        int u = b.RetalTarget(r.Self);
                        if (u < 0) return false;
                        r.Fc = MaskOf(u);
                        return true;
                    }
                case 0x06: // IF_HP: target's HP <= v * 128
                    return TargetValid(r, a, out int hu) && v * 128 >= b.Hp(hu);
                case 0x07: // IF_MP
                    return TargetValid(r, a, out int mu) && v >= b.Mp(mu);
                case 0x08: // IF_STATUS_SET: a random one of the targets with status v becomes the target
                    return StatusSet(r, a, v);
                case 0x09: // IF_STATUS_CLR
                    return !StatusSet(r, a, v);
                case 0x0B: // IF_MONSTER_TIMER: timer / 2 >= a | v << 8
                    return b.MonsterTimer(r.Self) / 2 >= (a | v << 8);
                case 0x0C: // IF_BATTLE_VAR_LESS
                    return GetVar(r, a) < v;
                case 0x0D: // IF_BATTLE_VAR_GREATER (>=)
                    return GetVar(r, a) >= v;
                case 0x0E: // IF_LEVEL_LESS (also true when no target is valid)
                    return !(TargetValid(r, a, out int lu) && b.Level(lu) >= v);
                case 0x0F: // IF_LEVEL_GREATER (>=)
                    return TargetValid(r, a, out int gu) && b.Level(gu) >= v;
                case 0x10: // IF_ONE_MONSTER_TYPE
                    return b.MonsterTypesAlive() <= 1;
                case 0x11: // IF_ALIVE: every monster in mask a (0: itself) is alive
                    {
                        int mask = a != 0 ? a : MaskOf(r.Self) >> 8;
                        return (b.MonstersAliveMask() & mask) == mask;
                    }
                case 0x12: // IF_DEAD: every monster in mask a (0: itself) is dead; allowed when unresponsive
                    {
                        f8 = false;
                        int mask = a != 0 ? a : MaskOf(r.Self) >> 8;
                        return ((b.MonstersInBattleMask() ^ b.MonstersAliveMask()) & mask) == mask;
                    }
                case 0x13: // IF_NUM_TARGETS: a = 0: characters alive >= v; else monsters alive <= v
                    return a == 0 ? b.CharsAlive() >= v : v >= b.MonstersAlive();
                case 0x14: // IF_SWITCH_SET: bit v of variable a (AIGetBit: bits past 7 never match)
                    return v < 8 && (GetVar(r, a) >> v & 1) != 0;
                case 0x15: // IF_SWITCH_CLR
                    return !(v < 8 && (GetVar(r, a) >> v & 1) != 0);
                case 0x16: // IF_BATTLE_TIMER
                    return b.BattleTimer() / 2 >= (a | v << 8);
                case 0x17: // IF_TARGET_VALID
                    return TargetValid(r, a, out _);
                case 0x18: // IF_GAU_PRESENT (the SNES leaves the carry as it was when Gau is recruited: false)
                    return !b.GauRecruited();
                case 0x19: // IF_SELF_IN_SLOT
                    return ((MaskOf(r.Self) >> 8) & a) != 0;
                case 0x1A: // IF_WEAK_ELEMENT
                    return TargetValid(r, a, out int wu) && (b.WeakElements(wu) & v) != 0;
                case 0x1B: // IF_BATTLE_ID
                    return b.Formation() == (a | v << 8);
                case 0x1C: // IF_ALWAYS: allowed when unresponsive
                    f8 = false;
                    return true;
                default:   // $00 never, $0A unused
                    return false;
            }
        }

        static bool StatusSet(Run r, int code, int status)
        {
            var b = r.B;
            if (!TargetValid(r, code, out _)) return false;
            int word = status >= 0x10 ? 1 : 0, bit = 1 << (status & 0x0F);
            if (word == 1)                // status 3/4: only the living ($3A74)
                for (int u = 0; u < 10; u++) if (b.Dead(u)) r.Fc &= ~MaskOf(u);
            int keep = 0;
            for (int u = 0; u < 10; u++)
                if ((r.Fc & MaskOf(u)) != 0 && (b.Status(u, word) & bit) != 0) keep |= MaskOf(u);
            r.Fc = keep;
            if (keep == 0) return false;
            r.Fc = RandBit(b, keep);
            return true;
        }

        // GetBattleVar: $00-$23 the battle's ($3EB0), from $24 the monster's own byte
        static int GetVar(Run r, int i) => i < 0x24 ? r.B.BattleVar(i) : r.M.Var;
        public static bool IsBattleVar(int i) => i < 0x24;

        // CHANGE_VAR (command $2E): b1 = variable, b2: bit 7 clear = set to the low 6 bits,
        // bit 7 set = add (bit 6 clear) or subtract (bit 6 set) the low 6 bits. Returns the new value.
        public static byte ChangeVar(byte value, int op)
        {
            int x = op & 0xFF;
            if ((x & 0x80) == 0) return (byte)(x & 0x3F);
            int n = x & 0x3F;
            return (byte)((x & 0x40) == 0 ? value + n : value - n);
        }

        // CHANGE_SWITCH (command $2F): b1 = 0 toggle, 1 set, 2 clear bit b3 of variable b2
        public static byte ChangeSwitch(byte value, int kind, int bit)
        {
            int m = 1 << (bit & 7);
            return kind switch { 0 => (byte)(value ^ m), 1 => (byte)(value | m), _ => (byte)(value & ~m) };
        }
    }
}
