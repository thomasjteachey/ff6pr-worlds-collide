using System;
using System.Globalization;
using System.IO;
using HarmonyLib;
using Last.Data;
using Last.Systems;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // Testing aid for porting BNW's damage formulas: the PR's formulas are compiled code we
    // can't read, so every damage/heal result is written with its inputs to
    // BepInEx/bnw_damage.csv (attacker and target stats, weapon, spell power, rows, result).
    // Fitting those rows shows the PR's actual formula, which the BNW hooks then scale.
    static class DamageLog
    {
        internal static bool Enabled = true;
        const int MaxRows = 4000;
        static StreamWriter file;
        static int rows;

        static readonly string Header = "time,kind,ability,ability_type,command,spirit_ability," +
            "atk,atk_side,atk_lv,atk_vigor,atk_magic,atk_spirit,atk_attack,weapon,weapon_bp,atk_row," +
            "tgt,tgt_side,tgt_lv,tgt_def,tgt_mdef,tgt_spirit,tgt_row,side_attack,spell_power,value,hit,hits,game_value,bnw_factor";

        public static void Apply(Harmony h)
        {
            var calc = typeof(CalcControllerProvider);
            Hooks.Patch(h, calc, "GetFightStatus", typeof(DamageLog), postfix: nameof(Fight_Postfix));
            Hooks.Patch(h, calc, "GetMagicStatus", typeof(DamageLog), postfix: nameof(Magic_Postfix));
            Hooks.Patch(h, calc, "GetRecoveryStatus", typeof(DamageLog), postfix: nameof(Heal_Postfix));
        }

        public static void Fight_Postfix(CalcControllerProvider __instance, ICalcResult __result) => Write(__instance, __result, "fight", -1);
        public static void Magic_Postfix(CalcControllerProvider __instance, int standardValue, ICalcResult __result) => Write(__instance, __result, "magic", standardValue);
        public static void Heal_Postfix(CalcControllerProvider __instance, ICalcResult __result) => Write(__instance, __result, "heal", -1);

        static void Write(CalcControllerProvider c, ICalcResult result, string kind, int spellPower)
        {
            if (!Enabled || rows >= MaxRows || c == null || result == null || !Hooks.IsA<CalcControllerProvider>(c)) return;
            try
            {
                if (file == null) Open();
                var info = c.calcInfo;
                var ability = c.ability;
                var res = result.TryCast<CalcResult>();
                var weapon = info?.userWeapon;
                string Row(CharacterParameterBase p) => p == null ? ",,,,,,," :
                    $"{p.ConfirmedLevel()},{p.ConfirmedPower()},{p.ConfirmedMagic()},{p.ConfirmedVitality()},{p.ConfirmedAttack()}";
                string Target(CharacterParameterBase p) => p == null ? ",,,," :
                    $"{p.ConfirmedLevel()},{p.ConfirmedDefense()},{p.ConfirmedAbilityDefense()},{p.ConfirmedVitality()}";
                int abilityId = ability?.Id ?? 0;
                bool spirit = EsperHooks.D?.SpiritMagicAbilities.Contains(abilityId) ?? false;
                string line = string.Join(",",
                    DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), kind, abilityId, ability?.TypeId ?? 0,
                    info?.command?.Id ?? 0, spirit ? 1 : 0,
                    Csv(c.userCharaData?.Name ?? "enemy"), c.userCharaData != null ? "P" : "E", Row(c.attack),
                    weapon?.Id ?? 0, weapon?.Attack ?? 0, info?.userCorpsId.ToString() ?? "",
                    Csv(c.targetCharaData?.Name ?? (info?.targetMonster != null ? "monster " + info.targetMonster.Id : "enemy")),
                    c.targetCharaData != null ? "P" : "E", Target(c.target), info?.targetCorpsId.ToString() ?? "",
                    info?.isSideAttack == true ? 1 : 0, spellPower,
                    res?.value ?? result.GetValue(false), res?.hitType.ToString() ?? result.GetHitType().ToString(), result.GetHitCount(),
                    kind == "heal" ? "" : BattleFormulas.LastGameValue.ToString(),
                    kind == "heal" ? "" : BattleFormulas.LastFactor.ToString("0.###", CultureInfo.InvariantCulture));
                file.WriteLine(line);
                if (++rows == 1) Plugin.L.LogInfo($"[damage log] writing damage results to {path}");
            }
            catch (Exception e) { Hooks.WarnOnce("DamageLog", e); }
        }

        static string path;

        static void Open()
        {
            path = Path.Combine(BepInEx.Paths.BepInExRootPath, "bnw_damage.csv");
            // a file from an older version with other columns: keep it aside
            if (File.Exists(path))
            {
                string first;
                using (var r = new StreamReader(path)) first = r.ReadLine();
                if (first != Header) File.Move(path, Path.Combine(BepInEx.Paths.BepInExRootPath, $"bnw_damage_{DateTime.Now:yyyyMMdd_HHmmss}.csv"));
            }
            bool fresh = !File.Exists(path);
            file = new StreamWriter(path, append: true) { AutoFlush = true };
            if (fresh) file.WriteLine(Header);
            file.WriteLine($"# session {DateTime.Now:yyyy-MM-dd HH:mm}");
        }

        static string Csv(string s) => (s ?? "").Replace(",", " ");
    }
}
