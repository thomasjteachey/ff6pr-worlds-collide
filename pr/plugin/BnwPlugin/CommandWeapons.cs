using System;
using System.Collections.Generic;
using System.Linq;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // The weapons that allow Runic and Bushido, moved to BNW's. The PR keeps both lists in code
    // (decompiled tools/decomp/runic, tools/decomp/bushido): Serial.FF6.Management
    // .BlockMagicSwordData and .KillingSwordData each hold a ValidWeaponContentId list, exactly
    // the SNES weapons with byte 19 bit 7 (Runic, 51 incl. spears, katanas, Magus Rod) and bit 1
    // (Bushido, 28). BNW's (bnw_data.json "command_weapons"): Runic 23 (daggers, swords, Atma
    // Weapon), Bushido 14. Without such a weapon the command is greyed out (BNW's "Swordless
    // Runic" / "Brushless Sketch" family). Each list is refilled once the providers are up, only
    // while it still holds vanilla's. Which spells Runic absorbs is data (port_flags, flag 3).
    // Sketch needs a brush in a hand (BNW "Brushless Sketch", c2.asm SketchBrshChk / BrushHand:
    // the item's name starts with the brush icon; bnw_data.json "sketch"). The PR lets any weapon
    // Sketch; its command check (CommandAvailableData.IsAvailable, decompiled tools/decomp/morph)
    // gets a postfix that turns Sketch off without a brush.
    static class CommandWeapons
    {
        internal static bool Enabled = true;

        public static void Patch(HarmonyLib.Harmony h)
        {
            if (!Enabled || EsperHooks.D.Brushes.Length == 0 || EsperHooks.D.SketchCommandId == 0) return;
            Hooks.Patch(h, typeof(Serial.FF6.Management.CommandAvailableData), "IsAvailable", typeof(CommandWeapons),
                postfix: nameof(Available_Postfix));
        }

        internal static bool HasBrush(Last.Data.User.OwnedCharacterData c)
        {
            var equipment = c?.EquipmentData;
            if (equipment == null) return false;
            var brushes = EsperHooks.D.Brushes;
            foreach (var it in equipment.Values)
                if (it != null && brushes.Contains(it.ContentId)) return true;
            return false;
        }

        public static void Available_Postfix(Last.Data.User.OwnedCharacterData targetData, Last.Data.Master.Command command, ref bool __result)
        {
            if (!Enabled || !__result || command == null || targetData == null) return;
            try
            {
                if (command.Id != EsperHooks.D.SketchCommandId || HasBrush(targetData)) return;
                __result = false;
                Hooks.NoteOnce("sketch-brush", () => $"[sketch] Sketch off for {targetData.Name}: no brush (BNW)");
            }
            catch (Exception e) { Hooks.WarnOnce("CommandWeapons.Sketch", e); }
        }

        // Check once a save is loaded: the game's own IsAvailable for Sketch, per party member
        // (null while no characters are loaded).
        internal static string SketchCheck()
        {
            var d = EsperHooks.D;
            if (!Enabled || d.SketchCommandId == 0) return "[sketch] check: off";
            var owned = Last.Management.UserDataManager.Instance()?.GetOwnedCharactersClone(false);
            if (owned == null || owned.Count == 0) return null;
            var data = Serial.ProviderManager.Instance?.CommandAvailableProvider?.serialData
                ?.TryCast<Serial.FF6.Management.CommandAvailableData>();
            var command = Last.Data.Master.MasterManager.Instance?.GetData<Last.Data.Master.Command>(d.SketchCommandId);
            if (data == null || command == null) return null;
            var parts = new System.Collections.Generic.List<string>();
            foreach (var c in owned)
            {
                if (c == null || parts.Count >= 6) continue;
                bool ok = data.IsAvailable(c, command, null, null);
                parts.Add($"{c.Name} {(HasBrush(c) ? "brush" : "no brush")} -> {(ok ? "on" : "off")}");
            }
            return $"[sketch] check (command {d.SketchCommandId}, {d.Brushes.Length} brushes): {string.Join("; ", parts)}";
        }

        static Il2CppSystem.Collections.Generic.List<int> GameList(string command)
        {
            var pm = Serial.ProviderManager.Instance;
            if (pm == null) return null;
            return command switch
            {
                "runic" => pm.BlockMagicSwordProvider?.serialData?.TryCast<Serial.FF6.Management.BlockMagicSwordData>()?.ValidWeaponContentId,
                "bushido" => pm.KillingSwordProvider?.serialData?.TryCast<Serial.FF6.Management.KillingSwordData>()?.ValidWeaponContentId,
                _ => null,
            };
        }

        // Returns one log line once every list is handled, null while the providers aren't loaded.
        internal static string Apply()
        {
            var d = EsperHooks.D;
            if (!Enabled || d.CommandWeapons.Count == 0) return "[weapons] Runic/Bushido: the game's own lists";
            var notes = new List<string>();
            foreach (var kv in d.CommandWeapons)
            {
                var list = GameList(kv.Key);
                if (list == null) return null;
                var now = Enumerable.Range(0, list.Count).Select(i => list[i]).OrderBy(x => x).ToArray();
                if (now.SequenceEqual(kv.Value.Bnw.OrderBy(x => x))) { notes.Add($"{kv.Key} BNW's {now.Length}"); continue; }
                if (!now.SequenceEqual(kv.Value.Vanilla.OrderBy(x => x)))
                {
                    notes.Add($"{kv.Key} unknown list ({now.Length}: {string.Join(",", now)}), left alone");
                    continue;
                }
                list.Clear();
                foreach (var id in kv.Value.Bnw) list.Add(id);
                notes.Add($"{kv.Key} BNW's {kv.Value.Bnw.Length} (was vanilla's {now.Length})");
            }
            return "[weapons] " + string.Join("; ", notes);
        }
    }
}
