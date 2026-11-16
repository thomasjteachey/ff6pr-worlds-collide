namespace BraveNewWorld.Rules
{
    // BNW battle arithmetic the plugin applies, in the SNES's integer steps (offline-tested in
    // Rules.Tests).
    public static class BattleMath
    {
        // The two-handed grip (c2.asm 4914 PlayerPhys): Battle Power + Battle Power / 2.
        public static int Grip(int battlePower) => battlePower + battlePower / 2;

        // Shock (c2.asm C23EDD): x = Level * Stamina + current HP, then (x * 2 + x) / 4 in 16 bits
        // (ASL's carry goes into the ADC; a sum past 65535 wraps, as on the SNES: Level 99,
        // Stamina 128 at 9999 HP hits for 619), halved from the back row.
        public static int Shock(int level, int stamina, int hp, bool backRow)
        {
            int x = (level * stamina + hp) & 0xFFFF;
            int twice = x << 1;
            int v = ((twice & 0xFFFF) + x + (twice > 0xFFFF ? 1 : 0)) & 0xFFFF;
            v >>= 2;
            return backRow ? v >> 1 : v;
        }

        // Shock's cost to its user: max HP / 8 (LSR x3).
        public static int ShockCost(int maxHp) => maxHp >> 3;

        // A sketched physical attack's "Vigor": the sketcher's Magic, doubled as the SNES stores
        // Vigor (c2.asm SketchMag: ASL in 8 bits, so Magic 128+ wraps), handed back undoubled.
        public static int SketchVigor(int magic) => ((magic << 1) & 0xFF) >> 1;

        // True Knight (c2.asm C2123B TrueKnight / EvalKnight): the guard that covers an ally, or
        // -1. A guard must wear the knight's effect and stand in the front row. A Near Fatal
        // target is always covered; otherwise only a back-row target, by a guard who isn't Near
        // Fatal, when random(0..191) (0..95 while the guard defends) < the guard's Stamina. The
        // guard must be able to act (Blocked: dead, petrified, seized, the status blacklist), and
        // of those who would, the one with the most HP covers (equal HP: the later one in the
        // list; BNW's loop runs from the last party slot down, so callers pass slot order reversed).
        public sealed class CoverGuardInfo
        {
            public bool Knight, BackRow, Dying, Defending, Blocked;
            public int Stamina, Hp;
        }

        public static int CoverGuard(bool targetDying, bool targetBackRow, System.Collections.Generic.IList<CoverGuardInfo> guards,
            System.Func<int, int> random)
        {
            int best = -1, bestHp = 0;
            for (int i = 0; i < guards.Count; i++)
            {
                var g = guards[i];
                if (g == null || !g.Knight || g.BackRow) continue;
                if (!targetDying)
                {
                    if (!targetBackRow || g.Dying) continue;
                    if (random(g.Defending ? 96 : 192) >= g.Stamina) continue;
                }
                if (g.Blocked || g.Hp < bestHp) continue;
                best = i;
                bestHp = g.Hp;
            }
            return bestHp > 0 ? best : -1;
        }
    }
}
