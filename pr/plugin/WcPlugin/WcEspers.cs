using System;
using System.Linq;
using HarmonyLib;

namespace WorldsCollide.PR
{
    // WC's esper level-up bonuses (-ebs shuffled, -ebr random; data/espers.py): the seed's SNES
    // bonus code per esper (wc_seed.py esper_bonus). The PR builds its bonuses in code
    // (Serial.FF6.Management.MagicStoneData: type, value, text by magicite content id), so an esper
    // whose bonus changed answers as a vanilla esper with that bonus does (its "donor"). Two codes
    // no vanilla esper has: Speed +2 (Speed +1's, doubled; the PR has its text) and HP +100% (HP
    // +10%'s, x10; text of our own). No bonus: an esper without one.
    static class WcEspers
    {
        internal static string LastBonus = "";
        static bool busy;

        public static void Patch(Harmony h)
        {
            var t = typeof(Serial.FF6.Management.MagicStoneData);
            int n = 0;
            foreach (var (name, post) in new[] { ("GetParameterType", nameof(Type_Postfix)), ("GetParameterValue", nameof(Value_Postfix)),
                                                 ("GetBonusBaseValue", nameof(Base_Postfix)), ("GetMagicBonusMessageId", nameof(Message_Postfix)) })
            {
                var m = AccessTools.Method(t, name);
                if (m == null) continue;
                h.Patch(m, postfix: new HarmonyMethod(typeof(WcEspers), post));
                n++;
            }
            Plugin.L.LogInfo($"[wc] esper bonuses: {n} of 4 lookups hooked");
            var set = AccessTools.Method(typeof(global::Last.Management.OwnedCharacterClient), "SetMagicStone");
            if (set != null) h.Patch(set, prefix: new HarmonyMethod(typeof(WcEspers), nameof(SetMagicStone_Prefix)));
            // The Magic command: WC randomizes only a character's second command (data/commands.py
            // sets commands[1]); Fight, Magic and Item stay, so every character but Gogo and Umaro
            // has Magic in the SNES and casts (and summons) once they know a spell or hold an
            // esper. The PR's rows give Magic up front only to Terra and Celes and add it on a
            // character's first magicite equip (MagicStoneDetailsControllerBase.EquipMagicStone,
            // never removed), the same thing; that is left as it is. RestoreMagicCommands gives it
            // back where an earlier plugin took it away, and to WC's natural-magic learners
            // (-nm1/-nm2 random), who may never equip magicite.
            // The PR also treats the Magic command as permission to display an esper's learnable
            // spells, so before a character's first equip the list would be empty. Let only this
            // calculation see a temporary Magic command; it is never saved or used in battle.
            var learning = AccessTools.Method(typeof(Serial.FF6.UI.MagicStoneDetailsControllerBase), "CreateLearnigAbilitys");
            if (learning != null)
                h.Patch(learning,
                    prefix: new HarmonyMethod(typeof(WcEspers), nameof(CreateLearning_Prefix)),
                    postfix: new HarmonyMethod(typeof(WcEspers), nameof(CreateLearning_Postfix)));
            Plugin.L.LogInfo($"[wc] esper equip UI: learning {(learning == null ? "not found" : "hooked")}");
            var used = AccessTools.Method(typeof(global::Last.Battle.BattlePlugManager), "IsUsedMagicStoneAbilityInBattle");
            if (used != null) h.Patch(used, postfix: new HarmonyMethod(typeof(WcEspers), nameof(Used_Postfix)));
        }

        internal static bool HasCommand(global::Last.Data.User.OwnedCharacterData c, int id)
        {
            if (c?.CommandList == null) return false;
            foreach (var command in c.CommandList)
                if (command != null && command.Id == id) return true;
            return false;
        }

        public static void CreateLearning_Prefix(global::Last.Data.User.OwnedCharacterData __0,
                                                  out global::Last.Data.Master.Command __state)
        {
            var characterData = __0;
            __state = null;
            if (!Plugin.WcMode || characterData?.CommandList == null || HasCommand(characterData, 7) || HasCommand(characterData, 31)) return;
            try
            {
                var masters = global::Last.Data.Master.MasterManager.Instance;
                var magic = masters?.GetData<global::Last.Data.Master.Command>(7);
                if (magic == null) return;
                characterData.CommandList.Add(magic);
                __state = magic;
            }
            catch (Exception e) { WcRunner.WarnOnce("esper-learning-command", e); }
        }

        public static void CreateLearning_Postfix(global::Last.Data.User.OwnedCharacterData __0,
                                                   global::Last.Data.Master.Command __state)
        {
            var characterData = __0;
            if (__state == null || characterData?.CommandList == null) return;
            try { characterData.CommandList.Remove(__state); }
            catch (Exception e) { WcRunner.WarnOnce("esper-learning-command-remove", e); }
        }

        // a spell (PR abilities 31-84: the 54 of SNES 0-53) learned to 100%
        internal static bool KnowsSpell(global::Last.Data.User.OwnedCharacterData c)
        {
            var list = c?.OwnedAbilityList;
            if (list == null) return false;
            foreach (var a in list)
                if (a?.Ability != null && a.Ability.Id >= 31 && a.Ability.Id <= 84 && a.SkillLevel >= 100) return true;
            return false;
        }

        // Magic for whoever can use it, as in WC's SNES (see Patch): characters 1-13 (Umaro has no
        // Magic) holding magicite or knowing a spell, without Magic (7) or X-Magic (31). Saves
        // from WC plugin 0.6.0 and earlier had it taken away after every equip (the user's Deck
        // save, 2026-11-12: espers couldn't be summoned). Run every few seconds in the field
        // (WcObjectives.Poll), so natural magic learned in a battle shows up after it.
        internal static int RestoreMagicCommands()
        {
            if (!Plugin.WcMode || WcSeeds.Active == null) return 0;
            var owned = global::Last.Management.UserDataManager.Instance()?.OwnedCharacterList;
            if (owned == null) return 0;
            int restored = 0;
            var who = new System.Collections.Generic.List<int>();
            global::Last.Management.OwnedCharacterClient client = null;
            foreach (var c in owned)
            {
                if (c == null || c.characterStatusId < 1 || c.characterStatusId > 13 || HasCommand(c, 7) || HasCommand(c, 31)) continue;
                if (c.MagicStoneId <= 0 && !KnowsSpell(c)) continue;
                client ??= new global::Last.Management.OwnedCharacterClient();
                client.AddCommand(c.Id, 7);                     // what EquipMagicStone does
                client.UpdateChangeCommandForAccessory(c.Id);
                if (!HasCommand(c, 7) && !HasCommand(c, 31)) continue;
                restored++;
                who.Add(c.characterStatusId);
            }
            if (restored > 0) Plugin.L.LogInfo($"[wc] magicite: Magic command restored for character(s) {string.Join(",", who)} (magicite or spells)");
            return restored;
        }

        // WC's -eer / -eebr (esper restrictions): only the seed's characters may equip an esper
        // (data/espers.py equipable_*); others get a note instead (WcScreen.Toast)
        public static bool SetMagicStone_Prefix(int characterId, int contentId)
        {
            try
            {
                var s = WcSeeds.Active;
                if (!Plugin.WcMode || s == null || s.EsperEquip.Count == 0 || !s.EsperEquip.TryGetValue(contentId, out var who)) return true;
                var owned = global::Last.Management.UserDataManager.Instance()?.OwnedCharacterList;
                global::Last.Data.User.OwnedCharacterData c = null;
                if (owned != null) foreach (var x in owned) if (x != null && x.Id == characterId) c = x;
                if (c == null || c.characterStatusId > 14 || who.Contains(c.characterStatusId)) return true;
                WcScreen.Toast($"{c.Name} can't equip {WcEvents.ContentName(contentId)} in this seed");
                Plugin.L.LogInfo($"[wc] -eer: {c.characterStatusId} can't equip {contentId} (only {string.Join(",", who)})");
                return false;
            }
            catch (Exception e) { WcRunner.WarnOnce("esper-equip", e); return true; }
        }

        // WC's -ems (esper multi-summon): a character may summon again in the same battle
        public static void Used_Postfix(ref bool __result)
        {
            if (!__result) return;
            var s = WcSeeds.Active;
            if (Plugin.WcMode && s != null && s.Flags.Split(' ').Contains("-ems")) __result = false;
        }

        const int Hp10 = 0, Hp100 = 6, Speed1 = 11, Speed2 = 12, None = 0xFF;

        // (donor content id, scale) for an esper whose bonus the seed changed; null = the PR's own
        internal static (int Donor, int Scale, int Code)? Donor(int contentId)
        {
            var s = WcSeeds.Active;
            if (!Plugin.WcMode || s == null || s.EsperBonus.Count == 0 || !s.EsperBonus.TryGetValue(contentId, out int code)) return null;
            int scale = 1, want = code;
            if (code == Speed2 && !s.EsperBonusVanilla.ContainsValue(Speed2)) { want = Speed1; scale = 2; }
            else if (code == Hp100 && !s.EsperBonusVanilla.ContainsValue(Hp100)) { want = Hp10; scale = 10; }
            else if (code > 16) want = None;
            foreach (var kv in s.EsperBonusVanilla)
                if (kv.Value == want) return (kv.Key, scale, code);
            return null;
        }

        public static void Type_Postfix(Serial.FF6.Management.MagicStoneData __instance, int contentId, ref Last.Defaine.ParameterType __result)
        {
            if (busy) return;
            var d = Donor(contentId);
            if (d == null) return;
            busy = true;
            try { __result = __instance.GetParameterType(d.Value.Donor); }
            catch (Exception e) { WcRunner.WarnOnce("esper-type", e); }
            finally { busy = false; }
        }

        public static void Value_Postfix(Serial.FF6.Management.MagicStoneData __instance, int contentId, int value, ref int __result)
        {
            if (busy) return;
            var d = Donor(contentId);
            if (d == null) return;
            busy = true;
            try
            {
                __result = __instance.GetParameterValue(d.Value.Donor, value) * d.Value.Scale;
                LastBonus = $"esper {contentId}: bonus code {d.Value.Code} as esper {d.Value.Donor}'s x{d.Value.Scale} = {__result} (base {value})";
            }
            catch (Exception e) { WcRunner.WarnOnce("esper-value", e); }
            finally { busy = false; }
        }

        public static void Base_Postfix(Serial.FF6.Management.MagicStoneData __instance, int contentId, ref int __result)
        {
            if (busy) return;
            var d = Donor(contentId);
            if (d == null) return;
            busy = true;
            try { __result = __instance.GetBonusBaseValue(d.Value.Donor) * d.Value.Scale; }
            catch (Exception e) { WcRunner.WarnOnce("esper-base", e); }
            finally { busy = false; }
        }

        public static void Message_Postfix(Serial.FF6.Management.MagicStoneData __instance, int contentId, ref string __result)
        {
            if (busy) return;
            var d = Donor(contentId);
            if (d == null) return;
            busy = true;
            try
            {
                var key = __instance.GetMagicBonusMessageId(d.Value.Donor);
                if (d.Value.Code == Speed2 && d.Value.Scale == 2)
                    key = key == "MSG_MAG_INF_146" ? "MSG_MAG_INF_166" : key == "MSG_MAG_INF_161" ? "MSG_MAG_INF_167" : key;
                else if (d.Value.Code == Hp100 && d.Value.Scale == 10)
                {
                    bool longText = key == "MSG_MAG_INF_138";
                    key = longText ? "WC_ESPER_HP100" : "WC_ESPER_HP100_SHORT";
                    WcEvents.SetText(key, longText ? "+100% bonus to HP increase at next level-up" : "HP +100%");
                }
                __result = key;
            }
            catch (Exception e) { WcRunner.WarnOnce("esper-text", e); }
            finally { busy = false; }
        }
    }
}
