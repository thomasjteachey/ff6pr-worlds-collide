using System;
using System.Linq;
using HarmonyLib;

namespace WorldsCollide.PR
{
    // WC's -pd (permadeath): a fallen character stays fallen. WC's own data does the rest (no Life
    // spells to learn, no Fenix Downs to buy or find; the port carries both). The field's revivals
    // are skipped for a KO'd character (0 HP): inns, tents and story heals
    // (SystemCall.Current.FullRecoveryHpAndMp / RemoveAllCondititons) and the scripts' recovery
    // checks (External.Character.CanRecovery).
    static class WcChallenges
    {
        internal static string LastKept = "";

        public static void Patch(Harmony h)
        {
            var cur = typeof(global::Last.Interpreter.Instructions.SystemCall.Current);
            int n = 0;
            foreach (var name in new[] { "FullRecoveryHpAndMp", "RemoveAllCondititons" })
            {
                var m = AccessTools.Method(cur, name);
                if (m == null) continue;
                h.Patch(m, prefix: new HarmonyMethod(typeof(WcChallenges), nameof(Recovery_Prefix)));
                n++;
            }
            var can = AccessTools.Method(typeof(global::Last.Interpreter.Instructions.External.Character), "CanRecovery");
            if (can != null) { h.Patch(can, postfix: new HarmonyMethod(typeof(WcChallenges), nameof(CanRecovery_Postfix))); n++; }
            Plugin.L.LogInfo($"[wc] permadeath: {n} of 3 recovery hooks");

            // -scis: Sketch's hit roll (UniqueUpdateParameterProvider.HitCalc, key ff6_ability_sketch:
            // CommonUtility.GetRandPlus(100) < (caster - target level + 20) x 5, decompiled
            // tools/decomp/wc) always hits. HitCalc itself can't be patched (its ref Dictionary
            // parameters make the patched method an invalid program, and it runs every unique command:
            // Runic, Slot, Steal...), so the hook is IsResistanceSketch, which only Sketch calls, just
            // before that roll; the roll's hook goes in once a seed with -scis is played (GetRandPlus
            // runs for every random number in the game)
            harmony = h;
            var resist = AccessTools.Method(typeof(Serial.FF6.Calc.UniqueUpdateParameterProvider), "IsResistanceSketch");
            if (resist != null) h.Patch(resist, postfix: new HarmonyMethod(typeof(WcChallenges), nameof(Resist_Postfix)));
            // -csb: the cursed shield's count (DataStorage category 8, index 9; uncursed at 256:
            // SystemCall.Current.CursedShield, decompiled tools/decomp/wc)
            // -saw: the Atma Weapon's term divides by 32, not 64 (the PR's CalcExecuteFF6.PhysicalExecution:
            // ((T4 x T3 / 256) >> 6) + 1, decompiled tools/decomp/physical): the fight result of a
            // hand holding it (CalcControllerProvider.GetFightStatus, a CalcResult) doubles
            var fight = AccessTools.Method(typeof(global::Last.Systems.CalcControllerProvider), "GetFightStatus");
            if (fight != null) h.Patch(fight, postfix: new HarmonyMethod(typeof(WcChallenges), nameof(Fight_Postfix)));
            var shield = AccessTools.Method(typeof(global::Last.Interpreter.Instructions.SystemCall.Current), "CursedShield");
            if (shield != null) h.Patch(shield, prefix: new HarmonyMethod(typeof(WcChallenges), nameof(CursedShield_Prefix)));
        }

        static int sketchFrame = -1;
        internal static int SketchesForced;
        static Harmony harmony;
        static bool randPatched;

        static bool Flag(string f) => Plugin.WcMode && WcSeeds.Active != null && WcSeeds.Active.Flags.Split(' ').Contains(f);

        // the roll's hook, once: a seed with -scis is active (WcSeeds.Apply / startup)
        internal static void EnsureSketchRoll()
        {
            if (randPatched || harmony == null || !Flag("-scis")) return;
            randPatched = true;
            try
            {
                var rand = AccessTools.Method(typeof(global::CommonUtility), "GetRandPlus", new[] { typeof(int) });
                if (rand != null) harmony.Patch(rand, prefix: new HarmonyMethod(typeof(WcChallenges), nameof(Rand_Prefix)));
                Plugin.L.LogInfo("[wc] -scis: Sketch's hit roll hooked");
            }
            catch (Exception e) { WcRunner.WarnOnce("sketch-roll", e); }
        }

        // not resistant: the next 0-99 roll in this frame is Sketch's hit
        public static void Resist_Postfix(bool __result)
        {
            if (!__result && Flag("-scis")) { EnsureSketchRoll(); sketchFrame = UnityEngine.Time.frameCount; }
        }

        public static bool Rand_Prefix(int max, ref int __result)
        {
            if (sketchFrame < 0 || max != 100) return true;
            bool now = sketchFrame == UnityEngine.Time.frameCount;
            sketchFrame = -1;
            if (!now) return true;
            SketchesForced++;
            __result = 0;
            return false;
        }

        internal const int AtmaWeapon = 30;   // PR weapon id (SNES item 28)
        internal static bool? SawOverride;     // tests: force -saw on / off
        internal static string LastAtma = "";

        public static void Fight_Postfix(global::Last.Systems.CalcControllerProvider __instance, global::Last.Systems.ICalcResult __result)
        {
            try
            {
                if (__result == null || !(SawOverride ?? Flag("-saw"))) return;
                var info = __instance.GetCalcInfo();
                if (info?.userWeapon == null || info.userWeapon.Id != AtmaWeapon || info.userCharaData == null) return;
                if (__result.GetHitType() != global::Last.Systems.HitType.Hit) return;
                int v = __result.GetValue(false);
                if (v <= 0) return;
                int doubled = Math.Min(9999, v * 2 - 1);
                __result.SetValue(doubled);
                LastAtma = $"Atma Weapon {v} -> {doubled}";
            }
            catch (Exception e) { WcRunner.WarnOnce("atma", e); }
        }

        internal const int ShieldCategory = 8, ShieldCount = 9, ShieldUncurse = 256;

        public static void CursedShield_Prefix()
        {
            try
            {
                var s = WcSeeds.Active;
                if (!Plugin.WcMode || s == null || s.CursedShieldBattles >= ShieldUncurse || s.CursedShieldBattles <= 0) return;
                var ds = global::Last.Interpreter.DataStorage.Instance;
                var cat = (global::Last.Interpreter.DataStorage.Category)ShieldCategory;
                int count = ds.Get(cat, ShieldCount);
                if (count + 1 >= s.CursedShieldBattles && count + 1 < ShieldUncurse)
                {
                    ds.Set(cat, ShieldCount, ShieldUncurse - 1);   // this battle's count reaches 256
                    Plugin.L.LogInfo($"[wc] -csb: the cursed shield uncurses after {s.CursedShieldBattles} battles");
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("cursed-shield", e); }
        }

        static bool On() => Plugin.WcMode && WcSeeds.Active != null && WcSeeds.Active.Flags.Split(' ').Contains("-pd") &&
                            WcEvents.Flag(global::Last.Interpreter.DataStorage.Category.kScenarioFlag4, WcEvents.WcGameFlag) == 1;

        // a fallen character by character status id (or owned id)
        internal static bool Fallen(int id)
        {
            var owned = global::Last.Management.UserDataManager.Instance()?.OwnedCharacterList;
            if (owned == null) return false;
            foreach (var c in owned)
                if (c != null && (c.characterStatusId == id || c.Id == id) && c.characterStatusId >= 1 && c.characterStatusId <= 14)
                    return c.Parameter != null && c.Parameter.CurrentHP <= 0;
            return false;
        }

        public static bool Recovery_Prefix(int characterStatusId)
        {
            try
            {
                if (!On() || !Fallen(characterStatusId)) return true;
                LastKept = $"character {characterStatusId} stays fallen (-pd)";
                Plugin.L.LogInfo("[wc] " + LastKept);
                return false;
            }
            catch (Exception e) { WcRunner.WarnOnce("permadeath", e); return true; }
        }

        public static void CanRecovery_Postfix(int characterId, ref bool __result)
        {
            try { if (__result && On() && Fallen(characterId)) __result = false; }
            catch (Exception e) { WcRunner.WarnOnce("permadeath-can", e); }
        }
    }
}
