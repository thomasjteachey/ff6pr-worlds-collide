using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using BraveNewWorld.Rules;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Last.Data;
using Last.Data.Master;
using Last.Data.User;
using Last.Defaine;
using Last.Management;
using Last.Systems;
using Il2CppInts = Il2CppSystem.Collections.Generic.List<int>;
using Il2CppIntMap = Il2CppSystem.Collections.Generic.Dictionary<int, int>;
using Il2CppConditions = Il2CppSystem.Collections.Generic.List<Last.Data.Master.Condition>;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW's esper system on top of the PR:
    //  * each esper can only be equipped by its one or two BNW users
    //  * vanilla "level-up bonus" off; instead EP from battles (XP/8 x SP) raises the
    //    character's Esper Level (max 25); each EL is spent on the equipped esper's bonus
    //  * equipped espers give BNW's equip bonus (stats, elements, statuses, HP/MP %)
    // Every hook is wrapped so a failure logs once and leaves the game's own result.
    static class EsperHooks
    {
        internal static BnwData D;
        internal static bool Restrict = true, Levels = true, EquipBonuses = true, NoVanillaBonus = true, AutoSpend = false;

        // The generic hook helpers (Patch with its shared-code refusal, IsA, WarnOnce, NoteOnce,
        // TinyNativeCode) are the modding kit's since 2026-11-11: plugin/PrKit/Hooks.cs (pr/MODDING.md).

        // Logged once at startup: shows the guard accepts the right type and rejects others.
        internal static string GuardSelfTest(Il2CppObjectBase sample)
        {
            return $"[guard] self-test: MasterManager accepted={Hooks.IsA<MasterManager>(sample)}, " +
                   $"as UserDataManager rejected={!Hooks.IsA<UserDataManager>(sample)}, " +
                   $"class pointers: PlayerCharacterParameter={Hooks.ClassPtr<PlayerCharacterParameter>() != IntPtr.Zero} " +
                   $"BattleResultProvider={Hooks.ClassPtr<BattleResultProvider>() != IntPtr.Zero}";
        }

        public static void Apply(Harmony h)
        {
            Patch(h, typeof(OwnedCharacterClient), "SetMagicStone", prefix: nameof(SetMagicStone_Prefix));
            // the esper list (greyed-out espers, equip buzzer) is handled in EsperMenu

            var stone = typeof(Serial.FF6.Management.MagicStoneData);
            Patch(h, stone, "GetParameterType", postfix: nameof(ParameterType_Postfix));
            Patch(h, stone, "GetParameterValue", postfix: nameof(ParameterValue_Postfix));
            Patch(h, stone, "GetMagicBonusMessageId", postfix: nameof(BonusMessage_Postfix));

            Patch(h, typeof(BattleResultProvider), "Genelate", postfix: nameof(BattleResult_Postfix));

            var p = typeof(PlayerCharacterParameter);
            Patch(h, p, "ConfirmedMaxHp", postfix: nameof(MaxHp_Postfix));
            Patch(h, p, "ConfirmedMaxMp", postfix: nameof(MaxMp_Postfix));
            Patch(h, p, "ConfirmedPower", postfix: nameof(Power_Postfix));
            Patch(h, p, "ConfirmedAgility", postfix: nameof(Agility_Postfix));
            Patch(h, p, "ConfirmedVitality", postfix: nameof(Vitality_Postfix));
            Patch(h, p, "ConfirmedMagic", postfix: nameof(Magic_Postfix));
            Patch(h, p, "ConfirmedDefense", postfix: nameof(Defense_Postfix));
            Patch(h, p, "ConfirmedAbilityDefense", postfix: nameof(AbilityDefense_Postfix));
            Patch(h, p, "ConfirmedEvasionRate", postfix: nameof(Evasion_Postfix));
            Patch(h, p, "ConfirmedAbilityEvasionRate", postfix: nameof(AbilityEvasion_Postfix));
            Patch(h, p, "ConfirmedResistanceAttributeList", postfix: nameof(Elements_Postfix));
            Patch(h, p, "ConfirmedResistanceCondtionList", postfix: nameof(Protect_Postfix));
            Patch(h, p, "GetEquipmentConfirmedConditionList", postfix: nameof(AutoStatus_Postfix));
        }

        static void Patch(Harmony h, Type type, string method, string prefix = null, string postfix = null, Type[] args = null) =>
            Hooks.Patch(h, type, method, typeof(EsperHooks), prefix, postfix, args);

        // --- helpers --------------------------------------------------------------------

        internal static Esper EsperOf(OwnedCharacterData c)
        {
            if (c == null) return null;
            return D.EsperByContent(c.MagicStoneId);
        }

        static OwnedCharacterData FindCharacter(int ownedId)
        {
            var list = UserDataManager.Instance()?.OwnedCharacterList;
            if (list == null) return null;
            foreach (var c in list)
                if (c != null && c.Id == ownedId) return c;
            return null;
        }

        // --- 1. who can equip what ------------------------------------------------------

        public static bool SetMagicStone_Prefix(OwnedCharacterClient __instance, int characterId, int contentId)
        {
            if (!Restrict || !Hooks.IsA<OwnedCharacterClient>(__instance)) return true;
            try
            {
                var c = FindCharacter(characterId);
                if (c == null) Hooks.NoteOnce("setstone-miss", () => $"[espers] SetMagicStone: no owned character with id {characterId}");
                if (c == null || EsperRules.CanEquip(D, c.characterStatusId, contentId)) return true;
                Plugin.L.LogInfo($"[espers] {c.Name} can't equip {D.EsperByContent(contentId)?.Name} in Brave New World");
                return false;
            }
            catch (Exception e) { Hooks.WarnOnce("SetMagicStone", e); return true; }
        }

        // --- 2. vanilla level-up bonuses off, BNW bonus text on --------------------------

        public static void ParameterType_Postfix(Serial.FF6.Management.MagicStoneData __instance, ref ParameterType __result)
        {
            if (NoVanillaBonus && Hooks.IsA<Serial.FF6.Management.MagicStoneData>(__instance)) __result = ParameterType.Non;
        }

        public static void ParameterValue_Postfix(Serial.FF6.Management.MagicStoneData __instance, ref int __result)
        {
            if (NoVanillaBonus && Hooks.IsA<Serial.FF6.Management.MagicStoneData>(__instance)) __result = 0;
        }

        public static void BonusMessage_Postfix(Serial.FF6.Management.MagicStoneData __instance, int contentId, ref string __result)
        {
            if (!Hooks.IsA<Serial.FF6.Management.MagicStoneData>(__instance)) return;
            try
            {
                var e = D.EsperByContent(contentId);
                if (e != null) __result = $"MSG_BNW_ESPER_BONUS_{e.SnesId:00}";
            }
            catch (Exception ex) { Hooks.WarnOnce("BonusMessage", ex); }
        }

        // --- 3. EP and Esper Levels after battle -----------------------------------------

        public static void BattleResult_Postfix(BattleResultProvider __instance, BattleResultData __result, int expValue)
        {
            if (!Levels || __result == null || !Hooks.IsA<BattleResultProvider>(__instance)) return;
            try
            {
                int sp = Math.Max(__result.GetMp, __result.GetAbp);
                Hooks.NoteOnce("result-fields", () => $"[espers] battle result: exp={expValue} GetExp={__result.GetExp} GetMp={__result.GetMp} GetAbp={__result.GetAbp}");
                if (!EsperStore.Available) return;
                foreach (var c in __result.CharacterList)
                {
                    var after = c?.AfterData;
                    if (after == null) continue;
                    int xp = c.GetExp;
                    if (xp <= 0) continue;
                    int sid = after.characterStatusId;
                    var esper = EsperOf(after);
                    var p = EsperStore.Get(sid);
                    int before = p.El;
                    int gained = EsperRules.AfterBattle(D, ref p, sid, esper != null, xp, sp);
                    var spent = new List<string>();
                    for (int i = 0; i < gained && AutoSpend; i++)
                    {
                        var added = EsperRules.SpendEl(D, ref p, esper, BaseOf(after.Parameter));
                        if (added != null) spent.Add(added.ToString());
                    }
                    if (esper != null) EsperStore.Put(sid, p);
                    if (gained > 0)
                        Plugin.L.LogInfo($"[espers] {after.Name}: EL {before} -> {p.El} (EP {p.Ep}, {p.ElBank} to spend) with {esper?.Name}" +
                                         (spent.Count > 0 ? $": auto-spent {string.Join(", ", spent)}" : ""));
                }
            }
            catch (Exception e) { Hooks.WarnOnce("BattleResult", e); }
        }

        internal static BaseStats BaseOf(PlayerCharacterParameter p) => p == null ? default : new BaseStats
        {
            Power = p.BasePower + p.AddtionalPower,
            Agility = p.BaseAgility + p.AddtionalAgility,
            Vitality = p.BaseVitality + p.AddtionalVitality,
            Magic = p.BaseMagic + p.AddtionalMagic,
        };

        // --- 4. stats: spent ELs + equipped esper's bonus --------------------------------

        static bool Bonuses(PlayerCharacterParameter p, out Esper esper, out StatBonus el)
        {
            esper = null;
            el = null;
            if (FormulaProbe.Active) return false;  // the probe sets every stat itself
            if (!Hooks.IsA<PlayerCharacterParameter>(p)) return false;
            var c = p?.parent;
            if (c == null) return false;
            if (EquipBonuses) esper = EsperOf(c);
            if (Levels)
            {
                var s = EsperStore.Get(c.characterStatusId).Spent;
                if (s != null && !s.IsEmpty) el = s;
            }
            return esper != null || el != null;
        }

        static void Stat(PlayerCharacterParameter p, ref int result, string name, Func<StatBonus, int> fromEl,
            Func<EquipBonus, int> fromEquip, int max)
        {
            try
            {
                if (!Bonuses(p, out var esper, out var el)) return;
                int v = result + (el != null ? fromEl(el) : 0) + (esper != null ? fromEquip(esper.Equip) : 0);
                result = Math.Max(0, Math.Min(max, v));
            }
            catch (Exception e) { Hooks.WarnOnce(name, e); }
        }

        public static void Power_Postfix(PlayerCharacterParameter __instance, ref int __result) =>
            Stat(__instance, ref __result, "Power", s => s.Power, q => q.Power, 255);

        public static void Agility_Postfix(PlayerCharacterParameter __instance, ref int __result) =>
            Stat(__instance, ref __result, "Agility", s => s.Agility, q => q.Agility, 255);

        public static void Vitality_Postfix(PlayerCharacterParameter __instance, ref int __result) =>
            Stat(__instance, ref __result, "Vitality", s => s.Vitality, q => q.Vitality, 255);

        public static void Magic_Postfix(PlayerCharacterParameter __instance, ref int __result) =>
            Stat(__instance, ref __result, "Magic", s => s.Magic, q => q.Magic, 255);

        public static void Defense_Postfix(PlayerCharacterParameter __instance, ref int __result) =>
            Stat(__instance, ref __result, "Defense", s => 0, q => q.Defense, 255);

        public static void AbilityDefense_Postfix(PlayerCharacterParameter __instance, ref int __result) =>
            Stat(__instance, ref __result, "AbilityDefense", s => 0, q => q.AbilityDefense, 255);

        public static void Evasion_Postfix(PlayerCharacterParameter __instance, ref int __result) =>
            Stat(__instance, ref __result, "Evasion", s => 0, q => q.EvasionRate, 255);

        public static void AbilityEvasion_Postfix(PlayerCharacterParameter __instance, ref int __result) =>
            Stat(__instance, ref __result, "AbilityEvasion", s => 0, q => q.AbilityEvasionRate, 255);

        public static void MaxHp_Postfix(PlayerCharacterParameter __instance, ref int __result)
        {
            try
            {
                if (!Bonuses(__instance, out var esper, out var el)) return;
                int v = __result + (el?.Hp ?? 0);
                __result = EsperRules.WithPercent(Math.Min(9999, v), esper?.Equip.MaxHpPct ?? 0, 9999);
            }
            catch (Exception e) { Hooks.WarnOnce("MaxHp", e); }
        }

        public static void MaxMp_Postfix(PlayerCharacterParameter __instance, ref int __result)
        {
            try
            {
                if (!Bonuses(__instance, out var esper, out var el)) return;
                int v = __result + (el?.Mp ?? 0);
                __result = EsperRules.WithPercent(Math.Min(999, v), esper?.Equip.MaxMpPct ?? 0, 999);
            }
            catch (Exception e) { Hooks.WarnOnce("MaxMp", e); }
        }

        // --- 5. elements, status protection, auto-statuses --------------------------------

        static Il2CppIntMap Merge(Il2CppIntMap src, Action<Dictionary<int, int>> change)
        {
            var d = new Dictionary<int, int>();
            if (src != null)
                foreach (var kv in src) d[kv.Key] = kv.Value;
            change(d);
            var dst = new Il2CppIntMap();
            foreach (var kv in d) dst.Add(kv.Key, kv.Value);
            return dst;
        }

        public static void Elements_Postfix(PlayerCharacterParameter __instance, ref Il2CppIntMap __result)
        {
            if (!EquipBonuses || FormulaProbe.Active || !Hooks.IsA<PlayerCharacterParameter>(__instance)) return;
            try
            {
                var src = __result;
                if (src != null && src.Count > 0) Hooks.NoteOnce("elements", () => $"[espers] element resistance sample: {Dump(src)}");
                var e = EsperOf(__instance?.parent);
                if (e == null || e.Equip.HalfAttributes.Length == 0) return;
                __result = Merge(src, d => EsperRules.AddHalves(d, e.Equip.HalfAttributes));
            }
            catch (Exception ex) { Hooks.WarnOnce("Elements", ex); }
        }

        public static void Protect_Postfix(PlayerCharacterParameter __instance, ref Il2CppIntMap __result)
        {
            if (!EquipBonuses || FormulaProbe.Active || !Hooks.IsA<PlayerCharacterParameter>(__instance)) return;
            try
            {
                var src = __result;
                if (src != null && src.Count > 0) Hooks.NoteOnce("protect", () => $"[espers] status resistance sample: {Dump(src)}");
                var e = EsperOf(__instance?.parent);
                if (e == null || e.Equip.ProtectConditions.Length == 0) return;
                __result = Merge(src, d => EsperRules.AddProtections(d, e.Equip.ProtectConditions));
            }
            catch (Exception ex) { Hooks.WarnOnce("Protect", ex); }
        }

        public static void AutoStatus_Postfix(PlayerCharacterParameter __instance, ref Il2CppConditions __result)
        {
            if (!EquipBonuses || FormulaProbe.Active || !Hooks.IsA<PlayerCharacterParameter>(__instance)) return;
            try
            {
                var e = EsperOf(__instance?.parent);
                if (e == null || e.Equip.AutoConditions.Length == 0) return;
                var have = new HashSet<int>();
                var list = new Il2CppConditions();
                if (__result != null)
                    foreach (var c in __result) { list.Add(c); if (c != null) have.Add(c.Id); }
                var mm = MasterManager.Instance;
                foreach (var id in e.Equip.AutoConditions)
                {
                    if (have.Contains(id)) continue;
                    var cond = mm.GetData<Condition>(id);
                    if (cond != null) list.Add(cond);
                }
                __result = list;
            }
            catch (Exception ex) { Hooks.WarnOnce("AutoStatus", ex); }
        }

        static string Dump(Il2CppIntMap m)
        {
            if (m == null) return "null";
            var parts = new List<string>();
            foreach (var kv in m) parts.Add($"{kv.Key}:{kv.Value}");
            return "{" + string.Join(", ", parts) + "}";
        }
    }
}
