using System;
using System.Linq;
using HarmonyLib;

namespace WorldsCollide.PR
{
    // WC's Coliseum (data/coliseum.py, flags -cor/-cosr/-crr/-crsr/-crvr/-cnee/-cnil...): the seed's
    // opponent and reward for each wagered item (wc_seed.py coliseum: the matches WC changed). The PR
    // builds its table in code (Serial.FF6.Management.ColosseumInfomationData, by wager content id):
    // its GetMonsterId / GetRewardItemId / IsRewardNameUnknown answer from the seed instead.
    static class WcColiseum
    {
        public static void Patch(Harmony h)
        {
            var t = typeof(Serial.FF6.Management.ColosseumInfomationData);
            int n = 0;
            foreach (var (name, post) in new[] { ("GetMonsterId", nameof(Monster_Postfix)), ("GetRewardItemId", nameof(Reward_Postfix)),
                                                 ("IsRewardNameUnknown", nameof(Hidden_Postfix)) })
            {
                var m = AccessTools.Method(t, name);
                if (m == null) continue;
                h.Patch(m, postfix: new HarmonyMethod(typeof(WcColiseum), post));
                n++;
            }
            Plugin.L.LogInfo($"[wc] coliseum: {n} of 3 lookups hooked");
        }

        internal static int MatchParty;   // the last match's party ("Win A Coliseum Match")

        static int[] Match(int wager)
        {
            var s = WcSeeds.Active;
            if (!Plugin.WcMode || s == null || s.Coliseum.Count == 0) return null;
            return s.Coliseum.TryGetValue(wager, out var m) ? m : null;
        }

        // Coliseum parties retargeted to another monster: party -> (slot, original monster)
        static readonly System.Collections.Generic.Dictionary<int, (int Slot, int Monster)> retargeted =
            new System.Collections.Generic.Dictionary<int, (int, int)>();

        static int Get(global::Last.Data.Master.MonsterParty p, int slot) => slot switch
        {
            1 => p.Monster1, 2 => p.Monster2, 3 => p.Monster3, 4 => p.Monster4, 5 => p.Monster5, 6 => p.Monster6, _ => 0,
        };

        static void Set(global::Last.Data.Master.MonsterParty p, int slot, int monster)
        {
            switch (slot)
            {
                case 1: p.Monster1 = monster; break;
                case 2: p.Monster2 = monster; break;
                case 3: p.Monster3 = monster; break;
                case 4: p.Monster4 = monster; break;
                case 5: p.Monster5 = monster; break;
                case 6: p.Monster6 = monster; break;
            }
        }

        static void Retarget(int partyId, int monster)
        {
            var p = global::Last.Data.Master.MasterManager.Instance.GetData<global::Last.Data.Master.MonsterParty>(partyId);
            if (p == null) return;
            if (!retargeted.TryGetValue(partyId, out var orig))
            {
                int slot = Enumerable.Range(1, 6).FirstOrDefault(i => Get(p, i) > 0);
                if (slot == 0) return;
                orig = (slot, Get(p, slot));
                retargeted[partyId] = orig;
            }
            Set(p, orig.Slot, monster);
        }

        static void Restore(int partyId)
        {
            if (!retargeted.TryGetValue(partyId, out var orig)) return;
            var p = global::Last.Data.Master.MasterManager.Instance.GetData<global::Last.Data.Master.MonsterParty>(partyId);
            if (p != null) Set(p, orig.Slot, orig.Monster);
            retargeted.Remove(partyId);
        }

        internal static string Last = "";   // tests: the game's own answer and ours
        internal static int GameMonster, GameReward;

        public static void Monster_Postfix(int selectItemContentId, ref int __result)
        {
            try
            {
                var m = Match(selectItemContentId);
                if (m == null) return;
                GameMonster = __result;
                if (m[2] > 0)
                {
                    Restore(m[2]);
                    Last = $"wager {selectItemContentId}: party game {__result} (vanilla table {m[0]}) -> {m[2]}";
                    __result = m[2];
                }
                else if (m[5] > 0)
                {
                    // no Coliseum copy of WC's opponent: the game's party for this wager fights it
                    Retarget(__result, m[5]);
                    Last = $"wager {selectItemContentId}: party game {__result} (vanilla table {m[0]}) keeps, monster -> {m[5]}";
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("coliseum-monster", e); }
            finally { MatchParty = __result; }
        }

        public static void Reward_Postfix(int selectItemContentId, ref int __result)
        {
            try
            {
                var m = Match(selectItemContentId);
                if (m == null) return;
                GameReward = __result;
                Last += $"; reward game {__result} (vanilla table {m[1]}) -> {m[3]}";
                __result = m[3];
            }
            catch (Exception e) { WcRunner.WarnOnce("coliseum-reward", e); }
        }

        public static void Hidden_Postfix(int selectItemContentId, ref bool __result)
        {
            try
            {
                var m = Match(selectItemContentId);
                if (m != null) __result = m[4] != 0;
            }
            catch (Exception e) { WcRunner.WarnOnce("coliseum-hidden", e); }
        }
    }
}
