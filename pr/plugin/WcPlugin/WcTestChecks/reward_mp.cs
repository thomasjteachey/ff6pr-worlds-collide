// The reward_mp check's plan (2026-11-21): what a won battle's magic points pay with -mpm.
using System;
using System.Collections.Generic;
using System.Linq;
using Last.Battle;
using Last.Data.Master;
using Last.Management;

namespace WorldsCollide.PR
{
    static partial class WcTest
    {
        // -mpm (WcRewards): a won battle's magic points reach the equipped esper's spells multiplied, once,
        // the morph gauge gets the plain amount, and the result screen shows what the espers got. An ordinary
        // party with magic points (formation 7 if it has them and no battle flags, else the first such, the
        // multiplied amount under WC's 255 cap if one is), a standing party member with an esper that still
        // teaches something (the one equipped, else one equipped here), the party kept alive on auto, the
        // monsters at 1 HP. Checked: the amount the learning got = the battle's x -mpm (at most 255); each of
        // the esper's unlearned spells = before + learn rate (Learning value2) x that, to 100; each standing
        // character's MagicLearningValue (morph) gain = the plain amount (WC: that / -mpm); the result's GetMp =
        // the multiplied amount. The PR's own Boost settings as they are (x1 on a new game).
        static List<(float, Func<bool>)> Plan_reward_mp(string name)
        {
            int party = 0, winsAt = 0, holder = -1, stone = 0;
            bool won = false;
            var morphBefore = new Dictionary<int, int>();
            var levelBefore = new Dictionary<int, int>();
            var spells = new List<(int ability, int rate)>();
            int Cheat(int v)
            {
                try { var c = CheatSettingsClient.Instance(); return c != null ? c.GetResultMagicProficiency(v) : v; }
                catch (Exception) { return v; }
            }
            bool Plain(MonsterParty p)
            {
                if (p == null || p.BattleFlagGroupId > 0 || p.GetValue <= 0) return false;
                var ms = new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6 }.Where(m => m > 0)
                    .Select(m => MasterManager.Instance.GetData<Monster>(m)).ToList();
                return ms.Count > 0 && ms.All(m => m != null && m.Exp > 0);
            }
            Dictionary<int, global::Last.Data.User.OwnedCharacterData> Owned()
            {
                var d = new Dictionary<int, global::Last.Data.User.OwnedCharacterData>();
                foreach (var c in UserDataManager.Instance().OwnedCharacterList) if (c != null) d[c.Id] = c;
                return d;
            }
            // an esper's spells (Learning type 2: value1 the esper's content, value2 the learn rate)
            List<(int ability, int rate)> SpellsOf(int esper)
            {
                var list = new List<(int ability, int rate)>();
                if (esper <= 0) return list;
                foreach (var kv in MasterManager.Instance.GetList<Learning>())
                {
                    var r = kv.Value;
                    if (r == null || r.TypeId != 2 || r.Value1 != esper || r.Value2 <= 0) continue;
                    var content = MasterManager.Instance.GetData<Content>(r.ContentId);
                    if (content == null || content.TypeId != 4) continue;
                    list.Add((content.TypeValue, r.Value2));
                }
                return list;
            }
            int Level(global::Last.Data.User.OwnedCharacterData c, int ability)
            {
                if (c?.OwnedAbilityList == null) return 0;
                foreach (var a in c.OwnedAbilityList) if (a?.Ability != null && a.Ability.Id == ability) return a.SkillLevel;
                return 0;
            }
            bool Teaches(global::Last.Data.User.OwnedCharacterData c, int esper) => SpellsOf(esper).Any(sp => Level(c, sp.ability) < 100);
            bool Report()
            {
                recorder = null;
                var udm = UserDataManager.Instance();
                int m = WcRewards.MpMultiplier, b = WcRewards.LastMpBase;
                int paid = WcRewards.MpPaid(b, m), morph = WcRewards.MpMorph(b, m);
                Check(won, $"reward_mp: party {party}'s battle was won");
                if (!won) return false;
                Check(b > 0 && WcRewards.LastMpPaid == paid && WcRewards.LastMpPaidTimes > 0,
                      $"reward_mp: the battle's {b} magic points x{m} = {paid} (WC: at most 255); the esper learning got {WcRewards.LastMpPaid} " +
                      $"({WcRewards.LastMpPaidTimes} character(s)), the morph gauge's read {WcRewards.LastMpMorph}");
                var owned = Owned();
                int cp = Cheat(paid), plain = Cheat(b);
                if (owned.TryGetValue(holder, out var hc))
                {
                    var rows = spells.Where(sp => levelBefore.ContainsKey(sp.ability) && levelBefore[sp.ability] < 100)
                        .Select(sp => (sp.ability, sp.rate, before: levelBefore[sp.ability], after: Level(hc, sp.ability))).ToList();
                    Check(rows.Count > 0 && rows.All(x => x.after == Math.Min(100, x.before + x.rate * cp)),
                          $"reward_mp: character {hc.characterStatusId}'s esper {stone}, each spell + rate x {cp}: " +
                          string.Join(", ", rows.Select(x => $"ability {x.ability} x{x.rate} {x.before} -> {x.after} (want {Math.Min(100, x.before + x.rate * cp)}; " +
                                                              $"unmultiplied {Math.Min(100, x.before + x.rate * plain)})")));
                }
                else Check(false, $"reward_mp: character id {holder} (the esper's) not found after the battle");
                var standing = udm.GetPartyMemberData().ToArray().Where(c => c != null).Select(c => c.Id)
                    .Where(id => morphBefore.ContainsKey(id) && owned.TryGetValue(id, out var c) && c.Parameter.CurrentHP > 0).ToList();
                int cm = Cheat(morph);
                var gains = standing.Select(id => owned[id].MagicLearningValue - morphBefore[id]).ToList();
                Check(gains.Count > 0 && gains.All(g => g == cm),
                      $"reward_mp: the morph gauge (MagicLearningValue) got the battle's {b} (WC: {paid} / {m} = {morph}, shown x boost {cm}), " +
                      $"not x{m}: [{string.Join(" ", gains)}]");
                Check(WcRewards.LastResultMp == cp,
                      $"reward_mp: the result screen shows {WcRewards.LastResultMp} magic points, what the espers got {cp}");
                return false;
            }
            var steps = new List<(float, Func<bool>)>
            {
                (0f, () =>
                {
                    int m = WcRewards.MpMultiplier;
                    bool Fits(MonsterParty p) => Plain(p) && (m <= 1 || p.GetValue * m <= 255);
                    var seven = MasterManager.Instance.GetData<MonsterParty>(7);
                    party = Fits(seven) ? 7 : 0;
                    if (party == 0)
                        foreach (var kv in MasterManager.Instance.GetList<MonsterParty>())
                            if (kv.Value != null && kv.Value.Id > 0 && kv.Value.Id < 400 && Fits(kv.Value)) { party = kv.Value.Id; break; }
                    if (party == 0)
                        foreach (var kv in MasterManager.Instance.GetList<MonsterParty>())
                            if (kv.Value != null && kv.Value.Id > 0 && kv.Value.Id < 400 && Plain(kv.Value)) { party = kv.Value.Id; break; }
                    Check(party > 0, $"reward_mp: an ordinary party with magic points: {party}");
                    if (party <= 0) return false;
                    var udm = UserDataManager.Instance();
                    var members = udm.GetPartyMemberData().ToArray().Where(c => c != null && c.Parameter != null && c.Parameter.CurrentHP > 0).ToList();
                    // the esper: one a party member has on that still teaches something, else one equipped here
                    var who = members.FirstOrDefault(c => c.MagicStoneId > 0 && Teaches(c, c.MagicStoneId));
                    if (who == null)
                    {
                        var s = WcSeeds.Active;
                        var esperSet = new SortedSet<int>();
                        foreach (var kv in MasterManager.Instance.GetList<Learning>())
                            if (kv.Value != null && kv.Value.TypeId == 2 && kv.Value.Value1 > 0 && kv.Value.Value2 > 0) esperSet.Add(kv.Value.Value1);
                        var espers = esperSet.ToList();
                        var client = new OwnedCharacterClient();
                        foreach (var c in members.Where(c => c.characterStatusId >= 1 && c.characterStatusId <= 12))
                        {
                            foreach (int e in espers)
                            {
                                if (s != null && s.EsperEquip.TryGetValue(e, out var allowed) && !allowed.Contains(c.characterStatusId)) continue;
                                if (!Teaches(c, e)) continue;
                                try { client.SetMagicStone(c.Id, e); } catch (Exception) { }
                                var now = Owned().TryGetValue(c.Id, out var cc) ? cc : c;
                                if (now.MagicStoneId != e) now.MagicStoneId = e;   // (magicite not held on a new game)
                                if (now.MagicStoneId == e) { who = now; break; }
                            }
                            if (who != null) break;
                        }
                    }
                    Check(who != null, $"reward_mp: a party member with an esper that teaches something: " +
                                       (who != null ? $"character {who.characterStatusId}, esper {who.MagicStoneId}" : "none"));
                    if (who == null) return false;
                    holder = who.Id;
                    stone = who.MagicStoneId;
                    spells.Clear();
                    spells.AddRange(SpellsOf(stone));
                    levelBefore.Clear();
                    foreach (var sp in spells) levelBefore[sp.ability] = Level(who, sp.ability);
                    morphBefore.Clear();
                    foreach (var c in udm.OwnedCharacterList) if (c != null) morphBefore[c.Id] = c.MagicLearningValue;
                    WcRewards.LastMpBase = WcRewards.LastMpPaid = WcRewards.LastMpMorph = WcRewards.LastResultMp = -1;
                    WcRewards.LastMpPaidTimes = 0;
                    winsAt = WcScaling.Victories;
                    recorder = () =>
                    {
                        var b = BattlePlugManager.Instance();
                        if (b == null || !b.IsBattle()) return;
                        foreach (var p in b.GetPlayerUnits())
                        {
                            var pr = p?.BattleUnitDataInfo?.Parameter;
                            if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999) { if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999; pr.CurrentHP = 9999; }
                        }
                    };
                    WcBattles.Queue(party);
                    Plugin.L.LogInfo($"[wc test] reward_mp: party {party} queued (magic points {MasterManager.Instance.GetData<MonsterParty>(party)?.GetValue}), " +
                                     $"-mpm x{m}; character {who.characterStatusId} with esper {stone}: " +
                                     string.Join(", ", spells.Select(sp => $"ability {sp.ability} x{sp.rate} at {levelBefore[sp.ability]}")));
                    return true;
                }),
            };
            for (int k = 0; k < 20; k++)
            {
                bool last = k == 19;
                steps.Add((3f, () =>
                {
                    if (WcScaling.Victories > winsAt) { won = true; return Report(); }
                    if (last) return Report();
                    var b = BattlePlugManager.Instance();
                    if (b == null || !b.IsBattle()) return true;
                    var ui = Last.UI.BattleUIManager.Instance;
                    if (ui != null && !ui.IsAuto()) ui.SetIsAuto(true);
                    foreach (var e in b.GetEnemyUnits())
                    {
                        var pr = e?.BattleUnitDataInfo?.Parameter;
                        if (pr != null && pr.CurrentHP > 1) pr.CurrentHP = 1;   // the party's next hits win it
                    }
                    return true;
                }));
            }
            return steps;
        }
    }
}
