using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Battle;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW's periodic effects (3.0 tick-calc.asm at C2/0AFF, c0.asm TickLogic, c2.asm:4439):
    //  * amounts, from the unit's own stats, + 2 and capped at 255 like a spell power:
    //      Regen        MaxHP/64 + Stamina*Level/16
    //      Poison, Sap  MaxHP / (Stamina/8 + 16)
    //    (vanilla: MaxHP*Stamina/1024). Monster Stamina is BNW's: max HP high byte + 1, <= 32.
    //  * Poison grows each tick by a counter c (damage x(1 + c/2)): characters c = 0, 2, 4...
    //    up to 31 (x16.5), monsters 0, 2, 5, 11, 23, 31; the counter starts over with the status.
    //  * variance: damage on characters BNW's Stamina-based one, on monsters and heals vanilla's.
    //  * ticks come twice as often as vanilla (Regen/Sap 4 of 8 status steps, Poison 2 of 8).
    // Game side (decompiled): BattleConditionFunction.GetRateValue(typeIndex) gives Poison's
    // (type 1) and Sap's (type 4) tick before elements; Regen keeps its tick in healValue;
    // BattleUtility.CalcATBProgressCycleForVital(unit, 1 Poison / 2 Sap) and
    // RegenerationConditionFunction.CalcGenerateRegeneCycle are the tick intervals.
    static class StatusTicks
    {
        internal static bool Enabled = true;
        static readonly Random Rng = new Random();
        static readonly Dictionary<IntPtr, int> poisonCounter = new Dictionary<IntPtr, int>();

        public static void Apply(Harmony h)
        {
            Hooks.Patch(h, typeof(BattleConditionFunction), "GetRateValue", typeof(StatusTicks), postfix: nameof(Rate_Postfix));
            Hooks.Patch(h, typeof(RegenerationConditionFunction), "GenerateRegeneHeal", typeof(StatusTicks), prefix: nameof(Regen_Prefix));
            Hooks.Patch(h, typeof(RegenerationConditionFunction), "CalcGenerateRegeneCycle", typeof(StatusTicks), postfix: nameof(RegenCycle_Postfix));
            Hooks.Patch(h, typeof(BattleUtility), "CalcATBProgressCycleForVital", typeof(StatusTicks), postfix: nameof(Cycle_Postfix));
        }

        // a new battle starts every counter over
        public static void Track()
        {
            var battle = BattlePlugManager.Instance();
            if ((battle == null || !battle.IsBattle()) && poisonCounter.Count > 0) poisonCounter.Clear();
        }

        static bool Stats(BattleUnitData u, out int maxHp, out int stamina, out int level, out bool player)
        {
            maxHp = stamina = level = 0;
            player = false;
            var p = u?.BattleUnitDataInfo?.Parameter;
            if (p == null) return false;
            maxHp = p.ConfirmedMaxHp();
            level = p.ConfirmedLevel();
            player = u.TryCast<BattlePlayerData>() != null;
            stamina = player ? Math.Clamp(p.ConfirmedVitality(), 0, 255) : BattleFormulas.MonsterStamina(maxHp);
            return maxHp > 0;
        }

        static int VanillaVariance(int d) => d * Rng.Next(224, 256) / 256 + 1;

        public static void Rate_Postfix(BattleConditionFunction __instance, int typeIndex, ref int __result)
        {
            if (!Enabled || __instance == null) return;
            try
            {
                bool poison = typeIndex == 1 && Hooks.IsA<PoisonConditionFunction>(__instance);
                bool sap = typeIndex == 4 && Hooks.IsA<SlipConditionFunction>(__instance);
                if (!poison && !sap) return;
                var unit = __instance.BattleUnitData;
                if (!Stats(unit, out int maxHp, out int stamina, out _, out bool player)) return;
                int d = Math.Min(255, maxHp / (stamina / 8 + 16) + 2);
                int counter = 0;
                if (poison)
                {
                    var key = __instance.Pointer;
                    poisonCounter.TryGetValue(key, out counter);
                    d = d * (2 + counter) / 2;
                    int next = counter == 0 ? 2 : player ? counter + 2 : counter * 2 + 1;
                    poisonCounter[key] = Math.Min(31, next);
                }
                d = player ? d * BattleFormulas.DefensiveVariance(stamina, Rng) / 225 : VanillaVariance(d);
                int was = __result, now = Math.Max(1, d);
                __result = now;
                string what = poison ? "Poison" : "Sap";
                Hooks.NoteOnce("tick-" + what + (player ? "-p" : "-m"), () =>
                    $"[ticks] {what} on a {(player ? "character" : "monster")} (MaxHP {maxHp}, Stamina {stamina}, step {counter}): game {was} -> BNW {now}");
            }
            catch (Exception e) { Hooks.WarnOnce("StatusTicks.Rate", e); }
        }

        public static void Regen_Prefix(RegenerationConditionFunction __instance)
        {
            if (!Enabled || __instance == null) return;
            try
            {
                if (!Stats(__instance.BattleUnitData, out int maxHp, out int stamina, out int level, out bool player)) return;
                int sp = Math.Min(255, maxHp / 64 + stamina * level / 16 + 2);
                int was = __instance.healValue, now = VanillaVariance(sp);
                __instance.healValue = now;
                Hooks.NoteOnce("tick-regen" + (player ? "-p" : "-m"), () =>
                    $"[ticks] Regen (MaxHP {maxHp}, Stamina {stamina}, Lv {level}): game {was} -> BNW {now}");
            }
            catch (Exception e) { Hooks.WarnOnce("StatusTicks.Regen", e); }
        }

        public static void RegenCycle_Postfix(ref float __result)
        {
            if (Enabled && __result > 0f) __result *= 0.5f;
        }

        public static void Cycle_Postfix(int type, ref float __result)
        {
            if (Enabled && (type == 1 || type == 2) && __result > 0f) __result *= 0.5f;
        }
    }
}
