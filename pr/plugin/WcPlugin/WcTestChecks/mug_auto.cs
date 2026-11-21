// The mug_auto check's plan: moved verbatim out of WcTest.cs's CheckPlans (roadmap item 5).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PrKit;
using Last.Battle;
using Last.Data.Master;
using Last.Data.User;
using Last.Interpreter;
using Last.Management;
using Last.Map;
using UnityEngine;
using Object = UnityEngine.Object;
using PrKit.Ff6;   // SnesAi: the kit's FF6 AI interpreter
using PrKit.Seeds;

namespace WorldsCollide.PR
{
    static partial class WcTest
    {
        static List<(float, Func<bool>)> Plan_mug_auto(string name)
        {
                // auto-battle with Mug as a character's first command and no Fight (the user's seed
                // gaadl68ix7j4: Gau, 2026-11-11, mugged himself): with no history the game builds
                // Fight, swaps it for Defend on the character itself (no Fight command), and
                // SnesAutobattle makes the default a Mug: at an enemy, never the character.
                // Then a second battle (2026-11-12, Gau "sits there with a full ATB bar, sometimes it
                // works"): its first replay starts from the first battle's Mug, aimed at a monster of
                // that battle; the character must go on mugging this battle's monsters.
                // Request "mug=old": the earlier patch's replay (the remembered act as it was), to
                // show the stall it left
                int lead = -1;
                var saved = new int[6];
                Il2CppSystem.Collections.Generic.List<Command> savedList = null;
                CharacterStatus row = null;
                var firstFoes = new List<BattleEnemyData>();
                int split = -1, waits = 0;
                bool requeued = false;
                SnesAutobattle.ReplayAsBefore = req != null && req.GetValueOrDefault("mug", "") == "old";
                BattleUnitData Me() => BattlePlugManager.Instance()?.GetPlayerUnits()?.ToArray()
                    .FirstOrDefault(u => u?.GetOwnedCharacterData()?.characterStatusId == lead);
                // The party never dies, nor does one monster (the anchor): a won fight waits on its
                // results screen (no input in a headless run); the forced end skips the results. In
                // the first battle (party 9: four monsters) the others die, so the character's
                // remembered Mug target dies under it; in the second every monster is kept
                int fight = 1, deathsAt = -1;
                IntPtr anchor = IntPtr.Zero;
                var dead = new HashSet<IntPtr>();
                bool toldList = false;
                void KeepAlive()
                {
                    var b = BattlePlugManager.Instance();
                    if (b == null || !b.IsBattle()) return;
                    var foes = b.GetEnemyUnits();
                    if (fight == 1 && anchor == IntPtr.Zero && foes != null && foes.Length > 0 && foes[foes.Length - 1] != null)
                        anchor = foes[foes.Length - 1].Pointer;
                    for (int i = 0; foes != null && i < foes.Length; i++)
                    {
                        var e = foes[i];
                        var ep = e?.BattleUnitDataInfo?.Parameter;
                        if (ep == null || fight != 1 || e.Pointer == anchor) continue;
                        if (ep.CurrentHP <= 0 && dead.Add(e.Pointer))
                        {
                            if (deathsAt < 0) deathsAt = WcBattles.Acts.Count;
                            Plugin.L.LogInfo($"[wc test] mug_auto: a monster died after {WcBattles.Acts.Count} acts");
                        }
                    }
                    // the battle reads the character's commands from the unit's own data, which the
                    // field-side change above didn't reach (the first runs: [1 26 19 ...], so the game's
                    // IsHaveMagic turned every replay down and only the default Mug ran); a seed's Mug
                    // is there, as the user's Gau's is
                    var mine = Me()?.GetOwnedCharacterData();
                    var cmds = mine?.CommandList;
                    if (cmds != null && (cmds.Count == 0 || cmds[0]?.Id != 11))
                    {
                        var list = new Il2CppSystem.Collections.Generic.List<Command>();
                        list.Add(MasterManager.Instance.GetData<Command>(11));
                        foreach (var c in cmds) if (c != null && c.Id != 1 && c.Id != 11) list.Add(c);
                        mine.CommandList = list;
                        if (!toldList)
                        {
                            toldList = true;
                            var field = UserDataManager.Instance()?.GetPartyMemberData()?.ToArray().FirstOrDefault(c => c?.characterStatusId == lead);
                            Plugin.L.LogInfo($"[wc test] mug_auto: the battle unit's commands were [{string.Join(" ", cmds.ToArray().Select(c => c?.Id ?? 0))}]; " +
                                             $"its data is the party's: {field != null && field.Pointer == mine.Pointer}");
                        }
                    }
                    var units = new List<BattleUnitData>();
                    foreach (var p in b.GetPlayerUnits()) units.Add(p);
                    foreach (var e in b.GetEnemyUnits())
                        if (e != null && (fight != 1 || e.Pointer == anchor)) units.Add(e);
                    foreach (var u in units)
                    {
                        var pr = u?.BattleUnitDataInfo?.Parameter;
                        if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999)
                        {
                            if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999;
                            pr.CurrentHP = 9999;
                        }
                    }
                }
                void Restore()
                {
                    recorder = null;
                    SnesAutobattle.ReplayAsBefore = false;
                    if (row != null)
                    {
                        row.CommandId1 = saved[0]; row.CommandId2 = saved[1]; row.CommandId3 = saved[2];
                        row.CommandId4 = saved[3]; row.CommandId5 = saved[4]; row.CommandId6 = saved[5];
                    }
                    var owned = UserDataManager.Instance()?.GetPartyMemberData()?.ToArray().FirstOrDefault(c => c?.characterStatusId == lead);
                    if (owned != null && savedList != null) owned.CommandList = savedList;
                }
                string Show(IEnumerable<(int Who, int Command, string Targets)> acts) =>
                    string.Join("; ", acts.Select(a => $"{a.Who}:{a.Command}->[{a.Targets}]"));
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        var party = UserDataManager.Instance()?.GetPartyMemberData();
                        var owned = party != null && party.Count > 0 ? party[0] : null;
                        if (owned == null) { Check(false, "mug_auto: no party"); return false; }
                        lead = owned.characterStatusId;
                        row = MasterManager.Instance.GetData<CharacterStatus>(lead);
                        saved = new[] { row.CommandId1, row.CommandId2, row.CommandId3, row.CommandId4, row.CommandId5, row.CommandId6 };
                        row.CommandId1 = 11;
                        if (row.CommandId2 == 1) row.CommandId2 = 0;
                        if (row.CommandId3 == 1) row.CommandId3 = 0;
                        if (row.CommandId4 == 1) row.CommandId4 = 0;
                        if (row.CommandId5 == 1) row.CommandId5 = 0;
                        if (row.CommandId6 == 1) row.CommandId6 = 0;
                        savedList = owned.CommandList;
                        var list = new Il2CppSystem.Collections.Generic.List<Command>();
                        list.Add(MasterManager.Instance.GetData<Command>(11));
                        if (savedList != null)
                            foreach (var c in savedList) if (c != null && c.Id != 1 && c.Id != 11) list.Add(c);
                        owned.CommandList = list;
                        WcBattles.Acts.Clear();
                        recorder = KeepAlive;
                        WcBattles.Queue(9);
                        Plugin.L.LogInfo($"[wc test] mug_auto: character {lead} has Mug first, no Fight; battle 9 queued");
                        return true;
                    }),
                    (8, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        Check(battle != null && battle.IsBattle(), "mug_auto: in battle");
                        var ui = Last.UI.BattleUIManager.Instance;
                        ui?.SetIsAuto(true);
                        Check(ui != null && ui.IsAuto(), "mug_auto: auto-battle on");
                        // a Mug aimed at the character itself, as the earlier default made it and
                        // its replays kept it: turned on an enemy
                        var me = Me();
                        if (me != null)
                        {
                            var fixedAct = SnesAutobattle.ReplayMug(null, MugAt(me, me), lead);
                            var t = fixedAct?.TargetUnitDatas;
                            Check(t != null && t.Length == 1 && SnesAutobattle.Muggable(t[0], battle.GetEnemyUnits()) && (int)fixedAct.CurrentTargetType == 3,
                                  $"mug_auto: a Mug aimed at itself is turned on an enemy (type {(fixedAct == null ? -1 : (int)fixedAct.CurrentTargetType)})");
                        }
                        else Check(false, "mug_auto: the character's battle unit");
                        firstFoes.Clear();
                        foreach (var e in battle?.GetEnemyUnits() ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BattleEnemyData>(0))
                            if (e != null) firstFoes.Add(e);
                        return true;
                    }),
                    (22, () =>
                    {
                        var mine = WcBattles.Acts.Where(a => a.Who == lead).ToList();
                        Plugin.L.LogInfo($"[wc test] mug_auto: battle 1 acts {Show(WcBattles.Acts)}");
                        var mugs = mine.Where(a => a.Command == 11).ToList();
                        Check(mugs.Count > 0, $"mug_auto: character {lead} mugged in auto-battle ({mine.Count} acts)");
                        Check(mugs.All(a => a.Targets.Length > 0 && a.Targets.Split(' ').All(t => t == "e")),
                              $"mug_auto: every Mug at an enemy ({string.Join(", ", mugs.Select(a => a.Targets))})");
                        // after the first monster died: the character kept its turns (a stalled one sits
                        // with a full gauge while the others go on acting)
                        if (deathsAt < 0) Check(false, "mug_auto: a monster died in the first battle");
                        else
                        {
                            var after = WcBattles.Acts.Skip(deathsAt).Where(a => a.Who > 0).ToList();
                            int last = after.FindLastIndex(a => a.Who == lead);
                            int trailing = last < 0 ? after.Count : after.Count - 1 - last;
                            Check(last >= 0 && trailing <= 4,
                                  $"mug_auto: character {lead} kept acting after monsters died ({after.Count(a => a.Who == lead)} of its acts after the first death; " +
                                  $"{trailing} by the others since its last; {dead.Count} died)");
                        }
                        split = WcBattles.Acts.Count;
                        // the judge honours isForce only when enabled and of the Normal type
                        var j = BattlePlugManager.Instance()?.BattleEndJugment;
                        if (j != null)
                        {
                            j.SetEnabled(true);
                            j.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal;
                            j.isForce = true;
                        }
                        return true;
                    }),
                };
                // the first battle's end (the results screen), then the second battle
                for (int w = 0; w < 8; w++)
                    steps.Add((3f, () =>
                    {
                        if (requeued) return true;
                        var b = BattlePlugManager.Instance();
                        if (b != null && b.IsBattle()) { waits++; return true; }
                        fight = 2;
                        WcBattles.Queue(7);
                        requeued = true;
                        Plugin.L.LogInfo($"[wc test] mug_auto: battle 1 over; battle 7 queued again");
                        return true;
                    }));
                steps.Add((10f, () =>
                {
                    var battle = BattlePlugManager.Instance();
                    bool on = requeued && battle != null && battle.IsBattle();
                    Check(on, $"mug_auto: a second battle ({(requeued ? "queued" : $"the first still on after {waits * 3} s")})");
                    if (!on) { Restore(); return false; }
                    Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                    // the history's Mug, aimed at a monster of the first battle: re-aimed at one of this battle
                    var me = Me();
                    var stale = firstFoes.FirstOrDefault();
                    if (me != null && stale != null)
                    {
                        var fixedAct = SnesAutobattle.ReplayMug(null, MugAt(me, stale), lead);
                        var t = fixedAct?.TargetUnitDatas;
                        Check(!SnesAutobattle.Muggable(stale, battle.GetEnemyUnits()) && t != null && t.Length == 1 &&
                              SnesAutobattle.Muggable(t[0], battle.GetEnemyUnits()),
                              "mug_auto: a Mug aimed at the first battle's monster is turned on one of this battle");
                    }
                    else Check(false, $"mug_auto: the character's battle unit ({me != null}) and the first battle's monsters ({firstFoes.Count})");
                    return true;
                }));
                steps.Add((20f, () =>
                {
                    var after = WcBattles.Acts.Skip(Math.Max(0, split)).ToList();
                    Plugin.L.LogInfo($"[wc test] mug_auto: battle 2 acts {Show(after)}");
                    var mugs = after.Where(a => a.Who == lead && a.Command == 11).ToList();
                    Check(mugs.Count > 0 && mugs.All(a => a.Targets.Length > 0 && a.Targets.Split(' ').All(t => t == "e")),
                          $"mug_auto: character {lead} went on mugging in the second battle ({mugs.Count} Mugs: {string.Join(", ", mugs.Select(a => a.Targets))}; " +
                          $"{after.Count(a => a.Who != lead)} acts by the others)");
                    Restore();
                    return false;
                }));
                return steps;
            }
    }
}
