using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BraveNewWorld.Rules;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.Data.User;
using PassiveProvider = Serial.FF6.Management.PassiveContentProvider;
using FlagData = Serial.FF6.Management.SpcialFlagData;
using Last.Management;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // The PR keys some equipment effects on item ids inside its code (static members such
    // as CalcExecuteCorrection.MagicCorrectionAccessoryIds or SpcialFlagData.NonEncountItemId).
    // BNW gave those effects to other items and reused the slots, so left alone the port
    // would give e.g. "no random encounters" to BNW's Moogle Charm. Each list in
    // bnw_data.json hard_items names the vanilla items it should hold now and BNW's items
    // for that effect; a list is rewritten only if it holds exactly the expected vanilla
    // items (as weapon/armor ids or as content ids, whichever the game uses), else logged.
    // Some are compile-time constants in the game (SpcialFlagData's item ids): those have
    // no storage, writing one crashes the game (v0.11.0), so they are never written; their
    // checks are redone with BNW's items in hooks on the methods that read them.
    static class HardItems
    {
        internal static bool Enabled = true;
        static BnwData D => EsperHooks.D;
        static readonly HashSet<string> settled = new HashSet<string>();

        public static void Apply(Harmony h)
        {
            var t = typeof(PassiveProvider);
            Hooks.Patch(h, t, "GetExp", typeof(HardItems), postfix: nameof(GetExp_Postfix));
            Hooks.Patch(h, t, "GetGil", typeof(HardItems), postfix: nameof(GetGil_Postfix));
            // SpcialFlagData.IsEncount/GetEncountValue/IsMoveHealing take the party by
            // reference; patching them makes Il2CppInterop emit invalid code (v0.11.2: an
            // exception on every encounter check). Not patched: the encounter relics keep the
            // game's own (vanilla-slot) checks until a safe hook point is found.
            Hooks.Patch(h, typeof(Last.Map.Tamanosuzu), "CheckUseItem", typeof(HardItems), postfix: nameof(Tintinabar_Postfix));
        }

        // The field's walking-regen check (Tintinabar, "Tamanosuzu"): BNW has none.
        public static void Tintinabar_Postfix(Last.Map.Tamanosuzu __instance, ref bool __result)
        {
            var l = ListOf("HealingItemId");
            if (!Enabled || !__result || l == null || l.Bnw.Length > 0 || !Hooks.IsA<Last.Map.Tamanosuzu>(__instance)) return;
            __result = false;
            Hooks.NoteOnce("tintinabar", () => "[items] walking regen (old Tintinabar slot) cancelled on the field");
        }

        // --- static fields: only real, non-constant ones may be written ------------------

        const int FieldStatic = 0x10, FieldLiteral = 0x40;  // IL2CPP FIELD_ATTRIBUTE_*

        // null if the game's static field can be written, else why not.
        internal static string WriteProblem(Type t, string member)
        {
            var ptrField = t.GetField("NativeFieldInfoPtr_" + member, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (ptrField == null) return "no field info";
            var info = (IntPtr)ptrField.GetValue(null);
            if (info == IntPtr.Zero) return "no field info";
            int flags = IL2CPP.il2cpp_field_get_flags(info);
            if ((flags & FieldLiteral) != 0) return "a constant";
            if ((flags & FieldStatic) == 0) return "not static";
            return null;
        }

        // --- hooks for the constants ---------------------------------------------------

        static HardList ListOf(string member) => D?.HardItems.FirstOrDefault(h => h.Member == member && h.Type.EndsWith("SpcialFlagData"));

        internal static bool Wears(OwnedCharacterData c, HardItem[] items)
        {
            var equipment = c?.EquipmentData;
            if (equipment == null || items.Length == 0) return false;
            foreach (var it in equipment.Values)
                if (it != null && items.Any(i => i.ContentId == it.ContentId)) return true;
            return false;
        }


        // x2 EXP (Exp. Egg) and x2 gil (Cat Hood): BNW has no item with either, but the PR
        // would still give them to whatever BNW put in those slots (Daryl's Soul).
        static bool Removed(string passive) => D != null && D.Passives.TryGetValue(passive, out var items) && items.Length == 0;

        public static void GetExp_Postfix(PassiveProvider __instance, int baseValue, ref int __result)
        {
            if (Enabled && __result != baseValue && Removed("exp_x2") && Hooks.IsA<PassiveProvider>(__instance))
            {
                int was = __result;
                Hooks.NoteOnce("exp-x2", () => $"[items] x2 EXP from an old Exp. Egg slot cancelled ({baseValue} -> {was})");
                __result = baseValue;
            }
        }

        public static void GetGil_Postfix(PassiveProvider __instance, int baseValue, ref int __result)
        {
            if (Enabled && __result != baseValue && Removed("gil_x2") && Hooks.IsA<PassiveProvider>(__instance))
            {
                int was = __result;
                Hooks.NoteOnce("gil-x2", () => $"[items] x2 gil from an old Cat Hood slot cancelled ({baseValue} -> {was})");
                __result = baseValue;
            }
        }

        // Called every few seconds until every list is in BNW's shape (a class's static
        // constructor may run late and put vanilla's values back).
        internal static void Fix()
        {
            if (!Enabled || D == null || settled.Count >= D.HardItems.Count) return;
            foreach (var list in D.HardItems)
            {
                var key = list.Type + "." + list.Member;
                if (settled.Contains(key)) continue;
                try
                {
                    if (FixList(list)) settled.Add(key);
                }
                catch (Exception e)
                {
                    settled.Add(key);
                    Plugin.L.LogWarning($"[items] {key}: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        internal static void InitClass(Type t)
        {
            var store = typeof(Il2CppClassPointerStore<>).MakeGenericType(t);
            var field = store.GetField("NativeClassPtr", BindingFlags.Public | BindingFlags.Static);
            var ptr = (IntPtr)(field?.GetValue(null) ?? IntPtr.Zero);
            if (ptr == IntPtr.Zero)
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle);
                ptr = (IntPtr)(field?.GetValue(null) ?? IntPtr.Zero);
            }
            if (ptr != IntPtr.Zero) IL2CPP.il2cpp_runtime_class_init(ptr);
        }

        // true = done with this list (rewritten, already BNW's, or not ours to touch)
        static bool FixList(HardList list)
        {
            var key = list.Type.Split('.').Last() + "." + list.Member;
            var t = FindType(list.Type);
            var prop = t?.GetProperty(list.Member, BindingFlags.Public | BindingFlags.Static);
            if (prop == null) { Plugin.L.LogWarning($"[items] {key}: not found in the game"); return true; }
            if (list.Kind != "dict")
            {
                var problem = WriteProblem(t, list.Member);
                if (problem != null)
                {
                    Plugin.L.LogInfo($"[items] {key}: {problem} in the game, not rewritten" +
                                     (list.Type.EndsWith("SpcialFlagData") ? " (the game's own check stays: its methods can't be hooked safely)" : ""));
                    return true;
                }
            }
            InitClass(t);
            switch (list.Kind)
            {
                case "id":
                {
                    int cur = (int)prop.GetValue(null);
                    var want = Want(list, new[] { cur }, key, out var space);
                    if (want == null) return space != null;
                    int value = want.Length > 0 ? want[0] : -1;
                    if (want.Length > 1) Plugin.L.LogWarning($"[items] {key}: one id, BNW has {want.Length} items; using {Names(list.Bnw)[0]}");
                    if (cur == value) return true;
                    prop.SetValue(null, value);
                    Plugin.L.LogInfo($"[items] {key}: {string.Join(", ", Names(list.Vanilla, cur))} -> {(value < 0 ? "nothing (BNW removed it)" : string.Join(", ", Names(list.Bnw)))} ({space} id {cur} -> {value})");
                    return true;
                }
                case "array":
                {
                    var cur = prop.GetValue(null) as Il2CppStructArray<int>;
                    if (cur == null) return false;
                    var values = cur.ToArray();
                    var want = Want(list, values, key, out var space);
                    if (want == null) return space != null;
                    if (values.SequenceEqual(want)) return true;
                    prop.SetValue(null, new Il2CppStructArray<int>(want));
                    Plugin.L.LogInfo($"[items] {key}: [{string.Join(", ", Names(list.Vanilla))}] -> [{string.Join(", ", Names(list.Bnw))}] ({space} ids)");
                    return true;
                }
                case "dict":
                {
                    var dict = prop.GetValue(null) as Il2CppSystem.Collections.Generic.Dictionary<int, Il2CppStructArray<int>>;
                    if (dict == null) return false;
                    var keys = new List<int>();
                    foreach (var k in dict.Keys) keys.Add(k);
                    bool all = true;
                    foreach (var k in keys)
                    {
                        var values = dict[k].ToArray();
                        var want = Want(list, values, $"{key}[{k}]", out var space);
                        if (want == null) { all &= space != null; continue; }
                        if (values.SequenceEqual(want)) continue;
                        dict[k] = new Il2CppStructArray<int>(want);
                        Plugin.L.LogInfo($"[items] {key}[{k}]: [{string.Join(", ", Names(list.Vanilla, values))}] -> [{string.Join(", ", Names(list.Bnw))}] ({space} ids)");
                    }
                    return all;
                }
            }
            Plugin.L.LogWarning($"[items] {key}: unknown kind '{list.Kind}'");
            return true;
        }

        // BNW's values in the id space the game uses here, or null to leave the list alone
        // (space null = unexpected contents, reported; space set = nothing to do).
        static int[] Want(HardList list, int[] values, string key, out string space)
        {
            var vals = values.Where(v => v > 0).ToArray();
            space = null;
            if (vals.Length == 0) { space = "empty"; return null; }
            if (vals.All(v => list.Vanilla.Any(i => i.ItemId == v)) || vals.All(v => list.Bnw.Any(i => i.ItemId == v)))
                space = "item";
            else if (vals.All(v => list.Vanilla.Any(i => i.ContentId == v)) || vals.All(v => list.Bnw.Any(i => i.ContentId == v)))
                space = "content";
            if (space == null)
            {
                Plugin.L.LogWarning($"[items] {key}: holds [{string.Join(", ", values)}], not the expected " +
                                    $"[{string.Join(", ", Names(list.Vanilla))}]; left alone");
                space = "unknown";
                return null;
            }
            return space == "item" ? list.Bnw.Select(i => i.ItemId).ToArray() : list.Bnw.Select(i => i.ContentId).ToArray();
        }

        static string[] Names(HardItem[] items, params int[] onlyIds)
        {
            var pick = onlyIds.Length == 0 ? items : items.Where(i => onlyIds.Contains(i.ItemId) || onlyIds.Contains(i.ContentId)).ToArray();
            return pick.Length == 0 ? new[] { "-" } : pick.Select(i => i.Name).ToArray();
        }
    }
}
