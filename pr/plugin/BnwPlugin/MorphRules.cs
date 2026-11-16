using System;
using HarmonyLib;
using Last.Battle;
using Last.Data.Master;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW's Morph has no gauge (c2.asm 308 "always available now", 450 and 894 skip the morph
    // gauge): once Morphed, Terra stays Morphed until she Reverts or the battle ends, and the
    // command can always be used. The PR (decompiled tools/decomp/morph): the Morph command
    // (id 8) needs MagicLearningValue > 7 (CommandAvailableData.IsAvailable), and
    // TranceConditionFunction drains a gauge (MagicLearningValue x 0.5 or x 1) by time unless
    // its isPermanent flag is set (then gauge 100, no drain). Both are switched to BNW's.
    // (Morph's damage multipliers are in BattleFormulas.StatusFactor.)
    static class MorphRules
    {
        internal static bool Enabled = true;
        const int MorphCommand = 8;

        public static void Apply(Harmony h)
        {
            if (!Enabled) return;
            Hooks.Patch(h, typeof(TranceConditionFunction), "Start", typeof(MorphRules), prefix: nameof(Start_Prefix));
            Hooks.Patch(h, typeof(Serial.FF6.Management.CommandAvailableData), "IsAvailable", typeof(MorphRules), postfix: nameof(Available_Postfix));
        }

        public static void Start_Prefix(TranceConditionFunction __instance)
        {
            if (!Enabled || __instance == null) return;
            try
            {
                __instance.isPermanent = true;
                Hooks.NoteOnce("morph-permanent", () => "[morph] Morph without a gauge: lasts until Revert or the battle's end (BNW)");
            }
            catch (Exception e) { Hooks.WarnOnce("MorphRules.Start", e); }
        }

        public static void Available_Postfix(Command command, ref bool __result)
        {
            if (!Enabled || __result || command == null) return;
            try
            {
                if (command.Id == MorphCommand) __result = true;
            }
            catch (Exception e) { Hooks.WarnOnce("MorphRules.Available", e); }
        }
    }
}
