using System;
using System.Collections.Generic;
using BraveNewWorld.Rules;
using HarmonyLib;
using Last.Battle;
using Last.Battle.Function;
using Last.Data;
using Last.Data.Master;
using Last.Defaine;
using Last.Management;
using Last.Systems;
using FunctionProvider = Serial.FF6.Calc.FunctionProvider;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW command and status rules (c2.asm; decompiled game code in tools/decomp):
    //  * Steal (StealFunction C2/399E): success (Speed*2 + 112)/256, sure at Speed >= 72;
    //    the rare slot when random(256) < Speed, else the common one; an empty slot fails.
    //    The PR: (Lv + 50 - target Lv)/99, rare 12% (CalcExecuteFF6.StealExecution). Applied
    //    on CalcControllerProvider.GetStealStatus: HitType Hit (0) + stealContentId = slot
    //    (1 common, 2 rare), Non (-1) = failed.
    //  * turns (c1.asm HalfTurn, C2/67B0 CheckCantrip): after Defend or Row the gauge starts
    //    half full; after a successful Steal/Mug or a character's Scan it is full (a free
    //    turn). BattleProgressATB.ActExectionEndDelegate zeroes the gauge at act end; the
    //    postfix sets it again.
    //  * Reflect (ReflectClear C2/...): permanent (condition data), but each reflection has an
    //    85/256 chance to remove it, unless it comes from equipment; removed when the act ends.
    //  * Spirit evasion of statuses (StamEvdChk C2/23B2): BNW's Stamina-evadable spells and
    //    monster specials miss a target's status with chance (Stamina+1)/128
    //    (AddConditionFunctionSerial.CheckForceMissCondition).
    static class CommandRules
    {
        internal static bool Steal = true, Turns = true, Reflect = true, SpiritEvade = true, Dance = true;
        static readonly Random Rng = new Random();
        static BnwData D => EsperHooks.D;

        const int Defend = 2, Row = 5, DanceCommand = 22, ScanAbility = 64;
        const int DanceStatus = 51;  // condition 51 (type 619), added by every dance (function 9)
        static readonly Dictionary<IntPtr, int> lastCommand = new Dictionary<IntPtr, int>();
        static readonly HashSet<IntPtr> freeTurn = new HashSet<IntPtr>();
        static readonly List<BattleUnitData> reflectBreaks = new List<BattleUnitData>();

        public static void Apply(Harmony h)
        {
            Hooks.Patch(h, typeof(CalcControllerProvider), "GetStealStatus", typeof(CommandRules), postfix: nameof(Steal_Postfix));
            Hooks.Patch(h, typeof(FunctionProvider), "CreateActFunction", typeof(CommandRules), prefix: nameof(Act_Prefix));
            Hooks.Patch(h, typeof(BattleProgressATB), "ActExectionEndDelegate", typeof(CommandRules), postfix: nameof(ActEnd_Postfix));
            Hooks.Patch(h, typeof(TargetFunction), "OverrideToReflectAct", typeof(CommandRules), postfix: nameof(Reflect_Postfix));
            Hooks.Patch(h, typeof(Serial.FF6.Battle.AddConditionFunctionSerial), "CheckForceMissCondition", typeof(CommandRules),
                postfix: nameof(ForceMiss_Postfix));
            Hooks.Patch(h, typeof(CalcControllerProvider), "GetUniqueStatus", typeof(CommandRules), postfix: nameof(Unique_Postfix));
        }

        // --- Steal --------------------------------------------------------------------------

        public static void Steal_Postfix(CalcControllerProvider __instance, ICalcResult __result)
        {
            if (!Steal || __result == null || !Hooks.IsA<CalcControllerProvider>(__instance)) return;
            try
            {
                if (__instance.userCharaData == null || __instance.attack == null) return;  // monsters steal gil as in vanilla
                var monster = __instance.calcInfo?.targetMonster;
                if (monster == null) return;
                int common = monster.StealContentId1, rare = monster.StealContentId2;
                if (common == 0 && rare == 0) return;  // nothing to steal: the game's own message
                var res = __result.TryCast<CalcResult>();
                if (res == null) return;
                // BNW CoinHelp (c2.asm 2773): Steal goes through hit determination like an attack:
                // hit rate 255, 50 when the thief is blinded (BlindHelp), against the target's
                // Evade: hit if HitRate * (255 - Evade*2 + 1) / 256 > random(0..99)
                var thief = __instance.GetUnitData(__instance.attack);
                var thiefStatus = thief?.BattleUnitDataInfo?.GetConfirmedConditionType();
                bool blind = thiefStatus != null && thiefStatus.Contains(Last.Defaine.ConditionType.Blind);
                int evade = Math.Clamp(__instance.target?.ConfirmedEvasionRate() ?? 0, 0, 128);
                int hitChance = (blind ? 50 : 255) * Math.Max(0, 256 - evade * 2) / 256;
                if (hitChance <= Rng.Next(100))
                {
                    __result.SetStatus(__result.GetValue(false), HitType.Miss, __result.GetHitCount(), __result.GetConditions(), false);
                    res.SetStealContentId(0);
                    Hooks.NoteOnce("steal-miss", () => $"[commands] Steal missed (BNW hit roll: {hitChance}% with Evade {evade}{(blind ? ", blinded" : "")})");
                    return;
                }
                int speed = Math.Clamp(__instance.attack.ConfirmedAgility(), 0, 255);
                int chance = speed * 2 + 112;
                bool success = chance >= 256 || Rng.Next(256) < chance;
                int slot = Rng.Next(256) < speed ? 2 : 1;
                if ((slot == 2 ? rare : common) == 0) success = false;
                var gameHit = __result.GetHitType();
                int gameSlot = res.GetStealContentId();
                __result.SetStatus(__result.GetValue(false), success ? HitType.Hit : HitType.Non, __result.GetHitCount(),
                    __result.GetConditions(), false);
                res.SetStealContentId(success ? slot : 0);
                if (success && thief != null) freeTurn.Add(thief.Pointer);
                Hooks.NoteOnce("steal", () =>
                    $"[commands] Steal (Speed {speed}): game {gameHit}/slot {gameSlot} -> BNW {(success ? (slot == 2 ? "rare" : "common") : "fail")} " +
                    $"(success {Math.Min(256, chance)}/256, rare {speed}/256)");
            }
            catch (Exception e) { Hooks.WarnOnce("CommandRules.Steal", e); }
        }

        // --- turns ---------------------------------------------------------------------------

        public static void Act_Prefix(BattleActData battleActData)
        {
            if ((!Turns && !Dance) || battleActData == null) return;
            try
            {
                var unit = battleActData.AttackUnitData;
                if (unit == null) return;
                var ptr = unit.Pointer;
                freeTurn.Remove(ptr);
                lastCommand[ptr] = battleActData.Command?.Id ?? 0;
                var abilities = battleActData.abilityList;
                if (abilities != null && abilities.Count > 0 && abilities[0]?.Id == ScanAbility && unit.TryCast<BattlePlayerData>() != null)
                    freeTurn.Add(ptr);
            }
            catch (Exception e) { Hooks.WarnOnce("CommandRules.Act", e); }
        }

        public static void ActEnd_Postfix(BattleProgressATB __instance, BattleUnitData battleUnitData, bool isCounter)
        {
            try
            {
                RemoveBrokenReflects();
                if (isCounter || battleUnitData == null || !Hooks.IsA<BattleProgressATB>(__instance)) return;
                var ptr = battleUnitData.Pointer;
                // BNW (3.0 choreography.asm): no Dance status, one dance per command; the
                // dancer stays under control (vanilla/PR: auto-dances until the battle ends)
                if (Dance && lastCommand.TryGetValue(ptr, out int danced) && danced == DanceCommand)
                {
                    BattlePlugManager.Instance()?.BattleConditionController?.InterruptRemoveCondition(battleUnitData, DanceStatus);
                    Hooks.NoteOnce("dance-once", () => "[commands] Dance: one dance per command (Dance status removed)");
                }
                float max = BattleProgressATB.MaxATBGauge;
                if (!Turns) { lastCommand.Remove(ptr); return; }
                if (freeTurn.Remove(ptr))
                {
                    __instance.ChangeATBGaugeByUnitData(battleUnitData, max);
                    Hooks.NoteOnce("turn-free", () => "[commands] free turn after a successful Steal/Mug or Scan: gauge full");
                }
                else if (lastCommand.TryGetValue(ptr, out int cmd) && (cmd == Defend || cmd == Row))
                {
                    __instance.ChangeATBGaugeByUnitData(battleUnitData, max * 126f / 256f);
                    Hooks.NoteOnce("turn-half", () => $"[commands] half turn after {(cmd == Defend ? "Defend" : "Row")}: gauge {max * 126f / 256f:0.#} of {max:0.#}");
                }
                lastCommand.Remove(ptr);
            }
            catch (Exception e) { Hooks.WarnOnce("CommandRules.ActEnd", e); }
        }

        // --- Reflect -------------------------------------------------------------------------

        public static void Reflect_Postfix(BattleActData battleActData, BattleUnitData reflectUser)
        {
            if (!Reflect || reflectUser == null) return;
            try
            {
                if (Rng.Next(256) >= 85 || FromEquipment(reflectUser)) return;
                reflectBreaks.Add(reflectUser);
            }
            catch (Exception e) { Hooks.WarnOnce("CommandRules.Reflect", e); }
        }

        // equipment Reflect is an immunity in BNW (it never breaks); monsters keep theirs too
        static bool FromEquipment(BattleUnitData u)
        {
            var p = u.BattleUnitDataInfo?.Parameter?.TryCast<PlayerCharacterParameter>();
            if (p == null) return true;
            var list = p.GetEquipmentConfirmedConditionList();
            if (list == null) return false;
            foreach (var c in list)
                if (c != null && c.ConditionType == (int)ConditionType.Reflex) return true;
            return false;
        }

        static void RemoveBrokenReflects()
        {
            if (reflectBreaks.Count == 0) return;
            var conditions = BattlePlugManager.Instance()?.BattleConditionController;
            foreach (var u in reflectBreaks)
            {
                try
                {
                    conditions?.InterruptRemoveCondition(u, ConditionType.Reflex);
                    Hooks.NoteOnce("reflect-break", () => "[commands] Reflect broke after reflecting (85/256)");
                }
                catch (Exception e) { Hooks.WarnOnce("CommandRules.ReflectRemove", e); }
            }
            reflectBreaks.Clear();
        }

        // --- unique formulas (ability process_prog) ------------------------------------------
        //  GIL_THROW (GP Rain, CoinToss C2/...): Stamina*10*Level / (2*(targets+1))
        //  CHAKRA (Mantra, C2 3316): (current HP/64 + Level) * Stamina / 4 to each ally
        internal static bool Uniques = true;

        public static void Unique_Postfix(CalcControllerProvider __instance, ICalcResult __result)
        {
            if (!Uniques || __result == null || !Hooks.IsA<CalcControllerProvider>(__instance)) return;
            try
            {
                var prog = __instance.ability?.ProcessProg;
                var a = __instance.attack;
                if (string.IsNullOrEmpty(prog) || a == null) return;
                int lv = a.ConfirmedLevel();
                bool player = a.TryCast<PlayerCharacterParameter>() != null;
                int sta = player ? Math.Clamp(a.ConfirmedVitality(), 0, 255) : BattleFormulas.MonsterStamina(a.ConfirmedMaxHp());
                int value = __result.GetValue(false), now;
                var hit = __result.GetHitType();
                if (prog == "GIL_THROW" && hit == HitType.Hit)
                    now = sta * 10 * lv / (2 * (Math.Max(1, __instance.numTarget) + 1));
                else if (prog == "CHAKRA" && hit == HitType.Recovery)
                    now = (a.CurrentHP / 64 + lv) * sta / 4;
                else return;
                now = Math.Clamp(now, 1, 9999);
                __result.SetValue(now);
                Hooks.NoteOnce("unique-" + prog, () => $"[commands] {prog}: game {value} -> BNW {now} (Lv {lv}, Stamina {sta})");
            }
            catch (Exception e) { Hooks.WarnOnce("CommandRules.Unique", e); }
        }

        // --- Spirit evasion ------------------------------------------------------------------

        public static void ForceMiss_Postfix(CharacterParameterBase attackerParameter, CharacterParameterBase targetParameter,
            Condition condition, ref bool __result)
        {
            if (!SpiritEvade || __result || D == null || targetParameter == null || attackerParameter == null) return;
            try
            {
                var c = BattleControllerManagement.Instance()?.GetCurrentProvider()?.TryCast<CalcControllerProvider>();
                var ability = c?.ability;
                // only for the act being computed (the provider's own attacker and target)
                if (ability == null || c.attack == null || c.target == null ||
                    c.attack.Pointer != attackerParameter.Pointer || c.target.Pointer != targetParameter.Pointer) return;
                if (!D.SpiritEvadeAbilities.Contains(ability.Id)) return;
                bool player = targetParameter.TryCast<PlayerCharacterParameter>() != null;
                int stamina = player ? Math.Clamp(targetParameter.ConfirmedVitality(), 0, 255)
                    : BattleFormulas.MonsterStamina(targetParameter.ConfirmedMaxHp());
                if (Rng.Next(128) > stamina) return;
                __result = true;
                int id = ability.Id;
                Hooks.NoteOnce("spirit-evade", () => $"[commands] Spirit evaded a status (ability {id}, Stamina {stamina}: {stamina + 1}/128)");
            }
            catch (Exception e) { Hooks.WarnOnce("CommandRules.SpiritEvade", e); }
        }
    }
}
