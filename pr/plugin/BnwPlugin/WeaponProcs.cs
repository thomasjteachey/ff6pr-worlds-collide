using System;
using System.Linq;
using BraveNewWorld.Rules;
using HarmonyLib;
using Last.Battle;
using FunctionProvider = Serial.FF6.Calc.FunctionProvider;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // Weapon spell procs, BNW style (bnw 3.0 item.asm RandomCast). Which spell each weapon
    // casts, and its chance without help, is data the porter writes (battle_extra_data
    // add_ability_weapon). What the data can't say: a "High Proc Rate" relic raises the
    // wearer's chances (1 in 4 -> 1 in 2, brushes 1 in 2 -> 3 in 4). Before each action is
    // built, every proc entry gets the acting character's chance (actions run one at a time).
    static class WeaponProcs
    {
        internal static bool Enabled = true;
        static BnwData D => EsperHooks.D;

        public static void Apply(Harmony h)
        {
            Hooks.Patch(h, typeof(FunctionProvider), "CreateActFunction", typeof(WeaponProcs), prefix: nameof(Create_Prefix));
            Hooks.Patch(h, typeof(BattleActExection), "SetBattleActData", typeof(WeaponProcs), prefix: nameof(Set_Prefix),
                args: new[] { typeof(BattleActData), typeof(BattleActData), typeof(bool) });
        }

        public static void Create_Prefix(FunctionProvider __instance, BattleActData battleActData)
        {
            if (!Enabled || D == null || battleActData == null || D.Procs.Weapons.Count == 0 || !Hooks.IsA<FunctionProvider>(__instance)) return;
            try
            {
                var list = BattlePlugManager.Instance()?.BattleExtraDescController?.desc?.add_ability_weapon;
                if (list == null) return;
                bool high = HasHighRateRelic(battleActData.AttackUnitData);
                foreach (var e in list)
                {
                    if (e == null) continue;
                    int rate = D.Procs.RateFor(e.weapon_id, high);
                    if (rate >= 0 && e.ability_per != rate) e.ability_per = rate;
                }
                if (high) Hooks.NoteOnce("proc-high", () => $"[procs] High Proc Rate relic on {Who(battleActData.AttackUnitData)}: raised proc chances");
            }
            catch (Exception e) { Hooks.WarnOnce("WeaponProcs.Rate", e); }
        }

        static bool HasHighRateRelic(BattleUnitData unit)
        {
            var ids = D.Procs.HighRateContentIds;
            if (unit == null || ids.Length == 0) return false;
            var equipment = unit.GetOwnedCharacterData()?.EquipmentData;
            if (equipment == null) return false;
            foreach (var item in equipment.Values)
                if (item != null && ids.Contains(item.ContentId)) return true;
            return false;
        }

        static string Who(BattleUnitData unit)
        {
            try { return unit?.GetOwnedCharacterData()?.Name ?? "an enemy"; }
            catch { return "?"; }
        }

        // Logging only: the first proc of a session, to confirm the data is live.
        public static void Set_Prefix(BattleActExection __instance, BattleActData baseActData, BattleActData addActData, bool isAddAbilityByWeapon)
        {
            if (!isAddAbilityByWeapon || addActData == null || !Hooks.IsA<BattleActExection>(__instance)) return;
            try
            {
                var abilities = addActData.abilityList;
                int id = abilities != null && abilities.Count > 0 && abilities[0] != null ? abilities[0].Id : 0;
                var user = addActData.AttackUnitData ?? baseActData?.AttackUnitData;
                Hooks.NoteOnce("proc-fired", () => $"[procs] first proc this session: {Who(user)} cast ability {id}");
            }
            catch (Exception e) { Hooks.WarnOnce("WeaponProcs.Log", e); }
        }
    }
}
