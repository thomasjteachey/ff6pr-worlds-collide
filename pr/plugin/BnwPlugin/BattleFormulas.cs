using System;
using System.Collections.Generic;
using BraveNewWorld.Rules;
using HarmonyLib;
using Last.Battle;
using Last.Data;
using Last.Defaine;
using Last.Systems;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW battle formulas the plugin can apply on top of the PR's own:
    //  * Spirit-based abilities (spell byte 7 bit 2, c2.asm:1448 StamTest): 33 abilities
    //    (Restore, Regen, Rerise, Aurabolt, many Lores/Rages/Dances...) use the caster's
    //    Spirit (PR Vitality) where other spells use Magic. While the PR computes one of
    //    them (CalcControllerProvider.Get*Status with .ability on the list), a player's
    //    ConfirmedMagic answers with ConfirmedVitality;
    //  * ATB speed (c2.asm:397): BNW fills characters' gauges at 75*(Spd+51)/16 and
    //    monsters' at 112.5*(Spd+20)/16, where vanilla used 96*(Spd+20)/16 for both. The
    //    PR's ATBCalc result is scaled by BNW / vanilla (logged, to confirm its shape).
    //  * damage: the PR computes vanilla FF6's formulas (checked against logged fights:
    //    monster and character Fight, Cure, Fire with Shell/Safe x170/256). BNW's changes are
    //    applied as factors on the PR's result (CalcControllerProvider.GetFightStatus /
    //    GetMagicStatus postfix):
    //      - monster special attacks: BNW multiplier / vanilla's (the PR applies vanilla's);
    //      - characters' Fight/Jump: BNW's BP + 2Vig + BP*Lv^2*Vig/6144 over vanilla's
    //        BP + Lv^2*(BP + 2Vig)/256*3/2 (c2.asm:2080; BP = the PR's Attack);
    //      - hits on characters: BNW's defensive variance ((255-S) - rand(0..30-S/4))/225,
    //        S = the target's Vigor (physical) or Spirit (magic), over vanilla's mean
    //        variance 0.9355 (c2.asm:5760);
    //      - characters in the back row take x0.75 from physical hits, not x0.5;
    //        Defend halves magic damage too (c2.asm:626);
    //      - Bushido x1.5 with a grip weapon (RelicRules: the two-handed grip);
    //      - Shock: 3/4 (Level x Stamina + current HP), the user losing max HP / 8 (c2.asm C23EDD);
    //      - the Windslash / Aero procs x1.5 two-handed, else x0.75 (c2.asm Wpn_Chk).
    static class BattleFormulas
    {
        internal static bool Spirit = true, Atb = true, Damage = true;
        static readonly Random Rng = new Random();
        // for the damage log: the PR's value and the factor applied to the last result
        [ThreadStatic] internal static int LastGameValue;
        [ThreadStatic] internal static double LastFactor;
        static BnwData D => EsperHooks.D;
        [ThreadStatic] static int spiritDepth;

        public static void Apply(Harmony h)
        {
            var calc = typeof(CalcControllerProvider);
            foreach (var m in new[] { "GetMagicStatus", "GetRecoveryStatus" })
                Hooks.Patch(h, calc, m, typeof(BattleFormulas), prefix: nameof(MagicCalc_Prefix), postfix: nameof(Calc_Postfix));
            foreach (var m in new[] { "GetFixedBaseValueMagicStatus", "GetFixedBaseValueRecoveryStatus" })
                Hooks.Patch(h, calc, m, typeof(BattleFormulas), prefix: nameof(Calc_Prefix), postfix: nameof(Calc_Postfix));
            Hooks.Patch(h, typeof(PlayerCharacterParameter), "ConfirmedMagic", typeof(BattleFormulas), postfix: nameof(Magic_Postfix));
            Hooks.Patch(h, typeof(EnemyCharacterParameter), "ConfirmedMagic", typeof(BattleFormulas), postfix: nameof(EnemyMagic_Postfix));
            Hooks.Patch(h, typeof(BattleProgressATBFF6), "ATBCalc", typeof(BattleFormulas), postfix: nameof(Atb_Postfix));
            Hooks.Patch(h, calc, "GetFightStatus", typeof(BattleFormulas), postfix: nameof(Fight_Postfix));
            Hooks.Patch(h, calc, "GetMagicStatus", typeof(BattleFormulas), postfix: nameof(MagicDamage_Postfix));
            Hooks.Patch(h, calc, "GetRecoveryStatus", typeof(BattleFormulas), postfix: nameof(Recovery_Postfix));
        }

        // --- damage ------------------------------------------------------------------------

        public static void Fight_Postfix(CalcControllerProvider __instance, ICalcResult __result) => Adjust(__instance, __result, true);
        public static void MagicDamage_Postfix(CalcControllerProvider __instance, int standardValue, ICalcResult __result)
        {
            // the game passes 0 here for spells (damage log); the ability carries the spell power
            spellPower = standardValue > 0 ? standardValue : (__instance?.ability?.StandardValue ?? 0);
            Adjust(__instance, __result, false);
        }

        [ThreadStatic] static int spellPower;
        static readonly HashSet<int> ToolItems = new HashSet<int> { 30, 31, 32, 33, 34, 35, 36, 37 };  // PR item ids

        // monster heals: only the residual of the Magic x2/3 answer (below)
        public static void Recovery_Postfix(CalcControllerProvider __instance, ICalcResult __result)
        {
            if (!Damage || __result == null || !Hooks.IsA<CalcControllerProvider>(__instance)) return;
            try
            {
                if (__result.GetHitType() != HitType.Recovery) { residualPending = false; return; }
                int value = __result.GetValue(false);
                double f = MonsterResidual(__instance, __instance.ability?.StandardValue ?? 0) * StatusFactor(__instance, false, true);
                if (value <= 0 || value >= 9999 || Math.Abs(f - 1.0) < 1e-6) return;
                LastGameValue = value;
                LastFactor = f;
                __result.SetValue(Math.Clamp((int)Math.Round(value * f), 1, 9999));
            }
            catch (Exception e) { Hooks.WarnOnce("BattleFormulas.Recovery", e); }
        }

        // The game multiplies the answered Magic by 1.5 as a fraction (probe: monster Fire with
        // Magic 1 answered as 1 hits like Magic 1.5), so 2/3 can't be exact: the leftover is
        // (4 SP + Lv*want*SP/32) / (4 SP + Lv*1.5*answer*SP/32), within a few percent.
        static double MonsterResidual(CalcControllerProvider c, int sp)
        {
            if (!residualPending) return 1.0;
            residualPending = false;
            if (sp <= 0 || residualAnswer <= 0 || c.attack == null) return 1.0;
            int lv = c.attack.ConfirmedLevel();
            double want = 4.0 * sp + lv * residualWant * sp / 32.0;
            double got = 4.0 * sp + lv * 1.5 * residualAnswer * sp / 32.0;
            return got > 0 ? want / got : 1.0;
        }

        const double VanillaMeanVariance = 0.9355;  // mean of 224..255 / 256
        const int BushidoType = 601;
        const int ShockType = 618;
        static readonly HashSet<int> FightTypes = new HashSet<int> { 4, 15, 16 };  // Attack, Jump, Jump landing

        static void Adjust(CalcControllerProvider c, ICalcResult result, bool physical)
        {
            LastGameValue = 0;
            LastFactor = 1.0;
            if (!Damage || D == null || c == null || result == null || !Hooks.IsA<CalcControllerProvider>(c)) return;
            try
            {
                var hit = result.GetHitType();
                if (hit != HitType.Hit && hit != HitType.Critical) return;
                int value = result.GetValue(false);
                LastGameValue = value;
                if (value <= 0) return;
                double f = 1.0, fVariance = 1.0;
                var ability = c.ability;
                int abilityId = ability?.Id ?? 0;
                bool userIsPlayer = c.userCharaData != null, targetIsPlayer = c.targetCharaData != null;
                if (D.SpecialMultipliers.TryGetValue(abilityId, out var special))
                {
                    f *= special;
                    Note("special", $"monster special {abilityId}: x{special} (BNW over vanilla)");
                }
                if (physical && userIsPlayer && ability != null && FightTypes.Contains(ability.TypeId) && c.attack != null)
                {
                    var a = c.attack;
                    // the game's Fight takes BP per hand (PlayerCharacterParameter.GetArmConfirmedAttack)
                    var pp = a.TryCast<PlayerCharacterParameter>();
                    var arm = c.calcInfo == null || c.calcInfo.isDominantArm ? BattleActData.ArmType.Right : BattleActData.ArmType.Left;
                    int lv = a.ConfirmedLevel(), vig = a.ConfirmedPower();
                    int bp = pp != null ? pp.GetArmConfirmedAttack(arm) : a.ConfirmedAttack();
                    if (bp <= 0) bp = a.ConfirmedAttack();
                    double vanilla = VanillaFight(lv, bp, vig);
                    bool sketch = Sketching(c);
                    int vigBnw = sketch ? BattleMath.SketchVigor(a.ConfirmedMagic()) : vig;
                    double bnw = BnwFight(lv, bp, vigBnw);
                    if (vanilla > 0)
                    {
                        f *= FormulaRatio(c, value, bnw, vanilla);
                        Note(sketch ? "sketch-fight" : "fight", $"{(sketch ? "sketched " : "")}Fight by {c.userCharaData.Name} (Lv {lv}, BP {bp}, " +
                             $"{(sketch ? $"Magic for Vigor {vigBnw}" : $"Vigor {vig}")}): x{bnw / vanilla:0.###} (BNW {bnw:0} / vanilla {vanilla:0})");
                    }
                }
                // Edgar's physical tools (Drill, Chainsaw, Auto Crossbow: PR items 33/35/37): BNW's
                // PlayerPhys with the tool's power as Battle Power. Drill and Chainsaw ignoring
                // Defense (BNW Drill_Saw, c2.asm 2038) is the game's own already
                // (CalcExecuteCorrection.IgnoreDefenseItemIds = [35, 33])
                var tool = c.item?.Item;
                bool isTool = tool != null && ToolItems.Contains(tool.Id);
                if (physical && userIsPlayer && isTool && tool.StandardValue > 0 && c.attack != null)
                {
                    var a = c.attack;
                    int lv = a.ConfirmedLevel(), vig = a.ConfirmedPower(), bp = tool.StandardValue;
                    double vanilla = VanillaFight(lv, bp, vig), bnw = BnwFight(lv, bp, vig);
                    if (vanilla > 0) f *= FormulaRatio(c, value, bnw, vanilla);
                    Note("tool", $"tool {tool.Id} (BP {bp}): x{bnw / Math.Max(1, vanilla):0.###}");
                }
                // physical abilities other than Fight and Bushido (Blitzes, BNW's physical spells, now
                // Physical functions in the port): the game's Fight formula with the spell power as
                // Battle Power (vanilla); BNW's PlayerPhys with the same BP (c2.asm:4893)
                if (physical && userIsPlayer && !isTool && ability != null && !FightTypes.Contains(ability.TypeId) && ability.TypeId != BushidoType
                    && ability.StandardValue > 0 && c.attack != null)
                {
                    var a = c.attack;
                    int lv = a.ConfirmedLevel(), vig = a.ConfirmedPower(), bp = ability.StandardValue;
                    bool sketch = Sketching(c);
                    int vigBnw = sketch ? BattleMath.SketchVigor(a.ConfirmedMagic()) : vig;
                    double vanilla = VanillaFight(lv, bp, vig), bnw = BnwFight(lv, bp, vigBnw);
                    if (vanilla > 0)
                    {
                        f *= FormulaRatio(c, value, bnw, vanilla);
                        Note(sketch ? "sketch-skill" : "phys-skill", $"{(sketch ? "sketched " : "")}physical skill {abilityId} (BP {bp}" +
                             $"{(sketch ? $", Magic for Vigor {vigBnw}" : "")}): x{bnw / vanilla:0.###} (BNW {bnw:0} / vanilla {vanilla:0})");
                    }
                }
                // Bushido (ability type 601): the game uses the skill's spell power as Battle Power
                // (vanilla); BNW's spell power is a multiplier: BP = (right + left weapon + base BP
                // x (2 if dual-wielding)) / 8 x power (c0.asm GetBushPwr, c2.asm:4898), then BNW Fight
                if (physical && userIsPlayer && ability != null && ability.TypeId == BushidoType && c.attack != null)
                {
                    var a = c.attack;
                    int lv = a.ConfirmedLevel(), vig = a.ConfirmedPower(), power = ability.StandardValue;
                    int right = c.calcInfo?.userRightWeapon?.Attack ?? 0, left = c.calcInfo?.userLeftWeapon?.Attack ?? 0;
                    D.BaseBp.TryGetValue(c.userCharaData.Id, out int baseBp);
                    if (right + left == 0) right = Math.Max(0, a.ConfirmedAttack() - baseBp);
                    int bp = (right + left + baseBp * (right > 0 && left > 0 ? 2 : 1)) / 8 * power;
                    // a grip weapon worn (BNW's katanas): x1.5, shield or not (c2.asm C2/185B)
                    bool grip = RelicRules.Enabled && RelicRules.HasGripWeapon(c.userCharaData);
                    if (grip) bp = BattleMath.Grip(bp);
                    double vanilla = VanillaFight(lv, power, vig), bnw = BnwFight(lv, bp, vig);
                    if (vanilla > 0)
                    {
                        f *= FormulaRatio(c, value, bnw, vanilla);
                        Note("bushido" + (grip ? "-grip" : ""), $"Bushido {abilityId} (power {power}, weapons {right}+{left}, base {baseBp}" +
                                                               $"{(grip ? ", grip x1.5" : "")}): BP {bp}, x{bnw / vanilla:0.###}");
                    }
                }
                // Shock (ability type 618: Leo's, and Celes's with Leo's Crest): BNW's 3/4 (Level x
                // Stamina + current HP), halved from the back row, ignoring defense (the port gives
                // the ability content flag group 11), over the game's magic formula (power x 4 +
                // Level x Magic x power / 32). The user then loses max HP / 8 (c2.asm C23EDD; Track)
                if (!physical && userIsPlayer && ability != null && ability.TypeId == ShockType && c.attack != null)
                {
                    var a = c.attack;
                    int lv = a.ConfirmedLevel(), sta = Math.Clamp(a.ConfirmedVitality(), 0, 255), mag = a.ConfirmedMagic();
                    int hp = ShockUserHp(c, a);
                    bool back = c.userCorpsId == Last.Defaine.User.CorpsId.Back;
                    int bnw = Shock(lv, sta, hp, back);
                    int sp = spellPower > 0 ? spellPower : ability.StandardValue;
                    double vanilla = 4.0 * sp + (double)lv * mag * sp / 32;
                    if (vanilla > 0)
                    {
                        f *= FormulaRatio(c, value, bnw, vanilla);
                        Note("shock", $"Shock by {c.userCharaData.Name} (Lv {lv}, Stamina {sta}, HP {hp}{(back ? ", back row" : "")}): " +
                                      $"BNW {bnw} / the game's formula {vanilla:0} (power {sp}, Magic {mag})");
                    }
                }
                // BNW's weapon-flag spells (the Windslash / Aero procs, c2.asm Wpn_Chk): x1.5 when the
                // user grips its weapon with both hands, else x0.75
                if (userIsPlayer && RelicRules.Enabled && D.WeaponFlagSpells.Contains(abilityId))
                {
                    bool grip = RelicRules.Gripping(c.userCharaData);
                    f *= grip ? 1.5 : 0.75;
                    Note("weapon-flags-" + grip, $"weapon-flag spell {abilityId} by {c.userCharaData.Name}: x{(grip ? "1.5 (two-handed)" : "0.75")}");
                }
                if (targetIsPlayer && c.target != null)
                {
                    // physical: Vigor as the SNES stores it (doubled, capped at 255, halved again)
                    int s = physical ? Math.Min(2 * c.target.ConfirmedPower(), 255) / 2 : Math.Clamp(c.target.ConfirmedVitality(), 0, 255);
                    double variance = DefensiveVariance(s, Rng) / 225.0;
                    fVariance = variance / VanillaMeanVariance;
                    f *= fVariance;
                    Note("variance", $"hit on {c.targetCharaData.Name}: {(physical ? "Vigor" : "Spirit")} {s} -> x{variance / VanillaMeanVariance:0.###}");
                    if (physical && c.targetCorpsId == Last.Defaine.User.CorpsId.Back)  // the unit's own row
                    {
                        f *= 1.5;
                        Note("back-row", $"back-row {c.targetCharaData.Name} takes x0.75, not x0.5");
                    }
                    if (!physical && Defending(c))
                    {
                        f *= 0.5;
                        Note("defend", $"{c.targetCharaData.Name} defending: magic damage halved");
                    }
                }
                if (!physical) f *= MonsterResidual(c, spellPower);
                // BNW caps after its variance: a value the game already capped stays capped by it
                if (value >= 9999 && fVariance != 1.0) f /= fVariance;
                f *= StatusFactor(c, physical, false);
                if (Math.Abs(f - 1.0) < 1e-6) return;
                LastFactor = f;
                int now = Math.Clamp((int)Math.Round(value * f), 1, 9999);
                result.SetValue(now);
            }
            catch (Exception e) { Hooks.WarnOnce("BattleFormulas.Damage", e); }
        }

        internal static int Shock(int lv, int sta, int hp, bool backRow) => BattleMath.Shock(lv, sta, hp, backRow);

        // a sketched attack: the PR makes the sketcher (Relm) the attacker of the monster's ability,
        // under the Sketch command; BNW then takes her Magic for Vigor in physical hits (c2.asm
        // SketchMag), her level and Battle Power being the PR's already
        static bool Sketching(CalcControllerProvider c)
        {
            int sketch = D?.SketchCommandId ?? 0;
            return sketch > 0 && c.calcInfo?.command != null && c.calcInfo.command.Id == sketch;
        }

        // Shock's cost to its user: max HP / 8, once per Shock, a frame after its hits are worked
        // out (every target's damage uses the HP before it, as BNW's stored "damage taken" does);
        // a cost that empties the HP KOs the user through the game's condition controller.
        sealed class ShockUse { public int Frame, Hp; public BattleUnitData Unit; public bool Charged; }
        static readonly Dictionary<IntPtr, ShockUse> shocks = new Dictionary<IntPtr, ShockUse>();
        static bool shockPending;

        static int ShockUserHp(CalcControllerProvider c, CharacterParameterBase a)
        {
            var unit = c.GetUnitData(a);
            if (unit == null || FormulaProbe.Active) return a.CurrentHP;   // probe calls: no cost
            int frame = UnityEngine.Time.frameCount;
            if (shocks.TryGetValue(unit.Pointer, out var s) && frame - s.Frame < 30) return s.Hp;
            shocks[unit.Pointer] = new ShockUse { Frame = frame, Hp = a.CurrentHP, Unit = unit };
            shockPending = true;
            return a.CurrentHP;
        }

        internal static void Track()
        {
            if (!shockPending) return;
            try
            {
                int frame = UnityEngine.Time.frameCount;
                bool left = false;
                foreach (var s in shocks.Values)
                {
                    if (s.Charged) continue;
                    if (frame <= s.Frame) { left = true; continue; }
                    s.Charged = true;
                    var p = s.Unit?.BattleUnitDataInfo?.Parameter;
                    if (p == null || p.CurrentHP <= 0) continue;
                    int was = p.CurrentHP, cost = BattleMath.ShockCost(p.ConfirmedMaxHp());
                    int now = Math.Max(0, was - cost);
                    p.CurrentHP = now;
                    if (now == 0)
                        BattlePlugManager.Instance()?.BattleConditionController?.InterruptAddCondition(s.Unit, ConditionType.UnableFight);
                    Hooks.NoteOnce("shock-cost", () => $"[formulas] Shock's cost: user HP {was} -> {now} (max HP / 8 = {cost})");
                }
                shockPending = left;
            }
            catch (Exception e) { shockPending = false; Hooks.WarnOnce("BattleFormulas.ShockCost", e); }
        }

        // vanilla FF6 Fight (the PR's): BP + (Lv * Lv * (BP + Vigor2) / 256) * 3 / 2, Vigor2 = min(2 Vigor, 255)
        internal static double VanillaFight(int lv, int bp, int vigor)
        {
            int vig2 = Math.Min(2 * vigor, 255);
            return bp + (lv * lv * (bp + vig2) / 256) * 3 / 2;
        }

        // BNW PlayerPhys (c2.asm:4893), in the SNES's integer steps: (BP / 16) * (Lv * (Lv * Vigor / 16)) / 24
        // + BP + Vigor2, capped at 30000 when the products overflow
        internal static double BnwFight(int lv, int bp, int vigor)
        {
            int vig2 = Math.Min(2 * vigor, 255), vig = vig2 >> 1;
            long t = (long)lv * ((lv * vig) >> 4);
            if (t > 0xFFFF) return 30000;
            long u = (long)(bp >> 4) * t;
            if ((u >> 16) >= 7) return 30000;
            return Math.Min(30000, u / 24 + bp + vig2);
        }

        // BNW Variance (c2.asm:5761) for hits on characters: (255 - S) - rand(0..30 - S/4); past S = 120
        // the range is negative and the value is (255 - S) - (30 - S/4), no randomness
        internal static int DefensiveVariance(int s, Random rng)
        {
            int range = 30 - s / 4;
            return (255 - s) - (range >= 0 ? rng.Next(range + 1) : range);
        }

        // Morph and character-on-character hits (formula probe 2026-11-05 measured the game's own:
        // Morphed attacker x2 on everything, heals too; Morphed target: magic x0.5, physical x1;
        // a character hitting a character x0.5, heals not). BNW (c2.asm TargetDamageMod,
        // HandleMorph): Morphed attacker +50%; Morphed target x2*(255 - min(Stamina, 96))/256
        // from every attack; character on character x0.25.
        static double StatusFactor(CalcControllerProvider c, bool physical, bool heal)
        {
            double f = 1.0;
            if (HasCondition(c, c.attack, ConditionType.Trance))
            {
                f *= 0.75;
                Note("morph-attacker", "Morphed attacker: x1.5 (BNW) instead of the game's x2");
            }
            // BNW ImpNerf (c2.asm 4759): an imped attacker's damage/healing x0.5, not with items
            if (c.item == null && HasCondition(c, c.attack, ConditionType.Kappa))
            {
                f *= 0.5;
                Note("imp-attacker", "Imped attacker: x0.5 (BNW)");
            }
            if (!heal && c.targetCharaData != null && c.target != null)
            {
                if (HasCondition(c, c.target, ConditionType.Trance))
                {
                    int sta = Math.Min(Math.Clamp(c.target.ConfirmedVitality(), 0, 255), 96);
                    double m = 2.0 * (255 - sta) / 256;
                    f *= physical ? m : m * 2;
                    Note("morph-target", $"Morphed target (Stamina {sta}): x{m:0.##} (BNW)");
                }
                if (c.userCharaData != null)
                {
                    f *= 0.5;
                    Note("party-hit", "character hitting a character: x0.25 (BNW) instead of the game's x0.5");
                }
            }
            return f;
        }

        // BNW / vanilla for a formula, on the game's value. When the game already capped it at
        // 9999 the ratio would scale the cap: estimate the uncapped value instead from the vanilla
        // formula, the mean variance and the target's Defense (unless the ability ignores it).
        static double FormulaRatio(CalcControllerProvider c, int value, double bnw, double vanilla)
        {
            double ratio = bnw / vanilla;
            if (value < 9999 || ratio >= 1) return ratio;
            double def = IgnoresDefense(c.ability) || c.target == null ? 1.0 : (255 - Math.Clamp(c.target.ConfirmedDefense(), 0, 255)) / 256.0;
            double uncapped = vanilla * VanillaMeanVariance * def;
            double want = Math.Min(9999, uncapped * ratio);
            Note("capped", $"game value capped at 9999: uncapped ~{uncapped:0} -> BNW {want:0}");
            return want / value;
        }

        static readonly Dictionary<int, bool> ignoreDefense = new Dictionary<int, bool>();

        // content flag 5 = ignore defense (formula probe 2026-11-05)
        static bool IgnoresDefense(Last.Data.Master.Ability a)
        {
            if (a == null) return false;
            int g = a.ContentFlagGroupId;
            if (g == 0) return false;
            if (ignoreDefense.TryGetValue(g, out bool known)) return known;
            bool found = false;
            var list = Last.Data.Master.MasterManager.Instance?.GetList<Last.Data.Master.ContentFlagGroup>();
            if (list != null)
                foreach (var kv in list)
                    if (kv.Value != null && kv.Value.GroupId == g && kv.Value.ContentFlagId == 5) { found = true; break; }
            ignoreDefense[g] = found;
            return found;
        }

        static bool HasCondition(CalcControllerProvider c, CharacterParameterBase p, ConditionType type)
        {
            if (p == null) return false;
            var list = c.GetUnitData(p)?.BattleUnitDataInfo?.GetConfirmedConditionType();
            return list != null && list.Contains(type);
        }

        static bool Defending(CalcControllerProvider c)
        {
            var conditions = c.GetUnitData(c.target)?.BattleUnitDataInfo?.GetConfirmedConditionType();
            return conditions != null && conditions.Contains(Last.Defaine.ConditionType.Defend);
        }

        static void Note(string key, string text) => Hooks.NoteOnce("dmg-" + key, () => "[formulas] " + text);

        // --- Spirit instead of Magic ------------------------------------------------------

        // D3: the PR multiplies a monster caster's Magic by 1.5 in magic damage and heals, as
        // vanilla did (formula probe 2026-11-05: monster Cure with Magic 1 heals 48-54, Magic
        // x1.5 floored; spell power x1.5 would give 65-74). BNW removed it (c2.asm:2169). While
        // a monster's spell is computed its ConfirmedMagic answers 2/3, so the game's own x1.5
        // lands on BNW's value and the 9999 cap comes after, as in BNW.
        [ThreadStatic] static IntPtr monsterCaster;
        [ThreadStatic] static bool monsterSpirit, residualPending;
        [ThreadStatic] static int residualWant, residualAnswer;

        public static void MagicCalc_Prefix(CalcControllerProvider __instance, ref int __state) => Enter(__instance, ref __state, true);
        public static void Calc_Prefix(CalcControllerProvider __instance, ref int __state) => Enter(__instance, ref __state, false);

        // __state: 1 = Spirit swap on, 2 = monster caster set
        static void Enter(CalcControllerProvider c, ref int state, bool monsterMagic)
        {
            state = 0;
            if (D == null || !Hooks.IsA<CalcControllerProvider>(c)) return;
            try
            {
                int id = c.ability?.Id ?? 0;
                bool spirit = Spirit && id != 0 && D.SpiritMagicAbilities.Contains(id);
                if (spirit) { spiritDepth++; state |= 1; }
                if (monsterMagic && Damage && c.userCharaData == null && c.attack != null && Hooks.IsA<EnemyCharacterParameter>(c.attack))
                {
                    monsterCaster = c.attack.Pointer;
                    monsterSpirit = spirit;
                    residualPending = false;
                    state |= 2;
                }
            }
            catch (Exception e) { Hooks.WarnOnce("BattleFormulas.Calc", e); }
        }

        public static void Calc_Postfix(int __state)
        {
            if ((__state & 1) != 0 && spiritDepth > 0) spiritDepth--;
            if ((__state & 2) != 0) monsterCaster = IntPtr.Zero;
        }

        // BNW monster Stamina (c2.asm:2160 MonsterStamina): max HP high byte + 1, at most 32
        internal static int MonsterStamina(int maxHp)
        {
            int hi = (Math.Min(maxHp, 0xFFFF) >> 8) & 0xFF;
            return hi < 32 ? hi + 1 : 32;
        }

        public static void EnemyMagic_Postfix(EnemyCharacterParameter __instance, ref int __result)
        {
            if (monsterCaster == IntPtr.Zero || __instance == null || __instance.Pointer != monsterCaster) return;
            try
            {
                int want = monsterSpirit ? MonsterStamina(__instance.ConfirmedMaxHp()) : __result;
                int was = __result;
                __result = (want * 2 + 1) / 3;  // the game's x1.5 brings it back to BNW's value
                int now = __result;
                residualWant = want;
                residualAnswer = now;
                residualPending = true;
                Hooks.NoteOnce("monster-magic" + (monsterSpirit ? "-spirit" : ""), () => monsterSpirit
                    ? $"[formulas] monster Spirit ability: Magic {was} -> BNW Stamina {want} (answers {now}; the game adds x1.5)"
                    : $"[formulas] monster spell: Magic {was} answers {now} (the game's x1.5 brings it to {want}, no x1.5 in BNW)");
            }
            catch (Exception e) { Hooks.WarnOnce("BattleFormulas.EnemyMagic", e); }
        }

        // Runs after the esper hooks' own ConfirmedMagic postfix (registered later), so the
        // Spirit value replaces the whole Magic value, EL bonuses included.
        public static void Magic_Postfix(PlayerCharacterParameter __instance, ref int __result)
        {
            if (spiritDepth <= 0 || !Hooks.IsA<PlayerCharacterParameter>(__instance)) return;
            try
            {
                int magic = __result, spirit = __instance.ConfirmedVitality();
                __result = spirit;
                Hooks.NoteOnce("spirit-magic", () => $"[formulas] Spirit-based ability: caster's Magic {magic} -> Spirit {spirit}");
            }
            catch (Exception e) { Hooks.WarnOnce("BattleFormulas.Magic", e); }
        }

        // --- ATB speed -----------------------------------------------------------------------

        public static void Atb_Postfix(BattleProgressATBFF6 __instance, BattleUnitData battleUnitData, ref float __result)
        {
            if (!Atb || battleUnitData == null || __result <= 0f || !Hooks.IsA<BattleProgressATBFF6>(__instance)) return;
            try
            {
                bool player = battleUnitData.TryCast<BattlePlayerData>() != null;
                int speed = battleUnitData.BattleUnitDataInfo?.Parameter?.ConfirmedAgility() ?? 0;
                double ratio = player ? 75.0 * (speed + 51) / (96.0 * (speed + 20)) : 112.5 / 96.0;
                float was = __result;
                __result = (float)(was * ratio);
                float now = __result;
                Hooks.NoteOnce("atb-" + (player ? "player" : "enemy") + "-" + (speed / 10),
                    () => $"[formulas] ATB {(player ? "character" : "monster")} speed {speed}: game {was:0.###} -> BNW {now:0.###} (x{ratio:0.###})");
            }
            catch (Exception e) { Hooks.WarnOnce("BattleFormulas.Atb", e); }
        }
    }
}
