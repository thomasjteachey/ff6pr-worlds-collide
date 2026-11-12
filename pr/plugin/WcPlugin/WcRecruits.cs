using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Last.Data.Master;
using Last.Data.User;
using Last.Defaine;
using Last.Management;

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
                    else if (WcEvents.IsRecruited(c.characterStatusId)) levels.Add(c.Parameter.BaseLevel);
                }
                if (joiner == null || levels.Count == 0) return;
                int target = Math.Min(99, levels.Sum() / levels.Count);
                if (target > joiner.Parameter.BaseLevel) LastJoin = RaiseTo(joiner, target);
            }
            catch (Exception e) { WcRunner.WarnOnce("average-level", e); }
        }

        // the level, its HP / MP (growth curve), exp, and the level-learned skills up to it
        internal static string RaiseTo(OwnedCharacterData c, int target)
        {
            var p = c.Parameter;
            int from = p.BaseLevel, hp = 0, mp = 0;
            var mm = MasterManager.Instance;
            int group = c.characterStatus?.GrowthCurveGroupId ?? 1;
            foreach (var kv in mm.GetList<GrowthCurve>())
            {
                var g = kv.Value;
                if (g != null && g.GroupId == group && g.Lv > from && g.Lv <= target) { hp += g.HpValue1; mp += g.MpValue1; }
            }
            p.BaseLevel = target;
            p.BaseMaxHp += hp;
            p.BaseMaxMp += mp;
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
            var what = $"character {c.characterStatusId} joins at level {target} (was {from}; HP +{hp}, MP +{mp}; learned [{string.Join(",", taught)}])";
            Plugin.L.LogInfo("[wc] -sal: " + what);
            return what;
        }
    }
}
