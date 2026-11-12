using System;
using System.Collections.Generic;
using System.Linq;
using Last.Battle;
using Last.Data.Master;
using UnityEngine;

namespace WorldsCollide.PR
{
    // Every monster runs its Worlds Collide script the way the SNES runs it (the user, 2026-11-12: "please
    // make all enemies/bosses behave like WC SNES"): the seed carries each monster's SNES AI (main and
    // counter sections, tools/port_ai.py snes_ai_data), SnesAi.cs is FF6's interpreter, and this file is
    // the battle it looks at plus the hooks that turn its actions into the PR's acts.
    //
    // This part: the battle as the interpreter sees it (ISnesAiBattle). Units 0-3 are the party slots
    // (BattlePlayerData.corpsIndex), 4-9 the monster slots in the formation's order; FF6's battle timer
    // and monster timers tick every 16 frames of battle time ($3A44, MonsterTimer: C2/5BFC); variables
    // $00-$23 are the battle's, shared by its monsters (each monster has its own byte for $24 and up).
    static partial class WcEnemyAi
    {
        const double SnesFps = 60.0988, TickSeconds = 16 / SnesFps;

        // SNES status bit (status 1-4, 0-31) -> PR ConditionType (-1: none; NearFatal is HP-based)
        static readonly int[] StatusCondition =
        {
            9, 34, 10, 607, 604, 601, 11, 5,           // Dark, Zombie, Poison, Magitek, Clear, Imp, Petrify, Wound
            404, -2, 14, 6, 410, 12, 407, 7,            // Condemned, Near Fatal, Image, Mute, Berserk, Muddle, Sap, Sleep
            619, 25, 16, 15, 17, 412, 19, 413,          // Dance, Regen, Slow, Haste, Stop, Shell, Safe, Reflect
            616, 603, 602, 621, 421, 416, -1, 409,      // Rage, Frozen, Reraise, Morph, Chant, Hidden, Interceptor, Float
        };

        internal sealed class Fight
        {
            public readonly Dictionary<int, SnesAiMonster> Monsters = new Dictionary<int, SnesAiMonster>();  // by UniqueId
            public readonly Dictionary<int, int> Slot = new Dictionary<int, int>();       // UniqueId -> SNES slot 0-5
            public readonly Dictionary<int, int> SnesId = new Dictionary<int, int>();     // UniqueId -> SNES monster id
            public readonly byte[] Vars = new byte[0x24];
            public readonly double[] MonsterTime = new double[6];
            public double BattleTime;
            public int Formation = -1;
            public int[] CellSlots;   // the seed's SNES slot of each party cell (null: by cell order)
            public int[][] Members;   // the seed's member of each SNES slot: {SNES id, cell, shown at start, listed} (null: unknown)
            public readonly HashSet<int> Hidden = new HashSet<int>();   // SNES slots hidden (F5 hide/kill, or hidden from the start)
            public bool HoldEnd;   // F5 mode 4: the battle can't end until a monster is shown
            public double HoldUntil;   // a hold's safety limit (battle time): nothing keeps a battle open for good
            // a death's entries/exits, applied once the dying monster has left the field (the PR is still
            // running its death when FinalAttackAct asks): (the dying unit, its slot, its SNES id, the
            // entries, the battle time after which they apply anyway)
            public readonly List<(BattleEnemyData unit, int slot, int snesId, List<SnesAiAction> entries, double by)> Deferred =
                new List<(BattleEnemyData, int, int, List<SnesAiAction>, double)>();
            // members coming in, one at a time (InBattleEnemy keeps one pending load: a second call drops the first)
            public readonly Queue<(int prId, int cell)> Creating = new Queue<(int, int)>();
            public int InFlightCell;
            public double InFlightSince;
            // what last hit each monster slot (its counter script reads it)
            public readonly int[] Retal = Enumerable.Repeat(-1, 6).ToArray();
            public readonly int[] RetalCmdUnit = Enumerable.Repeat(-1, 6).ToArray();
            public readonly int[] RetalCmd = new int[6], RetalAtk = new int[6], RetalItm = new int[6];
            public readonly int[] RetalElemUnit = Enumerable.Repeat(-1, 6).ToArray();
            public readonly int[] RetalElem = new int[6];
            public readonly int[] PrevAttacker = Enumerable.Repeat(-1, 6).ToArray();
            public System.Random Rng = new System.Random();
        }

        internal static Fight Current;

        // the battle's units by SNES unit index (0-3 party slots, 4-9 monster slots); null where empty
        internal static BattleUnitData[] Units()
        {
            var u = new BattleUnitData[10];
            var bm = BattlePlugManager.Instance();
            if (bm == null) return u;
            try
            {
                foreach (var p in bm.GetPlayerUnits())
                {
                    var pd = p?.TryCast<BattlePlayerData>();
                    if (pd != null && pd.corpsIndex >= 0 && pd.corpsIndex < 4) u[pd.corpsIndex] = pd;
                }
                var f = Current;
                foreach (var e in bm.GetEnemyUnits())
                {
                    var ed = e?.TryCast<BattleEnemyData>();
                    if (ed == null || f == null) continue;
                    if (!f.Slot.ContainsKey(ed.UniqueId)) Join(f, ed);   // brought in since (F5)
                    if (!f.Slot.TryGetValue(ed.UniqueId, out int s)) continue;
                    u[4 + s] = ed;
                }
            }
            catch (Exception ex) { WcRunner.WarnOnce("enemy-ai-units", ex); }
            return u;
        }

        static bool HasCondition(BattleUnitData u, int type)
        {
            var list = u?.BattleUnitDataInfo?.Parameter?.CurrentConditionList;
            if (list == null) return false;
            foreach (var c in list)
                if (c != null && (int)c.ConditionType == type) return true;
            return false;
        }

        static bool IsDead(BattleUnitData u)
        {
            var p = u?.BattleUnitDataInfo?.Parameter;
            if (p == null) return true;
            return p.CurrentHP <= 0 || HasCondition(u, 5) || HasCondition(u, 11);   // Wound / Petrify
        }

        // the battle as FF6's AI sees it
        internal sealed class View : ISnesAiBattle
        {
            readonly Fight f;
            readonly BattleUnitData[] u;
            public View(Fight fight, BattleUnitData[] units) { f = fight; u = units; }

            public int Rand(int n) => n <= 1 ? 0 : f.Rng.Next(n);

            public int CharActor(int slot)
            {
                var pd = u[slot]?.TryCast<BattlePlayerData>();
                int id = pd?.ownedCharacterData?.characterStatusId ?? 0;
                return id >= 1 && id <= 14 ? id - 1 : (id > 0 ? id : -1);   // PR character 1-14 = SNES actor 0-13
            }

            bool Out(int unit) => unit >= 4 && f.Hidden.Contains(unit - 4);

            public bool MonsterPresent(int slot) => u[4 + slot] != null && !IsDead(u[4 + slot]) && !HasCondition(u[4 + slot], 416) && !Out(4 + slot);

            public int TargetableMask()
            {
                int m = 0;
                for (int i = 0; i < 10; i++)
                    if (u[i] != null && !IsDead(u[i]) && !HasCondition(u[i], 416) && !HasCondition(u[i], 420) && !Out(i)) m |= SnesAi.MaskOf(i);
                return m;
            }

            public bool Dead(int unit) => u[unit] != null && IsDead(u[unit]);

            public int Status(int unit, int word)
            {
                var x = u[unit];
                if (x == null) return 0;
                int w = 0;
                for (int b = 0; b < 16; b++)
                {
                    int bit = word * 16 + b, type = StatusCondition[bit];
                    bool on = type == -2 ? NearFatal(x) : type > 0 && HasCondition(x, type);
                    if (on) w |= 1 << b;
                }
                return w;
            }

            static bool NearFatal(BattleUnitData x)
            {
                var p = x?.BattleUnitDataInfo?.Parameter;
                return p != null && p.CurrentHP > 0 && p.CurrentHP * 8 <= p.ConfirmedMaxHp();
            }

            public int Hp(int unit) => u[unit]?.BattleUnitDataInfo?.Parameter?.CurrentHP ?? 0;
            public int Mp(int unit) => u[unit]?.BattleUnitDataInfo?.Parameter?.CurrentMP ?? 0;
            public int Level(int unit) => u[unit]?.BattleUnitDataInfo?.Parameter?.ConfirmedLevel() ?? 0;
            public int WeakElements(int unit)    // monsters: their data's weak elements (characters: none)
            {
                if (unit < 4 || u[unit] == null) return 0;
                var ed = u[unit].TryCast<BattleEnemyData>();
                return ed != null && f.SnesId.TryGetValue(ed.UniqueId, out int id) && WcSeeds.Active?.SnesAi?.Weak.TryGetValue(id, out int w) == true ? w : 0;
            }
            public bool CharPresentFlag(int slot) => u[slot] != null;
            public int BattleTimer() => (int)(f.BattleTime / TickSeconds);
            public int MonsterTimer(int unit) => unit >= 4 ? (int)(f.MonsterTime[unit - 4] / TickSeconds) : 0;
            public int Formation() => f.Formation;
            public int CharsAlive() => Enumerable.Range(0, 4).Count(s => u[s] != null && !IsDead(u[s]));
            public int MonstersAlive() => Enumerable.Range(4, 6).Count(s => u[s] != null && !IsDead(u[s]) && !Out(s));
            public int MonsterTypesAlive()
            {
                var kinds = new HashSet<int>();
                for (int s = 4; s < 10; s++)
                    if (u[s] != null && !IsDead(u[s]) && !Out(s)) kinds.Add(u[s].GetMonster()?.Id ?? 0);
                return kinds.Count;
            }
            public int MonstersInBattleMask()
            {
                int m = 0;
                for (int s = 0; s < 6; s++) if (u[4 + s] != null || (f.Members != null && s < f.Members.Length && f.Members[s] != null)) m |= 1 << s;
                return m;
            }
            public int MonstersAliveMask()
            {
                int m = 0;
                for (int s = 0; s < 6; s++) if (u[4 + s] != null && !IsDead(u[4 + s]) && !Out(4 + s)) m |= 1 << s;
                return m;
            }
            public bool GauRecruited()
            {
                try
                {
                    foreach (var c in Last.Management.UserDataManager.Instance().OwnedCharacterList)
                        if (c?.characterStatusId == 12) return true;
                }
                catch (Exception) { }
                return false;
            }
            public byte BattleVar(int i) => f.Vars[i];
            public int RetalTarget(int unit) => unit >= 4 ? f.Retal[unit - 4] : -1;
            public int RetalCmdTarget(int unit) => unit >= 4 ? f.RetalCmdUnit[unit - 4] : -1;
            public int RetalCommand(int unit) => unit >= 4 ? f.RetalCmd[unit - 4] : 0;
            public int RetalAttack(int unit) => unit >= 4 ? f.RetalAtk[unit - 4] : 0;
            public int RetalItem(int unit) => unit >= 4 ? f.RetalItm[unit - 4] : 0;
            public int RetalElemTarget(int unit) => unit >= 4 ? f.RetalElemUnit[unit - 4] : -1;
            public int RetalElements(int unit) => unit >= 4 ? f.RetalElem[unit - 4] : 0;
            public int PrevAttacker(int unit) => unit >= 4 ? f.PrevAttacker[unit - 4] : -1;
            public int TargettingTarget(int unit) => -1;
            public void ClearTargettingTarget(int unit) { }
        }

        // FF6's timers run on battle time (the PR stops time while an action plays; the SNES doesn't: close
        // enough for the scripts' "after N seconds" checks)
        internal static void Update()
        {
            var bm = BattlePlugManager.Instance();
            if (bm == null || !bm.IsBattle()) return;
            StartHides();
            if (Current == null || fightSerial != battleSerial) return;   // the last battle's fight: nothing of it carries over
            Tick(Time.deltaTime);
            RunDeferred(Current);
            PumpCreations(Current);
        }

        internal static void Tick(float dt)
        {
            var f = Current;
            if (f == null) return;
            f.BattleTime += dt;
            var u = Units();
            for (int s = 0; s < 6; s++)
                if (u[4 + s] != null && !IsDead(u[4 + s]) && !f.Hidden.Contains(s)) f.MonsterTime[s] += dt;
        }
    }
}
