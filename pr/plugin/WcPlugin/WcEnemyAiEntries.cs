using System;
using System.Collections.Generic;
using Last.Battle;

namespace WorldsCollide.PR
{
    // WcEnemyAi, part 4: monsters coming and going: WC's AI command F5 and the formations' hidden members.
    // F5 anim mode mask, as the SNES's action handler (MONSTER_ENTRY_EXIT, everything8215 action_2.asm)
    // does it: mode 0 restore (full HP, then show), 1 kill (untargetable, then hide), 2 show, 3 hide,
    // 4 hide with the battle kept from ending until a show, 5 hide with Wound; the mask is SNES monster
    // slots (0: the monster itself, resolved by the interpreter). A formation's byte 1 is its
    // shown-at-start mask: members outside it start hidden (the seed's party_members: each slot's
    // monster, cell, shown at start, whether the PR party lists it).
    // The PR's way: a member the party doesn't list comes in with EventCommand.InBattleEnemy into its cell
    // (Square's AddEnemy abilities do the same: BattleFunction20 clears the cell's old enemy data first);
    // one that leaves gets the Gone condition (the PR's Escape), lifted when it comes back.
    static partial class WcEnemyAi
    {
        internal const int GoneCondition = 3;   // Escape: out of the battle
        // the PR's own ways of keeping a member out (Hide, NoSelect, NotTargeting, Chadarnook's NoSelectChad,
        // EventFadeOut): a member WC's script shows comes back from all of them
        static readonly int[] OutConditions = { GoneCondition, 416, 625, 667, 673, 675 };

        static void LiftOut(EventCommand ec, BattleEnemyData unit)
        {
            foreach (int c in OutConditions)
                if (HasCondition(unit, c)) ec.RecoveryConditionEnemy(unit.UniqueId, c);
        }
        internal static int Entries;             // for the self-test and the log
        internal static string LastEntry = "";
        static int startHidesFor = -1;

        static BattleEnemyData UnitAt(Fight f, int slot)
        {
            var bm = BattlePlugManager.Instance();
            if (bm == null) return null;
            BattleEnemyData dead = null;   // a living one first: a cell can hold a dead one and its replacement
            foreach (var e in bm.GetEnemyUnits())
            {
                var ed = e?.TryCast<BattleEnemyData>();
                if (ed == null || !f.Slot.TryGetValue(ed.UniqueId, out int s) || s != slot) continue;
                if (!IsDead(ed)) return ed;
                dead = dead ?? ed;
            }
            return dead;
        }

        // the sprite (and its shadow) on or off: the Gone condition keeps a monster out of the battle (no
        // turns, no targeting: entry_exit_probe) but leaves it drawn
        static void SetVisible(int uniqueId, bool on)
        {
            try
            {
                var ent = BattlePlugManager.Instance()?.EventCommand?.GetEnemyEntity(uniqueId);
                if (ent == null) return;
                foreach (var r in ent.gameObject.GetComponentsInChildren<UnityEngine.Renderer>(true))
                    if (r != null) r.enabled = on;
            }
            catch (Exception e) { WcRunner.WarnOnce("enemy-ai-visible", e); }
        }

        // "everyone dies like a boss" (F5 xx 01/05 FF): the PR's erase-all, which ends the fight
        static bool EraseAllIn(List<SnesAiAction> entries)
        {
            foreach (var e in entries) if ((e.B2 == 1 || e.B2 == 5) && e.B3 == 0xFF) return true;
            return false;
        }

        // no monster left shown and alive (and no F5 mode 4 holding the battle open): the SNES ends the
        // battle there; the PR's erase-all does it here
        static bool AllGone(Fight f)
        {
            if (f.HoldEnd) return false;
            var bm = BattlePlugManager.Instance();
            if (bm == null) return false;
            foreach (var e in bm.GetEnemyUnits())
            {
                var ed = e?.TryCast<BattleEnemyData>();
                if (ed == null || IsDead(ed)) continue;
                if (f.Slot.TryGetValue(ed.UniqueId, out int s) && f.Hidden.Contains(s)) continue;
                return false;
            }
            return true;
        }

        // the battle kept from ending (F5 mode 4; a death that brings others in), for at most `seconds`
        static void Hold(Fight f, BattlePlugManager bm, double seconds)
        {
            bm?.BattleEndJugment?.SetEnabled(false);
            f.HoldEnd = true;
            f.HoldUntil = Math.Max(f.HoldUntil, f.BattleTime + seconds);
        }

        static void RunDeferred(Fight f)
        {
            var bm = BattlePlugManager.Instance();
            if (bm == null) return;
            if (f.HoldEnd && f.BattleTime > f.HoldUntil)
            {
                bm.BattleEndJugment?.SetEnabled(true);   // nothing came: the battle may end again
                f.HoldEnd = false;
            }
            if (f.Deferred.Count == 0) return;
            for (int i = f.Deferred.Count - 1; i >= 0; i--)
            {
                var d = f.Deferred[i];
                bool gone = true;
                foreach (var e in bm.GetEnemyUnits())
                    if (e != null && d.unit != null && e.Pointer == d.unit.Pointer) gone = false;
                if (!gone && f.BattleTime < d.by) continue;
                f.Deferred.RemoveAt(i);
                try { ApplyEntries(f, d.slot, d.snesId, d.entries); }
                catch (Exception e) { WcRunner.WarnOnce("enemy-ai-deferred", e); }
            }
        }

        static void Refill(BattleEnemyData u)
        {
            var p = u?.BattleUnitDataInfo?.Parameter;
            if (p != null) p.CurrentHP = p.ConfirmedMaxHp();
        }

        static void ApplyEntries(Fight f, int self, int snesId, List<SnesAiAction> entries)
        {
            if (entries.Count == 0) return;
            var bm = BattlePlugManager.Instance();
            var ec = bm?.EventCommand;
            if (ec == null) return;
            foreach (var a in entries)
            {
                int mode = a.B2, mask = a.B3 & 0x3F;
                var did = new List<string>();
                for (int s = 0; s < 6; s++)
                {
                    if ((mask >> s & 1) == 0) continue;
                    var unit = UnitAt(f, s);
                    bool alive = unit != null && !IsDead(unit);
                    if (mode == 0 || mode == 2)
                    {
                        if (alive && !f.Hidden.Contains(s))
                        {
                            if (mode == 0) Refill(unit);
                            did.Add($"{s}:here");
                        }
                        else if (alive)
                        {
                            LiftOut(ec, unit);
                            SetVisible(unit.UniqueId, true);
                            if (mode == 0) Refill(unit);
                            f.Hidden.Remove(s);
                            did.Add($"{s}:back");
                        }
                        else did.Add(Bring(f, ec, bm, s, unit));
                        if (f.HoldEnd && f.Creating.Count == 0) { bm.BattleEndJugment?.SetEnabled(true); f.HoldEnd = false; }
                    }
                    else
                    {
                        if (!alive || f.Hidden.Contains(s)) continue;
                        ec.AddConditionEnemy(unit.UniqueId, GoneCondition);
                        SetVisible(unit.UniqueId, false);
                        f.Hidden.Add(s);
                        did.Add($"{s}:gone");
                        if (mode == 4) Hold(f, bm, 30);
                    }
                }
                Entries++;
                LastEntry = $"monster {snesId} slot {self}: {a} -> {string.Join(" ", did)}";
                if (WcTest.Running) Plugin.L.LogInfo("[wc] enemy AI entry/exit: " + LastEntry);
            }
        }

        // a member brought in the PR's way: into its cell, a dead one's data there cleared first (AddEnemy's
        // CreateEnemy does the same); the new unit joins the fight as that slot when it appears
        static string Bring(Fight f, EventCommand ec, BattlePlugManager bm, int s, BattleEnemyData dead)
        {
            var m = f.Members != null && s < f.Members.Length ? f.Members[s] : null;
            int cell = m != null ? m[1] : dead?.UniqueId ?? 0;
            int id = m != null ? m[0] : dead != null && f.SnesId.TryGetValue(dead.UniqueId, out int sid) ? sid : -1;
            if (cell <= 0 || id < 0) return $"{s}:unknown";
            f.Slot.Remove(cell);
            f.SnesId.Remove(cell);
            if (f.CellSlots != null && cell < f.CellSlots.Length) f.CellSlots[cell] = s;
            f.Creating.Enqueue((id + 1, cell));
            f.Hidden.Remove(s);
            return $"{s}:in({id + 1}@{cell})";
        }

        // the queued members, one at a time: the next once the last is on the field (or 3 s on)
        static void PumpCreations(Fight f)
        {
            var bm = BattlePlugManager.Instance();
            var ec = bm?.EventCommand;
            if (ec == null) return;
            if (f.InFlightCell > 0)
            {
                bool here = false;
                foreach (var e in bm.GetEnemyUnits())
                {
                    var ed = e?.TryCast<BattleEnemyData>();
                    if (ed != null && ed.UniqueId == f.InFlightCell && !IsDead(ed)) here = true;
                }
                if (!here && f.BattleTime - f.InFlightSince < 3) return;
                f.InFlightCell = 0;
                if (f.Creating.Count == 0 && f.HoldEnd) { bm.BattleEndJugment?.SetEnabled(true); f.HoldEnd = false; }
            }
            if (f.Creating.Count == 0) return;
            var (prId, cell) = f.Creating.Dequeue();
            try { bm.BattleStatusControl?.BattleStatusControlInfo?.RemoveEnemyData(cell); }   // a dead one there (AddEnemy does it)
            catch (Exception e) { WcRunner.WarnOnce("enemy-ai-remove", e); }
            ec.InBattleEnemy(prId, cell);
            f.InFlightCell = cell;
            f.InFlightSince = f.BattleTime;
        }

        // the battle's opening: members the PR party lists but the SNES formation hides from the start
        // (Chadarnook's demon, one of Ifrit's Shivas, the three hidden Ultroses) leave at once
        static void StartHides()
        {
            if (!Active || startHidesFor == battleSerial) return;
            var bm = BattlePlugManager.Instance();
            if (bm == null || !bm.IsBattle() || bm.GetEnemyUnits().Length == 0) return;
            startHidesFor = battleSerial;
            var f = EnsureFight();
            if (f.Members == null || bm.EventCommand == null) return;
            // only where WC's scripts run the fight (a fight on the PR's own AI expects its members)
            var plugin = WcSeeds.Active?.SnesAi?.Plugin;
            for (int s = 0; s < f.Members.Length; s++)
                if (f.Members[s] != null && (plugin == null || !plugin.Contains(f.Members[s][0]))) return;
            for (int s = 0; s < f.Members.Length; s++)
            {
                var m = f.Members[s];
                if (m == null || m[2] != 0 || m[3] != 1) continue;
                var unit = UnitAt(f, s);
                if (unit == null) continue;
                bm.EventCommand.AddConditionEnemy(unit.UniqueId, GoneCondition);
                SetVisible(unit.UniqueId, false);
                if (WcTest.Running) Plugin.L.LogInfo($"[wc] enemy AI: slot {s} (monster {m[0]}, cell {m[1]}) hidden from the start");
            }
        }
    }
}
