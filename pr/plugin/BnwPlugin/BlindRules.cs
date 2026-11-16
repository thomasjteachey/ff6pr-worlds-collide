using System;
using HarmonyLib;
using Last.Battle;
using Last.Data;
using Last.Defaine;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW's Blind (Synchysi's patch, c2.asm 1372, 5384 BlindHelp): a blinded attacker's
    // physical hit rate is 50 (of 255), even for "can't miss" weapons and attacks.
    // The PR (decompiled PhysicalExecution, tools/decomp/physical) takes the hit rate from the
    // attacker's accuracy (ConfirmedAccuracyRate / GetArmConfirmedAccuracyRate, 255 = never
    // misses and skips Blind) and halves it when the attacker is blinded. So in battle a
    // blinded unit's accuracy reads 100: the game's own halving then gives BNW's 50.
    static class BlindRules
    {
        internal static bool Enabled = true;
        const int BlindAccuracy = 100;  // the PR halves it: 50

        public static void Apply(Harmony h)
        {
            if (!Enabled) return;
            Hooks.Patch(h, typeof(PlayerCharacterParameter), "ConfirmedAccuracyRate", typeof(BlindRules), postfix: nameof(Accuracy_Postfix));
            Hooks.Patch(h, typeof(EnemyCharacterParameter), "ConfirmedAccuracyRate", typeof(BlindRules), postfix: nameof(Accuracy_Postfix));
            Hooks.Patch(h, typeof(PlayerCharacterParameter), "GetArmConfirmedAccuracyRate", typeof(BlindRules), postfix: nameof(Accuracy_Postfix));
        }

        public static void Accuracy_Postfix(CharacterParameterBase __instance, ref int __result)
        {
            // BattleFormulas.Damage is off in the probe's raw pass (the game's own Blind)
            if (!Enabled || !BattleFormulas.Damage || __instance == null) return;
            try
            {
                var battle = BattlePlugManager.Instance();
                if (battle == null || !battle.IsBattle()) return;
                var unit = UnitOf(battle, __instance);
                var conditions = unit?.BattleUnitDataInfo?.GetConfirmedConditionType();
                bool enemy = __instance.TryCast<EnemyCharacterParameter>() != null;
                Hooks.NoteOnce($"blind-seen-{enemy}-{unit != null}", () => $"[blind] accuracy read for a {(enemy ? "monster" : "character")}: unit {(unit != null ? "found" : "not found")}");
                if (conditions == null || !conditions.Contains(ConditionType.Blind)) return;
                int was = __result;
                __result = BlindAccuracy;
                Hooks.NoteOnce("blind-hit-" + enemy, () => $"[blind] blinded attacker: accuracy {was} -> {BlindAccuracy} (the game halves it: BNW's 50)");
            }
            catch (Exception e) { Hooks.WarnOnce("BlindRules", e); }
        }

        static BattleUnitData UnitOf(BattlePlugManager battle, CharacterParameterBase p)
        {
            var ptr = p.Pointer;
            foreach (var u in battle.GetPlayerUnits())
                if (u?.BattleUnitDataInfo?.Parameter != null && u.BattleUnitDataInfo.Parameter.Pointer == ptr) return u;
            foreach (var u in battle.GetEnemyUnits())
                if (u?.BattleUnitDataInfo?.Parameter != null && u.BattleUnitDataInfo.Parameter.Pointer == ptr) return u;
            return null;
        }
    }
}
