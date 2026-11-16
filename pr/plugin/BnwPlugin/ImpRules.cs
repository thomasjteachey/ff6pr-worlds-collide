using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Last.Battle;
using Last.Defaine;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW's Imp: equipment keeps working while imped (c2.asm 858: vanilla's "Imp equipment"
    // handling skipped), and an imped attacker's damage and healing are halved, except with
    // the Item command (c2.asm 4759 ImpNerf; the halving is in BattleFormulas.StatusFactor).
    // The PR copies vanilla: ParameterProvider's GetArmorDefence / GetOffensivePower /
    // GetWeaponOffensivePower ask BattleUtility.CheckHaveCondition(conditions, 601 "Kappa")
    // and drop every non-Imp weapon/armor (decompiled 2026-11-05, tools/decomp/imp). While
    // those three run, the Imp check answers "no".
    static class ImpRules
    {
        internal static bool Enabled = true;
        [ThreadStatic] static int inEquipment;
        const int Imp = 601;

        public static void Apply(Harmony h)
        {
            if (!Enabled) return;
            var pp = typeof(Serial.FF6.Management.ParameterProvider);
            int n = 0;
            foreach (var m in pp.GetMethods().Where(m => m.DeclaringType == pp &&
                         (m.Name == "GetArmorDefence" || m.Name == "GetOffensivePower" || m.Name == "GetWeaponOffensivePower")))
            {
                try
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(ImpRules), nameof(Enter)),
                               finalizer: new HarmonyMethod(typeof(ImpRules), nameof(Leave)));
                    n++;
                }
                catch (Exception e) { Plugin.L.LogWarning($"[imp] {m.Name}: {e.Message}"); }
            }
            var check = typeof(BattleUtility).GetMethods().Where(m => m.Name == "CheckHaveCondition").ToList();
            // (List<Condition> conditions, ConditionType / int): the overloads the getters call
            bool Takes(System.Reflection.MethodInfo m, Type second)
            {
                var ps = m.GetParameters();
                return ps.Length == 2 && ps[0].ParameterType.Name.StartsWith("List") && ps[1].ParameterType == second;
            }
            var byEnum = check.FirstOrDefault(m => Takes(m, typeof(ConditionType)));
            var byInt = check.FirstOrDefault(m => Takes(m, typeof(int)));
            int c = 0;
            foreach (var (m, post) in new[] { (byEnum, nameof(NotImpEnum)), (byInt, nameof(NotImpInt)) })
            {
                if (m == null) continue;
                var tiny = Hooks.TinyNativeCode(m);
                if (tiny != null) { Plugin.L.LogWarning($"[imp] CheckHaveCondition not patched ({tiny})"); continue; }
                h.Patch(m, postfix: new HarmonyMethod(typeof(ImpRules), post));
                c++;
            }
            Plugin.L.LogInfo($"[imp] equipment rule: {n} getters, {c} condition checks hooked");
        }

        public static void Enter() => inEquipment++;

        public static Exception Leave(Exception __exception)
        {
            inEquipment = Math.Max(0, inEquipment - 1);
            return __exception;
        }

        public static void NotImpEnum(ConditionType checkConditionType, ref bool __result)
        {
            if (__result && inEquipment > 0 && (int)checkConditionType == Imp) Skipped(ref __result);
        }

        public static void NotImpInt(int checkConditionType, ref bool __result)
        {
            if (__result && inEquipment > 0 && checkConditionType == Imp) Skipped(ref __result);
        }

        static void Skipped(ref bool result)
        {
            result = false;
            Hooks.NoteOnce("imp-equipment", () => "[imp] an imped character's equipment counts (BNW)");
        }
    }
}
