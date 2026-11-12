using System;
using System.Collections.Generic;
using System.Linq;
using Last.Battle;
using UnityEngine;

namespace WorldsCollide.PR
{
    // WcEnemyAi, part 2: what the interpreter's actions become in the PR.
    static partial class WcEnemyAi
    {
        const int Nothing = 696;   // the PR AIs' idle ability

        // SNES attack index -> PR ability for this monster: its Battle (EE) and special (EF), WC's scaled
        // placeholders (WcAbilityScaling swaps them for a tier's spell when the act is made), its special
        // for WC's special tiers (5E-60), else the spell/skill map; 0 when there's nothing to cast
        internal static int AbilityFor(int snesMonster, int attack)
        {
            var d = WcSeeds.Active?.SnesAi;
            if (d == null) return 0;
            int battle = d.Battle.TryGetValue(snesMonster, out int bb) ? bb : 1;
            if (attack == 0xEE) return battle;
            if (attack == 0xEF) return d.Special.TryGetValue(snesMonster, out int sp) ? sp : battle;
            var sc = WcSeeds.Active?.Scaling;
            if (sc != null)
            {
                if (attack >= 0x5E && attack <= 0x60) return d.Special.TryGetValue(snesMonster, out int sp2) ? sp2 : battle;
                int k = attack >= 0x36 && attack <= 0x50 ? attack - 0x36 : attack >= 0x55 && attack <= 0x5D ? attack - 0x3A : -1;
                if (k >= 0 && k < sc.Count) return sc.Base + k;
            }
            int a = attack < d.Attacks.Length ? d.Attacks[attack] : 0;
            return a > 0 ? a : battle;
        }

        // an FF6 target mask -> the PR units (null: the ability's default targets)
        internal static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BattleUnitData> TargetsOf(int mask, BattleUnitData[] units)
        {
            if (mask == 0) return null;
            var list = new List<BattleUnitData>();
            for (int i = 0; i < 10; i++)
                if ((mask & SnesAi.MaskOf(i)) != 0 && units[i] != null && !IsDead(units[i])) list.Add(units[i]);
            return list.Count == 0 ? null : new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BattleUnitData>(list.ToArray());
        }

        // variable / switch changes: $00-$23 the battle's, from $24 the monster's own byte
        static void ApplyVars(Fight f, SnesAiMonster m, SnesAiAction a)
        {
            if (a.Op == SnesAiOp.ChangeVar)
            {
                if (SnesAi.IsBattleVar(a.B1)) f.Vars[a.B1] = SnesAi.ChangeVar(f.Vars[a.B1], a.B2);
                else m.Var = SnesAi.ChangeVar(m.Var, a.B2);
            }
            else if (a.Op == SnesAiOp.ChangeSwitch)
            {
                if (SnesAi.IsBattleVar(a.B2)) f.Vars[a.B2] = SnesAi.ChangeSwitch(f.Vars[a.B2], a.B1, a.B3);
                else m.Var = SnesAi.ChangeSwitch(m.Var, a.B1, a.B3);
            }
        }

        // FB misc effects the battle can do here (C2/1E5E MiscAIEffectTbl): timers; the rest is logged
        static void ApplyMisc(Fight f, int slot, SnesAiAction a)
        {
            switch (a.B1)
            {
                case 0x00: f.MonsterTime[slot] = 0; break;   // reset this monster's timer
                case 0x04: f.BattleTime = 0; break;          // reset the battle timer
                default:
                    if (Seen.Add($"misc {a.B1:x2}")) Plugin.L.LogInfo($"[wc] enemy AI: misc effect {a.B1:x2} {a.B2:x2} not done yet");
                    break;
            }
        }

        internal static readonly HashSet<string> Seen = new HashSet<string>();
    }
}
