using System;
using System.Collections.Generic;
using BraveNewWorld.Rules;
using HarmonyLib;
using Last.Battle;
using Last.Data;
using Last.Data.User;
using UnityEngine;
using Random = System.Random;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW's rules for relic and weapon effects the game data can't express (BNW asm refs):
    //  * two-handed grip: Battle Power x1.5 (c2.asm 4914 PlayerPhys), not the PR's x2. The PR
    //    multiplies a character's whole attack by EquipmentAbilityData.TwoHandedMagnification (2)
    //    inside PlayerCharacterParameter.ConfirmedAttack / GetArmConfirmedAttack whenever
    //    OwnedCharacterData.HasTwohand: the character has ability 251 and holds one weapon that
    //    isn't on its InvaliteTwoHandeds list with the other hand empty (decompiled
    //    tools/decomp/twohand). In BNW only its 15 grip weapons give 251 (weapon byte 12 bit 3:
    //    the 7 spears, 7 katanas, Excalibur, all two-hand capable, byte 19 bit 6), so the game's
    //    check is BNW's (c2.asm 799-850: no grip beside a shield or a second weapon); only the
    //    factor changes. Bushido gets x1.5 from any grip weapon worn, shield or not
    //    (c2.asm C2/185B BushidoCommand checks only the relic bit: BattleFormulas), and the
    //    Windslash / Aero procs x1.5 when gripped, else x0.75 (c2.asm Wpn_Chk);
    //  * Black Belt counter: chance (Stamina + 32) / 129 (c2.asm 3753-3757), not a flat %;
    //  * Dragon Horn: 25% chance of one extra bounce (c2.asm 1155-1167);
    //  * MP critical hits cost Level / 2 MP, halved by the half-MP relics (c2.asm 3018, 88);
    //  * +25% magic relics don't stack (c2.asm 731);
    //  * an equipped esper's +25% physical / magic bonus (c0.asm 296-330: Alexandr, Ragnarok)
    //    counts like the relic.
    //  * dual wield and Offering are weapon properties in BNW (equip abilities 252/253 from
    //    weapon byte 12 bits 4/0; the PR checks the Genji Glove / Offering accessories, whose
    //    slots BNW turned into consumables): physical damage of Fight-type commands -25% when
    //    holding two weapons and -25% with an Offering weapon (c2.asm 2104-2125, the Genji
    //    effect is on only for weapon/weapon, 799-844), and Offering strikes twice, not 4
    //    times (c2.asm 1099). Through the game's own corrections (types 0/1, applied on
    //    Fight) and its "disorderly shoot" (Offering) attack.
    // Values the PR's code keeps to itself (the boost multipliers, the MP crit cost) are
    // logged the first time, to confirm these hooks read them right.
    static class RelicRules
    {
        internal static bool Enabled = true;
        static BnwData D => EsperHooks.D;
        static readonly Random Rng = new Random();
        const int HalfMpAbility = 291;      // equip ability of the "MP cost / 2" relics (function 62)
        const int OfferingAbility = 253;    // BNW weapon property "Offering" (weapon byte 12 bit 0)
        const int GripAbility = 251;        // the two-handed grip (vanilla's Gauntlet; BNW weapon byte 12 bit 3)
        const float RelicBoost = 0.25f;     // +25% from a physical or magic relic
        static bool constantsSet;
        static float counterGate = 1f;  // the game's own Black Belt chance, when it can't be lifted

        public static void Apply(Harmony h)
        {
            Hooks.Patch(h, typeof(ActUtility), "GetBlackBeltCounterAct", typeof(RelicRules), prefix: nameof(Counter_Prefix));
            Hooks.Patch(h, typeof(Serial.FF6.Management.SerialBattleUtility), "CanContiniusJumpAttack", typeof(RelicRules), postfix: nameof(Jump_Postfix));
            var calc = typeof(CalcExecuteCorrection);
            Hooks.Patch(h, calc, "IsUseMpCriticalOccurrence", typeof(RelicRules), postfix: nameof(MpCrit_Postfix));
            Hooks.Patch(h, calc, "GetAccessoryPhysicalCorrectionValue", typeof(RelicRules), postfix: nameof(Physical_Postfix));
            Hooks.Patch(h, calc, "GetAccessoryMagicCorrectionValue", typeof(RelicRules), postfix: nameof(Magic_Postfix));
            var util = typeof(Serial.FF6.Management.SerialBattleUtility);
            Hooks.Patch(h, util, "CreateContinuousActData", typeof(RelicRules),
                prefix: nameof(Continuous_Prefix), postfix: nameof(Continuous_Postfix));
            Hooks.Patch(h, util, "IsDisorderlyShootAttack", typeof(RelicRules), postfix: nameof(OfferingUnit_Postfix),
                args: new[] { typeof(BattleUnitData) });
            Hooks.Patch(h, util, "IsDisorderlyShootAttack", typeof(RelicRules), postfix: nameof(OfferingAct_Postfix),
                args: new[] { typeof(BattleActData) });
            var pcp = typeof(PlayerCharacterParameter);
            Hooks.Patch(h, pcp, "ConfirmedAttack", typeof(RelicRules), prefix: nameof(Attack_Prefix), postfix: nameof(Attack_Postfix));
            Hooks.Patch(h, pcp, "GetArmConfirmedAttack", typeof(RelicRules), prefix: nameof(Attack_Prefix), postfix: nameof(Attack_Postfix));
            Hooks.Patch(h, typeof(OwnedCharacterData), "get_HasTwohand", typeof(RelicRules), postfix: nameof(HasTwohand_Postfix));
        }

        // --- two-handed grip ---------------------------------------------------------------

        [ThreadStatic] static int attackDepth;
        [ThreadStatic] static bool gripSeen;

        // While the game computes a character's attack its HasTwohand answers no (so no x2), and
        // the attack it then returns gets BNW's x1.5. __state keeps an enclosing call's flag.
        public static void Attack_Prefix(out bool __state)
        {
            __state = gripSeen;
            gripSeen = false;
            attackDepth++;
        }

        public static void Attack_Postfix(ref int __result, bool __state)
        {
            if (attackDepth > 0) attackDepth--;
            bool seen = gripSeen;
            gripSeen = __state;
            if (!seen) return;
            try
            {
                int was = __result;
                __result = BattleMath.Grip(was);   // BNW: BP + BP / 2 (16-bit, past the 255 a hand holds)
                int now = __result;
                Hooks.NoteOnce("grip", () => $"[relics] two-handed grip: attack {was} -> {now} (BNW x1.5, not the game's x2)");
            }
            catch (Exception e) { Hooks.WarnOnce("RelicRules.Grip", e); }
        }

        public static void HasTwohand_Postfix(ref bool __result)
        {
            if (attackDepth <= 0 || !__result || !Enabled || FormulaProbe.Active) return;
            gripSeen = true;
            __result = false;
        }

        // the game's own answer (outside an attack computation), for the proc spells
        internal static bool Gripping(OwnedCharacterData c)
        {
            if (c == null) return false;
            int depth = attackDepth;
            attackDepth = 0;
            try { return c.HasTwohand; }
            finally { attackDepth = depth; }
        }

        // a grip weapon worn (BNW's relic bit, which Bushido checks)
        internal static bool HasGripWeapon(OwnedCharacterData c) => HasAbility(c, GripAbility);

        internal static bool DualWields(OwnedCharacterData c) => DualWielding(c);

        // Once a save is loaded: each character holding a grip weapon, whether the game says it
        // grips it, and its attack with BNW's x1.5 beside the game's own (null until loaded).
        internal static string GripCheck()
        {
            if (!Enabled) return "[grip] check: off";
            var owned = Last.Management.UserDataManager.Instance()?.GetOwnedCharactersClone(false);
            if (owned == null || owned.Count == 0) return null;
            var parts = new List<string>();
            foreach (var c in owned)
            {
                var p = c?.Parameter;
                if (p == null || !HasGripWeapon(c)) continue;
                bool grip = Gripping(c);
                int ours = p.ConfirmedAttack();
                Enabled = false;
                int game;
                try { game = p.ConfirmedAttack(); }
                finally { Enabled = true; }
                parts.Add($"{c.Name} grips {grip}: attack {ours} (the game's own {game})");
            }
            return "[grip] check: " + (parts.Count == 0 ? "nobody holds a grip weapon" : string.Join("; ", parts));
        }

        static bool HasAbility(OwnedCharacterData c, int id)
        {
            var a = c?.EquipmentAbilitys;
            return a != null && a.Contains(id);
        }

        static OwnedCharacterData Owner(BattleUnitData u) => u?.TryCast<BattlePlayerData>()?.ownedCharacterData;

        // two weapons in hand, as the game itself decides it for its Genji Glove correction
        static bool DualWielding(OwnedCharacterData c)
        {
            var util = Last.Management.BattleProviderManager.Instance?.GetSerialBattleUtility();
            return c != null && util != null && util.IsSecondlyAttack(c);
        }

        // BNW's Offering weapons make the attack an Offering ("disorderly shoot") attack
        public static void OfferingUnit_Postfix(BattleUnitData battleUnitData, ref bool __result)
        {
            if (__result || !Enabled || FormulaProbe.Active) return;
            try { if (HasAbility(Owner(battleUnitData), OfferingAbility)) __result = true; }
            catch (Exception e) { Hooks.WarnOnce("RelicRules.Offering", e); }
        }

        public static void OfferingAct_Postfix(BattleActData battleActData, ref bool __result)
        {
            if (__result || !Enabled || FormulaProbe.Active || battleActData == null) return;
            try { if (HasAbility(Owner(battleActData.AttackUnitData), OfferingAbility)) __result = true; }
            catch (Exception e) { Hooks.WarnOnce("RelicRules.OfferingAct", e); }
        }

        // probe runs: each party member's hands and what the physical corrections give them
        // (with BNW's rules on), so the dual wield / Offering paths are seen to work
        internal static void LogParty(BattlePlugManager battle)
        {
            try
            {
                foreach (var p in battle.GetPlayerUnits())
                {
                    var c = Owner(p);
                    if (c == null) continue;
                    var abilities = c.EquipmentAbilitys;
                    var vals = new List<string>();
                    foreach (var t in new[] { CalcExecuteCorrection.PhysicalType.PilgrimsPassport, CalcExecuteCorrection.PhysicalType.GenchisBasho,
                                              CalcExecuteCorrection.PhysicalType.BasketsOfGiants, CalcExecuteCorrection.PhysicalType.Gauntlet })
                        vals.Add($"{t} {CalcExecuteCorrection.GetAccessoryPhysicalCorrectionValue(t, c)}");
                    var util = Last.Management.BattleProviderManager.Instance?.GetSerialBattleUtility();
                    Plugin.L.LogInfo($"[relics] {c.Name}: dual wielding {DualWielding(c)}, offering {util?.IsDisorderlyShootAttack(p)}, " +
                                     $"abilities 251/252/253 {HasAbility(c, 251)}/{HasAbility(c, 252)}/{HasAbility(c, OfferingAbility)}; " +
                                     string.Join(", ", vals));
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[relics] party check: {e}"); }
        }

        // BNW: Offering strikes twice (c2.asm C21624: 4 -> 2), with or without a second weapon
        public static void Continuous_Prefix(BattleActData battleActData, bool isDisorderlyShootAttack)
        {
            if (!Enabled || battleActData == null || !isDisorderlyShootAttack) return;
            try
            {
                int n = battleActData.continuousCount;
                if (n <= 2) return;
                battleActData.continuousCount = 2;
                Hooks.NoteOnce("offering-hits", () => $"[relics] Offering: {n} strikes -> 2 (BNW)");
            }
            catch (Exception e) { Hooks.WarnOnce("RelicRules.OfferingHits", e); }
        }

        // Diagnostics for Offering and dual wield ("secondly attack"): how the game counts the
        // extra hits.
        public static void Continuous_Postfix(BattleActData battleActData, bool isSecondlyAttack, bool isDisorderlyShootAttack)
        {
            if (!Enabled || battleActData == null || !(isSecondlyAttack || isDisorderlyShootAttack)) return;
            try
            {
                string kind = isDisorderlyShootAttack ? "offering" : "dual-wield";
                int count = battleActData.continuousCount, commands = battleActData.continuousCommandCount;
                var fight = battleActData.ContinuousFight;
                Hooks.NoteOnce("continuous-" + kind, () => $"[relics] {kind} attack built: continuousCount {count}, " +
                                                              $"continuousCommandCount {commands}, ContinuousFight {fight}");
            }
            catch (Exception e) { Hooks.WarnOnce("RelicRules.Continuous", e); }
        }

        // Static tuning values; called every few seconds until set (classes initialise late).
        internal static void SetConstants()
        {
            if (!Enabled || constantsSet) return;
            try
            {
                // the two-handed multiplier lives on the game's equipment-ability data object
                // (2: the attack doubled; the grip hooks above apply BNW's x1.5 instead)
                var equip = Serial.ProviderManager.Instance?.EquipmentAbilityProvider?.serialData
                    ?.TryCast<Serial.FF6.Management.EquipmentAbilityData>();
                if (equip == null) return;  // not created yet: try again later
                HardItems.InitClass(typeof(BattlePassiveConstants));
                int twoHand = equip.TwoHandedMagnification;
                int counter = BattlePassiveConstants.BlackBeltCounterPercentage;
                // the BNW roll happens in Counter_Prefix; the game's own gate must always pass
                // (only if it's a real field: writing a game constant crashes the game)
                var problem = HardItems.WriteProblem(typeof(BattlePassiveConstants), nameof(BattlePassiveConstants.BlackBeltCounterPercentage));
                if (problem == null) BattlePassiveConstants.BlackBeltCounterPercentage = 100;
                else
                {
                    // the game rolls its own counter% too: ask ours for BNW's chance divided by it
                    counterGate = Math.Max(0.01f, counter / 100f);
                    Plugin.L.LogInfo($"[relics] Black Belt counter % is {problem} in the game ({counter}%): BNW's chance is divided by it, so up to {counter}%");
                }
                constantsSet = true;
                Plugin.L.LogInfo($"[relics] two-handed grip: the game's x{twoHand} -> BNW's x1.5 (grip hooks)" +
                                 $"; Black Belt counter {counter}% -> BNW's (Stamina + 32) / 129");
            }
            catch (Exception e) { constantsSet = true; Hooks.WarnOnce("RelicRules.Constants", e); }
        }

        // --- Black Belt counter ------------------------------------------------------------

        public static bool Counter_Prefix(BattleUnitData attack, BattleUnitData target, ref BattleActData __result)
        {
            if (!Enabled || !constantsSet || target == null) return true;
            try
            {
                int stamina = target.BattleUnitDataInfo?.Parameter?.ConfirmedVitality() ?? 0;
                double chance = Math.Min(1.0, (stamina + 32) / 129.0 / counterGate);
                bool counters = Rng.NextDouble() < chance;
                Hooks.NoteOnce("counter", () => $"[relics] first Black Belt counter check: Stamina {stamina} -> {(stamina + 32) * 100 / 129}% ({(counters ? "counters" : "no counter")})");
                if (counters) return true;
                __result = null;
                return false;
            }
            catch (Exception e) { Hooks.WarnOnce("RelicRules.Counter", e); return true; }
        }

        // --- Dragon Horn -------------------------------------------------------------------

        static readonly Dictionary<IntPtr, (float At, int Bounces)> jumps = new Dictionary<IntPtr, (float, int)>();

        // The game keys Dragon Horn on an item id (a constant) that BNW gave to another item,
        // so the check is redone on BNW's Dragon Horn items (Dragon Helm).
        public static void Jump_Postfix(BattleUnitData battleUnitData, ref bool __result)
        {
            if (!Enabled || battleUnitData == null) return;
            try
            {
                var horn = D?.HardItems.Find(h => h.Member == "ArmorIdHornOfDragon");
                if (horn != null)
                {
                    if (!HardItems.Wears(battleUnitData.GetOwnedCharacterData(), horn.Bnw)) { __result = false; return; }
                }
                else if (!__result) return;
                float now = Time.unscaledTime;
                var key = battleUnitData.Pointer;
                // a new jump if this unit's last bounce check was a while ago
                if (!jumps.TryGetValue(key, out var j) || now - j.At > 4f) j = (now, 0);
                bool bounce = j.Bounces == 0 && Rng.Next(4) == 0;
                jumps[key] = (now, j.Bounces + 1);
                Hooks.NoteOnce("dragon-horn", () => $"[relics] first Dragon Horn check: {(bounce ? "one extra bounce" : "no extra bounce")} (BNW: 25%, once)");
                __result = bounce;
            }
            catch (Exception e) { Hooks.WarnOnce("RelicRules.Jump", e); }
        }

        // --- MP critical hits --------------------------------------------------------------

        public static void MpCrit_Postfix(OwnedCharacterData ownedCharacterData, ref int useMp, bool __result)
        {
            if (!Enabled || !__result || ownedCharacterData == null) return;
            try
            {
                int level = ownedCharacterData.Parameter?.ConfirmedLevel() ?? 1;
                int cost = Math.Max(1, level / 2);
                var abilities = ownedCharacterData.EquipmentAbilitys;
                if (abilities != null && abilities.Contains(HalfMpAbility)) cost = Math.Max(1, cost / 2);
                int was = useMp;
                useMp = cost;
                Hooks.NoteOnce("mp-crit", () => $"[relics] first MP critical: cost {was} -> {cost} (level {level})");
            }
            catch (Exception e) { Hooks.WarnOnce("RelicRules.MpCrit", e); }
        }

        // --- +25% physical / magic -----------------------------------------------------------

        // The game's value is a multiplier (1 = none) or an added fraction (0 = none); learn
        // which from what it returns, and add one relic's worth.
        static float WithOneBoost(float value) => value >= 0.999f ? Math.Max(value, 1f + RelicBoost) : Math.Max(value, RelicBoost);

        static Esper EquippedEsper(OwnedCharacterData c) => c == null ? null : D?.EsperByContent(c.MagicStoneId);

        public static void Physical_Postfix(CalcExecuteCorrection.PhysicalType physicalType, OwnedCharacterData ownedCharacterData, ref float __result)
        {
            if (FormulaProbe.Active) { __result = 0f; return; }  // probe: no relic boosts
            if (!Enabled || ownedCharacterData == null) return;
            try
            {
                float was = __result;
                // what the game returns per kind (0 Offering, 1 Genji, 2 Atlas Armlet, 3 Gauntlet)
                // and which of BNW's weapon abilities the wearer has: for porting Offering/dual
                // wield/two-hand damage (their relic slots are dead in BNW)
                var abilities = ownedCharacterData.EquipmentAbilitys;
                string has = abilities == null ? "?" : $"two-hand {abilities.Contains(251)}, dual {abilities.Contains(252)}, offering {abilities.Contains(253)}";
                Hooks.NoteOnce("phys-boost-" + physicalType, () => $"[relics] physical correction {physicalType}: game value {was} for {ownedCharacterData.Name} ({has})");
                // BNW's -25%s: the game multiplies Fight damage by these when they're above 0
                if (physicalType == CalcExecuteCorrection.PhysicalType.PilgrimsPassport && __result <= 0f
                    && HasAbility(ownedCharacterData, OfferingAbility))
                    __result = 0.75f;
                else if (physicalType == CalcExecuteCorrection.PhysicalType.GenchisBasho && __result <= 0f
                         && DualWielding(ownedCharacterData))
                    __result = 0.75f;
                float now = __result;
                if (now != was)
                    Hooks.NoteOnce("phys-bnw-" + physicalType, () => $"[relics] BNW {physicalType}: {was} -> {now} for {ownedCharacterData.Name}");
                if (physicalType != CalcExecuteCorrection.PhysicalType.BasketsOfGiants) return;
                var esper = EquippedEsper(ownedCharacterData);
                if (esper != null && esper.Equip.PhysicalPct > 0) __result = WithOneBoost(__result);
            }
            catch (Exception e) { Hooks.WarnOnce("RelicRules.Physical", e); }
        }

        public static void Magic_Postfix(OwnedCharacterData ownedCharacterData, ref int equipNum, ref float __result)
        {
            if (FormulaProbe.Active) { __result = 0f; return; }  // probe: no relic boosts
            if (!Enabled || ownedCharacterData == null) return;
            try
            {
                float was = __result;
                int n = equipNum;
                Hooks.NoteOnce("magic-boost-" + Math.Min(n, 2), () => $"[relics] magic boost value seen: {was} with {n} relic(s) ({ownedCharacterData.Name})");
                if (equipNum > 1)
                {
                    // BNW: two +25% magic relics still give +25%
                    __result = __result >= 0.999f ? 1f + (__result - 1f) / equipNum : __result / equipNum;
                    equipNum = 1;
                }
                var esper = EquippedEsper(ownedCharacterData);
                if (esper != null && esper.Equip.MagicPct > 0 && equipNum == 0)
                {
                    __result = WithOneBoost(__result);
                    equipNum = 1;
                }
            }
            catch (Exception e) { Hooks.WarnOnce("RelicRules.Magic", e); }
        }
    }
}
