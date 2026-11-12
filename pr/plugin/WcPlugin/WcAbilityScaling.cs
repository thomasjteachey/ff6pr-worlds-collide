using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.Battle;
using Last.Data.Master;

namespace WorldsCollide.PR
{
    // WC's ability scaling (-ase / -asr) when a monster acts. WC's enemy scripts cast placeholders
    // its assembly turns into a spell at every cast (data/enemy_script_custom_commands.py):
    //   tier = min(level x 2 / int((factor + 3) x 2), 8)   (8 from level int((factor + 3) x 8) up)
    //          + the placeholder's offset (0-2) + a random -2..+2 (weights 25/64/89/64/14 of 256),
    //          at least 0, at most 11;
    //   spell = any spell of that tier of the placeholder's category (WC data/enemy_ability_tiers.py).
    // Level = the monster's level in the battle (WC's level scaling included: WcScaling).
    // The port compiles placeholder k to ability 7600 + k (ai_compile.SCALED_BASE, a copy of a
    // spell of its category) and the seed carries the tiers as PR abilities (ability_scaling).
    // Every act a monster's AI makes is built by ActSelectEnemy.CreateActData(ability, targets,
    // type); the AI's Act passes no targets and the game picks them there from the ability, so the
    // swap goes in front of it. A counter (CounterAct) passes the attacker unless the ability
    // targets its user (BattleTargetSelectUtility.IsSelf): when the swap changes that, the targets
    // follow the counter's own rule. Until 2026-11-11 the port picked one spell per script from the
    // monster's unscaled level (the user's Sandhorse, ROM level 27, always cast Wind Slash).
    static class WcAbilityScaling
    {
        static readonly Random Rng = new Random();
        static readonly int[] Distortion = { 0x00, 0x19, 0x59, 0xb2, 0xf2 };   // WC's tier_distribution_thresholds
        const int MaxLevelTier = 8, TierCount = 12;
        static PropertyInfo unitOf;
        internal static int Swaps;
        internal static string LastSwap = "";

        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(ActSelectEnemy), "CreateActData");
            if (m == null) throw new MissingMethodException("ActSelectEnemy.CreateActData");
            unitOf = AccessTools.Property(typeof(ActSelect), "BattleUnitData");
            h.Patch(m, prefix: new HarmonyMethod(typeof(WcAbilityScaling), nameof(CreateActData_Prefix)));
            Plugin.L.LogInfo("[wc] ability scaling: monster acts hooked");
        }

        // WC's tier for a monster of this level casting placeholder offset `offset` (roll: 0-255)
        internal static int Tier(int level, int offset, double factor, int roll)
        {
            double perTier = factor + 3;
            int tier = level >= (int)(perTier * MaxLevelTier) ? MaxLevelTier : level * 2 / Math.Max(1, (int)(perTier * 2));
            int x = Distortion.Length - 1;
            while (x > 0 && roll < Distortion[x]) x--;
            int a = Math.Max(tier + offset + x, 2) - 2;
            return Math.Min(a, TierCount - 1);
        }

        // the spell WC casts for placeholder ability `ability`, or 0 (not a placeholder)
        internal static int Pick(WcSeeds.AbilityScaling s, int ability, int level, Func<int, int> next)
        {
            int k = ability - s.Base;
            if (k < 0 || k >= s.Count || k / 3 >= s.Tiers.Length) return 0;
            var tiers = s.Tiers[k / 3];
            if (tiers.Length == 0) return 0;
            var pool = tiers[Math.Min(Tier(level, k % 3, s.Factor, next(256)), tiers.Length - 1)];
            return pool.Length == 0 ? 0 : pool[next(pool.Length)];
        }

        public static void CreateActData_Prefix(ActSelectEnemy __instance, ref int __0,
                                                ref Il2CppReferenceArray<BattleUnitData> __1, BattleActData.ActType __2)
        {
            try
            {
                var s = WcSeeds.Active?.Scaling;
                if (!Plugin.WcMode || s == null || __0 < s.Base || __0 >= s.Base + s.Count) return;
                var unit = unitOf?.GetValue(__instance) as BattleUnitData;
                int level = unit?.BattleUnitDataInfo?.Parameter?.ConfirmedLevel() ?? 1;
                int spell = Pick(s, __0, level, n => Rng.Next(n));
                if (spell == 0) return;
                var mm = MasterManager.Instance;
                var was = mm?.GetData<Ability>(__0);
                var now = mm?.GetData<Ability>(spell);
                if (now == null) return;
                if (was != null && global::Last.Systems.BattleTargetSelectUtility.IsSelf((global::Last.Defaine.Master.AbilityRangeType)was.RengeId) !=
                                   global::Last.Systems.BattleTargetSelectUtility.IsSelf((global::Last.Defaine.Master.AbilityRangeType)now.RengeId))
                {
                    // the counter's rule for the spell it casts: the attacker, or none (the game's pick)
                    var attacker = __2 == BattleActData.ActType.Interrupt ? __instance.GetAttackUnitData() : null;
                    __1 = global::Last.Systems.BattleTargetSelectUtility.IsSelf((global::Last.Defaine.Master.AbilityRangeType)now.RengeId) || attacker == null
                        ? null : new BattleUnitData[] { attacker };
                }
                Swaps++;
                LastSwap = $"monster {unit?.TryCast<BattleEnemyData>()?.GetMonster()?.Id}: Lv {level}, ability {__0} -> {spell}";
                __0 = spell;
            }
            catch (Exception e) { WcRunner.WarnOnce("ability-scaling", e); }
        }
    }
}
