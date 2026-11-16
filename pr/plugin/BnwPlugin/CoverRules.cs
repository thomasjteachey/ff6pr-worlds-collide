using System;
using System.Collections.Generic;
using BraveNewWorld.Rules;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.Battle;
using Last.Battle.Function;
using Last.Defaine;
using Last.Defaine.User;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // True Knight cover as BNW has it (c2.asm C2123B TrueKnight / EvalKnight / SmartCover; the
    // choice itself is BattleMath.CoverGuard, offline-tested). The PR (decompiled
    // tools/decomp/cover, cover2): TargetFunction.ProtectExtractTarget picks a single target's
    // guard: ProtectTarget for jobs with innate cover (IsInitialProtectDying: none in FF6),
    // otherwise its own second path, which covers a Near Fatal ally from a wearer of the knight's
    // effect (condition type 606 TipsForKnights: the port's wear condition 29 for relic bit 12.6),
    // both through act.SetProtectData(guard, covered, ...). A postfix there redoes the choice by
    // BNW's rules whenever a knight stands among the target's allies (and the game's own cover,
    // if any, was a knight's), through the same SetProtectData:
    //  * a Near Fatal target is covered; a healthy one only in the back row, with chance
    //    Stamina / 192 per knight (Stamina / 96 while the knight defends), by a knight who isn't
    //    Near Fatal; knights stand in the front row; the knight with the most HP covers;
    //  * no cover from a party member's own attack unless it's confused, a zombie or charmed;
    //    none of a dead, petrified, invisible, zombie, Magitek or Image target; no monster guards;
    //  * a knight can't cover while dead, petrified, invisible, a zombie, in Magitek armor,
    //    blinded, asleep, confused, berserk, with Image, dancing, stopped, frozen or hidden.
    // BNW also drops the knight's Defend when it covers (done here); its Love Token relic has no
    // PR counterpart. Config: [Battle] RelicRules.
    static class CoverRules
    {
        internal static bool Enabled = true;
        static readonly Random Rng = new Random();

        public static void Apply(Harmony h)
        {
            if (!Enabled) return;
            Hooks.Patch(h, typeof(TargetFunction), "ProtectExtractTarget", typeof(CoverRules), postfix: nameof(Extract_Postfix));
        }

        static readonly ConditionType[] TargetBlocked =
        {
            ConditionType.UnableFight, ConditionType.Mineralization, ConditionType.Transparent, ConditionType.Zombie,
            ConditionType.Madou, ConditionType.Blink,
        };
        static readonly ConditionType[] GuardBlocked =
        {
            ConditionType.UnableFight, ConditionType.Mineralization, ConditionType.Transparent, ConditionType.Zombie,
            ConditionType.Madou, ConditionType.Blind, ConditionType.Sleep, ConditionType.Confusion, ConditionType.Bersak,
            ConditionType.Blink, ConditionType.Dance, ConditionType.Stop, ConditionType.Freeze, ConditionType.Hide,
        };
        static readonly ConditionType[] Uncontrolled = { ConditionType.Confusion, ConditionType.Zombie, ConditionType.Temptation };

        static Il2CppSystem.Collections.Generic.List<ConditionType> Conditions(BattleUnitData u) =>
            u?.BattleUnitDataInfo?.GetConfirmedConditionType();

        static bool Has(Il2CppSystem.Collections.Generic.List<ConditionType> list, params ConditionType[] types)
        {
            if (list == null) return false;
            foreach (var t in types)
                if (list.Contains(t)) return true;
            return false;
        }

        static bool Knight(BattleUnitData u) => Has(Conditions(u), ConditionType.TipsForKnights);

        // BNW's guard for an attack on a party member, or null; knightNear: a knight stands among
        // the target's allies at all (else the game's choice stays)
        static BattleUnitData Choose(BattleActData act, BattleUnitData target, out bool knightNear, out string why)
        {
            knightNear = false;
            why = null;
            var battle = BattlePlugManager.Instance();
            var players = battle?.GetPlayerUnits();
            if (players == null) return null;
            var attacker = act.AttackUnitData;
            var units = new List<BattleUnitData>();
            var infos = new List<BattleMath.CoverGuardInfo>();
            for (int i = players.Count - 1; i >= 0; i--)   // BNW checks from the last slot down
            {
                BattleUnitData g = players[i];
                if (g == null || g.Pointer == target.Pointer || (attacker != null && g.Pointer == attacker.Pointer)) continue;
                var gc = Conditions(g);
                var p = g.BattleUnitDataInfo?.Parameter;
                bool knight = Has(gc, ConditionType.TipsForKnights);
                knightNear |= knight;
                infos.Add(new BattleMath.CoverGuardInfo
                {
                    Knight = knight,
                    BackRow = BattleUtility.GetCorpsId(g) == CorpsId.Back,
                    Dying = Has(gc, ConditionType.Dying),
                    Defending = Has(gc, ConditionType.Defend),
                    Blocked = p == null || p.CurrentHP <= 0 || Has(gc, GuardBlocked),
                    Stamina = p == null ? 0 : Math.Clamp(p.ConfirmedVitality(), 0, 255),
                    Hp = p?.CurrentHP ?? 0,
                });
                units.Add(g);
            }
            if (!knightNear) return null;
            if (attacker != null && attacker.TryCast<BattlePlayerData>() != null && !Has(Conditions(attacker), Uncontrolled)) return null;
            var tc = Conditions(target);
            if (Has(tc, TargetBlocked)) return null;
            bool dying = Has(tc, ConditionType.Dying);
            int pick = BattleMath.CoverGuard(dying, BattleUtility.GetCorpsId(target) == CorpsId.Back, infos, n => Rng.Next(n));
            if (pick < 0) return null;
            why = dying ? "Near Fatal ally" : $"back-row ally, Stamina {infos[pick].Stamina}{(infos[pick].Defending ? " (defending)" : "")}";
            return units[pick];
        }

        public static void Extract_Postfix(ref BattleActData __result)
        {
            if (!Enabled || __result == null) return;
            try
            {
                var act = __result;
                var targets = act.TargetUnitDatas;
                BattleUnitData covered = act.ProtectedTarget;
                BattleUnitData target = covered ?? (targets != null && targets.Length == 1 ? targets[0] : null);
                if (target == null || target.TryCast<BattlePlayerData>() == null) return;   // several targets / a monster: the game's
                BattleUnitData gameGuard = covered != null && targets != null && targets.Length == 1 && targets[0].Pointer != covered.Pointer
                    ? targets[0] : null;
                if (gameGuard != null && !Knight(gameGuard)) return;   // some other cover: the game's
                var guard = Choose(act, target, out bool knightNear, out string why);
                if (!knightNear) return;
                if (guard != null && gameGuard != null && guard.Pointer == gameGuard.Pointer) return;   // the game chose the same
                if (guard == null)
                {
                    if (covered == null) return;
                    Uncover(act, target);
                    Hooks.NoteOnce("cover-undone", () => "[cover] the game's knight cover undone: BNW's rules say no");
                    return;
                }
                Cover(act, guard, target);
                if (Has(Conditions(guard), ConditionType.Defend))
                    BattlePlugManager.Instance()?.BattleConditionController?.InterruptRemoveCondition(guard, ConditionType.Defend);
                Hooks.NoteOnce("cover-" + (why.StartsWith("Near") ? "dying" : "stamina"), () => $"[cover] a True Knight covers a {why} (BNW)");
            }
            catch (Exception e) { Hooks.WarnOnce("CoverRules", e); }
        }

        // the game's own cover: what SetProtectData leaves on the act (the probe's self-test logs
        // it); both directions keep the act's targets in step with it
        internal static bool SwapsTargets = true;

        static void Cover(BattleActData act, BattleUnitData guard, BattleUnitData target)
        {
            act.SetProtectData(guard, target, null);
            if (SwapsTargets)
            {
                var t = new Il2CppReferenceArray<BattleUnitData>(1);
                t[0] = guard;
                act.TargetUnitDatas = t;
            }
        }

        static void Uncover(BattleActData act, BattleUnitData target)
        {
            act.ProtectedTarget = null;
            act.ProtectUnitOthers = null;
            var t = new Il2CppReferenceArray<BattleUnitData>(1);
            t[0] = target;
            act.TargetUnitDatas = t;
        }

        // Probe runs only (FormulaProbe.Running: a throwaway battle the game then quits): a
        // front-row member made a knight, another member Near Fatal and then healthy, a monster's
        // Attack on it through the game's TargetFunction.ProtectExtractTarget, with the game's own
        // rules (this postfix off) and BNW's. Null until such a battle.
        internal static string SelfTest()
        {
            if (!Enabled) return "[cover] self-test: off";
            if (!FormulaProbe.Running || GameHarness.Active) return null;   // the harness plays real battles
            var battle = BattlePlugManager.Instance();
            if (battle == null || !battle.IsBattle()) return null;
            var players = battle.GetPlayerUnits();
            var enemies = battle.GetEnemyUnits();
            var cc = battle.BattleConditionController;
            var mm = Last.Data.Master.MasterManager.Instance;
            var fight = mm?.GetData<Last.Data.Master.Command>(1);
            var attack = mm?.GetData<Last.Data.Master.Ability>(1);
            if (players == null || players.Count < 2 || enemies == null || enemies.Count == 0 || cc == null || fight == null || attack == null)
                return null;
            int gi = 1;
            for (int i = 0; i < players.Count; i++)
                if (players[i] != null && BattleUtility.GetCorpsId(players[i]) != CorpsId.Back) { gi = i; break; }
            int ti = gi == 0 ? 1 : 0;
            BattleUnitData target = players[ti], guard = players[gi], enemy = enemies[0];
            var tp = target?.BattleUnitDataInfo?.Parameter;
            if (tp == null || guard == null) return null;

            string Run(bool bnw)
            {
                var abilities = new Il2CppSystem.Collections.Generic.List<Last.Data.Master.Ability>();
                abilities.Add(attack);
                var targets = new Il2CppReferenceArray<BattleUnitData>(1);
                targets[0] = target;
                var act = new BattleActData(enemy, targets, fight, abilities, BattleActData.ActType.Normal,
                    (BattleActData.TargetType)0, BattleActData.ArmType.Right, (BattleActData.ActAbilityType)0, 1, false);
                bool was = Enabled;
                Enabled = bnw;
                BattleActData result;
                try { result = TargetFunction.ProtectExtractTarget(act); }
                finally { Enabled = was; }
                var now = result?.TargetUnitDatas;
                string to = now == null || now.Length == 0 ? "none" : now[0].Pointer == guard.Pointer ? "knight"
                    : now[0].Pointer == target.Pointer ? "target" : "other";
                return $"{(result?.ProtectedTarget != null ? "covered" : "not covered")}, act targets {to}";
            }

            int hpWas = tp.CurrentHP;
            try
            {
                cc.InterruptAddCondition(guard, ConditionType.TipsForKnights);
                bool knight = Knight(guard);
                tp.CurrentHP = Math.Max(1, tp.ConfirmedMaxHp() / 16);
                cc.InterruptAddCondition(target, ConditionType.Dying);
                bool dyingSet = Has(Conditions(target), ConditionType.Dying);
                bool guardBack = BattleUtility.GetCorpsId(guard) == CorpsId.Back;
                bool targetBack = BattleUtility.GetCorpsId(target) == CorpsId.Back;
                bool blocked = Has(Conditions(guard), GuardBlocked);
                string gameDying = Run(false), bnwDying = Run(true);
                bool expected = knight && dyingSet && !guardBack && !blocked;
                cc.InterruptRemoveCondition(target, ConditionType.Dying);
                tp.CurrentHP = hpWas;
                string gameHealthy = Run(false), bnwHealthy = Run(true);
                bool ok = bnwDying.StartsWith(expected ? "covered" : "not covered")
                          && (targetBack || bnwHealthy.StartsWith("not covered"));
                return $"[cover] self-test {(ok ? "ok" : "FAILED")}: knight set {knight}, Near Fatal set {dyingSet}, knight " +
                       $"{(guardBack ? "back" : "front")} row{(blocked ? ", blocked" : "")}, target {(targetBack ? "back" : "front")} row; " +
                       $"Near Fatal target: the game's rules {gameDying}, BNW's {bnwDying} (BNW: {(expected ? "covered" : "not covered")}); " +
                       $"healthy target: the game's {gameHealthy}, BNW's {bnwHealthy}";
            }
            finally
            {
                tp.CurrentHP = hpWas;
                cc.InterruptRemoveCondition(target, ConditionType.Dying);
                cc.InterruptRemoveCondition(guard, ConditionType.TipsForKnights);
            }
        }
    }
}
