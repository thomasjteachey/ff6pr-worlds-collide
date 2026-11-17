using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Last.Data.Master;
using Last.Data.User;
using Last.Defaine;
using Last.Interpreter;
using Last.Management;
using UnityEngine;

namespace WorldsCollide.PR
{
    // WC's recruit options. -stl (start level) is the port's (character_status lv). Here:
    //  -sal: a character who joins starts at the average level of those recruited before
    //        (instruction/c0.py recruit_character -> average_level), with the HP/MP that level
    //        gives (the PR's growth curve) and the skills it teaches (level learning rows);
    //  -eu:  Umaro may open the Equip menu (the PR keeps him to relics:
    //        CharacterInfomationData's menu restrictions).
    static class WcRecruits
    {
        internal static string LastJoin = "";
        const int Umaro = 14;
        static float nextRepair;
        static string lastRepairState = "";

        public static void Patch(Harmony h)
        {
            var t = typeof(Serial.FF6.Management.CharacterInfomationData);
            int n = 0;
            foreach (var (name, post) in new[] { ("CanOpenRestrictionMenuWindow", nameof(CanOpen_Postfix)),
                                                 ("GetDisplayRestrictionCharaIdList", nameof(Restricted_Postfix)),
                                                 ("IsChangeEquipmentOnlyAccessory", nameof(OnlyRelics_Postfix)) })
            {
                var m = AccessTools.Method(t, name);
                if (m == null) continue;
                h.Patch(m, postfix: new HarmonyMethod(typeof(WcRecruits), post));
                n++;
            }
            Plugin.L.LogInfo($"[wc] recruits: {n} of 3 menu checks hooked (-eu)");
        }

        static bool On(string flag) => Plugin.WcMode && WcSeeds.Active != null && WcSeeds.Active.Flags.Split(' ').Contains(flag);

        public static void CanOpen_Postfix(MenuCommandId commandId, int charaStatusId, ref bool __result)
        {
            if (!__result && charaStatusId == Umaro && commandId == MenuCommandId.Equipment && On("-eu")) __result = true;
        }

        public static void Restricted_Postfix(MenuCommandId commandId, ref Il2CppSystem.Collections.Generic.List<int> __result)
        {
            if (__result == null || commandId != MenuCommandId.Equipment || !__result.Contains(Umaro) || !On("-eu")) return;
            var list = new Il2CppSystem.Collections.Generic.List<int>();   // a copy: the PR's own list stays
            foreach (var id in __result) if (id != Umaro) list.Add(id);
            __result = list;
        }

        public static void OnlyRelics_Postfix(int characterStatusId, ref bool __result)
        {
            if (__result && characterStatusId == Umaro && On("-eu")) __result = false;
        }

        // a character joins (WcEvents.Join_Postfix, before the objectives' stat results)
        internal static void Joined(int statusId)
        {
            try
            {
                if (!On("-sal") || statusId < 1 || statusId > 14) return;
                var owned = UserDataManager.Instance()?.OwnedCharacterList;
                if (owned == null) return;
                OwnedCharacterData joiner = null;
                var levels = new List<int>();
                foreach (var c in owned)
                {
                    if (c == null || c.characterStatusId < 1 || c.characterStatusId > 14) continue;
                    if (c.characterStatusId == statusId) joiner = c;
                    else if (WcEvents.IsRecruited(c.characterStatusId)) levels.Add(c.Parameter.ConfirmedLevel());
                }
                if (joiner == null || levels.Count == 0) return;
                int target = Math.Min(99, levels.Sum() / levels.Count);
                // JoinCharacter may already put the recruit at, above or below WC's target level
                // and keep the corresponding HP/MP growth in AddtionalMaxHp/Mp. FreshRecruit
                // removes that PR growth first, so always normalize the recruit to WC's average.
                // Skipping equal left Mog at level 34 with 70 HP; skipping a PR level above the
                // average did the same to Relm.
                LastJoin = RaiseTo(joiner, target);
            }
            catch (Exception e) { WcRunner.WarnOnce("average-level", e); }
        }

        // the level, its HP / MP (growth curve), exp, and the level-learned skills up to it
        internal static string RaiseTo(OwnedCharacterData c, int target)
        {
            var p = c.Parameter;
            int from = p.ConfirmedLevel();
            var mm = MasterManager.Instance;
            Expected(c, target, out var start, out int expectedHp, out int expectedMp);
            // Despite its name, AddtionalLevel is the PR's saved/menu level, not an offset from
            // BaseLevel. ConfirmedLevel returns it directly. Subtracting BaseLevel here made every
            // -sal recruit too low (Relm's base 3 and requested 21 became displayed level 18).
            p.AddtionalLevel = Math.Max(1, target);
            // The master row is the seed's starting level and base HP/MP. Rebuild the complete
            // curve from there rather than adding only levels above p.BaseLevel: JoinCharacter can
            // have assigned target already, while FreshRecruit has just removed its additional HP.
            int hpBefore = p.BaseMaxHp, mpBefore = p.BaseMaxMp;
            // FreshRecruit normally cleared the PR's automatic growth. For an old affected save,
            // however, AddtionalMaxHp/Mp can contain legitimate growth already. Fill only the
            // missing part of the complete curve so that repairing its level cannot double it.
            p.BaseMaxHp += Math.Max(0, expectedHp - (p.BaseMaxHp + p.AddtionalMaxHp));
            p.BaseMaxMp += Math.Max(0, expectedMp - (p.BaseMaxMp + p.AddtionalMaxMp));
            foreach (var kv in mm.GetList<ExpTable>())
                if (kv.Value != null && kv.Value.GroupId == 1 && kv.Value.Lv == target && c.saveData != null) c.saveData.currentExp = kv.Value.TotalExp;
            var taught = new List<int>();
            foreach (var kv in mm.GetList<Learning>())
            {
                var l = kv.Value;
                if (l == null || l.TypeId != 1 || l.JobId != c.JobId || l.Value1 > target) continue;
                var content = mm.GetData<Content>(l.ContentId);
                if (content == null || content.TypeId != 4) continue;   // an ability
                if (!WcSkills.Knows(c, content.TypeValue) && WcSkills.Teach(c, content.TypeValue)) taught.Add(content.TypeValue);
            }
            p.CurrentHP = p.ConfirmedMaxHp();
            p.CurrentMP = p.ConfirmedMaxMp();
            var what = $"character {c.characterStatusId} joins at level {target} (was {from}; HP +{p.BaseMaxHp - hpBefore}, MP +{p.BaseMaxMp - mpBefore}; learned [{string.Join(",", taught)}])";
            Plugin.L.LogInfo("[wc] -sal: " + what);
            return what;
        }

        static void Expected(OwnedCharacterData c, int target, out CharacterStatus start, out int hp, out int mp)
        {
            var mm = MasterManager.Instance;
            start = mm.GetData<CharacterStatus>(c.characterStatusId) ?? c.characterStatus;
            int startLevel = start?.Lv ?? target;
            int group = start?.GrowthCurveGroupId ?? c.characterStatus?.GrowthCurveGroupId ?? 1;
            hp = start?.Hp ?? c.Parameter.BaseMaxHp;
            mp = start?.Mp ?? c.Parameter.BaseMaxMp;
            foreach (var kv in mm.GetList<GrowthCurve>())
            {
                var g = kv.Value;
                if (g != null && g.GroupId == group && g.Lv > startLevel && g.Lv <= target)
                {
                    hp += g.HpValue1;
                    mp += g.MpValue1;
                }
            }
        }

        // Repair saves made by the recruit-level bug above. Compare natural HP/MP (base plus level
        // growth) with the complete curve at the saved level. This covers the PR's several internal
        // layouts without touching equipment, and a normally leveled character already meets it.
        internal static void Update()
        {
            if (Time.unscaledTime < nextRepair) return;
            nextRepair = Time.unscaledTime + 2f;
            // Old Moogle Defense saves may have let vanilla's temporary-roster cleanup dismiss
            // their already-recruited Mog. The WC recruited flag survives, so restore him once the
            // completed save is safely back on the field. This repair is independent of -sal.
            WcEvents.RestoreMoogleDefenseMog();
            // Randomized joins skip vanilla naming scenes. Repair only blank/question-mark
            // placeholders, including existing saves, and preserve all legitimate custom names.
            WcEvents.RepairCharacterNames();
            // Older saves can own a character but lack WC's private gate bit.  Roster ownership
            // is enough to restore that bit; this is independent of starting-average-level.
            WcEvents.RepairRecruitedFlagsFromRoster();
            WcEvents.RepairKohlingenCafe();
            if (!On("-sal")) return;
            try
            {
                var owned = UserDataManager.Instance()?.OwnedCharacterList;
                var mm = MasterManager.Instance;
                if (owned == null || mm == null) return;
                // One state line per distinct load/change: enough to diagnose an affected save
                // without a noisy per-frame dump. Include the roster, active corps, repair guards,
                // and Relm's natural HP fields (equipment is only in ConfirmedMaxHp).
                var ids = new List<int>();
                OwnedCharacterData relm = null;
                foreach (var c in owned)
                {
                    if (c == null) continue;
                    ids.Add(c.characterStatusId);
                    if (c.characterStatusId == 9) relm = c;
                }
                var active = UserDataManager.Instance()?.GetPartyMemberData();
                var activeIds = new List<int>();
                if (active != null) foreach (var c in active) if (c != null) activeIds.Add(c.characterStatusId);
                string relmState = relm == null ? "missing" :
                    $"L{relm.Parameter.ConfirmedLevel()} (base {relm.Parameter.BaseLevel} +{relm.Parameter.AddtionalLevel}) " +
                    $"HP {relm.Parameter.CurrentHP}/{relm.Parameter.ConfirmedMaxHp()} " +
                    $"(base {relm.Parameter.BaseMaxHp}, add {relm.Parameter.AddtionalMaxHp})";
                string mogState = "missing";
                foreach (var c in owned)
                    if (c != null && c.characterStatusId == 11) mogState = $"owned/selectable={c.IsEnableCorps}";
                string repairState = $"WC {WcEvents.Flag(DataStorage.Category.kScenarioFlag4, WcEvents.WcGameFlag)}, " +
                    $"Mog recruited {WcEvents.IsRecruited(11)}, defense start {WcEvents.Flag(DataStorage.Category.kScenarioFlag1, 13)}, " +
                    $"done {WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 62)}; owned [{string.Join(" ", ids)}], " +
                    $"active [{string.Join(" ", activeIds)}], Mog {mogState}, Relm {relmState}";
                if (repairState != lastRepairState)
                {
                    lastRepairState = repairState;
                    Plugin.L.LogInfo("[wc] recruit repair state: " + repairState);
                }
                if (WcEvents.Flag(DataStorage.Category.kScenarioFlag4, WcEvents.WcGameFlag) != 1) return;
                // Builds with the old -sal calculation saved Relm at target minus her base level,
                // and some Moogle Defense saves compounded that into a much larger gap. Repair
                // only the unmistakable case (Relm at least ten levels below the other recruited
                // characters), so a normally lagging character is never silently power-levelled.
                if (relm != null && WcEvents.IsRecruited(9))
                {
                    var peers = new List<int>();
                    foreach (var c in owned)
                        if (c != null && c.characterStatusId >= 1 && c.characterStatusId <= 14 &&
                            c.characterStatusId != 9 && WcEvents.IsRecruited(c.characterStatusId))
                            peers.Add(c.Parameter.ConfirmedLevel());
                    if (peers.Count > 0)
                    {
                        int target = Math.Min(99, peers.Sum() / peers.Count);
                        int level = relm.Parameter.ConfirmedLevel();
                        if (target - level >= 10)
                        {
                            string repaired = RaiseTo(relm, target);
                            Plugin.L.LogInfo($"[wc] repaired saved Relm level from {level} to recruited-party average {target}: {repaired}");
                        }
                    }
                }
                foreach (var c in owned)
                {
                    // The owned list is authoritative here. Affected Moogle Defense autosaves can
                    // have lost a WC recruited bit along with their temporary roster state; core
                    // status ids 1-14 are real characters, while the event guests start at 19.
                    if (c == null || c.characterStatusId < 1 || c.characterStatusId > 14) continue;
                    var p = c.Parameter;
                    var start = mm.GetData<CharacterStatus>(c.characterStatusId);
                    if (p == null || start == null) continue;
                    int level = p.ConfirmedLevel();
                    Expected(c, level, out _, out int expectedHp, out int expectedMp);
                    int missingHp = expectedHp - (p.BaseMaxHp + p.AddtionalMaxHp);
                    int missingMp = expectedMp - (p.BaseMaxMp + p.AddtionalMaxMp);
                    if (missingHp <= 0 && missingMp <= 0) continue;
                    if (missingHp > 0) p.BaseMaxHp += missingHp;
                    if (missingMp > 0) p.BaseMaxMp += missingMp;
                    p.CurrentHP = p.ConfirmedMaxHp();
                    p.CurrentMP = p.ConfirmedMaxMp();
                    string fixedState = $"character {c.characterStatusId} at level {level}: HP +{Math.Max(0, missingHp)}, MP +{Math.Max(0, missingMp)}";
                    Plugin.L.LogInfo($"[wc] repaired saved recruit growth: {fixedState}; HP {p.CurrentHP}/{p.ConfirmedMaxHp()}, MP {p.CurrentMP}/{p.ConfirmedMaxMp()}");
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("recruit-growth-repair", e); }
        }
    }
}
